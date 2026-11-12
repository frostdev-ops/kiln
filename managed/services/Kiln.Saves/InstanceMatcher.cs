// SPDX-License-Identifier: MIT
// The rule that decides, after a load or a join, which saved record belongs to which item (PLAN D6 rules 1 to 4). A pure
// function: the records of one holder, the witnesses they were saved with, the holder's item entities now, and which
// generation the world file vouches for. The one failure it may never have is an item getting another item's data, so
// where it is not certain it sets the record aside or leaves it parked; it never guesses and never takes "the first".
namespace Kiln;

/// <summary>One entry of a witness: what one slot of the holder held at a save (flat slot, item id, full fingerprint).</summary>
internal readonly record struct WitnessEntry(int Slot, InstanceFields Fields);

/// <summary>A saved record as the matcher sees it.</summary>
/// <param name="Slot">The flat slot it was saved at.</param>
/// <param name="Stable">Item id and the stable part of its fingerprint.</param>
/// <param name="Seen">The observation it was saved with (the generation whose save read it fresh), the key of its witness.</param>
/// <param name="Aside">Set aside in an earlier session: never attached again.</param>
/// <param name="OnlyTrusted">May attach only through a trusted holder state, never by rule 2 (a record of an item that
/// was gone from a player's inventory at the save: destroyed, or the player leaving, not known yet).</param>
internal readonly record struct MatchRecord(int Slot, StableFingerprint Stable, uint Seen, bool Aside = false, bool OnlyTrusted = false);

/// <summary>An item entity of the holder now.</summary>
/// <param name="Slot">Its flat slot.</param>
/// <param name="Fields">Its save-record fields now.</param>
/// <param name="HasLiveData">It already carries instance data of this session (it can not take a saved record too).</param>
internal readonly record struct MatchItem(int Slot, InstanceFields Fields, bool HasLiveData = false);

internal enum MatchKind
{
    /// <summary>Attach to the item in the record's own slot.</summary>
    AttachExact,
    /// <summary>Attach to the one item that fits, in another slot.</summary>
    AttachMoved,
    /// <summary>Not certain: never attached, reported (rule 3).</summary>
    SetAside,
    /// <summary>No candidate: waits (rule 4).</summary>
    Parked,
    /// <summary>Set aside in an earlier session; stays so.</summary>
    AlreadyAside,
}

/// <summary>The decision for one record.</summary>
/// <param name="Record">Index into the records given.</param>
/// <param name="Kind">What happens to it.</param>
/// <param name="Item">Index into the items given; -1 unless attached.</param>
/// <param name="Reason">Why, in words.</param>
internal readonly record struct MatchOutcome(int Record, MatchKind Kind, int Item, string Reason)
{
    public bool Attached => Kind is MatchKind.AttachExact or MatchKind.AttachMoved;
}

internal static class InstanceMatcher
{
    /// <summary>Durability and fuel ratios count as unchanged within this much (the loader rebuilds them from a ratio and
    /// may round by one unit).</summary>
    internal const double RatioTolerance = 0.002;

    /// <summary>Whether the holder's list now equals a witness: the same slots, each with the same item id and full
    /// fingerprint (stable part equal, durability and fuel ratios within <see cref="RatioTolerance"/>).</summary>
    public static bool SameAsWitness(IReadOnlyList<WitnessEntry> witness, IReadOnlyList<MatchItem> items)
    {
        if (witness.Count != items.Count) return false;
        var a = witness.OrderBy(w => w.Slot).ToArray();
        var b = items.OrderBy(i => i.Slot).ToArray();
        for (var i = 0; i < a.Length; i++)
            if (a[i].Slot != b[i].Slot || !SameFull(a[i].Fields, b[i].Fields))
                return false;
        return true;
    }

    /// <summary>The full fingerprint: the stable part, and durability and fuel as ratios.</summary>
    public static bool SameFull(in InstanceFields a, in InstanceFields b) =>
        a.Stable == b.Stable && SameRatio(a.Durability, a.DurabilityMax, b.Durability, b.DurabilityMax) &&
        SameRatio(a.Fuel, a.FuelMax, b.Fuel, b.FuelMax);

    private static bool SameRatio(int cur1, int max1, int cur2, int max2)
    {
        if (max1 <= 0 || max2 <= 0) return max1 <= 0 && max2 <= 0 && cur1 == cur2;
        return Math.Abs((double)cur1 / max1 - (double)cur2 / max2) <= RatioTolerance;
    }

    /// <summary>Whether a record's holder state is trusted (rule 1): its observation is the generation the world file
    /// vouches for (<paramref name="trustedGeneration"/>: given only for a world holder whose loaded generation is
    /// Exact or Staged), or the holder's list now equals the witness it was saved with.</summary>
    public static bool Trusted(in MatchRecord r, IReadOnlyDictionary<uint, IReadOnlyList<WitnessEntry>> witnesses,
        IReadOnlyList<MatchItem> items, uint? trustedGeneration) =>
        (trustedGeneration is { } g && g != 0 && r.Seen == g) ||
        (r.Seen != 0 && witnesses.TryGetValue(r.Seen, out var w) && SameAsWitness(w, items));

    /// <summary>Decides every record of one holder. Pure: nothing is read or changed.</summary>
    /// <param name="records">The holder's saved records (waiting and set aside alike).</param>
    /// <param name="witnesses">Observation -> the holder's whole list of item entities at that save.</param>
    /// <param name="items">The holder's item entities now.</param>
    /// <param name="trustedGeneration">The generation whose observations the world file vouches for; null for a player,
    /// or for a world whose loaded generation is older than its file (or none).</param>
    public static MatchOutcome[] Match(IReadOnlyList<MatchRecord> records, IReadOnlyDictionary<uint, IReadOnlyList<WitnessEntry>> witnesses,
        IReadOnlyList<MatchItem> items, uint? trustedGeneration)
    {
        var outcome = new MatchOutcome?[records.Count];
        var bySlot = new Dictionary<int, int>();
        for (var i = 0; i < items.Count; i++)
            if (!bySlot.TryAdd(items[i].Slot, i))
                throw new ArgumentException($"two items in flat slot {items[i].Slot}", nameof(items));

        // What a record would take, before conflicts are looked at: record -> (item, kind, reason).
        var proposal = new Dictionary<int, (int item, MatchKind kind, string reason)>();
        var untrusted = new List<int>();
        for (var r = 0; r < records.Count; r++)
        {
            var rec = records[r];
            if (rec.Aside)
            {
                outcome[r] = new MatchOutcome(r, MatchKind.AlreadyAside, -1, "set aside in an earlier session; it is never attached automatically");
                continue;
            }
            if (Trusted(rec, witnesses, items, trustedGeneration))
            {
                // Rule 1: nothing moved unseen, so the record's own slot is the item, twins included.
                if (bySlot.TryGetValue(rec.Slot, out var it) && items[it].Fields.Stable == rec.Stable)
                    proposal[r] = (it, MatchKind.AttachExact, $"the holder is as it was saved: the item in its own slot {rec.Slot}");
                else
                    outcome[r] = new MatchOutcome(r, MatchKind.Parked, -1, bySlot.TryGetValue(rec.Slot, out var other)
                        ? $"the holder is as it was saved, but slot {rec.Slot} holds {items[other].Fields.Stable}, not {rec.Stable}"
                        : $"the holder is as it was saved, but slot {rec.Slot} is empty");
                continue;
            }
            if (rec.OnlyTrusted)
            {
                outcome[r] = new MatchOutcome(r, MatchKind.Parked, -1,
                    "it was gone from the player's inventory at the save, so it only comes back to an unchanged inventory, and this one has changed");
                continue;
            }
            untrusted.Add(r);
        }

        // Rule 2: the one item of the holder with this item id and stable fingerprint, wanted by exactly one record.
        // Every active record counts as wanting it (trusted ones too): a twin on either side is never resolved.
        var active = Enumerable.Range(0, records.Count).Where(r => !records[r].Aside).ToArray();
        foreach (var group in untrusted.GroupBy(r => records[r].Stable))
        {
            var fp = group.Key;
            var candidates = Enumerable.Range(0, items.Count).Where(i => items[i].Fields.Stable == fp).ToArray();
            var wanting = active.Count(r => records[r].Stable == fp);
            if (candidates.Length == 0)
            {
                foreach (var r in group)
                    outcome[r] = new MatchOutcome(r, MatchKind.Parked, -1, $"no item {fp} in the holder");
                continue;
            }
            if (candidates.Length == 1 && wanting == 1)
            {
                var r = group.Single();
                var it = candidates[0];
                // At the save the holder had a look-alike of this item too, and one of the two is gone: which one is not known.
                if (witnesses.TryGetValue(records[r].Seen, out var saved) && saved.Count(e => e.Fields.Stable == fp) is var twins and > 1)
                {
                    outcome[r] = new MatchOutcome(r, MatchKind.SetAside, -1,
                        $"at the save the holder had {twins} items {fp}; now it has one, and which one is gone is not known");
                    continue;
                }
                proposal[r] = items[it].Slot == records[r].Slot
                    ? (it, MatchKind.AttachExact, $"the holder changed; the one item {fp} is still in its slot {records[r].Slot}")
                    : (it, MatchKind.AttachMoved, $"the holder changed; the one item {fp} moved from slot {records[r].Slot} to {items[it].Slot}");
                continue;
            }
            var why = $"{candidates.Length} item{(candidates.Length == 1 ? "" : "s")} and {wanting} record{(wanting == 1 ? "" : "s")} {fp} in a holder that changed since the save";
            foreach (var r in group) outcome[r] = new MatchOutcome(r, MatchKind.SetAside, -1, why);
        }

        // Rule 3: several records for one item: every one of them is set aside.
        foreach (var crowd in proposal.GroupBy(p => p.Value.item).Where(g => g.Count() > 1).ToArray())
            foreach (var (r, _) in crowd)
            {
                proposal.Remove(r);
                outcome[r] = new MatchOutcome(r, MatchKind.SetAside, -1, $"{crowd.Count()} records want the item in slot {items[crowd.Key].Slot}");
            }

        foreach (var (r, (it, kind, reason)) in proposal)
            outcome[r] = items[it].HasLiveData
                ? new MatchOutcome(r, MatchKind.SetAside, -1, $"the item in slot {items[it].Slot} already carries data of this session")
                : new MatchOutcome(r, kind, it, reason);

        return outcome.Select(o => o!.Value).ToArray();
    }
}
