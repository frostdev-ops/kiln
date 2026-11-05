// SPDX-License-Identifier: MIT
// Compact semantic versions and version ranges for kiln.mod.json. Self-contained: Kiln.Sdk's build tasks compile this
// file too, so a manifest is validated the same way at build time and at load time.
using System.Globalization;

namespace Kiln.Host;

/// <summary>A semantic version (https://semver.org). Parsing is lenient: <c>1</c> and <c>1.2</c> mean <c>1.0.0</c>
/// and <c>1.2.0</c>, a leading <c>v</c> is ignored. Build metadata (<c>+...</c>) is kept but never compared.</summary>
internal readonly struct SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private static readonly string[] NoPre = [];

    public SemVersion(int major, int minor, int patch, string[]? pre = null, string? build = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre ?? NoPre;
        _build = build ?? "";
    }

    private readonly string[]? _pre;
    private readonly string? _build;

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Pre-release identifiers (empty for a release).</summary>
    public string[] Pre => _pre ?? NoPre;

    public string Build => _build ?? "";
    public bool IsPrerelease => Pre.Length > 0;

    /// <summary>The same version without pre-release and build parts.</summary>
    public SemVersion Release => new(Major, Minor, Patch);

    /// <summary><c>X.Y.Z-0</c>: the lowest version of X.Y.Z (below every pre-release of it).</summary>
    public SemVersion Lowest => new(Major, Minor, Patch, ["0"]);

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a semantic version");

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        if (!TryParsePartial(text, out var parts, out var count, out var pre, out var build, out var wildcard) || count == 0 || wildcard)
            return false;
        version = new SemVersion(parts[0], count > 1 ? parts[1] : 0, count > 2 ? parts[2] : 0, pre, build);
        return true;
    }

    /// <summary>Parses <c>1</c>, <c>1.2</c>, <c>1.2.3</c>, <c>1.x</c>, <c>1.2.*</c> (count = numeric parts before
    /// the first wildcard, 0 for <c>*</c>), with optional <c>-pre</c> and <c>+build</c>.</summary>
    internal static bool TryParsePartial(string? text, out int[] parts, out int count, out string[] pre, out string build,
        out bool wildcard)
    {
        parts = [0, 0, 0];
        count = 0;
        pre = NoPre;
        build = "";
        wildcard = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            build = s[(plus + 1)..];
            if (build.Length == 0 || !build.Split('.').All(IsIdentifier)) return false;
            s = s[..plus];
        }
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var p = s[(dash + 1)..].Split('.');
            if (p.Length == 0 || !p.All(IsIdentifier)) return false;
            if (p.Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsAsciiDigit))) return false;  // no leading zeros
            pre = p;
            s = s[..dash];
        }
        var nums = s.Split('.');
        if (nums.Length is 0 or > 3) return false;
        for (var i = 0; i < nums.Length; i++)
        {
            var n = nums[i];
            if (n is "x" or "X" or "*")
            {
                wildcard = true;
                continue;
            }
            if (wildcard) return false;  // 1.x.3
            if (n.Length == 0 || !n.All(char.IsAsciiDigit) || (n.Length > 1 && n[0] == '0')) return false;
            if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
            count = i + 1;
        }
        if (wildcard && pre.Length > 0) return false;
        return true;
    }

    private static bool IsIdentifier(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public int CompareTo(SemVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        var a = Pre;
        var b = other.Pre;
        if (a.Length == 0 || b.Length == 0) return b.Length.CompareTo(a.Length);  // release > pre-release
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(SemVersion other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Pre));
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator ==(SemVersion a, SemVersion b) => a.Equals(b);
    public static bool operator !=(SemVersion a, SemVersion b) => !a.Equals(b);

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + string.Join('.', Pre) : "") + (Build.Length > 0 ? "+" + Build : "");
}

/// <summary>
/// A set of accepted versions. Syntax (combine with spaces or commas = AND, <c>||</c> = OR):
/// <list type="bullet">
/// <item><c>*</c> or empty: any version.</item>
/// <item><c>1.2.3</c> (bare) or <c>^1.2.3</c>: compatible with 1.2.3: <c>&gt;=1.2.3 &lt;2.0.0</c>; for 0.x the minor is the
/// breaking part (<c>^0.2</c> = <c>&gt;=0.2.0 &lt;0.3.0</c>), as in Cargo and npm.</item>
/// <item><c>~1.2</c>: patch updates only: <c>&gt;=1.2.0 &lt;1.3.0</c>.</item>
/// <item><c>=1.2.3</c>: exactly that version. <c>1.x</c>, <c>1.2.*</c>: wildcards.</item>
/// <item><c>&gt;=1.0 &lt;2</c>, <c>&gt;1.0</c>, <c>&lt;=2.1</c>: comparators. <c>1.0 - 2.0</c>: inclusive hyphen range.</item>
/// <item><c>[1.0,2.0)</c>, <c>[1.0,)</c>, <c>(,2.0]</c>, <c>[1.2.3]</c>: Maven/Forge intervals.</item>
/// </list>
/// An exclusive upper bound <c>&lt;X.Y.Z</c> also excludes the pre-releases of X.Y.Z (<c>&lt;2</c> rejects 2.0.0-beta).
/// </summary>
internal sealed class VersionRange
{
    private enum Op { Eq, Gt, Ge, Lt, Le }

    private readonly record struct Comparator(Op Op, SemVersion Version)
    {
        public bool Test(SemVersion v) => Op switch
        {
            Op.Eq => v == Version,
            Op.Gt => v > Version,
            Op.Ge => v >= Version,
            Op.Lt => v < Version,
            _ => v <= Version,
        };
    }

    private readonly List<List<Comparator>> _sets;  // OR of ANDs; one empty set = any

    private VersionRange(string text, List<List<Comparator>> sets)
    {
        Text = text;
        _sets = sets;
    }

    public string Text { get; }
    public static VersionRange Any { get; } = new("*", [[]]);
    public bool IsAny => _sets.Exists(s => s.Count == 0);

    public bool Satisfies(SemVersion v) => _sets.Exists(set => set.TrueForAll(c => c.Test(v)));

    /// <summary><paramref name="v"/> or any later version with the same major (an <c>apiVersion</c> written as a bare
    /// version: Kiln's API is additive within a major, 0.x included).</summary>
    public static VersionRange AtLeastSameMajor(SemVersion v) =>
        new(v.ToString(), [[new Comparator(Op.Ge, v.Release), new Comparator(Op.Lt, new SemVersion(v.Major + 1, 0, 0).Lowest)]]);

    public static VersionRange Parse(string text) =>
        TryParse(text, out var r, out var error) ? r : throw new FormatException(error);

    public static bool TryParse(string? text, out VersionRange range, out string error)
    {
        range = Any;
        error = "";
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s == "*") return true;
        var sets = new List<List<Comparator>>();
        foreach (var alt in s.Split("||"))
        {
            var part = alt.Trim();
            if (part.Length == 0)
            {
                error = $"version range '{s}': empty alternative";
                return false;
            }
            if (part[0] is '[' or '(')
            {
                if (!ParseIntervals(part, sets, out error)) return false;
                continue;
            }
            var set = new List<Comparator>();
            if (!ParseComparators(part, set, out error))
            {
                error = $"version range '{s}': {error}";
                return false;
            }
            sets.Add(set);
        }
        range = new VersionRange(s, sets);
        return true;
    }

    public override string ToString() => Text;

    private static bool ParseComparators(string part, List<Comparator> set, out string error)
    {
        error = "";
        var tokens = part.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // join operators written apart from their version (">= 1.0")
        for (var i = 0; i < tokens.Count - 1; i++)
            if (tokens[i] is ">=" or "<=" or ">" or "<" or "=" or "^" or "~")
            {
                tokens[i] += tokens[i + 1];
                tokens.RemoveAt(i + 1);
            }
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i + 2 < tokens.Count && tokens[i + 1] == "-")  // hyphen range
            {
                if (!Partial(tokens[i], out var lo) || !Partial(tokens[i + 2], out var hi))
                {
                    error = $"bad hyphen range '{tokens[i]} - {tokens[i + 2]}'";
                    return false;
                }
                set.Add(new Comparator(Op.Ge, lo.Floor));
                set.Add(hi.Count == 3 ? new Comparator(Op.Le, hi.Floor) : new Comparator(Op.Lt, hi.Next.Lowest));
                i += 2;
                continue;
            }
            if (!ParseOne(tokens[i], set, out error)) return false;
        }
        return true;
    }

    private static bool ParseOne(string token, List<Comparator> set, out string error)
    {
        error = "";
        var op = token.StartsWith(">=") || token.StartsWith("<=") ? token[..2]
            : token.Length > 0 && token[0] is '>' or '<' or '=' or '^' or '~' ? token[..1] : "";
        var rest = token[op.Length..];
        if (op == "~" && rest.StartsWith('>')) rest = rest[1..];  // ~> (Ruby style) = ~
        if (!Partial(rest, out var p))
        {
            error = $"'{token}' is not a version or comparator";
            return false;
        }
        if (p.Count == 0)  // * / x
        {
            if (op is "<" or ">") set.Add(new Comparator(Op.Lt, new SemVersion(0, 0, 0).Lowest));  // matches nothing
            return true;
        }
        if (op == "" && p.Wildcard) op = "=";  // 1.2.x / 1.* mean "any 1.2.z" / "any 1.y.z", not caret
        switch (op)
        {
            case "" or "^":
            {
                set.Add(new Comparator(Op.Ge, p.Floor));
                SemVersion upper;
                if (p.Major > 0 || p.Count == 1) upper = new SemVersion(p.Major + 1, 0, 0);
                else if (p.Minor > 0 || p.Count == 2) upper = new SemVersion(0, p.Minor + 1, 0);
                else upper = new SemVersion(0, 0, p.Patch + 1);
                set.Add(new Comparator(Op.Lt, upper.Lowest));
                break;
            }
            case "~":
                set.Add(new Comparator(Op.Ge, p.Floor));
                set.Add(new Comparator(Op.Lt, (p.Count == 1 ? new SemVersion(p.Major + 1, 0, 0) : new SemVersion(p.Major, p.Minor + 1, 0)).Lowest));
                break;
            case "=":
                if (p.Count == 3) set.Add(new Comparator(Op.Eq, p.Floor));
                else
                {
                    set.Add(new Comparator(Op.Ge, p.Floor));
                    set.Add(new Comparator(Op.Lt, p.Next.Lowest));
                }
                break;
            case ">=":
                set.Add(new Comparator(Op.Ge, p.Floor));
                break;
            case ">":
                set.Add(p.Count == 3 ? new Comparator(Op.Gt, p.Floor) : new Comparator(Op.Ge, p.Next.Lowest));
                break;
            case "<":
                set.Add(new Comparator(Op.Lt, p.Floor.IsPrerelease ? p.Floor : p.Floor.Lowest));
                break;
            case "<=":
                set.Add(p.Count == 3 ? new Comparator(Op.Le, p.Floor) : new Comparator(Op.Lt, p.Next.Lowest));
                break;
        }
        return true;
    }

    private static bool ParseIntervals(string text, List<List<Comparator>> sets, out string error)
    {
        error = "";
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
            if (i >= text.Length) break;
            if (text[i] is not ('[' or '('))
            {
                error = $"version range '{text}': expected '[' or '(' at {i}";
                return false;
            }
            var close = text.IndexOfAny([']', ')'], i);
            if (close < 0)
            {
                error = $"version range '{text}': unclosed interval";
                return false;
            }
            var lowerInclusive = text[i] == '[';
            var upperInclusive = text[close] == ']';
            var body = text[(i + 1)..close];
            var comma = body.IndexOf(',');
            var set = new List<Comparator>();
            if (comma < 0)
            {
                if (!lowerInclusive || !upperInclusive || !SemVersion.TryParse(body, out var exact))
                {
                    error = $"version range '{text}': '[{body}]' must be an exact version in square brackets";
                    return false;
                }
                set.Add(new Comparator(Op.Eq, exact));
            }
            else
            {
                var lo = body[..comma].Trim();
                var hi = body[(comma + 1)..].Trim();
                if (lo.Length > 0)
                {
                    if (!SemVersion.TryParse(lo, out var l))
                    {
                        error = $"version range '{text}': bad lower bound '{lo}'";
                        return false;
                    }
                    set.Add(new Comparator(lowerInclusive ? Op.Ge : Op.Gt, l));
                }
                if (hi.Length > 0)
                {
                    if (!SemVersion.TryParse(hi, out var h))
                    {
                        error = $"version range '{text}': bad upper bound '{hi}'";
                        return false;
                    }
                    set.Add(upperInclusive ? new Comparator(Op.Le, h) : new Comparator(Op.Lt, h.IsPrerelease ? h : h.Lowest));
                }
            }
            sets.Add(set);
            i = close + 1;
        }
        if (sets.Count == 0)
        {
            error = $"version range '{text}': no interval";
            return false;
        }
        return true;
    }

    private readonly record struct PartialVersion(int Major, int Minor, int Patch, int Count, string[] Pre, bool Wildcard)
    {
        public SemVersion Floor => new(Major, Minor, Patch, Pre);

        /// <summary>The first version above every version matching the partial (1.2 → 1.3.0, 1 → 2.0.0).</summary>
        public SemVersion Next => Count switch
        {
            1 => new SemVersion(Major + 1, 0, 0),
            2 => new SemVersion(Major, Minor + 1, 0),
            _ => new SemVersion(Major, Minor, Patch + 1),
        };
    }

    private static bool Partial(string text, out PartialVersion p)
    {
        p = default;
        if (!SemVersion.TryParsePartial(text, out var parts, out var count, out var pre, out _, out var wildcard)) return false;
        p = new PartialVersion(parts[0], parts[1], parts[2], count, pre, wildcard);
        return true;
    }
}
