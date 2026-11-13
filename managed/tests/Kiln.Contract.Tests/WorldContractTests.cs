// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.World (entities, components, sets, nearby, teleport) by the contract. Its tracked writes have their
/// own tests (Kiln.World.Tests).</summary>
public sealed class WorldContractTests : ContractTest
{
    [Fact]
    public void With_no_service_the_features_are_lowered_and_tracked_writes_say_why()
    {
        StartRows(typeof(World).Assembly);
        foreach (var f in WorldRows.Features) AssertOffOffline(f, "needs kiln.world");
        Assert.False(Capabilities.Is(World.TrackedWritesCapability));
        Assert.NotEqual(CapabilityReason.Unknown, Capabilities.Get(World.TrackedWritesCapability).Reason);
    }

    [Fact]
    public void Members_carry_their_side_and_thread()
    {
        AssertHas<ServerOnlyAttribute>(typeof(World));
        AssertHas<ServerOnlyAttribute>(typeof(Nearby));
        AssertHas<ServerOnlyAttribute>(typeof(Teleport));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(World), "Exists", typeof(EntityId)));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Nearby), "Within", typeof(IEnumerable<EntityId>), typeof(EntityId), typeof(double)));
    }
}

public sealed class TeleportResultTests
{
    [Fact]
    public void A_teleport_result_is_a_kiln_result_and_keeps_its_reason_text()
    {
        IKilnResult moved = new TeleportResult(TeleportStatus.Moved, new EntityId(1), default, default, 0, 0, 1, "");
        Assert.True(moved.Ok);
        Assert.Null(moved.Reason);
        var refused = new TeleportResult(TeleportStatus.Failed, new EntityId(1), default, default, 0, 0, 0, "no such spawn point");
        Assert.False(((IKilnResult)refused).Ok);
        Assert.Equal("no such spawn point", ((IKilnResult)refused).Reason);
    }
}
