// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Blocks.Tests;

/// <summary>Abilities.RemoveBuff and BuffsOf without a game: the program Kiln builds, the id-only overload's GUID, the
/// capability rows and the error rule. The removal itself runs in a lab (mods/tests/abilities-test).</summary>
public sealed class RemoveBuffTests
{
    [Fact]
    public void TheRemoveProgramWaitsOneStepThenCallsRemoveBuffAndSignalsItsAnswer()
    {
        var b = Abilities.BuildRemoveProgram();
        Assert.Equal(Abilities.Programs.IdOf(Abilities.RemoveBuffProgram), BitConverter.ToUInt32(b, 0));
        Assert.Equal(Abilities.RemoveBuffProgram, new Guid(b.AsSpan(4, 16)));
        var codeAt = 0x18 + BitConverter.ToInt32(b, 0x18);
        var count = BitConverter.ToInt32(b, 0x1c);
        var code = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(b, codeAt + i * 4)).ToArray();
        Assert.Equal(28u, code[0]);  // RVM first: the listener has asked for signals before removeBuff runs
        var remove = Array.IndexOf(code, 0xca687904u);
        var signal = Array.IndexOf(code, 0x38b05bafu);
        Assert.True(remove > 0 && signal > remove, "removeBuff, then kiln.Signal");
        Assert.Equal(19u, code[remove - 2]);
        Assert.Equal(19u, code[signal - 2]);
        // removeBuff pops entity [2], buff [1], wasRemoved [0]: pushed in that order
        Assert.Equal([11u, 0xFFFF0001u, 11u, 0xFFFF0000u, 11u, 0xFFFF0002u], code[1..7]);
        // kiln.Signal(value = wasRemoved, tag)
        Assert.Equal([11u, Abilities.RemoveSignalTag, 11u, 0xFFFF0002u], code[(signal - 6)..(signal - 2)]);
        Assert.Equal([27u, 29u, 30u], code[^3..]);
        // three variables: the buff reference (16 B), the target (4 B), the answer (bool, 1 B)
        var layoutAt = 0x28 + BitConverter.ToInt32(b, 0x28);
        Assert.Equal(3, BitConverter.ToInt32(b, 0x2c));
        Assert.Equal([16, 4, 1], Enumerable.Range(0, 3).Select(i => (int)BitConverter.ToUInt16(b, layoutAt + i * 24 + 12)));
        Assert.Equal(0xc894953du, BitConverter.ToUInt32(b, layoutAt + 2 * 24 + 8));
    }

    [Theory]
    [InlineData(0x339519bdu)]  // the healing potion's heal-over-time buff
    [InlineData(0x00000001u)]
    [InlineData(0xffffffffu)]
    [InlineData(0xc6a35bfeu)]
    public void AnIdBecomesSixteenBytesWithThatHash(uint id) => Assert.Equal(id, Abilities.IdOf(Abilities.GuidWithId(id)));

    [Fact]
    public void BadArgumentsThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => { _ = Abilities.RemoveBuff(EntityId.None, Guid.NewGuid()); });
        Assert.Throws<ArgumentException>(() => { _ = Abilities.RemoveBuff(EntityId.None, 0x339519bdu); });
        Assert.Throws<ArgumentException>(() => { _ = Abilities.RemoveBuff(new EntityId(5), 0u); });
    }

    [Fact]
    public void WithoutKilnAbilitiesTheCallsThrowKilnUnavailable()
    {
        var e = Assert.Throws<KilnUnavailableException>(() => { _ = Abilities.RemoveBuff(new EntityId(5), Guid.NewGuid()); });
        Assert.Equal("abilities.buffs", e.Capability);
        Assert.False(Capabilities.Is("abilities.buffs"));
    }

    [Fact]
    public void TheBuffsRowNamesWhatIsMissing()
    {
        Capability Off(string name, CapabilityReason reason, string text) => new(name, CapabilityState.Unavailable, reason, text, "sigdb");
        var all = (true, true, true);
        Assert.Equal((CapabilityState.Available, CapabilityReason.None, ""),
            AbilitiesCaps.DecideBuffs(all, null, null, null, null, true, null));
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.abilities is not loaded"),
            AbilitiesCaps.DecideBuffs(null, null, null, null, null, true, null));
        var (s, r, t) = AbilitiesCaps.DecideBuffs((true, false, true), null, null,
            Off("abilities.programs", CapabilityReason.SymbolMissing, "symbol abilities/GetProgram: 0 matches"), null, true, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.SymbolMissing), (s, r));
        Assert.Equal("needs abilities.programs: symbol abilities/GetProgram: 0 matches", t);
        (s, r, t) = AbilitiesCaps.DecideBuffs((true, true, false), null, null, null, null, true, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Faulted), (s, r));
        Assert.Contains("instance hooks", t);
        (s, r, _) = AbilitiesCaps.DecideBuffs(all, null, null, null, null, false, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing), (s, r));
    }

    [Fact]
    public void TheListRowNeedsKilnPlayersAndItsReflection()
    {
        Assert.Equal(CapabilityState.Available, AbilitiesCaps.DecideList(true, null, null).State);
        Assert.Equal(CapabilityReason.ServiceMissing, AbilitiesCaps.DecideList(false, null, null).Reason);
        var off = new Capability("players.reflection", CapabilityState.Unavailable, CapabilityReason.Policy, "safe idle", "sigdb");
        var (s, r, t) = AbilitiesCaps.DecideList(true, null, off);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Policy, "needs players.reflection: safe idle"), (s, r, t));
    }

    [Fact]
    public void AnUnknownSignalIsNotARemoval()
    {
        Assert.False(Abilities.CompleteRemoval(123456, true));
        Assert.False(Abilities.RemovalsPending);
    }

    [Fact]
    public void TheResultFollowsTheErrorRule()
    {
        IKilnResult ok = new BuffRemoveResult(new EntityId(7), 0x339519bd, true, "ignored");
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        var no = new BuffRemoveResult(new EntityId(7), 0x339519bd, false, Abilities.NotRemovedText);
        Assert.False(no.Ok);
        Assert.Equal(Abilities.NotRemovedText, no.Reason);
        Assert.Contains("not removed", no.ToString());
    }
}
