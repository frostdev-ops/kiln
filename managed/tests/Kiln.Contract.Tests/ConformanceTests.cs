// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Rules every adopted service follows (docs/kiln/modding/contract.md), checked over its whole public surface
/// by reflection, so a member added later cannot miss them silently.</summary>
public sealed class ConformanceTests : ContractTest
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static bool IsVisible(Type t)
    {
        for (var x = t; x is not null; x = x.DeclaringType)
            if (!(x.IsPublic || x.IsNestedPublic || x.IsNestedFamily || x.IsNestedFamORAssem)) return false;
        return true;
    }

    private static IEnumerable<Type> PublicTypes(Assembly a) => a.GetTypes().Where(IsVisible);

    private static IEnumerable<MemberInfo> Marked(Assembly a)
    {
        foreach (var t in a.GetTypes())
        {
            yield return t;
            foreach (var m in t.GetMembers(Declared)) yield return m;
        }
    }

    [Theory]
    [MemberData(nameof(Adopted.Names), MemberType = typeof(Adopted))]
    public void A_result_type_with_Ok_implements_IKilnResult(string service)
    {
        var missing = PublicTypes(Adopted.Named(service))
            .Where(t => t.Name.EndsWith("Result", StringComparison.Ordinal) && !t.IsEnum && !t.IsInterface)
            .Where(t => t.GetProperty("Ok", BindingFlags.Public | BindingFlags.Instance) is { } ok && ok.PropertyType == typeof(bool))
            .Where(t => !typeof(IKilnResult).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();
        Assert.True(missing.Length == 0, "result types with Ok that do not implement IKilnResult: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Adopted.Names), MemberType = typeof(Adopted))]
    public void Side_and_thread_attributes_do_not_contradict(string service)
    {
        var bad = new List<string>();
        foreach (var m in Marked(Adopted.Named(service)))
        {
            bool Has<T>() where T : Attribute => m.GetCustomAttribute<T>(false) is not null;
            if (Has<ServerOnlyAttribute>() && Has<ClientOnlyAttribute>()) bad.Add($"{Name(m)}: server only and client only");
            if (Has<TickThreadOnlyAttribute>() && (Has<AnyThreadAttribute>() || Has<RenderThreadAttribute>())) bad.Add($"{Name(m)}: tick thread only and another thread");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Theory]
    [MemberData(nameof(Adopted.Names), MemberType = typeof(Adopted))]
    public void A_required_capability_has_a_valid_name_and_a_row_that_says_why_it_is_off(string service)
    {
        var assembly = Adopted.Named(service);
        StartRows(assembly);
        var bad = new List<string>();
        foreach (var m in Marked(assembly))
            foreach (var a in m.GetCustomAttributes<RequiresCapabilityAttribute>(false))
            {
                if (!Capabilities.ValidName(a.Name)) bad.Add($"{Name(m)}: '{a.Name}' is not a capability name");
                // No game here: the service's start has to leave a row for every name it marks a member with, so that
                // Capabilities.Why answers with a reason and not with "no capability of this name is registered".
                var row = Capabilities.Get(a.Name);
                if (row.State == CapabilityState.Available) bad.Add($"{Name(m)}: {a.Name} is available with no game");
                else if (row.Reason == CapabilityReason.Unknown) bad.Add($"{Name(m)}: {a.Name} has no row after the service's start");
                else if (row.ReasonText.Length == 0) bad.Add($"{Name(m)}: {a.Name} is off without a reason text");
            }
        Assert.True(bad.Count == 0, string.Join("; ", bad.Distinct()));
    }

    /// <summary>The analyzers' table lists the public members that reach a tick-thread check in the code (ApiScan). Each
    /// of them carries [TickThreadOnly] itself, on its property or event, or on its type.</summary>
    [Theory]
    [MemberData(nameof(Adopted.Names), MemberType = typeof(Adopted))]
    public void What_the_code_requires_the_tick_thread_for_is_marked_TickThreadOnly(string service)
    {
        var assembly = Adopted.Named(service);
        var marked = new HashSet<string>(StringComparer.Ordinal);
        var typesMarked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in assembly.GetTypes())
        {
            if (t.GetCustomAttribute<TickThreadOnlyAttribute>(false) is not null) typesMarked.Add(TypeId(t));
            foreach (var m in t.GetMembers(Declared))
            {
                if (m.GetCustomAttribute<TickThreadOnlyAttribute>(false) is null) continue;
                switch (m)
                {
                    case PropertyInfo p:
                        if (p.GetMethod is { } g) marked.Add(Key(g));
                        if (p.SetMethod is { } s) marked.Add(Key(s));
                        break;
                    case EventInfo e:
                        if (e.AddMethod is { } add) marked.Add(Key(add));
                        if (e.RemoveMethod is { } remove) marked.Add(Key(remove));
                        break;
                    case MethodBase mb:
                        marked.Add(Key(mb));
                        break;
                }
            }
        }
        var own = new HashSet<string>(assembly.GetTypes().Select(TypeId), StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in TickTable())
        {
            var (type, member) = Split(id);
            if (!own.Contains(type) || typesMarked.Contains(type)) continue;
            if (!marked.Contains(type + "." + member)) missing.Add(id);
        }
        Assert.True(missing.Count == 0, $"{missing.Count} member(s) reach a tick-thread check without [TickThreadOnly]: " + string.Join("; ", missing.Take(12)));
    }

    // "M:Kiln.Spawn.Destroy(Kiln.EntityId)" -> ("Kiln.Spawn", "Destroy/1"): the type and the member with its arity.
    // Overloads with the same number of parameters share a key, which is exact enough: the tool marks each of them.
    private static (string Type, string Member) Split(string id)
    {
        var body = id[2..];
        var paren = body.IndexOf('(');
        var head = paren < 0 ? body : body[..paren];
        var arity = paren < 0 ? 0 : Arity(body[(paren + 1)..^1]);
        var dot = head.LastIndexOf('.');
        var name = head[(dot + 1)..];
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return (head[..dot], $"{name}/{arity}");
    }

    private static int Arity(string parameters)
    {
        if (parameters.Length == 0) return 0;
        var depth = 0;
        var n = 1;
        foreach (var c in parameters)
        {
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ',' && depth == 0) n++;
        }
        return n;
    }

    private static string TypeId(Type t) => (t.FullName ?? t.Name).Replace('+', '.');

    private static string Key(MethodBase m) => $"{TypeId(m.DeclaringType!)}.{(m.IsConstructor ? "#ctor" : m.Name)}/{m.GetParameters().Length}";

    private static string Name(MemberInfo m) => m is Type t ? t.Name : $"{m.DeclaringType?.Name}.{m.Name}";

    private static IEnumerable<string> TickTable()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "KilnApis.g.cs"));
        var start = text.IndexOf("string[] TickThread", StringComparison.Ordinal);
        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "KilnApis.g.cs has no TickThread table");
        foreach (Match m in Regex.Matches(text[start..end], "^\\s*\"([^\"]+)\"", RegexOptions.Multiline))
            yield return m.Groups[1].Value;
    }

    [Fact]
    public void The_tick_table_is_read()
    {
        var ids = TickTable().ToArray();
        Assert.True(ids.Length > 500, $"{ids.Length} ids");
        Assert.Contains("M:Kiln.Spawn.Destroy(Kiln.EntityId)", ids);
        Assert.Equal(("Kiln.Spawn", "Destroy/1"), Split("M:Kiln.Spawn.Destroy(Kiln.EntityId)"));
        Assert.Equal(("Kiln.Spawn", "Find/4"), Split("M:Kiln.Spawn.Find(System.Guid,System.Nullable{Kiln.WorldPosition},System.Double,System.Int32)"));
        Assert.Equal(("Kiln.Spawn", "get_EntityCount/0"), Split("M:Kiln.Spawn.get_EntityCount"));
    }
}
