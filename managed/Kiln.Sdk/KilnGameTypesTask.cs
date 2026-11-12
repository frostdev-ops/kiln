// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Kiln.Sdk;

/// <summary>
/// Writes the mod's layout stamp, <c>kiln.gametypes.json</c> in <see cref="OutputDirectory"/>: for every generated game
/// type of <c>Kiln.Game</c> the built mod references (<c>Kiln.Ecs+Lamp</c>), the layout hash it was compiled against,
/// the fields it names with their C# types, each named struct's role and attribute element type (or null), and for an
/// enum or bitmask all its values. Kiln.Host compares it with the
/// loaded <c>Kiln.Game</c> before the mod loads (<c>GameLayoutStamps.Check</c>, docs/kiln/typegen.md §3.7).
/// Both assemblies are read with System.Reflection.Metadata and never loaded. The layouts come from the table embedded
/// in <see cref="GameReference"/> (resource <c>Kiln.Game.layouts</c>); a type that is not in it (the nine legacy types
/// of <c>Kiln.GameTypes</c>, <c>Ecs.StaticArray&lt;N&gt;</c>, <c>Ecs.ObjectReference</c>, <c>Ecs.GameGuid</c>) is not
/// stamped. Two kinds of generated methods are stamped like fields, when the mod calls them: a single-field wrapper's
/// implicit conversions (<c>op_Implicit</c>, with the wrapped type) and an attribute component's <c>Values</c>
/// (<c>get_Values</c>, with its <c>Span&lt;T&gt;</c> type). No generated type referenced: no file, and a stale one is deleted.
/// Warning KILN020: the reference has no layout table (an older Kiln.Game). Error KILN021: an assembly is unreadable.
/// </summary>
public sealed class KilnGameTypes : Microsoft.Build.Utilities.Task
{
    /// <summary>The stamp's file name, beside the mod's entry assembly.</summary>
    public const string FileName = "kiln.gametypes.json";

    /// <summary>The name of the layout table resource in Kiln.Game.dll.</summary>
    public const string TableResource = "Kiln.Game.layouts";

    /// <summary>The built mod assembly.</summary>
    [Required] public string AssemblyPath { get; set; } = "";

    /// <summary>The Kiln.Game.dll the mod compiled against. Empty: the mod does not reference Kiln.Game (no file).</summary>
    public string GameReference { get; set; } = "";

    /// <summary>The mod's output folder.</summary>
    [Required] public string OutputDirectory { get; set; } = "";

    /// <summary>The stamp written, or empty when there is none.</summary>
    [Output] public string StampFile { get; private set; } = "";

    public override bool Execute()
    {
        var target = Path.Combine(OutputDirectory, FileName);
        if (GameReference.Length == 0) return NoStamp(target);
        if (!File.Exists(GameReference))
        {
            Log.LogWarning(null, "KILN020", null, null, 0, 0, 0, 0, $"the Kiln.Game reference '{GameReference}' does not exist: no layout stamp ({FileName})");
            return NoStamp(target);
        }
        string? tableText;
        try
        {
            tableText = ReadTable(GameReference);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.LogError(null, "KILN021", null, GameReference, 0, 0, 0, 0, $"cannot read the Kiln.Game reference: {e.Message}");
            return false;
        }
        if (tableText is null)
        {
            Log.LogWarning(null, "KILN020", null, GameReference, 0, 0, 0, 0,
                $"this Kiln.Game has no layout table (resource {TableResource}; an older Kiln.Game): {Path.GetFileName(AssemblyPath)} gets no layout stamp ({FileName}), so Kiln cannot tell before it loads whether a game update broke it");
            return NoStamp(target);
        }
        var table = LayoutTable.Parse(tableText, out var tableError);
        if (table is null)
        {
            Log.LogError(null, "KILN021", null, GameReference, 0, 0, 0, 0, $"the Kiln.Game layout table is unreadable: {tableError}");
            return false;
        }
        Dictionary<string, SortedSet<string>> used;
        Guid mvid;
        try
        {
            used = References(AssemblyPath, table, out mvid);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.LogError(null, "KILN021", null, AssemblyPath, 0, 0, 0, 0, $"cannot read the mod assembly: {e.Message}");
            return false;
        }
        if (used.Count == 0) return NoStamp(target);
        var json = Write(Path.GetFileName(AssemblyPath), mvid, table, used);
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            if (!File.Exists(target) || File.ReadAllText(target) != json)
                File.WriteAllText(target, json, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "KILN021", null, target, 0, 0, 0, 0, $"cannot write the layout stamp: {e.Message}");
            return false;
        }
        StampFile = target;
        Log.LogMessage(MessageImportance.Normal, $"{Path.GetFileName(AssemblyPath)}: layout stamp {FileName} for {used.Count} generated Kiln.Game type(s) (layout set {table.Set})");
        return true;
    }

    private bool NoStamp(string target)
    {
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
                Log.LogMessage(MessageImportance.Normal, $"{Path.GetFileName(AssemblyPath)}: references no generated Kiln.Game type; removed the stale {FileName}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "KILN021", null, target, 0, 0, 0, 0, $"cannot remove the stale layout stamp: {e.Message}");
            return false;
        }
        return true;
    }

    /// <summary>The text of the embedded resource <see cref="TableResource"/>, or null when the assembly has none.</summary>
    internal static string? ReadTable(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        foreach (var h in md.ManifestResources)
        {
            var r = md.GetManifestResource(h);
            if (!md.StringComparer.Equals(r.Name, TableResource) || !r.Implementation.IsNil) continue;
            var dir = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
            if (dir.Size == 0) return null;
            var block = pe.GetSectionData(dir.RelativeVirtualAddress);
            var reader = block.GetReader();
            reader.Offset = checked((int)r.Offset);
            var length = reader.ReadInt32();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
        return null;
    }

    /// <summary>The generated types of <paramref name="table"/> that the assembly references, each with the names of
    /// the fields it references (by <c>MemberReference</c>).</summary>
    internal static Dictionary<string, SortedSet<string>> References(string path, LayoutTable table, out Guid mvid)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
        var names = new Dictionary<TypeReferenceHandle, string?>();
        string? FullName(TypeReferenceHandle h, int depth)
        {
            if (names.TryGetValue(h, out var known)) return known;
            var t = md.GetTypeReference(h);
            string? full = null;
            var name = md.GetString(t.Name);
            switch (t.ResolutionScope.Kind)
            {
                case HandleKind.AssemblyReference:
                    var a = md.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope);
                    if (md.StringComparer.Equals(a.Name, "Kiln.Game"))
                    {
                        var ns = md.GetString(t.Namespace);
                        full = ns.Length > 0 ? ns + "." + name : name;
                    }
                    break;
                case HandleKind.TypeReference when depth < 32:
                    // a nested type: through its enclosing reference
                    if (FullName((TypeReferenceHandle)t.ResolutionScope, depth + 1) is { } outer) full = outer + "+" + name;
                    break;
            }
            names[h] = full;
            return full;
        }
        var used = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var h in md.TypeReferences)
            if (FullName(h, 0) is { } full && table.Types.ContainsKey(full) && !used.ContainsKey(full))
                used[full] = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var h in md.MemberReferences)
        {
            var m = md.GetMemberReference(h);
            if (m.Parent.Kind != HandleKind.TypeReference) continue;
            if (FullName((TypeReferenceHandle)m.Parent, 0) is not { } full || !used.TryGetValue(full, out var fields)) continue;
            var sig = md.GetBlobReader(m.Signature);
            var kind = sig.ReadSignatureHeader().Kind;
            var name = md.GetString(m.Name);
            if (table.Types[full].Fields is not { } layout || !layout.ContainsKey(name)) continue;
            // instance fields of the layout (the generated statics Hash, Name, Layout, ... are not in the table), and
            // the generated methods the table lists as <name>@method:<type> (a wrapper's op_Implicit, an attribute
            // component's get_Values): a game update can take them away while the type stays
            var isMethod = table.Methods.Contains((full, name));
            if (kind == SignatureKind.Field ? !isMethod : kind == SignatureKind.Method && isMethod) fields.Add(name);
        }
        return used;
    }

    internal static string Write(string assembly, Guid mvid, LayoutTable table, Dictionary<string, SortedSet<string>> used)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("assembly", assembly);
            w.WriteString("mvid", mvid.ToString("D"));
            w.WriteString("layoutSet", table.Set);
            w.WriteStartObject("types");
            foreach (var name in used.Keys.Order(StringComparer.Ordinal))
            {
                var t = table.Types[name];
                w.WriteStartObject(name);
                w.WriteString("layout", t.Layout);
                if (t.Fields is { } fields)
                {
                    w.WriteString("role", t.Role);
                    w.WriteString("attribute", t.Attribute);
                    w.WriteStartObject("fields");
                    foreach (var f in used[name]) w.WriteString(f, fields[f]);
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteStartObject("values");
                    foreach (var (n, v) in t.Values!)
                    {
                        w.WritePropertyName(n);
                        w.WriteRawValue(v);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The layout table of a Kiln.Game (docs/kiln/typegen.md §3.6), as far as the stamp needs it.</summary>
    internal sealed class LayoutTable
    {
        public string Set = "";
        public readonly Dictionary<string, Entry> Types = new(StringComparer.Ordinal);
        /// <summary>The members of <see cref="Types"/> that are generated methods (<c>name@method:type</c>), not fields.</summary>
        public readonly HashSet<(string Type, string Member)> Methods = [];

        internal sealed class Entry
        {
            public required string Layout;
            public string? Role, Attribute;
            public Dictionary<string, string>? Fields;        // struct: name -> C# type, as in the table
            public List<(string Name, string Value)>? Values;  // enum or bitmask: in table order
        }

        public static LayoutTable? Parse(string text, out string? error)
        {
            error = null;
            var t = new LayoutTable();
            var header = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var p = line.Split('\t');
                if (!header)
                {
                    if (p.Length < 2 || p[0] != "kiln.game.layouts" || p[1] != "1")
                    {
                        error = "no 'kiln.game.layouts 1' header";
                        return null;
                    }
                    foreach (var kv in p.Skip(2))
                        if (kv.StartsWith("set=", StringComparison.Ordinal)) t.Set = kv[4..];
                    if (t.Set.Length == 0)
                    {
                        error = "the header has no set";
                        return null;
                    }
                    header = true;
                    continue;
                }
                if (p.Length < 6 || p[0] is not ("S" or "E" or "F"))
                {
                    error = "bad line: " + (line.Length > 80 ? line[..80] : line);
                    return null;
                }
                var e = new Entry { Layout = p[2] };
                if (p[0] == "S")
                {
                    e.Role = p[5];
                    e.Fields = new(StringComparer.Ordinal);
                    foreach (var m in p.Skip(6))
                    {
                        int at = m.IndexOf('@'), colon = m.IndexOf(':');
                        if (at <= 0 || colon < at)
                        {
                            error = $"bad field '{m}' of {p[1]}";
                            return null;
                        }
                        e.Fields[m[..at]] = m[(colon + 1)..];
                        if (m.AsSpan(at + 1, colon - at - 1).SequenceEqual("method")) t.Methods.Add((p[1], m[..at]));
                    }
                    // The generator emits Values and IGameAttribute<TSelf, TValue> together, with the same element.
                    if (e.Fields.TryGetValue("get_Values", out var view) && view.StartsWith("Span<", StringComparison.Ordinal) && view.EndsWith('>'))
                        e.Attribute = view[5..^1];
                }
                else
                {
                    e.Values = [];
                    foreach (var m in p.Skip(6))
                    {
                        var eq = m.IndexOf('=');
                        if (eq <= 0 || !decimal.TryParse(m[(eq + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                        {
                            error = $"bad value '{m}' of {p[1]}";
                            return null;
                        }
                        e.Values.Add((m[..eq], m[(eq + 1)..]));
                    }
                }
                t.Types[p[1]] = e;
            }
            if (!header)
            {
                error = "empty";
                return null;
            }
            return t;
        }
    }
}
