// SPDX-License-Identifier: MIT
// The matcher's named cases (PLAN D6 rules 1 to 4, and the phase-2a conditions: a witness for every record, a settled
// witness and items as the loader made them for rule 1), on the pure function; and the load-order predicate.
using Xunit;

namespace Kiln;

public sealed class InstanceMatcherTests
{
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 5);
    private static readonly InstanceFields Sword7 = Rec.Of(Rec.Sword, level: 7);
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring, level: 2, perksAvailable: 2, perksUnlocked: 1);

    private static MatchRecord R(int slot, InstanceFields f, uint seen = 10, bool aside = false, bool onlyTrusted = false) => new(slot, f.Stable, seen, aside, onlyTrusted);

    private static MatchItem I(int slot, InstanceFields f, bool live = false) => new(slot, f, live);

    private static Witness Saw(bool settled, params (int slot, InstanceFields f)[] list) => new(list.Select(x => new WitnessEntry(x.slot, x.f)).ToList(), settled);

    /// <summary>The holder at the save of observation <paramref name="seen"/>, settled.</summary>
    private static Dictionary<uint, Witness> W(uint seen, params (int slot, InstanceFields f)[] list) => new() { [seen] = Saw(true, list) };

    /// <summary>A world holder unless <paramref name="world"/> is false (a player).</summary>
    private static MatchOutcome[] Run(MatchRecord[] records, MatchItem[] items, Dictionary<uint, Witness> witness, uint? trusted = null, bool asMade = true,
        bool world = true) =>
        InstanceMatcher.Match(records, witness, items, trusted, asMade, world);

    [Fact]
    public void ExactUnderTheWorldFilesOwnGenerationAttachesTwinsToTheirOwnSlots()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword), I(6, Ring)], W(10, (3, Sword), (5, Sword), (6, Ring)), trusted: 10);
        Assert.Equal((MatchKind.AttachExact, 0), (o[0].Kind, o[0].Item));
        Assert.Equal((MatchKind.AttachExact, 1), (o[1].Kind, o[1].Item));
    }

    [Fact]
    public void ExactWhenAPlayersInventoryEqualsItsWitnessAttachesTwinsToTheirOwnSlots()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword), I(6, Ring)], W(10, (3, Sword), (5, Sword), (6, Ring)), world: false);
        Assert.All(o, x => Assert.Equal(MatchKind.AttachExact, x.Kind));
        Assert.Equal([0, 1], o.Select(x => x.Item));
    }

    [Fact]
    public void AnOlderObservationIsNotTrustedByTheGeneration()
    {
        // generation 12 is the world file's own, but the records were observed at 10 (carried over unread): twins are not resolved
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword)], W(10, (3, Sword), (5, Sword), (7, Ring)), trusted: 12);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void MovedWhenTheOneItemThatFitsIsInAnotherSlot()
    {
        var o = Run([R(3, Ring)], [I(7, Ring), I(3, Sword)], W(10, (3, Ring), (4, Sword)));
        Assert.Equal(MatchKind.AttachMoved, o[0].Kind);
        Assert.Equal(0, o[0].Item);
        Assert.Contains("moved from slot 3 to 7", o[0].Reason);
    }

    [Fact]
    public void UntrustedButStillInItsSlotIsExact()
    {
        var o = Run([R(3, Ring)], [I(3, Ring), I(4, Sword)], W(10, (3, Ring), (9, Sword)));
        Assert.Equal((MatchKind.AttachExact, 0), (o[0].Kind, o[0].Item));
    }

    [Fact]
    public void TwinsInAChangedHolderAreBothSetAside()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword), I(9, Ring)], W(10, (3, Sword), (5, Sword)));
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
        Assert.All(o, x => Assert.Equal(-1, x.Item));
    }

    [Fact]
    public void OneRecordAndTwoCandidatesIsAmbiguous()
    {
        var o = Run([R(3, Sword)], [I(3, Sword), I(4, Sword)], W(10, (3, Sword)));
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
        Assert.Contains("2 items and 1 record", o[0].Reason);
    }

    [Fact]
    public void TwoRecordsAndOneCandidateAreBothSetAside()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(4, Sword)], W(10, (3, Sword), (5, Sword)));
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void NoCandidateIsParked()
    {
        var o = Run([R(3, Ring)], [I(3, Sword)], W(10, (3, Ring)));
        Assert.Equal(MatchKind.Parked, o[0].Kind);
    }

    [Fact]
    public void TheStableFingerprintMustAgree()
    {
        // same item id, other level: not the same item
        var o = Run([R(3, Sword)], [I(3, Sword7)], W(10, (3, Sword)), trusted: 10);
        Assert.Equal(MatchKind.Parked, o[0].Kind);
        var u = Run([R(3, Sword)], [I(3, Sword7)], W(10, (3, Sword)));
        Assert.Equal(MatchKind.Parked, u[0].Kind);
    }

    [Fact]
    public void ARecordSetAsideEarlierIsNeverAttached()
    {
        var o = Run([R(3, Ring, aside: true)], [I(3, Ring)], W(10, (3, Ring)), trusted: 10);
        Assert.Equal(MatchKind.AlreadyAside, o[0].Kind);
        Assert.False(o[0].Attached);
    }

    [Fact]
    public void ATrustedRecordWhoseOwnSlotIsEmptyWaitsInsteadOfMoving()
    {
        var o = Run([R(3, Ring)], [I(7, Ring)], W(10, (7, Ring)), trusted: 10);
        Assert.Equal(MatchKind.Parked, o[0].Kind);
    }

    [Fact]
    public void ARecordGoneAtTheSaveComesBackOnlyToAnUnchangedHolder()
    {
        var w = W(10, (3, Ring));
        var unchanged = Run([R(3, Ring, onlyTrusted: true)], [I(3, Ring)], w, world: false);
        Assert.Equal(MatchKind.AttachExact, unchanged[0].Kind);
        var changed = Run([R(3, Ring, onlyTrusted: true)], [I(4, Ring)], w, world: false);
        Assert.Equal(MatchKind.Parked, changed[0].Kind);
    }

    [Fact]
    public void AnItemThatAlreadyCarriesThisSessionsDataTakesNoRecord()
    {
        var o = Run([R(3, Ring)], [I(3, Ring, live: true)], W(10, (3, Ring)), trusted: 10);
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
    }

    [Fact]
    public void AnOlderRecordWithTheFingerprintOfATrustedOneIsSetAside()
    {
        // seen 10 = trusted generation (twins in 3 and 5); a parked record from an earlier session has the same fingerprint
        var w = W(10, (3, Sword), (5, Sword));
        w[4] = Saw(true, (8, Sword));
        var o = Run([R(3, Sword, seen: 10), R(5, Sword, seen: 10), R(8, Sword, seen: 4)], [I(3, Sword), I(5, Sword)], w, trusted: 10);
        Assert.Equal(MatchKind.AttachExact, o[0].Kind);
        Assert.Equal(MatchKind.AttachExact, o[1].Kind);
        Assert.Equal(MatchKind.SetAside, o[2].Kind);
    }

    [Fact]
    public void TwoTrustedRecordsForOneSlotAreBothSetAside()
    {
        var w = W(4, (3, Sword));
        w[10] = Saw(true, (3, Sword));
        var o = Run([R(3, Sword, seen: 10), R(3, Sword, seen: 4)], [I(3, Sword)], w, world: false);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void WitnessComparesDurabilityAndFuelAsRatios()
    {
        var a = Rec.Of(Rec.Can, fuel: 3200, fuelMax: 6400);
        var reloaded = Rec.Of(Rec.Can, fuel: 50, fuelMax: 100);  // the loader rebuilt max, same ratio
        var used = Rec.Of(Rec.Can, fuel: 3000, fuelMax: 6400);
        Assert.True(InstanceMatcher.SameAsWitness([new WitnessEntry(1, a)], [I(1, reloaded)]));
        Assert.False(InstanceMatcher.SameAsWitness([new WitnessEntry(1, a)], [I(1, used)]));
        Assert.False(InstanceMatcher.SameAsWitness([new WitnessEntry(1, a)], [I(2, a)]));
        Assert.False(InstanceMatcher.SameAsWitness([new WitnessEntry(1, a)], [I(1, a), I(2, Ring)]));
    }

    [Fact]
    public void TwinsWithDifferentWearSwappedUnseenAreNotTrusted()
    {
        // durability tells the twins apart in the witness, so a swap makes the holder untrusted, and the twins are set aside
        var worn = Rec.Of(Rec.Sword, durability: 50);
        var fresh = Rec.Of(Rec.Sword, durability: 90);
        var o = Run([R(3, worn, seen: 4), R(5, fresh, seen: 4)], [I(3, fresh), I(5, worn)], W(4, (3, worn), (5, fresh)));
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void ResultsAreIndexedByRecordAndEveryRecordGetsOne()
    {
        var recs = new[] { R(1, Ring), R(2, Sword), R(3, Sword), R(4, Sword7) };
        var o = Run(recs, [I(1, Ring), I(2, Sword), I(9, Sword7)], W(10, (1, Ring), (2, Sword), (3, Sword), (4, Sword7)));
        Assert.Equal(recs.Length, o.Length);
        Assert.Equal([0, 1, 2, 3], o.Select(x => x.Record));
        Assert.Equal(MatchKind.AttachExact, o[0].Kind);
        Assert.Equal(MatchKind.SetAside, o[1].Kind);   // two sword records, one sword
        Assert.Equal(MatchKind.SetAside, o[2].Kind);
        Assert.Equal(MatchKind.AttachMoved, o[3].Kind);
    }

    // ---- H3: no witness, no attachment ----

    [Fact]
    public void ARecordWithoutAWitnessIsSetAsideEvenWhenItIsTheOneFit()
    {
        var o = Run([R(3, Ring, seen: 10)], [I(7, Ring)], W(4, (3, Ring)));
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
        Assert.Contains("without the holder's list", o[0].Reason);
        var never = Run([R(3, Ring, seen: 0)], [I(3, Ring)], new());
        Assert.Equal(MatchKind.SetAside, never[0].Kind);
    }

    [Fact]
    public void RuleOneThroughTheGenerationNeedsTheWitnessToo()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword)], new(), trusted: 10);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    // ---- H4: only a settled observation vouches for the holder ----

    [Fact]
    public void AnUnsettledWitnessDoesNotResolveTwinsButASingleFitStillAttaches()
    {
        var w = new Dictionary<uint, Witness> { [10] = Saw(false, (3, Sword), (5, Sword), (6, Ring)) };
        var o = Run([R(3, Sword), R(5, Sword), R(6, Ring)], [I(3, Sword), I(5, Sword), I(6, Ring)], w, trusted: 10);
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
        Assert.Equal(MatchKind.SetAside, o[1].Kind);
        Assert.Equal(MatchKind.AttachExact, o[2].Kind);
        Assert.Contains("changed", o[2].Reason);   // decided by rule 2
    }

    // ---- H2: rule 1 needs the items as the loader made them ----

    [Fact]
    public void ItemsNotAsTheLoaderMadeThemTurnRuleOneOff()
    {
        var w = W(10, (3, Sword), (5, Sword), (6, Ring));
        var o = Run([R(3, Sword), R(5, Sword), R(6, Ring)], [I(3, Sword), I(5, Sword), I(6, Ring)], w, trusted: 10, asMade: false);
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
        Assert.Equal(MatchKind.SetAside, o[1].Kind);
        Assert.Equal(MatchKind.AttachExact, o[2].Kind);
        var same = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword)], W(10, (3, Sword), (5, Sword)), asMade: false, world: false);
        Assert.All(same, x => Assert.Equal(MatchKind.SetAside, x.Kind));   // the witness alone does not do it either
    }

    // ---- phase 2b: a world holder needs the world file's own generation and an equal list ----

    [Fact]
    public void AWorldFileEditedWithItsHeaderKeptIsNotTrusted()
    {
        // generation 10 is the world file's own by its header, but the file was edited: the worn and the fresh sword swapped
        var worn = Rec.Of(Rec.Sword, durability: 50);
        var fresh = Rec.Of(Rec.Sword, durability: 90);
        var o = Run([R(3, worn), R(5, fresh)], [I(3, fresh), I(5, worn)], W(10, (3, worn), (5, fresh)), trusted: 10);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void TheWitnessAloneVouchesForAPlayerNotForAWorldHolder()
    {
        var w = W(4, (3, Sword), (5, Sword));
        var chest = Run([R(3, Sword, seen: 4), R(5, Sword, seen: 4)], [I(3, Sword), I(5, Sword)], w, trusted: null);
        Assert.All(chest, x => Assert.Equal(MatchKind.SetAside, x.Kind));
        var player = Run([R(3, Sword, seen: 4), R(5, Sword, seen: 4)], [I(3, Sword), I(5, Sword)], w, world: false);
        Assert.All(player, x => Assert.Equal(MatchKind.AttachExact, x.Kind));
    }

    [Fact]
    public void LoadOrderAcceptsIdsAscendingInSlotOrderInOneRunAfterTheHolder()
    {
        var chest = new EntityId(1042);
        Assert.True(LoadOrder.AsMade(chest, true, [(3, 1043u), (8, 1044u)], 2, [3, 8], out _));   // the one lab data point
        Assert.True(LoadOrder.AsMade(chest, true, [], 0, [], out _));
        Assert.True(LoadOrder.AsMade(chest, true, [(3, 1043u)], 2, null, out _));            // without the loader's slots, one taken out
        Assert.True(LoadOrder.AsMade(chest, true, [(8, 1044u)], 2, null, out _));
        Assert.True(LoadOrder.AsMade(new EntityId(3), false, [(10, 5000u), (11, 5001u)], 2, null, out _));  // a player: no anchor
    }

    [Fact]
    public void LoadOrderRefusesASwapALaterItemAndItemsFromElsewhere()
    {
        var chest = new EntityId(1042);
        Assert.False(LoadOrder.AsMade(chest, true, [(3, 1044u), (8, 1043u)], 2, null, out var why));
        Assert.Contains("changed places", why);
        Assert.False(LoadOrder.AsMade(chest, true, [(3, 1043u), (8, 1045u)], 2, null, out why));     // made right after the load
        Assert.Contains("came in", why);
        Assert.False(LoadOrder.AsMade(chest, true, [(3, 1043u), (8, 1044u), (9, 1045u)], 2, null, out why));  // more than the loader made
        Assert.False(LoadOrder.AsMade(chest, true, [(3, 2001u), (8, 2002u)], 2, null, out why));     // a whole run made later
        Assert.Contains("not made with holder", why);
        Assert.False(LoadOrder.AsMade(chest, true, [(3, 1000u), (8, 1001u)], 2, null, out _));       // older than the holder
        Assert.False(LoadOrder.AsMade(new EntityId(3), false, [(10, 5000u), (11, 5002u)], 2, null, out _));
    }

    [Fact]
    public void WithTheLoadersSlotsKnownAMoveOrAShiftInOrderIsRefused()
    {
        var chest = new EntityId(1042);
        // made in 3 and 5; the one in 5 moved to 9: the order holds, the slot does not
        Assert.False(LoadOrder.AsMade(chest, true, [(3, 1043u), (9, 1044u)], 2, [3, 5], out var why));
        Assert.Contains("moved", why);
        // made in 3, 5, 8; the one in 5 taken out and the one in 3 shifted into 5: a twin would sit in the gone one's slot
        Assert.False(LoadOrder.AsMade(chest, true, [(5, 1043u), (8, 1045u)], 3, [3, 5, 8], out why));
        Assert.Contains("gone", why);
        Assert.True(LoadOrder.AsMade(chest, true, [(3, 1043u), (5, 1044u), (8, 1045u)], 3, [8, 3, 5], out _));
    }

    // ---- a player's join: the loader makes worn gear before the backpack ----

    private static readonly InstanceFields Helm = Rec.Of(Rec.Bow, level: 15, perksAvailable: 5, perksUnlocked: 5);

    /// <summary>A player's inventory as the real join of 2026-11-12 made it, as far as its log tells: the item in slot 256
    /// (entity 11643) first, the backpack's 20 items (slots 7 to 37) as one run ending at 11672 in slot 37.</summary>
    private static List<(int Slot, uint Id, StableFingerprint Stable)> RealJoin()
    {
        var list = new List<(int, uint, StableFingerprint)>();
        uint id = 11643;
        for (var s = 256; s <= 263; s++) list.Add((s, id++, Rec.Of(Rec.Ring, level: s).Stable));   // worn, link 0
        list.Add((266, id++, Rec.Of(Rec.Ring, level: 99).Stable));                                    // link 1
        list.Add((312, id++, Rec.Of(Rec.Can, level: 3).Stable));                                       // link 7
        int[] backpack = [7, 8, 10, 11, 12, 13, 14, 16, 17, 22, 24, 25, 26, 27, 28, 29, 31, 34, 35, 37];
        foreach (var s in backpack) list.Add((s, id++, (s is 26 or 27 ? Sword : Rec.Of(Rec.Sword, level: 100 + s)).Stable));
        list.Add((324, id++, Rec.Of(Rec.Can, level: 4).Stable));                                       // link 8
        list.Add((325, id++, Rec.Of(Rec.Can, level: 5).Stable));
        return list;
    }

    [Fact]
    public void APlayersJoinWithWornGearMadeBeforeTheBackpackIsAsMade()
    {
        var join = RealJoin();
        Assert.Equal(11672u, join.Single(i => i.Slot == 37).Id);
        // the check of world holders refuses it (the warning of the real join)
        Assert.False(LoadOrder.AsMade(new EntityId(1), false, join.Select(i => (i.Slot, i.Id)).ToList(), join.Count, null, out var flat));
        Assert.Contains("slot 256 (entity 11643) is older than the one in slot 37 (entity 11672)", flat);
        Assert.True(LoadOrder.AsMadeByLinks(join, out var why), why);
        Assert.Contains("link 0 (slots 256-263) 11643-11650 (8), link 1 (slots 264-271) 11651 (1), link 7 (slots 312-319) 11652 (1), " +
                        "the backpack 11653-11672 (20), link 8 (slots 320-327) 11673-11674 (2)", why);
        Assert.True(LoadOrder.AsMadeByLinks([], out _));
    }

    [Fact]
    public void APlayersJoinRefusesASwapInALinkABetweenLinksAndAnItemFromElsewhere()
    {
        List<(int Slot, uint Id, StableFingerprint Stable)> Swap(int a, int b) =>
            RealJoin().Select(i => i.Slot == a ? (b, i.Id, i.Stable) : i.Slot == b ? (a, i.Id, i.Stable) : i).ToList();
        // the twins in the backpack swapped (the fingerprints stay as they were, the ids do not)
        Assert.False(LoadOrder.AsMadeByLinks(Swap(26, 27), out var why));
        Assert.Contains("in the backpack the item in slot 27", why);
        // two worn items swapped within their link
        Assert.False(LoadOrder.AsMadeByLinks(Swap(256, 257), out why));
        Assert.Contains("in link 0", why);
        // a worn item and a backpack item trade places (links of several items): their ranges overlap
        Assert.False(LoadOrder.AsMadeByLinks(Swap(263, 7), out why));
        Assert.Contains("overlap", why);
        // an item from a chest (made long before the join) in place of the first backpack item: the ranges overlap
        var old = RealJoin().Select(i => i.Slot == 7 ? (7, 1043u, i.Stable) : i).ToList();
        Assert.False(LoadOrder.AsMadeByLinks(old, out why));
        Assert.Contains("overlap", why);
        // an item made since the join in place of the last one the loader made: outside the run
        var later = RealJoin().Select(i => i.Slot == 325 ? (325, 20000u, i.Stable) : i).ToList();
        Assert.False(LoadOrder.AsMadeByLinks(later, out why));
        Assert.Contains("came in", why);
    }

    [Fact]
    public void APlayersJoinWithLookAlikesInTwoLinksIsNotAsMade()
    {
        // A one-item link and a one-item backpack trading their items keeps every id check: only look-alikes make that
        // invisible, so look-alikes in two links turn the slot rule off (twins set aside, a unique item by rule 2).
        List<(int, uint, StableFingerprint)> worn = [(256, 500u, Helm.Stable), (10, 501u, Sword.Stable)];
        Assert.True(LoadOrder.AsMadeByLinks(worn, out _));
        List<(int, uint, StableFingerprint)> twins = [(256, 500u, Sword.Stable), (10, 501u, Sword.Stable)];
        Assert.False(LoadOrder.AsMadeByLinks(twins, out var why));
        Assert.Contains("look-alikes", why);
        Assert.Contains("slots 10, 256", why);
        // look-alikes inside one link are what the order check is for
        List<(int, uint, StableFingerprint)> backpack = [(256, 500u, Helm.Stable), (10, 501u, Sword.Stable), (11, 502u, Sword.Stable)];
        Assert.True(LoadOrder.AsMadeByLinks(backpack, out _));
    }
}
