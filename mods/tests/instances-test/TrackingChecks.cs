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

    // ---- what the per-step read costs: 10, 100, 1000 followed items ----
    [LabCheck(50, "tracking.cost")]
    private async Task TrackingCost()
    {
        var sword = Sword();
        if (sword == null) return;
        var given = new List<(EntityId Holder, ItemChange Change)>();
        var holders = await QuietContainers(4, 200);
        // more room: chests of the world's own container template next to the first one (the lab is a copy)
        if (holders.Count > 0 && Spawn.Inspect(holders[0]) is { Template: { } tmpl } info)
        {
            var spawned = new List<EntityId>();
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    var e = Spawn.Entity(tmpl, new WorldPosition(info.Position.X + 2 * (i % 8), info.Position.Y + 40, info.Position.Z + 2 * (i / 8)),
                        0, new SpawnOptions { Flags = SpawnFlags.Transient, Tag = "instances-test" }).GetAwaiter().GetResult();
                    spawned.Add(e);
                }
                catch (Exception ex)
                {
                    Info($"cost: spawning a container failed: {ex.Message}");
                    break;
                }
            }
            await Until(() => spawned.All(e => Items.Read(e) is { Initialized: true }), 200);
            holders.AddRange(spawned.Where(e => Items.Read(e) is { Initialized: true }));
            Info($"cost: {spawned.Count} container(s) spawned, {holders.Count} holder(s) to fill");
        }
        var pending = new List<(EntityId, Task<ItemsResult>)>();
        var before = new HashSet<EntityId>();
        var planned = 0;
        foreach (var h in holders)
        {
            var inv = Items.Read(h);
            var free = inv?.FreeSlots() ?? 0;
            if (free == 0) continue;
            foreach (var s in inv!.Slots.Where(x => !x.ItemEntity.IsNone)) before.Add(s.ItemEntity);
            pending.Add((h, Items.Give(h, sword.Id, free, options: GiveOptions.NoMerge)));
            planned += free;
            if (planned >= 1100) break;
        }
        foreach (var (h, task) in pending)
        {
            await task;
            // a result lists 16 changes at most: the read names every new item entity
            foreach (var s in Items.Read(h)!.Slots.Where(x => x.Item == sword.Id && !x.Ref.IsNone && !before.Contains(x.ItemEntity)))
                given.Add((h, new ItemChange(s.Slot, s.Item, 1, s.ItemEntity) { Ref = s.Ref }));
        }
        Info($"cost: {given.Count} item entities given ({pending.Count} give(s))");
        foreach (var n in new[] { 10, 100, 1000 })
        {
            if (given.Count < n)
            {
                Info($"cost: only {given.Count} item entities, not {n}");
                continue;
            }
            var handles = given.Take(n).Select(x => Items.LabFollow(x.Change.Ref)).ToList();
            await Steps(70);
            var status = Items.LabInstancesStatus;
            var us = Regex.Match(status, @"([0-9.]+) us per step").Groups[1].Value;
            Check(status.Contains($"followed {n},") && us.Length > 0, $"cost: {n} followed items: {us} us per step ({status})");
            foreach (var h in handles) h.Dispose();
        }
        // take everything back
        foreach (var grp in given.GroupBy(x => x.Holder))
            foreach (var (_, ch) in grp)
                await Items.Take(grp.Key, sword.Id, 1, ch.Slot, true, ch.ItemEntity);
        Info($"cost: taken back; {Items.LabInstancesStatus}");
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
        foreach (var r in recs)
        {
            var holder = new EntityId(r.Holder);
            var info = Spawn.Inspect(holder);
            Check(info != null && info.TemplateName == r.Template, $"check 5: holder {holder} is the same container after the restart ({info?.TemplateName})");
            var match = Items.Read(holder)?.Slots.FirstOrDefault(s => !s.Ref.IsNone && s.Ref.Location() is { } l && l.FlatSlot == r.FlatSlot);
            var loc = match?.Ref.Location();
            Check(match != null && match.Item.Value == r.Item && match.Level == r.Level,
                $"check 5: flat slot {r.FlatSlot} holds the same item after the restart: {match?.Ref} (was entity {r.Entity}), level {match?.Level} " +
                $"(was {r.Level}), slot {loc?.Slot} (was {new SlotId(r.Slot)})");
            if (match != null) await Items.Take(holder, match.Item, 1, match.Slot, true, match.ItemEntity);
        }
        File.Delete(file);
        File.Delete(Path.Combine(Context.DataDirectory, "boot1.done"));
    }
}
