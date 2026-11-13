// SPDX-License-Identifier: MIT
// Kiln.Quests dialogue definitions, and chat conversations. A dialogue is in the game's own NPC menu unless it says
// "native": false (Kiln.Quests/Native builds it; ApplyOption below runs what a player picked there). A chat conversation is the
// text fallback for mods vanilla clients join: one open conversation per holder, chat lines, /talk <n>; the server picks the
// options a player sees, checks a choice again when it arrives, and runs its actions.
namespace Kiln;

/// <summary>The answer to a conversation request.</summary>
public sealed record ConversationResult(bool Ok, string Reason) : IKilnResult
{
    // IKilnResult's reason is null when the action happened; this record's own Reason keeps its text ("" then).
    string? IKilnResult.Reason => Ok ? null : Reason;

    internal static readonly ConversationResult Yes = new(true, "");
    internal static ConversationResult No(string why) => new(false, why);
    public override string ToString() => Ok ? "ok" : $"refused: {Reason}";
}

/// <summary>What a holder is being shown (tick thread; for tools and tests).</summary>
public sealed record ConversationView(long Id, string Dialogue, string Node, UiText? Speaker, UiText Text, IReadOnlyList<(int Index, UiText Text)> Options, EntityId Npc);

/// <summary>Dialogue graphs: lines in the game's own NPC menu, or chat conversations for mods vanilla clients join.</summary>
public static class Dialogues
{
    private sealed class Session
    {
        public required long Id;
        public required QuestHolder Holder;
        public required DialogueDefinition Def;
        public required string Node;
        public EntityId Npc;
        public QuestNpcDefinition? NpcDef;
        public WorldPosition? At;
        public long LastActive = Environment.TickCount64;
        public List<int> Shown = new();
        public bool Busy;
    }

    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, EntityId> Bound = new(StringComparer.Ordinal);
    private static long _next;
    private const double GiveUpDistance = 50;
    private const long IdleMs = 300_000;

    internal static int OpenCount => Sessions.Count;

    private static readonly UiText CloseText = UiText.Of(("en", "Farewell"), ("de", "Lebewohl"));

    // ------------------------------------------------------------------------------------------- definitions ------

    /// <summary>Registers a dialogue as the calling mod's (it goes away when the mod unloads). Throws
    /// <see cref="ArgumentException"/> when a node it refers to does not exist.</summary>
    public static DialogueDefinition Define(DialogueDefinition dialogue)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        var mod = QuestsInternals.CurrentMod;
        dialogue.ModId = mod?.Info.Id ?? dialogue.ModId;
        var problems = Check(dialogue);
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));
        Quests.EnsureStarted();
        lock (Quests.Gate) Quests.DialogueRegistry[dialogue.FullId] = (dialogue, mod, false);
        if (mod is not null) QuestsInternals.OnRetiring(mod, () => { lock (Quests.Gate) Quests.DialogueRegistry.Remove(dialogue.FullId); });
        NativeExport.Changed(dialogue.ModId);
        return dialogue;
    }

    internal static void ForgetBlock(DialogueDefinition definition)
    {
        bool ownsId;
        lock (Quests.Gate)
        {
            ownsId = !Quests.DialogueRegistry.TryGetValue(definition.FullId, out var current) || ReferenceEquals(current.Def, definition);
            if (ownsId)
            {
                Quests.DialogueRegistry.Remove(definition.FullId);
                NativeExport.DefinedInData('d', definition.FullId, false);
                NativeExport.Changed(definition.ModId);
            }
        }
        foreach (var session in Sessions.Values.Where(s => ownsId ? s.Def.FullId == definition.FullId : ReferenceEquals(s.Def, definition)).ToArray()) CloseFor(session.Holder, true);
    }

    public static IReadOnlyList<DialogueDefinition> Definitions
    {
        get
        {
            lock (Quests.Gate) return Quests.DialogueRegistry.Values.Select(e => e.Def).ToArray();
        }
    }

    /// <summary>A dialogue by full id, or by its own id when only one mod has it.</summary>
    public static DialogueDefinition? Find(string id) => FindDefinition(id, null);

    internal static DialogueDefinition? FindDefinition(string id, string? modId)
    {
        lock (Quests.Gate)
        {
            if (Quests.DialogueRegistry.TryGetValue(id, out var e)) return e.Def;
            if (modId is { Length: > 0 } && Quests.DialogueRegistry.TryGetValue($"{modId}:{id}", out e)) return e.Def;
            if (id.Contains(':')) return null;
            var m = Quests.DialogueRegistry.Values.Where(x => x.Def.Id == id).Take(2).ToArray();
            return m.Length == 1 ? m[0].Def : null;
        }
    }

    internal static List<string> Check(DialogueDefinition d)
    {
        var problems = new List<string>();
        if (!d.Nodes.ContainsKey(d.Start)) problems.Add($"dialogue {d.Id}: no start node \"{d.Start}\"");
        void Target(string? name, string where)
        {
            if (name is not (null or "end") && !d.Nodes.ContainsKey(name)) problems.Add($"dialogue {d.Id}: {where} goes to unknown node \"{name}\"");
        }
        foreach (var (id, n) in d.Nodes)
        {
            Target(n.Goto, $"node {id}");
            for (var i = 0; i < n.Options.Count; i++) Target(n.Options[i].Goto, $"node {id} option {i + 1}");
        }
        return problems;
    }

    // ------------------------------------------------------------------------------------------------- NPCs ---------
    internal static void Bind(string fullNpcId, EntityId entity)
    {
        Bound[fullNpcId] = entity;
        QuestsInternals.Info($"NPC {fullNpcId} is entity {entity}");
    }

    /// <summary>A new world: entity ids mean nothing there.</summary>
    internal static void ClearBindings() => Bound.Clear();

    internal static bool IsBound(QuestNpcDefinition d, EntityId entity) => !entity.IsNone && Bound.GetValueOrDefault(d.FullId) == entity;

    /// <summary>The entity an NPC is bound to (none when there is no binding).</summary>
    internal static EntityId BoundEntity(QuestNpcDefinition d) => Bound.GetValueOrDefault(d.FullId);

    internal static QuestNpcDefinition? NpcOf(EntityId entity, string? template)
    {
        foreach (var d in Quests.NpcDefinitions)
            if (IsBound(d, entity)) return d;
        if (template is null) return null;
        foreach (var d in Quests.NpcDefinitions)
            if (d.Template?.Equals(template, StringComparison.OrdinalIgnoreCase) == true) return d;
        return null;
    }

    /// <summary>The NPC definition near a position, with its entity when one is found.</summary>
    private static (QuestNpcDefinition Def, EntityId Entity)? Near(WorldPosition at)
    {
        (QuestNpcDefinition, EntityId)? best = null;
        var bestD = double.MaxValue;
        foreach (var d in Quests.NpcDefinitions)
        {
            if (Bound.TryGetValue(d.FullId, out var b) && World.Available && World.Position(b) is { } bp && bp.DistanceTo(at) <= d.TalkRange && bp.DistanceTo(at) < bestD)
            {
                best = (d, b);
                bestD = bp.DistanceTo(at);
                continue;
            }
            if (d.Template is { Length: > 0 } t && Spawn.Available)
            {
                foreach (var e in Spawn.FindByName(t, at, d.TalkRange, 4))
                {
                    if (Spawn.Inspect(e) is not { } info || info.TemplateName?.Equals(t, StringComparison.OrdinalIgnoreCase) != true) continue;
                    var dist = info.Position.DistanceTo(at);
                    if (dist >= bestD) continue;
                    best = (d, e);
                    bestD = dist;
                }
            }
            if (d.At is { } p && p.DistanceTo(at) <= d.TalkRange && p.DistanceTo(at) < bestD)
            {
                best = (d, EntityId.None);
                bestD = p.DistanceTo(at);
            }
        }
        return best;
    }

    // ---------------------------------------------------------------------------------------------- opening ---------

    /// <summary>Opens a dialogue for a player in game (at <paramref name="node"/>, default its start). Replaces a
    /// conversation they had open. Tick thread.</summary>
    [TickThreadOnly]
    public static ConversationResult Open(EntityId player, string dialogue, EntityId npc = default, string? node = null)
    {
        Game.AssertTickThread();
        var d = Find(dialogue);
        return d is null ? ConversationResult.No($"no dialogue \"{dialogue}\"") : Open(QuestHolder.Of(player), d, npc, node);
    }

    internal static ConversationResult Open(QuestHolder h, DialogueDefinition d, EntityId npc, string? node, QuestNpcDefinition? def = null)
    {
        if (h.IsNone || h.IsWorld) return ConversationResult.No("a conversation needs a player");
        node ??= d.Start;
        if (!d.Nodes.ContainsKey(node)) return ConversationResult.No($"dialogue {d.FullId} has no node \"{node}\"");
        var s = new Session { Id = ++_next, Holder = h, Def = d, Node = node, Npc = npc, NpcDef = def ?? NpcForEntity(npc) };
        if (!npc.IsNone && World.Available) s.At = World.Position(npc);
        s.At ??= s.NpcDef?.At;
        Sessions[h.Key] = s;
        QuestsInternals.Info($"{h.Key} opens conversation {d.FullId} ({s.Id}) with {(npc.IsNone ? s.NpcDef?.Id ?? "nobody" : npc.ToString())}");
        Show(s, true);
        return ConversationResult.Yes;
    }

    private static QuestNpcDefinition? NpcForEntity(EntityId npc)
    {
        if (npc.IsNone) return null;
        try
        {
            return NpcOf(npc, Spawn.Available ? Spawn.Inspect(npc)?.TemplateName : null);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or SpawnException)
        {
            return null;
        }
    }

    /// <summary>A player talks to the NPC near them (or, for a synthetic holder, the one named): a conversation, or
    /// the quests that NPC offers and takes. Tick thread.</summary>
    [TickThreadOnly]
    public static ConversationResult Talk(QuestHolder h, string? npc = null)
    {
        Game.AssertTickThread();
        if (h.IsNone || h.IsWorld) return ConversationResult.No("a conversation needs a player");
        QuestNpcDefinition? def;
        EntityId entity = default;
        if (npc is not null && SyntheticInput.IsSynthetic(h))
        {
            def = Quests.FindNpcDef(npc, null);
            if (def is null) return ConversationResult.No($"no NPC \"{npc}\"");
        }
        else
        {
            if (QuestObjectives.PositionOf(h) is not { } at) return ConversationResult.No("your position is unknown");
            if (Near(at) is not { } found) return ConversationResult.No("there is nobody to talk to here");
            def = found.Def;
            entity = found.Entity;
        }
        return TalkTo(h, def, entity);
    }

    private static ConversationResult TalkTo(QuestHolder h, QuestNpcDefinition def, EntityId entity)
    {
        if (def.Dialogue is { } name)
        {
            var d = FindDefinition(name, def.ModId);
            if (d is null) return ConversationResult.No($"{def.FullId}: no dialogue \"{name}\"");
            // its lines are in the game's own menu: that is where this conversation happens
            if (d.Native && !SyntheticInput.IsSynthetic(h)) return ConversationResult.No($"talk to {def.Name?.For(Quests.ChatLanguage) ?? def.Id} in the game");
            QuestObjectives.OnTalk(h, entity, def.Template, def, 0, null);
            return Open(h, d, entity, null, def);
        }
        QuestObjectives.OnTalk(h, entity, def.Template, def, 0, null);
        return AutoTalk(h, def, entity);
    }

    /// <summary>An NPC with no dialogue of its own: "accept" for the quests it gives, "hand over" for the steps it takes.</summary>
    private static ConversationResult AutoTalk(QuestHolder h, QuestNpcDefinition def, EntityId entity)
    {
        var node = new DialogueNode { Text = def.Name ?? new UiText(def.Id) };
        foreach (var q in Quests.Definitions)
        {
            if (!SyntheticInput.IsSynthetic(h) && NativeQuests.ShownByGame(q)) continue;  // its topics are in the game's menu
            if (q.Giver is { } g && Quests.FindNpcDef(g, q.ModId)?.FullId == def.FullId && Quests.CanStart(q, h))
                node.Options.Add(new DialogueOption
                {
                    Text = Prefixed(("en", "Accept: "), ("de", "Annehmen: "), q.Name),
                    Do = [new QuestAction { Kind = QuestActionKind.Start, Name = q.FullId }],
                });
            if (Quests.BookOf(h).Quests.GetValueOrDefault(q.FullId) is { Status: QuestStatus.Active } rec && Quests.CurrentStep(q, rec) is { } step &&
                step.Objectives.Any(o => o.Kind == QuestObjectiveKind.Deliver && o.To is { } to && Quests.FindNpcDef(to, q.ModId)?.FullId == def.FullId))
                node.Options.Add(new DialogueOption
                {
                    Text = step.Text,
                    Do = [new QuestAction { Kind = QuestActionKind.Advance, Name = $"{q.FullId}.{step.Id}" }],
                });
        }
        if (node.Options.Count == 0) return ConversationResult.No($"{def.Name?.For(Quests.ChatLanguage) ?? def.Id} has nothing for you right now");
        node.Options.Add(new DialogueOption { Text = CloseText });
        var d = new DialogueDefinition { Id = "auto", Start = "auto" };
        d.ModId = def.ModId;
        d.Nodes["auto"] = node;
        return Open(h, d, entity, null, def);
    }

    private static UiText Prefixed((string L, string T) en, (string L, string T) de, UiText name)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in name.Languages.Append(en.L).Append(de.L).Distinct(StringComparer.OrdinalIgnoreCase))
            o[k] = (k.StartsWith(de.L, StringComparison.OrdinalIgnoreCase) ? de.T : en.T) + name.For(k);
        return new UiText(o);
    }

    /// <summary>The game's dialogue reached an NPC that has a Kiln conversation.</summary>
    internal static void OnGameLine(QuestHolder h, DialogueLineEvent e, QuestNpcDefinition? def)
    {
        var open = Sessions.ContainsKey(h.Key);
        if (open && e.IsFarewell)
        {
            CloseFor(h, false);
            return;
        }
        if (open || e.IsFarewell || def?.Dialogue is not { } name) return;
        // a native dialogue's lines are in the game's menu; a chat conversation starts here for players the menu cannot reach
        // refused (not a player, the dialogue lacks the node): the game's own dialogue goes on, nothing waits for it
        if (FindDefinition(name, def.ModId) is { Native: false } d) _ = Open(h, d, e.Npc, null, def);
    }

    // ---------------------------------------------------------------------------------------------- showing --------
    private static void Show(Session s, bool entering)
    {
        var node = s.Def.Nodes[s.Node];
        var ctx = new QuestContext(s.Holder, null, s.Npc, s.Def.ModId);
        s.Shown = new List<int>();
        for (var i = 0; i < node.Options.Count; i++)
            if (node.Options[i].If is not { } c || QuestActions.Holds(c, ctx)) s.Shown.Add(i);
        s.LastActive = Environment.TickCount64;
        Send(s, node);
        if (!entering) return;
        QuestObjectives.OnTalk(s.Holder, s.Npc, s.NpcDef?.Template, s.NpcDef, 0, s.Node);
        if (node.Do.Count > 0)
            _ = QuestActions.RunAll(node.Do, new QuestActionContext(s.Holder, null, s.Npc, s.Def.ModId), $"{s.Def.FullId}/{s.Node}", true);
    }

    private static void Send(Session s, DialogueNode node)
    {
        var speaker = node.Speaker ?? s.NpcDef?.Name;
        var lang = Quests.ChatLanguage;
        var who = speaker?.For(lang);
        QuestPresent.Raw(s.Holder, who is { Length: > 0 } ? $"[{who}] {node.Text.For(lang)}" : node.Text.For(lang));
        if (node.Options.Count == 0)
        {
            QuestPresent.Raw(s.Holder, "(/talk to go on)");
            return;
        }
        for (var i = 0; i < s.Shown.Count; i++) QuestPresent.Raw(s.Holder, $"  {i + 1}) {node.Options[s.Shown[i]].Text.For(lang)}");
        QuestPresent.Raw(s.Holder, "(/talk <number> to answer, /talk end to leave)");
    }

    /// <summary>What a holder is being shown; null when no conversation is open. Tick thread.</summary>
    [TickThreadOnly]
    public static ConversationView? View(QuestHolder h)
    {
        Game.AssertTickThread();
        if (!Sessions.TryGetValue(h.Key, out var s)) return null;
        var n = s.Def.Nodes[s.Node];
        return new ConversationView(s.Id, s.Def.FullId, s.Node, n.Speaker ?? s.NpcDef?.Name, n.Text,
            s.Shown.Select(i => (i, n.Options[i].Text)).ToArray(), s.Npc);
    }

    // ---------------------------------------------------------------------------------------------- choosing -------

    /// <summary>A choice from /talk: <paramref name="shown"/> is the number the player saw (1-based, among the options
    /// shown to them); 0 continues a node without options.</summary>
    internal static Task<ConversationResult> ChooseShown(QuestHolder h, int shown)
    {
        if (!Sessions.TryGetValue(h.Key, out var s)) return Task.FromResult(ConversationResult.No("you are not in a conversation"));
        if (shown == 0) return Choose(h, s.Id, -1);
        return shown >= 1 && shown <= s.Shown.Count
            ? Choose(h, s.Id, s.Shown[shown - 1])
            : Task.FromResult(ConversationResult.No($"answer with a number from 1 to {s.Shown.Count}"));
    }

    /// <summary>Runs a choice: <paramref name="option"/> is the option's place in the node (-1: continue a node without
    /// options). The option's condition is checked now, not when it was shown. Tick thread; completes on it.</summary>
    [TickThreadOnly]
    public static async Task<ConversationResult> Choose(QuestHolder h, long conversation, int option)
    {
        Game.AssertTickThread();
        if (!Sessions.TryGetValue(h.Key, out var s) || (conversation != 0 && s.Id != conversation)) return ConversationResult.No("this conversation is over");
        if (s.Busy) return ConversationResult.No("wait a moment");
        var node = s.Def.Nodes[s.Node];
        s.LastActive = Environment.TickCount64;
        if (TooFar(s))
        {
            CloseFor(h, true);
            return ConversationResult.No("you walked away from the conversation");
        }
        if (node.Options.Count == 0 || option < 0)
        {
            if (node.Options.Count != 0) return ConversationResult.No("choose one of the options");
            GoTo(s, node.Goto);
            return ConversationResult.Yes;
        }
        if (option >= node.Options.Count) return Refuse(s, node, $"no option {option}");
        var opt = node.Options[option];
        var ctx = new QuestActionContext(h, null, s.Npc, s.Def.ModId);
        if (opt.If is { } c && !QuestActions.Holds(c, ctx.Condition))
        {
            QuestsInternals.Info($"{h.Key} {s.Def.FullId}/{s.Node}[{option}]: refused: the condition does not hold");
            return Refuse(s, node, "you cannot choose that now");
        }
        if (Events.HasSubscribers<ConversationOptionEvent>() &&
            Events.Post(new ConversationOptionEvent(h, s.Def, s.Node, option, s.Npc)) is { Canceled: true } ev)
            return Refuse(s, node, ev.RefuseMessage ?? "you cannot choose that now");
        QuestsInternals.Info($"{h.Key} {s.Def.FullId}/{s.Node}[{option}] chosen");
        s.Busy = true;
        QuestRun run;
        try
        {
            run = await QuestActions.RunAll(opt.Do, ctx, $"{s.Def.FullId}/{s.Node}[{option}]", false).OnTick();
        }
        finally
        {
            s.Busy = false;
        }
        // an action may have opened another conversation or the player may have left
        if (!Sessions.TryGetValue(h.Key, out var cur) || !ReferenceEquals(cur, s)) return ConversationResult.Yes;
        if (!run.Ok) return Refuse(s, node, run.Reason);
        GoTo(s, opt.Goto);
        return ConversationResult.Yes;
    }

    private static ConversationResult Refuse(Session s, DialogueNode node, string why)
    {
        Tell(s.Holder, why);
        Show(s, false);
        return ConversationResult.No(why);
    }

    private static void Tell(QuestHolder h, string text) => QuestPresent.Raw(h, text);

    private static void GoTo(Session s, string? next)
    {
        if (next is null or "end" || !s.Def.Nodes.ContainsKey(next))
        {
            CloseFor(s.Holder, true);
            return;
        }
        s.Node = next;
        Show(s, true);
    }

    private static bool TooFar(Session s)
    {
        if (s.At is not { } at || QuestObjectives.PositionOf(s.Holder) is not { } p) return false;
        return p.DistanceTo(at) > GiveUpDistance;
    }

    /// <summary>Ends a holder's conversation. Tick thread.</summary>
    [TickThreadOnly]
    public static void Close(QuestHolder h)
    {
        Game.AssertTickThread();
        CloseFor(h, true);
    }

    internal static void CloseFor(QuestHolder h, bool tell)
    {
        if (!Sessions.Remove(h.Key, out var s)) return;
        QuestsInternals.Info($"{h.Key} closes conversation {s.Def.FullId} ({s.Id})");
        if (tell) QuestPresent.Raw(h, "(the conversation is over)");
    }

    internal static void Maintain()
    {
        if (Sessions.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var s in Sessions.Values.ToArray())
            if (now - s.LastActive > IdleMs || TooFar(s)) CloseFor(s.Holder, true);
    }

    // ------------------------------------------------------------------------------------------- native options ----

    /// <summary>The game's own dialogue chose an option that a mod's quest file defines (Kiln.Quests/Native maps the line
    /// to the option): checks its <c>if</c> and runs its <c>do</c>; the game's menu goes on by itself, so <c>goto</c> is
    /// ignored. Tick thread; completes on it.</summary>
    [TickThreadOnly]
    public static async Task<ConversationResult> ApplyOption(EntityId player, EntityId npc, string fullDialogueId, string nodeId, int optionIndex)
    {
        Game.AssertTickThread();
        var d = Find(fullDialogueId);
        if (d is null) return ConversationResult.No($"no dialogue \"{fullDialogueId}\"");
        if (!d.Nodes.TryGetValue(nodeId, out var node)) return ConversationResult.No($"dialogue {d.FullId} has no node \"{nodeId}\"");
        if (optionIndex < 0 || optionIndex >= node.Options.Count) return ConversationResult.No($"node {nodeId} has no option {optionIndex}");
        var h = QuestHolder.Of(player);
        if (h.IsNone) return ConversationResult.No("no such player");
        var opt = node.Options[optionIndex];
        var ctx = new QuestActionContext(h, null, npc, d.ModId);
        if (opt.If is { } c && !QuestActions.Holds(c, ctx.Condition)) return ConversationResult.No("the option's condition does not hold");
        var run = await QuestActions.RunAll(opt.Do, ctx, $"{d.FullId}/{nodeId}[{optionIndex}]", false).OnTick();
        return run.Ok ? ConversationResult.Yes : ConversationResult.No(run.Reason);
    }
}
