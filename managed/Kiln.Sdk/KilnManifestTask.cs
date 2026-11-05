// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using Kiln.Host;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Kiln.Sdk;

/// <summary>
/// Writes <see cref="Output"/> (obj/kiln.mod.json): generated from the project's properties and items, or, when the
/// project has its own <see cref="Source"/> kiln.mod.json, that file after validation. Either way the result is
/// parsed with the loader's own <c>Manifest.Parse</c>, so a manifest that builds is one Kiln accepts.
/// Errors: KILN001 invalid manifest, KILN002 entry assembly mismatch. Warnings: KILN003.
/// </summary>
public sealed class KilnManifest : Microsoft.Build.Utilities.Task
{
    [Required] public string Output { get; set; } = "";
    public string Source { get; set; } = "";
    [Required] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [Required] public string Version { get; set; } = "";
    [Required] public string ApiVersion { get; set; } = "";
    public string Side { get; set; } = "server";
    public string Clients { get; set; } = "";
    /// <summary>Used when neither <see cref="Clients"/> nor the assembly says.</summary>
    public string ClientsFallback { get; set; } = "";
    /// <summary>The built assembly: what its blocks ask of players' games is in its metadata (written at compile time).</summary>
    public string AssemblyPath { get; set; } = "";
    /// <summary>The mod's blocks/ folder: a new VFX in a data file asks for <c>mod</c>, as one in code does.</summary>
    public string BlocksDirectory { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Description { get; set; } = "";
    public string License { get; set; } = "";
    public string Url { get; set; } = "";
    [Required] public string EntryAssembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Advanced { get; set; } = "";
    public string Features { get; set; } = "";
    public string Exports { get; set; } = "";
    public ITaskItem[] Dependencies { get; set; } = [];
    public ITaskItem[] LoadBefore { get; set; } = [];
    public ITaskItem[] LoadAfter { get; set; } = [];
    public ITaskItem[] Incompatible { get; set; } = [];
    public string TestsAutoRun { get; set; } = "";
    public string TestTimeoutSeconds { get; set; } = "";

    [Output] public string ModId { get; private set; } = "";
    [Output] public string ModVersion { get; private set; } = "";

    public override bool Execute()
    {
        var own = Source.Length > 0 && File.Exists(Source);
        string json;
        try
        {
            json = own ? File.ReadAllText(Source) : Generate();
        }
        catch (IOException e)
        {
            Log.LogError(null, "KILN001", null, Source, 0, 0, 0, 0, $"cannot read {Source}: {e.Message}");
            return false;
        }
        Manifest manifest;
        try
        {
            manifest = Manifest.Parse(json);
        }
        catch (InvalidDataException e)
        {
            if (own) Log.LogError(null, "KILN001", null, Source, 0, 0, 0, 0, e.Message);
            else Log.LogError(null, "KILN001", null, null, 0, 0, 0, 0, $"{e.Message} (from the project's Kiln properties)");
            return false;
        }
        if (!string.Equals(manifest.EntryAssembly, EntryAssembly, StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError(null, "KILN002", null, own ? Source : null, 0, 0, 0, 0,
                $"kiln.mod.json names entryAssembly '{manifest.EntryAssembly}' but the project builds '{EntryAssembly}'");
            return false;
        }
        if (own && manifest.Id != Id)
            Log.LogWarning(null, "KILN003", null, Source, 0, 0, 0, 0, $"kiln.mod.json has id '{manifest.Id}'; KilnModId is '{Id}' (the file wins)");
        foreach (var w in manifest.Warnings) Log.LogWarning(null, "KILN003", null, own ? Source : null, 0, 0, 0, 0, w);
        if (!SemVersion.TryParse(manifest.Version, out var v) || manifest.Version.Split('-')[0].Split('.').Length != 3)
            Log.LogWarning(null, "KILN003", null, own ? Source : null, 0, 0, 0, 0,
                $"version '{manifest.Version}' is not a full semantic version (MAJOR.MINOR.PATCH); Kiln reads it as {v}");

        ModId = manifest.Id;
        ModVersion = manifest.Version;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Output))!);
        if (!File.Exists(Output) || File.ReadAllText(Output) != json) File.WriteAllText(Output, json);
        Log.LogMessage(MessageImportance.Low, $"kiln.mod.json: {manifest.Id} {manifest.Version} ({(own ? "validated" : "generated")})");
        return true;
    }

    // [assembly: AssemblyMetadata("Kiln.Clients", "kiln")], from Kiln.Analyzers. The manifest is written after the assembly is built.
    private string? ClientsOfAssembly()
    {
        if (AssemblyPath.Length == 0 || !File.Exists(AssemblyPath)) return null;
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(AssemblyPath));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var value = md.GetBlobBytes(md.GetCustomAttribute(h).Value);
                var text = Encoding.UTF8.GetString(value);
                var at = text.IndexOf("Kiln.Clients", StringComparison.Ordinal);
                if (at < 0) continue;
                foreach (var level in new[] { "none", "kiln", "mod" })
                    if (text.IndexOf(level, at + 12, StringComparison.Ordinal) >= 0) return level;
            }
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException)
        {
            Log.LogMessage(MessageImportance.Low, $"kiln.mod.json: cannot read {AssemblyPath}: {e.Message}");
        }
        return null;
    }

    // What the data files ask for: a "vfx" section, or a "vfx" field that names a new VFX. The rest of what they can ask for is kiln lint's (docs/kiln/modding/lint.md).
    private string? ClientsOfBlocks()
    {
        if (BlocksDirectory.Length == 0 || !Directory.Exists(BlocksDirectory)) return null;
        foreach (var file in Directory.GetFiles(BlocksDirectory, "*.json"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (AsksForMod(doc.RootElement, top: true)) return "mod";
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                Log.LogMessage(MessageImportance.Low, $"kiln.mod.json: cannot read {file}: {e.Message}");
            }
        }
        return null;
    }

    private static bool AsksForMod(System.Text.Json.JsonElement e, bool top)
    {
        if (e.ValueKind == System.Text.Json.JsonValueKind.Array) return e.EnumerateArray().Any(x => AsksForMod(x, false));
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name != "vfx")
            {
                if (AsksForMod(p.Value, false)) return true;
                continue;
            }
            if (top) return p.Value.ValueKind == System.Text.Json.JsonValueKind.Object && p.Value.EnumerateObject().Any();
            var name = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString()
                : p.Value.ValueKind == System.Text.Json.JsonValueKind.Object && p.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name != null && !name.StartsWith("game:", StringComparison.Ordinal) && !Guid.TryParse(name, out _)) return true;
        }
        return false;
    }

    // The higher of what the code and the data files ask for.
    private string? ClientsNeeded()
    {
        var code = ClientsOfAssembly();
        var data = ClientsOfBlocks();
        return data ?? code;
    }

    private static List<string> List(string value) =>
        value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Flag(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private string Generate()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("id", Id);
            w.WriteString("name", Name.Length > 0 ? Name : Id);
            w.WriteString("version", Version);
            if (Description.Length > 0) w.WriteString("description", Description);
            var authors = List(Authors);
            if (authors.Count > 0)
            {
                w.WriteStartArray("authors");
                foreach (var a in authors) w.WriteStringValue(a);
                w.WriteEndArray();
            }
            if (License.Length > 0) w.WriteString("license", License);
            if (Url.Length > 0) w.WriteString("url", Url);
            w.WriteString("apiVersion", ApiVersion);
            w.WriteString("side", Side.Length > 0 ? Side : "server");
            var clients = Clients.Trim().Length > 0 ? Clients.Trim() : ClientsNeeded() ?? ClientsFallback.Trim();
            if (clients.Length > 0) w.WriteString("clients", clients);
            w.WriteString("entryAssembly", EntryAssembly);
            if (EntryType.Length > 0) w.WriteString("entryType", EntryType);
            if (Dependencies.Length > 0)
            {
                w.WriteStartObject("dependencies");
                foreach (var d in Dependencies)
                {
                    var range = d.GetMetadata("Version") is { Length: > 0 } r ? r : "*";
                    if (Flag(d.GetMetadata("Optional")))
                    {
                        w.WriteStartObject(d.ItemSpec);
                        w.WriteString("version", range);
                        w.WriteBoolean("optional", true);
                        w.WriteEndObject();
                    }
                    else w.WriteString(d.ItemSpec, range);
                }
                w.WriteEndObject();
            }
            WriteIds(w, "loadBefore", LoadBefore);
            WriteIds(w, "loadAfter", LoadAfter);
            if (Incompatible.Length > 0)
            {
                w.WriteStartObject("incompatible");
                foreach (var x in Incompatible) w.WriteString(x.ItemSpec, x.GetMetadata("Version") is { Length: > 0 } r ? r : "*");
                w.WriteEndObject();
            }
            WriteStrings(w, "features", List(Features));
            WriteStrings(w, "exports", List(Exports));
            if (Flag(Advanced)) w.WriteBoolean("advanced", true);
            if (Flag(TestsAutoRun) || TestTimeoutSeconds.Trim().Length > 0)
            {
                w.WriteStartObject("tests");
                w.WriteBoolean("autoRun", Flag(TestsAutoRun));
                if (int.TryParse(TestTimeoutSeconds, out var t) && t > 0) w.WriteNumber("timeoutSeconds", t);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void WriteIds(Utf8JsonWriter w, string name, ITaskItem[] items) =>
        WriteStrings(w, name, items.Select(i => i.ItemSpec).ToList());

    private static void WriteStrings(Utf8JsonWriter w, string name, List<string> values)
    {
        if (values.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }
}
