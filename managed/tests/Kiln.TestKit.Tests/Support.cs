// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using Xunit;

// Harnesses run one at a time anyway (Kiln's state is process-wide); the waiting is tested on purpose in HarnessTests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kiln.TestKit.Tests;

internal static class TestMods
{
    private static readonly Lazy<Dictionary<string, string>> Folders = new(() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "mods.txt"))
            .Where(l => l.Length > 0)
            .ToDictionary(l => Path.GetFileNameWithoutExtension(l), l => Path.GetDirectoryName(l)!));

    /// <summary>The built output folder of test mod <paramref name="project"/>.</summary>
    public static string Folder(string project) => Folders.Value[project];

    /// <summary>The lines of <paramref name="mod"/> that start with <paramref name="prefix"/>, texts only.</summary>
    public static List<string> Lines(this KilnHarness kiln, string mod, string prefix) =>
        kiln.LogsOf(mod).Where(l => l.Text.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l.Text).ToList();

    /// <summary>Whether <paramref name="owner"/> has subscriptions on the bus. Its own frame: in a Debug build a
    /// temporary of the test method (the array of subscriptions, which reach the mod's handlers) would stay on the stack and
    /// keep the mod's load context alive through a later unload check.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Subscribed(string owner) => Events.Subscriptions.Any(s => s.Owner == owner);

    public static ModManifest Base() => new("tk-base");
    public static ModManifest User() => new ModManifest("tk-user").DependsOn("tk-base", "^1.0");
}
