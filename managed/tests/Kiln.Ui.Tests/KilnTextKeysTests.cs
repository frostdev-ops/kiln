using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.KilnTextTests;

// The keys of Kiln's own texts against the code (docs/kiln/translating-game.md "For Kiln's developers"):
//  - every literal key of a KilnText.T / KilnText.Plural call in Kiln's sources is in runtime/locales/en.json (a Plural
//    key as <key>.one and <key>.other), and every "native." key kiln-ui.dll's C++ names;
//  - the placeholders a call fills are exactly the ones its English text has ({0}.. for positional values, {name} for named
//    ones; Plural fills {count});
//  - every key of en.json is used somewhere (a literal in the sources, or under a prefix a call builds keys from);
//  - en.json has no key twice; a translation's keys are English's and its placeholders English's.
public sealed partial class KilnTextKeysTests
{
    private static readonly string Root = RepoRoot();

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        var d = Path.GetDirectoryName(source);
        while (d is not null && !File.Exists(Path.Combine(d, "VERSION"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("no repository above " + source);
    }

    private static readonly string[] ManagedDirs = ["managed/services", "managed/Kiln.Testing", "managed/Kiln.Api"];

    private static IEnumerable<string> Sources() =>
        ManagedDirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> NativeSources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "native", "services", "ui"))
            .Where(f => f.EndsWith(".cpp", StringComparison.Ordinal) || f.EndsWith(".h", StringComparison.Ordinal) || f.EndsWith(".inl", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static Dictionary<string, string> Table(string file)
    {
        var dupes = new List<string>();
        var t = Flatten(File.ReadAllBytes(file), dupes);
        Assert.True(dupes.Count == 0, $"{Path.GetFileName(file)} has a key twice: {string.Join(", ", dupes)}");
        return t;
    }

    // a JSON object's dotted keys, noting a key that comes twice (JsonDocument would keep the last one silently)
    private static Dictionary<string, string> Flatten(byte[] json, List<string> dupes)
    {
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = new List<string>();
        string? name = null;
        while (r.Read())
        {
            switch (r.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (name is not null) path.Add(name);
                    name = null;
                    break;
                case JsonTokenType.EndObject:
                    if (path.Count > 0) path.RemoveAt(path.Count - 1);
                    break;
                case JsonTokenType.PropertyName:
                    name = r.GetString();
                    break;
                case JsonTokenType.String:
                    var key = string.Join('.', path.Append(name!));
                    if (!table.TryAdd(key, r.GetString()!)) dupes.Add(key);
                    name = null;
                    break;
                default:
                    Assert.Fail($"only texts and objects in a locale file (at {string.Join('.', path.Append(name ?? ""))})");
                    break;
            }
        }
        return table;
    }

    private static string En => Path.Combine(Root, "runtime", "locales", "en.json");

    // ---- the calls ----

    private sealed record Call(string File, int Line, string Method, string Key, bool Prefix, List<string>? Named, int Positional);

    [GeneratedRegex(@"\bKilnText\.(T|Plural)\(")]
    private static partial Regex CallStart();

    [GeneratedRegex(@"""(?:[a-z0-9_]+\.)+[a-z0-9_]+\.?""")]
    private static partial Regex KeyLiteral();

    private static List<Call> Calls()
    {
        var calls = new List<Call>();
        foreach (var file in Sources())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CallStart().Matches(text))
            {
                var args = Arguments(text, m.Index + m.Length);
                if (args.Count == 0) continue;
                var first = args[0].Trim();
                var lit = Regex.Match(first, @"^""((?:[^""\\]|\\.)*)""\s*(\+.*)?$", RegexOptions.Singleline);
                if (!lit.Success) continue;  // a key from a variable: its literals count as used, below
                var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                var rest = args.Skip(m.Groups[1].Value == "Plural" ? 2 : 1).Select(a => a.Trim()).ToList();
                List<string>? named = null;
                var positional = 0;
                foreach (var a in rest)
                {
                    var t = Regex.Match(a, @"^\(\s*""(\w+)""\s*,", RegexOptions.Singleline);
                    if (t.Success) (named ??= []).Add(t.Groups[1].Value);
                    else positional++;
                }
                calls.Add(new Call(Path.GetRelativePath(Root, file), line, m.Groups[1].Value, lit.Groups[1].Value, lit.Groups[2].Success, named, positional));
            }
        }
        return calls;
    }

    // the top-level arguments of a call whose '(' ends just before `at` (strings, chars and nesting skipped)
    private static List<string> Arguments(string text, int at)
    {
        var args = new List<string>();
        var depth = 0;
        var start = at;
        for (var i = at; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                var verbatim = i > 0 && text[i - 1] == '@';
                var raw = c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                if (raw)
                {
                    var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                for (i++; i < text.Length; i++)
                {
                    if (!verbatim && text[i] == '\\') { i++; continue; }
                    if (text[i] == c)
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                        break;
                    }
                }
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (text[start..i].Trim().Length > 0 || args.Count > 0) args.Add(text[start..i]);
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(text[start..i]);
                start = i + 1;
            }
        }
        return args;
    }

    [GeneratedRegex(@"\{\{|\}\}|\{([^{}:]+)(?::[^{}]*)?\}")]
    private static partial Regex Placeholder();

    private static HashSet<string> Placeholders(string template)
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Placeholder().Matches(template))
            if (m.Groups[1].Success) s.Add(m.Groups[1].Value.Trim());
        return s;
    }

    [Fact]
    public void Every_key_kiln_uses_is_in_english_with_the_placeholders_it_fills()
    {
        var en = Table(En);
        var problems = new List<string>();
        foreach (var c in Calls())
        {
            var where = $"{c.File}:{c.Line} {c.Method}(\"{c.Key}\")";
            if (c.Prefix)
            {
                if (!en.Keys.Any(k => k.StartsWith(c.Key, StringComparison.Ordinal))) problems.Add($"{where}: no key of en.json starts with it");
                continue;
            }
            string[] keys = c.Method == "Plural" ? [c.Key + ".one", c.Key + ".other"] : [c.Key];
            foreach (var key in keys)
            {
                if (!en.TryGetValue(key, out var template))
                {
                    problems.Add($"{where}: en.json has no '{key}'");
                    continue;
                }
                var used = Placeholders(template);
                if (c.Named is { } named)
                {
                    var given = new HashSet<string>(named, StringComparer.OrdinalIgnoreCase);
                    if (c.Method == "Plural") given.Add("count");
                    if (c.Positional > 0) problems.Add($"{where}: mixes named and positional values");
                    var missing = used.Where(u => !given.Contains(u)).ToList();
                    var extra = given.Where(g => !used.Contains(g, StringComparer.OrdinalIgnoreCase) && !(c.Method == "Plural" && g == "count")).ToList();
                    if (missing.Count > 0) problems.Add($"{where}: '{key}' has {{{string.Join("}, {", missing)}}} that the call does not fill");
                    var judged = c.Method != "Plural" || key.EndsWith(".other", StringComparison.Ordinal);  // a .one may leave a value out
                    if (extra.Count > 0 && judged)
                        problems.Add($"{where}: the call fills {string.Join(", ", extra)}, which '{key}' does not have");
                }
                else
                {
                    var given = c.Method == "Plural" ? new HashSet<string> { "count" } : Enumerable.Range(0, c.Positional).Select(i => i.ToString()).ToHashSet();
                    var missing = used.Where(u => !given.Contains(u)).ToList();
                    if (missing.Count > 0) problems.Add($"{where}: '{key}' has {{{string.Join("}, {", missing)}}} that the call does not fill");
                    if (c.Method == "T" && c.Positional > 0 && used.Count(u => u.All(char.IsAsciiDigit)) < c.Positional)
                        problems.Add($"{where}: {c.Positional} values for '{key}', which uses {used.Count(u => u.All(char.IsAsciiDigit))}");
                }
            }
        }
        // the C++ side names its keys as literals ("native.footer.loaded"); a plural group by its base
        foreach (var file in NativeSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"""(native\.[a-z0-9_.]+)"""))
            {
                var k = m.Groups[1].Value;
                if (!en.ContainsKey(k) && !en.ContainsKey(k + ".other"))
                    problems.Add($"{Path.GetRelativePath(Root, file)}: en.json has no '{k}'");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_english_key_is_used()
    {
        var en = Table(En);
        var used = new HashSet<string>(StringComparer.Ordinal) { KilnText.LanguageNameKey };
        var prefixes = new List<string>();
        foreach (var file in Sources().Concat(NativeSources()))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in KeyLiteral().Matches(text))
            {
                var k = m.Value[1..^1];
                if (k.EndsWith('.')) prefixes.Add(k);
                else used.Add(k);
            }
            foreach (Match m in Regex.Matches(text, @"""([a-z0-9_]+\.)""\s*\+"))  // "dev." + id + ".title": a one-part prefix
                prefixes.Add(m.Groups[1].Value);
        }
        var unused = en.Keys.Where(k =>
        {
            if (used.Contains(k) || prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))) return false;
            var dot = k.LastIndexOf('.');
            return !(dot > 0 && KilnText.PluralForms.Contains(k[(dot + 1)..]) && used.Contains(k[..dot]));  // a Plural call names the base
        }).ToList();
        Assert.True(unused.Count == 0, "en.json keys no code uses:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Translations_have_englishs_keys_and_placeholders()
    {
        var en = Table(En);
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(En)!, "*.json").Where(f => Path.GetFileName(f) != "en.json"))
        {
            var name = Path.GetFileName(file);
            var t = Table(file);
            foreach (var (key, text) in t)
            {
                var dot = key.LastIndexOf('.');
                var plural = dot > 0 && KilnText.PluralForms.Contains(key[(dot + 1)..]) && en.ContainsKey(key[..dot] + ".other");
                if (!en.ContainsKey(key) && !plural)
                {
                    problems.Add($"{name}: '{key}' is not a key of en.json");
                    continue;
                }
                var mine = Placeholders(text);
                if (plural)
                {
                    var b = key[..dot];
                    var english = Placeholders(en[b + ".other"]).Concat(en.TryGetValue(b + ".one", out var one) ? Placeholders(one) : []).ToHashSet();
                    var stray = mine.Where(p => !english.Contains(p) && p != "count").ToList();
                    if (stray.Count > 0) problems.Add($"{name}: '{key}' has {{{string.Join("}, {", stray)}}}, which English has not");
                }
                else if (!mine.SetEquals(Placeholders(en[key])))
                    problems.Add($"{name}: '{key}' has {{{string.Join("}, {", mine.Order())}}}, English {{{string.Join("}, {", Placeholders(en[key]).Order())}}}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_built_in_english_is_the_file()
    {
        var en = Table(En);
        Assert.All(en.Keys, k => Assert.Matches("^[a-z0-9_]+(\\.[a-z0-9_]+)+$", k));  // lower case, dotted: what the scanner reads
        Assert.Equal(en.OrderBy(p => p.Key, StringComparer.Ordinal), KilnText.English().OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void The_scanner_reads_calls_as_written()
    {
        var args = Arguments("""("a.b", ("name", f(x, y)), "s,\"t", 'c', [1, 2]) + 3""", 1);
        Assert.Equal(5, args.Count);
        Assert.Equal(["count", "x"], Placeholders("{count:N0} {{lit}} {x} }}").Order());
        var dupes = new List<string>();
        Flatten(Encoding.UTF8.GetBytes("""{ "a": { "b": "1" }, "a": { "b": "2" }, "c": "3" }"""), dupes);
        Assert.Equal(["a.b"], dupes);
    }
}
