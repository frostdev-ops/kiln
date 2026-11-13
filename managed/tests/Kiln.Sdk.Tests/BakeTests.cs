// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln.Sdk.Bake;
using Kiln.TestKit;
using Xunit;

namespace Kiln.SdkTests;

/// <summary>The recorders run with process-wide switches and the harness one at a time: these tests do not run in parallel.</summary>
[CollectionDefinition("bake", DisableParallelization = true)]
public sealed class BakeCollection;

/// <summary>mods/tests/bake-test, built with KilnBake: its baked files, and what its code defines when it runs.</summary>
[Collection("bake")]
public sealed class BakedModTests
{
    private static string ModFolder =>
        Path.GetDirectoryName(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "bake-test.txt")).First(l => l.Length > 0))!;

    private static JsonObject Read(string relative) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(ModFolder, relative)))!;

    [Fact]
    public void TheBuildBakedEachKindIntoTheModFolder()
    {
        var quests = Read(Baker.QuestsFile("bake-test"));
        Assert.Equal(Baker.Comment, (string?)quests["comment"]);
        Assert.Equal(["ash_count", "cinder_count"], ((JsonObject)quests["quests"]!).Select(kv => kv.Key));
        var skills = Read(Baker.SkillsFile("bake-test"));
        Assert.Equal(Baker.Comment, (string?)skills["comment"]);
        var node = Assert.Single((JsonArray)skills["gameTree"]!["nodes"]!)!;
        Assert.Equal("7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a", (string?)node["guid"]);
        Assert.Equal("baketest_cinder", (string?)node["name"]);
        var vfx = Read(Baker.VfxFile("bake-test"));
        Assert.Equal(Baker.Comment, (string?)vfx["comment"]);
        Assert.Equal(["baked_spark"], ((JsonObject)vfx["vfx"]!).Select(kv => kv.Key));
        Assert.Null(vfx["materials"]);
    }

    [Fact]
    public void TheSameCodeRunAgainBakesTheSameFiles()
    {
        var copy = CopyOf(ModFolder);
        try
        {
            var was = File.ReadAllText(Path.Combine(copy, Baker.QuestsFile("bake-test")));
            var result = Baker.Run(copy);
            Assert.Empty(result.Warnings);
            Assert.Equal(was, File.ReadAllText(Path.Combine(copy, Baker.QuestsFile("bake-test"))));
            Assert.Empty(result.Written);
            Assert.Equal([Baker.QuestsFile("bake-test"), Baker.SkillsFile("bake-test"), Baker.VfxFile("bake-test")],
                result.Unchanged.Select(f => f.Replace('/', Path.DirectorySeparatorChar)));
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    [Fact]
    public void AtRunTimeTheQuestsTheCodeDefinesAreTheBakedOnes()
    {
        // The mod as Kiln runs it (no bake): Kiln.Quests' document for the game's journal, what it writes to kiln/data at run time.
        using var harness = new KilnHarness(new KilnHarnessOptions { ThrowOnModFault = true });
        var mod = harness.LoadFolder(ModFolder);
        harness.Step(3);
        Assert.True(mod.IsLoaded, mod.Error);
        var now = NativeExport.Document("bake-test", out _);
        var baked = NativeExport.BakedFile(mod.Folder, "bake-test");
        Assert.NotNull(baked);
        Assert.Null(NativeExport.BakedDifference(baked!, now));
    }

    [Fact]
    public void AtRunTimeTheNodeTheCodeDefinesIsTheBakedOne()
    {
        // GameTree.Define sends {"nodes": [definition.ToJson()]}: the request the bake recorded, and the one it compares at run time.
        var definition = new GameNodeDefinition
        {
            Guid = new Guid("7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a"),
            Name = "baketest_cinder",
            Type = "Attribute",
            Class = "Mage",
            Like = "0xcfa5985b",
            From = ["0xcfa5985b"],
            Offset = (170, 40),
            Cost = 1,
            Texts = new Dictionary<string, (string, string, string?)> { ["en"] = ("Cinder", "Baked at build.", null) },
        };
        var request = Request(definition);
        var baked = GameTree.BakedFile(ModFolder, "bake-test");
        Assert.True(GameTree.SameAsBaked(request, baked["7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a"], ModFolder, Path.GetTempPath()));
        var changed = Request(new GameNodeDefinition { Guid = definition.Guid, Name = definition.Name, Type = "Attribute", Class = "Mage", Like = definition.Like, From = definition.From, Offset = (170, 40), Cost = 2, Texts = definition.Texts });
        Assert.False(GameTree.SameAsBaked(changed, baked["7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a"], ModFolder, Path.GetTempPath()));
    }

    internal static JsonObject Request(GameNodeDefinition d) =>
        GameTree.NodesOf(new JsonObject { ["nodes"] = new JsonArray(d.ToJson()), ["mod"] = "bake-test" }.ToJsonString()).Nodes.Single();

    internal static string CopyOf(string folder)
    {
        var copy = Path.Combine(Path.GetTempPath(), "kiln-bake-" + Guid.NewGuid().ToString("N"));
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(copy, Path.GetRelativePath(folder, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return copy;
    }
}

/// <summary>The recorders and the files, without a mod.</summary>
[Collection("bake")]
public sealed class BakeUnitTests
{
    [Fact]
    public void EveryRecorderMarksItsFilesTheSameWay()
    {
        Assert.Equal(Baker.Comment, NativeExport.BakedComment);
        Assert.Equal(Baker.Comment, GameTree.BakedComment);
        Assert.Equal(Baker.Comment, VfxNative.BakedComment);
    }

    [Fact]
    public void QuestDifferencesAreNamed()
    {
        var baked = (JsonObject)JsonNode.Parse("""{"comment":"x","quests":{"a":{"name":{"en":"A"}},"b":{"name":{"en":"B"}}},"npcs":{"n":{"template":"t"}}}""")!;
        Assert.Null(NativeExport.BakedDifference(baked, (JsonObject)JsonNode.Parse("""{"comment":"y","npcs":{"n":{"template":"t"}},"quests":{"b":{"name":{"en":"B"}},"a":{"name":{"en":"A"}}}}""")!));
        var diff = NativeExport.BakedDifference(baked, (JsonObject)JsonNode.Parse("""{"quests":{"a":{"name":{"en":"A2"}},"c":{}},"npcs":{"n":{"template":"t"}}}""")!);
        Assert.Equal("quest a differs, quest c is not in it, quest b is only in it", diff);
        Assert.Equal("NPC n is only in it, quest a is only in it, quest b is only in it", NativeExport.BakedDifference(baked, null));
    }

    [Fact]
    public void ANodeIsBakedWithItsIconByItsPathInTheModFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-mod-" + Guid.NewGuid().ToString("N"));
        var inside = (JsonObject)JsonNode.Parse($$"""{"guid":"{{Guid.NewGuid()}}","name":"n","iconFile":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "icons", "a.png"))}},"mod":"m"}""")!;
        var portable = GameTree.Portable(inside, dir, Path.GetTempPath())!;
        Assert.Equal("icons/a.png", (string?)portable["iconFile"]);
        Assert.Null(portable["mod"]);
        Assert.True(GameTree.SameAsBaked(inside, portable, dir, Path.GetTempPath()));
        var outside = (JsonObject)JsonNode.Parse($$"""{"guid":"{{Guid.NewGuid()}}","name":"n","iconFile":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(Path.GetTempPath(), "elsewhere.png"))}}}""")!;
        Assert.Null(GameTree.Portable(outside, dir, Path.GetTempPath()));
        // a relative path in a definition from code is the kiln folder's (kiln.skills resolves code definitions there)
        var kiln = Path.GetDirectoryName(dir)!;
        var relative = (JsonObject)JsonNode.Parse($$"""{"guid":"7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a","name":"n","iconFile":"{{Path.GetFileName(dir)}}/icons/b.png"}""")!;
        Assert.Equal("icons/b.png", (string?)GameTree.Portable(relative, dir, kiln)!["iconFile"]);
        var elsewhere = (JsonObject)JsonNode.Parse("""{"guid":"7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a","name":"n","iconFile":"icons/b.png"}""")!;
        Assert.Null(GameTree.Portable(elsewhere, dir, kiln));  // <kiln>/icons/b.png: not the mod's
    }

    [Fact]
    public void WhileBakingGameTreeRecordsInsteadOfAskingKilnSkills()
    {
        Assert.False(GameTree.Available);  // no kiln.skills in a test process
        GameTree.Baking = true;
        try
        {
            GameTree.ForgetBaked("rec-test");
            Assert.True(GameTree.Available);
            var g = Guid.NewGuid();
            var report = GameTree.DefineJson($$"""{"mod":"rec-test","nodes":[{"guid":"{{g}}","name":"rec","cost":2},{"name":"no-guid"}]}""");
            Assert.Equal(["defined", "error"], report.Select(r => r.State));
            var (nodes, problems) = GameTree.BakedOf("rec-test");
            Assert.Empty(problems);
            var node = Assert.Single(nodes);
            Assert.Equal(g.ToString(), (string?)node["guid"]);
            Assert.Equal(2, (int?)node["cost"]);
            // the same GUID again replaces it, as kiln.skills redefines
            GameTree.DefineJson($$"""{"mod":"rec-test","nodes":[{"guid":"{{g}}","name":"rec","cost":3}]}""");
            Assert.Equal(3, (int?)Assert.Single(GameTree.BakedOf("rec-test").Nodes)["cost"]);
        }
        finally
        {
            GameTree.Baking = false;
            GameTree.ForgetBaked("rec-test");
        }
        Assert.False(GameTree.Available);
        // a KilnUnavailableException naming kiln.skills since dev.27's contract adoption: still a NotSupportedException
        Assert.ThrowsAny<NotSupportedException>(() => GameTree.DefineJson("""{"nodes":[]}"""));
    }

    [Fact]
    public void WhileBakingTheVfxBuilderRecordsWhatItIsHanded()
    {
        Assert.Null(VfxNative.BuildStatusJson());
        Assert.Null(VfxNative.DefineJson("""{"format":1,"mod":"vfx-rec","dir":"x","vfx":{"a":{}}}""", 0));
        VfxNative.Baking = true;
        try
        {
            VfxNative.ForgetBaked("vfx-rec");
            Assert.Contains("\"refusedMods\":[]", VfxNative.BuildStatusJson());
            var report = (JsonObject)JsonNode.Parse(VfxNative.DefineJson("""
                {"format":1,"mod":"vfx-rec","dir":"x","vfx":{"spark":{"particles":[{"burst":2}]}}, // a data file's bytes
                 "materials":{"steel":{"roughness":0.5,}}}
                """, 0)!)!;
            Assert.Equal("pending", (string?)report["blocks"]![0]!["status"]);
            Assert.Equal("steel", (string?)report["materials"]![0]!["name"]);
            Assert.Equal("""{"ok":true,"released":0}""", VfxNative.DefineJson("""{"format":1,"mod":"vfx-rec","release":true}""", 0));
            var baked = VfxNative.BakedOf("vfx-rec");
            Assert.Equal(2, (int?)baked["vfx"]["spark"]["particles"]![0]!["burst"]);
            Assert.Equal(0.5, (double?)baked["materials"]["steel"]["roughness"]);
        }
        finally
        {
            VfxNative.Baking = false;
            VfxNative.ForgetBaked("vfx-rec");
        }
    }

    [Fact]
    public void TheVfxFileLeavesOutWhatTheModsDataFilesDefine()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-mod-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "blocks"));
            File.WriteAllText(Path.Combine(dir, "blocks", "vfx.json"), """
                { "vfx": { "from_data": {}, }, // a comment
                  "materials": { "data_steel": {} } }
                """);
            Assert.Equal(["vfx/from_data", "materials/data_steel"], Baker.DataBlockNames(dir));
            VfxNative.Baking = true;
            VfxNative.ForgetBaked("vfx-doc");
            VfxNative.DefineJson("""{"format":1,"mod":"vfx-doc","dir":"x","vfx":{"from_data":{"a":1}}}""", 0);
            VfxNative.DefineJson("""{"format":1,"mod":"vfx-doc","dir":"x","vfx":{"from_code":{"b":2}}}""", 0);
            VfxNative.DefineJson("""{"format":1,"mod":"vfx-doc","dir":"x","materials":{"data_steel":{}}}""", 0);
            var doc = Baker.VfxDocument("vfx-doc", dir)!;
            Assert.Equal(["from_code"], ((JsonObject)doc["vfx"]!).Select(kv => kv.Key));
            Assert.Null(doc["materials"]);
        }
        finally
        {
            VfxNative.Baking = false;
            VfxNative.ForgetBaked("vfx-doc");
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheModsOwnFilesAreLeftAloneAndStaleBakedFilesGo()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-mod-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "kiln.mod.json"), """{"id":"plain","name":"Plain","version":"1.0.0","apiVersion":"0.2"}""");
            // a data-only mod defines nothing in code: what an earlier bake wrote goes, the author's own file stays
            Directory.CreateDirectory(Path.Combine(dir, "quests", "generated"));
            Directory.CreateDirectory(Path.Combine(dir, "skills"));
            var own = Path.Combine(dir, Baker.QuestsFile("plain"));
            File.WriteAllText(own, """{"format":1,"comment":"written by Kiln from what the mod defines in code: docs/kiln/modding/quests.md, Code","quests":{}}""");
            var stale = Path.Combine(dir, Baker.SkillsFile("plain"));
            File.WriteAllText(stale, new JsonObject { ["format"] = 1, ["comment"] = Baker.Comment, ["gameTree"] = new JsonObject { ["nodes"] = new JsonArray() } }.ToJsonString());
            Assert.True(Baker.IsBaked(stale));
            Assert.False(Baker.IsBaked(own));
            var dry = Baker.Run(dir, new BakeOptions { DryRun = true });
            Assert.Equal(["skills/plain.generated.json"], dry.Removed);
            Assert.True(File.Exists(stale));
            var result = Baker.Run(dir);
            Assert.Equal(["skills/plain.generated.json"], result.Removed);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(own));
            Assert.Contains(result.Notes, n => n.Contains("data-only", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AModThatDoesNotLoadWithoutTheGameIsNotBakedAndSaysWhy()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-mod-").FullName;
        try
        {
            // an entry assembly that is not there: the loader refuses the mod, as it refuses one whose OnLoad needs the game
            File.WriteAllText(Path.Combine(dir, "kiln.mod.json"), """{"id":"broken","name":"Broken","version":"1.0.0","apiVersion":"0.2","entryAssembly":"Broken.dll"}""");
            var result = Baker.Run(dir);
            Assert.Empty(result.Written);
            Assert.Empty(result.Warnings);  // a limit of the bake, not a fault of the mod: a note
            Assert.Contains(result.Notes, n => n.StartsWith("not baked: the mod does not load without the game", StringComparison.Ordinal) && n.Contains("next start", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
