// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Kiln.GameIds;

/// <summary>
/// Game ids as C# constants, from the local catalog (docs/kiln/modding/catalogs.md). Classes are named Game* so they
/// never clash with Kiln's own types (Kiln.Items, Kiln.Skills) when a mod imports both namespaces:
/// <c>Kiln.GameIds.GameItems.Weapon_T1_1H_SwordEpic_01</c> = <c>"Weapon_T1_1H_SwordEpic_01"</c>, with the game's English
/// name and description as XML docs. With <c>Fields</c> also the game's data structures as typed paths
/// (<see cref="GameFieldsEmitter"/>). Input: the AdditionalFiles item with metadata <c>KilnCatalog=true</c> (added by
/// Kiln.Sdk's Kiln.GameIds.targets) and the build property <c>KilnGameIds</c> (sections: <c>Items;Perks;...</c>,
/// <c>Fields</c> or <c>Fields.Items</c>, <c>All</c>, <c>None</c>; default <see cref="DefaultSections"/>).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class GameIdsGenerator : IIncrementalGenerator
{
    public const string DefaultSections = "Items;Perks;Buffs;Skills;Workshops";

    internal static readonly DiagnosticDescriptor NoCatalog = new(
        "KILN190", "No game-data catalog",
        "No game-data catalog found: Kiln.GameIds constants were not generated. Run `kiln catalog build` (or set KilnCatalog / KILN_CATALOG), then rebuild.",
        "Kiln.GameIds", DiagnosticSeverity.Info, isEnabledByDefault: true,
        helpLinkUri: "https://kiln.invalid/docs/modding/catalogs#kiln190");

    internal static readonly DiagnosticDescriptor BadCatalog = new(
        "KILN191", "Unreadable game-data catalog",
        "The game-data catalog {0} could not be read ({1}); Kiln.GameIds constants were not generated. Run `kiln catalog build --force`.",
        "Kiln.GameIds", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: "https://kiln.invalid/docs/modding/catalogs#kiln191");

    internal static readonly DiagnosticDescriptor UnknownSection = new(
        "KILN192", "Unknown KilnGameIds section",
        "KilnGameIds names '{0}', which is not a section Kiln.GameIds generates ({1})",
        "Kiln.GameIds", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: "https://kiln.invalid/docs/modding/catalogs#kiln192");

    internal static readonly DiagnosticDescriptor FieldsNeedContent = new(
        "KILN193", "GameFields need Kiln.Content",
        "KilnGameIds asks for Fields, but this project does not reference Kiln.Content, where Field<T> lives: add Content to KilnServices",
        "Kiln.GameIds", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: "docs/kiln/modding/analyzers.md#kiln193");

    internal static readonly DiagnosticDescriptor OldCatalog = new(
        "KILN198", "Catalog made by an older Kiln",
        "The game-data catalog {0} was made by an older Kiln and lacks what GameFields need (template fields, configs, field kinds). Run `kiln catalog build`, then rebuild.",
        "Kiln.GameIds", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: "docs/kiln/modding/analyzers.md#kiln198");

    /// <summary>The catalog recipe that has everything <see cref="GameFieldsEmitter"/> uses.</summary>
    private const int FieldsRecipe = 3;

    private const string FieldsSection = "Fields";

    /// <summary>What a section becomes: the class, the constant's value (the entry's <c>name</c> or its <c>id</c>,
    /// whichever data packs accept as the key; <c>guid</c> for VFX, which have no names of their own and where a GUID
    /// always finds one), and whether identifiers come from display names (buffs and skill nodes are named
    /// <c>buff_&lt;id&gt;</c> / <c>skill_&lt;id&gt;</c> in the game).</summary>
    internal sealed class Spec
    {
        public Spec(string section, string cls, bool valueIsId, bool identFromDisplay, string what, bool valueIsGuid = false)
        {
            ValueIsGuid = valueIsGuid;
            Section = section;
            Class = cls;
            ValueIsId = valueIsId;
            IdentFromDisplay = identFromDisplay;
            What = what;
        }

        public string Section { get; }
        public string Class { get; }
        public bool ValueIsId { get; }
        public bool ValueIsGuid { get; }
        public bool IdentFromDisplay { get; }
        public string What { get; }
    }

    internal static readonly Spec[] Specs =
    [
        new("items", "GameItems", false, false, "items (keen::ItemInfo debug names; data packs' `items` keys, `base`, recipe items)"),
        new("perks", "GamePerks", false, false, "perks (keen::Perk debug names; `perks` keys)"),
        new("buffs", "GameBuffs", true, true, "buffs (`0x` buff type ids; `buffs` keys)"),
        new("skills", "GameSkills", true, true, "skill-tree nodes (`0x` node ids; `skills` keys)"),
        new("workshops", "GameWorkshops", true, false, "crafting stations (`0x` workshop ids; a recipe's Station)"),
        new("recipes", "GameRecipes", false, false, "recipes (debug names; `recipes` keys)"),
        new("templates", "GameTemplates", false, false, "entity templates (names; `templates` keys, spawns)"),
        new("sequences", "GameSequences", false, false, "action sub-sequences (names; `sequences` keys)"),
        new("sounds", "GameSounds", false, false, "sound cues (first sound's name; use the GUID in cook recipes)"),
        new("vfx", "GameVfx", false, false, "VFX, named after what uses them (GUIDs: VfxId.Of, VfxRef.Game)", valueIsGuid: true),
    ];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sections = context.AnalyzerConfigOptionsProvider.Select(static (p, _) =>
        {
            // KilnGameIdsList: Kiln.GameIds.targets passes KilnGameIds with ',' (';' starts a comment in .editorconfig)
            if (!p.GlobalOptions.TryGetValue("build_property.KilnGameIdsList", out var v) || string.IsNullOrWhiteSpace(v))
                p.GlobalOptions.TryGetValue("build_property.KilnGameIds", out v);
            p.GlobalOptions.TryGetValue("build_property.UsingKilnSdk", out var sdk);
            return (Sections: string.IsNullOrWhiteSpace(v) ? DefaultSections : v!.Trim(), Sdk: sdk == "true");
        });
        var catalogs = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(static pair => pair.Right.GetOptions(pair.Left).TryGetValue("build_metadata.AdditionalFiles.KilnCatalog", out var v) &&
                                  string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            .Select(static (pair, ct) => (Path: pair.Left.Path, Text: pair.Left.GetText(ct)?.ToString()))
            .Collect();
        var hasFieldType = context.CompilationProvider.Select(static (c, _) => c.GetTypeByMetadataName("Kiln.Field`1") is not null);
        context.RegisterSourceOutput(catalogs.Combine(sections).Combine(hasFieldType), static (spc, input) =>
        {
            var ((files, opts), fieldType) = input;
            Run(spc, files, opts.Sections, opts.Sdk, fieldType);
        });
    }

    private static void Run(SourceProductionContext spc, ImmutableArray<(string Path, string? Text)> files, string sectionsProp, bool sdk, bool fieldType)
    {
        if (string.Equals(sectionsProp, "none", StringComparison.OrdinalIgnoreCase) || sectionsProp == "false") return;
        var wanted = new List<Spec>();
        bool fieldsAsked = false, fieldsImplied = false;
        var fieldSections = new List<string>();   // Fields.Items: that section's paths only
        foreach (var raw in sectionsProp.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim();
            if (s.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                wanted.AddRange(Specs.Where(x => !wanted.Contains(x)));
                fieldsImplied = true;
                continue;
            }
            if (s.Equals(FieldsSection, StringComparison.OrdinalIgnoreCase) || s.Equals("GameFields", StringComparison.OrdinalIgnoreCase))
            {
                fieldsAsked = true;
                continue;
            }
            if (s.StartsWith(FieldsSection + ".", StringComparison.OrdinalIgnoreCase))
            {
                fieldSections.Add(s.Substring(FieldsSection.Length + 1));
                continue;
            }
            var spec = Specs.FirstOrDefault(x => x.Class.Equals(s, StringComparison.OrdinalIgnoreCase) || x.Section.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                                                 x.Class.Equals("Game" + s, StringComparison.OrdinalIgnoreCase));
            if (spec is null)
                spc.ReportDiagnostic(Diagnostic.Create(UnknownSection, Location.None, s, string.Join(", ", Specs.Select(x => x.Class.Substring(4)).Concat(new[] { FieldsSection }))));
            else if (!wanted.Contains(spec)) wanted.Add(spec);
        }
        var file = files.Where(f => f.Text is not null).OrderBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
        if (file.Text is null)
        {
            if (sdk) spc.ReportDiagnostic(Diagnostic.Create(NoCatalog, Location.None));
            return;
        }
        if ((fieldsAsked || fieldSections.Count > 0) && !fieldType) spc.ReportDiagnostic(Diagnostic.Create(FieldsNeedContent, Location.None));
        var fields = fieldType && (fieldsAsked || fieldsImplied || fieldSections.Count > 0);
        var want = new HashSet<string>(wanted.Select(w => w.Section), StringComparer.Ordinal);
        if (fields) want.Add("configs");
        Catalog cat;
        try
        {
            cat = Catalog.Read(file.Text, want, fields);
        }
        catch (Exception e)
        {
            spc.ReportDiagnostic(Diagnostic.Create(BadCatalog, Location.None, file.Path, e.Message));
            return;
        }
        spc.AddSource("Kiln.GameIds.GameCatalog.g.cs", SourceText.From(CatalogInfo(cat, file.Path), Encoding.UTF8));
        foreach (var spec in wanted)
        {
            if (!cat.Sections.TryGetValue(spec.Section, out var entries)) continue;
            spc.AddSource($"Kiln.GameIds.{spec.Class}.g.cs", SourceText.From(Section(spec, entries, cat), Encoding.UTF8));
        }
        if (!fields) return;
        if (cat.Recipe < FieldsRecipe) spc.ReportDiagnostic(Diagnostic.Create(OldCatalog, Location.None, file.Path));
        var fieldCatalog = FieldCatalog.From(cat);
        HashSet<string>? only = null;
        if (!fieldsAsked && !fieldsImplied)
        {
            only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in fieldSections)
                if (fieldCatalog.Fields.ContainsKey(name.ToLowerInvariant())) only.Add(name);
                else spc.ReportDiagnostic(Diagnostic.Create(UnknownSection, Location.None, FieldsSection + "." + name, string.Join(", ", fieldCatalog.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => FieldsSection + "." + GameFieldsEmitter.Pascal(k)))));
        }
        foreach (var (hint, source) in new GameFieldsEmitter(fieldCatalog, only).Emit(Header, BuildText(cat)))
            spc.AddSource(hint, SourceText.From(source, Encoding.UTF8));
    }

    private static string BuildText(Catalog cat)
    {
        cat.Game.TryGetValue("side", out var side);
        cat.Game.TryGetValue("steamBuild", out var steam);
        cat.Game.TryGetValue("svn", out var svn);
        return (side ?? "game") + (steam is { Length: > 0 } ? " " + steam : "") + (svn is { Length: > 0 } ? " (data " + svn + ")" : "");
    }

    // ---------------------------------------------------------------- the catalog (only what the generator needs)

    internal sealed class Catalog
    {
        public Dictionary<string, string> Game { get; } = new(StringComparer.Ordinal);
        public string? Generated { get; set; }
        public Dictionary<string, List<Dictionary<string, object?>>> Sections { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object?> Fields { get; private set; } = new(StringComparer.Ordinal);
        public int Recipe { get; private set; }

        public static Catalog Read(string text, HashSet<string> want, bool wantFields = false)
        {
            var c = new Catalog();
            var j = new MiniJson(text);
            string? kind = null;
            j.Object(key =>
            {
                switch (key)
                {
                    case "kind":
                        kind = j.Value() as string;
                        break;
                    case "generated":
                        c.Generated = j.Value() as string;
                        break;
                    case "recipe":
                        c.Recipe = j.Value() is double r ? (int)r : 0;
                        break;
                    case "fields" when wantFields:
                        if (j.Value() is Dictionary<string, object?> f) c.Fields = f;
                        break;
                    case "game":
                        if (j.Value() is Dictionary<string, object?> g)
                            foreach (var kv in g)
                                if (kv.Value is string s) c.Game[kv.Key] = s;
                        break;
                    case "sections":
                        j.Object(sec =>
                        {
                            if (!want.Contains(sec))
                            {
                                j.SkipValue();
                                return;
                            }
                            var list = new List<Dictionary<string, object?>>();
                            j.Array(() =>
                            {
                                if (j.Value() is Dictionary<string, object?> e) list.Add(e);
                            });
                            c.Sections[sec] = list;
                        });
                        break;
                    default:
                        j.SkipValue();
                        break;
                }
            });
            if (kind is not null && kind != "kiln.catalog") throw new FormatException($"not a game-data catalog (kind '{kind}')");
            return c;
        }
    }

    private static string? Str(Dictionary<string, object?> e, string k) => e.TryGetValue(k, out var v) ? v as string : null;

    private static string CatalogInfo(Catalog cat, string path)
    {
        var sb = Header();
        sb.AppendLine("namespace Kiln.GameIds");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>The game build these constants come from (the local catalog, made from your own installed game).</summary>");
        sb.AppendLine("    public static class GameCatalog");
        sb.AppendLine("    {");
        void C(string name, string? v, string doc)
        {
            sb.Append("        /// <summary>").Append(Xml(doc)).AppendLine("</summary>");
            sb.Append("        public const string ").Append(name).Append(" = ").Append(Lit(v ?? "")).AppendLine(";");
        }
        cat.Game.TryGetValue("side", out var side);
        cat.Game.TryGetValue("steamBuild", out var steam);
        cat.Game.TryGetValue("svn", out var svn);
        cat.Game.TryGetValue("kfcVersion", out var kfc);
        C("Side", side, "\"client\" or \"server\": the game the catalog was made from.");
        C("SteamBuild", steam, "Steam's build id of that game (empty when unknown).");
        C("DataBuild", svn, "Keen's data build (the first part of the .kfc version).");
        C("KfcVersion", kfc, "The whole .kfc version string.");
        C("Generated", cat.Generated, "When the catalog was made (UTC).");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static StringBuilder Header()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by Kiln.GameIds from the local game-data catalog (made from your own installed game; never commit it). </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591");
        return sb;
    }

    internal static string Section(Spec spec, List<Dictionary<string, object?>> entries, Catalog cat)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { spec.Class, "Ids", "Guids" };
        var rows = new List<(string Ident, string Value, string? Id, string? Guid, string Doc, string? Remarks)>();
        foreach (var e in entries)
        {
            var name = Str(e, "name");
            var id = Str(e, "id");
            var guid = Str(e, "guid");
            var display = Str(e, "displayName");
            var value = spec.ValueIsGuid ? guid : spec.ValueIsId ? id : name;
            if (value is null) continue;
            string? baseIdent;
            if (spec.IdentFromDisplay || name is null || LooksGenerated(name))
                baseIdent = display is { Length: > 0 } ? Ident(display) + (id is { Length: 10 } ? "_" + id.Substring(2) : "") : name is null || LooksGenerated(name) ? null : Ident(name);
            else baseIdent = Ident(name);
            if (baseIdent is null) continue;
            var ident = baseIdent;
            for (var n = 2; !used.Add(ident); n++) ident = baseIdent + "_" + n.ToString(CultureInfo.InvariantCulture);
            var facts = new List<string>();
            foreach (var k in new[] { "kind", "rarity", "family", "slot", "type", "scene" })
                if (Str(e, k) is { Length: > 0 } f) facts.Add(f);
            if (e.TryGetValue("tier", out var tier) && tier is double t) facts.Add("tier " + t.ToString(CultureInfo.InvariantCulture));
            if (e.TryGetValue("maxStack", out var ms) && ms is double m) facts.Add("stack " + m.ToString(CultureInfo.InvariantCulture));
            if (spec.ValueIsId && name is not null && name != value) facts.Add(name);
            if (Str(e, "item") is { Length: > 0 } item) facts.Add("unlocks " + item);
            if (e.TryGetValue("length", out var len) && len is double seconds) facts.Add(seconds.ToString(CultureInfo.InvariantCulture) + " s");
            if (spec.ValueIsGuid && Parameters(e) is { Length: > 0 } parameters) facts.Add("parameters " + parameters);
            if (id is not null) facts.Add(id);
            if (guid is not null) facts.Add(guid);
            var doc = display is { Length: > 0 } ? display + (facts.Count > 0 ? " (" + string.Join(", ", facts) + ")" : "") : string.Join(", ", facts);
            rows.Add((ident, value, id, guid, doc, Str(e, "description")));
        }
        var sb = Header();
        var build = BuildText(cat);
        sb.AppendLine("namespace Kiln.GameIds");
        sb.AppendLine("{");
        sb.Append("    /// <summary>").Append(Xml($"{rows.Count} {spec.What}, from the local catalog of {build}.")).AppendLine("</summary>");
        sb.Append("    public static partial class ").AppendLine(spec.Class);
        sb.AppendLine("    {");
        foreach (var r in rows)
        {
            sb.Append("        /// <summary>").Append(Xml(r.Doc)).AppendLine("</summary>");
            if (r.Remarks is { Length: > 0 }) sb.Append("        /// <remarks>").Append(Xml(r.Remarks)).AppendLine("</remarks>");
            sb.Append("        public const string ").Append(r.Ident).Append(" = ").Append(Lit(r.Value)).AppendLine(";");
        }
        if (rows.Any(r => r.Id is { Length: 10 }))
        {
            sb.Append("        /// <summary>The numeric ids (`0x` selectors) of <see cref=\"").Append(spec.Class).AppendLine("\"/>.</summary>");
            sb.AppendLine("        public static class Ids");
            sb.AppendLine("        {");
            foreach (var r in rows.Where(r => r.Id is { Length: 10 }))
                sb.Append("            public const uint ").Append(r.Ident).Append(" = ").Append(r.Id).AppendLine("u;");
            sb.AppendLine("        }");
        }
        if (rows.Any(r => r.Guid is not null))
        {
            sb.Append("        /// <summary>The GUIDs (resource and reference keys) of <see cref=\"").Append(spec.Class).AppendLine("\"/>.</summary>");
            sb.AppendLine("        public static class Guids");
            sb.AppendLine("        {");
            foreach (var r in rows.Where(r => r.Guid is not null))
                sb.Append("            public const string ").Append(r.Ident).Append(" = ").Append(Lit(r.Guid!)).AppendLine(";");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>A VFX's parameters as a short text, those with a known name.</summary>
    private static string Parameters(Dictionary<string, object?> e)
    {
        if (!e.TryGetValue("parameters", out var v) || v is not List<object?> list) return "";
        var named = list.OfType<Dictionary<string, object?>>().Where(p => Str(p, "name") is not null).Select(p => $"{Str(p, "name")}: {Str(p, "type")}");
        return string.Join(", ", named);
    }

    /// <summary>A GUID-named or <c>guid#k</c> entry: no useful identifier.</summary>
    private static bool LooksGenerated(string name) => name.IndexOf('#') >= 0 || (name.Length == 36 && name.Count(c => c == '-') == 4);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>A valid C# identifier for a game name: letters, digits and '_' kept (others become '_', runs
    /// collapsed), a leading digit gets '_', keywords get '@'.</summary>
    internal static string Ident(string s)
    {
        var sb = new StringBuilder(s.Length + 1);
        foreach (var ch in s)
        {
            if (ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_')) sb.Append(ch);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
        }
        while (sb.Length > 1 && sb[sb.Length - 1] == '_' && !s.EndsWith("_", StringComparison.Ordinal)) sb.Length--;
        if (sb.Length == 0) sb.Append('_');
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        var id = sb.ToString();
        return Keywords.Contains(id) ? "@" + id : id;
    }

    internal static string Lit(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    internal static string Xml(string s)
    {
        var flat = s.Replace("\r", " ").Replace("\n", " ");
        return flat.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
