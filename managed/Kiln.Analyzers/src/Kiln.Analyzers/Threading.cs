// SPDX-License-Identifier: MIT
#pragma warning disable RS1030 // method-group handlers are found by binding the containing type's other declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kiln.Analyzers;

/// <summary>Where a piece of code runs, as far as the analyzers can tell.</summary>
internal enum ThreadKind
{
    /// <summary>Not known (stored delegates, guarded by Game.IsTickThread, ...): never reported.</summary>
    Unknown,

    /// <summary>The server's tick thread: OnLoad/OnUnload, [Subscribe] and [KilnTest] methods, Scheduler.Post work.</summary>
    Tick,

    /// <summary>A Kiln callback: a game thread (the tick thread for server-side handlers; the client's main, UI or render
    /// thread for client/UI callbacks).</summary>
    Game,

    /// <summary>Not a game thread: Task.Run, threads, timers, thread-pool callbacks, after ConfigureAwait(false).</summary>
    Off,

    /// <summary>Internal: a synchronous use (LINQ, List.ForEach): the enclosing code decides.</summary>
    Continue,
}

internal readonly struct ThreadContext
{
    public ThreadContext(ThreadKind kind, string where)
    {
        Kind = kind;
        Where = where;
    }

    public ThreadKind Kind { get; }

    /// <summary>Human text that completes "this code runs ...", e.g. "on a thread-pool thread (Task.Run)".</summary>
    public string Where { get; }

    public static readonly ThreadContext Unknown = new(ThreadKind.Unknown, "");
    public static readonly ThreadContext Continue = new(ThreadKind.Continue, "");
}

/// <summary>Classifies code by the thread it runs on (KILN101, KILN105). One per compilation.</summary>
internal sealed class ThreadClassifier
{
    private readonly Compilation _compilation;
    private readonly ConcurrentDictionary<IMethodSymbol, ThreadContext> _methods = new(SymbolEqualityComparer.Default);

    public ThreadClassifier(Compilation compilation) => _compilation = compilation;

    public ThreadContext Classify(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        var child = node;
        for (var n = node.Parent; n != null; child = n, n = n.Parent)
        {
            ct.ThrowIfCancellationRequested();
            switch (n)
            {
                case IfStatementSyntax ifs when child != ifs.Condition && MentionsThreadCheck(ifs.Condition):
                case ConditionalExpressionSyntax ce when child != ce.Condition && MentionsThreadCheck(ce.Condition):
                    return ThreadContext.Unknown;  // the code checks its thread itself
                case AnonymousFunctionExpressionSyntax f:
                {
                    if (AfterConfigureAwaitFalse(f.Body, node, model, out var why)) return new ThreadContext(ThreadKind.Off, why);
                    var use = ClassifyDelegateUse(f, model, ct);
                    if (use.Kind != ThreadKind.Continue) return use;
                    break;
                }
                case LocalFunctionStatementSyntax lf:
                {
                    if (AfterConfigureAwaitFalse((SyntaxNode?)lf.Body ?? lf.ExpressionBody, node, model, out var why))
                        return new ThreadContext(ThreadKind.Off, why);
                    break;  // a local function called directly runs where its caller runs
                }
                case BaseMethodDeclarationSyntax md:
                {
                    if (AfterConfigureAwaitFalse((SyntaxNode?)md.Body ?? md.ExpressionBody, node, model, out var why))
                        return new ThreadContext(ThreadKind.Off, why);
                    return model.GetDeclaredSymbol(md, ct) is IMethodSymbol m ? ClassifyMethod(m, ct) : ThreadContext.Unknown;
                }
                case AccessorDeclarationSyntax ad:
                    return model.GetDeclaredSymbol(ad, ct) is IMethodSymbol acc ? ClassifyMethod(acc, ct) : ThreadContext.Unknown;
                case MemberDeclarationSyntax or GlobalStatementSyntax:
                    return ThreadContext.Unknown;
            }
        }
        return ThreadContext.Unknown;
    }

    /// <summary>The thread a method runs on from its own shape (Kiln entry points) or from how its type hands it out
    /// as a method group (Task.Run(Work), Context.Tick.Pre += OnPre, ...).</summary>
    public ThreadContext ClassifyMethod(IMethodSymbol m, CancellationToken ct) =>
        _methods.GetOrAdd(m.OriginalDefinition, x => ClassifyMethodCore(x, ct));

    private ThreadContext ClassifyMethodCore(IMethodSymbol m, CancellationToken ct)
    {
        for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
            if (o.ContainingType is { Name: "Mod" } t && t.ContainingNamespace?.ToDisplayString() == "Kiln" && o.Name is "OnLoad" or "OnUnload")
                return new ThreadContext(ThreadKind.Tick, $"the tick thread ({m.Name})");
        foreach (var a in m.GetAttributes())
        {
            var c = a.AttributeClass;
            if (c is null) continue;
            var ns = c.ContainingNamespace?.ToDisplayString();
            if (c.Name == "SubscribeAttribute" && ns == "Kiln") return new ThreadContext(ThreadKind.Tick, "the tick thread (a [Subscribe] handler)");
            if (c.Name == "KilnTestAttribute" && ns == "Kiln.Testing") return new ThreadContext(ThreadKind.Tick, "the tick thread (a [KilnTest] method)");
        }
        return MethodGroupUse(m, ct);
    }

    private ThreadContext MethodGroupUse(IMethodSymbol m, CancellationToken ct)
    {
        if (m.ContainingType is null || m.MethodKind != MethodKind.Ordinary) return ThreadContext.Unknown;
        ThreadContext best = ThreadContext.Unknown;
        foreach (var r in m.ContainingType.DeclaringSyntaxReferences)
        {
            var root = r.GetSyntax(ct);
            if (!_compilation.SyntaxTrees.Contains(root.SyntaxTree)) continue;
            var model = _compilation.GetSemanticModel(root.SyntaxTree);
            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != m.Name) continue;
                ExpressionSyntax expr = id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma : id;
                if (expr.Parent is InvocationExpressionSyntax inv && inv.Expression == expr) continue;  // a call, not a group
                var info = model.GetSymbolInfo(expr, ct);
                var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (!SymbolEqualityComparer.Default.Equals(sym?.OriginalDefinition, m)) continue;
                var use = ClassifyDelegateUse(expr, model, ct);
                if (use.Kind == ThreadKind.Off) return use;
                if (use.Kind is ThreadKind.Tick or ThreadKind.Game && best.Kind == ThreadKind.Unknown) best = use;
            }
        }
        return best;
    }

    /// <summary>How a lambda or method group is used: posted to the tick thread, started on another thread, handed to
    /// Kiln as a callback, run synchronously (Continue), or stored (Unknown).</summary>
    public ThreadContext ClassifyDelegateUse(ExpressionSyntax expr, SemanticModel model, CancellationToken ct)
    {
        SyntaxNode e = expr;
        while (e.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax) e = e.Parent;
        switch (e.Parent)
        {
            case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: { } call } } arg:
            {
                var info = model.GetSymbolInfo(call, ct);
                var target = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                return target is null ? ThreadContext.Unknown : ClassifyCallTarget(target, call, model);
            }
            case AssignmentExpressionSyntax asg when asg.Right == e && asg.IsKind(SyntaxKind.AddAssignmentExpression):
                return model.GetSymbolInfo(asg.Left, ct).Symbol is IEventSymbol ev ? ClassifyEvent(ev) : ThreadContext.Unknown;
            default:
                return ThreadContext.Unknown;
        }
    }

    private ThreadContext ClassifyCallTarget(IMethodSymbol target, SyntaxNode call, SemanticModel model)
    {
        target = target.ReducedFrom ?? target;
        var type = target.ContainingType?.OriginalDefinition;
        var ns = type?.ContainingNamespace?.ToDisplayString();
        var tn = type?.Name;
        var name = target.Name;
        if (ns == "Kiln" && tn == "Scheduler" && name == "Post") return new ThreadContext(ThreadKind.Tick, "the tick thread (Scheduler.Post)");
        if (ns == "Kiln" && (tn, name) is ("Events", "On") or ("ModMessages", "On"))
            return new ThreadContext(ThreadKind.Tick, $"the tick thread (a {tn}.On handler)");
        switch (ns, tn, name)
        {
            case ("System.Threading.Tasks", "Task", "Run"):
                return Off("on a thread-pool thread (Task.Run)");
            case ("System.Threading.Tasks", "Task", "ContinueWith"):
                return call.DescendantNodes().OfType<ArgumentSyntax>().Any(a =>
                           model.GetTypeInfo(a.Expression).Type?.Name == "TaskScheduler" ||
                           a.Expression.ToString().Contains("ExecuteSynchronously"))
                    ? ThreadContext.Unknown
                    : Off("on a thread-pool thread (Task.ContinueWith)");
            case ("System.Threading.Tasks", "TaskFactory", "StartNew"):
                return Off("on a thread-pool thread (Task.Factory.StartNew)");
            case ("System.Threading", "Thread", ".ctor"):
                return Off("on its own thread (new Thread)");
            case ("System.Threading", "ThreadPool", _):
                return Off($"on a thread-pool thread (ThreadPool.{name})");
            case ("System.Threading", "Timer", ".ctor"):
                return Off("on a timer thread (System.Threading.Timer)");
            case ("System.Threading.Tasks", "Parallel", _):
                return Off($"on thread-pool threads (Parallel.{name})");
        }
        if (KilnApis.IsKilnAssembly(target.ContainingAssembly))
            return new ThreadContext(ThreadKind.Game, $"a game thread (a Kiln callback: {tn}.{name})");
        // The mod's own helpers may store the delegate: unknown. Library calls (LINQ, List.ForEach) run it now.
        if (SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, _compilation.Assembly)) return ThreadContext.Unknown;
        return ThreadContext.Continue;
    }

    private static ThreadContext ClassifyEvent(IEventSymbol ev)
    {
        var type = ev.ContainingType?.OriginalDefinition;
        var ns = type?.ContainingNamespace?.ToDisplayString();
        var tn = type?.Name;
        switch (ns, tn, ev.Name)
        {
            case ("System.Timers", "Timer", "Elapsed"):
                return Off("on a timer thread (System.Timers.Timer.Elapsed)");
            case ("System.IO", "FileSystemWatcher", _):
                return Off($"on a thread-pool thread (FileSystemWatcher.{ev.Name})");
            case ("System.Diagnostics", "Process", "OutputDataReceived" or "ErrorDataReceived" or "Exited"):
                return Off($"on a thread-pool thread (Process.{ev.Name})");
            case ("System", "AppDomain", "ProcessExit" or "UnhandledException" or "DomainUnload"):
                return Off($"on the thread that raises AppDomain.{ev.Name}");
            case ("System.Threading.Tasks", "TaskScheduler", "UnobservedTaskException"):
                return Off("on the finalizer thread (TaskScheduler.UnobservedTaskException)");
            case ("System", "Console", "CancelKeyPress"):
                return Off("on a signal thread (Console.CancelKeyPress)");
            case ("System.Net.Sockets", "SocketAsyncEventArgs", "Completed"):
                return Off("on an I/O thread (SocketAsyncEventArgs.Completed)");
            case ("System.ComponentModel", "BackgroundWorker", "DoWork"):
                return Off("on a thread-pool thread (BackgroundWorker.DoWork)");
        }
        if (ns == "Kiln" && tn is "TickEvents" or "GameLogEvents")
            return new ThreadContext(ThreadKind.Tick, $"the tick thread (a {(tn == "TickEvents" ? "Tick" : "GameLog")}.{ev.Name} handler)");
        if (KilnApis.IsKilnAssembly(ev.ContainingAssembly))
            return new ThreadContext(ThreadKind.Game, $"a game thread (a Kiln event handler: {tn}.{ev.Name})");
        return ThreadContext.Unknown;
    }

    private static ThreadContext Off(string where) => new(ThreadKind.Off, where);

    private static bool MentionsThreadCheck(ExpressionSyntax cond) =>
        cond.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.ValueText is "IsTickThread" or "IsClientThread");

    /// <summary>True when <paramref name="node"/> comes after an <c>await x.ConfigureAwait(false)</c> in
    /// <paramref name="body"/> (outside nested functions) with no <c>await Scheduler.NextTick()</c> in between: from
    /// there on the code continues on a thread-pool thread.</summary>
    public static bool AfterConfigureAwaitFalse(SyntaxNode? body, SyntaxNode node, SemanticModel model, out string why)
    {
        why = "";
        if (body is null) return false;
        var off = false;
        foreach (var aw in body.DescendantNodes(n => n == body || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                     .OfType<AwaitExpressionSyntax>())
        {
            if (aw.Span.End > node.SpanStart) break;
            if (IsConfigureAwaitFalse(aw.Expression, model))
            {
                off = true;
                var line = aw.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                why = $"after 'await ...ConfigureAwait(false)' (line {line}), on a thread-pool thread";
            }
            else if (model.GetTypeInfo(aw.Expression).Type is { Name: "NextTickAwaitable" } t &&
                     t.ContainingType?.Name == "Scheduler")
            {
                off = false;
            }
        }
        return off;
    }

    private static bool IsConfigureAwaitFalse(ExpressionSyntax e, SemanticModel model)
    {
        if (e is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } } inv ||
            inv.ArgumentList.Arguments.Count != 1)
            return false;
        var arg = inv.ArgumentList.Arguments[0].Expression;
        var c = model.GetConstantValue(arg);
        if (c.HasValue && c.Value is bool b) return !b;
        // ConfigureAwaitOptions (.NET 8): off the context unless ContinueOnCapturedContext is set.
        if (model.GetTypeInfo(arg).Type?.Name == "ConfigureAwaitOptions") return !arg.ToString().Contains("ContinueOnCapturedContext");
        return false;
    }
}

/// <summary>Small syntax/semantic helpers shared by the analyzers.</summary>
internal static class Syntax
{
    /// <summary>The innermost function (lambda, local function, method, accessor) that contains <paramref name="node"/>.</summary>
    public static SyntaxNode? EnclosingFunction(SyntaxNode node)
    {
        for (var n = node.Parent; n != null; n = n.Parent)
            if (n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
                return n;
        return null;
    }

    public static bool IsAsync(SyntaxNode? function) => function switch
    {
        AnonymousFunctionExpressionSyntax f => f.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
        LocalFunctionStatementSyntax lf => lf.Modifiers.Any(SyntaxKind.AsyncKeyword),
        MethodDeclarationSyntax md => md.Modifiers.Any(SyntaxKind.AsyncKeyword),
        _ => false,
    };

    public static SyntaxNode? FunctionBody(SyntaxNode? function) => function switch
    {
        AnonymousFunctionExpressionSyntax f => f.Body,
        LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
        BaseMethodDeclarationSyntax md => (SyntaxNode?)md.Body ?? md.ExpressionBody,
        AccessorDeclarationSyntax ad => (SyntaxNode?)ad.Body ?? ad.ExpressionBody,
        _ => null,
    };

    /// <summary>True when the type is Kiln.Mod or derives from it.</summary>
    public static bool IsModType(ITypeSymbol? t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.Name == "Mod" && b.ContainingNamespace?.ToDisplayString() == "Kiln" && b.ContainingAssembly?.Name == "Kiln.Api")
                return true;
        return false;
    }

    /// <summary>Walks every declaration of <paramref name="type"/> in this compilation.</summary>
    public static IEnumerable<(SyntaxNode Root, SemanticModel Model)> Declarations(INamedTypeSymbol type, Compilation compilation, CancellationToken ct)
    {
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            var root = r.GetSyntax(ct);
            if (!compilation.SyntaxTrees.Contains(root.SyntaxTree)) continue;
#pragma warning disable RS1030
            yield return (root, compilation.GetSemanticModel(root.SyntaxTree));
#pragma warning restore RS1030
        }
    }

    public static string Display(ISymbol s)
    {
        if (s is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove } acc &&
            acc.AssociatedSymbol is { } assoc)
            s = assoc;
        if (s is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor) return $"new {ctor.ContainingType.Name}";
        return s.ContainingType is { } t ? $"{t.Name}.{s.Name}" : s.Name;
    }
}
