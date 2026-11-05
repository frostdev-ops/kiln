// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN106: threads, timers, file watchers, sockets, files and external subscriptions a mod creates and never
/// releases. Kiln's own handles (commands, zones, channels, hooks, windows, ...) are released on unload and are not
/// reported.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnreleasedAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Properties for the code fix: fix = "dispose", member = the field/property holding it.</summary>
    public const string FixKey = "fix";
    public const string MemberKey = "member";

    private static readonly string[] ReleaseMethods = { "Dispose", "DisposeAsync", "Close", "Stop", "Join", "Interrupt", "Unregister", "Abort" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.Unreleased);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Kiln.Mod") is null) return;
            start.RegisterOperationAction(Analyze, OperationKind.ObjectCreation, OperationKind.Invocation);
        });
    }

    /// <summary>What needs releasing, by the type created (or the call that returns a registration).</summary>
    private static string? Tracked(IOperation op)
    {
        switch (op)
        {
            case IObjectCreationOperation oc:
            {
                var t = oc.Type?.OriginalDefinition;
                switch (t?.ContainingNamespace?.ToDisplayString(), t?.Name)
                {
                    case ("System.Threading", "Timer"): return "a System.Threading.Timer";
                    case ("System.Timers", "Timer"): return "a System.Timers.Timer";
                    case ("System.Threading", "PeriodicTimer"): return "a PeriodicTimer";
                    case ("System.Threading", "Thread"): return "a thread";
                    case ("System.IO", "FileSystemWatcher"): return "a FileSystemWatcher";
                    case ("System.IO", "FileStream" or "StreamWriter"): return $"a {t!.Name}";
                    case ("System.Net.Sockets", "Socket" or "TcpListener" or "TcpClient" or "UdpClient"): return $"a {t!.Name}";
                    case ("System.Net", "HttpListener"): return "an HttpListener";
                }
                return null;
            }
            case IInvocationOperation inv:
            {
                var m = inv.TargetMethod;
                var t = m.ContainingType?.OriginalDefinition;
                switch (t?.ContainingNamespace?.ToDisplayString(), t?.Name, m.Name)
                {
                    case ("System.Threading", "ThreadPool", "RegisterWaitForSingleObject" or "UnsafeRegisterWaitForSingleObject"):
                        return "a RegisteredWaitHandle (ThreadPool.RegisterWaitForSingleObject)";
                    case ("System", "IObservable", "Subscribe"):
                        return "an IObservable subscription";
                    case ("System.IO", "File", "Open" or "OpenWrite" or "Create" or "AppendText" or "CreateText"):
                        return $"a file opened with File.{m.Name}";
                }
                return null;
            }
        }
        return null;
    }

    private static void Analyze(OperationAnalysisContext ctx)
    {
        var what = Tracked(ctx.Operation);
        if (what is null) return;
        var holder = Holder(ctx.Operation, out var verdict);
        if (verdict == Verdict.Fine) return;
        var props = ImmutableDictionary<string, string?>.Empty;
        if (verdict == Verdict.Member)
        {
            var type = holder!.ContainingType;
            if (IsReleasedInType(holder, type, ctx.Compilation, ctx.CancellationToken)) return;
            // conn.Reader = new Thread(...) in another type than the field's: look there too.
            if (ctx.ContainingSymbol.ContainingType is { } here && !SymbolEqualityComparer.Default.Equals(here, type) &&
                IsReleasedInType(holder, here, ctx.Compilation, ctx.CancellationToken))
                return;
            if (Syntax.IsModType(type) && !holder.IsStatic && what != "a thread")
                props = props.Add(FixKey, "dispose").Add(MemberKey, holder.Name);
            what = $"{what} held in '{holder.Name}'";
        }
        else if (verdict == Verdict.Local)
        {
            what = $"{what} in local '{holder!.Name}'";
        }
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.Unreleased, ctx.Operation.Syntax.GetLocation(), props, Capitalize(what)));
    }

    private enum Verdict { Fine, Member, Local, Dropped }

    /// <summary>Where the created object goes: a using (fine), a field/property (check the type releases it), a local
    /// (check the function releases, returns, stores or hands it on), or nowhere (dropped: it can never be released).</summary>
    private static ISymbol? Holder(IOperation op, out Verdict verdict)
    {
        var cur = op;
        var parent = op.Parent;
        while (parent is IConversionOperation or IParenthesizedOperation) { cur = parent; parent = parent.Parent; }
        // new Thread(...).Start(): the thread itself is dropped.
        if (parent is IInvocationOperation { TargetMethod.Name: "Start" } start && start.Instance == cur)
        {
            verdict = start.Parent is IExpressionStatementOperation ? Verdict.Dropped : Verdict.Fine;
            return null;
        }
        switch (parent)
        {
            case IUsingOperation or IUsingDeclarationOperation:
                verdict = Verdict.Fine;
                return null;
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation decl }:
                if (InUsing(decl))
                {
                    verdict = Verdict.Fine;
                    return null;
                }
                return LocalFate(decl.Symbol, op.Syntax, out verdict);
            case IFieldInitializerOperation fi when fi.InitializedFields.Length == 1:
                verdict = Verdict.Member;
                return fi.InitializedFields[0];
            case IPropertyInitializerOperation pi when pi.InitializedProperties.Length == 1:
                verdict = Verdict.Member;
                return pi.InitializedProperties[0];
            case ISimpleAssignmentOperation a when a.Value == cur:
                switch (a.Target)
                {
                    case IFieldReferenceOperation f:
                        verdict = Verdict.Member;
                        return f.Field;
                    case IPropertyReferenceOperation { Property.IsIndexer: false } p:
                        verdict = Verdict.Member;
                        return p.Property;
                    case ILocalReferenceOperation l:
                        return LocalFate(l.Local, op.Syntax, out verdict);
                }
                verdict = Verdict.Fine;
                return null;
            case IExpressionStatementOperation:
                verdict = Verdict.Dropped;
                return null;
            case IDiscardOperation:
            case ISimpleAssignmentOperation { Target: IDiscardOperation }:
                verdict = Verdict.Dropped;
                return null;
            default:
                verdict = Verdict.Fine;  // an argument, a return value, a collection element: someone else owns it
                return null;
        }
    }

    private static bool InUsing(IOperation decl)
    {
        var p = decl.Parent;
        for (var i = 0; i < 3 && p != null; i++, p = p.Parent)
            if (p is IUsingOperation or IUsingDeclarationOperation) return true;
        return false;
    }

    private static ISymbol LocalFate(ILocalSymbol local, SyntaxNode creation, out Verdict verdict)
    {
        verdict = Verdict.Local;
        var body = Syntax.FunctionBody(Syntax.EnclosingFunction(creation));
        if (body is null) { verdict = Verdict.Fine; return local; }
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.ValueText != local.Name || id.SpanStart <= creation.SpanStart) continue;
            var p = id.Parent;
            if (p is MemberAccessExpressionSyntax ma && ma.Expression == id && ReleaseMethods.Contains(ma.Name.Identifier.ValueText) ||
                p is ConditionalAccessExpressionSyntax ca && ca.Expression == id ||
                p is ArgumentSyntax || p is ReturnStatementSyntax || p is ArrowExpressionClauseSyntax ||
                p is AssignmentExpressionSyntax asg && asg.Right == id ||
                p is UsingStatementSyntax || p is EqualsValueClauseSyntax || p is InitializerExpressionSyntax)
            {
                verdict = Verdict.Fine;  // released, handed on or stored (stores are checked where they happen)
                break;
            }
        }
        return local;
    }

    private static bool IsReleasedInType(ISymbol member, INamedTypeSymbol type, Compilation compilation, CancellationToken ct)
    {
        foreach (var (root, model) in Syntax.Declarations(type, compilation, ct))
        {
            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != member.Name) continue;
                ExpressionSyntax expr = id.Parent is MemberAccessExpressionSyntax owner && owner.Name == id ? owner : id;  // this.x, c.x
                var p = expr.Parent;
                var released =
                    p is MemberAccessExpressionSyntax ma && ma.Expression == expr && ReleaseMethods.Contains(ma.Name.Identifier.ValueText) ||
                    p is ConditionalAccessExpressionSyntax ca && ca.Expression == expr &&
                    ca.WhenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax mb } &&
                    ReleaseMethods.Contains(mb.Name.Identifier.ValueText) ||
                    p is UsingStatementSyntax || p is ArgumentSyntax;  // using (x), or handed to a helper that disposes it
                if (released && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expr, ct).Symbol?.OriginalDefinition, member.OriginalDefinition))
                    return true;
            }
        }
        return false;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
