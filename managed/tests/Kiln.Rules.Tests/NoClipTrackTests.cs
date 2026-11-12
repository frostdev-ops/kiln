// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Rules.Tests;

/// <summary>One player's NoClip from start to end, with a world the test controls: the last free position, what ends
/// it, and above all that collision never comes back while a living character is inside something.</summary>
public class NoClipTrackTests
{
    private sealed class FakeWorld : INoClipWorld
    {
        public WorldPosition? At = new WorldPosition(100, 50, 100);
        public Func<WorldPosition, NoClipSpace> Kind = _ => NoClipSpace.Free;
        public byte? Count = 7;
        public bool IsDead;
        public DateTime Clock = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        public readonly List<(WorldPosition To, TaskCompletionSource<TeleportResult> Done)> Moves = new();
        public bool Throws;

        public WorldPosition? Position => At;
        public NoClipSpace Space(WorldPosition at) => Kind(at);
        public byte? Teleports => Count;
        public bool Dead => IsDead;
        public bool InWorld(WorldPosition at) => at.Y > -500;
        public DateTime Now => Clock;

        public Task<TeleportResult> Teleport(WorldPosition to)
        {
            if (Throws) throw new InvalidOperationException("no teleport service");
            var done = new TaskCompletionSource<TeleportResult>();
            Moves.Add((to, done));
            return done.Task;
        }

        /// <summary>The newest move lands: the character is at its target, and the game counted a teleport.</summary>
        public void Land()
        {
            var (to, done) = Moves[^1];
            At = to;
            Count = (byte)(Count!.Value + 1);
            done.SetResult(new TeleportResult(TeleportStatus.Moved, new EntityId(7), to, to, 0, TeleportFlags.None, 3, ""));
        }

        public void Refuse(string why) =>
            Moves[^1].Done.SetResult(new TeleportResult(TeleportStatus.Failed, new EntityId(7), At!.Value, Moves[^1].To, 0, TeleportFlags.None, 0, why));
    }

    private static readonly WorldPosition Surface = new(100, 50, 100);
    private static readonly WorldPosition Deep = new(100, 43, 100);

    // Below y 49 is rock.
    private static NoClipSpace Rock(WorldPosition p) => p.Y < 49 ? NoClipSpace.Inside : NoClipSpace.Free;

    private static (NoClipTrack, FakeWorld) Flying()
    {
        var w = new FakeWorld { Kind = Rock };
        var t = new NoClipTrack(w);
        t.Entering();
        return (t, w);
    }

    private static List<(NoClipEnd, WorldPosition?)> Ended(NoClipTrack t)
    {
        var all = new List<(NoClipEnd, WorldPosition?)>();
        while (t.TakeEnded(out var reason, out var to)) all.Add((reason, to));
        return all;
    }

    [Fact]
    public void ItStartsWhereTheCharacterStandsAndFollowsItThroughOpenSpace()
    {
        var (t, w) = Flying();
        Assert.True(t.On);
        Assert.Equal(Surface, t.LastFree);  // collision was on until now: this place is free, whatever the voxels say
        w.At = new WorldPosition(104, 60, 100);
        Assert.Equal(default, t.Step());
        Assert.Equal(w.At, t.LastFree);
        w.At = Deep;
        t.Step();
        Assert.Equal(new WorldPosition(104, 60, 100), t.LastFree);  // inside the rock: the last free place stays
        Assert.Equal(Deep, t.LastSeen);
        w.Kind = _ => NoClipSpace.Unknown;                          // voxels not loaded: not known to be free
        w.At = new WorldPosition(300, 60, 100);
        t.Step();
        Assert.Equal(new WorldPosition(104, 60, 100), t.LastFree);
        w.Kind = Rock;
        w.At = new WorldPosition(300, -600, 100);                   // outside the world: never a place to go back to
        t.Step();
        Assert.Equal(new WorldPosition(104, 60, 100), t.LastFree);
        Assert.Empty(Ended(t));
    }

    [Fact]
    public void EnteringTwiceChangesNothing()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Entering();
        Assert.Equal(Surface, t.LastFree);  // not replaced by the place inside the rock
    }

    [Fact]
    public void EndedInOpenSpaceCollisionSimplyReturns()
    {
        var (t, w) = Flying();
        w.At = new WorldPosition(100, 70, 100);
        t.Step();
        t.Leaving();
        Assert.False(t.On);
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
        Assert.Null(t.LastFree);
        Assert.Equal(default, t.Step());  // nothing more to do
    }

    [Fact]
    public void EndedInsideSomethingItIsMovedBackFirstAndCollisionWaits()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        Assert.False(t.On);
        Assert.True(t.Restoring);               // the hooks must not hear of the end yet
        Assert.Equal(Surface, Assert.Single(w.Moves).To);
        Assert.Empty(Ended(t));
        Assert.False(t.Step().Released);        // the move is on its way
        Assert.True(t.Restoring);
        w.Land();
        var r = t.Step();
        Assert.True(r.Released);
        Assert.False(t.Restoring);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)Surface)], Ended(t));
        Assert.Equal(1, t.Attempts);
    }

    [Fact]
    public void AMoveBackThatFailsNeverReturnsCollisionInsideTheRock()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        w.Refuse("the entity is busy");
        var r = t.Step();
        Assert.False(r.Released);
        Assert.True(t.Restoring);               // still held
        Assert.Contains("the entity is busy", r.Problem);
        Assert.Empty(Ended(t));
        // not tried again at once
        Assert.Equal(default, t.Step());
        Assert.Single(w.Moves);
        w.Clock = w.Clock.AddSeconds(NoClipTrack.RetrySeconds + 0.1);
        t.Step();
        Assert.Equal(2, w.Moves.Count);
        Assert.Equal(2, t.Attempts);
        // the second one fails too, and a third lands
        w.Refuse("still busy");
        Assert.NotNull(t.Step().Problem);
        w.Clock = w.Clock.AddSeconds(NoClipTrack.RetrySeconds + 0.1);
        t.Step();
        Assert.Equal(3, w.Moves.Count);
        Assert.True(t.Restoring);
        w.Land();
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)Surface)], Ended(t));
    }

    [Fact]
    public void AMoveThatNeverAnswersHoldsForAsLongAsItTakes()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        for (var i = 0; i < 500; i++)
        {
            w.Clock = w.Clock.AddSeconds(0.04);
            Assert.False(t.Step().Released);
        }
        Assert.True(t.Restoring);
        Assert.Single(w.Moves);                 // no second move on top of one that is still pending
        Assert.Empty(Ended(t));
    }

    [Fact]
    public void AMoveThatFaultsOrCannotStartIsAFailedMove()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        w.Throws = true;
        t.Leaving();
        Assert.True(t.Restoring);               // could not even start: held all the same
        var r = t.Step();
        Assert.False(r.Released);
        Assert.Contains("it failed", r.Problem);
        w.Throws = false;
        w.Clock = w.Clock.AddSeconds(NoClipTrack.RetrySeconds + 0.1);
        t.Step();
        w.Moves[^1].Done.SetException(new InvalidOperationException("lost"));
        Assert.Contains("it failed", t.Step().Problem);
        Assert.True(t.Restoring);
    }

    [Fact]
    public void AMoveThatEndsSomewhereElseInsideIsNotArrival()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        // the service says moved, but the character is far from the target and still in rock
        var (to, done) = w.Moves[^1];
        w.At = new WorldPosition(100, 20, 100);
        done.SetResult(new TeleportResult(TeleportStatus.Moved, new EntityId(7), w.At.Value, to, 30, TeleportFlags.None, 3, ""));
        var r = t.Step();
        Assert.False(r.Released);
        Assert.True(t.Restoring);
        Assert.Contains("it ended at", r.Problem);
    }

    [Fact]
    public void ThePlayerFlyingOutWhileAMoveFailedEndsTheHoldWithoutAPosition()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        w.Refuse("busy");
        t.Step();
        w.At = new WorldPosition(100, 55, 100);  // collision is still off: the player flew up into the open
        var r = t.Step();
        Assert.True(r.Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void DyingDuringTheMoveReleasesIt()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        w.IsDead = true;
        Assert.True(t.Step().Released);
        Assert.False(t.Restoring);
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void ACharacterThatIsGoneIsNotHeld()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        w.At = null;
        Assert.True(t.Step().Released);
        Assert.False(t.Restoring);
    }

    [Fact]
    public void NoFreePositionToGoToMeansNoMove()
    {
        var w = new FakeWorld { Kind = Rock, At = new WorldPosition(100, -700, 100) };  // it began outside the world
        var t = new NoClipTrack(w);
        t.Entering();
        w.At = new WorldPosition(100, -650, 100);
        t.Step();
        t.Leaving();
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);
        Assert.Single(Ended(t));
    }

    [Fact]
    public void DeathAndTeleportAreReportedOnceAndEndWithoutAMove()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        w.IsDead = true;
        Assert.Equal(NoClipEnd.Death, t.Step().Trip);
        t.Leaving();  // the runtime used up the requests and resolved
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);  // the game moves a dead character itself
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));

        (t, w) = Flying();
        w.At = Deep;
        t.Step();
        w.Count = 8;
        w.At = new WorldPosition(900, 30, 900);  // where the game put it; the voxels there say rock (or are not loaded)
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        t.Leaving();
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);  // never pulled back to where it came from
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void ATeleportThatTheRulesNoticeFirstIsStillATeleport()
    {
        // an area rule set: the teleport takes the player out of the area, the rules are resolved, and only then would
        // Step see the counter. The destination's voxels are not loaded yet.
        var (t, w) = Flying();
        w.At = new WorldPosition(100, 60, 100);
        t.Step();
        w.Count = 8;
        w.At = new WorldPosition(4000, 300, 4000);
        w.Kind = _ => NoClipSpace.Unknown;
        t.Leaving();
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void LastSeenIsFromBeforeTheTeleport()
    {
        var (t, w) = Flying();
        w.At = new WorldPosition(120, 60, 100);
        t.Step();
        w.Count = 8;
        w.At = new WorldPosition(4000, 300, 4000);
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        Assert.Equal(new WorldPosition(120, 60, 100), t.LastSeen);  // the runtime uses up the area's request with it
    }

    [Fact]
    public void AskedAgainDuringTheMoveItGoesOnAndEndsOnce()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        Assert.True(t.Restoring);
        // on again before the move lands
        t.Entering();
        Assert.True(t.On);
        Assert.Equal(Surface, t.LastFree);       // not replaced by the place in the rock
        Assert.Equal(default, t.Step());         // the move is still in flight
        // off again, still before it lands: no second move, no report yet
        t.Leaving();
        Assert.True(t.Restoring);
        Assert.Single(w.Moves);
        Assert.Empty(Ended(t));
        w.Land();
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)Surface)], Ended(t));  // once, with where it was put
    }

    [Fact]
    public void AskedAgainAndKeptTheMoveLandsWithoutEndingAnything()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving();
        t.Entering();
        w.Land();                                // the move's own teleport bumps the game's counter
        Assert.Equal(default, t.Step());
        Assert.True(t.On);
        Assert.False(t.Restoring);
        Assert.Empty(Ended(t));
        Assert.Null(t.Step().Trip);              // that teleport was Kiln's move, not one that ends NoClip
        Assert.Equal(Surface, t.LastFree);
        // and a real teleport afterwards still ends it
        w.Count = (byte)(w.Count!.Value + 1);
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
    }

    [Fact]
    public void ItCanBeUsedAgainAfterItEnded()
    {
        var (t, w) = Flying();
        t.Leaving();
        Ended(t);
        w.At = new WorldPosition(10, 60, 10);
        w.Count = 20;
        t.Entering();
        Assert.Equal(w.At, t.LastFree);
        Assert.Null(t.Step().Trip);              // the counter is read anew at each start
        t.Leaving();
        Assert.Single(Ended(t));
    }

    [Fact]
    public void LeavingWithoutEnteringDoesNothing()
    {
        var w = new FakeWorld();
        var t = new NoClipTrack(w);
        t.Leaving();
        Assert.Equal(default, t.Step());
        Assert.Empty(Ended(t));
        Assert.Empty(w.Moves);
    }
}
