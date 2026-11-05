# Kiln.Testing: tests that run inside the server (Forge's GameTest)

```csharp
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

public sealed class MyTests
{
    [KilnTest]
    public void Mod_is_loaded() => Assert.True(Mods.IsLoaded("my-mod"));

    [KilnTest(TimeoutSeconds = 10)]
    public async Task Something_happens_within_a_few_steps(TestContext t)
    {
        StartIt();
        await t.Until(() => Done, TimeSpan.FromSeconds(5), "it to finish");   // checked once per step
        await t.Ticks(3);                                                      // three server steps later
        Assert.True(Game.IsTickThread);
    }

    [KilnTest(Skip = "needs a player")]
    public void Later() { }
}
```
- Reference it with `<KilnServices>Testing</KilnServices>` (Kiln.Sdk); `Kiln.Testing.dll` ships in `kiln/host`.
- A test is a method with `[KilnTest]` (`Name`, `TimeoutSeconds`, `Skip`, `Order`): static or instance, returning
  `void`, `Task` or `ValueTask`, taking nothing or a `TestContext`. Instance tests on the `Mod` subclass run on the
  live mod instance; other classes need a parameterless constructor (`IDisposable`/`IAsyncDisposable` are honoured).
- Tests run one at a time, on the tick thread, in `Order`, then class name, then declaration order. Every `await`
  comes back to the tick thread (the mod's synchronization context), so tests may touch game state and call services.
- `TestContext`: `Ticks(n)`, `Until(cond, timeout)`, `Delay(time)`, `Log`, `Mod`, `Cancellation` (signalled on timeout).
- `Assert`: `True`, `False`, `Equal`, `NotEqual`, `Null`, `NotNull`, `Same`, `Contains`, `InRange`, `Throws`,
  `ThrowsAsync`, `Fail`, `Skip`.
- Timeout: per test (`TimeoutSeconds`, else the manifest's `tests.timeoutSeconds`, else `kiln.json`
  `mods.test_timeout_s`, default 30 s). Only awaits are timed: a test that blocks the tick thread cannot be stopped.

## Running
- Automatically: manifest `"tests": {"autoRun": true}` (Kiln.Sdk `<KilnTestsAutoRun>true</KilnTestsAutoRun>`) runs
  them once the server has started, and after each hot reload while it runs (not during `reload-test`).
- On demand: `echo "test my-mod [filter]" > kiln/control/t.cmd` → `kiln/control/t.result.json`.
- From code: `await Kiln.Testing.Tests.RunAsync("my-mod", filter)` (a chat command, a dashboard op) → `TestReport`.
- Unloading the mod ends its run (its timers are canceled, nothing keeps it alive).

## Results
`kiln.log`, tagged with the mod:
```
[I] [mod:sample-tests] tests: running 10 test(s) of sample-tests 0.1.0
[I] [mod:sample-tests] PASS SampleTestsMod.Imc_round_trip (97.5 ms)
[I] [mod:sample-tests] SKIP SampleTestsMod.Skipped_example: shows how a skipped test is reported
[E] [mod:x] FAIL MyTests.Something (timeout): did not finish within 10 s
[I] [mod:sample-tests] tests: sample-tests: 9 passed, 0 failed, 1 skipped in 0.61 s; results …\kiln\logs\tests\sample-tests.json
```
and `kiln/logs/tests/<mod>.json`: `{mod, version, started, ms, ok, passed, failed, skipped, results: [{name, outcome
(pass|fail|skip|timeout), ms, message, stack}]}`.
