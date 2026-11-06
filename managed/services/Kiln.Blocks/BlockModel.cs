// SPDX-License-Identifier: MIT
// The blocks: what a mod author writes, in C# and (the same shape) in blocks/*.json. docs/kiln/modding/blocks.md.
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>Something <see cref="Blocks.Define{T}"/> takes: a spell, a weapon, a status, an effect.</summary>
public interface IBlock { }

/// <summary>How a spell, an attack or an ability reaches what it affects: <see cref="ProjectileBlock"/>, <see cref="SelfBlock"/>, <see cref="AroundBlock"/>, <see cref="ConeBlock"/>, <see cref="BeamBlock"/>, <see cref="ZoneBlock"/>, <see cref="ChainBlock"/>, <see cref="SummonBlock"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ProjectileBlock), "projectile")]
[JsonDerivedType(typeof(SelfBlock), "self")]
[JsonDerivedType(typeof(AroundBlock), "around")]
[JsonDerivedType(typeof(ConeBlock), "cone")]
[JsonDerivedType(typeof(BeamBlock), "beam")]
[JsonDerivedType(typeof(ZoneBlock), "zone")]
[JsonDerivedType(typeof(ChainBlock), "chain")]
[JsonDerivedType(typeof(SummonBlock), "summon")]
public interface IDeliveryBlock { }

// ---------------------------------------------------------------------------------------------------- effects -----

/// <summary>
/// What happens. The same effect plugs in everywhere: a spell's delivery, a weapon's <see cref="WeaponBlock.OnHit"/>, a status's
/// <see cref="StatusBlock.OnApply"/>, <see cref="StatusBlock.OnTick"/> and <see cref="StatusBlock.OnExpire"/>.
/// The parts run in this order: <see cref="Chance"/> decides, <see cref="Area"/>, <see cref="Spawns"/>, <see cref="Vfx"/>, <see cref="Sound"/>,
/// <see cref="Damage"/> with <see cref="Knockback"/> and <see cref="Flinch"/>, <see cref="Heal"/> and <see cref="Restores"/>, <see cref="Removes"/>,
/// <see cref="Applies"/>, <see cref="Then"/>. Where there is no target (the place a projectile stopped), only an area, a spawn, a VFX and a
/// sound can happen.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record EffectBlock : IBlock
{
    /// <summary>Damage to the target. On a spell the first one is its main hit: it is the number the spell's tooltip shows
    /// and the staff's power adds to it, unless <see cref="DamageBlock.Flat"/>. Everywhere else it is exactly this number.
    /// The target's armour and the attacker's stats apply after it, as for every hit in the game.</summary>
    public DamageBlock? Damage { get; init; }

    /// <summary>Health given to the target.</summary>
    public int? Heal { get; init; }

    /// <summary>A status put on the target.</summary>
    public StatusApply? Applies { get; init; }

    /// <summary>How far the hit pushes the target. Without <see cref="Damage"/> this is a hit for 0.</summary>
    public Knockback Knockback { get; init; } = Knockback.None;

    /// <summary>How the target flinches.</summary>
    public Flinch Flinch { get; init; } = Flinch.Light;

    /// <summary>0..1: the whole effect happens this often. Null: always.</summary>
    public double? Chance { get; init; }

    /// <summary>Who it is for: what the trigger gives it (what was hit, who has the status), or the caster, whoever wields the weapon or cast the spell.</summary>
    public EffectOn On { get; init; } = EffectOn.Target;

    /// <summary>Everything around the place the effect happens gets <see cref="AreaBlock.Effects"/>.</summary>
    public AreaBlock? Area { get; init; }

    /// <summary>More effects, run after this one on the same target.</summary>
    public IReadOnlyList<EffectRef>? Then { get; init; }

    /// <summary>Statuses taken off the target: blocks of a mod, or <c>GameBuffs</c> constants.</summary>
    public IReadOnlyList<StatusRef>? Removes { get; init; }

    /// <summary>Mana, stamina and health given to the target. <c>Restore.Mana(20)</c>.</summary>
    public RestoreBlock? Restores { get; init; }

    /// <summary>Spawned at the target, or at the place: a <c>GameTemplates</c> constant or a template's GUID.</summary>
    public TemplateRef? Spawns { get; init; }

    /// <summary>A VFX at the target or the place. It rides on a hidden entity that lives as long as the VFX takes. In a status's <see cref="StatusBlock.OnApply"/> the buff plays it.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }

    /// <summary>A sound at the target or the place, for everyone near it who has Kiln.</summary>
    [ClientsModWhen("^(?![0-9a-fA-F]{8}-)[^:]+$")]
    public SoundRef? Sound { get; init; }

    /// <summary>A full-screen effect for the target, when it is a player, or for the players near the place. It needs <c>Seconds</c>.</summary>
    public ScreenRef? Screen { get; init; }

    /// <summary>An ability the target casts, as if from code: its costs and cooldown apply, and a cast that is refused does nothing. With <see cref="On"/> = caster: whoever wields, wears or cast.</summary>
    public AbilityRef? Casts { get; init; }

    /// <summary>What a cast that is refused gives back, and what one that is not takes besides: an item's use.</summary>
    internal (string Item, int Took, int More)? CastsFrom { get; init; }

    /// <summary>The spell's charges a cast takes besides the one the game takes.</summary>
    internal (string Item, int Count)? TakesCharges { get; init; }
}

/// <summary>An area: who is in it is found when it happens.</summary>
public sealed record AreaBlock
{
    /// <summary>Metres.</summary>
    public required float Radius { get; init; }
    public AreaAffects Affects { get; init; } = AreaAffects.Enemies;
    /// <summary>What each one in the area gets.</summary>
    public required IReadOnlyList<EffectRef> Effects { get; init; }
    /// <summary>A VFX where the area is, once. Its size is the VFX's own: it does not follow <see cref="Radius"/>.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
}

public enum EffectOn { Target, Caster }
public enum AreaAffects { Enemies, Friends }
public enum Flinch { None = 0, VeryLight = 1, Light = 2, Medium = 3, Heavy = 4 }
public enum Knockback { None = 0, Small = 1, Normal = 2, Big = 3, VeryBig = 4, VerySmall = 5, PullSmall = 6, PullNormal = 7 }

/// <summary>Damage per kind. <c>Damage.Ice(40)</c>, <c>Damage.Ice(30) + Damage.Fire(10)</c>.</summary>
public sealed record DamageBlock
{
    public float Cut { get; init; }
    public float Pierce { get; init; }
    public float Blunt { get; init; }
    public float Fire { get; init; }
    public float Ice { get; init; }
    public float Shroud { get; init; }
    public float Lightning { get; init; }
    public float Poison { get; init; }
    public int Wood { get; init; }
    public int Stone { get; init; }
    public int Metal { get; init; }
    public int Explosion { get; init; }

    /// <summary>On a spell: exactly this number, with no share of the staff's power. Elsewhere damage is flat anyway.</summary>
    public bool Flat { get; init; }

    /// <summary>The kinds that hurt, added up.</summary>
    [JsonIgnore] public float Total => Cut + Pierce + Blunt + Fire + Ice + Shroud + Lightning + Poison;

    public static DamageBlock operator +(DamageBlock a, DamageBlock b) => new()
    {
        Cut = a.Cut + b.Cut, Pierce = a.Pierce + b.Pierce, Blunt = a.Blunt + b.Blunt, Fire = a.Fire + b.Fire, Ice = a.Ice + b.Ice,
        Shroud = a.Shroud + b.Shroud, Lightning = a.Lightning + b.Lightning, Poison = a.Poison + b.Poison, Wood = a.Wood + b.Wood,
        Stone = a.Stone + b.Stone, Metal = a.Metal + b.Metal, Explosion = a.Explosion + b.Explosion, Flat = a.Flat || b.Flat,
    };
}

/// <summary>Shorthands for <see cref="DamageBlock"/>.</summary>
public static class Damage
{
    public static DamageBlock Cut(float n) => new() { Cut = n };
    public static DamageBlock Pierce(float n) => new() { Pierce = n };
    public static DamageBlock Blunt(float n) => new() { Blunt = n };
    public static DamageBlock Fire(float n) => new() { Fire = n };
    public static DamageBlock Ice(float n) => new() { Ice = n };
    public static DamageBlock Shroud(float n) => new() { Shroud = n };
    public static DamageBlock Lightning(float n) => new() { Lightning = n };
    public static DamageBlock Poison(float n) => new() { Poison = n };
}

// --------------------------------------------------------------------------------------------------- statuses -----

/// <summary>A buff or debuff: its own block, made of effects. Shown in the game's buff bar with its name and time.</summary>
public sealed record StatusBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Seconds it lasts unless whoever applies it says otherwise. Needed unless <see cref="While"/> says when it ends.</summary>
    public double Seconds { get; init; }
    /// <summary>A player has it for as long as they are in this state: any of the game's, <c>PlayerStateId.Gliding</c>, in JSON a name such as <c>"Gliding"</c> or <c>"HangGliding"</c>.</summary>
    public PlayerStateRef? While { get; init; }
    public StatusKind Kind { get; init; } = StatusKind.Debuff;
    /// <summary>What it looks like: its icon and body effect. A premade <see cref="StatusLook"/> today; your own with VfxBlock.</summary>
    public StatusLook Look { get; init; } = StatusLook.Frost;
    /// <summary>The icon, when it is not the look's: a <c>GameBuffs</c> constant, whose icon it takes.</summary>
    public string? Icon { get; init; }
    /// <summary>Applied again while it runs: the timer starts over (true), or the new one replaces the old (false).</summary>
    public bool Refreshes { get; init; } = true;

    /// <summary>Stats changed on whoever has it, for as long as they have it: <c>Modifies = { [Stat.MoveSpeed] = -0.5 }</c> is half speed.
    /// Whole-number stats (strength, maximum health, armour) take whole numbers; the rest are shares, 0.1 being 10%.</summary>
    public IDictionary<Stat, double> Modifies { get; init; } = new Dictionary<Stat, double>();

    /// <summary>A VFX on whoever has it, for as long as they have it. It takes the place of the <see cref="Look"/>'s.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }

    /// <summary>A full-screen effect on the screen of the player who has it, from when it starts until it ends, then by its fade out.</summary>
    public ScreenRef? Screen { get; init; }

    /// <summary>Runs on whoever has it when it starts.</summary>
    public IReadOnlyList<EffectRef>? OnApply { get; init; }
    /// <summary>Runs on whoever has it every <see cref="TickEvery"/> seconds.</summary>
    public IReadOnlyList<EffectRef>? OnTick { get; init; }
    public double TickEvery { get; init; } = 1;
    /// <summary>Runs on whoever has it when it ends.</summary>
    public IReadOnlyList<EffectRef>? OnExpire { get; init; }

    /// <summary>Game fields this block does not cover, set on the buff it makes: <c>Tweak = { { GameFields.Buffs.HideInUi, true } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(BuffTweakConverter))]
    public FieldValues Tweak { get; init; } = new();

    /// <summary>The name this block had before a rename. Ids come from the oldest name, so saves keep it.</summary>
    public string? Was { get; init; }
}

public enum StatusKind { Debuff, Buff }

/// <summary>What a status looks like. Premade instances of a block you will also be able to write yourself.</summary>
[JsonConverter(typeof(PremadeConverter<StatusLook>))]
public sealed record StatusLook(string Key, string Starter) : IPremade
{
    /// <summary>The frost slow's icon and body effect.</summary>
    public static readonly StatusLook Frost = new("frost", "0x5f5d2f5c");
    /// <summary>The poison icon and body effect.</summary>
    public static readonly StatusLook Poison = new("poison", "0x5f425faf");
    internal static readonly StatusLook[] All = [Frost, Poison];
}

/// <summary>A status to apply: the status, and optionally for how long.</summary>
[JsonConverter(typeof(StatusApplyConverter))]
public sealed record StatusApply
{
    public required StatusRef Status { get; init; }
    /// <summary>Seconds. Null: the status's own <see cref="StatusBlock.Seconds"/>.</summary>
    public double? Seconds { get; init; }

    public static implicit operator StatusApply(StatusRef status) => new() { Status = status };
    public static implicit operator StatusApply(BlockHandle<StatusBlock> status) => new() { Status = status };
    public static implicit operator StatusApply(StatusBlock status) => new() { Status = status };
    public static implicit operator StatusApply(string status) => new() { Status = status };
}

// ----------------------------------------------------------------------------------------------------- spells -----

/// <summary>A staff spell: an item in the stack per charge. A cast takes its <see cref="CastBlock.ManaCost"/> and one charge.</summary>
public sealed record SpellBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>How it is cast: animation, timing, costs.</summary>
    public required CastBlock Cast { get; init; }
    /// <summary>How it gets there, and what it does when it does.</summary>
    public required IDeliveryBlock Delivery { get; init; }
    /// <summary>What happens to the caster the moment it leaves the staff.</summary>
    public IReadOnlyList<EffectRef>? OnCast { get; init; }
    /// <summary>The spell's item level. Its power follows from the main damage, so this only shows in the tooltip.</summary>
    public int Level { get; init; } = 1;
    /// <summary>Charges in one stack.</summary>
    public int MaxCharges { get; init; } = 500;
    /// <summary>How it is crafted. <see cref="RecipeBlock.Makes"/> is how many charges one craft gives.</summary>
    public RecipeBlock? Recipe { get; init; }
    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();

    /// <summary>The name this block had before a rename. Ids come from the oldest name, so saves keep it.</summary>
    public string? Was { get; init; }
}

/// <summary>How a spell is cast.</summary>
public sealed record CastBlock
{
    public int ManaCost { get; init; }
    public int StaminaCost { get; init; }
    /// <summary>How many a cast takes of the spell's stack, or of an ability's item. 0: none, it is used for ever. Null: 1. An ability with no item has none to take.</summary>
    public int? Charges { get; init; }
    /// <summary>The motion. On a spell null is <see cref="CastAnimation.FastBolt"/>; on an ability, none.</summary>
    public CastAnimation? Animation { get; init; }
    /// <summary>Seconds into the animation the spell leaves the staff (cost, sound and effects move with it). Null: the animation's own.</summary>
    public double? ReleaseAt { get; init; }
    /// <summary>A VFX on the caster's hand, when the animation has its own: it takes that one's place and moment.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
}

/// <summary>A cast animation with its timing. Premade instances of a block you will also be able to write yourself.</summary>
[JsonConverter(typeof(PremadeConverter<CastAnimation>))]
public sealed record CastAnimation(string Key, string Sequence, double ChargeSeconds) : IPremade
{
    /// <summary>The quick bolt from the staff (Ice Bolt's).</summary>
    public static readonly CastAnimation FastBolt = new("fast-bolt", "Attack_Player_Staff_FixedAnim_IceBolt_fast", 1.0);
    /// <summary>The longer lob (Fireball's).</summary>
    public static readonly CastAnimation Lob = new("lob", "Attack_Player_Staff_FixedAnim_Fireball_fast", 2.5);
    internal static readonly CastAnimation[] All = [FastBolt, Lob];
}

/// <summary>A projectile: it flies, and where it stops <see cref="OnImpact"/> happens.</summary>
public sealed record ProjectileBlock : IDeliveryBlock
{
    /// <summary>Metres per second.</summary>
    public float Speed { get; init; } = 30;
    /// <summary>0 flies straight, 1 falls like a thrown stone.</summary>
    public float Gravity { get; init; } = 0.1f;
    /// <summary>Metres before it fizzles.</summary>
    public float Range { get; init; } = 150;
    public ProjectileLook Look { get; init; } = ProjectileLook.IceBolt;
    /// <summary>What happens at the place it stops. It has no target of its own there: use <see cref="EffectBlock.Area"/>.</summary>
    public IReadOnlyList<EffectRef>? OnImpact { get; init; }
    /// <summary>What happens to the creature it strikes, if it struck one. A projectile needs this or <see cref="OnImpact"/>.</summary>
    public IReadOnlyList<EffectRef>? OnHit { get; init; }
    /// <summary>It turns toward what it was aimed at. Null: it flies where it was sent.</summary>
    public HomingBlock? Homing { get; init; }
    /// <summary>Game fields this block does not cover, set on the template it flies as: <c>Tweak = { { GameFields.Templates.LifeTime.LifeTime, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(TemplateTweakConverter))]
    public FieldValues Tweak { get; init; } = new();

    /// <summary>A VFX on the projectile while it flies, in place of the <see cref="Look"/>'s. An <see cref="OnImpact"/> effect with a VFX likewise takes the place of the look's burst.</summary>
    [ClientsModWhen(VfxRef.OwnName)]
    public VfxRef? Vfx { get; init; }
}

/// <summary>What a projectile and its impact look and sound like. Premade instances of a block you will also be able to write yourself.</summary>
[JsonConverter(typeof(PremadeConverter<ProjectileLook>))]
public sealed record ProjectileLook(string Key, string Projectile, string Impact, string HandItem) : IPremade
{
    public static readonly ProjectileLook IceBolt = new("ice-bolt", "Projectile_Player_Staff_IceBolt_1", "Explosion_Player_Spell_IceBolt_1", "Ammo_T1_Spell_Ice_Bolt_lvl5");
    public static readonly ProjectileLook Fireball = new("fireball", "Projectile_Player_Staff_Fireball_1", "Explosion_Player_Spell_Fireball_1", "Ammo_T1_Spell_Fire_Ball_lvl10");
    internal static readonly ProjectileLook[] All = [IceBolt, Fireball];
}

/// <summary>Premade instances that JSON names by <see cref="Key"/>.</summary>
public interface IPremade { string Key { get; } }
