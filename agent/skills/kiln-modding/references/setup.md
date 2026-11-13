# Project setup: Kiln.Sdk, templates, build targets

Wiki: [dev kit](https://frostdev.io/kiln/wiki/dev-kit), [your first mod](https://frostdev.io/kiln/wiki/first-mod).

`Kiln.Api`, `Kiln.Sdk` and `Kiln.Templates` share the full MDK pack version: `0.2.0-dev.27` for this release, or a unique
`0.2.0-dev.local.<date>.<time>` for a local pack. `0.2.0` is only the source's base version. These packages use a local feed,
not nuget.org: pass the MDK feed as `--feed` or `$KILN_FEED`. Use the actual pack version when editing a project by hand.

## New project
```sh
kiln new mod MyMod --feed <feed> --server <server folder> --set tests=true
# same thing:
dotnet new kiln-mod -n MyMod --feed <feed> --server <server folder> --tests --services "Players;Chat"
```

| Template | Gives you | Side, clients |
|---|---|---|
| `mod` | a code mod | server, none |
| `datapack` | `content/*.json` | server, none |
| `items` | new items and recipes | both, mod |
| `creature` | a creature clone | both, kiln |
| `ability` | an ability; `impact=true` adds a program | both, none |
| `ui` | a game UI overlay | client, mod |
| `model` | a glTF model and recipe | both, mod |
| `sound` | sounds, cues and recipe | both, mod |
| `talents` | a talent tree | both, mod |
| `native` | a C++ mod | both |
| `test` | a test mod; `target=<mod>` | server, none |
| `spell`, `vfx`, `body` | a spell, an effect, a creature with its own body, from blocks | both; `clients` as the blocks need (KILN112) |
| `unit-test` | an xunit project on `Kiln.TestKit` (dev.26+) | |
| dev.27+: `events`, `saved-data`, `rules` (NoClip too), `network` | one topic each | server, none; `network` both, mod |
| dev.27+: `screen-effect` | screen effects as blocks | both; `clients` as the blocks need |
| dev.27+ advanced: `hooks`, `shader`, `world-program` | a game-function detour, shader hooks, a program in the game's scheduler | `hooks`, `shader` advanced; `shader` both, mod |

**dev.20+ block templates:** `item-blocks`, `world-blocks`, `creature-blocks`, `npc`, `cosmetic`,
`building-material`, `ability-blocks`, `quest`; `ui --set blocks=true` selects UI as data. Use `kiln new --list` from the
installed MDK to confirm availability. These add typed blocks and/or `blocks/*.json`; keep the form you need. `items` and
`creature` remain the clone-based path when a block has no field for the change. [blocks.md](blocks.md).

**dev.26+:** `dotnet new kiln-unit-test -n MyMod.Tests --mod ../MyMod/MyMod.csproj` makes an xunit project on
the `Kiln.TestKit` package ([testing.md](testing.md)). `Game` in `KilnServices` references `Kiln.Game` (`Kiln.Ecs` typed reads);
the build then writes `kiln.gametypes.json` beside the mod, which Kiln checks at load. Release packs carry their full version
(`0.2.0-dev.26`) and an SDK feed folder (`sdk/feed/`). The launcher can move its data folder (Settings, Library, Data
folder); after a move, delete a native mod project's `build` folder once (CMake keeps the old SDK path). Not run in a real
launcher window yet.

The project file:
```xml
<Project Sdk="Kiln.Sdk/0.2.0-dev.27">
  <PropertyGroup>
    <KilnModId>my-mod</KilnModId>
    <Version>1.0.0</Version>
    <KilnServices>Players;Chat</KilnServices>
  </PropertyGroup>
  <ItemGroup>
    <KilnDependency Include="sample-lib" Version="^1.2" />
  </ItemGroup>
</Project>
```

## Properties (each maps to a manifest field)
| Property | Default | Manifest |
|---|---|---|
| `KilnModId` | lower-case `AssemblyName` | `id` |
| `KilnModName`, `Version` | `AssemblyName`, 1.0.0 | `name`, `version` |
| `Authors`, `Description`, `PackageLicenseExpression`, `PackageProjectUrl` | | `authors`, `description`, `license`, `url` |
| `KilnApiVersion` | the SDK's (0.2.2) | `apiVersion` |
| `KilnSide` | server | `side` |
| `KilnClients` | from the side: server none, else mod | `clients` |
| `KilnEntryType` | the only public `Mod` subclass | `entryType` |
| `KilnAdvanced` | false | `advanced` |
| `KilnFeatures`, `KilnExports` | | `features`, `exports` (KILN121 counts `KilnFeatures` as asked) |
| `KilnTestsAutoRun`, `KilnTestTimeoutSeconds` | | `tests` |

Not in the manifest:
| Property | Does |
|---|---|
| `KilnServices` | services to reference: Abilities, Assets, Audio, Blocks, Build, Chat, Client, Combat, Content, Creatures, Gameplay, Hooks, Items, Network, Permissions, PlayerAnim, Players, Quests, Rules, Saves, Settings, Skills, Spawn, Time, Ui, Vfx, World; `Game` (`Kiln.Ecs` types); and `Testing` |
| `KilnServer`, `KilnModsDir` | deploy target |
| `KilnReload` | KilnDeploy also reloads the mod |
| `KilnModFolders` | data folders copied into the mod; includes `blocks`, content, assets, skills, quests, icons, Impact and audio sources; inspect the installed SDK for the full list |
| `KilnPackExclude` | left out of the zip; default `assets/cooked/**` |
| `KilnAnalyzers` | false: no KILN1xx |
| `KilnGameIds`, `KilnCatalog` | game-id constants |
| `KilnBake` | dev.27+: false turns off baking code-defined quests, game-tree nodes, VFX and materials into the mod (on when `KilnServices` names Quests, Skills, Blocks or Vfx); `KilnBakeSteps`, `KilnBakeTimeoutSeconds`. `docs/kiln/modding/baked.md` |
| `KilnWrites` | dev.27+: false stops the build adding `World.Write`/`Update` types to `"writes"` |

Items: `KilnDependency` (`Version`, `Optional`), `KilnLoadBefore`, `KilnLoadAfter`, `KilnIncompatible` (`Version`).

## kiln.mod.json
None in the project folder: the build generates one. One there: the build validates and copies it. The loader's own parser checks it.
`KILN001` invalid, `KILN002` wrong `entryAssembly`, `KILN004` unknown service, warning `KILN003`. Fields:
[manifest.md](manifest.md).

## Targets
`kiln` drives these: `dotnet build` (the output folder is the mod folder), `-t:KilnDeploy -p:KilnServer=<server>`, `-p:KilnReload=true`,
`-t:KilnPack`.

NuGet caches each package id/version. Give each local pack a new full version and update the project to it; re-packing
the same version can leave the project using its old cached packages.

## A finished mod
```
kiln/mods/my-mod/
  kiln.mod.json
  MyMod.dll  MyMod.pdb       (+ NuGet dependencies)
  content/*.json             data packs
  skills/*.json              talents
  blocks/*.json              building blocks (including quests/UI on dev.20+)
  quests/*.json              quests, NPCs, dialogue
  kiln.programs.json          optional dev.20+ boot ECS declarations
  assets/assets.json         icons, textures, resources
  assets/cook.json           recipe; assets/cooked/ is made on the player's machine
  audio/  impact/  icons/    sources
```
A `.disabled` file in the folder switches the mod off at the next start.
