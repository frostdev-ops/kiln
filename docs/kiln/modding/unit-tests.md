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
`WriteServerConfig(json)` the server's `enshrouded_server.json` (user groups), both read at once. The replies come
back in the result.

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

**A world of entities (since 0.2.0-dev.27).** `kiln.Entities` is a `FakeWorld`: entities with components laid out by
Kiln.Game's types, under the real Kiln.World. A mod's `World.Has`, `TryRead`, `Find`, `Query`, `Position`,
`Ecs.TryGet<T>` and `Reflected.Component` read it, and its tracked writes run on it with kiln.world's rules, so the
same world code runs offline and in a server.
```csharp
using var kiln = new KilnHarness();
var mod = kiln.LoadFolder(builtModFolder);          // or Load<TMod>(new ModManifest("my-mod").Writes("Fuel"))
kiln.Step();                                        // the server starts: the mods' "writes" are read now
var can = kiln.Entities.Create(new WorldPosition(10, 20, 30));
kiln.Entities.Set(can, new Ecs.ScaleComponent { uniformScale = 1f });      // the test's hand: untracked, at once
var r = kiln.As(mod, () => World.Write(can, new Ecs.ScaleComponent { uniformScale = 2f }));   // the mod's call
kiln.Step();                                        // the world steps: the write runs and answers
Assert.True(r.Result.Ok, r.Result.Reason);
Assert.Equal(2f, kiln.Entities.Get<Ecs.ScaleComponent>(can).uniformScale);
```
- **The test's hand:** `Create()`, `Create(position)`, `Destroy`, `Set<T>`, `Get<T>`, `TryGet<T>`, `Remove<T>`,
  `SetBytes` / `GetBytes` (a component by name, as bytes), `SetPosition`, `All`, `Exists`. They put things into the
  world as the game would have: no write tracking, no event. With Kiln.Game beside the tests a component name it
  does not know is refused (`ArgumentException`): an entity of the game carries no other. Where an entity stands is
  its `CurrentTransform` component, as in the game: `Create(position)` and `SetPosition` write it, `World.Position`
  and `World.Transform` decode it, and a store into its bytes moves the entity. A fake player's entity stands at the
  player's position (its `CurrentTransform` follows `FakePlayer.Position` when the world steps, as the game's movement
  writes it; `SetPosition` on the player's entity moves the player too) and can be given components; they go with the
  player, and the next player in the slot (the same entity id: slot + 1) starts with none. Reads never change the
  world.
- **Tracked writes** (`World.Write`, `Update`, `WriteField`, `WriteAt`) follow kiln.world, and the kit is tested at
  the native service's own boundaries (`native/services/world/tests/parity.json`, which the native host test reads
  too): the write runs at the start of the harness's next step and its task completes in that step; one step runs 512
  writes and the rest wait for the next steps; 4,096 writes not yet answered, refusals included, are the most that can
  wait (the next answers `Dropped` at once); one step delivers 1,024 answers. A write whose entity is removed while it
  waits never runs, also when the id comes back (a player who leaves, and the next player in the slot); a write carries
  its entity's generation (a `SpawnTime` the test gives it with `SetBytes(e, "SpawnTime", …)`), compared right before
  the write, and one for an entity without a `SpawnTime` is not kept waiting behind a full step (it answers `Dropped`
  at once). A mod whose `"writes"` cannot be read loads, and its writes answer `NotDeclared` (`ListRefused`). A type the
  mod's manifest does not list under `"writes"` is refused with the sentence that says what to add. The lists are read
  from the manifests in the harness's mods folder at its first step, as kiln.world reads them when a server starts
  (folders in name order, without `.disabled` ones, client-only mods or an id two folders share), so a mod loaded
  later (a hot load) is refused, as in the game; a name Kiln.Game does not know is `UnknownComponent` and takes none
  of the 91 places. Without Kiln.Game beside the tests the kit cannot tell a component name from a typo: it says so
  in the log at the first step and refuses every listed name. A mod built with Kiln.Sdk and loaded with `LoadFolder`
  has the list its build made; for
  `Load<TMod>` with a manifest of the test's own, `ModManifest.Writes("Fuel", …)` lists them.
  `ComponentWrittenEvent` is posted for a change, and `kiln.Entities.Writes` records every write that reached the
  world, with the bytes before and after. `World.WriteInfo` may be asked from any thread.
- **The game's side** is the test's to script. `kiln.Entities.GameWrites<Ecs.Fuel>("fuel_setup", (EntityId e, ref
  Ecs.Fuel f) => …)` is a program of the game that writes the type every step: tracked writes of the type then name
  it (`GameWriters`), wait one more step and say whether the value stayed (`Kept`); a writer that derives the value
  makes `Kept` false. `kiln.Entities.Track("Fuel", save: true, replication: false)` says what the game's write
  tracking does with a type (`SaveTracked`, `ReplicationTracked`; null otherwise: the kit cannot know).
- **Game types.** With a Kiln.Game beside the tests (the mod references it), the kit's `kiln.types` holds the layouts
  that Kiln.Game was generated with, so Kiln.Game's first-use check passes for the types the mod was compiled
  against, and `GameTypes.Find`, `Reflected.Component(e, "Fuel")["dataStorage[0]"]` and `World.WriteField` work.
- **Not faked, and they say so:** world programs (`EcsPrograms.Available` is false: there is no scheduler), game
  systems and their hooks (`GameSystems`), the game's event streams (`GameEvents`), teleport (`Teleport.To` answers
  Failed), the game's attribute formulas (a write over the maximum is not clamped), a save of the world's entities,
  and everything behind a component: no program of the game runs, so a `Health` of 0 kills nobody. Spawning,
  items, combat and the other services have recording fakes of their own or none (below, and the table).

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

## Recording fakes (since 0.2.0-dev.27)
A recording fake is a native table of the kit's under the service's real managed code: what a mod asks is answered
from a small model of the game and recorded, so the test can read what the mod did, in order and with the step. What
a fake does not model it answers as the service does when that part is off: the call says unavailable, the
capability row says offline, never "ok". A test switches a faked row off with `kiln.Capabilities.Override(name,
false)`; the fake then answers as the native service does without it. The table below has each fake's row.

- **Spawn** (`kiln.Spawned`). `Spawn.Entity`, `TryEntity`, `Group`, `Item` make an entity in the fake world at once
  (`kiln.Entities`), at the position asked, turned by the yaw, with a `SpawnTime`; `EntitySpawnedEvent` and
  `EntityDestroyedEvent` are Kiln.Spawn's own. Spawning works from the step the server started (`spawn.world` is
  pending before, as while a server loads its save). The kit does not know the game's templates: the test says which
  ones the game has, `kiln.Spawned.AddTemplate("Enemy_Vukah_Melee", TemplateKind.Creature, "Vukah")`, and an unknown
  one is refused as the game refuses it (`ArgumentException` from `TryEntity`, a `SpawnException` from `Entity`). The
  generic item pickup is always there. `Destroy`, `Inspect`, `Exists`, `Find`, `FindByName`, `EntityCount`,
  `Tracked`, `CleanupTracked` and `Refresh` read the fake world, so an entity the test moves or removes with
  `kiln.Entities` is seen moved or gone. `kiln.Spawned.All` lists what is still there (template, tag, origin,
  position, item and amount for a drop), `kiln.Spawned.Calls` every spawn, destroy and cleanup with its answer.
  `EntityData` works on the fake world's entities. Not modelled: extra spawn components (`spawn.components` is
  offline), what a template does (a creature neither moves nor fights), its level, `DoNotSave`, saving spawned
  entities with the world.

- **Chat** (`kiln.Chat`, `player.Chat`, `player.Say`). The fake players in game are the chat's players.
  `ada.Say("hello")` types a line: at the start of the next step it reaches `Chat.Received` handlers,
  `ChatMessageEvent` and, for a line that starts with `/`, the command as Ada; a line nobody consumed is shown to every
  player, Ada too, as `Ada: hello`. `Chat.Tell`, `Broadcast`, `Say` and command replies are shown at once, from the
  step the server started (a send before that says the game server was not seen yet). `ada.Chat` is what Ada's chat
  showed, in order; `kiln.Chat.Typed` every typed line (`Consumed` when a handler or a command took it),
  `kiln.Chat.Sent` every server line with the mod that sent it. Not modelled: the game's inbox
  (`Chat.Diagnostics.LastLine` is unavailable), `"enableTextChat"` (the chat is always on), a chat player's account
  hash and connection state (0).

- **Items** (`kiln.Items`, `player.Inventory`). The kit does not know the game's items: the test adds the ones its
  mod uses, with their stack size, `kiln.Items.AddItem("Material_T1_Twigs", maxStack: 50)` (gear with
  `gear: true` and its level range); an unknown item is refused as the game refuses it. A fake player in game has an
  inventory of `kiln.Items.PlayerInventories` generic 8-slot inventories (5 by default: the kit does not model the
  game's action bars, equipment and backpack); `kiln.Items.AddContainer(kiln.Entities.Create(at), 3)` gives an entity
  of the fake world one. `Items.Give` and `Take` run as one transaction at the start of the next step and their task
  completes then: a give tops up stacks first, then fills empty slots, gear one per slot with an item entity of its own;
  what does not fit is never created (`Partial`, or `NoRoom`, also with `AllOrNothing`); a take is all or nothing and
  destroys gear's item entity; a dry run changes nothing; a mod that unloads leaves its queued requests to run, unanswered,
  as kiln.items does. `Read`, `FindInventories`, `Lookup` and `All` read the fake.
  `player.Inventory.Count("…")`, `.Slots`, `.Put(item, n)` and `.Clear()` are the test's view and hand;
  `kiln.Items.Calls` lists every give and take with what became of it. Not modelled, and offline: water, moves between
  inventories, container names, transfer hooks, item instances, equip and unequip, recipe results, upgrades and gem
  slots at creation.

- **Combat** (`kiln.Combat`). The kit does not know the game's health values: the test gives an entity of the fake
  world or a fake player health, `kiln.Combat.SetHealth(wolf, 100, 100)`. A mod's `Combat.Damage` or `Kill` applies at
  the start of the next step (its own `EntityHurtEvent` comes at once, from Kiln.Combat); `kiln.Combat.Hit(target, 40,
  attacker)` is a hit of the game: while a mod handles `EntityHurtEvent` or `EntityHealEvent`, it is held one step for
  the handlers (they may change the amount or cancel it, as on a server) and applies at the start of the step after;
  with no such handler it applies at the next step. `EntityDamagedEvent` and `EntityDeathEvent` (health below 1, the
  killing blow's attacker) follow at the next step, as the game's death check finds it (healed back before then, the
  entity lives; a player's health ends with that player); `SetHealth` to 0 kills without a killer, god mode drops damage
  and keeps 1 on a set, `Heal` adds up to the maximum, `kiln.Combat.Respawn(player, 150)` brings a dead player back
  (`PlayerRespawnEvent`). `kiln.Combat.Health(e)` and `kiln.Combat.Hits` are the test's view. Not modelled, and
  offline: the game's attributes (`combat.attributes`), the hold filter, fall damage, regeneration, attribute heals,
  knockback, loot drops, the game's damage formulas, armour and kill credit.

- **Time** (`kiln.Time`). The day/night clock runs on the harness's virtual time from the first step: a new world
  starts at 06:00, the day phase runs to 18:00 in `kiln.Time.DayLength` (the game's default 30 minutes), the night in
  `NightLength` (12 minutes), each step moves it by its 40.8 ms times the speed a mod holds (`Time.SetSpeed`, `SetCycle`).
  `Time.State`, `OfDay`, `IsDay`, `Set` (forward to the next such hour, or the game's reset) and `TimeSetEvent` are
  Kiln.Time's; `DayStartedEvent` and `NightStartedEvent` come as Kiln.Time polls the clock (every 12 steps). Set the
  phases to 2 minutes and the clock near the change (`kiln.Time.Set(17.9)`) to reach a night in a few dozen steps.
  `kiln.Time.Calls` lists the mods' sets and speeds. Not modelled, and offline: the weather (`time.weather`,
  `time.weather_choose`: its table, layers, choice and reset), the sync probe, sleeping, saving the time with the world.

- **Quests and talent trees** need no fake: Kiln.Quests' quest engine and Kiln.Skills' talent trees are Kiln's own
  managed code, and they run on the fake players and saves. A quest a mod defines is started, advanced and completed
  for a fake player through `Quests.Start`, `Advance` and `Complete`, with `QuestStartedEvent`, `QuestStepEvent` and
  `QuestCompletedEvent`; a node of a mod's talent tree is unlocked with `Skills.Unlock` and read back with `LevelOf`
  and `Points`, with `TalentUnlockingEvent` (a handler may refuse) and `TalentChangedEvent`. Their native parts stay
  offline: the game's journal and knowledge (`kiln.knowledge`), quest markers (`kiln.questmark`), the game's own skill
  tree (`kiln.skills`, `GameTree`) and the game's levels and points (`kiln.progression`).

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
that asked for one and whose body passed is reported skipped, with the names: `needs kiln.settings, which the test kit
does not fake; the body passed`. A failed body stays failed, and a timed-out body stays a timeout (an xunit failure),
with the unavailable names added to its message. Merely checking an optional service never hides an unrelated
regression. To skip on an unavailable capability, let `KilnUnavailableException` reach the runner; catching it and
then failing another assertion is still a failure. So a test that needs combat is never passed offline by accident.
A test that skips itself
(`Assert.Skip("kiln.combat hits are not available")`) keeps its own message.

A test that registers a command asks for `kiln.chat` too (Kiln.Chat subscribes to players' chat lines for it); since
0.2.0-dev.27 the kit fakes `kiln.chat`, so such a test is no longer skipped and needs no `AllowUnavailable`.

Known gap: a service that keeps the answer "no table" in a `Lazy` or `static readonly` asks only once per process,
so a later test that uses it is not caught. Kiln.Ui's render table is one; Kiln.Network asks for `kiln.net` once, when
the first channel opens, so a test that only opens a channel is not caught either (read `Network.Available` in it).

## The watchdog
Ordinary harness calls, including `Step`, `Unload` and `Reload`, have a 30 s watchdog budget
(`KilnHarnessOptions.CallTimeout`). `Dispose` bounds the server stop separately to 30 s
(`KilnHarnessOptions.StopTimeout`). A blocked mod callback is stopped and the harness abandoned, as for a stopped
test; the exception reports the mod id and the stack where the stop reached it. Disposal still retires the remaining
mods without running their callbacks and releases the harness for the next test. In an xunit suite a shutdown failure
is a fixture-cleanup failure, not just a diagnostic, so a blocked `OnUnload` cannot leave the suite green.

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
| Commands (console) | real |  | Kiln.Chat's command registry: [Command] methods, typed trees, help, permission levels; RunCommand as the console returns the reply lines; players type command lines in chat through the fake kiln.chat (FakePlayer.Say) |
| Inter-mod messages | real |  | Mods.Send between loaded mods, delivered at the next step before the receiver's Pre, dropped with an unloaded sender |
| Locale files | real |  | a mod's locales/<language>.json, the lookup order and fallbacks; KilnHarness.Language switches the game's language (a dedicated server says en-us) |
| Fault policy | real |  | Kiln.Host's fault policy (5 faults in 60 s of virtual time disable a mod, ModDisabledEvent, a crash report in the temp folder); by default the harness call during which a mod faulted throws |
| Game log lines | real |  | ModContext.GameLog handlers and the lifecycle's watch run as in the game, on lines the test writes (KilnHarness.GameLog): the game writes none |
| Game features | unavailable |  | Game.HasFeature is false for every game feature (sigdb) unless the test overrides it; an override changes the answer only, nothing behind the feature is faked |
| Tests.RunAsync | unavailable |  | Kiln.Testing's Tests.RunAsync from mod code says "Kiln.Host is not running" offline |
| Players | recording fake | `kiln.players` | Kiln.Players' own code on the kit's kiln.players: KilnHarness.Players.Join/Leave take effect at the next step (Players.All, Find, Joined/Left, PlayerJoinedEvent/PlayerLeftEvent); entity slot + 1, Steam id, admin bit, user-group rights, the test's positions; 16 slots. Unavailable: raw records, the game-log replay, the position probe for non-players, native subscribers |
| Commands as a player, permissions | real |  | Kiln.Chat's Commands.Execute with a fake player as source (KilnHarness.RunCommand(player, line)) and Kiln.Permissions' real rules on permissions.json and enshrouded_server.json in the temp folder (WritePermissions, WriteServerConfig); @p and player names resolve against the fake players. Replies come back in the result and, through the fake kiln.chat, in the player's chat |
| Saved data (WorldData, PlayerData) | recording fake | `kiln.saves` | Kiln.Saves' store on the kit's kiln.saves: a world id and revision per server folder, the save cycle on the steps (WorldSaving, staged generation, WorldSaved), the game's autosave (30 s virtual), player-left and shutdown saves; a harness on the same folder reads the last save (KilnHarness.Kill: no shutdown save). Unavailable: backups, the world file's path, the snapshot stage, a client's hosted world |
| EntityData | recording fake | `kiln.saves` | on player entities: stored as PlayerData under the fake player's Steam id; on the fake world's entities (kiln.spawn's fake finds them) kept with the world's mod data |
| Config (per world) | real |  | Mod.WorldConfig<T>() binds to the harness world's folder (kiln/data/worlds/<id>/config) through Kiln.Saves and differs between world ids |
| Spawn | recording fake | `kiln.spawn`, `spawn.create`, `spawn.destroy`, `spawn.entities`, `spawn.templates`, `spawn.world`, `spawn.components` | Kiln.Spawn's own code on the kit's kiln.spawn: a spawn (template or item drop) makes an entity in the fake world at once, at its position and yaw with a SpawnTime, from the step the server started (spawn.world pending before); the templates are the ones the test adds (KilnHarness.Spawned.AddTemplate) plus the generic item pickup, an unknown one is refused; Destroy, Inspect, Exists, Find, FindByName, EntityCount, the tracked list, CleanupTracked and Refresh read the fake world; EntitySpawnedEvent and EntityDestroyedEvent are Kiln.Spawn's; KilnHarness.Spawned.Calls records every spawn, destroy and cleanup. Offline: extra spawn components (spawn.components), what a template does, the level, DoNotSave, saving spawned entities and re-finding tracked ones after a restart |
| Chat | recording fake | `kiln.chat`, `chat.send`, `chat.receive`, `chat.players` | Kiln.Chat's own code on the kit's kiln.chat: the fake players in game are the chat's players; a line a player types (FakePlayer.Say) reaches Chat.Received, ChatMessageEvent and chat commands at the start of the next step, and one nobody consumed is shown to every player; Tell, Broadcast, Say and command replies are shown at once from the step the server started, per player (FakePlayer.Chat, KilnHarness.Chat). Offline: the game's inbox (Chat.Diagnostics.LastLine), enableTextChat, the account hash and connection state of a chat player |
| Items | recording fake | `kiln.items`, `items.transaction`, `items.read`, `items.program`, `items.fuel`, `items.move`, `items.names`, `items.transfer`, `items.moves`, `items.instances`, `items.equip`, `items.pide_options`, `items.result_peek`, `items.recipe` | Kiln.Items' own code on the kit's kiln.items (the v1 table): the items are the ones the test adds (KilnHarness.Items.AddItem, with their stack size), an inventory per fake player in game (FakePlayer.Inventory, 5 generic 8-slot inventories unless the test says) and per container entity (AddContainer); Give and Take run as one transaction at the start of the next step (stacks topped up first, gear one per slot with an item entity, Partial or NoRoom when it does not fit, take all or nothing, dry runs), ItemsGivenEvent and ItemsTakenEvent are Kiln.Items'; Read, FindInventories and the catalog read the fake; KilnHarness.Items.Calls records each. Offline: water, moves, container names, transfer hooks, item instances, equip, recipe results, upgrades and gem slots, the game's player inventory layout |
| Combat | recording fake | `kiln.combat`, `combat.health`, `combat.hits`, `combat.deaths`, `combat.actor_states`, `combat.falls`, `combat.regen`, `combat.attr_heals`, `combat.attributes`, `combat.hold_filter` | Kiln.Combat's own code on the kit's kiln.combat (the v1 table): health is what the test gives an entity of the fake world or a fake player (KilnHarness.Combat.SetHealth); a mod's Damage or Kill and the game's hit (KilnHarness.Combat.Hit) apply at the start of the next step, the game's held one step for EntityHurtEvent / EntityHealEvent handlers, who may change or cancel it; EntityDamagedEvent, EntityDeathEvent (health below 1, the killing blow's attacker), PlayerRespawnEvent (KilnHarness.Combat.Respawn) at the next step; SetHealth (0 kills without a killer), Heal up to the maximum, god mode; KilnHarness.Combat.Hits records each hit. Offline: attributes, the hold filter, fall damage, regeneration, attribute heals, knockback, loot drops, the game's damage formulas and kill credit |
| Time | recording fake | `kiln.time`, `time.read`, `time.set`, `time.scale`, `time.weather`, `time.weather_choose`, `time.sync_probe` | Kiln.Time's own code on the kit's kiln.time: the day/night clock runs on the harness's virtual time from the first step (a new world at 06:00; day 06:00 to 18:00 in KilnHarness.Time.DayLength, 30 min by default, night in NightLength, 12 min), times the speed a mod holds (Time.SetSpeed); Time.State, OfDay, IsDay, Set (forward, or the game's reset), SetCycle, TimeSetEvent and the day and night events as Kiln.Time polls the clock; KilnHarness.Time.Calls records mods' changes. Offline: the weather (its table, layers, choice and reset), the sync probe, sleeping, saving the time with the world |
| Quests (Kiln.Quests' engine) | real |  | a quest a mod defines (Quests.Define, quests/*.json) is started, advanced and completed for a fake player through Quests.Start, Advance, Complete, SetStep, Fail and Abandon, with QuestStartedEvent, QuestStepEvent, QuestCompletedEvent and its record in the world's saved data: the engine is Kiln's managed code on the fake players and saves. Unavailable: the game's journal and knowledge (kiln.knowledge), quest markers (kiln.questmark), the game's NPCs and their conversations |
| Skills (Kiln.Skills' talent trees) | real |  | a talent tree a mod registers (Skills.Register, skills/*.json "trees"): Skills.Unlock, LevelOf, Points, State, TalentUnlockingEvent (a handler may refuse) and TalentChangedEvent, per player in the saved data: Kiln's managed code on the fake players and saves. Unavailable: the game's own skill tree (kiln.skills: GameTree), the game's levels and points (kiln.progression), the tree screen and what an effect does in the game |
| kiln.net | unavailable | `kiln.net` | no fake: a true pair is a client hosting its world (a kiln.client fake, a client-side harness, a client tick) and shares one channel registry, so no handshake refusal could be tested; Kiln.Network reports itself unavailable |
| World: entities and components | recording fake | `kiln.world` | Kiln.World's own code on the kit's kiln.world: KilnHarness.Entities (FakeWorld) makes entities and puts components on them, laid out by Kiln.Game's types (a name they do not know is refused); World.Exists, Has, TryRead, ReadBytes, Find, Query, Position, Transform and WriteRaw read and store them, and no read changes them. Where an entity stands is its CurrentTransform's bytes, as in the game; a fake player's follows the player's position when the world steps, and a player's components go with the player (the next one in the slot starts with none). Unavailable: teleport, world programs (EcsPrograms: there is no scheduler), and everything behind a component (the game's programs do not run: a Health of 0 kills nobody) |
| World: tracked writes | recording fake | `world.tracked_writes` | World.Write, Update, WriteField and WriteAt with kiln.world's rules, tested at the native service's boundaries (native/services/world/tests/parity.json): the write runs at the next step and answers in it (one step later for a type a fake game program writes, with Kept), 512 a step and the rest later, 4,096 not yet answered at most (refusals included), 1,024 answers a step; a type the mod's manifest does not list under "writes" is refused with the sentence that says what to add (the lists are read at the first step, so a hot-loaded mod's are not; a name Kiln.Game does not know takes none of the 91 places, and without Kiln.Game every name is refused; a mod whose list cannot be read loads and its writes say why); a write carries its entity's generation (SpawnTime) and one for an entity without it is not kept waiting behind a full step; a write whose entity is removed while it waits never runs, also when the id comes back (a player's slot); ComponentWrittenEvent for a change, FakeWorld.Writes records each; WriteInfo from any thread. Not modelled: the game's attribute formulas (no clamp), which types the game saves or replicates (null unless FakeWorld.Track says), a save of the world's entities, a second world (a hosted game's) |
| Game types (Kiln.Ecs, Reflected, GameTypes) | recording fake | `kiln.types` | the kit's kiln.types holds the types of the Kiln.Game beside the tests (the layouts the mod was compiled against: about 1,500 structs, enums and arrays of the game's components and events), so Ecs.TryGet, Ecs.Has, Ecs.All, Reflected.Component and GameTypes.Find work on the fake world. Without a Kiln.Game in the test output the service is unavailable. Unavailable either way: game systems and their hooks (GameSystems), the profiler, the game's event streams (GameEvents), default values, alignments, and the game's other 9,500 types |
| [KilnTest] tests | real |  | Kiln.Testing's own runner, started test by test through Kiln.Host (KilnHarness.RunTest, KilnTestSession, the xunit KilnTestSuite): same order, timeouts and results as in a server. Offline a passing body that asked for an unfaked service or a feature that is off is skipped with the names; failed bodies and timeouts remain failures with the names. A test that blocks the tick thread is stopped by the watchdog and reported as a timeout with the stack where it blocked |
| kiln.abilities | unavailable | `kiln.abilities` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.assets | unavailable | `kiln.assets` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.audio | unavailable | `kiln.audio` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.build | unavailable | `kiln.build` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.caps | unavailable | `kiln.caps` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.characters | unavailable | `kiln.characters` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.client | unavailable | `kiln.client` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.content | unavailable | `kiln.content` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.craftmenu | unavailable | `kiln.craftmenu` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.creatures | unavailable | `kiln.creatures` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.eml | unavailable | `kiln.eml` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.example | unavailable | `kiln.example` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.flame | unavailable | `kiln.flame` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.gamedata | unavailable | `kiln.gamedata` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.gameplay | unavailable | `kiln.gameplay` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.hooks | unavailable | `kiln.hooks` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.knowledge | unavailable | `kiln.knowledge` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.npcai | unavailable | `kiln.npcai` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.pacing | unavailable | `kiln.pacing` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.playeranim | unavailable | `kiln.playeranim` | no fake: the service finds no native table, so its own Available is false and its row says offline |
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
| kiln.steamguard | unavailable | `kiln.steamguard` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.surfaces | unavailable | `kiln.surfaces` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui | unavailable | `kiln.ui` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui.tick | unavailable | `kiln.ui.tick` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.vfx | unavailable | `kiln.vfx` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.worldtables | unavailable | `kiln.worldtables` | no fake: the service finds no native table, so its own Available is false and its row says offline |
<!-- coverage:end -->

Hooks, memory, Impact programs, AI and rendering stay unfaked.

## What was checked, and what was not
Run on Linux (.NET SDK 10.0.113) for dev.26, with every package restored from a feed packed from the tree:
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

For dev.27, the fake world: `managed/tests/Kiln.TestKit.Tests/EntitiesTests.cs` (10 tests) and
`FakeWorldParityTests.cs` (12: the native limits, the kit's table field for field against
`native/services/world/tests/abi.json`, the declaration cases, 513 writes in one step, the limit with refusals, a
player who leaves while a write waits and the next player in the slot, CurrentTransform's bytes, WriteInfo from four
threads while the tick thread changes the world) load `tk-world`
(`mods/TkWorld`, a mod built with Kiln.Sdk whose build filled `"writes": ["Fuel", "ScaleComponent"]` from its code) and
runs its world code on `kiln.Entities`: typed and reflected reads, entity sets, tracked writes with their answers and
events, the refusals, a scripted game writer (`Kept` true and false), the lists read at the first step, a fake
player's entity, the unfaked parts. The same calls run in a lab server in `mods/tests/world-writes-test`
([../services/world.md](../services/world.md#tests-lab)); the two are not one test body, and the kit's attribute
writes are not clamped as the game's are. Not run: the fake world from the packed `Kiln.TestKit` package (only from
the tree).

For dev.27, the recording fakes: `SpawnFakeTests.cs` (6), `ChatFakeTests.cs` (4), `ItemsFakeTests.cs` (5),
`CombatFakeTests.cs` (4) and `TimeFakeTests.cs` (4) run `tk-fakes` (`mods/TkFakes`) against each fake: what it
models, the refusals, a test switching it off, the next harness starting clean, and its structs against the service's
own mirrors (sizes; the headers are mirrored by hand, not checked field for field). `FakesReviewTests.cs` (14) pins
where a review found a fake answering otherwise than the native service (`native/services/<service>/`) does for the
same call: a short status record, item names for the process, a cancel that keeps the transaction, a released owner's
hits, the death check, a player's health per session, switched-off chat, the items program, the kind mask, a new
world's day, a healing hit at the maximum, the world key's terminator. `ProgressEnginesTests.cs` (3) runs
`tk-progress` (a quest and a talent tree in code) on the fake players and saves. `SampleFakesTests.cs` (4) runs
Kiln's samples with their real logic: CreatureTweaks spawns and clears its Elite Vukah, AdminTools warns a player in
chat, SavedData labels the chest next to a player, SkillTreeSample's tree is unlocked for a player. Not run: any of it
from the packed `Kiln.TestKit` package, and none of it against a game: what the fakes model is read from the headers,
Kiln's services' code and `docs/re/`, never from a lab run of the same test bodies.

Not checked: Windows (the package's targets, the harness's temporary folders, the watchdog's stop), macOS, Visual
Studio's and Rider's test explorers (only `dotnet test` was run), and any SDK other than 10.0.113.
