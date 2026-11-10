# The dev kit

Wiki: [dev kit](https://frostdev.io/kiln/wiki/dev-kit), [kiln](https://frostdev.io/kiln/wiki/cli),
[lint](https://frostdev.io/kiln/wiki/lint), [VS Code](https://frostdev.io/kiln/wiki/vscode),
[catalogs](https://frostdev.io/kiln/wiki/catalogs), [debugging](https://frostdev.io/kiln/wiki/debugging).

## The `kiln` command
| Command | Does |
|---|---|
| `kiln new --list`, `kiln new <template> <Name> [--server S] [--feed F] [--set opt=value]` | a project; `tests=true`, `impact=true`, `target=<mod>` |
| `kiln build [-c CONFIG] [--no-cook] [-- args]` | cooks if needed, builds; C# or native |
| `kiln cook [--check] [--game DIR]` | every `cook.json`; `--check` exits 1 when one is needed |
| `kiln deploy [--server S] [--reload]` | copies into the server; hot reloads |
| `kiln pack` | `<id>-<version>.zip`, no cooked files |
| `kiln run [--server S] [--stop\|--restart\|--status] [--wait N]` | starts a launcher or kilnctl server; follows the log until up |
| `kiln reload [ID]`, `kiln test [ID] [FILTER] [--exact NAME]` | through the control folder |
| `kiln logs [-f] [--mod ID] [--level D\|I\|W\|E] [-n N]` | kiln.log, filtered |
| `kiln lint [PATH…]`, `kiln debug [--dap]`, `kiln catalog status\|build\|path\|search` | below |

`--server`: a folder or server name, else `$KILN_SERVER`, else the project's `KilnServer`. Native mods need `kiln run --restart`.

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
| KILN105 | blocking call on the game thread | `await` |
| KILN106 | thread, timer, watcher, socket, subscription never released | dispose in `OnUnload` |
| KILN107 (error) | advanced API without `KilnAdvanced` | opt in, or don't use it |
| KILN108 | service used but not in `KilnServices` | add it |
| KILN110 (error) | bad or duplicate block name | rename it |
| KILN111 | a name that is no block, or the wrong kind; only info for a game-style name (`Weapon_T1_...`) your local catalog lacks (dev.21+). Names from `content/*.json` and `ItemDefinition` count | define it, or use the handle `Blocks.Define` returned |
| KILN112 (error) | `clients` too low for the mod's blocks | raise `clients` (`kiln` or `mod`) |

KILN001-004 are manifest checks. KILN190: no catalog, so no game-id constants. Configure in `.editorconfig`
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
