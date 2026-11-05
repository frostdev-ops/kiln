// SPDX-License-Identifier: MIT
// Kiln.Players on the event bus: PlayerJoinedEvent / PlayerLeftEvent (docs/kiln/events.md).
namespace Kiln;

/// <summary>A player logged in (their entity exists). Posted on the tick thread at the start of the step after
/// kiln.players saw the join (the same moment as <see cref="Players.Joined"/>). Not cancelable. Players already in game
/// when the first handler subscribes raise nothing: read <see cref="Players.All"/>.</summary>
public sealed class PlayerJoinedEvent : KilnEvent
{
    static PlayerJoinedEvent() => PlayerEventPump.Start();

    internal PlayerJoinedEvent(PlayerEvent e)
    {
        Player = e.Player;
        Step = e.Step;
        Sequence = e.Sequence;
    }

    public Player Player { get; }

    /// <summary>The server-world step of the join.</summary>
    public ulong Step { get; }

    /// <summary>kiln.players' running event number.</summary>
    public ulong Sequence { get; }

    public override string ToString() => $"joined: {Player}";
}

/// <summary>A player left (disconnect, kick, shutdown). <see cref="Player"/> is as they were last seen in game.
/// Posted like <see cref="PlayerJoinedEvent"/>. Not cancelable.</summary>
public sealed class PlayerLeftEvent : KilnEvent
{
    static PlayerLeftEvent() => PlayerEventPump.Start();

    internal PlayerLeftEvent(PlayerEvent e)
    {
        Player = e.Player;
        Step = e.Step;
        Sequence = e.Sequence;
    }

    public Player Player { get; }
    public ulong Step { get; }
    public ulong Sequence { get; }

    public override string ToString() => $"left: {Player}";
}

/// <summary>Reads kiln.players' event ring on the service tick while someone listens (one native call per step).</summary>
internal static class PlayerEventPump
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
                _cursor = Players.LastSequence;
            }
            catch (Exception)
            {
                _cursor = 0;
            }
            _tick = ServiceTick.Add("kiln.players events", Pump);
        }
    }

    private static void Pump(in TickInfo tick)
    {
        var last = Players.LastSequence;
        if (last <= _cursor) return;
        var joined = Events.HasSubscribers<PlayerJoinedEvent>();
        var left = Events.HasSubscribers<PlayerLeftEvent>();
        if (!joined && !left)
        {
            _cursor = last;  // nobody listens: do not replay these later
            return;
        }
        foreach (var e in Players.EventsAfter(_cursor, 64))
        {
            _cursor = Math.Max(_cursor, e.Sequence);
            if (e.Joined && joined) Events.Post(new PlayerJoinedEvent(e));
            else if (!e.Joined && left) Events.Post(new PlayerLeftEvent(e));
        }
    }
}
