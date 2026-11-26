# kiln.build: world edits the game saves and replicates

Mods change the world (terrain, building blocks, props) the way the game itself does, so **vanilla clients see the
result** and the game's save keeps it. Plus WorldEdit-style schematics (`.kschem`), a per-session undo journal,
`Build.PlaceOnce` (a structure placed once per world) and `/build` admin commands.

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_build.h` (`kiln.build` v1) |
| Native | `native/services/build/` → `kiln/services/kiln-build.dll` |
| Symbols | `sigdb/symbols/server.d/build.json` (features `build.read`, `build.write`, `build.items`, `build.zones`, `build.persist`) |
| Managed | `managed/services/Kiln.Build/` → `kiln/host/Kiln.Build.dll` (`Build`, `Schematic`, `BuildCommands`, events) |
| Sample | `mods/samples/Schematics` (the `/build` commands, a built-in outpost, outposts placed once per world) |
| Test | `mods/tests/build-test` (four boots: SIGINT, kill -9, cleanup) |
| Evidence | `docs/re/build.md` |

## How it works (and the game's rules it follows)
- **Voxels.** Terrain and building blocks are the same grid: 0.5 m cells (level 6 of the engine's pyramid), origin
  (0, 0, 0), 20480 x 20480 cells. A cell is the engine's own u16: material (1..127 terrain, 128..255 building-block
  materials such as Rough Wood Block 143) and fill 0..255 (255 solid). Kiln reads cells through the `ecs::VoxelGrid`
  interface the game publishes and writes them through the engine's block writer and store commit, the sink every
  building and terraforming kernel of the game ends in. The store tracks changes per connected peer: every client gets
  the new cells like after a player's own edit.
- **Streaming.** A dedicated server keeps voxel tiles (128 m) resident only around bases and players. Kiln asks for
  the tiles an edit needs (reading a tile makes the game load it in 1-3 s) and the async API waits for them
  (`BuildOptions.LoadTimeout`, default 20 s). Sync calls on a tile that is not loaded throw `BuildException` (and ask
  for it).
- **Saving.** The game saves terrain **only inside player-base build zones**; everything else regrows at the next
  restart. Kiln remembers the 16 m chunks its own writes touch (`kiln/data/kiln.build/regions/<world GUID>.json`).
  When the game saves the world, Kiln adds boxes to the list of regions that save writes, for the kept chunks the
  bases do not save whole already, so Kiln's edits are saved like a base. Inside a Kiln region the players' own
  digging is kept too. `Build.Release(box)` forgets regions (the terrain there reverts at the next restart).
- **Kiln's boxes, bases and Flame Altars** (kiln#136, thanks bisdat). The save writes one record per region, and the
  world list counts the records as Flame Altars. A base's own record keeps the terrain of the 4 m blocks its zone
  touches, so a kept 16 m chunk wholly inside a base adds no box, unless the base's zone touches more than 4,096 chunks
  (below, Limits). A kept chunk outside every base adds a box
  (neighbouring chunks share one), and so does a kept chunk on a base's edge that reaches outside it, since the base's
  record drops the part outside its zone and Kiln knows only which 16 m chunks its edits touched, not where in them.
  The world list shows each box as one more altar: the save has no other place for terrain
  ([RE notes](../../re/build.md) §12). The box is only in the save: the base areas the game groups stations, storage
  and beds by, and sends to every client, are the real bases alone. (While a world loads, the game briefly treats
  every saved record, a box too, as a build zone, until the altars' own zones replace them.) Kiln decides at each save
  from the regions of that same save, so a save right after a load, when an altar's zone is still its first size,
  still keeps every edit. When a base shrinks, moves, is abandoned or goes,
  its kept chunks get boxes again at the next save; the JSON keeps every chunk either way. A world saved by 30.8 or
  earlier shows its extra altars until its next save. Up to 0.2.0-dev.30.8 every kept chunk had a box in every save,
  and the boxes were also base zones for the game's systems.
- **Transient edits** (`BuildFlags.Transient`) add no region and are gone after the next restart, also where the game's
  save writes them anyway (inside a player base, or in a 32-cell chunk another edit keeps): every transient write
  journals the cells it changed, their value before and after, on disk before it commits them: appended to
  `kiln/data/kiln.build/transient/<world GUID>.log`, which is compacted into `<world GUID>.bin` at each save, every
  second and when it grows. If the journal cannot be written (a full disk), the transient write is refused and nothing
  is committed, so no save ever holds a transient cell without its record. At the next start,
  once the world runs, each journaled cell that still holds what the transient write left gets its earlier value back,
  block by block as its tiles load, through the same store commit as any edit (players see it; no `BuildEditingEvent`
  is raised for it). A cell changed since (a player dug it) is left as it is; a block whose put-back commit fails stays
  journaled for the next start. A later edit without the flag takes its
  cells out of the journal (they are meant to stay), on disk before it commits; if that cannot be written, an edit
  that sets journaled transient cells is refused (`BuildException`, "unavailable") rather than risk the next start
  putting it back (edits elsewhere are not affected); a transient write again in the new session makes them that
  session's. Put-back cells stay on record until two world files were written after it. Limits: the journal holds
  8,192 blocks (about 4 million cells); past that, when the journal cannot be written, and while the world's identity is
  unknown (no kiln.saves, or
  kiln.saves could not read the world's id from the game; it reads it at load, also for a new world before its first
  save), a transient write is refused (`BuildException`, "unavailable"). `Transient` with `Unloaded` is refused
  (`ArgumentException`): the put-back needs the real earlier cells.
- **Crash behaviour.** The game writes the world when it saves (every 30 s, on a player leaving, on shutdown, on
  `Saves.Request()`). An edit followed by a save survives `kill -9`; an edit after the last save is lost by `kill -9`
  like any player edit. Kiln's region list is written within a second of an edit and is harmless when the edit was lost.
- **Props** are entities: Kiln places them with the components the game's building helper gives a placed prop
  (CurrentTransform, DisableRandomLootOnDestroy, UsedItem = what dismantling gives back). The game saves props
  everywhere; base mechanics (comfort, crafting stations in range, beds) count them like player-placed ones because the
  game recomputes those from entities every step.
- **Ownership.** Voxels have no owner in the engine. A Kiln edit names an actor (the player it is for, or none) and its
  mod; neither is written into the world. Kiln's edits never pass the game's building programs, so
  `Gameplay.Protect` zones do not stop them unless the edit says `BuildFlags.RespectZones` (then the actor is checked
  like a player building there; player edits with the build hammer stay governed by the zones as before). To protect
  player bases, an edit touching a base's build zone (the game's own BuildZoneSystem list) is refused unless
  `BuildFlags.InBases`, and props that would found a base (flame altars) need `BuildFlags.AllowAltars`.
  Since 0.2.0-dev.30.6, requested protection fails closed: without `InBases`, missing/unreadable base mappings refuse
  the edit; with `RespectZones`, an unavailable zone checker refuses it. Build status diagnostics distinguish those
  cases. Explicit `InBases` and leaving `RespectZones` unset preserve their existing opt-out behaviour.

## Block facts, wall searches and controlled breaking

Since 0.2.0-dev.30.6: `Build.BlockAt`, `Build.FindWall` and `Build.BreakBox` expose loaded cell facts, typed
shipped-world provenance, cell-stepped wall hits and guarded, selective breaking. See [the block API](build-blocks.md)
for capabilities, refusal reasons, damage/drop availability and partial-commit semantics.

## Using it from a mod
Reference `Kiln.Build` (`Private="false" ExcludeAssets="runtime"`). Everything runs on the tick thread (handlers,
`OnLoad`, continuations after `await`).

```csharp
using System.Numerics;
using Kiln;

// cells
var box = Build.BoxAround(new WorldPosition(3024, 903, 1552), 2, 2, 2);       // CellBox, ±2 m
await Build.Load(box);                                                      // stream the tiles in
Voxel[] cells = Build.Read(box);                                            // x fastest, then y, then z
BuildResult r = Build.Write(box, cells.Select(c => c.IsAir ? c : Voxel.Of(155)).ToArray());   // repaint as Rough Stone
// r.CellsChanged, r.Verified (read back equal), r.Kept (in the game's save), r.Refusal (Zone, Base, NotLoaded, ...)

// shapes (async: they load the tiles first)
await Build.Dig(BuildShape.Sphere(pos, 3));
await Build.Add(BuildShape.Box(pos, 2, 0.5, 2), Build.MaterialOf(Items.Lookup("Block_T1_Wood_Rough_CRAFTING")!.Id.Value));
await Build.Flatten(BuildShape.Cylinder(pos, 6, 4), levelY: pos.Y);
await Build.Smooth(BuildShape.Sphere(pos, 4), strength: 0.7f, passes: 2);
await Build.Paint(BuildShape.Sphere(pos, 4), material: 2);
await Build.Replace(BuildShape.Box(pos, 8, 4, 8), from: 1, to: 2);

// props
EntityId chest = Build.PlaceProp("Prop_Decoration_T1_Storage_24", pos, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
IReadOnlyList<PlacedProp> mine = Build.Props(box);                          // building props (UsedItem) in the box
Build.RemoveProp(chest);

// schematics
Schematic s = await Schematic.CaptureAsync(Build.BoxBetween(cornerA, cornerB), new() { Name = "hut" });
s.Save(Path.Combine(Context.DataDirectory, "hut.kschem"));
PasteResult p = await Schematic.Load(file).Paste(target, new PasteOptions { Rotation = 1 });   // 90° about +Y
await Build.Undo();                                                         // this mod's newest edit

// once per world (recorded in WorldData: a restart or reload does not place it again)
PlaceOnceResult once = await Build.PlaceOnce("my-outpost", s, new WorldPosition(3024, 902.6, 1552));
```

### `Build` (static)
| Member | Notes |
|---|---|
| `Available`, `Status` | `BuildStatus`: read/write/props/persist, counters, kept chunks, `Detail` (why something is off) |
| `Grid` | `VoxelGrid` (cell size, origin, size, tile size, level) |
| `CellOf(pos)`, `CornerOf(cell)`, `CenterOf(cell)`, `BoxBetween(a, b)`, `BoxAround(c, hx, hy, hz)` | world metres ↔ cells |
| `IsLoaded(box)` (asks for missing tiles), `await Load(box, timeout)` | streaming |
| `Read(box)` / `TryRead` / `await ReadAsync` / `ReadCell(cell)` | |
| `Write(box, cells, options, mask)` / `await WriteAsync` | exact cells; mask: false = keep the world's cell |
| `WriteIfUnchanged(box, cells, expected, options, mask)` | compare-and-write (since dev.28): writes only if the cells it sets still hold `expected` when it commits, else `BuildRefusal.Changed` and nothing is written ([below](#compare-and-write-since-dev28)) |
| `await Dig / Add / Flatten / Smooth / Paint / Replace(shape, …)`, `ShapeBox(op, shape)` | `BuildShape.Box/Sphere/Cylinder` in metres; a one-cell smooth edge like the game's brushes |
| `PlaceProp(template|guid, pos, rotation, itemId)`, `RemoveProp(e)`, `Prop(e)`, `Props(min, max | box, filter)` | `PropFilter.Placed` (default: what players placed), `Saved`, `All`, `Static`. A template is a game template, a clone (kiln.gamedata), or the item that places one |
| `Bases` | `PlayerBase` (altar, build-zone centre and half size) |
| `BaseLimits` | `BaseLimits`: the Flame Altar count and build-zone sizes, the game's own and this start's, and the base rules in them (since 0.2.0-dev.31, [below](#base-rules-and-base-limits-since-020-dev31)) |
| `MaterialOf(itemId)`, `TemplateOf(itemId)` | the voxel material a block item places; the template a prop item places |
| `Keep(box)`, `Release(box)` | Kiln's save regions |
| `History(owner)`, `JournalOwner(options)`, `await Undo(owner)`, `await UndoEntry(id)` | the session journal |
| `await PlaceOnce(key, schematic, at, options)`, `PlaceOnce(key, Func<Task<bool>>)`, `WasPlaced`, `PlacedOnce`, `ForgetPlaced` | per mod, per world |

`BuildOptions`: `Flags` (`RespectZones`, `InBases`, `AllowAltars`, `Unloaded`, `DryRun`, `Transient`), `Actor`,
`Journal` (default true), `LoadTimeout`, `Label`.

### Journal and undo
Every edit through Kiln.Build is recorded for the session under its owner (the calling mod, or `player:<entity>` when
`Actor` is set; `/build` uses the player): the cells before and after, the props placed and removed. `Undo` writes the
old cells back **only where the world still holds what Kiln wrote** (a later change by a player or another edit wins;
`UndoResult.CellsKept` counts those), removes the props it placed and puts removed ones back. A paste is one entry.
The journal keeps the newest 64 entries and about 24 M cells; it is gone after a restart.

### Events
| Event | When | Thread | Cancel | Fields |
|---|---|---|---|---|
| `BuildEditingEvent` : cancelable | before a Kiln edit changes anything (cells, shapes, pastes, props) | tick, inside the call | the edit is skipped; the call returns `BuildRefusal.Canceled` (props: `EntityId.None`) | `Kind`, `Box`, `Actor`, `Origin` (mod id), `Label`; read-only |
| `BuildEditEvent` | after a Kiln edit (also when the world refused it: zone, base, not loaded; and when `WriteIfUnchanged` found the cells changed) | tick, inside the call | no | `Kind`, `Box`, `Actor`, `Origin`, `Label`, `Result`, `PropsPlaced`, `PropsRemoved`, `JournalId`, `Changed` |

Player edits with the build hammer are Kiln.Gameplay's `BuildEvent`s, not these.

### Compare-and-write (since dev.28)
A mod that reads cells, decides and then writes cannot know whether something changed them in between: a
`BuildEditingEvent` handler of another mod runs inside the write, before the commit. `Build.WriteIfUnchanged(box,
cells, expected, options, mask)` takes the cells the caller read and compares them with the world **after the editing
handlers and right before the commit**, in the same call: where `mask` is true (every cell without a mask), the world
must still hold `expected`. One cell that differs and the whole write is refused: `BuildRefusal.Changed`, nothing is
written, `BuildEditEvent` carries the refusal. Unmasked cells are not compared; the writer keeps the world's values
there anyway. Kiln.Terrain's overlays write this way.

```csharp
await Build.Load(box);
var before = Build.Read(box);
var r = Build.WriteIfUnchanged(box, Carve(before), before);
if (r.Refusal == BuildRefusal.Changed) { /* someone edited it: read again, or leave it */ }
```

Why the compare and the commit are one step: both run on the tick thread inside one call, in Kiln's Pre or Post,
where no job of the game's world runs (Pre runs before the step, Post after `WaitWorldJobs`). No other mod's code runs
between them, and the game's own voxel writers (the terraforming impact and the brush kernels, which commit through the
same store; `docs/re/build.md` §3) run inside the world's step, so they cannot fall between them either, also when a
dedicated server runs that step's programs on worker threads. Not established: whether any other caller of the store's
commit runs outside the world's jobs (the store commit's other callers and the streaming threads were not traced).
The tiles must be loaded: a missing tile is `NotLoaded`, also with `BuildFlags.Unloaded`.

## Base rules and base limits (since 0.2.0-dev.31)

How many Flame Altars a world may have at each Flame level, and how large an altar's build zone is at each altar level,
are numbers the game reads once, when it starts. **Base rules** change them in Kiln's own names: a mod names no game
type, field or GUID, and two mods with base rules never conflict.

| Member | JSON | Range | Means |
|---|---|---|---|
| `MinimumAltars` | `minimumAltars` | 1 to 16, or left out | at least this many Flame Altars at every Flame level |
| `ExtraAltars` | `extraAltars` | 0 to 15 | this many more than the game's number at every Flame level, at most 16 |
| `BuildAreaPercent` | `buildAreaPercent` | 100 to 200, or left out | every altar level's build zone at this share of the game's, at most 256 m an axis |
| `Sizes` | `sizes` | altar levels 1 to 5, one entry each | at least this full size (`AltarSize(level, Vector3)`, `{"altarLevel": 1, "size": [x, y, z]}`): each axis finite, 0.5 to 256 m, a multiple of 0.5 |

A rule never lowers the game's numbers. A value out of range (17 altars, a size of 300 m, altar level 0) throws
`ArgumentException` when it is staged; in a blocks file it refuses the mod before the game starts, naming the rule and
the member. Above 16 Flame Altars is not a base rule: the game's code counts no more than 16. Since dev.32 the registry field
`limits.maxAltars` (with `balance.altarsPerFlameLevel` above 16) raises that through Kiln's own hook of the game's altar check
([registries/limits.md](../modding/registries/limits.md)); the game's bases still count 16.

```csharp
// in Mod.OnBoot (kiln.mod.json: "boot": true)
BootDefinitions.For(boot).Define("roomy", new BaseRulesBlock
{
    MinimumAltars = 6, ExtraAltars = 2, BuildAreaPercent = 150,
    Sizes = [new AltarSize(1, new Vector3(64, 64, 64))],
});
```

```json
{ "format": 1, "baseRules": { "roomy": { "minimumAltars": 6, "extraAltars": 2, "buildAreaPercent": 150,
                                        "sizes": [{ "altarLevel": 1, "size": [64, 64, 64] }] } } }
```

The JSON is a blocks file's `baseRules` section (`blocks/*.json`; a data-only mod needs nothing else, see
`mods/samples/RoomyBases`). A name the blocks file and `OnBoot` both stage stays the file's.

**How Kiln combines them.** kiln.content reads every mod's rules before the game starts and makes one change of the
game's balancing table when the game's resource service comes up, before the game reads it:

- at each Flame level the count is the largest any rule asks for: `max(game, min(16, max(minimumAltars, game + extraAltars)))`
  over every rule;
- at each altar level, on each axis, the size is the largest any rule asks for (`buildAreaPercent` of the game's size,
  held at 256 m, or a `sizes` entry);
- this is written **after every mod's own resource patches** and only raises: each number ends at the larger of what the
  patches left and what the rules ask of the game's own value. A mod that patches the same fields by the game's names
  (More Flame Altars 0.4.0: `altarsPerFlameLevel[*]`, `buildzoneSizesPerAltarLevel[*].x/y/z`) is not refused because of
  base rules, in either load order, and the result is never lower than what it or any rule asked for. Base rules claim
  no field, so the rules of [Two mods on one resource](../modding/resource-patches.md#two-mods-on-one-resource) never
  see them.

If Kiln cannot write the change (a game build whose table has another shape), every mod with a rule gets a fault in
`kiln.log` and `state/boot.json` naming its rule, and the start goes on with the game's table (a mod that says
`"boot": "required"` stops it). The change holds until the process ends: a rule changed in a config applies at the next
start, where it is applied again.

**Reading it back: `Build.BaseLimits`** (`[TickThreadOnly]`, server side as the rest of `Build`; capability
`build.base_limits`; null before the world runs) is a `BaseLimits`:

| Member | |
|---|---|
| `FlameLevel`, `CurrentAltars` | the world's Flame level and its altar count now (its progress entity) |
| `OriginalCount`, `EffectiveCount` | the game's own count and the count the game runs with, at `FlameLevel` |
| `OriginalCounts`, `EffectiveCounts` | the same at every Flame level (index = level, from 0) |
| `OriginalSizes`, `EffectiveSizes` | `AltarSize` per altar level 1 to 5: full sizes in metres; `PlayerBase.Half` of an altar of that level is half of it |
| `AppliedRules` | the rules in this start's table, as `mod:name`, in load order |
| `PatchedBy` | mods whose own resource patches of the table are in it |

"Original" is the game's own table, read before any boot patch of this start; "effective" is the table the game runs
with (as kiln.assets serves it). On a game that joins a server, the server's numbers decide what may be placed; the
player's own game draws zones and counts altars by its own table, which has the rules only when it runs the mod too
(so a mod with base rules says `"side": "both"`, `"clients": "mod"`). `BaseLimits` reads the server's.

| Capability | Kind | Says |
|---|---|---|
| `build.base_rules` | native (kiln.content), static | base rules can be staged and are written in this process: as `mods.boot.resource_patches` (`policy` without a boot phase, `service-missing` without kiln.assets' resources) |
| `build.base_limits` | managed (Kiln.Build), static, reported at Kiln.Build's first use | `Build.BaseLimits` can read: kiln.base_rules is here (kiln.content of 0.2.0-dev.31 or later) and kiln.world runs (`service-missing` otherwise) |

Native: `kiln.base_rules` v1 (`native/core/abi/services/kiln_base_rules.h`, published by kiln-content.dll) answers
the same as JSON (`state`, tick thread). Tests: `native/services/content/tests/baserules_test.sh` (host: More Flame
Altars' exact patch and two mods' rules in every load order, no refusal, the largest result; bounds; the read side),
`managed/tests/Kiln.Blocks.Tests/BaseRulesTests.cs` (staging, JSON, lowering, the reader), lab
`mods/tests/base-rules-test` (dedicated server, two starts). Not covered: changing the rules while the game runs, more
than 16 Flame Altars, per-base sizes, merging bases; a player's build zone on screen is a guided client check.


### Base rules and the registry

Base rules are an earlier writer of the balancing table, as resource patches are: they are written into the table before
the registry stage, and the registry stage composes on their result (ruling D1). A registry edit of
`balance.altarsPerFlameLevel` or `balance.buildzoneSizesPerAltarLevel` therefore wins over a base rule. The registry
report names the rule as the writer below for the counts (`composedOn` or `overridden`, writer `<mod>/baseRules/<name>`);
for the sizes the registry stage still composes on the rules' values, but names no writer below it (it models the members
of the size list only from its own edits). `Build.BaseLimits` reports the values the
game runs with, after the registry, and `BaseLimits.RegistryChanged` lists every value a registry edit changed after a rule
had raised it (`"altarsPerFlameLevel[1]: 8 -> 2 (rules-a:roomy)"`).

## The `.kschem` format (version 1)
Little-endian binary:

| Offset | Size | Content |
|---|---|---|
| 0 | 8 | magic `KSCHEM\r\n` (`4B 53 43 48 45 4D 0D 0A`) |
| 8 | 4 | u32 version = 1 |
| 12 | 4 | u32 header length H |
| 16 | H | header, UTF-8 JSON (below) |
| 16+H | 4 | u32 cell data length C |
| 20+H | C | raw DEFLATE (RFC 1951) of `size.x * size.y * size.z` u16 cells, x fastest, then y, then z; each cell `material | fill << 8` (the engine's format) |

```json
{
  "format": "kiln.schematic", "version": 1,
  "name": "hut", "author": "build-test", "description": "",
  "size": [19, 14, 19],                 // cells
  "cellSize": 0.5,                      // metres (the grid it was captured on)
  "anchor": [9, 3, 9],                  // the paste point, in cells from the low corner (default: bottom centre)
  "cellOrder": "x,y,z", "cellEncoding": "u16le material|fill<<8, deflate",
  "props": [ { "template": "65baa890-022a-454b-9cb7-b92cb8c06cd4", "name": "Prop_Decoration_T1_Storage_24",
               "pos": [1.5, 2.6, 5.5],   // metres from the low corner (float)
               "rot": [0, 0.7071, 0, 0.7071],   // quaternion x y z w
               "item": 213939880 } ],    // UsedItem: 0 = the item that places the template, 4294967295 = none
  "source": { "world": "11111111-…", "box": [6003, 1802, 3087, 19, 14, 19], "corner": [3001.5, 901, 1543.5],
              "kiln": "0.2.0-dev", "captured": "2026-11-04T16:35:10Z" }   // optional
}
```
- **Rotation** (`Schematic.Rotated(k)`, `PasteOptions.Rotation`): k quarter turns about +Y (yaw +90° each). A box of
  `sx x sy x sz` becomes `sz x sy x sx`; cell `(x, y, z)` goes to `(z, y, sx - 1 - x)`; the anchor turns with it; a prop
  offset `(px, py, pz)` goes to `(pz, py, sx * cellSize - px)` and its rotation to `q90 * q`. Building blocks are plain
  cells, so their shapes turn exactly.
- **Paste**: the anchor cell lands on the cell of the target position (`TargetBox(at, rotation, offset)`).
  `PasteMode.Replace` (default) writes every cell, air included (rooms carve themselves into terrain);
  `PasteMode.Merge` writes only cells with fill > 0. `ClearProps` removes building props in the box first.
- Capture keeps building props (`PropFilter.Placed`) unless asked for more, never a flame altar.
- `Schematic.Hash`: SHA-256 over the size, the cells and the props (what PlaceOnce records).

## Commands (`BuildCommands.Register()`, admin only)
`/build` belongs to Kiln. A mod that wants it calls `BuildCommands.Register()` (the Schematics sample does): the first
call adds the command, later calls share it, and it goes when the last registration is disposed or its mod unloads.
The handlers run as Kiln: an edit from the console is journaled under `kiln`, a player's under that player. Schematics
live in `kiln/data/kiln.build/schematics/`.

```
/build [status]                       what works here
/build pos1 [x y z] | pos2 [x y z]    selection corners (your position by default); /build sel shows it
/build capture <name>                 selection -> <name>.kschem
/build paste <name> [0|90|180|270] [x y z]
/build list | undo | history          your journal (per player)
/build dig <sphere|box|cylinder> <size> [x y z]
/build add <sphere|box|cylinder> <size> <material id | block item> [x y z]
/build flatten <radius> [level] | smooth <radius> | paint <radius> <material>
/build prop <template> [yaw] [x y z]
/build bases | release                player bases; stop keeping Kiln's terrain in the selection
```
Admin edits by hand may touch bases (`InBases` is set: the admin asked for it by position). Permission node
`kiln.command.build`, level Admin.

## Admin ops (for Kiln.Admin)
| Op | Maps to |
|---|---|
| `build_status` | `Build.Status` + `Grid` |
| `build_bases` | `Build.Bases` |
| `build_read {x,y,z,sx,sy,sz}` | `Build.ReadAsync(new CellBox(...))` → base64 of the u16 cells (≤ 64k cells) |
| `build_shape {edit, shape, x,y,z, hx,hy,hz, material?, level?, flags?}` | `Build.Dig/Add/Flatten/Smooth/Paint/Replace` with `InBases` only when asked |
| `build_props {min, max, filter}` / `build_prop_place {template, x,y,z, yaw, item?}` / `build_prop_remove {entity}` | `Build.Props` / `PlaceProp` / `RemoveProp` |
| `build_schematics` | the files in `kiln/data/kiln.build/schematics/` (name, size, props) |
| `build_capture {name, min, max}` / `build_paste {name, x,y,z, rotation, mode}` | `Schematic.CaptureAsync(...).Save`, `Schematic.Load(...).Paste` |
| `build_undo {owner?}` / `build_history {owner?}` | `Build.Undo` / `Build.History` (owner `"admin"` or a player's) |
| `build_regions` / `build_release {box}` | `Status.KeptChunks` / `Build.Release` |
| `build_placeonce_list` / `build_placeonce_forget {mod, key}` | `Build.PlacedOnce` / `ForgetPlaced`; Kiln.Admin can only reach its own records today (other mod ids are refused) until Kiln.Build gets an admin-level `PlacedOnceOf(modId)` / `ForgetPlaced(modId, key)` |

## C table (`kiln_build.h`)
`status`, `grid`, `ready` (asks for missing tiles), `read`, `write` (`KilnBuildWrite`: box, cells, optional mask,
flags, actor), `shape_box`, `shape` (`KilnBuildShape`: op, shape, centre, half, level, strength, passes, material),
`prop_place` (`KilnBuildProp`), `prop_remove`, `props_in_box`, `prop_info`, `bases`, `item_material`, `item_template`,
`keep`, `release`, `terrain_budget_reserve`, `terrain_budget_charge`, and from task threads `grid_for_session`
([below](#voxels-from-tasks-since-020-dev303)). Results in `KilnBuildResult` (`R_CHANGED`, `R_VERIFIED`, `R_KEPT`, `R_ZONE`, `R_BASE`,
`R_NOT_LOADED`, `R_DRY_RUN`). Tick thread except `status`. Lab-only debug channel: create `kiln/data/kiln.build/debug`
before boot, then write commands (`info`, `column x z`, `dump`, `surface`, `tiles`, `load`, `bases`, `props`, `item`,
`shape`, `sing`, `vw`) to `kiln/data/kiln.build/cmd` (see `native/services/build/debug.cpp`).

## Voxels from tasks (since 0.2.0-dev.30.3)

Kiln's own task threads (`kiln.tasks`, and the interpreter that runs mods in the ShroudForge format in kiln mode) can
read the voxel grid's geometry without waiting for the server tick: `grid_for_session`, appended to the table at offset
152 (the table is 160 bytes with it; the version stays 1, so check `header.size`). It is **experimental**: its
kiln.caps row is `build.voxel_grid_tasks`.

```c
KilnBuildGridSpec spec = { sizeof spec };
uint64_t session = world->game_world_session(KILN_WORLD_SERVER);
KilnResult rc = build->grid_for_session(owner, session, (KilnStr){ "voxel", 5 }, &spec);  /* owner: your plugin's */
/* KILN_OK: spec.origin (metres of cell (0, 0, 0)), spec.cell_size (0.5), spec.maximum (cells per axis) */
```

- **What it reads.** The server world's voxel grid (a dedicated server, or a game client while it hosts): its id
  (`"voxel"`, the only grid), the origin of cell (0, 0, 0), the cell size per axis and the extent in cells per axis
  (`[0, maximum)`: x and z the streaming grid, 20480 cells on the current builds; y 2^31 - 8, since the engine has no y
  bound). `KilnBuildGrid`'s 8192 cells of height is Kiln's own limit for the tick members, not the engine's.
- **Threads.** Any thread but a world's own. Kiln takes the engine's own write gate without waiting (between frames;
  every frame holds it), copies the grid's fields twice and compares them, releases the gate and checks the session
  again. It calls no game code, allocates nothing and never posts work to a tick. On the server tick it answers
  `KILN_ECS_ERR_BUSY` at once: use `grid` there.
- **The caller.** The first argument is the caller's own `KilnOwner` (the one its plugin got): a busy refusal from
  the engine's gate holds back only that owner for the rest of the server step, never other mods.
- **The session.** The caller names the server world session it expects: kiln.world's
  `game_world_session(KILN_WORLD_SERVER)`. Another session is `KILN_ECS_ERR_STALE`, and so is a session that ends
  during the call.
- **Results.** `KILN_OK`; `KILN_ERR_INVALID_ARGUMENT` (a NULL or short record, owner 0, session 0, an empty id, a NUL
  in it, or over 15 bytes); `KILN_ERR_NOT_FOUND` (another id); `KILN_ERR_UNAVAILABLE` (off on this build, no server world in this
  process, a client joined to another machine's world, or the server world is stopping); `KILN_ECS_ERR_STALE`;
  `KILN_ECS_ERR_BUSY` (a frame holds the gate, another Kiln thread does, this owner's busy refusal earlier in the same
  server step, a call on the server tick, or the grid is not ready yet: the world is starting, or it moved between the
  two copies; try again at a later step); `KILN_ECS_ERR_UNREADABLE` (a copy faulted). The record is written on
  `KILN_OK` only.
- **On this build.** `build.voxel_grid_tasks` is available when kiln.build's voxel reads are (`build.read`), the
  running game matches one of Kiln's exact engine profiles (dedicated server 26430253, client 26430625) with its write
  gate, and kiln.world's `game_world_session` is there; its text names the profile. Otherwise it is unavailable,
  `symbol-missing`, with the check that failed, and the member answers `KILN_ERR_UNAVAILABLE`.
- **Not yet.** Reading and writing cells from task threads are not in this release: use `read` and `write` on the
  tick. Nothing here keeps Kiln save regions.

## Lab results (lab `build`, a copied world, no player)
`build-test: done, 79 PASS, 0 FAIL over all phases` (four boots):
```
PASS read back after write: 64 cells, 1 blocks, verified True, kept True, 0.01 ms
PASS add box (material 2): 1473 cells / dig box (the hut's room): 500 cells / paint the roof (material 66): 144 cells
PASS Rough Wood Block places voxel material 143 / a roof beam of Rough Wood Block cells: 124 cells
PASS place prop: 2542 Prop_Decoration_T1_Storage_24 item 213939880 at (3004.5, 902.6, 1549.5) flags Placed, Saved
PASS a write inside the base without InBases is refused: Base (altar 1025)
PASS dig for player 5 in a Protect zone with RespectZones: Zone (zone 1, ours 1)
PASS capture -> .kschem -> load: 19x14x19 cells (1846 solid), 2 props, 2212 bytes, hash 6d1456e3a74c
PASS schematic round trip: capture -> paste elsewhere turned 90 degrees -> read equal (2312 cells, 2 props, verified True)
PASS undo of the paste: 2312 cells back, 2 props removed, B reads as before
PASS PlaceOnce placed it: 2270 cells, 2 props / PlaceOnce again: already placed
PASS survives SIGINT save + restart: box A reads 77e5e87de5abcd64 (recorded 77e5e87de5abcd64)   (A, B, C)
PASS props after SIGINT restart: 6 of 6 props back / PlaceOnce after the restart: already placed
PASS an edit saved before kill -9 survives it: box E reads a3ebf055f8d47152 (recorded a3ebf055f8d47152)
PASS an edit made after the last save is lost by kill -9 (D reads 86319aa8e71a3860, its original 86319aa8e71a3860)
PASS restore box A: 1424 cells back to the original … / after the cleanup and a restart box A reads its original
```
`reload-test schematics 3`: `3 reloads, 0 failures, avg 16.01 ms, max 22.37 ms, 0 leaked context(s)`. Idle cost: step
p50 1.09 ms, Kiln Pre p50 0.05 ms (other labs 0.82-0.98 ms step); the save-region hook caches its boxes.

Driver: `tools/lab/lab.sh new build 26070 --save <savegame dir>`, deploy with `--policy any`, start;
after `build-test: READY stop` stop with SIGINT and start; after `READY kill` `kill -9` the server and start; after
`READY stop` again SIGINT and start. Delete `kiln/data/build-test/` to start over.

## Client checklist (needs a joined player)
1. A vanilla client sees Kiln's persisted structures (terrain cells, building-material cells, props) after joining.
2. Live edits (shape, paste, prop) appear without relog; undo removes them again.
3. Building-material cells (128..255) look like the block items' materials; Kiln's material-only air cells next to new
   surfaces texture them as the game's own would (no grey/untextured faces).
4. A Kiln-placed prop is interactable (chest opens for a group with inventory access) and dismantles into its item
   (UsedItem) inside a base.
5. Players' own digging inside a Kiln region outside bases is kept across a restart (expected by design).
6. Teleport to an unloaded area right after a Kiln edit there: no stale terrain on the client.

Seen in a vanilla client (build 1285601) joined to a lab as Visitor: 1 yes (huts A/B/C from earlier
boots, the Kiln chest inside A; the game reports "Sheltered" inside a Kiln hut), 2 yes (live outpost paste, pillar,
pit, beam, chest; undo removed beam and chest), 3 yes (outpost shows the game's log walls with posts, thatch roof
with trim, stone foundation; the client collides with the walls), 6 yes (stone cube written while the player was at
the base 780 m away, visible at once after the teleport, gone after undo). 4 and 5 not testable as Visitor
(`canAccessInventories`/`canEditWorld` false: no prompt at the chest); they need a Friend or Admin login.

## Limits
- An edit outside every base shows as an extra Flame Altar in the world list (one per box) as long as Kiln keeps it;
  `Build.Release(box)` drops it. Edits inside bases add none, except in the 16 m chunks along a base's edge that reach
  outside it.
- If the game's region list cannot take all of Kiln's boxes in a save, that save keeps the game's own regions and
  only the boxes that fit (when its growth fails), or none of Kiln's (when appending faults), and Kiln logs it: the edits
  left out are back in the next save that succeeds, but a restart right after such a save loses them. Failing the save
  instead could stop the world from saving at all.
- Kiln tells the save-region builder's three callers apart by their call sites, which sigdb finds on each game build
  (`build/RetSaveRegionsWorldSave`, `build/RetSaveRegionsBaseZones`, `build/RetSaveRegionsBaseQuery`). On a build
  where the world save's call site is not found, Kiln's boxes go into every caller's list, as up to 0.2.0-dev.30.8, so
  that no edit goes unsaved: each box is then also a base zone the game's systems and clients use, and past 16 zones
  the game's base areas go wrong. Where only the base-zone or base-query call site is not found, the world save alone
  still gets them. Either way kiln.log has a warning when kiln.build starts, naming the missing names and what Kiln
  does instead, and so does the text of the kiln.caps row `build.persist_callers`: unavailable (`symbol-missing`)
  when every caller gets the boxes, available otherwise. Another mod's hook in front of Kiln's changes the return
  address Kiln sees; Kiln then looks at up to 8 return addresses below it for a known call site. A call it still cannot
  place gets the boxes, as it may be the world save, when another module's code is on the stack or the stack cannot be
  walked (kiln.log names the module once); only a call whose frames are all the game's own gets none. Windows' (or
  Wine's) own modules and the C runtime's, where every thread starts, are not another module: the look ends there.
- The game's voxel writer keeps at most 4,096 stored 16 m chunks per save record and skips the rest, scattered over
  the whole record ([RE notes](../../re/build.md) §12: the writer server `0x83d200` with its 4,096-key array set up at
  `0x83d2f8`, client `0x10b89b0` and `0x10b8aa8`). A base whose zone touches more than 4,096 chunk positions (a side
  over 240 m, which only a mod that enlarges zones makes, or exactly 240 m with a face on the 16 m grid) loses some of
  its terrain at every save, players' own building included; Kiln does not change that. Since 0.2.0-dev.30.9 Kiln's kept chunks inside such a base get boxes of their own (each box
  one more Flame Altar), and no Kiln box covers more than 4,096 chunks: a larger one is cut into several.
- Hosted worlds on a game client: the definitions resolve offline on client build 26404966 (one region file per world
  GUID); not run in a hosted world yet.
- Building blocks are written as cells; the game's own blueprint path (item shape + inventory) is not called.
- At most 4096 cells per axis and 4 M cells per call; schematics up to 4 M cells.
- Large areas evict other voxel tiles from the server's cache (the game reloads what it needs near players).

## The contract (dev.27)

Kiln.Build follows [the API contract](../modding/contract.md). Server side only (`[ServerOnly]` on `Build`).

- **Capabilities.** `kiln.build` and its sigdb features `build.read`, `build.write`, `build.persist`, `build.items`,
  `build.zones` (lowered by Kiln.Build when the service is not loaded). Every member that needs the service carries
  `[RequiresCapability("kiln.build")]` and throws `KilnUnavailableException` naming it (a `NotSupportedException`, as
  before).
- **Results.** `BuildResult`, `PasteResult` and `PlaceOnceResult` implement `IKilnResult`. `BuildResult.Ok` is
  `!Refused` and `Reason` puts `Refusal` in words (the zone or base altar id included; `Changed` since dev.28); `PasteResult.Reason` is the
  cells' refusal or the count of props that could not be placed; `PlaceOnceResult.Ok` is `Placed || AlreadyPlaced`.
  `UndoResult` stays a count (an undo with nothing to undo is null, as before).
- **Threads.** `[TickThreadOnly]` on everything that touches the world; `Build.Status` and `Available` are
  `[AnyThread]`.
- **Events.** Every edit already posts `BuildEditingEvent` (cancelable) and `BuildEditEvent`.
