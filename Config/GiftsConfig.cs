using CounterStrikeSharp.API.Core;

namespace CS2StoreGifts.Config;

public class GiftsConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;

    // Modelo usado cuando un regalo no define el suyo propio.
    // Cambialo por un modelo que exista/este precacheado en tu servidor (por ejemplo, un pumpkin de un addon de Halloween).
    public string DefaultModel { get; set; } = "models/props_survival/cash/cash_bag.vmdl";

    // Distancia (unidades de Source) para considerar que un jugador "toco" el regalo.
    public float PickupRadius { get; set; } = 60.0f;

    // Cada cuantos segundos se revisa la distancia de los jugadores a los regalos.
    public float CheckIntervalSeconds { get; set; } = 0.25f;

    // Sonido reproducido al jugador que recoge el regalo.
    public string PickupSound { get; set; } = "items/itempickup.vsnd";

    public bool AnnounceInChat { get; set; } = true;

    public string ChatPrefix { get; set; } = "[Regalo]";
}
