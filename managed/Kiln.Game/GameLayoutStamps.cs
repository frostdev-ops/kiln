// SPDX-License-Identifier: MIT
// Hand-written. The layout table embedded in this assembly (resource Kiln.Game.layouts, written by
// tools/typegen/typegen.py at build time) and the check of a mod's layout stamp (kiln.gametypes.json) against it.
// Formats: docs/kiln/typegen.md §3.
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Kiln;

/// <summary>
/// The layouts of the generated game types in this <c>Kiln.Game.dll</c>, and the check that tells whether a mod built
/// against other layouts can run against these. A mod binds to a generated type's fields by name when its code is
/// compiled at run time, so it keeps working while every field it uses is still there with the same type, and every
/// enum value it uses (enum constants are copied into the mod when it is built) keeps its number.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GameLayoutStamps
{
    /// <summary>The name of the embedded table.</summary>
    internal const string ResourceName = "Kiln.Game.layouts";

    private static readonly Lazy<string> _table = new(() =>
    {
        using var s = typeof(GameLayoutStamps).Assembly.GetManifestResourceStream(ResourceName);
        if (s is null) return "";
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    });

    private static readonly Lazy<LayoutTable> _parsed = new(() => LayoutTable.Parse(_table.Value));

    /// <summary>The table of this assembly, as embedded: one line per generated type with its layout hash and its
    /// fields (or enum values), after a header with the dump's side, exe SHA-256 and the hash of the whole set.</summary>
    public static string Table => _table.Value;

    /// <summary>
    /// Compares a mod's stamp (the text of its <c>kiln.gametypes.json</c>) with this assembly. False with a reason that
    /// names the type, and the field or enum value, when the mod cannot run against these layouts. Thread-safe.
    /// </summary>
    /// <param name="stampJson">The stamp's text.</param>
    /// <param name="reason">Null when the mod can run, else why not.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stampJson"/> is null.</exception>
    public static bool Check(string stampJson, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(stampJson);
        return Check(stampJson, _parsed.Value, out reason);
    }

    /// <summary><see cref="Check(string, out string?)"/> against another table (tests).</summary>
    internal static bool Check(string stampJson, string table, out string? reason) =>
        Check(stampJson, LayoutTable.Parse(table), out reason);

    private static bool Check(string stampJson, LayoutTable table, out string? reason)
    {
        reason = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stampJson);
        }
        catch (JsonException e)
        {
            reason = "the layout stamp is not readable JSON: " + e.Message;
            return false;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(out reason, "the layout stamp is not a JSON object");
            string who = root.TryGetProperty("assembly", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() + ": " : "";
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var sv) || sv != 1)
                return Fail(out reason, who + "unknown layout stamp schema " +
                    (root.TryGetProperty("schema", out var s2) ? s2.GetRawText() : "(none)") + "; this Kiln.Game reads schema 1");
            if (table.Error is { } bad)
                return Fail(out reason, who + "this Kiln.Game's layout table is unreadable: " + bad);
            // The same layout set: every generated type is as the mod saw it.
            if (root.TryGetProperty("layoutSet", out var set) && set.ValueKind == JsonValueKind.String &&
                string.Equals(set.GetString(), table.Set, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!root.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Object)
                return Fail(out reason, who + "the layout stamp has no \"types\" object");
            foreach (var t in types.EnumerateObject())
            {
                if (!table.Types.TryGetValue(t.Name, out var have))
                    return Fail(out reason, $"{who}{t.Name} is not in this Kiln.Game ({table.Describe()})");
                if (t.Value.ValueKind != JsonValueKind.Object)
                    return Fail(out reason, $"{who}{t.Name}: the stamp's entry is not an object");
                if (t.Value.TryGetProperty("layout", out var lay) && lay.ValueKind == JsonValueKind.String &&
                    string.Equals(lay.GetString(), have.Layout, StringComparison.OrdinalIgnoreCase))
                    continue;
                // The layout changed: what the mod uses must still be there, the same.
                if (t.Value.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    if (have.Fields is null)
                        return Fail(out reason, $"{who}{t.Name} is no longer a struct in this Kiln.Game ({table.Describe()})");
                    foreach (var f in fields.EnumerateObject())
                    {
                        string was = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString()! : f.Value.GetRawText();
                        if (!have.Fields.TryGetValue(f.Name, out var now))
                            return Fail(out reason, $"{who}{t.Name}.{f.Name} ({was}) is gone in this Kiln.Game ({table.Describe()})");
                        if (!string.Equals(was, now, StringComparison.Ordinal))
                            return Fail(out reason, $"{who}{t.Name}.{f.Name} changed from {was} to {now} in this Kiln.Game ({table.Describe()})");
                    }
                }
                if (t.Value.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
                {
                    if (have.Values is null)
                        return Fail(out reason, $"{who}{t.Name} is no longer an enum in this Kiln.Game ({table.Describe()})");
                    foreach (var v in values.EnumerateObject())
                    {
                        string was = v.Value.GetRawText();
                        if (v.Value.ValueKind != JsonValueKind.Number ||
                            !decimal.TryParse(was, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var wasN))
                            return Fail(out reason, $"{who}{t.Name}.{v.Name}: the stamp's value {was} is not an integer");
                        if (!have.Values.TryGetValue(v.Name, out var nowN))
                            return Fail(out reason, $"{who}{t.Name}.{v.Name} (= {was}) is gone in this Kiln.Game ({table.Describe()})");
                        if (wasN != nowN)
                            return Fail(out reason, $"{who}{t.Name}.{v.Name} changed from {was} to {nowN.ToString(CultureInfo.InvariantCulture)} in this Kiln.Game ({table.Describe()})");
                    }
                }
            }
            return true;
        }
    }

    private static bool Fail(out string? reason, string why)
    {
        reason = why;
        return false;
    }

    private sealed class Entry
    {
        public required string Layout;
        public Dictionary<string, string>? Fields;   // struct: field name -> C# type as written in the generated source
        public Dictionary<string, decimal>? Values;  // enum or bitmask: name -> value
    }

    private sealed class LayoutTable
    {
        public string Set = "", Side = "?", Exe = "";
        public string? Error;
        public readonly Dictionary<string, Entry> Types = new(StringComparer.Ordinal);

        public string Describe() => Exe.Length >= 8 ? $"{Side} {Exe[..8]}" : Side;

        public static LayoutTable Parse(string text)
        {
            var t = new LayoutTable();
            bool header = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var p = line.Split('\t');
                if (!header)
                {
                    if (p[0] != "kiln.game.layouts" || p.Length < 2 || p[1] != "1")
                    {
                        t.Error = "no kiln.game.layouts 1 header";
                        return t;
                    }
                    foreach (var kv in p.Skip(2))
                    {
                        int eq = kv.IndexOf('=');
                        if (eq < 0) continue;
                        string k = kv[..eq], v = kv[(eq + 1)..];
                        if (k == "set") t.Set = v;
                        else if (k == "side") t.Side = v;
                        else if (k == "exe_sha256") t.Exe = v;
                    }
                    header = true;
                    continue;
                }
                // S|E|F, C# full name, layout, qualified name, size, role or underlying type, members...
                if (p.Length < 6 || p[0] is not ("S" or "E" or "F"))
                {
                    t.Error = "bad line: " + (line.Length > 80 ? line[..80] : line);
                    return t;
                }
                var e = new Entry { Layout = p[2] };
                if (p[0] == "S")
                {
                    e.Fields = new(StringComparer.Ordinal);
                    foreach (var m in p.Skip(6))
                    {
                        int at = m.IndexOf('@'), colon = m.IndexOf(':');
                        if (at <= 0 || colon < at) { t.Error = "bad field " + m + " of " + p[1]; return t; }
                        e.Fields[m[..at]] = m[(colon + 1)..];
                    }
                }
                else
                {
                    e.Values = new(StringComparer.Ordinal);
                    foreach (var m in p.Skip(6))
                    {
                        int eq = m.IndexOf('=');
                        if (eq <= 0 || !decimal.TryParse(m[(eq + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
                        {
                            t.Error = "bad value " + m + " of " + p[1];
                            return t;
                        }
                        e.Values[m[..eq]] = v;
                    }
                }
                t.Types[p[1]] = e;
            }
            if (!header) t.Error = "empty";
            return t;
        }
    }
}
