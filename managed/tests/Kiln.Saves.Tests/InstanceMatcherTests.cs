// SPDX-License-Identifier: MIT
// The matcher's named cases (PLAN D6 rules 1 to 4), on the pure function.
using Xunit;

namespace Kiln;

public sealed class InstanceMatcherTests
{
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 5);
    private static readonly InstanceFields Sword7 = Rec.Of(Rec.Sword, level: 7);
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring, level: 2, perksAvailable: 2, perksUnlocked: 1);
    private static readonly Dictionary<uint, IReadOnlyList<WitnessEntry>> NoWitness = new();

    private static MatchRecord R(int slot, InstanceFields f, uint seen = 10, bool aside = false, bool onlyTrusted = false) => new(slot, f.Stable, seen, aside, onlyTrusted);

    private static MatchItem I(int slot, InstanceFields f, bool live = false) => new(slot, f, live);

    private static Dictionary<uint, IReadOnlyList<WitnessEntry>> W(uint seen, params (int slot, InstanceFields f)[] list) =>
        new() { [seen] = list.Select(x => new WitnessEntry(x.slot, x.f)).ToList() };

    private static MatchOutcome[] Run(MatchRecord[] records, MatchItem[] items, uint? trusted = null, Dictionary<uint, IReadOnlyList<WitnessEntry>>? witness = null) =>
        InstanceMatcher.Match(records, witness ?? NoWitness, items, trusted);

    [Fact]
    public void ExactUnderTheWorldFilesOwnGenerationAttachesTwinsToTheirOwnSlots()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword)], trusted: 10);
        Assert.Equal((MatchKind.AttachExact, 0), (o[0].Kind, o[0].Item));
        Assert.Equal((MatchKind.AttachExact, 1), (o[1].Kind, o[1].Item));
    }

    [Fact]
    public void ExactWhenTheHolderEqualsItsWitnessAttachesTwinsToTheirOwnSlots()
    {
        var w = W(10, (3, Sword), (5, Sword), (6, Ring));
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword), I(6, Ring)], trusted: null, witness: w);
        Assert.All(o, x => Assert.Equal(MatchKind.AttachExact, x.Kind));
        Assert.Equal([0, 1], o.Select(x => x.Item));
    }

    [Fact]
    public void AnOlderObservationIsNotTrustedByTheGeneration()
    {
        // generation 12 is the world file's own, but the records were observed at 10 (carried over unread): twins are not resolved
        var o = Run([R(3, Sword, seen: 10), R(5, Sword, seen: 10)], [I(3, Sword), I(5, Sword)], trusted: 12);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void MovedWhenTheOneItemThatFitsIsInAnotherSlot()
    {
        var o = Run([R(3, Ring)], [I(7, Ring), I(3, Sword)]);
        Assert.Equal(MatchKind.AttachMoved, o[0].Kind);
        Assert.Equal(0, o[0].Item);
        Assert.Contains("moved from slot 3 to 7", o[0].Reason);
    }

    [Fact]
    public void UntrustedButStillInItsSlotIsExact()
    {
        var o = Run([R(3, Ring)], [I(3, Ring), I(4, Sword)]);
        Assert.Equal((MatchKind.AttachExact, 0), (o[0].Kind, o[0].Item));
    }

    [Fact]
    public void TwinsInAChangedHolderAreBothSetAside()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(3, Sword), I(5, Sword), I(9, Ring)]);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
        Assert.All(o, x => Assert.Equal(-1, x.Item));
    }

    [Fact]
    public void OneRecordAndTwoCandidatesIsAmbiguous()
    {
        var o = Run([R(3, Sword)], [I(3, Sword), I(4, Sword)]);
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
        Assert.Contains("2 items and 1 record", o[0].Reason);
    }

    [Fact]
    public void TwoRecordsAndOneCandidateAreBothSetAside()
    {
        var o = Run([R(3, Sword), R(5, Sword)], [I(4, Sword)]);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void NoCandidateIsParked()
    {
        var o = Run([R(3, Ring)], [I(3, Sword)]);
        Assert.Equal(MatchKind.Parked, o[0].Kind);
    }

    [Fact]
    public void TheStableFingerprintMustAgree()
    {
        // same item id, other level: not the same item
        var o = Run([R(3, Sword)], [I(3, Sword7)], trusted: 10);
        Assert.Equal(MatchKind.Parked, o[0].Kind);
        var u = Run([R(3, Sword)], [I(3, Sword7)]);
        Assert.Equal(MatchKind.Parked, u[0].Kind);
    }

    [Fact]
    public void ARecordSetAsideEarlierIsNeverAttached()
    {
        var o = Run([R(3, Ring, aside: true)], [I(3, Ring)], trusted: 10);
        Assert.Equal(MatchKind.AlreadyAside, o[0].Kind);
        Assert.False(o[0].Attached);
    }

    [Fact]
    public void ATrustedRecordWhoseSlotHoldsSomethingElseWaitsInsteadOfMoving()
    {
        var o = Run([R(3, Ring)], [I(7, Ring)], trusted: 10);
        Assert.Equal(MatchKind.Parked, o[0].Kind);
    }

    [Fact]
    public void ARecordGoneAtTheSaveComesBackOnlyToAnUnchangedHolder()
    {
        var w = W(10, (3, Ring));
        var unchanged = Run([R(3, Ring, onlyTrusted: true)], [I(3, Ring)], witness: w);
        Assert.Equal(MatchKind.AttachExact, unchanged[0].Kind);
        var changed = Run([R(3, Ring, onlyTrusted: true)], [I(4, Ring)], witness: w);
        Assert.Equal(MatchKind.Parked, changed[0].Kind);
    }

    [Fact]
    public void AnItemThatAlreadyCarriesThisSessionsDataTakesNoRecord()
    {
        var o = Run([R(3, Ring)], [I(3, Ring, live: true)], trusted: 10);
        Assert.Equal(MatchKind.SetAside, o[0].Kind);
    }

    [Fact]
    public void AnOlderRecordWithTheFingerprintOfATrustedOneIsSetAside()
    {
        // seen 10 = trusted generation (twins in 3 and 5); a parked record from an earlier session has the same fingerprint
        var o = Run([R(3, Sword, seen: 10), R(5, Sword, seen: 10), R(8, Sword, seen: 4)], [I(3, Sword), I(5, Sword)], trusted: 10);
        Assert.Equal(MatchKind.AttachExact, o[0].Kind);
        Assert.Equal(MatchKind.AttachExact, o[1].Kind);
        Assert.Equal(MatchKind.SetAside, o[2].Kind);
    }

    [Fact]
    public void TwoTrustedRecordsForOneSlotAreBothSetAside()
    {
        var w = W(4, (3, Sword));
        var o = Run([R(3, Sword, seen: 10), R(3, Sword, seen: 4)], [I(3, Sword)], trusted: 10, witness: w);
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
        var w = W(4, (3, worn), (5, fresh));
        var o = Run([R(3, worn, seen: 4), R(5, fresh, seen: 4)], [I(3, fresh), I(5, worn)], witness: w);
        Assert.All(o, x => Assert.Equal(MatchKind.SetAside, x.Kind));
    }

    [Fact]
    public void ResultsAreIndexedByRecordAndEveryRecordGetsOne()
    {
        var recs = new[] { R(1, Ring), R(2, Sword), R(3, Sword), R(4, Sword7) };
        var o = Run(recs, [I(1, Ring), I(2, Sword), I(9, Sword7)]);
        Assert.Equal(recs.Length, o.Length);
        Assert.Equal([0, 1, 2, 3], o.Select(x => x.Record));
        Assert.Equal(MatchKind.AttachExact, o[0].Kind);
        Assert.Equal(MatchKind.SetAside, o[1].Kind);   // two sword records, one sword
        Assert.Equal(MatchKind.SetAside, o[2].Kind);
        Assert.Equal(MatchKind.AttachMoved, o[3].Kind);
    }
}
