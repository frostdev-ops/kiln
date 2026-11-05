// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN101: a tick-thread API (KilnApis.TickThread, generated from the services' RequireTickThread checks)
/// called from code that runs on another thread.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TickThreadAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property: the expression that reaches a Scheduler from the code's type ("Context.Scheduler").</summary>
    public const string SchedulerKey = "scheduler";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.TickThreadOffThread);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Kiln.Scheduler") is null) return;  // not a Kiln mod
            var cache = new KilnApis.Cache();
            var classifier = new ThreadClassifier(start.Compilation);
            start.RegisterOperationAction(ctx =>
            {
                var member = Member(ctx.Operation);
                if (member is null || !cache.RequiresTickThread(member)) return;
                var where = classifier.Classify(ctx.Operation.Syntax, ctx.Operation.SemanticModel!, ctx.CancellationToken);
                if (where.Kind != ThreadKind.Off) return;
                var props = ImmutableDictionary<string, string?>.Empty;
                if (SchedulerExpression(ctx.ContainingSymbol) is { } s) props = props.Add(SchedulerKey, s);
                ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.TickThreadOffThread, ReportAt(ctx.Operation.Syntax), props,
                    Syntax.Display(member), where.Where));
            }, OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.EventAssignment, OperationKind.ObjectCreation);
        });
    }

    /// <summary>The member an operation uses: the method, the accessor that runs, the event accessor, the ctor.</summary>
    internal static ISymbol? Member(IOperation op) => op switch
    {
        IInvocationOperation i => i.TargetMethod,
        IObjectCreationOperation o => o.Constructor,
        IEventAssignmentOperation { EventReference: IEventReferenceOperation er } ea =>
            (ISymbol?)(ea.Adds ? er.Event.AddMethod : er.Event.RemoveMethod) ?? er.Event,
        IPropertyReferenceOperation p => IsWritten(p) ? (ISymbol?)p.Property.SetMethod ?? p.Property : (ISymbol?)p.Property.GetMethod ?? p.Property,
        _ => null,
    };

    private static bool IsWritten(IPropertyReferenceOperation p) =>
        p.Parent is ISimpleAssignmentOperation a && a.Target == p;

    private static Location ReportAt(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } => ma.Name.GetLocation(),
        MemberAccessExpressionSyntax ma => ma.Name.GetLocation(),
        _ => node.GetLocation(),
    };

    /// <summary>"Context.Scheduler" in a Mod subclass; "&lt;member&gt;.Scheduler" / "&lt;member&gt;" when the type holds a
    /// ModContext / Scheduler; null otherwise (no fix).</summary>
    internal static string? SchedulerExpression(ISymbol? containing)
    {
        var type = containing as INamedTypeSymbol ?? containing?.ContainingType;
        if (type is null) return null;
        var inStatic = containing?.IsStatic ?? false;
        if (Syntax.IsModType(type) && !inStatic) return "Context.Scheduler";
        foreach (var m in type.GetMembers())
        {
            if (inStatic && !m.IsStatic) continue;
            var t = m switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => null };
            if (t?.ContainingNamespace?.ToDisplayString() != "Kiln" || m.IsImplicitlyDeclared) continue;
            if (t.Name == "ModContext") return $"{m.Name}.Scheduler";
            if (t.Name == "Scheduler") return m.Name;
        }
        return null;
    }
}
