// SPDX-License-Identifier: MIT
// Terrain materials: ground, rock and ore of a mod's own. A terrain material is a cell's low byte 1..127, as the game's dirt, stone and ores are. kiln.worldtables merges it into
// the game's tables at boot: an id and a GUID in every world's table, a terraforming config copied from the game material it starts from (its sounds, friction, particles and
// strength), and with Placeable an item in the pool the game's terrain shapes place from. A client draws it with the layers of the game material in Look.
// docs/kiln/services/worldtables.md, "Terrain materials"; docs/re/building.md 1.6.
using System.Text.Json;

namespace Kiln;

/// <summary>A new terrain material: ground, rock or an ore vein, dug with the game's tools and placed by code with <c>Build.Add</c>, <c>Paint</c> or <c>Replace</c> and its
/// <see cref="TableBlockHandle{T}.CellId"/>. It starts from a game terrain material (<see cref="Like"/>) and looks like one (<see cref="Look"/>). Every player needs the mod, as cells
/// reach them as ids. The game reads these tables once at start: a material new to this game is there from the next start, see <see cref="TableBlockHandle{T}.Status"/>. 11 fit
/// across all mods, apart from the building materials' 49.</summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record TerrainMaterialBlock : IBlock
{
    /// <summary>What players read: its item's name when it's <see cref="Placeable"/>.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The game terrain material it starts from: a <c>GameMaterials</c> Terrain name or <c>Cell&lt;1..127&gt;</c>. It brings that material's strength, what digging it gives,
    /// its sounds, friction, footing for NPCs and buffs (tar's, mud's); <see cref="Strength"/> and the flags change what they say. Null: plain stone, <c>TerrainStone</c>.</summary>
    public string? Like { get; init; }

    /// <summary>The game terrain material whose look it takes: the same layers, so it draws as that material does, and digs up its particles. Null: <see cref="Like"/>'s.</summary>
    public string? Look { get; init; }

    /// <summary>What it takes to dig it, and what comes out. Unset fields are <see cref="Like"/>'s. On a <see cref="Placeable"/> material, digging gives its own item back unless
    /// <see cref="StrengthBlock.Drops"/> says otherwise.</summary>
    public PartRef<StrengthBlock>? Strength { get; init; }

    /// <summary>The game's flatten tool works on it. Null: as <see cref="Like"/>.</summary>
    public bool? Flatten { get; init; }

    /// <summary>Feet slide on it, as on ice. Null: as <see cref="Like"/>.</summary>
    public bool? Sliding { get; init; }

    /// <summary>It has an item that the game's terrain shapes place, named <see cref="Name"/>, and digging gives it back. False: like the game's ores, it is only found and dug.</summary>
    public bool Placeable { get; init; }

    /// <summary>The item's picture: a PNG in the mod's folder, or <c>item:&lt;name&gt;</c>. Null: the game's stone's.</summary>
    public string? Icon { get; init; }

    /// <summary>How the item is made, for a <see cref="Placeable"/> material. Null: it isn't crafted, only dug.</summary>
    public RecipeBlock? Recipe { get; init; }

    /// <summary>The block's old name, after a rename: the material's id comes from it, so its cells stay in every world.</summary>
    public string? Was { get; init; }
}

internal static partial class Compiler
{
    // a terrain item that the game's terrain shapes place: slot TerrainMaterial, in the pool "TerrainMaterials"
    private const string TerrainStarter = "Build_TerrainMaterial_T1_Stone";

    /// <summary>Terrain materials. Null: not one.</summary>
    private static BlockHandle? DefineTerrainBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock =>
        block is TerrainMaterialBlock t ? DefineTerrainMaterial(ctx, low, name, t) : null;

    private static BlockHandle DefineTerrainMaterial(BlockContext ctx, Lowered low, string name, TerrainMaterialBlock m)
    {
        ctx.Need(ClientsLevel.Mod, null, "a new terrain material has to be on every player's game: cells reach them as ids");
        RequireName(ctx, m.Name, "a terrain material");
        if (m.Recipe != null && !m.Placeable) throw new BlockError(ctx.Block, "recipe", "makes its item, and it has none: say Placeable = true");
        if (m.Like is { Length: 0 }) throw new BlockError(ctx.Block, "like", "takes a game terrain material: a GameMaterials Terrain name or Cell<1..127>");
        if (m.Look is { Length: 0 }) throw new BlockError(ctx.Block, "look", "takes a game terrain material: a GameMaterials Terrain name or Cell<1..127>");
        var key = m.Was ?? name;
        var mod = ctx.Mod.Id;
        var guid = BlockIds.Derive(mod, "terrain", key);
        // what kiln.worldtables compares with its boot read: the same fields a data file gives it
        var block = new System.Text.Json.Nodes.JsonObject { ["name"] = m.Name };
        if (m.Like != null) block["like"] = m.Like;
        if (m.Look != null) block["look"] = m.Look;
        if (StrengthNode(ctx, m.Strength) is { } strength) block["strength"] = strength;
        if (m.Flatten is { } flatten) block["flatten"] = flatten;
        if (m.Sliding is { } sliding) block["sliding"] = sliding;
        if (m.Placeable) block["placeable"] = true;
        if (m.Was != null) block["was"] = m.Was;
        var (status, id) = AskTables(ctx, low, "terrainMaterials", name, block);
        low.Json["material"] = guid.ToString();
        foreach (var field in new[] { "like", "look", "flatten", "sliding", "placeable" })
            if (block[field] is { } v) low.Json[field] = v.DeepClone();
        var game = $"{mod}.{name}";
        if (m.Placeable && status == TableBlockStatus.Registered && id >= 0)
        {
            var d = new Draft(ctx, low, key, name, TerrainStarter);
            d.Fields["equipment.voxelData.placeVoxelMaterialId"] = id;
            d.Refs["equipment.voxelData.placeVoxelMaterial"] = guid.ToString();
            if (m.Icon != null) d.Icon = m.Icon;
            d.Commit(m.Name, m.Description);
            if (m.Recipe is { } recipe) DefineRecipe(ctx, low, key, game, recipe);
        }
        return new TableBlockHandle<TerrainMaterialBlock>(mod, name, guid, game, m, status, id);
    }
}

internal static class TerrainJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds = [("terrainMaterials", BlockJson.Make<TerrainMaterialBlock>)];
}
