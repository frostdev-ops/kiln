// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kiln.Analyzers;

/// <summary>KILN194, KILN195 (reported by Kiln.GameIds' path analyzer): replace a path string with the one it probably meant.
/// A constant declared elsewhere gets no fix: the message says what to write there.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(FieldPathCodeFix)), Shared]
public sealed class FieldPathCodeFix : CodeFixProvider
{
    private const string SuggestionKey = "suggestion";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("KILN194", "KILN195");
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(SuggestionKey, out var path) || path is null) return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true) is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)) return;
        context.RegisterCodeFix(CodeAction.Create($"Change to \"{path}\"", _ =>
        {
            var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(path)).WithTriviaFrom(literal);
            return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(literal, replacement)));
        }, nameof(FieldPathCodeFix)), d);
    }
}
