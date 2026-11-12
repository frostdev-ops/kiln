// SPDX-License-Identifier: MIT
// The comparisons of the lab check, shared by the [KilnTest] methods and the guided client check. Everything here runs
// on the tick thread (kiln.world and Reflected are tick-thread APIs).
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;

namespace TypegenProbe;

/// <summary>One generated ECS component type, as the check sees it.</summary>
public sealed class CheckedType
{
    public required Type Type { get; init; }
    public required string Name { get; init; }           // the game's short name (kiln.world)
    public required string QualifiedName { get; init; }
    public required int Size { get; init; }
    public int LiveEntities { get; set; }
    public List<EntityId> Entities { get; } = [];
    public HashSet<string> Features { get; } = [];      // enum, bitmask, array-of-structs, depth3, ...
    // results
    public int BytesCompared, BytesMatched, BytesGone, FieldEntities, FieldsGone, FieldsCompared, FieldsMatched;
    public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);
    public List<string> Mismatches { get; } = [];
    internal Checker Checker => _checker ??= (Checker)Activator.CreateInstance(typeof(Checker<>).MakeGenericType(Type))!;
    private Checker? _checker;
    public override string ToString() => QualifiedName;
}

/// <summary>A leaf value reached through a generated struct.</summary>
public readonly record struct Leaf(string Path, int Offset, int Size, object Value);

internal abstract class Checker
{
    /// <summary>Ecs.TryGet&lt;T&gt;: the typed copy's bytes, or null when it returned false.</summary>
    public abstract byte[]? TypedBytes(EntityId e, out object? boxed);
    public abstract string? WhyNot();
    public abstract bool Supports();
    public abstract object FromBytes(byte[] bytes);
}

internal sealed class Checker<T> : Checker where T : unmanaged, IGameComponent<T>
{
    public override byte[]? TypedBytes(EntityId e, out object? boxed)
    {
        if (!Ecs.TryGet<T>(e, out var v)) { boxed = null; return null; }
        boxed = v;
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref v, 1)).ToArray();
    }
    public override string? WhyNot() => Ecs.WhyNot<T>();
    public override bool Supports() => Ecs.Supports<T>();
    public override object FromBytes(byte[] bytes) => MemoryMarshal.Read<T>(bytes);
}

public static class TypedReadCheck
{
    /// <summary>Inline arrays are walked element by element up to this many elements; the rest is counted as skipped.</summary>
    public const int ArrayCap = 64;
    /// <summary>At most this many leaves per entity are compared; the rest is counted as skipped.</summary>
    public const int LeafCap = 4096;

    const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Every generated ECS component type of the loaded Kiln.Game.</summary>
    public static List<CheckedType> AllComponentTypes()
    {
        var list = new List<CheckedType>();
        foreach (var t in typeof(Ecs).GetNestedTypes(BindingFlags.Public))
        {
            if (!t.IsValueType || t.IsGenericTypeDefinition) continue;
            if (!t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IGameComponent<>))) continue;
            string S(string n) => (string)t.GetField(n, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            list.Add(new CheckedType
            {
                Type = t, Name = S("Name"), QualifiedName = S("QualifiedName"),
                Size = (int)t.GetField("Size", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!,
            });
        }
        return list;
    }

    /// <summary>Fills Features: what kinds of field the type holds, by .NET reflection over the generated struct.</summary>
    public static void Describe(CheckedType c)
    {
        void Walk(Type t, int depth)
        {
            if (depth > 1) c.Features.Add("nested");
            if (depth >= 3) c.Features.Add("depth3");
            foreach (var f in Fields(t))
            {
                var ft = f.FieldType;
                if (f.GetCustomAttribute<FixedBufferAttribute>() != null) { c.Features.Add("raw-buffer"); continue; }
                Kind(ft, depth);
            }
        }
        void Kind(Type ft, int depth)
        {
            if (ft.IsEnum) c.Features.Add(ft.GetCustomAttribute<FlagsAttribute>() != null ? "bitmask" : "enum");
            else if (IsInlineArray(ft, out _, out var elem))
            {
                c.Features.Add("static-array");
                if (IsGeneratedStruct(elem)) c.Features.Add("array-of-structs");
                Kind(elem, depth);
            }
            else if (IsGeneratedStruct(ft)) Walk(ft, depth + 1);
        }
        Walk(c.Type, 1);
    }

    static readonly ConcurrentDictionary<Type, FieldInfo[]> _fields = new();
    static FieldInfo[] Fields(Type t) => _fields.GetOrAdd(t, x => x.GetFields(Inst).OrderBy(Offset).ToArray());
    static int Offset(FieldInfo f) => f.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0;

    static bool IsGeneratedStruct(Type t) =>
        t.IsValueType && !t.IsPrimitive && !t.IsEnum && t.DeclaringType == typeof(Ecs) && !IsInlineArray(t, out _, out _)
        && t != typeof(Ecs.ObjectReference) && t != typeof(Ecs.GameGuid);

    static bool IsInlineArray(Type t, out int length, out Type element)
    {
        length = 0;
        element = typeof(void);
        if (!t.IsGenericType || t.GetCustomAttribute<InlineArrayAttribute>() is not { } a) return false;
        length = a.Length;
        element = t.GetGenericArguments()[0];
        return true;
    }

    static readonly ConcurrentDictionary<Type, int> _sizes = new();
    static int SizeOf(Type t) => _sizes.GetOrAdd(t, x =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(x).Invoke(null, null)!);

    static readonly ConcurrentDictionary<(Type, Type), MethodInfo> _elem = new();
    static object ElementAt(object array, Type arrayType, Type elem, int i) =>
        _elem.GetOrAdd((arrayType, elem), k => typeof(TypedReadCheck).GetMethod(nameof(Elem), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(k.Item1, k.Item2)).Invoke(null, [array, i])!;
    static object Elem<TArr, TElem>(object array, int i) where TArr : struct
    {
        var a = (TArr)array;
        return Unsafe.Add(ref Unsafe.As<TArr, TElem>(ref a), i)!;
    }

    /// <summary>Every leaf of a boxed generated struct, walked through its fields by .NET reflection. Skipped parts
    /// (raw buffers, array elements past <see cref="ArrayCap"/>, leaves past <see cref="LeafCap"/>) are counted in
    /// <paramref name="skipped"/> by reason.</summary>
    public static List<Leaf> Leaves(object boxed, Type type, Dictionary<string, int> skipped)
    {
        var leaves = new List<Leaf>();
        void Skip(string why, int n = 1) => skipped[why] = skipped.GetValueOrDefault(why) + n;
        void Struct(object v, Type t, string prefix, int baseOff)
        {
            foreach (var f in Fields(t))
            {
                var path = prefix + f.Name;
                if (f.GetCustomAttribute<FixedBufferAttribute>() != null) { Skip("raw buffer (blob array or blob string, data outside the struct)"); continue; }
                Value(f.GetValue(v)!, f.FieldType, path, baseOff + Offset(f));
            }
        }
        void Value(object v, Type t, string path, int off)
        {
            if (leaves.Count >= LeafCap) { Skip($"over {LeafCap} leaves per entity"); return; }
            if (t.IsPrimitive || t.IsEnum || t == typeof(Ecs.ObjectReference) || t == typeof(Ecs.GameGuid))
            {
                leaves.Add(new Leaf(path, off, SizeOf(t), v));
            }
            else if (IsInlineArray(t, out var n, out var elem))
            {
                var es = SizeOf(elem);
                for (var i = 0; i < Math.Min(n, ArrayCap); i++) Value(ElementAt(v, t, elem, i), elem, $"{path}[{i}]", off + i * es);
                if (n > ArrayCap) Skip($"array elements past {ArrayCap}", n - ArrayCap);
            }
            else if (IsGeneratedStruct(t)) Struct(v, t, path + ".", off);
            else Skip($"unknown C# type {t.Name}");
        }
        Struct(boxed, type, "", 0);
        return leaves;
    }

    /// <summary>The field path that covers byte <paramref name="offset"/> of a generated struct (all leaves, no caps).</summary>
    public static string FieldAt(CheckedType c, byte[] typed, int offset)
    {
        var skipped = new Dictionary<string, int>();
        foreach (var l in Leaves(c.Checker.FromBytes(typed), c.Type, skipped))
            if (offset >= l.Offset && offset < l.Offset + l.Size) return l.Path;
        foreach (var f in Fields(c.Type))
            if (f.GetCustomAttribute<FixedBufferAttribute>() is { } fb && offset >= Offset(f) && offset < Offset(f) + fb.Length)
                return f.Name + " (raw buffer)";
        return $"(bytes no field names, +{offset})";
    }

    /// <summary>Compares the typed copy with the raw component bytes. Null when equal, else the mismatch, naming the
    /// type and the field of the first differing byte.</summary>
    public static string? CompareBytes(CheckedType c, byte[] typed, byte[] raw)
    {
        if (raw.Length != typed.Length)
            return $"{c.QualifiedName}: {typed.Length} typed bytes, the live component has {raw.Length}";
        for (var i = 0; i < typed.Length; i++)
            if (typed[i] != raw[i])
                return $"{c.QualifiedName}.{FieldAt(c, typed, i)}: byte +{i} is 0x{typed[i]:X2} typed, 0x{raw[i]:X2} raw";
        return null;
    }

    /// <summary>Bytes: Ecs.TryGet against World.ReadBytes for every planned entity of the type, in this tick.</summary>
    public static void RunBytes(CheckedType c, int maxEntities)
    {
        foreach (var e in c.Entities.Take(maxEntities))
        {
            var raw = World.ReadBytes(e, c.Name);
            if (raw is null) { c.BytesGone++; continue; }       // the entity lost the component since the plan
            c.BytesCompared++;
            var typed = c.Checker.TypedBytes(e, out _);
            if (typed is null) { c.Mismatches.Add($"{c.QualifiedName}: Ecs.TryGet returned false for entity {e.Value}, which has the component"); continue; }
            if (CompareBytes(c, typed, raw) is { } why) c.Mismatches.Add($"entity {e.Value}: {why}");
            else c.BytesMatched++;
        }
    }

    /// <summary>Fields: every leaf of the typed copy against Reflected.Component(entity, qualified name)[path], for one
    /// entity, in this tick. Returns false when the entity no longer has the component.</summary>
    public static bool RunFields(CheckedType c, EntityId e, object? overrideBoxed = null, List<string>? into = null)
    {
        var rv = Reflected.Component(e, c.QualifiedName);
        if (rv is null)
        {
            // gone since the plan, unless kiln.world still has it: then Reflected and kiln.world disagree
            if (World.ReadBytes(e, c.Name) is not null)
                (into ?? c.Mismatches).Add($"entity {e.Value}: {c.QualifiedName}: Reflected.Component is null, kiln.world has the component");
            else if (overrideBoxed is null) c.FieldsGone++;
            return false;
        }
        object? boxed = overrideBoxed;
        if (boxed is null && c.Checker.TypedBytes(e, out boxed) is null)
        {
            (into ?? c.Mismatches).Add($"{c.QualifiedName}: Ecs.TryGet returned false for entity {e.Value}, which has the component");
            return true;
        }
        if (overrideBoxed is null) c.FieldEntities++;
        var skipped = overrideBoxed is null ? c.Skipped : new Dictionary<string, int>();   // a self-test counts nothing
        foreach (var leaf in Leaves(boxed!, c.Type, skipped))
        {
            object? live;
            try { live = rv[leaf.Path]; }
            catch (Exception x) when (x is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                var reason = $"Reflected cannot read it: {x.GetType().Name}: {x.Message}";
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                continue;
            }
            if (overrideBoxed is null) c.FieldsCompared++;
            if (Same(leaf, live, out var gen, out var got)) { if (overrideBoxed is null) c.FieldsMatched++; }
            else (into ?? c.Mismatches).Add($"entity {e.Value}: {c.QualifiedName}.{leaf.Path} (+{leaf.Offset}): typed {gen}, reflected {got}");
        }
        return true;
    }

    static bool Same(Leaf leaf, object? live, out string gen, out string got)
    {
        var v = leaf.Value;
        gen = Show(v);
        got = live is ReflectedValue r ? "bytes " + Convert.ToHexString(r.Bytes) : Show(live);
        switch (v)
        {
            case bool b: return live is bool lb && lb == b;
            case float f: return live is double d && f.Equals((float)d);
            case double d2: return live is double ld && d2.Equals(ld);
            case Ecs.ObjectReference o: { Span<byte> s = stackalloc byte[16]; o.CopyTo(s); gen = "bytes " + Convert.ToHexString(s); return live is ReflectedValue rv && rv.Bytes.AsSpan().SequenceEqual(s); }
            case Ecs.GameGuid g: { Span<byte> s = stackalloc byte[16]; g.CopyTo(s); gen = "bytes " + Convert.ToHexString(s); return live is ReflectedValue rv && rv.Bytes.AsSpan().SequenceEqual(s); }
        }
        // integers, enums and bitmasks: the same bits, within the field's size
        ulong mask = leaf.Size >= 8 ? ulong.MaxValue : (1UL << (leaf.Size * 8)) - 1;
        ulong a = Bits(v), bits;
        switch (live)
        {
            case long l: bits = unchecked((ulong)l); break;
            case ulong u: bits = u; break;
            default: return false;
        }
        return (a & mask) == (bits & mask);
    }

    static ulong Bits(object v) => v switch
    {
        Enum e => Bits(Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()))),
        byte x => x, sbyte x => unchecked((ulong)x), ushort x => x, short x => unchecked((ulong)x),
        uint x => x, int x => unchecked((ulong)x), ulong x => x, long x => unchecked((ulong)x),
        char x => x,
        _ => throw new NotSupportedException(v.GetType().Name),
    };

    static string Show(object? v) => v switch
    {
        null => "null",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        Enum e => $"{e} ({Bits(e)})",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
    };
}
