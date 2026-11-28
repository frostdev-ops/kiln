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

**Building-tool declarations (since 0.2.0-dev.30.5).** `ToolMaterialPoolBlock`, `TerrainBrushBlock` and
`BuildingToolBlock` can load offline. New policy pools and computed `toolBrushShapes` descriptors are pending,
not live game registrations: the lowered family is visible through `Blocks.Lowered`, but it publishes no items or
actions until its boot definitions are registered. A recipe whose output is a pending tool is recorded in
`Blocks.Lowered` without registering a live recipe; `Content.Available` remains false and ordinary content calls
still refuse offline. Tool pools are policy metadata, not extra game-visible pool rows;
the vanilla terrain pool stays unchanged. The kit embeds the 56-item `TerrainMaterials` membership and item-to-cell
mapping decoded from client build `5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77`,
registry resource `9931ffd4-6732-4123-9178-46dd1ffafb0d` (pool GUID
`256c43ba-dd20-4369-b3bf-f49c671a59ec`). A pool must use a member that places its declared terrain cell.
`kiln.WorldTables.PendingToolDefinitionsJson(modId)` copies the accepted policy and brush descriptors;
`TerrainBrushCells(modId, brushKey)` copies the computed sphere's raw fill bytes, not a live game resource.
`TerrainItem(name, itemId, cell)` scripts an item-to-cell mapping for a refusal test without adding the item to the
immutable vanilla pool. The kit does not run a material picker, payment, mining, undo, rendering or native tool hooks.

For definitions independent of runtime conditions, set `<KilnBakeWorldTables>true</KilnBakeWorldTables>` in the mod
project. The SDK records the policy pools and computed brushes in `blocks.tables.json`, which the game's boot
merge reads on the first start; it does not pretend they were live during the bake. See [baked.md](baked.md).

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

**Tasks.** A mod's `Context.Tasks` run on the kit's task pool: two worker threads, as on Kiln's own pool. A task's
hops back to the tick (`ToTick`, `OnTick`, `PostToTick`) run in the next `Step`, before `Tick.Pre`, and
`NextGateWindow` resumes after a step. The workers run in real time beside the steps, so a test waits for them with
`kiln.Tasks.Settle()`: it waits while their work runs and steps the server whenever every task waits for the tick, until
all of them have ended, and returns the steps it took. A task that never ends fails the test with a `TimeoutException`
naming it and what it waits for (after 10 s or 1000 steps by default; `Settle(maxSteps, timeout)` sets both).
`kiln.Tasks.Live` counts the tasks that have not ended. The pool's per-mod caps and per-step budgets are not modelled.
A task's world reads (`WorldConcurrent`) read the fake world's entities, between the harness's steps.
Since 0.2.0-dev.30.7, mod API 0.2.16 (no longer experimental). Neither mod nor test projects need a `KILNX003` opt-in
([tasks.md](tasks.md#project-setup)). A container made
with `kiln.Items.AddContainer` carries the game's `Inventory` component, so a query for it finds the container.
`kiln.Capabilities.Override("world.ecs_read", false)` is a game build with no exact engine profile: every read is
`Unavailable` and `OrOnTick` takes the tick path. `kiln.Entities.BusyReads = n` makes the next `n` frame-quiet reads
`Busy`. `KilnHarnessOptions.Side` sets the game process the tasks see (client-world hops):
`HarnessSide.DedicatedServer` (the default), `HarnessSide.JoinedClient` (no server world, so a hop there throws
`KilnUnavailableException`; the client world steps on the harness's tick thread) or `HarnessSide.HostingClient` (both
worlds; the client world on a main thread of its own, stepped after the server world in each `Step`).
`kiln.OnMainThread(...)` runs code on the client world's thread, so a task started there comes back there;
`kiln.LeaveWorld()` and `kiln.EnterWorld()` end and start the worlds' sessions, as leaving and hosting or joining again
do. Tasks are explained in [tasks.md](tasks.md).

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
them too (`kiln.LogFile`); read it with `kiln.ReadLogFile()`, not `File.ReadAllText`, which Windows refuses while
the harness holds the file open for writing.

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
answer only: nothing behind a feature is faked. `ClearOverride(name)` drops it. A row a Kiln service composes from
others (`queries.ground`, `queries.reachability`) can be switched off as well: `Kiln.Capabilities` and the service's
members read it off at once. Switching such a row on cannot lift it while the service reports it off (the worse of
the two is shown, as in the game). Whenever a row of the kit changes (an override, a fake's own row such as
`kiln.Queries.Voxels` turning the ray on) and when a harness starts, the services' composed rows are decided again,
so a raw `Kiln.Capabilities.Is` read follows at once instead of at the services' refresh every 25 steps.

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
  `kiln.Items.Calls` lists every give and take with what became of it. Gear (since 0.2.0-dev.30): every piece the
  fake makes has enhancement slots (rarity + 1; `kiln.Items.SetPerkSlots(item, n)` says how many perk slots its type
  fills), a level, a durability of 100 and a stamp, so `slot.Ref` (and a give result's `Ref`) name it for
  `Items.GearOf`, `SetRarity`, `SetEnhancements` and `SetLevel`; a write runs at the start of the next step with
  kiln.items' rules, the shown rarity and level follow a step later, and `kiln.Items.SetGear(item, unlocked: 1, level: 6)`
  plays the game's own change (a workbench unlock) for the gear events' read of the players' items. Max durability is
  not computed again after a change. Not modelled, and offline: water, moves between inventories, container names,
  transfer hooks, item instances (`Items.Held`, locations), equip and unequip, recipe results, upgrades and gem slots at
  creation. GiveEquip is modelled separately: configure the type with `kiln.Items.SetEquipmentSlotType`, then test
  logical equipment, Backpack/Take replacement, DryRun and EquipChangedEvent. It refuses dead players and joined clients;
  physical equipment-page allocation, visuals, stats and persistence are not simulated. Arrow, Spell and FishingBait
  follow kiln.items' ammo rules (since 0.2.0-dev.31.12): the item must be in the Ammunition category, the stack goes
  into a free cell of the player's fake backpack (NoRoom when there is none, even for Take) and stays selected there,
  and an instance-backed one (`gear: true`, an eternal spell) is created with its own item entity; a replaced eternal
  keeps its entity in another free cell (Backpack) or is destroyed (Take).
  `kiln.Capabilities.Override("items.give_equip.instance_ammo", false)` plays a build without that feature.

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

- **Boot phase** (`kiln.BootPhase`, since 0.2.0-dev.28) <a id="boot-phase-since-020-dev28"></a>. `Run(folder)` runs a boot
  mod's `OnBoot` as a start does: once, on an instance of its own in a load context of its own, with the same staging
  (`BootDefinitions`: canonical JSON, `DuplicateDefinition`, `NotInBootKinds`; `Blocks.Define` throws in the frame) and
  the same lowering through Kiln.Blocks (a definition that does not compile or names a block outside the `OnBoot`,
  `BootReferenceOutside`, fails the run; a data file's block of the same name wins, `ShadowedByFile`). The `BootRun` it
  returns has what was staged, what lowered, the state (`Committed`, or `ExcludedCallback` with the reason) and the
  issues; the outcome is the process's boot generation, so `kiln.LoadFolder(folder)` then loads a committed mod (its
  definitions' run-time parts and `BootDefinitionsCommittedEvent`) and refuses a failed one (`RestartRequiredBoot`), as a
  start does. `SetRecord(key, json)` scripts what `boot.GameData.Read` answers (an unscripted key is `NotFound`).
  Switching `mods.boot` off refuses the mod before its `OnBoot`; `mods.boot.game_data` off makes `Read` throw. Not
  modelled: the native kiln.boot and its collectors, so the boot generation's items, clones and nodes do not reach the
  kit's other fakes; several mods' drop rules; the stopped start of a `"required"` mod.
- **Program wraps** (`kiln.Programs`, since 0.2.0-dev.29) <a id="program-wraps"></a>. The kit's kiln.world is v4: Kiln.Systems'
  own code runs on it. There is no game scheduler offline, so the test declares the game's programs its mod wraps:
  `kiln.Programs.Add("fuel_setup", new FakeProgramLists { Read = ["CurrentTransform"], Write = ["Fuel"] }, slice => ...)`
  (the delegate stands for the game's update), or `kiln.Programs.Add<Ecs.Programs.FuelSetup>()` for a Kiln.Game view, whose
  lists and contract are then the program's, so `Systems.Wrap<Ecs.Programs.FuelSetup>()` is made and its view binds.
  `Systems.Find`, `All` and `Wrap` see the declared programs. At the start of every step the fake runs each program once
  per archetype of the fake world's entities that have its read, write and `With` components (of the types the world
  knows; template data and the `Dynamic*` state are not matched), none of `Without`: the Befores (high priority first),
  the Replace or the declared update (a Replace's `RunOriginal` runs it once), the Afters in the reverse order. A wrap
  applies at the next step (`Pending`, then `Active`) and goes at the step after `Dispose`. Per-entity stages iterate
  the slice and read and write the entities' components in the fake world (`EntityView`, typed views, `AroundEach`);
  a write marks once per entity and component (`kiln.Programs.Marks`) and raises `ComponentWrittenEvent` as on a server;
  `WhenArchetype` decides by the entity's component set; tick-thread APIs throw inside a handler. `kiln.Programs.Updates`
  counts the slices the update ran on. The fake times stages against 1 ms, excluding the declared original update;
  ten consecutive overruns disable the stage. Managed exceptions disable their stage and count toward the real mod
  fault policy. These host timings are not game frame-time evidence. Not modelled: the game's worker threads (every slice runs on the harness's tick
  thread, `IsWorldThread` is true; `systems.threads` and `LabSystems` are offline), the order of the game's programs
  among themselves (table order), singleton slots (empty: `Read` of `impact::AttributeRegistry` throws), the attribute
  formulas (`Attribute32.Changed()` checks the block and the program's registry declaration and changes nothing),
  native faults, client and prediction worlds (`systems.wraps.client` and `systems.wraps.prediction` are offline), boot reservations
  (`Systems.Define`: `systems.define` is offline), native SET.
- **Impact natives** (`kiln.ImpactNatives`, since 0.2.0-dev.29) <a id="impact-natives"></a>. The kit's kiln.abilities has
  its natives only: `Abilities.Natives.RegisterSync`, an `[ImpactNative]` method's generated registration, `DeclareTyped`,
  `Attach` and `StateOf` run on it, as on a dedicated server. Nothing runs a program offline, so the test makes the
  ECALLs: `kiln.ImpactNatives.Call(new FakeImpactCall("frostmark.OnHit", FakeImpactArg.Entity(target),
  FakeImpactArg.Float(1f)))` answers the call's `Error` and `Value` and the arguments' variables after it (`Arg<float>(1)`).
  The call runs as the native's thunk runs it, on a thread of its own while the tick thread waits (tick-thread API throws
  there): the typed declaration's check (0x2C another type or size, 0x18 a required argument not connected), the handler,
  0x11 when it threw, 0x1F for a native the registry does not hold (its mod unloaded, refused, unknown). As on a
  dedicated server, the world's native registry is there before the mods load: a native registered in `OnLoad` is live at
  once and takes one of the 19 keys the registry leaves for mods (the 20th is refused); the world's programs come in at
  the second step. The game's programs and the natives they call at which sites are the
  ones the test declares (`kiln.ImpactNatives.AddProgram(0x7a120ee8, new FakeEcall("applyDamage", site))`); a call of a
  game native (`new FakeImpactCall("applyDamage", ...) { Program = 0x7a120ee8, Site = site }`) runs the matching
  attachments in the service's order (Before by priority, one Instead, the game's native once, After), and the game's
  native does what `SetGameNative` scripts (else it pushes 1). `Attach` refuses as the service does, and one made before
  the world is `Pending`, then `Active` or `Off` (`Active` once its program is declared). `NativeContext.TryRead` and
  `TryWrite` reach the fake world's components (`kiln.Entities`) of impact_simulation's other_read and other_write lists
  (this game build's; `kiln.ImpactNatives.OtherRead` and `OtherWrite` can change them), from the first Kiln native call
  of the world while the simulated serial condition holds: `kiln.ImpactNatives.Serial = false` (impact_simulation not
  serial on the world's thread, reason 6) or the row `abilities.ecs_pin` switched off (the pin unknown, reason 7) closes
  the bridge for the rest of that fake world, as the service does, while the natives go on answering; the fake does not
  reproduce the game's scheduler. A write is stored as written (no attribute formulas) and raises `ComponentWrittenEvent`
  in the next step.
  `kiln.ImpactNatives.Calls` records each call. Not modelled: the programs' code, program registration, instances,
  attributes, sequences, buffs, `Natives.Register`'s suspending natives, the watchdog and the 0.2 ms budget (the other
  kiln.abilities rows are offline).
- **The game's inventory slots** (`kiln.UiInventory`, since 0.2.0-dev.30) <a id="inventory-slots"></a>. Ui.Inventory's own
  code runs on the kit's kiln.ui.inventory, so a mod's slot clicks and marks are tested offline. The test draws what the
  game would: `kiln.UiInventory.Draw(HudScreen.StorageChest, new FakeInventorySlot(Item: 0xAA, ItemEntity: 900, Holder:
  chest))` makes it the latest UI frame (`Drawn` lists it); `Hover(0)` puts the cursor on a slot (`Ui.Inventory.Hovered`);
  `Click(0, SlotButton.Left, SlotModifiers.Ctrl)` is the player's press: on a screen with a pointer (the in-game menu,
  player crafting, the backpack, a chest, a station, NPC crafting, a factory; never `HudScreen.InGame`, where the hotbar
  attacks) every `OnClick` handler whose button and exact modifiers match gets the `SlotClick` with the slot's item and
  `ItemEntity` (and, since 0.2.0-dev.30.3, `Pointer` at the slot's centre: the kit draws slot `i` at 10 + 90 * (i % 8),
  20 + 90 * (i / 8), 80 x 80, one pixel per UI unit), at once on the calling thread, and the answer says how many got it and whether the game would have seen
  the press (`Taken`). Nothing drawn, another screen, or a row switched off: no handler runs. Marks stay Kiln.Ui's:
  `kiln.UiInventory.MarkOf(0)` is what Kiln would draw on a drawn slot (an item mark of any mod first, then the
  `MarkWhen` rules in order), so `Mark`, `Unmark`, `ClearMarks` and `MarkWhen` are checked by reading it after a
  `Draw` (the mark follows the item to another slot). `Ui.Values.Slots()` and `SubscribeSlots` read the drawn frames.
  `kiln.UiInventory.ClickSubscriptions` counts the handlers held. A server half that checks the clicked item (the
  SlotMarks sample) is tested with the Items fake: give the item to a fake player, send its entity. Not modelled: the game's
  drawing and hooks, pixels (the mapping answers no viewport, so no mark is drawn), the 250 ms frame age, client sessions,
  the delivery after the next UI frame on the client's main thread, the game's popups (since 0.2.0-dev.31.9 a game leaves
  out the marks under its item menu and other popups; offline none is open: `MarkOf` answers as with no popup and
  `GameScreen.PopupOpen` is false).

```csharp
[Fact]
public void CtrlClickAsksToMarkTheItem()
{
    using var kiln = new KilnHarness();
    kiln.Load<MyMod>();   // its OnLoad: Ui.Inventory.OnClick(new SlotClickOptions { Modifiers = SlotModifiers.Ctrl }, ...)
    kiln.Step();
    kiln.UiInventory.Draw(HudScreen.Backpack, new FakeInventorySlot(Item: 0xAA, ItemEntity: 900));
    Assert.Equal(new FakeSlotClickResult(1, Taken: true), kiln.UiInventory.Click(0, SlotButton.Left, SlotModifiers.Ctrl));
    Assert.Equal(new FakeSlotClickResult(0, false), kiln.UiInventory.Click(0));   // a plain click is the game's
}
```

- **Presses a listener owns, and the controllers** (`kiln.Input`, since 0.2.0-dev.31.13) <a id="owned-input"></a>.
  `KeyInput.Listen` with `KeyListenFlags.Own` ([ui.md](../ui.md#owning-a-press-until-its-release-keylistenflagsown-since-020-dev3113-mod-api-0222))
  and `PadInput` run on the kit's kiln.ui.input, whose ownership rules are the native ones (the cases in
  `native/services/ui/tests/own_parity.json` run on both). The test plays the player: `kiln.Input.Down(code, controller,
  mods, at)` is a new press (true when an owning listener took it), `Repeat` a key's auto-repeat, `Up` a release (true when
  it was kept from the game), `FocusLost` and `WorldChange` end ownership early, `Leak` says the game saw an owned press go
  down after all (its release then reaches the game). `Game` is what reached the game, `Others` what Kiln's other key
  handlers were given; `HiddenFromGame` says whether a button is held without the game knowing. `SetPad` and `Unplug` set
  what the game's last poll of a controller read, for `PadInput.Read`. Switch `ui.input.owned` or `ui.input.pad_state`
  off to test a mod on a Kiln without them. Not modelled: the window, raw input, XInput, Kiln's menu key and Escape
  ahead of the owners, the other handlers' answers.

```csharp
[Fact]
public void BlueprintOwnsTheRightButtonGesture()
{
    using var kiln = new KilnHarness();
    kiln.Step();
    var held = false;
    using var l = kiln.Run(() => KeyInput.Listen(p =>
    {
        if (p.VirtualKey != 0x02) return false;
        held = p.Down;                     // the press starts the gesture, its release (or its end) finishes it
        return true;
    }, KeyListenFlags.Own | KeyListenFlags.Pointer));
    Assert.True(kiln.Input.Down(0x02));    // owned
    Assert.True(held);
    Assert.True(kiln.Input.Up(0x02));      // kept from the game
    Assert.False(held);
    Assert.Equal(0, kiln.Input.Game.Count); // the game saw neither
}
```

- **The HUD's ammunition and spell lists** (`kiln.AmmoList`, since 0.2.0-dev.30.6) <a id="ammo-lists"></a>.
  Ui.AmmoList's own code runs on the kit's kiln.ui.ammo, so a mod's rows and its picks are tested offline. The test
  plays the game: `kiln.AmmoList.Hold(AmmoListKind.Arrow, bow, new AmmoListRow(arrow, 20))` builds the list for a held
  weapon with the game's own rows, `SetGameRows` changes them (the backpack changed), `Unhold` puts the weapon away,
  `NewSession` starts another session (snapshots and queued picks cleared), `KnownItems` names the items of a kind (a
  row of any other item is refused, as the game refuses it), `Player` sets the context's player. Each `Step()` is a UI
  frame: the providers of a list whose context changed are asked and their composed rows published. `Shown(kind)` is the
  list as the HUD would show it (the game's rows, then or instead the mods'), `Provided(kind)` the mods' rows,
  `ModeOf(kind)` Append, Replace or null (the game's list as is), `Publishes` counts the snapshots. `Select` and
  `Selected` model the selection, which stays on its item when the rows change. `Pick(kind, item)` is the player's pick:
  true when it is a mod row the game does not list with stock (a game row at count 0, a used-up stack, is none), which
  `Ui.AmmoList.Picked` gets at the next `Step()` (for the mod that provided it); false when the game equips it itself.
  Switch `ui.ammo.list` or `ui.ammo.pick` off with `kiln.Capabilities.Override` to test a mod's refusal path. Not
  modelled: the game's hook and drawing, its equip request, the server half, and a stack that leaves the backpack before
  the game builds its rows again (the game's rows are the backpack here; in the game a pick of such a row raises
  `Picked` since 0.2.0-dev.31.10, GitHub issue 145). A `Pick` after `SetGameRows` and before the next `Step()` is decided
  by the new rows, as in the game; `Shown` still composes the earlier snapshot with them until that step, where the game
  shows only its own rows.
  **Changed in 0.2.0-dev.31.12 (GitHub issue 145), in Append:** a mod's row of an item the game lists itself, also with
  stock, is now published behind the game's row, so `Provided(kind)` lists it too (`Shown` does not change: the game's
  row shows in its place). A test that asserts `Provided` in Append must add those rows, in the provider's order. And a
  `Pick` of such a row after `SetGameRows` set the game's count of it to 0, before the next `Step()`, now returns true
  and raises `Picked` for that mod (the stack left the backpack), where it returned false before.

```csharp
[Fact]
public void QuiverArrowsFollowTheBackpacksAndAPickAsksTheServer()
{
    using var kiln = new KilnHarness();
    kiln.Load<MyMod>();   // its OnLoad: Ui.AmmoList.Provide(AmmoListKind.Arrow, ...) and Ui.AmmoList.Picked += ...
    kiln.Step();
    kiln.AmmoList.Hold(AmmoListKind.Arrow, new ItemId(0xB0), new AmmoListRow(new ItemId(0x10), 20));
    kiln.Step();
    Assert.Equal(new AmmoListRow(new ItemId(0x30), 12), kiln.AmmoList.Shown(AmmoListKind.Arrow)[1]);
    Assert.True(kiln.AmmoList.Pick(AmmoListKind.Arrow, new ItemId(0x30)));   // not in the backpack: the mod's
    kiln.Step();                                                             // Picked runs here
}
```

- **Screen and window placement** (`kiln.UiPlacement`, since 0.2.0-dev.31.11, mod API 0.2.20) <a id="ui-placement"></a>.
  Kiln.Ui's own placement code runs unchanged (`UiScreenOptions.Position`, `MoveTo`, `Resize`, `Placement`,
  `RememberPlacement`; [the guide](../ui.md#placing-screens-and-windows-since-020-dev3111-mod-api-0220)); kiln.ui
  itself stays offline, so nothing is drawn. `kiln.UiPlacement.Frame(screen)` (or a `UiWindow`) plays one overlay frame
  of it on a viewport the test sets (`ViewportWidth`, `ViewportHeight`, `Scale`; 1920 x 1080 at 1 by default) and
  returns where it is, or null while it is closed: ImGui's Always, FirstUseEver and Appearing and its 32-pixel minimum
  are modelled, a window sized by its contents takes `ContentSize`. `PlayerPlaces(screen, x, y, width, height)` is the
  player's drag in the next frame; `Remembered(screen)` reads what Kiln keeps in ui-placement.json in the harness's
  config folder (`FilePath`, written at once offline); `Restart()` is a game start (the file is read again; write your
  own file before it to script a moved or corrupt one). Not modelled: imgui.ini, size constraints a native plugin sets,
  the title bar and scrolling.

```csharp
[Fact]
public void GrimoireOpensBesideBetterSpells()
{
    using var kiln = new KilnHarness();
    var spells = kiln.Load<BetterSpells>();   // its OnLoad: Ui.Screen("Better Spells", ...)
    var grimoire = kiln.Load<Grimoire>();     // its OnLoad: Ui.Screen("Grimoire", ...)
    kiln.Step();
    var bs = ((BetterSpells)spells.Instance).Screen;
    var g = ((Grimoire)grimoire.Instance).Screen;
    bs.Open();
    var b = kiln.UiPlacement.Frame(bs)!.Value;
    g.MoveTo(b.Right, b.Y, pivotX: 0);        // what Grimoire does when it opens beside it
    g.Open();
    Assert.Equal(b.Right, kiln.UiPlacement.Frame(g)!.Value.Left, 3);
}
```

  **Docked windows** (since 0.2.0-dev.31.16, mod API 0.2.23; [the guide](../ui.md#docking-windows-since-020-dev3116-mod-api-0223)):
  `kiln.UiPlacement.FrameWindows()` plays one overlay frame of every open window of the harness's mods with Kiln.Ui's own
  dock code (the group's leader first, the others beside it in the same frame, the group kept inside the viewport, a
  docked window waiting for its closed target, Shift, a drop beside the old side) and returns where each one is, keyed by
  window (one that waits is not in it). `PlayerDrags(window, x, y, shift: false)` is the player holding the window and
  dragging its centre to (x, y) in the next frame; the frame after without another drag is the drop. A `PlayerPlaces`
  that changes a window's size is a resize. The kit's clock moves 16 ms a frame (a docked window waits for its target 250
  ms at most: 16 frames). Not modelled: which window's edge has the pointer where two touch, imgui.ini.

```csharp
[Fact]
public void GrimoireMovesWithBetterSpells()
{
    using var kiln = new KilnHarness();
    var spells = kiln.Load<BetterSpells>();   // Ui.Window("Better Spells", ..., new() { PlacementId = "spells", AcceptDock = true })
    var grimoire = kiln.Load<Grimoire>();     // docks its window: DockTo(Ui.FindWindow("betterspells", "spells")!, DockSide.Right)
    kiln.Step();
    var bs = ((BetterSpells)spells.Instance).Window;
    var g = ((Grimoire)grimoire.Instance).Window;
    kiln.UiPlacement.PlayerDrags(bs, 0.5f, 0.5f);
    var f = kiln.UiPlacement.FrameWindows();
    Assert.Equal(f[bs].Right, f[g].Left, 3);  // in the same frame
    kiln.UiPlacement.PlayerDrags(g, 0.8f, 0.2f, shift: true);
    kiln.UiPlacement.FrameWindows();
    Assert.Null(g.DockedTo);                  // Shift: moved alone, undocked
}
```

- **Outlines in the world** (`kiln.Outlines`, since 0.2.0-dev.30.3) <a id="outlines"></a>. `Ui.World.Outline`'s own code
  runs on the kit's kiln.render, so what a mod outlines is tested offline. `kiln.Outlines.All` lists every request the
  harness got (`FakeOutline`: entity, colour, pulse, priority, duration, state, reason, group, `ColorShared`, the mod
  that asked), `Live` the ones not ended, `Of(outline)` the one behind a `WorldOutline`, `Showing(entity)` the one an
  entity shows. A new request waits (`Pending`) until the next `Step()`, where kiln.render's rules run: on one entity
  the highest priority, then the newest, shows and the others wait (`Outshone`); two colours show at once and a third
  shares the nearest; a duration ends on the harness's virtual time. The test plays the game's side:
  `GameHighlight(entity)` (the game's own highlight shows instead, unless `Setting = FakeOutlineSetting.ModsFirst`),
  `Setting = FakeOutlineSetting.Off` (every request waits), `HideModel(entity)` (`NoModel`), `Despawn(entity)` and
  `EndWorld()` (end requests at once), or pins one request's state with `SetState(outline, state, reason)`. Dispose,
  `Ui.World.ClearOutlines` and the mod's unload end requests (`Removed`). `kiln.Capabilities.Override("ui.world_outline",
  false)` makes `Outline` throw `KilnUnavailableException`. Not modelled: the game's highlight itself (nothing is
  drawn), `RenderFrameEvent` and its drawing (the draw functions do nothing; `Render.Available` stays false), the
  camera (`render.camera` is offline). Without the kit (a plain unit test, no Kiln core) `Ui.World.OutlinesAvailable`
  is false and `Outline` throws `KilnUnavailableException`.

```csharp
[Fact]
public void FilledChestsPulseGreen()
{
    using var kiln = new KilnHarness();
    kiln.Load<MyMod>();   // outlines chests in green, pulsing, for 8 s, when its server half says they were filled
    kiln.Step();
    kiln.Run(() => MyMod.OnFilled([new EntityId(4100)]));
    kiln.Step();          // the next client step: kiln.render applies it
    var o = kiln.Outlines.Showing(new EntityId(4100));
    Assert.Equal((Color.Green, true, OutlineState.Shown), (o!.Color, o.Pulse, o.State));
    Assert.Equal(TimeSpan.FromSeconds(8), o.Duration);
}
```

- **Live game values** (`kiln.UiSnapshot`, since 0.2.0-dev.31, mod API 0.2.17) <a id="live-game-values"></a>. Kiln.Blocks'
  `BlockUi.ReadWorld` and `DemandWorld` (and a window's `world.*` bindings) run on the kit's kiln.ui.snapshot, so a mod
  that reads the local player's health, position or inventory is tested offline. The test is the game's collector:
  `kiln.UiSnapshot.Publish(new FakeWorldSample { Vitals = new FakeVitals(413, 500), Position = new FakeWorldPosition(1.5,
  -2, 3.25), Inventory = [new FakeWorldSlot(Item: 0xAA, Count: 1)], HostHours = 13.5 })` makes the newest publication at
  once. Only what something demanded is in it (`Demanded`, `Demands` show what the mod asked for); a group the sample
  leaves out, or that nobody demanded, is `NoSnapshot`, and `Unavailable` gives a group another reason
  (`StaleSource`, `LayoutOff` ...). More than 512 slots is `Budget`; the hour on a `HarnessSide.JoinedClient` harness is
  `DeferredSource`. `EndWorld()` is a world change: the publication goes, every token is revoked, `ReadWorld` answers
  `NoWorld` until the next `Publish`. `Tokens` counts reads not released. `kiln.Capabilities.Override("ui.blocks.world",
  false)` makes `ReadWorld` and `DemandWorld` throw `KilnUnavailableException`. Not modelled: the game's collector (05a's
  stage, the client world's step, GetEntityTransform, kiln.time), the 100 ms cadence, the 1000 ms age and the 2 s token
  life; the fake answers on every harness side. Window state, a mod's own data (`BlockUi.Bindings`) and Server-target
  commands need kiln.ui and kiln.net, which the kit does not fake: test their logic without the kit.

```csharp
[Fact]
public void TheHealthLineShowsTheGamesHealth()
{
    using var kiln = new KilnHarness();
    kiln.Load<MyMod>();   // its OnLoad: BlockUi.DemandWorld(UiWorldFields.LocalVitals)
    kiln.UiSnapshot.Publish(new FakeWorldSample { Vitals = new FakeVitals(413, 500) });
    Assert.Equal("413 / 500", kiln.Run(MyMod.HealthText));   // reads BlockUi.ReadWorld()
}
```

- **Crosshair** (`kiln.Crosshair`, since 0.2.0-dev.31.13, mod API 0.2.22). `Crosshair.Read` and `TryGet` read the
  sample the test aims: `kiln.Crosshair.Aim(position, normal, distance, entity, voxelMaterial)` is one game trace that
  hit (the next `Sequence`, the harness's current step as `ClientStep`, `FakeCrosshair.Session`, fresh), a field not
  given is null as when the game gives none; `AimAtNothing()` a miss (`GameStatus` 31); `Age(TimeSpan)` ages the
  newest sample (stale past `Crosshair.FreshFor`); `Fault()` reads faulted; `Clear()` no sample. `kiln.Crosshair.Reads`
  counts the reads. Reads work from any thread. Not modelled: the game's trace itself (its reach, what it can hit).
  ```csharp
  kiln.Crosshair.Aim(new WorldPosition(1, 2, 3), voxelMaterial: 37, distance: 4f);
  Assert.Equal("terrain 37 · 4.0 m", kiln.Run(AimReadout.Describe));
  ```

- **Quests and talent trees** need no fake: Kiln.Quests' quest engine and Kiln.Skills' talent trees are Kiln's own
  managed code, and they run on the fake players and saves. A quest a mod defines is started, advanced and completed
  for a fake player through `Quests.Start`, `Advance` and `Complete`, with `QuestStartedEvent`, `QuestStepEvent` and
  `QuestCompletedEvent`; a node of a mod's talent tree is unlocked with `Skills.Unlock` and read back with `LevelOf`
  and `Points`, with `TalentUnlockingEvent` (a handler may refuse) and `TalentChangedEvent`. Their native parts stay
  offline: the game's journal and knowledge (`kiln.knowledge`), quest markers (`kiln.questmark`), the game's own skill
  tree (`kiln.skills`, `GameTree`) and the game's levels and points (`kiln.progression`).

### Replication filters, touches, item remap and message hooks

Register fixture components with `kiln.Repl.AddComponent` and connect the peers your mod should see.
`kiln.Repl.ConnectFake` adds a vanilla loopback peer, and `kiln.Repl.ConnectLocal` adds a hosted world's own client.
`kiln.ReplNet.Connect` returns a `NetPeer` for message hooks and `Replication.Inject`. Pass `vanilla: false` to
stand in for a ready Kiln peer with current content.

- **Filters.** Register `Replication.Filter<T>` from the mod or with `kiln.Run`. Step to refresh selector membership,
  then call `kiln.Repl.Send(peerId, message)` for each peer. The next step delivers the resulting component bytes
  through `Replication.Watch`. Filters run in priority order on each peer's own copy of the scripted snapshot.
  `FilterContext.TryRead` reads components that the test put in `kiln.Entities`.
- **Touch.** Assert the result of `Replication.Touch` and inspect `kiln.ReplNet.Touches` for the requested entity
  IDs in call order. Script another `Send` to test what a fresh snapshot would contain.
- **Item remap.** Call `kiln.ReplNet.ConfigureItemRemap` with the fixture's item ID fields, game item IDs and
  look-alikes, then step to resolve the peers. Vanilla peers receive look-alikes or zero for non-game IDs.
  A mod can select additional peers with `Replication.RemapItems`. Remapping runs after filters, and
  `Replication.RemapStats()` reports the sites and counters from the fixture.
- **Messages.** Register `Replication.OnMessage` on the tick thread, then call
  `kiln.ReplNet.DriveMessage(peerId, channel, direction, bytes)`. It returns the resulting bytes or `null` for a
  dropped message. A joined-client harness uses peer ID zero for the server. `kiln.ReplNet.Injections` records copies
  of the bytes requested through `Replication.Inject`, including refused requests. Set `kiln.ReplNet.ValidateSend`
  to supply a scripted verdict for injects and send rewrites. Without a verdict, an inject returns a refusal and a
  send rewrite falls back to the original bytes. Use `DriveMessage` to exercise the hooks independently of an inject.

Scripted sends run synchronously on the harness tick thread and cover byte transformations. The game controls
worker-thread execution, relevance, baselines, transport and automatic re-sends, so those require a game test.
Filter registrations that request automatic source-change invalidation on the client channel are unavailable
offline. Sidecars need the game transport and relevance tracking, and `Encode`, `Decode` and schema inspection
need the game's codec. For a hosted harness, message hooks model the server half.

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
is a fixture-cleanup failure, not just a diagnostic, so a blocked `OnUnload` cannot leave the suite green. A fake of
the kit whose end throws does not cut `Dispose` short either: every fake ends, the harness lets go of Kiln's state and
the next harness can start, and then `Dispose` throws what was thrown (an `AggregateException` when several parts
failed).

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
| Kiln tasks | recording fake | `tasks.pool` | Context.Tasks runs as in the game on the kit's pool (two real worker threads): ToTick, OnTick and PostToTick hops run in the harness's steps before Tick.Pre, NextGateWindow resumes after a step, an unload cancels a mod's tasks; KilnHarness.Tasks.Settle runs them to their end; KilnHarnessOptions.Side gives a joined or a hosting client's worlds (client-world hops on the game's main thread). The pool's per-mod caps and per-step budgets are not modelled |
| Registry authoring | recording fake |  | KilnHarness.Registry seeds copied JSON and records one OnRegistry pass's IR with expiring contexts; no native table, live application, pass composition or mapping/restart proof is simulated |
| Config (global) | real |  | Config.Register of every kind (common, server, client): files in the temp kiln/config, edits on disk reach the change handlers (file watcher: pace steps in real time), bad files are refused and the values kept |
| Commands (console) | real |  | Kiln.Chat's command registry: [Command] methods, typed trees, help, permission levels; RunCommand as the console returns the reply lines; players type command lines in chat through the fake kiln.chat (FakePlayer.Say) |
| Inter-mod messages | real |  | Mods.Send between loaded mods, delivered at the next step before the receiver's Pre, dropped with an unloaded sender |
| Locale files | real |  | a mod's locales/<language>.json, the lookup order and fallbacks; KilnHarness.Language switches the game's language (a dedicated server says en-us) |
| Fault policy | real |  | Kiln.Host's fault policy (5 faults in 60 s of virtual time disable a mod, ModDisabledEvent, a crash report in the temp folder); by default the harness call during which a mod faulted throws |
| Game log lines | real |  | ModContext.GameLog handlers and the lifecycle's watch run as in the game, on lines the test writes (KilnHarness.GameLog): the game writes none |
| Game textures (GameTexture) | real |  | Kiln.Api's GameTexture decodes and encodes game pixel formats (BC1 to BC7, packed, 8 to 64-bit) as in the game: pure code on the test's bytes, with the same answers as kiln mode's image.decode_texture and image.encode_texture; the game's own textures are offline (read texture data from a blob the test adds) |
| Game features | unavailable |  | Game.HasFeature is false for every game feature (sigdb) unless the test overrides it; an override changes the answer only, nothing behind the feature is faked |
| Tests.RunAsync | unavailable |  | Kiln.Testing's Tests.RunAsync from mod code says "Kiln.Host is not running" offline |
| Screen and window placement (since 0.2.0-dev.31.11) | real |  | Kiln.Ui's own placement code: UiScreenOptions.Position, MoveTo and Resize on UiScreen and UiWindow (applied once, on the next frame; on a closed screen when it next opens), Placement, RememberPlacement in ui-placement.json in the harness's config folder (per mod and id, clamped into the viewport; a corrupt file is kept as .bad). KilnHarness.UiPlacement.Frame plays one overlay frame on a viewport the test sets, with ImGui's Always, FirstUseEver and Appearing and its 32-pixel minimum; PlayerPlaces is the player's drag, Restart a game start. Window docking (since 0.2.0-dev.31.16, mod API 0.2.23: Ui.FindWindow, AcceptDock, DockTo, Undock, DockChanged) with Kiln.Ui's own dock code: FrameWindows plays a frame of every open window (the group's leader first, the others beside it in the same frame, kept inside the viewport, a docked window waiting for its target, Shift, a drop beside the old side), PlayerDrags is the player holding and dragging one. Offline: kiln.ui itself (nothing is drawn), imgui.ini, size constraints a native plugin sets, which window's edge has the pointer where two touch |
| Players | recording fake | `kiln.players` | Kiln.Players' own code on the kit's kiln.players: KilnHarness.Players.Join/Leave take effect at the next step (Players.All, Find, Joined/Left, PlayerJoinedEvent/PlayerLeftEvent); entity slot + 1, Steam id, admin bit, user-group rights, the test's positions; 16 slots. Unavailable: raw records, the game-log replay, the position probe for non-players, native subscribers |
| Commands as a player, permissions | real |  | Kiln.Chat's Commands.Execute with a fake player as source (KilnHarness.RunCommand(player, line)) and Kiln.Permissions' real rules on permissions.json and enshrouded_server.json in the temp folder (WritePermissions, WriteServerConfig); @p and player names resolve against the fake players. Replies come back in the result and, through the fake kiln.chat, in the player's chat |
| Saved data (WorldData, PlayerData) | recording fake | `kiln.saves` | Kiln.Saves' store on the kit's kiln.saves: a world id and revision per server folder, the save cycle on the steps (WorldSaving, staged generation, WorldSaved), the game's autosave (30 s virtual), player-left and shutdown saves; a harness on the same folder reads the last save (KilnHarness.Kill: no shutdown save). Unavailable: backups, the world file's path, the snapshot stage, a client's hosted world |
| EntityData | recording fake | `kiln.saves` | on player entities: stored as PlayerData under the fake player's Steam id; on the fake world's entities (kiln.spawn's fake finds them) kept with the world's mod data |
| Config (per world) | real |  | Mod.WorldConfig<T>() binds to the harness world's folder (kiln/data/worlds/<id>/config) through Kiln.Saves and differs between world ids |
| Spawn | recording fake | `kiln.spawn`, `spawn.create`, `spawn.destroy`, `spawn.entities`, `spawn.templates`, `spawn.world`, `spawn.components` | Kiln.Spawn's own code on the kit's kiln.spawn: a spawn (template or item drop) makes an entity in the fake world at once, at its position and yaw with a SpawnTime, from the step the server started (spawn.world pending before); the templates are the ones the test adds (KilnHarness.Spawned.AddTemplate) plus the generic item pickup, an unknown one is refused; Destroy, Inspect, Exists, Find, FindByName, EntityCount, the tracked list, CleanupTracked and Refresh read the fake world; EntitySpawnedEvent and EntityDestroyedEvent are Kiln.Spawn's; KilnHarness.Spawned.Calls records every spawn, destroy and cleanup. Offline: extra spawn components (spawn.components), what a template does, the level, DoNotSave, saving spawned entities and re-finding tracked ones after a restart |
| Chat | recording fake | `kiln.chat`, `chat.send`, `chat.receive`, `chat.players` | Kiln.Chat's own code on the kit's kiln.chat: the fake players in game are the chat's players; a line a player types (FakePlayer.Say) reaches Chat.Received, ChatMessageEvent and chat commands at the start of the next step, and one nobody consumed is shown to every player; Tell, Broadcast, Say and command replies are shown at once from the step the server started, per player (FakePlayer.Chat, KilnHarness.Chat). Offline: the game's inbox (Chat.Diagnostics.LastLine), enableTextChat, the account hash and connection state of a chat player |
| Items | recording fake | `kiln.items`, `items.transaction`, `items.read`, `items.program`, `items.gear`, `items.gear.power`, `items.gear.armor`, `items.rename`, `items.give_equip`, `items.give_equip.instance_ammo`, `items.gear.read`, `items.gear.write`, `items.gear.durability`, `items.gear.upgrade`, `items.fuel`, `items.move`, `items.names`, `items.transfer`, `items.moves`, `items.instances`, `items.equip`, `items.equip_sets`, `items.pide_options`, `items.result_peek`, `items.recipe` | Kiln.Items' own code on the kit's kiln.items (the v1 table, v8's gear functions and v9's container renames): the items are the ones the test adds (KilnHarness.Items.AddItem, with their stack size), an inventory per fake player in game (FakePlayer.Inventory, 5 generic 8-slot inventories unless the test says) and per container entity (AddContainer); Give and Take run as one transaction at the start of the next step (stacks topped up first, gear one per slot with an item entity, Partial or NoRoom when it does not fit, take all or nothing, dry runs), ItemsGivenEvent and ItemsTakenEvent are Kiln.Items'; Read, FindInventories and the catalog read the fake; KilnHarness.Items.Calls records each. Gear: every piece has enhancement slots, a level, durability and a stamp (FakeSlot.Ref); GearOf, SetRarity, SetEnhancements and SetLevel run with kiln.items' rules at the next step, the shown rarity and level a step later, the gear events are Kiln.Items' (the game's own change: KilnHarness.Items.SetGear; perk slots of a type: SetPerkSlots). Rarity upgrades: Items.UpgradeRarity runs Kiln.Items' own code and the fake takes the cost from the payers' plain stacks and makes the write in the same step, all or nothing (CantPay, payers, refusals as kiln.items); KilnHarness.Items.SetUpgradeRolls names the rolls. Per-instance values (package P7): Items.SetPower, SetArmor and ValuesOf on the kit's scale table (no damage or armour is computed), SetDurabilityMax and SetLevel with AnyLevel with kiln.items' rules. Container renames (v9): Items.SetContainerName with kiln.items' rules (the game's Rename field's limits and the entity at the call, the name stored at the next step, "" clears it; read with KilnHarness.Items.ContainerName). GiveEquip models logical equipment, slot-type checks (SetEquipmentSlotType), Backpack or Take replacement, DryRun and EquipChangedEvent in one step, without modelling the game's page layout. Offline: water, moves, reading container names, transfer hooks, item instances, equip, recipe results, unlocked upgrades and gem slots at creation, the game's player inventory layout, max durability computed again after a gear change |
| Inventory owner kinds | recording fake | `kiln.items.inventory_kinds`, `items.inventory_kinds` | Kiln.Items' FindInventoryOwners (and Kiln.Blocks' Bases.Chests on top of it) on the kit's kiln.items.inventory_kinds: the owners FindInventories finds, with kiln.items' flags, guards and bounded buffer; players are Player, player storage holders PlayerStorage, a container what the test sets (FakeInventory.Kind; Unknown by default, as an ordinary chest). Offline: the game's components (FactoryStation, the bases' crafting stock) are not read |
| Combat | recording fake | `kiln.combat`, `combat.health`, `combat.hits`, `combat.deaths`, `combat.actor_states`, `combat.falls`, `combat.regen`, `combat.attr_heals`, `combat.attributes`, `combat.hold_filter` | Kiln.Combat's own code on the kit's kiln.combat (the v1 table): health is what the test gives an entity of the fake world or a fake player (KilnHarness.Combat.SetHealth); a mod's Damage or Kill and the game's hit (KilnHarness.Combat.Hit) apply at the start of the next step, the game's held one step for EntityHurtEvent / EntityHealEvent handlers, who may change or cancel it; EntityDamagedEvent, EntityDeathEvent (health below 1, the killing blow's attacker), PlayerRespawnEvent (KilnHarness.Combat.Respawn) at the next step; SetHealth (0 kills without a killer), Heal up to the maximum, god mode; KilnHarness.Combat.Hits records each hit. Offline: attributes, the hold filter, fall damage, regeneration, attribute heals, knockback, loot drops, the game's damage formulas and kill credit |
| Time | recording fake | `kiln.time`, `time.read`, `time.set`, `time.scale`, `time.weather`, `time.weather_choose`, `time.sync_probe` | Kiln.Time's own code on the kit's kiln.time: the day/night clock runs on the harness's virtual time from the first step (a new world at 06:00; day 06:00 to 18:00 in KilnHarness.Time.DayLength, 30 min by default, night in NightLength, 12 min), times the speed a mod holds (Time.SetSpeed); Time.State, OfDay, IsDay, Set (forward, or the game's reset), SetCycle, TimeSetEvent and the day and night events as Kiln.Time polls the clock; KilnHarness.Time.Calls records mods' changes. Offline: the weather (its table, layers, choice and reset), the sync probe, sleeping, saving the time with the world |
| Quests (Kiln.Quests' engine) | real |  | a quest a mod defines (Quests.Define, quests/*.json) is started, advanced and completed for a fake player through Quests.Start, Advance, Complete, SetStep, Fail and Abandon, with QuestStartedEvent, QuestStepEvent, QuestCompletedEvent and its record in the world's saved data: the engine is Kiln's managed code on the fake players and saves. Unavailable: the game's journal and knowledge (kiln.knowledge), quest markers (kiln.questmark), the game's NPCs and their conversations |
| Skills (Kiln.Skills' talent trees) | real |  | a talent tree a mod registers (Skills.Register, skills/*.json "trees"): Skills.Unlock, LevelOf, Points, State, TalentUnlockingEvent (a handler may refuse) and TalentChangedEvent, per player in the saved data: Kiln's managed code on the fake players and saves. Unavailable: the game's own skill tree (kiln.skills: GameTree), the game's levels and points (kiln.progression), the tree screen and what an effect does in the game |
| kiln.net | unavailable | `kiln.net` | no fake: a true pair is a client hosting its world (a kiln.client fake, a client-side harness, a client tick) and shares one channel registry, so no handshake refusal could be tested; Kiln.Network reports itself unavailable |
| Building input (Kiln.Client) | unavailable | `client.building_input` | no fake: BuildingInput emits the local player's ordinary building input through a game client's input path; the kit's harness is a server with no client world, so every submit throws KilnUnavailableException (offline). Kiln.Api.Tests drive the managed side on a fake kiln.client table |
| Steam peer messages (Kiln.Network) | unavailable | `net.steam_messages` | no fake: SteamMessages moves a mod's own Steam networking messages through kiln.net and the game's Steam; the kit has neither, so Open throws KilnUnavailableException (offline) and Status answers not answered. Kiln.Network.Tests drive the managed side on a fake kiln.net table |
| kiln.gemcraft | unavailable | `kiln.gemcraft` | no fake: Gems.Socket, Unsocket, Upgrade and Recycle run the game's own forge transaction in a server world, and GemCraftingEvent / GemCraftedEvent come from a hook on the game's gem handler; the kit has neither, so the calls end Unavailable (Kiln.Abilities.Tests drive the managed side on a fake kiln.gemcraft table) |
| Impact natives | recording fake | `kiln.abilities`, `abilities.natives`, `abilities.natives.sync`, `abilities.natives.attach`, `abilities.natives.ecs` | Kiln.Abilities' own natives code on the kit's kiln.abilities: RegisterSync, an [ImpactNative] method's generated registration, DeclareTyped, Attach and Detach, StateOf, Natives.All. As on a dedicated server, the world's native registry is there before the mods load (19 keys of it for mods' natives: the 20th is refused) and its programs come in at the second step; a native taken off answers 0x1F. KilnHarness.ImpactNatives.Call runs one ECALL as the native's thunk does, on a thread of its own (no tick-thread API, as in the game): a typed declaration's check (0x2C another type or size, 0x18 a required argument not connected), the handler's answer, 0x11 when it threw; for a game native the attachments whose program and site match, Before by priority, then one Instead, then the game's native (what the test scripts with SetGameNative; else it answers 1), then After. Game programs and their call sites are the ones the test declares (AddProgram): Attach refuses as the service does (program not loaded, site not found, a second Instead, not a native), and one made before the world is Pending, then Active or Off. NativeContext's TryRead and TryWrite reach the fake world's components of impact_simulation's other_read and other_write lists (this build's, or the test's), on from the first Kiln native call while the simulated serial condition holds (ImpactNatives.Serial); an unknown pin or lost serial condition closes it for the world. KilnHarness.ImpactNatives.Calls records each call. Offline: the Impact programs themselves (nothing runs a program's code: a test makes the calls), program registration, instances, attributes (no formulas: a written attribute is stored as written), sequences and item uses (Abilities.UseItem), buffs, the suspending natives of Register, the watchdog and the 0.2 ms budget |
| Assets | recording fake | `kiln.assets`, `assets.content`, `assets.blob_catalog` | Kiln.Assets' own code on the kit's kiln.assets: content blobs (AddBlob, OverrideBlob, ReadBlob, ReadResidentBlob, RemoveBlob) and the blob catalog (OpenBlobCatalog) over the game index a test scripts (KilnHarness.Assets.AddGameBlob, IndexReady), with kiln.assets' rules: one entry per hash, the later registration served, sorted snapshots that never change, 64 handles, NotReady while the index cannot be read. The hashes are the kit's own (size and SHA-256), not the game's. Resources (assets.resources), UI icons (assets.ui_icons), textures, dumps and manifests are offline: ReflectedResources.Available is false and Assets' resource calls throw. |
| Boot phase (Mod.OnBoot) | recording fake | `kiln.boot`, `mods.boot`, `mods.boot.game_data`, `mods.boot.phase` | KilnHarness.BootPhase.Run runs a boot mod's OnBoot as Kiln.Host does at a start: once, on an instance of its own, with BootDefinitions' staging (canonical JSON, DuplicateDefinition, NotInBootKinds, PhaseClosed, Blocks.Define refused) and Kiln.Blocks' lowering of what it staged (a definition that does not compile, BootReferenceOutside, ShadowedByFile), and records the outcome as the process's boot generation: a committed mod then loads in the harness (its definitions' runtime parts, BootDefinitionsCommittedEvent), a failed one is refused RestartRequiredBoot. boot.GameData answers from KilnHarness.BootPhase.SetRecord. Not modelled: the native kiln.boot (its table answers unavailable), the collectors' prepare and the generation's registrations (the kit's fakes do not get the boot generation's items, clones or nodes), several mods' drop and dependency rules, the exit code of a stopped start |
| Camera | recording fake | `kiln.camera`, `camera.service`, `camera.server_mode`, `camera.remote`, `camera.local_pose`, `camera.local_fov`, `camera.local_profile`, `camera.local_shake` | Kiln.Camera's own code on the kit's kiln.camera. The lease rules are the service's: one lease per player and endpoint, AlreadyOwned for the same mod, Busy unless another mod's priority is higher (the old lease ends superseded before the new one is applied), update and end with their refusals, answers and Camera.Changed one step after the call; calls outside mod code (owner 0) are refused, and a mod that unloads has its applied leases end OwnerRetired. There is no picture and no clock offline: a request counts as applied the step after it was made, and a lease ends only when the mod ends it or the test says so (KilnHarness.Camera.Expire, End, StopWorld; RefuseNext scripts a refusal); blends do nothing. A server's visual request for a player's client (camera.remote) reaches only a player the test made a Kiln client (KilnHarness.Camera.KilnClient, with the local parts it offers): another joined player is refused RequiresKilnClient, a player nobody joined PeerUnavailable, a missing part ClientCapability; there is no wire, ACK or timeout. Not modelled (the row reads offline): the frame snapshot (Camera.GetVisual). |
| Crosshair (since 0.2.0-dev.31.13) | recording fake | `kiln.client.crosshair`, `client.crosshair` | Crosshair.Read and TryGet read the sample the test aims (KilnHarness.Crosshair: Aim, AimAtNothing, Age, Fault, Clear): hit or miss, position, normal, distance, entity, voxel material, the next sequence and the harness's step; stale past Crosshair.FreshFor. Not modelled: the game's trace (its reach, the held item, what it hits), the client session's start and end. In a game the first read of the process answers NoSample while Kiln's hook goes in; the fake answers the aimed sample at once. |
| Component watches | recording fake | `kiln.component_changes`, `events.component_net` | Kiln.World's own consumer of ComponentChanges.Watch on the kit's kiln.component_changes, rule for rule the service's: 32 watches, 4096 present (entity, component) pairs, 128 roots a pair (a watch that does not fit is refused with the reason, one that grows past them is suspended whole), roots that must stay the same lives, the inventory links of PlayerInventoryAndItems (InventorySetup and Inventory bytes of the fake world), the origin rule (the kit's tracked writes are Kiln's with the mod's id; bytes a test sets are Unknown), the two-frame queue, a world that ends every watch. The compares run at the start of each step (the kit has no window calls): a change made in step N is posted in step N+1's Pre, and a new watch's baseline is read when it is made. HarnessComponentChanges.SetAttribute gives the typed values of AttributeValueChangedEvent. Offline: the game's own writes (a test plays them with FakeWorld.SetBytes), kiln.combat's and kiln.items' writers' tokens, program wraps' tokens, the timing of the native window calls |
| Content lookups (game texts per language, voxel materials) | recording fake | `content.game_texts`, `content.voxel_materials` | KilnHarness.GameTexts scripts the game's text tables per language and items' text tags; KilnHarness.VoxelMaterials the voxel material rows (what the game's terraforming registry, world table and items give). Kiln's own code answers from them: Content.TextLanguages, TextIn, TryGetTexts, LoadTextsAsync, Items.TextTags, Description, DescriptionIn, DisplayNameIn, Content.TryGetVoxelMaterials and LoadVoxelMaterialsAsync with their labels. A dedicated-server harness (the default) has no game texts, as a dedicated server: the same wrong-side reason; English from a Kiln catalog is not modelled. Not modelled: the game's loca bytes and resource reads (host tests cover the readers), Content.Text (kiln.content's main-thread lookup), new items' texts |
| Creature control | recording fake | `kiln.creature_control`, `creature_control` | Kiln.CreatureControl's own code on the kit's kiln.creature_control: bodies are the fake world's entities (counter-range ids, no players); the roles' priority (Ridden > Vehicle > PetCombat > PetFollow/PetStay/EngineGuard), one base lease per body (the same owner's next one replaces it, Released/Canceled "Replaced"; another owner's is Busy), one lease per other role, lower leases Preempted and resumed with a new transition sequence; an input lease is Active two steps after it takes the body (the broker's removal step and its check; at once when the body is held already), an AI-on lease four steps after it (three zeroed steps and the restore's check; at once when nothing was removed), and waits while the body is busy (KilnHarness.CreatureControl.SetBusy; 5 s fails it Quiescence); a release ends drive permission at once, the last one is Released four steps later (also for a Pending lease whose removal was queued; otherwise Canceled the next step); a Vehicle that was Active ends only with its body (body_retired); quarantine revokes at once, denies the root (root_policy) and ends the leases Retired once the body is gone; a destroyed body fails its leases BodyGone; terminal leases stay queryable 10 minutes; notices per owner; KilnHarness.CreatureControl.Calls records each call. Offline: the game's components (nothing is removed or zeroed), another owner's AI layer (AiInhibited), the entity counter's identity check, failure injection |
| Decisions | recording fake | `kiln.decisions`, `kiln.window_clock` | Kiln.Api's own dispatcher of decisions on the kit's kiln.decisions and kiln.window_clock: the service's broker rule for rule (request ids, frozen batches that a read does not consume, answers that are taken once, the deadlines: two more steps for a held action, the same step for a client's input, 4096 open requests, 8192 kept outcomes), one DecisionCompletedEvent per decision, and a world that ends (KilnHarness.Decisions.RestartWorld: what is open ends WorldEnded, the next world has the next epoch). KilnHarness.Decisions.Outcomes records every decision's end. An entity's identity comes from the harness's fake world (a life serial per world and entity; no player sessions). Decisions.ReevaluatePickup forgets the walk-over pickups a handler canceled (drop and receiver; once the harness says the build holds walk-over pickups). Offline: the game's side of every decision (no death, pickup or equipment change is held here: the events that need one are faked with their services), the sampled exposures of walk-over pickups (a receiver leaving and coming back, a changed amount) |
| Player storage | recording fake | `kiln.items.storage`, `items.storage`, `items.storage.persist` | Kiln.Items' player storage on the kit's kiln.items.storage: registrations with kiln.items.storage's rules (keys, limits, filters), holders as fake containers (Items.Read, Give, Take, FindInventories with InventoryKinds.PlayerStorage; a give the filter refuses is Filtered), restores with quarantine and parking, live registrations, captures into batches and the captures-done stage, parks, returns and death, scripted through KilnHarness.PlayerStorage (the game's side: Join, Apply, Restore, Capture, CapturesDone, Leave, Die). Offline: the game's record codec (records carry slot, item, count and level), item entities in storage, moves (the kit has no v3 table), captures at the kit's own saves, the client copy (items.storage.client). |
| NPC bodies (NpcBodies.Status) | recording fake | `blocks.npc_body` | the boot build's rows of NPC blocks with a body of their own, scripted with KilnHarness.NpcBodies (Ready, Failed with a reason, Removed): NpcBodies.Status by handle and by mod:block key, Failed for a body defined from code (built from blocks/*.json only), RemovalPendingRestart after the mod unloads, one NpcBodyBuildChangedEvent per change at the next step, and the handshake's kiln.npcbodies. Not modelled: the native boot build itself (cooking, admission, the closure hash: the test gives them), the rest of kiln.knowledge |
| NPC speech (since 0.2.0-dev.31.16) | recording fake | `kiln.npcspeech`, `npcspeech.bubble`, `npcspeech.voice` | Kiln.Npcs on the kit's kiln.npcspeech: Npcs.Say, Npcs.Speech(...).Suppress / Replace / Clear with the owners' priorities, and what a Kiln client's bubble of each NPC would be told (KilnHarness.NpcSpeech: ModeOf, LinesOf, Saying in a language). Not modelled: the game's bubble itself (which NPC is the player's target, the 5 m range, its random pick among the lines, the voice-overs Kiln mutes) and the network: the kit is the host, nothing is sent. |
| Queries | recording fake | `kiln.queries`, `queries.voxel_ray`, `queries.ground`, `queries.line_of_sight`, `queries.overlap_origins` | Kiln.Queries' own code on the kit's kiln.queries. A voxel ray answers what the test scripts (KilnHarness.Queries.Voxels; without a script every ray is unavailable): there are no voxels offline, so no test here says what the game's ray would hit. The rules around the ray are the service's: bad numbers throw, the budget (KilnHarness.Queries.MaxRaysPerStep, 64) refuses with BudgetExceeded and starts again each step, refusals keep their reasons. EntitiesInSphere and EntitiesInBox read the harness's fake world: entity origins, closed shapes, nearest first. RequestPath, FindPath, CancelPath and CanReach run on a fake queue with the service's rules (8 open per mod and 32 in all, counting results not yet delivered; one terminal per request; an anchor that goes ends OwnerGone): a request answers after KilnHarness.Queries.PathSteps steps what the test scripts (KilnHarness.Queries.Paths; without a script a straight walking route to the target). There is no path grid offline. |
| Outlines in the world | recording fake | `kiln.render`, `ui.world_outline` | Ui.World.Outline's own code on the kit's kiln.render v3: each request is recorded with its entity, colour, pulse, priority, duration and ThroughWalls flag (KilnHarness.Outlines.All, Live, Of, Showing); at the start of the next step the native rules decide its state: on one entity the highest priority, then the newest, shows and the others wait (Outshone); the game's own highlight shows instead (GameHighlight) unless the player's setting is mods-first; off keeps every request waiting; an entity without a model waits (HideModel); two colours at once and a third shares the nearest (ColorShared); a duration ends on the harness's virtual time. Despawn and EndWorld end requests, SetState pins one. Offline: the game's highlight itself, RenderFrameEvent's drawing (the draw functions do nothing; Render.Available is false without kiln.ui) and the camera (render.camera). |
| Network inspector (Replication) | recording fake | `kiln.repl`, `repl.observe`, `repl.client`, `repl.loopback` | Kiln.Replication's own code on the kit's kiln.repl: the test connects peers to machine slots (KilnHarness.Repl.Connect, ConnectFake, ConnectLocal) and scripts what the server sends them (Send, Destroy, Drop, Count) and what a client receives (Receive, ReceiveDestroy). Replication.Stats holds the counters per owner (the first hold of all starts them from 0, a mod's unload lets go) and reads one row per peer and channel; Replication.Watch and the receive side's watches get one event per message at the start of the next step with the full component values by name (the registry the test adds, AddComponent; names from kiln.types where the kit has it, else #index), Gap after Drop, DESTROYED then ENDED, and ENDED 'id reused' (ReuseId), 'peer left' (Leave) and 'world stopped' (StopWorld, the harness's end) as the last event; Replication.Component reads the registry from the first step. NetPeer stays null on events and rows (the kit has no kiln.net peers): PeerId, Local and Fake say who. The kit's mods run on the server side, so Replication.WatchReceived reports repl.client off (wrong side); the receive watches are faked on a client-side harness only. Offline: the game's send path, codecs and sizes, the capture file (repl capture), the loopback harness (repl.loopback, kiln.repl.lab) |
| kiln.repl.lab | unavailable | `kiln.repl.lab` | no fake: the loopback harness's payloads and hook cost exist only on a lab server running the game |
| kiln.repl.net | recording fake | `kiln.repl.net`, `repl.filter`, `repl.touch`, `repl.itemremap`, `repl.messages`, `repl.sidecar`, `repl.schema` | KilnHarness.Repl.Send runs per-peer component filters and scripted item remapping. Watches see the resulting bytes. KilnHarness.ReplNet records touch and inject requests and drives message hooks. Sidecars, the game codec and the simulation that schedules and transports replication are unavailable offline. |
| Villager routines | recording fake | `kiln.routines`, `routines.decisions` | Kiln.Blocks' Routines (Assign, Remove, Resume, Inspect) and both routine events on the kit's kiln.routines: one owner per villager (named by NpcId), changes queued and committed at the start of the next step, each completed once in that step, 256 queued changes and then QueueFull, revisions per world, OwnedBy/NotAssigned/Revoked/WorldStopped as the service answers them; the harness's end stops the world (teardown events). The villager's decisions are the test's (KilnHarness.Routines: SetClock, Decide, Finish, SetSleepAllowed); within one step assignment events come before task events. Offline: the game's villager worker, its chooser and sleep check (nothing chooses by itself), manual suspension (S5), restore after a restart |
| The client's skill-tree view (since 0.2.0-dev.31.13) | recording fake | `kiln.skills.client`, `skills.client_view` | Kiln.Skills' ClientSkills on the kit's kiln.skills.client: the local player's tree a test builds (KilnHarness.ClientSkills.AddNode, Link: type, class, cost, levels, screen position, shadow nodes), their levels (Unlock, Lock) and total points; spent and available as kiln-skills.dll reads them from the game's screen (the levels' costs, shadow nodes included; available at least 0), link counts per node. Loaded = false is the time before the first copy (Points null). The row skills.client_view can be switched off. Offline: the game's replication of the character, the screen's update timing, the demand window |
| Player crafting sources | recording fake | `kiln.stations`, `crafting.sources`, `items.lock_sources`, `crafting.build_sources`, `crafting.respec_sources` | Player/container registrations, copied allow/deny filters, order metadata, the Locks, Fills, Building and Respec opt-ins, per-player counts (all, and lock-, fill-, building- or respec-opted only), inventory permission, disposal and mod unload. No game RecipePaid hook, lock check or interaction payment, station fill, build cursor or placement payment, skill respec payment, base stock, station outputs or client screens are simulated. |
| The client's station view (since 0.2.0-dev.31.13) | recording fake | `kiln.stations.client`, `stations.client_view` | Kiln.Blocks' ClientStations on the kit's kiln.stations.client: the stations a test puts in the client's cache (KilnHarness.ClientStations.Add: workshop, base, position, state, selected recipe and choices, crafts loaded, water, the cycle's start, the station's inventory or none), the client's recipes (SetRecipe) and clock (Clock, SpeedFactor); times, progress and finished outputs computed as kiln-items.dll computes them (the game HUD's arithmetic, only while Running). The row stations.client_view can be switched off. Offline: the game's cache itself (which stations the server sends, its update timing), the demand window and the copy's age (every read answers the current script) |
| Terrain | recording fake | `kiln.terrain`, `terrain.overlays`, `terrain.baked`, `terrain.biomes` | Kiln.Terrain's own code on an in-memory world (KilnHarness.Terrain): flat ground, the cells a test sets, base boxes, one ledger per mod and world id, and Kiln's witness of the kept plans with the world file's revision (SaveWorld, LoseLedger: a lost ledger is refused, a first install over matching ground is not). Inspect, Apply, Forget and Applied run as in the game: batches, the ledger's rules, conflicts, Terrain.Changed. Not modelled: streaming (tiles are loaded unless the test says not), the game's save itself (the ledger lives in the harness, not in the fake kiln.saves), kiln.build's own events (its transient journal is modelled: Restart puts transient cells back), and time: from code that is not a mod's, every batch runs in the same step. terrain.baked and terrain.biomes are off, as in the game. |
| The HUD's ammunition and spell lists | recording fake | `kiln.ui.ammo`, `ui.ammo.list`, `ui.ammo.pick` | Ui.AmmoList's own code on the kit's kiln.ui.ammo: a test holds a bow or a staff with the game's own rows (KilnHarness.AmmoList.Hold, SetGameRows, Unhold, NewSession, KnownItems); at each step Kiln.Ui asks the providers when a list's context changed and publishes one snapshot, which the native rules check (one owner, the context's serial and session, 64 rows, no duplicate or item 0, the list's item catalog). Shown and ModeOf read the list as the HUD would compose it; the selection stays on its item (Select, Selected); Pick of a mod row the game does not list with stock (a row it kept at count 0 is none) raises Ui.AmmoList.Picked at the next step for the mod that provided it. Offline: the game's hook, its drawing and its equip request, and a stack that leaves the backpack before the game builds its rows again. |
| Native chest buttons (since 0.2.17) | recording fake | `kiln.ui.chest`, `ui.chest.buttons` | AddChestButton registrations share the ui.values owner budget; four active captions, unique and length-checked. UiChest.Buttons/Press model a storage chest's native actions and invoke the real managed callback with the current container; disposal and five faults remove it. Not modelled: native row drawing, focus/navigation, native text budgets, game thread/session timing or hooks. |
| Owned key, mouse and controller presses; the controllers' view | recording fake | `kiln.ui.input`, `ui.input.owned`, `ui.input.pad_state` | KeyInput.Listen with KeyListenFlags.Own (and Pointer, Mouse, Pad) and PadInput on the kit's kiln.ui.input: a test plays presses (KilnHarness.Input.Down, Repeat, Up), a focus loss, a world change and a press the game saw after all (Leak), and reads what reached the game (Game) and Kiln's other key handlers (Others); the ownership rules are the native ones (native/services/ui/tests/own_parity.json runs on both). SetPad and Unplug set what the game's last poll of a controller read. Offline: the game window, raw input and XInput themselves, Kiln's menu key and Escape ahead of the owners, the key subscribers' and bindings' own answers (Others take nothing), the cursor. |
| The game's inventory slots | recording fake | `kiln.ui.inventory`, `ui.slots.click`, `ui.slots.inspect`, `ui.inventory.panels` | Ui.Inventory's own code on the kit's kiln.ui.inventory: OnClick handlers subscribe with their button, exact modifiers and Take; a test draws a screen's slots (KilnHarness.UiInventory.Draw), hovers one (Hovered) and clicks it (Click): only on a screen with a pointer, the matching handlers get the click with the slot's item and entity, Taken when one asked for it. Marks and MarkWhen rules are Kiln.Ui's (MarkOf: what a drawn slot would show); Ui.Values.Slots reads the drawn frame. The screen a Draw draws on is the open one: Ui.Inventory.Screen and ScreenChanged follow it (OpenBackpack, OpenChest with the chest's entity, CloseScreen), and PanelsShown names the AddPanel panels that would show there (their screens and When). Offline: the game's drawing and hooks, the pixel mapping (no panel is drawn or placed), the 250 ms frame age, the game's popups (none is open: MarkOf answers as with no popup, GameScreen.PopupOpen is false). |
| Captured item detail (Since 0.2.0-dev.30.5) | recording fake | `kiln.ui.item_detail`, `ui.item_detail` | Owned text/stat-float recordings, exact client identity or item+level+rarity lookup, strict UTF-8 byte budgets, worker reads and 64-entry capture-order eviction. UiItemDetail.Capture/Clear supply captures and model a world change. Not modelled: game text construction, native capture failures, session lifecycle, hooks or drawing. |
| Expanded slot subscriptions | recording fake | `kiln.ui.slots` | Negotiated 120-byte slot frames from UiInventory.Draw, including rarity and level. Legacy kiln.ui.values subscribers retain 112-byte records. Not modelled: game drawing, hooks or pixel mapping. |
| Live game values (BlockUi.ReadWorld) | recording fake | `kiln.ui.snapshot`, `ui.blocks.world`, `world.vitals`, `world.position`, `world.inventory`, `world.host_time` | Kiln.Blocks' own BlockUi.ReadWorld, DemandWorld and the world.* bindings on the kit's kiln.ui.snapshot: demands join into the groups a publication carries (64 per mod, 1024 in all), a test publishes the local player's vitals, position, own inventory and the hosted world's hour (KilnHarness.UiSnapshot.Publish): what nobody demanded is NoSnapshot, more than 512 slots is Budget, the hour on a JoinedClient harness is DeferredSource. A read pins one whole publication (2 tokens per mod, 128 in all; an undersized copy writes nothing and the retry gets the same one); EndWorld clears it and revokes every token (NoWorld until the next Publish). Offline: the game's collector (05a's stage, the client world's Post, GetEntityTransform, kiln.time), the 100 ms cadence, the 1000 ms age and the 2 s token life; the fake answers on every side, a dedicated server's harness included. Window state, mods' own data and Server-target commands need kiln.ui and kiln.net, which the kit does not fake. |
| Native slot tints (since 0.2.17) | recording fake | `kiln.ui.tints`, `ui.slots.tint` | TintWhen runs the managed rules in registration order over a copied slot view; null keeps the prior colour, item filters and disposal apply, five consecutive faults stop a rule. Not modelled: game hooks, native descriptor copying, colour strength, rarity art or drawing. |
| Values in the game UI | recording fake | `kiln.ui.values`, `ui.values.detail`, `ui.values.slot`, `ui.values.held`, `ui.values.hud`, `ui.values.tooltip_lines` | Ui.Values' own code on the kit's kiln.ui.values: providers register (ItemBar, SlotBar, HeldBar, Hud, TooltipLines) and a test draws a value or a detail view (KilnHarness.UiValues.Draw, DetailLines) over a fake item: the providers run in order over the accumulated value, edits are checked by the kind's masks, refusals and faults are recorded (Decisions). Offline: the game's drawing and hooks, change events and the Item details window. Slot frames (ui.slots.inspect): the frames a test draws with KilnHarness.UiInventory.Draw. |
| Systems | recording fake | `systems.wraps`, `systems.wraps.typed` | Kiln.Systems' own code on the kit's kiln.world v4. The programs are the ones the test declares (KilnHarness.Programs.Add, or Add<T> with a Kiln.Game view: its lists and contract); each step runs each one once per archetype of the fake world's entities that have its read, write and with components (of the types the world knows; template data is not matched), on the tick thread: Befores high priority first, then the Replace or the declared update, then the Afters in reverse. Per-entity stages iterate the slice and read and write the entities' components in the fake world; marks are recorded (KilnHarness.Programs.Marks), WhenArchetype decides by the entity's component set, the contract is FNV-1a-32 of the lists. The 1 ms stage budget excludes the original; ten consecutive overruns disable it. Managed faults use the real mod policy. Not modelled: the game's workers and its scheduling order between programs, singleton slots (empty), attribute formulas (Changed() validates and changes nothing), native faults, client and prediction worlds, boot reservations (systems.define offline), native SET. |
| WorldTables | recording fake | `kiln.worldtables`, `worldtables.budgets`, `worldtables.material_pools` | Scripted immutable boot pools, budgets, refusal and receipts; pending tool-policy pools validated against the decoded 56-item vanilla terrain pool and item-to-cell mapping; computed spherical brush descriptors and fill bytes; recorded recipes for pending tools (kiln.content stays unavailable). No extra game-visible tool pool rows or live tool actions. New calls cannot register into the closed boot registry. No game resource ingestion, selector, renderer, save remap, or actual network handshake is modelled. |
| World: entities and components | recording fake | `kiln.world` | Kiln.World's own code on the kit's kiln.world: KilnHarness.Entities (FakeWorld) makes entities and puts components on them, laid out by Kiln.Game's types (a name they do not know is refused); World.Exists, Has, TryRead, ReadBytes, Find, Query, Position, Transform and WriteRaw read and store them, and no read changes them. Where an entity stands is its CurrentTransform's bytes, as in the game; a fake player's follows the player's position when the world steps, and a player's components go with the player (the next one in the slot starts with none). Unavailable: teleport, world programs (EcsPrograms: there is no scheduler), and everything behind a component (the game's programs do not run: a Health of 0 kills nobody) |
| World: tracked writes | recording fake | `world.tracked_writes` | World.Write, Update, WriteField and WriteAt with kiln.world's rules, tested at the native service's boundaries (native/services/world/tests/parity.json): the write runs at the next step and answers in it (one step later for a type a fake game program writes, with Kept), 512 a step and the rest later, 4,096 not yet answered at most (refusals included), 1,024 answers a step; a type the mod's manifest does not list under "writes" is refused with the sentence that says what to add (the lists are read at the first step, so a hot-loaded mod's are not; a name Kiln.Game does not know takes none of the 91 places, and without Kiln.Game every name is refused; a mod whose list cannot be read loads and its writes say why); a write carries its entity's generation (SpawnTime) and one for an entity without it is not kept waiting behind a full step; a write whose entity is removed while it waits never runs, also when the id comes back (a player's slot); ComponentWrittenEvent for a change, FakeWorld.Writes records each; WriteInfo from any thread. Not modelled: the game's attribute formulas (no clamp), which types the game saves or replicates (null unless FakeWorld.Track says), a save of the world's entities, a second world (a hosted game's) |
| World: reads from tasks (WorldConcurrent) | recording fake | `world.ecs_read` | kiln.world's ECS reads from task threads with the native service's rules (shared with Kiln's own unit tests): Open, Validate, Read, ReadBytes, ReadMany, Query (bounded or not, complete or nothing) and their statuses, on the fake server world's entities, read from a task's worker between the harness's work items; a new entity under a used id (a player's slot) is stale, a component added or removed changes the layout. kiln.Capabilities.Override("world.ecs_read", false) is a build with no exact profile (everything Unavailable, OrOnTick takes the tick path); FakeWorld.BusyReads makes the next frame-quiet reads Busy. Not modelled: the client and prediction worlds, template names (Template is Absent), torn or unreadable copies |
| World: writes from tasks (WorldConcurrent) | recording fake | `world.ecs_write` | kiln.world's compare-exchange writes from task threads with the native service's rules (shared with Kiln's own unit tests): WritePolicy, TryCompareExchange and TryCompareExchangeBytes (compare, masked merge, no-op, conflict with the current bytes) on the fake server world's components, from a task's worker between the harness's work items; another mod's ref, a stale ref and an unloaded mod are refused stale; on the tick always Busy; ComponentExchangedEvent at the next step, naming the mod. The policy: plain bytes from Kiln.Game's types, the size from them or the test's bytes, and the tracking only as FakeWorld.Track gives it (until then not writable, as kiln.world answers when it cannot read the bits). FakeWorld.BusyWrites makes the next writes meet a busy gate (and latches the mod for the step). kiln.Capabilities.Override("world.ecs_write", false) is a build without them. Not modelled: the engine's gate and frames, faults, rollbacks and uncertain writes, the event queue's limit, which components the game really tracks (the in-game check lists them) |
| Game types (Kiln.Ecs, Reflected, GameTypes) | recording fake | `kiln.types` | the kit's kiln.types holds the types of the Kiln.Game beside the tests (the layouts the mod was compiled against: about 1,500 structs, enums and arrays of the game's components and events), so Ecs.TryGet, Ecs.Has, Ecs.All, Reflected.Component and GameTypes.Find work on the fake world. Without a Kiln.Game in the test output the service is unavailable. Unavailable either way: game systems and their hooks (GameSystems), the profiler, the game's event streams (GameEvents), default values, alignments, and the game's other 9,500 types |
| [KilnTest] tests | real |  | Kiln.Testing's own runner, started test by test through Kiln.Host (KilnHarness.RunTest, KilnTestSession, the xunit KilnTestSuite): same order, timeouts and results as in a server. Offline a passing body that asked for an unfaked service or a feature that is off is skipped with the names; failed bodies and timeouts remain failures with the names. A test that blocks the tick thread is stopped by the watchdog and reported as a timeout with the stack where it blocked |
| kiln.assets.vfx | unavailable | `kiln.assets.vfx` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.audio | unavailable | `kiln.audio` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.base_rules | unavailable | `kiln.base_rules` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.build | unavailable | `kiln.build` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.caps | unavailable | `kiln.caps` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.characters | unavailable | `kiln.characters` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.client | unavailable | `kiln.client` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.combat.speed | unavailable | `kiln.combat.speed` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.content | unavailable | `kiln.content` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.craftmenu | unavailable | `kiln.craftmenu` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.creatures | unavailable | `kiln.creatures` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.eml | unavailable | `kiln.eml` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.engine | unavailable | `kiln.engine` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.example | unavailable | `kiln.example` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.flame | unavailable | `kiln.flame` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.gamedata | unavailable | `kiln.gamedata` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.gameplay | unavailable | `kiln.gameplay` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.hooks | unavailable | `kiln.hooks` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.knowledge | unavailable | `kiln.knowledge` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.log | unavailable | `kiln.log` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.markers | unavailable | `kiln.markers` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.markers.bridge | unavailable | `kiln.markers.bridge` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.markers.lab | unavailable | `kiln.markers.lab` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.mod-values | unavailable | `kiln.mod-values` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.npcai | unavailable | `kiln.npcai` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.pacing | unavailable | `kiln.pacing` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.playeranim | unavailable | `kiln.playeranim` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.playerstate | unavailable | `kiln.playerstate` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.profile | unavailable | `kiln.profile` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.progress | unavailable | `kiln.progress` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.progression | unavailable | `kiln.progression` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.questmark | unavailable | `kiln.questmark` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.registry | unavailable | `kiln.registry` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.registry.live | unavailable | `kiln.registry.live` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.rules | unavailable | `kiln.rules` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.screen | unavailable | `kiln.screen` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.settings | unavailable | `kiln.settings` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.skills | unavailable | `kiln.skills` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.stationmenus | unavailable | `kiln.stationmenus` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.steamguard | unavailable | `kiln.steamguard` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.surfaces | unavailable | `kiln.surfaces` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.survivors | unavailable | `kiln.survivors` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.tasks | unavailable | `kiln.tasks` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.tools | unavailable | `kiln.tools` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.toolwear | unavailable | `kiln.toolwear` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui | unavailable | `kiln.ui` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui.blocks | unavailable | `kiln.ui.blocks` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.ui.tick | unavailable | `kiln.ui.tick` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.vfx | unavailable | `kiln.vfx` | no fake: the service finds no native table, so its own Available is false and its row says offline |
| kiln.world.budget | unavailable | `kiln.world.budget` | no fake: the service finds no native table, so its own Available is false and its row says offline |
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
