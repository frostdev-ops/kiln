// SPDX-License-Identifier: MIT
// The SDK's KilnWrites task (Kiln.Sdk.Tasks.dll), run in-process on the fixture mod: which calls it reads, the game
// names it takes from Kiln.Game's layout table, and how it puts them into a manifest without touching the rest.
using System.Collections;
using System.Text.Json;
using Kiln.Sdk;
using Microsoft.Build.Framework;
using Xunit;

namespace Kiln.WorldTests;

sealed class Engine : IBuildEngine
{
    public readonly List<BuildErrorEventArgs> Errors = [];
    public readonly List<BuildWarningEventArgs> Warnings = [];
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "WritesTaskTests";
    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
    public void LogCustomEvent(CustomBuildEventArgs e) { }
    public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
    public void LogMessageEvent(BuildMessageEventArgs e) { }
    public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
}

public class WritesTaskTests
{
    private static string ModDll => Path.Combine(AppContext.BaseDirectory, "WritesMod.dll");
    private static string GameDll => Path.Combine(AppContext.BaseDirectory, "Kiln.Game.dll");

    private const string Minimal = """
        {
          // a mod's own manifest
          "id": "writes-mod",
          "version": "1.0.0",
          "apiVersion": "0.2",
          "entryAssembly": "WritesMod.dll",
        }
        """;

    // "writes" as the loader reads it: comments and trailing commas allowed.
    private static string[] Writes(string manifest)
    {
        using var doc = JsonDocument.Parse(manifest, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.TryGetProperty("writes", out var list) ? [.. list.EnumerateArray().Select(e => e.GetString()!)] : [];
    }

    private static (bool Ok, Engine Engine, KilnWrites Task, string Manifest) Run(string manifest, string? game = null, string? assembly = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "writes-runs", Guid.NewGuid().ToString("N"))).FullName;
        var file = Path.Combine(dir, "kiln.mod.json");
        File.WriteAllText(file, manifest);
        var engine = new Engine();
        var task = new KilnWrites { BuildEngine = engine, AssemblyPath = assembly ?? ModDll, GameReference = game ?? GameDll, ManifestPath = file };
        return (task.Execute(), engine, task, File.ReadAllText(file));
    }

    [Fact]
    public void The_scan_reads_typed_writes_and_updates_and_nothing_else()
    {
        var scan = KilnWrites.Find(ModDll);
        Assert.Equal(
            [("Kiln.Ecs+Fuel", "Update"), ("Kiln.Ecs+ScaleComponent", "Update"), ("Kiln.Ecs+ScaleComponent", "Write"), ("WritesMod.Homemade", "Write")],
            scan.Types.Order().ToArray());
        // World.SetTransformScale writes CurrentTransform whatever its arguments (0.2.0-dev.28)
        Assert.Equal(["CurrentTransform"], scan.Fixed);
        // the mod's own generic helper: the build cannot know its type
        var open = Assert.Single(scan.Unresolved);
        Assert.Equal("Write", open.Method);
        Assert.Contains("type parameter", open.Why);
    }

    [Fact]
    public void The_layout_table_gives_the_game_names_of_components_only()
    {
        var components = KilnWrites.Components(File.Exists(GameDll) ? ReadTable() : null)!;
        Assert.Equal("Fuel", components["Kiln.Ecs+Fuel"]);
        Assert.Equal("ScaleComponent", components["Kiln.Ecs+ScaleComponent"]);
        Assert.Equal(687, components.Count);
        Assert.DoesNotContain(components.Keys, k => k.Contains("Event", StringComparison.Ordinal) && k.EndsWith("StartGuardingEvent", StringComparison.Ordinal));
        Assert.Null(KilnWrites.Components(null));

        static string ReadTable()
        {
            using var s = typeof(Ecs).Assembly.GetManifestResourceStream("Kiln.Game.layouts")!;
            return new StreamReader(s).ReadToEnd();
        }
    }

    [Fact]
    public void The_task_fills_writes_from_the_code_and_warns_about_what_it_cannot_resolve()
    {
        var (ok, engine, task, manifest) = Run(Minimal);
        Assert.True(ok, string.Join("\n", engine.Errors.Select(e => e.Message)));
        Assert.Equal("CurrentTransform;Fuel;ScaleComponent", task.Added);
        Assert.Equal(["CurrentTransform", "Fuel", "ScaleComponent"], Writes(manifest));
        Assert.Contains("// a mod's own manifest", manifest);   // the rest of the file keeps its text
        // one warning for the generic helper, one for the mod's own struct
        Assert.Equal(2, engine.Warnings.Count);
        Assert.All(engine.Warnings, w => Assert.Equal("KILN030", w.Code));
        Assert.Contains(engine.Warnings, w => w.Message!.Contains("type parameter"));
        Assert.Contains(engine.Warnings, w => w.Message!.Contains("WritesMod.Homemade"));
    }

    [Fact]
    public void Names_the_author_listed_stay_and_a_second_run_changes_nothing()
    {
        var own = Minimal.Replace("\"id\"", "\"writes\": [\"Health\", \"Fuel\", /* by hand */ ],\n  \"id\"");
        var (ok, _, task, manifest) = Run(own);
        Assert.True(ok);
        Assert.Equal("CurrentTransform;ScaleComponent", task.Added);
        Assert.Equal(["Health", "Fuel", "CurrentTransform", "ScaleComponent"], Writes(manifest));
        Assert.Contains("/* by hand */", manifest);

        var (text, added) = KilnWrites.Fill(manifest, ["Fuel", "ScaleComponent", "Health", "CurrentTransform"]);
        Assert.Empty(added);
        Assert.Equal(manifest, text);
    }

    [Theory]
    [InlineData("{ \"id\": \"a\" }", "{\n  \"writes\": [\"Fuel\"], \"id\": \"a\" }")]
    [InlineData("{ \"writes\": [], \"id\": \"a\" }", "{ \"writes\": [\"Fuel\"], \"id\": \"a\" }")]
    [InlineData("{ \"writes\": [\"Mana\"], \"id\": \"a\" }", "{ \"writes\": [\"Mana\", \"Fuel\"], \"id\": \"a\" }")]
    [InlineData("{ \"writes\": [\"Mana\",], \"id\": \"a\" }", "{ \"writes\": [\"Mana\", \"Fuel\",], \"id\": \"a\" }")]
    [InlineData("{ \"tests\": { \"writes\": [\"x\"] }, \"id\": \"ä\" }", "{\n  \"writes\": [\"Fuel\"], \"tests\": { \"writes\": [\"x\"] }, \"id\": \"ä\" }")]
    public void Fill_edits_only_the_top_level_list(string before, string after)
    {
        var (text, added) = KilnWrites.Fill(before, ["Fuel"]);
        Assert.Equal(["Fuel"], added);
        Assert.Equal(after, text);
    }

    [Fact]
    public void A_manifest_whose_writes_is_no_list_is_an_error_not_a_guess()
    {
        Assert.Throws<InvalidDataException>(() => KilnWrites.Fill("{ \"writes\": \"Fuel\" }", ["Fuel"]));
        Assert.Throws<InvalidDataException>(() => KilnWrites.Fill("[]", ["Fuel"]));
        var (ok, engine, _, manifest) = Run(Minimal.Replace("\"id\"", "\"writes\": 7,\n  \"id\""));
        Assert.False(ok);
        Assert.Equal("KILN031", Assert.Single(engine.Errors).Code);
        Assert.Contains("\"writes\": 7", manifest);   // left as it was
    }

    [Fact]
    public void A_transform_scale_call_alone_adds_CurrentTransform_also_without_Kiln_Game()
    {
        var (ok, engine, task, manifest) = Run(Minimal, game: "");
        Assert.True(ok, string.Join("\n", engine.Errors.Select(e => e.Message)));
        Assert.Contains("CurrentTransform", task.Added.Split(';'));
        Assert.Contains("CurrentTransform", Writes(manifest));
    }

    [Fact]
    public void An_assembly_without_typed_writes_leaves_the_manifest_alone()
    {
        var (ok, engine, task, manifest) = Run(Minimal, assembly: typeof(Kiln.EntityId).Assembly.Location);
        Assert.True(ok);
        Assert.Empty(engine.Warnings);
        Assert.Equal("", task.Added);
        Assert.Equal(Minimal, manifest);
    }
}
