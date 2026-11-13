# Definitions baked at build

The game reads its quest journal, its skill tree, its VFX and its materials once, when it starts, before a mod's code
runs. What a mod defines in **data files** is there at the first start. What it defines in **code** used to be written to
Kiln's data folder at run time and built from the next start. Since 0.2.0-dev.27, Kiln.Sdk's build bakes what the code
defines into the mod. The first start then has it too, on the server and on every player's game that installs the mod.

| Kind | Defined in code with | Baked into the mod as | Read at start by |
|---|---|---|---|
| Quests, quest NPCs, dialogues shown by the game | `Quests.Define`, `Quests.DefineNpc`, `QuestBlock` and `DialogueBlock` in C# | `quests/generated/<id>.json` | kiln.quests' start-up build ([quests.md](quests.md)) |
| Nodes in the game's skill tree | `GameTree.Define` / `GameTree.DefineJson` | `skills/<id>.generated.json` (`"gameTree"`) | kiln.skills ([../services/skills.md](../services/skills.md), part A) |
| VFX and materials | `Blocks.Define(name, VfxBlock)` / `MaterialBlock` | `blocks/generated/<id>.json` | kiln.vfx ([vfx.md](vfx.md), [materials.md](materials.md)) |
| World programs | nothing to bake: they are declared in `kiln.programs.json`, which the SDK already copies | | kiln.world ([ecs-programs.md](ecs-programs.md)) |
| Affix kinds and pools | nothing to bake: they are Kiln's own, not the game's, and defined when the mod loads (`Blocks.Define`, `Affixes.Define`, `blocks/*.json`) | | Kiln.Blocks ([affixes.md](affixes.md)) |

`<id>` is the mod's id. Each file's `"comment"` says the build wrote it, and Kiln goes by that comment.

## How it runs
The target `KilnBake` (`managed/Kiln.Sdk/Sdk/Kiln.Bake.targets`) runs after `KilnManifest`. It is on by default when
`KilnServices` names `Quests`, `Skills`, `Blocks` or `Vfx`. It runs the built mod with Kiln's own loader under
[KilnHarness](unit-tests.md), with no game: the mod's `OnLoad`, the server's start and its first 10 steps. During that run,
Kiln.Quests, Kiln.Skills and Kiln.Vfx record what the code defines instead of handing it to the game. The step then writes
the files into the output folder, so `KilnDeploy` and `KilnPack` ship them.

```xml
<PropertyGroup>
  <KilnBake>false</KilnBake>                          <!-- no bake: the next start, as before -->
  <KilnBakeSteps>30</KilnBakeSteps>                   <!-- define something in a later step? run longer (default 10) -->
  <KilnBakeTimeoutSeconds>300</KilnBakeTimeoutSeconds> <!-- the whole bake's deadline (default 120) -->
</PropertyGroup>
```

- **The mod's own file wins.** If the project ships its own file at one of these paths, the bake leaves it alone and
  says so.
- **What the bake wrote never outlives what the code defines.** A file of an earlier bake goes from the output folder
  in these cases:
  - the code no longer defines anything of that kind;
  - the mod does not load without the game (below);
  - the bake fails;
  - the bake is turned off (`KilnBake=false`);
  - the file is named for another mod id (left from a rename).

  `KilnDeploy` also removes the three files from the deployed folder before it copies, so one the output folder no
  longer has does not stay on the server. What goes is told by the file's own marker, not by a list, so an author's file
  now at a path an earlier bake wrote stays, and nothing is left when the build's bookkeeping is gone.
- **A mod that does not load without the game** is not baked. A build line says so (`Kiln.Sdk.Bake: <id>: not baked:
  ...`). That is a limit of the bake, not a fault of the mod, and the build goes on.
- **Warning `KILN030`:** a node whose icon cannot be baked, or an error the mod logs while it is baked.
- **Error `KILN032`:** a bake that crashes, or does not end within `KilnBakeTimeoutSeconds` (an `OnLoad` that never
  returns). The bake process is ended, and the build fails, naming `KilnBake=false`. Nothing of an earlier bake is left:
  the process removes it at its own deadline, and the build removes it again by marker after any failure.
  A thread the mod leaves running does not keep the bake alive: the process ends when the bake is done.
- **It bakes again** when the mod, its data files, the SDK, `KilnBakeSteps` or `KilnBakeTimeoutSeconds` change, or a
  file the last bake wrote is gone from the output folder.
- **An IDE's design-time build never bakes**, so it never runs the mod's code.
- **The mod's code runs with invariant globalization**, as Kiln.Host does in the game. The files are the same on every
  build machine, and the same definitions in another order give the same file.

**Why it runs the code.** The other route was attributes read by a source generator. A generator sees only constants. A
mod computes its definitions: a loop over its own table, names built from strings, a helper library's builders, blocks
compiled from JSON. Only the code that runs in the game gives what the game gets, so the bake runs that code.

## At run time
Nothing changes for a mod built with an older SDK. A mod built with this SDK defines the same things again when it runs,
and Kiln compares them with what it shipped:

| Kind | Same as baked | Not the same |
|---|---|---|
| Quests | kiln.log: `quests: <id>: what it defines in code matches quests/generated/<id>.json`; Kiln's data file keeps only what the baked file does not have | warning naming each quest, NPC or dialogue that differs, is new, or is gone. The baked quests, NPCs and dialogues win at every start: Kiln's data file leaves out every id the baked file has, so the game shows the baked version |
| Game-tree nodes | the node is left as kiln.skills read it at boot: in the tree, nothing cached for the next start | warning per node. The tree keeps the baked node, read at start, and the code's version is defined as before (pending, cached). The baked file still wins at every start |
| VFX, materials | the builder reports the block `registered` (the same definition it built at start) | it is kept in `kiln/data/vfx/pending/<id>/` as before. At start the baked one is read first, and the builder keeps the first of two blocks with one name (kiln.log says so). The warning and the toast say to rebuild the mod, not to restart |

The rule in all three cases: **the baked file is part of the mod and wins at start.** If code defines something else after
the build, rebuild the mod to bring the file up to date. A mod whose definitions depend on settings, other mods or the
game's data should set `KilnBake=false`.

A mod's folder is input from the internet, so kiln.vfx reads its JSON with limits. Nothing is allocated or parsed past
them.

| Folder | Entries looked at | JSON files | Each | In all | Nesting |
|---|---|---|---|---|---|
| `blocks/generated/` (baked) | 1024 | 16 | 4 MiB | 16 MiB | 64 |
| `blocks/` (the author's data files) | 4096 | 256 | 16 MiB | 64 MiB | 64 |

- **Both readers:** the limits of `blocks/` hold for kiln.vfx's reader at boot and for Kiln.Blocks' managed reader of
  the same files (`BlockJson.Read`), which also reads no file that is reached through a link or lies outside the mod's
  folder. The managed reader checks a path for links and then opens it, so on every system a file swapped between the
  two could still be read; it stays under the size limits.
- **Why these numbers:** real block files are a few KiB and a handful per mod; the repository's largest is 6 KiB, and its
  most in one mod is 6. The author's limits are generous so no real mod meets them, and use the same size and depth
  kiln.quests' start-up build takes for the same files.
- **Too many files:** the folder listing stops there and none of that folder is read.
- **Links:** each folder and file is opened first, then checked: its final path must lie inside the mod's folder, and
  nothing on the way down from the mod's folder may be a link or reparse point. Only then is it read, from that handle.
  So a link, a junction (on `blocks` itself too) or a path swapped between the check and the read cannot lead out.
- **Under Wine** (the Linux server, Proton): Wine resolves no link in a handle's final path. It marks a link to a folder
  as a reparse point, so that link is refused, on `blocks` itself too. A link to a file looks like a plain file there, so
  it is read, within the same size, count and depth limits. Lab, GE-Proton: `mods/tests/bake-test/fixtures/make-limit-probes.sh`.
- **Kiln's managed side:** it reads a baked file only to compare it (the warnings above), with the same bounds: at most
  4 MiB, no link on the way, nothing outside the mod's folder.

Each refusal is a line in kiln.log (`vfx: <id>: blocks/<file>: not read: ...`), and the file is left out.

## What is not baked
- **A mod that does not load without the game.** The harness has no game, and a block that lowers onto kiln.assets or
  kiln.content throws there: statuses, effects, items, spells and talent blocks. A talent block needs kiln.assets for its
  generated Impact program. The build says `not baked: the mod does not load without the game` and bakes nothing for that mod; set `KilnBake=false`
  to skip the try.
- **A node whose `IconFile` lies outside the mod's folder.** That path is the build machine's, not the mod's. A path inside
  the folder is baked relative to it, as kiln.skills reads a mod file's paths. A relative path in a definition from code
  is the kiln folder's, as kiln.skills reads code definitions, and is resolved there first.
- **A client-only mod** (`"side": "client"`): the harness is a server.
- **A mod whose required dependency is not there**, since the harness loads this mod only. **Definitions made after the
  first steps:** raise `KilnBakeSteps`.
- **NPC blocks from code** (`kiln/data/kiln-quests/native/<id>.blocks.json`), **C# world-table blocks** (material tweaks,
  building and terrain materials) and **crafting props from C#** keep their next-start rule. Their data-file forms are there
  at once.

## Checked
- `managed/tests/Kiln.Sdk.Tests` (25 tests):
  - the recorders, the files and their marks, an author's file left alone, a stale file removed, and a mod that does
    not load;
  - `mods/tests/bake-test`, built with the step: its three files, and the same files from a second bake;
  - its quests under KilnHarness, equal to the baked ones;
  - a run-time `GameTree.Define` against a fake kiln.skills: the node as baked is not sent again, and a changed one is;
  - fixture mods in the bake's own process:
    - a thread `OnLoad` leaves running does not keep the process alive;
    - an `OnLoad` that never returns ends at the deadline (`KILN032`);
    - a throwing `OnLoad`, and one that needs kiln.assets, leave nothing of an earlier bake (a renamed id's file
      included) and keep the author's;
  - the same file from the same definitions in another order, and on a German build machine;
  - malformed and other SDKs' files left alone, relative icons, baked dialogues ahead of Kiln's data file, and the
    "rebuild" note;
  - the targets on a stub project through `dotnet msbuild`: turned off, deploy, a missing output, changed settings,
    design-time.
- Lab, `mods/tests/bake-test` on a dedicated server (server 26430253), started with an empty `kiln/data` and
  `kiln/config/vfx.json {"sim": true}`:
  - At that first start, the two quests are in the game's journal (`NativeQuests.ShownByGame`), the node is in the
    game's tree (`in-tree`, read from `skills/bake-test.generated.json`, and `GameTree.Define` left it so), and the VFX
    is `registered`.
  - Kiln keeps nothing for the next start of any kind: 10 PASS.
  - With the lab's copies of the three files edited to differ from the code, each kind warns, the VFX warning says to
    rebuild, and Kiln writes no quests data file (every id is the baked file's).
- A freshly packed SDK (`managed/Kiln.Sdk/pack.sh` into a local feed) and a mod restored from it:
  - the bake runs from the package (`bake/` beside Kiln.Api's `kiln/lib`), and `KilnDeploy` ships the files;
  - with the quests taken out of the code, their file goes from the output and the deployed folder;
  - with `KilnBake=false`, the rest goes too.
- Not checked: a player's game (a client reads the same files with the same readers).
