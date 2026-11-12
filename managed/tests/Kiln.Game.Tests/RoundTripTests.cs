// SPDX-License-Identifier: MIT
// The round trip over the built assemblies: the Kiln.Game under test against every schema-2 dump under sigdb/types,
// the variant built from the made-up fixture dump against that dump, and a doctored copy of the fixture that must fail.
// Also the legacy part: the v1 emitter on the old dump and on the new server dump against the dev.24 golden.
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.Game.Tests;

public class RoundTripTests
{
    const string NoDump = "skipped: no schema-2 dump under sigdb/types yet";

    static string LanternsDump => GenRepo.Fixture("gen", "lanterns.json");

    static Assembly LoadIsolated(string path)
    {
        var ctx = new AssemblyLoadContext("roundtrip-" + Path.GetFileName(Path.GetDirectoryName(path)), isCollectible: true);
        return ctx.LoadFromAssemblyPath(path);
    }

    static Assembly Lanterns => LoadIsolated(GenRepo.Built("variants", "lanterns", "Kiln.Game.dll"));

    public static TheoryData<string> CommittedDumps()
    {
        var d = new TheoryData<string>();
        foreach (var f in GameDump.Committed()) d.Add(Path.GetRelativePath(GenRepo.Root(), f).Replace('\\', '/'));
        if (d.Count == 0) d.Add(NoDump);
        return d;
    }

    [Theory]
    [MemberData(nameof(CommittedDumps))]
    public void Every_type_of_a_committed_dump_round_trips_through_the_built_Kiln_Game(string dump)
    {
        if (dump == NoDump) Assert.Skip(NoDump);
        var problems = RoundTrip.Check(GenRepo.GameAssembly, GameDump.Read(GenRepo.Path_(dump)));
        Assert.True(problems.Count == 0, $"{problems.Count} problem(s):\n" + string.Join("\n", problems.Take(40)));
    }

    [Fact]
    public void The_server_and_client_dumps_have_the_same_types()
    {
        var dumps = GameDump.Committed().Select(f => (File: f, Json: JsonNode.Parse(File.ReadAllText(f))!)).ToList();
        var server = dumps.Where(x => (string?)x.Json["side"] == "server").ToList();
        var client = dumps.Where(x => (string?)x.Json["side"] == "client").ToList();
        if (server.Count == 0 || client.Count == 0) Assert.Skip("skipped: no schema-2 server and client dump pair under sigdb/types yet");
        // One Kiln.Game serves both sides: the newest pair must agree in everything but side and exe.
        var (s, c) = (server[^1].Json, client[^1].Json);
        foreach (var key in new[] { "components", "events", "types" })
            Assert.True(JsonNode.DeepEquals(s[key], c[key]),
                $"server {Path.GetFileName(server[^1].File)} and client {Path.GetFileName(client[^1].File)} differ in \"{key}\": " +
                "one Kiln.Game no longer serves both sides; build one per side");
    }

    [Fact]
    public void The_built_Kiln_Game_comes_from_a_committed_dump()
    {
        var dumps = GameDump.Committed();
        if (dumps.Count == 0) Assert.Skip(NoDump);
        var header = GameLayoutStampsTests.Header(Kiln.GameLayoutStamps.Table);
        Assert.Contains(dumps, f => GameDump.Read(f) is var d && d.Exe == header["exe_sha256"] && d.Side == header["side"]);
    }

    [Fact]
    public void The_fixture_variant_round_trips_through_its_dump()
    {
        var problems = RoundTrip.Check(Lanterns, GameDump.Read(LanternsDump));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_fixture_variant_compiles_the_program_descriptors_of_the_made_up_catalog()
    {
        var asm = Lanterns;
        var glow = asm.GetType("Kiln.Ecs+Programs+LanternGlow")!;
        object? F(string n) => glow.GetField(n, BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        Assert.Equal("lantern_glow", F("Name"));
        Assert.Equal(["Kiln.Ecs+Lantern"], ((Type[])F("Tpl")!).Select(t => t.FullName));
        Assert.Equal(["Kiln.Ecs+Gauges", "Kiln.Ecs+Comet"], ((Type[])F("Read")!).Select(t => t.FullName));
        Assert.Equal(["Kiln.Ecs+Warmth"], ((Type[])F("Write")!).Select(t => t.FullName));
        Assert.Equal(["light_cache"], (string[])F("SysRead")!);
        Assert.Equal(["Kiln.Ecs+LanternLit"], ((Type[])F("EventsOut")!).Select(t => t.FullName));
        Assert.NotNull(asm.GetType("Kiln.Ecs+Programs+Program7"));
        Assert.Null(GenRepo.GameAssembly.GetType("Kiln.Ecs+Programs"));  // the shipped build has no catalog
    }

    [Fact]
    public void A_doctored_dump_fails_the_round_trip()
    {
        // One offset moved: Lantern.mode from 8 to 9.
        var json = JsonNode.Parse(File.ReadAllText(LanternsDump))!;
        var lantern = json["types"]!.AsArray().Single(t => (string?)t!["qualifiedName"] == "keen::ecs::Lantern")!;
        lantern["fields"]!.AsArray().Single(f => (string?)f!["name"] == "mode")!["offset"] = 9;
        var problems = RoundTrip.Check(Lanterns, GameDump.Parse(json.ToJsonString()));
        Assert.Contains("keen::ecs::Lantern.mode: FieldOffset 8, the dump says 9", problems);

        // A type the assembly lacks, a resized struct and a renumbered enum value.
        json = JsonNode.Parse(File.ReadAllText(LanternsDump))!;
        var types = json["types"]!.AsArray();
        types.Add(JsonNode.Parse("""{"name":"Wick","qualifiedName":"keen::ecs::Wick","hash":"0x00000000","kind":18,"size":4,"align":4,"fields":[]}"""));
        types.Single(t => (string?)t!["qualifiedName"] == "keen::ecs::Spark")!["size"] = 12;
        types.Single(t => (string?)t!["qualifiedName"] == "keen::LanternMode")!["values"]![1]!["value"] = 7;
        problems = RoundTrip.Check(Lanterns, GameDump.Parse(json.ToJsonString()));
        Assert.Contains("keen::ecs::Wick: no generated type Ecs.Wick", problems);
        Assert.Contains(problems, p => p.StartsWith("keen::ecs::Spark: Unsafe.SizeOf<Spark>() is 8, the dump says 12", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("keen::LanternMode: values ", StringComparison.Ordinal));
    }

    // --- the legacy part ------------------------------------------------------------------------------------------

    static readonly Regex StructRx = new(@"public unsafe struct (\w+)\n\{\n(.*?)\n\}", RegexOptions.Singleline);
    static readonly Regex FieldRx = new(@"\[FieldOffset\((\d+)\)\] public (?:fixed byte (\w+)\[(\d+)\]|(\w+) (\w+));");

    /// <summary>The nine structs of a legacy text as the golden table writes them.</summary>
    static List<(string Name, int Size, string Hash, List<(string, int, string)> Fields)> ParseLegacy(string text)
    {
        var list = new List<(string, int, string, List<(string, int, string)>)>();
        foreach (Match m in StructRx.Matches(text))
        {
            int size = int.Parse(Regex.Match(text[..m.Index], @"Size = (\d+)\)\]\s*$").Groups[1].Value);
            string hash = Regex.Match(m.Groups[2].Value, @"Hash = (0x[0-9A-F]+);").Groups[1].Value;
            var fields = FieldRx.Matches(m.Groups[2].Value).Select(f => f.Groups[2].Success
                ? (f.Groups[2].Value, int.Parse(f.Groups[1].Value), $"fixed byte[{f.Groups[3].Value}]")
                : (f.Groups[5].Value, int.Parse(f.Groups[1].Value), f.Groups[4].Value)).ToList();
            list.Add((m.Groups[1].Value, size, hash, fields));
        }
        return list;
    }

    static void AssertGolden(string text, string what)
    {
        var golden = GenRepo.ReadJson(GenRepo.Fixture("legacy", "golden.json")).GetProperty("structs").EnumerateArray()
            .Select(s => (s.GetProperty("name").GetString()!, s.GetProperty("size").GetInt32(), s.GetProperty("hash").GetString()!,
                s.GetProperty("fields").EnumerateArray().Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("offset").GetInt32(), f.GetProperty("type").GetString()!)).ToList()))
            .ToList();
        var got = ParseLegacy(text);
        Assert.Equal(golden.Count, got.Count);
        for (int i = 0; i < golden.Count; i++)
        {
            Assert.Equal((golden[i].Item1, golden[i].Item2, golden[i].Item3), (got[i].Name, got[i].Size, got[i].Hash));
            Assert.True(golden[i].Item4.SequenceEqual(got[i].Fields), $"{what}: {golden[i].Item1}'s fields differ from the golden");
        }
    }

    static string Work(string name)
    {
        var d = Path.Combine(AppContext.BaseDirectory, "typegen-work", name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void The_legacy_emitter_on_the_old_dump_gives_the_golden_offsets()
    {
        var old = GenRepo.Path_("sigdb", "types", "server", "c5c9fe50c6398d2adedd98923c2d04bf9d9b4af64c1447ac167cdf8f874ff84f.json");
        var outFile = Path.Combine(Work("legacy-old"), "Generated.cs");
        var r = GenRepo.Python(GenRepo.Path_("tools", "typegen", "typegen.py"), old, outFile);
        Assert.True(r.Code == 0, r.Err);
        AssertGolden(File.ReadAllText(outFile), "old dump");
    }

    [Fact]
    public void The_legacy_part_from_the_new_server_dump_gives_the_golden_offsets_in_the_dev24_text()
    {
        var server = GameDump.Committed().Where(f => GameDump.Read(f).Side == "server").ToList();
        if (server.Count == 0) Assert.Skip("skipped: no schema-2 server dump under sigdb/types yet");
        var dir = Work("legacy-new");
        var r = GenRepo.Python(GenRepo.Path_("tools", "typegen", "typegen.py"), "generate", "--dump", server[^1], "--out", dir, "--legacy-obsolete");
        Assert.True(r.Code == 0, r.Err);
        var text = File.ReadAllText(Path.Combine(dir, "Kiln.Game.Legacy.g.cs"));
        AssertGolden(text, "new server dump");
        // The same text as dev.24's Generated.cs, but for the marker, the Source line, one pragma and the [Obsolete]s.
        static string[] Lines(string t) => t.Split('\n')
            .Where(l => !l.StartsWith("// kiln-typegen:", StringComparison.Ordinal) && !l.StartsWith("// Source: ", StringComparison.Ordinal)
                        && !l.StartsWith("#pragma warning disable CS0618", StringComparison.Ordinal) && !l.StartsWith("[System.Obsolete(", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(Lines(File.ReadAllText(GenRepo.Path_("managed", "Kiln.Game", "Generated.cs"))), Lines(text));
        Assert.Equal(9, Regex.Matches(text, @"\[System\.Obsolete\(""Use Kiln\.Ecs\.\w+\.""\)\]").Count);
    }

    [Fact]
    public void The_legacy_types_of_the_built_Kiln_Game_are_obsolete_and_name_their_new_type()
    {
        if (GameDump.Committed().Count == 0) Assert.Skip(NoDump);
        foreach (var name in new[] { "NetworkHealth", "Health", "Mana", "Stamina", "CurrentTransform", "LastTransform", "Velocity", "TeleportCount", "NpcCommon" })
        {
            var t = GenRepo.GameAssembly.GetType("Kiln.GameTypes." + name)!;
            Assert.Equal($"Use Kiln.Ecs.{name}.", t.GetCustomAttribute<ObsoleteAttribute>()?.Message);
            Assert.NotNull(GenRepo.GameAssembly.GetType("Kiln.Ecs+" + name));
        }
        // The two classes point at the root class as a whole.
        Assert.Equal("Use Kiln.Ecs.", GenRepo.GameAssembly.GetType("Kiln.GameTypes.Components")!.GetCustomAttribute<ObsoleteAttribute>()?.Message);
        Assert.Equal("Use Kiln.Ecs.", GenRepo.GameAssembly.GetType("Kiln.GameTypes.ComponentTypes")!.GetCustomAttribute<ObsoleteAttribute>()?.Message);
    }
}
