# Custom game folders

Since 0.2.0-dev.29.17 the Kiln Launcher plays a copy of Enshrouded that no Steam library knows: an older build you keep
for mods, a copy on another drive, a backup of the game folder. Steam's Play only ever starts the copy in its own
library, so for such a folder Kiln starts the game itself.

## Adding one

On Play, use **Add** under the game folder (or **Add a folder** when Kiln found no game), or Settings, Games. Pick any
folder that has `enshrouded.exe`. A folder that is not Steam's own copy of Enshrouded is a **custom folder**: the game
picker and Settings say "custom folder", and the launcher asks how to start it right away, with **Start the game
directly** chosen. "Keep the default" keeps that.

Everything else works as for Steam's copy: Install Kiln, the mods list and profiles, the Library, copy and Z: path
modes, save backups before a start, the start progress bar, the stop watch (exit, copy-back of mod configs in copy
mode, crash reports) and the support report. None of it needs Steam.

## How Kiln starts it

Play, then the game folder line, **Starts directly** / **Starts through Steam** opens the choice for the selected game:

- **Through Steam**: Play asks Steam to start Enshrouded (`steam://rungameid/1203620`), as Steam's Play button does.
  The default for Steam's own copy. For a custom folder this starts Steam's copy, not the folder.
- **Start the game directly**: Play starts this folder's `enshrouded.exe` itself, after the same profile, save backup
  and start steps. The default for a custom folder. On a PC with no Steam installed it is the only way, and the dialog
  does not offer the other.

The choice is kept per game in the launcher's record of the install (`<data>/installs/<installId>.json`, `start`).

## Steam is not needed

A direct start needs no Steam at all: Steam does not have to be installed or running, and the launcher reads no Steam
library, opens no `steam://` link and gives no Steam launch-option advice for it. Adding the folder, Install Kiln,
profiles, the mods list, Play, the start progress, the stop watch, copy-back, crash reports, updates and the support
report all work on a PC without Steam. A Steam library left on a drive (another system's, or with Steam uninstalled)
is a custom folder there too: Steam's Play could not start it.

Kiln starts the exe and nothing else. It doesn't set `SteamAppId` or `SteamGameId`, doesn't write or change
`steam_appid.txt` and doesn't touch `steam_api64.dll` or anything else of the game's own. Whether a given copy of the
game needs anything more to run (Steam, for one) is that copy's business, not Kiln's.

### Windows

Kiln runs `enshrouded.exe` with the game folder as its working folder. Kiln's `dinput8.dll` is next to the exe, which
Windows loads first, so nothing else is set.

### Linux

The game runs under a umu-run, Proton or Wine you pick:

- **Automatic** (the default) picks, in this order, and the dialog says which and why:
  1. `umu-run` (umu-launcher, the usual way to run Proton outside Steam) when it is on your PATH;
  2. else the newest Proton found (GE-Proton and other compatibility tools first, then Valve's Proton builds if a Steam
     library has any);
  3. else `wine` on your PATH.
- Any of those Kiln found. Kiln looks for Proton builds where they are kept without Steam too:
  `~/.local/share/Steam/compatibilitytools.d` (whether or not Steam is installed; umu downloads there as well),
  `~/.steam/root/compatibilitytools.d`, `$XDG_DATA_HOME/Steam/compatibilitytools.d`, Heroic's
  `~/.config/heroic/tools/proton`, Lutris's `$XDG_DATA_HOME/lutris/runners/proton`, the system's
  `/usr/share/steam/compatibilitytools.d`, and, when Steam is installed, its `compatibilitytools.d` folders and
  libraries.
- **Another umu-run, Proton or Wine…**: a `umu-run` program, a Proton folder (the one with the `proton` script) or a
  `wine` program.

What each sets, besides `WINEDLLOVERRIDES=dinput8=n,b` (`dinput8,xinput9_1_0=n,b` when Ultimate ASI Loader is chained;
plain Wine also gets `mshtml=`):

- **umu-run**: `umu-run <exe>` with `WINEPREFIX` (the prefix), `GAMEID=umu-default` and `PROTONPATH` (the newest
  GE-Proton or other compatibility-tools Proton found, so umu downloads no Proton). Without one, `PROTONPATH` is left
  out and umu uses its own Proton, which it downloads at its first start. umu also sets up its runtime at its first
  start, which needs the internet once. Kiln gives umu's own default `GAMEID`, not `umu-1203620`: umu turns
  `umu-<number>` into `SteamAppId`, which Kiln never sets.
- **Proton**: `<proton> run <exe>` with `STEAM_COMPAT_DATA_PATH` (the compatdata folder) and
  `STEAM_COMPAT_CLIENT_INSTALL_PATH`. Proton needs that variable to name a folder: your Steam folder when Steam is
  installed, otherwise an empty folder of Kiln's own, `~/.local/share/kiln-prefixes/_compat-client` (under
  `$XDG_DATA_HOME` when set), made at the first start. Valve's Proton builds expect Steam's runtime container and may
  not start outside Steam on every system; GE-Proton (directly or through umu-run) is the reliable choice.
- **Wine**: `wine <exe>` with `WINEPREFIX`.

Inherited `WINEPREFIX`, `WINEDLLOVERRIDES`, `STEAM_COMPAT_*`, `SteamAppId`, `SteamGameId`, `GAMEID`, `PROTONPATH` and
`LD_PRELOAD` of the launcher's own environment are dropped. The dialog shows the exact command.

**Prefix.** Kiln makes one prefix per game, at the first start: `~/.local/share/kiln-prefixes/<installId>` (under
`$XDG_DATA_HOME` when set). It is outside the launcher's data folder and never moves with it. The Wine prefix is its
`pfx` folder for every runner; the saves of this copy live in it. Or pick a folder: for Proton the folder that holds
`pfx` (a Steam `compatdata` folder), for umu-run and Wine the prefix itself. The first start takes longer while the
prefix is made.

The umu, Proton or Wine output of the last direct start is in `<data>/logs/game-start-<installId>.log`, and the support
report carries it.

Steam Deck Game Mode is not covered.

## Unknown game builds

Kiln's hooks run only on game builds it has a verified record of (sigdb's build records, by the SHA-256 of
`enshrouded.exe`). The launcher checks the folder's exe against the records of the Kiln in the game (or, before Kiln is
installed, the Kiln the launcher would install). For a build it doesn't know, Play and Settings say:

> Kiln doesn't know this game build (`<first 12 hex digits>`). Kiln starts with the game but stays idle: no mods load.

The game itself runs normally. A Kiln update that knows the build fixes it. The support report says the same in its
`unknown-build` finding. Known builds say nothing extra.

**Where the build comes from.** The 12 hex digits are the start of the SHA-256 of `enshrouded.exe` (on a dedicated
server `enshrouded_server.exe`). Kiln looks for `kiln/sigdb/builds/<client|server>/<sha256>.json` with
`"verified": true`. The game's own build number (its `svn`) is not enough: two exes
of the same game version can differ, and Kiln's hooks go to exact places in one exe.

**What to do** (GitHub issue 99):
- A Steam copy: in Steam, Enshrouded > Properties > **Betas** should be "None" (the public branch), and Installed Files >
  **Verify integrity of game files** puts back an `enshrouded.exe` a tool changed on disk. Kiln knows the current
  public Steam builds.
- A copy from another store or launcher: say which one in an issue, so a record for that build can be added.
- An older game version: update the game. Kiln has no records for builds older than the ones it was checked on.
- At your own risk: `"policy": "any"` in `<game folder>/kiln/kiln.json` (or only for the game client:
  `"client": { "policy": "any" }`, [client.md](../client.md#kilnjson-per-side)) makes Kiln resolve its signatures on the
  unknown exe and run anyway, as the test labs do. A signature can land on the wrong place there, which can crash the
  game or damage a save: back up your saves first, and set it back to `"verified-only"` if anything misbehaves. Kiln
  never falls back to another build's record by itself.
