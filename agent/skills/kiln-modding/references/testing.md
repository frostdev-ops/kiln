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

## Reading the logs
`kiln logs [-f] [--mod ID] [--level E] [-n N]`, or:

| File | What |
|---|---|
| `kiln/logs/kiln.log` | Kiln and every mod (`[mod:<id>]`), load order, failures with reasons, data-pack problems |
| `kiln/logs/mods.txt` | the current mod list with states |
| `kiln/logs/tests/<id>.json` | test results |
| `kiln/crash-reports/crash-*.txt` | a disabled mod, an unhandled exception, a native fault: description, system, mod list, stack, last log lines |
| the game's own log | `logs/enshrouded_server.log` in the server folder; `Context.GameLog.Line` mirrors it |

Success looks like `mod my-mod 0.1.0 loaded in … ms`. A fault looks like
`[E] [host] mod x threw in Pre (4/5): …`; after the limit, `disabled after 5 faults within 60 s; reload x to try
again; crash report …`. Fault settings live in `kiln/kiln.json` `mods` (`fault_limit`, `fault_window_s`,
`test_timeout_s`).

## Linting
`kiln lint`, the analyzers and game-id checks: [devkit.md](devkit.md). Impact programs: `impact.py lint my.bin`.
