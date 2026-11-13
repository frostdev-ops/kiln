// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln's managed assemblies loaded fresh into a context of their own and started the way the host starts them
/// (a backend, then the first service tick with ServiceTick.AutoStart): what a game process sees at its first tick, with
/// statics nobody touched yet and no call into a service's rows. The test process's own copies are not involved.</summary>
internal sealed class IsolatedKiln
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private sealed class Context(string dir) : AssemblyLoadContext("kiln-isolated-start", isCollectible: false)
    {
        // Kiln's assemblies (and this test assembly, for its backend) fresh from the test's folder; the runtime and the
        // test frameworks from the default context
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not { } n || !n.StartsWith("Kiln.", StringComparison.Ordinal)) return null;
            var file = Path.Combine(dir, n + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }

    private readonly Context _alc = new(AppContext.BaseDirectory);
    private readonly Assembly _api;
    private ulong _step;

    /// <summary>The isolated copy of every adopted service assembly, by name.</summary>
    public readonly Dictionary<string, Assembly> Services = new(StringComparer.Ordinal);

    /// <param name="autostart">The assemblies whose ServiceTick.AutoStart entries run (the others are skipped, so a
    /// test does not start file watchers or tools of services it does not look at).</param>
    public IsolatedKiln(IReadOnlyCollection<string> autostart)
    {
        _api = _alc.LoadFromAssemblyName(new AssemblyName("Kiln.Api"));
        var self = _alc.LoadFromAssemblyName(typeof(IsolatedKiln).Assembly.GetName());
        var backend = Activator.CreateInstance(self.GetType(typeof(OfflineBackend).FullName!, throwOnError: true)!, nonPublic: true);
        Type("Kiln.Backend").GetProperty("Current", All)!.SetValue(null, backend);
        var entries = ((string Assembly, string Type)[])Type("Kiln.ServiceTick").GetField("AutoStart", All)!.GetValue(null)!;
        for (var i = 0; i < entries.Length; i++)
            if (!autostart.Contains(entries[i].Assembly)) entries[i] = ("", "");  // this copy's list only
        foreach (var name in autostart) Services[name] = _alc.LoadFromAssemblyName(new AssemblyName(name));
    }

    public Type Type(string name) => _api.GetType(name, throwOnError: true)!;

    /// <summary>The AutoStart list as Kiln.Api has it (read from a fresh copy, before any test changed it).</summary>
    public static (string Assembly, string Type)[] AutoStart() =>
        ((string Assembly, string Type)[])typeof(ServiceTick).GetField("AutoStart", All)!.GetValue(null)!;

    /// <summary>One service tick, as the host drives it (the first one starts the services).</summary>
    public void Step()
    {
        _step++;
        var tick = Type("Kiln.HostAccess").GetMethod("Tick", All)!.Invoke(null, [_step, (uint)_step * 2, 2u, 0L, 40.8]);
        Type("Kiln.ServiceTick").GetMethod("Run", All)!.Invoke(null, [tick]);
    }

    /// <summary>The row of <paramref name="name"/>: state and reason as their enum names, and the reason text.</summary>
    public (string State, string Reason, string Text) Row(string name)
    {
        var row = Type("Kiln.Capabilities").GetMethod("Get", All)!.Invoke(null, [name])!;
        string P(string p) => row.GetType().GetProperty(p)!.GetValue(row)!.ToString()!;
        return (P("State"), P("Reason"), P("ReasonText"));
    }

    /// <summary>The names of the service tick's handlers now.</summary>
    public IReadOnlyList<string> TickHandlers()
    {
        var entries = (Array)Type("Kiln.ServiceTick").GetField("_entries", All)!.GetValue(null)!;
        return [.. entries.Cast<object>().Select(e => (string)e.GetType().GetField("Name", All)!.GetValue(e)!)];
    }

    /// <summary>Every capability name a [RequiresCapability] of <paramref name="assembly"/> names.</summary>
    public static IEnumerable<string> RequiredNames(Assembly assembly)
    {
        static IEnumerable<CustomAttributeData> Of(MemberInfo m) =>
            m.CustomAttributes.Where(a => a.AttributeType.FullName == "Kiln.RequiresCapabilityAttribute");
        foreach (var t in assembly.GetTypes())
        {
            foreach (var a in Of(t)) yield return (string)a.ConstructorArguments[0].Value!;
            foreach (var m in t.GetMembers(All | BindingFlags.DeclaredOnly))
                foreach (var a in Of(m)) yield return (string)a.ConstructorArguments[0].Value!;
        }
    }
}

/// <summary>A service's capability rows are there from its start, the first service tick, with no call made: what a mod
/// that checks <c>Capabilities.Is</c> before it calls depends on. Proven on fresh copies of the assemblies (no
/// reflection into the rows, no earlier test's state).</summary>
public sealed class ServiceStartTests
{
    private static readonly string[] AdoptedNames = [.. Adopted.Assemblies.Select(a => a.GetName().Name!)];

    /// <summary>Services whose rows only lower the sigdb features of their missing table: they start at their first use
    /// (the core's kiln.&lt;service&gt; row says the table is missing from the start), and every assembly the first tick
    /// loads costs every start (a game's, a test kit harness's).</summary>
    internal static readonly Dictionary<string, string> LowersOnly = new(StringComparer.Ordinal)
    {
        ["Kiln.Build"] = "build.* lowered when kiln.build is missing",
        ["Kiln.Creatures"] = "creatures.* lowered when kiln.creatures is missing",
        ["Kiln.Hooks"] = "kiln.hooks probed again after a registry reset",
        ["Kiln.Example"] = "the template: only the core's kiln.example row",
    };

    /// <summary>Every adopted service started once, as in a game process at its first tick (shared: nothing changes it).</summary>
    internal static readonly Lazy<IsolatedKiln> Started = new(() =>
    {
        var kiln = new IsolatedKiln(AdoptedNames);
        kiln.Step();
        return kiln;
    });

    [Fact]
    public void Every_adopted_service_with_rows_of_its_own_starts_at_the_first_service_tick()
    {
        var entries = IsolatedKiln.AutoStart();
        var missing = new List<string>();
        foreach (var assembly in Adopted.Assemblies)
        {
            var holdsRows = assembly.GetTypes().Any(t =>
                t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Any(f => f.FieldType == typeof(ServiceCapRows)));
            var name = assembly.GetName().Name!;
            if (LowersOnly.ContainsKey(name)) continue;
            if (holdsRows && !entries.Any(e => e.Assembly == name && assembly.GetType(e.Type) is not null)) missing.Add(name);
        }
        Assert.True(missing.Count == 0, "rows of their own but no startup type in ServiceTick.AutoStart: " + string.Join(", ", missing));
    }

    [Fact]
    public void After_the_first_tick_every_required_capability_has_a_row_that_says_why_it_is_off()
    {
        var kiln = Started.Value;
        var bad = new List<string>();
        foreach (var (name, assembly) in kiln.Services)
            if (!LowersOnly.ContainsKey(name))
            foreach (var cap in IsolatedKiln.RequiredNames(assembly).Distinct())
            {
                var (state, reason, text) = kiln.Row(cap);
                if (reason == "Unknown") bad.Add($"{name}: {cap} has no row after the first tick");
                else if (state == "Available") bad.Add($"{name}: {cap} is available with no game");
                else if (text.Length == 0) bad.Add($"{name}: {cap} is off without a reason text");
            }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Theory]
    [InlineData("gameplay.entity_events", "needs kiln.gameplay")]
    [InlineData("gameplay.spawn_replace", "needs kiln.gameplay")]
    [InlineData("content.references", "needs kiln.content")]
    [InlineData("content.ready", "needs kiln.content")]
    [InlineData("assets.textures", "needs kiln.assets")]
    [InlineData("settings.ready", "needs kiln.settings")]
    [InlineData("spawn.components", "needs kiln.spawn")]
    [InlineData("skills.functions", "needs kiln.skills")]
    [InlineData("chat.send", "needs kiln.chat")]
    [InlineData("quests.journal", "needs kiln.knowledge")]
    [InlineData("ui.screens", "needs kiln.ui")]
    public void A_row_a_service_reports_itself_is_there_after_the_first_tick(string row, string because)
    {
        var (state, reason, text) = Started.Value.Row(row);
        Assert.Equal("Unavailable", state);
        Assert.Equal("Offline", reason);
        Assert.StartsWith(because, text);
    }

    [Fact]
    public void Content_keeps_its_session_row_in_step_on_the_service_tick()
    {
        Assert.Contains("kiln.content capabilities", Started.Value.TickHandlers());
    }
}
