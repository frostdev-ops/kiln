// SPDX-License-Identifier: MIT
// The SDK's pieces against each other: BlockJson's sections and the block records they make, against schema/kiln-blocks.schema.json.
// (DataClients against BlockJson: Kiln.Analyzers.Tests DataClientsTests. Lint's section lists against the schema: kiln-manage lint/sections_test.rs.)
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Kiln.Blocks.Tests;

public sealed class SchemaDriftTests
{
    // Top-level schema properties that are no section of blocks.
    private static readonly HashSet<string> NotSections = ["$schema", "format", "comment"];

    // Intentional differences between a record and its schema entry, "section.field". Keep this short; say why for each.
    private static readonly HashSet<string> Allowed =
    [
        // PropBlock.Collider is inherited, but a station and furniture keep the collision they start from: the compiler refuses it (world-blocks.md), so the schema leaves it out
        "stations.collider", "containers.collider", "doors.collider", "beds.collider", "seats.collider",
        "stations.colliders", "containers.colliders", "doors.colliders", "beds.colliders", "seats.colliders",
    ];

    private static string Repo([CallerFilePath] string self = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(self) ?? "" })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "schema/kiln-blocks.schema.json"))) return d.FullName;
        throw new DirectoryNotFoundException("schema/kiln-blocks.schema.json not found");
    }

    private static readonly JsonDocument Schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "schema/kiln-blocks.schema.json")));

    // Every section of blocks/*.json and the block type it makes (as DataClientsTests reads BlockJson.Kinds).
    private static List<(string Section, Type Type)> Sections()
    {
        var kinds = (Array)typeof(IBlock).Assembly.GetType("Kiln.BlockJson", throwOnError: true)!.GetField("Kinds", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var list = new List<(string, Type)>();
        foreach (ITuple k in kinds)
        {
            var make = ((Delegate)k[1]!).Method;
            var type = make.IsGenericMethod ? make.GetGenericArguments()[0]
                : make.Name switch { "MakeVfx" => typeof(VfxBlock), "MakeMaterial" => typeof(MaterialBlock), _ => throw new InvalidOperationException($"{k[0]}: {make.Name} makes a type this test does not know") };
            list.Add(((string)k[0]!, type));
        }
        return list;
    }

    private static readonly Dictionary<string, JsonDocument> Docs = new() { ["kiln-blocks.schema.json"] = Schema };

    // Follows $ref, also into a sibling schema file ("kiln-vfx.schema.json#/$defs/section"): the entry and the file it lives in.
    private static (JsonElement, string) Def(JsonElement e, string file)
    {
        while (e.TryGetProperty("$ref", out var r))
        {
            var path = r.GetString()!;
            var hash = path.IndexOf('#');
            if (hash > 0) file = path[..hash];
            if (!Docs.TryGetValue(file, out var doc)) Docs[file] = doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "schema", file)));
            e = doc.RootElement;
            foreach (var part in path[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries)) e = e.GetProperty(part);
        }
        return (e, file);
    }

    // The schema entry of one block of a section: its additionalProperties (after any $ref to the section itself).
    private static (JsonElement, string) BlockEntry(string section)
    {
        var (entry, file) = Def(Schema.RootElement.GetProperty("properties").GetProperty(section), "kiln-blocks.schema.json");
        Assert.True(entry.TryGetProperty("additionalProperties", out var block), $"{section}: the schema names no block entry (additionalProperties)");
        return (block, file);
    }

    // The property names a schema entry allows: its own, and those of every $ref, allOf, anyOf and oneOf branch that is an object.
    private static void Names(JsonElement e, string file, HashSet<string> into, int depth = 0)
    {
        if (depth > 8) return;
        (e, file) = Def(e, file);
        if (e.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject()) into.Add(p.Name);
        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
            if (e.TryGetProperty(key, out var list))
                foreach (var branch in list.EnumerateArray()) Names(branch, file, into, depth + 1);
    }

    private static HashSet<string> SchemaNames(string section)
    {
        var (block, file) = BlockEntry(section);
        var names = new HashSet<string>();
        Names(block, file, names);
        return names;
    }

    // The JSON fields a record reads: public settable properties, camelCase or [JsonPropertyName], minus [JsonIgnore].
    private static HashSet<string> Fields(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            // a premade's Key is its name among the game's premades (a "like" or part reference), not a field a data block writes
            .Where(p => !(p.Name == "Key" && typeof(IPremade).IsAssignableFrom(t)))
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet();

    [Fact]
    public void Sections_are_the_schemas()
    {
        var code = Sections().Select(s => s.Section).ToHashSet();
        var schema = Schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).Where(n => !NotSections.Contains(n)).ToHashSet();
        Assert.True(code.SetEquals(schema),
            $"only in BlockJson.Kinds: [{string.Join(", ", code.Except(schema))}]; only in the schema: [{string.Join(", ", schema.Except(code))}]");
    }

    [Fact]
    public void Every_record_field_is_in_the_schema_and_back()
    {
        var problems = new List<string>();
        foreach (var (section, type) in Sections())
        {
            if (!Schema.RootElement.GetProperty("properties").TryGetProperty(section, out _)) continue;   // Sections_are_the_schemas says so
            var schema = SchemaNames(section);
            var fields = Fields(type);
            foreach (var f in fields.Except(schema).Order(StringComparer.Ordinal))
                if (!Allowed.Contains($"{section}.{f}") && f != "comment")
                    problems.Add($"{section}.{f}: {type.Name} reads it, the schema does not list it");
            foreach (var f in schema.Except(fields).Order(StringComparer.Ordinal))
                if (!Allowed.Contains($"{section}.{f}") && f != "comment")
                    problems.Add($"{section}.{f}: the schema lists it, {type.Name} has no such field");
        }
        Assert.True(problems.Count == 0, $"{problems.Count} differences between the block records and schema/kiln-blocks.schema.json:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void The_allowlist_has_no_stale_entries()
    {
        var sections = Sections().ToDictionary(s => s.Section, s => s.Type);
        foreach (var a in Allowed)
        {
            var (section, field) = (a[..a.IndexOf('.')], a[(a.IndexOf('.') + 1)..]);
            Assert.True(sections.TryGetValue(section, out var type), $"allowlist: no section {section}");
            var schema = SchemaNames(section);
            Assert.True(Fields(type).Contains(field) != schema.Contains(field), $"allowlist: {a} is no longer a difference");
        }
    }
}
