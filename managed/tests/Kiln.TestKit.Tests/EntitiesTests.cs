// SPDX-License-Identifier: MIT
// The fake world (KilnHarness.Entities) under the real Kiln.World and Kiln.Game: a mod's world code runs offline as it
// runs in a server. The mod is TkWorld, built with Kiln.Sdk, loaded from its built folder: the "writes" its manifest
// lists are the ones the build read from its code.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using TestKitTypes;
using TkWorld;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class EntitiesTests
{
    private static ModHandle Load(KilnHarness kiln) => kiln.LoadFolder(TestMods.Folder("TkWorld"));

    private static Ecs.Fuel Water(int units, int max)
    {
        var f = new Ecs.Fuel { storageOffset = 8, storageCount = 2 };
        MemoryMarshal.AsBytes(new Span<Ecs.Fuel>(ref f))[6] = 1;   // flags: has values
        f.Values[0] = units;
        f.Values[1] = max;
        return f;
    }

    private static string Line(KilnHarness kiln, string prefix) => kiln.Lines("tk-world", prefix).LastOrDefault() ?? "(no line: " + kiln.DumpLogs() + ")";

    [Fact]
    public void The_kit_s_mirror_of_the_table_has_the_header_s_sizes_and_Kiln_World_s()
    {
        Assert.Equal(280, Unsafe.SizeOf<WorldAbi.Table>());
        Assert.Equal(Unsafe.SizeOf<WorldWritesTable>(), Unsafe.SizeOf<WorldAbi.Table>());
        Assert.Equal((72, 80, 256, 72), (Unsafe.SizeOf<WorldAbi.WriteRequest>(), Unsafe.SizeOf<WorldAbi.WriteResult>(), Unsafe.SizeOf<WorldAbi.WriteInfo>(), Unsafe.SizeOf<WorldAbi.WriteStatus>()));
        Assert.Equal((Unsafe.SizeOf<WriteRequestNative>(), Unsafe.SizeOf<WriteResultNative>(), Unsafe.SizeOf<WriteInfoNative>(), Unsafe.SizeOf<WriteStatusNative>()),
            (Unsafe.SizeOf<WorldAbi.WriteRequest>(), Unsafe.SizeOf<WorldAbi.WriteResult>(), Unsafe.SizeOf<WorldAbi.WriteInfo>(), Unsafe.SizeOf<WorldAbi.WriteStatus>()));
        Assert.Equal(240, (int)Marshal.OffsetOf<WorldAbi.Table>(nameof(WorldAbi.Table.WriteSubmit)));
        Assert.Equal(184, (int)Marshal.OffsetOf<WorldAbi.Table>(nameof(WorldAbi.Table.GetStatus)));
        Assert.Equal(Unsafe.SizeOf<WorldNative.Status>(), Unsafe.SizeOf<WorldAbi.Status>());
        Assert.Equal(Unsafe.SizeOf<WorldNative.Transform>(), Unsafe.SizeOf<WorldAbi.Transform>());
        Assert.Equal(World.ComponentHash("Fuel"), WorldAbi.Hash("Fuel"));
        Assert.Equal(Ecs.Fuel.Hash, WorldAbi.Hash("Fuel"));
    }

    [Fact]
    public void The_build_listed_the_types_the_mod_s_code_writes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestMods.Folder("TkWorld"), "kiln.mod.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var writes = doc.RootElement.GetProperty("writes").EnumerateArray().Select(e => e.GetString()).Order().ToArray();
        Assert.Equal(["Fuel", "ScaleComponent"], writes);   // not Stamina: the code names it only as a string
    }

    [Fact]
    public void Entities_and_components_are_what_Kiln_World_and_Kiln_Game_read()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var well = kiln.Entities.Create(new WorldPosition(10, 20, 30));
        var rock = kiln.Entities.Create();
        kiln.Entities.Set(well, Water(100, 6400)).Set(well, new Ecs.ScaleComponent { uniformScale = 1f });
        kiln.Entities.Set(rock, new Ecs.ScaleComponent { uniformScale = 2f });

        kiln.Run(() =>
        {
            Assert.True(World.Available);
            Assert.True(World.Exists(well));
            Assert.False(World.Exists(new EntityId(77777)));
            Assert.True(World.Has(well, "Fuel"));
            Assert.False(World.Has(rock, "Fuel"));
            Assert.Equal(16, World.ComponentSize(well, "Fuel"));
            Assert.True(World.TryRead<float>(rock, "ScaleComponent", out var scale) && scale == 2f);
            Assert.Equal([well], World.Find("Fuel"));
            Assert.Equal([well, rock], World.Find("ScaleComponent").OrderBy(e => e.Value));
            Assert.Empty(World.Find("NoSuchComponentKiln"));
            Assert.Empty(World.Find("Health"));               // a component nobody has
            Assert.Equal(new WorldPosition(10, 20, 30), World.Position(well));
            Assert.Null(World.Position(rock));                // made without a position
            Assert.Equal(2, World.EntityCount);
            Assert.True(World.Status is { WorldLive: true, Find: true, Teleport: false, Programs: false });

            // entity sets
            Assert.Equal([well, rock], World.Query("ScaleComponent").OrderBy(e => e.Value));
            Assert.Equal([rock], World.Query(["ScaleComponent"], ["Fuel"]));
            Assert.Equal([well], World.Query<Ecs.Fuel, Ecs.ScaleComponent>());
            var set = World.Query<Ecs.Fuel>();
            Assert.True(set.Contains(well) && !set.Contains(rock) && set.Count == 1);

            // Kiln.Game's typed reads: the first-use check runs against the kit's type table
            Assert.Null(Ecs.WhyNot<Ecs.Fuel>());
            Assert.True(Ecs.TryGet<Ecs.Fuel>(well, out var fuel));
            Assert.Equal((100, 6400), (fuel.Values[0], fuel.Values[1]));
            Assert.False(Ecs.Has<Ecs.Fuel>(rock));
            Assert.Equal([well], Ecs.All<Ecs.Fuel>());

            // reflected by name, the game's own field names
            Assert.True(TypeTable.Available);
            Assert.Equal(16, TypeTable.Find("Fuel")!.Size);
            Assert.Equal("keen::ecs::Fuel", TypeTable.Find("keen::ecs::Fuel")!.QualifiedName);
            Assert.Equal(100L, Reflected.Component(well, "Fuel")!["dataStorage[0]"]);
            Assert.Contains(TypeTable.Components, t => t.Name == "ScaleComponent");
            Assert.Equal(687, TypeTable.Components.Count);
        });

        // a raw store is seen by the test's hand, and the other way round
        kiln.Run(() => Assert.True(World.WriteRaw(rock, "ScaleComponent", 0, 3f)));
        Assert.Equal(3f, kiln.Entities.Get<Ecs.ScaleComponent>(rock).uniformScale);
        Assert.True(kiln.Entities.Remove<Ecs.ScaleComponent>(rock));
        Assert.False(kiln.Entities.TryGet<Ecs.ScaleComponent>(rock, out _));
        Assert.True(kiln.Entities.Destroy(well));
        Assert.Equal([rock], kiln.Entities.All);
        kiln.Run(() => Assert.False(World.Exists(well)));
        Assert.Throws<ArgumentException>(() => kiln.Entities.Set(new EntityId(4242), new Ecs.ScaleComponent()));
    }

    [Fact]
    public void A_mod_s_tracked_write_runs_at_the_next_step_answers_and_raises_its_event()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        Assert.True(kiln.HasLog("loaded: tracked writes True; Fuel Ready, attribute True, 0 game writer(s); Stamina NotDeclared", "tk-world"), kiln.DumpLogs());
        var can = kiln.Entities.Create();
        var barrel = kiln.Entities.Create();
        kiln.Entities.Set(can, Water(0, 6400)).Set(barrel, Water(50, 100));

        kiln.RunCommand("tk-fill 3200");
        Assert.Equal(0, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);   // queued: nothing is written before the world steps
        kiln.Step(4);
        Assert.Equal(3200, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);
        Assert.Equal(100, kiln.Entities.Get<Ecs.Fuel>(barrel).Values[0]);   // the mod clamps to the maximum itself
        Assert.Contains($"fill {can}: Written: Fuel of {can}; steps 1; writers []; save ?", kiln.Lines("tk-world", $"fill {can}"));
        Assert.Equal("filled 2", Line(kiln, "filled"));
        Assert.Contains($"event: Fuel of {can} written by tk-world, now 3200", kiln.Lines("tk-world", "event:"));

        // the record of what the world ran
        var writes = kiln.Entities.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal(("tk-world", can, "Fuel", WorldWriteStatus.Written), (writes[0].ModId, writes[0].Entity, writes[0].Component, writes[0].Status));
        Assert.Equal((0, 3200), (BitConverter.ToInt32(writes[0].Before, 8), BitConverter.ToInt32(writes[0].After, 8)));

        // the same value again: unchanged, no event
        var events = kiln.Lines("tk-world", "event:").Count;
        kiln.RunCommand("tk-fill 3200");
        kiln.Step(4);
        Assert.Contains($"fill {can}: Unchanged: Fuel of {can}; steps 1; writers []; save ?", kiln.Lines("tk-world", $"fill {can}"));
        Assert.Equal(events, kiln.Lines("tk-world", "event:").Count);

        // typed whole-component write of a plain component; a field by its path; what the mod reads back
        kiln.Entities.Set(can, new Ecs.ScaleComponent { uniformScale = 1f });
        kiln.RunCommand($"tk-scale {can.Value} 1.5");
        kiln.RunCommand($"tk-field {can.Value} 64");
        kiln.Step(2);
        Assert.Equal($"scale: Written: ScaleComponent of {can}; steps 1; kept unchecked", Line(kiln, "scale:"));
        Assert.Equal($"field: Written: Fuel of {can}", Line(kiln, "field:"));
        Assert.Equal(1.5f, kiln.Entities.Get<Ecs.ScaleComponent>(can).uniformScale);
        var read = kiln.RunCommand($"tk-read {can.Value}");
        Assert.Equal($"fuel 64/6400; reflected 64; has scale True; at nowhere; exists True", string.Join("", read.Output));
    }

    [Fact]
    public void What_the_game_refuses_comes_back_as_an_answer_with_the_reason()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var can = kiln.Entities.Create();
        kiln.Entities.Set(can, Water(10, 6400));
        kiln.Entities.SetBytes(can, "Stamina", new byte[16]);

        // a type the manifest does not list: refused, and nothing is written raw instead
        kiln.RunCommand($"tk-stamina {can.Value}");
        kiln.Step(2);
        var line = Line(kiln, "stamina:");
        Assert.StartsWith($"stamina: NotDeclared: Stamina of {can}: no loaded mod lists Stamina under \"writes\"", line);
        Assert.Contains("add \"writes\": [\"Stamina\"]", line);
        Assert.Equal(new byte[16], kiln.Entities.GetBytes(can, "Stamina"));

        // an attribute's header is the game's
        kiln.RunCommand($"tk-header {can.Value}");
        kiln.Step(2);
        Assert.StartsWith($"header: Refused: Fuel of {can}: Fuel is an attribute", Line(kiln, "header:"));
        Assert.Equal(10, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);

        // a change that throws writes nothing and is the mod's fault, not the harness's
        kiln.RunCommand($"tk-throw {can.Value}");
        kiln.Step(2);
        Assert.StartsWith($"throw: Faulted: Fuel of {can}: the change threw InvalidOperationException: on purpose", Line(kiln, "throw:"));
        Assert.Equal(10, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);

        // an entity that is not there, and a bad argument
        var mod = kiln.Mod("tk-world");
        var gone = kiln.As(mod, () => World.WriteAt(new EntityId(999_999), "Fuel", 8, 1));
        kiln.Step(2);
        Assert.True(gone.IsCompletedSuccessfully);
        Assert.Equal((false, WorldWriteStatus.NoComponent), (gone.Result.Ok, gone.Result.Status));
        Exception? bad = null;
        try { kiln.As(mod, () => World.WriteAt(EntityId.None, "Fuel", 8, 1)); }
        catch (Exception e) { bad = e; }
        Assert.IsType<ArgumentException>(bad);   // a bad argument throws at the call, it is no answer
        var past = kiln.As(mod, () => World.WriteAt(can, "Fuel", 14, 1));
        kiln.Step(2);
        Assert.Equal((WorldWriteStatus.OutOfRange, 16), (past.Result.Status, past.Result.ComponentSize));
    }

    [Fact]
    public void A_fake_game_program_that_writes_the_type_makes_the_answer_wait_and_say_whether_the_value_stayed()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var can = kiln.Entities.Create();
        kiln.Entities.Set(can, Water(0, 6400));
        var mod = kiln.Mod("tk-world");

        // a writer that leaves the value alone (the game's refill of a full container)
        using (kiln.Entities.GameWrites<Ecs.Fuel>("fuel_setup"))
        {
            Assert.Equal((1, "fuel_setup"), kiln.As(mod, () => (World.WriteInfo<Ecs.Fuel>().GameWriterCount, World.WriteInfo<Ecs.Fuel>().GameWriters[0])));
            var kept = kiln.As(mod, () => World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 500));
            kiln.Step();
            Assert.False(kept.IsCompleted);                       // written, and waiting for the game's programs to run once more
            Assert.Equal(500, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);
            kiln.Step();
            Assert.True(kept.IsCompletedSuccessfully);
            Assert.Equal((true, 2, 1), (kept.Result.Kept, kept.Result.Steps, kept.Result.GameWriterCount));
            Assert.Equal(["fuel_setup"], kept.Result.GameWriters);
        }

        // a writer that derives the value every step: Kiln's write is the last of its step and gone in the next
        using (kiln.Entities.GameWrites<Ecs.Fuel>("fuel_drain", (EntityId _, ref Ecs.Fuel f) => f.Values[0] = 0))
        {
            var lost = kiln.As(mod, () => World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 700));
            kiln.Step();
            Assert.Equal(700, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);   // after the game's writer of that step
            kiln.Step();
            Assert.Equal((true, false), (lost.Result.Ok, lost.Result.Kept));
            Assert.Equal(0, kiln.Entities.Get<Ecs.Fuel>(can).Values[0]);
        }

        // no writer any more: answered in one step, unchecked
        var alone = kiln.As(mod, () => World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 900));
        kiln.Step();
        Assert.Equal((null, 1), (alone.Result.Kept, alone.Result.Steps));

        // what the game's tracking does with a type is the test's to say
        Assert.Null(alone.Result.SaveTracked);
        kiln.Entities.Track("Fuel", save: true, replication: false);
        var tracked = kiln.As(mod, () => World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 901));
        kiln.Step();
        Assert.Equal((true, false), (tracked.Result.SaveTracked, tracked.Result.ReplicationTracked));
    }

    [Fact]
    public void The_lists_are_read_when_the_server_starts_as_in_the_game()
    {
        using var kiln = new KilnHarness();
        // loaded from its class with the test's own manifest: Stamina listed by hand, Fuel not
        var typed = kiln.Load<TkWorldMod>(new ModManifest("tk-typed").Writes("Stamina"));
        kiln.Step();
        var e = kiln.Entities.Create();
        kiln.Entities.Set(e, Water(1, 10));
        kiln.Entities.SetBytes(e, "Stamina", new byte[16]);
        var stamina = kiln.As(typed, () => World.WriteAt(e, "Stamina", 8, 5));
        var fuel = kiln.As(typed, () => World.Update<Ecs.Fuel>(e, (ref Ecs.Fuel f) => f.Values[0] = 2));
        kiln.Step();
        Assert.True(stamina.Result.Ok, stamina.Result.Reason);
        Assert.Equal(5, BitConverter.ToInt32(kiln.Entities.GetBytes(e, "Stamina")!, 8));
        Assert.Equal(WorldWriteStatus.NotDeclared, fuel.Result.Status);

        // a mod loaded after the first step is a hot load: its "writes" were not there when the world started
        var late = Load(kiln);
        kiln.Step();
        Assert.True(late.IsLoaded, late.Error);
        Assert.Equal(WorldWriteState.NotListed, kiln.As(late, () => World.WriteInfo("Stamina").State));
        Assert.Equal(WorldWriteState.NotDeclared, kiln.As(late, () => World.WriteInfo<Ecs.Fuel>().State));
        var refused = kiln.As(late, () => World.Update<Ecs.Fuel>(e, (ref Ecs.Fuel f) => f.Values[0] = 3));
        kiln.Step();
        Assert.Equal(WorldWriteStatus.NotDeclared, refused.Result.Status);
        Assert.Contains("restart the server", refused.Result.Reason);
        Assert.Equal(1, kiln.Entities.Get<Ecs.Fuel>(e).Values[0]);
    }

    [Fact]
    public void A_fake_player_stands_in_the_world_and_can_carry_components()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var ada = kiln.Players.Join("Ada", position: new WorldPosition(1, 2, 3));
        kiln.Step();
        Assert.True(kiln.Entities.Exists(ada.Entity));
        kiln.Run(() =>
        {
            Assert.True(World.Exists(ada.Entity));
            Assert.Equal(new WorldPosition(1, 2, 3), World.Position(ada.Entity));
        });
        kiln.Entities.Set(ada.Entity, Water(7, 64));
        kiln.Run(() => Assert.Equal([ada.Entity], World.Find("Fuel")));
        kiln.RunCommand("tk-fill 64");
        kiln.Step(2);
        Assert.Equal(64, kiln.Entities.Get<Ecs.Fuel>(ada.Entity).Values[0]);
    }

    [Fact]
    public void What_is_not_faked_says_so()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var e = kiln.Entities.Create(new WorldPosition(0, 0, 0));
        kiln.Run(() =>
        {
            Assert.False(EcsPrograms.Available);                 // no scheduler: world programs do not run
            Assert.False(EcsPrograms.SupportsOtherWrites);
            Assert.False(Teleport.Available);
            Assert.False(GameSystems.Available);                 // no systems, no hooks, no profiler
            Assert.Empty(GameSystems.All);
            Assert.Equal(-1, GameEvents.Each("EntityDiedEvent", _ => { }));
        });
        Assert.Throws<NotSupportedException>(() => kiln.As(kiln.Mod("tk-world"), () => EcsPrograms.Bind("pulse", _ => { })));
        var moved = kiln.Run(() => Teleport.To(e, new WorldPosition(5, 5, 5)));
        Assert.True(moved.IsCompletedSuccessfully);
        Assert.False(moved.Result.Moved);
        Assert.Contains("unavailable", moved.Result.Reason);
        Assert.Contains(Coverage.All, r => r.Area == "World: tracked writes" && r.Kind == CoverageKind.RecordingFake);
    }

    [Fact]
    public void A_test_can_switch_the_world_off_and_the_next_harness_starts_clean()
    {
        using (var kiln = new KilnHarness())
        {
            Load(kiln);
            kiln.Step();
            var e = kiln.Entities.Create();
            kiln.Entities.Set(e, Water(1, 2));
            Assert.True(kiln.Run(() => World.SupportsTrackedWrites));
            kiln.Capabilities.Override("kiln.world", false);
            kiln.Step(26);   // Kiln.World looks at its capability row every 25 steps
            kiln.Run(() =>
            {
                Assert.False(World.SupportsTrackedWrites);
                Assert.False(World.Has(e, "Fuel"));              // the switched-off table has no entities
                Exception? off = null;
                try { World.WriteAt(e, "Fuel", 8, 5); }
                catch (Exception x) { off = x; }
                Assert.Equal("world.tracked_writes", Assert.IsType<KilnUnavailableException>(off).Capability);
            });
        }
        using var next = new KilnHarness();
        Load(next);
        next.Step();
        Assert.Empty(next.Entities.All);
        Assert.Empty(next.Entities.Writes);
        Assert.True(next.Run(() => World.SupportsTrackedWrites), next.DumpLogs());
        Assert.True(next.HasLog("loaded: tracked writes True", "tk-world"), next.DumpLogs());
    }
}
