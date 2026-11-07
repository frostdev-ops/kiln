# Kiln on a dedicated server

This page is for server owners. It shows how to put Kiln on an Enshrouded dedicated server, check that it runs, add
mods, and take Kiln off again. You don't need to build anything.

## Pick your route

| Your server | What you download | How you install | Section |
|---|---|---|---|
| Rented (GTXGaming, G-Portal, ...): you only have FTP or a file manager | `kiln-server-<version>.zip` (the server pack) | unzip it into the server folder | [Rented server](#rented-server) |
| Your own Windows PC | the server pack, or the Kiln Launcher | unzip, or the launcher's Servers page | [Your own Windows PC](#your-own-windows-pc) |
| Your own Linux machine, no desktop | `kilnctl` + `kiln-runtime-<version>.zip` + the systemd units | `kilnctl` commands | [Your own Linux server](#your-own-linux-server) |

Where to get the files: each Kiln release on GitHub (frostdev-ops/kiln, Releases) and on frostdev.io has them. On
the Nexus Mods page, the server pack is the optional file **Kiln server pack**; kilnctl is in **Kiln runtime (manual
install)**.

## The server pack

`kiln-server-<version>.zip` holds exactly what a server needs and nothing for players' games:

| In the zip | What it is |
|---|---|
| `dinput8.dll` | loads Kiln when the server starts |
| `kiln/` | Kiln: its core, services, its own .NET runtime, the server's symbol files, Kiln.Admin |
| `KILN-SERVER-README.txt` | the short version of this page |

Unzipped into the folder with `enshrouded_server.exe`, it's a complete install. It is the same set of files that
`kilnctl deploy --bundle` installs on a server, byte for byte (checked on 0.2.0-dev.17: 380 files, about 117 MB
unpacked, 50 MB zipped). It leaves out the client parts, EMF support and the sample mod.

Before you start, every route:
- Stop the server.
- Back up the savegame folder.
- Look for a `dinput8.dll` that is already in the server folder. That's another mod loader. Unzipping the pack
  replaces it, and that loader stops working. Keep a copy. `kilnctl bundle install` can keep the other loader running
  next to Kiln (it "chains" it); a plain unzip can't.

## Rented server

1. Stop the server in your host's panel.
2. Unzip the server pack on your PC. You get `dinput8.dll`, the `kiln` folder and the README.
3. Connect with FTP (FileZilla, WinSCP) or open the host's file manager.
4. Go to the folder with `enshrouded_server.exe`.
5. Upload `dinput8.dll` and the whole `kiln` folder into it. Keep the folders as they are.
   Some file managers can unzip: then upload the zip into that folder and unzip it there.
6. Start the server. Wait a minute.
7. Open `kiln/logs/kiln.log` and check it (see [Does it work?](#does-it-work)).

Things that can go wrong on rented hosts:

| You see | Why | What to do |
|---|---|---|
| no `kiln/logs/kiln.log` at all | the server didn't load `dinput8.dll` | check it sits next to `enshrouded_server.exe`, not in a subfolder |
| still no log, and the host runs Linux | some hosts run the Windows server under Wine; Wine needs `WINEDLLOVERRIDES="dinput8=n,b"` | ask the host's support to set it |
| the host deletes or blocks the DLL | some hosts don't allow custom DLLs | ask support; there's no way around it |

We haven't tried Kiln on any specific rented host yet. If you do, please tell us how it went.

## Your own Windows PC

1. Install the dedicated server: Steam's **Enshrouded Dedicated Server** tool (in your Library under Tools), or
   SteamCMD (app 2278520).
2. Stop the server.
3. Unzip the server pack into the server folder. With Steam that's usually
   `...\steamapps\common\EnshroudedServer`.
4. Start the server as usual. Windows loads `dinput8.dll` from that folder by itself. No setting needed.
5. Check `kiln\logs\kiln.log`.

The Kiln Launcher can do this too: its **Servers** page finds Steam's server, installs Kiln, starts and stops it, and
has admin pages.

Honest note: Kiln's server side has been tested under Wine on Linux, not yet on a real Windows PC. It should behave
the same. If it doesn't, please report it.

## Your own Linux server

On Linux the server runs under Wine. `kilnctl` does the whole job: it installs the game with SteamCMD, makes the Wine
prefix, installs Kiln, and runs the server. It doesn't need Steam or a desktop.

You need:

| What | Why |
|---|---|
| `steamcmd` | downloads the server |
| GE-Proton (in a Steam `compatibilitytools.d` folder) or your distro's wine | runs the server |
| Linux 6.14 or newer, for `/dev/ntsync` | fast Wine sync; set `require_ntsync = false` in the instance file to run without |
| `python3`, GNU `tar`, `systemctl` | used by some commands |

From the release, put `kilnctl` in `~/.local/bin` (the systemd units expect it there; on GitHub the file is called
`kilnctl-<version>-linux-x86_64`) and keep `kiln-runtime-<version>.zip` somewhere. Then:

```sh
install -Dm755 kilnctl ~/.local/bin/kilnctl
kilnctl init myserver --dir ~/srv/enshrouded/myserver --port 15637   # the instance file
kilnctl install myserver                                              # the game, through SteamCMD
kilnctl prefix myserver                                               # the Wine prefix
kilnctl deploy myserver --bundle kiln-runtime-<version>.zip           # Kiln's server parts
kilnctl preflight myserver                                            # every check; fix what fails
kilnctl start myserver                                                # returns when the world runs
kilnctl status myserver
kilnctl stop myserver                                                 # saves, then stops
```

To keep it running and start it at boot, use the systemd units from the release (`kiln@.service`,
`kiln-backup@.service`, `kiln-backup@.timer`; on GitHub they come as `kiln-systemd-units-<version>.tar.gz`):

```sh
install -Dm644 -t ~/.config/systemd/user kiln@.service kiln-backup@.service kiln-backup@.timer
systemctl --user daemon-reload
systemctl --user enable --now kiln@myserver                # start now and at login
systemctl --user enable --now kiln-backup@myserver.timer   # optional: a daily savegame backup
loginctl enable-linger "$USER"                             # run without anyone logged in, start at boot
journalctl --user -u kiln@myserver -f                      # the server's output
```

More: [kilnctl.md](kilnctl.md) (every command and check), [linux-server.md](linux-server.md) (the Wine setup).

Running the server under Wine yourself, without kilnctl? Unzip the server pack the same way and start Wine with
`WINEDLLOVERRIDES="dinput8=n,b"`. Don't turn off `mscoree` in that variable: Kiln's .NET part needs it.

## Does it work?

Open `kiln/logs/kiln.log` after the server has started.

| Line in kiln.log | Meaning |
|---|---|
| `build <long hex number>: verified` and `boot complete; handing control to the game` | Kiln runs, mods load |
| `safe idle: build ... is not verified ... the game runs vanilla` | the game was updated and this Kiln doesn't know the new build yet: the server runs without Kiln and its mods (see [Game updates](#game-updates)) |
| no kiln.log | `dinput8.dll` wasn't loaded: wrong folder, or Wine without the setting |

On Linux, `kilnctl status myserver` shows the same build line.

## Running the server: admin options

| Way | Where it works | What you need |
|---|---|---|
| Chat commands (`/help` in game) | everywhere | admin rights: a user group in `enshrouded_server.json` that may kick and ban, or a rule in `kiln/config/permissions.json` |
| The console folder | everywhere, also over FTP | write access to `kiln/control/console/` |
| `kilnctl cmd myserver <command>` | your own Linux server | kilnctl |
| The Kiln Launcher's server pages | a server on the same PC, or a Linux server it reaches over SSH (with kilnctl there) | the launcher; Kiln.Admin (in the server pack) |

**The console folder.** Put a text file ending in `.cmd` into `kiln/control/console/`. One command per line, the
leading `/` is optional, `//` starts a comment:

```
help
creative status
```

Within a few seconds Kiln runs it, deletes the `.cmd` file and writes `<name>.result.json` next to it with every
line's output. The lines run as the server console, with full rights. Over FTP, upload the file as `test.txt` and
then rename it to `test.cmd`: Kiln then never reads a half-uploaded file. Details: [commands.md](commands.md).

## Mods on the server

| Mod type | Where it goes |
|---|---|
| Kiln mod | `kiln/mods/<mod>/` (the folder with its `kiln.mod.json`) |
| Native Kiln mod | `kiln/native/<mod>/` |
| Other loaders' mods (EML, ShroudForge, EMF, ...) | need Kiln's part for that loader. The server pack has the server's default parts (listed in its README); for any other, install the full runtime with kilnctl (`--components`) or the launcher |

Restart the server after adding or removing mods.

What players need depends on the mod. Its `kiln.mod.json` says it in `clients`:

| `clients` | Players need | Players without it |
|---|---|---|
| `none` | nothing: plain Enshrouded works | join normally |
| `kiln` | Kiln; the server sends them the mod's new items and definitions | can't join without Kiln |
| `mod` | Kiln and the mod itself | refused, with a message naming the mod |

When a mod leaves `clients` out, Kiln guesses it from the mod's `side` (a server-only mod: `none`, others: `mod`),
but by default only a value the mod states makes the server turn players away. The joining rules can be changed in
`kiln/config/network.json` (`modSync`); see [network.md](network.md).

**Modpacks.** Players get `mod` mods from your server's modpack: you build it in the Kiln Launcher (Servers > your
server > Modpack), upload the zip anywhere players can download over https, and enter the link. Kiln then offers it
to every player who is refused for a missing mod, and the player's launcher installs it after they agree. The
launcher builds the modpack from the server's own mod folders, so it needs the server on the same PC or over SSH; on a
rented host that means working from a copy of the server folder, which we haven't tested. Details:
[launcher/modpacks.md](launcher/modpacks.md).

## Game updates

Every Enshrouded update changes the server program. Kiln checks the build at every start:

- **Kiln knows the build:** nothing to do.
- **Kiln doesn't know it yet:** the server runs normally, but vanilla: no Kiln, no mods. Your save stays fine. A
  Kiln update with the new build's signatures usually follows. Then update Kiln (below).

Players' games are separate: their Kiln needs to know their game build too. Players with the launcher get that from
Kiln updates in the launcher.

On Linux, `kilnctl update myserver` stops the server, backs up the save, updates the game through SteamCMD, says
whether Kiln knows the new build, and starts the server again. Teaching Kiln a new build itself needs a Kiln source
checkout; without one, wait for the Kiln update like everyone else.

## Updating Kiln

With the server pack:
1. Stop the server.
2. Delete `kiln/dotnet`, `kiln/host`, `kiln/services` and `kiln/sigdb`. They are Kiln's own files; your mods,
   settings and data live in other folders.
3. Upload or unzip the new pack the same way, replacing `dinput8.dll` and the files in `kiln/`.
   Edited `kiln/kiln.json`? Don't overwrite it, or make your change again after.
4. Start the server and check `kiln/logs/kiln.log`.

With kilnctl: `kilnctl stop myserver`, `kilnctl deploy myserver --bundle kiln-runtime-<new version>.zip`, `kilnctl
start myserver`. kilnctl removes files the new version no longer has, keeps your `kiln/kiln.json`, and backs up
anything it overwrites.

## Removing Kiln

With the server pack:
1. Stop the server.
2. Delete `dinput8.dll` and the `kiln` folder.
   To keep your mods' saved data, copy `kiln/data` (and `kiln/config` for their settings) somewhere first.
3. Start the server. It runs vanilla. Your savegame isn't touched.

With kilnctl: `kilnctl bundle uninstall --dir ~/srv/enshrouded/myserver`. It removes Kiln's files and puts back
whatever it replaced, byte for byte (another loader's `dinput8.dll` too). The `kiln` folder stays with your mods,
settings, data and logs; delete it if you don't need them.

## What hasn't been tested

- A real Windows PC: the server side ran under Wine only.
- Rented hosts: none tried yet (GTXGaming, G-Portal or others). Hosts that run Windows servers under Wine need the
  `WINEDLLOVERRIDES` setting from the host.
- A modpack built for a rented server from a local copy of its folder.

For developers: the pack is built by `tools/release/lib/serverpack.py` during a release (releasing.md).
