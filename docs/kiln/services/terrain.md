# Kiln.Terrain: bounded terrain overlays

A mod declares a bounded patch of ground (an ore vein, a mound, a tunnel, a small dungeon shell) and Kiln applies it
**once per world**, over several ticks, remembers who owns it and at which version, and refuses to write over what
players changed afterwards. The cells are ordinary world edits made through [kiln.build](build.md): the game saves
them and sends them to every client like a player's own digging, so vanilla clients follow.

| Layer | File |
|---|---|
| Managed | `managed/services/Kiln.Terrain/` → `kiln/host/Kiln.Terrain.dll` (`Terrain`, `TerrainPatch`, events) |
| Native | none: the library calls `Kiln.Build` and `Kiln.Saves` and adds no engine entry |
| Sample | `mods/samples/OreVein` |
| Test | `mods/tests/terrain-overlay-test` (three boots), `managed/tests/Kiln.Terrain.Tests` |
| Evidence | `docs/re/build.md` |

## Using it

```csharp
// a 3 m mound, added on top of the ground, once per world
var heights = new float[16 * 16];
for (var z = 0; z < 16; z++)
for (var x = 0; x < 16; x++)
    heights[z * 16 + x] = MathF.Max(0, 6f - MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (z - 7.5f) * (z - 7.5f)));
var corner = Build.CellOf(position);
var mound = new TerrainPatch("mound", 1, new CellBox(corner.X, corner.Y, corner.Z, 16, 6, 16),
    new TerrainHeightfield(heights, Fill: 1, Mode: TerrainHeightMode.AddOnTop), [new TerrainPaletteEntry(1)]);

var report = await Terrain.Inspect(mound);          // what would happen; writes nothing
var result = await Terrain.Apply(mound);            // one batch a tick
if (!result.Ok) Log.Warn($"mound: {result.State}, {result.Reason}");
```

Call it from mod code on the tick thread, on the server side (a dedicated server, or the world a player hosts once
its game test has passed). `Apply` waits for the world to run, so a mod may call it from `OnLoad`. The sample
`mods/samples/OreVein` does both shapes: a heightfield from C# and a voxel volume from `vein.json`.

## What it cannot do
- **Take ground away again.** `Forget` and removing the mod leave the cells. A plan that should be reversible keeps
  what it replaced itself (`Build.Read` before, `Build.Write` after), or uses `Transient` where no other edit keeps the chunk (see below).
- **A mod's own materials.** A palette names the game's materials only (1 to 116, 128 to 212). Materials a mod adds
  with Kiln.Blocks get their cell number per world (117 to 127 for terrain, 213 and up for building blocks) and a
  player without the mod has no look for them, so an overlay refuses those numbers with `ArgumentException`. Such
  cells can still be written with `Kiln.Build` directly by a mod that requires itself on clients.
- **See another mod's plan when that mod is not loaded.** The ledger is the owning mod's world data (see "The
  ledger"). Two mods that are both loaded never write one block; a mod added later can write over the ground of a
  mod that was removed.
- **Know about edits before its first apply.** A fresh plan writes over whatever is in its region (outside bases),
  unless the ground already holds its result (see "When the ledger is gone").
- **Recognise every lost ledger.** When the world's Kiln data is lost while the ground stays, a plan is refused where
  its ground still shows it; a plan changed everywhere since, or a new version that differs everywhere, is not
  recognised (see "When the ledger is gone").
- **Large landscapes.** A region is at most 128 m across and 1,048,576 cells; every block it touches is saved in
  full (1 KiB before packing) and sent to every player near it like any edit. Baked terrain (replacing the shipped
  tiles before the world loads) is not built: `terrain.baked` is off.
- Signed-distance sources, a blend of a heightfield with the ground, props and encounters: not in this version.

## Game tests
| Check | What it settles | State |
|---|---|---|
| `19f-overlay-client` | an overlay on a world a player hosts, and as a joined player sees it: appears, collides, digs, survives a reload | open; holds `terrain.overlays` on clients |
| `19f-terrain-client`, `19f-derived-companions` | a cooked terrain tile on a real client (the baked route's qualification) | open; no fixture in the client pass yet |
| `19f-base-exposure`, `19f-outer-tile` | removing a saved block; ground outside the shipped extent | deferred: no qualified primitive |

## Contract

This section is the contract of the overlay API. The engine facts behind it: a saved edit is a whole 8x8x8 block of
cells, an edited block hides the base terrain under it for good, and nothing can delete a saved block again.

### What a patch is

```csharp
public sealed record TerrainPatch(string Id, int Version, CellBox Region, TerrainSource Source,
    IReadOnlyList<TerrainPaletteEntry> Palette, TerrainPersistence Persistence = TerrainPersistence.Kept);
```

- `Id`: the plan's name inside the mod: 1 to 64 characters of `a-z`, `0-9`, `.`, `_`, `-`. `Version`: 1 or higher.
- `Region`: the box of cells the patch may touch (cells are 0.5 m; `Build.CellOf` converts a position).
- `Palette`: what the source's numbers mean. Entry `n` of the list is palette number `n + 1`; **number 0 always
  means "leave this cell as it is"**.
  `TerrainPaletteEntry(byte Material, byte Fill = 255)`: `Material` 0 is air (the cell is dug out, `Fill` must be 0
  or 255 and is ignored); 1 to 116 is a terrain material of the game; 128 to 212 a building-block material. 117 to
  127 and 213 to 255 are refused: a vanilla client has no look for them. `Fill` 1 to 255 for matter (255 solid).
- `Source`, one of:
  - `TerrainVoxelVolume(byte[] Cells)`: one palette number per cell of `Region`, x fastest, then y, then z (the order
    of `CellBox.Index`). Length must equal `Region.Count`.
  - `TerrainHeightfield(float[] Heights, byte Fill, TerrainHeightMode Mode = Replace, byte Surface = 0,
    int SurfaceDepth = 0)`: one height per column of `Region` (x fastest, then z; length `SizeX * SizeZ`), in cells
    above the region's floor, 0 to `SizeY`. `Fill` and `Surface` are palette numbers (`Surface` 0 = none); the top
    `SurfaceDepth` cells of a column use `Surface`. A fractional height fills the top cell partly.
    `Replace`: below the height the column becomes matter, above it air, up to the region's top.
    `AddOnTop`: only cells that hold less matter than the patch wants are written; nothing is dug.
  - A signed-distance source is not declared in this version.
- `Persistence`: `Kept` (saved with the world, recorded in the ledger) or `Transient` (no ledger record and no
  additional save region; it may be applied again whenever the mod asks). **Transient does not guarantee a reset:**
  a player base or any kept edit in the same 32-cell store chunk can still cause those cells to be saved.

The same shape in JSON (`TerrainPatch.FromJson`, `ToJson`); a byte array is base64:

```json
{ "id": "copper-vein", "version": 1,
  "region": { "x": 6040, "y": 1800, "z": 3100, "sizeX": 16, "sizeY": 8, "sizeZ": 16 },
  "palette": [ { "material": 12 }, { "material": 0 } ],
  "source": { "kind": "voxelVolume", "cells": "<base64>" },
  "persistence": "kept" }
```

`"source": { "kind": "heightfield", "heights": [..], "fill": 1, "mode": "replace", "surface": 2, "surfaceDepth": 1 }`.
An unknown `kind`, an unknown property or a wrong length is an `ArgumentException`.

### Limits

| Limit | Value | Why |
|---|---|---|
| Region, per axis | 256 cells (128 m) | "bounded": one tile across at most |
| Region, cells | 1,048,576 | a quarter of kiln.build's largest single write; bounds the ledger |
| Region, position | inside the voxel grid, Y from 0 to 8191 | reads outside the grid report "ready" and return air |
| Palette | 1 to 255 entries | a cell's number is a byte |
| Kept plans per mod and world | 64 | bounds the mod's world data |
| Cells per tick (`TerrainApplyOptions.CellsPerTick`) | default 32,768; 512 to 262,144 | one batch a tick; a full default batch took 42 ms on a lab server with no player (one measurement, 2026-11-13) |
| Batch | a box of whole 8-cell blocks, at most `CellsPerTick` cells, edge 8, 16, 32 or 64 | a saved edit is an 8x8x8 block |

Batches run in a fixed order (z, then y, then x), one per tick. The same patch gives the same batches and the same
cells on every machine (`AddOnTop` depends on the ground it finds).

### Apply

```csharp
[ServerOnly, TickThreadOnly, RequiresCapability("terrain.overlays")]
public static Task<TerrainResult> Apply(TerrainPatch patch, TerrainApplyOptions? options = null);
```

It waits for the world to run, then for each batch: asks for the tiles, reads the cells, records the intended
fingerprints, loads and writes through `Kiln.Build`'s guarded write (`Build.WriteIfUnchanged`: the cells it writes must
still be what the batch read, compared after the `BuildEditingEvent` handlers and right before the commit), checks
readback availability and raises `Terrain.Changed`. It is **not atomic**:
a patch is spread over ticks, and a cancel or a failure leaves the batches already written in the world. The result
says how many. A world stop or change ends the apply; a mod reload releases its running reservation so an explicit
apply from the new instance can resume the stored record.

`TerrainResult(bool Ok, string? Reason, TerrainState State, string Plan, int Version, long ChangedCells,
int Batches, IReadOnlyList<TerrainConflict> Conflicts)`:

| State | Ok | Meaning |
|---|---|---|
| `Completed` | yes | every batch is written |
| `AlreadyApplied` | yes | this world has this plan at this version: nothing was read or written |
| `Conflict` | no | written except in the blocks listed in `Conflicts`; those were left alone |
| `Partial` | no | stopped (canceled, tiles did not load, a write was refused): `Batches` are in the world |
| `Refused` | no | nothing was written (`Reason` says why) |

`TerrainApplyOptions`: `CellsPerTick`, `InBases` (default false: a block inside a player base's build zone is not
written and is listed as a conflict), `LoadTimeout` (default 20 s per batch), `Cancel` (checked between batches),
`Progress` (`IProgress<TerrainProgress>`: batches and cells so far; progress is not a change event).

A bad patch (empty, too big, outside the grid, a height that is not a number, a palette number with no entry, a bad
id) throws `ArgumentException` before anything is read. A capability that is off throws `KilnUnavailableException`
naming it. Anything the game or a rule refuses is `Ok = false` with a reason.

### The ledger: ownership, versions and conflicts

For a `Kept` patch the calling mod's world data (`WorldData`, key `kiln.terrain.ledger`) holds one record per plan
id: version, region, a hash of the patch's content, whether the apply is complete, and for **each 8x8x8 block the
patch addresses** two fingerprints: the block's cells inside the region before the write and after it (SHA-256 of
the cell values, first 8 bytes). A block is on the world's 8-cell grid. A patch addresses a cell when its palette
number is not 0 (a heightfield: every cell of the region; added on top: the cells below the height); a block with
no addressed cell is not the plan's. The fingerprint covers all of the block's cells inside the region, so an edit
next to a vein, in the same block and inside the region, also counts as "changed": the rule errs on the side of
the player. Fingerprints for an attempted write are recorded before calling Build, using the intended result;
post-write callbacks cannot become part of the plan's recorded result. WorldData normally stages the ledger with the
world save, as `Build.PlaceOnce` does. An older sidecar generation is refused. A missing ledger is detected where the
ground still shows the plan (see "When the ledger is gone"), not everywhere: keep backups of both the world and its Kiln
world-data directory.

| Situation | What happens |
|---|---|
| No record, and no block shows an earlier apply | every block is written; "before" and "after" are recorded |
| No record, but a block already holds this plan's result and shows its shape (the ledger was lost, or the plan was forgotten) | `Refused`: "ownership lost: …"; nothing is written or recorded (see "When the ledger is gone") |
| Same id, same version, same content, state `applied` | `AlreadyApplied` |
| Same id and version, other content | `Refused`: "content changed without a new version" |
| Lower version than recorded | `Refused`: a downgrade is never applied |
| Higher version | per block of the old record: the cells still equal its "after" → the block is this plan's and is written; anything else → the block is listed as `PlayerEdit` and left alone, and the plan gives up that block (later versions do not touch it either). Blocks the old version did not address are written as new. The record moves to the new version; the result is `Conflict` when any block was left |
| Record not complete (canceled, stopped, or a save fell between batches before a crash) | resumed: an attempted block that equals "after" is done; any other value is a conflict, including "before" (a player may have restored the original ground). Blocks never attempted may still be written |
| Corrupt or oversized ledger, or an older sidecar generation | refused without replacing stored data; `Applied` returns an empty list and `Forget` returns false, with a warning in the log |
| A block is addressed by a kept plan of **another mod** known in this session (applied, or being applied) | `Refused`, naming that mod and plan; nothing is written |
| The same plan is being applied already | `Refused` |
| A block lies in a player base and `InBases` is off | that block is listed as `BaseZone` and left alone |
| A block's cells change while its batch is being written (another mod's `BuildEditingEvent` handler edits them, or anything between the batch's read and its commit) | kiln.build's guard refuses the write; the batch is then written block by block, and a block whose written cells changed is not written at all: listed as `PlayerEdit` and given up. The batch's other blocks are written. The guard compares the cells the batch writes; a changed cell it does not write (added on top: a cell already full) stays as it is, and the block's recorded "after" then no longer matches, so a later version leaves it alone |

`TerrainConflict(TerrainConflictKind Kind, CellBox Block, string? Owner)`; kinds `PlayerEdit`, `OtherPlan`,
`BaseZone`.

Rules that follow from the engine:
- **Removing a plan does not restore the ground.** `Terrain.Forget(id)` drops the record (the plan no longer owns
  its blocks, and an `Apply` of the same id is a first install again: refused while its ground still shows the plan's
  result, see "When the ledger is gone"); the cells stay. Nothing can delete
  a saved block, so the terrain under an overlay never comes back by itself.
- **A mod that is gone keeps its ground.** Its cells stay in the world. Its ledger is in its own world data, which
  no other mod can read: another mod's plan over the same blocks is refused only while the owning mod is loaded and
  has used `Kiln.Terrain` in this session. (A ledger shared by all mods needs a world bundle of Kiln's own in
  Kiln.Saves: about half a day, not in this version.)
- A fresh plan does not know whether a player dug there before it: it writes, unless the ground already holds its
  result. Guarded are upgrades (by the fingerprints), resumes and edits made while a batch is being written.
- `Terrain.Applied` lists the calling mod's records in this world. `Forget` returns false while that plan is being
  applied, so it cannot discard the receipts of a running write.

### When the ledger is gone

The ledger is in the world's Kiln data (`kiln/data/worlds/<world id>/`), next to the world save, not in it. If that data
is lost or cannot be read, or the mod's ledger key is gone, while the world save keeps the ground, a kept plan has no
record again, and its next `Apply` would be a first install that writes over everything changed there since. Kiln writes
nothing into the game's world file (mods' world data, entity and item data and kiln.build's kept chunks are all files
under `kiln/data`), so no marker survives with the ground: the ground itself is the evidence.

Before a kept plan with no record writes anything, Kiln reads its region and one cell around it and asks each block the
plan addresses two questions:
1. **Does it hold the result already?** A fresh apply would change none of its cells (added on top: no cell holds less
   matter than the plan wants).
2. **Does the plan's shape show in it?** A cell the plan sets to matter holds exactly that and borders a cell the plan
   sets to another value that holds it (matter against air, two palette entries); or else the ground bordering the
   block's matter (cells the plan leaves alone, inside the region or just outside it) differs from that matter on every
   side. Added on top: every column top that holds exactly the plan's value has less matter right above it, and there is
   at least one. Air alone never counts: a tunnel's mouth in open air looks the same before and after.

One block that answers yes to both means the plan was applied here before. `Apply` returns `Refused` ("ownership lost:
the ground already holds plan 'x' in n block(s) (first x,y,z), but …"), writes nothing, records nothing and raises no
event; `Inspect` reports `Would = Refused` with the same reason. Blocks a player changed since are protected the same way,
as long as one block of the plan is untouched. A block that only repeats the ground (air above a heightfield, solid
ground under a mound, the ground's own material) does not count, so a first install over unchanged ground is as before.
If the tiles for the check do not load, the apply stops as `Partial` with nothing written.

What it cannot see:
- A plan of which every block was changed since: nothing is left to recognise, and the apply writes as a first install.
- A new version after the loss: only the result of the version being applied is known. Blocks where the new version
  equals the old one are recognised; a version that differs in every block is a first install.
- A block with no edge of the plan in it: a dig only (all air), or matter that the ground around it continues.
- A plan whose result already equals the ground where its shape shows (heights read from the ground to blend a mound's
  edge, in the ground's material): it is taken for an earlier apply and refused. Keep a patch's region to the blocks it
  changes.
- Another mod's plan with the same cells looks like this plan's.

What an operator can do: restore the world's Kiln data from a backup that matches the world save
(`Saves.RestoreOnNextStart`, or a `kilnctl backup` archive); the ledger is back and the plan behaves as recorded.
Adopting the ground as it stands (recording its cells as the plan's) is not available in this version, and
`Terrain.Forget` does not help: after `Forget`, ground that still holds the plan's result is refused the same way.

### Inspect

```csharp
[ServerOnly, TickThreadOnly, RequiresCapability("kiln.terrain")]
public static Task<TerrainPlanReport> Inspect(TerrainPatch patch, TerrainApplyOptions? options = null);
```

A dry run: validates, loads the tiles, and reports `Box`, `Cells` (addressed), `Blocks`, `Batches`,
`EstimatedBytes` (what the save grows by at most: 1,024 bytes per block before the game's own packing), what
`Apply` would do (`Would`: a `TerrainState`) and the conflicts it would meet. It writes nothing and records nothing.
`Ok = false` when the world is not running or the tiles did not load.

### Events

`Terrain.Changed` (`Action<TerrainChangedEvent>`): once per committed batch, on the tick thread, after the write and
the ledger update. `TerrainChangedEvent(string Mod, string Plan, int Version, CellBox Box, long CellsChanged,
int Batch, int BatchCount, bool Last)`. A refusal, a conflict-only batch and an `AlreadyApplied` raise nothing.
Every batch also raises kiln.build's own `BuildEditingEvent` and `BuildEditEvent` (label `terrain <plan> v<version>`,
origin the mod that applies the patch); a handler that cancels the first stops the patch as `Partial`. A handler that
changes cells the batch writes is not written over (the ledger row above); the batch is then written block by block,
so each of its blocks raises the two events once more. Terraforming is not part of the cancelable gameplay events.

### Capability rows

| Row | On when |
|---|---|
| `kiln.terrain` | the library is loaded |
| `terrain.overlays` | kiln.build reads, writes and keeps, and kiln.saves is on; server side. On a client that hosts a world it is held until the game test `19f-overlay-client` |
| `terrain.baked` | off (baked regions are not built) |
| `terrain.biomes` | off |
