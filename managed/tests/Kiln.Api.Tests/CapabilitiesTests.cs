// SPDX-License-Identifier: MIT
// Kiln.Capabilities against a fake kiln.caps table (the header's rules over a managed model) and without one.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>A kiln.caps v1 table (native/core/abi/services/kiln_caps.h) over a managed model: one report per owner and
/// name, the worst one shown (core first among equals, then the oldest), a sticky session flag, core names refused as
/// pending, subscribers called after the change. Counts every call.</summary>
internal sealed unsafe class FakeCaps : IDisposable
{
    private static FakeCaps? _current;

    private sealed record Report(ulong Owner, uint State, uint Reason, string Text, string Source, long Order);

    private readonly Dictionary<string, List<Report>> _reports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);
    private readonly HashSet<string> _coreNames = new(StringComparer.Ordinal);
    private readonly List<(nint Fn, nint User)> _subscribers = [];
    private readonly List<nint> _strings = [];
    private readonly Capabilities.CapsTable* _table;
    private readonly FakeServiceRegistration _registration;
    private readonly object _mu = new();
    private long _order;

    public ulong Generation = 1;
    public int Calls;
    public int Walks;  // count() calls
    public ulong SubscribedOwner;

    /// <summary>Runs once inside the next at() (a change on another thread whose callback has not come yet).</summary>
    public Action? DuringAt;

    public FakeCaps(FakeBackend backend)
    {
        _current = this;
        _table = (Capabilities.CapsTable*)NativeMemory.AllocZeroed((nuint)sizeof(Capabilities.CapsTable));
        _table->Header = new ServiceHeader { Size = (uint)sizeof(Capabilities.CapsTable), Version = 1 };
        _table->Count = &Count;
        _table->At = &At;
        _table->Find = &Find;
        _table->Report = &ReportFn;
        _table->Subscribe = &Subscribe;
        _table->Unsubscribe = &Unsubscribe;
        _table->Generation = &Gen;
        _registration = new FakeServiceRegistration(backend.Tables, "kiln.caps", (nint)_table);
    }

    public void Dispose()
    {
        _registration.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
        NativeMemory.Free(_table);
        foreach (var s in _strings) Marshal.FreeHGlobal(s);
    }

    /// <summary>The core's own report (owner 0): a sigdb feature or a service row.</summary>
    public void Core(string name, CapabilityState state, CapabilityReason reason = CapabilityReason.None, string text = "",
        string source = "sigdb", bool notify = true)
    {
        lock (_mu) _coreNames.Add(name);
        Set(0, name, (uint)state, (uint)reason, text, source, notify);
    }

    /// <summary>Every report of <paramref name="name"/> goes (its owners were revoked).</summary>
    public void Remove(string name)
    {
        Capabilities.CapRow row;
        lock (_mu)
        {
            var before = Visible(name);
            if (before is null || !_reports.Remove(name)) return;
            Generation++;
            row = Fill(before, name);
        }
        Notify(row, 2);
    }

    public int Subscribers
    {
        get { lock (_mu) return _subscribers.Count; }
    }

    private void Set(ulong owner, string name, uint state, uint reason, string text, string source, bool notify)
    {
        Capabilities.CapRow row;
        lock (_mu)
        {
            var before = Visible(name);
            if (!_reports.TryGetValue(name, out var list)) _reports[name] = list = [];
            var i = list.FindIndex(r => r.Owner == owner);
            var order = i >= 0 ? list[i].Order : ++_order;
            var report = new Report(owner, state, reason, text, source, order);
            if (i >= 0) list[i] = report;
            else list.Add(report);
            var flagged = state == 2 && _session.Add(name);
            var after = Visible(name)!;
            if (!flagged && before is not null && Same(before, after)) return;
            Generation++;
            if (!notify) return;
            row = Fill(after, name);
        }
        Notify(row, 1);
    }

    private static bool Same(Report a, Report b) =>
        a.State == b.State && a.Reason == b.Reason && a.Text == b.Text && a.Source == b.Source;

    private static int Rank(uint state) => state switch { 0 => 0, 2 => 1, _ => 2 };

    private Report? Visible(string name) =>
        _reports.TryGetValue(name, out var list) && list.Count > 0
            ? list.OrderBy(r => Rank(r.State)).ThenBy(r => r.Owner == 0 ? 0 : 1).ThenBy(r => r.Order).First()
            : null;

    // After the model's lock is released, on the thread that made the change (as the header says).
    private void Notify(Capabilities.CapRow row, uint change)
    {
        (nint Fn, nint User)[] subscribers;
        lock (_mu) subscribers = _subscribers.ToArray();
        foreach (var (fn, user) in subscribers)
            ((delegate* unmanaged[Cdecl]<Capabilities.CapRow*, uint, void*, void>)fn)(&row, change, (void*)user);
    }

    private Capabilities.CapRow Fill(Report r, string name) => new()
    {
        Size = (uint)sizeof(Capabilities.CapRow),
        State = r.State,
        Reason = r.Reason,
        Flags = _session.Contains(name) ? 1u : 0u,
        Name = Str(name),
        Text = Str(r.Text),
        Source = Str(r.Source),
    };

    private NativeStr Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        lock (_mu) _strings.Add(p);
        return new NativeStr { Ptr = (byte*)p, Len = (nuint)bytes.Length };
    }

    private string[] SortedNames() => _reports.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();

    // ---- the table ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Count()
    {
        var f = _current!;
        Interlocked.Increment(ref f.Calls);
        Interlocked.Increment(ref f.Walks);
        lock (f._mu) return (uint)f.SortedNames().Length;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int At(uint index, Capabilities.CapRow* out_)
    {
        var f = _current!;
        Interlocked.Increment(ref f.Calls);
        if (Interlocked.Exchange(ref f.DuringAt, null) is { } during) during();
        if (out_ == null || out_->Size < (uint)sizeof(Capabilities.CapRow)) return 1;
        lock (f._mu)
        {
            var names = f.SortedNames();
            if (index >= names.Length) return 2;
            *out_ = f.Fill(f.Visible(names[index])!, names[index]);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Find(NativeStr name, Capabilities.CapRow* out_)
    {
        var f = _current!;
        Interlocked.Increment(ref f.Calls);
        var n = name.ToString();
        lock (f._mu)
        {
            if (f.Visible(n) is not { } row) return 2;
            *out_ = f.Fill(row, n);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReportFn(ulong owner, NativeStr name, uint state, uint reason, NativeStr text)
    {
        var f = _current!;
        Interlocked.Increment(ref f.Calls);
        var n = name.ToString();
        if (owner == 0 || !Capabilities.ValidName(n) || state > 2 || reason > 9 || (state == 1) != (reason == 0)) return 1;
        lock (f._mu)
            if (state == 2 && f._coreNames.Contains(n)) return 1;
        f.Set(owner, n, state, reason, text.ToString(), owner == 777 ? Capabilities.OwnerId : $"owner:{owner}", true);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(ulong owner, delegate* unmanaged[Cdecl]<Capabilities.CapRow*, uint, void*, void> fn, void* user, ulong* handle)
    {
        var f = _current!;
        Interlocked.Increment(ref f.Calls);
        lock (f._mu)
        {
            f.SubscribedOwner = owner;
            f._subscribers.Add(((nint)fn, (nint)user));
            *handle = (ulong)f._subscribers.Count;
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(ulong handle)
    {
        Interlocked.Increment(ref _current!.Calls);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong Gen()
    {
        var f = _current!;
        Interlocked.Increment(ref f.Calls);
        lock (f._mu) return f.Generation;
    }
}

public sealed class CapabilitiesTests : KilnTest
{
    private readonly List<CapabilityChangedEvent> _changes = [];

    public CapabilitiesTests()
    {
        Capabilities.Reset();
        Events.On<CapabilityChangedEvent>(_changes.Add);
    }

    public override void Dispose()
    {
        Capabilities.Reset();
        base.Dispose();
    }

    private FakeCaps Native()
    {
        var caps = new FakeCaps(Backend);
        caps.Core("items.fuel", CapabilityState.Unavailable, CapabilityReason.SymbolMissing, "symbol items/FuelTick: find[0]: 0 matches");
        caps.Core("kiln.items", CapabilityState.Available, text: "v3", source: "core");
        caps.Core("net.kick", CapabilityState.Available);
        return caps;
    }

    // ------------------------------------------------------------------------------- reads --------------------

    [Fact]
    public void Is_Get_Why_and_All_read_the_native_rows()
    {
        using var caps = Native();
        Assert.False(Capabilities.Is("items.fuel"));
        Assert.True(Capabilities.Is("net.kick"));
        Assert.True(Game.HasFeature("kiln.items"));
        var fuel = Capabilities.Get("items.fuel");
        Assert.Equal(new Capability("items.fuel", CapabilityState.Unavailable, CapabilityReason.SymbolMissing,
            "symbol items/FuelTick: find[0]: 0 matches", "sigdb"), fuel);
        Assert.False(fuel.IsAvailable);
        Assert.Equal("items.fuel is unavailable: symbol items/FuelTick: find[0]: 0 matches (symbol-missing, from sigdb).",
            Capabilities.Why("items.fuel"));
        Assert.Equal("net.kick is available.", Capabilities.Why("net.kick"));
        Assert.Equal("kiln.items is available: v3.", Capabilities.Why("kiln.items"));
        Assert.Equal(["items.fuel", "kiln.items", "net.kick"], Capabilities.All.Select(r => r.Name));
        Assert.Equal(777ul, caps.SubscribedOwner);  // Kiln's own owner (FakeBackend.OwnerCreate)
    }

    [Fact]
    public void An_unknown_name_is_unavailable_with_reason_unknown_and_no_fallback_with_a_native_table()
    {
        using var caps = Native();
        Assert.False(Capabilities.Is("no.such"));
        Assert.Equal(new Capability("no.such", CapabilityState.Unavailable, CapabilityReason.Unknown,
            "no capability of this name is registered", ""), Capabilities.Get("no.such"));
        Assert.Equal("no.such is unavailable: no capability of this name is registered (unknown).", Capabilities.Why("no.such"));
        Assert.Throws<ArgumentNullException>(() => Capabilities.Is(null!));
    }

    [Fact]
    public void Is_does_not_call_the_native_table_once_the_snapshot_exists()
    {
        using var caps = Native();
        Assert.True(Capabilities.Is("net.kick"));
        var calls = caps.Calls;
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(Capabilities.Is("net.kick"));
            Assert.False(Capabilities.Is("no.such"));
            _ = Capabilities.Get("items.fuel");
        }
        Assert.Equal(calls, caps.Calls);
    }

    [Fact]
    public void Is_allocates_nothing()
    {
        using var caps = Native();
        for (var i = 0; i < 1000; i++) Capabilities.Is("net.kick");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Capabilities.Is("net.kick");
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    // ------------------------------------------------------------------------------- changes ------------------

    [Fact]
    public void The_snapshot_follows_native_changes_and_posts_one_event_per_row()
    {
        using var caps = Native();
        Assert.True(Capabilities.Is("net.kick"));
        caps.Core("tick", CapabilityState.Available);                                            // new
        caps.Core("net.kick", CapabilityState.Unavailable, CapabilityReason.Policy, "kiln.json"); // changed
        caps.Remove("items.fuel");                                                               // removed
        Assert.True(Capabilities.Is("tick"));
        Assert.False(Capabilities.Is("net.kick"));
        Assert.Equal(CapabilityReason.Unknown, Capabilities.Get("items.fuel").Reason);
        Assert.Empty(_changes);  // the bus delivers them on the tick thread
        Step();
        Assert.Equal(3, _changes.Count);
        var added = Assert.Single(_changes, e => e.Name == "tick");
        Assert.Equal(CapabilityReason.Unknown, added.Previous.Reason);
        Assert.True(added.Current.IsAvailable);
        var changed = Assert.Single(_changes, e => e.Name == "net.kick");
        Assert.True(changed.Previous.IsAvailable);
        Assert.Equal(CapabilityReason.Policy, changed.Current.Reason);
        var removed = Assert.Single(_changes, e => e.Name == "items.fuel");
        Assert.Equal(CapabilityReason.SymbolMissing, removed.Previous.Reason);
        Assert.Equal(CapabilityReason.Unknown, removed.Current.Reason);
    }

    [Fact]
    public void A_report_that_changes_nothing_posts_nothing()
    {
        using var caps = Native();
        Assert.True(Capabilities.Is("net.kick"));
        caps.Core("net.kick", CapabilityState.Available);
        Step();
        Assert.Empty(_changes);
    }

    [Fact]
    public void A_walk_that_sees_the_generation_move_walks_again()
    {
        using var caps = Native();
        caps.DuringAt = () => caps.Core("tick", CapabilityState.Available, notify: false);
        Assert.True(Capabilities.Is("tick"));
        Assert.Equal(2, caps.Walks);
        Assert.Equal(4, Capabilities.All.Count);
    }

    [Fact]
    public void Callbacks_from_other_threads_at_once_leave_the_last_state()
    {
        using var caps = Native();
        Assert.True(Capabilities.Is("net.kick"));
        // Four threads change rows at once; each change calls back on its own thread, outside the fake's lock.
        var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            for (var n = 0; n < 50; n++)
                caps.Core($"t{i}.row{n % 5}", n % 2 == 0 ? CapabilityState.Available : CapabilityState.Unavailable,
                    n % 2 == 0 ? CapabilityReason.None : CapabilityReason.Faulted);
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        Assert.Equal(3 + 20, Capabilities.All.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(Capabilities.Is($"t{i}.row3"));   // last written by n = 48: available
            Assert.False(Capabilities.Is($"t{i}.row4"));  // last written by n = 49: unavailable
        }
    }

    // ------------------------------------------------------------------------------- reports ------------------

    [Fact]
    public void Report_goes_to_the_native_table_with_Kilns_owner_and_is_read_back_at_once()
    {
        using var caps = Native();
        var mod = NewMod("reporter");
        As(mod, () => Capabilities.Report("items.moves", CapabilityState.Pending, CapabilityReason.Session, "no world is loaded yet"));
        var row = Capabilities.Get("items.moves");
        Assert.Equal(CapabilityState.Pending, row.State);
        Assert.True(row.SessionDependent);
        Assert.Equal(Capabilities.OwnerId, row.Source);  // Kiln's owner (777 in the fake backend), not the mod's
        Assert.Equal("items.moves is pending: no world is loaded yet (session, from capabilities).", Capabilities.Why("items.moves"));
        Capabilities.Report("items.moves", CapabilityState.Available);
        Assert.True(Capabilities.Is("items.moves"));
        Assert.True(Capabilities.Get("items.moves").SessionDependent);  // sticky
    }

    [Fact]
    public void A_report_cannot_turn_on_what_the_core_says_is_missing_but_can_turn_off()
    {
        using var caps = Native();
        Capabilities.Report("items.fuel", CapabilityState.Available);
        Assert.False(Capabilities.Is("items.fuel"));
        Assert.Equal("sigdb", Capabilities.Get("items.fuel").Source);
        Capabilities.Report("net.kick", CapabilityState.Unavailable, CapabilityReason.Faulted, "its hook could not be installed");
        Assert.Equal(CapabilityReason.Faulted, Capabilities.Get("net.kick").Reason);
    }

    [Fact]
    public void Report_refusals_say_which_rule()
    {
        using var caps = Native();
        Assert.Contains("1 to 96", Assert.Throws<ArgumentException>(() => Capabilities.Report("bad name", CapabilityState.Available)).Message);
        Assert.Contains("1 to 96", Assert.Throws<ArgumentException>(() => Capabilities.Report(new string('a', 97), CapabilityState.Available)).Message);
        Assert.Contains("1 to 96", Assert.Throws<ArgumentException>(() => Capabilities.Report("", CapabilityState.Available)).Message);
        Assert.Contains("never reported", Assert.Throws<ArgumentException>(() =>
            Capabilities.Report("items.x", CapabilityState.Unavailable, CapabilityReason.Unknown)).Message);
        Assert.Contains("reason None", Assert.Throws<ArgumentException>(() =>
            Capabilities.Report("items.x", CapabilityState.Available, CapabilityReason.Policy)).Message);
        Assert.Contains("needs a reason", Assert.Throws<ArgumentException>(() => Capabilities.Report("items.x", CapabilityState.Unavailable)).Message);
        Assert.Contains("not a capability state", Assert.Throws<ArgumentException>(() =>
            Capabilities.Report("items.x", (CapabilityState)7, CapabilityReason.Policy)).Message);
        // the core refuses pending for a name it reports itself
        var e = Assert.Throws<ArgumentException>(() => Capabilities.Report("items.fuel", CapabilityState.Pending, CapabilityReason.Session));
        Assert.Contains("items.fuel is a name the core reports itself", e.Message);
        Assert.Throws<ArgumentNullException>(() => Capabilities.Report(null!, CapabilityState.Available));
    }

    [Fact]
    public void A_long_text_is_cut_at_512_bytes_on_a_character_boundary()
    {
        Capabilities.Report("x.long", CapabilityState.Unavailable, CapabilityReason.Policy, new string('a', 511) + "é" + "tail");
        var text = Capabilities.Get("x.long").ReasonText;
        Assert.Equal(new string('a', 511), text);
    }

    // ------------------------------------------------------------------------------- no native table ----------

    [Fact]
    public void Without_a_table_rows_live_in_memory_with_the_same_rules()
    {
        Assert.False(Capabilities.Is("items.moves"));
        Capabilities.Report("items.moves", CapabilityState.Pending, CapabilityReason.Session, "no world is loaded yet");
        Assert.Equal("items.moves is pending: no world is loaded yet (session, from capabilities).", Capabilities.Why("items.moves"));
        Assert.True(Capabilities.Get("items.moves").SessionDependent);
        Capabilities.Report("items.moves", CapabilityState.Available);
        Assert.True(Capabilities.Is("items.moves"));
        Assert.True(Capabilities.Get("items.moves").SessionDependent);
        Assert.Contains("core reports itself", Assert.Throws<ArgumentException>(() =>
            Capabilities.Report("kiln.items", CapabilityState.Pending, CapabilityReason.Session)).Message);
        Step();
        Assert.Equal(2, _changes.Count);
        Assert.Equal(CapabilityReason.Unknown, _changes[0].Previous.Reason);
        Assert.Equal(CapabilityState.Available, _changes[1].Current.State);
    }

    [Fact]
    public void Without_a_table_a_name_with_no_row_asks_the_backend()
    {
        var backend = new FeatureBackend();
        Kiln.Backend.Current = backend;
        try
        {
            Assert.True(Capabilities.Is("tick"));
            Assert.True(Game.HasFeature("tick"));
            Assert.Equal(new Capability("tick", CapabilityState.Available, CapabilityReason.None, "", "backend"), Capabilities.Get("tick"));
            Assert.False(Capabilities.Is("items.fuel"));
            Assert.Equal(CapabilityReason.Unknown, Capabilities.Get("items.fuel").Reason);
            Assert.Empty(Capabilities.All);  // the fallback makes no rows
            Capabilities.Report("tick", CapabilityState.Unavailable, CapabilityReason.Policy, "off in kiln.json");
            Assert.False(Capabilities.Is("tick"));  // a row wins over the backend
        }
        finally
        {
            Kiln.Backend.Current = Backend;
        }
    }

    [Fact]
    public void Reset_drops_the_rows_and_the_native_state()
    {
        Capabilities.Report("x.mine", CapabilityState.Available);
        Assert.True(Capabilities.Is("x.mine"));
        using (var caps = Native())
        {
            Assert.False(Capabilities.Is("net.kick"));  // still the in-memory state: the table came after the first read
            Assert.True(Capabilities.Is("x.mine"));
            Capabilities.Reset();
            Assert.False(Capabilities.Is("x.mine"));
            Assert.True(Capabilities.Is("net.kick"));
            Assert.Equal(1, caps.Subscribers);
            Capabilities.Reset();
            Assert.True(Capabilities.Is("net.kick"));
            Assert.Equal(2, caps.Subscribers);
            caps.Core("tick", CapabilityState.Available);  // the first subscription's call is ignored, the second's walks
            Assert.True(Capabilities.Is("tick"));
        }
        Capabilities.Reset();
        Assert.False(Capabilities.Is("net.kick"));
    }

    [Fact]
    public void A_new_backend_starts_a_new_registry()
    {
        Capabilities.Report("x.mine", CapabilityState.Available);
        var other = new FakeBackend();
        Kiln.Backend.Current = other;
        try
        {
            Assert.False(Capabilities.Is("x.mine"));
        }
        finally
        {
            Kiln.Backend.Current = Backend;
        }
    }

    // ------------------------------------------------------------------------------- Services.Get<T> ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct Small { public ServiceHeader Header; public nint A; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Big { public ServiceHeader Header; public nint A, B, C, D; }

    private sealed unsafe class FakeTable : IDisposable
    {
        public readonly nint Pointer;
        private readonly FakeServiceRegistration _registration;

        public FakeTable(FakeBackend backend, string name, uint size, uint version)
        {
            Pointer = (nint)NativeMemory.AllocZeroed(64);
            *(ServiceHeader*)Pointer = new ServiceHeader { Size = size, Version = version };
            _registration = new FakeServiceRegistration(backend.Tables, name, Pointer);
        }

        public void Dispose()
        {
            _registration.Dispose();
            NativeMemory.Free((void*)Pointer);
        }
    }

    [Fact]
    public unsafe void A_table_smaller_than_the_callers_records_service_older_once()
    {
        using var caps = Native();
        caps.Core("kiln.ui", CapabilityState.Available, text: "v4", source: "core");
        using var table = new FakeTable(Backend, "kiln.ui", 16, 4);
        Assert.True(Capabilities.Is("kiln.ui"));
        var reports = caps.Calls;
        Assert.True(Services.Get<Big>("kiln.ui", 4) == null);
        var row = Capabilities.Get("kiln.ui");
        Assert.Equal(CapabilityReason.ServiceOlder, row.Reason);
        Assert.Equal($"kiln.ui v4 has a 16-byte table; this Kiln.Api needs {sizeof(Big)} bytes", row.ReasonText);
        var after = caps.Calls;
        for (var i = 0; i < 10; i++) Assert.True(Services.Get<Big>("kiln.ui", 4) == null);
        Assert.Equal(after, caps.Calls);  // recorded once, not on every call
        Assert.True(after > reports);
    }

    [Fact]
    public unsafe void A_null_with_an_available_row_is_a_version_below_the_minimum()
    {
        using var caps = Native();
        Assert.True(Services.Get<Small>("kiln.items", 4) == null);  // the fake backend has no kiln.items table
        Assert.Equal(new Capability("kiln.items", CapabilityState.Unavailable, CapabilityReason.ServiceOlder,
            "kiln.items is v3; this Kiln.Api needs v4", Capabilities.OwnerId), Capabilities.Get("kiln.items"));
    }

    [Fact]
    public unsafe void A_null_with_an_unavailable_row_adds_nothing()
    {
        using var caps = Native();
        caps.Core("kiln.net", CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln-net.dll is not installed", "core");
        Assert.True(Capabilities.Is("net.kick"));
        var calls = caps.Calls;
        Assert.True(Services.Get<Small>("kiln.net", 1) == null);
        Assert.Equal(calls, caps.Calls);
        Assert.Equal("kiln-net.dll is not installed", Capabilities.Get("kiln.net").ReasonText);
    }

    [Fact]
    public unsafe void A_null_with_no_row_is_service_missing_with_a_registry_and_offline_without()
    {
        using (var caps = Native())
        {
            Assert.True(Services.Get<Small>("kiln.gone", 2) == null);
            Assert.Equal(CapabilityReason.ServiceMissing, Capabilities.Get("kiln.gone").Reason);
            Assert.Equal("kiln.gone is not here (Kiln.Api needs v2)", Capabilities.Get("kiln.gone").ReasonText);
        }
        Capabilities.Reset();
        Assert.True(Services.Get<Small>("kiln.gone", 2) == null);
        Assert.Equal(new Capability("kiln.gone", CapabilityState.Unavailable, CapabilityReason.Offline,
            "no Kiln core in this process", Capabilities.OwnerId), Capabilities.Get("kiln.gone"));
        Capabilities.Report("kiln.gone", CapabilityState.Unavailable, CapabilityReason.Policy, "mine");
        Assert.True(Services.Get<Small>("kiln.gone", 2) == null);
        Assert.Equal("mine", Capabilities.Get("kiln.gone").ReasonText);  // once per name and cause; a row that is off stands
    }

    [Fact]
    public unsafe void A_failed_big_probe_first_does_not_poison_the_row_once_the_base_table_works()
    {
        using var caps = Native();
        caps.Core("kiln.ui", CapabilityState.Available, text: "v4", source: "core");
        using var table = new FakeTable(Backend, "kiln.ui", (uint)sizeof(Small), 4);
        Assert.True(Services.Get<Big>("kiln.ui", 1) == null);
        Assert.False(Capabilities.Is("kiln.ui"));
        Assert.True(Services.Get<Small>("kiln.ui", 1) != null);
        Assert.True(Capabilities.Is("kiln.ui"));
        Assert.Equal("core", Capabilities.Get("kiln.ui").Source);  // the report was taken back
        Assert.True(Services.Get<Big>("kiln.ui", 1) == null);
        Assert.True(Capabilities.Is("kiln.ui"));                    // and never lowered again
    }

    [Fact]
    public unsafe void A_failed_big_probe_after_the_base_table_changes_nothing()
    {
        using var caps = Native();
        caps.Core("kiln.ui", CapabilityState.Available, text: "v4", source: "core");
        using var table = new FakeTable(Backend, "kiln.ui", (uint)sizeof(Small), 4);
        Assert.True(Services.Get<Small>("kiln.ui", 1) != null);
        var calls = caps.Calls;
        Assert.True(Services.Get<Big>("kiln.ui", 1) == null);
        Assert.True(Capabilities.Is("kiln.ui"));
        Assert.Equal(calls, caps.Calls);
    }

    [Fact]
    public unsafe void Offline_the_probe_order_problem_is_the_same()
    {
        using var table = new FakeTable(Backend, "kiln.ui", (uint)sizeof(Small), 4);
        Assert.True(Services.Get<Big>("kiln.ui", 1) == null);
        Assert.Equal(CapabilityReason.ServiceOlder, Capabilities.Get("kiln.ui").Reason);
        Assert.True(Services.Get<Small>("kiln.ui", 1) != null);
        Assert.Equal(new Capability("kiln.ui", CapabilityState.Available, CapabilityReason.None, "v4", Capabilities.OwnerId),
            Capabilities.Get("kiln.ui"));
        Assert.True(Services.Get<Big>("kiln.ui", 1) == null);
        Assert.True(Capabilities.Is("kiln.ui"));
    }

    [Fact]
    public unsafe void Taking_a_report_back_never_undoes_a_services_own_report()
    {
        using var table = new FakeTable(Backend, "kiln.ui", (uint)sizeof(Small), 4);
        Capabilities.Report("kiln.ui", CapabilityState.Unavailable, CapabilityReason.Policy, "off in its config");
        Assert.True(Services.Get<Small>("kiln.ui", 1) != null);
        Assert.Equal("off in its config", Capabilities.Get("kiln.ui").ReasonText);
    }

    [Fact]
    public void Reason_codes_have_the_headers_names()
    {
        string[] names = ["none", "symbol-missing", "service-missing", "service-older", "wrong-side", "needs-kiln-client",
            "policy", "faulted", "offline", "session", "unknown"];
        for (var i = 0; i < names.Length; i++) Assert.Equal(names[i], Capabilities.CodeName((CapabilityReason)i));
        Assert.Equal("code-42", Capabilities.CodeName((CapabilityReason)42));
    }

    [Fact]
    public void The_attribute_keeps_its_name()
    {
        var attrs = typeof(Marked).GetMethod(nameof(Marked.Run))!.GetCustomAttributes(typeof(RequiresCapabilityAttribute), false)
            .Cast<RequiresCapabilityAttribute>().Select(a => a.Name).ToArray();
        Assert.Equal(["items.fuel", "kiln.items"], attrs.Order(StringComparer.Ordinal));
    }

    private static class Marked
    {
        [RequiresCapability("items.fuel")]
        [RequiresCapability("kiln.items")]
        public static void Run() { }
    }

    private sealed class FeatureBackend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => name == "tick";
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) => 0;
    }
}
