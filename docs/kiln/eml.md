# EML mods on Kiln (kiln-eml)

EML, the **Enshrouded Mod Loader** by Brabb3l ([kfc-parser](https://github.com/Brabb3l/kfc-parser), GPL-3.0, v0.2.0),
is the engine with the most Enshrouded data mods. Kiln runs EML mods unchanged through **kiln-eml**, a separate
GPL-3.0-or-later plugin built from EML's own source (`kiln/plugins/kiln-eml.dll`, source in
[gpl/kiln-eml](../../gpl/kiln-eml/README.md)). Kiln itself stays MIT. This is the same split EMF uses for its GPL
`emf_content.dll`.

- **Players:** drop EML mods into `<game>/mods/` as with EML, or add them to a Kiln profile. You don't need EML's
  `dbghelp.dll` and, on Linux, you don't need `WINEDLLOVERRIDES`: Kiln's own proxy starts kiln-eml.
- **Mod authors:** nothing changes. Same `mod.json`, same `src/mod.lua`, same Lua API, same game types.
- **Kiln mod authors:** the MIT ports (`Kiln.Eml` assembly) are listed under [MIT ports](#mit-ports-kilneml).

## 1. How EML works (mapped from its source, v0.2.0)

### Loading
- EML ships as a `dbghelp.dll` proxy (fallback `dinput8.dll`). The game imports dbghelp, so the proxy's `DllMain`
  runs at process start, before the game's entry point. It does all its work **inside `DllMain`**, under the loader
  lock:
  1. It forwards all 242 `dbghelp` exports to the system DLL (`GetSystemDirectory`).
  2. It reads `eml.json` from the working folder (and creates it if missing).
  3. It loads `<game>/mods/*` (folders and `.zip`), runs every mod, writes the patch, and returns.
- **Any error panics.** A bad mod, a parse error or a write failure shows a message box ("Fatal error") and the
  game stops (`panic = "abort"`).
- On Linux it needs `WINEDLLOVERRIDES="dbghelp=native,builtin"`, otherwise Wine's builtin dbghelp loads and EML never
  runs.
- The runtime step (`loader.runtime.register_dll`, capability `runtime`) is a stub in 0.2.0: it is declared and logged,
  and nothing is loaded.

### KFC patching
The game data is `<exe>.kfc` (header and tables), `<exe>.kfc_resources` (resident resources) and
`<exe>_NNN.dat` (content containers).
1. **Type registry.** EML extracts the game's reflection types from the exe, and caches them as `.cache/types.json`,
   keyed by the KFC version tag. A new game build gets re-extracted automatically: no EML release is needed while
   the reflection layout holds.
2. **Change cache.** `.cache/files.json` holds the mtimes of the game's `.kfc/.dat/.exe` files and of every mod file.
   When nothing changed, the patch is skipped.
3. **Backup.** On the first run it copies `<exe>.kfc` to `<exe>.kfc.bak`, and refreshes the copy when the game
   updates (the version tag changes). Every run reads the vanilla data from the backup, so patches never stack.
4. **Lua run.** Each mod gets its own Lua 5.4 environment. A mod runs when it declares `patch` (and patching is on) or
   `export` (and export is on). Resources are typed tables. Changes are tracked, and only changed or created
   resources are serialised.
5. **Write.**
   - An incremental KFC writer appends the new resources after the game's own in `<exe>.kfc_resources`.
   - New content goes into new `.dat` containers, padded to a power of two.
   - The header in `<exe>.kfc` is rewritten.
   - The game's own `.dat` files are never touched.

### Mod format
```
mods/<folder or .zip>/
  mod.json        { "id", "name", "version" (semver), "capabilities": ["patch"|"export"|"runtime"|"runtime-register-dll"],
                    "dependencies": [{ "id", "version" (semver req), "optional" }], "authors", "description", "license", "icon" }
  src/mod.lua     the main module (require "mods.<id>" = src/mod.lua)
  src/**.lua      more modules: require("a.b") = src/a/b.lua; another mod's: require("mods.<id>.a.b")
  ...             data files the mod reads with io.read (inside its own folder only)
```
- Mod ids must be unique.
- Folders whose name starts with `.` are skipped.
- `dependencies` is parsed but not enforced. Dependencies resolve through `require`.
- Mods run in hash-map order: two mods that write the same field have no defined winner.

### Settings and config
- `eml.json` in the game folder:
  - `enable_console` (bool): opens a console;
  - `use_export_flag` (bool): turns `export` on;
  - `export_directory` (string, default `export`).
- Environment variables:
  - `EML_CONSOLE`;
  - `EML_LOG_FILE_ENABLED`, `EML_LOG_FILE_PATH` (default `./logs`), `EML_LOG_FILE_MAX`, `EML_LOG_FILE_FILTER`;
  - `EML_LOG_STDOUT_ENABLED`, `EML_LOG_STDOUT_FILTER`.
- There are no per-mod settings. Mods that want options read their own files.

### ShroudForge and EMF
- **ShroudForge** vendors kfc-parser, so its `dbghelp.dll` carries EML: it runs EML packages plus its own runtime
  services. Its `extended.mod.json` adds settings. A "legacy EML package" is one ShroudForge stamped with
  `launcher: "EML"`. ShroudForge's runtime in Kiln is the `shroudforge` component (GPL-3, `third_party/shroudforge`,
  agent `sf`). See section 6.
- **EMF 0.5** builds EML content mods through a separate GPL-3.0 `emf_content.dll` that runs at start. Agent `emf26`.
  See section 6.

## 2. kiln-eml: how Kiln runs EML

### When
kiln-eml is an **early plugin**. kiln-core's early step ([early_plugins.cpp](../../native/core/src/early_plugins.cpp))
runs right after the native mods of phase `early`:
- on the game's main thread, at the entry gate, before any game code;
- before sigdb and hooks, and in safe idle too (EML needs no hooks and works on unverified builds);
- outside the loader lock, unlike EML.

The early step:
1. Loads every `kiln/plugins/kiln-*.dll` whose file holds the export name `kiln_plugin_early` (found through a
   read-only file mapping).
2. Calls each `kiln_plugin_early`.
3. Calls each `on_ready`.

kiln-eml publishes `kiln.eml` in its `kiln_plugin_early` and runs the mods in `on_ready`, so other early plugins can
hand it mods first (section 6). It does not run with kiln.json `"plugins": false`, play-once no-mods, or an invalid
profile. Later in the same boot the plugin step loads the DLL again: `kiln_plugin_main` returns OK and does nothing.
With an older kiln-core that has no early step, the run happens there instead, still before the game's entry point.

### Which mods
The candidates, in this order (the first mod with an id wins; later ones are `duplicate`):
1. **The Kiln profile's `mods.eml` list**: `[{ "id", "version", "path", "enabled" }]`, the same shape as `mods.emf`.
   kiln-core logs `profile: unknown mod kind "mods.eml" ignored` until detect or the coordinator add the kind to the
   schema; kiln-eml reads the list from the profile text (`kiln.profile` `json()`).
2. Folders other engine plugins add through `kiln.eml` `add_mod` (source = the plugin's name).
3. Drop-ins: `<game>/kiln/eml/mods/*`, then EML's own `<game>/mods/*`. These load without a profile (legacy), or when
   the profile has `"dropins": true` or lists `"eml"`. This is the same rule as every other Kiln kind.

Each candidate is classified. Only EML mods run; everything else is logged and listed with a reason:

| Folder | State | Reason |
|---|---|---|
| `mod.json` (valid EML manifest) + `src/mod.lua`, or such a `.zip` | run | |
| no `mod.json` | skipped | not an EML mod (logged quietly) |
| `flame.json` | skipped | a Flame mod |
| `mod.json` with `dll`/`main`/`emf`/`emfMin`/`emf_api` | skipped | an EMF mod |
| `extended.mod.json` whose `launcher` is not `"EML"` | skipped | a ShroudForge package (ShroudForge's runtime runs it) |
| an unknown capability, a bad semver | skipped | not an EML manifest |
| `enabled: false` in the profile | disabled | |

### The run
This is EML's own runner (`mod_loader::lua::run`), on the listed mods:
- **Patch on.** Export is on only when `<game>/eml.json` says `use_export_flag` (kiln-eml reads that file and never
  creates it).
- **Its own cache** in `<game>/kiln/eml/cache/` (types.json, files.json, Lua definitions for editors). The cache
  tracks exactly the listed mods, so switching a mod off re-patches.
- **The backup is EML's own** `<exe>.kfc.bak`, so a player can switch between EML's loader and Kiln's without losing
  vanilla data.

It fails soft:
- **A mod whose Lua fails is switched off, and the run repeats without it.** EML names each chunk `mods.<id>.<module>`,
  so the error names the mod. The other mods still patch.
- **A failure that names no mod** (a write error, a panic): the game data goes back to vanilla from `.kfc.bak`, and the
  cache is dropped so the next start re-runs. The game and the other Kiln mods run on.
- **No EML mods (left):** if a `.kfc.bak` exists and differs, `<exe>.kfc` is restored from it byte for byte, once.
  Otherwise kiln-eml touches nothing (`idle`).

How it composes with Kiln's asset layer: EML changes the files before the game reads them, and `kiln.assets`
overrides resources and blobs in memory after the game reads them. An override in a Kiln asset pack wins over an EML
patch of the same resource.

### Real EML present: kiln-eml steps aside
Two engines must never write the KFC one after the other: the second write would drop the first one's mods.

When EML's own proxy is **loaded in this process**, kiln-eml stands aside: EML has already patched in its `DllMain`.
The same holds for ShroudForge's, which carries EML. kiln-eml enumerates the modules named `dbghelp.dll`/`dinput8.dll`
or under `kiln\chain\`, outside `\windows\`, whose file holds `EML_LOG_FILE_PATH`, and leaves out Kiln proxies.
- kiln.log says `kiln-eml: standing aside: <path> is loaded: EML's own loader patched the game files this start`.
- `state/eml.json` has `phase: "aside"`.

When EML's `dbghelp.dll` only sits in the folder but is **not loaded** (Wine without the override), kiln-eml runs the
mods, which is the Linux case EML can't handle by itself. The installer (agent `detect`) may chain EML's `dinput8.dll`
in `kiln/chain/`. It then loads, and kiln-eml stands aside, as verified on a lab server (section 5).

### Logs and state
- `kiln.log`, owner `early:kiln-eml.dll`: EML's info, warning and error lines as `[eml] ...`, one line per failed or
  skipped mod, and a summary such as
  `kiln-eml: 2 EML mod(s) loaded, 3 resource(s) patched in enshrouded_server (1263 ms)`.
- `kiln/logs/eml.log`: everything, debug included.
- `kiln/state/eml.json` (format 1, for the launcher and `Kiln.Eml`):
  `{ phase: done|failed|aside|idle, file, patched, cached, restored, ms, error, aside, mods: [{ id, version, name, source, path, state, reason, capabilities }] }`.

### Licence layout
| What | Licence | Where |
|---|---|---|
| EML's crates (8, v0.2.0 + 4 marked changes) | GPL-3.0-or-later | `gpl/kiln-eml/eml/` |
| kiln-eml plugin + `kiln-eml-run` | GPL-3.0-or-later | `gpl/kiln-eml/kiln-eml/` |
| Licence, notice, build | GPL-3.0 text | `gpl/kiln-eml/{LICENSE,NOTICE.txt,README.md,build.sh,Cargo.toml,Cargo.lock}` |
| The early-plugin step | MIT | `native/core/src/early_plugins.{h,cpp}` |
| The `kiln.eml` table (C header) | MIT | `native/core/abi/services/kiln_eml.h` |
| MIT ports (from EML's docs and behaviour) | MIT | `managed/services/Kiln.Eml/`, tests `managed/tests/Kiln.Eml.Tests/` |
| This document, the test mods' descriptions | MIT | `docs/kiln/eml.md` |

GPL code exists only under `gpl/kiln-eml/`, which is its own Cargo workspace. No MIT file includes or links it. Kiln
talks to the DLL only through its C exports. Release artifacts:
- `kiln/plugins/kiln-eml.dll`, with `kiln-eml.LICENSE.txt` (GPL-3.0) and `kiln-eml.NOTICE.txt` next to it;
- runtime component `eml`;
- the source is `gpl/kiln-eml` in the source tarball.

## 3. Coverage: EML 0.2.0's Lua API

There are 89 functions (plus the generated `integer` helpers and the game types). kiln-eml runs EML's own code, so
every function is the original: **89/89 implemented**.
- The probe mod (section 5) calls each one: **89/89 passed** offline (`kiln-eml-run`, export on), and 88/89 in the lab
  server process. `io.export` is off there, as with EML, because the lab has no `eml.json`.
- The `integer` helpers (8 types; parse/truncate/clamp/is_valid/to_string plus checked/saturating/wrapping/overflowing
  arithmetic, 512 functions) are all EML's; 13 are sampled.
- Upstream quirk, kept: `integer.<T>.parse` ignores its documented `radix` argument.

The "Kiln equivalent" column is Kiln's own API (MIT). "port" means a new MIT port of the EML function (section 4).
The others name the Kiln API that does that job; Kiln changes data in memory at run time rather than in files.

| EML function | kiln-eml | Kiln equivalent (MIT) | Tested |
|---|---|---|---|
| `typeof(v)` | EML | C# `GetType()` | probe |
| `require(path)` (local, `mods.<id>.<path>`) | EML | assembly references, `IModExports` | probe (both forms) |
| `builtin` table | EML | n/a | probe |
| `loader.is_client` / `is_server` (fields) | EML | `api->side()`, `Game` | probe |
| `loader.features` (field) | EML | n/a | probe |
| `loader.has_mod(id)` | EML | **port** `Eml.IsLoaded(id)` (+ `Mods.*` for Kiln mods) | probe; C# test |
| `loader.runtime.register_dll(path)` | EML (stub upstream) | native mods (`native.json`) | probe |
| `game.version` (field) | EML | `api->build_info` | probe |
| `game.types.get(name or hash)` | EML | `Kiln.Game` generated types (typegen) | probe |
| `game.types.get_by_qualified_name` | EML | `Kiln.Game` | probe |
| `game.types.get_by_qualified_hash` | EML | `Assets.TypeHash(name)` | probe |
| `game.types.get_by_impact_name` | EML | n/a | probe |
| `game.types.get_by_impact_hash` | EML | n/a | probe |
| `game.types.get_all` | EML | `Kiln.Game` | probe |
| `game.types.of(value)` | EML | n/a | probe |
| `game.assets.get_resource(guid, type, part)` | EML | `Assets.ReadResource(key)` | probe |
| `game.assets.get_resource_parts(guid, type)` | EML | `Assets.ListResources(type)` | probe |
| `game.assets.get_resources_by_type(type)` | EML | `Assets.ListResources(type)` | probe |
| `game.assets.get_all_resources()` | EML | `Assets.Dump(spec)` | probe |
| `game.assets.get_resource_types()` | EML | `Kiln.Game` | probe |
| `game.assets.create_resource(value, type[, guid, part])` | EML | `Assets.Register(key, bytes)`, typed blocks | probe (both forms) |
| `game.assets.get_content(guid or ContentHash)` | EML | `kiln.assets` blob read | probe |
| `game.assets.get_all_contents()` | EML | n/a | probe |
| `game.assets.create_content(buffer)` | EML | `Assets.AddBlob(bytes)` | probe |
| `Content:read_data()` | EML | `kiln.assets` blob read | probe |
| `game.guid.hash(guid)` | EML | n/a | probe |
| `game.guid.from_content_hash(h)` / `to_content_hash(guid)` | EML | `ContentHash` in Kiln.Assets | probe |
| `hasher.fnv1a32(v)` | EML | **port** `DataHash.Fnv1a32` | probe; C# test |
| `hasher.crc32(v)` | EML | **port** `DataHash.Crc32` | probe; C# test |
| `hasher.crc64(v)` | EML | **port** `DataHash.Crc64` | probe; C# test |
| `buffer.create(capacity)` | EML | **port** `new DataBuffer(capacity)` | probe; C# test |
| `buffer.wrap(string)` | EML | **port** `DataBuffer.Wrap` | probe; C# test |
| `Buffer:head()` / `head(pos)` | EML | **port** `DataBuffer.Head` | probe; C# test |
| `Buffer:tail()` / `tail(pos)` | EML | **port** `DataBuffer.Tail` | probe; C# test |
| `Buffer:remaining()` / `capacity()` | EML | **port** `Remaining` / `Capacity` | probe; C# test |
| `Buffer:reset()` / `reserve(n)` / `skip(n)` | EML | **port** `Reset` / `Reserve` / `Skip` | probe; C# test |
| `Buffer:order()` / `order(o)` | EML | **port** `Order` (`ByteOrder`) | probe; C# test |
| `Buffer:copy(src, offset, length)` | EML | **port** `Copy` | probe; C# test |
| `Buffer:close()` | EML | n/a (GC) | probe |
| `Buffer:to_string()` | EML | **port** `ToString()` | probe; C# test |
| `Buffer:read_bool/i8/u8/i16/u16/i32/u32/i64/u64/f16/f32/f64` (12) | EML | **port** `ReadBool` ... `ReadF64` | probe; C# test |
| `Buffer:read_string(n)` | EML | **port** `ReadString` | probe; C# test |
| `Buffer:write_bool ... write_f64` (12) | EML | **port** `WriteBool` ... `WriteF64` | probe; C# test |
| `Buffer:write_string(s)` | EML | **port** `WriteString` | probe; C# test |
| `Buffer:read_resource(type)` / `write_resource(type, v)` | EML | `Kiln.Game` typed structs | probe |
| `image.create(w, h)` | EML | n/a | probe |
| `image.decode(buf, fmt)` / `encode(img, fmt)` | EML | `Assets.RegisterIcon(png)`, `OverrideTexture(png)` | probe (png) |
| `image.decode_texture` / `encode_texture` | EML | `Assets.OverrideTexture` | probe (R8G8B8A8) |
| `Image:get_pixel/set_pixel/get_pixel_packed/set_pixel_packed` | EML | n/a | probe |
| `io.read` / `read_to_string` | EML | `System.IO`, `ModContext` folders | probe |
| `io.list_files` / `exists` / `is_file` / `is_directory` | EML | `System.IO` | probe |
| `io.name` / `name_without_extension` / `extension` / `parent` / `join` | EML | `System.IO.Path` | probe |
| `io.export(path, data)` | EML (needs `use_export_flag`) | `ModContext.DataDirectory` | probe (offline) |
| `integer.*` (512) | EML | C# fixed-size integers, `checked`/`unchecked` | probe (13 sampled) |

**Counts:**
- implemented: 89/89, all by EML's own code;
- probe: 89/89 offline, 88/89 in the lab (the 89th, `io.export`, is off there);
- MIT ports: 42 functions (`DataHash` 3, `DataBuffer` 38, `Eml.IsLoaded` 1);
- an existing Kiln API does the job for 36 more; 11 have no Kiln equivalent (impact-name lookups, `types.of`,
  `get_all_contents`, `guid.hash`, `Buffer:close`, pixel access), and none is needed so far.

## 4. MIT ports (`Kiln.Eml`)
These are Kiln's own code (`managed/services/Kiln.Eml/`, deployed to `kiln/host/` like the other service assemblies),
written from EML's documentation and from what the probe observed, never from EML's code:
- `DataHash.Fnv1a32/Crc32/Crc64` (string or bytes): the algorithms EML's docs name (FNV-1a 32,
  CRC-32/ISO-HDLC, CRC-64/ECMA-182).
- `DataBuffer`: EML's buffer model. Writes append at `Tail` and reads consume from `Head`. It has `Order` (little or
  big), every fixed-size type including `Half`, strings, `Skip/Reset/Reserve/Copy`, `Wrap`, `ToString`. EML Lua code
  ports to C# line by line.
- `Eml.Status` / `Eml.IsLoaded(id)` / `Eml.Load(path)`: this start's `state/eml.json`. These are the port of
  `loader.has_mod`, plus the run's result for Kiln mods that build on an EML mod.

```csharp
if (Eml.IsLoaded("better_stamina")) Log.Info("EML mod better_stamina is active; skipping our own stamina tweak");
var b = new DataBuffer(); b.WriteU32(DataHash.Fnv1a32("keen::ItemInfo")); // 0xb5ce8765
```

Not ported, on purpose:
- typed game data (`read_resource`, `create_resource`, `types`): Kiln has its own typed path (`Kiln.Game` generated
  types, typed blocks, `kiln.assets` overrides at run time);
- image codecs: .NET libraries do that;
- a C#/Lua pre-game KFC writer: kiln-eml is the single writer of the game files (section 6), and a second writer
  would conflict.

## 5. Tests and evidence

### Host
- Rust tests: `gpl/kiln-eml/build.sh --test`. There are 8 (discovery, classification, profile `mods.eml` and
  drop-ins, de-duplication, naming the failing mod, idle, restore safety, missing game files).
- C# tests: `dotnet test managed/tests/Kiln.Eml.Tests` runs 8 (hash check values, buffer round trips, byte order,
  cursors, the state reader).
- `kiln-eml-run --game <dir> [--mod <path>]... [--profile <json>] [--no-patch] [--export --export-dir <d>] [--state <file>]`:
  the plugin's discovery and run on Linux, against a game folder (use a reflinked copy, never a real install). It
  prints the state JSON.

### Offline content check (2026-11-07, server build 1287369)
Test mods (in `/mnt/Dongus/kiln-wave/eml/testmods/`, not in the repo):
- `kiln_eml_slice` sets `keen::BalancingTable.playerBaseStamina` = 777;
- `kiln_eml_verify` (export) reads it back;
- `kiln_eml_probe` calls the whole API;
- `kiln_eml_broken` has a deliberate Lua error.

| Step | Result |
|---|---|
| slice on a reflinked copy | `playerBaseStamina before = 100` → `Applied 1 patches`, 1.8 s with the type extraction |
| second run | `No changes detected, skipping patching` (`cached: true`) |
| verify on the patched files | `playerBaseStamina = 777`; on the vanilla files `= 100` |
| probe | `kiln_eml_probe: 58/58 passed` (58 checks cover the 89 functions) |

### Lab server `wv-eml-1` (UDP 27100)
The deploy is minimal and native only (proxy, kiln-core with the early step, sigdb, kiln-eml; `clr: false`). These
are kiln.log lines:
```
[I 14:35:04.510] [early:kiln-eml.dll] [eml] kiln-eml 0.1.0 (EML 0.2.0) starting
[I 14:35:04.511] [kiln] service kiln.eml v1 published by early:kiln-eml.dll
[I 14:35:04.734] [early:kiln-eml.dll] [eml] kiln_eml_slice: playerBaseStamina set to 777
[I 14:35:04.769] [early:kiln-eml.dll] [eml] Applied 1 patches
[I 14:35:04.796] [early:kiln-eml.dll] kiln-eml: ...\mods\not-an-eml-mod skipped: an EMF mod (mod.json names a dll/main or an EMF API version)
[I 14:35:04.796] [early:kiln-eml.dll] kiln-eml: 1 EML mod(s) loaded, 1 resource(s) patched in enshrouded_server (283 ms)
[I 14:35:05.834] [kiln] boot complete; handing control to the game
```
- The server reached `Switching state from LoadEcsScene to Run`, and its `.kfc` read back 777.
- Restart: `1 EML mod(s) loaded; nothing changed since the last run, the game files keep their patches (24 ms)`.
- Broken mod next to the slice:
  `EML mod failed; running the others without it mod_id=kiln_eml_broken error=runtime error: [string "mods.kiln_eml_broken"]:2: attempt to index a nil value`.
  The slice stayed loaded.
- Probe in the server process: `kiln_eml_probe: 58/58 passed, 0 failed`, then
  `2 EML mod(s) loaded, 3 resource(s) patched (1263 ms)`. The server ran with resources and content the probe
  created.
- EML's own `dinput8.dll` (built from upstream v0.2.0 for the test) chained through `kiln/chain.ini [chain]`: EML
  patched in `DllMain` (its own log: `Applied 1 patches`), and kiln-eml logged
  `standing aside: ...\kiln\chain\eml\dinput8.dll is loaded: EML's own loader patched the game files this start`.
- All mods removed: `no EML mods; the game data was restored to vanilla (EML's .kfc.bak)`. `enshrouded_server.kfc`
  is byte-identical to the base install's again.
- A profile with `mods.eml: [{ path: Z:\...\library\kiln_eml_slice }]` and drop-ins off: the slice ran from the
  library (`source: profile`), and the EMF folder in `mods/` was not looked at.
- **Bug found and fixed** (change 3 in the README): after the probe's created content was removed, the server
  refused to start (`Container ..._010.dat has wrong size 4'096 (expected: 0)`). kiln-eml now truncates the padding
  containers. EML 0.2.0 has the same bug; it is worth reporting upstream.

## 6. Shared core: ShroudForge and EMF content
There must be **one writer of the game's KFC files per start**: two would undo each other. kiln-eml is that writer.
- **ShroudForge (agent `sf`).** ShroudForge's EML-format packages should reach kiln-eml through `kiln.eml` `add_mod`,
  from a `kiln_plugin_early` export in kiln-shroudforge.dll. Don't let the GPL `shroudforge` runtime patch the KFC
  itself. kiln-eml skips SF-own packages (`extended.mod.json` with a launcher other than "EML") unless they are added
  this way.
  - If the SF runtime must patch for SF-specific capabilities, it has two options: run before kiln-eml and pass its
    output folder as a mod, or tell kiln-eml to stand aside. The second needs a `kiln.eml` v2 slot (`stand_aside(reason)`);
    ask for it.
  - kiln-eml already stands aside for a loaded ShroudForge `dbghelp.dll`.
- **EMF content mods (agent `emf26`).** Same contract: kiln-emf's early export calls `add_mod` for each EMF mod that
  ships EML content, so no `emf_content.dll` is needed under Kiln.
- How a peer gets the table in its own `kiln_plugin_early`:
  `GetModuleHandleW(L"kiln-eml.dll")` → `GetProcAddress("kiln_eml_service")` → `get(1)`. The core loads every early
  DLL before it calls any `kiln_plugin_early`, so file-name order doesn't matter. Later, `api->service("kiln.eml", 1)`.
  `state_json()` returns the result.
- The SF and EMF runtimes are GPL too, so they may also link the crates in `gpl/kiln-eml/eml` directly. That is their
  call, inside their own GPL folders. The C table keeps the MIT plugins (kiln-shroudforge, kiln-emf) free of GPL code.

## 7. Client checklist (for the user)
The client has its own data files, so a client check must run on a copy. These are steps for the user, who does all
client testing:
1. Deploy Kiln with the `eml` component (or `tools/client/client.sh deploy --real` with a build that has
   `kiln/plugins/kiln-eml.dll`).
2. Put one small EML mod into `<game>/mods/`, for example one that raises max stamina or stack sizes. A mod from
   Nexus is fine.
3. Start the game. kiln.log should show `early plugin kiln-eml.dll` and
   `kiln-eml: 1 EML mod(s) loaded, N resource(s) patched in enshrouded (...)`. On Linux no `WINEDLLOVERRIDES` should
   be needed.
4. In a LOCAL world, check the mod's effect (for example a higher max stamina shown in the character screen).
5. Start again: `nothing changed since the last run` and a fast start.
6. Remove the mod and start: `the game data was restored to vanilla`. Steam's "verify files" should find nothing
   (EML's `.kfc.bak` may remain; it is not a game file).
7. Optional: install EML's own `dbghelp.dll` with the Wine override. kiln.log should say `standing aside`, and the mod
   still works (EML applied it).

Mods we need from the user (Nexus downloads, which agents can't fetch):
- 2-3 popular EML mods: one stat or balancing mod, one with `create_resource`/`create_content` (new items), one with
  several Lua modules. They are for the lab regression set.
- One mod that has both an EML package and EMF parts.

## 8. Open points
- The profile kind `eml` in `schema/profile.schema.json` and the launcher (detect / coordinator). kiln-eml already
  reads `mods.eml`.
- Library detection: `kiln-manage/src/engines.rs` treats a `mod.json` with `capabilities` as a ShroudForge package.
  Real EML mods have `"capabilities": ["patch"]`, so they show as ShroudForge (for detect).
- The launcher could read `state/eml.json` and show each EML mod's state. When the player turns kiln-eml off, it could
  offer EML's restore (copy `.kfc.bak` over `.kfc`).
- `kiln.eml` v2 `stand_aside(reason)` if the SF runtime keeps its own KFC writer.
