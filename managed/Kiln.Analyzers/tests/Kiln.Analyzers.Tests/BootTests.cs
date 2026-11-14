// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN034: an OnBoot override the manifest does not opt into; the run-time Blocks.Define in boot code.</summary>
public sealed class BootPhaseTests
{
    private static readonly BootAnalyzer A = new();

    private const string WithOnBoot = """
        public sealed class M : Mod
        {
            public override void OnBoot(BootContext boot)
            {
                var staged = BootDefinitions.For(boot).Define("cold", new StatusBlock { Name = "Cold", Seconds = 4 });
            }
        }
        """;

    [Fact]
    public async Task OnBoot_without_boot_in_the_manifest()
    {
        var d = await Run(A, WithOnBoot, manifest: """{ "id": "m", "side": "both" }""");
        Ids(d, "KILN034");
        Assert.Equal("OnBoot", d[0].At());
        Assert.Equal(DiagnosticSeverity.Error, d[0].Severity);
        Assert.Contains("'M.OnBoot' never runs", d[0].GetMessage());
        Assert.Contains("add \"boot\": true to kiln.mod.json", d[0].GetMessage());

        Ids(await Run(A, WithOnBoot, manifest: """{ "id": "m", "boot": false }"""), "KILN034");
        // a Kiln.Sdk project without its own kiln.mod.json: the manifest the SDK writes has no "boot"
        Ids(await Run(A, WithOnBoot, Props(("KilnModId", "m"), ("KilnAdvanced", "false"))), "KILN034");
        // the file wins over the property, as in the manifest task
        Ids(await Run(A, WithOnBoot, Props(("KilnModId", "m"), ("KilnBoot", "true")), manifest: """{ "id": "m" }"""), "KILN034");
    }

    [Fact]
    public async Task OnBoot_with_boot_in_the_manifest_or_nothing_known()
    {
        Ids(await Run(A, WithOnBoot, manifest: """{ "id": "m", "boot": true }"""));
        Ids(await Run(A, WithOnBoot, manifest: """{ "id": "m", "boot" : "required" }"""));
        Ids(await Run(A, WithOnBoot, Props(("KilnModId", "m"), ("KilnBoot", "required"))));
        Ids(await Run(A, WithOnBoot));  // not a Kiln.Sdk project: nothing known
        // no override: nothing to say about the manifest
        Ids(await Run(A, "public sealed class M : Mod { public override void OnLoad() { } }", manifest: """{ "id": "m" }"""));
    }

    [Fact]
    public async Task OnBoot_of_a_base_class_counts()
    {
        var d = await Run(A, """
            public abstract class Base : Mod { public override void OnBoot(BootContext boot) { } }
            public sealed class M : Base { public override void OnBoot(BootContext boot) { base.OnBoot(boot); } }
            """, manifest: """{ "id": "m" }""");
        Ids(d, "KILN034", "KILN034");
        Assert.Contains("'Base.OnBoot'", d[0].GetMessage());
        Assert.Contains("'M.OnBoot'", d[1].GetMessage());
    }

    [Fact]
    public async Task Blocks_Define_in_boot_code()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    Blocks.Define("cold", new StatusBlock { Name = "Cold", Seconds = 4 });
                    var made = new[] { "a", "b" }.Select(n => Blocks.Define(n, new StatusBlock { Name = n })).ToList();
                    void Local() => Blocks.Define("c", new StatusBlock { Name = "C" });
                    Local();
                    Stage();
                }

                [BootThreadOnly]
                private static void Stage() => Blocks.Define("d", new BuildingMaterialBlock { Name = "D" });
            }
            """, manifest: """{ "id": "m", "boot": true }""");
        Ids(d, "KILN034", "KILN034", "KILN034", "KILN034");
        Assert.All(d, x => Assert.Equal("Define", x.At()));
        Assert.Contains("Blocks.Define registers a block at once and is refused in OnBoot", d[0].GetMessage());
        Assert.Contains("stage it with BootDefinitions.For(boot).Define(name, block)", d[0].GetMessage());
        Assert.Contains("refused in 'Stage' ([BootThreadOnly])", d[3].GetMessage());
    }

    [Fact]
    public async Task Blocks_Define_outside_boot_code_and_staging_in_it()
    {
        Ids(await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad() => Blocks.Define("cold", new StatusBlock { Name = "Cold", Seconds = 4 });

                public override void OnBoot(BootContext boot)
                {
                    var definitions = BootDefinitions.For(boot);
                    var status = definitions.Define("cold", new StatusBlock { Name = "Cold", Seconds = 4 });
                    var json = definitions.DefineJson("statuses", "warm", "{\"name\":\"Warm\"}");
                    // handed to Kiln: runs later on the tick thread, not in the boot phase
                    Context.Scheduler.Post(() => Blocks.Define("later", new StatusBlock { Name = "Later" }));
                }

                private void Helper() => Blocks.Define("helper", new StatusBlock { Name = "Helper" });  // not marked: not known as boot code
            }
            """, manifest: """{ "id": "m", "boot": true }"""));
    }
}

/// <summary>KILN035: a tick, world or service API in boot code.</summary>
public sealed class BootThreadApiTests
{
    private static readonly BootAnalyzer A = new();
    private const string Boot = """{ "id": "m", "boot": true }""";

    [Fact]
    public async Task Tick_and_service_apis_in_OnBoot()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    Chat.Broadcast("booting");
                    var n = Players.All.Count;
                    Players.Joined += p => { };
                    var raw = Players.Diagnostics.RawSlot(0);
                }
            }
            """, manifest: Boot);
        Ids(d, "KILN035", "KILN035", "KILN035", "KILN035");
        Assert.Equal(DiagnosticSeverity.Warning, d[0].Severity);
        Assert.Equal("'Chat.Broadcast' must be called on the tick thread, but OnBoot runs on the boot thread before the game is entered, where no " +
                     "tick, world, player or live registry exists; read the game's data with boot.GameData.Read, stage blocks with " +
                     "BootDefinitions.For(boot), and do the rest in OnLoad", d[0].GetMessage());
        Assert.StartsWith("'Players.All' is a Kiln.Players service API, but OnBoot runs on the boot thread", d[1].GetMessage());
        Assert.StartsWith("'Players.Joined' is a Kiln.Players service API", d[2].GetMessage());
        Assert.StartsWith("'Diagnostics.RawSlot' must be called on the tick thread", d[3].GetMessage());  // a type nested in the service's class
    }

    [Fact]
    public async Task In_a_BootThreadOnly_method_a_local_function_and_a_LINQ_lambda()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    var names = new[] { "a" }.Select(n => Players.Find(n)).ToList();
                    int Count() => Players.Count;
                    _ = Count();
                    Announce();
                }

                [BootThreadOnly]
                private static void Announce() => Chat.Broadcast("boot");
            }
            """, manifest: Boot);
        Ids(d, "KILN035", "KILN035", "KILN035");
        Assert.Contains("'Players.Find'", d[0].GetMessage());
        Assert.Contains("'Players.Count'", d[1].GetMessage());
        Assert.Contains("but 'Announce' ([BootThreadOnly]) runs on the boot thread", d[2].GetMessage());
    }

    [Fact]
    public async Task What_boot_code_may_use()
    {
        Ids(await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    if (!Players.Available || !Capabilities.Is("mods.boot.game_data")) return;  // capability checks
                    var wood = boot.GameData.Read("item:Material_T1_Wood_Logs");                // [BootThreadOnly]
                    var definitions = BootDefinitions.For(boot);
                    var hit = definitions.Define("cold-hit", new EffectBlock { Damage = Damage.Ice(4) });  // data helpers of Kiln.Blocks
                    var state = Mods.BootState(boot.ModId);                                     // [AnyThread]
                    var name = nameof(Players.All);
                    Context.Scheduler.Post(() => Chat.Broadcast("later, on the tick thread"));
                    Context.Log.Info($"staged {hit.Ok} {state.State} {name}");
                }

                public override void OnLoad()
                {
                    Chat.Broadcast("loaded");
                    var n = Players.All.Count;
                }
            }
            """, manifest: Boot));
    }

    [Fact]
    public async Task Attributes_on_a_service_member_decide()
    {
        // [TickThreadOnly] on a Kiln member; [AnyThread] and [BootThreadOnly] members of a service's own class
        var lib = Library("""
            namespace Kiln
            {
                public static class Example
                {
                    public static void Run() { }
                    [AnyThread] public static void Log() { }
                    [BootThreadOnly] public static void Stage() { }
                }
                public static class Steps { [TickThreadOnly] public static void Step() { } }
            }
            """, "Kiln.Example");
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    Example.Run();
                    Example.Log();
                    Example.Stage();
                    Steps.Step();
                }
            }
            """, manifest: Boot, extra: [lib]);
        Ids(d, "KILN035", "KILN035", "KILN035");
        Assert.StartsWith("'Example.Run' is a Kiln.Example service API", d[0].GetMessage());
        Assert.StartsWith("'Example.Log' is a Kiln.Example service API", d[1].GetMessage());  // [AnyThread]: any thread, still no world
        Assert.StartsWith("'Steps.Step' must be called on the tick thread", d[2].GetMessage());
    }
}

/// <summary>KILN121 in boot code: OnBoot runs only while mods.boot is on (Mod.OnBoot is marked with it), so an override passes the row on;
/// the game-data snapshot (mods.boot.game_data) may still be missing and is asked for.</summary>
public sealed class BootCapabilityTests
{
    private static readonly CapabilityAnalyzer A = new();

    [Fact]
    public async Task An_OnBoot_override_has_mods_boot()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    var definitions = BootDefinitions.For(boot);
                    var status = definitions.Define("cold", new StatusBlock { Name = "Cold", Seconds = 4 });
                    var wood = boot.GameData.Read("item:Material_T1_Wood_Logs");
                    if (Capabilities.Is("mods.boot.game_data")) _ = boot.GameData.Read("template:NPC_Workshop_Fisher_Trader");
                }

                public override void OnLoad() => _ = BootDefinitions.For(null!);
            }
            """);
        Ids(d, "KILN121", "KILN121");
        Assert.Contains("needs capability mods.boot.game_data", d[0].GetMessage());
        Assert.Equal("For", d[1].At());  // outside OnBoot nothing asks for mods.boot
        Assert.Contains("needs capability mods.boot ", d[1].GetMessage());
    }
}
