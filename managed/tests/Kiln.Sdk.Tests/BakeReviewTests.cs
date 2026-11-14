// SPDX-License-Identifier: MIT
// What the review of the bake (Codex on 53670b80) asked for: the process ends (a forgotten foreground thread, an OnLoad that
// never returns), a failed bake leaves nothing of an earlier one, other ids' and odd baked files, the same file whatever
// the order or the machine's culture, relative icons, baked dialogues first, the "rebuild" note, a run-time Define against
// a fake kiln.skills, and the MSBuild side (turned off, deploy, outputs, settings, design-time) on a stub project.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Kiln.Sdk.Bake;
using Kiln.TestKit;
using Xunit;

namespace Kiln.SdkTests;

[Collection("bake")]
public sealed unsafe class BakeReviewTests
{
    private static string FixturesDll => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "bake-fixtures.txt")).First(l => l.Length > 0);

    /// <summary>A mod folder of one fixture type.</summary>
    private static string FixtureMod(string type, string id)
    {
        var dir = Directory.CreateTempSubdirectory("kiln-fixture-").FullName;
        File.Copy(FixturesDll, Path.Combine(dir, "BakeFixtures.dll"));
        File.WriteAllText(Path.Combine(dir, "kiln.mod.json"),
            $$"""{"id":"{{id}}","name":"{{type}}","version":"1.0.0","apiVersion":"0.2","entryAssembly":"BakeFixtures.dll","entryType":"BakeFixtures.{{type}}","side":"both","clients":"mod"}""");
        return dir;
    }

    private static void Put(string dir, string relative, JsonObject doc)
    {
        var f = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, doc.ToJsonString());
    }

    private static JsonObject Baked(JsonObject body)
    {
        body["format"] = 1;
        body["comment"] = Baker.Comment;
        return body;
    }

    /// <summary>The bake as the build runs it: its own process (dotnet exec Kiln.Sdk.Bake.dll), with a deadline of the test's own.</summary>
    private static (int Code, string Output, TimeSpan Took) RunProcess(string folder, int timeout, int wait = 90, string? culture = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // its own runtimeconfig, as the build runs it
        foreach (var a in new[] { "exec", Path.Combine(AppContext.BaseDirectory, "Kiln.Sdk.Bake.dll"), folder, "--timeout", timeout.ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        if (culture is not null)
        {
            psi.Environment["LANG"] = culture + ".UTF-8";
            psi.Environment["LC_ALL"] = culture + ".UTF-8";
            psi.Environment.Remove("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        }
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(wait)))
        {
            p.Kill(entireProcessTree: true);
            return (-999, "still running after " + wait + " s", sw.Elapsed);
        }
        return (p.ExitCode, output.Result + errors.Result, sw.Elapsed);
    }

    [Fact]
    public void AForegroundThreadAModLeavesDoesNotKeepTheBakeAlive()
    {
        var dir = FixtureMod("ForegroundThread", "thread-test");
        try
        {
            var (code, output, took) = RunProcess(dir, 60);
            Assert.True(code == 0, $"exit {code} after {took}: {output}");
            Assert.Contains("baked quests/generated/thread-test.json", output);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AnOnLoadThatNeverReturnsEndsAtTheDeadline()
    {
        var dir = FixtureMod("NeverReturns", "stuck-test");
        try
        {
            Put(dir, Baker.QuestsFile("stuck-test"), Baked(new JsonObject { ["quests"] = new JsonObject() }));
            var (code, output, took) = RunProcess(dir, 5, wait: 60);
            Assert.True(code == 4, $"exit {code} after {took}: {output}");
            Assert.Contains("error KILN032: the bake did not end within 5 s", output);
            Assert.False(File.Exists(Path.Combine(dir, Baker.QuestsFile("stuck-test"))));  // nothing of an earlier bake is left
            Assert.Contains("removed what an earlier bake wrote: quests/generated/stuck-test.json", output);
            Assert.True(took < TimeSpan.FromSeconds(45), $"took {took}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("Throws", "this mod's OnLoad throws")]
    // the bake's harness has the kit's kiln.assets (content blobs only): the icon call fails on the row that is off
    [InlineData("NeedsTheGame", "assets.ui_icons is not available")]
    public void AModThatDoesNotLoadLeavesNothingOfAnEarlierBake(string type, string why)
    {
        var dir = FixtureMod(type, "gone-test");
        try
        {
            // an earlier bake's files, the author's own file, and a file of the bake for another id (a renamed mod)
            Put(dir, Baker.QuestsFile("gone-test"), Baked(new JsonObject { ["quests"] = new JsonObject { ["old"] = new JsonObject() } }));
            Put(dir, Baker.VfxFile("gone-test"), Baked(new JsonObject { ["vfx"] = new JsonObject { ["old"] = new JsonObject() } }));
            Put(dir, Baker.SkillsFile("gone-test"), new JsonObject { ["comment"] = "the author's", ["gameTree"] = new JsonObject() });
            Put(dir, Baker.QuestsFile("old-name"), Baked(new JsonObject { ["quests"] = new JsonObject() }));
            var result = Baker.Run(dir);
            Assert.Empty(result.Warnings);
            Assert.Contains(result.Notes, n => n.StartsWith("not baked", StringComparison.Ordinal) && n.Contains(why, StringComparison.Ordinal));
            Assert.Empty(result.Written);
            Assert.Equal(["blocks/generated/gone-test.json", "quests/generated/gone-test.json", "quests/generated/old-name.json"], result.Removed.Order());
            Assert.True(File.Exists(Path.Combine(dir, Baker.SkillsFile("gone-test"))));  // the author's
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheSameDefinitionsGiveTheSameFileInAnyOrderAndCulture()
    {
        var ab = FixtureMod("QuestsAB", "order-test");
        var ba = FixtureMod("QuestsBA", "order-test");
        try
        {
            // the other order in the same process; then the bake's own process on a German build machine
            Baker.Run(ab);
            Baker.Run(ba);
            var a = File.ReadAllText(Path.Combine(ab, Baker.QuestsFile("order-test")));
            Assert.Equal(a, File.ReadAllText(Path.Combine(ba, Baker.QuestsFile("order-test"))));
            Assert.True(a.IndexOf("\"alpha\"", StringComparison.Ordinal) < a.IndexOf("\"beta\"", StringComparison.Ordinal));
            File.Delete(Path.Combine(ba, Baker.QuestsFile("order-test")));
            var (code, output, _) = RunProcess(ba, 60, culture: "de_DE");
            Assert.True(code == 0, output);
            var german = File.ReadAllText(Path.Combine(ba, Baker.QuestsFile("order-test")));
            Assert.Contains("\"Alpha 1.5 (de)\"", german);  // the mod's code formats as in the game (invariant), not as the build machine
            Assert.Equal(a, german);
        }
        finally
        {
            Directory.Delete(ab, true);
            Directory.Delete(ba, true);
        }
    }

    [Fact]
    public void OddBakedFilesAreNotTakenForTheBakes()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-mod-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "kiln.mod.json"), """{"id":"odd","name":"Odd","version":"1.0.0","apiVersion":"0.2"}""");
            // malformed, and one of a newer SDK (another comment): the author's for this bake, nothing for the run time
            Directory.CreateDirectory(Path.Combine(dir, "skills"));
            File.WriteAllText(Path.Combine(dir, Baker.SkillsFile("odd")), "{ \"comment\": \"" + Baker.Comment + "\", \"gameTree\": ");
            Put(dir, Baker.QuestsFile("odd"), new JsonObject { ["comment"] = "baked by the Kiln SDK 1.4 at build: another format", ["quests"] = new JsonObject() });
            var result = Baker.Run(dir);
            Assert.Empty(result.Removed);
            Assert.True(File.Exists(Path.Combine(dir, Baker.SkillsFile("odd"))) && File.Exists(Path.Combine(dir, Baker.QuestsFile("odd"))));
            Assert.Empty(GameTree.BakedFile(dir, "odd"));
            Assert.Null(NativeExport.BakedFile(dir, "odd"));
            Assert.Empty(VfxNative.BakedNames(dir, "odd"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BakedDialoguesQuestsAndNpcsStayAheadOfKilnsDataFile()
    {
        var now = (JsonObject)JsonNode.Parse("""{"format":1,"quests":{"a":{"x":1},"new":{"x":2}},"dialogues":{"talk":{"x":3},"other":{"x":4}},"npcs":{"n":{}}}""")!;
        var baked = (JsonObject)JsonNode.Parse("""{"comment":"c","quests":{"a":{"x":0}},"dialogues":{"talk":{"x":0}},"npcs":{"n":{}}}""")!;
        var data = NativeExport.ForDataFile(now, baked)!;
        Assert.Equal(["new"], ((JsonObject)data["quests"]!).Select(kv => kv.Key));
        Assert.Equal(["other"], ((JsonObject)data["dialogues"]!).Select(kv => kv.Key));  // the start-up build lets a later dialogue win
        Assert.Null(data["npcs"]);
        Assert.Same(now, NativeExport.ForDataFile(now, null));  // no baked file: all of it, as before
        Assert.Null(NativeExport.ForDataFile((JsonObject)JsonNode.Parse("""{"quests":{"a":{}}}""")!, baked));  // nothing left to write
    }

    [Fact]
    public void APendingBlockTheModBakedSaysRebuild()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-mod-").FullName;
        try
        {
            Put(dir, Baker.VfxFile("fx"), Baked(new JsonObject { ["vfx"] = new JsonObject { ["spark"] = new JsonObject() }, ["materials"] = new JsonObject { ["steel"] = new JsonObject() } }));
            Assert.Equal(["materials/steel", "vfx/spark"], VfxNative.BakedNames(dir, "fx").Order());
            var (log, toast) = VfxNative.PendingNote("vfx", "spark", "fx", true);
            Assert.Contains("rebuild the mod", log);
            Assert.DoesNotContain("restart the game", toast);
            Assert.Contains("rebuild", toast);
            Assert.Contains("restart the game", VfxNative.PendingNote("material", "steel", "fx", false).Toast);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- a fake kiln.skills: what the run time sends it ----

    private static readonly List<string> Defined = new();
    private static string _definitions = "[]";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeDefine(ulong owner, SkillsNative.NativeText text, byte* buf, nuint cap, nuint* len)
    {
        var json = Encoding.UTF8.GetString(text.Ptr, (int)text.Len);
        lock (Defined) Defined.Add(json);
        var nodes = GameTree.NodesOf(json).Nodes;
        var report = new JsonObject
        {
            ["ok"] = true,
            ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)new JsonObject { ["guid"] = (string?)n["guid"], ["name"] = (string?)n["name"], ["state"] = "pending" }).ToArray()),
        };
        return Fill(report.ToJsonString(), buf, cap, len);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeDefinitions(byte* buf, nuint cap, nuint* len) => Fill(_definitions, buf, cap, len);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint FakeUndefine(ulong owner) => 0;

    private static int Fill(string text, byte* buf, nuint cap, nuint* len)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        *len = (nuint)bytes.Length;
        if ((nuint)bytes.Length >= cap) return SkillsNative.BufferTooSmall;
        Marshal.Copy(bytes, 0, (IntPtr)buf, bytes.Length);
        buf[bytes.Length] = 0;
        return SkillsNative.Ok;
    }

    [Fact]
    public void AtRunTimeANodeAsBakedIsNotSentAgainAndAChangedOneIs()
    {
        var table = (SkillsNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(SkillsNative.Table));
        table->Size = (uint)sizeof(SkillsNative.Table);
        table->TableVersion = 1;
        table->Define = &FakeDefine;
        table->Definitions = &FakeDefinitions;
        table->UndefineOwner = &FakeUndefine;
        var folder = BakedModTests.CopyOf(Path.GetDirectoryName(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "bake-test.txt")).First(l => l.Length > 0))!);
        try
        {
            using var harness = new KilnHarness(new KilnHarnessOptions { ThrowOnModFault = true });
            var mod = harness.LoadFolder(folder);
            harness.Step(2);
            Assert.True(mod.IsLoaded, mod.Error);
            SkillsNative.UseForTests(table);
            const string guid = "7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a";
            // kiln.skills read the baked file at boot: the node is in the tree from there
            _definitions = new JsonArray(new JsonObject
            {
                ["guid"] = guid, ["id"] = "0x00000001", ["name"] = "baketest_cinder", ["owner"] = "bake-test",
                ["source"] = Path.Combine(mod.Folder, "skills", "bake-test.generated.json"), ["state"] = "in-tree",
            }).ToJsonString();
            var baked = GameTree.BakedFile(mod.Folder, "bake-test")[guid];
            lock (Defined) Defined.Clear();
            var same = harness.Run(() => GameTree.DefineJson(new JsonObject { ["mod"] = "bake-test", ["nodes"] = new JsonArray(baked.DeepClone()) }.ToJsonString()));
            Assert.Equal("in-tree", Assert.Single(same).State);
            lock (Defined) Assert.Empty(Defined);
            var changed = (JsonObject)baked.DeepClone();
            changed["cost"] = 2;
            var other = harness.Run(() => GameTree.DefineJson(new JsonObject { ["mod"] = "bake-test", ["nodes"] = new JsonArray(changed) }.ToJsonString()));
            Assert.Equal("pending", Assert.Single(other).State);
            lock (Defined) Assert.Contains("\"cost\":2", Assert.Single(Defined));
            Assert.True(harness.HasLog("is not what was baked into the mod at build"));
        }
        finally
        {
            SkillsNative.UseForTests(null);
            NativeMemory.Free(table);
            Directory.Delete(folder, true);
        }
    }
}

/// <summary>Kiln.Bake.targets on a stub project (no compiler): its targets one by one, through dotnet msbuild.</summary>
[Collection("bake")]
public sealed class BakeTargetsTests
{
    private static string Targets => Path.Combine(AppContext.BaseDirectory, "Kiln.Bake.targets");

    private sealed class Stub : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory("kiln-targets-").FullName;
        public string Out => Path.Combine(Dir, "out") + "/";
        public string Obj => Path.Combine(Dir, "obj") + "/";

        public Stub(string items = "")
        {
            Directory.CreateDirectory(Out);
            Directory.CreateDirectory(Obj);
            File.WriteAllText(Path.Combine(Dir, "stub.proj"), $"""
                <Project>
                  <PropertyGroup>
                    <_KilnServiceList>;Quests;</_KilnServiceList>
                    <KilnRepoMode>true</KilnRepoMode>
                    <KilnRepoRoot>{Dir}/</KilnRepoRoot>
                    <OutDir>{Out}</OutDir>
                    <IntermediateOutputPath>{Obj}</IntermediateOutputPath>
                    <KilnResolvedModId>stub</KilnResolvedModId>
                    <KilnSdkVersion>0.0.0-test</KilnSdkVersion>
                    <_KilnTasksDir>{AppContext.BaseDirectory}</_KilnTasksDir>
                  </PropertyGroup>
                  {items}
                  <Import Project="{Targets}" />
                  <Target Name="KilnDeploy" />
                </Project>
                """);
        }

        public (int Code, string Output) MsBuild(params string[] args)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("msbuild");
            psi.ArgumentList.Add(Path.Combine(Dir, "stub.proj"));
            psi.ArgumentList.Add("-nologo");
            psi.ArgumentList.Add("-v:m");
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit(120_000);
            return (p.ExitCode, o.Result + e.Result);
        }

        public void Dispose() => Directory.Delete(Dir, true);
    }

    [Fact]
    public void TurnedOffItRemovesWhatTheLastBakeWroteAndNothingElse()
    {
        using var s = new Stub();
        Directory.CreateDirectory(Path.Combine(s.Out, "quests", "generated"));
        File.WriteAllText(Path.Combine(s.Out, "quests", "generated", "stub.json"),
            new JsonObject { ["format"] = 1, ["comment"] = Baker.Comment, ["quests"] = new JsonObject() }.ToJsonString());
        File.WriteAllText(Path.Combine(s.Out, "quests", "generated", "authors.json"), "{}");
        File.WriteAllLines(Path.Combine(s.Obj, "kiln.bake.files"), ["quests/generated/stub.json"]);
        var (code, output) = s.MsBuild("-t:_KilnBakeOff", "-p:KilnBake=false");
        Assert.True(code == 0, output);
        Assert.False(File.Exists(Path.Combine(s.Out, "quests", "generated", "stub.json")));
        Assert.True(File.Exists(Path.Combine(s.Out, "quests", "generated", "authors.json")));
        Assert.False(File.Exists(Path.Combine(s.Obj, "kiln.bake.files")));
    }

    [Fact]
    public void DeployRemovesTheBakedFilesTheOutputNoLongerHas()
    {
        using var s = new Stub();
        var mods = Path.Combine(s.Dir, "server-mods");
        foreach (var f in new[] { "quests/generated/stub.json", "skills/stub.generated.json", "blocks/generated/stub.json", "quests/generated/authors.json" })
        {
            var p = Path.Combine(mods, "stub", f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "{}");
        }
        var (code, output) = s.MsBuild("-t:KilnDeploy", $"-p:KilnModsDir={mods}");
        Assert.True(code == 0, output);
        Assert.Equal(["authors.json"], Directory.GetFiles(Path.Combine(mods, "stub", "quests", "generated")).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(mods, "stub", "skills", "stub.generated.json")));
        Assert.False(File.Exists(Path.Combine(mods, "stub", "blocks", "generated", "stub.json")));
    }

    [Fact]
    public void AMissingOutputOrOtherSettingsBakeAgain()
    {
        using var s = new Stub();
        var stamp = Path.Combine(s.Obj, "kiln.bake.stamp");
        File.WriteAllText(stamp, "");
        File.WriteAllLines(Path.Combine(s.Obj, "kiln.bake.files"), ["quests/generated/stub.json"]);
        var (code, output) = s.MsBuild("-t:_KilnBakeOutputsGone");
        Assert.True(code == 0, output);
        Assert.False(File.Exists(stamp));  // the listed output is not in the output folder: the stamp goes, the bake runs

        var config = Path.Combine(s.Obj, "kiln.bake.config");
        Assert.Equal(0, s.MsBuild("-t:_KilnBakeConfig", "-p:KilnBakeSteps=10").Code);
        var first = File.ReadAllText(config);
        var at = File.GetLastWriteTimeUtc(config);
        Assert.Equal(0, s.MsBuild("-t:_KilnBakeConfig", "-p:KilnBakeSteps=10").Code);
        Assert.Equal(at, File.GetLastWriteTimeUtc(config));  // the same settings: untouched, so not a reason to bake
        Assert.Equal(0, s.MsBuild("-t:_KilnBakeConfig", "-p:KilnBakeSteps=30").Code);
        Assert.NotEqual(first, File.ReadAllText(config));  // a KilnBake input: changed settings bake again
    }

    [Fact]
    public void EveryFileCopiedToTheOutputIsAnInputOfTheBake()
    {
        // a JSON file OnLoad reads, copied with PreserveNewest or Always, as None or Content: changed, it bakes again
        using var s = new Stub("""
            <ItemGroup>
              <None Include="kept.json" CopyToOutputDirectory="PreserveNewest" />
              <None Include="always.json" CopyToOutputDirectory="Always" />
              <Content Include="content.json" CopyToOutputDirectory="PreserveNewest" />
              <Content Include="content-always.json" CopyToOutputDirectory="Always" />
              <None Include="not-copied.json" />
            </ItemGroup>
            """);
        var (code, output) = s.MsBuild("-t:_KilnBakeInputs", "-getItem:_KilnBakeInput");
        Assert.True(code == 0, output);
        foreach (var f in new[] { "kept.json", "always.json", "content.json", "content-always.json" }) Assert.Contains(f, output);
        Assert.DoesNotContain("not-copied.json", output);
    }

    [Fact]
    public void ADesignTimeBuildNeverBakes()
    {
        using var s = new Stub();
        var (code, output) = s.MsBuild("-getProperty:_KilnBakeNow", "-getItem:ProjectReference");
        Assert.True(code == 0, output);
        Assert.Contains("\"_KilnBakeNow\": \"true\"", output);
        Assert.Contains("Kiln.Sdk.Bake.csproj", output);
        (code, output) = s.MsBuild("-getProperty:_KilnBakeNow", "-getItem:ProjectReference", "-p:DesignTimeBuild=true");
        Assert.True(code == 0, output);
        Assert.Contains("\"_KilnBakeNow\": \"\"", output);
        Assert.DoesNotContain("Kiln.Sdk.Bake.csproj", output);
    }
}
