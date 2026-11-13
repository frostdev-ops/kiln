// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Gameplay by the contract.</summary>
public sealed class GameplayContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Gameplay).Assembly);
        Assert.False(Gameplay.Available);
        Assert.False(Gameplay.EntityEventsAvailable);
        AssertOffline("kiln.gameplay");
        foreach (var name in (string[])["gameplay.emit", "gameplay.recipes", "gameplay.spawn", "gameplay.entities", "gameplay.entity_events", "gameplay.spawn_replace"])
            Assert.StartsWith("needs kiln.gameplay", AssertOffline(name).ReasonText);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.gameplay", () => Gameplay.Recipe(1));
        AssertUnavailable("kiln.gameplay", () => Gameplay.WorkshopRecipes(1));
        AssertUnavailable("kiln.gameplay", () => Gameplay.WatchTeleports(new EntityId(5000), true));
        AssertUnavailable("kiln.gameplay", () => Gameplay.ReplaceNaturalSpawns(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gameplay.ReplaceNaturalSpawns(Guid.NewGuid(), Guid.NewGuid(), 2));
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Gameplay));
        AssertHas<ServerOnlyAttribute>(typeof(Offers));
        var recipe = Method(typeof(Gameplay), "Recipe", typeof(uint));
        AssertHas<TickThreadOnlyAttribute>(recipe);
        AssertRequires(recipe, "gameplay.recipes");
        AssertRequires(typeof(Gameplay.Inject), "gameplay.emit");
        var replace = Method(typeof(Gameplay), "ReplaceNaturalSpawns", typeof(Guid), typeof(Guid), typeof(double));
        AssertHas<AnyThreadAttribute>(replace);
        AssertRequires(replace, "gameplay.spawn_replace");
        AssertHas<AnyThreadAttribute>(Property(typeof(Gameplay), "Status"));
    }
}

/// <summary>Kiln.Build by the contract.</summary>
public sealed class BuildContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Build).Assembly);
        Assert.False(Build.Available);
        Assert.Null(Build.Status);
        AssertOffline("kiln.build");
        foreach (var name in (string[])["build.read", "build.write", "build.persist", "build.items", "build.zones"])
            Assert.StartsWith("needs kiln.build", AssertOffline(name).ReasonText);
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.build", () => _ = Build.Grid);
        AssertUnavailable("kiln.build", () => Build.IsLoaded(default));
        AssertUnavailable("kiln.build", () => Build.Prop(new EntityId(5000)));
        AssertUnavailable("kiln.build", () => _ = Build.Bases);
        AssertUnavailable("kiln.build", () => Build.RemoveProp(new EntityId(5000)));
    }

    [Fact]
    public void The_results_are_kiln_results_with_the_refusal_in_words()
    {
        IKilnResult ran = new BuildResult(12, 1, true, true, default, 0.5);
        Assert.True(ran.Ok);
        Assert.Null(ran.Reason);
        IKilnResult dry = new BuildResult(0, 0, false, false, default, 0, BuildRefusal.DryRun);
        Assert.True(dry.Ok);
        var zone = new BuildResult(0, 0, false, false, default, 0, BuildRefusal.Zone, 7);
        Assert.False(zone.Ok);
        Assert.True(zone.Refused);
        Assert.Contains("zone 7", zone.Reason);
        foreach (var refusal in Enum.GetValues<BuildRefusal>())
        {
            var r = new BuildResult(0, 0, false, false, default, 0, refusal);
            Assert.Equal(r.Refused, !r.Ok);
            Assert.Equal(r.Ok, r.Reason is null);
        }

        IKilnResult paste = new PasteResult(default, new BuildResult(4, 1, true, true, default, 0), 2, 1, 0, 0, []);
        Assert.False(paste.Ok);
        Assert.Equal("1 of 3 props could not be placed", paste.Reason);
        IKilnResult refusedPaste = new PasteResult(default, zone, 0, 0, 0, 0, []);
        Assert.Equal(zone.Reason, refusedPaste.Reason);
        IKilnResult pasted = new PasteResult(default, new BuildResult(4, 1, true, true, default, 0), 2, 0, 0, 0, []);
        Assert.True(pasted.Ok);
        Assert.Null(pasted.Reason);

        IKilnResult once = new PlaceOnceResult(false, false, null, null, "the area did not load");
        Assert.False(once.Ok);
        Assert.Equal("the area did not load", once.Reason);
        IKilnResult again = new PlaceOnceResult(false, true, null, null);
        Assert.True(again.Ok);
        Assert.Null(again.Reason);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Build));
        AssertHas<AnyThreadAttribute>(Property(typeof(Build), "Status"));
        var read = Method(typeof(Build), "Read", typeof(CellBox));
        AssertHas<TickThreadOnlyAttribute>(read);
        AssertRequires(read, "kiln.build");
        AssertRequires(Property(typeof(Build), "Available"));
    }
}

/// <summary>Kiln.Skills by the contract.</summary>
public sealed class SkillsContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Skills).Assembly);
        Assert.False(GameTree.Available);
        AssertOffline("kiln.skills");
        foreach (var name in (string[])["skills.functions", "skills.login_guard", "skills.shadow", "skills.tree_load"])
            Assert.StartsWith("needs kiln.skills", AssertOffline(name).ReasonText);
    }

    [Fact]
    public void The_game_tree_throws_unavailable_naming_the_service()
    {
        AssertUnavailable("kiln.skills", () => GameTree.Dump(false));
        AssertUnavailable("kiln.skills", () => GameTree.Resource(0, out _));
    }

    [Fact]
    public void A_talent_result_is_a_kiln_result_and_keeps_its_own_reason_text()
    {
        var ok = new TalentResult(true, "", 2, default);
        Assert.Equal("", ok.Reason);
        Assert.Null(((IKilnResult)ok).Reason);
        var no = new TalentResult(false, "not enough points", 0, default);
        Assert.Equal("not enough points", ((IKilnResult)no).Reason);
        Assert.False(((IKilnResult)no).Ok);
    }

    [Fact]
    public void Members_carry_their_thread_and_capability()
    {
        AssertHas<AnyThreadAttribute>(Property(typeof(GameTree), "Available"));
        AssertHas<TickThreadOnlyAttribute>(Method(typeof(GameTree), "Dump", typeof(bool)));
        AssertRequires(Method(typeof(GameTree), "Dump", typeof(bool)), "kiln.skills");
    }
}
