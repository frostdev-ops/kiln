// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceReviewTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword);
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring);

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public void AReusedPlayerSlotDoesNotDestroyThePreviousVisitsData(bool sameCharacter, bool noticeAfterJoin, bool sawLeave)
    {
        var w = _h.World;
        var player = w.Join(Hero);
        _h.Tag(w.Give(player, 0, Sword), 1);
        _h.Tick();
        _h.Save(1);
        w.SendCharacter(player);
        w.BeginLeave(player);
        if (sawLeave) _h.Tick();
        w.DestroyPlayerEntity(player);
        if (!noticeAfterJoin) w.TearDown(player);
        var other = new CharacterKey(0x2233445566778899, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000002"));
        w.Join(sameCharacter ? Hero : other, reuseId: player);
        if (noticeAfterJoin)
        {
            _h.Tick(); // the new visit has already been bound before the delayed gone notice
            w.TearDown(player);
        }
        w.Step += InstanceStore.LeaveWindowSteps + 1;
        _h.Tick();
        Assert.DoesNotContain(_h.Of<InstanceDataLostEvent>(), e => e.Reason == InstanceLossReason.Destroyed);
        Assert.Equal(1, TestMods.As(_h.Mod, () => InstanceData.All(_h.Kills).Count) + _h.Parked().Count);
        if (sameCharacter) Assert.Equal(2u, Store.Instances.Holders.Single(h => h.Key.Character == Hero).Joins);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AReplacedWitnessNeverVouchesForAnItemNotPlacedInItsRead(bool playerHolder, bool freshMate, bool pending)
    {
        var w = _h.World;
        var holder = playerHolder ? w.Join(Hero) : w.Chest(ChestTemplate, A);
        var a = w.Give(holder, 0, Sword);
        _h.Tag(a, 1);
        var ring = w.Give(holder, 1, Ring);
        if (freshMate) _h.Tag(ring, 2); // otherwise only step 2b re-witnesses the player
        _h.Tick();
        Store.Stage(1, SaveReason.Request, late: false);
        if (pending)
        {
            w.Holders[holder.Value].Initialized = false;
            w.Destroy(a); // a pending notice must not retain Fresh from the first staging either
            w.Holders[holder.Value].Initialized = true;
        }
        else w.Move(a, EntityId.None, -1, seen: false);
        w.Give(holder, 0, Sword); // another, untagged twin takes the slot
        Store.Stage(1, SaveReason.Request, late: false, snapshot: true);
        Store.Commit(1, 1001, SaveReason.Request);
        _h.Restart(1, FakeInstanceWorld.Load(w.SaveGame()));
        holder = playerHolder ? _h.World.Join(Hero) : _h.World.HolderAt(ChestTemplate, A);
        _h.Tick();
        Assert.Null(_h.Count(_h.World.At(holder, 0)));
        Assert.Equal(freshMate ? 2 : (int?)null, _h.Count(_h.World.At(holder, 1)));
        Assert.Single(_h.Parked(), p => p.State == ParkedState.SetAside);
        Assert.DoesNotContain(_h.Of<InstanceDataLostEvent>(), e => e.Reason == InstanceLossReason.Destroyed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StagingBatchesItemsAndResolvesEachHolderOnceEvenDuringMoveNotices(bool notices)
    {
        var w = _h.World;
        var chest = w.Chest(ChestTemplate, A);
        for (var i = 0; i < 100; i++) _h.Tag(w.Give(chest, i, Sword), i);
        w.NotifyMovesOnRead = notices;
        w.ResetReadCounts();
        Store.Stage(1, SaveReason.Request, late: false);
        Assert.Equal(1, w.BatchReadCalls);
        Assert.Equal(0, w.ReadCalls);
        Assert.Equal(1, w.HolderCalls);
        Assert.Equal(1, w.ReadHolderCalls);
    }

    [Fact]
    public void NoKeysAndNoRecordsMeansNoPlayerInventoryReadsAtJoinOrSave()
    {
        var w = _h.World;
        var player = w.Join(Hero);
        w.Give(player, 0, Sword);
        Store.Instances.EndSession(); // no mod uses instance data on this server
        w.ResetReadCounts();
        _h.Tick();
        _h.Save(1);
        Assert.Equal(0, w.ReadHolderCalls);
        Assert.Empty(Store.Instances.Holders);
    }

    [Fact]
    public void AKeyDeclaredMidVisitDoesNotInventAnEarlierPlayerWitness()
    {
        var w = _h.World;
        var player = w.Join(Hero);
        var a = w.Give(player, 0, Sword);
        var b = w.Give(player, 1, Sword);
        Store.Instances.EndSession();
        _h.Tick();
        _h.Save(1);
        w.Swap(a, b, seen: false);
        _h.Kills = _h.Declare<Kills>("kills");
        _h.Tick();
        var entry = Assert.Single(Store.Instances.Holders);
        var state = Assert.Single(entry.States);
        Assert.True((state.Id & InstanceStore.JoinIdBase) != 0);
        Assert.False(entry.Witness[state.Id].Settled); // no longer in loader order
    }

    [Fact]
    public void AnUnavailableHolderIdentityRefusesSetInsteadOfKeepingItOnlyInMemory()
    {
        var w = _h.World;
        var sword = w.Give(w.Chest(ChestTemplate, A), 0, Sword);
        w.SpawnOff = true;
        var error = Assert.Throws<KilnUnavailableException>(() => _h.Tag(sword, 1));
        Assert.Contains("kiln.spawn", error.Message);
        Assert.Null(Store.Instances.LiveOf(sword));
    }

    [Fact]
    public void ASessionsFailedFindDoesNotCountEvenIfItRecoversBeforeTheDeadline()
    {
        var w = _h.World;
        _h.Tag(w.Give(w.Chest(ChestTemplate, A), 0, Sword), 1);
        _h.Save(1);
        for (uint generation = 1; generation <= 3; generation++)
        {
            var absent = new FakeInstanceWorld { FindOff = true };
            _h.Restart(generation, absent);
            absent.FindOff = false; // the failed answer earlier in this session still matters
            _h.EndWindow();
            Assert.Single(Store.Instances.Holders);
            Assert.Equal(0, Store.Instances.Holders[0].Missed);
            _h.Save(generation + 1);
        }
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("pending")]
    [InlineData("older")]
    public void PlayerTwinsFromANewerFileNeverTakeRestoredSlots(string newer)
    {
        var w = _h.World;
        var player = w.Join(Hero);
        var a = w.Give(player, 0, Sword);
        var b = w.Give(player, 1, Sword);
        var ring = w.Give(player, 2, Ring);
        _h.Tag(a, 1);
        _h.Tag(b, 2);
        _h.Tag(ring, 3);
        _h.Tick();
        _h.Save(1);
        w.Swap(a, b);
        if (newer == "committed") _h.Save(2);
        if (newer == "pending") _h.StageOnly(2);
        var file = w.SaveGame(); // the client's file is newer than the restored world
        var match = _h.Restart(newer == "older" ? 2u : 1u, FakeInstanceWorld.Load(file));
        Assert.Equal(newer == "older" ? DataMatch.Older : DataMatch.Exact, match);
        player = _h.World.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(_h.World.At(player, 0)));
        Assert.Null(_h.Count(_h.World.At(player, 1)));
        Assert.Equal(3, _h.Count(_h.World.At(player, 2)));
        Assert.Equal(2, _h.Parked().Count(p => p.State == ParkedState.SetAside));
    }
}
