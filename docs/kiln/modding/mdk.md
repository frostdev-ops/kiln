# The MDK: Kiln.Sdk, packages, templates

Three NuGet packages, built into a local feed by `KILN_OUT=… managed/Kiln.Sdk/pack.sh` (`$KILN_OUT/nuget`):

| Package | Contents |
|---|---|
| `Kiln.Api` | `lib/net10.0/Kiln.Api.dll` (+ XML docs): what every mod compiles against. `kiln/lib/Kiln.<Service>.dll` and `Kiln.Testing.dll`: referenced only when a mod asks for them (`KilnServices`) |
| `Kiln.Sdk` | MSBuild SDK (`Sdk/Sdk.props`, `Sdk/Sdk.targets`, `Sdk/Kiln.Mod.targets`) and its task `tasks/net10.0/Kiln.Sdk.Tasks.dll`; the Roslyn analyzers and the game-id generator (`analyzers/dotnet/cs/`); the native SDK (`native/`) |
| `Kiln.Templates` | `dotnet new kiln-mod` and the other kinds in [Templates](#templates) |

All three carry the MDK version (`managed/Kiln.Sdk/Sdk/Kiln.Version.props`, today 0.2.0, matching the mod API
`Kiln.Mods.ApiVersion`).

## A mod project
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
The SDK sets `net10.0`, nullable, implicit usings, `EnableDynamicLoading`, no runtimeconfig/deps files; references
Kiln.Api and the listed services with `ExcludeAssets=runtime` (they come from `kiln/host` at run time and are never
copied); copies NuGet dependencies next to the mod (they load into the mod's own context).

### Properties and items
| Property | Default | Manifest field |
|---|---|---|
| `KilnModId` | lower-case `AssemblyName`, other characters → `-` | `id` |
| `KilnModName` | `AssemblyName` | `name` |
| `Version` | 1.0.0 | `version` |
| `Authors`, `Description`, `PackageLicenseExpression`, `PackageProjectUrl` | | `authors`, `description`, `license`, `url` |
| `KilnApiVersion` | the SDK's (0.2) | `apiVersion` |
| `KilnSide` | server; `both` when `KilnServices` has `Blocks` | `side` |
| `KilnClients` | from the side: server `none`, otherwise `mod`; with `Blocks` in `KilnServices`, what the blocks need ([blocks.md](blocks.md)) | `clients`: what joining players need (`none`, `kiln`, `mod`; manifest.md) |
| `KilnEntryType` | | `entryType` |
| `KilnAdvanced` | false | `advanced` |
| `KilnFeatures`, `KilnExports` | | `features`, `exports` (`;` lists) |
| `KilnTestsAutoRun`, `KilnTestTimeoutSeconds` | | `tests` |
| `KilnServices` | | (references) Players, Chat, World, Items, Spawn, Time, Settings, … and `Testing` |
| `KilnServer` / `KilnModsDir` | | deploy target: a server folder / its `kiln/mods` |
| `KilnReload` | false | KilnDeploy also writes `kiln/control/deploy-<id>.cmd` = `reload <id>` |
| `KilnPackageOutputPath` | next to the output folder | where KilnPack writes the zip |
| `KilnModFolders` | `content;assets;skills;quests;blocks;icons;impact;audio` | data folders copied into the mod folder |
| `KilnIncludeModFolders` | true | false: copy none of them automatically |
| `KilnPackExclude` | `assets/cooked/**` | globs KilnPack leaves out, relative to the mod folder. Cooked files are made from the player's own game, so they never ship |
| `KilnAnalyzers` | true | false: no KILN1xx analyzers ([analyzers.md](analyzers.md)) |
| `KilnGameIds` | `Items;Perks;Buffs;Skills;Workshops` | game-id constants generated from the local catalog; `All`, `None` ([catalogs.md](catalogs.md)) |
| `KilnCatalog`, `KilnCatalogSide` | `$KILN_CATALOG`, else `<data>/catalog`; server for `KilnSide` server, otherwise client | which catalog file `KilnGameIds` reads |

Items: `KilnDependency` (`Version`, `Optional`), `KilnLoadBefore`, `KilnLoadAfter`, `KilnIncompatible` (`Version`).

### Services and what they need
`KilnServices` lists the services your code uses. Some services expose types of others (`Kiln.Content` needs
`Kiln.Items`, for example). With the packages the SDK adds those references itself, so you list only what you call.
The dependency table comes with Kiln.Api (`kiln/lib/Kiln.Services.props`). KILN108 warns about a service your code uses
that is not listed.

### kiln.mod.json: generated or validated
Without a `kiln.mod.json` in the project folder, the build generates one from the properties. With one, the build
validates it and copies it. Either way the result is parsed by **the loader's own parser** (the task compiles
`managed/Kiln.Host/Manifest.cs`), so a manifest that builds is a manifest Kiln accepts. Errors: `KILN001` invalid
manifest (the message names the field), `KILN002` `entryAssembly` is not the built assembly, `KILN004` unknown
service in `KilnServices`; warning `KILN003` (unknown keys, short versions, id differs from `KilnModId`).

### Targets
- `dotnet build`: the output folder **is** the mod folder (`<Mod>.dll`, `.pdb`, `kiln.mod.json`, NuGet dependencies).
- `dotnet build -t:KilnDeploy -p:KilnServer=<server>`: copies the mod's files (not Kiln.*, not deps/runtimeconfig
  files) into `<server>/kiln/mods/<id>/`, replacing old dlls/pdbs. With `-p:KilnReload=true` the running server
  reloads it within a second (a mod that was not loaded yet is loaded: `reload` falls back to the folder).
- `dotnet build -t:KilnPack`: `<id>-<version>.zip` containing `<id>/…`; unzip it into `kiln/mods`.

## Using the feed
```sh
KILN_OUT=<build dir> managed/Kiln.Sdk/pack.sh
dotnet new install Kiln.Templates::0.2.0 --add-source $KILN_OUT/nuget
dotnet new kiln-mod -n MyMod --feed $KILN_OUT/nuget          # writes MyMod/nuget.config pointing at the feed
```
`dotnet new kiln-mod` options: `--modId`, `--services "Players;Chat"`, `--tests` (adds Kiln.Testing and a test
class, auto-run), `--feed <dir>`, `--server <dir>` (sets `KilnServer`). `kiln new` ([cli.md](cli.md)) does the same and
installs the templates when they are missing.
- The NuGet cache keeps a version once restored: after re-packing **the same version**, clear
  `~/.nuget/packages/kiln.*/0.2.0` (or build with `NUGET_PACKAGES=<fresh dir>`).
- To keep experiments out of your user setup: `dotnet new … --debug:custom-hive <dir>`.

`dotnet new kiln-service -n Weather -o <repo>` (run at the Kiln repository root) creates a native + managed service
like `kiln.example`: `native/services/weather/`, `native/core/abi/services/kiln_weather.h`,
`managed/services/Kiln.Weather/`. The new assembly must be added to Kiln.Api's `InternalsVisibleTo` list.

## Templates
`--modId`, `--feed` and `--server` work on every mod template. `kiln new --list` shows all the options.

| Template | Gives you | Side, clients | Lab result |
|---|---|---|---|
| `kiln-mod` | A code mod; `--tests` adds `[KilnTest]`s | server, none | `tests: tpl-code: 2 passed, 0 failed` |
| `kiln-datapack` | `content/*.json` edits to game data | server, none | `Material_T1_Wood_Logs maxStackSize 1000 (game: 250)` |
| `kiln-items` | New items and recipes, `content/`, `icons/` | both, mod | `TplItems_Shard registered, TplItems_Blade registered`, `recipe TplItems_Blade_Recipe defined` |
| `kiln-creature` | A creature clone with level, scale and spawning | both, kiln | `TplCreature_Elite … registered: level 18, scale 1.3` |
| `kiln-spell` | A staff spell from building blocks, in C# and `blocks/*.json` | both, kiln | `spell frostshard -> 1 item(s), 1 recipe(s), 2 template(s), 1 program(s)` |
| `kiln-vfx` | An effect from nothing, in C# and `blocks/*.json`, a small PNG, keys to play it | both, mod | Builds, lints and cooks from a clean feed. How it looks is a manual check in a client |
| `kiln-ability` | An ability; `--impact` adds an Impact program | both, none | `ability tpl-ability:blast registered` |
| `kiln-ui` | A game UI overlay | client, mod | Correctly skipped by a server (`side` client). A client check is manual |
| `kiln-native` | A C++ mod, `CMakeLists.txt` + `native.json` | both | `native mod tpl-native 0.1.0 loaded (boot, … kiln_native_init ok)` |
| `kiln-model` | A glTF model with `assets/cook.json` | both, mod | `PASS TplModel_Blade.iconModel = …`, 3 references |
| `kiln-sound` | Sounds and cues, `audio/`, `assets/cook.json` | both, mod | `sound chime: … (new)`, `cue chime: … (new)` |
| `kiln-talents` | A talent tree, `skills/`, `icons/` | both, mod | `tree tpl-talents:talents registered (2 nodes…)` |
| `kiln-test` | A separate test mod; `--target <mod>` depends on it | server, none | `tests: tpl-test: 4 passed, 0 failed, 1 skipped` (the skip needs a joined player) |
| `kiln-service` | A native + managed service, run at the repository root | | Not part of this run |

Cooked files come from your own game: run `kiln cook` before the first build of `kiln-model`, `kiln-ability --impact`
and `kiln-sound`. `kiln build` does it when a recipe is out of date.

`dotnet new` gives items new GUIDs each time. If you delete a template project and create it again under the same name,
a world that saw the first one keeps its items as placeholders and logs `not registered in the server world: the name …
is a game item's` at every start. Your new items still register. Use a fresh world or another name to avoid it.

## Native mods
`Kiln.Sdk` carries a native SDK in `native/`, so a C++ mod builds without a Kiln checkout:

| In the package | What |
|---|---|
| `native/cmake/KilnPlugin.cmake` | `kiln_plugin(<name> <sources…> [NATIVE_JSON <file>] [OUTPUT_NAME <dll>])` and the `deploy` target |
| `native/cmake/clang-cl-xwin.cmake` | The Linux toolchain: clang-cl, lld-link and llvm-rc, with the MSVC CRT and Windows SDK from cargo-xwin |
| `native/include/` | `kiln_api.h`, `kiln_native.h`, `services/kiln_<name>.h` |

```sh
kiln new native MyNative --server <server>
cd MyNative
kiln build && kiln deploy        # <server>/kiln/native/<id>/
kiln run --restart               # native mods load once per start
```
`kiln` finds the SDK in this order: `$KILN_SDK_DIR`, the restored package, the package in the feed (unpacked into
`<data>/sdk/`), the checkout's `managed/Kiln.Sdk/native`. By hand, see the top of `CMakeLists.txt`, and unpack the package
without its timestamps (`unzip -DD`): NuGet stores UTC, so west of Greenwich the files look newer than the build and ninja
stops with `manifest 'build.ninja' still dirty`.
- On Linux you need `clang-cl`, `lld-link`, `llvm-rc`, `ninja`, and the Windows SDK (`cargo xwin env` once downloads it).
- On Windows use the Visual Studio generator, or Ninja from a developer prompt, with no toolchain file.

Without `NATIVE_JSON`, `kiln_plugin` builds a Kiln plugin for `kiln/plugins/`. The contract is in
`docs/kiln/native-mods.md`.

## The tools around it
| Page | What |
|---|---|
| [cli.md](cli.md) | The `kiln` command: new, build, cook, deploy, run, reload, test, debug, lint, pack, logs, catalog |
| [analyzers.md](analyzers.md) | KILN1xx: what the compiler tells you about threads, hot reload and the manifest |
| [catalogs.md](catalogs.md) | The local game-data catalog and the game-id constants |
| [debugging.md](debugging.md) | Breakpoints in a running server with `kiln debug` |
| [vscode.md](vscode.md) | The VS Code extension |

## Inside the Kiln repository
In-tree mods use the same SDK without NuGet: import the files directly and the SDK switches to project references
(Kiln.Api, `managed/services/Kiln.<Service>`, Kiln.Testing, and Kiln.Sdk itself for its task, which runs out of
process from `$KILN_OUT/sdk-tasks/`):
```xml
<Project>
  <Import Project="../../../managed/Kiln.Sdk/Sdk/Sdk.props" />
  <PropertyGroup> … </PropertyGroup>
  <Import Project="../../../managed/Kiln.Sdk/Sdk/Sdk.targets" />
</Project>
```
`mods/samples/build.sh` builds the samples this way and lands them in `$KILN_OUT/mods/<id>` with `KilnDeploy`
(`-p:KilnModsDir=$KILN_OUT/mods`); `tools/deploy-server.sh <server> --loader-samples` does that and deploys them.
`tools/deploy-server.sh` ships every folder with a `kiln.mod.json` under `$KILN_OUT/mods`: the mod's own files, plus
its entry assembly and `exports` even when they are named `Kiln.*`.
