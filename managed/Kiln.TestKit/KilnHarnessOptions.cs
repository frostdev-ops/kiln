// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>Settings of one <see cref="KilnHarness"/>.</summary>
public sealed class KilnHarnessOptions
{
    /// <summary>A harness call during which a mod faulted throws <see cref="ModFaultException"/> (default). Off for tests
    /// of the fault policy itself (five faults disable a mod, <see cref="ModDisabledEvent"/>).</summary>
    public bool ThrowOnModFault { get; init; } = true;

    /// <summary>False (default): like a server without the game-log mirror, <see cref="ServerStartedEvent"/> fires at the
    /// first step (source <c>first-step</c>). True: like a server with the mirror, the server stays in
    /// <see cref="ServerPhase.Starting"/> until the game's "world running" line arrives
    /// (<see cref="KilnHarness.ReportWorldRunning"/>, source <c>game-log</c>), or after 2450 steps (source
    /// <c>timeout</c>), so a test can step between starting and started.</summary>
    public bool WaitForWorldRunning { get; init; }

    /// <summary>Wall-clock milliseconds one step stands for (<see cref="TickInfo.WallDeltaMs"/>); 40.8 is a dedicated
    /// server's 24.5 steps a second. The game clock advances by the same time in 60 Hz units (2 or 3 a step at 40.8).</summary>
    public double WallDeltaMs { get; init; } = 40.8;

    /// <summary>Faults within <see cref="FaultWindowSeconds"/> that disable a mod (kiln.json <c>mods.fault_limit</c>).</summary>
    public int FaultLimit { get; init; } = 5;

    /// <summary>The fault window in seconds of the harness's virtual time (steps × <see cref="WallDeltaMs"/>); 0: ever.</summary>
    public double FaultWindowSeconds { get; init; } = 60;

    /// <summary>Crash reports (a mod disabled by the fault policy) are written to <c>kiln/crash-reports</c> in the temp folder.</summary>
    public bool CrashReports { get; init; } = true;

    /// <summary>The timeout of one mod's <c>[KilnTest]</c> run (kiln.json <c>mods.test_timeout_s</c>).</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a new harness waits for the one still alive in this process (harnesses run one at a time: Kiln's
    /// state is process-wide). Then the constructor throws.</summary>
    public TimeSpan WaitForOtherHarness { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Keep the temp server folder after <see cref="KilnHarness.Dispose"/> (also: environment
    /// <c>KILN_TESTKIT_KEEP=1</c>).</summary>
    public bool KeepFolder { get; init; }

    /// <summary>Where the temp server folder is made; default the system's temp folder.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>Gets every captured log line as it is written (any thread), e.g. to show it in a test's output.</summary>
    public Action<LogLine>? LogSink { get; init; }
}
