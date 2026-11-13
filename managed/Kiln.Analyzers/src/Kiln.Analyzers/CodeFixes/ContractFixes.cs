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
/// reach, else an empty block with a comment) and "Drop it on purpose" (<c>_ = ...</c>).</summary>
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
            if (decl.Parent is BlockSyntax or SwitchSectionSyntax)
                context.RegisterCodeFix(CodeAction.Create("Check the result", ct =>
                {
                    var check = Check(local, call, model, decl.SpanStart);
                    return Task.FromResult(context.Document.WithSyntaxRoot(root.InsertNodesAfter(decl, new[] { check })));
                }, nameof(ResultCodeFix) + "Check"), d);
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
            var async = Syntax.IsAsync(fn);
            if (shape != "task" || async)
            {
                context.RegisterCodeFix(CodeAction.Create("Check the result", ct =>
                {
                    var name = FreeName("result", fn, model, stmt.SpanStart);
                    var value = shape == "task" ? AwaitExpression(expr.WithoutTrivia()) : expr.WithoutTrivia();
                    var declare = LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"),
                            SingletonSeparatedList(VariableDeclarator(Identifier(name)).WithInitializer(EqualsValueClause(value)))))
                        .WithLeadingTrivia(stmt.GetLeadingTrivia()).WithAdditionalAnnotations(Formatter.Annotation);
                    var check = Check(name, call, model, stmt.SpanStart).WithTrailingTrivia(stmt.GetTrailingTrivia());
                    SyntaxNode newRoot = stmt.Parent is BlockSyntax or SwitchSectionSyntax
                        ? root.ReplaceNode(stmt, new SyntaxNode[] { declare, check })
                        : root.ReplaceNode(stmt, Block(declare, check).WithAdditionalAnnotations(Formatter.Annotation));
                    return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));
                }, nameof(ResultCodeFix) + "Check"), d);
            }
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
        if (body is null || !body.Span.Contains(node.Span)) return;
        context.RegisterCodeFix(CodeAction.Create("Drop it on purpose (_ =)", ct =>
            Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(body, Discard(body.WithoutTrivia()).WithTriviaFrom(body)))),
            nameof(ResultCodeFix) + "Drop"), d);
    }

    private static AssignmentExpressionSyntax Discard(ExpressionSyntax value) =>
        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName("_"), value);

    /// <summary><c>if (!name.Ok) { Log.Warn($"call refused: {name.Reason}"); }</c> when <c>Log.Warn</c> binds here (a
    /// Mod's own Log, or a Logger member of the type), else the block holds a comment.</summary>
    private static StatementSyntax Check(string name, string call, SemanticModel model, int position)
    {
        var log = model.GetSpeculativeSymbolInfo(position, ParseExpression("Log.Warn(\"\")"), SpeculativeBindingOption.BindAsExpression).Symbol
            is IMethodSymbol;
        var inside = log
            ? $"    Log.Warn($\"{call} refused: {{{name}.Reason}}\");\n"
            : $"    // {call} was refused: {name}.Reason says why\n";
        return ParseStatement($"if (!{name}.Ok)\n{{\n{inside}}}\n").WithAdditionalAnnotations(Formatter.Annotation);
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
        var test = string.Join(" && ", rows.Select(r => $"Capabilities.Is({SymbolDisplay.FormatLiteral(r, quote: true)})"));
        context.RegisterCodeFix(CodeAction.Create($"Ask {test} first", ct =>
        {
            var condition = ParseExpression(string.Join(" && ", rows.Select(r => $"Kiln.Capabilities.Is({SymbolDisplay.FormatLiteral(r, quote: true)})")));
            condition = condition.ReplaceNodes(condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                    .Where(m => m.Expression is IdentifierNameSyntax { Identifier.ValueText: "Kiln" }),
                (_, m) => m.WithAdditionalAnnotations(Simplifier.Annotation));
            var guarded = IfStatement(condition, Block(stmt.WithoutLeadingTrivia().WithoutTrailingTrivia()))
                .WithTriviaFrom(stmt).WithAdditionalAnnotations(Formatter.Annotation);
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(stmt, guarded)));
        }, nameof(CapabilityCodeFix)), d);
    }
}
