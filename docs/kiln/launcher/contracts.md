# Contracts

The formats, flows, names and module boundaries the launcher, kilnctl and the runtime share. Admin transport:
[admin-protocol.md](admin-protocol.md).

Adding optional fields, new commands, new error codes or new enum values at the end is fine; every reader must
ignore unknown keys. Anything else is a format change: update every reader with it.

Machine-readable copies:
- JSON Schemas (draft 2020-12) in `schema/*.schema.json`, examples in `schema/examples/`, fixtures in
  `schema/fixtures/`.
- Rust types in `crates/kiln-manage`. `cargo test -p kiln-manage`
  deserializes every example and checks the kdh1 fixtures and the KAP test vector.
- C ABI in `native/core/abi/kiln_api.h`, `kiln_native.h`, `services/kiln_profile.h`.
- Tauri commands in `launcher/src-tauri/src/commands/*.rs` and `launcher/src/api/commands.ts`.

## 1. Conventions (all formats)
- JSON files are UTF-8 without BOM, keys in camelCase, and carry `"format": 1` (the format's major version). Readers
  refuse a larger `format`, ignore unknown keys, and treat a missing optional key as its default.
- Files Kiln or the launcher write are replaced atomically: write `<name>.tmp` in the same folder, flush, rename over
  the target. Readers never see half a file.
- Times are RFC 3339 UTC strings (`2026-11-04T12:00:00Z`). Exception: `play-once.ini` `created` (unix seconds, or
  RFC 3339 with a zone).
- Hashes are lower-case hex. `sha256` = 64 hex chars; directory hashes are `kdh1:<64 hex>` (section 5.11); mod-set
  fingerprints are `kms1:<64 hex>`.
- Mod ids follow the manifest rule `^[a-z0-9][a-z0-9._-]{0,63}$` for every kind (Kiln, native, EMF).
- "Game-side path": a path as the game process sees it (section 6). "Host path": as the launcher sees it.
- Relative paths inside formats use `/` and never contain `..`, unless the format says otherwise.
- Kiln's own JSON (manifests, configs) may contain `//` comments and trailing commas; the launcher's formats
  (profile.json, receipts, state files) are plain JSON.

## 2. Files at a glance
| File | Written by | Read by | Schema / example |
|---|---|---|---|
| `<game>/kiln/profile.json` | launcher (activation) | kiln-core, Kiln.Host, native loader, kiln-emf | `profile.schema.json`, `examples/profile.json`, `profile-copy.json` |
| `<game>/kiln/profile.once.json` | launcher (launch mode once) | kiln-core | same schema |
| `<game>/kiln/play-once.ini` | launcher | proxy (deletes it) | section 5.2, `fixtures/ini/play-once/`, `examples/play-once.ini` |
| `<game>/kiln/chain.ini` | installer ([chain]), activation ([profile]) | proxy | section 5.3, `fixtures/ini/chain/`, `examples/chain.ini` |
| `<game>/kiln/chain/once.ini` | launcher (launch mode once) | proxy | section 5.3, `examples/once.ini` |
| `<game>/dinput8-chain.ini` | EMF's installer | proxy (EMF rules) | EMF `docs/modding/other_mods.md` |
| `native.json` (mod folder) | mod author | native loader, launcher | `native.schema.json`, `examples/native.json`, `native-plain.json` |
| `<configDir>/native.json` | launcher | native loader | `native-state.schema.json`, `examples/native-state.json` |
| `<configDir>/.schema/<file>.json` | Kiln.Api `Config.Register`, kiln-emf | launcher | `config-schema.schema.json`, `examples/config-schema*.json` |
| `kiln-runtime.json` (bundle root) | `kilnctl bundle create` | installer | `kiln-runtime.schema.json`, `examples/kiln-runtime.json` |
| `<game>/kiln/.install.json` | installer | installer, launcher | `install-receipt.schema.json`, `examples/install-receipt.json` |
| `kiln-modpack.json` (modpack root) | modpack build (format 1), pack build (format 2, a Crucible `.kilnpack`: [crucible.md](crucible.md)) | launcher | `kiln-modpack.schema.json`, `examples/kiln-modpack.json`, `examples/kiln-pack.json` |
| `profiles/<pid>/pack/instance.json` (+ `base/`, `undo/`) | launcher (Crucible pack install/update) | launcher | `kiln-pack-instance.schema.json`, `examples/kiln-pack-instance.json` |
| `<game>/kiln/state/boot.json` | kiln-core | launcher | `state-boot.schema.json`, `examples/state-boot.json` |
| `<game>/kiln/state/progress.json` | kiln-core (dev.27; services, plugins and Kiln.Host report through kiln.progress v2) | launcher, server operators | `state-progress.schema.json`, `examples/state-progress.json`; section "Start progress" below |
| `<game>/kiln/state/mods.json` | Kiln.Host | launcher | `state-mods.schema.json`, `examples/state-mods.json` |
| `<game>/kiln/state/roster-refused.json` (`.last.json` after the next start) | kiln.knowledge (0.2.0-dev.29), when the mods' NPC roster stops the start | launcher, server operators | `state-roster-refused.schema.json`, `examples/state-roster-refused.json`; section 5.9 |
| `<game>/kiln/state/content-refused.json` (`.last.json` after the next start) | kiln.knowledge (0.2.0-dev.29), when the whole build of the mods' native quests and NPCs failed and stops the start | launcher, server operators | `state-content-refused.schema.json`, `examples/state-content-refused.json`; section 5.9 |
| `<game>/kiln/state/native.json` | native loader (core) | launcher | `state-native.schema.json`, `examples/state-native.json` |
| `<game>/kiln/state/debug/<windowsPid>.json` | kiln-core, only with kiln.json `"developer_debugging": true` (0.2.0-dev.31), after .NET started; removed at a clean exit, a stale one by the next `kiln debug` | kiln debug, the VS Code extension | `state-debug.schema.json`; `native/core/src/debug_descriptor.h` |
| `<game>/kiln/state/debug/session.json` | `kiln debug --dap` at every state change of its session (0.2.0-dev.31); the last state (`detached`, `ended`) stays, and only `attaching`, `running`, `paused` or `detaching` written by a proxy that still runs means attached | kiln reload, kiln deploy --reload, the SDK's KilnReload (KILN012), the VS Code extension | `kiln_manage::debug::SessionFile` (format 1: proxyPid, proxyStarted, state = DebugSessionState) |
| `<game>/kiln/state/hooks.json` | kiln-core (kiln.hooks v2, compat report) | launcher | `hooks-state.schema.json`, `examples/hooks-state.json`, `fixtures/hooks-state/` |
| `<game>/kiln/compat-rules.json`, `<configDir>/compat-rules.json` | Kiln (shipped in the runtime's `core` component since dev.29, from `native/core/compat-rules.json`; replaced by every update; may be missing on an older install) / the player | kiln-core, launcher | `compat-rules.schema.json`, `examples/compat-rules.json`, `fixtures/compat-rules/cases.json` |
| `<game>/kiln/state/emf.json` | kiln-emf | launcher | format in `docs/re/emf-compat.md` |
| `<game>/kiln/state/last-server.json` | Kiln.Network (client) | launcher | `state-last-server.schema.json`, `examples/state-last-server.json` |
| `<game>/kiln/state/graphics.json` | Kiln.Ui (client, 0.2.0-dev.29.18): what Kiln's graphics tools do, the upscaler behind the game's FSR 3 above all; written atomically at start and when it changes (checked every 2 s; no frame rates) | launcher (`GameStatus.graphicsState`; "Now" while the process that wrote it runs: `launch::written_after_start` against that process, and on Windows its pid) | `state-graphics.schema.json`, `examples/state-graphics.json`; [../graphics.md](../graphics.md) "What runs now, and why not" |
| `<game>/kiln/state/registry.json` | kiln.content's registry stage (0.2.0-dev.31, experimental KILNX031): written atomically when the stage ends (phase `start`) and again at each world load (`worldLoad`, the generation grows) | launcher (the Conflicts view, registry P2), kilnctl, support reports | `state-registry.schema.json`, `examples/state-registry.json`; section 5.9 |
| `<game>/kiln/data/content/registry/rollback.json`, `plans/<sha256>.json`, `plans/index.json` | the request: `kilnctl registry rollback` and the launcher's Conflicts view (Registry plan card, Tauri `registry_rollback`; since 0.2.0-dev.31.17) (`kiln_manage::registry_plan::ask`, removed by `--clear` / Clear); the plans and their index: kiln.content's registry stage, every start (older plans no start or pending request needs are removed at the end of a successful start, the newest 10 others kept; 0.2.0-dev.31.18) | the stage (the request, at every start until it is removed), kilnctl (`registry status`, `rollback`), the launcher (the plan card's dry run) | request `{format: 1, plan, from, requested, by}`; plans format 2 ([registries README](../modding/registries/README.md#plans-and-rollback)); registry.json `plan`, `rollback` |
| `<server>/kiln/data/kiln-admin/endpoint.json` | Kiln.Admin | launcher, kilnctl | `admin-endpoint.schema.json`, `examples/admin-endpoint.json` |
| `<server>/kiln/data/kiln-admin/admin-token` | Kiln.Admin (first start) | launcher, kilnctl | 64 hex chars ([admin-protocol.md](admin-protocol.md)) |
| `<game>/kiln/logs/proxy.log` | proxy | humans, launcher | one line per decision, rewritten at each start |
| `kiln.mod.json` | mod author | Kiln.Host, Kiln.Sdk, launcher | `kiln.mod.schema.json` (+ `clients`) |
| `content/*.json` (mod folder) | mod author | kiln.content, `kiln lint` | `content-pack.schema.json` |
| `skills/*.json` (mod folder) | mod author | kiln.skills, Kiln.Skills, `kiln lint` | `skills.schema.json` |
| `assets/assets.json` (mod folder) | mod author, cook tools | kiln.assets, `kiln lint` | `assets.schema.json` |
| `assets/cook.json`, `impact/cook.json` (mod folder) | mod author | modelcook, soundcook, tools/impact, `kiln lint` | `cook.schema.json` |
| `impact/natives.json` (mod folder) | mod author | tools/impact, kiln.abilities, `kiln lint` | `impact-natives.schema.json` |
| `*.kschem` header | Kiln.Build | Kiln.Build, `kiln lint` | `kschem-header.schema.json` |
| `schema/schemas.json`, `schema/idrefs.json` | `kiln lint` (`idrefs.json` is generated) | `kiln lint`, tools/vscode | which schema and JSON dialect each file has; where files name game entries |
| `kiln lint --json`, `library_lint` result | `kiln lint` | tools/vscode, launcher | `lint-report.schema.json`, `fixtures/lint/output.json` |
| `launcher/src/data/vendor-components.json` (compiled into the launcher) | Kiln's release process (`tools/release/vendor_pin.py` pins the hashes) | launcher | `vendor-components.schema.json`, `examples/vendor-components.json`; section 5.15 |
| `<game>/kiln/vendor/<vendor>/<version>/kiln-vendor.json` | launcher (vendor downloads, 0.2.0-dev.29) | launcher; Kiln in the game from phase B | `vendor-receipt.schema.json`, `examples/vendor-receipt.json`; section 5.15 |
| `<data>/state/vendor-licenses.json` | launcher (the licence dialog, 0.2.0-dev.29) | launcher | `vendor-licenses.schema.json`, `examples/vendor-licenses.json`; section 5.15 |

### Start progress (`kiln/state/progress.json`, 0.2.0-dev.27)
What a start is doing from `kiln_boot` until the game can be used, for the launcher's progress bar and time estimate.
kiln-core is the one writer: it replaces the file whole (`<file>.tmp`, then a rename) at once at every change of step
or state and at most every 250 ms for a change of label, detail or count (the last change is never lost), from a
thread of its own (nothing in the game waits for the disk, and no write comes from a per-frame hook). Fields:
`format` 1, `pid`, `kiln` (with the build stamp), `side`, `started` (UTC, milliseconds), `atMs`, `state`
(`booting`, `loading`, `ready`, `idle`, `failed`), `plan`, `steps` (`id`, `label`, `detail`, `done`/`total`, `ms`,
`tookMs`, `parts`), `why`, and (0.2.0-dev.29) `worldMs`: when the game's first world began, ms since `started`, written
at once (absent before). A stale file is told by `pid` and `started`, as for boot.json; nothing deletes it, and it
is not written again after `ready`, `idle` or `failed`. boot.json is unchanged.

| Step | What Kiln does there | Detail, count |
|---|---|---|
| `start` | kiln.json, the profile, compat rules, hashing the game image (from boot's first moment) | - |
| `native-early` | native mods of phase early; waiting for another loader's code scans (label says so) | - |
| `early-plugins` | `kiln_plugin_early` and its on_ready of each early plugin (ShroudForge's startup, EML's preparation) | the plugin by its loader's name (the progress window's text replaces it while it shows); early plugins; parts per plugin |
| `signatures` | sigdb: finding the game's functions | - |
| `services` | the tick hook, the in-core services, every service DLL's `kiln_plugin_main` | the service; service DLLs; parts `<name>: starting` |
| `plugins` | every plugin's `kiln_plugin_main`, then every module's `on_ready` (label "Getting Kiln's services and plugins ready"; kiln-vfx builds the mods' materials and VFX here), the exit link | the module (kiln-vfx: the mod and block it builds); modules with on_ready; parts `<name>: getting ready` |
| `native` | native mods of phase boot, world armed | - |
| `dotnet` | .NET and Kiln.Host start; chain.ini `[late]` DLLs | `Kiln.Host` |
| `mods` | from the end of boot (state `loading`; label "Waiting for the game to load the mods") until Kiln.Host has loaded the managed mods (client: the game's first menu frame; server: the world's first step) | the mod; mods |
| `content` | Kiln.Blocks compiles the loaded mods' `blocks/*.json` (materials are checked against kiln-vfx's boot build); none: it ends at once | `<mod>: <section> <block>`; data blocks |
| `game` | Kiln is done; the game goes on loading | - |

`ready`: once step `game` has begun (the mods are loaded and their content applied) and the side's signal has come,
in whichever order (a signal that comes first is remembered): a client's when kiln-ui sees the main menu's builder run
(or the first in-game frame), or (0.2.0-dev.29) when its first world begins, so a start whose menu mark never comes
(another mod's hook over the menu) still ends, and kiln.log says the mark had not come; a server's at Kiln.Host's
ServerStarted (what `kilnctl run --wait` waits for), or without .NET the server world's first step. A step begins when
the one before it ends, so in a final state the steps' `tookMs` add up to `atMs`. The launcher takes a client whose file
has `worldMs` and has said nothing for 60 s for a start that is over (the player is in a world), not for a silent one.

Shared definitions (ids, semver, kdh1, game paths): `schema/common.schema.json`. Validate every example with
`bun run check:schemas` in `launcher/`.

## 3. Launcher data
Root: `$KILN_DATA_DIR`, else the folder the pointer `data-folder.json` in the default root names, else the default root:
`$XDG_DATA_HOME/kiln` or `~/.local/share/kiln` (Linux), `%LOCALAPPDATA%\Kiln` (Windows).
`kiln_manage::paths::DataDirs::locate` (= `DataEnv::from_process().locate()`); the data folder's own rules below
("The data folder's place").

| Path | What |
|---|---|
| `settings.json` | launcher settings (`LauncherSettings`, section 9) |
| `community.json` | (Discord) the Community page shown, Rich Presence, reply notifications, the relay's address ([discord.md](discord.md)) |
| `community-session.json` | (Discord) the Kiln session key and its account, owner-only, only when the system has no keyring |
| `community-known.json` | (Discord) the last copy of the public known-problems list |
| `community-tester.json` | (Discord) a tester's results for the current test build, until Send results |
| `library/<kind>/<id>/<version>+<hash8>/` | immutable library entries (their files writable by the owner from 0.2.0-dev.29.18: an import from a folder no longer keeps a read-only file read-only, and neither does a placement from the entry); kind `kiln`, `native`, `plugin`, `emf`, or another engine's (`eml`, `shroudforge`, `set`, `greeneye`, `flame`, `shroudtopia`, `enshwick`, `shroudlink`, `ember`), see 5.1; hash8 = the first 8 hex chars of the folder's hash: its kdh1, for a Kiln mod its identity (5.11: kdh1 without what a cook left in `assets/cooked`, so an entry cooked in place is still the entry that was imported); of the DLL's sha256 for a plugin, whose folder holds the one DLL |
| `library/<kind>/<id>/<version>+<hash8>.json` | import record next to the folder: `{format, kind, id, version, hash, imported, source:{type,from}, size, name?, side?, clients?, file?, manifestId?}`. `manifestId` (dev.22): an engine mod whose own manifest id is no Kiln id (`Hoarders Helper`, `antisocialian's tweaks`) is filed under a Kiln id made from it (`hoarders-helper`) and keeps its own id here; the files in the folder are the author's, byte for byte, because the engine plugin reads that manifest and reports its state rows (`kiln/state/<engine>.json`) under it. Rows that carry a `path` match their profile entry by it; the mod manager (`ModCatalog`) also matches a row by the slug of its id. Two manifest ids with one slug get a short hash added to the second one's id |
| `library/kiln-kept.json` | (dev.29) the earlier versions an update kept for a roll back (`kiln_manage::kept`; `kiln-kept.schema.json`, `examples/kiln-kept.json`): per mod (`kind`, `id`) the kept entries, newest first (`version`, `hash`, `kept` time, `replacedBy`, `profile`: the profile whose change left it, and the `settings` kept with it: their folder `dir`, the files copied and their bytes, ShroudForge setting values, the game folder they came from), at most `keepVersions` (`settings.json`, default 1, at most 5; 0 keeps none), and `restores`: the settings a roll back left for its version's next placement, one per profile, in the game folder they came from (`preimages`: `files[gameRelativePath]` and `values[copyRelativePath][settingKey]` each hold `{before: sha256 | null, snapshot: sha256}`; `null` means absent; hashes of values use their serialized JSON. These guards are captured in the first durable restore record before settings writes. Each attempt settles matching snapshot content, writes only matching preimage content, and preserves/notes everything else. A missing preimage block is a legacy record: differing current content is preserved and settled, never overwritten. If both the preimage and current state are absent, retry writes the snapshot; a later deletion is preserved when the preimage was present. `applied`: its files and values are settled, restored content is flushed, and only the record is left to drop; a restore is marked so, then dropped, so a stop between the two never puts the files back again over a later change). `pending`: the record of a change written with the new state before its profile is saved (the profile, each mod's version before and after, each touched mod's state before, and `profileBefore`: the prior profile mod entries, including removals). **Recovery:** the file is written in one durable write (`util::write_atomic_durable_exclusive`, through the one seam `kiln_manage::durable`) only after what it names is flushed (a settings copy's files and folders, the name of its folder, the folders above it; for a commit, the profile's `meta.json` and its folder; for a restore marked done, the restored files and the folder entries naming them), under `library/kiln-kept.lock`, held from before the profile save until the record is dropped; at every later look under that lock (the launcher's start, every change, every placement) a `pending` change is undone when its touched profile entries still match `profileBefore` (legacy records compare the replaced versions), else finished; an entry whose Library entry is gone is dropped, and a missing settings copy is dropped with a note, a kept entry its own profile uses again is dropped, and every folder below `library/kiln-kept/` the file does not name is deleted (never through a link). Nothing else is touched by it. A kept entry is an ordinary Library entry (folder and record): a launcher before dev.29 does not read this file, lists a kept entry as another version of the mod ("Use <version>") and its Clean up may remove it; dev.29 then forgets it. An unreadable, malformed, or newer-format index is left untouched and its settings copies are not swept. Entries owned by a profile whose file is demonstrably absent are dropped. A file of another format is left as it is and nothing is kept |
| `library/kiln-kept/<kind>/<id>/<dir>/` | (dev.29) one settings copy, `<dir>` = `<version>+<hash8>.<12 hex>` (a new copy always gets a new folder; it is made in `.stage-<12 hex>` beside it and renamed once whole): each file at its path relative to the game folder (`greeneye/worstmap/minimap_config.ini`), copied from Kiln's game-folder copies of the version (`kiln/state/library-placements.json`) at the update: the files not as Kiln placed them (changed or added since) that are settings files or logs (`txt`, `ini`, `cfg`, `json`, `toml`, `yaml`, `yml`, `log`) and no manifest (`mod.json`, `extended.mod.json`, `native-plugin.ini`, the other engines' manifests at the top), never Lua, `src/` or anything else (scripts, DLLs), at most 16 MiB each and 64 MiB per version. Deleted once `kiln-kept.json` no longer names it |
| `profiles/<pid>/meta.json` | the profile definition (`kiln_manage::profile::Profile`; dev.21 adds `late`: native mod ids whose dllmain DLLs go to chain.ini `[late]`, left out when empty; dev.22 adds `dropinsEngines: true`: `dropins` names the engine kinds too, profiles.md "Drop-in folders") |
| `profiles/<pid>/config/` | the profile's mod configs (its `configDir`), with `.schema/`, `native.json` and the player's `compat-rules.json` |
| `profiles/<pid>/data/` | per-mod data when the profile chooses `dataDir: profile` |
| `web-panel-origins.json` | (0.2.0-dev.31.12) "Open web panel": each server panel's own loopback address and port (`{format: 1, servers: {"<key>": {ip: "127.x.y.z", port}}}`, `kiln_manage::admin::web_panel::origins`; key = the server id, or `<server id>@<certificate SHA-256>` for a panel with TLS, so a panel with another certificate gets another address and no panel's cookies reach another): 127.64.0.0 to 127.254.255.255, picked from a hash of the key and never given to another key, even after its server is removed (so no old cookie reaches a new server). An entry outside that range is not used (a new one is picked). Written under `web-panel-origins.json.lock` (several launchers on one data folder). Safe to delete: the same addresses come back from the same hashes |
| `profiles/<pid>/config-sync/<installId>.json` | (0.2.0-dev.29.16) copy mode: the game's config copy of the profile as Kiln last left it (`kiln_manage::profile::config_sync_record`): `{format: 1, files: {"<path below config/, '/'-separated>": {sha256, size, mtimeNs}}}`, written after every copy into the game and every copy back (section 6). The hash decides; size and time are for a reader. Safe to delete: the next copy back then decides by time |
| `profiles/<pid>/save-backups/<yyyymmddThhmmssZ>/` | automatic save backups, 10 kept (+ `backup.json`) |
| `installs/<installId>.json` | the launcher's record of a game install: the `GameInstall` + a copy of its receipt; (dev.29.17) `start` (`StartSettings {mode?: 'steam'\|'direct', runner?, prefix?}`: how the player wants it started, left out while every choice is Kiln's default; kept whoever writes the record again; [custom-game-folders.md](custom-game-folders.md)) |
| `installs/<installId>.notices-seen.json` | (dev.26) which activation notices the player has closed in the launcher for that game: `{format: 1, seen: [sha256 hex of each sentence]}`, oldest first, at most 512 (`kiln_manage::notices`, `notices_shown`; written through a temp file of its own random name) |
| `installs/<installId>.notices-seen.lock` | (dev.27) an empty file two launchers lock while one of them reads, changes and writes the memory above, so neither drops what the other remembered (`kiln_manage::notices::mark_seen`). Never read; safe to delete while no launcher runs |
| `runtime/kiln-runtime-<version>.zip` | runtime bundles (import, rollback) |
| `schema-cache/<modid>/<version>+<hash8>/<file>.json` | config schema snapshots cached per mod version |
| `backups/` | `saves/<gameId>/` (save backups without a profile), `install/<installId>/` (mirror of install backups), `profiles/` (deleted profiles), `mod-settings/<kind>-<id>-<ts>/` (a removed mod's settings, "Remove its settings too": the files as they were laid out under `games/<installId>/` and `profiles/<pid>/config/`, and `removed.json` = `{format, kind, id, removed, items: [{from, backup, entry?}]}`) |
| `logs/launcher.log` | the launcher's own log |
| `logs/game-start-<installId>.log` | (dev.29.17) the umu-run, Proton or Wine output of the last direct start of that install (written again at every one; the support report carries it) |
| `state/software-render.json` | Linux: there, a start without an override draws without the GPU (`{format, why, at, version}`; written by the launcher when its page did not start, or by Settings; [app.md](app.md), "Drawing without the GPU") |
| `state/webkit-sandbox.json` | (dev.29.18) Linux: there, a start without an override turns WebKit's process sandbox off (sets `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1` before GTK and WebKit start; a value the player set stays). `{format, why, at, version}`, the same shape as `software-render.json`; written by Settings, or after the player's click in one of the launcher's messages (by the restart it starts, once the beta copy's gate is open); never without such a choice. `--webkit-sandbox` / `--no-webkit-sandbox` and `KILN_LAUNCHER_WEBKIT_SANDBOX=on\|off` win over it ([app.md](app.md), "WebKit's sandbox (Linux)") |
| `state/desktop-entry.json` | Linux AppImage: there when the player turned "Kiln Launcher in the app menu" off (`{format, on: false, at}`); absent, every start writes or updates the app menu entry and icons ([app.md](app.md), "Taskbar icon and app menu") |
| `state/vendor-licenses.json` | (0.2.0-dev.29) the vendor licences the player agreed to in Settings, Graphics (Upscaler files; the Deck's Settings), per vendor and SHA-256 of the text (`kiln_manage::vendor::agreements`; section 5.15). Here and not in a game's `kiln.json`: the player agrees once on this PC, the game never reads it, and `kiln.json` is per install and written by the game and other tools. Lost or unreadable: the dialog comes again |
| `update.json` | the self-update's state: `{format, skipped, checked, updated: {from, to, at, source, notes}}` (releasing.md) |
| `servers/<id>.json` | launcher records of servers that are not kilnctl instances (steam, windows, remote) and renames (`ServerInstance`) |
| `modpacks/downloads/<sha256>.zip`, `modpacks/built/<serverId>/`, `modpacks/staging/`, `modpacks/dismissed.json` | modpack downloads (reused when the sha256 matches), built server modpacks, unpack staging, offers and refusals the user dismissed (`{gameId: {serverId: modpack sha256 | "refused <written>"}}`) |
| `nexus/` | the catalog on Nexus Mods ([catalog.md](catalog.md)): `oauth.json` (OAuth token pair and expiry, 0600, only without an OS keyring backend; existing backend failures are surfaced; retired personal-key entries are removed), `handler.json` (the nxm opt-in and the handler before Kiln; a build with Nexus off gives the links back at start and deletes it: `{format, registered, previous: {type: "desktop", id} \| {type: "command", command, icon, scope: "hkcu"\|"hklm"} \| null, exe, registeredAt, snapshot}`; format 2 snapshots preserve overwritten OS state before mutation), `verdicts.json` (what each mod's files hold, per mod and its `updated_at`), `installs.json` (mods installed from the catalog: `{format, mods: InstallRecord[]}`), `latest.json` (newest version per mod file, checked at most every 30 min), `inbox/*.json` (nxm links handed over by a second launcher process; private owner-only directory/files, bounded reads by the instance-lock owner), `downloads/<mod>-<file>/download-<random>/`, state transaction locks, `instance.lock` (held while a launcher runs) |
| `updates/receipts.json` | source receipts of update checks (`kiln_manage::updates::receipt`; `update-receipts.schema.json`, `examples/update-receipts.json`): per library entry what Kiln saw when it was installed or imported (the archive's SHA-256, a Nexus Mods file and version id; nothing observed is `null`), and per mod the update source that is accepted for it (a manual mapping with its source). Written at a library import of an archive, a Nexus Mods install and a check; never holds a credential or a path of the PC. A file of a newer format is left as it is |
| `updates/cache.json` | what update checks remember (`kiln_manage::updates::cache`): per source of one mod the last selection, when it was answered, ETag / Last-Modified, the wait after a failure, and request limits per provider host. A cache: one that cannot be read is dropped and built again; at most 10 MiB. `updates/.lock` serializes writes to both files (the launcher and `kiln updates check`) |

Profile ids: `^[a-z0-9][a-z0-9-]{0,47}$`, derived from the name (`Server: Ember Isles` -> `server-ember-isles`,
a `-2` suffix on clashes).

### The data folder's place (dev.26)
The whole data folder moves as one (`kiln_manage::data_folder`, `kiln_manage::data_folder_move`).
- **Pointer** `<default root>/data-folder.json` `{format, path, written, writer}` (`data-folder.schema.json`): `path` is
  absolute, a host path, without `\\?\`. No pointer: the default root is the data folder. Written and replaced atomically
  (`<name>.tmp`, flush, rename). `$KILN_DATA_DIR` wins over it. A pointer that cannot be read or parsed, has a larger
  `format`, names a relative path, or names a folder that is missing, a file, a link, unreadable or not a Kiln data
  folder is the error `data_folder` (section 11; `detail: {kind, pointer, path}`), never the default root and never an
  empty folder. `DataEnv::locate_checked` gives the same answer as a value for the launcher's dialog. Reading costs the
  pointer and one look at the folder's marker (its entries only without one). After a broken pointer:
  `data_folder_move::reset` renames it to `data-folder.json.old` (the default root is used), or `data_folder_move::repoint`
  points at a Kiln data folder the player picks (refused unless the pointer is broken; the folder must be a Kiln data
  folder, not an unfinished or unrelated move (a matching committed move is allowed), not a link or a file,
  not inside a game folder or Kiln's state or settings folder; only
  the pointer is written, and the previous one is put back byte for byte if the folder is not found through it).
- **A Kiln data folder** (`data_folder::recognize`): it has `.kiln-data-folder.json` (`{format, created}`, written into
  the new place just before the pointer), or a structure only the launcher makes: `library/<kind>/` named after a mod
  kind, `profiles/<pid>/meta.json`, `installs/<id>.json` or `runtime/kiln-runtime-*.zip` (unknown entries may sit next to
  them). A folder with `.kiln-data-move.json` (`{format, id}`) is a move in progress: never an ordinary "use this one"
  or move target; only Choose folder after a broken pointer can recover a matching committed move, as above.
- **Picked folder** (`data_folder::target_rule`): missing or empty (nothing, or only the pointer, the journal and file
  record, `quick.lock`, `data-folder.lock`, and in the default root the entry holding Kiln's state or settings folder):
  it becomes the data folder; a Kiln data folder: used as it is (no merge); anything else, and a volume root or mount
  point, gets the subfolder `Kiln` (Windows) or `kiln`, which must itself be missing, empty or a Kiln data folder. The
  default root never gets the subfolder: with anything else in it (also launcher entries only, such as `settings.json`
  and `logs/` a launcher before dev.26 left there) it is refused (`default_root_taken`, naming the entries: remove them
  first). What every refusal is (`data_folder::RefusalCode`, `check`): `KILN_DATA_DIR` set, another launcher, the quick
  flow busy, the game or a server running, an unfinished move, the target inside, around or the same as the current
  folder, inside a game or server folder or Kiln's state or settings folder, a link, a file, a network path, not
  writable, too small, a path the game reads longer than 259 characters (Windows), a link or special file inside the data
  folder, a target that already holds an entry of a name the move would make there.
- **Lock files** at the top of a data folder are `quick.lock` and `data-folder.lock`, by their names; inside `nexus/`
  every `*.lock`. Any other `*.lock` at the top is an entry the launcher does not know. Lock files are never copied.
- **Lock** `data-folder.lock` in the data root (`data_folder::DataLock`): shared by every launcher process from dev.26 on
  for as long as it runs, by `kiln` for its whole command when the root exists, and by `kilnctl` for every command
  that uses the root (the library, profiles, client and bundle installs, `deploy --bundle`, `backup`, `report`; for
  `update` only its backup step; not the commands on the server folder alone, such as `run`, `start`, `logs` or
  `gate`, so a server in the foreground never blocks a move), exclusive for a move, a switch or
  unfinished-copy recovery; taking it never waits. Recovery upgrades the launcher's lock and holds the partial copy's
  lock exclusively too, before scheduling its task and until it returns (also on Cancel or error). If either lock
  cannot be taken, nothing is removed and the notice asks the player to close other Kiln processes and restart. A move also holds `quick.lock`, and
  holds the target's `data-folder.lock` exclusively from the moment it writes the target's move marker until it returns,
  so a launcher that starts during the move cannot take the new place either.
- **The hash cache** `state/file-hashes.json` (`filecache`, keyed by absolute paths) is never copied: the new place builds
  its own. The old one goes with the old copy's lock files, by its name, except where `state` is the old root's own state
  folder. `filecache::unpersist` stops persisting it for the time a move runs (no flush puts it back into the old
  folder); `filecache::persist_at` starts it again.
- **Journal** `<default root>/data-folder.move.json` (`data-folder-move.schema.json`): `{format, id, from, to, started,
  createdTarget, createdParents, createdLock, entries:[{name, known, bytes}], skipped, state, previousPointer, rewritten,
  rewrittenAfter, hardLinks, result?}`, written atomically before the move writes into the target, kept until the launcher
  acknowledges the result (`data_folder_move::acknowledge`). A move is refused while one exists. Recovery goes by what is
  on disk (does the folder in use match `to`). A destination carrying the folder marker, or a journal past verifying,
  is never an unfinished copy, even after resetting the pointer or while its drive is missing: nothing is removed and
  the journal and file record stay until that folder is in use again. Choose folder accepts that committed destination
  when its move marker matches this journal; reset refuses a default root carrying any move marker. The journal and
  the file record, which the removals depend on, are replaced like the pointer (`data_folder::write_durable`): on
  Windows from a temporary file of their own (`<name>.<32 hex>.tmp`, which counts as one of Kiln's own files where a
  stop leaves it) by a write-through rename (`MoveFileExW` with `MOVEFILE_WRITE_THROUGH`); on Unix by
  `util::write_atomic` and a flush of the folder. That Windows durability is asked for, not verified.
- **File record** `<default root>/data-folder.move.files.json`: `{format, id, files:[{path, size, sha256, linked,
  sourceSha256?}]}`, every file the move copied, written once before the pointer.
- **A move** (`data_folder_move::move_to`): the target check again, the locks, the journal, the target, its lock and its
  `.kiln-data-move.json`, the copy of every entry, known and unknown (not the pointer, the journal, the file record,
  markers, lock files, the hash cache, and an entry the target keeps for itself, `Journal::skipped`: on Windows the
  default root's `state\`, which is `util::state_dir()`; it stays in the old folder and the result names it). A move
  on Unix also compares `(dev, ino)` for existing ancestors before any target or write probe is made, so another
  spelling on a case-insensitive disk cannot put the target inside the source. It
  never copies into, replaces or removes anything in the target that it did not make: a file or folder that is already
  there fails it (`conflict`, named). Hard links on one volume where they work, except what is written in place
  (`data_folder::written_in_place`: `logs/`, `profiles/<pid>/config/` and `data/`, `admin-assets/`, `editor-assets/`,
  `sdk/`, downloads under way), real copies otherwise, with modification times and Unix permissions; links and special
  files refuse the move. Then the verify (every file read back against the sha256 taken while copying; every library
  entry's kdh1 against its import record: an entry that already differed is copied as it is and named in the
  warnings), the source checked for changes (copied again, at most 3 rounds), the file record, the marker, on Unix the
  folders the move made and the target flushed to the disk, **the pointer: the one commit point** (moving to the default
  root removes it instead; on Unix the default root is flushed after it), the new place checked (`locate` gives it;
  profiles, library entries and settings read as before; if not, the previous pointer is put back and the move fails).
  If restoring that pointer fails, the verified new folder stays selected: the result is committed with a warning
  naming both folders and requesting a restart, no old-copy removal runs, and the launcher keeps all writers held.
  Otherwise, install records are written again for the new root (`pathMode`), the games whose `profile.json` was this folder's
  before the move (decided with the old root at the start) activated again, built modpacks' `file` rewritten (failures
  there are warnings), and last the old copy removed (`MoveResult.cleanup`, the rule below; a cancel there interrupts only
  the removal, which goes on at the next start). When a game's profile could not be activated again, that game still
  names the old copy: the removal phase is not run at all, `cleanup` says which game and why (the warning stays too),
  and the journal stays for the next start. `servers/<id>.json` `backupDir` is rewritten from the old root to the
  new one in the copy (its record line holds both hashes). A file to hard-link is read first (sha256 and size) and
  linked only after that read succeeded: a file that cannot be read (Windows: held open without read sharing) fails
  the move before anything of it is in the target. A cancel, an error, a full disk or a file that cannot be read
  before the commit removes what the move made in the target and the journal, also the file a failed copy step had
  just made (a copy cut off by a full disk); the old place is as it was and in use. No removal error of the move's own
  files is thrown away: a file or folder it made that it cannot remove again stays in its record of what it made, is
  listed as kept with the OS error (the error's `detail`, the journal's `result`), and the error names it and its
  folder. Such a clean-up keeps its proof: the target's `.kiln-data-move.json`, its `.kiln-data-folder.json`, its
  lock file and the journal (`state: failed`) stay, so the folder reads as a move in progress (never "use this one",
  never a target), and `remove_partial_copy` removes the rest at the next start.
  On Windows files are flushed one by one, and the pointer replacement uses `MoveFileExW` with
  `MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH` before removal begins. Moving back to the default uses a
  write-through rename to a unique tombstone before its best-effort deletion (a tiny tombstone can stay if deletion
  is refused). This requests Windows durability; Wine does not prove NTFS power-loss ordering or hardware flushes.
- **Removal** (`data_folder_move::remove_old_copy`, also the move's last phase; `pending`, `remove_partial_copy`, never by
  themselves): the old copy of a committed move goes file by file, only where the old file's sha256 is the record's (for
  a rewritten file, the old one's) and the file in the new place has the record's sha256, or the one the move itself
  wrote there after the commit (`rewrittenAfter`: only the install records and activation records of the known games and
  the files under `profiles/<pid>/config/` of the profiles the move activated, recorded where the move writes them). Then
  the hash cache, the lock files, the old markers, empty folders, and the old root when it is empty and is not the
  default root. Entries the launcher does not know and entries the target kept for itself are never removed. Calling it
  again when nothing is left removes what a running move could not (its own lock files, the empty old root). Nothing is
  removed without the journal, with a journal whose id is not the new place's marker's or the record's, without the
  record, or when the folder in use is not `to`; the result then says where the old folder is. Files added to the
  old folder after copying (including inside known entries) stay and are named in `kept` with their size and reason,
  so the notice still names the old folder and offers Open. Nor while any known
  game's `kiln/profile.json`, `kiln/profile.once.json`, `kiln/chain.ini` or `kiln/chain/once.ini` names a path under
  the old root (`configDir`, `dataDir`, a mod folder, a plugin DLL; Z and native mode), whoever wrote it, or cannot be
  read: the result names the game and says that activating its profile again lets the old copy go at the next start.
  A file of the launcher's own it could not remove (lock files, the markers, the hash cache) and an empty folder it
  could not remove (also the empty old root) are in `kept`, with the OS error; a later call tries again.
- **The guard of both removals** (the old copy's and a partial copy's; each removes a file because the identical file
  is in the other place, so the two places must really be two): (1) the move's two folders are two separate real
  folders: neither is a link or a reparse point, their canonical paths are not one and neither is inside the other,
  and they are not one folder by identity (device and inode; on Windows volume serial number and file index from
  `GetFileInformationByHandle`), which also catches a bind mount and two spellings on a disk that does not tell case.
  If not, `pending` is `Unclear` with one sentence that names both folders ("<a> and <b> are not two separate folders
  (<a> is a link), so Kiln removed nothing from either."), nothing is removed and the journal stays. (2) No path below
  either root is reached through a link (`engine_mods::refuse_symlink_path`, junctions included), also for the
  launcher's own files the old copy's removal takes (lock files, the hash cache) and its sweep of empty folders,
  which never enters a folder that is the other place's by another path. (3) The same single file is never removed:
  when the two paths give one identity, it is a hard link (proof without reading) only with two names or more and in
  two different folders; one name, or one folder reached twice (a bind mount over hard links), is one file reached
  twice. What fails (2) or (3), or whose identity cannot be read, stays, named: "a folder on its way, here or in the
  other folder, is a link, which Kiln never follows, so it stays", "it is the very file the other folder holds,
  reached by two paths (a link or a mount), so removing it would remove that one", "it is the very folder the other
  folder holds, reached by two paths (a mount), so nothing in it is removed", "Kiln could not tell whether it is a
  file of its own or the very file the other folder holds, so it stays" (`data_folder_move::guard_reason`).
- **Removing a proved file** (both removals, every file they remove by proof: hard links, the rewritten server record,
  files in `rewrittenAfter`, the cut-off file): what is removed is the file that was proved, never whatever has its path
  by then. The proof reads the file through one handle and takes its identity (device and inode), size and modification
  time from that handle before and after; a file that changed while it was read is not proved. Unix: the file is then
  set aside under `.<name>.kiln-removing` in its own folder (when that name would pass 255 bytes, the limit of ext4 and
  btrfs, counted in the bytes of the UTF-8 name: `.<the first 32 hex of the sha256 of the name>.kiln-removing`, 47
  bytes) by a rename that never replaces a file (`renameat2` with `RENAME_NOREPLACE`; where a file system does not offer
  it, a look first), checked again there (a plain file with the same identity, size and modification time; at or below
  16 MiB its content is read again and compared with the proof, for the cut-off file the prefix check again to its end;
  a hard link proved by its identity is never read) and only then removed. A file that differs is put back under its
  name when that is free, else it stays under the set-aside name; either way it is named in `kept` ("it changed while
  Kiln was removing it (another program wrote it), so it stays", or "... and a new file took its name meanwhile, so it
  stays under the name Kiln had set it aside under"). When the set-aside name is another file's, nothing of that file is
  touched and the file stays at its name ("Kiln sets a file aside under a name of its own before it removes it, and
  another file has that name here, so it stays"). A program that writes the path anew after the file was set aside makes
  a new file, which stays, named (the partial copy's removal looks at it like any file; the old copy's: "another program
  wrote a new file under this name while Kiln removed the one it had proved, so it stays"). A file a later run finds
  under a set-aside name (a stop between setting it aside and removing it) is proved under its original name and removed
  where it is; one its original name does not prove stays as it is, named ("a removal that stopped set it aside under
  this name, and ..."). The original name is found by computing the set-aside name, in either form, of the names it may
  stand for: the record line's name (the old copy), the names in the old folder's matching folder (a partial copy; at
  the target's top level the journal's entries); a short form that stands for none of them is never touched. Windows:
  the file is opened for its proof so that others may read it but not write or delete it, and it is deleted through that
  same handle (`FileDispositionInfoEx` with POSIX semantics, the read-only attribute ignored, which needs only the right
  to delete; else the read-only attribute cleared through the handle and `FileDispositionInfo`); no set-aside name is
  needed there. The handle asks for the right to write the file's attributes too, which that fallback needs, and is
  opened without it where it is refused (Wine refuses it on a read-only file); then a read-only file only the fallback
  could delete stays ("it could not be removed: it is read-only and Windows did not let Kiln clear that, so it stays").
  A file proved by its identity (a hard link) is opened to read its attributes, not its content. A file another program
  has open for writing, or without letting it be deleted, does not open and stays ("it could not be removed: another
  program has it open, so it stays"). Without POSIX deletes (FAT, exFAT; Wine) a file deleted through that handle keeps
  its name while another program has it open with delete sharing: a name that still leads to the removed file, or that
  cannot be opened because its delete is pending, is that file on its way out, not a new file. It is counted removed and
  named ("it is deleted, and its name (and a folder it keeps from being removed) goes when the other program that has it
  open lets go; the next run finishes there"); a partial copy is then not finished, and the next run finishes it. On
  Linux one case stays open: a program outside Kiln that already has a file of the folder open and writes it in the
  moment between Kiln's last check and the removal loses that write. And a second: above 16 MiB the second check
  compares identity, size and modification time only, so a write that keeps the size and leaves the time as it was (a
  writer that sets the time back, a write through a shared mapping to pages that are dirty already), anywhere from the
  end of the proof to the removal, is not caught.
- **A partial copy** found by a later start (`remove_partial_copy`, or `remove_partial_copy_with` with progress and
  cancel) is removed only when the target's `.kiln-data-move.json` has the journal's id, and then only what the old
  folder (still the folder in use, and complete) proves the move made. A plain file below one of the journal's entries
  goes when the old folder has a plain file at the same path with the same size and sha256, or when it is the same file
  (a hard link: two names of one file, not read), or, for `servers/<id>.json`, when it is byte for byte what the move
  writes from the old folder's file (`backupDir` rewritten). One file cut off by the interrupted copy goes too: a file
  shorter than the old folder's file at the same path whose every byte equals the old file's at that offset (an empty
  one too), never `servers/<id>.json` (written whole), at most one per move: it is remembered in the journal's result
  before it goes, so a cancelled and resumed recovery, or one after a crash, gets no second one, and a second such file
  stays, named ("it is the beginning of the file in the old folder, but an interrupted copy cuts off only one file and
  that one was removed already, so Kiln cannot tell that the move made this one"). The result lists it in `cutOff` ("cut
  off by the interrupted copy"). A target `logs/launcher.log` whose old file has grown since (a launcher ran on the old
  folder after the move stopped) is such a beginning too and takes that one removal. The journal's entries are visited
  smallest first (by the journal's bytes, then by name), so the small files a running launcher keeps writing in the old
  folder are checked and gone long before the large entries a player would cancel during. A folder goes when it is empty
  after that and the old folder has it. Nothing is read through a link, in the target or in the old folder (a link on
  the way in the old folder proves nothing). Everything else stays and is named in `kept` with its size and a reason
  (`data_folder_move::partial_reason`): "the old folder has no such file, so Kiln cannot tell that the move made it",
  "it differs from the file in the old folder (added, changed or replaced after the move stopped, or the old folder's
  file changed since)", "it is a link, which Kiln never follows or removes", "it is not a plain file or folder", "the
  move copied nothing by this name" (a top-level entry the journal does not list), "the old folder has no such folder,
  so Kiln cannot tell that the move made it" (an empty folder), "the move never copies this file" (`nexus/*.lock`, the
  hash cache). The launcher's own files at the target's top (lock files; at a default root the pointer, the journal and
  the file record, and the entry holding Kiln's state) are not the copy's and stay without a word. Leftovers of the
  move's marker writes (`.kiln-data-move.json.tmp`, `.kiln-data-folder.json.tmp`) go only when their content shows they
  are this move's (the journal's id; a folder marker created at or after the move started); any other stays, named ("its
  content does not show that this move wrote it"). Only when nothing was kept do the folder marker, the lock file the
  move made and the move marker (last), the target and the folders above it that the move made, and the journal and file
  record go. With anything kept they stay, `pending` stays `Uncommitted`, and the next call tries again: once the player
  has taken the named files out, the rest goes. A target that is gone, or that the player emptied by hand (no marker,
  nothing in it but the launcher's own files above: the lock file the move made, at a default root the pointer and the
  journal), ends the move cleanly: the folder goes when the move made it, and the journal and file record go. It reports
  a fraction and the file it checks; a cancel between two files returns `interrupted` with the marker and the journal
  kept, and the next call goes on. The result (what stayed, why, sizes, paths) is kept in the journal until
  acknowledged. `acknowledge` is refused (`refused`) while the partial copy of a move that did not commit still carries
  the journal's marker: the next start removes it first.
- **What stays at the default root after a move away**: the pointer (about 150 bytes), the journal and the file record
  until acknowledged, the entries the launcher does not know, and on Windows `state\` (`util::state_dir()`: server console
  captures, the keyring store's lock). The move removes the rest itself before anything else writes there.
- **Whose profile a game runs** (`profile::game_profile_owner`, for every launcher, also one that never moves anything):
  a game's `kiln/profile.json` is this data folder's profile only when its paths name this data folder (Z and native
  mode: `configDir`), or when it is the one this folder last wrote into the game (`installs/<id>.active.json`: the same
  id, generation and fingerprint); in copy mode, without such a record, the id alone decides as before dev.26. Another
  data folder's profile (another launcher, a `KILN_DATA_DIR`, a launcher before dev.26 at the default root after a move)
  is never written again, taken over or copied back into a profile of the same id here; `GameStatus.otherFolderProfile
  {id, root?}` says so and `activeProfile` is then empty; save backups taken then go to `backups/saves/<gameId>/`; an
  activation of this folder's profile of that id in copy mode keeps that game's config copy in
  `kiln/backups/profile-config-<pid>-<ts>/`.
- **A launcher before dev.26** does not read the pointer and takes no data-folder lock. Started after a move it runs at
  the default root as on a first run. A profile it activates in a game is another data folder's profile for the moved
  folder (above). Running during a move, it can still write the old place: what it changes during the copy is copied
  again; what it changes later stays in the old folder (the removal keeps changed files), and the old copy is not
  removed while a game's profile names it.

## 4. Game folder (`<game>/kiln/`)
Today's layout stays (`tools/deploy-server.sh`): `kiln-core.dll`, `kiln.json`, `sigdb/`, `services/`, `plugins/`,
`host/`, `dotnet/`, `mods/`, `config/`, `data/`, `logs/`, `control/`, `crash-reports/`. The launcher adds:

| Path | What |
|---|---|
| `profile.json`, `profile.once.json` | section 5.1 |
| `play-once.ini` | section 5.2 |
| `chain.ini`, `chain/` (displaced proxies, `once.ini`) | section 5.3 |
| `native/<id>/` | native mod drop-in folder (a folder with `native.json`) |
| `profiles/<pid>/<kind>/<id>/`, `profiles/<pid>/config/` | copy mode (section 6) |
| `../mods/<id>/` (ShroudForge's mods folder, `shroudforge::mods_dir`), `../shrouded_engine_tweaks/mods/<id>/` | ShroudForge and SET mods of the active profile, placed by activation with a `.kiln-library.json` marker (`{format, kind, id, version, hash}`; 5.1) plus a trusted placement receipt. A marker alone never authorizes changes; player folders are never touched |
| `../mods/<original folder>/` | the active profile's XHL companions (only XHL-Core loads them) and EML mods a native DLL reads there, placed by activation under the folder name they came with (5.1) |
| `../mods/<id>/`, `../greeneye/<id>/` | the active profile's enabled Flame and Greeneye mods, placed by activation with a marker and a receipt, with or without the engine's own loader: a Flame mod reads its own files (`sigs/`, `symbols/`, settings) from `<game>/mods/<id>/` wherever its DLL is loaded from, a Greeneye mod gets its folder as `greeneye\<id>\` from Greeneye's API. Files the mod wrote there stay across updates and go to `kiln/kept-mod-files/<kind>/<id>/` when the copy goes (5.1) |
| `state/` | status files for the launcher (section 5.9); created by kiln-core at boot (`KILN_PATH_STATE`) |
| `.install.json` | install receipt (section 5.7); a staged Kiln update (0.2.0-dev.30.9) writes a new one as its last file, and writes one for a server pack install that had none |
| `staged/` | (0.2.0-dev.30.9, servers) staged mod and Kiln updates, the kept versions for a rollback, their journals and results ([../staged-updates.md](../staged-updates.md)); never `kiln/data` |
| `backups/install-<yyyymmddThhmmssZ>/` | what the installer replaced, byte-identical |
| `.staging-<random>/` | installer staging; deleted at the end, stale ones deleted by the next install |
| `logs/proxy.log` | the proxy's decisions |
| `vendor/<vendor>/<version>/` | (0.2.0-dev.29) vendor upscaler files the player downloaded in Settings, Graphics (Upscaler files; the Deck's Settings), with their receipt `kiln-vendor.json` (section 5.15); `vendor/.lock` while a download or a removal runs. A client only |

Servers keep their layout: the launcher syncs library mods into `<server>/kiln/mods/<id>/` with
`.kiln-source.json` (`{format, library:{kind,id,version,hash}, synced, by}`) and switches them off with an empty
`.disabled` file; native mods go to `<server>/kiln/native/<id>/`. Other engines' mods go to the folder their plugin
reads on a server (`engine_mods::server_root`: `emf/mods`, `kiln/eml/mods`, ShroudForge's mods folder,
`shrouded_engine_tweaks/mods`, `greeneye`, `mods`) with the same marker; switched off, they move to
`<server>/kiln/disabled/<kind>/<id>/`. Profiles are a client feature.

World data (`kiln/data/worlds`) is never per profile and never moves with `dataDir`.

## 5. Formats

### 5.1 `profile.json`
```json
{
  "format": 1, "id": "ember-run", "name": "Ember run", "generation": 7,
  "written": "2026-11-04T12:00:00Z", "writer": "kiln-launcher 0.2.0-dev", "pathMode": "z",
  "configDir": "Z:\\home\\user\\.local\\share\\kiln\\profiles\\ember-run\\config",
  "dataDir": null, "dropins": false, "fingerprint": "kms1:…", "server": null,
  "mods": {
    "kiln":   [{ "id": "new-items", "version": "1.0.0", "path": "Z:\\…\\library\\kiln\\new-items\\1.0.0+3f2a9c1d", "hash": "kdh1:…", "enabled": true }],
    "native": [{ "id": "creative-mode", "version": "1.3.0", "path": "Z:\\…\\library\\native\\creative-mode\\1.3.0+0badc0de" }],
    "emf":    [{ "id": "beastmaster", "version": "0.3.3", "path": "Z:\\…\\library\\emf\\beastmaster\\0.3.3+c0ffee00" }],
    "plugin": [{ "path": "Z:\\…\\library\\plugin\\my-plugin\\1.0.0+12345678\\my-plugin.dll" }],
    "eml":    [{ "id": "bigger-stacks", "version": "0.1.0", "path": "Z:\\…\\library\\eml\\bigger-stacks\\0.1.0+0a1b2c3d" }],
    "shroudforge": [{ "id": "sfpkg", "version": "1.0.0", "path": "Z:\\…\\Enshrouded\\mods\\sfpkg", "enabled": false }]
  }
}
```
Rules:
- Required: `format`, `id`, `generation`, `mods`. `generation` grows with every write; kiln-core echoes `id` and
  `generation` in `state/boot.json`, so the launcher knows which profile the running game booted.
- `mods.<kind>[]` keep the launcher's order; each loader still sorts with its resolver (managed: Resolver.cs rules;
  native: the same rules over `after`/`before`/`requires`/`incompatible`; EMF: EMF's own order). `enabled: false`
  entries are reported as disabled and skipped.
- `path`: absolute game-side path (Z: or a Windows path), or relative to `<game>/kiln/` (copy mode). Kiln mods, native
  mods and EMF mods name a folder; plugins name the DLL. `mods.xhl` entries, and `mods.eml` entries of EML mods a native
  DLL reads in the game's mods folder, name the copy placed in `<game>/mods/<original folder>`; `mods.greeneye` entries
  of enabled Greeneye mods name Kiln's copy in `<game>/greeneye/<id>`, `mods.flame` entries of enabled Flame mods Kiln's
  copy in `<game>/mods/<id>`. In copy mode those are `../mods/<original folder>`, `../greeneye/<id>` and
  `../mods/<id>`, the only `..` forms the format allows (`../mods/` in `mods.xhl`, `mods.eml` and `mods.flame`,
  `../greeneye/` in `mods.greeneye`; one direct child with a name under the rule of
  `schema/fixtures/profile/game-folder-names.json`), written only for a runtime that declares `profileReader` 2 or more
  (3 or more for `mods.flame`; section 6, copy mode).
- `configDir` (default `<game>/kiln/config`) becomes `KILN_PATH_CONFIG`, `KilnPaths.Config` and
  `kiln.profile.config_dir()`. Mod config files (`<id>.json`), `.schema/`, `native.json` and EMF settings
  (`emf/<id>.ini`) live there.
- `dataDir` (default `<game>/kiln/data`): per-mod data (`<dataDir>/<id>/`); `kiln/data/worlds` stays put.
- `dropins`: `false` (default) loads only the listed mods; `true` also loads every kind's drop-in folder
  (`kiln/mods`, `kiln/native`, `kiln/plugins`, `emf/mods`) after them; a list (`["plugin"]`) does it for those kinds.
  A drop-in mod whose id is also listed in the profile is skipped (the profile wins).
- `server`: the server a modpack profile was made for (`id`, `name`, `address`, `modpack{url,sha256,name,version}`).
- Missing file: the legacy layout (drop-in folders only, `kiln.profile` mode LEGACY). Unreadable or invalid file: no
  mods of any kind, mode INVALID, the error in `kiln.log`, `state/boot.json` `profile.error` and the
  `kiln.profile.json()` text. Never fall back to the drop-ins silently.
- `profile.once.json` has the same format; kiln-core reads it instead when play-once `mode=profile` (section 5.2).
- Safety limits: 16 MiB file, JSON nesting 128, at most 4,096 entries per known kind; paths are at most 32,767
  bytes and contain no control characters. Optional versions are semantic versions of at most 256 bytes; folder
  hashes use kdh1 and plugin hashes SHA-256. Generations are in `1..9223372036854775807`. Limit violations make
  the profile INVALID with a specific error.

**Other engines' mods** (`mods.<kind>`, same entry shape as `emf`). kiln-core validates these lists like its own (a
broken entry makes the profile INVALID), counts them in `kiln.log` and `boot.json` `profile.counts.<kind>` and lists
missing folders in `profile.missing[]`, but `kiln.profile` `count()/get()` cover only the four Kiln kinds (no ABI
change): each engine's plugin reads its own list from `kiln.profile.json()`. Older cores warn
`unknown mod kind "mods.<kind>" ignored` and run on. A list is written only when it has entries, so a Kiln-only
profile is byte-identical to older launchers' files. `dropins` may name engine kinds; `true` is written when the
profile turns on all four Kiln kinds (profiles without `dropinsEngines`, as before) or every drop-in switch (profiles
with it, profiles.md "Drop-in folders"), and the engine plugins then read their own folders too.
Older cores can ignore the new `mods.<kind>` lists, but their `dropins` arrays reject unknown names: engine-specific
drop-in selections require an updated core. `dropins: true` keeps the older all-Kiln-kinds representation.

| Kind | Plugin | Entry `path` | How the plugin takes the list |
|---|---|---|---|
| `emf` | kiln-emf | library folder | `kiln.profile` `get(KILN_PROFILE_KIND_EMF)` |
| `eml` | kiln-eml (GPL) | library folder; for an EML mod a native DLL reads in the game's mods folder, the copy placed in `<game>/mods/<original folder>` | `discover::profile_candidates` over `json()`; `enabled: false` = disabled |
| `xhl` | none: only XHL-Core, installed separately (a Native mod or the author's own install), loads these | the copy placed in `<game>/mods/<original folder>` | no Kiln plugin reads the list and kiln-core ignores it: it is placement metadata. Activation places each enabled companion there, with its `native-plugin.ini`, for XHL-Core; Kiln never loads a companion's DLL. `dropins` never names `xhl` |
| `shroudforge` | kiln-shroudforge | the placed copy in ShroudForge's mods folder | hosts ShroudForge when an entry is enabled; ShroudForge reads its mods folder. Activation places each listed package there and writes only Kiln-owned copies' `extended.mod.json` `enabled` (`engine_mods::place`); original player folders are never switched. Kiln's copies the profile no longer lists are switched off (settings kept); a new version keeps the player's setting values |
| `set` | kiln-set | the placed copy in `shrouded_engine_tweaks/mods/` | kiln-set reads that folder. Activation places the enabled mods and removes Kiln's copies of disabled or unlisted ones (SET has no switch). kiln-set loads nothing under play-once no-mods or an invalid profile |
| `greeneye` | kiln-greeneye | Kiln's placed copy in `<game>/greeneye/<id>/` (an enabled mod), else the library folder (`mod.json` + DLL) | `compat.h` `plan_engine("greeneye")` |
| `flame` | kiln-flame | Kiln's placed copy in `<game>/mods/<id>/` (an enabled mod: the folder a Flame mod reads its own files from), else the library folder (`flame.json`) | `plan_engine("flame")`; `kiln/state/flame.json` says where each mod ran from and whether that is the folder it reads (`docs/kiln/engines/flame.md`) |
| `shroudtopia` | kiln-shroudtopia | library folder (mod DLL) | `plan_engine("shroudtopia")`; settings in `<configDir>/shroudtopia/shroudtopia.json` |
| `enshwick` | kiln-enshwick | library folder (`pack.toml`, or a folder with the mod DLL) | `plan_engine("enshwick")`; the plugin copies each enabled entry into `<game>/mods/<id>/` (Enshwick's loader reads only that folder), with its own receipts, and starts Enshwick's loader |
| `shroudlink` | kiln-shroudlink | library folder (mod DLLs directly in it) | its own `plan_from` (the same rule as `compat.h`); roots are the enabled entries, then `shroudlink_mods/` and its direct subfolders |
| `ember` | kiln-ember | library folder (one library) | `plan_engine("ember")`; finds and reports, starts nothing: Ember has published no mod ABI (the launcher says the mods don't run yet) |
| `dllmod` | kiln-dllmod | Kiln's placed copy in `<game>/Mods/EnshroudedModLoader/<id>/` (an enabled package: the folder Enshrouded ModLoader reads too; Copy mode `../Mods/EnshroudedModLoader/<id>` from `profileReader` 4), else the library folder (DLLs with their `.dll.mod`) | `plan_engine("dllmod")`; the packages start at a local world's start; `dropins` never names `dllmod` (a core before `profileReader` 4 calls such a profile invalid). `docs/kiln/engines/dllmod.md` |

Placement ownership requires `.kiln-library.json` plus a matching launcher receipt in
`kiln/state/library-placements.json` (`{path, marker, files, extension?, game_folder?}` entries, recording relative paths,
file hashes, directory entries, the exact SF extension text last written by Kiln, and `game_folder: true` for a copy in
`<game>/mods/<folder>` that a DLL reads there: an EML mod with a native part, a native mod's settings folder, an XHL
companion; cleanup needs that flag to accept such a copy, a copy without it is cleaned like an own-loader copy).
XHL-Core's copy in `kiln/chain/<library id>/` (XHL-Core under its own name, core.md "Profiles and activation") has an
entry too, without a marker file: `files` lists exactly the files Kiln wrote there, and Kiln replaces or removes one
only while it still holds those bytes; what XHL-Core writes next to it (its log) stays.
**Publication journal (dev.29):** before a copy is replaced, `kiln/state/placement-journal.json` records the
publication durably (`[{path, from: marker | null, to: <the receipt it publishes>, parked: <where the old copy waits> |
null}]`, the last one per path; it stays until that path's receipt moves on). Every placement and cleanup reads it
first, under the placement lock. Malformed or unreadable journals stop placement with an error and are left
unchanged. Every record's destination, receipt path, marker identity, and parked sibling path are validated before
recovery mutates anything. A publication replacing a currently receipted copy gets its new receipt only while the
published marker and Library file hashes match `to`; first publications without an old receipt remain unowned.
A stop after parking puts the old copy back with a directory rename. Parked trees are removed only after receipt
publication and verification that the replacement still exists and matches. Kiln records the old marker in the
staging record before deleting the parked contents, then deletes the marker once nothing else is left, then the empty
folders. A replaced folder that had no marker (only files a mod wrote, adopted with them) has nothing to record: it is
removed whole, and only while the journal names it. If the journal still names the old copy and the replacement is
missing, the old copy is preserved and reported. Completed records for a moved game's old root are retained without
accessing those paths.
A receipt that moved on or a changed published copy is not adopted. A launcher before dev.29 ignores the file.
**Staging record (0.2.0-dev.30.3):** a placement stages its copy in a hidden folder beside it (`.kiln-place-<12 hex>`:
the staged copy in `new`, a parked old copy in `old`). Before that folder is made, `kiln/state/placement-staging.json`
names it durably (`{format: 1, folders: [{folder, path, marker, gameFolder?, parked?}]}`); its entry goes once the folder's
removal is on disk (its parent folder flushed on Unix) at the end of the placement, and the file goes with its last
entry. A record Kiln cannot read, or larger than 1 MiB (never read whole), names no folder. Every placement and
uninstall, after the journal's recovery, removes only a folder the record names: one of that name, directly beside the
copy it stages in that copy's engine mod folder, with no links. The sweep can remove a `new` with the recorded marker
or no marker yet. Once the journal no longer names an `old`, the sweep can remove it if its marker matches `parked`,
or finish removing its empty directory if cleanup already removed the marker. Extra entries in the staging folder
keep it in place. If Windows holds a file open, the record stays for the next attempt, with a note. The journal's
format is unchanged: an older launcher reads it as before. A launcher that reads the staging record without `parked`
(0.2.0-dev.30.3 and 30.4) leaves a folder holding `old` as it is, and drops the field if it rewrites the record; that
folder then stays.
**Durability order:** an imported Library tree is flushed (files, then directories), durably renamed, then its
import record is replaced durably. A staged game copy (including carried settings) is flushed before its intent,
park, publish and receipt; both source and destination directory entries of these renames are flushed on Unix,
and Windows uses the write-through rename. The game's profile.json follows placement/restore, then its advisory
activation record; both use durable exclusive replacement. A rename can have happened even when its flush returns
an error: recovery staging is retained instead of deleted. These are software-order guarantees, not power-cut tests.
Unchanged ownership verification refuses a short/missing code file; it does not silently claim a damaged tree.
For a damaged Library entry, import/hash validation fails and re-import/repair can be needed. For a damaged game
copy, placement may retain/refuse it rather than overwrite player files; retry is automatic only when the journal
can still verify a published replacement or put back its parked predecessor. An interrupted first publication
without a receipt remains unowned. The activation record is advisory: failure leaves the durable game profile as
truth and the next refresh recomputes status. Windows directory creation/flush behavior, per-value process crashes
and concurrent activations have not been hardware/stress validated. Copy-mode fallback/config delivery, chain.ini,
explicit adoption/removal and identity migrations retain their existing protocols and are outside this replacement
transaction: a cut can leave stale metadata/partial delivery; verification can retain/refuse it, so manual repair
may be needed instead of automatic replay.
The marker alone is advisory: marker-only older copies and player-modified folders are left untouched. The
upgrade inventory excludes only ShroudForge's mutable enabled switch and setting values. Deactivate/uninstall cleanup
additionally requires the exact extension text to match; old receipts without it conservatively retain SF copies.
Only Kiln's own switch of an unchanged extension advances that exact receipt, never player settings carried through
an upgrade. ShroudForge's own Modloader adds `targets` with its default (`["client"]`, `["client", "server"]` for
launcher EML) when its in-game menu writes a file without one; that default is what ShroudForge reads from a file
without it, so such a file still matches the receipt (dev.26). Retained folders are returned as `{path, reason, mayRun,
off?}` (`off`, dev.26: the profile has the mod off or no longer lists it): by deactivate and uninstall (`PlacementCleanup`)
and by every activation (`Activation.retained`: a copy the profile switched off, removed or wants in another version,
that activation leaves because its code changed or Kiln cannot inspect or carry what is in it; one such copy never fails
the activation of the others). The launcher displays paths/reasons until dismissed and warns they may still run through
Kiln's engine plugin (such as SET's) or another loader. Copies are staged beside the destination and
the previous folder is restored if replacement fails. Activation refuses while the game runs.
In copy mode, the ShroudForge and SET profile entry paths name the verified source copies under
`kiln/profiles/<profile>/<kind>/<id>`; their plugins still read the separately placed fixed engine folders. Nothing
reads those copies' files: the core only checks that the folder is there (a missing one is "missing; skipped" in
kiln.log and `boot.json`, and the engine's plugin reads only the list), and since 0.2.0-dev.29.18 the in-game Mods page
shows and edits the copy the engine runs (Kiln.Ui `ModCatalog.EngineRunFolder`: Kiln's copy in the engine's folder whose
marker names the entry's kind and id, or the player's own folder of the same package there). Settings changed in that
copy survive every activation and reconcile, as the launcher's editor's do (`engine_settings_set` writes the same
file): placement compares `extended.mod.json` without its switch and settings, and carries the values to a new
version. The profile's switch is the one Kiln sets there at every activation.
Play once cannot select different ShroudForge or SET mods, versions or switches from the active profile: activate
that profile first to change their fixed folders. A refused play-once request leaves those folders unchanged.

`compat.h`'s rule for Greeneye, Flame, Shroudtopia, Enshwick, shroudlink and Ember: with a `mods.<kind>` list the profile's entries load and the
engine's own folder is read only when `dropins` is `true`; without the list the folder is read (mods the player put
there by hand keep working).

### 5.2 `play-once.ini`
```ini
; Kiln play-once flag. The proxy reads it, deletes it at once, and applies it to this one start only.
mode = no-mods
created = 1762257600
nonce = 4f1c0e3a9b2d4e6f
```
INI dialect (shared with chain.ini): UTF-8 with or without BOM, UTF-16LE with BOM, or ANSI (the system code page);
LF or CRLF; lines are trimmed (spaces, tabs); blank lines and lines starting with `;`, `#` or `//` are skipped;
`[section]` headers (case-insensitive). Reference parser: `schema/fixtures/ini/kini.py`; fixtures:
`schema/fixtures/ini/play-once/*.ini` with the expected decision in `<case>.json` (`python3 kini.py check`).

Grammar of play-once.ini:
- `key = value` lines; sections are ignored and so are lines without `=`. Keys are case-insensitive; a repeated key:
  the last one counts. Unknown keys are ignored.
- `mode` (required): `vanilla` | `no-mods` | `profile`.
- `created` (required): unix seconds (`1762257600`) or RFC 3339 with a zone (`2026-11-04T12:00:00Z`,
  `…+01:00`).
- `nonce` (required, non-empty): echoed in `state/boot.json` `playOnce.nonce`. The launcher writes 32 hex chars.
- `profile` (optional, mode profile only): the profile file, absolute or relative to `<game>/kiln/`; default
  `profile.once.json`.

What the proxy does, in this order:
1. `KILN_DISABLE=1`: vanilla, no chain, no gate; play-once.ini is left untouched.
2. Read the file, then delete it, before validating. Delete failed: `IGNORED_UNDELETABLE` (a flag that cannot be
   removed must not stick).
3. Dedicated server exe: `IGNORED_SERVER` (the file is still deleted).
4. Invalid (bad mode, missing created/nonce, bad created): `IGNORED_INVALID`.
5. `now - created > 600 s` or `created - now > 60 s`: `IGNORED_EXPIRED`.
6. Apply: `vanilla` loads nothing (no chain, no core); `no-mods` loads no chain entry and boots the core with
   `KILN_PLAY_ONCE_NO_MODS` (Kiln and its own services run; no managed, native, EMF, plugin or chained mod);
   `profile` boots with `KILN_PLAY_ONCE_PROFILE` and `play_once_profile` = the absolute profile path, and reads
   `kiln/chain/once.ini` instead of chain.ini `[profile]` (and, when `once.ini` has them, instead of `[chain]` and
   `[late]`, see 5.3).
An ignored flag boots as `NONE` and is reported (`state/boot.json` `playOnce.mode` = `ignored-…`, proxy.log).

### 5.3 `chain.ini`, `chain/once.ini`, `dinput8-chain.ini`
```ini
[chain]
; proxies the installer moved aside; a leading ; switches one off
chain\emf-dinput8.dll
[profile]
; native mods with phase "dllmain" of the active profile
Z:\home\user\.local\share\kiln\library\native\fps-unlock\2.0.0+00000000\fpsunlock.dll
```
Grammar (same dialect as 5.2):
- Sections `[chain]`, `[profile]` and `[late]`, case-insensitive; lines before any section count as `[chain]` (in
  `once.ini`: as `[profile]`). Other sections are ignored (they may belong to other tools); an older proxy ignores
  `[late]` the same way and loads the entries early.
- `once.ini` (play-once `mode=profile`) is read instead of chain.ini `[profile]`. Since dev.21 it may also have a
  `[chain]` and a `[late]`: what chain.ini would hold if the play-once profile were active, with that profile's compat
  rules applied (a displaced proxy its rules switch off is left out, one the *active* profile's rules switched off is
  back, its `late` rules and late natives are in `[late]`, the active profile's are not). A section the file has, even an
  empty one, **replaces** chain.ini's same section for that one start; a section it lacks leaves chain.ini's in effect
  (a `once.ini` from before dev.21 has neither). The launcher always writes all three. chain.ini itself is not touched.
  An older proxy reads only `[profile]` and uses chain.ini's `[chain]` and `[late]`. A normal start never reads `once.ini`.
  proxy.log says when `once.ini` supplied a section.
- `[late]` (dev.21) does not add DLLs. It names DLLs that the other lists already hold: an entry of `[chain]`,
  `<game>/dinput8-chain.ini` or `[profile]` (also `once.ini`'s) whose DLL is named in `[late]` is **deferred**: the
  proxy runs every check on it but does not load it, and kiln-core loads it after Kiln's services, plugins, native
  boot-phase mods and the managed host started (also in safe idle, and also in play-once `mode=profile`). Paths are
  read like `[chain]` paths (relative to `<game>/kiln/`; a relative line whose file is not there is tried relative
  to `<game>/`, for `dinput8-chain.ini` entries); entries and `[late]` lines are matched by canonical path, so
  spelling, case and `..` do not matter, and a missing file is matched by its resolved path. Deferred entries load
  in the order of the lists they came from (not in `[late]` order). A `[late]` line that names no entry loads
  nothing (proxy.log says so). Refused, missing and duplicate entries keep that status. Play-once `no-mods` defers
  nothing (all entries are `skipped`). EMF's own loader (a `[chain]` entry with its exports or marker text) is never
  deferred, whatever `[late]` says: it starts EMF itself, and kiln-emf decides when the plugins start whether the real
  EMF is in the process; proxy.log says so. A deferred DLL may patch the game's entry point: the gate puts the entry's
  bytes back before the core starts, so the patch saves the right bytes and runs when the gate continues (proxy.log
  and kiln.log note it). The core does not load a late DLL of a native mod that
  is disabled, on the other side or refused by `native.policy`, and it says `already-loaded` for a DLL that was in the
  process before the late phase (EMF's loader starts the DLLs of `dinput8-chain.ini` itself). The launcher keeps an entry in its own section and adds the same path to
  `[late]`; a `;` line in front (`; kiln rule <id>`) is a comment like any other. A compat rule that keeps an entry
  early (dev.29 `before`: it must load before another DLL, whose entry the rule puts in `[late]`) writes the player's own
  `[late]` line for it as `; kiln rule <id> off: <line>`, and a profile's late switch for such a `[profile]` entry is not
  written; the next activation without the rule puts both back. An older proxy reads both as it reads every comment.
- One DLL per line. A `key = value` line counts by its value. Only values ending in `.dll` (case-insensitive) count;
  other lines are ignored.
- Paths: absolute (`Z:\…` under Proton, real paths on Windows, UNC) or relative to `<game>/kiln/`; `/` is read as `\`.
- At most 256 entries per section: the first entry past the limit is reported as REFUSED with detail `too-many`, and
  any further entries are discarded (not reported).
- Writers: the installer owns `[chain]`, profile activation owns `[profile]`; each rewrites the whole file atomically
  and keeps the other section's lines byte-identical. The launcher writes `once.ini` (its `[profile]`, `[chain]` and
  `[late]`) together with play-once.ini for launch mode once and deletes it at the next activation.

Load order, all in the proxy's `DllMain` before it installs the gate (so entry patches unwind last-in, first-out):
1. `kiln/chain.ini [chain]`;
2. `<game>/dinput8-chain.ini` with EMF's rules (`[Chain]`, lines before any section, and `[EMF]` (the DLLs EMF 0.5's
   installer renamed), in file order; `;`/`#`/`//` comments, a `key=value` counts by its value, only DLLs inside the
   game folder: an entry outside is REFUSED `outside`; at most 256 entries across both sections, with the same
   overflow rule as above);
3. `kiln/chain.ini [profile]`, or `kiln/chain/once.ini` for play-once mode=profile (whose `[chain]` and `[late]`, when it
   has them, were used for steps 1 and the deferral instead of chain.ini's).

Decisions per entry (`KilnChainEntry`, `state/boot.json` `chain[]`): dedupe by canonical path (`duplicate`, loaded
once); refuse (`refused` + detail): `self` (the proxy itself), `system` (the system dinput8 or Wine's builtin), `kiln`
(a Kiln proxy or kiln-core.dll), `shroudforge`, `not-dll` (not a 64-bit PE DLL), `outside`, `too-many`; `missing`;
`failed` (LoadLibrary error, `error` = Win32 code); `skipped` (play-once no-mods); `late` (dev.21, in `KilnChainEntry`
only: deferred to the core, see `[late]`); else `loaded`. The proxy never calls FreeLibrary on a chained DLL. One line per decision goes to `kiln/logs/proxy.log` (rewritten at each start).

`KilnChainEntry` (kiln_api.h) = `{KilnStr path; u32 status; i32 error; void* module; u32 source; u32 detail}`
(`detail` = `KILN_CHAIN_REFUSED_SELF=1, _SYSTEM=2, _KILN=3, _SHROUDFORGE=4, _NOT_DLL=5, _OUTSIDE=6, _TOO_MANY=7`;
the native loader renames an older `reserved` field, layout unchanged). `source`: `chain=0, profile=1, emf=2`.
`status` adds `KILN_CHAIN_LATE = 6` (appended; an older core reads an unknown status as `failed`).

Proxy exports: `DirectInput8Create` (forwarded), `GetdfDIJoystick` (forwarded to the system DLL if it exports it,
else the proxy's own `c_dfDIJoystick`), and `const KilnProxyInfoData* __cdecl KilnProxyInfo(void)` returning
`{u32 size; u32 abi /* 2 */; KilnStr version; KilnStr marker; u32 boot_info_size; u32 reserved}`. Older proxies
have no export and count as abi 1.

Proxy classification (`kiln_manage::pe::classify_proxy` -> `ProxyKind`, the installer's table, and the proxy's
refusals use the same markers). Every DLL under a proxy name next to the game (`dinput8`, `dbghelp`, `dbgcore`,
`umpdc`, `cfgmgr32`, `winmm`, `powrprof`, `iphlpapi`, `version`, `xinput1_4`, `xinput9_1_0`, `dxgi`, `steam_api64`;
`engines::scan_proxies`) gets one kind; checks in this order, the first match wins; texts match as ASCII or UTF-16LE,
case ignored. Table and markers: `engines::ENGINES` ([engines.md](../engines.md)).

| Kind | Markers | Installer (`dinput8.dll`) | Proxy |
|---|---|---|---|
| `kiln` | export `KilnProxyInfo`, ASCII `KilnProxy/<abi>`, UTF-16LE `\kiln\kiln-core.dll` | replace | refuse (`kiln`) |
| `emf` | exports `emf_loader_identity` / `emf_loader_play_once`, text `EMF-LOADER-7d3c9f` (checked before ShroudForge: EMF 0.4+ also exports `ShroudforgeSetDinput8Provider`) | chain | load |
| `shroudforge` | `shroudforge/shroudforge-runtime.dll`, `shroudforge/config/modloader-config.json`, export `ShroudforgeSetDinput8Provider`, text `shroudforge` | refuse unless "switch from ShroudForge" (then backed up, never chained; `dbghelp.dll`, `winmm.dll`, `shroudforge/` stay) | refuse (`shroudforge`) |
| `eml` | `EML_LOG_FILE_PATH` | chain | load |
| `set` | `shrouded_engine_tweaks` | chain | load |
| `enshwick` | `enshwick` | chain | load |
| `greeneye` | `greeneye` | chain | load |
| `shroudtopia` | `shroudtopia` (Shroudweave too) | chain | load |
| `flame` | `FlameStartRuntime`, `flame.json` | chain | load |
| `shroudlink` | `shroudlink` | chain | load |
| `ember` | `ember-loader`, `ember_loader` | chain | load |
| `modloaderproxy` | `modloader-proxy.dll`, `chainloader.ini` | chain | load |
| `ultimateasiloader` | export or text `IsUltimateASILoader` | chained as `..\xinput9_1_0.dll` next to the game (refused when that name is taken) | load |
| `aldrich`, `livemap`, `sharedxp`, `reshade` | `aldrich` / `livemap.ini` / `xpshare.ini` / `reshade` | chain | load |
| `optiscaler` | `optiscaler.ini`, `optiscaler.log` (checked before ReShade: OptiScaler can load ReShade); its other names `d3d12.dll`, `wininet.dll`, `winhttp.dll`, `OptiScaler.asi` are found by `engines::optiscaler_in`, never by `scan_proxies` (dev.29.18) | chain | load |
| `chainloader` | `dinput8-chain.ini` | chain | load |
| `nativefolders` | `native-loader.log` (CharosDX's loader of `mods\<mod>\native\*.dll`) | chain | load |
| `unknown` | any other 64-bit PE DLL | chain | load |
| `notdll` / `none` | not a 64-bit PE DLL / no file | refuse / nothing to do | refuse (`not-dll`) |

Kiln owns only `dinput8.dll`; DLLs under the other proxy names are never moved (EML's `dbghelp.dll`, Shroudtopia's
`winmm.dll`, SET's `powrprof.dll` keep running next to Kiln). "chain" = moved to `kiln/chain/<word>-dinput8.dll` +
`[chain]` (an occupied name gets a numbered suffix).

**Library detection** (`library::detect` -> `Detected {kind, path, id, version, name, reason, items, engine}`;
`engine` = the `Engine` it was made for, kebab-case like `ProxyKind`). `DetectKind` besides Kiln's own (`kiln`,
`native`, `plugin`, `emf`, `modpack`, `runtime`, `bare-dll`, `unknown`): `eml`, `shroudforge`, `set`, `enshwick`,
`greeneye`, `shroudtopia`, `flame`, `shroudlink`, `ember` (mods for those engines), `proxy` (another engine's loader or
a tool: never imported), `data`, `save`, `reshade`, `cheat-table`, `asi`. EML vs ShroudForge: both use `mod.json` +
Lua and the same `capabilities`; a package is ShroudForge's only with SF-specific `extended.mod.json` metadata or an
SF schema (`shroudforge::is_sf_own_mod`); `launcher: "EML"` stays EML. A kind is imported only through its registered
loader (`engines::builtin_loaders`: `engine_mods::import_engine_mod` for `eml`, `shroudforge`, `set`, `greeneye`,
`flame`, `shroudtopia`, `enshwick`, `shroudlink`, `ember`), into `library/<kind>/`; the others are skipped with their
`reason`. Enshwick packs take `id`/`version`/`name` from `pack.toml`; an Ember mod is a folder in `ember/mods/` (no
manifest or known export exists yet) and its reason says it doesn't run yet. The launcher (MIT) only
copies files: it never links or runs an engine's code.

### 5.4 `native.json` and `<configDir>/native.json`
```jsonc
{
  "format": 1, "id": "creative-mode", "name": "Creative Mode", "version": "1.3.0",
  "side": "client", "dlls": ["creative.dll", "lib/creative-ui.dll"], "phase": "early",
  "init": "kiln_native_init", "after": ["fps-unlock"], "before": [], "requires": [],
  "incompatible": ["creative-legacy"], "enabled": true, "kiln": ">=0.2.0-dev", "builds": []
}
```
- Required: `id`, `version`, `dlls`. Defaults: `side: "client"`, `phase: "early"`, `init: null`, `enabled: true`,
  `clients` from side (server -> none, else mod).
- `dlls`: relative paths in the folder (subfolders allowed, no `..`), loaded in order with LoadLibrary (each under an
  SEH guard). `init`: an export of `dlls[0]`, called once right after it loads as
  `int __cdecl init(const KilnNativeContext*)` (`native/core/abi/kiln_native.h`); `null` = a plain DLL mod, only
  loaded. Return 0 ok, 1 failed, 2 unsupported. `init` must be null for phase `dllmain`.
- Phases: `dllmain` (proxy, chain.ini `[profile]`, no init), `early` (start of kiln_boot, also in safe idle), `boot`
  (after Kiln's services and plugins), `world` (first world step, needs hooks; skipped in safe idle).
- Ordering: the managed resolver's rules: `requires` = required dependency (any version), `after` = loadAfter,
  `before` = loadBefore, `incompatible`; ties by id; cycles fail their members.
- `kiln` (range): checked like a `kiln` dependency. `builds` (exe sha256s): when given and the exe is not listed, the
  mod still loads with the note "untested build".
- Policy `kiln.json` `"native": {"policy": "always" | "verified-only" | "never"}` (default `always`):
  `verified-only` loads native mods only on a verified game build, `never` loads none (state `policy`).
- Enabled precedence: a profile entry with `enabled: false` wins, then `<configDir>/native.json`
  `mods.<id>.enabled`, then native.json `enabled`.
- `<configDir>/native.json` = `{"format": 1, "mods": {"<id>": {"enabled": false}}}`, written by the launcher.
- Results: `state/native.json` (5.9).

### 5.5 Config schema snapshots `<configDir>/.schema/<file>.json`
Written whenever a mod registers a config (Kiln.Api `Config.Register`; kiln-emf for EMF settings): the config's
schema without values. Path: `.schema/` + the values file's path relative to configDir with its extension replaced by
`.json` (`my-mod.json` -> `.schema/my-mod.json`, `emf/beastmaster.ini` -> `.schema/emf/beastmaster.json`;
`kiln_manage::config::snapshot_path`). Shape = the admin op `config_get` result minus `value`/`fileValue`/`fromServer`:
```json
{ "format": 1, "source": "kiln", "file": "big-stacks.json", "valueFormat": "jsonc", "id": "big-stacks",
  "mod": "big-stacks", "modVersion": "0.3.1", "kind": "server", "written": "…", "kiln": "0.2.0-dev",
  "sections": [{ "path": "limits", "comment": "Stack limits", "synced": true }],
  "entries": [{ "path": "limits.multiplier", "type": "float", "comment": "…", "default": 2.0,
                "requiresRestart": false, "synced": true, "min": 1, "max": 100 }] }
```
`type`: `bool | int | float | string | enum | duration | list<…>` (ConfigValueKind); `options` for enums; durations
are strings (`"90s"`, `"5m"`). The launcher copies snapshots into `schema-cache/` per mod version, edits values
through a JSONC CST (comments kept) and validates like Kiln.Api. Running servers use the admin ops
`config_get`/`config_set` instead.

### 5.6 `kiln-runtime.json` (runtime bundle)
Bundle file: `kiln-runtime-<VERSION>+<yyyymmdd>.<gitsha>.zip`, deterministic (entries sorted bytewise, times = the
commit time, no extra attributes), its files laid out relative to the game folder. `kiln-runtime.json` at the root:
`format`, `version` (= the zip's), `kiln` (VERSION), `git`, `created` (commit time), `abi`, `api`, `profileReader`
(the profile.json forms its kiln-core reads: `KILN_PROFILE_READER` in `native/core/src/profile.h`; absent = 1), `dotnet`,
`components` (`core`, `admin`, `emf`, `samples`: `{sides, default, requires, description}`), `files[]`
(`{path, size, sha256, component, config}`, sorted by path), `builds.{server,client}[]` (`{sha256, steamBuild, svn,
verified}`). `config: true` files (`kiln/kiln.json`) are installed only when missing and never replaced or deleted.
A parity test checks the bundle's tree against `tools/deploy-server.sh`'s output.

### 5.7 `.install.json` (receipt) and the install rules
Fields: `format`, `installId` (uuid, stable across upgrades), `installed`, `updated`, `installer`,
`runtime{version, sha256, components, deselected}` (`sha256`: of the bundle zip, absent for an install from the bundle
unpacked into a folder, which has no zip; `deselected`: the defaults the player turned off; updates keep
them off unless required and add every other default for the side; automatic requirements preserve
opt-outs, and explicit choices preserve unselected earlier opt-outs even if now optional or absent;
absent in older receipts = nothing turned off), `side`, `platform` (`proton|wine|windows`), `game{exe, sha256, steamBuild}`,
`files[]` (`{path, sha256, size, component, config}`), `replaced[]` (`{path, sha256, size, class, action, chainPath,
backup, engine}`; `class`: `kiln|shroudforge|emf|unknown|file`; `engine`: the 5.3 `ProxyKind` when Kiln knows the
file's engine (`eml`, `set`, `flame` ...), absent otherwise, so older readers keep their five classes; `action`:
`chained|backed-up|replaced|adopted`; every path that existed before the install is recorded: `adopted` = the
bundle's exact bytes (or a config file), kept by uninstall).
Install:
1. Refuse while the game runs (`game_running`) or Steam updates it (`steam_updating`).
2. Stage into `kiln/.staging-<random>/` and verify every sha256.
3. Classify `dinput8.dll` (5.3 table).
4. Back up everything it replaces to `kiln/backups/install-<ts>/` (mirrored to `<data>/backups/install/<installId>/`),
   move displaced proxies to `kiln/chain/<kind>-dinput8.dll` + `[chain]`, put the files in place, `dinput8.dll` last.
5. Write the receipt last.
Upgrade: delete files the new bundle no longer has only when their sha256 still equals the receipt's. Uninstall:
restore every `replaced` file byte-identically (a displaced proxy moves back to `dinput8.dll`), remove the receipt's
unchanged non-config files and `chain.ini [chain]` lines it added, keep `kiln/`. Rollback: install an older bundle
from `<data>/runtime/`.

### 5.8 Modpacks: `kiln-modpack.json`, network.json, handshake properties
Zip layout: `kiln-modpack.json` + `kiln/<id>/…`, `native/<id>/…`, `emf/<id>/…`, `plugin/<file>.dll`; deterministic.
`kiln-modpack.json`: `format`, `name`, `version` (free text), `created`, `server{id,name}`, `kiln` (range players
need), `mods[]` (`{kind, id, version, name, path, hash, clients, side}`; `hash` = the folder's kdh1, a Kiln mod's
identity (5.11), a plugin's sha256).
Contents: the server's mods with `clients: "mod"`. A Kiln mod's cook outputs (`assets/cooked/`) are left out (dev.27): they
are made from the server's game files, differ on every player's machine, and each player's own cook makes them; the
mod's hash does not count them, so a pack built from a cooked server folder names the hash the server advertises and a
player's cooked folder has. `verify` refuses a pack whose Kiln mod carries files under `assets/cooked/` (no hash covers
them; a pack built by 0.2.0-dev.26 or older from a cooked folder is one: build it again). Other engines' mods ride along as `<kind>/<id>/…` (`eml/`,
`shroudforge/`, `flame/` …) with their kdh1, taken from each engine's server folder (`engine_mods::server_root`) when
their `clients` is `mod` (stated in their `mod.json`, else the kind's default: `eml`, `shroudforge`, `flame`). A player's
install imports them into the library under their kind and lists them in the profile "Server: <name>".
Install revalidates the current archive against its manifest, validates paths for Windows and verifies every listed
payload hash before importing. Server engine copies require both `.kiln-source.json` and a matching receipt and
payload inventory in `kiln/state/server-library.json` before sync update or removal. Marker-only older copies and
folders with player additions are not auto-adopted. An explicit enable/disable action can move a hand-installed
engine mod between its own folder and `kiln/disabled/<kind>/`; ambiguous folder names across kinds are refused.
These operations refuse while the server runs.

Server (`kiln/config/network.json`, Kiln.Network) adds:
`serverId` (default: generated once into `kiln/data/kiln-network/server-id`), `serverName` (default: the
enshrouded_server.json name), `modpack{url, sha256, name, version}`, `advertise` (default true). REJECT and WELCOME
gain trailing properties: server id/name, Kiln version, modpack, `required[]` (`{id, version, hash, kind}` of the
`clients: "mod"` mods; `hash` is the folder's kdh1, for a Kiln mod its identity (5.11), so a mod cooked on the server and
in a player's game is one mod; `kind` is absent for `kiln`, else `native` or an engine kind such as `eml` or `flame`: other
engines' mods are advertised so the launcher can tell the player, while the HELLO check covers Kiln mods only), truncated so the frame stays <= 7 KB (`kiln.required.more` = how many were left out); old
clients still decode them. Property keys (`kiln.server.*`, `kiln.modpack.*`, `kiln.required[.n]`, `.count`, `.more`,
`.pending`): docs/kiln/network.md "Server modpacks".

Client: Kiln.Network writes `state/last-server.json` (5.9) on every REJECT, on the first WELCOME of a connection and
when the server never answers (outcome `vanilla`).

### 5.9 State files (`<game>/kiln/state/`)
All written atomically; a file whose `pid` is not a live process is stale.
- `cooked.json` (dev.27, the launcher: `kiln_manage::cook`): `{format: 1, mods: {<mod id>: {identity, gameBuild, at, ok, files, error?}}}`,
  the last cook of each mod for this game: the mod's identity and the game's build then (`steam:<build id>`, else
  `file:<data file>:<size>:<mtime>`), whether it ended well, whether it made files (its receipts list some), and a
  failed cook's last lines. `cook::needs_cook` reads it; a file that cannot be read means everything is cooked again.
- `cook-receipts/<key>.json` (dev.27, written by the cook tools when Kiln runs them: `KILN_COOK_RECEIPT=1`,
  tools/modelcook/cook/cookpaths.py `receipt`): `{format: 1, tool, mod: <mod folder as given>, recipe: <relative to it>,
  at, files: {"cooked/<file>": <sha256 hex>}}`, one per mod folder, recipe and tool (`key`: 24 hex of the SHA-256 of
  folder, recipe and tool), replaced by that recipe's next cook. kiln.assets compares each file of a mod's
  `assets/cooked` with the receipts of this game (by SHA-256, wherever the file is); one no receipt lists is loaded
  with a warning (`kiln.json` `"cooked": "warn"`, the default), refused (`"local"`) or loaded (`"any"`). A store that is a
  link, or lies under one, is not read (kiln.assets, `cook::receipts_of`). The launcher checks the folders it cooked
  against their receipts (`cook::check_receipts`: a listed file gone or changed, or a cooked file no receipt lists,
  makes the mod due, `filesGone`); `kiln cook` cooks a recipe no receipt of the target game lists.
- `cook.lock` (dev.27, `state::lock_game`): an empty file a cook of the game holds an exclusive lock on for its whole run,
  and a start with mods (`launch::play_with`, modes `profile` and `once`) until Steam was asked. A start that finds it
  held waits 5 seconds, then fails with `busy`; a cook waits 10 seconds. The lock goes with the process. A start in mode
  `profile` also holds the lock of each Kiln mod folder it loads (`state::lock_cook_folder`, a file per real folder path
  in the system's temp folder, the one a cook of that folder holds) until Steam was asked.
- `boot.json` (kiln-core, end of `kiln_boot`, before the game's entry point): `kiln`, `abi`, `pid`, `written`,
  `bootMs`, `side`, `exe{name,sha256,verified}`, `policy`, `hooks`, `safeIdle`, `playOnce{mode, nonce}` (mode `none`,
  `no-mods`, `profile`, `ignored-expired`, `ignored-undeletable`, `ignored-server`, `ignored-invalid`),
  `profile{mode, id, name, generation, file, error, configDir, dataDir, counts}` (mode `legacy|active|once|no-mods|
  invalid`), `paths` (every KILN_PATH_*), `chain[]` (`{path, source: chain|emf|profile, status, detail, error}`; an entry deferred by
  `[late]` keeps its place, carries `late: true` and the status of the core's load; `lateIgnored: true` with status `loaded`
  when another loader had started the DLL before the late phase, so `[late]` moved nothing),
  `services[]` (`name@version`), `plugins[]` (`{file, id, version, status, error}`), `native{loaded, failed, pending}`,
  `clr{started, error}`, `errors[]`.
- `boot.json` `boot.stop` (0.2.0-dev.29, kiln.boot `stop_start`): `{code, reason, by, mods[]}` when a service ended the
  start on purpose (with or without a boot generation), else null; `boot.exitCode` is then that code. kiln.knowledge's
  roster uses code 19202 (0x4B02), its whole build of the mods' native quests and NPCs that failed 19203 (0x4B03): show
  `reason` and do not restart (below). progress.json says `state` `failed` with the same reason.
- `roster-refused.json` (kiln.knowledge, 0.2.0-dev.29; `state-roster-refused.schema.json`): the mods' NPC roster cannot
  be held on this side (more than 128 entries, or more than 64 where the second unlock page cannot be installed), so the
  start stopped before any world loaded and the process ended with 19202 (0x4B02; a Linux parent under Wine sees 2).
  Written first, before kiln.log's line, before kiln.boot writes boot.json and progress.json and before the game
  client's message box, so it is there even when the player ends the game while the message is open. Fields:
  `format` 1, `refused` true, `why` (the build's refusal, word for word: show it), `mods[]` (the mods that add roster
  NPCs: the ones to remove or update), `side` (`client`|`server`), `exitCode` 19202, `unixExitStatus` 2, `restart`
  false (a restart stops the same way: never restart on it), `advice` (what to do, as in kiln.log), `bootStop` (true:
  kiln.boot ended the start, so boot.json `boot.stop` and progress.json say the same; false: an older core, only this
  file and kiln.log), `pid`, `utc`. The next start moves it to `roster-refused.last.json`: a launcher that shows why the
  last start ended reads that one after a new start, and `roster-refused.json` right after the exit. A launcher shows
  `why` and the `mods` after the game exits with 19202, and offers to disable those mods.
- `content-refused.json` (kiln.knowledge, 0.2.0-dev.29; `state-content-refused.schema.json`): the whole build of the
  mods' native quests, knowledge and NPCs failed, so nothing of it is served (reading the mods' files failed in a way
  that is no one mod's, the build threw, the shared resources did not build, kiln.assets refused the built pack or is not there, the hook on the game's
  resource start is not there), while at least one enabled mod ships such content; the start stopped before any world
  loaded and the process ended with 19203 (0x4B03; a Linux parent under Wine sees 3). One mod's own errors never write
  it. The same fields, order of writing and `.last.json` rule as `roster-refused.json`, with `exitCode` 19203,
  `unixExitStatus` 3, `why` (what failed, word for word: show it), `mods[]` (the mods whose content was not served: the
  ones to remove or update) and `advice` (remove or update them; send kiln.log if it still stops). A launcher shows
  `why` and the `mods` after the game exits with 19203, and offers to disable those mods.
- `registry.json` (kiln.content's registry stage, 0.2.0-dev.31; `state-registry.schema.json`): what this start's registry
  stage did. Per touched field (`fields[]`): its Kiln id and field (or, `raw: true`, the Advanced tier's game member path),
  `applies` (`now`, `worldLoad`, `restart`), the base value and its origin, every writer in the order they apply (Kiln's
  older paths and the archive writer's packages below, then the registry's edits by stage and load order; each with its
  certainty: `certain` for Kiln's own writers, `named` for a ShroudForge/EML package kiln-eml's change journal names, with
  `named` `entry` or `field` (0.2.0-dev.31.18) saying whether the journal names it on the entry or on this field,
  `bestEffort` for a `drift` row of the drift probe), the result and the winner; `base.journal` (since 0.2.0-dev.31.17)
  says whether the journal named the archive writer's packages (`read`, `none`, `stale`, `unreadable`). Every
  conflict line (`conflicts[]`: `overridden`, `composed`, `composedOn`, `exclusive`, `dropped`, `yielded`, with winner,
  loser and one English sentence) and every refused edit (`refused[]`: mod, source, stage, ordinal, reason). `phase`
  `start` (restart fields final) or `worldLoad` (every field applied), `generation`, the regmap record of this build, the
  base's origin and archive writer, the order the mods ran in (`orderSource` `kiln.host` or `folders`). Caps: 20,000
  field rows, 2,000 conflicts, 2,000 refused edits (`truncated` counts what was left out; `counts` has the totals); kiln.log
  carries at most 50 of the lines per start and a total. A reader ignores members it does not know (format 1 only grows).
- Launcher stopped-start notice (dev.29): `kiln_manage::stopped_start::read` reads `roster-refused.json` first,
  then `content-refused.json`, then `boot.json` (`boot.stop`, or the older required-boot-mod exit 19201). The kind is
  `roster` (19202), `content` (19203) or `boot-mod` (anything else); the notice shows a sentence of its own for
  `roster` and `content` with the reason under it, and the reason with the names for `boot-mod`. Each read is capped at 256 KiB;
  missing, invalid and unknown-format evidence produces no notice (invalid files are logged). It never changes
  Kiln's files and does not read `.last.json` as a new stop. The command `launch_state.stoppedStart` returns the
  kind, reason, mod ids and available Library display names, advice, UTC, exit code and runtime pid.
  The watcher and command retain the observed process start, including external Steam starts, and inspect the
  evidence while it runs (including progress `failed`) and after exit. The embedded time must lie between that
  process start (rounded down to its timestamp's whole second) and the observation of exit; the file mtime must
  also be at least the process start. Windows requires the process pid; Proton uses the process time because
  its host pid differs from the Windows pid, then pins the runtime pid when a stop is accepted. No observed
  process identity means no notice: an old stop is never inferred from exit code alone.
  For a process that stops between polls, format-1 failed `progress.json` may supply the runtime pid and start
  time instead, but only if both its embedded start time and mtime are after this launcher's request. The stop
  must then match that runtime pid and time window.
  Desktop Play and Deck Home keep the notice until Dismiss, launcher Play or a running process whose timestamp proves it started after the refusal. Missing, invalid or indistinguishable timestamps keep the notice. Host and runtime pids are not compared to retire it under Proton. The launcher stores the notice
  and dismissed `(game, pid, UTC, exitCode)` in its own browser storage, so reload/restart cannot resurrect a
  dismissed stop. Review these mods opens the existing mod list's exact-id filter. Ordinary quits produce none.
  `kilnctl run` uses the same bounded reader within its foreground-run time interval and prints the reason and
  advice before returning the original exit status. Detached `start` includes those lines in its existing failure
  log tail. `status` labels the last stopped start from kilnctl's own `<run log stem>.stopped-start.json` receipt;
  `run` clears this receipt before another start. This receipt is outside Kiln's state files.
- `mods.json` (Kiln.Host, after every resolve/load/unload/reload; `generation` grows): `pid`, `written`, `generation`,
  `side`, `api`, `profile{id, generation, mode}`, `mods[]` (`{id, version, name, folder, source: profile|dropin, state:
  pending|loaded|disabled|failed|skipped|unloaded, reason, loadIndex, side, clients, faults}`).
- `native.json` (native loader, after each phase): `pid`, `written`, `policy`, `phase`, `mods[]` (`{id, version,
  folder, source, phase, state: pending|loaded|failed|disabled|skipped|policy|unsupported, reason, notes[], loadIndex,
  dlls[]{file, status, error, ms}, init{export, result, fault, ms}}`), and `chain.late[]` (`{path, source, status:
  pending|loaded|failed|missing|duplicate|skipped|already-loaded, note, error, ms, order}`) when chain DLLs were left to the
  core ([late]); `skipped`: its native mod is disabled, on the other side or refused, so the DLL was not loaded;
  `already-loaded`: the DLL was in the process before the late phase; `note` says why for those two.
- `hooks.json` (kiln-core, boot end, first world, link changes; `hooks-state.schema.json`, `kiln/services/hooks.md`):
  the hook registry's map, plus the closed-DLL report: `overlaps[]` (`{symbol, rva, kind: foreign-before|foreign-over|
  refused, foreign{module, offset}, owners[], fix: late|rule|none, chainEntry{path, source, status, late, lateIgnored?},
  message}`; a `refused` row has `null` for symbol, rva and often module and offset), `rules[]` (`{id, action, applied, message,
  subject, source, note}`) and `ruleFiles[]`. No `pid` before dev.21 (`pid` names the writing process).
- `set.json` (kiln-set, at start and after every mod load or `reload()`; dev.22): `format` 1, `plugin` `kiln-set`, `version`,
  `pid`, `side`, `status` (`waiting`: mods load at the main menu / first tick; `running`: loaded, see `mods`; `idle`: no
  `shrouded_engine_tweaks/mods` folder; `off`: play-once no-mods or an invalid profile; `stepped-aside`: SET's own
  powrprof.dll runs; `failed`: kiln-set could not start), `reason`, `steppedAside`, `steppedAsideReason`, `modsDir`,
  `updatedUnixMs`, `mods[]` (`{id: the folder name, name: mod.json "name" or the id, version: mod.json "version" or "",
  status: pending|loaded|failed|skipped, reason: the Lua error's first line, the SET version it needs, a game-build note
  or "", folder}`). Example: `{"format":1,"plugin":"kiln-set","pid":388,"status":"running","mods":[{"id":"sprint",
  "name":"Sprint Forever","version":"1.0.0","status":"loaded","reason":"","folder":"…\\shrouded_engine_tweaks\\mods\\sprint"}]}`.
- `last-server.json` (Kiln.Network on a client): `written`, `outcome` (`rejected|welcome|vanilla`), `reason`,
  `server{id, name, address, kiln}`, `modpack`, `required[]`, `truncated`, `missing[]`, `hosted` (the game's own hosted
  world: nothing missing, never offered; absent in an older file: the launcher compares the server id), `history[]` (<= 20 earlier
  entries, newest first, without their own history).

### 5.10 Admin endpoint
`<server>/kiln/data/kiln-admin/endpoint.json`: `{format, protocol: "kap/1", host: "127.0.0.1", port, pid, started,
kiln, admin, server{id,name}, token: "admin-token", bridge}`; written when the listener is up, deleted on unload.
`admin-token`: 64 lower-case hex chars (+ optional newline) = 32 random bytes, created on first start (0600 on Linux).
Port: `kiln/data/kiln-admin/config.json` `"tcp": {"enabled": true, "port": 0}` (0 = the query port number on TCP,
then + 1000 ... + 9000, then any free port). `pid` is the Windows pid (Wine): a stale file is told by a refused
connection. Protocol, ops and the SSH recipe: [admin-protocol.md](admin-protocol.md).

### 5.11 kdh1 (directory hash) and kms1 (mod-set fingerprint)
kdh1 of a folder:
1. Walk it recursively. Skip every entry (file or folder, any depth) whose name starts with `.`, with everything below
   it (`.kiln-source.json`, `.disabled`, `.schema/`, `.git/`).
2. Symlinks are **refused**: the root itself may be a symlink, but any symlink or Windows reparse point below it is an
   error, as is anything that is neither a regular file nor a folder. Empty folders contribute nothing.
3. Each regular file's relative path: components joined with `/`, each component NFC-normalized, UTF-8. A name that is
   not valid Unicode, or two paths equal after NFC, is an error. No case folding.
4. Sort the paths by their UTF-8 bytes.
5. `h = sha256( for each file: path "\0" size "\0" sha256hex(content) "\n" )`, size in decimal without leading zeros,
   sha256hex lower-case.
6. Result `"kdh1:" + lower-case hex(h)`. An empty folder: `kdh1:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
`hash8` = the first 8 hex chars after `kdh1:`. For a zip (modpack), hash each folder's entries with the same rules
(directory entries ignored, symlink entries refused; `kiln_manage::hash::dirhash_records`).
Reference: `schema/fixtures/dirhash/dirhash.py` (`--check`); cases and expected hashes in `schema/fixtures/dirhash/`;
Rust: `kiln_manage::hash::dirhash` (tested against the same fixtures).

**A mod's identity (dev.27)** is the kdh1 of the files that are the mod. kdh1 itself is unchanged; what changed is
which files count for a Kiln mod:
- A **Kiln mod** is a folder with a regular file named `kiln.mod.json` directly in it. Its **cook outputs**, the folder
  `assets/cooked` directly in it with everything below, are not walked: a cook (`kiln cook`) makes them on each machine
  from that machine's game files, so a dedicated server's differ from a player's (a server makes no models, each cook
  report names its own paths), and they are never shipped. The identity of a cooked mod is the kdh1 of the same mod
  without `assets/cooked`.
- Names are compared as kdh1 writes them (NFC, no case folding): `Assets/Cooked`, a file named `assets/cooked`, a
  `cooked` folder anywhere else and an `assets/cooked` below another folder all count. A link named `assets/cooked` is
  refused like any other; links inside the folder are not looked at.
- Every other folder (a native mod, another loader's mod, a folder without `kiln.mod.json`) has its plain kdh1 as its
  identity: its files are all its own, whatever they are called.
- For a zip's entries: when the mod's entries hold `kiln.mod.json` at its top, the entries below `assets/cooked/` do not
  count (`hash::mod_identity_records`); a modpack does not carry them at all (5.8).

**Cook outputs do not travel.** No hash covers `assets/cooked`, so Kiln never takes a file there from anyone else: the
Library leaves the folder out when it imports a Kiln mod from a folder or an archive (`library::copy_kiln_mod_tree`;
the import's `notes` say so), and a modpack that carries one is refused (5.8). What is in `assets/cooked` of a Library
entry or of a copy of it was made on this PC by a cook. The game itself does not check that: kiln.assets serves what
the mod's manifest names, and other services read the cook's index files there, so a folder a person copies into the
game by hand (a drop-in mod, a server's mods folder) runs with whatever `assets/cooked` it holds, as it runs with
whatever DLL it holds.

**Entries of an older Kiln.** Before dev.27 a Kiln mod imported from a cooked folder was recorded under the kdh1 of
the whole folder. `library::migrate_identities` (run before an activation and before a server sync) verifies such an
entry against that hash, copies it to `<version>+<hash8 of its identity>` with its record, makes every profile that
lists the old hash list the new one, and removes the old entry once nothing uses it. Its files come along as they are.
An entry whose files changed since its import is left as it is.

Everything that names a Kiln mod by a hash uses its identity: what a server advertises (`required[].hash`), what the
game compares at a join (`last-server.json` `missing`), a modpack's `mods[].hash`, the library (`<version>+<hash8>`,
the import record) and a profile's entries, the copy-mode copies under `kiln/profiles/` (a copy the player cooked is
kept), a server sync, and the mod-set fingerprint of drop-in mods. Before dev.27 these used the plain kdh1, so a cooked
mod read as changed. The cook keeps its side of the rule: everything it makes from the game goes into `assets/cooked/`
(a recipe whose `out` is elsewhere is refused), and outside it a cook writes only what follows from the mod's own files,
the same bytes on every machine, and only when they differ (docs/kiln/modding/models.md, "What a cook writes").
Reference: `dirhash.py --mod DIR`, `mod_identity`; `expected.json` holds `cases` (kdh1) and `identity` for every case;
Rust: `hash::mod_identity`, `hash::of_mod(kind, dir)`; C#: `DirHash.ModIdentity` (`managed/tests/Kiln.Network.Tests/DirHashTests.cs`
runs the same fixtures).

kms1 of a profile: `sha256( for each enabled entry of every kind, sorted by (kind, id) bytewise:
kind "\0" id "\0" version "\0" hash "\n" )` as `"kms1:" + hex` (`kiln_manage::hash::fingerprint`). A launch whose
kms1 differs from the game's last launched kms1 backs up the saves first.

### 5.12 Manifest `clients`
`kiln.mod.json` `"clients": "none" | "kiln" | "mod"`: what joining players need. Default from `side`: `server` ->
`none`, `client`/`both` -> `mod`. C#: `Manifest.Clients` (`ManifestClients`), schema `kiln.mod.schema.json`, docs
`docs/kiln/modding/manifest.md`. native.json has the same key and default rule.

### 5.13 ABI additions
- `KILN_PATH_CONFIG = 6` (mod configs: `<kiln>/config`, the profile's `configDir` once native-loader wires it) and
  `KILN_PATH_STATE = 7` (`<kiln>/state`, created at boot). An older core answers `KILN_ERR_INVALID_ARGUMENT`.
- `KilnBootInfo` v2 = v1 `{size, reserved, proxy_module}` + `{u32 play_once; u32 chain_count; const KilnChainEntry*
  chain; KilnStr play_once_nonce; KilnStr play_once_profile}` (64 bytes). The core copies fields as far as `size`
  covers them (`KILN_BOOT_INFO_HAS`); a v1 proxy (16 bytes) reads as play-once NONE, empty chain
  (`kiln::boot_info()`). `KILN_PLAY_ONCE_*`: NONE 0, VANILLA 1, NO_MODS 2, PROFILE 3, IGNORED_EXPIRED 16,
  IGNORED_UNDELETABLE 17, IGNORED_SERVER 18, IGNORED_INVALID 19. `KILN_CHAIN_*`: LOADED 0, FAILED 1, MISSING 2,
  REFUSED 3, DUPLICATE 4, SKIPPED 5, LATE 6 (dev.21, appended; `KilnBootInfo` is unchanged: a late entry is an ordinary
  `chain[]` entry with status 6).
- `KilnBootInfo` v3 (dev.24, 72 bytes) appends `u64 addon_log_size`: the length of `<game>\shroudlink_client.log`
  before the proxy loaded any chained DLL (0 without the file, `KILN_BOOT_LOG_UNKNOWN` = `UINT64_MAX` when it could not
  tell). The core's wait for shroudlink's client addon reads only what follows. With an older proxy the core takes it as
  unknown: then only the addon's UI hook can end that wait.
- `kiln_native.h`: `KilnNativeContext {size, abi_version, phase, side, api, owner, module, id, version, directory,
  config_dir (<configDir>/native/<id>), data_dir (<dataDir>/<id>), hooks_enabled, play_once}`,
  `typedef int (KILN_CALL* KilnNativeInitFn)(const KilnNativeContext*)`, phases, `KILN_NATIVE_INIT_{OK,FAILED,UNSUPPORTED}`.
- `services/kiln_profile.h`: `kiln.profile` v1 `{header, mode, id, name, generation, count(kind), get(kind, i,
  KilnProfileMod*), dropins (bitmask 1<<kind), config_dir, data_dir, json}`; modes LEGACY 0, ACTIVE 1, ONCE 2,
  NO_MODS 3, INVALID 4; kinds KILN 0, NATIVE 1, PLUGIN 2, EMF 3. Implemented by native-loader.
- `kiln_version.h` (generated from VERSION): `KILN_VERSION`, `_MAJOR`, `_MINOR`, `_PATCH`, `_PRERELEASE`.
- Managed: `KilnPaths.Config` (internal setter), `KilnPaths.State`; `KilnAbi.PathConfig/PathState`.

### 5.14 ABI additions (clients without Kiln)
All additive, appended to v1 tables (check `header.size`). Docs: `docs/kiln/network.md`
"Clients without Kiln".
- `kiln.assets`: `resource_is_new(key)` (1 once a key was served as a resource the game doesn't have, for the rest of the
  process; lock-free) and `resource_new_count(type)`.
- `kiln.net`: `gate_set(mode)`, `gate_info(KilnNetGateInfo*)`, `gate_release(peer)`, `entity_withheld(entity)`,
  `template_kiln_only(guid)`, `withhold_set(peer, on)`, `lab_selftest(op, a, b, out)` (lab transport only);
  `KilnNetPeerInfo.flags` bits `PROVED` 0x04, `HELD` 0x08, `WITHHELD` 0x10; `KILN_NET_GATE_CAN_GUARD`;
  `KILN_NET_KNOCK_INIT`, the four-byte proof frame `'K','N',1,10`.
- Kiln.Network wire: frame type 10 `Knock` (client to server, no body, swallowed by kiln.net). Old servers ignore it, a
  vanilla server logs one warning per frame.
- `kiln/config/network.json`: `vanillaHoldSeconds` (default 5, 1..20), `definitionsWaitSeconds` (default 5, 1..30). `modSync` left out now differs from
  `"warn"`: a Kiln client without a mod stating `clients: "mod"` is refused (REJECT with the usual properties, so `last-server.json` says `rejected` and the modpack offer fires).
- sigdb features `net.gate`, `net.withhold`, `net.client_guard` (server and client exes; on a server the guard exists only for lab
  tests), `net.client_ready` (client exe). The build records' symbol lists don't have the new symbols yet: the core resolves them from the patterns at boot and reads only
  `verified` from a record. Regenerate them at the next `build_record.py` run.

### 5.15 Vendor files (`<game>/kiln/vendor/`, 0.2.0-dev.29)
Vendor upscaler files the launcher downloads when the player asks for them (Settings, Graphics, Upscaler files, and the
offer under the upscaler choice; the Deck's Settings: [app.md](app.md) "Upscaler behind FSR 3 and the upscaler files";
[../graphics.md](../graphics.md) "Upscaler files"). Out of the preview `upscalers` since 0.2.0-dev.29.18, when phase B
runs them behind the game's FSR 3 option. `kiln_manage::vendor`.
A client only: a dedicated server is `unsupported`.
- **The manifest** `launcher/src/data/vendor-components.json` (compiled into the launcher; `vendor-components.schema.json`):
  `{format: 1, components: [{id, vendor, vendorName, name, enables, version, source?, license: {name, file, sha256,
  from}, files: [{name, url, size, sha256, gitBlob?}]}]}`. In 0.2.0-dev.29.18: `amd-fsr` 2.4.0 (AMD FSR:
  `amd_fidelityfx_loader_vk.dll`, `amd_fidelityfx_upscaler_vk.dll`, `amd_fidelityfx_framegeneration_vk.dll`, 72,981,808
  bytes, "MIT licence") and `intel-xess` 3.0.2 (Intel XeSS: `libxess.dll`, 77,795,704 bytes, "Intel Simplified Software
  License (October 2022)"), every file pinned. A `sha256` is 64 lower-case hex digits or `"not-pinned"`: a component with
  any file not pinned is `unavailable` and is never downloaded. Rules beyond the schema (`manifest::parse`): ids unique;
  one component per vendor and version (they share a folder); `vendor` is `amd`, `intel` or `nvidia`, and every URL is on
  that vendor's own GitHub organisation (in code, not data: `GPUOpen-LibrariesAndSDKs`; `intel`; `NVIDIA`, `NVIDIA-RTX`)
  at the tag `<version>` or `v<version>`; file names without a folder, not `kiln-vendor.json`, `*.part` or `*.tmp`;
  sizes from 1 byte to 512 MiB; the licence text bundled (`launcher/src/data/vendor-licenses/`: the vendor's words
  verbatim, LF line ends; AMD's is the MIT section of `Kits/FidelityFX/docs/license.md`, which lists the signed DLLs
  under it) and `license.sha256` the SHA-256 of that text with LF line ends; only NVIDIA's own files may be called DLSS
  (in the name, the vendor's name, the sentence or the licence's name); no key outside the schema (the manifest ships
  inside the build, so an unknown key is a typo, and both the schema and the parser refuse it).
- **Pinning.** `python3 -I tools/release/vendor_pin.py --dir <folder> [--component <id>] [--write | --check]`, with copies
  downloaded once by hand from the manifest's URLs (`<folder>/<id>-<version>/<name>`, `<folder>/<id>/<name>` or
  `<folder>/<name>`): each copy must have the manifest's size and git blob SHA-1 (GitHub's for the file at the tag) before
  its SHA-256 is written; a pinned hash is never replaced (a different one is an error: the file changed under the tag).
  The tool downloads nothing.
- **Hosts.** Only the manifest's URLs are fetched, each in one of three forms:
  `https://raw.githubusercontent.com/<org>/<repo>/<tag>/<path>` (a file at a tag, served directly: **no redirect is
  followed**; every file of 0.2.0-dev.29.18 is one of these), `https://media.githubusercontent.com/media/<org>/<repo>/<tag>/<path>`
  (a Git LFS file at a tag; no redirect either), or `https://github.com/<org>/<repo>/releases/download/<tag>/<asset>` (a
  release asset: exactly one redirect, carrying nothing but the user agent, to GitHub's asset storage
  `release-assets.githubusercontent.com` or `objects.githubusercontent.com`). Any other redirect, a second one, or one to
  `http` fails the download (`refused`). Through `kiln_manage::net`: https on port 443, public addresses only, no proxy,
  no cookie, no credential; the headers are the User-Agent `kiln/<version>`, `Accept-Encoding: identity` (the file's own
  bytes, so the size bound, the hash and a range count the same bytes; kept on the release asset's redirect), `Range`
  when a download goes on, and the transport's `Accept: */*`, nothing else; the real transport gives up after 10 s
  without a connection and 60 s without a byte. What the host sees: the player's IP address, that user agent and the
  file asked for.
- **The download** (`vendor_download`, a task). Each file goes to `<name>.part` in its version folder, is hashed as it
  arrives, may not pass the manifest's size (a longer answer stops it), and becomes `<name>` only when its size and
  SHA-256 are the manifest's; a file that differs is deleted (`hash_mismatch`, the message names the file and the host).
  Cancel and a broken connection keep the `.part`; the next download goes on with `Range: bytes=<n>-` (a raw file; a
  release asset starts again, since its redirect carries no range). An answer that does not continue the same file (a
  200, a 416, another range, or a whole that does not verify) starts that file again once, from byte 0. A file already in
  the folder with the manifest's size and SHA-256 is kept. Refused while the game runs (`game_running`), for a
  component not pinned (`refused`), without the player's agreement to the licence
  text (`consent_required`), and when the game's disk has less free space than the bytes still to come plus 256 MiB and
  5 % (`refused`, before anything is fetched; `data_folder::needed_bytes`). A write that fails mid-download is `io`.
- **Layout.** `<game>/kiln/vendor/<vendor>/<version>/` holds the component's files by their manifest names and the
  receipt; nothing goes into the game's own folder (other loaders and Kiln's step-aside list look there).
  `<game>/kiln/vendor/.lock` is locked while a download or a removal of that game runs (`busy` for a second one); it is
  never removed. Nothing below the game folder is reached through a link.
- **The receipt** `kiln-vendor.json` (`vendor-receipt.schema.json`): `{format: 1, component, vendor, version, installed,
  installer, license: {name, sha256}, files: [{name, size, sha256, url}]}`, written last and atomically, after every file
  verified, and removed first when a download of that folder starts. A folder without one is not complete and is not an
  install; its `version` is the folder's name. Anything can write the game folder, so a reader trusts no name in it: a
  receipt whose `version` is not a version or that names a file that is not a plain manifest-style name (a folder, `..`,
  an absolute path, a trailing dot, a Windows device name) is not an install, and removal deletes only plain names
  inside the version folder.
- **What Kiln in the game reads (phase B, 0.2.0-dev.29.18).** The game side trusts nothing in the game folder,
  the receipt included (anything can write it), only the list it was built with: the same manifest, shipped with the
  runtime of the same release, as `(component, version, file name, size, sha256)`.
  - **Which folder:** for each component, the newest version on that list whose folder has a receipt of that `component`
    and `format` 1. A folder of a version the list does not have is skipped (a launcher newer than the game's runtime
    downloads a version the runtime cannot check: no upscaler, never an unchecked one). `*.part` and `*.tmp` files and a
    folder without a receipt are never used.
  - **Which files:** the names, sizes and hashes come from the built-in list, never from the receipt (a receipt's names
    are never joined to a path).
  - **The check:** every file of the component, before the first one loads (AMD's loader loads the other two by name).
    Each is opened with sharing that refuses writes and deletes (`FILE_SHARE_READ` only), hashed through that handle
    (about 0.1 s for 80 MB, once per start) and kept open until it is loaded, so the bytes checked are the bytes loaded.
    A size or SHA-256 other than the list's: that component is not used this start, and the log says which file.
  - **What binds the check to the load:** the path. On Windows the open handle refuses writes and deletes, and Windows
    refuses to rename a folder with an open file in it, so the file that loads is the file that was hashed. Under Proton
    those sharing rules bind only Wine's own processes: a native Linux program running as the player could swap the
    folder between the check and the load. That is accepted. A program with the player's rights can already replace
    `kiln-ui.dll` or `dinput8.dll` (the same stance as `cooktrust.h` takes for cooked files), so the check guards against
    damaged, unfinished or mixed-up downloads and other tools' files, not against such a program.
  - **Loading:** by the full path in that folder with `LoadLibraryExW(..., LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
    LOAD_LIBRARY_SEARCH_SYSTEM32)`, never from the game's folder or `PATH`; AMD's upscaler DLL before its loader, so the
    loader's load by name finds the module already checked and loaded (the frame generation DLL is checked with them;
    Kiln makes no frame generation context in 0.2.0-dev.29.18 and does not map it). A file of one of these
    names in the game's own folder is another tool's: step aside (as `graphicsStepAside` does) rather than load next to it.
  - **Never:** a hook in or a change to `libxess.dll` (Intel's licence), a write into `kiln/vendor/` (the launcher's).
- **Update.** A newer manifest version downloads into its own folder; the earlier versions of the component go only
  after the new one verified, so a failed update keeps the old one.
- **Remove** (`vendor_remove`): every version folder of the component (found by their receipts) and an interrupted
  download of its manifest version, by the names Kiln put there (the receipt's and the manifest's files, their `.part`
  files, the receipt): the receipt first, then the files, then the folder and `<vendor>/` when they are empty. Anything
  else in a folder stays, named in the result, and so does the folder.
- **The licence agreements** `<data>/state/vendor-licenses.json` (`vendor-licenses.schema.json`): `{format: 1, vendors:
  {<vendor>: [{license, sha256, component, version, agreed}]}}`, added to when the player clicks "I agree, download". A
  download needs its vendor's entry with the component's `license.sha256`; a changed text asks again. Not read by the
  game. A file of another format counts as none and is replaced at the next agreement.

## 6. Path translation
`kiln_manage::winpath`, `kiln_manage::game::PathMode`:
- **Z mode** (Proton): `/home/u/x` <-> `Z:\home\u\x`, used only when `<pfx>/dosdevices/z:` resolves to `/` and the
  launcher data dir is a normal host path. Every `/` becomes `\`. A component Windows cannot name (`<>:"|?*\`,
  control characters, a trailing dot or space) does not translate: copy mode for the whole profile.
- **Native mode** (Windows): real paths as they are (no `\\?\` prefix).
- **Copy mode**: Flatpak or Snap Steam (the sandbox cannot see `~/.local/share/kiln`), a missing or redirected `Z:`,
  or an untranslatable path. Activation copies each enabled mod folder to `<game>/kiln/profiles/<pid>/<kind>/<id>/`
  (verified by kdh1 after the copy; unchanged copies are kept; files are made writable by their owner, 0.2.0-dev.29.18),
  a copy that changed in the game folder is never replaced without a word (0.2.0-dev.29.18): one that still holds a
  Library version of the mod is replaced as before; one whose only change is its kind's settings files
  (`engine_mods::settings_files`) and the mod's own output stays as it is; any other is moved to
  `<game>/kiln/backups/profile-copy-<pid>-<kind>-<id>-<yyyymmddThhmmssZ>/` (the three newest of each mod are kept)
  before the Library's files go in, with a
  sentence in the activation's `warnings` naming the files and that folder (for ShroudForge and SET also that the engine
  never read that copy and where settings apply). The profile's config dir goes to
  `<game>/kiln/profiles/<pid>/config/` (copied back to `<data>/profiles/<pid>/config/` before the next activation and
  after the game exits), and writes relative paths (`profiles/<pid>/kiln/<id>`), resolved against `<game>/kiln/`.
  The copy back goes file by file against `<data>/profiles/<pid>/config-sync/<installId>.json` (section 1), what the
  game's copy held when Kiln last copied it in or back (0.2.0-dev.29.16; before, the game's copy overwrote every file,
  so a Configs edit in the launcher was lost at the next activation, a mod update for one): a file the game changed or
  created comes back; one only the data folder changed (a launcher edit) stays and goes in at the activation; one both
  changed keeps the newer by modification time (the data folder's on a tie) and the other is saved beside it in the
  data folder as `<name>.conflict-<yyyymmddThhmmssZ>` (`-2`, ... when taken), with a sentence in the activation's
  `warnings` (in the launcher's log for the copy back after the game exits). A file missing from the game's copy is
  never deleted from the data folder; one deleted from the data folder (a whole-file reset) stays deleted while the
  game's copy of it is unchanged. Without a record (the first copy back after the update, an unreadable record) or
  without the profile's `config/` in the data folder, every file the two hold differently is such a conflict, and a
  file only one holds is kept. The profile's native switches are written into the data folder's `native.json` before
  the copy in, so both copies are the same afterwards.
  The only paths that leave `<game>/kiln/`: an EML mod a native DLL reads in the game's mods folder and an XHL
  companion as `../mods/<folder>`, an enabled Greeneye mod Kiln placed as `../greeneye/<id>`, an enabled Flame mod Kiln
  placed as `../mods/<id>`, each one folder name under the rule of `schema/fixtures/profile/game-folder-names.json`.
  Every kiln-core before `profileReader` 2 calls a profile with such an EML or Greeneye path invalid and loads no mods,
  and every one before 3 one with such a Flame path, so the launcher writes these forms only when the bundle the game's
  receipt names declares that revision or more and the receipt's `kiln/kiln-core.dll` is the file in the game;
  otherwise (an older runtime, a rollback, an older core unzipped by hand over an install, or a runtime the
  launcher has no bundle of), and for a hand-installed copy whose folder name the rule refuses (Kiln places no second
  copy beside it), those entries keep their `profiles/<pid>/<kind>/<id>` copy. After every install, update or
  rollback, of a client or a server, through the launcher (`runtime_install`, `runtime_rollback`, `server_deploy`:
  `quick::resume_active`) or `kilnctl` (`profile::refresh_active`), the active profile is written again for the runtime
  the game has then; the task's result carries that `activation`, and a failure of it is one of its `warnings`.
- Reverse (reading state files): `Z:\a\b` -> `/a/b`; `C:\…` -> `<pfx>/drive_c/…`; other letters through
  `<pfx>/dosdevices/<l>:`; relative -> `<game>/kiln/…`.

## 7. Flows
### Activation (`profile_activate`)
1. Refuse while the game runs. Resolve the profile (preview errors are shown, not fatal: the game reports the same).
2. Path mode (6). Copy mode: sync folders.
3. Write `<configDir>/native.json` from the profile's native switches; write `chain.ini [profile]` (dllmain natives).
   Place the fixed-folder copies (5.1, `engine_mods::place`). The answer, `Activation`, says what that left for the
   player: `retained` (`{path, reason, mayRun}`, as above), `keptFiles` (one sentence per mod whose files went to or came
   back from `kiln/kept-mod-files/` or `kiln/state/placed-settings/`), `warnings` (notes such as a ShroudForge setting
   value a new version refuses, a profile entry the player's own folder serves), `safetyNotes` (dev.26: the sentences of
   `warnings` about a player's own folder that may still run although the profile has its mod off; 0.2.0-dev.31.12: for a
   ShroudForge entry, or an EML entry in ShroudForge's mods folder with ShroudForge's switch on, the sentence says that
   Kiln has the ShroudForge runtime it hosts skip the folder at each start when that runtime takes Kiln's skip list
   (the bundled one from 0.2.0-dev.31.12 on; kiln.log says whether it was skipped), and how to stop it where Kiln
   cannot: [app.md](app.md)), `ownLoaders`. Each `retained` reason
   ends with what still runs from that folder. The launcher shows the first three until dismissed, each sentence once
   per game (`notices_unseen`; [app.md](app.md) "Notices about mod folders"), wherever a command
   returns an `Activation` or carries one (`AddReport.activation`, `ModpackInstallResult.activation`,
   `LaunchResult.activation`, an install result's `activation`, an error's `detail.activation`: `shell/notices.ts`).
   A re-apply without `profile_activate` (a profile save, a change outside the launcher, the watcher's start-up pass,
   a profile made or turned back on by `profile_ensure_default`) keeps its `Activation` in the launcher until the window
   takes it with `activation_notices` (each once), and says so with `kiln://activation`. `launch_play` returns
   `LaunchResult.activation` when it wrote the profile again before the start, or when reconciling an unchanged
   profile's fixed folders kept a copy, files or notes (a placed copy the player changed since).
4. Write `kiln/profile.json` (generation + 1, fingerprint, writer, written) atomically; delete a stale
   `kiln/chain/once.ini` and `kiln/profile.once.json`.
5. Record the activation in `<data>/installs/<installId>.json`; emit `kiln://game` `{kind: "status"}`.
Deactivate: move `profile.json` to `kiln/backups/profile-<ts>.json`, clear `[profile]`: the legacy layout.

### Add a mod (`library_add`: a drop anywhere on the launcher window)
1. Refuse at once (no task) when the game has no `kiln/` folder (`unsupported`: install Kiln first).
2. Where the mods go is decided before anything changes: the profile the game's `profile.json` names (when this data
   folder has it); else, with no profile at all, "My mods" (made below); else, with profiles but none active in the
   game, the profile the launcher remembers for it (`<data>/installs/<installId>.active.json`, which an uninstall of Kiln
   leaves while `kiln/profile.json` goes with Kiln's files: it is the profile the player had), activated again
   (`AddReport.resumed`); else nowhere: a deliberate "No profile" (`profile_deactivate` clears the record), a remembered
   profile that is gone, or another data folder's `profile.json` leave the files in the Library only, and
   `AddReport.libraryOnly` says so in plain words. `library_add_to_profile(gameId, profileId, entries)` then puts the
   entries into the profile the player picks (the result is an `AddReport` with an empty `import`; a profile that is not
   active stays as it is). "My mods" or the remembered profile while the game runs is refused (`game_running`) before the
   import.
3. Modpack zips (`kiln-modpack.json` at the root) are not imported: the result lists them (`modpacks`) for the consent
   flow (`modpack_inspect`/`modpack_install` with `{path}`). A modpack folder is skipped ("drop its zip").
4. `library::import` the rest; every entry imported or already there is added to the profile, enabled: new, or
   replacing another version of the same id, or switching a disabled entry on (`changes`, with what each replaced).
5. "My mods": `profile::create`, `dropins` every kind (what the legacy layout loaded still loads), activated
   (activation flow). A failed activation removes it.
6. Save the profile; each entry that replaced another version of the same mod keeps that version for a roll back (dev.29,
   `kiln_manage::kept`: with a copy of the settings its game-folder copies hold now, before the placement below carries
   them over; `keepVersions` in `settings.json`; not when the Library's disk would then have less free than
   `data_folder::needed_bytes` asks for: `notices` says so). Then `profile::refresh_active` writes `profile.json` again
   (not while the game runs: Play does it before the next start). When that write fails, the profile change is undone (a "My mods" made for it is removed)
   and the task fails; the library keeps what it imported.
7. `missing`: what the added Kiln mods (non-optional `dependencies`, not `kiln`) and native mods (`requires`) need and
   the profile lacks (enabled, any version).
Undo is `profile_revert(profileId, changes)`: each changed entry goes back (removed when it was new), newest first; an
entry changed since is left alone; then `profile.json` is written again where the profile is active. An entry that goes
back to another version is kept the same way (dev.29), and a kept version that comes back gets its settings back.
`profile_ensure_default(gameId)` is step 2 and 5 alone (Play runs it after it installed Kiln; it also turns the
remembered profile back on, so a reinstall brings the player's profile back before any drop).

What an import says (`ImportReport`): `imported` and `existing` (with `names`, the display name of each by `<kind>/<id>`),
`skipped` (left out, each with a reason: one sentence saying what happened and, when the player has to do something,
what; no final period), and `notes` (information, nothing to do: "EML's own loader is in this package: not needed,
Kiln runs EML mods", once per text). The launcher shows one short toast (names added, one left-out line) and
"Details" for the whole list. Archives: `.zip`, `.7z` (pure Rust, no password) and `.rar` (a tool already on the PC:
`unrar`, 7-Zip, or `bsdtar`/Windows' `tar.exe`; without one the reason says to extract it and drop the folder) are
unpacked into a private folder under the same limits.

### Launch (`launch_play`)
Before `launch_play` the launcher runs, by mode (`shell/play.ts` `beforeStart`): `profile`, `cook_run` (the cook, then
the boot game-data step); `once`, `boot_snapshot_run` for that profile (the boot game-data step only: a single start
cooks nothing); `vanilla` and `no-mods`, nothing. Neither step refuses the start.

1. Refuse while the game runs (`game_running`). (0.2.0-dev.31.13) Refuse while another start of the game is pending
   (`busy`, `detail: {kind: "already_starting"}`, "The game is already starting."; `commands/launch.rs`
   `refuse_pending`): a start the launcher tracks (Kiln or Vanilla) for which Steam was asked (or the direct start
   made), its game process not seen, less than 5 minutes (`STEAM_WAIT_MS`) since the ask; or another `launch_play` /
   `launch_confirm` inside its own start for the game. The pending start's record is left as it is.
2. Mode `profile`: nothing to write (Steam's Play button does the same). Modes `vanilla`/`no-mods`: write
   play-once.ini. Mode `once`: render the profile into `kiln/profile.once.json` (+ `kiln/chain/once.ini`: its dllmain
   natives and its chain plan, so the single start follows this profile's compat rules and late list), then
   play-once.ini `mode=profile`.
3. Save backup when the kms1 of what will load differs from the game's last launched kms1 (unless `skipBackup`);
   vanilla and no-mods count as the empty set.
4. Through Steam (the default for Steam's own copy): open `steam://rungameid/1203620` (xdg-open / ShellExecute). Never
   write Steam's config. Directly (dev.29.17; the default for a folder no Steam library knows, `GameInstall.source`
   `manual` and `library` null, else the player's `start` in the install record): the launcher starts the folder's
   `enshrouded.exe` itself (`kiln_manage::direct`): Windows runs it with the game folder as working folder (Kiln's
   `dinput8.dll` is loaded from there first; nothing is set); Linux runs `umu-run <exe>` with `WINEPREFIX` (the
   prefix itself), `GAMEID=umu-default` (never `umu-<appid>`: umu derives `SteamAppId` from it) and `PROTONPATH` (the
   first compatibility-tools Proton found; left out when none: umu's own), or `<proton> run <exe>` with
   `STEAM_COMPAT_DATA_PATH` (the compatdata folder) and `STEAM_COMPAT_CLIENT_INSTALL_PATH` (the Steam root when Steam
   is installed, else Kiln's empty `<kiln-prefixes>/_compat-client`, made at the start), or `wine <exe>` with
   `WINEPREFIX`, all with `WINEDLLOVERRIDES` `dinput8=n,b` (`dinput8,xinput9_1_0=n,b` with a chained Ultimate ASI
   Loader; Wine also `mshtml=`), in the game folder, in its own session, output into `logs/game-start-<installId>.log`.
   The runner is the player's, else automatic (`direct::default_runner`): `umu-run` on PATH, else the first Proton
   found (compatibility-tools folders, GE-Proton newest first: Steam's when installed, `~/.local/share/Steam`,
   `~/.steam/{root,steam}`, `$XDG_DATA_HOME/Steam`, Heroic's `tools/proton`, Lutris's `runners/proton`,
   `/usr/share/steam`; then Valve's Protons in the Steam libraries), else `wine` on PATH; Steam's compat tool mapping
   is not read. The prefix is the player's, else Kiln's own `$XDG_DATA_HOME/kiln-prefixes/<installId>` (outside the
   data folder: never moved with it; the Wine prefix is its `pfx` for every runner); while the install starts directly
   its `prefix` and `platform` (`proton` under umu-run or Proton, `wine` under Wine) are that prefix's (saves, path
   mode). The command is built before step 1's files are written (no runner refuses the start as it is). Inherited
   `WINEPREFIX`, `WINEDLLOVERRIDES`, `STEAM_COMPAT_*`, `SteamAppId`, `SteamGameId`, `GAMEID`, `UMU_ID`,
   `PROTONPATH`, `STORE`, `PROTON_VERB`, `LD_PRELOAD` are dropped. Nothing of a direct start needs Steam (installed or
   running): Kiln starts the exe and nothing else; it never sets `SteamAppId`/`SteamGameId`, never writes
   `steam_appid.txt`, never touches the game's Steam DLLs, and whether a copy needs Steam is that copy's business
   ([custom-game-folders.md](custom-game-folders.md)). Without any Steam install a Steam library folder is added as a
   custom folder (`game::inspect_with` with no roots). The `steam` start step (its id stays) is labelled "Starting the
   game" then, before it begins too (`LauncherStart.direct`); `LaunchResult.via` is `direct` and `uri` empty.
5. Watch: the process appears -> `kiln://game {kind: "started"}`; `state/boot.json` with a live pid and the nonce ->
   `{kind: "boot"}`; `mods.json`/`native.json` updates -> `{kind: "mods"|"native"}`; exit -> `{kind: "stopped"}`.
   play-once.ini still there after 120 s: the proxy did not run; under Proton show the `WINEDLLOVERRIDES="dinput8=n,b"
   %command%` advice (read-only check of the launch options). While the game runs: play-once.ini still there 30 s after
   the process started (`GameStatus.started`; the proxy deletes it in its DllMain), or a non-vanilla start of this
   launcher without a boot.json of this run 60 s in, means Kiln did not load: the state is "No Kiln" (warn), never
   "Starting", and the advice (or, without one, a "send proxy.log" banner) shows (`shell/gameState.ts` `playOnceMissed`,
   `shell/play.ts` `kilnMissing`).

### Install / upgrade / uninstall / rollback
Section 5.7. Long-running: `runtime_install` etc. return a task id; progress through `kiln://task`.

### Modpack join (client)
1. `state/last-server.json` changes (outcome `rejected` with `missing[]`, or `welcome` with a different modpack) ->
   `kiln://last-server`.
2. The consent modal: server name/id, missing mods, URL, size, "runs code on your PC". https only unless
   the settings allow http. Nothing downloads before the user says yes.
3. `modpack_install` (task): download -> zip sha256 (from last-server.json) -> `kiln-modpack.json` -> each mod's hash
   (kdh1; a Kiln mod's identity) -> library import -> profile `Server: <name>` (bound server) -> save backup (reason `modpack`) -> activate -> the UI
   offers Launch.

### Server mods
Sync library mods into `<server>/kiln/mods/<id>/` (+ `.kiln-source.json`), natives into `kiln/native/<id>/`;
switch with `.disabled`; never touch hand-placed folders without a marker. Running servers reload through the admin ops
`mod_load`/`mod_reload`/`mod_unload`.

## 8. Parity fixtures (C# <-> Rust)
`schema/fixtures/{resolver,semver,manifest}/*.json`. The expected values come from the real C# code
(`managed/tests/Kiln.Host.Tests/FixtureTests.cs` checks them; `KILN_FIXTURES_WRITE=1 dotnet test … --filter
FixtureTests` regenerates them: review the diff, it is a contract change). the Rust ports run the same
files.
- resolver: `{description, env: {kiln, api, side, unavailableFeatures[], loaded[<raw manifest>]}, mods: [{dir,
  manifest: <object> | raw: <text>}], expect: {order: [ids], rejected: [{id, dir, state, reason}]}}`. A `raw` that does
  not parse becomes a candidate whose error is the manifest error text. `rejected` is compared as a set (stored sorted
  by dir, then id); `reason` must match exactly.
- manifest: `{raw, syntax?, expect: {ok: true, manifest: {id, name, version, parsedVersion, apiVersion,
  entryAssembly, entryType, side, clients, authors, description, license, url, dependencies[{id, range, optional}],
  loadBefore, loadAfter, incompatible[{id, range}], features, exports, advanced, tests, warnings}} | {ok: false,
  error}}`. `entryAssembly` is `""` when the manifest has none (a mod of data only, dev.22). With `syntax: true` (invalid JSON) only the
  prefix `kiln.mod.json: not valid JSON` must match; other errors match exactly.
- semver: `parse.json` (`cases[{text, expect{ok, major, minor, patch, pre, build, text}}]`), `ranges.json`
  (`cases[{range, version, satisfies}]`, `invalid[{range, ok:false}]`), `order.json` (`ordered[]`, `equal[[a,b]]`),
  `api.json` (`cases[{api, current, satisfies}]`).
- INI and kdh1 fixtures (kdh1 and a mod's identity): sections 5.2, 5.3, 5.11 (Python references).
- KAP: `schema/fixtures/kap/handshake.json`.

## 9. Tauri commands
One file per area in `launcher/src-tauri/src/commands/`; the TypeScript wrappers in `launcher/src/api/commands.ts`
have the same names in camelCase (`profile_get` -> `profileGet`). Arguments are passed by name in camelCase (Tauri maps
them to the Rust snake_case parameters). Every command rejects with `KilnError` (section 11). `TaskId` commands start
a background task and return its id at once; the task's `result` (in `kiln://task`) has the type given after `->`.

Shared types (TypeScript names, defined in `commands.ts`; Rust in kiln-manage unless noted):
`GameId`, `ProfileId`, `ServerId`, `TaskId`, `ModKind = 'kiln'|'native'|'plugin'|'emf'|'eml'|'xhl'|'shroudforge'|'set'|'greeneye'|'flame'|'shroudtopia'|'enshwick'|'shroudlink'|'ember'` (section 5.1), `RunSide = 'server'|'client'`,
`LogTarget = {game: GameId} | {server: ServerId}`, `ConfigTarget = {profile: ProfileId} | {server: ServerId} |
{game: GameId}`.

| File | Command | Arguments | Returns |
|---|---|---|---|
| app | `app_info` | - | `AppInfo` {version, kiln, platform, arch, dataDir, commit, mock, noWebgl, deck: `DeckInfo`, distribution: "direct" \| "nexus", render?: `RenderInfo` (Linux), desktop?: `DesktopInfo` {on, installed, foreign, file} (Linux AppImage), sandbox?: `SandboxInfo` (Linux, dev.29.18), preview?: string[] (dev.29)} |
| app | `app_render_frame` | painted: boolean, visible: boolean | string \| null (visibility updates alone are not success; two consecutive visible animation frames in a shown, non-minimized main window confirm startup; the first automatic recovery attempt returns its explanation once; not sent while the data folder is held, see the data folder's commands) |
| app | `app_started` | - | null (the page's first screen is up; once per start; ends the Linux render watchdog) |
| app | `app_close_seen` | asking: boolean | null (the page's close guard took the window's close request: `asking` the player about unsaved work, else closing; without it the Linux launcher closes the window by itself, app.md "When the page's web process ends"; answers in every data folder state) |
| app | `app_close_unsaved` | unsaved: boolean | null (whether closing the window now would lose work, at the page's start and whenever it changes: a slow page with unsaved work gets 30 s instead of 5 s to answer a close on Linux; answers in every data folder state) |
| app | `app_render_set` | software: boolean | `RenderInfo` (sets or clears `state/software-render.json` for the next start; `unsupported` off Linux) |
| app | `app_render_force_set` | on: boolean | `RenderInfo` (dev.30.7; NVIDIA shared-memory drawing, off on clean starts; explicit off vetoes automatic recovery; `state/force-dmabuf.json`, next start; startup failure disables it; `unsupported` off Linux) |
| app | `app_sandbox_set` | on: boolean | `SandboxInfo` (dev.29.18; on removes `state/webkit-sandbox.json`, off writes it, for the next start; `unsupported` off Linux) |
| app | `app_dialog_default` | kind: string, fileName: string \| null, preferred: string \| null | `string \| null` (dev.30; the folder a file dialog opens in, or `<folder>/<fileName>`: `preferred` when it exists, else the kind's last folder, else Downloads, else home; never the working directory, `$APPDIR` or an AppImage mount; src-tauri/src/dialog_folder.rs) |
| app | `app_dialog_remember` | kind: string, path: string, folder: boolean | `null` (dev.30; keeps the folder the dialog ended in, `path` or its parent, in settings.json `dialogFolders`) |
| app | `app_desktop_set` | on: boolean | `DesktopInfo` (on: writes `kiln-launcher.desktop` and the icons, replacing a file of that name Kiln did not write; off: removes Kiln's, and `state/desktop-entry.json` keeps it off; `unsupported` outside a Linux AppImage) |
| app | `app_settings_get` | - | `LauncherSettings` |
| app | `app_settings_set` | settings: LauncherSettings | `LauncherSettings` |
| app | `app_locales` | - | `LocaleFiles` {dir, files: {tag, text}[], errors: {file, message}[]} (translation files in `<data>/locales/<tag>.json`, as text; at most 64, 1 MB each; [translating.md](../translating.md)) |
| app | `app_open_path` | path: string | null |
| app | `app_open_url` | url: string (https) | null |
| app | `task_list` | - | `Task[]` |
| app | `task_get` | taskId | `Task` |
| app | `task_cancel` | taskId | null |
| app | `app_assets_status` | - | `AssetsStatus {root, editor: {present, bytes, source?, build?, gameId?, gameBuild?, current?, canExtract?, task?, counts?, extracted?, modded?: {items, icons, current, version, generated, mods}}, admin: {map, catalog, bytes, source?, build?, gameBuild?, current?, canExtract?, markers?, task?, counts?, extracted?}}` (gameart additions: `source` "game" \| "copy", `build`/`gameBuild` the data builds of the art and of the installed client, `current` false = the game was updated since: offer to re-extract; [gameart.md](gameart.md)) |
| app | `app_assets_import` | kind: "editor" \| "admin", from: string \| null | `TaskId` (result `AssetsStatus`; `from` null: extracts the art from the installed client, read-only (`kiln_manage::gameart`; `editor`: task "Prepare game art", the art when missing or outdated, then the overlay of mod items `<data>/editor-assets/modded/` when the mods changed; `admin`: relief, tiles, markers (map/game/, every game language), marker icons, fonts, plus the local markers (fallback), finder data and catalogs copied from `$KILN_ADMIN_EXTRAS` when set). The launcher runs it on its own (`<GameArtAuto/>`), there is no button; `from` a folder: copies from that checkout into `<data>/{editor,admin}-assets` (dev fallback); served by the `kiln-asset` protocol; never downloads; while one runs, a second call for the same kind returns the running task) |
| game | `game_list` | - | `GameInstall[]` |
| game | `game_detect` | - | `GameInstall[]` (scans Steam, adds new ones) |
| game | `game_add` | path: string | `GameInstall` |
| game | `game_remove` | gameId | null |
| game | `game_status` | gameId | `GameStatus` (+ `started`: RFC 3339 start of the running game's process, null when stopped; + `otherFolderProfile {id, root?}` when another data folder's profile is active in the game, `activeProfile` null then: `profile::game_profile_owner`; (dev.29.17) + `start`: `'steam'\|'direct'` for a client; + `build: BuildCheck {sha256, short, known, from: 'game'\|'bundle'}`: the exe's sha256 against the game's `kiln/sigdb/builds/<side>/` records (`verified: true` counts), else the `builds` index of the runtime Play would install, left out when neither is there; `launchOptions` null for a direct start; (dev.29.18) + `graphicsTools?: GraphicsToolNote[] {engine: 'optiscaler', file, text, kilnOverlay}` for a client: OptiScaler in the game folder (`game::graphics_tools`, `engines::optiscaler_in`), the file, the Play page's note on Kiln's overlay and whether Kiln's overlay draws next to it (`OverlayMenu=false` and no frame generation in OptiScaler.ini), left out when there is none; (dev.29.18) + `graphicsStepAside?: GraphicsStepAside[] {kind, file, frameCap}` for a client: the tools next to which Kiln's graphics tools (kiln.json `graphics`) step aside (`game::graphics_step_aside`), in this order: `optiscaler` (`engines::optiscaler_in`), `streamline` (`sl.interposer.dll` in the game folder or `Streamline/`), `vkfg` (`EnshroudedVKFG.ini`), `dlsstweaks` (`dlsstweaks.ini`), `nvngx` (`nvngx.dll` or `_nvngx.dll`, not the game's `nvngx_dlss.dll`); names in any case, `file` relative to the game folder with forward slashes; `frameCap` true (the first three): the frame cap steps aside too, false: only the DLSS settings; (dev.29.18) + kind `upscaler`: another tool's upscaler DLL next to the game (`game::UPSCALER_FILES`, the list of kiln.ui's `graphics_aside.h`, the first found), and `upscaler: boolean`: Kiln's upscaler behind the game's FSR 3 steps aside too (the first three and `upscaler`); left out when there is none; (dev.29.18) + `graphicsState?: GraphicsNow {current, state: GraphicsState}` for a client: `<game>/kiln/state/graphics.json` (`game::graphics_now`, `state::read_graphics`; a file of another format or not JSON counts as none), `current` true while this install's game runs and wrote it after it started (on Windows also its pid), left out when there is none) |
| game | `game_start_get` | gameId | (dev.29.17) `StartSetup {mode, settings: StartSettings, custom, windows, steamFound, runners: RunnerInfo[], runner, defaultRunner, problem?, ownPrefix, command: DirectCommand \| null}` (`steamFound`: a Steam install was found; without one "Through Steam" is not offered for a direct start; `runners` in the order automatic takes them; `RunnerInfo {kind: 'umu'\|'proton'\|'wine', path, label, source: 'steam'\|'compat'\|'system'\|'custom', proton?}` (`proton`: umu's `PROTONPATH`), `DirectCommand {program, args, cwd, env, envRemove, runner, prefix}`: what a direct start runs now) |
| game | `game_start_set` | gameId, settings: StartSettings | (dev.29.17) `GameInstall` as it starts now (refused while the game runs, for a server, and for a runner or prefix on Windows; a relative path is refused) |
| runtime | `runtime_list` | - | `RuntimeBundle[]` |
| runtime | `runtime_import` | path: string | `RuntimeBundle` |
| runtime | `runtime_status` | gameId | `InstallState` |
| runtime | `runtime_install` | gameId, version: string, options: InstallOptions | `TaskId` -> `InstallReceipt` + `changedBackedUp: string[]` (Kiln files changed by hand that the install replaced, kept in the backup) + `backup: string \| null` (`<data>/backups/install/<installId>/install-<ts>`) + `restoredShroudforge: string[]` (legacy helpers recovered) + `warnings: string[]` (player replacements kept, with recovery paths) |
| runtime | `runtime_uninstall` | gameId | `TaskId` -> `UninstallReport {removed, restored, adopted, kept, placements: PlacementCleanup, warnings}`; changed player replacements remain, original backup paths are in warnings |
| runtime | `runtime_rollback` | gameId, version: string | `TaskId` -> `InstallReceipt` + `changedBackedUp`, `backup`, `restoredShroudforge`, `warnings` (as `runtime_install`) |
| runtime | `runtime_classify` | path: string | `ProxyClass` |
| profile | `profile_list` | - | `ProfileSummary[]` |
| profile | `profile_get` | profileId | `Profile` |
| profile | `profile_create` | name: string, copyFrom: ProfileId \| null | `Profile` |
| profile | `profile_save` | profile: Profile | `Profile` (conflict on a stale generation) |
| profile | `profile_delete` | profileId | null |
| profile | `profile_resolve` | profileId, side: RunSide, gameId?: the identified game whose kiln.json `mods.hosted`, EMF folder and installed native runtime apply (absent: launcher-scoped preview; see below) | `ResolvePreview` (`withWorld`: a client's Kiln mods that start with the player's own world) |
| profile | `profile_activate` | profileId, gameId | `Activation` |
| profile | `profile_deactivate` | gameId | `PlacementCleanup {removed: Path[], retained: [{path, reason, mayRun}]}`; removes only proven unchanged Kiln copies, keeps player edits and warns that retained mods may still run |
| profile | `profile_active` | gameId | `Activation \| null` (null also when another data folder's profile is active in the game) |
| profile | `activation_notices` | - | `Activation[]`: the re-applies' activations the window has not shown yet, oldest first; each is returned once (`kiln://activation`) |
| profile | `notices_unseen` | gameId, lines: `{text: string, safety?: boolean}[]` | `string[]` (dev.26): the `text`s to raise now: those not reported closed by the player before for this game (`installs/<id>.notices-seen.json`, section 3); asking remembers nothing. With `LauncherSettings.modFolderNotes` off only `safety` lines are considered (a retained folder with `mayRun` and `off`, a sentence of `Activation.safetyNotes`). Refused while the data folder is held, like every command not listed open (`src-tauri/src/folder.rs`) |
| profile | `notices_shown` | gameId, lines: string[] | `null` (dev.26): the player closed the notice that said these `text`s (its close button or one of its buttons on the desktop, OK or B on the Deck's notice dialog; being on screen is not enough): from now on `notices_unseen` leaves them out. Not retried when it fails (the sentence comes again later). Refused while the data folder is held |
| library | `library_list` | kind: ModKind \| null | `LibraryEntry[]` (a version's `changed: true` (dev.27): its files were changed in its folder since the import, looked at cheaply: the entry is hashed only when its manifest or folder is newer than its record; `library_take_over` uses the changed files. A version's `kept` (dev.29, absent otherwise): `KeptInfo {kept: RFC 3339, replacedBy?: version, settings: string[] (relative to the game folder), settingsSize: number, profiles?: string[] (the profiles whose change kept it: only they roll back to it; absent: any)}`, an earlier version an update kept for a roll back; kept versions come after the others in `versions`, so `versions[0]` is the newest one that is not kept unless only kept ones are left) |
| library | `library_detect` | path: string | `Detected` |
| library | `library_import` | paths: string[] | `TaskId` -> `ImportReport` |
| library | `library_add_to_profile` | gameId, profileId, entries: LibraryRef[] | `AddReport` (the entries into the profile the player picked after an add said `libraryOnly`; Undo is `profile_revert`) |
| library | `library_remove` | entry: LibraryRef, force: boolean, shown?: string[] \| null, settings?: boolean | `SettingsRemoval \| null` (null unless `settings` (dev.30, "Remove its settings too") and this was the mod's last version: then `{removed: string[], backup: string \| null, notes: string[]}`, what `kiln_manage::mod_settings` took out after its copy into `backups/mod-settings/`; a failure there is a note, the version is removed all the same. `game_running` while a game runs, or a server whose `profile.json` points into the library, even with `force`; `conflict` while `library_users` lists it, unless `force`. With `shown` (the `library_users` answer a dialog named): `conflict` when it lists anything not in `shown`, whatever `force` says) |
| library | `library_remove_settings` | kind: ModKind, id: string | `SettingsRemoval` (dev.30: a removed mod's settings, for a mod whose files were gone from the Library already; `conflict` while the Library holds a version of it, `game_running` while a known game runs) |
| library | `library_remove_many` | entries: LibraryRef[], force: boolean | `RemoveReport` {removed: LibraryRef[], failed: {entry: LibraryRef, error: KilnError}[]} (Clean up: one look at the running processes and the profiles for the batch; `game_running` for the whole batch, nothing removed, as `library_remove`; otherwise each version not removed is in `failed`) |
| library | `library_unused` | | `LibraryRef[]` (in no profile, no known game's `profile.json`, and no `profile.once.json` while the game's `play-once.ini` asks for it; never a version kept for a roll back (dev.29): it is removed on its own, `library_remove`) |
| library | `library_users` | entry: LibraryRef | `string[]` (profile ids, then `<profile> (active in <game folder>)` / `(play-once in <game folder>)`: what `library_remove` refuses over without `force`) |
| library | `library_lint` | entry: LibraryRef \| null, profileId: ProfileId \| null | `LintReport` (`schema/lint-report.schema.json`) |
| library | `library_take_over` | entry: LibraryRef | `TakenOver` {from: LibraryRef, to: LibraryRef, name} ("Use the changed files": an entry whose files the player changed in its folder, `LibraryVersion.changed` or a refusal's `detail.libraryChanged`, moves to its new hash with a record read from its manifest again; profiles that listed it list the new entry, the move is kept as `migrate_identities` keeps one, and active games get their `profile.json` written again. `game_running` while a game, or a server that may read the entry, runs; `refused` when the folder is no longer a mod of its kind or its manifest gives another id; `to == from` when nothing was changed) |
| library | `library_rollback` | gameId: string \| null, profileId, kind: ModKind, id: string, to: VersionRef \| null | `RollbackReport` {profileId, profileName, kind, id, name, from: VersionRef, to: VersionRef, changes: ProfileChange[], kept: boolean, settings: string[], notices: string[]} (dev.29, "Roll back to <version>": in the profile (the game's active one), the mod's entry becomes its kept version `to` (`VersionRef {version, hash}`; null: the newest kept one), switched on or off as it was; the version rolled away from becomes the kept one (`kept`), with its settings as its game-folder copies hold them now, unless `keepVersions` is 0 or the disk check refuses (`notices` say so). The roll back writes only the profile and `library/kiln-kept.json`; then, as after `profile_save`, `profile.json` is written again where the profile is active and the game is not running, and that placement (`profile::activate` / `refresh_active`, else Play's) puts the kept `settings` back into the mod's copy, once. Undo is `profile_revert(profileId, changes)`, which keeps the version it leaves the same way. `not_found` when the profile does not list the mod or the version is not kept) |
| config | `config_list` | target: ConfigTarget | `ConfigFileInfo[]` |
| config | `config_read` | target, file: string | `ConfigDocument` |
| config | `config_set` | target, file, path: string, value: unknown | `ConfigDocument` |
| config | `config_write` | target, file, text: string | `ConfigDocument` |
| config | `config_reset` | target, file, path: string \| null | `ConfigDocument` |
| config | `config_kiln_read` | gameId | `ConfigDocument` (kiln/kiln.json with the built-in schema snapshot `launcher/src/data/kiln-json.snapshot.json`; (dev.29.18) its `graphics` section: `fpsCap` int 0..500, `dlssPreset` default/J/K/L/M, `dlssScale` game/perMode/custom, `dlssRenderScale` and the four `dlss<Mode>Scale` floats 0.33..1, `readout` bool, none needing a restart; Settings, Graphics edits it) |
| config | `config_kiln_set` | gameId, path, value | `ConfigDocument` |
| config | `config_kiln_reset` | gameId, path | `ConfigDocument` |
| config | `config_native_state` | target | `NativeUserState` |
| config | `config_native_set` | target, modId: string, enabled: boolean \| null | `NativeUserState` |
| launch | `launch_play` | gameId, request: LaunchRequest {mode: 'profile'\|'vanilla'\|'no-mods'\|'once', profileId?, skipBackup?} | `LaunchResult` (+ `via: 'steam'\|'direct'`, dev.29.17) |
| launch | `launch_state` | gameId | `BootState` {boot, mods, native, stale} (launcher type) |
| launch | `launch_prestart` | gameId | `PreStart` or null: what kiln-eml is doing before the game's window exists (`kiln/state/eml.json`, phase running), while the game runs (`kiln_manage::prestart`) |
| launch | `launch_begin` | gameId, mode: LaunchMode | null: Play was pressed (before the cook); the launcher times the start's own steps from now (`kiln_manage::start_progress`). (0.2.0-dev.31.13) Every mode, `vanilla` too (its record goes once its game is seen); `busy` with `detail.kind` `already_starting` while another start is pending (`launch_play` step 1; a game that runs already is `game_running` first; checked again as the start is recorded), and the pending start is kept: the window does not call `launch_end` for that refusal |
| launch | `launch_progress` | gameId | `StartTimeline` or null: the start as one timeline (the launcher's steps, then `kiln/state/progress.json`'s), the running step and the time left from `<data>/state/start-times.json`; app.md "What a start is doing". (0.2.0-dev.31.13) `mode?: LaunchMode`: how the launcher's start was started (absent: not said); a Vanilla start's timeline exists until its game is there |
| launch | `launch_end` | gameId | null: Play ended before the game existed (refused before Steam was asked, or Stop waiting while Steam had not started the game: app.md "What a start is doing"); the start the launcher was timing is over |
| logs | `logs_list` | target: LogTarget | `LogFile[]` |
| logs | `logs_read` | target, file: string, offset: number \| null, maxBytes: number \| null | `LogChunk` |
| logs | `logs_follow` | target, file: string | string (subscription; lines in `kiln://log`) |
| logs | `logs_unfollow` | subscription: string | null |
| logs | `logs_crash_reports` | target | `CrashReport[]` |
| logs | `report_preview` | target: LogTarget \| null, exclude?: string[] | `ReportContents` {files: [{path, group: 'kiln'\|'mods'\|'settings'\|'listings', about, size, excludable, excluded, replaced: {home?: n, player?: n, ...}}], size, summary, never: [{id, text}]} (dev.29.22: "What's in this report"; the report built as `report_save` would write it, nothing written; [support-report.md](support-report.md)) |
| logs | `report_save` | target: LogTarget \| null, path: string, exclude?: string[] (other mods' files left out; dev.29.22), destination?: "chosen" \| "automatic" \| "downloads" | `ReportSaved` {path, size, findings: `ReportFinding[]` {id, level: 'problem'\|'warning'\|'info', text}, errors: string[]} (launcher type; writes the support report zip, `.zip` added when missing; returns the actual saved path. Chosen path first (default), then Kiln state staging + copy, then Downloads/home. Automatic: dialog error, saves in Kiln state, then Downloads/home. Downloads: no dialog, Downloads then home; null target: the system and the launcher only; [support-report.md](support-report.md)) |
| logs | `report_note` | note: "opened" \| "dialogCancelled" \| "dialogChosen" \| "dialogFailed" \| "saveToDownloads" \| "failed", detail?: string | null (records the window's report actions and dialog errors in launcher.log) |
| logs | `report_summary` | target: LogTarget \| null | string (the short summary for a post, redacted like the zip) |
| saves | `saves_list` | gameId | `SaveSlot[]` |
| saves | `saves_backups` | gameId | `SaveBackup[]` |
| saves | `saves_backup` | gameId, note: string \| null | `SaveBackup` |
| saves | `saves_restore` | gameId, backupId: string | null (game_running while it runs) |
| saves | `saves_delete` | gameId, backupId: string | null |
| editor | `editor_list_saves` | - | `EditorSaveFile[]` |
| editor | `editor_file_info` | path: string | `EditorFileInfo` |
| editor | `editor_read_file` | path: string | ArrayBuffer |
| editor | `editor_zstd_decompress` | data: number[] | ArrayBuffer |
| editor | `editor_zstd_compress` | data: number[] | ArrayBuffer |
| editor | `editor_game_running` | - | boolean |
| editor | `editor_backup_file` | path: string | string |
| editor | `editor_write_save` | path: string, data: number[] | null |
| editor | `editor_list_backups` | target: string | `EditorBackup[]` |
| editor | `editor_restore_backup` | src: string, target: string | null |
| editor | `editor_open_backups` | - | null |
| editor | `editor_storage_read` | path: string, gameId: string \| null | `{character, gameId, text: string \| null, local: boolean}` \| null; selected install's character sidecar, without changing either file |
| editor | `editor_return_storage` | request: `EditorStorageReturn` (`path`, `gameId`, `expectedSave`, `expectedSidecar`, `data`, `sidecar`, `oldWitness`, `newWitness`) | `{backup: string}`; LOCAL only, game closed, both originals compared, character and sidecar backed up. Reduced sidecar plus full pre-return historical pair is durable before the character replacement; the frontend returns base-game items only. |
| server | `server_list` | - | `ServerInstance[]` |
| server | `server_get` | serverId | `ServerInstance` |
| server | `server_create` | spec: ServerSpec | `TaskId` -> `ServerInstance` |
| server | `server_import` | dir: string, name: string | `ServerInstance` |
| server | `server_remove` | serverId, deleteFiles: boolean | null |
| server | `server_status` | serverId | `ServerStatus` (+ `installed`: the Kiln runtime in the server folder from its receipt, else a hand-placed Kiln proxy's version or `"unknown"`; null without Kiln and for remote servers) |
| server | `server_start` | serverId | `TaskId` -> `ServerStatus` |
| server | `server_stop` | serverId | `TaskId` -> `ServerStatus` |
| server | `server_restart` | serverId | `TaskId` -> `ServerStatus` |
| server | `server_update` | serverId | `TaskId` -> string \| null (new build id) |
| server | `server_deploy` | serverId, version: string | `TaskId` -> `InstallReceipt` + `changedBackedUp`, `backup` (as `runtime_install`) |
| server | `server_settings_get` | serverId | `ServerSettings` |
| server | `server_settings_set` | serverId, values: unknown | `ServerSettings` |
| server | `server_mods` | serverId | `ServerMod[]` |
| server | `server_mods_sync` | serverId, mods: LibraryRef[] | `TaskId` -> `ServerMod[]` |
| server | `server_mod_enable` | serverId, folder: string, enabled: boolean | null |
| server | `server_backups` | serverId | `ServerBackup[]` |
| server | `server_backup_create` | serverId | `TaskId` -> `ServerBackup` |
| server | `server_backup_restore` | serverId, name: string | `TaskId` -> null |
| server | `server_query` | serverId | `ServerQuery` |
| server | `server_modpack_status` | serverId | `ModpackPublishState` |
| server | `server_modpack_build` | serverId, name: string, version: string | `TaskId` -> `BuiltModpack` |
| server | `server_modpack_publish` | serverId, url: string | `ModpackPublishState` |
| server | `server_console_open` | serverId, maxLines: number \| null (default 5000, at most 50000) | `ConsoleOpen` {subscription, serverId, source: 'capture'\|'gamelog'\|'none', path: string \| null, lines: `ConsoleLine` {text, level: 'info'\|'warn'\|'error'\|'debug'\|'meta'}[], truncated} (the last run of the server's output; new lines in `kiln://console`; remote: unsupported; [servers.md](servers.md) "Console") |
| server | `server_console_close` | subscription: string | null |
| admin | `admin_connect` | serverId, transport: 'auto'\|'tcp'\|'file'\|'ssh' | `AdminSession` |
| admin | `admin_open_web_panel` | serverId, route?: 'auto'\|'local' | `WebPanelInfo` {serverId, origin, fingerprint: string \| null, via: 'local'\|'ssh'\|'hosted', isolated, idleCloseS, connections, public: string \| null, fallback: 'unreachable'\|'refused'\| null}; (0.2.0-dev.31.19) `route` 'auto' (the default): when the file has `public` + `publicSha256` (Kilnwarden 0.1.4: the panel's address on the internet, hosted access `https://<name>.kiln.nexus/` or an own address, written only while it really works), a check of at most 4 s (`kiln_manage::admin::web_panel::public`: the name resolves, a TLS handshake that takes only `publicSha256`'s certificate with the name as SNI, `GET /api/auth/status` answering 200 with `ownerExists` and `launcherLogin` true; no secret or cookie sent); if it passes, the one-time login goes to `<public>launcher#secret=` through a one-use bootstrap address of its own (`http://127.0.0.1:<port>/k/<handle>`, the same page and rules as a gateway's, its listener closed once taken or after 60 s) and no gateway is started: `via: 'hosted'`, `origin` = the address without its trailing slash, `fingerprint` = `publicSha256`, `public` = the address, idleCloseS and connections 0. If the check fails, the panel opens on this computer as below and `fallback` says why ('unreachable': no answer in time; 'refused': another certificate, a TLS alert, an HTTP error, no owner yet, or the launcher's login off). A `public` that is not `https://<DNS name>[:port]/` or has no valid `publicSha256` is ignored. 'local' ("Open on this computer instead") always opens it on this computer. (0.2.0-dev.31.20) 'auto' is 'local' while the server's "Always open the web panel on this computer" is on (`admin_web_panel_settings`, `alwaysLocal`): no check, the address on the internet is never contacted, `fallback` null. A remote server is the same: the file comes over ssh, the check goes straight to the address, the login is minted through the ssh tunnel. Without `public`, as before: opens the server's web panel in the browser from its `launcher-endpoint.json` (`{format: 1, host, port, sha256?}`, in `<data root>/kilnwarden/` (Kilnwarden) or, when that has none, `<data root>/kiln-webadmin/` (Kiln Web Admin, its old name: 0.1.0 test builds; 0.2.0-dev.31.14, `kiln_manage::admin::web_panel::ids`; before 31.14 only `kiln-webadmin`); when both have one and the server's `kiln/state/mods.json` has `kiln-webadmin` loaded, pending or disabled (by the fault policy; review of 0.2.0-dev.31.14), the old copy's (Kilnwarden stays off next to it in those states, so its file is left from an earlier run); the one-time login is minted for that mod's id: the data root is the booted profile's data folder when the server started with `dataDir: profile` (boot.json `profile.dataDir`, when the launcher can map it), else `kiln/data`; read once per open, and the one-time login is minted by the Kiln.Admin of that same root). (0.2.0-dev.31.12) Through the server's own **gateway** (`kiln_manage::admin::web_panel`): a listener the launcher binds and holds on the server's own loopback address (`origin` = `https://127.x.y.z:port`, from `web-panel-origins.json`, per server and certificate, so panels of different servers never share cookies), copying bytes only, to an address of this machine (checked on every connection; an IPv4-mapped address counts as its IPv4 one; unspecified, broadcast and multicast addresses are refused) or through `ssh -W` to a remote server; the leaf certificate (`sha256`) is pinned through that same path; a panel on another machine's address is refused (`unsupported`); on Linux only this user's programs may connect (a connection whose owner the socket table does not show is refused). With `sha256`, Kiln.Admin's `login_mint` secret goes into a one-use bootstrap page (`http://<origin ip>:<port>/k/<handle>`, 60 s, this user only on Linux) that sends the browser on with `location.replace` to `/launcher#secret=`: the browser's command line never holds the secret. Without `sha256`, `/signin` with no secret. The gateway closes 120 s after its last browser connection, after 10 minutes without traffic, on `admin_close_web_panel`, or when the launcher exits. A missing or invalid file: `admin_unavailable` / `invalid_argument` |
| admin | `admin_close_web_panel` | serverId | boolean (0.2.0-dev.31.12): closes the server's gateway (its tabs lose the connection); whether one was open |
| admin | `admin_web_panels` | - | `WebPanelInfo[]` (0.2.0-dev.31.12): the gateways open now |
| admin | `admin_web_panel_settings` | serverId | `WebPanelSettings` {alwaysLocal: boolean} (0.2.0-dev.31.20): the server's web panel settings on the launcher's side, from `web-panel-settings.json` in the launcher's data folder (`{format: 1, servers: {<serverId>: {alwaysLocal}}}`; `kiln_manage::admin::web_panel::settings`); local and remote servers alike. `alwaysLocal` ("Always open the web panel on this computer", the Overview's switch under Open web panel; default false): `admin_open_web_panel` 'auto' opens on this computer and never checks or uses `public`. A file that is there but can't be read (or another format) reads as `alwaysLocal` true for every server |
| admin | `admin_web_panel_settings_set` | serverId, settings: `WebPanelSettings` | `WebPanelSettings` (0.2.0-dev.31.20): saves them (an unknown server: `not_found`, as `server_get`); a server whose settings are all default has no entry. The file is written atomically under `web-panel-settings.json.lock`, kept after the server is removed, and survives restarts. A file that is there but can't be read is never replaced (that would turn every other server's `alwaysLocal` off): the change is refused (`parse`, or the reading error; `detail` {path, reason}) until the file is fixed or deleted |
| admin | `admin_disconnect` | session: string | null |
| admin | `admin_sessions` | - | `AdminSession[]` |
| admin | `admin_call` | session: string, op: string, args: object | unknown (the op's `data`; op_failed with its error) |
| admin | `admin_subscribe` | session: string, topic: string | null (pushes in `kiln://admin`) |
| admin | `admin_unsubscribe` | session: string, topic: string | null |
| modpack | `modpack_pending` | gameId | `LastServer \| null` |
| modpack | `modpack_inspect` | gameId, source: ModpackSource \| null (null: from last-server.json) | `ModpackPreview` |
| modpack | `modpack_install` | gameId, request: ModpackInstallRequest {source, consent: true, allowHttp?, activate?} | `TaskId` -> `ModpackInstallResult` |
| modpack | `modpack_dismiss` | gameId, serverId | null |
| update | `update_status` | - | `UpdateStatus` |
| update | `update_check` | - | `UpdateStatus` (asks the sources now) |
| update | `update_install` | - | `TaskId` -> `UpdateStatus` (download, verify; the AppImage is put in place; `unsupported` for a .deb, .rpm or development build) |
| update | `update_switch` | channel: `stable` \| `dev` \| `beta`, restore?: bool | `TaskId` -> `UpdateStatus` (dev.29: the channel switch the player started, after saving `updateChannel`: the channel's newest build downloaded, verified and put in place like an update; back from a test build to the release line also an older one, to the beta only a newer one; `not_found` when the channel has no other build; `restore`: the restart puts the "before beta" copy back first; `unsupported` as for `update_install`) |
| update | `update_restart` | - | null (restarts into the update that is ready; Windows: runs its installer; returns only on failure) |
| update | `update_skip` | version: string \| null | `UpdateStatus` (null: un-skips) |
| update | `update_ack` | - | `UpdateStatus` (clears `justUpdated`) |

**Profile preview runtime scope.** `profile_resolve` first resolves a supplied `gameId` to that game's path. A bad or
no-longer-known id returns `not_found`; it is not an unbound request and never falls back to the first/default game.
For resolver-planned native mods only, `native.json`'s `kiln` requirement is evaluated by the existing dependency
resolver against that path's `kiln/.install.json` (`InstallReceipt.runtime.version`), when `install::read_receipt`
accepts the receipt and the version parses with the resolver's semantic-version parser. Dependency failures still
propagate to native `requires` dependents. No special range, prerelease or release-floor promotion is added: plain
release alternatives retain their existing dev-as-release rule; explicit numbered-dev alternatives compare the
actual installed version (including a legacy bare `dev` stamp). A future floor still fails.

- No `gameId` (`resolve_preview`, `resolve_preview_for`, `resolve_preview_in(..., None)`, and `kilnctl profile show`):
  the explicitly unbound default remains `kiln_manage::VERSION`, the launcher's/CLI's build stamp, not any discovered
  game's version. A shipped numbered-dev stamp is therefore meaningful, but this is not an installed-runtime verdict.
- Missing, unreadable, malformed, unsupported-format or unsafe-path receipts, and receipts with an unparseable runtime
  version: native resolution retains that same launcher-scoped default. When there are resolver-planned native
  candidates, `warnings` explicitly says which fallback is in use and that installed runtime compatibility is unknown.
  This preserves a useful preview instead of failing the whole profile/managed/EMF flow. It never consults another
  game's receipt, a cached runtime bundle, `boot.json` or the running game's last version.
- A readable but stale receipt is deliberately a recorded-install snapshot: preview does not hash DLLs, compare file
  ages, or prove that the receipt still describes bytes on disk. Its recorded version is used even after an out-of-band
  replacement; a fresh preview rereads it. Installation/status and actual runtime admission remain separate authorities.
- This is **not** an installed managed-mod/API preview. Managed menu/world resolutions still use the launcher build
  and `profile::KILN_API`; `kilnBuild` diagnostics keep their existing separate path. Native packages placed directly
  into the game folder still use `game_files`' placement preview, not the native dependency resolver. Disabled mods,
  skipped engine loaders, runtime admission and modpack-generated floors are unchanged.
- Main-list rows still have their existing optimistic display when a preview is absent or errors: no rejection is
  invented, and that display is not confirmation of installed compatibility. Load order invents no native/managed
  order without a preview. Load order and Native and DLLs show preview warnings; no new runtime-admission guarantee
  is implied by an enabled row. Cache keys include profile, side and game id (null for unbound); game invalidation
  after install/rollback also invalidates only that game's previews, across profiles and sides.

The Nexus Mods build (`AppInfo.distribution` "nexus", built without the `self-update` feature; [releasing.md](releasing.md)
"The two builds") has the same seven commands with no self-update behind them: `update_status`, `update_check`,
`update_skip` and `update_ack` return the same `UpdateStatus` with `phase: "disabled"`, `distribution: "nexus"`,
`auto: false`, `install.inPlace: false` and `page` = the Kiln page on Nexus Mods, and change nothing; `update_install`,
`update_switch` and `update_restart` reject with `unsupported` (detail: that page). Nothing in that build reads the network for updates
or emits `kiln://update`; its UI calls none of these commands.
| library | `library_add` | gameId, paths: string[] | `TaskId` -> `AddReport` (import + add to the game's active profile + write profile.json, section 7 "Add a mod"; rejects at once with `unsupported` when the game has no `kiln/`) |
| profile | `profile_revert` | profileId, changes: ProfileChange[] | `Profile` (undoes a `library_add`; profile.json follows where the profile is active, as after `profile_save`) |
| profile | `profile_ensure_default` | gameId | `Activation \| null` (the active profile's; with no profile at all: "My mods" made and activated; null when profiles exist but none is active in the game) |
| deck | `deck_browse` | dir: string \| null | `DeckFolder` (one folder for the Deck's Add mods, null: Downloads (XDG download dir, else `~/Downloads`, else home): `{dir, name, parent, folders: {name, path}[] (not mods; hidden ones left out), mods: DeckFoundMod {path, file, folder, size, modified, detected: Detected}[] (what `library_detect` recognises: kiln, native, plugin, emf, bare-dll, modpack, or a zip/folder holding mods; newest first; zips listing no manifest or DLL, zips over 512 MiB and DLLs over 96 MiB are not opened), other (files left out), truncated (only the newest 400 entries looked at), hasGame (enshrouded.exe here), places: {name, path, kind: 'downloads'\|'home'\|'desktop'\|'card'\|'games'}[]}`; read-only; [deck.md](deck.md)) |
| deck | `deck_gamepad` | on: boolean | `GamepadStatus` {native (Linux: the evdev reader exists), running, devices: {device, name, vendor, product}[], denied (gamepads in /dev/input that could not be opened)} (starts or stops the reader; events in `kiln://gamepad`) |
| deck | `deck_fullscreen` | on: boolean | boolean (the main window's fullscreen state after the change) |
| deck | `deck_quit` | - | null (exits the launcher: Game Mode has no window buttons) |
| nexus | `nexus_account` | - | `NexusAccount` (backend-held OAuth session metadata; account validation cached for 60 s; never token material) |
| nexus | `nexus_sign_in` | - | `TaskId` (opens browser authorization with PKCE; result `NexusAccount`; cancellation/timeout supported; unavailable until Kiln has its own approved public registration) |
| nexus | `nexus_sign_out` | - | `NexusAccount` (invalidates pending sign-in, clears memory and stored OAuth credentials) |
| nexus | `nexus_search` | query: CatalogQuery | `CatalogPage` (the Kiln filter looks at up to 12 mods per call: `unchecked` > 0, ask again) |
| nexus | `nexus_mod` | modId: string (game-scoped) | `CatalogDetails` |
| nexus | `nexus_installed` | check: boolean | `InstalledCatalogMod[]` (`check`: look up the newest version of each installed file, cached 30 min) |
| nexus | `nexus_download` | gameId, modId, fileId (game-scoped version id) | `TaskId` -> `InstallReport` (premium accounts only; others reject with `refused`, `detail.url` = the file's page with its Mod Manager Download; Kiln's own page (301) is refused) |
| nexus | `nexus_links` | - | `PendingLink[]` (Enshrouded nxm links that arrived: `{id, modId, fileId, expired, received}`) |
| nexus | `nexus_link_install` | gameId, linkId | `TaskId` -> `InstallReport` (the link's key and expiry; an expired link rejects with `refused`; game/Kiln/key prerequisites are checked before claiming the link) |
| nexus | `nexus_link_dismiss` | linkId | null |
| nexus | `nexus_handler_status` | - | `HandlerStatus {supported, registered, active (the system's handler is Kiln now), previous: string \| null (its name)}` |
| nexus | `nexus_handler_set` | on: boolean | `HandlerStatus` (on: remember the current nxm handler, register the launcher; off: give it back; only on the user's click) |
| engines | `engine_settings_get` | kind: ModKind, id, profileId, gameId: GameId \| null | `EngineSettings {kind, id, format: "extended.mod.json" \| "shroudtopia.json" \| "none", file, schema (JSON Schema; ShroudForge's with x-kiln-* hints), values, note?, ownFolder?}` (valid mod id listed in the profile required; ShroudForge reads the folder ShroudForge reads in the selected game only when this profile is active (Kiln's copy, or the player's own folder that serves the entry, also one under another name holding the same package id: then `ownFolder: true`, dev.26), otherwise library defaults; Shroudtopia reads the profile's effective config, using the game copy for active copy mode; other engine kinds `format: "none"` with a note) |
| engines | `engine_settings_set` | kind, id, profileId, gameId \| null, key, value: Json | `EngineSettings` (one declared top-level key with schema validation; profile membership required; `game_running` while a game reading these files runs; ShroudForge requires the profile active in the selected game and either Kiln's unchanged placed copy or (dev.26) the player's own folder that serves the entry: there only that value is written, in that folder's `extended.mod.json`, and Kiln never switches, replaces or removes the folder; a copy of Kiln's whose library files changed stays `refused`; `unsupported` for kinds without settings) |
| engines | `engine_status` | gameId | `EngineStatus[] {kind, plugin, state: Json \| null, file, error?, ownLoader?, effects?, prerequisite?, startupFailed?}`, in `ModKind::ALL` order (the engine plugins' `kiln/state/<engine>.json`, null when not written; invalid, unreadable or symlinked state files report `error`). `ownLoader`: `OwnLoaderNote {kind, engine, file, via, folder, delivery, enabled, copied, notCopied, off?, text}` when the engine's own loader is installed in the game, and what that means for the active profile's mods of the kind (`off`, EML under ShroudForge only, when not empty: ids of `copied` whose copy ShroudForge has switched off, so it does not run them). `effects` (the `shroudforge` row only, always): `SfEffects {file, state: "current"\|"stale"\|"stopped"\|"unavailable"\|"invalid", updatedAt, reason, mods: {id, state, detail}[]}`, ShroudForge's own runtime report (`mods` only while it is current; each `state` is the word ShroudForge or the mod reported, never proof of a gameplay effect). The `xhl` row has `plugin` and `file` empty, `state` null and `prerequisite: {available, text}`: whether XHL-Core was recognized on the game's active proxy or chain paths, a file check, not a runtime report. `startupFailed` (dev.27, `sf_startup::EngineNotes`): ShroudForge's startup pass of the last start failed, `SfStartupNote {error, at?, ownLoader, version?, build?, mods, cure?, text}`, on the `shroudforge` row and, as a pointer, on the `eml` row while ShroudForge runs the EML mods |
| engines | `engine_own_folders` | profileId, gameId | `OwnFolder[] {kind, id, version, path, renameTo?, refusal?}`: the player's own ShroudForge folders that serve entries of the profile in that game (folders Kiln did not place; `engine_mods::own_folders`), each with whether Kiln can take it over. `refusal` absent: its files are the Library entry's; else the sentence why not, naming the first file that differs. `renameTo`: the folder Kiln's copies of the mod have (`<mods>/<id>`) when the player's is called something else. Empty unless the profile is the one the game uses. Reads only: nothing is written, no lock file is made |
| engines | `engine_adopt_folder` | kind: ModKind, id, profileId, gameId | `Adopted {kind, id, version, path, renamedFrom?, already?, settingsKept?}`. "Let Kiln manage this folder" (dev.27, `engine_mods::adopt`): the player's own ShroudForge folder that serves the profile entry becomes Kiln's placement (marker, receipt, inventory), as if activation had copied it there. Only on the player's request, never by itself. Refuses: `unsupported` for another kind; `not_found` when the entry is not in the profile or no folder of the player's holds it; `game_running`; `conflict` unless the profile is the one the game uses; `refused` with the folder's path and the first file that differs when its files are not byte for byte the Library entry's (the switch and the setting values in `extended.mod.json` may differ and stay; a link, a file of the player's, a changed setting description or range, or a second folder with the same package are refused), and for a copy of Kiln's own whose files changed; `hash_mismatch` when the Library entry changed on disk. Nothing in the folder is rewritten: it gets the marker file `.kiln-library.json` and, when it had another name, the name `<id>` (`renamedFrom`). `already`: it was Kiln's, nothing changed. `settingsKept`: the settings whose values are the player's. The next activation switches it as the profile says and updates it; the launcher runs `profile_activate` right after, which also refreshes the notes |
| engines | `eml_backup` | gameId | `EmlBackup[] {file, backup, patched}` (EML's `<exe>.kfc.bak` next to each `.kfc`; `patched`: they differ; read/hash errors and symlinks are refused) |
| engines | `eml_restore` | gameId | Legacy `string[]` signature; always `refused` for direct restoration (`game_running` takes precedence while running). Disable EML mods and launch updated Kiln for ownership-aware archive-writer restoration; shared backups alone prove no ownership. |
| compat | `chain_view` | profileId, gameId | `ChainView {entries: ChainViewEntry[], rules: CompatRuleResult[], ruleErrors, session \| null, overlaps: ChainOverlap[], active, current, error}` (the profile's chain entries with their `[late]` state and who set it (`lateBy`: `player`, `profile` or `rule:<id>`; a `[late]` line that a rule wrote into chain.ini, marked `; kiln rule <id>`, is that rule's even when it is another profile's; dev.29 `earlyBy`: `rule:<id>` when a `before` rule keeps the entry loading first, so its switch is the rule's (`late` false) and `lateBy` is the rule's for the DLL it loads after; a rule result of action `before` carries `early` (the entries it keeps first) besides `targets` (the ones it loads late), and `warning` when it fired but could not order them (nothing moved); `gated`: shroudlink's client addon, by its exports, as a [chain] proxy or a profile native: no late switch is offered and `chain_set_late` refuses late for it, only switching it back), the compat rules evaluated over the profile, and the overlaps of the game's last `state/hooks.json` mapped to chain entries and Library items; `session` null: no hooks.json yet) |
| compat | `chain_set_late` | profileId, gameId, path: string, late: boolean | `ChainView` (a `[profile]` entry changes the profile's late list, saved and re-applied where it is active; a `[chain]` entry changes the game's `chain.ini [late]` for every profile and is refused with `game_running` while the game runs; `not_found` when `path` is not in the profile's chain) |
| found | `found_scan` | gameId, profileId | `FoundReport {gameId, profileId, kilnInstalled, mods: FoundMod[], loaders: OwnLoader[], unreadable: {path, reason}[], truncated}`; `FoundMod {kind: ModKind \| null, name, id, version, path (relative to the game, '/'), state: "loads"\|"off"\|"inProfile"\|"cannotRun", via?: "kiln"\|"ownLoader", reason, fix?: "dropins"\|"add", profileMod?}`, `OwnLoader {engine, name, kinds, file}` (mods in the game folder Kiln did not put there and whether they load with that profile: profiles.md "Mods in the game folder"; never fails over a missing or unreadable folder) |
| found | `found_add` | gameId, profileId, path | `TaskId` -> `AddReport` (imports a `found_scan` entry into the Library and adds it to the profile, enabled; through `library_add`'s flow when the profile is active in the game; a refusal is in `import.skipped`; the player's folder stays) |
| cook | `cook_plan` | gameId | `CookJob[] {id, name, folder, identity, reason?, last?}` (dev.27, `kiln_manage::cook::plan`): the enabled Kiln mods of the profile the game uses that have something to cook (a `*/cook.json`, or blocks), by the folders the game's `kiln/profile.json` names. `reason`: why it needs a cook now (`notCooked`: installed or switched on, never cooked for this game; `modChanged`: its identity changed, it was updated; `gameChanged`: the game's build changed; `lastFailed`; `filesGone`: a file its last cook made is gone or changed, by its receipt), absent when it is cooked. `last`: its record in `kiln/state/cooked.json` (`{identity, gameBuild, at, ok, files, error?}`). Reads only: no tool is started |
| cook | `cook_run` | gameId, modId: string \| null | `TaskId` -> `CookRun {mods: [{id, name, reason, ok, error?}], activation?, bootSnapshot?}` (dev.27, `kiln_manage::cook::cook_due`): under the game's lock (`kiln/state/cook.lock`: no start and no other cook of the game meanwhile), first writes the active profile again when it changed since its activation (`profile::refresh_active`; `activation`: that activation, for its notices), then cooks the due mods one after the other with the cook tools the launcher carries (`cooktools::locate`: unpacked once under `<data>/cooktools`; Python is the bundle's `python/python.exe` on Windows, the system's `python3` on Linux), each from nothing (its `assets/cooked` and its receipts set aside, every recipe run whatever its own check says, and put back when the cook does not end well; a Library entry another game loads is skipped while that game runs or was just started) into its own `assets/cooked`, and records each in `kiln/state/cooked.json`; the tools write the receipts (`kiln/state/cook-receipts`). With `modId`: that one mod whatever its record says ("Cook again", `reason: "asked"`). Each tool runs within limits (30 min a recipe, 10 min without a line, 2 min a check) and is stopped with every process it started when it passes one; the task's cancel stops it the same way (task `canceled`), and so does the game being started meanwhile (`game_running`). Task progress: "Cooking <name> (i of n)", then each line the tools print (lines cut at 4096 bytes, the error text from the last 256 KiB). A mod's failed cook is in the result (`ok: false`, `error`: the tools' last lines), never the task's error: the others are cooked. When no tool can run (no Python 3.10+, no zstd), every due mod is `ok: false` with the one sentence that says what to install. Refused (`game_running`) while the game runs, and with `modId` in the 2 minutes after a start through the launcher; `busy` when another cook or a start holds the lock for 10 s; `not_found` for a `modId` with nothing to cook Without `modId` (Play) it then makes the boot game-data snapshot (dev.28, `kiln_manage::boot_snapshot`, [boot-snapshot.md](../modding/boot-snapshot.md)) when an enabled Kiln mod of the profile has `"boot": true` or `"required"`: `bootSnapshot` = `{status: made \| reused \| skipped \| refused \| failed \| unavailable, key?, path?, counts?, emptyKinds, skipped?, archive?, bytes?, seconds?, reason?}`; `skipped` ("no mod needs the boot snapshot") runs nothing and leaves a snapshot that is there; task progress "Game data for OnBoot mods (about 200 MB, once per game build): …"; stopped after 10 minutes or by the task's cancel; never the task's error |
| cook | `boot_snapshot_run` | gameId, profileId: string | `TaskId` -> `BootSnapshot` (dev.28, `kiln_manage::boot_snapshot::boot_mods_once`): Play once with another profile (launch mode `once`), before `launch_play`: the boot game-data step `cook_run` runs after Play's cook, for the mods of that single start: the profile's enabled Kiln mods that have `"boot": true` or `"required"` (read from their Library entries: the game loads that entry or Kiln's copy of it), and, when the profile loads Kiln drop-ins, each such drop-in folder it does not list. Same result, progress lines, limit and cancel as `cook_run`'s `bootSnapshot`; `skipped` ("no mod needs the boot snapshot") runs nothing. Cooks nothing. Refused while the game runs (`game_running`); `not_found` for an unknown profile; a producer that fails is in the result, never the task's error |

| data_folder | `data_folder_get` | - | `DataFolder` (answers in every state; with a broken pointer `path` is the default place and `problem` is set; while an unfinished move's partial copy is removed `undoing` is set and `notice` comes after it; `pending`: why a move cannot start now) |
| data_folder | `data_folder_size` | - | `{bytes, files}` (`data_folder::inventory`: what a move copies) |
| data_folder | `data_folder_check` | path (the folder the player picked) | `DataFolderPlan` (`data_folder::check_here`; a refusal is `action: "refuse"` with the crate's `reason` and `code`; a task that runs is the refusal `busy`, "Kiln is still working on: <title>.") |
| data_folder | `data_folder_move` | path (picked; the crate checks it again) | `TaskId` -> `DataFolderMoved` (`data_folder_move::move_to` with the process's lock; refused while a task runs, and in a build with Nexus on while another launcher holds `nexus/instance.lock` (one before dev.26 takes no data-folder lock); the file-hash cache stops writing for the move (`filecache::unpersist`) and writes again after a failed or canceled one; the last phase, task message "removing the old copy: <file>", removes the old copy: a cancel there ends the task as done with `cleanup.interrupted`, the rest goes at the next start; when a game's profile could not be written for the new place the removal does not run and `cleanup.reason` names the game; a failed or canceled task left the old place in use, its error names what is left in the target; the restart follows) |
| data_folder | `data_folder_use` | path (picked) | `DataFolder` of the folder now named (`use_folder`; with a broken pointer `repoint`); the restart follows |
| data_folder | `data_folder_reset` | - | `DataFolder` at the default place (`reset`; only with a broken pointer); the restart follows |
| data_folder | `data_folder_notice_ack` | - | null, never fails (the notice is gone for this process; the move's journal is acknowledged only for a finished removal; an unclear record stays until its committed folder is in use again; a removal that stopped or was refused, and an unfinished move's partial copy that could not be removed, keep it for the next start) |
| data_folder | `data_folder_restart` | - | does not return (the watcher stops, the lock goes, Tauri's `restart()`) |
| vendor | `vendor_status` | gameId | `VendorStatus {folder: "kiln/vendor", components: ComponentStatus[]}` (dev.29; section 5.15; dev.29.18 dropped `preview`): per component of the manifest `{id, vendor, vendorName, name, enables, version, source?, license: {name, sha256, from, agreed, agreedAt?}, hosts, size, files: {name, size, pinned}[], state: "notDownloaded"\|"downloaded"\|"updateAvailable"\|"unavailable"\|"damaged", installed?: {version, folder, sizeOnDisk, installed, problems?}, partial (bytes of an interrupted download), reason?}`, from the receipts and the files' sizes and SHA-256 (a hash is kept for the launcher's run while the file's size and modification time stay). `unsupported` for a server |
| vendor | `vendor_license` | componentId | `LicenseText {component, vendor, name, text (the bundled text in full), sha256, from, agreed}`; nothing is fetched |
| vendor | `vendor_download` | gameId, componentId, licenseSha256: string \| null | `TaskId` -> `DownloadReport {receipt, folder, fetched, kept, replaced, warnings}`. `licenseSha256`: the hash of the licence text the dialog showed, on the vendor's first download of that text; it is recorded before anything is fetched. Refused (before the task) for a server, a component not pinned, without the agreement (`consent_required`), while the game runs (dev.29.18: no preview needed) |
| vendor | `vendor_remove` | gameId, componentId | `RemoveReport {removed: string[] (folders, relative to the game), freed (bytes), kept: string[] (what stayed, one sentence each)}`; refused while the game runs |
| updates | `updates_check` | request?: `{offline?, refresh?, automatic?, providers?, source?}`; `providers`: the providers asked (`"github"`, `"nexus"`; absent: all), the others answer from what is remembered with no request; `source`: `{action: "map", kind, id, spec, confirm?}` (spec: the manifest's `updates` shape), `{action: "confirm", kind, id}` or `{action: "unmap", kind, id}` | `CheckReport {format, checkedAt, offline, mods: ModUpdateStatus[]}` (dev.28; the document of `kiln updates check --json`, [../modding/updates.md](../modding/updates.md)); also emitted as `kiln://mod-updates`. The source change is applied first: a mapping to another place than the mod is checked against is `conflict` without `confirm`, a spec the manifest's rules refuse is `invalid_argument`. `offline`: no request to any provider. Nothing is downloaded, installed or switched |
`ImportReport {imported: LibraryRef[], existing: LibraryRef[], skipped: {path, reason}[], notes: {path, text}[], names:
{"<kind>/<id>": string}}`, `AddReport {import: ImportReport, profileId, profileName, created (My mods made now), added:
AddedMod[], already: AddedMod[], changes: ProfileChange[], applied (profile.json written now), running (the game runs:
applies at the next start), activation, missing: {id, range, by}[], modpacks: string[], resumed (the profile the game had
before Kiln was reinstalled is active again), libraryOnly: string | null (why the mods are in the Library only)}`, `AddedMod {kind, id, version, hash, name}`,
`ProfileChange {kind, before: ProfileMod | null, after: ProfileMod}` (`kiln_manage::quick`).

Launcher-only types (Rust in `launcher/src-tauri/src/commands/app.rs` and `launch.rs`): `AppInfo` (`noWebgl`:
KILN_LAUNCHER_NO_WEBGL=1 for this start, or the launcher draws without the GPU; `render` (Linux only): `RenderInfo {mode:
"gpu"|"software", why: "default"|"marker"|"restart"|"environment"|"argument", marker: boolean, markerWhy?: string}`
(`src-tauri/src/render.rs`); `sandbox` (Linux only, dev.29.18): `SandboxInfo {want: "on"|"off", why:
"default"|"marker"|"restart"|"environment"|"argument", marker: boolean, markerWhy?: string, webkit: "on"|"off"|"unused"
(what WebKit does in this start: its GTK 3 API runs the sandbox only with WEBKIT_FORCE_SANDBOX=1), webkitWhy: string,
yours?: string (the player's own WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS, which decides), probe:
"works"|"fails"|"missing"|"unknown" (the probe of the bwrap WebKit runs), probeSays: string, probeFailed: boolean (bwrap
cannot start, and it says something about this system: not the AppImage's own missing bwrap while the sandbox is not
used)}` (`src-tauri/src/sandbox.rs`);
`distribution`: "direct" for the frostdev.io/GitHub build that updates itself,
"nexus" for the Nexus Mods build, which has no self-update and whose UI shows only a link to the Kiln page there
(`src-tauri/src/distribution.rs`; absent from an older backend: direct); `preview` (dev.29): the previews on for this
start, from settings.json `preview` and `KILN_LAUNCHER_PREVIEW` (names separated by commas or spaces), left out when
none is on (`src-tauri/src/commands/vendor.rs`; no feature is behind one in 0.2.0-dev.29.18: `"upscalers"` ended with phase B)), `LauncherSettings {steamRoots: string[], defaultGameId: string|null,
allowHttpModpacks: boolean, savesKeep: number, updateCheck: boolean (default true), updateChannel?: "stable"|"dev"
(absent: the running build's channel), animatedFire: boolean (default true), addToActiveProfile: boolean (default true;
a dropped mod goes through `library_add`; off: `library_import` only), uiMode?: "auto"|"deck"|"desktop" (the UI to start
in; absent or auto: the Deck UI when `AppInfo.deck.detected`; [deck.md](deck.md)), modFolderNotes: boolean (dev.26,
default true: "Show notes about mod folders"; off: an activation raises none of its notices except the first of a folder
that may still run although its mod is off; [app.md](app.md) "Notices about mod folders"), modUpdatesDaily: boolean
(dev.28, default false: "Check for mod updates once a day"; [catalog.md](catalog.md) "Update notices"), keepVersions:
number (dev.29, default 1, 0 to 5: "Earlier versions to keep", how many of a mod's earlier versions an update keeps for a
roll back with their settings; `app_settings_set` refuses more than 5 and trims what is kept to the new count; a
launcher before dev.29 drops the field when it saves its settings, and dev.29 then keeps 1; [catalog.md](catalog.md)
"Rolling back an update"), language?: string (dev.29: the launcher's language, "system" or absent = the system's,
else a tag such as "zh-CN"; [translating.md](../translating.md)), preview?: string[] (dev.29: previews of unfinished
features by name, set by hand with the launcher closed and kept by every save; none is used in 0.2.0-dev.29.18 (the
upscaler files are out of preview); left out while empty)}` (`checkUpdates`, never used, is gone;
readers ignore it), `UpdateStatus` (`launcher/src-tauri/src/selfupdate/mod.rs`; [releasing.md](releasing.md); `distribution`
"direct", or in the Nexus build "nexus" with `phase: "disabled"`, `commands/update.rs`),
`Task {id, kind (the command), title, state: 'queued'|'running'|'done'|'failed'|'canceled', progress: number|null,
message: string|null, started, finished: string|null, result?, error?: KilnError}`, `BootState {boot: BootJson|null,
mods: ModsJson|null, native: NativeJson|null, stale: boolean, log: {errors: number, first: string[]}|null}` (`log`:
kiln.log's `[E` lines of the run that wrote boot.json).

The data folder (`launcher/src-tauri/src/commands/data_folder.rs`, `folder.rs`; the crate's side in section 3):
`DataFolder {path, defaultPath, source: "env"|"pointer"|"default", problem: {path, reason, kind}|null, notice:
DataFolderNotice|null, elsewhere: boolean, undoing: {task: string|null, folder}|null, pending: string|null}`
(`elsewhere`: another launcher holds the folder's lock exclusively, it is moving it; `undoing`: this start removes an
unfinished move's partial copy at `folder`, the task "Remove the unfinished copy" (kind `data_folder_undo`; null for
the moment before it starts; its result is the crate's `Cleanup`, `remove_partial_copy_with`); `pending`: an earlier move is not finished, the crate would refuse a move with
`move_pending`, in words), `DataFolderEntry {name, bytes, path}`, `DataFolderPlan {picked, target, action: "move"|"use"|"refuse",
reason, code (RefusalCode, snake_case), bytes, files, needed, free, sameVolume, hardLinks, copyMode: string[] (games by
name that would run in copy mode from there and do not now), stays: DataFolderEntry[] (entries the launcher does not
know: copied, and left in the old folder), warnings: string[]}`, `DataFolderNotice {kind: "moved"|"undone"|"unclear",
from, to, left: string[], stayed: DataFolderEntry[], kept: {path, bytes, reason}[], removing, unfinished,
folderRemoved, reason}`, `DataFolderMoved {from, to, bytes, files, warnings, stayed: DataFolderEntry[], cleanup: {done,
interrupted, folderRemoved, kept, reason}|null}`. While the pointer is broken only `app_info`, `data_folder_get`, `_use`, `_reset`, `_restart`
and `deck_quit` answer, every other command rejects with `data_folder`; while another launcher moves the folder only
`app_info`, `data_folder_get`, `_restart` and `deck_quit` (the others: `busy`); while this one moves it or has moved it,
or removes an unfinished copy, only `app_info`, `task_*`, `data_folder_get`, `_restart`, `deck_quit`,
`deck_fullscreen`, `deck_gamepad`, `app_open_path` and `app_open_url` (the others: `busy`). New tasks fail at once
with `busy` then, except the move, the removal of an old copy and the removal of an unfinished copy.
`app_started` (the page's ready call for the render watchdog, which writes nothing) answers in all three states, so a
window that shows only the data folder's dialog is not taken for a black one. `app_close_seen` and `app_close_unsaved`
(the close path, writing nothing) answer in every state too, so closing the window never waits for the folder.
`app_render_frame` (the renderer keeps its
startup state in the data folder) is refused in all three: the page sends it only once the data folder gate lets the
launcher mount, and the backend writes no renderer state into a held folder.

`--selftest`: the binary runs headless, exercises the Rust side (data dir, crate calls with temp
folders) and exits 0/1; used under Wine for the NSIS build. Its report has `distribution` ("direct" or "nexus"; also
the line `distribution: <d>` on stdout), and its `self_update` check names it.

`DeckInfo` (`launcher/src-tauri/src/deck/detect.rs`): `{detected: boolean, signals: string[] ("SteamDeck=1", "SteamOS=1",
"XDG_CURRENT_DESKTOP=gamescope", "GAMESCOPE_WAYLAND_DISPLAY", "dmi:Jupiter" | "dmi:Galileo", "os-release:steamos"), gamescope:
boolean, device: "jupiter"|"galileo"|null, forced: "deck"|"desktop"|null}` (`forced`: `--deck` / `--desktop` on the command
line, the last one wins, else `KILN_LAUNCHER_UI=deck|desktop`). The UI: `forced`, else `uiMode` when not auto, else
`detected`, else the desktop UI ([deck.md](deck.md)).

| community | `community_settings_get` | - | `CommunitySettings` {format, shown, presence, notify, relay?} (`<data>/community.json`; local) |
| community | `community_settings_set` | patch: {shown?, presence?, notify?} | `CommunitySettings` |
| community | `community_status` | - | `CommunityStatus` {shown, account: {id, name, member, tester} \| null, online, relay: "ok"\|"down", presence: {on, discordRunning, showing}, notify, unread, postsAsOf} (local; no request) |
| community | `community_feed` | - | `CommunityFeed` {releases: {title, date, line, url}[], events: {title, start, end?, where, url}[]} (public; also keeps the known-problems list) |
| community | `community_check_member` | - | account \| null (`GET /me`) |
| community | `community_signout` | forget: boolean, posts?: boolean | null (the key goes from this PC whatever the relay says; forget reports a relay that did not hear it) |
| community | `community_signin_start` | mode: "browser"\|"qr", join: boolean, texts?: {done, failed} | `SignInStart` {id, code, url, expiresAt} |
| community | `community_signin_poll` | id | {state: "waiting"} \| {state: "approved", account} \| {state: "denied"\|"expired"\|"canceled"} |
| community | `community_signin_cancel` | id | null |
| community | `community_posts` | - | `SupportPost[]` ([discord.md](discord.md)) |
| community | `community_post_seen` / `community_post_solved` / `community_post_delete` | id | null |
| community | `community_presence_facts` | mods: number \| null, texts: PresenceTexts, notify?: {title, detail, open} | null (what only the window knows: the mod count, the words in the launcher's language) |
| community | `community_report_prepare` | target: LogTarget \| null, from: "logs"\|"banner"\|"crash"\|"community"\|"deck", exclude?: string[] (dev.29.22) | `ReportPreview` {prepared, findings, versions, tags, kindTags, size, limit, files, known, contents: `ReportContents`} (builds the support report once; nothing is sent) |
| community | `community_report_send` | prepared, draft: {title, kind, when?, text, zip: "team"\|"public"\|"none"} | `SupportPost` (sends the prepared report as shown) |
| community | `tester_status` | - | `TesterStatus` {tester, beta, hardware: {gpu, driver, vram, os, runner, caps, covers}, plan: {build, url, until, steps, hidden, sent} \| null} |
| community | `tester_join` | beta: boolean | null (sends the hardware tester_status showed) |
| community | `tester_leave` | - | null |
| community | `tester_result_set` | step, result?: "worked"\|"failed"\|"skipped"\|"clear", note? | plan \| null (kept on this PC) |
| community | `tester_results_send` | withReport: boolean, prepared?: string (the report `community_report_prepare` showed) | plan \| null |
| community | `community_steam_keyboard` | - | null (a Steam Deck only: `steam://open/keyboard`) |
| pack | `pack_inspect` | source: {path, sha256?} (a local .kilnpack; no URL) | `PackPreview` ([crucible.md](crucible.md)) |
| pack | `pack_install` | gameId?, request: `pack::InstallRequest` (local source, consent) | TaskId -> `pack::InstallResult` |
| pack | `pack_instance` | profileId | `Instance` \| null |
| pack | `pack_instances` | - | [profileId, `Instance`][] |
| pack | `pack_lacking` | profileId | `pack::Lacking` {version, missing, names, download}: what the profile loads (listed with the pack's hash, on) of its installed version and the Library lacks; read-only and cheap (the Library's records and its own changed check, as its list reads them; only the pack layer is hashed in full) |
| pack | `pack_update_plan` | profileId, source (local), optional? | `UpdatePlan` |
| pack | `pack_update` | request: `pack::UpdateRequest` (local source) | TaskId -> `UpdateResult` (error `conflict`, plan in detail) |
| pack | `pack_undo` | profileId | `Profile` |
| pack | `pack_uninstall` | profileId | `UninstallResult` {profile, unused} |
| pack | `pack_build` | request: `BuildRequest`, outDir (absolute) | TaskId -> `BuiltPack` ("Save as file") |
| pack | `pack_verify` | path, sha256? | `PackIndex` |
| pack | `pack_diff` | oldPath, newPath | `PackDiff` |
| pack | `pack_secret_scan` | file, text | string[] |
| pack | `pack_peek_format` | path | 1 (server modpack) \| 2 (Crucible pack) |
| pack | `pack_local_files` | profileId | `LocalFile[]` `{path, text}` (the player layer's data files) |
| pack | `pack_local_write` | profileId, path (`content/<name>.json`), text \| null (removes) | null |
| pack | `pack_local_save` | profileId | `Profile` (the player layer into the Library, last; out when empty) |
| pack | `pack_check` | path | `CheckReport` (the pack analysis of a file; the preview shows it) |
| crucible | `crucible_status` | - | `{enabled, dev, api, signedIn, account: {id, name} \| null}` (enabled false: no Crucible surface) |
| crucible | `crucible_open` | gameId?, target (code, share link, pack link, kiln:// link, version id) | `crucible::Preview`; a pack still uploading: `busy` with `{uploading: {version, progress}, pack}` |
| crucible | `crucible_install` | gameId?, request: {target (version id), consent, trustAlways?, optional?, name?, play?} | TaskId -> `crucible::InstallResult` {profile, instance, imported, overrides, previous, downloaded} |
| crucible | `crucible_updates` | gameId?, force? | `UpdateAvailable[]` (cached 5 minutes) |
| crucible | `crucible_update_plan` | profileId | `{versionId, download, plan: UpdatePlan}` (downloads what it needs) |
| crucible | `crucible_update` | gameId?, request: {profile, versionId, consent, optional?, resolve?, all?} | TaskId -> `UpdateResult` |
| crucible | `crucible_repair` | gameId?, request: {profile, path?} | TaskId -> `pack::RepairResult` {profile, version, imported, downloaded, left}: "Download missing mods", the `pack_lacking` parts of the installed version from Crucible (path: from that version's pack file); writes only the Library (no profile, version or setting change, no activation; allowed while the game runs) |
| crucible | `crucible_share` | gameId?, request: {profile, title?, overrides?} | TaskId -> `ShareResult` {pack, version, files, uploaded, skipped}; signed out: `auth_failed` |
| crucible | `crucible_shared` | profileId | `SharedState` {profile, pack, version, changed: {added, updated, removed} \| null, pending} \| null |
| crucible | `crucible_trust` | - | `Trust[]` {id, name, since} |
| crucible | `crucible_trust_set` | id, name, trusted | null |
| crucible | `crucible_links` | - | string[] (kiln:// links that arrived; drained) |
| crucible | `crucible_resume` | - | `{firstStart, code, packWaiting}` (asked once at start: come back after installing Kiln; only `packWaiting` lets the start read the clipboard) |
| crucible | `crucible_clipboard` | - | `string \| null`: the clipboard's text, read by the backend (arboard; never `navigator.clipboard`, which makes WebView2 ask the player), only when it may be a Crucible code or link (Got a code?, its Paste, a waiting pack); never logged |
| studio | `studio_list` | - | `StudioSummary[]` ([pack-studio.md](pack-studio.md)) |
| studio | `studio_create` | request: {name, from?, id?} | `Studio` {dir, source, mods, credits, profileName, layerFiles} |
| studio | `studio_get` / `studio_delete` | packId (, gameId? for delete: a loaded test instance goes back to the pack's profile) | `Studio` / null |
| studio | `studio_save` | packId, source: `PackSource` | `Studio` |
| studio | `studio_credits_set` | packId, credits: {id: {author, source, match?}} | `Studio` |
| studio | `studio_capture` | packId, gameId? | `CaptureFile[]` {path, mod?, mode, defaults, keys: {key, value, default?, chosen, personal, secret?}[], whole, side, size, refused?} |
| studio | `studio_capture_set` | packId, choices: {path, side, keys, whole?}[] | `Studio` |
| studio | `studio_order` | packId | `StudioOrder` {kiln: {id, name, loads, reason?, layer}[], late, warnings} |
| studio | `studio_layer_files` / `studio_layer_read` | packId (, path) | `LayerFile[]` / string |
| studio | `studio_layer_write` / `studio_layer_delete` / `studio_layer_set` / `studio_layer_lint` | packId, path, text / path / section, selector, field, value | `LintReport` of the layer |
| studio | `studio_pick` | packId, section, query, limit? | `PickHit[]` {section, name, id?, displayName?, from?} |
| studio | `studio_fields` | section | catalog `FieldInfo[]` |
| studio | `studio_categories` | - | `ItemCategory[]` {id, name?, items: {name, displayName?, hidden?}[], recipes: {name, displayName?}[]}: the catalog's recipe input categories, their items' display names and the game's recipes that take each (the recipe editor's "Any of" input); empty without a catalog |
| studio | `studio_analyze` | packId | TaskId -> `CheckReport` (modpack::check) |
| studio | `studio_test` | packId, gameId? | TaskId -> `pack::InstallResult` ("<name> (test)") |
| studio | `studio_test_report` / `studio_test_end` | packId (, gameId?) | `TestReport` {profile?, ran, conflicts, problems} / null |
| studio | `studio_build` | packId, outDir (absolute) | TaskId -> `BuiltPack` |
| studio | `studio_publish_check` | packId, request: `PublishRequest` | string[] (what is missing; sends nothing) |
| studio | `studio_publish` | packId, request: {version, changes?, visibility, attest, gameBuilds?, fresh?}, gameId? | TaskId -> `PublishResult` {pack, version, files, uploaded, skipped, public, needs, file}; signed out: `auth_failed`; a pack gone on Crucible: `refused` with detail.gone |
| studio | `studio_set_visibility` | packId, visibility, attest | `{pack, public, needs}` (Make public / link only, no new version) |
| studio | `studio_server_pack` | packId, version, serverId | TaskId -> `ServerInstallResult` (the version's server part + the player modpack) |

The community commands ([discord.md](discord.md)) refuse every request that would leave the PC while
`community.json`'s `shown` is false (`refused`, `detail.kind` "hidden"); their errors carry `detail.kind` (`offline`,
`relay`, `signin`, `not_member`, `too_young`, `rate_limited` with `limit` "hour"\|"day"\|"network", `too_big`,
`not_found`, ...), which the UI words.

Official launcher builds (`KILN_RELEASE_BUILD` set) require valid public Nexus OAuth registration when Nexus is enabled.
`KILN_NEXUS_ENABLED=0` disables the entire integration; absent/`1` enables it, other values fail the build.
`app_info.nexusEnabled` is the backend capability for both UIs. Disabled `nexus_*` commands return `unsupported`
before storage/provider/handler access; direct nxm callbacks exit without side effects.
The headless `--selftest` report adds `releaseBuild` and `nexusOAuth {enabled, configured, clientId, redirectUri}` from the
actual compiled configuration. Unconfigured development builds pass selftest with `configured: false`; release
report checks reject enabled unconfigured variants and accept explicit disabled official variants. This metadata
proves configuration, not Nexus approval or a working provider session.

Catalog types (`kiln_manage::nexus`, `launcher/src/api/nexus.ts`; [catalog.md](catalog.md)): `CatalogQuery {text, filter:
'kiln'|'all'|'installed' (default kiln), sort: 'endorsements'|'updated'|'new'|'downloads'|'name', page (1-based),
pageSize (24, at most 100), adult, refresh}`, `CatalogPage {mods: CatalogMod[], total, page, pageSize, unchecked, needsSignIn,
fetched}`, `CatalogMod {id (game-scoped), uid, name, summary, version, author, category, thumbnail, endorsements,
downloads, created, updated, adult, kind: 'kiln'|'native'|'emf'|'dll'|'modpack'|'other'|null (null: not looked at),
url, installed: InstalledInfo | null}`, `InstalledInfo {version, versionGsid, fileName, inLibrary, latestVersion,
latestGsid, updateAvailable}`, `CatalogDetails = CatalogMod + {description (plain text), credit, tags, uniqueDownloads,
images: {url, thumb, title}[], files: CatalogFile[], bestFile, requirements: Requirement[], filesUrl, needsSignIn}`,
`CatalogFile {fileId, fileName, versionId, versionGsid, version, category, uploaded, primary, kind, archive}`,
`Requirement {modId, name, fileName, minVersion, installed, kiln (Kiln itself), otherGame, url}`, `InstallReport {add:
AddReport (Undo: profile_revert with its profileId and changes), modId, name, version, fileName, check: {files,
matched, hashed, noManifest, sha256, size}, requirements, download}`, `InstalledCatalogMod = InstallRecord {modId, name,
fileId, fileName, versionId, versionGsid, version, refs: LibraryRef[], installed, source: 'nxm'|'catalog'} + {info:
InstalledInfo}`, `NexusAccount {configured, signedIn, tokenPlace: 'keyring'|'file'|null, name, premium, error: KilnError|null, rate:
{hourlyRemaining, dailyRemaining, blockedFor, calls}}`.

Command line: `kiln-launcher --nxm <link>` (what Kiln registers) or a bare `nxm://` argument: an Enshrouded link is
queued in `<data>/nexus/inbox/` and handed to the launcher that runs (this process exits), or this process starts with
it (held in memory, `kiln://nxm` once the data folder's gates open); another game's well-formed link goes to the handler
before Kiln and the process exits. URL invocations accept exactly `--nxm <link>` or one bare link, without additional
launcher options; malformed invocations exit 1.

`kiln-launcher --link <kiln://crucible/...>` (what Kiln registers for kiln:// while Crucible is on) or a bare `kiln://`
argument: the link is queued in `<data>/crucible/inbox/` and handed to the launcher that runs (this process exits), or
this process starts with it (held in memory, `kiln://crucible-link` once the gates open); the same strict argument shape
as nxm.

## 10. Events
Emitted with `app.emit(name, payload)`; `launcher/src/api/commands.ts` exports `KilnEvents` with the payload types.

| Event | Payload |
|---|---|
| `kiln://task` | `Task` (the whole task at every change) |
| `kiln://game` | `{gameId, kind: 'started'\|'stopped'\|'boot'\|'mods'\|'native'\|'status'\|'progress', pid: number\|null, status: GameStatus\|null}` (`progress`: the running game wrote `kiln/state/progress.json`, at most once a second, without a status) |
| `kiln://log` | `{subscription, target: LogTarget, file, lines: string[], next: number}` |
| `kiln://last-server` | `{gameId, lastServer: LastServer}` |
| `kiln://server` | `{serverId, status: ServerStatus}` |
| `kiln://admin` | `{session, serverId, event: {kind: 'push', topic, seq, data} \| {kind: 'closed', reason, retry} \| {kind: 'error', message} \| {kind: 'ready', transport, ops, topics}}` (`ready` after every (re)connect; `closed.retry`: the hub reconnects) |
| `kiln://update` | `UpdateStatus` (at every change of the self-update: check, download progress, ready, skip; never in the Nexus Mods build) |
| `kiln://console` | `{subscription, serverId, reset: boolean, source, path: string \| null, truncated, lines: ConsoleLine[]}` (`reset`: `lines` replace everything shown: a start, a stop, a start outside the launcher) |
| `kiln://gamepad` | `{kind: 'button', button: 'a'\|'b'\|'x'\|'y'\|'lb'\|'rb'\|'lt'\|'rt'\|'select'\|'start'\|'guide'\|'ls'\|'rs'\|'up'\|'down'\|'left'\|'right', pressed, repeat, device} \| {kind: 'axis', axis: 'rx'\|'ry', value: -1..1, device} \| {kind: 'connected', device, name, vendor, product} \| {kind: 'disconnected', device, name}` (the Deck UI's native reader while `deck_gamepad(true)`: directions merge the D-pad and the left stick and repeat while held (`repeat: true`); the right stick is sent for scrolling, at most ~25 a second, then one 0; [deck.md](deck.md)) |
| `kiln://nxm` | `{count}` (an Enshrouded Mod Manager Download link arrived; `count` links wait in `nexus_links`; the window comes to the front) |
| `kiln://crucible-link` | `{count}` (a kiln:// Crucible link arrived; `crucible_links` hands them over; the window comes to the front) |
| `kiln://mod-updates` | `CheckReport` (the result of every `updates_check`, also the daily one) |
| `kiln://community` | `{kind: 'changed'\|'reply', post?, title?, url?, by?}` (the Discord features: something the Community page shows changed, or Kiln support replied to a post; [discord.md](discord.md)) |
| `kiln://activation` | `{gameId}` (a profile was written again without `profile_activate`: after `profile_save`, `profile_revert` or `chain_set_late`, a change of `profile.json` outside the launcher, the watcher's start-up pass, `profile_ensure_default`; its `Activation` waits until the window takes it with `activation_notices`, which the launcher also calls once its listener is up) |

## 11. Errors
Every command rejects with `KilnError = {code, message, detail?}` (`kiln_manage::Error`, serialized as is). `message`
is one sentence for the user; `detail` is anything that helps (paths, OS error text, a list).

| Code | When |
|---|---|
| `not_implemented` | a stub |
| `invalid_argument` | bad input (id format, empty path) |
| `not_found` | no such game, profile, mod, file, server, session |
| `already_exists` | create over an existing thing |
| `io` | file system error |
| `parse` | a file could not be parsed |
| `schema` | parsed but broke the format's rules |
| `conflict` | stale generation, concurrent change, library entry still in use |
| `game_running` | the game runs (install, activation, restore, launch) |
| `steam_updating` | Steam updates or validates the game |
| `permission` | the OS refused access. (dev.29.18) An install, update, Repair or rollback that Windows refused a DLL or exe of (access denied, os error 5; a sharing violation, os error 32, and Windows Security's virus errors 225 and 226 come as `io`) has `detail.blocked: {path, osError}` (game-relative path): an antivirus or another program may be blocking it (`blocked`) |
| `unsupported` | not possible here (platform, setup, untranslatable path) |
| `network` | download / TCP / SSH failure |
| `hash_mismatch` | a sha256 or kdh1 did not match |
| `consent_required` | remote code without the user's yes |
| `refused` | refused on purpose (ShroudForge proxy, http URL, a symlink in a mod folder) |
| `timeout` | waited too long |
| `canceled` | the user canceled the task |
| `busy` | too many tasks/requests in flight |
| `auth_failed` | KAP/1 handshake refused |
| `admin_unavailable` | no admin transport reaches the server |
| `op_failed` | an admin op answered ok=false (message = its error) |
| `internal` | a bug |
| `data_folder` | the data folder's pointer cannot be used (section 3; `detail: {kind, pointer, path}`), or a moved data folder does not read as the old one |

## 12. Crate `kiln-manage` modules
Features: default = none (serde, serde_json, sha2, unicode-normalization); `zip`, `http` (ureq, rustls), `admin`
(hmac, getrandom), `jsonc` (jsonc-parser CST), `saves` (zstd), `server` (toml), `gameart` (zstd, bcdec_rs, image-webp,
png, flate2), `full` = all. kilnctl uses the
default; the launcher uses `full`. Builds for Linux and `x86_64-pc-windows-msvc` (`cargo xwin check -p kiln-manage
--target x86_64-pc-windows-msvc [--all-features]`).

| Module | Public items (signatures in the source) |
|---|---|
| `error` | `Error {code, message, detail}`, `ErrorCode`, `Result<T>`, `Error::{new, not_implemented, with_detail, invalid, not_found}` |
| `task` | `trait Progress { report(Option<f32>, &str); canceled() }`, `NoProgress` |
| `paths` | `DataDirs::{at, locate, library, profiles, installs, runtime, schema_cache, backups, logs, settings, library_entry, profile}`, `DataEnv::{from_process, default_root, locate, locate_checked, home_dir, state_dir, config_dir}`, `Located {dirs, source, default_root}`, `RootSource {Env, Pointer, Default}`, `LocateProblem::into_error`, `ProblemKind`, `paths::game::*` constants |
| `data_folder` | `recognize(&Path, &DataEnv) -> Result<FolderState>`, `inventory(&Path, &DataEnv) -> Result<Inventory>`, `own_entries`, `foreign_entries`, `is_top_lock`, `check(&CheckInput, &dyn SystemFacts) -> Result<Plan, Refusal>`, `check_here`, `target_rule`, `check_form`, `relation_to_current`, `game_path_too_long`, `needed_bytes`, `space_at`, `written_in_place`, `is_own_file`, `canonical`, `known_servers`, `read_pointer`, `write_pointer`, `remove_pointer`, `check_pointed`, `DataLock::{shared, exclusive, upgrade, downgrade, is_exclusive, path}`; `FolderState`, `Evidence`, `Inventory`, `EntrySize`, `LongestPath`, `Plan`, `Action`, `Refusal`, `RefusalCode`, `Target`, `CheckInput`, `SystemFacts`, `Facts`, `RealFacts`, `Space`, `Pointer`, `PathParts`, `PathRoot`, `PathStyle`; constants `POINTER`, `POINTER_OLD`, `JOURNAL`, `FILE_RECORD`, `MARKER`, `MOVE_MARKER`, `LOCK`, `KNOWN_ENTRIES`, `HASH_CACHE`, `WINDOWS_MAX_PATH`, `MARGIN_BYTES` |
| `data_folder_move` | `move_to(&Located, &DataEnv, &Path, &dyn SystemFacts, Option<&mut DataLock>, &MoveOptions, &mut dyn Progress) -> Result<MoveResult>`, `use_folder(&Located, &DataEnv, &Path, &dyn SystemFacts, Option<&mut DataLock>) -> Result<Located>`, `reset(&DataEnv) -> Result<Located>`, `repoint(&DataEnv, &Path, &dyn SystemFacts) -> Result<Located>`, `pending(&DataEnv) -> Result<Pending>`, `remove_old_copy(&DataEnv, &mut dyn Progress) -> Result<Cleanup>`, `remove_partial_copy(&DataEnv) -> Result<Cleanup>`, `remove_partial_copy_with(&DataEnv, &mut dyn Progress) -> Result<Cleanup>`, `partial_reason`, `acknowledge(&DataEnv)`, `read_journal`, `MAX_ROUNDS`; `Journal`, `JournalEntry`, `FileRecord`, `RecordedFile`, `Rewrite`, `MoveOptions`, `MoveResult`, `Stayed`, `Pending`, `Cleanup`, `Kept` |
| `hash` | `dirhash(&Path) -> Result<String>`, `dirhash_records(impl IntoIterator<Item = Record>)`, `sha256_file`, `sha256_hex`, `hex`, `hash8`, `fingerprint(entries) -> String`, `Record`, `FingerprintEntry` |
| `vdf` | `parse(&str) -> Result<Vdf>`, `Vdf::{get, path, as_str, entries}` |
| `steam` | `roots()`, `libraries(&SteamRoot)`, `app_manifest(&Path, u32)`, `compat_tool`, `launch_options`, `compatdata`; `SteamRoot`, `SteamFlavor`, `Library`, `AppManifest`, `LaunchOptions` |
| `game` | `detect()`, `inspect(&Path)`, `known(&DataDirs)`, `add`, `add_with`, `remove`, `status`, `path_mode`, (dev.29.17) `set_start`, `sync_start`, `apply_start`, `add_on`, `set_start_on`, `apply_start_on`, `asi_loader_chained`; `GameInstall`, `GameKind`, `Platform`, `PathMode`, `GameStatus` (+ `otherFolderProfile`, `start`, `build`, (dev.29.18) `graphicsTools`, `graphicsStepAside`, `graphicsState`), (dev.29.18) `graphics_tools(&Path)`, `GraphicsToolNote`, `graphics_step_aside(&Path)`, `GraphicsStepAside`, `UPSCALER_FILES`, `graphics_now(&GameInstall, Option<u32>)`, `GraphicsNow`, `OtherFolderProfile`, `LaunchOptionsInfo`, `InstallRecord` (+ `start`) |
| `pe` | `exports(&Path)`, `is_pe64_dll(&[u8])`, `classify_proxy(&Path) -> Result<ProxyClass>`; `ProxyKind {None, Kiln, ShroudForge, Emf, Unknown, NotDll}`, `ProxyClass`, marker constants |
| `proc` | `list()`, `find_game(&GameInstall)`, `alive(u32)`; `ProcessInfo` |
| `launch` | `steam_uri(u32)`, `open_uri(&str)`, `play(&DataDirs, &GameInstall, &LaunchRequest) -> Result<LaunchResult>` (through Steam or directly, as the install's record says), `play_with` (Steam, a test's opener), `play_via(.., Starter)`, `direct_log`; `LaunchMode`, `LaunchRequest`, `LaunchResult`, `Starter` |
| `direct` | (dev.29.17) `StartMode`, `StartSettings`, `is_custom`, `effective_mode`, `settings`, `mode_of`, `prefixes_root`, `own_prefix`, `prefix_paths`, `runner_at`, `runners`, `default_runner`, `resolve_runner`, `command_for` (the command from facts, no I/O), `plan`, `prefix_of`, `spawn`, `setup` (`StartSetup`),
`Host` (home, `XDG_DATA_HOME`, PATH and the system's compatibility-tools folders; `plan_on`, `prefix_on`, `setup_on`,
`game::add_on`, `set_start_on`, `apply_start_on` take one: tests), `compat_roots`, `compat_client_dir`, `umu_proton`,
`platform_of`, `UMU_RUN`, `UMU_GAMEID`, `NO_RUNNER`, `build_check` (`BuildCheck`), `known_in_game`, `known_in_manifest`, `newest_bundle`, `unknown_build_sentence`; `RunnerInfo`, `RunnerKind`, `DirectCommand`, `SCRUB` |
| `winpath` | `unix_to_z(&Path)`, `z_to_unix(&str)`, `z_drive_ok(&Path)`, `to_game(&Path, &GameInstall)`, `to_host(&str, &GameInstall)` |
| `bundle` | `read(&Path)`, `verify(&Path, &mut dyn Progress)`, `create(&Path, &Path, &CreateOptions)`, `list(&DataDirs)`, `import(&DataDirs, &Path)`; `RuntimeManifest`, `RuntimeComponent`, `RuntimeFile`, `RuntimeBuilds`, `RuntimeBuild`, `RuntimeBundle`, `CreateOptions` |
| `install` | `read_receipt(&Path)`, `status`, `install(&DataDirs, &GameInstall, &Path, &InstallOptions, &mut dyn Progress)`, `uninstall`, `rollback`, `plan`; `InstallReceipt`, `ReceiptRuntime`, `ReceiptGame`, `ReceiptFile`, `ReplacedFile`, `ReplacedClass`, `ReplacedAction`, `InstallOptions`, `InstallState` |
| `blocked` | (dev.29.18) `classify(Option<i32>, &str, windows)`, `mark_with`, `io_at(&Path, what, rel)`, `of(&Error)`, `last_in_log(&str, &Path)`, `finding_text`, `program_file`, `game_rel`; `Blocked {path, osError}`, `Logged`, `ACCESS_DENIED`, `SHARING_VIOLATION`, `VIRUS_INFECTED`, `VIRUS_DELETED`: a DLL or exe the install could not write because Windows refused it, as `detail.blocked`, and the last such failure in launcher.log (the report's `install-blocked`) |
| `chain` | `parse(&[u8]) -> ChainIni`, `parse_once(&[u8]) -> OnceIni` (`profile`; `chain` and `late` only when the file has the section), `read(&Path)`, `read_raw`, `write_chain_section`, `write_profile_section`, `write_profile_plan(&Path, &ProfilePlan)`, `write_once(&Path, &ProfilePlan)`, `rule_late_entries`, `is_late`, `set_late(&Path, &str, bool)`, `add_chain_entry`, `remove_chain_entry`, `rule_off_entries`, `plan_is_current`, `game_path`; `ChainEntry`, `ChainIni`, `OnceIni`, `EntryStatus`, `ProfilePlan` |
| `compat` | `parse_rules`, `load_rules`, `evaluate(&[SourcedRule], &Facts, &[PlanEntry])`, `apply`, `glob`, `file_name`, `parse_hooks`, `read_hooks(&Path)`, `overlaps(&HooksState)`, `plan` (returns what it read of chain.ini and hooks.json too: `Plan.chain_text`, `.ini`, `.hooks`), `dllmain_entries`, `view(&DataDirs, &Profile, &GameInstall) -> Result<ChainView>` (reads each file once), `find_entry(&DataDirs, &Profile, &GameInstall, &str) -> Result<PlanEntry>` (one entry by path, without the view), `set_entry_late`, `kind_engine`; `Matcher`, `Rule`, `RuleAction`, `RuleSource`, `SourcedRule`, `Facts`, `Section`, `PlanEntry`, `RuleResult`, `HooksState`, `HookTarget`, `HookLink`, `ForeignDest`, `ForeignSymbol`, `Overlap`, `OverlapKind`, `CoreRule`, `Plan`, `ChainView`, `ChainViewEntry`, `OverlapView`, `LibraryHit`, `SessionInfo` |
| `manifest` | `parse_kiln(&str) -> Result<KilnManifest>`, `read_kiln`, `parse_native(&str) -> Result<NativeManifest>`, `read_native`; `Side`, `RunSide`, `Clients::default_for`, `ModKind`, `KilnManifest`, `Dependency`, `Incompatibility`, `TestsSpec`, `NativeManifest`, `NativePhase`, `IncompatibleSpec`, `OneOrMany` |
| `semver` | `SemVersion::{parse, compare, release}`, `VersionRange::{parse, at_least_same_major, satisfies, is_any}` |
| `resolver` | `resolve(&[Candidate], &Environment) -> Result<Resolution>`; `Candidate`, `Environment`, `ModState`, `Rejection`, `Resolution` |
| `library` | `list(&DataDirs, Option<ModKind>)`, `detect(&Path)`, `import(&DataDirs, &[PathBuf], &mut dyn Progress)`, `remove(&DataDirs, &LibraryRef, bool)` (GameRunning while a game runs, or a server whose profile file points into the library, whatever the bool), `unused(&DataDirs)`, `users`, `path_of`; `LibraryRef`, `LibraryEntry`, `LibraryVersion`, `ImportSource`, `DetectKind`, `Detected`, `ImportReport`, `Skipped` |
| `profile` | `list`, `get`, `create(&DataDirs, &str, Option<&str>)`, `save`, `delete`, `resolve_preview`, `activate(&DataDirs, &str, &GameInstall) -> Result<Activation>`, `deactivate`, `active`, `render`, `read_game_profile`, `game_profile_owner(&DataDirs, &GameInstall) -> Result<GameProfileOwner>`, `fingerprint(&Profile)`; `GameProfileOwner {None, Here, Elsewhere}`, `ProfileFile`, `ProfileFileMods`, `FolderMod`, `PluginMod`, `Dropins`, `BoundServer`, `ModpackRef`, `Profile`, `ProfileMods`, `ProfileMod`, `DataDirMode`, `ProfileSummary`, `ProfileCounts`, `Activation`, `ResolvePreview`, `NativePlanEntry` |
| `quick` | `add(&DataDirs, &GameInstall, &[PathBuf], &mut dyn Progress) -> Result<AddReport>`, `add_with` (running given), `ensure_default`, `ensure_default_with`, `revert(&DataDirs, &str, &[ProfileChange]) -> Result<Profile>`, `is_modpack`, `DEFAULT_PROFILE`; `AddReport`, `AddedMod`, `MissingMod`, `ProfileChange`, `Target` |
| `playonce` | `render(&PlayOnce) -> String`, `decide(&[u8], i64, bool, bool)`, `write(&Path, &PlayOnce)`, `pending(&Path)`, `new_nonce()`; `PlayOnce`, `PlayOnceMode`, `PlayOnceStatus` |
| `config` | `snapshot_path`, `list`, `read`, `set`, `write`, `reset`, `validate`, `read_native_state`, `write_native_state`; `ConfigTarget`, `SchemaSnapshot`, `SnapshotSection`, `SnapshotEntry`, `SnapshotSource`, `ValueFormat`, `NativeUserState`, `NativeModSwitch`, `ConfigFileInfo`, `ConfigDocument`, `ValidationError` |
| `jsonc` | `split_path(&str) -> Vec<PathSeg>`, `parse`, `set_value`, `remove_value` |
| `saves` | `list`, `backups`, `backup`, `restore`, `delete`, `KEEP`; `SaveSlot`, `SlotKind`, `SaveBackup`, `BackupReason`; `saves::editor::{list_saves, file_info, read_file, zstd_decompress, zstd_compress, game_running, backup_file, write_save, list_backups, restore_backup, backups_dir}` |
| `logs` | `list(&Path)`, `read(&Path, &str, Option<u64>, Option<u64>)`, `crash_reports`, `Follower::{open, poll}`; `LogFile`, `LogKind`, `LogChunk`, `CrashReport` |
| `report` | `build(&ReportInput) -> Report` (never fails; unreadable sections in `errors`), `write_zip(&Report, &Path)` (feature `zip`), `redact(&str, &Redactor)`, `redact_json`, `is_secret_key`, `player_names`, `Redactor::{new, current, add_home, add_word, add_whole_word, add_players_from, add_steam_accounts}`, `file_name`, `os_info`, `os_release`, `folder_mods`, `folder_loaded`, `engine_headline`, `engine_mod_lines`, `host_name`, `LEFT_OUT`; `ReportInput`, `Report`, `Finding`, `Level`, `OsInfo`, `FolderMod` ([support-report.md](support-report.md)) |
| `control` | `send(&Path, &str, Duration) -> Result<Value>` |
| `state` | `read_boot`, `read_mods`, `read_native`, `read_last_server`, (dev.29.18) `read_graphics` (implemented); `GraphicsState`, `UpscalerState`, `VendorFilesSeen`, `BootJson` (+ `BootProfile`, `ProfileMode`, `BootChainEntry`, `ChainSource`, `ChainStatus`, `RefusalDetail`, ...), `ModsJson`, `ManagedMod`, `NativeJson`, `NativeModStatus`, `LastServer`, `RequiredMod`, `JoinOutcome`, `ServerRef` |
| `server` (`instance settings steamcmd run control query backup remote game procs console proton`) | `instance::{list, get, create, import, remove, record}`, `settings::{read, write, validate, schema}`, `steamcmd::{install, update}`, `run::{status, start, stop, restart, pid, headless, ctrl_c}`, `console::{capture_path, open_capture, pick, snapshot, ConsoleFollower::{open, poll, source}, ConsoleLine, ConsoleLevel, ConsoleSource, ConsoleSnapshot, ConsoleUpdate}`, `proton::{plan, SteamLaunch, parse_launch_options}`, `control::{mods, sync, set_enabled}`, `query::query`, `backup::{list, create, restore}`, `remote::{run, read_file}`; `ServerInstance` (+ `runner`, `prefix`, `backupDir`, `kilnctl`, `stopTimeoutS`), `ServerRunner {Kilnctl, Steam, Windows, Remote}`, `ServerSpec` (+ `wine`, `skipSteamcmd`, `resume`), `ServerStatus`, `ServerState`, `ServerMod` (+ `kind`, `steamMessages` when true), `SourceMarker`, `NetworkModpackConfig`, `RemoteSpec`, `ServerQuery`, `ServerBackup`, `ServerSettings` (+ `schema`, `running`); kilnctl's moved pieces (instance files, game, procs, the foreground runner) |
| `admin` (`protocol client bridge tunnel discover datadir hub web_panel`) | `protocol::{encode, decode, decode_known, write_frame, read_payload, parse_token, mac_message, mac, op_budget, error_code, Frame, KapErrorCode, Endpoint, Limits, ServerIdent}` + constants (`MAX_SERVER_FRAME` = 16 MiB), `client::{AdminClient::{connect, connect_dir, connect_ssh, connect_tcp, connect_file, session, call, subscribe, unsubscribe, next_event, is_open, shutdown, close, send_frame}, Transport, AdminSession, AdminEvent, TcpTarget}`, `bridge::FileBridge::{new, locate, alive, call, call_raw, state}`, `tunnel::{SshTunnel::{open, open_to, close, alive, last_error, errors}, ssh_stdio, splice_child, pump, SSH_SETUP, MAX_CHILDREN}` (0.2.0-dev.31.12: held local port, `ssh -W` per connection, this user's programs only on Linux, at most 8 at once), `discover::{endpoint, endpoint_at, token, token_at, live_admin_dir, live_admin_dir_in, probe, remote_admin_dir, remote_endpoint, remote_token, remote_endpoint_and_token, remote_endpoint_and_token_at}`, `datadir::{booted_data_dir, local_data_root, remote_data_root, DataRoot::{local_mod, remote_mod}, local_host_path, remote_host_path}` (0.2.0-dev.31.12: a profile's `dataDir`; one data root, never a mix), `peer::{admits, decide, same_user, owner, parse_addr}` (0.2.0-dev.31.12), `client::AdminClient::{connect_at, connect_dir_in, connect_ssh_at}` and `bridge::FileBridge::locate_in` (0.2.0-dev.31.12: a given data root, the server's Wine prefix), `web_panel::{WebPanels::{new, open, close, close_all, list}, WebPanelInfo, Via, Opened, gateway, origins::{slot, key, ours, derive, remember_port}, peer, pin, endpoint::{root, read, read_in, find_in}, ids::{PanelMod, KILNWARDEN, KILN_WEBADMIN, ALL, find, old_copy_in_mods_json}}` (feature `web-panel`, 0.2.0-dev.31.12), `hub::{AdminHub::{new, connect, call, subscribe, unsubscribe, disconnect, sessions, session_of, shutdown}, HubEvent, Connector}` |
| `gameart` (feature `gameart`) | `extract_editor`, `extract_map`, `status`, `game_build`, `build_of`, `read_source`, `write_source`, `recipe_of`, `GameData::open`; `SourceInfo`, `GameSource`, `ArtStatus`, `GameBuild`, `EditorOptions`, `MapOptions`, `LayoutSource`; `kfc::Kfc`, `refl::{PeFile, TypeDb}`, `res`, `texture`, `image`, `work` ([gameart.md](gameart.md)) |
| `modpack` | `inspect`, `download`, `verify`, `extract`, `check_against`, `install`, `build`, `publish`, `publish_state`, `client_mods`, `required`, `server_identity`; `ModpackManifest`, `ModpackMod`, `ModpackServer`, `ModpackSource`, `ModpackPreview`, `ModpackInstallRequest`, `ModpackInstallResult`, `BuiltModpack`, `ModpackPublishState`, `ClientMod` |
| `modpack::pack` | Crucible packs ([crucible.md](crucible.md)): `build`, `read`, `verify`, `peek_format`, `inspect`, `install`, `instance`, `instances`, `plan_update`, `update`, `undo`, `uninstall`, `diff`, `install_server`, `check_index`, `secret_findings`, `override_path_ok`; `PackIndex`, `PackMod`, `PackLayer`, `PackOverride`, `BuildRequest`, `BuiltPack`, `PackPreview`, `Instance`, `InstallRequest`, `InstallResult`, `UpdatePlan`, `UpdateRequest`, `UpdateResult`, `PackDiff`, `ServerInstallRequest`, `ServerInstallResult` |
| `modpack::studio` | Pack Studio's pack source ([pack-studio.md](pack-studio.md)): `init`, `create`, `open`, `list`, `save`, `delete`, `set_credits`, `public_needs`, `build_request`, `build`, `analyze`, `keep_version`, `version_file`, `record_published`, `capture`, `set_capture`, `layer_files`, `layer_read`, `layer_write`, `layer_delete`, `layer_set`, `layer_lint`, `pick`, `fields`, `order`, `test`, `test_report`, `test_end`; `PackSource`, `Studio`, `StudioMod`, `NewPack`, `CaptureFile`, `CaptureChoice`, `StudioOrder`, `PickHit`, `TestReport`, `Published` |
| `crucible::publish` | `publish(&DataDirs, &Crucible, &Path, &PublishRequest, Option<&GameInstall>, &mut dyn Progress) -> Result<PublishResult>`, `set_visibility`, `precheck`, `pack_state` |
| `modpack::merge` | the three-way merge of config overrides: `leaves`, `value_at`, `apply`, `three_way`; `OverrideMode`, `Pick`, `KeyConflict` |
| `lint` | `lint_paths`, `lint_text`, `lint_profile`, `lint_library_entry`, `rules`, `schema_for`, `docs::{idrefs, schema_markdown}`; `LintOptions`, `LintReport`, `Diagnostic`, `Severity`, `Span`, `Fix`, `TextEdit`, `Related` |
| `vendor` (`manifest agreements store download`; 0.2.0-dev.29) | `builtin()`, `manifest::{parse, check_url, license_text, license_sha256, vendor_orgs, redirect_hosts, file_name_ok, BUILTIN, LICENSE_TEXTS, HOSTS, NOT_PINNED, MAX_FILE, VendorManifest, Component, License, VendorFile}`, `agreements::{Agreements::{load, agreed, agreed_at}, record, path, Agreement}`, `store::{Store::{at, for_game, root, folder, rel, lock, receipts, problems, partial, status, component_status, remove}, version_cmp, Receipt, ReceiptFile, ReceiptLicense, VendorState, ComponentStatus, LicenseInfo, FileInfo, Installed, RemoveReport, DIR, RECEIPT, PART_SUFFIX, LOCK}`, `download::{download, policy, user_agent, DownloadReport}` (section 5.15) |
| `nexus` (`api catalog install nxm handler keystore oauth version`; network: feature `http`) | `client(token, app_version)`; `api::{Client::{new, with_credentials, has_credentials, search, mod_details, mod_files, file_versions, file_version_by_gsid, dependencies, download_links, validate, manifest, open_download, rate}, Credentials, Transport, UreqTransport, AppId, Account, ManifestEntry, RateState, parse_manifest}`, `catalog::{Catalog::{new, search, details, verify, files, requirements, installed, check_updates, record_install, cached_verdict}, classify, newest, bbcode_to_text, files_url, mod_url, root_of, refuse_kiln, GAME, KILN_MOD, KILN_EPOCH, CHECK_BATCH, Query, Filter, Sort, Page, CatalogMod, Details, CatalogFile, Requirement, InstallRecord, InstalledMod, InstalledInfo, ModType, Verdict}`, `install::{install, download, check_zip, InstallRequest, InstallReport, CheckReport}`, `nxm::{parse, game_of, NxmLink}`, `handler::{register, unregister, status, state, forward, platform, acquire_instance, queue_link, take_links, exec_argv, exec_quote, fill_command, split_command, LinuxMime, HandlerPlatform, HandlerState, HandlerStatus, Previous}`, `keystore::{TokenStore::{new, with_backend, get, set, clear, file_path}, TokenPlace, SecretBackend, retire_legacy, write_private}`, `oauth::{Config::{registered, new}, Login::{bind, authorize_url, wait}, Session::{new, save, clear, place}, Tokens}`, `version::{compare, is_newer}` ([catalog.md](catalog.md)) |
| `community` (`settings relay session signin post known hardware testers presence`) | the Discord features ([discord.md](discord.md)): `settings::CommunitySettings::{load, save, update, network_allowed, relay}`, `relay_base`; `relay::Relay::{new, with_session, signin_start, signin_exchange, signin_poll, me, signout, forget, report, posts, post_seen, post_solved, post_delete, feed, known_issues, tester, tester_join, tester_leave, tester_results}`, `kind_of`, `LoopbackTransport` (tests, debug builds); `session::{SessionStore, Stored, sign_out}`; `signin::{Attempt, Loopback, Secret, Progress}`; `post::{build, wire, PublicPost}`; `known::{KnownList, best}`; `hardware::{read_linux, read_windows, capabilities, pci_name, Hardware, Capability}`; `testers::{steps_for, Results, Plan}`; `presence::{activity, socket_paths, connect, discord_running, Ipc}`. `net::Client::allow_delete` lets only this client send DELETE; `report::PublicFacts` (`Report::facts`, report.json `facts`) |

kilnctl is built on the crate; it also has `bundle`, `client`, `admin` and `deploy --bundle`.

## 13. Frontend contracts (`launcher/`)
- React 18 + TypeScript + Vite, bun. Router: `HashRouter`. Data: TanStack Query; state: zustand.
- `src/main.tsx`: `DataFolderGate` (`src/shell/DataFolderGate.tsx`) around both UIs: `data_folder_get` first; a broken
  pointer or another launcher's move mounts only its dialog ([app.md](app.md), "The data folder").
- `src/api/commands.ts`: one typed function per command (section 9) + the shared types + `KilnEvents`. Names and
  argument shapes are the contract; pages import from here, never call `invoke` directly.
- `src/api/kinds.ts`: the 14 `ModKind`s for the UI: `MOD_KINDS`, `CORE_KINDS`, `IMPORTED_KINDS` (every engine kind,
  = `engine_mods::IMPORTED`), `ENGINE_KINDS`, `NOT_RUNNING_KINDS` / `runsYet` / `NOT_RUNNING_NOTE` (=
  `engine_mods::runs_yet`: Ember), `KIND_NAME` (= `ModKind::label`), `KIND_SHORT` (badges), `KIND_TONE`,
  `KIND_PLUGIN`, `emptyMods()`, `totalCount(counts)`.
- `src/api/mock.ts`: used automatically when `window.__TAURI_INTERNALS__` is absent (plain browser, Playwright mock
  mode): every command answers from in-memory fixtures (built on `schema/examples/`), unknown ones reject with
  `not_implemented`.
- `src/api/admin.ts`: KAP/admin types, `<AdminProvider serverId>` and the hooks
  `useAdminState([serverId])`, `useHasOp([serverId,] op)`, `useAdminCaller([serverId])` (op runner),
  `useAdminCall(op, options?)` (TanStack mutation), `useAdminQuery(op, args?, options?)` over one AdminHub session per
  server; without a server id they use the nearest provider. The admin routes are mounted by `<AdminRoutes/>` at
  `servers/:id/admin/*`. Pages: [admin-pages.md](admin-pages.md).
- `src/theme/tokens.css`: generated from `branding/palette.json`: `--k-<name>` for every colour (with `--k-well` and
  `--k-line-hot`), `--k-radius-{s,m,l}`, `--k-font-{display,ui,label,mono}` (Grenze Gotisch, Alegreya Sans, Alegreya
  Sans SC, Inconsolata; bundled locally, no web-font URLs), `--k-text-on-ember: #170803` (text on ember or gold fills
  is never white), `--k-ember-text-on-light`.
- Routes: Deck UI `/deck`, `/deck/{mods,add,catalog,kiln,help,settings}` (its own frame outside the shell, [deck.md](deck.md)); Play `/`; Client `/profiles/:id/{mods,native,emf,engines,configs,saves}`, `/library`, `/catalog`, `/runtime`,
  `/logs`, `/saves`, `/editor`, `/settings`, `/about`; Servers `/servers/:id/{overview,logs,ticks,settings,configs,mods,
  backups,modpack}`; Admin `/servers/:id/admin/{players,map,zones,items,spawn,world,combat,creatures,gamedata,permissions,
  content,saves,crashes}` (`admin/configs`, `admin/mods` and `admin/console` redirect to the merged server pages).

Library usage includes saved profiles under `snapshots/before-beta/profiles/`, named `<id> (before beta)`. Clean up
and ordinary removal protect their packages until the snapshot is replaced, including disabled entries. Saved
profiles and metadata files are never read through symlinks. Explicit forced removal still honors its existing
confirmation. Restore preflights saved profile references against present Library records and paths before any
replacement, also when recovering a staged restore. A legacy launcher can still remove these packages; restore then
refuses and names what to reimport.

The beta copy includes launcher settings and profile metadata. It excludes `library/kiln-kept.json` and its
settings folders. Restore and recovery skip that journal even in older copies and restore stages, leaving its live
payload and ownership together.

A release launcher selects only its shipped runtime for automatic game updates, comparing bundle version and
creation time. Cached test runtimes remain available for explicit selection on Runtime. Without a shipped runtime a
release offers no automatic game update. Test launchers keep the existing cached-bundle update behavior.

Capture, startup recovery and restore upgrade the existing data-folder lock to exclusive access. Another launcher
using that folder causes a clear refusal. A blocked restore retains exclusivity; a completed startup or a return to
the original session downgrades to shared access. Restart handoff keeps exclusivity until the process releases the
lock. An identical startup with no beta work may share the folder. An upgrade refusal returns to the session only
after shared access is reacquired. A lock error holds local writers behind the recovery gate until restart.

Beta startup gate commands: `snapshot_status` returns `{ phase: "preparing" | "blocked" | "ready", reasons: string[], canContinue: boolean }`; `snapshot_continue` takes no arguments and records an explicit decision to continue without a complete pre-beta copy. Neither bypasses an unfinished restore journal. `UpdateStatus.restoreRequested` records the accepted restore choice, and `beforeBetaWarning` carries persisted opt-out reasons.

ShroudForge hosting: the existing `kiln/state/shroudforge.json` carries `mode: "self-hosted"` and additive
`loaderFile` (the module path observed by the adapter; bootstrap preferred, runtime fallback). Older records may lack
`loaderFile`. `engine_status.state` preserves this last-start evidence. Current `ownLoader.file` / `found_scan.loaders`
come from disk detection and decide the next-start warning; historical state never resurrects a removed loader.
