# The mod API: the world, writes and building

Part of the mod API guides ([api.md](api.md) is the index). Here: spawn, world, time, settings and combat, typed ECS reads,
tracked world writes, world edits, block queries and controlled breaking, building tools, and the ECS programs pointer.
Signatures are the common ones; the XML docs and the generated API reference ([docs-map.md](docs-map.md)) have the rest.

## Spawn, world, time, settings, combat
- Spawn: `Spawn.Lookup(template)`, `await Spawn.Entity(template, pos, yaw, options)`, `Spawn.Item`,
  `Spawn.Group`, `Spawn.Inspect`, `Spawn.Destroy`, `Spawn.Tracked`, `Spawn.CleanupTracked(tag)`. Check
  `Spawn.WorldRunning` first. dev.20+: `SpawnOptions.Components` takes up to 64 `SpawnComponent.Of(name, bytes/value)`
  entries applied with the spawn, before its first tick; check `Spawn.SupportsComponents`. Exact reflected sizes are
  required, and `SpawnTime` needs `SpawnFlags.NoTrack`. This is distinct from queued component changes later.
- World: positions are `WorldPosition` in metres; teleport and entity reads; dev.27+ `World.Query` gives entity sets.
  Writes: [below](#tracked-world-writes-dev27). dev.28+ voxel rays, ground, line of sight, paths (Kiln.Queries), terrain
  overlays (Kiln.Terrain) and `World.SetTransformScale`: [gameplay.md](gameplay.md). Reads from a Kiln task:
  `WorldConcurrent` ([tasks.md](tasks.md)).
- Time: `Time.State`, `Time.Set("19:45")`, `Time.ResetWeather()`. Set the time only after the world runs
  (`ServerStartedEvent`), or the save load overwrites it. dev.27+ weather: `await Time.SetWeather("rain",
  TimeSpan.FromMinutes(10))` → `WeatherResult` (by name, on every layer that has it, or `layer:`; no duration holds it
  until `Time.ReleaseWeather()`; `"none"` is a clear sky). `Time.Weather` (layers: what runs, what Kiln holds),
  `Time.WeatherPhenomena` (the names), `WeatherChangedEvent` (`ByKiln` tells Kiln's from the game's own rolls).
  Capability `time.weather_choose`. A hold does not outlast a restart.
- Settings: `Settings.Get(name)`, `Settings.Set(name, value)`, `TrySet`, `Settings.All`.
- Combat (dev.20+): `Attributes.Get(entity, GameAttribute.Stamina)` and `Set` / `Add` (also Mana, BodyHeat, Oxygen);
  check `Available`/read results. These use the game attribute path, on the tick thread.
- Combat: `Combat.GetHealth`, `SetHealth`, `Heal`, `await Combat.Damage(target, n, attacker)`, `Kill`,
  `SetInvulnerable`; `EntityHurtEvent` (cancel, change `Amount`), `EntityDeathEvent`, ...
- Gameplay (dev.20+): `Offers.Add/Remove/IsActive/Status` puts an E prompt on a suitable world entity, owned by this mod;
  it needs collision and cannot already have a game/other-mod offer. Check availability/status; pending is not visible proof.
- Gameplay: `InteractEvent`, `CraftEvent`, `LootEvent`, `TeleportEvent`, `NaturalSpawnEvent`,
  protection zones (`Gameplay.Protect`).

## Typed ECS reads (dev.26+; not seen by a player)
`<KilnServices>Game</KilnServices>`, then `Kiln.Ecs` (Kiln.Game): a typed struct for every component and event of
the game, generated per verified build (`Ecs.Health`, `Ecs.LastTransform`, `Ecs.Enemy01_State`, `Ecs.FactionType`).
```csharp
if (!Ecs.Available) return;                                   // kiln.world off: the reads would throw
if (!Ecs.Supports<Ecs.LastTransform>()) { Log.Warn(Ecs.WhyNot<Ecs.LastTransform>()!); return; }
if (Ecs.TryGet<Ecs.LastTransform>(entity, out var t)) Log.Info($"x {t.transform.position.x}");
foreach (var e in Ecs.All<Ecs.Health>()) { }                  // also Ecs.Get<T>(e), Ecs.Has<T>(e)
```
- `TryGet`, `Get`, `Has`, `All`: tick thread; `Available`, `Supports`, `WhyNot`: any thread.
- Each type is checked against the running game at first use. A mismatch reads as nothing (false, null, empty) with
  one warning: never a partial read. Kiln also stamps the mod (`kiln.gametypes.json`) and refuses it by name at load
  when a type it uses changed; an enum constant the compiler folds into a number is not stamped.
- Reads only: a struct is a copy. Write with [tracked world writes](#tracked-world-writes-dev27) (dev.27+).
- `Kiln.GameTypes.Components` and the nine old structs are obsolete (`Use Kiln.Ecs.`) and still work.
- dev.28+: `GameType.ImpactName`, `GameTypes.ByImpactName` find a type by the name Impact scripts use (`ecs.Health`).
  dev.29+: `Ecs.Programs.*` are typed views of the game's programs for `Systems.Wrap<T>` ([world-programs.md](world-programs.md)).
- Server lab: typed reads equal raw bytes for 118 components; the rest and the client side are unproven.
  Page: `docs/kiln/typegen.md` §4.

## Tracked world writes (dev.27+)
Kiln.World with `Game` in `KilnServices`. Server worlds only (a dedicated server or a hosting game). Page:
`docs/kiln/services/world.md`.
```csharp
var r = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1]);   // next sim step
if (!r.Ok) Log.Warn(r.ToString());                     // NotDeclared, NoComponent, Dropped (ask again next step), ...
else if (r.Kept == false) Log.Warn($"written over by {r.WrittenOverBy ?? "the game or another mod"}");
```
- `World.Write<T>(entity, value)`, `World.Update<T>(entity, change)` (the only way to change an attribute's values:
  Health, Stamina, Fuel), `World.WriteField(entity, "Component", "path", value)`, `World.WriteAt(entity, "Component",
  offset, value)` → `Task<WorldWriteResult>`. They go through the game's write tracking: saved with the world, sent to
  players where the game tracks the type (`SaveTracked`, `ReplicationTracked`). The `Update` callback runs inside the
  game's step: touch only the value. Capability `world.tracked_writes` (`World.SupportsTrackedWrites`);
  `ComponentWrittenEvent` follows a change.
- List each written component type under `"writes"` in kiln.mod.json. Kiln.Sdk adds the types your code passes to
  `World.Write`/`Update`; list by hand what `WriteField`/`WriteAt` name as a string (warning KILN030: a type the build
  could not resolve). Unlisted: `NotDeclared`. `World.WriteInfo(component)` says whether you can write it and which
  game programs write it too. 512 writes run per step; all mods share 91 component types.
- A write goes to the entity its id named at the call, never to a later one in the same slot. One for an entity without
  a spawn time (loaded from the save, an item entity) that would wait behind a full step answers `Dropped`.
- A type the game also writes every step can be written over one step later: `Kept` false. `WrittenOverBy` names the
  Kiln policy that did it: a water refill (`Items.Fuel.Refill`, `Items.Fuel.KeepFull`, or a player's `Water` rule, such
  as the Creative menu's rule set) keeps a container's `Fuel` at its level. End or narrow that refill before you write
  `Fuel`, or change the water through `Items.Fuel`. Null with `Kept` false: a game program or another mod.
- `World.TryWrite` is obsolete: `World.WriteRaw`, a raw store the game neither saves nor sends, only for inputs the game
  reads in the coming step (`PlayerInput`).

## World edits
`await Build.Dig / Add / Flatten / Smooth / Paint / Replace(BuildShape.Sphere|Box|Cylinder(...))`,
`Build.Read/Write(box, cells)`, `Build.PlaceProp(template, pos, rotation)`, `Schematic.CaptureAsync`, `.Paste`,
`await Build.PlaceOnce(key, schematic, at)`, `await Build.Undo()`. Results carry `Refusal` (zone, base, not loaded).
Vanilla clients see the edits and the game saves them.

dev.28+: `Build.WriteIfUnchanged` writes only while the cells still are what you read. Terrain overlays:
[gameplay.md](gameplay.md).

**dev.20+ flame writes:** `await WorldProgress.SetFlameLevel(level)` (Quests) returns `FlameChange`: inspect `Ok`,
`Reason`, `Marked` and `Mirrored`, not just queue acceptance. `LimitsAt(level)` reads derived limits. Client UI/fog/placement
checks remain pending; a failed rollback is explicitly reported and may leave changed state.

## Terrain: overlays, and the baked tiles (dev.32 tooling)
What a mod can do to the ground is an **overlay**: `Terrain.Apply(new TerrainPatch(...))` (Kiln.Terrain, server, tick thread) writes a
bounded patch once per world through `Kiln.Build`, so the game saves it and vanilla clients see it. A region is at most 128 m
across and 1,048,576 cells; it cannot be taken away again (`Forget` does not undo the cells: keep what you replaced yourself or use
`Transient`); a palette names the game's materials only. Sample `OreVein`, pages `docs/kiln/modding/terrain-tiles.md` (a walkthrough for both), `docs/kiln/services/terrain.md`.

**Baked terrain tiles** are the game's own shipped ground (18,964 tiles, 7 levels of detail), read by the game before a world loads.
dev.32 adds **tools and tests only** for them (`tools/kfc`: `kfc.py terrain info|check|column|cook`, a tile codec, a checker that
every shipped tile round-trips byte for byte, a lab qualification pack): **no mod API, no ABI, no format a mod can ship**, and
`terrain.baked` stays off. So a mod cannot replace ground tiles yet: build an overlay. A lab dedicated server reads a Kiln-written
tile and a reused shipped tile back cell for cell (`mods/tests/terrain-tile-test`, 8 checks); **no game client has shown** a cooked tile
(holes, old horizon, seams across levels of detail, collision, a joined or hosted client). Say so, and do not promise "custom
terrain" in a mod's description.

## Block queries and controlled breaking (30.6+, mod API 0.2.15)
`Build.BlockAt`, `Build.FindWall` and `Build.BreakBox` (Kiln.Build). **Server tick, in Pre or Post only**: not from a world
program or a Kiln task; a dedicated server or a hosting client is authoritative, a joined client cannot read or break the
host's world. Nothing waits for a load: call `Build.Load` before querying an unloaded area. Cells must lie inside
`Build.Grid`; an out-of-grid cell is `InvalidArgument`, not proof of air. Page `docs/kiln/services/build-blocks.md`.
```csharp
BlockQueryResult q = Build.BlockAt(new WorldPosition(3024, 903, 1552));
if (q.Info is { } b) Log.Info($"{b.Kind}, material {b.Material}, {b.Provenance}, health {b.Health}");   // else q.Refusal, q.Reason
BlockQueryResult wall = Build.FindWall(from, to, FindWallFlags.PlayerTerrain);
// wall.Ok && !wall.Found: a clear line. !wall.Ok: refused or unavailable, NOT a clear line.
var options = new BlockBreakOptions
{
    Flags = BlockBreakFlags.Instant | BlockBreakFlags.PlayerTerrain,
    Build = new BuildOptions { Flags = BuildFlags.RespectZones | BuildFlags.InBases, Actor = player },
};
BlockBreakResult r = Build.BreakBox(new CellBox(x, y, z, 8, 4, 8), options);       // at most 4096 loaded cells
if (!r.Ok) Log.Info($"{r.Refusal}: {r.Reason}");
```
- **`BlockInfo`**: `Kind` (`BuildBlockKind.Air`, `Terrain`, `Building`), raw `Material` (terrain 1-127, **building 128-255**)
  and `Fill`, `Center`, `Provenance` (`BlockProvenance.PlayerPlaced`, `Natural`, `Unknown`: a comparison with the shipped
  world, not an owner), `Hardness` (`BlockHardness.Soft` .. `VeryHard`, `Unbreakable`), `Health`, `Damage` (null = unknown,
  never guessed). A refusal is `BlockRefusal` (`NotLoaded`, `WrongThread`, `WrongSide`, `Offline`, `NoSession`, `Zone`, `Base`,
  `InvalidArgument`, `SymbolMissing`, `ServiceMissing`, `Faulted`, `Unverified`, `DamageMapFull`, ...).
- **`FindWall`**: the first building cell from `from` to `to` (the start cell included, at most 64 metres, deterministic cell
  stepping, not a game raycast). `PlayerTerrain` also stops at player-placed terrain, `Terrain` at any terrain.
- **`BreakBox`**: `Selection` (a bool per cell, x fastest), `Materials` (raw ids; empty = all breakable), `Damage` (five ints by
  hardness), `ProtectedSphere = new BlockBreakSphere(center, radius, margin, minimumRadius)` (spares nearby ruins),
  `TerrainHealthScale`, `TerrainHealAfter`. The default is damage mode (cracks, healing in about 20 s), not instant
  removal; `Instant` removes. Natural and unknown terrain is never removed; natural buildings need `WorldBuildings`;
  unbreakable and unknown-material cells are always spared and counted. A mode with no qualified implementation refuses
  the **whole call before any change**; `Drops` is unavailable (`SymbolMissing`).
- **`BlockBreakResult`**: `Considered`, `Damaged`, `Removed`, `Dropped`, `ProtectedCells`, `UnknownCells`, `UnbreakableCells`,
  `ExcludedCells`, `BlocksWritten`, `Kept`, `Verified`, `MayHaveChanged`, `Refusal`. **No rollback is promised**: a fault can
  leave earlier cells written (`MayHaveChanged`); without `Verified`, `Removed` is a lower bound; never repeat the whole
  call or give drops from planned counts. Session undo and zones follow the other `Build` calls.
- Capabilities `build.block_info`, `build.block_metadata`, `build.block_provenance`, `build.find_wall`,
  `build.break_building`, `build.break_damage`, `build.break_terrain`, `build.break_drops` (SymbolMissing). Not run in a real game.

## Building tools (30.5+, mod API 0.2.14)
A tool that adds or clears terrain with the game's own rules, declared as blocks (`Blocks` and `Tools` in `KilnServices`;
`clients: mod`: every participating game needs the mod). Pages `docs/kiln/services/tools.md`, `tool-actions.md`,
`tool-payment.md`, `terrain-tool-carving.md`.
```csharp
var materials = Blocks.Define("loose-stone", new ToolMaterialPoolBlock { Name = "Loose stone",
    Entries = [new("Build_TerrainMaterial_T1_Rubble", 8, 16)] });          // item, terrain cell, VoxelsPerItem
var small = Blocks.Define("small", new TerrainBrushBlock { Name = "Small stone brush",
    Shape = TerrainBrushShape.Sphere, Radius = 0.5 });
Blocks.Define("stone-tool", new BuildingToolBlock { Name = "Stone tool", MaterialPool = materials, Brushes = [small],
    Primary = new TerrainToolAction { Mode = TerrainToolMode.Add },
    Secondary = new TerrainToolAction { Mode = TerrainToolMode.Remove, RemoveMaterials = [8], TerrainOnly = true } });
```
- A **pool** has 1-64 distinct items and distinct terrain cells (1-127); every item must place its declared cell and belong
  to the game's own terrain material pool (the compiler refuses a non-member). `VoxelsPerItem` 1 to 1,000,000: placement pays
  the rounded-up added volume divided by it, from the backpack and connected Magic chests (ordinary chests are not a
  source), with the game's undo. A tool with no Add action needs no pool.
- A **brush** is a sphere of radius 0.25 to 8 m; a tool has up to 64 brushes, and the game's brush picker shows the first 16
  in the tool's order. The stroke uses the exact radius; the cursor preview is the nearest vanilla Overgrowth size (0.5,
  1.5 or 2.5 m; the game draws it as a fixed effect per shape). The tool owns its brushes. `Identity =
  BlockIdentity.Legacy(...)` keeps an existing saved item, `Was` a renamed one, `Icon` a PNG in the mod folder.
- **`TerrainToolAction`** (Kiln.Tools): `Mode` `TerrainToolMode.Add` or `Remove`; `RemoveMaterials` the terrain cells a Remove
  may clear (an empty list permits none; cell 0 and 128-255 are not valid); `TerrainOnly` must be true. Primary and secondary
  follow the player's own building controls; the game's permission, range and protected-area checks still run, and a stroke
  must fit inside one permitted building area. The actions are declarative: there is no managed callback.
- `ToolMaterials.TrySnapshot(actor, tool, out snapshot)` reads a player's counts of a tool's materials (not authority; it returns false
  while `tools.material_snapshot` is unavailable, as in 30.5 and 30.6). Capabilities `tools.actions`, `.material_lookup`, `.material_picker`, `.preview`, `.brush_families`,
  `.identity`. Not yet checked in a real game: the owner runs the guided checks.

## ECS world programs (dev.20+)
For `kiln.programs.json` boot declarations and `EcsPrograms.Bind` in `OnLoad`, dev.29+ client-world programs, wraps of
the game's programs (`Kiln.Systems`) and `Systems.Define`, read [world-programs.md](world-programs.md). Ordinary `OnLoad` is too late to add a scheduler declaration. A callback is a worker
context, never a place to call tick-thread game services or retain component pointers.
