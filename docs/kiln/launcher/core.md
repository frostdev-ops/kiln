# kiln-manage: the core modules

`crates/kiln-manage` is the library behind kilnctl and the Kiln Launcher. This page covers the core
modules (contracts.md 12): what each does, the functions the Tauri commands call, and the behaviour callers rely on.
Signatures are in the source; every function returns `kiln_manage::Result<T>` (`Error {code, message, detail}`).
Server, admin and modpack modules: [servers.md](servers.md), [admin-protocol.md](admin-protocol.md), [modpacks.md](modpacks.md).

Features: the default build (kilnctl without features, the launcher's `--selftest`) has everything except zips
(`zip`: bundle read/create/verify/extract, zip import in the library) and zstd (`saves`: the save editor's
compression). kilnctl enables `server` + `zip`, the launcher `full`. Builds pass on Linux and `x86_64-pc-windows-msvc`;
for Windows the integration tests build (`cargo xwin test --no-run --target x86_64-pc-windows-msvc --features full
--test '*'`; the data-folder ones pass under Wine, windows-checklist.md), the library's own unit tests do not
(`found/tests.rs` and `report_tests.rs` use Unix-only APIs).

## Data and paths
- `paths::DataDirs::locate()`: `$KILN_DATA_DIR`, else the folder the pointer `<default root>/data-folder.json` names,
  else the default root `$XDG_DATA_HOME/kiln` / `~/.local/share/kiln`, Windows `%LOCALAPPDATA%\Kiln`. A broken
  pointer is the error `data_folder`, never the default. The environment is a value: `paths::DataEnv::from_process()`
  reads it, tests build it by hand; `DataEnv::locate_checked` says what is wrong instead of failing. Tests and kilnctl
  runs set `KILN_DATA_DIR`.
- Moving the data folder (contracts.md 3, "The data folder's place"): `data_folder::{recognize, inventory, check}` say
  what a folder is, what the data folder holds and whether a picked folder can take it (a `Plan` or a `Refusal`);
  `data_folder_move::move_to` moves it (copy, verify, pointer, records written again, and last the old copy removed:
  `MoveResult.cleanup`), `use_folder` switches to an existing one, `reset` starts fresh after a broken pointer and
  `repoint` points at a Kiln data folder the player picks after one; at the next start `pending`, `remove_old_copy` (what
  an interrupted removal left, file by file, by the move's record) and `remove_partial_copy` (only with the target's
  marker), then `acknowledge`. A move never writes into, replaces or removes what it did not make in the target. A
  launcher process holds `data_folder::DataLock::shared` for as long as it runs and passes it to the move; while a move
  runs `filecache::unpersist` keeps the hash cache out of the old folder. `game::status` says another data folder's
  profile in a game as `otherFolderProfile` (`profile::game_profile_owner`). Tests: `crates/kiln-manage/tests/data_folder.rs`,
  `data_folder_move.rs` (fixture: `tests/fixture/mod.rs`).
- `util`: `write_atomic` / `write_json_atomic` (`<name>.tmp` + rename), `read_json` (None when missing),
  `rfc3339`, `parse_rfc3339`, `ts_compact` (`yyyymmddThhmmssZ`), `uuid_v4`, `random_bytes`, `copy_tree`, plus
  kilnctl's helpers (`expand`, `clean`, `state_dir`, `config_dir`, `human_size`, ...).

## Discovery (Steam, games, processes)
- `steam::roots()` (`$KILN_STEAM_ROOTS` overrides; native, `~/.steam`, Flatpak, Snap; Windows registry),
  `libraries`, `app_manifest` (`updating` from StateFlags), `compat_tool`, `launch_options`: read-only, never written.
- `game::detect()` finds the client (1203620) and server (2278520) in every library; `inspect(path)` describes a
  folder; `add/remove/known` keep `<data>/installs/<id>.json` = `{format, install, receipt?, added, updated}`;
  `status` = `GameStatus` (running, `started`: when the game process started, whoever started it, Steam updating,
  `install::status`, active profile, play-once pending, boot.json, launch options + the
  `WINEDLLOVERRIDES="dinput8=n,b" %command%` advice); `path_mode` (contracts.md 6).
- **Install ids** are stable: the receipt's `installId` when Kiln is installed, else `game::path_install_id(dir)`, a
  UUID from sha256(canonical path). The first install keeps that UUID as `installId`, so the id does not change.
- `proc::list`, `proc::find_game(install)` (Linux: comm + argv[0] as a `Z:\` path or the cwd; Windows: the image
  path), `proc::alive`, `proc::start_time(pid)` (Linux: `/proc/<pid>/stat` starttime after `/proc/stat` btime, the
  Wine process under Proton; Windows: `GetProcessTimes` with `PROCESS_QUERY_LIMITED_INFORMATION`).
- `winpath::{unix_to_z, z_to_unix, z_drive_ok, to_game, to_host}`.

## Runtime bundles and the installer
See [bundle.md](bundle.md). `bundle::{read, verify, create, list, import, find, extract, safe_rel_path,
compare_versions, sides}`; `install::{install, uninstall, uninstall_report, rollback, status, plan,
select_components, read_receipt, modified_files, standard_dirs}`; `pe::{classify_proxy, classify_bytes, exports,
is_pe64_dll}` (`runtime_classify`). `InstallOptions {components, switchFromShroudforge, installer}`: pass
`installer: "kiln-launcher <version>"`. Progress: `report(fraction, message)` for every staged/installed file and a
final summary line; `canceled()` is polled between files (the install undoes itself). `install_report` returns the
receipt and an `InstallReport {changedBackedUp, backup}`: the Kiln files changed by hand that the install replaced
(their copies are in `kiln/backups/install-<ts>/` and `<data>/backups/install/<installId>/install-<ts>/`), and that
launcher-side folder. `restoredShroudforge` lists missing helpers recovered from legacy switch receipts; `warnings`
name current player files that were kept instead of overwriting them. A repeat install or upgrade never replaces the
first original proxy backup. `uninstall_report` adds `placements: {removed, retained: [{path, reason, mayRun}]}` and
`warnings`: only verified unchanged fixed-folder copies are removed, current player replacements are kept and their
original backup recovery paths reported. The launcher returns that structured result in the task and displays retained
paths until dismissed. It does not restore game archives from a previous SF/EML writer's working `.bak`.
Error codes the UI should expect: `game_running` (detail `pid`), `steam_updating` (detail `stateFlags`), `refused`
(ShroudForge, not a DLL, unsafe zip names), `hash_mismatch`, `conflict` (side mismatch), `not_found`.

## Library
`<data>/library/<kind>/<id>/<version>+<hash8>/` + `<version>+<hash8>.json` (`library::ImportRecord`), immutable and
content addressed: re-importing the same bytes reports `existing`, other bytes add a new folder.
- `detect(path)`: `kiln.mod.json` -> Kiln mod; `native.json` -> native; `mod.json` with an integer `emf` and its DLL
  (`dll` or `<id>.dll`) -> EMF; `kiln-modpack.json` -> modpack; `kiln-runtime.json` -> runtime (import it with
  `bundle::import`); a DLL by its exports: `kiln_plugin_main` -> plugin, `emf_mod_load` -> EMF (a `mod.json` is
  generated), `DirectInput8Create` -> a Kiln or ShroudForge proxy is refused, any other proxy -> native mod with
  phase `dllmain` (chain-loaded), anything else -> native mod, phase `early`; zips and folders of mods list `items`.
- `import(dirs, paths, progress)` -> `ImportReport {imported, existing, skipped[{path, reason}]}`; dot entries are not
  copied (not part of kdh1); the copy's kdh1 must equal the source's. Bare DLLs get version `0.0.0` and an id from the
  file name.
- `list(kind)`, `record(ref)`, `users(ref)` (profiles listing it), `remove(ref, force)` (`conflict` while used),
  `path_of(ref)` (a plugin: the DLL).

## Profiles and activation
`<data>/profiles/<pid>/{meta.json,config/,data/}`. `profile::create(name, copyFrom)` (id from the name, `-2` on
clashes), `get`, `list` (`ProfileSummary.active` = installs whose profile.json names it), `save` (`conflict` on a stale
`generation`; returns generation + 1), `delete` (moved to `<data>/backups/profiles/`), `fingerprint` (kms1),
`KILN_API`.
- `resolve_preview(profile, side)`: managed mods through the Resolver.cs port (disabled entries come back as
  `disabled`), native mods through the same rules (`requires`, `after`, `before`, `incompatible`, `kiln`), plus
  warnings for entries missing from the library. On a client the managed mods resolve twice, as Kiln.Host does: the
  menu, then with `Environment.hosting` (the player's own world); what loads only then is `with_world` (skipped,
  `WITH_WORLD`), a server mod that fails then says `WORLD_FAILS` + the reason. `resolve_preview_for(.., hosted)` with
  `hosted_server_mods(game)` (kiln.json `mods.hosted`; false: `HOSTED_OFF`, no `with_world`).
- `activate(pid, install)`: refuses while running and without `kiln/`; path mode (Z when `pfx/dosdevices/z:` -> `/`,
  else copy; an untranslatable path also means copy); copy mode syncs verified copies into
  `<game>/kiln/profiles/<pid>/<kind>/<id>/` (unchanged copies stay, stale ones go) and copies the game-side config
  back first; XHL companions and EML mods a native DLL reads are placed in `<game>/mods/<original folder>/`, and
  their profile.json entries name that copy (in copy mode `../mods/<original folder>`, and `../greeneye/<id>` for a
  placed Greeneye mod, only for a runtime that declares `profileReader` 2 and under the folder name rule, else the
  `kiln/profiles` copy; contracts.md 5.1 and 6); XHL-Core whose Library file is called `dinput8.dll` (as downloaded)
  runs from Kiln's copy `<game>/kiln/chain/<library id>/xhl-core.dll` (with a native.json naming it; profile.json,
  `[profile]` and `[late]` name the copy, in copy mode as `chain/<library id>`). Why: when XHL-Core 2.x loads it asks
  for the module called `dinput8.dll`; if that is itself (under Wine it is, when its own file has that name) it waits
  for the game to call its own DirectInput8Create, which behind Kiln's proxy never happens, and starts no plugin. Under
  its own name it hooks the game's DirectInput8Create call and starts its plugins there
  (`engine_mods::xhl_core_chain_dlls`). Kiln writes and removes only its own files there (exact bytes, receipt in
  `library-placements.json`); XHL-Core's log next to it stays. Then activation writes
  `<configDir>/native.json` (the profile's native switches), `chain.ini [profile]` (dllmain DLLs; `[chain]`
  byte-identical), `kiln/profile.json` (generation = previous + 1), clears `chain/once.ini` and
  `profile.once.json`, records `<data>/installs/<id>.active.json`.
- `copy_back(pid, install)` after the game exits (copy mode), `deactivate` (profile.json to `kiln/backups/`,
  `[profile]` cleared), `deactivate_report` (also returns the fixed-folder placement cleanup result), `active`, `render`
  (no writes), `read_game_profile`, `write_once` (launch mode once). Activation never edits player-owned SF folders.
  Cleanup needs a trusted placement receipt plus unchanged contents, including exact SF settings bytes (old receipts
  without these remain ambiguous). Changed copies remain where they are; the launcher lists paths/reasons and warns
  that Kiln's engine plugin (such as SET's) or another loader may still run them. No player settings or source Library files are deleted blindly.

## Launch, play-once, chain.ini
- `launch::play(dirs, install, request)`: refuses while running; mode `profile` clears a stale play-once flag, `vanilla`
  / `no-mods` write `kiln/play-once.ini`, `once` renders `profile.once.json` + `chain/once.ini` and writes mode
  `profile`; a save backup (`saves`, reason `launch`) when the kms1 of what loads differs from the last launch
  (`<data>/installs/<id>.launch.json`; vanilla and no-mods are the empty set); then `steam://rungameid/<app>`
  (xdg-open / ShellExecuteW). A failure removes the flag again. `play_with` takes the save folders and the opener
  (tests). Then `wait_started`, `read_boot_for(install, nonce)`, `boot_is_current` (boot.json was written after the
  running game's process started; on Windows its pid must also be that process's).
- `hostenv::{command, clean, clean_with, changes}`: every program of the host the crate starts (xdg-open and with it
  Steam, Wine, SteamCMD, tar, cp, ssh, systemctl, archive tools, xdg-mime, secret-tool, a forwarded nxm handler) gets
  this process's environment without the launcher AppImage's entries. AppRun points `PYTHONHOME`, `PYTHONPATH`,
  `PERLLIB`, `LD_LIBRARY_PATH`, `PATH`, `XDG_DATA_DIRS`, `QT_PLUGIN_PATH` and `GSETTINGS_SCHEMA_DIR` into the mounted
  image (`$APPDIR`); a child inherits them. Seen on 2026-11-11: Play with Steam closed started Steam through
  `xdg-open`, Steam kept the AppImage's `PYTHONHOME`, Proton's Python launch script died with "Failed to import
  encodings module", and no game of that Steam session started until Steam was restarted from the desktop. Rule:
  every `:`-separated entry at or under `$APPDIR` is dropped, a variable left empty is removed, `APPDIR`, `APPIMAGE`,
  `ARGV0` and `OWD` do not go along; with `APPDIR` unset nothing changes. New spawn sites use `hostenv::command`.
- `playonce::{render, write, pending, clear, new_nonce, decide, decide_detail}`; `chain::{parse, parse_once, read,
  write_chain_section, write_profile_section, add_chain_entry, remove_chain_entry, write_once}`. Both match every
  `schema/fixtures/ini` case (kini.py).

## Configs, saves, logs, control
- `config` (offline mod configs; JSONC edits keep comments and Kiln's meta lines through `jsonc`'s lossless tree;
  validation as Kiln.Api's `ConfigSchema`), `read_native_state` / `write_native_state`.
- `saves`: slots in Steam `userdata/<id>/1203620/remote` and `Saved Games/keen games/Enshrouded` (prefix under Proton),
  `backup` / `backups` / `restore` (refused while running; backs up first) / `delete`, 10 automatic kept, manual never
  pruned; `saves::editor` = the Save Editor's commands.
- `logs`: `list`, `read` (CR-tolerant chunks), `Follower` (truncation and replacement), `crash_reports`,
  `boot_state`, `tick_stats`, `mod_stats`.
- `control::send(game, command, timeout)` (`kiln/control`), `control::console` (`kiln/control/console`, Kiln.Chat).

## Parity with the C# loader
`semver`, `manifest` (kiln.mod.json, native.json) and `resolver` are ports of SemVer.cs, Manifest.cs and Resolver.cs;
`tests/parity.rs` runs every `schema/fixtures/{semver,manifest,resolver}` case with the same texts the C# tests check.
