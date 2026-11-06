# Servers in the Kiln Launcher

Dedicated servers the launcher lists, installs, runs and administers. Code: `crates/kiln-manage/src/server/**`,
`src/admin/**`, `src/modpack.rs`; Tauri commands `launcher/src-tauri/src/commands/{server,admin,modpack}.rs`; pages
`launcher/src/pages/servers/**`. Contracts: [contracts.md](contracts.md) sections 4, 7, 9;
admin transport: [admin-protocol.md](admin-protocol.md).

## Kinds of server (`ServerInstance.runner`)
| Runner | What | Start | Stop |
|---|---|---|---|
| `kilnctl` (Linux) | a kilnctl instance file (`~/.config/kiln/instances/<id>.toml`), Wine (pinned GE-Proton) with its own prefix `<dir>.pfx`, a SteamCMD install | Wine in its own session (`setsid`), output appended to kilnctl's run log (the capture file) | `systemctl --user stop kiln@<id>` when that unit runs, else SIGINT, the save, `wineserver -w` (SIGKILL + `wineserver -k` after the stop timeout) |
| `steam` | a server the Steam client installed (found in the Steam libraries, `source: "steam"` until imported) | Linux: without Steam, the Proton build's own wine in Steam's Proton prefix (below); Windows: like `windows` | SIGINT (Windows: like `windows`); **refused while players are online** (A2S, else the admin `players` op) |
| `windows` | the exe, on Windows | the exe with `CREATE_NO_WINDOW`, output into the capture file | Ctrl-C into the server's console (AttachConsole + GenerateConsoleCtrlEvent; the game saves and exits), else the admin `save_now` op, then TerminateProcess |
| `remote` | another machine over SSH (`remote {host, port?, dir, instance?}`) | `ssh <host> kilnctl start <instance>` | `ssh <host> kilnctl stop <instance>` |

A start returns when the game log says `LoadEcsScene to Run` (or fails: the process vanished, 10 min timeout). kilnctl
and the launcher manage the same kilnctl instances; `kilnctl status <id>` and the launcher agree.

**No window, ever**: the server's output shows only in the launcher. Every
local start sends the server's stdout and stderr into its capture file and gives it no terminal, so Wine gives it a
console without a window; the server runs in its own session and outlives the launcher. No Wine process the launcher
or kilnctl starts (the server, `wineboot`, `reg`) can reach a display: `DISPLAY` is unset and `WAYLAND_DISPLAY` names
a socket that does not exist (`kiln-no-display`). Unset is not enough: libwayland then connects to
`$XDG_RUNTIME_DIR/wayland-0`, the desktop's compositor (measured: two connections from a server start in a prefix
without `Graphics=null`), so a console, message box or crash dialog would open on the desktop. The server runs fine
without any display (measured, GE-Proton 11-11).

**Steam installs** (`server::proton`, Linux). Steam's Play button (and `steam://`) runs the server through Proton's
`steam.exe`, which gives every console program a console window (a Steam-started server shows one, titled
`S:\steamapps\common\EnshroudedServer\enshrouded_server.exe`). The launcher starts it itself, with what Steam would
use, read-only from Steam's files (never writes Steam's configuration or the prefix's registry):
- the Proton build: the app's mapping in `config/config.vdf` (`CompatToolMapping/2278520`), else the default mapping
  (app 0), else the build named in the prefix's `config_info`; looked up in `compatibilitytools.d` (folder or
  registered name) and Valve's `steamapps/common/Proton*`;
- the prefix `<library>/steamapps/compatdata/2278520/pfx` (missing: "start it once from Steam"); refused when Proton
  would upgrade it first (`<compatdata>/version` differs from the build's `CURRENT_PREFIX_VERSION`);
- the launch options (`userdata/*/config/localconfig.vdf`): `VAR=value` words before `%command%` are the environment
  (`WINEDLLOVERRIDES` is added to Kiln's `mshtml=;dinput8=n,b`; `WINEPREFIX`, `DISPLAY`, `LD_PRELOAD` are left out),
  words after it are arguments, wrappers (`gamemoderun`) are left out; all noted in the console.
The server then runs under `<build>/files/bin/wine` like a kilnctl instance; without `steam.exe` it loads the
`steamclient64.dll` next to it, like a SteamCMD install. Valve's own Proton builds are made for Steam's runtime
container; their wine is run directly too (works for GE-Proton; untested for Valve's).

**Where servers come from** (`instance::list`): kilnctl instance files, then launcher records `<data>/servers/<id>.json`
(steam/windows/remote servers, renamed entries), then Steam installs nobody recorded (ids `steam`, `steam-2`, ...).

**New server** (`server_create`, Linux): writes the kilnctl instance file (the same text as `kilnctl init`, wine pinned
to the newest GE-Proton), copies the game files with `cp -a --reflink=always` from `reflinkFrom` when given (free on
btrfs/XFS; config, saves, logs, Kiln and dinput8.dll are not copied), runs `steamcmd +app_update 2278520 validate`
(progress from its `progress:` lines), writes `enshrouded_server.json` (name, port), creates the prefix (`wineboot -i`,
`Graphics=null`). Kiln goes in afterwards with `server_deploy` (a runtime bundle through `kiln_manage::install`).
**Import** (`server_import`): a Steam library folder becomes a steam record; any other folder a kilnctl instance.
**Remove** forgets the server; `deleteFiles` also deletes its folder, prefix and backups (never a Steam install, never
while it runs).

## Console
`kiln_manage::server::console`, component `ServerConsole.tsx` (on the server's Overview).
- **Capture file** `<state>/<id>.run.log` (`$XDG_STATE_HOME/kiln`; Windows `%LOCALAPPDATA%\Kiln\state`): the file
  `kilnctl start` already writes. Each start appends a marker (`---- kiln-launcher start <id> at <time>`, kilnctl's
  `---- kilnctl start ...`) and `kiln-launcher: ...` notes (what ran, Proton build, launch options); a start rotates a
  file over 8 MiB to `.old`. The console shows the last run (up to 5000 lines, `truncated` when cut).
- **Reattach**: the server writes into the file, not into the launcher; a reopened launcher reads it again.
- **Levels**: the game prints every line of `logs/enshrouded_server.log` to stdout without its `[I|W|E time]` prefix
  (stdout = the game log + Wine/Steam lines, in lockstep, not buffered); a captured line takes the level of the same
  text in the game log. Wine's `err:`/`warn:`/`fixme:` lines and `wine: ...` are known by prefix; the launcher's and
  kilnctl's lines are `meta`.
- **Source**: `capture` while the capture is no more than a minute older than the game log; else `gamelog` (the
  server was started by Steam, systemd or by hand: its game log, levels from the prefixes); `none` before any output.
  A follower re-checks every 2 s and sends a `reset` when the source changes.
- **Commands**: the command line (`kiln>`) runs Kiln commands over the server's shared admin session (`admin_call` op
  `command_run`) while Kiln.Admin is connected, with Tab completion (`command_complete`), history, "run as" a player
  and the command list (`commands_describe`); mounted only while the server runs.
- Tauri (contract additions): `server_console_open(serverId, maxLines: number | null)` ->
  `{subscription, serverId, source, path, lines: {text, level}[], truncated}`; `server_console_close(subscription)`;
  event `kiln://console {subscription, serverId, reset, source, path, truncated, lines}` (lines in batches of up to
  2000, every 0.5 s; `reset`: the lines replace the view). At most 16 open consoles (the oldest is closed). Remote
  servers: `unsupported`.

## Pages (`/servers/:id/...`)
One grouped list per server replaces the server tabs and the admin tabs (`launcher/src/shell/serverNav.ts`): Run
(Overview, Logs, Tick stats), World (Players, Map, Zones, Spawn, Creatures, Combat, World), Data (Items, Game data,
Content, Permissions), Files (Settings, Configs, Mods, Backups, Saves, Modpack, Crashes). The layout
(`ServerLayout.tsx`) shows the server's name (a button: change server), its live sentence (state, uptime, players, the
admin connection) and Start or Restart and Stop; Update the game, Install Kiln, the admin transport and Remove sit in
its `···` menu. It holds the shared admin session while the server runs.
- **overview**: the Console (above) and an aside: step p99 over the last minutes (tick stats), steps per second, max
  step, run by, port, admin endpoint, policy, game and Kiln versions, pid.
- **logs**: the shared LogsFor (kiln.log, the game log, proxy.log, mod logs). **ticks**: the tick stats.
- **settings**: `enshrouded_server.json`: name, slots, port, chat, preset, every difficulty setting (the game's
  ranges, minutes stored as nanoseconds), user groups (passwords unique, permissions, reserved
  slots), bans. Validated, unknown keys kept, written with tabs. Refused while the
  server runs (the game rewrites the file).
- **configs**: while the server runs, the live configs (admin ops `config_list`, `config_set`, `config_reload`: Kiln
  checks each change and applies it at once, except entries marked Restart); while it is stopped, the shared
  ConfigEditor on `{server: id}` (offline JSONC edits).
- **mods**: one table for `kiln/mods/*` and `kiln/native/*` and what the running Kiln says about each (admin op
  `mods`: state, tick cost, faults); the flame writes or removes an empty `.disabled` file (the next start); the aside
  acts on the selected mod (`mod_reload`, `mod_unload`, `mod_load`, `mod_test`; Kiln.Admin can't unload itself).
  Sync from the library: each synced folder gets `.kiln-source.json` (`{format, library: {kind, id, version, hash},
  synced, by}`); sync replaces/deletes only folders with that marker; a hand-placed folder of the same id is reported
  as a conflict and left alone. The copy is checked against the library entry's kdh1 first, staged next to the
  target, then swapped in (a `.disabled` flag is kept). A running server gets `mod_unload` before a removal,
  `mod_reload` after a change, `mod_load` for a new folder.
- **backups**: Kiln.Saves backups (`kiln/backups/<stamp>-<name>/`, made with the admin op `backup_create` while the
  server runs: consistent, right after a save) and kilnctl tarballs (`<backup_dir>/<id>-savegame-<stamp>.tar.gz`,
  savegame + kiln/data, made when the server is stopped or has no admin). Restore when stopped: the current save and
  `kiln/data/worlds` move to `<backup_dir>/before-restore-<stamp>/` first. While running, a Kiln.Saves backup is staged
  for the next start (`backup_restore`); tarballs need a stopped server. Steam servers keep tarballs in
  `<data>/backups/servers/<id>/`, never inside the Steam library.
- **modpack**: [modpacks.md](modpacks.md).
- **admin/**: the admin pages over `AdminHub` ([admin-pages.md](admin-pages.md)).

## Admin sessions
`AdminHub` (`kiln_manage::admin::hub`, held in a process-wide OnceLock by `commands/admin.rs`): one session per server,
its id stable across reconnects. `admin_connect(serverId, transport)` returns the existing session when there is one.
Transports: `tcp` (endpoint.json + admin-token on this machine), `file` (the file bridge; state.json polled into
`state` pushes), `ssh` (remote: endpoint and token over `ssh cat`, then a `ssh -N -L` tunnel), `auto` (local: tcp, else
the file bridge when its state.json is fresh; remote: ssh; an auth failure never falls back). Events, as
`kiln://admin {session, serverId, event}`: `push {topic, seq, data}`, `ready {transport, ops, topics}` after every
(re)connect, `closed {reason, retry}` (`retry: true`: the hub reconnects with backoff 1, 2, 4 ... 30 s and re-subscribes
every topic; `false`: final, after admin_disconnect or an auth failure), `error {message}`. `admin_call` errors:
`not_found` (no such session), `admin_unavailable` (reconnecting), `op_failed` with the op's text and
`detail {op, transport, kapCode}`, `busy`, `timeout`.

## Tests
- `cargo test -p kiln-manage --all-features --lib admin:: modpack server::`: KAP client against an in-process mock
  (handshake, wrong token, impostor server mac, concurrent calls, pushes, pings, hub reconnect + re-subscribe, file
  bridge fallback), modpack build determinism / verify / tamper refusal / download, settings validation, A2S parsing,
  ssh quoting.
- Ignored lab tests: `KILN_LAB=<lab> ... -- --ignored lab_` (the client against a real Kiln.Admin, TCP vs file bridge;
  offline required[] equals the server's), `KILN_SSH_HOST=<alias> ... lab_ssh_tunnel` (a real tunnel), and
  `server::tests::lab_instance_lifecycle` (create with reflink + SteamCMD, start, A2S, refusals while running, stop,
  settings edit, backup; run the test binary directly with `XDG_CONFIG_HOME`, `XDG_STATE_HOME`, `KILN_DATA_DIR` pointing
  at scratch folders, since mise's cargo shim reads its trust store from `XDG_STATE_HOME`).
- `tools/nettest/kaptest.py --lab <lab>`: the server side of KAP/1 (37 checks), including every op over TCP against
  the file bridge.
- Console and windowless starts: unit tests `server::console::`, `server::proton::`; ignored lab tests
  `server::tests::lab_console_windowless` (`KILN_TEST_SERVER_ID=<id>` with the scratch `XDG_*`/`KILN_DATA_DIR`, and
  `KILN_STEAM_ROOTS=<fake Steam root>` for a steam copy: start, capture from the marker, game-log levels, follow
  through the stop) and, cross-built, `server::tests_windows::lab_windows_windowless` (`cargo xwin test ... --no-run`,
  then the exe under Wine with `KILN_TEST_SERVER_DIR='Z:\...'`: CREATE_NO_WINDOW start, capture, Ctrl-C stop).
