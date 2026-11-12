// SPDX-License-Identifier: MIT
// A player's leave in the three orders the leave-order investigation found (out/instances/t3/leave-order.md): the menu
// leave (save while in game, Left, snapshot staging while Leaving, the character sent, the entity destroyed, the items one
// step later), the crash (no leave save; the client keeps the file of the last save it received) and the server
// shutdown. The first three tests are t3's scratch tests, made ours.
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceLeaveTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 5);
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring, level: 2, perksAvailable: 2);

    public void Dispose() => _h.Dispose();

    /// <summary>A world save; the game sends the character back to its client.</summary>
    private void SaveAndSend(uint revision, EntityId player)
    {
        _h.Save(revision);
        _h.World.SendCharacter(player);
    }

    /// <summary>Leave from the menu, in the investigation's order.</summary>
    private void MenuLeave(EntityId player, uint revision)
    {
        var w = _h.World;
        Store.Stage(revision, SaveReason.PlayerLeft, late: false);                    // WorldSavingEvent: still in game
        w.BeginLeave(player);                                                           // kiln.players: Left (Leaving)
        _h.Tick();                                                                      // the store sees it out of game
        Store.Stage(revision, SaveReason.PlayerLeft, late: false, snapshot: true);     // the snapshot staging, Leaving
        Store.Commit(revision, 1000 + revision, SaveReason.PlayerLeft);
        w.SendCharacter(player);                                                        // the final save, acknowledged
        w.DestroyPlayerEntity(player);                                                  // Remove Entity for Player
        _h.Tick();
        w.TearDown(player);                                                             // the items, one step later
        _h.Tick();
    }

    private (EntityId Player, ItemInstanceRef S1, ItemInstanceRef S2) TwinsOnAPlayer()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var s1 = w.Give(p, 10, Sword);
        var s2 = w.Give(p, 11, Sword);
        _h.Tag(s1, 1);
        _h.Tag(s2, 2);
        _h.Tick();
        return (p, s1, s2);
    }

    // ---- t3's tests ----

    [Fact]
    public void CrashLeaveAfterATwinSwapNeverGivesOneTwinTheOthersValue()
    {
        var w = _h.World;
        var (p, s1, s2) = TwinsOnAPlayer();
        SaveAndSend(1, p);                                       // the client's file: s1@10, s2@11
        w.Swap(s1, s2);                                          // seen: s1@11, s2@10
        w.Crash(p);                                              // no leave save reaches the client
        _h.Tick();
        _h.Save(2);
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));              // in the client's file slot 10 is s1
        Assert.Equal(2, _h.Count(w.At(back, 11)));
    }

    [Fact]
    public void CrashLeaveAfterAMoveFindsTheItem()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var s1 = w.Give(p, 10, Sword);
        _h.Tag(s1, 1);
        SaveAndSend(1, p);
        w.Move(s1, p, 14);
        w.Crash(p);
        _h.Tick();
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
        Assert.Empty(_h.Parked());
    }

    [Fact]
    public void ItemEntitiesThatOutliveThePlayerEntityAreStillParkedWithTheCharacter()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var s1 = w.Give(p, 10, Sword);
        _h.Tag(s1, 1);
        SaveAndSend(1, p);
        w.Holders[p.Value].InGame = false;                       // the player entity is gone first
        w.Holders[p.Value].Initialized = false;
        w.Holders.Remove(p.Value);
        w.Move(s1, EntityId.None, -1);                           // a read of the item: no holder any more
        w.Destroy(s1);                                           // and then the item entity goes
        _h.Tick();
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
        Assert.Equal(InstanceParkReason.PlayerLeft, Assert.Single(_h.Of<InstanceDataParkedEvent>()).Reason);
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
    }

    // ---- the menu leave ----

    [Fact]
    public void AMenuLeaveInTheInvestigationsOrderGivesTwinsBackExact()
    {
        var w = _h.World;
        var (p, _, _) = TwinsOnAPlayer();
        SaveAndSend(1, p);
        MenuLeave(p, 2);
        var parked = _h.Of<InstanceDataParkedEvent>();
        Assert.Equal(2, parked.Count);
        Assert.All(parked, e => Assert.Equal(InstanceParkReason.PlayerLeft, e.Reason));
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
        Assert.Equal(2, _h.Count(w.At(back, 11)));
        Assert.All(_h.Of<InstanceDataRestoredEvent>(), e => Assert.Equal(InstanceMatch.Exact, e.Match));
    }

    [Fact]
    public void AMenuLeaveWhoseLeavingPlayerAnswersWithoutItsCharacterLosesNothing()
    {
        var w = _h.World;
        var (p, _, _) = TwinsOnAPlayer();
        SaveAndSend(1, p);
        w.ForgetLeavingCharacter = true;                         // the lookup at the snapshot staging finds no character
        MenuLeave(p, 2);
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
        var records = _h.Bundle(2, $"character-instances/{Hero.Text}/sword-mod")!["holders"]![0]!["records"]!.AsArray();
        Assert.All(records, r => Assert.Equal(2, r!["seen"]!.GetValue<int>()));   // read at the leave save itself
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
        Assert.Equal(2, _h.Count(w.At(back, 11)));
    }

    [Fact]
    public void TwinsSwappedShortlyBeforeALeaveComeBackSetAsideNeverSwapped()
    {
        // The client may come back with the leave save's file or an earlier one it got: both list the same two swords,
        // in other places. Kiln cannot tell which file it is, so the twins are set aside.
        var w = _h.World;
        var (p, s1, s2) = TwinsOnAPlayer();
        SaveAndSend(1, p);
        w.Swap(s1, s2);
        MenuLeave(p, 2);
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(w.At(back, 10)));
        Assert.Null(_h.Count(w.At(back, 11)));
        Assert.Equal(2, _h.Parked().Count(x => x.State == ParkedState.SetAside));
    }

    // ---- crashes ----

    [Fact]
    public void ACrashWithTwinsNotSwappedGivesEachItsOwn()
    {
        var w = _h.World;
        var (p, _, _) = TwinsOnAPlayer();
        SaveAndSend(1, p);
        w.Crash(p);
        _h.Tick();
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
        Assert.Equal(2, _h.Count(w.At(back, 11)));
    }

    [Fact]
    public void ACrashWhenTheClientsFileIsOneSaveOlderAttachesNothingWrong()
    {
        var w = _h.World;
        var (p, s1, s2) = TwinsOnAPlayer();
        SaveAndSend(1, p);                                       // the client has this file
        w.Swap(s1, s2);
        _h.Save(2);                                              // and never got this one
        w.Crash(p);
        _h.Tick();
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(w.At(back, 10)));                   // both saves list the same; not certain: set aside
        Assert.Null(_h.Count(w.At(back, 11)));
    }

    [Fact]
    public void ACrashWithAnOlderButRecognisableFileGivesEachItsOwnFromThatFile()
    {
        var w = _h.World;
        var (p, s1, s2) = TwinsOnAPlayer();
        var ring = w.Give(p, 12, Ring);
        _h.Tag(ring, 3);
        SaveAndSend(1, p);                                       // the client has this file: s1@10, s2@11, ring@12
        w.Swap(s1, s2);
        w.Move(ring, p, 13);                                     // this tells the two saves apart
        _h.Save(2);                                              // the client never got it
        w.Crash(p);
        _h.Tick();
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
        Assert.Equal(2, _h.Count(w.At(back, 11)));
        Assert.Equal(3, _h.Count(w.At(back, 12)));
    }

    [Fact]
    public void AnItemTaggedAfterASaveThatHadItElsewhereIsNotTrustedToTheLaterSlot()
    {
        // Every save reads every player in game, with data or not: the client may come back with a file from before an item
        // got its data, and that file must be one Kiln has read.
        var w = _h.World;
        w.CharacterFiles[Hero] = [(10, Sword), (11, Sword)];
        var p = w.Join(Hero);
        _h.Tick();                                               // the join read: x@10, y@11
        var x = w.At(p, 10);
        var y = w.At(p, 11);
        w.Swap(x, y);                                            // nobody follows them yet: x@11, y@10
        SaveAndSend(1, p);                                       // the client gets this file
        w.Swap(x, y);                                            // back: x@10, y@11
        _h.Tag(x, 1);
        _h.Save(2);                                              // missed by the client
        w.Crash(p);
        _h.Tick();
        var back = w.Join(Hero);                                 // with the file of save 1: slot 10 is y
        _h.Tick();
        Assert.Null(_h.Count(w.At(back, 10)));
        Assert.True(_h.Count(w.At(back, 11)) is null or 1);
    }

    // ---- shutdown with a player online ----

    [Fact]
    public void AShutdownWhoseItemsGoBeforeTheStagingGivesThemBack()
    {
        var w = _h.World;
        var (p, _, _) = TwinsOnAPlayer();
        SaveAndSend(1, p);
        w.BeginLeave(p);                                         // every record to Leaving
        w.DestroyPlayerEntity(p);
        w.TearDown(p);                                           // the items are gone before the shutdown save
        _h.SaveFirstStagingOnly(2, SaveReason.Shutdown);
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
        _h.Restart(2, FakeInstanceWorld.Load(w.SaveGame()));
        var back = _h.World.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(_h.World.At(back, 10)));
        Assert.Equal(2, _h.Count(_h.World.At(back, 11)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShutdownWhoseItemsGoAfterTheStagingGivesThemBackOrSetsThemAside(bool sent)
    {
        var w = _h.World;
        var (p, s1, s2) = TwinsOnAPlayer();
        var ring = w.Give(p, 12, Ring);
        _h.Tag(ring, 3);
        SaveAndSend(1, p);
        w.Move(ring, p, 13);
        w.BeginLeave(p);
        _h.SaveFirstStagingOnly(2, SaveReason.Shutdown);         // the items still read, in their player
        if (sent) w.SendCharacter(p);                            // the shutdown save reached the client, or not
        w.DestroyPlayerEntity(p);
        _h.Restart(2, FakeInstanceWorld.Load(w.SaveGame()));
        var back = _h.World.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(_h.World.At(back, 10)));
        Assert.Equal(2, _h.Count(_h.World.At(back, 11)));
        Assert.Equal(3, _h.Count(_h.World.At(back, sent ? 13 : 12)));
        _ = (s1, s2);
    }

    // ---- D2: a read that places an item nowhere changes nothing ----

    [Fact]
    public void ASaveThatReadsTheItemsPlacedNowhereKeepsTheirLastPlaceAndObservation()
    {
        var w = _h.World;
        var (p, _, _) = TwinsOnAPlayer();
        SaveAndSend(1, p);
        w.DestroyPlayerEntity(p);                                // a crash; the items exist one more step, placed nowhere
        _h.Save(2);                                              // and a save lands in that step
        var records = _h.Bundle(2, $"character-instances/{Hero.Text}/sword-mod")!["holders"]![0]!["records"]!.AsArray();
        Assert.Equal([10, 11], records.Select(r => r!["slot"]!.GetValue<int>()).Order());
        Assert.All(records, r => Assert.Equal(1, r!["seen"]!.GetValue<int>()));
        w.TearDown(p);
        _h.Tick();
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
        Assert.Equal(2, _h.Of<InstanceDataParkedEvent>().Count);
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 10)));
        Assert.Equal(2, _h.Count(w.At(back, 11)));
    }

    // ---- D3: a leaving player's items keep their character ----

    [Fact]
    public void AKnownCharacterIsNotReplacedByNoneWhileTheItemStaysInTheSameHolder()
    {
        var known = HolderIdentity.OfPlayer(Hero, "p");
        var none = HolderIdentity.OfPlayer(default, "p");
        var accountOnly = HolderIdentity.OfPlayer(new CharacterKey(7, Guid.Empty), "p");
        var chest = HolderIdentity.OfWorld(ChestTemplate, A, false);
        Assert.Equal(known, InstanceStore.Kept(known, sameHolder: true, none));
        Assert.Equal(known, InstanceStore.Kept(known, sameHolder: true, accountOnly));
        Assert.Equal(known, InstanceStore.Kept(known, sameHolder: true, HolderIdentity.None));
        Assert.Equal(chest, InstanceStore.Kept(chest, sameHolder: true, HolderIdentity.None));
        Assert.Equal(none, InstanceStore.Kept(known, sameHolder: false, none));       // another holder: its own answer
        Assert.Equal(known, InstanceStore.Kept(none, sameHolder: true, known));        // learning it is fine
    }

    [Fact]
    public void UnbindingALeavingPlayerKeepsItsRecordsAndItsLiveItems()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var ring = w.Give(p, 12, Ring);
        _h.Tag(ring, 3);
        SaveAndSend(1, p);
        w.Crash(p);
        _h.Tick();
        w.CharacterFiles[Hero] = [];                             // the ring does not come back with this file: it waits
        var back = w.Join(Hero);
        var s1 = w.Give(back, 10, Sword);
        _h.Tag(s1, 1);
        _h.Tick();
        Assert.Equal(ParkedState.Waiting, Assert.Single(_h.Parked()).State);
        var entry = Store.Instances.Holders.Single(h => h.Key.Kind == HolderKind.Player);
        Assert.True(entry.IsBound);
        w.BeginLeave(back);                                      // Leaving: out of the in-game list
        _h.Tick();
        Assert.False(entry.IsBound);                             // CheckBound lets go of the entity ...
        Assert.Single(entry.Records);                            // ... the waiting record stays with the character
        Assert.NotNull(Store.Instances.LiveOf(s1));             // ... and the live item stays live
        _h.Save(2);                                              // the leave save's staging, while Leaving
        var records = _h.Bundle(2, $"character-instances/{Hero.Text}/sword-mod")!["holders"]![0]!["records"]!.AsArray();
        Assert.Equal(["live", "waiting"], records.Select(r => r!["state"]!.GetValue<string>()).Order());
    }
}
