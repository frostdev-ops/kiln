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

## Per-world config (`WorldConfig<T>`)
Settings that an admin tunes for each world: one file per world, in the world's folder next to `WorldData`
(Enshwick's per-world `settings.toml`). The class is the same kind as for `Config.Register` (comments, ranges, enums,
sections; see [config.md](../config.md)).
```csharp
public sealed class ShopRules
{
    [Comment("Gold a new character starts with, in this world."), Range(0, 100000)]
    public int StartGold { get; set; } = 50;
    [RequiresRestart] public int Slots { get; set; } = 4;
}

public sealed class ShopMod : Mod
{
    public override void OnLoad()
    {
        var rules = WorldConfig<ShopRules>();           // the same as Config.RegisterWorld<ShopRules>()
        rules.Changed += e => { if (e.Source == ConfigChangeSource.World) Log.Info($"now {rules.WorldId}: {rules.Value.StartGold} gold"); };
    }
}
```
- **File:** `kiln/data/worlds/<world id>/config/<mod id>-world.json` (a second one, `WorldConfig<T>("name")`:
  `<mod id>-<name>-world.json`). Kiln writes it from the class when the world has none. `ConfigFile.FilePath` and `.WorldId`
  tell which one is open. Saves.Backup does not copy these files; they are settings, not world state.
- **Values follow the world.** On a dedicated server there is one world and the file is read at `OnLoad`. A game client hosts
  one world after another: when the next one opens, the old values are put away (its file keeps them), the new world's file
  (or the defaults, and a new file) takes their place, and `Changed` fires with `Source == ConfigChangeSource.World` for every
  entry whose value differs. That happens before `WorldLoadedEvent`, so read `rules.Value` there. While no world is open
  `Value` holds the defaults (in memory; nothing is written).
- **A new world** has no id until its first save. The config runs on the defaults, `Set` works in memory, and the file is written
  with the id.
- **Edits** of the file while the world runs are picked up like any config (file watcher, five-second poll), `Source == File`.
  `[RequiresRestart]` entries wait for the next time this world opens, as Enshwick's settings do.
- `[Synced]` entries reach Kiln clients like those of a server config. `Config.All` and `Config.Schema()` (what the dashboard reads) list the
  config with `perWorld: true` and `world: <id>`. There is no offline schema snapshot for the launcher: the files of a world do not exist before it does.
- A world's file is not removed when the mod is.

Lab: `mods/tests/settings-test` ran on a lab server (a new world, with no id when the mod loaded): the config bound to the world
when the game gave it its id, one world closed and another loaded on Kiln.Saves' lab hooks (the order a hosted game runs, as
`world-switch-test` plays it), the first world's values came back from its file, and an edit of the file applied 3 steps later
(the watcher works under Wine). Not yet run in a game client: hosting a second world after the first.

## Mod texts (locales)
A mod ships its strings in `locales/<language>.json` beside `kiln.mod.json`. `Mod.Text` answers in the language of the game.
```json
// locales/en.json                      // locales/de.json
{ "greeting": "Hello {name}",           { "greeting": "Hallo {name}",
  "shop": { "buy": "Buy ({0} gold)" } }   "shop": { "buy": "Kaufen ({0} Gold)" } }
```
```csharp
Log.Info(Text("greeting", new { name = player.Name }));      // Mod.Text(key, args), on the tick thread or any other
var german = TextIn("de-de", "shop.buy", 12);                // a language you chose yourself (a player's own setting)
var l = Context.Locale;                                      // Languages, Keys, Has, TryGet, Missing("de"), Reload()
```
- **Files:** one JSON object per language, named by the language tag (`en`, `de`, `pt-BR`, `zh-CN`; case and `_` do not matter).
  Comments and trailing commas are fine. Values are strings; a nested object makes dotted keys (`shop.buy`).
- **Placeholders:** `{0}`, `{1}` for positional arguments; `{name}` for a named value (one object such as `new { name = "Ada" }`, or
  one dictionary); `{count:N0}` for a number format (always the invariant culture, so every server writes numbers alike);
  `{{` and `}}` for braces. A placeholder without a value stays as written.
- **Which text:** the file of `Game.Language` (`de-de`), then its main variety (`de`), then English (`en`). A few languages look
  in a close relative first: `es-mx` tries `es-419`, `nb` and `no` try each other. `ModLocale.LookupOrder("es-mx")` gives the list.
  A key no file has comes back as the key (so the gap shows on screen) and is logged once.
- **`Game.Language`:** a dedicated server says `en-us`, always. A game client says the language of the game's texts as
  kiln.ui reads it from the game (`de-de`, `pt-br`, ... the game's own keys), `en-us` before the game's UI has drawn a frame or
  when this build cannot tell. It is read when asked, so a player who changes the language sees the new one at the next call.
  **Not yet seen in a game client** (the client checklist has it).
- **Translators:** `Context.Locale.Missing("de")` lists the keys English has and German lacks; `kiln lint` says the same
  ([KL212](lint.md#kl212-locale-key)). A translation can be late: its player reads English meanwhile.
- A mod's items, talents and other block texts have their own per-language fields (`Texts`); this is for the strings your code shows.

| Other loader | Kiln |
|---|---|
| ShroudForge `locales/*.json`, `shroudforge.i18n.get(key)`, `shroudforge.locale()` | `locales/*.json`, `Mod.Text(key, args)`, `Game.Language` |
| Enshwick `src/texts.toml` (a table per language, `en` fallback), `i18n::language()` | `locales/*.json` (a file per language, `en` fallback), `Game.Language` |

## Status lines (`StatusLines`, in Kiln.Ui)
What a mod says about itself where an admin or a player looks it up. A toast is gone in seconds and a HUD line is always on
screen; a status line is read when someone asks (Enshwick's `add_status` and the `enshwick status` summary).
```csharp
StatusLines.Add(() => $"{_merged} zones merged, {_seams} seams");   // a source: Kiln calls it only when someone looks
StatusLines.Set("state", "waiting for the world");                  // or push a text; an empty text removes the line
```
- **A source** runs only when the status is read, on whichever thread reads it, as your mod. Keep it quick; read fields or
  atomics the tick thread wrote; touch no game state. A source that throws shows nothing and is logged once. Several lines are fine
  (`\n`); empty ones are dropped. A line has an id within your mod: the same id replaces it.
- **Where it shows:** the **Mods** tab of Kiln's menu on a client (under the table, refreshed a few times a second);
  `StatusLines.Summary()` as text, `StatusLines.Lines(modId)`, `StatusLines.All()`; `StatusLines.WriteToLog()` for a server's log;
  `kiln-admin`'s `mods` operation adds a `statusLines` list per mod for the dashboard.
- Lines go with the mod that added them. The registry works on a dedicated server too: nothing in it draws.

## Pacing (`Pacing`)
Where the engine waits and how many worker threads it starts (Enshwick's `pacing`): a mod uses them to make the game use less CPU.
```csharp
Pacing.OnServerWait((ref long ns) => { if (ns == Pacing.ServerOnlineWaitNs) ns *= 2; });   // fewer passes between steps
Pacing.OnLoopWait((in LoopWait w) => { /* sleep up to w.LeftNanoseconds when w.Milliseconds == 0 */ });
if (!Pacing.Available) Log.Info("this build's waits are not known: the handlers above are never called");
```
- **What runs where:** handlers run inside the game's loops, on the thread that waits, as often as it waits: keep them short, take no
  locks, call no game API, and expect several threads at once for a loop wait (a lab server's loops made over 100,000 waits a
  second, almost all of them the 0 ms polls below). They run in the mods' load order, each on the last one's answer. A handler that
  throws three times in a row is removed (logged).
- **Loop wait** (`OnLoopWait`): the event loop is about to wait on its handles. `Milliseconds` is the time left to its deadline in whole
  milliseconds, truncated; with less than a millisecond left the game waits 0 ms, again and again, until the deadline passes, which is
  the polling a handler can replace by one sleep of up to `LeftNanoseconds` (never past it: the game still does its own wait and sees every event).
  With no deadline `Milliseconds` is `int.MaxValue` and `LeftNanoseconds` is `long.MaxValue`. The handler cannot change the wait.
- **Server wait** (`OnServerWait`): the server loop picked its wait between two passes: `Pacing.ServerIdleWaitNs` (40 ms) with nobody
  online, `ServerOnlineWaitNs` (1 ms) with players, `ServerSlowWaitNs` (0.1 ms) after a pass longer than 14 ms. Change it with `ref`. The
  loop still wakes for an event and for its own timers; what handlers leave is clamped into 0 to `Pacing.ServerMaxWaitNs` (1 s). With nobody
  online a pass is the wait plus the step itself, so doubling the idle wait roughly halves the step rate. A longer wait with players online
  means fewer passes between the simulation steps and up to that much more latency for what arrives between them.
- **Worker caps** (`CapTaskWorkers`) cannot be set by a Kiln mod. The task system sizes its pools when the game starts, before the first server
  step, and a mod loads at that step: the call returns false (`Pacing.TaskWorkersStarted` is true) and logs why once. What caps the pools on a Kiln
  server is a file the operator writes, `kiln/config/kiln-pacing.json` (in the profile's config folder when a profile is active), read at boot:
  ```json
  { "taskWorkers": { "default": 4, "background": 3 } }
  ```
  or `{ "taskWorkers": 4 }` for both. A cap is the most workers a group starts with, besides the main workers the default group always adds (a lab server
  with 16 CPUs went from `Group 'Default' Worker:10 Main:2` and `Group 'Background' Worker:8` to `Worker:6 Main:2` and `Worker:3`; the ECS task queue,
  sized from the default group, went from 8 to 6). It applies at the next start. The lowest cap of every setter wins.
- **Which places exist** is a fact about the game build: `Pacing.Sites` says which Kiln found (`PacingSites.LoopWait`, `MessageWait`,
  `ServerWait`, `TaskWorkers`); a handler's `Found` says whether it will be called. A dedicated server has `LoopWait | ServerWait | TaskWorkers`;
  a client adds `MessageWait` (its loop's second wait, on handles and window messages) and has no worker caps. Kiln finds them with byte patterns
  in its symbol database (`sigdb/symbols/server.d/pacing.json`; how they were found: docs/re/pacing.md), so a game update that changes that
  code makes `Sites` shrink, never crash: `Pacing.Available` is false and the handlers are never called. A hook goes into the game only when a mod
  registers a handler for its place, and stays in; with no handler it costs a flag check, and with none registered the game's code is untouched.
- **Next to Enshwick's own loader** (kiln-enshwick hosting an Enshwick mod that paces the same places): both run. Kiln's hook goes in at the
  first handler, later than Enshwick's, chained over it. Started the other way round (`"start": "ready"` in `kiln-enshwick.json` with a worker cap
  in `kiln-pacing.json`, so Kiln's hooks are in first) Enshwick's loader no longer finds those places and its mods are told so; nothing else
  breaks (docs/kiln/engines/enshwick.md).
- **Checked in a lab server** (Steam build 26430253, nobody online): a loop-wait and a server-wait handler were called with consistent values;
  a handler that made the idle wait 80 ms gave about half the steps (12.0 Hz against 22.2), 10 ms gave 2.5x (54.8 Hz), disposing it brought the
  rate back (22.0 Hz), and a disposed handler was never called again; the worker caps above; next to Enshwick's optimizer mod too (below). Not run: players
  online (the 1 ms and 0.1 ms waits), a client.
- `set_change` and `change_data` in Enshwick's table are not pacing: they declare changes of vanilla data values (set, times,
  reset, merged across mods). Kiln's counterpart is the typed fields of `Kiln.Content` and, with the reflected-resource work,
  reversible overrides; they are not part of `Pacing`.
