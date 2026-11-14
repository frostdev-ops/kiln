// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

namespace Kiln.TestKit;

/// <summary>
/// A fake of one native service that plugs itself into the kit (since 0.2.0-dev.28): a class in
/// <c>Fakes/&lt;Service&gt;/</c> with a parameterless constructor. The kit finds every one in its own assembly, so a
/// new fake touches no shared file: not <c>FakeTables.Register</c>, not <c>KilnHarness.Fakes.cs</c>, not
/// <c>Coverage.Fakes.cs</c>. What a test scripts and reads goes into a part of <see cref="KilnHarness"/> in the same
/// folder (<c>KilnHarness.&lt;Service&gt;.cs</c>); the fake's state for one harness comes from
/// <see cref="KitFakes.State{T}"/>. The fakes of dev.27 and earlier are wired by hand in those three files and stay so.
/// </summary>
internal interface IKitFake
{
    /// <summary>The native service's name (<c>kiln.queries</c>); the fakes run in the ordinal order of it.</summary>
    string Service { get; }

    /// <summary>Once per process, before any Kiln service can ask for a table: publish the fake table
    /// (<c>KitBackend.Instance.RegisterTable</c>) and its capability rows (<see cref="FakeTables.Features"/>,
    /// <see cref="FakeTables.Offline"/>, <see cref="FakeTables.Session"/>).</summary>
    void Register();

    /// <summary>A harness starts: after the world, the players, the saves and the earlier fakes.</summary>
    void Start(KilnHarness harness);

    /// <summary>The start of a step, where the native service's Pre would run.</summary>
    void BeforeStep(KilnHarness harness, ulong step);

    /// <summary>The harness ends, before the players and the world go.</summary>
    void End(KilnHarness harness);

    /// <summary>The fake's rows of the coverage table (<see cref="Coverage"/>): what is faked, what is not.</summary>
    void AddCoverage(List<CoverageRow> rows);
}

/// <summary>The self-registering fakes (<see cref="IKitFake"/>) of this assembly, and their state per harness.</summary>
internal static class KitFakes
{
    private static readonly Lazy<IReadOnlyList<IKitFake>> Found = new(Discover);
    private static readonly ConditionalWeakTable<KilnHarness, Dictionary<Type, object>> States = new();

    /// <summary>Every fake, in the ordinal order of its service name.</summary>
    public static IReadOnlyList<IKitFake> All => Found.Value;

    /// <summary>The state object of type <typeparamref name="T"/> that belongs to <paramref name="harness"/>, made on
    /// first use and gone with the harness. Any thread.</summary>
    public static T State<T>(KilnHarness harness) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(harness);
        var bag = States.GetOrCreateValue(harness);
        lock (bag)
        {
            if (bag.TryGetValue(typeof(T), out var have)) return (T)have;
            var made = new T();
            bag[typeof(T)] = made;
            return made;
        }
    }

    private static IReadOnlyList<IKitFake> Discover()
    {
        var list = new List<IKitFake>();
        foreach (var type in typeof(KitFakes).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IKitFake).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"{type.FullName} is an IKitFake without a parameterless constructor");
            list.Add((IKitFake)Activator.CreateInstance(type)!);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Service, b.Service));
        for (var i = 1; i < list.Count; i++)
            if (list[i].Service == list[i - 1].Service)
                throw new InvalidOperationException($"two fakes of {list[i].Service}: {list[i - 1].GetType().Name} and {list[i].GetType().Name}");
        return list;
    }
}
