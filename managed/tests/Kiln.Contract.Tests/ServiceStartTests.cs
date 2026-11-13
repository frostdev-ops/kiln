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

    private object? _mod;

    /// <summary>Runs <paramref name="call"/> as a mod's code runs: with a mod context of this copy current on the thread
    /// (ModContext.CurrentMod).</summary>
    public object? AsMod(Func<object?> call)
    {
        _mod ??= Type("Kiln.HostAccess").GetMethod("CreateContext", All)!.Invoke(null,
            [Activator.CreateInstance(Type("Kiln.ModInfo"), "caller", "Caller", "1.0.0", "."), 90UL, "."]);
        var current = Type("Kiln.ModContext").GetField("CurrentMod", All)!;
        var before = current.GetValue(null);
        current.SetValue(null, _mod);
        try
        {
            return call();
        }
        finally
        {
            current.SetValue(null, before);
        }
    }

    /// <summary>The type names of the events the handlers of <see cref="Subscribe"/> were given (they do nothing else).</summary>
    public static readonly List<string> Seen = [];

    private static void Ignore<T>(T e)
    {
        lock (Seen) Seen.Add(e!.GetType().Name);
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
        ["Kiln.World"] = "world.* lowered when kiln.world is missing, at its first use, with world.tracked_writes",
        ["Kiln.Blocks"] = "no native table; its rows exist from its first use (an item block's compile, the affix " +
                          "session), so a manifest's features cannot require them",
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
        _gameplayFn = (nint)fn;
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
    private static nint _combatFn, _gameplayFn;  // the listeners the services gave the fake tables

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetListener(delegate* unmanaged[Cdecl]<CombatNative.Event*, uint, void*, uint> fn, void* user)
    {
        _listeners++;
        _combatFn = (nint)fn;
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

    private static int _holdPushes;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeHoldFilter(uint* ids, uint count, uint interest)
    {
        _holdPushes++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeIdsOff(uint kind, CombatNative.AttributeIds* ids) => 3;  // no attribute resource read yet

    private static CombatNative.TableV3* CombatV3()
    {
        var t = (CombatNative.TableV3*)NativeMemory.AllocZeroed((nuint)sizeof(CombatNative.TableV3));
        t->V2.V1.Header = new ServiceHeader { Size = (uint)sizeof(CombatNative.TableV3), Version = 1 };
        t->V2.V1.SetListener = &FakeSetListener;
        t->V2.AttributeIdsFn = &FakeIdsOff;
        t->HoldFilter = &FakeHoldFilter;
        return t;
    }

    /// <summary>A mod's first subscription to a hit event starts Kiln.Combat's listener and widens what kiln.combat holds
    /// before Events.On returns: a hit later in that step is held (as in dev.26, where the subscription started it all).</summary>
    [Fact]
    public void The_first_hurt_subscription_widens_the_hold_at_once()
    {
        _listeners = 0;
        _holdPushes = 0;
        var kiln = new IsolatedKiln(["Kiln.Combat"]);
        kiln.Serve("kiln.combat", (nint)CombatV3());
        kiln.Step();
        Assert.Equal(0, _listeners);
        kiln.Subscribe(kiln.Services["Kiln.Combat"].GetType("Kiln.EntityHurtEvent", throwOnError: true)!);
        Assert.Equal(1, _listeners);
        Assert.True(_holdPushes > 0, "the hold filter was not widened for the new subscription");
    }

    private static int _attr = 10;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAttrGet(uint e, uint component, uint attribute, int* v)
    {
        *v = _attr;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAbilitiesStatus(AbilitiesNative.Status* s) => 0;  // here, with nothing of the world yet

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeAttrAdd(uint e, uint component, uint attribute, int delta)
    {
        _attr += delta;
        return 0;
    }

    /// <summary>An Abilities attribute write that only a catch-all handler sees posts CombatChangedEvent without
    /// starting Kiln.Combat's listener (a catch-all handler is no use of Kiln.Combat's events).</summary>
    [Fact]
    public void An_attribute_change_seen_by_a_catch_all_handler_starts_no_combat_listener()
    {
        _listeners = 0;
        var abilities = (AbilitiesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(AbilitiesNative.Table));
        abilities->Header = new ServiceHeader { Size = (uint)sizeof(AbilitiesNative.Table), Version = 1 };
        abilities->GetStatus = &FakeAbilitiesStatus;
        abilities->AttrGet = &FakeAttrGet;
        abilities->AttrAdd = &FakeAttrAdd;
        var kiln = new IsolatedKiln(["Kiln.Combat", "Kiln.Abilities"]);
        kiln.Serve("kiln.combat", (nint)CombatV3());
        kiln.Serve("kiln.abilities", (nint)abilities);
        kiln.Step();
        kiln.Subscribe(kiln.Type("Kiln.KilnEvent"));
        var asm = kiln.Services["Kiln.Abilities"];
        var attributes = asm.GetType("Kiln.Abilities+Attributes", throwOnError: true)!;
        var resource = Enum.Parse(asm.GetType("Kiln.AbilityResource", throwOnError: true)!, "Mana");
        var entity = Activator.CreateInstance(kiln.Type("Kiln.EntityId"), 300u)!;
        var add = attributes.GetMethod("Add", [kiln.Type("Kiln.EntityId"), resource.GetType(), typeof(int)])!;
        lock (IsolatedKiln.Seen) IsolatedKiln.Seen.Clear();
        Assert.True((bool)add.Invoke(null, [entity, resource, 5])!);
        lock (IsolatedKiln.Seen) Assert.Contains("CombatChangedEvent", IsolatedKiln.Seen);  // the change was posted
        Assert.Equal(0, _listeners);
    }

    /// <summary>A table published after the first look-up: a mod subscribed while kiln.combat was missing, so the
    /// listener could not start; the service tick's refresh finds the table and starts it (a core says service-missing
    /// here, as a late service would have it).</summary>
    [Fact]
    public void A_listener_waiting_for_a_late_table_starts_when_the_refresh_finds_it()
    {
        _listeners = 0;
        var kiln = new IsolatedKiln(["Kiln.Combat"]);
        var reason = Enum.Parse(kiln.Type("Kiln.CapabilityReason"), "ServiceMissing");
        kiln.Type("Kiln.Capabilities").GetMethod("ReportService", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["kiln.combat", reason, "kiln.combat is not here yet"]);
        kiln.Step();
        kiln.Subscribe(kiln.Services["Kiln.Combat"].GetType("Kiln.EntityDeathEvent", throwOnError: true)!);
        Assert.Equal(0, _listeners);  // no table: nothing to listen to
        kiln.Serve("kiln.combat", (nint)CombatV3());
        for (var i = 0; i < 25; i++) kiln.Step();
        Assert.Equal(1, _listeners);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeCombatStatus(CombatNative.Status* s) => 3;  // unavailable: no status

    /// <summary>A dev.26 mod with only a catch-all handler that calls Kiln.Combat: its first call starts the listener (as
    /// the call loaded the assembly in dev.26), and the next native event reaches the handler. The rows' own reads at
    /// the service tick (Attributes.Available every 25 steps) are no use.</summary>
    [Fact]
    public void A_catch_all_handler_and_one_plain_call_into_combat_get_the_next_native_event()
    {
        _listeners = 0;
        var t = CombatV3();
        t->V2.V1.GetStatus = &FakeCombatStatus;
        var kiln = new IsolatedKiln(["Kiln.Combat"]);
        kiln.Serve("kiln.combat", (nint)t);
        kiln.Step();
        kiln.Subscribe(kiln.Type("Kiln.KilnEvent"));
        for (var i = 0; i < 30; i++) kiln.Step();  // the rows' refresh reads Kiln.Combat: not a use
        Assert.Equal(0, _listeners);
        var combat = kiln.Services["Kiln.Combat"].GetType("Kiln.Combat", throwOnError: true)!;
        kiln.AsMod(() => combat.GetProperty("Available")!.GetValue(null));  // one plain call by a mod
        Assert.Equal(1, _listeners);
        lock (IsolatedKiln.Seen) IsolatedKiln.Seen.Clear();
        var died = new CombatNative.Event { Size = (uint)sizeof(CombatNative.Event), Kind = CombatNative.Died, Target = 300 };
        ((delegate* unmanaged[Cdecl]<CombatNative.Event*, uint, void*, uint>)_combatFn)(&died, 1, null);
        lock (IsolatedKiln.Seen) Assert.Contains("EntityDeathEvent", IsolatedKiln.Seen);
    }

    /// <summary>Kiln.Gameplay the same way: a mod's plain call starts the listener its catch-all handler alone does not,
    /// and the next native event reaches the handler.</summary>
    [Fact]
    public void A_catch_all_handler_and_one_plain_call_into_gameplay_get_the_next_native_event()
    {
        _subscribes = 0;
        var t = (GameplayNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(GameplayNative.Table));
        t->Header = new ServiceHeader { Size = (uint)sizeof(GameplayNative.Table), Version = 1 };
        t->Subscribe = &FakeSubscribe;
        t->Status = &FakeStatus;
        var kiln = new IsolatedKiln(["Kiln.Gameplay"]);
        kiln.Serve("kiln.gameplay", (nint)t);
        kiln.Step();
        kiln.Subscribe(kiln.Type("Kiln.KilnEvent"));
        for (var i = 0; i < 30; i++) kiln.Step();
        Assert.Equal(0, _subscribes);
        var gameplay = kiln.Services["Kiln.Gameplay"].GetType("Kiln.Gameplay", throwOnError: true)!;
        kiln.AsMod(() => gameplay.GetProperty("Available")!.GetValue(null));
        Assert.Equal(1, _subscribes);
        lock (IsolatedKiln.Seen) IsolatedKiln.Seen.Clear();
        var craft = new GameplayNative.Event { Size = (uint)sizeof(GameplayNative.Event), Kind = GameplayNative.Craft, Actor = 1, Target = 300 };
        ((delegate* unmanaged[Cdecl]<GameplayNative.Event*, void*, uint>)_gameplayFn)(&craft, null);
        lock (IsolatedKiln.Seen) Assert.Contains("CraftEvent", IsolatedKiln.Seen);
    }
}
