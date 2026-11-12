// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Kiln.Host;
using Kiln.Testing;

namespace Kiln.TestKit;

/// <summary>How <see cref="KilnHarness.RunTest"/> judges and guards one <c>[KilnTest]</c>.</summary>
public sealed class KilnTestOptions
{
    /// <summary>The defaults.</summary>
    public static KilnTestOptions Default { get; } = new();

    /// <summary>Unavailable names (<c>kiln.spawn</c>, a game feature) a test may ask for and still count: for tests
    /// whose subject is the unavailable path. Any other name asked during a passing test makes it skipped offline;
    /// failures and timeouts stay failures, with the names in their message.</summary>
    public IReadOnlyCollection<string> AllowUnavailable { get; init; } = [];

    /// <summary>How much longer than the test's timeout one harness step may take before the watchdog stops the tick
    /// thread (default 2 s).</summary>
    public TimeSpan WatchdogGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long <see cref="KilnTestSession"/>'s setup (and the server start) may block the tick thread
    /// (default 60 s).</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>One <c>[KilnTest]</c> as the harness ran it.</summary>
/// <param name="Name">The test's name (<c>attr.Name ?? Class.Method</c>).</param>
/// <param name="Outcome">The kit's verdict: Kiln.Testing's, except that a test that asked for something the kit does not
/// fake is <see cref="TestOutcome.Skip"/> when its body passed; failures and timeouts remain failures, and a test the
/// watchdog stopped is <see cref="TestOutcome.Timeout"/>.</param>
/// <param name="Duration">How long it ran.</param>
/// <param name="Message">Why it failed, was skipped or timed out; for an offline skip also what the body did.</param>
/// <param name="Stack">Where it failed (the assertion's frames), or for a stopped test the tick thread's stack where it
/// blocked.</param>
/// <param name="Unavailable">The unavailable names (services, features) asked while it ran, by the test's mod or a mod it
/// depends on, without the allowed ones.</param>
public sealed record KilnTestResult(string Name, TestOutcome Outcome, TimeSpan Duration, string? Message, string? Stack, IReadOnlyList<string> Unavailable)
{
    /// <summary>What Kiln.Testing's runner reported for the test; null when the watchdog stopped it, or the runner never
    /// reported.</summary>
    public TestResult? Runner { get; init; }

    /// <summary>Every log line written while it ran.</summary>
    public IReadOnlyList<LogLine> Log { get; init; } = [];

    /// <summary>What the kit did around the test (a fresh harness after a stopped test, faults of other code).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed partial class KilnHarness
{
    /// <summary>The steps a test gets at full speed; after them each step waits a millisecond, so a test that waits on
    /// time (<c>Delay</c>, <c>Until</c>, a timer, a thread) gets about a step a millisecond, not a busy core.</summary>
    internal const int FreeSteps = 50;

    /// <summary>
    /// The <c>[KilnTest]</c> names of <paramref name="assembly"/> in the order Kiln.Testing's runner runs them: by
    /// <c>Order</c>, then the type's full name, then declaration order. A name is the attribute's <c>Name</c>, else
    /// <c>Class.Method</c>; a name given twice is listed once (<see cref="RunTest"/> runs both).
    /// </summary>
    public static IReadOnlyList<string> ListTests(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Discover([assembly]).Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal sealed record TestInfo(string Name, int TimeoutSeconds);

    // Kiln.Testing's TestRunner.Discover, by the public attribute (the runner is internal to Kiln.Testing).
    internal static List<TestInfo> Discover(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<(TestInfo Test, int Order, string Type, int Token)>();
        foreach (var asm in assemblies)
        {
            Type?[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (var t in types)
            {
                if (t is null) continue;
                foreach (var m in t.GetMethods(flags))
                {
                    if (m.GetCustomAttribute<KilnTestAttribute>() is not { } attr) continue;
                    found.Add((new TestInfo(attr.Name ?? $"{t.Name}.{m.Name}", attr.TimeoutSeconds), attr.Order, t.FullName ?? t.Name, m.MetadataToken));
                }
            }
        }
        return found.OrderBy(c => c.Order).ThenBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Token).Select(c => c.Test).ToList();
    }

    /// <summary>
    /// Runs one <c>[KilnTest]</c> of <paramref name="mod"/> through Kiln.Testing's own runner (as the control command
    /// <c>test &lt;mod&gt; =name</c> does) and steps the server until the runner reports. Starts the server first when no
    /// step has run.
    /// </summary>
    /// <remarks>
    /// <para><b>Pacing.</b> The first <see cref="FreeSteps"/> steps run at full speed; after them every step is followed by
    /// a millisecond's sleep, so a test that waits on wall time (<c>TestContext.Delay</c>, <c>Until</c>, a timer, its own
    /// thread) costs about one step a millisecond instead of a busy core.</para>
    /// <para><b>Skipped offline.</b> When the test's mod, or a mod it depends on, asked for a Kiln service the kit does not
    /// fake or a game feature that is off while it ran, a passing body becomes <see cref="TestOutcome.Skip"/> naming them.
    /// A failed or timed-out body stays a failure with the names in its message;
    /// <see cref="KilnTestOptions.AllowUnavailable"/> exempts names. A test that
    /// ends in <see cref="KilnUnavailableException"/> is skipped with its capability. Kiln's own code (service ticks, the
    /// loader) asks for tables too; those asks are not the test's.</para>
    /// <para><b>Watchdog.</b> A step that does not return within the test's timeout (its <c>TimeoutSeconds</c>, else the
    /// manifest's <c>tests.timeoutSeconds</c>, else <see cref="KilnHarnessOptions.TestTimeout"/>) plus
    /// <see cref="KilnTestOptions.WatchdogGrace"/> means the test blocks the tick thread: it is stopped (an interrupt for a
    /// wait, sleep or lock; an abort for a loop), reported as <see cref="TestOutcome.Timeout"/> with the tick thread's stack
    /// where it blocked, and the harness is <see cref="HarnessState.Abandoned"/>: dispose it and use a new one
    /// (<see cref="KilnTestSession"/> does that).</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The mod has no <c>[KilnTest]</c> of that name.</exception>
    /// <exception cref="InvalidOperationException">The runner refused to start (the mod is not loaded, or disabled).</exception>
    public KilnTestResult RunTest(ModHandle mod, string name, KilnTestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return RunCore(mod.Id, name, options ?? KilnTestOptions.Default, []);
    }

    /// <summary>Runs every <c>[KilnTest]</c> of <paramref name="mod"/>, one <see cref="RunTest"/> each, in the runner's
    /// order. After a test the watchdog stopped, the harness cannot run more: each remaining test is
    /// <see cref="TestOutcome.Skip"/> with that reason (never passed, never left out).</summary>
    public IReadOnlyList<KilnTestResult> RunTests(ModHandle mod, KilnTestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        Boot();
        var names = Call(() => TestNamesOf(mod.Id));
        var results = new List<KilnTestResult>();
        foreach (var name in names)
        {
            if (_tick.AbandonReason is { } why)
            {
                results.Add(new KilnTestResult(name, TestOutcome.Skip, TimeSpan.Zero, $"not run: {why}", null, []));
                continue;
            }
            results.Add(RunTest(mod, name, options));
        }
        return results;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string[] TestNamesOf(string id) => Discover(LiveOf(id).Context.Assemblies).Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // The runner's timeout for the test, and the mods whose asks count for it. Primitives only: nothing of the mod's
    // load context may stay referenced from here.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (TimeSpan Timeout, HashSet<string> Scope, int Count) Plan(string id, string name)
    {
        var mod = LiveOf(id);
        var tests = Discover(mod.Context.Assemblies).Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var manifest = mod.Manifest.Tests is { TimeoutSeconds: > 0 } t ? TimeSpan.FromSeconds(t.TimeoutSeconds) : _options.TestTimeout;
        var timeout = tests.Count == 0 ? manifest
            : tests.Max(x => x.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(x.TimeoutSeconds) : manifest) * tests.Count;
        var scope = new HashSet<string>(StringComparer.Ordinal) { id };
        var queue = new Queue<string>([id]);
        while (queue.TryDequeue(out var next))
            if (_mods.Find(next) is { } m)
                foreach (var d in m.ResolvedDependencies)
                    if (scope.Add(d)) queue.Enqueue(d);
        return (timeout, scope, tests.Count);
    }

    internal KilnTestResult RunCore(string id, string name, KilnTestOptions options, IReadOnlyList<string> notes)
    {
        ThrowIfDisposed();
        Boot();
        var (timeout, scope, count) = Call(() => Plan(id, name));
        if (count == 0) throw new ArgumentException($"mod {id} has no [KilnTest] named '{name}'", nameof(name));
        var budget = timeout + options.WatchdogGrace;
        var dog = new Watchdog { Budget = budget, Subject = $"test {name}" };
        var unavailable = new UnavailableWatch(_tick.Thread);
        long logMark;
        lock (_logLock) logMark = _logSequence;
        Call(MissCaches.Rearm);
        var askMark = KitBackend.Instance.Asked.Mark();
        var faults = new List<string>();
        TestReport? report = null;
        string? error = null;
        var finished = false;
        var sw = Stopwatch.StartNew();
        string? noReport = null;
        _tick.Watchdog = dog;
        AppDomain.CurrentDomain.FirstChanceException += unavailable.OnFirstChance;
        try
        {
            var steps = 0;
            var started = false;
            while (!finished)
            {
                if (!started)
                {
                    var refusal = Call(() => _mods.StartTests(id, "=" + name, (r, e) =>
                    {
                        report = r;
                        error = e;
                        finished = true;
                    }, out var why) ? null : why);
                    started = refusal is null;
                    if (!started && !refusal!.Contains("already running", StringComparison.Ordinal)) throw new InvalidOperationException(refusal);
                    // refused as "already running": a run of the mod is in progress (tests.autoRun); step it out first
                }
                if (sw.Elapsed > budget + budget + TimeSpan.FromSeconds(5))
                {
                    noReport = started
                        ? $"Kiln.Testing's runner had not reported after {sw.Elapsed.TotalSeconds:0.#} s (the test's timeout is {timeout.TotalSeconds:0.#} s)"
                        : $"another test run of {id} was still in progress after {sw.Elapsed.TotalSeconds:0.#} s, so this test never started";
                    break;
                }
                try
                {
                    Call(StepOnce);
                }
                catch (ModFaultException e)
                {
                    faults.AddRange(e.Faults);
                }
                if (++steps > FreeSteps && !finished) Thread.Sleep(1);
            }
        }
        catch (HarnessStoppedException) when (dog.Stopped is not null)
        {
            // the watchdog stopped the tick thread: reported below
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= unavailable.OnFirstChance;
            _tick.Watchdog = null;
        }
        sw.Stop();
        if (noReport is not null && dog.Stopped is null) GiveUp($"{noReport}: the harness cannot run more tests");
        IReadOnlyList<LogLine> log;
        lock (_logLock) log = _lines.Where(l => l.Sequence > logMark).ToArray();
        var allowed = new HashSet<string>(options.AllowUnavailable, StringComparer.Ordinal);
        var asked = KitBackend.Instance.Asked.Since(askMark)
            .Where(a => a.ModId is not null && scope.Contains(a.ModId) && !allowed.Contains(a.Name))
            .ToList();
        var names = asked.Select(a => a.Name).Distinct(StringComparer.Ordinal).ToArray();
        var allNotes = notes.Concat(faults.Select(f => $"a mod faulted while the test ran: {f}")).ToList();

        if (dog.Stopped is { } stop)
        {
            var message = $"blocked the tick thread: a server step did not return within {budget.TotalSeconds:0.##} s (the test's timeout " +
                          $"{timeout.TotalSeconds:0.##} s and {options.WatchdogGrace.TotalSeconds:0.##} s grace); it was {stop.How}" +
                          (stop.Stack is null ? ". No stack: nothing reached the thread." : "");
            if (!stop.ThreadEnded) allNotes.Add("the tick thread could not be stopped: it keeps running in this process, outside any harness; its mods were retired without OnUnload");
            return Judge(new KilnTestResult(name, TestOutcome.Timeout, sw.Elapsed, message, stop.Stack, names) { Log = log, Notes = allNotes }, asked, null);
        }
        if (noReport is not null)
            return Judge(new KilnTestResult(name, TestOutcome.Timeout, sw.Elapsed, noReport, null, names) { Log = log, Notes = allNotes }, asked, null);
        if (report is null)
            return Judge(new KilnTestResult(name, TestOutcome.Fail, sw.Elapsed, error ?? "the runner reported nothing", null, names) { Log = log, Notes = allNotes },
                asked, unavailable.Capability);

        var mine = report.Results.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
            return Judge(new KilnTestResult(name, TestOutcome.Fail, sw.Elapsed, $"the runner ran {report.Results.Count} test(s) but none named '{name}'", null, names) { Log = log, Notes = allNotes }, asked, null);
        var runner = mine.Count == 1 ? mine[0] : Merge(name, mine);
        var outcome = runner.Outcome;
        if (outcome == TestOutcome.Pass && faults.Count > 0 && _options.ThrowOnModFault)
        {
            // as everywhere in the harness: a mod fault fails the call it happened in (KilnHarnessOptions.ThrowOnModFault)
            return Judge(new KilnTestResult(name, TestOutcome.Fail, TimeSpan.FromMilliseconds(runner.Ms),
                $"the test passed, but a mod faulted while it ran: {faults[0]}", null, names) { Runner = runner, Log = log, Notes = allNotes }, asked, unavailable.Capability);
        }
        return Judge(new KilnTestResult(name, outcome, TimeSpan.FromMilliseconds(runner.Ms), runner.Message, runner.Stack, names) { Runner = runner, Log = log, Notes = allNotes },
            asked, unavailable.Capability);
    }

    // The offline skip rule over the runner's verdict.
    private static KilnTestResult Judge(KilnTestResult r, IReadOnlyList<OfflineAsk> asked, string? unavailableCapability)
    {
        if (r.Outcome == TestOutcome.Skip) return r;  // the test's own Assert.Skip or [KilnTest(Skip = ...)]: its reason
        if (r.Outcome == TestOutcome.Fail && unavailableCapability is not null &&
            r.Message?.StartsWith(nameof(KilnUnavailableException) + ":", StringComparison.Ordinal) == true)
            return r with
            {
                Outcome = TestOutcome.Skip,
                Message = $"needs {unavailableCapability}, which is unavailable offline ({r.Message})",
            };
        if (asked.Count == 0) return r;
        var services = asked.Where(a => a.Kind == AskKind.Service).Select(a => a.Name).Distinct(StringComparer.Ordinal).ToList();
        var features = asked.Where(a => a.Kind == AskKind.Feature).Select(a => a.Name).Distinct(StringComparer.Ordinal).ToList();
        var needs = new List<string>();
        if (services.Count > 0) needs.Add($"{Join(services)}, which the test kit does not fake");
        if (features.Count > 0) needs.Add($"the game feature{(features.Count > 1 ? "s" : "")} {Join(features)}, which {(features.Count > 1 ? "are" : "is")} off offline");
        var body = r.Outcome switch
        {
            TestOutcome.Pass => "the body passed",
            TestOutcome.Timeout => $"the body timed out: {r.Message}",
            _ => $"the body failed: {r.Message}",
        };
        return r with
        {
            Outcome = r.Outcome == TestOutcome.Pass ? TestOutcome.Skip : r.Outcome,
            Message = $"needs {string.Join(", and ", needs)}; {body}",
        };
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    // Two tests with one name: the worst outcome, both messages.
    private static TestResult Merge(string name, IReadOnlyList<TestResult> results)
    {
        var worst = results.OrderByDescending(r => r.Outcome switch
        {
            TestOutcome.Timeout => 3,
            TestOutcome.Fail => 2,
            TestOutcome.Pass => 1,
            _ => 0,
        }).First();
        return new TestResult(name, worst.Outcome, results.Sum(r => r.Ms),
            $"{results.Count} tests are named '{name}': " + string.Join("; ", results.Select(r => $"{r.Outcome.ToString().ToLowerInvariant()}{(r.Message is null ? "" : ": " + r.Message)}")),
            worst.Stack);
    }

    /// <summary>For the kit's tests: every test of <paramref name="id"/> in one run of the runner without a filter, as
    /// the control command <c>test &lt;mod&gt;</c> runs them in a server.</summary>
    internal TestReport RunAllInTheRunner(string id)
    {
        TestReport? report = null;
        string? error = null;
        _mods.SuppressAutoTests = true;  // tests.autoRun would start the same run first, and this one would be refused
        Boot();
        Call(() =>
        {
            if (!_mods.StartTests(id, null, (r, e) => (report, error) = (r, e), out var why)) throw new InvalidOperationException(why);
        });
        StepUntil(() => report is not null || error is not null, 100_000, TimeSpan.FromMilliseconds(1));
        return report ?? throw new InvalidOperationException(error);
    }

    /// <summary>Gives the (idle) tick thread up: the harness cannot run more, and its dispose retires its mods without
    /// it.</summary>
    internal void GiveUp(string reason) => _tick.Abandon(reason);

    /// <summary>Dispose of a harness whose tick thread was given up on: the stop cannot run there, so what an unload does
    /// that needs no tick thread is done here, from the disposing thread. Every mod's context is retired (its event
    /// subscriptions, config registrations, commands, scheduled work, messages and services' per-mod state go, as at an
    /// unload), its native owner is revoked and its load context is told to unload. Not run: <c>OnUnload</c>,
    /// <see cref="ServerStoppingEvent"/>, and anything else of the mod's own code.</summary>
    private void RetireAbandoned()
    {
        var retired = new List<string>();
        LoadedMod[] mods;
        try
        {
            mods = _mods.Mods.Reverse().ToArray();
        }
        catch (InvalidOperationException)
        {
            mods = [];  // the stuck thread changed the list as it was read
        }
        foreach (var m in mods)
        {
            try
            {
                m.ModContext.Retire();
                foreach (var other in mods)
                    if (!ReferenceEquals(other, m))
                        other.ModContext.Messages.DropFrom(m.ModContext);
                _host.OwnerRevoke(m.Owner);
                if (m.Context.IsCollectible) m.Context.Unload();
                retired.Add(m.Id);
            }
            catch (Exception e)
            {
                _host.Log(LogLevel.Warn, $"testkit: retiring {m.Id} after the harness was stopped failed: {e.GetType().Name}: {e.Message}");
            }
        }
        try
        {
            UnloadCaches.Clear(_host.Log);
        }
        catch (Exception e)
        {
            _host.Log(LogLevel.Warn, $"testkit: clearing the loader's caches failed: {e.Message}");
        }
        _host.Log(LogLevel.Warn, $"testkit: harness stopped ({_tick.AbandonReason}); retired without OnUnload: {(retired.Count == 0 ? "none" : string.Join(", ", retired))}");
    }

    // A KilnUnavailableException thrown on the tick thread while the test runs: its capability, for the skip rule.
    private sealed class UnavailableWatch(Thread tick)
    {
        private volatile string? _capability;

        public string? Capability => _capability;

        public void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is KilnUnavailableException u && ReferenceEquals(Thread.CurrentThread, tick)) _capability = u.Capability;
        }
    }
}

/// <summary>
/// Runs a mod's <c>[KilnTest]</c> methods one by one on one harness and one load, as a server runs them, for a test
/// framework's adapter (<c>Kiln.TestKit.Xunit.KilnTestSuite</c> is one). The harness is made on the first
/// <see cref="Run"/>: <c>setup</c> gives it what it needs before the server starts (other mods, files) and loads the mod
/// whose tests run; then the server starts with the manifest's <c>tests.autoRun</c> held back (the session runs every
/// test itself). After a test the watchdog stopped, the next <see cref="Run"/> gets a fresh harness and a fresh load, and
/// its result says so.
/// </summary>
public sealed class KilnTestSession : IDisposable
{
    private readonly Func<KilnHarness, ModHandle> _setup;
    private readonly Func<KilnHarnessOptions> _harnessOptions;
    private readonly KilnTestOptions _options;
    private KilnHarness? _harness;
    private ModHandle? _mod;
    private string? _setupFailure, _restartNote;
    private bool _disposed;

    /// <param name="setup">On a new harness, before its first step: loads what the tests need and returns the handle of
    /// the mod whose <c>[KilnTest]</c> methods run.</param>
    /// <param name="harnessOptions">The options of each new harness; default <see cref="KilnHarnessOptions"/>'s.</param>
    /// <param name="options">How tests are judged and guarded.</param>
    public KilnTestSession(Func<KilnHarness, ModHandle> setup, Func<KilnHarnessOptions>? harnessOptions = null, KilnTestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _harnessOptions = harnessOptions ?? (() => new KilnHarnessOptions());
        _options = options ?? KilnTestOptions.Default;
    }

    /// <summary>The harness the next test runs on, if one is made (null before the first <see cref="Run"/>).</summary>
    public KilnHarness? Harness => _harness;

    /// <summary>How many harnesses the session made (more than one after a stopped test).</summary>
    public int HarnessCount { get; private set; }

    /// <summary>What the last dispose of a harness reported, if it did not stop cleanly.</summary>
    public string? DisposeError { get; private set; }

    /// <summary>Runs the test named <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">The setup threw or blocked: no test of the session can run.</exception>
    public KilnTestResult Run(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_setupFailure is not null) throw new InvalidOperationException(_setupFailure);
        if (_harness is { State: HarnessState.Abandoned })
        {
            _restartNote = $"ran on a fresh harness and a fresh load: the previous one was stopped ({_harness.TickThread.AbandonReason})";
            Retire();
        }
        if (_harness is null) Start();
        var notes = _restartNote is null ? Array.Empty<string>() : [_restartNote];
        return _harness!.RunCore(_mod!.Id, name, _options, notes);
    }

    private void Start()
    {
        var harness = new KilnHarness(_harnessOptions());
        HarnessCount++;
        _harness = harness;
        harness.Manager.SuppressAutoTests = true;  // the session runs every test; the manifest's autoRun would race it
        var dog = new Watchdog { Budget = _options.SetupTimeout, Subject = "the suite's setup" };
        harness.TickThread.Watchdog = dog;
        try
        {
            _mod = _setup(harness);
            if (_mod is null) throw new InvalidOperationException("the setup returned no mod");
            harness.Boot();
            if (harness.State == HarnessState.Starting)
            {
                harness.ReportWorldRunning();
                harness.StepUntil(() => harness.State == HarnessState.Running, 10);
            }
        }
        catch (Exception e)
        {
            _setupFailure = dog.Stopped is { } stop
                ? $"the suite's setup blocked the tick thread for more than {_options.SetupTimeout.TotalSeconds:0} s and was {stop.How}" +
                  (stop.Stack is null ? "" : "\n" + stop.Stack)
                : $"the suite's setup failed: {e.GetType().Name}: {e.Message}";
            var said = harness.Logs.Where(l => l.Level <= LogLevel.Warn).Select(l => l.ToString()).ToList();
            if (said.Count > 0) _setupFailure += "\nKiln's warnings and errors:\n" + string.Join("\n", said);
            Retire();
            throw new InvalidOperationException(_setupFailure, e);
        }
        finally
        {
            if (harness.State != HarnessState.Abandoned && harness.State != HarnessState.Disposed) harness.TickThread.Watchdog = null;
        }
    }

    private void Retire()
    {
        var h = _harness;
        _harness = null;
        _mod = null;
        if (h is null) return;
        try
        {
            h.Dispose();
        }
        catch (Exception e)
        {
            DisposeError = e.Message;
        }
    }

    /// <summary>Disposes the current harness.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Retire();
    }
}

/// <summary>
/// Services that remember a missing native table (<c>_looked</c>, <c>_checked</c>, <c>*Tried</c> next to a null table
/// pointer) ask the backend once per process, so the skip rule would see only the first test that used one. Before each
/// test the kit forgets those misses again, and the next use asks (and is recorded) anew. A service that keeps its miss
/// some other way (a <c>Lazy</c>, a <c>static readonly</c>) is not reached.
/// </summary>
internal static partial class MissCaches
{
    private static readonly object Lock = new();
    private static readonly HashSet<Type> Askers = [];
    private static readonly HashSet<Assembly> Assemblies = [];
    private static readonly HashSet<Type> Known = [];
    private static readonly List<(FieldInfo Flag, FieldInfo[] Tables)> Sites = [];
    private static bool _dirty;

    [GeneratedRegex("^_(looked|checked|tried)$|^_([A-Za-z0-9]+)(Tried|Looked|Checked)$", RegexOptions.CultureInvariant)]
    private static partial Regex FlagName();

    /// <summary>From <see cref="KitBackend.Service"/> on a miss: who asked.</summary>
    public static void Note()
    {
        var trace = new StackTrace(2, false);
        for (var i = 0; i < trace.FrameCount && i < 8; i++)
        {
            var type = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
            if (type is null || type.Assembly == typeof(MissCaches).Assembly || type.FullName == "Kiln.Services") continue;
            lock (Lock)
                for (var t = type; t is not null; t = t.DeclaringType)
                    if (Askers.Add(t))
                        _dirty = true;
            return;
        }
    }

    /// <summary>Forgets every remembered miss whose table is still null.</summary>
    public static unsafe void Rearm()
    {
        lock (Lock)
        {
            if (_dirty)
            {
                _dirty = false;
                foreach (var t in Askers)
                {
                    Add(t);  // its static constructor has run: it asked
                    if (Assemblies.Add(t.Assembly))
                    {
                        Type?[] types;
                        try
                        {
                            types = t.Assembly.GetTypes();
                        }
                        catch (ReflectionTypeLoadException e)
                        {
                            types = e.Types;
                        }
                        // others of its assembly only when reading their statics runs no static constructor
                        foreach (var other in types)
                            if (other is { TypeInitializer: null, ContainsGenericParameters: false })
                                Add(other);
                    }
                }
            }
            foreach (var (flag, tables) in Sites)
            {
                try
                {
                    if (tables.Any(f => f.GetValue(null) is Pointer p && Pointer.Unbox(p) != null)) continue;
                    flag.SetValue(null, flag.FieldType == typeof(bool) ? false : 0);
                }
                catch (Exception)
                {
                    // a field that cannot be read or written any more: left as it is
                }
            }
        }
    }

    private static void Add(Type type)
    {
        if (!Known.Add(type)) return;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo[] fields;
        try
        {
            fields = type.GetFields(flags);
        }
        catch (Exception)
        {
            return;
        }
        var tables = fields.Where(f => f.FieldType.IsPointer && !f.IsInitOnly && !f.IsLiteral).ToArray();
        if (tables.Length == 0) return;
        foreach (var f in fields)
        {
            if (f.IsInitOnly || f.IsLiteral || (f.FieldType != typeof(bool) && f.FieldType != typeof(int))) continue;
            var m = FlagName().Match(f.Name);
            if (!m.Success) continue;
            var own = m.Groups[2].Success ? tables.Where(t => t.Name == "_" + m.Groups[2].Value).ToArray() : tables;
            if (own.Length > 0) Sites.Add((f, own));
        }
    }
}

public static partial class Coverage
{
    static partial void AddRunner(List<CoverageRow> rows)
    {
        rows.Add(new CoverageRow("[KilnTest] tests", CoverageKind.Real, [],
            "Kiln.Testing's own runner, started test by test through Kiln.Host (KilnHarness.RunTest, KilnTestSession, the xunit " +
            "KilnTestSuite): same order, timeouts and results as in a server. Offline a passing body that asked for an unfaked service or a " +
            "feature that is off is skipped with the names; failed bodies and timeouts remain failures with the names. A test that blocks the tick thread is stopped by the watchdog and " +
            "reported as a timeout with the stack where it blocked"));
    }
}
