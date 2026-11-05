# kiln.spawn

Create and destroy server-world entities from templates (creatures, item drops, props), look templates up by name, and
keep a per-world list of what Kiln spawned so it can be removed again. Engine evidence: [docs/re/spawn.md](../../re/spawn.md).

| Layer | Path |
|---|---|
| C table | `native/core/abi/services/kiln_spawn.h` (`kiln.spawn` v1) |
| Native | `native/services/spawn/` → `kiln/services/kiln-spawn.dll` |
| Symbols | `sigdb/symbols/server.d/spawn.json` (features `spawn.create`, `spawn.destroy`, `spawn.templates`, `spawn.entities`) |
| Managed | `managed/services/Kiln.Spawn/` → `kiln/host/Kiln.Spawn.dll`, static class `Kiln.Spawn` |
| Test | `mods/tests/spawn-test/` (three boots; PASS/FAIL lines) |

## C# quick start

Reference the assembly like every service: `<ProjectReference Include=".../managed/services/Kiln.Spawn/Kiln.Spawn.csproj"
Private="false" ExcludeAssets="runtime" />`.

```csharp
using Kiln;

// Templates: name, display name or GUID text. Any thread.
TemplateInfo? vukah = Spawn.Lookup("Enemy_Vukah_Melee");      // or Spawn.Lookup("Vukah")
IReadOnlyList<TemplateInfo> creatures = Spawn.Templates.Where(t => t.Kind == TemplateKind.Creature).ToList();

// Everything below: tick thread (handlers, OnLoad, continuations after await).
if (!Spawn.WorldRunning) return;                               // the save is still loading
EntityId e = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(3805.8, 833.0, 1433.8), yawDegrees: 90,
                                new SpawnOptions { Tag = "raids", Level = 12 });
EntityId drop = await Spawn.Item(722268629 /* Material_T1_Wood_Logs */, 5, playerPos with { Y = playerPos.Y + 1 });
IReadOnlyList<EntityId> pack = await Spawn.Group("Vukah", playerPos, count: 5, spread: 4, new SpawnOptions { Tag = "admin" });

EntityInfo? info = Spawn.Inspect(e);          // template, position, yaw, tracked?, parent; null = gone
bool removed = Spawn.Destroy(e);              // any non-player entity
IReadOnlyList<EntityId> near = Spawn.FindByName("Enemy_", playerPos, radius: 80);

IReadOnlyList<TrackedEntity> mine = Spawn.Tracked;            // what Kiln spawned in this world
CleanupResult r = Spawn.CleanupTracked(tag: "admin");         // destroy them (all, or by tag / origin)
```

- `Spawn.Entity` / `Item` / `Group` return tasks that are already complete: the engine creates the entity at once.
  Failures fault the task: `SpawnException` (unknown template, bad position, engine refused; `Code` = native result)
  or `NotSupportedException` (service unavailable, world not running yet). Calling from another thread throws
  `InvalidOperationException` at once. Nothing is kept per mod, so a mod unload leaves no callbacks behind.
- Positions are exact: there is no ground snapping. Use a point on the ground (a player's position, an entity's).
- Yaw rotates about +Y (up): quaternion `(0, sin(yaw/2), 0, cos(yaw/2))`.
- `SpawnOptions.Flags`: `NoTrack` (not in Kiln's list; you clean up), `Transient` (adds the game's DoNotSave: never
  saved), `AllowUnknown` (spawn a GUID when the template list could not be read).
- `SpawnOptions.Components`: extra spawn components the engine copies into the new entity at once:
  `SpawnComponent.Of("Faction", new byte[] { 11 })`, `SpawnComponent.Of("ImpactHierarchy", bytes)`, or
  `new SpawnComponent(hash, bytes)` (hash = FNV-1a-32 of `"keen::ecs::<Name>"`). At most 64, one value per component;
  the size must equal the engine's (else `SpawnException`, the game logs the expected size). A component Kiln sets
  itself (CurrentTransform, OverrideLevel, DoNotSave) takes your value instead; SpawnTime is refused unless `NoTrack`.
  Check `Spawn.SupportsComponents` (older kiln.spawn: `NotSupportedException`). Native: `KilnSpawnRequest.components` /
  `component_count` (appended in v1; read when `size` covers them) and `capabilities()` bit
  `KILN_SPAWN_CAP_COMPONENTS`. Lab (abilities-test): `Vukah 2542 spawned with Faction 11 from a spawn component (read
  11)`, `a 2-byte Faction component is refused by the engine (code 7)`. kiln.abilities builds its impacts on it.
- `TemplateInfo.NeverSaved`: the template itself carries DoNotSave (true for most enemies and many pickups); its
  entities vanish at the next restart. `Expires`: the template has a LifeTime.
- `Spawn.EntityCount`: entities in the server world now.

## Behaviour on a dedicated server (lab-verified, no player connected)

- The server steps its world while it still loads the save (about 11 s); `Spawn.WorldRunning` turns true at the game's
  `Switching state from LoadEcsScene to Run`. Spawns before that fail (`NotSupportedException`).
- With no player near, spawned creatures stand still and pickups do not fall: the area is not simulated. They were not
  culled within the test window.
- A creature spawns with child entities (weak spots, VFX links) parented to it; `Destroy` removes those as well.
- Persistence follows the template: DoNotSave templates (Vukah, the generic item pickup) are gone after a restart;
  props (a barrel) are saved and come back with a new entity id. The server autosaves every 30 s and on SIGINT stop.

## Tracking (the sidecar)

Every spawn without `NoTrack` is recorded in `kiln/data/spawn/<world GUID>.json` (world GUID from the save header of
`<saveDirectory>/world*.ksd`; `Spawn.WorldKey`). The file is rewritten atomically on every change, and at most every
10 s for position updates.

```json
{ "format": 1, "world": "11111111-…", "save": "…\\savegame\\world0.ksd", "next_serial": 6,
  "entities": [ { "serial": 3, "template": "0ce28db1-…", "name": "Prop_Deco_Steppeprops_Barrel_01",
                  "spawn_time": 14500000294, "pos": [3799.75, 832.5, 1438.75], "spawn_pos": [...],
                  "spawned": 1793763368, "flags": 0, "tag": "spawn-test", "origin": "spawn-test",
                  "missed_sessions": 0, "last_entity": 2544 } ] }
```

- Entity ids only hold for one server session. After a restart Kiln re-finds records among entities of the same
  template that no other record holds: props, pickups and other static templates at the exact saved position (0.3 m),
  creatures and villagers within 8 m of the last known position (nearest). Lab: `re-found tracked #3
  (Prop_Deco_Steppeprops_Barrel_01) as entity 1311 by template + exact position`.
- A record is dropped when its entity disappears during a session (killed, collected, despawned), when it cannot come
  back after a restart (Transient, or a NeverSaved template; logged as "is not in the world: ..."), or after it was not
  found in 3 sessions. `CleanupTracked` destroys live records and drops missing ones; it re-checks the world first.
- `origin` is the calling mod's id; `tag` is yours. Kiln.Admin should tag its spawns ("admin") and clean up by tag.
- If the world has no save yet at boot, records go to `unknown-world.json` and move to the GUID file once the first
  save exists.

## Admin contract

| Admin op | Kiln |
|---|---|
| `spawn {guid, count 1..20, x, y, z, spread}` | `Spawn.Group(guid, new WorldPosition(x, y, z), count, spread ?? 4, new SpawnOptions { Tag = "admin" })` → `{spawned = ids.Count}` |
| `despawn_spawned` | `Spawn.CleanupTracked(tag: "admin")` → `{removed = Destroyed, failed = 0}` (also works after a restart) |
| `enemies` catalog | `Spawn.Templates.Where(t => t.Kind == TemplateKind.Creature)` (GUID, name, display name); `near`: `Spawn.FindByName("Enemy_", center, radius)` (kiln.world's `find(NetworkEnemy)` is the exact filter) |
| spawn items near a player | `Spawn.Item(itemId, amount, playerPos with { Y = playerPos.Y + 1 })` (ids from kiln.items / `items.json`) |

## C table (kiln_spawn.h)

`spawn(req, &id)`, `spawn_item(item, amount, pos, level, flags, tag, origin, &id)`, `destroy(id)`,
`entity_info(id, &snapshot)`, `find(guid|name filter, center, radius, ids, cap, &count)`, `template_find`,
`template_get`, `template_count`, `template_at`, `tracked_count`, `tracked_at`, `cleanup(tag, origin, &result)`,
`refresh(&live)`, `world_key`, `world_state`. Game-state functions return `KILN_ERR_WRONG_THREAD` off the tick
thread and `KILN_ERR_UNAVAILABLE` before the world runs; the catalog and tracked-list reads work from any thread.
Native plugins look it up in `on_ready`: `api->service({"kiln.spawn", 10}, 1)`.

## Failure modes

- The world pointer comes from the core (`KilnApi.server_world`); with an older core kiln.spawn hooks SimStep itself
  (capture only). The `kiln.spawn: ready (world from ...)` line says which.
- Unknown build: the sigdb features turn off and the service is not published (or publishes with destroy/templates/
  entity scan off; see the `kiln.spawn: ready (...)` line). The server runs on.
- Every engine call and memory walk is guarded (SEH); map walks check keys against records and give up on the first
  mismatch. A refused spawn logs the game's own reason (e.g. a component size mismatch) and returns
  `KILN_ERR_INTERNAL`.
- `Destroy` works on any non-player entity, including world and base entities: once saved, that is permanent.

## Test

```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/spawn-test/SpawnTest.csproj -c Release -o $KILN_OUT/mods/spawn-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any
tools/lab/lab.sh start <lab>   # boot 1: spawn, inspect, destroy, group, sidecar   (43 PASS)
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>   # boot 2: persistence + cleanup (9 PASS)
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>   # boot 3: nothing left (5 PASS)
grep -a spawn-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log
```
Delete `kiln/data/spawn-test/state.json` to start over. Use a fresh lab copy for a clean run (boot 1 leaves a barrel
in the save until boot 2 cleans it up).

## In a game client (hosted worlds)
- The world key comes from kiln.saves (the hosted world's GUID; a client has no server save folder). Until the world
  has loaded the tracker runs under `unknown-world` and re-keys itself (checked every 750 steps).
- When the game closes the server world, the sidecar is saved; the next server world opens its own at its first step and
  re-finds its records from there (records are per world GUID, ids per session).
