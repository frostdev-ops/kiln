# kiln-manage: the core modules

`crates/kiln-manage` is the library behind kilnctl and the Kiln Launcher. This page covers the core
modules (contracts.md 12): what each does, the functions the Tauri commands call, and the behaviour callers rely on.
Signatures are in the source; every function returns `kiln_manage::Result<T>` (`Error {code, message, detail}`).
Server, admin and modpack modules: [servers.md](servers.md), [admin-protocol.md](admin-protocol.md), [modpacks.md](modpacks.md).

Features: the default build (kilnctl without features, the launcher's `--selftest`) has everything except zips
(`zip`: bundle read/create/verify/extract, zip import in the library) and zstd (`saves`: the save editor's
compression). kilnctl enables `server` + `zip`, the launcher `full`. Builds and tests pass on Linux and
`x86_64-pc-windows-msvc` (`cargo xwin build --tests`, run under Wine).

## Data and paths
- `paths::DataDirs::locate()`: `$KILN_DATA_DIR`, else `$XDG_DATA_HOME/kiln` / `~/.local/share/kiln`, Windows
  `%LOCALAPPDATA%\Kiln`. Tests and kilnctl runs set `KILN_DATA_DIR`.
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
  warnings for entries missing from the library.
- `activate(pid, install)`: refuses while running and without `kiln/`; path mode (Z when `pfx/dosdevices/z:` -> `/`,
  else copy; an untranslatable path also means copy); copy mode syncs verified copies into
  `<game>/kiln/profiles/<pid>/<kind>/<id>/` (unchanged copies stay, stale ones go) and copies the game-side config
  back first; writes `<configDir>/native.json` (the profile's native switches), `chain.ini [profile]` (dllmain DLLs;
  `[chain]` byte-identical), `kiln/profile.json` (generation = previous + 1), clears `chain/once.ini` and
  `profile.once.json`, records `<data>/installs/<id>.active.json`.
- `copy_back(pid, install)` after the game exits (copy mode), `deactivate` (profile.json to `kiln/backups/`,
  `[profile]` cleared), `deactivate_report` (also returns the fixed-folder placement cleanup result), `active`, `render`
  (no writes), `read_game_profile`, `write_once` (launch mode once). Activation never edits player-owned SF folders.
  Cleanup needs a trusted placement receipt plus unchanged contents, including exact SF settings bytes (old receipts
  without these remain ambiguous). Changed copies remain where they are; the launcher lists paths/reasons and warns
  that the standalone loader may still run them. No player settings or source Library files are deleted blindly.

## Launch, play-once, chain.ini
- `launch::play(dirs, install, request)`: refuses while running; mode `profile` clears a stale play-once flag, `vanilla`
  / `no-mods` write `kiln/play-once.ini`, `once` renders `profile.once.json` + `chain/once.ini` and writes mode
  `profile`; a save backup (`saves`, reason `launch`) when the kms1 of what loads differs from the last launch
  (`<data>/installs/<id>.launch.json`; vanilla and no-mods are the empty set); then `steam://rungameid/<app>`
  (xdg-open / ShellExecuteW). A failure removes the flag again. `play_with` takes the save folders and the opener
  (tests). Then `wait_started`, `read_boot_for(install, nonce)`, `boot_is_current` (boot.json was written after the
  running game's process started; on Windows its pid must also be that process's).
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
