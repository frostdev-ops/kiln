# Game art from the user's own game

The launcher shows Keen Games' art in two places: the save editor (item icons, UI textures, fonts) and the admin map
(relief, tiles, markers with their names in every game language, marker icons, fonts). That art is game data: it is never committed, bundled or downloaded. Each user's
launcher makes its own copy from their own installed client, read-only, into the launcher data folder:

| Where | What | Made by |
|---|---|---|
| `<data>/editor-assets/{icons,ui,ui/tinted,fonts}` | the save editor's art | `kiln_manage::gameart::extract_editor` |
| `<data>/admin-assets/map/{gpu,tiles,fonts}`, `map/i/g-*.webp` | the admin map's relief, tiles, marker icons, fonts | `kiln_manage::gameart::extract_map` |
| `<data>/admin-assets/map/game/{markers.json,lang/*.json}` | the map's markers, place names and their texts in every game language | `extract_map` ([Map markers](#map-markers-mapmarkersrs)) |
| `<data>/admin-assets/map/data/`, `map/i/{i,j,p}-*`, `data/*.json` | local markers (the map's fallback), the finder data, item-named icons, catalogs | copied from the folder `$KILN_ADMIN_EXTRAS` names, when set (not extracted yet) |
| `<data>/{editor,admin}-assets/.source.json` | where the art came from | [`SourceInfo`](#sourcejson) |

The `kiln-asset` protocol (`launcher/src-tauri/src/assets.rs`) serves both folders.
(`crates/kiln-manage/src/gameart/`, feature `gameart`, part of `full`; the asset parts of `commands/app.rs` and
`assets.rs`; `launcher/src/components/GameArt.tsx`, the editor banner, the Settings "Game art" card;
`tools/launcher/fetch-*.sh`).

## In the app (automatic, no buttons)
- `<GameArtAuto/>` (mounted by the shell, `launcher/src/components/GameArt.tsx`) reads `app_assets_status` every 15 s and on window
  focus, and runs the work in the background, one kind at a time, when:
  - the art is missing (first launch) and a client is found;
  - the installed game's data build changed (`current: false`: a game update) or the recipe was bumped;
  - the mods changed (`editor.modded.current: false`): active profile, library, `<game>/kiln/mods/`, or the game's
    `kiln/state/content-definitions.json` (the overlay of mod items is rebuilt; the art itself is not re-extracted).
- While it runs: the task shows in the header's task tray (its name and three small cones). Errors: a toast once per
  state (same game build and mod set: no retry for 10 minutes; a new build or mod set retries at once;
  `steam_updating` retries at the next check) and a line in `<data>/logs/launcher.log`.
- Settings → Game art shows status only: source, game build, counts, size, last run, mod items and their mods.
- The editor shows placeholders and one line, "Preparing game art…", while the art is missing or being made; it
  remounts when the art or the mod overlay changes. The admin map shows "Preparing map art…" and probes again when the
  map art arrives.

## Commands (contracts.md section 9)
- `app_assets_import(kind, from)` → TaskId, result `AssetsStatus`:
  - `editor`, `from` null: task "Prepare game art": extracts the art from the client
    [`art_game`](../../../launcher/src-tauri/src/commands/app.rs) picks (the default game if it is a client, else the
    first known client, else the first client in the Steam libraries) when it is missing or outdated, then rebuilds the
    mod overlay when the mods changed; ends at once when nothing changed. Refused (`steam_updating`) while Steam
    updates the game; `not_found` without a client and without art.
  - `admin`, `from` null: copy markers/item icons/catalogs from `$KILN_ADMIN_EXTRAS` (when set), then extract the map from
    the client. Without a client: the full copy from that folder.
  - `from` a folder: copy from that folder (dev fallback; writes `.source.json` with `source: "copy"`).
  - While one runs, a second call for the same kind returns the running task's id. Task titles: "Extract game art",
    "Extract map art".
- `app_assets_status()` → `AssetsStatus`. Additions (all optional for older readers): `editor.{source, build, gameId,
  gameBuild, current, canExtract, task, counts, extracted, modded: {items, icons, current, version, generated, mods}}`,
  `admin.{source, build, gameBuild, current, canExtract, markers, task, counts, extracted}`.
  `current`: true = extracted from the installed data build with the current recipe; false = the game (or the recipe)
  changed since, offer to re-extract; null = unknown (a copy, or no client).
- `KILN_ASSETS_NO_DEV=1` (debug builds): drop the `launcher/assets-local` roots, to test the missing-art path.

## Rust API (`kiln_manage::gameart`)
| Item | What |
|---|---|
| `extract_editor(game_dir, dest, &EditorOptions, steam_build, &mut dyn Progress) -> Result<SourceInfo>` | the editor's art into `dest` |
| `extract_map(game_dir, dest, &MapOptions, steam_build, &mut dyn Progress) -> Result<SourceInfo>` | the map's art into `dest` (`<data>/admin-assets`) |
| `status(dest, Option<game_dir>) -> ArtStatus {source, build, gameBuild, current, extracted}` | compare a folder with the installed client (reads the `.kfc` header only) |
| `game_build(game_dir) -> GameBuild {kfcVersion, build}`, `read_source`, `write_source`, `recipe_of(kind)` | |
| `GameData::open(game_dir)` | the archive + layouts (+ exe sha256) |
| `kfc::Kfc` | read-only KFC4: `open`, `resources_of_type`, `read`, `read_stream`, `content_location`, `read_content[_prefix]`, `read_version` |
| `refl::{PeFile, TypeDb}` | the exe's reflection records: `from_exe`, `get`, `path("keen::UiTextureResource", "data.hash")`, `enum_values` |
| `res::{layouts, ui_textures, fonts}` | typed `UiTextureResource` / `UiFontResource` |
| `texture::{Format, decode}`, `image::{resize, fit_within, tint, encode_webp, decode_webp}` | pixels |
| `work::run_parallel` | threads, progress, cancel |

Every extraction writes into `<dest>/.extract-<pid>-<t>/` and swaps the finished folders in at the end
(`<old>` → `.old-*` → removed): a canceled or failed run leaves the previous art untouched; leftovers are removed by the
next run. The destination is refused when it lies inside the game folder. Game files are opened read-only, with
positional reads (several threads share one handle per `.dat`).

## How it reads the game
A port of Kiln's clean-room MIT reader `tools/kfc` (no kfc-parser code; layouts in docs/re/models.md and
docs/re/resources.md):
1. `enshrouded.kfc`: the KFC4 directory (version string, resource keys/values, type bundles with layout hashes, content
   keys/locations, zstd chunks of the resource stream). `enshrouded.kfc_resources`: zstd chunks, decompressed on
   demand (4 cached). `enshrouded_NNN.dat`: content blobs (texture pixels, font files).
2. `enshrouded.exe`: the reflection records in `.data`/`.rdata` (validated by FNV-1a of their names) give field offsets
   by name, enum values by name, and each type's layout hash. Used when the exe's layout hash of `UiTextureResource`
   equals the `.kfc` bundle's. If the exe cannot be read, built-in layouts (clients 26404966 and 26430625) are used, but
   only while the `.kfc` still has their layout hashes (`UiTextureResource` 0x3c9eb3fa, `UiFontResource` 0xb3965140);
   otherwise `unsupported`. Exe and `.kfc` from different builds (mid-update): `conflict`.
3. `UiTextureResource` (5,706 in 26430625): `{data.hash: ContentHash, debugName, size, type, format, levelCount}`; the
   content blob is the mip chain, largest first, tightly packed. `UiFontResource.fonts[]`: one OTF/TTF per content blob.
4. Pixels: RGBA8/BGRA8/R8/RG8/R16 copied; BC1-5, BC7 decoded with `bcdec_rs` (MIT); BC6H is not supported (none in UI).

## The editor recipe (`editor.rs`, `RECIPE` 1)
| File | Rule |
|---|---|
| `icons/<debugName>.webp` | every 2D part-0 `UiTextureResource` named `*_icon` (the editor's `items[id].debug + "_icon"`), fitted into 96x96 (never upscaled, aspect kept, premultiplied bicubic from mip 0 or the first mip at least twice the size) |
| `ui/<debugName>.webp` | every other 2D texture up to 256 px, plus `EDITOR_LARGE_UI` (27 larger ones the editor uses), at their own size |
| `ui/tinted/<name>.webp` | `TINTS` (51): a flat colour through a resized UI mask, alpha x factor. Measured against the editor's set: every source matched with alpha correlation 1.000, premultiplied error at most 3/255; the colours are the editor's `data/ui.json` ones |
| `fonts/font<NN>.otf\|ttf` | `UiFontResource.fonts[NN]` up to 2 MiB (12 files; the CJK fonts are skipped); extension from the file's magic |

Skipped on purpose (counted in `.source.json` `skipped`): multi-part textures (the world map set), non-2D, unusable
names, duplicate names (first wins), unknown formats, large UI images the editor does not use.

Encoding: lossless WebP (`image-webp`); fully transparent pixels get black RGB; opaque images drop alpha.

### Check against the Save Editor's own set (client 26430625)
- Files: the editor ships 4,587; this extraction writes 5,235 (4,367 icons, 793 UI, 51 tinted, 12 fonts, + the
  `.source.json`). **4,585 of the editor's 4,587 files are reproduced** under the same names and sizes. The two left:
  `icons/Consumable_Potion_Flask_Fog_Survival_Flask_T5_icon.webp` (no longer in this game build) and `ui/Shield.webp`
  (the texture is `shield` now; nothing in the editor's code or data names it).
- Pixels (premultiplied mean absolute difference, 0-255): icons 1.18 (300 sampled, the editor's are lossy WebP),
  UI 0.62 (all 166), tinted 0.24 (all 51); every size identical. Visually identical (side-by-side sheets).
- Size: 53 MB (icons 42 MB lossless at 96 px; the editor's lossy set is 26 MB).
- Time (8 threads): release 1.0 s, debug build 23.6 s (warm page cache; a cold read of ~400 MB of `.dat` takes longer).

## The map recipe (`map.rs`, `MAP_RECIPE` 3)
The world map is one `UiTextureResource` GUID with 207 parts. Layout of
`launcher/src/admin/map/gamemap.ts`:

| File | Made from | vs the Interactive Map's data |
|---|---|---|
| `map/gpu/base.bin` | the base height texture (`_map_shader_parameters_baseHeight`, R16 1280²), gzip of the u16 LE samples | equal except 105 of 1,638,400 samples (the game update) |
| `map/gpu/d/x_y.webp` | `DetailHeight(x,y)` (BC4 1024²), grey | ≤ 0.21/255 mean |
| `map/gpu/f/x_y.webp` | `FogZone(x,y)` (BC5 1024²) → R, G at 512² (2x2 box), B 0 | ≤ 0.04/255 mean |
| `map/tiles/{0..3}/{x}_{y}.webp` (138) | **Kiln's own render**: heights as 4 m block means, coloured with the ramp and hillshade of `launcher/src/admin/map/reliefShader.ts`, without its contours and Shroud; zoom 3 = one tile per detail tile, zooms 2..0 box-downsampled, transparent outside the world | same layout, its own look; it is only the fallback under the WebGL relief |
| `map/i/g-<name>.webp` | every `mapmarker_*` texture and `player_position` (54), plus the world-object category icons (13: `icon_chest` tinted gold/silver/wood, `icon_loot`, `journal_lorePageFound`, `collectionslot_smallFossils`, `icon_combat`, `icon_goal`, `logicGraph_entityCategories_icons_door`, `icon_fish`, `updraft_unlocked`, `double_jump_unlocked`, `spatial_hookshotTargetIcons_base`), fitted into 64 px | the four Emberwatch has are among them |
| `map/game/markers.json`, `map/game/lang/<code>.json` | [Map markers](#map-markers-mapmarkersrs) | replaces a local `map/data/markers.json` in the map |
| `map/fonts/harmonique.otf`, `notoserif-game.ttf` | `UiFontResource.fonts[0]` (Harmonique Display Regular), `[10]` (Noto Serif Regular), whole files | the Interactive Map's are subsets |

The world map set also holds parameter textures (gradients, noise). The launcher does not use them, so they are not
extracted: the relief (`launcher/src/admin/map/reliefShader.ts`, Kiln's own shaders) reads `gpu/base.bin`, `gpu/d/` and
`gpu/f/`, and the tiles are made from the same heights.

Release: 1.8 s (the markers 1.1 s of it; 0.5 s before them). Checked in the launcher's admin map (browser mock,
Playwright Chromium + SwiftShader): the WebGL relief loads from these files (`data-relief="ready"`), with the Shroud
overlay and live players. Recipe 2 added `game/` and the object icons; recipe 3 made the tiles Kiln's own and dropped the
parameter textures. Art made with an older recipe shows as outdated and `<GameArtAuto/>` re-extracts it on its own.

**Not from the game yet** (stay on a local copy, when there is one): the finder data `map/data/{find,chests,ore}.json` and `pts.bin`
(creature spawn points, gatherables, ore volumes, chest loot; without them the Find tab says so), the item-named marker
icons `map/i/{i,j,p}-*.webp`, the catalogs `data/{items,creature-templates}.json`.

## Map markers (`map/markers.rs`)
Rust port of Kiln's MIT reference `tools/kfc/kfc/mapmarkers.py` (`kfc.py map-markers`); both write the same JSON
(`real_client_markers` compares them value by value). Formats and evidence: docs/re/map.md. Read-only, from the
client's `.kfc` and the exe's reflection (field offsets by name; the five resource types' layout hashes must equal the
`.kfc`'s, else `conflict`; without reflection there are no markers and the map falls back to a local copy).

| What | From |
|---|---|
| map markers | the world scene's global chunk: entities whose template has an `ecs::MapMarker`; the category from the marker type's icon texture (`mapmarker_village` -> Villages, …; an unknown type gets its own category in "Other"); left out: `_noUI`/placeholder templates, invisible types, custom (player) types, the `FogWellPlant` labels (the Shroud Root plants are placed as objects) |
| place names (`labels`) | label-only marker types (lakes, peaks, districts; tier 1), and location markers whose name is used at most twice: villages tier 1; camps, farms, mines, dungeons, vaults, balefires, tombs tier 2; caves, enemy camps tier 3 |
| world objects | every chunk's entities by template name (prefix, then contains, case-insensitive): chests (`LootChest_Random_*` gold/silver/wood/shell, with their Level spawn value for the chest-level filter), lore (`LoreText_*`, named by `LoreReading.entryName`), Shroud Roots, grapple points, updrafts, jump pads, secret doors, puzzles, fishing spots, fossils, dragon braziers |
| names, per language | `LocaTagCollectionResource`: 15 languages; markup stripped. Category labels in other languages from a vetted source per category (the category's common marker name, or the game's text spelled like the label), else English |
| duplicates | one category: same name within 25 m or any name within 5 m (locked/unlocked spires), objects stacked within 1 m |

`markers.json` is the map's `MarkerData` (`cats`, `points: [cat, x, z, y, name, desc, level, itemIcon]`, `labels:
[name, x, z, tier]`) plus `format`, `source: "game"`, `build`, `lang` (en-US), `langs [{code, name}]`; `lang/<code>.json`
= `{cats, points, labels}` texts, index-aligned. 2.2 MB in all. `.source.json` counts: `markers`, `markerCategories`,
`placeNames`, `markerLanguages`.

**The launcher map** (`launcher/src/admin/map/engine.ts` `loadMarkers`/`setLang`, `MapView.tsx`): loads
`game/markers.json`, else a local `data/markers.json`; picks the saved language, else the system language's best
match ("de" -> de-DE), else English; "Names in" (Markers tab, Map look) switches the marker, category and place-name
texts (search finds the shown language). A line under the categories says where the markers came from. Saved category
visibility is merged with new category ids (their defaults); the "found" marks (point indices) are reset when the
marker set changes (`foundKey`: `game:<build>` or `copy`; an existing local set keeps its marks).

### Check against the Interactive Map's data (client 26430625 / data 1287369)
The comparison set is the Enshrouded Interactive Map's data (a third-party extraction of an older build).
`kfc.py map-markers --compare <markers.json>`; "game ≤25 m": game points with a
map-data point of the same category within 25 m; "data ≤25 m": the other way round; median: game point to its nearest
map-data point.

| category | game | map data | median m | game ≤25 m | data ≤25 m |
|---|---|---|---|---|---|
| Flame Shrines | 63 | 70 | 0.5 | 63 | 63 |
| Ancient Spires | 8 | 8 | 0.5 | 8 | 8 |
| Ancient Obelisks | 14 | 13 | 0.6 | 13 | 13 |
| Ancient Vaults | 26 | 10 | 1332 | 7 | 7 |
| Night Sanctums | 30 | 30 | 0.4 | 29 | 29 |
| Balefires | 9 | 8 | 0.4 | 7 | 7 |
| Hollow Halls | 6 | 6 | 0.4 | 6 | 6 |
| Dungeons (data + Sun Temples) | 37 | 32 | 4.7 | 26 | 26 |
| Hidden Tombs | 4 | 4 | 0.5 | 4 | 4 |
| Caves | 55 | 42 | 0.5 | 41 | 41 |
| Villages | 67 | 55 | 0.6 | 52 | 52 |
| Survivor Camps | 44 | 34 | 0.6 | 34 | 34 |
| Farms | 37 | 26 | 0.6 | 26 | 26 |
| Mines | 21 | 15 | 0.6 | 15 | 15 |
| Gem Forges | 21 | 18 | 0.4 | 18 | 18 |
| Shroud Lairs | 15 | 13 | 0.5 | 13 | 13 |
| Elixir Wells | 21 | 19 | 0.4 | 19 | 19 |
| Shroud Roots | 78 | 78 | 0.0 | 78 | 78 |
| Enemy Camps | 92 | 70 | 0.6 | 70 | 70 |
| Dragon Braziers | 8 | 8 | 0.0 | 8 | 8 |
| Lore (EW: 13 lore categories) | 538 | 517 | 0.0 | 493 | 487 |
| Fossils | 33 | 36 | 0.9 | 33 | 33 |
| Gold Chests | 448 | 401 | 0.0 | 401 | 401 |
| Silver Chests | 1,030 | 974 | 0.0 | 983 | 974 |
| Wooden Chests | 1,557 | 1,279 | 0.1 | 1,307 | 1,279 |
| Pearl Clams | 99 | 84 | 0.1 | 99 | 84 |
| Secret Doors | 255 | 189 | 0.0 | 192 | 189 |
| Puzzles | 64 | 59 | 0.0 | 60 | 59 |
| Updrafts | 148 | 118 | 0.0 | 118 | 118 |
| Jump Pads | 181 | 178 | 0.0 | 178 | 178 |
| Grapple Points | 1,379 | 1,222 | 0.0 | 1,241 | 1,222 |
| Fishing Spots, Fast Travel Points, Search Areas | 38, 2, 47 | 0, 0, 0 | | | |
| only in the map data: Bosses, Treasure Clues, Unique Finds, Music Sheets, Cyclops Skeletons, Drak Reliefs, Flame Mosaics, Dragon Map Pieces, Statues, Tapestries, Wall Hangings, Quest | 0 | 131, 100, 106, 11, 7, 7, 7, 4, 3, 2, 2, 1 | | | |

- Every map-data point of the shared object categories has a game point within 25 m (chests 2,653 of 2,654 within
  2 m); the extra game points are objects the older data did not have. "Ancient Vaults" differ by grouping only: the
  game's `challenge_chamber` type also holds the Ancient Lands' temples and dungeon entrances; the map data's three
  vaults without a match are hand-placed 35-640 m off (its "Cinder Vault" is the game's "Ancient Vault — Blacksmith").
- Place names: 290 (map data 293), 280 with the same name, 268 of them within 25 m.
- Sample positions (game | map data): Thornhold 4592.7, 3153.4 | 4593, 3153; Pikemead's Reach 5137.9, 4146.8 |
  5138, 4147; Ancient Spire Kindlewastes 8402.0, 2493.0 | 8402, 2493; Balefire Garrison "Silkfrost" 8157.1, 4687.4 |
  8157, 4687; Hidden Hollow Tomb 4056.4, 3992.2 | 4056, 3992; a gold chest 3952.5, 1634.4, 758.2 | identical; a grapple
  point 4402.0, 2756.3 | identical. Heights differ where the marker sits elsewhere on the object (the fast-travel spire
  marker at the top: 1047 vs 910 m).
- Chest levels: the game's Level spawn values (e.g. 15, 18, 27, 48); the map data's are coarser bands of them
  (5/10/13/15/20/27/30/35/40/45/50). The map's level filter takes ranges, so both work.
- Browser mock (Playwright, `VITE_KILN_ASSETS` = a scratch extraction): "Markers from your game's data (build
  1287369)", 34 categories, relief ready, no console errors and no failed requests (every category icon loads);
  German picked from a `de-DE` system locale ("Höhlen", "Schrein der Flamme", place names "Weißwind",
  "Dornenfeste"), search finds "Dornenfeste"; without `game/` the map shows the local set's 57 categories and no language
  choice; with both, the game's markers and the local finder.

## Items added by Kiln mods (`modded.rs`, `MODDED_RECIPE` 1)
The save editor names and draws items of Kiln mods, in saves that hold them and in its catalog. The launcher writes an
overlay next to the art, `<data>/editor-assets/modded/{items.json, icons/*.webp, source.json}`, and the editor merges it
into its item tables before it reads a save (`gamedata.js mergeModded`; launcher/src/editor/UPSTREAM.md).

Definitions (first source per GUID wins):
1. Runtime exports: `<game>/kiln/state/content-definitions.json`, written by **Kiln.Content** whenever its definitions
   change (`DefinitionsExport.cs`: every 25 ticks it compares `NewContent.DefinitionsJson()` with the last write; atomic
   tmp + rename; `{format: 1, written, definitions: […]}`). It holds every mod's code-defined and data-pack items,
   `@definitions` a server synced to this client, and placeholders. Plus the files in `$KILN_CONTENT_DEFINITIONS` (a
   path list, for tests and tools).
2. Data packs (`content/*.json` `items.add`, Kiln's JSON with comments) of the mods in the active profile
   (`kiln/profile.json`, game-side paths translated), `<game>/kiln/mods/*` (not `.disabled`) and the launcher library
   (`library/kiln/*/*`).

Per item: `id` = FNV-1a-32 of the GUID's 16 bytes in memory order (kiln.content's `NewContent.IdOf`, checked against
the lab export), `debug` = its name, `base` = its base item, names per language (`texts.<lang>.name`, else
`displayName`, else the name; `pt-*` → `pt`), `rarity` and `maxStack` from `fields`. The editor copies the base item's
entry (category, slot, stack, levels, extras, attribute lines) and replaces name, rarity, stack and icon.

Icons, first that exists: the mod's `assets/assets.json` `{"kind": "icon", guid, file}` (kiln.assets); the definition's
PNG (`iconFile` in exports, a `Z:\…` path under Proton translated; relative to the mod folder in packs); an
`item:<name>` or GUID alias (the editor extraction writes `.icon-index.json`: GUID → icon name); the base item's
extracted icon. Model and other kitbash references never change the icon (icons are baked per item GUID). PNGs are
fitted into 96x96 lossless WebP. `assets.json` `texture` entries that retexture a vanilla item icon become icon
overrides of that vanilla item. Status: `modded.current` compares a fingerprint of every input (paths, sizes, mtimes
of the exports, packs, `assets.json`, PNGs, the art's build) with the one recorded in `modded/source.json`.

Saves: the editor finds a character's lists by known item ids, so the overlay is merged before a save is read. A
stack nobody defines any more (its mod gone, no definition anywhere) cannot be told from noise by the strict read; the
editor then reads again accepting any id: such stacks show as **"Unknown item (0x…)"**, a read-only note appears, and
saving refuses unknown ids (it never writes blindly). A new character wearing fewer than four pieces is now read too
(the editor's equipment search accepts a shorter list when nothing else fits; byte-identical round trips checked).

Checked (on Xvfb, scratch data dir, the sample mods in its library, the lab's runtime export):
- the lab (server, this Kiln.Content) wrote 28 definitions; **newcontent-test 24 PASS / 0 FAIL, kitbash-test 24 PASS /
  0 FAIL** with the export on;
- the overlay: 17 items (NewItems' Frost Shard, Frost Edge, Glacier, Frost Mail, Frost Lantern; AssetSample's
  Emberseal; ModelSample's Kiln Tower Sword; kitbash-test and newcontent-test items), 3 own icons (Glacier's PNG,
  Emberseal's assets.json PNG, the Powder Ball retexture), the others their base item's icon;
- a test save (5 Frost Shards, 3 Frost Edges): the backpack
  and the selected item show "Frost Edge", Rare, with the base sword's icon; the catalog finds "Glacier" (own icon,
  Weapon · Epic) and 19 hits for "kiln" (3,962 items in all = 3,945 + 17);
- the same save with no definitions at all: "Unknown item (0xdc853d25)", the read-only note, no crash;
- the mod set changed (kitbash-test copied into the library): the overlay was rebuilt on its own within the next
  check (new `generated`, kitbash-test listed);
- first launch with an empty data dir: the art and the overlay were ready after 12 s, the map art after 22 s (debug
  build with the dev profile override), no click.

## `.source.json`
```json
{"format": 1, "kind": "editor", "source": "game", "recipe": 1,
 "game": {"path": "…/steamapps/common/Enshrouded",
          "kfcVersion": "1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54.051725Z", "build": "1287369",
          "steamBuild": "26430625", "exeSha256": "5d9ef2f1…", "layouts": "reflection"},
 "counts": {"fonts": 12, "icons": 4367, "tinted": 51, "ui": 793},
 "skipped": {"duplicate name": 1, "font over 2 MiB (CJK)": 16, "large UI image the editor does not use": 338,
             "multi-part": 206, "not 2D": 1},
 "bytes": 44248858, "extracted": "2026-11-04T19:59:47Z", "seconds": 1.0, "tool": "kiln-manage 0.2.0-dev gameart"}
```
Copies have `source: "copy"` and `from`. The fetch script's older `{from, copied}` reads as a copy.

## Game updates
- The `.kfc` version string changes with every data build; `status` compares it (header read only, cheap), so after an
  update `<GameArtAuto/>` re-extracts on its own within 15 s of the launcher seeing it.
- A changed `UiTextureResource` layout is followed through the exe's reflection (fields by name). A renamed field, a
  new pixel format (BC6H), renamed textures (`Shield` → `shield`), a reordered font list (the editor names fonts by
  index) or a changed map set (not 10x10 tiles of 1024²) would need a recipe update; the extraction then fails with a
  clear `unsupported` error or lists the gaps in `problems`, and the old art stays.
- While Steam updates the client the command refuses (`steam_updating`); the exe and data from different builds give
  `conflict`.
- Markers follow layouts by field name too. What a game update can break: a renamed component or field (no markers:
  `problems` lists `map/game/markers.json: …` and the map shows a local copy or none, the rest of the map art is
  made), a new `EntitySpawnValue` below Level (the walk refuses an unnamed bit), new marker icons (a category of their
  own under "Other", named after the icon), renamed templates (a world-object category comes out smaller or empty;
  compare the skip reasons and counts of `kfc.py map-markers --compare` before and after). Bump `MAP_RECIPE` when the
  rules change.

## Windows
Pure Rust except zstd (zstd-sys, C, already used by `saves`). `cargo xwin check -p kiln-manage --all-features --tests`
and `-p kiln-launcher` pass for `x86_64-pc-windows-msvc`. Positional reads use `FileExt::seek_read`; folders are swapped
with `rename` (works while the `kiln-asset` protocol is not holding a file open; it reads files whole). Not run on a
real Windows machine yet.

## Development
- Markers: `python3 tools/kfc/kfc.py map-markers [-o DIR] [--compare <markers.json>]` (the
  reference; prints the skip reasons and the comparison), `KILN_GAMEART_GAME=<client> KILN_GAMEART_OUT=<scratch>
  KILN_MARKERS_REF=<its -o DIR> cargo test -p kiln-manage --features gameart --release --lib -- --ignored
  real_client_markers --nocapture` (the Rust port, compared with the reference). Unit tests: `map::markers::tests`,
  `tools/kfc/tests/test_mapmarkers.py`.
- Tests: `cargo test -p kiln-manage --features gameart --lib gameart` (synthetic KFC4 + zstd + `.dat`, a synthetic PE
  with reflection records, codecs, a whole extraction, cancel, refusals). Real client (read-only, opt-in):
  `KILN_GAMEART_GAME=<client> KILN_GAMEART_OUT=<scratch> cargo test -p kiln-manage --features gameart --release --lib
  -- --ignored real_client --nocapture` (`real_client_map` for the map).
- `bun run dev` / `tauri dev` read `launcher/assets-local/` (WebKitGTK refuses custom-scheme images from
  http://localhost), not `<data>`: after the launcher made the art in a dev build, link it:
  `ln -sfn ~/.local/share/kiln/editor-assets launcher/assets-local/editor`, or use `tools/launcher/fetch-editor-assets.sh`.
- Debug builds extract about 20x slower than release (unoptimized decoders).

## Editor updates
When the Save Editor is re-vendored, check: new texture names larger than 256 px (add them to `EDITOR_LARGE_UI`), new
`ttex()` names (add a `TINTS` row: source, size, colour, alpha), new font files (`registerFonts` in `platform.js`), and
bump `RECIPE` so existing art shows as outdated. The real-client test plus a name diff against the editor's
`build-assets/game` shows the gaps.
