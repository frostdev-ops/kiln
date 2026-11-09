// SPDX-License-Identifier: MIT
// The blocks of things a player builds and leaves standing: a prop, and a station, which is a prop that makes things. C# records, and the same shape in blocks/*.json.
// No base to name and no GUIDs: which game item and template one starts from follows from its look, its kind or the surface it sits on.
// docs/kiln/modding/world-blocks.md.
using System.Numerics;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What a placed thing sits on. More than one: <c>Surface.Floor | Surface.Wall</c>; in JSON <c>"floor, wall"</c>. The first is what the cursor tries first.</summary>
[Flags]
public enum Surface { Floor = 1, Wall = 2, Ceiling = 4 }

/// <summary>Where and how a player can place something. A field left out: what its starter does.</summary>
public sealed record PlacementBlock : IBlock
{
    public Surface? On { get; init; }
    /// <summary>Only inside a base, as the game's furniture.</summary>
    public bool? InBase { get; init; }
    /// <summary>Metres, width, height and depth: the box that has to be free to place it, standing on its floor. Its collision stays its starter's.</summary>
    public Vector3? Size { get; init; }
    /// <summary>The cursor starts out snapping to the voxel grid. A player can toggle it.</summary>
    public bool? Snap { get; init; }
    /// <summary>It can stand in water.</summary>
    public bool? Water { get; init; }
    /// <summary>It can be placed below the Shroud's line.</summary>
    public bool? Fog { get; init; }
    /// <summary>Placing it takes the item from the stack. False: the item stays, as for a build tool.</summary>
    public bool? Consumed { get; init; }
}

/// <summary>A collision box, in metres, around the model's origin as the model is drawn.</summary>
public sealed record BoxCollider
{
    /// <summary>Width, height and depth.</summary>
    public required Vector3 Size { get; init; }
    /// <summary>Where its centre is. Null: standing on the floor under the origin, so the centre is half the height up.</summary>
    public Vector3? Center { get; init; }
}

/// <summary>A prop that <see cref="PropBlock.Provides"/> a crafting prop, by handle or name: a block of this mod, or <c>"other-mod:name"</c>.</summary>
[JsonConverter(typeof(PropRefConverter))]
public readonly record struct PropRef(string Value)
{
    public static implicit operator PropRef(string value) => new(value);
    public static implicit operator PropRef(BlockHandle handle) => new(handle.ToString());
    public override string ToString() => Value;
}

internal sealed class PropRefConverter : JsonConverter<PropRef>
{
    public override PropRef Read(ref System.Text.Json.Utf8JsonReader r, Type t, System.Text.Json.JsonSerializerOptions o) => new(r.GetString() ?? throw new System.Text.Json.JsonException("a prop's name"));
    public override void Write(System.Text.Json.Utf8JsonWriter w, PropRef v, System.Text.Json.JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

/// <summary>What the game's comfort counts, for whoever rests within range. The tooltip's Comfort row follows.</summary>
public sealed record ComfortBlock : IBlock
{
    public required int Amount { get; init; }
    /// <summary>Only the best of a category counts near a player, as the game's rule goes.</summary>
    public ComfortCategory Category { get; init; } = ComfortCategory.Hearth;
    /// <summary>Metres.</summary>
    public double Range { get; init; } = 5;
    /// <summary>Resistance to the cold near it. Every comfort prop of the game has 1.</summary>
    public int Warmth { get; init; } = 1;
}

/// <summary>The game's comfort categories.</summary>
public enum ComfortCategory { Hearth, Bed, Chair, Table, Bench, Throne, Bathtub, Sink, Toilet, Shower, Carpet, Illumination, Flame, Trophy, Stool, Kitchen, Wellness }

/// <summary>Something placed that stands there: furniture, a banner, a lamp. It needs a look: a game item that places something (<see cref="VisualBlock.Like"/>: its
/// model, its collision and how it places, all of it), or a cooked model (<see cref="VisualBlock.Model"/>: the rest then comes from what it sits on).</summary>
public record PropBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public PartRef<VisualBlock>? Visual { get; init; }
    public PartRef<PlacementBlock>? Placement { get; init; }
    public PartRef<ComfortBlock>? Comfort { get; init; }
    /// <summary>What a player bumps into and what hits stop at: one box. Null: with a cooked model of yours, the box around that model; with a game item's look, that item's
    /// collision. Only a prop with a <see cref="VisualBlock.Model"/> of its own takes one: a game item's look, a station or furniture keeps the collision it starts from.
    /// For more than one box see <see cref="Colliders"/>.</summary>
    public BoxCollider? Collider { get; init; }
    /// <summary>Several boxes in place of one: a table's top and its legs, a doorway's two posts. They are all solid, and together one shape in the template. The same rules as <see cref="Collider"/>
    /// (a model of yours, a floor or wall prop) and at most <see cref="BoxCollider"/> 16 of them. The box a player's cursor checks for room stays one: the box around them all.</summary>
    public IReadOnlyList<BoxCollider>? Colliders { get; init; }
    /// <summary>A recipe elsewhere can ask for it nearby: <c>NeedsProps = ["cold-forge"]</c> in a <see cref="RecipeBlock"/>. It takes one of the game's 13 free places for such props, in every
    /// game that has the mod: each game gives it its place when the game starts, from the mods' files. A prop of code is there from the second start.</summary>
    public bool Provides { get; init; }
    /// <summary>What happens when a player uses it: the prompt, what they play, a toggle, what it takes, effects. Furniture has its kind's; this changes what it says.</summary>
    public PartRef<InteractBlock>? OnInteract { get; init; }
    /// <summary>A VFX on it for as long as it stands, which is where the game's lamps have their light. A client check: nothing on a server draws it.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Light { get; init; }
    /// <summary>How the item that places it is made.</summary>
    public RecipeBlock? Recipe { get; init; }
    public LootBlock? Loot { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }

    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
    /// <summary>The same for the template it places: <c>TweakPlaced = { { GameFields.Templates.Shelter.CheckRange, 20 } }</c>.</summary>
    [JsonConverter(typeof(TemplateTweakConverter))]
    public FieldValues TweakPlaced { get; init; } = new();
}

/// <summary>A station a player builds: a prop that makes things. With <see cref="Processing"/> it works on its own, over time, from its own inventory, like a forge;
/// without, a player crafts at it at once, like a workbench. With <see cref="Recipes"/> on a kind that works on its own it has a workshop of its own: it runs those
/// recipes and no others.</summary>
public sealed record StationBlock : PropBlock
{
    /// <summary>The game's station it starts from: its model, collision and sound, and the menu it opens. Null: a drying rack with <see cref="Processing"/>, else a workbench.</summary>
    public StationKind? Kind { get; init; }
    /// <summary>A workshop of its own, which is what decides which recipes it runs. Null: yes when it works on its own and has <see cref="Processing"/> or <see cref="Recipes"/>. False: it shares its kind's,
    /// so it runs the game's recipes too, and its own also run at the game's stations of that kind. A bench can have one, but the server doesn't check a player's craft against a bench's workshop: only its menu changes.</summary>
    public bool? OwnWorkshop { get; init; }
    /// <summary>The menu it opens. Null: its kind's.</summary>
    public PartRef<MenuBlock>? Menu { get; init; }
    /// <summary>How it works on its own.</summary>
    public PartRef<ProcessingBlock>? Processing { get; init; }
    /// <summary>What it makes, each with <see cref="RecipeBlock.Output"/>. An item of your own can say the same from its side: <c>Recipe = new RecipeBlock { Station = thisStation, ... }</c>.</summary>
    public IReadOnlyList<RecipeBlock>? Recipes { get; init; }
}

/// <summary>How a station works on its own. A field left out: what its kind does.</summary>
public sealed record ProcessingBlock : IBlock
{
    /// <summary>Inventory slots, inputs and outputs in one.</summary>
    public int? Slots { get; init; }
    /// <summary>Rain puts it out. False: it never goes out. True: as the game's stations.</summary>
    public bool? GoesOutInRain { get; init; }
    /// <summary>What shows while it runs.</summary>
    public RunningBlock? Running { get; init; }
}

/// <summary>What a station shows while it runs. A client check: a server only carries these.</summary>
public sealed record RunningBlock
{
    /// <summary>In place of its kind's fire or steam.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
    /// <summary>A looping sound in place of its kind's: a cue of this mod (then <c>clients: mod</c>), <c>"other-mod:name"</c>, or a cue's GUID.</summary>
    [ClientsModWhen("^(?![0-9a-fA-F]{8}-)[^:]+$")]
    public SoundRef? Sound { get; init; }
}

/// <summary>The crafting menu a station opens. Today that is an existing station's, and the block's recipes are listed there among the game's. A workshop's menu with tabs of its own goes here.</summary>
public sealed record MenuBlock : IBlock
{
    /// <summary>The station whose menu it opens. Null: the station's kind.</summary>
    public StationKind? Like { get; init; }
}

/// <summary>A workshop of the game, with the item that places it. Premade instances: the build a station starts from, and the menu it opens.</summary>
/// <param name="Timed">The game's station works on its own, over time. False: a player crafts at it at once.</param>
[JsonConverter(typeof(PremadeConverter<StationKind>))]
public sealed record StationKind(string Key, string Item, string Template, string Workshop, bool Timed = false) : IPremade
{
    public static readonly StationKind Workbench = new("workbench", "Craftingstation_Workbench", "Craftingstation_Workbench", "0x4da88f73");
    public static readonly StationKind Carpenter = new("carpenter", "Craftingstation_Carpenter_Workbench", "Craftingstation_Carpenter_Workbench", "0x8996a8c4");
    public static readonly StationKind Kitchen = new("kitchen", "Craftingstation_Farmer_Kitchen", "Craftingstation_Farmer_Kitchen", "0x07ce61f0");
    public static readonly StationKind Forge = new("forge", "Prop_Production_T1_Forge", "T1_Forge_Factory", "0x8dca2c49", true);
    public static readonly StationKind Oven = new("oven", "Prop_Production_T3_Oven", "T3_Oven_Factory", "0x1ee9f0bf", true);
    public static readonly StationKind Alchemy = new("alchemy", "Prop_Production_T3_Alchemy_Station", "T3_Alchemy_Station_Factory", "0xd08b9300", true);
    public static readonly StationKind DryingRack = new("drying-rack", "Prop_Production_T1_Drying_Rack", "T1_Drying_Rack_Factory", "0x6b95dc33", true);
    public static readonly StationKind CharcoalKiln = new("charcoal-kiln", "Prop_Production_T1_Charcoal_Kiln", "T1_Charcoal_Kiln_Factory", "0x2546ae49", true);
    public static readonly StationKind Kiln = new("kiln", "Prop_Production_T2_Kiln", "T2_Kiln_Factory", "0x847eab53", true);
    public static readonly StationKind Smelter = new("smelter", "Prop_Production_T2_Smelter", "T2_Smelter_Factory", "0xf5aab50a", true);
    public static readonly StationKind Fireplace = new("fireplace", "Prop_production_T2_Cooking_Station", "T2_Fireplace_Factory", "0x39fbd98f", true);
    public static readonly StationKind Grinder = new("grinder", "Prop_Production_T1_HandGrinder", "T1_HandGrinder_Factory", "0x056d94f8", true);
    public static readonly StationKind Tannery = new("tannery", "Prop_Production_T3_Tanning_Station", "T3_Tanning_Station_Factory", "0xa4b9eaa5", true);
    public static readonly StationKind TableSaw = new("table-saw", "Prop_Production_T2_Table_Saw", "T2_Table_Saw_Factory", "0x1abf8143", true);
    internal static readonly StationKind[] All = [Workbench, Carpenter, Kitchen, Forge, Oven, Alchemy, DryingRack, CharcoalKiln, Kiln, Smelter, Fireplace, Grinder, Tannery, TableSaw];
}
