// SPDX-License-Identifier: MIT
using System.Reflection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]  // the backend, the bus and the registry binding are static

namespace Kiln.Contract.Tests;

/// <summary>A backend with no Kiln core and no native tables: every service is off, as in a tool or a unit test.</summary>
internal sealed class OfflineBackend : IKilnBackend
{
    public readonly List<string> Lines = [];
    public bool TickThread = true;
    public string Version => "test";
    public bool IsTickThread => TickThread;
    public bool HasFeature(string name) => false;
    public nint Service(string name, uint minVersion) => 0;

    public void Log(ulong owner, LogLevel level, string text)
    {
        lock (Lines) Lines.Add($"{level}: {text}");
    }
}

/// <summary>Fresh Kiln state per test: an offline backend (so a new capability registry binding), an empty bus and
/// service tick. The services' own statics stay as the process has them: their rows are reported again through
/// <see cref="StartRows"/>.</summary>
public abstract class ContractTest : IDisposable
{
    private readonly IKilnBackend _previous = Kiln.Backend.Current;
    internal readonly OfflineBackend Backend = new();
    private ulong _step;

    protected ContractTest()
    {
        Kiln.Backend.Current = Backend;
        ServiceTick.Reset();
        ServiceTick.AutoStartEnabled = false;
        Events.Reset();
        HostAccess.Current = null;
    }

    public virtual void Dispose()
    {
        if (ReferenceEquals(Kiln.Backend.Current, Backend)) Kiln.Backend.Current = _previous;
    }

    /// <summary>One service tick (as the host drives it).</summary>
    internal void Step(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            _step++;
            ServiceTick.Run(HostAccess.Tick(_step, (uint)_step * 2, 2, 0, 40.8));
        }
    }

    /// <summary>What a service's start does for its rows: every <see cref="ServiceCapRows"/> the assembly holds reports
    /// under this test's registry binding.</summary>
    internal static void StartRows(Assembly service)
    {
        foreach (var rows in RowSets(service)) rows.Ensure();
    }

    internal static IEnumerable<ServiceCapRows> RowSets(Assembly service)
    {
        foreach (var type in service.GetTypes())
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (f.FieldType == typeof(ServiceCapRows) && f.GetValue(null) is ServiceCapRows rows)
                    yield return rows;
    }

    /// <summary>The row is off because there is no game here, and says so.</summary>
    internal static Capability AssertOffline(string name)
    {
        var row = Capabilities.Get(name);
        Assert.True(row.State == CapabilityState.Unavailable, $"{name}: {Capabilities.Why(name)}");
        Assert.True(row.Reason == CapabilityReason.Offline, $"{name}: {Capabilities.Why(name)}");
        Assert.False(Capabilities.Is(name));
        return row;
    }

    /// <summary>The call throws <see cref="KilnUnavailableException"/> naming <paramref name="capability"/>, and a
    /// <c>catch (NotSupportedException)</c> written before dev.27 still catches it.</summary>
    internal static KilnUnavailableException AssertUnavailable(string capability, Action call)
    {
        NotSupportedException? caught = null;
        try
        {
            call();
        }
        catch (NotSupportedException e)
        {
            caught = e;
        }
        var k = Assert.IsType<KilnUnavailableException>(caught);
        Assert.Equal(capability, k.Capability);
        Assert.False(string.IsNullOrEmpty(k.Reason), $"{capability}: the exception gives no reason");
        return k;
    }

    internal static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, parameters)
        ?? throw new InvalidOperationException($"{type.Name}.{name}({string.Join(", ", parameters.Select(p => p.Name))}) not found");

    internal static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{type.Name}.{name} not found");

    internal static void AssertRequires(MemberInfo member, params string[] capabilities)
    {
        var names = member.GetCustomAttributes<RequiresCapabilityAttribute>().Select(a => a.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(capabilities.Order(StringComparer.Ordinal).ToArray(), names);
    }

    internal static void AssertHas<T>(MemberInfo member) where T : Attribute =>
        Assert.True(member.GetCustomAttribute<T>() is not null, $"{member.DeclaringType?.Name}.{member.Name} has no [{typeof(T).Name.Replace("Attribute", "")}]");
}
