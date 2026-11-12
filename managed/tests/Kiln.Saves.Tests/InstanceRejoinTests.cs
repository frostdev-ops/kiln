// SPDX-License-Identifier: MIT
// The first real player's leave and rejoin (lab dev26-playtest, 2026-11-12, sample mod item-data): the character loader
// made the worn gear (flat slot 256 and up) before the backpack, and the leave save's own read stayed the first staging's
// (its snapshot staging did not re-read the leaving player). Both turned a record that certainly fits into "set aside".
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceRejoinTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 45, durability: 470, durabilityMax: 470, perksAvailable: 5);
    private static readonly InstanceFields Helm = Rec.Of(Rec.Ring, level: 15, perksAvailable: 5, perksUnlocked: 5);
    private static readonly InstanceFields Boots = Rec.Of(Rec.Bow, level: 15, perksAvailable: 3, perksUnlocked: 3);

    public InstanceRejoinTests() => _h.World.PlayerLoaderWornFirst = true;

    public void Dispose() => _h.Dispose();

    private void SaveAndSend(uint revision, EntityId player)
    {
        _h.Save(revision);
        _h.World.SendCharacter(player);
    }

    /// <summary>Leave from the menu as the real one went: the leave save read the player once, at its first staging (the
    /// snapshot staging did not read the leaving player again), the final file was sent, the entity and its items went.</summary>
    private void MenuLeaveReadOnceAtTheRequest(EntityId player, uint revision)
    {
        var w = _h.World;
        _h.SaveFirstStagingOnly(revision, SaveReason.PlayerLeft);
        w.SendCharacter(player);
        w.BeginLeave(player);
        _h.Tick();
        w.DestroyPlayerEntity(player);
        _h.Tick();
        w.TearDown(player);
        _h.Tick();
    }

    [Fact]
    public void TwinsInTheBackpackComeBackWhenTheLoaderMakesWornGearFirst()
    {
        var w = _h.World;
        var p = w.Join(Hero);
        w.Give(p, 256, Helm);                                    // worn
        w.Give(p, 257, Boots);
        var s1 = w.Give(p, 26, Sword);
        var s2 = w.Give(p, 27, Sword);
        _h.Tag(s1, 1);
        _h.Tag(s2, 2);
        _h.Tick();
        SaveAndSend(1, p);
        w.Crash(p);                                              // no leave save: the file of save 1 comes back
        _h.Tick();
        var back = w.Join(Hero);                                 // helm, boots, then the two swords
        _h.Tick();
        Assert.Equal(1, _h.Count(w.At(back, 26)));
        Assert.Equal(2, _h.Count(w.At(back, 27)));
        Assert.Empty(_h.Of<InstanceDataLostEvent>());
    }

    [Fact]
    public void TwinsAtAMenuLeaveWhoseLeaveSaveWasReadOnlyAtTheRequestAreSetAsideNeverSwapped()
    {
        // The first repro: both swords in the backpack at the leave. The leave save's read is one simulation step before the
        // game's snapshot, so a swap in that step would not show: not certain, set aside (the promise), as before.
        var w = _h.World;
        var p = w.Join(Hero);
        w.Give(p, 256, Helm);
        var notched = w.Give(p, 27, Sword);
        w.Give(p, 26, Sword);
        _h.Tag(notched, 1);
        _h.Tick();
        SaveAndSend(1, p);
        MenuLeaveReadOnceAtTheRequest(p, 2);
        var back = w.Join(Hero);
        _h.Tick();
        Assert.Null(_h.Count(w.At(back, 26)));
        Assert.Null(_h.Count(w.At(back, 27)));
        Assert.Equal(ParkedState.SetAside, Assert.Single(_h.Parked()).State);
    }
}
