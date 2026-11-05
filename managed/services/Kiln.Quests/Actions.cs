// SPDX-License-Identifier: MIT
// Kiln.Quests: conditions (quest requirements, conversation options), actions (rewards, hooks, an option's "do"), the
// chat lines of chat quests, and the player's waypoint. Tick thread; actions that wait come back to it (QuestTick).
namespace Kiln;

/// <summary>What a list of actions did.</summary>
internal readonly record struct QuestRun(bool Ok, string Reason);

internal static class QuestActions
{
    private static readonly Queue<string> Recent = new();

    /// <summary>The last action outcomes (for admins and tests).</summary>
    public static IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (Recent) return Recent.ToArray();
        }
    }

    private static void Note(string line)
    {
        lock (Recent)
        {
            Recent.Enqueue(line);
            while (Recent.Count > 128) Recent.Dequeue();
        }
        QuestsInternals.Info(line);
    }

    // --------------------------------------------------------------------------------------------- conditions ------
    public static bool Holds(QuestCondition c, QuestContext ctx, int depth = 0)
    {
        if (depth > 16)
        {
            QuestsInternals.Warn($"{ctx.Quest?.FullId}: conditions nest too deep or refer to each other");
            return false;
        }
        try
        {
            switch (c.Kind)
            {
                case QuestConditionKind.All:
                    return c.Children.All(x => Holds(x, ctx, depth + 1));
                case QuestConditionKind.Any:
                    return c.Children.Any(x => Holds(x, ctx, depth + 1));
                case QuestConditionKind.Not:
                    return !c.Children.All(x => Holds(x, ctx, depth + 1));
                case QuestConditionKind.Knowledge:
                    return QuestObjectives.KnowledgeHolds(ctx.Holder, c.Name!);
                case QuestConditionKind.Level:
                    return LevelOf(ctx.Holder) >= c.Count;
                case QuestConditionKind.Item:
                    return QuestObjectives.Inventory(ctx.Holder, c.Name!) >= c.Count;
                case QuestConditionKind.Quest:
                    return QuestIs(c, ctx, depth);
                case QuestConditionKind.Step:
                    return StepIs(c, ctx);
                case QuestConditionKind.Predicate:
                    var f = c.Predicate ?? (c.Name is { } n ? Quests.NamedPredicate(n) : null);
                    if (f is null)
                    {
                        QuestsInternals.Warn($"{ctx.Quest?.FullId}: no predicate \"{c.Name}\" is registered: the condition is false");
                        return false;
                    }
                    return f(ctx);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            QuestsInternals.Warn($"{ctx.Quest?.FullId}: a {c.Kind} condition threw: {e.Message}");
        }
        return false;
    }

    private static int LevelOf(QuestHolder h)
    {
        if (QuestInput.IsSynthetic(h)) return QuestInput.LevelOf(h);
        if (h.Entity.IsNone || !Progression.Available) return 0;
        return Progression.Get(h.Entity)?.Level ?? 0;
    }

    private static bool QuestIs(QuestCondition c, QuestContext ctx, int depth)
    {
        if (Quests.Find(c.Name!, ctx.ModId) is not { } q)
        {
            QuestsInternals.Warn($"condition: no quest \"{c.Name}\"");
            return false;
        }
        var want = (c.Is ?? "done").ToLowerInvariant();
        if (depth > 8) return false;
        return want switch
        {
            "available" => Quests.CanStart(q, ctx.Holder),
            "active" => Quests.StatusOf(q, ctx.Holder) == QuestStatus.Active,
            "done" => Quests.StatusOf(q, ctx.Holder) == QuestStatus.Done,
            "failed" => Quests.StatusOf(q, ctx.Holder) == QuestStatus.Failed,
            "locked" => Quests.StatusOf(q, ctx.Holder) == QuestStatus.Locked,
            "started" => Quests.StatusOf(q, ctx.Holder) is QuestStatus.Active or QuestStatus.Done or QuestStatus.Failed,
            _ => throw new InvalidDataException($"quest condition \"is\": {want}"),
        };
    }

    private static bool StepIs(QuestCondition c, QuestContext ctx)
    {
        var (qid, sid) = SplitStep(c.Name!);
        if (sid is null || Quests.Find(qid, ctx.ModId) is not { } q || q.Step(sid) is not { } step)
        {
            QuestsInternals.Warn($"condition: no step \"{c.Name}\"");
            return false;
        }
        var want = (c.Is ?? "done").ToLowerInvariant();
        var owner = q.Scope == QuestScope.World ? QuestHolder.World : ctx.Holder;
        var rec = Quests.BookOf(owner).Quests.GetValueOrDefault(q.FullId);
        var index = q.Steps.IndexOf(step);
        var cur = rec is null ? null : Quests.CurrentStep(q, rec);
        return want switch
        {
            "active" => cur is not null && cur.Id == step.Id,
            "done" => rec is { Status: QuestStatus.Done } || (cur is not null && q.Steps.IndexOf(cur) > index),
            "pending" => rec is null || rec.Status == QuestStatus.Failed || (cur is not null && q.Steps.IndexOf(cur) < index),
            _ => throw new InvalidDataException($"step condition \"is\": {want}"),
        };
    }

    /// <summary>"quest.step" or "mod:quest.step".</summary>
    internal static (string Quest, string? Step) SplitStep(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? (name, null) : (name[..dot], name[(dot + 1)..]);
    }

    // ------------------------------------------------------------------------------------------------ actions ------

    /// <summary>Runs actions in order. <paramref name="keepGoing"/>: a failure is logged and the rest still runs (rewards);
    /// otherwise the first failure stops (a conversation option).</summary>
    public static async Task<QuestRun> RunAll(IReadOnlyList<QuestAction> list, QuestActionContext ctx, string what, bool keepGoing)
    {
        var first = "";
        var label = $"{ctx.Holder.Key} {ctx.Quest?.FullId ?? ctx.ModId} {what}";
        foreach (var a in list)
        {
            string? problem;
            try
            {
                problem = await One(a, ctx, label).OnTick();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                problem = e.Message;
            }
            Quests.ActionsRun++;
            if (problem is null) continue;
            Quests.ActionsFailed++;
            Note($"{label}: {a.Kind} {a.Name}: FAILED: {problem}");
            if (first.Length == 0) first = problem;
            if (!keepGoing) return new QuestRun(false, problem);
        }
        return new QuestRun(first.Length == 0, first);
    }

    private static async Task<string?> One(QuestAction a, QuestActionContext ctx, string label)
    {
        var h = ctx.Holder;
        var mod = ctx.ModId;
        switch (a.Kind)
        {
            case QuestActionKind.Give:
            {
                var to = Recipients(h);
                if (to.Count == 0)
                {
                    Note($"{label}: give {a.Count} x {a.Name}: skipped (no player)");
                    return null;
                }
                if (!Items.Available) return "kiln.items is unavailable";
                foreach (var e in to)
                {
                    var r = await Items.Give(e, a.Name!, a.Count, null, GiveOptions.Notify).OnTick();
                    if (r.Status is ItemsStatus.Done or ItemsStatus.Partial or ItemsStatus.NoRoom)
                    {
                        var rest = a.Count - r.Done;
                        if (rest > 0) await DropAtFeet(e, a.Name!, rest, label).OnTick();
                        Note($"{label}: give {a.Count} x {a.Name} to {e}: {r.Status} ({r.Done})");
                        continue;
                    }
                    return r.ToString();
                }
                return null;
            }
            case QuestActionKind.Take:
            {
                if (QuestInput.IsSynthetic(h))
                {
                    var have = QuestInput.ItemCount(h, a.Name!);
                    if (have < a.Count) return $"not enough {a.Name}: {have} of {a.Count}";
                    QuestInput.SetItemCount(h, a.Name!, have - a.Count);
                    return null;
                }
                if (h.Entity.IsNone) return "a player in game is needed";
                if (QuestObjectives.ItemIdOf(a.Name!) is not { } id) return $"no item {a.Name}";
                var r = await Items.Take(h.Entity, new ItemId(id), a.Count).OnTick();
                Note($"{label}: take {a.Count} x {a.Name}: {r.Status}");
                return r.Status == ItemsStatus.Done ? null : r.ToString();
            }
            case QuestActionKind.Xp:
            {
                var to = Recipients(h);
                if (to.Count == 0)
                {
                    Note($"{label}: xp {a.Value}: skipped (no player)");
                    return null;
                }
                if (!Progression.Available) return "kiln.progression is unavailable";
                string? problem = null;
                foreach (var e in to)
                {
                    var r = await Progression.AddExperience(e, a.Value, ExperienceSource.Quest).OnTick();
                    Note($"{label}: xp {a.Value} to {e}: {(r.Ok ? "ok" : r.Reason)}");
                    if (!r.Ok) problem = r.Reason;
                }
                return problem;
            }
            case QuestActionKind.Knowledge:
            {
                if (!Knowledge.Available) return "kiln.knowledge is unavailable";
                var id = Knowledge.Resolve(a.Name!);
                if (id.IsQuery) return $"{a.Name} is a query: it cannot be granted";
                var target = KnowledgeTarget(id.Id, a.Target, h, out var why);
                if (why is not null) return why;
                var r = await Knowledge.For(target).Grant(id.Id, a.Value).OnTick();
                Note($"{label}: knowledge {a.Name} ({(target.IsNone ? "world" : $"player {target}")}): {r}");
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Revoke:
            {
                if (!Knowledge.Available) return "kiln.knowledge is unavailable";
                var id = Knowledge.Resolve(a.Name!);
                if (id.IsQuery) return $"{a.Name} is a query: it cannot be revoked";
                var target = KnowledgeTarget(id.Id, a.Target, h, out var why);
                if (why is not null) return why;
                var r = await Knowledge.For(target).Revoke(id.Id, (int)a.Value - 1).OnTick();
                Note($"{label}: revoke {a.Name} ({(target.IsNone ? "world" : $"player {target}")}): {r}");
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Start:
            {
                var q = Quests.Find(a.Name!, mod);
                if (q is null) return $"no quest \"{a.Name}\"";
                var r = Quests.Start(q, h, a.Force);
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Complete:
            {
                var q = Quests.Find(a.Name!, mod);
                if (q is null) return $"no quest \"{a.Name}\"";
                var r = Quests.Complete(q.FullId, h);
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Fail:
            {
                var q = Quests.Find(a.Name!, mod);
                if (q is null) return $"no quest \"{a.Name}\"";
                var r = Quests.Fail(q.FullId, h);
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Reset:
            {
                var q = Quests.Find(a.Name!, mod);
                if (q is null) return $"no quest \"{a.Name}\"";
                var r = Quests.Reset(q.FullId, h);
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Advance:
            {
                var (qid, step) = SplitStep(a.Name!);
                var q = Quests.Find(qid, mod);
                if (q is null) return $"no quest \"{qid}\"";
                var r = await Quests.Advance(q.FullId, h, step, a.Force).OnTick();
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Spawn:
            {
                if (!Spawn.Available) return "kiln.spawn is unavailable";
                if (Where(a, h) is not { } wanted) return "no position for the spawn (no at, no player for the offset)";
                // on real ground, outside every base: the game pacifies enemies in a base and may remove them
                var at = a.Exact || QuestInput.IsSynthetic(h) ? wanted : await QuestPlaces.Settle(wanted, $"{label}: spawn {a.Name}").OnTick() ?? wanted;
                for (var i = 0; i < Math.Max(1, a.Count); i++)
                {
                    var p = i == 0 ? at : new WorldPosition(at.X + 2 * Math.Cos(i), at.Y, at.Z + 2 * Math.Sin(i));
                    var e = await Spawn.Entity(a.Name!, p, (float)a.Yaw, new SpawnOptions
                    {
                        Tag = a.Tag ?? $"quest:{mod}", Flags = a.Transient ? SpawnFlags.Transient : SpawnFlags.None,
                    }).OnTick();
                    Note($"{label}: spawned {a.Name} as {e} at {p}");
                    QuestPlaces.Verify(e, $"{label}: {a.Name} at {p}");
                    if (a.BindNpc is { } npc)
                    {
                        if (Quests.FindNpcDef(npc, mod) is { } d) Dialogues.Bind(d.FullId, e);
                        else return $"bindNpc: no NPC \"{npc}\"";
                    }
                }
                return null;
            }
            case QuestActionKind.Despawn:
            {
                if (!Spawn.Available) return "kiln.spawn is unavailable";
                var r = Spawn.CleanupTracked(a.Name);
                Note($"{label}: despawn tag {a.Name}: {r.Destroyed} destroyed");
                return null;
            }
            case QuestActionKind.Teleport:
            {
                if (h.Entity.IsNone) return "a player in game is needed";
                if (!Teleport.Available) return "teleport is unavailable";
                if (Where(a, h) is not { } at) return "no position for the teleport";
                var r = await Teleport.To(h.Entity, at).OnTick();
                return r.Moved ? null : r.Reason;
            }
            case QuestActionKind.Chat:
            case QuestActionKind.Toast:  // Kiln has no toasts of its own any more: the game's notifications follow its journal
                QuestPresent.Chat(h, a.Text ?? "");
                return null;
            case QuestActionKind.Waypoint:
                if (a.Clear)
                {
                    QuestMarkers.Clear(h);
                    return null;
                }
                if (Where(a, h) is not { } wp) return "no position for the waypoint";
                QuestMarkers.Set(h, wp);
                return null;
            case QuestActionKind.Talk:
            {
                var d = Dialogues.FindDefinition(a.Name!, mod);
                if (d is null) return $"no dialogue \"{a.Name}\"";
                var r = Dialogues.Open(h, d, ctx.Npc, a.Node);
                return r.Ok ? null : r.Reason;
            }
            case QuestActionKind.Signal:
                Quests.Signal(a.Name!, h, a.Count);
                return null;
            case QuestActionKind.Call:
            {
                var named = a.Run is null && a.Name is { } n ? Quests.NamedAction(n) : null;
                var f = a.Run ?? named?.Run;
                if (f is null) return $"no action \"{a.Name}\" is registered";
                await QuestsInternals.CallAs(named?.Owner ?? Quests.OwnerOf(ctx.Quest), () => f(ctx)).OnTick();
                return null;
            }
            default:
                return $"unknown action {a.Kind}";
        }
    }

    private static async Task DropAtFeet(EntityId e, string item, int count, string label)
    {
        // the rest of a reward the inventory has no room for lands at the player's feet: a reward is never lost
        try
        {
            if (QuestObjectives.ItemIdOf(item) is { } id && Spawn.Available && World.Position(e) is { } p)
                await Spawn.Item(id, count, new WorldPosition(p.X, p.Y + 0.5, p.Z)).OnTick();
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException or SpawnException)
        {
            Note($"{label}: {count} x {item} did not fit and could not be dropped: {x.Message}");
        }
    }

    private static IReadOnlyList<EntityId> Recipients(QuestHolder h)
    {
        if (h.IsWorld) return Quests.Online().Select(o => o.Entity).ToArray();
        return h.Entity.IsNone ? [] : [h.Entity];
    }

    private static EntityId KnowledgeTarget(uint id, string? target, QuestHolder h, out string? why)
    {
        why = null;
        if ("world".Equals(target, StringComparison.OrdinalIgnoreCase) || h.IsWorld) return EntityId.None;
        var info = Knowledge.Info(id);
        if ("player".Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            if (h.Entity.IsNone) why = "a player in game is needed for player knowledge";
            return h.Entity;
        }
        if (info is { IsPlayer: true } && !h.Entity.IsNone) return h.Entity;
        if (info is { IsWorld: true }) return EntityId.None;
        why = info is null ? $"0x{id:x8} is not knowledge of this game build" : "a player in game is needed for player knowledge";
        return EntityId.None;
    }

    private static WorldPosition? Where(QuestAction a, QuestHolder h)
    {
        if (a.At is { } at && a.Offset is null) return at;
        if (a.Offset is not { } off) return QuestObjectives.PositionOf(h);
        var from = a.At ?? QuestObjectives.PositionOf(h);
        return from is { } f ? new WorldPosition(f.X + off.X, f.Y + off.Y, f.Z + off.Z) : null;
    }
}

/// <summary>The lines of a chat quest (<c>"journal": "chat"</c>, or a native quest whose data did not build: kiln.log says why).
/// A quest in the game's journal says nothing here: the journal's notifications and steps follow its knowledge.</summary>
internal static class QuestPresent
{
    private static readonly UiText StartedText = UiText.Of(("en", "New quest: {0}"), ("de", "Neue Quest: {0}"));
    private static readonly UiText DoneText = UiText.Of(("en", "Quest complete: {0}"), ("de", "Quest abgeschlossen: {0}"));
    private static readonly UiText FailedText = UiText.Of(("en", "Quest failed: {0}"), ("de", "Quest fehlgeschlagen: {0}"));

    private static UiText Format(UiText template, UiText value)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in template.Languages.Concat(value.Languages)) d[k] = string.Format(template.For(k), value.For(k));
        return new UiText(d);
    }

    /// <summary>A step's objective line in one language, with its counter: "Recover the crates (1/3)".</summary>
    private static UiText Counted(UiText text, int value, int target)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in text.Languages) d[k] = $"{text.For(k)} ({value}/{target})";
        return new UiText(d);
    }

    private static bool Silent(QuestDefinition q) => NativeQuests.ShownByGame(q);

    public static void Started(QuestHolder h, QuestDefinition q)
    {
        if (!Silent(q)) Chat(h, Format(StartedText, q.Name));
    }

    public static void Completed(QuestHolder h, QuestDefinition q)
    {
        if (!Silent(q)) Chat(h, Format(DoneText, q.Name));
    }

    public static void Failed(QuestHolder h, QuestDefinition q)
    {
        if (!Silent(q)) Chat(h, Format(FailedText, q.Name));
    }

    public static void Step(QuestHolder h, QuestDefinition q, QuestRecord rec, QuestStep step)
    {
        if (Silent(q)) return;
        var o = step.Objectives.Count == 1 ? step.Objectives[0] : null;
        Chat(h, o is { Count: > 1 } ? Counted(step.Text, rec.Counters.GetValueOrDefault(QuestRecord.CounterKey(step.Id, 0)), o.Count) : step.Text);
    }

    /// <summary>A counter rose.</summary>
    public static void Progress(QuestHolder h, QuestDefinition q, QuestStep step, QuestObjective o, int value)
    {
        if (o.Count > 1 && !Silent(q)) Chat(h, Counted(o.Text ?? step.Text, value, o.Count));
    }

    public static void Chat(QuestHolder h, UiText text)
    {
        foreach (var t in h.IsWorld ? Quests.Online() : [h]) Tell(t, text);
    }

    private static void Tell(QuestHolder t, UiText text)
    {
        var s = text.For(Quests.ChatLanguage);
        if (s.Length == 0 || t.Entity.IsNone || !Kiln.Chat.Available) return;
        Kiln.Chat.Tell(t.Entity, "[Quest] " + s);
    }

    /// <summary>A plain chat line for a conversation (no [Quest] tag).</summary>
    public static void Raw(QuestHolder t, string text)
    {
        if (text.Length == 0 || t.Entity.IsNone || !Kiln.Chat.Available) return;
        Kiln.Chat.Tell(t.Entity, text);
    }
}

/// <summary>The player's waypoint (PlayerWaypoint {enabled, position}, the game's own map and compass pin), written by
/// the native service kiln.questmark (native/services/questmark). Works on vanilla clients if the game replicates the
/// component (docs/re/quests.md 6, client check C1).</summary>
internal static unsafe class QuestMarkers
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint, byte, double, double, double, int> Set;
        public delegate* unmanaged[Cdecl]<ulong*, ulong*, int> Counters;
        public delegate* unmanaged[Cdecl]<uint> Active;
    }

    private static Table* _t;
    private static bool _loggedMissing;
    private static readonly Dictionary<string, (WorldPosition At, string? Owner)> Current = new(StringComparer.Ordinal);

    private static Table* T => _t != null ? _t : (_t = Services.Get<Table>("kiln.questmark", 1));

    /// <summary>The service is loaded and its program runs in this world.</summary>
    public static bool Available => T != null && T->Active() != 0;

    /// <summary>Waypoints written and how many program updates saw a request (for tests).</summary>
    public static (ulong Requests, ulong Written) Counters
    {
        get
        {
            var t = T;
            if (t == null) return default;
            ulong a = 0, b = 0;
            t->Counters(&a, &b);
            return (a, b);
        }
    }

    /// <summary><paramref name="owner"/>: the quest that points there; only it takes the pin away again.</summary>
    public static void Set(QuestHolder h, WorldPosition at, string? owner = null)
    {
        foreach (var t in h.IsWorld ? Quests.Online() : [h])
        {
            Current[t.Key] = (at, owner);
            Write(t, at);
        }
    }

    /// <summary>Takes the pin away if <paramref name="owner"/> set it (null: whoever did).</summary>
    public static void Clear(QuestHolder h, string? owner = null)
    {
        foreach (var t in h.IsWorld ? Quests.Online() : [h])
        {
            if (!Current.TryGetValue(t.Key, out var c) || (owner is not null && c.Owner != owner)) continue;
            Current.Remove(t.Key);
            Write(t, null);
        }
    }

    /// <summary>A player left: nothing to take away from them.</summary>
    public static void Forget(QuestHolder h) => Current.Remove(h.Key);

    private static void Write(QuestHolder h, WorldPosition? at)
    {
        if (h.Entity.IsNone) return;
        var t = T;
        if (t == null)
        {
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                QuestsInternals.Warn("kiln.questmark is unavailable: quest steps set no waypoint on this build");
            }
            return;
        }
        var p = at ?? default;
        var rc = t->Set(h.Entity.Value, at is null ? (byte)0 : (byte)1, p.X, p.Y, p.Z);
        if (rc != 0) QuestsInternals.Warn($"waypoint of {h}: kiln.questmark returned {rc}");
        else QuestsInternals.Info($"waypoint of {h}: {(at is null ? "cleared" : at.ToString())}");
    }

    private static ulong _writtenLogged;

    /// <summary>Once a second: says when the program wrote what was asked, so a log tells a write that never happened from
    /// a waypoint a client does not draw.</summary>
    public static void LogWrites()
    {
        var (requests, written) = Counters;
        if (written == _writtenLogged) return;
        _writtenLogged = written;
        QuestsInternals.Info($"kiln.questmark wrote {written} waypoint(s) so far ({requests} requests)");
    }
}
