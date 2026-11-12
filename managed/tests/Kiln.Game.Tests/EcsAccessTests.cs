// SPDX-License-Identifier: MIT
// Typed access (Kiln.Ecs.TryGet/Get/Has/All/Supports/WhyNot) and its first-use check. The check is a pure function
// over the generated types (Kiln.Game.types) and a live type table; here the live table is a fake made from the
// made-up fixture's types, then changed one way at a time. The reads themselves need a game (lab task).
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Game.Tests;

public class EcsAccessTests
{
    static GeneratedTypes Lanterns() =>
        GeneratedTypes.Parse(File.ReadAllText(GenRepo.Fixture("gen", "expected", "Kiln.Game.types")));

    const string FixtureExe = "0000000000000000000000000000000000000000000000000000000000000001";

    /// <summary>A live table that is a copy of the generated types, so that a test can change it.</summary>
    sealed class FakeLive : ILiveTypes
    {
        public readonly Dictionary<string, Fake> Types = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Finds = new(StringComparer.Ordinal);

        public FakeLive(GeneratedTypes g)
        {
            foreach (var t in g.ByQn.Values)
                Types[t.Qn] = new Fake
                {
                    Kind = t.Kind, Size = t.Size, Hash = t.Hash, Inner = t.Inner,
                    Fields = [.. g.AllFields(t).Select(f => (f.Name, f.Offset, f.Type))],
                    Values = [.. t.Values.Select(v => (v.Name, (long)v.Value))],
                };
        }

        public ILiveType? Find(string qn)
        {
            Finds[qn] = Finds.GetValueOrDefault(qn) + 1;
            return Types.GetValueOrDefault(qn);
        }
    }

    sealed class Fake : ILiveType
    {
        public int Kind { get; set; }
        public int Size { get; set; }
        public uint Hash { get; set; }
        public string? Inner { get; set; }
        public List<(string Name, int Offset, string Type)> Fields = [];
        public List<(string Name, long Value)> Values = [];
        IReadOnlyList<(string Name, int Offset, string Type)> ILiveType.AllFields => Fields;
        IReadOnlyList<(string Name, long Value)> ILiveType.Values => Values;
    }

    static string? Why(GeneratedTypes g, string qn, ILiveTypes? live, string? exe = null) =>
        LayoutCheck.WhyNot(g, qn, live, () => exe);

    [Fact]
    public void Equal_layouts_are_supported_and_each_nested_type_is_looked_up_once()
    {
        var g = Lanterns();
        var live = new FakeLive(g);
        var memo = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var qn in new[] { "keen::ecs::Lantern", "keen::ecs::LanternLit", "keen::ecs::Comet", "keen::ecs::Warmth", "keen::ecs::Gauges", "keen::ecs::Unlit" })
            Assert.Null(LayoutCheck.WhyNot(g, qn, live, () => null, memo));
        Assert.Equal(1, live.Finds["keen::ecs::LampId"]);   // in Lantern (through a typedef) and in LanternLit
        Assert.Equal(1, live.Finds["keen::LanternMode"]);
        Assert.Equal(1, live.Finds["keen::uint32"]);
    }

    [Fact]
    public void A_moved_field_is_not_supported()
    {
        var g = Lanterns();
        var live = new FakeLive(g);
        var f = live.Types["keen::ecs::Lantern"].Fields;
        f[f.FindIndex(x => x.Name == "mode")] = ("mode", 9, "keen::LanternMode");
        Assert.Equal("keen::ecs::Lantern.mode: offset 8 generated, 9 live", Why(g, "keen::ecs::Lantern", live));
    }

    [Fact]
    public void A_resized_type_is_not_supported()
    {
        var g = Lanterns();
        var live = new FakeLive(g);
        live.Types["keen::ecs::Lantern"].Size = 144;
        Assert.Equal("keen::ecs::Lantern: size 136 generated, 144 live", Why(g, "keen::ecs::Lantern", live));
    }

    [Fact]
    public void A_changed_nested_type_makes_the_outer_type_unsupported()
    {
        var g = Lanterns();
        var live = new FakeLive(g);
        var f = live.Types["keen::glimmer::Trail"].Fields;
        f[f.FindIndex(x => x.Name == "kind")] = ("kind", 5, "keen::glimmer::Kind");
        Assert.Equal("keen::ecs::Comet.trail: keen::glimmer::Trail.kind: offset 4 generated, 5 live", Why(g, "keen::ecs::Comet", live));

        live = new FakeLive(g);
        live.Types["keen::ecs::Spark"].Size = 12;   // inside StaticArray<Spark,3>
        Assert.Equal("keen::ecs::Lantern.sparks: keen::ecs::Spark: size 8 generated, 12 live", Why(g, "keen::ecs::Lantern", live));

        live = new FakeLive(g);
        live.Types["keen::LanternMode"].Values[1] = ("Dim", 5);
        Assert.Equal("keen::ecs::Lantern.mode: keen::LanternMode.Dim: value 1 generated, 5 live", Why(g, "keen::ecs::Lantern", live));

        live = new FakeLive(g);
        var t = live.Types["keen::ecs::Spark"].Fields;
        t[t.FindIndex(x => x.Name == "tilt")] = ("tilt", 6, "keen::Notch");
        Assert.Equal("keen::ecs::Lantern.sparks: keen::ecs::Spark.tilt: type keen::Tilt generated, keen::Notch live", Why(g, "keen::ecs::Lantern", live));
    }

    [Fact]
    public void A_base_class_field_counts()
    {
        var g = Lanterns();
        var live = new FakeLive(g);
        var f = live.Types["keen::ecs::Warmth"].Fields;
        f.RemoveAt(f.FindIndex(x => x.Name == "count"));
        Assert.Equal("keen::ecs::Warmth.count: the game's type has no such field", Why(g, "keen::ecs::Warmth", live));
        live = new FakeLive(g);
        live.Types["keen::ecs::Warmth"].Fields.Add(("glow", 20, "keen::float32"));
        Assert.Equal("keen::ecs::Warmth: 4 fields generated, 5 live (glow new)", Why(g, "keen::ecs::Warmth", live));
    }

    [Fact]
    public void A_type_the_game_no_longer_has_is_not_supported()
    {
        var g = Lanterns();
        var live = new FakeLive(g);
        live.Types.Remove("keen::ecs::Unlit");
        Assert.Equal("keen::ecs::Unlit: the game has no such type", Why(g, "keen::ecs::Unlit", live));
        live.Types.Remove("keen::Moment");
        Assert.Equal("keen::ecs::Lantern.lit: keen::Moment: the game has no such type", Why(g, "keen::ecs::Lantern", live));
    }

    [Fact]
    public void Without_a_live_table_only_the_exe_the_dump_was_made_from_is_supported()
    {
        var g = Lanterns();
        Assert.Null(Why(g, "keen::ecs::Lantern", null, FixtureExe));
        Assert.Null(Why(g, "keen::ecs::Lantern", null, FixtureExe.ToUpperInvariant()));
        Assert.Equal("the game's type table (kiln.types) is not available and this Kiln.Game was generated for server 00000000, " +
                     "not for the running exe abcdef01", Why(g, "keen::ecs::Lantern", null, "abcdef01" + new string('0', 56)));
        Assert.StartsWith("the game's type table (kiln.types) is not available and the running exe could not be read",
            Why(g, "keen::ecs::Lantern", null, null));
        Assert.Equal("keen::ecs::Torch is not among the types this Kiln.Game was generated with", Why(g, "keen::ecs::Torch", null, FixtureExe));
    }

    [Fact]
    public void The_built_Kiln_Game_carries_every_type_of_its_dump()
    {
        var gen = EcsSupport.Generated;
        Assert.NotNull(gen);
        var dumps = GameDump.Committed();
        if (dumps.Count == 0) Assert.Skip("skipped: no schema-2 dump under sigdb/types yet");
        var d = dumps.Select(GameDump.Read).First(x => x.Exe == gen!.Exe);
        Assert.Equal(d.Types.Count, gen!.ByQn.Count);
        // The generated layouts against themselves through the live-table path: every component is supported.
        var live = new FakeLive(gen);
        var memo = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var c in d.Components)
            Assert.Null(LayoutCheck.WhyNot(gen, c, live, () => null, memo));
    }

    // --- the calls, in this test process (no game: no kiln.world, no kiln.types) ------------------------------------

    [Fact]
    public void With_no_game_the_check_says_why_and_the_reads_throw_kiln_world_unavailable()
    {
        Assert.False(Ecs.Available);
        Assert.False(Ecs.Supports<Ecs.NetworkHealth>());
        var why = Ecs.WhyNot<Ecs.NetworkHealth>();
        Assert.NotNull(why);
        Assert.Contains("the game's type table (kiln.types) is not available", why, StringComparison.Ordinal);
        var e = Assert.Throws<KilnUnavailableException>(() => Ecs.TryGet<Ecs.NetworkHealth>(default, out _));
        Assert.Equal("kiln.world", e.Capability);
        Assert.Equal("kiln.world", Assert.Throws<KilnUnavailableException>(() => Ecs.Get<Ecs.Health>(default)).Capability);
        Assert.Equal("kiln.world", Assert.Throws<KilnUnavailableException>(() => Ecs.Has<Ecs.Health>(default)).Capability);
        Assert.Equal("kiln.world", Assert.Throws<KilnUnavailableException>(() => Ecs.All<Ecs.Health>()).Capability);
    }

    [Fact]
    public void An_unsupported_type_is_warned_about_once_per_mod()
    {
        var lines = new List<(ulong, string)>();
        EcsSupport.WarnSink = (owner, text) => lines.Add((owner, text));
        try
        {
            EcsSupport.Unsupported(typeof(EcsAccessTests), "keen::ecs::X: size 8 generated, 12 live");
            EcsSupport.Unsupported(typeof(EcsAccessTests), "keen::ecs::X: size 8 generated, 12 live");
            Assert.Single(lines);
            Assert.Equal("Kiln.Game: Kiln.Game.Tests.EcsAccessTests cannot be read on this game build, typed access to it " +
                         "returns nothing: keen::ecs::X: size 8 generated, 12 live", lines[0].Item2);
        }
        finally { EcsSupport.WarnSink = null; }
    }

    [Fact]
    public void The_legacy_types_get_the_same_check_by_name()
    {
        var gen = EcsSupport.Generated!;
        foreach (var name in new[] { "NetworkHealth", "Health", "Mana", "Stamina", "CurrentTransform", "LastTransform", "Velocity", "TeleportCount", "NpcCommon" })
            Assert.Null(LayoutCheck.LegacyWhyNot(gen, GenRepo.GameAssembly.GetType("Kiln.GameTypes." + name)!, "keen::ecs::" + name));
        // The dev.24 shape with one field renamed: its NetworkHealth does not fit the generated keen::ecs::NetworkHealth.
        var ctx = new AssemblyLoadContext("legacy-check", isCollectible: true);
        try
        {
            var renamed = ctx.LoadFromAssemblyPath(GenRepo.Built("legacy", "Dev24Renamed", "Kiln.Game.dll"));
            Assert.Equal("Kiln.GameTypes.NetworkHealth.healthMaximum: keen::ecs::NetworkHealth has no such field",
                LayoutCheck.LegacyWhyNot(gen, renamed.GetType("Kiln.GameTypes.NetworkHealth")!, "keen::ecs::NetworkHealth"));
        }
        finally { ctx.Unload(); }
    }

    // --- the attribute view and the doc's example --------------------------------------------------------------------

    static Span<TValue> Slots<T, TValue>(ref T value) where T : unmanaged, IGameAttribute<T, TValue> where TValue : unmanaged =>
        T.ValuesOf(ref value);

    [Fact]
    public void An_attribute_component_has_its_slots_as_a_span_over_dataStorage()
    {
        var h = new Ecs.Health();
        h.Values[2] = 7;
        Assert.Equal(7, h.dataStorage[2]);
        Assert.Equal(8, h.Values.Length);
        Slots<Ecs.Health, int>(ref h)[3] = 9;
        Assert.Equal(9, h.dataStorage[3]);
    }

    /// <summary>docs/kiln/typegen.md §4's example, compiled here; it runs only with a game.</summary>
    static string DocExample(EntityId entity)
    {
        if (!Ecs.Available || !Ecs.Supports<Ecs.LastTransform>()) return "no typed reads on this build: " + Ecs.WhyNot<Ecs.LastTransform>();
        var lines = new List<string>();
        if (Ecs.TryGet<Ecs.LastTransform>(entity, out var t))
            lines.Add($"at {t.transform.position.x} (scale {t.transform.scale.y}), moving {t.velocity.z}");   // nested fields
        if (Ecs.Get<Ecs.NpcCommon>(entity) is { } npc)
            lines.Add($"mode {npc.movementMode}, step {npc.history[npc.historyIndex].movement.x}");          // an enum, an array element
        if (Ecs.Get<Ecs.Health>(entity) is { } hp)
            lines.Add($"health slots {string.Join(",", hp.Values.ToArray())}");                               // the attribute view
        return string.Join("; ", lines);
    }

    [Fact]
    public void The_doc_example_compiles_and_says_why_without_a_game()
    {
        Assert.StartsWith("no typed reads on this build: ", DocExample(default), StringComparison.Ordinal);
    }

    [Fact]
    public void The_root_class_reserves_every_member_name_it_declares()
    {
        var reserved = File.ReadAllText(GenRepo.Path_("tools", "typegen", "typegen.py"));
        foreach (var m in typeof(Ecs).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .Where(m => m is MethodInfo or PropertyInfo or FieldInfo && !m.Name.StartsWith('<') && !m.Name.StartsWith("get_", StringComparison.Ordinal)))
            Assert.True(reserved.Contains($"\"{m.Name}\"", StringComparison.Ordinal), $"Ecs.{m.Name} is not in ROOT_RESERVED of typegen.py");
    }
}
