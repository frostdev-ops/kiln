// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>Mod.OnBoot's rules (chapter 08). KILN034: a Mod subclass overrides <c>OnBoot</c> while the project's kiln.mod.json (or
/// <c>KilnBoot</c>) has no <c>"boot"</c>, so Kiln never calls it; and the run-time <c>Blocks.Define</c> called from boot code, which Kiln
/// refuses there (PhaseClosed). KILN035: a tick, world or service API called from boot code: a member that needs the tick thread
/// (<c>[TickThreadOnly]</c> or the generated tick table) or a member of a service's own static class (<c>Kiln.World</c>, <c>Kiln.Players</c>,
/// <c>Kiln.Content</c>, ...: the classes named after the services of the generated service table, and the types nested in them).
/// Boot code: the body of an <c>OnBoot</c> override, or of a method, accessor or type marked <c>[BootThreadOnly]</c>, with its local
/// functions and the lambdas that run at once (LINQ). A lambda handed to Kiln, to another thread or kept by the mod is not boot code.
/// Members marked <c>[BootThreadOnly]</c> (on themselves, their property or a type around them) and the capability guard properties
/// (<c>X.Available</c>, the generated guard table) are fine in boot code. <c>[AnyThread]</c> is not enough: it says which threads may call a
/// member, not that a world exists (<c>Players.All</c> is an empty snapshot before the game is entered).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BootAnalyzer : DiagnosticAnalyzer
{
    private const string BootThreadOnlyAttribute = "BootThreadOnlyAttribute";

    private static readonly Regex BootKey = new("\"boot\"\\s*:\\s*(true|false|null|\"[^\"]*\")", RegexOptions.CultureInvariant);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.BootPhase, Descriptors.BootThreadApi);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var mod = start.Compilation.GetTypeByMetadataName("Kiln.Mod");
            if (mod?.GetMembers("OnBoot").OfType<IMethodSymbol>().FirstOrDefault() is not { } onBoot) return;  // a Kiln.Api before OnBoot
            var boot = Boot(start.Options, start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
            if (boot == false)
                start.RegisterSymbolAction(ctx => AnalyzeOverride(ctx, onBoot), SymbolKind.Method);
            var cache = new KilnApis.Cache();
            var classifier = new ThreadClassifier(start.Compilation);
            start.RegisterOperationAction(ctx => AnalyzeUse(ctx, onBoot, cache, classifier),
                OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.EventAssignment, OperationKind.ObjectCreation,
                OperationKind.MethodReference, OperationKind.FieldReference);
        });
    }

    /// <summary>True when the project opts into the boot phase: kiln.mod.json's <c>"boot"</c> is <c>true</c> or <c>"required"</c> (the file
    /// wins, as in the SDK's manifest task), else <c>KilnBoot</c> says so. False when the file says nothing (or false), or when a Kiln.Sdk
    /// project (it passes <c>KilnModId</c>) has no file and no <c>KilnBoot</c>: the manifest the SDK writes then has no <c>"boot"</c>. Null
    /// when nothing is known (not a Kiln.Sdk project): no diagnostic.</summary>
    internal static bool? Boot(AnalyzerOptions options, AnalyzerConfigOptions global)
    {
        foreach (var file in options.AdditionalFiles)
        {
            if (!string.Equals(Path.GetFileName(file.Path), "kiln.mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            var text = file.GetText()?.ToString();
            if (text is null) continue;
            var m = BootKey.Match(text);
            return m.Success && m.Groups[1].Value is "true" or "\"required\"";
        }
        if (global.TryGetValue("build_property.KilnBoot", out var v) && v.Trim().Length > 0)
            return v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || v.Trim().Equals("required", StringComparison.OrdinalIgnoreCase);
        if (global.TryGetValue("build_property.KilnModId", out var id) && id.Trim().Length > 0) return false;
        return null;
    }

    private static void AnalyzeOverride(SymbolAnalysisContext ctx, IMethodSymbol onBoot)
    {
        var m = (IMethodSymbol)ctx.Symbol;
        if (!m.IsOverride || !Overrides(m, onBoot) || m.Locations.FirstOrDefault(l => l.IsInSource) is not { } at) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.BootPhase, at,
            $"'{m.ContainingType.Name}.OnBoot' never runs: the mod's kiln.mod.json has no \"boot\", and Kiln calls OnBoot only for a mod that " +
            "opts in; add \"boot\": true to kiln.mod.json (\"required\": a failure of its boot content stops the start), or move the code to OnLoad"));
    }

    private static void AnalyzeUse(OperationAnalysisContext ctx, IMethodSymbol onBoot, KilnApis.Cache cache, ThreadClassifier classifier)
    {
        var op = ctx.Operation;
        if (InNameOf(op)) return;
        var member = op switch
        {
            IMethodReferenceOperation mr => mr.Method,
            IFieldReferenceOperation { Field.IsConst: false } fr => fr.Field,
            IFieldReferenceOperation => null,
            _ => TickThreadAnalyzer.Member(op),
        };
        if (member is null || member.ContainingAssembly?.Name.StartsWith("Kiln", StringComparison.Ordinal) != true) return;
        if (IsBlocksDefine(member))
        {
            if (BootCode(op.Syntax, op.SemanticModel!, onBoot, classifier, ctx.CancellationToken) is not { } frame) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.BootPhase, ReportAt(op.Syntax),
                $"Blocks.Define registers a block at once and is refused in {frame} (PhaseClosed: the boot generation is not published yet); " +
                "stage it with BootDefinitions.For(boot).Define(name, block) and use its Reference where another block names it"));
            return;
        }
        var what = Why(member, cache);
        if (what is null) return;
        var where = BootCode(op.Syntax, op.SemanticModel!, onBoot, classifier, ctx.CancellationToken);
        if (where is null) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.BootThreadApi, ReportAt(op.Syntax), Syntax.Display(member), what, where));
    }

    /// <summary>Why <paramref name="member"/> has no place in boot code ("must be called on the tick thread", "is a Kiln.World service
    /// API"), or null when it may be called there.</summary>
    private static string? Why(ISymbol member, KilnApis.Cache cache)
    {
        var m = member is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : member;
        m = m.OriginalDefinition;
        if (Marked(m, BootThreadOnlyAttribute)) return null;
        var asProperty = m is IMethodSymbol { AssociatedSymbol: IPropertySymbol p } ? p : m as IPropertySymbol;
        if (asProperty is not null && KilnApis.GuardRows(asProperty).Count > 0) return null;  // answers what Capabilities.Is answers
        if (cache.RequiresTickThread(member)) return "must be called on the tick thread";
        if (ServiceClass(m) is { } service) return $"is a Kiln.{service} service API";
        return null;
    }

    /// <summary>The service whose own static class (<c>Kiln.&lt;Service&gt;</c> in assembly <c>Kiln.&lt;Service&gt;</c>) holds
    /// <paramref name="m"/>, itself or around a nested type; null otherwise.</summary>
    private static string? ServiceClass(ISymbol m)
    {
        var service = KilnApis.ServiceOf(m.ContainingAssembly);
        if (service is null || service == "Testing") return null;
        for (var t = m.ContainingType; t != null; t = t.ContainingType)
            if (t.ContainingType is null && t.IsStatic && t.Name == service && t.ContainingNamespace?.ToDisplayString() == "Kiln") return service;
        return null;
    }

    /// <summary>The attribute (matched by name in namespace Kiln) on the member, its property or event, or a type around it.</summary>
    private static bool Marked(ISymbol m, string attribute)
    {
        if (KilnApis.HasAttribute(m, attribute)) return true;
        if (m is IMethodSymbol { AssociatedSymbol: { } assoc } && KilnApis.HasAttribute(assoc, attribute)) return true;
        for (var t = m.ContainingType; t != null; t = t.ContainingType)
            if (KilnApis.HasAttribute(t, attribute)) return true;
        return false;
    }

    /// <summary>Kiln.Blocks' run-time <c>Blocks.Define</c> (every overload).</summary>
    private static bool IsBlocksDefine(ISymbol member) =>
        member is IMethodSymbol { Name: "Define", ContainingType: { Name: "Blocks", ContainingType: null } t } m &&
        t.ContainingNamespace?.ToDisplayString() == "Kiln" && m.ContainingAssembly?.Name == "Kiln.Blocks";

    /// <summary>"OnBoot" or "'Name' ([BootThreadOnly])" when <paramref name="node"/> runs on the boot thread, as far as the syntax tells;
    /// null otherwise (not boot code, or not known).</summary>
    internal static string? BootCode(SyntaxNode node, SemanticModel model, IMethodSymbol onBoot, ThreadClassifier classifier, CancellationToken ct)
    {
        for (var n = node.Parent; n != null; n = n.Parent)
        {
            ct.ThrowIfCancellationRequested();
            switch (n)
            {
                case AnonymousFunctionExpressionSyntax f:
                    // run at once (LINQ, List.ForEach): the code around it decides; handed to Kiln, to a thread or kept: not boot code
                    if (classifier.ClassifyDelegateUse(f, model, ct).Kind != ThreadKind.Continue) return null;
                    break;
                case LocalFunctionStatementSyntax:
                    break;  // runs where it is called
                case BaseMethodDeclarationSyntax md:
                    return model.GetDeclaredSymbol(md, ct) is IMethodSymbol m ? BootMethod(m, onBoot) : null;
                case AccessorDeclarationSyntax ad:
                    return model.GetDeclaredSymbol(ad, ct) is IMethodSymbol acc ? BootMethod(acc, onBoot) : null;
                case PropertyDeclarationSyntax { ExpressionBody: { } body } pd when body.Span.Contains(node.Span):
                    return model.GetDeclaredSymbol(pd, ct) is IPropertySymbol { GetMethod: { } getter } ? BootMethod(getter, onBoot) : null;
                case MemberDeclarationSyntax or GlobalStatementSyntax:
                    return null;
            }
        }
        return null;
    }

    private static string? BootMethod(IMethodSymbol m, IMethodSymbol onBoot)
    {
        if (Overrides(m, onBoot)) return "OnBoot";
        if (Marked(m, BootThreadOnlyAttribute))
            return $"'{(m.AssociatedSymbol ?? m).Name}' ([BootThreadOnly])";
        return null;
    }

    private static bool Overrides(IMethodSymbol m, IMethodSymbol onBoot)
    {
        for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
            if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, onBoot)) return true;
        return false;
    }

    private static bool InNameOf(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent)
            if (p is INameOfOperation) return true;
        return false;
    }

    private static Location ReportAt(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } => ma.Name.GetLocation(),
        MemberAccessExpressionSyntax ma => ma.Name.GetLocation(),
        _ => node.GetLocation(),
    };
}
