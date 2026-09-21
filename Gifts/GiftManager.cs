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

    // Solo es seguro llamar OnMapStart() (precachea modelos y crea entidades) cuando
    // realmente hay un mapa/servidor activo. Llamarlo antes (ej. durante Load() del
    // plugin, al arrancar el proceso) hace crashear el motor de CS2 por completo
    // ("FATAL ERROR: PrecacheGeneric called with no server!"), no es un error de .NET
    // que se pueda atrapar. Este flag evita volver a llamarlo sin necesidad y le permite
    // al plugin saber si todavia falta cargar el mapa actual.
    public bool IsLoaded { get; private set; }

    private string CurrentMapFile => Path.Combine(_mapsDirectory, $"{Server.MapName}.json");

    public void OnMapStart()
    {
        _entities.Clear();
        _collected.Clear();
        _gifts.Clear();
        _nextId = 1;

        Load();

        if (!string.IsNullOrEmpty(_config.DefaultModel))
            Server.PrecacheModel(_config.DefaultModel);

        foreach (GiftPoint gift in _gifts)
        {
            if (!string.IsNullOrEmpty(gift.Model))
                Server.PrecacheModel(gift.Model);
        }

        foreach (GiftPoint gift in _gifts)
            Spawn(gift);

        _checkTimer?.Kill();
        _checkTimer = _plugin.AddTimer(_config.CheckIntervalSeconds, CheckPlayers, TimerFlags.REPEAT);

        IsLoaded = true;
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

        if (!string.IsNullOrEmpty(model))
            Server.PrecacheModel(model);

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
        if (!File.Exists(CurrentMapFile))
            return;

        try
        {
            string json = File.ReadAllText(CurrentMapFile);
            List<GiftPoint>? loaded = JsonSerializer.Deserialize<List<GiftPoint>>(json);

            if (loaded == null || loaded.Count == 0)
                return;

            _gifts.AddRange(loaded);
            _nextId = _gifts.Max(g => g.Id) + 1;
        }
        catch (Exception ex)
        {
            _plugin.Logger.LogError(ex, "[CS2StoreGifts] No se pudo leer {File}", CurrentMapFile);
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

        CDynamicProp? prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
        if (prop == null || !prop.IsValid)
        {
            _plugin.Logger.LogError("[CS2StoreGifts] No se pudo crear la entidad para el regalo #{Id}", gift.Id);
            return;
        }

        prop.Teleport(new Vector(gift.X, gift.Y, gift.Z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
        prop.DispatchSpawn();
        prop.SetModel(model);

        // Evita que el prop bloquee el movimiento de los jugadores.
        prop.Collision.SolidType = SolidType_t.SOLID_NONE;
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_Collision", 0);

        _entities[gift.Id] = prop;
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
