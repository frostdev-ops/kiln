// SPDX-License-Identifier: MIT
// World progress on the event bus (docs/kiln/events.md). All posted on the tick thread, built only while someone
// subscribes, none cancelable (they report what the game already did). Sources:
//   FlameLevelChangedEvent      the progress entity's NetworkServerProgress, sampled every 30 steps;
//   SpireUnlockedEvent, ShrineFoundEvent, ShroudRootDestroyedEvent   KnowledgeChangedEvent of the fact's knowledge id;
//   BossKilledEvent             EntityDeathEvent of a boss (a template with a boss health bar), merged with the game's kill
//                               record (KnowledgeChangedEvent) when it has one;
//   ChestLootedEvent            the loaded chests' components, sampled every 30 steps;
//   QuestStatusChangedEvent     Kiln quests: QuestStateChangedEvent; vanilla quests: the game's evaluator, re-run for a
//                               player after a knowledge change (at most every 15 steps).
namespace Kiln;

/// <summary>Base of the progress events whose fact is knowledge: the world's or one character's.</summary>
public abstract class WorldProgressEvent : KilnEvent
{
    private protected WorldProgressEvent(EntityId target, ulong step)
    {
        Target = target;
        Step = step;
    }

    /// <summary>None: the world's knowledge changed; else the player whose knowledge changed.</summary>
    public EntityId Target { get; }
    public bool IsWorld => Target.IsNone;
    public Player? Player => Target.IsNone || !Players.Available ? null : Players.Find(Target);
    public ulong Step { get; }
}

/// <summary>The world's flame level changed (the Flame Altar's upgrade, or an admin's set-flame-level). Sampled every 30
/// steps (half a second), so it fires up to that late.</summary>
public sealed class FlameLevelChangedEvent : KilnEvent
{
    static FlameLevelChangedEvent() => WorldProgressPump.Start();

    internal FlameLevelChangedEvent(int oldLevel, FlameStatus flame)
    {
        OldLevel = oldLevel;
        Flame = flame;
    }

    public int OldLevel { get; }
    public int NewLevel => Flame.Level;
    public FlameStatus Flame { get; }
    public override string ToString() => $"flame level {OldLevel} -> {NewLevel}";
}

/// <summary>An Ancient Spire's knowledge was set: the world's unlock (<see cref="WorldProgressEvent.IsWorld"/>), a
/// character's twin of it (personal progression), or a character's "top reached" (<see cref="TopReached"/>).</summary>
public sealed class SpireUnlockedEvent : WorldProgressEvent
{
    static SpireUnlockedEvent() => WorldProgressPump.Start();

    internal SpireUnlockedEvent(Spire spire, EntityId target, bool top, ulong step) : base(target, step)
    {
        Spire = spire;
        TopReached = top;
    }

    public Spire Spire { get; }
    /// <summary>The character's own "top reached" knowledge, not the unlock.</summary>
    public bool TopReached { get; }
    public override string ToString() => $"{(IsWorld ? "world" : $"player {Target}")}: spire {Spire.Id} {(TopReached ? "top reached" : "unlocked")}";
}

/// <summary>A character found a Flame Shrine or Flame Sanctum (the game's record of it; exploration XP is paid with it).</summary>
public sealed class ShrineFoundEvent : WorldProgressEvent
{
    static ShrineFoundEvent() => WorldProgressPump.Start();

    internal ShrineFoundEvent(FlameShrine shrine, EntityId target, ulong step) : base(target, step) => Shrine = shrine;

    public FlameShrine Shrine { get; }
    public override string ToString() => $"{(IsWorld ? "world" : $"player {Target}")}: found {Shrine}";
}

/// <summary>A character destroyed a Shroud Root (an Elixir Well's when <see cref="ShroudRoot.ElixirWell"/>).</summary>
public sealed class ShroudRootDestroyedEvent : WorldProgressEvent
{
    static ShroudRootDestroyedEvent() => WorldProgressPump.Start();

    internal ShroudRootDestroyedEvent(ShroudRoot root, EntityId target, ulong step) : base(target, step) => Root = root;

    public ShroudRoot Root { get; }
    public override string ToString() => $"{(IsWorld ? "world" : $"player {Target}")}: destroyed {Root}";
}

/// <summary>A boss died: a creature the game gives a boss health bar. From the game's death event (with the
/// <see cref="Entity"/> and the <see cref="Killer"/>), or from the game's kill record alone when no death was seen (a
/// kill far from Kiln's view: <see cref="Entity"/> None) about 2 s after the record. One event per kill.</summary>
public sealed class BossKilledEvent : KilnEvent
{
    static BossKilledEvent() => WorldProgressPump.Start();

    internal BossKilledEvent(Boss? boss, string? template, EntityId entity, EntityId killer, WorldPosition? position, bool recorded, ulong step)
    {
        Boss = boss;
        Template = template;
        Entity = entity;
        Killer = killer;
        Position = position;
        Recorded = recorded;
        Step = step;
    }

    /// <summary>The placed boss it was (the nearest placement of its template), when known.</summary>
    public Boss? Boss { get; }
    /// <summary>The boss's template name.</summary>
    public string? Template { get; }
    /// <summary>The dead entity; None when only the kill record was seen.</summary>
    public EntityId Entity { get; }
    /// <summary>Who dealt the killing blow (a player or a creature), when Kiln saw it.</summary>
    public EntityId Killer { get; }
    public Player? KillerPlayer => Killer.IsNone || !Players.Available ? null : Players.Find(Killer);
    public WorldPosition? Position { get; }
    /// <summary>The game's kill record (knowledge) changed for this boss with the kill.</summary>
    public bool Recorded { get; internal set; }
    public ulong Step { get; }
    public override string ToString() => $"boss {Boss?.Id ?? Template ?? "?"} killed{(Killer.IsNone ? "" : $" by {Killer}")}{(Recorded ? " (recorded)" : "")}";
}

/// <summary>A loaded chest was emptied after its loot was generated. Sampled every 30 steps; only chests whose area is
/// loaded (near a player or a base). <see cref="NearestPlayer"/> is a guess: the player nearest to it then.</summary>
public sealed class ChestLootedEvent : KilnEvent
{
    static ChestLootedEvent() => WorldProgressPump.Start();

    internal ChestLootedEvent(ChestStatus chest, EntityId nearest)
    {
        Chest = chest;
        NearestPlayer = nearest;
    }

    public ChestStatus Chest { get; }
    public EntityId NearestPlayer { get; }
    public override string ToString() => $"looted {Chest}";
}

/// <summary>A player's standing in a quest changed: its status or its current step. Kiln quests right after the engine's
/// change; vanilla quests within a quarter second of the knowledge change that moved them (re-evaluated with the game's
/// evaluator). A character's quests loaded at join are not changes.</summary>
public sealed class QuestStatusChangedEvent : KilnEvent
{
    static QuestStatusChangedEvent() => WorldProgressPump.Start();

    internal QuestStatusChangedEvent(EntityId player, QuestEntry quest, QuestStatus oldStatus, int oldStep)
    {
        PlayerEntity = player;
        Quest = quest;
        OldStatus = oldStatus;
        OldStep = oldStep;
    }

    /// <summary>The player (None for a Kiln world quest).</summary>
    public EntityId PlayerEntity { get; }
    public Player? Player => PlayerEntity.IsNone || !Players.Available ? null : Players.Find(PlayerEntity);
    public QuestEntry Quest { get; }
    public QuestStatus OldStatus { get; }
    public QuestStatus NewStatus => Quest.Status;
    public int OldStep { get; }
    public int NewStep => Quest.Step;
    public override string ToString() => $"{(PlayerEntity.IsNone ? "holder without a player" : $"player {PlayerEntity}")} {Quest.Name ?? Quest.Id}: {OldStatus} -> {NewStatus}" +
                                         (OldStep != NewStep ? $", step {(OldStep < 0 ? "-" : OldStep + 1)} -> {(NewStep < 0 ? "-" : NewStep + 1)}" : "");
}

/// <summary>Produces the events above on the service tick while someone subscribes.</summary>
internal static class WorldProgressPump
{
    private static readonly object Lock = new();
    private static IDisposable? _tick;
    private static EventSubscription? _knowledge, _death, _kilnQuest;
    private static int _flame = -1;
    private static readonly Dictionary<string, (ulong Step, bool Death)> RecentBossKills = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (Boss Boss, ulong Step)> PendingRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<EntityId, bool> ChestSeen = new();
    private static readonly HashSet<EntityId> DirtyPlayers = new();
    private static readonly Dictionary<EntityId, (ulong Since, Dictionary<string, (QuestStatus Status, int Step)> States)> QuestCache = new();
    private static ulong _nextQuestRun;

    public const int SampleSteps = 30, QuestSteps = 15, BaselineSteps = 600, RecordWaitSteps = 120;

    public static void Start()
    {
        lock (Lock)
            _tick ??= ServiceTick.Add("world progress events", Pump);
    }

    private static void Pump(in TickInfo tick)
    {
        var flame = Events.HasSubscribers<FlameLevelChangedEvent>();
        var spire = Events.HasSubscribers<SpireUnlockedEvent>();
        var shrine = Events.HasSubscribers<ShrineFoundEvent>();
        var root = Events.HasSubscribers<ShroudRootDestroyedEvent>();
        var boss = Events.HasSubscribers<BossKilledEvent>();
        var chest = Events.HasSubscribers<ChestLootedEvent>();
        var quest = Events.HasSubscribers<QuestStatusChangedEvent>();
        if (spire || shrine || root || boss || chest) WorldIndex.EnsureStarted();

        Toggle(ref _knowledge, (spire || shrine || root || boss || quest) && Knowledge.Available, () => Events.On<KnowledgeChangedEvent>(OnKnowledge, EventPriority.Monitor));
        Toggle(ref _death, boss, () => Events.On<EntityDeathEvent>(OnDeath, EventPriority.Monitor));
        Toggle(ref _kilnQuest, quest, () => Events.On<QuestStateChangedEvent>(OnKilnQuest, EventPriority.Monitor));

        if (flame && tick.Step % SampleSteps == 0) SampleFlame();
        else if (!flame) _flame = -1;
        if (chest && tick.Step % SampleSteps == 7) SampleChests();
        else if (!chest && ChestSeen.Count > 0) ChestSeen.Clear();
        if (boss) FlushRecords(tick.Step);
        if (quest && tick.Step >= _nextQuestRun) RunQuests(tick.Step);
        else if (!quest && QuestCache.Count > 0)
        {
            QuestCache.Clear();
            DirtyPlayers.Clear();
        }
    }

    private static void Toggle(ref EventSubscription? sub, bool on, Func<EventSubscription> subscribe)
    {
        if (on && sub is null) sub = subscribe();
        else if (!on && sub is not null)
        {
            sub.Dispose();
            sub = null;
        }
    }

    // ---- flame ----

    private static void SampleFlame()
    {
        // the progress entity reads 0 until the world's save is applied (a moment after the world runs): not a change
        if (!Lifecycle.IsServerRunning || WorldProgress.Flame is not { Level: > 0 } f)
        {
            _flame = -1;
            return;
        }
        if (_flame > 0 && f.Level != _flame) Events.Post(new FlameLevelChangedEvent(_flame, f));
        _flame = f.Level;
    }

    // ---- knowledge facts ----

    private static void OnKnowledge(KnowledgeChangedEvent e)
    {
        if (!e.IsWorld && Events.HasSubscribers<QuestStatusChangedEvent>()) DirtyPlayers.Add(e.Target);
        else if (e.IsWorld && Events.HasSubscribers<QuestStatusChangedEvent>() && Players.Available)
            foreach (var p in Players.All)
                if (p.InGame) DirtyPlayers.Add(p.Entity);
        if (!e.Gained || WorldIndex.Places is not { } places) return;
        if (Events.HasSubscribers<SpireUnlockedEvent>())
            foreach (var s in places.Spires)
            {
                if (s.Unlock.Query.Type != KnowledgeQueryType.Extern && s.Unlock.Id == e.Id) Events.Post(new SpireUnlockedEvent(s, e.Target, false, e.Step));
                else if (s.TopReached?.Id == e.Id) Events.Post(new SpireUnlockedEvent(s, e.Target, true, e.Step));
            }
        if (Events.HasSubscribers<ShrineFoundEvent>())
            foreach (var s in places.Shrines)
                if (s.Discovery.Id == e.Id) Events.Post(new ShrineFoundEvent(s, e.Target, e.Step));
        if (Events.HasSubscribers<ShroudRootDestroyedEvent>())
            foreach (var r in WorldIndex.Roots ?? places.Roots)
                if (r.Destruction.Id == e.Id) Events.Post(new ShroudRootDestroyedEvent(r, e.Target, e.Step));
        if (Events.HasSubscribers<BossKilledEvent>() && WorldIndex.Bosses is { } bosses)
            foreach (var b in bosses)
            {
                if (b.Kill?.Id != e.Id) continue;
                if (RecentBossKills.TryGetValue(b.Id, out var r) && e.Step - r.Step < 1800) continue;  // the death already told
                PendingRecords.TryAdd(b.Id, (b, e.Step));
            }
    }

    // ---- bosses ----

    private static void OnDeath(EntityDeathEvent e)
    {
        if (!Spawn.Available || Spawn.Inspect(e.Victim) is not { Template: { } guid } info) return;
        if (WorldIndex.TemplateOf(guid) is not { IsBoss: true } t) return;
        Boss? placed = null;
        if (WorldIndex.Bosses is { } bosses)
            placed = bosses.Where(b => string.Equals(b.Template, t.Name, StringComparison.Ordinal))
                .OrderBy(b => b.Position.DistanceTo(e.Position)).FirstOrDefault(b => b.Position.DistanceTo(e.Position) < 150);
        var recorded = placed is not null && PendingRecords.Remove(placed.Id);
        if (placed is not null) RecentBossKills[placed.Id] = (e.Step, true);
        Events.Post(new BossKilledEvent(placed, info.TemplateName ?? t.Name, e.Victim, e.Killer, e.Position, recorded, e.Step));
    }

    private static void FlushRecords(ulong step)
    {
        if (PendingRecords.Count == 0) return;
        foreach (var (id, (b, at)) in PendingRecords.ToArray())
        {
            if (step - at < RecordWaitSteps) continue;
            PendingRecords.Remove(id);
            RecentBossKills[id] = (step, false);
            Events.Post(new BossKilledEvent(b, b.Template, EntityId.None, EntityId.None, b.Position, true, at));
        }
    }

    // ---- chests ----

    private static void SampleChests()
    {
        if (!World.Available) return;
        var alive = new HashSet<EntityId>();
        foreach (var e in World.Find("RandomLootContainer"))
        {
            alive.Add(e);
            if (!WorldProgress.ReadChest(e, out _, out var looted) || looted is null) continue;
            var was = ChestSeen.TryGetValue(e, out var w) ? w : (bool?)null;
            ChestSeen[e] = looted.Value;
            if (was == false && looted == true && WorldProgress.ChestOf(e) is { } c)
                Events.Post(new ChestLootedEvent(c, NearestPlayer(c.Position)));
        }
        foreach (var gone in ChestSeen.Keys.Where(k => !alive.Contains(k)).ToArray()) ChestSeen.Remove(gone);
    }

    private static EntityId NearestPlayer(WorldPosition at)
    {
        if (!Players.Available) return EntityId.None;
        EntityId best = default;
        var d = double.MaxValue;
        foreach (var p in Players.All)
            if (p.InGame && Players.PositionOf(p.Entity) is { } pos && pos.DistanceTo(at) < d)
            {
                d = pos.DistanceTo(at);
                best = p.Entity;
            }
        return d <= 16 ? best : EntityId.None;
    }

    // ---- quests ----

    private static readonly Dictionary<(string Holder, string Quest), int> KilnSteps = new();

    private static void OnKilnQuest(QuestStateChangedEvent e)
    {
        var entry = Quests.KilnEntry(e.Quest, e.Holder);
        var key = (e.Holder.Key, e.Quest.FullId);
        var oldStep = KilnSteps.TryGetValue(key, out var k) ? k : e.OldStatus == QuestStatus.Active ? entry.Step : -1;
        KilnSteps[key] = entry.Step;
        if (e.OldStatus == e.Status && oldStep == entry.Step) return;
        Events.Post(new QuestStatusChangedEvent(e.Holder.Entity, entry, e.OldStatus, oldStep));
    }

    private static void RunQuests(ulong step)
    {
        _nextQuestRun = step + QuestSteps;
        if (!Knowledge.Available) return;
        // a character's journal loads a moment after it joins (its record is not a change): re-take the baseline then
        if (Players.Available)
            foreach (var p in Players.All)
                if (p.InGame && (!QuestCache.TryGetValue(p.Entity, out var c) || (step - c.Since < BaselineSteps && step - c.Since >= BaselineSteps - QuestSteps)))
                    DirtyPlayers.Add(p.Entity);
        if (DirtyPlayers.Count == 0) return;
        foreach (var player in DirtyPlayers.ToArray())
        {
            DirtyPlayers.Remove(player);
            IReadOnlyList<JournalQuestState> states;
            try
            {
                states = Journal.States(player);  // the player's knowledge: throws once they left
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
            {
                QuestCache.Remove(player);
                continue;
            }
            var fresh = !QuestCache.TryGetValue(player, out var cached);
            var since = fresh ? step : cached.Since;
            var baseline = fresh || step - since < BaselineSteps;
            var map = fresh ? new Dictionary<string, (QuestStatus Status, int Step)>(StringComparer.Ordinal) : cached.States;
            foreach (var s in states)
            {
                var entry = Quests.VanillaEntry(s);
                var now = (entry.Status, entry.Step);
                if (!baseline && map.TryGetValue(entry.Id, out var old) && old != now)
                    Events.Post(new QuestStatusChangedEvent(player, entry, old.Status, old.Step));
                else if (!baseline && !map.ContainsKey(entry.Id) && entry.Status != QuestStatus.Locked)
                    Events.Post(new QuestStatusChangedEvent(player, entry, QuestStatus.Locked, -1));
                map[entry.Id] = now;
            }
            QuestCache[player] = (since, map);
        }
    }
}
