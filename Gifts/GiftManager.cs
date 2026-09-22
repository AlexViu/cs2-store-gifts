using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2StoreGifts.Config;
using CS2StoreGifts.Models;
using Microsoft.Extensions.Logging;
using StoreApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CS2StoreGifts.Gifts;

public class GiftManager
{
    private readonly CS2StoreGiftsPlugin _plugin;
    private readonly GiftsConfig _config;
    private readonly IStoreApi _storeApi;
    private readonly string _mapsDirectory;

    private readonly List<GiftPoint> _gifts = [];
    private readonly Dictionary<int, CBaseModelEntity> _entities = [];

    // Momento a partir del cual cada regalo se puede recoger. Se usa DateTime en vez de
    // tiempo del motor para no depender de ninguna llamada nativa.
    private readonly Dictionary<int, DateTime> _collectableAt = [];

    // Modelos que SI conseguimos registrar en el resource manifest del mapa actual.
    // Asignar con SetModel() un modelo que no este en el manifiesto revienta una
    // asercion nativa del motor (skeletoninstance.cpp, SetupModel) y mata el proceso
    // entero del servidor. No es una excepcion de .NET: no se puede atrapar desde C#.
    private readonly HashSet<string> _manifestedModels = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _checkTimer;
    private int _nextId = 1;

    public GiftManager(CS2StoreGiftsPlugin plugin, GiftsConfig config, IStoreApi storeApi)
    {
        _plugin = plugin;
        _config = config;
        _storeApi = storeApi;
        _mapsDirectory = Path.Combine(plugin.ModuleDirectory, "maps");
        Directory.CreateDirectory(_mapsDirectory);
    }

    public IReadOnlyList<GiftPoint> Gifts => _gifts;

    // Solo es seguro llamar OnMapStart() (crea entidades) cuando realmente hay un
    // mapa/servidor activo. Llamarlo antes (ej. durante Load() del plugin, al arrancar
    // el proceso) hace crashear el motor de CS2 por completo ("FATAL ERROR:
    // PrecacheGeneric called with no server!"), no es un error de .NET que se pueda
    // atrapar. Este flag evita volver a llamarlo sin necesidad y le permite al plugin
    // saber si todavia falta cargar el mapa actual.
    public bool IsLoaded { get; private set; }

    private string CurrentMapFile => Path.Combine(_mapsDirectory, $"{Server.MapName}.json");

    /// <summary>
    /// Unico momento en que se pueden registrar modelos para el mapa que se esta cargando.
    /// Server.PrecacheModel() llamado mas tarde (con el mapa ya activo) NO sirve: el
    /// manifiesto ya esta construido y el modelo seguira "missing from manifest".
    /// Se registran los modelos de TODOS los mapas guardados, no solo el actual, porque
    /// en este punto no hay garantia de que Server.MapName ya apunte al mapa nuevo.
    /// </summary>
    public void OnServerPrecacheResources(ResourceManifest manifest)
    {
        _manifestedModels.Clear();

        HashSet<string> models = CollectAllModels();
        _plugin.Logger.LogInformation("[CS2StoreGifts] Manifiesto: {Count} modelo(s) candidatos a registrar.", models.Count);

        foreach (string model in models)
        {
            try
            {
                _plugin.Logger.LogInformation("[CS2StoreGifts] Manifiesto: registrando '{Model}'", model);
                manifest.AddResource(model);
                _manifestedModels.Add(model);
            }
            catch (Exception ex)
            {
                _plugin.Logger.LogError(ex, "[CS2StoreGifts] Manifiesto: fallo registrando '{Model}'", model);
            }
        }

        _plugin.Logger.LogInformation("[CS2StoreGifts] Manifiesto: {Count} modelo(s) registrados correctamente.", _manifestedModels.Count);
    }

    /// <summary>
    /// Modelos que el plugin tiene permitido usar: DefaultModel + AllowedModels de la
    /// config. Deliberadamente NO se incluyen los modelos que aparezcan en los JSON de
    /// los mapas: un regalo guardado con un modelo que luego resulta invalido tumbaria
    /// el servidor en cada carga de mapa, y no hay forma de validarlo desde C#.
    /// </summary>
    private HashSet<string> CollectAllModels()
    {
        HashSet<string> models = new(StringComparer.OrdinalIgnoreCase);

        if (IsModelAllowed(_config.DefaultModel))
            models.Add(_config.DefaultModel);

        foreach (string model in _config.AllowedModels)
        {
            if (IsModelAllowed(model))
                models.Add(model);
        }

        return models;
    }

    /// <summary>
    /// Si el modelo esta autorizado por configuracion. Es una lista blanca porque
    /// CounterStrikeSharp no expone ninguna forma de comprobar si un modelo existe o es
    /// valido (PrecacheModel y AddResource son void y no informan de nada), y asignar uno
    /// invalido mata el proceso del servidor entero.
    /// </summary>
    public bool IsModelAllowed(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        return model.Equals(_config.DefaultModel, StringComparison.OrdinalIgnoreCase)
            || _config.AllowedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Si el modelo esta autorizado Y registrado en el manifiesto de este mapa, es decir,
    /// si es seguro asignarlo a una entidad ahora mismo.
    /// </summary>
    public bool IsModelUsable(string? model)
    {
        string resolved = string.IsNullOrEmpty(model) ? _config.DefaultModel : model;
        return IsModelAllowed(resolved) && _manifestedModels.Contains(resolved);
    }

    /// <summary>
    /// true si no hay ningun modelo configurado: los regalos funcionan igual pero sin
    /// entidad visible. Es el modo que no puede crashear el servidor.
    /// </summary>
    public bool IsInvisibleMode => string.IsNullOrWhiteSpace(_config.DefaultModel);

    public void OnMapStart()
    {
        _entities.Clear();
        _collectableAt.Clear();
        _gifts.Clear();
        _nextId = 1;

        Load();

        _plugin.Logger.LogInformation(
            "[CS2StoreGifts] OnMapStart en '{Map}': {Count} regalo(s) leidos, {Models} modelo(s) disponibles en el manifiesto.",
            Server.MapName, _gifts.Count, _manifestedModels.Count);

        // No crear entidades aqui mismo: justo cuando dispara OnMapStart, el mundo del
        // mapa todavia puede no estar completamente listo para crear entidades del lado
        // nativo, y eso puede crashear el servidor entero (visto en produccion). Se da
        // un margen antes de spawnear.
        _plugin.AddTimer(1.5f, SpawnAllGifts);

        _checkTimer?.Kill();
        _checkTimer = _plugin.AddTimer(_config.CheckIntervalSeconds, CheckPlayers, TimerFlags.REPEAT);

        IsLoaded = true;
    }

    private void SpawnAllGifts()
    {
        _plugin.Logger.LogInformation("[CS2StoreGifts] SpawnAllGifts: creando {Count} regalo(s).", _gifts.Count);

        foreach (GiftPoint gift in _gifts)
        {
            MarkCollectableLater(gift);
            Spawn(gift);
        }

        _plugin.Logger.LogInformation("[CS2StoreGifts] SpawnAllGifts: terminado ({Count} entidades vivas).", _entities.Count);
    }

    public void OnMapEnd()
    {
        _checkTimer?.Kill();
        _checkTimer = null;

        foreach (CBaseModelEntity prop in _entities.Values)
            RemoveEntity(prop);

        _entities.Clear();
        IsLoaded = false;
    }

    public GiftPoint AddGift(int credits, Vector position, string? model)
    {
        GiftPoint gift = new()
        {
            Id = _nextId++,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            Credits = credits,
            Model = model,
        };

        _gifts.Add(gift);
        Save();

        _plugin.Logger.LogInformation(
            "[CS2StoreGifts] AddGift #{Id}: {Credits} creditos, modelo '{Model}', guardado en {File}.",
            gift.Id, credits, model ?? "(DefaultModel)", CurrentMapFile);

        // Si el modelo no esta en el manifiesto de este mapa, Spawn() lo omite en vez
        // de crashear el servidor. El regalo queda guardado y aparecera al cambiar de mapa.
        MarkCollectableLater(gift);
        Spawn(gift);

        return gift;
    }

    public bool RemoveNearest(Vector position, float maxDistance)
    {
        GiftPoint? nearest = null;
        float nearestDistSq = maxDistance * maxDistance;

        foreach (GiftPoint gift in _gifts)
        {
            float distSq = DistanceSquared(gift, position);

            if (distSq <= nearestDistSq)
            {
                nearestDistSq = distSq;
                nearest = gift;
            }
        }

        if (nearest == null)
            return false;

        _gifts.Remove(nearest);
        _collectableAt.Remove(nearest.Id);

        if (_entities.Remove(nearest.Id, out CBaseModelEntity? prop))
            RemoveEntity(prop);

        Save();
        return true;
    }

    private void Load()
    {
        List<GiftPoint> loaded = ReadGiftFile(CurrentMapFile);

        if (loaded.Count == 0)
            return;

        _gifts.AddRange(loaded);
        _nextId = _gifts.Max(g => g.Id) + 1;
    }

    private List<GiftPoint> ReadGiftFile(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<GiftPoint>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError(ex, "[CS2StoreGifts] No se pudo leer {File}", path);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_gifts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CurrentMapFile, json);
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError(ex, "[CS2StoreGifts] No se pudo guardar {File}", CurrentMapFile);
        }
    }

    private void Spawn(GiftPoint gift)
    {
        string model = string.IsNullOrEmpty(gift.Model) ? _config.DefaultModel : gift.Model;

        // Modo invisible: sin modelo configurado no se crea ninguna entidad. El regalo
        // se sigue pudiendo recoger por proximidad. Es el modo que no puede crashear.
        if (IsInvisibleMode && string.IsNullOrEmpty(gift.Model))
            return;

        // Barrera unica y obligatoria: solo se llega a tocar el motor si el modelo esta
        // en la lista blanca Y se registro en el manifiesto de este mapa. Asignar
        // cualquier otro modelo mata el proceso del servidor con una asercion nativa
        // (skeletoninstance.cpp / SetupModel) que no se puede atrapar desde C#, y no hay
        // ninguna API en CounterStrikeSharp para comprobar antes si un modelo es valido.
        if (!IsModelUsable(model))
        {
            _plugin.Logger.LogWarning(
                "[CS2StoreGifts] Regalo #{Id} omitido: el modelo '{Model}' no esta autorizado o no se registro en el manifiesto de este mapa. Anadelo a AllowedModels en la config si sabes que es valido.",
                gift.Id, model);
            return;
        }

        try
        {
            // Secuencia copiada de cs2-store (Item_PlayerSkin.Inspect), que hace justo
            // esto mismo para previsualizar modelos y funciona en produccion.
            CBaseModelEntity? prop = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (prop == null || !prop.IsValid)
            {
                _plugin.Logger.LogError("[CS2StoreGifts] Regalo #{Id}: CreateEntityByName devolvio una entidad nula o invalida", gift.Id);
                return;
            }

            prop.Spawnflags = 256u;

            // SOLID_VPHYSICS, igual que cs2-store. Antes se forzaba SOLID_NONE para que
            // los jugadores atravesaran el regalo, pero spawnear sin objeto de fisica
            // deja la entidad a medio inicializar y destruirla despues puede tumbar el
            // servidor. El regalo se recoge igual por proximidad al acercarse.
            prop.Collision.SolidType = SolidType_t.SOLID_VPHYSICS;

            prop.Teleport(new Vector(gift.X, gift.Y, gift.Z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
            prop.DispatchSpawn();

            // SetModel TIENE que ir en el frame siguiente. Tras DispatchSpawn la entidad
            // sigue en la "staging list" del motor durante el resto del frame actual, y
            // asignarle el modelo en ese estado dispara la asercion de
            // skeletoninstance.cpp (SetupModel):
            //   0 == (GetEntityIdentity()->GetFlags() & EF_IN_STAGING_LIST)
            // que mata el proceso del servidor entero. Hacerlo un frame despues es seguro.
            Server.NextFrame(() =>
            {
                if (!prop.IsValid)
                    return;

                prop.SetModel(model);
                ApplyScale(prop, gift.Id);
            });

            _entities[gift.Id] = prop;
        }
        catch (Exception ex)
        {
            // No todos los fallos aqui son excepciones de .NET atrapables (un modelo
            // realmente invalido puede crashear el proceso a nivel nativo), pero esto
            // cubre los casos que si se pueden atrapar sin tumbar el resto del plugin.
            _plugin.Logger.LogError(ex, "[CS2StoreGifts] Error creando la entidad del regalo #{Id} con modelo '{Model}'", gift.Id, model);
        }
    }

    /// <summary>
    /// Marca un regalo como no recogible durante los proximos PickupDelaySeconds.
    /// Ademas de evitar que el admin lo recoja nada mas colocarlo, impide que la entidad
    /// se cree y se destruya practicamente en el mismo frame.
    /// </summary>
    private void MarkCollectableLater(GiftPoint gift)
    {
        _collectableAt[gift.Id] = DateTime.UtcNow.AddSeconds(Math.Max(0, _config.PickupDelaySeconds));
    }

    private bool IsCollectable(GiftPoint gift)
    {
        return !_collectableAt.TryGetValue(gift.Id, out DateTime at) || DateTime.UtcNow >= at;
    }

    /// <summary>
    /// Posicion a cierta distancia por delante de donde mira el jugador. Mismo calculo
    /// que usa cs2-store para colocar el modelo de previsualizacion.
    /// </summary>
    public static Vector GetFrontPosition(Vector position, QAngle angles, float distance)
    {
        float radYaw = angles.Y * (MathF.PI / 180.0f);
        return position + new Vector(MathF.Cos(radYaw), MathF.Sin(radYaw), 0) * distance;
    }

    private void CheckPlayers()
    {
        if (_gifts.Count == 0)
            return;

        List<CCSPlayerController> players = Utilities.GetPlayers();
        float radiusSq = _config.PickupRadius * _config.PickupRadius;

        // Se itera sobre una copia: al recoger un regalo se elimina de _gifts, y modificar
        // la lista mientras se recorre la romperia.
        foreach (GiftPoint gift in _gifts.ToList())
        {
            if (!IsCollectable(gift))
                continue;

            foreach (CCSPlayerController player in players)
            {
                // El orden importa: hay que validar ANTES de desreferenciar. Leer un
                // campo (AbsOrigin, SteamID...) de un pawn o controller ya liberado por
                // el motor es un acceso a memoria invalida que mata el proceso, y no es
                // una excepcion de .NET que se pueda atrapar.
                if (!player.IsValid || player.IsBot || !player.PawnIsAlive)
                    continue;

                if (player.PlayerPawn is not { IsValid: true } pawnHandle)
                    continue;

                if (pawnHandle.Value is not { IsValid: true } pawn)
                    continue;

                Vector? origin = pawn.AbsOrigin;

                if (origin == null || DistanceSquared(gift, origin) > radiusSq)
                    continue;

                Collect(gift, player);
                break;
            }
        }
    }

    private void Collect(GiftPoint gift, CCSPlayerController player)
    {
        // Si el jugador ya no es valido no se recoge nada: el regalo sigue en la lista y
        // se reintentara en el siguiente tick del timer.
        if (!player.IsValid)
            return;

        string playerName = player.PlayerName;

        // El regalo se borra definitivamente, tambien del JSON del mapa: una vez recogido
        // no vuelve a aparecer aunque el mapa se reinicie o cambie. Se hace lo primero
        // para que un fallo posterior no lo deje recogiendose en bucle cada tick.
        _gifts.Remove(gift);
        _collectableAt.Remove(gift.Id);
        Save();

        _plugin.Logger.LogInformation(
            "[CS2StoreGifts] Collect #{Id}: {Player} recoge {Credits} creditos. Quedan {Left} regalo(s) en el mapa.",
            gift.Id, playerName, gift.Credits, _gifts.Count);

        // Logs por paso: el crash ocurre en algun punto de esta secuencia y la linea
        // anterior si llega al archivo, asi que la ultima que aparezca acota la llamada.
        try
        {
            _storeApi.GivePlayerCredits(player, gift.Credits);
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError(ex, "[CS2StoreGifts] Error dando {Credits} creditos a {Player}", gift.Credits, playerName);
        }

        _plugin.Logger.LogInformation("[CS2StoreGifts] Collect #{Id}: creditos entregados.", gift.Id);

        if (_entities.Remove(gift.Id, out CBaseModelEntity? prop))
            RemoveEntity(prop);

        _plugin.Logger.LogInformation("[CS2StoreGifts] Collect #{Id}: borrado de entidad encolado.", gift.Id);

        if (_config.AnnounceInChat)
        {
            Server.PrintToChatAll(
                $" {ChatColors.Green}{_config.ChatPrefix}{ChatColors.Default} {playerName} encontro un regalo y gano {ChatColors.Gold}{gift.Credits}{ChatColors.Default} creditos!");
        }

        _plugin.Logger.LogInformation("[CS2StoreGifts] Collect #{Id}: completado.", gift.Id);
    }

    /// <summary>
    /// Ajusta el tamano del modelo. Se aplica despues de SetModel, ya con la entidad
    /// spawneada y el modelo asignado, porque la escala vive en el nodo de escena del
    /// cuerpo y ese no existe hasta que hay modelo.
    /// </summary>
    private void ApplyScale(CBaseModelEntity prop, int giftId)
    {
        float scale = _config.ModelScale;

        if (scale <= 0f || Math.Abs(scale - 1.0f) < 0.001f)
            return;

        try
        {
            if (prop.CBodyComponent?.SceneNode is not { } sceneNode)
            {
                _plugin.Logger.LogWarning("[CS2StoreGifts] Regalo #{Id}: sin nodo de escena, no se puede escalar.", giftId);
                return;
            }

            sceneNode.Scale = scale;
            sceneNode.AbsScale = scale;
            Utilities.SetStateChanged(prop, "CBaseEntity", "m_CBodyComponent", 0);
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError(ex, "[CS2StoreGifts] Error escalando el regalo #{Id} a {Scale}", giftId, scale);
        }
    }

    /// <summary>
    /// Destruye la entidad de un regalo en el frame siguiente. Se difiere por el mismo
    /// motivo que SetModel: las operaciones de ciclo de vida de entidades hechas en
    /// mitad del frame (aqui, desde el callback de un timer) pueden dejar al motor en
    /// un estado inconsistente y tumbar el proceso.
    /// </summary>
    private void RemoveEntity(CBaseModelEntity? prop)
    {
        if (prop == null)
            return;

        Server.NextFrame(() =>
        {
            try
            {
                if (!prop.IsValid)
                    return;

                _plugin.Logger.LogInformation("[CS2StoreGifts] Destruyendo entidad de regalo...");
                prop.Remove();
                _plugin.Logger.LogInformation("[CS2StoreGifts] Entidad destruida.");
            }
            catch (Exception ex)
            {
                _plugin.Logger.LogError(ex, "[CS2StoreGifts] Error destruyendo la entidad de un regalo");
            }
        });
    }

    private static float DistanceSquared(GiftPoint gift, Vector position)
    {
        float dx = gift.X - position.X;
        float dy = gift.Y - position.Y;
        float dz = gift.Z - position.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
