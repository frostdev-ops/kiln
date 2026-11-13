// SPDX-License-Identifier: MIT
// The fake kiln.combat (native/core/abi/services/kiln_combat.h, the v1 table) under the real Kiln.Combat.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_combat.h</c>'s v1 table and its structs (with the fields appended later in v1). The sizes
/// are pinned by tests to Kiln.Combat's private mirrors (CombatNative).</summary>
internal static unsafe class CombatAbi
{
    public const string Service = "kiln.combat";
    public const uint Version = 1;  // the v1 table only: attributes (v2) and the hold filter (dev.27) are not there

    public const uint Hurt = 1, Heal = 2, Damaged = 3, Died = 4, Respawned = 5;
    public const uint ForceInterrupt = 0x01, Silent = 0x02, AsGameHit = 0x04, Kill = 0x08, NoCredit = 0x10;
    public const uint Queued = 0, Held = 1, Applied = 2, Dropped = 3;
    public const byte OriginGame = 0, OriginKiln = 1, OriginOther = 2;
    public const uint HitFlagKillingBlow = 0x2000, HitFlagHealing = 0x4000;

    public static uint Mask(uint kind) => 1u << (int)kind;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Health
    {
        public uint Size;
        public int Current, Max;
        public byte Dead, HasActor, Invulnerable, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DamageRequest
    {
        public uint Size, Target, Attacker, Source;
        public int Amount;
        public ushort Reaction;
        public byte PushBack, Reserved0;
        public uint Flags;
        public float Aggro;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HitStatus
    {
        public uint Size, State;
        public int HealthBefore, HealthAfter;
        public byte KillingBlow, R0, R1, R2;
        public uint Steps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size, Kind;
        public ulong Id;
        public uint Target, Part, Attacker, Source;
        public int Amount, HealthBefore, HealthAfter, HealthMax;
        public uint HitFlags;
        public ushort DamageTypes, Reaction;
        public byte AttackCategory, WeaponCategory, Origin, Lethal;
        public byte Canceled, IsPlayer, R0, R1;
        public uint ImpactId;
        public Vec3 Position;
        public ulong Step;
        public Vec3 Direction;
        public float FallDistance, FallFraction;
        public byte PushBack, Cause, R2, R3;
        public uint R4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Health, Hits, Deaths, Holding;
        public uint Interest;
        public ulong HitsSeen, HitsHeld, HitsReleased, HitsCanceled, HitsModified, KilnHits, GodBlocked, GodRestored, DeathsSeen, EventsDelivered;
        public uint Invulnerable, Reserved;
        public ulong GameDiedEvents, HitsDeferred, DropsSeen, DropsCanceled, RepeatHits;
        public byte Falls, Regen, AttrHeals, R0, R1, R2, R3, R4;
        public ulong FallsSeen, FallsCanceled, FallsModified, RegenSeen, RegenCanceled, AttrHealsSeen, AttrHealsCorrected, KnockbacksSeen,
            KnockbacksModified;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint, Health*, int> GetHealth;
        public delegate* unmanaged[Cdecl]<uint, int, int*, int> SetHealth;
        public delegate* unmanaged[Cdecl]<uint, int, int*, int> Heal;
        public delegate* unmanaged[Cdecl]<ulong, DamageRequest*, ulong*, int> Damage;
        public delegate* unmanaged[Cdecl]<ulong, HitStatus*, int> GetHitStatus;
        public delegate* unmanaged[Cdecl]<ulong, uint, byte, int> SetInvulnerable;
        public delegate* unmanaged[Cdecl]<uint, byte> Invulnerable;
        public delegate* unmanaged[Cdecl]<ulong, int> ReleaseOwner;
        public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<Event*, uint, void*, uint>, void*, int> SetListener;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.combat</c> table (v1): health, set_health, heal, damage,
/// hit_status, god mode, release_owner, the listener and status.</summary>
internal static unsafe class CombatTable
{
    private static CombatAbi.Table* _table;

    // One listener for the process (Kiln.Combat sets it once); each harness calls it once a step.
    private static nint _listener, _listenerUser;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (CombatAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(CombatAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(CombatAbi.Table), Version = CombatAbi.Version };
        t->GetHealth = &GetHealth;
        t->SetHealth = &SetHealth;
        t->Heal = &Heal;
        t->Damage = &Damage;
        t->GetHitStatus = &GetHitStatus;
        t->SetInvulnerable = &SetInvulnerable;
        t->Invulnerable = &Invulnerable;
        t->ReleaseOwner = &ReleaseOwner;
        t->SetListener = &SetListener;
        t->GetStatus = &GetStatus;
        _table = t;
        return (nint)t;
    }

    public static (nint Fn, nint User) Listener => (Volatile.Read(ref _listener), Volatile.Read(ref _listenerUser));

    private static CombatFake? Fake => FakeTables.Active(CombatAbi.Service)?.CombatFake;

    private static int Tick(out CombatFake fake)
    {
        fake = null!;
        if (Fake is not { } f) return FakeTables.Unavailable;
        if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
        fake = f;
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetHealth(uint entity, CombatAbi.Health* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            if (FakeTables.Off(CombatFake.HealthFeature)) return FakeTables.Unavailable;
            return f.Read(entity, out_);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetHealth(uint entity, int value, int* applied)
    {
        try
        {
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            if (FakeTables.Off(CombatFake.HealthFeature)) return FakeTables.Unavailable;
            rc = f.Set(entity, value, out var now);
            if (applied != null) *applied = now;
            return rc;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Heal(uint entity, int amount, int* healed)
    {
        try
        {
            if (healed != null) *healed = 0;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            if (FakeTables.Off(CombatFake.HealthFeature)) return FakeTables.Unavailable;
            if (amount < 0) return FakeTables.InvalidArgument;
            rc = f.AddHealth(entity, amount, out var added);
            if (healed != null) *healed = added;
            return rc;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Damage(ulong owner, CombatAbi.DamageRequest* request, ulong* id)
    {
        try
        {
            if (id != null) *id = 0;
            if (request == null || request->Size < (uint)sizeof(CombatAbi.DamageRequest)) return FakeTables.InvalidArgument;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            if (FakeTables.Off(CombatFake.HitsFeature)) return FakeTables.Unavailable;
            rc = f.KilnHit(owner, *request, out var hit);
            if (id != null) *id = hit;
            return rc;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetHitStatus(ulong id, CombatAbi.HitStatus* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            return Fake?.StatusOf(id, out_) ?? FakeTables.Unavailable;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetInvulnerable(ulong owner, uint entity, byte on)
    {
        try
        {
            var rc = Tick(out var f);
            return rc != FakeTables.Ok ? rc : f.God(owner, entity, on != 0);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Invulnerable(uint entity)
    {
        try
        {
            return Fake?.IsGod(entity) == true ? (byte)1 : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReleaseOwner(ulong owner)
    {
        try
        {
            Fake?.Release(owner);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetListener(delegate* unmanaged[Cdecl]<CombatAbi.Event*, uint, void*, uint> fn, void* user)
    {
        Volatile.Write(ref _listenerUser, (nint)user);
        Volatile.Write(ref _listener, (nint)fn);
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetStatus(CombatAbi.Status* out_)
    {
        try
        {
            // a caller built before the fall/regen fields passes the shorter size and gets the fields it knows (t_status)
            if (out_ == null || out_->Size < (uint)Marshal.OffsetOf<CombatAbi.Status>(nameof(CombatAbi.Status.Falls))) return FakeTables.InvalidArgument;
            var size = Math.Min(out_->Size, (uint)sizeof(CombatAbi.Status));
            var s = new CombatAbi.Status { Size = size };
            Fake?.Fill(ref s);  // else nothing works: no server world runs
            Buffer.MemoryCopy(&s, out_, size, size);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }
}

/// <summary>One harness's combat: health per entity of the fake world (what the game keeps in its Health component),
/// hits from mods and from "the game" (the test), held for the listener when it asks, applied at the start of the next
/// step, with the events the native service reports.</summary>
internal sealed unsafe class CombatFake
{
    public const string HealthFeature = "combat.health", HitsFeature = "combat.hits", DeathsFeature = "combat.deaths",
        ActorStatesFeature = "combat.actor_states";

    public static readonly string[] Features = [HealthFeature, HitsFeature, DeathsFeature, ActorStatesFeature];

    public static readonly string[] OfflineRows = ["combat.falls", "combat.regen", "combat.attr_heals", "combat.attributes", "combat.hold_filter"];

    internal sealed class Hp
    {
        public int Current, Max;
        public FakePlayer? Player;  // a player's health is a component of that player's entity: it ends with the player

        /// <summary>Dead as the game's death check leaves it: below 1 health.</summary>
        public bool Dead => Current < 1;
    }

    internal sealed class Hit
    {
        public required ulong Id, Owner, Requested;
        public required uint Target, Attacker, Source, Flags;
        public required int Amount;
        public required byte Origin;
        public required ushort Reaction;
        public required string? Mod;
        public uint State = CombatAbi.Queued;
        public ulong Settled;
        public int Before = -1, After = -1;
        public bool Killing, Modified, WasHeld;
    }

    private static long _ids;  // per process: hit and event ids never repeat

    private readonly KilnHarness _harness;
    private readonly object _lock = new();
    private readonly Dictionary<uint, Hp> _health = [];
    private readonly Dictionary<uint, HashSet<ulong>> _god = [];
    private readonly Dictionary<ulong, Hit> _hits = [];
    private readonly List<Hit> _order = [];
    private readonly List<Hit> _queue = [];  // applies at the start of the next step
    private readonly List<Hit> _fresh = [];  // the game's hits of the last step, to be held for the listener or applied
    private readonly List<CombatAbi.Event> _events = [];  // for the next listener call
    // health that fell below 1 since the last step, with the killing blow's: the death check decides at the next step
    private readonly Dictionary<uint, (uint Attacker, uint Source, byte Origin)> _dying = [];
    private readonly HashSet<uint> _reported = [];  // dead and reported; forgotten once alive again
    private uint _interest;
    private ulong _seen, _held, _released, _canceled, _modified, _kiln, _godBlocked, _deaths, _delivered;

    public CombatFake(KilnHarness harness) => _harness = harness;

    private bool Running => _harness.State == HarnessState.Running;

    private EntitiesFake? World => _harness.EntitiesFake;

    private bool IsPlayer(uint entity) => PlayerAt(entity) is not null;

    private FakePlayer? PlayerAt(uint entity) => _harness.PlayersFake?.Present.FirstOrDefault(p => p.InGame && p.Entity.Value == entity);

    private Hp? Of(uint entity)
    {
        lock (_lock)
        {
            if (!_health.TryGetValue(entity, out var hp)) return null;
            var player = PlayerAt(entity);
            if (World?.Exists(entity) == true && (hp.Player is null ? player is null : ReferenceEquals(hp.Player, player))) return hp;
            _health.Remove(entity);  // the entity is gone (or its player: the next one in the slot is another entity), and its Health with it
            _dying.Remove(entity);
            _reported.Remove(entity);
            return null;
        }
    }

    public bool IsGod(uint entity)
    {
        lock (_lock) return _god.TryGetValue(entity, out var owners) && owners.Count > 0;
    }

    // ---- the test's hand ----

    public void Give(uint entity, int current, int max)
    {
        if (World?.Exists(entity) != true) throw new ArgumentException($"entity {entity} is not in the fake world (make it, or join a fake player)", nameof(entity));
        lock (_lock)
        {
            _health[entity] = new Hp { Current = Math.Clamp(current, 0, max), Max = max, Player = PlayerAt(entity) };
            _dying.Remove(entity);
            if (current < 1) _reported.Add(entity);  // dead as the game has it: nothing to report
            else _reported.Remove(entity);
        }
    }

    public Hp? HealthOf(uint entity) => Of(entity) is { } hp ? new Hp { Current = hp.Current, Max = hp.Max } : null;

    public ulong GameHit(uint target, int amount, uint attacker, uint source)
    {
        var hit = NewHit(0, target, attacker, source, amount, 0, CombatAbi.OriginGame, 0, null);
        lock (_lock)
        {
            _fresh.Add(hit);
            _seen++;
        }
        return hit.Id;
    }

    public void Respawn(uint player, int health)
    {
        lock (_lock)
        {
            if (Of(player) is not { } hp) throw new ArgumentException($"entity {player} has no health", nameof(player));
            if (!hp.Dead) throw new InvalidOperationException($"entity {player} is not dead");
            var before = hp.Current;
            hp.Current = Math.Clamp(health, 1, hp.Max);
            _dying.Remove(player);
            _reported.Remove(player);
            _events.Add(NewEvent(CombatAbi.Respawned, player, 0, 0, 0, before, hp.Current, hp.Max, CombatAbi.OriginOther));
        }
    }

    public IReadOnlyList<Hit> Hits
    {
        get
        {
            lock (_lock) return [.. _order];
        }
    }

    // ---- the table (tick thread) ----

    public int Read(uint entity, CombatAbi.Health* out_)
    {
        if (Of(entity) is not { } hp) return FakeTables.NotFound;
        *out_ = new CombatAbi.Health
        {
            Size = (uint)sizeof(CombatAbi.Health), Current = hp.Current, Max = hp.Max, Dead = hp.Dead ? (byte)1 : (byte)0, HasActor = 1,
            Invulnerable = IsGod(entity) ? (byte)1 : (byte)0,
        };
        return FakeTables.Ok;
    }

    public int Set(uint entity, int value, out int applied)
    {
        applied = 0;
        if (!Running) return FakeTables.Unavailable;
        if (Of(entity) is not { } hp) return FakeTables.NotFound;
        lock (_lock)
        {
            var floor = IsGod(entity) ? Math.Min(1, hp.Max) : 0;  // god mode keeps 1
            hp.Current = Math.Clamp(value, floor, hp.Max);
            applied = hp.Current;
            if (hp.Current < 1) _dying.TryAdd(entity, (0, 0, CombatAbi.OriginOther));  // the death check at the next step, without a killer
        }
        return FakeTables.Ok;
    }

    public int AddHealth(uint entity, int amount, out int added)
    {
        added = 0;
        if (!Running) return FakeTables.Unavailable;
        if (Of(entity) is not { } hp) return FakeTables.NotFound;
        lock (_lock)
        {
            added = (int)Math.Max(0, Math.Min((long)amount, (long)hp.Max - hp.Current));
            hp.Current += added;
        }
        return FakeTables.Ok;
    }

    public int KilnHit(ulong owner, CombatAbi.DamageRequest r, out ulong id)
    {
        id = 0;
        if (!Running) return FakeTables.Unavailable;
        if (Of(r.Target) is null) return FakeTables.NotFound;
        if ((r.Flags & CombatAbi.Kill) == 0 && r.Amount == 0) return FakeTables.InvalidArgument;
        var hit = NewHit(owner, r.Target, r.Attacker, r.Source == 0 ? r.Attacker : r.Source, r.Amount, r.Flags, CombatAbi.OriginKiln, r.Reaction,
            HostAccess.Current?.Info.Id);
        lock (_lock)
        {
            if ((r.Flags & CombatAbi.AsGameHit) != 0) _fresh.Add(hit);  // like a hit the engine produced: held for the listener
            else _queue.Add(hit);
            _seen++;
        }
        id = hit.Id;
        return FakeTables.Ok;
    }

    private Hit NewHit(ulong owner, uint target, uint attacker, uint source, int amount, uint flags, byte origin, ushort reaction, string? mod)
    {
        var hit = new Hit
        {
            Id = (ulong)Interlocked.Increment(ref _ids), Owner = owner, Requested = _harness.CurrentStep, Target = target, Attacker = attacker,
            Source = source == 0 ? attacker : source, Flags = flags, Amount = amount, Origin = origin, Reaction = reaction, Mod = mod,
        };
        lock (_lock)
        {
            _hits[hit.Id] = hit;
            _order.Add(hit);
        }
        return hit;
    }

    public int StatusOf(ulong id, CombatAbi.HitStatus* out_)
    {
        lock (_lock)
        {
            if (!_hits.TryGetValue(id, out var h)) return FakeTables.NotFound;
            *out_ = new CombatAbi.HitStatus
            {
                Size = (uint)sizeof(CombatAbi.HitStatus), State = h.State, HealthBefore = h.Before, HealthAfter = h.After,
                KillingBlow = h.Killing ? (byte)1 : (byte)0,
                Steps = (uint)Math.Max(0L, (long)(h.State is CombatAbi.Applied or CombatAbi.Dropped ? h.Settled : _harness.CurrentStep) - (long)h.Requested),
            };
            return FakeTables.Ok;
        }
    }

    public int God(ulong owner, uint entity, bool on)
    {
        if (Of(entity) is null && !IsPlayer(entity)) return FakeTables.NotFound;
        lock (_lock)
        {
            if (!_god.TryGetValue(entity, out var owners)) _god[entity] = owners = [];
            if (on) owners.Add(owner);
            else owners.Remove(owner);
            if (owners.Count == 0) _god.Remove(entity);
        }
        return FakeTables.Ok;
    }

    public void Release(ulong owner)
    {
        lock (_lock)
        {
            foreach (var (e, owners) in _god.ToList())
                if (owners.Remove(owner) && owners.Count == 0) _god.Remove(e);
            // the owner's requests the engine has not concluded or held yet (t_release_owner erases them from g_requests); a
            // hit that was held for the listener is the engine's by now and applies
            foreach (var list in new[] { _queue, _fresh })
                foreach (var h in list.Where(h => h.Owner == owner && owner != 0 && h.Origin == CombatAbi.OriginKiln && !h.WasHeld).ToList())
                {
                    list.Remove(h);
                    h.State = CombatAbi.Dropped;
                    h.Settled = _harness.CurrentStep;
                }
        }
    }

    public void Fill(ref CombatAbi.Status s)
    {
        lock (_lock)
        {
            s.Health = FakeTables.Off(HealthFeature) ? (byte)0 : (byte)1;
            s.Hits = FakeTables.Off(HitsFeature) ? (byte)0 : (byte)1;
            s.Deaths = FakeTables.Off(DeathsFeature) ? (byte)0 : (byte)1;
            s.Holding = (_interest & (CombatAbi.Mask(CombatAbi.Hurt) | CombatAbi.Mask(CombatAbi.Heal))) != 0 ? (byte)1 : (byte)0;
            s.Interest = _interest;
            s.HitsSeen = _seen;
            s.HitsHeld = _held;
            s.HitsReleased = _released;
            s.HitsCanceled = _canceled;
            s.HitsModified = _modified;
            s.KilnHits = _kiln;
            s.GodBlocked = _godBlocked;
            s.DeathsSeen = _deaths;
            s.EventsDelivered = _delivered;
            s.Invulnerable = (uint)_god.Count;
        }
    }

    // ---- a step: the hits concluded since the last one, then the listener (the game's Pre) ----

    public void BeforeStep(ulong step)
    {
        List<Hit> apply, held;
        lock (_lock)
        {
            apply = [.. _queue];
            _queue.Clear();
            var holding = (_interest & (CombatAbi.Mask(CombatAbi.Hurt) | CombatAbi.Mask(CombatAbi.Heal))) != 0;
            held = holding ? [.. _fresh] : [];
            if (!holding) apply.AddRange(_fresh);
            _fresh.Clear();
        }
        foreach (var h in apply) Conclude(h, step);
        DeathCheck();
        var batch = new List<CombatAbi.Event>();
        lock (_lock)
        {
            batch.AddRange(_events);
            _events.Clear();
            foreach (var h in held)
            {
                h.State = CombatAbi.Held;
                h.WasHeld = true;
                _held++;
                var hp = Of(h.Target);
                var heal = h.Amount < 0;
                var amount = (h.Flags & CombatAbi.Kill) != 0 ? hp?.Current ?? 0 : (int)Math.Min(Math.Abs((long)h.Amount), int.MaxValue);
                var e = NewEvent(heal ? CombatAbi.Heal : CombatAbi.Hurt, h.Target, h.Attacker, h.Source, amount, hp?.Current ?? -1, -1,
                    hp?.Max ?? -1, h.Origin, h.Id);
                e.Lethal = !heal && hp is not null && amount >= hp.Current ? (byte)1 : (byte)0;
                e.Reaction = h.Reaction;
                batch.Add(e);
            }
        }
        var (fn, user) = CombatTable.Listener;
        if (fn == 0) return;
        var events = batch.ToArray();
        uint interest;
        fixed (CombatAbi.Event* p = events)
            interest = ((delegate* unmanaged[Cdecl]<CombatAbi.Event*, uint, void*, uint>)fn)(p, (uint)events.Length, (void*)user);
        lock (_lock)
        {
            _interest = interest;
            _delivered += (ulong)events.Length;
            // the held hits as the handlers left them: canceled ones dropped, the others apply at the start of the next step
            foreach (var e in events)
            {
                if (e.Kind is not (CombatAbi.Hurt or CombatAbi.Heal) || !_hits.TryGetValue(e.Id, out var h)) continue;
                if (e.Canceled != 0)
                {
                    h.State = CombatAbi.Dropped;
                    h.Settled = step;
                    _canceled++;
                    continue;
                }
                var amount = Math.Max(0, e.Amount) * (e.Kind == CombatAbi.Heal ? -1 : 1);
                if (amount != h.Amount)
                {
                    h.Amount = amount;
                    h.Modified = true;
                    _modified++;
                }
                h.State = CombatAbi.Queued;
                _released++;
                _queue.Add(h);
            }
        }
    }

    // The engine's hit conclusion for one hit: health changes, the killing blow, the events for the listener.
    private void Conclude(Hit h, ulong step)
    {
        lock (_lock)
        {
            h.Settled = step;
            var hp = Of(h.Target);
            var damage = h.Amount > 0 || (h.Flags & CombatAbi.Kill) != 0;
            if (hp is null || (damage && IsGod(h.Target)) || (h.Amount == 0 && (h.Flags & CombatAbi.Kill) == 0))
            {
                if (hp is not null && damage) _godBlocked++;
                h.State = CombatAbi.Dropped;
                return;
            }
            var amount = (h.Flags & CombatAbi.Kill) != 0 && !h.Modified ? hp.Current : h.Amount;
            h.Before = hp.Current;
            hp.Current = (int)Math.Clamp((long)hp.Current - amount, 0, hp.Max);
            h.After = hp.Current;
            h.Killing = h.Before > 0 && h.After <= 0;
            h.State = CombatAbi.Applied;
            if (h.Origin == CombatAbi.OriginKiln) _kiln++;
            var e = NewEvent(CombatAbi.Damaged, h.Target, h.Attacker, h.Source, h.Before - h.After, h.Before, h.After, hp.Max, h.Origin, h.Id);
            e.HitFlags = (h.Killing ? CombatAbi.HitFlagKillingBlow : 0) | (amount < 0 ? CombatAbi.HitFlagHealing : 0);
            e.Reaction = h.Reaction;
            _events.Add(e);
            if (h.Killing) _dying[h.Target] = (h.Attacker, h.Source, h.Origin);  // the killing blow's credit
        }
    }

    // The game's death check, once a step: an entity whose health fell below 1 and is still below 1 now died (with the
    // killing blow's credit when a hit did it); one healed back before the check survived. An entity is reported once
    // until it is alive again.
    private void DeathCheck()
    {
        lock (_lock)
        {
            foreach (var e in _reported.Where(e => Of(e) is not { Dead: true }).ToList()) _reported.Remove(e);
            foreach (var (entity, kill) in _dying.ToList())
            {
                _dying.Remove(entity);
                if (Of(entity) is not { Dead: true } hp || !_reported.Add(entity)) continue;
                _deaths++;
                _events.Add(NewEvent(CombatAbi.Died, entity, kill.Attacker, kill.Source, 0, -1, hp.Current, hp.Max, kill.Origin));
            }
        }
    }

    private CombatAbi.Event NewEvent(uint kind, uint target, uint attacker, uint source, int amount, int before, int after, int max, byte origin, ulong id = 0)
    {
        var at = World?.PositionOf(target) ?? _harness.PlayersFake?.Present.FirstOrDefault(p => p.Entity.Value == target)?.Position ?? default;
        return new CombatAbi.Event
        {
            Size = (uint)sizeof(CombatAbi.Event), Kind = kind, Id = id != 0 ? id : (ulong)Interlocked.Increment(ref _ids), Target = target, Part = target,
            Attacker = attacker, Source = source, Amount = amount, HealthBefore = before, HealthAfter = after, HealthMax = max, Origin = origin,
            IsPlayer = IsPlayer(target) ? (byte)1 : (byte)0, Position = new CombatAbi.Vec3 { X = at.X, Y = at.Y, Z = at.Z }, Step = _harness.CurrentStep,
        };
    }
}
