// SPDX-License-Identifier: MIT
// Mod.OnBoot's lowering (chapter 08 §4.3 steps 1-3, S5): staged definitions become the collectors' documents through the same Compiler.Lower
// a runtime Define uses, with a sink that registers nothing; the same input gives the same ids; a data file of the mod wins.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Blocks.Tests;

public sealed class BootLoweringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-boot-lowering-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static List<BootStaged> Sample()
    {
        var f = new BootContext("boot-gallery", 1, BootRoles.DedicatedServer, new UnavailableGameData());
        var d = BootDefinitions.For(f);
        Assert.True(d.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 }).Ok);
        Assert.True(d.Define("cold-hit", new EffectBlock { Applies = new StatusApply { Status = "cold" } }).Ok);
        Assert.True(d.Define("wood-token", new ItemBlock { Name = "Boot Token", Stack = 20 }).Ok);
        Assert.True(d.Define("cold-talent", new TalentBlock { Name = "Boot Cold Talent", Keeps = "cold", Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, 0] } }).Ok);
        return f.Staged;
    }

    private string Request(IEnumerable<BootStaged> staged, string? dir = null) => new JsonObject
    {
        ["id"] = "boot-gallery", ["clients"] = "mod", ["dir"] = dir,
        ["staged"] = new JsonArray(staged.Select(s => (JsonNode)new JsonObject { ["kind"] = s.Kind, ["key"] = s.Key, ["json"] = s.Json }).ToArray()),
    }.ToJsonString();

    private static JsonObject Lower(string request) => JsonNode.Parse(BootLowering.LowerOwner(request))!.AsObject();

    [Fact]
    public void The_sample_lowers_into_documents_of_assets_content_and_skills_and_registers_nothing()
    {
        var sink = new CountingSink();
        var previous = Lowered._sinkOverride;
        Lowered._sinkOverride = sink;  // anything that escapes the boot sink lands here
        try
        {
            var r = Lower(Request(Sample()));
            Assert.True(r["ok"]!.GetValue<bool>(), (string?)r["reason"]);
            Assert.Equal(["statuses/cold", "effects/cold-hit", "items/wood-token", "talents/cold-talent"], r["definitions"]!.AsArray().Select(x => (string)x!));
            var docs = r["docs"]!.AsObject();
            var content = docs["content"]!.AsArray();
            Assert.Contains(content, d => (string?)d!["document"]!["op"] == "clone" && (string?)d["document"]!["section"] == "buffs");
            Assert.Contains(content, d => (string?)d!["document"]!["op"] == "define" && (string?)d["document"]!["kind"] == "items");
            var program = docs["assets"]!.AsArray().First()!["document"]!["entries"]![0]!;
            Assert.Equal("0xd4922d88", (string?)program["type"]);
            Assert.NotEmpty(Convert.FromBase64String((string)program["data"]!));
            var node = Assert.Single(docs["skills"]!.AsArray())!["document"]!["nodes"]![0]!;
            Assert.Equal("0x8efcc472", (string?)node["from"]![0]);
            Assert.Equal(0, sink.Count);
            Assert.False(Lowered._bootLowering);
        }
        finally
        {
            Lowered._sinkOverride = previous;
        }
    }

    [Fact]
    public void A_runtime_Define_and_the_boot_lowering_give_the_same_ids()
    {
        var r = Lower(Request(Sample()));
        var buff = r["docs"]!["content"]!.AsArray().First(d => (string?)d!["document"]!["section"] == "buffs")!["document"]!["definition"]!;
        var item = r["docs"]!["content"]!.AsArray().First(d => (string?)d!["document"]!["kind"] == "items")!["document"]!["definition"]!;
        var mod = new ModBlocks("boot-gallery", 1) { ManifestClients = "mod" };
        var (status, _, _) = Compiler.Lower(mod, "cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 });
        var (token, _, _) = Compiler.Lower(mod, "wood-token", new ItemBlock { Name = "Boot Token", Stack = 20 });
        Assert.Equal(status.Guid.ToString(), (string?)buff["guid"]);
        Assert.Equal(token.Guid.ToString(), (string?)item["guid"]);
        // and the same input lowers to the same bytes again
        Assert.Equal(r.ToJsonString(), Lower(Request(Sample())).ToJsonString());
    }

    [Fact]
    public void A_data_file_of_the_mod_wins_over_staged_code()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "blocks"));
        File.WriteAllText(Path.Combine(_dir, "blocks", "gallery.json"), """{ "statuses": { "cold": { "name": "File Cold", "seconds": 9 } } }""");
        var r = Lower(Request(Sample().Where(s => s.Kind == "statuses" || s.Kind == "items"), _dir));
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(["items/wood-token"], r["definitions"]!.AsArray().Select(x => (string)x!));
        var issue = Assert.Single(r["issues"]!.AsArray())!;
        Assert.Equal("ShadowedByFile", (string?)issue["code"]);
        Assert.Equal("cold", (string?)issue["key"]);
        Assert.DoesNotContain(r["docs"]!["content"]!.AsArray(), d => (string?)d!["document"]!["section"] == "buffs");
    }

    [Fact]
    public void A_definition_that_does_not_compile_refuses_the_author()
    {
        var staged = new List<BootStaged> { new("statuses", "nameless", """{"seconds":4}""") };
        var r = Lower(Request(staged));
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("statuses/nameless", (string?)r["reason"]);
    }

    private sealed class CountingSink : Lowered.ILoweredSink
    {
        public int Count;
        public void Program(ulong owner, Guid guid, byte[] blob) => Count++;
        public void Clone(ulong owner, string section, JsonObject def) => Count++;
        public void Item(ulong owner, ItemDefinition d) => Count++;
        public void Recipe(ulong owner, RecipeDefinition d) => Count++;
    }
}
