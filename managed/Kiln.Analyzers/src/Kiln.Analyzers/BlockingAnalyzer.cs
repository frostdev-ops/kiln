// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN105: blocking waits in code that runs on the tick thread (or another game thread via a Kiln callback),
/// and the ways of blocking on, or escaping, a Kiln task: <c>KilnTask.Wait()</c> and blocking on the plain
/// <c>Task</c> of <c>KilnTask.AsTask()</c> in a task body, and a <c>ctx.ToTick()</c> nobody awaits.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property for the code fix: awaitResult (x.Result), awaitWait (x.Wait()), awaitGetResult
    /// (x.GetAwaiter().GetResult()), delay (Thread.Sleep(n)). Only set inside async functions.</summary>
    public const string FixKey = "fix";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        Descriptors.BlockingCall, Descriptors.BlockingTaskCall, Descriptors.TickHopDiscarded);

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
        var op = ctx.Operation;
        var model = ctx.Operation.SemanticModel!;
        var syntax = op.Syntax;
        // ctx.ToTick() on its own hops nothing: the hop happens in its awaiter.
        if (op is IInvocationOperation hop && ThreadClassifier.IsKilnMember(hop.TargetMethod.OriginalDefinition, "KilnTaskContext", "ToTick"))
        {
            if (IsDiscardedHop(hop, model))
                ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.TickHopDiscarded, syntax.GetLocation(), "ctx.ToTick()"));
            return;
        }
        var (name, fix, receiver) = Blocking(op);
        var kilnWait = op is IInvocationOperation wait && IsKilnTaskWait(wait);
        if (kilnWait && op is IInvocationOperation kilnWaitOp) (name, fix, receiver) = ("KilnTask.Wait", "awaitWait", kilnWaitOp.Instance);
        if (name is null) return;
        var fromAsTask = !kilnWait && IsFromAsTask(receiver, model);
        if (fromAsTask) name += " (KilnTask.AsTask())";
        // Spawn.Entity(...).Result & co.: Kiln APIs that return Task.FromResult (KilnApis.CompletedTasks).
        if (ProcessWideAnalyzer.Unwrap(receiver) is IInvocationOperation src && cache.ReturnsCompletedTask(src.TargetMethod)) return;
        if (IsLocalOfCompletedApi(receiver, model, cache)) return;
        if (receiver is not null && AwaitedBefore(receiver, syntax, model)) return;
        var where = classifier.Classify(syntax, model, ctx.CancellationToken);
        var onGameThread = where.Kind is ThreadKind.Tick or ThreadKind.Game;
        // A blocking wait on a Kiln task (its own Wait, or the escaped AsTask Task) is refused in a task
        // body too, but only where the task's scope is proven: after 'await ...ConfigureAwait(false)' on
        // some path the code is usually on a .NET pool thread, where Wait is allowed, as in Task.Run.
        // Everything else still needs the tick or a game thread: a plain Task a task blocks on (its own
        // Task.Run work, say) is its own business there.
        if (!onGameThread && !(where.Task == TaskScope.Active && (kilnWait || fromAsTask))) return;
        var props = ImmutableDictionary<string, string?>.Empty;
        if (fix is not null && CanAwaitHere(syntax)) props = props.Add(FixKey, fix);
        ctx.ReportDiagnostic(Diagnostic.Create(onGameThread ? Descriptors.BlockingCall : Descriptors.BlockingTaskCall,
            syntax.GetLocation(), props, name, where.Where));
    }

    /// <summary><c>KilnTask.Wait()</c> itself: on <c>KilnTask</c>/<c>KilnTask&lt;T&gt;</c> by symbol, never a
    /// look-alike <c>Wait()</c> of the mod's own types. (Its <c>Result</c> and its own awaiter's
    /// <c>GetResult()</c> throw instead of blocking, so they are not this rule.)</summary>
    private static bool IsKilnTaskWait(IInvocationOperation inv) =>
        inv.TargetMethod.Name == "Wait" && inv.Arguments.Length == 0 && IsKilnTaskType(inv.TargetMethod.ContainingType);

    private static bool IsKilnTaskType(ITypeSymbol? t) =>
        ThreadClassifier.IsKilnType(t?.OriginalDefinition, "KilnTask") || ThreadClassifier.IsKilnType(t?.OriginalDefinition, "KilnTask<T>");

    /// <summary>The plain <c>Task</c> that <c>KilnTask.AsTask()</c> hands out is the receiver here: directly
    /// (<c>job.AsTask().Result</c>), or through a local whose only value is that call (a local written again
    /// is no longer proven to be it). By symbol: a user type's own <c>AsTask()</c> is not an escape.</summary>
    private static bool IsFromAsTask(IOperation? receiver, SemanticModel model)
    {
        switch (ProcessWideAnalyzer.Unwrap(receiver))
        {
            case IInvocationOperation inv:
                return IsAsTask(inv.TargetMethod);
            case ILocalReferenceOperation { Local: var local }:
            {
                if (local.DeclaringSyntaxReferences.Length != 1) return false;
                if (local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var init } decl) return false;
                if (decl.SyntaxTree != model.SyntaxTree) return false;
                if (ProcessWideAnalyzer.Unwrap(model.GetOperation(init)) is not IInvocationOperation call || !IsAsTask(call.TargetMethod)) return false;
                var body = Syntax.FunctionBody(Syntax.EnclosingFunction(decl));
                if (body is null) return false;
                foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (id.Identifier.ValueText != local.Name || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)) continue;
                    // Written again: plainly, by deconstruction ('(t, n) = ...') or through ref/out.
                    if (id.Ancestors().OfType<AssignmentExpressionSyntax>().Any(a => a.Left.Span.Contains(id.Span))) return false;
                    if (id.Parent is ArgumentSyntax arg && arg.RefKindKeyword.Span.Length > 0) return false;
                }
                return true;
            }
            default:
                return false;
        }
    }

    private static bool IsAsTask(IMethodSymbol m) =>
        m.Name == "AsTask" && IsKilnTaskType(m.OriginalDefinition.ContainingType);

    /// <summary>A <c>ctx.ToTick()</c> whose result runs nothing: an expression statement, a <c>_ =</c>, or a
    /// local that is never read again (a hop kept in a local and awaited later, <c>var hop = ctx.ToTick();
    /// await hop;</c>, is the documented way to keep one). Returned, passed on or awaited by hand
    /// (<c>GetAwaiter()</c>) is not judged.</summary>
    private static bool IsDiscardedHop(IInvocationOperation hop, SemanticModel model)
    {
        var parent = hop.Parent is IVariableInitializerOperation init ? init.Parent : hop.Parent;
        switch (parent)
        {
            case IAwaitOperation:
                return false;
            case IExpressionStatementOperation:
                return true;
            case ISimpleAssignmentOperation { Target: IDiscardOperation }:
                return true;
            case IVariableDeclaratorOperation { Symbol: ILocalSymbol local } decl:
            {
                // The declarator's own initializer mentions no local; any later read may await or pass it on.
                var body = Syntax.FunctionBody(Syntax.EnclosingFunction(hop.Syntax));
                if (body is null) return false;
                foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (id.Identifier.ValueText != local.Name) continue;
                    if (id.Ancestors().Any(a => a == decl.Syntax)) continue;
                    if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)) return false;
                }
                return true;
            }
            default:
                return false;
        }
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
