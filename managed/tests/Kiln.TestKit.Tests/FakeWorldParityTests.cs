// SPDX-License-Identifier: MIT
// The fake world against the native service at the boundaries where a mod could tell them apart. The numbers and the
// cases are native/services/world/tests/parity.json, which the native host test (writes_test.cpp) runs too: the batch
// of one step, the declaration rules, an entity that goes away and comes back under the same id while its write
// waits, CurrentTransform's bytes, and WriteInfo asked from other threads while the tick thread changes the world.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using TkWorld;
using Xunit;

// The harness steps on the test's thread: a task is read once a step completed it, and the probes' tasks end with the test.
#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.TestKit.Tests;

public sealed class FakeWorldParityTests
{
    private static JsonNode Fixture(string file, [CallerFilePath] string self = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "native", "services", "world"))) { repo = dir; break; }
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "native", "services", "world", "tests", file)))!;
    }

    private static JsonNode Parity => Fixture("parity.json");

    private static ModHandle Load(KilnHarness kiln) => kiln.LoadFolder(TestMods.Folder("TkWorld"));

    private static IEnumerable<string> Expand(JsonNode spec)
    {
        if (spec is JsonArray list)
        {
            foreach (var item in list)
                foreach (var name in Expand(item!)) yield return name;
            yield break;
        }
        if (spec is JsonValue) { yield return (string)spec!; yield break; }
        var pattern = (string)spec["pattern"]!;
        var from = spec["from"] is { } f ? (int)f : 0;
        for (var i = from; i < from + (int)spec["count"]!; i++) yield return pattern.Replace("{i}", i.ToString());
    }

    [Fact]
    public void The_limits_are_the_native_ones()
    {
        var l = Parity["limits"]!;
        Assert.Equal(((int)l["perUpdate"]!, (int)l["maxPending"]!, (int)l["maxTypes"]!, (int)l["maxPerMod"]!, (int)l["deliverPerPre"]!, (int)l["maxBytes"]!),
            (WorldAbi.PerUpdate, WorldAbi.MaxPending, WorldAbi.MaxTypes, WorldAbi.MaxPerMod, WorldAbi.DeliverPerPre, WorldAbi.MaxBytes));
    }

    [Fact]
    public void The_kit_s_table_matches_the_header_field_for_field()
    {
        var abi = Fixture("abi.json");
        foreach (var (native, mirror) in (ValueTuple<string, Type>[])[("KilnWorldApi", typeof(WorldAbi.Table)),
                     ("KilnWorldWriteRequest", typeof(WorldAbi.WriteRequest)), ("KilnWorldWriteResult", typeof(WorldAbi.WriteResult)),
                     ("KilnWorldWriteInfo", typeof(WorldAbi.WriteInfo)), ("KilnWorldWriteStatus", typeof(WorldAbi.WriteStatus))])
        {
            var fields = abi[native]!["fields"]!.AsArray().Select(f => ((string)f![0]!, (int)f[1]!)).ToList();
            var mine = mirror.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .OrderBy(f => f.MetadataToken).Select(f => (f.Name, Offset: (int)Marshal.OffsetOf(mirror, f.Name))).ToList();
            Assert.Equal((int)abi[native]!["size"]!, Marshal.SizeOf(mirror));
            Assert.Equal(fields.Count, mine.Count);
            for (var i = 0; i < fields.Count; i++)
                Assert.True(fields[i].Item2 == mine[i].Offset, $"{mirror.Name}.{mine[i].Name} is at {mine[i].Offset}, {native}.{fields[i].Item1} at {fields[i].Item2}");
        }
    }

    // ---- which names the write program takes ----

    public static TheoryData<string> DeclarationCases() => [.. Parity["declarations"]!.AsArray().Select(c => (string)c!["case"]!)];

    [Theory]
    [MemberData(nameof(DeclarationCases))]
    public void Declarations_follow_the_native_admission(string name)
    {
        Assert.True(TypesFake.Available, TypesFake.Source);
        var c = Parity["declarations"]!.AsArray().Single(x => (string)x!["case"]! == name)!;
        var known = TypesFake.ComponentNames().Order(StringComparer.Ordinal).ToList();
        string Name(string reference)
        {
            var at = reference.IndexOf(':');
            var i = int.Parse(reference[(at + 1)..]);
            return reference[..at] == "known" ? known[i] : $"NoSuchComponentKiln{i}";
        }
        var mods = c["mods"]!.AsArray().Select(m => ((string)m!["id"]!, (IReadOnlyList<string>)[.. Expand(m["writes"]!).Select(Name)])).ToList();
        var lists = WriteLists.Admit(mods, TypesFake.IsComponent);
        string State(string mod, string reference)
        {
            var hash = WorldAbi.Hash(Name(reference));
            if (!lists.Types.TryGetValue(hash, out var state)) return "NotDeclared";
            if (mod.Length > 0 && !lists.Listed[mod].Contains(hash)) return "NotListed";
            return ((WorldWriteState)state).ToString();
        }
        foreach (var (reference, want) in c["expect"]!.AsObject()) Assert.Equal((string)want!, State("", reference));
        if (c["expectFor"] is JsonObject per)
            foreach (var (mod, states) in per)
                foreach (var (reference, want) in states!.AsObject()) Assert.Equal((string)want!, State(mod, reference));
    }

    [Fact]
    public void Seeded_bytes_do_not_make_an_unknown_type_writable()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var e = kiln.Entities.Create();
        var thrown = Assert.Throws<ArgumentException>(() => kiln.Entities.SetBytes(e, "NoSuchComponentKiln", new byte[8]));
        Assert.Contains("no component type 'NoSuchComponentKiln'", thrown.Message);
        var mod = kiln.Mod("tk-world");
        var r = kiln.As(mod, () => World.WriteAt(e, "NoSuchComponentKiln", 0, 1));
        kiln.Step();
        Assert.Equal(WorldWriteStatus.NotDeclared, r.Result.Status);
        Assert.Empty(kiln.Run(() => World.Find("NoSuchComponentKiln")));
    }

    // ---- one step runs 512 requests ----

    [Fact]
    public void One_step_runs_512_requests_and_the_next_the_rest()
    {
        var b = Parity["batch"]!;
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var e = kiln.Entities.Create();
        kiln.Entities.Set(e, new Ecs.ScaleComponent { uniformScale = 0f });
        var mod = kiln.Mod("tk-world");
        var tasks = kiln.As(mod, () => Enumerable.Range(1, (int)b["submitted"]!)
            .Select(i => World.Write(e, new Ecs.ScaleComponent { uniformScale = i })).ToList());
        kiln.Step();
        Assert.Equal((int)b["firstStep"]!, tasks.Count(t => t.IsCompleted));
        Assert.Equal((float)(int)b["firstStep"]!, kiln.Entities.Get<Ecs.ScaleComponent>(e).uniformScale);
        kiln.Step();
        Assert.Equal((int)b["firstStep"]! + (int)b["secondStep"]!, tasks.Count(t => t.IsCompleted));
        Assert.All(tasks, t => Assert.Equal(WorldWriteStatus.Written, t.Result.Status));
        Assert.Equal(2, tasks[^1].Result.Steps);   // it waited one step behind the full one
    }

    [Fact]
    public void Requests_past_the_limit_are_refused_refusals_included()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var e = kiln.Entities.Create();
        var mod = kiln.Mod("tk-world");
        // refusals (a type tk-world does not list) wait for their delivery and count
        var refused = kiln.As(mod, () => Enumerable.Range(0, (int)Parity["limits"]!["maxPending"]!).Select(_ => World.WriteAt(e, "Stamina", 8, 1)).ToList());
        var over = kiln.As(mod, () => World.WriteAt(e, "Stamina", 8, 2));
        Assert.True(over.IsCompletedSuccessfully);
        Assert.Equal(WorldWriteStatus.Dropped, over.Result.Status);
        Assert.Contains("4096 tracked writes are waiting", over.Result.Reason);
        kiln.Step();
        Assert.Equal(WorldAbi.DeliverPerPre, refused.Count(t => t.IsCompleted));   // one step delivers a bounded share
        kiln.Step(4);
        Assert.All(refused, t => Assert.Equal(WorldWriteStatus.NotDeclared, t.Result.Status));
    }

    // ---- an entity that goes away while its write waits ----

    [Fact]
    public void A_write_that_waits_while_its_player_leaves_never_lands_on_the_next_player_in_the_slot()
    {
        var lt = Parity["lifetime"]!;
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var ada = kiln.Players.Join("Ada", position: new WorldPosition(1, 2, 3));
        kiln.Step();
        kiln.Entities.Set(ada.Entity, new Ecs.ScaleComponent { uniformScale = 1f });
        var filler = kiln.Entities.Create();
        kiln.Entities.Set(filler, new Ecs.ScaleComponent { uniformScale = 0f });
        var mod = kiln.Mod("tk-world");
        var mine = kiln.As(mod, () =>
        {
            for (var i = 1; i <= (int)lt["filler"]!; i++) _ = World.Write(filler, new Ecs.ScaleComponent { uniformScale = i });
            return World.Write(ada.Entity, new Ecs.ScaleComponent { uniformScale = 9f });
        });
        ada.Leave();
        kiln.Step();                       // the filler takes the step; Ada is gone at its end
        Assert.False(mine.IsCompleted);
        Assert.False(kiln.Entities.Exists(ada.Entity) && kiln.Entities.TryGet<Ecs.ScaleComponent>(ada.Entity, out _));
        var bea = kiln.Players.Join("Bea");
        Assert.Equal(ada.Entity, bea.Entity);   // the same slot: the same id
        Assert.False(kiln.Entities.TryGet<Ecs.ScaleComponent>(bea.Entity, out _));   // Ada's record did not become Bea's
        kiln.Entities.Set(bea.Entity, new Ecs.ScaleComponent { uniformScale = 3f });
        kiln.Step();
        Assert.Equal((string)lt["status"]!, mine.Result.Status.ToString());
        Assert.Contains((string)lt["reasonContains"]!, mine.Result.Reason);
        Assert.Equal(3f, kiln.Entities.Get<Ecs.ScaleComponent>(bea.Entity).uniformScale);
    }

    [Fact]
    public void A_player_s_components_leave_with_the_player()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var ada = kiln.Players.Join("Ada");
        kiln.Step();
        kiln.Entities.Set(ada.Entity, new Ecs.ScaleComponent { uniformScale = 4f });
        ada.Leave();
        kiln.Step();
        kiln.Run(() =>
        {
            Assert.False(World.Has(ada.Entity, "ScaleComponent"));
            Assert.DoesNotContain(ada.Entity, World.Find("ScaleComponent"));
        });
        var bea = kiln.Players.Join("Bea");
        kiln.Step();
        kiln.Run(() => Assert.False(World.Has(bea.Entity, "ScaleComponent")));
        Assert.DoesNotContain(ada.Entity, kiln.Entities.All);
    }

    // ---- where an entity stands is its CurrentTransform ----

    [Fact]
    public void Position_and_transform_are_CurrentTransform_s_bytes()
    {
        var t = Parity["transform"]!;
        var hex = (string)t["hex"]!;
        var bytes = Convert.FromHexString(hex);
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var e = kiln.Entities.Create();
        kiln.Entities.SetBytes(e, (string)t["component"]!, bytes);
        var p = t["position"]!.AsArray().Select(v => (double)v!).ToArray();
        var r = t["rotation"]!.AsArray().Select(v => (float)(double)v!).ToArray();
        var s = t["scale"]!.AsArray().Select(v => (float)(double)v!).ToArray();
        kiln.Run(() =>
        {
            Assert.Equal(new WorldPosition(p[0], p[1], p[2]), World.Position(e));
            var x = World.Transform(e)!.Value;
            Assert.Equal((r[0], r[1], r[2], r[3]), (x.Rotation.X, x.Rotation.Y, x.Rotation.Z, x.Rotation.W));
            Assert.Equal((s[0], s[1], s[2]), (x.Scale.X, x.Scale.Y, x.Scale.Z));
            // a store into the bytes moves it, as in the game
            Assert.True(World.WriteRaw(e, "CurrentTransform", 0, 7L << 32));
            Assert.Equal(7.0, World.Position(e)!.Value.X);
        });
        // too small to be a transform: no position, as the native service answers
        var small = kiln.Entities.Create();
        kiln.Entities.SetBytes(small, "CurrentTransform", bytes.AsSpan(0, (int)t["minSize"]! - 1));
        kiln.Run(() => Assert.Null(World.Position(small)));
        // the test's hand writes the same bytes
        var placed = kiln.Entities.Create(new WorldPosition(10, 20, 30));
        var made = kiln.Entities.GetBytes(placed, "CurrentTransform")!;
        Assert.Equal(10.0, BitConverter.ToInt64(made) / 4294967296.0);
        Assert.Equal(1f, BitConverter.ToSingle(made, 0x24));   // identity rotation
        kiln.Entities.SetPosition(placed, new WorldPosition(-1, 0, 0.5));
        kiln.Run(() => Assert.Equal(new WorldPosition(-1, 0, 0.5), World.Position(placed)));
    }

    // ---- WriteInfo from other threads ----

    [Fact]
    public void WriteInfo_from_other_threads_while_the_tick_thread_changes_the_world()
    {
        using var kiln = new KilnHarness();
        Load(kiln);
        kiln.Step();
        var mod = kiln.Mod("tk-world");
        using var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var answers = 0;
        var probes = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                try
                {
                    var info = World.WriteInfo("Fuel");
                    if (info.GameWriterCount != info.GameWriters.Count) throw new InvalidOperationException("a torn answer");
                    if (World.WriteInfo("Stamina").State != WorldWriteState.NotDeclared) throw new InvalidOperationException("Stamina is nobody's");
                    Interlocked.Increment(ref answers);
                }
                catch (Exception e)
                {
                    lock (errors) errors.Add(e);
                }
        })).ToArray();
        for (var i = 0; i < 60; i++)
        {
            var writer = kiln.Entities.GameWrites<Ecs.Fuel>($"fuel_{i}");
            var e = kiln.Entities.Create(new WorldPosition(i, 0, 0));
            kiln.Entities.Set(e, new Ecs.ScaleComponent { uniformScale = i });
            kiln.Entities.Track("Stamina", save: i % 2 == 0, replication: false);
            kiln.Step();
            writer.Dispose();
            kiln.Entities.Destroy(e);
        }
        stop.Cancel();
        Task.WaitAll(probes);
        Assert.Empty(errors);
        Assert.True(answers > 0);
        Assert.Equal(WorldWriteState.Ready, kiln.As(mod, () => World.WriteInfo<Ecs.Fuel>().State));
    }
}
