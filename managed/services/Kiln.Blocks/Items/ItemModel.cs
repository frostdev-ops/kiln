// SPDX-License-Identifier: MIT
// The item blocks: what a mod author writes for things that go in a bag, a hand, or on the body. C# records, and the same shape in
// blocks/*.json. No base to name and no GUIDs: a hidden starter comes from the block's kind, class or slot. A part (a look, stats, a class,
// a slot, an on-hit) is a block of its own: inline, from Blocks.Define, or by name. A premade such as WeaponClass.Sword is a plain instance.
// docs/kiln/modding/item-blocks.md.
using System.Collections;
using System.Text.Json.Serialization;

namespace Kiln;

// ------------------------------------------------------------------------------------------------------ parts -----

/// <summary>A part of a block: inline, from <see cref="Blocks.Define{T}"/>, a premade's key, or a block's name.</summary>
[JsonConverter(typeof(PartRefConverterFactory))]
public sealed class PartRef<T> where T : class, IBlock
{
    internal PartRef(T? inline, string? name) { Inline = inline; Name = name; }
    public T? Inline { get; }
    public string? Name { get; }
    public static implicit operator PartRef<T>(T block) => new(block, null);
    public static implicit operator PartRef<T>(BlockHandle<T> handle) => new(handle.Block, handle.ToString());
    public static implicit operator PartRef<T>(string name) => new(null, name);
}

/// <summary>What an item looks like. Empty: its starter's look.</summary>
public sealed record VisualBlock : IBlock
{
    /// <summary>A game item, or an item of this mod, whose whole look this item takes: held or worn model, dropped model, collection model and icon.</summary>
    public ItemRef? Like { get; init; }
    /// <summary>The model alone: <c>item:&lt;name&gt;</c>, or the GUID of a model your <c>assets/assets.json</c> serves (then <c>clients: mod</c>).</summary>
    public string? Model { get; init; }
    /// <summary>The icon alone: <c>item:&lt;name&gt;</c>, or a PNG in the mod's folder (then <c>clients: mod</c>).</summary>
    public string? Icon { get; init; }
    /// <summary>A game item, or an item of this mod, whose model this item shows lying on the ground.</summary>
    public ItemRef? Dropped { get; init; }
    /// <summary><c>#rrggbb</c>: the colour of the parts the game lets you tint. A client check: nothing here draws it.</summary>
    public string? Tint { get; init; }
    /// <summary>A VFX on the thing itself, for as long as it is there: in the hand while it is held (right hand by default, at the grip; <see cref="VfxRef.Offset"/> moves it
    /// up the blade), or on what stands there for a placed thing, as a prop's light. A VFX of what happens goes on that: an on-hit, a status, an effect.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
}

/// <summary>What an item does to what its wearer hits. A list of effects works wherever this is asked for: <c>OnHit = [burst]</c>.</summary>
public sealed record OnHitBlock : IBlock
{
    public IReadOnlyList<EffectRef> Effects { get; init; } = [];
    /// <summary>A VFX where a hit lands, as an effect with only a <see cref="EffectBlock.Vfx"/> would show it.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
}

/// <summary>Effects on a hit: <c>[burst]</c>, an <see cref="OnHitBlock"/> inline or from <see cref="Blocks.Define{T}"/>, or a block's name.</summary>
[JsonConverter(typeof(HitsRefConverter))]
public sealed class HitsRef : IEnumerable<EffectRef>
{
    private readonly List<EffectRef> _list = new();
    public HitsRef() { }
    internal HitsRef(PartRef<OnHitBlock> part) => Part = part;
    /// <summary>The on-hit block this names, when it is not a plain list.</summary>
    public PartRef<OnHitBlock>? Part { get; }
    /// <summary>The list form: <c>OnHit = [burst, other]</c>.</summary>
    public void Add(EffectRef effect) => _list.Add(effect);
    public int Count => _list.Count;
    public IEnumerator<EffectRef> GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public static implicit operator HitsRef(OnHitBlock block) => new(block);
    public static implicit operator HitsRef(BlockHandle<OnHitBlock> handle) => new(handle);
    public static implicit operator HitsRef(string name) => new((PartRef<OnHitBlock>)name);
}

/// <summary>A perk in an enhancement slot: a game perk's name or GUID (<c>GamePerks</c>), <c>item:&lt;name&gt;</c> (that item's perk in the same slot),
/// or <c>none</c>, or a <see cref="PerkBlock"/> by handle or name. The game's rule applies: it runs once a player has unlocked its slot.</summary>
[JsonConverter(typeof(PerkRefConverter))]
public readonly record struct PerkRef(string? Value)
{
    public static implicit operator PerkRef(string? value) => new(value);
    public static implicit operator PerkRef(BlockHandle<PerkBlock> handle) => new(handle.ToString());
    public override string ToString() => Value ?? "";
}

// ---------------------------------------------------------------------------------------------- what a made item is -----

/// <summary>What a made item is: its colour, and how many enhancements it can have (one more than the rank).</summary>
public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }

// ---------------------------------------------------------------------------------------------------- weapons -----

/// <summary>A weapon that is exactly what its recipe tooltip shows (the crafted-item rule is built in).</summary>
public sealed record WeaponBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Where to get it, in the tooltip. Null: nothing. The same goes for <see cref="Description"/>: a block does not take the texts of what it is made from.</summary>
    public string? Source { get; init; }
    /// <summary>How it is held and swung: <c>WeaponClass.Sword</c>, or your own.</summary>
    public required PartRef<WeaponClass> Class { get; init; }
    public PartRef<VisualBlock>? Visual { get; init; }
    public PartRef<WeaponStats>? Stats { get; init; }
    /// <summary>Enhancement slots, from the first. A slot you leave out keeps the class's perk. The game's rule applies: a perk runs once its slot is unlocked.</summary>
    public IReadOnlyList<PerkRef>? Perks { get; init; }
    /// <summary>What every hit its wielder lands does to the target, while it is in hand, with no unlock. Damage dealt by an effect does not count as a hit here.</summary>
    public HitsRef? OnHit { get; init; }
    /// <summary>What a hit that kills does to what it killed, while it is in hand.</summary>
    public HitsRef? OnKill { get; init; }
    public RecipeBlock? Recipe { get; init; }
    public LootBlock? Loot { get; init; }
    /// <summary>The name this block had before a rename. Ids come from the oldest name, so saves keep it.</summary>
    public string? Was { get; init; }
    /// <summary>What its attack slots play: its own motions, timing and chains, slot by slot. Null: its class's (docs/kiln/modding/item-blocks.md, "Moves").</summary>
    public WeaponMoves? Moves { get; init; }

    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

public sealed record WeaponStats : IBlock
{
    /// <summary>The tooltip's level; a made weapon has exactly this.</summary>
    public int Level { get; init; } = 1;
    /// <summary>The tooltip's damage at that level. Null: what the class has at that level; with <see cref="Damage"/>, its total.</summary>
    public int? Power { get; init; }
    public Rarity Rarity { get; init; } = Rarity.Common;
    /// <summary>The kinds it deals. The shares of the kinds that hurt are what counts, <c>Wood</c>, <c>Stone</c> and <c>Metal</c> are exact. Null: the class's.</summary>
    public DamageBlock? Damage { get; init; }
    /// <summary>A shield's block strength, 0..1. Null: the class's.</summary>
    public double? Block { get; init; }
    /// <summary>Stamina each use costs; the tooltip follows. Null: the class's.</summary>
    public int? StaminaCost { get; init; }
}

/// <summary>A class of weapon: how it is held and swung, which a look and stats can start from. The premades are plain instances; write your own with
/// <c>WeaponClass.Sword with { Visual = look }</c>, or in JSON <c>{ "like": "sword", "visual": … }</c>. Your own animations come with animcook.</summary>
public sealed record WeaponClass : IBlock, IPremade
{
    public static readonly WeaponClass Sword = new() { Key = "sword", Starter = "Weapon_T1_1H_Sword_Craft", PerksFrom = "Weapon_T1_1H_Sword_01" };
    public static readonly WeaponClass Axe = new() { Key = "axe", Starter = "Weapon_T1_1H_Axe_01" };
    public static readonly WeaponClass Mace = new() { Key = "mace", Starter = "Weapon_T1_1H_Mace_Craft", PerksFrom = "Weapon_T1_1H_MaceEpic_01" };
    public static readonly WeaponClass Greatsword = new() { Key = "greatsword", Starter = "Weapon_T1_2H_GreatSword_01" };
    public static readonly WeaponClass Greataxe = new() { Key = "greataxe", Starter = "Weapon_T1_2H_GreatAxe_01" };
    public static readonly WeaponClass Hammer = new() { Key = "hammer", Starter = "Weapon_T1_2H_Hammer_01" };
    public static readonly WeaponClass Daggers = new() { Key = "daggers", Starter = "Weapon_T1_2H_Daggers_01_DPS" };
    public static readonly WeaponClass Bow = new() { Key = "bow", Starter = "Weapon_T1_2H_Bow_01" };
    public static readonly WeaponClass Staff = new() { Key = "staff", Starter = "Weapon_T1_2H_Staff_Craft", PerksFrom = "Weapon_T1_2H_Staff_01" };
    public static readonly WeaponClass Shield = new() { Key = "shield", Starter = "Shield_T1_DPS" };
    /// <summary>What <see cref="WandBlock"/> starts from. A weapon block of this class would swing the game's own spells: use a wand block.</summary>
    public static readonly WeaponClass Wand = new() { Key = "wand", Starter = "Weapon_T1_1H_Wand_02_Ice" };
    internal static readonly WeaponClass[] All = [Sword, Axe, Mace, Greatsword, Greataxe, Hammer, Daggers, Bow, Staff, Shield, Wand];

    /// <summary>How JSON names a premade.</summary>
    public string Key { get; init; } = "";
    /// <summary>In JSON, the premade this one starts from: <c>{ "like": "sword", … }</c>. In C#: <c>WeaponClass.Sword with { … }</c>.</summary>
    public string? Like { get; init; }
    /// <summary>The look its weapons have unless they say otherwise.</summary>
    public PartRef<VisualBlock>? Visual { get; init; }
    /// <summary>The stats its weapons have unless they say otherwise.</summary>
    public PartRef<WeaponStats>? Stats { get; init; }
    /// <summary>The perks its weapons have unless they say otherwise.</summary>
    public IReadOnlyList<PerkRef>? Perks { get; init; }
    internal string? Starter { get; init; }
    /// <summary>The item the enhancement slots above the starter's come from.</summary>
    internal string? PerksFrom { get; init; }
}

/// <summary>A one-handed caster whose attacks are spells of its own: it needs no spell item, and never runs out of charges.</summary>
public sealed record WandBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Where to get it, in the tooltip. Null: nothing.</summary>
    public string? Source { get; init; }
    /// <summary>The primary attack. Its main damage is the wand's power: the tooltip's number, and what perks and stats scale.</summary>
    public required WandAttack Primary { get; init; }
    /// <summary>The heavy attack. Null: the primary again. Its damage is exactly what it says.</summary>
    public WandAttack? Heavy { get; init; }
    /// <summary>The special ability attack, once a player has the game's skill for it. Null: the primary again. Its damage is exactly what it says.</summary>
    public WandAttack? Special { get; init; }
    public PartRef<WeaponClass>? Class { get; init; }
    public PartRef<VisualBlock>? Visual { get; init; }
    public PartRef<WeaponStats>? Stats { get; init; }
    /// <inheritdoc cref="WeaponBlock.Perks"/>
    public IReadOnlyList<PerkRef>? Perks { get; init; }
    /// <inheritdoc cref="WeaponBlock.OnHit"/>
    public HitsRef? OnHit { get; init; }
    /// <inheritdoc cref="WeaponBlock.OnKill"/>
    public HitsRef? OnKill { get; init; }
    public RecipeBlock? Recipe { get; init; }
    public LootBlock? Loot { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }

    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

/// <summary>One of a wand's attacks: how it is cast and what it sends.</summary>
public sealed record WandAttack
{
    /// <summary>The mana it costs and when it leaves the wand. <see cref="CastBlock.Animation"/> is a staff's: a wand's comes from <see cref="Moves"/>.</summary>
    public required CastBlock Cast { get; init; }
    public required IDeliveryBlock Delivery { get; init; }
    /// <summary>The motion. Null: the attack slot's own.</summary>
    public WandMoves? Moves { get; init; }
}

/// <summary>A wand's attack motion, with its timing. Premade instances; <c>WandMoves.Heavy with { Animation = …, HitAt = 1.1 }</c> plays another state or moves the release
/// (in JSON <c>{ "like": "heavy", "animation": …, "hitAt": 1.1 }</c>; without <c>like</c>, the attack's own motion).</summary>
[JsonConverter(typeof(MotionConverter<WandMoves>))]
public sealed record WandMoves(string Key, string Sequence, double ReleaseSeconds, int[] Fires, int[] Silenced) : IPremade
{
    /// <summary>The state the motion plays, as <see cref="MoveBlock.Animation"/> takes it. Null: the motion's own.</summary>
    public MoveAnimation? Animation { get; init; }
    /// <summary>Seconds into the motion the attack leaves the wand: the release, as <see cref="CastBlock.ReleaseAt"/> (give one of them, or the same number). The rest of the
    /// motion is scaled to fit before it and shifted after it. Null: the motion's own.</summary>
    public double? HitAt { get; init; }
    /// <summary>JSON's object with no <c>like</c>: the attack's own motion, with what the object changes.</summary>
    internal static readonly WandMoves Unset = new("", "", 0, [], []);

    /// <summary>The quick bolt.</summary>
    public static readonly WandMoves Bolt = new("bolt", "Attack_Player_Wand_IceChain_normal", 0.35, [0, 1], []);
    /// <summary>The long wind-up.</summary>
    public static readonly WandMoves Heavy = new("heavy", "Attack_Player_Wand_AllElements_Heavy", 1.033, [0, 5], [1, 2, 3, 4, 6, 7, 8, 9]);
    /// <summary>The special ability's throw.</summary>
    public static readonly WandMoves Special = new("special", "Attack_Player_Wand_SpecialAbility_Magnetic_Projectile", 0.35, [0], [1, 2, 3, 4]);
    internal static readonly WandMoves[] All = [Bolt, Heavy, Special];
}

// ------------------------------------------------------------------------------------------------------ armor -----

/// <summary>Something worn: its slot, its stats and its look. Made exactly as its recipe tooltip shows.</summary>
public sealed record ArmorBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Where to get it, in the tooltip. Null: nothing.</summary>
    public string? Source { get; init; }
    public required PartRef<ArmorSlot> Slot { get; init; }
    /// <summary>A family of looks: this piece takes the look the set has for its slot. <see cref="Visual"/> and <see cref="Look"/> win over it.</summary>
    public PartRef<ArmorSet>? Set { get; init; }
    public PartRef<VisualBlock>? Visual { get; init; }
    /// <summary>What it looks like on the body: a model of the mod's own, skinned or on one joint, for both bodies (docs/kiln/modding/cosmetics.md). It wins over <see cref="Visual"/> for what is
    /// worn; <see cref="Visual"/> still gives the dropped model and the tint.</summary>
    public PartRef<WornLook>? Look { get; init; }
    public PartRef<ArmorStats>? Stats { get; init; }
    /// <inheritdoc cref="WeaponBlock.Perks"/>
    public IReadOnlyList<PerkRef>? Perks { get; init; }
    /// <summary>What every hit the wearer takes does to whoever dealt it, while it is worn, with no unlock.</summary>
    public HitsRef? OnHitTaken { get; init; }
    public RecipeBlock? Recipe { get; init; }
    public LootBlock? Loot { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }

    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

/// <summary>What a piece of armor gives. A null number: what its slot has at that level.</summary>
public sealed record ArmorStats : IBlock
{
    /// <summary>The tooltip's level; a made piece has exactly this.</summary>
    public int Level { get; init; } = 1;
    public Rarity Rarity { get; init; } = Rarity.Common;
    /// <summary>The tooltip's physical armour.</summary>
    public int? Physical { get; init; }
    /// <summary>The tooltip's magical armour.</summary>
    public int? Magical { get; init; }
    /// <summary>Armour against one kind, on top of the two above.</summary>
    public ArmorResist? Resist { get; init; }
    /// <summary>What wearing it adds: <c>{ [EquipBonus.Health] = 60 }</c>. A share (<c>EquipBonus.MeleeDamage</c>) is 0.05 for 5%.</summary>
    public IDictionary<EquipBonus, double>? Bonus { get; init; }
}

/// <summary>Armour against one kind.</summary>
public sealed record ArmorResist
{
    public int Cut { get; init; }
    public int Pierce { get; init; }
    public int Blunt { get; init; }
    public int Fire { get; init; }
    public int Ice { get; init; }
    public int Shroud { get; init; }
    public int Lightning { get; init; }
}

/// <summary>Where a piece is worn. The premades are plain instances: <c>ArmorSlot.Chest with { Visual = look }</c>, in JSON <c>{ "like": "chest", … }</c>.</summary>
public sealed record ArmorSlot : IBlock, IPremade
{
    public static readonly ArmorSlot Head = new() { Key = "head", Starter = "Armor_T0_Fur_Helmet" };
    public static readonly ArmorSlot Chest = new() { Key = "chest", Starter = "Armor_T0_Fur_Chest" };
    public static readonly ArmorSlot Arms = new() { Key = "arms", Starter = "Armor_T0_Fur_Arms" };
    public static readonly ArmorSlot Legs = new() { Key = "legs", Starter = "Armor_T0_Fur_Legs" };
    public static readonly ArmorSlot Feet = new() { Key = "feet", Starter = "Armor_T0_Fur_Boots" };
    public static readonly ArmorSlot Ring = new() { Key = "ring", Starter = "Ring_T1_Ring_of_Health", ClearConfigs = ["0x9d74c0c0", "0xc24ecbbb"] };
    internal static readonly ArmorSlot[] All = [Head, Chest, Arms, Legs, Feet, Ring];

    /// <summary>How JSON names a premade.</summary>
    public string Key { get; init; } = "";
    /// <summary>In JSON, the premade this one starts from.</summary>
    public string? Like { get; init; }
    public PartRef<VisualBlock>? Visual { get; init; }
    public PartRef<ArmorStats>? Stats { get; init; }
    internal string? Starter { get; init; }
    /// <summary>Stat configs of the starter that are not this block's: zeroed unless the block's bonus names them.</summary>
    internal string[] ClearConfigs { get; init; } = [];
}

/// <summary>A family of looks, one per slot. The premades are plain instances; write your own from <see cref="VisualBlock"/>s or <see cref="WornLook"/>s.</summary>
public sealed record ArmorSet : IBlock, IPremade
{
    private static ArmorSet From(string key, string stem) => new()
    {
        Key = key,
        Head = new VisualBlock { Like = stem + "Helmet" }, Chest = new VisualBlock { Like = stem + "Chest" }, Arms = new VisualBlock { Like = stem + "Arms" },
        Legs = new VisualBlock { Like = stem + "Legs" }, Feet = new VisualBlock { Like = stem + "Boots" },
    };
    public static readonly ArmorSet Warrior = From("warrior", "Armor_T1_Warrior_DPS_");
    public static readonly ArmorSet Mage = From("mage", "Armor_T1_Mage_Wizard_DPS_");
    public static readonly ArmorSet Ranger = From("ranger", "Armor_T1_Ranger_Archer_DPS_");
    internal static readonly ArmorSet[] All = [Warrior, Mage, Ranger];

    /// <summary>How JSON names a premade.</summary>
    public string Key { get; init; } = "";
    public ArmorLook? Head { get; init; }
    public ArmorLook? Chest { get; init; }
    public ArmorLook? Arms { get; init; }
    public ArmorLook? Legs { get; init; }
    public ArmorLook? Feet { get; init; }
}

/// <summary>A stat a piece of gear adds while it is worn. The game turns the item's configs into the wearer's stats and the tooltip follows them.</summary>
[JsonConverter(typeof(EquipBonusConverter))]
public sealed record EquipBonus(string Key, string Config, uint Row, string Format, bool Whole) : IPremade
{
    public static readonly EquipBonus Health = new("health", "0x9d74c0c0", 0xf38b388c, "Normal", true);
    public static readonly EquipBonus Stamina = new("stamina", "0x6fcbc14e", 0x61cea4c1, "Normal", true);
    public static readonly EquipBonus Mana = new("mana", "0xcdfaf8a5", 0xda23cb46, "Normal", true);
    public static readonly EquipBonus HealthRegen = new("health-regen", "0xc24ecbbb", 0xbb67c94d, "Normal", true);
    public static readonly EquipBonus StaminaRegen = new("stamina-regen", "0xa22ca171", 0x63049452, "Normal", true);
    public static readonly EquipBonus ManaRegen = new("mana-regen", "0x4e133b42", 0x6835c34a, "Normal", true);
    public static readonly EquipBonus MeleeDamage = new("melee-damage", "0x0f614860", 0xf4f5c558, "Percentage", false);
    public static readonly EquipBonus RangedDamage = new("ranged-damage", "0xc0d6755c", 0xbfdc0793, "Percentage", false);
    public static readonly EquipBonus MagicDamage = new("magic-damage", "0x4bf0438d", 0x81ec5961, "Percentage", false);
    public static readonly EquipBonus CritChance = new("crit-chance", "0xd98a69f9", 0x990abdba, "Percentage", false);
    public static readonly EquipBonus CritDamage = new("crit-damage", "0xfbf8f864", 0xdfca9aa8, "Percentage", false);
    public static readonly EquipBonus FrostResist = new("frost-resist", "0x424ee99c", 0x49f4ff5b, "Normal", true);
    public static readonly EquipBonus SprintSpeed = new("sprint-speed", "0x0a0c87e7", 0xbd98219d, "Percentage", false);
    internal static readonly EquipBonus[] All = [Health, Stamina, Mana, HealthRegen, StaminaRegen, ManaRegen, MeleeDamage, RangedDamage, MagicDamage, CritChance, CritDamage, FrostResist, SprintSpeed];
    public override string ToString() => Key;
}

// ------------------------------------------------------------------------------------------------------ items -----

/// <summary>Anything else that goes in a bag: a material, a potion, food, an arrow, a tool. Its kind follows from what you fill in, or say it.</summary>
public sealed record ItemBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Where to get it, in the tooltip. Null: nothing.</summary>
    public string? Source { get; init; }
    /// <summary>Null: a <see cref="Tool"/> makes a tool, <see cref="Ammo"/> an arrow, <see cref="Feeds"/> food, <see cref="Heals"/> or <see cref="OnUse"/> a potion, the rest a material.</summary>
    public ItemKind? Kind { get; init; }
    public PartRef<VisualBlock>? Visual { get; init; }
    /// <summary>How many fit in a stack. Null: the kind's own. Something with an item entity (a tool) is always 1.</summary>
    public int? Stack { get; init; }
    public Rarity? Rarity { get; init; }
    /// <summary>The tooltip's level, for what has one.</summary>
    public int? Level { get; init; }
    /// <summary>Texts per language: <c>["de"] = new ItemTexts { Name = "…" }</c>. <see cref="Name"/> and <see cref="Description"/> are every other language's.</summary>
    public IReadOnlyDictionary<string, ItemTexts>? Texts { get; init; }

    /// <summary>Health using it gives at once; the tooltip's number.</summary>
    public int? Heals { get; init; }
    /// <summary>What using it does, to whoever uses it.</summary>
    public IReadOnlyList<EffectRef>? OnUse { get; init; }
    /// <summary>The status eating it gives, in the game's food slots. Its tooltip's duration is the status's.</summary>
    public StatusRef? Feeds { get; init; }
    /// <summary>What happens to whoever used it, once the item has really gone. A hit that stops the use before that stops this too.</summary>
    public IReadOnlyList<EffectRef>? OnEat { get; init; }
    /// <summary>When it can be used. Refused, the item stays and it gives nothing.</summary>
    public EatRule? CanEat { get; init; }

    /// <summary>Seconds it burns in a fire.</summary>
    public double? Fuel { get; init; }
    /// <summary>The comfort a placed one gives.</summary>
    public int? Comfort { get; init; }
    public ToolBlock? Tool { get; init; }
    public AmmoBlock? Ammo { get; init; }
    /// <summary>A <see cref="ItemKind.WateringCan"/>'s or <see cref="ItemKind.WaterPouch"/>'s capacity, in charges as the game counts them: the game's watering can holds 100,
    /// its pouch 20 sips. 1..1023: a player's game is sent the water as charges × 64 in 16 bits. Null: the kind's own.</summary>
    public int? Holds { get; init; }
    /// <summary>How fast a water container fills and pours, as a multiple of the game's own: at a lake, at a well or a barrel, and onto crops and soil alike. The game
    /// moves the item's Volume (32 for the can, 16 for the pouch: half a charge, a quarter) about 20 times a second while the button is held, and has no rate for
    /// filling alone. 0.25..8. Null: the game's.</summary>
    public double? Flow { get; init; }

    public RecipeBlock? Recipe { get; init; }
    public LootBlock? Loot { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }

    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

/// <summary>What sort of item it is: one of these, each checked for the fields it uses. <see cref="Starter"/> is the game item it starts from. There is no kind of
/// your own: an item that starts from a game item you name is <c>Content.Items.Register</c>'s (docs/kiln/services/content.md).</summary>
[JsonConverter(typeof(PremadeConverter<ItemKind>))]
public sealed record ItemKind : IPremade
{
    private ItemKind(string key, string starter, string? water = null, int volume = 0) => (Key, Starter, Water, Volume) = (key, starter, water, volume);

    public static readonly ItemKind Material = new("material", "Material_T1_Bones");
    public static readonly ItemKind Consumable = new("consumable", "Consumable_Potion_Antidote_Poison_T2");
    public static readonly ItemKind Food = new("food", "Food_T1_raw_sweets_Honey");
    public static readonly ItemKind Ammo = new("ammo", "Ammo_T1_Arrow_Flint_lvl5");
    public static readonly ItemKind Tool = new("tool", "Tool_T1_Axe_Scrap_Grassland");
    /// <summary>The game's watering can: filled at a lake or a well, poured on crops and soil. <see cref="ItemBlock.Holds"/> says how much.</summary>
    public static readonly ItemKind WateringCan = new("watering-can", "Tool_T1_WateringCan", "Pide_Fuel_Watering_Can", 32);
    /// <summary>The game's water pouch: filled at a lake or a well, a sip at a time to drink. <see cref="ItemBlock.Holds"/> says how many sips.</summary>
    public static readonly ItemKind WaterPouch = new("water-pouch", "Tool_Water_Pouch_Refillable", "Pide_Fuel_Water_Pouch", 16);
    internal static readonly ItemKind[] All = [Material, Consumable, Food, Ammo, Tool, WateringCan, WaterPouch];

    /// <summary>How JSON names it.</summary>
    public string Key { get; }
    /// <summary>The game item it starts from.</summary>
    public string Starter { get; }
    /// <summary>A water container's: the template its water lives in (the item's own entity, <c>pidEntity</c>), which every item of the kind shares.</summary>
    internal string? Water { get; }
    /// <summary>A water container's: the water one step of filling or pouring moves, in the game's units (64 a charge): its item's Volume config.</summary>
    internal int Volume { get; }
    internal bool HoldsWater => Water != null;
}

/// <summary>What a tool does: what it is for, and how well. Each class takes the fields it has a number for, and refuses the others by name.</summary>
public sealed record ToolBlock
{
    /// <summary>What it is for: an axe takes wood, a pickaxe stone and metal and digs, a rake flattens the ground, a glider flies, a grappling hook pulls, a fishing rod
    /// fishes, a torch burns. The premades are plain instances.</summary>
    public required ToolClass Harvests { get; init; }
    /// <summary>How hard it bites: an axe's wood damage, a pickaxe's or a sieve's stone damage, a rake's flattening strength (the game's rakes: 500 and 1000). Null: the class's.
    /// For an axe or a pickaxe the rest of what it can do scales along.</summary>
    public int? Power { get; init; }
    /// <summary>Stamina each use costs; the tooltip follows. Null: the class's.</summary>
    public int? StaminaCost { get; init; }
    /// <summary>Metres: how far a rake flattens around where it aims (the game's: 3), how far a grappling hook reaches (13.5). Null: the class's.</summary>
    public double? Range { get; init; }
    /// <summary>Seconds a torch burns while it is held, the tooltip's Duration (the game's torch: 600). Then it becomes <see cref="BreaksInto"/>. Null: the class's.</summary>
    public double? Durability { get; init; }
    /// <summary>What it becomes when its durability runs out, as the game's torch becomes a burnt one: a game item or an item of this mod. Null: the class's.</summary>
    public ItemRef? BreaksInto { get; init; }
    /// <summary>A fishing rod's pull and stamina. Null: the class's.</summary>
    public FishingBlock? Fishing { get; init; }
}

/// <summary>How a fishing rod fights a fish.</summary>
public sealed record FishingBlock
{
    /// <summary>How hard it pulls: the tooltip's Strength (the game's simple rod: 1, the improved one: 2).</summary>
    public double? Strength { get; init; }
    /// <summary>How long it holds out: the tooltip's Endurance (30 and 45).</summary>
    public double? Endurance { get; init; }
}

/// <summary>What a tool class does, which decides which <see cref="ToolBlock"/> fields it takes.</summary>
public enum ToolUse { Harvest, Flatten, Glide, Hook, Fish, Light }

/// <summary>A class of tool, with its starter and the number <see cref="ToolBlock.Power"/> scales from.</summary>
[JsonConverter(typeof(PremadeConverter<ToolClass>))]
public sealed record ToolClass(string Key, string Starter, int WoodDamage, int StoneDamage, int MetalDamage, int MainPower, int Slots, bool Digs) : IPremade
{
    public static readonly ToolClass Axe = new("wood", "Tool_T1_Axe_Scrap_Grassland", 20, 4, 2, 20, 3, false);
    public static readonly ToolClass Pickaxe = new("stone", "Tool_T1_Pickaxe_Scrap_Grassland", 8, 12, 8, 12, 3, true);
    /// <summary>The game's rake: it flattens the ground where it aims, in a base.</summary>
    public static readonly ToolClass Rake = new("rake", "Tool_T0_Rake_Wooden", 0, 0, 0, 500, 3, false) { Use = ToolUse.Flatten };
    /// <summary>The game's mining sieve: stone, metal and wood, and it digs as deep as the game's sieve.</summary>
    public static readonly ToolClass Sieve = new("sieve", "Tool_T6_GoldSieve_Prospector_Wetlands", 14, 20, 14, 20, 3, false);
    /// <summary>The game's glider, in its own slot.</summary>
    public static readonly ToolClass Glider = new("glider", "Tool_Glider_T1", 0, 0, 0, 0, 0, false) { Use = ToolUse.Glide };
    /// <summary>The game's grappling hook, in its own slot.</summary>
    public static readonly ToolClass GrapplingHook = new("grappling-hook", "Tool_Grappling_Hook", 0, 0, 0, 0, 0, false) { Use = ToolUse.Hook };
    /// <summary>The game's simple fishing rod.</summary>
    public static readonly ToolClass FishingRod = new("fishing-rod", "Tool_T1_FishingRod_basic", 0, 0, 0, 0, 0, false) { Use = ToolUse.Fish };
    /// <summary>The game's torch: held, it lights and burns down.</summary>
    public static readonly ToolClass Torch = new("torch", "Tool_Torch01", 3, 1, 1, 3, 0, false) { Use = ToolUse.Light };
    internal static readonly ToolClass[] All = [Axe, Pickaxe, Rake, Sieve, Glider, GrapplingHook, FishingRod, Torch];

    /// <summary>What it does.</summary>
    public ToolUse Use { get; init; } = ToolUse.Harvest;
    /// <summary>It has an item entity with enhancement slots, level and rarity, as the axe's.</summary>
    internal bool Gear => Slots > 0;
}

/// <summary>Shorthands for <see cref="ToolBlock.Harvests"/>.</summary>
public static class Harvest
{
    /// <summary>An axe.</summary>
    public static readonly ToolClass Wood = ToolClass.Axe;
    /// <summary>A pickaxe: stone, metal, and digging.</summary>
    public static readonly ToolClass Stone = ToolClass.Pickaxe;
    /// <summary>A rake: it flattens the ground.</summary>
    public static readonly ToolClass Ground = ToolClass.Rake;
}

/// <summary>What an arrow adds to the bow that shoots it.</summary>
public sealed record AmmoBlock
{
    /// <summary>The power it adds to the bow's. Null: <see cref="Damage"/>'s total, or the kind's own.</summary>
    public int? Power { get; init; }
    /// <summary>The kinds it deals: the shares count, as for a weapon. Null: the kind's own (pierce).</summary>
    public DamageBlock? Damage { get; init; }
    /// <summary>A flight of its own. Null: an arrow's.</summary>
    public IDeliveryBlock? Delivery { get; init; }
}

// ----------------------------------------------------------------------------------------------- how it is made -----

/// <summary>How something is crafted. On an item, a prop or a station it says how that block is made. On its own (in <c>blocks/*.json</c> under <c>"recipes"</c>, or
/// <c>Blocks.Define("name", new RecipeBlock { Output = ... })</c>) it is one more recipe at a station, for any item: a game item at the game's workbench, or an item of yours
/// at a game station. Alone it needs <see cref="Output"/>.</summary>
public sealed record RecipeBlock : IBlock
{
    /// <summary><see cref="Stations.Hand"/>, a <c>GameWorkshops</c> constant, or a <see cref="StationBlock"/> of this mod.</summary>
    public StationRef Station { get; init; } = Stations.Hand;
    /// <summary>How many one craft gives.</summary>
    public int Makes { get; init; } = 1;
    /// <summary>Item and count: <c>GameItems</c> constants, or an item this mod defines.</summary>
    public IDictionary<ItemRef, int> Needs { get; init; } = new Dictionary<ItemRef, int>();
    /// <summary>Seconds at a timed station. Null: the station's own.</summary>
    public double? Seconds { get; init; }
    /// <summary>What it makes: in a station's <see cref="StationBlock.Recipes"/>, and in a recipe block of its own, where it is needed. Elsewhere a recipe makes the block it is on.</summary>
    public ItemRef? Output { get; init; }
    /// <summary>Fuel: what each cycle burns. It is an input, as in the game's data, and a station with none left stops the same way: charcoal in a forge is one.</summary>
    public IDictionary<ItemRef, int> Burns { get; init; } = new Dictionary<ItemRef, int>();
    /// <summary>A player crafts it only under a roof, the game's rule for most bench recipes. A station that works on its own doesn't check it, so its recipes refuse it.</summary>
    public bool? Sheltered { get; init; }
    /// <summary>The comfort level it asks of whoever makes it.</summary>
    public int? Comfort { get; init; }
    /// <summary>Props that have to stand near the station: blocks that <see cref="PropBlock.Provides"/> one, by handle or name (<c>"other-mod:name"</c> for another's). At most 8. A craft at a station
    /// the prop is not near is refused. This is for manual stations: the game's automated factory craft path has no nearby-prop context, so factory recipes refuse this field.
    /// The prop's block has to be in the game's table first: see <see cref="PropBlock.Provides"/>.</summary>
    public IReadOnlyList<PropRef>? NeedsProps { get; init; }
}

public static partial class Stations
{
    /// <summary>The player's own crafting menu.</summary>
    public const string Hand = "hand";
}

/// <summary>Where a recipe is crafted: <c>"hand"</c>, a workshop's <c>"0x…"</c> id, or a station block by handle or name.</summary>
[JsonConverter(typeof(StationRefConverter))]
public readonly record struct StationRef(string Value)
{
    public static implicit operator StationRef(string value) => new(value);
    public static implicit operator StationRef(BlockHandle<StationBlock> handle) => new(handle.ToString());
    public override string ToString() => Value;
}

// ---------------------------------------------------------------------------------------------- how it is found -----

/// <summary>Where it can be found, besides being made.</summary>
public sealed record LootBlock
{
    /// <summary>It is rolled in chests and world pickups where this item is, with its chances. Null: it is not rolled.</summary>
    public ItemRef? Like { get; init; }
    public int? StackMin { get; init; }
    public int? StackMax { get; init; }
    /// <summary>The game's own drop tables it goes into: what enemies and fixed chests hold. For a table of your own, see <see cref="LootTableBlock"/>.</summary>
    public IReadOnlyList<DropPlace>? Tables { get; init; }
    /// <summary>Gear only: a roll gives it a level and rarity of its own, rather than the one fixed level and rarity. The opposite of the crafted-item rule, so it refuses a recipe.</summary>
    public bool Rolled { get; init; }
}

/// <summary>A place in the game's drop tables: where an item goes into a table the game already has.</summary>
public sealed record DropPlace
{
    /// <summary>A table's GUID or a glob over GUIDs.</summary>
    public string? Table { get; init; }
    /// <summary>Every table that drops this item, next to it.</summary>
    public ItemRef? DropsLike { get; init; }
    public int CountMin { get; init; } = 1;
    public int CountMax { get; init; } = 1;
    /// <summary>0..1.</summary>
    public double Chance { get; init; } = 1;
    /// <summary>Weight where the table picks one of several.</summary>
    public double Weight { get; init; } = 1;
}
