// SPDX-License-Identifier: MIT
// A shape of your own for the game's building materials: a wall with a round window, an arch, a stair of your own. A shape is two things in the game:
//  - a blueprint item (category Blueprints, slot BuildTool): its placement and snapping boxes, its placement colliders, the material pool it cycles through;
//  - an entry in VoxelBlueprintItemRegistryResource, keyed by the item's id: its size in cells and one bit per cell. A single table every shape of every mod shares, so
//    kiln.worldtables merges it at boot, as it does the block items of new materials.
// Cells are half a metre. Bit i of the data (byte i / 8, lowest bit first) is the cell x + X * (y + Y * z), y up: the game's 4 m door wall, 8 x 8 x 1, is
// c3 c3 c3 c3 c3 ff ff ff. A placement takes one block of the chosen material per cell it fills; cells already built are skipped and cost nothing. The item's
// requiredCraftingIngredients count isn't what a placement takes (the game's door wall says 20 and takes 44). docs/re/building.md 1.2.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>A new shape on the game's building materials: in the build menu beside the walls and floors, and it takes every material they take. The cells are a picture of it, half a metre
/// each. The game reads its shapes once at start: a shape new to this game is there from the next start, see <see cref="TableBlockHandle{T}.Status"/>. Every player needs the mod: each game merges its
/// shapes' table from the mods' files at its own start.</summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record BlueprintBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The shape, half a metre a cell: layers from the bottom up; in a layer, rows from the back (+z) to the front; in a row, cells from left to right. <c>#</c> is a cell,
    /// <c>.</c> or a space is none. Rows and layers may be short: what is missing is empty.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Cells { get; init; }

    /// <summary>A roof shape: it cycles through the roof materials, not the walls'.</summary>
    public bool Roof { get; init; }

    /// <summary>Not used. A placement takes one block of the chosen material per cell it fills, as the game's own shapes do, and cells already built cost nothing; a
    /// shape's cost can't be set.</summary>
    [Obsolete("A placement takes one block of the chosen material per cell it fills; a shape's cost can't be set.")]
    public int? Cost { get; init; }

    /// <summary>How the shape is made. Null: it is not crafted, as the game's own shapes aren't.</summary>
    public RecipeBlock? Recipe { get; init; }

    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }

    /// <summary>Game fields this block does not cover, set on the blueprint item: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

internal static partial class Compiler
{
    // Starters with one placement collider and no snap box offsets, whose snapping box is their placement box: a slab of cells, as a shape of cells is. (The sloped
    // roofs have a snapping box one cell past their cells and a collider per step: a shape that kept those landed off its cursor.)
    private const string ShapeStarter = "Blueprint_Voxel_Block_Wall_Straight_4m", RoofShapeStarter = "Blueprint_Voxel_Roof_Flat_4m";
    private const int MaxCells = 32;

    /// <summary>The cells as the game keeps them: (x, y, z) in cells, and one bit per cell.</summary>
    internal static (int X, int Y, int Z, byte[] Bits, int Full) ShapeCells(BlockContext ctx, IReadOnlyList<IReadOnlyList<string>>? layers)
    {
        if (layers is not { Count: > 0 }) throw new BlockError(ctx.Block, "cells", "a shape needs its cells: layers of rows of # and .");
        var y = layers.Count;
        var z = layers.Max(l => l?.Count ?? 0);
        var x = layers.SelectMany(l => l ?? []).Select(r => (r ?? "").TrimEnd().Length).DefaultIfEmpty(0).Max();
        if (x == 0 || z == 0) throw new BlockError(ctx.Block, "cells", "the shape has no cells: # is a cell");
        if (x > MaxCells || y > MaxCells || z > MaxCells) throw new BlockError(ctx.Block, "cells", $"{x} x {y} x {z} cells: a shape fits in {MaxCells} each way ({MaxCells / 2} m)");
        var bits = new byte[(x * y * z + 7) / 8];
        var full = 0;
        for (var ly = 0; ly < y; ly++)
        {
            var rows = layers[ly] ?? [];
            for (var lz = 0; lz < rows.Count; lz++)
            {
                var row = rows[lz] ?? "";
                for (var lx = 0; lx < row.Length; lx++)
                {
                    var c = row[lx];
                    if (c is '.' or ' ') continue;
                    if (c != '#') throw new BlockError(ctx.Block, $"cells[{ly}][{lz}]", $"'{c}' at {lx}: a cell is # and none is . or a space");
                    var i = lx + x * (ly + y * lz);
                    bits[i / 8] |= (byte)(1 << (i % 8));
                    full++;
                }
            }
        }
        if (full == 0) throw new BlockError(ctx.Block, "cells", "the shape has no cells: # is a cell");
        return (x, y, z, bits, full);
    }

    private static BlockHandle DefineBlueprint(BlockContext ctx, Lowered low, string name, BlueprintBlock b)
    {
        ctx.Need(ClientsLevel.Mod, null, "a new shape has to be in every player's game: each one merges its shapes' table from the mods' files at its start");
        RequireName(ctx, b.Name, "a shape");
        var (x, y, z, bits, full) = ShapeCells(ctx, b.Cells);
        var key = b.Was ?? name;
        var mod = ctx.Mod.Id;
        var d = new Draft(ctx, low, key, name, b.Roof ? RoofShapeStarter : ShapeStarter);
        var (sx, sy, sz) = (x * 0.5, y * 0.5, z * 0.5);
        // The game snaps a shape by its snapping box and places its cells from the box's corner: every one of the game's shapes has the two boxes alike, so a
        // snapping box left at the starter's size moves the cells away from the cursor.
        foreach (var box in new[] { "placementAABB", "snappingAABB" })
        {
            d.Fields[$"equipment.{box}min.x"] = 0.0;
            d.Fields[$"equipment.{box}min.y"] = 0.0;
            d.Fields[$"equipment.{box}min.z"] = 0.0;
            d.Fields[$"equipment.{box}max.x"] = sx;
            d.Fields[$"equipment.{box}max.y"] = sy;
            d.Fields[$"equipment.{box}max.z"] = sz;
        }
        foreach (var end in new[] { "Min", "Max" })
            foreach (var axis in new[] { "x", "y", "z" })
                d.Fields[$"equipment.voxelSnappingConfig.snapBoxOffset{end}.{axis}"] = 0;
        // Lists in the item: kiln.gamedata's item paths reach into them, kiln.content's fields don't. The starter's one placement collider becomes the shape's box,
        // as the game's slabs have it (centre and half size both half the box).
        const string collider = "equipment.placementColliders[0].dataArray[0]";
        d.DataRules[$"{collider}.offset.x"] = sx / 2;
        d.DataRules[$"{collider}.offset.y"] = sy / 2;
        d.DataRules[$"{collider}.offset.z"] = sz / 2;
        d.DataRules[$"{collider}.shape.halfSize.x"] = sx / 2;
        d.DataRules[$"{collider}.shape.halfSize.y"] = sy / 2;
        d.DataRules[$"{collider}.shape.halfSize.z"] = sz / 2;
        // What a placement in the open takes: one block per cell. The game doesn't read this count for a shape; it lists the truth where anything shows it.
        d.DataRules["equipment.requiredCraftingIngredients[0].count"] = full;
        // what the client draws under the cursor: kiln.worldtables serves the shape's model under the item's own GUID
        d.Refs["equipment.voxelObject"] = d.Guid.ToString();
        var shape = new JsonObject { ["size"] = new JsonArray(x, y, z), ["cells"] = Convert.ToHexString(bits).ToLowerInvariant(), ["full"] = full, ["cost"] = full, ["roof"] = b.Roof };
        low.Json["blueprint"] = shape;
        var (status, _) = AskShapes(ctx, low, name, new JsonObject { ["name"] = b.Name, ["item"] = d.Guid.ToString(), ["size"] = new JsonArray(x, y, z), ["cells"] = Convert.ToBase64String(bits), ["roof"] = b.Roof });
        // A blueprint item with no entry in the shapes' table is one the game can't place: it is made once the table has it.
        if (status == TableBlockStatus.Registered)
        {
            d.Commit(b.Name, b.Description);
            if (b.Recipe is { } recipe) DefineRecipe(ctx, low, key, d.Game, recipe);
        }
        return new TableBlockHandle<BlueprintBlock>(mod, name, d.Guid, d.Game, b, status, -1);
    }

    /// <summary>kiln.worldtables' word on a shape. A build whose kiln.worldtables takes no shapes says nothing of it: then nothing is made, and the log says why.</summary>
    private static (TableBlockStatus Status, int Id) AskShapes(BlockContext ctx, Lowered low, string name, JsonObject block)
    {
        var mod = ctx.Mod;
        mod.ManifestClients ??= BlocksBridge.ManifestClients(mod.Id);
        var doc = new JsonObject { ["format"] = 1, ["mod"] = mod.Id, ["dir"] = BlocksBridge.ModDirectory(mod.Id), ["clients"] = mod.ManifestClients, ["blueprints"] = new JsonObject { [name] = block } };
        var report = WorldTablesNative.DefineJson(doc.ToJsonString());
        JsonObject? r = null;
        try { r = report != null ? JsonNode.Parse(report) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { }
        foreach (var e in r?["errors"] as JsonArray ?? [])
        {
            var (field, text) = TableError(e?.GetValue<string>() ?? "", name);
            throw new BlockError(ctx.Block, field, text);
        }
        foreach (var x in r?["blocks"] as JsonArray ?? [])
        {
            if ((string?)x?["name"] != name || (string?)x?["kind"] != "blueprint") continue;
            low.Json["status"] = (string?)x["status"];
            return (string?)x["status"] switch { "registered" => (TableBlockStatus.Registered, -1), "pending" => (TableBlockStatus.Pending, -1), _ => (TableBlockStatus.Off, -1) };
        }
        low.Json["status"] = "off";
        low.AfterCommit(() =>
        {
            var text = $"{name}: kiln.worldtables on this build keeps no shapes yet: the shape is checked and nothing is made";
            lock (mod.Warnings)
                if (!mod.Warnings.Contains(text)) mod.Warnings.Add(text);
            mod.ReportDirty = true;
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {text}");
        });
        return (TableBlockStatus.Off, -1);
    }
}
