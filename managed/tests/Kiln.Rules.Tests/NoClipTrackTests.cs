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
        public Func<WorldPosition, ulong>? Shape;  // the cells there; by default they change when the kind of place does
        public ulong Cells(WorldPosition at) => Shape?.Invoke(at) ?? (ulong)Kind(at);
        public Func<WorldPosition, NoClipSpace>? Upper;  // the waist and the head; by default as the whole body
        public NoClipSpace Torso(WorldPosition at) => Upper?.Invoke(at) ?? Kind(at);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
        w.At = null;
        Assert.True(t.Step().Released);
        Assert.False(t.Restoring);
    }

    [Fact]
    public void WithNowhereToGoBackToCollisionWaitsUntilTheCharacterIsFree()
    {
        var w = new FakeWorld { Kind = Rock, At = new WorldPosition(100, -700, 100) };  // it began outside the world
        var t = new NoClipTrack(w);
        t.Entering();
        w.At = new WorldPosition(100, -650, 100);
        t.Step();
        t.Leaving(out _);
        Assert.True(t.Restoring);               // inside something: collision does not come back, move or no move
        Assert.Empty(w.Moves);
        for (var i = 0; i < 100; i++)
        {
            w.Clock = w.Clock.AddSeconds(0.5);
            Assert.False(t.Step().Released);
        }
        Assert.Empty(w.Moves);                  // there is no place Kiln may move it to
        Assert.Empty(Ended(t));
        w.At = new WorldPosition(100, 60, 100); // the player flew out
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void LandingAtTheTargetIsNotEnoughWhenThePlaceIsNoLongerFree()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Kind = _ => NoClipSpace.Inside;       // someone built over the last free position meanwhile
        w.Land();
        var r = t.Step();
        Assert.False(r.Released);               // at the target, and inside something there
        Assert.True(t.Restoring);
        Assert.Contains("inside something there", r.Problem);
        Assert.Empty(Ended(t));
        // the same move again would change nothing: it is not repeated, and it is said once
        for (var i = 0; i < 10; i++)
        {
            w.Clock = w.Clock.AddSeconds(NoClipTrack.RetrySeconds + 0.1);
            var again = t.Step();
            Assert.False(again.Released);
            Assert.Null(again.Problem);
        }
        Assert.Single(w.Moves);
        Assert.True(t.Restoring);
        w.Kind = Rock;                          // what was built there is taken away again
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)Surface)], Ended(t));  // it is where the move put it
    }

    // A slope: where the character stands there is matter at knee height beside it, so the cells never read as empty
    // there, and below y 49 is rock. The cells' own number only changes when someone builds.
    private static (NoClipTrack, FakeWorld) FlyingFromASlope()
    {
        var w = new FakeWorld
        {
            Kind = p => p.Y < 52 ? NoClipSpace.Inside : NoClipSpace.Free,
            Upper = Rock,                                           // waist and head are in the open on the slope
            Shape = p => Math.Abs(p.X - 100) < 0.25 ? 77ul : 78ul,  // the next cell along the slope looks different
        };
        var t = new NoClipTrack(w);
        t.Entering();
        return (t, w);
    }

    [Fact]
    public void WhereNoClipBeganIsFreeBecauseCollisionWasOnThere()
    {
        // on and off again without moving: nothing to move back from, whatever the cells say
        var (t, w) = FlyingFromASlope();
        t.Step();
        Assert.Null(t.Leaving(out _));
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void PutBackWhereNoClipBeganTheCharacterIsReleasedThere()
    {
        // seen in a lab: the starting place on sloped ground was the last free position, and its cells are not empty
        var (t, w) = FlyingFromASlope();
        w.At = Deep;
        t.Step();
        Assert.Equal(Surface, t.LastFree);
        t.Leaving(out _);
        Assert.True(t.Restoring);
        Assert.Equal(Surface, Assert.Single(w.Moves).To);
        w.Land();
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)Surface)], Ended(t));
        Assert.Single(w.Moves);
    }

    [Fact]
    public void WhereNoClipBeganNoLongerCountsOnceSomethingWasBuiltThere()
    {
        var (t, w) = FlyingFromASlope();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Shape = _ => 99;                      // a block was placed at the starting place meanwhile
        w.Land();
        var r = t.Step();
        Assert.False(r.Released);
        Assert.True(t.Restoring);
        Assert.Contains("inside something there", r.Problem);
        w.At = new WorldPosition(100, 60, 100); // the player flies out
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void AStepAwayFromWhereNoClipBeganTheCellsDecideAgain()
    {
        var (t, w) = FlyingFromASlope();
        w.At = new WorldPosition(100.5, 50, 100);  // half a metre along the slope, still in matter by the cells
        t.Step();
        t.Leaving(out _);
        Assert.True(t.Restoring);
        Assert.Equal(Surface, Assert.Single(w.Moves).To);
    }

    [Fact]
    public void CloseToWhereNoClipBeganButInCellsThatLookDifferentIsNotThatPlace()
    {
        // 28 cm sideways is within reach of the starting place's word, but the character is in the next cell (a wall
        // beside the start would be exactly this): the word is for the place, not for its surroundings
        var (t, w) = FlyingFromASlope();
        w.At = new WorldPosition(100.28, 50, 100);
        t.Step();
        t.Leaving(out _);
        Assert.True(t.Restoring);
        w.Land();                                // back at the starting place itself
        Assert.True(t.Step().Released);
        // and a few centimetres within the same cells is that place
        (t, w) = FlyingFromASlope();
        w.At = new WorldPosition(100.1, 50, 100);
        t.Step();
        Assert.Null(t.Leaving(out _));
        Assert.False(t.Restoring);
    }

    [Fact]
    public void ACharacterThatWasNotStandingWhenNoClipBeganGetsNoWordForThatPlace()
    {
        // NoClip begins with the character already in matter up to the waist (it got there some other way): that
        // collision was on then says nothing. Ended there without moving, collision waits.
        var w = new FakeWorld { Kind = _ => NoClipSpace.Inside, Shape = _ => 5 };
        var t = new NoClipTrack(w);
        t.Entering();
        t.Step();
        Assert.Null(t.Leaving(out _));
        Assert.True(t.Restoring);
        w.Land();                                // the move to where it began lands: still in matter there
        var r = t.Step();
        Assert.False(r.Released);
        Assert.Contains("inside something there", r.Problem);
        w.Kind = Rock;                           // it gets out
        w.At = new WorldPosition(100, 60, 100);
        Assert.True(t.Step().Released);
        Assert.Single(w.Moves);
    }

    [Fact]
    public void AnUnknownPlaceIsNotAFreeOne()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Kind = _ => NoClipSpace.Unknown;      // the voxels at the target are not loaded
        w.Land();
        Assert.False(t.Step().Released);
        Assert.True(t.Restoring);
    }

    [Fact]
    public void FlyingOutWhileTheMoveIsStillOutWaitsForItsAnswer()
    {
        // a move cannot be called off: where it lands is looked at with collision still off
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.At = new WorldPosition(100, 55, 100); // free, with the move not answered yet
        for (var i = 0; i < 10; i++) Assert.False(t.Step().Released);
        Assert.True(t.Restoring);
        w.Land();                               // it lands at the last free position
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)Surface)], Ended(t));
        Assert.Single(w.Moves);
    }

    [Fact]
    public void FlyingOutAndAMoveThatFailsReleasesWhereTheCharacterIs()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.At = new WorldPosition(100, 55, 100);
        Assert.False(t.Step().Released);
        w.Refuse("busy");
        Assert.True(t.Step().Released);         // answered, and the character is in the open: no second move
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
        Assert.Single(w.Moves);
    }

    [Fact]
    public void EndedInTheOpenWithAMoveOfAnEarlierHoldStillOutWaitsForThatMoveToo()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);                        // hold, move 1 out
        t.Entering();                            // on again
        w.At = new WorldPosition(100, 70, 100);  // flies into the open
        t.Step();
        t.Leaving(out _);                        // off again, in the open, with move 1 still out
        Assert.True(t.Restoring);                // not finished: the move may land anywhere
        Assert.Empty(Ended(t));
        w.Land();
        Assert.True(t.Step().Released);
        Assert.Single(Ended(t));
        Assert.Single(w.Moves);
    }

    [Fact]
    public void AMoveStillOutAfterADeathIsNeitherDoubledNorTakenForATeleport()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.IsDead = true;
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));
        // respawned, NoClip on again, inside something again, off again: the first move has still not answered
        w.IsDead = false;
        w.At = new WorldPosition(500, 60, 500);
        t.Entering();
        Assert.Null(t.Step().Trip);
        w.At = new WorldPosition(500, 40, 500);
        t.Step();
        t.Leaving(out _);
        Assert.True(t.Restoring);
        Assert.Single(w.Moves);                 // no second move on top of the one that is out
        // the old move lands (at the old place, far from this hold's): Kiln's own teleport, not the game's
        w.Land();
        var r = t.Step();
        Assert.True(r.Released);                // the old place is free, and free is what counts
        Assert.Equal([(NoClipEnd.RuleEnded, (WorldPosition?)null)], Ended(t));
        t.Entering();
        Assert.Null(t.Step().Trip);
    }

    [Fact]
    public void ATeleportByTheGameDuringTheHoldEndsItWhereTheGamePutTheCharacter()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Refuse("busy");
        t.Step();                               // waiting for the next try
        w.Count = 8;                            // fast travel
        w.At = new WorldPosition(4000, 300, 4000);
        w.Kind = _ => NoClipSpace.Unknown;      // not loaded there yet
        Assert.True(t.Step().Released);
        Assert.False(t.Restoring);
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
        w.Clock = w.Clock.AddSeconds(5);
        t.Step();
        Assert.Single(w.Moves);                 // never pulled back
    }

    [Fact]
    public void ATeleportWhileNoClipWasAskedAgainDuringAHoldStillEndsIt()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Refuse("busy");
        t.Step();
        t.Entering();                           // on again during the hold
        w.Count = 8;                            // and then the game teleports the character
        w.At = new WorldPosition(4000, 300, 4000);
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);  // the hold is over (NoClip is on again), the teleport is seen
        Assert.Equal(Deep, t.LastSeen);         // from before the teleport: the area it left has its request used up
        Assert.False(t.Restoring);
    }

    [Fact]
    public void ATeleportDuringARestartedHoldIsToldEvenWhenTheRulesNoticeItFirst()
    {
        // hold, NoClip asked again, a teleport out of the rule's area, and the rules are resolved before any step
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Refuse("busy");
        t.Step();
        t.Entering();
        w.Count = 8;
        w.At = new WorldPosition(4000, 300, 4000);
        w.Kind = _ => NoClipSpace.Unknown;
        Assert.Equal(NoClipEnd.Teleport, t.Leaving(out var before));  // the runtime uses up the requests
        Assert.Equal(Deep, before);
        Assert.True(t.Step().Released);          // the hold that was running ends there, as a teleport
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
        Assert.Single(w.Moves);
    }

    [Fact]
    public void ADeathDuringARestartedHoldIsToldToo()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        t.Entering();
        w.IsDead = true;
        Assert.Equal(NoClipEnd.Death, t.Leaving(out _));
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void SomeoneElsesTeleportWhileAMoveIsOutIsNotTakenForTheMoveAndNothingPullsThePlayerBack()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);                        // move 1 to the surface is out
        w.Count = 8;                             // fast travel meanwhile
        w.At = new WorldPosition(4000, 300, 4000);
        w.Kind = p => p.X > 3000 ? NoClipSpace.Unknown : Rock(p);
        Assert.False(t.Step().Released);         // the move has not answered: whose teleport it was is not known yet
        // the move answers "moved" (the game counts any teleport), but the character is not where Kiln sent it
        var (to, done) = w.Moves[^1];
        done.SetResult(new TeleportResult(TeleportStatus.Moved, new EntityId(7), w.At!.Value, to, 0, TeleportFlags.None, 3, ""));
        var r = t.Step();
        Assert.True(r.Released);
        Assert.Null(r.Problem);                  // not a failed move to try again
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
        w.Clock = w.Clock.AddSeconds(5);
        t.Step();
        Assert.Single(w.Moves);                  // no second move to the old place
    }

    [Fact]
    public void SomeoneElsesTeleportWhileAMoveIsOutAndNoClipIsOnAgainEndsNoClip()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        t.Entering();                            // on again, with the move still out
        Assert.Equal(default, t.Step());
        w.Count = 8;
        w.At = new WorldPosition(4000, 300, 4000);
        Assert.Equal(default, t.Step());         // not known yet whose teleport
        Assert.Equal(Deep, t.LastSeen);          // and the new place is not taken for one it flew to
        var (to, done) = w.Moves[^1];
        done.SetResult(new TeleportResult(TeleportStatus.Moved, new EntityId(7), w.At!.Value, to, 0, TeleportFlags.None, 3, ""));
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        Assert.Equal(Deep, t.LastSeen);
    }

    [Fact]
    public void AMoveThatLandsAfterADeathInsideSomethingTurnsCollisionOffAgainQuietly()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);                        // move 1 to the surface is out
        w.IsDead = true;                         // only a death ends things with a move out
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));
        // respawned elsewhere; someone builds over the old place, and then the move lands there
        w.IsDead = false;
        w.At = new WorldPosition(140, 55, 100);
        Assert.Equal(default, t.Step());
        w.Kind = p => Math.Abs(p.X - 100) < 2 ? NoClipSpace.Inside : Rock(p);
        w.Land();
        var r = t.Step();
        Assert.True(r.Held);                     // the hooks hear that collision is off again
        Assert.True(t.Restoring);
        Assert.Contains("landed late", r.Problem);
        Assert.Single(w.Moves);                  // nowhere to move it: it waits
        Assert.False(t.Step().Released);
        w.At = new WorldPosition(104, 55, 100);  // the player flies out of the new block
        Assert.True(t.Step().Released);
        Assert.Empty(Ended(t));                  // NoClip's end was reported once, before
        Assert.False(t.Restoring);
        // and it can be used again as if nothing had been
        t.Entering();
        Assert.Equal(w.At, t.LastFree);
        Assert.Null(t.Step().Trip);
    }

    [Fact]
    public void AMoveThatLandsAfterADeathOnTheUnchangedSlopeItCameFromHoldsNothing()
    {
        // the starting place on a slope keeps its word until the move that may land there has landed
        var (t, w) = FlyingFromASlope();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);                        // move 1 to the starting place is out
        w.IsDead = true;
        Assert.True(t.Step().Released);
        w.IsDead = false;
        w.At = new WorldPosition(140, 60, 100);
        t.Step();
        w.Land();                                // back at the starting place: knee matter, as when it stood there
        var r = t.Step();
        Assert.False(r.Held);
        Assert.False(t.Restoring);
        Assert.Single(Ended(t));                 // the death, nothing else
    }

    [Fact]
    public void LeavingDoesNotTakeTheCounterWhileAMoveIsOut()
    {
        // restart during a pending move, a step, then someone else's teleport out of the rule's area, and the rules are
        // resolved first. The move's answer must still be judged against the count from before it.
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);                        // move 1 out
        t.Entering();
        t.Step();                                // the hold is over, NoClip is on, the move still out
        w.Count = 8;
        w.At = new WorldPosition(4000, 300, 4000);
        w.Kind = p => p.X > 3000 ? NoClipSpace.Unknown : Rock(p);
        Assert.Null(t.Leaving(out _));           // whose teleport it was is not known yet
        Assert.True(t.Restoring);
        var (to, done) = w.Moves[^1];
        done.SetResult(new TeleportResult(TeleportStatus.Moved, new EntityId(7), w.At!.Value, to, 0, TeleportFlags.None, 3, ""));
        var r = t.Step();
        Assert.Equal(NoClipEnd.Teleport, r.Trip);  // now it is known: the runtime uses up the requests
        Assert.True(r.Released);
        Assert.Null(r.Problem);                  // not a failed move
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
        w.Clock = w.Clock.AddSeconds(5);
        t.Step();
        Assert.Single(w.Moves);                  // and nothing pulls the player back
    }

    [Fact]
    public void ATeleportToldThroughLeavingIsNotToldAgainToARequestMadeAfterIt()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Refuse("busy");
        t.Step();
        t.Entering();                            // on again during the hold
        w.Count = 8;
        w.At = new WorldPosition(4000, 60, 4000);
        Assert.Equal(NoClipEnd.Teleport, t.Leaving(out _));  // told once
        Assert.Null(t.Leaving(out _));           // not on: nothing to tell
        t.Entering();                            // asked anew, after the teleport, before any step
        Assert.Null(t.Leaving(out _));           // and ended again: the same teleport is not told twice
        t.Entering();
        Assert.Null(t.Step().Trip);              // NoClip goes on where the character is now
        Assert.True(t.On);
        Assert.False(t.Restoring);
        w.Count = 9;                             // a teleport after that ends it as always
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
    }

    [Fact]
    public void DeathAndTeleportAreReportedOnceAndEndWithoutAMove()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        w.IsDead = true;
        Assert.Equal(NoClipEnd.Death, t.Step().Trip);
        t.Leaving(out _);  // the runtime used up the requests and resolved
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);  // the game moves a dead character itself
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));

        (t, w) = Flying();
        w.At = Deep;
        t.Step();
        w.Count = 8;
        w.At = new WorldPosition(900, 60, 900);  // where the game put it
        w.Kind = _ => NoClipSpace.Unknown;       // not loaded there yet: the game's to handle
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        t.Leaving(out _);
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);  // never pulled back to where it came from
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void ATeleportOntoASlopeEndsItAtOnce()
    {
        // fast travel puts the character on sloped ground: matter at its knees, as wherever a character stands
        var (t, w) = Flying();
        w.Count = 8;
        w.At = new WorldPosition(900, 50, 900);
        w.Kind = _ => NoClipSpace.Inside;
        w.Upper = _ => NoClipSpace.Free;
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        t.Leaving(out _);
        Assert.False(t.Restoring);
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void ATeleportIntoSolidGroundKeepsCollisionOffThereWithoutMovingTheCharacterBack()
    {
        // a mod teleports the character into rock (a place it did not check)
        var (t, w) = Flying();
        w.At = new WorldPosition(100, 60, 100);
        t.Step();
        w.Count = 8;
        w.At = new WorldPosition(900, 30, 900);
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        Assert.Null(t.Leaving(out _));           // the step told the runtime already
        Assert.True(t.Restoring);                // collision would drop it through the world there
        Assert.Empty(Ended(t));
        for (var i = 0; i < 5; i++)
        {
            w.Clock = w.Clock.AddSeconds(2);
            Assert.False(t.Step().Released);
        }
        Assert.Empty(w.Moves);                   // it stays where the teleport put it
        w.At = new WorldPosition(900, 55, 900);  // the player flies up out of it
        Assert.True(t.Step().Released);
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void ATeleportIntoSolidGroundDuringAHoldIsHeldThereToo()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        t.Leaving(out _);
        w.Refuse("busy");
        t.Step();
        w.Count = 8;
        w.At = new WorldPosition(900, 30, 900);  // into rock again, by someone else's teleport
        Assert.False(t.Step().Released);
        Assert.True(t.Restoring);
        w.Clock = w.Clock.AddSeconds(5);
        t.Step();
        Assert.Single(w.Moves);                  // no move back to the old place, now or later
        w.At = new WorldPosition(900, 55, 900);
        Assert.True(t.Step().Released);
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
        // the runtime is told, with where the character was before: the area's request is used up like any other
        Assert.Equal(NoClipEnd.Teleport, t.Leaving(out var before));
        Assert.Equal(new WorldPosition(100, 60, 100), before);
        Assert.False(t.Restoring);
        Assert.Empty(w.Moves);
        Assert.Equal([(NoClipEnd.Teleport, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void ADeathThatTheRulesNoticeFirstIsToldToo()
    {
        var (t, w) = Flying();
        w.At = Deep;
        t.Step();
        w.IsDead = true;
        Assert.Equal(NoClipEnd.Death, t.Leaving(out _));
        Assert.False(t.Restoring);
        Assert.Equal([(NoClipEnd.Death, (WorldPosition?)null)], Ended(t));
    }

    [Fact]
    public void WhatTheStepReportedIsNotToldASecondTime()
    {
        var (t, w) = Flying();
        w.Count = 8;
        Assert.Equal(NoClipEnd.Teleport, t.Step().Trip);
        Assert.Null(t.Leaving(out _));          // the runtime used the requests up when the step said so
        (t, w) = Flying();
        w.At = new WorldPosition(100, 70, 100);
        t.Step();
        Assert.Null(t.Leaving(out _));          // a plain end has nothing to use up
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
        t.Leaving(out _);
        Assert.True(t.Restoring);
        // on again before the move lands
        t.Entering();
        Assert.True(t.On);
        Assert.Equal(Surface, t.LastFree);       // not replaced by the place in the rock
        Assert.Equal(default, t.Step());         // the move is still in flight
        // off again, still before it lands: no second move, no report yet
        t.Leaving(out _);
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
        t.Leaving(out _);
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
        t.Leaving(out _);
        Ended(t);
        w.At = new WorldPosition(10, 60, 10);
        w.Count = 20;
        t.Entering();
        Assert.Equal(w.At, t.LastFree);
        Assert.Null(t.Step().Trip);              // the counter is read anew at each start
        t.Leaving(out _);
        Assert.Single(Ended(t));
    }

    [Fact]
    public void LeavingWithoutEnteringDoesNothing()
    {
        var w = new FakeWorld();
        var t = new NoClipTrack(w);
        t.Leaving(out _);
        Assert.Equal(default, t.Step());
        Assert.Empty(Ended(t));
        Assert.Empty(w.Moves);
    }
}
