# Project setup: Kiln.Sdk, templates, build targets

Wiki: [dev kit](https://frostdev.io/kiln/wiki/dev-kit), [your first mod](https://frostdev.io/kiln/wiki/first-mod).

Three NuGet packages at the MDK version (0.2.0): `Kiln.Api`, `Kiln.Sdk`, `Kiln.Templates`. They aren't on nuget.org yet: a Kiln
checkout builds them into a local feed. Pass it as `--feed` or `$KILN_FEED`.

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

There's no quest template: add `quests/*.json` to a `mod` or `datapack`.

The project file:
```xml
<Project Sdk="Kiln.Sdk/0.2.0">
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
| `KilnApiVersion` | the SDK's (0.2) | `apiVersion` |
| `KilnSide` | server | `side` |
| `KilnClients` | from the side: server none, else mod | `clients` |
| `KilnEntryType` | the only public `Mod` subclass | `entryType` |
| `KilnAdvanced` | false | `advanced` |
| `KilnFeatures`, `KilnExports` | | `features`, `exports` |
| `KilnTestsAutoRun`, `KilnTestTimeoutSeconds` | | `tests` |

Not in the manifest:
| Property | Does |
|---|---|
| `KilnServices` | services to reference: Abilities, Assets, Audio, Build, Chat, Client, Combat, Content, Creatures, Gameplay, Items, Network, Permissions, Players, Quests, Saves, Settings, Skills, Spawn, Time, Ui, World; and `Testing` |
| `KilnServer`, `KilnModsDir` | deploy target |
| `KilnReload` | KilnDeploy also reloads the mod |
| `KilnModFolders` | data folders copied into the mod; default `content;assets;skills;quests;icons;impact;audio` |
| `KilnPackExclude` | left out of the zip; default `assets/cooked/**` |
| `KilnAnalyzers` | false: no KILN1xx |
| `KilnGameIds`, `KilnCatalog` | game-id constants |

Items: `KilnDependency` (`Version`, `Optional`), `KilnLoadBefore`, `KilnLoadAfter`, `KilnIncompatible` (`Version`).

## kiln.mod.json
None in the project folder: the build generates one. One there: the build validates and copies it. The loader's own parser checks it.
`KILN001` invalid, `KILN002` wrong `entryAssembly`, `KILN004` unknown service, warning `KILN003`. Fields:
[manifest.md](manifest.md).

## Targets
`kiln` drives these: `dotnet build` (the output folder is the mod folder), `-t:KilnDeploy -p:KilnServer=<server>`, `-p:KilnReload=true`,
`-t:KilnPack`.

After re-packing the same SDK version, clear `~/.nuget/packages/kiln.*/0.2.0`, or you build against the old packages.

## A finished mod
```
kiln/mods/my-mod/
  kiln.mod.json
  MyMod.dll  MyMod.pdb       (+ NuGet dependencies)
  content/*.json             data packs
  skills/*.json              talents
  quests/*.json              quests, NPCs, dialogue
  assets/assets.json         icons, textures, resources
  assets/cook.json           recipe; assets/cooked/ is made on the player's machine
  audio/  impact/  icons/    sources
```
A `.disabled` file in the folder switches the mod off at the next start.
