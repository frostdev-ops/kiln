// SPDX-License-Identifier: MIT
// Creatures and NPCs as blocks: a body, how it moves, how it sees, what it does. Each part is a block of its own, inline or defined once and named, and
// the premades (Bodies.Vukah, Animations.Vukah, AIs.Vukah, Behaviours.VukahMelee) are plain instances of them. docs/kiln/modding/npc-blocks.md.
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The blocks of a creature: what <see cref="Blocks.Define{T}"/> takes for them asks players' games for no more than the parts need.</summary>
public interface INpcBlock : IBlock { }

// ------------------------------------------------------------------------------------------------------- states -----

/// <summary>
/// A state of an animation graph: what a sequence, an attack or the AI names to make a body play something. The engine's own states are
/// members (<see cref="Idle"/>, <see cref="Walk"/>, ...); yours are names, <c>new AnimState("slam")</c>, with the id
/// <c>UUIDv5(&lt;mod&gt;:state:&lt;name&gt;)</c>, the same the cook gives the graph's state of that name. In JSON, a name.
/// A state of one of the game's graphs is <see cref="Game"/>.
/// </summary>
[JsonConverter(typeof(AnimStateConverter))]
public sealed class AnimState : IEquatable<AnimState>
{
    // The states the engine itself asks for (locomotion_animation's table), with the GUID the game's sequences use where one is known. docs/re/npc-internals.md 2.5.
    internal static readonly (string Name, uint Hash, Guid? Guid)[] Engine =
    [
        ("Idle", 0x1490BED7, new Guid("2e701760-3fb1-4e08-b14a-4e012cac15d0")), ("Walk", 0x1A8DBBBB, new Guid("a26eaa64-1902-496c-bc67-070c87a32303")),
        ("Run", 0x28689701, null), ("Sprint", 0x25369C6D, null), ("IdleCombat", 0x5D8FED2B, new Guid("3f4d9c3e-9ca3-45aa-8f84-7249920bc26b")),
        ("Fall", 0xDD657BE9, new Guid("979ffb20-6612-4c72-a443-b7d99cc25688")), ("IdleSneak", 0x30387ABB, null), ("Sneak", 0x230EE7D7, null),
        ("Dead", 0x5E840C07, new Guid("7e2728b2-32b2-4481-a470-5d5848409427")), ("Stun", 0xE5699BCD, new Guid("1477d689-8d8c-424c-bb5e-6f4cf42b2ac8")),
    ];

    /// <summary>A state of this mod's animation by its name, or the engine's (<c>"Idle"</c>), or one of the game's graphs (<c>"0x1490bed7"</c>, a GUID).</summary>
    public AnimState(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>What it was given as: a name, an engine state's name, a GUID or an id.</summary>
    public string Name { get; }

    public static AnimState Idle { get; } = new("Idle");
    public static AnimState Walk { get; } = new("Walk");
    public static AnimState Run { get; } = new("Run");
    public static AnimState Sprint { get; } = new("Sprint");
    public static AnimState IdleCombat { get; } = new("IdleCombat");
    public static AnimState Fall { get; } = new("Fall");
    public static AnimState IdleSneak { get; } = new("IdleSneak");
    public static AnimState Sneak { get; } = new("Sneak");
    public static AnimState Dead { get; } = new("Dead");
    public static AnimState Stun { get; } = new("Stun");

    /// <summary>A state of one of the game's graphs, by the GUID its sequences name.</summary>
    public static AnimState Game(Guid id) => new(id.ToString());

    public static implicit operator AnimState(string name) => new(name);

    /// <summary>The engine state this is, or null.</summary>
    internal string? EngineName => Engine.FirstOrDefault(e => string.Equals(e.Name, Name, StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>A state of the game's, named by its id or GUID.</summary>
    internal bool IsGame => Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || System.Guid.TryParse(Name, out _);

    /// <summary>The value the graph's state input holds for it.</summary>
    internal uint Hash(string mod)
    {
        if (EngineName is { } e) return Engine.First(x => x.Name == e).Hash;
        if (Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt32(Name[2..], 16);
        return BlockIds.Fnv(Guid(mod) ?? default);
    }

    /// <summary>The GUID a sequence names it by; null for an engine state whose GUID is not known, and for one named by its id.</summary>
    internal Guid? Guid(string mod)
    {
        if (EngineName is { } e) return Engine.First(x => x.Name == e).Guid;
        if (System.Guid.TryParse(Name, out var g)) return g;
        if (Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        return BlockIds.Derive(mod, "state", Name);
    }

    public bool Equals(AnimState? other) => other is not null && (EngineName is { } e ? e == other.EngineName : string.Equals(Name, other.Name, StringComparison.Ordinal));
    public override bool Equals(object? obj) => obj is AnimState s && Equals(s);
    public override int GetHashCode() => (EngineName ?? Name).GetHashCode(StringComparison.Ordinal);
    public override string ToString() => EngineName ?? Name;
}

// -------------------------------------------------------------------------------------------------------- clips -----

/// <summary>What a state plays: an animation of the glTF by its name, or a blend of several (<see cref="Blends"/>). In JSON, the name, or
/// <c>{ "clip": "Slam", "speed": 1.2 }</c>.</summary>
[JsonConverter(typeof(ClipConverter))]
public sealed record Clip
{
    public Clip() { }

    /// <summary>The glTF animation by its name.</summary>
    public Clip(string name) => Name = name;

    /// <summary>The glTF animation. Null with <see cref="Blend"/>.</summary>
    [JsonPropertyName("clip")]
    public string? Name { get; init; }
    /// <summary>Several animations mixed by an input, such as the direction a creature walks.</summary>
    public BlendSpace? Blend { get; init; }
    /// <summary>Plays faster (above 1) or slower.</summary>
    public double? Speed { get; init; }
    /// <summary>Plays again from the start. The engine's moving and idle states loop unless this says otherwise; every other state plays once.</summary>
    public bool? Loop { get; init; }
    /// <summary>Moves on to this state of the same animation when its clip ends.</summary>
    public AnimState? Then { get; init; }
    /// <summary>The motion of the root joint moves the creature instead.</summary>
    public bool? RootMotion { get; init; }
    /// <summary>Seconds it blends in over.</summary>
    public double? BlendIn { get; init; }
    /// <summary>On <see cref="AnimationBlock.Hit"/>: the animation whose pose it is relative to, so it adds to whatever plays. Without one it replaces it.</summary>
    public string? Reference { get; init; }
    /// <summary>On <see cref="AnimationBlock.Hit"/>: only these joints.</summary>
    public IReadOnlyList<string>? Bones { get; init; }

    public static implicit operator Clip(string name) => new(name);

    public override string ToString() => Name ?? "blend";
}

/// <summary>Animations mixed by an input: at each value of the input its animation, blended in between.</summary>
public sealed record BlendSpace
{
    /// <summary>An input the engine fills (<c>moveDirection</c>, <c>hitDirection</c>, <c>lookYaw</c>, ...), or a name of your own a sequence sets.</summary>
    public string Input { get; init; } = "moveDirection";
    /// <summary>Value of the input -> the glTF animation that plays there.</summary>
    public IDictionary<string, string> Clips { get; init; } = new Dictionary<string, string>();
    /// <summary>The input wraps around, as a direction does (−180 is 180).</summary>
    public bool? Circular { get; init; }
}

/// <summary>Blends for <see cref="AnimationBlock"/>'s states: <c>Walk = Blends.ByDirection("WalkF", "WalkB")</c>.</summary>
public static class Blends
{
    /// <summary>The engine's move direction picks the animation: forward at 0°, back at ±180°, left and right at −90° and 90° when given.</summary>
    public static Clip ByDirection(string forward, string back, string? left = null, string? right = null)
    {
        var clips = new Dictionary<string, string> { ["0"] = forward, ["-180"] = back, ["180"] = back };
        if (left != null) clips["-90"] = left;
        if (right != null) clips["90"] = right;
        return new Clip { Blend = new BlendSpace { Input = "moveDirection", Clips = clips } };
    }

    /// <summary>Any input, with an animation at each value.</summary>
    public static Clip On(string input, params (double At, string Clip)[] clips) =>
        new() { Blend = new BlendSpace { Input = input, Clips = clips.ToDictionary(x => Number(x.At), x => x.Clip) } };

    internal static string Number(double d) => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture) : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>An animation played over the states: always, or each time an overlay fires (the engine fires <c>hit</c> on a hit reaction).</summary>
public sealed record LayerBlock
{
    public string? Name { get; init; }
    /// <summary><c>hit</c>, or a name of your own a sequence fires. Null: it always plays.</summary>
    public string? Overlay { get; init; }
    /// <summary>The glTF animation.</summary>
    [JsonPropertyName("clip")]
    public string? Clip { get; init; }
    public BlendSpace? Blend { get; init; }
    public double? Speed { get; init; }
    /// <summary>The animation whose pose it is relative to: then it adds to what plays. Without one it replaces it.</summary>
    public string? Reference { get; init; }
    /// <summary>Only these joints.</summary>
    public IReadOnlyList<string>? Bones { get; init; }
    public double? Weight { get; init; }
    public double? FadeIn { get; init; }
    public double? FadeOut { get; init; }
}

// -------------------------------------------------------------------------------------------------- animation -----

/// <summary>
/// How a body moves: the clips of a glTF, which of them the engine's states play, your own states, and what plays over them. Kiln's cook makes
/// the skeleton, the clips and a state machine graph from it when the mod builds, and nests machines past the engine's 34 transitions, so a list of
/// states is all you write. <see cref="Idle"/> is needed; <see cref="Walk"/> and <see cref="Run"/> when the creature moves; <see cref="IdleCombat"/>
/// and <see cref="Fall"/> stand as in Idle when left out.
/// <para>An animation with its own files is cooked from <c>blocks/*.json</c>; C# names it. The premades (<see cref="Animations"/>) are the game's
/// graphs.</para>
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record AnimationBlock : INpcBlock, IPremade
{
    /// <summary>How JSON names a premade.</summary>
    public string Key { get; init; } = "";
    /// <summary>In JSON, the premade this one starts from.</summary>
    public string? Like { get; init; }
    /// <summary>The glTF file of the mod (its path in the mod's folder): its armature is the skeleton, its animations the clips. Players need the mod.</summary>
    [ClientsModWhen(".")]
    public string? Clips { get; init; }
    /// <summary>The skeleton's name: the file's name without its extension when left out. A body and an animation share a skeleton when they name the same one.</summary>
    public string? Skeleton { get; init; }
    /// <summary>The clips are authored on a rig of their own: play them on <see cref="Skeleton"/>, a body's, by pairing joints (<see cref="RetargetBlock"/>).</summary>
    public RetargetBlock? Retarget { get; init; }
    /// <summary>A game creature whose graph this is (the premades).</summary>
    public string? Template { get; init; }

    public Clip? Idle { get; init; }
    public Clip? Walk { get; init; }
    public Clip? Run { get; init; }
    public Clip? Sprint { get; init; }
    public Clip? IdleCombat { get; init; }
    public Clip? Fall { get; init; }
    public Clip? IdleSneak { get; init; }
    public Clip? Sneak { get; init; }
    /// <summary>Plays when it dies, and holds its last frame.</summary>
    public Clip? Dead { get; init; }
    public Clip? Stun { get; init; }
    /// <summary>Plays over everything when it is hit (the engine's hit overlay).</summary>
    public Clip? Hit { get; init; }
    /// <summary>What a hit reaction, a stun or a parry plays over the whole body: the base creature's hit sequences play it. Default: <see cref="Hit"/>'s clip
    /// once, else Idle. <see cref="Stun"/> defaults to it, and <see cref="Dead"/> to Idle, held.</summary>
    public Clip? Stagger { get; init; }

    /// <summary>States of your own: what attacks, rules and sequences play by name.</summary>
    public IDictionary<AnimState, Clip> States { get; init; } = new Dictionary<AnimState, Clip>();
    /// <summary>More animations over the states.</summary>
    public IReadOnlyList<LayerBlock>? Layers { get; init; }

    // the premades JSON names by key (ItemParts.Part reads this field)
    internal static readonly AnimationBlock[] All = Animations.All;
}

/// <summary>The game's graphs, as animations to give a creature of that body.</summary>
public static class Animations
{
    public static readonly AnimationBlock Vukah = new() { Key = "vukah", Template = "Enemy_Vukah_Melee" };
    public static readonly AnimationBlock Scavenger = new() { Key = "scavenger", Template = "Enemy_Scavenger_Melee01" };
    public static readonly AnimationBlock Fisher = new() { Key = "fisher", Template = "NPC_Workshop_Fisher_Trader" };
    /// <summary>A villager's graph: the fisher trader's.</summary>
    public static readonly AnimationBlock Villager = Fisher with { Key = "villager" };
    internal static readonly AnimationBlock[] All = [Vukah, Scavenger, Fisher, Villager];
}

// ------------------------------------------------------------------------------------------------------- bodies -----

/// <summary>A collision shape of a body, centred on its root.</summary>
public sealed record ColliderBlock
{
    /// <summary><c>capsule</c> or <c>sphere</c>.</summary>
    public string Shape { get; init; } = "capsule";
    public double Radius { get; init; } = 0.5;
    /// <summary>A capsule's full height, its caps included.</summary>
    public double Height { get; init; } = 2;
}

/// <summary>A capsule standing on the ground.</summary>
public static class Capsule
{
    public static ColliderBlock Of(double radius, double height) => new() { Shape = "capsule", Radius = radius, Height = height };
}

/// <summary>A sphere.</summary>
public static class Sphere
{
    public static ColliderBlock Of(double radius) => new() { Shape = "sphere", Radius = radius, Height = radius * 2 };
}

/// <summary>
/// What a creature is: a model of yours on its skeleton (cooked when the mod builds), or a game creature's body (<see cref="Bodies"/>). The
/// game's renderer poses a model only by the skeleton its animation graph names, so a body of your own and its animation have to share one:
/// Kiln checks that at <c>Define</c>.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record BodyBlock : INpcBlock, IPremade
{
    public string Key { get; init; } = "";
    public string? Like { get; init; }
    /// <summary>A game creature whose body this is (the premades).</summary>
    public string? Template { get; init; }
    /// <summary>A skinned glTF of the mod (its path in the mod's folder). Players need the mod.</summary>
    [ClientsModWhen(".")]
    public string? Model { get; init; }
    /// <summary>The skeleton's name: the model file's name without its extension when left out.</summary>
    public string? Skeleton { get; init; }
    /// <summary>Its size for each creature that has it (1 = as modelled).</summary>
    public double? Scale { get; init; }
    /// <summary>Its collision: the first shape for hits, the second for what it bumps into; one shape is both. Every collider mode of the body gets them.</summary>
    public IReadOnlyList<ColliderBlock>? Colliders { get; init; }
    /// <summary>The game's attachment slots on the joints of its skeleton: where gear hangs and hits come from. <c>{ [Slot.HandR] = "hand_r" }</c>.</summary>
    public IDictionary<Slot, string>? Slots { get; init; }
    /// <summary>How much room it needs to walk: radius and height in metres, for paths and steps.</summary>
    public BodySize? Size { get; init; }
    /// <summary>A game NPC's workshop: what a vendor on this body sells from (<c>0x…</c>).</summary>
    public string? Workshop { get; init; }
    /// <summary>The item that summons a roster NPC of this body, which a <see cref="RosterBlock"/>'s tool starts from.</summary>
    public string? Tool { get; init; }

    // the premades JSON names by key (ItemParts.Part reads this field)
    internal static readonly BodyBlock[] All = Bodies.All;
}

/// <summary>How much room a body needs: what paths are searched with.</summary>
public sealed record BodySize(double Radius, double Height);

/// <summary>The game's bodies.</summary>
public static class Bodies
{
    public static readonly BodyBlock Vukah = new() { Key = "vukah", Template = "Enemy_Vukah_Melee" };
    public static readonly BodyBlock Scavenger = new() { Key = "scavenger", Template = "Enemy_Scavenger_Melee01" };
    /// <summary>The fisher trader. Vendors on it sell at his workshop, which he keeps too.</summary>
    public static readonly BodyBlock Fisher = new() { Key = "fisher", Template = "NPC_Workshop_Fisher_Trader", Workshop = "0xc4acc104", Tool = "NPC_Townsfolk_Male_Villager_08_fisher_Trader" };
    /// <summary>A villager: the fisher's body.</summary>
    public static readonly BodyBlock Villager = Fisher with { Key = "villager" };
    internal static readonly BodyBlock[] All = [Vukah, Scavenger, Fisher, Villager];
}

// ----------------------------------------------------------------------------------------------------------- AI -----

/// <summary>
/// How it notices and chases: the game's tuning of a creature to start from (<see cref="AIs"/>), and what to change. Distances in metres. A body
/// of your own runs the AI of <see cref="Template"/>, the Vukah's by default; hundreds of its fields have no neutral value, so a premade is
/// where good values come from.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed partial record AIBlock : INpcBlock, IPremade
{
    public string Key { get; init; } = "";
    public string? Like { get; init; }
    /// <summary>The game creature whose AI this starts from.</summary>
    public string? Template { get; init; }
    /// <summary>How far it sees a target by day (the game's <c>combatCommitmentSetup.view.range</c>): less at night and in water, by the AI's
    /// <c>nightRangeFactor</c> (the Vukah's 0.6) and <c>waterRangeFactor</c>. A brain's own perception sees this far too.</summary>
    public double? Sight { get; init; }
    /// <summary>Half the angle of its view, in degrees (<c>combatCommitmentSetup.view.coneHalfAngle</c>).</summary>
    public double? SightAngle { get; init; }
    /// <summary>Within this distance a target it sees is engaged at once (<c>combatCommitmentSetup.gain.inRange.range</c>; the game's 0 is its
    /// combat distances + 0.5 m).</summary>
    public double? Aggro { get; init; }
    /// <summary>How far from home it chases before it goes back.</summary>
    public double? Leash { get; init; }
    /// <summary>Its side: who it fights. Equal factions are friends, as are the players' side.</summary>
    public Faction? Faction { get; init; }
    /// <summary>False: it stands where it is (the game's Move, Roaming and Patrol off). Then Walk and Run aren't needed.</summary>
    public bool? Moves { get; init; }
    /// <summary>False: it does not fight (the game's Combat off).</summary>
    public bool? Fights { get; init; }
    /// <summary>Decides its combat target, on the tick thread, every <see cref="PickEvery"/> seconds: an entity, or <c>default</c> for the game's choice.
    /// In JSON a name: <c>Blocks.Target("name", npc =&gt; ...)</c>.</summary>
    [JsonIgnore]
    public Func<NpcView, EntityId>? PickTarget { get; init; }
    /// <summary>In JSON: a target picker registered by name with <c>Blocks.Target</c>.</summary>
    [JsonPropertyName("pickTarget")]
    public string? PickTargetName { get; init; }
    public double? PickEvery { get; init; }
    /// <summary>Any other AI field, by its path in the game's AI (<c>Enemy01BehaviorState</c> unless it names another component): <c>["combatSetup.circlingSetup.minRadius"] = "4"</c>.</summary>
    public IDictionary<string, string>? Tune { get; init; }

    // the premades JSON names by key (ItemParts.Part reads this field)
    internal static readonly AIBlock[] All = AIs.All;
}

/// <summary>The game's AI tunings.</summary>
public static class AIs
{
    /// <summary>The Vukah: a melee fighter that sees 18 m by day (11 m at night) and keeps its distance while it waits.</summary>
    public static readonly AIBlock Vukah = new() { Key = "vukah", Template = "Enemy_Vukah_Melee" };
    /// <summary>The scavenger melee fighter.</summary>
    public static readonly AIBlock Scavenger = new() { Key = "scavenger", Template = "Enemy_Scavenger_Melee01" };
    /// <summary>A heavy fighter: the Vukah's AI, slower to turn away, holding its ground longer.</summary>
    public static readonly AIBlock Heavy = Vukah with { Key = "heavy", Leash = 40 };
    /// <summary>One that stands where it is put and never starts a fight: guards, townsfolk.</summary>
    public static readonly AIBlock Still = Vukah with { Key = "still", Moves = false, Fights = false };
    internal static readonly AIBlock[] All = [Vukah, Scavenger, Heavy, Still];
}

// ---------------------------------------------------------------------------------------------------- behaviour -----

/// <summary>When the game considers an attack (<c>keen::enemy01::AttackStyle</c>).</summary>
public enum AttackStyle { Normal, Retaliate, CounterAttack, StunBreak, Special, Initiate, InitiateFollowUp, IgnoreThreat, Flanking }

/// <summary>
/// One attack: the state it plays, how near it has to be, how often, and what its hit does. Kiln makes the sequence that plays it and the hit:
/// at <see cref="HitAt"/> everything in front within <see cref="Range"/> gets <see cref="OnHit"/>, or <see cref="Delivery"/> leaves.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record AttackBlock : INpcBlock
{
    /// <summary>What rules, <c>PickAttack</c> and <c>Act.Attack</c> call it. Default: its place in the list, <c>attack0</c>.</summary>
    public string? Name { get; init; }
    /// <summary>The state it plays: one the animation has. A state the graph lacks would play nothing and its hit would come from nowhere, so it is refused.</summary>
    public AnimState? Plays { get; init; }
    /// <summary>The longest distance to its target it starts from.</summary>
    public double Range { get; init; } = 2.5;
    /// <summary>The shortest.</summary>
    public double? MinRange { get; init; }
    /// <summary>Seconds after any attack of the creature before this one is chosen again.</summary>
    public double? Cooldown { get; init; }
    /// <summary>When the game considers it. Default: <see cref="AttackStyle.Normal"/>.</summary>
    public IReadOnlyList<AttackStyle>? Style { get; init; }
    /// <summary>0..1: how often it is chosen when it could be.</summary>
    public double? Chance { get; init; }
    /// <summary>Seconds into it that the hit happens.</summary>
    public double HitAt { get; init; } = 0.5;
    /// <summary>Seconds it lasts. Default: its clip's length at its speed.</summary>
    public double? Length { get; init; }
    /// <summary>Degrees in front of it that its hit reaches.</summary>
    public double Angle { get; init; } = 100;
    /// <summary>What a hit does, to everything in front within <see cref="Range"/>.</summary>
    public IReadOnlyList<EffectRef>? OnHit { get; init; }
    /// <summary>A ranged attack: what leaves at <see cref="HitAt"/>, as a spell's delivery. Then <see cref="OnHit"/> is not used.</summary>
    public IDeliveryBlock? Delivery { get; init; }
    /// <summary>A game sequence this attack plays as it is, on a game body: its GUID. Then Plays, OnHit and Delivery are not used.</summary>
    public Guid? Sequence { get; init; }
}

/// <summary>
/// A rule: on a trigger, every so often, when its conditions hold, do something. Conditions and actions are kiln.creatures' <see cref="Is"/> and
/// <see cref="Act"/>, and <c>Act.Effect</c> runs an effect. Code in <c>Is.Custom</c> and <c>Act.Custom</c> runs on the tick thread: a condition every
/// <see cref="Every"/> seconds, an action queued from the game's job thread to the next step.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record RuleBlock : INpcBlock
{
    public string? Name { get; init; }
    /// <summary>When it is looked at: every <see cref="Every"/> seconds by default, or on entering a phase, starting an attack, ...</summary>
    public RuleMode On { get; init; } = RuleMode.OnUpdate;
    public double Every { get; init; } = 1;
    /// <summary>Seconds before it can fire again.</summary>
    public double Cooldown { get; init; }
    /// <summary>0..1.</summary>
    public double Chance { get; init; } = 1;
    /// <summary>All of these hold.</summary>
    [JsonConverter(typeof(RuleConditionsConverter))]
    public IReadOnlyList<RuleCondition> When { get; init; } = [];
    [JsonConverter(typeof(RuleActionsConverter))]
    public IReadOnlyList<RuleAction> Do { get; init; } = [];
}

/// <summary>
/// What a creature does: attacks and rules, on top of the game's combat AI (blocking, flanking, hit reactions stay). <see cref="PickAttack"/>
/// replaces one decision; <see cref="Brain"/> replaces all of them.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record BehaviourBlock : INpcBlock, IPremade
{
    public string Key { get; init; } = "";
    public string? Like { get; init; }
    /// <summary>Keeps this game creature's attacks and rules; the block's come after them. Its sequences play that creature's states, so this needs its animation.</summary>
    public string? Template { get; init; }
    public IReadOnlyList<PartRef<AttackBlock>>? Attacks { get; init; }
    public IReadOnlyList<PartRef<RuleBlock>>? Rules { get; init; }
    /// <summary>The phases its rules name. With a list, a rule naming another is refused.</summary>
    public IReadOnlyList<string>? Phases { get; init; }
    /// <summary>Decides the attack each time it would choose, on the tick thread: an attack's name, or null for the game's choice.</summary>
    [JsonIgnore]
    public Func<NpcView, string?>? PickAttack { get; init; }
    [JsonPropertyName("pickAttack")]
    public string? PickAttackName { get; init; }
    public double? PickEvery { get; init; }
    /// <summary>Your code drives it instead of the game's AI: a new <see cref="NpcBrain"/> for each creature. The game's decisions and its perception
    /// stop for it; <see cref="NpcControl"/> sees for it. In JSON a name: <c>Blocks.Brain("name", () =&gt; new MyBrain())</c>.</summary>
    [JsonIgnore]
    public Func<NpcBrain>? Brain { get; init; }
    [JsonPropertyName("brain")]
    public string? BrainName { get; init; }

    // the premades JSON names by key (ItemParts.Part reads this field)
    internal static readonly BehaviourBlock[] All = Behaviours.All;
}

/// <summary>The game's behaviours.</summary>
public static class Behaviours
{
    /// <summary>The Vukah's attacks and rules.</summary>
    public static readonly BehaviourBlock VukahMelee = new() { Key = "vukah-melee", Template = "Enemy_Vukah_Melee" };
    /// <summary>The scavenger's.</summary>
    public static readonly BehaviourBlock ScavengerMelee = new() { Key = "scavenger-melee", Template = "Enemy_Scavenger_Melee01" };
    /// <summary>Nothing of its own: no attacks, no rules. A start for a creature of your own.</summary>
    public static readonly BehaviourBlock None = new() { Key = "none" };
    internal static readonly BehaviourBlock[] All = [VukahMelee, ScavengerMelee, None];
}

// ---------------------------------------------------------------------------------------------------- creatures -----

/// <summary>What a creature is worth in a fight.</summary>
public sealed record CreatureStats
{
    /// <summary>1..60: its health and damage follow from it as the game scales them.</summary>
    public int? Level { get; init; }
    /// <summary>Its share of damage dealt, 1 as the game has it.</summary>
    public double? Damage { get; init; }
    /// <summary>Its share of experience given, 1 as the game has it.</summary>
    public double? Experience { get; init; }
    /// <summary>Damage it takes of a kind, 1 as normal, 0 immune.</summary>
    public IDictionary<DamageKind, double> Takes { get; init; } = new Dictionary<DamageKind, double>();
}

/// <summary>Where a creature turns up by itself: in place of the game's creatures of a template.</summary>
public sealed record SpawnBlock
{
    /// <summary>A game creature: <c>GameTemplates</c> constant or name.</summary>
    public required string Replaces { get; init; }
    /// <summary>0..1 of them.</summary>
    public double Chance { get; init; } = 1;
    public bool NightOnly { get; init; }
    public bool DayOnly { get; init; }
}

/// <summary>
/// A creature: its body, how it moves, how it sees, what it does. On a game body with a game animation it is the game's creature with a new mind,
/// which vanilla players watch with no mod (<c>clients: none</c>); a body or animation of your own makes a new template that every player's
/// game needs (<c>clients: mod</c>). Kiln works this out.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record CreatureBlock : INpcBlock
{
    /// <summary>Its name, for logs and Kiln players (vanilla players see the body's own).</summary>
    public string? Name { get; init; }
    /// <summary>The name before a rename: ids come from the oldest name.</summary>
    public string? Was { get; init; }
    public required PartRef<BodyBlock> Body { get; init; }
    /// <summary>Default: the body's own, for a game body.</summary>
    public PartRef<AnimationBlock>? Animation { get; init; }
    /// <summary>Default: the body's, for a game body; the Vukah's for one of your own.</summary>
    public PartRef<AIBlock>? AI { get; init; }
    /// <summary>Default: the body's, for a game body; none for one of your own.</summary>
    public PartRef<BehaviourBlock>? Behaviour { get; init; }
    public CreatureStats? Stats { get; init; }
    public IReadOnlyList<SpawnBlock>? Spawns { get; init; }
}

// --------------------------------------------------------------------------------------------------------- NPCs -----

/// <summary>A price: so many of an item.</summary>
public sealed record Price(ItemRef Item, int Count)
{
    public static Price Of(ItemRef item, int count = 1) => new(item, count);
}

/// <summary>What an NPC sells: item -> its price. A recipe at its workshop each, which every NPC of that workshop shows.</summary>
public sealed record VendorBlock
{
    public IDictionary<ItemRef, Price> Sells { get; init; } = new Dictionary<ItemRef, Price>();
    /// <summary>How many one trade gives, per item; 1 when left out.</summary>
    public IDictionary<ItemRef, int>? Gives { get; init; }
    /// <summary>The workshop (<c>0x…</c>). Default: the body's.</summary>
    public string? Workshop { get; init; }
}

/// <summary>A place in the game's roster of NPCs: unlocked, summoned with a tool, given a bed. One per world. The game builds its roster at start, so
/// a roster NPC defined in code is there from the next start.</summary>
public sealed record RosterBlock
{
    /// <summary>A knowledge name of the mod's quests that unlocks it; null: always.</summary>
    public string? UnlockedBy { get; init; }
    /// <summary>What the tool that summons it is called under its name ("Tobin's Whistle").</summary>
    public string? SummonTool { get; init; }
    /// <summary>The roster page's text about it.</summary>
    public string? Description { get; init; }
    /// <summary>How far it wanders from where it is placed.</summary>
    public double? RoamingRange { get; init; }
}

/// <summary>Where an NPC is put: next to the first player, or at a spot.</summary>
public sealed record NpcPlace
{
    public double[]? At { get; init; }
    /// <summary>Metres from the first player, between these two.</summary>
    public double[]? Distance { get; init; }
    /// <summary>Only the first time in a world.</summary>
    public bool Once { get; init; } = true;
    /// <summary>
    /// A base is fine (the default): a friendly NPC may stand in one, so it comes as near as <see cref="Distance"/> asks while the player is home.
    /// false keeps it 20 m outside every base zone, as a quest's places are.
    /// </summary>
    public bool InBase { get; init; } = true;
    /// <summary>Exactly <see cref="At"/>: no ground, no base check.</summary>
    public bool Exact { get; init; }
}

/// <summary>
/// A friendly NPC: a creature with a conversation (<see cref="Talk"/>, the quests' <see cref="DialogueDefinition"/>), wares and a place in the
/// roster. It is a new NPC of its own, one per world, on a game NPC's body. Players need the mod: the game builds NPCs and their lines at start.
/// </summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record NpcBlock : INpcBlock
{
    public required string Name { get; init; }
    public string? Was { get; init; }
    public required PartRef<BodyBlock> Body { get; init; }
    public PartRef<AnimationBlock>? Animation { get; init; }
    public PartRef<AIBlock>? AI { get; init; }
    public PartRef<BehaviourBlock>? Behaviour { get; init; }
    /// <summary>What it says: a dialogue (<c>Dialogues.Define</c>, or one of the mod's <c>quests/*.json</c> by its id).</summary>
    [JsonIgnore]
    public DialogueDefinition? Talk { get; init; }
    [JsonPropertyName("talk")]
    public string? TalkName { get; init; }
    public VendorBlock? Vendor { get; init; }
    public RosterBlock? Roster { get; init; }
    public NpcPlace? Place { get; init; }
    /// <summary>Its names in other languages: <c>{ ["de"] = "Tobin der Wanderer" }</c>.</summary>
    public IDictionary<string, string>? Names { get; init; }
}
