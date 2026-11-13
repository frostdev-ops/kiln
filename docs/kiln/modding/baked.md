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

`<id>` is the mod's id. Each file's `"comment"` says the build wrote it, and Kiln goes by that comment.

## How it runs
The target `KilnBake` (`managed/Kiln.Sdk/Sdk/Kiln.Bake.targets`) runs after `KilnManifest`. It is on by default when
`KilnServices` names `Quests`, `Skills`, `Blocks` or `Vfx`. It runs the built mod with Kiln's own loader under
[KilnHarness](unit-tests.md), with no game: the mod's `OnLoad`, the server's start and its first 10 steps. During that run,
Kiln.Quests, Kiln.Skills and Kiln.Vfx record what the code defines instead of handing it to the game. The step then writes
the files into the output folder, so `KilnDeploy` and `KilnPack` ship them.

```xml
<PropertyGroup>
  <KilnBake>false</KilnBake>        <!-- no bake: the next start, as before -->
  <KilnBakeSteps>30</KilnBakeSteps> <!-- define something in a later step? run longer (default 10) -->
</PropertyGroup>
```

- **The mod's own file wins.** If the project ships its own file at one of these paths, the bake leaves it alone and
  says so.
- **Stale files go.** If a file came from an earlier bake and the code no longer defines anything of that kind, it is
  removed.
- **The build never fails for the bake.** A mod that does not load without the game is not baked, and a build line
  says so (`Kiln.Sdk.Bake: <id>: not baked: ...`). That is a limit of the bake, not a fault of the mod. A node whose icon
  cannot be baked, an error the mod logs while it is baked, or a bake that cannot run is a warning `KILN030`. Either
  way, that part comes from the next start, as without the bake.

**Why it runs the code.** The other route was attributes read by a source generator. A generator sees only constants. A
mod computes its definitions: a loop over its own table, names built from strings, a helper library's builders, blocks
compiled from JSON. Only the code that runs in the game gives what the game gets, so the bake runs that code.

## At run time
Nothing changes for a mod built with an older SDK. A mod built with this SDK defines the same things again when it runs,
and Kiln compares them with what it shipped:

| Kind | Same as baked | Not the same |
|---|---|---|
| Quests | kiln.log: `quests: <id>: what it defines in code matches quests/generated/<id>.json` | warning naming each quest, NPC or dialogue that differs, is new, or is gone. At start, the baked quests and NPCs win over what Kiln keeps from code, so the journal shows the baked version. A dialogue shows the code's version from the next start |
| Game-tree nodes | the node is left as kiln.skills read it at boot: in the tree, nothing cached for the next start | warning per node. The tree keeps the baked node, read at start, and the code's version is defined as before (pending, cached). The baked file still wins at every start |
| VFX, materials | the builder reports the block `registered` (the same definition it built at start) | it is kept in `kiln/data/vfx/pending/<id>/` as before. At start the baked one is read first, and the builder keeps the first of two blocks with one name (kiln.log says so) |

The rule in all three cases: **the baked file is part of the mod and wins at start.** If code defines something else after
the build, rebuild the mod to bring the file up to date. A mod whose definitions depend on settings, other mods or the
game's data should set `KilnBake=false`.

## What is not baked
- **A mod that does not load without the game.** The harness has no game, and a block that lowers onto kiln.assets or
  kiln.content throws there: statuses, effects, items, spells and talent blocks. A talent block needs kiln.assets for its
  generated Impact program. The build says `not baked: the mod does not load without the game` and bakes nothing for that mod; set `KilnBake=false`
  to skip the try.
- **A node whose `IconFile` lies outside the mod's folder.** That path is the build machine's, not the mod's. A path inside
  the folder is baked relative to it, as kiln.skills reads a mod file's paths.
- **A client-only mod** (`"side": "client"`): the harness is a server.
- **A mod whose required dependency is not there**, since the harness loads this mod only. **Definitions made after the
  first steps:** raise `KilnBakeSteps`.
- **NPC blocks from code** (`kiln/data/kiln-quests/native/<id>.blocks.json`), **C# world-table blocks** (material tweaks,
  building and terrain materials) and **crafting props from C#** keep their next-start rule. Their data-file forms are there
  at once.

## Checked
- `managed/tests/Kiln.Sdk.Tests` (12 tests):
  - the recorders, the files and their marks, an author's file left alone, a stale file removed, and a mod that does
    not load;
  - `mods/tests/bake-test`, built with the step: its three files, and the same files from a second bake;
  - its quests under KilnHarness, equal to the baked ones;
  - its node's run-time request, equal to the baked node (and a changed one is not).
- Lab, `mods/tests/bake-test` on a dedicated server (server 26430253), started with an empty `kiln/data`:
  - At that first start, the two quests are in the game's journal (`NativeQuests.ShownByGame`) and the node is in the
    game's tree (`in-tree`, read from `skills/bake-test.generated.json`). `GameTree.Define` left it so.
  - The quests Kiln.Quests writes at run time equal the baked ones, and kiln.skills keeps nothing for the next start:
    8 PASS.
  - With `kiln/config/vfx.json {"sim": true}`, kiln.vfx builds the baked VFX at boot (`built 1 Kiln VFX`). The block
    is `registered` at the first start, and nothing is kept in `kiln/data/vfx/pending`: 10 PASS.
  - With the lab's copies of the three files edited to differ from the code: each kind warns (VFX as "new to this
    game"), the code's versions are kept as before, and at the next start the baked files still win (kiln.vfx: "a
    block of this name is already defined; the first stays").
- Not checked: a player's game (a client reads the same files with the same readers), the packed SDK on a machine
  without the repository beyond one build from the local feed (`managed/Kiln.Sdk/pack.sh`).
