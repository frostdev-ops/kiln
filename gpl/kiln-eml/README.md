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
| `kiln-eml/` | The plugin: `discover` (which mods), `runner` (one fail-soft run), `progress` (the live state file and the progress window), `logging`, `plugin` (the DLL exports), and the host runner `kiln-eml-run`. |
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
   `FileBasicInfo.ChangeTime`), which moves with every write, also when the last-write time is put back (on Windows
   a program can still set it on purpose: this catches tools and accidents, not deliberate hiding). It is trusted
   only when it is complete, the file is on a volume where a scratch-file test in the game folder (a same-size
   rewrite with the last-write time restored) moved the change time and a 100 ns last-write time survived (FAT and
   exFAT fail), the change is older than three seconds (a coarse clock could give a later change the same stamp),
   and the file did not change while it was read. Everything else is read in full, as
   before; `KILN_EML_FULL_HASH=1` turns reuse off. `files.json` gains an optional `stamps` map (older files load
   without it, and older readers ignore it) and is replaced whole. The CRC-64 values are unchanged but computed with
   sixteen-way tables (`crc::Table<16>`), several times faster than the crate's default. `RunResult` and kiln's
   `eml.json` report the bytes hashed and files reused. The problem, and OS change stamps as the way out, were
   analysed by the reporter of [bisdat/kiln-mods#17](https://github.com/bisdat/kiln-mods/issues/17) (whose draft
   PR 18 explored the same idea); this code is written independently.

10. `mod-loader-lua/cache` (`prints.rs`, `mod.rs`, `io.rs`, `track.rs`, `tests.rs`), `lib.rs`, `progress.rs`: a start
    that has to read nothing does not (dev.22). A game file nobody needs the bytes of (`Need::Identity`: `track_game_files`
    and `track_game_inputs`; the mods' files stay `Need::Content`) is recorded by its stamp alone, where the stamp can be
    trusted by the rules of change 9: its third fingerprint number is a digest of the stamp and its name is in the new
    optional `unread` list of `files.json`. A later stamp change is a changed file (the patch runs again) instead of a CRC
    comparison with a CRC that was never computed; a saved CRC is still compared by reading the file until the start
    knows it must patch anyway (another build id, no cache, a file that already differs: `Fingerprints::patch_certain`,
    and the same on the first fingerprint that differs from the saved one). A file read earlier in a run stays a read
    file in it (`pinned`), so that its fingerprints compare. `files.partial.json` (`resume_from`, `finish`) keeps the
    fingerprints a run has read, replaced whole about every two seconds, with no build id; the next start takes them like
    saved fingerprints. `Fingerprints::announce` says before a step what it will read and why (a log line), and the
    run reports into `Progress` (`progress.rs`: stage, bytes done and to do, the file; a ticker thread logs a line every five
    seconds, a warning when a read moves no byte, and hands each change to the host's `Sink`). `run_with` takes the
    `Progress`; `run` is `run_with` with log lines only. `kiln-eml/src/progress.rs` is the sink: the live state file
    (`phase: running` and `progress`) and the `kiln.progress` window of kiln-core; `runner.rs` creates the `Progress`,
    gives the sink the mods, and says what the no-mods restore check reads.

11. `mod-loader-lua/ownership.rs`, `lib.rs`, `load.rs`, `transaction.rs`, `cache/mod.rs`, `kiln-eml/src/runner.rs`,
    `plugin.rs`: exact SHA-256 output and working-backup receipts gate automatic restoration and patch-cache skipping;
    the receipt keeps the previous owned generation until a publication's header is live. A change cache that merely
    exists never establishes ownership; once, with no receipt, the change cache of dev.20 to dev.23 is accepted as proof
    when it names the live header and resource file by content and the backups exactly (`adopt`). The change cache
    records the receipt whose output it fingerprinted (`owner`), so an unchanged start reads nothing; the full check is
    kept before a restore. A restore also removes the containers the receipt lists that the vanilla header does not
    reference. Unproven colliding DATs beyond the vanilla header's range are journalled and moved without replacement
    into `kiln/eml/displaced/<run-id>/`, after the vanilla-header barrier and before publishing any more archive data.
    They are never deleted. State/log guidance lists names, sizes and destinations on the moving start, and existing
    recovery folders on later starts. Pending displacement names cannot become ownership proof merely by matching a
    future receipt's bytes. Real archive changes invalidate only a recognized ShroudForge prepared-asset cache (SF's own shapes) under
    SF's write lock, held until the last archive write, never its startup lease, preserving settings and retaining
    exact original/replacement bytes in a recovery journal. Unknown schemas/receipts, busy locks, an SF preparation in
    progress, SF's unfinished publication journal and linked descendants (asked by name too, for links made on the
    Linux side under Wine) refuse unsafe handoffs; the selected game location itself and its ancestors may be links.
    Idle EML never restores another loader's output merely because a shared `.kfc.bak` exists, and says why it left
    differing data (`reason` in the state). Scratch names are recorded and flushed in Kiln's ownership folder before
    creating a stage or game-folder temp. Next-start cleanup consumes those records under the install lock, leaving
    foreign children and unrecorded game-folder copies intact and naming their paths and byte sizes in state/log
    guidance. State temps are reclaimed and the newest 16 historical SF handoff journals retained.
    Linux and Windows ownership tests cover these boundaries; a headless actual SF/EML/uninstall/SF dedicated-server
    round trip confirmed standalone SF reapplies unchanged-fingerprint asset mods without reinstalling.

The plugin validates paths from each authorized root down (root included), leaving linked Steam/Wine layout
ancestors alone; it also checks final resolved containment. Profile paths resolve against `<game>/kiln`.
Wine exposes directory links by name but hides Unix file symlinks, which the installer must exclude.
The v2 service preserves the v1 prefix and `get(1)`; C++ layout and Rust v1-reader regressions verify it.
The plugin checks ABI sizes, holds an OS install lock for its process lifetime, rejects an already-running
Windows game from that install, and exposes the append-only v2 `stand_aside` reservation to SF. Interrupted stages may
remain on disk after a hard crash but never replace the live header before their data is ready. Old EML backups without
resource snapshots restore the header exactly and retain their resource prefix; unreferenced mod containers are retained.

Standing aside (EML's own loader or ShroudForge's in the process, or `stand_aside` from hosted ShroudForge), the plugin
patches nothing and lists the profile's EML mods in `kiln/state/eml.json` (`discover::aside_entries`): `own-loader`
when the mod is in that loader's folder, `not-loaded` when it is only in the Library, `disabled` when the profile has
it off. EML's own loader runs every package of its folder. ShroudForge reads only its own mods folder and runs a
package there only while that package's `extended.mod.json` says `enabled: true`, on the sides its `targets` name
(none: an EML package runs on both); a legacy EML package has no such file until ShroudForge (or Kiln's launcher, for
its own copies) writes one. So under ShroudForge a mod in its folder that it has off is `off-in-shroudforge` and one
for the other side `other-side`, each with a reason that says what to do (for a file ShroudForge refuses, such as
another `schemaVersion`, that no switch helps), and the kiln.log line counts only the mods
that run (`discover::shroudforge_switch`, kiln-eml's own reader of that boolean and the targets; no ShroudForge code).
ShroudForge's own proxy with `"modloader": "eml"` in its config keeps EML's rule. In a start without ShroudForge
kiln-eml reads only `launcher` from that file: `enabled: false` there never switches a mod off for kiln-eml.

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
over its 128-folder / 1024-entry / 512 MiB budget (lasting until it is cleaned), 7 an I/O error reading or locking the
cache (a file in use, a drive briefly gone, a permission problem: try again later). A link, an invalid entry or a bad
path in the cache stays 2. Codes 5, 6 and 7 were added in dev.21; before, all were 2. Use kiln-emf and kiln-eml of the
same Kiln release: a kiln-emf from before dev.21 treats only 2 as transient (5, 6 and 7 mark the pictures failed until
the inputs change), and a newer kiln-emf reads code 2 with the old "another portrait generator" message or an OS error
text ("(os error N)") as transient.

Cache ancestors must also be regular directories. On Wine/Proton, an ancestor may instead be an OS volume boundary:
Wine reports Unix mounts and its Z: root as mount-point reparse entries even though they do not redirect paths.
The renderer accepts these only when Wine is present, the entry has the mount-point tag but no reparse data, and
the volume API confirms that exact directory as the volume root. Data-backed junctions and symbolic links remain
refused, as do links inside the cache; the cache itself must be a regular directory.

Focused checks: `cargo test --release -p kiln-eml --lib portrait`. Optional read-only archive tests take
`EMF_PORTRAIT_GAME=<game folder>` and `EMF_PORTRAIT_OUT=<output folder>`. Keep their output outside the game.
