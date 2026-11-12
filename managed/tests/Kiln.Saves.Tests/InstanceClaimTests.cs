// SPDX-License-Identifier: MIT
// InstanceData.Claim and Discard (dev.27): a parked record put on an item a mod names, or dropped; the record is gone
// afterwards, also from the next save, and the events say so. The admin command does the same for every mod.
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceClaimTests : IDisposable
{
    private readonly InstanceHarness _h = new();

    public void Dispose()
    {
        Commands.Unregister(InstanceCommand.Name);
        InstanceCommand.ResetForTests();
        _h.Dispose();
    }

    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword);

    /// <summary>Two identical swords with data, moved where Kiln did not see it: both records set aside; the swords now
    /// sit in slots 4 and 6 with no data.</summary>
    private (ItemInstanceRef At4, ItemInstanceRef At6) SetAside(Action? alsoTag = null)
    {
        var w = _h.World;
        var chest = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(chest, 4, Sword), 2);
        _h.Tag(w.Give(chest, 5, Sword), 3);
        alsoTag?.Invoke();
        _h.Save(1);
        var snap = w.SaveGame();
        _h.Restart(2, FakeInstanceWorld.Load(new FakeInstanceWorld.Snapshot([(snap.World[0].Holder, [(4, Sword), (6, Sword)])], [])));
        var holder = _h.World.HolderAt(ChestTemplate, A);
        return (_h.World.At(holder, 4), _h.World.At(holder, 6));
    }

    [Fact]
    public void ClaimPutsTheRecordOnTheItemAndItStaysThereAfterASave()
    {
        var (at4, at6) = SetAside();
        var parked = _h.Parked();
        Assert.Equal(2, parked.Count);
        Assert.All(parked, p => Assert.Equal(ParkedState.SetAside, p.State));
        Assert.All(parked, p => Assert.Equal(16, p.RecordId.Length));
        var three = parked.Single(p => p.Value!.Count == 3);
        _h.Events.Clear();
        var r = TestMods.As(_h.Mod, () => InstanceData.Claim(three, at6));
        Assert.True(r.Ok, r.ToString());
        Assert.Null(r.Reason);
        Assert.Equal(["kills"], r.Keys);
        Assert.Equal(3, _h.Count(at6));
        Assert.Null(_h.Count(at4));
        var left = Assert.Single(_h.Parked());
        Assert.Equal(2, left.Value!.Count);
        var restored = Assert.Single(_h.Of<InstanceDataRestoredEvent>());
        Assert.Equal((at6, "sword-mod", "kills", InstanceMatch.Claimed), (restored.Item, restored.Mod, restored.Key, restored.Match));
        Assert.Contains("claimed by sword-mod", restored.Detail);
        // the next save carries it on the item, and the claimed record is gone
        _h.Save(3);
        _h.Restart(3, FakeInstanceWorld.Load(_h.World.SaveGame()));
        var holder = _h.World.HolderAt(ChestTemplate, A);
        Assert.Equal(3, _h.Count(_h.World.At(holder, 6)));
        Assert.Equal([2], _h.Parked().Select(p => p.Value!.Count));
    }

    [Fact]
    public void ARecordClaimedOnceIsNoLongerParkedAndAnItemWithTheKeyIsRefused()
    {
        var (at4, at6) = SetAside();
        var parked = _h.Parked();
        _h.Tag(at4, 9);
        var refused = TestMods.As(_h.Mod, () => InstanceData.Claim(parked[0], at4));
        Assert.False(refused.Ok);
        Assert.Contains("already has kills", refused.Reason);
        Assert.Equal(9, _h.Count(at4));
        Assert.Equal(2, _h.Parked().Count);  // nothing moved
        Assert.True(TestMods.As(_h.Mod, () => InstanceData.Claim(parked[0], at6)).Ok);
        var e = Assert.Throws<ArgumentException>(() => TestMods.As(_h.Mod, () => InstanceData.Claim(parked[0], at6)));
        Assert.Contains("no longer parked", e.Message);
        Assert.Throws<ArgumentException>(() => TestMods.As(_h.Mod, () => InstanceData.Claim(parked[1], ItemInstanceRef.None)));
        Assert.Throws<ArgumentNullException>(() => TestMods.As(_h.Mod, () => InstanceData.Claim<Kills>(null!, at6)));
    }

    [Fact]
    public void DiscardDropsTheRecordForGood()
    {
        SetAside();
        var parked = _h.Parked();
        _h.Events.Clear();
        Assert.True(TestMods.As(_h.Mod, () => InstanceData.Discard(parked[0])));
        Assert.False(TestMods.As(_h.Mod, () => InstanceData.Discard(parked[0])));
        var lost = Assert.Single(_h.Of<InstanceDataLostEvent>());
        Assert.Equal((InstanceLossReason.Discarded, "sword-mod"), (lost.Reason, lost.Mod));
        Assert.Contains("discarded by sword-mod", lost.Detail);
        Assert.Single(_h.Parked());
        _h.Save(3);
        _h.Restart(3, FakeInstanceWorld.Load(_h.World.SaveGame()));
        Assert.Single(_h.Parked());
    }

    [Fact]
    public void AnotherModsValuesInTheSameRecordStayParked()
    {
        var other = TestMods.Create("gem-mod");
        var gems = _h.Declare<Note>("gem", mod: other);
        var (at4, at6) = SetAside(alsoTag: () =>
            TestMods.As(other, () => _h.World.At(_h.World.HolderAt(ChestTemplate, A), 5).Set(gems, new Note { Text = "ruby" })));
        var mine = _h.Parked().Single(p => p.Value!.Count == 3);
        Assert.True(TestMods.As(_h.Mod, () => InstanceData.Claim(mine, at4)).Ok);
        var theirs = TestMods.As(other, () => InstanceData.Parked(gems));
        var gem = Assert.Single(theirs);
        Assert.Equal(("ruby", mine.RecordId), (gem.Value!.Text, gem.RecordId));
        Assert.False(TestMods.As(other, () => at4.Has(gems)));
        Assert.True(TestMods.As(other, () => InstanceData.Claim(gem, at4)).Ok);
        Assert.Equal("ruby", TestMods.As(other, () => at4.Get(gems)).Text);
        Assert.Empty(TestMods.As(other, () => InstanceData.Parked(gems)));
    }

    [Fact]
    public void TheAdminCommandClaimsAndDiscardsForEveryMod()
    {
        _ = typeof(Commands).Assembly;
        Commands.Unregister(InstanceCommand.Name);
        InstanceCommand.ResetForTests();
        Assert.True(InstanceCommand.TryRegister(final: true));
        var (_, at6) = SetAside();
        var parked = _h.Parked();
        var holder = _h.World.HolderAt(ChestTemplate, A);
        var listing = Commands.Execute(CommandSource.Console, "/instances");
        Assert.Contains(listing.Output, l => l.Contains($"record {parked[0].RecordId}"));
        var claim = Commands.Execute(CommandSource.Console, $"/instances claim {parked[0].RecordId} {holder.Value} 6");
        Assert.True(claim.Ok, claim.Text);
        Assert.Contains("claimed record", claim.Output[0]);
        Assert.Equal(parked[0].Value!.Count, _h.Count(at6));
        var again = Commands.Execute(CommandSource.Console, $"/instances claim {parked[0].RecordId} {holder.Value} 6");
        Assert.False(again.Ok);
        Assert.Contains("no parked record", again.Error);
        var discard = Commands.Execute(CommandSource.Console, $"/instances discard {parked[1].RecordId}");
        Assert.True(discard.Ok, discard.Text);
        Assert.Contains("sword-mod [kills]", discard.Output[0]);
        Assert.Empty(_h.Parked());
        var noSlot = Commands.Execute(CommandSource.Console, $"/instances claim abc {holder.Value} x");
        Assert.False(noSlot.Ok);
        Assert.True(Commands.Execute(CommandSource.ForSteamId(76561198000000001, "Guest"), $"/instances discard {parked[1].RecordId}").Denied);
    }
}
