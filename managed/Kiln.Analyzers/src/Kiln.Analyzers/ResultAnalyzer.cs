// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN120: the value of a call that returns an <c>IKilnResult</c> (or a Task/ValueTask of one) is thrown away:
/// an expression statement (<c>Items.Equip(...);</c>, <c>await Items.Equip(...);</c>, an expression lambda whose
/// delegate returns void), or a local that holds it and is never read. <c>_ = ...</c> (and a local named <c>_</c>) is the
/// author's word that the drop is meant. Decided by the type, not by method names.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResultAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property: "statement" (a result), "await" (an awaited result), "task" (a task of a result,
    /// not awaited), "local" (stored in a local never read).</summary>
    public const string ShapeKey = "shape";
    /// <summary>Diagnostic property: the call, as the message names it ("Items.Equip").</summary>
    public const string CallKey = "call";
    /// <summary>Diagnostic property for "local": the local's name.</summary>
    public const string LocalKey = "local";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.ResultDiscarded);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var result = start.Compilation.GetTypeByMetadataName("Kiln.IKilnResult");
            if (result is null) return;
            start.RegisterOperationAction(ctx => Statement(ctx, result), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(ctx => Local(ctx, result), OperationKind.VariableDeclarator);
        });
    }

    private static void Statement(OperationAnalysisContext ctx, INamedTypeSymbol result)
    {
        var value = ((IExpressionStatementOperation)ctx.Operation).Operation;
        var shape = Shape(value, result, out var call);
        if (shape is null || call is null) return;
        var what = shape == "task" ? "is a task nobody awaits or reads" : "is thrown away";
        Report(ctx, call, shape, what, null);
    }

    // var r = await Items.Equip(...);   and r is never read
    private static void Local(OperationAnalysisContext ctx, INamedTypeSymbol result)
    {
        var decl = (IVariableDeclaratorOperation)ctx.Operation;
        var local = decl.Symbol;
        if (local.Name == "_" || decl.GetVariableInitializer()?.Value is not { } value) return;
        while (value is IConversionOperation { IsImplicit: true } conv) value = conv.Operand;
        if (Shape(value, result, out var call) is null || call is null) return;
        if (decl.Syntax is not VariableDeclaratorSyntax declarator) return;
        var body = Syntax.FunctionBody(Syntax.EnclosingFunction(declarator)) ?? declarator.Ancestors().OfType<GlobalStatementSyntax>().FirstOrDefault()?.Parent;
        if (body is null || IsRead(local, body, ctx.Operation.SemanticModel!, ctx.CancellationToken)) return;
        Report(ctx, call, "local", $"is stored in '{local.Name}' and never read", local.Name);
    }

    private static void Report(OperationAnalysisContext ctx, IOperation call, string shape, string what, string? local)
    {
        var member = Member(call);
        var display = member is null ? call.Syntax.ToString() : Syntax.Display(member);
        var props = ImmutableDictionary<string, string?>.Empty.Add(ShapeKey, shape).Add(CallKey, display);
        if (local is not null) props = props.Add(LocalKey, local);
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultDiscarded, ReportAt(call.Syntax), props, display, what));
    }

    /// <summary>What the value is: "statement" (a result), "await" (an awaited result), "task" (a task of a result); null
    /// for anything else. <paramref name="call"/> is the operation that produced it (an invocation, mostly).</summary>
    internal static string? Shape(IOperation value, INamedTypeSymbol result, out IOperation? call)
    {
        call = null;
        if (value is IAwaitOperation aw)
        {
            if (!IsResult(aw.Type, result)) return null;
            call = Unwrap(aw.Operation);
            return "await";
        }
        if (value is not (IInvocationOperation or IConditionalAccessOperation)) return null;
        call = Unwrap(value);
        if (IsResult(value.Type, result)) return "statement";
        if (IsTaskOf(value.Type, result)) return "task";
        call = null;
        return null;
    }

    // x?.Call() -> Call(); task.ConfigureAwait(false) -> task
    private static IOperation Unwrap(IOperation op)
    {
        for (var i = 0; i < 8; i++)
        {
            switch (op)
            {
                case IConditionalAccessOperation ca:
                    op = ca.WhenNotNull;
                    continue;
                case IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } inner }:
                    op = inner;
                    continue;
                case IConversionOperation { IsImplicit: true } conv:
                    op = conv.Operand;
                    continue;
            }
            break;
        }
        return op;
    }

    internal static bool IsResult(ITypeSymbol? type, INamedTypeSymbol result)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) type = nullable.TypeArguments[0];
        if (type is null || type.TypeKind == TypeKind.Error) return false;
        if (SymbolEqualityComparer.Default.Equals(type, result)) return true;
        foreach (var i in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(i, result)) return true;
        return false;
    }

    internal static bool IsTaskOf(ITypeSymbol? type, INamedTypeSymbol result) =>
        type is INamedTypeSymbol { Name: "Task" or "ValueTask", Arity: 1 } t &&
        t.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" && IsResult(t.TypeArguments[0], result);

    private static ISymbol? Member(IOperation op) => op switch
    {
        IInvocationOperation i => i.TargetMethod,
        IPropertyReferenceOperation p => p.Property,
        IFieldReferenceOperation f => f.Field,
        ILocalReferenceOperation l => l.Local,
        _ => null,
    };

    internal static Location ReportAt(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } => ma.Name.GetLocation(),
        InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax mb } => mb.Name.GetLocation(),
        InvocationExpressionSyntax { Expression: SimpleNameSyntax n } => n.GetLocation(),
        MemberAccessExpressionSyntax ma => ma.Name.GetLocation(),
        _ => node.GetLocation(),
    };

    /// <summary>True when <paramref name="local"/> is read anywhere in <paramref name="body"/>: any use that is not the
    /// target of a plain assignment.</summary>
    private static bool IsRead(ILocalSymbol local, SyntaxNode body, SemanticModel model, CancellationToken ct)
    {
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.ValueText != local.Name) continue;
            if (id.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } a && a.Left == id) continue;
            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local)) return true;
        }
        return false;
    }
}
