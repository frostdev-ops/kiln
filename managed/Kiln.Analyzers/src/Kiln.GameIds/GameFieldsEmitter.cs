// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kiln.GameIds;

/// <summary>
/// C# for the catalog's <c>fields</c>: <c>GameFields.Items.DamageSetup.DmgMod</c> is a <c>Kiln.Field&lt;float&gt;</c> with the path
/// <c>damageSetup.dmgMod</c>. Sections are static classes. Below them each structure is a small struct holding its path,
/// which lets a list give out elements (<c>Tags[2].Tag</c>, <c>Tags.All.Tag</c>) and keeps the types cheap: nothing is
/// stored in static fields and a property is only a string concatenation. Enum and flag fields use <c>GameEnums</c>, made
/// from the same catalog. Config values go by the names the game's Impact programs give them.
/// </summary>
internal sealed class GameFieldsEmitter
{
    private const string FieldType = "global::Kiln.Field";
    private const string ReadOnlyFieldType = "global::Kiln.ReadOnlyField";
    private const string JsonNodeType = "global::System.Text.Json.Nodes.JsonNode?";
    private const string PathHelper = "global::Kiln.FieldPath";
    private const string Enums = "global::Kiln.GameIds.GameEnums.";
    private const string Nodes = "global::Kiln.GameIds.GameFieldNodes.";

    private readonly FieldCatalog _cat;
    private readonly Dictionary<string, EnumDef> _enums = new(StringComparer.Ordinal);
    private readonly StringBuilder _nodes = new();
    private readonly HashSet<string> _nodeNames = new(StringComparer.Ordinal) { "ConfigValues" };

    private sealed class EnumDef
    {
        public EnumDef(string type, bool flags, List<string> names, List<double>? numbers)
        {
            Type = type;
            Flags = flags;
            Names = names;
            Numbers = numbers;
        }

        public string Type { get; }
        public bool Flags { get; }
        public List<string> Names { get; }
        public List<double>? Numbers { get; }
        public string Ident { get; set; } = "";
    }

    public GameFieldsEmitter(FieldCatalog cat)
    {
        _cat = cat;
        CollectEnums();
    }

    /// <summary>The generated files: (hint name, source).</summary>
    public IEnumerable<(string Hint, string Source)> Emit(Func<StringBuilder> header, string build)
    {
        var fields = header();
        fields.AppendLine("namespace Kiln.GameIds");
        fields.AppendLine("{");
        fields.Append("    /// <summary>").Append(GameIdsGenerator.Xml($"Typed paths of the game's data structures, from the local catalog of {build}. Use them with Content.Items.Get(...).Set(field, value) and the other typed overloads; a field's Path is the string a data pack writes."))
            .AppendLine("</summary>");
        fields.AppendLine("    public static partial class GameFields");
        fields.AppendLine("    {");
        foreach (var kv in _cat.Fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value.Count == 0) continue;
            var cls = Pascal(kv.Key);
            var tree = FieldNode.Build(kv.Value);
            fields.Append("        /// <summary>").Append(GameIdsGenerator.Xml($"The paths of `{kv.Key}` entries.")).AppendLine("</summary>");
            fields.Append("        public static partial class ").AppendLine(cls);
            fields.AppendLine("        {");
            var used = Reserved(cls);
            foreach (var child in tree.Children)
                Member(fields, "            ", child, kv.Key, cls, null, used);
            fields.AppendLine("        }");
        }
        fields.AppendLine("    }");
        fields.AppendLine("}");
        yield return ("Kiln.GameIds.GameFields.g.cs", fields.ToString());

        var nodes = header();
        nodes.AppendLine("namespace Kiln.GameIds");
        nodes.AppendLine("{");
        nodes.AppendLine("    /// <summary>The structures of <see cref=\"GameFields\"/>: reached through it, not named by hand.</summary>");
        nodes.AppendLine("    public static partial class GameFieldNodes");
        nodes.AppendLine("    {");
        ConfigStruct(nodes);
        nodes.Append(_nodes);
        nodes.AppendLine("    }");
        nodes.AppendLine("}");
        yield return ("Kiln.GameIds.GameFieldNodes.g.cs", nodes.ToString());

        yield return ("Kiln.GameIds.GameEnums.g.cs", EnumsSource(header(), build));
    }

    // ---------------------------------------------------------------- members

    private static HashSet<string> Reserved(string own) =>
        new(StringComparer.Ordinal) { own, "Path", "Self", "All", "Item", "ToString", "Equals", "GetHashCode", "GetType", "Named", "ById" };

    private static string Unique(HashSet<string> used, string name)
    {
        var id = name;
        for (var n = 2; !used.Add(id); n++) id = name + "_" + n.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    /// <summary>The expression for a path below a structure (<paramref name="at"/> null: at a section's root).</summary>
    private static string PathExpr(string? at, string suffix) =>
        at is null ? GameIdsGenerator.Lit(suffix) : at + " + " + GameIdsGenerator.Lit("." + suffix);

    private void Member(StringBuilder sb, string indent, FieldNode n, string section, string prefix, string? at, HashSet<string> used)
    {
        var ident = Unique(used, Pascal(n.Name));
        Doc(sb, indent, n.Row, at is null ? n.Name : null);
        var path = PathExpr(at, n.Name);
        if (n.Row?.EffectiveKind == "configs")
        {
            sb.Append(indent).Append(at is null ? "public static " : "public ").Append(Nodes).Append("ConfigValues ").Append(ident)
                .Append(" => new(").Append(GameIdsGenerator.Lit(section)).Append(", ").Append(path).AppendLine(");");
            return;
        }
        if (n.IsLeaf)
        {
            sb.Append(indent).Append(at is null ? "public static " : "public ").Append(Leaf(n.Row)).Append(' ').Append(ident)
                .Append(" => new(").Append(GameIdsGenerator.Lit(section)).Append(", ").Append(path).AppendLine(");");
            return;
        }
        var type = Struct(n, section, prefix + "_" + ident);
        sb.Append(indent).Append(at is null ? "public static " : "public ").Append(Nodes).Append(type).Append(' ').Append(ident)
            .Append(" => new(").Append(path).AppendLine(");");
    }

    /// <summary>The struct of a structure or list node, written into the nodes file. Returns its name.</summary>
    private string Struct(FieldNode n, string section, string wanted)
    {
        var name = Unique(_nodeNames, GameIdsGenerator.Ident(wanted).TrimStart('@'));
        var sb = new StringBuilder();
        sb.Append("        public readonly struct ").AppendLine(name);
        sb.AppendLine("        {");
        sb.AppendLine("            private readonly string _p;");
        sb.Append("            public ").Append(name).AppendLine("(string path) { _p = path; }");
        sb.AppendLine("            /// <summary>The path of this structure.</summary>");
        sb.AppendLine("            public string Path => _p;");
        sb.AppendLine("            public override string ToString() => _p;");
        var used = Reserved(name);
        if (n.Row is not null)
        {
            sb.AppendLine("            /// <summary>This structure or list as one value (JSON, as in a data pack).</summary>");
            sb.Append("            public ").Append(Leaf(n.Row)).Append(" Self => new(").Append(GameIdsGenerator.Lit(section)).AppendLine(", _p);");
        }
        foreach (var c in n.Children)
            Member(sb, "            ", c, section, name, "_p", used);
        if (n.Element is { } el)
            ListMembers(sb, n, el, section, name);
        sb.AppendLine("        }");
        _nodes.Append(sb);
        return name;
    }

    private void ListMembers(StringBuilder sb, FieldNode list, FieldNode el, string section, string owner)
    {
        string type, make;
        if (el.IsLeaf || el.Row?.EffectiveKind == "configs")
        {
            type = el.IsLeaf ? Leaf(el.Row) : Nodes + "ConfigValues";
            make = "new " + type + "(" + GameIdsGenerator.Lit(section) + ", {0})";
        }
        else
        {
            type = Nodes + Struct(el, section, owner + "_Element");
            make = "new " + type + "({0})";
        }
        sb.Append("            /// <summary>Element ").Append(list.FixedCount > 0 ? "0 to " + (list.FixedCount - 1).ToString(CultureInfo.InvariantCulture) : "by index").AppendLine(".</summary>");
        sb.Append("            public ").Append(type).Append(" this[int index] => ")
            .Append(string.Format(CultureInfo.InvariantCulture, make, PathHelper + ".Element(_p, index, " + list.FixedCount.ToString(CultureInfo.InvariantCulture) + ")")).AppendLine(";");
        sb.AppendLine("            /// <summary>Every element: a write changes them all.</summary>");
        sb.Append("            public ").Append(type).Append(" All => ").Append(string.Format(CultureInfo.InvariantCulture, make, "_p + \"[*]\"")).AppendLine(";");
        if (list.FixedCount == 0)
        {
            sb.AppendLine("            /// <summary>An element by what kiln.gamedata selects with: <c>Type</c>, <c>Type#k</c> (the k-th of that type in a list of variants) or the GUID of its <c>guid</c> field.</summary>");
            sb.Append("            public ").Append(type).Append(" this[string selector] => ").Append(string.Format(CultureInfo.InvariantCulture, make, "_p + \"[\" + selector + \"]\"")).AppendLine(";");
        }
    }

    private static void Doc(StringBuilder sb, string indent, FieldRow? row, string? rootName)
    {
        if (row is null)
        {
            if (rootName is not null) sb.Append(indent).Append("/// <summary><c>").Append(GameIdsGenerator.Xml(rootName)).AppendLine("</c></summary>");
            return;
        }
        sb.Append(indent).Append("/// <summary>").Append(GameIdsGenerator.Xml(row.Type)).Append(": <c>").Append(GameIdsGenerator.Xml(row.Path)).Append("</c>")
            .Append(row.Writable ? "" : ". Read-only: an identity field or a name.").AppendLine("</summary>");
    }

    /// <summary>The field type of a leaf.</summary>
    private string Leaf(FieldRow? row)
    {
        var value = row is null ? JsonNodeType : ValueType(row.EffectiveKind, row.Type, row.EnumNames);
        return (row is null || row.Writable ? FieldType : ReadOnlyFieldType) + "<" + value + ">";
    }

    private string ValueType(string kind, string? type, List<string>? enumNames)
    {
        switch (kind)
        {
            case "bool": return "bool";
            case "uint8": return "byte";
            case "sint8": return "sbyte";
            case "uint16": return "ushort";
            case "sint16": return "short";
            case "uint32": return "uint";
            case "sint32": return "int";
            case "uint64": return "ulong";
            case "sint64": return "long";
            case "float32": return "float";
            case "float64": case "number": return "double";
            case "time": return "global::System.TimeSpan";
            case "guid": return "global::System.Guid";
            case "ref": case "string": return "string";
            case "enum":
            case "flags":
                return type is not null && enumNames is not null && _enums.TryGetValue(type, out var e) ? Enums + e.Ident : JsonNodeType;
            default:
                return JsonNodeType;
        }
    }

    // ---------------------------------------------------------------- configs

    private void ConfigStruct(StringBuilder sb)
    {
        sb.AppendLine("        /// <summary>Config values by the names the game's Impact programs give them. A name several config ids share resolves to the one the entry holds; if it holds none, use <see cref=\"ById{T}\"/>.</summary>");
        sb.AppendLine("        public readonly struct ConfigValues");
        sb.AppendLine("        {");
        sb.AppendLine("            private readonly string _s, _p;");
        sb.AppendLine("            public ConfigValues(string section, string path) { _s = section; _p = path; }");
        sb.AppendLine("            /// <summary>The path of this list of configs.</summary>");
        sb.AppendLine("            public string Path => _p;");
        sb.AppendLine("            public override string ToString() => _p;");
        sb.AppendLine("            /// <summary>This list as one value (JSON, as in a data pack).</summary>");
        sb.Append("            public ").Append(FieldType).Append('<').Append(JsonNodeType).AppendLine("> Self => new(_s, _p);");
        sb.AppendLine("            /// <summary>A config by a name this game does not list, such as one your own Impact program declares (<c>mymod.Radius</c>).</summary>");
        sb.Append("            public ").Append(FieldType).AppendLine("<T> Named<T>(string name) => new(_s, _p + \"[\" + name + \"]\");");
        sb.AppendLine("            /// <summary>A config by its id.</summary>");
        sb.Append("            public ").Append(FieldType).AppendLine("<T> ById<T>(uint id) => new(_s, _p + \".0x\" + id.ToString(\"x8\"));");
        var used = Reserved("ConfigValues");
        var byName = _cat.Configs.Where(c => c.Names.Count > 0).GroupBy(c => c.Names[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in byName)
        {
            var name = g.First().Names[0];
            var ident = Unique(used, Pascal(name));
            var type = ConfigValueType(g.ToList());
            var ids = string.Join(", ", g.Select(c => c.Id));
            var others = g.SelectMany(c => c.Names).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            sb.Append("            /// <summary>").Append(GameIdsGenerator.Xml((g.First().Type ?? "a value") + ", " + ids + (others.Count > 0 ? "; also called " + string.Join(", ", others) : "") + "."))
                .AppendLine("</summary>");
            sb.Append("            public ").Append(FieldType).Append('<').Append(type).Append("> ").Append(ident).Append(" => new(_s, _p + ").Append(GameIdsGenerator.Lit("." + name)).AppendLine(");");
        }
        sb.AppendLine("        }");
    }

    private string ConfigValueType(List<ConfigRow> ids)
    {
        var types = ids.Select(c => ValueType(c.Kind ?? "number", c.Type, c.EnumNames)).Distinct(StringComparer.Ordinal).ToList();
        if (types.Count == 1) return types[0];
        return types.All(t => t is "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "float" or "double") ? "double" : JsonNodeType;
    }

    // ---------------------------------------------------------------- enums

    private void CollectEnums()
    {
        void Add(string? type, string? kind, List<string>? names, List<double>? numbers)
        {
            if (type is null || names is null || names.Count == 0 || _enums.ContainsKey(type)) return;
            var flags = kind == "flags";
            if (flags && (names.Count > 64 || (numbers?.Any(n => n >= 64) ?? false))) return;
            _enums[type] = new EnumDef(type, flags, names, numbers);
        }
        foreach (var list in _cat.Fields.Values)
            foreach (var r in list)
                Add(r.Type, r.EffectiveKind, r.EnumNames, r.EnumNumbers);
        foreach (var c in _cat.Configs)
            Add(c.Type, c.Kind, c.EnumNames, c.EnumNumbers);
        // the short name when it is free, the qualified one when two types share it
        var bare = _enums.Values.GroupBy(e => ShortEnumName(e.Type), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal) { "GameEnums" };
        foreach (var e in _enums.Values.OrderBy(x => x.Type, StringComparer.Ordinal))
        {
            var s = ShortEnumName(e.Type);
            var want = bare[s] > 1 ? Pascal(GameIdsGenerator.Ident(e.Type.Replace("::", "_"))) : GameIdsGenerator.Ident(s);
            e.Ident = Unique(used, want);
        }
    }

    /// <summary><c>actor::StateMask</c> is StateMask; <c>Bitmask8&lt;ecs::ClientFlag&gt;</c>, an unnamed set of flags, is ClientFlagMask.</summary>
    private static string ShortEnumName(string type)
    {
        const string bitmask = "Bitmask";
        if (type.StartsWith(bitmask, StringComparison.Ordinal) && type.IndexOf('<') is var open && open > 0)
        {
            var inner = type.Substring(open + 1).TrimEnd('>');
            return ShortEnumName(inner) + "Mask";
        }
        var i = type.LastIndexOf("::", StringComparison.Ordinal);
        return i < 0 ? type : type.Substring(i + 2);
    }

    private string EnumsSource(StringBuilder sb, string build)
    {
        sb.AppendLine("namespace Kiln.GameIds");
        sb.AppendLine("{");
        sb.Append("    /// <summary>").Append(GameIdsGenerator.Xml($"The game's enums and flag sets, from the local catalog of {build}.")).AppendLine("</summary>");
        sb.AppendLine("    public static partial class GameEnums");
        sb.AppendLine("    {");
        foreach (var e in _enums.Values.OrderBy(x => x.Ident, StringComparer.Ordinal))
        {
            sb.Append("        /// <summary>").Append(GameIdsGenerator.Xml((e.Flags ? "Flags of " : "") + e.Type)).AppendLine("</summary>");
            if (e.Flags) sb.AppendLine("        [global::System.Flags]");
            var underlying = e.Flags || (e.Numbers?.Any(n => n > int.MaxValue) ?? false) ? " : ulong" : "";
            sb.Append("        public enum ").Append(e.Ident).AppendLine(underlying);
            sb.AppendLine("        {");
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < e.Names.Count; i++)
            {
                var name = e.Names[i];
                var ident = Unique(used, GameIdsGenerator.Ident(name));
                if (ident.TrimStart('@') != name)
                    sb.Append("            [global::System.Runtime.Serialization.EnumMember(Value = ").Append(GameIdsGenerator.Lit(name)).AppendLine(")]");
                var number = e.Numbers is not null && i < e.Numbers.Count ? (ulong)e.Numbers[i] : (ulong)i;
                sb.Append("            ").Append(ident).Append(" = ");
                if (e.Flags) sb.Append("1UL << ").Append(number.ToString(CultureInfo.InvariantCulture));
                else sb.Append(number.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(",");
            }
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>An identifier for a game name: its first letter upper case.</summary>
    internal static string Pascal(string name)
    {
        var id = GameIdsGenerator.Ident(name);
        if (id.Length > 0 && id[0] == '@') id = id.Substring(1);
        return id.Length > 0 && char.IsLower(id[0]) ? char.ToUpperInvariant(id[0]) + id.Substring(1) : id;
    }
}
