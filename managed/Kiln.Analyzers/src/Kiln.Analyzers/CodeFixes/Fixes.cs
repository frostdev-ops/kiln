// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Kiln.Analyzers;

/// <summary>KILN101: wrap the statement (or expression body) in Scheduler.Post(() => ...).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(TickThreadCodeFix)), Shared]
public sealed class TickThreadCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.TickThreadOffThread.Id);
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(TickThreadAnalyzer.SchedulerKey, out var scheduler) || scheduler is null) return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
        if (root is null || node is null) return;
        var fn = Syntax.EnclosingFunction(node);
        var stmt = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        SyntaxNode? target = null;
        if (stmt is ExpressionStatementSyntax && Syntax.EnclosingFunction(stmt) == fn) target = stmt;
        else if (fn is AnonymousFunctionExpressionSyntax { ExpressionBody: { } body } && body.Span.Contains(node.Span)) target = body;
        if (target is null) return;
        context.RegisterCodeFix(CodeAction.Create($"Run it on the tick thread ({scheduler}.Post)", ct =>
        {
            SyntaxNode replacement = target is ExpressionStatementSyntax es
                ? ExpressionStatement(Post(scheduler, es.Expression.WithoutTrivia())).WithTriviaFrom(es)
                : Post(scheduler, ((ExpressionSyntax)target).WithoutTrivia()).WithTriviaFrom(target);
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(target, replacement.WithAdditionalAnnotations(Formatter.Annotation))));
        }, nameof(TickThreadCodeFix)), d);
    }

    private static InvocationExpressionSyntax Post(string scheduler, ExpressionSyntax body) =>
        InvocationExpression(ParseExpression($"{scheduler}.Post"),
            ArgumentList(SingletonSeparatedList(Argument(ParenthesizedLambdaExpression(body)))));
}

/// <summary>KILN102: async void → async Task.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AsyncVoidCodeFix)), Shared]
public sealed class AsyncVoidCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.AsyncVoid.Id);
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(AsyncVoidAnalyzer.FixKey, out var fix) || fix != "task") return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var method = root?.FindToken(d.Location.SourceSpan.Start).Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (root is null || method is null) return;
        context.RegisterCodeFix(CodeAction.Create("Return Task", ct =>
        {
            var task = ParseTypeName("System.Threading.Tasks.Task").WithTriviaFrom(method.ReturnType).WithAdditionalAnnotations(Simplifier.Annotation);
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(method, method.WithReturnType(task))));
        }, nameof(AsyncVoidCodeFix)), d);
    }
}

/// <summary>KILN103: add `Event -= Handler;` to OnUnload.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ProcessWideEventCodeFix)), Shared]
public sealed class ProcessWideEventCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.ProcessWideEvent.Id);
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(ProcessWideAnalyzer.FixKey, out var fix) || fix != "unsubscribe") return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var add = root?.FindNode(d.Location.SourceSpan).AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        var type = add?.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (root is null || add is null || type is null) return;
        context.RegisterCodeFix(CodeAction.Create("Remove the handler in OnUnload", ct =>
        {
            var remove = ExpressionStatement(AssignmentExpression(SyntaxKind.SubtractAssignmentExpression,
                add.Left.WithoutTrivia(), add.Right.WithoutTrivia()));
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(type, OnUnload.Append(type, remove))));
        }, nameof(ProcessWideEventCodeFix)), d);
    }
}

/// <summary>KILN105: .Result / .Wait() / .GetAwaiter().GetResult() → await; Thread.Sleep(n) → await Task.Delay(n).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BlockingCodeFix)), Shared]
public sealed class BlockingCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.BlockingCall.Id);
    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(BlockingAnalyzer.FixKey, out var fix) || fix is null) return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true) as ExpressionSyntax;
        if (root is null || node is null) return;
        ExpressionSyntax? replacement = (fix, node) switch
        {
            ("awaitResult", MemberAccessExpressionSyntax ma) => Await(ma.Expression),
            ("awaitWait", InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }) => Await(ma.Expression),
            ("awaitGetResult", InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ga } }
            }) => Await(ga.Expression),
            ("delay", InvocationExpressionSyntax sleep) => Await(InvocationExpression(
                ParseExpression("System.Threading.Tasks.Task.Delay").WithAdditionalAnnotations(Simplifier.Annotation), sleep.ArgumentList)),
            _ => null,
        };
        if (replacement is null) return;
        var title = fix == "delay" ? "Use await Task.Delay" : "Use await";
        context.RegisterCodeFix(CodeAction.Create(title, ct =>
        {
            var r = (node.Parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax ? ParenthesizedExpression(replacement) : replacement)
                .WithTriviaFrom(node);
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(node, r)));
        }, nameof(BlockingCodeFix) + fix), d);
    }

    private static ExpressionSyntax Await(ExpressionSyntax e) => AwaitExpression(e.WithoutTrivia());
}

/// <summary>KILN106: add `member?.Dispose();` to OnUnload.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UnreleasedCodeFix)), Shared]
public sealed class UnreleasedCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.Unreleased.Id);
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(UnreleasedAnalyzer.FixKey, out var fix) || fix != "dispose" ||
            !d.Properties.TryGetValue(UnreleasedAnalyzer.MemberKey, out var member) || member is null)
            return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var type = root?.FindNode(d.Location.SourceSpan).Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (root is null || type is null) return;
        context.RegisterCodeFix(CodeAction.Create($"Dispose '{member}' in OnUnload", ct =>
        {
            var stmt = ParseStatement($"{member}?.Dispose();");
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(type, OnUnload.Append(type, stmt))));
        }, nameof(UnreleasedCodeFix)), d);
    }
}

/// <summary>Appends a statement to the class's OnUnload override, creating it when missing.</summary>
internal static class OnUnload
{
    public static ClassDeclarationSyntax Append(ClassDeclarationSyntax type, StatementSyntax statement)
    {
        statement = statement.WithAdditionalAnnotations(Formatter.Annotation);
        var existing = type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m =>
            m.Identifier.ValueText == "OnUnload" && m.ParameterList.Parameters.Count == 0 && m.Modifiers.Any(SyntaxKind.OverrideKeyword));
        if (existing is not null)
        {
            var body = existing.Body ?? Block(existing.ExpressionBody is { } eb ? new StatementSyntax[] { ExpressionStatement(eb.Expression) } : new StatementSyntax[0]);
            var updated = existing.WithBody(body.AddStatements(statement)).WithExpressionBody(null).WithSemicolonToken(default)
                .WithAdditionalAnnotations(Formatter.Annotation);
            return type.ReplaceNode(existing, updated);
        }
        var method = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "OnUnload")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword)))
            .WithBody(Block(statement))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return type.AddMembers(method);
    }
}
