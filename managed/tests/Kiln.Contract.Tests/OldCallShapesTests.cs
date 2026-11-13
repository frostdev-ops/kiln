// SPDX-License-Identifier: MIT
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>The API is additive in source too: code that compiled against dev.26 compiles now. A new overload in an old
/// method group can break that without the binary gate noticing (<c>var d = Spawn.Destroy;</c> loses its delegate type,
/// <c>Chat.Tell(default, "hi")</c> turns ambiguous), so an identity entry point gets a name of its own
/// (docs/kiln/modding/contract.md, "Additive in source").</summary>
public sealed class OldCallShapesTests
{
    /// <summary>Call shapes of dev.26 that a new overload would break. Add the shapes of a method group before giving
    /// it an overload.</summary>
    private const string Legacy = """
        using System;
        using System.Threading.Tasks;
        using Kiln;

        static class WrittenForDev26
        {
            static void Shapes(Player player, ChatPlayer chatter, ContentEntry entry, CreatureRef creature)
            {
                // a one-overload group has a natural delegate type
                var destroy = Spawn.Destroy;
                Func<EntityId, bool> destroy2 = Spawn.Destroy;
                var tell = Chat.Tell;
                var broadcast = Chat.Broadcast;
                // default and null arguments that bound to exactly one overload
                Chat.Tell(default, "hello");
                Chat.Say(default, "hello");
                Spawn.Destroy(default);
                Permissions.Has(null!, "node");
                PermissionSubject.ForPlayer(null!);
                bool alive = Spawn.Exists(default);
                Task<EntityId> spawned = Spawn.Entity("Enemy_Vukah_Melee", default);
                var reset = entry.Reset;
                var revert = Content.Revert;
                var level = creature.Level;
                creature.Level = level;
                var creatureReset = creature.Reset;
            }
        }
        """;

    private static readonly Lazy<MetadataReference[]> References = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && File.Exists(p)).Select(p => MetadataReference.CreateFromFile(p))]);

    private static IReadOnlyList<string> Errors(string source)
    {
        var compilation = CSharpCompilation.Create("OldCallShapes",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())];
    }

    [Fact]
    public void Code_written_for_dev26_still_compiles()
    {
        var errors = Errors(Legacy);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void The_compile_check_sees_an_overload_break()
    {
        // the check itself: a group with two overloads has no natural delegate type, and default is ambiguous
        var errors = Errors("""
            static class Two { public static void M(int a) { } public static void M(string a) { } }
            static class Use { static void U() { var m = Two.M; Two.M(default); } }
            """);
        Assert.Contains(errors, e => e.Contains("CS8917", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("CS0121", StringComparison.Ordinal));
    }

    /// <summary>The released method groups (PublicAPI.Shipped.txt) of the adopted services, with their overload count.</summary>
    private static Dictionary<(string Type, string Method), int> ShippedGroups(string assembly)
    {
        var groups = new Dictionary<(string, string), int>();
        var file = Path.Combine(AppContext.BaseDirectory, "shipped", assembly, "PublicAPI.Shipped.txt");
        if (!File.Exists(file)) return groups;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            foreach (var prefix in (string[])["static ", "virtual ", "abstract ", "override ", "const ", "readonly "])
                if (line.StartsWith(prefix, StringComparison.Ordinal)) line = line[prefix.Length..];
            var paren = line.IndexOf('(');
            if (paren < 0 || line.StartsWith('#') || line.Contains("operator", StringComparison.Ordinal)) continue;
            var head = line[..paren];
            if (head.IndexOf('<') is var lt and > 0) head = head[..lt];  // Of<T>: one more overload of Of
            var dot = head.LastIndexOf('.');
            if (dot < 0) continue;
            var (type, method) = (head[..dot], head[(dot + 1)..]);
            if (type.EndsWith("." + method, StringComparison.Ordinal) || method is "Deconstruct" or "Equals" or "GetHashCode" or "ToString" or "<Clone>$" or "PrintMembers")
                continue;  // constructors and what records generate
            groups[(type, method)] = groups.GetValueOrDefault((type, method)) + 1;
        }
        return groups;
    }

    private static Type? Resolve(Assembly assembly, string name)
    {
        // PublicAPI files write nested types with dots: Kiln.Ui.Screens is Kiln.Ui+Screens
        for (var candidate = name; ;)
        {
            if (assembly.GetType(candidate) is { } t) return t;
            var dot = candidate.LastIndexOf('.');
            if (dot <= "Kiln".Length) return null;
            candidate = candidate[..dot] + "+" + candidate[(dot + 1)..];
        }
    }

    /// <summary>Groups that took a new overload which no dev.26 call can meet, and why.</summary>
    private static readonly Dictionary<string, string> Accepted = new(StringComparer.Ordinal)
    {
        // nine overloads in dev.26: `var d = Blocks.Define;` and `Blocks.Define(null)` were ambiguous already; the two
        // new ones take AffixBlock and AffixPoolBlock, types new in dev.27 (affix-blocks, the owner's decision)
        ["Kiln.Blocks.Define"] = "the two typed affix overloads of dev.27",
    };

    [Theory]
    [MemberData(nameof(Adopted.Names), MemberType = typeof(Adopted))]
    public void A_method_group_released_in_dev26_gains_no_overload(string service)
    {
        var assembly = Adopted.Named(service);
        var grown = new List<string>();
        foreach (var ((typeName, method), shipped) in ShippedGroups(service))
        {
            var type = Resolve(assembly, typeName);
            if (type is null) continue;  // a type forwarded elsewhere (ItemId): its own assembly is checked
            var now = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Count(m => m.Name == method && !m.IsSpecialName);
            if (now > shipped && !Accepted.ContainsKey($"{typeName}.{method}"))
                grown.Add($"{typeName}.{method}: {shipped} overload(s) in dev.26, {now} now");
        }
        Assert.True(grown.Count == 0, "give the new shape a name of its own: " + string.Join("; ", grown));
    }
}
