# The `kiln` command line

`kiln` is the modder's command set: create a mod from a template, build it, cook its game-derived files, start a local
server, deploy, hot-reload, run the in-server tests, attach a debugger, lint, package, read the logs, and look things up
in the local game-data catalog. It drives what Kiln already has (Kiln.Sdk's MSBuild targets, the control folder, Kiln.Testing's
result files, the launcher's server instances, the dev labs, the linter, the catalogs) instead of doing it a second
way, so `dotnet build -t:KilnDeploy` and `kiln deploy` do the same thing.

Source: `crates/kiln-cli` (binary `kiln`, on `kiln-manage`). Not kilnctl: kilnctl runs dedicated servers for operators
(Linux, Wine); `kiln` is for mod authors on Linux and Windows.

```sh
cargo install --path crates/kiln-cli          # -> ~/.cargo/bin/kiln (or: cargo build -p kiln-cli)
kiln new items MyItems --server ~/srv/enshrouded/labs/<lab>
cd MyItems
kiln build && kiln deploy --reload            # build, copy into <server>/kiln/mods/my-items, hot-reload it
kiln test                                     # the mod's [KilnTest] methods, in the running server
kiln logs --mod my-items -f
kiln pack                                     # bin/<config>/my-items-0.1.0.zip (never with cooked game files)
```

## Conventions
- **Output.** Human text by default. `--json` (any command, any position) prints one JSON document on stdout
  (`kiln logs --json`: one object per line); tool output (MSBuild, CMake, cook tools) then goes to stderr. Errors:
  `{"ok": false, "error": {"code", "message", "detail"?}}`.
- **Exit codes.** 0 ok, 1 failure (build failed, a test failed, lint errors, nothing answered), 2 usage.
- **The project.** Commands that act on a mod take `[PROJECT]`: a `.csproj`, a native mod folder (CMakeLists.txt
  calling `kiln_plugin`), or a folder holding one; default: the current folder.
- **The server.** `--server` takes a folder (a dedicated server or a game with Kiln), a dev lab name
  (`~/srv/enshrouded/labs/<name>`, `$KILN_LABS`), or a server instance id (the launcher's and kilnctl's,
  `kiln_manage::server::instance`). Without it: `$KILN_SERVER`, then the project's `KilnServer` property.
- **Where things come from.** The MDK feed (Kiln.Sdk, Kiln.Api, Kiln.Templates): `--feed`, `$KILN_FEED`,
  `$KILN_OUT/nuget`, `<data>/sdk/nuget`. The cook tools: `$KILN_REPO` or a development checkout when one is set, otherwise the copy
  `kiln` carries (unpacked on first use). Labs still need a checkout. The installed game the cook tools read: `--game`, `$KILN_GAME`, else the Steam client detected by
  kiln-manage. The native SDK (headers, `KilnPlugin.cmake`, the Linux toolchain): `$KILN_SDK_DIR`, the restored
  Kiln.Sdk package, the package in the feed (unpacked into `<data>/sdk/`), or the checkout's `managed/Kiln.Sdk/native`.

## Commands
| Command | Does | `--json` result |
|---|---|---|
| `kiln new --list` | the templates and their options | `[{id, name, description, kind, language, options: [{name, type, default, description, choices?}]}]` |
| `kiln new <template> <Name> [-o DIR] [--server S] [--feed F] [--set opt=value]…` | `dotnet new kiln-<kind>`; installs Kiln.Templates from the feed when missing. The `kiln-` prefix may be left out | `{ok, template, dir, project}` |
| `kiln build [PROJECT] [-c CONFIG] [--no-cook] [-- args]` | `kiln cook` first when a recipe is out of date, then `dotnet build` (or CMake configure + build for native mods). MSBuild's lines pass through unchanged (KILN0xx, KILN1xx, CS…) | `{ok, project, kind}` |
| `kiln cook [PROJECT] [--check] [--force] [--game DIR]` | every `*/cook.json` of the project with the tool it names: `modelcook` (models), `impact` (Impact programs), `soundcook` (sounds), `animcook` (animations), only when its `--check` says a cook is needed (`--force`: every recipe, also one whose check reads it as up to date: that check reads the mod's files, not the game, so use it after a game update). A recipe no receipt of that game lists yet is cooked too (files cooked before receipts existed). The tools record what they wrote in the game folder they read, when it has Kiln (`<game>/kiln/state/cook-receipts`): that game warns about, or with `"cooked": "local"` refuses, cooked files no receipt lists ([cooking.md](cooking.md)). A second cook of the same folder waits for the first. With no modelcook recipe in `assets/cook.json`, also the files of the worn looks in `blocks/*.json` ([cosmetics.md](cosmetics.md)). `--check` never reads the game and exits 1 when a cook is needed. `PROJECT` may also be an installed mod's folder (with its `kiln.mod.json`); `--game` a dedicated server's folder, which cooks what a server uses and skips the models, one line each ([dedicated-servers.md](../dedicated-servers.md#mods-on-the-server)). What the cook makes from the game goes into `assets/cooked` only (a recipe whose `out` is elsewhere is refused), and that folder does not count as the mod: cooked on a server or in a player's game, it is the same mod ([models.md](models.md), "What a cook writes") | `{ok, recipes: [{recipe, tool, ok, cooked \| needed \| skipped}]}` |
| `kiln deploy [PROJECT] [--server S] [-r\|--reload] [-c CONFIG]` | C#: `dotnet build -t:KilnDeploy -p:KilnServer=…` (`-p:KilnReload=true` with `--reload`). Native: CMake target `deploy` into `<server>/kiln/native/<id>/` (native mods load once per start: `kiln run --restart`) | `{ok, project, server, via, id, reload}` |
| `kiln pack [PROJECT] [-o DIR]` | C#: `KilnPack` (`<id>-<version>.zip` holding `<id>/`; `KilnPackExclude`, default `assets/cooked/**`, stays out). Native: `<id>-<version>.zip` of the staged mod folder without pdbs | `{ok, zip}` |
| `kiln publish --artifact ZIP \| --project PROJECT --to github --plan FILE` | Audits the zip, freezes it with `kiln.publish.json` and the notes, writes a plan to review; sends nothing ([publishing.md](publishing.md)). Then `--apply FILE --confirm` (draft release + three assets), `--resume TX --confirm`, `--publish-draft TX --confirm`, `--resolve-step STEP --remote-id ID --transaction TX --confirm`; `--store-token github` / `--forget-token github` | plan: `{ok, state, txId, plan, digest, mod, destination, artifact, operations}`; others: `{ok, state, txId, confirmedSteps, unknownSteps, remoteIds, reason, manualSteps}` |
| `kiln run [--server S] [--stop\|--restart\|--status] [--wait SECONDS]` | starts a lab (`tools/lab/lab.sh start`) or a server instance (`kiln_manage::server::run`), then follows kiln.log until the loader's summary (`mods: N loaded, …`) and `lifecycle: server started`, printing the mod and lifecycle lines (default wait 180 s; 0 = don't wait). Folders that are neither are refused | `{ok, server, via, lab, running, modsLoaded, lines}` |
| `kiln reload [ID] [--server S]` | control command `reload <id>` (dependents unload, the mod reloads from disk, everything loads again). ID default: the project's `KilnModId` | the control result: `{command, ok, message, data: {affected}, time}` |
| `kiln test [ID] [FILTER] [--exact NAME] [--server S] [--timeout S]` | control command `test <id> [filter]`, waits for the run, prints `kiln/logs/tests/<id>.json` | the report unchanged: `{mod, version, started, ms, ok, passed, failed, skipped, results: [{name, outcome, ms, message, stack}]}`; with `--exact` only that test's result and `"filteredBy": "cli"`; no run: `{ok: false, error: {code: not_running \| not_loaded \| timeout \| refused}}` |
| `kiln debug [--server S] [--dap]` | a .NET debugger (netcoredbg, not bundled with the CLI) on a running local server's mods, inside that server's own Wine prefix ([debugging.md](debugging.md)). Plain: says how. `--dap`: a Debug Adapter Protocol proxy on stdin/stdout that never ends the server. A server in a Steam library is always refused | `{ok, server, via, lab, pid, hostPid, wine, prefix, netcoredbg, env, command}`; errors `not_running`, `refused`, `tool_missing`, `not_found`, `unsupported`. `--dap` answers in DAP instead: every request but `disconnect` fails with the reason, and it exits 0 after `disconnect` |
| `kiln logs [--server S] [-f] [--mod ID] [--level D\|I\|W\|E] [-n N] [--file kiln\|game\|proxy\|mods]` | the last N lines of kiln.log (or another log), filtered; `-f` follows (handles rotation) | one `{t, level, tag, text}` per line |
| `kiln blocks list [--server S] [--mod ID]`, `kiln blocks show NAME [--programs]` | What a server's mods' building blocks became, and what went wrong ([blocks.md](blocks.md)) | `{mods: [{mod, blocks, errors, warnings}]}`; `{mod, name, block, programs}` |
| `kiln lint [PATH…] [--catalog F] [--profile DIR] [--min-severity L] [--off KLnnn]… [--stdin --stdin-path P] [--list-rules]` | the Kiln linter (`kiln_manage::lint`) over files, mod folders and Kiln.Sdk projects. Text: one `<path>:<line>:<col>: <severity> <code>: <message>` line per finding, then a summary | the linter's report (`report.to_json()`, schema/lint-report.schema.json) |
| `kiln skeleton player OUT.glb [--body male\|female\|none] [--game DIR]` | the player's skeleton from your installed game, with a body to model around, to weight a worn model against ([cosmetics.md](cosmetics.md)). It holds the game's data: keep it out of your mod | `{ok, file}` |
| `kiln catalog status [--game DIR]` | where the catalogs are and whether they match the installed game (cheap: the `.kfc` header) | `{dir, catalogs: [{side, path, exists, current, game, gameBuild, generated, counts}]}` |
| `kiln catalog build [--side client\|server] [--game DIR] [--force] [--types]` | makes or refreshes `<data>/catalog/{client,server}.json` from the installed game (only when outdated unless `--force`; `--types`: the reflection dump for local Kiln.Game types) | `{ok, catalogs: [{side, path, built, game, counts, bytes, seconds}]}` |
| `kiln catalog path` | the catalog folder | `{dir, client, server, exists, current}` |
| `kiln catalog search <query> [--section S] [--limit N] [--side S]` | entries whose name, display name, id or GUID contain every word | `{catalog, hits: [{section, name, id, guid, displayName}]}` |
| `kiln vfx info <name\|guid>` | One game VFX from the catalog: what finds it, how long it runs, what it takes ([vfx.md](vfx.md)) | the catalog's entry |
| `kiln boot-snapshot --game DIR [--force]` | makes the game-data snapshot `Mod.OnBoot` reads (items, templates, skill nodes, materials) in `<game>/kiln/data/boot-gamedata/<key>/`, once per game build: about a minute and about 200 MB the first time, with checksum verification on reuse (the main Play action and `tools/deploy-server.sh` do it when an enabled OnBoot mod needs it). Exit 0 when one is in place, also when a kind is empty (a warning names it); 1 when there is none ([boot-snapshot.md](boot-snapshot.md)) | `{status, key, path, counts, emptyKinds, skipped, archive, bytes, seconds, reason}` |

Test filters: Kiln.Testing matches a case-insensitive **substring** of the test name (`[KilnTest(Name = …)]`, else
`<Class>.<Method>`, no namespace), and the control folder splits on spaces, so a filter has none. `--exact` runs the
substring and keeps the one result whose name equals it.

## Examples
```sh
kiln new --list --json | jq '.[].id'
kiln new kiln-test MyModTests --set target=my-mod      # a test mod that depends on my-mod
kiln build -c Release -- -v:n                          # extra MSBuild arguments after --
kiln cook --check || kiln cook                         # models / Impact programs / sounds from your game
kiln run --server mylab --restart --wait 300
kiln test my-mod Imc_ --json | jq '.failed'
kiln catalog search "bronze sword" --section items
kiln lint content/ --json --min-severity warning
echo '{"format": 1, "items": {"Nope": {}}}' | kiln lint --json --stdin --stdin-path content/x.json
```

## Lab check
Every template made with `kiln new`, built and deployed with `kiln deploy`, cooked with `kiln cook`, the lab started
with `kiln run` (`lab mylab up after 20 s`, then the mod lines and `lifecycle: server started (step 274,
game-log)`), `kiln test tpl-code` (2 passed), `kiln test tpl-code --exact TplCodeTests.Ticks_advance --json` (one
result, `"filteredBy": "cli"`), `kiln reload --json` (`"message": "reloaded tpl-code 0.1.0"`, the tests ran again on
reload), `kiln logs --mod tpl-code -n 5`, `kiln lint` over every template project. The template lab results are in
mdk.md, "Templates"; the `kiln debug` results are in debugging.md, "Lab check".
