// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN102: async void methods and async lambdas converted to void-returning delegates.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncVoidAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property: "task" when the method can simply return Task (code fix).</summary>
    public const string FixKey = "fix";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.AsyncVoid);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterOperationAction(AnalyzeLambda, OperationKind.AnonymousFunction);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext ctx)
    {
        var m = (IMethodSymbol)ctx.Symbol;
        if (!m.IsAsync || !m.ReturnsVoid || m.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation)) return;
        string message;
        var fixable = false;
        if (OverridesModLifecycle(m))
        {
            message = $"'{m.Name}' is async void: Kiln continues at its first await as if it had finished " +
                      (m.Name == "OnLoad" ? "(dependents load and ModsLoaded fires while it still runs)" : "(the mod unloads while it still runs)") +
                      ", nothing can await it, and an exception thrown after it leaves the tick thread takes the server down; " +
                      "keep it synchronous and start the async work from an async Task method (keep the Task)";
        }
        else if (m.GetAttributes().Any(a => a.AttributeClass is { Name: "SubscribeAttribute" } c && c.ContainingNamespace?.ToDisplayString() == "Kiln"))
        {
            message = $"[Subscribe] handler '{m.Name}' is async void: the event bus needs void handlers, so the event is done " +
                      "(and cancellation decided) at its first await; handle the event synchronously and start async work from an async Task method";
        }
        else
        {
            message = $"'{m.Name}' is async void: it cannot be awaited, Kiln's tests and unload cannot wait for it, and an exception " +
                      "thrown after it leaves the tick thread takes the server down; return Task";
            fixable = m.MethodKind == MethodKind.Ordinary && !m.IsOverride && !ImplementsInterface(m) && !IsUsedAsDelegate(m, ctx);
        }
        var props = fixable ? ImmutableDictionary<string, string?>.Empty.Add(FixKey, "task") : ImmutableDictionary<string, string?>.Empty;
        foreach (var loc in m.Locations.Where(l => l.IsInSource).Take(1))
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncVoid, loc, props, message));
    }

    private static void AnalyzeLambda(OperationAnalysisContext ctx)
    {
        var f = (IAnonymousFunctionOperation)ctx.Operation;
        if (!f.Symbol.IsAsync || !f.Symbol.ReturnsVoid) return;
        var delegateType = (f.Parent as IDelegateCreationOperation)?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? "a void delegate";
        var at = f.Syntax switch
        {
            AnonymousFunctionExpressionSyntax a when a.AsyncKeyword.Span.Length > 0 => a.AsyncKeyword.GetLocation(),
            _ => f.Syntax.GetLocation(),
        };
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncVoid, at,
            $"async lambda converted to '{delegateType}', which returns void: nothing can await it, and an exception thrown " +
            "after it leaves the tick thread takes the server down; use an overload that takes a Func<Task>, or call an async Task method from a synchronous handler"));
    }

    internal static bool OverridesModLifecycle(IMethodSymbol m)
    {
        for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
            if (o.Name is "OnLoad" or "OnUnload" && o.ContainingType is { Name: "Mod" } t && t.ContainingNamespace?.ToDisplayString() == "Kiln")
                return true;
        return false;
    }

    private static bool ImplementsInterface(IMethodSymbol m) =>
        m.ContainingType.AllInterfaces.SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Any(im => SymbolEqualityComparer.Default.Equals(m.ContainingType.FindImplementationForInterfaceMember(im), m));

    /// <summary>Changing the return type breaks method-group uses (event handlers, Action parameters).</summary>
    private static bool IsUsedAsDelegate(IMethodSymbol m, SymbolAnalysisContext ctx)
    {
        foreach (var (root, model) in Syntax.Declarations(m.ContainingType, ctx.Compilation, ctx.CancellationToken))
        {
            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != m.Name) continue;
                ExpressionSyntax expr = id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma : id;
                if (expr.Parent is InvocationExpressionSyntax inv && inv.Expression == expr) continue;
                if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expr, ctx.CancellationToken).Symbol?.OriginalDefinition, m) ||
                    model.GetSymbolInfo(expr, ctx.CancellationToken).CandidateSymbols.Any(c => SymbolEqualityComparer.Default.Equals(c.OriginalDefinition, m)))
                    return true;
            }
        }
        return false;
    }
}
