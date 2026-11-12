// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Kiln.Testing;

/// <summary>One discovered <see cref="ClientCheckAttribute"/> method.</summary>
internal sealed record CheckCase(
    string Mod, string ModName, string ModVersion, string Id, string Title, string Area, string? Since,
    IReadOnlyList<string> Tags, ClientCheckNeeds Needs, int Order, string? Confirm, MethodInfo? Method, string? Problem)
{
    public string Key => Mod + "/" + Id;
    public bool Has(string tag) => Tags.Contains(tag, StringComparer.Ordinal);
    public bool Cheats => Has(ClientCheckTags.Cheats);
    public bool GuardProof => Has(ClientCheckTags.GuardProof);
}

/// <summary>The mods that registered checks (<see cref="ClientChecks.Register"/>), and the discovery of their methods.</summary>
internal static partial class CheckRegistry
{
    internal sealed record Registered(Mod Instance, ModContext Context, IReadOnlyList<CheckCase> Cases, long Order);

    private static readonly object Lock = new();
    private static Registered[] _mods = [];
    private static long _order;

    /// <summary>Raised (on the registering thread) after a mod's checks were added or removed.</summary>
    internal static event Action? Changed;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    public static IReadOnlyList<Registered> Mods
    {
        get
        {
            lock (Lock) return _mods;
        }
    }

    /// <summary>Every registered check in run order: <see cref="ClientCheckAttribute.Order"/>, then registration (load)
    /// order, then class and declaration order.</summary>
    public static IReadOnlyList<CheckCase> All => Mods.SelectMany(m => m.Cases.Select((c, i) => (c, m.Order, i)))
        .OrderBy(x => x.c.Order).ThenBy(x => x.Order).ThenBy(x => x.i).Select(x => x.c).ToList();

    public static (Registered Mod, CheckCase Case)? Find(string mod, string id)
    {
        foreach (var m in Mods)
            if (m.Context.Info.Id == mod)
                foreach (var c in m.Cases)
                    if (c.Id == id) return (m, c);
        return null;
    }

    public static int Register(Mod mod)
    {
        var context = mod.Context;
        lock (Lock)
        {
            if (_mods.Any(m => ReferenceEquals(m.Instance, mod))) return _mods.First(m => ReferenceEquals(m.Instance, mod)).Cases.Count;
        }
        var cases = Discover(AssembliesOf(mod), context.Info.Id, context.Info.Name, context.Info.Version);
        var entry = new Registered(mod, context, cases, Interlocked.Increment(ref _order));
        lock (Lock)
        {
            // a reload registers a new instance under the same id: the old one is gone
            _mods = [.. _mods.Where(m => m.Context.Info.Id != context.Info.Id || m.Context.Alive), entry];
        }
        context.Retiring += () => Remove(entry);
        if (!context.Alive) Remove(entry);
        Changed?.Invoke();
        return cases.Count;
    }

    private static void Remove(Registered entry)
    {
        lock (Lock) _mods = _mods.Where(m => !ReferenceEquals(m, entry)).ToArray();
        Changed?.Invoke();
    }

    /// <summary>For tests: forget every registration.</summary>
    internal static void Reset()
    {
        lock (Lock) _mods = [];
    }

    /// <summary>The mod's own assemblies: those of its load context, or only the one of its <see cref="Mod"/> class when
    /// it was not loaded in a collectible context of its own (tests).</summary>
    private static IEnumerable<Assembly> AssembliesOf(Mod mod)
    {
        var own = mod.GetType().Assembly;
        var alc = AssemblyLoadContext.GetLoadContext(own);
        return alc is { IsCollectible: true } ? alc.Assemblies.ToArray() : [own];
    }

    public static List<CheckCase> Discover(IEnumerable<Assembly> assemblies, string mod, string modName, string version)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
        var found = new List<(CheckCase Case, string Type, int Token)>();
        foreach (var asm in assemblies)
        {
            Type?[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (var t in types)
            {
                if (t is null) continue;
                foreach (var m in t.GetMethods(flags))
                {
                    ClientCheckAttribute? a;
                    try
                    {
                        a = m.GetCustomAttribute<ClientCheckAttribute>();
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (a is null) continue;
                    var tags = (a.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                    var area = string.IsNullOrWhiteSpace(a.Area) ? modName : a.Area.Trim();
                    var c = new CheckCase(mod, modName, version, a.Id ?? "", string.IsNullOrWhiteSpace(a.Title) ? a.Id ?? "" : a.Title,
                        area, a.Since, tags, a.Needs, a.Order, string.IsNullOrWhiteSpace(a.Confirm) ? null : a.Confirm, m, ShapeProblem(t, m, a));
                    found.Add((c, t.FullName ?? t.Name, m.MetadataToken));
                }
            }
        }
        var list = found.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Token).Select(x => x.Case).ToList();
        // a repeated id: every copy after the first is refused (the ledger keys entries by mod and id)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
            if (!seen.Add(list[i].Id) && list[i].Problem is null)
                list[i] = list[i] with { Problem = $"the id {list[i].Id} is used by another check of this mod" };
        return list;
    }

    private static string? ShapeProblem(Type t, MethodInfo m, ClientCheckAttribute a)
    {
        if (a.Id is null || !IdPattern().IsMatch(a.Id))
            return "the id must be lower-case letters, digits, '.', '_' or '-', at most 64 characters";
        if (!Enum.IsDefined(a.Needs)) return $"unknown Needs value {(int)a.Needs}";
        if (m.IsGenericMethodDefinition || t.ContainsGenericParameters) return "a check cannot be generic";
        var ps = m.GetParameters();
        if (ps.Length != 1 || ps[0].ParameterType != typeof(CheckContext)) return "a check takes one CheckContext";
        if (m.ReturnType != typeof(Task) && m.ReturnType != typeof(ValueTask)) return "a check returns Task or ValueTask";
        if (!m.IsStatic && t.IsAbstract) return "an instance check needs a concrete class";
        if (!m.IsStatic && !t.IsSubclassOf(typeof(Mod)) && t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is null)
            return "an instance check outside the Mod class needs a parameterless constructor";
        return null;
    }
}
