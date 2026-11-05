# VS Code extension (tools/vscode)

`tools/vscode` is the Kiln extension for VS Code (TypeScript, MIT). It works on mod folders anywhere; with a Kiln
checkout (`kiln.root`, or a workspace inside the repository) it also runs the Impact assembler and the labs.

It builds into a local `.vsix` only. It is never published to the Marketplace or Open VSX.

## Install and build
```sh
cd tools/vscode
npm ci
npm run package                      # $KILN_OUT/kiln-vscode-<version>.vsix (KILN_OUT default <repo>/.out/vscode)
code --install-extension $KILN_OUT/kiln-vscode-0.1.0.vsix
```
`npm run build` (sync + esbuild) is enough for a development host: `code --extensionDevelopmentPath=$PWD`.

`npm run sync` copies what the extension bundles from the tree:
- `schema/*.schema.json`, `schema/schemas.json`, `schema/idrefs.json` → `data/schemas/`;
- `tools/impact/data/*` → `data/impact/index.json`;
- `branding/logo` → `media/`;

and regenerates `contributes.jsonValidation` and the JSONC file patterns in `package.json`. Run it (or `npm run build`)
after a schema change.

| Tool | Needed for |
|---|---|
| `kiln` CLI (`crates/kiln-cli`, setting `kiln.cli.path`) | lint, catalog build, `kiln new` templates, `kiln test`/`reload`. Without it the extension uses `dotnet` and the control folder |
| `dotnet` | build, deploy, pack (`dotnet build -t:KilnDeploy`), `dotnet new kiln-mod` |
| `python3` + a Kiln checkout | Impact diagnostics, cook, disassemble; lab start/stop (`tools/lab/lab.sh`) |
| `kilnctl` | start/stop of kilnctl instances |

## Mod files (JSON)
- **Schemas.** `schema/schemas.json` decides which file gets which schema (`fileMatch` globs). Its
  `dialect` says what the real reader accepts:
  - `comments: true` files open as JSONC;
  - the bundled schema copies carry `allowComments`/`allowTrailingCommas` from the dialect.
  - Example: content packs allow comments but no trailing commas, because nlohmann rejects them.

  `tools/vscode/schemas.fallback.json` adds Kiln's own runtime files that schemas.json does not map (`kiln/state/*.json`,
  `kiln/profile.json`, `kiln/.install.json`, `kiln/data/kiln-admin/endpoint.json`, `kiln/config/.schema/*.json`).
- **Lint.** On save, `kiln lint --json <file>` runs, and its diagnostics appear under the source `kiln lint`, with the
  code and rule.
  - `kiln.lint.onType` also lints unsaved buffers through `--stdin --stdin-path`.
  - **Kiln: Lint Mod** lints a whole mod folder.
  - A diagnostic's `fixes` become quick fixes.
  - `docs` links the code to its page.
  - Spans are UTF-8 byte offsets; columns in the text form are code points.
  - Codes: KL0xx syntax, KL1xx schema, KL2xx manifest, KL3xx ids, KL4xx game ids, KL5xx assets/cook, KL6xx licences,
    KL9xx internal.
- **Game ids.** Completion and hover come from the game-data catalog.
  - Location: `kiln.catalog.path`, else `$KILN_CATALOG`, else `~/.local/share/kiln/catalog`. The extension reads
    `client.json`, else `server.json`.
  - `schema/idrefs.json` says which JSON values are references: `{files, pointer, section, forms}`, where `*` is any key
    or index and a trailing `~` means the key itself. Until that file exists, a built-in table covers the samples'
    data packs.
  - Hover shows the display name, kind, tier, id, GUID and the item icon (inlined from the catalog's `icons` folder).
  - C# string literals that name a catalog entry get the same hover.
  - The catalog is game data: the extension reads it in place and never copies it.

## VFX blocks
In `blocks/*.json` ([vfx.md](vfx.md)):
- **Fields.** Completion, hover and problems for the `vfx` section come from the schema.
- **What a `vfx` field names.** Completion offers this mod's blocks and the catalog's game VFX, by any name that finds them. One that only a GUID can find goes in as `game:<guid>`.
  Hover shows how long a game VFX runs, what else uses it and its parameters. It works on `VfxId.Game("...")` and `VfxRef.Game("...")` in C# too.
- **Parameters.** Inside `"parameters"`, completion offers the game VFX's parameters with their types and defaults.
- **The budget.** Hover on a block's name: its share of a VFX's entries, and what the mod's blocks use of the VFX, meshes, mesh memory and sounds in VFX left for all mods.
- **Snippets.** `vfx-burst`, `vfx-trail`, `vfx-light`, `vfx-flipbook`, `vfx-game`, `vfx-parameters`.

The bundled schema copies put `allOf` around a reference to another schema file that has sibling keywords. VS Code 1.145's JSON service otherwise reads local references inside that file against the wrong root, and the
whole document goes unchecked. `npm run sync` does this; the files in `schema/` stay as they are.

## Mods view, build, deploy, reload
A mod is a folder with `kiln.mod.json` and/or a Kiln.Sdk project. Its id comes from `kiln.mod.json`, else
`<KilnModId>`, else the lower-case AssemblyName.

| Command | With the `kiln` CLI | Without it |
|---|---|---|
| Build Mod | `kiln build <proj>` | `dotnet build <proj> -tl:off -p:GenerateFullPaths=true` |
| Deploy Mod to Test Server | `kiln deploy <proj> --server <dir>` | `… -t:KilnDeploy -p:KilnServer=<dir>` |
| Deploy and Reload Mod | `… --reload` | `… -p:KilnReload=true` |
| Pack Mod | `kiln pack <proj>` | `… -t:KilnPack` |
| Reload Mod on Test Server | `kiln reload <id> --server <dir> --json` | control folder `reload <id>` |
| Send Control Command | | `mods`, `gc`, `reload-test <id> 20`, `unload`, any command |
| New Mod | `kiln new --list --json`, then `kiln new <template> <name> --dir … --set k=v --json` | `dotnet new kiln-mod` (installs Kiln.Templates from a local feed, `.out/*/nuget`, when it is missing) |

- **Tasks.** Each command runs as a task. Task type `kiln`: `{"type": "kiln", "task": "build|deploy|pack|lint|cook",
  "project": …, "reload": true, "server": "<name>", "configuration": "Release"}`.
- **Extra dotnet arguments.** `kiln.dotnet.buildArgs` adds arguments to every dotnet build, e.g. `--artifacts-path` for
  in-tree mods. With extra arguments set, dotnet runs directly (the CLI has no pass-through).

Problem matchers (all contributed by the extension):

| Matcher | Reads |
|---|---|
| `$kiln-sdk` | `file(line,col): error KILN1xx: message [proj]` (analyzers, any KILN code with a location) |
| `$kiln-sdk-file` | `kiln.mod.json : error KILN001: message [proj]` |
| `$kiln-sdk-project` | `error KILN010: message [proj]` (no file: reported on the project) |
| `$kiln-dotnet` | the compiler's own codes (CS…), KILN codes excluded |
| `$kiln-lint` | `path:line:col: severity KLnnn: message` (`kiln lint` text output) |
| `$kiln-impact` | `impact: file.kimp:line: message` (tools/impact) |

## Test servers
The **Test Servers** view lists:
- `kiln.servers` entries: `{name, dir, kind: folder|kilnctl|lab, instance}`;
- kilnctl instances (`~/.config/kiln/instances/*.toml`);
- with `kiln.server.discoverLabs`, the labs in `~/srv/enshrouded/labs`.

The selected server (status bar) is what deploy, reload, tests, the log and the console use.
- **State.** A server is running when it has an `enshrouded_serv` process: the same WINEPREFIX test as lab.sh and
  kilnctl, or the process's cwd inside the folder. A live KAP endpoint also counts.
- **Start/stop.** kilnctl instances use `kilnctl start|stop <name>`; labs use `tools/lab/lab.sh start|stop <name>`;
  plain folders have no start/stop.
- **Read-only.** A folder inside a Steam library (`steamapps/common`, `steamapps/compatdata`) is read-only. The
  extension follows its log and opens its console, but never deploys, reloads, runs tests, starts or stops it.

## Testing view (Kiln.Testing)
- **Discovery.** `[KilnTest]` methods in each mod's C# files appear as mod → class → test. The name is what
  Kiln.Testing reports: the attribute's name, else `Class.Method`.
- **Profiles.**
  - **Deploy, reload and run** (default): deploys with reload, waits for the reload line in kiln.log, then runs.
  - **Run (as deployed)**: runs the deployed build as it is.
- **Run.** `kiln test <id> [--exact name] --server <dir> --json`, else the control folder `test <id> [filter]`.
  - Results come from `kiln/logs/tests/<id>.json`.
  - A single test sends its name as the filter. Kiln.Testing's filter is a case-insensitive substring, so the
    extension reports only the requested tests.
  - Failures carry the message, the stack, and the first stack frame inside the mod as their location.
  - The mod's kiln.log lines stream into the run output.

## kiln.log
**Follow kiln.log** (Test Servers view) opens an output channel per server:
- the last `kiln.log.initialLines` lines, then new lines as they come;
- a server restart (the log truncated or replaced) is noted.

Highlighting (also for any `kiln/logs/*.log` opened in an editor):
- `[E]` lines and FAIL use the theme's log.error colour; `[W]` lines use log.warning; PASS uses log.info.
- These are the built-in log grammar's scopes, so every theme colours them.

Links:
- `[mod:<id>]` → the workspace mod's manifest, else the deployed one;
- stack frames `in <file>:line N` (Wine `Z:\` paths too) → file and line;
- POSIX paths with `:line[:col]`;
- `PASS`/`FAIL`/`SKIP <name>` → the test method.

## Server console (KAP/1)
**Open Server Console** reads `<server>/kiln/data/kiln-admin/endpoint.json` and the `admin-token`, and connects with the
HMAC handshake (docs/kiln/launcher/admin-protocol.md). It refuses a stale endpoint (connect refused) or a wrong token,
and says why.

In the terminal:
- a plain line is a console command (`command_run`, as the server console would run it);
- Tab completes through `command_complete`;
- `.op <name> [k=v … | {json}]` calls any Kiln.Admin op, and `.ops` lists them;
- `.log on|off` streams kiln.log (the `log` topic);
- Up/Down walks the history.

The token stays in memory. The console is a terminal, so nothing is written to disk.

## Game Data view
- **Browse.** Sections and entries come from the catalog. Sections with more than 300 entries are grouped by name
  prefix (`Weapon_…`, `Material_…`); items show their icons.
- **Actions.** Show the entry as JSON, copy the name or GUID, or insert the name at the cursor.
- **Search.** **Search Game Data** searches names, display names, GUIDs and ids.
- **Build.** **Build Game-Data Catalog** runs `kiln catalog build`.
- **Reload.** The catalog reloads when its file changes.

## Impact assembly (.kimp, .kpatch)
Language features:
- **Grammar:** directives, sections, labels, opcodes; `ecall` natives, `$variables`, `_`, GUIDs, times, masks.
- **Hover:**
  - opcodes and directives;
  - natives: signature in args[] order with types and directions, hash, argc, returns, suspends, and vanilla use;
  - `$variables`: their declaration and type size;
  - types, config aliases, and string config ids (shows the FNV-1a id);
  - the program GUID (shows the program id the game looks up).
- **Completion:**
  - instructions and directives;
  - natives after `ecall`, as a snippet with the arguments;
  - variables, and `_` in arguments;
  - types after `var $x`, config aliases after `config`, labels after `br`/`brt`/`brf`, stream names.
- **Navigation:** signature help inside `ecall name(…)`, outline (variables, labels), go to definition.
- **Snippets:** program skeleton, the wait idiom, the collision loop, `kiln.Log`, `kiln.Signal`.

A mod's own natives (`impact/natives.json`, and the files listed in `cook.json` `natives`) join the vanilla ones and
Kiln's built-ins.

**Diagnostics** (`kiln.impact.diagnostics`: `onType` by default, `onSave`, `off`) come from
`tools/vscode/python/kimp_check.py`. It runs `tools/impact`'s assembler and the verifier kiln.abilities applies.
- Assembler errors show on their line.
- Lint findings (E_*, W_*, I_*) map from pc to source line through the assembler's own items; variable findings point
  at their `var` line.
- A `.kpatch` is assembled against any variable name: instructions, natives, argc and labels are checked; variables
  are checked when the cook applies the patch.

Commands:
- **New Impact Program**: a `.kimp` with a fresh GUID in the mod's `impact/`.
- **Cook Impact Programs**: `impact.py cook <cook.json> --update-manifest`, as a task.
- **Disassemble Impact Program**: a vanilla GUID read from your installed game. The listing opens as an untitled
  document; vanilla listings are game data, so never save them into a mod.
- **FNV-1a Hash**: of a name, or the program id of a GUID.

## Debugging
The `kiln` debug type attaches a .NET debugger to a running test server: breakpoints in your mod's C#, stack, locals,
evaluate. Start it from the Test Servers view (right-click a running server: **Attach Debugger to Test Server**) or from
`launch.json`:

```json
{ "type": "kiln", "request": "attach", "name": "Kiln: attach to server", "server": "my-lab" }
```

- `server` is a name from the Test Servers view, a lab name or a server folder. Leave it out to use the selected server.
- **Deploy a Debug build of your mod.** Breakpoints in a Release build never bound in testing, with `justMyCode` on or off:
  they stay hollow. The default `dotnet build` and `kiln deploy` configuration is Debug; check `kiln.dotnet.buildArgs` if
  you set one.
- The Debug Console lists "You are debugging a Release build of ..." for Kiln's own assemblies. That is expected. It only
  matters when it names your mod.
- Breakpoints use the Linux paths in your mod's PDBs. They show as hollow until the debugger has read the modules, which
  took about 2 s in testing.
- **Reattach after a reload.** Breakpoints stay on the old copy of a reloaded mod, so they no longer stop. The adapter says
  so in a notification. Before attaching it asks the server to collect old copies; if some stay loaded it warns you to
  restart the server first.
- While the server is stopped at a breakpoint it is paused for everyone on it.

**netcoredbg comes with the extension.** The `.vsix` bundles the win64 build of netcoredbg 3.2.0-1092, so there is nothing
to install. To use another copy, set `kiln.debug.netcoredbg` or `$KILN_NETCOREDBG`, or put one at
`tools/netcoredbg/netcoredbg.exe` in the Kiln data folder (`~/.local/share/kiln` on Linux). An explicit path that does not
exist is an error; it never falls back to the bundled copy. The extension passes the copy it found to `kiln debug` as
`KILN_NETCOREDBG`.

**How it runs.** The .NET runtime runs inside Wine, so netcoredbg must too. It uses the server's prefix and the wine of
the running server, so it joins the same wineserver; attach uses the Windows pid from `wine tasklist.exe` in that prefix.
The adapter is `kiln debug --server <dir> --dap` when the CLI has that command (the extension asks `kiln debug --json`
first, so a refusal is an ordinary notification), and otherwise the same job done by `dist/dapProxy.js`. A bare folder, or
a set `kiln.debug.wine`, always uses `dist/dapProxy.js`.

**What keeps the server alive.** A debugger that goes away without detaching takes the server down with it, so both
adapters:
- send every `disconnect` with `terminateDebuggee: false`, and answer `launch`, `terminate` and `restart` with an error;
- detach on their own when the client goes away (window reload, extension host crash) or on SIGINT, SIGTERM or SIGHUP,
  and wait for the answer;
- run netcoredbg in its own process group and never signal it.

**What it cannot protect against.** netcoredbg dying while attached takes the server down (measured with
SIGKILL). Don't `kill -9` it, or a process tree that contains it. Watch expressions also run in the server, so one can end it
like any other call ([debugging.md](debugging.md)).

**What it refuses, before anything runs:** servers inside a Steam library (folder, prefix or running process, also through
a symlink), a stopped server, several server processes in one prefix, a process started from another folder, and a second
debugger on the same server.

Other tools for looking into a mod: Kiln.Testing, kiln.log and `kiln/crash-reports/`.

## Development
| Path | What |
|---|---|
| `src/core/` | logic without VS Code (unit-tested headless): lint report, UTF-8 spans, KAP/1 client, control folder, catalog, idrefs, globs, Impact data, C# test discovery, kiln.log links, servers, projects, DAP guard, debug targets |
| `src/dapProxy.ts` | the built-in debug adapter, a process of its own (`dist/dapProxy.js`) |
| `scripts/fetch-netcoredbg.mjs` | fetches the pinned netcoredbg release into the build dir, checks its sha256 and stages it in `dist/netcoredbg`. `npm run package` runs it, so packaging needs the network once. Never commit the binaries; their licences are in `licenses/netcoredbg.txt` |
| `src/features/` | VS Code features: one file each (catalog, vfx, lint, mods/tasks, servers, testing, log, console, impact, newMod, debug) |
| `python/kimp_check.py` | the assembler bridge (imports tools/impact; MIT) |
| `syntaxes/`, `snippets/` | grammars, language configuration, snippets |
| `test/unit/` | mocha, headless: `npm run test:unit` |
| `test/integration/` | mocha inside VS Code: `npm run test:integration` |

**Integration tests.**
- They run in the installed VS Code (`/usr/share/code/code`, or `KILN_VSCODE_EXE`); nothing is downloaded.
- The run is headless (`xvfb-run`) and isolated: a throwaway `--user-data-dir` and `--extensions-dir` under
  `KILN_VSCODE_SCRATCH`, no Wayland, no session D-Bus. The runner refuses to start outside xvfb-run.
- The fixtures are a synthetic mod, a fake server folder (an in-test stand-in answers its control folder), a fake
  `kiln lint`, the launcher's mock Kiln.Admin (`tools/launcher/mock-admin.py`), the synthetic catalog fixture
  (`crates/kiln-manage/src/catalog/testdata`), and a mock netcoredbg under a mock wine (`test/fixtures/dap`).
- With `KILN_IT_LAB=<lab dir>`, a lab suite also runs against a real server. Add `KILN_IT_REAL_CLI=<kiln>` to use the real
  CLI, and `KILN_IT_NETCOREDBG=<netcoredbg.exe>` (or `bundled`, for `dist/netcoredbg`) to run the real attach tests against that lab.
