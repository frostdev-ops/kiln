# The dev kit

Wiki: [dev kit](https://frostdev.io/kiln/wiki/dev-kit), [kiln](https://frostdev.io/kiln/wiki/cli),
[lint](https://frostdev.io/kiln/wiki/lint), [VS Code](https://frostdev.io/kiln/wiki/vscode),
[catalogs](https://frostdev.io/kiln/wiki/catalogs), [debugging](https://frostdev.io/kiln/wiki/debugging).

## The `kiln` command
| Command | Does |
|---|---|
| `kiln new --list`, `kiln new <template> <Name> [--server S] [--feed F] [--set opt=value]` | a project; `tests=true`, `impact=true`, `target=<mod>` |
| `kiln build [-c CONFIG] [--no-cook] [-- args]` | cooks if needed, builds; C# or native |
| `kiln cook [PATH] [--check] [--force] [--game DIR]` | every `cook.json`; `--check` exits 1 when one is needed; `--force` cooks every recipe (after a game update). dev.27+: writes cook receipts into the game it read; the tools ship with `kiln` (no `KILN_REPO` or checkout), Python 3.10+ needed. A server: `kiln cook <server>/kiln/mods/<mod> --game <server>` |
| `kiln deploy [--server S] [--reload]` | copies into the server; hot reloads |
| `kiln pack` | `<id>-<version>.zip`, no cooked files |
| `kiln publish --artifact ZIP \| --project P --to github --plan FILE`, then apply and publish | dev.28+: a GitHub release in steps you confirm: a plan (the zip checked for game data, cooked files, saves and secrets, then frozen), a draft, a public release. Token from the system's credential store (`kiln publish --store-token github`) or `KILN_PUBLISH_GITHUB_TOKEN`. Only tested against recorded answers: make the first release to a repository you can delete. `docs/kiln/modding/publishing.md` |
| `kiln updates check [--offline] [--refresh]` | dev.28+: newer versions of installed mods, from their `updates` |
| `kiln blocks list [--mod ID]`, `kiln blocks show NAME [--programs]` | what a server's blocks became, and what went wrong |
| `kiln boot-snapshot --game DIR [--force]` | dev.28+: the game-data snapshot `OnBoot` reads (the launcher makes it at Play) |
| `kiln vfx info <name\|guid>`, `kiln skeleton player OUT.glb` | one game VFX from the catalog; the player's skeleton to weight a worn model against |
| `kiln run [--server S] [--stop\|--restart\|--status] [--wait N]` | starts a launcher or kilnctl server; follows the log until up |
| `kiln reload [ID]`, `kiln test [ID] [FILTER] [--exact NAME]` | through the control folder |
| `kiln logs [-f] [--mod ID] [--level D\|I\|W\|E] [-n N]` | kiln.log, filtered |
| `kiln lint [PATH…]`, `kiln debug [--dap]`, `kiln catalog status\|build\|path\|search` | below |

`--server`: a folder or server name, else `$KILN_SERVER`, else the project's `KilnServer`. Native mods need `kiln run --restart`.
dev.29.17+: `kilnctl tick-compare BEFORE AFTER [--json]` compares two `kiln/logs/tick-stats.jsonl` files (step time, steps
over budget, each mod's share); the stats name the slowest Pre subscribers since dev.29.

`--json`: one document on stdout, tool output on stderr, errors as `{"ok": false, "error": {"code", "message"}}`.

## kiln lint
`kiln lint [PATH…] [--json] [--catalog F] [--profile DIR] [--min-severity L] [--off KLnnn] [--stdin --stdin-path P] [--list-rules]`.
Exit 1 means errors. Build a Kiln.Sdk project once first: its manifest is read from the build output.
KL0xx reading, KL1xx schema, KL2xx manifest vs content, KL3xx ids, KL4xx game references (needs a catalog), KL5xx files and cook recipes,
KL6xx licences. Findings can carry `fixes`.

## Analyzers (in every Kiln.Sdk build)
| Id | Catches | Fix |
|---|---|---|
| KILN101 | tick-thread API off the tick thread | `Context.Scheduler.Post` |
| KILN102 | `async void` | `async Task`; keep `OnLoad` synchronous |
| KILN103 | process-wide event handler never removed | `-=` in `OnUnload` |
| KILN104 | mod object in process-wide state | undo it in `OnUnload` |
| KILN105 | blocking call on the game thread; dev.29.18+ also in `[Command]` methods, `Commands.Register`/`Executes` and `Chat.Received` handlers, and misuse of Kiln tasks (`Wait()`, an unawaited `ToTick()`, reads after `ConfigureAwait(false)`) | `await`; a Kiln task |
| KILN106 | thread, timer, watcher, socket, subscription never released | dispose in `OnUnload` |
| KILN107 (error) | advanced API without `KilnAdvanced` | opt in, or don't use it |
| KILN108 | service used but not in `KilnServices` | add it |
| KILN109 (dev.29+) | client-world `EcsPrograms.TryBind` off the game's main thread | `OnLoad` or `Client.Post` |
| KILN110 (error) | bad or duplicate block name | rename it |
| KILN111 | a name that is no block, or the wrong kind; only info for a game-style name (`Weapon_T1_...`) your local catalog lacks (dev.21+). Names from `content/*.json` and `ItemDefinition` count | define it, or use the handle `Blocks.Define` returned |
| KILN112 (error) | `clients` too low for the mod's blocks | raise `clients` (`kiln` or `mod`) |
| KILN120 (dev.27+) | a Kiln result (`IKilnResult`, or a task of one) nobody reads | check `result.Ok` and pass `Reason` on, or `_ = ...` on purpose (both are code fixes) |
| KILN121 (dev.27+) | a `[RequiresCapability("row")]` member used with no `Capabilities.Is("row")`, no guard property of the service (`NoClip.Available`, `World.SupportsTrackedWrites`, `Time.WeatherChoiceAvailable`) and no `catch` of `KilnUnavailableException` in the same member | check or catch there, or mark the caller `[RequiresCapability]`; a check inside your own helper or a field is not followed |
| KILN122 (dev.31+), KILN123 (dev.31+) | `[RegistryEntryPoints]` points declared without a real `OnRegistry` override; zero, `Manual` or unknown automatic points | declare only points you author, with an override |
| KILN124 (info, dev.32+), KILN125 (info, dev.32+) | an old data path (a pack section, a resource patch, `ContentEntry` / `GameDataEntry`) a registry field covers; Native-tier code (a hook or memory write by address) a registry field covers | edit the registry field it names (the fix moves one-to-one pack values); the hint never refuses a mod |
| KILN194 (error), KILN195, KILN196, KILN197 (error) | a game path string that doesn't exist, a config name no program declares, a write to an identity field, a typed field of another section | the suggestion, or the typed field |
| KILN034 (error, dev.28+) | `OnBoot` without `"boot"`, or the run-time `Blocks.Define` in boot code | `KilnBoot`, `BootDefinitions` |
| KILN035 (dev.28+) | a tick, world or service API in boot code | move it to `OnLoad` |
| KILN170 to KILN174 (dev.29+) | an `[ImpactNative]` method the generator cannot make (KILN174: no `Abilities`) | static, typed parameters, own name |
| KILN180 to KILN182 (dev.29+) | a typed-hook descriptor it cannot use, no `Hooks` service, a sigdb file that is not JSON | fix the descriptor |

Experimental ids are errors until the project opts in with `NoWarn`: `KILNX001` lab members (never in a mod players
install), `KILNX002` not proven in a client (`SteamGuard`). `KILNX003` for `WorldConcurrent` is retired:
Since 0.2.0-dev.30.7, mod API 0.2.16 (no longer experimental).

KILN001-004 are manifest checks. Build steps: KILN020/021 the layout stamp (`Game`); dev.27+ KILN030 a warning of the
`"writes"` step or the bake, KILN031 an unreadable input, KILN032 a bake that failed or timed out (names `KilnBake=false`; dev.28+ also an
`OnBoot` that throws), dev.29+ KILN036 a `[Query]` struct the server cannot reserve or an `impact/natives.json` it cannot write, KILN037 a
hand-written native entry replaced. KILN190: no catalog, so no game-id constants. Configure in `.editorconfig`
(`dotnet_diagnostic.KILN105.severity`). Don't suppress one without saying why.

## Catalogs and game ids
`kiln catalog build` makes the catalog from the user's own game, read-only. Never commit or ship it. `kiln catalog search "bronze sword"
[--section items] [--json]`.

Kiln.Sdk generates constants from it: `using Kiln.GameIds;`, `GameItems.X`, `GameItems.Ids.X`, `GameItems.Guids.X`, `GameBuffs`, `GamePerks`,
`GameSkills`, `GameWorkshops`; with `<KilnGameIds>` also `GameRecipes`, `GameTemplates`, `GameSequences`, `GameSounds`.

## VS Code
The `.vsix`, installed by hand, adds schemas, lint on save, game-id hover and completion, Mods, Test Servers, Testing and Game Data views, a
kiln.log follower, a server console, Impact assembly and a `kiln` debug type.

## Debugger
`kiln debug --server <name> --dap` is a Debug Adapter Protocol proxy for netcoredbg (win64 build, bundled with the VS Code extension;
otherwise `KILN_NETCOREDBG`). In VS Code: `{ "type": "kiln", "request": "attach", "server": "<name>" }`.
- Deploy a Debug build: Release breakpoints never bind.
- A breakpoint pauses the whole server. Test servers only; Steam-library servers are refused.
- Never `kill -9` netcoredbg while attached: the server dies, with no save. Disconnect instead.
- After `kiln reload`, reattach. Watch expressions run in the server.
- Dedicated servers on Linux and Wine only. Windows servers are untested, and the client isn't covered.
