// SPDX-License-Identifier: MIT
// A held model with moving parts (VisualBlock.Animated), with no game: what a look lowers to for a class held in the right hand, one held in the left, a
// shield and a pair of daggers, with and without a VFX; that C# and JSON say the same; what is refused; and the hand picked from rows read when content is
// ready (made up here: the left hand for a hammer, the arm for a shield, as the game's starters have them). Part B: an ability's cast refuses the cast moves.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("served seam")]
public sealed class AnimatedHeldTests : IDisposable
{
    private const string Mod = "held-test";
    private static readonly Func<Guid, string, bool> Real = Compiler.Served;

    // the cooked files are served here, unless a test says otherwise
    public AnimatedHeldTests() => Compiler.Served = (_, _) => true;
    public void Dispose() => Compiler.Served = Real;
    private const string Ember = "85e60392-52e2-4488-8653-2d539c18c80c";

    private static JsonObject Lower<T>(T b, string clients = "mod") where T : class, IBlock => Compiler.Lower(new ModBlocks(Mod, 1) { ManifestClients = clients }, "cinderbrand", b).Low.Json;

    private static WeaponBlock Weapon(WeaponClass cls, VisualBlock look) => new() { Name = "Cinderbrand", Class = cls, Visual = look };

    private static readonly AnimatedModel Moving = new() { Model = "cinderbrand_anim", Graph = "cinderbrand" };

    private static JsonObject Template(JsonObject j, string name) => j["templates"]!.AsArray().Single(t => (string)t!["name"]! == name)!.AsObject();

    [Fact]
    public void A_moving_look_makes_a_template_of_the_items_own_and_the_item_holds_it()
    {
        var j = Lower(Weapon(WeaponClass.Sword, new VisualBlock { Model = Guid.Empty.ToString(), Animated = Moving }));
        var t = Template(j, "held-test.cinderbrand.moving");
        Assert.Equal(BlockIds.Derive(Mod, "template", "cinderbrand.moving").ToString(), (string)t["guid"]!);
        Assert.Equal(Compiler.AnimatedStarter, (string)t["base"]!);
        Assert.Equal(["VfxParametersTemplateComponent"], t["removeComponents"]!.AsArray().Select(x => (string)x!));
        // the GUIDs the cook derives from the steps' names
        Assert.Equal(BlockIds.Derive(Mod, "model", "cinderbrand_anim").ToString(), (string)t["fields"]!["ModelResource.model"]!);
        Assert.Equal(BlockIds.Derive(Mod, "graph", "cinderbrand").ToString(), (string)t["fields"]!["AnimationGraphResourceComponent2.animationGraph"]!);
        // no VFX of the look: none, not the staff's own
        Assert.Equal("none", (string)t["fields"]!["VfxComponentResource.vfx"]!);
        Assert.Single(j["templates"]!.AsArray());                                      // and no rigid .held template beside it
        var item = j["items"]![0]!;
        Assert.Equal((string)t["guid"]!, (string)item["refs"]!["equipment.visualEntity"]!);
        // the rigid model stays what the icon, the dropped model and the collection show
        Assert.Equal(Guid.Empty.ToString(), (string)j["itemParts"]!["model"]!);
        Assert.Equal("Weapon_T1_1H_Sword_Craft", (string)j["animatedHeld"]!["starter"]!);
    }

    [Fact]
    public void Where_nothing_is_cooked_the_same_template_stands_in_with_the_rigid_model_and_no_graph()
    {
        var rigid = Guid.NewGuid().ToString();
        var look = new VisualBlock { Model = rigid, Animated = Moving, Vfx = new VfxRef(Ember) };
        var cooked = Lower(Weapon(WeaponClass.Sword, look));
        Compiler.Served = (_, _) => false;
        var bare = Lower(Weapon(WeaponClass.Sword, look));
        var (c, b) = (Template(cooked, "held-test.cinderbrand.moving"), Template(bare, "held-test.cinderbrand.moving"));
        // the same template on every machine, and the item always holds it
        Assert.Equal((string)c["guid"]!, (string)b["guid"]!);
        Assert.Equal((string)b["guid"]!, (string)bare["items"]![0]!["refs"]!["equipment.visualEntity"]!);
        Assert.Equal((string)c["base"]!, (string)b["base"]!);
        // cooked: the skinned model and its graph; not: the rigid model, and no graph of ours
        Assert.Equal(BlockIds.Derive(Mod, "model", "cinderbrand_anim").ToString(), (string)c["fields"]!["ModelResource.model"]!);
        Assert.NotNull(c["fields"]!["AnimationGraphResourceComponent2.animationGraph"]);
        Assert.Equal(rigid, (string)b["fields"]!["ModelResource.model"]!);
        Assert.Null(b["fields"]!["AnimationGraphResourceComponent2.animationGraph"]);
        // everything else is the same: the VFX
        Assert.Equal(Ember, (string)b["fields"]!["VfxComponentResource.vfx"]!);
        Assert.True((bool)cooked["animatedHeld"]!["cooked"]!);
        Assert.False((bool)bare["animatedHeld"]!["cooked"]!);
    }

    [Fact]
    public void A_model_cooked_without_its_graph_is_not_cooked_and_a_look_with_no_rigid_model_stands_in_with_its_starters()
    {
        var graph = BlockIds.Derive(Mod, "graph", "cinderbrand");
        Compiler.Served = (g, type) => g != graph;
        var j = Lower(Weapon(WeaponClass.Hammer, new VisualBlock { Animated = Moving }));
        var t = Template(j, "held-test.cinderbrand.moving");
        Assert.Null(t["fields"]!["ModelResource.model"]);
        Assert.Null(t["fields"]!["AnimationGraphResourceComponent2.animationGraph"]);
        Assert.Equal("item:Weapon_T1_2H_Hammer_01.iconModel", (string)j["templateRules"]!["ModelResource.model"]!);
        // a look like an item stands in with that item's model
        var like = Lower(Weapon(WeaponClass.Hammer, new VisualBlock { Like = "Weapon_T2_2H_Hammer_01", Animated = Moving }));
        Assert.Equal("item:Weapon_T2_2H_Hammer_01.iconModel", (string)like["templateRules"]!["ModelResource.model"]!);
    }

    [Fact]
    public void A_pairs_hands_stand_in_each_by_its_own_files()
    {
        var left = BlockIds.Derive(Mod, "model", "fang_l_anim");
        Compiler.Served = (g, _) => g != left;
        var j = Lower(Weapon(WeaponClass.Daggers, new VisualBlock { Model = Guid.Empty.ToString(), Animated = Moving with { LeftModel = "fang_l_anim", LeftGraph = "fang_l" } }));
        Assert.NotNull(Template(j, "held-test.cinderbrand.moving.r")["fields"]!["AnimationGraphResourceComponent2.animationGraph"]);
        Assert.Null(Template(j, "held-test.cinderbrand.moving.l")["fields"]!["AnimationGraphResourceComponent2.animationGraph"]);
        Assert.Equal(Guid.Empty.ToString(), (string)Template(j, "held-test.cinderbrand.moving.l")["fields"]!["ModelResource.model"]!);
    }

    [Fact]
    public void The_looks_vfx_sits_on_the_moving_model_at_its_root()
    {
        var j = Lower(Weapon(WeaponClass.Sword, new VisualBlock { Animated = Moving, Vfx = new VfxRef(Ember) { Offset = new System.Numerics.Vector3(0, 0.52f, 0) } }));
        var f = Template(j, "held-test.cinderbrand.moving")["fields"]!;
        Assert.Equal(Ember, (string)f["VfxComponentResource.vfx"]!);
        Assert.Equal("Root", (string)f["VfxComponent.slot"]!);
        Assert.Equal(0.52, double.Parse(f["VfxComponent.attachmentOffset.localOffset.y"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture), 5);
        Assert.Null(j["held"]);
    }

    [Theory]
    [InlineData("hammer", "Weapon_T1_2H_Hammer_01")]
    [InlineData("shield", "Shield_T1_DPS")]
    [InlineData("bow", "Weapon_T1_2H_Bow_01")]
    public void Every_one_handed_class_gets_the_same_template_its_hand_read_from_its_starter(string cls, string starter)
    {
        var c = WeaponClass.All.Single(x => x.Key == cls);
        var j = Lower(Weapon(c, new VisualBlock { Animated = Moving }));
        Assert.Equal(Compiler.AnimatedStarter, (string)Template(j, "held-test.cinderbrand.moving")["base"]!);
        Assert.Equal(starter, (string)j["animatedHeld"]!["starter"]!);
    }

    [Fact]
    public void Json_and_csharp_lower_to_the_same()
    {
        var json = BlockJson.Parse<WeaponBlock>($$"""
            { "name": "Cinderbrand", "class": "sword", "visual": { "animated": { "model": "cinderbrand_anim", "graph": "cinderbrand" }, "vfx": { "name": "{{Ember}}", "offset": [0, 0.5, 0] } } }
            """);
        var cs = Weapon(WeaponClass.Sword, new VisualBlock { Animated = Moving, Vfx = new VfxRef(Ember) { Offset = new System.Numerics.Vector3(0, 0.5f, 0) } });
        Assert.Equal(ItemCompileGoldenTests.Text(Lower(cs)), ItemCompileGoldenTests.Text(Lower(json)));
    }

    [Fact]
    public void Daggers_get_a_parent_with_a_moving_child_in_each_hand()
    {
        var g = Guid.NewGuid().ToString();
        var j = Lower(Weapon(WeaponClass.Daggers, new VisualBlock { Animated = Moving with { LeftModel = "fang_l_anim", LeftGraph = g }, Vfx = new VfxRef(Ember) }));
        var parent = Template(j, "held-test.cinderbrand.moving");
        Assert.Equal(Compiler.AnimatedPairStarter, (string)parent["base"]!);
        var r = Template(j, "held-test.cinderbrand.moving.r");
        var l = Template(j, "held-test.cinderbrand.moving.l");
        Assert.Equal(BlockIds.Derive(Mod, "template", "cinderbrand.moving.r").ToString(), (string)r["guid"]!);
        Assert.Equal(BlockIds.Derive(Mod, "model", "cinderbrand_anim").ToString(), (string)r["fields"]!["ModelResource.model"]!);
        Assert.Equal(BlockIds.Derive(Mod, "model", "fang_l_anim").ToString(), (string)l["fields"]!["ModelResource.model"]!);
        Assert.Equal(g, (string)l["fields"]!["AnimationGraphResourceComponent2.animationGraph"]!);
        Assert.All(new[] { r, l }, t => Assert.Equal(Ember, (string)t["fields"]!["VfxComponentResource.vfx"]!));
        Assert.Equal((string)parent["guid"]!, (string)j["items"]![0]!["refs"]!["equipment.visualEntity"]!);
        // without a left model the left hand holds the same one
        var same = Lower(Weapon(WeaponClass.Daggers, new VisualBlock { Animated = Moving }));
        Assert.Equal((string)Template(same, "held-test.cinderbrand.moving.r")["fields"]!["ModelResource.model"]!, (string)Template(same, "held-test.cinderbrand.moving.l")["fields"]!["ModelResource.model"]!);
    }

    [Theory]
    [InlineData("""{ "graph": "g" }""", "visual.animated.model")]
    [InlineData("""{ "model": "m" }""", "visual.animated.graph")]
    [InlineData("""{ }""", "visual.animated")]
    [InlineData("""{ "model": "models/m.glb", "graph": "g" }""", "visual.animated.model")]
    [InlineData("""{ "model": "m", "graph": "00000000-0000-0000-0000-000000000000" }""", "visual.animated.graph")]
    [InlineData("""{ "model": "m", "graph": "g", "leftModel": "l", "leftGraph": "lg" }""", "visual.animated.leftModel")]
    public void Wrong_animated_looks_are_refused_by_field(string animated, string field)
    {
        var e = Assert.Throws<BlockError>(() => Lower(BlockJson.Parse<WeaponBlock>($$"""{ "name": "W", "class": "sword", "visual": { "animated": {{animated}} } }""")));
        Assert.Equal(field, e.Field);
    }

    [Fact]
    public void A_pairs_left_half_needs_both_parts()
    {
        Assert.Equal("visual.animated.leftGraph", Assert.Throws<BlockError>(() => Lower(Weapon(WeaponClass.Daggers, new VisualBlock { Animated = Moving with { LeftModel = "l" } }))).Field);
        Assert.Equal("visual.animated.leftModel", Assert.Throws<BlockError>(() => Lower(Weapon(WeaponClass.Daggers, new VisualBlock { Animated = Moving with { LeftGraph = "l" } }))).Field);
    }

    [Fact]
    public void What_is_not_held_or_a_manifest_without_mod_is_refused()
    {
        var e = Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Ore", Visual = new VisualBlock { Animated = Moving } }));
        Assert.Equal("visual.animated", e.Field);
        Assert.Contains("not held", e.Message);
        Assert.Equal("visual.animated", Assert.Throws<BlockError>(() => Lower(new ArmorBlock { Name = "Hat", Slot = ArmorSlot.Head, Visual = new VisualBlock { Animated = Moving } })).Field);
        Assert.Contains("clients", Assert.Throws<BlockError>(() => Lower(Weapon(WeaponClass.Sword, new VisualBlock { Animated = Moving }), clients: "kiln")).Message);
        // a tool is held
        Lower(new ItemBlock { Name = "Pick", Tool = new ToolBlock { Harvests = ToolClass.Pickaxe }, Visual = new VisualBlock { Animated = Moving } });
    }

    // ---- the hand, from made-up rows ----

    [Theory]
    [InlineData("HandR")]
    [InlineData("HandL")]        // a greataxe's, a hammer's, a bow's
    [InlineData("ArmL")]         // a shield's
    public void One_model_goes_in_the_slot_of_the_starters_visual(string slot)
    {
        var problems = new List<string>();
        var rules = Compiler.AnimatedRules("weapon w", "visual.animated", "m.w.moving", false, new HeldRow(slot, []), null, problems);
        Assert.Empty(problems);
        var (template, r) = Assert.Single(rules);
        Assert.Equal("m.w.moving", template);
        Assert.Equal(slot, (string)r["SlotAttachment.attachmentSlot"]!);
    }

    [Fact]
    public void A_starter_held_in_no_hand_is_reported()
    {
        var problems = new List<string>();
        Assert.Empty(Compiler.AnimatedRules("weapon w", "visual.animated", "t", false, new HeldRow(null, []), null, problems));
        Assert.Contains("held in no hand", Assert.Single(problems));
        problems.Clear();
        Assert.Empty(Compiler.AnimatedRules("weapon w", "visual.animated", "t", false, null, null, problems));
        Assert.Contains("no visual template", Assert.Single(problems));
        // a starter that is a parent of one child: the child's slot
        Assert.Equal("HandL", HeldHands.One(new HeldRow("None", ["HandL"]), out _));
    }

    [Fact]
    public void A_pairs_hands_follow_the_starter_and_its_children_the_parents_order()
    {
        var problems = new List<string>();
        // the starter's pair has the left first; the parent copied lists the right first
        var rules = Compiler.AnimatedRules("weapon w", "visual.animated", "t", true, new HeldRow(null, ["HandL", "HandR"]), new HeldRow(null, ["HandR", "HandL"]), problems);
        Assert.Empty(problems);
        var byName = rules.ToDictionary(x => x.Template, x => x.Rules);
        Assert.Equal("HandL", (string)byName["t.l"]["SlotAttachment.attachmentSlot"]!);
        Assert.Equal("HandR", (string)byName["t.r"]["SlotAttachment.attachmentSlot"]!);
        Assert.Equal("t.r", (string)byName["t"]["SubEntities.subEntities[0].templateRef"]!);
        Assert.Equal("t.l", (string)byName["t"]["SubEntities.subEntities[1].templateRef"]!);
        Assert.Empty(Compiler.AnimatedRules("weapon w", "visual.animated", "t", true, new HeldRow("HandR", []), null, problems));
        Assert.Contains("one thing in each hand", Assert.Single(problems));
    }

    // ---- part B ----

    [Theory]
    [InlineData("""{ "hitAt": 0.5 }""", "cast.animation.hitAt")]
    [InlineData("""{ "like": "lob", "animation": "0x00000001" }""", "cast.animation.animation")]
    public void An_abilitys_cast_refuses_the_cast_moves(string animation, string field)
    {
        var e = Assert.Throws<BlockError>(() => Compiler.Lower(new ModBlocks(Mod, 1) { ManifestClients = "mod" }, "nova",
            BlockJson.Parse<AbilityBlock>($$"""{ "name": "Nova", "cast": { "animation": {{animation}} }, "onCast": [ { "heal": 5 } ] }""")));
        Assert.Equal(field, e.Field);
        Assert.Contains("not yet on an ability's cast", e.Message);
        // the premade alone is still an ability's motion
        Compiler.Lower(new ModBlocks(Mod, 1) { ManifestClients = "mod" }, "nova", BlockJson.Parse<AbilityBlock>("""{ "name": "Nova", "cast": { "animation": "lob" }, "onCast": [ { "heal": 5 } ] }"""));
    }
}
