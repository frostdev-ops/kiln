# Test server, hot reload, in-server tests, logs

Wiki: [testing](https://frostdev.io/kiln/wiki/testing), [hot reload](https://frostdev.io/kiln/wiki/hot-reload),
[debugging](https://frostdev.io/kiln/wiki/debugging), [servers](https://frostdev.io/kiln/wiki/servers).

## A local test server
Use a server nobody plays on. Ways to get one:
- **Kiln Launcher**: Servers → New server (Linux: SteamCMD install, own Wine prefix), then install Kiln on it.
- **kilnctl** (Linux):
  ```sh
  kilnctl init mytest --dir <dir> --port 15637 --policy any
  kilnctl install mytest && kilnctl prefix mytest && kilnctl deploy mytest
  kilnctl start mytest       # returns when the world runs
  kilnctl logs mytest --kiln # kiln.log
  kilnctl stop mytest        # the game saves
  ```
  `kiln run --server mytest` starts it too and follows the log until it's up.

`policy any` lets Kiln run on a game build without a verified record. On `verified-only` (the default) an unknown
build runs vanilla and your mod never loads: kiln.log says so at boot.

No player is needed for most checks. Design player-free checks: entities you spawn, containers, NPCs in the world,
log lines. Write down what only a joined player can confirm (UI, client visuals, real hits) instead of claiming it.

## Launcher notes (dev.21+)
- Library Remove and Clean up are refused while Enshrouded runs ("Close Enshrouded first: the game may be reading this mod"),
  also with force. Clean up never lists a version the game's active profile uses. A running server blocks Remove only when its
  profile points into the Library.
- A profile's native page lists each chain.ini entry with "Load after Kiln and ShroudForge", last session's hook overlaps with
  "Load late", and the compat rules that apply.
- Play-once with a profile follows that profile's chain and compat rules for that start only.
- These pages and messages are covered by tests and mock data, not by a hand-run against a real install.

## Deploy and hot reload
```sh
kiln deploy                   # copy into <server>/kiln/mods/<id>
kiln deploy --reload          # and reload it in the running server
```
The control folder does the rest: write a command into `kiln/control/<name>.cmd`, read `<name>.result.json`.
| Command | Does |
|---|---|
| `mods` | every mod: id, version, state, error, side, load index, dependencies, faults, ms/step |
| `load <folder>` | load `kiln/mods/<folder>` |
| `unload <id>` | unload it and its dependents |
| `reload <id>` | dependents unload, the mod reloads from disk, everything loads again |
| `reload-test <id> <n>` | n reloads, one per step, then a leak check |
| `test <id> [filter]` | run its `[KilnTest]` methods |
| `gc` | full collection; how many unloaded mod contexts are still alive |

```sh
echo "reload my-mod" > <server>/kiln/control/r.cmd && sleep 2 && cat <server>/kiln/control/r.result.json
```
What survives a reload: `WorldData` / `PlayerData` / `EntityData` (serialized and read into the new types), config
files. What does not: anything in static fields, your own threads and timers (stop them in `OnUnload`), content and
creature layers (they come off and your new `OnReady` / `OnLoad` puts them back).

## In-server tests (Kiln.Testing)
`<KilnServices>Testing</KilnServices>`, or `dotnet new kiln-mod --tests`.
```csharp
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

public sealed class MyTests
{
    [KilnTest] public void Mod_is_loaded() => Assert.True(Mods.IsLoaded("my-mod"));

    [KilnTest(TimeoutSeconds = 10)]
    public async Task Happens_within_a_few_steps(TestContext t)
    {
        StartIt();
        await t.Until(() => Done, TimeSpan.FromSeconds(5), "it to finish");   // checked once per step
        await t.Ticks(3);
        Assert.True(Game.IsTickThread);
    }

    [KilnTest(Skip = "needs a player")] public void Later() { }
}
```
- Tests run one at a time on the tick thread, so they may touch game state and call services. Instance tests on the
  `Mod` subclass run on the live mod instance.
- `TestContext`: `Ticks(n)`, `Until(cond, timeout)`, `Delay(time)`, `Log`, `Mod`, `Cancellation`.
- `Assert`: `True`, `False`, `Equal`, `NotEqual`, `Null`, `NotNull`, `Same`, `Contains`, `InRange`, `Throws`,
  `ThrowsAsync`, `Fail`, `Skip`.
- Only awaits are timed: a test that blocks the tick thread cannot be stopped. Never block.
- Run: `kiln test [id] [filter] [--exact name] [--json]`, `"tests": {"autoRun": true}` (at server start and after each hot reload),
  `echo "test my-mod" > kiln/control/t.cmd`, or `await Kiln.Testing.Tests.RunAsync("my-mod", filter)`.
- Results: kiln.log lines `PASS` / `FAIL` / `SKIP` tagged `[mod:<id>]`, and `kiln/logs/tests/<id>.json`
  (`passed`, `failed`, `skipped`, `results[]` with `outcome`, `message`, `stack`).

## Offline unit tests (Kiln.TestKit, dev.26+)
Runs a mod under `dotnet test` with no game: Kiln.Host's real loader, a tick thread of its own, console commands, fake
players and saved data. Needs the dev.26 MDK (package `Kiln.TestKit`, same version as `Kiln.Sdk`). Page:
`docs/kiln/modding/unit-tests.md`.
```sh
dotnet new kiln-unit-test -n MyMod.Tests --mod ../MyMod/MyMod.csproj && dotnet test MyMod.Tests
```
```csharp
using var kiln = new KilnHarness();
var mod = kiln.Load<MyMod.MyModMod>();          // a second, isolated copy, as the game loads it
kiln.Step();                                    // first step: mods load, the server starts
Assert.True(mod.IsLoaded, mod.Error ?? kiln.DumpLogs());
var ada = kiln.Players.Join("Ada"); kiln.Step();
var r = kiln.RunCommand(ada, "mymod status");   // or RunCommand(line) as the console
var n = kiln.Run(() => Players.Count);          // Kiln API only through Run/As: it runs on the harness's tick thread
```
- `public sealed class ModKilnTests : KilnTestSuite<MyMod.MyModMod>;` (namespace `Kiln.TestKit.Xunit`) runs every
  `[KilnTest]` as an xunit test, in the in-game runner's order.
- Real offline: loading, lifecycle, events, scheduler, configs, commands, permissions, messages, locales, the fault policy;
  dev.27+ also the quests and talent trees your mod defines, on fake players. Faked: players, `WorldData`/`PlayerData`,
  `EntityData`. dev.27+ recording fakes the test reads back: spawning (`kiln.Spawned`, `AddTemplate` first), chat
  (`player.Say`, `player.Chat`), items (`kiln.Items.AddItem` first, `player.Inventory`), combat (`kiln.Combat.SetHealth`,
  `kiln.Combat.Hit`), the day clock (`kiln.Time`) and a world of entities and components with tracked writes
  (`kiln.Entities`). The rest (weather, equip, moves between inventories, item instances, content, abilities, network,
  rendering, hooks, the game's journal and skill tree) reports itself unavailable, and a test that asked for one is
  skipped with its name; `AllowUnavailable` lets a suite run anyway. `kiln.Capabilities.Override(name, available)`
  (dev.28+ also switches off rows built from it). The page's table "What is real offline" lists each row.
- Newer fakes and phases: dev.28+ `KilnHarness.BootPhase` (runs `OnBoot`, [boot.md](boot.md)), `kiln.Queries`,
  `KilnHarness.Terrain`; dev.29+ `KilnHarness.Programs` (program wraps, [world-programs.md](world-programs.md)),
  `KilnHarness.ComponentChanges`, `kiln.ImpactNatives` (`[ImpactNative]` methods, `RegisterSync`, attachments), world-table
  and buff budgets; dev.29.18+ `kiln.Tasks.Settle()` / `Live` and `KilnHarnessOptions.Side` (dedicated server, hosting
  or joined client; `EnterWorld`, `LeaveWorld`, `OnMainThread`), `FakeWorld.BusyReads`/`BusyWrites` (no experimental opt-in from dev.30.7 / mod API 0.2.16)
  ([tasks.md](tasks.md)); dev.29.19+ `kiln.LogFile` and `kiln.ReadLogFile()` read the harness's kiln.log while it is
  written (`File.ReadAllText` on it fails on Windows).
- `KilnHarness.Registry` (dev.31+, a recording fake: `Seed` values, `Author(mod, point, phase)` runs one pass and returns the edits
  it would send, dev.32+ `Reauthors` lists the mods that asked `Registries.Reauthor()`, in order (`RunReauthored(mod, offTick)` runs that pass off the tick, and `Refused` names a call over an edit cap); it proves your
  callback, never the game's mapping, composition or live application, [api-registry.md](api-registry.md)); `KilnHarness.NpcSpeech`
  (dev.31.16+; picks, farewells, menu lines, topics and a pick of one, [api-npcs.md](api-npcs.md)); dev.32+ `KilnHarness.Pets`
  and `KilnHarness.Taming` (Kiln's real roster and attempt engine on the kit's world: `Join` a player, `Place` and `Give`,
  read `Bodies`, `Of`, `HostilePairs`; one player per character across both, a clean start per harness;
  [api-pets.md](api-pets.md), [api-taming.md](api-taming.md)). **No fake for `ItemCosts` yet, and the kit's taming wears no
  tools:** test those rules as plain functions and the flow on a local server.
- Gaps: a service that caches "no table" (`Kiln.Ui` render, `kiln.net` at the first channel) is asked once per process, so
  a later test can pass where it should be skipped: read the service's `Available` in the test. No client/server pair.
  One harness per process. A blocked test is stopped by the watchdog (Linux tested, Windows not).
- Offline green is not a server or client check: run `kiln test` on a test server as well.

## Guided client checks (`[ClientCheck]`, dev.26+)
For what only a player can judge. Nothing of the runner has run in a real client yet. Page: `docs/kiln/modding/testing.md`.
```csharp
public override void OnLoad() => ClientChecks.Register(this);      // Kiln.Testing
[ClientCheck("blocks.frost-flash", "Frost flash on hit", Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Hosted)]
public async Task Frost_flash(CheckContext c)
{
    c.Instruct("Hit the dummy three times. Does a blue flash appear?");
    await c.Until(() => Hits >= 3, TimeSpan.FromMinutes(2), "three hits");
    c.Expect(Hits == 3, "three hits counted");
    await c.Verdict();                                             // the player's pass, fail or skip
}
```
- Tick thread, like `[KilnTest]`. `CheckContext`: `Instruct`, `Expect` (dev.29+: a failed `Expect` makes a Pass answer
  a Fail; pass `true` to only show a value), `Verdict`,
  `OnEnd`, `Ticks`, `Until`, `Delay`, `Player` (`EntityId`), `Session`, `Cancellation`.
- The player opens the list with Ctrl+Shift+F5 (Ctrl+Shift+F6/F7/F8: pass, fail, skip); verdicts go to `kiln/state/client-checks.json`.
- A `cheats` check (level, xp, skill points, knowledge) starts only while the player's achievement guard protects and a
  `guard-proof` check passed on that build; it asks for a throwaway character every time.
- Write checks for what you could not see; don't report them as passed until a player ran them.
- Check sets to copy (dev.26+, none run by a player yet): item data (`mods/tests/instances-test/ClientChecks.cs`, area
  "Item data"), player states (`mods/samples/PlayerStates`, area "Player animations"), weapon moves and held models
  (`mods/tests/weapon-moves-test`, `blocks.moves-*`, `blocks.held-animated*`).
- Offline (`Kiln.TestKit`) item data, player states and weapon moves are unavailable (the items fake has no item
  instances; `kiln.playeranim` and `kiln.gamedata` have no fakes). Test them on a test server.

## Slow steps
`kiln/logs/tick-stats.jsonl` (dev.29+) has each step's time per Kiln service and plugin, the slowest Pre subscribers and
each minute's worst step; kiln.log's per-mod stats line splits `max Z ms (pre A / post B)`, and dev.29.16+ the load
line splits `loaded in T ms (layout check A ms, assemblies B ms, OnLoad C ms)`. Compare two runs with
`kilnctl tick-compare BEFORE AFTER` (dev.29.17+). dev.29.19+: a single-player world's pauses (the game's menu) go into
`paused_ms`, not `interval_ms`.

**Impact memory (dev.29.19+, read-only watch; not yet seen in a real game).** The game keeps every hit in one 16 MiB
pool per world; full, new hits fail, the world crawls and the game can crash. kiln.log warns at the first 50, 75 and
90 % of a session (`kiln.abilities: the server world's impact memory is 75% full ...`) and names the top 5 programs by live instances ("a
mod's program" for one not in the game's list); at 75 % it lists the oldest live instances of the top one: hits
hundreds of seconds old are the leak. Each world's minute line in `tick-stats.jsonl` ends with
`"impact":{"used_pct","peak_pct","capacity_mib","live","live_peak"}`. `kiln/control/impacts` with `dump` logs it once,
`off` turns the watch off. A mod's impact program in the top list after a long session is the first suspect. Page
`docs/kiln/services/abilities.md` "Impact memory watch".

## Reading the logs
`kiln logs [-f] [--mod ID] [--level E] [-n N]`, or:

| File | What |
|---|---|
| `kiln/logs/kiln.log` | Kiln and every mod (`[mod:<id>]`), load order, failures with reasons, data-pack problems |
| `kiln/logs/mods.txt` | the current mod list with states |
| `kiln/logs/tests/<id>.json` | test results |
| `kiln/crash-reports/crash-*.txt` | a disabled mod, an unhandled exception, a native fault: description, system, mod list, stack, last log lines |
| the game's own log | `logs/enshrouded_server.log` in the server folder; `Context.GameLog.Line` mirrors it |

Players send a support report zip; dev.29.18+ its `report.json` has a `versions` object (Kiln, launcher, system, runner,
game build, how many mods are on); dev.29.19+ it also holds Building Tools', ShroudLink's and Auto Loot Plus' logs.
Success looks like `mod my-mod 0.1.0 loaded in … ms`. A fault looks like
`[E] [host] mod x threw in Pre (4/5): …`; after the limit, `disabled after 5 faults within 60 s; reload x to try
again; crash report …`. Fault settings live in `kiln/kiln.json` `mods` (`fault_limit`, `fault_window_s`,
`test_timeout_s`).

## Linting
`kiln lint`, the analyzers and game-id checks: [devkit.md](devkit.md). Impact programs: `impact.py lint my.bin`.
