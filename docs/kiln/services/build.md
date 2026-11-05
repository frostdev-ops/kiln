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
  restart. Kiln remembers the 16 m chunks its own writes touch and adds them to the region list the game's save
  writes, so Kiln's edits are saved like a base (`kiln/data/kiln.build/regions/<world GUID>.json`). Inside a Kiln
  region the players' own digging is kept too. `BuildFlags.Transient` skips that; `Build.Release(box)` forgets regions
  (the terrain there reverts at the next restart).
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
| `await Dig / Add / Flatten / Smooth / Paint / Replace(shape, …)`, `ShapeBox(op, shape)` | `BuildShape.Box/Sphere/Cylinder` in metres; a one-cell smooth edge like the game's brushes |
| `PlaceProp(template|guid, pos, rotation, itemId)`, `RemoveProp(e)`, `Prop(e)`, `Props(min, max | box, filter)` | `PropFilter.Placed` (default: what players placed), `Saved`, `All`, `Static` |
| `Bases` | `PlayerBase` (altar, build-zone centre and half size) |
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
| `BuildEditEvent` | after a Kiln edit (also when the world refused it: zone, base, not loaded) | tick, inside the call | no | `Kind`, `Box`, `Actor`, `Origin`, `Label`, `Result`, `PropsPlaced`, `PropsRemoved`, `JournalId`, `Changed` |

Player edits with the build hammer are Kiln.Gameplay's `BuildEvent`s, not these.

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
The Schematics sample registers them; schematics live in `kiln/data/kiln.build/schematics/`.

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
`<mod>.command.build`, level Admin.

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
`keep`, `release`. Results in `KilnBuildResult` (`R_CHANGED`, `R_VERIFIED`, `R_KEPT`, `R_ZONE`, `R_BASE`,
`R_NOT_LOADED`, `R_DRY_RUN`). Tick thread except `status`. Lab-only debug channel: create `kiln/data/kiln.build/debug`
before boot, then write commands (`info`, `column x z`, `dump`, `surface`, `tiles`, `load`, `bases`, `props`, `item`,
`shape`, `sing`, `vw`) to `kiln/data/kiln.build/cmd` (see `native/services/build/debug.cpp`).

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
- Hosted worlds on a game client: the definitions resolve offline on client build 26404966 (one region file per world
  GUID); not run in a hosted world yet.
- Building blocks are written as cells; the game's own blueprint path (item shape + inventory) is not called.
- At most 4096 cells per axis and 4 M cells per call; schematics up to 4 M cells.
- Large areas evict other voxel tiles from the server's cache (the game reloads what it needs near players).
