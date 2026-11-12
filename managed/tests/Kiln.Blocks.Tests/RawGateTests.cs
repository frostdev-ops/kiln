// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Blocks.Tests;

/// <summary>The advanced gate on the raw entry points of kiln.creatures and kiln.abilities (the same rule as
/// kiln.hooks'): an ordinary mod gets InvalidOperationException naming the entry point; an advanced mod gets past it
/// and, with no core here, meets "not available" (NotSupportedException) where the native table is needed. Kiln's own
/// paths behind those entry points stay open to ordinary mods.</summary>
[CollectionDefinition("raw gates", DisableParallelization = true)]
public sealed class RawGatesCollection;

[Collection("raw gates")]
public sealed class RawGateTests
{
    internal static readonly ModContext Plain =
        HostAccess.CreateContext(new ModInfo("plain-mod", "Plain", "1.0.0", "no-such-folder"), 0, "no-such-folder");
    internal static readonly ModContext Coremod =
        HostAccess.CreateContext(new ModInfo("coremod", "Coremod", "1.0.0", "no-such-folder") { Advanced = true }, 0, "no-such-folder");

    internal static T As<T>(ModContext? mod, Func<T> f)
    {
        var outer = HostAccess.Current;
        HostAccess.Current = mod;
        try { return f(); }
        finally { HostAccess.Current = outer; }
    }

    internal static void As(ModContext? mod, Action f) => As<object?>(mod, () => { f(); return null; });

    internal static void AssertGated(string api, string modId, Action call)
    {
        var e = Assert.Throws<InvalidOperationException>(call);
        Assert.StartsWith($"{api}: mod '{modId}' is not advanced.", e.Message);
        Assert.Contains("add \"advanced\": true to its kiln.mod.json to allow it.", e.Message);
    }

    [Fact]
    public void ArsenalsJobHandlerIsForAdvancedModsOnly()
    {
        As(Plain, () => AssertGated("Arsenals.JobHandler", "plain-mod", () => Arsenals.JobHandler(7, 0)));
        var e = Assert.Throws<NotSupportedException>(() => As(Coremod, () => Arsenals.JobHandler(7, 0)));
        Assert.Contains("kiln.creatures is not available", e.Message);
        var outside = Assert.Throws<InvalidOperationException>(() => As(null, () => Arsenals.JobHandler(7, 0)));
        Assert.Equal("Arsenals.JobHandler must be called from a mod (on the tick thread or in OnLoad)", outside.Message);
    }
}
