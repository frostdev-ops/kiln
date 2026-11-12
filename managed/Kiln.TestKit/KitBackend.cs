// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Kiln.TestKit;

/// <summary>The kit's <see cref="IKilnBackend"/>: one for the whole process, installed by the module initializer (over
/// Kiln.Api's placeholder only) and again by each <see cref="KilnHarness"/> (a test fixture may have put its own in
/// between), never removed by the kit. It sits under
/// Kiln's services: a service asks it for its native table and gets the fake the kit registered for that name, or 0, so
/// every line of managed service code runs unchanged and an unfaked service reports itself unavailable by its own code.</summary>
internal sealed class KitBackend : IKilnBackend
{
    /// <summary><see cref="Game.KilnVersion"/> while no harness runs.</summary>
    public const string IdleVersion = "testkit";

    /// <summary>Owner of the kit's own capability reports.</summary>
    public const string CapabilityOwner = "testkit";

    public static KitBackend Instance { get; } = new();

#pragma warning disable CA2255  // a test kit: the backend must be in place before any Kiln service asks for a table
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ModuleInit()
    {
        // Only over Kiln.Api's placeholder: a test fixture that put a fake backend of its own in place keeps it (the
        // kit's module initializer can run in the middle of such a test, at the first touch of a kit type).
        if (Backend.Current.GetType().DeclaringType == typeof(Backend)) Install();
    }

    /// <summary>Makes the kit's backend <see cref="Backend.Current"/> (again).</summary>
    public static void Install()
    {
        if (!ReferenceEquals(Backend.Current, Instance)) Backend.Current = Instance;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, nint> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> _owners = new();
    private readonly Queue<LogLine> _idleLines = new();
    private long _nextOwner = 1000, _idleSeq;
    private volatile KilnHarness? _harness;

    private KitBackend()
    {
        Capabilities = new OfflineCapabilities();
        foreach (var name in ServiceNames.All)
            Capabilities.Report(CapabilityOwner, name, CapState.Unavailable, CapReason.Offline, NoFakeText, CapabilityOwner);
    }

    /// <summary>The reason text of a Kiln service the kit does not fake.</summary>
    public const string NoFakeText = "the test kit has no fake of this service (no game runs in this process)";

    /// <summary>The capability rows (the local stub until the core package's registry lands).</summary>
    public ICapabilityRegistry Capabilities { get; }

    /// <summary>Every name the backend answered "unavailable" for (no table, a feature that is off).</summary>
    public OfflineAsks Asked { get; } = new();

    /// <summary>The Kiln version this kit is built from (Kiln.Api's, without build metadata): what <see cref="Game.KilnVersion"/>
    /// says while a harness runs and what the loader checks <c>"kiln"</c> dependencies against.</summary>
    public static string KilnVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var v = typeof(Mod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Mod).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public KilnHarness? Harness => _harness;

    public void Attach(KilnHarness harness)
    {
        lock (_lock)
        {
            if (_harness is not null && !ReferenceEquals(_harness, harness)) throw new InvalidOperationException("another KilnHarness is attached");
            _harness = harness;
        }
    }

    public void Detach(KilnHarness harness)
    {
        lock (_lock)
            if (ReferenceEquals(_harness, harness)) _harness = null;
    }

    // ---- the native service tables (process lifetime) ----

    /// <summary>Publishes a fake native table under <paramref name="name"/> for the rest of the process: services cache
    /// the pointer they got first, so it is never taken back (a fake says "off" from inside its functions). The table
    /// starts with the <c>{size, version}</c> header every service table has. Its <c>kiln.&lt;service&gt;</c> row turns
    /// available. Registering the same pointer again is a no-op; another pointer for the name throws.</summary>
    public unsafe void RegisterTable(string name, nint table)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (table == 0) throw new ArgumentException("a table pointer of 0 means 'unavailable': leave the name unregistered", nameof(table));
        if (((ServiceHeader*)table)->Size < (uint)sizeof(ServiceHeader)) throw new ArgumentException($"{name}: the table's header says size {((ServiceHeader*)table)->Size}", nameof(table));
        lock (_lock)
        {
            if (_tables.TryGetValue(name, out var have))
            {
                if (have == table) return;
                throw new InvalidOperationException($"{name} already has a fake table for this process (services keep the first pointer they were given)");
            }
            _tables[name] = table;
        }
        Capabilities.Report(CapabilityOwner, name, CapState.Available, CapReason.None, "faked by the test kit", CapabilityOwner);
    }

    /// <summary>Whether the kit has a fake table for <paramref name="name"/>.</summary>
    public bool HasTable(string name)
    {
        lock (_lock) return _tables.ContainsKey(name);
    }

    // ---- IKilnBackend ----

    public string Version => _harness is null ? IdleVersion : KilnVersion;

    public bool IsTickThread => _harness?.IsTickThread ?? false;

    public bool HasFeature(string name)
    {
        if (Capabilities.Is(name)) return true;
        Asked.Record(AskKind.Feature, name, HostAccess.Current?.Info.Id);
        return false;
    }

    public unsafe nint Service(string name, uint minVersion)
    {
        nint table;
        lock (_lock) _tables.TryGetValue(name, out table);
        if (table != 0 && ((ServiceHeader*)table)->Version >= minVersion) return table;
        Asked.Record(AskKind.Service, name, HostAccess.Current?.Info.Id);
        return 0;
    }

    public ulong OwnerCreate(string id)
    {
        var owner = (ulong)Interlocked.Increment(ref _nextOwner);
        lock (_lock) _owners[owner] = id;
        return owner;
    }

    /// <summary>What a native owner id stands for: <c>mod:&lt;id&gt;</c>, <c>host</c>, or <c>kiln</c> for 0.</summary>
    public string OwnerName(ulong owner)
    {
        if (owner == 0) return "kiln";
        lock (_lock) return _owners.TryGetValue(owner, out var id) ? id : $"owner {owner}";
    }

    public void Log(ulong owner, LogLevel level, string text)
    {
        var source = OwnerName(owner);
        if (_harness is { } h)
        {
            h.Capture(level, source, text);
            return;
        }
        lock (_idleLines)
        {
            _idleLines.Enqueue(new LogLine(++_idleSeq, 0, level, source, text));
            while (_idleLines.Count > MaxIdleLines) _idleLines.Dequeue();
        }
    }

    /// <summary>How many log lines written while no harness runs are kept.</summary>
    public const int MaxIdleLines = 256;

    /// <summary>The last <see cref="MaxIdleLines"/> lines written while no harness ran (step 0).</summary>
    public IReadOnlyList<LogLine> IdleLines
    {
        get
        {
            lock (_idleLines) return _idleLines.ToArray();
        }
    }
}

/// <summary>What the backend was asked for and answered "unavailable".</summary>
internal enum AskKind
{
    /// <summary>A native service table (<c>Service(name)</c> returned 0).</summary>
    Service = 0,
    /// <summary>A feature (<c>Game.HasFeature</c>, a manifest's <c>features</c>) that is off.</summary>
    Feature = 1,
}

/// <summary>One "unavailable" answer: the name, and the mod whose code was running (<see cref="HostAccess.Current"/>;
/// null for Kiln's own code and the loader). <see cref="Sequence"/> is the latest time it was asked.</summary>
internal readonly record struct OfflineAsk(AskKind Kind, string Name, string? ModId, long Sequence);

/// <summary>The record of unavailable answers, kept as one entry per (kind, name, mod) with the sequence of its last ask,
/// so it stays small however often a service asks. <see cref="Mark"/> then <see cref="Since"/>: the names asked in
/// between (the offline skip rule of the xunit adapter). Thread-safe.</summary>
internal sealed class OfflineAsks
{
    private readonly object _lock = new();
    private readonly Dictionary<(AskKind, string, string?), long> _last = new();
    private long _sequence;

    public long Mark()
    {
        lock (_lock) return _sequence;
    }

    public void Record(AskKind kind, string name, string? modId)
    {
        lock (_lock) _last[(kind, name, modId)] = ++_sequence;
    }

    /// <summary>Everything asked after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<OfflineAsk> Since(long mark)
    {
        lock (_lock)
            return _last.Where(kv => kv.Value > mark).OrderBy(kv => kv.Value)
                .Select(kv => new OfflineAsk(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value)).ToArray();
    }
}
