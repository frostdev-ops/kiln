// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN105: blocking waits in code that runs on the tick thread (or another game thread via a Kiln callback).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property for the code fix: awaitResult (x.Result), awaitWait (x.Wait()), awaitGetResult
    /// (x.GetAwaiter().GetResult()), delay (Thread.Sleep(n)). Only set inside async functions.</summary>
    public const string FixKey = "fix";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.BlockingCall);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Kiln.Mod") is null) return;
            var classifier = new ThreadClassifier(start.Compilation);
            var cache = new KilnApis.Cache();
            start.RegisterOperationAction(ctx => Analyze(ctx, classifier, cache), OperationKind.Invocation, OperationKind.PropertyReference);
        });
    }

    private static void Analyze(OperationAnalysisContext ctx, ThreadClassifier classifier, KilnApis.Cache cache)
    {
        var (name, fix, receiver) = Blocking(ctx.Operation);
        if (name is null) return;
        // Spawn.Entity(...).Result & co.: Kiln APIs that return Task.FromResult (KilnApis.CompletedTasks).
        if (ProcessWideAnalyzer.Unwrap(receiver) is IInvocationOperation src && cache.ReturnsCompletedTask(src.TargetMethod)) return;
        var syntax = ctx.Operation.Syntax;
        if (IsLocalOfCompletedApi(receiver, ctx.Operation.SemanticModel!, cache)) return;
        if (receiver is not null && AwaitedBefore(receiver, syntax, ctx.Operation.SemanticModel!)) return;
        var where = classifier.Classify(syntax, ctx.Operation.SemanticModel!, ctx.CancellationToken);
        if (where.Kind is not (ThreadKind.Tick or ThreadKind.Game)) return;
        var props = ImmutableDictionary<string, string?>.Empty;
        if (fix is not null && CanAwaitHere(syntax)) props = props.Add(FixKey, fix);
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.BlockingCall, syntax.GetLocation(), props, name, where.Where));
    }

    /// <summary>(display name, fix kind, the task whose result is read) of a blocking operation, or (null, ...).</summary>
    private static (string? Name, string? Fix, IOperation? Receiver) Blocking(IOperation op)
    {
        switch (op)
        {
            case IPropertyReferenceOperation { Property.Name: "Result" } p:
            {
                var t = p.Property.ContainingType?.OriginalDefinition;
                if (t?.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" && t.Name is "Task" or "ValueTask")
                    return IsCompletedTaskFactory(p.Instance) ? (null, null, null) : ($"{t.Name}.Result", "awaitResult", p.Instance);
                return (null, null, null);
            }
            case IInvocationOperation inv:
            {
                var m = inv.TargetMethod;
                var t = m.ContainingType?.OriginalDefinition;
                var ns = t?.ContainingNamespace?.ToDisplayString();
                var tn = t?.Name;
                if (FirstArgIsZero(inv)) return (null, null, null);  // a poll, not a wait
                switch (ns, tn, m.Name)
                {
                    case ("System.Threading", "Thread", "Sleep"):
                        return ("Thread.Sleep", "delay", null);
                    case ("System.Threading", "Thread", "Join"):
                    case ("System.Threading.Tasks", "Task", "WaitAll" or "WaitAny"):
                    case ("System.Threading", "WaitHandle", "WaitOne" or "WaitAll" or "WaitAny"):
                    case ("System.Threading", "SemaphoreSlim" or "ManualResetEventSlim" or "CountdownEvent", "Wait"):
                    case ("System.Threading", "Barrier", "SignalAndWait"):
                    case ("System.Threading", "Monitor", "Wait"):
                    case ("System.Threading", "SpinWait", "SpinUntil"):
                    case ("System.Diagnostics", "Process", "WaitForExit"):
                        return ($"{tn}.{m.Name}", null, null);
                    case ("System.Threading.Tasks", "Task", "Wait"):
                        if (IsCompletedTaskFactory(inv.Instance)) return (null, null, null);
                        return ("Task.Wait", inv.Arguments.Length == 0 ? "awaitWait" : null, inv.Instance);
                    case ("System.Runtime.CompilerServices", _, "GetResult") when tn!.EndsWith("Awaiter"):
                    {
                        // x.GetAwaiter().GetResult()
                        var task = (ProcessWideAnalyzer.Unwrap(inv.Instance) as IInvocationOperation) is { TargetMethod.Name: "GetAwaiter" } ga ? ga.Instance : null;
                        if (task is not null && IsCompletedTaskFactory(task)) return (null, null, null);
                        return ("GetAwaiter().GetResult()", task is not null ? "awaitGetResult" : null, task);
                    }
                }
                return (null, null, null);
            }
        }
        return (null, null, null);
    }

    private static bool FirstArgIsZero(IInvocationOperation inv) =>
        inv.Arguments.Length > 0 && inv.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value.ConstantValue is { HasValue: true, Value: 0 or 0L };

    /// <summary>Task.FromResult(x).Result, Task.CompletedTask.Wait(): already complete, nothing blocks.</summary>
    private static bool IsCompletedTaskFactory(IOperation? task) => ProcessWideAnalyzer.Unwrap(task) switch
    {
        IInvocationOperation { TargetMethod.Name: "FromResult" or "FromException" or "FromCanceled" } => true,
        IPropertyReferenceOperation { Property.Name: "CompletedTask" } => true,
        _ => false,
    };

    /// <summary>`var t = Creatures.Spawn(...); ... t.Result`: a local whose only value is the initializer, a call of an
    /// API that returns a completed task (KilnApis.CompletedTasks). Any later write (`t = ...`, `ref t`, `out t`) and it is
    /// no longer proven complete.</summary>
    private static bool IsLocalOfCompletedApi(IOperation? receiver, SemanticModel model, KilnApis.Cache cache)
    {
        if (ProcessWideAnalyzer.Unwrap(receiver) is not ILocalReferenceOperation { Local: var local }) return false;
        if (local.DeclaringSyntaxReferences.Length != 1) return false;
        if (local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var init } decl) return false;
        if (decl.SyntaxTree != model.SyntaxTree) return false;
        if (ProcessWideAnalyzer.Unwrap(model.GetOperation(init)) is not IInvocationOperation call || !cache.ReturnsCompletedTask(call.TargetMethod)) return false;
        var body = Syntax.FunctionBody(Syntax.EnclosingFunction(decl));
        if (body is null) return false;
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.ValueText != local.Name || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)) continue;
            if (id.Parent is AssignmentExpressionSyntax a && a.Left == id) return false;
            if (id.Parent is ArgumentSyntax arg && arg.RefKindKeyword.Span.Length > 0) return false;
        }
        return true;
    }

    /// <summary>`await t;` (or Task.WhenAll(..., t, ...)) earlier in the same function: t is complete, its Result does not block.
    /// Also `if (t.IsCompleted...) ... t.Result`.</summary>
    private static bool AwaitedBefore(IOperation receiver, SyntaxNode at, SemanticModel model)
    {
        var sym = ProcessWideAnalyzer.Unwrap(receiver) switch
        {
            ILocalReferenceOperation l => (ISymbol)l.Local,
            IParameterReferenceOperation p => p.Parameter,
            IFieldReferenceOperation f => f.Field,
            IPropertyReferenceOperation pr => pr.Property,
            _ => null,
        };
        if (sym is null) return false;
        var body = Syntax.FunctionBody(Syntax.EnclosingFunction(at));
        if (body is null) return false;
        foreach (var aw in body.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            if (aw.SpanStart >= at.SpanStart) break;
            if (aw.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, sym)))
                return true;
        }
        foreach (var anc in at.Ancestors())
        {
            var cond = anc switch { IfStatementSyntax i => i.Condition, ConditionalExpressionSyntax c => c.Condition, _ => null };
            if (cond is not null && cond.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(id => id.Identifier.ValueText.StartsWith("IsCompleted") || id.Identifier.ValueText == "Status"))
                return true;
            if (anc == body) break;
        }
        return false;
    }

    /// <summary>The fix inserts an await: only inside an async function, outside lock, unsafe and query clauses.</summary>
    private static bool CanAwaitHere(SyntaxNode node)
    {
        var fn = Syntax.EnclosingFunction(node);
        if (!Syntax.IsAsync(fn)) return false;
        for (var n = node.Parent; n != null && n != fn; n = n.Parent)
            if (n is LockStatementSyntax or UnsafeStatementSyntax or QueryExpressionSyntax)
                return false;
        return true;
    }
}
