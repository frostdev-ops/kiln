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

## Guided checks (`[ClientCheck]`): what a player has to see

A `[KilnTest]` proves what code can read. Some things only a player can judge: a flash on hit, a sound, a menu that
opens, a dragon that turns when you steer it. A guided check is the shape for those: the check sets the scene, tells the
player what to do and what to look for, asserts what code can read, and waits for the player's verdict. Every verdict
goes into a file, so a pass through the checks leaves a record.

> **Status.** The runner, the ledger and the refusal rule are covered by unit tests and run on a dedicated lab server
> through the control folder and the joined channel (`mods/tests/client-check-test`). **Nothing of it has run in a real
> game client yet:** the screen, the HUD line, the keys, the hosted session and a joined session between two machines
> are not proven. Treat what this section says about them as the design until a first client pass confirms it.

```csharp
using Kiln.Testing;

public sealed class MyMod : Mod
{
    public override void OnLoad() => ClientChecks.Register(this);   // lists this mod's checks with the runner

    [ClientCheck("blocks.frost-flash", "Frost flash on hit", Area = "Blocks", Since = "1.4.0",
                 Tags = [ClientCheckTags.Visual], Needs = ClientCheckNeeds.Hosted, Order = 10)]
    public async Task Frost_flash(CheckContext c)
    {
        var dummy = SpawnDummyInFrontOf(c.Player);                    // set the scene with the services you already use
        c.OnEnd(() => Despawn(dummy));                                // runs however the check ends
        c.Instruct("Hit the dummy three times. Does a blue flash appear on each hit?");
        await c.Until(() => Hits(dummy) >= 3, TimeSpan.FromMinutes(2), "three hits");
        c.Expect(Hits(dummy) == 3, "three hits counted", $"counted {Hits(dummy)}");
        await c.Verdict();                                            // pass, fail (with one line) or skip
    }
}
```

### Writing one
- A check is an async method returning `Task` or `ValueTask` and taking one `CheckContext`: an instance method of your
  `Mod` class (it runs on the live instance), a static method, or an instance method of a class with a parameterless
  constructor. Reference `Kiln.Testing` (`<KilnServices>Testing</KilnServices>`) and call `ClientChecks.Register(this)` in
  `OnLoad`; the checks leave the list when the mod unloads.
- `[ClientCheck(id, title)]`: `id` is unique in the mod (lower case, digits, `.`, `_`, `-`, at most 64; by custom
  `area.slug`). `Area` groups the list (default: the mod's name), `Since` names the release that added what the check
  covers, `Order` sorts "run all" (then load order, then declaration order), `Needs` is `Any`, `Hosted` or `Joined`, and
  `Confirm` is a sentence shown before the body runs (see below). A method of the wrong shape is listed with its problem
  and refused.
- `CheckContext`: `Instruct(text)` sets the current instruction (on the HUD line and in the list); `Expect(condition,
  what, detail)` records an assertion beside the verdict, without throwing and without changing the verdict;
  `Verdict()` waits for the player (the first verdict is the check's; a body that returns without asking is asked by
  the runner); `OnEnd(cleanup)` runs last-first when the check ends, however it ends; `Ticks(n)`, `Until(condition,
  timeout, what)` (a timeout records a failed assertion and returns false), `Delay(time)`; `Mod`, `Log`, `Player` (the
  entity of the player who runs it, `EntityId.None` from the control folder), `Session`, and `Cancellation` (signalled when
  the player stops the run, gives a verdict before the body asked, or the mod unloads).
- The body runs on the tick thread like a `[KilnTest]`; every `await` comes back there. The context does not teleport,
  give, spawn or set the time: call the services your mod already references.
- A verdict is the player's. A failed assertion is written next to a pass; it does not turn it into a fail.

### Tags
`ClientCheckTags`: `visual` and `audio` (the player judges what they see or hear), `two-players` (needs a second
player), `cheats` (below), `guard-proof` (below), and `windows`, `steam-deck`, `launcher` for grouping. Any other tag is
free for your own grouping.

### Checks tagged `cheats`
A check that uses level, experience, skill-point or knowledge commands can unlock real Steam achievements on the
player's account, and an unlocked achievement cannot be taken back. The game client reports to Steam whatever the
loaded character reaches. So the runner starts a `cheats` check only while the **achievement guard**
(`Kiln.SteamGuard`, capability `steam.achievement_guard`) of the player's own game client is on and dropping those
reports, and refuses it in every other state. Every line must hold, each read at the moment the check starts:

1. The session is hosted (started from this game's own list or keys), or joined with the joining client's own guard
   answer in its start request. From the control folder it is always refused (nobody at the game answered for its
   guard); together with `two-players` it is always refused (the other player's guard cannot answer).
2. The guard answered its latch with yes. The latch stays on until the game exits: the runner never releases it, so a
   character changed by the check stays protected for the rest of the session.
3. The guard reports itself protecting after the latch: its hook installed, its target verified, on.
4. The check is the `guard-proof` check itself, or the newest `guard-proof` entry in that client's ledger, for the same
   game build (sha256) and Kiln version, is a pass. The first `cheats` check on a new build is the one that shows the
   guard working; until its newest run passed (a later fail or skip closes the gate again), every other `cheats` check
   is refused.

A missing guard, a status that does not arrive or arrives late, a read that throws, a latch that says no and a ledger
that cannot be read all refuse. The refusal names the line that failed, in the list, in kiln.log
(`checks: refused <mod>/<id> ...: line N: ...`) and in the control folder's result. A refused check has no entry.
While a check runs, the runner also holds the guard (cheats or not); when a `cheats` check ends it reads the guard
again, and if a report passed to Steam or the guard stopped protecting during the check, the entry's note says so and a
warning stays on the HUD line until the game exits.

A `guard-proof` check must have a `Confirm` text (one without is refused): it is the one check the guard cannot cover,
because it tests the guard. The runner shows the text before the body runs, and the body starts only after the player
answers it with the screen's own "Yes, run it" button (never the pass key, never a default). No yes: the check does not
start and has no entry. Any other check may carry a `Confirm` text too.

What the guard does not cover is in `Kiln.SteamGuard`'s documentation. In particular, a character changed by a `cheats`
check and loaded later in a game without the guard can still reach its achievements then.

### Running the pass
- **In the game (hosted).** Host a world (single player or co-op host). The list opens with Ctrl+Shift+F5; pass, fail
  and skip are Ctrl+Shift+F6, F7 and F8 (rebind them in the Kiln menu, Keys). The list shows the checks by area with
  each one's last verdict and, where one applies, why it would be refused; "Run" starts one, "Run all" runs them in
  order. The HUD line shows the current instruction all the time, also with the list closed, so you can play the check.
  After fail, type one line and press Enter; Escape records the fail without a note. Stop ends a check without an entry.
  On a gamepad the list's own buttons work with the screen's navigation; the three keys outside the list are keyboard
  only for now. The runner never opens by itself and never takes focus from the game.
- **Joined to a server.** The list shows the server's checks; the body runs on the server, and your instruction, keys
  and verdict stay on your client. A server takes joined checks only while its operator keeps a `kiln/control/checks/`
  folder. Your client needs Kiln and at least one mod with checks loaded (the runner starts with the first
  registration).
- **A dedicated server.** No screen: drive it through the control folder.
- In the main menu nothing runs: host a world or join a server.

### The ledger: `kiln/state/client-checks.json`
Schema `schema/state-client-checks.schema.json`. `checks` is the catalogue of every check the runner has seen (so
checks that never ran can be counted); `entries` holds one entry per verdict: mod and version, id, title, area, since,
tags, Kiln version, game build (side, sha256, verified), session (`hosted`, `joined`, `server`), verdict, note, time,
the assertions, the kiln.log lines written during the check (at most 200 lines and 32 KiB; `logDropped` counts the
rest), and `guard` on `cheats` checks. A check run again adds an entry; the newest one counts. The file is written whole
to a temporary file, flushed to disk and renamed over the old one after every verdict, so a crash loses at most the
check that was waiting for its verdict. A ledger that cannot be read is moved aside as
`client-checks.unreadable-<time>.json` and a new one starts; it is never overwritten. A joined check is written on the
server and on the joining client.

No user name, home path, machine name, Steam id, IP address or token goes into the ledger: notes, assertion details and
log lines pass the same classes of pattern as the launcher's support report (home folders in every slash style become
`<home>`, Steam ids `<steamid>`, IPv4 and IPv6 addresses `<ip>`, secrets after their name `<redacted>`).

### The control folder: `kiln/control/checks/`
Polled only while the folder exists. Write `<name>.cmd`, read `<name>.result.json` (`{command, ok, message, data,
time}`, as the host's control folder writes). A command whose outcome comes later gets its result then.

| Command | Result |
|---|---|
| `list` | every check with its last verdict and refusal, the session and the guard |
| `status` | the running check (phase, instruction, assertions), the queue, the guard, the warning |
| `run <mod> <id>` | when it started, waits for a yes (`kind: confirming`) or was refused (`refused: line N: ...`) |
| `run-all [filter]` | how many were queued (filter: part of a mod, id, title or area, or a tag) |
| `pass`, `fail <one line>`, `skip [one line]` | when the entry is written, with the entry |
| `yes`, `no` | the answer to a check's Confirm text |
| `stop` | ends the running check without an entry and clears the queue |

`mods/tests/client-check-test/fixtures/client_checks_lab.py` drives a lab server this way, and joins it as a scripted
client on kiln.net's lab transport.
