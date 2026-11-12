// SPDX-License-Identifier: MIT
// One player's NoClip as Kiln.Rules tracks it, between the rules and the hooks: when it started, where the character
// last was in open space, what ended it, and the move back before collision returns. It has no game access of its own:
// the runtime gives it the world through INoClipWorld, and a unit test gives it a fake one.
namespace Kiln;

/// <summary>What <see cref="NoClipTrack"/> needs to know of one character and may do to it.</summary>
internal interface INoClipWorld
{
    /// <summary>Where the character is; null when it has no position (gone).</summary>
    WorldPosition? Position { get; }

    /// <summary>What kind of place <paramref name="at"/> is for a character standing there.</summary>
    NoClipSpace Space(WorldPosition at);

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
/// <param name="Released">The character is in open space again after a move back (or by itself): the hooks may hear
/// that NoClip is off.</param>
/// <param name="Problem">A move back that did not work, for the log; the hold goes on.</param>
internal readonly record struct NoClipStepResult(NoClipEnd? Trip = null, bool Released = false, string? Problem = null);

/// <summary>
/// The rule of the whole thing: collision never returns while a living character is inside something. The game does
/// not push a character out of solid ground, it lets it fall through (docs/re/rules.md section 8). So when the rules
/// stop carrying NoClip inside something, the tracker starts a move back to the last free position and reports
/// <see cref="Restoring"/>: the runtime keeps the hooks' flag on until the character is in open space, by that move,
/// by another try of it, or because the player flew out.
/// </summary>
internal sealed class NoClipTrack(INoClipWorld world)
{
    /// <summary>How long after a move back failed the next one starts.</summary>
    internal const double RetrySeconds = 1;

    /// <summary>A move back counts as arrived this close to its target (the move is exact; this allows a step of
    /// flight in between).</summary>
    internal const double ArrivedWithin = 3;

    private byte? _teleports;
    private NoClipEnd? _trip;                      // a death or teleport reported by Step, for the Leaving that follows
    private Task<TeleportResult>? _move;           // the move back in flight
    private DateTime _retryAt;
    private WorldPosition _target;
    private readonly Queue<(NoClipEnd Reason, WorldPosition? RestoredTo)> _ended = new();

    /// <summary>The player's rules carry NoClip (between <see cref="Entering"/> and <see cref="Leaving"/>).</summary>
    public bool On { get; private set; }

    /// <summary>The last place where the character was in open space while NoClip was on; at first, where it was when
    /// NoClip began. Null when NoClip is off and no move back is in flight.</summary>
    public WorldPosition? LastFree { get; private set; }

    /// <summary>Where the character was at the last step with NoClip on (a teleport is noticed one step later: this is
    /// from before it).</summary>
    public WorldPosition? LastSeen { get; private set; }

    /// <summary>The rules no longer carry NoClip, but the character was inside something: collision stays off until it
    /// is in open space.</summary>
    public bool Restoring { get; private set; }

    /// <summary>Moves back started for the current restore.</summary>
    public int Attempts { get; private set; }

    /// <summary>The rules start carrying NoClip. Call it before the hooks hear of it: collision is still on then, so
    /// where the character is now is free.</summary>
    public void Entering()
    {
        if (On) return;
        On = true;
        _trip = null;
        if (Restoring) return;  // asked again while a move back is in flight: it goes on from where that leaves it
        LastFree = LastSeen = world.Position;
        _teleports = world.Teleports;
    }

    /// <summary>The rules stop carrying NoClip. Call it before the hooks hear of it. When <see cref="Restoring"/> is true
    /// afterwards, the hooks must not hear of it yet.</summary>
    public void Leaving()
    {
        if (!On) return;
        On = false;
        if (Restoring) return;  // the move back that is in flight finishes it (Step)
        // What ended it: what Step reported, else what shows now (an area left by a teleport is resolved before Step
        // sees the counter change).
        var reason = _trip ?? (world.Dead ? NoClipEnd.Death : world.Teleports != _teleports ? NoClipEnd.Teleport : NoClipEnd.RuleEnded);
        _trip = null;
        // After a death the game moves the character itself; after a teleport it is where it was put.
        if (reason == NoClipEnd.RuleEnded && LastFree is { } free && world.InWorld(free) && world.Position is { } at &&
            world.Space(at) != NoClipSpace.Free)
        {
            Restoring = true;
            _target = free;
            Attempts = 0;
            StartMove();
            return;
        }
        Finish(reason, null);
    }

    /// <summary>Once a server step.</summary>
    public NoClipStepResult Step()
    {
        if (Restoring) return StepRestore();
        if (!On) return default;
        if (world.Dead) return new(Trip: _trip = NoClipEnd.Death);
        var count = world.Teleports;
        if (count != _teleports)
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

    private NoClipStepResult StepRestore()
    {
        if (On)
        {
            // Asked again meanwhile: nothing ends. A move still in flight lands first, so its teleport is not taken
            // for one that ends NoClip.
            if (_move is { IsCompleted: false }) return default;
            EndRestore();
            _teleports = world.Teleports;
            LastSeen = world.Position;
            if (LastSeen is { } p && world.InWorld(p) && world.Space(p) == NoClipSpace.Free) LastFree = p;
            return default;
        }
        if (world.Dead || world.Position is not { } at)
        {
            // dead (the game moves the body itself) or gone: nothing to hold
            EndRestore();
            Finish(world.Dead ? NoClipEnd.Death : NoClipEnd.RuleEnded, null);
            return new(Released: true);
        }
        if (_move is { IsCompleted: false }) return default;  // on its way
        var arrived = _move is { IsCompletedSuccessfully: true } done && done.Result.Moved && Distance(at, _target) <= ArrivedWithin;
        if (arrived || world.Space(at) == NoClipSpace.Free)
        {
            // there, or in open space by itself (the player flew out while a move failed)
            EndRestore();
            Finish(NoClipEnd.RuleEnded, arrived ? _target : null);
            return new(Released: true);
        }
        // The move failed or ended somewhere else, and the character is still inside something: collision stays off,
        // and the move is tried again.
        string? problem = null;
        if (_move is not null)
        {
            problem = $"move {Attempts} to the last free position {_target} did not work (" +
                      (_move.IsCompletedSuccessfully ? _move.Result.Moved ? $"it ended at {at}" : _move.Result.Reason : "it failed") +
                      "); collision stays off until the character is in open space";
            _move = null;
            _retryAt = world.Now.AddSeconds(RetrySeconds);
        }
        else if (world.Now >= _retryAt)
        {
            StartMove();
        }
        return new(Problem: problem);
    }

    private void StartMove()
    {
        Attempts++;
        try
        {
            _move = world.Teleport(_target);
        }
        catch (Exception e)
        {
            _move = Task.FromException<TeleportResult>(e);
        }
    }

    private void EndRestore()
    {
        Restoring = false;
        _move = null;
    }

    private void Finish(NoClipEnd reason, WorldPosition? restoredTo)
    {
        LastFree = LastSeen = null;
        _ended.Enqueue((reason, restoredTo));
    }

    private static double Distance(WorldPosition a, WorldPosition b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
