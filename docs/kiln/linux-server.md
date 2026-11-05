# Running an Enshrouded dedicated server on Linux (no Steam client)

The server is a Windows program, so it runs under Wine. Kiln does not need Proton's launcher or the Steam client.

## Choice of Wine
| Runner | What it is | Use |
|---|---|---|
| **GE-Proton's bundled wine** (default) | `GE-Proton*/files/bin/wine`, called directly; no `proton` script, no Steam folder needed | Pinned, self-contained build (wine 11.0 staging in GE-Proton 11-11) with ntsync; the same Wine on every host |
| distro wine-staging | `/usr/bin/wine` (11.20 on Arch at the time of writing) | Works the same; version moves with the distro |
| full Proton launcher | `proton run` with `STEAM_COMPAT_*` set | Only for A/B checks; needs the Steam client folder |

A dedicated server renders nothing, so Proton's extras (DXVK, vkd3d, game fixes, Steam runtime) bring nothing.
Measured on the same install and world (probe only, 10 s windows): plain wine step p50 0.73 ms / p99 1.09 ms,
Proton p50 0.74 ms / p99 1.31 ms. Both step the server world at 24.5 Hz.

## Install and prefix
```sh
steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir <dir> +login anonymous +app_update 2278520 validate +quit
export WINEPREFIX=<prefix> WINEDEBUG=-all WINEDLLOVERRIDES="mshtml=;dinput8=n,b"
unset DISPLAY WAYLAND_DISPLAY
wine wineboot -i
wine reg add 'HKCU\Software\Wine\Drivers' /v Graphics /d null /f   # headless: null display driver
```
- `dinput8=n,b` loads Kiln's proxy from the server folder.
- **Do not disable `mscoree`.** Under Wine, the .NET runtime opens assemblies through the loader, which routes .NET
  images via `mscoree`; with `mscoree=` every managed load fails with `0x8007007E` (module not found).
- `mshtml=` stops Wine offering to install Gecko.
- ntsync: `/dev/ntsync` must exist (kernel 6.14+); GE-Proton's wine prints `ntsync: up and running`.

## Start / stop
- Start: `cd <dir> && wine ./enshrouded_server.exe` (foreground; logs in `<dir>/logs/`). The world reaches
  `LoadEcsScene to Run` in ~15 s on a fresh world.
- Stop: SIGINT to the `enshrouded_server.exe` process, then `wineserver -w`. The server saves and exits in ~12 s.
- The [`kilnctl`](kilnctl.md) runner and its systemd unit (`runner/systemd/kiln@.service`) wrap the same steps.

## Lab base (`~/srv/enshrouded/lab2`)
`tools/lab/lab.sh new` reflinks its game files from `~/srv/enshrouded/lab2`, a vanilla SteamCMD install (anonymous).
When the game updates, update lab2 so that every new lab gets the new build. Keep the old exe first (the next
`sigport port` needs it):
```sh
sha=$(sha256sum ~/srv/enshrouded/lab2/enshrouded_server.exe | cut -d' ' -f1)
cp -n --reflink=auto ~/srv/enshrouded/lab2/enshrouded_server.exe ~/.local/state/kiln/exe-cache/$sha.exe
steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir ~/srv/enshrouded/lab2 +login anonymous \
  +app_update 2278520 validate +quit                                  # ends with "Success! App '2278520' fully installed."
```
Labs that already exist keep their build. lab2's `kiln/`, `dinput8.dll`, `savegame/` and `logs/` are not game files;
SteamCMD leaves them alone and `lab.sh new` skips them.

| Date | Build (Steam) | `enshrouded_server.exe` sha256 | Notes |
|---|---|---|---|
| 2026-11-03 | 26404334 (svn 1285601) | `c5c9fe50…` | first install; exe kept in `~/.local/state/kiln/exe-cache/` |
| 2026-11-04 | 26430253 (svn 1287369) | `f0d824be…` | SteamCMD update (only `enshrouded_server.exe` and `enshrouded_server.kfc` changed); keep the old pair for A/B labs (replace both files in a new lab) |
