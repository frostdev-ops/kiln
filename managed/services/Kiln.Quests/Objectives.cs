// SPDX-License-Identifier: MIT
// Kiln.Quests objectives: small evaluators on Kiln events and polls (tick thread). Every real source (deaths, spawns,
// crafts, builds, dialogue lines, positions, inventories) ends in the same functions as the synthetic input
// (QuestInput), so lab tests without a player run the same code a player's actions do.
using System.Collections.Concurrent;

namespace Kiln;

internal static class QuestObjectives
{
    private static int _kinds;
    private static bool _tags, _deaths, _destroys, _crafts, _builds, _lines, _players;
    private static readonly HashSet<uint> Counted = new();
    private static readonly Dictionary<string, (WorldPosition At, long Until)> MarkerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(EntityId Entity, string Template, string? Tag, long Until)> RecentSpawns = new();

    private static bool Has(QuestObjectiveKind k) => (_kinds & (1 << (int)k)) != 0;

    /// <summary>Which objectives exist decides which events the engine listens to: a server without quests pays nothing.</summary>
    public static void Rebuild()
    {
        var kinds = 0;
        var tags = false;
        var destroys = false;
        var any = false;
        foreach (var q in Quests.Definitions)
        {
            any = true;
            foreach (var s in q.Steps)
                foreach (var o in s.Objectives)
                {
                    kinds |= 1 << (int)o.Kind;
                    tags |= o.Tag is not null;
                    destroys |= o.CountDestroyed;
                }
        }
        _kinds = kinds;
        _tags = tags;
        bool npcs;
        lock (Quests.Gate) npcs = Quests.NpcRegistry.Count > 0;
        QuestsInternals.RunAs(null, () =>
        {
            Try(ref _deaths, Has(QuestObjectiveKind.Kill), () => { Events.On<EntityDeathEvent>(OnDeath); Events.On<EntitySpawnedEvent>(OnSpawned); });
            Try(ref _destroys, destroys, () => Events.On<EntityDestroyedEvent>(OnDestroyed));
            Try(ref _crafts, Has(QuestObjectiveKind.Craft), () => Events.On<ItemCraftedEvent>(OnCrafted));
            Try(ref _builds, Has(QuestObjectiveKind.Build), () => Events.On<BuildEvent>(OnBuilt));
            Try(ref _lines, Has(QuestObjectiveKind.Talk) || npcs, () => Events.On<DialogueLineEvent>(OnLine));
            Try(ref _players, any || npcs, () => { Events.On<PlayerJoinedEvent>(OnJoined); Events.On<PlayerLeftEvent>(OnLeft); });
        });
    }

    private static void Try(ref bool done, bool needed, Action subscribe)
    {
        if (done || !needed) return;
        done = true;
        try
        {
            subscribe();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or NotSupportedException or TypeInitializationException)
        {
            QuestsInternals.Warn($"an objective source is unavailable: {e.Message}");
        }
    }

    // ------------------------------------------------------------------------------------------ positions, parties ----
    public static WorldPosition? PositionOf(QuestHolder h)
    {
        if (SyntheticInput.IsSynthetic(h)) return SyntheticInput.PositionOf(h);
        if (h.Entity.IsNone || !Players.Available) return null;
        try
        {
            return Players.PositionOf(h.Entity);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The other holders within <paramref name="radius"/> metres of a holder.</summary>
    public static IReadOnlyList<QuestHolder> PartyOf(QuestHolder h, double radius)
    {
        if (PositionOf(h) is not { } at) return [];
        var list = new List<QuestHolder>();
        foreach (var o in Quests.Online().Concat(SyntheticInput.Known))
            if (o.Key != h.Key && PositionOf(o) is { } p && p.DistanceTo(at) <= radius) list.Add(o);
        return list;
    }

    // ----------------------------------------------------------------------------------------------- credit ------
    private static void Credit(QuestObjectiveKind kind, QuestHolder? actor, bool share, Func<QuestDefinition, QuestObjective, int> amount)
    {
        if (!Has(kind)) return;
        Apply(QuestHolder.World, kind, amount, static q => q.Scope == QuestScope.World);
        if (actor is not { IsNone: false, IsWorld: false } a) return;
        Apply(a, kind, amount, static q => q.Scope != QuestScope.World);
        if (!share || PositionOf(a) is not { } at) return;
        foreach (var o in Quests.Online().Concat(SyntheticInput.Known))
        {
            if (o.Key == a.Key || PositionOf(o) is not { } p) continue;
            var d = p.DistanceTo(at);
            Apply(o, kind, amount, q => q.Scope == QuestScope.Party && d <= q.PartyRadius);
        }
    }

    private static void Apply(QuestHolder h, QuestObjectiveKind kind, Func<QuestDefinition, QuestObjective, int> amount, Func<QuestDefinition, bool> filter)
    {
        var book = Quests.BookOf(h);
        if (book.Quests.Count == 0) return;
        foreach (var (id, rec) in book.Quests.ToArray())
        {
            if (rec.Status != QuestStatus.Active || Quests.Find(id) is not { } q || !filter(q)) continue;
            if (Quests.CurrentStep(q, rec) is not { } step) continue;
            for (var i = 0; i < step.Objectives.Count; i++)
            {
                var o = step.Objectives[i];
                if (o.Kind != kind) continue;
                var n = amount(q, o);
                if (n <= 0) continue;
                Set(q, h, rec, step, i, o, rec.Counters.GetValueOrDefault(QuestRecord.CounterKey(step.Id, i)) + n);
                if (rec.Status != QuestStatus.Active || rec.StepId != step.Id) break;
            }
        }
    }

    private static void Set(QuestDefinition q, QuestHolder h, QuestRecord rec, QuestStep step, int index, QuestObjective o, int value)
    {
        var key = QuestRecord.CounterKey(step.Id, index);
        var old = rec.Counters.GetValueOrDefault(key);
        value = Math.Clamp(value, 0, o.Count);
        if (value == old)
        {
            // already full: a step that was refused (QuestStepEvent) or moved by a changed definition still has to finish
            if (value >= o.Count) Quests.CheckStep(q, h, rec, step);
            return;
        }
        rec.Counters[key] = value;
        Quests.BookOf(h).Revision++;
        if (Events.HasSubscribers<QuestObjectiveProgressEvent>())
            Events.Post(new QuestObjectiveProgressEvent(q, h, step, index, o, old, value));
        QuestsInternals.Info($"{h.Key} {q.FullId}.{step.Id}[{index}] {o.Kind}: {old} -> {value}/{o.Count}");
        if (value > old) QuestPresent.Progress(h, q, step, o, value);
        Quests.CheckStep(q, h, rec, step);
    }

    // ---------------------------------------------------------------------------------------------- the sources ----
    private static void OnSpawned(EntitySpawnedEvent e)
    {
        if (e.TemplateName is null && e.Tag is null) return;
        // a dead entity may be gone by the time its death is reported: remember what Kiln spawned for a while
        var now = Environment.TickCount64;
        RecentSpawns.RemoveAll(s => s.Until < now);
        if (RecentSpawns.Count < 4096) RecentSpawns.Add((e.Entity, e.TemplateName ?? "", e.Tag, now + 600_000));
    }

    private static void OnDeath(EntityDeathEvent e)
    {
        if (e.IsPlayer) return;
        if (!Counted.Add(e.Victim.Value)) return;
        if (Counted.Count > 4096) Counted.Clear();
        var (template, tag) = Identify(e.Victim);
        // the killer, else what dealt the blow (a thrown or fired weapon can report its owner only as the source)
        var who = e.Killer.IsPlayer ? e.Killer : e.Source.IsPlayer ? e.Source : EntityId.None;
        var killer = who.IsNone ? default : QuestHolder.Of(who);
        OnKill(killer.IsNone ? null : killer, template, tag, e.Position, false);
    }

    private static void OnDestroyed(EntityDestroyedEvent e)
    {
        if (e.Reason != "destroy" || !Counted.Add(e.Entity.Value)) return;
        if (Counted.Count > 4096) Counted.Clear();
        var known = RecentSpawns.FindLast(s => s.Entity == e.Entity);
        OnKill(null, e.TemplateName ?? (known.Template is { Length: > 0 } t ? t : null), known.Tag, e.Position, true);
    }

    private static (string? Template, string? Tag) Identify(EntityId victim)
    {
        string? template = null, tag = null;
        var known = RecentSpawns.FindLast(s => s.Entity == victim);
        if (known.Template is { Length: > 0 }) template = known.Template;
        tag = known.Tag;
        if (!Spawn.Available) return (template, tag);
        try
        {
            template ??= Spawn.Inspect(victim)?.TemplateName;
            if (_tags && tag is null) tag = Spawn.Tracked.FirstOrDefault(t => t.Entity == victim)?.Tag;
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException or SpawnException)
        {
            QuestsInternals.Warn($"who died: {x.Message}");
        }
        return (template, tag);
    }

    public static void OnKill(QuestHolder? killer, string? template, string? tag, WorldPosition? at, bool destroyed) =>
        Credit(QuestObjectiveKind.Kill, killer, true, (_, o) => KillMatches(o, template, tag, at, destroyed) ? 1 : 0);

    private static bool KillMatches(QuestObjective o, string? template, string? tag, WorldPosition? at, bool destroyed)
    {
        if (destroyed && !o.CountDestroyed) return false;
        if (o.Template is { } t && !t.Equals(template, StringComparison.OrdinalIgnoreCase)) return false;
        if (o.Family is { } f && template?.Contains(f, StringComparison.OrdinalIgnoreCase) != true) return false;
        if (o.Tag is { } g && !g.Equals(tag, StringComparison.Ordinal)) return false;
        if (o.Near is { } n && (at is not { } p || p.DistanceTo(n) > o.Radius)) return false;
        return true;
    }

    private static void OnCrafted(ItemCraftedEvent e)
    {
        if (!e.Succeeded || !e.ByPlayer) return;
        var h = QuestHolder.Of(e.Operator);
        Credit(QuestObjectiveKind.Craft, h, false, (_, o) =>
        {
            if (o.Recipe is { } r) return Knowledge.TryParseId(r, out var id) && id == e.RecipeId ? 1 : 0;
            if (o.Item is not { } item || ItemIdOf(item) is not { } wanted) return 0;
            return e.Outputs.Where(s => s.ItemId == wanted).Sum(s => s.Count);
        });
    }

    private static void OnBuilt(BuildEvent e)
    {
        if (e.Outcome != BuildOutcome.Done || !e.IsPlacement || e.Player.IsNone) return;
        Credit(QuestObjectiveKind.Build, QuestHolder.Of(e.Player), true, (_, o) => o.Item is { } item && ItemIdOf(item) == e.ItemId ? 1 : 0);
    }

    public static void OnCraftInput(QuestHolder h, string item, int count) =>
        Credit(QuestObjectiveKind.Craft, h, false, (_, o) => o.Item is { } i && i.Equals(item, StringComparison.OrdinalIgnoreCase) ? count : 0);

    public static void OnBuildInput(QuestHolder h, string prop, int count) =>
        Credit(QuestObjectiveKind.Build, h, true, (_, o) => o.Item is { } i && i.Equals(prop, StringComparison.OrdinalIgnoreCase) ? count : 0);

    public static void OnSignal(string name, QuestHolder? holder, int amount)
    {
        if (!Has(QuestObjectiveKind.Custom)) return;
        Func<QuestDefinition, QuestObjective, int> f = (_, o) => o.Signal == name ? amount : 0;
        if (holder is { } h)
        {
            Credit(QuestObjectiveKind.Custom, h, true, f);
            return;
        }
        Apply(QuestHolder.World, QuestObjectiveKind.Custom, f, static q => q.Scope == QuestScope.World);
        foreach (var o in Quests.Online()) Apply(o, QuestObjectiveKind.Custom, f, static q => q.Scope != QuestScope.World);
    }

    /// <summary>A player talked to an NPC: the game's dialogue line, or a chat conversation.</summary>
    public static void OnTalk(QuestHolder h, EntityId entity, string? template, QuestNpcDefinition? def, uint line, string? node) =>
        Credit(QuestObjectiveKind.Talk, h, false, (q, o) => TalkMatches(q, o, entity, template, def, line, node) ? 1 : 0);

    private static bool TalkMatches(QuestDefinition q, QuestObjective o, EntityId entity, string? template, QuestNpcDefinition? def, uint line, string? node)
    {
        if (o.Line is { } l && (line == 0 || ResolveLine(q.ModId, l) != line)) return false;
        if (o.Node is { } n && n != node) return false;
        return o.Npc is null || NpcIs(o.Npc, q.ModId, entity, template, def);
    }

    /// <summary>A line named by knowledge ("line:…", "0x…") or by Kiln.Quests/Native.</summary>
    public static uint? ResolveLine(string modId, string text)
    {
        try
        {
            if (Knowledge.Available && Knowledge.TryResolve(text, out var k)) return k.Id;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
        }
        return Quests.LineResolver?.Invoke(modId, text);
    }

    /// <summary>Is this entity / definition the NPC named in a quest file?</summary>
    public static bool NpcIs(string name, string modId, EntityId entity, string? template, QuestNpcDefinition? def)
    {
        if (Quests.FindNpcDef(name, modId) is { } d)
        {
            if (def is not null) return d.FullId == def.FullId;
            if (Dialogues.IsBound(d, entity)) return true;
            return d.Template is { } t && t.Equals(template, StringComparison.OrdinalIgnoreCase);
        }
        var plain = name.StartsWith("npc:", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        if (plain.EndsWith(".unlock", StringComparison.OrdinalIgnoreCase)) plain = plain[..^7];
        return template?.Contains(plain, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void OnLine(DialogueLineEvent e)
    {
        if (e.Action != DialogueAction.Start) return;
        var h = QuestHolder.Of(e.PlayerEntity);
        if (h.IsNone) return;
        string? template = null;
        try
        {
            if (Spawn.Available) template = Spawn.Inspect(e.Npc)?.TemplateName;
        }
        catch (Exception x) when (x is NotSupportedException or InvalidOperationException or SpawnException)
        {
            QuestsInternals.Warn($"which NPC: {x.Message}");
        }
        var def = Dialogues.NpcOf(e.Npc, template);
        OnTalk(h, e.Npc, template, def, e.LineId, null);
        Dialogues.OnGameLine(h, e, def);
    }

    private static void OnJoined(PlayerJoinedEvent e)
    {
        var h = QuestHolder.Of(e.Player);
        Quests.PayPending(h);
        QuestPlaces.Restore(h);
    }

    private static void OnLeft(PlayerLeftEvent e)
    {
        Dialogues.CloseFor(QuestHolder.Of(e.Player), false);
        QuestPlaces.Left(QuestHolder.Of(e.Player));
    }

    // ---------------------------------------------------------------------------------------------------- polls ----
    /// <summary>Every ~0.5 s: positions.</summary>
    public static void PollFast()
    {
        if (!Has(QuestObjectiveKind.Reach)) return;
        foreach (var h in Quests.Online())
        {
            if (PositionOf(h) is not { } p) continue;
            Evaluate(h, p, false);
            Evaluate(QuestHolder.World, p, false);
        }
    }

    /// <summary>Every ~1 s: inventories, knowledge, custom counters.</summary>
    public static void PollSlow()
    {
        if (!Has(QuestObjectiveKind.Collect) && !Has(QuestObjectiveKind.Knowledge) && !Has(QuestObjectiveKind.Custom) && !Has(QuestObjectiveKind.Reach)) return;
        foreach (var h in Quests.Online()) Evaluate(h, null, true);
        Evaluate(QuestHolder.World, null, true);
    }

    /// <summary>Objectives whose value is a state of the world rather than an event.</summary>
    public static void Evaluate(QuestHolder h, WorldPosition? at, bool slow)
    {
        var book = Quests.BookOf(h);
        if (book.Quests.Count == 0) return;
        foreach (var (id, rec) in book.Quests.ToArray())
        {
            if (rec.Status != QuestStatus.Active || Quests.Find(id) is not { } q) continue;
            if ((q.Scope == QuestScope.World) != h.IsWorld) continue;
            if (Quests.CurrentStep(q, rec) is not { } step) continue;
            for (var i = 0; i < step.Objectives.Count; i++)
            {
                var o = step.Objectives[i];
                var value = -1;
                switch (o.Kind)
                {
                    case QuestObjectiveKind.Reach when at is { } p:
                        if (ReachTarget(q, step, rec, o, h, p) is { } target && target.DistanceTo(p) <= o.Radius) value = 1;
                        break;
                    case QuestObjectiveKind.Collect when slow:
                        value = Inventory(h, o.Item!);
                        break;
                    case QuestObjectiveKind.Knowledge when slow:
                        value = KnowledgeHolds(h, o.Knowledge!) ? 1 : 0;
                        break;
                    case QuestObjectiveKind.Custom when slow && o.Progress is not null:
                        value = SafeProgress(o, new QuestContext(h, q, EntityId.None, q.ModId));
                        break;
                }
                if (value >= 0) Set(q, h, rec, step, i, o, value);
                if (rec.Status != QuestStatus.Active || rec.StepId != step.Id) break;
            }
            // a step held open a moment (Quests.CheckStep) finishes now, also when the player walked off the spot meanwhile
            if (slow && rec.Status == QuestStatus.Active && rec.StepId == step.Id) Quests.CheckStep(q, h, rec, step);
        }
    }

    private static int SafeProgress(QuestObjective o, QuestContext c)
    {
        try
        {
            return o.Progress!(c);
        }
        catch (Exception e)
        {
            QuestsInternals.Warn($"a custom objective's code threw: {e.Message}");
            return -1;
        }
    }

    public static bool KnowledgeHolds(QuestHolder h, string name)
    {
        if (!Knowledge.Available) return false;
        try
        {
            return Knowledge.Has(h.Entity, name);
        }
        catch (Exception e) when (e is KeyNotFoundException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static WorldPosition? ReachTarget(QuestDefinition q, QuestStep step, QuestRecord rec, QuestObjective o, QuestHolder h, WorldPosition from)
    {
        // a place Kiln settled on the ground counts by the map: a player on the slope above it has reached it
        if ((o.At is not null || o.Site) && QuestPlaces.KnownFor(q, step) is { } settled) return settled with { Y = from.Y };
        if (o.Site) return null;
        if (o.At is { } at) return at;
        if (o.Offset is { } off)
        {
            // a holder without a position at the step's start: the first position seen is the origin
            rec.StepOrigin ??= [from.X, from.Y, from.Z];
            return new WorldPosition(rec.StepOrigin[0] + off.X, rec.StepOrigin[1] + off.Y, rec.StepOrigin[2] + off.Z);
        }
        if (o.Marker is not { } m || !Spawn.Available) return null;
        var now = Environment.TickCount64;
        if (MarkerCache.TryGetValue(m, out var c) && c.Until > now) return c.At;
        try
        {
            var found = Spawn.FindByName(m, from, 1e9, 1);
            if (found.Count > 0 && Spawn.Inspect(found[0]) is { } info)
            {
                MarkerCache[m] = (info.Position, now + 30_000);
                return info.Position;
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or SpawnException)
        {
            QuestsInternals.Warn($"reach marker {m}: {e.Message}");
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------- items ----
    private static readonly ConcurrentDictionary<string, uint> ItemIds = new(StringComparer.OrdinalIgnoreCase);

    public static uint? ItemIdOf(string item)
    {
        if (ItemIds.TryGetValue(item, out var id)) return id;
        if (!Items.Available) return null;
        var info = Items.Lookup(item);
        if (info is null) return null;
        ItemIds[item] = info.Id.Value;
        return info.Id.Value;
    }

    /// <summary>How many of an item a holder has (a world holder: everyone in game together).</summary>
    public static int Inventory(QuestHolder h, string item)
    {
        if (h.IsWorld) return Quests.Online().Sum(o => Inventory(o, item));
        if (SyntheticInput.IsSynthetic(h)) return SyntheticInput.ItemCount(h, item);
        if (h.Entity.IsNone || ItemIdOf(item) is not { } id) return 0;
        try
        {
            return Items.Read(h.Entity)?.Count(new ItemId(id)) ?? 0;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>Takes a deliver objective's items. Returns null, or why not.</summary>
    public static async Task<string?> TakeForDeliver(QuestHolder h, QuestObjective o, int count)
    {
        var label = LabelOf(o.Item!);
        var have = Inventory(h, o.Item!);
        if (have < count) return $"you need {count} x {label} (you have {have})";
        if (SyntheticInput.IsSynthetic(h))
        {
            SyntheticInput.SetItemCount(h, o.Item!, have - count);
            return null;
        }
        if (h.IsWorld || h.Entity.IsNone) return "a player in game has to hand this over";
        if (ItemIdOf(o.Item!) is not { } id) return $"this server has no item {o.Item}";
        var r = await Items.Take(h.Entity, new ItemId(id), count).OnTick();
        return r.Status == ItemsStatus.Done ? null : $"could not take {count} x {label}: {r}";
    }

    public static string LabelOf(string item)
    {
        try
        {
            return Items.Available ? Items.Lookup(item)?.Label ?? item : item;
        }
        catch (Exception)
        {
            return item;
        }
    }
}

/// <summary>Feeds the engine the same way real events do, without a player: for lab tests, tools and mods with their own
/// sources. Tick thread. A holder from <see cref="QuestHolder.Synthetic"/> has a position, an inventory and a level only
/// through these calls. Lab and test API (KILNX001): it may change or go in any release.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX001")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class QuestInput
{
    /// <summary>The synthetic holders seen so far.</summary>
    public static IReadOnlyCollection<QuestHolder> Known => SyntheticInput.Known;

    public static bool IsSynthetic(QuestHolder h) => SyntheticInput.IsSynthetic(h);

    /// <summary>A creature died (or was destroyed, with <paramref name="destroyed"/>): the same path as the game's deaths.</summary>
    public static void Kill(QuestHolder? killer, string template, string? tag = null, WorldPosition? at = null, bool destroyed = false)
    {
        Game.AssertTickThread();
        if (killer is { } k) SyntheticInput.See(k);
        QuestObjectives.OnKill(killer, template, tag, at, destroyed);
    }

    /// <summary>Sets how many of an item a synthetic holder has and evaluates their collect objectives.</summary>
    public static void SetInventory(QuestHolder h, string item, int count)
    {
        Game.AssertTickThread();
        SyntheticInput.See(h);
        SyntheticInput.SetItemCount(h, item, count);
        QuestObjectives.Evaluate(h, null, true);
    }

    /// <summary>Puts a synthetic holder at a position and evaluates reach objectives (their own and the world's).</summary>
    public static void Reach(QuestHolder h, WorldPosition at)
    {
        Game.AssertTickThread();
        SyntheticInput.See(h);
        SyntheticInput.SetPosition(h, at);
        QuestObjectives.Evaluate(h, at, false);
        QuestObjectives.Evaluate(QuestHolder.World, at, false);
    }

    /// <summary>A holder talked to an NPC (a definition id, or a template name). <paramref name="line"/>: the game's line id.</summary>
    public static void Talk(QuestHolder h, string npc, uint line = 0, string? node = null, string? modId = null)
    {
        Game.AssertTickThread();
        SyntheticInput.See(h);
        var def = Quests.FindNpcDef(npc, modId);
        QuestObjectives.OnTalk(h, EntityId.None, def?.Template ?? npc, def, line, node);
    }

    public static void Craft(QuestHolder h, string item, int count = 1)
    {
        Game.AssertTickThread();
        SyntheticInput.See(h);
        QuestObjectives.OnCraftInput(h, item, count);
    }

    public static void Build(QuestHolder h, string prop, int count = 1)
    {
        Game.AssertTickThread();
        SyntheticInput.See(h);
        QuestObjectives.OnBuildInput(h, prop, count);
    }

    /// <summary>A synthetic holder's character level (level conditions).</summary>
    public static void SetLevel(QuestHolder h, int level)
    {
        SyntheticInput.See(h);
        SyntheticInput.SetLevel(h, level);
    }

    /// <summary>Forgets synthetic positions, inventories and levels.</summary>
    public static void Clear() => SyntheticInput.Clear();
}

/// <summary>The synthetic holders' state behind <see cref="QuestInput"/>, for the engine's own reads (not experimental,
/// so Kiln's real paths need no suppression).</summary>
internal static class SyntheticInput
{
    private static readonly Dictionary<string, WorldPosition> Positions = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string, string), int> Inventories = new();
    private static readonly Dictionary<string, int> Levels = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, QuestHolder> KnownHolders = new(StringComparer.Ordinal);

    internal static IReadOnlyCollection<QuestHolder> Known => KnownHolders.Values.ToArray();

    internal static bool IsSynthetic(QuestHolder h) => h.Entity.IsNone && (h.Account & 0x8000000000000000ul) != 0;

    internal static void See(QuestHolder h)
    {
        if (IsSynthetic(h)) KnownHolders[h.Key] = h;
    }

    internal static void SetItemCount(QuestHolder h, string item, int count) => Inventories[(h.Key, item.ToLowerInvariant())] = count;
    internal static int ItemCount(QuestHolder h, string item) => Inventories.GetValueOrDefault((h.Key, item.ToLowerInvariant()));

    internal static void SetPosition(QuestHolder h, WorldPosition at) => Positions[h.Key] = at;
    internal static WorldPosition? PositionOf(QuestHolder h) => Positions.TryGetValue(h.Key, out var p) ? p : null;

    internal static void SetLevel(QuestHolder h, int level) => Levels[h.Key] = level;
    internal static int LevelOf(QuestHolder h) => Levels.GetValueOrDefault(h.Key, 1);

    internal static void Clear()
    {
        Positions.Clear();
        Inventories.Clear();
        Levels.Clear();
        KnownHolders.Clear();
    }
}
