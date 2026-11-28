# Terrain: overlays for mods, and the baked tiles behind them

Enshrouded's ground is **baked**: the world's terrain is a grid of tiles the game ships, 8x8x8-cell blocks of voxels in seven levels
of detail. A mod changes ground in two different ways, and only the first is for mods:

| | What it is | For | Status |
|---|---|---|---|
| **Overlay** (`Terrain.Apply`) | a bounded patch of ground (an ore vein, a mound, a tunnel, a small dungeon shell) written once per world as ordinary building edits | mod authors | on (`terrain.overlays`) |
| **Baked tiles** (`kfc.py terrain`) | read, check and re-write the game's own tile files; a lab qualification pack for one dedicated lab server | Kiln's developers and researchers | tools only (`terrain.baked` is **off**: no mod can replace the shipped tiles) |

The overlay contract is [services/terrain.md](../services/terrain.md); the engine facts about the tiles are
[docs/re/build.md](../../re/build.md) section 9.

## Make it: a mound of ground in a world

A server mod, one call. Kiln applies the patch **once per world**, over several ticks, remembers who owns it and protects it from
being written over again. The cells are ordinary world edits, so the game saves them and sends them to every client like a
player's digging; vanilla clients follow.

1. Make the project ([mdk.md](mdk.md)): `dotnet new kiln-mod -n MoundMod`, with `<KilnServices>Terrain;Build;World;Chat</KilnServices>`.
2. Declare the patch (a 16 x 16 mound, 3 m high, on top of the ground at a spot next to a player) and apply it:

   ```csharp
   public sealed class MoundMod : Mod
   {
       [Command("mound", Description = "raise a mound next to you", Permission = CommandPermission.Admin)]
       private async Task Mound(CommandContext c)
       {
           if (!Build.Available || !Capabilities.Is("terrain.overlays")) throw new CommandException("terrain overlays are off on this server");
           var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
           var heights = new float[16 * 16];
           for (var z = 0; z < 16; z++)
           for (var x = 0; x < 16; x++)
               heights[z * 16 + x] = MathF.Max(0, 6f - MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (z - 7.5f) * (z - 7.5f)));   // cells above the floor
           var corner = Build.CellOf(at);
           var mound = new TerrainPatch("mound", 1, new CellBox(corner.X, corner.Y, corner.Z, 16, 6, 16),
               new TerrainHeightfield(heights, Fill: 1, Mode: TerrainHeightMode.AddOnTop), [new TerrainPaletteEntry(1)]);

           var result = await Terrain.Apply(mound);                 // bounded units across ticks
           c.Reply(result.Ok ? "the mound is up" : $"{result.State}: {result.Reason}");
       }
   }
   ```

3. Build and deploy to a server, join and run `/mound`. The ground rises next to you over a few seconds. Kiln's ledger remembers
   the plan by id and version: the same patch again is `AlreadyApplied` (nothing is read or written), and a changed patch under the
   same version is refused until it has a new one. `Terrain.Inspect(patch)` says what would happen and writes nothing.

The sample [`mods/samples/OreVein`](../../../mods/samples/OreVein) does both shapes: a heightfield from C# and a voxel volume from
JSON (`vein.json`). A patch is a region of at most 128 m across and 1,048,576 cells; a palette names the game's terrain materials
(1 to 116) and building-block materials (128 to 212), never a mod's own; `Persistence` is `Kept` (saved with the world) or
`Transient` (gone after the next restart). The whole contract, the limits and what Kiln cannot do:
[services/terrain.md](../services/terrain.md#contract).

What an overlay **cannot** do: take ground away again once it is saved (`Forget` only drops the ownership), use a mod's own
materials, or describe a large landscape. Those need a baked tile, which no mod has.

## Make it (tools): read a tile and cook a lab pack

For Kiln's developers: the tile codec reads and writes the game's own tiles, and a **lab qualification pack** lets one dedicated lab
server read a tile Kiln wrote. Nothing of it reaches a mod, and the pack's coarser levels and companion data stay stale, so no game
client may join.

1. Look at the world and one tile (reads only, from the installed game or `--game DIR`):

   ```sh
   python3 -I tools/kfc/kfc.py terrain info                 # each part's root: levels, tile grids, bytes
   python3 -I tools/kfc/kfc.py terrain column 6048 3104      # one cell column at world cell (x, z): height, filled cells, hashes
   python3 -I tools/kfc/kfc.py terrain check --sample 8 --hash   # every rule the reader relies on, on a sample of tiles
   ```

   `check --all --canonical` validates every shipped tile (18,964 distinct tiles, 5.59 GB; about 2.5 hours with 3 jobs) and that
   Kiln's writer rebuilds each column byte for byte. It exits 1 on a refused tile.
2. Write a plan: the cells to change at the server's level 6 (`kiln.build`'s cell numbers), and boxes whose cells to compare after:

   ```json
   {
     "format": 1,
     "part": 0,
     "edits": [
       { "kind": "fill", "box": { "x": 6100, "y": 1700, "z": 3280, "sizeX": 16, "sizeY": 600, "sizeZ": 16 }, "cell": { "material": 2 } },
       { "kind": "empty", "tile": 1063 }
     ],
     "checks": [ { "name": "pillar", "box": { "x": 6100, "y": 1700, "z": 3280, "sizeX": 16, "sizeY": 600, "sizeZ": 16 } } ]
   }
   ```

   `fill` sets every cell of a box, `random` takes a seeded choice from a palette, `empty` makes a whole tile air (it reuses the
   game's own air tile when the game ships one).
3. Cook it: `python3 -I tools/kfc/kfc.py terrain cook plan.json -o out/pack`. The folder holds `assets/assets.json` (a resource
   override of the world root, and a blob with an explicit hash per new tile), `assets/terrain/*.bin` and `receipt.json` (the game
   build, every tile's old and new hash, the checks' cell hashes).
4. Prove it on a fresh dedicated lab server with [`mods/tests/terrain-tile-test`](../../../mods/tests/terrain-tile-test)
   (`run.py --lab <lab>`): the server reads the tile Kiln wrote exactly as the pack holds it, through `kiln.build`.

## What a tile is

- A tile file is an envelope of cell columns; a column is runs of uniform or compressed 8x8x8 blocks (10-bit lengths), with a
  material byte and a fill byte per cell (0 air, 255 solid, 1 to 254 partial). Material 1 to 116 is terrain, 128 and up building
  blocks.
- Each tile is stored under the game's own `ContentHash` of its bytes; the world root lists the tile grid per level and part
  (`VoxelWorldResource`, layout hash `0xce3a352f`: another layout is refused).
- The coarser levels are not a simple function of level 6, so a cooker's LOD filter has to be derived, never assumed.
- Kiln's writer rebuilds every shipped tile byte for byte, which is what makes a rewritten tile safe to offer.

The bits, the reader's quirks and the evidence: [docs/re/build.md](../../re/build.md) section 9. The code:
`tools/kfc/kfc/voxeltile.py` (its docstring is the format), `voxelworld.py`, `terraincook.py`.

## In multiplayer

An overlay is applied on the server and saved like a player's digging: every client, vanilla included, follows. A lab pack is for a
dedicated lab server only.

## Known limits

- `terrain.baked` is off: Kiln builds no baked terrain for mods. The tools above are research and qualification.
- The pack changes level 6 (the server's read and write level) only: the coarser levels, `voxelHashes` and the CPU-displacement
  companions, fog, the biome map and the scene entities stay as shipped, so a client would show stale distant terrain.
- Not seen in a real game: a cooked terrain tile on a real client (check `19f-terrain-client`), an overlay on a hosted world as a
  joined player sees it (`19f-overlay-client`).
