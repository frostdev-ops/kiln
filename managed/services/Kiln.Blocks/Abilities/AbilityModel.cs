// SPDX-License-Identifier: MIT
// Perks, abilities, talents, and the deliveries beyond a projectile: C# records, and the same shape in blocks/*.json. docs/kiln/modding/blocks.md.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

// ------------------------------------------------------------------------------------------------- deliveries -----

/// <summary>A delivery that is not a flight. It happens where it is placed, facing where the caster aims, and finds who it reaches when it happens: no collider, so it runs with or without a player near.
/// A spell's staff or a wand places it at the hand; an ability at the caster.</summary>
public abstract record PlacedDelivery : IDeliveryBlock
{
    /// <summary>On a spell, what shows in the hand.</summary>
    public ProjectileLook Look { get; init; } = ProjectileLook.IceBolt;
    /// <summary>A VFX where it happens, facing where it goes. Its size is the VFX's own.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
    /// <summary>Game fields this block does not cover, set on the template it is placed as. It wins over what the block sets.</summary>
    [JsonConverter(typeof(TemplateTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

/// <summary>Only the caster: a buff, a heal, a dash.</summary>
public sealed record SelfBlock : PlacedDelivery
{
    public required IReadOnlyList<EffectRef> Effects { get; init; }
}

/// <summary>Everyone around the caster.</summary>
public sealed record AroundBlock : PlacedDelivery
{
    /// <summary>Metres.</summary>
    public required float Radius { get; init; }
    public AreaAffects Affects { get; init; } = AreaAffects.Enemies;
    public required IReadOnlyList<EffectRef> Effects { get; init; }
}

/// <summary>Everyone in a cone in front of the caster.</summary>
public sealed record ConeBlock : PlacedDelivery
{
    /// <summary>Metres.</summary>
    public float Range { get; init; } = 8;
    /// <summary>The cone's whole width, in degrees: 1 to 179.</summary>
    public float Angle { get; init; } = 60;
    public AreaAffects Affects { get; init; } = AreaAffects.Enemies;
    public required IReadOnlyList<EffectRef> Effects { get; init; }
}

/// <summary>Everyone along a line in front of the caster. It goes through what it hits, and through walls.</summary>
public sealed record BeamBlock : PlacedDelivery
{
    /// <summary>Metres.</summary>
    public float Range { get; init; } = 20;
    /// <summary>Metres across.</summary>
    public float Width { get; init; } = 1;
    public AreaAffects Affects { get; init; } = AreaAffects.Enemies;
    public required IReadOnlyList<EffectRef> Effects { get; init; }
    /// <summary>How long it keeps hitting, turning with the caster. 0: one hit, along the aim it was cast at.</summary>
    public double Seconds { get; init; }
    /// <summary>Seconds between hits while it lasts.</summary>
    public double TickEvery { get; init; } = 0.25;
}

/// <summary>A place that keeps hitting whoever is in it.</summary>
public sealed record ZoneBlock : PlacedDelivery
{
    /// <summary>Metres.</summary>
    public required float Radius { get; init; }
    public required double Seconds { get; init; }
    public double TickEvery { get; init; } = 1;
    /// <summary>Metres in front of the caster. 0: at the caster.</summary>
    public float Ahead { get; init; }
    public AreaAffects Affects { get; init; } = AreaAffects.Enemies;
    public required IReadOnlyList<EffectRef> Effects { get; init; }
}

/// <summary>From the first one in front of the caster, on to the next within reach of each, never the same one twice.</summary>
public sealed record ChainBlock : PlacedDelivery
{
    /// <summary>Metres to the first.</summary>
    public float Range { get; init; } = 15;
    /// <summary>How wide the caster looks for the first, in degrees.</summary>
    public float Angle { get; init; } = 120;
    /// <summary>How many more after the first: 0 to 8.</summary>
    public int Jumps { get; init; } = 2;
    /// <summary>Metres from one to the next.</summary>
    public float JumpRange { get; init; } = 6;
    public AreaAffects Affects { get; init; } = AreaAffects.Enemies;
    public required IReadOnlyList<EffectRef> Effects { get; init; }
}

/// <summary>Something spawned in front of the caster: a <c>GameTemplates</c> constant or a template's GUID, as a child of the caster, at its level and of its faction.</summary>
public sealed record SummonBlock : PlacedDelivery
{
    public required TemplateRef Template { get; init; }
    /// <summary>1 to 16, side by side.</summary>
    public int Count { get; init; } = 1;
    /// <summary>Metres in front of the caster.</summary>
    public float Ahead { get; init; } = 2;
    /// <summary>When they are taken away. Null: they live on.</summary>
    public double? Seconds { get; init; }
}

/// <summary>A projectile that turns toward what it was aimed at: what the game's own homing projectiles do.</summary>
public sealed record HomingBlock
{
    /// <summary>How fast it turns; the game's own run from 20 to 80.</summary>
    public float TurnSpeed { get; init; } = 20;
    /// <summary>Metres along its aim it looks for what to turn to, when it was given no target.</summary>
    public float SeekRange { get; init; } = 12;
    /// <summary>Seconds it flies straight first.</summary>
    public double Delay { get; init; }
}

// ------------------------------------------------------------------------------------------------------ perks -----

/// <summary>An enhancement on an item, in its <c>Perks</c>. The game's rule applies: it runs while the item is in an equipment slot, once a player has unlocked that enhancement at an upgrade.
/// A weapon's <see cref="WeaponBlock.OnHit"/> is the same effects with no unlock.</summary>
public sealed record PerkBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>The number the tooltip shows in front of the name: <c>"5%"</c>, <c>"12"</c>, <c>"2.5"</c>, <c>"8s"</c>. Null: what a single <see cref="Modifies"/> entry or a single on-hit effect's chance says, else none.</summary>
    public string? Value { get; init; }
    /// <summary>The icon, when it is not the starter's: a <c>GamePerks</c> constant, whose icon it takes.</summary>
    public string? Icon { get; init; }
    /// <summary>Stats changed on whoever wears or wields it: <c>Modifies = { [Stat.MaxHealth] = 40 }</c>.</summary>
    public IDictionary<Stat, double> Modifies { get; init; } = new Dictionary<Stat, double>();
    /// <summary>What every hit they land does to what was hit.</summary>
    public HitsRef? OnHit { get; init; }
    /// <summary>What a hit that kills does to what it killed.</summary>
    public HitsRef? OnKill { get; init; }
    /// <summary>What every hit they take does to whoever dealt it.</summary>
    public HitsRef? OnHitTaken { get; init; }
    /// <summary>Game fields this block does not cover, set on the perk it makes. It wins over what the block sets.</summary>
    [JsonConverter(typeof(PerkTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
    /// <summary>The name this block had before a rename. Ids come from the oldest name, so saves keep it.</summary>
    public string? Was { get; init; }
}

internal sealed class PerkTweakConverter() : TweakConverter("perks");

// -------------------------------------------------------------------------------------------------- abilities -----

/// <summary>Something a character casts that is no staff's: on a key, a chat command, an item, or from code with <see cref="Abilities.Cast"/>. It pays its costs, keeps its cooldown, and then does what its delivery says.
/// Its id is <c>&lt;mod&gt;:&lt;name&gt;</c>, also what <c>/cast</c> takes.</summary>
public sealed record AbilityBlock : IBlock
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>The default key ("G", "Shift+F"); players remap it in the Kiln menu. Binding it needs the mod on the player's game. Null: no key.</summary>
    [ClientsModWhen(".")]
    public string? Key { get; init; }
    /// <summary>A chat command that casts it, without the prefix. <c>/cast &lt;id&gt;</c> always works.</summary>
    public string? Command { get; init; }
    /// <summary>An item that casts it when used. <see cref="CastBlock.Charges"/> is how many one cast takes of it: 0 keeps the item for ever. A cast that is refused gives back what it took.</summary>
    public ItemBlock? Item { get; init; }
    /// <summary>Costs, timing and motion. Without an <see cref="CastBlock.Animation"/> nothing plays and the delivery leaves from the caster's body.</summary>
    public CastBlock? Cast { get; init; }
    /// <summary>Special-ability charge, the bar that fills in combat.</summary>
    public int SpecialCost { get; init; }
    /// <summary>Seconds, per caster.</summary>
    public double Cooldown { get; init; }
    /// <summary>What it aims at: where the caster faces, or the nearest enemy within <see cref="Range"/> (and no enemy: no cast).</summary>
    public AbilityTargeting Aim { get; init; } = AbilityTargeting.Forward;
    /// <summary>Metres, for <see cref="Aim"/>.</summary>
    public double Range { get; init; } = 20;
    /// <summary>How it gets there. Null: only <see cref="OnCast"/>.</summary>
    public IDeliveryBlock? Delivery { get; init; }
    /// <summary>What happens to the caster the moment it leaves.</summary>
    public IReadOnlyList<EffectRef>? OnCast { get; init; }
    /// <summary>Keys and commands work for players only. From an effect or code, anything with what it costs can cast it.</summary>
    public bool PlayersOnly { get; init; } = true;
    /// <summary>The name this block had before a rename.</summary>
    public string? Was { get; init; }
}

/// <summary>An ability: what <see cref="Blocks.Define{T}"/> returned, its name in this mod, or <c>"other-mod:name"</c>.</summary>
[JsonConverter(typeof(AbilityRefConverter))]
public readonly record struct AbilityRef(string Value)
{
    public static implicit operator AbilityRef(string value) => new(value);
    public static implicit operator AbilityRef(BlockHandle<AbilityBlock> handle) => new(handle.ToString());
    public override string ToString() => Value;
}

internal sealed class AbilityRefConverter : JsonConverter<AbilityRef>
{
    public override AbilityRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, AbilityRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

// ---------------------------------------------------------------------------------------------------- talents -----

/// <summary>A node of a skill tree: in the game's own tree with <see cref="Game"/>, or in a <see cref="TalentTreeBlock"/>'s <see cref="TalentTreeBlock.Nodes"/>. It works for as long as a character has it.
/// Either way every player's game has to build the same tree from the same files.</summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record TalentBlock : IBlock
{
    public required string Name { get; init; }
    /// <summary>Null: what <see cref="Modifies"/> says, per level.</summary>
    public string? Description { get; init; }
    /// <summary>Texts per language: <c>["de"] = new ItemTexts { Name = "…", Description = "…" }</c>.</summary>
    public IReadOnlyDictionary<string, ItemTexts>? Texts { get; init; }
    /// <summary>Points per level.</summary>
    public int Cost { get; init; } = 1;
    /// <summary>Levels. 1 is one unlock. The game's tree keeps at most 7.</summary>
    public int MaxLevel { get; init; } = 1;
    /// <summary>What leads here. In a tree: its node names. In the game's tree: talents of this mod by name, or game nodes by <c>"0x&lt;id&gt;"</c>.</summary>
    public IReadOnlyList<string>? Requires { get; init; }
    /// <summary>In a tree: all of <see cref="Requires"/>, not any one. The game's tree has only "any".</summary>
    public bool RequireAll { get; init; }
    /// <summary>In a tree: points spent in it first.</summary>
    public int PointsSpent { get; init; }
    /// <summary>In a tree: the character level needed.</summary>
    public int CharacterLevel { get; init; }
    /// <summary>Stats changed, per level: <c>Modifies = { [Stat.Strength] = 2 }</c> is +2 a level.</summary>
    public IDictionary<Stat, double> Modifies { get; init; } = new Dictionary<Stat, double>();
    /// <summary>What every hit they land does to what was hit. The same at every level.</summary>
    public HitsRef? OnHit { get; init; }
    /// <summary>What a hit that kills does to what it killed.</summary>
    public HitsRef? OnKill { get; init; }
    /// <summary>What every hit they take does to whoever dealt it.</summary>
    public HitsRef? OnHitTaken { get; init; }
    /// <summary>A status they have for as long as they have the talent.</summary>
    public StatusRef? Keeps { get; init; }
    /// <summary>What happens to them when it starts to work: at the unlock, and at each join.</summary>
    public IReadOnlyList<EffectRef>? OnStart { get; init; }
    /// <summary>An ability only those with this talent can cast.</summary>
    public AbilityRef? Unlocks { get; init; }
    /// <summary>A PNG in the mod's folder.</summary>
    public string? Icon { get; init; }
    /// <summary>In a tree: the grid cell. Null: worked out from what leads here.</summary>
    public int? Column { get; init; }
    public int? Row { get; init; }
    /// <summary>A place in the game's own tree. The game's screen draws it and its skill points pay for it. Past the game's room for nodes Kiln's shadow table carries it, and a node here is there from the next start of a dedicated server, which reads its tree before mods load.</summary>
    public GameTreePlace? Game { get; init; }
    /// <summary>The name this block had before a rename. Saves keep what comes from the oldest name.</summary>
    public string? Was { get; init; }
}

/// <summary>Where a talent sits in the game's own tree.</summary>
public sealed record GameTreePlace
{
    /// <summary>Game nodes by <c>"0x&lt;id&gt;"</c> that lead here, as well as what the talent's <see cref="TalentBlock.Requires"/> says. A node must hang on the tree.</summary>
    public IReadOnlyList<string>? From { get; init; }
    /// <summary>Links out of it, to game nodes.</summary>
    public IReadOnlyList<string>? To { get; init; }
    /// <summary>The screen's colour: Warrior, Mage, Ranger or Neutral.</summary>
    public string Class { get; init; } = "Neutral";
    /// <summary>[x, y] from the node it hangs on, in the tree's units. The game's links are about 140 long.</summary>
    public double[]? Offset { get; init; }
    /// <summary>[x, y] in the tree's units, about 1700 to 6500.</summary>
    public double[]? Position { get; init; }
}

/// <summary>A whole tree of its own, on Kiln's screen in the game's look, opened from the pause menu or a key. Kiln keeps each character's unlocks with the character; <c>/talents</c> works on any client.</summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record TalentTreeBlock : IBlock
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Where its points come from. Null: 1, and 1 more per character level.</summary>
    public TalentPointsBlock? Points { get; init; }
    /// <summary>Its entry in the pause menu. Null: <see cref="Name"/>.</summary>
    public string? Menu { get; init; }
    /// <summary>A key that opens it. Null: none.</summary>
    public string? Key { get; init; }
    /// <summary>Everything can be given back for free. False: only one level at a time, last first.</summary>
    public bool Respec { get; init; } = true;
    /// <summary>Node name to talent, inline, by handle or by block name. Keep the names: saves hold them.</summary>
    public IDictionary<string, PartRef<TalentBlock>> Nodes { get; init; } = new Dictionary<string, PartRef<TalentBlock>>();
    /// <summary>The name this block had before a rename. Saves keep what comes from the oldest name.</summary>
    public string? Was { get; init; }
}

/// <summary>Where a tree's points come from.</summary>
public sealed record TalentPointsBlock
{
    /// <summary>What a level 1 character has.</summary>
    public int Start { get; init; } = 1;
    /// <summary>More per character level.</summary>
    public int PerLevel { get; init; } = 1;
    /// <summary>0..1: a share of the game's skill points instead. The game's tree keeps all of its own.</summary>
    public double? ShareOfGame { get; init; }
}

// --------------------------------------------------------------------------------------------- what players do -----

/// <summary>Something a player does or is, which any of the game's states and actions can say: a <see cref="PlayerStateId"/> in C#, the same name or the game's own in JSON
/// (<c>"Gliding"</c>, <c>"HangGliding"</c>, <c>"Glider"</c>). <see cref="PlayerStates.All"/> has every one the running game has, and docs/kiln/modding/player-states.md lists them.
/// Two more say what happens to a consumable: <see cref="Eating"/> and <see cref="Eaten"/>.</summary>
[JsonConverter(typeof(PlayerStateRefConverter))]
public readonly record struct PlayerStateRef(string Name)
{
    /// <summary>A player has started to use a consumable. It can still be stopped, by a rule or by a hit.</summary>
    public static readonly PlayerStateRef Eating = new("eating");
    /// <summary>The consumable has really gone.</summary>
    public static readonly PlayerStateRef Eaten = new("eaten");

    public static implicit operator PlayerStateRef(string name) => new(name);
    public static implicit operator PlayerStateRef(PlayerStateId state) => new($"0x{(ushort)state:x4}");
    public override string ToString() => Name;
}

internal sealed class PlayerStateRefConverter : JsonConverter<PlayerStateRef>
{
    public override PlayerStateRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, PlayerStateRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Name);
}

/// <summary>When a player can use a consumable. Refused, the sequence stops before it uses the item up: the item stays and gives nothing. An item whose sequence uses it up in its first moment cannot be
/// refused, and a rule on one is an error by name when content is ready.</summary>
public sealed record EatRule
{
    /// <summary>Only while the player is in one of these.</summary>
    public IReadOnlyList<PlayerStateRef>? When { get; init; }
    /// <summary>Not while the player is in any of these.</summary>
    public IReadOnlyList<PlayerStateRef>? Unless { get; init; }
    /// <summary>From code: your own check, on the tick thread. JSON has only <see cref="When"/> and <see cref="Unless"/>.</summary>
    [JsonIgnore]
    public Func<Player, bool>? If { get; init; }
    /// <summary>What happens to the player when it is refused.</summary>
    public IReadOnlyList<EffectRef>? OnRefused { get; init; }
}

/// <summary>Any state or action of a player drives effects. Nothing is watched until a mod has a trigger, and then only what triggers ask for.
/// Runs on the server world: a dedicated server, or a game's host.</summary>
[BlockClients(ClientsLevel.None)]
public sealed record TriggerBlock : IBlock
{
    /// <summary>A state entered, an action started, or <see cref="PlayerStateRef.Eating"/> and <see cref="PlayerStateRef.Eaten"/>.</summary>
    public required PlayerStateRef On { get; init; }
    /// <summary>Only with this item: what an action uses, what is eaten, and for a state what is in hand or worn. A <c>GameItems</c> constant, or an item of this mod.</summary>
    public ItemRef? Item { get; init; }
    /// <summary>Only while the player is in one of these.</summary>
    public IReadOnlyList<PlayerStateRef>? When { get; init; }
    /// <summary>Not while the player is in any of these.</summary>
    public IReadOnlyList<PlayerStateRef>? Unless { get; init; }
    /// <summary>From code: your own check, on the tick thread. JSON has only <see cref="When"/> and <see cref="Unless"/>.</summary>
    [JsonIgnore]
    public Func<Player, bool>? If { get; init; }
    /// <summary>Seconds, per player, before it can happen again.</summary>
    public double Cooldown { get; init; }
    /// <summary>What happens to the player.</summary>
    public IReadOnlyList<EffectRef>? Do { get; init; }
    /// <summary>What happens to the player when they leave the state. A state only: an action, or a meal, has no such moment here.</summary>
    public IReadOnlyList<EffectRef>? OnEnd { get; init; }
    /// <summary>Stops the action, or the eating. What it did in its first moment has happened; a state cannot be stopped.</summary>
    public bool Stop { get; init; }
    /// <summary>The name this block had before a rename. Only its programs' keys follow from it.</summary>
    public string? Was { get; init; }
}
