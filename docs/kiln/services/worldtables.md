# kiln.worldtables: the game's shared building-material tables

Some game resources are a single table for the whole game: the material GUIDs a world saves with its cells, the terraforming registry (hardness, health, what a
tool does to a material), the material pools the build menu cycles through, the blueprint items. Two mods can't each override one of them. kiln.worldtables
reads the game's own bytes once, applies every enabled mod's entries, and serves the result through kiln.assets before the game reads any of it. Evidence:
[docs/re/building.md](../../re/building.md). The modder's guide is [modding/world-blocks.md](../modding/world-blocks.md), "Materials".

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_worldtables.h` (`kiln.worldtables` v1) |
| Native | `native/services/worldtables/` → `kiln/services/kiln-worldtables.dll`. `merge.cpp` is portable: ids, the handshake hash, and writers over a resource's bytes |
| Managed | `managed/services/Kiln.Blocks/World/` (`MaterialTweakBlock`, `BuildingMaterialBlock`, `StrengthBlock`, `WorldTables`; `SurfaceBlock` is kiln.surfaces') |
| Handshake | `managed/services/Kiln.Network/WorldTablesGate.cs` |
| Lint | KL330 material-budget, KL331 material-count, KL332 material-reference; KL201 for a material in a mod that doesn't say `clients: "mod"` |
| Catalog | section `materials` (`GameMaterials`): name, cell id, GUID, hardness, health, class |
| Test | `mods/tests/worldtables-test/` (`run.py` drives a lab server through 15 scenarios) |

## What it changes
- `MaterialTweakBlock` changes a game material's strength in place: hardness, health, the tool class modifier, the loot item and rate. Only the registry
  changes. The dig rate is the server's, so a tweak asks nothing of players' games.
- `BuildingMaterialBlock` adds a material: an id and a GUID in every world's GUID table, a registry config, its block item in the default or roof pool, and
  the item's blueprint entry. Kiln.Blocks makes the block item: a clone of a game block with the material's id and GUID in `equipment.voxelData`.
  Every player needs the mod (`clients: "mod"`), because cells reach players as ids.
- Digging a new material gives back what the game's own blocks give: the registry's loot item and rate are its recipe's first input at the recipe's
  ratio (Rough Stone: 100 blocks from 2 stone, a stone per 50 dug; all 85 of the game's configs name a raw material, none its block item). The input
  is resolved by name at boot; one that isn't a game item leaves the starting config's loot, with a warning. `strength.drops` wins; `"block"` is the
  material's own block item, one per block. A kept material keeps its loot but never its own block item, which doesn't exist without its mod.
- A block's `surface` is checked here with kiln.surfaces' reader, on every side, so a wrong one refuses its block everywhere alike; the registered or
  pending comparison uses its normal form, so a block from code and its data file agree. A surface named by a block comes from the mod's `surfaces`
  sections, and a writer reads it written out in the block's definition. The pictures are kiln.surfaces' ([surfaces.md](surfaces.md)).
- `BlueprintBlock` adds a shape: an entry in the blueprint-items table under its item's id, with its own size and one bit per half-metre cell, after
  the game's and the block items' entries. Kiln.Blocks makes the item (a clone of the game's straight wall or roof) and points its `voxelObject` at
  the item's own GUID, where this service serves a `keen::VoxelModelResource` of the shape: one byte per cell, `0x12` where a cell is, as the game's
  walls; the client draws it under the cursor. The game's 4 m door wall written as cells gives its table entry and its model byte for byte (lab and
  host test). An item id the table already has is refused by name. Shapes take no ids of their own, so they're not in the handshake hash; their mod's
  `clients: "mod"` makes every player have them.
- A table with nothing to change is served as the game has it. With no mod, every table is the game's to the byte (the lab compares them).

## When
- `on_ready`: read every enabled mod's `blocks/*.json` (sections `materialTweaks`, `buildingMaterials`, `strengths`, `blueprints`), `blocks.tables.json` when there is
  one, and what code defined in an earlier run (below).
- When the game's resource service comes up (kiln.assets' `when_resources_ready`), on the game's thread and before its first read: resolve names, give
  ids, write the tables, register them as overrides, then run the table writers. Every service's `on_ready` has run by then, on both sides (client log:
  services ready 3 s before the resource service).
- A block defined from C# arrives after that, when mods load. The game has read its tables by then, so the block is kept in
  `kiln/data/worldtables/pending/<mod>/` and is there from the next start: `TableBlockHandle.Status` says `Pending`, then `Registered`. Once the world
  runs, a kept block the code no longer defines is removed, so it's gone from the next start.

## Ids
A building material is a byte in a cell, 128..255. The game uses 128..212, so 213..255 are free (43). Six of the game's ids have no item and only deprecated
surfaces (129, 131, 134, 137, 139, 156); they come after the free ones and take their config over, with a warning in kiln.log. 49 in all, for every mod
together.
- Live materials (enabled mods) take ids first, in sorted GUID order. A material's GUID is `UUIDv5(<mod>:material:<name>)`, or `<was>` after a rename.
- Kept materials (absent mods, below) come after every live one, also in GUID order.
- Past the room, a material is refused by name: `block buildingMaterial m00 (mod x): refused: the game has room for 49 new building materials across all
  mods`. The others load. Kept materials hold room and are never dropped for a live one.
- A save keeps a GUID per id and remaps on load, so an id may change between sessions. Never store an id; store the GUID, and ask `material_id` / `CellIdOf`.

## Worlds and absent mods
Without Kiln's help, a world opened without a material's mod turns its cells to air: the game's load maps an unknown GUID to 0, and the next save makes that
final. So every world remembers each material it ran with (`kiln/data/worldtables/worlds/<world GUID>.json`: mod, block, strength). At boot, a remembered
material whose mod is absent stays in the GUID table and the registry with its last strength. Its cells stay and dig as before, a client draws the shader's
fallback for them, and a mod enabled again finds its walls.
- At boot it isn't known which world will load, so a material any world remembers is kept.
- `WorldTables.Forget(guid)` (C: `forget`) stops keeping one in every world: from the next start, a world's cells of it are lost when it loads.
- A world opened in a game without Kiln still loses them. Nothing here can prevent that.

## Client and server agree
Cells replicate as raw bytes with no table, so a client with other ids shows the wrong walls. `table_hash()` is 16 hex digits over the live materials' ids and
GUIDs (`0000000000000000` with none, and from a Kiln without this service). HELLO and WELCOME carry `kiln.worldtables` (the hash) and `kiln.worldtables.ids`
(`213=<guid>,...`). When they differ, the server refuses the client with a REJECT naming what differs, whatever `modSync` says except `off`:
`This server's building materials don't match your game's: brick-a (mod wt-mat-a) is id 214 here and 213 on your game. Join with the same mods, and restart
if you just changed them.` Kept materials aren't in the hash: they come after every live id, past what any client's render tables hold.

## Table writers (new material slots)
A service that owns a table per material (the client's render tables: `BuildingMaterialBlendingResource`, `GiVoxelBuildingMaterialResource`,
`BuildingMaterialParametersResource`) adds a writer and writes its table from the ids given here:
```c
static const KilnWorldTablesApi* WT;
static void KILN_CALL write_tables(const KilnWorldTablesApi* wt, void* user) {
    for (uint32_t i = 0, n = wt->material_count(); i < n; ++i) {
        KilnWtMaterial m = {sizeof m};
        if (wt->material_at(i, &m) != KILN_OK || m.state != KILN_WT_LIVE) continue;
        const uint32_t slot = m.cell_id - 128;   // the render tables' index
        // m.definition: the block's JSON as written, with its "surface"; m.mod_dir: where its files are
        // ... build the slot, then resource_put the table through kiln.assets
    }
}
static void KILN_CALL on_ready(void* u) {   // in on_ready: the merge runs after every service's on_ready
    WT = api->service((KilnStr){"kiln.worldtables", 16}, 1);
    if (WT && WT->state() != KILN_WT_OFF) WT->add_writer(owner, &write_tables, NULL);
}
```
- Writers run once, on the game's thread, after the ids are known and before the game's reads. A writer that can't do its table logs why and leaves it; the
  service's own tables stay merged.
- Only live materials get a slot. A table's count (`layerCount`, `materialCount`) should end at the last live id, so a kept id is past it and draws the
  shader's fallback.
- A reused id (`m.reused`) replaces a deprecated slot's data in place; others extend the table.
- `first_new_id()`: the first id after the game's own entries (213 on the current builds).

## Fail safe
Every layout comes from the game's reflection in the running exe: field offsets of the registry, its configs, the world resource, the pools and blueprint
items, and the hardness and class enums, checked against the names mods write. A type or field that doesn't resolve, a resource that isn't there or doesn't
read as its layout says, or kiln.assets refusing a table: the service turns off, nothing is served, and kiln.log says why:
`worldtables: this game build doesn't resolve: keen::BuildingTerraformingConfig has no field healthPoints. The game's tables are untouched: material blocks are
off this session`. `kiln/config/worldtables.json` `{"enabled": false}` turns it off too.

## Status
- kiln.log: every tweak (`tweak hard-wood (mod x): WoodRough (143): hardness SlightlyHard -> ModeratelyHard`), every material with its id, what was refused,
  the hash.
- `kiln/data/worldtables/status.json`, `WorldTables.StatusJson()`, C `status_json`: state, hash, ids, tweaks, shapes (item, id, size), errors, refusals, and each table's bytes and
  FNV-1a against the game's.

## Tests
```sh
export KILN_OUT=<build dir>
native/build.sh kiln-core kiln-proxy kiln-assets kiln-worldtables
dotnet build mods/tests/worldtables-test/WorldTablesTest.csproj -c Release -o $KILN_OUT/mods/worldtables-test --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/worldtables-test/fixture/WtFixture.csproj -c Release -o $KILN_OUT/wt-fixture --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new mylab <port> --save <savegame dir>
tools/deploy-server.sh <labs>/mylab --policy any
mods/tests/worldtables-test/run.py --lab mylab --port <lab transport port> [--low]
```
Each scenario is one server start; the save carries over: no mods (tables byte-identical to the game's files, the game's own explosion on four
materials), a tweak that changes nothing, tweaks (Unbreakable, a softer and a harder category, health), bad blocks, a layout that doesn't resolve, a new
material (its id, its dig, its item in the pools), a restart, a mod list that moves the id, the mod removed (walls stay) and back, the handshake with fake
clients, a forgotten material, 50 materials (49 placed, one refused), and a tweak from C# across four starts. `kiln/config/worldtables.json`
`{"testMissingField": "<field>"}` is a test hook: the service reads the layouts as if the game lacked that field.

## Not proven
Nothing here has been drawn by a client. A client's own registry only feeds its cursor and movement, as far as the code shows; the render tables come from a
table writer.
