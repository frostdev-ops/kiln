// SPDX-License-Identifier: MIT
// The retime of a move: one monotonic map over a sequence's own clock, computed when content is ready from the clone's own events, so that no game timing is
// written into the tree. With h0 the first damage event and len0 the length:
//   f(t) = t * hitAt / h0                              up to h0
//   f(t) = hitAt + (t - h0)                            after it (a shift), or
//   f(t) = hitAt + (t - h0) * (length - hitAt) / (len0 - h0)   when length is given.
// length alone keeps h0; a motion with no damage event and a length scales as a whole. Through the map go every event's time, the end of every window
// (time + duration; a damage window keeps its own duration), the end of every other span an event carries (lerpDuration, hitStopDuration, blend times),
// SetActionOportunity.followUpTime, and the length.
// Order: kiln.gamedata keeps a sub-sequence's events sorted by time (a stable sort over the original list) and [Type#k] counts in the current order
// (docs/kiln/services/gamedata.md, "Event order"). The map keeps the order, and the rules are written so that no event overtakes another on the way:
// events that move later from the last to the first, then events that move earlier from the first to the last.
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One event of a sub-sequence as content ready reads it: its type (without namespace), its place among the events of its type, its times in seconds.</summary>
internal sealed record EventRow(string Type, int K, double Time, double Duration)
{
    /// <summary>Spans on the event's own clock that start at <see cref="Time"/>, by field: <c>lerpDuration</c>, <c>hitStopDuration</c>, <c>blendInDuration</c>, ...</summary>
    public IReadOnlyDictionary<string, double> Spans { get; init; } = new Dictionary<string, double>();
    /// <summary>A SetActionOportunity's: when its follow-up starts, on the sequence's clock.</summary>
    public double? FollowUpTime { get; init; }
    /// <summary>A SetActionOportunity's: the input it waits for.</summary>
    public string? TriggeredBy { get; init; }
    /// <summary>A SetActionOportunity's: the sequence it hands over to. Empty: none.</summary>
    public Guid FollowUp { get; init; }
    /// <summary>A SetAnimationEvent's or SetAnimationOverlayEvent's state.</summary>
    public Guid State { get; init; }
    /// <summary>The path of this event in the rules: <c>events[Type#k]</c>.</summary>
    public string Path => $"events[{Type}#{K}]";
}

/// <summary>A sub-sequence of a sequence: the clone has the same, so its rules go to the clone's own entry for it.</summary>
internal sealed record SubSequence(Guid Sequence, int Index, double Length, IReadOnlyList<EventRow> Events);

/// <summary>The map of one move: <see cref="F"/>.</summary>
internal sealed record TimeMap(double H0, double Len0, double HitAt, double Length)
{
    /// <summary>Monotonic: IEEE's operations are, so no two events swap even by a rounding. The blow lands on hitAt and the end on the length exactly.</summary>
    public double F(double t)
    {
        if (t == H0 && H0 > 0) return HitAt;
        if (t == Len0) return Length;
        if (t <= H0) return H0 <= 0 || HitAt == H0 ? t : t * HitAt / H0;
        var after = Len0 - H0;
        var scale = after > 0 ? (Length - HitAt) / after : 1;
        // no scale: a plain shift (none at all when the blow stays), so that what does not change is not rounded into a change
        if (scale == 1) return HitAt == H0 ? t : t + (HitAt - H0);
        return HitAt + (t - H0) * scale;
    }
}

internal static class MoveTiming
{
    private const double Eps = 1e-6;

    /// <summary>Event types whose blow a move's hitAt is about: a swing's SpawnImpact.</summary>
    public static bool Swing(EventRow e) => e.Type == "SpawnImpact";

    /// <summary>The map for a move, or why it cannot have one.</summary>
    public static (TimeMap? Map, string? Problem) Map(SubSequence s, Func<EventRow, bool> damage, double? hitAt, double? length)
    {
        var hits = s.Events.Where(damage).ToList();
        var len0 = s.Length;
        if (!double.IsFinite(len0) || len0 <= 0) return (null, $"its length is {len0} s");
        if (hits.Count == 0)
        {
            if (hitAt != null) return (null, "it has no damage event, so there is no blow to move: leave hitAt out");
            // no blow: the length scales the whole motion
            return (new TimeMap(0, len0, 0, length ?? len0), null);
        }
        var h0 = hits.Min(e => e.Time);
        var at = hitAt ?? h0;
        if (hitAt != null && h0 <= 0) return (null, "its first damage event is at 0 s, so what comes before it cannot be scaled to put it elsewhere");
        if (length != null && len0 - h0 <= 0) return (null, $"its first damage event ({h0:0.###} s) is at its end ({len0:0.###} s), so nothing after it can be scaled");
        var end = length ?? at + (len0 - h0);
        if (!(at > 0) || !(end > at) || !double.IsFinite(end))
            return (null, $"hitAt {at:0.###} s is not inside the move's length (0, {end:0.###})");
        return (new TimeMap(h0, len0, at, end), null);
    }

    /// <summary>The rules that retime a sub-sequence, in an order in which no event overtakes another. Only what changes is written.</summary>
    public static JsonObject Rules(SubSequence s, TimeMap m, Func<EventRow, bool> damage)
    {
        var rules = new JsonObject();
        var moved = s.Events.Select((e, i) => (e, i, to: m.F(e.Time))).ToList();
        // a time is written whenever it changes at all: one left where it was by a hair could be overtaken by one written
        foreach (var (e, _, to) in moved.Where(x => x.to > x.e.Time).OrderByDescending(x => x.i)) rules[$"{e.Path}.time"] = to;
        foreach (var (e, _, to) in moved.Where(x => x.to < x.e.Time).OrderBy(x => x.i)) rules[$"{e.Path}.time"] = to;
        foreach (var (e, _, to) in moved)
        {
            if (e.Duration > 0 && !damage(e))
            {
                var d = m.F(e.Time + e.Duration) - to;
                if (Math.Abs(d - e.Duration) > Eps) rules[$"{e.Path}.duration"] = d;
            }
            foreach (var (field, span) in e.Spans)
            {
                if (span <= 0) continue;
                var d = m.F(e.Time + span) - to;
                if (Math.Abs(d - span) > Eps) rules[$"{e.Path}.{field}"] = d;
            }
            if (e.FollowUpTime is { } fu && Math.Abs(m.F(fu) - fu) > Eps) rules[$"{e.Path}.followUpTime"] = m.F(fu);
        }
        var len = m.F(s.Length);
        if (Math.Abs(len - s.Length) > Eps) rules["length"] = len;
        return rules;
    }
}
