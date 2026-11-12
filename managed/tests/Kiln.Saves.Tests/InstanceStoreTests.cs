// SPDX-License-Identifier: MIT
// The store against the fake game: saves, loads, kill -9, a restored generation, an older generation, holders that are never
// found or not certain, a mod that is not loaded, the caps, a mod's unload and reload, destroyed items, players leaving and
// coming back, and the end of a session.
using System.Text.Json.Nodes;
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceStoreTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 5);
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring, level: 2, perksAvailable: 2);

    public void Dispose() => _h.Dispose();

    // ---- the dev.20 case: two identical swords ----

    [Fact]
    public void TwoIdenticalSwordsKeepTheirOwnValuesThroughSwapsMovesSaveAndRestart()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var b = w.Chest(ChestTemplate, B);
        var s1 = w.Give(a, 3, Sword);
        var s2 = w.Give(a, 5, Sword);
        _h.Tag(s1, 111);
        _h.Tag(s2, 222);
        w.Swap(s1, s2);                 // in the same chest
        w.Move(s1, b, 0);               // to another chest
        w.Move(s1, a, 7);               // and back, to another slot
        _h.Save(1);
        Assert.Equal(DataMatch.Exact, _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame())));
        var na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(222, _h.Count(_h.World.At(na, 3)));   // s2 was swapped into slot 3
        Assert.Equal(111, _h.Count(_h.World.At(na, 7)));   // s1 went to B and came back to slot 7
        Assert.Equal(2, _h.Of<InstanceDataRestoredEvent>().Count(e => e.Match == InstanceMatch.Exact));
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
    }

    [Fact]
    public void TwoIdenticalSwordsMovedToAnotherChestAndBackAcrossTwoRestarts()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        w.Chest(ChestTemplate, B);
        _h.Tag(w.Give(a, 3, Sword), 1);
        _h.Tag(w.Give(a, 5, Sword), 2);
        _h.Save(1);
        _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame()));
        w = _h.World;
        var na = w.HolderAt(ChestTemplate, A);
        var nb = w.HolderAt(ChestTemplate, B);
        var x = w.At(na, 3);
        var y = w.At(na, 5);
        w.Move(x, nb, 1);
        w.Move(y, nb, 2);
        _h.Save(2);
        _h.Restart(2, FakeInstanceWorld.Load(w.SaveGame()));
        w = _h.World;
        nb = w.HolderAt(ChestTemplate, B);
        na = w.HolderAt(ChestTemplate, A);
        Assert.Equal(1, _h.Count(w.At(nb, 1)));
        Assert.Equal(2, _h.Count(w.At(nb, 2)));
        w.Move(w.At(nb, 2), na, 0);
        w.Move(w.At(nb, 1), na, 4);
        _h.Save(3);
        _h.Restart(3, FakeInstanceWorld.Load(w.SaveGame()));
        na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(2, _h.Count(_h.World.At(na, 0)));
        Assert.Equal(1, _h.Count(_h.World.At(na, 4)));
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
    }

    [Fact]
    public void KillNineBeforeTheWorldWriteGivesTheOldGenerationAndItsOwnItems()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var s1 = w.Give(a, 3, Sword);
        var s2 = w.Give(a, 5, Sword);
        _h.Tag(s1, 10);
        _h.Tag(s2, 20);
        _h.Save(1);
        var file = w.SaveGame();                 // the world file of revision 1
        w.Swap(s1, s2);
        _h.Get(s1).Count = 11;
        _h.StageOnly(2);                         // kill -9 after staging generation 2, before the world write
        Assert.Equal(DataMatch.Exact, _h.Restart(1, FakeInstanceWorld.Load(file)));
        var na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(10, _h.Count(_h.World.At(na, 3)));
        Assert.Equal(20, _h.Count(_h.World.At(na, 5)));
    }

    [Fact]
    public void KillNineAfterTheWorldWriteGivesTheStagedGenerationTrusted()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var s1 = w.Give(a, 3, Sword);
        var s2 = w.Give(a, 5, Sword);
        _h.Tag(s1, 10);
        _h.Tag(s2, 20);
        _h.Save(1);
        w.Swap(s1, s2);
        _h.StageOnly(2);                         // the world file of revision 2 is written, Kiln never confirms it
        Assert.Equal(DataMatch.Staged, _h.Restart(2, FakeInstanceWorld.Load(w.SaveGame()), unix: 9999));
        var na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(20, _h.Count(_h.World.At(na, 3)));
        Assert.Equal(10, _h.Count(_h.World.At(na, 5)));
    }

    [Fact]
    public void ARestoredOlderGenerationBringsBackItsOwnValuesOnItsOwnItems()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var s1 = w.Give(a, 3, Sword);
        var s2 = w.Give(a, 5, Sword);
        _h.Tag(s1, 1);
        _h.Tag(s2, 2);
        _h.Save(1);
        var backup = w.SaveGame();
        w.Swap(s1, s2);
        _h.Get(s1).Count = 100;
        _h.Save(2);
        Assert.Equal(DataMatch.Exact, _h.Restart(1, FakeInstanceWorld.Load(backup)));   // the backup of revision 1 is restored
        var na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(1, _h.Count(_h.World.At(na, 3)));
        Assert.Equal(2, _h.Count(_h.World.At(na, 5)));
    }

    [Fact]
    public void UnderAnOlderGenerationTwinsAreSetAsideAndStaySetAsideLater()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var s1 = w.Give(a, 3, Sword);
        var s2 = w.Give(a, 5, Sword);
        _h.Tag(s1, 1);
        _h.Tag(s2, 2);
        _h.Save(1);
        w.Swap(s1, s2, seen: false);             // played without Kiln: the world file is newer than any generation
        w.Give(a, 9, Ring);
        Assert.Equal(DataMatch.Older, _h.Restart(4, FakeInstanceWorld.Load(w.SaveGame())));
        var na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Null(_h.Count(_h.World.At(na, 3)));
        Assert.Null(_h.Count(_h.World.At(na, 5)));
        var lost = _h.Of<InstanceDataLostEvent>();
        Assert.Equal(2, lost.Count);
        Assert.All(lost, e => Assert.Equal(InstanceLossReason.Ambiguous, e.Reason));
        Assert.All(_h.Parked(), p => Assert.Equal(ParkedState.SetAside, p.State));
        Assert.Equal([1, 2], _h.Parked().Select(p => p.Value!.Count).Order());
        // saved as set aside; the next load, now with the world file's own generation, does not attach them either
        _h.Save(5);
        _h.Events.Clear();
        Assert.Equal(DataMatch.Exact, _h.Restart(5, FakeInstanceWorld.Load(_h.World.SaveGame())));
        na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Null(_h.Count(_h.World.At(na, 3)));
        Assert.Null(_h.Count(_h.World.At(na, 5)));
        Assert.Empty(_h.Of<InstanceDataRestoredEvent>());
        Assert.Equal(2, _h.Parked().Count(p => p.State == ParkedState.SetAside));
    }

    [Fact]
    public void UnderAnOlderGenerationTheOneItemThatFitsGetsItsValueMoved()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var ring = w.Give(a, 3, Ring);
        w.Give(a, 4, Sword);
        _h.Tag(ring, 7);
        _h.Save(1);
        w.Move(ring, a, 12, seen: false);
        Assert.Equal(DataMatch.Older, _h.Restart(3, FakeInstanceWorld.Load(w.SaveGame())));
        var na = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(7, _h.Count(_h.World.At(na, 12)));
        Assert.Equal(InstanceMatch.Moved, Assert.Single(_h.Of<InstanceDataRestoredEvent>()).Match);
    }

    [Fact]
    public void AnItemMovedUnseenIsSavedWhereItIsNow()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var ring = w.Give(a, 3, Ring);
        _h.Tag(ring, 7);
        w.Move(ring, a, 6, seen: false);         // the tracker missed it: the save reads it fresh
        _h.Save(1);
        var rec = _h.Bundle(1, "instances/sword-mod")!["holders"]![0]!["records"]![0]!;
        Assert.Equal(6, rec["slot"]!.GetValue<int>());
        Assert.Equal(1, rec["seen"]!.GetValue<int>());
    }

    // ---- holders ----

    [Fact]
    public void AHolderNeverFoundIsKeptThreeSessionsThenDropped()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        var gone = new FakeInstanceWorld.Snapshot([], []);       // the chest is not in the world any more
        for (uint s = 1; s <= 3; s++)
        {
            _h.Events.Clear();
            _h.Restart(s, FakeInstanceWorld.Load(gone));
            _h.EndWindow();
            if (s < 3)
            {
                Assert.Equal(InstanceParkReason.HolderAway, Assert.Single(_h.Of<InstanceDataParkedEvent>()).Reason);
                Assert.Equal(ParkedState.Waiting, Assert.Single(_h.Parked()).State);
                Assert.Equal((int)s, _h.Parked()[0].SessionsMissed);
            }
            else
            {
                Assert.Equal(InstanceLossReason.Unmatched, Assert.Single(_h.Of<InstanceDataLostEvent>()).Reason);
                Assert.Empty(_h.Parked());
            }
            _h.Save(s + 1);
        }
        Assert.Null(_h.Bundle(4, "instances/sword-mod"));
    }

    [Fact]
    public void AHolderFoundAgainStartsItsMissedSessionsOver()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        var away = new FakeInstanceWorld.Snapshot([], []);
        for (uint s = 1; s <= 2; s++)
        {
            _h.Restart(s, FakeInstanceWorld.Load(away));
            _h.EndWindow();
            _h.Save(s + 1);
        }
        Assert.Equal(2, _h.Parked()[0].SessionsMissed);
        // the chest is back, but the ring is not in it: the holder is found, the record waits for its item
        var back = new FakeInstanceWorld.Snapshot([(HolderIdentity.OfWorld(ChestTemplate, A, false, "Chest"), [(0, Rec.Of(Rec.Sword))])], []);
        _h.Restart(3, FakeInstanceWorld.Load(back));
        Assert.Equal(InstanceParkReason.NoCandidate, _h.Of<InstanceDataParkedEvent>().Last().Reason);
        _h.Save(4);
        Assert.Null(_h.Bundle(4, "instances/sword-mod")!["holders"]![0]!["missed"]);
        _h.Events.Clear();
        _h.Restart(4, FakeInstanceWorld.Load(away));
        _h.EndWindow();
        Assert.Empty(_h.Of<InstanceDataLostEvent>());           // not dropped: the holder missed one session since it was found
        Assert.Equal(1, Assert.Single(_h.Parked()).SessionsMissed);
    }

    [Fact]
    public void AHolderThatStreamsInLateIsMatchedWithinTheWindow()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        var loaded = FakeInstanceWorld.Load(w.SaveGame(), streamed: false);
        _h.Restart(1, loaded);
        Assert.Empty(_h.Of<InstanceDataRestoredEvent>());   // the inventory is not initialized yet
        var na = loaded.HolderAt(ChestTemplate, A);
        loaded.Holders[na.Value].Initialized = true;
        loaded.Step += 25;
        _h.Tick();
        Assert.Equal(5, _h.Count(loaded.At(na, 3)));
    }

    [Fact]
    public void TwoEntitiesOfTheTemplateInTheRadiusSetTheHoldersRecordsAside()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        var loaded = FakeInstanceWorld.Load(w.SaveGame(), streamed: false);   // not matched before the second one is there
        var twin = loaded.Chest(ChestTemplate, new WorldPosition(A.X + 0.3, A.Y, A.Z));
        loaded.Give(twin, 3, Ring);
        _h.Restart(1, loaded);
        Assert.Equal(InstanceLossReason.Ambiguous, Assert.Single(_h.Of<InstanceDataLostEvent>()).Reason);
        Assert.Equal(ParkedState.SetAside, Assert.Single(_h.Parked()).State);
        Assert.Null(_h.Count(loaded.At(twin, 3)));
    }

    [Fact]
    public void AHolderCrowdedAtTheSaveIsSetAsideEvenIfOnlyOneIsThereAtTheLoad()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        w.Chest(ChestTemplate, new WorldPosition(A.X + 0.3, A.Y, A.Z));
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        Assert.True(_h.Bundle(1, "instances/sword-mod")!["holders"]![0]!["crowded"]!.GetValue<bool>());
        var snap = w.SaveGame();
        var only = new FakeInstanceWorld.Snapshot(snap.World.Where(x => x.Holder.Position == A).ToList(), []);
        _h.Restart(1, FakeInstanceWorld.Load(only));
        Assert.Equal(ParkedState.SetAside, Assert.Single(_h.Parked()).State);
    }

    [Fact]
    public void AModTouchingAnItemMatchesItsHolderAtOnce()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        var loaded = FakeInstanceWorld.Load(w.SaveGame());
        _h.Restart(1, loaded, tick: false);                // no tick yet: the mod reads first
        var na = loaded.HolderAt(ChestTemplate, A);
        Assert.Equal(5, _h.Get(loaded.At(na, 3)).Count);
    }

    // ---- a mod that is not loaded ----

    [Fact]
    public void AModNotLoadedKeepsItsDataOnTheItemThroughMovesAndRestartsUntilTheItemIsDestroyed()
    {
        var other = TestMods.Create("engraver");
        var engraving = _h.Declare<Note>("text", mod: other);
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var b = w.Chest(ChestTemplate, B);
        var ring = w.Give(a, 3, Ring);
        TestMods.As(other, () => ring.Set(engraving, new Note { Text = "for Ada" }));
        TestMods.Retire(other);                                 // the engraver unloads; its value is kept as JSON
        _h.Save(1);
        _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame()));    // a session without the engraver
        w = _h.World;
        var nring = w.At(w.HolderAt(ChestTemplate, A), 3);
        Assert.NotNull(Store.Instances.LiveOf(nring));          // the record attached to the item anyway
        w.Move(nring, w.HolderAt(ChestTemplate, B), 6);
        _h.Save(2);
        Assert.Contains("for Ada", _h.Bundle(2, "instances/engraver")!.ToJsonString());
        _h.Restart(2, FakeInstanceWorld.Load(w.SaveGame()));
        var back = TestMods.Create("engraver");                 // the engraver is installed again
        var key = _h.Declare<Note>("text", mod: back);
        var moved = _h.World.At(_h.World.HolderAt(ChestTemplate, B), 6);
        Assert.Equal("for Ada", TestMods.As(back, () => moved.Get(key).Text));
        TestMods.Retire(back);
        _h.World.Destroy(moved);                                // dropped only with the item
        var lost = Assert.Single(_h.Of<InstanceDataLostEvent>());
        Assert.Equal((InstanceLossReason.Destroyed, "engraver"), (lost.Reason, lost.Mod));
        _h.Save(3);
        Assert.Null(_h.Bundle(3, "instances/engraver"));
    }

    // ---- caps ----

    [Fact]
    public void AHolderKeepsAtMost256RecordsPerModTheOldestWaitingGoFirst()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        for (var i = 0; i < 200; i++) _h.Tag(w.Give(a, i, Rec.Of(Rec.Ring, gem: (uint)i + 1)), i);
        _h.Save(1);
        Assert.Equal(200, _h.Bundle(1, "instances/sword-mod")!["holders"]![0]!["records"]!.AsArray().Count);
        // the chest comes back with 100 other items: the 200 records wait (no candidate), the 100 new items get data
        var other = new FakeInstanceWorld.Snapshot([(HolderIdentity.OfWorld(ChestTemplate, A, false, "Chest"),
            Enumerable.Range(0, 100).Select(i => (i, Rec.Of(Rec.Ring, gem: 1000u + (uint)i))).ToList())], []);
        _h.Restart(1, FakeInstanceWorld.Load(other));
        Assert.Equal(200, _h.Parked().Count);
        var na = _h.World.HolderAt(ChestTemplate, A);
        for (var i = 0; i < 100; i++) _h.Tag(_h.World.At(na, i), 1000 + i);
        _h.Save(2);
        Assert.Equal(256, _h.Bundle(2, "instances/sword-mod")!["holders"]![0]!["records"]!.AsArray().Count);
        Assert.Equal(44, _h.Of<InstanceDataLostEvent>().Count(e => e.Reason == InstanceLossReason.Capped));
        Assert.Equal(156, _h.Parked().Count);
        Assert.Equal(100, TestMods.As(_h.Mod, () => InstanceData.All(_h.Kills)).Count);
    }

    [Fact]
    public void AValueOver64KBIsRefusedAtSetAndAGrownObjectKeepsItsLastFormThatFit()
    {
        var w = _h.World;
        var ring = w.Give(w.Chest(ChestTemplate, A), 3, Ring);
        var huge = new Kills { Tag = new string('x', 70 * 1024) };
        Assert.Throws<ArgumentException>(() => TestMods.As(_h.Mod, () => ring.Set(_h.Kills, huge)));
        var k = _h.Get(ring);
        k.Tag = "small";
        _h.Save(1);
        k.Tag = new string('y', 70 * 1024);                    // the live object grows past the cap
        k.Count = 9;
        _h.Save(2);
        var saved = _h.Bundle(2, "instances/sword-mod")!["holders"]![0]!["records"]![0]!["keys"]!["kills"]!;
        Assert.Equal("small", saved["Tag"]!.GetValue<string>());
        Assert.Contains(Store.Instances.Listing(), l => l.Contains("too big") && l.Contains("kills"));
    }

    // ---- mod lifetime ----

    [Fact]
    public void AModsUnloadFreezesItsValuesAndAReloadReadsThemIntoTheNewTypes()
    {
        var w = _h.World;
        var ring = w.Give(w.Chest(ChestTemplate, A), 3, Ring);
        _h.Get(ring).Count = 41;
        _h.Get(ring).Count++;
        var oldKey = _h.Kills;
        TestMods.Retire(_h.Mod);
        Assert.All(Store.Instances.Live.SelectMany(l => l.Values.ByMod.Values.SelectMany(k => k.Values)), s => Assert.Null(s.Value));
        Assert.Throws<InvalidOperationException>(() => TestMods.As(_h.Mod, () => ring.Get(oldKey)));
        var again = TestMods.Create("sword-mod");
        var key = _h.Declare<Kills>("kills", mod: again);
        Assert.NotSame(oldKey, key);
        Assert.Equal(42, TestMods.As(again, () => ring.Get(key).Count));
    }

    // ---- destroyed items, players ----

    [Fact]
    public void AnItemDestroyedInAChestLosesItsDataAtOnce()
    {
        var w = _h.World;
        var ring = w.Give(w.Chest(ChestTemplate, A), 3, Ring);
        _h.Tag(ring, 3);
        Assert.True(w.IsFollowed(ring));
        w.Destroy(ring);
        var lost = Assert.Single(_h.Of<InstanceDataLostEvent>());
        Assert.Equal(InstanceLossReason.Destroyed, lost.Reason);
        Assert.Equal(ring, lost.LastRef);
        Assert.Throws<ArgumentException>(() => _h.Get(ring));
        Assert.Null(_h.Count(ring));
        _h.Save(1);
        Assert.Null(_h.Bundle(1, "instances/sword-mod"));
    }

    [Fact]
    public void ARefWhoseIdNowBelongsToAnotherItemIsStale()
    {
        var w = _h.World;
        var chest = w.Chest(ChestTemplate, A);
        var ring = w.Give(chest, 3, Ring);
        _h.Tag(ring, 3);
        w.Destroy(ring, seen: false);                           // not noticed yet
        var reused = w.Give(chest, 3, Ring, reuseId: ring.Entity.Id.Value);
        Assert.NotEqual(ring.Entity.Serial, reused.Entity.Serial);
        Assert.Null(_h.Count(reused));                          // the new item does not get the old one's data
        Assert.False(TestMods.As(_h.Mod, () => ring.Has(_h.Kills)));
        Assert.False(TestMods.As(_h.Mod, () => ring.Remove(_h.Kills)));
        Assert.Throws<ArgumentException>(() => _h.Get(ring));
    }

    [Fact]
    public void APlayerWhoLeavesTakesTheRecordAlongAndGetsItBackOnRejoining()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var s1 = w.Give(p, 10, Sword);
        var s2 = w.Give(p, 11, Sword);
        _h.Tag(s1, 1);
        _h.Tag(s2, 2);
        _h.Save(1);                                             // the player's witness
        w.Swap(s1, s2);                                         // seen: the records follow
        w.Leave(p);
        _h.Tick();
        var parked = _h.Of<InstanceDataParkedEvent>();
        Assert.Equal(2, parked.Count);
        Assert.All(parked, e => Assert.Equal((InstanceParkReason.PlayerLeft, Hero), (e.Reason, e.Character)));
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Equal(2, _h.Count(w.At(back, 10)));
        Assert.Equal(1, _h.Count(w.At(back, 11)));
    }

    [Fact]
    public void APlayersItemsGoWithTheCharacterAcrossARestart()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var s1 = w.Give(p, 10, Sword);
        _h.Tag(s1, 1);
        _h.Tag(w.Give(p, 11, Sword), 2);
        _h.Save(1);
        Assert.Contains($"character-instances/{Hero.Text}/sword-mod", _h.Scopes(1));
        Assert.DoesNotContain("instances/sword-mod", _h.Scopes(1));
        _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame()));
        Assert.Empty(_h.Of<InstanceDataRestoredEvent>());       // not in game yet: nothing is decided
        var back = _h.World.Join(Hero);
        _h.Tick();
        Assert.Equal(1, _h.Count(_h.World.At(back, 10)));
        Assert.Equal(2, _h.Count(_h.World.At(back, 11)));
    }

    [Fact]
    public void ACharacterThatComesBackChangedGetsOnlyCertainRecords()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        _h.Tag(w.Give(p, 10, Sword), 1);
        _h.Tag(w.Give(p, 11, Sword), 2);
        _h.Tag(w.Give(p, 12, Ring), 3);
        _h.Save(1);
        _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame()));
        var file = _h.World.CharacterFiles[Hero];                // played elsewhere: the swords swapped, the ring moved
        file[0] = (11, file[0].Fields);
        file[1] = (10, file[1].Fields);
        file[2] = (20, file[2].Fields);
        var back = _h.World.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(_h.World.At(back, 10)));
        Assert.Null(_h.Count(_h.World.At(back, 11)));
        Assert.Equal(3, _h.Count(_h.World.At(back, 20)));
        Assert.Equal(2, _h.Parked().Count(x => x.State == ParkedState.SetAside));
    }

    [Fact]
    public void AnItemTrashedByAPlayerWhoStaysIsDestroyedAfterTheWindow()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var ring = w.Give(p, 10, Ring);
        _h.Tag(ring, 3);
        w.Destroy(ring);
        _h.Tick();
        Assert.Empty(_h.Of<InstanceDataLostEvent>());           // not decided in that step
        w.Step += InstanceStore.LeaveWindowSteps;
        _h.Tick();
        Assert.Equal(InstanceLossReason.Destroyed, Assert.Single(_h.Of<InstanceDataLostEvent>()).Reason);
        Assert.Empty(_h.Of<InstanceDataParkedEvent>());
    }

    [Fact]
    public void AnItemTrashedJustBeforeThePlayerLeavesIsNotParked()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        var ring = w.Give(p, 10, Ring);
        var twin = w.Give(p, 11, Ring);
        _h.Tag(ring, 3);
        w.Destroy(ring);                                        // trashed: the inventory is still there
        w.Step += 5;
        w.Leave(p);                                             // and the player leaves within the window
        _h.Tick();
        Assert.Equal(InstanceLossReason.Destroyed, Assert.Single(_h.Of<InstanceDataLostEvent>()).Reason);
        Assert.Empty(_h.Of<InstanceDataParkedEvent>());
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(w.At(back, 11)));                   // the twin never gets the trashed ring's data
        _ = twin;
    }

    [Fact]
    public void AnItemMovedFromAChestToAPlayerMovesItsRecordNothingIsCopied()
    {
        var w = _h.World;
        var chest = w.Chest(ChestTemplate, A);
        var ring = w.Give(chest, 3, Ring);
        _h.Tag(ring, 9);
        _h.Save(1);
        Assert.NotNull(_h.Bundle(1, "instances/sword-mod"));
        var p = w.Join(Hero);
        w.Move(ring, p, 14);
        _h.Save(2);
        Assert.Null(_h.Bundle(2, "instances/sword-mod"));
        var rec = _h.Bundle(2, $"character-instances/{Hero.Text}/sword-mod")!["holders"]![0]!["records"]!.AsArray();
        Assert.Equal(14, Assert.Single(rec)!["slot"]!.GetValue<int>());
    }

    [Fact]
    public void SessionKeysAreNeverWritten()
    {
        var w = _h.World;
        var temp = _h.Declare<Note>("scratch", new InstanceKeyOptions { Persist = Persist.Session });
        var ring = w.Give(w.Chest(ChestTemplate, A), 3, Ring);
        TestMods.As(_h.Mod, () => ring.Get(temp).Text = "only now");
        w.Move(ring, w.Chest(ChestTemplate, B), 1);
        Assert.Equal("only now", TestMods.As(_h.Mod, () => ring.Get(temp).Text));
        _h.Save(1);
        Assert.Null(_h.Bundle(1, "instances/sword-mod"));
    }

    [Fact]
    public void WithoutItemReadsTheSavedRecordsRideIntoTheNextGenerationUnchanged()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Ring), 5);
        _h.Save(1);
        var off = FakeInstanceWorld.Load(w.SaveGame());
        off.Off = true;                                         // kiln.items has no item instances in this session
        _h.Restart(1, off);
        _h.EndWindow();
        _h.Save(2);
        var holder = _h.Bundle(2, "instances/sword-mod")!["holders"]![0]!;
        Assert.Null(holder["missed"]);                          // a session without reads is not a missed session
        Assert.Equal(5, holder["records"]![0]!["keys"]!["kills"]!["Count"]!.GetValue<int>());
        off.Off = false;
        _h.Restart(2, FakeInstanceWorld.Load(w.SaveGame()));
        Assert.Equal(5, _h.Count(_h.World.At(_h.World.HolderAt(ChestTemplate, A), 3)));
    }

    [Fact]
    public void TheBundleFormatIsWrittenDown()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A, name: "PropChest");
        w.Give(a, 2, Sword);
        _h.Tag(w.Give(a, 3, Rec.Of(Rec.Can, fuel: 3200, fuelMax: 6400)), 5);
        _h.Save(1);
        var doc = _h.Bundle(1, "instances/sword-mod")!;
        Assert.Equal(1, doc["format"]!.GetValue<int>());
        Assert.Equal("instances/sword-mod", doc["scope"]!.GetValue<string>());
        var holder = doc["holders"]![0]!;
        Assert.Equal("world", holder["holder"]!["kind"]!.GetValue<string>());
        Assert.Equal(ChestTemplate.ToString(), holder["holder"]!["template"]!.GetValue<string>());
        Assert.Equal("PropChest", holder["holder"]!["name"]!.GetValue<string>());
        Assert.Equal(2, holder["witness"]!["1"]!["items"]!.AsArray().Count);  // the holder's whole list, with the sword that has no data
        Assert.True(holder["witness"]!["1"]!["settled"]!.GetValue<bool>());     // read at the snapshot staging
        var rec = holder["records"]![0]!;
        Assert.Equal(12, rec["rec"]!.AsArray().Count);
        Assert.Equal("live", rec["state"]!.GetValue<string>());
        Assert.Equal(16, rec["id"]!.GetValue<string>().Length);
    }
}

[Collection("instance data")]
public sealed class InstanceHardeningTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 5);
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring, level: 2, perksAvailable: 2);

    public void Dispose() => _h.Dispose();

    /// <summary>A chest with twin swords 10 (slot 3) and 20 (slot 5) and a ring 7 (slot 6), saved as revision 1.</summary>
    private FakeInstanceWorld.Snapshot TwinsSaved()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Sword), 10);
        _h.Tag(w.Give(a, 5, Sword), 20);
        _h.Tag(w.Give(a, 6, Ring), 7);
        _h.Save(1);
        return w.SaveGame();
    }

    // ---- H1: world holders are looked for all session, with a budget ----

    [Fact]
    public void AChestThatStreamsInAfterTheWindowIsMatchedByTheTickBeforeAnyModTouchesIt()
    {
        var loaded = FakeInstanceWorld.Load(TwinsSaved(), streamed: false);
        _h.Restart(1, loaded);
        _h.EndWindow();                                          // not there in the window: one missed session
        Assert.Equal(1, _h.Parked()[0].SessionsMissed);
        var chest = loaded.HolderAt(ChestTemplate, A);
        loaded.Holders[chest.Value].Initialized = true;          // it streams in at minute 30
        loaded.Step += 25;
        _h.Tick();
        var s3 = loaded.At(chest, 3);
        var s5 = loaded.At(chest, 5);
        Assert.NotNull(Store.Instances.LiveOf(s3));              // matched by the tick, before anyone touched it
        loaded.Swap(s3, s5);                                     // the player swaps the twins: seen, the values follow
        Assert.Equal(10, _h.Count(loaded.At(chest, 5)));
        Assert.Equal(20, _h.Count(loaded.At(chest, 3)));
        _h.Save(2);
        Assert.Null(_h.Bundle(2, "instances/sword-mod")!["holders"]![0]!["missed"]);   // found later: the missed session is undone
    }

    [Fact]
    public void ATaggedSwordTakenOutLateKeepsItsValue()
    {
        var loaded = FakeInstanceWorld.Load(TwinsSaved(), streamed: false);
        _h.Restart(1, loaded);
        _h.EndWindow();
        var chest = loaded.HolderAt(ChestTemplate, A);
        loaded.Holders[chest.Value].Initialized = true;
        loaded.Step += 25;
        _h.Tick();
        var p = loaded.Join(Hero);
        loaded.Move(loaded.At(chest, 6), p, 12);                 // the player takes the ring out
        Assert.Equal(7, _h.Count(loaded.At(p, 12)));
        Assert.Empty(_h.Parked());
    }

    [Fact]
    public void TheSearchForHoldersHasABudgetPerTickAndGoesRoundRobin()
    {
        var w = _h.World;
        for (var i = 0; i < 40; i++) _h.Tag(w.Give(w.Chest(ChestTemplate, new WorldPosition(10 * i, 0, 0)), 0, Ring), i);
        _h.Save(1);
        var loaded = FakeInstanceWorld.Load(w.SaveGame());
        _h.Restart(1, loaded, tick: false);
        _h.Tick();
        Assert.Equal(InstanceStore.ResolvePerTick, loaded.FindCalls);
        Assert.Equal(40 - InstanceStore.ResolvePerTick, Store.Instances.Holders.Count(h => !h.IsBound));
        _h.Tick();
        _h.Tick();
        Assert.Equal(0, Store.Instances.Holders.Count(h => !h.IsBound));
        Assert.Equal(40, loaded.FindCalls);
    }

    // ---- H2: rule 1 needs the items as the loader made them ----

    [Fact]
    public void TwinsSwappedBetweenTheLoadAndTheMatchAreSetAsideNotSwapped()
    {
        var loaded = FakeInstanceWorld.Load(TwinsSaved());
        _h.Restart(1, loaded, tick: false);
        var chest = loaded.HolderAt(ChestTemplate, A);
        loaded.Swap(loaded.At(chest, 3), loaded.At(chest, 5), seen: false);   // before the store matched the chest
        _h.Tick();
        Assert.Null(_h.Count(loaded.At(chest, 3)));
        Assert.Null(_h.Count(loaded.At(chest, 5)));
        Assert.Equal(7, _h.Count(loaded.At(chest, 6)));           // the single fit still attaches
        Assert.Equal(2, _h.Of<InstanceDataLostEvent>().Count(e => e.Reason == InstanceLossReason.Ambiguous));
    }

    [Fact]
    public void ATwinMadeAfterTheLoadInTheLastSlotGetsNothing()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Sword), 10);
        _h.Tag(w.Give(a, 5, Sword), 20);
        _h.Save(1);
        var loaded = FakeInstanceWorld.Load(w.SaveGame());
        _h.Restart(1, loaded, tick: false);
        var chest = loaded.HolderAt(ChestTemplate, A);
        loaded.Destroy(loaded.At(chest, 5), seen: false);         // the sword in slot 5 is crafted away
        var made = loaded.Give(chest, 5, Sword);                  // and a new twin is put there
        _h.Tick();
        Assert.Null(_h.Count(made));
        Assert.Null(_h.Count(loaded.At(chest, 3)));               // not certain which sword is gone
    }

    [Fact]
    public void ALoaderThatMakesItemsOutOfSlotOrderLeavesTwinsAside()
    {
        var loaded = FakeInstanceWorld.Load(TwinsSaved(), outOfSlotOrder: true);
        _h.Restart(1, loaded);
        var chest = loaded.HolderAt(ChestTemplate, A);
        Assert.Null(_h.Count(loaded.At(chest, 3)));
        Assert.Null(_h.Count(loaded.At(chest, 5)));
        Assert.Equal(7, _h.Count(loaded.At(chest, 6)));
    }

    // ---- H3: no witness, no attachment ----

    [Fact]
    public void APlayerWhoLeavesBeforeAnySaveHasNoWitnessAndTheRecordIsSetAside()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        _h.Tag(w.Give(p, 10, Ring), 3);
        w.Leave(p);                                               // no save while the player was in game
        _h.Tick();
        Assert.Equal(InstanceParkReason.PlayerLeft, Assert.Single(_h.Of<InstanceDataParkedEvent>()).Reason);
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(w.At(back, 10)));
        var aside = Assert.Single(_h.Parked());
        Assert.Equal(ParkedState.SetAside, aside.State);
        Assert.Contains("without the holder's list", aside.Reason);
    }

    // ---- H4: only a settled observation vouches for the holder ----

    [Fact]
    public void ASwapInTheStepBeforeTheSnapshotIsNotTrustedWithoutTheSnapshotStaging()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        var s1 = w.Give(a, 3, Sword);
        var s2 = w.Give(a, 5, Sword);
        _h.Tag(s1, 10);
        _h.Tag(s2, 20);
        FakeInstanceWorld.Snapshot? file = null;
        // the first staging reads 10 in slot 3; in the next simulation step the player swaps them; the game saves that
        _h.SaveFirstStagingOnly(1, between: () =>
        {
            w.Swap(s1, s2, seen: false);                          // the tracker has not reported it before the snapshot
            file = w.SaveGame();
        });
        Assert.False(_h.Bundle(1, "instances/sword-mod")!["holders"]![0]!["witness"]!["1"]!["settled"]!.GetValue<bool>());
        Assert.Equal(DataMatch.Exact, _h.Restart(1, FakeInstanceWorld.Load(file!)));
        var chest = _h.World.HolderAt(ChestTemplate, A);
        Assert.Null(_h.Count(_h.World.At(chest, 3)));             // never 10 on the sword that was 20
        Assert.Null(_h.Count(_h.World.At(chest, 5)));
        Assert.Equal(2, _h.Parked().Count(x => x.State == ParkedState.SetAside));
    }

    [Fact]
    public void AShutdownSavesFirstStagingIsSettled()
    {
        var w = _h.World;
        var a = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(a, 3, Sword), 10);
        _h.Tag(w.Give(a, 5, Sword), 20);
        _h.SaveFirstStagingOnly(1, SaveReason.Shutdown);
        Assert.True(_h.Bundle(1, "instances/sword-mod")!["holders"]![0]!["witness"]!["1"]!["settled"]!.GetValue<bool>());
        _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame()));
        var chest = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(10, _h.Count(_h.World.At(chest, 3)));
        Assert.Equal(20, _h.Count(_h.World.At(chest, 5)));
    }

    [Fact]
    public void AnEarlierWitnessWithoutTheSettledFlagReadsAsUnsettled()
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse("{\"format\":1,\"scope\":\"instances/sword-mod\",\"holders\":[{\"holder\":{\"kind\":\"world\",\"template\":\"" + ChestTemplate + "\",\"pos\":[10,0,10],\"mobile\":false},\"witness\":{\"1\":[[3," + Rec.Sword.Value + ",1,5,100,100,0,0,0,0,0,0,0],[5," + Rec.Sword.Value + ",1,5,100,100,0,0,0,0,0,0,0]]},\"records\":[{\"id\":\"00000000000000a1\",\"slot\":3,\"rec\":[" + Rec.Sword.Value + ",1,5,100,100,0,0,0,0,0,0,0],\"seen\":1,\"state\":\"live\",\"keys\":{\"kills\":{\"Count\":10}}},{\"id\":\"00000000000000a2\",\"slot\":5,\"rec\":[" + Rec.Sword.Value + ",1,5,100,100,0,0,0,0,0,0,0],\"seen\":1,\"state\":\"live\",\"keys\":{\"kills\":{\"Count\":20}}}]}]}")!.AsObject();
        Store.Instances.Absorb("instances/sword-mod", doc);
        Assert.False(Store.Instances.Holders.Single().Witness[1].Settled);
    }
}

[Collection("instance data")]
public sealed class EndSessionTests
{
    [Fact]
    public void AfterEndSessionNothingOfTheFirstWorldIsReadOrWritten()
    {
        using var h = new InstanceHarness("world-one");
        var w = h.World;
        var ring = w.Give(w.Chest(ChestTemplate, A), 3, Rec.Of(Rec.Ring));
        h.Tag(ring, 1);
        var bundle = Store.GetBundleRaw("world/sword-mod", "sword-mod");
        bundle.Keys["state"] = new WorldStore.Slot { Json = "\"of world one\"" };
        h.Save(1);
        var firstFiles = Directory.EnumerateFiles(h.Root, "*", SearchOption.AllDirectories).Order().ToList();

        SavesRuntime.EndSession();
        Assert.Null(Store.WorldId);
        Assert.Null(Store.Root);
        Assert.Equal(0, Store.Instances.LiveCount);
        Assert.Equal(0, Store.Instances.HolderCount);
        Assert.Null(Store.FindBundle("world/sword-mod"));
        Assert.False((bool)typeof(WorldStore).GetField("_closed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Store)!);
        Assert.Throws<InvalidOperationException>(() => h.Get(ring));   // the first session's key went with it

        var second = Path.Combine(AppContext.BaseDirectory, "instance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(second, "manifests"));
        Directory.CreateDirectory(Path.Combine(second, "blobs"));
        try
        {
            InstanceHarness.OpenFolder(second, "world-two");
            typeof(WorldStore).GetMethod("Load", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(Store, [1u, 1001u]);
            Assert.Equal(DataMatch.None, Store.Match);
            Assert.Null(Store.FindBundle("world/sword-mod"));
            Assert.Equal(0, Store.Instances.HolderCount);
            var mod = TestMods.Create("sword-mod");
            var key = TestMods.As(mod, () => InstanceData.Key<Kills>("kills"));
            var w2 = new FakeInstanceWorld();
            Store.Instances.World = w2;
            var other = w2.Give(w2.Chest(ChestTemplate, A), 3, Rec.Of(Rec.Ring));
            TestMods.As(mod, () => other.Get(key).Count = 2);
            Store.Stage(1, SaveReason.Request, late: false);
            Store.Commit(1, 1001, SaveReason.Request);
            Assert.Equal(firstFiles, Directory.EnumerateFiles(h.Root, "*", SearchOption.AllDirectories).Order().ToList());  // world one untouched
            var manifest = JsonNode.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(second, "manifests")).Single()))!;
            Assert.Equal("world-two", manifest["world"]!.GetValue<string>());
            Assert.Equal(["instances/sword-mod"], ((JsonObject)manifest["bundles"]!).Select(kv => kv.Key));
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }
}
