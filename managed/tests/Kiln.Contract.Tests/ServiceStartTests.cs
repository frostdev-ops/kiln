// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>A sigdb feature a service's start has handled (ServiceCaps.IsServedFeature in this copy).</summary>
    public bool ServedFeature(string name) =>
        (bool)Type("Kiln.ServiceCaps").GetMethod("IsServedFeature", All)!.Invoke(null, [name])!;

    /// <summary>Serves <paramref name="table"/> as the native service <paramref name="name"/> to this copy.</summary>
    public void Serve(string name, nint table)
    {
        var backend = Type("Kiln.Backend").GetProperty("Current", All)!.GetValue(null)!;
        ((Dictionary<string, nint>)backend.GetType().GetField("Tables", All)!.GetValue(backend)!)[name] = table;
    }

    /// <summary>Subscribes a handler that does nothing to the event type <paramref name="eventType"/> (a type of this copy)
    /// through this copy's <c>Events.On</c>.</summary>
    public void Subscribe(Type eventType)
    {
        var handler = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(eventType),
            typeof(IsolatedKiln).GetMethod(nameof(Ignore), All)!.MakeGenericMethod(eventType));
        var on = Type("Kiln.Events").GetMethods(All).Single(m => m.Name == "On" && m.GetParameters() is { Length: 3 } p &&
                                                                 p[1].ParameterType.Name == "EventPriority");
        var g = on.MakeGenericMethod(eventType);
        g.Invoke(null, [handler, .. g.GetParameters().Skip(1).Select(p => p.DefaultValue)]);
    }

    private static void Ignore<T>(T e)
    {
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
        ["Kiln.Saves"] = "starts at its first use: its start subscribes to kiln.saves' pump; saves.* lowered and " +
                         "InstanceData's rows reported then (the core's kiln.saves row says the table is missing from the start)",
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
            foreach (var cap in IsolatedKiln.RequiredNames(assembly).Distinct().Where(c => !ConformanceTests.RowsOfOthers.ContainsKey(c)))
            {
                var (state, reason, text) = kiln.Row(cap);
                if (reason == "Unknown")
                {
                    if (!kiln.ServedFeature(cap)) bad.Add($"{name}: {cap} has no row after the first tick");
                }
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
    [InlineData("combat.attributes", "needs kiln.combat")]
    [InlineData("combat.hold_filter", "needs kiln.combat")]
    [InlineData("abilities.buffs", "kiln.abilities is unavailable")]
    [InlineData("time.speed", "needs kiln.time")]
    public void A_row_a_service_reports_itself_is_there_after_the_first_tick(string row, string because)
    {
        var (state, reason, text) = Started.Value.Row(row);
        Assert.Equal("Unavailable", state);
        Assert.Equal("Offline", reason);
        Assert.StartsWith(because, text);
    }

    /// <summary>The sigdb features a service serves are handled at its start: with no Kiln core they have no row (the
    /// core reports features), and in a game a feature the core claims is lowered while its service is missing.</summary>
    [Theory]
    [InlineData("chat.send")]
    [InlineData("quests.journal")]
    [InlineData("ui.screens")]
    [InlineData("combat.hits")]
    [InlineData("abilities.natives")]
    [InlineData("gameplay.craft")]
    [InlineData("content.items")]
    [InlineData("skills.functions")]
    public void A_feature_a_service_serves_is_handled_at_its_start(string feature)
    {
        Assert.True(Started.Value.ServedFeature(feature));
        Assert.Equal("Unknown", Started.Value.Row(feature).Reason);
    }

    [Fact]
    public void Content_keeps_its_session_row_in_step_on_the_service_tick()
    {
        Assert.Contains("kiln.content capabilities", Started.Value.TickHandlers());
    }
}

/// <summary>Kiln.Gameplay starts at the first service tick for its rows; its event listener, and with it the game actions
/// a handler can cancel, waits for a mod's use, as when nothing loaded the assembly before a mod did.</summary>
public sealed unsafe class GameplayStartTests
{
    private static int _subscribes;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSubscribe(ulong owner, uint mask, delegate* unmanaged[Cdecl]<GameplayNative.Event*, void*, uint> fn,
        void* user, ulong* handle)
    {
        _subscribes++;
        *handle = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(GameplayNative.Status* s) => 3;  // unavailable: no status

    [Fact]
    public void The_first_tick_reports_the_rows_and_a_catch_all_handler_starts_no_listener()
    {
        _subscribes = 0;
        var t = (GameplayNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(GameplayNative.Table));
        t->Header = new ServiceHeader { Size = (uint)sizeof(GameplayNative.Table), Version = 1 };
        t->Subscribe = &FakeSubscribe;
        t->Status = &FakeStatus;
        var kiln = new IsolatedKiln(["Kiln.Gameplay"]);
        kiln.Serve("kiln.gameplay", (nint)t);
        kiln.Step();
        Assert.NotEqual("Unknown", kiln.Row("gameplay.entity_events").Reason);  // the rows are reported
        Assert.Equal(0, _subscribes);
        // a handler of every event (a mod written before Kiln.Gameplay was started at the first tick) is not a use
        kiln.Subscribe(kiln.Type("Kiln.KilnEvent"));
        kiln.Subscribe(kiln.Type("Kiln.CancelableEvent"));
        kiln.Step();
        Assert.Equal(0, _subscribes);
        // a handler of one of its events is
        kiln.Subscribe(kiln.Services["Kiln.Gameplay"].GetType("Kiln.CraftEvent", throwOnError: true)!);
        Assert.Equal(1, _subscribes);
    }

    private static int _listeners;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetListener(delegate* unmanaged[Cdecl]<CombatNative.Event*, uint, void*, uint> fn, void* user)
    {
        _listeners++;
        return 0;
    }

    /// <summary>Kiln.Combat the same way: with its listener kiln.combat holds hits a step for a handler (and a hurt event
    /// can be canceled); a catch-all handler must not start that on a server whose mods never use Kiln.Combat.</summary>
    [Fact]
    public void Combat_starts_its_rows_at_the_first_tick_and_its_listener_at_a_use()
    {
        _listeners = 0;
        var t = (CombatNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(CombatNative.Table));
        t->Header = new ServiceHeader { Size = (uint)sizeof(CombatNative.Table), Version = 1 };
        t->SetListener = &FakeSetListener;
        var kiln = new IsolatedKiln(["Kiln.Combat"]);
        kiln.Serve("kiln.combat", (nint)t);
        kiln.Step();
        Assert.NotEqual("Unknown", kiln.Row("combat.hold_filter").Reason);
        Assert.Equal(0, _listeners);
        kiln.Subscribe(kiln.Type("Kiln.KilnEvent"));
        kiln.Step();
        Assert.Equal(0, _listeners);
        kiln.Subscribe(kiln.Services["Kiln.Combat"].GetType("Kiln.EntityDeathEvent", throwOnError: true)!);
        Assert.Equal(1, _listeners);
    }
}
