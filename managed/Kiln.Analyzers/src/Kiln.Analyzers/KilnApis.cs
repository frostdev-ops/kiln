// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Kiln.Analyzers;

/// <summary>Lookups over the generated tables (KilnApis.g.cs) and facts about Kiln's assemblies.</summary>
internal static partial class KilnApis
{
    // In a nested class: static initializers of the two partial files run in file order, so sets built here from
    // the generated arrays would see them still null.
    private static class Sets
    {
        public static readonly HashSet<string> Tick = new(TickThread, StringComparer.Ordinal);
        public static readonly HashSet<string> Advanced = new(KilnApis.Advanced, StringComparer.Ordinal);
        public static readonly HashSet<string> Services = new(KilnApis.Services, StringComparer.OrdinalIgnoreCase);
        public static readonly HashSet<string> Completed = new(CompletedTasks, StringComparer.Ordinal);
        public static readonly Dictionary<string, string[]> Guards = ParseGuards();

        private static Dictionary<string, string[]> ParseGuards()
        {
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var entry in CapabilityGuards)
            {
                var eq = entry.IndexOf('=');
                if (eq > 0) d[entry.Substring(0, eq)] = entry.Substring(eq + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
            return d;
        }
    }

    /// <summary>The capability rows a Kiln property answers like Capabilities.Is does (NoClip.Available:
    /// rules.noclip), from the generated CapabilityGuards table; empty for any other symbol.</summary>
    internal static IReadOnlyList<string> GuardRows(ISymbol symbol)
    {
        if (symbol is not IPropertySymbol { IsStatic: true } p || p.ContainingAssembly?.Name.StartsWith("Kiln", StringComparison.Ordinal) != true)
            return Array.Empty<string>();
        var id = p.OriginalDefinition.GetDocumentationCommentId();
        return id is not null && Sets.Guards.TryGetValue(id, out var rows) ? rows : Array.Empty<string>();
    }

    /// <summary>Attribute names honoured in addition to the tables (a future Kiln.Api may mark members itself).</summary>
    internal const string TickThreadOnlyAttribute = "TickThreadOnlyAttribute";
    internal const string AdvancedApiAttribute = "AdvancedApiAttribute";

    /// <summary>Kiln's own assemblies: they release a mod's registrations when it unloads.</summary>
    internal static bool IsKilnAssembly(IAssemblySymbol? a)
    {
        if (a is null) return false;
        var n = a.Name;
        if (n is "Kiln.Api" or "Kiln.Testing" or "Kiln.Game" or "Kiln.Host") return true;
        return n.StartsWith("Kiln.", StringComparison.Ordinal) && Sets.Services.Contains(n.Substring(5));
    }

    /// <summary>The service name of a Kiln service assembly ("Players" for Kiln.Players; "Testing" for Kiln.Testing).</summary>
    internal static string? ServiceOf(IAssemblySymbol? a)
    {
        if (a is null) return null;
        if (a.Name == "Kiln.Testing") return "Testing";
        if (!a.Name.StartsWith("Kiln.", StringComparison.Ordinal)) return null;
        var s = a.Name.Substring(5);
        return Sets.Services.Contains(s) ? s : null;
    }

    internal static bool HasAttribute(ISymbol s, string name)
    {
        foreach (var a in s.GetAttributes())
            if (a.AttributeClass?.Name == name && a.AttributeClass.ContainingNamespace?.ToDisplayString() is "Kiln" or "Kiln.Api")
                return true;
        return false;
    }

    /// <summary>Per-compilation cache of "does this member require the tick thread / the advanced tier".</summary>
    internal sealed class Cache
    {
        private readonly ConcurrentDictionary<ISymbol, bool> _tick = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ISymbol, bool> _advanced = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ISymbol, bool> _completed = new(SymbolEqualityComparer.Default);

        public bool RequiresTickThread(ISymbol member) =>
            _tick.GetOrAdd(member, m => Lookup(m, Sets.Tick, TickThreadOnlyAttribute));

        public bool RequiresAdvanced(ISymbol member) =>
            _advanced.GetOrAdd(member, m => Lookup(m, Sets.Advanced, AdvancedApiAttribute));

        /// <summary>A Kiln API whose returned task is always complete (Task.FromResult): its Result never blocks.</summary>
        public bool ReturnsCompletedTask(ISymbol member) =>
            _completed.GetOrAdd(member, m => Lookup(m, Sets.Completed, "\0"));

        private static bool Lookup(ISymbol m, HashSet<string> set, string attribute)
        {
            if (m is IMethodSymbol { ReducedFrom: { } reduced }) m = reduced;
            m = m.OriginalDefinition;
            var asm = m.ContainingAssembly;
            if (asm is null || !asm.Name.StartsWith("Kiln", StringComparison.Ordinal)) return false;
            if (HasAttribute(m, attribute) || (m.ContainingType is { } t && HasAttribute(t, attribute))) return true;
            if (m is IMethodSymbol { AssociatedSymbol: { } assoc } && HasAttribute(assoc, attribute)) return true;  // on the property/event
            var id = m.GetDocumentationCommentId();
            return id is not null && set.Contains(id);
        }
    }
}
