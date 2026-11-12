// SPDX-License-Identifier: MIT
// A seeded random test of the matcher over many holders. Each holder lives through: a save (records and witness, settled or
// not); for an unsettled one, a last simulation step the game saved and Kiln did not see; changes nobody saw between the save
// and the load; the loader making the holder's item entities (ids from one growing counter, in slot order); and changes
// nobody saw between the load and the match (swaps, moves, items taken out, items made after the load). Twins, worn items,
// records from older observations, records without a witness, records set aside before, items that already carry data,
// trusted and not, and a generation that claims trust it does not have. For every outcome the invariants must hold.
using Xunit;

namespace Kiln;

public sealed class InstanceMatcherPropertyTests(ITestOutputHelper output)
{
    private sealed class Thing
    {
        public int Identity;
        public InstanceFields Fields;
        public uint Id;          // entity id (0 before the load)
        public int LoadSlot = -1;
        public bool MadeAfterLoad;
        public bool AddedSinceSave;
    }

    private sealed record Case(MatchRecord[] Records, int[] RecordIdentity, MatchItem[] Items, Thing[] ItemThings, HashSet<int> Present,
        Dictionary<uint, Witness> Witnesses, uint? Trusted, bool AsMade, bool LoadSlotsKnown, bool HonestTrust, bool World, string Story);

    private static readonly InstanceFields[] Pool =
    [
        Rec.Of(Rec.Sword, level: 5),
        Rec.Of(Rec.Sword, level: 7),
        Rec.Of(Rec.Ring, level: 2, perksAvailable: 2),
        Rec.Of(Rec.Ring, level: 2, perksAvailable: 2, dye: 0xd7e00001),
    ];

    private const uint Holder = 1000;

    private static Case Make(Random rng)
    {
        var next = 0;
        // durability per item: distinct beyond the ratio tolerance, so the only undetectable cases (look-alikes swapped, or
        // one taken out and another put in, with nothing else changed) cannot arise unless the generator makes them on purpose
        var wear = Enumerable.Range(0, 400).Select(i => 5 * i).OrderBy(_ => rng.Next()).ToArray();
        var wearAt = 0;
        InstanceFields New() => Pool[rng.Next(Pool.Length)] with { Durability = wear[wearAt++], DurabilityMax = 1000 };
        var story = new List<string>();

        // the holder at the save: slot -> thing
        var state = new Dictionary<int, Thing>();
        var n = rng.Next(0, 9);
        while (state.Count < n) state.TryAdd(rng.Next(16), new Thing { Identity = next++, Fields = New() });
        const uint seen = 10, older = 4, missing = 77;
        var settled = rng.NextDouble() < 0.8;
        var witnesses = new Dictionary<uint, Witness>
        {
            [seen] = new(state.Select(kv => new WitnessEntry(kv.Key, kv.Value.Fields)).ToList(), settled),
        };
        var records = new List<MatchRecord>();
        var recordId = new List<int>();
        foreach (var (slot, t) in state)
        {
            if (rng.NextDouble() >= 0.7) continue;
            records.Add(new MatchRecord(slot, t.Fields.Stable, rng.NextDouble() < 0.05 ? missing : seen,
                Aside: rng.NextDouble() < 0.05, OnlyTrusted: rng.NextDouble() < 0.05));
            recordId.Add(t.Identity);
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
            witnesses[older] = new Witness(w, rng.NextDouble() < 0.8);
        }

        void Change(int ops, bool afterLoad, ref uint counter, string phase)
        {
            for (var o = 0; o < ops; o++)
            {
                var free = Enumerable.Range(0, 16).Where(s => !state.ContainsKey(s)).ToArray();
                var used = state.Keys.ToArray();
                switch (rng.Next(5))
                {
                    case 0 when used.Length > 0 && free.Length > 0:
                    {
                        var a = used[rng.Next(used.Length)];
                        var b = free[rng.Next(free.Length)];
                        state[b] = state[a];
                        state.Remove(a);
                        story.Add($"{phase}: move {a}->{b}");
                        break;
                    }
                    case 1 when used.Length > 1:
                    {
                        var a = used[rng.Next(used.Length)];
                        var b = used[rng.Next(used.Length)];
                        (state[a], state[b]) = (state[b], state[a]);
                        story.Add($"{phase}: swap {a}<->{b}");
                        break;
                    }
                    case 2 when used.Length > 0:
                    {
                        var a = used[rng.Next(used.Length)];
                        state.Remove(a);
                        story.Add($"{phase}: take {a}");
                        break;
                    }
                    case 3 when free.Length > 0:
                    {
                        var b = free[rng.Next(free.Length)];
                        var t = new Thing { Identity = next++, Fields = New(), AddedSinceSave = true };
                        if (afterLoad)
                        {
                            t.Id = counter++;
                            t.MadeAfterLoad = true;
                        }
                        state[b] = t;
                        story.Add($"{phase}: put new #{t.Identity} in {b}");
                        break;
                    }
                    case 4 when used.Length > 0:
                    {
                        var a = used[rng.Next(used.Length)];
                        state[a].Fields = state[a].Fields with { Durability = wear[wearAt++] };
                        story.Add($"{phase}: wear {a}");
                        break;
                    }
                }
            }
        }

        uint none = 0;
        // the last simulation step before the snapshot: the game saved it, an unsettled observation did not see it
        if (!settled && rng.NextDouble() < 0.5) Change(1, false, ref none, "last step");
        var ops1 = rng.Next(0, 5);
        Change(ops1, false, ref none, "save->load");
        // the loader: ids from the growing counter, in flat-slot order, right after the holder
        var id = Holder + 1;
        foreach (var (slot, t) in state.OrderBy(kv => kv.Key))
        {
            t.Id = id++;
            t.LoadSlot = slot;
            t.MadeAfterLoad = false;
        }
        var counter = id + (uint)rng.Next(0, 3);
        Change(rng.NextDouble() < 0.5 ? 0 : rng.Next(1, 4), true, ref counter, "load->match");

        var ordered = state.OrderBy(kv => kv.Key).ToArray();
        var items = ordered.Select(kv => new MatchItem(kv.Key, kv.Value.Fields, HasLiveData: rng.NextDouble() < 0.05)).ToArray();
        var things = ordered.Select(kv => kv.Value).ToArray();
        // a world holder (3 in 4) or a player (no generation, no anchor: the player's entity id is a session slot);
        // trust: none; the generation, given honestly (only when nothing changed between save and load); or claimed regardless
        var world = rng.NextDouble() < 0.75;
        var mode = world ? rng.Next(3) : 1;
        uint? trusted = mode switch { 0 => ops1 == 0 ? seen : null, 1 => null, _ => seen };
        // as the store computes it
        var made = items.Length;
        IReadOnlyList<int>? loadSlots = null;
        if (trusted is { } g && witnesses.TryGetValue(g, out var gw) && gw.Settled)
        {
            made = gw.Entries.Count;
            loadSlots = gw.Entries.Select(e => e.Slot).ToList();
        }
        var asMade = LoadOrder.AsMade(new EntityId(Holder), world, ordered.Select(kv => (kv.Key, kv.Value.Id)).ToList(), made, loadSlots, out _);
        return new Case(records.ToArray(), recordId.ToArray(), items, things, things.Select(t => t.Identity).ToHashSet(), witnesses, trusted, asMade,
            loadSlots is not null, mode != 2 || ops1 == 0, world, (world ? "chest" : "player") + ": " + string.Join("; ", story));
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
            var thing = c.ItemThings[o.Item];
            if (rec.Aside) bad.Add($"record {o.Record} was set aside before and got attached");
            if (item.HasLiveData) bad.Add($"record {o.Record} attached to an item that carries this session's data");
            if (item.Fields.Stable != rec.Stable) bad.Add($"record {o.Record} {rec.Stable} attached to {item.Fields.Stable}");
            // no witness, no attachment
            if (!c.Witnesses.ContainsKey(rec.Seen)) bad.Add($"record {o.Record} has no witness and got attached");
            var ruleOne = c.AsMade && InstanceMatcher.Trusted(rec, c.Witnesses, c.Items, c.Trusted, c.World);
            if (ruleOne)
            {
                // under rule 1 every attachment is the record's own slot, with equal item id and stable fingerprint
                if (item.Slot != rec.Slot) bad.Add($"trusted record {o.Record} (slot {rec.Slot}) attached in slot {item.Slot}");
                // and the item is one the loader made, in the slot it made it in (the slot: where the loader's slots are known)
                if (c.HonestTrust && thing.MadeAfterLoad) bad.Add($"record {o.Record} attached by rule 1 to item #{thing.Identity}, made after the load");
                if (c.HonestTrust && c.LoadSlotsKnown && thing.LoadSlot != item.Slot)
                    bad.Add($"record {o.Record} attached by rule 1 to item #{thing.Identity}, which moved from slot {thing.LoadSlot} to {item.Slot} after the load");
            }
            else
            {
                if (rec.OnlyTrusted) bad.Add($"record {o.Record} may come back only to an unchanged holder, attached to a changed one");
                // otherwise no attached pair has a twin on either side
                var itemTwins = c.Items.Count(i => i.Fields.Stable == rec.Stable);
                var recordTwins = c.Records.Count(x => !x.Aside && x.Stable == rec.Stable);
                if (itemTwins != 1) bad.Add($"untrusted record {o.Record} attached with {itemTwins} look-alike items in the holder");
                if (recordTwins != 1) bad.Add($"untrusted record {o.Record} attached with {recordTwins} look-alike records in the holder");
            }
            // ground truth: never another item's data, except what nobody can see: the record's own item gone and a look-alike
            // put in since the save
            if (c.HonestTrust && c.RecordIdentity[o.Record] is var truth and >= 0 && thing.Identity != truth &&
                !(!c.Present.Contains(truth) && thing.AddedSinceSave))
                bad.Add($"record of item #{truth} (slot {rec.Slot}) went to item #{thing.Identity} (slot {item.Slot})" +
                        $"{(c.Present.Contains(truth) ? ", and its own item is still there" : ", which was there at the save")}");
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
        int twinsSeen = 0, trustedCases = 0, notAsMade = 0, ruleOne = 0;
        var failures = new List<string>();
        for (var h = 0; h < holders; h++)
        {
            var c = Make(rng);
            var outcome = InstanceMatcher.Match(c.Records, c.Witnesses, c.Items, c.Trusted, c.AsMade, c.World);
            foreach (var o in outcome) counts[o.Kind] = counts.GetValueOrDefault(o.Kind) + 1;
            if (c.Items.GroupBy(i => i.Fields.Stable).Any(g => g.Count() > 1)) twinsSeen++;
            if (c.Trusted is not null) trustedCases++;
            if (!c.AsMade) notAsMade++;
            ruleOne += outcome.Count(o => o.Attached && c.AsMade && InstanceMatcher.Trusted(c.Records[o.Record], c.Witnesses, c.Items, c.Trusted, c.World));
            foreach (var f in Check(c, outcome))
                if (failures.Count < 20) failures.Add($"seed {seed} holder {h} (trust {c.Trusted?.ToString() ?? "none"}, as made {c.AsMade}; {c.Story}): {f}");
                else break;
        }
        output.WriteLine($"seed {seed}: {holders} holders, {twinsSeen} with twins, {trustedCases} with a trusted generation, {notAsMade} not as made, " +
                         $"{ruleOne} attached by rule 1; " + string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        foreach (var f in failures) output.WriteLine(f);
        // the generator must reach every kind of outcome, and rule 1 must be exercised, or the invariants were not
        foreach (var kind in Enum.GetValues<MatchKind>()) Assert.True(counts.GetValueOrDefault(kind) > 0, $"no {kind} outcome was produced");
        Assert.True(ruleOne > 0 && notAsMade > 0, "rule 1 and the load-order check were not both exercised");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
