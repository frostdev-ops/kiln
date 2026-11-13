// SPDX-License-Identifier: MIT
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Kiln.Host;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Kiln.Sdk;

/// <summary>
/// Fills <c>"writes"</c> in the built mod's kiln.mod.json (<see cref="ManifestPath"/>, the copy in the output folder)
/// from the mod's code: every generated component type of <c>Kiln.Game</c> that the built assembly passes to
/// <c>World.Write&lt;T&gt;</c> or <c>World.Update&lt;T&gt;</c> (Kiln.World) is added by its game name, so the native
/// world service gives its write program access to it when the server starts and the author keeps no list by hand.
/// Names already in the manifest stay (a mod lists by hand what its code names only as a string:
/// <c>World.WriteField</c>, <c>World.WriteAt</c>); the rest of the file keeps its text, comments included.
/// The assemblies are read with System.Reflection.Metadata and never loaded. The game names come from the layout table
/// embedded in <see cref="GameReference"/> (resource <c>Kiln.Game.layouts</c>).
/// Warning KILN030: a call names a type the build cannot resolve to a component (a type parameter of the mod's own
/// generic method, a type that is not a generated component): list the component under <c>"writes"</c> by hand.
/// Error KILN031: the assembly or the manifest cannot be read or written.
/// </summary>
public sealed class KilnWrites : Microsoft.Build.Utilities.Task
{
    /// <summary>The generic methods of <c>Kiln.World</c> whose type argument is a component the mod writes.</summary>
    public static readonly string[] Methods = ["Write", "Update"];

    /// <summary>The built mod assembly.</summary>
    [Required] public string AssemblyPath { get; set; } = "";

    /// <summary>The Kiln.Game.dll the mod compiled against. Empty: the mod does not reference Kiln.Game.</summary>
    public string GameReference { get; set; } = "";

    /// <summary>The kiln.mod.json to fill, in the output folder.</summary>
    [Required] public string ManifestPath { get; set; } = "";

    /// <summary>The names added to the manifest, ";"-separated (empty when none was).</summary>
    [Output] public string Added { get; private set; } = "";

    public override bool Execute()
    {
        if (!File.Exists(AssemblyPath) || !File.Exists(ManifestPath)) return true;  // nothing built, or no manifest: nothing to fill
        Scan scan;
        try
        {
            scan = Find(AssemblyPath);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.LogError(null, "KILN031", null, AssemblyPath, 0, 0, 0, 0, $"cannot read the mod assembly for its tracked writes: {e.Message}");
            return false;
        }
        foreach (var call in scan.Unresolved)
            Log.LogWarning(null, "KILN030", null, AssemblyPath, 0, 0, 0, 0,
                $"World.{call.Method}<{call.Type}> is called with a type the build cannot resolve to a component ({call.Why}): " +
                "list the component it writes under \"writes\" in kiln.mod.json, or the write is refused at run time");
        if (scan.Types.Count == 0) return true;

        Dictionary<string, string>? components = null;  // C# full name -> the game's short name
        if (GameReference.Length > 0 && File.Exists(GameReference))
        {
            try
            {
                components = Components(KilnGameTypes.ReadTable(GameReference));
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.LogError(null, "KILN031", null, GameReference, 0, 0, 0, 0, $"cannot read the Kiln.Game reference for the component names: {e.Message}");
                return false;
            }
        }
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (type, method) in scan.Types)
        {
            if (components is not null && components.TryGetValue(type, out var name)) names.Add(name);
            else if (components is null && type.StartsWith("Kiln.Ecs+", StringComparison.Ordinal) && !type["Kiln.Ecs+".Length..].Contains('+'))
                names.Add(type["Kiln.Ecs+".Length..]);  // no table (an older Kiln.Game): keen::ecs::X is Kiln.Ecs.X
            else
                Log.LogWarning(null, "KILN030", null, AssemblyPath, 0, 0, 0, 0,
                    $"World.{method}<{type.Replace('+', '.')}> names a type that is not a generated component of this Kiln.Game: " +
                    "list the component it writes under \"writes\" in kiln.mod.json, or the write is refused at run time");
        }
        if (names.Count == 0) return true;

        try
        {
            var text = File.ReadAllText(ManifestPath);
            var (filled, added) = Fill(text, names);
            if (added.Count == 0) return true;
            // What the build writes is what the loader reads. The loader only warns about a list it cannot read (a mod
            // that loaded before tracked writes must still load); the mod's own build stops instead.
            if (Manifest.Parse(filled).WritesError is { } why) throw new InvalidDataException($"kiln.mod.json: \"writes\": {why}");
            File.WriteAllText(ManifestPath, filled, new UTF8Encoding(false));
            Added = string.Join(";", added);
            Log.LogMessage(MessageImportance.Normal, $"kiln.mod.json: \"writes\" += {string.Join(", ", added)} (the component types {Path.GetFileName(AssemblyPath)} writes)");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Log.LogError(null, "KILN031", null, ManifestPath, 0, 0, 0, 0, $"cannot add the mod's tracked writes to kiln.mod.json: {e.Message}");
            return false;
        }
    }

    /// <summary>What the assembly's calls to <c>World.Write</c> and <c>World.Update</c> name.</summary>
    public sealed class Scan
    {
        /// <summary>Type arguments that are types of another assembly or of the mod: full name (nested with +) and method.</summary>
        public readonly List<(string Type, string Method)> Types = [];
        /// <summary>Type arguments the build cannot name: a type parameter, a generic or array type.</summary>
        public readonly List<(string Method, string Type, string Why)> Unresolved = [];
    }

    /// <summary>Reads every instantiation of a generic method <see cref="Methods"/> of <c>Kiln.World</c> in the assembly.</summary>
    public static Scan Find(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var scan = new Scan();
        var seen = new HashSet<(string, string)>();
        for (int row = 1, count = md.GetTableRowCount(TableIndex.MethodSpec); row <= count; row++)
        {
            var spec = md.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
            if (spec.Method.Kind != HandleKind.MemberReference) continue;
            var member = md.GetMemberReference((MemberReferenceHandle)spec.Method);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var parent = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (!md.StringComparer.Equals(parent.Name, "World") || !md.StringComparer.Equals(parent.Namespace, "Kiln")) continue;
            if (parent.ResolutionScope.Kind != HandleKind.AssemblyReference ||
                !md.StringComparer.Equals(md.GetAssemblyReference((AssemblyReferenceHandle)parent.ResolutionScope).Name, "Kiln.World")) continue;
            var method = md.GetString(member.Name);
            if (Array.IndexOf(Methods, method) < 0) continue;
            var blob = md.GetBlobReader(spec.Signature);
            if (blob.ReadSignatureHeader().Kind != SignatureKind.MethodSpecification || blob.ReadCompressedInteger() != 1) continue;
            var code = blob.ReadSignatureTypeCode();
            switch (code)
            {
                case SignatureTypeCode.TypeHandle:
                    var handle = blob.ReadTypeHandle();
                    var name = handle.Kind switch
                    {
                        HandleKind.TypeReference => NameOf(md, (TypeReferenceHandle)handle, 0),
                        HandleKind.TypeDefinition => NameOf(md, (TypeDefinitionHandle)handle, 0),
                        _ => null,
                    };
                    if (name is null) Add(scan.Unresolved, (method, "?", "a type the metadata does not name"));
                    else if (seen.Add((name, method))) scan.Types.Add((name, method));
                    break;
                case SignatureTypeCode.GenericMethodParameter:
                case SignatureTypeCode.GenericTypeParameter:
                    Add(scan.Unresolved, (method, "T", "a type parameter of the mod's own generic code"));
                    break;
                default:
                    Add(scan.Unresolved, (method, code.ToString(), "not a plain component type"));
                    break;
            }
        }
        return scan;

        static void Add(List<(string, string, string)> list, (string, string, string) item)
        {
            if (!list.Contains(item)) list.Add(item);
        }
    }

    private static string? NameOf(MetadataReader md, TypeReferenceHandle h, int depth)
    {
        var t = md.GetTypeReference(h);
        var name = md.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            return depth < 32 && NameOf(md, (TypeReferenceHandle)t.ResolutionScope, depth + 1) is { } outer ? outer + "+" + name : null;
        var ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    private static string? NameOf(MetadataReader md, TypeDefinitionHandle h, int depth)
    {
        var t = md.GetTypeDefinition(h);
        var name = md.GetString(t.Name);
        var outer = t.GetDeclaringType();
        if (!outer.IsNil) return depth < 32 && NameOf(md, outer, depth + 1) is { } o ? o + "+" + name : null;
        var ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>The components of a Kiln.Game layout table: C# full name (<c>Kiln.Ecs+Fuel</c>) to the game's short name
    /// (<c>Fuel</c>). Null when the reference has no table.</summary>
    public static Dictionary<string, string>? Components(string? table)
    {
        if (table is null) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in table.Split('\n'))
        {
            var p = raw.TrimEnd('\r').Split('\t');
            // S <C# name> <layout> <game name> <size> <role> fields...
            if (p.Length < 6 || p[0] != "S" || p[5] != "component") continue;
            var colon = p[3].LastIndexOf("::", StringComparison.Ordinal);
            map[p[1]] = colon < 0 ? p[3] : p[3][(colon + 2)..];
        }
        return map;
    }

    /// <summary>The manifest text with <paramref name="names"/> in its <c>"writes"</c>: missing names are appended to an
    /// existing list, or a new list becomes the first key. Everything else keeps its text. Returns the names added.</summary>
    /// <exception cref="InvalidDataException">The text is not a manifest object, or its "writes" is not a list.</exception>
    public static (string Text, List<string> Added) Fill(string text, IEnumerable<string> names)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("kiln.mod.json: the file must contain one JSON object");
        var open = (int)reader.TokenStartIndex;
        var have = new HashSet<string>(StringComparer.Ordinal);
        int end = -1, last = -1;  // the list's closing bracket, and the end of its last element
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isWrites = reader.ValueTextEquals("writes");
            reader.Read();
            if (!isWrites)
            {
                reader.Skip();
                continue;
            }
            if (reader.TokenType != JsonTokenType.StartArray) throw new InvalidDataException("kiln.mod.json: 'writes' must be an array of strings");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String) throw new InvalidDataException("kiln.mod.json: 'writes' must be an array of strings");
                have.Add(reader.GetString()!);
                last = (int)reader.BytesConsumed;
            }
            end = (int)reader.TokenStartIndex;
        }
        var added = names.Where(n => !have.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
        if (added.Count == 0) return (text, added);
        var list = string.Join(", ", added.Select(n => JsonSerializer.Serialize(n)));
        byte[] insert;
        int at;
        if (end < 0)
        {
            at = open + 1;
            insert = Encoding.UTF8.GetBytes($"\n  \"writes\": [{list}],");
        }
        else if (last < 0)
        {
            at = end;  // an empty list
            insert = Encoding.UTF8.GetBytes(list);
        }
        else
        {
            at = last;  // right after the last name, before any trailing comma or comment
            insert = Encoding.UTF8.GetBytes(", " + list);
        }
        var filled = new byte[bytes.Length + insert.Length];
        bytes.AsSpan(0, at).CopyTo(filled);
        insert.CopyTo(filled.AsSpan(at));
        bytes.AsSpan(at).CopyTo(filled.AsSpan(at + insert.Length));
        return (Encoding.UTF8.GetString(filled), added);
    }
}
