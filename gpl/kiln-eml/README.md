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
| `kiln-eml/` | The plugin: `discover` (which mods), `runner` (one fail-soft run), `progress` (the live state file and the progress window), `logging`, `plugin` (the DLL exports), the host runner `kiln-eml-run` and the read-only archive inspector `kiln-eml-inspect`. |
| `build.sh` | `cargo xwin` build to `$KILN_OUT/cmake/bin/plugins/kiln-eml.dll` (static CRT) and `$KILN_OUT/eml-inspect/windows-x86_64/kiln-eml-inspect.exe`; `--host` also builds `kiln-eml-run` and the Linux `kiln-eml-inspect`, `--test` runs the tests. |
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
    guidance. A writer holds its stage from creation until it is dropped and stages, publishes and cleans only through
    that hold (it lists the stage through a handle opened relative to the hold: Wine 11.0, GE-Proton11's, refuses a
    listing by path while the hold is open); the emptied stage is removed through the same handle on Windows, and kept
    (never removed by path) on the host builds. State temps are reclaimed and the newest 16 historical SF handoff journals retained.
    Linux and Windows ownership tests cover these boundaries; a headless actual SF/EML/uninstall/SF dedicated-server
    round trip confirmed standalone SF reapplies unchanged-fingerprint asset mods without reinstalling.

12. `mod-loader-lua/startup_host.rs`, `lib.rs`, `env/app_state.rs`, `transaction.rs` (one witness accessor): a private
    startup facade over the existing publisher (the prepared archive and the prepared run). `StartupHost` binds a caller-validated baseline, type
    registry and held install lock; `begin_stage`/`StagedArchive::prepare` give one private, read-validated
    `PreparedArchive` per host. `StartupHost::prepare_eml` runs EML's unchanged Lua pass on that one staged writer
    (`AppState::hosted`: no second baseline capture, writer or lock, every archive reader and the VM closed before it
    returns) and stops before publication, returning the archive and a separate `CacheCommit`; a verified cache hit
    returns the already-published identity instead and stages nothing. `PreparedArchive::publish` is the sole
    publisher and returns that generation's `PublishedArchive`; `CacheCommit::finish_after_publish` saves `files.json`
    only for that exact generation, while its receipt is still live and every mod and game input is as before the
    Lua pass. A caller's BASE origin (`BaseOrigin`, keyed to the captured `BaselineIdentity`) travels verbatim to the
    prepared and published identities and is never created, upgraded or persisted here. `run`/`run_with` compose the
    same check, Lua pass, publication and cache completion internally, with unchanged behaviour and texts.
    `StagedArchive::lend` lends that one stage's writer to a VM whose functions must own what they capture:
    `LentStage::writer` hands out `StageWriter` handles to the SAME writer, `LentStage::prepare` takes it back before
    preparing as `StagedArchive::prepare` does, and from then on (or once the lent stage is dropped, which aborts)
    every handle refuses to write. `StartupHost::reference_reader` opens the captured baseline's reader, as a prepared
    EML run reads it. `LentStage::prepare_with` takes the writer back from every handle first, then lets the
    caller write into that same stage (the SF pass's dirty resources), then prepares; its error aborts the stage.
13. `mod-loader-lua/failpoint.rs`, `publication_tests/`, and `failpoint!` boundaries in `transaction.rs`, `load.rs`,
    `ownership.rs`, `cleanup.rs`, `displacement.rs`, `lib.rs`, `env/app_state.rs` and `startup_host.rs` (the
    lent `StageWriter`'s writes and `prepare_with`'s take-back and serialized-resource writes):
    named publication boundaries that exist only in mod-loader-lua's own unit tests (`cfg(test)`; no production
    failpoint). The kill/error matrix runs a first publication, a growing and a shrinking replacement (with SF's
    applied state and a foreign container in the way), the no-mod restore and the SF-dialect pass's lent stage on the
    startup facade (a content written through the VM's handle, the stage taken back, the edited resource serialized
    into it, prepared and published), and at every boundary each passes (235, every repeated file its own) injects an
    I/O failure and, separately, kills a child test process stopped exactly there. The live archive must then validate and be the old
    set, the vanilla base or the complete new set; recovery runs twice, then a no-mod restore. The children are created
    only while no thread of the test process holds a file lock (`test_gate::spawn` and `LockHold`: on Linux a child
    shares its creator's flock descriptions until its exec), and a test of the matrix fails on a lock or a process
    creation in the EML sources that bypasses this.
14. `kiln-eml/src/sf_pass.rs` (the retry driver, adapted from ShroudForge at Kiln's fork pin 2df1de68, MIT, bonsaibauer:
    `retry_without_failed_mod.rs`, `apply_asset_mods.rs`, `name_failing_mod.rs`, `ModEnvironment::without`): the
    SF-dialect startup pass driver. Only when the frozen launch plan's `startupDialect` is `shroudforge`, a pass that
    failed in one package's own entrypoint runs once more, from the clean baseline (a new `StartupHost` generation; the
    failed one published nothing), without that package and every package that needs it (directly, through another,
    or through a switched-on optional dependency that drops out with it, as the fork's planner). Not retried: an I/O error in
    the error's chain, a `(os error N)` or access-denied text, and any failure no package raised. A second failure
    keeps the first error; the record gets `skipped` and `retryError`, and the log the fork's exact `startup-assets`
    lines. The EML dialect keeps `runner.rs`'s own behaviour. Kiln mode wires it to the real SF pass.
15. `kiln-eml/src/sf_startup/` (the type session, adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68, MIT, bonsaibauer:
    `lua/bindings/game/{mod,types,resolve_type}.rs`, `reflection/traverse_reflected_types.rs`, `session/mod.rs`'s Type
    cache, `lua/readonly_collections.rs`, `register_functions.rs`; `format_adapter.rs` adapts `container/resource_lookup.rs`
    of bonsaibauer's kfc-parser fork, ShroudForge's GPL-3.0-or-later `kfc-parser` submodule, commit db1580a9): the
    SF-dialect startup pass's type session and `game.version` / `game.types`. One session per startup VM holds the
    registry, the selected index's data version and the Type cache; each package gets its own `game.types` table over
    it. Exact qualified/Impact-name lookup, collision-refusing hash domains, `resolve`/`is_a` selectors, `get_all`
    including DS by default, Type metadata and read-only collections as SF has them; `of` answers through recognizers
    the typed-value builders add (the value wrappers and the asset session). The pass composes it (item 23); unused by the shipped DLL until kiln mode
    activates that. `mod-loader-lua`'s `ownership.rs` and `displacement.rs` now sort JSON keys explicitly (see "Lua
    dependency alignment").
16. `kiln-eml/src/sf_startup/resource_values/` (the codec, adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68, MIT,
    bonsaibauer: `lua/resource_values/{validate_resource_values,convert_resource_values,value_path}.rs`, the container
    walks of `lua/resource_values/owned_values.rs`, the Guid cast of `lua/bindings/game/content_id_casts.rs`,
    `lua/values/encode_owned_value.rs`): the typed value codec. Validation of Lua values against reflected types (plain
    tables read with `Table::get`/`len`/`sequence_values` as SF reads them, exact `at <path>: ...` errors), conversion
    to kfc values and resource bytes, and decoding back. Value wrappers and asset userdata reach it only through the
    `TypedObjects` seam (the value wrappers, the asset session). Where SF panics (DS kinds, a faulty registry) it returns an error instead; so do a
    value nested more than 128 levels deep and a cyclic type chain, where SF overflows its stack or kfc loops.
17. `kiln-eml/src/sf_startup/resource_values/{owned,mapped,objects}.rs` (the value wrappers, adapted from ShroudForge 1.7.9 at
    Kiln's fork pin 2df1de68, MIT, bonsaibauer: `lua/resource_values/{owned_values,mapped_values,mod}.rs`): SF's six
    value wrappers (owned and mapped struct/array/variant) and the production `TypedObjects`. Gets return the stored
    object, assignments and insertions validate and clone (the fork's field/element-type setter, not 1.8's enclosing
    type), mapped values read lazily and never change their original bytes, `pairs` of a mapped array walks its original
    length. `ValueTables` is the typed half of `table.insert/remove/clear` behind sfi-core's dispatcher port. Resource and
    Content GUIDs come through `AssetUserdata`, implemented by the asset session's `AssetIdentities` (`game/resource.rs`).
18. `kiln-eml/src/sf_startup/game/{assets,casts,content,resource}.rs` (the asset session, first half, adapted from ShroudForge
    1.7.9 at Kiln's fork pin 2df1de68, MIT, bonsaibauer: `lua/bindings/game/assets/{mod,access_file_resources,
    access_file_content,content,resource}.rs`, `lua/bindings/game/content_id_casts.rs`, `session/asset_resources.rs`):
    `game.assets`' archive catalog and content functions over one asset session per startup VM (the captured
    baseline index, its reader, one resource-record cache, the staged-content copies and the lent `StageWriter`).
    As SF, not EML: lookups and lists see the reference catalog only (a content created in the pass is not found by
    `get_content`, though its Content reads its own staged bytes); `get_resource_parts` returns ascending parts.
    `create_content` writes to the one stage at once. The asset session (same sources plus `startup/commit_asset_changes.rs` and
    `lua/bindings/buffer/mod.rs`): `Resource.data` (lazy, one value per record, nested edits dirty, the setter validates
    and copies after PATCH), `create_resource` (explicit or random identity; a duplicate of the reference catalog OR the
    cache is refused, where SF checks its cache only), `AssetSession::prepare` (reader closed, writer taken back from
    every handle, every replaced or edited record serialized into that stage in resource-id order, prepared; an error
    aborts the attempt) and `sf_startup/buffer_resource.rs` (`Buffer:read_resource`/`write_resource` through the core
    Buffer's port). Unread parts of an edited value convert through a bounded copy of kfc's `Value::from`
    (`Converter::mapped`: the codec's depth limit, at most 8 values per source byte plus 64 Ki, no array room reserved from
    a stored count, and the wrappers' chain check on every struct, field and array type before kfc lists or maps it; kfc's own
    recursion overflowed the stack on self-referencing bytes and looped or overflowed on a faulty cyclic chain). The pass
    composes it (item 23); unused by the shipped DLL until kiln mode activates that.
19. `kiln-eml/src/archive_service.rs`, `archive_service/{view,registry,errors}.rs` (the archive table's metadata, kiln-eml's own code):
    the GPL provider of kiln.eml.archive v1 (`native/core/abi/services/kiln_eml_archive.h`, declared through the
    generated `kiln-abi` bindings). The early export `kiln_eml_archive_service` and the table published as
    `kiln.eml.archive` (`plugin.rs`, `start`) are the same process-lifetime table. It implements the metadata
    subset (acquire, identity, release, registry_metadata) and the per-call error channel (error_copy, error_release),
    reported as the complete `eml.archive.metadata` and `eml.archive.errors` groups with their member rows; item 23
    adds the read group. The codec and attribute slots are null and `eml.archive.codec`/`.attributes` are reported
    unavailable. Views bind an owner to
    a generation the startup controller authorized (`authorize`, every pin equal, no paths) and read an immutable registry
    snapshot (lossless ordered type metadata as `game.types` has it); no file, reader, lock or Lua is touched. The
    last open view or call frees a retired generation's snapshot. `registry_id` is per snapshot; `registry_sha256` is
    Kiln's own digest: SHA-256 over every byte a page returns except its pin (both version texts and each type's JSON
    or failure text, tagged and length-prefixed, in registry order; `registry.rs` has the exact layout).
20. `kiln-eml/src/archive_values.rs`, `archive_values/{arena,lifetime,node,input,operations,packet}.rs` (the typed graph;
    `node.rs` and `input.rs` adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68, MIT, bonsaibauer, through
    items 16 and 17: `lua/resource_values/{owned_values,mapped_values,mod,validate_resource_values,
    convert_resource_values}.rs`, the Guid cast of `lua/bindings/game/content_id_casts.rs`; the rest kiln-eml's own):
    the typed graph behind kiln.eml.values v1 (`native/core/abi/services/kiln_eml_values.h`), in Rust. The six
    wrappers as nodes with SF's caches, laziness, aliases, clones, dirtiness and iteration; a validator that reads Lua
    input only through a protected host reader, in SF's order and count; arenas with process-unique never-reused tokens,
    any-thread close and worker-side drain, host-object pins where SF's slots keep a Lua value; and the frozen payload
    and error packets (`schema/sfi/eml-values/`).
21. `kiln-eml/src/inspection.rs`, `kiln_eml_inspect.rs` (the inspector, kiln-eml's own code) and `mod-loader-lua`
    `ownership.rs` `readonly` (receipt, seal and path-check views for it; `read_receipt` shares its `understood` rule):
    the read-only archive inspector, see "Archive inspector" below.
22. `kiln-eml/src/archive_values/{abi,reader}.rs`, `plugin.rs` (the C table, kiln-eml's own code), the `archive_service`
    registry source (`authorize_with_registry`, `registry::digest`) and `kfc-base` `reflection/registry.rs` (`Clone` on
    `TypeRegistry`, marked `kiln-eml:`): kiln.eml.values v1 as its C table, registered as `kiln.eml.values` right
    after `kiln.eml.archive` with `eml.values` and one capability row per member. `arena_create` copies the kfc
    registry the startup controller authorized beside the generation's snapshot (once per arena, on its worker) and refuses
    a copy whose digest is not the view's `registry_sha256`; it never builds a registry from the snapshot JSON. The
    host's `KilnEmlLuaReaderV1` is admitted by its covered prefix only and checked before every callback; `wake` gets
    the control token and every member refuses from inside it. A worker that exits with its arena open revokes it
    without calling the host, and LOADED_SNAPSHOT roots are recorded weakly (the frontend's ResourceInfo keeps them).
    Every member runs under `catch_unwind`; only the header's records, tokens and the frozen packets cross.
23. `kiln-eml/src/sf_startup/{plan,common,runner}.rs` (the pass composition, adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68,
    MIT, bonsaibauer: `startup/run_startup_mods.rs`, `lua/mod_runner.rs`, `lua/require_mod.rs`, `lua/bindings/mod.rs`,
    `lua/eml/v1/create_loader_api.rs`, `modloader/mod_manifest_types.rs` `requires_pregame`), `game/assets.rs`
    (`AssetSession::with_objects`) and `mod-loader-lua` `startup_host.rs` (`StartupHost::registry`, `baseline`,
    `stem`): the SF-dialect startup pass end to end, driven by its host. `StartupPlan` freezes one attempt's inputs:
    the discovered packages (`loader.has_mod`), the admitted load order, the global PATCH/EXPORT, side, the 1.7.9
    dialect, a Kiln-owned export root (refused when it is or contains the game folder, `kiln`, `kiln/eml`, `mods` or
    `shroudforge`, or lies inside `kiln/eml`, `mods` or `shroudforge`; the root and each of those folders compared
    where they resolve, so a linked or junctioned `mods` protects its target), the build's runtime operation catalog and
    the locale. `run_attempt` runs one attempt on a `StartupHost` generation: the stage and its lent writer (PATCH
    only), the baseline reader after it, ONE startup VM (sfi-core's `LuaVm`) with the wrappers' `ValueTables` and the asset session's
    `BufferResources` attached before any Lua access, one type and one asset session, every package registered in order
    (`game.version` and `game.assets` on the core's `game` table, then sfi-core's `compose::install` with `game.types`
    from the session and the runtime registrar's pregame `runtime`, `runtime.types` the same table, then SF's pregame
    `loader.features.runtime.dll` = false and a `loader.runtime.register_dll` that refuses), the entrypoints SF's runner
    selects (`requires_pregame` and the package's patch or export against the global features), then
    `AssetSession::prepare` with the VM alive, the VM closed, and the prepared generation handed back for
    `PreparedStartup::publish`, the sole publisher. A package's Lua error is `PassError::Package` (the retry driver retries without
    it); any other failure, or an error from the caller's `on_step` at any step, aborts the stage and leaves the game
    folder as it was. What the stage cannot undo (the packages entered; when the pass exports, any of them may have
    exported, as `io.export` follows the global EXPORT only) is recorded for the startup controller. As in ShroudForge 1.7.9, a package
    whose id contains a dot runs its entrypoint (looked up by exact id) but cannot `require` its own modules, and no
    other package reaches it as `mods.<id>`: module paths split at the first dot ("mod not found: dot"). Every 1.8-only
    binding stays absent. Unused by the shipped DLL until the startup controller seals admission and kiln mode activates the pass.
24. `mod-loader-lua` `test_gate.rs` (the gate of item 13, moved out of `failpoint.rs`), `Cargo.toml` (the dev-only
    `test-gate` feature), `ownership.rs` and `startup_host.rs` (their holds under `cfg(any(test, feature =
    "test-gate"))`), `kiln-eml/Cargo.toml` (`[dev-dependencies]`) and `kiln-eml/src/{gate_tests,runner,portrait_export,
    plugin,plugin_records_tests,kiln_mode_tests,runner_ownership_tests}.rs`: the test lock/spawn gate in kiln-eml's unit
    tests too. That test binary runs itself (and `cp`) as a child while other test threads take and release the
    install, portrait cache and ShroudForge locks. Each of those is taken under a `LockHold` (the install lock keeps
    its hold in `GAME_LOCKS` as a `KeptHold`; a run holds one from before `RUN_LOCK`), every process is created through
    `test_gate::spawn`, the two tests that check the install lock from a second process create it before they lock,
    and the tests release the install locks they take, also when they fail (`runner::GameLockRelease`). Only kiln-eml's
    `[dev-dependencies]` enable the feature, so no normal build (kiln-eml.dll included) has the gate. `gate_tests.rs`
    fails on a lock or a process creation in kiln-eml's sources that bypasses it, and on a mod-loader-lua hold built
    for `cfg(test)` alone. The gate covers two test binaries: mod-loader-lua's unit tests and kiln-eml's library unit
    tests (`cargo test -p kiln-eml --lib`). kiln-eml's integration test binaries (`kiln-eml/tests/*.rs`) are not
    gated: a `cargo test` build compiles mod-loader-lua's holds into them too, but nothing there creates a process
    through the gate, so they never wait.
25. `kiln-eml/src/archive_service/read.rs`, `authorize_with_archive` in `archive_service/view.rs` and the RESOURCE
    roots in `archive_values/abi.rs` (the archive table's read group, kiln-eml's own code; ShroudForge 1.7.9's visibility rules, none of its
    code): kiln.eml.archive v1's read group (resource_json, find_resource, list_resources, read_resource,
    content_read_range, list_contents), reported as `eml.archive.read` with its six member rows. A generation
    authorized with an `ArchiveBacking` reads the captured reference index (`<stem>.kfc.bak`) over the resources file
    and DAT containers, plus its own entries: the staged bytes of its pass (`open`) or, read-only, the published
    index's entries that differ from the reference (`open_live`). find and read see those first; lists and contents
    see the reference only. A known size answers BUFFER_TOO_SMALL before any IO, every other read opens, checks and
    seeks, and resource_json's lossless tree is built once per resource per generation. Each resource chunk is checked
    once before EML's reader decodes it (its range in the file, a claim its zstd frames can produce, a decode this
    process can reserve). Failures keep their cause behind a token. kiln.eml.values RESOURCE roots read the same
    archive.
26. `kiln-eml/src/sf_startup/pregame_runtime/` (the pregame runtime, adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68,
    MIT, bonsaibauer: `lua/ingame/{mod,check_ingame_access,set_profile_patch_enabled,access_named_service}.rs`,
    `lua/ingame/{ecs,impact,world,player_input,functions,overrides}/`, `lua/values/` and `transport/lua/network.rs`;
    the overrides' `track_runtime_overrides.lua` and `create_override_targets.lua` verbatim) and `common.rs`/`runner.rs`
    (one shared runtime registrar host per package, the value seam in `Shared`): the runtime families ShroudForge's pregame VM has,
    with its pregame behaviour, installed after the core's composition on the same `runtime` table. `runtime.ecs`,
    `.impact`, `.services`, `.world`, `.network`, `.patch` and `.functions` convert the arguments their source
    converts first, then answer the phase refusal in each function's own shape; the members without a phase check
    answer what a process without the native provider answers (world session 0, context kind "unknown", the bridge's
    missing registry and function inventory, `get_catalog` over the session's `keen::ecs::*` types with every live
    fact false); `runtime.values` works on the session's registry (`decode`, `new`, `encode`, `normalize_field`
    through the codec and the wrappers); `runtime.overrides` is ShroudForge's own tracker and target factories, with
    the four claims refusing where their source does. Checked against ShroudForge's oracle on both pins (140 records,
    byte-identical). The attribute catalog members are item 28's. The network status reports ShroudForge's default
    loader settings (no server id, no allowed clients): Kiln has no ShroudForge loader configuration.
27. `mod-loader-lua` `transaction.rs` (`InputWitness`), `startup_host.rs` (`open_matching`, `input_witness`) and
    `cache/{mod,prints,stamp}.rs` (stamp reader, racy window, escape hatch, file-system check: crate-visible) (the stamped witness,
    kiln-eml's own code): the stamped startup witness. Checking the game's archive inputs (`StartupHost` opening,
    `begin_stage` twice, prepare and publish, every `run_with` writer) no longer hashes every input: the small parsed
    files (`<stem>.kfc`, `.kfc.bak`, `.kfc.bak.integrity`) keep their SHA-256 and are hashed again at every check;
    the resources, their backup and every container keep a stamp (file identity and change time from the change
    cache's own reader, length, last-write time) and a check opens them for metadata only (Linux O_PATH, Windows
    FILE_READ_ATTRIBUTES), so it reads none of their bytes. A large file is hashed instead when no stamp can vouch for
    it: `KILN_EML_FULL_HASH` set (the change cache's escape hatch turns this use of the stamp off too), no identity or
    change time reported, a FAT/exFAT/CDFS/UDF volume (Windows), or a change within the change cache's racy window
    (3 s) before the capture. An input absent at the capture is a change by its presence alone (a large one that
    appears is not read). The working baseline's backups (`BaselineIdentity`) are hashed once per capture, the
    resource backup between two equal stamps. `InputWitness` is `Send + Sync + Clone`; the startup controller captures it once on the
    boot main thread and each attempt opens its host with `StartupHost::open_matching`, which refuses a witness of
    another folder, stem or container count and any input changed since. The witness writes nothing, so it runs no
    scratch-file test of the volume as the change cache does: under Wine a FAT or exFAT host mount can report NTFS and
    is stamped, which Linux's in-memory change time and the new inode number it gives a file read back from the disk
    still catch. Not caught: a program that sets the Windows ChangeTime back on purpose after an in-place edit (the file
    id tells a replacement). No new write, lock, process or Mutex; `run_with`'s output is unchanged. The
    capture also stamps the live header's containers past the working baseline's (a published or cached generation's
    own), so a package DLL that rewrites one in place during a kiln start is seen; the witness is still named by the
    baseline's container count.
28. `kiln-eml/src/sf_startup/attributes/` (the attribute catalog, adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68, MIT,
    bonsaibauer: `reflection/build_attribute_catalog.rs` but its in-game `read_header` and `bind_storage`, and
    `reflection/evaluate_attribute_program.rs` with its tests), `sf_startup/pregame_runtime/{attributes,
    loaded_resources}.rs` (`lua/ingame/ecs/attributes/*.rs`, `lua/convert_json.rs`, `lua/ingame/resources/*.rs`), the
    VM's attribute catalog in `game/assets.rs` (`AssetSession::attribute_catalog`, `create_table_with`) and
    `common.rs`: the last two pregame parity gaps. `runtime.ecs.get_attributes`, `get_attribute` and
    `evaluate_attributes` read the reference archive's attribute catalog, built once per VM from its
    AttributeContainerResource and BaseAttributeResource records (decoded by the value codec and bounded walk, not kfc's
    `Value::from_bytes`) and the registry's attribute components, its failure kept as ShroudForge keeps it; the
    attribute programs are evaluated detached, as ShroudForge models them. `runtime.overrides.attribute` resolves its
    descriptor, then meets the pregame read refusal. Every package's `game.assets` carries ShroudForge's seven
    loaded-resource functions at its position with their pregame answers: the two lookups raise the ingame-only text,
    resolve/prepare/by-field answer the read denial, `write_loaded_field` the assets write denial before any write (no
    new write boundary), `read_loaded_field` refuses a Resource that is not a runtime snapshot. Checked against
    ShroudForge's own catalog, program and result code compiled unchanged over the same synthetic archive (the oracle
    has no archive reader).
29. `kiln-eml/src/startup.rs`, `sf_startup/{admission,cache,retry}.rs`, `sf_startup/plan.rs` (`retry_without`,
    `fork_order`), `sf_pass.rs` (`run_pass_with`), `mod-loader-lua` `ownership.rs` (`SfHandoff`, the owed-handoff
    marker, the receipt's `baseOrigin`), `startup_host.rs` (`with_handoff`, `BaseOrigin::recorded`, `restore_base`,
    `StartupRecord`, `capture_baseline`, `vouch_live`, `CacheCommit<'guard>`), `transaction.rs`, `load.rs`
    (`create_backup_with`, `restore_backup_with`, the witnessed writer), `cleanup.rs` (the new temps), and
    `kfc-base` `container/file.rs` (`bundles_in_range`) (the startup controller, kiln-eml's own code; the retry's order and closure
    adapted from ShroudForge 1.7.9 at Kiln's fork pin 2df1de68, MIT, notice in sf_pass.rs): the startup controller of
    a kiln-mode start, a library the plugin wiring wires. The early witness classifies the archive with the inspector,
    checks the plan's frames, executable and BASE origin (`plan-stale`), refuses a reference index whose resource-type
    bundles lie outside its index list, captures or refreshes the working baseline before any DLL runs and takes the
    stamped input witness (captured again once a just-changed input leaves the racy window). The pass admits the
    plan's members minus native refusals, changed packages and what needs them, runs the ShroudForge dialect with its
    one retry (each attempt a new host over the witness, the retry in the fork's re-planned order, a missing own
    `src/mod.lua` retried) or EML's pass, publishes once, or restores the verified BASE when no plan contribution is
    committed, and returns the outcome record and the live generation's grant inputs. Kiln mode (`SfHandoff::Deferred`)
    never opens ShroudForge's folder: every write that changes the live pair records
    `kiln/eml/ownership/<stem>.sf-handoff-owed.json` first, and the next Live write (or `settle_owed_handoff`) marks
    ShroudForge's applied record stale and removes it. A Deferred publication records the BASE origin in the receipt
    (only the two unclassified tokens, keyed to the backups, carried over only while they are the same); a verified
    restore removes the receipt. The startup cache (`kiln/eml/cache/sf-startup-<stem>.json`) is written only under the
    install guard after the one publication; EML's change cache completion is refused once the guard is gone. New
    write boundaries (the owed marker, the receipt's removal, the startup record) are in the kill/error matrix.
30. `kfc-resource` `mapped/{mod,util,error}.rs` (eml-array-count): a blob array's stored count, and a static array's
    registered one, is checked against the bytes from the array's start before anything is made from it
    (`check_count`; each element takes its registered size, never less than its kind reads and never 0; a count of 0 is
    not checked). kfc trusted the count and every reader makes one entry per element up front (kfc's `Value::from`,
    EML's `MappedArrayValue`, the value wrappers, the typed graph), so bytes claiming 2^32-1 elements asked for about
    100 GiB at once and aborted the process, as ShroudForge does; now it is the mapping error `ArrayCountExceedsData`.
    Valid data always fits: every element is written to its full size, and in the game's registry every type is at
    least one byte and at least what its kind reads. Comments at the readers in `mod-loader-lua`
    `env/game/assets/value/mapped.rs`, `resource_values/{mapped,converter}.rs` and `archive_values/node.rs`.
31. `kiln-eml/src/sf_startup/plan.rs` (`within`, kiln-eml's own code): `ExportRoot::select` knows a protected folder
    by any of its names. Under Wine (Proton) `fs::canonicalize` keeps the spelling it was given instead of the folder's
    own, so `<game>\Mods`, an 8.3 short name (`SHRO~CSB`) or another drive letter for the same folder (`Z:\...`)
    resolved to a different name than the protected `<game>\mods` and was taken; on native Windows a local share
    (`\\localhost\C$\...`) did the same. The root and the protected folders are now compared by name (on Windows
    case-folded as NTFS and Wine fold, checked against the system's own table) and by file identity (volume and file
    number of the folder and each of its parents). Either test refuses, so neither can take a root the other refuses.
32. `kiln-eml/src/same_path.rs` (new), `runner.rs` (`other_game_running`, `lock_game`, `GAME_LOCKS`), `discover.rs`
    (`windows_same_folder`'s fold) and `sf_startup/plan.rs` (`within` moved to `same_path`) (kiln-eml's own
    code): one install by any of its names. The check that refuses a start while a game of this install already runs
    compared the running image's canonical path with `eq_ignore_ascii_case`, and the install lock knew a held install
    by its exact canonical path. Under Wine both keep the spelling the game was started or named by (another case,
    non-ASCII too; an 8.3 name; Z: for C:), and on native Windows a game started from `\\localhost\C$\...` is named
    `\\?\UNC\...`, so a game of this install started by another name was not seen running and a second lock of the
    same install by another name failed. The running-game check now compares by name (folded as NTFS and Wine fold, as
    the export root does) or by file identity (volume and file number); either one is enough, and an executable that
    cannot be identified is compared by name only, as before. The install lock knows a held install by its exact
    canonical path or its identity, never by the fold: the fold is coarser than NTFS, and two installs it joins (the
    Kelvin sign and `K`) must each take their own lock. A volume serial of 0 is no identity there: Wine gives 0 for
    every mount it knows no serial for (btrfs subvolumes, tmpfs), so two folders on two such mounts share one. `windows_same_folder` (classification only) uses the same fold, which also
    joins the Greek letters with a subscript iota and, coarser than NTFS, the Kelvin sign with `K`. No new Mutex
    (`GAME_LOCKS` keeps its entry in MUTEXES).
33. `kiln-eml/src/startup_seam.rs` (new), `plugin.rs`, `runner.rs` (`held_lock`, which knows the held install as `lock_game` does; `run_kiln`), `kiln_mode.rs`, `startup.rs`
    (`without_grant`; no BASE origin in a record without a grant; a terminal's coherence counts only for a run that
    loaded a DLL), `Cargo.toml` (sfi-policy) and `mod-loader-lua` `lib.rs` (`load_type_registry` exported) and
    `transaction.rs` (`under_wine` asked once and kept, `prime_witness_checks` on the boot thread, so a witness check
    under the loader lock resolves no symbol) (the plugin wiring, kiln-eml's own code): kiln.eml.startup v1's provider and the startup controller's
    startup controller wired into the plugin. In a kiln-backend start kiln-eml's early step reads the frozen launch plan with the
    interpreter's own reader (sfi-policy's `LaunchPlan`, `build_info`'s executable digest and side, `check_folders`: no
    two packages in one folder under two names), takes the install
    lock for the process, runs the startup controller's witness before any package DLL loads (a panic there is a refused witness) and serves
    the early export `kiln_eml_startup_service` (attach, witness_wait, witness_recheck, native_terminal, outcome; one
    `Mutex` and a `Condvar`, nothing that needs the loader lock). Its early on_ready is the barrier: at most 20 s after
    kiln-sfi's attach for the native terminal (read strictly), then the startup controller's pass, one publication, and the grant
    (`authorize_with_archive`, PUBLISHED, once). Kiln mode never runs an EML mod on its own: the plan decides every
    package, an unlisted EML contributor is skipped in the ShroudForge and EML dialects, and
    kiln/state/eml.json shows the pass's rows (`source: "shroudforge"`). A hosted start's `stand_aside` settles the handoff
    a kiln-mode start owed ShroudForge. No new write boundary (the pass's are the startup controller's); one new `Mutex` (in MUTEXES).
    The early step also makes the process's first Lua state, so the barrier's pass loads no library and resolves no
    symbol while a package's DllMain may still hold the loader lock (Wine's loader takes it for both). It
    also asks first what on_ready and the pass otherwise ask Wine's loader: the executable's name (`plugin.rs`
    `exe_stem`, kept for the process), Wine's path conversion export for discovery (`discover.rs` `prime_dos_name`),
    std's thread-local destructor callback and the package containment checks' export (`startup_seam.rs`
    `prime_the_pass`).
34. `mod-loader-base` `registry/fs.rs` (`sanitize_path` and the read diagnosis, `wine_within`): a folder
    link in a disk mod that leads out of it is refused under Wine too. Under Wine (Proton) every open follows a Unix
    symbolic link and `canonicalize` keeps it in the path, so `starts_with(root)` passed and a read went through a
    link to anywhere. The path must now also lie under the root by Wine's own Unix names
    (`wine_get_unix_file_name`, which resolves every link); a path Wine gives no Unix name is refused. Elsewhere
    nothing changes (`canonicalize` already resolves every link there). Test
    `mod-loader-base/tests/wine_links.rs` (a Unix link under Wine, a junction natively). The
    looked-up export is a module static and `prime_wine_within` (re-exported in `registry/mod.rs`) makes the lookup
    in kiln-eml's early phase, before a package DLL's initializer may hold the loader lock.
35. `kiln-eml/src/sf_startup/journal.rs` (new), `sf_startup/game/assets.rs` (the recorder, each resource's owner),
    `sf_startup/resource_values/{objects,owned,mapped,mod}.rs` (`WriteOwner`, `ValueObjects::owned_by`, a write notice
    after each assignment, insertion, removal, clear and variant `value` of the six wrappers; a deep copy belongs to the
    seam it is copied into, not to its source), `sf_startup/runner.rs`, `sf_startup/cache.rs` (record format 2),
    `startup.rs` (`Point::Journaled`) and `mod-loader-lua` `ownership.rs` (`journal_path`, `forget_journal`,
    `before_restore`), `startup_host.rs` (`StartupJournal`, `restore_base`), `cleanup.rs` (the journal's temps) (kiln-eml's
    own code): the startup change journal of a kiln-mode ShroudForge-dialect start,
    `kiln/eml/ownership/<stem>.change-journal.json` (`schema/sfi/eml-journal/startup-journal.schema.json`), a record of
    its own beside the receipt, never part of the startup outcome. Per package of the committed attempt, in the order
    the entrypoints ran, with its plan identity (id, version, kind, content and settings digests): the resources its
    entrypoint wrote (resource key; for one another package created in the pass, that package and ordinal), the ones it
    created (ordinal, key, whether the package gave the GUID) and the contents it created; writes made while no
    entrypoint runs are `unattributed`; never a value. The attribution is exact by ownership, with no value walk: every
    typed value of a resource (decoded, validated or copied into it) carries that resource as its owner and each write
    through it is noted for the running entrypoint (one cell comparison per write), so a resource two packages edit
    names both, a write through a reference kept from another package names the writer, a read names nobody, and a write
    into a value the package detached from its resource still names that resource (over-named, never missed). A created
    resource is named `<package id>:<ordinal>` across runs while the package creates in the same order (Lua seeds its
    string hashing per run). At most 4096 entries per package and 32768 in all; beyond, `truncated`, never a failure. It
    is bound to one generation: written after the one publication under the install guard, only while the receipt is the
    one it names (`StartupJournal::write_for`), before the startup cache, which records its SHA-256 and hits only with
    that journal byte for byte; removed before a publication writes anything (`prepare_from`; one that cannot be removed
    refuses the publication) and before a verified BASE restore (`restore_base`, `forget_receipt`) or EML's own no-mod
    restore changes a live file (`before_restore`; one that cannot be removed refuses the restore). A journal that
    cannot be written after the publication leaves the publication standing, a warning (`startup-journal-not-saved`) and
    no cache record, so the next start journals again. The EML dialect writes none (its publication removes the previous
    one). New write boundaries (`journal.removed`, the journal's temp write and replace) are in the kill/error matrix.
    No new Mutex.
36. `kiln-eml/src/startup_seam.rs` (`published_identity`, `prime_the_pass`) and `plugin.rs` (`run_now`) (the boot identity,
    kiln-eml's own code): kiln.eml.startup's table gets its sixth member, `published_identity` (48 -> 56 bytes, still
    version 1): the identity alone of the generation a kiln-mode start granted, session-free and read-only, for
    kiln.boot's OnBoot game data (OK only once the barrier finished with a grant; UNAVAILABLE before, without a grant and
    with no launch installed; a key for another launch or plan, a session or a short record is INVALID_ARGUMENT; one
    lock, a copy, nothing logged). A kiln-mode start passes no `kiln.progress` table to its run (the state file's
    progress stays): the core's first `begin` asks user32 for the screen size, which can load Wine's display driver
    while a package DLL's initializer holds the loader lock. The early phase also asks sfi-core's export-root check its
    Wine lookup (`sfi_core::eml::io::prime_export_checks`). No new write boundary, no new Mutex.
37. `kiln-eml/src/archive_service/attributes.rs` (new), `archive_service.rs`, `archive_service/{read,view}.rs` (kiln-eml's own
    code over the attribute catalog of item 28): kiln.eml.archive's attribute group, `attribute_catalog`,
    `attribute_resolve` and `attribute_evaluate`, now in the table and reported as `eml.archive.attributes` with its three
    member rows. A generation authorized with its archive builds ShroudForge's catalog once, on its first attribute
    call, from its REFERENCE catalog only (the reference index and reader, never staged records) with a copy of its
    registry, and keeps it (a failure too) for its life: the catalog reply and each entry's JSON are materialized with
    it, so a size probe and the copy answer the same bytes. Replies follow schema/sfi/eml-archive/attribute-*.schema.json:
    each descriptor and layout as the neutral tagged tree of ShroudForge's JSON (key order kept), an entry's
    `definition_guid` from its one associated component. Failures keep ShroudForge's own text behind an error token
    (catalog failures UNAVAILABLE; resolve's missing ID or name NOT_FOUND, an ambiguous name UNAVAILABLE; evaluate's
    incomplete root NOT_FOUND, a short or long input INVALID_ARGUMENT, an unverified operation enum or unsupported scalar
    UNAVAILABLE, a program failing on the given bits INVALID_ARGUMENT); a short trace buffer leaves the output bits
    untouched. No engine memory, no Lua, no new write boundary; the catalog's lock is held only around its own lookups.
38. `mod-loader-lua` `ownership.rs` (`hosted_shroudforge_output`, `RestoreProof`, `before_restore_as`), `load.rs`
    (`restore_backup_as`), `startup_host.rs` (`restore_base`, `VERIFY_GAME_FILES`) and `kiln-eml/src/startup.rs` (the
    witness) (kiln-eml's own code): a kiln-mode start over Kiln's bundled ShroudForge's output. That ShroudForge's fork
    records its last startup asset pass beside the backup (`<stem>.kfc.prepared.json`, scheme `kiln-sf-prepared 1`,
    or 2 since 0.2.0-dev.31.12, which changed only its export state; record_prepared_game_files.rs): the header's size and whole SHA-256, the resource file's size, last-write time and
    the SHA-256 of its first and last MiB, both backups' size and last-write time, Steam's build id. No receipt lists that
    output, so the verified BASE used to refuse it (`neither the BASE nor output Kiln can prove it published`) and Kiln's
    interpreter did not start for a player who switched from Kiln's bundled ShroudForge. Now, in kiln mode only
    (Deferred), output whose record matches it in all of that (the build only where the record and Steam's manifest
    both name one) is restored from the backups present as owned output is: the backups pass `validate_backup` (this
    build's version, the seal), the proof is checked again in `before_restore_as`, the owed ShroudForge handoff is
    recorded, Kiln's older receipt goes. A record naming the live header but not the rest (stale) and output nothing
    proves stay refused and change nothing. The witness never takes that output as a new working baseline (backups
    gone or of another game build: `baseline-shroudforge-output`). Every such refusal ends with what the player does
    (`VERIFY_GAME_FILES`: verify the game files in Steam, then start again from the Kiln launcher), and kiln-sfi's
    `startup-grant-missing` reason quotes it (`base-not-restored` first, else the first attempt's error, else the
    warning that ended the start). EML mode (Live) is unchanged. Tests: `m6_hosted_restore.rs`, ownership.rs
    `hosted_shroudforge_output_is_proven_by_its_whole_record_only`.
39. `mod-loader-lua` `transaction.rs` (`validate_stage`, `sync_file`, `copy_counted`) and `ownership.rs` (`prepare_from`)
    (kiln-eml's own code): a publication that is not cached no longer reads the game's containers back. The check of a
    staged archive read every content of every container the header lists, the game's own included: 48 GiB on a client,
    at every first kiln-mode start, every start whose packages or settings changed, and every EML run that was not cached
    (a first kiln-mode start took 160 s for one small package; an 8 GiB synthetic archive took 30 s, 26 s of them in that
    read). It still reads back and hashes every content of the stage's own containers and decodes every resource; the
    contents of the baseline's containers that the baseline index lists at the same place are not all read: the writer
    only appends containers, and the input witness holds the baseline's container files (identity, change time, length)
    from the capture to the publication, checked before and after the validation and again before publishing. Four of
    each container's (spread from its first to its last, each at most 4 MiB) are still read back and checked: neither
    the witness nor the backup's own checks show that the baseline index describes the containers at all, and a backup
    left from an earlier run over other game files passes those (seen in the field as "staged content hash mismatch",
    which this keeps). Any other content in a baseline container is read and checked as before. A Deferred publication no longer
    hashes the live resource file just to tell ShroudForge's handoff whether it changed (it hands nothing over). Staging
    copies move 1 MiB at a time. A flush to the drive that takes two seconds or more logs which file and how long. Tests:
    transaction.rs `validation_reads_the_stages_own_containers_back_and_not_the_baselines`,
    `validation_refuses_a_baseline_whose_containers_hold_other_bytes_at_its_places`,
    `validation_reads_a_baseline_containers_content_the_baseline_does_not_list_there`,
    `baseline_samples_spread_from_the_first_content_to_the_last_and_skip_large_ones`, and `m6_startup_timing.rs`
    (ignored: a client-sized archive, run by hand).
40. `mod-loader-lua` `load.rs` (`create_backup_with`, `stale_backup`, `base_relation`, `containers_hold`,
    `restore_backup_as`), `ownership.rs` (`header_is_kilns`, `readonly::base_relation`) and `kiln-eml/src/inspection.rs`
    (note `baseline-stale`) (kiln-eml's own code): a working baseline of the live game build that does not describe the
    game's data files is found before anything is staged. The backup's own checks (version, seal or live resource
    prefix) pass a backup that is itself modded data: another mod loader or an earlier start can keep the modded pair it
    found under the backup's names, a later start seals it again while it equals the live files, and after Steam's file
    check (the game's own header and resource file back, the extra containers left) a container that backup lists can
    hold other bytes. EML mode built the stage from it, and the stage's read-back refused every start with "staged
    content hash mismatch" (0.2.0-dev.30.4; 0.2.0-dev.30.1 had no read-back and published it). Now `create_backup`
    compares the two headers by the writer's rule (`base_relation`: every writer builds a header over a baseline by
    keeping its containers, contents and resource chunks in place and appending): the live header is the backup or was
    built over it (Under), and every container the backup lists is there at its size (`containers_hold`; the game itself
    refuses another size), or the start is refused with what to do (verify the game files in Steam, then start again; if
    the message comes back, move the three backup files out of the game folder). A backup built over the live header
    (Over) is replaced by a new backup of the live files, before any seal is repaired or written, when no ownership
    receipt lists the live header, no record of Kiln's bundled mod loader names it, and every container the live header
    lists has its size; else refused. A smaller header must also be internally consistent (container content counts,
    content ranges and resource ranges); a damaged live index missing entries cannot replace an intact backup. `restore_backup_as` puts back only a backup whose containers are at their sizes. The
    inspector agrees for kiln mode: Over is the live set as a new baseline (note `baseline-stale`, origin
    `rebaselined-unclassified`; the boot witness takes it through `create_backup`), unless a live file is still Kiln's
    output; a backup that is neither is `baseline-corrupt`. A backup the live header itself was built over (a start without
    the read-back published from it) passes the headers' comparison; the stage's sampled read-back (item 39) can refuse
    it when a sampled content differs. That baseline-content refusal says what to do (`load::stage_refusal`, EML mode and
    kiln mode's prepared stages); a mismatch in a newly staged container does not blame the backup. Tests: load.rs
    `base_relation_follows_how_a_writer_builds_over_a_baseline`,
    `a_backup_whose_containers_are_not_at_their_size_is_neither_built_from_nor_restored`,
    `a_header_only_backup_built_over_the_game_files_is_replaced`, and `m6_stale_backup.rs` (the field's game folder
    through the production runner and the inspector, and the read-back as the second layer).
41. `kfc-content` `image/encode.rs` (`encode_with_threads`, `encode_threads`, `set_encode_threads`) and
    `kiln-eml/src/runner.rs` (`encode_threads`, `EncodeThreads`) (0.2.0-dev.31.13): a BC1 to BC7 texture is encoded in
    strips of whole block rows, each by whichever thread takes it next (the calling thread and up to count - 1 helpers),
    the strips then put into the output in order. The calling thread never waits for a helper to start (one created
    while another thread holds the Windows loader lock runs only once it is free): it takes strips from the same counter
    and waits only for strips a helper has taken; a helper that has not started, or that the OS refuses, leaves its
    strips to the threads that run, never a panic, and while one has not started no new helper is made. So the helpers
    own their input (a copy of the texture's RGBA8 pixels, freed when its last strip is done) and each strip has its own
    buffer: about twice the serial encode's memory for the texture being encoded (review of 31.13; the test
    `a_helper_that_has_not_started_yet_is_not_waited_for` in `encode.rs`). block_compression makes a new compressor for every block, so the bytes are the serial
    encode's (`tests/parallel_encode.rs`: every BC format, uneven strips, more threads than rows, whole mip chains, very
    wide and very tall textures, sizes that are not whole blocks; the refused-thread test in `encode.rs`). `encode`
    stays EML's serial encode until the host sets a count; EML's run (`runner::run` outside kiln mode, which in the
    plugin runs in kiln-core's early step outside the loader lock) sets the PC's threads, at most 16,
    `KILN_EML_ENCODE_THREADS` overriding (1 = serial, at most 64, `0` or a non-number ignored), and puts the old count
    back when it ends, also on an error or a panic. Kiln mode stays serial: its pass can run while a package DLL holds the loader lock,
    where a new thread cannot start. EMF Beastmaster's three packs spent 397 of their 401 s of Lua in BC7.
42. `mod-loader-lua` `steam_files.rs` (new) and `load.rs` (`create_backup_with`) (kiln-eml's own code, 0.2.0-dev.31.13):
    a new working baseline is never taken from game files that Steam does not list as they are. `create_backup_with`
    copies the game files over the backups when there are none, they are of another game build or they are stale.
    The archive's own checks show that the game files are a whole archive, not that no program changed them since
    Steam wrote them. Steam's record does: `steamapps/appmanifest_<app>.acf` names the installed depot manifests,
    `<Steam>/depotcache/<depot>_<manifest>.manifest` lists each file's size and SHA-1 (Windows: also the depotcache of
    the Steam installation the registry names). Where it lists `<stem>.kfc` and `<stem>.kfc_resources` otherwise, the
    copy is refused ("... are not the files Steam installed for build <id> ... Verify the game files in Steam, then start
    through Kiln again") and the pair present stays (after a game update the only clean copy there is); listed as they
    are, or no manifest, the copy goes on as before. Only those two files are hashed, only on that path; nothing is
    written. The same reader and rule as Kiln's ShroudForge fork (`check_steam_installed_files.rs`,
    `verify_working_baseline.rs`), so both writers of the shared pair decide alike. Tests: load.rs
    `a_backup_of_another_build_is_not_replaced_by_files_steam_did_not_install` (then Steam's files are taken: the
    adoption), `a_backup_of_another_build_is_replaced_by_the_files_steam_installed`, `steam_files` (6). The appmanifest
    is read 16 tables deep at most (one nested without end overflowed the stack: `an_appmanifest_nested_without_end_tells_nothing`).
43. `mod-loader-lua` `steam_files.rs` (`installed_backups`, `compare`, `lookup`, `listed_files`, `extends`), `load.rs`
    (`create_backup_with`, `restore_backup_as`, `judge`, `Verdict`, `stale_words`, `stale_live_words`, `doubt_words`,
    `set_aside_backups`, `STALE_WORDS`, `KNOWN_LEFTOVERS`), `startup_host.rs` (`known_leftover_refusal`), `ownership.rs`
    (`SF_PREPARED_SCHEMES`), `kiln-eml` `startup.rs` (`witness`: `baseline-stale-data`) (kiln-eml's own code, stale game
    data, 0.2.0-dev.31.16, and its review): a working baseline that passes every check (its seal, its containers, a
    live header built over it) is still not the game's own when an earlier start took it of game files another loader
    had patched (HadToRegister, 2026-11-26: Auto Loot Plus's auto-smash program). Before kiln-eml builds from the pair
    in EML mode or puts it back, Steam's manifest of the installed build is asked about the backups themselves (in the
    place of `<stem>.kfc` and `<stem>.kfc_resources`; SHA-1 of both, read only on a patch run or a restore). Listed
    otherwise: beside game files Steam lists as they are, those become the new pair; otherwise refused with "Your game
    files still contain changes from a mod that isn't installed. Verify the game files in Steam (Properties > Installed
    Files > Verify integrity), then start again." (the server's words name SteamCMD's validate), and nothing changes,
    only where the pair is proven to carry a mod's changes (a known mod's program in it, or a resource backup that is
    Steam's resource file with more data after it); without that proof the pair is used and restored as before and the
    start logs why it is in doubt (Steam's appmanifest can name a build whose files are not in the folder). Without a
    manifest only a known mod's program refuses. A kiln-mode start (`witness`) refuses, header only, a working baseline
    or game files it would take as one that hold a known mod's program. A pair kiln-eml replaces (another build's, one
    built over the game's header, one that is not the installed build's own) is set aside in
    `kiln/eml/backups/outdated/<stem>/<generation>/`, header backup first, and only the newest generation is kept; it was
    overwritten before. Kiln's ShroudForge fork decides the same (`verify_working_baseline.rs` `proven`), and its prepared
    record of scheme 3 (written by a pass that proved its pair) is accepted as hosted ShroudForge output like schemes 1
    and 2. Tests: load.rs `a_backup_that_carries_a_mods_changes_is_neither_built_from_nor_restored`,
    `a_backup_steam_does_not_vouch_for_is_used_as_before_when_nothing_in_it_shows_a_mods_changes`,
    `a_backup_of_the_previous_build_under_files_steam_installed_is_set_aside_and_retaken`,
    `a_backup_whose_resource_file_is_steams_with_more_after_it_is_refused`, `steam_files`
    `backups_are_compared_with_the_files_steam_installed`, `a_file_is_steams_with_more_after_it_only_when_its_start_is_steams`,
    `m6_stale_backup` `kiln_mode_never_takes_or_builds_from_game_data_that_holds_a_known_mods_program`, `ownership`
    `hosted_shroudforge_output_is_proven_by_its_whole_record_only`.
44. `mod-loader-lua` `env/writes.rs` (new), the value wrappers (`value/mapped.rs`, `value/simple.rs`, `value.rs`
    `walk_writes`/`note_put`), `env/app_state.rs`, `env/game/assets.rs`, `runner/mod.rs`, `runner/state.rs`, `lib.rs` (`RunResult::journal`,
    `RunResult::generation`) (kiln-eml's own code, 0.2.0-dev.31.18): EML's pass records which mod wrote which resource
    and where, for the EML dialect's change journal (kiln-eml `sf_startup/journal.rs`, format 2). The runner names the
    running entrypoint (`require("mods.<id>")`; a mod's entry module is its own mod's code even when another mod requires
    it before its turn); each value wrapper keeps a log of the writes made through it (a field
    assigned, the value put in place whole, a list's membership changed), a resource's `data` replaced, a resource or a
    content created are noted per writer; once the mods ran, `AppState::write_record` walks every materialized value as
    it will be written and turns each log into a member path at its published place (`recipes[37].craftingDuration`,
    `input[]`, `""` for the whole resource). Off outside a run (one thread-local read); in a run a write costs a
    thread-local read and a scan of its value's own small log; a copy's log starts empty (the assignment that put it
    there names its writer), a write into a value detached from its resource is not named, and the writes logged in a
    value replaced (a field, an element, a variant's value, a resource's data) move onto the value put in its place,
    member by member (`value::graft`), since the new value can be a copy of it. Nothing is compared or
    serialized differently: the dirty flags and the bytes written are EML's. Tests: `env::writes` (2),
    `value::tests::writes_are_named_by_their_published_member_paths`, `a_replaced_value_keeps_the_writes_made_in_it_before`,
    `kept_writes_move_with_the_members_of_the_copy`, `journal_cost_copy_back` (ignored),
    `journal_cost` (ignored: times the log and the walk), kiln-eml `m6_eml_journal`.
45. `mod-loader-lua` `startup_host.rs` (`PreparedEmlRun::record`) (kiln-eml's own code, 0.2.0-dev.32): a prepared EML run
    (kiln mode's EML dialect) keeps the run's record of who wrote what (change 44), so kiln-eml writes that dialect's
    change journal once its generation is published (`startup.rs` `eml_pass`, format 2 of dialect `eml`, bound to the
    generation digest). kiln-eml's own SF-dialect pass (not EML's code) also records the member paths each package changed
    (`sf_startup/journal_paths.rs`, `game/assets.rs` `compare_running`: after each entrypoint its written resources are
    encoded as the commit encodes them and compared with what they held before it ran; only those that differ are
    decoded and walked), written as format 2 of dialect `shroudforge` with `notApplied`; the startup cache's record is
    format 3 (a record of format 2 names a format-1 journal: it misses once). Tests: `sf_startup::journal_paths` (5),
    kiln-eml `m6_startup_journal` (the SF dialect's paths, a kiln-mode EML-dialect journal).

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

## Lua dependency alignment

`kiln-eml` links the MIT `sfi-core` crate into this GPL component, in one direction only: it is a normal
dependency (the SF-dialect startup pass's type session, `kiln-eml/src/sf_startup/`), so the shipped DLL links it. The
MIT workspaces still have no GPL dependency. `sfi-core` is not the `kiln-sfi` runtime plugin
and does not load either ShroudForge runtime.
Its source, workspace manifest and licence are under `crates/sfi/`; its shared conformance inputs are under
`tools/sfi/conformance/`. Include these with this folder when distributing corresponding source.

The workspace deliberately pins `mlua =0.11.6` with Lua 5.4, vendored and error-send, resolving `mlua-sys 0.10.0` and
`lua-src 550.0.0`, as the frozen SF/core graph does. This replaces EML's locked `0.11.2 / 0.8.3 / 548.1.1` graph only
after a dependency differential; matching the native Lua 5.4.8 sources alone is not binding-layer evidence.
Brabb3l's existing Lua dialect, writer and runner are unchanged.

`kiln-eml/tests/m6_lua_dependency.rs` exercises the strict comparator, verifies one exact Lua dependency universe and
passes GPL-created fixture tables/userdata through the production core `LuaVm`. The source-only differential tools
are in `kiln-eml/tests/fixtures/m6/eml_dependency/`: `prepare.py` copies a retained **pre-alignment** source tree into a
fresh output directory and adds the identical test-only runner child to each copy. It never edits production sources
or invokes Cargo. Invoke this tool from the current source tree, passing the retained baseline separately. Capture
old results before preparing the candidate; `prepare.py` checks the old locked versions and requires the old capture.
Run the copied `mod-loader-lua` test `m6_capture_dependency_bindings` with optional `M6_WITNESS_OUT` pointing to
`<output>/old-observations` or `<output>/candidate-observations`, then run the unchanged EML witnesses and GPL suites.
All Cargo/build commands must use the project's shared build slot and offline dependencies. The candidate copy is
the baseline with this tree's workspace manifests and `Cargo.lock` copied over unchanged (plus `crates/sfi` and
`tools/sfi/conformance` when the graph links `sfi-core`): it builds exactly the production graph, with no lock refresh,
and differs from the old copy in those four files only. `compare.py <old>/bindings-v2.json <candidate>/bindings-v2.json`
compares the shared v2 transcript without a private normalizer or ignored fields. Dependency/source hashes and raw
errors are retained separately. Compare the decoded archive semantics separately from randomized physical file order.
Any Lua-visible difference blocks a production lock change pending an explicit decision.

Result (re-run 2026-11-19 on the v2 base; the first alignment kept no evidence of its run): the old
copy was the pre-alignment tree (mlua 0.11.2 / mlua-sys 0.8.3 / lua-src 548.1.1), the candidate this graph (0.11.6 /
0.10.0 / 550.0.0, with `sfi-core` 0.1.0 linked into `kiln-eml`). The 85 shared-v2 records (27 of them failures, with
their messages), the 27 raw error texts and the semantic observation (7 raw errors) were identical; a second old
capture was identical too. The physical observation has the same file names and sizes; its bytes differ between any
two runs, old against old included (randomized serialization order), so it is not compared byte for byte. This is
startup Brabb3l dependency evidence, not promotion of any SF API row or proof of real-game behavior.
After that `sfi-core` was a dev-dependency until the startup pass took it in (locked versions unchanged). Since then it is a normal dependency
again, so the plugin's graph takes `sfi-core`'s `serde_json` `preserve_order` and full `regex` features. The JSON
kiln-eml writes from a `serde_json::Value` (ShroudForge's rewritten state and its handoff journal, the move journal,
the recovery evidence) sorts its keys explicitly, so it keeps the sorted key order it had without that feature
(`*_keeps_sorted_key_order` tests). `regex` is used here by `tracing-subscriber`'s log filter only.

## Image dependency differential (image 0.25.6 -> 0.25.10)

The lock refresh for `sfi-core`'s pins (1607abdc) moved `image` 0.25.6 -> 0.25.10 for the plugin, with `half` 2.6.0 ->
2.7.1, `png` 0.17 -> 0.18, `zune-jpeg` 0.4 -> 0.5 (replacing `jpeg-decoder`), `gif`, `tiff`, `exr`, `ravif`/`rav1e`
(`block_compression` stays 0.6.0). `eml/kfc-content/tests/texture_differential.rs` runs, on synthetic inputs only, every
image path EML's startup pass reaches: `image.encode_texture`/`decode_texture` (`kfc_content::image::{size_of_format,
encode, decode}`; these do not call `image`, only `half` and `block_compression`) for all 147 `PixelFormat`s at 1x1, 7x5
and 64x64 (encode, decode of that encoding, decode of fixed pseudo-random blocks), and `image.encode`/`image.decode`
(`RgbaImage::write_to`, `image::load_from_memory[_with_format]`) for the 16 format names the Lua binding accepts, plus a
JPEG (RGB8), Farbfeld (RGBA16), HDR (RGB32F) and OpenEXR (RGBA32F) input for the decoders whose encoders refuse RGBA8.
Each build also decodes the other build's encodings. Built with the lock of 39435bb7^ and with today's, file by file
(1157 outputs, 1183 with the cross decodes; each build reproduces itself exactly across processes):
- **textures**: identical, all 441 cases, including the same refusals and panics of unimplemented formats;
- **identical**: every encoding of png, gif, webp, pnm, tga, bmp, ico, qoi and of the four extra inputs, and every decode
  of the same bytes (png, gif, webp, tga, bmp, ico, qoi, tiff, jpeg, farbfeld, hdr, openexr), by format and by guess;
- **avif encode**: same length (849 bytes), different bytes from offset 100 (`rav1e` 0.7 -> 0.8); neither build decodes
  AVIF ("The image format Avif is not supported"), so `image.decode` of AVIF fails in both;
- **tiff encode**: 3274 -> 3286 bytes, differing from offset 8 (`tiff` 0.9 -> 0.11 layout); both builds decode either
  file to the same pixels;
- **pnm decode**: an RGBA PAM (`P7`, `TUPLTYPE RGB_ALPHA`, the bytes `image.encode(img, "pnm")` writes in both builds)
  failed to decode in 0.25.6 ("The encoder or decoder for Pnm does not support the color type `Rgba8`") and now decodes
  to the encoded picture;
- **error texts** of `image.encode` with RGBA8: hdr "Format error encoding Hdr:\nhdr format currently only supports the
  `Rgb32F` color type" -> "The encoder or decoder for Hdr does not support the color type `Rgba8`"; openexr "Format error
  encoding OpenExr:\nwriting color type Rgba8 not yet supported" -> "The encoder or decoder for OpenExr does not support
  the color type `Rgba8`" (Lua sees them after "failed to encode image: ").

Not covered: dds and pcx decoding (the crate has no encoder to make an input), real mod images, and Windows builds
(host Linux only).

## Archive inspector (`kiln-eml-inspect`)

A separate GPL process the launcher runs before a start, so that the launch plan carries real archive digests and the
BASE's origin. `kiln-eml/src/inspection.rs` (library), `kiln-eml/src/kiln_eml_inspect.rs` (binary), schema
`kiln-eml/schema/archive-inspection.schema.json`, tests `kiln-eml/tests/m6_inspection.rs`. Kiln's MIT code never links
it; it runs the binary and reads its JSON.

    kiln-eml-inspect --game <absolute dir> --side client|server [--hash-containers]

One JSON object on stdout, exit 0, also for a refusal (`status: "refused"`, `input`/`base` null, `reasons`); exit 2
for bad arguments (nothing on stdout). The entry is disjoint from `kiln-eml-run`: no patch runner, no Lua, no package
or DLL, no type cache, no state, log, backup, receipt, journal or lock file, no adoption, repair or recovery, and nothing
under `<game>/shroudforge` (or ShroudForge's journal in the game folder) is read. Files are opened for reading only;
on Windows with read/write/delete sharing (no other writer is blocked) and `FILE_FLAG_OPEN_REPARSE_POINT`. Links and
reparse points below the game folder are refused, as the writer refuses them.

**Read-only proof** (tests): a snapshot of the whole fixture tree (names, bytes, modes, mtimes, ctimes) is unchanged,
inotify sees no create/modify/attrib/close-after-write/move/delete event and no open of `.kiln-eml.lock` or of
ShroudForge's folder, a tree without write permission is inspected, and (`--ignored`, needs unprivileged user and mount
namespaces) a read-only bind mount in a private namespace is inspected.

**What it reports.** `input`: the live header, resource file and the containers the header lists, with its class:
`owned-output` (the ownership receipt's current generation, nothing in it unproven, or its previous one, is live file
for file, over exactly the backups present), `base-copy` (byte-identical to the BASE) or `unclassified`. `base`: the
set a kiln-mode start builds from, with its evidence, strongest first:

| Evidence | When |
|---|---|
| `receipt-baseline` | the backups present are exactly the receipt's `backups` (size and SHA-256) and pass the writer's checks |
| `sealed-baseline` | no matching receipt; the backup pair matches its seal (`<stem>.kfc.bak.integrity`) for the live build |
| `unsealed-baseline` | no seal (an older EML's pair), or a seal another writer left behind (other lengths, or a pair equal to the live files): the live resource file's prefix equals the backup's, as the writer checks |
| `current-set` | no backup at all, a backup of another game build (note `baseline-outdated`, origin `rebaselined-unclassified`), a backup of this build that was built over the live header (an archive writer's output kept under the backup's name, by another mod loader or an earlier start; note `baseline-stale`, origin `rebaselined-unclassified`; refused as `baseline-corrupt` when a live file is still Kiln's output), or a receipt whose backups are all gone over live files that are not Kiln's output from it (note `receipt-stale`, origin `rebaselined-unclassified`): the validated live set |

Refused, with no base: a receipt this build does not understand (`receipt-unknown`, kept as it is), a receipt whose
baseline is gone while a live file is still Kiln's output from it (`baseline-missing`, naming that file; its text
tells the user to verify the game files in Steam), a header-only or resources-only backup
(`baseline-incomplete`; its text tells the user to verify the game files in Steam and move the leftover backup files
out of the game folder, so that Kiln takes a new baseline), a receipt path holding something that is not a receipt
this build reads (over the writer's 16 MiB cap, or not a regular file: `receipt-unknown`), a pair that fails its seal and the unsealed rule, a backup of this build that is neither the live header nor built under it, or one built over the live header while a live file is still Kiln's output (`baseline-corrupt`), and any missing,
invalid, linked, unreadable or changed input (`missing`, `invalid`, `link`, `unreadable`, `changed`). Containers the
live header does not list are reported (`retained-container`, note `unreferenced-containers`), never read or blocking.

**After lost backups (kiln mode only).** Steam's verification, a manual delete or a game update can
take away the backups a receipt names. With no backup file left (a leftover one is `baseline-incomplete`), the live
set validated as a first-managed one would be, and none of its files Kiln's output from the receipt, the live set is a
new BASE: note `receipt-stale`, origin `rebaselined-unclassified` (never "first managed", never vanilla). A live file
is Kiln's output when it equals (size first, then SHA-256; a container listed at its size is read only then) a file the
receipt lists for its current or previous generation, unless those listed bytes are the receipt's own backup of that
member (an output file Kiln left as the game's). Files the live header does not reference are not compared. The
inspector only reads; the boot witness acts on the same verdict: under the held install lock it renames that very
receipt (by its SHA-256, still no backup present) to `kiln/eml/ownership/<stem>.stale-<uuid>.json` as a recovery
copy, never deleting it, then takes the new baseline through the writer (Deferred) and the publication records
`rebaselined-unclassified` keyed to it (`startup_host::set_aside_stale_receipt`). EML mode never reads the
inspector and keeps its own rules.

**BASE origin (frozen in the launch plan as `game.archiveBaseOrigin`).** Integrity is not provenance: nothing here
calls a set vanilla, and true vanilla is Steam's file verification.

| Token | Exact label | When |
|---|---|---|
| `first-managed-unclassified` | `unclassified: game files as found when Kiln first managed this install` | `current-set` with no backup present, or a receipt's `baseOrigin` with this token keyed to exactly this baseline |
| `rebaselined-unclassified` | `unclassified: game files as found when Kiln took a new baseline after the game changed` | `current-set` because the backups present are of another game build (note `baseline-outdated`) or were built over the live header (note `baseline-stale`), or a receipt's `baseOrigin` with this token keyed to exactly this baseline |
| `unrecorded` | `unrecorded: a working baseline from an earlier start; where its files came from was not recorded` | every other backup-based BASE |

A receipt's `baseOrigin` (written by kiln mode, read here) is `{"token", "label", "baseline"}`, `baseline` mapping each of
`<stem>.kfc.bak`, `<stem>.kfc_resources.bak`, `<stem>.kfc.bak.integrity` to `{"size", "sha256"}` or null when absent
(the inspection's `base.baseline`). It is kept only when it has exactly these three keys, one of the two unclassified
tokens with that token's own label, and a `baseline` equal to the backups present; anything else (another baseline,
an unknown token, a token with the other token's label, extra keys) is ignored with note `receipt-origin-ignored` and
the origin is `unrecorded`. No origin is ever upgraded.

**Digest framing** (`kiln-eml-inspect archive-set v1`), the `sha256` of `input` and `base`: SHA-256 over the framing
name and `\n`, the entry count (u64 LE), then per entry sorted bytewise by archive member name (`<stem>.kfc`,
`<stem>.kfc_resources`, `<stem>_NNN.dat`): the name's length (u64 LE) and bytes, the proof kind (1 = SHA-256, 2 =
exact length only), the length (u64 LE) and, for SHA-256, the 32 digest bytes. Members, not physical files, are framed,
so a BASE in backups and an input with the same bytes have the same digest (a restored install; a first-managed set,
whose BASE is the input). Containers are proven by exact length by default (they are GiBs); `--hash-containers` hashes
them (proof kind 1, another digest). The header, resource file, backups and executable are always read in full and
hashed once each; every file is checked again after all are read (fresh open: identity, length, modification time and
change time, which an ordinary write cannot put back), and the files whose bytes are parsed (header, backup header,
seal, receipt; small) are hashed again, so a file changed or replaced meanwhile, also rewritten in place with its
length and modification time kept, refuses the inspection. Not caught: a same-length rewrite of the resource file or a
container whose change time is put back too (possible on Windows with `SetFileInformationByHandle`) or does not move
(file systems without a change time, such as FAT and exFAT, or a write within the same timestamp tick as the last look
where the kernel has no fine-grained change times).

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
