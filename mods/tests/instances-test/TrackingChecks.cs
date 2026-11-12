// SPDX-License-Identifier: MIT
// Checks 4-5 of task t1 (following items): the three causes of ItemMovedEvent and a gone item; what the per-step read
// costs with many followed items; flat slots before a save equal flat slots after a restart.
using System.Text.Json;
using System.Text.RegularExpressions;
using Kiln;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    // ---- check 4: Transaction, Transfer, Observed, gone ----
    [LabCheck(40, "tracking.causes")]
    private async Task TrackingCauses()
    {
        // c takes everything b holds (a move all), so c is the emptiest and b one whose stacks all fit into c
        var quiet = (await QuietContainers(2, 60)).Select(e => (E: e, Inv: Items.Read(e)!)).OrderByDescending(x => x.Inv.FreeSlots()).ToList();
        var cPick = quiet.FirstOrDefault();
        var bPick = quiet.Skip(1).FirstOrDefault(x => x.Inv.UsedSlots + 1 <= cPick.Inv?.FreeSlots());
        var aPick = quiet.Skip(1).FirstOrDefault(x => x.E != bPick.E);
        Check(cPick.Inv != null && bPick.Inv != null && aPick.Inv != null,
            $"check 4: three quiet containers, the move all's target with room for its source ({quiet.Count} quiet)");
        if (cPick.Inv == null || bPick.Inv == null || aPick.Inv == null) return;
        var (a, b, c) = (aPick.E, bPick.E, cPick.E);
        var sword = Sword();
        if (sword == null) return;
        var g = await Items.Give(a, sword.Id, 1);
        var gc = g.Changes.First(x => !x.ItemEntity.IsNone);
        var item = gc.Ref;
        var events = new List<ItemMovedEvent>();
        using var sub = Events.On<ItemMovedEvent>(events.Add);
        ItemLocation? goneAt = null;
        ulong goneStep = 0;
        using var follow = Items.LabFollow(item, (loc, step) => { goneAt = loc; goneStep = step; });
        await Steps(3);
        Check(events.Count == 0, $"check 4: following {item}: nothing reported while it stays ({events.Count})");

        // 1. Kiln's own move: Transaction
        var m = await Items.Move(a, SlotOf(a, gc.ItemEntity)!.Slot, b);
        await Until(() => events.Count >= 1, 10);
        var e1 = events.ElementAtOrDefault(0);
        Check(m.Succeeded && e1 is { Cause: ItemMoveCause.Transaction } && e1.Item == item && e1.From.Holder == a && e1.To.Holder == b,
            $"check 4: Items.Move: {e1}");

        // 2. the game's move all, by Kiln: Transfer (its transfer report makes the tracker read again at once)
        var before = Items.Read(b)!;
        var ma = await Items.MoveAll(b, c);
        await Until(() => events.Count >= 2, 10);
        var e2 = events.ElementAtOrDefault(1);
        Check(ma.Succeeded && e2 is { Cause: ItemMoveCause.Transfer } && e2.From.Holder == b && e2.To.Holder == c, $"check 4: Items.MoveAll: {e2}");
        // put back what else moved from b to c
        foreach (var ch in ma.Changes.Where(x => x.ItemEntity != gc.ItemEntity))
            await Items.Move(c, ch.To, b, ch.ItemEntity.IsNone ? ch.Amount : 0, toSlot: ch.From);
        var restored = Items.Read(b)!.Slots.Count(s => !s.IsEmpty) == before.Slots.Count(s => !s.IsEmpty) - 1;
        Info($"check 4: move all put back by its ledger: {(restored ? "yes" : "no")} ({ma.Changes.Count} piece(s))");

        // 3. a move no hook names (kept from the tracker): Observed, from the per-step read
        var stepBefore = e2?.Step ?? 0;
        var u = await Items.LabMoveUnseen(c, SlotOf(c, gc.ItemEntity)!.Slot, a);
        await Until(() => events.Count >= 3, 20);
        var e3 = events.ElementAtOrDefault(2);
        Check(u.Succeeded && e3 is { Cause: ItemMoveCause.Observed } && e3.From.Holder == c && e3.To.Holder == a,
            $"check 4: a move the hooks do not see (Items.LabMoveUnseen, the lab switch): {e3} (the move all was at step {stepBefore})");

        // 4. taken: gone, once, with its last location
        var last = SlotOf(a, gc.ItemEntity);
        var t = await Items.Take(a, sword.Id, 1, last!.Slot, true, gc.ItemEntity);
        await Until(() => goneAt != null, 10);
        Check(t.Succeeded && goneAt is { } gl && gl.Holder == a && gl.Slot == last.Slot,
            $"check 4: taken: gone once at step {goneStep} with its last location {goneAt}");
        await Steps(3);
        Check(events.Count == 3, $"check 4: every move reported once ({events.Count} event(s))");
    }

    // ---- the per-step read's budget: many item entities, the cost with and without it, the bound on lateness ----
    private readonly List<EntityId> _spawnedChests = [];
    private readonly List<(EntityId Holder, InventorySlot Slot)> _many = [];
    private ulong _now;  // the step of the latest tick callback

    private void Clock(in TickInfo t) => _now = t.Step;

    /// <summary>Up to <paramref name="want"/> item entities (swords) in spawned chests, with their refs.</summary>
    private async Task MakeItems(int want)
    {
        var sword = Sword();
        var near = (await QuietContainers(1, 1)).FirstOrDefault();
        var tmpl = Spawn.Lookup("Prop_Storage_40_T5_Chest_Silver") ?? Spawn.Lookup("Prop_Storage_32_T2_Chest_Wood") ??
                   (Spawn.Inspect(near) is { Template: { } g } ? Spawn.Template(g) : null);
        var at = Spawn.Inspect(near)?.Position;
        if (sword == null || tmpl == null || at is not { } p)
        {
            Check(false, $"items for the budget: a sword {sword}, a chest template {tmpl?.Name}, a place {at}");
            return;
        }
        var made = 0;
        for (var i = 0; made < want && i < 480; i++)
        {
            try
            {
                var e = Spawn.Entity(tmpl.Id, new WorldPosition(p.X + 3 * (i % 20), p.Y + 60, p.Z + 3 * (i / 20)), 0,
                    new SpawnOptions { Flags = SpawnFlags.Transient, Tag = "instances-test" }).GetAwaiter().GetResult();
                _spawnedChests.Add(e);
            }
            catch (Exception ex)
            {
                Info($"budget: spawning a chest failed after {i}: {ex.Message}");
                break;
            }
            if (i % 20 == 19)
            {
                await Until(() => _spawnedChests.All(c => Items.Read(c) is { Initialized: true }), 100);
                made = _spawnedChests.Sum(c => Items.Read(c)?.FreeSlots() ?? 0);
            }
        }
        await Until(() => _spawnedChests.All(c => Items.Read(c) is { Initialized: true }), 200);
        var gives = new List<Task<ItemsResult>>();
        var planned = 0;
        foreach (var c in _spawnedChests)
        {
            var free = Items.Read(c)?.FreeSlots() ?? 0;
            if (free == 0 || planned >= want) continue;
            var n = Math.Min(free, want - planned);
            gives.Add(Items.Give(c, sword!.Id, n, options: GiveOptions.NoMerge));
            planned += n;
        }
        foreach (var task in gives) await task;
        foreach (var c in _spawnedChests)
            foreach (var slot in Items.Read(c)?.Slots.Where(x => !x.Ref.IsNone) ?? [])
                _many.Add((c, slot));
        Info($"budget: {_spawnedChests.Count} chest(s) of {tmpl.Name} spawned, {_many.Count} item entities in them");
    }

    private static readonly System.Text.RegularExpressions.Regex Cost = new(
        @"budget (\S+) .*rotation at (\d+) of (\d+), rechecking (\d+).* ([0-9.]+) us per step, worst ([0-9.]+) us, ([0-9.]+) items per step \(avg of (\d+)\)");

    [LabCheck(70, "tracking.budget")]
    private async Task TrackingBudget()
    {
        await MakeItems(10000);
        if (_many.Count < 100) return;
        Info($"budget: load {LoadAverage()}");
        for (var pass = 1; pass <= 2; pass++)
            foreach (var n in new[] { 10, 100, 1000, 10000 })
            {
                if (_many.Count < n)
                {
                    Info($"budget: only {_many.Count} item entities, not {n}");
                    continue;
                }
                var handles = _many.Take(n).Select(x => Items.LabFollow(x.Slot.Ref)).ToList();
                foreach (var budget in new[] { 64, 0 })
                {
                    Items.LabTrackerReadsPerStep = budget;
                    await Steps(70);
                    var st = Items.LabInstancesStatus;
                    var m = Cost.Match(st);
                    Check(m.Success && m.Groups[8].Value == "64",
                        $"budget pass {pass}: {n} followed, {(budget == 0 ? "no budget" : $"budget {budget}")}: " +
                        (m.Success ? $"{m.Groups[5].Value} us per step, worst {m.Groups[6].Value} us, {m.Groups[7].Value} items per step" : st));
                }
                Items.LabTrackerReadsPerStep = 64;
                foreach (var h in handles) h.Dispose();
                await Steps(2);
            }
        Info($"budget: load {LoadAverage()}");
    }

    // With more followed items than the budget, an unseen move of the item the rotation has just read is reported within
    // ceil(followed / 64) + 1 steps of the step it was asked in (+1: the move itself runs in the next sim step).
    [LabCheck(71, "tracking.rotation-bound")]
    private async Task TrackingRotationBound()
    {
        if (_many.Count < 1000) await MakeItems(1000);
        const int n = 1000;
        if (_many.Count < n) return;
        Context.Tick.Pre += Clock;
        var events = new List<ItemMovedEvent>();
        using var sub = Events.On<ItemMovedEvent>(events.Add);
        var set = _many.Take(n).ToList();
        var handles = set.Select(x => Items.LabFollow(x.Slot.Ref)).ToList();
        var (lastHolder, last) = set[^1];
        // the spawned chests are full: the move goes to a container of the world with room
        var target = (await QuietContainers(1, 1)).FirstOrDefault();
        Check(!target.IsNone, $"rotation bound: a container with room for the move: {target}");
        if (target.IsNone) return;
        // a full rotation (the ring drops earlier followers at its wrap), then the step that reads the last item
        await Steps(2 * (n / 64 + 2));
        var ok = await Until(() => Cost.Match(Items.LabInstancesStatus) is { Success: true } m && m.Groups[2].Value == m.Groups[3].Value &&
                                   m.Groups[3].Value == n.ToString(), 200);
        var asked = _now;
        var mv = Items.LabMoveUnseen(lastHolder, last.Slot, target);
        var bound = (ulong)((n + 63) / 64 + 2);
        await Until(() => events.Any(e => e.Item == last.Ref), (int)bound + 20);
        var ev = events.FirstOrDefault(e => e.Item == last.Ref);
        var r = await mv;
        Check(ok && r.Succeeded && ev is { Cause: ItemMoveCause.Observed } && ev.Step - asked <= bound,
            $"rotation bound: {n} followed, the last item of the rotation moved unseen at step {asked} (right after its read): " +
            $"{(ev == null ? "not reported" : $"reported at step {ev.Step}, {ev.Step - asked} step(s) later (bound {bound})")}");
        foreach (var h in handles) h.Dispose();
        Context.Tick.Pre -= Clock;
        await Items.Move(target, SlotOf(target, last.ItemEntity)?.Slot ?? default, lastHolder);
    }

    // A holder's teardown: the holder goes in one call, its items one step later; the read in between finds them placed
    // nowhere. No move may be reported for that read, and the gone carries the last placed location.
    [LabCheck(72, "tracking.teardown")]
    private async Task TrackingTeardown()
    {
        if (_spawnedChests.Count == 0) await MakeItems(40);
        var chest = _spawnedChests.LastOrDefault();
        var items = _many.Where(x => x.Holder == chest).Take(5).ToList();
        if (chest.IsNone || items.Count == 0) return;
        Context.Tick.Pre += Clock;
        var events = new List<ItemMovedEvent>();
        using var sub = Events.On<ItemMovedEvent>(events.Add);
        var gone = new Dictionary<ItemInstanceRef, (ItemLocation At, ulong Step)>();
        Items.LabTrackerReadsPerStep = 0;  // every followed item every step: the one-step gap is read for sure
        var handles = items.Select(x => Items.LabFollow(x.Slot.Ref, (at, step) => gone[x.Slot.Ref] = (at, step))).ToList();
        await Steps(3);
        var destroyedAt = _now;
        Spawn.Destroy(chest);
        _spawnedChests.Remove(chest);
        await Until(() => gone.Count == items.Count, 20);
        var right = items.Count(x => gone.TryGetValue(x.Slot.Ref, out var g) && g.At.Holder == chest && g.At.IsPlaced && g.At.Slot == x.Slot.Slot);
        Check(events.Count == 0 && right == items.Count,
            $"teardown: chest {chest} destroyed at step {destroyedAt}: {events.Count} move(s) reported (want 0); {gone.Count} gone, " +
            $"{right} with their last placed location in the chest; gone at step(s) {string.Join(",", gone.Values.Select(g => g.Step).Distinct())}");
        Items.LabTrackerReadsPerStep = 64;
        foreach (var h in handles) h.Dispose();
        Context.Tick.Pre -= Clock;
        _many.RemoveAll(x => x.Holder == chest);
    }

    [LabCheck(73, "tracking.budget-cleanup")]
    private async Task TrackingBudgetCleanup()
    {
        foreach (var c in _spawnedChests) Spawn.Destroy(c);
        Info($"budget: {_spawnedChests.Count} spawned chest(s) removed with their items; {Items.LabInstancesStatus}");
        _spawnedChests.Clear();
        _many.Clear();
        await Steps(3);
    }

    private static string LoadAverage()
    {
        try
        {
            return File.ReadAllText(@"Z:\proc\loadavg").Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    // ---- check 5: flat slots before a save = flat slots after a restart ----
    private sealed record FlatRecord(uint Holder, string Template, double X, double Y, double Z, int FlatSlot, ulong Slot, uint Item,
        int Level, uint Entity);

    [LabCheck(60, "tracking.flatslots.save", Boot = 1)]
    private async Task FlatSlotsSave()
    {
        var containers = await QuietContainers(3, 3);
        var sword = Sword();
        if (containers.Count < 2 || sword == null)
        {
            Check(false, $"check 5: two quiet containers and a gear item ({containers.Count})");
            return;
        }
        var recs = new List<FlatRecord>();
        foreach (var h in containers.Take(3))
        {
            var inv = Items.Read(h)!;
            // an empty slot in the last link that has one (a second or third link shows the link x 8 numbering)
            var free = inv.Slots.Where(s => s.IsEmpty && s.Category == InventoryCategory.Generic).OrderByDescending(s => s.Link).FirstOrDefault();
            if (free == null) continue;
            var lv = Math.Max(1, sword.LevelMin);
            var g = await Items.Give(h, sword.Id, 1, new Gear(lv, ItemRarity.Rare), slot: free.Slot);
            var ch = g.Changes.FirstOrDefault(x => !x.Ref.IsNone);
            var loc = ch.Ref.IsNone ? null : ch.Ref.Location();
            var read = ch.Ref.IsNone ? null : SlotOf(h, ch.ItemEntity);
            var info = Spawn.Inspect(h);
            Check(loc is { IsSavedSlot: true } && read != null, $"check 5: gave {ch.Ref} into {h} slot {free.Slot} (link {free.Link}): location {loc}");
            if (loc is not { } l || read == null || info == null) continue;
            recs.Add(new FlatRecord(h.Value, info.TemplateName ?? "", info.Position.X, info.Position.Y, info.Position.Z, l.FlatSlot,
                l.Slot.Value, sword.Id.Value, read.Level, ch.ItemEntity.Value));
        }
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Path.Combine(Context.DataDirectory, "flat-slots.json"), JsonSerializer.Serialize(recs));
        File.WriteAllText(Path.Combine(Context.DataDirectory, "boot1.done"), DateTimeOffset.UtcNow.ToString("O"));
        Info($"check 5: {recs.Count} item(s) recorded with their flat slots; waiting 40 s for an autosave, then restart the lab");
        await Steps(25 * 40);
        Info("check 5: boot 1 done (an autosave has run): stop and start the lab for boot 2");
    }

    [LabCheck(60, "tracking.flatslots.restart", Boot = 2)]
    private async Task FlatSlotsRestart()
    {
        var file = Path.Combine(Context.DataDirectory, "flat-slots.json");
        var recs = File.Exists(file) ? JsonSerializer.Deserialize<List<FlatRecord>>(File.ReadAllText(file)) ?? [] : [];
        Check(recs.Count > 0, $"check 5: {recs.Count} record(s) from boot 1");
        var containers = Items.FindInventories(InventoryKinds.Containers);
        foreach (var r in recs)
        {
            // entity ids change at a restart (the loader hands them out in load order): find the holder by template and
            // position, as instance data does, and only when exactly one container fits
            var near = containers.Where(e => Spawn.Inspect(e) is { } i && i.TemplateName == r.Template &&
                                             Math.Abs(i.Position.X - r.X) < 0.5 && Math.Abs(i.Position.Y - r.Y) < 0.5 &&
                                             Math.Abs(i.Position.Z - r.Z) < 0.5).ToList();
            Check(near.Count == 1, $"check 5: the holder {r.Template} at ({r.X:0.0}, {r.Y:0.0}, {r.Z:0.0}), entity {r.Holder} before the restart, " +
                                   $"is found again: {string.Join(", ", near)}");
            if (near.Count != 1) continue;
            var holder = near[0];
            var match = Items.Read(holder)?.Slots.FirstOrDefault(s => !s.Ref.IsNone && s.Ref.Location() is { } l && l.FlatSlot == r.FlatSlot);
            var loc = match?.Ref.Location();
            Check(match != null && match.Item.Value == r.Item && loc is { IsSavedSlot: true },
                $"check 5: flat slot {r.FlatSlot} of holder {holder} (was {r.Holder}) holds the same item after the restart: {match?.Ref} " +
                $"(was entity {r.Entity}), slot {loc?.Slot} (was {new SlotId(r.Slot)})");
            if (match != null) await Items.Take(holder, match.Item, 1, match.Slot, true, match.ItemEntity);
        }
        File.Delete(file);
        File.Delete(Path.Combine(Context.DataDirectory, "boot1.done"));
    }
}
