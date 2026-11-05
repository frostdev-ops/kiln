// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Kiln.GameIds;

/// <summary>One path of the catalog's <c>fields</c> list.</summary>
internal sealed class FieldRow
{
    public FieldRow(string path, string type, string? kind, bool writable, List<string>? names, List<double>? numbers)
    {
        Path = path;
        Type = type;
        Kind = kind;
        Writable = writable;
        EnumNames = names;
        EnumNumbers = numbers;
    }

    public string Path { get; }
    public string Type { get; }
    public string? Kind { get; }
    public bool Writable { get; }
    public List<string>? EnumNames { get; }
    public List<double>? EnumNumbers { get; }

    /// <summary>A catalog without <c>kind</c> (made by an older Kiln): what the type's name says.</summary>
    public string EffectiveKind => Kind ?? (EnumNames is not null ? "enum" : FieldCatalog.ScalarOfType(Type) ?? "struct");
}

/// <summary>One id of the catalog's <c>configs</c> section.</summary>
internal sealed class ConfigRow
{
    public ConfigRow(string id, string? name, List<string> names, bool shared, string? type, string? kind, List<string>? enumNames, List<double>? enumNumbers)
    {
        Id = id;
        Name = name;
        Names = names;
        Shared = shared;
        Type = type;
        Kind = kind;
        EnumNames = enumNames;
        EnumNumbers = enumNumbers;
    }

    public string Id { get; }
    public string? Name { get; }
    public List<string> Names { get; }
    public bool Shared { get; }
    public string? Type { get; }
    public string? Kind { get; }
    public List<string>? EnumNames { get; }
    public List<double>? EnumNumbers { get; }
}

/// <summary>The part of the catalog typed fields and the path analyzer read: <c>fields</c> and <c>configs</c>.</summary>
internal sealed class FieldCatalog
{
    private static readonly HashSet<string> PlainScalars = new(StringComparer.Ordinal)
    {
        "bool", "uint8", "sint8", "uint16", "sint16", "uint32", "sint32", "uint64", "sint64", "float32", "float64", "float",
    };

    public Dictionary<string, List<FieldRow>> Fields { get; } = new(StringComparer.Ordinal);
    public List<ConfigRow> Configs { get; } = new();
    public int Recipe { get; set; }

    public static string? ScalarOfType(string type) => type == "float" ? "float32" : PlainScalars.Contains(type) ? type : null;

    public static FieldCatalog Read(string text)
    {
        var cat = GameIdsGenerator.Catalog.Read(text, new HashSet<string>(StringComparer.Ordinal) { "configs" }, wantFields: true);
        return From(cat);
    }

    public static FieldCatalog From(GameIdsGenerator.Catalog cat)
    {
        var c = new FieldCatalog { Recipe = cat.Recipe };
        foreach (var kv in cat.Fields)
        {
            if (kv.Value is not List<object?> list) continue;
            var rows = new List<FieldRow>(list.Count);
            foreach (var o in list)
            {
                if (o is not Dictionary<string, object?> d || !(d.TryGetValue("path", out var p) && p is string path)) continue;
                rows.Add(new FieldRow(path, d.TryGetValue("type", out var t) ? t as string ?? "" : "", d.TryGetValue("kind", out var k) ? k as string : null,
                    !(d.TryGetValue("writable", out var w) && w is false), Strings(d, "enum"), Numbers(d, "enumNumbers")));
            }
            c.Fields[kv.Key] = rows;
        }
        if (cat.Sections.TryGetValue("configs", out var cfgs))
            foreach (var d in cfgs)
            {
                if (!(d.TryGetValue("id", out var i) && i is string id)) continue;
                c.Configs.Add(new ConfigRow(id, d.TryGetValue("name", out var n) ? n as string : null, Strings(d, "names") ?? new List<string>(),
                    d.TryGetValue("shared", out var sh) && sh is true, d.TryGetValue("type", out var t) ? t as string : null,
                    d.TryGetValue("kind", out var k) ? k as string : null, Strings(d, "enum"), Numbers(d, "enumNumbers")));
            }
        return c;
    }

    private static List<string>? Strings(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object?> l ? l.OfType<string>().ToList() : null;

    private static List<double>? Numbers(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object?> l ? l.OfType<double>().ToList() : null;
}

/// <summary>A catalog's paths as a tree, shared by the generator (one member per node) and the analyzer (a string path
/// walks it the way kiln.gamedata walks the game's reflection).</summary>
internal sealed class FieldNode
{
    private readonly Dictionary<string, FieldNode> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldNode> _byLower = new(StringComparer.OrdinalIgnoreCase);

    public FieldNode(string name) => Name = name;

    public string Name { get; }
    public FieldRow? Row { get; set; }
    public List<FieldNode> Children { get; } = new();

    /// <summary>What <c>[i]</c> and <c>[*]</c> lead to.</summary>
    public FieldNode? Element { get; set; }

    /// <summary>A short static array lists its indices: how many. 0: any index.</summary>
    public int FixedCount { get; set; }

    public bool IsLeaf => Children.Count == 0 && Element is null;

    /// <summary>A child by name: exact, then ignoring case, as kiln.gamedata looks fields up.</summary>
    public FieldNode? Child(string name) => _byName.TryGetValue(name, out var c) || _byLower.TryGetValue(name, out c) ? c : null;

    private FieldNode GetOrAdd(string name)
    {
        if (_byName.TryGetValue(name, out var c)) return c;
        c = new FieldNode(name);
        _byName[name] = c;
        if (!_byLower.ContainsKey(name)) _byLower[name] = c;
        Children.Add(c);
        return c;
    }

    public static FieldNode Build(IEnumerable<FieldRow> rows)
    {
        var root = new FieldNode("");
        foreach (var row in rows)
        {
            var n = root;
            foreach (var tok in FieldPathToken.Parse(row.Path))
            {
                if (!tok.IsIndex)
                {
                    n = n.GetOrAdd(tok.Text);
                    continue;
                }
                n.Element ??= new FieldNode("[]");
                if (int.TryParse(tok.Text, out var i)) n.FixedCount = Math.Max(n.FixedCount, i + 1);
                n = n.Element;
            }
            n.Row ??= row;
        }
        return root;
    }
}

/// <summary>A segment of a path: <c>a.b[3].c</c> is a, b, [3], c.</summary>
internal readonly struct FieldPathToken
{
    public FieldPathToken(bool isIndex, string text)
    {
        IsIndex = isIndex;
        Text = text;
    }

    public bool IsIndex { get; }
    public string Text { get; }

    /// <summary>The segments, or null when the brackets do not close.</summary>
    public static List<FieldPathToken>? TryParse(string path)
    {
        var out_ = new List<FieldPathToken>();
        var cur = new StringBuilder();
        void Flush()
        {
            if (cur.Length == 0) return;
            out_.Add(new FieldPathToken(false, cur.ToString()));
            cur.Clear();
        }
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '.') Flush();
            else if (c == '[')
            {
                Flush();
                var end = path.IndexOf(']', i);
                if (end < 0) return null;
                out_.Add(new FieldPathToken(true, path.Substring(i + 1, end - i - 1)));
                i = end;
            }
            else cur.Append(c);
        }
        Flush();
        return out_;
    }

    public static List<FieldPathToken> Parse(string path) => TryParse(path) ?? new List<FieldPathToken>();
}
