# Lifecycle, the Mods API, IMC, hot reload

## Events (Kiln.Api `Lifecycle.cs`; posted on the event bus, tick thread, none cancelable)
| Event | Forge | When (dedicated server) |
|---|---|---|
| `ModsLoadedEvent` (`Loaded`, `NotLoaded`) | `FMLLoadCompleteEvent` | once, at the first server-world step, after the last `OnLoad` |
| `ServerStartingEvent` (`Step`) | `ServerStartingEvent` | right after ModsLoaded (step 1; the world steps, the game still loads) |
| `ServerStartedEvent` (`Step`, `Source`) | `ServerStartedEvent` | the first step after the game logs `Switching state from LoadEcsScene to Run` (seen through the game-log mirror, ~11 s / ~270 steps after step 1 in the lab). Without the mirror: at the first step; if the line never comes: after ~100 s (`Source` = `game-log` / `first-step` / `timeout`) |
| `ServerStoppingEvent` (`Step`, `Reason`) | `ServerStoppingEvent` | on Ctrl-C/SIGINT (also SIGTERM/SIGHUP/SIGQUIT), or the game's `Trigger gameflow shutdown` line; then every mod unloads in reverse order (`OnUnload`) |
| `ModReloadedEvent` (`Id`, `Version`, `Ok`, `Cascade`) | – | after a hot reload, cascade included; the new instance receives it |
| `ModDisabledEvent` (`Id`, `Faults`, `Reason`, `CrashReport`) | – | the fault policy disabled a mod (faults.md) |

```csharp
[Subscribe] private void OnStarted(ServerStartedEvent e) => Log.Info($"world live at step {e.Step}");
[Subscribe] private void OnStopping(ServerStoppingEvent e) => SaveMyState();   // still on the tick thread, world intact
```
`Lifecycle.Phase` (`Booting`, `Starting`, `Running`, `Stopping`, client: `Idle`), `Lifecycle.ModsLoaded`,
`Lifecycle.IsServerRunning`, `Lifecycle.StartedAtStep` tell a mod that loads later (hot reload) what it missed.

### How the stop is caught (Wine)
SIGINT reaches the process as a console Ctrl-C (`CTRL_C_EVENT`). Kiln.Host registers its handler
(`PosixSignalRegistration`) at the first server step, after the game registered its own, so Kiln's runs first. It
asks the tick thread to post ServerStopping and unload the mods, waits for that (at most `mods.stop_timeout_ms`,
default 5000 ms), and lets the signal through to the game, which then logs `Trigger gameflow shutdown, exit: Ctrl_C`
and saves. Lab log (SIGINT via `tools/lab/lab.sh stop`):
```
[I 00:37:03.937] [host] lifecycle: SIGINT received; stopping mods on the tick thread (up to 5000 ms) before the game shuts down
[I 00:37:03.944] [mod:sample-lib] lifecycle ServerStopping at step 4123 (SIGINT); tick thread True
[I 00:37:03.946] [host] lifecycle: server stopping (SIGINT, step 4123): 5 mod(s) notified and unloaded in 2.8 ms
[I 00:37:03.946] [host] lifecycle: mods stopped 9 ms after SIGINT; the game continues its shutdown
[I 00:37:03.987] [host] lifecycle: the game began its shutdown ('[app] Trigger gameflow shutdown, exit: Ctrl_C')
```
If no signal reaches Kiln (the game stops some other way) the game's own shutdown line starts the same sequence at
the next step, when the world still steps. The game's final save comes after ServerStopping (Kiln.Saves posts its
shutdown save event right after it).

### On a client (from the client-core work; not yet run in a client)
**Mods load in the game's menu** (like Forge loading mods at start-up): kiln.ui's UI tick (`kiln.ui.tick`,
the game's UI frame, which runs in every menu) makes Kiln.Host load the mods once the game's UI is up, before any world
exists, so a mod can put entries into the main menu (`Ui.Menus`, docs/kiln/ui.md). On a client, **`OnLoad` can run
before any world exists**: register things there (content definitions, menu entries, key bindings, windows, configs,
network channels, event handlers), but do world work in the world/server lifecycle events (`ServerStarted` for a
hosted world, `Client.Tick` / `Client.State` for the client world). Calls that need a world thread (e.g. Content.Set
or other world services) answer "wrong thread" / "unavailable" in the menu. In the menu there are no mod ticks
(`Context.Tick`), `Scheduler.Post` / `await NextTick()` work waits for the first world step, and the control folder
is polled from world steps only; Kiln's service tick (queued events, config watchers) runs about 30 times a second.
Without kiln.ui (or with `KILN_NO_MENU_LOAD=1`) mods load at the first world step as before. Dedicated servers are
unaffected.

Without the menu load, mods load at the first step of either the hosted server world or the client world (in game). While the client
hosts, the server world drives the mods (services and `Context.Tick` on the server thread, as in a dedicated
server); otherwise the client world does (main thread). Never both at once. A hosted session's server world start
fires ServerStarting + ServerStarted (`Source` = `world-started`); its destruction fires ServerStopping
(`Reason` = `world-stopping`, on the destroying thread, world intact). Side "server" mods load at each hosted world's
first step and unload after its ServerStopping; client and both mods stay loaded (`Lifecycle.Phase` = `Idle` between
sessions). See docs/kiln/client.md, "Mods and sides".

## The Mods API (Forge's `ModList`)
```csharp
foreach (var m in Mods.All) Log.Info($"{m.Id} {m.Version} {m.State} {m.Error}");   // loaded first, in load order
if (Mods.IsLoaded("sample-lib")) …
var lib = Mods.Get("sample-lib");          // ModEntry: Id, Name, Version, Side, State, Error, Dependencies, LoadIndex, Info
var s = lib!.Stats;                        // Steps, TotalMs, PreMsPerStep, PostMsPerStep, MaxMs, Faults, RecentFaults, LastFault…
Mods.Current;                              // the calling mod
Mods.Side;                                 // Server / Client
var r = await Mods.ReloadAsync("my-mod");  // also UnloadAsync(id), LoadAsync(folder): run at the next step
```
`ModState`: `Pending`, `Loaded`, `Disabled` (fault policy), `Failed` (with `Error`), `Skipped` (other side,
`.disabled`), `Unloaded`. `ModInfo` (on `Context.Info` and `ModEntry.Info`) carries the whole manifest: side,
authors, description, license, url, apiVersion, dependencies, exports, features, `Advanced`.

## Inter-mod messages (Forge's InterModComms)
```csharp
// receiver (in OnLoad)
Context.Messages.On("register-recipe", m => Register(m.PayloadAs<RecipeSpec>()!, from: m.Sender));
// sender (any thread)
bool sent = Mods.Send("crafting-lib", "register-recipe", new RecipeSpec(…));
```
- Delivered on the tick thread at the receiver's next step, before its Pre handlers, in the receiver's context
  (its exceptions count as the receiver's faults).
- A message waits (at most 256 per receiver, oldest dropped) until a handler for its method exists;
  `Context.Messages.Take(method)` polls instead.
- Dropped when the receiver unloads, and when the sender unloads (its payload may hold its types).
- Payloads: BCL types, Kiln types, or types from an assembly the receiver exports. A type private to the sender is
  only visible as `object`.

## Hot reload
Control folder (`kiln/control/<name>.cmd` → `<name>.result.json`), or `Mods.ReloadAsync`:
| Command | Does |
|---|---|
| `mods` | every mod: id, version, state, error, side, load index, dependencies, exports, faults, ms/step |
| `load <folder>` | load `kiln/mods/<folder>` (retries mods that failed for want of it) |
| `unload <id>` | unload it and its dependents (reverse load order) |
| `reload <id>` | dependents unload, the mod reloads from disk, everything loads again; a mod that was never loaded is loaded from `kiln/mods/<id>` |
| `reload-test <id> <n>` | n reloads, one per step (auto-run tests paused), then a leak check |
| `test <id> [filter]` | run its `[KilnTest]` methods (testing.md) |
| `gc` | full collection; how many unloaded mod contexts are still alive |

After every unload Kiln flushes the framework's process-wide caches that can hold a mod's types (System.Text.Json
type metadata of shared options such as `JsonSerializerOptions.Default`, reflection-emit accessors; the runtime's
hot-reload `ClearCache` handlers). Without that, a mod serializing its own types with the default options never
unloads. What a mod must still release itself: threads, timers, sockets, files, static references held by *other*
(non-mod) code.
