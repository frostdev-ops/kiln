// SPDX-License-Identifier: MIT
// The retime of a move, over made-up event lists (no game timings): the blow lands on hitAt, the end on length, a damage window keeps its duration, the
// other spans and followUpTime follow the map, and the rules come in an order in which no event overtakes another. That last part is shown by doing what
// kiln.gamedata does: apply one rule at a time, resolve [Type#k] in the current order, and after each time change sort again (stable, over the original list).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.BlockTests;

public sealed class MoveTimingTests
{
    private static EventRow E(string type, double t, double d = 0) => new(type, 0, t, d);

    /// <summary>A list with its per-type k filled in, in list order.</summary>
    internal static SubSequence Sub(double length, params EventRow[] events)
    {
        var seen = new Dictionary<string, int>();
        var list = events.Select(e =>
        {
            var k = seen.GetValueOrDefault(e.Type);
            seen[e.Type] = k + 1;
            return e with { K = k };
        }).ToList();
        return new SubSequence(Guid.NewGuid(), 0, length, list);
    }

    // a swing: wind-up, two openings for the next input, two damage events at the same moment (one per tag), the hand-back, a movement lerp
    internal static SubSequence Swing() => Sub(1.6,
        E("SetStateEvent", 0), E("SetAnimationEvent", 0), E("EnableAbilityEvent", 0),
        E("DisableAbilityEvent", 0.1), E("DisableAbilityEvent", 0.12),
        E("SetActionOportunity", 0.18) with { TriggeredBy = "Primary", FollowUp = Guid.NewGuid(), FollowUpTime = 0.45 },
        E("SetActionOportunity", 0.18) with { TriggeredBy = "Heavy", FollowUp = Guid.NewGuid(), FollowUpTime = 0.55 },
        E("SpawnImpact", 0.2, 0.15), E("SpawnImpact", 0.2, 0.15),
        E("EnableAbilityEvent", 0.35), E("LerpMovementFactorEvent", 0.4) with { Spans = new Dictionary<string, double> { ["lerpDuration"] = 0.2 } },
        E("EnableAbilityEvent", 0.5), E("EnableAbilityEvent", 0.7), E("VfxNotifierEvent", 0.7, 0.3));

    private sealed class Sim
    {
        public sealed class Ev(int index, EventRow row)
        {
            public readonly int Index = index;
            public readonly string Type = row.Type;
            public double Time = row.Time, Duration = row.Duration;
            public readonly Dictionary<string, double> Fields = row.Spans.ToDictionary(x => x.Key, x => x.Value);
        }

        public readonly List<Ev> Original;
        public List<Ev> Order;
        public double Length;

        public Sim(SubSequence s)
        {
            Original = s.Events.Select((e, i) => new Ev(i, e)).ToList();
            Order = Original.ToList();
            Length = s.Length;
            if (s.Events.Any(e => e.FollowUpTime != null))
                foreach (var (e, row) in Original.Zip(s.Events))
                    if (row.FollowUpTime is { } fu) e.Fields["followUpTime"] = fu;
        }

        private static readonly Regex Path = new(@"^events\[(\w+)#(\d+)\]\.(\w+)$");

        /// <summary>One rule as kiln.gamedata applies it: [Type#k] in the current order, then a stable sort over the original list after a time change.</summary>
        public void Apply(string path, double value)
        {
            if (path == "length")
            {
                Length = value;
                return;
            }
            var m = Path.Match(path);
            Assert.True(m.Success, path);
            var e = Order.Where(x => x.Type == m.Groups[1].Value).ElementAt(int.Parse(m.Groups[2].Value));
            switch (m.Groups[3].Value)
            {
                case "time": e.Time = value; Order = Original.OrderBy(x => x.Time).ToList(); break;   // OrderBy is stable, over the original order
                case "duration": e.Duration = value; break;
                default: e.Fields[m.Groups[3].Value] = value; break;
            }
        }

        public bool InOriginalOrder => Order.Select(x => x.Index).SequenceEqual(Enumerable.Range(0, Original.Count));
    }

    private static (Sim Sim, List<string> Broken) Run(SubSequence s, JsonObject rules)
    {
        var sim = new Sim(s);
        var broken = new List<string>();
        foreach (var (path, v) in rules)
        {
            sim.Apply(path, (double)v!);
            if (!sim.InOriginalOrder) broken.Add(path);
        }
        return (sim, broken);
    }

    private static TimeMap Map(SubSequence s, double? hitAt, double? length)
    {
        var (map, problem) = MoveTiming.Map(s, MoveTiming.Swing, hitAt, length);
        Assert.True(map != null, problem);
        return map!;
    }

    [Theory]
    [InlineData(0.3, 1.0)]     // later before the blow, earlier after it: both passes
    [InlineData(0.6, 3.0)]     // everything later
    [InlineData(0.1, 0.5)]     // everything earlier
    [InlineData(0.6, null)]    // a shift after the blow
    [InlineData(null, 2.4)]    // length alone: the blow stays
    public void The_blow_lands_on_hitAt_the_end_on_length_and_no_event_overtakes_another(double? hitAt, double? length)
    {
        var s = Swing();
        var m = Map(s, hitAt, length);
        var rules = MoveTiming.Rules(s, m, MoveTiming.Swing);
        var (sim, broken) = Run(s, rules);
        Assert.Empty(broken);
        // every event where the map puts it, found by its own place: a rule that hit another event would show here
        foreach (var (e, row) in sim.Original.Zip(s.Events)) Assert.Equal(m.F(row.Time), e.Time, 9);
        var impacts = sim.Original.Where(e => e.Type == "SpawnImpact").ToList();
        Assert.All(impacts, e => Assert.Equal(hitAt ?? 0.2, e.Time, 12));
        Assert.Equal(length ?? (hitAt ?? 0.2) + 1.4, sim.Length, 12);
        // a damage window keeps its own duration; another window's end follows the map, as does a lerp's and the follow-up's time
        Assert.All(impacts, e => Assert.Equal(0.15, e.Duration, 12));
        var vfx = sim.Original.Single(e => e.Type == "VfxNotifierEvent");
        Assert.Equal(m.F(1.0), vfx.Time + vfx.Duration, 9);
        var lerp = sim.Original.Single(e => e.Type == "LerpMovementFactorEvent");
        Assert.Equal(m.F(0.6), lerp.Time + lerp.Fields["lerpDuration"], 9);
        var opp = sim.Original.Where(e => e.Type == "SetActionOportunity").ToList();
        Assert.Equal(m.F(0.45), opp[0].Fields["followUpTime"], 9);
        Assert.Equal(m.F(0.55), opp[1].Fields["followUpTime"], 9);
    }

    [Fact]
    public void The_same_rules_in_plain_order_would_move_the_wrong_events()
    {
        // the control: what the order rule prevents does happen without it, so the test above can fail
        var s = Swing();
        var rules = MoveTiming.Rules(s, Map(s, 0.6, 3.0), MoveTiming.Swing);
        var plain = new JsonObject(rules.Where(r => r.Key.EndsWith(".time")).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => KeyValuePair.Create(r.Key, r.Value?.DeepClone())));
        var (sim, broken) = Run(s, plain);
        Assert.NotEmpty(broken);
        var dis = sim.Original.Where(e => e.Type == "DisableAbilityEvent").ToList();
        Assert.False(Math.Abs(dis[0].Time - 0.3) < 1e-9 && Math.Abs(dis[1].Time - 0.36) < 1e-9, "the plain order happened to work: the control proves nothing");
    }

    [Fact]
    public void The_map_is_monotonic_and_continuous_at_the_blow()
    {
        var m = new TimeMap(0.2, 1.6, 0.3, 1.0);
        double last = -1;
        for (var t = 0.0; t <= 2; t += 0.001)
        {
            Assert.True(m.F(t) >= last);
            last = m.F(t);
        }
        Assert.Equal(0.3, m.F(0.2));
        Assert.Equal(1.0, m.F(1.6));
        Assert.Equal(0.3, m.F(0.2 - 1e-12), 9);
    }

    [Fact]
    public void A_move_without_a_blow_scales_as_a_whole_and_refuses_hitAt()
    {
        var block = Sub(0.4, E("SetAnimationEvent", 0), E("LoopEvent", 0.1, 0.2), E("SetActionOportunity", 0.067) with { FollowUpTime = 0.35, TriggeredBy = "Primary", FollowUp = Guid.NewGuid() });
        var (none, why) = MoveTiming.Map(block, MoveTiming.Swing, 0.2, null);
        Assert.Null(none);
        Assert.Contains("no damage event", why);
        var m = Map(block, null, 0.8);
        var rules = MoveTiming.Rules(block, m, MoveTiming.Swing);
        Assert.Equal(0.2, (double)rules["events[LoopEvent#0].time"]!, 9);
        Assert.Equal(0.4, (double)rules["events[LoopEvent#0].duration"]!, 9);
        Assert.Equal(0.7, (double)rules["events[SetActionOportunity#0].followUpTime"]!, 9);
        Assert.Equal(0.8, (double)rules["length"]!, 9);
    }

    [Fact]
    public void Retimes_that_cannot_be_are_refused_with_the_reason()
    {
        var s = Swing();
        Assert.Contains("not inside", MoveTiming.Map(s, MoveTiming.Swing, 0.3, 0.3).Problem);
        var atZero = Sub(1, E("SpawnImpact", 0, 0.1), E("EnableAbilityEvent", 0.5));
        Assert.Contains("at 0 s", MoveTiming.Map(atZero, MoveTiming.Swing, 0.3, null).Problem);
        var atEnd = Sub(1, E("SpawnImpact", 1));
        Assert.Contains("at its end", MoveTiming.Map(atEnd, MoveTiming.Swing, 0.5, 2).Problem);
    }

    [Fact]
    public void Unchanged_values_are_not_written()
    {
        var s = Swing();
        var rules = MoveTiming.Rules(s, Map(s, 0.2, null), MoveTiming.Swing);
        Assert.Empty(rules);
    }
}

public sealed class MoveRuntimeRowTests
{
    [Fact]
    public void An_event_of_the_games_json_becomes_a_row_with_its_type_place_and_times()
    {
        var seen = new Dictionary<string, int>();
        var follow = Guid.NewGuid();
        var a = MoveRuntime.Row(JsonNode.Parse("""{ "$type": "keen::actor::SetActionOportunity", "time": 0.2, "duration": 0, "triggeredBy": "Primary", "followUp": "FOLLOW", "followUpTime": { "value": 0.45 } }""".Replace("FOLLOW", follow.ToString()))!.AsObject(), seen);
        var b = MoveRuntime.Row(JsonNode.Parse("""{ "$type": "SetActionOportunity", "time": { "value": 0.3 }, "duration": 0.1 }""")!.AsObject(), seen);
        var c = MoveRuntime.Row(JsonNode.Parse("""{ "$type": "keen::actor::LerpMovementFactorEvent", "time": 0.4, "duration": 0, "lerpDuration": 0.2 }""")!.AsObject(), seen);
        Assert.Equal(("SetActionOportunity", 0, 0.2, "Primary", follow, 0.45), (a.Type, a.K, a.Time, a.TriggeredBy!, a.FollowUp, a.FollowUpTime!.Value));
        Assert.Equal(("SetActionOportunity", 1, 0.3, 0.1), (b.Type, b.K, b.Time, b.Duration));
        Assert.Equal(("LerpMovementFactorEvent", 0, 0.2), (c.Type, c.K, c.Spans["lerpDuration"]));
        Assert.Equal("events[SetActionOportunity#1]", b.Path);
    }
}
