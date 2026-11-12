// SPDX-License-Identifier: MIT
// The SDK's stamp task (Kiln.Sdk.KilnGameTypes, in Kiln.Sdk.Tasks.dll) run in-process with a small build engine on the
// fixture mod (Fixtures/stamp/StampMod, compiled against Kiln.Game variant stamp-a, built from the made-up dump a.json)
// and on assemblies that reference no generated type. The stamp must list exactly the types and fields the fixture
// names, every value of an enum or bitmask it names, the fields of a nested struct it reaches through a field, and
// nothing else.
using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Build.Framework;
using Xunit;

namespace Kiln.Game.Tests;

/// <summary>A build engine that keeps what the task logs.</summary>
sealed class StampEngine : IBuildEngine
{
    public readonly List<BuildErrorEventArgs> Errors = [];
    public readonly List<BuildWarningEventArgs> Warnings = [];
    public readonly List<string> Messages = [];
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "StampTaskTests";
    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
    public void LogCustomEvent(CustomBuildEventArgs e) { }
    public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
    public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e.Message ?? "");
    public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
}

static class Stamps
{
    public static string ModDll => GenRepo.Built("stamp", "StampMod", "StampMod.dll");
    public static string Variant(string v) => GenRepo.Built("variants", "stamp-" + v, "Kiln.Game.dll");

    /// <summary>A fresh folder under the test output (the build tree, never the source tree).</summary>
    public static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "stamp-runs", Guid.NewGuid().ToString("N"))).FullName;

    public static (bool Ok, StampEngine Engine, Kiln.Sdk.KilnGameTypes Task) Run(string assembly, string gameReference, string outDir)
    {
        var engine = new StampEngine();
        var task = new Kiln.Sdk.KilnGameTypes { BuildEngine = engine, AssemblyPath = assembly, GameReference = gameReference, OutputDirectory = outDir };
        return (task.Execute(), engine, task);
    }

    /// <summary>The fixture mod's stamp, made by the task against variant stamp-a.</summary>
    public static string ModStamp()
    {
        var dir = NewDir();
        var (ok, engine, task) = Run(ModDll, Variant("a"), dir);
        Assert.True(ok, string.Join("\n", engine.Errors.Select(e => e.Message)));
        Assert.Equal(Path.Combine(dir, Kiln.Sdk.KilnGameTypes.FileName), task.StampFile);
        return File.ReadAllText(task.StampFile);
    }

    public static Guid Mvid(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }

    /// <summary>The layout table embedded in a Kiln.Game file (read in a context of its own, then unloaded).</summary>
    public static string Table(string gameDll)
    {
        var ctx = new AssemblyLoadContext("stamp-table", isCollectible: true);
        try
        {
            using var s = ctx.LoadFromAssemblyPath(gameDll).GetManifestResourceStream("Kiln.Game.layouts")!;
            return new StreamReader(s).ReadToEnd();
        }
        finally { ctx.Unload(); }
    }

    /// <summary>The table line of a type, split at its tabs.</summary>
    public static string[] Line(string table, string fullName) =>
        table.Split('\n').Select(l => l.Split('\t')).Single(p => p.Length > 2 && p[1] == fullName);
}

public class StampTaskTests
{
    [Fact]
    public void The_stamp_lists_exactly_the_types_fields_and_values_the_fixture_mod_names()
    {
        var text = Stamps.ModStamp();
        var table = Stamps.Table(Stamps.Variant("a"));
        var set = GameLayoutStampsTests.Header(table)["set"];
        string L(string t) => Stamps.Line(table, t)[2];
        // the whole file, byte for byte: deterministic, sorted, the table's type strings
        var expected = $$"""
            {
              "schema": 1,
              "assembly": "StampMod.dll",
              "mvid": "{{Stamps.Mvid(Stamps.ModDll):D}}",
              "layoutSet": "{{set}}",
              "types": {
                "Kiln.Ecs+Glimmer_Mode": {
                  "layout": "{{L("Kiln.Ecs+Glimmer_Mode")}}",
                  "values": {
                    "Off": 0,
                    "Dim": 1,
                    "Bright": 2
                  }
                },
                "Kiln.Ecs+Glimmer_ModeMask": {
                  "layout": "{{L("Kiln.Ecs+Glimmer_ModeMask")}}",
                  "values": {
                    "Off": 1,
                    "Dim": 2,
                    "Bright": 4
                  }
                },
                "Kiln.Ecs+Glow": {
                  "layout": "{{L("Kiln.Ecs+Glow")}}",
                  "role": "component",
                  "attribute": "float",
                  "fields": {
                    "get_Values": "Span<float>"
                  }
                },
                "Kiln.Ecs+Lamp": {
                  "layout": "{{L("Kiln.Ecs+Lamp")}}",
                  "role": "component",
                  "attribute": null,
                  "fields": {
                    "core": "Ecs.LampCore",
                    "flags": "Ecs.Glimmer_ModeMask",
                    "glow": "float",
                    "mode": "Ecs.Glimmer_Mode",
                    "wick": "Ecs.WickId"
                  }
                },
                "Kiln.Ecs+LampCore": {
                  "layout": "{{L("Kiln.Ecs+LampCore")}}",
                  "role": "struct",
                  "attribute": null,
                  "fields": {
                    "heat": "float",
                    "ticks": "uint"
                  }
                },
                "Kiln.Ecs+WickId": {
                  "layout": "{{L("Kiln.Ecs+WickId")}}",
                  "role": "struct",
                  "attribute": null,
                  "fields": {
                    "op_Implicit": "uint"
                  }
                }
              }
            }

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), text);
        // what the fixture never names is not in it: Lamp.spare, Torch, Glimmer_Hue, the bases Component and
        // AttributeComponent, Ecs itself; the fields behind the methods it calls (WickId.id, Glow.dataStorage)
        Assert.DoesNotContain("spare", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dataStorage", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Torch", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hue", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Component", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enum_constant_folded_to_a_number_is_not_stamped_or_checked()
    {
        using var pe = new PEReader(File.OpenRead(Stamps.ModDll));
        var md = pe.GetMetadataReader();
        Assert.DoesNotContain(md.TypeReferences, h => md.StringComparer.Equals(md.GetTypeReference(h).Name, "Glimmer_Hue"));
        var stamp = Stamps.ModStamp();
        Assert.DoesNotContain("Glimmer_Hue", stamp, StringComparison.Ordinal);
        var table = Stamps.Table(Stamps.Variant("a"));
        var set = GameLayoutStampsTests.Header(table)["set"];
        var changed = table.Replace("set=" + set, "set=ffffffffffffffff", StringComparison.Ordinal)
            .Replace(Stamps.Line(table, "Kiln.Ecs+Glimmer_Hue")[2], "00000000000000ff", StringComparison.Ordinal)
            .Replace("Blue=1", "Blue=7", StringComparison.Ordinal);
        Assert.NotEqual(table, changed);
        Assert.True(GameLayoutStamps.Check(stamp, changed, out var reason), reason);
    }

    [Fact]
    public void Enum_types_in_fields_locals_and_ldtoken_are_stamped_without_generated_member_access()
    {
        var assembly = GenRepo.Built("stamp", "EnumMetadata", "EnumMetadata.dll");
        var dir = Stamps.NewDir();
        var (ok, engine, task) = Stamps.Run(assembly, Stamps.Variant("a"), dir);
        Assert.True(ok, string.Join("\n", engine.Errors.Select(e => e.Message)));
        using var stamp = JsonDocument.Parse(File.ReadAllText(task.StampFile));
        var types = stamp.RootElement.GetProperty("types");
        Assert.Equal(["Kiln.Ecs+Glimmer_Hue", "Kiln.Ecs+Glimmer_Mode", "Kiln.Ecs+Glimmer_ModeMask"],
            types.EnumerateObject().Select(t => t.Name));
        foreach (var type in types.EnumerateObject())
            Assert.NotEmpty(type.Value.GetProperty("values").EnumerateObject());
        var table = Stamps.Table(Stamps.Variant("a"));
        var changed = table.Replace("set=" + GameLayoutStampsTests.Header(table)["set"], "set=ffffffffffffffff", StringComparison.Ordinal)
            .Replace(Stamps.Line(table, "Kiln.Ecs+Glimmer_Hue")[2], "00000000000000ff", StringComparison.Ordinal)
            .Replace("Blue=1", "Blue=7", StringComparison.Ordinal);
        Assert.False(GameLayoutStamps.Check(stamp.RootElement.GetRawText(), changed, out var reason));
        Assert.Contains("Kiln.Ecs+Glimmer_Hue.Blue changed from 1 to 7", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stamp_passes_against_the_Kiln_Game_it_was_made_from()
    {
        var stamp = Stamps.ModStamp();
        var ctx = new AssemblyLoadContext("stamp-check-a", isCollectible: true);
        try
        {
            var check = ctx.LoadFromAssemblyPath(Stamps.Variant("a")).GetType("Kiln.GameLayoutStamps", throwOnError: true)!
                .GetMethod("Check", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string).MakeByRefType()])!;
            var args = new object?[] { stamp, null };
            Assert.True((bool)check.Invoke(null, args)!, (string?)args[1]);
        }
        finally { ctx.Unload(); }
    }

    [Fact]
    public void An_assembly_that_names_no_generated_type_gets_no_file()
    {
        // Kiln.Api references no Kiln.Game at all
        var dir = Stamps.NewDir();
        var (ok, engine, task) = Stamps.Run(typeof(Kiln.Mod).Assembly.Location, GenRepo.GameAssembly.Location, dir);
        Assert.True(ok);
        Assert.Empty(engine.Warnings);
        Assert.Equal("", task.StampFile);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        // the legacy consumer references Kiln.Game, but only the nine dev.24 types of Kiln.GameTypes: not stamped
        (ok, engine, task) = Stamps.Run(GenRepo.Built("legacy", "Consumer", "Kiln.Game.LegacyConsumer.dll"), GenRepo.GameAssembly.Location, dir);
        Assert.True(ok);
        Assert.Empty(engine.Warnings);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public void A_stale_stamp_is_removed()
    {
        var dir = Stamps.NewDir();
        var stale = Path.Combine(dir, Kiln.Sdk.KilnGameTypes.FileName);
        File.WriteAllText(stale, "{ \"schema\": 1 }");
        var (ok, _, _) = Stamps.Run(typeof(Kiln.Mod).Assembly.Location, GenRepo.GameAssembly.Location, dir);
        Assert.True(ok);
        Assert.False(File.Exists(stale));
        // no Kiln.Game reference at all (the targets pass none): the same
        File.WriteAllText(stale, "{ \"schema\": 1 }");
        (ok, _, _) = Stamps.Run(Stamps.ModDll, "", dir);
        Assert.True(ok);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_stamp_is_replaced_and_an_unchanged_one_is_not_rewritten()
    {
        var dir = Stamps.NewDir();
        var file = Path.Combine(dir, Kiln.Sdk.KilnGameTypes.FileName);
        File.WriteAllText(file, "{ \"schema\": 1, \"mvid\": \"00000000-0000-0000-0000-000000000000\" }");
        Assert.True(Stamps.Run(Stamps.ModDll, Stamps.Variant("a"), dir).Ok);
        Assert.Contains(Stamps.Mvid(Stamps.ModDll).ToString("D"), File.ReadAllText(file), StringComparison.Ordinal);
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, when);
        Assert.True(Stamps.Run(Stamps.ModDll, Stamps.Variant("a"), dir).Ok);
        Assert.Equal(when, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_Kiln_Game_without_a_layout_table_is_a_warning_and_no_file()
    {
        // the dev.24 shape of Kiln.Game has no embedded table
        var dir = Stamps.NewDir();
        var (ok, engine, task) = Stamps.Run(Stamps.ModDll, GenRepo.Built("legacy", "Dev24Shape", "Kiln.Game.dll"), dir);
        Assert.True(ok);
        Assert.Empty(engine.Errors);
        var w = Assert.Single(engine.Warnings);
        Assert.Equal("KILN020", w.Code);
        Assert.Contains("no layout table", w.Message, StringComparison.Ordinal);
        Assert.Equal("", task.StampFile);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public void An_unreadable_mod_assembly_is_an_error()
    {
        var dir = Stamps.NewDir();
        var bad = Path.Combine(dir, "NotAnAssembly.dll");
        File.WriteAllText(bad, "not a PE file");
        var (ok, engine, _) = Stamps.Run(bad, Stamps.Variant("a"), dir);
        Assert.False(ok);
        Assert.Equal("KILN021", Assert.Single(engine.Errors).Code);
    }

    [Fact]
    public void The_stamp_is_valid_json_with_the_fields_GameLayoutStamps_reads()
    {
        using var doc = JsonDocument.Parse(Stamps.ModStamp());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(["schema", "assembly", "mvid", "layoutSet", "types"], root.EnumerateObject().Select(p => p.Name));
        foreach (var t in root.GetProperty("types").EnumerateObject())
            Assert.True(t.Value.TryGetProperty("fields", out _) ^ t.Value.TryGetProperty("values", out _), t.Name);
    }
}
