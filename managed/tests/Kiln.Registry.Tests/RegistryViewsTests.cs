// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Registry;
using Xunit;

namespace Kiln.RegistryTests;

/// <summary>kiln.registry.view as the native table answers it: views, fields, reads, selectors and entries, with refusals
/// carrying their reason in the buffer.</summary>
internal sealed class FakeViews : RegistryViews.IBackend
{
    public readonly Dictionary<string, (int Rc, string Text)> Reads = new(StringComparer.Ordinal);
    public int Calls;
    public int Views(out string text)
    {
        Calls++;
        text = """{"format":1,"views":[{"name":"items","tier":"stable","fields":38,"registry":"items"},{"name":"creatures","tier":"stable","fields":1,"registry":"creatures"},{"name":"quest","tier":"advanced","fields":5}]}""";
        return 0;
    }
    public int Fields(string view, out string text)
    {
        Calls++;
        if (view != "quest") { text = "there is no view " + view + " on this game build"; return RegistryViews.NotFound; }
        text = """
            {"view":"quest","tier":"advanced","fields":[
            {"path":"xpReward","type":"int","readOnly":true,"state":"readable"},
            {"path":"kind","type":"enum","values":["main","sideQuest"],"readOnly":true,"state":"readable"},
            {"path":"rewardItems","type":"list","element":"struct","readOnly":true,"state":"readable"},
            {"path":"rewardItems[].item","type":"id","ref":"itemInfo","readOnly":true,"state":"readable"},
            {"path":"events","type":"list","element":"unread","readOnly":true,"state":"unread","reason":"a member that holds one of several kinds of value"},
            {"path":"maxStackSize","type":"int","readOnly":true,"state":"readable","coveredBy":"items.maxStack","exact":true}]}
            """;
        return 0;
    }
    public int Read(string view, string id, string field, out string text)
    {
        Calls++;
        if (Reads.TryGetValue($"{view}|{id}|{field}", out var r)) { text = r.Text; return r.Rc; }
        text = $"{view} has no field {field} on this game build";
        return RegistryViews.NotFound;
    }
    public int Select(string view, string selector, out string text)
    {
        Calls++;
        text = selector == "*" ? """["game:quest_main","game:quest_side"]""" : """["game:quest_side"]""";
        return 0;
    }
    public int Entry(string view, string id, out string text)
    {
        Calls++;
        text = """{"view":"quest","id":"game:quest_side","tier":"advanced","fields":{"xpReward":40,"kind":"sideQuest"},"refused":{"events":"quest.events: a member that holds one of several kinds of value"}}""";
        return 0;
    }
}

[Collection("registry views")]
public sealed class RegistryViewsTests : IDisposable
{
    private readonly FakeViews _fake = new();
    public RegistryViewsTests() => RegistryViews.Override = _fake;
    public void Dispose() => RegistryViews.Override = null;

    [Fact]
    public void ViewsListEveryRegistryAndResourceTypeWithTheirTier()
    {
        Assert.True(RegistryViews.Available);
        var all = RegistryViews.All;
        Assert.Equal(new[] { "items", "creatures", "quest" }, all.Select(v => v.Name));
        Assert.Equal(RegistryViewTier.Stable, RegistryViews.Find("creatures")!.Tier);
        Assert.Equal(RegistryViewTier.Advanced, RegistryViews.Find("quest")!.Tier);
        Assert.Null(RegistryViews.Find("nope"));
    }

    [Fact]
    public void FieldsCarryKindsStatesReasonsAndTheRegistryFieldThatCoversThem()
    {
        var quest = RegistryViews.Find("quest")!;
        Assert.Equal(6, quest.Fields.Count);
        var calls = _fake.Calls;
        Assert.Same(quest.Fields, quest.Fields);  // asked once
        Assert.Equal(calls, _fake.Calls);
        Assert.Equal(new[] { "main", "sideQuest" }, quest.Field("kind")!.Values);
        Assert.Equal("itemInfo", quest.Field("rewardItems[3].item")!.Ref);
        Assert.Equal(RegistryViewFieldType.Struct, quest.Field("rewardItems")!.Element);
        var events = quest.Field("events")!;
        Assert.False(events.Readable);
        Assert.Equal(RegistryViewFieldState.Unread, events.State);
        Assert.Contains("several kinds", events.Reason);
        var covered = quest.Field("maxStackSize")!;
        Assert.Equal("items.maxStack", covered.CoveredBy);
        Assert.True(covered.Exact);
        Assert.Null(quest.Field("nope"));
    }

    [Fact]
    public void ReadsAreTypedAndARefusalNamesItsReason()
    {
        _fake.Reads["quest|game:quest_main|xpReward"] = (0, "100");
        _fake.Reads["quest|game:quest_main|kind"] = (0, "\"main\"");
        _fake.Reads["quest|game:quest_main|duration"] = (0, "2.5");
        _fake.Reads["quest|game:quest_main|rewardItems[0].item"] = (0, "\"game:wood\"");
        _fake.Reads["quest|game:quest_main|tags"] = (0, "[\"combat\",\"gather\"]");
        _fake.Reads["quest|game:quest_side|bonus"] = (RegistryViews.Unavailable, "quest[game:quest_side].bonus: this entry has no value of it on this game build");
        var e = RegistryViews.Find("quest")!["game:quest_main"];
        Assert.Equal(100, e.Get<int>("xpReward"));
        Assert.Equal(100L, e.Get<long>("xpReward"));
        Assert.Equal(100.0, e.Get<double>("xpReward"));
        Assert.Equal("main", e.Get<string>("kind"));
        Assert.Equal(TimeSpan.FromSeconds(2.5), e.Get<TimeSpan>("duration"));
        Assert.Equal(new RegistryId("game:wood"), e.Get<RegistryId>("rewardItems[0].item"));
        Assert.Equal(new[] { "combat", "gather" }, e.Get<string[]>("tags"));
        Assert.Throws<InvalidCastException>(() => e.Get<int>("duration"));  // 2.5 is not a whole number
        var side = RegistryViews.Find("quest")!["game:quest_side"];
        Assert.False(side.TryGet<double>("bonus", out _, out var reason));
        Assert.Contains("game:quest_side", reason);
        var ex = Assert.Throws<KilnUnavailableException>(() => side.Get<double>("bonus"));
        Assert.Contains("this entry has no value", ex.Message);
        Assert.Throws<KeyNotFoundException>(() => side.Get<int>("nope"));
        Assert.False(RegistryViews.Find("quest")!.TryRead(new RegistryId("game:quest_side"), "nope", out _, out var why));
        Assert.Contains("quest has no field nope", why);
    }

    [Fact]
    public void SelectAndSnapshot()
    {
        var quest = RegistryViews.Find("quest")!;
        Assert.Equal(new[] { new RegistryId("game:quest_main"), new RegistryId("game:quest_side") }, quest.Select());
        Assert.Equal(new[] { new RegistryId("game:quest_side") }, quest.Select("game:quest_s*"));
        var snap = quest["game:quest_side"].Snapshot();
        Assert.Equal(40, snap.Fields["xpReward"]!.GetValue<int>());
        Assert.Contains("events", snap.Refused.Keys);
        Assert.DoesNotContain("events", snap.Fields.Keys);
    }

    [Fact]
    public void WithoutTheNativeTableTheViewsAreUnavailable()
    {
        RegistryViews.Override = null;  // the test host has no core: no table
        Assert.False(RegistryViews.Available);
        Assert.Throws<KilnUnavailableException>(() => RegistryViews.All);
    }
}

/// <summary>The generic entry over the generated catalog: every registry by name, its fields by Kiln path, typed and
/// checked the way the generated properties are, recording the same edits.</summary>
public sealed class RegistryGenericEntryTests
{
    private static int Id(string qualified) => RegistrySchema.Find(qualified)!.Id;

    [Fact]
    public void ReadsAndWritesTheSameCellsAsTheTypedEntry()
    {
        var cells = new FakeCells();
        cells.Values[(Id("items.maxStack"), -1)] = 20L;
        var generic = new RegistryEntry("items", new RegistryId("game:wood"), cells);
        var typed = new ItemEntry(new RegistryId("game:wood"), cells);
        Assert.Equal(20L, generic.Get("maxStack"));
        Assert.Equal(20, generic.Get<int>("maxStack"));
        generic.Set("maxStack", 500);
        Assert.Equal(500, typed.MaxStack);
        typed.MaxStack = 600;
        Assert.Equal(600L, generic.Get<long>("maxStack"));
        Assert.Equal(cells.Edits[0], cells.Edits[1].Replace("600", "500"));
    }

    [Fact]
    public void FieldsAreCheckedAgainstTheSchema()
    {
        var cells = new FakeCells();
        var e = new RegistryEntry("items", new RegistryId("game:wood"), cells);
        Assert.Throws<ArgumentException>(() => e.Set("nope", 1));
        Assert.Throws<ArgumentException>(() => e.Set("maxStack", 1.5));
        Assert.Throws<ArgumentException>(() => e.Set("maxStack", "many"));
        Assert.Throws<ArgumentException>(() => e.Set("rarity", "mythic"));
        e.Set("rarity", "epic");
        Assert.Throws<ArgumentException>(() => e.Set("hasDamage", true));  // read-only
        Assert.Throws<ArgumentException>(() => e.Set("perks", "x"));       // a list needs an index
        Assert.Throws<ArgumentException>(() => e.Set("maxStack[x]", 1));
        Assert.Equal("items.rarity", e.Spec("rarity").Field);
        Assert.Single(cells.Edits);
    }

    [Fact]
    public void ListElementsByIndex()
    {
        var cells = new FakeCells();
        int inputs = Id("recipes.inputs"), count = Id("recipes.inputs[].count");
        cells.Counts[inputs] = 2;
        cells.Values[(count, 1)] = 3L;
        var e = new RegistryEntry("recipes", new RegistryId("game:plank"), cells);
        Assert.Equal(2, e.Count("inputs"));
        Assert.Equal(3, e.Get<int>("inputs[1].count"));
        e.Set("inputs[1].count", 4);
        Assert.Equal(4L, cells.Values[(count, 1)]);
        Assert.Throws<ArgumentException>(() => e.Count("duration"));
    }
}
