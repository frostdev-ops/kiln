# kiln-eml (GPL-3.0-or-later)

EML, the Enshrouded Mod Loader by Brabb3l ([kfc-parser](https://github.com/Brabb3l/kfc-parser)), as a Kiln plugin:
`kiln/plugins/kiln-eml.dll` runs unmodified EML mods (`mod.json` + `src/mod.lua`) before the game reads its data.
User and author documentation: [docs/kiln/eml.md](../../docs/kiln/eml.md).

## Licence boundary

Everything under this folder is GPL-3.0-or-later ([LICENSE](LICENSE)), like EML. It is its own Cargo workspace, not a
member of Kiln's MIT workspace, and builds its own artifact. Kiln's MIT code never includes or links code from here; it
loads the DLL and calls its C exports, described by Kiln's own MIT header
[native/core/abi/services/kiln_eml.h](../../native/core/abi/services/kiln_eml.h). Kiln's ABI structs are mirrored in
[kiln-eml/src/plugin.rs](kiln-eml/src/plugin.rs) (MIT headers, compatible with GPL-3).

Kiln's own MIT ports of EML capabilities (managed/services/Kiln.Eml/) are written from EML's documentation and observed
behaviour, never from this code.

## Layout

| Path | What |
|---|---|
| `eml/` | EML's crates from kfc-parser v0.2.0 (tag `v0.2.0`, commit `16708594d686b030d909f2d835b199d879ea9ad8`): `kfc`, `kfc-base`, `kfc-content`, `kfc-resource`, `mod-loader`, `mod-loader-base`, `mod-loader-lua`, `mod-loader-runtime`. EML's proxies (`dbghelp-proxy`, `dinput8-proxy`), CLI and GUI are not used. |
| `kiln-eml/` | The plugin: `discover` (which mods), `runner` (one fail-soft run), `logging`, `plugin` (the DLL exports), and the host runner `kiln-eml-run`. |
| `build.sh` | `cargo xwin` build to `$KILN_OUT/cmake/bin/plugins/kiln-eml.dll` (static CRT); `--host` also builds `kiln-eml-run`, `--test` runs the tests. |
| `NOTICE.txt` | Shipped next to the DLL as `kiln-eml.NOTICE.txt`, with `LICENSE` as `kiln-eml.LICENSE.txt`. |

## Changes to EML's code

Each is marked `kiln-eml:` in the source.

1. `mod-loader-base`: `ModEnvironment::load_from` / `from_parts` / `mod_paths` and `ModRegistry::load_paths`: an
   environment from an explicit list of mod folders and zips with its own cache folder, instead of `<game>/mods` and
   `<game>/.cache` only. The body of `load_mod` moved to `load_mod_at` (same checks).
2. `mod-loader-lua` `lib.rs`: the change cache tracks exactly the listed mods (`track_mods`), so switching a mod off in
   a Kiln profile re-patches. `RunResult` reports `cached` and `patched`.
3. `mod-loader-lua` `load.rs`: the KFC writer truncates the padding `.dat` containers (`truncate_containers`). Upstream
   keeps a container a previous run filled (a mod that called `create_content`, since removed) while the new header
   lists it as empty, and the game then refuses to start ("Container ..._010.dat has wrong size 4'096 (expected: 0)").
   Found on a lab server on 2026-11-07; worth reporting upstream.
4. Workspace `Cargo.toml`: release builds unwind on panic (EML aborts), so kiln-eml catches a failing run.

5. `mod-loader-lua/transaction.rs`, `load.rs`, `env/app_state.rs`: private staged writers, flushed header-last
   publication through a vanilla-header barrier, atomic backups/restores, exact resource snapshots and rejection
   of corrupt same-version backups. Default game containers are never rewritten.
6. `mod-loader-lua/cache`, `lib.rs`, `build.rs`: content fingerprints, full mtimes, resources/backups/config/options,
   code-derived deterministic build IDs, no export short circuit, no cache after inputs changed during the run.
7. `kfc-base/container/writer.rs`: explicit header flush/sync, so buffered write errors propagate; regression test
   preserving the removed-content padding fix.
8. `mod-loader-base` `registry/fs.rs`, `registry/mod.rs`: `FileSystem::probe_read` / `diagnose` and `ReadFailure` name the
   stage at which reading a mod's file fails (root metadata, directory metadata, file metadata, canonicalize,
   containment check, open, read) with the raw OS error and which parts of the path exist; `Mod::age`.
   `mod-loader-lua` `lua/error.rs`, `runner/state.rs`: a module that cannot be read carries its qualified name
   (`mods.<id>.<module>`) in the error, so `runner::failing_mod` drops only that mod, and logs the stage, the state of the
   mod folder and the time since the mod was loaded (GitHub issue 4). A module an intact mod does not have
   (`ReadFailure::missing`: root and `src` there, the file not) is `no such module: ...` instead of `failed to load
   module: ...`, so the mod that required it is dropped, not the one named. `kiln-eml/src/runner.rs` `preflight` reads every
   mod's `src/mod.lua` through that file system before the game-data phase.

9. `mod-loader-lua/cache` (`prints.rs`, `stamp.rs`, `tests.rs`), `lib.rs`, `load.rs`: each game file is fingerprinted
   once per run and not at all while the OS shows it unchanged. EML 0.2.0 CRC-hashed every `.dat`/`.kfc`/`.exe` file
   up to four times per start (the change cache, the barrier before Lua, the barrier before publication, the snapshot
   it saves): on a real client, 55 containers of about 48 GiB, a patch run read about 190 GiB and a start that changed
   nothing 48 GiB. `Fingerprints` keeps a memo of (length, last-write time, CRC-64, OS stamp) per file for the run and
   seeds it from the saved `files.json`; every barrier re-stats and re-reads only what changed. The stamp is the OS's
   file identity and change time (Linux: device, inode, `ctime`; Windows and Wine: `FileIdInfo` volume and file id,
   `FileBasicInfo.ChangeTime`), which a program cannot set back the way it can a last-write time. It is trusted only
   when it is complete, the file is on a volume where a scratch-file test in the game folder (a same-size rewrite
   with the last-write time restored) moved the change time, the change is older than three seconds (a coarse clock could give a later
   change the same stamp), and the file did not change while it was read. Everything else is read in full, as
   before; `KILN_EML_FULL_HASH=1` turns reuse off. `files.json` gains an optional `stamps` map (older files load
   without it, and older readers ignore it) and is replaced whole. The CRC-64 values are unchanged but computed with
   sixteen-way tables (`crc::Table<16>`), several times faster than the crate's default. `RunResult` and kiln's
   `eml.json` report the bytes hashed and files reused. The problem, and OS change stamps as the way out, were
   analysed by the reporter of [bisdat/kiln-mods#17](https://github.com/bisdat/kiln-mods/issues/17) (whose draft
   PR 18 explored the same idea); this code is written independently.

The plugin validates paths from each authorized root down (root included), leaving linked Steam/Wine layout
ancestors alone; it also checks final resolved containment. Profile paths resolve against `<game>/kiln`.
Wine exposes directory links by name but hides Unix file symlinks, which the installer must exclude.
The v2 service preserves the v1 prefix and `get(1)`; C++ layout and Rust v1-reader regressions verify it.
The plugin checks ABI sizes, holds an OS install lock for its process lifetime, rejects an already-running
Windows game from that install, and exposes the append-only v2 `stand_aside` reservation to SF. Interrupted stages may
remain on disk after a hard crash but never replace the live header before their data is ready. Old EML backups without
resource snapshots restore the header exactly and retain their resource prefix; unreferenced mod containers are retained.

## Updating to a new EML release

Copy the eight crates from the new tag over `eml/`, re-apply the listed changes (search `kiln-eml:` in the old copy),
update the tag and commit here, in `NOTICE.txt` and in `kiln-eml/src/lib.rs` (`EML_VERSION`), run
`build.sh --host --test` and the probe mod (docs/kiln/eml.md, "Tests").

## EMF creature portraits

The separate GPL component also provides `kiln_eml_portraits`: bounded UTF-8 JSON in/out, called by kiln-emf before
EMF mod DLLs enter. It uses the offline CPU renderer from EMF 0.6.0, commit
`bd6d90644785df80e4a61cb6ec55f9dab8e16b22`, under its MIT copyright notice (NOTICE.txt). The reader/decoder stays in
this GPL directory. It reads game archives and writes only the requested portrait cache, never game data.

`cargo build --release -p kiln-eml --bin kiln-portraits` builds the same renderer as an offline tool:
`kiln-portraits request.json`. The JSON contains `game_dir`, `file_name` (`enshrouded` or `enshrouded_server`),
`cache_dir`, and `items` with `key`, one of `template`/`model`, `frames` and `size`. Keys follow EMF's image cache
rules. There are at most 32 items, 32 Mi output pixels (33,554,432), 64 frames per item and 512 pixels per side. A cache
lock serializes processes. Symlinks/reparse points are refused in the cache and its portrait folders. New frames
and their index replace one whole folder; failures preserve the prior drawing.

The reply's `code`: 0 all drawn or current, 2 a refused request (lasting until its inputs change), 3 the run failed
(archive unreadable), 4 some items failed, 5 another generator holds the cache lock (try again later), 6 the cache is
over its 128-folder / 1024-entry / 512 MiB budget (lasting until it is cleaned). Codes 5 and 6 were added in dev.21;
before, both were 2. Use kiln-emf and kiln-eml of the same Kiln release: a kiln-emf from before dev.21 treats only 2 as
contention, and a newer kiln-emf reads the old "another portrait generator" message of code 2 as contention.

Cache ancestors must also be regular directories. On Wine/Proton, an ancestor may instead be an OS volume boundary:
Wine reports Unix mounts and its Z: root as mount-point reparse entries even though they do not redirect paths.
The renderer accepts these only when Wine is present, the entry has the mount-point tag but no reparse data, and
the volume API confirms that exact directory as the volume root. Data-backed junctions and symbolic links remain
refused, as do links inside the cache; the cache itself must be a regular directory.

Focused checks: `cargo test --release -p kiln-eml --lib portrait`. Optional read-only archive tests take
`EMF_PORTRAIT_GAME=<game folder>` and `EMF_PORTRAIT_OUT=<output folder>`. Keep their output outside the game.
