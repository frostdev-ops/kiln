// SPDX-License-Identifier: MIT
// kiln.playeranim lab test, player free: the measuring half of mods/tests/player-anim-test (run.py is the other half, it judges). Each server
// start reads plan.json from this mod's folder (run.py writes it), defines the sequence copies it names, spawns a 1_Player (the game's player
// character, no client behind it) and plays sequences on it, sampling what the server's animation engine made of them: the state id the graph
// was given (ServerAnimationGraphInput+0x22c) and the model-space position of the slots HandR, HandL, FootL and FootR every 2 steps. It also
// logs what kiln.playeranim says (PlayerAnimStates) and the player's graph as kiln.assets serves it. With "graphAttempts" in the plan it
// tries to replace the player's graph from code, at load and while the 1_Player exists, and logs what each attempt got (scenario
// graph-override). Lines start with "pat: "; the last is "pat: done <scenario>".
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kiln;

namespace PlayerAnimTest;

public sealed class PlayerAnimTestMod : Mod
{
    private static readonly Guid PlayerGraph = Guid.Parse("21319e83-5087-4fff-8795-22997843649e");
    private const string GraphType = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0";
    private static readonly AssetKey GraphKey = new(PlayerGraph, GraphType);
    private static readonly WorldPosition Pos = new(3865.8, 833.0, 1433.8);
    private static readonly int[] Slots = [8, 9, 4, 5];   // HandR, HandL, FootL, FootR (animcook graphgen.SLOTS)

    private string _dir = "";
    private JsonObject _plan = new();
    private string _scenario = "?";
    private readonly List<EntityId> _mine = [];
    private readonly Dictionary<EntityId, string> _equipped = new();

    private void Info(string s) => Log.Info("pat: " + s);

    public override void OnLoad()
    {
        _dir = Context.Info.Directory;
        if (!Client.IsDedicatedServer) return;
        var planFile = Path.Combine(_dir, "plan.json");
        if (!File.Exists(planFile)) { Info("no plan.json: nothing to do"); return; }
        _plan = (JsonObject)JsonNode.Parse(File.ReadAllText(planFile))!;
        _scenario = (string?)_plan["scenario"] ?? "?";
        // the test-only requests of kiln.skills (selecting a hotbar slot on a player entity with no client) are off without this file
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");
        Report("load");
        if ((bool?)_plan["graphAttempts"] == true) GraphAttempts("at-load");
        if ((bool?)_plan["define"] == true)
        {
            // a definition from code: the service of this build does not take them yet (define_json answers unavailable)
            try
            {
                var r = PlayerAnimStates.Define(new PlayerAnimState("from-code", "0841b0b5-88cc-483f-9d24-35da62438649", "Attack_Sword_Shield_02"));
                Info($"define: {r.Mod}/{r.Name} status {r.Status}, ok {r.Ok}, reason {r.Reason ?? "-"}");
            }
            catch (Exception e) { Info($"define: threw {e.GetType().Name}: {e.Message}"); }
        }
        foreach (var c in (_plan["clones"] as JsonArray) ?? [])
        {
            var clone = new GameDataClone { Guid = Guid.Parse((string)c!["guid"]!), Name = (string)c["name"]!, Base = (string)c["base"]! };
            foreach (var (k, v) in c["set"]!.AsObject()) clone.Fields[k] = (string)v!;
            try
            {
                var d = Content.Sequences.Define(clone);
                Info($"clone {d.Name} {d.Guid} of {d.Base}: state {d.State}, error {d.Error ?? "-"}");
            }
            catch (Exception e) { Info($"clone {clone.Name} refused: {e.Message}"); }
        }
        _ = Run();
    }

    public override void OnUnload()
    {
        foreach (var e in _mine) try { if (Spawn.Exists(e)) Spawn.Destroy(e); } catch { }
        _mine.Clear();
    }

    /// <summary>What kiln.playeranim says now: the graph, its hash, the states the plan asks about, every mod state, game states by name.</summary>
    private void Report(string when)
    {
        Info($"{when}: service available {PlayerAnimStates.Available}, graph {PlayerAnimStates.Graph}, hash {PlayerAnimStates.GraphHash}");
        foreach (var l in (_plan["lookups"] as JsonArray) ?? [])
        {
            var mod = (string)l![0]!;
            var name = (string)l[1]!;
            var s = PlayerAnimStates.Find(mod, name);
            Info(s is null
                ? $"{when}: state {mod}/{name}: none (guid {PlayerAnimStates.GuidOf(mod, name)}, id 0x{PlayerAnimStates.IdOf(PlayerAnimStates.GuidOf(mod, name)):x8})"
                : $"{when}: state {s.Mod}/{s.Name}: status {s.Status}, id 0x{s.Id:x8}, guid {s.Guid}, kind {s.Kind}, machine {s.Machine}, like {s.Like}, reason {(string.IsNullOrEmpty(s.Reason) ? "-" : s.Reason)}");
        }
        var all = PlayerAnimStates.All;
        Info($"{when}: all {all.Count}: {string.Join(", ", all.Select(s => $"{s.Mod}/{s.Name}={s.Status}"))}");
        foreach (var n in (_plan["gameStates"] as JsonArray) ?? [])
        {
            var g = PlayerAnimStates.FindGameStates((string)n!);
            Info($"{when}: game state {n}: {string.Join("; ", g.Select(x => $"{x.Machine}/{x.Name} {x.Kind} [{string.Join(",", x.Ids.Select(i => $"0x{i:x8}"))}]"))}");
        }
    }

    private void Served(string prefix)
    {
        var g = Assets.ReadResource(GraphKey);
        Info($"{prefix}player graph as kiln.assets reads it: {(g is null ? "none" : $"{g.Length} B, sha256 {Sha(g)}")}");
    }

    /// <summary>The capability row anim.player_states as kiln/state/capabilities.json has it now.</summary>
    private void Capability(string when)
    {
        var file = Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "..", "state", "capabilities.json"));
        try
        {
            var doc = JsonNode.Parse(File.ReadAllText(file))!;
            var row = (doc["capabilities"] as JsonArray)?.FirstOrDefault(r => (string?)r!["name"] == "anim.player_states");
            Info($"{when}: capability anim.player_states: {(row is null ? "none" : $"{row["state"]} | {row["reasonText"]}")}");
        }
        catch (Exception e) { Info($"{when}: capability anim.player_states: {file} unreadable: {e.Message}"); }
    }

    /// <summary>Every way a mod's code has to replace the player's graph: Assets.Override and Assets.Register (refused in Kiln.Assets),
    /// a field override through ReflectedResource (around that check: Kiln.Api puts the whole resource natively, as kiln.overrides), and
    /// Assets.RemoveResource. The bytes put are the ones served at that moment: a put that got through with the player there would not change them.</summary>
    private void GraphAttempts(string when)
    {
        var bytes = Assets.ReadResource(GraphKey) ?? [];
        Attempt($"{when}: Assets.Override", () => Assets.Override(GraphKey, bytes));
        Attempt($"{when}: Assets.Register", () => Assets.Register(GraphKey, bytes));
        try
        {
            var r = Assets.Reflect(GraphKey);
            if (r is null) Info($"{when}: reflected set: no reflected resource");
            else
            {
                var v = r["numGenericFeetIKCount"];
                var res = r.Set("numGenericFeetIKCount", v);
                Info($"{when}: reflected set numGenericFeetIKCount = {v}: ok {res.Ok}, status {res.Status}, message {res.Message}");
            }
        }
        catch (Exception e) { Info($"{when}: reflected set: threw {e.GetType().Name}: {e.Message}"); }
        Attempt($"{when}: Assets.RemoveResource", () => Assets.RemoveResource(GraphKey));
    }

    private void Attempt(string what, Action a)
    {
        try { a(); Info($"{what}: returned"); }
        catch (Exception e) { Info($"{what}: threw {e.GetType().Name}: {e.Message}"); }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..16].ToLowerInvariant();
    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }
    private static Guid SeqOf(string s) => Guid.TryParse(s, out var g) ? g : Content.Sequences.Get(s)?.Guid ?? Guid.Empty;

    private static string Slot(byte[]? sl, int slot)
    {
        if (sl is null) return "nan,nan,nan";
        var o = 0x20 + slot * 48;
        return $"{BitConverter.ToSingle(sl, o + 36):F3},{BitConverter.ToSingle(sl, o + 40):F3},{BitConverter.ToSingle(sl, o + 44):F3}";
    }

    private async Task Equip(EntityId p, string item)
    {
        if (_equipped.TryGetValue(p, out var have) && have == item) return;
        var data = Content.ItemData.Get(item);
        if (data is null) { Info($"equip {item}: no such item"); return; }
        var slot = Items.Read(p)?.Slots.FirstOrDefault(s => !s.IsEmpty && s.Item.Value == data.Id);
        if (slot is null)
        {
            await Items.Give(p, item, 1);
            await Steps(20);
            slot = Items.Read(p)?.Slots.FirstOrDefault(s => !s.IsEmpty && s.Item.Value == data.Id);
        }
        var ok = slot is not null && GameTree.GameRequest(p, 5, (uint)slot.Slot.Index);
        await Steps(30);
        _equipped[p] = item;
        Info($"equip {item} on {p}: hotbar slot {slot?.Slot.Index}, selected {ok}");
    }

    /// <summary>Plays one sequence and logs, every 2 steps: step, milliseconds since the start, the state id, and the four slots.</summary>
    private async Task Play(EntityId p, string who, JsonNode c)
    {
        var label = (string)c["label"]!;
        if ((string?)c["equip"] is { } item) await Equip(p, item);
        var seq = SeqOf((string)c["seq"]!);
        var steps = (int?)c["steps"] ?? 100;
        var seqId = Abilities.IdOf(seq);
        var watch = Stopwatch.StartNew();
        var ok = seq != Guid.Empty && Abilities.PlaySequence(p, seq, SequenceOptions.Default);
        var line = new StringBuilder();
        var playing = 0;
        for (var i = 0; i <= steps; i += 2)
        {
            var inp = World.ReadBytes(p, "ServerAnimationGraphInput");
            var sl = World.ReadBytes(p, "ServerAnimationGraphSlotComponent");
            var st = inp is { Length: >= 0x230 } ? BitConverter.ToUInt32(inp, 0x22c) : 0;
            var na = World.ReadBytes(p, "NetworkActor");
            if (na is not null && BitConverter.ToUInt32(na, 0) == seqId) playing++;
            line.Append($"{i}:{watch.ElapsedMilliseconds}:{st:x8}");
            foreach (var s in Slots) line.Append(':').Append(Slot(sl, s));
            line.Append(' ');
            await Steps(2);
        }
        Info($"play {who} {label} seq {seq} (0x{seqId:x8}) started {ok}, sequence seen in {playing} samples | {line}");
        await Steps((int?)c["after"] ?? 40);
    }

    private async Task<EntityId> Player(double dx)
    {
        var p = await Spawn.Entity("1_Player", new WorldPosition(Pos.X + dx, Pos.Y, Pos.Z), 0, new SpawnOptions { Tag = "player-anim-test", Flags = SpawnFlags.Transient });
        _mine.Add(p);
        await Steps(30);
        var h = World.ReadBytes(p, "ServerAnimationGraphComponent2");
        Info($"1_Player {p}: graph handle {(h is null ? "none" : BitConverter.ToInt32(h, 0).ToString())}");
        return p;
    }

    private async Task Run()
    {
        try
        {
            var t0 = DateTime.UtcNow;
            while (!Spawn.WorldRunning && (DateTime.UtcNow - t0).TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            Report("running");
            Served("");
            if ((bool?)_plan["noPlayer"] == true) { Info("no player in this run"); return; }
            var p = await Player(0);
            foreach (var c in (_plan["plays"] as JsonArray) ?? []) await Play(p, "p1", c!);
            if ((bool?)_plan["graphAttempts"] == true)
            {
                // the 1_Player's graph is live now: a replacement of it would end the server (kiln.assets refuses it)
                Capability("before");
                GraphAttempts("with-player");
                var t3 = DateTime.UtcNow;
                while ((DateTime.UtcNow - t3).TotalSeconds < 3) await Steps(10);  // capabilities.json is written at most once a second
                Report("after-attempts");
                Served("after-attempts: ");
                Capability("after");
                foreach (var c in (_plan["playsAfterAttempts"] as JsonArray) ?? []) await Play(p, "p1", c!);
            }
            if ((string?)_plan["pause"] is { } pause)
            {
                // run.py does something to the running server now (a mod's unload or reload through kiln/control) and then writes go-<pause>
                var go = Path.Combine(_dir, "go-" + pause);
                Info($"paused {pause}: waiting for {go}");
                var t1 = DateTime.UtcNow;
                while (!File.Exists(go) && (DateTime.UtcNow - t1).TotalSeconds < 120) await Steps(10);
                Info($"go {pause}: {File.Exists(go)}");
                Report("after-" + pause);
                foreach (var c in (_plan["playsAfter"] as JsonArray) ?? []) await Play(p, "p1", c!);
            }
            if (_plan["second"] is JsonArray second && second.Count > 0)
            {
                await Steps(100);
                var q = await Player(4);
                foreach (var label in second)
                {
                    var c = (_plan["plays"] as JsonArray)?.FirstOrDefault(x => (string)x!["label"]! == (string)label!);
                    if (c is not null) await Play(q, "p2", c);
                }
            }
            var up = (int?)_plan["upSeconds"] ?? 0;
            if (up > 0)
            {
                Info($"test over; staying up {up} s");
                var t2 = DateTime.UtcNow;
                while ((DateTime.UtcNow - t2).TotalSeconds < up) await Steps(20);
                Info($"up {up} s after the test; 1_Player exists {Spawn.Exists(p)}");
            }
        }
        catch (Exception e) { Info("threw: " + e); }
        finally { OnUnload(); Info($"done {_scenario}"); }
    }
}
