// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Content by the contract.</summary>
public sealed class ContentContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Content).Assembly);
        Assert.False(Content.Available);
        Assert.False(Content.Ready);
        Assert.False(Content.GameDataAvailable);
        Assert.False(NewContent.Available);
        AssertOffline("kiln.content");
        AssertOffline("kiln.gamedata");
        foreach (var name in (string[])["content.items", "content.recipes", "content.new", "content.references", "content.ready"])
            AssertOffOffline(name, "needs kiln.content");
        AssertOffOffline("gamedata.template_clones", "needs kiln.gamedata");
        Assert.False(Capabilities.Get("content.ready").SessionDependent);
    }

    [Fact]
    public void A_call_that_needs_a_service_throws_unavailable_naming_it()
    {
        AssertUnavailable("kiln.content", () => Content.Items.Get("Material_T1_Wood_Logs"));
        AssertUnavailable("kiln.content", () => Content.Revert());
        AssertUnavailable("kiln.content", () => Content.ReloadPacks());
        AssertUnavailable("kiln.content", () => Content.Dump());
        AssertUnavailable("kiln.content", () => Content.PackReport());
        // a registration is a mod's: "from mod code" is checked before the service, as before
        Assert.Throws<InvalidOperationException>(() => NewContent.RegisterJson(ContentKind.Items, "{}"));
        AssertUnavailable("kiln.gamedata", () => Content.ConfigVocabulary());
    }

    [Fact]
    public void A_change_made_from_code_posts_one_event_and_builds_none_without_a_subscriber()
    {
        Content.Changed(ContentChange.Reverted, null, "");  // nobody listens: nothing is built or posted
        var seen = new List<ContentChangedEvent>();
        using var sub = Events.On<ContentChangedEvent>(seen.Add);
        Content.Changed(ContentChange.Applied, null, "mod bigstacks: stacks.json");
        Content.Changed(ContentChange.Reverted, null, "");
        Assert.Equal(2, seen.Count);
        Assert.Equal(ContentChange.Applied, seen[0].Change);
        Assert.Null(seen[0].Kind);
        Assert.Equal(0u, seen[0].Id);
        Assert.Equal("mod bigstacks: stacks.json", seen[0].Path);
        Assert.Equal("", seen[0].Origin);
        Assert.Equal(ContentChange.Reverted, seen[1].Change);
        Assert.Contains("Applied", seen[0].ToString());
    }

    [Fact]
    public void Members_carry_their_thread_and_capability()
    {
        var get = Method(typeof(ContentRegistry), "Get", typeof(string));
        AssertHas<TickThreadOnlyAttribute>(get);
        AssertRequires(get, "kiln.content");
        AssertRequires(Method(typeof(ContentEntry), "SetRef", typeof(string), typeof(string), typeof(bool)), "content.references");
        AssertRequires(Method(typeof(NewContent), "RegisterJson", typeof(ContentKind), typeof(string)), "content.new");
        AssertRequires(Method(typeof(GameDataRegistry), "Define", typeof(GameDataClone)), "gamedata.template_clones");
        AssertRequires(Method(typeof(GameDataEntry), "Get", typeof(string)), "kiln.gamedata");
        AssertHas<AnyThreadAttribute>(Property(typeof(Content), "Ready"));
        AssertRequires(Property(typeof(Content), "Available"));
    }
}

/// <summary>Kiln.Creatures by the contract.</summary>
public sealed class CreaturesContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Creatures).Assembly);
        Assert.False(Creatures.Available);
        Assert.Null(Creatures.Status);
        AssertOffline("kiln.creatures");
        foreach (var name in (string[])["creatures.ai", "creatures.arsenals", "creatures.attacks", "creatures.rules", "creatures.commands"])
            AssertOffOffline(name, "needs kiln.creatures");
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        var creature = Creatures.Of(new EntityId(5000));
        AssertUnavailable("kiln.creatures", () => creature.Inspect());
        AssertUnavailable("kiln.creatures", () => creature.Level = 5);
        AssertUnavailable("kiln.creatures", () => _ = creature.AiEnabled);
        AssertUnavailable("kiln.creatures", () => creature.Reset());
        AssertUnavailable("kiln.creatures", () => creature.Ai.Set("aggroSetup.viewRange", 12));
    }

    [Fact]
    public void A_change_of_a_creature_posts_one_event_and_builds_none_without_a_subscriber()
    {
        Creatures.Changed(new EntityId(5000), "Level");  // nobody listens
        var seen = new List<CreatureChangedEvent>();
        using var sub = Events.On<CreatureChangedEvent>(seen.Add);
        Creatures.Changed(new EntityId(5000), "Level");
        Creatures.Changed(new EntityId(5001), "Ai.Aggro.viewRange");
        Assert.Equal(2, seen.Count);
        Assert.Equal(new EntityId(5000), seen[0].Entity);
        Assert.Equal("Level", seen[0].What);
        Assert.Equal("", seen[0].Origin);
        Assert.Equal("Ai.Aggro.viewRange", seen[1].What);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Creatures));
        AssertHas<ServerOnlyAttribute>(typeof(Arsenals));
        AssertHas<ServerOnlyAttribute>(typeof(Villagers));
        AssertHas<AnyThreadAttribute>(Property(typeof(Creatures), "Status"));
        var level = Property(typeof(CreatureRef), "Level");
        AssertHas<TickThreadOnlyAttribute>(level);
        AssertRequires(level, "kiln.creatures");
        AssertRequires(Method(typeof(Arsenals), "Define", typeof(string), typeof(ArsenalSpec)), "creatures.arsenals");
        AssertRequires(Property(typeof(Creatures), "Available"));
    }
}
