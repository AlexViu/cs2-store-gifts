using CounterStrikeSharp.API.Core;

namespace CS2StoreGifts.Config;

public class GiftsConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;

    // Modelo usado cuando un regalo no define el suyo propio.
    //
    // VACIO POR DEFECTO A PROPOSITO: asignar un modelo que no existe o que no es valido
    // para un prop mata el proceso del servidor entero con una asercion nativa del motor,
    // y CounterStrikeSharp no ofrece ninguna forma de comprobar si un modelo es valido
    // antes de usarlo. Con el valor vacio los regalos funcionan igual (se recogen por
    // proximidad) pero no se crea ninguna entidad visible: es el modo que no puede
    // crashear. Pon aqui un modelo solo cuando hayas verificado que funciona.
    public string DefaultModel { get; set; } = "";

    // Unicos modelos que se aceptan en css_gift_add (ademas de DefaultModel).
    // Cualquier otra ruta se rechaza sin llegar a tocar el motor. Estos modelos se
    // registran en el resource manifest de cada mapa, asi que se pueden usar de
    // inmediato sin esperar a un cambio de mapa.
    public List<string> AllowedModels { get; set; } = [];

    // Distancia (unidades de Source) para considerar que un jugador "toco" el regalo.
    public float PickupRadius { get; set; } = 60.0f;

    // A que distancia por delante del admin se coloca el regalo con css_gift_add.
    // Si se creara justo bajo sus pies, el propio admin lo recogeria al instante.
    public float PlaceDistance { get; set; } = 100.0f;

    // Margen antes de que un regalo recien creado se pueda recoger. Evita el ciclo de
    // crear y destruir la entidad en el mismo instante, que es agresivo para el motor.
    public float PickupDelaySeconds { get; set; } = 3.0f;

    // Cada cuantos segundos se revisa la distancia de los jugadores a los regalos.
    public float CheckIntervalSeconds { get; set; } = 0.25f;

    // Sonido reproducido al jugador que recoge el regalo.
    // Vacio por defecto: reproducirlo implica una llamada nativa mas en el momento de
    // la recogida, y no aporta nada imprescindible. Si lo rellenas, tiene que ser un
    // sonido precacheado en el servidor (se comprueba con IsSoundPrecached antes de usarlo).
    public string PickupSound { get; set; } = "";

    public bool AnnounceInChat { get; set; } = true;

    public string ChatPrefix { get; set; } = "[Regalo]";
}
