// SPDX-License-Identifier: MIT
// The round trip: a built Kiln.Game against the schema-2 dump it was generated from, read again here without the
// generator. Every type of the dump has its C# form with the dump's sizes, offsets, names, values and hashes. The
// naming rules and the layout hash are written a second time below, from docs/kiln/typegen.md §3, so a generator that
// strays from the doc fails here.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kiln.Game.Tests;

/// <summary>One schema-2 dump, read for the round trip.</summary>
sealed class GameDump
{
    public sealed record Field(string Name, int Offset, string Type);
    public sealed record Value(string Name, decimal Number);
    public sealed record Ty(string Name, string Qn, string Hash, int Kind, int Size, int Align, string? Inner,
        List<Field>? Fields, List<Value>? Values);

    public string Side = "", Exe = "";
    public readonly List<Ty> Types = [];
    public readonly Dictionary<string, Ty> By = new(StringComparer.Ordinal);
    public HashSet<string> Components = [], Events = [];

    public static GameDump Read(string path) => Parse(File.ReadAllText(path));

    public static GameDump Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var d = new GameDump { Side = r.GetProperty("side").GetString()!, Exe = r.GetProperty("exe_sha256").GetString()! };
        d.Components = r.GetProperty("components").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        d.Events = r.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        foreach (var t in r.GetProperty("types").EnumerateArray())
        {
            var ty = new Ty(t.GetProperty("name").GetString()!, t.GetProperty("qualifiedName").GetString()!,
                t.GetProperty("hash").GetString()!, t.GetProperty("kind").GetInt32(), t.GetProperty("size").GetInt32(),
                t.GetProperty("align").GetInt32(), t.TryGetProperty("inner", out var i) ? i.GetString() : null,
                t.TryGetProperty("fields", out var fs)
                    ? fs.EnumerateArray().Select(f => new Field(f.GetProperty("name").GetString()!, f.GetProperty("offset").GetInt32(), f.GetProperty("type").GetString()!)).ToList()
                    : null,
                t.TryGetProperty("values", out var vs)
                    ? vs.EnumerateArray().Select(v => new Value(v.GetProperty("name").GetString()!, decimal.Parse(v.GetProperty("value").GetRawText()))).ToList()
                    : null);
            d.Types.Add(ty);
            d.By[ty.Qn] = ty;
        }
        return d;
    }

    /// <summary>The schema-2 dumps under sigdb/types, by side.</summary>
    public static List<string> Committed()
    {
        var dir = GenRepo.Path_("sigdb", "types");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories)
            .Where(IsSchema2)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    static bool IsSchema2(string f)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(f));
        return doc.RootElement.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() == 2;
    }
}

static class RoundTrip
{
    public const string Root = "Ecs";  // Kiln.Game.csproj KilnGameRoot

    static readonly Dictionary<int, Type> Prims = new()
    {
        [1] = typeof(bool), [2] = typeof(byte), [3] = typeof(sbyte), [4] = typeof(ushort), [5] = typeof(short),
        [6] = typeof(uint), [7] = typeof(int), [8] = typeof(ulong), [9] = typeof(long), [10] = typeof(float), [11] = typeof(double),
    };
    static readonly Dictionary<int, Type> MaskUnder = new() { [13] = typeof(byte), [14] = typeof(ushort), [15] = typeof(uint), [16] = typeof(ulong) };

    public static uint Fnv32(string s)
    {
        uint h = 0x811C9DC5u;
        foreach (var b in Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 0x01000193u;
        return h;
    }

    public static ulong Fnv64(string s)
    {
        ulong h = 0xCBF29CE484222325ul;
        foreach (var b in Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 0x100000001B3ul;
        return h;
    }

    /// <summary>keen::ecs::X and keen::X are X; keen::ns::X is Ns_X (each namespace part in PascalCase).</summary>
    public static string CsName(string qn)
    {
        var parts = qn.Split("::");
        var ns = parts[..^1].ToList();
        if (ns.Count > 0 && ns[0] == "keen") ns.RemoveAt(0);
        if (ns.Count == 1 && ns[0] == "ecs") ns.Clear();
        static string Pascal(string p) => string.Concat(p.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
        return ns.Count == 0 ? parts[^1] : string.Concat(ns.Select(Pascal)) + "_" + parts[^1];
    }

    /// <summary>The layout hash of docs/kiln/typegen.md §3, computed from the dump alone.</summary>
    public static ulong Layout(GameDump d, string qn, Dictionary<string, ulong> memo)
    {
        if (memo.TryGetValue(qn, out var h)) return h;
        var t = d.By[qn];
        string H(string q) => Layout(d, q, memo).ToString("x16");
        var sb = new StringBuilder();
        switch (t.Kind)
        {
            case >= 1 and <= 11: sb.Append($"primitive {qn}\nkind {t.Kind}\nsize {t.Size}\n"); break;
            case 12:
                sb.Append($"enum {qn}\nsize {t.Size}\nunderlying {t.Inner}\n");
                foreach (var v in t.Values!) sb.Append($"value {v.Name}={v.Number}\n");
                break;
            case >= 13 and <= 16: sb.Append($"bitmask {qn}\nsize {t.Size}\nenum {t.Inner} {H(t.Inner!)}\n"); break;
            case 17: sb.Append($"typedef {qn}\ntarget {t.Inner} {H(t.Inner!)}\n"); break;
            case 19: sb.Append($"array {qn}\nlength {t.Size / d.By[t.Inner!].Size}\nelement {t.Inner} {H(t.Inner!)}\n"); break;
            case 18:
                sb.Append($"struct {qn}\nsize {t.Size}\nalign {t.Align}\n");
                sb.Append(t.Inner is null ? "base -\n" : $"base {t.Inner} {H(t.Inner)}\n");
                foreach (var f in t.Fields!) sb.Append($"field {f.Name} {f.Offset} {f.Type} {H(f.Type)}\n");
                break;
            default: sb.Append($"leaf {qn}\nkind {t.Kind}\nsize {t.Size}\nalign {t.Align}\n"); break;
        }
        return memo[qn] = Fnv64(sb.ToString());
    }

    /// <summary>A struct's fields with its bases' first.</summary>
    public static List<GameDump.Field> Flat(GameDump d, GameDump.Ty t) =>
        [.. t.Inner is null ? [] : Flat(d, d.By[t.Inner]), .. t.Fields!];

    static GameDump.Ty Resolve(GameDump d, GameDump.Ty t)
    {
        while (t.Kind == 17) t = d.By[t.Inner!];
        return t;
    }

    public static int SizeOf(Type t) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;

    /// <summary>Every difference between the assembly and the dump, one line each; empty when they agree.</summary>
    public static List<string> Check(Assembly asm, GameDump d)
    {
        var bad = new List<string>();
        var memo = new Dictionary<string, ulong>();
        var root = asm.GetType("Kiln." + Root);
        if (root is null) return ["no class Kiln." + Root];
        Type? Nested(string name) => root.GetNestedType(name, BindingFlags.Public);
        var iStruct = asm.GetType("Kiln.IGameStruct`1")!;
        var iComp = asm.GetType("Kiln.IGameComponent`1")!;
        var iEvent = asm.GetType("Kiln.IGameEvent`1")!;
        var iAttr = asm.GetType("Kiln.IGameAttribute`2")!;
        int expectedTypes = 2;  // the hand-written ObjectReference and GameGuid
        var lengths = new HashSet<int>();

        // The C# type a field of dump type qn must have; null when it is a raw byte buffer.
        string? Expect(GameDump.Ty ft, out Type? clr)
        {
            ft = Resolve(d, ft);
            clr = null;
            switch (ft.Kind)
            {
                case >= 1 and <= 11: clr = Prims[ft.Kind]; return clr.Name;
                case 12 or 18: clr = Nested(CsName(ft.Qn)); return CsName(ft.Qn);
                case >= 13 and <= 16: clr = Nested(CsName(ft.Inner!) + "Mask"); return CsName(ft.Inner!) + "Mask";
                case 28 when ft.Size == 16: clr = Nested("ObjectReference"); return "ObjectReference";
                case 29 when ft.Size == 16: clr = Nested("GameGuid"); return "GameGuid";
                case 19:
                {
                    var e = d.By[ft.Inner!];
                    int n = ft.Size / e.Size;
                    if (Expect(e, out var ec) is null || ec is null) return null;
                    var def = Nested($"StaticArray{n}`1");
                    lengths.Add(n);
                    if (def is null) return $"StaticArray{n}<…> (missing)";
                    clr = def.MakeGenericType(ec);
                    return $"StaticArray{n}<{ec.Name}>";
                }
                default: return null;
            }
        }

        foreach (var t in d.Types)
        {
            var at = t.Qn;
            switch (t.Kind)
            {
                case 18:
                {
                    expectedTypes++;
                    var cs = CsName(t.Qn);
                    var ty = Nested(cs);
                    if (ty is null) { bad.Add($"{at}: no generated type {Root}.{cs}"); continue; }
                    if (!ty.IsValueType || ty.StructLayoutAttribute?.Value != LayoutKind.Explicit || ty.StructLayoutAttribute.Size != t.Size)
                        bad.Add($"{at}: {cs} is not an explicit struct of {t.Size} bytes");
                    int size = SizeOf(ty);
                    if (size != t.Size) bad.Add($"{at}: Unsafe.SizeOf<{cs}>() is {size}, the dump says {t.Size}");
                    object? S(string n) => ty.GetField(n, BindingFlags.Public | BindingFlags.Static) is { IsInitOnly: true, IsLiteral: false } f ? f.GetValue(null) : "(missing or not static readonly)";
                    uint hash = Convert.ToUInt32(t.Hash, 16);
                    if (hash != Fnv32(t.Qn)) bad.Add($"{at}: the dump's hash {t.Hash} is not FNV-1a-32 of the name");
                    if (!Equals(S("Hash"), hash)) bad.Add($"{at}: Hash is {S("Hash")}, the dump says {t.Hash}");
                    if (!Equals(S("Name"), t.Name)) bad.Add($"{at}: Name is {S("Name")}");
                    if (!Equals(S("QualifiedName"), t.Qn)) bad.Add($"{at}: QualifiedName is {S("QualifiedName")}");
                    if (!Equals(S("Size"), t.Size)) bad.Add($"{at}: Size is {S("Size")}");
                    if (!Equals(S("Layout"), Layout(d, t.Qn, memo))) bad.Add($"{at}: Layout is {S("Layout")}, the doc's hash is {Layout(d, t.Qn, memo):x16}");
                    var flat = Flat(d, t);
                    var fields = ty.GetFields(BindingFlags.Public | BindingFlags.Instance).ToDictionary(f => f.Name);
                    if (fields.Count != flat.Count) bad.Add($"{at}: {fields.Count} fields, the dump (bases included) has {flat.Count}");
                    foreach (var f in flat)
                    {
                        if (!fields.TryGetValue(f.Name, out var fi)) { bad.Add($"{at}.{f.Name}: no such field"); continue; }
                        int off = fi.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;
                        if (off != f.Offset) bad.Add($"{at}.{f.Name}: FieldOffset {off}, the dump says {f.Offset}");
                        var ft = d.By[f.Type];
                        var fb = fi.GetCustomAttribute<FixedBufferAttribute>();
                        int fsize = fb != null ? fb.Length : SizeOf(fi.FieldType);
                        if (fsize != ft.Size) bad.Add($"{at}.{f.Name}: its C# type is {fsize} bytes, {f.Type} is {ft.Size}");
                        var want = Expect(ft, out var clr);
                        if (want is null)
                        {
                            if (fb is null || fb.ElementType != typeof(byte)) bad.Add($"{at}.{f.Name}: {f.Type} should stay a raw byte buffer, is {fi.FieldType.Name}");
                        }
                        else if (clr is null || fi.FieldType != clr)
                            bad.Add($"{at}.{f.Name}: C# type {fi.FieldType}, expected {want}");
                        else if (Resolve(d, ft) is { Kind: 19 } arr && SizeOf(clr.GetGenericArguments()[0]) != d.By[arr.Inner!].Size)
                            bad.Add($"{at}.{f.Name}: the inline array's element is {SizeOf(clr.GetGenericArguments()[0])} bytes, {arr.Inner} is {d.By[arr.Inner!].Size}");
                    }
                    bool isComp = d.Components.Contains(t.Qn), isEvent = d.Events.Contains(t.Qn);
                    bool Implements(Type def) => ty.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == def && i.GetGenericArguments()[0] == ty);
                    if (!Implements(iStruct)) bad.Add($"{at}: does not implement IGameStruct<{cs}>");
                    if (Implements(iComp) != isComp) bad.Add($"{at}: IGameComponent<{cs}> does not match the component list");
                    if (Implements(iEvent) != isEvent) bad.Add($"{at}: IGameEvent<{cs}> does not match the event list");
                    // An attribute component (base chain through keen::ecs::AttributeComponent, a dataStorage array):
                    // IGameAttribute<T, element> and a Span<element> Values.
                    Type? element = null;
                    bool attrBase = false;
                    for (var x = t; x.Inner is not null; x = d.By[x.Inner]) attrBase |= x.Inner == "keen::ecs::AttributeComponent";
                    if (isComp && attrBase && flat.FirstOrDefault(f => f.Name == "dataStorage") is { } ds && Resolve(d, d.By[ds.Type]) is { Kind: 19 } dsArr)
                        Expect(d.By[dsArr.Inner!], out element);
                    var attr = ty.GetInterfaces().SingleOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == iAttr);
                    if ((attr?.GetGenericArguments()[1]) != element)
                        bad.Add($"{at}: IGameAttribute<{cs}, …> is {attr?.GetGenericArguments()[1].Name ?? "missing"}, the dump makes it {element?.Name ?? "none"}");
                    if (element is not null && ty.GetProperty("Values")?.PropertyType != typeof(Span<>).MakeGenericType(element))
                        bad.Add($"{at}: no Span<{element.Name}> Values");
                    break;
                }
                case 12:
                {
                    expectedTypes++;
                    var ty = Nested(CsName(t.Qn));
                    if (ty is null || !ty.IsEnum) { bad.Add($"{at}: no generated enum {Root}.{CsName(t.Qn)}"); continue; }
                    var under = Prims[Resolve(d, d.By[t.Inner!]).Kind];
                    if (Enum.GetUnderlyingType(ty) != under) bad.Add($"{at}: underlying type {Enum.GetUnderlyingType(ty)}, expected {under}");
                    var have = ty.GetFields(BindingFlags.Public | BindingFlags.Static).ToDictionary(f => f.Name, f => Convert.ToDecimal(f.GetRawConstantValue()));
                    var want = t.Values!.ToDictionary(v => v.Name, v => v.Number);
                    if (!have.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(want.OrderBy(k => k.Key, StringComparer.Ordinal)))
                        bad.Add($"{at}: values {string.Join(",", have.Select(k => $"{k.Key}={k.Value}"))}, the dump has {string.Join(",", want.Select(k => $"{k.Key}={k.Value}"))}");
                    break;
                }
                case >= 13 and <= 16:
                {
                    expectedTypes++;
                    var e = d.By[t.Inner!];
                    var name = CsName(e.Qn) + "Mask";
                    var ty = Nested(name);
                    if (ty is null || !ty.IsEnum) { bad.Add($"{at}: no generated mask {Root}.{name}"); continue; }
                    if (ty.GetCustomAttribute<FlagsAttribute>() is null) bad.Add($"{at}: {name} is not [Flags]");
                    if (Enum.GetUnderlyingType(ty) != MaskUnder[t.Kind]) bad.Add($"{at}: {name} is over {Enum.GetUnderlyingType(ty)}");
                    var have = ty.GetFields(BindingFlags.Public | BindingFlags.Static).ToDictionary(f => f.Name, f => Convert.ToDecimal(f.GetRawConstantValue()));
                    var want = e.Values!.ToDictionary(v => v.Name, v => (decimal)(1ul << (int)v.Number));
                    if (!have.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(want.OrderBy(k => k.Key, StringComparer.Ordinal)))
                        bad.Add($"{at}: {name} members are not 1 << each value of {e.Qn}");
                    break;
                }
            }
        }
        foreach (var n in lengths)
        {
            var def = Nested($"StaticArray{n}`1");
            if (def?.GetCustomAttribute<InlineArrayAttribute>()?.Length != n) bad.Add($"StaticArray{n}: not an [InlineArray({n})]");
        }
        expectedTypes += lengths.Count;
        // Ecs.Programs (program descriptors, only in a build given a catalog) is not one of the dump's types.
        int nested = root.GetNestedTypes(BindingFlags.Public).Count(t => t.Name != "Programs");
        if (nested != expectedTypes) bad.Add($"{Root} has {nested} public nested types; the dump makes {expectedTypes} (structs, enums, masks, array lengths, 2 hand-written)");
        return bad;
    }
}
