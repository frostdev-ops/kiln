// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Kiln.TestKit.Xunit;

/// <summary>
/// Every <c>[KilnTest]</c> method of <typeparamref name="TMod"/>'s assembly as an xunit test, run under a
/// <see cref="KilnHarness"/> through Kiln.Testing's own runner: the same test body runs offline under <c>dotnet test</c>
/// and in a server.
/// </summary>
/// <example><code>
/// public sealed class MyModTests : KilnTestSuite&lt;MyModTestsMod&gt;
/// {
///     // a test mod that depends on the mod under test (the kiln-test template's shape)
///     protected override void Setup(KilnHarness kiln) => kiln.Load&lt;MyMod&gt;();
/// }
/// </code></example>
/// <remarks>
/// <para><b>As in a server.</b> All tests of the suite run one after another on one harness and one load of the mod, in
/// the runner's order (<c>Order</c>, the type's full name, declaration order), also under <c>dotnet test --filter</c>
/// (the display name is the test's name: <c>--filter "DisplayName~Imc"</c>). The manifest's <c>tests.autoRun</c> is held
/// back. Suites share one xunit collection, so they never run at the same time.</para>
/// <para><b>Results.</b> Pass; fail with the runner's message and the assertion's stack; skip (the test's own, or
/// offline: it asked for a service the kit does not fake or a feature that is off, see
/// <see cref="KilnHarness.RunTest"/>); timeout, as a failure. A test method the runner cannot call (a parameter other
/// than one <c>TestContext</c>, a return type other than void, Task or ValueTask) fails with the runner's reason, as it
/// does in a server. The mod's log lines of the test go to the test's output.</para>
/// <para><b>Watchdog.</b> A test that blocks the tick thread is stopped after its timeout and
/// <see cref="WatchdogGrace"/>, fails as a timeout with the stack where it blocked, and the remaining tests run on a
/// fresh harness and load (their output says so).</para>
/// </remarks>
/// <typeparam name="TMod">The <see cref="Mod"/> whose assembly holds the <c>[KilnTest]</c> methods.</typeparam>
[Collection(KilnTestSuites.CollectionName)]
[TestCaseOrderer(typeof(KilnTestOrderer))]
public abstract class KilnTestSuite<TMod> : IClassFixture<KilnSuiteLifetime> where TMod : Mod
{
    /// <summary>The <c>[KilnTest]</c> names of <typeparamref name="TMod"/>'s assembly, for xunit's discovery.</summary>
    public static IEnumerable<TheoryDataRow<string>> KilnTests =>
        KilnHarness.ListTests(typeof(TMod).Assembly).Select(name => new TheoryDataRow<string>(name) { TestDisplayName = name });

    /// <summary>The options of each harness the suite makes. The default waits up to 30 minutes for another harness of
    /// the process (another suite running in parallel) to end.</summary>
    protected virtual KilnHarnessOptions HarnessOptions() => new() { WaitForOtherHarness = TimeSpan.FromMinutes(30) };

    /// <summary>Before the mod loads, on each new harness: other mods it needs (a test mod's mod under test), fake
    /// players, config files.</summary>
    protected virtual void Setup(KilnHarness kiln) { }

    /// <summary>Loads the mod whose tests run (default <c>kiln.Load&lt;TMod&gt;()</c>, its own manifest file); override
    /// for a <see cref="ModManifest"/> built in code or <see cref="KilnHarness.LoadFolder"/>.</summary>
    protected virtual ModHandle LoadMod(KilnHarness kiln) => kiln.Load<TMod>();

    /// <summary>Unavailable names the tests may ask for and still count (tests of the unavailable path).</summary>
    protected virtual IReadOnlyCollection<string> AllowUnavailable => [];

    /// <summary>How much longer than its timeout a test may block the tick thread before the watchdog stops it.</summary>
    protected virtual TimeSpan WatchdogGrace => KilnTestOptions.Default.WatchdogGrace;

    /// <summary>One <c>[KilnTest]</c>, by name. A mod with no <c>[KilnTest]</c> method gives one skipped test, not a
    /// failure (a suite made by <c>dotnet new kiln-unit-test</c> for a mod that has no tests yet).</summary>
    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(KilnTests))]
    public void KilnTest(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var options = new KilnTestOptions { AllowUnavailable = AllowUnavailable, WatchdogGrace = WatchdogGrace };
        var session = KilnTestSuites.Enter(GetType(), () => new KilnTestSession(kiln =>
        {
            Setup(kiln);
            return LoadMod(kiln);
        }, HarnessOptions, options));
        KilnTestReport.Report(session.Run(name));
    }
}

/// <summary>The class fixture of every <see cref="KilnTestSuite{TMod}"/>: when xunit has run the suite's tests it disposes
/// this, and the suite's harness goes with it.</summary>
public sealed class KilnSuiteLifetime : IDisposable
{
    private readonly Type? _suite = (global::Xunit.TestContext.Current.TestClass as IXunitTestClass)?.Class;

    /// <summary>Disposes the suite's harness.</summary>
    public void Dispose() => KilnTestSuites.Close(_suite);
}

/// <summary>Orders a <see cref="KilnTestSuite{TMod}"/>'s tests as Kiln.Testing's runner does, whatever subset xunit was
/// asked to run; other tests of the class come after, in xunit's default order.</summary>
public sealed class KilnTestOrderer : ITestCaseOrderer
{
    /// <summary>The suite's <c>[KilnTest]</c> cases in the in-game runner's order, then the class's other cases.</summary>
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases) where TTestCase : notnull, ITestCase
    {
        var kiln = new List<(int Index, TTestCase Case)>();
        var others = new List<TTestCase>();
        foreach (var c in testCases)
        {
            if (c is IXunitTestCase x && x.TestMethod.Method.Name == nameof(KilnTestSuite<Mod>.KilnTest) && KilnTestSuites.ModOf(x.TestClass.Class) is { } mod &&
                NameOf(x) is { } name)
            {
                var index = KilnTestSuites.IndexOf(mod, name);
                kiln.Add((index < 0 ? int.MaxValue : index, c));
            }
            else others.Add(c);
        }
        kiln.Sort((a, b) => a.Index.CompareTo(b.Index));
        return [.. kiln.Select(k => k.Case), .. DefaultTestCaseOrderer.Instance.OrderTestCases(others)];
    }

    // A pre-enumerated row keeps its data on the test case; the method's arguments are the fallback.
    private static string? NameOf(IXunitTestCase c) =>
        (c as XunitTestCase)?.TestMethodArguments is [string a] ? a : c.TestMethod.TestMethodArguments is [string b] ? b : null;
}

/// <summary>The suites' sessions, one per suite class.</summary>
internal static class KilnTestSuites
{
    public const string CollectionName = "Kiln.TestKit suites";

    private static readonly object Lock = new();
    private static readonly Dictionary<Type, IReadOnlyList<string>> Lists = [];
    private static readonly Dictionary<Type, KilnTestSession> Sessions = [];
    private static int _hooked;

    /// <summary>The <c>TMod</c> of a suite class, or null.</summary>
    public static Type? ModOf(Type? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KilnTestSuite<>))
                return t.GetGenericArguments()[0];
        return null;
    }

    public static int IndexOf(Type mod, string name)
    {
        IReadOnlyList<string>? list;
        lock (Lock)
            if (!Lists.TryGetValue(mod, out list))
                Lists[mod] = list = KilnHarness.ListTests(mod.Assembly);
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>The session of <paramref name="suite"/>, made on its first test.</summary>
    public static KilnTestSession Enter(Type suite, Func<KilnTestSession> create)
    {
        lock (Lock)
        {
            if (Interlocked.Exchange(ref _hooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => Close(null, throwOnError: false);
            if (!Sessions.TryGetValue(suite, out var session)) Sessions[suite] = session = create();
            return session;
        }
    }

    /// <summary>Disposes the session of <paramref name="suite"/> (null: every session).</summary>
    public static void Close(Type? suite, bool throwOnError = true)
    {
        List<KilnTestSession> closing;
        lock (Lock)
        {
            closing = suite is null ? [.. Sessions.Values] : Sessions.TryGetValue(suite, out var s) ? [s] : [];
            if (suite is null) Sessions.Clear();
            else Sessions.Remove(suite);
        }
        var errors = new List<string>();
        foreach (var s in closing)
        {
            s.Dispose();
            if (s.DisposeError is not { } error) continue;
            errors.Add(error);
            global::Xunit.TestContext.Current.SendDiagnosticMessage($"[kiln] a suite's harness did not stop cleanly: {error}");
        }
        if (throwOnError && errors.Count > 0)
            throw new InvalidOperationException("a suite's harness did not stop cleanly: " + string.Join("\n", errors));
    }
}
