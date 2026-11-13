// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Saves (world, player, character and entity data, saves and backups) by the contract. InstanceData has
/// its own tests (Kiln.Saves.Tests).</summary>
public sealed class SavesContractTests : ContractTest
{
    [Fact]
    public void With_no_service_the_features_are_lowered_and_the_rows_are_there()
    {
        StartRows(typeof(Saves).Assembly);
        foreach (var f in SavesCaps.Features) AssertOffOffline(f, "needs kiln.saves");
        Assert.NotEqual(CapabilityReason.Unknown, Capabilities.Get("saves.instance_data").Reason);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        if (SavesRuntime.Ensure()) return;  // a table from another test of this process (none is served here)
        AssertUnavailable("kiln.saves", () => WorldData.Set("k", new object()));
        AssertUnavailable("kiln.saves", () => PlayerData.Get<object>(76561198000000000, "k"));
        AssertUnavailable("kiln.saves", () => Saves.Request());
    }

    [Fact]
    public void A_save_result_is_a_kiln_result()
    {
        IKilnResult ok = new SaveResult(true, SaveReason.Request, 7, TimeSpan.FromMilliseconds(40), null);
        IKilnResult no = new SaveResult(false, SaveReason.Request, 0, TimeSpan.Zero, "the world is not running");
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        Assert.False(no.Ok);
        Assert.Equal("the world is not running", no.Reason);
    }

    [Fact]
    public void Members_carry_their_capability()
    {
        AssertRequires(Method(typeof(Saves), "Request"), "kiln.saves");
        // for a player EntityData is PlayerData and needs no kiln.spawn: the mark names what every entity needs
        AssertRequires(typeof(EntityData).GetMethods().First(m => m.Name == "Set"), "kiln.saves");
        AssertRequires(typeof(Saves).GetMethods().First(m => m.Name == "RestoreOnNextStart"), "kiln.saves");
        AssertRequires(Property(typeof(WorldData), "WorldId"));  // answers null without the service
    }
}
