// SPDX-License-Identifier: MIT
// ToolBlock.WearPerDebit with no game: what is refused as written, that nothing of it is content, and the intent's life
// (ToolWearRuntime): set once where a server world may run, tried again while the world or the item is not there, silent
// on a game that only joined, the block's error where the game or the server cannot do it, gone with the mod.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("tool wear intents")]
public sealed class ToolWearBlockTests : IDisposable
{
    private const string Mod = "wear-test";
    private readonly Func<bool> _server = ToolWearRuntime.ServerHere;
    private readonly Func<bool> _heldWas = ToolWearRuntime.Held;
    private bool _held;
    private readonly Func<ModBlocks, uint, int, ToolWearResult> _set = ToolWearRuntime.Set;
    private readonly Action<ToolWearRule> _remove = ToolWearRuntime.Remove;
    private readonly Func<Action<ulong>, IDisposable?> _tick = ToolWearRuntime.AddTick;
    private readonly List<(string Mod, uint Item, int Cost)> _sets = new();
    private readonly List<ToolWearRule> _removed = new();
    private Func<ToolWearResult> _answer = () => new ToolWearResult { Ok = true };
    private int _ticks;

    public ToolWearBlockTests()
    {
        ToolWearRuntime.Reset();
        ToolWearRuntime.ServerHere = () => true;
        ToolWearRuntime.Held = () => _held;
        ToolWearRuntime.Set = (mod, item, cost) => { _sets.Add((mod.Id, item, cost)); return _answer(); };
        ToolWearRuntime.Remove = _removed.Add;
        ToolWearRuntime.AddTick = _ => { _ticks++; return new NoTick(); };
    }

    public void Dispose()
    {
        ToolWearRuntime.Reset();
        (ToolWearRuntime.ServerHere, ToolWearRuntime.Set, ToolWearRuntime.Remove, ToolWearRuntime.AddTick) = (_server, _set, _remove, _tick);
        ToolWearRuntime.Held = _heldWas;
    }

    private sealed class NoTick : IDisposable
    {
        public void Dispose() { }
    }

    private static ModBlocks NewMod() => new(Mod, 9) { ManifestClients = "mod" };
    private static ItemBlock Pick(int? wear, ToolClass? cls = null) => new() { Name = "Pick", Tool = new ToolBlock { Harvests = cls ?? ToolClass.Pickaxe, WearPerDebit = wear } };
    private static JsonObject Lower(ItemBlock b) => Compiler.Lower(NewMod(), "pick", b).Low.Json;

    [Fact]
    public void The_cost_is_checked_as_written_and_is_no_content()
    {
        Assert.Equal(7, (int)Lower(Pick(7))["toolWear"]!["perDebit"]!);
        Assert.Equal(0, (int)Lower(Pick(0))["toolWear"]!["perDebit"]!);
        Assert.Null(Lower(Pick(null))["toolWear"]);
        Assert.Equal("tool.wearPerDebit", Assert.Throws<BlockError>(() => Lower(Pick(-1))).Field);
        Assert.Equal("tool.wearPerDebit", Assert.Throws<BlockError>(() => Lower(Pick(32768))).Field);
        // a torch burns down by time: durability, not a debit
        var e = Assert.Throws<BlockError>(() => Lower(Pick(3, ToolClass.Torch)));
        Assert.Equal("tool.wearPerDebit", e.Field);
        // the items, recipes and clones a block with a cost lowers to are those of the same block without it
        var with = Lower(Pick(7));
        var without = Lower(Pick(null));
        with.Remove("toolWear");
        Assert.Equal(ItemCompileGoldenTests.Text(without), ItemCompileGoldenTests.Text(with));
        // lowering alone asks nothing of the server: the intent is added when the block commits
        Assert.Empty(_sets);
        Assert.Equal(0, ToolWearRuntime.Waiting);
        Assert.Equal(JsonSerializer(Pick(7)), JsonSerializer(BlockJson.Parse<ItemBlock>("""{ "name": "Pick", "tool": { "harvests": "stone", "wearPerDebit": 7 } }""")));
    }

    private static string JsonSerializer(ItemBlock b) => ItemCompileGoldenTests.Text(Lower(b));

    [Fact]
    public void An_intent_is_set_once_where_a_server_may_run()
    {
        var mod = NewMod();
        ToolWearRuntime.Add(mod, "pick", 0x1234, 7);
        Assert.Equal(1, _ticks);
        ToolWearRuntime.Step(2);                 // not its step
        Assert.Empty(_sets);
        ToolWearRuntime.Step(26);
        Assert.Equal([(Mod, 0x1234u, 7)], _sets);
        ToolWearRuntime.Step(51);
        Assert.Single(_sets);                    // done: not set again
        Assert.Equal(0, ToolWearRuntime.Waiting);
        Assert.Empty(mod.Errors);
        ToolWearRuntime.Add(mod, "axe", 0x5678, 3);
        Assert.Equal(1, _ticks);                 // one tick for all
    }

    [Theory]
    [InlineData("NoAuthoritativeWorld")]
    [InlineData("PendingContent")]
    [InlineData("UnknownItem")]
    public void What_is_not_there_yet_is_tried_again_and_is_no_error(string reason)
    {
        var mod = NewMod();
        _answer = () => new ToolWearResult { Reason = reason };
        ToolWearRuntime.Add(mod, "pick", 0x1234, 7);
        ToolWearRuntime.Step(1);
        ToolWearRuntime.Step(26);
        Assert.Equal(2, _sets.Count);
        Assert.Equal(1, ToolWearRuntime.Waiting);
        Assert.Empty(mod.Errors);
        _answer = () => new ToolWearResult { Ok = true };
        ToolWearRuntime.Step(51);
        Assert.Equal(0, ToolWearRuntime.Waiting);
    }

    [Fact]
    public void A_game_that_only_joined_sets_nothing_and_says_nothing()
    {
        var mod = NewMod();
        ToolWearRuntime.ServerHere = () => false;
        ToolWearRuntime.Add(mod, "pick", 0x1234, 7);
        for (ulong s = 1; s < 200; s += 25) ToolWearRuntime.Step(s);
        Assert.Empty(_sets);
        Assert.Empty(mod.Errors);
        // it starts hosting later: the same intent is set then
        ToolWearRuntime.ServerHere = () => true;
        ToolWearRuntime.Step(201);
        Assert.Single(_sets);
    }

    [Fact]
    public void A_hosted_game_whose_row_is_held_waits_without_an_error_and_sets_the_rule_once_the_gate_lifts()
    {
        var mod = NewMod();
        _held = true;
        ToolWearRuntime.Add(mod, "pick", 0x1234, 7);
        for (ulong s = 1; s < 200; s += 25) ToolWearRuntime.Step(s);
        Assert.Empty(_sets);                     // the setter would throw: it is not asked
        Assert.Empty(mod.Errors);
        Assert.Equal(1, ToolWearRuntime.Waiting);
        _held = false;
        ToolWearRuntime.Step(201);
        Assert.Single(_sets);
        Assert.Equal(0, ToolWearRuntime.Waiting);
    }

    [Fact]
    public void An_item_the_game_never_gets_is_said_once_as_a_warning_and_still_tried()
    {
        var mod = NewMod();
        _answer = () => new ToolWearResult { Reason = "UnknownItem" };
        ToolWearRuntime.Add(mod, "pick", 0x1234, 7);
        for (ulong s = 1; s < 25 * 60; s += 25) ToolWearRuntime.Step(s);
        Assert.Single(mod.Warnings);
        Assert.Empty(mod.Errors);
        Assert.Equal(60, _sets.Count);
        Assert.Equal(1, ToolWearRuntime.Waiting);
    }

    [Fact]
    public void What_the_game_or_the_server_cannot_do_is_the_blocks_error_once()
    {
        var mod = NewMod();
        _answer = () => new ToolWearResult { Reason = "UnsupportedTool: Rake is not a tool with an item entity" };
        ToolWearRuntime.Add(mod, "rake", 0x1, 7);
        ToolWearRuntime.Step(1);
        ToolWearRuntime.Step(26);
        Assert.Single(_sets);
        Assert.Equal("block rake, tool.wearPerDebit: UnsupportedTool: Rake is not a tool with an item entity", Assert.Single(mod.Errors));
        _answer = () => throw new KilnUnavailableException("items.tool_wear_debit", "this game build has no approved description");
        ToolWearRuntime.Add(mod, "pick", 0x2, 7);
        ToolWearRuntime.Step(51);
        ToolWearRuntime.Step(76);
        Assert.Equal(2, mod.Errors.Count);
        Assert.Contains("items.tool_wear_debit", mod.Errors[1]);
        Assert.Equal(2, _sets.Count);
    }

    [Fact]
    public void A_mods_rules_go_with_it_and_another_mods_stay()
    {
        var (a, b) = (NewMod(), new ModBlocks("other", 10));
        var rule = (ToolWearRule)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ToolWearRule));
        _answer = () => new ToolWearResult { Ok = true, Rule = rule };
        ToolWearRuntime.Add(a, "pick", 0x1, 7);
        ToolWearRuntime.Add(b, "pick", 0x2, 7);
        ToolWearRuntime.Add(a, "late", 0x3, 7);
        ToolWearRuntime.Step(1);
        Assert.Equal(3, _sets.Count);
        ToolWearRuntime.Forget(a);
        Assert.Equal(2, _removed.Count);
        ToolWearRuntime.Add(b, "more", 0x4, 1);
        _answer = () => new ToolWearResult { Reason = "PendingContent" };
        ToolWearRuntime.Step(26);
        Assert.Equal((b.Id, 0x4u, 1), _sets[^1]);    // only the other mod's intent is left to try
        Assert.Equal(4, _sets.Count);
    }

    [Fact]
    public void Exhausted_rule_capacity_is_a_permanent_block_error()
    {
        var mod = NewMod();
        _answer = () => new ToolWearResult { Reason = "TooManyRules" };
        ToolWearRuntime.Add(mod, "pick", 0x1234, 7);
        ToolWearRuntime.Step(1);
        ToolWearRuntime.Step(26);
        Assert.Equal("block pick, tool.wearPerDebit: TooManyRules", Assert.Single(mod.Errors));
        Assert.Single(_sets);
        Assert.Equal(0, ToolWearRuntime.Waiting);
        Assert.True(mod.ReportDirty);
    }
}
