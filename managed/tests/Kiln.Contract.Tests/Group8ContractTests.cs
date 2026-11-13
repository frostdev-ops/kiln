// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Abilities (impacts, programs, natives, sequences, buffs, abilities) by the contract.</summary>
public sealed class AbilitiesContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Abilities).Assembly);
        Assert.False(Abilities.Available);
        Assert.Null(Abilities.Status);
        AssertOffline("kiln.abilities");
        foreach (var name in (string[])["abilities.attributes", "abilities.instances", "abilities.natives", "abilities.programs", "abilities.sequences"])
            Assert.StartsWith("needs kiln.abilities", AssertOffline(name).ReasonText);
        Assert.Equal(CapabilityState.Unavailable, Capabilities.Get("abilities.buffs").State);
        Assert.NotEqual(CapabilityReason.Unknown, Capabilities.Get("abilities.buff_list").Reason);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.abilities", () => Abilities.PlaySequence(new EntityId(5000), Guid.NewGuid()));
        AssertUnavailable("kiln.abilities", () => Abilities.Programs.Refresh());
        AssertUnavailable("kiln.abilities", () => Abilities.Natives.Observe("applyDamage"));
    }

    [Fact]
    public void A_refused_cast_is_a_kiln_result()
    {
        IKilnResult refused = CastResult.Refused("on cooldown", TimeSpan.FromSeconds(3));
        Assert.False(refused.Ok);
        Assert.Equal("on cooldown", refused.Reason);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        var play = Method(typeof(Abilities), "PlaySequence", typeof(EntityId), typeof(Guid), typeof(SequenceOptions));
        AssertHas<TickThreadOnlyAttribute>(play);
        AssertRequires(play, "kiln.abilities", "abilities.sequences");
        AssertHas<AnyThreadAttribute>(Property(typeof(Abilities), "Status"));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Abilities), "Cast", typeof(EntityId), typeof(string), typeof(CastOrigin)));
        AssertRequires(Method(typeof(Abilities.Programs), "Refresh"), "kiln.abilities");
    }
}
