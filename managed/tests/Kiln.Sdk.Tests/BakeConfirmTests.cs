// SPDX-License-Identifier: MIT
// The confirm review of the bake (Codex on 888f47de): every managed read of a baked file is bounded (4 MiB, no link on the way
// from the mod's folder), and the build's own clean-up removes only what the SDK wrote (its marker), whatever its bookkeeping
// says: when the bake is turned off, and when a bake fails or is ended at its deadline.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Kiln.Sdk;
using Kiln.Sdk.Bake;
using Xunit;

namespace Kiln.SdkTests;

[Collection("bake")]
public sealed class BakeConfirmTests
{
    private static JsonObject Baked(JsonObject body)
    {
        body["format"] = 1;
        body["comment"] = Baker.Comment;
        return body;
    }

    private static void Put(string dir, string relative, string text)
    {
        var f = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    private static void PutAll(string dir, string id, string pad)
    {
        var nodes = new JsonArray(new JsonObject { ["guid"] = "7a4e1c2d-5b3f-4e8a-9c6d-0f1e2d3c4b5a", ["name"] = "n" });
        Put(dir, Baker.VfxFile(id), Baked(new JsonObject { ["vfx"] = new JsonObject { ["spark"] = new JsonObject() }, ["pad"] = pad }).ToJsonString());
        Put(dir, Baker.QuestsFile(id), Baked(new JsonObject { ["quests"] = new JsonObject { ["q"] = new JsonObject() }, ["pad"] = pad }).ToJsonString());
        Put(dir, Baker.SkillsFile(id), Baked(new JsonObject { ["gameTree"] = new JsonObject { ["nodes"] = nodes }, ["pad"] = pad }).ToJsonString());
    }

    [Fact]
    public void ABakedFileOverFourMiBIsReadByNoOne()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-mod-").FullName;
        try
        {
            PutAll(dir, "small", "");
            Assert.Equal(["vfx/spark"], VfxNative.BakedNames(dir, "small"));  // the same files, small: read
            Assert.NotNull(NativeExport.BakedFile(dir, "small"));
            Assert.Single(GameTree.BakedFile(dir, "small"));
            PutAll(dir, "huge", new string('x', 5 << 20));
            Assert.Empty(VfxNative.BakedNames(dir, "huge"));    // the game's stale-VFX note
            Assert.Null(NativeExport.BakedFile(dir, "huge"));   // the quests comparison
            Assert.Empty(GameTree.BakedFile(dir, "huge"));      // the game-tree comparison
            Assert.False(Baker.IsBaked(Path.Combine(dir, Baker.VfxFile("huge"))));  // the build's own look
            Assert.Null(BakedFiles.Read(dir, Baker.QuestsFile("huge")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ABakedFileBehindALinkIsReadByNoOne()
    {
        var root = Directory.CreateTempSubdirectory("kiln-link-").FullName;
        try
        {
            var outside = Path.Combine(root, "outside");
            var mod = Path.Combine(root, "mod");
            PutAll(outside, "fx", "");
            Directory.CreateDirectory(mod);
            // the mod's blocks, quests and skills folders are links to another folder
            Directory.CreateSymbolicLink(Path.Combine(mod, "blocks"), Path.Combine(outside, "blocks"));
            Directory.CreateSymbolicLink(Path.Combine(mod, "quests"), Path.Combine(outside, "quests"));
            Directory.CreateSymbolicLink(Path.Combine(mod, "skills"), Path.Combine(outside, "skills"));
            Assert.Empty(VfxNative.BakedNames(mod, "fx"));
            Assert.Null(NativeExport.BakedFile(mod, "fx"));
            Assert.Empty(GameTree.BakedFile(mod, "fx"));
            // a link as the file itself
            var mod2 = Path.Combine(root, "mod2");
            Directory.CreateDirectory(Path.Combine(mod2, "blocks", "generated"));
            File.CreateSymbolicLink(Path.Combine(mod2, Baker.VfxFile("fx")), Path.Combine(outside, Baker.VfxFile("fx")));
            Assert.Empty(VfxNative.BakedNames(mod2, "fx"));
            Assert.False(Baker.IsBaked(Path.Combine(mod2, Baker.VfxFile("fx"))));
            // a path that leaves the folder
            Assert.Null(BakedFiles.Read(mod, "../outside/" + Baker.QuestsFile("fx").Replace('\\', '/')));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TheCleanUpRemovesOnlyWhatTheSdkWrote()
    {
        var dir = Directory.CreateTempSubdirectory("kiln-out-").FullName;
        try
        {
            Put(dir, Baker.QuestsFile("m"), Baked(new JsonObject { ["quests"] = new JsonObject() }).ToJsonString());
            Put(dir, Baker.VfxFile("m"), Baked(new JsonObject { ["vfx"] = new JsonObject() }).ToJsonString());
            Put(dir, Baker.QuestsFile("old-name"), Baked(new JsonObject { ["quests"] = new JsonObject() }).ToJsonString());
            // the author's file at a baked path (it replaced an earlier baked one): it stays, whatever the bookkeeping lists
            Put(dir, Baker.SkillsFile("m"), """{"comment":"the author's","gameTree":{"nodes":[]}}""");
            Put(dir, Path.Combine("quests", "generated", "authors.json"), """{"quests":{}}""");
            var removed = BakedOutputs.Clean(dir, "m");
            Assert.Equal(["blocks/generated/m.json", "quests/generated/m.json", "quests/generated/old-name.json"], removed.Order());
            Assert.True(File.Exists(Path.Combine(dir, Baker.SkillsFile("m"))));
            Assert.True(File.Exists(Path.Combine(dir, "quests", "generated", "authors.json")));
            Assert.Empty(BakedOutputs.Clean(dir, "m"));
            Assert.Empty(BakedOutputs.Clean(Path.Combine(dir, "missing"), "m"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheMarkerIsTheSameEverywhere() => Assert.Equal(Baker.Comment, BakedOutputs.Comment);
}

/// <summary>The clean-up through the targets (a stub project, dotnet msbuild): turned off without bookkeeping, an author's
/// replacement, and a bake that fails.</summary>
[Collection("bake")]
public sealed class BakeCleanTargetsTests
{
    private static string Targets => Path.Combine(AppContext.BaseDirectory, "Kiln.Bake.targets");

    private sealed class Stub : IDisposable
    {
        public readonly string Dir = Directory.CreateTempSubdirectory("kiln-targets-").FullName;
        public string Out => Path.Combine(Dir, "out") + "/";
        public string Obj => Path.Combine(Dir, "obj") + "/";

        public Stub()
        {
            Directory.CreateDirectory(Out);
            Directory.CreateDirectory(Obj);
            File.WriteAllText(Path.Combine(Dir, "mod.dll"), "");
            File.WriteAllText(Path.Combine(Out, "kiln.mod.json"), """{"id":"stub"}""");
            File.WriteAllText(Path.Combine(Dir, "stub.proj"), $"""
                <Project>
                  <PropertyGroup>
                    <_KilnServiceList>;Quests;</_KilnServiceList>
                    <KilnRepoMode>true</KilnRepoMode>
                    <KilnRepoRoot>{Dir}/</KilnRepoRoot>
                    <OutDir>{Out}</OutDir>
                    <IntermediateOutputPath>{Obj}</IntermediateOutputPath>
                    <TargetPath>{Dir}/mod.dll</TargetPath>
                    <KilnResolvedModId>stub</KilnResolvedModId>
                    <KilnSdkVersion>0.0.0-test</KilnSdkVersion>
                    <_KilnTasksDir>{AppContext.BaseDirectory}</_KilnTasksDir>
                  </PropertyGroup>
                  <Import Project="{Targets}" />
                  <Target Name="KilnDeploy" />
                </Project>
                """);
        }

        public void Baked(string relative) => File.WriteAllText(Path.Combine(Out, relative),
            new JsonObject { ["format"] = 1, ["comment"] = Baker.Comment, ["quests"] = new JsonObject() }.ToJsonString());

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
            p.WaitForExit(180_000);
            return (p.ExitCode, o.Result + e.Result);
        }

        public void Dispose() => Directory.Delete(Dir, true);
    }

    [Fact]
    public void TurnedOffWithoutBookkeepingStillRemovesWhatTheSdkWrote()
    {
        using var s = new Stub();
        Directory.CreateDirectory(Path.Combine(s.Out, "quests", "generated"));
        Directory.CreateDirectory(Path.Combine(s.Out, "skills"));
        s.Baked("quests/generated/stub.json");
        File.WriteAllText(Path.Combine(s.Out, "skills", "stub.generated.json"), """{"comment":"the author's","gameTree":{}}""");
        var (code, output) = s.MsBuild("-t:_KilnBakeOff", "-p:KilnBake=false");
        Assert.True(code == 0, output);
        Assert.False(File.Exists(Path.Combine(s.Out, "quests", "generated", "stub.json")));
        Assert.True(File.Exists(Path.Combine(s.Out, "skills", "stub.generated.json")));
    }

    [Fact]
    public void TurnedOffKeepsAnAuthorsFileThatReplacedABakedOne()
    {
        using var s = new Stub();
        Directory.CreateDirectory(Path.Combine(s.Out, "quests", "generated"));
        // the bookkeeping still lists the path, but the file there is the author's now
        File.WriteAllLines(Path.Combine(s.Obj, "kiln.bake.files"), ["quests/generated/stub.json"]);
        File.WriteAllText(Path.Combine(s.Out, "quests", "generated", "stub.json"), """{"comment":"the author's","quests":{}}""");
        var (code, output) = s.MsBuild("-t:_KilnBakeOff", "-p:KilnBake=false");
        Assert.True(code == 0, output);
        Assert.True(File.Exists(Path.Combine(s.Out, "quests", "generated", "stub.json")));
    }

    [Fact]
    public void AFailedBakeLeavesNothingOfAnEarlierOne()
    {
        using var s = new Stub();
        Directory.CreateDirectory(Path.Combine(s.Out, "quests", "generated"));
        s.Baked("quests/generated/stub.json");
        File.WriteAllText(Path.Combine(s.Out, "quests", "generated", "authors.json"), """{"quests":{}}""");
        // a bake host that cannot run: dotnet exec fails, as a crash or an ended process would
        var host = Path.Combine(s.Dir, "host") + "/";
        Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(host, "Kiln.Sdk.Bake.dll"), "not an assembly");
        var (code, output) = s.MsBuild("-t:KilnBake", $"-p:_KilnBakeHostDir={host}");
        Assert.NotEqual(0, code);
        Assert.Contains("KILN032", output);
        Assert.False(File.Exists(Path.Combine(s.Out, "quests", "generated", "stub.json")));
        Assert.True(File.Exists(Path.Combine(s.Out, "quests", "generated", "authors.json")));
        Assert.False(File.Exists(Path.Combine(s.Obj, "kiln.bake.stamp")));
    }
}
