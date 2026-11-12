// SPDX-License-Identifier: MIT
// The allow-list over the committed layout dumps (sigdb/types). The owner allowed committing them on one condition:
// structure only (type and field names, kinds, sizes, alignments, offsets, hashes, enum values). Every key at every
// level must be in the allowed set of the file's schema and every value must have the shape that key allows, so a
// default-value byte, a flags byte, an address or any other blob read from the exe cannot reach the tree unnoticed.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.Game.Tests;

/// <summary>Checks one dump file. Every problem is a line naming the file and the key path.</summary>
public static partial class DumpAllowList
{
    // Schema 2 (tools/typegen/typedump), keys in the order the tool writes them.
    static readonly string[] Top2 = ["schema", "side", "exe_sha256", "components", "events", "types"];
    static readonly string[] Type2 = ["name", "qualifiedName", "hash", "kind", "size", "align", "inner", "fields", "values"];
    static readonly string[] Field2 = ["name", "offset", "type"];
    static readonly string[] Value2 = ["name", "value"];
    // Schema 1 (the legacy lab probe dump), keys in its order.
    static readonly string[] Top1 = ["comment", "side", "types"];
    static readonly string[] Type1 = ["name", "qualifiedName", "hash", "size", "kind", "fields"];
    static readonly string[] Field1 = ["name", "offset", "type", "size", "kind"];

    const int MaxTypeName = 128, MaxMemberName = 64, MaxComment = 160;
    const long MaxSize = 1 << 20;

    // A C++ type name as the game writes it: identifiers joined by ::, optionally one template argument list made
    // of identifiers, ::, digits and commas (no spaces occur in the game's names).
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*(<[A-Za-z0-9_:<>,]+>)?$")]
    private static partial Regex TypeNameRx();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex MemberNameRx();
    [GeneratedRegex(@"^0x[0-9A-F]{8}$")]
    private static partial Regex HashRx();
    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex ShaRx();
    // Anything that looks like hex data rather than a name: 16 or more hex digits, with or without 0x.
    [GeneratedRegex(@"(0x)?[0-9A-Fa-f]{16,}")]
    private static partial Regex HexBlobRx();
    // The legacy file's one free-text line.
    [GeneratedRegex(@"^[A-Za-z0-9 .,:/_-]+$")]
    private static partial Regex CommentRx();

    public static uint Fnv1a(string s)
    {
        uint h = 0x811c9dc5u;
        foreach (byte c in System.Text.Encoding.UTF8.GetBytes(s)) h = (h ^ c) * 0x01000193u;
        return h;
    }

    /// <summary>Problems in the file at <paramref name="path"/>, shown as <paramref name="label"/>. When
    /// <paramref name="location"/> is set the file must sit at <c>&lt;side&gt;/&lt;exe_sha256&gt;.json</c> (schema 2)
    /// or <c>&lt;side&gt;/&lt;sha&gt;.json</c> (schema 1).</summary>
    public static List<string> Check(string path, string label, bool location)
    {
        var errors = new List<string>();
        void Err(string key, string what) => errors.Add($"{label}: {key}: {what}");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException e)
        {
            Err("(file)", "not JSON: " + e.Message);
            return errors;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Err("(root)", "not an object"); return errors; }
            bool v2 = root.TryGetProperty("schema", out _);
            var ctx = new Ctx(Err, v2);
            if (v2) CheckV2(root, ctx);
            else CheckV1(root, ctx);
            if (location)
            {
                string side = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                string stem = Path.GetFileNameWithoutExtension(path);
                if (root.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() != side)
                    Err("side", $"is \"{s.GetString()}\" but the file is under {side}/");
                if (!ShaRx().IsMatch(stem)) Err("(file name)", "is not <sha256>.json");
                if (v2 && root.TryGetProperty("exe_sha256", out var x) && x.ValueKind == JsonValueKind.String &&
                    x.GetString() != stem)
                    Err("exe_sha256", "does not equal the file name");
            }
        }
        return errors;
    }

    sealed record Ctx(Action<string, string> Err, bool V2);

    // Keys: each allowed, none twice, in the schema's order. Returns the properties by name.
    static Dictionary<string, JsonElement> Keys(JsonElement o, string at, string[] allowed, string[] required, Ctx c)
    {
        var seen = new Dictionary<string, JsonElement>();
        int last = -1;
        foreach (var p in o.EnumerateObject())
        {
            string key = at.Length == 0 ? p.Name : $"{at}.{p.Name}";
            int i = Array.IndexOf(allowed, p.Name);
            if (i < 0) { c.Err(key, "key not allowed"); continue; }
            if (!seen.TryAdd(p.Name, p.Value)) { c.Err(key, "key appears twice"); continue; }
            if (i < last) c.Err(key, "key out of order");
            last = Math.Max(last, i);
        }
        foreach (var r in required)
            if (!seen.ContainsKey(r)) c.Err(at.Length == 0 ? r : $"{at}.{r}", "required key missing");
        return seen;
    }

    static string? Str(Dictionary<string, JsonElement> o, string name, string at, Ctx c, Regex rx, int max)
    {
        if (!o.TryGetValue(name, out var v)) return null;
        string key = $"{at}.{name}".TrimStart('.');
        if (v.ValueKind != JsonValueKind.String) { c.Err(key, $"must be a string, is {v.ValueKind}"); return null; }
        string s = v.GetString()!;
        if (s.Length > max) { c.Err(key, $"string longer than {max}"); return null; }
        if (!rx.IsMatch(s)) { c.Err(key, $"\"{Clip(s)}\" is not a name"); return null; }
        if (HexBlobRx().IsMatch(s)) { c.Err(key, $"\"{Clip(s)}\" looks like hex data"); return null; }
        return s;
    }

    static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;

    static long? Int(Dictionary<string, JsonElement> o, string name, string at, Ctx c, long min, long max)
    {
        if (!o.TryGetValue(name, out var v)) return null;
        string key = $"{at}.{name}".TrimStart('.');
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long n) || v.GetRawText().Contains('.') ||
            v.GetRawText().Contains('e') || v.GetRawText().Contains('E'))
        {
            c.Err(key, $"must be an integer, is {v.ValueKind}");
            return null;
        }
        if (n < min || n > max) { c.Err(key, $"{n} is outside {min}..{max}"); return null; }
        return n;
    }

    static void Hash(Dictionary<string, JsonElement> o, string at, Ctx c, string? qn)
    {
        if (!o.TryGetValue("hash", out var v)) return;
        string key = $"{at}.hash";
        if (v.ValueKind != JsonValueKind.String || !HashRx().IsMatch(v.GetString()!))
        {
            c.Err(key, "must be \"0x\" and eight upper-case hex digits");
            return;
        }
        uint h = uint.Parse(v.GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (qn != null && h != Fnv1a(qn)) c.Err(key, $"is not FNV-1a-32 of \"{qn}\" (0x{Fnv1a(qn):X8})");
    }

    static IEnumerable<(JsonElement e, string at)> Items(Dictionary<string, JsonElement> o, string name, string at, Ctx c)
    {
        if (!o.TryGetValue(name, out var v)) yield break;
        string key = $"{at}.{name}".TrimStart('.');
        if (v.ValueKind != JsonValueKind.Array) { c.Err(key, $"must be an array, is {v.ValueKind}"); yield break; }
        int i = 0;
        foreach (var e in v.EnumerateArray()) yield return (e, $"{key}[{i++}]");
    }

    static void Side(Dictionary<string, JsonElement> o, Ctx c)
    {
        if (o.TryGetValue("side", out var s) && (s.ValueKind != JsonValueKind.String || s.GetString() is not ("server" or "client")))
            c.Err("side", "must be \"server\" or \"client\"");
    }

    sealed class T2
    {
        public string? Qn;
        public long Kind, Size = -1;
        public string? Inner;
        public List<(string at, long offset, string? type)> Fields = [];
    }

    static void CheckV2(JsonElement root, Ctx c)
    {
        var top = Keys(root, "", Top2, Top2, c);
        if (top.TryGetValue("schema", out var sv) && !(sv.ValueKind == JsonValueKind.Number && sv.GetRawText() == "2"))
            c.Err("schema", "must be 2");
        Side(top, c);
        if (top.TryGetValue("exe_sha256", out var sha) && (sha.ValueKind != JsonValueKind.String || !ShaRx().IsMatch(sha.GetString()!)))
            c.Err("exe_sha256", "must be 64 lower-case hex digits");

        var types = new List<T2>();
        foreach (var (e, at) in Items(top, "types", "", c))
        {
            if (e.ValueKind != JsonValueKind.Object) { c.Err(at, $"must be an object, is {e.ValueKind}"); continue; }
            var o = Keys(e, at, Type2, ["name", "qualifiedName", "hash", "kind", "size", "align"], c);
            var t = new T2();
            Str(o, "name", at, c, TypeNameRx(), MaxTypeName);
            t.Qn = Str(o, "qualifiedName", at, c, TypeNameRx(), MaxTypeName);
            Hash(o, at, c, t.Qn);
            t.Kind = Int(o, "kind", at, c, 1, 29) ?? 0;
            t.Size = Int(o, "size", at, c, 0, MaxSize) ?? -1;
            long? align = Int(o, "align", at, c, 1, 64);
            if (align is long a && (a & (a - 1)) != 0) c.Err($"{at}.align", $"{a} is not a power of two");
            t.Inner = Str(o, "inner", at, c, TypeNameRx(), MaxTypeName);
            bool kindKnown = t.Kind is >= 1 and <= 29;
            if (kindKnown && o.ContainsKey("inner") && t.Kind is < 12 or > 19)
                c.Err($"{at}.inner", $"not allowed on kind {t.Kind}");
            if (kindKnown && !o.ContainsKey("inner") && t.Kind is >= 12 and <= 19 and not 18)
                c.Err($"{at}.inner", $"required on kind {t.Kind}");
            if (kindKnown && t.Kind != 18 && o.ContainsKey("fields")) c.Err($"{at}.fields", $"not allowed on kind {t.Kind}");
            if (kindKnown && t.Kind == 18 && !o.ContainsKey("fields")) c.Err($"{at}.fields", "required on kind 18");
            if (kindKnown && t.Kind is < 12 or > 16 && o.ContainsKey("values")) c.Err($"{at}.values", $"not allowed on kind {t.Kind}");
            if (kindKnown && t.Kind == 12 && !o.ContainsKey("values")) c.Err($"{at}.values", "required on kind 12");
            foreach (var (f, fat) in Items(o, "fields", at, c))
            {
                if (f.ValueKind != JsonValueKind.Object) { c.Err(fat, $"must be an object, is {f.ValueKind}"); continue; }
                var fo = Keys(f, fat, Field2, Field2, c);
                Str(fo, "name", fat, c, MemberNameRx(), MaxMemberName);
                long off = Int(fo, "offset", fat, c, 0, MaxSize) ?? -1;
                string? ft = Str(fo, "type", fat, c, TypeNameRx(), MaxTypeName);
                t.Fields.Add((fat, off, ft));
            }
            foreach (var (v, vat) in Items(o, "values", at, c))
            {
                if (v.ValueKind != JsonValueKind.Object) { c.Err(vat, $"must be an object, is {v.ValueKind}"); continue; }
                var vo = Keys(v, vat, Value2, Value2, c);
                Str(vo, "name", vat, c, MemberNameRx(), MaxMemberName);
                if (vo.TryGetValue("value", out var n) &&
                    !(n.ValueKind == JsonValueKind.Number && (n.TryGetInt64(out _) || n.TryGetUInt64(out _)) &&
                      !n.GetRawText().Contains('.') && !n.GetRawText().Contains('e') && !n.GetRawText().Contains('E')))
                    c.Err($"{vat}.value", $"must be an integer, is {n.ValueKind}");
            }
            types.Add(t);
        }

        var byName = new Dictionary<string, T2>(StringComparer.Ordinal);
        string? prev = null;
        for (int i = 0; i < types.Count; ++i)
        {
            var q = types[i].Qn;
            if (q == null) continue;
            if (!byName.TryAdd(q, types[i])) c.Err($"types[{i}].qualifiedName", $"\"{q}\" appears twice");
            if (prev != null && string.CompareOrdinal(prev, q) > 0) c.Err($"types[{i}].qualifiedName", "types are not sorted by qualifiedName");
            prev = q;
        }
        for (int i = 0; i < types.Count; ++i)
        {
            var t = types[i];
            if (t.Inner != null && !byName.ContainsKey(t.Inner))
                c.Err($"types[{i}].inner", $"\"{t.Inner}\" is not a type in this file");
            foreach (var (fat, off, ft) in t.Fields)
            {
                if (ft == null) continue;
                if (!byName.TryGetValue(ft, out var target)) { c.Err($"{fat}.type", $"\"{ft}\" is not a type in this file"); continue; }
                if (off >= 0 && t.Size >= 0 && target.Size >= 0 && off + target.Size > t.Size)
                    c.Err($"{fat}.offset", $"{off} + {target.Size} ends past the struct's size {t.Size}");
            }
        }
        foreach (var list in new[] { "components", "events" })
        {
            string? last = null;
            foreach (var (e, at) in Items(top, list, "", c))
            {
                if (e.ValueKind != JsonValueKind.String) { c.Err(at, $"must be a string, is {e.ValueKind}"); continue; }
                string s = e.GetString()!;
                if (s.Length > MaxTypeName || !TypeNameRx().IsMatch(s) || HexBlobRx().IsMatch(s)) { c.Err(at, $"\"{Clip(s)}\" is not a name"); continue; }
                if (!byName.ContainsKey(s)) c.Err(at, $"\"{s}\" is not a type in this file");
                if (last != null && string.CompareOrdinal(last, s) >= 0) c.Err(at, "not sorted or not unique");
                last = s;
            }
        }
    }

    static void CheckV1(JsonElement root, Ctx c)
    {
        var top = Keys(root, "", Top1, ["side", "types"], c);
        Str(top, "comment", "", c, CommentRx(), MaxComment);
        Side(top, c);
        foreach (var (e, at) in Items(top, "types", "", c))
        {
            if (e.ValueKind != JsonValueKind.Object) { c.Err(at, $"must be an object, is {e.ValueKind}"); continue; }
            var o = Keys(e, at, Type1, ["name", "qualifiedName", "hash", "size", "kind"], c);
            Str(o, "name", at, c, TypeNameRx(), MaxTypeName);
            string? qn = Str(o, "qualifiedName", at, c, TypeNameRx(), MaxTypeName);
            Hash(o, at, c, qn);
            long size = Int(o, "size", at, c, 0, MaxSize) ?? -1;
            Int(o, "kind", at, c, 1, 29);
            foreach (var (f, fat) in Items(o, "fields", at, c))
            {
                if (f.ValueKind != JsonValueKind.Object) { c.Err(fat, $"must be an object, is {f.ValueKind}"); continue; }
                var fo = Keys(f, fat, Field1, Field1, c);
                Str(fo, "name", fat, c, MemberNameRx(), MaxMemberName);
                long off = Int(fo, "offset", fat, c, 0, MaxSize) ?? -1;
                Str(fo, "type", fat, c, TypeNameRx(), MaxTypeName);
                long fsize = Int(fo, "size", fat, c, 0, MaxSize) ?? -1;
                Int(fo, "kind", fat, c, 1, 29);
                if (off >= 0 && fsize >= 0 && size >= 0 && off + fsize > size)
                    c.Err($"{fat}.offset", $"{off} + {fsize} ends past the struct's size {size}");
            }
        }
    }
}

public class DumpAllowListTests
{
    static string RepoRoot([CallerFilePath] string source = "")
    {
        // The test assembly may be built outside the tree (--artifacts-path); then the source file's place is used.
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "sigdb", "types")) &&
                    File.Exists(Path.Combine(d.FullName, "managed", "tests", "Kiln.Game.Tests", "Kiln.Game.Tests.csproj")))
                    return d.FullName;
        throw new InvalidOperationException("repository root not found above the test assembly or its source");
    }

    static string Fixture(string name) =>
        Path.Combine(RepoRoot(), "managed", "tests", "Kiln.Game.Tests", "Fixtures", "dump", name);

    [Fact]
    public void Every_committed_dump_holds_only_allowed_keys_and_shapes()
    {
        string root = RepoRoot(), types = Path.Combine(root, "sigdb", "types");
        var files = Directory.GetFiles(types, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        var errors = new List<string>();
        foreach (var f in files)
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (!f.EndsWith(".json", StringComparison.Ordinal)) { errors.Add($"{rel}: (file): only .json dumps may live under sigdb/types"); continue; }
            errors.AddRange(DumpAllowList.Check(f, rel, location: true));
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(50)));
        // The legacy schema-1 dump is always among them.
        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("c5c9fe50", StringComparison.Ordinal));
    }

    [Fact]
    public void A_well_formed_schema_2_fixture_passes()
    {
        var errors = DumpAllowList.Check(Fixture("good.json"), "good.json", location: false);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Theory]
    [InlineData("defaults-key.json", "types[1].defaults", "key not allowed")]
    [InlineData("flags-key.json", "types[0].flags", "key not allowed")]
    [InlineData("rva-key.json", "types[2].rva", "key not allowed")]
    [InlineData("byte-array.json", "types[0].values[1].value", "must be an integer")]
    [InlineData("hex-blob.json", "types[1].fields[0].name", "looks like hex data")]
    [InlineData("unresolved-type.json", "types[1].fields[1].type", "is not a type in this file")]
    [InlineData("wrong-hash.json", "types[2].hash", "is not FNV-1a-32")]
    [InlineData("nested-object.json", "types[1].fields[0].offset", "must be an integer")]
    [InlineData("field-past-end.json", "types[1].fields[1].offset", "ends past the struct's size")]
    [InlineData("legacy-extra-key.json", "types[0].fields[0].defaultValue", "key not allowed")]
    public void A_bad_fixture_is_rejected_naming_file_and_key(string file, string key, string what)
    {
        var errors = DumpAllowList.Check(Fixture(file), file, location: false);
        Assert.Contains(errors, e => e.StartsWith($"{file}: {key}: ", StringComparison.Ordinal) && e.Contains(what, StringComparison.Ordinal));
    }

    [Fact]
    public void The_hash_is_fnv1a_32_of_the_qualified_name()
    {
        Assert.Equal(0xDC101091u, DumpAllowList.Fnv1a("keen::ecs::Health"));
    }
}
