# CS2StoreGifts

Plugin de [CounterStrikeSharp](https://docs.cssharp.dev/) para Counter-Strike 2 que permite colocar **regalos de créditos** en cualquier punto del mapa. Cuando un jugador camina sobre uno, recibe créditos automáticamente en su cuenta de [cs2-store](https://github.com/schwarper/cs2-store).

No modifica ni reemplaza cs2-store: es un plugin independiente que se conecta a su API (`IStoreApi`), así que puedes actualizar cs2-store por separado sin romper nada.

## Requisitos

- Un servidor de CS2 con [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) instalado.
- [cs2-store](https://github.com/schwarper/cs2-store) instalado y cargando **antes** que este plugin.
- El archivo `StoreApi.dll` de cs2-store presente en:
  ```
  game/csgo/addons/counterstrikesharp/shared/StoreApi/StoreApi.dll
  ```
  cs2-store lo deja ahí solo si instalaste su build/release oficial (no si solo copiaste el `.dll` de `Store`). Sin este archivo, CS2StoreGifts nunca podrá conectarse a la API de cs2-store, aunque cs2-store este cargado y funcionando.

## Cómo agregarlo al servidor

1. Compila el proyecto (`dotnet build -c Release`) o descarga el `.dll` ya compilado.
2. Copia `CS2StoreGifts.dll` (no `StoreApi.dll`, ese ya lo trae cs2-store) a:
   ```
   game/csgo/addons/counterstrikesharp/plugins/CS2StoreGifts/CS2StoreGifts.dll
   ```
3. Reinicia el servidor (o recarga los plugins). En el arranque se genera solo el archivo de configuración en:
   ```
   game/csgo/addons/counterstrikesharp/configs/plugins/CS2StoreGifts/CS2StoreGifts.json
   ```
4. Ajusta el `.json` (radio de recogida, distancia de colocación, mensaje de chat…). Por defecto **los regalos son invisibles**: se recogen igual, pero no se crea ninguna entidad. Para que se vean, lee la sección siguiente.

## Modelos: cómo activarlos sin tumbar el servidor

Esta es la parte delicada del plugin y conviene entenderla:

- CS2 exige que un modelo esté registrado en el *resource manifest* del mapa antes de asignarlo a una entidad. Ese registro solo puede hacerse mientras el mapa carga (el plugin lo hace en `OnServerPrecacheResources`).
- Si se asigna un modelo que no existe, no está registrado, o no es un prop válido, el motor **mata el proceso del servidor entero** con una aserción nativa de C++. No es una excepción de .NET: no se puede capturar ni evitar desde el plugin una vez hecha la llamada.
- CounterStrikeSharp **no ofrece ninguna forma de comprobar si un modelo es válido** (`PrecacheModel` y `AddResource` son `void` y no informan de nada).

Por eso el plugin usa una lista blanca en vez de aceptar cualquier ruta:

```json
{
  "DefaultModel": "",
  "AllowedModels": []
}
```

- Con `DefaultModel` vacío los regalos funcionan pero son invisibles. Es el modo seguro por defecto.
- Para usar un modelo, ponlo en `DefaultModel` y/o en `AllowedModels`, y **reinicia el mapa** para que se registre en el manifiesto.
- `css_gift_add <creditos> <modelo>` solo acepta modelos de esa lista. Cualquier otra ruta se rechaza con un mensaje, sin llegar a tocar el motor.

Sirve cualquier modelo que exista y esté montado en el mapa, incluidos los de agente (`agents/...`) que uses como skins en cs2-store. Verifica cada modelo nuevo en un servidor de pruebas, nunca en producción.

### Por qué el orden de creación importa

La entidad del regalo se crea con la misma secuencia que usa cs2-store para previsualizar skins, y **el orden no es negociable**:

```csharp
prop.Spawnflags = 256u;
prop.Collision.SolidType = SolidType_t.SOLID_NONE;  // antes de spawnear
prop.Teleport(...);
prop.DispatchSpawn();

Server.NextFrame(() =>                               // SetModel va un frame despues
{
    if (prop.IsValid)
        prop.SetModel(model);
});
```

Tras `DispatchSpawn()` la entidad sigue en la *staging list* del motor durante el resto del frame. Llamar a `SetModel()` en ese mismo frame dispara la aserción `0 == (flags & EF_IN_STAGING_LIST)` en `skeletoninstance.cpp` y **mata el proceso del servidor entero** — no es una excepción de .NET y no se puede capturar.

### Darte permiso para usar los comandos

Los comandos de administración requieren el permiso `@css/root` de CounterStrikeSharp. Si no lo tienes, el comando simplemente no responde (no da ningún mensaje de error).

Edita `game/csgo/addons/counterstrikesharp/configs/admins.json` y agrégate con tu SteamID64, por ejemplo:

```json
{
  "TuNombre": {
    "identity": "STEAM_1:0:XXXXXXXX",
    "flags": ["@css/root"]
  }
}
```

Guarda el archivo y vuelve a conectarte al servidor (o usa `css_admin_reload` si tu instalación de CounterStrikeSharp lo soporta) para que tome el cambio.

## Cómo funciona

- Al iniciar cada mapa, el plugin lee un archivo JSON propio de ese mapa (`configs/plugins/CS2StoreGifts/maps/<nombre_del_mapa>.json`) y crea una entidad visual en cada posición guardada, sin colisión con los jugadores.
- Cada cierto tiempo revisa la distancia entre cada jugador vivo y cada regalo no recogido. Si un jugador entra en el radio configurado, recibe los créditos a través de `IStoreApi.GivePlayerCredits()`, se anuncia en el chat (si está activado) y el regalo desaparece.
- No hay sonido de recogida a propósito: la implementación anterior (`ExecuteClientCommandFromServer("play <ruta>")`) tumbaba el servidor. La API correcta es `CBaseEntity.EmitSound`, que espera un nombre de *soundevent* y no una ruta de archivo.
- Cada regalo se puede recoger **una sola vez por mapa**: vuelve a estar disponible cuando el mapa se reinicia o cambia.
- Los regalos se crean y gestionan en vivo, dentro del juego, con comandos de administrador (requieren el permiso `@css/root`):

  | Comando | Qué hace |
  |---|---|
  | `css_gift_add <creditos> [modelo]` | Coloca un regalo unos pasos por delante de ti (`PlaceDistance`), con los créditos indicados. El modelo es opcional (si lo omites, usa `DefaultModel`). No se puede recoger durante los primeros `PickupDelaySeconds`, para que no lo cojas tú al colocarlo. |
  | `css_gift_remove` | Elimina el regalo más cercano a tu posición (radio de 150 unidades). |
  | `css_gift_list` | Lista los regalos del mapa actual (ID, créditos y posición). |
  | `css_gift_reload` | Recarga los regalos del mapa desde el archivo JSON. |

  Ejemplo: párate donde quieras el regalo y ejecuta `css_gift_add 100` (o `css_gift_add 500 models/props_halloween/pumpkin_01.vmdl` para usar un modelo específico). Queda guardado automáticamente para la próxima vez que se cargue ese mapa.
