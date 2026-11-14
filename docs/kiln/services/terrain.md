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
- `Persistence`: `Kept` (saved with the world, recorded in the ledger) or `Transient` (not saved, not recorded,
  gone at the next restart: for ground that is meant to reset; it may be applied again whenever the mod asks).

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
| Cells per tick (`TerrainApplyOptions.CellsPerTick`) | default 32,768; 512 to 262,144 | one batch a tick; measured in the lab test |
| Batch | a box of whole 8-cell blocks, at most `CellsPerTick` cells, edge 8, 16, 32 or 64 | a saved edit is an 8x8x8 block |

Batches run in a fixed order (z, then y, then x), one per tick. The same patch gives the same batches and the same
cells on every machine (`AddOnTop` depends on the ground it finds).

### Apply

```csharp
[ServerOnly, TickThreadOnly, RequiresCapability("terrain.overlays")]
public static Task<TerrainResult> Apply(TerrainPatch patch, TerrainApplyOptions? options = null);
```

It waits for the world to run, then for each batch: asks for the tiles, reads the cells, writes through
`Build.WriteAsync`, reads back, records the batch in the ledger and raises `Terrain.Changed`. It is **not atomic**:
a patch is spread over ticks, and a cancel or a failure leaves the batches already written in the world. The result
says how many.

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
id: version, region, a hash of the patch's content, state (`applying` or `applied`), and for **each 8x8x8 block the
patch addresses** two fingerprints: the addressed cells before the write and after it (SHA-256 of the cell values,
first 8 bytes). A block is on the world's 8-cell grid; "addressed" are the block's cells inside the region whose
palette number is not 0 (for a heightfield: every cell of the region). The record and the cells reach the disk with
the same world save, as `Build.PlaceOnce`'s do.

| Situation | What happens |
|---|---|
| No record | every block is written; "before" and "after" are recorded |
| Same id, same version, same content, state `applied` | `AlreadyApplied` |
| Same id and version, other content | `Refused`: "content changed without a new version" |
| Lower version than recorded | `Refused`: a downgrade is never applied |
| Higher version | per block of the old record: the cells still equal its "after" → the block is this plan's and is written; anything else → the block is listed as `PlayerEdit` and left alone, and the plan gives up that block (later versions do not touch it either). Blocks the old version did not address are written as new. The record moves to the new version; the result is `Conflict` when any block was left |
| Record in state `applying` (a save fell between batches, then a crash) | resumed: a block that equals "after" is done, one that equals "before" is written, anything else is a conflict |
| A block is addressed by a kept plan of **another mod** known in this session | `Refused`, naming that mod and plan; nothing is written |
| A block lies in a player base and `InBases` is off | that block is listed as `BaseZone` and left alone |

`TerrainConflict(TerrainConflictKind Kind, CellBox Block, string? Owner)`; kinds `PlayerEdit`, `OtherPlan`,
`BaseZone`.

Rules that follow from the engine:
- **Removing a plan does not restore the ground.** `Terrain.Forget(id)` drops the record (the plan no longer owns
  its blocks, and an `Apply` of the same id starts fresh over whatever is there); the cells stay. Nothing can delete
  a saved block, so the terrain under an overlay never comes back by itself.
- **A mod that is gone keeps its ground.** Its cells stay in the world. Its ledger is in its own world data, which
  no other mod can read: another mod's plan over the same blocks is refused only while the owning mod is loaded and
  has used `Kiln.Terrain` in this session. (A ledger shared by all mods needs a world bundle of Kiln's own in
  Kiln.Saves: about half a day, not in this version.)
- A fresh plan does not know whether a player dug there before it: it writes. Only upgrades are guarded.
- `Terrain.Applied` lists the calling mod's records in this world.

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
Every batch also raises kiln.build's own `BuildEditingEvent` and `BuildEditEvent` (origin `terrain`); a handler that
cancels the first stops the patch as `Partial`. Terraforming is not part of the cancelable gameplay events.

### Capability rows

| Row | On when |
|---|---|
| `kiln.terrain` | the library is loaded |
| `terrain.overlays` | kiln.build reads, writes and keeps, and kiln.saves is on; server side. On a client that hosts a world it is held until the game test `19f-overlay-client` |
| `terrain.baked` | off (baked regions are not built) |
| `terrain.biomes` | off |
