// SPDX-License-Identifier: MIT
// The guided rejoin checks of mods/tests/world-writes-test (ClientChecks.cs) without the game: their plan's file, its
// reading, when the second half may judge, and which can it may take (RejoinPlan.cs, compiled into this project). The
// review of 0bbe4e0d: a personal can taken by its slot or its water, one plan for every player and world, and a second
// half that passed with no rejoin at all.
using WorldWritesTest;
using Xunit;

namespace Kiln.WorldTests;

public sealed class RejoinPlanTests
{
    private const string World = "11111111-2222-4333-8444-555555555555", Character = "76561198000000000-0123456789abcdef0123456789abcdef";
    private static readonly string Session1 = Rejoin.Session(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), 65, 1200);

    private static RejoinPlan Plan(string world = World, string character = Character, string? session = null) =>
        new("5f0c", world, character, session ?? Session1, 23 * 64, "2026-11-13T20:13:11Z");

    [Fact]
    public void One_plan_per_world_and_character()
    {
        Assert.Equal($"rejoin-{World}-{Character}.json", RejoinPlan.FileName(World, Character));
        Assert.NotEqual(RejoinPlan.FileName(World, Character), RejoinPlan.FileName("22222222-2222-4333-8444-555555555555", Character));
        Assert.NotEqual(RejoinPlan.FileName(World, Character), RejoinPlan.FileName(World, "76561198000000000-other"));
        Assert.Equal("rejoin-a_b-c__d.json", RejoinPlan.FileName("a/b", "c\\.d"));   // never a path
        Assert.Equal(RejoinState.OtherWorldOrCharacter, Rejoin.Judge(Plan(), "22222222-2222-4333-8444-555555555555", Character, "other"));
        Assert.Equal(RejoinState.OtherWorldOrCharacter, Rejoin.Judge(Plan(), World, "76561198000000000-other", "other"));
    }

    [Fact]
    public void A_plan_reads_back_and_anything_else_is_no_plan()
    {
        var p = Plan();
        Assert.Equal(p, RejoinPlan.Parse(p.ToJson()));
        Assert.Null(RejoinPlan.Parse(null));
        Assert.Null(RejoinPlan.Parse(""));
        Assert.Null(RejoinPlan.Parse("{ not json"));
        Assert.Null(RejoinPlan.Parse("""{"Item":2394000183,"Flat":3,"Units":1472,"When":"x"}"""));   // the old, slot-based plan
        Assert.Null(RejoinPlan.Parse(p with { Mark = "" } is var q ? q.ToJson() : ""));
    }

    // Review finding 5: the second half judges only after a leave and a return, never in the first half's session.
    [Fact]
    public void The_second_half_judges_only_in_another_session()
    {
        Assert.Equal(RejoinState.NoPlan, Rejoin.Judge(null, World, Character, Session1));
        Assert.Equal(RejoinState.SameSession, Rejoin.Judge(Plan(), World, Character, Session1));
        // a hosted world opened again: the mod loaded anew
        Assert.Equal(RejoinState.Ready, Rejoin.Judge(Plan(), World, Character, Rejoin.Session(Guid.NewGuid(), 65, 1200)));
        // a player who left a dedicated server and joined again: same mod load, a new connection and join step
        var load = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        Assert.Equal(RejoinState.Ready, Rejoin.Judge(Plan(), World, Character, Rejoin.Session(load, 129, 5400)));
    }

    // Review finding 3: only the item that carries this check's mark is the check's can; none or two: nothing is taken.
    [Fact]
    public void Only_the_one_item_with_the_mark_is_the_checks_can()
    {
        Assert.Equal((2, (string?)null), Rejoin.Marked([null, "other-run", "5f0c", null], "5f0c"));
        var (none, whyNone) = Rejoin.Marked([null, null], "5f0c");   // a personal can at 23 in the old slot: no mark, not taken
        Assert.Equal(-1, none);
        Assert.Contains("no item you carry", whyNone);
        var (two, whyTwo) = Rejoin.Marked(["5f0c", null, "5f0c"], "5f0c");
        Assert.Equal(-1, two);
        Assert.Contains("2 items", whyTwo);
        Assert.Equal(-1, Rejoin.Marked([], "5f0c").Index);
    }
}
