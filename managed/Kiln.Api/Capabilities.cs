// SPDX-License-Identifier: MIT
// The capability registry (native/core/abi/services/kiln_caps.h, service kiln.caps v1): one table that says, by name,
// what works in this process on this game build and side, and why something does not. The native core fills it; this
// file reads it as an immutable snapshot that is rebuilt when the core says a row changed.
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Whether a capability works (<see cref="Capability.State"/>).</summary>
public enum CapabilityState
{
    /// <summary>It does not work here; <see cref="Capability.Reason"/> and <see cref="Capability.ReasonText"/> say why.</summary>
    Unavailable = 0,

    /// <summary>It works.</summary>
    Available = 1,

    /// <summary>It depends on the session (no world yet, world data not loaded) and is not decided yet.</summary>
    Pending = 2,
}

/// <summary>Why a capability is off or pending (<see cref="Capability.Reason"/>). The values are the codes of
/// <c>kiln_caps.h</c>; <c>capabilities.json</c> writes the kebab-case name given with each.</summary>
public enum CapabilityReason
{
    /// <summary><c>none</c>: only with <see cref="CapabilityState.Available"/>.</summary>
    None = 0,

    /// <summary><c>symbol-missing</c>: a symbol or constant of the feature did not resolve on this game build. The text
    /// names the first one that failed: <c>symbol &lt;name&gt;: &lt;sigdb's reason&gt;</c>.</summary>
    SymbolMissing = 1,

    /// <summary><c>service-missing</c>: the service is not here (its DLL is not installed, was skipped, failed to load,
    /// or its init said no).</summary>
    ServiceMissing = 2,

    /// <summary><c>service-older</c>: the service is here but older than the caller needs (its version is below the
    /// minimum asked for, or its table is smaller).</summary>
    ServiceOlder = 3,

    /// <summary><c>wrong-side</c>: server only on a client, or client only on a server.</summary>
    WrongSide = 4,

    /// <summary><c>needs-kiln-client</c>: works only for players whose client runs Kiln.</summary>
    NeedsKilnClient = 5,

    /// <summary><c>policy</c>: switched off by configuration or by the build policy (safe idle on an unverified build,
    /// <c>kiln.json</c>, a service's own config).</summary>
    Policy = 6,

    /// <summary><c>faulted</c>: it was on and faulted, or its hook could not be installed.</summary>
    Faulted = 7,

    /// <summary><c>offline</c>: no game in this process (the test kit, a tool).</summary>
    Offline = 8,

    /// <summary><c>session</c>: waits for the session (no world yet, world data not loaded); the usual reason of a
    /// <see cref="CapabilityState.Pending"/> row.</summary>
    Session = 9,

    /// <summary><c>unknown</c>: no row of this name. Never stored: only <see cref="Capabilities.Get"/> answers it.</summary>
    Unknown = 10,
}

/// <summary>One row of the capability registry: a name, whether it works, and why not.</summary>
/// <param name="Name">The capability's name: a sigdb feature (<c>items.fuel</c>), <c>kiln.&lt;service&gt;</c> for a
/// service table being here, <c>&lt;service&gt;.&lt;thing&gt;</c> for a row a service registers.</param>
/// <param name="State">Whether it works.</param>
/// <param name="Reason">Why it is off or pending; <see cref="CapabilityReason.None"/> when it works.</param>
/// <param name="ReasonText">The reason in words, for a log line; empty when there is nothing to say (an available row
/// may carry a note, such as a service's version: <c>v3</c>).</param>
/// <param name="Source">Who made the report that is shown: <c>sigdb</c> (a feature row), <c>core</c> (a service row the
/// core derived), else the reporting owner's name.</param>
public readonly record struct Capability(
    string Name, CapabilityState State, CapabilityReason Reason, string ReasonText, string Source)
{
    /// <summary>The row depends on the session: it was reported <see cref="CapabilityState.Pending"/> at least once. A
    /// mod manifest's <c>features</c> may not name it; check <see cref="Capabilities.Is"/> at run time instead.</summary>
    public bool SessionDependent { get; init; }

    /// <summary><see cref="State"/> is <see cref="CapabilityState.Available"/>.</summary>
    public bool IsAvailable => State == CapabilityState.Available;
}

/// <summary>A row of the capability registry changed: posted on the bus (tick thread, at the start of the next step)
/// for each row that is new, changed or removed. Built only while someone subscribes.</summary>
public sealed class CapabilityChangedEvent : KilnEvent
{
    internal CapabilityChangedEvent(string name, Capability previous, Capability current)
    {
        Name = name;
        Previous = previous;
        Current = current;
    }

    /// <summary>The capability's name.</summary>
    public string Name { get; }

    /// <summary>The row before the change. <see cref="Capability.Reason"/> is <see cref="CapabilityReason.Unknown"/>
    /// when the row is new.</summary>
    public Capability Previous { get; }

    /// <summary>The row now. <see cref="Capability.Reason"/> is <see cref="CapabilityReason.Unknown"/> when the row was
    /// removed.</summary>
    public Capability Current { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Name}: {Capabilities.StateName(Previous.State)} -> {Capabilities.StateName(Current.State)} ({Capabilities.CodeName(Current.Reason)})";
}

/// <summary>Marks a member that needs a capability. The member throws <see cref="KilnUnavailableException"/> naming
/// <see cref="Name"/> when <see cref="Capabilities.Is"/> of it is false, so a caller can check
/// <c>Capabilities.Is(name)</c> first, or log <see cref="Capabilities.Why"/>. On a type: every member of it needs the
/// capability. Documentation only: nothing checks it at build or run time.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor |
                AttributeTargets.Event | AttributeTargets.Class | AttributeTargets.Struct |
                AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class RequiresCapabilityAttribute(string name) : Attribute
{
    /// <summary>The capability's name, as <see cref="Capabilities.Is"/> takes it.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// What works in this process, on this game build and side, and why something does not: one row per capability name,
/// filled by the Kiln core (every sigdb feature, every service), by native services and plugins, and by Kiln's managed
/// services. Every member may be called on any thread.
/// </summary>
/// <remarks>
/// <para>Names: a sigdb feature keeps its name (<c>items.fuel</c>, <c>net.kick</c>, <c>tick</c>);
/// <c>kiln.&lt;service&gt;</c> says that service's table is here (<c>kiln.items</c>); <c>&lt;service&gt;.&lt;thing&gt;</c>
/// is a row a service registers for one of its abilities (<c>items.moves</c>). Names are case-sensitive.</para>
/// <para>Most rows are fixed when the game has booted. A row that depends on the session is
/// <see cref="CapabilityState.Pending"/> until it is decided and carries <see cref="Capability.SessionDependent"/>; a
/// mod's manifest may require only rows without that flag (<c>features</c> in <c>kiln.mod.json</c>).</para>
/// <para>Reads cost a dictionary lookup in a snapshot; the snapshot follows the core's table and posts a
/// <see cref="CapabilityChangedEvent"/> for each row that changes. The same table is written to
/// <c>kiln/state/capabilities.json</c> for the launcher.</para>
/// </remarks>
public static unsafe class Capabilities
{
    internal const string ServiceName = "kiln.caps";
    internal const uint ServiceVersion = 1;
    internal const int NameMax = 96;
    internal const int TextMax = 512;

    /// <summary>The owner id Kiln's own reports are made with, and the source in-memory rows show.</summary>
    internal const string OwnerId = "capabilities";

    private const string NoRowText = "no capability of this name is registered";

    // One per backend Kiln.Api ran against (in the game: one for the process). What it holds lives as long as it does.
    internal sealed class Binding(IKilnBackend backend, long epoch)
    {
        public readonly IKilnBackend Backend = backend;
        public readonly long Epoch = epoch;
        public CapsTable* Table;        // null: no kiln.caps in this process; rows are kept in memory
        public ulong Owner;             // Kiln's own owner for reports and the subscription
        public bool Subscribed;
        // Services.Get<T>'s bookkeeping (Services.cs): names a table satisfied, (name, cause) pairs it recorded, and the
        // names whose current managed report it made (so taking a report back never undoes a service's own).
        public readonly ConcurrentDictionary<string, byte> Found = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<(string Name, int Cause), byte> Recorded = new();
        public readonly HashSet<string> ByServices = new(StringComparer.Ordinal);
        // What RefreshIdentity last reported for identity.entities / identity.players: -1 nothing, 0 pending, 1 available.
        public int IdentityEntities = -1, IdentityPlayers = -1;
    }

    // An immutable snapshot of the rows. Replaced whole, never changed.
    private sealed class Snapshot(Binding binding, Dictionary<string, Capability> rows, ulong generation)
    {
        public readonly Binding Binding = binding;
        public readonly Dictionary<string, Capability> Rows = rows;
        public readonly ulong Generation = generation;
        public readonly ReadOnlyCollection<Capability> Sorted =
            Array.AsReadOnly(rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray());
    }

    private static readonly object Gate = new();
    private static Snapshot? _snapshot;
    private static long _epochs;

    /// <summary>Whether <paramref name="name"/> works here: its row is <see cref="CapabilityState.Available"/>. A name
    /// with no row is false. Any thread; a dictionary lookup, no native call.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    [AnyThread]
    public static bool Is(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var s = Current();
        if (s.Rows.TryGetValue(name, out var row)) return row.State == CapabilityState.Available;
        return s.Binding.Table == null && s.Binding.Backend.HasFeature(name);
    }

    /// <summary>The row of <paramref name="name"/>. A name with no row gives
    /// <see cref="CapabilityState.Unavailable"/> with <see cref="CapabilityReason.Unknown"/>. Any thread.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    [AnyThread]
    public static Capability Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var s = Current();
        if (s.Rows.TryGetValue(name, out var row)) return row;
        if (s.Binding.Table == null && s.Binding.Backend.HasFeature(name))
            return new Capability(name, CapabilityState.Available, CapabilityReason.None, "", "backend");
        return NoRow(name);
    }

    /// <summary>One sentence about <paramref name="name"/> a mod can log: the name, the state, the reason in words, the
    /// reason code and who said so, e.g. <c>items.fuel is unavailable: symbol items/FuelTick: find[0]: 0 matches
    /// (symbol-missing, from sigdb).</c> or <c>items.fuel is available.</c> Any thread.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    [AnyThread]
    public static string Why(string name) => Sentence(Get(name));

    /// <summary>Every row, sorted by name (ordinal). A snapshot: it does not change after it was read. Any thread.</summary>
    [AnyThread]
    public static IReadOnlyList<Capability> All => Current().Sorted;

    /// <summary>A managed service's row (Kiln's own assemblies), held by Kiln's managed owner rather than the calling
    /// mod's, so it stays when that mod unloads. With the native registry the row a reader sees is the worst report for
    /// the name (unavailable beats pending beats available). The snapshot holds the new row before this returns.</summary>
    /// <param name="name">1 to 96 bytes of <c>[A-Za-z0-9._/-]</c>.</param>
    /// <param name="state">The new state. <see cref="CapabilityState.Pending"/> makes the name session-dependent for good.</param>
    /// <param name="reason"><see cref="CapabilityReason.None"/> exactly when <paramref name="state"/> is available; never
    /// <see cref="CapabilityReason.Unknown"/>.</param>
    /// <param name="text">The reason in words (cut at 512 bytes).</param>
    /// <exception cref="ArgumentException">A bad name, state or reason, or <see cref="CapabilityState.Pending"/> for a
    /// name the core reports itself (a sigdb feature, <c>kiln.&lt;service&gt;</c>).</exception>
    /// <exception cref="InvalidOperationException">The core refused the report for another reason.</exception>
    internal static void Report(string name, CapabilityState state, CapabilityReason reason = CapabilityReason.None,
        string? text = null)
    {
        var s = Current();
        lock (Gate) s.Binding.ByServices.Remove(name ?? "");
        Write(s, name!, state, reason, text);
    }

    /// <summary>Drops the in-memory rows, the cached native snapshot and <see cref="Services"/>' records; the next read
    /// starts over with <see cref="Backend.Current"/>. Tests and the test kit. A native subscription made before stays
    /// (its calls are ignored): the process keeps one, and the fake table may already be gone.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Volatile.Write(ref _snapshot, null);
            _epochs++;
        }
    }

    // ------------------------------------------------------------------------------------- identity rows -------

    private static readonly EntitySourceOrder[] EntityPlaces = Enum.GetValues<EntitySourceOrder>();
    private static IDisposable? _identityTick;

    /// <summary>The host calls this once at start: binds the registry and keeps the rows <c>identity.entities</c> and
    /// <c>identity.players</c> (what <see cref="EntityRef"/> and <see cref="PlayerRef"/> throw
    /// <see cref="KilnUnavailableException"/> with) in step with their sources. The services that supply identity start
    /// at the first tick, after manifests were checked, so both are session rows: pending until a source is there,
    /// then available, and pending again when it goes. Checked on the service tick (two reads a step), not in a getter.</summary>
    internal static void Start()
    {
        _ = Current();
        lock (Gate)
        {
            _identityTick?.Dispose();
            _identityTick = ServiceTick.Add("kiln capabilities", static (in TickInfo _) => RefreshIdentity());
        }
        RefreshIdentity();
    }

    internal static void RefreshIdentity()
    {
        var b = Current().Binding;
        var entities = IdentitySources.Entities is not null;
        if (!entities)
            foreach (var place in EntityPlaces)
                if (IdentitySources.EntitySource(place) is not null)
                {
                    entities = true;
                    break;
                }
        ReportIdentity(b, EntityRef.Capability, entities, ref b.IdentityEntities,
            "no service supplies entity serials yet (the services that do start at the first tick)");
        ReportIdentity(b, PlayerRef.Capability, IdentitySources.Players is not null, ref b.IdentityPlayers,
            "no service supplies players yet (the services that do start at the first tick)");
    }

    private static void ReportIdentity(Binding b, string name, bool there, ref int reported, string waiting)
    {
        var now = there ? 1 : 0;
        if (reported == now) return;
        reported = now;  // also when the report is refused: once, not every step
        try
        {
            if (there) Report(name, CapabilityState.Available);
            else Report(name, CapabilityState.Pending, CapabilityReason.Session, waiting);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            b.Backend.Log(0, LogLevel.Warn, $"capabilities: {name} was not reported: {e.Message}");
        }
    }

    // ------------------------------------------------------------------------------------- codes and text ------

    /// <summary>The kebab-case name <c>kiln_caps.h</c> and <c>capabilities.json</c> give a reason code.</summary>
    internal static string CodeName(CapabilityReason reason) => reason switch
    {
        CapabilityReason.None => "none",
        CapabilityReason.SymbolMissing => "symbol-missing",
        CapabilityReason.ServiceMissing => "service-missing",
        CapabilityReason.ServiceOlder => "service-older",
        CapabilityReason.WrongSide => "wrong-side",
        CapabilityReason.NeedsKilnClient => "needs-kiln-client",
        CapabilityReason.Policy => "policy",
        CapabilityReason.Faulted => "faulted",
        CapabilityReason.Offline => "offline",
        CapabilityReason.Session => "session",
        CapabilityReason.Unknown => "unknown",
        _ => "other-" + ((int)reason).ToString(System.Globalization.CultureInfo.InvariantCulture),  // as capabilities.json writes a code it has no name for
    };

    /// <summary>The name <c>capabilities.json</c> gives a state.</summary>
    internal static string StateName(CapabilityState state) => state switch
    {
        CapabilityState.Available => "available",
        CapabilityState.Pending => "pending",
        _ => "unavailable",
    };

    internal static string Sentence(Capability row)
    {
        var sb = new StringBuilder(row.Name).Append(" is ").Append(StateName(row.State));
        if (row.ReasonText is { Length: > 0 } text) sb.Append(": ").Append(text);
        if (row.State != CapabilityState.Available)
        {
            sb.Append(" (").Append(CodeName(row.Reason));
            if (row.Source is { Length: > 0 } source) sb.Append(", from ").Append(source);
            sb.Append(')');
        }
        return sb.Append('.').ToString();
    }

    private static Capability NoRow(string name) =>
        new(name, CapabilityState.Unavailable, CapabilityReason.Unknown, NoRowText, "");

    // ------------------------------------------------------------------------------------- for Services --------

    /// <summary>The current binding (Services.Get&lt;T&gt;'s bookkeeping lives there).</summary>
    internal static Binding CurrentBinding => Current().Binding;

    /// <summary>The row of <paramref name="name"/> if it has one (no <c>HasFeature</c> fallback).</summary>
    internal static bool TryGetRow(string name, out Capability row) => Current().Rows.TryGetValue(name, out row);

    /// <summary>Services.Get&lt;T&gt; returned null for <paramref name="name"/>: an unavailable report it can take back.</summary>
    internal static void ReportService(string name, CapabilityReason reason, string text)
    {
        var s = Current();
        lock (Gate)
        {
            if (s.Binding.Found.ContainsKey(name)) return;
            // A report can synchronously call subscribers that find the base table. Mark ours before writing it,
            // so their successful lookup can take it back; serialize a concurrent success with the same lock.
            s.Binding.ByServices.Add(name);
            try
            {
                Write(s, name, CapabilityState.Unavailable, reason, text);
            }
            catch
            {
                s.Binding.ByServices.Remove(name);
                throw;
            }
        }
    }

    /// <summary>Services.Get&lt;T&gt; found <paramref name="name"/>'s table: takes back the report it made (native: an
    /// available report, which hides behind every other; in memory: the row says the service is here).</summary>
    internal static void ServiceFound(string name, uint version)
    {
        var s = Current();
        bool ours;
        lock (Gate) ours = s.Binding.ByServices.Remove(name);
        if (s.Binding.Table != null)
        {
            if (ours) Write(s, name, CapabilityState.Available, CapabilityReason.None, null);
            return;
        }
        if (ours || !s.Rows.ContainsKey(name))
            Write(s, name, CapabilityState.Available, CapabilityReason.None, "v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------------------------- the snapshot --------

    private static Snapshot Current()
    {
        var s = Volatile.Read(ref _snapshot);
        return s != null && ReferenceEquals(s.Binding.Backend, Backend.Current) ? s : Bind();
    }

    private static Snapshot Bind()
    {
        lock (Gate)
        {
            var backend = Backend.Current;
            var s = _snapshot;
            if (s != null && ReferenceEquals(s.Binding.Backend, backend)) return s;
            var binding = new Binding(backend, ++_epochs);
            var table = (CapsTable*)backend.Service(ServiceName, ServiceVersion);
            if (table == null || table->Header.Size < (uint)sizeof(CapsTable))
            {
                s = new Snapshot(binding, new Dictionary<string, Capability>(StringComparer.Ordinal), 0);
            }
            else
            {
                binding.Table = table;
                binding.Owner = backend.OwnerCreate(OwnerId);
                ulong handle = 0;
                var rc = table->Subscribe(binding.Owner, &OnChanged, (void*)binding.Epoch, &handle);
                binding.Subscribed = rc == Ok;
                if (!binding.Subscribed)
                    backend.Log(0, LogLevel.Warn, $"capabilities: kiln.caps refused the change subscription ({rc}); rows follow Kiln's own reports only");
                var rows = Walk(table, out var generation);
                s = new Snapshot(binding, rows, generation);
            }
            Volatile.Write(ref _snapshot, s);
            return s;
        }
    }

    // Native rows, consistent with one generation (walked again while it moves; after eight tries the last walk is kept
    // with generation 0, so the next change walks again).
    private static Dictionary<string, Capability> Walk(CapsTable* t, out ulong generation)
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = t->Generation();
            var count = t->Count();
            var rows = new Dictionary<string, Capability>((int)Math.Min(count, 4096u), StringComparer.Ordinal);
            for (uint i = 0; i < count; i++)
            {
                CapRow r = default;
                r.Size = (uint)sizeof(CapRow);
                if (t->At(i, &r) != Ok) break;
                var row = FromNative(&r);
                rows[row.Name] = row;
            }
            var after = t->Generation();
            if (before == after || attempt >= 7)
            {
                generation = before == after ? after : 0;
                return rows;
            }
        }
    }

    private static Capability FromNative(CapRow* r)
    {
        var state = r->State <= 2 ? (CapabilityState)r->State : CapabilityState.Unavailable;
        return new Capability(r->Name.ToString(), state, (CapabilityReason)r->Reason, r->Text.ToString(), r->Source.ToString())
        {
            SessionDependent = (r->Flags & FlagSession) != 0,
        };
    }

    // The core says a row changed. Any thread, also a native one, two at once: walk again under the lock.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChanged(CapRow* row, uint change, void* user)
    {
        try
        {
            Refresh((long)user);
        }
        catch
        {
            // never let an exception cross into native code
        }
    }

    private static void Refresh(long epoch)
    {
        lock (Gate)
        {
            var s = _snapshot;
            if (s == null || s.Binding.Epoch != epoch || s.Binding.Table == null) return;
            var t = s.Binding.Table;
            if (s.Generation != 0 && t->Generation() == s.Generation) return;
            var rows = Walk(t, out var generation);
            // A walk calls into the core, and the core delivers a change it notices there (an owner that went, a tick
            // hook that faulted) on this thread, inside that call: this method ran again under the same lock and may
            // have published already. Compare with what readers see now, so no row's change is posted twice.
            var shown = _snapshot;
            if (shown == null || !ReferenceEquals(shown.Binding, s.Binding)) return;
            Publish(shown, new Snapshot(s.Binding, rows, generation));
        }
    }

    // Gate held. Replaces the snapshot and posts one event per row that is new, changed or removed.
    private static void Publish(Snapshot previous, Snapshot next)
    {
        Volatile.Write(ref _snapshot, next);
        if (!Events.HasSubscribers<CapabilityChangedEvent>()) return;
        try
        {
            foreach (var now in next.Sorted)
            {
                if (!previous.Rows.TryGetValue(now.Name, out var before)) Events.Enqueue(new CapabilityChangedEvent(now.Name, NoRow(now.Name), now));
                else if (before != now) Events.Enqueue(new CapabilityChangedEvent(now.Name, before, now));
            }
            foreach (var before in previous.Sorted)
                if (!next.Rows.ContainsKey(before.Name))
                    Events.Enqueue(new CapabilityChangedEvent(before.Name, before, NoRow(before.Name)));
        }
        catch (InvalidOperationException)
        {
            // the bus's queue is full (nothing drains it): the snapshot is right, the events are lost
        }
    }

    // ------------------------------------------------------------------------------------- reports -------------

    private static void Write(Snapshot s, string name, CapabilityState state, CapabilityReason reason, string? text)
    {
        Validate(name, state, reason);
        text = Cut(text ?? "");
        var b = s.Binding;
        if (b.Table == null)
        {
            if (state == CapabilityState.Pending && name.StartsWith("kiln.", StringComparison.Ordinal))
                throw new ArgumentException(StaticName(name), nameof(state));
            lock (Gate)
            {
                var cur = Current();
                if (!ReferenceEquals(cur.Binding, b)) return;  // Reset or another backend in between
                var rows = new Dictionary<string, Capability>(cur.Rows, StringComparer.Ordinal);
                var session = state == CapabilityState.Pending || (rows.TryGetValue(name, out var old) && old.SessionDependent);
                rows[name] = new Capability(name, state, reason, text, OwnerId) { SessionDependent = session };
                Publish(cur, new Snapshot(b, rows, cur.Generation + 1));
            }
            return;
        }
        if (b.Owner == 0) throw new InvalidOperationException($"capabilities: no Kiln owner to report {name} with (the core gave none)");
        int rc;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var textBytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* np = nameBytes)
        fixed (byte* tp = textBytes)
            rc = b.Table->Report(b.Owner, new NativeStr { Ptr = np, Len = (nuint)nameBytes.Length }, (uint)state, (uint)reason,
                new NativeStr { Ptr = tp, Len = (nuint)textBytes.Length });
        switch (rc)
        {
            case Ok:
                Refresh(b.Epoch);  // the caller reads its own report, whether or not the change callback came already
                return;
            case ErrInvalidArgument:
                throw new ArgumentException(state == CapabilityState.Pending
                    ? StaticName(name)
                    : $"kiln.caps refused the report of {name} ({StateName(state)}, {CodeName(reason)}) as invalid", nameof(name));
            case ErrRevoked:
                throw new InvalidOperationException($"capabilities: Kiln's owner for reports is gone; {name} was not reported");
            case ErrUnavailable:
                throw new InvalidOperationException($"capabilities: Kiln's owner already holds {256} reports; {name} was not reported");
            default:
                throw new InvalidOperationException($"capabilities: kiln.caps answered {rc} to the report of {name}");
        }
    }

    private static string StaticName(string name) =>
        $"{name} is a name the core reports itself (a sigdb feature or kiln.<service>): it is fixed at boot and cannot be " +
        "reported pending. Report the part that depends on the session as a row of its own (<service>.<thing>)";

    private static void Validate(string name, CapabilityState state, CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ValidName(name))
            throw new ArgumentException($"capability name '{name}' must be 1 to {NameMax} bytes of [A-Za-z0-9._/-]", nameof(name));
        if (state is not (CapabilityState.Unavailable or CapabilityState.Available or CapabilityState.Pending))
            throw new ArgumentException($"{(int)state} is not a capability state", nameof(state));
        if (reason == CapabilityReason.Unknown)
            throw new ArgumentException("Unknown is never reported: it is what Capabilities.Get answers for a name with no row", nameof(reason));
        if (reason is < CapabilityReason.None or > CapabilityReason.Session)
            throw new ArgumentException($"{(int)reason} is not a capability reason", nameof(reason));
        if ((state == CapabilityState.Available) != (reason == CapabilityReason.None))
            throw new ArgumentException(state == CapabilityState.Available
                ? $"an available capability has reason None, not {reason}"
                : $"a{(state == CapabilityState.Pending ? " pending" : "n unavailable")} capability needs a reason other than None", nameof(reason));
    }

    internal static bool ValidName(string name)
    {
        if (name.Length is 0 or > NameMax) return false;
        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '/' or '-')) return false;
        return true;
    }

    // At most TextMax bytes of UTF-8, cut at a character boundary.
    private static string Cut(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= TextMax) return text;
        var bytes = 0;
        var i = 0;
        while (i < text.Length)
        {
            var n = char.IsSurrogatePair(text, i) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(i, n));
            if (bytes + size > TextMax) break;
            bytes += size;
            i += n;
        }
        return text[..i];
    }

    // ------------------------------------------------------------------------------------- native layout -------

    private const int Ok = 0, ErrInvalidArgument = 1, ErrUnavailable = 3, ErrRevoked = 6;
    private const uint FlagSession = 1;

    /// <summary>KilnCapRow (kiln_caps.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CapRow
    {
        public uint Size;
        public uint State;
        public uint Reason;
        public uint Flags;
        public NativeStr Name;
        public NativeStr Text;
        public NativeStr Source;
    }

    /// <summary>KilnCapsApi v1 (kiln_caps.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CapsTable
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint> Count;
        public delegate* unmanaged[Cdecl]<uint, CapRow*, int> At;
        public delegate* unmanaged[Cdecl]<NativeStr, CapRow*, int> Find;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, uint, uint, NativeStr, int> Report;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<CapRow*, uint, void*, void>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<ulong> Generation;
    }
}
