// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
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

/// <summary>KILN120: "Check the result" (a local and <c>if (!result.Ok)</c>, with <c>Log.Warn</c> when a Log is in
/// reach, else an empty block with a comment) and "Drop it on purpose" (<c>_ = ...</c>). Check is offered only where the
/// analyzer found it sound (a result that cannot be null, not reached through ?., not a task kept in a local); Drop only
/// where <c>_</c> names nothing (a parameter or local called <c>_</c> would be assigned instead).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ResultCodeFix)), Shared]
public sealed class ResultCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.ResultDiscarded.Id);
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(ResultAnalyzer.ShapeKey, out var shape) || shape is null) return;
        d.Properties.TryGetValue(ResultAnalyzer.CallKey, out var call);
        call ??= "the call";
        var checkable = d.Properties.TryGetValue(ResultAnalyzer.CheckKey, out var check) && check == "yes";
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
        if (root is null || model is null || node is null) return;

        if (shape == "local")
        {
            if (!d.Properties.TryGetValue(ResultAnalyzer.LocalKey, out var local) || local is null) return;
            var decl = node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>();
            if (decl is null || decl.Declaration.Variables.Count != 1 || decl.Declaration.Variables[0] is not { Initializer.Value: { } init } v || v.Identifier.ValueText != local ||
                !decl.UsingKeyword.IsKind(SyntaxKind.None) || decl.IsConst)
                return;
            if (checkable && decl.Parent is BlockSyntax or SwitchSectionSyntax)
                context.RegisterCodeFix(CodeAction.Create("Check the result", ct =>
                {
                    var test = Check(v.Identifier.WithoutTrivia(), call, model, decl.SpanStart).WithTrailingTrivia(decl.GetTrailingTrivia());
                    return Task.FromResult(context.Document.WithSyntaxRoot(root.InsertNodesAfter(decl, new[] { test })));
                }, nameof(ResultCodeFix) + "Check"), d);
            // the declaration goes away with the drop: only when nothing else names the local (`r = ...` later, a lambda
            // or a local function that writes it), else the fix would leave a name without its declaration
            if (DiscardFree(model, decl.SpanStart) && !UsedElsewhere(v, model, context.CancellationToken))
                context.RegisterCodeFix(CodeAction.Create("Drop it on purpose (_ =)", ct =>
                {
                    var drop = ExpressionStatement(Discard(init)).WithTriviaFrom(decl).WithAdditionalAnnotations(Formatter.Annotation);
                    return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(decl, drop)));
                }, nameof(ResultCodeFix) + "Drop"), d);
            return;
        }

        // The value: the expression of a statement, or the body of an expression lambda / member whose delegate returns void.
        var stmt = node.FirstAncestorOrSelf<ExpressionStatementSyntax>();
        var fn = Syntax.EnclosingFunction(node);
        if (stmt is not null && Syntax.EnclosingFunction(stmt) == fn)
        {
            var expr = stmt.Expression;
            if (checkable && (shape != "task" || Syntax.IsAsync(fn)))
            {
                context.RegisterCodeFix(CodeAction.Create("Check the result", ct =>
                {
                    var name = Identifier(FreeName("result", fn, model, stmt.SpanStart));
                    var value = shape == "task" ? AwaitExpression(expr.WithoutTrivia()) : expr.WithoutTrivia();
                    var declare = LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"),
                            SingletonSeparatedList(VariableDeclarator(name).WithInitializer(EqualsValueClause(value)))))
                        .WithLeadingTrivia(stmt.GetLeadingTrivia()).WithAdditionalAnnotations(Formatter.Annotation);
                    var test = Check(name, call, model, stmt.SpanStart).WithTrailingTrivia(stmt.GetTrailingTrivia());
                    SyntaxNode newRoot = stmt.Parent is BlockSyntax or SwitchSectionSyntax
                        ? root.ReplaceNode(stmt, new SyntaxNode[] { declare, test })
                        : root.ReplaceNode(stmt, Block(declare, test).WithAdditionalAnnotations(Formatter.Annotation));
                    return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                }, nameof(ResultCodeFix) + "Check"), d);
            }
            if (DiscardFree(model, stmt.SpanStart))
                context.RegisterCodeFix(CodeAction.Create("Drop it on purpose (_ =)", ct =>
                {
                    var drop = stmt.WithExpression(Discard(expr.WithoutTrivia()).WithTriviaFrom(expr));
                    return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(stmt, drop)));
                }, nameof(ResultCodeFix) + "Drop"), d);
            return;
        }
        var body = fn switch
        {
            AnonymousFunctionExpressionSyntax { ExpressionBody: { } eb } => eb,
            _ => node.FirstAncestorOrSelf<ArrowExpressionClauseSyntax>()?.Expression,
        };
        if (body is null || !body.Span.Contains(node.Span) || !DiscardFree(model, body.SpanStart)) return;
        context.RegisterCodeFix(CodeAction.Create("Drop it on purpose (_ =)", ct =>
            Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(body, Discard(body.WithoutTrivia()).WithTriviaFrom(body)))),
            nameof(ResultCodeFix) + "Drop"), d);
    }

    private static AssignmentExpressionSyntax Discard(ExpressionSyntax value) =>
        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName("_"), value);

    // any name in the local's scope (its block, with lambdas and local functions in it) that binds to it
    private static bool UsedElsewhere(VariableDeclaratorSyntax v, SemanticModel model, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(v, ct) is not ILocalSymbol local) return true;
        var scope = v.FirstAncestorOrSelf<BlockSyntax>() ?? (SyntaxNode?)v.FirstAncestorOrSelf<SwitchSectionSyntax>() ?? v.SyntaxTree.GetRoot(ct);
        foreach (var id in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (id.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local))
                return true;
        return false;
    }

    // `_ = x` is a discard only when no parameter, local or member called _ is in reach (`_ => NoClip.Set(_, true)`)
    private static bool DiscardFree(SemanticModel model, int position) => model.LookupSymbols(position, name: "_").IsEmpty;

    /// <summary><c>if (!name.Ok) { Log.Warn("call refused: " + name.Reason); }</c> when <c>Log.Warn</c> binds here (a
    /// Mod's own Log, or a Logger member of the type), else the block holds a comment. Built as syntax: the call's name
    /// goes in as a string literal escaped by SyntaxFactory.</summary>
    private static StatementSyntax Check(SyntaxToken name, string call, SemanticModel model, int position)
    {
        var log = model.GetSpeculativeSymbolInfo(position, ParseExpression("Log.Warn(\"\")"), SpeculativeBindingOption.BindAsExpression).Symbol
            is IMethodSymbol;
        ExpressionSyntax Member(string member) => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName(name), IdentifierName(member));
        BlockSyntax inside;
        if (log)
        {
            var text = BinaryExpression(SyntaxKind.AddExpression,
                LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(call + " refused: ")), Member("Reason"));
            inside = Block(ExpressionStatement(InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("Log"), IdentifierName("Warn")),
                ArgumentList(SingletonSeparatedList(Argument(text))))));
        }
        else
        {
            var line = $"// {call} was refused: {name.ValueText}.Reason says why".Replace("\r", " ").Replace("\n", " ");
            inside = Block().WithCloseBraceToken(Token(TriviaList(Comment(line), ElasticCarriageReturnLineFeed), SyntaxKind.CloseBraceToken, TriviaList()));
        }
        return IfStatement(PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, Member("Ok")), inside)
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    /// <summary><paramref name="baseName"/>, or with a number, so that it names nothing in reach at
    /// <paramref name="position"/> and nothing the function declares.</summary>
    private static string FreeName(string baseName, SyntaxNode? fn, SemanticModel model, int position)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (Syntax.FunctionBody(fn) is { } body)
            foreach (var n in body.DescendantNodesAndSelf())
            {
                switch (n)
                {
                    case VariableDeclaratorSyntax v: taken.Add(v.Identifier.ValueText); break;
                    case SingleVariableDesignationSyntax s: taken.Add(s.Identifier.ValueText); break;
                    case ForEachStatementSyntax f: taken.Add(f.Identifier.ValueText); break;
                    case CatchDeclarationSyntax c: taken.Add(c.Identifier.ValueText); break;
                    case ParameterSyntax p: taken.Add(p.Identifier.ValueText); break;
                    case LocalFunctionStatementSyntax l: taken.Add(l.Identifier.ValueText); break;
                }
            }
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? baseName : baseName + i;
            if (!taken.Contains(name) && model.LookupSymbols(position, name: name).IsEmpty) return name;
        }
    }
}

/// <summary>KILN121: put the statement under <c>if (Capabilities.Is("row"))</c>.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CapabilityCodeFix)), Shared]
public sealed class CapabilityCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Descriptors.CapabilityUnchecked.Id);
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(CapabilityAnalyzer.FixKey, out var fix) || fix != "wrap" ||
            !d.Properties.TryGetValue(CapabilityAnalyzer.RowsKey, out var list) || string.IsNullOrEmpty(list))
            return;
        var rows = list!.Split(',');
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var stmt = root?.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<ExpressionStatementSyntax>();
        if (root is null || stmt is null) return;
        var title = string.Join(" && ", rows.Select(r => $"Capabilities.Is({SymbolDisplay.FormatLiteral(r, quote: true)})"));
        context.RegisterCodeFix(CodeAction.Create($"Ask {title} first", ct =>
        {
            // Kiln.Capabilities.Is("row") && ...: built as syntax, each row a literal escaped by SyntaxFactory
            ExpressionSyntax? condition = null;
            foreach (var row in rows)
            {
                var type = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("Kiln"), IdentifierName("Capabilities"))
                    .WithAdditionalAnnotations(Simplifier.Annotation);
                var ask = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, type, IdentifierName("Is")),
                    ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(row))))));
                condition = condition is null ? ask : BinaryExpression(SyntaxKind.LogicalAndExpression, condition, ask);
            }
            var guarded = IfStatement(condition!, Block(stmt.WithoutLeadingTrivia().WithoutTrailingTrivia()))
                .WithTriviaFrom(stmt).WithAdditionalAnnotations(Formatter.Annotation);
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(stmt, guarded)));
        }, nameof(CapabilityCodeFix)), d);
    }
}
