// SPDX-License-Identifier: MIT
// Mod.OnBoot's staging (chapter 08 §4.1, S2): BootDefinitions copies a block into canonical JSON and hands it to kiln.boot; nothing compiles.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Blocks.Tests;

public sealed class BootStagingTests
{
    private static BootContext Frame(bool bootOn = true, Func<string, string, string, (bool, string?)>? stage = null) =>
        new("boot-gallery", 1, BootRoles.DedicatedServer, new UnavailableGameData()) { BootOn = bootOn, Stage = stage };

    // the sample's kinds this build lowers, as code and as the data twin of blocks/boot-gallery.json (keys in another order, spaces)
    public static TheoryData<IBlock, string, string, string> Twins() => new()
    {
        { new StatusBlock { Name = "Boot Cold", Seconds = 4 }, "statuses", "cold", """{ "seconds": 4, "name": "Boot Cold" }""" },
        { new ItemBlock { Name = "Boot Token", Stack = 20 }, "items", "wood-token", """{ "stack": 20, "name": "Boot Token" }""" },
        { new TalentBlock { Name = "Boot Cold Talent", Keeps = "cold", Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, 0] } }, "talents", "cold-talent",
          """{"game": {"offset": [170, 0], "from": ["0x8efcc472"]}, "keeps": "cold", "name": "Boot Cold Talent"}""" },
    };

    [Theory]
    [MemberData(nameof(Twins))]
    public void Code_staging_equals_the_canonical_form_of_the_data_twin(IBlock block, string kind, string key, string twin)
    {
        var code = Frame();
        var data = Frame();
        var r = Stage(BootDefinitions.For(code), key, block);
        Assert.True(r, "staged");
        Assert.True(BootDefinitions.For(data).DefineJson(kind, key, twin).Ok);
        Assert.Equal(code.Staged.Single().Json, data.Staged.Single().Json);
        Assert.Equal(kind, code.Staged.Single().Kind);
        // canonical: keys sorted, no white space
        Assert.Equal(BootDefinitions.Canonical(JsonNode.Parse(code.Staged.Single().Json)), code.Staged.Single().Json);
    }

    private static bool Stage(BootDefinitions d, string key, IBlock block) => block switch
    {
        StatusBlock b => d.Define(key, b).Ok,
        ItemBlock b => d.Define(key, b).Ok,
        TalentBlock b => d.Define(key, b).Ok,
        _ => throw new ArgumentException(block.GetType().Name),
    };

    [Fact]
    public void BOOT_CAP_OFF_names_the_capability()
    {
        var e = Assert.Throws<KilnUnavailableException>(() => BootDefinitions.For(Frame(bootOn: false)));
        Assert.Contains("mods.boot", e.Message);
    }

    [Fact]
    public void Bad_names_and_shapes_throw_ArgumentException()
    {
        var d = BootDefinitions.For(Frame());
        foreach (var bad in new[] { "Bad", "a/b", "a:b", "a..b", "1a", "", new string('a', 97) })
            Assert.Throws<ArgumentException>(() => d.Define(bad, new StatusBlock { Name = "x", Seconds = 1 }));
        Assert.Throws<ArgumentException>(() => d.DefineJson("weapons", "w", "{}"));                       // not a boot kind
        Assert.Throws<ArgumentException>(() => d.DefineJson("statuses", "s", """{"seconds": "four"}""")); // not a StatusBlock
    }

    [Fact]
    public void A_chapter_kind_this_build_does_not_lower_is_a_soft_refusal()
    {
        var d = BootDefinitions.For(Frame());
        var r = d.DefineJson("npcs", "keeper", "{}");
        Assert.False(r.Ok);
        Assert.StartsWith("NotInBootKinds:", r.Reason);
        var p = d.Define("cold-prop", new PropBlock { Name = "Boot Crafting Prop" });
        Assert.False(p.Ok);
        Assert.StartsWith("NotInBootKinds:", p.Reason);
    }

    [Fact]
    public void Same_content_again_is_fine_other_content_is_DuplicateDefinition()
    {
        var f = Frame();
        var d = BootDefinitions.For(f);
        Assert.True(d.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 }).Ok);
        Assert.True(d.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 }).Ok);
        var dup = d.Define("cold", new StatusBlock { Name = "Other", Seconds = 4 });
        Assert.False(dup.Ok);
        Assert.StartsWith("DuplicateDefinition:", dup.Reason);
        Assert.Single(f.Staged);
        var reference = d.Define("hit", new EffectBlock { Applies = new StatusApply { Status = "cold" } }).Reference!;
        Assert.Equal("boot-gallery:hit", reference.QualifiedName);
        Assert.Equal("effects", reference.Kind);
    }

    [Fact]
    public void A_closed_frame_answers_PhaseClosed()
    {
        var f = Frame();
        var d = BootDefinitions.For(f);
        f.Open = false;
        var r = d.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 });
        Assert.False(r.Ok);
        Assert.StartsWith("PhaseClosed:", r.Reason);
        Assert.Empty(f.Staged);
    }

    [Fact]
    public void The_native_stage_gets_the_canonical_json_and_its_refusal_is_kept()
    {
        var seen = new List<(string, string, string)>();
        var f = Frame(stage: (k, n, j) => { seen.Add((k, n, j)); return n == "refused" ? (false, "TooMany: native") : (true, null); });
        var d = BootDefinitions.For(f);
        Assert.True(d.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 }).Ok);
        Assert.Equal(("statuses", "cold", f.Staged.Single().Json), seen.Single());
        var r = d.Define("refused", new StatusBlock { Name = "R", Seconds = 1 });
        Assert.False(r.Ok);
        Assert.Equal("TooMany: native", r.Reason);
        Assert.Single(f.Staged);
    }

    [Fact]
    public void Blocks_Define_is_refused_inside_a_boot_frame()
    {
        BootFrame.Current = Frame();
        try
        {
            // the class Kiln.Blocks is hidden by this project's namespace Kiln.Blocks: called through reflection
            var define = typeof(StatusBlock).Assembly.GetType("Kiln.Blocks")!.GetMethods().Single(m => m.Name == "Define" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(typeof(StatusBlock));
            var e = Assert.Throws<System.Reflection.TargetInvocationException>(() => define.Invoke(null, ["cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 }]));
            var inner = Assert.IsType<InvalidOperationException>(e.InnerException);
            Assert.StartsWith("PhaseClosed:", inner.Message);
        }
        finally
        {
            BootFrame.Current = null;
        }
    }

    [Fact]
    public void Read_names_mods_boot_game_data_while_no_reader_exists()
    {
        var f = Frame();
        var e = Assert.Throws<KilnUnavailableException>(() => f.GameData.Read("item:Material_T1_Wood_Logs"));
        Assert.Contains("mods.boot.game_data", e.Message);
    }
}
