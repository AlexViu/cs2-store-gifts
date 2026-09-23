# CS2StoreGifts

A [CounterStrikeSharp](https://docs.cssharp.dev/) plugin for Counter-Strike 2 that lets you place **credit gifts** anywhere on the map. When a player walks up to one, they automatically receive credits in their [cs2-store](https://github.com/schwarper/cs2-store) account.

It does not modify or replace cs2-store: it is a standalone plugin that talks to its API (`IStoreApi`), so you can update cs2-store independently without breaking anything.

## Requirements

- A CS2 server with [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed.
- [cs2-store](https://github.com/schwarper/cs2-store) installed and loading **before** this plugin.
- cs2-store's `StoreApi.dll` present at:
  ```
  game/csgo/addons/counterstrikesharp/shared/StoreApi/StoreApi.dll
  ```
  cs2-store only puts it there if you installed its official build/release (not if you just copied the `Store` DLL). Without this file, CS2StoreGifts can never connect to the cs2-store API, even if cs2-store itself is loaded and working fine.

## Installation

1. Download `CS2StoreGifts.dll` from the [latest release](https://github.com/AlexViu/cs2-store-gifts/releases), or build it yourself with `dotnet build -c Release`.
2. Copy `CS2StoreGifts.dll` (not `StoreApi.dll` — that one ships with cs2-store) to:
   ```
   game/csgo/addons/counterstrikesharp/plugins/CS2StoreGifts/CS2StoreGifts.dll
   ```
3. Restart the server. On startup it generates its config file at:
   ```
   game/csgo/addons/counterstrikesharp/configs/plugins/CS2StoreGifts/CS2StoreGifts.json
   ```
4. Adjust the config. By default **gifts are invisible**: they still work and can be collected, but no entity is created. To make them visible, read the models section below.

## Configuration

```json
{
  "ConfigVersion": 1,
  "DefaultModel": "",
  "AllowedModels": [],
  "PickupRadius": 60.0,
  "ModelScale": 1.0,
  "PlaceDistance": 100.0,
  "PickupDelaySeconds": 3.0,
  "CheckIntervalSeconds": 0.25,
  "AnnounceInChat": true,
  "ChatPrefix": "[Regalo]"
}
```

| Option | What it does | Default |
|---|---|---|
| `DefaultModel` | Model used by gifts that don't specify their own. Empty means invisible gifts. | `""` |
| `AllowedModels` | Whitelist of models accepted by `css_gift_add`. See below. | `[]` |
| `PickupRadius` | How close a player must get to collect a gift, in Source units. | `60.0` |
| `ModelScale` | Prop size. `1.0` is the original size, `0.5` is half. Many game props are too large for a gift lying on the floor. | `1.0` |
| `PlaceDistance` | How far in front of the admin `css_gift_add` places the gift. | `100.0` |
| `PickupDelaySeconds` | Grace period before a newly created gift can be collected, so you don't collect it yourself while placing it. | `3.0` |
| `CheckIntervalSeconds` | How often player proximity is checked. | `0.25` |
| `AnnounceInChat` | Announce pickups in global chat. | `true` |
| `ChatPrefix` | Prefix for the chat announcement. | `[Regalo]` |

Missing fields fall back to their defaults, so a partial config file is fine.

## Commands

All commands require the `@css/root` permission.

| Command | What it does |
|---|---|
| `css_gift_add <credits> [model]` | Places a gift `PlaceDistance` units in front of you, worth the given credits. The model is optional (falls back to `DefaultModel`). It can't be collected for the first `PickupDelaySeconds`. |
| `css_gift_remove` | Removes the nearest gift to your position (within 150 units). |
| `css_gift_list` | Lists the gifts on the current map (ID, credits and position). |
| `css_gift_reload` | Reloads the current map's gifts from its JSON file. |

Example: stand where you want it and run `css_gift_add 100`, or `css_gift_add 500 models/props/crates/cs2_drop_crate_01.vmdl` to use a specific model.

### Granting yourself permission

If you lack `@css/root`, the commands reply telling you so. Edit `game/csgo/addons/counterstrikesharp/configs/admins.json` and add yourself:

```json
{
  "YourName": {
    "identity": "STEAM_1:0:XXXXXXXX",
    "flags": ["@css/root"]
  }
}
```

Save it and reconnect to the server so the change is picked up.

## Models: enabling them without crashing the server

This is the delicate part of the plugin and it's worth understanding:

- CS2 requires a model to be registered in the map's *resource manifest* before it can be assigned to an entity. That registration can only happen while the map is loading (the plugin does it in `OnServerPrecacheResources`).
- If you assign a model that doesn't exist, isn't registered, or isn't a valid prop, the engine **kills the entire server process** with a native C++ assertion. It is not a .NET exception: it cannot be caught or prevented from the plugin once the call is made.
- CounterStrikeSharp offers **no way to check whether a model is valid** (`PrecacheModel` and `AddResource` both return `void` and report nothing).

That's why the plugin uses a whitelist instead of accepting arbitrary paths:

- With `DefaultModel` empty, gifts work but are invisible. This is the safe default.
- To use a model, put it in `DefaultModel` and/or `AllowedModels`, then **restart the map** so it gets registered in the manifest.
- `css_gift_add <credits> <model>` only accepts models from that list. Any other path is rejected with a message, without ever touching the engine.

Any model that exists and is mounted on the map works, including agent models (`agents/...`) used as cs2-store skins. Verify every new model on a test server, never in production.

### Why the creation order matters

The gift entity is created with the same sequence cs2-store uses to preview skins, and **the order is not negotiable**:

```csharp
prop.Spawnflags = 256u;
prop.Collision.SolidType = SolidType_t.SOLID_VPHYSICS;  // before spawning
prop.Teleport(...);
prop.DispatchSpawn();

Server.NextFrame(() =>                                   // SetModel goes one frame later
{
    if (prop.IsValid)
        prop.SetModel(model);
});
```

After `DispatchSpawn()` the entity stays in the engine's *staging list* for the rest of the frame. Calling `SetModel()` in that same frame trips the `0 == (flags & EF_IN_STAGING_LIST)` assertion in `skeletoninstance.cpp` and **kills the whole server process** — again, not a .NET exception, and not catchable.

Entity destruction is deferred to `Server.NextFrame` for the same reason.

## How it works

- On map start, the plugin reads that map's own JSON file (`addons/counterstrikesharp/plugins/CS2StoreGifts/maps/<mapname>.json`) and spawns an entity at each saved position. Gift props are solid, so you bump into them rather than walking through.
- Every `CheckIntervalSeconds` it measures the distance between each living player and each gift. If a player enters `PickupRadius`, they get the credits through `IStoreApi.GivePlayerCredits()`, the pickup is announced in chat (if enabled) and the gift disappears.
- Once collected, a gift is **permanently deleted from the map's JSON**: it does not come back on map restart or map change. Gifts are consumable — restock them with `css_gift_add`.
- There is deliberately no pickup sound. The previous implementation (`ExecuteClientCommandFromServer("play <path>")`) crashed the server. The correct API is `CBaseEntity.EmitSound`, which expects a *soundevent* name rather than a file path.
