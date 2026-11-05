// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kiln.GameIds;

/// <summary>What a path check found.</summary>
internal enum PathVerdict
{
    /// <summary>The path is there, or it goes where the catalog cannot see (below a list of variants).</summary>
    Ok,
    /// <summary>The game has no such field.</summary>
    Unknown,
    /// <summary>A config name no program of the game declares. A mod's own program can add names, so this may be right.</summary>
    UnknownConfig,
}

internal readonly struct PathResult
{
    public PathResult(PathVerdict verdict, string? what = null, string? suggestion = null, FieldRow? row = null)
    {
        Verdict = verdict;
        What = what;
        Suggestion = suggestion;
        Row = row;
    }

    public PathVerdict Verdict { get; }

    /// <summary>Why, in words.</summary>
    public string? What { get; }

    /// <summary>The whole path as it probably should be.</summary>
    public string? Suggestion { get; }

    /// <summary>The field, when the path names one exactly.</summary>
    public FieldRow? Row { get; }
}

/// <summary>Checks string paths against the catalog's field lists, the way kiln.gamedata resolves them
/// (native/services/content/gamedata_walk.cpp): dotted names, exact then ignoring case; <c>[i]</c>, <c>[*]</c>, <c>[Type#k]</c> and
/// <c>[guid]</c> on lists; <c>Component#k</c> first on templates; below a config list a config by name, <c>0x</c> id or bracket.</summary>
internal sealed class FieldIndex
{
    private readonly Dictionary<string, FieldNode> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _configNames = new(StringComparer.OrdinalIgnoreCase);

    public FieldIndex(FieldCatalog cat)
    {
        foreach (var kv in cat.Fields)
        {
            _roots[kv.Key] = FieldNode.Build(kv.Value);
            _paths[kv.Key] = kv.Value.Select(r => r.Path).ToList();
        }
        foreach (var c in cat.Configs)
            foreach (var n in c.Names)
                _configNames.Add(n);
    }

    /// <summary>The catalog lists this section's paths (arsenals and drops have none).</summary>
    public bool HasSection(string section) => _roots.ContainsKey(section);

    public PathResult Check(string section, string path)
    {
        if (!_roots.TryGetValue(section, out var root)) return new PathResult(PathVerdict.Ok);
        var tokens = FieldPathToken.TryParse(path);
        if (tokens is null) return new PathResult(PathVerdict.Unknown, $"'{path}' has an unclosed '['");
        if (tokens.Count == 0) return new PathResult(PathVerdict.Ok);   // the whole entry
        var node = root;
        var done = "";
        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (node.Row?.EffectiveKind == "configs" && (tok.IsIndex || node.Child(tok.Text) is null))
                return ConfigName(section, tok, done, tokens, i);
            if (tok.IsIndex)
            {
                if (node.Element is { } el)
                {
                    if (int.TryParse(tok.Text, out var index) && node.FixedCount > 0 && index >= node.FixedCount)
                        return new PathResult(PathVerdict.Unknown, $"{done} has {node.FixedCount} entries", Join(tokens, i, done + "[" + (node.FixedCount - 1) + "]"));
                    node = el;
                    done += "[" + tok.Text + "]";
                    continue;
                }
                return node.Row is { } r && !IsPlain(r)
                    ? new PathResult(PathVerdict.Ok)
                    : new PathResult(PathVerdict.Unknown, $"{(done.Length == 0 ? "the entry" : done)} is not a list");
            }
            var name = tok.Text;
            var nth = "";
            if (i == 0 && section == "templates" && name.IndexOf('#') is var hash && hash > 0 && int.TryParse(name.Substring(hash + 1), out _))
            {
                nth = name.Substring(hash);
                name = name.Substring(0, hash);
            }
            if (node.Child(name) is { } child)
            {
                node = child;
                done += (done.Length == 0 ? "" : ".") + tok.Text;
                continue;
            }
            if (node.IsLeaf && node.Row is not null)
                return !IsPlain(node.Row)
                    ? new PathResult(PathVerdict.Ok)
                    : new PathResult(PathVerdict.Unknown, $"{done} is a {node.Row.Type}: it has no field '{tok.Text}'");
            if (node.Element is not null && node.Row is not null && node.Children.Count == 0)
                return new PathResult(PathVerdict.Unknown, $"{done} is a list: it needs an index before '{tok.Text}'", Join(tokens, i - 1, done + "[0]"));
            return Missing(section, tokens, i, done, node, name, nth);
        }
        return new PathResult(PathVerdict.Ok, row: node.Row);
    }

    /// <summary>A plain scalar or an enum: nothing lies below it. Other leaves are wrappers, references or variants, where the
    /// walk at run time can still go on.</summary>
    private static bool IsPlain(FieldRow r) => FieldCatalog.ScalarOfType(r.Type) is not null || r.EnumNames is not null;

    private PathResult ConfigName(string section, FieldPathToken tok, string done, List<FieldPathToken> tokens, int at)
    {
        // a bracket holds a name with dots: a mod's own program. 0x<id> is any id.
        if (tok.IsIndex || tok.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || _configNames.Count == 0 || _configNames.Contains(tok.Text))
            return new PathResult(PathVerdict.Ok);
        var best = FieldSuggest.Best(tok.Text, _configNames);
        return new PathResult(PathVerdict.UnknownConfig, $"no Impact program of this game names a config '{tok.Text}'",
            best is null ? null : Join(tokens, at, done + "." + best));
    }

    private PathResult Missing(string section, List<FieldPathToken> tokens, int at, string done, FieldNode node, string name, string nth)
    {
        var where = done.Length == 0
            ? section == "templates" ? "no template has a component or field" : $"{section} have no field"
            : $"{done} has no field";
        string? suggestion = null;
        if (FieldSuggest.Best(name, node.Children.Select(c => c.Name)) is { } sibling)
            suggestion = Join(tokens, at, (done.Length == 0 ? "" : done + ".") + sibling + nth);
        // the right field elsewhere: a missing or misspelled segment
        var whole = Normal(string.Concat(tokens.Select(t => t.IsIndex ? "[*]" : "." + t.Text)).TrimStart('.'));
        suggestion ??= FieldSuggest.Best(whole, _paths[section].Select(Normal));
        if (suggestion is null)
        {
            var tail = "." + name;
            var ends = _paths[section].Where(p => p.EndsWith(tail, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (ends.Count == 1) suggestion = ends[0];
        }
        return new PathResult(PathVerdict.Unknown, $"{where} '{tokens[at].Text}'", suggestion);
    }

    // perkIds[2] and perkIds[*] are one thing to a reader
    private static string Normal(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[*]");

    /// <summary>A path with the segments up to <paramref name="at"/> replaced by <paramref name="head"/>.</summary>
    private static string Join(List<FieldPathToken> tokens, int at, string head)
    {
        var s = head;
        for (var i = at + 1; i < tokens.Count; i++)
            s += tokens[i].IsIndex ? "[" + tokens[i].Text + "]" : "." + tokens[i].Text;
        return s;
    }
}

/// <summary>"Did you mean": the closest candidate within a distance that grows with the word, or nothing when none is close or
/// two are equally close.</summary>
internal static class FieldSuggest
{
    public static string? Best(string word, IEnumerable<string> candidates)
    {
        if (word.Length == 0) return null;
        var limit = Math.Max(1, Math.Min(4, word.Length / 3));
        string? best = null;
        var bestD = int.MaxValue;
        var tie = false;
        foreach (var c in candidates)
        {
            if (Math.Abs(c.Length - word.Length) > limit || c == word) continue;
            var d = Distance(word, c);
            if (d > limit) continue;
            if (d < bestD)
            {
                best = c;
                bestD = d;
                tie = false;
            }
            else if (d == bestD && !string.Equals(best, c, StringComparison.Ordinal)) tie = true;
        }
        return tie ? null : best;
    }

    /// <summary>Optimal string alignment distance, ignoring case.</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2]) && char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
