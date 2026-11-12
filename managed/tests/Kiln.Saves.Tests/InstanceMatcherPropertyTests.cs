// SPDX-License-Identifier: MIT
// A seeded random test of the matcher over many holders: twins, moves, swaps, items added and removed between save and load,
// worn items, records from older observations, records set aside before, items that already carry data, trusted and not
// (and a generation that claims trust it does not have). For every outcome the invariants must hold.
using Xunit;

namespace Kiln;

public sealed class InstanceMatcherPropertyTests(ITestOutputHelper output)
{
    private sealed record Case(MatchRecord[] Records, int[] RecordIdentity, MatchItem[] Items, int[] ItemIdentity, HashSet<int> Added,
        Dictionary<uint, IReadOnlyList<WitnessEntry>> Witnesses, uint? Trusted, bool HonestTrust, int Ops);

    private static readonly InstanceFields[] Pool =
    [
        Rec.Of(Rec.Sword, level: 5),
        Rec.Of(Rec.Sword, level: 7),
        Rec.Of(Rec.Ring, level: 2, perksAvailable: 2),
        Rec.Of(Rec.Ring, level: 2, perksAvailable: 2, dye: 0xd7e00001),
    ];

    private static Case Make(Random rng)
    {
        var next = 0;
        // durability per item: distinct beyond the ratio tolerance, so the only undetectable case (look-alikes swapped with
        // nothing else changed) cannot arise and ground truth can be checked in every honest case
        var wear = Enumerable.Range(0, 200).Select(i => 5 * i).OrderBy(_ => rng.Next()).ToArray();
        var wearAt = 0;
        InstanceFields New() => Pool[rng.Next(Pool.Length)] with { Durability = wear[wearAt++], DurabilityMax = 1000 };

        // the holder at the save: slot -> (identity, fields)
        var atSave = new Dictionary<int, (int id, InstanceFields f)>();
        var n = rng.Next(0, 9);
        while (atSave.Count < n) atSave.TryAdd(rng.Next(16), (next++, New()));
        const uint seen = 10, older = 4;
        var witnesses = new Dictionary<uint, IReadOnlyList<WitnessEntry>>
        {
            [seen] = atSave.Select(kv => new WitnessEntry(kv.Key, kv.Value.f)).ToList(),
        };
        var records = new List<MatchRecord>();
        var recordId = new List<int>();
        foreach (var (slot, (id, f)) in atSave)
        {
            if (rng.NextDouble() >= 0.7) continue;
            records.Add(new MatchRecord(slot, f.Stable, seen, Aside: rng.NextDouble() < 0.05, OnlyTrusted: rng.NextDouble() < 0.05));
            recordId.Add(id);
        }
        // records carried over from an older observation (their own witness, their item unknown here)
        if (rng.NextDouble() < 0.3)
        {
            var w = new List<WitnessEntry>();
            var slots = new HashSet<int>();
            for (var k = rng.Next(1, 4); k > 0; k--)
            {
                var s = rng.Next(16);
                if (!slots.Add(s)) continue;
                var f = New();
                w.Add(new WitnessEntry(s, f));
                if (rng.NextDouble() < 0.6)
                {
                    records.Add(new MatchRecord(s, f.Stable, older, Aside: rng.NextDouble() < 0.1));
                    recordId.Add(-1);
                }
            }
            witnesses[older] = w;
        }

        // between save and load
        var now = atSave.ToDictionary(kv => kv.Key, kv => kv.Value);
        var added = new HashSet<int>();
        var ops = rng.Next(0, 5);
        for (var o = 0; o < ops; o++)
        {
            var free = Enumerable.Range(0, 16).Where(s => !now.ContainsKey(s)).ToArray();
            var used = now.Keys.ToArray();
            switch (rng.Next(5))
            {
                case 0 when used.Length > 0 && free.Length > 0:  // move
                {
                    var a = used[rng.Next(used.Length)];
                    var b = free[rng.Next(free.Length)];
                    now[b] = now[a];
                    now.Remove(a);
                    break;
                }
                case 1 when used.Length > 1:  // swap
                {
                    var a = used[rng.Next(used.Length)];
                    var b = used[rng.Next(used.Length)];
                    (now[a], now[b]) = (now[b], now[a]);
                    break;
                }
                case 2 when used.Length > 0:  // removed (trash, crafted, played elsewhere)
                    now.Remove(used[rng.Next(used.Length)]);
                    break;
                case 3 when free.Length > 0:  // added
                {
                    var id = next++;
                    added.Add(id);
                    now[free[rng.Next(free.Length)]] = (id, New());
                    break;
                }
                case 4 when used.Length > 0:  // worn
                {
                    var a = used[rng.Next(used.Length)];
                    now[a] = (now[a].id, now[a].f with { Durability = wear[wearAt++] });
                    break;
                }
            }
        }
        var items = now.OrderBy(kv => kv.Key).Select(kv => new MatchItem(kv.Key, kv.Value.f, HasLiveData: rng.NextDouble() < 0.05)).ToArray();
        var itemId = now.OrderBy(kv => kv.Key).Select(kv => kv.Value.id).ToArray();
        var changed = !InstanceMatcher.SameAsWitness(witnesses[seen], items);
        // trust: none; the generation, given honestly (only when nothing changed); or claimed although things changed
        var mode = rng.Next(3);
        uint? trusted = mode switch { 0 => changed ? null : seen, 1 => null, _ => seen };
        return new Case(records.ToArray(), recordId.ToArray(), items, itemId, added, witnesses, trusted, mode != 2 || !changed, ops);
    }

    /// <summary>Checks one case; returns the failures in words.</summary>
    private static List<string> Check(Case c, MatchOutcome[] outcome)
    {
        var bad = new List<string>();
        if (outcome.Length != c.Records.Length) bad.Add($"{outcome.Length} outcomes for {c.Records.Length} records");
        for (var r = 0; r < outcome.Length; r++)
            if (outcome[r].Record != r) bad.Add($"outcome {r} is for record {outcome[r].Record}");
        var attached = outcome.Where(o => o.Attached).ToArray();
        // no item gets two records
        foreach (var g in attached.GroupBy(o => o.Item).Where(g => g.Count() > 1))
            bad.Add($"item in slot {c.Items[g.Key].Slot} got {g.Count()} records");
        foreach (var o in outcome)
        {
            var rec = c.Records[o.Record];
            if (!o.Attached)
            {
                if (o.Item != -1) bad.Add($"record {o.Record} is {o.Kind} but names item {o.Item}");
                continue;
            }
            // a record goes to one item, which exists
            if (o.Item < 0 || o.Item >= c.Items.Length)
            {
                bad.Add($"record {o.Record} attached to item {o.Item} of {c.Items.Length}");
                continue;
            }
            var item = c.Items[o.Item];
            if (rec.Aside) bad.Add($"record {o.Record} was set aside before and got attached");
            if (item.HasLiveData) bad.Add($"record {o.Record} attached to an item that carries this session's data");
            if (item.Fields.Stable != rec.Stable) bad.Add($"record {o.Record} {rec.Stable} attached to {item.Fields.Stable}");
            var trusted = InstanceMatcher.Trusted(rec, c.Witnesses, c.Items, c.Trusted);
            if (trusted)
            {
                // under a trusted state every attachment is the record's own slot, with equal item id and stable fingerprint
                if (item.Slot != rec.Slot) bad.Add($"trusted record {o.Record} (slot {rec.Slot}) attached in slot {item.Slot}");
            }
            else
            {
                if (rec.OnlyTrusted) bad.Add($"record {o.Record} may come back only to an unchanged holder, attached to a changed one");
                // under an untrusted state no attached pair has a twin on either side
                var itemTwins = c.Items.Count(i => i.Fields.Stable == rec.Stable);
                var recordTwins = c.Records.Count(x => !x.Aside && x.Stable == rec.Stable);
                if (itemTwins != 1) bad.Add($"untrusted record {o.Record} attached with {itemTwins} look-alike items in the holder");
                if (recordTwins != 1) bad.Add($"untrusted record {o.Record} attached with {recordTwins} look-alike records in the holder");
            }
            // ground truth: never another item's data, except the one thing nobody can see: the record's own item gone and a
            // look-alike added since the save
            if (c.HonestTrust && c.RecordIdentity[o.Record] is var truth and >= 0)
            {
                var got = c.ItemIdentity[o.Item];
                var ownGone = !c.ItemIdentity.Contains(truth);
                if (got != truth && !(ownGone && c.Added.Contains(got)))
                    bad.Add($"record of item #{truth} (slot {rec.Slot}) went to item #{got} (slot {item.Slot}){(ownGone ? " which was there at the save" : "")}");
            }
        }
        return bad;
    }

    [Theory]
    [InlineData(20261111, 4000)]
    [InlineData(7, 4000)]
    [InlineData(424242, 4000)]
    public void InvariantsHoldOverManyRandomHolders(int seed, int holders)
    {
        var rng = new Random(seed);
        var counts = new Dictionary<MatchKind, int>();
        int twinsSeen = 0, trustedCases = 0;
        var failures = new List<string>();
        for (var h = 0; h < holders; h++)
        {
            var c = Make(rng);
            var outcome = InstanceMatcher.Match(c.Records, c.Witnesses, c.Items, c.Trusted);
            foreach (var o in outcome) counts[o.Kind] = counts.GetValueOrDefault(o.Kind) + 1;
            if (c.Items.GroupBy(i => i.Fields.Stable).Any(g => g.Count() > 1)) twinsSeen++;
            if (c.Trusted is not null) trustedCases++;
            foreach (var f in Check(c, outcome))
                if (failures.Count < 20) failures.Add($"seed {seed} holder {h} ({c.Ops} changes, trust {c.Trusted?.ToString() ?? "none"}): {f}");
                else break;
        }
        output.WriteLine($"seed {seed}: {holders} holders, {twinsSeen} with twins, {trustedCases} with a trusted generation; " +
                         string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var f in failures) output.WriteLine(f);
        // the generator must reach every kind of outcome, or the invariants were not exercised
        foreach (var kind in Enum.GetValues<MatchKind>()) Assert.True(counts.GetValueOrDefault(kind) > 0, $"no {kind} outcome was produced");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
