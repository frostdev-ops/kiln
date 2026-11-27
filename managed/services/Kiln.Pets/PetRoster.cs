// SPDX-License-Identifier: MIT
// The pets of a world (19d §4.1, §4.2, §4.7): one central roster owns every PetId. Records are kept per character in the
// world's save (IPetStore), bodies are made and retired through IPetWorld; every change runs on the server tick thread,
// serialized per pet, and settles before its events are posted. StateRevision rises once per logical change; after-events
// of one change share it; a distinct intermediate transition has its own. The save receipt acknowledges only the revision
// the committed bytes hold.
namespace Kiln;

internal sealed class PetState
{
    // kept in the record
    public PetId Id;
    public CharacterKey Owner;
    public string Definition = "";
    public PetLife Life;
    public bool WantsSummoned;
    public PetOrder Order;
    public int Level = 1;
    public double HealthFraction = 1;
    public int LastHealthMax;
    public WorldPosition? Anchor;
    public string? AnchorWorld;
    public ulong StateRevision;
    // derived from receipts
    public ulong DurableRevision;
    public PetCommit? LastCommit;
    // session
    public PetPresence Presence;
    public PetTransition Transition;
    public EntityRef Target;
    public PetBody? Body;
    public int Health;
    public int HealthMax;
    public WorldPosition? LastSafe;
    public PetRoster.Op? Running;
    public PetDefinition? Snapshot;          // the definition a live body or a running operation uses (immutable meanwhile)
    public PetChangeCause? PendingDown;      // a down the final save staging already wrote (its retirement follows)
    public PetChangeCause? PendingLeave;     // a logout whose retirement is retried
    public PetChangeCause? DownCause;
    public bool DownCaptured;
    public PetDownedOutcome? DownOutcome;
    public int RetireTries;
    public Guid? ReviveOperation;
    public double ReviveElapsed, ReviveDuration;
    public object? Session;                  // the follower's / the broker's own state (packages 3, 4)

    public string Label => Snapshot?.DisplayName ?? PetsRuntime.Definition(Definition)?.DisplayName ?? Definition;

    public PetSnapshot View(ulong epoch) => new()
    {
        Id = Id, Owner = Owner, Definition = Definition, Label = Label,
        WorldEpoch = Body?.WorldEpoch ?? 0, BodyGeneration = Body?.Generation ?? 0, Body = Body?.Entity ?? EntityRef.None,
        Life = Life, Presence = Presence, Transition = Transition, WantsSummoned = WantsSummoned, RequestedOrder = Order,
        RequestedTarget = Target, RecoveryAnchor = Presence == PetPresence.RecoveryAnchor || (Life == PetLife.Downed && Anchor is not null) ? Anchor : null,
        Level = Level,
        Health = Life == PetLife.Downed ? 0 : Body is not null ? Health : HealthOf(HealthFraction, LastHealthMax),
        HealthMax = Body is not null ? HealthMax : LastHealthMax,
        StateRevision = StateRevision, DurableRevision = DurableRevision, LastCommit = LastCommit,
        ReviveOperation = ReviveOperation, ReviveElapsed = ReviveElapsed, ReviveDuration = ReviveDuration,
    };

    /// <summary>ceil(max x fraction), as revive and summon restore it (a share stored as 0.55 of 100 is 55, not 56).</summary>
    public static int HealthOf(double fraction, int max) => max <= 0 ? 0 : (int)Math.Ceiling(Math.Clamp(fraction, 0, 1) * max - 1e-6);

    public PetRecordData Record() => new()
    {
        Id = Id, Definition = Definition, Life = Life, WantsSummoned = WantsSummoned, RequestedOrder = Order == PetOrder.Stay ? PetOrder.Stay : PetOrder.Follow,
        Level = Level, HealthFraction = HealthFraction, LastHealthMax = LastHealthMax, RecoveryAnchor = Anchor, AnchorWorld = AnchorWorld,
        StateRevision = StateRevision,
    };
}

internal sealed partial class PetRoster
{
    internal const int MaxRecordsPerCharacter = 128, MaxBodies = 256;
    internal const double RetainSeconds = 600, AnchorMeters = 3;
    internal const int RetireRetries = 120;   // steps a failed retirement is retried (protected meanwhile) before RetireFailed

    private readonly IPetWorld _world;
    private readonly IPetStore _store;
    private readonly Dictionary<CharacterKey, OwnerState> _owners = [];
    private readonly Dictionary<PetId, PetState> _pets = [];
    private readonly List<Op> _queue = [];
    private readonly Dictionary<(CharacterKey, Guid), Op> _retained = [];
    private readonly HashSet<CharacterKey> _online = [];
    private readonly Dictionary<uint, Dictionary<CharacterKey, (string Sha, Dictionary<PetId, (ulong Revision, PetLife Life)> Revisions)>> _receipts = [];
    private int _saveSession = -1;
    private ulong _epoch;
    private bool _presenceChanged;
    private readonly List<KilnEvent> _events = [];

    /// <summary>Where settled after-events go (PetsRuntime's queue; tests read them).</summary>
    internal Action<KilnEvent> Post { get; set; } = PetsRuntime.Enqueue;
    /// <summary>Asks the mods what a pet that goes down becomes (posts PetDowningEvent at once; tests answer).</summary>
    internal Func<PetDowningEvent, PetDowningEvent> AskDowning { get; set; } = e => Events.Post(e);
    /// <summary>Whether a target may be attacked by the pet; null: yes, else the reason. Default: the pets' target rule
    /// (PetTargetRule) on what the world says of the target.</summary>
    internal Func<PetState, EntityRef, string?> TargetCheck { get; set; }

    /// <summary>Character pairs a mod declared hostile (PvP by mod rule), each ordered.</summary>
    private readonly HashSet<(CharacterKey, CharacterKey)> _hostile = [];
    private bool _hostileDirty;

    internal void SetHostile(CharacterKey a, CharacterKey b, bool on)
    {
        var pair = PetTargetRule.Pair(a, b);
        if (on ? _hostile.Add(pair) : _hostile.Remove(pair)) _hostileDirty = true;
    }

    internal bool IsHostile(CharacterKey a, CharacterKey b) => _hostile.Contains(PetTargetRule.Pair(a, b));

    private string? DefaultTargetCheck(PetState p, EntityRef target)
    {
        var def = p.Snapshot ?? PetsRuntime.Definition(p.Definition);
        if (def is null) return "DefinitionUnknown";
        var targetPet = _pets.Values.FirstOrDefault(x => x.Body is { } b && b.Entity.Id == target.Id);
        var targetDef = targetPet is null ? null : targetPet.Snapshot ?? PetsRuntime.Definition(targetPet.Definition);
        return PetTargetRule.Check(p, def, target, _world.Classify(target), targetPet, targetDef, _hostile);
    }

    internal sealed class OwnerState(CharacterKey key)
    {
        public readonly CharacterKey Key = key;
        public readonly List<PetState> Pets = [];
        public bool Dirty;
        public string? Unread;   // a document Kiln could not read: kept as it is, never overwritten
    }

    public PetRoster(IPetWorld world, IPetStore store)
    {
        TargetCheck = DefaultTargetCheck;
        _world = world;
        _store = store;
        _epoch = world.WorldEpoch;
        store.Staging += OnStaging;
        store.Staged += OnStaged;
        store.Committed += OnCommitted;
    }

    public void Detach()
    {
        _store.Staging -= OnStaging;
        _store.Staged -= OnStaged;
        _store.Committed -= OnCommitted;
    }

    // ---- reads ----

    public PetSnapshot? Get(PetId id) => _pets.TryGetValue(id, out var p) ? p.View(_epoch) : null;

    public IReadOnlyList<PetSnapshot> Of(CharacterKey owner)
    {
        var o = EnsureLoaded(owner);
        return o is null ? [] : o.Pets.Where(p => p.Life != PetLife.Released).Select(p => p.View(_epoch)).ToList();
    }

    internal PetState? State(PetId id) => _pets.GetValueOrDefault(id);

    /// <summary>The owner's view of their pets (released ones left out), for the channel kiln:pets.</summary>
    internal IReadOnlyList<PetViewEntry> ViewOf(CharacterKey owner)
    {
        if (!_owners.TryGetValue(owner, out var o)) return [];
        var list = new List<PetViewEntry>();
        foreach (var p in o.Pets.Where(p => p.Life != PetLife.Released))
        {
            var v = p.View(_epoch);
            var hud = (p.Snapshot ?? PetsRuntime.Definition(p.Definition))?.Hud ?? new PetHudPolicy();
            list.Add(new PetViewEntry
            {
                Id = p.Id, Definition = p.Definition, Label = v.Label, Life = v.Life, Presence = v.Presence, Transition = v.Transition,
                Body = p.Body is { } b ? new PetBodyViewKey(b.Entity.Id.Value, b.Generation) : null,
                RecoveryAnchor = v.Presence == PetPresence.RecoveryAnchor ? v.RecoveryAnchor : null, RequestedOrder = v.RequestedOrder,
                Level = v.Level, Current = v.Health, Maximum = v.HealthMax, ReviveOperation = v.ReviveOperation,
                ReviveElapsedMs = (uint)Math.Clamp(v.ReviveElapsed * 1000, 0, uint.MaxValue), ReviveDurationMs = (uint)Math.Clamp(v.ReviveDuration * 1000, 0, uint.MaxValue),
                StateRevision = v.StateRevision, DurableRevision = v.DurableRevision, ShowBar = hud.Bar, ShowMarker = hud.RecoveryMarker,
            });
        }
        return list;
    }
    internal IEnumerable<PetState> AllStates => _pets.Values;
    internal IPetWorld World => _world;

    public bool DefinitionInUse(string id) =>
        _pets.Values.Any(p => p.Definition == id && (p.Body is not null || p.Running is not null || p.Transition != PetTransition.None));

    internal int LiveBodies => _pets.Values.Count(p => p.Body is not null);

    // ---- requests ----

    internal enum Kind { Grant, Summon, Dismiss, Release, Command, Revive, CancelRevive, LoginSummon }

    internal sealed class Op
    {
        public Kind Kind;
        public Guid OperationId;
        public CharacterKey OwnerKey;
        public PlayerRef Who;
        public PetId Pet;
        public string Payload = "";
        public ulong Expected;
        public PetOrder Order;
        public EntityRef Target;
        public string? Definition;
        public Guid CancelTarget;
        public ItemSelection? Selection;
        public readonly TaskCompletionSource<PetResult> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double DoneAt = double.NaN;
        public int Phase;
        public double Since;
        public PetBody? Body;
        public PetOwner Owner;
        public int Level;
        public object? Extra;
        public List<CostReceipt> Costs = [];
        public bool Internal => Kind == Kind.LoginSummon;
    }

    /// <summary>Queues a request; it starts at the next step. A retained operation id gives its first task back; the same id
    /// with another payload answers RequestConflict.</summary>
    internal Task<PetResult> Submit(Op op)
    {
        var key = (op.OwnerKey, op.OperationId);
        if (_retained.TryGetValue(key, out var have))
            return have.Payload == op.Payload ? have.Source.Task
                : Task.FromResult(new PetResult(false, "RequestConflict: this operation id was used for another request", Get(op.Pet), op.OperationId, []));
        _retained[key] = op;
        _queue.Add(op);
        return op.Source.Task;
    }

    private void Complete(Op op, bool ok, string? reason, PetState? p)
    {
        if (op.Source.Task.IsCompleted) return;
        op.DoneAt = _world.Now;
        if (p is not null && ReferenceEquals(p.Running, op)) p.Running = null;
        op.Source.TrySetResult(new PetResult(ok, ok ? null : reason, p?.View(_epoch), op.OperationId, op.Costs.ToList()));
    }

    // ---- the step ----

    /// <summary>The service tick (start of a step, tick thread).</summary>
    public void Step()
    {
        if (_world.WorldEpoch != _epoch) WorldChanged();
        Presence();
        if (_hostileDirty || _presenceChanged)
        {
            _world.SyncHostility(_hostile);
            _hostileDirty = _presenceChanged = false;
        }
        Reconcile();
        Move();
        AdvanceRunning();
        StartQueued();
        Expire();
        Flush();
    }

    private void Flush()
    {
        if (_events.Count == 0) return;
        var list = _events.ToList();
        _events.Clear();
        foreach (var e in list) Post(e);
    }

    private void Expire()
    {
        var now = _world.Now;
        foreach (var (key, op) in _retained.Where(x => !double.IsNaN(x.Value.DoneAt) && now - x.Value.DoneAt >= RetainSeconds).ToList())
            _retained.Remove(key);
    }

    /// <summary>The server world ended (or another started): nothing is restored into another world. Bodies are gone with
    /// it; live pets become absent with their health kept; running requests end WorldEnded; the next world's records are
    /// read from its own save.</summary>
    private void WorldChanged()
    {
        foreach (var p in _pets.Values)
        {
            if (p.Running is { } op) Complete(op, false, "WorldEnded: the server world ended", p);
            p.Body = null;
            p.Presence = PetPresence.Absent;
            p.Transition = PetTransition.None;
            p.Target = EntityRef.None;
            p.ReviveOperation = null;
            p.Snapshot = null;
            DropSession(p);
        }
        foreach (var op in _queue) Complete(op, false, "WorldEnded: the server world ended", null);
        _queue.Clear();
        _owners.Clear();
        _pets.Clear();
        _online.Clear();
        _receipts.Clear();
        _epoch = _world.WorldEpoch;
        OnWorldChanged?.Invoke();
    }

    internal event Action? OnWorldChanged;

    /// <summary>The mods that defined <paramref name="definitions"/> unloaded: requests of their pets end, bodies are retired
    /// (their health and the owner's wish kept), anchors stay. The records stay, dormant until a definition comes back.</summary>
    internal void Unload(IReadOnlyCollection<string> definitions)
    {
        foreach (var p in _pets.Values.Where(p => definitions.Contains(p.Definition)).ToList())
        {
            if (p.Running is { Kind: Kind.Revive }) CancelChannel(p, PetChangeCause.Unload);
            else if (p.Running is { } op) Complete(op, false, "DefinitionUnloaded: the mod that defined the pet unloaded", p);
            if (p.Body is not null) RetireForLeave(p, PetChangeCause.Unload);
        }
        foreach (var op in _queue.Where(q => _pets.TryGetValue(q.Pet, out var p) && definitions.Contains(p.Definition)).ToList())
        {
            _queue.Remove(op);
            Complete(op, false, "DefinitionUnloaded: the mod that defined the pet unloaded", State(op.Pet));
        }
        Flush();
    }

    // ---- owners coming and going ----

    private void Presence()
    {
        if (_epoch == 0) return;
        var now = new HashSet<CharacterKey>();
        foreach (var o in _world.OnlineOwners())
            if (!o.Key.IsNone && !o.Key.IsAccountOnly) now.Add(o.Key);
        foreach (var left in _online.Where(k => !now.Contains(k)).ToList())
        {
            _presenceChanged = true;
            _online.Remove(left);
            OwnerLeft(left);
        }
        foreach (var came in now.Where(k => !_online.Contains(k)).ToList())
        {
            _presenceChanged = true;
            _online.Add(came);
            OwnerCame(came);
        }
    }

    /// <summary>Logout: requests end, live bodies are retired (Ready becomes Ready/Absent with its health kept, Downed stays
    /// Downed/Absent); WantsSummoned is kept, so the next login brings it back. No release, no ownership loss.</summary>
    private void OwnerLeft(CharacterKey key)
    {
        if (!_owners.TryGetValue(key, out var o)) return;
        foreach (var p in o.Pets)
        {
            if (p.Running is { Kind: Kind.Revive }) CancelChannel(p, PetChangeCause.Logout);   // its preview goes with it
            else if (p.Running is { } op) Complete(op, false, "OwnerLeft: the owner left the world", p);
            CancelChannel(p, PetChangeCause.Logout);
            if (p.Body is not null) RetireForLeave(p, PetChangeCause.Logout);
            else if (p.Presence == PetPresence.RecoveryAnchor)
                Mutate(p, Guid.Empty, PetChangeCause.Logout, s => s.Presence = PetPresence.Absent, c => new PetRecoveryAnchorChangedEvent(c));
        }
        foreach (var op in _queue.Where(q => q.OwnerKey == key).ToList())
        {
            _queue.Remove(op);
            Complete(op, false, "OwnerLeft: the owner left the world", State(op.Pet));
        }
    }

    private void RetireForLeave(PetState p, PetChangeCause cause)
    {
        ReadInto(p);
        if (!_world.Retire(p.Body!, cause))
        {
            // retried at the next step: the body stays protected meanwhile, never left as a wild creature
            p.Presence = PetPresence.Retiring;
            p.PendingLeave = cause;
            return;
        }
        p.PendingLeave = null;
        Mutate(p, Guid.Empty, cause, s =>
        {
            s.Body = null;
            s.Presence = PetPresence.Absent;
            s.Transition = PetTransition.None;
            s.Target = EntityRef.None;
            s.Snapshot = null;
            DropSession(s);
        }, c => new PetBodyRetiredEvent(c));
    }

    /// <summary>Login: the records are read, and what the owner wanted out comes back (a Ready pet's body; a Downed pet's
    /// anchor next to its owner, never its health).</summary>
    private void OwnerCame(CharacterKey key)
    {
        var o = EnsureLoaded(key);
        if (o is null) return;
        foreach (var p in o.Pets.Where(p => p.WantsSummoned && p.Life != PetLife.Released && p.Body is null && p.Running is null))
            _queue.Add(new Op { Kind = Kind.LoginSummon, OperationId = Guid.NewGuid(), OwnerKey = key, Pet = p.Id, Payload = "login" });
    }

    internal OwnerState? EnsureLoaded(CharacterKey key)
    {
        if (key.IsNone || key.IsAccountOnly) return null;
        if (_owners.TryGetValue(key, out var have)) return have;
        if (!_store.Available || _epoch == 0) return null;
        var o = new OwnerState(key);
        if (_store.Load(key) is { } json)
        {
            var problems = new List<string>();
            var doc = PetRecordJson.Read(json, key, problems);
            foreach (var line in problems) PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: character {key}: {line}");
            if (doc is null) o.Unread = json;
            else
                foreach (var r in doc.Pets)
                {
                    if (_pets.ContainsKey(r.Id)) continue;  // one PetId, one owner
                    var p = new PetState
                    {
                        Id = r.Id, Owner = key, Definition = r.Definition, Life = r.Life, WantsSummoned = r.WantsSummoned, Order = r.RequestedOrder,
                        Level = r.Level, HealthFraction = r.Life == PetLife.Downed ? 0 : r.HealthFraction, LastHealthMax = r.LastHealthMax,
                        Anchor = r.RecoveryAnchor, AnchorWorld = r.AnchorWorld, StateRevision = r.StateRevision, DurableRevision = r.StateRevision,
                        Presence = PetPresence.Absent,
                    };
                    o.Pets.Add(p);
                    _pets[p.Id] = p;
                }
        }
        _owners[key] = o;
        return o;
    }

    // ---- live bodies ----

    private void ReadInto(PetState p)
    {
        if (p.Body is null) return;
        var r = _world.Read(p.Body);
        if (!r.Exists || r.Lethal) return;
        if (r.HealthMax > 0)
        {
            p.Health = r.Health;
            p.HealthMax = r.HealthMax;
            p.LastHealthMax = r.HealthMax;
            p.HealthFraction = Math.Clamp((double)r.Health / r.HealthMax, 0, 1);
        }
    }

    /// <summary>Every bound body, every step, before any request: a body gone is a lost body (never rebuilt by itself), a
    /// lethal reading or the guard's latch is a down, health changes are stats.</summary>
    private void Reconcile()
    {
        foreach (var p in _pets.Values.ToList())
        {
            if (p.PendingLeave is { } leave && p.Body is not null)
            {
                RetireForLeave(p, leave);
                continue;
            }
            if (p.Transition == PetTransition.Downing)
            {
                ContinueDown(p);
                continue;
            }
            if (p.PendingDown is { } pending)
            {
                BeginDown(p, pending);
                continue;
            }
            if (p.Body is null || p.Presence != PetPresence.Live || p.Transition is PetTransition.Spawning or PetTransition.Dismissing or PetTransition.Releasing) continue;
            var r = _world.Read(p.Body);
            if (!r.Exists)
            {
                BeginDown(p, PetChangeCause.BodyLost);
                continue;
            }
            if (r.ProtectionLost)
            {
                BeginDown(p, PetChangeCause.ProtectionLost);
                continue;
            }
            if (r.ControlLost is { } lost)
            {
                // the broker ended a lease of ours: the body is no longer held as a pet; it is retired (never left wild)
                PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: pet {p.Id}: {lost}; its body is retired");
                BeginDown(p, PetChangeCause.Control);
                continue;
            }
            if (r.Lethal)
            {
                BeginDown(p, PetChangeCause.NativeLethal);
                continue;
            }
            if (double.IsFinite(r.Position.X) && double.IsFinite(r.Position.Y) && double.IsFinite(r.Position.Z)) p.LastSafe = r.Position;
            if (r.HealthMax > 0 && (r.Health != p.Health || r.HealthMax != p.HealthMax))
                Mutate(p, Guid.Empty, PetChangeCause.Stats, s =>
                {
                    s.Health = r.Health;
                    s.HealthMax = r.HealthMax;
                    s.LastHealthMax = r.HealthMax;
                    s.HealthFraction = Math.Clamp((double)r.Health / r.HealthMax, 0, 1);
                }, c => new PetStatsChangedEvent(c));
        }
    }

    /// <summary>A pet goes down: the mods choose what it becomes, its producers are revoked and its combat body retired;
    /// only once the body is gone does the pet settle and PetDownedEvent go out.</summary>
    private void BeginDown(PetState p, PetChangeCause cause)
    {
        if (p.Running is { } op && op.Kind is not (Kind.Dismiss or Kind.Release))
            Complete(op, false, $"BodyDowned: the pet went down ({cause})", p);
        CancelChannel(p, cause);
        var def = p.Snapshot ?? PetsRuntime.Definition(p.Definition);
        var outcome = def?.Downed.Outcome ?? PetDownedOutcome.RecoveryAnchor;
        try { outcome = AskDowning(new PetDowningEvent(p.View(_epoch), cause, outcome)).Outcome; }
        catch (Exception e) { PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: a PetDowningEvent handler threw: {e.Message}"); }
        if (!Enum.IsDefined(outcome)) outcome = PetDownedOutcome.RecoveryAnchor;
        p.DownOutcome = outcome;
        p.DownCause = cause;
        var captured = p.PendingDown is not null;   // the final save staging already wrote it Downed with its revision
        p.PendingDown = null;
        if (p.Body is not null && cause != PetChangeCause.BodyLost)
        {
            if (captured) { p.Transition = PetTransition.Downing; p.Presence = PetPresence.Retiring; }
            else Mutate(p, Guid.Empty, cause, s => { s.Transition = PetTransition.Downing; s.Presence = PetPresence.Retiring; }, c => new PetTransitionChangedEvent(c));
            p.RetireTries = 0;
            p.DownCaptured = captured;
            ContinueDown(p);
            return;
        }
        p.DownCaptured = captured;
        SettleDown(p);
    }

    private void ContinueDown(PetState p)
    {
        if (p.Body is not null && !_world.Retire(p.Body, p.DownCause ?? PetChangeCause.NativeLethal))
        {
            // the guard keeps it protected and its outgoing hits denied; retried every step, never resumed as alive
            if (++p.RetireTries % 60 == 1) PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: pet {p.Id}: its body {p.Body} could not be retired yet; retrying (it stays protected)");
            return;
        }
        SettleDown(p);
    }

    private void SettleDown(PetState p)
    {
        var cause = p.DownCause ?? PetChangeCause.NativeLethal;
        var outcome = p.DownOutcome ?? PetDownedOutcome.RecoveryAnchor;
        var hadBody = p.Body is not null || cause == PetChangeCause.BodyLost;
        var anchor = p.LastSafe ?? p.Anchor;
        var events = new List<Func<PetChange, PetChangeEvent>>();
        if (cause == PetChangeCause.ProtectionLost) events.Add(c => new PetProtectionLostEvent(c));
        if (cause == PetChangeCause.BodyLost) events.Add(c => new PetBodyLostEvent(c));
        else if (hadBody) events.Add(c => new PetBodyRetiredEvent(c));
        events.Add(c => new PetDownedEvent(c));
        if (outcome == PetDownedOutcome.Release) events.Add(c => new PetReleasedEvent(c));
        Mutate(p, Guid.Empty, cause, s =>
        {
            s.Body = null;
            s.Life = outcome == PetDownedOutcome.Release ? PetLife.Released : PetLife.Downed;
            s.Health = 0;
            s.HealthFraction = 0;
            s.Transition = PetTransition.None;
            s.Order = PetOrder.Follow;
            s.Target = EntityRef.None;
            s.Snapshot = null;
            DropSession(s);
            s.Anchor = anchor;
            s.AnchorWorld = PetsRuntime.WorldId;
            s.Presence = outcome == PetDownedOutcome.RecoveryAnchor && anchor is not null ? PetPresence.RecoveryAnchor : PetPresence.Absent;
            if (outcome == PetDownedOutcome.Release) s.WantsSummoned = false;
        }, bump: !p.DownCaptured, events.ToArray());
        p.DownOutcome = null;
        p.DownCause = null;
        p.DownCaptured = false;
    }

    // ---- movement (package 4) ----

    /// <summary>The engine side of followers; null: bodies are not moved (no follower in this process).</summary>
    internal IPetMover? Mover { get; set; }

    private void DropSession(PetState p)
    {
        if (p.Session is PetFollower f && Mover is not null) f.Forget(Mover);
        p.Session = null;
    }

    /// <summary>Every live pet's follower, after the bodies were read and before requests: follow, stay, attack.</summary>
    private void Move()
    {
        if (Mover is null) return;
        foreach (var p in _pets.Values.ToList())
        {
            if (p.Body is null || p.Presence != PetPresence.Live || p.Transition != PetTransition.None || p.Life != PetLife.Ready) continue;
            if (_world.OnlineOwner(p.Owner) is not { } owner) continue;
            var def = p.Snapshot ?? PetsRuntime.Definition(p.Definition);
            if (def is null) continue;
            var f = p.Session as PetFollower;
            if (f is null) p.Session = f = new PetFollower();
            try { f.Step(p, def, owner, Mover, _world.Now); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: pet {p.Id}'s follower: {e.Message}");
            }
            if (f.TargetEnded && p.Order == PetOrder.Attack)
            {
                // the target is gone or dead: back to following (the broker resumes the base lease under the combat one)
                _world.Order(p.Body, PetOrder.Follow, EntityRef.None, out _);
                Mutate(p, Guid.Empty, PetChangeCause.Control, s => { s.Order = PetOrder.Follow; s.Target = EntityRef.None; }, c => new PetCommandChangedEvent(c));
            }
        }
    }

    // ---- running requests ----

    private void AdvanceRunning()
    {
        foreach (var p in _pets.Values.ToList())
            if (p.Running is { } op && !op.Source.Task.IsCompleted) Advance(op, p);
    }

    private void StartQueued()
    {
        var batch = _queue.ToList();
        _queue.Clear();
        foreach (var op in batch) Start(op);
    }

    private void Start(Op op)
    {
        if (op.Kind == Kind.Grant)
        {
            Grant(op);
            return;
        }
        if (!_pets.TryGetValue(op.Pet, out var p))
        {
            EnsureLoaded(op.OwnerKey);
            if (!_pets.TryGetValue(op.Pet, out p))
            {
                Complete(op, false, "NotFound: no such pet", null);
                return;
            }
        }
        if (p.Owner != op.OwnerKey)
        {
            Complete(op, false, "NotOwner: the pet belongs to another character", null);
            return;
        }
        if (p.Life == PetLife.Released)
        {
            Complete(op, false, "AlreadyReleased: the pet was released", p);
            return;
        }
        if (op.Expected != 0 && op.Expected != p.StateRevision)
        {
            Complete(op, false, $"StaleRevision: the pet is at revision {p.StateRevision}, the request was made at {op.Expected}", p);
            return;
        }
        if (op.Kind == Kind.CancelRevive)
        {
            CancelRevive(op, p);
            return;
        }
        if (p.Running is not null || p.Transition != PetTransition.None || p.PendingDown is not null)
        {
            if (op.Internal) _queue.Add(op);   // a login summon waits its turn
            else Complete(op, false, $"Busy: the pet is {(p.Transition != PetTransition.None ? p.Transition.ToString() : "busy")}", p);
            return;
        }
        if (op.Kind != Kind.Grant && op.Kind != Kind.Release && op.Kind != Kind.Dismiss)
        {
            var owner = op.Internal ? _world.OnlineOwner(op.OwnerKey) : _world.Owner(op.Who, out var why) ?? Refuse(op, p, why);
            if (owner is null)
            {
                if (op.Internal) Complete(op, false, "OwnerLeft", p);
                return;
            }
            op.Owner = owner.Value;
        }
        p.Running = op;
        switch (op.Kind)
        {
            case Kind.Summon or Kind.LoginSummon: Summon(op, p); break;
            case Kind.Dismiss: Dismiss(op, p); break;
            case Kind.Release: Release(op, p); break;
            case Kind.Command: Command(op, p); break;
            case Kind.Revive: StartRevive(op, p); break;
            default: Complete(op, false, "Unsupported", p); break;
        }
    }

    private PetOwner? Refuse(Op op, PetState p, string? why)
    {
        Complete(op, false, why ?? "OwnerOffline: the owner is not in game", p);
        return null;
    }

    private void Advance(Op op, PetState p)
    {
        switch (op.Kind)
        {
            case Kind.Summon or Kind.LoginSummon: AdvanceSummon(op, p); break;
            case Kind.Dismiss or Kind.Release: AdvanceRetire(op, p); break;
            case Kind.Revive: AdvanceRevive(op, p); break;
        }
    }

    // ---- grant ----

    private void Grant(Op op)
    {
        var def = op.Definition is { } id ? PetsRuntime.Definition(id) : null;
        if (def is null)
        {
            Complete(op, false, $"DefinitionUnknown: no pet definition {op.Definition}", null);
            return;
        }
        var o = EnsureLoaded(op.OwnerKey);
        if (o is null)
        {
            Complete(op, false, _store.Available && _epoch != 0 ? "NoCharacter: the owner has no character id" : "RecordsUnavailable: no world records here", null);
            return;
        }
        if (o.Unread is not null)
        {
            Complete(op, false, "RecordsUnreadable: this character's pet records are of a newer Kiln or do not read; they are kept as they are", null);
            return;
        }
        var alive = o.Pets.Where(x => x.Life != PetLife.Released).ToList();
        if (alive.Count(x => x.Definition == def.Id) >= def.Caps.RetainedPerCharacter)
        {
            Complete(op, false, $"Capacity: the character keeps {def.Caps.RetainedPerCharacter} pets of {def.Id} already", null);
            return;
        }
        if (o.Pets.Count >= MaxRecordsPerCharacter)
        {
            Complete(op, false, $"Capacity: a character keeps at most {MaxRecordsPerCharacter} pet records", null);
            return;
        }
        var level = def.Level.Level;
        if (def.Level.Mode == PetLevelMode.OwnerAtTame)
        {
            if (_world.OnlineOwner(op.OwnerKey) is not { Level: > 0 } owner)
            {
                Complete(op, false, "LevelUnavailable: the owner's level cannot be read (is the owner in game?)", null);
                return;
            }
            level = owner.Level;
        }
        var p = new PetState { Id = new PetId(Guid.NewGuid()), Owner = op.OwnerKey, Definition = def.Id, Life = PetLife.Ready, Level = Math.Clamp(level, 1, PetDefinitionRules.MaxLevel), Presence = PetPresence.Absent };
        var before = p.View(_epoch);
        o.Pets.Add(p);
        _pets[p.Id] = p;
        Mutate(p, op.OperationId, PetChangeCause.Request, _ => { }, before, c => new PetGrantedEvent(c));
        Complete(op, true, null, p);
    }

    // ---- summon ----

    private int LiveOf(CharacterKey owner) => _pets.Values.Count(x => x.Owner == owner && (x.Body is not null || x.Transition == PetTransition.Spawning));
    private int LiveOfDefinition(string def) => _pets.Values.Count(x => x.Definition == def && (x.Body is not null || x.Transition == PetTransition.Spawning));

    private void Summon(Op op, PetState p)
    {
        if (p.Life == PetLife.Downed)
        {
            SummonAnchor(op, p);
            return;
        }
        if (p.Body is not null)
        {
            Complete(op, true, null, p);   // idempotent: no health reset, no event
            return;
        }
        if (!_world.CanMakeBodies(out var off))
        {
            Complete(op, false, off, p);
            return;
        }
        var def = PetsRuntime.Definition(p.Definition);
        if (def is null)
        {
            Complete(op, false, $"DefinitionUnknown: {p.Definition} is not defined in this session (the record is kept)", p);
            return;
        }
        var creature = PetCreatures.Resolve(def.Creature, out var why);
        if (creature is null || creature.Problem is not null)
        {
            Complete(op, false, creature?.Problem ?? why, p);
            return;
        }
        if (LiveOf(p.Owner) >= def.Caps.LivePerCharacter)
        {
            Complete(op, false, $"Capacity: {def.Caps.LivePerCharacter} live pet(s) per character for {def.Id}", p);
            return;
        }
        if (LiveOfDefinition(def.Id) >= def.Caps.LivePerWorld)
        {
            Complete(op, false, $"Capacity: {def.Caps.LivePerWorld} live pets of {def.Id} in the world", p);
            return;
        }
        if (LiveBodies >= MaxBodies)
        {
            Complete(op, false, $"Capacity: {MaxBodies} pet bodies in the world", p);
            return;
        }
        var level = def.Level.Mode switch
        {
            PetLevelMode.Fixed => def.Level.Level,
            PetLevelMode.OwnerAtSummon => op.Owner.Level,
            _ => p.Level,
        };
        if (level <= 0)
        {
            Complete(op, false, "LevelUnavailable: the owner's level cannot be read", p);
            return;
        }
        level = Math.Clamp(level, 1, creature.LevelMax > 0 ? Math.Min(creature.LevelMax, PetDefinitionRules.MaxLevel) : PetDefinitionRules.MaxLevel);
        var at = _world.SafeGround(op.Owner.Position, 2, 4, def, creature);
        if (at is null)
        {
            Complete(op, false, "NoSafeGround: no safe ground 2 to 4 m from the owner", p);
            return;
        }
        p.Snapshot = def;
        Mutate(p, op.OperationId, PetChangeCause.Request, s => s.Transition = PetTransition.Spawning, c => new PetTransitionChangedEvent(c));
        var body = _world.CreateBody(new PetBodyRequest
        {
            Pet = p.Id, Owner = p.Owner, OwnerSession = op.Owner.Session, Definition = def, Creature = creature, Position = at.Value, Level = level,
            HealthFraction = Math.Max(p.HealthFraction, 1e-9),
        }, out var refused);
        if (body is null)
        {
            p.Snapshot = null;
            Mutate(p, op.OperationId, PetChangeCause.Request, s => s.Transition = PetTransition.None, c => new PetTransitionChangedEvent(c));
            Complete(op, false, refused ?? "SpawnRefused", p);
            return;
        }
        op.Body = body;
        op.Level = level;
        op.Since = _world.Now;
        op.Phase = 1;
        AdvanceSummon(op, p);
    }

    private void AdvanceSummon(Op op, PetState p)
    {
        if (op.Phase != 1 || op.Body is not { } body) return;
        var poll = _world.Poll(body, op.Since, out var why);
        if (poll == PetBodyPoll.Pending) return;
        if (poll == PetBodyPoll.Ready && !_world.Activate(body, out why)) poll = PetBodyPoll.Failed;
        if (poll == PetBodyPoll.Failed)
        {
            // the staging body never was the pet: it goes, and the record is as it was (no health, order or wish changed)
            _world.Retire(body, PetChangeCause.Request);
            p.Snapshot = null;
            Mutate(p, op.OperationId, PetChangeCause.Request, s => s.Transition = PetTransition.None, c => new PetTransitionChangedEvent(c));
            Complete(op, false, why ?? "BodyNotReady", p);
            return;
        }
        var r = _world.Read(body);
        var resetOrder = p.Order != PetOrder.Follow || !p.Target.IsNone;
        var events = new List<Func<PetChange, PetChangeEvent>>();
        if (resetOrder) events.Add(c => new PetCommandChangedEvent(c));
        events.Add(c => new PetSummonedEvent(c));
        Mutate(p, op.Internal ? Guid.Empty : op.OperationId, op.Internal ? PetChangeCause.Login : PetChangeCause.Request, s =>
        {
            s.Body = body;
            s.Presence = PetPresence.Live;
            s.Transition = PetTransition.None;
            s.WantsSummoned = true;
            s.Order = PetOrder.Follow;
            s.Target = EntityRef.None;
            s.Level = op.Level;
            if (r.HealthMax > 0)
            {
                s.Health = r.Health;
                s.HealthMax = r.HealthMax;
                s.LastHealthMax = r.HealthMax;
            }
            s.LastSafe = r.Position;
        }, events.ToArray());
        op.Phase = 2;
        BodyActivated?.Invoke(p);
        Complete(op, true, null, p);
    }

    /// <summary>A pet's body became live (package 3/4 start its lease and follower).</summary>
    internal event Action<PetState>? BodyActivated;

    private void SummonAnchor(Op op, PetState p)
    {
        var def = PetsRuntime.Definition(p.Definition);
        if (p.Presence == PetPresence.RecoveryAnchor && p.Anchor is { } at && at.DistanceTo(op.Owner.Position) <= AnchorMeters)
        {
            Complete(op, true, null, p);   // idempotent
            return;
        }
        var spot = def is null ? (WorldPosition?)null : _world.SafeGround(op.Owner.Position, 0, AnchorMeters, def, null);
        if (spot is null)
        {
            Complete(op, false, def is null ? $"DefinitionUnknown: {p.Definition}" : "NoSafeGround: no safe ground within 3 m of the owner", p);
            return;
        }
        var events = new List<Func<PetChange, PetChangeEvent>> { c => new PetRecoveryAnchorChangedEvent(c) };
        if (p.Presence != PetPresence.RecoveryAnchor || !p.WantsSummoned) events.Add(c => new PetSummonedEvent(c));
        Mutate(p, op.Internal ? Guid.Empty : op.OperationId, op.Internal ? PetChangeCause.Login : PetChangeCause.Request, s =>
        {
            s.Anchor = spot;
            s.AnchorWorld = PetsRuntime.WorldId;
            s.Presence = PetPresence.RecoveryAnchor;
            s.WantsSummoned = true;
        }, events.ToArray());
        Complete(op, true, null, p);
    }

    // ---- dismiss and release ----

    private void Dismiss(Op op, PetState p)
    {
        if (p.Body is not null)
        {
            ReadInto(p);
            Mutate(p, op.OperationId, PetChangeCause.Request, s => { s.Transition = PetTransition.Dismissing; s.Presence = PetPresence.Retiring; }, c => new PetTransitionChangedEvent(c));
            op.Phase = 1;
            p.RetireTries = 0;
            AdvanceRetire(op, p);
            return;
        }
        if (p.Life == PetLife.Downed)
        {
            if (p.Presence == PetPresence.Absent && !p.WantsSummoned)
            {
                Complete(op, true, null, p);
                return;
            }
            var hid = p.Presence == PetPresence.RecoveryAnchor;
            var events = new List<Func<PetChange, PetChangeEvent>>();
            if (hid) events.Add(c => new PetRecoveryAnchorChangedEvent(c));
            events.Add(c => new PetDismissedEvent(c));
            Mutate(p, op.OperationId, PetChangeCause.Request, s => { s.Presence = PetPresence.Absent; s.WantsSummoned = false; }, events.ToArray());
            Complete(op, true, null, p);
            return;
        }
        if (p.WantsSummoned) Mutate(p, op.OperationId, PetChangeCause.Request, s => s.WantsSummoned = false, c => new PetDismissedEvent(c));
        Complete(op, true, null, p);
    }

    private void Release(Op op, PetState p)
    {
        if (p.Body is not null)
        {
            ReadInto(p);
            Mutate(p, op.OperationId, PetChangeCause.Request, s => { s.Transition = PetTransition.Releasing; s.Presence = PetPresence.Retiring; }, c => new PetTransitionChangedEvent(c));
            op.Phase = 1;
            p.RetireTries = 0;
            AdvanceRetire(op, p);
            return;
        }
        CancelChannel(p, PetChangeCause.Request);
        Mutate(p, op.OperationId, PetChangeCause.Request, s =>
        {
            s.Life = PetLife.Released;
            s.Presence = PetPresence.Absent;
            s.WantsSummoned = false;
            s.Target = EntityRef.None;
        }, c => new PetReleasedEvent(c));
        Complete(op, true, null, p);
    }

    private void AdvanceRetire(Op op, PetState p)
    {
        if (op.Phase != 1 || p.Body is null) return;
        if (!_world.Retire(p.Body, PetChangeCause.Request))
        {
            if (++p.RetireTries < RetireRetries) return;
            // the body stays with its protection and its record; the request reports it
            Mutate(p, op.OperationId, PetChangeCause.Request, s => { s.Transition = PetTransition.None; s.Presence = PetPresence.Live; }, c => new PetTransitionChangedEvent(c));
            Complete(op, false, "RetireFailed: the body could not be removed; it stays protected and the record is kept", p);
            return;
        }
        var release = op.Kind == Kind.Release;
        Mutate(p, op.OperationId, PetChangeCause.Request, s =>
        {
            s.Body = null;
            s.Transition = PetTransition.None;
            s.Presence = PetPresence.Absent;
            s.WantsSummoned = false;
            s.Target = EntityRef.None;
            s.Snapshot = null;
            DropSession(s);
            if (release) s.Life = PetLife.Released;
        }, c => new PetBodyRetiredEvent(c), release ? c => new PetReleasedEvent(c) : c => new PetDismissedEvent(c));
        Complete(op, true, null, p);
    }

    // ---- orders ----

    private void Command(Op op, PetState p)
    {
        if (p.Life != PetLife.Ready || p.Body is null)
        {
            Complete(op, false, "NotSummoned: the pet has no body to order", p);
            return;
        }
        if (op.Order == PetOrder.Attack && TargetCheck(p, op.Target) is { } protectedWhy)
        {
            Complete(op, false, protectedWhy, p);
            return;
        }
        var target = op.Order == PetOrder.Attack ? op.Target : EntityRef.None;
        if (p.Order == op.Order && p.Target == target)
        {
            Complete(op, true, null, p);
            return;
        }
        if (!_world.Order(p.Body, op.Order, target, out var refused))
        {
            Complete(op, false, refused ?? "ControlRefused", p);
            return;
        }
        Mutate(p, op.OperationId, PetChangeCause.Request, s => { s.Order = op.Order; s.Target = target; }, c => new PetCommandChangedEvent(c));
        OrderChanged?.Invoke(p);
        Complete(op, true, null, p);
    }

    /// <summary>A live pet's order changed (package 3/4 move its lease and follower).</summary>
    internal event Action<PetState>? OrderChanged;

    // ---- changes ----

    private void Mutate(PetState p, Guid operation, PetChangeCause cause, Action<PetState> change, params Func<PetChange, PetChangeEvent>[] events) =>
        Mutate(p, operation, cause, change, p.View(_epoch), events);

    private void Mutate(PetState p, Guid operation, PetChangeCause cause, Action<PetState> change, bool bump, params Func<PetChange, PetChangeEvent>[] events)
    {
        var before = p.View(_epoch);
        change(p);
        if (bump) p.StateRevision++;
        Changed(p, operation, cause, before, events);
    }

    private void Mutate(PetState p, Guid operation, PetChangeCause cause, Action<PetState> change, PetSnapshot before, params Func<PetChange, PetChangeEvent>[] events)
    {
        change(p);
        p.StateRevision++;
        Changed(p, operation, cause, before, events);
    }

    private void Changed(PetState p, Guid operation, PetChangeCause cause, PetSnapshot before, Func<PetChange, PetChangeEvent>[] events)
    {
        if (_owners.TryGetValue(p.Owner, out var o)) o.Dirty = true;
        var after = p.View(_epoch);
        foreach (var make in events)
            _events.Add(make(new PetChange { EventId = Guid.NewGuid(), OperationId = operation, ObservedStep = _world.Step, Before = before, After = after, Cause = cause }));
        Changes?.Invoke(p);
    }

    /// <summary>Any change of a pet (the owners' view publisher marks its owner dirty).</summary>
    internal event Action<PetState>? Changes;

    // ---- the save ----

    /// <summary>A staging (tick thread): dirty characters' documents are written. The final snapshot staging first re-reads
    /// every live body (19d §4.7): a lethal reading or a missing body is written Downed now, so a save in the lethal step
    /// never keeps it Ready; its retirement and events follow at the next step with the revision written here.</summary>
    private void OnStaging(ServiceCharacters.StagingInfo info)
    {
        if (info.Session != _saveSession)
        {
            _saveSession = info.Session;
            _receipts.Clear();
        }
        if (info.Snapshot)
            foreach (var p in _pets.Values)
            {
                if (p.Body is null || p.Transition is PetTransition.Spawning || p.Presence != PetPresence.Live || p.PendingDown is not null) continue;
                var r = _world.Read(p.Body);
                if (!r.Exists || r.Lethal || r.ProtectionLost)
                {
                    p.Life = PetLife.Downed;
                    p.HealthFraction = 0;
                    p.Health = 0;
                    p.StateRevision++;
                    p.PendingDown = !r.Exists ? PetChangeCause.BodyLost : r.ProtectionLost ? PetChangeCause.ProtectionLost : PetChangeCause.NativeLethal;
                    if (_owners.TryGetValue(p.Owner, out var dirty)) dirty.Dirty = true;
                }
                else if (r.HealthMax > 0 && (r.Health != p.Health || r.HealthMax != p.HealthMax))
                {
                    p.Health = r.Health;
                    p.HealthMax = r.HealthMax;
                    p.LastHealthMax = r.HealthMax;
                    p.HealthFraction = Math.Clamp((double)r.Health / r.HealthMax, 0, 1);
                    p.StateRevision++;
                    if (_owners.TryGetValue(p.Owner, out var dirty)) dirty.Dirty = true;
                }
            }
        foreach (var o in _owners.Values)
        {
            if (!o.Dirty || o.Unread is not null) continue;
            if (o.Pets.Count == 0) _store.Delete(o.Key);
            else _store.Write(o.Key, PetRecordJson.Write(new PetRecordDoc(o.Key, o.Pets.Select(x => x.Record()).ToList())));
            o.Dirty = false;
        }
    }

    private void OnStaged(ServiceCharacters.Receipt receipt)
    {
        if (receipt.Service != PetsRuntime.ServiceName) return;
        if (receipt.Session != _saveSession)
        {
            _saveSession = receipt.Session;
            _receipts.Clear();
        }
        if (!_receipts.TryGetValue(receipt.Generation, out var gen)) _receipts[receipt.Generation] = gen = [];
        // a snapshot staging replaces the request staging's receipt of the same generation
        gen[receipt.Character] = (receipt.Sha256, PetRecordJson.Revisions(receipt.Bytes));
    }

    /// <summary>The game committed a generation: exactly the revisions its staged bytes hold become durable, and released
    /// tombstones it holds go (the next staging drops them).</summary>
    private void OnCommitted(ServiceCharacters.CommitInfo info)
    {
        if (info.Session != _saveSession || !_receipts.TryGetValue(info.Generation, out var gen)) return;
        foreach (var (key, (sha, revisions)) in gen)
        {
            if (!_owners.TryGetValue(key, out var o)) continue;
            foreach (var (id, (rev, life)) in revisions)
            {
                if (!_pets.TryGetValue(id, out var p) || p.Owner != key) continue;
                if (rev > p.DurableRevision || p.LastCommit is null)
                {
                    var previous = p.DurableRevision;
                    p.DurableRevision = Math.Max(p.DurableRevision, rev);
                    p.LastCommit = new PetCommit { WorldEpoch = (ulong)info.Session, WorldRevision = info.Generation, WorldUnix = info.WorldUnix, StateRevision = rev, BundleHash = sha };
                    _events.Add(new PetRecordCommittedEvent(id, key, rev, previous, p.LastCommit));
                }
                if (life == PetLife.Released && p.Life == PetLife.Released && p.StateRevision == rev && p.Running is null)
                {
                    o.Pets.Remove(p);
                    _pets.Remove(id);
                    o.Dirty = true;
                }
            }
        }
        foreach (var g in _receipts.Keys.Where(g => g <= info.Generation).ToList()) _receipts.Remove(g);
        Flush();
    }
}
