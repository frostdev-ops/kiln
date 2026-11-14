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
/// Both get <c>"kilnBuild"</c>: the Kiln build of the Kiln.Api the mod compiled against (<see cref="KilnApiPath"/>), so Kiln.Host and
/// the launcher can say when a game's runtime is older than the mod.
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
    /// <summary>The mod's blocks/ folder: what its data files ask of players' games (<see cref="DataClients"/>; the other data folders are beside it).</summary>
    public string BlocksDirectory { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Description { get; set; } = "";
    public string License { get; set; } = "";
    public string Url { get; set; } = "";
    [Required] public string EntryAssembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    /// <summary><c>true</c> or <c>required</c>: <c>"boot"</c> (the mod has boot content, Mod.OnBoot); empty or <c>false</c>: none.
    /// Another value is written as it is, so the loader's own parse names it (KILN001).</summary>
    public string Boot { get; set; } = "";
    public string Advanced { get; set; } = "";
    public string Features { get; set; } = "";
    public string Exports { get; set; } = "";
    public ITaskItem[] Dependencies { get; set; } = [];
    public ITaskItem[] LoadBefore { get; set; } = [];
    public ITaskItem[] LoadAfter { get; set; } = [];
    public ITaskItem[] Incompatible { get; set; } = [];
    public string TestsAutoRun { get; set; } = "";
    public string TestTimeoutSeconds { get; set; } = "";
    /// <summary><c>"updates"</c>: where new versions are published. <c>github</c> or <c>nexus</c>; empty: github when
    /// <see cref="UpdatesRepository"/> is set, nexus when <see cref="UpdatesNexusModId"/> is, else no <c>"updates"</c>.</summary>
    public string UpdatesProvider { get; set; } = "";
    /// <summary>GitHub: <c>owner/repo</c>.</summary>
    public string UpdatesRepository { get; set; } = "";
    /// <summary>GitHub: the release tag with <c>{version}</c> once (default <c>v{version}</c>).</summary>
    public string UpdatesTagPattern { get; set; } = "";
    /// <summary>GitHub: the zip's file name in a release, with <c>{version}</c> at most once.</summary>
    public string UpdatesAsset { get; set; } = "";
    /// <summary>GitHub: <c>stable</c> (default) or <c>prerelease</c>.</summary>
    public string UpdatesChannel { get; set; } = "";
    /// <summary>Nexus Mods: the number of the mod's page.</summary>
    public string UpdatesNexusModId { get; set; } = "";
    /// <summary>Nexus Mods: the id of the file whose versions are followed.</summary>
    public string UpdatesNexusFileChainId { get; set; } = "";
    /// <summary>The Kiln.Api assembly the mod compiled against (its reference): its Kiln.Build metadata becomes <c>"kilnBuild"</c>.</summary>
    public string KilnApiPath { get; set; } = "";

    [Output] public string ModId { get; private set; } = "";
    [Output] public string ModVersion { get; private set; } = "";

    public override bool Execute()
    {
        var own = Source.Length > 0 && File.Exists(Source);
        string json;
        var stamp = StampOf(KilnApiPath);
        try
        {
            json = own ? WithStamp(File.ReadAllText(Source), stamp) : Generate(stamp);
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
        if (!manifest.HasCode)
        {
            // a manifest without entryAssembly is a mod of data only; a project that builds code must name its assembly
            Log.LogError(null, "KILN001", null, own ? Source : null, 0, 0, 0, 0,
                $"kiln.mod.json: 'entryAssembly' is required (the project builds '{EntryAssembly}')");
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

    // Kiln.Api's [assembly: AssemblyMetadata("Kiln.Build", ...)] and ("Kiln.BuildCreated", ...) (managed/Directory.Build.targets).
    // None: a Kiln.Api built without git, or no reference found; the manifest then has no stamp and nothing is compared.
    private KilnBuild? StampOf(string path)
    {
        path = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (path.Length == 0 || !File.Exists(path)) return null;
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            string? version = null, created = null;
            foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(h);
                if (ca.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) continue;
                var parent = md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)ca.Constructor).Parent;
                if (parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference ||
                    md.GetString(md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)parent).Name) != "AssemblyMetadataAttribute") continue;
                var blob = md.GetBlobReader(ca.Value);
                if (blob.ReadUInt16() != 1) continue;  // the custom attribute prolog
                var key = blob.ReadSerializedString();
                var value = blob.ReadSerializedString();
                if (key == KilnBuild.VersionKey) version = value;
                else if (key == KilnBuild.CreatedKey) created = value;
            }
            return KilnBuild.From(version, created);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException)
        {
            Log.LogMessage(MessageImportance.Low, $"kiln.mod.json: cannot read {path}: {e.Message}");
            return null;
        }
    }

    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string StampJson(KilnBuild stamp) =>
        $"{{ \"version\": {JsonSerializer.Serialize(stamp.Version, Plain)}" +
        (stamp.Created is { } c ? $", \"created\": {JsonSerializer.Serialize(c, Plain)}" : "") + " }";

    // A project's own kiln.mod.json, with the stamp as its first key (its text otherwise as written).
    private string WithStamp(string text, KilnBuild? stamp)
    {
        if (stamp == null) return text;
        try
        {
            if (Manifest.Parse(text).BuiltWith is { } theirs)
            {
                if (theirs != stamp)
                    Log.LogWarning(null, "KILN003", null, Source, 0, 0, 0, 0,
                        $"kiln.mod.json says kilnBuild {theirs.Version}, but this build compiles against Kiln {stamp.Version}: leave kilnBuild out, the build writes it");
                return text;
            }
        }
        catch (InvalidDataException)
        {
            return text;  // the error comes from the validation that follows
        }
        var at = text.IndexOf('{');
        return at < 0 ? text : text[..(at + 1)] + $"\n  \"kilnBuild\": {StampJson(stamp)}," + text[(at + 1)..];
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

    // What the mod's data files ask for (DataClients): blocks/*.json beside the project, and the other data folders there.
    private string? ClientsOfData()
    {
        if (BlocksDirectory.Length == 0) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(BlocksDirectory.TrimEnd('/', '\\')));
        return dir == null ? null : DataClients.OfFolder(dir, text => Log.LogMessage(MessageImportance.Low, $"kiln.mod.json: {text}"));
    }

    // The higher of what the code and the data files ask for.
    private string? ClientsNeeded() => DataClients.Max(ClientsOfAssembly(), ClientsOfData());

    private static List<string> List(string value) =>
        value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Flag(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private string Generate(KilnBuild? stamp)
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
            var boot = Boot.Trim();
            if (boot.Equals("true", StringComparison.OrdinalIgnoreCase)) w.WriteBoolean("boot", true);
            else if (boot.Length > 0 && !boot.Equals("false", StringComparison.OrdinalIgnoreCase)) w.WriteString("boot", boot.ToLowerInvariant());
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
            WriteUpdates(w);
            if (stamp != null)
            {
                w.WriteStartObject("kilnBuild");
                w.WriteString("version", stamp.Version);
                if (stamp.Created != null) w.WriteString("created", stamp.Created);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    // "updates" from the KilnUpdates* properties, written as they are: Manifest.Parse reads the result, and what it
    // cannot read comes back as a build warning (KILN003) naming the property's key.
    private void WriteUpdates(Utf8JsonWriter w)
    {
        var provider = UpdatesProvider.Trim();
        if (provider.Length == 0)
            provider = UpdatesRepository.Trim().Length > 0 ? "github" : UpdatesNexusModId.Trim().Length > 0 ? "nexus" : "";
        if (provider.Length == 0) return;
        w.WriteStartObject("updates");
        w.WriteString("provider", provider);
        if (provider == "nexus")
        {
            w.WriteNumber("gameId", 5811);
            // a number when it is one; else the text, so the reader names what is wrong with it
            if (long.TryParse(UpdatesNexusModId.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mod))
                w.WriteNumber("modId", mod);
            else w.WriteString("modId", UpdatesNexusModId.Trim());
            w.WriteString("fileChainId", UpdatesNexusFileChainId.Trim());
        }
        else
        {
            w.WriteString("repository", UpdatesRepository.Trim());
            if (UpdatesTagPattern.Trim().Length > 0) w.WriteString("tagPattern", UpdatesTagPattern.Trim());
            if (UpdatesAsset.Trim().Length > 0) w.WriteString("asset", UpdatesAsset.Trim());
            if (UpdatesChannel.Trim().Length > 0) w.WriteString("channel", UpdatesChannel.Trim());
        }
        w.WriteEndObject();
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
