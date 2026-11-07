// SPDX-License-Identifier: MIT
// Tools of every kind the game has: what each class takes ends up in the item's configs and tooltip rows, and what a class has no number for is refused by name.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod
{
    private void DefineTools()
    {
        Blocks.Define("ash", new ItemBlock { Name = "Ash", Stack = 50, Visual = new VisualBlock { Like = "Material_T1_Charcoal" } });
        Blocks.Define("garden-rake", new ItemBlock { Name = "Garden Rake", Tool = new ToolBlock { Harvests = ToolClass.Rake, Power = 750, Range = 5, StaminaCost = 1 } });
        Blocks.Define("slow-torch", new ItemBlock { Name = "Slow Torch", Tool = new ToolBlock { Harvests = ToolClass.Torch, Durability = 1200, BreaksInto = "ash" } });
        Blocks.Define("charcoal-torch", new ItemBlock { Name = "Charcoal Torch", Tool = new ToolBlock { Harvests = ToolClass.Torch, BreaksInto = "Material_T1_Charcoal" } });
        Blocks.Define("kite", new ItemBlock { Name = "Kite", Tool = new ToolBlock { Harvests = ToolClass.Glider, StaminaCost = 2 } });
        Blocks.Define("long-hook", new ItemBlock { Name = "Long Hook", Tool = new ToolBlock { Harvests = ToolClass.GrapplingHook, Range = 20, StaminaCost = 30 } });
        Blocks.Define("strong-rod", new ItemBlock { Name = "Strong Rod", Tool = new ToolBlock { Harvests = ToolClass.FishingRod, Fishing = new FishingBlock { Strength = 3, Endurance = 60 } } });
        Blocks.Define("fine-sieve", new ItemBlock { Name = "Fine Sieve", Tool = new ToolBlock { Harvests = ToolClass.Sieve, Power = 40 } });
        Blocks.Define("ash-axe", new ItemBlock { Name = "Ash Axe", Tool = new ToolBlock { Harvests = Harvest.Wood, Power = 30, BreaksInto = "ash" } });

        Refused("durability on an axe", () => Blocks.Define("axe-durable", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = Harvest.Wood, Durability = 10 } }), "axe-durable", "tool.durability", "only the game's torches");
        Refused("range on an axe", () => Blocks.Define("axe-range", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = Harvest.Wood, Range = 3 } }), "tool.range", "rake or a grappling hook");
        Refused("fishing on a torch", () => Blocks.Define("torch-fish", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = ToolClass.Torch, Fishing = new FishingBlock { Strength = 1 } } }), "tool.fishing");
        Refused("power on a glider", () => Blocks.Define("glider-power", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = ToolClass.Glider, Power = 3 } }), "tool.power");
        Refused("stamina on a fishing rod", () => Blocks.Define("rod-stamina", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = ToolClass.FishingRod, StaminaCost = 3 } }), "tool.staminaCost", "no stamina");
        Refused("breaksInto on a glider", () => Blocks.Define("glider-breaks", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = ToolClass.Glider, BreaksInto = "ash" } }), "tool.breaksInto", "no durability");
        Refused("a rake reaching too far", () => Blocks.Define("rake-far", new ItemBlock { Name = "x", Tool = new ToolBlock { Harvests = ToolClass.Rake, Range = 50 } }), "tool.range", "0..32");
    }

    private static GameDataEntry Item(string block) => Content.ItemData.Get($"{Me}.{block}") ?? throw new InvalidOperationException($"no item {block}");
    private static double N(GameDataEntry e, string path) => e.Get(path).Number;
    private static string S(GameDataEntry e, string path) => e.GetJson(path)?["value"]?.ToString() ?? "";

    private static List<(uint Loca, int Value)> Rows(GameDataEntry d)
    {
        var r = new List<(uint, int)>();
        for (var i = 0; i < 12; i++)
        {
            try { r.Add(((uint)N(d, $"uiValues[{i}].locaId"), (int)N(d, $"uiValues[{i}].value"))); }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { break; }
        }
        return r;
    }

    private static string RowText(GameDataEntry d) => string.Join(", ", Rows(d).Select(r => $"0x{r.Loca:x8}={r.Value}"));

    private void Tools()
    {
        foreach (var twin in new[] { "", "-json" })
        {
            var rake = Item("garden-rake" + twin);
            Check(N(rake, "impactValues.0xadb211a3") == 750 && N(rake, "impactValues.RangeTerraforming") == 5 && N(rake, "impactValues.RangeTerraformingFlatten") == 5 && N(rake, "impactValues.StaminaCost") == 1,
                $"rake{twin}: power {N(rake, "impactValues.0xadb211a3")}, range {N(rake, "impactValues.RangeTerraforming")}/{N(rake, "impactValues.RangeTerraformingFlatten")}, stamina {N(rake, "impactValues.StaminaCost")}");
            Check(N(rake, "equipment.placementColliders[0].dataArray[0].shape.halfSize.x") == 5 && N(rake, "equipment.placementColliders[0].dataArray[0].shape.halfSize.z") == 5 && S(rake, "equipment.terraformingType") == "Flatten",
                $"rake{twin}: the cursor's box follows the range, and it flattens: {N(rake, "equipment.placementColliders[0].dataArray[0].shape.halfSize.x")}, {S(rake, "equipment.terraformingType")}");
            Check(Rows(rake).Any(r => r.Value == 750), $"rake{twin}: the tooltip shows its power: {RowText(rake)}");

            var torch = Item("slow-torch" + twin);
            var ash = Item("ash" + (twin == "" ? "" : "-json"));
            Check(N(torch, "impactValues.0x45ab43e7") == 1200, $"torch{twin}: burns {N(torch, "impactValues.0x45ab43e7")} s");
            Check(Rows(torch).Any(r => r.Value == 1200), $"torch{twin}: the tooltip's Duration row says so: {RowText(torch)}");
            Check(S(torch, "exchangeOnBroken") == ash.Guid.ToString(), $"torch{twin}: burnt, it becomes Ash {ash.Guid}: {S(torch, "exchangeOnBroken")}");
            var axe = Item("ash-axe" + twin);
            Check(S(axe, "exchangeOnBroken") == ash.Guid.ToString() && N(axe, "damageSetup.distribution.woodDamage") == 30, $"axe{twin}: breaks into Ash, wood damage {N(axe, "damageSetup.distribution.woodDamage")}");

            var kite = Item("kite" + twin);
            Check(S(kite, "equipment.slot") == "Glider" && N(kite, "impactValues.StaminaCost") == 2, $"glider{twin}: in the glider slot, stamina {N(kite, "impactValues.StaminaCost")}");
            var hook = Item("long-hook" + twin);
            Check(S(hook, "equipment.slot") == "HookShot" && N(hook, "impactValues.0xb78235f8") == 20 && N(hook, "impactValues.0xf76c843c") == 20 && N(hook, "impactValues.StaminaCost") == 30,
                $"grappling hook{twin}: reach {N(hook, "impactValues.0xb78235f8")}/{N(hook, "impactValues.0xf76c843c")}, stamina {N(hook, "impactValues.StaminaCost")}");
            var rod = Item("strong-rod" + twin);
            Check(N(rod, "impactValues.FishingStrength") == 3 && N(rod, "impactValues.FishingEndurance") == 60, $"fishing rod{twin}: strength {N(rod, "impactValues.FishingStrength")}, endurance {N(rod, "impactValues.FishingEndurance")}");
            Check(Rows(rod).Any(r => r.Value == 3) && Rows(rod).Any(r => r.Value == 60), $"fishing rod{twin}: the tooltip rows follow: {RowText(rod)}");
            var sieve = Item("fine-sieve" + twin);
            Check(N(sieve, "damageSetup.distribution.stoneDamage") == 40 && N(sieve, "damageSetup.distribution.woodDamage") == 28, $"sieve{twin}: stone {N(sieve, "damageSetup.distribution.stoneDamage")}, wood {N(sieve, "damageSetup.distribution.woodDamage")}");
        }
        var charcoal = Content.ItemData.Get("Material_T1_Charcoal")!;
        Check(S(Item("charcoal-torch"), "exchangeOnBroken") == charcoal.Guid.ToString(), $"a torch that burns into a game item: {S(Item("charcoal-torch"), "exchangeOnBroken")}");
        // the game's own tools are untouched
        var gameRake = Content.ItemData.Get("Tool_T0_Rake_Wooden")!;
        var gameTorch = Content.ItemData.Get("Tool_Torch01")!;
        Check(N(gameRake, "impactValues.0xadb211a3") == 500 && N(gameTorch, "impactValues.0x45ab43e7") == 600, $"the game's rake and torch keep theirs: {N(gameRake, "impactValues.0xadb211a3")}, {N(gameTorch, "impactValues.0x45ab43e7")}");
    }
}
