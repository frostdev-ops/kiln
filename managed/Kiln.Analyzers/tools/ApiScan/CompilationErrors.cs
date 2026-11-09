// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace Kiln.Analyzers.Tools;

internal static class ApiScanCompilationErrors
{
    internal static IReadOnlyList<Diagnostic> Read(Compilation compilation) => compilation.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error && !GeneratedRegexImplementation(compilation, d)).ToArray();

    private static bool GeneratedRegexImplementation(Compilation compilation, Diagnostic diagnostic)
    {
        // ApiScan does not run the regex generator. Exempt only its annotated methods, not every missing partial body.
        if (diagnostic.Id != "CS8795" || diagnostic.Location.SourceTree is not { } tree) return false;
        var method = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method == null || compilation.GetSemanticModel(tree).GetDeclaredSymbol(method) is not IMethodSymbol symbol) return false;
        if (!symbol.IsStatic || symbol.Arity != 0 || symbol.Parameters.Length != 0 ||
            !SymbolEqualityComparer.Default.Equals(symbol.ReturnType, compilation.GetTypeByMetadataName("System.Text.RegularExpressions.Regex"))) return false;
        var attribute = compilation.GetTypeByMetadataName("System.Text.RegularExpressions.GeneratedRegexAttribute");
        var args = symbol.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute))?.ConstructorArguments;
        if (args is not { Length: >= 1 and <= 3 } values || values[0].Value is not string pattern) return false;
        // Only the pattern/options/timeout constructors used by these projects are supported. Unknown forms fail closed.
        if (values.Length >= 2 && values[1].Value is not int || values.Length == 3 && values[2].Value is not int) return false;
        var options = values.Length >= 2 ? (RegexOptions)(int)values[1].Value! : RegexOptions.None;
        var timeout = values.Length == 3 ? (int)values[2].Value! : -1;
        try
        {
            _ = new Regex(pattern, options, timeout == -1 ? Regex.InfiniteMatchTimeout : TimeSpan.FromMilliseconds(timeout));
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
