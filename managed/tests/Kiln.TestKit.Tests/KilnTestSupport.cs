// SPDX-License-Identifier: MIT
using Kiln.Testing;

namespace Kiln.TestKit.Tests;

internal static class KilnTests
{
    /// <summary>Every test of <paramref name="id"/> in one run of Kiln.Testing's runner, without a filter, as a server
    /// runs them (the control command <c>test &lt;mod&gt;</c>).</summary>
    public static TestReport RunAllInTheRunner(KilnHarness kiln, string id) => kiln.RunAllInTheRunner(id);

    /// <summary>Every listed test of the session's mod, one <see cref="KilnTestSession.Run"/> each.</summary>
    public static List<KilnTestResult> RunAll(this KilnTestSession session, Type mod) =>
        KilnHarness.ListTests(mod.Assembly).Select(session.Run).ToList();

    public static KilnTestResult Named(this IEnumerable<KilnTestResult> results, string name) => results.Single(r => r.Name == name);

    public static string Describe(this IEnumerable<KilnTestResult> results) =>
        string.Join("\n", results.Select(r => $"{r.Name}: {r.Outcome} {r.Message}"));
}
