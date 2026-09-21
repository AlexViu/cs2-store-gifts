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
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "halloweencs2";
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
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            bool existedBefore = _manager != null;

            if (!EnsureManager())
                return;

            // Si EnsureManager acaba de crear el manager, ya cargo el mapa actual
            // (ver EnsureManager). Solo hace falta recargar en cambios de mapa posteriores.
            if (existedBefore)
                _manager!.OnMapStart();
        });
        RegisterListener<Listeners.OnMapEnd>(() => _manager?.OnMapEnd());

        EnsureManager();
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        EnsureManager();
    }

    public override void Unload(bool hotReload)
    {
        _manager?.OnMapEnd();
    }

    /// <summary>
    /// Intenta conectar con cs2-store si todavia no lo hemos logrado. Se puede llamar
    /// varias veces: cs2-store puede cargar despues que este plugin (orden de carga,
    /// hot-reload, etc.), asi que no basta con intentarlo una sola vez en Load/OnAllPluginsLoaded.
    /// </summary>
    private bool EnsureManager()
    {
        if (_manager != null)
            return true;

        _storeApi ??= StoreCapability.Get();

        if (_storeApi == null)
            return false;

        _manager = new GiftManager(this, Config, _storeApi);
        _manager.OnMapStart();
        Logger.LogInformation("[CS2StoreGifts] Conectado a cs2-store correctamente.");
        return true;
    }

    [ConsoleCommand("css_gift_add", "Coloca un regalo de creditos en tu posicion actual. Uso: css_gift_add <creditos> [modelo]")]
    [CommandHelper(minArgs: 1, usage: "<creditos> [modelo]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnGiftAddCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || !HasAccess(player))
            return;

        if (!EnsureManager())
        {
            command.ReplyToCommand("CS2StoreGifts no esta listo (cs2-store no cargado).");
            return;
        }

        if (!int.TryParse(command.GetArg(1), out int credits) || credits <= 0)
        {
            command.ReplyToCommand("Cantidad de creditos invalida.");
            return;
        }

        Vector? origin = player.PlayerPawn.Value?.AbsOrigin;
        if (origin == null)
        {
            command.ReplyToCommand("No se pudo obtener tu posicion.");
            return;
        }

        string? model = command.ArgCount > 2 ? command.GetArg(2) : null;
        GiftPoint gift = _manager!.AddGift(credits, origin, model);

        command.ReplyToCommand($"Regalo #{gift.Id} colocado con {credits} creditos en tu posicion actual.");
    }

    [ConsoleCommand("css_gift_remove", "Elimina el regalo mas cercano a tu posicion.")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnGiftRemoveCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || !HasAccess(player))
            return;

        if (!EnsureManager())
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
            return;

        if (!EnsureManager())
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
            return;

        if (!EnsureManager())
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
