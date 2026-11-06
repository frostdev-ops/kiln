# Game-data catalogs: game ids for tools and IntelliSense

Mods name the game's things by id: item and recipe names in data packs, template and sequence names in kiln.gamedata
sections, perk and buff ids, sound cue and model GUIDs in cook recipes. The **catalog** lists them all, made from
**your own installed game**, read-only, on your machine. The linter (`kiln lint`) checks names against it, the VS Code
extension completes and explains them, `kiln catalog search` finds them, and the **Kiln.GameIds** source generator
turns them into C# constants with IntelliSense.

The catalog is Keen Games' data. It is never committed, bundled or downloaded: each modder makes their own copy, like
the launcher's game art (docs/kiln/launcher/gameart.md).

## Where it lives
| What | Path |
|---|---|
| Client catalog | `<dir>/client.json` (from `enshrouded.kfc` + `enshrouded.exe`) |
| Server catalog | `<dir>/server.json` (from a dedicated server's `enshrouded_server.kfc` + `enshrouded_server.exe`) |
| Kiln.Game types | `<dir>/types/<side>-<exe sha256>.json` (reflection dump), `<dir>/Kiln.Game/Generated.cs` |
| `<dir>` | `$KILN_CATALOG` when it names a folder (a `.json` file: its folder); else `<launcher data dir>/catalog`: Linux `~/.local/share/kiln/catalog` (`$XDG_DATA_HOME/kiln/catalog`), Windows `%LOCALAPPDATA%\Kiln\catalog`, or `$KILN_DATA_DIR/catalog` |

Readers look for a file in this order (`kiln_manage::catalog::find`):
1. an explicit path (`kiln lint --catalog F`, the generator's `KilnCatalog` property): a file, or a folder;
2. `$KILN_CATALOG`: a file, or a folder;
3. the default folder.

For a folder they take `client.json`, then `server.json` (the generator: by the mod's side, see below).

## Making and refreshing it
```sh
kiln catalog build                 # every installed side; skipped when current
kiln catalog build --side client --force
kiln catalog status                # per side: file, game build, current?, counts
kiln catalog path --json           # {dir, client, server, exists, current}
kiln catalog search "bronze sword" [--section items] [--limit 20] [--json]
```
`kiln catalog build` finds the games itself:
- `$KILN_CATALOG_GAME` (one game folder), else
- the launcher's known games, else
- the Steam libraries (client app 1203620, server app 2278520).

A build takes about half a second (release build: client 0.5 s, server 1.0 s).

**Current or outdated.** The catalog records the game's `.kfc` version string (it changes with every data build) and
the extraction `recipe`. `status` reads only the `.kfc` header, so the check is cheap.
- `current: false`: the game was updated, or Kiln's rules changed. Run `kiln catalog build` again.
- `current: null`: no game to compare with.

A build when nothing changed returns at once (`built: false`).

**Refusals.**
- Steam is updating the game (app manifest `StateFlags`): `steam_updating`.
- The exe and the data are from different builds, so a type's layout hash differs (mid-update): `conflict`.
- The destination is inside the game folder: `refused`. Kiln never writes into game folders.

Fail soft: a section the game changed is left out and named in `problems`; the rest is written. A dedicated server has
no texts, so `server.json` has no `loca`, `sounds` or display names.

## The format (`kind: "kiln.catalog"`, `format: 1`)
Schema: `crates/kiln-manage/src/catalog/catalog.schema.json`. Synthetic fixture (invented names, every section):
`crates/kiln-manage/src/catalog/testdata/client.json`. The file has the header pretty-printed and one entry per
line. Every section and every entry key is optional; readers ignore what they do not know.

```jsonc
{
  "format": 1, "kind": "kiln.catalog", "generator": "kiln-manage 0.2.0-dev catalog",
  "generated": "2026-11-04T19:40:12Z", "recipe": 1,
  "game": { "side": "client", "dir": "…/Enshrouded", "steamBuild": "26430625", "build": "26430625", "svn": "1287369",
            "kfcVersion": "1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54.051725Z", "exeSha256": "5d9ef2f1…" },
  "languages": ["en-us", "de-de", "fr-fr", …],          // the game's text languages; displayName is en-us
  "icons": "/home/me/.local/share/kiln/editor-assets/icons",   // or null; relative = to the catalog file
  "counts": { "items": 4268, … }, "problems": [],
  "sections": { "items": [ {…}, … ], … },
  "fields": { "items": [ {"path": "maxStackSize", "type": "uint16"}, … ], … }
}
```
Entries carry:
- `name`: the key data packs write;
- `id`: `"0x%08x"`, lower case;
- `guid`: lower case, 36 characters;

where the game has them, plus the per-section fields below.

| Section | Made from | name / id / guid | More fields | Count (client 1287369) |
|---|---|---|---|---|
| `items` | every `keen::ItemInfo` | debugName / ItemId / objectId | `displayName`, `description` (en-us), `kind` (ItemCategory), `rarity`, `generateRarity` (true on gear that keeps it: loot weapons; absent otherwise; kiln lint KL406), `tier` (the `T<n>` token of the name: a naming convention, not a game field), `maxStack`, `icon` (file name in `icons`), `registered: false` (only on the 55 ItemInfo the ItemRegistry does not list) | 4,268 (4,213 registered) |
| `recipes` | `RecipeRegistryResource.recipes` | debugName / recipeId / recipeGuid | `displayName`, `workshop` (`0x` id), `workshopName`, `inputs` / `outputs` (`{item, count}` or `{category, count}`) | 2,542 |
| `loot` | `LootableItemsResource.items` | the item's name / ItemId | | 4,213 |
| `drops` | every `ecs::DefaultInventoryResource` | – / – / GUID | | 420 |
| `templates` | every `ecs::TemplateResource` (names read by offset) | name (the GUID when empty) / FNV-1a of the GUID / GUID | `family`, `components` (short type names: what a `templates` path may start with) | 12,269 |
| `arsenals` | `enemy::EnemyArsenalRegistryResource.arsenals` | GUID / FNV-1a / GUID | `templates` (the templates whose `EnemyArsenalReference` names it) | 201 |
| `graphs` | every `anim_graph::runtime_graph::AnimationGraphResource2_0` | the shortest name among the templates that use it (the GUID when none) / FNV-1a of the GUID / GUID | `skeleton`, `states` (`{name, id, guid when a sequence names it, clip, seconds, loop, or blend}`), `floatInputs`, `slots`, `nodes`, `templates` | 279 |
| `sequences` | every `actor::ActorSequenceResource`, one entry per sub-sequence | sub-sequence name (`<guid>#k` when unnamed or repeated) / FNV-1a(GUID) ^ k·0x9E3779B1 / the sequence GUID | `sequence`, `index` (k > 0), `length` (s) | 2,162 |
| `configs` | every config id a `keen::ImpactProgram` binds, and the ones the game's code reads | the most used name / config id / – | `names` (every name, most used first), `shared` (true when `name` also names another id), `programs`, `readBy`, `type`, `kind`, `enum` | 348 |
| `buffs` | `BuffTypeCollectionResource.buffs` | `buff_<id>` / buffTypeId / – | `displayName`, `description`, `slot`, `hidden` | 274 |
| `perks` | every `keen::Perk` | debugName / FNV-1a of the GUID / GUID | `displayName`, `description` | 365 |
| `skills` | `SkillTreeResource.nodes` | `skill_<id>` / node id / – | `displayName`, `description`, `item` (the ability it unlocks), `type`, `maxLevel`, `costs` | 243 |
| `workshops` | `WorkshopRegistryResource` (`npcs`, `workshops`) | the station item's name / workshopId / workshopGuid | `displayName`, `kind` (`workshop` / `npc`) | 51 |
| `itemCategories` | `RecipeRegistryResource.inputCategories` (recipe inputs `{"category": "0x…"}`) | – / inputCategoryId / – | `items` (member names) | 22 |
| `resources` | RenderModel, RenderMaterialResource, ModelHierarchyResource, UiTextureResource, ImpactProgram, SoundContainerResource, SoundResource, VoiceoverResource (what cook recipes and asset entries name) | texture / sound debugName / – / GUID | `type`, `part` (> 0) | 45,220 (server 2,093) |
| `sounds` | every `SoundContainerResource` (sound cues; client only) | the first sound's debugName (the GUID when none) / FNV-1a / GUID | `scene`, `positional`, `entries` | 6,951 |
| `vfx` | every `keen::VfxResource` (client only). VFX have no names of their own, so see below | a template that carries it, a sequence that plays it, a label, or `Vfx_<guid8>` / FNV-1a of the GUID / GUID | `namedFrom`, `byName`, `names`, `usedBy`, `particles`, `emitters`, `meshes`, `textures`, `cullDistance`, `killTime`, `length`, `parameters` | 1,575 |
| `materials` | `TerraformingEfficiencyRegistryResource`, the world's `materialGuids`, the items that place each material | from the placing item's name (`Block_T1_Wood_Rough_CRAFTING` → `WoodRough`, `Build_TerrainMaterial_T1_Stone` → `TerrainStone`), else `Cell<id>` / — / the material's GUID | `kind` (`building` / `terrain`), `cell`, `displayName`, `item`, `hardness`, `health`, `breaks`, `roof` | 201 |
| `loca` | `LocaTagCollectionResource`, en-us (client only) | – / tag id (FNV-1a of the tag GUID) / – | `text` (with the game's markup) | 16,740 |

The names and ids follow kiln.content and kiln.gamedata exactly (`native/services/content/gamedata_res.cpp`): what the
catalog calls `skill_81d8b64f` is what a `skills` section accepts. Display names have the game's markup removed.

**VFX names.** A VFX gets its `name` in this order:
- a template whose first `VfxComponentResource` is this VFX (`namedFrom: "template"`, `byName: true`);
- a sequence whose first `VfxNotifierEvent` plays it (`namedFrom: "sequence"`, `byName: true`);
- a label from something else that uses it: a component's `vfx` field, a later event (`Prop_Crate_DestroyOnLoot`; `namedFrom: "use"`);
- `Vfx_<guid8>` when nothing does.

`byName` means `VfxId.Game` and `"game:<name>"` find it at run time, because that is the lookup they make, and `names` lists every name that does. The others have only a label, so use the GUID: `usedBy` lists up to 8 such uses. `length`
is how long Kiln runs it as a one-shot: the larger of `killTime` and its `duration` + `lifetime` parameters, 10 s if it says nothing, 1 to 60 s.

`parameters` are `{name, type, default}`, or `{hash, type, default}` where the name isn't known. The game stores only the FNV-1a of a name, so names are recovered by hashing the strings in your exe and the names of particle state members.
That finds about a fifth of them (client 1287369: 1,541 of 8,049 uses). `type` comes from the DsTypeId, the FNV-1a of `VfxEffectParameter<Type>`.

`fields` lists, per section, the paths data packs can name. They come from the exe's reflection:
- `{path, type, kind, writable?, enum?, enumNumbers?}`; paths like `maxStackSize`, `damageSetup.dmgMod`, `perkIds[2]`,
  `tags[*].tag`;
- `type` drops the `keen::` prefix;
- `kind` says what the value is: a scalar's name (`float32`, `uint16`, `bool`), `time`, `enum`, `flags`, `ref`, `guid`, `string`,
  `list`, `variant`, `configs`, or `struct` (nothing below it is listed);
- `enum` holds the value names of enum and flag fields; `enumNumbers` their numbers (for flags, the bit) when they aren't 0, 1, 2, ...;
- `writable: false` marks identity fields (ids, GUIDs, ItemInfo `flags`, debug names) and names;
- sections: items (420 paths), recipes (45), loot (29), buffs (74), perks (47), skills (35), sequences (10), templates (7,134).

`templates` lists the template's own fields, then every component's, with the component's name as the first segment:
`LifeTime.lifeTime`, `Enemy01BehaviorState.aggroSetup.viewRange`. A component with no fields of its own is listed by name. Which
template has which component is in that template's `components`.

Below a `configs` field (`impactValues`, `configValues`, `ImpactConfiguration.configValues`) a path takes a config by any of its
`names` in the `configs` section. Below a `variant` nothing is listed, because the type depends on the entry.

Size and speed: the client catalog is 21 MB (templates 7 MB with their components, resources 4.4 MB, `fields` 1 MB). It loads in
0.12 s (Rust, release). The server catalog is 12 MB.

### Rust API (`kiln_manage::catalog`)
| Item | Feature | What |
|---|---|---|
| `Catalog::load(path)`, `Catalog::from_slice`, `.section(name)`, `.lookup(section, key)`, `.counts()` | default | the reader (lenient; `lookup`: name case-sensitive, `0x` id and GUID case-insensitive, no globs) |
| `search(&cat, query, section, limit) -> Vec<Hit {section, name, id, guid, displayName}>` | default | words AND, exact > prefix > contains; `loca` only when asked for |
| `default_dir()`, `catalog_dir()`, `find(side, explicit)`, `file_for(dir, side)`, `side_of(game_dir)` | default | paths |
| `status(dir, game_dir) -> Vec<CatalogStatus {side, path, exists, current, game, gameBuild, generated, counts, error}>` | default | |
| `write(path, &cat)`, `to_text(&cat)` | default | the writer (atomic) |
| `build(game_dir, dest, &BuildOptions {force, steam_build, sections, icons_dir, types_dump}, &mut dyn Progress) -> BuildInfo {side, path, built, game, counts, bytes, seconds, problems, typesDump}` | `gameart` | one side |
| `build_default(side, &BuildOptions, &mut dyn Progress) -> Vec<BuildInfo>`, `game_dirs()` | `gameart` | every installed side into `catalog_dir()` |
| `types::{dump, write_dump, from_exe, generate_cs, DEFAULT_COMPONENTS}` | `gameart` | Kiln.Game types (below) |

The reader is part of the default build. Making a catalog reads KFC4 data and needs feature `gameart` (gameart's
`kfc` and `refl` readers, a port of Kiln's MIT `tools/kfc`; no kfc-parser code).

## C#: Kiln.GameIds (IntelliSense for game ids)
Kiln.Sdk adds the generator to every mod. With a catalog on the machine, the mod gets constants:
```csharp
using Kiln.GameIds;

Content.Items.Get(GameItems.Weapon_T1_1H_SwordEpic_01)!.Set("maxStackSize", 5);   // hover: "Bronze Sword (Weapons, …)"
uint id = GameItems.Ids.Weapon_T1_1H_SwordEpic_01;                                 // 0x43108313
string guid = GameItems.Guids.Weapon_T1_1H_SwordEpic_01;
Content.Buffs.Get(GameBuffs.Rested_cdbe9c80);                                      // "0xcdbe9c80"
Log.Info($"built against {GameCatalog.Side} {GameCatalog.SteamBuild} (data {GameCatalog.DataBuild})");
```
The classes are named `Game*` so they never clash with Kiln's own `Kiln.Items` / `Kiln.Skills` when a mod imports
both namespaces.

| Class | Constant value | Identifier |
|---|---|---|
| `GameItems`, `GamePerks`, `GameRecipes`, `GameTemplates`, `GameSequences`, `GameSounds` | the entry's `name` | the name (GUID-named and `<guid>#k` entries are left out) |
| `GameVfx` | the GUID, which always finds it (`VfxId.Of`, `VfxRef.Game`) | the name |
| `GameMaterials` | the material's `name` (`WoodRough`; what `MaterialTweakBlock.Material` takes) | the name |
| `GameBuffs`, `GameSkills` | the `0x` id (the data packs' key) | display name + `_<id>` (`Rested_cdbe9c80`; the game names them `buff_<id>`) |
| `GameWorkshops` | the `0x` workshop id (a recipe's station) | the station item's name |

Each class also has `Ids` (`const uint`) and `Guids` (`const string`) where entries have them. `Kiln.GameIds.GameCatalog`
names the build: `Side`, `SteamBuild`, `DataBuild`, `KfcVersion`, `Generated`. XML docs carry the English name, facts
(kind, rarity, tier, stack, id, GUID) and the description, so the IDE shows them on hover and in completion.

Identifiers:
- characters other than letters, digits and `_` become `_`;
- a leading digit gets `_`;
- C# keywords get `@`;
- repeats get `_2`, `_3`, … in catalog order.

| Property | Default | |
|---|---|---|
| `KilnGameIds` | `Items;Perks;Buffs;Skills;Workshops;Materials` | sections (with or without `Game`); any of those plus `Recipes`, `Templates`, `Sequences`, `Sounds`, `Vfx`; `Fields` or `Fields.Items` for typed paths ([typed-fields.md](typed-fields.md)); `All`; `None` |
| `KilnCatalog` | `$KILN_CATALOG`, else the default folder | a catalog file or folder |
| `KilnCatalogSide` | `server` for `KilnSide` server, else `client` | which file of a folder first; the other one when it is missing (the server catalog has no display names) |

Diagnostics:
- **KILN190** (info): no catalog; nothing is generated. Run `kiln catalog build`. Code that uses the constants fails
  with CS0234 until a catalog exists.
- **KILN191** (warning): the catalog cannot be read.
- **KILN192** (warning): `KilnGameIds` names an unknown section.
- **KILN193** (warning): `KilnGameIds` asks for `Fields` and the project doesn't reference Kiln.Content.
- **KILN198** (warning): the catalog is older than what `GameFields` needs. Run `kiln catalog build`.
- **KILN194** to **KILN197**: string paths checked against the catalog ([analyzers.md](analyzers.md#kiln194)).

Cost on the real client catalog:
- default sections: ~5,000 constants, 150 ms per compilation;
- `All`: 57,656 constants (12k templates), 0.6 s to generate and 2.9 s with the compile;
- `Fields`: about 8,000 paths, 1.3 MB of dll, 3 to 7 s more per compile; `Fields.Items` alone: 130 KB, about 1 s.

The generator re-runs only when the catalog file or the property changes.

The generator is `managed/Kiln.Analyzers/src/Kiln.GameIds` (netstandard2.0 `IIncrementalGenerator`, its own small
JSON reader). The path analyzer lives in the same assembly and reads the same file. It ships in the Kiln.Sdk package (`analyzers/dotnet/cs/Kiln.GameIds.dll`); `Sdk/Kiln.GameIds.targets`
wires it:
- the catalog as an `AdditionalFiles` item with `KilnCatalog="true"`;
- the property passed as `KilnGameIdsList` with `,` (`;` starts a comment in the generated `.editorconfig`).

The generated code is compiled into the mod: the constants are plain strings and numbers, no game data is shipped
beyond the ids the mod uses itself.

## Kiln.Game from your game
`managed/Kiln.Game/Generated.cs` (the typed ECS component structs: NetworkHealth, Health, Mana, Stamina,
CurrentTransform, LastTransform, Velocity, TeleportCount, NpcCommon) is game-derived. It was made from a reflection
dump the lab probe (`tools/typegen/probe`) walked at run time, `sigdb/types/server/<sha>.json`, through
`tools/typegen/typegen.py`. The committed file stays. The local path makes the same file offline from the installed
exe, with no lab and no running game:
```sh
kiln catalog build --types            # also writes <dir>/types/<side>-<sha>.json and <dir>/Kiln.Game/Generated.cs
python3 tools/typegen/typegen.py <dir>/types/server-<sha>.json <out>/Generated.cs   # the same, with the Python tool
dotnet build managed/Kiln.Game -p:KilnLocalGameTypes=<dir>/Kiln.Game/Generated.cs   # needs the Kiln.Game.csproj switch (pending)
```
- The dump has the probe's shape: the record's own fields, without base-struct fields, like the probe.
- `catalog::types::generate_cs` is a port of typegen.py.

Checks:
- the server exe of build 26404334 (`c5c9fe50…`, the build the committed files came from), read offline, gives a dump
  **equal** to the committed `sigdb/types/server/c5c9fe50….json`;
- both typegen.py and the Rust port turn it into the committed `Generated.cs` byte for byte (apart from the `// Source:`
  line); a unit test keeps the port equal;
- the current server 26430253 (`f0d824be…`) gives the same bindings (the nine layouts did not change);
- Kiln.Game builds against the local file.

## Legal review notes
- **Stays local, never committed or shipped**: `client.json` (with its `vfx` section), `server.json`, `types/*.json` and the local
  `Kiln.Game/Generated.cs`. They are made on the user's machine from the user's own install. Like gameart, the default
  location is the launcher's data folder, outside the repository and the game folder.
- **Committed and game-derived** (flag for the review): `managed/Kiln.Game/Generated.cs` and
  `sigdb/types/server/c5c9fe50….json`. They hold the layout of nine ECS component types (field names, offsets, sizes,
  type hashes), read from the game's reflection. With the local path in place, the committed copies could be dropped
  in favour of generating them on each machine; the decision is open.
- **Committed and invented**: `crates/kiln-manage/src/catalog/testdata/client.json` and `testdata/icons/*.webp`
  (`Test_Frost_Blade`, `Enemy_Test_Frostling`, …, two 8x8 pictures drawn for the test). The field names and section
  shapes mirror the game's reflection names (`maxStackSize`, `damageSetup.dmgMod`, enum names like `Common`/`Rare`),
  as the data-pack docs do. The catalog unit tests build synthetic KFC4/PE files in a temp folder.
- **In a mod's build output**: a mod that uses `Kiln.GameIds` compiles the constants it references (names, ids, GUIDs
  of game entries) into its own dll, the same identifiers its data packs would spell out by hand.

## Development
- Tests: `cargo test -p kiln-manage --lib catalog` (reader, search, status, fixture vs schema) and
  `--features gameart` (a whole build from a synthetic KFC4 + PE, refusals, the typegen port).
  `dotnet test managed/Kiln.Analyzers/tests/Kiln.GameIds.Tests` (generator, `GameFields`, path analyzer;
  `KILN_CATALOG_TEST=<client.json>` adds runs over a real catalog and every path string in `mods/`;
  `KILN_LIVE_PATHS=<live-paths.json>` from `mods/tests/typed-test` adds every path the running game reports).
- Real game (read-only, opt-in): `KILN_CATALOG_GAME=<game folder> KILN_CATALOG_OUT=<scratch> cargo test -p
  kiln-manage --features gameart --release --lib -- --ignored catalog::tests::build::real_game --nocapture`; an exe
  alone: `KILN_CATALOG_EXE=<exe> KILN_CATALOG_SIDE=server KILN_CATALOG_OUT=<scratch> … -- --ignored
  catalog::tests::real_exe_types`.
- After a game update: compare `counts` and `problems` before and after. Bump `catalog::RECIPE` when a rule changes,
  so existing catalogs read as outdated.
