// SPDX-License-Identifier: MIT
// A brain's sight with the perception policy (chapter 01 section 5.1): the default asks no ray; Voxels sees only what has
// a clear line, counts every other outcome, and never falls back to distance alone.
using Xunit;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kiln.BlockTests;

public sealed class NpcPerceptionTests
{
    private sealed class QueryBackend(nint table) : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => true;
        public nint Service(string name, uint minVersion) => name == "kiln.queries" ? table : 0;
        public void Log(ulong owner, LogLevel level, string text) { }
    }

    [Fact]
    public unsafe void Automatic_perception_rechecks_a_stale_composed_row_before_skipping_or_seeing()
    {
        var previous = Backend.Current;
        var table = (QueriesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(QueriesNative.Table));
        table->Header = new ServiceHeader { Size = (uint)sizeof(QueriesNative.Table), Version = QueriesNative.Version };
        string? Off() => (string?)typeof(NpcRuntime).GetMethod("SightOff", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        try
        {
            Backend.Current = new QueryBackend((nint)table);
            Capabilities.Reset();
            QueriesNative.Forget();
            QueriesCaps.Require(QueriesCaps.LineOfSight);
            ServiceCaps.Static(QueriesCaps.LineOfSight, false, CapabilityReason.ServiceMissing, "reported before dependencies were ready");
            var p = Voxels();
            Assert.Equal([A], p.Look(Self, At, [Three[0]], Off, (_, _, _) => Open));
            Assert.Equal(0ul, p.Stats.CapabilityUnavailableUpdates);
            Capabilities.Report("queries.voxel_ray", CapabilityState.Unavailable, CapabilityReason.Faulted, "ray faulted");
            Assert.Empty(p.Look(Self, At, [Three[0]], Off, (_, _, _) => Open));
            Assert.Equal(1ul, p.Stats.CapabilityUnavailableUpdates);
            Assert.Contains("ray faulted", p.Stats.LastRefusal);
            // The real Look must handle an unavailable capability before asking the world for candidates. This backend
            // has no world table; the automatic path still clears its old target and counts exactly one update.
            var ctl = new NpcControl(Self, new NpcCreature
            {
                Mod = "perception-test", Name = "guard", Variant = "perception-test:guard", Template = "test", Owner = default,
                Perception = new() { Occlusion = NpcOcclusionMode.Voxels },
            }) { Target = A };
            ctl.SeenList.Add(A);
            typeof(NpcRuntime).GetMethod("Look", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [ctl]);
            Assert.Empty(ctl.Seen);
            Assert.True(ctl.Target.IsNone);
            Assert.Equal((1ul, 1ul), (ctl.PerceptionStats.Updates, ctl.PerceptionStats.CapabilityUnavailableUpdates));
        }
        finally
        {
            QueriesNative.Forget();
            Backend.Current = previous;
            Capabilities.Reset();
            NativeMemory.Free(table);
        }
    }

    private static readonly EntityId Self = new(2000), A = new(2001), B = new(2002), C = new(2003);
    private static readonly WorldPosition At = new(0, 10, 0);

    private static readonly (EntityId, WorldPosition)[] Three = [(A, new(5, 10, 0)), (B, new(10, 10, 0)), (C, new(15, 10, 0))];

    private static NpcPerception Voxels() => new(new NpcPerceptionPolicy { Occlusion = NpcOcclusionMode.Voxels });

    private static VoxelSightResult Open => new(true, null, true, null);
    private static VoxelSightResult Wall => new(true, null, false, new VoxelRayHit(new(3, 11.5, 0), 3, 1, 0xff01));
    private static VoxelSightResult No(string reason) => new(false, reason, false, null);

    [Fact]
    public void The_default_sees_every_candidate_and_asks_no_ray()
    {
        var p = new NpcPerception(new NpcPerceptionPolicy());
        var asked = 0;
        var seen = p.Look(Self, At, Three, () => "off", (_, _, _) => { asked++; return Wall; });
        Assert.Equal([A, B, C], seen);
        Assert.Equal(0, asked);
        Assert.Equal(new NpcPerceptionStats(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null), p.Stats);
    }

    [Fact]
    public void Voxels_sees_only_a_clear_line_and_counts_what_else_happened()
    {
        var p = Voxels();
        var answers = new Dictionary<EntityId, VoxelSightResult> { [A] = Wall, [B] = Open, [C] = No("NotLoaded") };
        var asked = new List<(EntityId From, EntityId To, VoxelSightOptions Options)>();
        var seen = p.Look(Self, At, Three, () => null, (from, to, o) => { asked.Add((from, to, o)); return answers[to]; });
        Assert.Equal([B], seen);   // not A (behind a wall), not C (unknown is not seen)
        Assert.Equal([A, B, C], asked.Select(x => x.To));
        Assert.All(asked, x => Assert.Equal((Self, new WorldPosition(0, 1.5, 0), new WorldPosition(0, 1.5, 0)), (x.From, x.Options.FromOffset, x.Options.ToOffset)));
        var s = p.Stats;
        Assert.Equal((1ul, 3ul, 1ul, 1ul, 1ul, "NotLoaded"), (s.Updates, s.Checks, s.Visible, s.Occluded, s.NotLoaded, s.LastRefusal));

        // each refusal has its counter; another reason is OtherRefused; the last refusal is the latest look's
        foreach (var (reason, read) in new (string, Func<NpcPerceptionStats, ulong>)[]
                 { ("OutOfGrid", x => x.OutOfGrid), ("BudgetExceeded", x => x.BudgetExceeded), ("EntityGone", x => x.EntityGone), ("WorldNotReady", x => x.OtherRefused) })
        {
            var before = read(p.Stats);
            Assert.Empty(p.Look(Self, At, [Three[0]], () => null, (_, _, _) => No(reason)));
            Assert.Equal(before + 1, read(p.Stats));
            Assert.Equal(reason, p.Stats.LastRefusal);
        }
        // a look with no refusal clears it
        Assert.Equal([A], p.Look(Self, At, [Three[0]], () => null, (_, _, _) => Open));
        Assert.Null(p.Stats.LastRefusal);
        Assert.Equal(6ul, p.Stats.Updates);
    }

    [Fact]
    public void A_capability_that_is_off_sees_nothing_and_never_falls_back()
    {
        var p = Voxels();
        var asked = 0;
        var seen = p.Look(Self, At, Three, () => "game test pending", (_, _, _) => { asked++; return Open; });
        Assert.Empty(seen);
        Assert.Equal(0, asked);
        var s = p.Stats;
        Assert.Equal((1ul, 0ul, 1ul, "queries.line_of_sight: game test pending"), (s.Updates, s.Checks, s.CapabilityUnavailableUpdates, s.LastRefusal));
        p.Look(Self, At, Three, () => "game test pending", (_, _, _) => Open);
        Assert.Equal(2ul, p.Stats.CapabilityUnavailableUpdates);   // once per look

        // it goes in the middle of a look: what was seen before is dropped, the rest is not asked
        var q = Voxels();
        var calls = 0;
        var partial = q.Look(Self, At, Three, () => null, (_, to, _) => ++calls == 2 ? throw new KilnUnavailableException("queries.line_of_sight", "faulted") : Open);
        Assert.Empty(partial);
        Assert.Equal(2, calls);
        Assert.Equal((1ul, 1ul, 1ul), (q.Stats.CapabilityUnavailableUpdates, q.Stats.Visible, q.Stats.Updates));
        Assert.StartsWith("queries.line_of_sight", q.Stats.LastRefusal);
    }

    [Fact]
    public void A_candidate_that_is_no_ray_costs_no_question()
    {
        var p = Voxels();
        var asked = new List<EntityId>();
        (EntityId, WorldPosition)[] candidates = [(A, new(300, 10, 0)), (B, At), (C, new(5, 10, 0))];   // too far for a ray; the same point; fine
        var seen = p.Look(Self, At, candidates, () => null, (_, to, _) => { asked.Add(to); return Open; });
        Assert.Equal([C], seen);
        Assert.Equal([C], asked);
        var s = p.Stats;
        Assert.Equal((1ul, 1ul, 1ul, 1ul), (s.Checks, s.OutOfRange, s.OtherRefused, s.Visible));
        // the service itself refusing the numbers is the same outcome
        var q = Voxels();
        Assert.Empty(q.Look(Self, At, [Three[0]], () => null, (_, _, _) => throw new ArgumentException("no ray")));
        Assert.Equal((1ul, "DegenerateRay"), (q.Stats.OtherRefused, q.Stats.LastRefusal));
    }

    [Fact]
    public void A_policy_is_checked_without_a_world()
    {
        Assert.Null(NpcPerception.Problem(new NpcPerceptionPolicy()));
        Assert.Null(NpcPerception.Problem(new NpcPerceptionPolicy { Occlusion = NpcOcclusionMode.Voxels, FromOffset = new(-32, 32, 0) }));
        Assert.NotNull(NpcPerception.Problem(null));
        Assert.NotNull(NpcPerception.Problem(new NpcPerceptionPolicy { Occlusion = (NpcOcclusionMode)2 }));
        Assert.NotNull(NpcPerception.Problem(new NpcPerceptionPolicy { FromOffset = new(0, 32.5, 0) }));
        Assert.NotNull(NpcPerception.Problem(new NpcPerceptionPolicy { ToOffset = new(double.NaN, 0, 0) }));
    }

    private sealed class StillBrain : NpcBrain
    {
        public override void Tick(NpcControl npc) { }
    }

    private static ModBlocks Mod(string id) => new(id, 1) { ManifestClients = "mod" };

    [Fact]
    public void A_creature_takes_its_behaviours_policy_and_a_policy_without_a_brain_is_an_error()
    {
        var voxels = new NpcPerceptionPolicy { Occlusion = NpcOcclusionMode.Voxels, ToOffset = new(0, 0.8, 0) };
        // with a brain it compiles
        Compiler.Lower(Mod("perception-brain"), "guard", new CreatureBlock { Body = Bodies.Vukah, Behaviour = Behaviours.None with { Brain = () => new StillBrain(), Perception = voxels } });
        // without one the block is wrong, at the field
        var e = Assert.Throws<BlockError>(() => Compiler.Lower(Mod("perception-nobrain"), "guard", new CreatureBlock { Body = Bodies.Vukah, Behaviour = Behaviours.None with { Perception = voxels } }));
        Assert.Equal("behaviour.perception", e.Field);
        var missing = Assert.Throws<BlockError>(() => Compiler.Lower(Mod("perception-missing-brain"), "guard", new CreatureBlock
        {
            Body = Bodies.Vukah,
            Behaviour = Behaviours.None with { BrainName = "perception-brain-not-registered", Perception = voxels },
        }));
        Assert.Equal("behaviour.brain", missing.Field); // the existing named-brain validator runs before perception
        var bad = Assert.Throws<BlockError>(() => Compiler.Lower(Mod("perception-bad"), "guard",
            new CreatureBlock { Body = Bodies.Vukah, Behaviour = Behaviours.None with { Brain = () => new StillBrain(), Perception = voxels with { FromOffset = new(0, 99, 0) } } }));
        Assert.Equal("behaviour.perception", bad.Field);
        // the default policy with no brain is every creature of before
        Compiler.Lower(Mod("perception-default"), "guard", new CreatureBlock { Body = Bodies.Vukah, Behaviour = Behaviours.None });
    }

    [Fact]
    public void JSON_writes_the_same_policy()
    {
        var b = BlockJson.Parse<BehaviourBlock>("""{ "brain": "guard", "perception": { "occlusion": "Voxels", "fromOffset": { "x": 0, "y": 1.5, "z": 0 }, "toOffset": { "x": 0, "y": 0.8, "z": 0 } } }""");
        Assert.Equal(new NpcPerceptionPolicy { Occlusion = NpcOcclusionMode.Voxels, ToOffset = new(0, 0.8, 0) }, b.Perception);
        Assert.Equal(NpcOcclusionMode.Voxels, BlockJson.Parse<BehaviourBlock>("""{ "brain": "guard", "perception": { "occlusion": "voxels" } }""").Perception.Occlusion);
        Assert.Equal(new NpcPerceptionPolicy(), BlockJson.Parse<BehaviourBlock>("""{ "brain": "guard" }""").Perception);
        Assert.Throws<System.Text.Json.JsonException>(() => BlockJson.Parse<BehaviourBlock>("""{ "brain": "guard", "perception": { "occlusion": "xray" } }"""));
        Assert.Throws<System.Text.Json.JsonException>(() => BlockJson.Parse<BehaviourBlock>("""{ "brain": "guard", "perception": { "sees": "all" } }"""));
    }
}
