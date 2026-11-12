// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>An entity's health (engine health points).</summary>
/// <param name="Current">Health points now.</param>
/// <param name="Max">The computed maximum.</param>
/// <param name="Dead">The game's dead state (Actor state 7, read through the game's own state test) as its death check
/// leaves it at the next step: that check marks an actor dead while Health is below 1 and alive again once Health is
/// 1 or more. So a heal or <see cref="Combat.SetHealth"/> that revives a dead entity reads back alive at once, and
/// <c>SetHealth(e, 0)</c> reads back dead (dying), although the game flips its mark one step later. False without an
/// Actor.</param>
/// <param name="HasActor">The entity has an Actor (creatures, players, NPCs).</param>
/// <param name="Invulnerable">Kiln god mode is on for it.</param>
public readonly record struct Health(int Current, int Max, bool Dead, bool HasActor, bool Invulnerable)
{
    public double Fraction => Max > 0 ? (double)Current / Max : 0;
    public override string ToString() => $"{Current}/{Max}{(Dead ? " dead" : "")}{(Invulnerable ? " god" : "")}";
}

/// <summary>Options for <see cref="Combat.Damage"/>.</summary>
public sealed record DamageOptions
{
    public static readonly DamageOptions Default = new();

    /// <summary>Stagger of the target (none by default).</summary>
    public HitReaction Reaction { get; init; } = HitReaction.None;
    /// <summary>Push-back force 0..255.</summary>
    public byte PushBack { get; init; }
    /// <summary>Threat for enemy AI (the game's own hits use about 1).</summary>
    public float Aggro { get; init; } = 1f;
    /// <summary>The hit interrupts the target's current action.</summary>
    public bool ForceInterrupt { get; init; }
    /// <summary>No HitEvent: health changes, but no reaction, no damage number for clients, no kill credit for this hit
    /// (a player who hit the target before keeps the credit; see <see cref="NoCredit"/>).</summary>
    public bool Silent { get; init; }
    /// <summary>Clears the target's credit before the hit. The game gives a kill's XP (and kill credit) to the last player
    /// who hit the target, whoever lands the killing blow; with no attacker and this option, a kill credits nobody.</summary>
    public bool NoCredit { get; init; }
    /// <summary>The direct source entity (weapon, projectile); default: the attacker.</summary>
    public EntityId Source { get; init; }
    /// <summary>Treat the hit like one the engine produced: no synchronous <see cref="EntityHurtEvent"/>; it is held and
    /// reported in the next Pre like game hits (when anything subscribes to EntityHurtEvent).</summary>
    public bool AsGameHit { get; init; }
}

public enum HitState
{
    /// <summary>Waiting for the engine's next hit conclusion (or held for EntityHurtEvent handlers).</summary>
    Pending = 0,
    /// <summary>The engine applied it to the target.</summary>
    Applied = 2,
    /// <summary>Never applied: canceled by a handler, the target is invulnerable or gone, the engine skipped it.</summary>
    Dropped = 3,
}

/// <summary>Outcome of one <see cref="Combat.Damage"/> / <see cref="Combat.Kill"/> hit.</summary>
public sealed record HitResult(HitState State, EntityId Target, int Amount, int HealthBefore, int HealthAfter,
    bool KillingBlow, int Steps)
{
    public bool Applied => State == HitState.Applied;
    public override string ToString() => Applied
        ? $"hit {Target}: {HealthBefore} -> {HealthAfter}{(KillingBlow ? " (killing blow)" : "")} after {Steps} step(s)"
        : $"hit {Target} {State}";
}

/// <summary>Counters of kiln.combat (diagnostics).</summary>
public sealed record CombatStatus(bool Health, bool Hits, bool Deaths, bool Holding, uint Interest, ulong HitsSeen,
    ulong HitsHeld, ulong HitsReleased, ulong HitsCanceled, ulong HitsModified, ulong KilnHits, ulong GodBlocked,
    ulong GodRestored, ulong DeathsSeen, ulong EventsDelivered, uint Invulnerable, ulong GameDiedEvents,
    ulong HitsDeferred, ulong DropsSeen, ulong DropsCanceled, ulong RepeatHits)
{
    /// <summary>Fall damage is intercepted on this build (<see cref="EntityFallEvent"/>).</summary>
    public bool Falls { get; init; }
    /// <summary>Natural regeneration reaches <see cref="EntityHealEvent"/>.</summary>
    public bool Regeneration { get; init; }
    /// <summary>changeAttributeValue health gains reach <see cref="EntityHealEvent"/>.</summary>
    public bool AttributeHeals { get; init; }
    public ulong FallsSeen { get; init; }
    public ulong FallsCanceled { get; init; }
    public ulong FallsModified { get; init; }
    public ulong RegenSeen { get; init; }
    public ulong RegenCanceled { get; init; }
    public ulong AttributeHealsSeen { get; init; }
    public ulong AttributeHealsCorrected { get; init; }
    public ulong KnockbacksSeen { get; init; }
    public ulong KnockbacksModified { get; init; }
}

/// <summary>
/// Health, damage, healing, kills and god mode on the server world, through the engine's own paths: health writes go
/// through the attribute system (the game copies Health into the replicated NetworkHealth every step) and damage is a
/// real engine hit (health change, killing blow, hit reaction, damage numbers and kill credit for the attacker).
/// Tick thread only. Events: <see cref="EntityHurtEvent"/>, <see cref="EntityHealEvent"/>,
/// <see cref="EntityDamagedEvent"/>, <see cref="EntityDeathEvent"/>, <see cref="PlayerRespawnEvent"/>,
/// <see cref="EntityDropsEvent"/>.
/// </summary>
public static unsafe class Combat
{
    private sealed class Pending
    {
        public required TaskCompletionSource<HitResult> Source;
        public required ModContext? Mod;
        public required EntityId Target;
        public required int Amount;
        public ulong Id;
    }

    private static readonly object Gate = new();
    private static readonly List<Pending> PendingHits = new();
    private static readonly HashSet<ModContext> WatchedMods = new(ReferenceEqualityComparer.Instance);
    private static int _listening;

    static Combat() => EnsureListener();

    /// <summary>True when health access works on this build and a server world runs.</summary>
    public static bool Available => CombatNative.T != null && Status is { Health: true };

    /// <summary>True when Damage/Kill (the engine hit path) work.</summary>
    public static bool HitsAvailable => CombatNative.T != null && Status is { Hits: true };

    public static CombatStatus? Status
    {
        get
        {
            var t = CombatNative.T;
            if (t == null) return null;
            CombatNative.Status s = default;
            s.Size = (uint)sizeof(CombatNative.Status);  // an older native fills only what it knows (the rest stays 0)
            if (t->GetStatus(&s) != CombatNative.Ok) return null;
            return new CombatStatus(s.Health != 0, s.Hits != 0, s.Deaths != 0, s.Holding != 0, s.Interest, s.HitsSeen,
                s.HitsHeld, s.HitsReleased, s.HitsCanceled, s.HitsModified, s.KilnHits, s.GodBlocked, s.GodRestored,
                s.DeathsSeen, s.EventsDelivered, s.Invulnerable, s.GameDiedEvents, s.HitsDeferred, s.DropsSeen,
                s.DropsCanceled, s.RepeatHits)
            {
                Falls = s.Falls != 0, Regeneration = s.Regen != 0, AttributeHeals = s.AttrHeals != 0, FallsSeen = s.FallsSeen,
                FallsCanceled = s.FallsCanceled, FallsModified = s.FallsModified, RegenSeen = s.RegenSeen,
                RegenCanceled = s.RegenCanceled, AttributeHealsSeen = s.AttrHealsSeen,
                AttributeHealsCorrected = s.AttrHealsCorrected, KnockbacksSeen = s.KnockbacksSeen,
                KnockbacksModified = s.KnockbacksModified,
            };
        }
    }

    // ------------------------------------------------------------------------------------------------ health ------

    /// <summary>The entity's health, or null when it has none (props without Health, unknown ids).</summary>
    public static Health? GetHealth(EntityId entity) => TryGetHealth(entity, out var h) ? h : null;

    public static bool TryGetHealth(EntityId entity, out Health health)
    {
        var t = CombatNative.Require("Combat.GetHealth");
        Services.RequireTickThread("Combat.GetHealth");
        CombatNative.Health h = default;
        h.Size = (uint)sizeof(CombatNative.Health);
        if (t->GetHealth(entity.Value, &h) != CombatNative.Ok)
        {
            health = default;
            return false;
        }
        health = new Health(h.Current, h.Max, h.Dead != 0, h.HasActor != 0, h.Invulnerable != 0);
        return true;
    }

    /// <summary>Sets current health (clamped to 0..max) silently. 0 kills on the next step without a killer. Returns
    /// the new health.</summary>
    public static int SetHealth(EntityId entity, int value)
    {
        var t = CombatNative.Require("Combat.SetHealth");
        Services.RequireTickThread("Combat.SetHealth");
        int applied = 0;
        var rc = t->SetHealth(entity.Value, value, &applied);
        if (rc != CombatNative.Ok) throw new InvalidOperationException($"Combat.SetHealth({entity}): {CombatNative.Describe(rc)}");
        return applied;
    }

    /// <summary>Heals up to max, silently (no HitEvent). Fires <see cref="EntityHealEvent"/> first (cancelable,
    /// amount writable). Returns the health actually added (0 when canceled or already full).</summary>
    public static int Heal(EntityId entity, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var t = CombatNative.Require("Combat.Heal");
        Services.RequireTickThread("Combat.Heal");
        if (Events.HasSubscribers<EntityHealEvent>() && TryGetHealth(entity, out var h))
        {
            var ev = Events.Post(new EntityHealEvent(entity, amount, h.Current, h.Max, IsPlayerId(entity)));
            if (ev.Canceled) return 0;
            amount = ev.Amount;
        }
        int healed = 0;
        var rc = t->Heal(entity.Value, amount, &healed);
        if (rc != CombatNative.Ok) throw new InvalidOperationException($"Combat.Heal({entity}): {CombatNative.Describe(rc)}");
        return healed;
    }

    // ------------------------------------------------------------------------------------------------ hits --------

    /// <summary>Deals <paramref name="amount"/> damage to <paramref name="target"/> as an engine hit from
    /// <paramref name="attacker"/> (kill credit and XP go to the attacker when it is a player). Fires
    /// <see cref="EntityHurtEvent"/> first (unless <see cref="DamageOptions.AsGameHit"/>); a canceled event gives a
    /// Dropped result. The hit applies in the engine's next hit conclusion (this step when called in Pre); the task
    /// completes on the tick thread at the next Pre after that.</summary>
    public static Task<HitResult> Damage(EntityId target, int amount, EntityId attacker = default, DamageOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        return Hit(target, amount, attacker, options ?? DamageOptions.Default, kill: false);
    }

    /// <summary>Kills <paramref name="target"/> with a hit of exactly its current health (a killing blow; kill credit
    /// for <paramref name="attacker"/>). Fires <see cref="EntityHurtEvent"/> (Lethal) first.</summary>
    public static Task<HitResult> Kill(EntityId target, EntityId attacker = default, DamageOptions? options = null) =>
        Hit(target, 0, attacker, options ?? DamageOptions.Default, kill: true);

    private static Task<HitResult> Hit(EntityId target, int amount, EntityId attacker, DamageOptions o, bool kill)
    {
        var t = CombatNative.Require(kill ? "Combat.Kill" : "Combat.Damage");
        Services.RequireTickThread(kill ? "Combat.Kill" : "Combat.Damage");
        EnsureListener();
        if (!TryGetHealth(target, out var h))
            return Task.FromResult(new HitResult(HitState.Dropped, target, amount, -1, -1, false, 0));
        if (kill) amount = Math.Max(1, h.Current);
        if (!o.AsGameHit && Events.HasSubscribers<EntityHurtEvent>())
        {
            var ev = Events.Post(new EntityHurtEvent(target, attacker, amount, h.Current, h.Max, IsPlayerId(target)));
            if (ev.Canceled) return Task.FromResult(new HitResult(HitState.Dropped, target, amount, h.Current, h.Current, false, 0));
            if (ev.Amount != amount) kill = false;  // a handler changed a kill into plain damage
            amount = ev.Amount;
            if (amount == 0) return Task.FromResult(new HitResult(HitState.Dropped, target, 0, h.Current, h.Current, false, 0));
        }
        if (!o.AsGameHit && (o.PushBack != 0 || o.Reaction != HitReaction.None) && Events.HasSubscribers<EntityKnockbackEvent>())
        {
            var kb = Events.Post(new EntityKnockbackEvent(target, attacker, o.PushBack, o.Reaction, IsPlayerId(target)));
            o = kb.Canceled
                ? o with { PushBack = 0, Reaction = HitReaction.None }
                : o with { PushBack = (byte)kb.Strength, Reaction = kb.Reaction };
        }
        CombatNative.DamageRequest req = default;
        req.Size = (uint)sizeof(CombatNative.DamageRequest);
        req.Target = target.Value;
        req.Attacker = attacker.Value;
        req.Source = o.Source.Value;
        req.Amount = amount;
        req.Reaction = (ushort)o.Reaction;
        req.PushBack = o.PushBack;
        req.Aggro = o.Aggro;
        req.Flags = (o.ForceInterrupt ? CombatNative.DamageForceInterrupt : 0) | (o.Silent ? CombatNative.DamageSilent : 0) |
                    (o.AsGameHit ? CombatNative.DamageAsGameHit : 0) | (kill ? CombatNative.DamageKill : 0) |
                    (o.NoCredit ? CombatNative.DamageNoCredit : 0);
        ulong id = 0;
        var rc = t->Damage(Services.CallerOwner, &req, &id);
        if (rc != CombatNative.Ok)
            throw new InvalidOperationException($"Combat.{(kill ? "Kill" : "Damage")}({target}): {CombatNative.Describe(rc)}");
        var mod = ModContext.CurrentMod;
        var p = new Pending
        {
            Source = new TaskCompletionSource<HitResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            Mod = mod,
            Target = target,
            Amount = amount,
            Id = id,
        };
        lock (Gate)
        {
            PendingHits.Add(p);
            Watch(mod);
        }
        return p.Source.Task;
    }

    /// <summary>State of a hit by its native id (diagnostics; <see cref="Damage"/> already reports it).</summary>
    internal static HitResult? Status_(ulong id, EntityId target, int amount)
    {
        var t = CombatNative.T;
        if (t == null) return null;
        CombatNative.HitStatus s = default;
        s.Size = (uint)sizeof(CombatNative.HitStatus);
        if (t->HitStatus(id, &s) != CombatNative.Ok) return new HitResult(HitState.Dropped, target, amount, -1, -1, false, 0);
        var state = s.State switch { 2 => HitState.Applied, 3 => HitState.Dropped, _ => HitState.Pending };
        return new HitResult(state, target, amount, s.HealthBefore, s.HealthAfter, s.KillingBlow != 0, (int)s.Steps);
    }

    // ------------------------------------------------------------------------------------------------ god mode ----

    /// <summary>God mode for one entity (players too): damage hits on it are dropped before they apply (no reaction
    /// either), and health it loses another way (fall damage, drowning) is restored before the game's death check.
    /// Healing still works. Held per mod: it ends when the mod turns it off or unloads (and every mod that turned it
    /// on has let go).</summary>
    public static void SetInvulnerable(EntityId entity, bool on = true)
    {
        var t = CombatNative.Require("Combat.SetInvulnerable");
        Services.RequireTickThread("Combat.SetInvulnerable");
        var rc = t->SetInvulnerable(Services.CallerOwner, entity.Value, on ? (byte)1 : (byte)0);
        if (rc != CombatNative.Ok) throw new InvalidOperationException($"Combat.SetInvulnerable({entity}): {CombatNative.Describe(rc)}");
        lock (Gate) Watch(ModContext.CurrentMod);
    }

    public static bool IsInvulnerable(EntityId entity)
    {
        var t = CombatNative.T;
        return t != null && t->Invulnerable(entity.Value) != 0;
    }

    private static bool IsPlayerId(EntityId e) => e.Value is >= 1 and <= 1023;

    // ------------------------------------------------------------------------------------------------ plumbing ----

    // Runs when the assembly is first used (a mod creating a handler for one of its events, or calling Combat): the
    // native service then calls OnEvents once per step.
    [ModuleInitializer]
    internal static void ModuleInit() => EnsureListener();

    internal static void EnsureListener()
    {
        if (Volatile.Read(ref _listening) != 0) return;
        var t = CombatNative.T;
        if (t == null) return;
        if (Interlocked.Exchange(ref _listening, 1) != 0) return;
        t->SetListener(&OnEvents, null);
        ReportHoldFilterRow();
    }

    /// <summary>combat.hold_filter: kiln.combat holds only the hits a handler's entity filter lets through (a table with
    /// hold_filter, since 0.2.0-dev.27). Without it a filtered handler still gets only its entities' hits, but every hit
    /// of the world waits a step.</summary>
    internal const string HoldFilterCapability = "combat.hold_filter";

    private static void ReportHoldFilterRow()
    {
        try
        {
            if (CombatNative.V3 != null) Capabilities.Report(HoldFilterCapability, CapabilityState.Available);
            else
                Capabilities.Report(HoldFilterCapability, CapabilityState.Unavailable, CapabilityReason.ServiceOlder,
                    "this kiln-combat.dll has no hold filter (update Kiln): filtered handlers still get only their entities' hits, but every hit waits a step");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"Kiln.Combat: {HoldFilterCapability} was not reported: {e.Message}");
        }
    }

    private static void Watch(ModContext? mod)
    {
        if (mod is null || !WatchedMods.Add(mod)) return;
        mod.Retiring += () => Retire(mod);
    }

    // The mod unloads: release its god-mode entries and queued hits, drop the tasks that point into its context.
    private static void Retire(ModContext mod)
    {
        List<Pending> drop;
        lock (Gate)
        {
            WatchedMods.Remove(mod);
            drop = PendingHits.Where(p => ReferenceEquals(p.Mod, mod)).ToList();
            PendingHits.RemoveAll(p => ReferenceEquals(p.Mod, mod));
        }
        var t = CombatNative.T;
        if (t != null) t->ReleaseOwner(mod.Owner);
        foreach (var p in drop) p.Source.TrySetCanceled();
    }

    private static uint Interest()
    {
        uint m = 0;
        if (Events.HasSubscribers<EntityHurtEvent>()) m |= CombatNative.Mask(CombatNative.Hurt);
        if (Events.HasSubscribers<EntityHealEvent>()) m |= CombatNative.Mask(CombatNative.Heal);
        if (Events.HasSubscribers<EntityDamagedEvent>()) m |= CombatNative.Mask(CombatNative.Damaged);
        if (Events.HasSubscribers<EntityDeathEvent>()) m |= CombatNative.Mask(CombatNative.Died);
        if (Events.HasSubscribers<PlayerRespawnEvent>()) m |= CombatNative.Mask(CombatNative.Respawned);
        if (Events.HasSubscribers<EntityDropsEvent>()) m |= CombatNative.Mask(CombatNative.Drops);
        if (Events.HasSubscribers<EntityKnockbackEvent>()) m |= CombatNative.Mask(CombatNative.Knockback);
        if (Events.HasSubscribers<EntityFallEvent>()) m |= CombatNative.Mask(CombatNative.Fall);
        return m;
    }

    // ---- the entity filter of held hits (since 0.2.0-dev.27): kiln.combat holds only the hits a handler's filter lets
    // through (Events.On<T>(handler, EventFilter)); one handler without a filter holds every hit, as before.
    private static int _filterVersion = -1;
    private static uint[] _pushedFilter = [];

    /// <summary>The entities the handlers of the held kinds watch: null = every hit (a handler without a filter, or
    /// nobody with a filter). Pure over the three unions, for the tests.</summary>
    internal static uint[]? HoldFilterOf(params IReadOnlySet<EntityId>?[] unions)
    {
        HashSet<uint>? all = null;
        foreach (var u in unions)
        {
            if (u is null) return null;           // someone takes every event of a held kind
            (all ??= []).UnionWith(u.Select(e => e.Value));
        }
        return all is { Count: > 0 } ? all.Order().ToArray() : null;
    }

    private static void PushHoldFilter()
    {
        var version = Events.Version;
        if (version == _filterVersion) return;
        _filterVersion = version;
        var unions = new List<IReadOnlySet<EntityId>?>();
        if (Events.HasSubscribers<EntityHurtEvent>()) unions.Add(Events.EntityFilterUnion(typeof(EntityHurtEvent)));
        if (Events.HasSubscribers<EntityHealEvent>()) unions.Add(Events.EntityFilterUnion(typeof(EntityHealEvent)));
        if (Events.HasSubscribers<EntityKnockbackEvent>()) unions.Add(Events.EntityFilterUnion(typeof(EntityKnockbackEvent)));
        var ids = HoldFilterOf([.. unions]) ?? [];
        if (ids.AsSpan().SequenceEqual(_pushedFilter)) return;
        var t = CombatNative.V3;
        if (t == null)
        {
            if (ids.Length > 0)
                Backend.Current.Log(0, LogLevel.Info, "Kiln.Combat: this kiln-combat.dll has no hold filter: every hit is held while a handler wants hits");
            _pushedFilter = [];
            return;
        }
        if (ids.Length > 4096) ids = [];  // more than the native filter takes: hold every hit
        int rc;
        fixed (uint* p = ids) rc = t->HoldFilter(p, (uint)ids.Length);
        if (rc == CombatNative.Ok) _pushedFilter = ids;
    }

    private static bool _sizeWarned;
    private static readonly HashSet<ulong> CanceledHits = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnEvents(CombatNative.Event* events, uint count, void* user)
    {
        try
        {
            if (count > 0 && events[0].Size != (uint)sizeof(CombatNative.Event))
            {
                // A kiln-combat.dll of another release (its event array has a different stride): read nothing.
                if (!_sizeWarned)
                {
                    _sizeWarned = true;
                    Backend.Current.Log(0, LogLevel.Error,
                        $"Kiln.Combat: native events are {events[0].Size} bytes, this assembly expects {sizeof(CombatNative.Event)}; combat events are off");
                }
                return 0;
            }
            CanceledHits.Clear();
            for (uint i = 0; i < count; ++i) Dispatch(ref events[i]);
            CompleteHits();
            PushHoldFilter();
            return Interest();
        }
        catch (Exception e)
        {
            try { Backend.Current.Log(0, LogLevel.Error, $"Kiln.Combat: event dispatch failed: {e}"); } catch { }
            return 0;
        }
    }

    private static void Dispatch(ref CombatNative.Event e)
    {
        switch (e.Kind)
        {
            case CombatNative.Hurt when Events.HasSubscribers<EntityHurtEvent>():
            {
                var ev = Events.Post(new EntityHurtEvent(e));
                e.Amount = ev.Amount;
                e.Canceled = ev.Canceled ? (byte)1 : (byte)0;
                if (ev.Canceled) CanceledHits.Add(e.Id);
                break;
            }
            case CombatNative.Knockback when Events.HasSubscribers<EntityKnockbackEvent>():
            {
                if (CanceledHits.Contains(e.Id)) break;  // the hit itself was canceled: nothing pushes
                var ev = Events.Post(new EntityKnockbackEvent(e));
                e.Canceled = ev.Canceled ? (byte)1 : (byte)0;
                e.PushBack = (byte)Math.Clamp(ev.Strength, 0, 255);
                e.Reaction = (ushort)ev.Reaction;
                if (ev.Direction != ev.OriginalDirection)
                    e.Direction = new CombatNative.Vec3 { X = ev.Direction.X, Y = ev.Direction.Y, Z = ev.Direction.Z };
                break;
            }
            case CombatNative.Fall when Events.HasSubscribers<EntityFallEvent>():
            {
                var ev = new EntityFallEvent(e);
                if (e.Canceled != 0) ev.Cancel();  // god mode
                Events.Post(ev);
                e.Amount = ev.Damage;
                e.Canceled = ev.Canceled ? (byte)1 : (byte)0;
                break;
            }
            case CombatNative.Heal when Events.HasSubscribers<EntityHealEvent>():
            {
                var ev = Events.Post(new EntityHealEvent(e));
                e.Amount = ev.Amount;
                e.Canceled = ev.Canceled ? (byte)1 : (byte)0;
                break;
            }
            case CombatNative.Damaged when Events.HasSubscribers<EntityDamagedEvent>():
                Events.Post(new EntityDamagedEvent(e));
                break;
            case CombatNative.Died when Events.HasSubscribers<EntityDeathEvent>():
                Events.Post(new EntityDeathEvent(e));
                break;
            case CombatNative.Respawned when Events.HasSubscribers<PlayerRespawnEvent>():
                Events.Post(new PlayerRespawnEvent(e));
                break;
            case CombatNative.Drops when Events.HasSubscribers<EntityDropsEvent>():
                e.Canceled = Events.Post(new EntityDropsEvent(e)).Canceled ? (byte)1 : (byte)0;
                break;
        }
    }

    private static void CompleteHits()
    {
        Pending[] all;
        lock (Gate)
        {
            if (PendingHits.Count == 0) return;
            all = PendingHits.ToArray();
        }
        foreach (var p in all)
        {
            var r = Status_(p.Id, p.Target, p.Amount);
            if (r is null || r.State == HitState.Pending) continue;
            lock (Gate) PendingHits.Remove(p);
            p.Source.TrySetResult(r);
        }
    }
}
