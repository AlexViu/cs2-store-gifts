using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CS2StoreGifts.Config;
using CS2StoreGifts.Gifts;
using CS2StoreGifts.Models;
using Microsoft.Extensions.Logging;
using StoreApi;

namespace CS2StoreGifts;

public class CS2StoreGiftsPlugin : BasePlugin, IPluginConfig<GiftsConfig>
{
    public override string ModuleName => "CS2StoreGifts";
    public override string ModuleVersion => "1.7.0";
    public override string ModuleAuthor => "Lonza";
    public override string ModuleDescription => "Regalos de creditos para cs2-store colocables en el mapa.";

    public GiftsConfig Config { get; set; } = new();

    private static readonly PluginCapability<IStoreApi?> StoreCapability = new("cs2-store:api");

    private IStoreApi? _storeApi;
    private GiftManager? _manager;

    public void OnConfigParsed(GiftsConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        // OnMapStart es el UNICO lugar seguro para precachear modelos/crear entidades:
        // ahi ya existe un mapa/servidor activo de verdad. Nunca llamar EnsureLoaded()
        // desde Load()/OnAllPluginsLoaded() en un arranque en frio: el servidor todavia
        // no tiene mapa cargado en ese momento y CS2 crashea con un error nativo
        // ("FATAL ERROR: PrecacheGeneric called with no server!") que ni siquiera es
        // una excepcion de .NET, no se puede atrapar con try/catch.
        // Unico momento valido para registrar modelos en el resource manifest del mapa.
        // Sin esto, SetModel() sobre el prop del regalo revienta una asercion nativa del
        // motor ("resource requested but is not in the system / missing from a manifest")
        // y mata el proceso del servidor entero.
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            if (EnsureManager())
                _manager!.OnServerPrecacheResources(manifest);
        });

        RegisterListener<Listeners.OnMapStart>(_ => EnsureLoaded());
        RegisterListener<Listeners.OnMapEnd>(() => _manager?.OnMapEnd());
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        // Solo resuelve la conexion con cs2-store (sin tocar mapas/entidades).
        EnsureManager();

        // hotReload=true significa que el plugin se cargo con el servidor YA corriendo
        // (ej. css_plugins load), es decir que ya hay un mapa activo de verdad: ahi si
        // es seguro cargar los regalos de una vez en lugar de esperar al proximo cambio de mapa.
        if (hotReload)
            EnsureLoaded();
    }

    public override void Unload(bool hotReload)
    {
        _manager?.OnMapEnd();
    }

    /// <summary>
    /// Resuelve la conexion con cs2-store (IStoreApi) si todavia no lo hemos logrado.
    /// No toca mapas ni entidades: es seguro llamarlo en cualquier momento, incluso
    /// antes de que haya un mapa cargado.
    /// </summary>
    private bool EnsureManager()
    {
        if (_manager != null)
            return true;

        try
        {
            _storeApi ??= StoreCapability.Get();

            if (_storeApi == null)
                return false;

            _manager = new GiftManager(this, Config, _storeApi);

            // Se vuelca la config efectiva para poder verificar de un vistazo que build
            // y que valores esta usando realmente el servidor.
            Logger.LogInformation(
                "[CS2StoreGifts] v{Version} conectado a cs2-store. DefaultModel='{Model}' ({Mode}), Escala={Scale}, AllowedModels={Allowed}, PickupRadius={Radius}, PlaceDistance={Place}, PickupDelay={Delay}s.",
                ModuleVersion,
                Config.DefaultModel,
                string.IsNullOrWhiteSpace(Config.DefaultModel) ? "modo invisible, no se crean entidades" : "con entidad visible",
                Config.ModelScale,
                Config.AllowedModels.Count,
                Config.PickupRadius,
                Config.PlaceDistance,
                Config.PickupDelaySeconds);

            return true;
        }
        catch (Exception ex)
        {
            // Nunca dejar que un fallo aqui (ej. cs2-store todavia no registro su API,
            // problema de orden de carga, permisos de disco) tumbe el plugin entero.
            // Se reintentara en la siguiente llamada (comando o cambio de mapa).
            Logger.LogWarning(ex, "[CS2StoreGifts] Todavia no se pudo conectar con cs2-store, se reintentara.");
            _storeApi = null;
            return false;
        }
    }

    /// <summary>
    /// Como EnsureManager(), pero ademas garantiza que los regalos del mapa actual esten
    /// cargados (precache + entidades). SOLO llamar desde un contexto donde ya sabemos
    /// que hay un mapa activo: el listener de OnMapStart, o un comando ejecutado por un
    /// jugador conectado (si hay un jugador conectado, hay un mapa cargado).
    /// </summary>
    private bool EnsureLoaded()
    {
        if (!EnsureManager())
            return false;

        if (!_manager!.IsLoaded)
            _manager.OnMapStart();

        return true;
    }

    [ConsoleCommand("css_gift_add", "Coloca un regalo de creditos en tu posicion actual. Uso: css_gift_add <creditos> [modelo]")]
    [CommandHelper(minArgs: 1, usage: "<creditos> [modelo]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnGiftAddCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid)
            return;

        if (!HasAccess(player))
        {
            command.ReplyToCommand("No tienes permiso para usar este comando (requiere @css/root).");
            return;
        }

        if (!EnsureLoaded())
        {
            command.ReplyToCommand("CS2StoreGifts no esta listo (cs2-store no cargado).");
            return;
        }

        if (!int.TryParse(command.GetArg(1), out int credits) || credits <= 0)
        {
            command.ReplyToCommand("Cantidad de creditos invalida.");
            return;
        }

        if (player.PlayerPawn is not { IsValid: true } pawnHandle ||
            pawnHandle.Value is not { IsValid: true } pawn ||
            pawn.AbsOrigin is not { } playerOrigin)
        {
            command.ReplyToCommand("No se pudo obtener tu posicion.");
            return;
        }

        // El regalo se coloca unos pasos por delante, no bajo tus pies: si no, lo
        // recogerias tu mismo en el instante en que lo creas.
        Vector origin = GiftManager.GetFrontPosition(playerOrigin, pawn.EyeAngles, Config.PlaceDistance);

        string? model = command.ArgCount > 2 ? command.GetArg(2) : null;

        // Se rechaza aqui, antes de guardar nada: no hay forma de validar un modelo desde
        // C#, y usar uno invalido mata el proceso del servidor entero. Solo se aceptan los
        // modelos que el admin haya declarado explicitamente como buenos en la config.
        if (model != null && !_manager!.IsModelAllowed(model))
        {
            command.ReplyToCommand($"Modelo no autorizado: '{model}'.");
            command.ReplyToCommand("Anadelo a AllowedModels en CS2StoreGifts.json y reinicia el mapa para registrarlo en el manifiesto.");
            return;
        }

        GiftPoint gift = _manager!.AddGift(credits, origin, model);

        if (_manager.IsInvisibleMode && model == null)
        {
            command.ReplyToCommand($"Regalo #{gift.Id} colocado con {credits} creditos (invisible: no hay DefaultModel configurado).");
            return;
        }

        command.ReplyToCommand($"Regalo #{gift.Id} colocado con {credits} creditos, {Config.PlaceDistance:F0} unidades por delante de ti.");
        command.ReplyToCommand($"No se puede recoger hasta dentro de {Config.PickupDelaySeconds:F0}s.");
    }

    [ConsoleCommand("css_gift_remove", "Elimina el regalo mas cercano a tu posicion.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnGiftRemoveCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid)
            return;

        if (!HasAccess(player))
        {
            command.ReplyToCommand("No tienes permiso para usar este comando (requiere @css/root).");
            return;
        }

        if (!EnsureLoaded())
        {
            command.ReplyToCommand("CS2StoreGifts no esta listo (cs2-store no cargado).");
            return;
        }

        Vector? origin = player.PlayerPawn.Value?.AbsOrigin;
        if (origin == null)
        {
            command.ReplyToCommand("No se pudo obtener tu posicion.");
            return;
        }

        bool removed = _manager!.RemoveNearest(origin, 150.0f);
        command.ReplyToCommand(removed ? "Regalo eliminado." : "No hay ningun regalo cerca (radio de 150 unidades).");
    }

    [ConsoleCommand("css_gift_list", "Lista los regalos del mapa actual.")]
    public void OnGiftListCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !HasAccess(player))
        {
            command.ReplyToCommand("No tienes permiso para usar este comando (requiere @css/root).");
            return;
        }

        if (!EnsureLoaded())
        {
            command.ReplyToCommand("CS2StoreGifts no esta listo (cs2-store no cargado).");
            return;
        }

        IReadOnlyList<GiftPoint> gifts = _manager!.Gifts;
        command.ReplyToCommand($"Regalos en este mapa: {gifts.Count}");

        foreach (GiftPoint gift in gifts)
        {
            command.ReplyToCommand($"#{gift.Id} - {gift.Credits} creditos - ({gift.X:F0}, {gift.Y:F0}, {gift.Z:F0})");
        }
    }

    [ConsoleCommand("css_gift_reload", "Recarga los regalos del mapa actual desde el archivo de configuracion.")]
    public void OnGiftReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !HasAccess(player))
        {
            command.ReplyToCommand("No tienes permiso para usar este comando (requiere @css/root).");
            return;
        }

        if (!EnsureLoaded())
        {
            command.ReplyToCommand("CS2StoreGifts no esta listo (cs2-store no cargado).");
            return;
        }

        _manager!.OnMapEnd();
        _manager.OnMapStart();
        command.ReplyToCommand("Regalos recargados.");
    }

    private static bool HasAccess(CCSPlayerController player)
    {
        return AdminManager.PlayerHasPermissions(player, ["@css/root"]);
    }
}
