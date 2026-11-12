// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit.v3;

namespace Kiln.TestKit.Xunit;

/// <summary>A <see cref="KilnTestResult"/> as xunit's result: the output lines, then pass, a dynamic skip, or a failure
/// whose stack is the test's.</summary>
internal static class KilnTestReport
{
    public static void Report(KilnTestResult r)
    {
        if (global::Xunit.TestContext.Current.TestOutputHelper is { } output)
        {
            foreach (var note in r.Notes) output.WriteLine($"[kiln] {note}");
            if (r.Unavailable.Count > 0) output.WriteLine($"[kiln] unavailable offline, asked during the test: {string.Join(", ", r.Unavailable)}");
            if (r.Runner is { } runner && runner.Outcome != r.Outcome)
                output.WriteLine($"[kiln] Kiln.Testing's runner said {runner.Outcome.ToString().ToLowerInvariant()}{(runner.Message is null ? "" : ": " + runner.Message)}");
            foreach (var line in r.Log) output.WriteLine(line.ToString());
        }
        switch (r.Outcome)
        {
            case TestOutcome.Pass:
                return;
            case TestOutcome.Skip:
                throw new KilnTestSkippedException(r.Message ?? "skipped");
            case TestOutcome.Timeout:
                throw new KilnTestTimeoutException(r);
            default:
                throw new KilnTestFailedException(r);
        }
    }
}

/// <summary>xunit's dynamic skip: an exception whose message starts with <see cref="DynamicSkipToken.Value"/>.</summary>
internal sealed class KilnTestSkippedException(string reason) : Exception(DynamicSkipToken.Value + reason);

/// <summary>A failed <c>[KilnTest]</c>: the runner's message, and the stack of the assertion in the test.</summary>
internal class KilnTestFailedException(KilnTestResult result) : Exception(result.Message ?? "failed")
{
    /// <summary>The test as the harness ran it.</summary>
    public KilnTestResult Result { get; } = result;

    /// <summary>The test's own stack (where it failed), not this exception's.</summary>
    public override string? StackTrace => Result.Stack;
}

/// <summary>A <c>[KilnTest]</c> that timed out: in the runner (an await that did not finish) or stopped by the watchdog
/// (with the tick thread's stack where it blocked).</summary>
internal sealed class KilnTestTimeoutException(KilnTestResult result) : KilnTestFailedException(result);
