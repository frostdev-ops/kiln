// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN107 (advanced API without the opt-in) and KILN108 (a Kiln service the project does not declare). Both
/// read what Kiln.Sdk passes to the compiler: build_property.KilnAdvanced, build_property.KilnServicesList and the
/// project's own kiln.mod.json (an AdditionalFile). Projects without Kiln.Sdk provide neither: no diagnostics.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModProjectAnalyzer : DiagnosticAnalyzer
{
    private static readonly Regex AdvancedKey = new("\"advanced\"\\s*:\\s*(true|false)", RegexOptions.CultureInvariant);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.AdvancedApi, Descriptors.MissingService);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var global = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            var advanced = Advanced(start.Options, global);
            if (advanced == false)
            {
                var cache = new KilnApis.Cache();
                start.RegisterOperationAction(ctx =>
                {
                    var member = ctx.Operation is IMethodReferenceOperation mr ? mr.Method : TickThreadAnalyzer.Member(ctx.Operation);
                    if (member is null || !cache.RequiresAdvanced(member)) return;
                    ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.AdvancedApi, ctx.Operation.Syntax.GetLocation(), Syntax.Display(member)));
                }, OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.ObjectCreation, OperationKind.EventAssignment,
                    OperationKind.MethodReference);
            }
            // Kiln.Sdk passes KilnServicesList (comma-separated: .editorconfig cuts values at ';').
            if (global.TryGetValue("build_property.KilnServicesList", out var list) || global.TryGetValue("build_property.KilnServices", out list))
            {
                var declared = new HashSet<string>(
                    list.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                start.RegisterSemanticModelAction(ctx => AnalyzeServices(ctx, declared));
            }
        });
    }

    /// <summary>True/false from the project's kiln.mod.json ("advanced"; the file wins, as in the SDK's manifest task),
    /// else from KilnAdvanced; null when neither is known.</summary>
    private static bool? Advanced(AnalyzerOptions options, AnalyzerConfigOptions global)
    {
        foreach (var file in options.AdditionalFiles)
        {
            if (!string.Equals(Path.GetFileName(file.Path), "kiln.mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            var text = file.GetText()?.ToString();
            if (text is null) continue;
            var m = AdvancedKey.Match(text);
            return m.Success && m.Groups[1].Value == "true";
        }
        if (global.TryGetValue("build_property.KilnAdvanced", out var v))
            return string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return null;
    }

    private static void AnalyzeServices(SemanticModelAnalysisContext ctx, HashSet<string> declared)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = ctx.SemanticModel.SyntaxTree.GetRoot(ctx.CancellationToken);
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Parent is QualifiedNameSyntax q && q.Left == name) continue;  // the namespace part of Kiln.X.Y
            if (name.Ancestors().Any(a => a is UsingDirectiveSyntax)) continue;
            var sym = ctx.SemanticModel.GetSymbolInfo(name, ctx.CancellationToken).Symbol;
            if (sym is null || sym is INamespaceSymbol) continue;
            var service = KilnApis.ServiceOf(sym.ContainingAssembly);
            if (service is null || declared.Contains(service) || !reported.Add(service)) continue;
            var display = sym is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor ? ctor.ContainingType.Name : Syntax.Display(sym);
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.MissingService, name.GetLocation(), display, service));
        }
    }
}
