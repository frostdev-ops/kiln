// SPDX-License-Identifier: MIT
// A weapon's moves (Kiln.Blocks, docs/kiln/modding/item-blocks.md "Moves") on a dedicated server, read back from the running game's data and played on a
// spawned player character next to a dummy. blocks/weapons.json has the weapons; chain-sword-cs below is chain-sword in C#. What is expected is worked out
// here from the game's own (original) values and the numbers the blocks give, not from what the moves code wrote: a clone's GUID by the derive rule, the
// retime by the formula of the docs, the slots by their selectors. Logs "weapon-moves-test: PASS ..." / "FAIL ..." and "weapon-moves-test done: N PASS, M FAIL".
// Start it twice: the second run checks the ids and the slots across the restart. A file named fail-once in the mod's data folder turns one expectation of
// each group wrong, so that each group is seen to fail. Started with KILN_PLAYERSTATE_LAB=1 it also starts a swing with the weapon as the used item.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;
using Kiln.Testing;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod : Mod
{
    private const string Me = "weapon-moves-test";

    // the game's sequences the blocks start from, by sub-sequence name
    private const string Light1 = "Attack_Player_1H_Single_LightChain1", Light2 = "Attack_Player_1H_Single_LightChain2";
    private const string SwordHeavy = "Attack_Player_Melee_Heavy_Sword_Diagonal", AxeHeavy = "Attack_Player_Melee_Heavy_2H_Axe_01";
    private const string GameBlock = "Ability_Block", WandBolt = "Attack_Player_Wand_IceChain_normal";
    private const string SwordStarter = "Weapon_T1_1H_Sword_Craft", GameSword = "Weapon_T1_1H_Sword_01", AxeStarter = "Weapon_T1_2H_GreatAxe_01", WandStarter = "Weapon_T1_1H_Wand_02_Ice";

    // the game states the moves name (another weapon family's), and the parry's pose by its id
    internal const string AxeSwing1 = "ad276dac-1bb6-4406-bdfc-c584edfd0f5d", AxeSwing2 = "6bc6f8fb-5ecb-4351-836f-4d5b50f72007";
    internal const string GreatswordHeavy = "30c8d4c5-5927-47ec-84b1-c00d32c7f35d", WandHeavyOverlay = "718fce5f-df4b-4dca-8ea6-ad69599134ce";
    internal const uint ParryPose = 0xfeec6023;

    /// <summary>One move as the blocks wrote it: the block, the clone's suffix, the sequence it starts from, the state, the timing.</summary>
    internal sealed record Case(string Block, string Suffix, string Like, Guid? State, uint? StateId, double? HitAt, double? Length, bool AtReady);

    internal static readonly Case[] Cases =
    [
        new("chain-sword", "primary.1", Light1, Guid.Parse(AxeSwing1), null, 0.40, null, false),
        new("chain-sword", "primary.2", Light2, Guid.Parse(AxeSwing2), null, 0.45, null, false),
        new("chain-sword", "heavy", SwordHeavy, Guid.Parse(GreatswordHeavy), null, 0.60, 1.80, false),
        new("chain-sword", "block", GameBlock, null, ParryPose, null, null, true),
        new("chain-sword-cs", "primary.1", Light1, Guid.Parse(AxeSwing1), null, 0.40, null, false),
        new("chain-sword-cs", "primary.2", Light2, Guid.Parse(AxeSwing2), null, 0.45, null, false),
        new("chain-sword-cs", "heavy", SwordHeavy, Guid.Parse(GreatswordHeavy), null, 0.60, 1.80, false),
        new("chain-sword-cs", "block", GameBlock, null, ParryPose, null, null, true),
        new("heavy-greataxe", "heavy", AxeHeavy, null, null, 1.40, null, true),
    ];

    private int _pass, _fail;
    private bool _wrong;   // fail-once: one expectation of each group is wrong
    private readonly List<EntityId> _spawned = new();
    private readonly List<(ulong Step, EntityId Target, EntityId Attacker, uint Impact, int Amount)> _hurts = new();
    private readonly List<(ulong Step, EntityId Target, EntityId Attacker, int Amount)> _damaged = new();
    private readonly List<(ulong Step, EntityId Root, uint Program, string? Template)> _impacts = new();
    private readonly Dictionary<ulong, uint> _clock = new();
    private ulong _step;
    private Watch? _watch;

    private sealed class Watch(EntityId actor)
    {
        public readonly EntityId Actor = actor;
        public uint LastSeq;
        public long LastTime;
        public readonly List<(ulong Step, uint Sequence, long TriggerTime)> Seen = new();
    }

    private void Info(string s) => Log.Info($"{Me}: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"{Me}: PASS {what}"); }
        else { _fail++; Log.Error($"{Me}: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static string G(string block) => $"{Me}.{block}";
    internal static Guid CloneGuid(string block, string suffix) => BlockIds.Derive(Me, "sequence", $"{block}.{suffix}");
    internal static uint Fnv(Guid g)
    {
        var h = 0x811c9dc5u;
        foreach (var c in g.ToByteArray()) h = (h ^ c) * 0x1000193u;
        return h;
    }

    public override void OnLoad()
    {
        // chain-sword of blocks/weapons.json, in C#
        Blocks.Define("chain-sword-cs", new WeaponBlock
        {
            Name = "Chain Sword", Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare },
            Moves = new WeaponMoves
            {
                Primary = new[]
                {
                    new MoveBlock { Animation = AxeSwing1, Like = Light1, HitAt = 0.40 },
                    new MoveBlock { Animation = AxeSwing2, Like = Light2, HitAt = 0.45 },
                },
                Heavy = new MoveBlock { Animation = GreatswordHeavy, Like = SwordHeavy, HitAt = 0.60, Length = 1.80 },
                Block = new MoveBlock { Animation = "0xfeec6023" },
            },
        });
        // a clone of the bolt that only the reference checks below write to
        Content.Sequences.Define(new GameDataClone { Guid = BlockIds.Derive(Me, "sequence", "probe"), Name = G("probe"), Base = WandBolt });
        var found = ClientChecks.Register(this);
        ListChecks(found);
        if (!Client.IsDedicatedServer) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        Directory.CreateDirectory(Context.DataDirectory);
        _wrong = File.Exists(Path.Combine(Context.DataDirectory, "fail-once"));
        Context.Tick.Pre += OnPre;
        Events.On<EntityHurtEvent>(e => _hurts.Add((e.Step, e.Target, e.Attacker, e.ImpactId, e.OriginalAmount)));
        Events.On<EntityDamagedEvent>(e => _damaged.Add((e.Step, e.Target, e.Attacker, e.Amount)));
        Events.On<ImpactStartedEvent>(e => _impacts.Add((e.Step, e.Impact.Root, e.Impact.ProgramId, e.TemplateName)));
        _ = Run();
    }

    // What Register made of this mod's checks: the runner's list, read where it keeps it (nothing runs them before proof's runner lands).
    private void ListChecks(int found)
    {
        var list = new List<string>();
        try
        {
            var registry = typeof(ClientChecks).Assembly.GetType("Kiln.Testing.CheckRegistry");
            if (registry?.GetProperty("All", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)?.GetValue(null) is System.Collections.IEnumerable all)
                foreach (var c in all)
                {
                    var t = c.GetType();
                    if ((string?)t.GetProperty("Mod")?.GetValue(c) != Me) continue;
                    var problem = (string?)t.GetProperty("Problem")?.GetValue(c);
                    var tags = (IEnumerable<string>?)t.GetProperty("Tags")?.GetValue(c) ?? [];
                    list.Add($"{t.GetProperty("Id")?.GetValue(c)} \"{t.GetProperty("Title")?.GetValue(c)}\" [{string.Join(",", tags)}] needs {t.GetProperty("Needs")?.GetValue(c)} order {t.GetProperty("Order")?.GetValue(c)}: {(problem == null ? "listed" : "REFUSED: " + problem)}");
                }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Reflection.TargetInvocationException or InvalidCastException) { list.Add($"(the list could not be read: {e.Message})"); }
        Log.Info($"{Me}: client checks: Register found {found}; {string.Join("; ", list)}");
    }

    private void OnPre(in TickInfo t)
    {
        _step = t.Step;
        _clock[t.Step] = t.Clock;
        if (_watch is not { } w || Abilities.SequenceOf(w.Actor) is not { } s || s.Sequence == 0) return;
        if (s.Sequence == w.LastSeq && s.TriggerTime == w.LastTime) return;
        w.LastSeq = s.Sequence;
        w.LastTime = s.TriggerTime;
        w.Seen.Add((t.Step - 1, s.Sequence, s.TriggerTime));   // seen in this Pre: started in the step before
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(150);
            if (_wrong) Info("fail-once is in the data folder: one expectation of each group is wrong on purpose");
            await Part("report", () => { Report(); return Task.CompletedTask; });
            await Part("clones", () => { Clones(); return Task.CompletedTask; });
            await Part("slots", () => { SlotChecks(); return Task.CompletedTask; });
            await Part("untouched", () => { Untouched(); return Task.CompletedTask; });
            await Part("retime", () => { Retime(); return Task.CompletedTask; });
            await Part("chain data", () => { ChainData(); return Task.CompletedTask; });
            await Part("wand", () => { Wand(); return Task.CompletedTask; });
            await Part("twin", () => { Twin(); return Task.CompletedTask; });
            await Part("held models", () => { HeldChecks(); return Task.CompletedTask; });
            await Part("item models", ItemModels);
            await Part("the sample", () => { SampleLoads(); return Task.CompletedTask; });
            await Part("ids", () => { Ids(); return Task.CompletedTask; });
            await Part("in the world", World_);
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        _watch = null;
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Part(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name}: exception: {e}"); }
    }

    // ---- reading the game's data ------------------------------------------------------------------------------

    /// <summary>One event of a sub-sequence, in the current order (kiln.gamedata keeps them sorted), with its original values.</summary>
    internal sealed record Ev(int Index, string Type, int K, double Time, double Orig, double Duration, double OrigDuration);

    private static double Val(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

    internal static List<Ev> EventsOf(GameDataEntry s)
    {
        var all = s.GetJson("events[*]") as JsonArray ?? [];
        var times = s.GetJson("events[*].time") as JsonArray ?? [];
        var durations = s.GetJson("events[*].duration") as JsonArray ?? [];
        var seen = new Dictionary<string, int>();
        var list = new List<Ev>();
        for (var i = 0; i < all.Count; i++)
        {
            var type = ((string?)all[i]?["type"] ?? "?").Split("::")[^1];
            var k = seen.GetValueOrDefault(type);
            seen[type] = k + 1;
            var t = i < times.Count ? times[i] : null;
            var d = i < durations.Count ? durations[i] : null;
            list.Add(new Ev(i, type, k, Val(t, "value"), Val(t, "original"), Val(d, "value"), Val(d, "original")));
        }
        return list;
    }

    /// <summary>A sequence's SetActionOportunity events: the input, the follow-up now and originally, the follow-up time now and originally.</summary>
    internal static List<(int K, string Trigger, Guid FollowUp, Guid Orig, double Time, double OrigTime)> Openings(GameDataEntry s)
    {
        var r = new List<(int, string, Guid, Guid, double, double)>();
        for (var k = 0; k < 32; k++)
        {
            var p = $"events[SetActionOportunity#{k}]";
            try { r.Add((k, s.Get($"{p}.triggeredBy").Name ?? "", s.Get($"{p}.followUp").Guid, s.Original($"{p}.followUp").Guid, s.Get($"{p}.followUpTime").Number, s.Original($"{p}.followUpTime").AsDouble)); }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { break; }
        }
        return r;
    }

    /// <summary>One entry of an item's sequences: what starts it, now and originally.</summary>
    internal sealed record SlotRow(int Index, string Trigger, string[] Requirements, string Special, Guid Sequence, Guid Orig);

    private static string[] Flags(JsonNode? n)
    {
        if (n is JsonObject o && o.ContainsKey("value")) n = o["value"];
        IEnumerable<string> raw = n switch
        {
            JsonArray a => a.Select(x => x is JsonObject xo && xo["value"] is { } v ? v.ToString() : x?.ToString() ?? ""),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', '|', ' '], StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };
        return raw.Select(x => x.Trim()).Where(x => x.Length > 0 && x != "None").Order(StringComparer.Ordinal).ToArray();
    }

    internal static List<SlotRow> Slots(GameDataEntry item)
    {
        var r = new List<SlotRow>();
        for (var i = 0; i < 32; i++)
        {
            var p = $"sequences[{i}]";
            try
            {
                r.Add(new SlotRow(i, item.Get($"{p}.triggeredBy").Name ?? "", Flags(item.GetJson($"{p}.requirements")), item.Get($"{p}.specialInteractionType").Name ?? "Invalid",
                    item.Get($"{p}.sequence").Guid, item.Original($"{p}.sequence").Guid));
            }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { break; }
        }
        return r;
    }

    // the selectors of the slots the blocks move: what starts each (docs: "Slots")
    private static readonly (string Slot, string Trigger, string[] Requirements)[] Selectors =
        [("primary", "Primary", []), ("heavy", "Heavy", ["IsGrounded"]), ("heavyAir", "Heavy", ["IsAirborne"]), ("block", "Block", [])];

    internal static int[] Where(List<SlotRow> rows, string slot)
    {
        var (_, trigger, req) = Selectors.First(s => s.Slot == slot);
        return rows.Where(r => r.Trigger == trigger && r.Special is "Invalid" or "" && r.Requirements.SequenceEqual(req.Order(StringComparer.Ordinal))).Select(r => r.Index).ToArray();
    }

    // the motion's first state: its full-body one, or its overlay (a wand's)
    private static Guid StateOf(GameDataEntry s, bool original = false)
    {
        foreach (var path in new[] { "events[SetAnimationEvent#0].animationName", "events[SetAnimationOverlayEvent#0].overlayName" })
            try { return original ? s.Original(path).Guid : s.Get(path).Guid; }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { }
        return Guid.Empty;
    }

    private static Guid OriginalStateOf(GameDataEntry s) => StateOf(s, original: true);

    private static GameDataEntry Seq(string name) => Content.Sequences.Get(name) ?? throw new InvalidOperationException($"no sequence {name}");

    private static string Show(List<SlotRow> rows) => string.Join(" ", rows.Select(r => $"[{r.Index} {r.Trigger}{(r.Requirements.Length > 0 ? "/" + string.Join("+", r.Requirements) : "")}{(r.Special is "Invalid" or "" ? "" : "/" + r.Special)} {Content.Sequences.Get(r.Sequence.ToString())?.Name ?? r.Sequence.ToString()}]"));

    // ---- what the block decided ---------------------------------------------------------------------------------

    private void Report()
    {
        var mine = Blocks.Report().GetValueOrDefault(Me);
        var errors = (mine.Errors ?? []).Distinct().ToList();
        var overlay = errors.Where(e => e.Contains("overlayName", StringComparison.Ordinal)).ToList();
        if (_wrong) overlay.Add("(fail-once)");
        // kiln.content's item path (native/services/content/refs.cpp) refuses an item's model field naming a model this machine does not serve: on a
        // machine that did not cook, the look's rigid model of the mod. A defect outside the moves and the held look, routed to the coordinator; its own line
        var itemModels = errors.Where(e => e.Contains("is not a keen::Model the game has loaded", StringComparison.Ordinal)).ToList();
        Check(errors.Except(itemModels).Count() == 0, $"the blocks came through with no word from the services, content ready's moves included: [{string.Join(" | ", errors.Except(itemModels))}]");
        Check(itemModels.Count == 0, $"kiln.content's item path takes the look's rigid model (a DEFECT of kiln.content, not of the moves or the held look, when this fails: it refuses an item's model field naming a model this machine does not serve, refs.cpp): [{string.Join(" | ", itemModels)}]");
        Check(overlay.Count == 0, $"an overlay named by a wand's move is written (kiln.content takes an OverlayId as it takes a StateId): [{string.Join(" | ", overlay)}]");
        Refusals();
        Info($"warnings: [{string.Join(" | ", Blocks.Warnings(Me))}]");
        foreach (var b in new[] { "chain-sword", "heavy-greataxe", "moves-wand" })
        {
            var low = JsonNode.Parse(Blocks.Lowered(b, Me) ?? "{}")!;
            Info($"{b}: moves {low["moves"]?.ToJsonString()}");
            Info($"{b}: movesReady {low["movesReady"]?.ToJsonString()}");
            Check(low["movesReady"] is JsonObject { Count: > 0 }, $"{b}: content ready's half ran on the game's data: movesReady has {(low["movesReady"] as JsonObject)?.Count ?? 0} part(s)");
        }
    }

    // What kiln.content still refuses beside the overlay it takes: a reference the game loads as a resource, to one it does not have.
    private void Refusals()
    {
        var probe = G("probe");
        var made = Guid.NewGuid().ToString();
        var template = Content.Apply("{\"format\":1,\"sequences\":{\"" + probe + "\":{\"events[SpawnEntityEvent#0].templateReference\":\"" + made + "\"}}}", $"{Me}: a missing template");
        Check(template.Errors.Count > 0 && Seq(probe).Get("events[SpawnEntityEvent#0].templateReference").Guid != Guid.Parse(made),
            $"a reference to a template the game does not have is still refused: [{string.Join(" | ", template.Errors)}]");
        var overlay = Content.Apply("{\"format\":1,\"sequences\":{\"" + probe + "\":{\"events[SetAnimationOverlayEvent#0].overlayName\":\"" + made + "\"}}}", $"{Me}: a new overlay");
        var took = Seq(probe).Get("events[SetAnimationOverlayEvent#0].overlayName").Guid;
        if (_wrong) took = Guid.Empty;
        Check(overlay.Errors.Count == 0 && took == Guid.Parse(made), $"an overlay name no resource has is taken, as a state's is (only hashed): {took} [{string.Join(" | ", overlay.Errors)}]");
    }

    // ---- 1. clones --------------------------------------------------------------------------------------------

    private void Clones()
    {
        foreach (var c in Cases)
        {
            var name = $"{G(c.Block)}.{c.Suffix}";
            var clone = Content.Sequences.Get(name);
            var want = CloneGuid(c.Block, c.Suffix);
            if (_wrong && c == Cases[0]) want = BlockIds.Derive(Me, "sequence", $"{c.Block}.{c.Suffix}.x");
            if (!Check(clone != null && clone.Guid == want && clone.IsClone, $"{name}: a clone under its name, GUID {clone?.Guid} by derive(mod, \"sequence\", \"{c.Block}.{c.Suffix}\") = {want}")) continue;
            // it starts from its like: the same events in the same order, the like's own values as its originals
            var like = Seq(c.Like);
            var mineEv = EventsOf(clone!);
            var theirs = EventsOf(like);
            var same = mineEv.Count == theirs.Count && mineEv.Zip(theirs).All(p => p.First.Type == p.Second.Type && Math.Abs(p.First.Orig - p.Second.Orig) < 1e-6)
                       && Math.Abs(clone!.Original("length").AsDouble - like.Original("length").AsDouble) < 1e-6;
            Check(same, $"{name}: starts from {c.Like}: {mineEv.Count} events of the same types in the same order as its {theirs.Count}, its original times and length {clone!.Original("length").AsDouble:0.###} s");
            // and names the state it was given
            var state = clone.Get("events[SetAnimationEvent#0].animationName").Guid;
            var given = c.State ?? Guid.Empty;
            if (c.StateId is { } id)
                Check(Fnv(state) == id && state != like.Get("events[SetAnimationEvent#0].animationName").Guid, $"{name}: plays the state 0x{id:x8}: its SetAnimationEvent names {state}, whose FNV-1a is 0x{Fnv(state):x8} (the like's: {like.Get("events[SetAnimationEvent#0].animationName").Guid})");
            else if (c.State != null)
                Check(state == given, $"{name}: plays the state it was given: {state} (0x{Fnv(state):x8}); the like's {like.Get("events[SetAnimationEvent#0].animationName").Guid}");
            else
                Check(state == like.Get("events[SetAnimationEvent#0].animationName").Guid, $"{name}: no animation given, it plays its like's state {state}");
        }
    }

    // ---- 2. slots ------------------------------------------------------------------------------------------------

    private void SlotChecks()
    {
        void Weapon(string block, string starter, Dictionary<string, string> moved)
        {
            var item = Content.ItemData.Get(G(block)) ?? throw new InvalidOperationException($"no item {block}");
            var mine = Slots(item);
            var theirs = Slots(Content.ItemData.Get(starter)!);
            Info($"{block}: {Show(mine)}");
            var expect = theirs.Select(r => r.Sequence).ToArray();
            foreach (var (slot, suffix) in moved)
                foreach (var i in Where(theirs, slot)) expect[i] = CloneGuid(block, suffix);
            if (_wrong && block == "chain-sword") expect[Where(theirs, "heavyAir")[0]] = theirs[Where(theirs, "heavyAir")[0]].Sequence;
            var at = string.Join(", ", moved.Select(m => $"{m.Key} at [{string.Join(",", Where(theirs, m.Key))}]"));
            Check(mine.Count == theirs.Count && Where(theirs, "primary").Length > 0, $"{block}: the slots of its class's starter {starter}: {mine.Count} of {theirs.Count}; {at}");
            for (var i = 0; i < Math.Min(mine.Count, theirs.Count); i++)
            {
                var what = moved.FirstOrDefault(m => Where(theirs, m.Key).Contains(i)).Key;
                Check(mine[i].Sequence == expect[i] && mine[i].Trigger == theirs[i].Trigger && mine[i].Requirements.SequenceEqual(theirs[i].Requirements),
                    $"{block}: slot {i} ({theirs[i].Trigger}{(theirs[i].Requirements.Length > 0 ? " " + string.Join("+", theirs[i].Requirements) : "")}) holds {(what != null ? $"its {what} clone" : "the starter's")}: {Content.Sequences.Get(mine[i].Sequence.ToString())?.Name ?? mine[i].Sequence.ToString()}");
            }
        }
        var chain = new Dictionary<string, string> { ["primary"] = "primary.1", ["heavyAir"] = "primary.1", ["heavy"] = "heavy", ["block"] = "block" };
        Weapon("chain-sword", SwordStarter, chain);
        Weapon("chain-sword-cs", SwordStarter, chain);
        Weapon("heavy-greataxe", AxeStarter, new() { ["heavy"] = "heavy" });
        Weapon("plain-sword", SwordStarter, new());
    }

    // the control: the game's weapons and sequences are what they were
    private void Untouched()
    {
        foreach (var item in new[] { SwordStarter, GameSword, AxeStarter, WandStarter })
        {
            var rows = Slots(Content.ItemData.Get(item)!);
            Check(rows.Count > 0 && rows.All(r => r.Sequence == r.Orig), $"the game's {item} holds its own sequences: {rows.Count} slots, none changed");
        }
        var mineAll = Cases.Select(c => CloneGuid(c.Block, c.Suffix)).ToHashSet();
        var sword = Slots(Content.ItemData.Get(GameSword)!);
        Check(sword.Count > 0 && !sword.Any(r => mineAll.Contains(r.Sequence)), $"the game's own sword {GameSword} plays none of this mod's moves: {Show(sword)}");
        foreach (var name in new[] { Light1, Light2, SwordHeavy, AxeHeavy, GameBlock, WandBolt })
        {
            var s = Seq(name);
            var ev = EventsOf(s);
            var moved = ev.Where(e => Math.Abs(e.Time - e.Orig) > 1e-9 || (!double.IsNaN(e.Duration) && Math.Abs(e.Duration - e.OrigDuration) > 1e-9)).ToList();
            var opens = Openings(s);
            var wrong = _wrong && name == Light1 ? 1 : 0;
            Check(moved.Count == wrong && Math.Abs(s.Get("length").Number - s.Original("length").AsDouble) < 1e-9 && opens.All(o => o.FollowUp == o.Orig && Math.Abs(o.Time - o.OrigTime) < 1e-9)
                  && StateOf(s) == OriginalStateOf(s),
                $"the game's {name} keeps the game's times: {ev.Count} events, none moved{(moved.Count > 0 ? $" ({string.Join(", ", moved.Select(m => $"{m.Type}#{m.K} {m.Orig:0.###}->{m.Time:0.###}"))})" : "")}, length {s.Get("length").Number:0.###} s, its follow-ups and state its own");
        }
    }

    // ---- 3. retime, as data -------------------------------------------------------------------------------------

    /// <summary>The docs' map: up to the first blow scaled onto hitAt, after it shifted, or scaled to end on length.</summary>
    internal static double Map(double t, double h0, double len0, double hit, double len)
    {
        if (t <= h0) return h0 <= 0 ? t : t * hit / h0;
        return hit + (t - h0) * (len - hit) / (len0 - h0);
    }

    private void Retime()
    {
        foreach (var c in Cases.Where(c => c.HitAt != null || c.Length != null))
        {
            var name = $"{G(c.Block)}.{c.Suffix}";
            var s = Seq(name);
            var ev = EventsOf(s);
            var hits = ev.Where(e => e.Type == "SpawnImpact").ToList();
            var h0 = hits.Min(e => e.Orig);
            var len0 = s.Original("length").AsDouble;
            var hit = c.HitAt ?? h0;
            var len = c.Length ?? hit + (len0 - h0);
            if (_wrong && c == Cases[0]) hit += 0.1;
            var first = hits.Min(e => e.Time);
            Check(Math.Abs(first - hit) < 1e-4 && hits.All(e => Math.Abs(e.Duration - e.OrigDuration) < 1e-6),
                $"{name}: the first damage event is at {first:0.####} s, hitAt {hit} (the game's {h0:0.###} s); the {hits.Count} damage windows keep their {hits[0].OrigDuration:0.###} s");
            Check(Math.Abs(s.Get("length").Number - len) < 1e-4, $"{name}: length {s.Get("length").Number:0.####} s, {(c.Length != null ? "its length" : "shifted with the blow")} {len:0.####} (the game's {len0:0.###})");
            var sorted = ev.Zip(ev.Skip(1)).All(p => p.First.Time <= p.Second.Time + 1e-9);
            var off = ev.Where(e => Math.Abs(e.Time - Map(e.Orig, h0, len0, hit, len)) > 1e-4).ToList();
            Check(sorted && off.Count == 0, $"{name}: all {ev.Count} events moved by the map and still in time order{(off.Count > 0 ? $"; off: {string.Join(", ", off.Select(e => $"{e.Type}#{e.K} {e.Orig:0.###}->{e.Time:0.####} (map {Map(e.Orig, h0, len0, hit, len):0.####})"))}" : "")}");
            var windows = ev.Where(e => e.Type != "SpawnImpact" && e.OrigDuration > 0).ToList();
            var badWindows = windows.Where(e => Math.Abs(e.Duration - (Map(e.Orig + e.OrigDuration, h0, len0, hit, len) - Map(e.Orig, h0, len0, hit, len))) > 1e-4).ToList();
            Check(badWindows.Count == 0, $"{name}: the ends of its {windows.Count} other window(s) moved with the map{(badWindows.Count > 0 ? $"; off: {string.Join(", ", badWindows.Select(e => $"{e.Type}#{e.K} {e.OrigDuration:0.###}->{e.Duration:0.###}"))}" : "")}");
            var opens = Openings(s).Where(o => o.OrigTime > 0).ToList();
            var badOpen = opens.Where(o => Math.Abs(o.Time - Map(o.OrigTime, h0, len0, hit, len)) > 1e-4).ToList();
            Check(opens.Count == 0 || badOpen.Count == 0, $"{name}: the follow-up times moved with it: {string.Join(", ", opens.Select(o => $"{o.Trigger} {o.OrigTime:0.###}->{o.Time:0.###}"))}");
        }
    }

    // ---- 4. chain, as data ------------------------------------------------------------------------------------------

    private void ChainData()
    {
        foreach (var block in new[] { "chain-sword", "chain-sword-cs" })
        {
            var one = Seq($"{G(block)}.primary.1");
            var two = Seq($"{G(block)}.primary.2");
            var heavy = Seq($"{G(block)}.heavy");
            void Links(GameDataEntry s, string label, Guid primary, Guid heavyTo, bool heavyKeepsOthers)
            {
                var o = Openings(s);
                var p = o.Where(x => x.Trigger == "Primary" && x.Orig != Guid.Empty).ToList();
                var h = o.Where(x => x.Trigger == "Heavy" && x.Orig != Guid.Empty).ToList();
                var want = _wrong && label == "primary.1" ? heavyTo : primary;
                if (p.Count > 0) Check(p.All(x => x.FollowUp == want), $"{block}.{label}: its {p.Count} Primary follow-up(s) name {Content.Sequences.Get(want.ToString())?.Name}: [{string.Join(", ", p.Select(x => Content.Sequences.Get(x.FollowUp.ToString())?.Name))}]");
                Check(h.Count > 0 && h.All(x => x.FollowUp == heavyTo || (heavyKeepsOthers && x.FollowUp == x.Orig)), $"{block}.{label}: its {h.Count} Heavy follow-up(s) name {Content.Sequences.Get(heavyTo.ToString())?.Name}: [{string.Join(", ", h.Select(x => Content.Sequences.Get(x.FollowUp.ToString())?.Name))}]");
                var none = o.Where(x => x.Orig == Guid.Empty).ToList();
                Check(none.All(x => x.FollowUp == Guid.Empty), $"{block}.{label}: its {none.Count} opening(s) with no follow-up (an evade) still have none");
            }
            Links(one, "primary.1", two.Guid, heavy.Guid, false);
            Links(two, "primary.2", one.Guid, heavy.Guid, false);
            // the game's heavy names itself: the heavy clone names itself; its Primary opening has no follow-up and keeps none
            Links(heavy, "heavy", one.Guid, heavy.Guid, false);
        }
        var axe = Seq($"{G("heavy-greataxe")}.heavy");
        var game = Seq(AxeHeavy);
        var opens = Openings(axe);
        Check(opens.All(o => o.Orig == game.Guid ? o.FollowUp == axe.Guid : o.FollowUp == o.Orig),
            $"heavy-greataxe.heavy: where the game's heavy names itself it names itself, the rest stay the game's: [{string.Join(", ", opens.Select(o => $"{o.Trigger}->{Content.Sequences.Get(o.FollowUp.ToString())?.Name ?? "-"}"))}]");
    }

    // ---- the wand ------------------------------------------------------------------------------------------------

    private void Wand()
    {
        var name = $"{G("moves-wand")}.primary";
        var s = Seq(name);
        var game = Seq(WandBolt);
        var item = Content.ItemData.Get(G("moves-wand"))!;
        Check(item.Get("sequences[0].sequence").Guid == s.Guid && s.IsClone, $"moves-wand: its primary slot plays its own {name} ({s.Guid})");
        var overlay = s.Get("events[SetAnimationOverlayEvent#0].overlayName").Guid;
        Check(s.Get("events[SetAnimationOverlayEvent#1].overlayName").Guid == game.Get("events[SetAnimationOverlayEvent#1].overlayName").Guid, "moves-wand: the bolt's second overlay is the bolt's");
        Check(overlay == Guid.Parse(WandHeavyOverlay), $"moves-wand: a GUID with no kind goes to the motion's overlay, the first one: {overlay}, given {WandHeavyOverlay}");
        // the firing shots (the bolt's first two): the first lands on hitAt, the rest follow by the map
        var want = _wrong ? 0.7 : 0.60;
        var shots = Enumerable.Range(0, 2).Select(i => (Now: s.Get($"events[SpawnEntityEvent#{i}].time").Number, Was: s.Original($"events[SpawnEntityEvent#{i}].time").AsDouble)).ToList();
        var ev = EventsOf(s);
        var h0 = shots.Min(x => x.Was);
        var len0 = s.Original("length").AsDouble;
        var len = 0.60 + (len0 - h0);
        var off = ev.Where(e => Math.Abs(e.Time - Map(e.Orig, h0, len0, 0.60, len)) > 1e-4).ToList();
        Check(Math.Abs(shots.Min(x => x.Now) - want) < 1e-4 && off.Count == 0 && ev.Zip(ev.Skip(1)).All(p => p.First.Time <= p.Second.Time + 1e-9),
            $"moves-wand: the bolt's first firing shot at hitAt {want}, the other events by the map: shots [{string.Join(", ", shots.Select(x => $"{x.Was:0.###}->{x.Now:0.####}"))}], length {s.Get("length").Number:0.###} s, the {ev.Count} events in time order{(off.Count > 0 ? $"; off: {string.Join(", ", off.Select(e => $"{e.Type}#{e.K}"))}" : "")}");
    }

    // ---- C# and JSON ---------------------------------------------------------------------------------------------

    private void Twin()
    {
        static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "chain-sword-cs", "chain-sword");
        var cs = Normal(Blocks.Lowered("chain-sword-cs", Me));
        var json = Normal(Blocks.Lowered("chain-sword", Me));
        if (_wrong) json += " ";
        Check(cs.Length > 40 && cs == json, $"chain-sword-cs (C#) lowers to what chain-sword (blocks/weapons.json) does, the moves and what content ready made of them included: {cs.Length} characters");
        if (cs != json) Info($"C#: {cs}\nJSON: {json}");
    }

    // ---- 6. ids and slots across a restart ------------------------------------------------------------------------

    private void Ids()
    {
        var now = new JsonObject();
        foreach (var c in Cases) now[$"{c.Block}.{c.Suffix}"] = Content.Sequences.Get($"{G(c.Block)}.{c.Suffix}")?.Guid.ToString();
        now["moves-wand.primary"] = Content.Sequences.Get($"{G("moves-wand")}.primary")?.Guid.ToString();
        foreach (var b in new[] { "chain-sword", "chain-sword-cs", "heavy-greataxe", "plain-sword", "moves-wand" })
        {
            var item = Content.ItemData.Get(G(b))!;
            now[b] = item.Guid.ToString();
            now[$"{b} slots"] = string.Join(",", Slots(item).Select(r => r.Sequence));
        }
        var file = Path.Combine(Context.DataDirectory, "ids.json");
        if (File.Exists(file))
        {
            var was = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var (k, v) in now)
            {
                var expected = _wrong && k == "chain-sword" ? Guid.Empty.ToString() : was[k]?.ToString();
                Check(expected == v?.ToString(), $"after the restart {k} is still {(k.EndsWith(" slots", StringComparison.Ordinal) ? $"the same {v?.ToString().Split(',').Length} sequences" : v?.ToString())}");
            }
            File.Delete(file);
            return;
        }
        File.WriteAllText(file, now.ToJsonString());
        Info("ids written: restart the lab for the second run");
    }
}
