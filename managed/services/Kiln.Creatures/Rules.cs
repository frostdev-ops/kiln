// SPDX-License-Identifier: MIT
// Behaviour as rules in the game's own arsenal data: conditions and actions as values, the arsenal writer's front end,
// and the tick-side pump for Kiln's own kinds. Native side: native/services/creatures/{arsenal,rules}.cpp. Usage:
// docs/kiln/services/creatures.md "Rules and arsenals".
//
// The dispatcher runs on the game's job threads. Nothing a mod writes here runs there: a condition's code runs on the
// tick and publishes its value to a table; an action's code is queued and runs on the tick. There is no way to pass a
// delegate to the job-thread side.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Ids of what rules name. They follow from the mod id and the name only, as block ids do:
/// UUIDv5 of <c>&lt;mod&gt;:&lt;kind&gt;:&lt;name&gt;</c>; a game id is FNV-1a over the GUID's 16 bytes.</summary>
public static class RuleIds
{
    private static readonly Guid Namespace = new("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77");

    /// <summary>The derived GUID.</summary>
    public static Guid Derive(string mod, string kind, string name)
    {
        Span<byte> ns = stackalloc byte[16];
        Namespace.TryWriteBytes(ns, bigEndian: true, out _);
        var text = System.Text.Encoding.UTF8.GetBytes($"{mod}:{kind}:{name}");
        var all = new byte[16 + text.Length];
        ns.CopyTo(all);
        text.CopyTo(all, 16);
        var h = SHA1.HashData(all);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>FNV-1a-32 over a GUID's bytes: the game's id of a sequence or buff with that GUID.</summary>
    public static uint Fnv(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x01000193u;
        return h;
    }

    internal static string CallerMod => ModContext.CurrentMod?.Info.Id ?? "kiln";

    /// <summary>The GUID of the calling mod's phase <paramref name="name"/>.</summary>
    public static Guid Phase(string name) => Derive(CallerMod, "phase", name);

    /// <summary>The GUID of the calling mod's attack <paramref name="name"/>.</summary>
    public static Guid Attack(string name) => Derive(CallerMod, "attack", name);
}

/// <summary>What a condition or action sees on the tick thread: one creature, read when asked.</summary>
public sealed class NpcView
{
    private CreatureInfo? _info;
    private bool _read;

    internal NpcView(EntityId entity) => Entity = entity;

    public EntityId Entity { get; }

    /// <summary>The creature, with every <see cref="CreatureRef"/> API. Changes are layers of the mod that owns the rule.</summary>
    public CreatureRef Creature => Creatures.Of(Entity);

    /// <summary>A snapshot of the creature, taken once per call; null when it is gone.</summary>
    public CreatureInfo? Info
    {
        get
        {
            if (!_read)
            {
                _info = Creature.Inspect();
                _read = true;
            }
            return _info;
        }
    }

    public int Health => Info?.Health ?? 0;
    public int HealthMax => Info?.HealthMax ?? 0;

    /// <summary>Health as a fraction, 0 to 1.</summary>
    public double HealthFraction => HealthMax > 0 ? (double)Health / HealthMax : 0;

    public EnemyState State => Info?.State ?? EnemyState.Invalid;

    /// <summary>The combat target, default when it has none.</summary>
    public EntityId Target => Info?.Target ?? default;

    /// <summary>Where it stands, null when it is gone.</summary>
    public WorldPosition? Position => World.Position(Entity);

    /// <summary>Metres to the combat target, null without one.</summary>
    public double? TargetDistance =>
        Target.Value != 0 && World.TryGetPosition(Entity, out var a) && World.TryGetPosition(Target, out var b) ? a.DistanceTo(b) : null;

    /// <summary>Its phase is this one of the calling mod's.</summary>
    public bool InPhase(string name) => Creature.Phase == RuleIds.Fnv(RuleIds.Phase(name));
}

/// <summary>A condition of a rule or an attack. Make one with <see cref="Is"/>.</summary>
public sealed class RuleCondition
{
    internal RuleCondition(JsonObject json, RuleSlotUse? use = null)
    {
        Json = json;
        Use = use;
    }

    internal JsonObject Json { get; }
    internal RuleSlotUse? Use { get; }

    /// <summary>The opposite. Any kind can be negated: the game's rule walker applies it.</summary>
    public RuleCondition Not()
    {
        var j = (JsonObject)Json.DeepClone();
        j["negated"] = !(j["negated"]?.GetValue<bool>() ?? false);
        return new RuleCondition(j, Use);
    }

    /// <summary>The JSON the arsenal writer takes.</summary>
    public string ToJson() => Json.ToJsonString();
}

/// <summary>An action of a rule. Make one with <see cref="Act"/>.</summary>
public sealed class RuleAction
{
    internal RuleAction(JsonObject json, RuleActionUse? use = null)
    {
        Json = json;
        Use = use;
    }

    internal JsonObject Json { get; }
    internal RuleActionUse? Use { get; }

    /// <summary>The JSON the arsenal writer takes.</summary>
    public string ToJson() => Json.ToJsonString();
}

// A condition's code before it has a slot, or the name it will find one under.
internal sealed record RuleSlotUse(Func<NpcView, bool>? Fn, double Every, string? Name);
internal sealed record RuleActionUse(Action<NpcView>? Fn, string? Name);

/// <summary>Conditions. The game's own kinds cost no hook; <see cref="HasBuff"/>, <see cref="TimeBetween"/>, <see cref="InPhase(string)"/>
/// and <see cref="Custom"/> are Kiln's, and read a table the tick publishes before each step.</summary>
public static class Is
{
    /// <summary>Health at or below this fraction (0 to 1). The game's <c>EnemyHealthPercentageConstrain</c>.</summary>
    public static RuleCondition HealthBelow(double fraction) => Game("EnemyHealthPercentageConstrain", ("minPercentage", 0.0), ("maxPercentage", fraction));

    /// <summary>Health at or above this fraction (0 to 1).</summary>
    public static RuleCondition HealthAbove(double fraction) => Game("EnemyHealthPercentageConstrain", ("minPercentage", fraction), ("maxPercentage", 1.0));

    /// <summary>Combat target within these metres. The game's <c>DistanceConstrain</c>.</summary>
    public static RuleCondition TargetWithin(double metres) => TargetBetween(0, metres);

    /// <summary>Combat target farther than these metres.</summary>
    public static RuleCondition TargetBeyond(double metres) => TargetBetween(metres, 1e9);

    /// <summary>Combat target between these distances, metres.</summary>
    public static RuleCondition TargetBetween(double min, double max) => Game("DistanceConstrain", ("min", min), ("max", max));

    /// <summary>The creature has this buff. Kiln's kind: as of the last few ticks.</summary>
    public static RuleCondition HasBuff(Guid buff) => new(new JsonObject { ["kiln"] = "buff", ["buff"] = buff.ToString() });

    /// <summary>Time of day within [from, to) hours; wraps past midnight when from &gt; to. Kiln's kind.</summary>
    public static RuleCondition TimeBetween(double from, double to) => new(new JsonObject { ["kiln"] = "time", ["from"] = from, ["to"] = to });

    /// <summary>It is night: 18 to 6. Use <see cref="TimeBetween"/> for a world with other hours.</summary>
    public static RuleCondition Night => TimeBetween(18, 6);

    /// <summary>It is day: 6 to 18.</summary>
    public static RuleCondition Day => TimeBetween(6, 18);

    /// <summary>The creature is in the calling mod's phase <paramref name="name"/>, set by <see cref="Act.SetPhase(string)"/>
    /// or <see cref="CreatureRef.SetPhase(string)"/>. Works on any body.</summary>
    public static RuleCondition InPhase(string name) => InPhase(RuleIds.Phase(name));

    /// <summary>The creature is in this phase: Kiln's, or the game's on a body that has phases.</summary>
    public static RuleCondition InPhase(Guid phase) => new(new JsonObject { ["kiln"] = "phase", ["phase"] = phase.ToString() });

    /// <summary>Your own condition. <paramref name="check"/> runs on the tick thread with the full API, every
    /// <paramref name="every"/> seconds, for each creature that uses the rule. Kiln stores the result; the rule reads that.
    /// So the value is as old as its last check, and false until the first one.</summary>
    public static RuleCondition Custom(Func<NpcView, bool> check, double every = 1)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new RuleCondition(new JsonObject { ["kiln"] = "condition" }, new RuleSlotUse(check, every, null));
    }

    /// <summary>A condition registered by name with <see cref="Arsenals.Condition"/>, here or in another mod. It reads false
    /// until the name is registered.</summary>
    public static RuleCondition Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new RuleCondition(new JsonObject { ["kiln"] = "condition" }, new RuleSlotUse(null, 1, name));
    }

    /// <summary>A handler on the game's job thread, registered with <see cref="Arsenals.JobHandler"/>. For hot paths: it may read
    /// only what is safe there and call no tick API. <see cref="Custom"/> is what you want unless you measured otherwise.</summary>
    [AdvancedApi]
    public static RuleCondition Job(uint id, uint arg = 0) => new(new JsonObject { ["kiln"] = "job", ["id"] = id, ["arg"] = arg });

    /// <summary>Any of the game's constraint kinds, by type name (<c>"StateConstrain"</c>) and field values as the game's
    /// reflection names them. Enums and flags take names; times take seconds; lists of flags take arrays.</summary>
    public static RuleCondition Game(string type, params (string Field, object? Value)[] fields) => new(RuleJson.Game(type, fields));
}

/// <summary>Actions. Kiln's kinds and your code run after the game's rule says so; your code runs on the tick thread.</summary>
public static class Act
{
    /// <summary>Your own action: queued when the rule fires, run on the tick thread in the next step with the full API.
    /// Nothing about it runs on the game's job threads.</summary>
    public static RuleAction Custom(Action<NpcView> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RuleAction(new JsonObject { ["kiln"] = "queue" }, new RuleActionUse(run, null));
    }

    /// <summary>An action registered by name with <see cref="Arsenals.Action"/>.</summary>
    public static RuleAction Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new RuleAction(new JsonObject { ["kiln"] = "queue" }, new RuleActionUse(null, name));
    }

    /// <summary>Sets the creature's phase (see <see cref="Is.InPhase(string)"/>). Works on any body. For a body's own phases
    /// and the rule modes that follow them, use the game's <c>SetPhaseAction</c>.</summary>
    public static RuleAction SetPhase(string name) => SetPhase(RuleIds.Phase(name));

    /// <summary>Sets the creature's phase by GUID.</summary>
    public static RuleAction SetPhase(Guid phase) => new(new JsonObject { ["kiln"] = "phase", ["phase"] = phase.ToString() });

    /// <summary>Starts the calling mod's attack <paramref name="name"/> through the game's own selection, past its style,
    /// range and constraints. It runs when the creature is in Combat; a rule usually says so with
    /// <c>Is.Game("StateConstrain", ("states", new[] { "Combat" }))</c>.</summary>
    public static RuleAction Attack(string name) => Attack(RuleIds.Attack(name));

    /// <summary>Starts the attack with this GUID.</summary>
    public static RuleAction Attack(Guid attack) => new(new JsonObject { ["kiln"] = "attack", ["attack"] = attack.ToString() });

    /// <summary>A handler on the game's job thread, registered with <see cref="Arsenals.JobHandler"/>. For hot paths: it may read
    /// only what is safe there and call no tick API. <see cref="Custom"/> is what you want unless you measured otherwise.</summary>
    [AdvancedApi]
    public static RuleAction Job(uint id, uint arg = 0) => new(new JsonObject { ["kiln"] = "job", ["id"] = id, ["arg"] = arg });

    /// <summary>Any of the game's action kinds, by type name (<c>"SetCombatStateAction"</c>) and field values.</summary>
    public static RuleAction Game(string type, params (string Field, object? Value)[] fields) => new(RuleJson.Game(type, fields));
}

internal static class RuleJson
{
    internal static JsonObject Game(string type, (string Field, object? Value)[] fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        var j = new JsonObject { ["type"] = type };
        foreach (var (f, v) in fields) j[f] = Value(v);
        return j;
    }

    internal static JsonNode? Value(object? v) => v switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        Guid g => g.ToString(),
        TimeSpan t => t.TotalSeconds,
        string s => s,
        bool b => b,
        Enum e => e.ToString(),
        System.Collections.IEnumerable list => new JsonArray(list.Cast<object?>().Select(Value).ToArray()),
        _ => JsonValue.Create(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)),
    };

    // The friendly forms of a data pack, on top of what the writer takes as it is.
    internal static RuleCondition Condition(JsonElement e, string mod)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ArgumentException($"a condition must be an object: {e.GetRawText()}");
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "healthBelow": return Is.HealthBelow(p.Value.GetDouble());
                case "healthAbove": return Is.HealthAbove(p.Value.GetDouble());
                case "targetWithin": return Is.TargetWithin(p.Value.GetDouble());
                case "targetBeyond": return Is.TargetBeyond(p.Value.GetDouble());
                case "hasBuff": return Is.HasBuff(Guid.Parse(p.Value.GetString() ?? ""));
                case "time": return Is.TimeBetween(p.Value[0].GetDouble(), p.Value[1].GetDouble());
                case "night": return Is.Night;
                case "day": return Is.Day;
                case "inPhase": return Is.InPhase(PhaseOf(p.Value, mod));
                case "custom": return Is.Named(p.Value.GetString() ?? "");
                case "not": return Condition(p.Value, mod).Not();
            }
        }
        return new RuleCondition((JsonObject)JsonNode.Parse(e.GetRawText())!);
    }

    internal static RuleAction Action(JsonElement e, string mod)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ArgumentException($"an action must be an object: {e.GetRawText()}");
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "setPhase": return Act.SetPhase(PhaseOf(p.Value, mod));
                case "attack": return Act.Attack(AttackOf(p.Value, mod));
                case "custom": return Act.Named(p.Value.GetString() ?? "");
            }
        }
        return new RuleAction((JsonObject)JsonNode.Parse(e.GetRawText())!);
    }

    private static Guid PhaseOf(JsonElement v, string mod)
    {
        var s = v.GetString() ?? "";
        return Guid.TryParse(s, out var g) ? g : RuleIds.Derive(mod, "phase", s);
    }

    private static Guid AttackOf(JsonElement v, string mod)
    {
        var s = v.GetString() ?? "";
        return Guid.TryParse(s, out var g) ? g : RuleIds.Derive(mod, "attack", s);
    }
}

/// <summary>When a rule is looked at. <c>keen::enemy01::BehaviorMode</c>.</summary>
public enum RuleMode
{
    /// <summary>Every <see cref="ArsenalRule.Every"/> seconds while no other rule is running.</summary>
    OnUpdate = 0,
    /// <summary>Entering one of the body's own phases.</summary>
    OnEnter = 1,
    /// <summary>Leaving one of the body's own phases.</summary>
    OnExit = 2,
    OnStartAttack = 3,
    OnEndAttack = 4,
    /// <summary>Entering an AI state.</summary>
    OnEnterState = 5,
    /// <summary>Leaving an AI state.</summary>
    OnExitState = 6,
}

/// <summary>One attack of an arsenal. Named attacks get a stable GUID from the mod id and the name.</summary>
public sealed class ArsenalAttack
{
    /// <summary>Its name in this mod; <see cref="Act.Attack(string)"/> and <see cref="AttackControl.Force(string)"/> take it.</summary>
    public string? Name { get; init; }

    /// <summary>The GUID when the attack needs a given one; else it follows from <see cref="Name"/>.</summary>
    public Guid? Id { get; init; }

    /// <summary>Change the base arsenal's attack with this GUID instead of adding one. Fields you leave out stay.</summary>
    public Guid? Modify { get; init; }

    /// <summary>Change the base arsenal's attack with this index instead of adding one.</summary>
    public int? ModifyIndex { get; init; }

    /// <summary>The <c>ActorSequenceResource</c> it plays. A game sequence on a game body needs nothing on clients.</summary>
    public Guid? Sequence { get; init; }

    /// <summary>When the game considers it (<c>keen::enemy01::AttackStyle</c>): Normal, Retaliate, CounterAttack, StunBreak,
    /// Special, Initiate, InitiateFollowUp, IgnoreThreat, Flanking. Default for a new attack: Normal.</summary>
    public string[]? Style { get; init; }

    public double? Chance { get; init; }
    public double? Threat { get; init; }
    public double? MinRange { get; init; }
    public double? MaxRange { get; init; }
    public string[]? Flags { get; init; }

    /// <summary>Must hold for the game to pick it.</summary>
    public List<RuleCondition>? Selection { get; init; }

    /// <summary>Must hold for it to start once picked.</summary>
    public List<RuleCondition>? Execution { get; init; }

    /// <summary>Ends it when one holds.</summary>
    public List<RuleCondition>? Abort { get; init; }

    /// <summary>Any other field of <c>AttackDesscription</c>, by the game's name (speed, cooldownGroupID, ...).</summary>
    public Dictionary<string, object?>? Fields { get; init; }
}

/// <summary>One rule: when, if, then.</summary>
public sealed class ArsenalRule
{
    /// <summary>Its name in this mod. Gives it a stable GUID.</summary>
    public string? Name { get; init; }

    /// <summary>Change the base arsenal's rule with this GUID instead of adding one.</summary>
    public Guid? Modify { get; init; }

    /// <summary>Change the base arsenal's rule with this index instead of adding one.</summary>
    public int? ModifyIndex { get; init; }

    public RuleMode When { get; init; } = RuleMode.OnUpdate;

    /// <summary>Seconds between checks of an OnUpdate rule, at least. Default 1.</summary>
    public double Every { get; init; } = 1;

    /// <summary>Seconds between checks, at most. Default the same as <see cref="Every"/>.</summary>
    public double? EveryMax { get; init; }

    /// <summary>Seconds a rule rests after it fired.</summary>
    public double Cooldown { get; init; }

    /// <summary>Chance it fires when its conditions hold, 0 to 1.</summary>
    public double Chance { get; init; } = 1;

    public List<RuleCondition> If { get; init; } = [];
    public List<RuleCondition>? AbortIf { get; init; }
    public List<RuleAction> Do { get; init; } = [];
}

/// <summary>What to write into an arsenal.</summary>
public sealed class ArsenalSpec
{
    /// <summary>A new arsenal starts with this one's attacks and rules (the low-level way; a block does not name one).</summary>
    public Guid? From { get; init; }

    public List<ArsenalAttack> Attacks { get; init; } = [];
    public List<ArsenalRule> Rules { get; init; } = [];

    /// <summary>Decides the attack for each creature, on the tick thread, every <see cref="PickEvery"/> seconds: an attack
    /// name or null for the game's own choice. A creature using this arsenal does not start an attack until it has had an
    /// answer.</summary>
    public Func<NpcView, string?>? PickAttack { get; init; }

    /// <summary>Decides the combat target for each creature, on the tick thread, every <see cref="PickEvery"/> seconds; default
    /// for the game's own choice.</summary>
    public Func<NpcView, EntityId>? PickTarget { get; init; }

    public double PickEvery { get; init; } = 0.25;
}

/// <summary>An arsenal as the AI reads it now.</summary>
public sealed record ArsenalInfo(Guid Id, bool IsKiln, ulong Address, IReadOnlyList<ArsenalInfo.Attack> Attacks, IReadOnlyList<ArsenalInfo.Rule> Rules, IReadOnlyList<string> Problems)
{
    public sealed record Attack(int Index, Guid Id, Guid Sequence, int Style);
    public sealed record Rule(int Index, Guid Id, int Mode, int When, int Actions);
}

/// <summary>What the rule system has resolved and done so far. Any thread, except that the counts of creatures read 0 off the tick.</summary>
public sealed record RulesStatus(bool Writer, bool Dispatcher, bool Attacks, bool AiSwitch, bool RuleHooks, bool AttackHooks, int Arsenals, int Tracked,
    int Overrides, int AiOff, int MapCapacity, int MapCount, ulong Constraints, ulong ConstraintsTrue, ulong Actions, ulong ActionsDropped, ulong Selects,
    ulong SelectsForced, ulong SelectsFiltered, ulong PicksWaiting, ulong FollowupsRefused, ulong Starts, ulong StartsOffOverride, ulong SequencesRefused,
    ulong PreNs, ulong PreCalls, ulong JobTicks, ulong JobCalls, ulong JobTickHz);

/// <summary>A mod's additions to one arsenal. They go away with <see cref="Dispose"/> or when the mod unloads.</summary>
public sealed class ArsenalHandle : IDisposable
{
    internal ArsenalHandle(Guid id, Creatures.OwnerKey owner, string source)
    {
        Id = id;
        Owner = owner;
        Source = source;
    }

    /// <summary>The arsenal's GUID: set it on a creature with <see cref="CreatureRef.Arsenal"/> or a variant's AI values.</summary>
    public Guid Id { get; }

    internal Creatures.OwnerKey Owner { get; }
    internal string Source { get; }
    internal readonly List<int> Slots = [];
    internal readonly List<uint> Actions = [];
    internal Func<NpcView, string?>? PickAttack;
    internal Func<NpcView, EntityId>? PickTarget;
    internal double PickEvery = 0.25;
    internal bool Removed;

    /// <summary>Takes this mod's additions out; the game's arsenal comes back when nothing else adds to it.</summary>
    public void Dispose() => Arsenals.Remove(this);
}

/// <summary>Overrides one creature's attack choice. Tick thread. A layer of the calling mod; goes with it.</summary>
/// <remarks>The override holds on every path the game starts an attack by: its selection, a rule's <c>SelectAttackAction</c>,
/// a sequence's combo follow-up, a rule playing an attack's sequence directly. docs/re/npc-internals.md 4.4 lists them.</remarks>
public readonly unsafe struct AttackControl
{
    private readonly EntityId _entity;
    private readonly Creatures.OwnerKey _owner;

    internal AttackControl(EntityId entity, Creatures.OwnerKey owner)
    {
        _entity = entity;
        _owner = owner;
    }

    /// <summary>Whenever the creature attacks, it starts this one, past its style, range, cooldown and constraints.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The arsenal the creature reads now has no attack at <paramref name="index"/>.</exception>
    /// <exception cref="NotSupportedException">The creature has no arsenal, or attack overrides are off on this build.</exception>
    [TickThreadOnly]
    public void Force(int index) => Set(index, ~0u);

    /// <summary>Forces the calling mod's attack by name, in the arsenal the creature reads now.</summary>
    [TickThreadOnly]
    public void Force(string name) => Force(RuleIds.Attack(name));

    /// <summary>Forces an attack by GUID, in the arsenal the creature reads now.</summary>
    [TickThreadOnly]
    public void Force(Guid attack)
    {
        var index = Arsenals.AttackIndexOf(_entity, attack);
        if (index < 0) throw new KeyNotFoundException($"entity {_entity}: its arsenal has no attack {attack}");
        Force(index);
    }

    /// <summary>The game picks with its own rules, among these indexes only (0 to 31).</summary>
    [TickThreadOnly]
    public void Allow(params int[] indexes)
    {
        uint mask = 0;
        foreach (var i in indexes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(i);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(i, 31);
            mask |= 1u << i;
        }
        Set(-1, mask);
    }

    /// <summary>The game chooses as it does without an override, and an arsenal with <see cref="ArsenalSpec.PickAttack"/> stops waiting.</summary>
    [TickThreadOnly]
    public void Natural() => Set(-1, ~0u);

    /// <summary>Drops this mod's override.</summary>
    [TickThreadOnly]
    public void Clear()
    {
        var t = Creatures.Tick("AttackControl.Clear");
        var o = Creatures.RequireOwner(_owner, "AttackControl.Clear");
        Creatures.Check(t->SetAttackOverride(o.Native, _entity.Value, null), $"entity {_entity}: attack override");
    }

    private void Set(int forced, uint allow)
    {
        var t = Creatures.Tick("AttackControl");
        var o = Creatures.RequireOwner(_owner, "AttackControl");
        var v = new CreaturesNative.AttackOverride { Size = (uint)sizeof(CreaturesNative.AttackOverride), Forced = forced, Allow = allow };
        var rc = t->SetAttackOverride(o.Native, _entity.Value, &v);
        if (forced >= 0 && rc == CreaturesNative.InvalidArgument)
            throw new ArgumentOutOfRangeException(nameof(forced), forced, $"entity {_entity}: its arsenal has no attack {forced}");
        if (forced >= 0 && rc == CreaturesNative.Unavailable)
            throw new NotSupportedException($"entity {_entity}: cannot force an attack (no arsenal, or attack overrides are off here)");
        Creatures.Check(rc, $"entity {_entity}: attack override");
    }
}

/// <summary>
/// Arsenals as data, and rules with Kiln's own kinds. An arsenal holds a creature's attacks and rules, and the AI looks it up by
/// GUID in every update. <see cref="Define"/> makes a new one for a creature to use; <see cref="Extend"/> adds to one the game
/// has, for every creature using it. Several mods on one arsenal merge. All of it is server side: a game sequence on a game body
/// needs nothing on clients.
/// </summary>
public static unsafe class Arsenals
{
    private static readonly List<ArsenalHandle> Handles = [];
    private static int _generation;

    /// <summary>True when arsenals can be written on this build.</summary>
    public static bool Available => Status is { Writer: true };

    /// <summary>What resolved and what ran; null without kiln.creatures.</summary>
    public static RulesStatus? Status
    {
        get
        {
            var t = CreaturesNative.T;
            if (t == null) return null;
            var s = new CreaturesNative.RulesStatus { Size = (uint)sizeof(CreaturesNative.RulesStatus) };
            if (t->GetRulesStatus(&s) != CreaturesNative.Ok) return null;
            return new RulesStatus(s.Writer != 0, s.Dispatcher != 0, s.Attacks != 0, s.AiSwitch != 0, s.RuleHooks != 0, s.AttackHooks != 0, (int)s.Arsenals,
                (int)s.Tracked, (int)s.Overrides, (int)s.AiOff, (int)s.MapCapacity, (int)s.MapCount, s.Constraints, s.ConstraintsTrue, s.Actions, s.ActionsDropped,
                s.Selects, s.SelectsForced, s.SelectsFiltered, s.PicksWaiting, s.FollowupsRefused, s.Starts, s.StartsOffOverride, s.SequencesRefused,
                s.PreNs, s.PreCalls, s.JobTicks, s.JobCalls, s.JobTickHz);
        }
    }

    /// <summary>Times the job-thread side (<see cref="RulesStatus.JobTicks"/>); a few nanoseconds per Kiln kind while on.</summary>
    public static bool Timing
    {
        set => RulePump.SetTiming(value);
    }

    /// <summary>A new arsenal of the calling mod: GUID <c>UUIDv5(&lt;mod&gt;:arsenal:&lt;name&gt;)</c>. It exists from the next step.
    /// Throws <see cref="ArgumentException"/> with the reason for a type or field the game's reflection does not have; a limit
    /// that depends on the base arsenal (32 attacks, 32 rules) shows in <see cref="Info"/>'s problems instead.</summary>
    [TickThreadOnly]
    public static ArsenalHandle Define(string name, ArsenalSpec spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var owner = Creatures.RequireOwner(Creatures.OwnerKey.Caller, "Arsenals.Define");
        return Write(RuleIds.Derive(owner.Name, "arsenal", name), spec, owner, owner.Name, "");
    }

    /// <summary>An arsenal written for a block: its GUID, owner and mod given, so a block in a data file (with no mod code running) has one too.
    /// What the same owner and source wrote before is replaced.</summary>
    internal static ArsenalHandle DefineFor(Guid id, ArsenalSpec spec, Creatures.OwnerKey owner, string mod, string source)
    {
        // written later than the mod's OnLoad (a game client's hosted world): its code conditions and actions are still the mod's, and go with it
        var outer = ModContext.CurrentMod;
        if (owner.Mod is { } m) ModContext.CurrentMod = m;
        try
        {
            RemoveOwned(owner, source);
            return Write(id, spec, owner, mod, source);
        }
        finally { ModContext.CurrentMod = outer; }
    }

    /// <summary>Adds to an arsenal the game has, for every creature that uses it, by its GUID. The game's own attacks and rules stay
    /// at their indexes; yours come after them.</summary>
    [TickThreadOnly]
    public static ArsenalHandle Extend(Guid arsenal, ArsenalSpec spec)
    {
        var owner = Creatures.RequireOwner(Creatures.OwnerKey.Caller, "Arsenals.Extend");
        return Write(arsenal, spec, owner, owner.Name, "");
    }

    /// <summary>Adds to the arsenal a template names.</summary>
    [TickThreadOnly]
    public static ArsenalHandle Extend(string template, ArsenalSpec spec)
    {
        var t = Creatures.Template(template) ?? throw new KeyNotFoundException($"no creature template '{template}'");
        return Extend(t.Arsenal, spec);
    }

    /// <summary>The arsenal a template names, or null.</summary>
    public static Guid? OfTemplate(string template) => Creatures.Template(template)?.Arsenal;

    /// <summary>Registers a condition by name for rules that name it (<see cref="Is.Named"/>, <c>{"custom": name}</c> in a data
    /// pack). Runs on the tick thread, as <see cref="Is.Custom"/>.</summary>
    public static void Condition(string name, Func<NpcView, bool> check, double every = 1)
    {
        ArgumentNullException.ThrowIfNull(check);
        Creatures.RequireOwner(Creatures.OwnerKey.Caller, "Arsenals.Condition");
        RulePump.BindCondition(name, check, every, ModContext.CurrentMod);
    }

    /// <summary>Registers an action by name for rules that name it (<see cref="Act.Named"/>, <c>{"custom": name}</c> in a data pack).
    /// Runs on the tick thread, as <see cref="Act.Custom"/>.</summary>
    public static void Action(string name, Action<NpcView> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Creatures.RequireOwner(Creatures.OwnerKey.Caller, "Arsenals.Action");
        RulePump.BindAction(name, run, ModContext.CurrentMod);
    }

    /// <summary>Registers a handler for <see cref="Is.Job"/> and <see cref="Act.Job"/>, id 1 to 63: an
    /// <c>[UnmanagedCallersOnly]</c> function <c>uint (uint entity, uint id, uint arg, void* user)</c>. It runs on the game's job
    /// threads inside the sim step. Zero removes it; it also goes when the mod unloads.</summary>
    [AdvancedApi]
    [TickThreadOnly]
    public static void JobHandler(uint id, nint function, nint user = 0)
    {
        var t = Creatures.Tick("Arsenals.JobHandler");
        var o = Creatures.RequireOwner(Creatures.OwnerKey.Caller, "Arsenals.JobHandler");
        Creatures.Check(t->RulesJobHandler(o.Native, id, (void*)function, (void*)user), $"job handler {id}");
    }

    /// <summary>What the tick side of Kiln's kinds has cost so far: this mod's and other mods' conditions, actions and picks, in the Pre they ran in.</summary>
    public static (double Milliseconds, long Pumps) PumpCost => RulePump.Cost;

    /// <summary>The arsenal with this GUID as the AI reads it now, or null. Tick thread.</summary>
    [TickThreadOnly]
    public static ArsenalInfo? Info(Guid id)
    {
        var t = Creatures.Tick("Arsenals.Info");
        var g = stackalloc CreaturesNative.GuidBytes[1];
        g[0] = CreaturesNative.GuidBytes.From(id);
        var text = NativeText.Read((b, cap, len) => t->ArsenalInfo(g, b, cap, len), out var rc);
        if (rc == CreaturesNative.NotFound) return null;
        Creatures.Check(rc, "arsenal info");
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        var attacks = r.GetProperty("attacks").EnumerateArray().Select(a => new ArsenalInfo.Attack(a.GetProperty("index").GetInt32(),
            Guid.Parse(a.GetProperty("guid").GetString()!), Guid.Parse(a.GetProperty("sequence").GetString()!), a.GetProperty("style").GetInt32())).ToList();
        var rules = r.GetProperty("rules").EnumerateArray().Select(a => new ArsenalInfo.Rule(a.GetProperty("index").GetInt32(),
            Guid.TryParse(a.GetProperty("guid").GetString(), out var rg) ? rg : default, a.GetProperty("mode").GetInt32(), a.GetProperty("when").GetInt32(),
            a.GetProperty("actions").GetInt32())).ToList();
        var problems = r.GetProperty("problems").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        return new ArsenalInfo(id, r.GetProperty("kiln").GetBoolean(), r.GetProperty("address").GetUInt64(), attacks, rules, problems);
    }

    /// <summary>Rebuilds Kiln's arsenals from the game's current data; Kiln.Creatures does this after a content change. Tick thread.</summary>
    [TickThreadOnly]
    public static int Refresh()
    {
        var t = Creatures.Tick("Arsenals.Refresh");
        uint n = 0;
        t->ArsenalRefresh(&n);
        return (int)n;
    }

    /// <summary>Reads every arsenal of the game and writes it back through the writer in memory: how many, and how many differ. Tick thread.</summary>
    [TickThreadOnly]
    public static (int Checked, int Bad) SelfTest()
    {
        var t = Creatures.Tick("Arsenals.SelfTest");
        uint c = 0, b = 0;
        Creatures.Check(t->ArsenalSelftest(&c, &b), "arsenal self test");
        return ((int)c, (int)b);
    }

    /// <summary>How many times a condition slot was evaluated, and came out true. For tests and tuning.</summary>
    public static (ulong Evaluated, ulong True) ConditionStats(RuleCondition condition)
    {
        var slot = RulePump.SlotOf(condition);
        var t = CreaturesNative.Require("Arsenals.ConditionStats");
        if (slot < 0) return default;
        ulong e = 0, y = 0;
        t->RulesConditionStats((uint)slot, &e, &y);
        return (e, y);
    }

    internal static void Remove(ArsenalHandle h)
    {
        if (h.Removed) return;
        h.Removed = true;
        lock (Handles) Handles.Remove(h);
        _generation++;
        var t = CreaturesNative.T;
        if (t == null) return;
        var g = CreaturesNative.GuidBytes.From(h.Id);
        t->ArsenalRemove(h.Owner.Native, &g);
        if (h.PickAttack != null) t->AttackPickArsenal(h.Owner.Native, &g, 0);
        RulePump.Release(h);
    }

    internal static void Retire(ModContext mod)
    {
        List<ArsenalHandle> mine;
        lock (Handles) mine = Handles.Where(h => h.Owner.Mod == mod).ToList();
        foreach (var h in mine) Remove(h);
        RulePump.Retire(mod);
    }

    internal static int RemoveOwned(Creatures.OwnerKey owner, string? source)
    {
        List<ArsenalHandle> mine;
        lock (Handles) mine = Handles.Where(h => h.Owner.Native == owner.Native && (source is null || h.Source == source)).ToList();
        foreach (var h in mine) Remove(h);
        return mine.Count;
    }

    internal static IReadOnlyList<ArsenalHandle> HandlesOf(Guid arsenal)
    {
        lock (Handles) return Handles.Where(h => h.Id == arsenal).ToList();
    }

    // { "id"|"extends": name or GUID, "from"?, "attacks": [...], "rules": [...] }: the friendly kinds of RuleJson, and every field
    // the writer takes.
    internal static int ApplyPack(JsonElement list, Creatures.OwnerKey owner, string source, List<string> errors)
    {
        var mod = owner.Name.StartsWith("pack:", StringComparison.Ordinal) ? owner.Name[5..] : owner.Name.StartsWith("mod:", StringComparison.Ordinal) ? owner.Name[4..] : owner.Name;
        var n = 0;
        foreach (var e in list.EnumerateArray())
        {
            try
            {
                Guid id;
                if (e.TryGetProperty("extends", out var ex))
                {
                    var s = ex.GetString() ?? "";
                    id = Guid.TryParse(s, out var g) ? g : (Creatures.Template(s)?.Arsenal ?? throw new ArgumentException($"no creature template '{s}'"));
                }
                else
                {
                    id = RuleIds.Derive(mod, "arsenal", e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : throw new ArgumentException("an arsenal needs \"id\" or \"extends\""));
                }
                Guid? from = null;
                if (e.TryGetProperty("from", out var f))
                {
                    var s = f.GetString() ?? "";
                    from = Guid.TryParse(s, out var g) ? g : (Creatures.Template(s)?.Arsenal ?? throw new ArgumentException($"no creature template '{s}'"));
                }
                var spec = new ArsenalSpec
                {
                    From = from,
                    Attacks = e.TryGetProperty("attacks", out var atk) ? atk.EnumerateArray().Select(a => PackAttack(a, mod)).ToList() : [],
                    Rules = e.TryGetProperty("rules", out var rl) ? rl.EnumerateArray().Select(r => PackRule(r, mod)).ToList() : [],
                };
                Write(id, spec, owner, mod, source);
                n++;
            }
            catch (Exception x) when (x is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException or NotSupportedException or JsonException)
            {
                errors.Add($"creatures.arsenals: {x.Message}");
            }
        }
        return n;
    }

    private static List<RuleCondition>? Conds(JsonElement parent, string name, string mod) =>
        parent.TryGetProperty(name, out var v) ? v.EnumerateArray().Select(c => RuleJson.Condition(c, mod)).ToList() : null;

    private static ArsenalAttack PackAttack(JsonElement a, string mod)
    {
        var known = new HashSet<string> { "name", "id", "modify", "index", "sequence", "style", "chance", "threat", "minRange", "maxRange", "flags", "selection", "execution", "abort" };
        var fields = new Dictionary<string, object?>();
        foreach (var p in a.EnumerateObject())
            if (!known.Contains(p.Name)) fields[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        var modify = a.TryGetProperty("modify", out var m) ? m.GetString() : null;
        return new ArsenalAttack
        {
            Name = a.TryGetProperty("name", out var n) ? n.GetString() : null,
            Id = a.TryGetProperty("id", out var i) ? Guid.Parse(i.GetString() ?? "") : modify is null && a.TryGetProperty("name", out var nn) ? RuleIds.Derive(mod, "attack", nn.GetString() ?? "") : null,
            Modify = modify is null ? null : Guid.Parse(modify),
            ModifyIndex = a.TryGetProperty("index", out var ix) ? ix.GetInt32() : null,
            Sequence = a.TryGetProperty("sequence", out var q) ? Guid.Parse(q.GetString() ?? "") : null,
            Style = a.TryGetProperty("style", out var st) ? st.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null,
            Chance = a.TryGetProperty("chance", out var c) ? c.GetDouble() : null,
            Threat = a.TryGetProperty("threat", out var th) ? th.GetDouble() : null,
            MinRange = a.TryGetProperty("minRange", out var mi) ? mi.GetDouble() : null,
            MaxRange = a.TryGetProperty("maxRange", out var ma) ? ma.GetDouble() : null,
            Flags = a.TryGetProperty("flags", out var fl) ? fl.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null,
            Selection = Conds(a, "selection", mod),
            Execution = Conds(a, "execution", mod),
            Abort = Conds(a, "abort", mod),
            Fields = fields.Count > 0 ? fields : null,
        };
    }

    private static ArsenalRule PackRule(JsonElement r, string mod)
    {
        var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
        double every = 1;
        double? everyMax = null;
        if (r.TryGetProperty("every", out var e))
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                every = e[0].GetDouble();
                everyMax = e[1].GetDouble();
            }
            else
            {
                every = e.GetDouble();
            }
        }
        return new ArsenalRule
        {
            Name = name,
            Modify = r.TryGetProperty("modify", out var m) ? Guid.Parse(m.GetString() ?? "") : null,
            ModifyIndex = r.TryGetProperty("index", out var ix) ? ix.GetInt32() : null,
            When = r.TryGetProperty("mode", out var w) ? Enum.Parse<RuleMode>(w.GetString() ?? "", ignoreCase: true) : RuleMode.OnUpdate,
            Every = every,
            EveryMax = everyMax,
            Cooldown = r.TryGetProperty("cooldown", out var c) ? c.GetDouble() : 0,
            Chance = r.TryGetProperty("chance", out var ch) ? ch.GetDouble() : 1,
            If = Conds(r, "when", mod) ?? [],
            AbortIf = Conds(r, "abort", mod),
            Do = r.TryGetProperty("do", out var d) ? d.EnumerateArray().Select(x => RuleJson.Action(x, mod)).ToList() : [],
        };
    }

    private static ArsenalHandle Write(Guid id, ArsenalSpec spec, Creatures.OwnerKey owner, string mod, string source)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var t = Creatures.Tick("Arsenals");
        if (t->Header.Size < (uint)sizeof(CreaturesNative.Table)) throw new NotSupportedException("Arsenals needs a newer kiln.creatures");
        // A new handle replaces what this owner wrote here before.
        foreach (var old in HandlesOf(id).Where(h => h.Owner.Native == owner.Native).ToList()) Remove(old);
        var h = new ArsenalHandle(id, owner, source) { PickAttack = spec.PickAttack, PickTarget = spec.PickTarget, PickEvery = spec.PickEvery };
        var ok = false;
        try
        {
            var doc = new JsonObject { ["guid"] = id.ToString() };
            if (spec.From is { } from) doc["from"] = from.ToString();
            var attacks = new JsonArray();
            foreach (var a in spec.Attacks) attacks.Add(AttackJson(a, mod, h));
            doc["attacks"] = attacks;
            var rules = new JsonArray();
            var nameless = 0;
            foreach (var r in spec.Rules) rules.Add(RuleToJson(r, mod, h, ref nameless));
            if (spec.PickTarget != null)
            {
                // A rule finds the creatures: it fires for each one that uses this arsenal, in its AI update.
                var pick = spec.PickTarget;
                rules.Add(RuleToJson(new ArsenalRule
                {
                    Name = "kiln.pick-target",
                    Every = spec.PickEvery,
                    Do = [Act.Custom(npc => RulePump.ApplyTargetPick(npc, pick, spec.PickEvery))],
                }, mod, h, ref nameless));
            }
            doc["rules"] = rules;
            Span<byte> error = stackalloc byte[1024];
            var u = new CreaturesNative.Utf8(doc.ToJsonString());
            int rc;
            nuint len = 0;
            fixed (byte* p = u.Bytes)
            fixed (byte* e = error)
                rc = t->ArsenalDefine(owner.Native, u.Str(p), e, (nuint)error.Length, &len);
            if (rc != CreaturesNative.Ok)
            {
                var text = System.Text.Encoding.UTF8.GetString(error[..Math.Min((int)len, error.Length - 1)]);
                if (rc == CreaturesNative.Unavailable) throw new NotSupportedException($"arsenal {id}: {text}");
                throw new ArgumentException($"arsenal {id}: {text}");
            }
            if (spec.PickAttack != null)
            {
                var g = CreaturesNative.GuidBytes.From(id);
                rc = t->AttackPickArsenal(owner.Native, &g, 1);
                if (rc == CreaturesNative.Unavailable) throw new NotSupportedException($"arsenal {id}: attack overrides are not available on this build");
                Creatures.Check(rc, "PickAttack");
            }
            ok = true;
        }
        finally
        {
            if (!ok) RulePump.Release(h);
        }
        lock (Handles) Handles.Add(h);
        _generation++;
        return h;
    }

    private static JsonObject AttackJson(ArsenalAttack a, string mod, ArsenalHandle h)
    {
        var j = new JsonObject();
        if (a.Modify is { } m) j["modify"] = m.ToString();
        else if (a.ModifyIndex is { } mi) j["index"] = mi;
        else
        {
            var id = a.Id ?? (a.Name is null ? throw new ArgumentException("a new attack needs a Name or an Id") : RuleIds.Derive(mod, "attack", a.Name));
            j["guid"] = id.ToString();
        }
        if (a.Sequence is { } s) j["sequence"] = s.ToString();
        if (a.Style != null) j["attackStyle"] = RuleJson.Value(a.Style);
        if (a.Chance is { } c) j["selectionChance"] = c;
        if (a.Threat is { } th) j["threat"] = th;
        if (a.MinRange is { } r0) j["minRange"] = r0;
        if (a.MaxRange is { } r1) j["maxRange"] = r1;
        if (a.Flags != null) j["flags"] = RuleJson.Value(a.Flags);
        if (a.Fields != null)
            foreach (var (k, v) in a.Fields)
                j[k] = RuleJson.Value(v);
        if (a.Selection != null) j["selectionConstrain"] = Conditions(a.Selection, h);
        if (a.Execution != null) j["executionConstrain"] = Conditions(a.Execution, h);
        if (a.Abort != null) j["abortConstraints"] = Conditions(a.Abort, h);
        return j;
    }

    private static JsonObject RuleToJson(ArsenalRule r, string mod, ArsenalHandle h, ref int nameless)
    {
        var j = new JsonObject();
        if (r.Modify is { } m) j["modify"] = m.ToString();
        else if (r.ModifyIndex is { } mi) j["index"] = mi;
        else
        {
            var name = r.Name ?? $"#{h.Id}:{nameless++}";
            j["guid"] = RuleIds.Derive(mod, "rule", r.Name ?? name).ToString();
            j["mode"] = r.When.ToString();
            j["every"] = new JsonArray(r.Every, r.EveryMax ?? r.Every);
            j["cooldown"] = r.Cooldown;
        }
        if (r.Chance < 1) j["chance"] = r.Chance;
        j["when"] = Conditions(r.If, h);
        if (r.AbortIf != null) j["abort"] = Conditions(r.AbortIf, h);
        var actions = new JsonArray();
        foreach (var a in r.Do) actions.Add(RulePump.Bind(a, h));
        j["do"] = actions;
        return j;
    }

    private static JsonArray Conditions(List<RuleCondition> list, ArsenalHandle h)
    {
        var a = new JsonArray();
        foreach (var c in list) a.Add(RulePump.Bind(c, h));
        return a;
    }

    // ---- attack lookups ----

    private static readonly Dictionary<Guid, ArsenalInfo?> InfoCache = [];
    private static int _pumped;

    // An arsenal is rebuilt in the Pre after a change, before this pump: the cache starts over then.
    internal static void Pumped()
    {
        if (_pumped == _generation) return;
        _pumped = _generation;
        InfoCache.Clear();
    }

    internal static int AttackIndex(Guid arsenal, Guid attack)
    {
        for (var fresh = false; ; fresh = true)
        {
            if (fresh || !InfoCache.TryGetValue(arsenal, out var info)) InfoCache[arsenal] = info = Info(arsenal);
            if (info != null)
                foreach (var a in info.Attacks)
                    if (a.Id == attack)
                        return a.Index;
            if (fresh) return -1;
        }
    }

    internal static int AttackIndexOf(EntityId entity, Guid attack)
    {
        var t = Creatures.Tick("Arsenals");
        CreaturesNative.GuidBytes g;
        uint known = 0;
        Creatures.Check(t->ArsenalOf(entity.Value, &g, &known), $"entity {entity}: arsenal");
        return AttackIndex(g.ToGuid(), attack);
    }

    internal static void ForgetCache() => InfoCache.Clear();
}

// The tick side of Kiln's kinds: conditions evaluated on a schedule, queued actions, attack picks. Runs from the creature
// listener in Pre, so a condition's value reaches the job threads before the coming step.
internal static unsafe class RulePump
{
    private sealed class Slot
    {
        public int Index;
        public string? Name;
        public Func<NpcView, bool>? Fn;
        public double Every = 1;
        public ModContext? Mod;
        public int Uses;
        public bool Warned;
        public readonly Dictionary<uint, long> Due = [];
    }

    private sealed class ActionEntry
    {
        public uint Id;
        public string? Name;
        public Action<NpcView>? Fn;
        public ModContext? Mod;
        public int Uses;
        public bool Warned;
        public long Runs;
    }

    private static readonly Slot?[] Slots = new Slot?[256];
    private static readonly Dictionary<RuleSlotUse, Slot> SlotsByUse = [];
    private static readonly Dictionary<string, Slot> SlotsByName = [];
    private static readonly Dictionary<uint, ActionEntry> ActionsById = [];
    private static readonly Dictionary<string, ActionEntry> ActionsByName = [];
    private static readonly Dictionary<RuleActionUse, ActionEntry> ActionsByUse = [];
    private static readonly Dictionary<uint, (ArsenalHandle Handle, long Due)> Picks = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static uint _nextAction = 1;
    private static bool _timing;
    private static uint _flags;

    internal static void SetTiming(bool on)
    {
        _timing = on;
        Flags();
    }

    private static void Flags()
    {
        var t = CreaturesNative.T;
        if (t == null) return;
        var every = Slots.Any(s => s is { Due.Count: > 0 }) || Picks.Count > 0;
        var f = (every ? 1u : 0u) | (_timing ? 2u : 0u);
        if (f == _flags) return;
        _flags = f;
        t->RulesPump(f);
    }

    internal static int SlotOf(RuleCondition c) =>
        c.Use != null && (c.Use.Fn != null && SlotsByUse.TryGetValue(c.Use, out var s) || c.Use.Name != null && SlotsByName.TryGetValue(c.Use.Name, out s)) ? s!.Index : -1;

    internal static long RunsOf(RuleAction a) =>
        a.Use != null && (a.Use.Fn != null && ActionsByUse.TryGetValue(a.Use, out var e) || a.Use.Name != null && ActionsByName.TryGetValue(a.Use.Name, out e)) ? e!.Runs : 0;

    private static Slot NewSlot()
    {
        for (var i = 0; i < Slots.Length; i++)
        {
            if (Slots[i] != null) continue;
            return Slots[i] = new Slot { Index = i };
        }
        throw new InvalidOperationException("more than 256 custom conditions in this process");
    }

    internal static JsonObject Bind(RuleCondition c, ArsenalHandle h)
    {
        var j = (JsonObject)c.Json.DeepClone();
        if (c.Use is not { } use) return j;
        Slot? s;
        if (use.Fn != null)
        {
            if (!SlotsByUse.TryGetValue(use, out s))
            {
                s = NewSlot();
                s.Fn = use.Fn;
                s.Every = use.Every;
                s.Mod = ModContext.CurrentMod;
                SlotsByUse[use] = s;
            }
        }
        else if (!SlotsByName.TryGetValue(use.Name!, out s))
        {
            s = NewSlot();
            s.Name = use.Name;
            SlotsByName[use.Name!] = s;
        }
        s.Uses++;
        h.Slots.Add(s.Index);
        j["slot"] = s.Index;
        return j;
    }

    internal static JsonObject Bind(RuleAction a, ArsenalHandle h)
    {
        var j = (JsonObject)a.Json.DeepClone();
        if (a.Use is not { } use) return j;
        ActionEntry? e;
        if (use.Fn != null)
        {
            if (!ActionsByUse.TryGetValue(use, out e))
            {
                e = new ActionEntry { Id = _nextAction++, Fn = use.Fn, Mod = ModContext.CurrentMod };
                ActionsByUse[use] = e;
                ActionsById[e.Id] = e;
            }
        }
        else if (!ActionsByName.TryGetValue(use.Name!, out e))
        {
            e = new ActionEntry { Id = _nextAction++, Name = use.Name };
            ActionsByName[use.Name!] = e;
            ActionsById[e.Id] = e;
        }
        e.Uses++;
        h.Actions.Add(e.Id);
        j["id"] = e.Id;
        return j;
    }

    internal static void BindCondition(string name, Func<NpcView, bool> check, double every, ModContext? mod)
    {
        if (!SlotsByName.TryGetValue(name, out var s))
        {
            s = NewSlot();
            s.Name = name;
            SlotsByName[name] = s;
        }
        else if (s.Fn != null && s.Mod != mod)
        {
            throw new InvalidOperationException($"condition '{name}' is already registered by {s.Mod?.Info.Id}");
        }
        s.Fn = check;
        s.Every = every;
        s.Mod = mod;
    }

    internal static void BindAction(string name, Action<NpcView> run, ModContext? mod)
    {
        if (!ActionsByName.TryGetValue(name, out var e))
        {
            e = new ActionEntry { Id = _nextAction++, Name = name };
            ActionsByName[name] = e;
            ActionsById[e.Id] = e;
        }
        else if (e.Fn != null && e.Mod != mod)
        {
            throw new InvalidOperationException($"action '{name}' is already registered by {e.Mod?.Info.Id}");
        }
        e.Fn = run;
        e.Mod = mod;
    }

    internal static void ApplyTargetPick(NpcView npc, Func<NpcView, EntityId> pick, double every)
    {
        var chosen = pick(npc);
        if (chosen.Value == 0) return;
        npc.Creature.PreferTarget(chosen, every * 3);
    }

    private static void FreeSlot(Slot s)
    {
        var t = CreaturesNative.T;
        if (t != null && Game.IsTickThread) t->RulesConditionClear((uint)s.Index);
        Slots[s.Index] = null;
        foreach (var k in SlotsByUse.Where(kv => kv.Value == s).Select(kv => kv.Key).ToList()) SlotsByUse.Remove(k);
        if (s.Name != null) SlotsByName.Remove(s.Name);
    }

    private static void FreeAction(ActionEntry e)
    {
        ActionsById.Remove(e.Id);
        foreach (var k in ActionsByUse.Where(kv => kv.Value == e).Select(kv => kv.Key).ToList()) ActionsByUse.Remove(k);
        if (e.Name != null) ActionsByName.Remove(e.Name);
    }

    internal static void Release(ArsenalHandle h)
    {
        foreach (var i in h.Slots)
        {
            if (Slots[i] is not { } s) continue;
            if (--s.Uses <= 0 && (s.Name is null || s.Fn is null)) FreeSlot(s);
        }
        h.Slots.Clear();
        foreach (var id in h.Actions)
        {
            if (!ActionsById.TryGetValue(id, out var e)) continue;
            if (--e.Uses <= 0 && (e.Name is null || e.Fn is null)) FreeAction(e);
        }
        h.Actions.Clear();
        foreach (var k in Picks.Where(kv => kv.Value.Handle == h).Select(kv => kv.Key).ToList()) Picks.Remove(k);
        Arsenals.ForgetCache();
        Flags();
    }

    // A mod is gone: nothing of its code may stay reachable.
    internal static void Retire(ModContext mod)
    {
        foreach (var s in Slots)
        {
            if (s == null || s.Mod != mod) continue;
            s.Fn = null;
            s.Mod = null;
            s.Due.Clear();
            if (s.Uses <= 0) FreeSlot(s);
        }
        foreach (var e in ActionsById.Values.ToList())
        {
            if (e.Mod != mod) continue;
            e.Fn = null;
            e.Mod = null;
            if (e.Uses <= 0) FreeAction(e);
        }
        Flags();
    }

    private static long _pumpTicks, _pumps;

    internal static (double, long) Cost => (_pumpTicks * 1000.0 / Stopwatch.Frequency, _pumps);

    internal static void Pump()
    {
        var t = CreaturesNative.T;
        if (t == null || t->Header.Size < (uint)sizeof(CreaturesNative.Table)) return;
        var started = Stopwatch.GetTimestamp();
        try { PumpCore(t); }
        finally
        {
            _pumpTicks += Stopwatch.GetTimestamp() - started;
            _pumps++;
        }
    }

    private static void PumpCore(CreaturesNative.Table* t)
    {
        Arsenals.Pumped();
        var buf = stackalloc CreaturesNative.RuleWork[64];
        var now = Clock.ElapsedMilliseconds;
        uint n;
        var changed = false;
        while ((n = t->RulesDrain(buf, 64)) != 0)
        {
            for (uint i = 0; i < n; i++)
            {
                ref var w = ref buf[i];
                switch (w.Kind)
                {
                    case CreaturesNative.WorkAction:
                        RunAction(w.Id, new EntityId(w.Entity));
                        break;
                    case CreaturesNative.WorkCondition:
                        if (w.Id < 256 && Slots[w.Id] is { } s)
                        {
                            s.Due[w.Entity] = now;
                            changed = true;
                        }
                        break;
                    case CreaturesNative.WorkPick:
                        changed |= AddPick(t, w.Entity, now);
                        break;
                    case CreaturesNative.WorkGone:
                        foreach (var sl in Slots) sl?.Due.Remove(w.Entity);
                        Picks.Remove(w.Entity);
                        changed = true;
                        break;
                }
            }
        }
        foreach (var s in Slots)
        {
            if (s == null || s.Due.Count == 0) continue;
            List<uint>? due = null;
            foreach (var kv in s.Due)
                if (kv.Value <= now) (due ??= []).Add(kv.Key);
            if (due == null) continue;
            foreach (var e in due)
            {
                s.Due[e] = now + (long)(s.Every * 1000);
                t->RulesConditionSet(e, (uint)s.Index, Evaluate(s, new EntityId(e)) ? 1u : 0u);
            }
        }
        if (Picks.Count > 0) RunPicks(t, now);
        if (changed) Flags();
    }

    private static bool Evaluate(Slot s, EntityId e)
    {
        var fn = s.Fn;
        if (fn == null) return false;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = s.Mod;
        try { return fn(new NpcView(e)); }
        catch (Exception x)
        {
            if (!s.Warned)
            {
                s.Warned = true;
                Backend.Current.Log(0, LogLevel.Error, $"Kiln.Creatures: a rule condition of {s.Mod?.Info.Id} threw and counts as false: {x}");
            }
            return false;
        }
        finally { ModContext.CurrentMod = outer; }
    }

    private static void RunAction(uint id, EntityId e)
    {
        if (!ActionsById.TryGetValue(id, out var a) || a.Fn is not { } fn) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = a.Mod;
        try
        {
            a.Runs++;
            fn(new NpcView(e));
        }
        catch (Exception x)
        {
            if (!a.Warned)
            {
                a.Warned = true;
                Backend.Current.Log(0, LogLevel.Error, $"Kiln.Creatures: a rule action of {a.Mod?.Info.Id} threw: {x}");
            }
        }
        finally { ModContext.CurrentMod = outer; }
    }

    private static bool AddPick(CreaturesNative.Table* t, uint entity, long now)
    {
        CreaturesNative.GuidBytes g;
        uint known = 0;
        if (t->ArsenalOf(entity, &g, &known) != CreaturesNative.Ok) return false;
        var handle = Arsenals.HandlesOf(g.ToGuid()).FirstOrDefault(h => h.PickAttack != null);
        if (handle == null) return false;
        Picks[entity] = (handle, now);
        return true;
    }

    private static void RunPicks(CreaturesNative.Table* t, long now)
    {
        List<uint>? due = null;
        foreach (var kv in Picks)
            if (kv.Value.Due <= now) (due ??= []).Add(kv.Key);
        if (due == null) return;
        foreach (var e in due)
        {
            var (h, _) = Picks[e];
            Picks[e] = (h, now + (long)(h.PickEvery * 1000));
            var o = new CreaturesNative.AttackOverride { Size = (uint)sizeof(CreaturesNative.AttackOverride), Forced = -1, Allow = ~0u };
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = h.Owner.Mod;
            try
            {
                var name = h.PickAttack?.Invoke(new NpcView(new EntityId(e)));
                if (name != null)
                {
                    var id = Guid.TryParse(name, out var g) ? g : RuleIds.Derive(h.Owner.Name, "attack", name);
                    var index = Arsenals.AttackIndex(h.Id, id);
                    if (index >= 0) o.Forced = index;
                }
            }
            catch (Exception x)
            {
                Backend.Current.Log(0, LogLevel.Error, $"Kiln.Creatures: PickAttack of {h.Owner.Name} threw: {x.Message}");
            }
            finally { ModContext.CurrentMod = outer; }
            t->SetAttackOverride(h.Owner.Native, e, &o);
        }
    }
}
