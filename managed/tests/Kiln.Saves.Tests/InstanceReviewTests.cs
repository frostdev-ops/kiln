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
