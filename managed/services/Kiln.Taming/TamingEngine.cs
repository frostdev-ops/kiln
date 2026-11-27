// SPDX-License-Identifier: MIT
// The attempt machine of dossier 19e section 4: one claimant per target, server challenges and request ids with a replay
// cache, exact-cost steps through kiln.item_costs (ItemCosts), automatic finalization with a cost-free retry, before- and
// after-events in 19e's order, timeouts in simulation time, and the definition's refund policy at the end. The world, the
// costs and the binding are seams (GameTamingWorld, GameTamingCosts, PetsTamingBinder), so the tests drive it with fakes
// and a clock of their own. Tick thread only.
namespace Kiln;

/// <summary>The owner of an attempt as the world has them now.</summary>
internal readonly record struct TamingOwner(PlayerRef Player, EntityRef Entity, ulong Session, WorldPosition Position);

/// <summary>A target as the world has it now.</summary>
internal readonly record struct TamingTarget(bool Exists, bool Dead, WorldPosition Position, double HealthFraction, string CreatureKey, int NativeProgress, bool NativeTameable)
{
    /// <summary>Its template (a creature block's id resolves to it).</summary>
    public Guid Template { get; init; }
}

internal interface ITamingWorld
{
    double Now { get; }
    ulong WorldEpoch { get; }
    TamingOwner? Owner(PlayerRef player, out string? reason);
    TamingTarget? Target(EntityRef target);
}

internal interface ITamingCosts
{
    Task<CostReceipt> Take(ItemSelection selection, int count, Guid requestId);
    Task<CostReceipt> Wear(ItemSelection selection, int wear, Guid requestId, bool allowBreak);
    Task<CostReceipt> Refund(CostReceipt paid, Guid requestId);
    IReadOnlyList<InventorySlot> Slots(EntityRef owner);
}

internal sealed record TamingBindRequest(TameAttemptId Attempt, PlayerRef Owner, EntityRef Target, TamingDefinition Definition, Guid RequestId);

internal readonly record struct TamingBindResult(bool Ok, PetSnapshot? Pet, string? Reason, bool Transient);

/// <summary>What a tame binds to (PetsTamingBinder: a Kiln.Pets record for a definition that names a pet; nothing for one
/// that does not, where the tame is the attempt's own result and the mod's handlers decide what it means).</summary>
internal interface ITamingBinder
{
    /// <summary>At Begin, after the before-event: reserve what the bind needs (a roster slot). Null: reserved.</summary>
    string? Reserve(TamingBindRequest request);
    /// <summary>Before the last step's cost and before a retry: null when a bind would go through now; else why, and
    /// whether it may change (transient) or not.</summary>
    (string Reason, bool Transient)? Preflight(TamingBindRequest request);
    /// <summary>The irreversible bind (it may settle at a later step: a pet record is made at the roster's next step).</summary>
    Task<TamingBindResult> Bind(TamingBindRequest request);
    /// <summary>The bind succeeded and the attempt is Committed (tick thread, before the after-events): what follows a
    /// tame (the wild body goes, the pet comes out). It never undoes the bind.</summary>
    void Committed(TamingBindRequest request, TamingBindResult result);
    /// <summary>An attempt ended (with or without a bind): its reservations go.</summary>
    void Release(TamingBindRequest request);
}

internal sealed class NoBinder : ITamingBinder
{
    public string? Reserve(TamingBindRequest request) => null;
    public (string Reason, bool Transient)? Preflight(TamingBindRequest request) => null;
    public Task<TamingBindResult> Bind(TamingBindRequest request) => Task.FromResult(new TamingBindResult(true, null, null, false));
    public void Committed(TamingBindRequest request, TamingBindResult result) { }
    public void Release(TamingBindRequest request) { }
}

internal sealed class TamingEngine
{
    // 19e 4.2 quotas (Kiln's policy, not game capacities)
    internal const int MaxRequestsPerSecond = 10, MaxReceiptsPerAttempt = 256, MaxStepsPerDefinition = 64, MaxActive = 512, MaxTombstones = 65536;
    internal const double ClosedPayloadSeconds = 600;

    private readonly ITamingWorld _world;
    private readonly ITamingCosts _costs;
    internal ITamingBinder Binder = new NoBinder();
    internal Func<CancelableEvent, CancelableEvent> Ask = ev => Events.Post(ev);
    internal Action<KilnEvent> Tell = ev => Events.Post(ev);
    internal Func<Guid> NewId = Guid.NewGuid;
    /// <summary>The template a creatures registry id or a creature block's id names (null: none known).</summary>
    internal Func<string, Guid?> ResolveCreature = _ => null;

    private readonly Dictionary<string, (TamingDefinition Def, string Mod)> _defs = new(StringComparer.Ordinal);
    private readonly Dictionary<TameAttemptId, Attempt> _attempts = new();
    private readonly Dictionary<EntityId, TameAttemptId> _claims = new();
    private readonly Dictionary<(PlayerRef, Guid), (string Fingerprint, TamingResult Result)> _begins = new();
    private readonly Queue<(PlayerRef, Guid)> _beginOrder = new();
    private readonly HashSet<TameAttemptId> _tombstones = new();
    private readonly Queue<TameAttemptId> _tombOrder = new();
    private readonly Dictionary<PlayerRef, Queue<double>> _rate = new();
    private ulong _epoch;

    internal TamingEngine(ITamingWorld world, ITamingCosts costs)
    {
        _world = world;
        _costs = costs;
        _epoch = world.WorldEpoch;
    }

    private sealed class Attempt
    {
        public TameAttemptId Id;
        public PlayerRef Owner;
        public EntityRef OwnerEntity;
        public ulong Session, Epoch;
        public EntityRef Target;
        public TamingDefinition Def = null!;
        public TamingState State;
        public int Completed;
        public ulong Revision;
        public Guid Challenge;
        public double Started, LastProgress, ClosedAt = -1;
        public readonly List<CostReceipt> Spent = new();
        public readonly List<TamingRefund> Refunds = new();
        public double? Threshold;
        public int NativeBaseline;
        public readonly Dictionary<Guid, (string Fingerprint, Task<TamingResult> Task)> Replay = new();
        public bool CancelRequested;
        public Guid CancelRequest;
        public TaskCompletionSource<TamingResult>? CancelTcs;
        public string? FailAfterSettle;          // a failure seen while a cost or a bind was in flight: applied when it settles
        public string? ExpireAfterSettle;        // a timeout seen meanwhile: checked again when it settles
        // the step in flight
        public Task<CostReceipt>? Cost;
        public TaskCompletionSource<TamingResult>? StepTcs;
        public TamingToken? StepToken;
        public Guid StepRequest;
        public ItemSelection? StepSelection;
        public TamingProgress? StepBefore;
        // the bind in flight (the last step paid, or a cost-free retry)
        public Task<TamingBindResult>? Binding;
        public TaskCompletionSource<TamingResult>? BindTcs;
        public TamingProgress? BindBefore;
        public IReadOnlyList<CostReceipt> BindReceipts = [];
        public Guid BindRequest;
        public bool BindRetry;
        /// <summary>A before-event of this attempt runs (19e 4.4's transient reentrancy guard): a cancel or a failure asked
        /// meanwhile (a mod's handler calling Taming.Cancel) waits for it, as for a cost in flight, and then ends the attempt
        /// with nothing new spent.</summary>
        public bool InBefore;
        public bool Settling => Cost is not null || Binding is not null || InBefore;
        // refunds in flight at the end
        public List<(CostReceipt Paid, Task<CostReceipt> Refund)>? RefundsPending;
        public Action? AfterRefunds;
        public TamingBindRequest Bind(Guid request) => new(Id, Owner, Target, Def, request);
    }

    // ------------------------------------------------------------------------------------------- definitions ------

    internal static void Validate(TamingDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrWhiteSpace(d.Id) || d.Id.Length > 128 || d.Id != d.Id.ToLowerInvariant() || !d.Id.Contains(':'))
            throw new ArgumentException($"\"{d.Id}\": a definition id is lower case with a mod prefix (my-mod:wolf), at most 128 characters", nameof(d));
        if (d.Steps is null || d.Steps.Count is < 1 or > MaxStepsPerDefinition) throw new ArgumentException("1 to 64 steps", nameof(d));
        for (var i = 0; i < d.Steps.Count; i++)
        {
            var where = $"steps[{i}]";
            switch (d.Steps[i])
            {
                case FeedStep f when f.Item.IsNone || f.Count is < 1 or > 999:
                    throw new ArgumentException($"{where}: a feed step names an item and 1 to 999 of it", nameof(d));
                case WeakenStep w when !(w.MaxHealthFraction > 0 && w.MaxHealthFraction <= 1):
                    throw new ArgumentException($"{where}: a weaken threshold is in (0, 1]", nameof(d));
                case UseToolStep t when t.Item.IsNone || t.Wear is < 0 or > ItemCosts.MaxWear:
                    throw new ArgumentException($"{where}: a tool step names an item and 0 to 32767 wear", nameof(d));
                case NativeStep n when n.Steps is < 1 or > 255:
                    throw new ArgumentException($"{where}: a native step counts 1 to 255 of the game's steps", nameof(d));
                case null:
                    throw new ArgumentException($"{where}: no step", nameof(d));
                case FeedStep or WeakenStep or UseToolStep or NativeStep:
                    break;
                default:
                    throw new ArgumentException($"{where}: unknown step {d.Steps[i].GetType().Name}", nameof(d));
            }
        }
        if (!(d.RangeMeters >= 1 && d.RangeMeters <= 8)) throw new ArgumentException("rangeMeters is 1 to 8", nameof(d));
        if (!(d.InactivitySeconds >= 1 && d.InactivitySeconds <= 300)) throw new ArgumentException("inactivitySeconds is 1 to 300", nameof(d));
        if (!(d.MaxAttemptSeconds >= d.InactivitySeconds && d.MaxAttemptSeconds <= 3600))
            throw new ArgumentException("maxAttemptSeconds is at least inactivitySeconds and at most 3600", nameof(d));
        if (!Enum.IsDefined(d.Refund)) throw new ArgumentException("not a refund policy", nameof(d));
        if (d.Pet is not null && (d.Pet.Length is 0 or > 128 || !d.Pet.Contains(':') || d.Pet != d.Pet.ToLowerInvariant()))
            throw new ArgumentException($"pet \"{d.Pet}\" is not a pet definition id (my-mod:wolf)", nameof(d));
        if (d.Creatures is null || d.Creatures.Count == 0) throw new ArgumentException("a definition names the creatures it tames", nameof(d));
        foreach (var c in d.Creatures)
            if (string.IsNullOrWhiteSpace(c) || !c.Contains(':') || c != c.ToLowerInvariant())
                throw new ArgumentException($"\"{c}\" is not a creatures registry id (game:<name>, my-mod:<name>, lower case; a trailing * for a prefix)", nameof(d));
    }

    internal void Define(TamingDefinition d, string mod)
    {
        Validate(d);
        if (_defs.TryGetValue(d.Id, out var old))
        {
            if (old.Mod != mod) throw new InvalidOperationException($"DefinitionInUse: {d.Id} is mod {old.Mod}'s");
            if (!Same(old.Def, d) && _attempts.Values.Any(a => a.Def.Id == d.Id && !Terminal(a.State)))
                throw new InvalidOperationException($"DefinitionInUse: {d.Id} has attempts running; it changes when they end");
        }
        _defs[d.Id] = (d, mod);
    }

    internal TamingDefinition? Definition(string id) => _defs.TryGetValue(id, out var d) ? d.Def : null;

    internal IReadOnlyList<TamingDefinition> Definitions => _defs.Values.Select(v => v.Def).ToArray();

    /// <summary>The same definition by value (its lists by their items).</summary>
    internal static bool Same(TamingDefinition a, TamingDefinition b) =>
        a with { Steps = b.Steps, Creatures = b.Creatures } == b && a.Steps.SequenceEqual(b.Steps) && a.Creatures.SequenceEqual(b.Creatures);

    /// <summary>A mod unloaded: its definitions go, and their running attempts fail (DefinitionGone).</summary>
    internal void RemoveMod(string mod)
    {
        foreach (var id in _defs.Where(kv => kv.Value.Mod == mod).Select(kv => kv.Key).ToList())
        {
            _defs.Remove(id);
            foreach (var a in _attempts.Values.Where(a => a.Def.Id == id && !Terminal(a.State)).ToList()) Fail(a, "DefinitionGone", Guid.Empty);
        }
    }

    /// <summary>Whether the definition tames the target: an id equal to its creatures registry id, an id that names its
    /// template (a creature block's), or a prefix of its registry id (a trailing *).</summary>
    internal bool Matches(TamingDefinition d, TamingTarget t) =>
        d.Creatures.Any(c => c.EndsWith('*') ? t.CreatureKey.StartsWith(c[..^1], StringComparison.Ordinal)
            : c == t.CreatureKey || t.Template != Guid.Empty && ResolveCreature(c) == t.Template);

    private static bool Terminal(TamingState s) => s is TamingState.Committed or TamingState.Canceled or TamingState.Failed or TamingState.Expired;

    // ------------------------------------------------------------------------------------------- views ------------

    private TamingProgress View(Attempt a) =>
        new(a.Id, a.Owner, a.Epoch, a.Session, a.Target, a.Def.Id, a.State, a.Completed, a.Def.Steps.Count, a.Revision, a.Challenge, a.Started,
            a.LastProgress, Math.Min(a.LastProgress + a.Def.InactivitySeconds, a.Started + a.Def.MaxAttemptSeconds), a.Spent.ToArray())
        {
            Refunds = a.Refunds.ToArray(),
            CancelRequested = a.CancelRequested,
        };

    internal TamingProgress? Inspect(TameAttemptId id) => _attempts.TryGetValue(id, out var a) ? View(a) : null;

    private static TamingResult Refuse(Guid request, string reason, TamingProgress? p = null) => new(false, reason, p, null, request, false, []);

    private static string Fp(params object?[] parts) => string.Join("|", parts.Select(p => p?.ToString() ?? "-"));

    private static string SelectionFp(ItemSelection? s) =>
        s is null ? "none" : Fp(s.InventoryOwner.Id.Value, s.InventoryOwner.Serial, s.Slot.Value, s.ExpectedItem.Value, s.ExpectedCount, s.Instance.Entity.Id.Value, s.Instance.Entity.Serial);

    private bool RateLimited(PlayerRef owner)
    {
        var now = _world.Now;
        if (!_rate.TryGetValue(owner, out var q)) _rate[owner] = q = new Queue<double>();
        while (q.Count > 0 && q.Peek() <= now - 1) q.Dequeue();
        if (q.Count >= MaxRequestsPerSecond) return true;
        q.Enqueue(now);
        return false;
    }

    // ------------------------------------------------------------------------------------------- begin ------------

    internal TamingResult Begin(PlayerRef player, EntityRef target, string definition, Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("an empty request id", nameof(requestId));
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (target.IsNone) throw new ArgumentException("no target", nameof(target));
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        var fp = Fp("begin", target.Id.Value, target.Serial, definition);
        if (_begins.TryGetValue((player, requestId), out var seen))
            return seen.Fingerprint == fp ? seen.Result : Refuse(requestId, "RequestConflict: the request id was used for another begin");
        var r = BeginCore(player, target, definition, requestId);
        if (r.Reason?.StartsWith("TooManyRequests", StringComparison.Ordinal) == true) return r;  // the limit is not this request's answer
        _begins[(player, requestId)] = (fp, r);
        _beginOrder.Enqueue((player, requestId));
        while (_beginOrder.Count > MaxTombstones) _begins.Remove(_beginOrder.Dequeue());
        return r;
    }

    private TamingResult BeginCore(PlayerRef player, EntityRef target, string definition, Guid requestId)
    {
        if (RateLimited(player)) return Refuse(requestId, "TooManyRequests: 10 a second");
        if (!_defs.TryGetValue(definition, out var entry)) return Refuse(requestId, $"DefinitionUnknown: {definition}");
        var def = entry.Def;
        if (_world.Owner(player, out var why) is not { } owner) return Refuse(requestId, why ?? "OwnerOffline: the player is not in game");
        if (_world.Target(target) is not { Exists: true, Dead: false } t) return Refuse(requestId, "BodyGone: the target is not there");
        if (!Matches(def, t)) return Refuse(requestId, $"TemplateMismatch: {t.CreatureKey} is not one {def.Id} tames");
        if (def.Steps.Any(s => s is NativeStep) && !t.NativeTameable) return Refuse(requestId, "TemplateMismatch: a native step needs a tameable wild animal");
        if (owner.Position.DistanceTo(t.Position) > def.RangeMeters) return Refuse(requestId, $"OutOfRange: within {def.RangeMeters} m");
        if (_claims.TryGetValue(target.Id, out var claimant) && _attempts.TryGetValue(claimant, out var other) && !Terminal(other.State))
            return Refuse(requestId, "Busy: another taming of this target runs");
        if (_attempts.Values.Count(a => !Terminal(a.State)) >= MaxActive) return Refuse(requestId, "Capacity: 512 attempts run in this world");
        if (_tombstones.Count >= MaxTombstones) return Refuse(requestId, "Capacity: this world's closed attempts are at their limit");
        var asked = (TamingBeginningEvent)Ask(new TamingBeginningEvent(new TamingBeginContext(requestId, player, target, def.Id, def.Steps.Count)));
        if (asked.Canceled) return Refuse(requestId, $"RefusedByMod: {asked.CanceledBy ?? "a mod"} refused it");
        // a handler may have begun another attempt on this target meanwhile: one claimant per target, the first one wins
        if (_claims.TryGetValue(target.Id, out claimant) && _attempts.TryGetValue(claimant, out other) && !Terminal(other.State))
            return Refuse(requestId, "Busy: another taming of this target began meanwhile");
        if (_attempts.Values.Count(x => !Terminal(x.State)) >= MaxActive) return Refuse(requestId, "Capacity: 512 attempts run in this world");
        var a = new Attempt
        {
            Id = new TameAttemptId(NewId()), Owner = player, OwnerEntity = owner.Entity, Session = owner.Session, Epoch = _world.WorldEpoch, Target = target,
            Def = def, State = TamingState.Active, Revision = 1, Challenge = NewId(), Started = _world.Now, LastProgress = _world.Now, NativeBaseline = Math.Max(0, t.NativeProgress),
        };
        string? refused;
        try { refused = Binder.Reserve(a.Bind(requestId)); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { refused = $"Refused: {e.Message}"; }
        if (refused is not null) return Refuse(requestId, refused);
        _attempts[a.Id] = a;
        _claims[target.Id] = a.Id;
        var p = View(a);
        Tell(new TamingStartedEvent(p, requestId));
        return new TamingResult(true, null, p, null, requestId, false, []);
    }

    // ------------------------------------------------------------------------------------------- advance ----------

    private TamingResult? Check(Attempt? a, PlayerRef player, Guid request, out TamingOwner owner)
    {
        owner = default;
        if (a is null || a.ClosedAt >= 0 && _world.Now - a.ClosedAt > ClosedPayloadSeconds) return Refuse(request, "AttemptClosed: no such attempt");
        if (a.Owner != player) return Refuse(request, "NotOwner: the attempt is another player's");
        if (_world.Owner(player, out var why) is not { } o) return Refuse(request, why ?? "OwnerOffline: the player is not in game");
        if (o.Session != a.Session || _world.WorldEpoch != a.Epoch) return Refuse(request, "StaleSession: another session or world than the attempt's");
        owner = o;
        return null;
    }

    // a request's id and, where the action needs one, its token: a programmer's error throws
    private static TamingToken? Unpack(TamingRequest request, bool needsToken, string action)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty) throw new ArgumentException("an empty request id", nameof(request));
        if (needsToken && request.Token is null)
            throw new ArgumentException($"{action} names the step it expects: TamingRequest.New(progress.Token)", nameof(request));
        return request.Token;
    }

    internal Task<TamingResult> Advance(PlayerRef player, TameAttemptId id, TamingRequest request, ItemSelection? selection)
    {
        var token = Unpack(request, needsToken: true, "an advance")!;
        var rid = request.RequestId;
        _attempts.TryGetValue(id, out var a);
        var fp = Fp("advance", id.Value, token.StepChallenge, token.ExpectedRevision, token.StepIndex, SelectionFp(selection));
        // the replay cache first: the same request is the same task or result, nothing more
        if (a is not null && a.Replay.TryGetValue(rid, out var seen))
            return seen.Fingerprint == fp ? seen.Task : Task.FromResult(Refuse(rid, "RequestConflict: the request id was used for another action"));
        if (_tombstones.Contains(id) && a is null) return Task.FromResult(Refuse(rid, "AttemptClosed: the attempt is over"));
        if (Check(a, player, rid, out var owner) is { } bad) return Task.FromResult(bad);
        if (a!.Replay.Count >= MaxReceiptsPerAttempt) return Task.FromResult(Refuse(rid, "TooManyRequests: 256 requests for this attempt"));
        if (RateLimited(player)) return Task.FromResult(Refuse(rid, "TooManyRequests: 10 a second"));
        var task = AdvanceCore(a, owner, rid, token, selection);
        a.Replay[rid] = (fp, task);
        return task;
    }

    private Task<TamingResult> AdvanceCore(Attempt a, TamingOwner owner, Guid request, TamingToken token, ItemSelection? selection)
    {
        switch (a.State)
        {
            case TamingState.CostPending:
                return Task.FromResult(Refuse(request, "Busy: a step's cost is being paid", View(a)));
            case TamingState.AwaitingCommit:
                return Task.FromResult(Refuse(request, "WrongState: every step is paid; retry the tame (Taming.Retry)", View(a)));
            case not TamingState.Active:
                return Task.FromResult(Refuse(request, $"AttemptClosed: the attempt is {a.State}", View(a)));
        }
        if (token.StepChallenge != a.Challenge || token.ExpectedRevision != a.Revision || token.StepIndex != a.Completed)
            return Task.FromResult(Refuse(request, "StaleStep: the token is not the attempt's current step", View(a)));
        var step = a.Def.Steps[a.Completed];
        // the selection's shape: a programmer's error throws
        switch (step)
        {
            case WeakenStep or NativeStep when selection is not null:
                throw new ArgumentException($"a {step.Kind} step takes no selection", nameof(selection));
            case FeedStep when selection is null || !selection.IsStack:
                throw new ArgumentException("a feed step takes a plain stack's selection", nameof(selection));
            case UseToolStep when selection is null || selection.IsStack:
                throw new ArgumentException("a tool step takes a gear item's selection", nameof(selection));
        }
        if (_world.Target(a.Target) is not { Exists: true, Dead: false } t)
        {
            Fail(a, "BodyGone", request);
            return Task.FromResult(Refuse(request, "BodyGone: the target is not there", View(a)));
        }
        if (owner.Position.DistanceTo(t.Position) > a.Def.RangeMeters) return Task.FromResult(Refuse(request, $"OutOfRange: within {a.Def.RangeMeters} m", View(a)));
        if (step is WeakenStep w ? t.HealthFraction > w.MaxHealthFraction : a.Def.RequireThresholdUntilCommit && a.Threshold is { } th && t.HealthFraction > th)
            return Task.FromResult(Refuse(request, $"Threshold: the target's health must be at most {(step is WeakenStep ws ? ws.MaxHealthFraction : a.Threshold):0.###} of its maximum", View(a)));
        if (selection is not null)
        {
            var want = step is FeedStep f ? f.Item : ((UseToolStep)step).Item;
            if (selection.InventoryOwner.Id != a.OwnerEntity.Id || selection.ExpectedItem != want || step is FeedStep fs && selection.ExpectedCount < fs.Count)
                return Task.FromResult(Refuse(request, "StaleSelection: the selection is not the owner's slot of the step's item", View(a)));
        }
        if (step is NativeStep n && t.NativeProgress - a.NativeBaseline < n.Steps)
            return Task.FromResult(Refuse(request, $"NativePending: the game counted {Math.Max(0, t.NativeProgress - a.NativeBaseline)} of {n.Steps} step(s) since the last one", View(a)));
        var last = a.Completed == a.Def.Steps.Count - 1;
        var before = View(a);
        a.State = TamingState.CostPending;  // a reentrant request now gets Busy
        var quoted = step is FeedStep q ? q.Count : 0;
        var wear = step is UseToolStep u ? u.Wear : 0;
        var advancing = (TamingAdvancingEvent)AskGuarded(a, new TamingAdvancingEvent(new TamingAdvanceContext(before, request, token, a.Completed, step.Kind, selection, quoted, wear, last)));
        if (EndedInBefore(a, request) is { } endedA) return Task.FromResult(endedA);
        if (advancing.Canceled)
        {
            a.State = TamingState.Active;
            return Task.FromResult(Refuse(request, $"RefusedByMod: {advancing.CanceledBy ?? "a mod"} refused the step (nothing was spent)", View(a)));
        }
        if (last)
        {
            if (Preflight(a, request) is { } pre)
            {
                a.State = TamingState.Active;
                return Task.FromResult(Refuse(request, pre.Reason, View(a)));
            }
            var committing = (TamingCommittingEvent)AskGuarded(a, new TamingCommittingEvent(new TamingCommitContext(a.Id, request, a.Owner, a.Target, a.Def.Id, a.Spent.ToArray(),
                selection, quoted, wear, false)));
            if (EndedInBefore(a, request) is { } endedC) return Task.FromResult(endedC);
            if (committing.Canceled)
            {
                a.State = TamingState.Active;
                return Task.FromResult(Refuse(request, $"RefusedByMod: {committing.CanceledBy ?? "a mod"} refused the tame (nothing more was spent)", View(a)));
            }
        }
        a.StepToken = token;
        a.StepRequest = request;
        a.StepSelection = selection;
        a.StepBefore = before;
        a.StepTcs = new TaskCompletionSource<TamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            a.Cost = step switch
            {
                FeedStep fd => _costs.Take(selection!, fd.Count, request),
                UseToolStep tl => _costs.Wear(selection!, tl.Wear, request, tl.AllowBreak),
                _ => null,
            };
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // the adapter refused before it ran (its capability went off, a shape it does not take): nothing was taken, the
            // attempt is as it was, and the caller sees the adapter's exception (an off row: KilnUnavailableException)
            a.Cost = null;
            a.StepTcs = null;
            a.StepToken = null;
            a.StepSelection = null;
            a.StepBefore = null;
            a.State = TamingState.Active;
            a.Replay.Remove(request);
            throw;
        }
        if (step is NativeStep) a.NativeBaseline = t.NativeProgress;
        if (step is WeakenStep wk && a.Def.RequireThresholdUntilCommit) a.Threshold = wk.MaxHealthFraction;
        var task = a.StepTcs.Task;
        if (a.Cost is null || a.Cost.IsCompleted) Settle(a);  // no cost (weaken, native), or the receipt is already here
        return task;
    }

    // a step's cost settled (or it had none): apply it, defer, fail, expire or cancel
    private void Settle(Attempt a)
    {
        var request = a.StepRequest;
        var tcs = a.StepTcs!;
        var before = a.StepBefore!;
        CostReceipt? receipt = null;
        if (a.Cost is { } cost)
            receipt = cost.IsCompletedSuccessfully ? cost.Result : new CostReceipt(ItemId.None, 0, 0, false) { RequestId = request, Uncertain = true, Reason = cost.Exception?.GetBaseException().Message ?? "the cost did not settle" };
        a.Cost = null;
        a.StepTcs = null;
        var receipts = receipt is null ? Array.Empty<CostReceipt>() : [receipt];
        if (receipt is { Uncertain: true })
        {
            // neither paid nor free: the attempt ends, the receipt is kept, nothing is retried or refunded
            a.Spent.Add(receipt);
            var waiting = a.CancelRequested;
            a.CancelRequested = false;
            a.FailAfterSettle = a.ExpireAfterSettle = null;
            tcs.TrySetResult(Fail(a, "UncertainCommit", request, receipts));
            if (waiting) CompleteCancel(a);
            return;
        }
        if (receipt is { Committed: true, Kind: CostKind.StackTake } && (receipt.Disposition == CostDisposition.Partial || receipt.Taken < receipt.Requested))
        {
            // the game took only part of the stack (19e 5.2): spent as the receipt says, never the step; the attempt ends
            // PartialCostCommitted (its refund policy gives back what was taken), with no retry of the rest
            a.Spent.Add(receipt);
            var waiting = a.CancelRequested;
            a.CancelRequested = false;
            a.FailAfterSettle = a.ExpireAfterSettle = null;
            tcs.TrySetResult(Fail(a, "PartialCostCommitted", request, receipts));
            if (waiting) CompleteCancel(a);
            return;
        }
        if (receipt is { Committed: false })
        {
            a.State = TamingState.Active;  // nothing spent: the same token stays valid
            var refused = new TamingResult(false, $"Refused: {receipt.Refusal}: {receipt.Reason}", View(a), null, request, false, receipts);
            if (EndAfterSettle(a, "NoCost") is { } ended) refused = refused with { Progress = ended.Progress };
            tcs.TrySetResult(refused);
            return;
        }
        // applied
        if (receipt is not null) a.Spent.Add(receipt);
        a.Completed++;
        a.Revision++;
        a.Challenge = NewId();
        a.LastProgress = _world.Now;
        a.State = TamingState.Active;
        var after = View(a);
        Tell(new TamingStepChangedEvent(before, after, request, receipts));
        if (a.ExpireAfterSettle is not null && _world.Now < Deadline(a)) a.ExpireAfterSettle = null;  // the step was progress
        if (EndAfterSettle(a, "CostCommitted") is { } end)
        {
            tcs.TrySetResult(new TamingResult(false, end.Reason, end.Progress, null, request, true, receipts));
            return;
        }
        if (a.Completed < a.Def.Steps.Count)
        {
            tcs.TrySetResult(new TamingResult(true, null, View(a), null, request, true, receipts));
            return;
        }
        StartCommit(a, request, after, receipts, retry: false, tcs);
    }

    // a before-event under the reentrancy guard: what its handlers ask of this attempt (a cancel, a failure) waits for it
    private CancelableEvent AskGuarded(Attempt a, CancelableEvent ev)
    {
        a.InBefore = true;
        try { return Ask(ev); }
        finally { a.InBefore = false; }
    }

    // after a guarded before-event: a cancel or a failure its handlers asked ends the attempt here, before any cost (the
    // request that posted the event answers with that end; nothing new was spent). Null: none was asked.
    private TamingResult? EndedInBefore(Attempt a, Guid request)
    {
        if (!a.CancelRequested && a.FailAfterSettle is null) return null;
        a.State = TamingState.Active;
        var end = EndAfterSettle(a, "NoCost")!;
        return new TamingResult(false, end.Reason, end.Progress, null, request, false, a.Spent.ToArray());
    }

    private double Deadline(Attempt a) => Math.Min(a.LastProgress + a.Def.InactivitySeconds, a.Started + a.Def.MaxAttemptSeconds);

    private string ExpiryCause(Attempt a) => _world.Now >= a.Started + a.Def.MaxAttemptSeconds ? "MaxAttemptSeconds" : "InactivitySeconds";

    // what was asked while something settled, in this order: a cancel, a failure, a timeout. Null: none (the attempt goes on).
    private TamingResult? EndAfterSettle(Attempt a, string spent)
    {
        if (a.CancelRequested)
        {
            EndCanceled(a, a.CancelRequest, $"Canceled: {spent}");
            return new TamingResult(false, $"Canceled: the attempt was canceled while this settled ({spent})", View(a), null, a.CancelRequest, false, a.Spent.ToArray());
        }
        if (a.FailAfterSettle is { } f)
        {
            a.FailAfterSettle = null;
            return Fail(a, f, Guid.Empty);
        }
        if (a.ExpireAfterSettle is { } x)
        {
            a.ExpireAfterSettle = null;
            Expire(a, x);
            return new TamingResult(false, $"Expired: {x}", View(a), null, Guid.Empty, false, a.Spent.ToArray());
        }
        return null;
    }

    private (string Reason, bool Transient)? Preflight(Attempt a, Guid request)
    {
        try { return Binder.Preflight(a.Bind(request)); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { return ($"Refused: {e.Message}", false); }
    }

    // the last step is paid (or a cost-free retry): bind once; the attempt stays CostPending (Busy) until the bind settles
    private void StartCommit(Attempt a, Guid request, TamingProgress before, IReadOnlyList<CostReceipt> receipts, bool retry, TaskCompletionSource<TamingResult> tcs)
    {
        a.State = TamingState.CostPending;
        Task<TamingBindResult> bind;
        try { bind = Binder.Bind(a.Bind(request)); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            bind = Task.FromResult(new TamingBindResult(false, null, e.Message, false));
        }
        a.Binding = bind;
        a.BindTcs = tcs;
        a.BindBefore = before;
        a.BindReceipts = receipts;
        a.BindRequest = request;
        a.BindRetry = retry;
        if (bind.IsCompleted) FinishCommit(a);
    }

    private void FinishCommit(Attempt a)
    {
        var task = a.Binding!;
        var tcs = a.BindTcs!;
        var before = a.BindBefore!;
        var request = a.BindRequest;
        var receipts = a.BindReceipts;
        var retry = a.BindRetry;
        a.Binding = null;
        a.BindTcs = null;
        var bind = task.IsCompletedSuccessfully ? task.Result : new TamingBindResult(false, null, task.Exception?.GetBaseException().Message ?? "the bind did not settle", false);
        if (bind.Ok)
        {
            // the irreversible commit: whatever was asked meanwhile (a cancel, a timeout, the owner leaving) comes too late
            a.State = TamingState.Committed;
            a.ClosedAt = _world.Now;
            a.FailAfterSettle = a.ExpireAfterSettle = null;
            a.CancelRequested = false;
            Release(a);
            try { Binder.Committed(a.Bind(request), bind); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { }
            var done = View(a);
            Tell(new TamedEvent(before, done, request, bind.Pet));
            Tell(new TamingCompletedEvent(before, done, request));
            tcs.TrySetResult(new TamingResult(true, null, done, bind.Pet, request, !retry, retry ? a.Spent.ToArray() : receipts));
            CompleteCancel(a);
            return;
        }
        a.State = bind.Transient ? TamingState.AwaitingCommit : TamingState.Active;
        if (EndAfterSettle(a, "CostCommitted") is { } end)
        {
            tcs.TrySetResult(new TamingResult(false, end.Reason, end.Progress, null, request, !retry, receipts));
            return;
        }
        if (bind.Transient)
        {
            var deferred = View(a);
            Tell(new TamingCommitDeferredEvent(before, deferred, request, bind.Reason ?? "CommitDeferred"));
            tcs.TrySetResult(new TamingResult(false, $"CommitDeferred: {bind.Reason}", deferred, null, request, !retry, receipts));
            return;
        }
        tcs.TrySetResult(Fail(a, $"Refused: {bind.Reason}", request, receipts) with { StepApplied = !retry });
    }

    internal Task<TamingResult> Retry(PlayerRef player, TameAttemptId id, TamingRequest request)
    {
        var token = Unpack(request, needsToken: true, "a retry")!;
        _attempts.TryGetValue(id, out var a);
        var fp = Fp("retry", id.Value, token.StepChallenge, token.ExpectedRevision, token.StepIndex);
        if (a is not null && a.Replay.TryGetValue(request.RequestId, out var seen))
            return seen.Fingerprint == fp ? seen.Task : Task.FromResult(Refuse(request.RequestId, "RequestConflict: the request id was used for another action"));
        if (_tombstones.Contains(id) && a is null) return Task.FromResult(Refuse(request.RequestId, "AttemptClosed: the attempt is over"));
        if (Check(a, player, request.RequestId, out var owner) is { } bad) return Task.FromResult(bad);
        if (a!.Replay.Count >= MaxReceiptsPerAttempt) return Task.FromResult(Refuse(request.RequestId, "TooManyRequests: 256 requests for this attempt"));
        if (RateLimited(player)) return Task.FromResult(Refuse(request.RequestId, "TooManyRequests: 10 a second"));
        Task<TamingResult> task;
        if (a.State != TamingState.AwaitingCommit) task = Task.FromResult(Refuse(request.RequestId, $"WrongState: the attempt is {a.State}", View(a)));
        else if (token.StepChallenge != a.Challenge || token.ExpectedRevision != a.Revision || token.StepIndex != a.Completed)
            task = Task.FromResult(Refuse(request.RequestId, "StaleStep: the token is not the attempt's current one", View(a)));
        else if (_world.Target(a.Target) is not { Exists: true, Dead: false } t) task = Task.FromResult(Fail(a, "BodyGone", request.RequestId));
        else if (owner.Position.DistanceTo(t.Position) > a.Def.RangeMeters) task = Task.FromResult(Refuse(request.RequestId, $"OutOfRange: within {a.Def.RangeMeters} m", View(a)));
        else if (a.Def.RequireThresholdUntilCommit && a.Threshold is { } th && t.HealthFraction > th)
            task = Task.FromResult(Refuse(request.RequestId, $"Threshold: the target's health must be at most {th:0.###} of its maximum", View(a)));
        else if (Preflight(a, request.RequestId) is { } pre)
            task = Task.FromResult(Refuse(request.RequestId, pre.Transient ? $"CommitDeferred: {pre.Reason}" : pre.Reason, View(a)));
        else
        {
            var asked = (TamingCommittingEvent)AskGuarded(a, new TamingCommittingEvent(new TamingCommitContext(a.Id, request.RequestId, a.Owner, a.Target, a.Def.Id, a.Spent.ToArray(), null, 0, 0, true)));
            if (a.CancelRequested || a.FailAfterSettle is not null)
            {
                // a handler canceled (or failed) the attempt: it ends here, cost-free, and binds nothing
                var end = EndAfterSettle(a, "NoCost")!;
                task = Task.FromResult(new TamingResult(false, end.Reason, end.Progress, null, request.RequestId, false, a.Spent.ToArray()));
            }
            else if (asked.Canceled) task = Task.FromResult(Refuse(request.RequestId, $"RefusedByMod: {asked.CanceledBy ?? "a mod"} refused the tame", View(a)));
            else
            {
                var tcs = new TaskCompletionSource<TamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = tcs.Task;
                a.Replay[request.RequestId] = (fp, task);
                StartCommit(a, request.RequestId, View(a), [], retry: true, tcs);
                return task;
            }
        }
        a.Replay[request.RequestId] = (fp, task);
        return task;
    }

    // ------------------------------------------------------------------------------------------- cancel -----------

    internal Task<TamingResult> Cancel(PlayerRef player, TameAttemptId id, TamingRequest request)
    {
        // by the attempt's id: its random id is never reused and only its owner may cancel it, so a cancel made on a stale
        // view still ends only that attempt; a token, when given, must still be current (cancel only if nothing moved)
        var token = Unpack(request, needsToken: false, "a cancel");
        _attempts.TryGetValue(id, out var a);
        var fp = Fp("cancel", id.Value, token?.StepChallenge);
        if (a is not null && a.Replay.TryGetValue(request.RequestId, out var seen))
            return seen.Fingerprint == fp ? seen.Task : Task.FromResult(Refuse(request.RequestId, "RequestConflict: the request id was used for another action"));
        if (_tombstones.Contains(id) && a is null) return Task.FromResult(Refuse(request.RequestId, "AttemptClosed: the attempt is over"));
        if (Check(a, player, request.RequestId, out _) is { } bad) return Task.FromResult(bad);
        if (a!.Replay.Count >= MaxReceiptsPerAttempt) return Task.FromResult(Refuse(request.RequestId, "TooManyRequests: 256 requests for this attempt"));
        Task<TamingResult> task;
        if (Terminal(a.State)) task = Task.FromResult(Refuse(request.RequestId, $"AttemptClosed: the attempt is {a.State}", View(a)));
        else if (token is not null && token.StepChallenge != a.Challenge) task = Task.FromResult(Refuse(request.RequestId, "StaleStep: the token is not the attempt's current one", View(a)));
        else if (a.CancelRequested) task = a.CancelTcs!.Task;   // a cancel is waiting for a settlement already: the same answer
        else if (a.Settling)
        {
            // the cost in flight settles first: its receipt decides what the cancel spent
            a.CancelRequested = true;
            a.CancelRequest = request.RequestId;
            a.CancelTcs = new TaskCompletionSource<TamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = a.CancelTcs.Task;
        }
        else
        {
            var tcs = new TaskCompletionSource<TamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.CancelTcs = tcs;
            a.CancelRequest = request.RequestId;
            EndCanceled(a, request.RequestId, "Canceled");
            task = tcs.Task;
        }
        a.Replay[request.RequestId] = (fp, task);
        return task;
    }

    private void EndCanceled(Attempt a, Guid request, string cause)
    {
        var before = View(a);
        a.CancelRequested = false;
        a.State = TamingState.Canceled;
        a.ClosedAt = _world.Now;
        Release(a);
        Settlement(a, refund: a.Def.Refund == TamingRefundPolicy.Escrow, () =>
        {
            var after = View(a);
            Tell(new TamingCanceledEvent(before, after, request, cause));
            a.CancelTcs?.TrySetResult(new TamingResult(true, cause, after, null, request, false, a.Spent.ToArray()));
        });
    }

    // a cancel that waited for a settlement which closed the attempt another way (a tame, an uncertain cost)
    private void CompleteCancel(Attempt a)
    {
        a.CancelRequested = false;
        a.CancelTcs?.TrySetResult(new TamingResult(false, $"AttemptClosed: the attempt is {a.State}", View(a), null, a.CancelRequest, false, a.Spent.ToArray()));
    }

    // ------------------------------------------------------------------------------------------- the end ----------

    private TamingResult Fail(Attempt a, string cause, Guid request, IReadOnlyList<CostReceipt>? receipts = null)
    {
        if (Terminal(a.State)) return Refuse(request, $"AttemptClosed: the attempt is {a.State}", View(a));
        if (a.Settling)
        {
            a.FailAfterSettle ??= cause;  // the receipt (or the bind) decides first; never assume the cost did not happen
            return Refuse(request, $"{cause}: a cost or the tame is being settled", View(a));
        }
        var before = View(a);
        a.State = TamingState.Failed;
        a.ClosedAt = _world.Now;
        Release(a);
        // refund on fail and escrow give back what was surely paid; an uncertain receipt never (Settlement leaves it out)
        var refund = a.Def.Refund != TamingRefundPolicy.NonRefundable;
        Settlement(a, refund, () => Tell(new TamingFailedEvent(before, View(a), request, cause)));
        return new TamingResult(false, cause, View(a), null, request, false, receipts ?? a.Spent.ToArray());
    }

    private void Expire(Attempt a, string cause)
    {
        var before = View(a);
        a.State = TamingState.Expired;
        a.ClosedAt = _world.Now;
        Release(a);
        Settlement(a, refund: a.Def.Refund == TamingRefundPolicy.Escrow, () => Tell(new TamingExpiredEvent(before, View(a), cause)));
    }

    private void Release(Attempt a)
    {
        if (_claims.TryGetValue(a.Target.Id, out var c) && c == a.Id) _claims.Remove(a.Target.Id);
        try { Binder.Release(a.Bind(Guid.Empty)); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { }
    }

    // the refund policy at the end: give back every committed receipt (never an uncertain one), each once, under a request
    // id of its own derived from the paid one (a second settlement of the same attempt can never refund twice)
    private void Settlement(Attempt a, bool refund, Action done)
    {
        var paid = refund ? a.Spent.Where(r => r is { Committed: true, Uncertain: false, Taken: > 0 }).ToList() : [];
        if (paid.Count == 0)
        {
            done();
            return;
        }
        a.RefundsPending = paid.Select(p => (p, RefundOf(p))).ToList();
        a.AfterRefunds = done;
        PollRefunds(a);
    }

    // one refund through the adapter; one it refuses by throwing (its row went off, a shape it does not take) is a refused
    // receipt, so the end still comes, with its event and its answer, and says what was not given back
    private Task<CostReceipt> RefundOf(CostReceipt paid)
    {
        var id = RefundId(paid.RequestId);
        try { return _costs.Refund(paid, id); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Task.FromResult(new CostReceipt(paid.Item, paid.Taken, 0, false)
            {
                RequestId = id, Kind = paid.Kind == CostKind.GearWear ? CostKind.GearRestore : CostKind.StackRefund, Selection = paid.Selection,
                Refusal = CostRefusal.NotRun, Reason = $"the refund could not be submitted: {e.Message}", RefundOf = paid.RequestId,
            });
        }
    }

    internal static Guid RefundId(Guid paid)
    {
        var b = paid.ToByteArray();
        for (var i = 0; i < b.Length; i++) b[i] ^= 0x5a;
        b[7] = (byte)((b[7] & 0x0f) | 0x40);
        return new Guid(b);
    }

    private void PollRefunds(Attempt a)
    {
        if (a.RefundsPending is not { } pending || pending.Any(p => !p.Refund.IsCompleted)) return;
        foreach (var (paid, r) in pending)
            a.Refunds.Add(new TamingRefund(paid, r.IsCompletedSuccessfully ? r.Result : null));
        a.RefundsPending = null;
        var done = a.AfterRefunds;
        a.AfterRefunds = null;
        done?.Invoke();
    }

    // ------------------------------------------------------------------------------------------- choices ----------

    internal TamingChoicesResult Choices(PlayerRef player, TameAttemptId id)
    {
        if (!_attempts.TryGetValue(id, out var a)) return new(false, "AttemptClosed: no such attempt", null, []);
        if (a.Owner != player) return new(false, "NotOwner: the attempt is another player's", null, []);
        if (a.State != TamingState.Active) return new(false, $"WrongState: the attempt is {a.State}", TokenOf(a), []);
        var step = a.Def.Steps[a.Completed];
        if (step is WeakenStep or NativeStep) return new(true, null, TokenOf(a), []);
        var item = step is FeedStep f ? f.Item : ((UseToolStep)step).Item;
        var count = step is FeedStep fc ? fc.Count : 1;
        var list = new List<TamingItemChoice>();
        var truncated = false;
        foreach (var s in _costs.Slots(a.OwnerEntity))
        {
            if (s.Item != item || s.IsEmpty) continue;
            if (step is FeedStep ? !s.ItemEntity.IsNone || s.Count < count : s.ItemEntity.IsNone || s.Ref.IsNone || s.IsBroken) continue;
            if (list.Count == MaxChoices)
            {
                truncated = true;   // the first 256 are listed, and the answer says there were more (never a refusal)
                break;
            }
            var label = $"{s.Slot} x{s.Count}";
            list.Add(new TamingItemChoice(ItemSelection.Of(a.OwnerEntity, s), label.Length > 64 ? label[..64] : label));
        }
        return new(true, null, TokenOf(a), list) { Truncated = truncated };
    }

    /// <summary>The most choices <see cref="Choices"/> lists.</summary>
    internal const int MaxChoices = 256;

    private static TamingToken TokenOf(Attempt a) => new(a.Challenge, a.Revision, a.Completed);

    // ------------------------------------------------------------------------------------------- the tick ---------

    internal void Tick()
    {
        var now = _world.Now;
        if (_world.WorldEpoch != _epoch)
        {
            // a new world: no old request id or rate answers in it (old attempts fail WorldEnded below)
            _epoch = _world.WorldEpoch;
            _begins.Clear();
            _beginOrder.Clear();
            _rate.Clear();
        }
        foreach (var a in _attempts.Values.ToList())
        {
            if (a.RefundsPending is not null) PollRefunds(a);
            if (a.Cost is { IsCompleted: true })
            {
                Settle(a);
                continue;
            }
            if (a.Binding is { IsCompleted: true })
            {
                FinishCommit(a);
                continue;
            }
            if (Terminal(a.State))
            {
                if (a.ClosedAt >= 0 && now - a.ClosedAt > ClosedPayloadSeconds && a.RefundsPending is null)
                {
                    _attempts.Remove(a.Id);
                    if (_tombstones.Add(a.Id)) _tombOrder.Enqueue(a.Id);
                }
                continue;
            }
            // the world, the owner, the target
            string? fatal = null;
            if (_world.WorldEpoch != a.Epoch) fatal = "WorldEnded";
            else if (_world.Owner(a.Owner, out _) is not { } o || o.Session != a.Session) fatal = "OwnerLeft";
            else if (_world.Target(a.Target) is not { Exists: true, Dead: false }) fatal = "BodyGone";
            if (fatal is not null)
            {
                Fail(a, fatal, Guid.Empty);
                continue;
            }
            if (now >= Deadline(a))
            {
                if (a.Settling) a.ExpireAfterSettle ??= ExpiryCause(a);  // settle first: a cost in flight is never assumed away
                else Expire(a, ExpiryCause(a));
            }
        }
        while (_tombOrder.Count > MaxTombstones) _tombstones.Remove(_tombOrder.Dequeue());
    }

    internal int Active => _attempts.Values.Count(a => !Terminal(a.State));
}
