// SPDX-License-Identifier: MIT
// Task t4, final pass: what the first runs did not cover, on the code with the tracker's budget and Kiln.Saves' one
// observation per record (run-t4.sh, phases of t4/data.json):
//   c1..c3  costs, in a fresh world before the data run: 1,000 identical swords in 48 spawned chests; per round two saves
//           with no instance data, then saves with 10, 100 and 1,000 tagged (kiln.log's staging lines, paired by the
//           revision this test logs); after each round a restart and the load's matching time.             stop, stop, stop
//   f1..f3  after the data run: (1) with more than 64 tagged items, a tagged sword moved by the unseen lab move and a
//           save one step later, kill -9, restart: its value on it, Exact at the new slot; (2) an ItemMovedEvent handler
//           that calls Get and Set on the moved item while a save is staged (the unseen move and Saves.Request() in one
//           step), kill -9, restart: no exception, the value consistent.                              kill, kill, end
// t4.t1end ends the boots of t1's own checks (run-t4.sh with t1's check names and t4.t1end).
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    // ---- the end of a run of t1's checks: boot 1 stop, boot 2 end ----

    [LabCheck(900, "t4.t1end")]
    private void T4T1End()
    {
        Directory.CreateDirectory(T4Dir);
        var f = Path.Combine(T4Dir, "t1-boots");
        var n = (File.Exists(f) ? int.Parse(File.ReadAllText(f)) : 0) + 1;
        File.WriteAllText(f, n.ToString());
        Ready(n >= 2 ? "end" : "stop");
    }

    // ---- costs ----

    private sealed class CostState
    {
        public List<HolderPos> Chests { get; set; } = [];
        public int Round { get; set; }
    }

    private string CostFile => Path.Combine(T4Dir, "cost.json");

    private async Task C1()
    {
        // 48 chests of the world's storage template, 40 m up (chests 60 m up did not come back after a restart)
        var containers = Items.FindInventories(InventoryKinds.Containers).Select(e => Spawn.Inspect(e)).Where(i => i is not null).ToList();
        var anchor = containers.FirstOrDefault(i => i!.TemplateName?.Contains("Storage_24") == true) ?? containers.FirstOrDefault();
        var template = anchor?.TemplateName?.Contains("Storage_24") == true ? anchor.Template!.Value
            : Spawn.Templates.First(t => t.Name.Contains("Storage_24")).Id;
        // A fresh, unstreamed world has no container anchor. These synthetic lab coordinates need no player.
        var at = anchor?.Position ?? new WorldPosition(0, 0, 0);
        var st = new CostState();
        var chests = new List<EntityId>();
        for (var i = 0; i < 48; i++)
        {
            var e = await Spawn.Entity(template,
                new WorldPosition(at.X - 30 + 2.5 * (i % 8), SafeY(at.Y + 40), at.Z - 60 + 2.5 * (i / 8)), 0,
                new SpawnOptions { Tag = "instances-test-t4-cost" });
            chests.Add(e);
        }
        await Until(() => chests.All(e => Items.Read(e) is { Initialized: true }), 200);
        var sword = Sword()!;
        var given = 0;
        foreach (var h in chests)
        {
            if (given >= 1000) break;
            var g = await Items.Give(h, sword.Id, Math.Min(24, 1000 - given), TwinGear(sword), GiveOptions.NoMerge);
            given += g.Done;
        }
        st.Chests = chests.Select(PosOf).ToList();
        File.WriteAllText(CostFile, JsonSerializer.Serialize(st));
        Info($"t4 cost: {given} swords in {chests.Count} spawned chests");
        await CostRound(st);
    }

    private List<(EntityId Holder, InventorySlot Slot)> CostSwords(CostState st)
    {
        var sword = Sword()!;
        var list = new List<(EntityId, InventorySlot)>();
        foreach (var p in st.Chests)
        {
            var h = Resolve(p);
            if (h.IsNone) continue;
            foreach (var s in Items.Read(h)!.Slots.Where(s => s.Item == sword.Id && !s.Ref.IsNone)) list.Add((h, s));
        }
        return list;
    }

    /// <summary>One round: no data (all removed), two saves; then 10, 100, 1000 tagged, a save each.</summary>
    private async Task CostRound(CostState st)
    {
        st.Round++;
        File.WriteAllText(CostFile, JsonSerializer.Serialize(st));
        var swords = CostSwords(st);
        var removed = swords.Count(x => x.Slot.Ref.Remove(Key));
        Info($"t4 cost round {st.Round}: {swords.Count} swords found, {removed} values removed; {InstanceData.All(Key).Count} values of this key left");
        for (var i = 1; i <= 2; i++)
        {
            await Steps(30);
            var r = await Saves.Request();
            Info($"t4 cost round {st.Round}: save 'no data {i}' revision {r.Revision} ({r})");
        }
        var tagged = 0;
        foreach (var n in new[] { 10, 100, 1000 })
        {
            for (; tagged < n && tagged < swords.Count; tagged++)
                swords[tagged].Slot.Ref.Set(Key, new T4Tag { Name = $"C{tagged}", Count = st.Round });
            await Steps(30);
            var r = await Saves.Request();
            Info($"t4 cost round {st.Round}: save '{tagged} tagged' revision {r.Revision} ({r})");
        }
        Next(st.Round >= 2 ? "c3" : "c2", "stop");
    }

    /// <summary>The load's matching of the 1000 tagged swords: every one its own value, and when the last came back.</summary>
    private async Task<bool> CostMatch(CostState st)
    {
        var sw = Stopwatch.StartNew();
        var maxStep = 0.0;
        var last = Stopwatch.GetTimestamp();
        void Measure(in TickInfo _)
        {
            var now = Stopwatch.GetTimestamp();
            maxStep = Math.Max(maxStep, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
        }
        Context.Tick.Pre += Measure;
        var swords = CostSwords(st);
        bool AllBack() => swords.Count(x => x.Slot.Ref.Has(Key)) >= 1000;
        await Until(AllBack, 25 * 120);
        Context.Tick.Pre -= Measure;
        swords = CostSwords(st);
        int own = 0, wrong = 0, none = 0;
        foreach (var (h, s) in swords)
        {
            if (!s.Ref.TryGet(Key, out var v)) none++;
            else if (v.Name.StartsWith('C') && v.Count == st.Round) own++;
            else wrong++;
        }
        // the truth is the slot: C<i> went to the i-th sword in chest order at tagging; the same order after the load
        var order = 0;
        var misplaced = 0;
        foreach (var (h, s) in swords)
        {
            if (s.Ref.TryGet(Key, out var v) && v.Name != $"C{order}") misplaced++;
            order++;
        }
        Check(wrong == 0 && misplaced == 0 && own == 1000,
            $"t4 cost round {st.Round}: after the restart {own} of 1000 tagged swords carry their own value ({misplaced} in another's slot, {none} none, {wrong} other); " +
            $"the test waited {sw.ElapsedMilliseconds} ms after it started, longest step {maxStep:F1} ms (the store's per-holder lines carry the times)");
        return true;
    }

    private async Task C2()
    {
        var st = JsonSerializer.Deserialize<CostState>(File.ReadAllText(CostFile))!;
        await CostMatch(st);
        await CostRound(st);
    }

    private async Task C3()
    {
        var st = JsonSerializer.Deserialize<CostState>(File.ReadAllText(CostFile))!;
        await CostMatch(st);
        var removed = CostSwords(st).Count(x => x.Slot.Ref.Remove(Key));
        await Saves.Request();
        Info($"t4 cost: done; {removed} values removed for the data run");
        Next("d1", "stop");
    }

    /// <summary>A height a spawned chest keeps over a restart. Chests spawned at y 895.5 and 913.5 were not in the world
    /// after a restart (kiln.spawn: "48 of 52 tracked records live"); chests at 873.5 and 875.5 were.</summary>
    private static double SafeY(double y) => Math.Min(y, 875.0);

    // ---- f1: a save right after an unseen move, with more than 64 tagged items ----

    private async Task F1()
    {
        // swords whose chest did not come back after a restart (spawned too high, see SafeY) are no longer checked
        foreach (var gone in _ds.Swords.Where(s => Resolve(s.Holder).IsNone).ToList())
        {
            Info($"t4 f1: {gone} is dropped from the checks: its chest is not in the world");
            _ds.Swords.Remove(gone);
        }
        await VerifyLoaded("f1", LoadedStates());
        var sword = Sword()!;
        var a = Spawn.Inspect(Chest("A"))!;
        // 10 chests of 24: 230 tagged twins B0.. (the per-step read covers 64 a step), an untagged twin BU in K9
        for (var i = 0; i < 10; i++)
        {
            var e = await Spawn.Entity(a.Template!.Value, new WorldPosition(a.Position.X + 20 + 2.5 * i, SafeY(a.Position.Y + 40), a.Position.Z + 20), 0,
                new SpawnOptions { Tag = "instances-test-t4-budget" });
            await Until(() => Items.Read(e) is { Initialized: true }, 100);
            _ds.Chests[$"K{i}"] = PosOf(e);
        }
        var n = 0;
        for (var i = 0; i < 9; i++)
            for (var j = 0; j < 23 && n < 200; j++)
                await Give($"B{n++}", $"K{i}", true, TwinGear(sword), sword);
        await Give("BU", "K9", false, TwinGear(sword), sword);
        await Steps(10);
        Info($"t4 f1: {InstanceData.All(Key).Count} values of this key live; {Items.LabInstancesStatus}");
        // the move, and a save one step later
        var t = Rec("B0");
        var from = Resolve(t.Holder);
        var slot = AtFlat(from, t.Flat)!;
        var to = Chest("K9");
        var stepAtMove = 0UL;
        void Note(in TickInfo ti) => stepAtMove = stepAtMove == 0 ? ti.Step : stepAtMove;
        Context.Tick.Pre += Note;
        var move = Items.LabMoveUnseen(from, slot.Slot, to);
        await Steps(1);
        Context.Tick.Pre -= Note;
        _inflight++;
        var save = Saves.Request();
        var m = await move;
        var now = Items.Read(to)!.Slots.FirstOrDefault(x => x.ItemEntity == _refs["B0"].Entity.Id);
        var oldFlat = t.Flat;
        if (now != null)
        {
            t.Holder = PosOf(to);
            t.Flat = FlatOf(Items.Read(to)!, now.Slot);
        }
        _inflight--;
        Changed("B0 moved unseen");
        var r = await save;
        Info($"t4 f1: B0 moved unseen ({m.Status}) from {from} flat {oldFlat} to K9 {to} flat {t.Flat}, step {stepAtMove}; the save {r}; " +
             $"Location() now {_refs["B0"].Location()}; {Items.LabInstancesStatus}");
        _ds.Notes["budget"] = JsonSerializer.Serialize(new { t.Holder, t.Flat, rev = r.Revision, oldFlat });
        Next("f2", "kill");
    }

    // ---- f2: B0 after the restart; then an ItemMovedEvent handler writes while a save is staged ----

    private async Task F2()
    {
        await VerifyLoaded("f2", LoadedStates());
        var b0 = Rec("B0");
        var at = AtFlat(Resolve(b0.Holder), b0.Flat);
        var ok = at is { IsEmpty: false } && at.Ref.TryGet(Key, out var v) && v.Name == "B0" && _restored.TryGetValue(at.Ref, out var how) && how == InstanceMatch.Exact;
        Check(ok, $"t4 f2 (budget): B0, moved unseen a step before the save, is at its new slot in K9 flat {b0.Flat} with its own value, restored Exact " +
                  $"({(at == null ? "no item there" : $"{at.Ref}: {(at.Ref.TryGet(Key, out var w) ? $"{w.Name} #{w.Count}" : "no value")}, " +
                                                          $"{(_restored.TryGetValue(at.Ref, out var hm) ? hm.ToString() : "no restored event")}")}); loaded {_worldLoaded} ({_ds.Notes.GetValueOrDefault("budget")})");

        // the handler
        var h = Rec("B1");
        var href = _refs["B1"];
        var results = new List<string>();
        var newCount = 5000;
        using var sub = Events.On<ItemMovedEvent>(e =>
        {
            if (e.Item != href) return;
            try
            {
                var open = _saveOpen;
                var val = e.Item.Get(Key);
                var before = val.Count;
                e.Item.Set(Key, new T4Tag { Name = val.Name, Count = newCount });
                h.Count = newCount;
                results.Add($"step {e.Step} ({e.Cause}, {e.From} -> {e.To}), a save {(open ? $"being staged (revision {_savingRev})" : "not in progress")}: Get gave {val.Name} #{before}, Set #{newCount}");
                Changed("B1's value set in the ItemMovedEvent handler");
            }
            catch (Exception ex)
            {
                results.Add($"threw {ex.GetType().Name}: {ex.Message}");
            }
        });
        var from = Resolve(h.Holder);
        var slot = AtFlat(from, h.Flat)!;
        var to = Chest("K9");
        _inflight++;
        var move = Items.LabMoveUnseen(from, slot.Slot, to);
        var save = Saves.Request();
        var m = await move;
        var now = Items.Read(to)!.Slots.FirstOrDefault(x => x.ItemEntity == href.Entity.Id);
        if (now != null)
        {
            h.Holder = PosOf(to);
            h.Flat = FlatOf(Items.Read(to)!, now.Slot);
        }
        _inflight--;
        Changed("B1 moved unseen");
        var r = await save;
        await Until(() => results.Count > 0, 100);
        Check(results.Count == 1 && !results[0].StartsWith("threw"),
            $"t4 f2 (handler): ItemMovedEvent for B1 (moved {m.Status} to K9 flat {h.Flat}, the save {r}): {string.Join(" | ", results)}");
        Check(href.TryGet(Key, out var nv) && nv.Count == newCount, $"t4 f2 (handler): B1's value now #{nv?.Count}");
        _ds.Notes["handler"] = $"revision {r.Revision}: {string.Join(" | ", results)}";
        Next("f3", "kill");
    }

    private async Task F3()
    {
        var states = LoadedStates();
        await VerifyLoaded("f3 after the handler's save", states);
        var b1 = Rec("B1");
        var at = AtFlat(Resolve(b1.Holder), b1.Flat);
        Info($"t4 f3 (handler): B1 at K9 flat {b1.Flat}: {(at?.Ref.TryGet(Key, out var v) == true ? $"{v.Name} #{v.Count}" : "no value")}, " +
             $"{(at != null && _restored.TryGetValue(at.Ref, out var m) ? m.ToString() : "no restored event")}; loaded {_worldLoaded}; {_ds.Notes.GetValueOrDefault("handler")}");
        Next("g1", "stop");
    }

    // ---- g1, g2: break-it 3 again, with chests at a height that is saved ----

    private async Task G1()
    {
        var sword = Sword()!;
        // next to the K chests of f1, which came back after their restarts (chests spawned elsewhere did not, at any height)
        var a = Spawn.Inspect(Chest("K0"))!;
        foreach (var gone in _ds.Swords.Where(s => Resolve(s.Holder).IsNone).ToList()) _ds.Swords.Remove(gone);
        foreach (var (name, dx) in new (string, double)[] { ("P1g", 0), ("P2g", 0.3), ("Q1g", 3), ("Q2g", 4) })
        {
            var e = await Spawn.Entity(a.Template!.Value, new WorldPosition(a.Position.X + dx, a.Position.Y, a.Position.Z + 5), 0,
                new SpawnOptions { Tag = "instances-test-t4-breakit3" });
            await Until(() => Items.Read(e) is { Initialized: true }, 100);
            _ds.Chests[name] = PosOf(e);
            Info($"t4 g1 break-it 3: chest {name} = {e} {_ds.Chests[name]}");
            await Give("Z" + name, name, true, TwinGear(sword), sword);
        }
        VerifyLive("g1 break-it 3 set up", _ds.Swords.Where(x => x.Name.StartsWith("ZP") || x.Name.StartsWith("ZQ")).ToList());
        await SaveNow("g1 break-it 3");
        Next("g2", "stop");
    }

    private async Task G2()
    {
        await VerifyLoaded("g2 after break-it 3 (P1g/P2g 0.3 m apart, Q1g/Q2g 1 m apart)", LoadedStates(), s => s.Name.EndsWith('g'));
        foreach (var n in new[] { "P1g", "P2g", "Q1g", "Q2g" })
            Info($"t4 g2 break-it 3: chest {n} resolves to {Resolve(_ds.Chests[n])}");
        Ready("end");
    }
}
