// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Blocks.Tests;

/// <summary>Abilities.RemoveBuff and BuffsOf without a game: the program Kiln builds, how a waiting removal is judged,
/// the id-only overload's GUID, the capability rows and the error rule. The removal itself runs in a lab
/// (mods/tests/abilities-test).</summary>
public sealed class RemoveBuffTests
{
    [Fact]
    public void TheRemoveProgramCallsRemoveBuffKeepsItsAnswerReadableAndEndsItself()
    {
        var b = Abilities.BuildRemoveProgram();
        Assert.Equal(Abilities.Programs.IdOf(Abilities.RemoveBuffProgram), BitConverter.ToUInt32(b, 0));
        Assert.Equal(Abilities.RemoveBuffProgram, new Guid(b.AsSpan(4, 16)));
        var codeAt = 0x18 + BitConverter.ToInt32(b, 0x18);
        var count = BitConverter.ToInt32(b, 0x1c);
        var code = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt32(b, codeAt + i * 4)).ToArray();
        // removeBuff pops entity [2], buff [1], wasRemoved [0]: pushed in that order, at once (no wait before it)
        Assert.Equal([11u, 0xFFFF0001u, 11u, 0xFFFF0000u, 11u, 0xFFFF0002u, 19u], code[..7]);
        Assert.Equal(0xca687904u, code[8]);
        // POP the native's result, then Done = 1: ICONST1, GSTORE Done
        Assert.Equal([27u, 13u, 24u, 0xFFFF0000u | Abilities.VarDone], code[9..13]);
        // the answer stays readable for RemoveWaitSteps yields (Kiln reads it at the next step and destroys the entity);
        // then the program destroys itself, so no entity waits for good when Kiln's managed side is gone
        Assert.All(code[13..(13 + Abilities.RemoveWaitSteps)], w => Assert.Equal(28u, w));
        Assert.Equal([29u, 30u], code[^2..]);
        Assert.Equal(13 + Abilities.RemoveWaitSteps + 2, code.Length);
        Assert.DoesNotContain(0x38b05bafu, code);  // no kiln.Signal: the answer does not ride the event queue
        // four variables: the buff reference (16 B), the target (4 B), the answer and the done flag (bool, 1 B each)
        var layoutAt = 0x28 + BitConverter.ToInt32(b, 0x28);
        Assert.Equal(4, BitConverter.ToInt32(b, 0x2c));
        Assert.Equal([16, 4, 1, 1], Enumerable.Range(0, 4).Select(i => (int)BitConverter.ToUInt16(b, layoutAt + i * 24 + 12)));
        Assert.Equal(0xc894953du, BitConverter.ToUInt32(b, layoutAt + 2 * 24 + 8));
        Assert.Equal(0xc894953du, BitConverter.ToUInt32(b, layoutAt + 3 * 24 + 8));
    }

    [Fact]
    public void ARemovalIsJudgedFromWhatItsProgramSaysNow()
    {
        // the world, the impact entity, done (null: not readable), the game's answer, age in steps
        Assert.Equal(Abilities.RemovalState.Waiting, Abilities.Judge(true, true, false, false, 1));
        Assert.Equal(Abilities.RemovalState.Waiting, Abilities.Judge(true, true, null, false, 1));   // its instance is not up yet
        Assert.Equal(Abilities.RemovalState.Removed, Abilities.Judge(true, true, true, true, 2));
        Assert.Equal(Abilities.RemovalState.NotRemoved, Abilities.Judge(true, true, true, false, 2));
        Assert.Equal(Abilities.RemovalState.Gone, Abilities.Judge(true, false, null, false, 2));       // canceled, destroyed
        Assert.Equal(Abilities.RemovalState.TimedOut, Abilities.Judge(true, true, false, false, Abilities.RemoveTimeoutSteps + 1));
        Assert.Equal(Abilities.RemovalState.Waiting, Abilities.Judge(true, true, false, false, Abilities.RemoveTimeoutSteps));
        // a closed server world ends everything, whatever was read before
        Assert.Equal(Abilities.RemovalState.WorldClosed, Abilities.Judge(false, true, true, true, 2));
        Assert.Equal(Abilities.RemovalState.WorldClosed, Abilities.Judge(false, false, null, false, 500));
        // only a removal says nothing; every other end names its reason
        Assert.Null(Abilities.ReasonOf(Abilities.RemovalState.Removed));
        Assert.Equal(Abilities.NotRemovedText, Abilities.ReasonOf(Abilities.RemovalState.NotRemoved));
        foreach (var st in new[] { Abilities.RemovalState.Gone, Abilities.RemovalState.WorldClosed, Abilities.RemovalState.TimedOut })
            Assert.False(string.IsNullOrEmpty(Abilities.ReasonOf(st)));
        Assert.Contains("world closed", Abilities.ReasonOf(Abilities.RemovalState.WorldClosed));
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
    public void TheBuffsRowNamesWhatIsMissingAndWaitsForAServerWorld()
    {
        Capability Off(string name, CapabilityReason reason, string text) => new(name, CapabilityState.Unavailable, reason, text, "sigdb");
        var all = (true, true, true);
        Assert.Equal((CapabilityState.Available, CapabilityReason.None, ""),
            AbilitiesCaps.DecideBuffs(all, null, null, null, null, true, true, null));
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.abilities is not loaded"),
            AbilitiesCaps.DecideBuffs(null, null, null, null, null, true, true, null));
        // a sigdb feature that did not resolve is off for good, with its reason
        var (s, r, t) = AbilitiesCaps.DecideBuffs((true, false, true), null, null,
            Off("abilities.programs", CapabilityReason.SymbolMissing, "symbol abilities/GetProgram: 0 matches"), null, true, true, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.SymbolMissing), (s, r));
        Assert.Equal("needs abilities.programs: symbol abilities/GetProgram: 0 matches", t);
        // natives or programs "off" with their features resolved: the game's registries come with a server world. A client's
        // mods load in the main menu, so this is a wait, not a verdict (it was a permanent "unavailable" before)
        (s, r, t) = AbilitiesCaps.DecideBuffs((false, false, true), null, null, null, null, true, true, null);
        Assert.Equal((CapabilityState.Pending, CapabilityReason.Session, AbilitiesCaps.WaitingText), (s, r, t));
        Assert.Equal(CapabilityState.Pending, AbilitiesCaps.DecideBuffs((true, false, true), null, null, null, null, true, true, null).State);
        (s, r, t) = AbilitiesCaps.DecideBuffs((true, true, false), null, null, null, null, true, true, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Faulted), (s, r));
        Assert.Contains("instance hooks", t);
        (s, r, _) = AbilitiesCaps.DecideBuffs(all, null, null, null, null, false, false, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing), (s, r));
        // kiln.spawn is there, but an older one that takes no extra components: RemoveBuff could only throw
        (s, r, t) = AbilitiesCaps.DecideBuffs(all, null, null, null, null, true, false, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceOlder), (s, r));
        Assert.Contains("kiln.spawn", t);
        // an older kiln.spawn is said before the wait for a world: waiting would not help
        Assert.Equal(CapabilityReason.ServiceOlder, AbilitiesCaps.DecideBuffs((false, false, true), null, null, null, null, true, false, null).Reason);
    }

    [Fact]
    public void TheListRowTellsAMissingServiceFromAReflectionThatReadNothing()
    {
        Assert.Equal(CapabilityState.Available, AbilitiesCaps.DecideList(true, null, null).State);
        var (s, r, t) = AbilitiesCaps.DecideList(null, null, null);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing), (s, r));
        Assert.Contains("kiln.playerstate is not loaded", t);
        (s, r, t) = AbilitiesCaps.DecideList(false, null, null);          // the service is there; its reflection read nothing
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Faulted), (s, r));
        Assert.Contains("reflection", t);
        var off = new Capability("players.reflection", CapabilityState.Unavailable, CapabilityReason.Policy, "safe idle", "sigdb");
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Policy, "needs players.reflection: safe idle"), AbilitiesCaps.DecideList(true, null, off));
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.Policy, "needs players.reflection: safe idle"), AbilitiesCaps.DecideList(false, null, off));
    }

    [Fact]
    public void AClosingWorldTakesNoRemovalsUntilAStepRunsAgain()
    {
        Assert.False(Abilities.RemovalsClosed);
        Abilities.CloseRemovals();
        Assert.False(Abilities.RemovalsPending);
        // from here a removal would never be answered (no step reads it): it is refused with a result instead
        Assert.True(Abilities.RemovalsClosed);
        Assert.Contains("closing", Abilities.ClosingText);
        Abilities.PollRemovals();                // the next session's first step
        Assert.False(Abilities.RemovalsClosed);
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
