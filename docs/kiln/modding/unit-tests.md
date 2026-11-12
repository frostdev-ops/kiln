# Kiln.TestKit: unit tests of a mod with no game

`Kiln.TestKit` runs a mod under `dotnet test`. A `KilnHarness` is a Kiln server with no game: a temporary server
folder with its `kiln/` tree, the mod loaded by Kiln.Host's own loader, ticks driven by the test, console commands,
fake players and every log line captured. A mod's `[KilnTest]` methods ([testing.md](testing.md)) run as xunit tests,
the same bodies that run in a server.

What it is not: there is no game, no game data and no native code. Kiln.Api, Kiln.Host and the managed services run
unchanged; a service the kit has no fake for finds no native table and reports itself unavailable, by its own code,
as on a build that lacks it. The kit never reads the game's files, so a test that needs an item catalog has to name
the author's own local catalog. The table at the end is generated from the kit and says, area by area, what is real,
what is a recording fake and what is unavailable offline.

## A first test
```sh
dotnet new kiln-mod -n MyMod
dotnet new kiln-unit-test -n MyMod.Tests --mod ../MyMod/MyMod.csproj
dotnet test MyMod.Tests
```
`kiln-unit-test` takes the path of the mod's project, relative to the new project's folder (`--mod`), the mod's entry
class (`--modType`, default `<Name>.<Name>Mod` from the project's file name, as `kiln-mod` names it), and, like the
other templates, `--feed` (a local feed: writes a `nuget.config`) and `--kilnSdkVersion`. It makes:
- `MyMod.Tests.csproj`: a `PackageReference` to `Kiln.TestKit` (the same version as `Kiln.Sdk`), xunit v3, and a
  `ProjectReference` to the mod.
- `ModTests.cs`: one test that loads the mod, steps, runs `help` as the console, joins and removes a fake player and
  looks for a line of the mod's `OnLoad`.
- `ModKilnTests.cs`: `class ModKilnTests : KilnTestSuite<MyMod.MyModMod>;`, every `[KilnTest]` of the mod as an xunit
  test (a mod with none gives one skipped test).

```csharp
[Fact]
public void The_mod_loads_steps_and_sees_a_player()
{
    using var kiln = new KilnHarness();
    var mod = kiln.Load<MyMod.MyModMod>();          // the mod's own kiln.mod.json, from its project
    kiln.Step();                                    // the first step: the mods load and the server starts
    Assert.True(mod.IsLoaded, mod.Error ?? kiln.DumpLogs());
    Assert.True(kiln.HasLog("loaded", mod.Id));
    var ada = kiln.Players.Join("Ada");
    kiln.Step();
    Assert.Equal(1, kiln.Run(() => Players.Count));
}
```

One `PackageReference` is enough. The `Kiln.TestKit` package carries `Kiln.TestKit.dll` and `Kiln.Host.dll` (run
time only; the test compiles against the kit), depends on `Kiln.Api` of the same version and references every
assembly of that package's `kiln/lib/` (`Kiln.Testing` and every `Kiln.<Service>.dll`), so they sit beside
`Kiln.Host.dll` in the test output as they sit in `kiln/host` in the game, and a test can call any service. Its
build targets copy a referenced mod project's `kiln.mod.json` to `<Assembly>.kiln.mod.json` and the other files of
the mod's output folder (`locales/`, `blocks/`, ...) to `<Assembly>.mod/` in the test output, which is where
`Load<TMod>()` looks. `KilnTestKitServices=false` and `KilnTestKitModFolders=false` turn either off.

## The harness by task

**Threads.** The harness has a tick thread of its own. `Game.IsTickThread` is true there only, so tick-thread API
from a worker fails as in the game. Every harness call runs on that thread and returns when done. Test code calls
Kiln API through it: `kiln.Run(() => Players.All)`, or as a mod, `kiln.As(mod, () => ...)`. One harness lives at a
time per process (Kiln's state is process-wide): a second waits for the first to be disposed.

**Loading.** `kiln.Load<TMod>()` loads a second, isolated copy of the assembly that holds `TMod`, as the game does:
its files are copied into `kiln/mods/<id>` and Kiln.Host's loader loads them into a collectible load context of
their own. The handle gives `Mod Instance`, never `TMod`, and the statics of the copy the test project references
are not the loaded mod's: `TMod.Something = 1` in the test changes nothing the mod sees. Read the mod's state
through Kiln API, its log, or reflection on `mod.Instance` inside `kiln.Run`. The manifest is a `ModManifest` built
in code (`kiln.Load<MyMod>(new ModManifest("my-mod").DependsOn("other", "^1.0"))`) or the mod's own file
(`<Assembly>.kiln.mod.json`, or a `kiln.mod.json` beside the assembly whose `entryAssembly` names it). Texts come from
`ModManifest.LocalesDirectory`, `<Assembly>.locales/`, `<Assembly>.mod/locales/` or that `kiln.mod.json`'s
`locales/`. `kiln.LoadFolder(path)` loads a built mod folder as Kiln's deploy copies one. Mods given before the
first step load with it, in dependency order; after it, `Load` is a hot load.

**Stepping and time.** `Step(n)` runs server steps in Kiln.Host's order (the first one loads the mods, fires
`ModsLoaded`, `ServerStarting` and `ServerStarted`). `Boot()` runs the first step if it has not run, `StepUntil(cond)`
steps until a condition holds. `CurrentStep` counts from 1. A step is virtual time: 40.8 ms
(`KilnHarnessOptions.WallDeltaMs`), with no sleeping. `WaitForWorldRunning` holds `ServerStarted` back until the test
calls `ReportWorldRunning()`; `GameLog(line)` delivers a line of the game's log to `ModContext.GameLog` handlers.
`Stop(reason)` stops the server as a signal does (`ServerStopping`, then every `OnUnload`); `Dispose` does it when
the test has not.

**Commands.** `kiln.RunCommand("time set 7")` runs a line as the server console through `Commands.Execute` and
returns its `CommandResult` (`Ok`, `Found`, `Denied`, `Error`, the reply lines in `Output`). `RunCommand(source,
line)` takes any `CommandSource` (`CommandSource.Remote("dashboard", admin: false)`), `RunCommand(player, line)` a
fake player. Permissions are Kiln.Permissions' real rules: `WritePermissions(json)` writes `permissions.json`,
`WriteServerConfig(json)` the server's `enshrouded_server.json` (user groups), both read at once. Chat itself is not
faked: replies come back in the result and reach no chat.

**Players.** `kiln.Players.Join("Ada", steamId, rights, isAdmin, position)` gives a `FakePlayer`; it is in game at
the next step (`Players.All`, `Joined`, `PlayerJoinedEvent`), and `Leave()` takes effect at the next step's scan
(`Left`, `PlayerLeftEvent`). Kiln.Players' own code runs against the kit's fake of `kiln.players`. A player joined
before the first step is already in game when the mods load, which a real server cannot do.

**Saved data.** `WorldData`, `PlayerData` and `EntityData` on player entities run Kiln.Saves' own store against the
kit's fake of `kiln.saves`, in the temporary folder. `kiln.World` has the world's `Id`, `Revision`, `Directory` and
`Save()`. The fake world saves as the game does: every 30 s of virtual time, when a player leaves and at the stop.
A new harness with `KilnHarnessOptions.ServerDirectory` and `WorldId` set to an earlier one's reads its last save;
`Kill()` skips the shutdown save (the mods' `OnUnload` still runs). `ServerDirectory` is for an isolated test folder,
never a real game/server folder: a nonempty folder without the kit's `savegame/kiln-testkit-world.json` marker is
refused before anything is written or removed. A reused test folder keeps its saved data and configs, but its
`kiln/mods` is cleared so the test can load the mods again.

**Config.** `Config.Register` of every kind writes into the temporary `kiln/config` (`kiln.ConfigDirectory`); a file
the test edits there reaches the change handlers through the file watcher, so pace the steps in real time
(`StepUntil(cond, pace: ...)`). `Mod.WorldConfig<T>()` binds to the harness world's folder.

**Logs.** `kiln.Logs` holds every line Kiln wrote while the harness lived: the mods' loggers, Kiln's services and
the host. `LogsOf(mod)`, `FindLogs(text)`, `HasLog(text, modId)`, `DumpLogs()` (for an assertion message);
`KilnHarnessOptions.LogSink` sees each line as it is written. `kiln/logs/kiln.log` in the temporary folder has
them too.

**Faults.** Kiln.Host's fault policy runs as in the game (5 faults in 60 s of virtual time disable a mod, a crash
report in the temporary folder). A harness call during which a mod faulted throws `ModFaultException` when the call
is done; `ThrowOnModFault = false` turns that off.

**Reload and the collection check.** `kiln.Unload(mod)` and `kiln.Reload(mod)` work like the control commands
(dependents first) and then make sure the old load contexts are gone; one still alive throws `ModLeakException`
naming the mod. The usual causes: a handler on a process-wide event, a static of a shared assembly, a thread or timer
of the mod's own, or a test local holding `mod.Instance` or `mod.Context`. In a Debug build a temporary of the test
method that reached into the mod can stay on the stack: read such things inside `kiln.Run` or a helper method of its
own. `Unload` right after a mod's auto-run `[KilnTest]` run started (manifest `tests.autoRun`, still awaiting) throws
`ModLeakException` too: Kiln.Host lets go of the run at its next step. Step until the run has ended, or pass
`checkCollected: false`.

**Capabilities.** `kiln.Capabilities.Is(name)` answers what `Game.HasFeature` answers, `Why(name)` gives the sentence
a mod could log, `All` lists the rows. `Override(name, available)` switches a game feature on or off, or a faked
service off, for the test; it cannot switch on a service the kit has no fake for, and an override changes the
answer only: nothing behind a feature is faked. `ClearOverride(name)` drops it.

## One test body, two runners
```csharp
public sealed class MeterTests
{
    [KilnTest]
    public void Adds_up() => Assert.Equal(3, Meter.Sum(1, 2));      // Kiln.Testing.Assert
}
```
In a server this runs with `kiln test my-mod` (or `tests.autoRun`). Under `dotnet test` it runs through a suite:
```csharp
public sealed class MyModKilnTests : KilnTestSuite<MyModMod>;      // Kiln.TestKit.Xunit
```
The suite gives one xunit test per `[KilnTest]`, named by the test's name (`dotnet test --filter
"DisplayName~Adds_up"`). All tests of the suite run one after another on one harness and one load of the mod, in the
runner's order (`Order`, the class's full name, declaration order), with the manifest's `tests.autoRun` held back.
`Setup(kiln)` loads other mods first (a test mod's mod under test), `LoadMod(kiln)` replaces the load (a
`ModManifest` in code, `LoadFolder`), `AllowUnavailable` lists unavailable names a test may ask for and still count.
A test's own skip, a failure with the runner's message and stack, and a timeout come through as xunit results; the
mod's log lines of the test go to the test's output.

## Offline skip rule
While a `[KilnTest]` body runs, the kit records every service and game feature it answered "unavailable". A test
that asked for one and whose body passed is reported skipped, with the names: `needs kiln.spawn, which the test kit
does not fake; the body passed`. A failed body stays failed, and a timed-out body stays a timeout (an xunit failure),
with the unavailable names added to its message. Merely checking an optional service never hides an unrelated
regression. To skip on an unavailable capability, let `KilnUnavailableException` reach the runner; catching it and
then failing another assertion is still a failure. So a test that needs combat is never passed offline by accident.
A test that skips itself
(`Assert.Skip("kiln.combat hits are not available")`) keeps its own message.

A test that registers a command asks for `kiln.chat` too (Kiln.Chat subscribes to players' chat lines for it), so it
is skipped offline though the command registry is real. When the test runs its lines through `Commands.Execute` or
`kiln.RunCommand`, never through chat, list `kiln.chat` in the suite's `AllowUnavailable`.

Known gap: a service that keeps the answer "no table" in a `Lazy` or `static readonly` asks only once per process,
so a later test that uses it is not caught. Kiln.Ui's render table is one; Kiln.Network asks for `kiln.net` once, when
the first channel opens, so a test that only opens a channel is not caught either (read `Network.Available` in it).

## The watchdog
Ordinary harness calls, including `Step`, `Unload` and `Reload`, have a 30 s watchdog budget
(`KilnHarnessOptions.CallTimeout`). `Dispose` bounds the server stop separately to 30 s
(`KilnHarnessOptions.StopTimeout`). A blocked mod callback is stopped and the harness abandoned, as for a stopped
test; the exception reports the mod id and the stack where the stop reached it. Disposal still retires the remaining
mods without running their callbacks and releases the harness for the next test.

A `[KilnTest]` that blocks the tick thread (a loop, `Thread.Sleep`, `.Result`, a lock) past its timeout and a grace
of 2 s (`KilnTestSuite.WatchdogGrace`) is stopped: an interrupt for a wait, a lock or a sleep, an abort for a loop. It
is reported as a timeout with the stack where it blocked, and the suite goes on with a fresh harness and a fresh
load (the next tests' output says so). In a server a test like that cannot be stopped ([testing.md](testing.md)).
What a stopped test leaves behind: the stopped harness's mods never ran `OnUnload` and never saw
`ServerStoppingEvent`, and threads the mod started itself keep running. A command or admin check that test code
registered outside a mod outlives the harness. The watchdog was measured on Linux only. The stack is what the
runtime has: in a Release build of the mod a method that was inlined, or whose last call became a tail call, is not
in it (a test whose whole body is `=> Thread.Sleep(...)` shows the sleep, not the test's name).

## What a test sees of real time
Steps are virtual: 40.8 ms each, with no sleeping, so `Step(1470)` is a minute of server time however fast it runs. `Environment.TickCount64`, `DateTime.UtcNow`, `Stopwatch` and `Task.Delay` are real time: a mod that measures
time with them sees almost none pass between steps, and an `await Task.Delay(...)` in a test waits for real. A
`[KilnTest]` timeout is real time. The config file watcher is real time too.

## What is real offline
<!-- coverage:begin: generated from Kiln.TestKit's Coverage.All; KILN_UPDATE_DOCS=1 and the DocTableTests rewrite it -->
| Area | Offline | Capability | What holds |
|---|---|---|---|
| Mod loading and reload | real |  | Kiln.Host's loader: kiln.mod.json, the resolver, a collectible load context per mod, hot load, unload and the reload cascade; Unload and Reload check that the old load context was collected |
| Lifecycle | real |  | OnLoad in dependency order, ModsLoaded, ServerStarting, ServerStarted (at the first step, or after the test's world-running line), ServerStopping, OnUnload in reverse order; Lifecycle.Phase and StartedAtStep |
| Ticks | real | `tick` | Tick.Pre and Tick.Post in Kiln.Host's order; virtual time (40.8 ms and 2 to 3 clock units a step, no sleeping): Environment.TickCount64 and DateTime.UtcNow stay real time |
| Events | real |  | Kiln.Api's event bus: [Subscribe], Events.On, Post, Enqueue, priorities and cancel; a mod's handlers go with its unload |
| Scheduler and await | real |  | Scheduler.Post from any thread, await Scheduler.NextTick, the mod's synchronization context; Game.IsTickThread is true on the harness's tick thread only, so tick-thread API from a worker throws as in the game |
| Config (global) | real |  | Config.Register of every kind (common, server, client): files in the temp kiln/config, edits on disk reach the change handlers (file watcher: pace steps in real time), bad files are refused and the values kept |
| Commands (console) | real |  | Kiln.Chat's command registry: [Command] methods, typed trees, help, permission levels; RunCommand as the console returns the reply lines. Chat itself (kiln.chat) is not faked: players' chat lines never arrive |
| Inter-mod messages | real |  | Mods.Send between loaded mods, delivered at the next step before the receiver's Pre, dropped with an unloaded sender |
| Locale files | real |  | a mod's locales/<language>.json, the lookup order and fallbacks; KilnHarness.Language switches the game's language (a dedicated server says en-us) |
| Fault policy | real |  | Kiln.Host's fault policy (5 faults in 60 s of virtual time disable a mod, ModDisabledEvent, a crash report in the temp folder); by default the harness call during which a mod faulted throws |
| Game log lines | real |  | ModContext.GameLog handlers and the lifecycle's watch run as in the game, on lines the test writes (KilnHarness.GameLog): the game writes none |
| Game features | unavailable |  | Game.HasFeature is false for every game feature (sigdb) unless the test overrides it; an override changes the answer only, nothing behind the feature is faked |
| Tests.RunAsync | unavailable |  | Kiln.Testing's Tests.RunAsync from mod code says "Kiln.Host is not running" offline |
| Players | recording fake | `kiln.players` | Kiln.Players' own code on the kit's kiln.players: KilnHarness.Players.Join/Leave take effect at the next step (Players.All, Find, Joined/Left, PlayerJoinedEvent/PlayerLeftEvent); entity slot + 1, Steam id, admin bit, user-group rights, the test's positions; 16 slots. Unavailable: raw records, the game-log replay, the position probe for non-players, native subscribers |
| Commands as a player, permissions | real |  | Kiln.Chat's Commands.Execute with a fake player as source (KilnHarness.RunCommand(player, line)) and Kiln.Permissions' real rules on permissions.json and enshrouded_server.json in the temp folder (WritePermissions, WriteServerConfig); @p and player names resolve against the fake players. Replies come back in the result, chat is not faked |
| Saved data (WorldData, PlayerData) | recording fake | `kiln.saves` | Kiln.Saves' store on the kit's kiln.saves: a world id and revision per server folder, the save cycle on the steps (WorldSaving, staged generation, WorldSaved), the game's autosave (30 s virtual), player-left and shutdown saves; a harness on the same folder reads the last save (KilnHarness.Kill: no shutdown save). Unavailable: backups, the world file's path, the snapshot stage, a client's hosted world |
| EntityData | recording fake | `kiln.saves` | on player entities: stored as PlayerData under the fake player's Steam id. On every other entity unavailable (EntityData.Available is false: it needs kiln.spawn, which is not faked before dev.26's fake world) |
| Config (per world) | real |  | Mod.WorldConfig<T>() binds to the harness world's folder (kiln/data/worlds/<id>/config) through Kiln.Saves and differs between world ids |
| kiln.net | unavailable | `kiln.net` | no fake: a true pair is a client hosting its world (a kiln.client fake, a client-side harness, a client tick) and shares one channel registry, so no handshake refusal could be tested; Kiln.Network reports itself unavailable |
| [KilnTest] tests | real |  | Kiln.Testing's own runner, started test by test through Kiln.Host (KilnHarness.RunTest, KilnTestSession, the xunit KilnTestSuite): same order, timeouts and results as in a server. Offline a passing body that asked for an unfaked service or a feature that is off is skipped with the names; failed bodies and timeouts remain failures with the names. A test that blocks the tick thread is stopped by the watchdog and reported as a timeout with the stack where it blocked |
| kiln.abilities | unavailable | `kiln.abilities` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.assets | unavailable | `kiln.assets` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.audio | unavailable | `kiln.audio` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.build | unavailable | `kiln.build` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.characters | unavailable | `kiln.characters` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.chat | unavailable | `kiln.chat` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.client | unavailable | `kiln.client` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.combat | unavailable | `kiln.combat` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.content | unavailable | `kiln.content` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.craftmenu | unavailable | `kiln.craftmenu` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.creatures | unavailable | `kiln.creatures` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.eml | unavailable | `kiln.eml` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.example | unavailable | `kiln.example` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.flame | unavailable | `kiln.flame` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.gamedata | unavailable | `kiln.gamedata` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.gameplay | unavailable | `kiln.gameplay` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.hooks | unavailable | `kiln.hooks` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.items | unavailable | `kiln.items` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.knowledge | unavailable | `kiln.knowledge` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.npcai | unavailable | `kiln.npcai` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.pacing | unavailable | `kiln.pacing` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.playerstate | unavailable | `kiln.playerstate` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.profile | unavailable | `kiln.profile` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.progress | unavailable | `kiln.progress` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.progression | unavailable | `kiln.progression` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.questmark | unavailable | `kiln.questmark` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.render | unavailable | `kiln.render` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.rules | unavailable | `kiln.rules` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.screen | unavailable | `kiln.screen` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.settings | unavailable | `kiln.settings` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.skills | unavailable | `kiln.skills` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.spawn | unavailable | `kiln.spawn` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.surfaces | unavailable | `kiln.surfaces` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.time | unavailable | `kiln.time` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.types | unavailable | `kiln.types` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui | unavailable | `kiln.ui` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui.tick | unavailable | `kiln.ui.tick` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.vfx | unavailable | `kiln.vfx` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.world | unavailable | `kiln.world` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.worldtables | unavailable | `kiln.worldtables` | no fake: the service finds no native table, so its own Available is false and its row says offline |
<!-- coverage:end -->

Planned for dev.26: a fake world laid out by the generated `Kiln.Game` types, and recording fakes for combat,
spawn, items, time, chat, quests and skills. Hooks, memory, Impact programs, AI and rendering stay unfaked.

## What was checked, and what was not
Run on Linux (.NET SDK 10.0.113) for dev.25, with every package restored from a feed packed from the tree:
- Kiln.TestKit's own tests (`managed/tests/Kiln.TestKit.Tests`), the table check on this page among them: it fails
  when a `Coverage` row changes and the page does not (checked by changing one row).
- `dotnet new kiln-mod` then `dotnet new kiln-unit-test` against the packed feed: `dotnet test` passed (the harness
  test passed; the suite gave one skipped test, as `kiln-mod` makes no `[KilnTest]`). With `kiln-mod --tests` the
  harness test and both `[KilnTest]` methods passed. With the mod's `OnLoad` made to throw, `dotnet test` failed
  (`ModLoadException`).
- A real mod from outside Kiln's repository (a training-dummy mod with a damage meter, its files unchanged, a new test
  project beside it): its two meter tests passed; its two tests that need spawn, build and combat were skipped, one
  by the kit's rule (`needs kiln.spawn ...`), one by its own `Assert.Skip` naming `kiln.combat`. Harness tests of it
  passed: it loads with its own manifest and `blocks/`, steps, a fake player leaving reaches its `Players.Left`
  handler, and 20 reloads and the unload leave no load context. Its damage report in chat needs chat, spawn and
  combat, so it was not run offline. Nothing in its load wrote a warning or an error in the mod's name; its
  `blocks/dummies.json` sits in the mod folder but nothing reads it offline, and no line says so.
- `tk-parity` (`managed/tests/Kiln.TestKit.Tests/mods/TkParity`) offline: its event and command tests passed, its
  three network tests were skipped for `kiln.net`. The same folder in a lab server: not run when this page was written.

Not checked: Windows (the package's targets, the harness's temporary folders, the watchdog's stop), macOS, Visual
Studio's and Rider's test explorers (only `dotnet test` was run), and any SDK other than 10.0.113.
