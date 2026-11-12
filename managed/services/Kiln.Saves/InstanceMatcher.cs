// SPDX-License-Identifier: MIT
// The rule that decides, after a load or a join, which saved record belongs to which item (PLAN D6 rules 1 to 4). A pure
// function: the records of one holder, the witnesses they were saved with, the holder's item entities now, which generation
// the world file vouches for, and whether the holder's items are as the loader made them. The one failure it may never have
// is an item getting another item's data, so where it is not certain it sets the record aside or leaves it parked; it never
// guesses and never takes "the first".
namespace Kiln;

/// <summary>One entry of a witness: what one slot of the holder held at a save (flat slot, item id, full fingerprint).</summary>
internal readonly record struct WitnessEntry(int Slot, InstanceFields Fields);

/// <summary>A holder's whole list of item entities at one save.</summary>
/// <param name="Entries">Every item entity of the holder then, with or without data.</param>
/// <param name="Settled">Read where no simulation step could follow before the game's snapshot (the snapshot staging, or
/// a shutdown save). Only a settled observation can vouch that the holder was saved as Kiln read it.</param>
internal sealed record Witness(IReadOnlyList<WitnessEntry> Entries, bool Settled);

/// <summary>A saved record as the matcher sees it.</summary>
/// <param name="Slot">The flat slot it was saved at.</param>
/// <param name="Stable">Item id and the stable part of its fingerprint.</param>
/// <param name="Seen">The observation it was saved with (the generation whose save read it fresh), the key of its witness.</param>
/// <param name="Aside">Set aside in an earlier session: never attached again.</param>
/// <param name="OnlyTrusted">May attach only through a trusted holder state, never by rule 2 (a record of an item that
/// was gone from a player's inventory at the save: destroyed, or the player leaving, not known yet).</param>
/// <param name="Alt">A player's record: its slot (-1: not in the inventory then) at every other state the client may bring
/// back as its character file (the join of that visit and the saves after it). A client brings back the file of the last
/// save it received, which need not be the last save Kiln read.</param>
internal readonly record struct MatchRecord(int Slot, StableFingerprint Stable, uint Seen, bool Aside = false, bool OnlyTrusted = false,
    IReadOnlyList<(uint Seen, int Slot)>? Alt = null)
{
    /// <summary>Every observation: the main one (when it has one) and the alternates.</summary>
    public IEnumerable<(uint Seen, int Slot)> Observations => (Seen != 0 ? [(Seen, Slot)] : Array.Empty<(uint, int)>()).Concat(Alt ?? []);
}

/// <summary>What a record's states say about the holder now.</summary>
internal enum Vouch
{
    /// <summary>Not vouched for: the holder changed since (rule 2 decides).</summary>
    No,
    /// <summary>The holder is as one of its states, and those agree on the item's slot.</summary>
    Slot,
    /// <summary>The holder is as states in which the item was not there: it is not in the holder.</summary>
    Absent,
    /// <summary>The holder is as several states that put the item in different places: not certain.</summary>
    Ambiguous,
}

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

/// <summary>Whether a holder's item entities are still as the loader made them in this session: the evidence rule 1 needs
/// for the time between the holder's load and the match, when nothing follows its items yet.</summary>
/// <remarks>The fact behind it: within one server world every non-player entity id comes from one counter that only
/// grows, and no id is used twice. If the loader makes a holder's item entities in one go, in flat-slot order, right after
/// the holder (the lab has to show this; one data point so far: chest 1042, its can in slot 3 entity 1043, its pouch in slot
/// 8 entity 1044), then a swap of two items breaks the ascending order, and an item made later or brought in from elsewhere
/// falls outside the loader's run of ids. The lab measurement decides the slack constants; it must never turn the
/// predicate into "always true": if the loader does not work that way, rule 1 stays off. A player's character loader does
/// not work that way (worn gear first): <see cref="AsMadeByLinks"/>.</remarks>
internal static class LoadOrder
{
    /// <summary>Condition 2: the ids span at most (made - 1 + this): a gapless run, today.</summary>
    internal const int SpanSlack = 0;

    /// <summary>Condition 3, for a holder whose own id comes from the same counter (a world holder): the items follow it,
    /// within (made + this) ids. Today: right after it.</summary>
    internal const int AnchorSlack = 0;

    /// <summary>Decides it for one holder.</summary>
    /// <param name="holder">The holder entity.</param>
    /// <param name="anchored">The holder's own id comes from the entity counter (world holders; a player's entity id is a
    /// session slot, so it is not).</param>
    /// <param name="items">The holder's item entities now: flat slot and entity id.</param>
    /// <param name="made">How many item entities the loader made for it: the item count of the generation's witness when
    /// the world file is that generation, else the count now.</param>
    /// <param name="loadSlots">The slots the loader filled, when known: the settled witness of the generation the world file
    /// vouches for. Then every one of those items must still be there, each in its slot.</param>
    /// <param name="why">The reason, in words.</param>
    /// <remarks>Ascending ids prove that no two items changed places, not that none moved: an item moved to an empty
    /// slot, or several shifted in order, keep the order. With the loader's slots known, the same slots filled by the same
    /// run of ids in order means every item is where it was made. Without them (a player's join, an older generation) a
    /// shift in order into the witness's slots after the load is not seen; it needs look-alikes with equal wear and a
    /// deliberate pattern of moves within the match delay.</remarks>
    public static bool AsMade(EntityId holder, bool anchored, IReadOnlyList<(int Slot, uint Id)> items, int made, IReadOnlyList<int>? loadSlots,
        out string why)
    {
        if (items.Count == 0)
        {
            why = "no items";
            return true;
        }
        if (items.Count > made)
        {
            why = $"{items.Count} items where the loader made {made}: one came in since";
            return false;
        }
        var sorted = items.OrderBy(i => i.Slot).ToArray();
        // 1. ids strictly ascending in flat-slot order: the identity is the only permutation that keeps a sorted list sorted
        for (var i = 1; i < sorted.Length; i++)
            if (sorted[i].Id <= sorted[i - 1].Id)
            {
                why = $"the item in slot {sorted[i].Slot} (entity {sorted[i].Id}) is older than the one in slot {sorted[i - 1].Slot} " +
                      $"(entity {sorted[i - 1].Id}): items changed places since they were made";
                return false;
            }
        // 2. one run: an item made after the load, or brought in from another holder, lies outside it
        var span = (long)sorted[^1].Id - sorted[0].Id;
        if (span > made - 1 + SpanSlack)
        {
            why = $"entity ids {sorted[0].Id} to {sorted[^1].Id} for {made} item(s) made by the loader: an item came in since";
            return false;
        }
        // 3. that run follows the holder
        if (anchored && (sorted[0].Id <= holder.Value || (long)sorted[^1].Id - holder.Value > made + AnchorSlack))
        {
            why = $"entity ids {sorted[0].Id} to {sorted[^1].Id} were not made with holder {holder}";
            return false;
        }
        // 4. the loader's own slots, each still holding its item
        if (loadSlots is not null && (items.Count != made || !sorted.Select(i => i.Slot).SequenceEqual(loadSlots.Order())))
        {
            why = items.Count != made
                ? $"{made - items.Count} of the {made} items the loader made are gone or elsewhere"
                : "an item moved to another slot since the load";
            return false;
        }
        why = $"entity ids ascend in slot order in one run after the holder";
        return true;
    }

    /// <summary>Flat slots from here on are not the save's own numbers: worn gear, currency, nested links (256 + the
    /// link's position in the holder's link tree x 8 + index).</summary>
    internal const int UnsavedSlotBase = 256;

    /// <summary>A player's run: at most this many ids that are not the inventory's items may lie among them (the link
    /// inventories, anything the loader makes for an item). Not measured yet; the join's log line lists the runs.</summary>
    internal const int PlayerSpanSlack = 64;

    /// <summary>A player's link group of a flat slot: -1 for the backpack (the saved slots 0-255, which the character
    /// saver numbers in one sequence), else the link tree position of an unsaved link.</summary>
    internal static int LinkGroup(int flatSlot) => flatSlot < UnsavedSlotBase ? -1 : (flatSlot - UnsavedSlotBase) / 8;

    private static string GroupName(int group) =>
        group < 0 ? "the backpack" : $"link {group} (slots {UnsavedSlotBase + group * 8}-{UnsavedSlotBase + group * 8 + 7})";

    /// <summary>The same question for a player's inventory just after its join, where the character loader's order is not
    /// the flat-slot order: a real join (dev26-playtest, 2026-11-12) made the item in flat slot 256 (worn) before the
    /// backpack's. So the order is checked per link group (the backpack's saved slots as one group, each other link as its
    /// own): within each group the ids ascend with the slot, and the groups' id ranges do not overlap, in whatever order the
    /// loader made the groups. All ids lie within one run (<see cref="PlayerSpanSlack"/>), so an item from elsewhere shows.</summary>
    /// <param name="items">The inventory now: flat slot, entity id, stable fingerprint.</param>
    /// <param name="why">The reason in words; it lists each group's id range, so a join's log line shows the loader's
    /// order.</param>
    /// <remarks>What the ids cannot show is a swap between two groups when nothing else breaks: two one-item groups trading
    /// their items, or equal groups trading all of them. That needs look-alikes in two groups (a move of one item to another
    /// group, with every slot's fingerprint as before, leaves a look-alike of it behind), so a holder with look-alikes in two
    /// groups (a sword worn and its twin in the backpack) is not "as made": its twins are set aside, a unique item still
    /// comes back by rule 2. Like <see cref="AsMade"/>, ascending ids prove that nothing changed places, not that nothing
    /// moved to an empty slot.</remarks>
    public static bool AsMadeByLinks(IReadOnlyList<(int Slot, uint Id, StableFingerprint Stable)> items, out string why)
    {
        if (items.Count == 0)
        {
            why = "no items";
            return true;
        }
        var groups = items.GroupBy(i => LinkGroup(i.Slot))
            .Select(g => (Group: g.Key, Items: g.OrderBy(i => i.Slot).ToArray()))
            .OrderBy(g => g.Items.Min(i => i.Id))
            .ToArray();
        var runs = string.Join(", ", groups.Select(g =>
        {
            var lo = g.Items.Min(i => i.Id);
            var hi = g.Items.Max(i => i.Id);
            return $"{GroupName(g.Group)} {lo}{(hi != lo ? $"-{hi}" : "")} ({g.Items.Length})";
        }));
        // 1. within a group, ids strictly ascending in slot order: no two of its items changed places
        foreach (var (group, sorted) in groups)
            for (var i = 1; i < sorted.Length; i++)
                if (sorted[i].Id <= sorted[i - 1].Id)
                {
                    why = $"in {GroupName(group)} the item in slot {sorted[i].Slot} (entity {sorted[i].Id}) is older than the one in slot " +
                          $"{sorted[i - 1].Slot} (entity {sorted[i - 1].Id}): items changed places since they were made; ids by link: {runs}";
                    return false;
                }
        // 2. the groups were made one after the other: an item moved between two groups of several items overlaps them
        for (var g = 1; g < groups.Length; g++)
        {
            var below = groups[g - 1];
            var above = groups[g];
            if (below.Items.Max(i => i.Id) > above.Items.Min(i => i.Id))
            {
                why = $"the ids of {GroupName(below.Group)} and {GroupName(above.Group)} overlap: items changed places between links since " +
                      $"they were made; ids by link: {runs}";
                return false;
            }
        }
        // 3. one run: an item brought in from elsewhere lies far outside it
        var span = (long)items.Max(i => i.Id) - items.Min(i => i.Id);
        if (span > items.Count - 1 + PlayerSpanSlack)
        {
            why = $"entity ids {items.Min(i => i.Id)} to {items.Max(i => i.Id)} for {items.Count} item(s): an item came in from elsewhere since " +
                  $"the join; ids by link: {runs}";
            return false;
        }
        // 4. look-alikes in two groups: a swap between them would leave every check above as it was
        foreach (var same in items.GroupBy(i => i.Stable))
        {
            var where = same.Select(i => LinkGroup(i.Slot)).Distinct().ToArray();
            if (where.Length > 1)
            {
                why = $"look-alikes {same.Key} in {GroupName(where[0])} and {GroupName(where[1])} (slots " +
                      $"{string.Join(", ", same.Select(i => i.Slot).Order())}): a swap between links would not show in the ids; ids by link: {runs}";
                return false;
            }
        }
        why = $"entity ids ascend in slot order within each link, one link after the other: {runs}";
        return true;
    }
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

    /// <summary>Whether a record's holder state is vouched for between the save and the load (the first half of rule 1):
    /// its witness exists and is settled, and the holder's list now equals that witness; for a world holder also its
    /// observation is the generation the world file vouches for (<paramref name="trustedGeneration"/>: given only when the
    /// loaded generation is Exact or Staged), so an edit of the world file that keeps its header shows as a changed list.
    /// Rule 1 also needs the holder's items as the loader made them.</summary>
    /// <remarks><c>world</c>: a world holder (a chest, an NPC); false for a player, whose state is vouched for by its
    /// witnesses alone.</remarks>
    public static bool Trusted(in MatchRecord r, IReadOnlyDictionary<uint, Witness> witnesses, IReadOnlyList<MatchItem> items, uint? trustedGeneration,
        bool world) => Vouched(r, witnesses, items, trustedGeneration, world).Kind == Vouch.Slot;

    /// <summary>What the record's states say about the holder now. A world holder: its one observation, the world file's own
    /// generation, settled, with an equal list. A player: every state the client may have brought back as its file whose
    /// list equals the inventory now; all settled; they must agree on the item's slot (or all lack it).</summary>
    public static (Vouch Kind, int Slot, string Why) Vouched(in MatchRecord r, IReadOnlyDictionary<uint, Witness> witnesses, IReadOnlyList<MatchItem> items,
        uint? trustedGeneration, bool world)
    {
        if (world)
            return r.Seen != 0 && witnesses.TryGetValue(r.Seen, out var w) && w.Settled && SameAsWitness(w.Entries, items) &&
                   trustedGeneration is { } g && r.Seen == g
                ? (Vouch.Slot, r.Slot, $"the holder is as it was saved: the item in its own slot {r.Slot}")
                : (Vouch.No, -1, "");
        var obs = r.Observations.ToList();
        if (obs.Count == 0 || obs.Any(o => !witnesses.ContainsKey(o.Seen))) return (Vouch.No, -1, "");
        // a state read one step early may differ from the file the client got by a swap nobody saw
        if (obs.Any(o => !witnesses[o.Seen].Settled)) return (Vouch.No, -1, "");
        var same = obs.Where(o => SameAsWitness(witnesses[o.Seen].Entries, items)).ToList();
        if (same.Count == 0) return (Vouch.No, -1, "");
        var slots = same.Select(o => o.Slot).Distinct().ToList();
        if (slots.Count > 1)
            return (Vouch.Ambiguous, -1, $"the inventory is as it was at {same.Count} saved states that put the item in different places " +
                                         $"(slots {string.Join(", ", slots.Select(x => x < 0 ? "none" : x.ToString()))}); which one the character came back as is not known");
        return slots[0] < 0
            ? (Vouch.Absent, -1, "the inventory is as it was when the item was not in it")
            : (Vouch.Slot, slots[0], slots[0] == r.Slot
                ? $"the inventory is as it was saved: the item in its own slot {r.Slot}"
                : $"the inventory is as the character's file of an earlier save had it: the item in slot {slots[0]}");
    }

    /// <summary>Decides every record of one holder. Pure: nothing is read or changed.</summary>
    /// <param name="records">The holder's saved records (waiting and set aside alike).</param>
    /// <param name="witnesses">Observation -> the holder's whole list of item entities at that save.</param>
    /// <param name="items">The holder's item entities now.</param>
    /// <param name="trustedGeneration">The generation whose observations the world file vouches for; null for a player,
    /// or for a world whose loaded generation is older than its file (or none).</param>
    /// <param name="asMade">The holder's items are as the loader made them in this session (<see cref="LoadOrder"/>):
    /// nothing changed places and nothing came in between the load and now. Without it rule 1 does not apply.</param>
    /// <param name="world">A world holder: rule 1 needs the world file's own generation and an equal list (a player: the
    /// list alone).</param>
    public static MatchOutcome[] Match(IReadOnlyList<MatchRecord> records, IReadOnlyDictionary<uint, Witness> witnesses,
        IReadOnlyList<MatchItem> items, uint? trustedGeneration, bool asMade, bool world)
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
            // No witness: nobody knows whether a look-alike stood beside it at the save, nor whether the holder is as saved.
            if (!rec.Observations.Any() || rec.Observations.Any(o => !witnesses.ContainsKey(o.Seen)))
            {
                outcome[r] = new MatchOutcome(r, MatchKind.SetAside, -1, rec.Seen == 0 || !witnesses.ContainsKey(rec.Seen)
                    ? "it was saved without the holder's list of items, so whether a look-alike stood beside it is not known"
                    : "one of the states the character may come back as was not read (a save that could not read the inventory)");
                continue;
            }
            var v = Vouched(rec, witnesses, items, trustedGeneration, world);
            if (v.Kind == Vouch.Ambiguous)
            {
                outcome[r] = new MatchOutcome(r, MatchKind.SetAside, -1, v.Why);
                continue;
            }
            if (v.Kind == Vouch.Absent)
            {
                outcome[r] = new MatchOutcome(r, MatchKind.Parked, -1, v.Why);
                continue;
            }
            if (asMade && v.Kind == Vouch.Slot)
            {
                // Rule 1: nothing moved unseen, so the slot the holder's state gives is the item, twins included.
                if (bySlot.TryGetValue(v.Slot, out var it) && items[it].Fields.Stable == rec.Stable)
                    proposal[r] = (it, MatchKind.AttachExact, v.Why);
                else
                    outcome[r] = new MatchOutcome(r, MatchKind.Parked, -1, bySlot.TryGetValue(v.Slot, out var other)
                        ? $"the holder is as it was saved, but slot {v.Slot} holds {items[other].Fields.Stable}, not {rec.Stable}"
                        : $"the holder is as it was saved, but slot {v.Slot} is empty");
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
                // At a save the holder had a look-alike of this item too, and one of the two is gone: which one is not known.
                // (A state in which the item was not there but a look-alike was: the look-alike may be what came back.)
                var doubt = records[r].Observations.Select(o => (o.Slot, Twins: witnesses[o.Seen].Entries.Count(e => e.Fields.Stable == fp)))
                    .FirstOrDefault(o => o.Slot >= 0 ? o.Twins > 1 : o.Twins > 0);
                if (doubt.Twins > 0)
                {
                    outcome[r] = new MatchOutcome(r, MatchKind.SetAside, -1, doubt.Slot >= 0
                        ? $"at a save the holder had {doubt.Twins} items {fp}; now it has one, and which one is gone is not known"
                        : $"at a saved state without this item the holder had a look-alike {fp}; the one now may be that one");
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
