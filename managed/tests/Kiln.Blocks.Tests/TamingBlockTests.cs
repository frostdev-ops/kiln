// SPDX-License-Identifier: MIT
// TamingBlock (dev.32 TAME review, coordinator decision 2): a typed front-end in Kiln.Blocks that compiles to Kiln.Taming's
// TamingDefinition, so JSON names a mod's own items (and creature and pet blocks) as every block does. C# and blocks/*.json
// lower to the same definition; every field is refused by its path; items not readable while compiling are checked once
// content is ready, as a pet's revive cost.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class TamingBlockTests : IDisposable
{
    private static readonly (Func<bool> Tick, Func<bool> Client, Func<bool> Running) Real = (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning);
    private readonly Func<string, (ItemId Id, bool Gear)?> _find = PetCompiler.FindItem;
    private readonly Action<TamingDefinition, string> _register = TamingCompiler.Register;
    private readonly Func<bool> _readable = TamingCompiler.ItemsReadable;
    private readonly List<(TamingDefinition Def, string Mod)> _defined = new();
    private readonly Dictionary<string, (ItemId, bool)> _items = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Food_T2_Dog_Bait"] = (new ItemId(0xb59dfc2c), false),
        ["Tool_T1_Whistle"] = (new ItemId(0x1234), true),
    };

    public TamingBlockTests()
    {
        NpcServerSide.OnTickThread = () => false;
        NpcServerSide.IsClient = () => true;
        NpcServerSide.ServerRunning = () => false;
        PetCompiler.FindItem = n => _items.TryGetValue(n, out var i) ? i : null;
        TamingCompiler.Register = (d, mod) => _defined.Add((d, mod));
        TamingCompiler.ItemsReadable = () => true;
    }

    public void Dispose()
    {
        (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning) = Real;
        PetCompiler.FindItem = _find;
        TamingCompiler.Register = _register;
        TamingCompiler.ItemsReadable = _readable;
    }

    private static ModBlocks Mod(string id, string clients = "none") => new(id, 1) { ManifestClients = clients };

    private static (BlockHandle Handle, JsonObject Json, Lowered Low) Lower(ModBlocks mod, string name, TamingBlock b)
    {
        var (h, low, _) = Compiler.Lower(mod, name, b);
        return (h, (JsonObject)low.Json["taming"]!, low);
    }

    private static TamingBlock Wolf() => new()
    {
        Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
        Steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed("Food_T2_Dog_Bait", 2), TamingStepBlock.Tool("Tool_T1_Whistle", 3)],
        Refund = TamingRefundPolicy.Escrow, InactivitySeconds = 60, MaxAttemptSeconds = 600,
    };

    [Fact]
    public void A_taming_block_compiles_to_the_definition_Taming_Define_takes()
    {
        var mod = Mod("tame-a");
        var (h, json, low) = Lower(mod, "wolf", Wolf());
        Assert.Equal("tame-a:wolf", h.GameName);
        low.CommitAfter();
        var (d, owner) = Assert.Single(_defined);
        Assert.Equal("tame-a", owner);
        Assert.Equal("tame-a:wolf", d.Id);
        Assert.Equal(["game:enemy_wildbeast_wolf_earlygame"], d.Creatures);
        Assert.Equal(new TamingStep[] { new WeakenStep(0.4), new FeedStep(new ItemId(0xb59dfc2c), 2), new UseToolStep(new ItemId(0x1234), 3) }, d.Steps);
        Assert.Equal((TamingRefundPolicy.Escrow, 60.0, 600.0, (string?)null), (d.Refund, d.InactivitySeconds, d.MaxAttemptSeconds, d.Pet));
        Assert.Equal("escrow", json["refund"]!.GetValue<string>());
        Assert.Equal("Food_T2_Dog_Bait", json["steps"]![1]!["item"]!.GetValue<string>());
    }

    [Fact]
    public void Csharp_and_json_lower_to_the_same_definition()
    {
        var cs = Lower(Mod("tame-b"), "wolf", Wolf()).Json;
        var block = BlockJson.Parse<TamingBlock>("""
            {
              "creatures": ["game:enemy_wildbeast_wolf_earlygame"],
              "steps": [ { "kind": "weaken", "maxHealthFraction": 0.4 },
                         { "kind": "feed", "item": "Food_T2_Dog_Bait", "count": 2 },
                         { "kind": "tool", "item": "Tool_T1_Whistle", "wear": 3, "allowBreak": false } ],
              "refund": "escrow", "inactivitySeconds": 60, "maxAttemptSeconds": 600
            }
            """);
        Assert.Equal(cs.ToJsonString(), Lower(Mod("tame-b"), "wolf", block).Json.ToJsonString());
    }

    [Fact]
    public void Json_names_the_mod_s_own_items_creatures_and_pet()
    {
        var mod = Mod("tame-c");
        var bait = mod.Handles["wolf-bait"] = new BlockHandle<ItemBlock>("tame-c", "wolf-bait", Guid.NewGuid(), "tame-c.wolf-bait", new ItemBlock { Name = "Wolf Bait", Stack = 20 });
        _items[bait.GameName] = (new ItemId(0xdeadbeef), false);
        Compiler.Define(mod, "pup", new CreatureBlock { Body = Bodies.Vukah });
        mod.Handles["tame-pup"] = new BlockHandle<PetBlock>("tame-c", "tame-pup", Guid.NewGuid(), "tame-c:tame-pup", new PetBlock { Creature = "pup" });
        var block = BlockJson.Parse<TamingBlock>("""
            { "creatures": ["pup", "game:animal_wildlife_dog_*"], "pet": "tame-pup",
              "steps": [ { "kind": "feed", "item": "wolf-bait" }, { "kind": "native", "steps": 2 } ] }
            """);
        var (_, json, low) = Lower(mod, "pup-taming", block);
        low.CommitAfter();
        var d = Assert.Single(_defined).Def;
        Assert.Equal(["tame-c:pup", "game:animal_wildlife_dog_*"], d.Creatures);
        Assert.Equal("tame-c:tame-pup", d.Pet);
        Assert.Equal(new ItemId(0xdeadbeef), ((FeedStep)d.Steps[0]).Item);
        Assert.Equal(new NativeStep(2), d.Steps[1]);
        Assert.Equal(bait.GameName, json["steps"]![0]!["item"]!.GetValue<string>());
    }

    // API-SHAPE-2 A13: a taming names its creatures as a pet names its creature (PartRef<CreatureBlock>): a creatures
    // registry id, a creature block's handle or name, or one written in place
    [Fact]
    public void Creatures_are_creature_references_as_a_pets_creature_is()
    {
        var mod = Mod("tame-f");
        var pup = Compiler.Define(mod, "pup", new CreatureBlock { Body = Bodies.Vukah });
        var block = Wolf() with
        {
            Creatures = ["game:enemy_wildbeast_wolf_earlygame", (BlockHandle<CreatureBlock>)pup, "pup", new CreatureBlock { Body = Bodies.Vukah }, "game:animal_wildlife_dog_*"],
        };
        Assert.Equal(typeof(IReadOnlyList<PartRef<CreatureBlock>>), typeof(TamingBlock).GetProperty(nameof(TamingBlock.Creatures))!.PropertyType);
        Assert.Equal(typeof(PartRef<CreatureBlock>), typeof(PetBlock).GetProperty(nameof(PetBlock.Creature))!.PropertyType);
        Lower(mod, "wolf", block).Low.CommitAfter();
        var d = Assert.Single(_defined).Def;
        Assert.Equal("game:enemy_wildbeast_wolf_earlygame", d.Creatures[0]);
        Assert.Equal(["tame-f:pup", "tame-f:pup"], d.Creatures.Skip(1).Take(2));
        Assert.StartsWith("tame-f:", d.Creatures[3]);   // the inline creature, compiled as the taming's own
        Assert.Equal("game:animal_wildlife_dog_*", d.Creatures[4]);
    }

    [Theory]
    [InlineData("creatures")]
    [InlineData("creatures[0]")]
    [InlineData("steps")]
    [InlineData("steps[0].kind")]
    [InlineData("steps[0].count")]
    [InlineData("steps[0].item")]
    [InlineData("steps[0].wear")]
    [InlineData("steps[1].maxHealthFraction")]
    [InlineData("steps[0].steps")]
    [InlineData("rangeMeters")]
    [InlineData("inactivitySeconds")]
    [InlineData("maxAttemptSeconds")]
    [InlineData("pet")]
    public void Wrong_values_are_refused_by_field(string field)
    {
        var ok = Wolf();
        var bad = field switch
        {
            "creatures" => ok with { Creatures = [] },
            "creatures[0]" => ok with { Creatures = ["wolf*"] },
            "steps" => ok with { Steps = [] },
            "steps[0].kind" => ok with { Steps = [new TamingStepBlock { Kind = "pet" }] },
            "steps[0].count" => ok with { Steps = [TamingStepBlock.Feed("Food_T2_Dog_Bait", 1000)] },
            "steps[0].item" => ok with { Steps = [TamingStepBlock.Feed("Tool_T1_Whistle")] },   // gear fed
            "steps[0].wear" => ok with { Steps = [TamingStepBlock.Feed("Food_T2_Dog_Bait") with { Wear = 2 }] },   // a tool's field
            "steps[1].maxHealthFraction" => ok with { Steps = [TamingStepBlock.Native(), TamingStepBlock.Weaken(0)] },
            "steps[0].steps" => ok with { Steps = [TamingStepBlock.Native(256)] },
            "rangeMeters" => ok with { RangeMeters = 9 },
            "inactivitySeconds" => ok with { InactivitySeconds = 0 },
            "maxAttemptSeconds" => ok with { InactivitySeconds = 60, MaxAttemptSeconds = 30 },
            "pet" => ok with { Pet = "nowhere" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var e = Assert.Throws<BlockError>(() => Compiler.Lower(Mod("tame-d"), "t", bad));
        Assert.Equal(field, e.Field);
    }

    [Fact]
    public void An_item_not_readable_while_compiling_is_checked_once_content_is_ready()
    {
        var block = Wolf() with { Steps = [TamingStepBlock.Feed("Food_T9_Unknown_Bait")] };
        var missing = Mod("tame-e");
        Compiler.Lower(missing, "t", block).Low.CommitAfter();
        Assert.Empty(_defined);                                    // nothing defined while the item is not known
        var problems = new List<string>();
        foreach (var check in missing.ReadyChecks.ToList()) check(problems.Add);
        Assert.Contains("steps[0].item: the game has no item \"Food_T9_Unknown_Bait\"", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Empty(_defined);                                    // never defined: a Begin of it is DefinitionUnknown

        var late = Mod("tame-f");
        Compiler.Lower(late, "t", block).Low.CommitAfter();
        _items["Food_T9_Unknown_Bait"] = (new ItemId(99), false);
        problems.Clear();
        foreach (var check in late.ReadyChecks.ToList()) check(problems.Add);
        Assert.Empty(problems);
        Assert.Equal(new ItemId(99), ((FeedStep)Assert.Single(_defined).Def.Steps[0]).Item);
    }

    // lab 11-27 (review bot lab bot1): on a dedicated server content is ready before Kiln.Items reads the game's item
    // catalog, so a block taming's game item was reported "the game has no item" at content ready, and the taming was never
    // defined, while the same item was readable 20 s later
    [Fact]
    public void An_item_catalog_not_readable_at_content_ready_is_waited_for()
    {
        var block = Wolf() with { Steps = [TamingStepBlock.Feed("Food_T9_Late_Bait")] };
        var mod = Mod("tame-i");
        Compiler.Lower(mod, "t", block).Low.CommitAfter();
        TamingCompiler.ItemsReadable = () => false;               // content ready, the catalog not read yet
        var problems = new List<string>();
        foreach (var check in mod.ReadyChecks.ToList()) check(problems.Add);
        Assert.Empty(problems);                                   // not called missing
        Assert.Empty(_defined);
        TamingCompiler.Pump();                                    // still not readable: still waiting
        Assert.Empty(_defined);
        _items["Food_T9_Late_Bait"] = (new ItemId(123), false);
        TamingCompiler.ItemsReadable = () => true;
        TamingCompiler.Pump();
        Assert.Empty(problems);
        Assert.Equal(new ItemId(123), ((FeedStep)Assert.Single(_defined).Def.Steps[0]).Item);
    }

    [Fact]
    public void A_pet_another_mod_defined_in_code_is_named_by_its_id()
    {
        var (_, json, low) = Lower(Mod("tame-h"), "t", Wolf() with { Pet = "other-mod:hound" });
        low.CommitAfter();
        Assert.Equal("other-mod:hound", Assert.Single(_defined).Def.Pet);
        Assert.Equal("other-mod:hound", json["pet"]!.GetValue<string>());
    }

    [Fact]
    public void A_renamed_taming_keeps_its_id_and_asks_nothing_of_players_games()
    {
        var (h, json, _) = Lower(Mod("tame-g"), "companion", Wolf() with { Was = "wolf" });
        Assert.Equal("tame-g:wolf", json["id"]!.GetValue<string>());
        Assert.Equal("tame-g:wolf", h.GameName);
    }
}
