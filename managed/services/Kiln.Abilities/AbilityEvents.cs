// SPDX-License-Identifier: MIT
// kiln.abilities on the event bus. All events are posted on the tick thread in Pre, with what happened in the
// previous step, and are only built while someone subscribes. docs/kiln/services/abilities.md lists them.
namespace Kiln;

/// <summary>An Impact instance was created and the game copied its config values in (observe only; one step later).
/// <see cref="Instance"/> reads its variables while it lives.</summary>
public sealed class ImpactStartedEvent : KilnEvent
{
    static ImpactStartedEvent() => AbilitiesDispatch.Ensure();
    internal ImpactStartedEvent(ImpactRef impact, ulong step) { Impact = impact; Step = step; }

    /// <summary>The instance that was created.</summary>
    public ImpactRef Impact { get; }
    /// <summary>The step the instance was created in.</summary>
    public ulong Step { get; }
    /// <summary>The live instance, to read or write its variables while it exists.</summary>
    public ImpactInstance Instance => new(Impact.ImpactId);

    /// <summary>The entity's template name (looked up now; null when the entity is gone).</summary>
    public string? TemplateName => Spawn.Inspect(Impact.Self)?.TemplateName;
}

/// <summary>
/// Before an instance's first run (Forge-style "per cast" tweak): the instance exists with the game's config values
/// and has not run yet; handlers change its variables (<see cref="Instance"/>, <see cref="SetFloat"/>, ...) and the
/// program runs with them. <see cref="CancelableEvent.Cancel"/> destroys the entity before its first run.
/// </summary>
/// <remarks>While anyone subscribes, new instances are held for one step so the handlers can run on the tick thread
/// (about 41 ms on a dedicated server). <see cref="Abilities.HoldPrograms"/> narrows that to some programs; with no
/// filter every new instance is held. Instances created by Kiln with <see cref="Abilities.RunImpact(string, WorldPosition, ImpactOptions)"/> arrive with
/// their configs already applied.</remarks>
public sealed class ImpactStartingEvent : CancelableEvent
{
    static ImpactStartingEvent() => AbilitiesDispatch.Ensure();
    internal ImpactStartingEvent(ImpactRef impact, ulong step) { Impact = impact; Step = step; }

    /// <summary>The instance that is about to run for the first time.</summary>
    public ImpactRef Impact { get; }
    /// <summary>The step the instance was created in.</summary>
    public ulong Step { get; }
    /// <summary>The instance, to read or write its variables before its first run.</summary>
    public ImpactInstance Instance => _instance ??= new ImpactInstance(Impact.ImpactId);
    private ImpactInstance? _instance;

    /// <summary>The entity's template name (looked up now); null when the entity is gone.</summary>
    public string? TemplateName => Spawn.Inspect(Impact.Self)?.TemplateName;
    /// <summary>True when the instance's root (the caster) is a player.</summary>
    public bool RootIsPlayer => Impact.Root.IsPlayer;

    /// <summary>A 4-byte variable read as a float (<paramref name="key"/> as for
    /// <see cref="ImpactInstance.Find(string)"/>); null when the program has no such variable.</summary>
    public float? GetFloat(string key) => Instance.GetFloat(key);
    /// <summary>Writes a float variable. False when the program has no such variable or the write was
    /// refused.</summary>
    public bool SetFloat(string key, float value) => Instance.SetFloat(key, value);

    /// <summary>Multiplies a float variable (e.g. "AttackMod", "NoiseRange"). False when the program has none.</summary>
    public bool Scale(string key, float factor) => Instance.GetFloat(key) is { } v && Instance.SetFloat(key, v * factor);

    /// <summary>Sets a variable from a value (float, int, bool, <see cref="TimeSpan"/>, <see cref="Guid"/>,
    /// <see cref="DamageSetup"/> or byte[]), converted to the variable's type. False when the program has no such
    /// variable or the value does not fit.</summary>
    public bool Set(string key, object value) => Instance.Set(key, value);
}

/// <summary>An instance reported by <see cref="ImpactStartedEvent"/> or <see cref="ImpactStartingEvent"/> no longer
/// exists (its program finished or its entity was destroyed). Observe only.</summary>
public sealed class ImpactEndedEvent : KilnEvent
{
    static ImpactEndedEvent() => AbilitiesDispatch.Ensure();
    internal ImpactEndedEvent(ImpactRef impact, ulong step) { Impact = impact; Step = step; }
    /// <summary>The instance that no longer exists.</summary>
    public ImpactRef Impact { get; }
    /// <summary>The step in which Kiln saw that it was gone.</summary>
    public ulong Step { get; }
}

/// <summary>
/// A mod changed a variable of a live Impact instance (<see cref="ImpactInstance.Write"/>, <see cref="ImpactInstance.Set"/>
/// and the typed setters). Posted on the tick thread right after the write took, and only when the bytes changed; a write
/// made while this event is being dispatched (a handler that writes again) is posted at the start of the next step instead,
/// once per instance and variable (the newest wins), and only if that instance is still the one written (an ended
/// instance's id can name another). Observe only. Built only while someone subscribes. Since 0.2.0-dev.27.
/// </summary>
[ServerOnly]
public sealed class ImpactVariableChangedEvent : KilnEvent, IEntityEvent
{
    internal ImpactVariableChangedEvent(uint impactId, uint programId, EntityId self, string variable, string origin)
    {
        ImpactId = impactId;
        ProgramId = programId;
        Self = self;
        Variable = variable;
        Origin = origin;
    }

    /// <summary>The instance's id (<see cref="ImpactInstance.ImpactId"/>).</summary>
    public uint ImpactId { get; }

    /// <summary>Its program.</summary>
    public uint ProgramId { get; }

    /// <summary>Its self entity.</summary>
    public EntityId Self { get; }

    /// <summary>The variable's name.</summary>
    public string Variable { get; }

    /// <summary>The id of the mod that made the change ("" when Kiln made it for no mod).</summary>
    public string Origin { get; }

    /// <summary>The instance, to read the value now.</summary>
    public ImpactInstance Instance => new(ImpactId);

    /// <summary>Whether the event is about <paramref name="entity"/> (the instance's self).</summary>
    public bool Involves(EntityId entity) => !entity.IsNone && entity == Self;

    /// <summary>The change and who made it, for a log line.</summary>
    public override string ToString() =>
        $"impact {ImpactId} (program 0x{ProgramId:x8}, self {Self}): {Variable} by {(Origin.Length > 0 ? Origin : "kiln")}";
}

/// <summary>A vanilla native ran inside a program (observe only; posted in the next Pre). Kiln observes applyDamage,
/// applyBuff and spawnEntity (the typed subclasses) and any native passed to <see cref="Abilities.Natives.Observe"/>.
/// To change or cancel a call use <see cref="Abilities.Natives.Wrap"/> (job thread) or <see cref="Abilities.OnDamage"/>.
/// </summary>
public class ImpactNativeEvent : KilnEvent
{
    static ImpactNativeEvent() => AbilitiesDispatch.Ensure();
    internal ImpactNativeEvent(in NativeSnapshot s) => Snapshot = s;
    internal readonly NativeSnapshot Snapshot;

    /// <summary>The native's hash: FNV-1a-32 of its name. <see cref="NativeName"/> gives the name.</summary>
    public uint Native => Snapshot.Hash;
    /// <summary>The native's name; its hash as <c>0x&lt;hash&gt;</c> when it is unknown.</summary>
    public string NativeName => Abilities.Natives.NameOf(Snapshot.Hash);
    /// <summary>The instance whose program called the native.</summary>
    public ImpactRef Impact => Snapshot.Impact;
    /// <summary>The call-site id of the call in the program.</summary>
    public uint Site => Snapshot.Site;
    /// <summary>The step the call ran in.</summary>
    public ulong Step => Snapshot.Step;
    /// <summary>The value the native pushed (applyDamage: 1 = the hit was not blocked).</summary>
    public byte Result => Snapshot.Result;
    /// <summary>The native's error byte: 0 = ok, 1 to 3 = the call suspended.</summary>
    public byte Error => Snapshot.Error;
    /// <summary>The number of arguments of the call.</summary>
    public int ArgCount => Snapshot.Args.Length;
    /// <summary>The raw argument (a handle 0xFFFF0000|index, 0 = not connected, or a raw value).</summary>
    public uint RawArg(int i) => Snapshot.Args[i];
    /// <summary>The first 4 bytes of argument i's variable after the call (or the raw value).</summary>
    public uint ArgU32(int i) => Snapshot.Values[i];
    /// <summary><see cref="ArgU32(int)"/> read as a float.</summary>
    public float ArgFloat(int i) => BitConverter.UInt32BitsToSingle(Snapshot.Values[i]);
    /// <summary>Argument <paramref name="i"/> as an entity; none when it is not connected.</summary>
    public EntityId ArgEntity(int i) => IsConnected(i) ? new EntityId(Snapshot.Values[i]) : EntityId.None;
    /// <summary>True when argument <paramref name="i"/> is a variable handle (0xFFFF0000|index), not 0 or a raw
    /// value.</summary>
    public bool IsConnected(int i) => (Snapshot.Args[i] & 0xFFFF0000) == 0xFFFF0000;
    /// <summary>The bytes of argument i's variable when it was larger than 4 bytes (else empty).</summary>
    public ReadOnlySpan<byte> ArgBytes(int i) => Snapshot.Bytes(i);
}

/// <summary>applyDamage ran (args: 0 wasBlocked, 1 attacker/root, 2 target, 3 position, 6 AttackMod, 12 push-back,
/// 13 hit reaction, 14 damage setup).</summary>
public sealed class ImpactDamageEvent : ImpactNativeEvent
{
    internal ImpactDamageEvent(in NativeSnapshot s) : base(s) { }
    /// <summary>The attacker or root (argument 1).</summary>
    public EntityId Attacker => ArgEntity(1);
    /// <summary>The target (argument 2).</summary>
    public EntityId Target => ArgEntity(2);
    /// <summary>The attack multiplier (argument 6); 1 when the call site passes none.</summary>
    public float AttackMod => IsConnected(6) ? ArgFloat(6) : 1f;
    /// <summary>The damage setup (argument 14); default when it is not connected.</summary>
    public DamageSetup Damage => DamageSetup.From(ArgBytes(14));
    /// <summary>The position of the hit (argument 3); default when the argument holds none.</summary>
    public WorldPosition Position => ArgBytes(3) is { Length: 12 } p
        ? new WorldPosition(BitConverter.ToSingle(p), BitConverter.ToSingle(p[4..]), BitConverter.ToSingle(p[8..]))
        : default;
    /// <summary>True when the hit was blocked (argument 0, wasBlocked).</summary>
    public bool Blocked => IsConnected(0) && ArgU32(0) != 0;
}

/// <summary>applyBuff ran (args: 0 buff type, 1 target, 2 damage setup, 4 duration).</summary>
public sealed class ImpactBuffEvent : ImpactNativeEvent
{
    internal ImpactBuffEvent(in NativeSnapshot s) : base(s) { }
    /// <summary>The buff type's GUID (argument 0); empty when it is not connected.</summary>
    public Guid Buff => ArgBytes(0) is { Length: 16 } b ? new Guid(b) : Guid.Empty;
    /// <summary>The entity that gets the buff (argument 1).</summary>
    public EntityId Target => ArgEntity(1);
    /// <summary>The buff's duration (argument 4); zero when it is not connected.</summary>
    public TimeSpan Duration => ArgBytes(4) is { Length: 8 } b ? TimeSpan.FromTicks(BitConverter.ToInt64(b) / 100) : TimeSpan.Zero;
}

/// <summary>spawnEntity ran (args: 0 the spawned entity (when connected), 5 template, 6 transform).</summary>
public sealed class ImpactSpawnEvent : ImpactNativeEvent
{
    internal ImpactSpawnEvent(in NativeSnapshot s) : base(s) { }
    /// <summary>The template's GUID (argument 5); empty when it is not connected.</summary>
    public Guid Template => ArgBytes(5) is { Length: 16 } b ? new Guid(b) : Guid.Empty;
    /// <summary>The template's name; null when there is no template or Kiln does not know it.</summary>
    public string? TemplateName => Template == Guid.Empty ? null : Spawn.Templates.FirstOrDefault(t => t.Id == Template)?.Name;
    /// <summary>The spawned entity (argument 0); none when it is not connected.</summary>
    public EntityId Spawned => ArgEntity(0);
    /// <summary>Position of the transform argument (32.32 fixed point in the engine).</summary>
    public WorldPosition Position => ArgBytes(6) is { Length: >= 24 } b
        ? new WorldPosition(BitConverter.ToInt64(b) / 4294967296.0, BitConverter.ToInt64(b[8..]) / 4294967296.0,
            BitConverter.ToInt64(b[16..]) / 4294967296.0)
        : default;
}

/// <summary>A program called kiln.Signal(value, tag): Kiln's bridge from Impact programs to mods (fire and forget;
/// posted in the next Pre).</summary>
public sealed class ImpactSignalEvent : KilnEvent
{
    static ImpactSignalEvent() => AbilitiesDispatch.Ensure();
    internal ImpactSignalEvent(ImpactRef impact, uint tag, uint site, uint type, byte[] value, ulong step)
    {
        Impact = impact; Tag = tag; Site = site; ValueTypeHash = type; Value = value; Step = step;
    }
    /// <summary>The instance whose program signalled.</summary>
    public ImpactRef Impact { get; }
    /// <summary>The tag the program passed (the second argument of <c>kiln.Signal</c>).</summary>
    public uint Tag { get; }
    /// <summary>The call-site id of the signal in the program.</summary>
    public uint Site { get; }
    /// <summary>The value variable's type hash (0 = a raw u32).</summary>
    public uint ValueTypeHash { get; }
    /// <summary>The bytes of the value argument: a variable's bytes, or the raw 32-bit value.</summary>
    public byte[] Value { get; }
    /// <summary>The step the program signalled in.</summary>
    public ulong Step { get; }
    /// <summary><see cref="Value"/> as an int: its first 4 bytes, or a 1-byte value as that byte; 0
    /// otherwise.</summary>
    public int AsInt => Value.Length >= 4 ? BitConverter.ToInt32(Value) : Value.Length == 1 ? Value[0] : 0;
    /// <summary><see cref="Value"/> as a float (its first 4 bytes); 0 when it is shorter.</summary>
    public float AsFloat => Value.Length >= 4 ? BitConverter.ToSingle(Value) : 0;
    /// <summary><see cref="Value"/> as an entity id (its first 4 bytes).</summary>
    public EntityId AsEntity => new(Value.Length >= 4 ? BitConverter.ToUInt32(Value) : 0);
}

/// <summary>An actor started an action sequence (attack, spell cast, ability, hit reaction ...): its
/// <c>Actor.sequenceRuntimeId</c> changed. Polled every step for players and every actor Kiln knows (refreshed every
/// 2 s), only while subscribed. Observe only.</summary>
public sealed class SequenceStartedEvent : KilnEvent
{
    static SequenceStartedEvent() { AbilitiesDispatch.Ensure(); SequenceWatch.Hook(); }
    internal SequenceStartedEvent(EntityId actor, uint seq, long time, int call) { Actor = actor; Sequence = seq; TriggerTime = time; CallIndex = call; }
    /// <summary>The actor that started the sequence.</summary>
    public EntityId Actor { get; }
    /// <summary>FNV-1a of the ActorSequenceResource GUID (<see cref="Abilities.IdOf"/>).</summary>
    public uint Sequence { get; }
    /// <summary>The sequence's trigger time, in ECS nanoseconds
    /// (<see cref="Abilities.SequenceOf(EntityId)"/>).</summary>
    public long TriggerTime { get; }
    /// <summary>The sequence's call index (<see cref="Abilities.SequenceOf(EntityId)"/>).</summary>
    public int CallIndex { get; }
}

/// <summary>An actor's sequence ended (replaced by none or by another sequence). Observe only.</summary>
public sealed class SequenceEndedEvent : KilnEvent
{
    static SequenceEndedEvent() { AbilitiesDispatch.Ensure(); SequenceWatch.Hook(); }
    internal SequenceEndedEvent(EntityId actor, uint seq, TimeSpan duration) { Actor = actor; Sequence = seq; Duration = duration; }
    /// <summary>The actor whose sequence ended.</summary>
    public EntityId Actor { get; }
    /// <summary>The sequence that ended: FNV-1a of the ActorSequenceResource GUID
    /// (<see cref="Abilities.IdOf"/>).</summary>
    public uint Sequence { get; }
    /// <summary>Wall time it was seen running.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>A scripted ability is about to be cast (all checks passed, nothing paid yet). Handlers may change
/// <see cref="Cost"/> or cancel (the caster gets <see cref="Reason"/>).</summary>
public sealed class AbilityCastEvent : CancelableEvent
{
    internal AbilityCastEvent(AbilityCast cast, AbilityCost cost) { Cast = cast; Cost = cost; }
    /// <summary>The cast that is about to start.</summary>
    public AbilityCast Cast { get; }
    /// <summary>What the cast will cost: the ability's cost at first. A handler may change it.</summary>
    public AbilityCost Cost { get; set; }
    /// <summary>The refusal text the caster gets when a handler cancels the event. Default <c>"canceled"</c>.</summary>
    public string Reason { get; set; } = "canceled";
}

/// <summary>A scripted ability was cast (cost paid, cooldown started; its OnCast is running). Observe only.</summary>
public sealed class AbilityCastedEvent : KilnEvent
{
    internal AbilityCastedEvent(AbilityCast cast, AbilityCost paid) { Cast = cast; Paid = paid; }
    /// <summary>The cast.</summary>
    public AbilityCast Cast { get; }
    /// <summary>What the cast cost: the cost as <see cref="AbilityCastEvent"/> left it.</summary>
    public AbilityCost Paid { get; }
}

/// <summary>A buff was taken off an entity through <see cref="Abilities.RemoveBuff(EntityId, Guid)"/>: the game's
/// removeBuff said it removed it. Posted on the tick thread when the removal's result is known, before the caller's task
/// completes, only while the event has subscribers. Not posted for buffs that run out or that the game's own programs
/// remove. Observe only (it already happened).</summary>
[ServerOnly]
public sealed class BuffRemovedEvent : KilnEvent
{
    internal BuffRemovedEvent(EntityId target, uint buff, string? mod)
    {
        Target = target;
        Buff = buff;
        Mod = mod;
    }

    /// <summary>The entity the buff was on.</summary>
    public EntityId Target { get; }

    /// <summary>The buff's id (FNV-1a of its GUID).</summary>
    public uint Buff { get; }

    /// <summary>The id of the mod that removed it; null when Kiln did, outside any mod.</summary>
    public string? Mod { get; }
}
