// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>SequenceStartedEvent / SequenceEndedEvent: polls Actor.sequenceRuntimeId of every actor (the list is
/// refreshed every 50 steps) while anyone subscribes.</summary>
internal static class SequenceWatch
{
    private sealed record State(uint Sequence, long Time, int Call, long SeenMs);

    private static readonly Dictionary<uint, State> Last = new();
    private static List<EntityId> _actors = new();
    private static ulong _refreshed;
    private static bool _hooked;

    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        AbilitiesDispatch.Tick += OnTick;
    }

    private static void OnTick(ulong step)
    {
        var started = Events.HasSubscribers<SequenceStartedEvent>();
        var ended = Events.HasSubscribers<SequenceEndedEvent>();
        if (!started && !ended)
        {
            if (Last.Count > 0) Last.Clear();
            return;
        }
        if (!World.Available) return;
        if (_actors.Count == 0 || step >= _refreshed + 50)
        {
            _refreshed = step;
            _actors = World.Find("Actor").Take(2048).ToList();
        }
        var now = Environment.TickCount64;
        foreach (var a in _actors)
        {
            var s = Abilities.SequenceOf(a);
            if (s is not { } cur)
            {
                Last.Remove(a.Value);
                continue;
            }
            Last.TryGetValue(a.Value, out var prev);
            var same = prev is not null && prev.Sequence == cur.Sequence && prev.Time == cur.TriggerTime && prev.Call == cur.CallIndex;
            if (same) continue;
            if (prev is { Sequence: not 0 } && ended)
                Events.Post(new SequenceEndedEvent(a, prev.Sequence, TimeSpan.FromMilliseconds(now - prev.SeenMs)));
            if (cur.Sequence != 0 && started && prev is not null)  // the first sight of an actor is not a start
                Events.Post(new SequenceStartedEvent(a, cur.Sequence, cur.TriggerTime, cur.CallIndex));
            Last[a.Value] = new State(cur.Sequence, cur.TriggerTime, cur.CallIndex, now);
        }
    }
}
