// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Rules (per-player rules, creative mode) by the contract. NoClip has its own tests (Kiln.Rules.Tests).</summary>
public sealed class RulesContractTests : ContractTest
{
    [Fact]
    public void With_no_service_the_features_are_lowered()
    {
        StartRows(typeof(Rules).Assembly);
        foreach (var f in RulesCaps.Features) AssertOffOffline(f, "needs kiln.rules");
    }

    [Fact]
    public void A_creative_result_is_a_kiln_result_and_keeps_its_message()
    {
        IKilnResult ok = new CreativeResult(true, true, "Creative mode on for Ada.");
        IKilnResult no = new CreativeResult(false, false, "Ada's game has no Kiln.");
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        Assert.Equal("Ada's game has no Kiln.", no.Reason);
        Assert.Equal("Creative mode on for Ada.", ((CreativeResult)ok).Message);
    }

    [Fact]
    public void Members_carry_their_side_and_thread()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Rules));
        AssertHas<ServerOnlyAttribute>(typeof(Creative));
        AssertHas<ServerOnlyAttribute>(typeof(CreativeTools));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Creative), "Set", typeof(EntityId), typeof(bool)));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Rules), "Of", typeof(EntityId)));
    }
}
