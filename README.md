# CS2StoreGifts

Plugin de [CounterStrikeSharp](https://docs.cssharp.dev/) para Counter-Strike 2 que permite colocar **regalos de créditos** en cualquier punto del mapa. Cuando un jugador camina sobre uno, recibe créditos automáticamente en su cuenta de [cs2-store](https://github.com/schwarper/cs2-store).

No modifica ni reemplaza cs2-store: es un plugin independiente que se conecta a su API (`IStoreApi`), así que puedes actualizar cs2-store por separado sin romper nada.

## Requisitos

- Un servidor de CS2 con [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) instalado.
- [cs2-store](https://github.com/schwarper/cs2-store) instalado y cargando **antes** que este plugin.
- [.NET SDK 10](https://dotnet.microsoft.com/download) si vas a compilar el plugin tú mismo.

## Instalación

### Opción A: compilar desde el código fuente

```powershell
git clone https://github.com/AlexViu/cs2-store-gifts.git
cd cs2-store-gifts
dotnet build -c Release
```

El `.dll` compilado queda en `bin/Release/net10.0/CS2StoreGifts.dll`.

### Opción B: usar un build ya compilado

Descarga el `.dll` y sáltate el paso de compilación.

### Copiar al servidor

Copia el `.dll` a esta ruta de tu servidor:

```
game/csgo/addons/counterstrikesharp/plugins/CS2StoreGifts/CS2StoreGifts.dll
```

Reinicia el servidor (o recarga los plugins). En el arranque se generará automáticamente el archivo de configuración en:

```
game/csgo/addons/counterstrikesharp/configs/plugins/CS2StoreGifts/CS2StoreGifts.json
```

## Configuración

`CS2StoreGifts.json`:

| Campo | Descripción | Valor por defecto |
|---|---|---|
| `DefaultModel` | Modelo usado por los regalos que no definen uno propio al crearse. Debe ser un modelo válido de tu servidor. | `models/props_survival/cash/cash_bag.vmdl` |
| `PickupRadius` | Distancia (unidades de Source) a la que un jugador debe acercarse para recoger el regalo. | `60.0` |
| `CheckIntervalSeconds` | Cada cuántos segundos se revisa la posición de los jugadores respecto a los regalos. | `0.25` |
| `PickupSound` | Sonido que se reproduce al recoger un regalo. | `items/itempickup.vsnd` |
| `AnnounceInChat` | Si se anuncia en el chat global cuando alguien recoge un regalo. | `true` |
| `ChatPrefix` | Prefijo del mensaje de chat. | `[Regalo]` |

> Cambia `DefaultModel` por un modelo que exista/esté precacheado en tu servidor (por ejemplo, una calabaza de un addon de Halloween) antes de usar el plugin en producción.

## Uso en el juego

Los regalos se guardan **por mapa**, en `configs/plugins/CS2StoreGifts/maps/<nombre_del_mapa>.json`. Se crean y gestionan con comandos de administrador (requieren el permiso `@css/root`).

### Colocar un regalo

Párate en el punto exacto del mapa donde quieres el regalo y ejecuta:

```
css_gift_add <creditos> [modelo]
```

- `<creditos>` (obligatorio): cuántos créditos de cs2-store gana el jugador que lo recoja.
- `[modelo]` (opcional): ruta de un modelo específico para ese regalo. Si lo omites, usa `DefaultModel`.

Ejemplos:

```
css_gift_add 100
css_gift_add 500 models/props_halloween/pumpkin_01.vmdl
```

El regalo aparece de inmediato en el mundo y queda guardado para la próxima vez que se cargue ese mapa.

### Quitar un regalo

Párate cerca del regalo que quieres eliminar (radio de 150 unidades) y ejecuta:

```
css_gift_remove
```

Elimina el regalo más cercano a tu posición.

### Listar los regalos del mapa actual

```
css_gift_list
```

Muestra el ID, los créditos y la posición de cada regalo colocado en el mapa.

### Recargar los regalos

Si editaste el archivo JSON del mapa a mano, o quieres reiniciar los regalos recogidos sin cambiar de mapa:

```
css_gift_reload
```

## Cómo funciona

- Al iniciar cada mapa, el plugin lee el archivo JSON de ese mapa y crea una entidad visual (`prop_dynamic_override`) en cada posición guardada, sin colisión con los jugadores.
- Cada cierto tiempo (`CheckIntervalSeconds`) revisa la distancia entre cada jugador vivo y cada regalo no recogido. Si un jugador entra en el radio (`PickupRadius`), recibe los créditos vía `IStoreApi.GivePlayerCredits()`, se reproduce un sonido, se anuncia en el chat (si está activado) y el regalo desaparece.
- Cada regalo se puede recoger **una sola vez por mapa**: vuelve a estar disponible cuando el mapa se reinicia o cambia.

## Créditos

- [cs2-store](https://github.com/schwarper/cs2-store) por schwarper — el sistema de créditos/tienda sobre el que se apoya este plugin.
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) — el framework de plugins para CS2.
