// SPDX-License-Identifier: MIT
// One player's NoClip as Kiln.Rules tracks it, between the rules and the hooks: when it started, where the character
// last was in open space, what ended it, and the hold on collision until the character is in open space again. It has
// no game access of its own: the runtime gives it the world through INoClipWorld, and a unit test gives it a fake one.
namespace Kiln;

/// <summary>What <see cref="NoClipTrack"/> needs to know of one character and may do to it.</summary>
internal interface INoClipWorld
{
    /// <summary>Where the character is; null when it has no position (gone).</summary>
    WorldPosition? Position { get; }

    /// <summary>What kind of place <paramref name="at"/> is for a character standing there.</summary>
    NoClipSpace Space(WorldPosition at);

    /// <summary>A number for what fills the cells around a character's body at <paramref name="at"/>: it changes when
    /// one of them changes (built over, dug out, loaded or unloaded).</summary>
    ulong Cells(WorldPosition at);

    /// <summary>What the cells at the waist and the head of a character at <paramref name="at"/> say, leaving the
    /// knees out. Free: it can be standing there, with matter at knee height beside it at most (a slope, a step).
    /// Inside: it is in matter up to the waist or the head.</summary>
    NoClipSpace Torso(WorldPosition at);

    /// <summary>The game's teleport counter of the character: it changes with every teleport, whoever caused it.</summary>
    byte? Teleports { get; }

    bool Dead { get; }

    /// <summary>Whether Kiln may move a character to <paramref name="at"/> at all.</summary>
    bool InWorld(WorldPosition at);

    /// <summary>Starts moving the character to <paramref name="to"/>, exactly there (no ground search).</summary>
    Task<TeleportResult> Teleport(WorldPosition to);

    DateTime Now { get; }
}

/// <summary>What one step of the tracker asks of the runtime.</summary>
/// <param name="Trip">A death or a teleport seen while NoClip was on: the runtime uses up the requests that asked for it
/// and resolves the rules, which reaches <see cref="NoClipTrack.Leaving"/>.</param>
/// <param name="Released">The hold ended: the hooks may hear that NoClip is off.</param>
/// <param name="Problem">A move back that did not work, for the log; the hold goes on.</param>
/// <param name="Held">A hold began in this step (a move back landed late, inside something): the hooks must hear that
/// collision is off again.</param>
internal readonly record struct NoClipStepResult(NoClipEnd? Trip = null, bool Released = false, string? Problem = null, bool Held = false);

/// <summary>
/// The rule of the whole thing: collision never returns while a living character is inside something. The game does
/// not push a character out of solid ground, it lets it fall through (docs/re/rules.md section 8). So when the rules
/// stop carrying NoClip inside something, the tracker holds (<see cref="Restoring"/>: the runtime keeps the hooks'
/// flag on) and moves the character back to its last free position. The hold ends only when the character is in open
/// space (after the move, another try of it, or because the player flew out) or when it dies. Being near the move's
/// target is not enough: the place is asked again.
/// <para>A teleport by the game or a mod is never undone: the character stays where that put it, and no move back is
/// started or repeated afterwards. If that place is known to be inside something, collision waits there too.</para>
/// <para>A move back cannot be called off once asked for. It may land after the hold ended (the player flew out and
/// was released): if it then lands inside something (built over meanwhile), collision goes off again, quietly, until
/// the character is free.</para>
/// <para>Open space is judged from the cells at the character's body, with one exception. Where the character stood
/// when NoClip began, collision was on: that place is free whatever its knee cell says (on a slope or a step there
/// is matter at knee height beside the character), for as long as its cells stay what they were and the character is
/// in cells that look the same. A lab showed why it matters: a character put back at such a starting place was never
/// released when only empty cells counted. A character whose waist or head is in matter when NoClip begins gets no
/// such word: it was not standing there.</para>
/// </summary>
internal sealed class NoClipTrack(INoClipWorld world)
{
    /// <summary>How long after a move back failed the next one starts.</summary>
    internal const double RetrySeconds = 1;

    /// <summary>A move back counts as the reason the character is free when it ended this close to its target.</summary>
    internal const double ArrivedWithin = 3;

    /// <summary>How close to the place NoClip began the character must be for that place's word to count.</summary>
    internal const double VouchedWithin = 0.3;

    private enum Settled { Nothing, Landed, Foreign, Failed }

    private byte? _teleports;                      // the game's counter as last accounted for
    private NoClipEnd? _trip;                      // a death or teleport reported by Step, for the Leaving that follows
    // The move back that was asked for and has not answered yet. It outlives the hold (a death ends the hold, the
    // move may still land): no second one is started on top of it.
    private Task<TeleportResult>? _move;
    private WorldPosition _moveTarget;             // where the move that is out was sent
    private WorldPosition? _target;                // where the hold moves the character to; null: nowhere (left) to move it to
    private WorldPosition? _landed;                // where a move of this hold put the character
    private DateTime _retryAt;                     // when the next move may start after one that failed
    private NoClipEnd _holdReason;                 // what the hold reports when it ends: RuleEnded, or Teleport (put inside something by one)
    private bool _quiet;                           // a hold after a late landing: NoClip's end was already reported
    private (WorldPosition At, ulong Cells)? _vouched;  // where NoClip began (collision was on), and its cells then
    private readonly Queue<(NoClipEnd Reason, WorldPosition? RestoredTo)> _ended = new();

    /// <summary>The player's rules carry NoClip (between <see cref="Entering"/> and <see cref="Leaving"/>).</summary>
    public bool On { get; private set; }

    /// <summary>The last place where the character was in open space while NoClip was on; at first, where it was when
    /// NoClip began. Null when NoClip is off and nothing is held.</summary>
    public WorldPosition? LastFree { get; private set; }

    /// <summary>Where the character was at the last step with NoClip on (a teleport is noticed one step later: this is
    /// from before it).</summary>
    public WorldPosition? LastSeen { get; private set; }

    /// <summary>The rules no longer carry NoClip, but the character is inside something: collision stays off until it
    /// is in open space.</summary>
    public bool Restoring { get; private set; }

    /// <summary>Moves back started for the current hold.</summary>
    public int Attempts { get; private set; }

    /// <summary>The rules start carrying NoClip. Call it before the hooks hear of it: collision is still on then, so
    /// where the character stands now is free.</summary>
    public void Entering()
    {
        if (On) return;
        On = true;
        _trip = null;
        if (Restoring) return;  // asked again during a hold: it goes on from there (Step ends the hold)
        var at = world.Position;
        LastFree = LastSeen = at;
        _vouched = at is { } p && world.Torso(p) == NoClipSpace.Free ? (p, world.Cells(p)) : null;
        if (_move is null) _teleports = world.Teleports;  // with a move still out, its landing settles the counter
    }

    /// <summary>The rules stop carrying NoClip. Call it before the hooks hear of it; when <see cref="Restoring"/> is true
    /// afterwards, the hooks must not hear of it yet.</summary>
    /// <param name="seenBefore">Where the character was at the last step, before whatever ended it.</param>
    /// <returns>A death or a teleport that ended it and that <see cref="Step"/> had not reported: the runtime uses up
    /// the requests for it now (an area the teleport left is resolved before Step sees the counter). Else null.</returns>
    public NoClipEnd? Leaving(out WorldPosition? seenBefore)
    {
        seenBefore = LastSeen;
        if (!On) return null;
        On = false;
        var dead = world.Dead;
        var teleported = _move is null && world.Teleports != _teleports;
        // Asked again during a hold and ended again before a step ran: the hold that is running finishes it (Step).
        // A death or a teleport in between is still told, for the requests; the hold's step sees the teleport itself.
        if (Restoring) return dead ? NoClipEnd.Death : teleported ? NoClipEnd.Teleport : null;
        var told = _trip is not null;
        var reason = _trip ?? (dead ? NoClipEnd.Death : teleported ? NoClipEnd.Teleport : NoClipEnd.RuleEnded);
        _trip = null;
        var tell = !told && reason != NoClipEnd.RuleEnded ? reason : (NoClipEnd?)null;
        _teleports = world.Teleports;
        if (world.Position is { } at)
        {
            // Inside something, collision waits, with or without a place to move the character to.
            if (reason == NoClipEnd.RuleEnded && !Free(at))
            {
                Hold(NoClipEnd.RuleEnded, LastFree is { } free && world.InWorld(free) ? free : null);
                return tell;
            }
            // Put into something by a teleport (a mod's, to a place it did not check): collision waits there. The
            // character is never moved back. Matter at the knees alone is where a character stands on a slope, and
            // a place whose cells are not loaded yet is the game's to handle: it holds a character until they are.
            if (reason == NoClipEnd.Teleport && world.Torso(at) == NoClipSpace.Inside)
            {
                Hold(NoClipEnd.Teleport, null);
                return tell;
            }
        }
        // After a death the game moves the body itself.
        Finish(reason, null);
        return tell;
    }

    /// <summary>Once a server step.</summary>
    public NoClipStepResult Step()
    {
        var settled = Settle(out var problem);
        if (Restoring && On) EndHold();  // asked again meanwhile: nothing ends, and NoClip goes on below
        if (Restoring) return StepHold(settled, problem);
        if (!On)
        {
            // A move of a hold that is over landed now (the character was free elsewhere and collision came back).
            // Inside something there, collision goes off again until the character is free.
            if (settled == Settled.Landed && !world.Dead && world.Position is { } late && !Free(late))
            {
                Hold(NoClipEnd.RuleEnded, null, quiet: true);
                return new(Held: true, Problem: $"a move back landed late at {_moveTarget}, inside something there (built over meanwhile); " +
                                                "collision is off again until the character is in open space");
            }
            return default;
        }
        if (world.Dead) return new(Trip: _trip = NoClipEnd.Death);
        if (settled == Settled.Foreign) return new(Trip: _trip = NoClipEnd.Teleport);
        var count = world.Teleports;
        if (_move is null && count != _teleports)
        {
            _teleports = count;
            return new(Trip: _trip = NoClipEnd.Teleport);
        }
        // While a move of Kiln's own is out, a counter that moved is its landing or someone else's teleport: told
        // apart when it answers. Until then the place is not taken for one the character flew to.
        if (count == _teleports && world.Position is { } at)
        {
            LastSeen = at;
            if (world.InWorld(at) && world.Space(at) == NoClipSpace.Free) LastFree = at;
        }
        return default;
    }

    /// <summary>The next "NoClip ended" to report, oldest first; false when there is none.</summary>
    public bool TakeEnded(out NoClipEnd reason, out WorldPosition? restoredTo)
    {
        if (_ended.TryDequeue(out var e))
        {
            (reason, restoredTo) = e;
            return true;
        }
        (reason, restoredTo) = (NoClipEnd.RuleEnded, null);
        return false;
    }

    // The move that was out has answered. Landed: the character is where Kiln sent it. Foreign: the game's counter
    // moved and the character is somewhere else, so it was someone else's teleport that counted (the game reports any
    // counter change as "moved"). Failed: nothing moved; `problem` says why.
    private Settled Settle(out string? problem)
    {
        problem = null;
        if (_move is not { IsCompleted: true } move) return Settled.Nothing;
        _move = null;
        var before = _teleports;
        var now = world.Teleports;
        _teleports = now;
        var moved = move.IsCompletedSuccessfully && move.Result.Moved;
        var at = world.Position;
        if (moved && at is { } p && Distance(p, _moveTarget) <= ArrivedWithin)
        {
            if (Restoring && _target == _moveTarget) _landed = _moveTarget;
            return Settled.Landed;
        }
        if (now != before) return Settled.Foreign;
        _retryAt = world.Now.AddSeconds(RetrySeconds);
        problem = $"move {Attempts} to the last free position {_moveTarget} did not work (" +
                  (move.IsCompletedSuccessfully ? moved ? $"it ended at {at}" : move.Result.Reason : "it failed") + ")";
        return Settled.Failed;
    }

    private NoClipStepResult StepHold(Settled settled, string? problem)
    {
        if (world.Dead || world.Position is not { } at)
        {
            // dead (the game moves the body itself) or gone: nothing to hold
            var dead = world.Dead;
            Release(dead ? NoClipEnd.Death : _holdReason, null);
            return new(Released: true);
        }
        if (settled == Settled.Foreign || (_move is null && world.Teleports != _teleports))
        {
            // Someone else teleported it (fast travel during a retry): it is where that put it and is never pulled
            // back from there. No move is started or repeated; the hold ends as a teleport.
            _teleports = world.Teleports;
            _target = null;
            _landed = null;
            _holdReason = NoClipEnd.Teleport;
            problem = null;
        }
        // Open space ends the hold: by the move, or by itself (the player flew out). After a teleport, so does any
        // place where the character is not in matter up to the waist or the head (see Leaving).
        if (Free(at) || (_holdReason == NoClipEnd.Teleport && world.Torso(at) != NoClipSpace.Inside))
        {
            Release(_holdReason, _holdReason == NoClipEnd.RuleEnded && _landed is { } l && Distance(at, l) <= ArrivedWithin ? l : null);
            return new(Released: true);
        }
        // Still inside something: collision stays off. A move that landed where it was sent and left the character
        // inside (something was built there meanwhile, or the place is not loaded yet) is not repeated: the same move
        // changes nothing. Collision waits for the place to be free or for the player to fly out.
        if (_landed is not null && _target is not null)
        {
            problem ??= $"move {Attempts} landed at {_target}, but the character is inside something there (it is not moved there again)";
            _target = null;
        }
        if (_move is null && _target is not null && world.Now >= _retryAt) StartMove();
        return new(Problem: problem is null ? null : problem + "; collision stays off until the character is in open space");
    }

    private void Hold(NoClipEnd reason, WorldPosition? target, bool quiet = false)
    {
        Restoring = true;
        _holdReason = reason;
        _quiet = quiet;
        _landed = null;
        _target = target;
        Attempts = _move is null ? 0 : 1;  // a move of an earlier hold that is still out is this hold's first
        _retryAt = world.Now;
        if (_target is not null) StartMove();
    }

    private void StartMove()
    {
        if (_move is not null || _target is not { } target) return;  // never a second move on top of one that is out
        Attempts++;
        _moveTarget = target;
        try
        {
            _move = world.Teleport(target);
        }
        catch (Exception e)
        {
            _move = Task.FromException<TeleportResult>(e);
        }
    }

    private void EndHold()
    {
        Restoring = false;
        _quiet = false;
        _landed = null;
        _target = null;
    }

    // The hold is over: collision may return. A quiet hold reports nothing (NoClip's end was reported before it).
    private void Release(NoClipEnd reason, WorldPosition? restoredTo)
    {
        var quiet = _quiet;
        EndHold();
        if (quiet) LastFree = LastSeen = null;
        else Finish(reason, restoredTo);
    }

    private void Finish(NoClipEnd reason, WorldPosition? restoredTo)
    {
        LastFree = LastSeen = null;
        _vouched = null;
        _ended.Enqueue((reason, restoredTo));
    }

    // Open space by the cells; or the place where NoClip began, with its cells unchanged since and the character in
    // cells that look the same (a step sideways into a wall is not that place).
    private bool Free(WorldPosition at) =>
        world.Space(at) == NoClipSpace.Free ||
        _vouched is { } v && Distance(at, v.At) <= VouchedWithin && world.Cells(v.At) == v.Cells && world.Cells(at) == v.Cells;

    private static double Distance(WorldPosition a, WorldPosition b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
