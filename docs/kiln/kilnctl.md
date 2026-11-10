# kilnctl: the Linux server runner

`kilnctl` installs, prepares and runs Enshrouded dedicated servers (with or without Kiln) under Wine on Linux. It does
the same steps as the lab script (`tools/lab/lab.sh`), with checks, as one binary and
a systemd user unit. The runner decisions are in [linux-server.md](linux-server.md): GE-Proton's bundled wine called
directly (default) or distro wine, no `proton` script, no Steam client, SteamCMD for the game files, null graphics
driver, ntsync.

Source: `runner/kilnctl` (Rust, MIT, a member of the root Cargo workspace next to `crates/kiln-manage` and the
launcher backend). Units: `runner/systemd/`. The reusable logic lives in `crates/kiln-manage`
(instance files `server::instance`, Wine `wine`, the game side `server::game`, /proc `server::procs`, the runner
`server::run`, SteamCMD `server::steamcmd`, the console channel `control`, paths/time `util`); kilnctl keeps thin
shims with the same CLI, the same messages and the same 34 tests ([launcher/core.md](launcher/core.md)).

## Build and install
```sh
cargo build --release -p kilnctl                                    # target/release/kilnctl ($CARGO_TARGET_DIR if set)
cargo install --path runner/kilnctl --root ~/.local                  # ~/.local/bin/kilnctl (the units expect it there)
```
Runtime needs: `steamcmd`, GE-Proton (in a Steam `compatibilitytools.d` folder, or anywhere with `wine = "ge:/path"`)
or distro wine, `python3` (for the sigdb check in `update`), coreutils `cp`/`tail`, GNU `tar`, and `systemctl` for
the unit integration (sha256 is computed in-process).

## Quick start
```sh
kilnctl init myserver --dir ~/srv/enshrouded/myserver --port 15637      # writes ~/.config/kiln/instances/myserver.toml
kilnctl install myserver                    # SteamCMD (add --reflink-from <existing install> on btrfs/XFS: seconds)
kilnctl prefix myserver                     # wineboot -i + Graphics=null, verified
kilnctl deploy myserver                     # Kiln via tools/deploy-server.sh (policy from the instance file)
kilnctl preflight myserver                  # all checks; exit 1 on a failure
kilnctl start myserver                      # background; returns when the world runs
kilnctl status myserver
kilnctl stop myserver                       # SIGINT, save, wineserver -w
```
For a server that should stay up: install the systemd unit (below) and use `systemctl --user enable --now kiln@myserver`.

## Instance file
`~/.config/kiln/instances/<name>.toml` (`$XDG_CONFIG_HOME` respected). Names: 1-48 of `[a-z0-9_-]`, starting with a
letter or digit; they double as systemd instance names. `init` writes every key with a comment; only `dir` is
required.

| Key | Default | Meaning |
|---|---|---|
| `dir` | (required) | Server folder (the one with `enshrouded_server.exe`) |
| `prefix` | `<dir>.pfx` | Wine prefix of this instance; each instance has its own |
| `wine` | `"ge"` | `"ge"` newest GE-Proton; `"ge:GE-Proton11-11-x86_64"` that one (a name or a GE folder path); `"system"` wine + wineserver on `PATH`; or a path to a wine binary (its `wineserver` next to it), a GE folder or a wine `bin/` folder. `init --wine ge` pins the newest installed GE-Proton (`ge:<name>`), so installing a newer GE-Proton never swaps the Wine under an existing prefix |
| `query_port` | `15637` | UDP query port; must equal `queryPort` in `enshrouded_server.json` (preflight fails otherwise) |
| `backup_dir` | `<dir>.backups` | Savegame tars |
| `require_ntsync` | `true` | `false` turns a missing/unusable `/dev/ntsync` into a preflight warning |
| `stop_timeout` | `90` | Seconds `stop` (and `run` after its launcher exits) wait for the save; 10-900 |
| `[kiln] source` | the tree kilnctl was built from | Kiln checkout: `tools/deploy-server.sh`, `sigdb/`, `tools/sigport/sigdb_check.py` |
| `[kiln] build` | `<source>/native/build` | Kiln build output (`KILN_OUT`) that `deploy` lays out |
| `[kiln] policy` | `"verified-only"` | Written to `kiln/kiln.json` on deploy; `"any"` for labs |
| `[kiln] samples` | `false` | Also deploy the sample mod and native plugin |
| `[env]` | empty | Extra variables for every Wine call (`WINEDEBUG`, `KILN_DISABLE = "1"`, ...) |

Refused at load time: relative paths, `dir == prefix`, a `dir`/`prefix` inside a Steam client library
(`steamapps/common`, `steamapps/compatdata`: kilnctl manages its own SteamCMD install and never the client's or the
live server's), ports below 1024, `[env] WINEPREFIX`, and any `WINEDLLOVERRIDES` that takes Wine's builtin `mscoree`
away (`mscoree=`, `mscoree=d`, `mscoree=n`, `*=n`).

## What every Wine call gets
```
WINEPREFIX=<prefix>  WINEDEBUG=-all  WINEDLLOVERRIDES="mshtml=;dinput8=n,b"  KILN_INSTANCE=<name>   (+ [env])
unset: DISPLAY WAYLAND_DISPLAY WINESERVER WINELOADER WINEDLLPATH WINEARCH STEAM_COMPAT_DATA_PATH
       STEAM_COMPAT_CLIENT_INSTALL_PATH KILN_DISABLE (inherited values; [env] may set KILN_DISABLE)
```
`dinput8=n,b` loads Kiln's proxy; `mshtml=` stops the Gecko prompt. Never disable `mscoree`: under Wine the .NET
runtime opens assemblies through it, and every managed load then fails with `0x8007007E`. Unsetting `DISPLAY` and
`WAYLAND_DISPLAY` matters under systemd too: the user manager often carries the desktop's display variables.

## Commands
| Command | What it does |
|---|---|
| `init <name> --dir D [--port N] [--wine ge\|system\|PATH] [--prefix P] [--backup-dir B] [--policy verified-only\|any] [--samples] [--kiln-source S] [--kiln-build B] [--force]` | Validates and writes the instance file (refuses to overwrite without `--force`). Does not touch `D` |
| `install <name> [--reflink-from DIR]` | `steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir D +login anonymous +app_update 2278520 validate +quit`. Success = SteamCMD's `Success! App '2278520'` line (its exit status is unreliable). `--reflink-from` first runs `cp -a --reflink=always` of everything in `DIR` except `kiln savegame logs dinput8.dll enshrouded_server.json backup` (refused when the target already has the exe), so SteamCMD only validates. Then writes `enshrouded_server.json` if missing (see below) |
| `prefix <name>` | `wine wineboot -i`, `wine reg add HKCU\Software\Wine\Drivers /v Graphics /d null /f`, `wineserver -w`, then checks `system.reg`, `user.reg`, `drive_c/windows/system32` and `"Graphics"="null"`. Re-running updates an existing prefix |
| `deploy <name> [--from KILN_OUT] [--samples] [--build]` | Runs `tools/deploy-server.sh <dir> --no-build` with `KILN_OUT` (the one definition of the layout). `--build` lets the script build first. On a fresh folder it passes `--policy`; on an existing `kiln/kiln.json` it only changes the `policy` value, so hand edits (`log_level`, `clr`, ...) survive |
| `preflight <name>` | The checks below; prints one line each; exit 1 on any failure (systemd's `ExecStartPre`) |
| `run <name>` | Foreground server (systemd's `ExecStart`); see "Process model" |
| `start <name> [--systemd] [--timeout S]` | Preflight, then `kilnctl run` detached (`setsid`, output appended to `~/.local/state/kiln/<name>.run.log`, rotated at 8 MB); waits for `LoadEcsScene to Run` in the game log written after the start (default 300 s). `--systemd` starts `kiln@<name>` instead and waits the same way. Already running: says so, exit 0 |
| `stop <name> [--timeout S]` | If `kiln@<name>` is active: `systemctl --user stop` (so `Restart=on-failure` does not bring it back). Otherwise SIGINT to the server, wait up to `stop_timeout`, `wineserver -w`. Reports whether a save was written during the stop. On timeout: SIGKILL + `wineserver -k`, exit 1 |
| `status <name>` | Install/build id, prefix, wine version, pid, uptime, world state (last `Switching state` line), UDP bound, ntsync in use (an fd on `/dev/ntsync`), unit state, Kiln policy and its build line from `kiln.log`, save, backups. Exit 0 running, 3 stopped |
| `logs <name> [-n N] [-f] [--kiln\|--run]` | Game log (CRs stripped), `kiln/logs/kiln.log`, or the detached runner's output |
| `backup <name>` | `tar -czf <backup_dir>/<name>-savegame-<UTC stamp>.tar.gz` of the save folder (`saveDirectory`). While the server runs, a tar that sees the save change (GNU tar exit 1) is retried up to 3 times |
| `update <name> [--no-start] [--timeout S]` | Stop (if running; remembers whether systemd ran it), back up, `steamcmd ... validate`, then `tools/sigport/sigdb_check.py` on the new exe with the deployed definitions: build id and exe sha256 before/after, symbols/constants that fail, features on and OFF, and what the policy does with this build. Starts again the same way it was running. On a new build without a verified record (Kiln deployed): `sigport port` from the cached old exe, the lab gate, and the verified record only when the port is complete, the definitions need no change and the gate is green (docs/kiln/sigport.md). `--dry-run [--new-exe E] [--old E]`, `--no-port`, `--no-gate`, `--no-mark` |
| `port <name> [--old E] [--new E] [--defs T] [--no-gate] [--mark]` | Port the symbols to the instance's exe and boot the lab gate; `--mark` writes the record on green |
| `gate <name> [--baseline] [--tests a,b] [--gate-port N]` | Boot `<name>-gate` (copy of the instance, policy any, self tests) and compare with the baseline |
| `list` | Instances, ports, running or not |
| `report [<name>] [--dir D] [--out FILE] [--print]` | A support report of an instance, a server or client folder, or (neither) the client Steam knows: one zip (`./kiln-report-<date>.zip`) with kiln.log, proxy.log, the state files, the profile, chain.ini, the receipt, listings of the folder and `summary.txt` with findings; home folder, user and machine names, Steam ids and IP addresses replaced. Prints the short summary. `--print`: summary.txt to the terminal, no zip. Only reads ([launcher/support-report.md](launcher/support-report.md)) |
| `deploy <name> --bundle ZIP [--samples]` | Installs a runtime bundle instead of running deploy-server.sh: `kiln_manage::install` with the server components (`core`, `sigdb-server`, `admin`; `--samples` adds `samples`), receipt `kiln/.install.json`, then the instance's policy in `kiln/kiln.json` (in place). The tree equals deploy-server.sh's (tools/package/parity.sh) |
| `bundle create STAGE [--out DIR] [--git SHA] [--epoch S]` | Zips a staging folder (`tools/package/stage.sh`) into `kiln-runtime-<VERSION>+<yyyymmdd>.<git>.zip`, deterministically (defaults: the source tree's HEAD and its commit time), then verifies it |
| `bundle verify ZIP` | Every entry against `kiln-runtime.json` (size, sha256, no extra or unsafe entries); prints the components and verified builds |
| `bundle install ZIP --dir D [--components a,b] [--switch-from-shroudforge] [--real]` | Install or upgrade into a game folder (client or server by its exe); a folder inside a Steam library needs `--real` |
| `bundle uninstall --dir D [--real]` | Removes the bundle's unchanged files, restores what it replaced byte-identically, keeps `kiln/` |
| `client status [--dir D]` | The client Kiln sees (default: the Steam client, read-only): proxy class, receipt, chain.ini, active profile, running, play-once pending, Steam launch options and the `WINEDLLOVERRIDES` advice, the last `state/boot.json`. Exit 0 running, 3 not |
| `client install ZIP`, `client uninstall` `[--dir D] [--real]` | `bundle install/uninstall` for the client; without `--dir` the Steam client folder, which needs `--real` |
| `client activate PROFILE \| --deactivate [--dir D] [--real]` | Profile activation: `kiln/profile.json`, chain.ini `[profile]`, `<configDir>/native.json` (Z: or copy mode) |
| `client play-once vanilla\|no-mods\|profile\|clear [--profile P] [--dir D] [--real]` | Writes `kiln/play-once.ini` for the next start (profile: also `kiln/profile.once.json` + `kiln/chain/once.ini`). Never starts the game and never touches Steam's config |
| `library import PATH...`, `library list [KIND]` | The launcher's content-addressed mod library (`$KILN_DATA_DIR`, else `~/.local/share/kiln`) |
| `profile create NAME [--copy-from P]`, `profile add P kind:id[@version]... [--disabled]`, `profile show P [--side S]` | Launcher profiles; `show` prints the resolved load order |

### Preflight checks
| Check | Fails when |
|---|---|
| wine | the runner does not resolve (no GE-Proton, no wine on PATH, missing `wineserver`) |
| ntsync | `/dev/ntsync` is missing, not a character device, or cannot be opened read-write (a warning with `require_ntsync = false`) |
| exe | `enshrouded_server.exe` is missing or not a PE file |
| prefix | the prefix is incomplete or Graphics is not `null` |
| process | a server already runs in this prefix, or any server runs in this folder with another prefix |
| port | the UDP query port is bound (names the owning process when it is yours) or cannot be bound; warns when another instance file uses the same port |
| config | `enshrouded_server.json` is missing (the game would come up on 15637), not valid JSON, has no numeric `queryPort`, or its `queryPort` differs from the instance; warns on `slotCount` outside 1-16 |
| env | the effective `WINEDLLOVERRIDES` takes builtin `mscoree` away |
| kiln | half-deployed (proxy without `kiln/` or the reverse), required files missing (`kiln-core.dll`, `sigdb/server.json`, `kiln.json`, `host/Kiln.Host.dll`, `host/Kiln.Api.dll`, `dotnet/host/fxr`, `dotnet/shared/Microsoft.NETCore.App`), invalid JSON in `kiln.json`/sigdb files, an unknown policy, or `dinput8` not native-first. Warnings: not deployed (vanilla), `kiln.json` policy differs from the instance, deployed binaries differ from `[kiln] build`, a broken mod folder, and an unverified build under `verified-only` (Kiln would boot and stay idle) |

### enshrouded_server.json
`install` writes one when the folder has none, before anything can start the game (without it the game creates a
default config on UDP 15637, which may be another server's port): name `Kiln <name>`, `./savegame`, `./logs`,
`0.0.0.0`, the instance's `queryPort`, 16 slots, preset `Default`, and the game's four user groups (Admin, Friend,
Guest, Visitor with the presets' permissions) with random 16-character passwords. The game completes it on the first
start (adds `gameSettings`). kilnctl never edits an existing file; change the port in both places.

## Process model
- `run` starts `<wine> ./enshrouded_server.exe` in the server folder, stdin `/dev/null`, in its own process group, with
  `SIGTTOU` ignored (so Wine writing terminal escapes from the background cannot stop it).
- GE-Proton's wine runs `start.exe /exec`, which spawns the server as a separate process that is reparented to the
  nearest subreaper (the systemd user manager); distro wine runs the server in the launched process. Either way the
  server is found in `/proc` by name (`enshrouded_serv`, or argv[0] ending in `enshrouded_server.exe`) and the
  `WINEPREFIX` in its environment. Every instance has its own prefix, so this never matches another server; the live
  server runs in a Proton prefix under `steamapps/compatdata`.
- SIGINT, SIGTERM and SIGHUP to `kilnctl run` become **one** SIGINT to the server (more signals are logged and
  ignored). The game logs `Trigger gameflow shutdown, exit: Ctrl_C`, saves and exits with status 0. A Ctrl-C in a
  terminal reaches only kilnctl's process group, so the server gets it once, not twice. A stop requested before the
  server process exists is sent to it when it appears (after 10 s without one, to the launcher's process group).
- After the launcher exits, `run` waits for the server process (up to `stop_timeout`), then `wineserver -w` (30 s,
  then `wineserver -k`), and exits with the launcher's status (128 + signal when killed). A crashed server gives a
  non-zero status, which `Restart=on-failure` acts on.

## systemd (user units)
Files in `runner/systemd/`:
- `kiln@.service`: `ExecStartPre=%h/.local/bin/kilnctl preflight %i`, `ExecStart=%h/.local/bin/kilnctl run %i`,
  `Type=exec`, `KillSignal=SIGINT`, `KillMode=mixed` (only kilnctl gets SIGINT; whatever is left after it exits is
  SIGKILLed), `TimeoutStopSec=90`, `Restart=on-failure`, `RestartSec=15`, at most 5 starts in 10 minutes.
- `kiln-backup@.service` + `kiln-backup@.timer`: `kilnctl backup %i` daily (randomised by 30 min, `Persistent=true`).

A release ships the three units next to kilnctl (`runtime/`; on GitHub as `kiln-systemd-units-<version>.tar.gz`):
install those with `install -Dm644 -t ~/.config/systemd/user kiln@.service kiln-backup@.service kiln-backup@.timer`
and kilnctl with `install -Dm755 kilnctl ~/.local/bin/kilnctl`; [dedicated-servers.md](dedicated-servers.md) walks server
owners through it. From a checkout (nothing of this is done for you):
```sh
cargo install --path runner/kilnctl --root ~/.local
install -Dm644 -t ~/.config/systemd/user runner/systemd/kiln@.service runner/systemd/kiln-backup@.service runner/systemd/kiln-backup@.timer
systemctl --user daemon-reload
systemctl --user enable --now kiln@myserver                 # start now and at login
systemctl --user enable --now kiln-backup@myserver.timer    # optional daily backup
loginctl enable-linger "$USER"                              # optional: run without a login session, start at boot
journalctl --user -u kiln@myserver -f                       # kilnctl's lines + the game's console output
```
If kilnctl lives elsewhere, change the two paths in the unit (or add a drop-in with an empty `ExecStart=` and new
`ExecStartPre=`/`ExecStart=` lines).

With the unit active, `kilnctl stop`/`update` go through `systemctl --user stop`/`start`, and `kilnctl status` shows
the unit state. `kilnctl start --systemd <name>` starts the unit and waits for the world. Without lingering, user units
stop at logout, and so does a server started detached with `kilnctl start` from a terminal session.

A failing preflight fails the start (`ExecStart` never runs); `Restart=on-failure` retries it every 15 s until the
start limit.

## Updates and Kiln
`kilnctl update` caches the exe of a verified build (`~/.local/state/kiln/exe-cache/`) before SteamCMD runs; on a new
unverified build it ports Kiln's symbols (`tools/sigport/port.py`), boots the lab gate and marks the build verified on
green, else leaves `NEXT.txt` + `symbols.patch` for a human and the server starts in safe idle. Record a gate baseline
once: `kilnctl gate <name> --baseline`. See docs/kiln/sigport.md.

## Files kilnctl writes
| Path | What |
|---|---|
| `~/.config/kiln/instances/<name>.toml` | `init` |
| `<dir>/` game files, `<dir>/enshrouded_server.json` (only if missing) | `install` |
| `<prefix>/` | `prefix` |
| `<dir>/dinput8.dll`, `<dir>/kiln/` | `deploy` (through `tools/deploy-server.sh`), `deploy --bundle` / `bundle install` (+ `kiln/.install.json`, `kiln/backups/install-<ts>/`, `kiln/chain/`) |
| `<data>/installs/`, `<data>/library/`, `<data>/profiles/`, `<data>/backups/install/` | `bundle`, `client`, `library`, `profile` (`<data>` = `$KILN_DATA_DIR` or `~/.local/share/kiln`) |
| `<client>/kiln/profile.json`, `play-once.ini`, `profile.once.json`, `chain.ini`, `chain/once.ini` | `client activate`, `client play-once` |
| `<backup_dir>/<name>-savegame-*.tar.gz` | `backup`, `update` (never pruned) |
| `~/.local/state/kiln/<name>.run.log`, `<name>.sigdb.json` | `start` (detached runner output), `update` |

Restore a backup with the server stopped: `tar -xzf <backup> -C <dir>` (replaces `savegame/`).

## Verified
- `cargo test -p kilnctl`: the same 34 tests by name; `--help` of the root and all 16 earlier subcommands
  byte-identical (only `deploy` gained `--bundle`, the root lists the new commands).
- `tools/package/stage.sh` + `kilnctl bundle create` twice: byte-identical zips (365 files). `tools/package/parity.sh`:
  bundle install == deploy-server.sh tree (`diff -r`, 344 files; 348 with `--samples`).
- `kilnctl deploy mylab --bundle <zip>` into a lab: `boot complete`, the build verified, SimStep hooked,
  kiln-admin loaded, the world reached Run.
- `client install/uninstall` on a client copy with EMF's loader as dinput8.dll: chained as `kiln/chain/emf-dinput8.dll`,
  backup + receipt; an upgrade bundle; uninstall left the folder byte-identical to before (outside `kiln/`).

## Verified: a full run
Instance `kilnctl-test` (`~/srv/enshrouded/kilnctl-test`, UDP 25760, GE-Proton11-11, policy `any`), Kiln from
`native/build`:
- `install --reflink-from ~/srv/enshrouded/lab2`: 28 entries reflinked in 0.2 s; SteamCMD validate and
  `Success! App '2278520' fully installed.` in 17 s total (build 26404334).
- `prefix`: 13.5 s (`ntsync: up and running.`). `deploy`: 0.8 s. `preflight`: all ok.
- `start`: world up after 14 s (`Switching state from LoadEcsScene to Run`); `kiln.log` `boot complete`,
  `SimStep hooked`, first server-world step; `status`: UDP 25760 bound, ntsync in use.
- `stop`: `Trigger gameflow shutdown, exit: Ctrl_C`, server gone after 3.8 s, wineserver after 4.0 s, `world0.ksd`
  written; runner exit status 0.
- Transient unit (`systemd-run --user --unit=kiln@kilnctl-test` with the unit's settings): preflight in
  `ExecStartPre`, every Wine process in the unit's cgroup; SIGKILL of the server: exit 1, `auto-restart`, back up
  after 15 s (`NRestarts=1`); `kilnctl stop` used `systemctl --user stop`: SIGINT forwarded once, save written, exit 0,
  unit stopped in 1.3 s. A second unit on the same port failed in `ExecStartPre` (`UDP 25760 is in use`).
- Ctrl-C in a pty (`script`): one `Ctrl_C` in the game log, exit 0. SIGTERM to `kilnctl run`: same.
- `update` while running: stop, backup, validate, sigdb (60/60 symbols, 139/139 constants; OFF: `items.transaction`,
  matching Kiln's own `feature items.transaction unavailable`), start; 34 s.
- `wine = "system"` (wine-11.20 Staging) with its own prefix: up in 15 s, ntsync in use, Kiln boots, clean stop.
- `cargo test`: parsing and config logic (instance files, wine specs, overrides, /proc parsers, server json,
  log watching, timestamps, sigdb report).
