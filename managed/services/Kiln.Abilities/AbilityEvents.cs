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

    public ImpactRef Impact { get; }
    public ulong Step { get; }
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
/// filter every new instance is held. Instances created by Kiln with <see cref="Abilities.RunImpact"/> arrive with
/// their configs already applied.</remarks>
public sealed class ImpactStartingEvent : CancelableEvent
{
    static ImpactStartingEvent() => AbilitiesDispatch.Ensure();
    internal ImpactStartingEvent(ImpactRef impact, ulong step) { Impact = impact; Step = step; }

    public ImpactRef Impact { get; }
    public ulong Step { get; }
    public ImpactInstance Instance => _instance ??= new ImpactInstance(Impact.ImpactId);
    private ImpactInstance? _instance;

    public string? TemplateName => Spawn.Inspect(Impact.Self)?.TemplateName;
    public bool RootIsPlayer => Impact.Root.IsPlayer;

    public float? GetFloat(string key) => Instance.GetFloat(key);
    public bool SetFloat(string key, float value) => Instance.SetFloat(key, value);

    /// <summary>Multiplies a float variable (e.g. "AttackMod", "NoiseRange"). False when the program has none.</summary>
    public bool Scale(string key, float factor) => Instance.GetFloat(key) is { } v && Instance.SetFloat(key, v * factor);

    public bool Set(string key, object value) => Instance.Set(key, value);
}

/// <summary>An instance reported by <see cref="ImpactStartedEvent"/> or <see cref="ImpactStartingEvent"/> no longer
/// exists (its program finished or its entity was destroyed). Observe only.</summary>
public sealed class ImpactEndedEvent : KilnEvent
{
    static ImpactEndedEvent() => AbilitiesDispatch.Ensure();
    internal ImpactEndedEvent(ImpactRef impact, ulong step) { Impact = impact; Step = step; }
    public ImpactRef Impact { get; }
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

    public uint Native => Snapshot.Hash;
    public string NativeName => Abilities.Natives.NameOf(Snapshot.Hash);
    public ImpactRef Impact => Snapshot.Impact;
    public uint Site => Snapshot.Site;
    public ulong Step => Snapshot.Step;
    /// <summary>The value the native pushed (applyDamage: 1 = the hit was not blocked).</summary>
    public byte Result => Snapshot.Result;
    public byte Error => Snapshot.Error;
    public int ArgCount => Snapshot.Args.Length;
    /// <summary>The raw argument (a handle 0xFFFF0000|index, 0 = not connected, or a raw value).</summary>
    public uint RawArg(int i) => Snapshot.Args[i];
    /// <summary>The first 4 bytes of argument i's variable after the call (or the raw value).</summary>
    public uint ArgU32(int i) => Snapshot.Values[i];
    public float ArgFloat(int i) => BitConverter.UInt32BitsToSingle(Snapshot.Values[i]);
    public EntityId ArgEntity(int i) => IsConnected(i) ? new EntityId(Snapshot.Values[i]) : EntityId.None;
    public bool IsConnected(int i) => (Snapshot.Args[i] & 0xFFFF0000) == 0xFFFF0000;
    /// <summary>The bytes of argument i's variable when it was larger than 4 bytes (else empty).</summary>
    public ReadOnlySpan<byte> ArgBytes(int i) => Snapshot.Bytes(i);
}

/// <summary>applyDamage ran (args: 0 wasBlocked, 1 attacker/root, 2 target, 3 position, 6 AttackMod, 12 push-back,
/// 13 hit reaction, 14 damage setup).</summary>
public sealed class ImpactDamageEvent : ImpactNativeEvent
{
    internal ImpactDamageEvent(in NativeSnapshot s) : base(s) { }
    public EntityId Attacker => ArgEntity(1);
    public EntityId Target => ArgEntity(2);
    public float AttackMod => IsConnected(6) ? ArgFloat(6) : 1f;
    public DamageSetup Damage => DamageSetup.From(ArgBytes(14));
    public WorldPosition Position => ArgBytes(3) is { Length: 12 } p
        ? new WorldPosition(BitConverter.ToSingle(p), BitConverter.ToSingle(p[4..]), BitConverter.ToSingle(p[8..]))
        : default;
    public bool Blocked => IsConnected(0) && ArgU32(0) != 0;
}

/// <summary>applyBuff ran (args: 0 buff type, 1 target, 2 damage setup, 4 duration).</summary>
public sealed class ImpactBuffEvent : ImpactNativeEvent
{
    internal ImpactBuffEvent(in NativeSnapshot s) : base(s) { }
    public Guid Buff => ArgBytes(0) is { Length: 16 } b ? new Guid(b) : Guid.Empty;
    public EntityId Target => ArgEntity(1);
    public TimeSpan Duration => ArgBytes(4) is { Length: 8 } b ? TimeSpan.FromTicks(BitConverter.ToInt64(b) / 100) : TimeSpan.Zero;
}

/// <summary>spawnEntity ran (args: 0 the spawned entity (when connected), 5 template, 6 transform).</summary>
public sealed class ImpactSpawnEvent : ImpactNativeEvent
{
    internal ImpactSpawnEvent(in NativeSnapshot s) : base(s) { }
    public Guid Template => ArgBytes(5) is { Length: 16 } b ? new Guid(b) : Guid.Empty;
    public string? TemplateName => Template == Guid.Empty ? null : Spawn.Templates.FirstOrDefault(t => t.Id == Template)?.Name;
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
    public ImpactRef Impact { get; }
    public uint Tag { get; }
    public uint Site { get; }
    /// <summary>The value variable's type hash (0 = a raw u32).</summary>
    public uint ValueTypeHash { get; }
    public byte[] Value { get; }
    public ulong Step { get; }
    public int AsInt => Value.Length >= 4 ? BitConverter.ToInt32(Value) : Value.Length == 1 ? Value[0] : 0;
    public float AsFloat => Value.Length >= 4 ? BitConverter.ToSingle(Value) : 0;
    public EntityId AsEntity => new(Value.Length >= 4 ? BitConverter.ToUInt32(Value) : 0);
}

/// <summary>An actor started an action sequence (attack, spell cast, ability, hit reaction ...): its
/// <c>Actor.sequenceRuntimeId</c> changed. Polled every step for players and every actor Kiln knows (refreshed every
/// 2 s), only while subscribed. Observe only.</summary>
public sealed class SequenceStartedEvent : KilnEvent
{
    static SequenceStartedEvent() { AbilitiesDispatch.Ensure(); SequenceWatch.Hook(); }
    internal SequenceStartedEvent(EntityId actor, uint seq, long time, int call) { Actor = actor; Sequence = seq; TriggerTime = time; CallIndex = call; }
    public EntityId Actor { get; }
    /// <summary>FNV-1a of the ActorSequenceResource GUID (<see cref="Abilities.IdOf"/>).</summary>
    public uint Sequence { get; }
    public long TriggerTime { get; }
    public int CallIndex { get; }
}

/// <summary>An actor's sequence ended (replaced by none or by another sequence). Observe only.</summary>
public sealed class SequenceEndedEvent : KilnEvent
{
    static SequenceEndedEvent() { AbilitiesDispatch.Ensure(); SequenceWatch.Hook(); }
    internal SequenceEndedEvent(EntityId actor, uint seq, TimeSpan duration) { Actor = actor; Sequence = seq; Duration = duration; }
    public EntityId Actor { get; }
    public uint Sequence { get; }
    /// <summary>Wall time it was seen running.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>A scripted ability is about to be cast (all checks passed, nothing paid yet). Handlers may change
/// <see cref="Cost"/> or cancel (the caster gets <see cref="Reason"/>).</summary>
public sealed class AbilityCastEvent : CancelableEvent
{
    internal AbilityCastEvent(AbilityCast cast, AbilityCost cost) { Cast = cast; Cost = cost; }
    public AbilityCast Cast { get; }
    public AbilityCost Cost { get; set; }
    public string Reason { get; set; } = "canceled";
}

/// <summary>A scripted ability was cast (cost paid, cooldown started; its OnCast is running). Observe only.</summary>
public sealed class AbilityCastedEvent : KilnEvent
{
    internal AbilityCastedEvent(AbilityCast cast, AbilityCost paid) { Cast = cast; Paid = paid; }
    public AbilityCast Cast { get; }
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
