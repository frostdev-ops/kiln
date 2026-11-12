// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Blocks.Tests.RawGateTests;

namespace Kiln.Blocks.Tests;

/// <summary>kiln.abilities' raw entry points: a program blob (<see cref="Abilities.Programs.Register(byte[])"/>).
/// Kiln's own paths behind it (the buff program, asset programs) stay open to ordinary mods.</summary>
[Collection("raw gates")]
public sealed class AbilitiesGateTests
{
    [Fact]
    public void ProgramsRegisterIsForAdvancedModsOnly()
    {
        var blob = new byte[16];
        As(Plain, () =>
        {
            AssertGated("Abilities.Programs.Register", "plain-mod", () => Abilities.Programs.Register(blob));
            AssertGated("Abilities.Programs.Register", "plain-mod", () => Abilities.Programs.Register(blob, out _));
        });
        As(Coremod, () =>
        {
            var e = Assert.Throws<NotSupportedException>(() => Abilities.Programs.Register(blob));
            Assert.Contains("kiln.abilities is not available", e.Message);
            Assert.Throws<NotSupportedException>(() => Abilities.Programs.Register(blob, out _));
        });
        Assert.Throws<InvalidOperationException>(() => As(null, () => Abilities.Programs.Register(blob)));
    }

    [Fact]
    public void KilnsOwnProgramsStayOpenToOrdinaryMods()
    {
        // ApplyBuff registers Kiln's buff program for whichever mod uses buffs: it reaches the native table (here: none),
        // never the gate
        var buff = As(Plain, () => Abilities.ApplyBuff(new EntityId(5), Guid.NewGuid()));
        var e = Assert.IsType<NotSupportedException>(buff.Exception?.InnerException);
        Assert.Equal("Abilities.Programs.Register: kiln.abilities is not available on this build", e.Message);
        // Kiln.Blocks ships generated programs as assets and asks kiln.abilities to pick them up: open, no gate
        var refresh = Assert.Throws<NotSupportedException>(() => As(Plain, () => Abilities.Programs.Refresh()));
        Assert.Equal("Abilities.Programs.Refresh: kiln.abilities is not available on this build", refresh.Message);
    }
}
