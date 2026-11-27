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
using Microsoft.CodeAnalysis.Text;

namespace Kiln.Analyzers;

/// <summary>KILN124 where the registry field holds the same value one to one: moves a data pack's value
/// (<c>"items": {"Wood": {"maxStackSize": 500}}</c>) into the pack's "registry" section as one alter edit of the Kiln field
/// (<c>"registry": {"alter": [{"registry": "items", "select": {"id": "Wood"}, "field": "maxStack", "op": 500}]}</c>,
/// schema/registry-section.schema.json), the entry still named by its key (a registry selector reads an old debug name as
/// <c>game:&lt;name in lower case&gt;</c>, D3). Only a plain value or <c>{"set": v}</c> of an entry named by its key: a relative
/// op of an old section works on the game's value, the registry's on the value below it, and an entry with conditions is not
/// one entry, so those stay as they are. The registry edit is a layer above every old pack (D1).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RegistryHintFix), DocumentKinds = new[] { nameof(TextDocumentKind.AdditionalDocument) }), Shared]
public sealed class RegistryHintFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("KILN124");
    public override FixAllProvider? GetFixAllProvider() => null;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var d = context.Diagnostics[0];
        if (!d.Properties.TryGetValue(RegistryHintAnalyzer.FixableKey, out var ok) || ok != "true" ||
            !d.Properties.TryGetValue(RegistryHintAnalyzer.FieldKey, out var field) || field is null ||
            !d.Properties.TryGetValue(RegistryHintAnalyzer.SectionKey, out var section) || section is null ||
            !d.Properties.TryGetValue(RegistryHintAnalyzer.SelectorKey, out var selector) || selector is null)
            return Task.CompletedTask;
        var document = context.TextDocument;
        context.RegisterCodeFix(CodeAction.Create($"Move to the registry field {field}",
            ct => Move(document, d.Location.SourceSpan, field, section, selector, ct), nameof(RegistryHintFix)), d);
        return Task.CompletedTask;
    }

    private static async Task<Solution> Move(TextDocument document, TextSpan key, string field, string section, string selector, CancellationToken ct)
    {
        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        var moved = MoveText(text.ToString(), key.Start, field, section, selector);
        return moved is null ? document.Project.Solution : document.Project.Solution.WithAdditionalDocumentText(document.Id, SourceText.From(moved, text.Encoding));
    }

    /// <summary>The file with the member whose key starts at <paramref name="keyStart"/> moved into the registry section; null
    /// when the file no longer reads that way, the entry has conditions, or the registry section already edits that field of that
    /// entry (or does not read as a registry section: it is left alone).</summary>
    internal static string? MoveText(string s, int keyStart, string field, string section, string selector)
    {
        var doc = JsonSpans.Parse(s);
        var sec = doc?.Find(section)?.Value;
        var entry = sec?.Find(selector)?.Value;
        var member = entry?.Members.FirstOrDefault(m => m.KeyStart == keyStart);
        if (doc is null || entry is null || member is null || !RegistryHintAnalyzer.OneEntry(selector)) return null;
        if (entry.Members.Any(m => m.Key.StartsWith("@", StringComparison.Ordinal))) return null;  // conditions: not one entry
        var value = member.Value;
        if (value.Type == JsonSpans.Kind.Object)
        {
            if (value.Members.Count != 1 || value.Members[0].Key != "set") return null;
            value = value.Members[0].Value;  // {"set": v}
        }
        if (value.Type is not (JsonSpans.Kind.Number or JsonSpans.Kind.Literal) || value.Text == "null") return null;
        var valueText = s.Substring(value.Start, value.End - value.Start);
        var dot = field.IndexOf('.');
        if (dot <= 0) return null;
        var registry = field.Substring(0, dot);
        var kiln = field.Substring(dot + 1);
        var changes = new List<(int Start, int End, string Text)>();
        // out of the old section: the member and the comma that separates it from a neighbour
        int i = entry.Members.IndexOf(member);
        if (entry.Members.Count == 1) changes.Add((member.Start, member.Value.End, ""));
        else if (i + 1 < entry.Members.Count) changes.Add((member.Start, entry.Members[i + 1].Start, ""));
        else changes.Add((entry.Members[i - 1].Value.End, member.Value.End, ""));
        // into the registry section's alter list, as far as it already goes
        string Q(string x) => "\"" + x.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var edit = $"{{{Q("registry")}: {Q(registry)}, {Q("select")}: {{{Q("id")}: {Q(selector)}}}, {Q("field")}: {Q(kiln)}, {Q("op")}: {valueText}}}";
        var reg = doc.Find("registry")?.Value;
        if (reg is null) changes.Add(Insert(doc, $"{Q("registry")}: {{{Q("alter")}: [{edit}]}}"));
        else if (reg.Type != JsonSpans.Kind.Object || reg.Members.Any(m => m.Key is not ("add" or "alter" or "final"))) return null;
        else if (reg.Find("alter")?.Value is not { } alter) changes.Add(Insert(reg, $"{Q("alter")}: [{edit}]"));
        else if (alter.Type != JsonSpans.Kind.Array || alter.Items.Any(e => Edits(e, registry, selector, kiln))) return null;  // the registry already sets it: leave both
        else changes.Add(alter.Items.Count == 0 ? (alter.End - 1, alter.End - 1, edit) : (alter.Items[alter.Items.Count - 1].End, alter.Items[alter.Items.Count - 1].End, ", " + edit));
        var sb = new System.Text.StringBuilder(s);
        foreach (var c in changes.OrderByDescending(c => c.Start)) sb.Remove(c.Start, c.End - c.Start).Insert(c.Start, c.Text);
        return sb.ToString();
    }

    // an alter edit of this field of this entry (its select.id read as the registry reads it: game:<lower> without a namespace)
    private static bool Edits(JsonSpans e, string registry, string selector, string kiln)
    {
        if (e.Type != JsonSpans.Kind.Object || e.Find("registry")?.Value.Text != registry) return false;
        var id = e.Find("select")?.Value.Find("id")?.Value.Text;
        static string Norm(string x) => (x.IndexOf(':') < 0 && x != "*" ? "game:" + x : x).ToLowerInvariant();
        if (id is null || Norm(id) != Norm(selector)) return false;
        var f = e.Find("field")?.Value.Text;
        return f == kiln || (e.Find("fields")?.Value is { Type: JsonSpans.Kind.Object } fs && fs.Find(kiln) is not null);
    }

    // before an object's closing brace, after a comma when it has members
    private static (int, int, string) Insert(JsonSpans obj, string member)
    {
        int at = obj.End - 1;
        return obj.Members.Count == 0 ? (at, at, member) : (obj.Members[obj.Members.Count - 1].Value.End, obj.Members[obj.Members.Count - 1].Value.End, ", " + member);
    }
}
