// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;
namespace Kiln.BlockTests;

// What a registered building tool hands kiln.content, as a removal-only tool with three brushes declares it (kiln#130):
// every item rule, itemdata rule and reference must name a field and a target the game's content layer accepts. The
// native half of this check runs the exported document through kiln.content's own resolvers (test-tool-content.sh).
public sealed class ToolCompilerContentTests : IDisposable
{
    private static readonly byte[] Rubble = [8, 64, 66, 69];
    private static ModBlocks Mod() => new("tool-content-test", 1) { ManifestClients = "mod" };

    public void Dispose() => WorldTablesNative.TestDefine = null;

    // kiln.worldtables registers every tool row asked for: the table rows a baked mod has at the next start.
    private static string Registered(string document)
    {
        var doc = JsonNode.Parse(document)!.AsObject();
        var blocks = new JsonArray();
        foreach (var (section, value) in doc)
            if (value is JsonObject rows && section is "toolBrushShapes" or "toolMaterialPools")
                foreach (var (name, _) in rows) blocks.Add(new JsonObject { ["name"] = name, ["status"] = "registered" });
        return new JsonObject { ["blocks"] = blocks }.ToJsonString();
    }

    internal static (ModBlocks Mod, Lowered Low, BlockHandle Tool, BlockHandle[] Brushes) LowerRemover()
    {
        WorldTablesNative.TestDefine = Registered;
        var mod = Mod();
        var brushes = new[] { ("small", 0.5), ("medium", 1.5), ("large", 2.5) }.Select(b =>
        {
            var (handle, _, _) = Compiler.Lower(mod, b.Item1, new TerrainBrushBlock { Name = "Remover " + b.Item1, Radius = b.Item2 });
            mod.Handles[b.Item1] = handle;
            return handle;
        }).ToArray();
        var action = new TerrainToolAction { Mode = TerrainToolMode.Remove, RemoveMaterials = Rubble, TerrainOnly = true };
        var (tool, low, _) = Compiler.Lower(mod, "remover", new BuildingToolBlock
        {
            Name = "Remover", Description = "Removes rubble.",
            Brushes = ["small", "medium", "large"], // this mod's brushes by name (a handle names its mod: the process-wide registry)
            Primary = action, Secondary = action,
        });
        return (mod, low, tool, brushes);
    }

    [Fact]
    public void Each_brush_sets_its_radius_through_the_impact_config_the_base_brush_carries()
    {
        var (_, low, _, brushes) = LowerRemover();
        Assert.Equal("registered", (string?)low.Json["status"]);
        var rules = low.Json["toolItemRules"]!.AsObject();
        var radii = new[] { 0.5, 1.5, 2.5 };
        for (var i = 0; i < brushes.Length; i++)
        {
            var mine = rules[brushes[i].GameName]!.AsObject();
            // Building_Overgrowth_Shape_Small carries FloatImpactConfig 0x5258131e (BrushRadius, 0.5); an item has no field of that name.
            Assert.Equal(radii[i], (double)mine["impactValues.0x5258131e"]!);
            Assert.Single(mine);
        }
    }

    [Fact]
    public void No_brush_is_tagged_with_its_root_identity()
    {
        // The picker's query scans the game data's terraformingTools list; a runtime brush is never in it, so the native
        // frame adapter writes the family's rows itself. A root GUID is no ItemTag the game knows: kiln.content refuses it.
        var (mod, low, _, brushes) = LowerRemover();
        low.CommitAfter(); // the rules kiln.content gets at content ready: they are only queued here
        try
        {
            foreach (var b in brushes)
            {
                var itemdata = mod.ReadyRules.Where(r => r.Item1 == "itemdata" && r.Item2 == b.GameName).SelectMany(r => r.Item3.Select(p => p.Key)).Order().ToArray();
                Assert.Equal(["sequences[0].sequence", "sequences[0].sequenceId", "sequences[1].sequence", "sequences[1].sequenceId"], itemdata);
                Assert.Equal(itemdata, low.Json["toolActionRules"]![b.GameName]!.AsObject().Select(p => p.Key).Order().ToArray());
            }
            Assert.DoesNotContain(mod.ReadyRules, r => r.Item3.Any(p => p.Key.StartsWith("tags", StringComparison.Ordinal)));
        }
        finally { mod.Reset(); }
    }

    [Fact]
    public void Each_brush_names_its_own_terrain_preview_model_and_the_vanilla_terrain_pool()
    {
        var (_, low, tool, brushes) = LowerRemover();
        var items = low.Json["items"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal(tool.Guid.ToString(), (string?)items[0]["guid"]);
        Assert.Null(items[0]["refs"]!["equipment.terrainVoxelObject"]);
        for (var i = 0; i < brushes.Length; i++)
        {
            var refs = items[i + 1]["refs"]!.AsObject();
            Assert.Equal(brushes[i].Guid.ToString(), (string?)refs["equipment.terrainVoxelObject"]);
            Assert.Equal("256c43ba-dd20-4369-b3bf-f49c671a59ec", (string?)refs["equipment.voxelBlueprintMaterialPool"]);
        }
    }

    [Fact]
    public void Each_brush_previews_with_the_cursor_effect_of_the_vanilla_shape_nearest_its_radius()
    {
        // kiln#153: the game's cursor preview is the shape item's own equipment.cursorVfx, one effect per vanilla size
        // (Small 0.5 m, Medium 1.5 m, Big 2.5 m); the impact radius is the brush's own (impactValues.0x5258131e). Every brush
        // was based on Building_Overgrowth_Shape_Small and kept its small preview whatever its radius.
        var (_, low, _, brushes) = LowerRemover();
        var items = low.Json["items"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        var shapes = new[] { "Building_Overgrowth_Shape_Small", "Building_Overgrowth_Shape_Medium", "Building_Overgrowth_Shape_Big" };
        for (var i = 0; i < brushes.Length; i++)
            Assert.Equal("item:" + shapes[i], (string?)items[i + 1]["refs"]!["equipment.cursorVfx"]);
        Assert.Null(items[0]["refs"]!["equipment.cursorVfx"]); // the root keeps Overgrowth Tool's (it has none)
    }

    [Theory]
    [InlineData(0.25, "Building_Overgrowth_Shape_Small")]
    [InlineData(0.5, "Building_Overgrowth_Shape_Small")]
    [InlineData(0.99, "Building_Overgrowth_Shape_Small")]
    [InlineData(1.0, "Building_Overgrowth_Shape_Medium")]
    [InlineData(1.5, "Building_Overgrowth_Shape_Medium")]
    [InlineData(1.99, "Building_Overgrowth_Shape_Medium")]
    [InlineData(2.0, "Building_Overgrowth_Shape_Big")]
    [InlineData(8.0, "Building_Overgrowth_Shape_Big")]
    public void The_preview_shape_is_the_vanilla_size_nearest_the_radius_ties_to_the_larger(double radius, string shape)
        => Assert.Equal(shape, Compiler.BrushPreviewShape(radius));

    [Fact]
    public void A_remove_action_asks_the_game_cursor_for_no_building_material()
    {
        // The game's build cursor checks the selected building material for an action whose item asks for it, and the
        // impact refuses the stroke while that check fails (CursorFlag MissingMaterials). The base brush
        // Building_Overgrowth_Shape_Small asks on primary, its Add; a removal-only tool has no material to select, so its
        // primary Remove never reached the terrain (kiln#133). A Remove asks on neither input.
        var (_, low, _, brushes) = LowerRemover();
        var items = low.Json["items"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        for (var i = 0; i < brushes.Length; i++)
        {
            var fields = items[i + 1]["fields"]!.AsObject();
            Assert.Equal("False", (string?)fields["equipment.cursorConfig.checkRequiredMaterialsOnPrimaryAction"]);
            Assert.Equal("False", (string?)fields["equipment.cursorConfig.checkRequiredMaterialsOnSecondaryAction"]);
        }
        Assert.False(items[0]["fields"]!.AsObject().ContainsKey("equipment.cursorConfig.checkRequiredMaterialsOnPrimaryAction"));
    }

    // KILN_TOOL_CONTENT_FIXTURE=<file>: the document native/services/tools/tests/test-tool-content.sh feeds kiln.content's
    // resolvers with the game's reflection and the real Small brush bytes. Exactly what this compiler emits, no hand edits.
    [Fact]
    public void Exports_the_emitted_content_for_the_native_content_check()
    {
        var path = Environment.GetEnvironmentVariable("KILN_TOOL_CONTENT_FIXTURE");
        if (string.IsNullOrEmpty(path)) return;
        var (_, low, _, brushes) = LowerRemover();
        var document = new JsonObject
        {
            ["items"] = low.Json["items"]!.DeepClone(),
            ["itemRules"] = low.Json["toolItemRules"]!.DeepClone(),
            ["itemdata"] = low.Json["toolActionRules"]!.DeepClone(),
            ["sequences"] = new JsonArray(low.Json["sequences"]!.AsArray().Select(s => (JsonNode?)JsonValue.Create((string?)s!["guid"])).ToArray()),
            // kiln.worldtables serves a terrain keen::VoxelModelResource under each registered brush's item GUID
            ["servedModels"] = new JsonArray(brushes.Select(b => (JsonNode?)JsonValue.Create(b.Guid.ToString())).ToArray()),
        };
        File.WriteAllText(path, document.ToJsonString());
    }
}
