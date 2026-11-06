// SPDX-License-Identifier: MIT
// The material blocks: what a mod author writes, in C# and (the same shape) in blocks/*.json. docs/kiln/modding/world-blocks.md.
namespace Kiln;

/// <summary>How hard a material is: which column of a tool's or an explosion's damage applies to it. <see cref="Unbreakable"/> takes no damage from anything, and can't be dismantled.</summary>
public enum Hardness { Soft, SlightlyHard, ModeratelyHard, Hard, VeryHard, Unbreakable }

/// <summary>Which of a tool's class modifiers applies when it digs a material. <see cref="None"/>: 1.0, whatever the tool.</summary>
public enum Breaks { None, Wood, Stone, Metal }

/// <summary>What it takes to dig a material, and what comes out. Cost per block = <see cref="Health"/> / (the tool's damage for <see cref="Hardness"/> x the server's mining factor x the class modifier).
/// A field left out stays what it was: the game material's own, or for a new material the game's plainest of that <see cref="Breaks"/>.</summary>
[BlockClients(ClientsLevel.None)]
public sealed record StrengthBlock : IBlock
{
    /// <summary>The damage column a tool or an explosion uses against it.</summary>
    public Hardness? Hardness { get; init; }

    /// <summary>Health points of a block. Rough Stone is 250, an Iron Block 2000.</summary>
    public int? Health { get; init; }

    /// <summary>The tool's class modifier that applies.</summary>
    public Breaks? Breaks { get; init; }

    /// <summary>How many blocks dug give one loot item.</summary>
    public int? ItemsPerBlocks { get; init; }

    /// <summary>The loot item: a <c>GameItems</c> name or a GUID, or on a new material <see cref="OwnBlock"/>. A new material that doesn't say gives back the first of its recipe's
    /// <see cref="RecipeBlock.Needs"/> at the recipe's ratio, as the game's own blocks do: 2 stone make 100 Rough Stone blocks, and 50 dug give a stone back.</summary>
    public string? Drops { get; init; }

    /// <summary><see cref="Drops"/> of a new material: its own block item, one per block dug unless <see cref="ItemsPerBlocks"/> says.</summary>
    public const string OwnBlock = "block";
}

/// <summary>Changes a game material in place: <c>Material = GameMaterials.StoneRough, Strength = new() { Hardness = Hardness.Unbreakable }</c>. The dig rate is the server's, so a tweak of
/// <see cref="Strength"/> asks nothing of players' games. The game reads these tables once at start: a tweak new to this game is there from the next start, see <see cref="TableBlockHandle{T}.Status"/>.</summary>
[BlockClients(ClientsLevel.None)]
public sealed record MaterialTweakBlock : IBlock
{
    /// <summary>The game material: a <c>GameMaterials</c> name, <c>Cell&lt;id&gt;</c>, or the material's GUID.</summary>
    public required string Material { get; init; }

    /// <summary>What it takes to dig it.</summary>
    public PartRef<StrengthBlock>? Strength { get; init; }

    /// <summary>New pictures for it: a layer written here replaces the game's, the rest stays. Only pictures, so it asks nothing of other players: a game without the mod draws the game's own.
    /// The textures are cooked on each client at start.</summary>
    public PartRef<SurfaceBlock>? Surface { get; init; }
}

/// <summary>A new building material: an id and a GUID in the game's tables, its strength, and a block item the game's shapes (walls, floors, stairs, columns) can list. Every player needs the mod, as cells
/// reach them as ids. The game reads these tables once at start: a material new to this game is there from the next start, see <see cref="TableBlockHandle{T}.Status"/>. 49 fit across all mods.</summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record BuildingMaterialBlock : IBlock
{
    /// <summary>The block item's name, as players read it.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>What it takes to dig it. Unset fields come from the game's plainest material of that <see cref="StrengthBlock.Breaks"/>; stone when that is unset too.</summary>
    public PartRef<StrengthBlock>? Strength { get; init; }

    /// <summary>It's a roof material: the roof pieces list it, not the walls.</summary>
    public bool Roof { get; init; }

    /// <summary>How it looks. Null: like the game's Rough Stone. The textures are cooked on each client at start; a game without them draws the game's plain fallback stone.</summary>
    public PartRef<SurfaceBlock>? Surface { get; init; }

    /// <summary>The block item's picture: a PNG in the mod's folder, or <c>item:&lt;name&gt;</c>. Null: a block of its surface, drawn on each client at start.</summary>
    public string? Icon { get; init; }

    /// <summary>How the block item is made. Null: it isn't crafted.</summary>
    public RecipeBlock? Recipe { get; init; }

    /// <summary>The block's old name, after a rename: the material's id comes from it, so its walls stay in every world.</summary>
    public string? Was { get; init; }
}

/// <summary>Whether a material block is in this game's tables now.</summary>
public enum TableBlockStatus
{
    /// <summary>The boot merge has this block with this definition.</summary>
    Registered,

    /// <summary>The game read its tables before this definition existed: it's kept, and it's there from the next start.</summary>
    Pending,

    /// <summary>This game build doesn't resolve or the service is off: the game's tables are untouched, and kiln.log says why.</summary>
    Off,
}

/// <summary>What <see cref="Blocks.Define(string, MaterialTweakBlock)"/> and <see cref="Blocks.Define(string, BuildingMaterialBlock)"/> give back.</summary>
public sealed class TableBlockHandle<T> : BlockHandle<T> where T : class, IBlock
{
    internal TableBlockHandle(string mod, string name, Guid guid, string gameName, T block, TableBlockStatus status, int cellId) : base(mod, name, guid, gameName, block)
    {
        Status = status;
        CellId = cellId;
    }

    /// <summary>Whether this game has it now.</summary>
    public TableBlockStatus Status { get; }

    /// <summary>The material's id in a cell this session, or -1. It may change between sessions.</summary>
    public int CellId { get; }
}
