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
    private readonly Dictionary<int, CDynamicProp> _entities = [];
    private readonly HashSet<int> _collected = [];

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

        foreach (string model in CollectAllModels())
        {
            try
            {
                manifest.AddResource(model);
                _manifestedModels.Add(model);
            }
            catch (Exception ex)
            {
                _plugin.Logger.LogError(ex, "[CS2StoreGifts] No se pudo registrar el modelo '{Model}' en el manifiesto", model);
            }
        }

        _plugin.Logger.LogInformation("[CS2StoreGifts] {Count} modelo(s) registrados en el manifiesto del mapa.", _manifestedModels.Count);
    }

    /// <summary>
    /// Todos los modelos que este plugin podria llegar a usar: el DefaultModel de la
    /// config y los modelos propios de los regalos de todos los mapas guardados.
    /// </summary>
    private HashSet<string> CollectAllModels()
    {
        HashSet<string> models = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_config.DefaultModel))
            models.Add(_config.DefaultModel);

        foreach (string file in Directory.EnumerateFiles(_mapsDirectory, "*.json"))
        {
            foreach (GiftPoint gift in ReadGiftFile(file))
            {
                if (!string.IsNullOrEmpty(gift.Model))
                    models.Add(gift.Model);
            }
        }

        return models;
    }

    /// <summary>
    /// Indica si un modelo se pudo registrar en el manifiesto de este mapa, es decir,
    /// si es seguro asignarlo a una entidad. Un modelo escrito a mano en css_gift_add
    /// con el mapa ya cargado NO lo estara: se guardara igual, pero no se puede spawnear
    /// hasta el siguiente cambio de mapa.
    /// </summary>
    public bool IsModelAvailable(string? model)
    {
        string resolved = string.IsNullOrEmpty(model) ? _config.DefaultModel : model;
        return !string.IsNullOrEmpty(resolved) && _manifestedModels.Contains(resolved);
    }

    public void OnMapStart()
    {
        _entities.Clear();
        _collected.Clear();
        _gifts.Clear();
        _nextId = 1;

        Load();

        // No crear entidades aqui mismo: justo cuando dispara OnMapStart, el mundo del
        // mapa todavia puede no estar completamente listo para crear entidades del lado
        // nativo, y eso puede crashear el servidor entero (visto en produccion). Se da
        // un margen antes de precachear/spawnear.
        _plugin.AddTimer(1.5f, SpawnAllGifts);

        _checkTimer?.Kill();
        _checkTimer = _plugin.AddTimer(_config.CheckIntervalSeconds, CheckPlayers, TimerFlags.REPEAT);

        IsLoaded = true;
    }

    private void SpawnAllGifts()
    {
        foreach (GiftPoint gift in _gifts)
            Spawn(gift);
    }

    public void OnMapEnd()
    {
        _checkTimer?.Kill();
        _checkTimer = null;

        foreach (CDynamicProp prop in _entities.Values)
        {
            if (prop.IsValid)
                prop.Remove();
        }

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

        // Si el modelo no esta en el manifiesto de este mapa, Spawn() lo omite en vez
        // de crashear el servidor. El regalo queda guardado y aparecera al cambiar de mapa.
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
        _collected.Remove(nearest.Id);

        if (_entities.Remove(nearest.Id, out CDynamicProp? prop) && prop.IsValid)
            prop.Remove();

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

        if (string.IsNullOrEmpty(model))
        {
            _plugin.Logger.LogError("[CS2StoreGifts] El regalo #{Id} no tiene modelo (ni propio ni DefaultModel configurado); se omite.", gift.Id);
            return;
        }

        // Proteccion critica: asignar un modelo que no este en el resource manifest del
        // mapa mata el proceso del servidor con una asercion nativa que no se puede
        // atrapar desde C#. Mejor no crear la entidad.
        if (!_manifestedModels.Contains(model))
        {
            _plugin.Logger.LogError(
                "[CS2StoreGifts] El modelo '{Model}' del regalo #{Id} no esta registrado en el manifiesto de este mapa; no se crea la entidad (spawnearlo crashearia el servidor). Aparecera tras un cambio de mapa si el modelo existe.",
                model, gift.Id);
            return;
        }

        try
        {
            CDynamicProp? prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
            if (prop == null || !prop.IsValid)
            {
                _plugin.Logger.LogError("[CS2StoreGifts] No se pudo crear la entidad para el regalo #{Id}", gift.Id);
                return;
            }

            prop.SetModel(model);
            prop.Teleport(new Vector(gift.X, gift.Y, gift.Z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
            prop.DispatchSpawn();

            // Evita que el prop bloquee el movimiento de los jugadores.
            prop.Collision.SolidType = SolidType_t.SOLID_NONE;
            Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_Collision", 0);

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

    private void CheckPlayers()
    {
        if (_gifts.Count == 0)
            return;

        List<CCSPlayerController> players = Utilities.GetPlayers();
        float radiusSq = _config.PickupRadius * _config.PickupRadius;

        foreach (GiftPoint gift in _gifts)
        {
            if (_collected.Contains(gift.Id))
                continue;

            foreach (CCSPlayerController player in players)
            {
                if (!player.IsValid || player.IsBot || !player.PawnIsAlive)
                    continue;

                CCSPlayerPawn? pawn = player.PlayerPawn.Value;
                Vector? origin = pawn?.AbsOrigin;

                if (pawn == null || !pawn.IsValid || origin == null)
                    continue;

                if (DistanceSquared(gift, origin) > radiusSq)
                    continue;

                Collect(gift, player);
                break;
            }
        }
    }

    private void Collect(GiftPoint gift, CCSPlayerController player)
    {
        _collected.Add(gift.Id);

        _storeApi.GivePlayerCredits(player, gift.Credits);

        if (_entities.Remove(gift.Id, out CDynamicProp? prop) && prop.IsValid)
            prop.Remove();

        if (!string.IsNullOrEmpty(_config.PickupSound))
            player.ExecuteClientCommandFromServer($"play {_config.PickupSound}");

        if (_config.AnnounceInChat)
        {
            Server.PrintToChatAll(
                $" {ChatColors.Green}{_config.ChatPrefix}{ChatColors.Default} {player.PlayerName} encontro un regalo y gano {ChatColors.Gold}{gift.Credits}{ChatColors.Default} creditos!");
        }
    }

    private static float DistanceSquared(GiftPoint gift, Vector position)
    {
        float dx = gift.X - position.X;
        float dy = gift.Y - position.Y;
        float dz = gift.Z - position.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
