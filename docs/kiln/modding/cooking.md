# Cooking: the files a mod makes from each player's game

Some of what a mod adds is made from the game's own data: a model of yours is built on the game's model and material
types, a clip is retargeted onto the game's skeleton, a sound bank uses the game's sound types, an Impact program is a
clone of one of the game's programs. Those files are **cooked** on each machine from that machine's game, and never
shipped: a download carries the mod's sources and recipes, and each PC (and each dedicated server) makes the rest. This
page says how, for authors and for players. The tools themselves: [models.md](models.md), [animations.md](animations.md),
[sounds.md](sounds.md), [impact.md](impact.md).

## What a mod needs to be cooked

A mod has something to cook when it has a recipe (`<mod>/assets/cook.json`, `<mod>/impact/cook.json`, any
`<mod>/<folder>/cook.json`) or blocks whose looks or sounds name files (`blocks/*.json`). Everything a cook makes goes
into the mod's `assets/cooked` folder and nowhere else: the tools refuse a recipe whose `out` points elsewhere, a step
name that would lead out of it, and a link anywhere on the way. Outside it a cook writes only `assets/assets.json` (the
manifest, which the mod ships: the author's cook writes it, a player's cook finds it as shipped) and
`blocks/<name>.materials.json` (made from a glTF's materials, the same bytes everywhere).

`assets/cooked` is not part of the mod: its identity (the hash the Library, a modpack and a server know it by,
[../launcher/contracts.md](../launcher/contracts.md) 5.11) leaves that folder out, so a mod cooked on a server and in a
player's game is the same mod. `kiln pack` leaves it out of the zip, the Library leaves it out of an import, a modpack
that carries it is refused, and a server sync copies a mod without it.

## Only what this PC cooked is loaded

A cook Kiln runs records what it wrote, in the game it read: `<game>/kiln/state/cook-receipts/*.json`, one receipt per mod
folder, recipe and tool, with the SHA-256 of every file it put into `assets/cooked`. **kiln.assets loads a file of a mod's
`assets/cooked` only when a receipt of that game lists its bytes.** A cooked file that came any other way (in an archive,
from a modpack built by an older Kiln, copied by hand into `kiln/mods`, left in a Library entry by an older Kiln, in a
folder handed to `Assets.LoadManifest`) is not loaded; the log says which and what to do:

```
[W] assets: <mod folder>: 2 cooked files not loaded (cooked/blade.bin, cooked/blade.blob): no cook of this game made them. Cooked files are made on each PC from its own game, never taken from a download or a copy: the launcher cooks the mod before the game starts; on a dedicated server run kiln cook <mod folder> --game <server folder>
```

The rule is about provenance, not trust in an author: a cooked file is game data made for one game build, and only a cook
on this machine knows it was made from this machine's game. A file whose bytes a cook here did make (the same mod cooked
for another folder of this game) loads. What it does not cover: the indexes the cook writes next to the binaries
(`assets/cooked/audio.json`, read by Kiln.Audio and the quest packs) name GUIDs only and are read as they are; the files
they point at go through kiln.assets and its check.

A development server can turn the check off in its `kiln/kiln.json`: `"cooked": "any"` (top level, or in its `"server"`
section). Its log then says so at every start. `tools/deploy-server.sh` sets it for labs (`--cooked local` keeps the
rule). A player's game never needs it: the launcher cooks.

## Who cooks, and when

- **The launcher**, for a player: before the game starts (Play, on the desktop and on the Deck), it applies the active
  profile if it changed, then cooks every enabled Kiln mod of that profile that needs it, in the folder the game loads it
  from (the Library entry, or Kiln's copy in the game). A mod needs a cook when it was never cooked for this game (it
  was installed or switched on), it was updated, the game was updated, its last cook failed, or a file its last cook
  made is gone or changed (checked against its receipt). Each such mod is cooked from nothing: its `assets/cooked` is
  emptied first and every recipe runs, whatever the recipe's own up-to-date check says (that check reads the mod's files,
  not the game). A failed cook stops neither the other mods nor the start: one notice names the mod with the tools'
  own words, and the mod runs without the files it would have made. "Cook again" in a mod's menu (the Deck: Y on its
  row) cooks one mod whenever you want. Details: [../launcher/app.md](../launcher/app.md), "Cooking mods".
- **`kiln cook`**, for an author and a server operator: `kiln cook [mod folder] [--game DIR] [--check] [--force]`
  cooks every recipe whose own check says it is needed (`--force`: every recipe, after a game update), and records what
  it wrote in the game folder it read (`--game`, `$KILN_GAME`, else the installed client), if that folder has Kiln
  (`kiln/`). `kiln build` runs the same cook first. [cli.md](cli.md).
- **A dedicated server's operator** runs `kiln cook <server>/kiln/mods/<mod> --game <server>` for each mod with a recipe,
  after installing or updating the mod, and **after updating Kiln to 0.2.0-dev.27 or newer** (cooks of older Kilns left
  no receipt, so their files are not loaded until cooked again) and after a game update.
  [../dedicated-servers.md](../dedicated-servers.md#mods-on-the-server).

The cook tools run by hand (`python3 tools/modelcook/modelcook.py ...`) write no receipt: what they make is for a
development server with `"cooked": "any"`, or is cooked again through Kiln.

## For authors

- Ship the recipe, the sources and the manifest the cook writes (`assets/assets.json`, with `--update-manifest`, which
  `kiln build` and `kiln cook` pass). Never ship `assets/cooked`: `kiln pack` leaves it out.
- Test with your client: `kiln build` cooks against your installed game and records the cook there; `kiln deploy` to
  your game's `kiln/mods` copies files your cook made, which load. Deploying to a server copies files cooked from your
  client: the server does not load them (`kiln deploy` says so) until you run `kiln cook <deployed folder> --game
  <server>`, or the server has `"cooked": "any"`.
- A cook that fails in a player's launcher shows the tools' last lines to the player: make your recipe's errors say what
  is missing (a game GUID a game update removed, a glTF the cook refuses).

## For players

You do not run anything: the launcher cooks before the game starts. On Linux it needs `python3` (3.10 or newer) with a
zstd decoder (Python 3.14 has one; else the system's libzstd, which nearly every distribution has, or the `zstandard`
package); the notice says what to install when one is missing. On Windows the launcher brings its own Python. A mod that
could not be cooked still loads, without its models, animations or sounds of its own.
