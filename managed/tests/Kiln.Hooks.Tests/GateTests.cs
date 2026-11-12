// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Hooks.Tests;

/// <summary>The advanced gate: it reads <see cref="ModInfo.Advanced"/> (the loader's flag), and every raw entry point
/// refuses an ordinary mod with the same InvalidOperationException. With no core here, an advanced mod gets past the
/// gate and then meets "unavailable" where the entry point needs the native table: the two are told apart by type and
/// text.</summary>
[Collection("kiln.hooks state")]
public sealed class GateTests
{
    static readonly ModContext Plain = TestMods.Make("plain-mod", advanced: false);
    static readonly ModContext Advanced = TestMods.Make("advanced-mod", advanced: true);

    [Fact]
    public void TheGateReadsTheLoadersFlagNotTheManifestFile()
    {
        // Neither context has a kiln.mod.json in its folder: only ModInfo.Advanced can say "advanced".
        TestMods.As(Plain, () => TestMods.AssertGated("Memory.Read", "plain-mod", () => Memory.Read<int>(0x1000)));
        var e = Assert.Throws<NotSupportedException>(() => TestMods.As(Advanced, () => Memory.Read<int>(0x1000)));
        Assert.Contains("kiln.hooks is unavailable", e.Message);
    }

    [Fact]
    public void OutsideAnyModTheGateAsksForAMod()
    {
        var e = Assert.Throws<InvalidOperationException>(() => TestMods.As(null, () => Memory.Read<int>(0x1000)));
        Assert.Equal("Memory.Read must be called from a mod (on the tick thread or in OnLoad)", e.Message);
    }
}
