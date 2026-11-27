// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Kiln.Analyzers;

/// <summary>
/// Hints toward the stable tier (docs/kiln/modding/tiers.md), info level, never errors and never a refusal (D5): a mod's old
/// path that a registry field covers (KILN124: a data pack's or resource patch's member path, a constant path given to
/// ContentEntry/GameDataEntry), and code of the Native tier (KILN125: a hook by address or by a game function a hooks
/// registry's field covers, a write into memory by address). The table of what covers what is generated from the
/// registries' schemas and regmap/common (RegistryHints.g.cs, tools/registrygen), never kept by hand.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RegistryHintAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic properties the fix reads.</summary>
    public const string FieldKey = "field", SectionKey = "section", SelectorKey = "selector", FixableKey = "fixable";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.RegistryCovers, Descriptors.NativeTier);

    private static readonly Dictionary<string, string> SectionRoots = RegistryHints.Sections.ToDictionary(s => s.Section, s => s.Root, StringComparer.Ordinal);
    private static readonly ILookup<(string Root, string Path), (string Field, bool Exact)> ByPath =
        RegistryHints.Paths.ToLookup(p => (p.Root, p.Path), p => (p.Field, p.Exact));
    private static readonly Dictionary<string, string> ByFunction = RegistryHints.Hooks
        .GroupBy(h => h.Function, StringComparer.Ordinal).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(h => h.Field)), StringComparer.Ordinal);
    private static readonly Regex Index = new(@"\[[^\]]*\]", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Ops = new(StringComparer.Ordinal) { "set", "mul", "add", "min", "max", "raiseOnly", "lowerOnly", "reset", "yield", "exclusive" };

    /// <summary>The registry field covering a path below a root, and whether it is the same value one to one; null when none.
    /// A list element ("altarsPerFlameLevel[2]") is covered by its list's field, never one to one.</summary>
    internal static (string Field, bool Exact)? Covering(string root, string path)
    {
        var p = path.ToLowerInvariant();
        foreach (var hit in ByPath[(root, p)]) return hit;
        var generic = Index.Replace(p, "[]");
        foreach (var hit in ByPath[(root, generic)]) return (hit.Field, hit.Exact && generic == p);
        if (generic.EndsWith("[]", StringComparison.Ordinal))
            foreach (var hit in ByPath[(root, generic.Substring(0, generic.Length - 2))]) return (hit.Field, false);
        // a resource patch on an array entry's resource names the element through its array
        foreach (var (section, sroot) in RegistryHints.Sections)
        {
            var slash = sroot.IndexOf('/');
            if (slash < 0 || sroot.Substring(0, slash) != root) continue;
            var arr = sroot.Substring(slash + 1).ToLowerInvariant();  // "recipes[]"
            if (generic.StartsWith(arr + ".", StringComparison.Ordinal))
                foreach (var hit in ByPath[(sroot, generic.Substring(arr.Length + 1))]) return (hit.Field, false);
        }
        return null;
    }

    /// <summary>The Kiln field a hooked game function's name stands for (the part after the last '/' or "::"), or null.</summary>
    internal static string? CoveringFunction(string symbol)
    {
        var name = symbol;
        foreach (var sep in new[] { "/", "::" })
        {
            var i = name.LastIndexOf(sep, StringComparison.Ordinal);
            if (i >= 0) name = name.Substring(i + sep.Length);
        }
        return ByFunction.TryGetValue(name.ToLowerInvariant(), out var f) ? f : null;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var c = start.Compilation;
            var kinds = new Kinds(c);
            start.RegisterAdditionalFileAction(DataFile);
            if (kinds.Any) start.RegisterOperationAction(ctx => Invocation(ctx, kinds), OperationKind.Invocation);
            if (kinds.Patch is not null)
            {
                start.RegisterOperationAction(ctx => PatchCreation(ctx, kinds), OperationKind.ObjectCreation);
                start.RegisterOperationAction(ctx => PatchIndexer(ctx, kinds), OperationKind.PropertyReference);
            }
        });
    }

    private sealed class Kinds
    {
        public Kinds(Compilation c)
        {
            ContentEntry = c.GetTypeByMetadataName("Kiln.ContentEntry");
            GameDataEntry = c.GetTypeByMetadataName("Kiln.GameDataEntry");
            Hooks = c.GetTypeByMetadataName("Kiln.Hooks");
            Symbols = c.GetTypeByMetadataName("Kiln.Symbols");
            Memory = c.GetTypeByMetadataName("Kiln.Memory");
            Marshal = c.GetTypeByMetadataName("System.Runtime.InteropServices.Marshal");
            Patch = c.GetTypeByMetadataName("Kiln.ResourcePatchBlock");
        }
        public INamedTypeSymbol? ContentEntry, GameDataEntry, Hooks, Symbols, Memory, Marshal, Patch;
        public bool Any => ContentEntry is not null || GameDataEntry is not null || Hooks is not null || Memory is not null || Marshal is not null;
        public bool IsEntry(ITypeSymbol? t) => t is not null && (Same(t, ContentEntry) || Same(t, GameDataEntry));
        public static bool Same(ISymbol? a, ISymbol? b) => a is not null && b is not null && SymbolEqualityComparer.Default.Equals(a, b);
    }

    private static readonly HashSet<string> EntryPathMethods = new(StringComparer.Ordinal) { "Get", "Original", "GetNumber", "GetJson", "GetRef", "Set", "SetJson", "Reset" };

    // ---------------------------------------------------------------- C#

    private static void Invocation(OperationAnalysisContext ctx, Kinds kinds)
    {
        var inv = (IInvocationOperation)ctx.Operation;
        var m = inv.TargetMethod;
        var type = m.ContainingType;
        if (kinds.IsEntry(type) && EntryPathMethods.Contains(m.Name) && m.Parameters.Length > 0 && m.Parameters[0].Type.SpecialType == SpecialType.System_String)
        {
            var arg = inv.Arguments.FirstOrDefault(a => a.Parameter is not null && SymbolEqualityComparer.Default.Equals(a.Parameter, m.Parameters[0]));
            if (arg?.Value.ConstantValue is not { HasValue: true, Value: string path }) return;
            var section = SectionOf(inv.Instance, 0);
            if (section is null || !SectionRoots.TryGetValue(section, out var root)) return;
            if (Covering(root, path) is { } hit)
                ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.RegistryCovers, arg.Value.Syntax.GetLocation(), path, section, hit.Field));
            return;
        }
        if (Kinds.Same(type, kinds.Hooks) && m.Name is "Mid" or "Detour" && inv.Arguments.Length > 0)
        {
            var first = inv.Arguments[0].Value;
            if (first.ConstantValue is { HasValue: true, Value: string symbol })
            {
                if (CoveringFunction(symbol) is { } field)
                    ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.NativeTier, inv.Syntax.GetLocation(), $"Hooks.{m.Name}(\"{symbol}\")",
                        "hooks a game function", $"; the registry field {field} covers it: set that field instead, and the hook is Kiln's to keep"));
                return;
            }
            if (first.Type?.SpecialType is SpecialType.System_UIntPtr or SpecialType.System_IntPtr or SpecialType.System_UInt64 or SpecialType.System_Int64)
                ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.NativeTier, inv.Syntax.GetLocation(), $"Hooks.{m.Name}(<address>)",
                    "hooks the game's code by address", Advice));
            return;
        }
        if (Kinds.Same(type, kinds.Symbols) && m.Name is "Get" or "TryGet" && inv.Arguments.Length > 0 &&
            inv.Arguments[0].Value.ConstantValue is { HasValue: true, Value: string name } && CoveringFunction(name) is { } covered)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.NativeTier, inv.Syntax.GetLocation(), $"Symbols.{m.Name}(\"{name}\")",
                "finds a game function", $"; the registry field {covered} covers it: set that field instead"));
            return;
        }
        if (Kinds.Same(type, kinds.Memory) && m.Name is "Write" or "Patch")
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.NativeTier, inv.Syntax.GetLocation(), $"Memory.{m.Name}", "writes into the game's memory by address", Advice));
            return;
        }
        if (Kinds.Same(type, kinds.Marshal) && (m.Name.StartsWith("Write", StringComparison.Ordinal) || (m.Name == "Copy" && m.Parameters.Length == 4 && m.Parameters[2].Type.SpecialType == SpecialType.System_IntPtr)))
        {
            var target = m.Name == "Copy" ? inv.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 2)?.Value : inv.Arguments.FirstOrDefault()?.Value;
            if (target is null || OwnAllocation(target)) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.NativeTier, inv.Syntax.GetLocation(), $"Marshal.{m.Name}",
                "writes into memory by address (if that address is the game's)", Advice));
        }
    }

    // ---- C# resource patches (OnBoot): new ResourcePatchBlock { Type = "keen::BalancingTable", Set = { ["path"] = v } }, and
    // block.Set["path"] = v on a local made that way

    private static void PatchCreation(OperationAnalysisContext ctx, Kinds kinds)
    {
        var create = (IObjectCreationOperation)ctx.Operation;
        if (!Kinds.Same(create.Type, kinds.Patch) || create.Initializer is null || PatchType(create) is not { } type) return;
        foreach (var init in create.Initializer.Initializers)
            if (init is IMemberInitializerOperation { InitializedMember: IPropertyReferenceOperation { Property.Name: "Set" }, Initializer: { } set })
                foreach (var e in set.Initializers)
                    if (e is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } target })
                        PatchKey(ctx, type, target.Arguments[0].Value);
    }

    private static void PatchIndexer(OperationAnalysisContext ctx, Kinds kinds)
    {
        var p = (IPropertyReferenceOperation)ctx.Operation;
        if (!p.Property.IsIndexer || p.Arguments.Length != 1 || p.Instance is not IPropertyReferenceOperation { Property.Name: "Set" } set ||
            !Kinds.Same(set.Property.ContainingType, kinds.Patch)) return;
        var owner = set.Instance;
        while (owner is IConversionOperation c) owner = c.Operand;
        if (owner is not ILocalReferenceOperation local || local.SemanticModel is not { } model) return;
        foreach (var r in local.Local.DeclaringSyntaxReferences)
            if (r.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } init } && init.SyntaxTree == model.SyntaxTree &&
                model.GetOperation(init) is IObjectCreationOperation create && Kinds.Same(create.Type, kinds.Patch) && PatchType(create) is { } type)
                PatchKey(ctx, type, p.Arguments[0].Value);
    }

    private static string? PatchType(IObjectCreationOperation create)
    {
        foreach (var init in create.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
            if (init is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "Type" }, Value.ConstantValue: { HasValue: true, Value: string t } })
                return t;
        return null;
    }

    private static void PatchKey(OperationAnalysisContext ctx, string type, IOperation key)
    {
        if (key.ConstantValue is not { HasValue: true, Value: string path } || Covering(type, path) is not { } hit) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.RegistryCovers, key.Syntax.GetLocation(), path, "a resource patch of " + Short(type), hit.Field));
    }

    private const string Advice = "; a Kiln registry field or service covers much of the game's data and rules (docs/kiln/modding/tiers.md), and Kiln keeps those working across game updates";

    /// <summary>A pointer the code itself allocated (a local or field from Marshal.Alloc*, NativeMemory.Alloc*): not the game's.</summary>
    private static bool OwnAllocation(IOperation op)
    {
        while (op is IConversionOperation conv) op = conv.Operand;
        ISymbol? sym = op switch { ILocalReferenceOperation l => l.Local, IFieldReferenceOperation f => f.Field, _ => null };
        if (sym is null || op.SemanticModel is not { } model) return false;
        foreach (var r in sym.DeclaringSyntaxReferences)
        {
            var node = r.GetSyntax();
            var init = node switch { VariableDeclaratorSyntax v => v.Initializer?.Value, _ => null };
            if (init is null || node.SyntaxTree != model.SyntaxTree) continue;
            if (model.GetOperation(init) is IInvocationOperation alloc && alloc.TargetMethod.Name.StartsWith("Alloc", StringComparison.Ordinal) &&
                alloc.TargetMethod.ContainingType.Name is "Marshal" or "NativeMemory")
                return true;
        }
        return false;
    }

    /// <summary>The data section of an entry expression (<c>Content.Items.Get(x)</c>, <c>Content.Templates.Get(x)</c>, a local
    /// holding one); null when the code does not say.</summary>
    private static string? SectionOf(IOperation? op, int depth)
    {
        if (op is null || depth > 12) return null;
        while (op is IConversionOperation c) op = c.Operand;
        switch (op)
        {
            case IPropertyReferenceOperation p:
                if (p.Instance is null && p.Property.ContainingType is { Name: "Content", ContainingNamespace.Name: "Kiln" })
                    return p.Property.Name switch
                    {
                        "Templates" => "templates", "Arsenals" => "arsenals", "Sequences" => "sequences", "Buffs" => "buffs", "Perks" => "perks",
                        "Skills" => "skills", "ItemData" or "Items" => "items", "Recipes" => "recipes", "Loot" => "loot", "Drops" => "drops",
                        _ => null,
                    };
                return p.Property.IsIndexer ? SectionOf(p.Instance, depth + 1) : null;
            case IInvocationOperation inv:
                var from = inv.Instance ?? (inv.TargetMethod.IsExtensionMethod && inv.Arguments.Length > 0 ? inv.Arguments[0].Value : null);
                return from is null ? null : SectionOf(from, depth + 1);
            case IConditionalAccessInstanceOperation:
                for (var a = op.Parent; a is not null; a = a.Parent)
                    if (a is IConditionalAccessOperation ca) return SectionOf(ca.Operation, depth + 1);
                return null;
            case ILocalReferenceOperation local:
                var model = local.SemanticModel;
                if (model is null || local.Local.DeclaringSyntaxReferences.Length != 1) return null;
                var decl = local.Local.DeclaringSyntaxReferences[0].GetSyntax();
                if (decl is not VariableDeclaratorSyntax { Initializer.Value: { } init } || decl.SyntaxTree != model.SyntaxTree) return null;
                while (init is PostfixUnaryExpressionSyntax post) init = post.Operand;
                return SectionOf(model.GetOperation(init), depth + 1);
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- data packs and blocks files

    private static void DataFile(AdditionalFileAnalysisContext ctx)
    {
        var path = ctx.AdditionalFile.Path;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(path), "kiln.mod.json", StringComparison.OrdinalIgnoreCase)) return;
        if (ctx.Options.AnalyzerConfigOptionsProvider.GetOptions(ctx.AdditionalFile).TryGetValue("build_metadata.AdditionalFiles.KilnCatalog", out var cat) &&
            string.Equals(cat, "true", StringComparison.OrdinalIgnoreCase)) return;
        var text = ctx.AdditionalFile.GetText(ctx.CancellationToken);
        if (text is null) return;
        var doc = JsonSpans.Parse(text.ToString());
        if (doc is not { Type: JsonSpans.Kind.Object }) return;
        foreach (var top in doc.Members)
        {
            if (top.Key == "resourcePatches" && top.Value.Type == JsonSpans.Kind.Object)
            {
                foreach (var patch in top.Value.Members)
                {
                    if (patch.Value.Find("type")?.Value is not { Type: JsonSpans.Kind.String } t || patch.Value.Find("set")?.Value is not { Type: JsonSpans.Kind.Object } set) continue;
                    foreach (var m in set.Members) Report(ctx, text, path, m, t.Text, "a resource patch of " + Short(t.Text), null, null);
                }
                continue;
            }
            if (top.Key == "registry" || !SectionRoots.TryGetValue(top.Key, out var root) || top.Value.Type != JsonSpans.Kind.Object) continue;
            foreach (var entry in top.Value.Members)
            {
                if (entry.Value.Type != JsonSpans.Kind.Object || entry.Key == "add") continue;
                Walk(ctx, text, path, entry.Value, "", root, top.Key, entry.Key);
            }
        }
    }

    private static void Walk(AdditionalFileAnalysisContext ctx, SourceText text, string file, JsonSpans obj, string prefix, string root, string section, string selector)
    {
        foreach (var m in obj.Members)
        {
            if (m.Key.StartsWith("@", StringComparison.Ordinal)) continue;
            var key = prefix + m.Key;
            // an object of ops is a value; any other object sets a struct's members
            if (m.Value.Type == JsonSpans.Kind.Object && m.Value.Members.Count > 0 && !m.Value.Members.All(x => Ops.Contains(x.Key)))
            {
                Walk(ctx, text, file, m.Value, key + ".", root, section, selector);
                continue;
            }
            if (prefix.Length == 0) Report(ctx, text, file, m, root, section, section, selector);
            else ReportNested(ctx, text, file, m, key, root, section);
        }
    }

    private static void ReportNested(AdditionalFileAnalysisContext ctx, SourceText text, string file, JsonSpans.Member m, string key, string root, string section)
    {
        if (Covering(root, key) is not { } hit) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.RegistryCovers, Where(text, file, m), key, section, hit.Field));
    }

    private static void Report(AdditionalFileAnalysisContext ctx, SourceText text, string file, JsonSpans.Member m, string root, string where,
        string? section, string? selector)
    {
        if (Covering(root, m.Key) is not { } hit) return;
        var props = ImmutableDictionary<string, string?>.Empty.Add(FieldKey, hit.Field);
        // one to one, a plain value or {"set": v}, an entry named by its own key: the fix moves it into the registry section
        var plain = m.Value.Type is JsonSpans.Kind.Number or JsonSpans.Kind.Literal ||
                    (m.Value.Type == JsonSpans.Kind.Object && m.Value.Members.Count == 1 && m.Value.Members[0].Key == "set" &&
                     m.Value.Members[0].Value.Type is JsonSpans.Kind.Number or JsonSpans.Kind.Literal);
        if (hit.Exact && plain && section is not null && selector is not null && m.Value.Text != "null" &&
            hit.Field.StartsWith(section + ".", StringComparison.Ordinal) && !selector.Contains('*'))
            props = props.Add(FixableKey, "true").Add(SectionKey, section).Add(SelectorKey, selector);
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.RegistryCovers, Where(text, file, m), props, m.Key, where, hit.Field));
    }

    private static Location Where(SourceText text, string file, JsonSpans.Member m)
    {
        var span = TextSpan.FromBounds(m.KeyStart, m.KeyEnd);
        return Location.Create(file, span, text.Lines.GetLinePositionSpan(span));
    }

    private static string Short(string type) => type.Substring(type.LastIndexOf(':') + 1);
}
