// SPDX-License-Identifier: MIT
// The nine dev.24 types of Kiln.Game (namespace Kiln.GameTypes), checked by reflection over the built Kiln.Game.dll
// against a table derived from Generated.cs and Components.cs as dev.24 shipped them (Fixtures/legacy/golden.json).
// Mods built against dev.24 bind to these members by name and signature, so each of them must stay.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Kiln.Game.Tests;

public class LegacyGoldenTests
{
    static readonly JsonElement Golden = GenRepo.ReadJson(GenRepo.Fixture("legacy", "golden.json"));

    static Type Legacy(string name) =>
        GenRepo.GameAssembly.GetType("Kiln.GameTypes." + name) ?? throw new Xunit.Sdk.XunitException("missing type Kiln.GameTypes." + name);

    public static TheoryData<string> StructNames()
    {
        var d = new TheoryData<string>();
        foreach (var s in Golden.GetProperty("structs").EnumerateArray()) d.Add(s.GetProperty("name").GetString()!);
        return d;
    }

    static JsonElement GoldenStruct(string name) =>
        Golden.GetProperty("structs").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    /// <summary>A field's type as the golden table writes it: the C# keyword of a primitive, or "fixed byte[N]".</summary>
    internal static string FieldTypeText(FieldInfo f)
    {
        var fb = f.GetCustomAttribute<FixedBufferAttribute>();
        if (fb != null) return $"fixed {Keyword(fb.ElementType)}[{fb.Length}]";
        return Keyword(f.FieldType);
    }

    internal static string Keyword(Type t) => t == typeof(bool) ? "bool" : t == typeof(byte) ? "byte" : t == typeof(sbyte) ? "sbyte"
        : t == typeof(ushort) ? "ushort" : t == typeof(short) ? "short" : t == typeof(uint) ? "uint" : t == typeof(int) ? "int"
        : t == typeof(ulong) ? "ulong" : t == typeof(long) ? "long" : t == typeof(float) ? "float" : t == typeof(double) ? "double"
        : t.FullName ?? t.Name;

    [Fact]
    public void The_golden_table_holds_the_nine_dev24_types()
    {
        Assert.Equal(9, Golden.GetProperty("structs").GetArrayLength());
        Assert.Equal(43, Golden.GetProperty("structs").EnumerateArray().Sum(s => s.GetProperty("fields").GetArrayLength()));
        Assert.Equal(9, Golden.GetProperty("componentTypes").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(StructNames))]
    public void Each_dev24_struct_keeps_its_size_fields_and_constants(string name)
    {
        var g = GoldenStruct(name);
        var t = Legacy(name);
        Assert.True(t.IsPublic && t.IsValueType && !t.IsGenericType, name + " is a public non-generic struct");
        Assert.Equal(LayoutKind.Explicit, t.StructLayoutAttribute!.Value);
        int size = g.GetProperty("size").GetInt32();
        Assert.Equal(size, t.StructLayoutAttribute.Size);
        Assert.Equal(size, SizeOf(t));

        // public const uint Hash, public const string Component (compile-time constants, as dev.24 shipped them)
        var hash = t.GetField("Hash", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(hash);
        Assert.True(hash!.IsLiteral && hash.FieldType == typeof(uint), name + ".Hash is a const uint");
        Assert.Equal(Convert.ToUInt32(g.GetProperty("hash").GetString(), 16), (uint)hash.GetRawConstantValue()!);
        var comp = t.GetField("Component", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(comp);
        Assert.True(comp!.IsLiteral && comp.FieldType == typeof(string), name + ".Component is a const string");
        Assert.Equal(g.GetProperty("component").GetString(), (string)comp.GetRawConstantValue()!);

        // Instance fields: exactly the golden ones, each at its offset with its type; a fixed buffer keeps its
        // compiler-made nested type (what a mod's IL names) and its length.
        var expected = g.GetProperty("fields").EnumerateArray()
            .Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("offset").GetInt32(), f.GetProperty("type").GetString()!))
            .ToList();
        var actual = t.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => (f.Name, f.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1, FieldTypeText(f)))
            .OrderBy(f => f.Item2).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(expected.OrderBy(f => f.Item2).ThenBy(f => f.Item1, StringComparer.Ordinal), actual);
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.GetCustomAttribute<FixedBufferAttribute>() is not { } fb) continue;
            Assert.Equal($"<{f.Name}>e__FixedBuffer", f.FieldType.Name);
            Assert.Equal(t, f.FieldType.DeclaringType);
            Assert.Equal(fb.Length, SizeOf(f.FieldType));
            Assert.Equal(typeof(byte), f.FieldType.GetField("FixedElementField")?.FieldType);
        }
    }

    [Fact]
    public void ComponentTypes_keeps_its_nine_constants()
    {
        var t = Legacy("ComponentTypes");
        Assert.True(t.IsAbstract && t.IsSealed, "ComponentTypes is a static class");
        var actual = t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).ToDictionary(f => f.Name, f => (uint)f.GetRawConstantValue()!);
        var expected = Golden.GetProperty("componentTypes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => Convert.ToUInt32(c.GetProperty("value").GetString(), 16));
        Assert.Equal(expected.OrderBy(k => k.Key), actual.OrderBy(k => k.Key));
    }

    [Fact]
    public void Components_keeps_every_method_with_its_exact_signature()
    {
        var t = Legacy("Components");
        Assert.True(t.IsAbstract && t.IsSealed, "Components is a static class");
        var actual = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(Signature).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var expected = Golden.GetProperty("components").EnumerateArray().Select(e => e.GetString()!)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
        Assert.NotNull(t.GetProperty("Available", BindingFlags.Public | BindingFlags.Static));
    }

    internal static int SizeOf(Type t) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;

    /// <summary>"static ReturnType Name&lt;T&gt;(Type name, out Type name) where T : unmanaged".</summary>
    internal static string Signature(MethodInfo m)
    {
        string Name(Type t)
        {
            if (t.IsByRef) return Name(t.GetElementType()!);
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType)
            {
                var d = t.GetGenericTypeDefinition().FullName!;
                return d[..d.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
            }
            return t.FullName!;
        }
        string Param(ParameterInfo p) => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + Name(p.ParameterType) + " " + p.Name;
        var gen = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
        var where = string.Concat(m.GetGenericArguments().Select(a =>
        {
            var c = new List<string>();
            bool unmanaged = a.CustomAttributes.Any(x => x.AttributeType.Name == "IsUnmanagedAttribute");
            if (unmanaged) c.Add("unmanaged");
            else if (a.GenericParameterAttributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) c.Add("struct");
            c.AddRange(a.GetGenericParameterConstraints().Where(x => x != typeof(ValueType)).Select(Name));
            return c.Count == 0 ? "" : $" where {a.Name} : {string.Join(", ", c)}";
        }));
        return $"{(m.IsStatic ? "static " : "")}{Name(m.ReturnType)} {m.Name}{gen}({string.Join(", ", m.GetParameters().Select(Param))}){where}";
    }
}
