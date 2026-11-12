// SPDX-License-Identifier: MIT
// Task t4, part 2: instance data against the game, chests only, over many boots (run-t4.sh data). The one failure the
// package may never have is an item getting another item's data, so every check compares each tagged item's value with
// what this test knows it put there, and reports a set-aside record with the matcher's reason (a set-aside is not a
// pass by itself).
//
// Ground truth. Identical swords cannot be told apart by the game, so the test keeps its own record of where each one is
// (holder by template and position, flat slot) and appends that record to t4/ledger.jsonl at every WorldSavingEvent,
// keyed by the revision the save writes. After any restart (SIGINT, kill -9, a restored backup) the expected state is
// the ledger entry of the revision that loaded. A change made between a save's WorldSavingEvent and its WorldSavedEvent
// appends a second entry for that revision ("racy"): then either place counts for the sword that moved.
//
// Phases (t4/data.json), one per boot unless said:
//   d1  twins S1 S2 (tagged) and S4 (untagged) in chest A, S3 (tagged twin) in B; moves within A, swaps, between chests,
//       into a second chest and back, an unseen move, a move all, each verified; where a followed item is each step;
//       destroy, split, 64 KB, a value that grows too big; the rider's twins; a save; backup t4-b1; S3 moved; a save.  stop
//   d2  after SIGINT: every value Exact on its own sword; moves, a save, more moves not saved.                        kill
//   d3  after kill -9 between saves: the loaded revision's state; moves; a save.                                       kill
//   d4  after kill -9 right after a save; RestoreOnNextStart(t4-b1).                                                   stop
//   d5  after the restore: t4-b1's state; break-it 1 (a take and an identical give into the slot); break-it 2 (a move
//       in the step of a save).                                                                                       kill
//   d6  break-it 1 and 2 after a restart; break-it 3 setup: two chests of one template side by side.                 stop
//   d7  break-it 3 outcome; the rider's mod goes away.                                                           rider-off
//   d8  without the rider: its twins moved by Items.Move; a save.                                               rider-on
//   d9  the rider checks its values; 10 hot reloads (every reloaded instance checks the values in its OnLoad);
//       /instances through the console folder.                                                                         stop
//   n1  10, 100 and 1000 tagged twins in spawned chests, a save each (the store logs each staging's time).             stop
//   d10 matching those records at a load; unequal twins: a save, one moved, a save, that generation dropped.  drop-gen
//   d11 the matcher's decision for the unequal twins on the older generation.                                          stop
//   e1  break-it 4 setup: twins W1 W2 tagged in A, a save.                                                             stop
//   e2  break-it 4: W1 and W2 swapped in the first tick a mod can move items after the load, racing the matcher.        end
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace InstancesTest;

/// <summary>The value the test keeps on each tagged item.</summary>
public sealed class T4Tag
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public string Pad { get; set; } = "";
}

public sealed partial class InstancesTestMod
{
    private InstanceKey<T4Tag>? _key;
    private InstanceKey<T4Tag> Key => _key ?? throw new InvalidOperationException("t4.tag not declared");

    private sealed class HolderPos
    {
        public string Template { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public override string ToString() => $"{Template}@({X:0.0},{Y:0.0},{Z:0.0})";
        public bool Same(HolderPos o) => o.Template == Template && Math.Abs(o.X - X) < 0.01 && Math.Abs(o.Y - Y) < 0.01 && Math.Abs(o.Z - Z) < 0.01;
    }

    private sealed class SwordRec
    {
        public string Name { get; set; } = "";
        public bool Tagged { get; set; }
        public HolderPos Holder { get; set; } = new();
        public int Flat { get; set; }
        public int Count { get; set; }
        public bool Frozen { get; set; }   // its count is not bumped (the grow test, the rider's)
        public SwordRec Clone() => (SwordRec)MemberwiseClone();
        public override string ToString() => $"{Name} {(Tagged ? $"#{Count}" : "untagged")} at {Holder} flat {Flat}";
    }

    private sealed class DataState
    {
        public string Phase { get; set; } = "d1";
        public int Boot { get; set; }
        public List<SwordRec> Swords { get; set; } = [];
        public Dictionary<string, HolderPos> Chests { get; set; } = [];
        public Dictionary<string, uint> Backups { get; set; } = [];
        public Dictionary<string, string> Notes { get; set; } = [];
        public List<SwordRec> Extra { get; set; } = [];       // checks of their own (break-its, unequal twins)
    }

    private sealed class LedgerLine
    {
        public uint Rev { get; set; }
        public bool Racy { get; set; }
        public string Why { get; set; } = "";
        public List<SwordRec> Swords { get; set; } = [];
    }

    private DataState _ds = new();
    private readonly Dictionary<string, ItemInstanceRef> _refs = [];
    private readonly List<string> _events = [];
    private readonly Dictionary<ItemInstanceRef, InstanceMatch> _restored = [];
    private readonly List<InstanceDataLostEvent> _lost = [];
    private readonly List<InstanceDataParkedEvent> _parked = [];
    private WorldLoadedEvent? _worldLoaded;
    private uint _savingRev;
    private bool _saveOpen;
    private int _inflight;
    private int _aside, _waiting;
    private Action<WorldSavingEvent>? _onSaving;

    private string DataFile => Path.Combine(T4Dir, "data.json");
    private string LedgerFile => Path.Combine(T4Dir, "ledger.jsonl");

    // ---------------------------------------------------------------------------------------------- events ----

    [LabOnLoad("t4.key")]
    private void T4DeclareKey()
    {
        _key = InstanceData.Key<T4Tag>("t4.tag");
        Directory.CreateDirectory(Path.Combine(Context.DataDirectory, "t4"));
        if (File.Exists(Path.Combine(Context.DataDirectory, "t4", "data.json")))
            _ds = JsonSerializer.Deserialize<DataState>(File.ReadAllText(Path.Combine(Context.DataDirectory, "t4", "data.json")))!;
        if (_ds.Phase == "e2" && !File.Exists(Path.Combine(Context.DataDirectory, "t4", "early-swap.done")))
            Context.Tick.Pre += EarlySwapTick;
        T4ReloadCheck();
    }

    [Subscribe]
    private void T4OnRestored(InstanceDataRestoredEvent e)
    {
        if (e.Mod != Context.Info.Id) return;
        _restored[e.Item] = e.Match;
        _events.Add($"restored {e.Key} on {e.Item} {e.Match}: {e.Detail}");
        Info($"t4 event: restored {e.Mod}:{e.Key} on {e.Item} ({e.Match}): {e.Detail}");
    }

    [Subscribe]
    private void T4OnLost(InstanceDataLostEvent e)
    {
        _lost.Add(e);
        Info($"t4 event: lost {e.Mod} [{string.Join(",", e.Keys)}] of {e.Item} last {e.LastRef} ({e.Reason}): {e.Detail}");
    }

    [Subscribe]
    private void T4OnParked(InstanceDataParkedEvent e)
    {
        _parked.Add(e);
        Info($"t4 event: parked {e.Mod} [{string.Join(",", e.Keys)}] of {e.Item} in {e.Holder} slot {e.FlatSlot} ({e.Reason}): {e.Detail}");
    }

    [Subscribe]
    private void T4OnLoaded(WorldLoadedEvent e)
    {
        _worldLoaded = e;
        Info($"t4 event: {e}");
    }

    [Subscribe]
    private void T4OnSaving(WorldSavingEvent e)
    {
        _savingRev = e.Revision;
        _saveOpen = true;
        if (_ds.Swords.Count > 0) AppendLedger(e.Revision, _inflight > 0, $"saving {e.Reason} at step {e.Step}{(_inflight > 0 ? $", {_inflight} move(s) in flight" : "")}");
        _onSaving?.Invoke(e);
    }

    [Subscribe]
    private void T4OnSaved(WorldSavedEvent e)
    {
        if (e.Revision == _savingRev || !e.Success) _saveOpen = false;
        if (e.Success) _savedRevs.Add(e.Revision);
    }

    private readonly HashSet<uint> _savedRevs = [];

    private void AppendLedger(uint rev, bool racy, string why)
    {
        var line = JsonSerializer.Serialize(new LedgerLine { Rev = rev, Racy = racy, Why = why, Swords = _ds.Swords.Select(s => s.Clone()).ToList() });
        using var f = new FileStream(LedgerFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        var b = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        f.Write(b);
        f.Flush(true);
    }

    /// <summary>After any change of the expected state: if a save is between its WorldSavingEvent and its write, that
    /// revision may hold either state.</summary>
    private void Changed(string what)
    {
        if (_saveOpen && _ds.Swords.Count > 0)
        {
            AppendLedger(_savingRev, true, $"changed during the save: {what}");
            Info($"t4: '{what}' happened while revision {_savingRev} was being saved; either state counts for it");
        }
        SaveState();
    }

    private void SaveState() => File.WriteAllText(DataFile, JsonSerializer.Serialize(_ds, new JsonSerializerOptions { WriteIndented = true }));

    private List<List<SwordRec>> LedgerOf(uint rev)
    {
        // A restored backup makes the game number its next saves again from the backup's revision (t4-b1 is 3013: the
        // next save is 3014 a second time). Lines are in time order; a revision lower than the line before starts a new
        // epoch, and a revision belongs to the newest epoch that wrote it.
        var byEpoch = new Dictionary<int, List<List<SwordRec>>>();
        if (!File.Exists(LedgerFile)) return [];
        var epoch = 0;
        uint prev = 0;
        foreach (var l in File.ReadAllLines(LedgerFile))
        {
            if (l.Length == 0) continue;
            var e = JsonSerializer.Deserialize<LedgerLine>(l)!;
            if (e.Rev < prev) epoch++;
            prev = e.Rev;
            if (e.Rev != rev) continue;
            if (!byEpoch.TryGetValue(epoch, out var list)) byEpoch[epoch] = list = [];
            list.Add(e.Swords);
        }
        if (byEpoch.Count > 1) Info($"t4: revision {rev} was written in {byEpoch.Count} epochs (a restore numbers saves again); the newest counts");
        return byEpoch.Count == 0 ? [] : byEpoch[byEpoch.Keys.Max()];
    }

    // --------------------------------------------------------------------------------------------- holders ----

    private HolderPos PosOf(EntityId h)
    {
        var i = Spawn.Inspect(h) ?? throw new InvalidOperationException($"holder {h} cannot be inspected");
        return new HolderPos { Template = i.TemplateName ?? i.Template?.ToString() ?? "?", X = i.Position.X, Y = i.Position.Y, Z = i.Position.Z };
    }

    /// <summary>The test's own truth of a holder: the one container of the template within 0.15 m of the position
    /// (tighter than instance data's 0.5 m, so break-it 3's chests 0.3 m apart stay two for the test).</summary>
    private EntityId Resolve(HolderPos p)
    {
        var near = Items.FindInventories(InventoryKinds.Containers)
            .Where(e => Spawn.Inspect(e) is { } i && (i.TemplateName ?? i.Template?.ToString()) == p.Template &&
                        Math.Abs(i.Position.X - p.X) < 0.15 && Math.Abs(i.Position.Y - p.Y) < 0.15 && Math.Abs(i.Position.Z - p.Z) < 0.15)
            .ToList();
        return near.Count == 1 ? near[0] : EntityId.None;
    }

    private EntityId Chest(string name) => Resolve(_ds.Chests[name]);

    /// <summary>The save's flat slot of a slot: (Generic links before its inventory) x 8 + index.</summary>
    private static int FlatOf(InventoryContents c, SlotId slot)
    {
        var generic = 0;
        foreach (var (inv, cat) in c.Inventories)
        {
            if (inv == slot.Inventory) return cat == InventoryCategory.Generic ? generic * 8 + slot.Index : -1;
            if (cat == InventoryCategory.Generic) generic++;
        }
        return -1;
    }

    private static InventorySlot? AtFlat(EntityId holder, int flat)
    {
        if (holder.IsNone) return null;
        var c = Items.Read(holder);
        return c?.Slots.FirstOrDefault(s => FlatOf(c, s.Slot) == flat);
    }

    private static List<InventorySlot> FreeGeneric(EntityId h) =>
        h.IsNone ? [] : Items.Read(h)?.Slots.Where(s => s.IsEmpty && s.Category == InventoryCategory.Generic).ToList() ?? [];

    private SwordRec Rec(string name) => _ds.Swords.First(s => s.Name == name);

    // ------------------------------------------------------------------------------------------------ moves ----

    private async Task<SwordRec?> Give(string name, string chest, bool tag, Gear gear, ItemInfo item, List<SwordRec>? into = null)
    {
        var h = Chest(chest);
        var free = FreeGeneric(h).FirstOrDefault();
        if (free == null)
        {
            Check(false, $"t4: no free slot in chest {chest} for {name}");
            return null;
        }
        var g = await Items.Give(h, item.Id, 1, gear, slot: free.Slot);
        var ch = g.Changes.FirstOrDefault(c => !c.Ref.IsNone);
        if (!g.Succeeded || ch.Ref.IsNone)
        {
            Check(false, $"t4: give {name} into {chest}: {g}");
            return null;
        }
        _refs[name] = ch.Ref;
        var c = Items.Read(h)!;
        var rec = new SwordRec { Name = name, Tagged = tag, Holder = PosOf(h), Flat = FlatOf(c, ch.Slot), Count = 1 };
        if (tag) ch.Ref.Set(Key, new T4Tag { Name = name, Count = 1 });
        (into ?? _ds.Swords).Add(rec);
        Changed($"gave {name}");
        Info($"t4: gave {name} ({item.Name} {gear}) into {chest} {h} flat {rec.Flat} as {ch.Ref}{(tag ? ", tagged" : "")}");
        return rec;
    }

    /// <summary>Items.Move of one sword to another chest (into <paramref name="toFlat"/> or the game's choice).</summary>
    private async Task<bool> MoveTo(string name, string chest, int? toFlat = null, List<SwordRec>? list = null, bool unseen = false)
    {
        var rec = (list ?? _ds.Swords).First(s => s.Name == name);
        var from = Resolve(rec.Holder);
        var to = Chest(chest);
        var s = AtFlat(from, rec.Flat);
        var r = _refs[name];
        if (s == null || s.ItemEntity != r.Entity.Id)
        {
            Check(false, $"t4: {name} is not where the test put it ({rec}): slot holds {s?.ItemEntity}");
            return false;
        }
        SlotId? target = null;
        if (toFlat is { } tf)
        {
            target = AtFlat(to, tf)?.Slot;
            if (target == null) throw new InvalidOperationException($"chest {chest} has no flat slot {tf}");
        }
        _inflight++;
        MoveResult? m = null;
        try
        {
            m = unseen ? await Items.LabMoveUnseen(from, s.Slot, to) : await Items.Move(from, s.Slot, to, toSlot: target);
        }
        finally
        {
            _inflight--;
        }
        if (!m.Succeeded)
        {
            Check(false, $"t4: move {name} {rec} -> {chest}{(toFlat is { } x ? $" flat {x}" : "")}: {m}");
            return false;
        }
        // where it is now, from a read of the target (the truth), not from the tracker
        var now = Items.Read(to)!.Slots.FirstOrDefault(x => x.ItemEntity == r.Entity.Id);
        if (now == null)
        {
            Check(false, $"t4: after the move {name} ({r}) is not in {chest}");
            return false;
        }
        rec.Holder = PosOf(to);
        rec.Flat = FlatOf(Items.Read(to)!, now.Slot);
        if (toFlat is { } want && rec.Flat != want) Check(false, $"t4: {name} asked for flat {want}, landed in {rec.Flat}");
        Changed($"moved {name} to {chest} flat {rec.Flat}");
        return true;
    }

    private void Bump(int n)
    {
        foreach (var s in _ds.Swords.Where(s => s.Tagged && !s.Frozen))
        {
            if (!_refs.TryGetValue(s.Name, out var r) || !r.TryGet(Key, out var v)) continue;
            v.Count = n;
            s.Count = n;
        }
        Changed($"counts -> {n}");
    }

    // ----------------------------------------------------------------------------------------------- verify ----

    /// <summary>In a session: each sword is where the test put it (a read), its ref is the item there, its location
    /// agrees, and its value is its own.</summary>
    private void VerifyLive(string label, List<SwordRec>? list = null)
    {
        int ok = 0, bad = 0;
        foreach (var s in list ?? _ds.Swords)
        {
            var h = Resolve(s.Holder);
            var at = AtFlat(h, s.Flat);
            var r = _refs.GetValueOrDefault(s.Name);
            if (r.IsNone)
            {
                bad++;
                Check(false, $"t4 {label}: {s}: the test has no ref for it this session");
                continue;
            }
            var loc = r.Location();
            var problems = new List<string>();
            if (at == null || at.ItemEntity != r.Entity.Id) problems.Add($"the slot holds {at?.ItemEntity}, not {r}");
            else if (at.Ref != r) problems.Add($"the read's ref {at.Ref} is not {r}");
            if (loc is not { } l || l.Holder != h || l.FlatSlot != s.Flat) problems.Add($"Location() says {loc}");
            if (s.Tagged)
            {
                if (!r.TryGet(Key, out var v)) problems.Add("no value");
                else if (v.Name != s.Name) problems.Add($"MIXUP: it carries {v.Name}'s value");
                else if (v.Count != s.Count) problems.Add($"count {v.Count}, expected {s.Count}");
            }
            else if (!r.IsNone && r.Has(Key)) problems.Add($"an untagged item has a value ({r.Get(Key).Name})");
            if (problems.Count == 0) ok++;
            else
            {
                bad++;
                Check(false, $"t4 {label}: {s}: {string.Join("; ", problems)}");
            }
        }
        AllCheck(label, list);
        Check(bad == 0, $"t4 {label}: {ok} item(s) each with its own value where the test put it");
    }

    /// <summary>Every (item, value) InstanceData.All lists sits where the test expects that value's sword.</summary>
    private void AllCheck(string label, List<SwordRec>? list = null)
    {
        var expected = (list ?? _ds.Swords).Concat(_ds.Extra).Where(s => s.Tagged).ToList();
        foreach (var (item, v) in InstanceData.All(Key))
        {
            var loc = item.Location();
            var rec = expected.FirstOrDefault(s => s.Name == v.Name);
            if (rec == null) continue;   // a value of another check of this run
            var h = Resolve(rec.Holder);
            if (loc is not { } l || l.Holder != h || l.FlatSlot != rec.Flat)
                Check(false, $"t4 {label}: InstanceData.All has {v.Name}'s value on {item} at {loc}; the test has {v.Name} at {rec.Holder} ({h}) flat {rec.Flat}");
        }
    }

    /// <summary>After a load: each expected sword (one of the ledger's states for the loaded revision) is found where the
    /// state says, and carries its own value with that state's count, or its record is set aside / waiting (reported with
    /// the reason). Rebinds the session refs.</summary>
    private async Task VerifyLoaded(string label, List<List<SwordRec>> states, Func<SwordRec, bool>? only = null)
    {
        if (states.Count == 0)
        {
            Check(false, $"t4 {label}: no ledger entry for the loaded revision {_worldLoaded?.Revision}");
            return;
        }
        var names = states[0].Where(s => only == null || only(s)).Select(s => s.Name).ToList();
        // where is each sword now? the one place among its states that holds an item of its kind
        var places = new Dictionary<string, (SwordRec Rec, InventorySlot Slot)>();
        foreach (var n in names)
        {
            var cands = states.Select(st => st.First(s => s.Name == n)).DistinctBy(s => $"{s.Holder}|{s.Flat}|{s.Count}").ToList();
            var found = cands.Select(c => (Rec: c, Slot: AtFlat(Resolve(c.Holder), c.Flat))).Where(x => x.Slot is { IsEmpty: false } && !x.Slot.Ref.IsNone).ToList();
            var byPlace = found.DistinctBy(x => $"{x.Rec.Holder}|{x.Rec.Flat}").ToList();
            if (byPlace.Count == 1)
            {
                // a racy revision may have two counts for one place: the value decides between them below
                places[n] = found[0];
                if (found.Count > 1) Info($"t4 {label}: {n}: either count {string.Join(" or ", found.Select(f => f.Rec.Count))} is right (racy revision)");
            }
            else Check(false, $"t4 {label}: {n}: {byPlace.Count} of its possible places hold an item ({string.Join(", ", cands)}): cannot tell where it is");
        }
        // wait for the matcher: every tagged one has a value or a parked record
        bool Settled() => places.Values.Where(p => p.Rec.Tagged).All(p => p.Slot.Ref.Has(Key) ||
                                                                          InstanceData.Parked(Key).Any(x => x.Value?.Name == p.Rec.Name));
        var settled = await Until(Settled, 25 * 90);
        if (!settled) Info($"t4 {label}: not every record decided after 90 s");
        int own = 0, aside = 0, waiting = 0, fail = 0;
        var parked = InstanceData.Parked(Key);
        foreach (var (n, (rec, slot)) in places)
        {
            var r = slot.Ref;
            _refs[n] = r;
            if (!rec.Tagged)
            {
                if (r.Has(Key))
                {
                    fail++;
                    Check(false, $"t4 {label}: MIXUP: untagged {n} at {rec.Holder} flat {rec.Flat} carries {r.Get(Key).Name}'s value");
                }
                else Info($"t4 {label}: untagged {n} has no value (right)");
                continue;
            }
            if (r.TryGet(Key, out var v))
            {
                var counts = states.Select(st => st.First(s => s.Name == n)).Where(s => s.Holder.Same(rec.Holder) && s.Flat == rec.Flat).Select(s => s.Count).ToHashSet();
                if (v.Name != n)
                {
                    fail++;
                    Check(false, $"t4 {label}: MIXUP: {n} at {rec.Holder} flat {rec.Flat} ({r}) carries {v.Name}'s value #{v.Count}");
                }
                else if (!counts.Contains(v.Count))
                {
                    fail++;
                    Check(false, $"t4 {label}: {n} has its own value but count {v.Count}, the loaded revision has {string.Join(" or ", counts)} (a mix of generations)");
                }
                else
                {
                    own++;
                    rec.Count = v.Count;
                    var how = _restored.TryGetValue(r, out var m) ? m.ToString() : "no restored event";
                    Check(true, $"t4 {label}: {n} has its own value #{v.Count} at {rec.Holder} flat {rec.Flat} ({how})");
                }
                continue;
            }
            var p = parked.FirstOrDefault(x => x.Value?.Name == n);
            if (p == null)
            {
                fail++;
                Check(false, $"t4 {label}: {n} at {rec.Holder} flat {rec.Flat} has no value and no parked record");
            }
            else if (p.State == ParkedState.SetAside)
            {
                aside++;
                rec.Tagged = false;   // from now on its sword carries no value (a set-aside record never attaches again)
                Log.Info($"ASIDE t4 {label}: {n}'s record set aside: {p}");
            }
            else
            {
                waiting++;
                rec.Tagged = false;
                Log.Info($"WAITING t4 {label}: {n}'s record waits: {p}");
            }
        }
        // no value of this test may sit on an item where the test does not expect it
        foreach (var (item, v) in InstanceData.All(Key))
        {
            if (!places.TryGetValue(v.Name, out var p)) continue;
            if (p.Slot.Ref != item)
            {
                fail++;
                Check(false, $"t4 {label}: MIXUP: {v.Name}'s value is on {item} at {item.Location()}, not on the sword at {p.Rec.Holder} flat {p.Rec.Flat}");
            }
        }
        _aside += aside;
        _waiting += waiting;
        Info($"t4 {label}: {own} own value(s), {aside} set aside, {waiting} waiting, {fail} wrong, of {places.Values.Count(x => x.Rec.Tagged)} tagged");
        // the expected state from now on is the one that loaded
        foreach (var (n, (rec, _)) in places)
        {
            var i = _ds.Swords.FindIndex(s => s.Name == n);
            if (i >= 0) _ds.Swords[i] = rec.Clone();
        }
        SaveState();
    }

    private List<List<SwordRec>> LoadedStates()
    {
        var rev = _worldLoaded?.Revision ?? Saves.Status?.Revision ?? 0;
        var st = LedgerOf(rev);
        if (st.Count == 0 && File.Exists(LedgerFile))
        {
            // a shutdown save posts its WorldSavingEvent at ServerStopping, or not at all: then nothing changed after the
            // newest earlier entry (the test stops the server right after its own save)
            var older = File.ReadAllLines(LedgerFile).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<LedgerLine>(l)!)
                .Where(e => e.Rev < rev).Select(e => e.Rev).DefaultIfEmpty(0u).Max();
            st = LedgerOf(older);
            Info($"t4: no ledger entry for revision {rev}: the newest earlier one, {older}, stands ({st.Count} entr{(st.Count == 1 ? "y" : "ies")})");
        }
        Info($"t4: loaded revision {rev} (data {_worldLoaded?.Data}): {st.Count} ledger entr{(st.Count == 1 ? "y" : "ies")}");
        return st;
    }

    // ------------------------------------------------------------------------------------------- the phases ----

    [LabCheck(210, "t4.data")]
    private async Task T4Data()
    {
        _ds.Boot++;
        Info($"t4 data: boot {_ds.Boot}, phase {_ds.Phase}; snapshot staging {Saves.SnapshotStagingAvailable}; " +
             $"world loaded {_worldLoaded}; {Items.LabInstancesStatus}");
        await Until(() => _worldLoaded != null && Saves.WorldDataReady, 25 * 30);
        switch (_ds.Phase)
        {
            case "d1": await D1(); break;
            case "d2": await D2(); break;
            case "d3": await D3(); break;
            case "d4": await D4(); break;
            case "d5": await D5(); break;
            case "d6": await D6(); break;
            case "d7": await D7(); break;
            case "d8": await D8(); break;
            case "d9": await D9(); break;
            case "d9r": await D9r(); break;
            case "e1": await E1(); break;
            case "e2": await E2(); break;
            case "u1": await U1(); break;
            case "u2": await U2(); break;
            case "u4": await U4(); break;
            case "n1": await N1(); break;
            case "d10": await D10(); break;
            case "d11": await D11(); break;
            default: Ready("end"); break;
        }
        Info($"t4 data: phase done; {_aside} set aside and {_waiting} waiting this boot; {InstanceStoreSummary()}");
    }

    private static string InstanceStoreSummary() => Items.LabInstancesStatus;

    private void Next(string phase, string ready)
    {
        _ds.Phase = phase;
        SaveState();
        Ready(ready);
    }

    private async Task<SaveResult> SaveNow(string why)
    {
        var r = await Saves.Request();
        Check(r.Success, $"t4: save ({why}): {r}");
        return r;
    }

    private Gear TwinGear(ItemInfo sword) => new(Math.Max(1, sword.LevelMin), ItemRarity.Rare);

    private async Task D1()
    {
        var sword = Sword()!;
        var quiet = (await QuietContainers(7, 200)).Where(e => Spawn.Inspect(e)?.TemplateName != null)
            .OrderBy(e => Items.Read(e)!.UsedSlots).ThenBy(e => e.Value).ToList();
        Check(quiet.Count >= 6, $"t4 d1: {quiet.Count} quiet containers with 7 free slots");
        if (quiet.Count < 6) return;
        string[] names = ["A", "B", "C", "E", "F", "G"];
        for (var i = 0; i < names.Length; i++)
        {
            _ds.Chests[names[i]] = PosOf(quiet[i]);
            Info($"t4 d1: chest {names[i]} = {quiet[i]} {_ds.Chests[names[i]]}, {Items.Read(quiet[i])!.FreeSlots()} free");
        }
        var gear = TwinGear(sword);
        await Give("S1", "A", true, gear, sword);
        await Give("S2", "A", true, gear, sword);
        await Give("S4", "A", false, gear, sword);
        await Give("S3", "B", true, gear, sword);
        var a = Items.Read(Chest("A"))!.Slots.Where(s => s.Item == sword.Id).ToList();
        Check(a.Count >= 3 && a.All(s => s.Level == a[0].Level && s.Rarity == a[0].Rarity && !s.IsBroken),
            $"t4 d1: the twins in A are alike: {string.Join(", ", a.Select(s => $"level {s.Level} {s.Rarity}"))}");
        VerifyLive("d1 given");

        // where a followed item is, every step, through the moves below (t2's doubt: an item in no holder's slot)
        var trail = new List<string>();
        var unplaced = 0;
        ItemLocation? lastLoc = null;
        void Trail(in TickInfo t)
        {
            if (!_refs.TryGetValue("S1", out var r)) return;
            var l = r.Location();
            if (l is { IsPlaced: false } || l == null) unplaced++;
            if (l != lastLoc) trail.Add($"step {t.Step}: {(l == null ? "null" : l.Value.IsPlaced ? $"{l.Value.Holder}:{l.Value.FlatSlot}" : $"UNPLACED {l}")}");
            lastLoc = l;
        }
        void Trail2(in TickInfo t)
        {
            if (!_refs.TryGetValue("S2", out var r)) return;
            var l = r.Location();
            if (l is { IsPlaced: false } || l == null)
            {
                unplaced++;
                trail.Add($"step {t.Step}: S2 {(l == null ? "null" : $"UNPLACED {l}")}");
            }
        }
        Context.Tick.Pre += Trail;
        Context.Tick.Post += Trail;
        Context.Tick.Pre += Trail2;
        Context.Tick.Post += Trail2;
        var n = 1;
        try
        {
            // within A: a move to another slot of A (the lab has no same-holder move: Items.Move refuses from == to, so
            // through B)
            var freeA = FreeGeneric(Chest("A")).Select(s => FlatOf(Items.Read(Chest("A"))!, s.Slot)).ToList();
            await MoveTo("S1", "B");
            await MoveTo("S1", "A", freeA.Last());
            Bump(++n);
            VerifyLive("d1 S1 moved within A");
            // swap: each into the other's slot
            int f1 = Rec("S1").Flat, f2 = Rec("S2").Flat;
            await MoveTo("S1", "B");
            await MoveTo("S2", "C");
            await MoveTo("S1", "A", f2);
            await MoveTo("S2", "A", f1);
            Bump(++n);
            VerifyLive("d1 S1 and S2 swapped in A");
            // between chests; into a second chest and back
            await MoveTo("S1", "B");
            Bump(++n);
            VerifyLive("d1 S1 into B");
            await MoveTo("S1", "C");
            Bump(++n);
            VerifyLive("d1 S1 into C");
            await MoveTo("S2", "C");
            Bump(++n);
            VerifyLive("d1 S2 into C too (twins in C)");
            await MoveTo("S1", "A");
            await MoveTo("S2", "A");
            Bump(++n);
            VerifyLive("d1 S1 and S2 back in A");
            // a move the hooks do not see: only the per-step read follows it
            await MoveTo("S2", "C", unseen: true);
            Bump(++n);
            VerifyLive("d1 S2 to C by an unseen move");
            await MoveTo("S2", "A", unseen: true);
            Bump(++n);
            VerifyLive("d1 S2 back to A by an unseen move");
            // the game's move all: S3 and the rest of B into G (the emptiest), then the rest back
            var b = Chest("B");
            var g = Chest("G");
            var before = Items.Read(b)!.Slots.Where(s => !s.IsEmpty).Select(s => s.Slot).ToList();
            _inflight++;
            var ma = await Items.MoveAll(b, g);
            _inflight--;
            Check(ma.Succeeded, $"t4 d1: move all B -> G: {ma.Status}, {ma.Changes.Count} piece(s)");
            var s3 = Items.Read(g)!.Slots.FirstOrDefault(s => s.ItemEntity == _refs["S3"].Entity.Id);
            if (s3 != null)
            {
                Rec("S3").Holder = PosOf(g);
                Rec("S3").Flat = FlatOf(Items.Read(g)!, s3.Slot);
                Changed("S3 moved by move all");
            }
            else Check(false, "t4 d1: S3 not in G after the move all");
            foreach (var ch in ma.Changes.Where(c => c.ItemEntity != _refs["S3"].Entity.Id))
                await Items.Move(g, ch.To, b, ch.ItemEntity.IsNone ? ch.Amount : 0, toSlot: ch.From);
            Bump(++n);
            VerifyLive("d1 S3 moved by the game's move all");
            await MoveTo("S3", "B");
            Bump(++n);
            VerifyLive("d1 S3 back in B");
        }
        finally
        {
            Context.Tick.Pre -= Trail;
            Context.Tick.Post -= Trail;
            Context.Tick.Pre -= Trail2;
            Context.Tick.Post -= Trail2;
        }
        Info($"t4 d1: S1's location at every Pre and Post through {trail.Count} change(s): {string.Join(" | ", trail)}");
        Check(unplaced == 0, $"t4 d1: a followed item was in no holder's slot at {unplaced} Pre/Post read(s) through Items.Move, MoveAll and the unseen move");

        await DestroySplitCaps();
        await RiderPlan();

        // a save, a backup, then S3 moves to C and the counts change: the backup's state differs from the newest
        await SaveNow("d1 after the moves");
        var bk = await Saves.Backup("t4-b1");
        _ds.Backups["t4-b1"] = bk.Revision;
        Info($"t4 d1: backup {bk}");
        await MoveTo("S3", "C");
        Bump(50);
        VerifyLive("d1 after the backup: S3 in C");
        await SaveNow("d1 after the backup");
        Next("d2", "stop");
    }

    /// <summary>Destroy a tagged item; split a stack; a value over 64 KB at Set; a live value that grows past it.</summary>
    private async Task DestroySplitCaps()
    {
        var sword = Sword()!;
        // destroy
        var lost0 = _lost.Count;
        var tmp = new List<SwordRec>();
        var s5 = await Give("S5", "C", true, TwinGear(sword), sword, tmp);
        if (s5 != null)
        {
            var r = _refs["S5"];
            var c = Chest("C");
            var t = await Items.Take(c, sword.Id, 1, AtFlat(c, s5.Flat)!.Slot, true, r.Entity.Id);
            await Until(() => _lost.Skip(lost0).Any(e => e.Reason == InstanceLossReason.Destroyed), 100);
            var ev = _lost.Skip(lost0).FirstOrDefault(e => e.Reason == InstanceLossReason.Destroyed);
            Check(t.Succeeded && ev != null && ev.Item == sword.Id && ev.Keys.Contains("t4.tag"),
                $"t4 destroy: a tagged sword taken: {t.Status}, lost event {ev?.Reason} keys [{string.Join(",", ev?.Keys ?? [])}] last {ev?.LastRef} ({ev?.Detail})");
            Check(!r.Has(Key) && !r.TryGet(Key, out _), "t4 destroy: Has and TryGet on the destroyed sword's ref are false");
            try
            {
                r.Get(Key);
                Check(false, "t4 destroy: Get on the destroyed sword's ref did not throw");
            }
            catch (ArgumentException e)
            {
                Check(true, $"t4 destroy: Get on the destroyed sword's ref throws ArgumentException ({e.Message})");
            }
        }

        // split: a stack that has an item entity
        var c2 = Chest("C");
        ItemInfo? stackable = null;
        ItemInstanceRef stackRef = default;
        var probed = 0;
        foreach (var it in Items.All.Where(i => i.MaxStack > 1 && !i.IsBlueprint && i.Category is ItemCategory.Ammunition or ItemCategory.Consumables
                     or ItemCategory.Tools or ItemCategory.Materials or ItemCategory.AnimalFood or ItemCategory.Collectibles or ItemCategory.Equipment
                     or ItemCategory.Weapons))
        {
            if (++probed > 60) break;
            var g = await Items.Give(c2, it.Id, 2, options: GiveOptions.NoMerge);
            var ch = g.Changes.FirstOrDefault();
            if (g.Succeeded && !ch.Ref.IsNone && ch.Amount >= 2)
            {
                stackable = it;
                stackRef = ch.Ref;
                break;
            }
            foreach (var x in g.Changes) await Items.Take(c2, it.Id, x.Amount, x.Slot, true);
        }
        if (stackable == null)
            Info($"t4 split: none of {probed} stackable items (ammunition, consumables, tools, materials, ...) came as a stack of 2 with an item entity: a plain stack has no item entity, so no data");
        else
        {
            stackRef.Set(Key, new T4Tag { Name = "STACK", Count = 2 });
            var from = stackRef.Location()!.Value;
            var m = await Items.Move(c2, from.Slot, Chest("B"), 1);
            var part = m.Changes.FirstOrDefault();
            Info($"t4 split: {stackable.Name} x2 with entity {stackRef}: moved 1 to B: {m.Status}; the moved part is {part.Ref} (same entity: {part.Ref == stackRef})");
            var left = Items.Read(c2)!.Slots.FirstOrDefault(s => s.Slot == from.Slot);
            Check(stackRef.TryGet(Key, out var v) && v.Name == "STACK" && (part.Ref == stackRef || !part.Ref.Has(Key)),
                $"t4 split: the value stays with the item entity {stackRef} (now at {stackRef.Location()}, the slot left holds {left?.Count} as {left?.Ref}); " +
                $"the part that left is {(part.Ref == stackRef ? "the same entity" : part.Ref.IsNone ? "a plain stack" : $"a new entity {part.Ref}, value: {part.Ref.Has(Key)}")}");
        }

        // caps: a value over 64 KB is refused at Set; a live value that grows past it keeps its last form that fit
        var big = new T4Tag { Name = "BIG", Pad = new string('x', 70 * 1024) };
        var s6 = await Give("S6", "C", true, TwinGear(sword), sword);
        if (s6 != null)
        {
            s6.Frozen = true;
            try
            {
                _refs["S6"].Set(Key, big);
                Check(false, "t4 caps: Set with a 70 KB value did not throw");
            }
            catch (ArgumentException e)
            {
                Check(true, $"t4 caps: Set with a 70 KB value throws ArgumentException: {e.Message}");
            }
            Check(_refs["S6"].Get(Key).Name == "S6", "t4 caps: after the refused Set the old value stays");
            // grows past it: a save writes the last form that fit (Pad empty), logged once
            _refs["S6"].Get(Key).Pad = new string('y', 70 * 1024);
            var rs = await SaveNow("caps: S6 too big");
            var saved = BundleValue(rs.Revision, "S6");
            Check(saved != null && saved["Pad"]?.GetValue<string>().Length == 0,
                $"t4 caps: revision {rs.Revision}'s bundle keeps S6's last form that fit: {(saved == null ? "no record" : $"Pad {saved["Pad"]?.GetValue<string>().Length} chars, Count {saved["Count"]}")}");
            _refs["S6"].Get(Key).Pad = "";
            _ds.Notes["S6"] = "its saved value must be the last one that fit: Pad empty";
            SaveState();
        }
    }

    /// <summary>The value of this test's key with Name <paramref name="name"/> in the instances bundle of generation
    /// <paramref name="rev"/>, read from kiln.saves' files.</summary>
    private JsonObject? BundleValue(uint rev, string name)
    {
        var root = Saves.DataDirectory;
        if (root == null) return null;
        var man = Directory.GetFiles(Path.Combine(root, "manifests"), $"{rev:D10}*.json").OrderBy(f => f.Contains("pending")).FirstOrDefault();
        if (man == null) return null;
        var m = JsonNode.Parse(File.ReadAllText(man))!;
        var sha = m["bundles"]?[$"instances/{Context.Info.Id}"]?["sha256"]?.GetValue<string>();
        if (sha == null) return null;
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "blobs", sha[..2], sha + ".json")))!;
        foreach (var h in doc["holders"]!.AsArray())
            foreach (var r in h!["records"]!.AsArray())
                if (r!["keys"]?["t4.tag"] is JsonObject v && (v["Name"] ?? v["name"])?.GetValue<string>() == name)
                    return new JsonObject { ["Pad"] = (v["Pad"] ?? v["pad"])?.GetValue<string>(), ["Count"] = (v["Count"] ?? v["count"])?.GetValue<int>() };
        return null;
    }

    private async Task D2()
    {
        await VerifyLoaded("d2 after SIGINT", LoadedStates());
        var exact = _restored.Count(kv => kv.Value == InstanceMatch.Exact);
        Check(_restored.Count > 0 && _restored.Values.All(m => m == InstanceMatch.Exact),
            $"t4 d2: every restore Exact after a plain restart ({exact} Exact, {_restored.Count - exact} Moved)");
        if (_refs.TryGetValue("S6", out var r6) && r6.TryGet(Key, out var v6))
            Info($"t4 caps: S6's value after the restart: Pad {v6.Pad.Length} chars (later saves wrote it small again)");
        // swap S1 and S2 in A, S2 to B (with S3? S3 is in C now), counts; a save; then a move and counts not saved
        int f1 = Rec("S1").Flat, f2 = Rec("S2").Flat;
        await MoveTo("S1", "B");
        await MoveTo("S2", "C");
        await MoveTo("S1", "A", f2);
        await MoveTo("S2", "A", f1);
        await MoveTo("S2", "C");   // S2 and S3 twins in C
        Bump(200);
        VerifyLive("d2 swapped, S2 to C");
        await SaveNow("d2");
        await MoveTo("S1", "C");
        Bump(300);
        VerifyLive("d2 S1 to C, not saved");
        Next("d3", "kill");
    }

    private async Task D3()
    {
        await VerifyLoaded("d3 after kill -9 between saves", LoadedStates());
        await MoveTo("S2", "A");
        Bump(400);
        VerifyLive("d3 S2 back to A");
        await SaveNow("d3");
        Next("d4", "kill");
    }

    private async Task D4()
    {
        await VerifyLoaded("d4 after kill -9 right after a save", LoadedStates());
        var b = Saves.RestoreOnNextStart("t4-b1");
        Info($"t4 d4: restore staged: {b}");
        Next("d5", "stop");
    }

    private async Task D5()
    {
        var want = _ds.Backups.GetValueOrDefault("t4-b1");
        Check(_worldLoaded?.Revision == want, $"t4 d5: the restored world file is t4-b1's revision {want}: loaded {_worldLoaded}");
        await VerifyLoaded("d5 after restoring t4-b1", LedgerOf(want));
        var s3 = _refs.GetValueOrDefault("S3");
        Info($"t4 d5: S3 (moved B -> C after the backup, back in B by the restore): restored {(_restored.TryGetValue(s3, out var m) ? m.ToString() : "no event")} at {s3.Location()}");
        var sword = Sword()!;

        // break-it 1: a tagged sword taken and an identical one given into its slot, in one step
        var x1 = await Give("X1", "E", true, TwinGear(sword), sword);
        if (x1 != null)
        {
            var e = Chest("E");
            var slot = AtFlat(e, x1.Flat)!.Slot;
            var take = Items.Take(e, sword.Id, 1, slot, true, _refs["X1"].Entity.Id);
            var give = Items.Give(e, sword.Id, 1, TwinGear(sword), slot: slot);
            var (t, g) = (await take, await give);
            var ch = g.Changes.FirstOrDefault(c => !c.Ref.IsNone);
            var newRef = ch.Ref;
            Info($"t4 break-it 1: take {t.Status} at step(s) {t.Steps}, give {g.Status} into the same slot: new {newRef} (old {_refs["X1"]})");
            await Steps(5);
            Check(t.Succeeded && g.Succeeded && newRef != _refs["X1"] && !newRef.Has(Key) && !_refs["X1"].Has(Key),
                $"t4 break-it 1: the identical sword given into the taken one's slot has no value (new {newRef}, value {newRef.Has(Key)}); the taken one's is gone");
            _ds.Swords.Remove(x1);
            _refs["X1n"] = newRef;
            if (!newRef.IsNone)
                _ds.Swords.Add(new SwordRec { Name = "X1n", Tagged = false, Holder = x1.Holder, Flat = FlatOf(Items.Read(e)!, ch.Slot) });
            Changed("break-it 1");
        }

        // break-it 2: a move in the step of a save: the move is queued in the WorldSavingEvent, so it runs between the
        // first staging and the game's snapshot; then kill -9 once that save is written
        var a = Chest("A");
        var s1 = Rec("S1");
        Task<MoveResult>? racing = null;
        uint racingRev = 0;
        var gTarget = Chest("G");
        _onSaving = e =>
        {
            _onSaving = null;
            var slot = AtFlat(a, s1.Flat);
            if (slot == null || slot.ItemEntity != _refs["S1"].Entity.Id) return;
            _inflight++;
            racingRev = e.Revision;
            racing = Items.Move(a, slot.Slot, gTarget);
            Info($"t4 break-it 2: S1 move A -> G queued inside the WorldSavingEvent of revision {e.Revision} ({e.Reason}, step {e.Step})");
        };
        // the next save, whichever starts it (the autosave comes within 30 s); kill -9 right after it is written
        await Until(() => racing != null && _savedRevs.Contains(racingRev), 25 * 90);
        if (racing != null)
        {
            var m2 = await racing;
            _inflight--;
            var now = Items.Read(gTarget)!.Slots.FirstOrDefault(x => x.ItemEntity == _refs["S1"].Entity.Id);
            Info($"t4 break-it 2: the racing move: {m2.Status}, {m2.Steps} step(s); revision {racingRev} written: {_savedRevs.Contains(racingRev)}");
            if (now != null)
            {
                // the ledger line of that revision has S1 in A; add the moved state as a second, racy line
                s1.Holder = PosOf(gTarget);
                s1.Flat = FlatOf(Items.Read(gTarget)!, now.Slot);
                AppendLedger(racingRev, true, "break-it 2: S1 moved in the step of the save");
                SaveState();
            }
        }
        else Check(false, "t4 break-it 2: no save started within 90 s");
        _ds.Notes["breakit2"] = $"revision {racingRev}";
        Next("d6", "kill");
    }

    private async Task D6()
    {
        var states = LoadedStates();
        await VerifyLoaded("d6 after break-it 1 and 2 (kill -9 after the racing save)", states);
        Info($"t4 break-it 2: S1 is at {_refs.GetValueOrDefault("S1").Location()} after the restart (the save {(states.Count > 1 ? "had the move in flight" : "had no move in flight")})");

        // break-it 3: two chests of one template side by side, each with a tagged twin in the same slot
        var sword = Sword()!;
        var a = Chest("A");
        var ai = Spawn.Inspect(a)!;
        var tmpl = ai.Template!.Value;
        var spots = new (string Name, double Dx)[] { ("P1", 0), ("P2", 0.3), ("Q1", 3), ("Q2", 4) };
        foreach (var (name, dx) in spots)
        {
            try
            {
                // a retried boot spawns 4 m further on (chests of an earlier try may have been saved)
                var pos = new WorldPosition(ai.Position.X + 6 + dx, ai.Position.Y + 40, ai.Position.Z + 6 + 4 * (_ds.Boot % 5));
                var e = await Spawn.Entity(tmpl, pos, 0, new SpawnOptions { Tag = "instances-test-t4" });
                await Until(() => Items.Read(e) is { Initialized: true }, 100);
                _ds.Chests[name] = PosOf(e);
                Info($"t4 break-it 3: chest {name} = {e} {_ds.Chests[name]}");
            }
            catch (Exception ex)
            {
                Check(false, $"t4 break-it 3: spawning chest {name}: {ex.Message}");
                Next("d7", "stop");
                return;
            }
        }
        foreach (var (name, _) in spots)
        {
            var rec = await Give("Z" + name, name, true, TwinGear(sword), sword);
            if (rec != null) Info($"t4 break-it 3: Z{name} in {name} flat {rec.Flat}");
        }
        VerifyLive("d6 break-it 3 set up");
        await SaveNow("break-it 3");
        Next("d7", "stop");
    }

    private async Task D7()
    {
        await VerifyLoaded("d7 after break-it 3 (P1/P2 0.3 m apart, Q1/Q2 1 m apart)", LoadedStates());
        foreach (var n in new[] { "P1", "P2", "Q1", "Q2" })
            Info($"t4 break-it 3: chest {n} resolves to {Resolve(_ds.Chests[n])} (entities of the template within 0.5 m decide)");
        await RiderWait("before going away");
        Next("d8", "rider-off");
    }

    private async Task D8()
    {
        await VerifyLoaded("d8 without the rider", LoadedStates());
        await RiderMove();
        await SaveNow("d8: the rider's twins moved while it is not loaded");
        Next("d9", "rider-on");
    }

    private async Task D9()
    {
        await RiderWait("back after it was not loaded");
        await VerifyLoaded("d9 after the rider came back", LoadedStates());

        // 10 hot reloads: the run script runs reload-test; every instance's OnLoad checks the values (T4ReloadCheck)
        WriteReloadPlan();
        var done = Path.Combine(T4Dir, "reload.done");
        File.Delete(done);
        _ds.Phase = "d9r";
        SaveState();
        Ready("reload 10");
    }

    private static List<SwordRec> Swapped(List<SwordRec> l)
    {
        var copy = l.Select(s => s.Clone()).ToList();
        var a = copy.First(s => s.Name == "W1");
        var b = copy.First(s => s.Name == "W2");
        (a.Holder, a.Flat, b.Holder, b.Flat) = (b.Holder, b.Flat, a.Holder, a.Flat);
        return copy;
    }

    /// <summary>Break-it 4 (phase e2): in the first tick where the world runs and kiln.items can move, the twins W1 and W2
    /// are swapped (their slots in the loaded revision's ledger), racing the matcher's first pass over their chest.</summary>
    private void EarlySwapTick(in TickInfo t)
    {
        if (_earlySwapStarted || _worldLoaded == null || !Spawn.WorldRunning || Items.Status() is not { Transactions: true }) return;
        _earlySwapStarted = true;
        Context.Tick.Pre -= EarlySwapTick;
        _ = EarlySwap(t.Step);
    }

    private bool _earlySwapStarted;

    private async Task EarlySwap(ulong step)
    {
        try
        {
            var l = LedgerOf(_worldLoaded!.Revision).LastOrDefault();
            if (l == null)
            {
                _ds.Notes["early"] = "no ledger entry";
                return;
            }
            _ds.Swords = l.Select(s => s.Clone()).ToList();
            var s1 = Rec("W1");
            var s2 = Rec("W2");
            if (!s1.Holder.Same(s2.Holder))
            {
                _ds.Notes["early"] = $"W1 and W2 are not in one chest ({s1}, {s2}); skipped";
                return;
            }
            var h = Resolve(s1.Holder);
            var x = AtFlat(h, s1.Flat);
            var y = AtFlat(h, s2.Flat);
            var b = Chest("B");
            var c = Chest("C");
            if (x == null || y == null || x.ItemEntity.IsNone || y.ItemEntity.IsNone)
            {
                _ds.Notes["early"] = "the twins are not in their slots at the load";
                return;
            }
            var t0 = Stopwatch.StartNew();
            var m1 = Items.Move(h, x.Slot, b);
            var m2 = Items.Move(h, y.Slot, c);
            var (r1, r2) = (await m1, await m2);
            var back1 = Items.Move(b, r1.Changes[0].To, h, toSlot: y.Slot);
            var back2 = Items.Move(c, r2.Changes[0].To, h, toSlot: x.Slot);
            var (r3, r4) = (await back1, await back2);
            var swapped = r1.Succeeded && r2.Succeeded && r3.Succeeded && r4.Succeeded;
            _ds.Notes["early"] = $"swap queued at step {step} ({DateTime.Now:HH:mm:ss.fff}): {r1.Status}/{r2.Status} then {r3.Status}/{r4.Status} in {t0.ElapsedMilliseconds} ms; " +
                                 $"W1 (entity {x.ItemEntity}) now in flat {s2.Flat}, W2 (entity {y.ItemEntity}) in flat {s1.Flat}";
            if (swapped) _ds.Notes["earlySwapped"] = "1";
            Info($"t4 break-it 4: {_ds.Notes["early"]}");
        }
        catch (Exception e)
        {
            _ds.Notes["early"] = $"threw: {e.Message}";
        }
        finally
        {
            SaveState();
            File.WriteAllText(Path.Combine(T4Dir, "early-swap.done"), "");
        }
    }

    // ---- hot reloads ----

    private sealed record ReloadItem(string Name, uint Holder, uint Entity, int Count);

    private void WriteReloadPlan()
    {
        var plan = _ds.Swords.Where(s => s.Tagged && _refs.ContainsKey(s.Name) && _refs[s.Name].TryGet(Key, out _))
            .Select(s => new ReloadItem(s.Name, Resolve(s.Holder).Value, _refs[s.Name].Entity.Id.Value, _refs[s.Name].Get(Key).Count)).ToList();
        File.WriteAllText(Path.Combine(T4Dir, "reload.json"), JsonSerializer.Serialize(plan));
        File.WriteAllText(Path.Combine(T4Dir, "reload.count"), "0");
        Info($"t4 reload: {plan.Count} tagged item(s) to find after each reload");
    }

    /// <summary>In OnLoad of every reloaded instance (phase d9r): every tagged item still has its own value.</summary>
    private void T4ReloadCheck()
    {
        if (_ds.Phase != "d9r") return;
        var file = Path.Combine(T4Dir, "reload.json");
        if (!File.Exists(file) || File.Exists(Path.Combine(T4Dir, "reload.done"))) return;
        var cf = Path.Combine(T4Dir, "reload.count");
        var n = (File.Exists(cf) ? int.Parse(File.ReadAllText(cf)) : 0) + 1;
        File.WriteAllText(cf, n.ToString());
        var plan = JsonSerializer.Deserialize<List<ReloadItem>>(File.ReadAllText(file))!;
        int ok = 0;
        var bad = new List<string>();
        foreach (var p in plan)
        {
            try
            {
                var s = Items.Read(new EntityId(p.Holder))?.Slots.FirstOrDefault(x => x.ItemEntity.Value == p.Entity);
                if (s == null) bad.Add($"{p.Name}: not in {p.Holder}");
                else if (!s.Ref.TryGet(Key, out var v)) bad.Add($"{p.Name}: no value");
                else if (v.Name != p.Name || v.Count != p.Count) bad.Add($"{p.Name}: carries {v.Name} #{v.Count}");
                else ok++;
            }
            catch (Exception e)
            {
                bad.Add($"{p.Name}: {e.GetType().Name}: {e.Message}");
            }
        }
        Check(bad.Count == 0, $"t4 reload {n}: OnLoad of the reloaded instance finds {ok} of {plan.Count} values on their items{(bad.Count > 0 ? ": " + string.Join("; ", bad) : "")}");
    }

    // ---- after the reloads: /instances, numbers ----

    private async Task D9r()
    {
        await Until(() => File.Exists(Path.Combine(T4Dir, "reload.done")), 25 * 120);
        var cf = Path.Combine(T4Dir, "reload.count");
        Info($"t4 reload: {(File.Exists(cf) ? File.ReadAllText(cf) : "0")} reloaded instance(s) ran their OnLoad check");
        await VerifyLoaded("d9r after 10 reloads", [_ds.Swords.Select(s => s.Clone()).ToList()]);
        File.Delete(Path.Combine(T4Dir, "console.done"));
        _ds.Phase = "d9c";
        SaveState();
        Ready("console instances");
        await Until(() => File.Exists(Path.Combine(T4Dir, "console.done")), 25 * 60);
        Next("n1", "stop");
    }

    private async Task N1()
    {
        await VerifyLoaded("n1", LoadedStates());
        await Numbers();
        Next("d10", "stop");
    }

    private readonly List<(int N, string Line)> _stageCosts = [];

    /// <summary>10, 100 and 1,000 tagged items: a save's staging time each (kiln.log's staging lines, debug level, give
    /// the store's own times; the test logs the save's duration as the game reports it).</summary>
    private async Task Numbers()
    {
        var sword = Sword()!;
        var a = Chest("A");
        var ai = Spawn.Inspect(a)!;
        var holders = new List<EntityId>();
        for (var i = 0; i < 48; i++)
        {
            try
            {
                // 40 m up, as break-it 3's chests (which came back after a restart); a first try 60 m up did not come back
                var e = await Spawn.Entity(ai.Template!.Value, new WorldPosition(ai.Position.X - 30 + 2.5 * (i % 8), ai.Position.Y + 40, ai.Position.Z - 30 + 2.5 * (i / 8)), 0,
                    new SpawnOptions { Tag = "instances-test-t4-numbers" });
                holders.Add(e);
            }
            catch (Exception ex)
            {
                Info($"t4 numbers: spawning a chest failed: {ex.Message}");
                break;
            }
        }
        await Until(() => holders.All(e => Items.Read(e) is { Initialized: true }), 200);
        var tagged = 0;
        var tags = new List<(EntityId Holder, int Flat, uint Entity)>();
        foreach (var n in new[] { 10, 100, 1000 })
        {
            foreach (var h in holders)
            {
                if (tagged >= n) break;
                var free = FreeGeneric(h).Count;
                if (free == 0) continue;
                var g = await Items.Give(h, sword.Id, Math.Min(free, n - tagged), TwinGear(sword), GiveOptions.NoMerge);
                var c = Items.Read(h)!;
                foreach (var s in c.Slots.Where(s => s.Item == sword.Id && !s.Ref.IsNone && !s.Ref.Has(Key)))
                {
                    if (tagged >= n) break;
                    s.Ref.Set(Key, new T4Tag { Name = $"N{tagged}", Count = n });
                    tags.Add((h, FlatOf(c, s.Slot), s.ItemEntity.Value));
                    tagged++;
                }
            }
            var sw = Stopwatch.StartNew();
            var r = await SaveNow($"numbers: {tagged} tagged");
            Info($"t4 numbers: {tagged} tagged items in {holders.Count} spawned chests: save {r} (request to written {sw.ElapsedMilliseconds} ms); the store's staging time is kiln.log's 'staged mod data generation {r.Revision}' line");
            _stageCosts.Add((tagged, $"revision {r.Revision}"));
        }
        _ds.Notes["numbers"] = string.Join(", ", _stageCosts.Select(x => $"{x.N}: {x.Line}"));
        _ds.Extra.Clear();
        File.WriteAllText(Path.Combine(T4Dir, "numbers.json"), JsonSerializer.Serialize(tags.Select(t => new { holder = PosOf(t.Holder), t.Flat, t.Entity })));
        SaveState();
    }

    // ---- d10: matching 1000 at a load; unequal twins ----

    private sealed record NumTag(HolderPos holder, int Flat, uint Entity);

    private async Task D10()
    {
        // the matcher's work at this load: how many of the 1000 came back and how long it took from the world's load
        var file = Path.Combine(T4Dir, "numbers.json");
        var tags = File.Exists(file) ? JsonSerializer.Deserialize<List<NumTag>>(File.ReadAllText(file))! : [];
        var sw = Stopwatch.StartNew();
        var steps = 0;
        var maxStepMs = 0.0;
        var last = Stopwatch.GetTimestamp();
        void Measure(in TickInfo _)
        {
            var now = Stopwatch.GetTimestamp();
            maxStepMs = Math.Max(maxStepMs, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
            steps++;
        }
        Context.Tick.Pre += Measure;
        await Until(() => _restored.Count >= tags.Count, 25 * 120);
        Context.Tick.Pre -= Measure;
        int own = 0, none = 0, wrong = 0;
        for (var i = 0; i < tags.Count; i++)
        {
            var s = AtFlat(Resolve(tags[i].holder), tags[i].Flat);
            if (s == null || s.Ref.IsNone || !s.Ref.TryGet(Key, out var v)) none++;
            else if (v.Name == $"N{i}") own++;
            else
            {
                wrong++;
                if (wrong <= 5) Check(false, $"t4 numbers: MIXUP: N{i}'s place holds {v.Name}'s value");
            }
        }
        var gone = tags.Select(t => t.holder).DistinctBy(h => $"{h}").Count(h => Resolve(h).IsNone);
        Check(wrong == 0 && own == tags.Count, $"t4 numbers: {own} of {tags.Count} twins (identical swords in spawned chests) carry their own value after the restart, " +
                                               $"{none} without, {wrong} with another's; {gone} of their chests not in the world");
        Info($"t4 numbers: {_restored.Count} restored event(s) for {tags.Count} tagged items at this load ({_restored.Values.Count(m => m == InstanceMatch.Exact)} Exact), " +
             $"{InstanceData.Parked(Key).Count} parked; waited {sw.ElapsedMilliseconds} ms ({steps} steps) after the test started, longest step {maxStepMs:F1} ms; " +
             "the store's per-holder lines (debug) carry the times");
        await VerifyLoaded("d10", LoadedStates());

        // unequal twins: U1 and U2 differ in one stable field (level, by an unlocked upgrade); a save; U1 moved within F;
        // a save; that generation is dropped, so the older one (rule 2) decides
        var sword = Sword()!;
        var unlock = Items.GearOptionsAvailable;
        var u1 = await Give("U1", "F", true, TwinGear(sword), sword);
        var u2 = await Give("U2", "F", true, unlock ? TwinGear(sword) with { Unlocked = 1 } : new Gear(Math.Max(1, sword.LevelMin) + 1, ItemRarity.Rare), sword);
        var f = Items.Read(Chest("F"))!.Slots.Where(s => s.Item == sword.Id).ToList();
        Info($"t4 unequal: U1 and U2 in F: {string.Join(", ", f.Select(s => $"flat {FlatOf(Items.Read(Chest("F"))!, s.Slot)} level {s.Level} {s.Rarity}"))}");
        var r1 = await SaveNow("unequal: before the move");
        var freeF = FreeGeneric(Chest("F")).Select(s => FlatOf(Items.Read(Chest("F"))!, s.Slot)).Last();
        await MoveTo("U1", "G");
        await MoveTo("U1", "F", freeF);
        VerifyLive("d10 U1 moved within F");
        var r2 = await SaveNow("unequal: after the move");
        _ds.Notes["unequal"] = $"{r1.Revision} then {r2.Revision} (dropped)";
        _ds.Phase = "d11";
        SaveState();
        Ready($"drop-gen {r2.Revision}");
    }

    private async Task D11()
    {
        Info($"t4 d11: loaded {_worldLoaded} (expected an older generation: {_ds.Notes.GetValueOrDefault("unequal")})");
        var states = LoadedStates();
        if (states.Count == 0)
        {
            // the world file is the dropped revision's: the test's truth is that revision's ledger; the data is older
            var rev = _worldLoaded?.Revision ?? 0;
            states = LedgerOf(rev);
        }
        await VerifyLoaded("d11 unequal twins on an older generation", states, s => s.Name is "U1" or "U2");
        foreach (var n in new[] { "U1", "U2" })
            Info($"t4 unequal: {n}: {(_refs.TryGetValue(n, out var r) && _restored.TryGetValue(r, out var m) ? $"restored {m}" : "not restored")}");
        await VerifyLoaded("d11 the rest on an older generation", states, s => s.Name is not ("U1" or "U2"));
        Next("e1", "stop");
    }

    // ---- break-it 4: twins swapped at once after a load, racing the matcher ----

    private async Task E1()
    {
        var sword = Sword()!;
        // a retried boot: the twins of an earlier try go first (taken: their data is dropped as destroyed)
        foreach (var old in _ds.Swords.Where(x => x.Name is "W1" or "W2").ToList())
        {
            var h = Resolve(old.Holder);
            if (AtFlat(h, old.Flat) is { IsEmpty: false } at && at.Item == sword.Id)
                Info($"t4 e1: an earlier W twin taken: {await Items.TakeSlot(h, at)}");
            _ds.Swords.Remove(old);
        }
        File.Delete(Path.Combine(T4Dir, "early-swap.done"));
        _ds.Notes.Remove("early");
        _ds.Notes.Remove("earlySwapped");
        await Give("W1", "A", true, TwinGear(sword), sword);
        await Give("W2", "A", true, TwinGear(sword), sword);
        VerifyLive("e1 twins W1 W2 in A", _ds.Swords.Where(x => x.Name is "W1" or "W2").ToList());
        // for e2's older-generation check: V3, an item of which chest E holds no other piece
        var e = Chest("E");
        var held = Items.Read(e)!.Slots.Where(x => !x.IsEmpty).Select(x => x.Item).ToHashSet();
        var v3 = Items.All.First(i => i.IsGear && i.LevelMin > 0 && !held.Contains(i.Id) && i.Category == ItemCategory.Equipment && i.Name.Contains("Boots"));
        _ds.Swords.RemoveAll(x => x.Name == "V3");
        await Give("V3", "E", true, new Gear(Math.Max(1, v3.LevelMin), ItemRarity.Rare), v3);
        await SaveNow("e1");
        Next("e2", "stop");
    }

    private async Task E2()
    {
        await Until(() => File.Exists(Path.Combine(T4Dir, "early-swap.done")), 25 * 60);
        Info($"t4 break-it 4: {_ds.Notes.GetValueOrDefault("early")}");
        var states = LoadedStates();
        var swapped = _ds.Notes.GetValueOrDefault("earlySwapped") == "1";
        await VerifyLoaded($"e2 W1 W2 after {(swapped ? "the early swap" : "no swap")}", swapped ? states.Select(Swapped).ToList() : states,
            s => s.Name is "W1" or "W2");
        await VerifyLoaded("e2 V3", states, s => s.Name == "V3");
        // V3, the one item of its kind in E: a save, a move within E, a save, that generation dropped: rule 2 decides
        var r1 = await SaveNow("V3 before the move");
        var freeE = FreeGeneric(Chest("E")).Select(x => FlatOf(Items.Read(Chest("E"))!, x.Slot)).Last();
        var from = Rec("V3").Flat;
        await MoveTo("V3", "G");
        await MoveTo("V3", "E", freeE);
        VerifyLive("e2 V3 moved within E", _ds.Swords.Where(x => x.Name == "V3").ToList());
        var r2 = await SaveNow("V3 after the move");
        _ds.Notes["unequalV3"] = $"V3 flat {from} -> {freeE}; {r1.Revision} then {r2.Revision} (dropped)";
        _ds.Phase = "u4";
        SaveState();
        Ready($"drop-gen {r2.Revision}");
    }

    private async Task U4()
    {
        Info($"t4 u4: loaded {_worldLoaded} ({_ds.Notes.GetValueOrDefault("unequalV3")})");
        await VerifyLoaded("u4 V3 on an older generation", LoadedStates(), s => s.Name == "V3");
        Info($"t4 unequal: V3: {(_refs.TryGetValue("V3", out var r) && _restored.TryGetValue(r, out var m) ? $"restored {m}" : "not restored")}");
        Ready("end");
    }

    // ---- unequal twins without a look-alike: V1 and V2 of an item no other slot of chest E holds ----

    private async Task U1()
    {
        // an item E holds nothing of (E has swords only): the order setup's fur arms, else any other gear
        var e = Chest("E");
        var held = Items.Read(e)!.Slots.Where(x => !x.IsEmpty).Select(x => x.Item).ToHashSet();
        var item = Items.Lookup("Armor_T0_Fur_Arms") is { } fa && !held.Contains(fa.Id)
            ? fa
            : Items.All.First(i => i.IsGear && i.LevelMin > 0 && !held.Contains(i.Id) && i.Category == ItemCategory.Weapons);
        var lv = Math.Max(1, item.LevelMin);
        var v1 = await Give("V1", "E", true, new Gear(lv, ItemRarity.Rare), item);
        var v2 = await Give("V2", "E", true, new Gear(lv + 1, ItemRarity.Rare), item);
        var slots = Items.Read(e)!.Slots.Where(x => x.Item == item.Id).ToList();
        Info($"t4 unequal: V1 and V2 ({item.Name}) in E: {string.Join(", ", slots.Select(x => $"flat {FlatOf(Items.Read(e)!, x.Slot)} level {x.Level} {x.Rarity}"))}");
        var r1 = await SaveNow("unequal V: before the move");
        var freeE = FreeGeneric(e).Select(x => FlatOf(Items.Read(e)!, x.Slot)).Last();
        await MoveTo("V1", "G");
        await MoveTo("V1", "E", freeE);
        VerifyLive("u1 V1 moved within E", _ds.Swords.Where(x => x.Name is "V1" or "V2").ToList());
        var r2 = await SaveNow("unequal V: after the move");
        _ds.Notes["unequalV"] = $"{r1.Revision} then {r2.Revision} (dropped)";
        _ds.Phase = "u2";
        SaveState();
        Ready($"drop-gen {r2.Revision}");
    }

    private async Task U2()
    {
        Info($"t4 u2: loaded {_worldLoaded} (expected an older generation: {_ds.Notes.GetValueOrDefault("unequalV")})");
        await VerifyLoaded("u2 unequal twins V1 V2 on an older generation", LoadedStates(), s => s.Name is "V1" or "V2");
        foreach (var n in new[] { "V1", "V2" })
            Info($"t4 unequal: {n}: {(_refs.TryGetValue(n, out var r) && _restored.TryGetValue(r, out var m) ? $"restored {m}" : "not restored")}");
        Ready("end");
    }
}
