// SPDX-License-Identifier: MIT
// Kiln.Players on the event bus: PlayerLevelChangedEvent / PlayerSkillsChangedEvent (docs/kiln/events.md,
// docs/kiln/services/progression.md).
namespace Kiln;

/// <summary>A player's level changed: a natural level-up (the game's player_level), a Kiln change (<see cref="Request"/>
/// is its id) or anything else the game did. Posted on the tick thread; seen in kiln.progression's snapshots (every 8
/// steps, ~0.3 s) and right after Kiln's own changes. Several level-ups between two snapshots arrive as one event.
/// Not cancelable.</summary>
public sealed class PlayerLevelChangedEvent : KilnEvent
{
    static PlayerLevelChangedEvent() => ProgressionEventPump.Start();

    internal PlayerLevelChangedEvent(EntityId entity, int oldLevel, int newLevel, ulong request, ulong step)
    {
        Entity = entity;
        OldLevel = oldLevel;
        NewLevel = newLevel;
        Request = request;
        Step = step;
    }

    public EntityId Entity { get; }
    /// <summary>The player (null if they left in the meantime).</summary>
    public Player? Player => Players.Available ? Players.Find(Entity) : null;
    public int OldLevel { get; }
    public int NewLevel { get; }
    /// <summary>The Kiln request that caused it (<see cref="ProgressionResult.Request"/>), 0 = the game.</summary>
    public ulong Request { get; }
    public ulong Step { get; }

    public override string ToString() => $"level {OldLevel} -> {NewLevel} (entity {Entity})";
}

/// <summary>A player's unlocked skill nodes changed (seen as a change of the points they spent): the player's own
/// skill tree, a Kiln change or the game's reset. Posted like <see cref="PlayerLevelChangedEvent"/>. Not
/// cancelable.</summary>
public sealed class PlayerSkillsChangedEvent : KilnEvent
{
    static PlayerSkillsChangedEvent() => ProgressionEventPump.Start();

    internal PlayerSkillsChangedEvent(EntityId entity, int oldSpent, int newSpent, ulong request, ulong step)
    {
        Entity = entity;
        OldPointsSpent = oldSpent;
        NewPointsSpent = newSpent;
        Request = request;
        Step = step;
    }

    public EntityId Entity { get; }
    public Player? Player => Players.Available ? Players.Find(Entity) : null;
    public int OldPointsSpent { get; }
    public int NewPointsSpent { get; }
    public ulong Request { get; }
    public ulong Step { get; }

    public override string ToString() => $"skill points spent {OldPointsSpent} -> {NewPointsSpent} (entity {Entity})";
}

/// <summary>
/// A Kiln progression request (<see cref="Progression.AddExperience"/>, <see cref="Progression.SetExperience"/>,
/// <see cref="Progression.GiveSkillPoints"/>, and any other that moved them) changed a player's experience or skill
/// points. Posted on the tick thread when the request completed (two steps after it was submitted, before the caller's
/// task continues), once per value that changed: <see cref="What"/> is <c>Experience</c> (into the current level) or
/// <c>SkillPoints</c> (the total). A level or the unlocked nodes keep <see cref="PlayerLevelChangedEvent"/> and
/// <see cref="PlayerSkillsChangedEvent"/>. A change made while this event is being dispatched (a handler that changes
/// progression again) is posted at the start of the next step instead, once per player and value (the newest wins), and
/// only while that player is still the one in the slot. Observe only. Built only while someone subscribes. Since
/// 0.2.0-dev.27.
/// </summary>
[ServerOnly]
public sealed class PlayerProgressionChangedEvent : KilnEvent, IEntityEvent
{
    internal PlayerProgressionChangedEvent(EntityId player, string what, long before, long after, ulong request, string origin)
    {
        Player = player;
        What = what;
        Before = before;
        After = after;
        Request = request;
        Origin = origin;
    }

    /// <summary>The player's entity.</summary>
    public EntityId Player { get; }

    /// <summary><c>Experience</c> or <c>SkillPoints</c>.</summary>
    public string What { get; }

    /// <summary>The value before.</summary>
    public long Before { get; }

    /// <summary>The value after.</summary>
    public long After { get; }

    /// <summary>The Kiln request (<see cref="ProgressionResult.Request"/>).</summary>
    public ulong Request { get; }

    /// <summary>The id of the mod that made the request ("" when Kiln made it for no mod).</summary>
    public string Origin { get; }

    /// <summary>Whether the event is about <paramref name="entity"/>.</summary>
    public bool Involves(EntityId entity) => !entity.IsNone && entity == Player;

    /// <summary>The change and who made it, for a log line.</summary>
    public override string ToString() =>
        $"player {Player}: {What} {Before} -> {After} (request {Request}) by {(Origin.Length > 0 ? Origin : "kiln")}";
}

/// <summary>The change events of Kiln's progression requests (the contract: every write Kiln makes has one).</summary>
internal static class ProgressionChanges
{
    // One key per value of one player changed by one mod: a handler that changes it again waits for the next step.
    internal static readonly ChangeNotices<PlayerProgressionChangedEvent, (EntityId, string, string)> Notices = new("kiln.progression");

    /// <summary>A request completed: posts what it changed of experience and skill points.</summary>
    internal static void Completed(ProgressionResult r, string origin)
    {
        if (!r.Ok || r.Before is not { } b || r.After is not { } a || !Events.HasSubscribers<PlayerProgressionChangedEvent>()) return;
        if (b.Experience != a.Experience) Post(r, "Experience", b.Experience, a.Experience, origin);
        if (b.SkillPointsTotal != a.SkillPointsTotal) Post(r, "SkillPoints", b.SkillPointsTotal, a.SkillPointsTotal, origin);
    }

    private static void Post(ProgressionResult r, string what, long before, long after, string origin) =>
        Notices.Post((r.Entity, what, origin), new PlayerProgressionChangedEvent(r.Entity, what, before, after, r.Request, origin), r.Entity);
}

/// <summary>Reads kiln.progression's event ring on the service tick while someone listens.</summary>
internal static class ProgressionEventPump
{
    private static readonly object Lock = new();
    private static IDisposable? _tick;
    private static ulong _cursor;

    public static void Start()
    {
        lock (Lock)
        {
            if (_tick is not null) return;
            try
            {
                _cursor = Progression.LastSequence;
            }
            catch (Exception)
            {
                _cursor = 0;
            }
            _tick = ServiceTick.Add("kiln.progression events", Pump);
        }
    }

    private static void Pump(in TickInfo tick)
    {
        var last = Progression.LastSequence;
        if (last <= _cursor) return;
        var level = Events.HasSubscribers<PlayerLevelChangedEvent>();
        var skills = Events.HasSubscribers<PlayerSkillsChangedEvent>();
        if (!level && !skills)
        {
            _cursor = last;
            return;
        }
        foreach (var e in Progression.EventsAfter(_cursor, 64))
        {
            _cursor = Math.Max(_cursor, e.Seq);
            if (e.Kind == 1 && level)
                Events.Post(new PlayerLevelChangedEvent(new EntityId(e.Entity), (int)e.OldValue, (int)e.NewValue, e.Request, e.Step));
            else if (e.Kind == 2 && skills)
                Events.Post(new PlayerSkillsChangedEvent(new EntityId(e.Entity), (int)e.OldValue, (int)e.NewValue, e.Request, e.Step));
        }
    }
}
