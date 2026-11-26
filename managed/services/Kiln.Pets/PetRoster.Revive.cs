// SPDX-License-Identifier: MIT
// The revive channel (19d §4.5, §5.1; package 6). A downed pet is revived from its visible recovery anchor: the cost is
// quoted first (nothing moves), the mods may refuse the attempt (PetRevivingEvent), the owner channels in simulation time
// (leaving range, the owner leaving or going down always cancel; moving and being hurt cancel when the definition says so),
// then everything is checked again, a private inert preview body is prepared (invulnerable, harmless), the exact cost is
// taken, and only then does the pet become Ready/Live with a new protected body. A cancel or refusal before the debit
// spends nothing; a failure after it reports its receipt and leaves the pet Downed (no refund is invented).
namespace Kiln;

internal sealed partial class PetRoster
{
    /// <summary>The costs (the exact-cost adapter); tests replace it.</summary>
    internal IPetCosts Costs { get; set; } = new GamePetCosts();
    /// <summary>Asks the mods whether a revive may start (posts PetRevivingEvent at once; tests answer).</summary>
    internal Func<PetRevivingEvent, PetRevivingEvent> AskReviving { get; set; } = e => Events.Post(e);

    private sealed class Revival
    {
        public PetDefinition Def = null!;
        public WorldPosition StartedAt;
        public int? StartHealth;
        public double ChannelStart;
        public Task<string?>? Quote;
        public Task<CostReceipt>? Commit;
        public PetCreatureInfo? Creature;
        public bool CancelRequested;
        public Op? CancelOp;
        public double PreviewSince;
    }

    private void StartRevive(Op op, PetState p)
    {
        if (p.Life != PetLife.Downed)
        {
            Complete(op, false, "NotDowned: only a downed pet is revived", p);
            return;
        }
        var def = PetsRuntime.Definition(p.Definition);
        if (def is null)
        {
            Complete(op, false, $"DefinitionUnknown: {p.Definition}", p);
            return;
        }
        if (!def.Revive.Allowed)
        {
            Complete(op, false, "ReviveNotAllowed: this pet's definition turns Kiln's revive off", p);
            return;
        }
        if (p.Presence != PetPresence.RecoveryAnchor || p.Anchor is not { } anchor)
        {
            Complete(op, false, "RecoveryNotSummoned: summon the recovery anchor first", p);
            return;
        }
        if (anchor.DistanceTo(op.Owner.Position) > def.Revive.RangeMeters)
        {
            Complete(op, false, $"OutOfRange: stand within {PetDefinitionRules.Num(def.Revive.RangeMeters)} m of the recovery anchor", p);
            return;
        }
        if (LiveOf(p.Owner) >= def.Caps.LivePerCharacter)
        {
            Complete(op, false, $"Capacity: {def.Caps.LivePerCharacter} live pet(s) per character for {def.Id}", p);
            return;
        }
        op.Extra = new Revival { Def = def, Quote = def.Revive.Cost is { } c ? Costs.Quote(op.Owner, c, op.Selection) : Task.FromResult<string?>(null) };
        op.Phase = 1;
        AdvanceRevive(op, p);
    }

    private void AdvanceRevive(Op op, PetState p)
    {
        var rv = (Revival)op.Extra!;
        switch (op.Phase)
        {
            case 1:   // the first quote: nothing has moved
                if (rv.Quote is { IsCompleted: false }) return;
                if (Quoted(rv.Quote!) is { } refused)
                {
                    Complete(op, false, refused, p);
                    return;
                }
                PetRevivingEvent asked;
                try { asked = AskReviving(new PetRevivingEvent(p.Owner, p.View(_epoch), op.OperationId, rv.Def.Revive.Cost)); }
                catch (Exception e)
                {
                    PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: a PetRevivingEvent handler threw: {e.Message}");
                    asked = new PetRevivingEvent(p.Owner, p.View(_epoch), op.OperationId, rv.Def.Revive.Cost);
                }
                if (asked.Canceled)
                {
                    Complete(op, false, $"ReviveRefused: {(asked.CanceledBy is { } by ? $"mod {by}" : "a mod")} refused it (nothing was taken)", p);
                    return;
                }
                rv.StartedAt = op.Owner.Position;
                rv.StartHealth = _world.OwnerHealth(op.Owner);
                rv.ChannelStart = _world.Now;
                Mutate(p, op.OperationId, PetChangeCause.Request, s =>
                {
                    s.Transition = PetTransition.Reviving;
                    s.ReviveOperation = op.OperationId;
                    s.ReviveElapsed = 0;
                    s.ReviveDuration = rv.Def.Revive.ChannelSeconds;
                }, c => new PetReviveStartedEvent(c));
                op.Phase = 2;
                return;
            case 2:   // the channel
                if (ChannelBroken(op, p, rv) is { } broken)
                {
                    EndChannel(op, p, broken, PetChangeCause.Channel);
                    return;
                }
                p.ReviveElapsed = Math.Min(_world.Now - rv.ChannelStart, rv.Def.Revive.ChannelSeconds);
                if (_world.Now - rv.ChannelStart < rv.Def.Revive.ChannelSeconds) return;
                // completed: everything again, then the second quote
                if (Recheck(op, p, rv) is { } why)
                {
                    EndChannel(op, p, why, PetChangeCause.Channel);
                    return;
                }
                rv.Quote = rv.Def.Revive.Cost is { } c ? Costs.Quote(op.Owner, c, op.Selection) : Task.FromResult<string?>(null);
                op.Phase = 3;
                goto case 3;
            case 3:
                if (rv.CancelRequested)
                {
                    EndChannel(op, p, "CanceledNoCost: the revive was canceled (nothing was taken)", PetChangeCause.Request);
                    return;
                }
                if (rv.Quote is { IsCompleted: false }) return;
                if (Quoted(rv.Quote!) is { } costGone)
                {
                    EndChannel(op, p, costGone, PetChangeCause.Channel);
                    return;
                }
                var spot = _world.SafeGround(p.Anchor!.Value, 0, 2, rv.Def, rv.Creature);
                if (spot is null)
                {
                    EndChannel(op, p, "NoSafeGround: no safe ground at the recovery anchor", PetChangeCause.Channel);
                    return;
                }
                var level = rv.Def.Level.Mode == PetLevelMode.OwnerAtSummon ? op.Owner.Level : rv.Def.Level.Mode == PetLevelMode.Fixed ? rv.Def.Level.Level : p.Level;
                if (level <= 0)
                {
                    EndChannel(op, p, "LevelUnavailable: the owner's level cannot be read", PetChangeCause.Channel);
                    return;
                }
                p.Snapshot = rv.Def;
                var body = _world.CreateBody(new PetBodyRequest
                {
                    Pet = p.Id, Owner = p.Owner, OwnerSession = op.Owner.Session, Definition = rv.Def, Creature = rv.Creature!, Position = spot.Value,
                    Level = Math.Clamp(level, 1, PetDefinitionRules.MaxLevel), HealthFraction = rv.Def.Revive.HealthFraction, Preview = true,
                }, out var spawnRefused);
                if (body is null)
                {
                    p.Snapshot = null;
                    EndChannel(op, p, spawnRefused ?? "SpawnRefused", PetChangeCause.Channel);
                    return;
                }
                op.Body = body;
                op.Level = level;
                rv.PreviewSince = _world.Now;
                op.Phase = 4;
                goto case 4;
            case 4:   // the inert preview gets ready (it can neither deal nor take a hit meanwhile)
                if (rv.CancelRequested)
                {
                    DropPreview(op, p);
                    EndChannel(op, p, "CanceledNoCost: the revive was canceled (nothing was taken)", PetChangeCause.Request);
                    return;
                }
                var poll = _world.Poll(op.Body!, rv.PreviewSince, out var notReady);
                if (poll == PetBodyPoll.Pending) return;
                if (poll == PetBodyPoll.Failed)
                {
                    DropPreview(op, p);
                    EndChannel(op, p, notReady ?? "BodyNotReady", PetChangeCause.Channel);
                    return;
                }
                rv.Commit = rv.Def.Revive.Cost is { } cost ? Costs.Commit(op.Owner, cost, op.Selection) : null;
                op.Phase = 5;
                goto case 5;
            case 5:   // the exact debit, then activation
                if (rv.Commit is { IsCompleted: false }) return;
                if (rv.Commit is { } commit)
                {
                    var receipt = commit.IsCompletedSuccessfully ? commit.Result : new CostReceipt(rv.Def.Revive.Cost!.Item, rv.Def.Revive.Cost.Count, 0, false);
                    if (!receipt.Committed)
                    {
                        DropPreview(op, p);
                        EndChannel(op, p, "CostUnavailable: the cost could not be taken at completion (nothing was taken)", PetChangeCause.Channel);
                        return;
                    }
                    op.Costs.Add(receipt);
                }
                if (rv.CancelRequested)
                {
                    DropPreview(op, p);
                    EndChannel(op, p, op.Costs.Count > 0 ? "CanceledCostCommitted: the revive was canceled after its cost was taken" : "CanceledNoCost: the revive was canceled (nothing was taken)", PetChangeCause.Request);
                    return;
                }
                if (_world.Read(op.Body!) is not { Exists: true } read || read.Lethal || !_world.Activate(op.Body!, out var lost))
                {
                    DropPreview(op, p);
                    EndChannel(op, p, op.Costs.Count > 0 ? "ReviveFailedCostCommitted: the new body failed after the cost was taken (the receipt says what was taken)" : "BodyNotReady", PetChangeCause.Channel);
                    return;
                }
                var events = new List<Func<PetChange, PetChangeEvent>> { c => new PetRevivedEvent(c) };
                if (p.Order != PetOrder.Follow || !p.Target.IsNone) events.Add(c => new PetCommandChangedEvent(c));
                Mutate(p, op.OperationId, PetChangeCause.Request, s =>
                {
                    s.Life = PetLife.Ready;
                    s.Body = op.Body;
                    s.Presence = PetPresence.Live;
                    s.Transition = PetTransition.None;
                    s.ReviveOperation = null;
                    s.ReviveElapsed = s.ReviveDuration = 0;
                    s.WantsSummoned = true;
                    s.Order = PetOrder.Follow;
                    s.Target = EntityRef.None;
                    s.Level = op.Level;
                    if (read.HealthMax > 0)
                    {
                        s.Health = read.Health;
                        s.HealthMax = read.HealthMax;
                        s.LastHealthMax = read.HealthMax;
                        s.HealthFraction = Math.Clamp((double)read.Health / read.HealthMax, 0, 1);
                    }
                    s.LastSafe = read.Position;
                }, events.ToArray());
                BodyActivated?.Invoke(p);
                Complete(op, true, null, p);
                if (rv.CancelOp is { } late) Complete(late, false, "AlreadyCompleted: the revive had completed", p);
                return;
        }
    }

    private static string? Quoted(Task<string?> quote) =>
        quote.IsCompletedSuccessfully ? quote.Result : $"CostUnavailable: {quote.Exception?.GetBaseException().Message ?? "the quote failed"}";

    private string? ChannelBroken(Op op, PetState p, Revival rv)
    {
        if (rv.CancelRequested) return "CanceledNoCost: the revive was canceled (nothing was taken)";
        var owner = _world.OnlineOwner(p.Owner);
        if (owner is not { } o || o.Session != op.Owner.Session) return "CanceledNoCost: the owner left";
        if (p.Presence != PetPresence.RecoveryAnchor || p.Anchor is not { } anchor) return "CanceledNoCost: the recovery anchor went";
        if (anchor.DistanceTo(o.Position) > rv.Def.Revive.RangeMeters) return "CanceledNoCost: the owner left the revive range";
        if (rv.Def.Revive.CancelOnMovement && o.Position.DistanceTo(rv.StartedAt) > rv.Def.Revive.MaxOwnerDisplacement) return "CanceledNoCost: the owner moved";
        var hp = _world.OwnerHealth(o);
        if (hp is <= 0) return "CanceledNoCost: the owner went down";
        if (rv.Def.Revive.CancelOnObservedOwnerDamage && hp is { } now && rv.StartHealth is { } then && now < then) return "CanceledNoCost: the owner was hurt";
        return null;
    }

    private string? Recheck(Op op, PetState p, Revival rv)
    {
        if (p.Life != PetLife.Downed) return "CanceledNoCost: the pet is no longer downed";
        if (PetsRuntime.Definition(p.Definition) is not { } def || PetsRuntime.CanonicalJson(def) != PetsRuntime.CanonicalJson(rv.Def))
            return "CanceledNoCost: the pet's definition changed";
        if (LiveOf(p.Owner) >= rv.Def.Caps.LivePerCharacter) return "Capacity: no live slot for the revived pet";
        if (!_world.CanMakeBodies(out var off)) return off;
        var creature = PetCreatures.Resolve(rv.Def.Creature, out var why);
        if (creature is null || creature.Problem is not null) return creature?.Problem ?? why;
        rv.Creature = creature;
        return null;
    }

    private void DropPreview(Op op, PetState p)
    {
        if (op.Body is { } b) _world.Retire(b, PetChangeCause.Request);
        op.Body = null;
        p.Snapshot = null;
    }

    private void EndChannel(Op op, PetState p, string reason, PetChangeCause cause)
    {
        if (p.ReviveOperation is not null || p.Transition == PetTransition.Reviving)
            Mutate(p, op.OperationId, cause, s =>
            {
                s.Transition = PetTransition.None;
                s.ReviveOperation = null;
                s.ReviveElapsed = s.ReviveDuration = 0;
            }, c => new PetReviveCanceledEvent(c));
        Complete(op, false, reason, p);
        if (((Revival)op.Extra!).CancelOp is { } cancel) cancel.Costs.AddRange(op.Costs);
        if (((Revival)op.Extra!).CancelOp is { } done) Complete(done, true, null, p);
    }

    /// <summary>A channel is cut by the world, not a request (the owner left, the pet went down meanwhile).</summary>
    private void CancelChannel(PetState p, PetChangeCause cause)
    {
        if (p.Running is { Kind: Kind.Revive } op && !op.Source.Task.IsCompleted)
        {
            DropPreview(op, p);
            EndChannel(op, p, op.Costs.Count > 0 ? "ReviveFailedCostCommitted: the revive was cut after its cost was taken" : $"CanceledNoCost: the revive was cut ({cause})", cause);
            return;
        }
        if (p.ReviveOperation is null) return;
        p.ReviveOperation = null;
        p.ReviveElapsed = p.ReviveDuration = 0;
    }

    /// <summary>CancelRevive: the named channel ends; a later channel is never ended by a delayed cancel; a revive that
    /// completed is not undone (AlreadyCompleted).</summary>
    private void CancelRevive(Op op, PetState p)
    {
        if (p.Running is { Kind: Kind.Revive } running && running.OperationId == op.CancelTarget && !running.Source.Task.IsCompleted)
        {
            var rv = (Revival)running.Extra!;
            rv.CancelRequested = true;
            rv.CancelOp = op;
            if (running.Phase <= 2)
            {
                DropPreview(running, p);
                EndChannel(running, p, "CanceledNoCost: the revive was canceled (nothing was taken)", PetChangeCause.Request);
            }
            // later phases end at the next step, after a pending debit settled (its receipt goes to both answers)
            return;
        }
        if (_retained.TryGetValue((op.OwnerKey, op.CancelTarget), out var earlier) && earlier.Kind == Kind.Revive && earlier.Source.Task.IsCompleted)
        {
            Complete(op, false, earlier.Source.Task.Result.Ok ? "AlreadyCompleted: the revive completed" : "AlreadyEnded: the revive had ended", p);
            return;
        }
        Complete(op, false, "NoChannel: no such revive runs", p);
    }
}
