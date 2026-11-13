// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Players (who is on the server, progression, player states) by the contract.</summary>
public sealed class PlayersContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Players).Assembly);
        AssertOffOffline("players.positions", "needs kiln.players");
        foreach (var f in PlayersCaps.Features.Concat(PlayersCaps.ProgressionFeatures).Concat(PlayersCaps.StateFeatures))
            AssertOffOffline(f, "needs");
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.players", () => _ = Players.All);
        AssertUnavailable("kiln.players", () => Players.Find(new EntityId(1)));
        AssertUnavailable("kiln.players", () => Players.FindBySteamId(76561198000000000));
        AssertUnavailable("kiln.players", () => Players.PositionOf(new EntityId(1)));
        AssertUnavailable("kiln.progression", () => Progression.Get(new EntityId(1)));
        AssertUnavailable("kiln.progression", () => Progression.SetLevel(new EntityId(1), 5));
    }

    [Fact]
    public void A_progression_result_is_a_kiln_result_and_keeps_its_own_reason_text()
    {
        IKilnResult ok = new ProgressionResult(1, ProgressionOp.SetLevel, new EntityId(1), true, "ok", 5, 2, null, null, "");
        IKilnResult no = new ProgressionResult(2, ProgressionOp.SetLevel, new EntityId(1), false, "not_found", 0, 0, null, null,
            "that player is not in the world");
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        Assert.False(no.Ok);
        Assert.Equal("that player is not in the world", no.Reason);
        Assert.Equal("", ((ProgressionResult)ok).Reason);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Players));
        AssertHas<ServerOnlyAttribute>(typeof(Progression));
        AssertHas<AnyThreadAttribute>(Property(typeof(Players), "All"));
        AssertHas<AnyThreadAttribute>(Method(typeof(Players), "Find", typeof(EntityId)));
        var person = Method(typeof(Players), "FindPerson", typeof(PlayerRef));
        AssertHas<TickThreadOnlyAttribute>(person);
        AssertRequires(person, "kiln.players", "identity.players");
        AssertRequires(Method(typeof(Players), "PositionOf", typeof(EntityId)), "kiln.players", "players.positions");
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(Progression), "UnlockedNodes", typeof(EntityId)));
        AssertRequires(Method(typeof(Progression), "SetLevel", typeof(EntityId), typeof(int)), "kiln.progression");
    }

    [Fact]
    public void Finding_a_person_asks_for_their_current_session_not_an_old_slot()
    {
        // no player source in this process: the person cannot be resolved, which says so
        var e = Assert.Throws<KilnUnavailableException>(() => Players.FindPerson(new PlayerRef(76561198000000000, Guid.Empty, EntityRef.None)));
        Assert.Equal("identity.players", e.Capability);
        Assert.Null(Players.FindPerson(PlayerRef.None));
    }
}

/// <summary>A completed progression request posts what it changed of experience and skill points.</summary>
public sealed class ProgressionChangeTests : ContractTest
{
    private static ProgressionSnapshot Snap(uint experience, int points) =>
        new(new EntityId(1), 5, 25, experience, 1000, 0, points, points, 0, 0, 0, 1, 0, 0, true, true, true, true, false, false, false, 1);

    private static ProgressionResult Done(ulong id, ProgressionOp op, bool ok, ProgressionSnapshot before, ProgressionSnapshot after) =>
        new(id, op, new EntityId(1), ok, ok ? "ok" : "refused", 0, 2, before, after, ok ? "" : "no");

    [Fact]
    public void A_request_that_changed_a_value_posts_one_event_and_one_that_changed_nothing_none()
    {
        var seen = new List<PlayerProgressionChangedEvent>();
        using var sub = Events.On<PlayerProgressionChangedEvent>(seen.Add);
        ProgressionChanges.Completed(Done(7, ProgressionOp.AddExperience, true, Snap(100, 4), Snap(200, 4)), "writer");
        ProgressionChanges.Completed(Done(8, ProgressionOp.AddExperience, true, Snap(200, 4), Snap(200, 4)), "writer");
        ProgressionChanges.Completed(Done(9, ProgressionOp.GiveSkillPoints, false, Snap(200, 4), Snap(200, 6)), "writer");
        ProgressionChanges.Completed(Done(10, ProgressionOp.GiveSkillPoints, true, Snap(200, 4), Snap(200, 6)), "writer");
        Assert.Equal(2, seen.Count);
        Assert.Equal(("Experience", 100L, 200L, 7UL, "writer"), (seen[0].What, seen[0].Before, seen[0].After, seen[0].Request, seen[0].Origin));
        Assert.Equal(("SkillPoints", 4L, 6L, 10UL), (seen[1].What, seen[1].Before, seen[1].After, seen[1].Request));
        Assert.True(seen[0].Involves(new EntityId(1)));
    }

    [Fact]
    public void A_handler_that_changes_progression_again_does_not_loop()
    {
        var calls = 0;
        using var sub = Events.On<PlayerProgressionChangedEvent>(_ =>
        {
            calls++;
            ProgressionChanges.Completed(Done(12, ProgressionOp.AddExperience, true, Snap(200, 4), Snap(300, 4)), "writer");
        });
        ProgressionChanges.Completed(Done(11, ProgressionOp.AddExperience, true, Snap(100, 4), Snap(200, 4)), "writer");
        Assert.Equal(1, calls);
        Assert.Equal(1, ProgressionChanges.Notices.Waiting);
        Step();
        Assert.Equal(2, calls);
    }
}
