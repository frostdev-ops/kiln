// SPDX-License-Identifier: MIT
// Kiln.Quests/Native: what a quest is in the game's own UI (docs/kiln/services/npcs.md). kiln-quests.dll built the resources at
// boot (native/services/quests/native_pack.cpp) and hands over a manifest; this mirrors the engine's quest state into the game's
// knowledge (the game's journal, notifications, markers and dialogue filters follow it), keeps the conditions a line's one
// knowledge check cannot express, runs the "do" of dialogue options, places NPCs and moves markers.
// The mirror is a reconciliation: what the knowledge should be follows from the engine's record, and it is written again whenever
// it differs. A change is mirrored at once, a join or a reset too, and every 2 s anything missed is put right, so nothing can leave
// the journal behind the engine for long.
using System.Text.Json;

namespace Kiln;

/// <summary>A step's knowledge. <paramref name="Count"/>: the counter its journal line shows, of <paramref name="Total"/> (0: none).
/// <paramref name="Ready"/>: a hand-in step: the player holds the items.</summary>
public sealed record NativeStepInfo(string Id, uint Visible, uint Active, uint Done, uint Count = 0, int Total = 0, uint Ready = 0);
public sealed record NativeQuestInfo(string Mod, string Id, string FullId, string Scope, uint EntryId, uint Started, uint Active, uint Done,
    IReadOnlyList<NativeStepInfo> Steps);
/// <summary>A line in the game's menu. <paramref name="Auto"/>: a topic Kiln made for a giver without a dialogue ("start:quest", "advance:quest.step").</summary>
public sealed record NativeLineInfo(string Mod, string Dialogue, string Npc, string Node, int Option, uint Line, string Auto = "");
public sealed record NativePlacement(QuestPlace Spec, float Yaw, bool Once, bool? Unlock);
public sealed record NativeNpcInfo(string Mod, string Id, string FullId, string Template, Guid Guid, bool IsClone, uint NpcId, bool Roster,
    uint Unlock, NativePlacement? Place, string Dialogue, string SummonItem);
public sealed record NativeMarkerInfo(string Mod, string Quest, string Step, string Template, Guid Guid, WorldPosition? At);
/// <summary>A knowledge object the engine keeps for what a line's one knowledge check cannot say. <paramref name="Kind"/>: "if" (an
/// option), "greeting", "available" (a quest with requirements).</summary>
public sealed record NativeComputed(string Mod, uint Id, string Kind, string Target, string Node, int Index, uint Gate, uint Heard);
public sealed record NativeModInfo(string Id, bool Ok, IReadOnlyDictionary<string, uint> Knowledge, IReadOnlyList<NativeQuestInfo> Quests,
    IReadOnlyList<NativeLineInfo> Lines, IReadOnlyList<NativeNpcInfo> Npcs, IReadOnlyList<NativeMarkerInfo> Markers, IReadOnlyList<string> Errors,
    IReadOnlyList<NativeComputed> Computed);
/// <summary>A knowledge value the engine's state asks for.</summary>
public readonly record struct NativeWanted(string Name, uint Id, uint Value);

/// <summary>What was built at boot from the mods' quests/*.json, and the run-time glue.</summary>
public static class NativeQuests
{
    private static IReadOnlyList<NativeModInfo>? _mods;
    private static string _raw = "";
    private static readonly Dictionary<string, NativeQuestInfo> ByQuest = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, NativeLineInfo> ByLine = [];
    private static readonly Dictionary<string, NativeMarkerInfo> MarkerOf = new(StringComparer.Ordinal);
    private static bool _started;
    private static ulong _mirrored, _lines, _placed;
    private static int _polls;

    /// <summary>The manifest as the service wrote it (JSON).</summary>
    public static string ManifestJson
    {
        get
        {
            Load();
            return _raw;
        }
    }

    /// <summary>Every mod with native data (a mod with errors is listed with Ok = false and built nothing).</summary>
    public static IReadOnlyList<NativeModInfo> Mods
    {
        get
        {
            Load();
            return _mods ?? [];
        }
    }

    /// <summary>Mirrored knowledge changes, dialogue options run, NPCs and markers placed.</summary>
    public static (ulong Mirrored, ulong Lines, ulong Placed) Counters => (_mirrored, _lines, _placed);

    public static NativeModInfo? Mod(string id) => Mods.FirstOrDefault(m => m.Id == id);
    public static NativeQuestInfo? Quest(string fullId)
    {
        Load();
        return ByQuest.GetValueOrDefault(fullId);
    }
    /// <summary>A mod's native object by name ("met_ilse", "q.lost_letter.active"); null when the build does not have it.</summary>
    public static uint? KnowledgeId(string mod, string name) => Mod(mod)?.Knowledge.TryGetValue(name, out var id) == true ? id : null;
    public static NativeNpcInfo? Npc(string fullId) => Mods.SelectMany(m => m.Npcs).FirstOrDefault(n => n.FullId == fullId);

    /// <summary>The game's journal shows this quest: it says native and the boot build made it.</summary>
    public static bool ShownByGame(QuestDefinition q) => q.Journal == QuestJournalMode.Native && Quest(q.FullId) is not null;

    private static uint Hex(JsonElement e) => e.ValueKind == JsonValueKind.String ? Convert.ToUInt32(e.GetString()!, 16) : e.GetUInt32();

    private static unsafe string? ReadManifest()
    {
        var t = KnowledgeNative.T;
        if (t == null || t->Header.Size < (uint)sizeof(KnowledgeNative.Table)) return null;
        nuint n = 0;
        t->NativeManifest(null, 0, &n);
        var buf = new byte[(int)n + 1];
        fixed (byte* p = buf)
            if (t->NativeManifest(p, (nuint)buf.Length, &n) != KnowledgeNative.Ok) return null;
        return System.Text.Encoding.UTF8.GetString(buf, 0, (int)n);
    }

    private static void Load()
    {
        if (_mods != null) return;
        if (ReadManifest() is not { } raw) return;
        _raw = raw;
        var mods = new List<NativeModInfo>();
        using var doc = JsonDocument.Parse(_raw);
        if (doc.RootElement.TryGetProperty("mods", out var ms))
            foreach (var m in ms.EnumerateArray())
            {
                var id = m.GetProperty("id").GetString() ?? "";
                var ok = m.TryGetProperty("ok", out var o) && o.GetBoolean();
                var errors = new List<string>();
                if (m.TryGetProperty("errors", out var es))
                    foreach (var e in es.EnumerateArray()) errors.Add(e.GetString() ?? "");
                var know = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                var quests = new List<NativeQuestInfo>();
                var lines = new List<NativeLineInfo>();
                var npcs = new List<NativeNpcInfo>();
                var markers = new List<NativeMarkerInfo>();
                var computed = new List<NativeComputed>();
                if (ok)
                {
                    foreach (var k in m.GetProperty("knowledge").EnumerateObject()) know[k.Name] = Hex(k.Value);
                    foreach (var q in m.GetProperty("quests").EnumerateArray())
                    {
                        var steps = new List<NativeStepInfo>();
                        foreach (var s in q.GetProperty("steps").EnumerateArray())
                            steps.Add(new NativeStepInfo(s.GetProperty("id").GetString()!, Hex(s.GetProperty("visible")), Hex(s.GetProperty("active")), Hex(s.GetProperty("done")),
                                s.TryGetProperty("count", out var cn) ? Hex(cn) : 0, s.TryGetProperty("total", out var tt) ? tt.GetInt32() : 0, s.TryGetProperty("ready", out var rd) ? Hex(rd) : 0));
                        var nq = new NativeQuestInfo(id, q.GetProperty("id").GetString()!, q.GetProperty("fullId").GetString()!, q.GetProperty("scope").GetString()!,
                            Hex(q.GetProperty("entryId")), Hex(q.GetProperty("started")), Hex(q.GetProperty("active")), Hex(q.GetProperty("done")), steps);
                        quests.Add(nq);
                        ByQuest[nq.FullId] = nq;
                    }
                    foreach (var l in m.GetProperty("lines").EnumerateArray())
                    {
                        var nl = new NativeLineInfo(id, l.GetProperty("dialogue").GetString()!, l.GetProperty("npc").GetString()!, l.GetProperty("node").GetString()!,
                            l.GetProperty("option").GetInt32(), Hex(l.GetProperty("line")), l.TryGetProperty("auto", out var au) ? au.GetString() ?? "" : "");
                        lines.Add(nl);
                        ByLine[nl.Line] = nl;
                    }
                    foreach (var c in m.GetProperty("npcs").EnumerateArray())
                    {
                        NativePlacement? place = null;
                        if (c.TryGetProperty("place", out var pl) && pl.ValueKind == JsonValueKind.Object)
                            place = new NativePlacement(Spec(pl), pl.TryGetProperty("yaw", out var y) ? y.GetSingle() : 0,
                                pl.TryGetProperty("once", out var once) && once.GetBoolean(), pl.TryGetProperty("unlock", out var ul) ? ul.GetBoolean() : null);
                        npcs.Add(new NativeNpcInfo(id, c.GetProperty("id").GetString()!, c.GetProperty("fullId").GetString()!, c.GetProperty("template").GetString()!,
                            Guid.Parse(c.GetProperty("guid").GetString()!), c.GetProperty("clone").GetBoolean(),
                            c.TryGetProperty("npcId", out var ni) ? Hex(ni) : 0, c.TryGetProperty("roster", out var r) && r.GetBoolean(),
                            c.TryGetProperty("unlock", out var u) ? Hex(u) : 0, place,
                            c.TryGetProperty("dialogue", out var dl) ? dl.GetString() ?? "" : "", c.TryGetProperty("summonItem", out var si) ? si.GetString() ?? "" : ""));
                    }
                    foreach (var k in m.GetProperty("markers").EnumerateArray())
                    {
                        var nk = new NativeMarkerInfo(id, k.GetProperty("quest").GetString()!, k.GetProperty("step").GetString()!, k.GetProperty("template").GetString()!,
                            Guid.Parse(k.GetProperty("guid").GetString()!), k.TryGetProperty("at", out var ka) && ka.ValueKind == JsonValueKind.Array ? Pos(ka) : null);
                        markers.Add(nk);
                        MarkerOf[$"{id}:{nk.Quest}.{nk.Step}"] = nk;
                    }
                    if (m.TryGetProperty("computed", out var cs))
                        foreach (var c in cs.EnumerateArray())
                            computed.Add(new NativeComputed(id, Hex(c.GetProperty("id")), c.GetProperty("kind").GetString()!, c.GetProperty("target").GetString()!,
                                c.TryGetProperty("node", out var cn) ? cn.GetString() ?? "" : "", c.TryGetProperty("index", out var ci) ? ci.GetInt32() : -1,
                                c.TryGetProperty("gate", out var cg) ? Hex(cg) : 0, c.TryGetProperty("heard", out var ch) ? Hex(ch) : 0));
                }
                mods.Add(new NativeModInfo(id, ok, know, quests, lines, npcs, markers, errors, computed));
            }
        _mods = mods;
    }

    private static WorldPosition Pos(JsonElement a)
    {
        if (a.ValueKind == JsonValueKind.Object) return new WorldPosition(a.GetProperty("x").GetDouble(), a.GetProperty("y").GetDouble(), a.GetProperty("z").GetDouble());
        var v = a.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new WorldPosition(v[0], v[1], v[2]);
    }

    // "place": the same keys as a step's "site" (Json.cs), plus what only an NPC has
    private static QuestPlace Spec(JsonElement o)
    {
        var p = new QuestPlace { InBase = o.TryGetProperty("inBase", out var ib) && ib.GetBoolean(), Exact = o.TryGetProperty("exact", out var ex) && ex.GetBoolean() };
        if (o.TryGetProperty("at", out var at)) p.At = Pos(at);
        if (o.TryGetProperty("near", out var near))
        {
            if (near.ValueKind == JsonValueKind.String)
            {
                if (near.GetString()!.Equals("player", StringComparison.OrdinalIgnoreCase)) p.NearPlayer = true;
                else p.NearNpc = near.GetString();
            }
            else p.At = Pos(near);
        }
        if (p.At is null && p.NearNpc is null) p.NearPlayer = true;
        if (o.TryGetProperty("distance", out var d))
        {
            if (d.ValueKind == JsonValueKind.Array)
            {
                p.MinDistance = d[0].GetDouble();
                p.MaxDistance = d[1].GetDouble();
            }
            else p.MinDistance = p.MaxDistance = d.GetDouble();
        }
        if (o.TryGetProperty("margin", out var mg)) p.BaseMargin = mg.GetDouble();
        return p;
    }

    // A new server world (a game client hosts one after another). What waited for the last world's players or knowledge is gone.
    private static void WorldStarted()
    {
        QuestPlaces.WorldStarted();
        Waiting.Clear();
        WaitingForPlayer.Clear();
        Unlocking.Clear();
        QuietUntil.Clear();
        _placedThisWorld = false;
        Dialogues.ClearBindings();
        PlaceWhenReady();
    }

    /// <summary>Starts the mirror, the dialogue options and the placement (the engine calls this; idempotent).</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        QuestsInternals.RunAs(null, () =>
        {
            Load();
            Quests.LineResolver = ResolveLine;
            Events.On<QuestStateChangedEvent>(OnState);
            Events.On<QuestObjectiveProgressEvent>(OnProgress);
            Events.On<DialogueLineEvent>(OnLine);
            Events.On<PlayerJoinedEvent>(OnJoined);
            Events.On<ServerStartedEvent>(_ => WorldStarted());
            Events.On<WorldLoadedEvent>(_ => PlaceWhenReady());
            if (Lifecycle.IsServerRunning) PlaceWhenReady();
        });
    }

    private static uint? ResolveLine(string mod, string text)
    {
        // "<dialogue>.<node>.<option>" or a raw "<dialogue>.<node>" greeting
        return Mod(mod)?.Knowledge.TryGetValue("line." + text, out var id) == true ? id : null;
    }

    // ---- quest state -> knowledge --------------------------------------------------------------------------------------

    private static void OnState(QuestStateChangedEvent e)
    {
        if (Quest(e.Quest.FullId) is { } q && e.Quest.Journal == QuestJournalMode.Native) Mirror(e.Quest, q, e.Holder);
    }

    private static void OnProgress(QuestObjectiveProgressEvent e)
    {
        if (Quest(e.Quest.FullId) is { } q && e.Quest.Journal == QuestJournalMode.Native) Mirror(e.Quest, q, e.Holder);
    }

    private static void OnJoined(PlayerJoinedEvent e)
    {
        var holder = QuestHolder.Of(e.Player);
        QuietUntil[holder.Key] = Environment.TickCount64 + QuietAfterJoinMs;
        Resync(holder, quiet: true);
        PlaceWhenReady();
    }

    /// <summary>Writes a holder's knowledge again from the engine's records: every quest, what differs. Tick thread. The engine
    /// does this after every change, at a join and every 2 s; call it after changing a save by hand. <paramref name="quiet"/>: this is
    /// the holder's progress coming back, not news: the game's journal notifies for none of it.</summary>
    [TickThreadOnly]
    public static void Resync(QuestHolder holder, bool quiet = false)
    {
        Game.AssertTickThread();
        foreach (var def in Quests.Definitions)
            if (def.Journal == QuestJournalMode.Native && Quest(def.FullId) is { } q) Mirror(def, q, holder, quiet);
        ComputeFor(holder);
    }

    /// <summary>What the game's knowledge should hold for a holder in a quest, as the engine sees it now. Tick thread. For tools and tests:
    /// it needs no player.</summary>
    [TickThreadOnly]
    public static IReadOnlyList<NativeWanted> Wanted(string fullQuestId, QuestHolder holder)
    {
        Game.AssertTickThread();
        return Quest(fullQuestId) is { } q && Quests.Find(fullQuestId) is { } def ? WantedOf(def, q, holder) : [];
    }

    private static List<NativeWanted> WantedOf(QuestDefinition def, NativeQuestInfo q, QuestHolder holder)
    {
        var rec = Quests.RecordFor(def, holder);
        var status = rec?.Status ?? QuestStatus.Available;
        var started = rec is not null;
        var stepIndex = -1;
        if (rec is { Status: QuestStatus.Active } && Quests.CurrentStep(def, rec) is { } cur) stepIndex = def.Steps.IndexOf(cur);
        else if (rec is { Status: QuestStatus.Failed }) stepIndex = rec.StepIndex;
        var w = new List<NativeWanted>(4 + q.Steps.Count * 5)
        {
            new("started", q.Started, started ? 1u : 0),
            new("active", q.Active, status == QuestStatus.Active && started ? 1u : 0),
            new("done", q.Done, status == QuestStatus.Done && started ? 1u : 0),
        };
        for (var i = 0; i < q.Steps.Count; i++)
        {
            var s = q.Steps[i];
            var at = status == QuestStatus.Active && started && i == stepIndex;
            var done = started && (status == QuestStatus.Done || (status == QuestStatus.Active && i < stepIndex));
            var seen = at || done || (status == QuestStatus.Failed && started && i <= stepIndex);
            w.Add(new NativeWanted($"{s.Id}.visible", s.Visible, seen ? 1u : 0));
            w.Add(new NativeWanted($"{s.Id}.active", s.Active, at ? 1u : 0));
            w.Add(new NativeWanted($"{s.Id}.done", s.Done, done ? 1u : 0));
            if (s.Count != 0)
            {
                var value = 0;
                if (done) value = s.Total;
                else if (seen && rec is not null && def.Step(s.Id) is { } st)
                    for (var k = 0; k < st.Objectives.Count; k++)
                        if (st.Objectives[k].Kind is QuestObjectiveKind.Kill or QuestObjectiveKind.Collect or QuestObjectiveKind.Craft or QuestObjectiveKind.Build or QuestObjectiveKind.Custom)
                            value += Math.Min(rec.Counters.GetValueOrDefault(QuestRecord.CounterKey(st.Id, k)), st.Objectives[k].Count);
                w.Add(new NativeWanted($"{s.Id}.count", s.Count, (uint)value));
            }
        }
        return w;
    }

    private static void Mirror(QuestDefinition def, NativeQuestInfo q, QuestHolder holder, bool quiet = false)
    {
        var world = q.Scope == "world";
        if (!world && holder.Entity.IsNone) return;  // offline or synthetic: mirrored when they join
        var scope = world ? Knowledge.World : Knowledge.For(holder.Entity);
        // the quest's own player Bool: the game sets it, a reset takes it back. Coming back quietly, it goes first: the game's
        // trigger_quest_rewards sets it when a finished quest lacks it, through its own apply, with the notification of a new finish.
        if (!holder.Entity.IsNone)
        {
            var rec = Quests.RecordFor(def, holder);
            if (rec is null) Apply(Knowledge.For(holder.Entity), new NativeWanted("rewarded", q.EntryId, 0));
            else if (quiet && rec.Status == QuestStatus.Done) Apply(Knowledge.For(holder.Entity), new NativeWanted("rewarded", q.EntryId, 1), quiet: true);
        }
        foreach (var w in WantedOf(def, q, world ? QuestHolder.World : holder)) Apply(scope, w, quiet);
    }

    // A join is the world's record of this character coming back, not news. The game's journal notifies for a query that holds and
    // whose state doesn't say it came from a save (bits 32..39 of the state, isSaveGameStates in its message; client 0xf04d90,
    // 0x2fc8e0). The character's record has the mark, and a knowledge the game's apply sets has not. A direct write keeps the mark
    // of an entry that had one, and a missing entry has it (0xff00000000). So what a join finds missing goes in that way: after a
    // visit to another world cleared a quest's mirror (EngineCharacters.cs), done quests used to notify again ("New quest: ...").
    // A write caused by a quest's state changing stays the game's own apply, with its notification.
    private const long QuietAfterJoinMs = 5000;
    private static readonly Dictionary<string, long> QuietUntil = new(StringComparer.Ordinal);

    private static bool IsQuiet(QuestHolder h) => QuietUntil.TryGetValue(h.Key, out var until) && Environment.TickCount64 < until;

    private static void Apply(KnowledgeScope scope, in NativeWanted w, bool quiet = false)
    {
        var id = w.Id;
        try
        {
            var st = scope.Get(id);
            if (st is null || st.Value.Value == w.Value) return;
            _mirrored++;
            // a Bool through the game's own apply: its notifications follow. A counter, a Bool going back, or what a join restores: the exact value.
            var task = w.Value == 0 ? scope.Revoke(id) : st.Value.Type == KnowledgeType.Bool && !quiet ? scope.Grant(id) : scope.Set(id, w.Value);
            task.ContinueWith(t => QuestsInternals.Warn($"native: knowledge 0x{id:x8}: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (NotSupportedException)
        {
            // the player left, or their knowledge is not loaded yet: the next pass gets it
        }
    }

    // ---- conditions a line's one knowledge check cannot say ------------------------------------------------------------------

    private static void ComputeFor(QuestHolder holder)
    {
        if (holder.Entity.IsNone) return;
        KnowledgeScope scope;
        try
        {
            scope = Knowledge.For(holder.Entity);
        }
        catch (NotSupportedException)
        {
            return;
        }
        foreach (var m in Mods)
            foreach (var c in m.Computed)
                Apply(scope, new NativeWanted(c.Target, c.Id, Holds(c, holder, scope) ? 1u : 0));
    }

    /// <summary>What the engine says now of a line's computed knowledge, for a holder (<see cref="NativeModInfo.Computed"/>). Tick
    /// thread. For tools and tests: it needs no player; a gate or a heard line counts as not known.</summary>
    [TickThreadOnly]
    public static bool Holds(NativeComputed c, QuestHolder holder)
    {
        Game.AssertTickThread();
        return Holds(c, holder, null);
    }

    private static bool Holds(NativeComputed c, QuestHolder h, KnowledgeScope? scope)
    {
        try
        {
            if (c.Gate != 0 && scope?.Has(c.Gate) != true) return false;
            if (c.Heard != 0 && scope?.Has(c.Heard) == true) return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        var ctx = new QuestContext(h, null, EntityId.None, c.Mod);
        switch (c.Kind)
        {
            case "available":
                return Quests.Find(c.Target, c.Mod) is { } q && Quests.CanStart(q, h);
            case "ready":
                return HandInReady(c.Target, c.Mod, h);
            case "greeting":
                return Dialogues.Find(c.Target) is { } g && c.Index < g.Greetings.Count && (g.Greetings[c.Index].If is not { } gi || QuestActions.Holds(gi, ctx));
            default:
                if (Dialogues.Find(c.Target) is not { } d || !d.Nodes.TryGetValue(c.Node, out var n) || c.Index >= n.Options.Count) return false;
                var option = n.Options[c.Index];
                if (option.If is { } oi && !QuestActions.Holds(oi, ctx)) return false;
                foreach (var a in option.Do)
                    if (a is { Kind: QuestActionKind.Advance, Force: false, Name: { } name } && IsHandIn(name, c.Mod) && !HandInReady(name, c.Mod, h)) return false;
                return true;
        }
    }

    private static bool IsHandIn(string questDotStep, string mod)
    {
        var (qid, sid) = QuestActions.SplitStep(questDotStep);
        return sid is not null && Quests.Find(qid, mod)?.Step(sid) is { } s && s.Objectives.Any(o => o.Kind == QuestObjectiveKind.Deliver);
    }

    // The holder is at this step and holds what it takes: the only moment a hand-in is on offer.
    private static bool HandInReady(string questDotStep, string mod, QuestHolder h)
    {
        var (qid, sid) = QuestActions.SplitStep(questDotStep);
        if (sid is null || Quests.Find(qid, mod) is not { } q || Quests.RecordFor(q, h) is not { Status: QuestStatus.Active } rec) return false;
        if (Quests.CurrentStep(q, rec) is not { } cur || cur.Id != sid) return false;
        for (var i = 0; i < cur.Objectives.Count; i++)
        {
            var o = cur.Objectives[i];
            if (o.Kind == QuestObjectiveKind.Deliver && QuestObjectives.Inventory(h, o.Item!) < o.Count - rec.Counters.GetValueOrDefault(QuestRecord.CounterKey(cur.Id, i))) return false;
        }
        return true;
    }

    /// <summary>Every ~1 s, on the server: conditions each second, the whole mirror every 2 s, what waits for a player.</summary>
    internal static void Poll()
    {
        if (!_started || Knowledge.Status is not { Applying: true } || !Saves.WorldDataReady) return;
        _polls++;
        PlaceWhenReady();
        var online = Quests.Online();
        foreach (var h in online) ComputeFor(h);
        if (_polls % 2 == 0)
        {
            foreach (var def in Quests.Definitions)
            {
                if (def.Journal != QuestJournalMode.Native || Quest(def.FullId) is not { } q) continue;
                if (q.Scope == "world") Mirror(def, q, QuestHolder.World);
                foreach (var h in online)
                    if (q.Scope != "world" || Quests.RecordFor(def, h) is null) Mirror(def, q, h, IsQuiet(h));
            }
        }
        PumpPlacements();
    }

    // ---- dialogue ------------------------------------------------------------------------------------------------------

    private static void OnLine(DialogueLineEvent e)
    {
        if (e.Action != DialogueAction.Start || e.PlayerEntity.IsNone) return;
        if (!ByLine.TryGetValue(e.LineId, out var l)) return;
        if (l.Option < 0)
        {
            QuestsInternals.Info($"native: {e.PlayerEntity} hears the greeting of {l.Dialogue} (line 0x{e.LineId:x8}) at NPC {e.Npc}");
            return;
        }
        _lines++;
        QuestsInternals.Info($"native: {e.PlayerEntity} chose {l.Dialogue} {l.Node}[{l.Option}] (line 0x{e.LineId:x8}) at NPC {e.Npc}");
        _ = RunOption(e.PlayerEntity, e.Npc, l);
    }

    private static async Task RunOption(EntityId player, EntityId npc, NativeLineInfo l)
    {
        try
        {
            var r = l.Auto.Length > 0 ? await RunAuto(player, l).OnTick() : await Dialogues.ApplyOption(player, npc, l.Dialogue, l.Node, l.Option).OnTick();
            if (r.Ok) return;
            QuestsInternals.Info($"native: {l.Dialogue} {l.Node}[{l.Option}] for {player}: {r.Reason}");
            // the game's menu cannot show a refusal; a line that should not be on offer goes away at the next pass
            QuestPresent.Raw(QuestHolder.Of(player), r.Reason);
            Resync(QuestHolder.Of(player));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            QuestsInternals.Warn($"native: {l.Dialogue} {l.Node}[{l.Option}]: {ex.Message}");
        }
    }

    // A topic Kiln made for an NPC without a dialogue: "start:<quest>" or "advance:<quest>.<step>".
    private static async Task<ConversationResult> RunAuto(EntityId player, NativeLineInfo l)
    {
        var h = QuestHolder.Of(player);
        var colon = l.Auto.IndexOf(':');
        var what = l.Auto[..colon];
        var arg = l.Auto[(colon + 1)..];
        if (what == "start")
        {
            var s = Quests.Find(arg, l.Mod) is { } q ? Quests.Start(q.FullId, h) : QuestResult.No($"no quest {arg}");
            return s.Ok ? ConversationResult.Yes : ConversationResult.No(s.Reason);
        }
        var (qid, step) = QuestActions.SplitStep(arg);
        if (Quests.Find(qid, l.Mod) is not { } q2) return ConversationResult.No($"no quest {qid}");
        var a = await Quests.Advance(q2.FullId, h, step).OnTick();
        return a.Ok ? ConversationResult.Yes : ConversationResult.No(a.Reason);
    }

    // ---- placement -----------------------------------------------------------------------------------------------------

    private sealed class PlacedBook
    {
        public HashSet<string> Placed { get; set; } = [];
    }
    private const string PlacedKey = "native-placed";
    private const int PlaceRetries = 5;
    private const long VerifyAfterMs = 3000;
    private static readonly List<(NativeNpcInfo Npc, uint Unlock)> Waiting = [];
    private static readonly List<NativeNpcInfo> WaitingForPlayer = [];
    private static bool _waitHooked, _placedThisWorld;

    // NPCs need the world's data (who was placed, where) and the world's entities (who is there). A dedicated server has both when it
    // starts. A hosted world starts about 10 s before the game loads it, and has its entities when its player arrives: checked any
    // earlier, an NPC that is there looks gone (seen as "was placed once in this world and is gone" in every later world of a
    // process, with the Warden standing there and never bound to his entity).
    private static void PlaceWhenReady()
    {
        if (_placedThisWorld || !Lifecycle.IsServerRunning || !Saves.WorldDataReady) return;
        if (Client.Available && Client.IsHosting && Quests.Online().Count == 0) return;
        _placedThisWorld = true;
        Place();
    }

    private static void Place()
    {
        foreach (var def in Quests.Definitions)
            if (def.Journal == QuestJournalMode.Native && Quest(def.FullId) is { Scope: "world" } q) Mirror(def, q, QuestHolder.World);
        foreach (var m in Mods)
        {
            if (!m.Ok) continue;
            foreach (var n in m.Npcs) PlaceNpc(n);
        }
    }

    private static PlacedBook Book() =>
        QuestsInternals.RunAs(QuestsInternals.DataOwner, () => WorldData.TryGet<PlacedBook>(PlacedKey, out var b) ? b : new PlacedBook());

    private static void Remember(string id)
    {
        var b = Book();
        b.Placed.Add(id);
        QuestsInternals.RunAs(QuestsInternals.DataOwner, () => WorldData.Set(PlacedKey, b));
    }

    private static void OnUnlock(KnowledgeChangedEvent e)
    {
        if (e.NewValue == 0) return;
        foreach (var w in Waiting.ToList())
        {
            if (w.Unlock != e.Id) continue;
            Waiting.Remove(w);
            PlaceWhenUnlocked(w.Npc);
        }
    }

    private static void PlaceNpc(NativeNpcInfo n)
    {
        var existing = Spawn.Find(n.Guid);
        if (existing.Count > 0)
        {
            Quests.BindNpc(n.FullId, existing[0]);
            QuestsInternals.Info($"native: NPC {n.FullId} is in the world: {existing[0]}");
            return;
        }
        if (n.Place is not { } p) return;
        if (p.Once && Book().Placed.Contains(n.FullId))
        {
            QuestsInternals.Info($"native: NPC {n.FullId} was placed once in this world and is gone: not placed again");
            return;
        }
        // The game destroys a roster NPC whose unlock does not hold (unique_npc_spawns, docs/re/creatures.md 7.6). A game NPC
        // placed as a giver gets its unlock, unless "unlock": false; a clone waits for its own knowledge
        // unless "unlock": true.
        var roster = Knowledge.Npcs.FirstOrDefault(r => r.NpcId == NativeIds.Fnv(n.Guid));
        if (roster is { Unlocked: false } && roster.Unlock.Id != 0)
        {
            if (p.Unlock ?? !n.IsClone)
            {
                _ = UnlockThenPlace(n, roster.Unlock.Id);
                return;
            }
            Waiting.Add((n, roster.Unlock.Id));
            if (!_waitHooked)
            {
                _waitHooked = true;
                Events.On<KnowledgeChangedEvent>(OnUnlock);
            }
            QuestsInternals.Info($"native: NPC {n.FullId} waits for its unlock 0x{roster.Unlock.Id:x8}");
            return;
        }
        _ = PlaceNpcAt(n, p, 0);
    }

    private static async Task UnlockThenPlace(NativeNpcInfo n, uint unlock)
    {
        try
        {
            var granted = await Knowledge.World.Grant(unlock).OnTick();
            if (granted.Ok) QuestsInternals.Info($"native: NPC {n.FullId}: its unlock 0x{unlock:x8} granted for this world");
            else QuestsInternals.Warn($"native: NPC {n.FullId}: its unlock 0x{unlock:x8} was not granted ({granted.Reason}); placing it once the roster says it is unlocked");
            PlaceWhenUnlocked(n);  // waits for the roster either way, and places it after 300 steps without it
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            QuestsInternals.Warn($"native: NPC {n.FullId}: {ex.Message}");
        }
    }

    // npc_unlock_state_server follows the knowledge within a step or two: spawned earlier, the game would destroy the NPC.
    private static readonly List<(NativeNpcInfo Npc, int Ticks)> Unlocking = [];
    private static IDisposable? _pump;

    private static void PlaceWhenUnlocked(NativeNpcInfo n)
    {
        Unlocking.Add((n, 0));
        _pump ??= ServiceTick.Add("native quests", Pump);
    }

    private static void Pump(in TickInfo tick)
    {
        for (var i = Unlocking.Count - 1; i >= 0; i--)
        {
            var (n, ticks) = Unlocking[i];
            var unlocked = Knowledge.Npcs.FirstOrDefault(r => r.NpcId == NativeIds.Fnv(n.Guid)) is { Unlocked: true };
            if (!unlocked && ticks < 300)
            {
                Unlocking[i] = (n, ticks + 1);
                continue;
            }
            Unlocking.RemoveAt(i);
            if (!unlocked) QuestsInternals.Warn($"native: NPC {n.FullId}: the roster does not report it unlocked; placing it anyway");
            if (n.Place is { } p && Spawn.Find(n.Guid).Count == 0) _ = PlaceNpcAt(n, p, 0);
        }
    }

    // an NPC placed near "player" waits for one; the world remembers where, so it is one place from then on
    private static void PumpPlacements()
    {
        if (WaitingForPlayer.Count == 0 || Quests.Online().Count == 0) return;
        foreach (var n in WaitingForPlayer.ToArray())
        {
            WaitingForPlayer.Remove(n);
            PlaceNpc(n);
        }
    }

    private static WorldPosition? AnchorFor(QuestPlace p, string mod)
    {
        if (p.At is { } at) return at;
        if (p.NearNpc is { } npc) return QuestPlaces.NpcPosition(npc, mod);
        foreach (var o in Quests.Online())
            if (QuestObjectives.PositionOf(o) is { } pos) return pos;
        return null;
    }

    private static async Task PlaceNpcAt(NativeNpcInfo n, NativePlacement p, int attempt)
    {
        try
        {
            WorldPosition at;
            if (p.Spec is { Exact: true, At: { } exact }) at = exact;
            else
            {
                if (AnchorFor(p.Spec, n.Mod) is not { } anchor)
                {
                    if (!WaitingForPlayer.Contains(n)) WaitingForPlayer.Add(n);
                    QuestsInternals.Info($"native: NPC {n.FullId} is placed near the first player in game");
                    return;
                }
                var spot = await QuestPlaces.SiteOf($"npc:{n.FullId}", p.Spec, anchor, $"NPC {n.FullId}").OnTick();
                if (spot is null) return;  // logged
                at = spot.Value;
            }
            var e = await Spawn.Entity(n.Guid, new WorldPosition(at.X, at.Y + 0.1, at.Z), p.Yaw, new SpawnOptions { Tag = "native-npc", Flags = SpawnFlags.NoTrack }).OnTick();
            _placed++;
            Quests.BindNpc(n.FullId, e);
            QuestsInternals.Info($"native: NPC {n.FullId} placed: {e} at {at}");
            _ = VerifyNpc(n, p, e, at, attempt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            QuestsInternals.Warn($"native: NPC {n.FullId}: {ex.Message}");
        }
    }

    // The game's rules can take an NPC away again (a locked roster NPC, a second one of its id). It says so, and tries again.
    private static async Task VerifyNpc(NativeNpcInfo n, NativePlacement p, EntityId e, WorldPosition at, int attempt)
    {
        var until = Environment.TickCount64 + VerifyAfterMs;
        while (Environment.TickCount64 < until) await QuestTick.NextStep();
        if (Spawn.Exists(e))
        {
            Remember(n.FullId);
            QuestsInternals.Info($"native: NPC {n.FullId} verified: {e} is still at {at} after {VerifyAfterMs / 1000} s");
            return;
        }
        var other = Spawn.Find(n.Guid);
        if (other.Count > 0)
        {
            QuestsInternals.Warn($"native: NPC {n.FullId}: {e} was removed, but {other[0]} is in the world (one per world): using that one");
            Quests.BindNpc(n.FullId, other[0]);
            return;
        }
        if (attempt >= PlaceRetries)
        {
            QuestsInternals.Error($"native: NPC {n.FullId}: the game removed it {attempt + 1} times at {at}; giving up. A roster NPC needs its unlock: \"place\": {{ \"unlock\": true }}");
            return;
        }
        QuestsInternals.Warn($"native: NPC {n.FullId}: the game removed {e} at {at}; placing it again (attempt {attempt + 2} of {PlaceRetries + 1})");
        await PlaceNpcAt(n, p, attempt + 1).OnTick();
    }

    // ---- markers -------------------------------------------------------------------------------------------------------

    /// <summary>Puts a step's map marker at a place. One entity per step: a step has one place per world (Places.cs), and it shows
    /// only to players at that step. Any entity of it elsewhere is moved.</summary>
    internal static void ShowMarker(QuestDefinition q, QuestStep step, WorldPosition at)
    {
        if (!MarkerOf.TryGetValue($"{q.FullId}.{step.Id}", out var k)) return;
        _ = PlaceMarker(k, at);
    }

    private static async Task PlaceMarker(NativeMarkerInfo k, WorldPosition at)
    {
        try
        {
            var same = false;
            foreach (var old in Spawn.Find(k.Guid))
            {
                if (Spawn.Inspect(old) is { } i && i.Position.DistanceTo(at) < 1) same = true;
                else Spawn.Destroy(old);
            }
            if (same) return;
            var e = await Spawn.Entity(k.Guid, at, 0, new SpawnOptions { Tag = "native-marker", Flags = SpawnFlags.NoTrack }).OnTick();
            _placed++;
            QuestsInternals.Info($"native: marker {k.Template} placed: {e} at {at}");
            QuestPlaces.Verify(e, $"native: marker {k.Template} at {at}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            QuestsInternals.Warn($"native: marker {k.Template}: {ex.Message}");
        }
    }
}
