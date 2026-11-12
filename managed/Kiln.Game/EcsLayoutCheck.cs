// SPDX-License-Identifier: MIT
// Hand-written. The first-use check of a generated type against the running game (docs/kiln/typegen.md §4): the
// layouts this Kiln.Game was generated with (the embedded resource Kiln.Game.types, every type of its dump) against
// the game's live type table (kiln.types), or, without one, the SHA-256 of the running exe against the dump's.
extern alias api;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kiln;

/// <summary>A type of the game's live type table, as the check needs it.</summary>
internal interface ILiveType
{
    int Kind { get; }
    int Size { get; }
    uint Hash { get; }
    /// <summary>The qualified name of a struct's base, an array's element, a typedef's target, an enum's underlying
    /// type or a bitmask's enum; null when there is none.</summary>
    string? Inner { get; }
    /// <summary>Every field, base classes first: name, offset, the field type's qualified name.</summary>
    IReadOnlyList<(string Name, int Offset, string Type)> AllFields { get; }
    /// <summary>An enum's values.</summary>
    IReadOnlyList<(string Name, long Value)> Values { get; }
}

/// <summary>The game's live type table: a type by qualified name, or null when the game has none of that name.</summary>
internal interface ILiveTypes
{
    ILiveType? Find(string qualifiedName);
}

/// <summary>The types a Kiln.Game was generated with (the resource Kiln.Game.types).</summary>
internal sealed class GeneratedTypes
{
    internal sealed record Field(string Name, int Offset, string Type);
    internal sealed record Ty(string Qn, int Kind, int Size, uint Hash, string? Inner, List<Field> Fields, List<(string Name, decimal Value)> Values);

    public string Side = "", Exe = "";
    public readonly Dictionary<string, Ty> ByQn = new(StringComparer.Ordinal);

    public static GeneratedTypes Parse(string text)
    {
        var g = new GeneratedTypes();
        bool header = false;
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 || raw.StartsWith("//", StringComparison.Ordinal)) continue;
            var p = raw.Split('\t');
            if (!header)
            {
                if (p[0] != "kiln.game.types" || p.Length < 2 || p[1] != "1") throw new FormatException("no kiln.game.types 1 header");
                foreach (var kv in p.Skip(2))
                {
                    var i = kv.IndexOf('=');
                    if (i < 0) continue;
                    if (kv[..i] == "side") g.Side = kv[(i + 1)..];
                    else if (kv[..i] == "exe_sha256") g.Exe = kv[(i + 1)..];
                }
                header = true;
                continue;
            }
            if (p.Length < 5) throw new FormatException("bad line: " + raw);
            var t = new Ty(p[0], int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture),
                Convert.ToUInt32(p[3], 16), p[4] == "-" ? null : p[4], [], []);
            foreach (var m in p.Skip(5))
            {
                int at = m.IndexOf('@'), colon = m.IndexOf(':'), eq = m.IndexOf('=');
                if (t.Kind == 18 && at > 0 && colon > at)
                    t.Fields.Add(new Field(m[..at], int.Parse(m[(at + 1)..colon], CultureInfo.InvariantCulture), m[(colon + 1)..]));
                else if (t.Kind == 12 && eq > 0)
                    t.Values.Add((m[..eq], decimal.Parse(m[(eq + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)));
                else throw new FormatException("bad member " + m + " of " + p[0]);
            }
            g.ByQn[t.Qn] = t;
        }
        if (!header) throw new FormatException("empty");
        return g;
    }

    /// <summary>A struct's fields, its bases' first.</summary>
    public IEnumerable<Field> AllFields(Ty t)
    {
        var chain = new List<Ty>();
        for (Ty? x = t; x != null; x = x.Inner != null && x.Kind == 18 && ByQn.TryGetValue(x.Inner, out var b) ? b : null)
            chain.Insert(0, x);
        return chain.SelectMany(c => c.Fields);
    }
}

/// <summary>The comparison itself: a pure function of the generated types, the live table and the exe's hash.</summary>
internal static class LayoutCheck
{
    /// <summary>Null when <paramref name="qn"/> can be read with the generated layout, else the first difference
    /// (type, field, generated value, live value). With no live table: supported only when the running exe is the one
    /// the dump was made from. <paramref name="memo"/> holds the types already checked (each is checked once).</summary>
    public static string? WhyNot(GeneratedTypes gen, string qn, ILiveTypes? live, Func<string?> exeSha256,
        Dictionary<string, string?>? memo = null)
    {
        if (!gen.ByQn.ContainsKey(qn)) return $"{qn} is not among the types this Kiln.Game was generated with";
        if (live is null)
        {
            var exe = exeSha256();
            if (exe is null)
                return $"the game's type table (kiln.types) is not available and the running exe could not be read, so " +
                       $"{qn} cannot be checked; this Kiln.Game was generated for {gen.Side} {Short(gen.Exe)}";
            return string.Equals(exe, gen.Exe, StringComparison.OrdinalIgnoreCase) ? null
                : $"the game's type table (kiln.types) is not available and this Kiln.Game was generated for {gen.Side} " +
                  $"{Short(gen.Exe)}, not for the running exe {Short(exe)}";
        }
        return Diff(gen, qn, live, memo ?? new(StringComparer.Ordinal));
    }

    static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;

    static string? Diff(GeneratedTypes gen, string qn, ILiveTypes live, Dictionary<string, string?> memo)
    {
        if (memo.TryGetValue(qn, out var known)) return known;
        memo[qn] = null;  // by-value types cannot contain themselves; this only stops a second visit
        var why = DiffOnce(gen, qn, live, memo);
        memo[qn] = why;
        return why;
    }

    static string? DiffOnce(GeneratedTypes gen, string qn, ILiveTypes live, Dictionary<string, string?> memo)
    {
        if (!gen.ByQn.TryGetValue(qn, out var g)) return $"{qn} is not among the types this Kiln.Game was generated with";
        var l = live.Find(qn);
        if (l is null) return $"{qn}: the game has no such type";
        if (l.Kind != g.Kind) return $"{qn}: kind {g.Kind} generated, {l.Kind} live";
        if (l.Size != g.Size) return $"{qn}: size {g.Size} generated, {l.Size} live";
        if (l.Hash != g.Hash) return $"{qn}: hash 0x{g.Hash:X8} generated, 0x{l.Hash:X8} live";
        // The dump names an inner type for kinds 12..19 only (a leaf's element is not followed), so only those compare.
        if (g.Kind is >= 12 and <= 17 or 19 && !string.Equals(l.Inner, g.Inner, StringComparison.Ordinal))
            return $"{qn}: inner type {g.Inner ?? "-"} generated, {l.Inner ?? "-"} live";
        switch (g.Kind)
        {
            case 18:
            {
                if (!string.Equals(l.Inner, g.Inner, StringComparison.Ordinal))
                    return $"{qn}: base {g.Inner ?? "-"} generated, {l.Inner ?? "-"} live";
                var lf = new Dictionary<string, (int Offset, string Type)>(StringComparer.Ordinal);
                foreach (var f in l.AllFields) lf[f.Name] = (f.Offset, f.Type);
                var gf = gen.AllFields(g).ToList();
                foreach (var f in gf)
                {
                    if (!lf.TryGetValue(f.Name, out var x)) return $"{qn}.{f.Name}: the game's type has no such field";
                    if (x.Offset != f.Offset) return $"{qn}.{f.Name}: offset {f.Offset} generated, {x.Offset} live";
                    if (!string.Equals(x.Type, f.Type, StringComparison.Ordinal)) return $"{qn}.{f.Name}: type {f.Type} generated, {x.Type} live";
                }
                if (lf.Count != gf.Count)
                    return $"{qn}: {gf.Count} fields generated, {lf.Count} live ({string.Join(", ", lf.Keys.Except(gf.Select(f => f.Name)).Take(3))} new)";
                foreach (var f in gf)
                    if (Diff(gen, f.Type, live, memo) is { } inner) return $"{qn}.{f.Name}: {inner}";
                return null;
            }
            case 12:
            {
                ulong mask = g.Size >= 8 ? ulong.MaxValue : (1ul << (8 * g.Size)) - 1;
                var lv = new Dictionary<string, ulong>(StringComparer.Ordinal);
                foreach (var v in l.Values) lv[v.Name] = unchecked((ulong)v.Value) & mask;
                foreach (var v in g.Values)
                {
                    ulong want = (v.Value < 0 ? unchecked((ulong)(long)v.Value) : (ulong)v.Value) & mask;
                    if (!lv.TryGetValue(v.Name, out var have)) return $"{qn}.{v.Name}: the game's enum has no such value";
                    if (have != want) return $"{qn}.{v.Name}: value {v.Value} generated, {have} live";
                }
                if (lv.Count != g.Values.Count) return $"{qn}: {g.Values.Count} values generated, {lv.Count} live";
                return null;
            }
            case >= 13 and <= 17 or 19:
                return g.Inner is null ? null : Diff(gen, g.Inner, live, memo);
            default:
                return null;  // primitives and leaves: kind and size are the layout
        }
    }

    /// <summary>A legacy struct (namespace Kiln.GameTypes) against the generated types of its game type: each of its
    /// own fields must be a generated field of the same offset and size. Null when they agree.</summary>
    public static string? LegacyWhyNot(GeneratedTypes gen, Type legacy, string qn)
    {
        if (!gen.ByQn.TryGetValue(qn, out var g)) return $"{qn} is not among the types this Kiln.Game was generated with";
        var fields = gen.AllFields(g).ToDictionary(f => f.Name, StringComparer.Ordinal);
        if (Unsafe.SizeOf(legacy) is var size && size != g.Size) return $"{legacy.FullName}: {size} bytes, {qn} is {g.Size}";
        foreach (var f in legacy.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            int off = f.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;
            int fsize = f.GetCustomAttribute<FixedBufferAttribute>() is { } fb ? fb.Length : Unsafe.SizeOf(f.FieldType);
            if (!fields.TryGetValue(f.Name, out var gf)) return $"{legacy.FullName}.{f.Name}: {qn} has no such field";
            if (gf.Offset != off) return $"{legacy.FullName}.{f.Name}: offset {off}, {qn} has it at {gf.Offset}";
            if (gen.ByQn.TryGetValue(gf.Type, out var ft) && ft.Size != fsize)
                return $"{legacy.FullName}.{f.Name}: {fsize} bytes, {gf.Type} is {ft.Size}";
        }
        return null;
    }

    private static class Unsafe
    {
        public static int SizeOf(Type t) => (int)typeof(System.Runtime.CompilerServices.Unsafe)
            .GetMethod(nameof(System.Runtime.CompilerServices.Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;
    }
}

/// <summary>The check as the typed calls use it: once per type, cached, thread-safe, one warning per mod and type.</summary>
internal static class EcsSupport
{
    internal const string ResourceName = "Kiln.Game.types";

    private static readonly Lazy<GeneratedTypes?> _gen = new(() =>
    {
        try
        {
            using var s = typeof(EcsSupport).Assembly.GetManifestResourceStream(ResourceName);
            if (s is null) return null;
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            return GeneratedTypes.Parse(r.ReadToEnd());
        }
        catch (FormatException) { return null; }
    });

    // Started when the check is first needed, so the hash of the running exe is ready when a type needs it.
    private static readonly Lazy<Task<string?>> _exe = new(() => Task.Run(HashProcessImage));
    private static readonly ConcurrentDictionary<Type, string?> _why = new();
    private static readonly ConcurrentDictionary<(ulong Owner, Type Type), bool> _warned = new();
    private static readonly object _gate = new();
    private static Dictionary<string, string?> _memo = new(StringComparer.Ordinal);

    /// <summary>Tests: where the warning goes instead of the mod's log.</summary>
    internal static Action<ulong, string>? WarnSink;

    internal static GeneratedTypes? Generated => _gen.Value;

    static string? HashProcessImage()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            using var f = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static ILiveTypes? Live() => api::Kiln.GameTypes.Available ? LiveGameTypes.Instance : null;

    /// <summary>Null when <paramref name="type"/> (game type <paramref name="qn"/>) can be read, else why not.</summary>
    public static string? WhyNot(Type type, string qn, Type? legacy = null) => _why.GetOrAdd(type, _ =>
    {
        var gen = _gen.Value;
        if (gen is null) return "this Kiln.Game has no layout data (the resource Kiln.Game.types is missing or unreadable)";
        string? why;
        lock (_gate) why = LayoutCheck.WhyNot(gen, qn, Live(), () => _exe.Value.GetAwaiter().GetResult(), _memo);
        return why ?? (legacy is null ? null : LayoutCheck.LegacyWhyNot(gen, legacy, qn));
    });

    /// <summary>True when the type can be read; else writes the one warning for the calling mod and returns false.</summary>
    public static bool Usable(Type type, string qn, Type? legacy = null)
    {
        var why = WhyNot(type, qn, legacy);
        if (why is null) return true;
        Unsupported(type, why);
        return false;
    }

    internal static void Unsupported(Type type, string why)
    {
        ulong owner = api::Kiln.ModContext.CurrentMod?.Owner ?? 0;
        if (!_warned.TryAdd((owner, type), true)) return;
        var text = $"Kiln.Game: {type.FullName} cannot be read on this game build, typed access to it returns nothing: {why}";
        if (WarnSink is { } sink) sink(owner, text);
        else api::Kiln.Backend.Current.Log(owner, api::Kiln.LogLevel.Warn, text);
    }

    /// <summary>Tests: forget every answer.</summary>
    internal static void Reset()
    {
        _why.Clear();
        _warned.Clear();
        lock (_gate) _memo = new(StringComparer.Ordinal);
    }
}

/// <summary>kiln.types (Kiln.Api's GameTypes) as the check sees it.</summary>
internal sealed class LiveGameTypes : ILiveTypes
{
    public static readonly LiveGameTypes Instance = new();

    public ILiveType? Find(string qualifiedName) =>
        api::Kiln.GameTypes.Find(qualifiedName) is { } t && t.QualifiedName == qualifiedName ? new Live(t) : null;

    private sealed class Live(api::Kiln.GameType t) : ILiveType
    {
        public int Kind => (int)t.Kind;
        public int Size => t.Size;
        public uint Hash => t.Hash;
        public string? Inner => t.Inner?.QualifiedName;
        public IReadOnlyList<(string Name, int Offset, string Type)> AllFields => [.. t.AllFields.Select(f => (f.Name, f.Offset, f.Type.QualifiedName))];
        public IReadOnlyList<(string Name, long Value)> Values => t.EnumValues;
    }
}
