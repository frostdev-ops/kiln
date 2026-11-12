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
internal readonly record struct NoClipStepResult(NoClipEnd? Trip = null, bool Released = false, string? Problem = null);

/// <summary>
/// The rule of the whole thing: collision never returns while a living character is inside something. The game does
/// not push a character out of solid ground, it lets it fall through (docs/re/rules.md section 8). So when the rules
/// stop carrying NoClip inside something, the tracker holds (<see cref="Restoring"/>: the runtime keeps the hooks'
/// flag on) and moves the character back to its last free position. The hold ends only when the character is in open
/// space (after the move, another try of it, or because the player flew out), when it dies, or when something else
/// teleports it. Being near the move's target is not enough: the place is asked again.
/// <para>Open space is judged from the cells at the character's body, with one exception. Where the character was
/// when NoClip began, collision was on: that place is free whatever its cells say (on a slope, a step or in a doorway
/// there is matter at knee height beside the character), for as long as those cells stay what they were. A lab showed
/// why it matters: a character put back at such a starting place was never released when only empty cells counted.</para>
/// </summary>
internal sealed class NoClipTrack(INoClipWorld world)
{
    /// <summary>How long after a move back failed the next one starts.</summary>
    internal const double RetrySeconds = 1;

    /// <summary>A move back counts as the reason the character is free when it ended this close to its target.</summary>
    internal const double ArrivedWithin = 3;

    /// <summary>How close to the place NoClip began the character must be for that place's word to count.</summary>
    internal const double VouchedWithin = 0.3;

    private byte? _teleports;                      // the game's counter as last accounted for
    private NoClipEnd? _trip;                      // a death or teleport reported by Step, for the Leaving that follows
    // The move back that was asked for and has not answered yet. It outlives the hold (a death ends the hold, the
    // move may still land): no second one is started on top of it, and its teleport is never taken for someone else's.
    private Task<TeleportResult>? _move;
    private WorldPosition? _target;                // where the hold moves the character to; null: nowhere (left) to move it to
    private WorldPosition? _landed;                // where a move of this hold put the character
    private (WorldPosition At, ulong Cells)? _vouched;  // where NoClip began (collision was on), and its cells then
    private DateTime _retryAt;
    private readonly Queue<(NoClipEnd Reason, WorldPosition? RestoredTo)> _ended = new();

    /// <summary>The player's rules carry NoClip (between <see cref="Entering"/> and <see cref="Leaving"/>).</summary>
    public bool On { get; private set; }

    /// <summary>The last place where the character was in open space while NoClip was on; at first, where it was when
    /// NoClip began. Null when NoClip is off and nothing is held.</summary>
    public WorldPosition? LastFree { get; private set; }

    /// <summary>Where the character was at the last step with NoClip on (a teleport is noticed one step later: this is
    /// from before it).</summary>
    public WorldPosition? LastSeen { get; private set; }

    /// <summary>The rules no longer carry NoClip, but the character was inside something: collision stays off until it
    /// is in open space.</summary>
    public bool Restoring { get; private set; }

    /// <summary>Moves back started for the current hold.</summary>
    public int Attempts { get; private set; }

    /// <summary>The rules start carrying NoClip. Call it before the hooks hear of it: collision is still on then, so
    /// where the character is now is free.</summary>
    public void Entering()
    {
        if (On) return;
        On = true;
        _trip = null;
        if (Restoring) return;  // asked again during a hold: it goes on from there (Step ends the hold)
        var at = world.Position;
        LastFree = LastSeen = at;
        _vouched = at is { } p ? (p, world.Cells(p)) : null;
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
        if (Restoring) return null;  // the hold that is running finishes it (Step)
        var seen = _trip is null;
        var reason = _trip ?? (world.Dead ? NoClipEnd.Death : _move is null && world.Teleports != _teleports ? NoClipEnd.Teleport : NoClipEnd.RuleEnded);
        _trip = null;
        // After a death the game moves the character itself; after a teleport it is where it was put. Otherwise: inside
        // something, collision waits, with or without a place to move the character to.
        if (reason == NoClipEnd.RuleEnded && world.Position is { } at && !Free(at))
        {
            Restoring = true;
            _landed = null;
            _target = LastFree is { } free && world.InWorld(free) ? free : null;
            Attempts = _move is null ? 0 : 1;  // a move of an earlier hold that is still out is this hold's first
            _retryAt = world.Now;
            if (_target is not null) StartMove();
            return null;
        }
        _teleports = world.Teleports;
        Finish(reason, null);
        return seen && reason != NoClipEnd.RuleEnded ? reason : null;
    }

    /// <summary>Once a server step.</summary>
    public NoClipStepResult Step()
    {
        var problem = Settle();
        if (Restoring) return StepHold(problem);
        if (!On) return default;
        if (world.Dead) return new(Trip: _trip = NoClipEnd.Death);
        var count = world.Teleports;
        if (_move is null && count != _teleports)  // while a move of Kiln's own is out, its landing is not a trip
        {
            _teleports = count;
            return new(Trip: _trip = NoClipEnd.Teleport);
        }
        if (world.Position is { } at)
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

    // The move that was out has answered: its teleport is Kiln's own (the counter is taken as it is now), and whether it
    // landed where it was sent is remembered for the hold. Returns what went wrong, if it did.
    private string? Settle()
    {
        if (_move is not { IsCompleted: true } move) return null;
        _move = null;
        var moved = move.IsCompletedSuccessfully && move.Result.Moved;
        if (moved) _teleports = world.Teleports;
        var at = world.Position;
        if (moved && _target is { } target && at is { } p && Distance(p, target) <= ArrivedWithin)
        {
            _landed = target;
            return null;
        }
        _retryAt = world.Now.AddSeconds(RetrySeconds);
        return $"move {Attempts} to the last free position {_target} did not work (" +
               (move.IsCompletedSuccessfully ? moved ? $"it ended at {at}" : move.Result.Reason : "it failed") + ")";
    }

    private NoClipStepResult StepHold(string? problem)
    {
        if (On)
        {
            // Asked again meanwhile: nothing ends, and NoClip goes on from where the character is.
            EndHold();
            LastSeen = world.Position;
            if (LastSeen is { } p && world.InWorld(p) && world.Space(p) == NoClipSpace.Free) LastFree = p;
            return default;
        }
        if (world.Dead || world.Position is not { } at)
        {
            // dead (the game moves the body itself) or gone: nothing to hold
            var dead = world.Dead;
            EndHold();
            Finish(dead ? NoClipEnd.Death : NoClipEnd.RuleEnded, null);
            return new(Released: true);
        }
        if (_move is null && world.Teleports != _teleports)
        {
            // something else teleported it (fast travel during a retry): it is where the game put it, and it is never
            // pulled back from there
            _teleports = world.Teleports;
            EndHold();
            Finish(NoClipEnd.Teleport, null);
            return new(Released: true);
        }
        if (Free(at))
        {
            // in open space: by the move, or by itself (the player flew out). A move still out lands on a character
            // with collision, at a place that was free.
            var to = _landed is { } l && Distance(at, l) <= ArrivedWithin ? l : (WorldPosition?)null;
            EndHold();
            Finish(NoClipEnd.RuleEnded, to);
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

    private void StartMove()
    {
        if (_move is not null || _target is not { } target) return;  // never a second move on top of one that is out
        Attempts++;
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
        _landed = null;
        _target = null;
    }

    private void Finish(NoClipEnd reason, WorldPosition? restoredTo)
    {
        LastFree = LastSeen = null;
        _vouched = null;
        _ended.Enqueue((reason, restoredTo));
    }

    // Open space by the cells, or the place where NoClip began with its cells unchanged since.
    private bool Free(WorldPosition at) =>
        world.Space(at) == NoClipSpace.Free ||
        _vouched is { } v && Distance(at, v.At) <= VouchedWithin && world.Cells(v.At) == v.Cells;

    private static double Distance(WorldPosition a, WorldPosition b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
