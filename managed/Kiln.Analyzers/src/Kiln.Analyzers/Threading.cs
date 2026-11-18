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
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

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

    /// <summary>The game client's main thread (#82): Client.Tick handlers, Client.Post work, after <c>await
    /// Client.NextTick()</c>, callbacks marked <c>[ClientMainThread]</c>, and a Kiln task's client-world hops. It steps the
    /// client world and runs the game's UI frame. A game thread (KILN105 reports blocking there).</summary>
    ClientMain,

    /// <summary>The render thread of Kiln's overlay: callbacks marked <c>[RenderThread]</c> (a window's draw). A game
    /// thread.</summary>
    Render,
}

/// <summary>A Kiln task's execution scope at a point, for the rules that care whether a task still owns the
/// code (KILN105's task rows). <see cref="Active"/>: a task segment or tick hop runs here, with the scope
/// Kiln installs around it. <see cref="Escaped"/>: the code belongs to a task body, but it left the task
/// (<c>await …ConfigureAwait(false)</c>), so no scope is installed any more. <see cref="None"/>: no task
/// body in sight.</summary>
internal enum TaskScope
{
    None,
    Active,
    Escaped,
}

internal readonly struct ThreadContext
{
    public ThreadContext(ThreadKind kind, string where, TaskScope task = TaskScope.None, ThreadKind home = ThreadKind.Unknown)
    {
        Kind = kind;
        Where = where;
        Task = task;
        Home = home;
    }

    public ThreadKind Kind { get; }

    /// <summary>Human text that completes "this code runs ...", e.g. "on a thread-pool thread (Task.Run)".</summary>
    public string Where { get; }

    /// <summary>The Kiln task scope here (a task worker, a tick hop, or code that escaped its task).</summary>
    public TaskScope Task { get; }

    /// <summary>In a Kiln task body (#82): where its parameterless hops land, from where its <c>Context.Tasks.Run</c> was
    /// called: <see cref="ThreadKind.Tick"/> (the server world's tick), <see cref="ThreadKind.ClientMain"/> (the client
    /// world's step on the main thread), <see cref="ThreadKind.Game"/> when that is not known. Unknown outside tasks.</summary>
    public ThreadKind Home { get; }

    public ThreadContext WithTask(TaskScope task) => new(Kind, Where, task, Home);

    public ThreadContext WithHome(ThreadKind home) => new(Kind, Where, Task, home);

    /// <summary>Every kind but the internal Continue: the code's own flow (hops, ConfigureAwait) can change it.</summary>
    public static bool Flows(ThreadKind kind) => kind != ThreadKind.Continue;

    /// <summary>The game's own threads: the tick, the client's main and render threads, a Kiln callback's.</summary>
    public static bool OnGameThread(ThreadKind kind) => kind is ThreadKind.Tick or ThreadKind.Game or ThreadKind.ClientMain or ThreadKind.Render;

    public static readonly ThreadContext Unknown = new(ThreadKind.Unknown, "");
    public static readonly ThreadContext Continue = new(ThreadKind.Continue, "");
}

/// <summary>Classifies code by the thread it runs on (KILN101, KILN105). One per compilation.</summary>
internal sealed class ThreadClassifier
{
    private readonly Compilation _compilation;
    private readonly ConcurrentDictionary<IMethodSymbol, ThreadContext> _methods = new(SymbolEqualityComparer.Default);

    public ThreadClassifier(Compilation compilation) => _compilation = compilation;

    public ThreadContext Classify(SyntaxNode node, SemanticModel model, CancellationToken ct) =>
        Classify(node, model, ct, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

    private ThreadContext Classify(SyntaxNode node, SemanticModel model, CancellationToken ct, HashSet<ISymbol> visiting)
    {
        ct.ThrowIfCancellationRequested();
        // Code under the author's own thread check is never judged, wherever it sits.
        var child = node;
        for (var n = node.Parent; n != null; child = n, n = n.Parent)
        {
            switch (n)
            {
                case IfStatementSyntax ifs when child != ifs.Condition && MentionsThreadCheck(ifs.Condition):
                case ConditionalExpressionSyntax ce when child != ce.Condition && MentionsThreadCheck(ce.Condition):
                    return ThreadContext.Unknown;
            }
        }
        var function = Syntax.EnclosingFunction(node);
        if (function is null) return ThreadContext.Unknown;
        var entry = EntryContext(function, model, ct, visiting);
        // Even with an unknown entry, leaving through ConfigureAwait(false) or hopping to the tick inside the
        // function itself settles the thread from that point on (the behaviour KILN101 always had for plain methods).
        if (!ThreadContext.Flows(entry.Kind)) return entry;
        return FlowAt(function, node, entry, model, ct, visiting);
    }

    /// <summary>Where execution of <paramref name="function"/> starts: a lambda by the call it is handed to, a local
    /// function by its call sites (a local function runs where its caller runs), a method by its entry points and
    /// method-group uses. A lambda a library runs at once (Continue) starts where the code around it has got to.</summary>
    private ThreadContext EntryContext(SyntaxNode function, SemanticModel model, CancellationToken ct, HashSet<ISymbol> visiting)
    {
        switch (function)
        {
            case AnonymousFunctionExpressionSyntax f:
            {
                var use = ClassifyDelegateUse(f, model, ct);
                if (use.Kind != ThreadKind.Continue) return WithTaskHome(use, f, model, ct, visiting);
                var outer = OuterFunction(f);
                if (outer is null) return ThreadContext.Unknown;
                var outerEntry = EntryContext(outer, model, ct, visiting);
                return ThreadContext.Flows(outerEntry.Kind)
                    ? FlowAt(outer, f, outerEntry, model, ct, visiting)
                    : outerEntry;
            }
            case LocalFunctionStatementSyntax lf:
                return LocalFunctionEntry(lf, model, ct, visiting);
            case BaseMethodDeclarationSyntax md:
                return model.GetDeclaredSymbol(md, ct) is IMethodSymbol m ? ClassifyMethod(m, ct) : ThreadContext.Unknown;
            case AccessorDeclarationSyntax ad:
                return model.GetDeclaredSymbol(ad, ct) is IMethodSymbol acc ? ClassifyMethod(acc, ct) : ThreadContext.Unknown;
            default:
                return ThreadContext.Unknown;
        }
    }

    private static SyntaxNode? OuterFunction(SyntaxNode function)
    {
        for (var n = function.Parent; n != null; n = n.Parent)
            if (n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
                return n;
        return null;
    }

    /// <summary>A local function's entry thread is the meet of its uses: invoked, it starts on its caller's thread at
    /// the call; handed out as a delegate, on the thread that delegate runs on. An ordinary method called the same
    /// way is deliberately not followed (unknown helpers stay quiet); local functions are the one call the rule
    /// follows, because their whole meaning is "run here, where I am called". With no use in sight it keeps the
    /// enclosing function's entry, as before.</summary>
    private ThreadContext LocalFunctionEntry(LocalFunctionStatementSyntax lf, SemanticModel model, CancellationToken ct, HashSet<ISymbol> visiting)
    {
        if (model.GetDeclaredSymbol(lf, ct) is not { } symbol) return ThreadContext.Unknown;
        if (!visiting.Add(symbol)) return ThreadContext.Unknown;  // a recursive use: its outer call decides
        try
        {
            List<ThreadContext>? uses = null;
            foreach (var id in lf.SyntaxTree.GetRoot(ct).DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != symbol.Name) continue;
                if (Syntax.EnclosingFunction(id) == lf) continue;  // inside its own body (recursion, its declaration)
                var found = model.GetSymbolInfo(id, ct);
                if (!SymbolEqualityComparer.Default.Equals(found.Symbol?.OriginalDefinition, symbol.OriginalDefinition)) continue;
                ExpressionSyntax expr = id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma : id;
                ThreadContext use;
                if (expr.Parent is InvocationExpressionSyntax inv && inv.Expression == expr)
                {
                    // A direct call: the local function starts where this call has got to.
                    var caller = Syntax.EnclosingFunction(id);
                    if (caller is null) continue;
                    var callerEntry = EntryContext(caller, model, ct, visiting);
                    use = ThreadContext.Flows(callerEntry.Kind)
                        ? FlowAt(caller, id, callerEntry, model, ct, visiting)
                        : callerEntry;
                }
                else
                {
                    use = ClassifyDelegateUse(expr, model, ct);
                    if (use.Kind == ThreadKind.Continue)
                    {
                        var caller = Syntax.EnclosingFunction(id);
                        if (caller is null) continue;
                        var callerEntry = EntryContext(caller, model, ct, visiting);
                        use = ThreadContext.Flows(callerEntry.Kind)
                            ? FlowAt(caller, expr, callerEntry, model, ct, visiting)
                            : callerEntry;
                    }
                    else
                    {
                        use = WithTaskHome(use, expr, model, ct, visiting);
                    }
                }
                (uses ??= new List<ThreadContext>()).Add(use);
            }
            if (uses is null || uses.Count == 0)
                return OuterFunction(lf) is { } outer ? EntryContext(outer, model, ct, visiting) : ThreadContext.Unknown;
            return Meet(uses);
        }
        finally
        {
            visiting.Remove(symbol);
        }
    }

    /// <summary>The home of a task body handed to <c>Context.Tasks.Run</c>, or of an <c>OnTick</c>/<c>PostToTick</c> delegate
    /// without a world (#82): where the code around the call runs. In a task body, that body's home; on the tick, the
    /// server world's tick; on the main thread, the client world; anywhere else off the game threads (or unknown), the
    /// server world, as Kiln does; in another game callback, not known (Game).</summary>
    private ThreadContext WithTaskHome(ThreadContext use, SyntaxNode delegateExpr, SemanticModel model, CancellationToken ct,
        HashSet<ISymbol> visiting)
    {
        if (use.Task != TaskScope.Active) return use;
        if (use.Home is ThreadKind.Tick or ThreadKind.ClientMain) return use;  // named by its world argument
        var call = delegateExpr.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (call is null) return use;
        var site = Classify(call, model, ct, visiting);
        var known = site.Home is ThreadKind.Tick or ThreadKind.ClientMain or ThreadKind.Game;
        if (use.Kind == ThreadKind.Tick)
        {
            // an OnTick/PostToTick delegate with no world (and OrOnTick's onTick, which goes through ctx.OnTick) runs
            // where the task's parameterless hops land: its home, the server world's tick when not known
            var at = known ? site.Home : ThreadKind.Tick;
            var what = use.Where.Contains("ctx.PostToTick") ? "ctx.PostToTick"
                : use.Where.Contains("OrOnTick") ? "WorldConcurrent.OrOnTick"
                : "ctx.OnTick";
            return Landing(at, what).WithHome(at);
        }
        // Context.Tasks.Run: a task started in a task takes the world of the hop it is started in, or (in a segment) its
        // parent's; elsewhere the thread of the call decides
        var home = site.Task == TaskScope.Active
            ? site.Kind is ThreadKind.Tick or ThreadKind.ClientMain ? site.Kind
            : known ? site.Home
            : ThreadKind.Tick
            : site.Kind switch
            {
                ThreadKind.Tick => ThreadKind.Tick,
                ThreadKind.ClientMain => ThreadKind.ClientMain,
                ThreadKind.Game => ThreadKind.Game,
                _ => ThreadKind.Tick,  // the render thread, off the game threads, unknown: the server world
            };
        return use.WithHome(home);
    }

    /// <summary>Where a task's hop of <paramref name="world"/> lands (a ThreadKind home), as the text says it.</summary>
    internal static ThreadContext Landing(ThreadKind world, string what) => world switch
    {
        ThreadKind.ClientMain => new ThreadContext(ThreadKind.ClientMain, $"the game's main thread (the client world's step, {what})", TaskScope.Active),
        ThreadKind.Tick => new ThreadContext(ThreadKind.Tick, $"the tick thread ({what})", TaskScope.Active),
        _ => new ThreadContext(ThreadKind.Game, $"a game thread ({what} of a task whose world is not known here)", TaskScope.Active),
    };

    /// <summary>The least tick-sure of several ways execution can arrive: Tick only when every way is the tick.</summary>
    private static ThreadContext Meet(IEnumerable<ThreadContext> contexts)
    {
        var seen = contexts.ToList();
        if (seen.Count == 0) return ThreadContext.Unknown;
        ThreadContext? best = null;
        var mixedGame = false;
        foreach (var c in seen)
        {
            if (best is null) { best = c; continue; }
            if (c.Kind != best.Value.Kind && ThreadContext.OnGameThread(c.Kind) && ThreadContext.OnGameThread(best.Value.Kind))
                mixedGame = true;  // the tick and the main thread, say: some game thread
            if (Rank(c.Kind) > Rank(best.Value.Kind)) best = c;
        }
        var homes = seen.Select(c => c.Home).Distinct().ToList();
        var home = homes.Count == 1 ? homes[0] : ThreadKind.Game;
        if (mixedGame && Rank(best!.Value.Kind) == 1)
            best = new ThreadContext(ThreadKind.Game, "a game thread (more than one way in)", best.Value.Task, home);
        // The task scope survives a join only when every way in has it; a mix of in-task and other
        // ways (or one escaped way) leaves the code in a task body without a proven scope.
        var task = seen.All(c => c.Task == TaskScope.Active) ? TaskScope.Active
            : seen.All(c => c.Task == TaskScope.None) ? TaskScope.None
            : TaskScope.Escaped;
        return best!.Value.WithTask(task).WithHome(home);
    }

    private static int Rank(ThreadKind kind) => kind switch
    {
        ThreadKind.Tick => 0,
        ThreadKind.Game or ThreadKind.ClientMain or ThreadKind.Render => 1,
        ThreadKind.Off => 2,
        _ => 3,
    };

    // ---- flow inside one function: hops, gate windows and ConfigureAwait(false) over the control-flow graph ----

    private readonly ConcurrentDictionary<SyntaxNode, ControlFlowGraph?> _graphs = new();

    /// <summary>The thread at <paramref name="node"/> inside <paramref name="function"/>, whose execution starts as
    /// <paramref name="entry"/>. A plain <c>await</c> resumes on the context it was made on, so it changes nothing;
    /// a genuine <c>await ctx.ToTick()</c> / <c>await Scheduler.NextTick()</c> lands (and stays) on the tick until
    /// <c>await ctx.NextGateWindow()</c> or an <c>await …ConfigureAwait(false)</c> takes the code back off it.</summary>
    private ThreadContext FlowAt(SyntaxNode function, SyntaxNode node, ThreadContext entry, SemanticModel model, CancellationToken ct, HashSet<ISymbol> visiting)
    {
        var body = Syntax.FunctionBody(function);
        if (body is null) return entry;
        var graph = GraphFor(function, model, ct);
        if (graph is null || graph.Blocks.Length == 0) return LinearFlow(body, node, entry, model);
        var flow = new FlowGraph(graph, model);
        return flow.At(entry, node) ?? LinearFlow(body, node, entry, model);
    }

    /// <summary>The control-flow graph of one function. Only a method-like body has a graph of its own in Roslyn;
    /// a lambda's or local function's graph hangs off its enclosing function's graph, so build those outside in.</summary>
    private ControlFlowGraph? GraphFor(SyntaxNode function, SemanticModel model, CancellationToken ct)
    {
        if (_graphs.TryGetValue(function, out var have)) return have;
        ControlFlowGraph? graph = null;
        try
        {
            switch (function)
            {
                case BaseMethodDeclarationSyntax or AccessorDeclarationSyntax:
                    graph = ControlFlowGraph.Create(function, model, ct);
                    break;
                case AnonymousFunctionExpressionSyntax:
                {
                    if (OuterFunction(function) is { } outer && GraphFor(outer, model, ct) is { } parent)
                    {
                        var op = parent.Blocks.SelectMany(b => b.Operations)
                            .SelectMany(o => o.DescendantsAndSelf())
                            .OfType<IFlowAnonymousFunctionOperation>()
                            .FirstOrDefault(o => o.Syntax == function);
                        if (op is not null) graph = parent.GetAnonymousFunctionControlFlowGraph(op, ct);
                    }
                    break;
                }
                case LocalFunctionStatementSyntax lf:
                {
                    if (OuterFunction(function) is { } outer && GraphFor(outer, model, ct) is { } parent &&
                        model.GetDeclaredSymbol(lf, ct) is { } symbol)
                        graph = parent.GetLocalFunctionControlFlowGraph(symbol, ct);
                    break;
                }
            }
        }
        catch (Exception)
        {
            graph = null;
        }
        _graphs[function] = graph;
        return graph;
    }

    /// <summary>Fallback when no graph can be built: awaits in text order before the node (the old approximation).</summary>
    private ThreadContext LinearFlow(SyntaxNode body, SyntaxNode node, ThreadContext entry, SemanticModel model)
    {
        var state = entry;
        foreach (var aw in body.DescendantNodes(n => n == body || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                     .OfType<AwaitExpressionSyntax>())
        {
            if (aw.Span.End > node.SpanStart || aw.Span.Contains(node.Span)) continue;
            state = ApplyAwait(state, aw, model);
        }
        return state;
    }

    private static ThreadContext ApplyAwait(ThreadContext state, SyntaxNode aw, SemanticModel model)
    {
        var change = aw is AwaitExpressionSyntax written ? AwaitChange(state, written, model) : ImplicitAwaitChange(state, aw, model);
        return change ?? state;
    }

    /// <summary>An await the compiler writes: <c>await foreach (… in xs.ConfigureAwait(false))</c> leaves the context at
    /// its first MoveNextAsync, so the loop body and the code after it run off it; <c>await using (x.ConfigureAwait(false))</c>
    /// (or <c>await using var</c>) leaves it at DisposeAsync, so the code after the using does. Null when it comes back
    /// to wherever it was made.</summary>
    private static ThreadContext? ImplicitAwaitChange(ThreadContext state, SyntaxNode aw, SemanticModel model)
    {
        var statement = aw.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        IEnumerable<ExpressionSyntax?> configured;
        string what;
        switch (statement)
        {
            case CommonForEachStatementSyntax fe when fe.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                configured = new[] { fe.Expression };
                what = "await foreach";
                break;
            case UsingStatementSyntax us when us.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                configured = us.Declaration is { } d ? d.Variables.Select(v => v.Initializer?.Value) : new[] { us.Expression };
                what = "await using";
                break;
            case LocalDeclarationStatementSyntax ld when ld.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                configured = ld.Declaration.Variables.Select(v => v.Initializer?.Value);
                what = "await using";
                break;
            default:
                return null;
        }
        if (!configured.Any(e => ConfiguredFalse(e, model))) return null;
        var line = statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new ThreadContext(ThreadKind.Off, $"after '{what} ...ConfigureAwait(false)' (line {line}), on a thread-pool thread",
            state.Task == TaskScope.None ? TaskScope.None : TaskScope.Escaped);
    }

    /// <summary>A <c>.ConfigureAwait(false)</c> anywhere along a call chain (<c>xs.WithCancellation(ct).ConfigureAwait(false)</c>
    /// and the other order alike).</summary>
    private static bool ConfiguredFalse(ExpressionSyntax? e, SemanticModel model)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        while (e is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv)
        {
            if (IsConfigureAwaitFalse(inv, model)) return true;
            e = ma.Expression;
        }
        return false;
    }

    /// <summary>What finishing this await does to the thread, or null when the continuation simply comes back to
    /// wherever the await was made. Decided by the type of the awaited value, so a hop kept in a variable
    /// (<c>var hop = ctx.ToTick(); await hop;</c>) counts too. Only the real Kiln awaitables hop: a look-alike named
    /// ToTick on a user type, an awaited <c>ctx.OnTick(...)</c> (it resumes in the task) and any other await leave the
    /// thread as it was. A task's own hops (<c>ctx.ToTick()</c>, <c>ctx.NextGateWindow()</c>) run under the
    /// task's scope; <c>ConfigureAwait(false)</c> is where that scope ends, and only a task hop brings it back.</summary>
    private static ThreadContext? AwaitChange(ThreadContext state, AwaitExpressionSyntax aw, SemanticModel model)
    {
        var expr = Unwrap(aw.Expression);
        if (IsConfigureAwaitFalse(expr, model))
        {
            var line = aw.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return new ThreadContext(ThreadKind.Off, $"after 'await ...ConfigureAwait(false)' (line {line}), on a thread-pool thread",
                state.Task == TaskScope.None ? TaskScope.None : TaskScope.Escaped);
        }
        var type = model.GetTypeInfo(expr).Type;
        if (IsKilnType(type, "KilnTaskContext.TickHop"))
        {
            // ctx.ToTick() lands where the task belongs; ctx.ToTick(WorldKind.X) where it says (#82)
            var world = HopWorld(expr, model, state.Home);
            return world == ThreadKind.Tick
                ? new ThreadContext(ThreadKind.Tick, "the tick thread (a tick hop)", TaskScope.Active, state.Home)
                : Landing(world, world == ThreadKind.ClientMain ? "a client-world hop" : "a tick hop").WithHome(state.Home);
        }
        if (IsKilnType(type, "Scheduler.NextTickAwaitable"))
            return new ThreadContext(ThreadKind.Tick, "the tick thread (a tick hop)", state.Task, state.Home);
        if (IsKilnType(type, "KilnTaskContext.GateWindow"))
            return new ThreadContext(ThreadKind.Off, "after 'await ...NextGateWindow()', on a Kiln task worker", TaskScope.Active, state.Home);
        if (IsKilnType(type, "Client.NextTickAwaitable"))
            // the main thread at the next client step; inside a task it has left the task (KILN105 says so at the await)
            return new ThreadContext(ThreadKind.ClientMain, "the game's main thread (after 'await Client.NextTick()')",
                state.Task == TaskScope.None ? TaskScope.None : TaskScope.Escaped, state.Home);
        return null;
    }

    /// <summary>The world a hop's awaitable goes to: <c>ctx.ToTick()</c> the task's home, <c>ctx.ToTick(WorldKind.Client)</c>
    /// the client world's step, <c>ToTick(WorldKind.Server)</c> the tick; a world that is not a constant, or a hop kept in a
    /// variable, is not known here (Game).</summary>
    internal static ThreadKind HopWorld(ExpressionSyntax awaited, SemanticModel model, ThreadKind home)
    {
        if (Unwrap(awaited) is not InvocationExpressionSyntax inv || model.GetOperation(inv) is not IInvocationOperation call ||
            !IsKilnMember(call.TargetMethod.OriginalDefinition, "KilnTaskContext", "ToTick"))
            return ThreadKind.Game;
        return WorldArgument(call, home);
    }

    /// <summary>A hop member's world argument (#82): none, the task's <paramref name="home"/>; the constant Server or
    /// Client, the tick or the main thread; anything else, not known (Game).</summary>
    internal static ThreadKind WorldArgument(IInvocationOperation call, ThreadKind home)
    {
        var arg = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "world");
        if (arg is null) return home is ThreadKind.Tick or ThreadKind.ClientMain ? home : ThreadKind.Game;
        return arg.Value.ConstantValue is { HasValue: true, Value: int w } ? w switch { 0 => ThreadKind.Tick, 2 => ThreadKind.ClientMain, _ => ThreadKind.Game }
            : ThreadKind.Game;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        return e;
    }

    /// <summary>A member of one exact Kiln type, by symbol: never a name look-alike on somebody else's type.</summary>
    internal static bool IsKilnMember(IMethodSymbol symbol, string type, string name) =>
        symbol.Name == name && IsKilnType(symbol.ContainingType, type);

    /// <summary>One exact type of a Kiln assembly (nested types as <c>Outer.Inner</c>).</summary>
    internal static bool IsKilnType(ITypeSymbol? type, string name) =>
        type is not null && KilnApis.IsKilnAssembly(type.ContainingAssembly) && type.OriginalDefinition.ToDisplayString() == "Kiln." + name;

    /// <summary>Must-analysis over one function's graph: a point is on the tick only when every path to it is.</summary>
    private sealed class FlowGraph
    {
        private readonly ControlFlowGraph _graph;
        private readonly SemanticModel _model;
        private readonly Dictionary<int, ThreadContext?> _regionCache = new();

        public FlowGraph(ControlFlowGraph graph, SemanticModel model)
        {
            _graph = graph;
            _model = model;
        }

        public ThreadContext? At(ThreadContext entry, SyntaxNode node)
        {
            var (ins, _) = Solve(entry);
            foreach (var block in _graph.Blocks)
            {
                if (block.Kind is BasicBlockKind.Entry or BasicBlockKind.Exit) continue;
                // The operation the caller's node belongs to: the smallest operation whose syntax covers it.
                IOperation? host = null;
                foreach (var op in block.Operations)
                foreach (var d in op.DescendantsAndSelf())
                    if (d.Syntax == node || d.Syntax.Span.Contains(node.Span) && (host is null || d.Syntax.Span.Length < host.Syntax.Span.Length))
                        host = d;
                if (host is null) continue;
                if (ins[block.Ordinal] is not { } state) return null;  // not reachable as far as the graph knows
                foreach (var aw in Awaits(block))
                {
                    if (aw.Span.End > node.SpanStart || aw.Span.Contains(node.Span)) continue;
                    state = ApplyAwait(state, aw, _model);
                }
                return state;
            }
            return null;
        }

        private (ThreadContext?[] Ins, ThreadContext?[] Outs) Solve(ThreadContext entry)
        {
            var ins = new ThreadContext?[_graph.Blocks.Length];
            var outs = new ThreadContext?[_graph.Blocks.Length];
            for (var round = 0; round < _graph.Blocks.Length * 6 + 16; round++)
            {
                var changed = false;
                foreach (var block in _graph.Blocks)
                {
                    ThreadContext? next = null;
                    if (block.Kind == BasicBlockKind.Entry) next = entry;
                    else
                    {
                        List<ThreadContext>? arrivals = null;
                        foreach (var pred in block.Predecessors)
                        {
                            if (outs[pred.Source.Ordinal] is not { } from) continue;
                            foreach (var region in pred.FinallyRegions) from = ThroughRegion(region, from) ?? from;
                            (arrivals ??= new List<ThreadContext>()).Add(from);
                        }
                        if (RegionSeed(block, ins) is { } seed) (arrivals ??= new List<ThreadContext>()).Add(seed);
                        if (arrivals is { Count: > 0 }) next = Meet(arrivals);
                    }
                    if (next is null) continue;
                    changed |= Widen(ref ins[block.Ordinal], next.Value);
                    changed |= Widen(ref outs[block.Ordinal], Transfer(block, ins[block.Ordinal]!.Value));
                }
                if (!changed) break;
            }
            return (ins, outs);
        }

        /// <summary>Joins an arrival into what a point had so far (<see cref="Meet"/>): the least tick-sure thread, and
        /// a task scope that survives only when every arrival has it. Both count, so a <c>ConfigureAwait(false)</c> that
        /// comes back round a loop reaches the loop's head even when the first way in was already off the tick. True
        /// when anything changed.</summary>
        private static bool Widen(ref ThreadContext? had, ThreadContext next)
        {
            if (had is not { } h)
            {
                had = next;
                return true;
            }
            var joined = Meet(new[] { h, next });
            if (Rank(joined.Kind) == Rank(h.Kind) && joined.Task == h.Task) return false;
            had = joined;
            return true;
        }

        /// <summary>The state a catch or finally block starts with: an exception (or the finally's run) can begin at
        /// any point of its try, so the meet of every state inside the try (each block's entry, and after each of its
        /// awaits). Null when its try is not judged yet.</summary>
        private ThreadContext? RegionSeed(BasicBlock block, ThreadContext?[] ins)
        {
            var region = block.EnclosingRegion;
            if (region is null || region.Kind is not (ControlFlowRegionKind.Catch or ControlFlowRegionKind.Finally)) return null;
            if (region.FirstBlockOrdinal != block.Ordinal) return null;
            var parent = region.EnclosingRegion;
            if (parent is null) return null;
            List<ThreadContext>? states = null;
            foreach (var nested in parent.NestedRegions)
            {
                if (nested.Kind != ControlFlowRegionKind.Try) continue;
                for (var o = nested.FirstBlockOrdinal; o <= nested.LastBlockOrdinal && o < ins.Length; o++)
                {
                    if (ins[o] is not { } s) continue;
                    (states ??= new List<ThreadContext>()).Add(s);
                    foreach (var aw in Awaits(_graph.Blocks[o])) states.Add(s = ApplyAwait(s, aw, _model));
                }
            }
            return states is { Count: > 0 } ? Meet(states) : null;
        }

        /// <summary>Runs a finally region over an arriving state (its disposal awaits count before the code after).</summary>
        private ThreadContext? ThroughRegion(ControlFlowRegion region, ThreadContext from)
        {
            var key = (region.FirstBlockOrdinal * 4 + Rank(from.Kind)) * 3 + (int)from.Task;
            if (_regionCache.TryGetValue(key, out var cached)) return cached;
            _regionCache[key] = null;  // a finally that (impossibly) re-enters itself
            ThreadContext? result = null;
            var ins = new ThreadContext?[region.LastBlockOrdinal + 1];
            var outs = new ThreadContext?[region.LastBlockOrdinal + 1];
            for (var round = 0; round < 32; round++)
            {
                var changed = false;
                for (var o = region.FirstBlockOrdinal; o <= region.LastBlockOrdinal; o++)
                {
                    var block = _graph.Blocks[o];
                    List<ThreadContext>? arrivals = null;
                    if (o == region.FirstBlockOrdinal) arrivals = new List<ThreadContext> { from };
                    foreach (var pred in block.Predecessors)
                    {
                        if (pred.Source.Ordinal < region.FirstBlockOrdinal || pred.Source.Ordinal > region.LastBlockOrdinal) continue;
                        if (outs[pred.Source.Ordinal] is { } s) (arrivals ??= new List<ThreadContext>()).Add(s);
                    }
                    if (arrivals is not { Count: > 0 }) continue;
                    changed |= Widen(ref ins[o], Meet(arrivals));
                    changed |= Widen(ref outs[o], Transfer(block, ins[o]!.Value));
                }
                if (!changed) break;
            }
            List<ThreadContext>? exits = null;
            for (var o = region.FirstBlockOrdinal; o <= region.LastBlockOrdinal; o++)
            {
                var block = _graph.Blocks[o];
                var leaves = (block.FallThroughSuccessor is null || block.FallThroughSuccessor.Destination is null ||
                              block.FallThroughSuccessor.Destination.Ordinal < region.FirstBlockOrdinal || block.FallThroughSuccessor.Destination.Ordinal > region.LastBlockOrdinal) &&
                             (block.ConditionalSuccessor is null || block.ConditionalSuccessor.Destination is null ||
                              block.ConditionalSuccessor.Destination.Ordinal < region.FirstBlockOrdinal || block.ConditionalSuccessor.Destination.Ordinal > region.LastBlockOrdinal);
                if (leaves && outs[o] is { } s) (exits ??= new List<ThreadContext>()).Add(s);
            }
            result = exits is { Count: > 0 } ? Meet(exits) : outs[region.LastBlockOrdinal];
            _regionCache[key] = result;
            return result;
        }

        private ThreadContext Transfer(BasicBlock block, ThreadContext state)
        {
            foreach (var aw in Awaits(block)) state = ApplyAwait(state, aw, _model);
            return state;
        }

        /// <summary>Every await this block runs (its operations, then its branch value), in the order they finish, once
        /// each: the written <c>await</c>s, and the ones <c>await foreach</c> (MoveNextAsync, in the loop's head) and
        /// <c>await using</c> (DisposeAsync, in its finally) make. A nested function's awaits are not in the block: they
        /// run when that function runs.</summary>
        private static List<SyntaxNode> Awaits(BasicBlock block)
        {
            var seen = new HashSet<SyntaxNode>();
            var list = new List<SyntaxNode>();
            void Collect(IOperation root)
            {
                foreach (var op in root.DescendantsAndSelf())
                    if (op is IAwaitOperation aw && seen.Add(aw.Syntax)) list.Add(aw.Syntax);
            }
            foreach (var op in block.Operations) Collect(op);
            if (block.BranchValue is { } branch) Collect(branch);
            list.Sort((a, b) => a.Span.End.CompareTo(b.Span.End));
            return list;
        }
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
            if (c.Name == "ImpactNativeAttribute" && ns == "Kiln") return new ThreadContext(ThreadKind.Off, "an [ImpactNative] handler (the simulation thread or a worker)");
            if (c.Name == "SubscribeAttribute" && ns == "Kiln") return new ThreadContext(ThreadKind.Tick, "the tick thread (a [Subscribe] handler)");
            if (c.Name == "KilnTestAttribute" && ns == "Kiln.Testing") return new ThreadContext(ThreadKind.Tick, "the tick thread (a [KilnTest] method)");
            // Kiln.Chat runs every command (chat, console folder, remote tools) through Commands.Execute, on the tick.
            if (c.Name == "CommandAttribute" && ns == "Kiln") return new ThreadContext(ThreadKind.Tick, "the tick thread (a [Command] handler)");
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
                if (ThreadContext.OnGameThread(use.Kind) && best.Kind == ThreadKind.Unknown) best = use;
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
                if (target is null) return ThreadContext.Unknown;
                // a Kiln parameter that says its thread (#82): [ClientMainThread] (a UI value provider, a slot
                // subscription), [RenderThread]
                if (KilnApis.IsKilnAssembly(target.ContainingAssembly) &&
                    (model.GetOperation(arg, ct) as IArgumentOperation)?.Parameter is { } parameter &&
                    MarkedThread(parameter) is { } marked)
                    return marked;
                // WorldConcurrent.OrOnTick runs offTick at once where it is called, and onTick through ctx.OnTick.
                if (IsKilnMember(target.OriginalDefinition, "WorldConcurrent", "OrOnTick"))
                    return (model.GetOperation(arg, ct) as IArgumentOperation)?.Parameter?.Name switch
                    {
                        "offTick" => ThreadContext.Continue,
                        "onTick" => new ThreadContext(ThreadKind.Tick, "the tick thread (WorldConcurrent.OrOnTick)", TaskScope.Active),
                        _ => ThreadContext.Unknown,
                    };
                return ClassifyCallTarget(target, call, model);
            }
            case AssignmentExpressionSyntax asg when asg.Right == e && asg.IsKind(SyntaxKind.AddAssignmentExpression):
                return model.GetSymbolInfo(asg.Left, ct).Symbol is IEventSymbol ev ? ClassifyEvent(ev) : ThreadContext.Unknown;
            default:
                return ThreadContext.Unknown;
        }
    }

    /// <summary>A parameter (or its delegate's first parameter type) marked with a Kiln thread attribute.</summary>
    private static ThreadContext? MarkedThread(IParameterSymbol parameter)
    {
        foreach (var a in parameter.GetAttributes())
        {
            if (a.AttributeClass is not { } c || c.ContainingNamespace?.ToDisplayString() != "Kiln") continue;
            if (c.Name == "ClientMainThreadAttribute")
                return new ThreadContext(ThreadKind.ClientMain, $"the game's main thread (a [ClientMainThread] callback: {parameter.ContainingSymbol.Name})");
            if (c.Name == "RenderThreadAttribute")
                return new ThreadContext(ThreadKind.Render, $"the render thread (a [RenderThread] callback: {parameter.ContainingSymbol.Name})");
        }
        // Action<UiDraw>: the draw object exists only on the render thread
        if (parameter.Type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke } &&
            invoke.Parameters.Any(p => p.Type.GetAttributes().Any(a => a.AttributeClass is { Name: "RenderThreadAttribute" } rc &&
                                                                       rc.ContainingNamespace?.ToDisplayString() == "Kiln")))
            return new ThreadContext(ThreadKind.Render, $"the render thread (a draw callback: {parameter.ContainingSymbol.Name})");
        return null;
    }

    private ThreadContext ClassifyCallTarget(IMethodSymbol target, SyntaxNode call, SemanticModel model)
    {
        target = target.ReducedFrom ?? target;
        var type = target.ContainingType?.OriginalDefinition;
        var ns = type?.ContainingNamespace?.ToDisplayString();
        var tn = type?.Name;
        var name = target.Name;
        if (ns == "Kiln" && tn == "Scheduler" && name == "Post") return new ThreadContext(ThreadKind.Tick, "the tick thread (Scheduler.Post)");
        // Kiln tasks: the Run delegate starts on a Kiln worker; an OnTick/PostToTick delegate runs on the tick of the world
        // its argument names (#82), or of the task's own world (EntryContext completes that from the call site).
        if (IsKilnMember(target.OriginalDefinition, "KilnTasks", "Run")) return Off("on a Kiln task worker (Context.Tasks.Run)", TaskScope.Active);
        foreach (var hop in new[] { "OnTick", "PostToTick" })
        {
            if (!IsKilnMember(target.OriginalDefinition, "KilnTaskContext", hop)) continue;
            var world = model.GetOperation(call) is IInvocationOperation op ? WorldArgument(op, ThreadKind.Unknown) : ThreadKind.Game;
            var named = target.Parameters.Any(p => p.Name == "world");
            if (!named) return new ThreadContext(ThreadKind.Tick, $"the tick thread (ctx.{hop})", TaskScope.Active);
            return Landing(world, $"ctx.{hop}").WithHome(world is ThreadKind.Tick or ThreadKind.ClientMain ? world : ThreadKind.Unknown);
        }
        // the game client's main thread: Kiln.Client's tick and posts, and callbacks marked [ClientMainThread]
        if (IsKilnMember(target.OriginalDefinition, "Client", "Post"))
            return new ThreadContext(ThreadKind.ClientMain, "the game's main thread (Client.Post)");
        if (ns == "Kiln" && (tn, name) is ("Events", "On") or ("ModMessages", "On"))
            return new ThreadContext(ThreadKind.Tick, $"the tick thread (a {tn}.On handler)");
        // Command handlers: Kiln.Chat's Commands.Execute runs them on the tick (chat in Pre, the console folder and
        // remote tools from a service tick).
        if (ns == "Kiln" && (tn, name) is ("Commands", "Register") or ("LiteralNode", "Executes") or ("ArgumentNode", "Executes"))
            return new ThreadContext(ThreadKind.Tick, $"the tick thread ({(tn == "ArgumentNode" ? "an" : "a")} {tn}.{name} handler)");
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
        if (ns == "Kiln" && (tn, ev.Name) is ("Chat", "Received"))
            return new ThreadContext(ThreadKind.Tick, "the tick thread (a Chat.Received handler)");
        if (ns == "Kiln" && tn is "TickEvents" or "GameLogEvents")
            return new ThreadContext(ThreadKind.Tick, $"the tick thread (a {(tn == "TickEvents" ? "Tick" : "GameLog")}.{ev.Name} handler)");
        if (ns == "Kiln" && tn == "Client" && ev.Name == "Tick" && KilnApis.IsKilnAssembly(ev.ContainingAssembly))
            return new ThreadContext(ThreadKind.ClientMain, "the game's main thread (a Client.Tick handler)");
        if (KilnApis.IsKilnAssembly(ev.ContainingAssembly) &&
            ev.GetAttributes().Any(a => a.AttributeClass is { Name: "ClientMainThreadAttribute" } c && c.ContainingNamespace?.ToDisplayString() == "Kiln"))
            return new ThreadContext(ThreadKind.ClientMain, $"the game's main thread (a {tn}.{ev.Name} handler)");
        if (KilnApis.IsKilnAssembly(ev.ContainingAssembly))
            return new ThreadContext(ThreadKind.Game, $"a game thread (a Kiln event handler: {tn}.{ev.Name})");
        return ThreadContext.Unknown;
    }

    private static ThreadContext Off(string where, TaskScope task = TaskScope.None) => new(ThreadKind.Off, where, task);

    private static bool MentionsThreadCheck(ExpressionSyntax cond) =>
        cond.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.ValueText is "IsTickThread" or "IsClientThread");

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
