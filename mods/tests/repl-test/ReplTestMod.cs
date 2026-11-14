// SPDX-License-Identifier: MIT
// kiln.repl on its loopback harness (ReplicationLab), on a lab server with no player (the dossier's chapter 11 §6).
//   loopback decode  4 fake players; a watering can in a chest, both forced relevant to each fake; a random water
//                    amount every step; for every message the fakes decoded, the can's NetworkFuel as the fake's client
//                    holds it must equal the can's live bytes (10,000+ checks); every component with the game's generic
//                    delta equal to the server's snapshot of that message; 0 decode faults. Components with a codec of
//                    their own (CurrentTransform) are lossy: reported, not failed.
//   release          after RemoveFakePeers the fakes' replication objects are inactive and their slot bits are gone.
//   sim threads      (chapter 11 §7.2) the slices of the game's fuel_network program while the fakes are active: on how
//                    many threads they ran; kiln.repl logs the world's players mask beside it.
//   lab gate         without kiln.json's policy "any" or without a lab port, every ReplicationLab member is refused
//                    with KilnUnavailableException("repl.loopback").
using System.Text.Json.Nodes;
using Kiln;

namespace ReplTest;

public sealed class ReplTestMod : Mod
{
    private const string Me = "repl-test";
    private const string Can = "Tool_T1_WateringCan";
    private const int Checks = 10_000;
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"{Me}: {s}");
    private bool Result(bool ok, string name, string detail)
    {
        if (ok) { _pass++; Log.Info($"{Me}: {name} PASS ({detail})"); }
        else { _fail++; Log.Error($"{Me}: {name} FAIL ({detail})"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    public override void OnLoad() => _ = Run().ContinueWith(t =>
    {
        if (t.Exception != null) Result(false, "run", $"the test threw: {t.Exception.InnerException}");
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    });

    private async Task Run()
    {
        for (var i = 0; i < 240 && !(Spawn.WorldRunning && Items.All.Count > 0); i++) await Steps(25);
        await Steps(100);  // the world settles; kiln.repl has read its registry
        var (policyAny, labPort) = LabConfig();
        Info($"kiln.json policy {(policyAny ? "any" : "not any")}, labPort {labPort}; repl.loopback {Capabilities.Get("repl.loopback")}");
        if (!policyAny || labPort == 0)
        {
            Refused(policyAny ? "no lab port" : labPort == 0 ? "no policy any, no lab port" : "no policy any");
            return;
        }
        await Loopback();
    }

    // The lab's own kiln.json and kiln/config/network.json, as the harness's gate reads them.
    private (bool PolicyAny, int LabPort) LabConfig()
    {
        var kiln = Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", ".."));
        static JsonNode? Load(string file)
        {
            try
            {
                return File.Exists(file)
                    ? JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        var k = Load(Path.Combine(kiln, "kiln.json"));
        var policy = (string?)k?["server"]?["policy"] ?? (string?)k?["policy"];
        var n = Load(Path.Combine(kiln, "config", "network.json"));
        var port = n?["labPort"] is JsonValue v && v.TryGetValue<int>(out var p) ? p : 0;
        return (policy == "any", port);
    }

    private void Refused(string why)
    {
        var refused = new List<string>();
        void Expect(string member, Action call)
        {
            try
            {
                call();
                refused.Add($"{member} answered");
            }
            catch (KilnUnavailableException e) when (e.Capability == "repl.loopback")
            {
            }
            catch (Exception e)
            {
                refused.Add($"{member} threw {e.GetType().Name}: {e.Message}");
            }
        }
        Expect("AddFakePeers", () => ReplicationLab.AddFakePeers(4));
        Expect("ForceRelevant", () => ReplicationLab.ForceRelevant(16, new EntityId(1)));
        Expect("RemoveFakePeers", ReplicationLab.RemoveFakePeers);
        Expect("LoopbackStats", () => ReplicationLab.LoopbackStats());
        Expect("Decoded", () => ReplicationLab.Decoded(16, new EntityId(1), "NetworkFuel"));
        var row = Capabilities.Get("repl.loopback");
        Result(refused.Count == 0 && row.State == CapabilityState.Unavailable && row.Reason == CapabilityReason.Policy, "lab gate",
            $"{why}: {(refused.Count == 0 ? "all 5 lab members refused with repl.loopback" : string.Join("; ", refused))}; row {row.State} {row.Reason}: {row.ReasonText}");
    }

    // A container of the world with room, else (a fresh lab world) a chest of the test's own at the start's altar.
    private async Task<EntityId> Chest()
    {
        for (var i = 0; i < 8; i++)
        {
            foreach (var e in Items.FindInventories(InventoryKinds.Containers))
                if (Items.Read(e) is { Initialized: true } c && c.Slots.Any(s => s.IsEmpty))
                    return e;
            await Steps(25);
        }
        WorldPosition? anchor = null;
        for (var i = 0; i < 120 && anchor == null; i++)
        {
            foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
                if (anchor == null)
                    foreach (var id in Spawn.FindByName(filter, max: 4))
                        if (Spawn.Inspect(id) is { } info) { anchor = info.Position; break; }
            if (anchor == null) await Steps(25);
        }
        if (anchor == null) return default;
        var chest = await Spawn.Entity("Prop_Decoration_T1_Storage_24", anchor.Value with { X = anchor.Value.X - 5, Z = anchor.Value.Z - 5 }, 0,
            new SpawnOptions { Tag = Me });
        for (var i = 0; i < 60 && Items.Read(chest) is not { Initialized: true }; i++) await Steps(1);
        Info($"no container with room in this world: a chest of the test's own, {chest}");
        return chest;
    }

    private async Task Loopback()
    {
        // a watering can in a chest of the world
        var chest = await Chest();
        if (!Result(!chest.IsNone, "setup", $"a container with room: {chest} {Spawn.Inspect(chest)?.TemplateName}")) return;
        var give = await Items.Give(chest, Items.Lookup(Can)!.Id, 1, options: GiveOptions.NoMerge);
        await Steps(2);
        var slot = give.Changes.Count > 0 ? Items.Read(chest)?.Slots.FirstOrDefault(s => s.Slot == give.Changes[0].Slot) : null;
        var can = slot?.ItemEntity ?? EntityId.None;
        var max = can.IsNone ? 0 : Items.Fuel.Read(can)?.Max ?? 0;
        if (!Result(!can.IsNone && max > 0, "setup", $"a watering can {can} in chest {chest}, water max {max}")) return;

        // the sim's threads before the fakes (a serial world: the tick thread only)
        var threads = new ThreadCount();
        IDisposable? hook = null;
        try
        {
            hook = GameSystems.Hook("fuel_network", before: threads.Add);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            Info($"sim threads: not measured ({e.Message})");
        }
        var tick = Environment.CurrentManagedThreadId;
        await Steps(120);
        var before = threads.Take();

        var fakes = ReplicationLab.AddFakePeers(4);
        Info($"fakes (machine/slot): {string.Join(", ", fakes.Select(f => $"{f.Machine}/{f.Slot}"))}");
        foreach (var f in fakes)
        {
            ReplicationLab.ForceRelevant(f.Machine, chest);
            ReplicationLab.ForceRelevant(f.Machine, can);
        }
        if (hook != null) await SimThreads(threads, before, tick);

        // a random water amount every step; every fresh decode of the can on each fake against the can's live bytes
        var rnd = new Random(12345);
        var last = fakes.ToDictionary(f => f.Machine, _ => uint.MaxValue);
        long checks = 0, fails = 0, steps = 0;
        string? firstFail = null;
        Task<FuelResult>? pending = null;
        while (checks < Checks && steps < 20_000)
        {
            await Steps(1);
            steps++;
            var live = World.ReadBytes(can, "NetworkFuel");
            foreach (var f in fakes)
            {
                var d = ReplicationLab.Decoded(f.Machine, can, "NetworkFuel");
                if (d is null || d.Tick == last[f.Machine]) continue;
                last[f.Machine] = d.Tick;
                checks++;
                if (live is not null && d.Bytes.Span.SequenceEqual(live)) continue;
                fails++;
                firstFail ??= $"fake {f.Machine} tick {d.Tick}: decoded {Convert.ToHexString(d.Bytes.Span)}, live {(live is null ? "none" : Convert.ToHexString(live))}";
            }
            if (pending is null || pending.IsCompleted) pending = Items.Fuel.Set(can, rnd.Next(0, max + 1));
        }
        var s = ReplicationLab.LoopbackStats();
        Info($"loopback stats: {s}");
        if (firstFail != null) Log.Warn($"{Me}: first NetworkFuel difference: {firstFail}");
        Result(checks >= Checks && fails == 0 && s.DecodeFaults == 0 && s.ComponentsDiffer == 0 && s.Decoded > 0, "loopback decode",
            $"{checks} NetworkFuel checks in {steps} steps, {fails} fail; decoded {s.Decoded}, components equal {s.ComponentsEqual}, " +
            $"differ {s.ComponentsDiffer}; custom-coded equal {s.CustomEqual}, differ {s.CustomDiffer} (reported, not failed); " +
            $"{s.DecodeFaults} decode faults; no baseline {s.NoBaseline}, dropped {s.Dropped}, acked {s.Acked} of {s.Messages} messages");
        hook?.Dispose();

        // release
        var fakeMachines = fakes.Aggregate(0, (m, f) => m | 1 << f.Machine);
        var fakeSlots = fakes.Aggregate(0, (m, f) => m | 1 << f.Slot);
        ReplicationLab.RemoveFakePeers();
        var now = ReplicationLab.LoopbackStats();
        await Steps(10);
        var later = ReplicationLab.LoopbackStats();
        Result(now.Fakes == 0 && (now.ActiveObjects & fakeMachines) == 0 && (now.SlotMask & fakeSlots) == 0 &&
               (later.ActiveObjects & fakeMachines) == 0 && (later.SlotMask & fakeSlots) == 0, "release",
            $"objects of machines 0x{fakeMachines:x5} inactive: active now 0x{now.ActiveObjects:x5}, 10 steps later 0x{later.ActiveObjects:x5}; " +
            $"slot mask 0x{now.SlotMask:x4}, 10 steps later 0x{later.SlotMask:x4} (fake slots 0x{fakeSlots:x4})");
    }

    // Chapter 11 §7.2: does a fake player put the sim's programs on the game's workers?
    private async Task SimThreads(ThreadCount threads, (long Slices, int Threads, bool OffTick) before, int tick)
    {
        threads.Take();
        await Steps(120);
        var with = threads.Take();
        Info($"sim threads: fuel_network before the fakes {before.Slices} slices on {before.Threads} thread(s){(before.OffTick ? ", some off the tick thread" : "")}; " +
             $"with 4 fakes {with.Slices} slices on {with.Threads} thread(s){(with.OffTick ? ", some off the tick thread" : ", all on the tick thread")} " +
             $"(tick thread {tick}): the sim {(with.Threads > 1 || with.OffTick ? "goes parallel" : "stays serial")} with fakes");
    }

    // Thread ids of a program's slices (game worker threads call it; counted with a lock).
    private sealed class ThreadCount
    {
        private readonly object _gate = new();
        private readonly HashSet<int> _ids = [];
        private long _slices;
        private int _tick = -1;

        public void Add()
        {
            var id = Environment.CurrentManagedThreadId;
            lock (_gate)
            {
                _slices++;
                _ids.Add(id);
            }
        }

        public (long Slices, int Threads, bool OffTick) Take()
        {
            lock (_gate)
            {
                if (_tick < 0) _tick = Environment.CurrentManagedThreadId;
                var r = (_slices, _ids.Count, _ids.Any(i => i != _tick));
                _slices = 0;
                _ids.Clear();
                return r;
            }
        }
    }
}
