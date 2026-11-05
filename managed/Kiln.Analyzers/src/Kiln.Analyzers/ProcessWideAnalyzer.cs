// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN103 (process-wide events never unsubscribed) and KILN104 (mod objects put into process-wide state):
/// what keeps a mod's collectible load context alive after a hot reload.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProcessWideAnalyzer : DiagnosticAnalyzer
{
    /// <summary>KILN103 property: "unsubscribe" when the handler is a method group in a Mod subclass (code fix).</summary>
    public const string FixKey = "fix";

    private static readonly string[] CollectionAdds =
        { "Add", "TryAdd", "AddOrUpdate", "GetOrAdd", "Insert", "Push", "Enqueue", "Set", "TryUpdate", "AddRange", "AddFirst", "AddLast" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.ProcessWideEvent, Descriptors.ProcessWideCache);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Kiln.Mod") is null) return;
            start.RegisterOperationAction(AnalyzeEvent, OperationKind.EventAssignment);
            start.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment,
                OperationKind.CoalesceAssignment);
            start.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        });
    }

    // ---- KILN103 ----

    private static void AnalyzeEvent(OperationAnalysisContext ctx)
    {
        var op = (IEventAssignmentOperation)ctx.Operation;
        if (!op.Adds || op.EventReference is not IEventReferenceOperation er) return;
        var ev = er.Event.OriginalDefinition;
        var own = ctx.Compilation.Assembly;
        if (SymbolEqualityComparer.Default.Equals(ev.ContainingAssembly, own) || KilnApis.IsKilnAssembly(ev.ContainingAssembly)) return;
        if (!ev.IsStatic && !IsProcessWide(er.Instance, own)) return;
        var type = ctx.ContainingSymbol.ContainingType;
        if (type is null || IsRemovedInType(ev, type, ctx.Compilation, ctx.CancellationToken)) return;
        var props = ImmutableDictionary<string, string?>.Empty;
        if (Syntax.IsModType(type) && Unwrap(op.HandlerValue) is IDelegateCreationOperation { Target: IMethodReferenceOperation })
            props = props.Add(FixKey, "unsubscribe");
        var name = ev.IsStatic ? $"{ev.ContainingType.Name}.{ev.Name}" : $"{er.Instance?.Syntax}.{ev.Name}";
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessWideEvent, op.Syntax.GetLocation(), props, name));
    }

    private static bool IsRemovedInType(IEventSymbol ev, INamedTypeSymbol type, Compilation compilation, CancellationToken ct)
    {
        foreach (var (root, model) in Syntax.Declarations(type, compilation, ct))
            foreach (var asg in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (asg.IsKind(SyntaxKind.SubtractAssignmentExpression) &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(asg.Left, ct).Symbol?.OriginalDefinition, ev))
                    return true;
        return false;
    }

    /// <summary>An instance reached through a static member of another (non-Kiln) assembly: AppDomain.CurrentDomain,
    /// AssemblyLoadContext.Default, a library's singleton.</summary>
    private static bool IsProcessWide(IOperation? instance, IAssemblySymbol own)
    {
        var m = StaticMember(Unwrap(instance));
        return m is not null && !SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, own) && !KilnApis.IsKilnAssembly(m.ContainingAssembly);
    }

    private static ISymbol? StaticMember(IOperation? op) => op switch
    {
        IPropertyReferenceOperation { Property.IsStatic: true } p => p.Property,
        IFieldReferenceOperation { Field.IsStatic: true } f => f.Field,
        _ => null,
    };

    // ---- KILN104 ----

    private static void AnalyzeAssignment(OperationAnalysisContext ctx)
    {
        var a = (IAssignmentOperation)ctx.Operation;
        var own = ctx.Compilation.Assembly;
        ISymbol? holder;
        string what;
        switch (a.Target)
        {
            case IPropertyReferenceOperation { Property.IsIndexer: true } idx when IsExternalStatic(StaticMember(Unwrap(idx.Instance)), own):
                holder = StaticMember(Unwrap(idx.Instance));
                what = "an entry of";
                break;
            case IPropertyReferenceOperation { Property.IsStatic: true } p when IsExternalStatic(p.Property, own):
                holder = p.Property;
                what = "";
                break;
            case IFieldReferenceOperation { Field.IsStatic: true } f when IsExternalStatic(f.Field, own):
                holder = f.Field;
                what = "";
                break;
            default:
                return;
        }
        if (!IsModOwned(a.Value, own, out var value)) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessWideCache, a.Syntax.GetLocation(),
            $"{value} is stored in {(what.Length > 0 ? what + " " : "")}'{holder!.ContainingType.Name}.{holder.Name}', static state of " +
            $"{holder.ContainingAssembly.Name}: it keeps this mod's load context alive after unload; clear it in OnUnload or keep the state in the mod"));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext ctx)
    {
        var inv = (IInvocationOperation)ctx.Operation;
        var m = inv.TargetMethod;
        var own = ctx.Compilation.Assembly;
        var type = m.ContainingType?.OriginalDefinition;
        var ns = type?.ContainingNamespace?.ToDisplayString();

        // GCHandle.Alloc (strong / pinned) without a Free in the type.
        if (ns == "System.Runtime.InteropServices" && type!.Name == "GCHandle" && m.Name == "Alloc")
        {
            if (inv.Arguments.Length > 1 && inv.Arguments[1].Value.ConstantValue is { HasValue: true, Value: int kind } && kind is 0 or 1) return;  // Weak, WeakTrackResurrection
            if (TypeCalls(ctx, "Free")) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessWideCache, inv.Syntax.GetLocation(),
                "GCHandle.Alloc roots its target until Free: a handle that is never freed keeps this mod's load context alive after unload; Free it in OnUnload (or use GCHandleType.Weak)"));
            return;
        }
        // TypeDescriptor.AddAttributes/AddProvider: a process-wide registry, never cleared on unload.
        if (ns == "System.ComponentModel" && type!.Name == "TypeDescriptor" && m.Name is "AddAttributes" or "AddProvider" or "AddProviderTransparent")
        {
            if (TypeCalls(ctx, "RemoveProvider") || TypeCalls(ctx, "RemoveProviderTransparent")) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessWideCache, inv.Syntax.GetLocation(),
                $"TypeDescriptor.{m.Name} registers process-wide and is not undone when the mod unloads: it keeps the mod's types alive; remove the provider in OnUnload"));
            return;
        }
        // AppContext.SetData / AppDomain.SetData with a mod object.
        if ((ns == "System" && type!.Name is "AppContext" or "AppDomain") && m.Name == "SetData" && inv.Arguments.Length >= 2)
        {
            if (!IsModOwned(inv.Arguments[1].Value, own, out var v)) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessWideCache, inv.Syntax.GetLocation(),
                $"{v} is stored with {type.Name}.SetData, process-wide: it keeps this mod's load context alive after unload; set it back to null in OnUnload"));
            return;
        }
        // ExternalType.StaticCollection.Add(modObject)
        if (CollectionAdds.Contains(m.Name) && StaticMember(Unwrap(inv.Instance)) is { } holder && IsExternalStatic(holder, own))
        {
            foreach (var arg in inv.Arguments)
            {
                if (!IsModOwned(arg.Value, own, out var v)) continue;
                ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.ProcessWideCache, inv.Syntax.GetLocation(),
                    $"{v} is added to '{holder.ContainingType.Name}.{holder.Name}', static state of {holder.ContainingAssembly.Name}: " +
                    "it keeps this mod's load context alive after unload; remove it in OnUnload"));
                return;
            }
        }
    }

    private static bool TypeCalls(OperationAnalysisContext ctx, string method)
    {
        var type = ctx.ContainingSymbol.ContainingType;
        if (type is null) return false;
        foreach (var (root, _) in Syntax.Declarations(type, ctx.Compilation, ctx.CancellationToken))
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.ValueText == method ||
                    inv.Expression is MemberBindingExpressionSyntax mb && mb.Name.Identifier.ValueText == method)
                    return true;
        return false;
    }

    private static bool IsExternalStatic(ISymbol? m, IAssemblySymbol own) =>
        m is not null && !SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, own) && !KilnApis.IsKilnAssembly(m.ContainingAssembly);

    internal static IOperation? Unwrap(IOperation? op)
    {
        while (op is IConversionOperation c) op = c.Operand;
        return op;
    }

    /// <summary>Whether a value holds the mod's own code or types: a lambda or method of the mod, an instance of a mod
    /// type, typeof(a mod type), or a generic/array over one.</summary>
    internal static bool IsModOwned(IOperation? value, IAssemblySymbol own, out string what)
    {
        what = "";
        var v = Unwrap(value);
        switch (v)
        {
            case null:
                return false;
            case IDelegateCreationOperation dc:
                if (dc.Target is IAnonymousFunctionOperation) { what = "a lambda of this mod"; return true; }
                if (dc.Target is IMethodReferenceOperation mr && SymbolEqualityComparer.Default.Equals(mr.Method.ContainingAssembly, own))
                {
                    what = $"a delegate to {mr.Method.Name}";
                    return true;
                }
                return false;
            case IAnonymousFunctionOperation:
                what = "a lambda of this mod";
                return true;
            case ITypeOfOperation t when IsModType(t.TypeOperand, own):
                what = $"typeof({t.TypeOperand.Name})";
                return true;
            case IInstanceReferenceOperation:
                what = "this mod object";
                return v.Type is not null && IsModType(v.Type, own);
        }
        if (v.Type is { } type && IsModType(type, own))
        {
            what = $"a {type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}";
            return true;
        }
        return false;
    }

    internal static bool IsModType(ITypeSymbol t, IAssemblySymbol own)
    {
        switch (t)
        {
            case IArrayTypeSymbol a:
                return IsModType(a.ElementType, own);
            case INamedTypeSymbol n:
                if (SymbolEqualityComparer.Default.Equals(n.ContainingAssembly, own)) return true;
                return n.TypeArguments.Any(x => IsModType(x, own));
            default:
                return false;
        }
    }
}
