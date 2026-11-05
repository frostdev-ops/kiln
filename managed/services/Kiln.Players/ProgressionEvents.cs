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
