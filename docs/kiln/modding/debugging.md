# Debugging mods

`kiln debug` attaches a .NET debugger to the mods in a running local server: breakpoints in your C#, the call stack,
locals, watch expressions, stepping. The debugger is [netcoredbg](https://github.com/Samsung/netcoredbg) (MIT). The VS Code
extension bundles it; the `kiln` CLI on its own does not, so follow Set up below when you use the CLI without the extension.

A breakpoint pauses the whole server. The game runs mods on its tick thread, so nothing steps while you look around,
and everyone connected freezes. Debug on a test server, not one people play on.

## Set up
1. Get the **win64** build of netcoredbg, because the server's .NET runtime is a Windows one, even under Wine. Tested:
   `netcoredbg-win64.zip` of release 3.2.0-1092, sha256
   `3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a`.
2. Unpack it so that `netcoredbg.exe` is at `<data>/tools/netcoredbg/netcoredbg.exe`, or set `KILN_NETCOREDBG` to the
   exe wherever it is. `<data>` is `~/.local/share/kiln` on Linux (`$XDG_DATA_HOME/kiln`) and `%LOCALAPPDATA%\Kiln` on
   Windows.
3. Build and deploy your mod in the Debug configuration, so its `.pdb` goes along and breakpoints bind:
   `kiln deploy --reload`.
4. Start the server: `kiln run --server <lab>`.

## Attach
**In VS Code,** with the Kiln extension ([vscode.md](vscode.md)), add this to `launch.json` and press F5. `server` is
optional and defaults to the server selected in the Test Servers view:
```json
{ "type": "kiln", "request": "attach", "name": "Kiln: attach to server", "server": "<lab>" }
```

**In another editor** that speaks the Debug Adapter Protocol, use this as the adapter, and send an `attach` request with
no `processId`:
```sh
kiln debug --server <lab> --dap
```

**To see what it would run,** without running anything in the prefix except `wine tasklist.exe`:
```sh
kiln debug --server <lab>          # add --json for {pid, hostPid, wine, prefix, netcoredbg, env, command}
```

`--server` takes a lab name, a server instance id, or their folder. Without it: `$KILN_SERVER`, then the project's
`KilnServer` property.

## What `kiln debug` protects
| Rule | What it does |
|---|---|
| Only the server you named | The debugger runs with that server's own `WINEPREFIX` and with the wine build that runs it. Windows process ids exist per prefix, so no other server is reachable from there. A folder whose prefix Kiln does not know is refused |
| Only its process | The proxy fills in the server's pid. An `attach` with any other `processId` gets an error |
| Steam libraries stay out | A server inside a `steamapps` folder is always refused, before anything runs in its prefix |
| The server survives the session | Every `disconnect` goes out with `terminateDebuggee: false`, whatever the client asked. `launch`, `terminate` and `restart` get an error, and the proxy does not advertise them, so an editor's Stop button detaches |
| A vanishing editor detaches cleanly | When stdin closes, or on SIGINT, SIGTERM or SIGHUP, the proxy sends its own `disconnect` and waits for the response |
| Breakpoints go to the live mod | Before attaching, the proxy asks the server to collect unloaded mod contexts (control command `gc`). Otherwise a breakpoint can bind to the old copy of a reloaded mod and never stop |
| The debugger is never killed | netcoredbg runs in its own process group, so a Ctrl-C at the terminal does not reach it. If it does not answer, the proxy closes its stdin, which also makes it detach, and leaves it running |

## What it cannot protect
- **Killing netcoredbg while it is attached ends the server,** with no shutdown and no save. Don't `kill -9` it, or a
  process tree that contains it. Disconnect from your editor, or end `kiln debug --dap` with a plain signal or by closing
  its stdin. Even `kill -9` on `kiln debug --dap` itself is survivable, because netcoredbg then detaches on its own.
- **A mod that leaks its old copy leaves a trap.** If a reloaded mod's old context cannot be collected, breakpoints may
  miss and a disconnect may end the server. `kiln debug` warns when `gc` reports that (`N unloaded mod context(s) still
  alive`). Restart the server before debugging.
- **Don't drive netcoredbg by hand with `--interpreter=cli`.** Console sessions that ended while the server was paused
  killed it three times during testing. `kiln debug` prints the `--interpreter=vscode` command only.
- **Your own expressions run in the server.** A watch expression can call anything, and `System.Environment.Exit(0)`
  in one ends the server like any other call. The proxy does not read expressions.

## Good to know
- Attaching is quick: a second or two on a small mod.
- Breakpoints match on the source path recorded in the `.pdb`, so debug on the machine that built the mod.
- The stack below your code is Kiln's: `Kiln.Host.ModManager.Pre`, `HostRuntime.Pre`, `Entry.OnPre`.
- Under Wine, netcoredbg needs `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, or it stops with `Cannot get symbol
  u_charsToUChars from libicuuc`. `kiln debug` sets it, along with `WINEDEBUG=-all`.
- **After `kiln reload`, disconnect and attach again.** Breakpoints in a mod reloaded during a session stop nowhere.
  The server is fine, and `kiln debug` tells you with `<module> was reloaded: breakpoints in it no longer stop`.

## Not covered
- **Windows servers:** `kiln debug` finds the process by its folder and runs netcoredbg directly. This is untested.
- **The game client:** `kiln debug` attaches to dedicated servers only.

## Lab check (netcoredbg 3.2.0-1092, GE-Proton11-11)
Every line was run against a disposable lab with `kiln debug --dap`, driven by a DAP test client. "Survives" means the
same process afterwards, and `kiln test` passing `Ticks_advance`.

| Check | Result |
|---|---|
| Breakpoint, stack, locals, `evaluate`, continue | Stops in `TplCodeMod.OnPre`, above `Kiln.Host.ModManager.Pre` and `HostRuntime.Pre` |
| Client disconnects with `terminateDebuggee: true`, running or stopped | Survives |
| stdin closes, SIGTERM, SIGINT, SIGHUP, stopped at a breakpoint | Survives, proxy exits 0 |
| `kill -9` on the proxy, stopped at a breakpoint | Survives |
| `kill -9` on netcoredbg, stopped at a breakpoint | **The server dies.** The proxy reports it and exits 1 |
| `launch`, `terminate`, `restart`, another `processId` | Refused, nothing reaches the debugger |
| Two labs running, same Windows pid (252) in both | Each session's `Environment.CurrentDirectory` is its own lab. While one lab is paused its `_steps` stands still and the other answers `kiln test` in under a second |
| A fake Steam library: folder, symlink, detected instance, `KILN_SERVER` | Refused before anything runs |
| Instance records naming another server's prefix | Refused: the process in that prefix was not started from that folder |
| Reload, then attach, 6 times | Stops 6 of 6, in two runs. Without the `gc` step: stopped in 1 of 5 rounds, and both test loops ended with the server dead, cause unknown |
| Reload during a session, then disconnect, 5 times | Survives 5 of 5, notice sent 5 of 5, next attach stops |

## When it says no
| Code | Means | Do |
|---|---|---|
| `not_running` | The server has no process in its prefix | `kiln run --server <lab>` |
| `refused` | A Steam library, or more than one server process in the prefix | Use a lab or another instance |
| `unsupported` | A folder that is neither a lab nor a known instance, or a remote server | Name a lab or an instance |
| `tool_missing` | No netcoredbg at the path in the message | See [Set up](#set-up) |
| `not_found` | The server, its prefix or its wine could not be found | Check the name with `tools/lab/lab.sh list` |

With `--dap`, the same message comes back as the error of every DAP request, so your editor shows it. Only `disconnect`
succeeds: nothing is attached, and the adapter then exits 0.
