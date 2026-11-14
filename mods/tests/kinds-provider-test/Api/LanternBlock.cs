// SPDX-License-Identifier: MIT
using Kiln;

namespace KindsProviderTest;

/// <summary>The chapter's LanternKind record: a torch that burns for <see cref="Seconds"/>. Section
/// "kinds-provider-test:lanterns".</summary>
public sealed record LanternBlock : IBlock
{
    public string Name { get; init; } = "";
    public double Seconds { get; init; } = 300;
    public int Twigs { get; init; } = 2;
    /// <summary>More recipe needs (item name to count). A list the author may change after a compile: the plan must not.</summary>
    public Dictionary<string, int> Extras { get; init; } = new();
}

/// <summary>A kind that asks for a capability no build has: its plans compile and are refused when applied.</summary>
public sealed record NeedyBlock : IBlock
{
    public string Name { get; init; } = "";
}

/// <summary>What the lab test reads of the provider. It lives as long as the provider's load.</summary>
public static class LanternProbe
{
    private static int _compiles;
    /// <summary>How often the provider's compilers ran in this load of the provider.</summary>
    public static int Compiles => Volatile.Read(ref _compiles);
    public static void Count() => Interlocked.Increment(ref _compiles);
    /// <summary>A plan the consumer leaves here before it reloads itself; its next instance tries to apply it.</summary>
    public static BlockPlan? Left;
}
