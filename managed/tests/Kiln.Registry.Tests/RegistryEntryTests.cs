// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registries;
using Xunit;
namespace Kiln.RegistryTests;
public sealed class RegistryEntryTests
{
    private static RegistryContext Frame(RegistryPhase phase, RegistryEntryPoint point = RegistryEntryPoint.GameStart, bool readOnly = false)
        => new(HostAccess.CreateContext(new ModInfo("test", "Test", "1", "."), 77, "."), point, phase, null, "entries",
            (_, _, field) => field is "inputs" or "outputs" ? "[{\"item\":\"game:wood\",\"count\":1}]" : field == "perks" ? "[]" : "10", (_, _) => ["game:wood"]) { ReadOnly = readOnly };
    [Fact]
    public void AddLikeBuffersOneStableDefinitionAndProjectsItsOwnTypedFields()
    {
        var context = Frame(RegistryPhase.Add); using var scope = context.Enter();
        var view = DataRegistry.For(context); ItemEntry? retained = null;
        view.Add(stage => stage.Items.AddLike(new("test:logs"), new("game:wood"), item => { retained = item; Assert.Equal(10, item.MaxStack); item.MaxStack = 500; }));
        var ir = JsonNode.Parse(context.CaptureIr()!)!;
        Assert.Equal("add", ir["stage"]!.GetValue<string>()); Assert.Single(ir["edits"]!.AsArray());
        Assert.Equal("test:logs", ir["edits"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("game:wood", ir["edits"]![0]!["like"]!.GetValue<string>());
        Assert.Equal(500, ir["edits"]![0]!["fields"]!["maxStack"]!.GetValue<int>());
        context.Close(); Assert.Equal(RegistryContextError.Closed, Assert.Throws<RegistryContextException>(() => retained!.MaxStack).Code);
    }
    [Fact]
    public void AddConfigureListsAreFoldedIntoTheDefinitionAndFaultsPublishNothing()
    {
        var context = Frame(RegistryPhase.Add); using var scope = context.Enter(); var view = DataRegistry.For(context);
        Assert.Throws<InvalidOperationException>(() => view.Add(stage => stage.Items.AddLike(new("test:fault"), new("game:wood"), _ => throw new InvalidOperationException("fault"))));
        Assert.Null(context.CaptureIr());
        view.Add(stage => stage.Recipes.AddLike(new("test:recipe"), new("game:recipe"), recipe => {
            recipe.Inputs.Clear(); var input = recipe.Inputs.AppendNew(); input.Item = new RegistryId("game:wood"); input.Count = 2;
        }));
        var ir = JsonNode.Parse(context.CaptureIr()!)!; Assert.Single(ir["edits"]!.AsArray());
        Assert.Equal(2, ir["edits"]![0]!["fields"]!["inputs"]![0]!["count"]!.GetValue<int>()); context.Close();
    }
    [Fact]
    public void AddNamespacesAndReadOnlyContextsFailBeforeRecording()
    {
        var context = Frame(RegistryPhase.Add); using var scope = context.Enter(); var view = DataRegistry.For(context);
        Assert.Throws<ArgumentException>(() => view.Add(stage => stage.Items.AddLike(new("other:logs"), new("game:wood"), _ => { })));
        Assert.Null(context.CaptureIr()); context.Close();
        var readOnly = Frame(RegistryPhase.Add, readOnly: true); using var readScope = readOnly.Enter();
        var resolved = DataRegistry.For(readOnly);
        Assert.Equal(RegistryContextError.ReadOnly, Assert.Throws<RegistryContextException>(() => resolved.Add(stage => stage.Items.AddLike(new("test:logs"), new("game:wood"), _ => { }))).Code);
        Assert.Null(readOnly.CaptureIr()); readOnly.Close();
    }
    [Fact]
    public void LocalCloneCyclesFailBeforeConfigureCanRecursivelyReadThem()
    {
        var context = Frame(RegistryPhase.Add); using var scope = context.Enter(); var view = DataRegistry.For(context);
        view.Add(stage => stage.Items.AddLike(new("test:a"), new("test:b"), _ => { }));
        var configured = false;
        Assert.Throws<ArgumentException>(() => view.Add(stage => stage.Items.AddLike(new("test:b"), new("test:a"), _ => configured = true)));
        Assert.False(configured); Assert.Single(JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray()); context.Close();
    }
    [Fact]
    public void RemoveUsesHiddenEntryIrAndFinalKeepsItsPhase()
    {
        var context = Frame(RegistryPhase.Final); using var scope = context.Enter(); var view = DataRegistry.For(context);
        view.Final(stage => { stage.Items.Remove("game:wood"); stage.Recipes.Remove("game:recipe"); stage.Loot.Remove("game:wood"); });
        var ir = JsonNode.Parse(context.CaptureIr()!)!; Assert.Equal("final", ir["stage"]!.GetValue<string>());
        Assert.Equal(3, ir["edits"]!.AsArray().Count);
        Assert.All(ir["edits"]!.AsArray(), edit => Assert.Equal("remove", edit!["entry"]!.GetValue<string>())); context.Close();
    }
    [Fact]
    public void LaterPointsRecordEntryIntentSoNativeCanReturnRestartRequired()
    {
        var context = Frame(RegistryPhase.Alter, RegistryEntryPoint.Manual); using var scope = context.Enter(); var view = DataRegistry.For(context);
        view.Add(stage => stage.Items.AddLike(new("test:logs"), new("game:wood"), item => item.MaxStack = 50));
        view.Items.Remove("game:wood");
        Assert.Equal(2, JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray().Count); context.Close();
    }
    // dev.32: the buffs and abilities registries through the stage: typed entries, a time as seconds, an enum as its Kiln name,
    // read-only fields without a setter.
    [Fact]
    public void BuffsAndAbilitiesAreTypedRegistriesOfTheStage()
    {
        var context = new RegistryContext(HostAccess.CreateContext(new ModInfo("test", "Test", "1", "."), 77, "."), RegistryEntryPoint.GameStart,
            RegistryPhase.Alter, null, "entries", (_, _, field) => field switch
            {
                "duration" => "10.0", "stacking" => "\"refresh\"", "kind" => "\"debuff\"", "saved" => "false", _ => "0.65",
            }, (_, _) => ["game:buff_5f425faf"]);
        using var scope = context.Enter();
        var view = DataRegistry.For(context);
        view.Alter(stage =>
        {
            var poison = stage.Buffs["game:buff_5f425faf"];
            Assert.Equal(TimeSpan.FromSeconds(10), poison.Duration);
            Assert.Equal(BuffStacking.Refresh, poison.Stacking);
            Assert.Equal(BuffKind.Debuff, poison.Kind);
            Assert.False(poison.Saved);
            poison.Duration = TimeSpan.FromSeconds(30);
            poison.Stacking = BuffStacking.Replace;
            var cast = stage.Abilities["game:attack_player_staff_fixedanim_fireball_fast"];
            Assert.Equal(TimeSpan.FromSeconds(0.65), cast.Timing.Release);
            cast.Timing.Release = TimeSpan.FromSeconds(0.45);
        });
        var edits = JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray();
        Assert.Equal(3, edits.Count);
        Assert.Equal(("buffs", "duration", 30.0), (edits[0]!["registry"]!.GetValue<string>(), edits[0]!["field"]!.GetValue<string>(), edits[0]!["op"]!.GetValue<double>()));
        Assert.Equal("replace", edits[1]!["op"]!.GetValue<string>());
        Assert.Equal(("abilities", "timing.release", 0.45), (edits[2]!["registry"]!.GetValue<string>(), edits[2]!["field"]!.GetValue<string>(), edits[2]!["op"]!.GetValue<double>()));
        Assert.Null(typeof(BuffEntry).GetProperty(nameof(BuffEntry.Kind))!.SetMethod);
        Assert.Null(typeof(BuffEntry).GetProperty(nameof(BuffEntry.Saved))!.SetMethod);
        context.Close();
    }
}
