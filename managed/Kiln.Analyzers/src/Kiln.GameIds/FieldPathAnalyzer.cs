// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Kiln.GameIds;

/// <summary>
/// Checks the string paths a mod gives kiln.content and kiln.gamedata against the local catalog: a typo that would fail
/// when the game runs fails the build, with what was probably meant. Only constant strings are checked, and only where the
/// section is known from the code (<c>Content.Templates.Get(...).Set("LifeTime.lifeTime", 5)</c>, through locals; a
/// <c>GameDataEntry</c> passed in as a parameter could be anything). No catalog on the machine: no checks.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FieldPathAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The Properties key of the corrected path.</summary>
    public const string SuggestionKey = "suggestion";

    private const string Category = "Kiln.GameData";
    private const string Docs = "docs/kiln/modding/analyzers.md#";

    internal static readonly DiagnosticDescriptor UnknownPath = new(
        "KILN194", "No such field in the game",
        "'{0}' is not a path of {1}: {2}{3}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Checked against the catalog made from your own game. At run time this path would be refused. GameFields gives typed paths that cannot go wrong this way.",
        helpLinkUri: Docs + "kiln194");

    internal static readonly DiagnosticDescriptor UnknownConfig = new(
        "KILN195", "Config name the game does not know",
        "'{0}' on {1}: {2}{3}",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "A mod's own Impact program can declare config names the game has never heard of, so this is a warning. Suppress it for your own names, or write them in brackets: impactValues[mymod.Radius].",
        helpLinkUri: Docs + "kiln195");

    internal static readonly DiagnosticDescriptor ReadOnlyPath = new(
        "KILN196", "Field Kiln does not change",
        "'{0}' of {1} is an identity field or a name: Kiln refuses to change it",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: Docs + "kiln196");

    internal static readonly DiagnosticDescriptor WrongSection = new(
        "KILN197", "Typed field of another section",
        "'{0}' is a field of {1}, and this entry is of {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: Docs + "kiln197");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnknownPath, UnknownConfig, ReadOnlyPath, WrongSection);

    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal) { "Get", "Original", "GetNumber", "GetJson", "GetRef", "Reset", "Fields" };
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal) { "Set", "SetJson" };

    /// <summary>One parse per additional file for as long as it lives: an editor analyzes the same file over and over.</summary>
    private static readonly ConditionalWeakTable<AdditionalText, Lazy<FieldIndex?>> Indexes = new();

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var c = start.Compilation;
            var kinds = new Types(c);
            if (kinds.GameDataEntry is null && kinds.ContentEntry is null) return;
            var provider = start.Options.AnalyzerConfigOptionsProvider;
            var file = start.Options.AdditionalFiles
                .Where(f => provider.GetOptions(f).TryGetValue("build_metadata.AdditionalFiles.KilnCatalog", out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
            if (file is null) return;
            // the catalog is read at the first path this project has, not for every compilation
            var index = Indexes.GetValue(file, static f => new Lazy<FieldIndex?>(() => Load(f)));
            start.RegisterOperationAction(ctx => Invocation(ctx, kinds, index), OperationKind.Invocation);
            start.RegisterOperationAction(ctx => Creation(ctx, kinds, index), OperationKind.ObjectCreation);
        });
    }

    private static FieldIndex? Load(AdditionalText file)
    {
        try
        {
            var text = file.GetText()?.ToString();
            return text is null ? null : new FieldIndex(FieldCatalog.Read(text));
        }
        catch (Exception)
        {
            return null;   // an unreadable catalog is the generator's to report (KILN191)
        }
    }

    private sealed class Types
    {
        public Types(Compilation c)
        {
            GameDataEntry = c.GetTypeByMetadataName("Kiln.GameDataEntry");
            ContentEntry = c.GetTypeByMetadataName("Kiln.ContentEntry");
            Content = c.GetTypeByMetadataName("Kiln.Content");
            Clone = c.GetTypeByMetadataName("Kiln.GameDataClone");
            ItemDefinition = c.GetTypeByMetadataName("Kiln.ItemDefinition");
            RecipeDefinition = c.GetTypeByMetadataName("Kiln.RecipeDefinition");
            Field = c.GetTypeByMetadataName("Kiln.Field`1");
            ReadOnlyField = c.GetTypeByMetadataName("Kiln.ReadOnlyField`1");
        }

        public INamedTypeSymbol? GameDataEntry { get; }
        public INamedTypeSymbol? ContentEntry { get; }
        public INamedTypeSymbol? Content { get; }
        public INamedTypeSymbol? Clone { get; }
        public INamedTypeSymbol? ItemDefinition { get; }
        public INamedTypeSymbol? RecipeDefinition { get; }
        public INamedTypeSymbol? Field { get; }
        public INamedTypeSymbol? ReadOnlyField { get; }

        public bool IsEntry(ITypeSymbol? t) =>
            t is not null && (SymbolEqualityComparer.Default.Equals(t, GameDataEntry) || SymbolEqualityComparer.Default.Equals(t, ContentEntry));
    }

    // ---------------------------------------------------------------- calls on entries

    private static void Invocation(OperationAnalysisContext ctx, Types kinds, Lazy<FieldIndex?> index)
    {
        var inv = (IInvocationOperation)ctx.Operation;
        var m = inv.TargetMethod;
        // a typed field on an entry of another section
        if (m.IsExtensionMethod && inv.Arguments.Length >= 2 && kinds.IsEntry(m.Parameters[0].Type) && IsTypedField(m.Parameters[1].Type, kinds))
        {
            var entrySection = SectionOf(inv.Arguments[0].Value, 0);
            var fieldSection = FieldSectionOf(inv.Arguments[1].Value);
            if (entrySection is not null && fieldSection is not null && entrySection != fieldSection)
                ctx.ReportDiagnostic(Diagnostic.Create(WrongSection, inv.Arguments[1].Syntax.GetLocation(), inv.Arguments[1].Syntax.ToString(), fieldSection, entrySection));
            return;
        }
        var isConfig = m.Name == "Config" && m.Parameters.Length >= 1 && m.Parameters[m.IsExtensionMethod ? 1 : 0].Type.SpecialType == SpecialType.System_String;
        IOperation? receiver;
        int pathArg;
        if (m.IsExtensionMethod)
        {
            if (!isConfig || inv.Arguments.Length < 2 || !kinds.IsEntry(m.Parameters[0].Type)) return;
            receiver = inv.Arguments[0].Value;
            pathArg = 1;
        }
        else
        {
            if (!kinds.IsEntry(m.ContainingType) || (!isConfig && !Reads.Contains(m.Name) && !Writes.Contains(m.Name))) return;
            if (m.Parameters.Length == 0 || m.Parameters[0].Type.SpecialType != SpecialType.System_String) return;
            receiver = inv.Instance;
            pathArg = 0;
        }
        var arg = inv.Arguments.FirstOrDefault(a => a.Parameter is not null && SymbolEqualityComparer.Default.Equals(a.Parameter, m.Parameters[pathArg]));
        if (arg is null || arg.ArgumentKind == ArgumentKind.DefaultValue) return;
        if (arg.Value.ConstantValue is not { HasValue: true, Value: string path }) return;
        var section = SectionOf(receiver, 0);
        if (section is null) return;
        if (index.Value is not { } idx || !idx.HasSection(section)) return;
        var target = isConfig ? ConfigPath(section, path) : path;
        if (target is null) return;
        Report(ctx, idx, section, target, path, arg.Value.Syntax, !isConfig && Writes.Contains(m.Name));
    }

    /// <summary>Where <c>Config(alias)</c> looks, as GameDataEntry.Config says.</summary>
    private static string? ConfigPath(string section, string alias) => section switch
    {
        "templates" => "ImpactConfiguration.configValues." + alias,
        "skills" => "configValues." + alias,
        "items" or "perks" or "buffs" => "impactValues." + alias,
        _ => null,
    };

    private static bool IsTypedField(ITypeSymbol t, Types kinds) =>
        t is INamedTypeSymbol { IsGenericType: true } n &&
        (SymbolEqualityComparer.Default.Equals(n.ConstructedFrom, kinds.Field) || SymbolEqualityComparer.Default.Equals(n.ConstructedFrom, kinds.ReadOnlyField));

    /// <summary>The section of <c>GameFields.&lt;Section&gt;.…</c>, from how the expression reaches it.</summary>
    private static string? FieldSectionOf(IOperation op)
    {
        for (var cur = op; cur is not null;)
        {
            cur = Strip(cur);
            if (cur is not IPropertyReferenceOperation p) return null;
            if (p.Instance is null)
            {
                var section = p.Property.ContainingType;
                return section.ContainingType?.Name == "GameFields" ? section.Name.ToLowerInvariant() : null;
            }
            cur = p.Instance;
        }
        return null;
    }

    // ---------------------------------------------------------------- definitions

    private static void Creation(OperationAnalysisContext ctx, Types kinds, Lazy<FieldIndex?> index)
    {
        var create = (IObjectCreationOperation)ctx.Operation;
        var type = create.Type;
        string? section = null;
        if (SymbolEqualityComparer.Default.Equals(type, kinds.ItemDefinition)) section = "items";
        else if (SymbolEqualityComparer.Default.Equals(type, kinds.RecipeDefinition)) section = "recipes";
        else if (SymbolEqualityComparer.Default.Equals(type, kinds.Clone)) section = SectionOfDefine(create);
        if (section is null || create.Initializer is null) return;
        foreach (var init in create.Initializer.Initializers)
        {
            IOperation? value = null;
            if (init is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "Fields" } } a) value = a.Value;
            else if (init is IMemberInitializerOperation { InitializedMember: IPropertyReferenceOperation { Property.Name: "Fields" } } mi) value = mi.Initializer;
            if (value is null) continue;
            foreach (var key in KeysOf(value))
            {
                if (key.ConstantValue is not { HasValue: true, Value: string path }) continue;
                if (index.Value is not { } idx || !idx.HasSection(section)) return;
                Report(ctx, idx, section, path, path, key.Syntax, write: true);
            }
        }
    }

    private static string? SectionOfDefine(IObjectCreationOperation create)
    {
        var parent = create.Parent;
        while (parent is IConversionOperation) parent = parent.Parent;
        if (parent is IArgumentOperation { Parent: IInvocationOperation { TargetMethod.Name: "Define" } inv }) return SectionOf(inv.Instance, 0);
        return null;
    }

    /// <summary>The string keys of a dictionary initializer: <c>["k"] = v</c> and <c>{ "k", v }</c>.</summary>
    private static IEnumerable<IOperation> KeysOf(IOperation value)
    {
        value = Strip(value);
        IObjectOrCollectionInitializerOperation? init = value switch
        {
            IObjectOrCollectionInitializerOperation i => i,
            IObjectCreationOperation c => c.Initializer,
            _ => null,
        };
        if (init is null) yield break;
        foreach (var e in init.Initializers)
        {
            if (e is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Arguments.Length: 1 } target }) yield return target.Arguments[0].Value;
            else if (e is IInvocationOperation { Arguments.Length: 2 } add) yield return add.Arguments[0].Value;
        }
    }

    // ---------------------------------------------------------------- the check

    private static void Report(OperationAnalysisContext ctx, FieldIndex index, string section, string target, string written, SyntaxNode at, bool write)
    {
        var r = index.Check(section, target);
        switch (r.Verdict)
        {
            case PathVerdict.Ok:
                if (write && r.Row is { Writable: false })
                    ctx.ReportDiagnostic(Diagnostic.Create(ReadOnlyPath, at.GetLocation(), written, section));
                return;
            case PathVerdict.Unknown:
                ctx.ReportDiagnostic(Make(UnknownPath, at, written, section, r, target));
                return;
            case PathVerdict.UnknownConfig:
                ctx.ReportDiagnostic(Make(UnknownConfig, at, written, section, r, target));
                return;
        }
    }

    private static Diagnostic Make(DiagnosticDescriptor d, SyntaxNode at, string written, string section, PathResult r, string target)
    {
        // Config("ManaCot") writes the name only; a full path is replaced as a whole
        string? fix = r.Suggestion;
        if (fix is not null && written != target && fix.EndsWith(written, StringComparison.Ordinal) == false)
        {
            var last = fix.LastIndexOf('.');
            fix = last >= 0 ? fix.Substring(last + 1) : fix;
        }
        var props = fix is null ? ImmutableDictionary<string, string?>.Empty : ImmutableDictionary<string, string?>.Empty.Add(SuggestionKey, fix);
        return Diagnostic.Create(d, at.GetLocation(), props, written, section, r.What, fix is null ? "" : $". Did you mean '{fix}'?");
    }

    // ---------------------------------------------------------------- which section an expression is

    private const int MaxTrace = 12;

    private static IOperation Strip(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c:
                    op = c.Operand;
                    continue;
                case IParenthesizedOperation p:
                    op = p.Operand;
                    continue;
                case IConditionalAccessInstanceOperation:
                    for (var a = op.Parent; a is not null; a = a.Parent)
                        if (a is IConditionalAccessOperation ca)
                        {
                            op = ca.Operation;
                            goto next;
                        }
                    return op;
                case ICoalesceOperation co:
                    op = co.Value;
                    continue;
                default:
                    return op;
            }
            next:;
        }
    }

    /// <summary>The catalog section of an entry or registry expression: <c>Content.Templates</c>, <c>Content.Templates.Get(x)</c>,
    /// <c>item.Data()</c>, an element of a loop over a registry, a local holding any of those. Null when the code does not say.</summary>
    private static string? SectionOf(IOperation? op, int depth)
    {
        if (op is null || depth > MaxTrace) return null;
        op = Strip(op);
        switch (op)
        {
            case IPropertyReferenceOperation p:
                if (p.Property.ContainingType is { Name: "Content", ContainingNamespace.Name: "Kiln" } && p.Instance is null)
                    return RegistrySection(p.Property.Name);
                return p.Property.IsIndexer ? SectionOf(p.Instance, depth + 1) : null;
            case IInvocationOperation inv:
                if (inv.TargetMethod.ContainingType is { Name: "Content", ContainingNamespace.Name: "Kiln" } && inv.Arguments.Length == 1 &&
                    inv.Arguments[0].Value.ConstantValue is { HasValue: true, Value: int v })
                    return inv.TargetMethod.Name switch
                    {
                        "GameData" => GameDataSectionName(v),
                        "Registry" => ContentKindName(v),
                        _ => null,
                    };
                // Get, Data, First, Where, ...: follows the entries it is called on
                var from = inv.Instance ?? (inv.TargetMethod.IsExtensionMethod && inv.Arguments.Length > 0 ? inv.Arguments[0].Value : null);
                return from is null || !LooksLikeEntries(inv.TargetMethod.ReturnType) ? null : SectionOf(from, depth + 1);
            case ILocalReferenceOperation local:
                return SectionOfLocal(local, depth);
            default:
                return null;
        }
    }

    private static bool LooksLikeEntries(ITypeSymbol t)
    {
        if (t is { Name: "GameDataEntry" or "ContentEntry" or "GameDataRegistry" or "ContentRegistry", ContainingNamespace.Name: "Kiln" }) return true;
        return t is INamedTypeSymbol { IsGenericType: true } g && g.TypeArguments.Length == 1 && LooksLikeEntries(g.TypeArguments[0]);
    }

    private static string? SectionOfLocal(ILocalReferenceOperation local, int depth)
    {
        var sym = local.Local;
        var model = local.SemanticModel;
        if (model is null || sym.DeclaringSyntaxReferences.Length != 1) return null;
        var decl = sym.DeclaringSyntaxReferences[0].GetSyntax();
        ExpressionSyntax? source = decl switch
        {
            VariableDeclaratorSyntax { Initializer: { } i } => i.Value,
            ForEachStatementSyntax f => f.Expression,
            SingleVariableDesignationSyntax d => d.Ancestors().OfType<IsPatternExpressionSyntax>().FirstOrDefault()?.Expression,
            _ => null,
        };
        if (source is null || decl.SyntaxTree != model.SyntaxTree) return null;
        // written again somewhere: the code no longer says which entry this is
        var scope = decl.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or PropertyDeclarationSyntax);
        if (scope is not null)
            foreach (var w in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (w.Left is IdentifierNameSyntax id && id.Identifier.ValueText == sym.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, sym))
                    return null;
        // `x!` and `(x)` have no operation of their own
        while (true)
        {
            if (source is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang) source = bang.Operand;
            else if (source is ParenthesizedExpressionSyntax paren) source = paren.Expression;
            else break;
        }
        return SectionOf(model.GetOperation(source), depth + 1);
    }

    private static string? RegistrySection(string property) => property switch
    {
        "Templates" => "templates",
        "Arsenals" => "arsenals",
        "Sequences" => "sequences",
        "Buffs" => "buffs",
        "Perks" => "perks",
        "Skills" => "skills",
        "ItemData" or "Items" => "items",
        "Recipes" => "recipes",
        "Loot" => "loot",
        "Drops" => "drops",
        _ => null,
    };

    // GameDataSection and ContentKind in Kiln.Content
    private static string? GameDataSectionName(int v) => v switch
    {
        1 => "templates",
        2 => "arsenals",
        3 => "sequences",
        4 => "buffs",
        5 => "perks",
        6 => "skills",
        7 => "items",
        _ => null,
    };

    private static string? ContentKindName(int v) => v switch
    {
        1 => "items",
        2 => "recipes",
        3 => "loot",
        4 => "drops",
        _ => null,
    };
}
