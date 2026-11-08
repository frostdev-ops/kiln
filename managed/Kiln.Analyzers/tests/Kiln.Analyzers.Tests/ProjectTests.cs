// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

public sealed class AdvancedTests
{
    private static readonly ModProjectAnalyzer A = new();

    private const string Source = """
        public sealed class M : Mod
        {
            public override void OnLoad()
            {
                var a = Symbols.Get("game::Foo");
                var ok = Memory.TryRead<int>(a, out var v);
                Chat.Broadcast("not advanced");
            }
        }
        """;

    [Fact]
    public async Task Advanced_api_without_the_opt_in()
    {
        var d = await Run(A, Source, Props(("KilnAdvanced", "false"), ("KilnServices", "Chat;Hooks")));
        Ids(d, "KILN107", "KILN107");
        Assert.Contains("Symbols.Get", d[0].GetMessage());
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d[0].Severity);
    }

    [Fact]
    public async Task Opted_in_or_unknown()
    {
        Ids(await Run(A, Source, Props(("KilnAdvanced", "true"), ("KilnServices", "Chat;Hooks"))));
        Ids(await Run(A, Source));  // not a Kiln.Sdk project: nothing known
        Ids(await Run(A, Source, Props(("KilnAdvanced", "false"), ("KilnServices", "Chat;Hooks")),
            manifest: "{ \"id\": \"m\", \"advanced\": true, // the file wins\n}"));
    }

    [Fact]
    public async Task Manifest_without_advanced_is_not_advanced()
    {
        var d = await Run(A, Source, Props(("KilnServices", "Chat,Hooks")), manifest: """{ "id": "m" }""");
        Ids(d, "KILN107", "KILN107");
    }
}

public sealed class ServicesTests
{
    private static readonly ModProjectAnalyzer A = new();

    [Fact]
    public async Task Service_types_need_their_KilnServices_entry()
    {
        var d = await Run(A, """
            using Kiln.Testing;
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Chat.Broadcast("listed");
                    Players.Joined += p => { };
                    Players.Left += p => { };
                    Combat.SetHealth(new EntityId(3), 5);
                }
                [KilnTest] public void T() { }
            }
            """, Props(("KilnServices", " chat ; combat")));
        Ids(d, "KILN108", "KILN108");
        Assert.Contains("Kiln.Players", d[0].GetMessage());
        Assert.Contains("Kiln.Testing", d[1].GetMessage());
    }

    [Fact]
    public async Task Sdk_comma_list()
    {
        Ids(await Run(A, "public sealed class M : Mod { public override void OnLoad() { Players.Joined += p => { }; Chat.Broadcast(\"x\"); } }",
            Props(("KilnServicesList", "Chat,Players"))));
    }

    [Fact]
    public async Task No_property_no_diagnostics()
    {
        Ids(await Run(A, "public sealed class M : Mod { public override void OnLoad() => Players.Joined += p => { }; }"));
    }
}

public sealed class BlocksTests
{
    private static readonly BlocksAnalyzer A = new();

    private static string Mod(string body) => "public sealed class M : Mod { public override void OnLoad() {\n" + body + "\n} }";

    [Fact]
    public async Task A_good_mod_is_quiet()
    {
        var d = await Run(A, Mod("""
            var chilled = Blocks.Define("chilled", new StatusBlock { Name = "Chilled", Seconds = 8 });
            Blocks.Define("burst", new EffectBlock { Damage = Damage.Ice(40), Applies = "chilled", Removes = ["other-mod:hold", "0x5f5d2f5c"] });
            Blocks.Define("again", new EffectBlock { Then = ["burst"] });
            """), Props(("KilnClients", "kiln")));
        Ids(d);
    }

    [Fact]
    public async Task Bad_and_duplicate_names()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("no spaces", new EffectBlock());
            Blocks.Define("twice", new EffectBlock());
            Blocks.Define("twice", new EffectBlock());
            """));
        Ids(d, "KILN110", "KILN110");
        Assert.Contains("no spaces", d[0].GetMessage());
        Assert.Contains("more than once", d[1].GetMessage());
    }

    [Fact]
    public async Task One_of_two_branches_is_not_a_duplicate()
    {
        Ids(await Run(A, Mod("""
            if (Environment.ProcessorCount > 2) Blocks.Define("either", new EffectBlock { Heal = 1 });
            else Blocks.Define("either", new EffectBlock { Heal = 2 });
            """)));
    }

    [Fact]
    public async Task A_name_that_is_nothing_or_the_wrong_kind()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("hit", new EffectBlock { Applies = "no-such-status" });
            Blocks.Define("wrong", new EffectBlock { Applies = "hit" });
            Blocks.Define("then", new EffectBlock { Then = ["no-such-effect"] });
            """));
        Ids(d, "KILN111", "KILN111", "KILN111");
        Assert.Contains("no-such-status", d[0].GetMessage());
        Assert.Contains("effect block", d[1].GetMessage());
        Assert.Contains("no-such-effect", d[2].GetMessage());
    }

    [Fact]
    public async Task Names_built_at_run_time_could_be_any()
    {
        Ids(await Run(A, Mod("""
            var n = Environment.ProcessorCount;
            Blocks.Define($"made{n}", new EffectBlock());
            Blocks.Define("uses", new EffectBlock { Then = ["made4"] });
            """)));
    }

    [Fact]
    public async Task Clients_too_low()
    {
        var source = Mod("""
            Blocks.Define("status", new StatusBlock { Name = "x", Seconds = 1 });
            Blocks.Define("own-sound", new EffectBlock { Sound = "chime" });
            """);
        (string, string)[] cook = [("/mod/assets/cook.json", """{ "sounds": { "chime": {} } }""")];   // "chime" is a cue of the mod's cook recipe
        var none = await Run(A, source, Props(("KilnClients", "none")), files: cook);
        Ids(none, "KILN112", "KILN112");
        Assert.Contains(none, x => x.GetMessage().Contains("Sound"));
        Assert.Contains(none, x => x.GetMessage().Contains("new status"));
        Ids(await Run(A, source, Props(("KilnClients", "kiln")), files: cook), "KILN112");
        Ids(await Run(A, source, Props(("KilnClients", "mod")), files: cook));
        Ids(await Run(A, source, files: cook));   // nothing said: the build works it out
        Ids(await Run(A, source, manifest: """{ "id": "m", "clients": "mod" }""", properties: Props(("KilnClients", "none")), files: cook));   // the file wins
    }

    [Fact]
    public async Task A_cue_by_guid_or_of_another_mod_is_not_the_mods_own()
    {
        Ids(await Run(A, Mod("""
            Blocks.Define("a", new EffectBlock { Sound = "2f3d7c11-90ab-4cde-8f01-23456789abcd" });
            Blocks.Define("b", new EffectBlock { Sound = "other-mod:chime" });
            """), Props(("KilnClients", "none"))));
    }

    [Fact]
    public async Task A_new_vfx_asks_for_the_mod_on_every_client()
    {
        var source = Mod("""
            var burst = Blocks.Define("burst", new VfxBlock { Particles = [new ParticleBlock { Burst = 1 }] });
            Blocks.Define("a", new EffectBlock { Vfx = "burst" });
            """);
        var none = await Run(A, source, Props(("KilnClients", "kiln")));
        Ids(none, "KILN112", "KILN112");
        Assert.Contains(none, x => x.GetMessage().Contains("new vfx"));
        Assert.Contains(none, x => x.GetMessage().Contains("Vfx"));
        Ids(await Run(A, source, Props(("KilnClients", "mod"))));
    }

    [Fact]
    public async Task A_game_vfx_by_name_asks_for_no_more_than_the_block()
    {
        Ids(await Run(A, Mod("""
            Blocks.Define("a", new EffectBlock { Vfx = "game:Explosion_Player_Spell_Fireball_1" });
            Blocks.Define("b", new EffectBlock { Vfx = "2f3d7c11-90ab-4cde-8f01-23456789abcd" });
            Blocks.Define("s", new StatusBlock { Name = "x", Seconds = 1, Vfx = VfxRef.Game("Explosion_Player_Spell_Fireball_1") });
            """), Props(("KilnClients", "kiln"))));
    }

    [Fact]
    public async Task A_vfx_name_that_is_nothing_or_the_wrong_kind()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("hold", new StatusBlock { Name = "x", Seconds = 1 });
            Blocks.Define("a", new EffectBlock { Vfx = "no-such-vfx" });
            Blocks.Define("b", new EffectBlock { Vfx = "hold" });
            """));
        Ids(d, "KILN111", "KILN111");
        Assert.Contains(d, x => x.GetMessage().Contains("no-such-vfx"));
        Assert.Contains(d, x => x.GetMessage().Contains("status block"));
    }

    [Fact]
    public async Task An_effect_alone_asks_for_nothing()
    {
        Ids(await Run(A, Mod("""Blocks.Define("only", new EffectBlock { Heal = 1 });"""), Props(("KilnClients", "none"))));
    }

    [Theory]
    [InlineData("""Blocks.Define("e", new EffectBlock { Heal = 1 });""", "none")]
    [InlineData("""Blocks.Define("s", new StatusBlock { Name = "x", Seconds = 1 });""", "kiln")]
    [InlineData("""Blocks.Define("e", new EffectBlock { Sound = "chime" });""", "mod")]
    [InlineData("""Blocks.Define("v", new VfxBlock { Light = new LightBlock { Radius = 3f } });""", "mod")]
    [InlineData("""Blocks.Define("m", new MaterialBlock { Tint = "#ffffff" });""", "mod")]
    [InlineData("""Blocks.Define("e", new EffectBlock { Vfx = "mine" });""", "mod")]
    [InlineData("""Blocks.Define("e", new EffectBlock { Vfx = "game:Explosion_Player_Spell_Fireball_1" });""", "none")]
    public void The_generator_writes_the_level_the_blocks_need(string body, string level)
    {
        var comp = Compile(Mod(body));
        var driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new ModClientsGenerator()).RunGenerators(comp);
        var text = driver.GetRunResult().GeneratedTrees.Single().ToString();
        Assert.Contains($"\"{ModClientsGenerator.MetadataKey}\", \"{level}\"", text);
    }

    [Fact]
    public void No_blocks_nothing_written()
    {
        var comp = Compile(Mod("""Chat.Broadcast("hello");"""));
        var driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new ModClientsGenerator()).RunGenerators(comp);
        Assert.Empty(driver.GetRunResult().GeneratedTrees);
    }
}
