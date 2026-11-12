// SPDX-License-Identifier: MIT
// A local stand-in for the core package's capability registry (out/core/SHAPE.md: Kiln.Capabilities). Same states,
// reason codes and values, row fields, "worst report wins", session flag. Internal on purpose: when Kiln.Capabilities is
// in the tree this folder goes, and ICapabilityRegistry's one user (KitBackend.Capabilities) talks to the real class
// through Capabilities.Report and Reset instead.
namespace Kiln.TestKit;

/// <summary>The state of a capability row (SHAPE.md <c>CapabilityState</c>).</summary>
internal enum CapState
{
    Unavailable = 0,
    Available = 1,
    /// <summary>Depends on the session (no world yet, world data not loaded).</summary>
    Pending = 2,
}

/// <summary>Why a row is not available (SHAPE.md <c>CapabilityReason</c>, same names and values).</summary>
internal enum CapReason
{
    None = 0,
    SymbolMissing = 1,
    ServiceMissing = 2,
    ServiceOlder = 3,
    WrongSide = 4,
    NeedsKilnClient = 5,
    Policy = 6,
    Faulted = 7,
    /// <summary>No game in this process: the test kit, a tool.</summary>
    Offline = 8,
    Session = 9,
    /// <summary>No row of this name (only <see cref="ICapabilityRegistry.Get"/> returns it).</summary>
    Unknown = 10,
}

/// <summary>One row (SHAPE.md <c>Capability</c>).</summary>
internal readonly record struct CapRow(string Name, CapState State, CapReason Reason, string ReasonText, string Source)
{
    /// <summary>The row was reported pending at some point (for good): a manifest's features may not name it.</summary>
    public bool SessionDependent { get; init; }

    public bool IsAvailable => State == CapState.Available;
}

/// <summary>What the kit needs from a capability registry. One implementation now (<see cref="OfflineCapabilities"/>);
/// after the core package lands, a thin one over <c>Kiln.Capabilities</c>.</summary>
internal interface ICapabilityRegistry
{
    bool Is(string name);
    CapRow Get(string name);
    string Why(string name);
    IReadOnlyList<CapRow> All { get; }

    /// <summary>A row changed: (previous, current). A previous with reason Unknown is a new row; a current with reason
    /// Unknown, a removed one. Raised outside the registry's lock, on the reporting thread.</summary>
    event Action<CapRow, CapRow>? Changed;

    /// <summary>Sets <paramref name="owner"/>'s one report for <paramref name="name"/> (replacing its earlier one).</summary>
    void Report(string owner, string name, CapState state, CapReason reason, string? text, string source);

    /// <summary>Removes <paramref name="owner"/>'s report for <paramref name="name"/>, if any.</summary>
    void Withdraw(string owner, string name);

    /// <summary>The row <paramref name="name"/> would show without <paramref name="owner"/>'s report.</summary>
    CapRow GetWithout(string owner, string name);
}

/// <summary>In-memory rows, one report per (owner, name); the row shown is the worst report: unavailable beats pending
/// beats available. Thread-safe.</summary>
internal sealed class OfflineCapabilities : ICapabilityRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Entry>> _reports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);
    private volatile Dictionary<string, CapRow> _rows = new(StringComparer.Ordinal);

    private sealed record Entry(string Owner, CapState State, CapReason Reason, string Text, string Source);

    public event Action<CapRow, CapRow>? Changed;

    public bool Is(string name) => _rows.TryGetValue(name, out var r) && r.IsAvailable;

    public CapRow Get(string name) => _rows.TryGetValue(name, out var r) ? r : Missing(name);

    public static CapRow Missing(string name) =>
        new(name, CapState.Unavailable, CapReason.Unknown, "no service or feature of this name is known in this process", "");

    public IReadOnlyList<CapRow> All => _rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();

    public string Why(string name) => Explain(Get(name));

    /// <summary>One sentence a mod could log.</summary>
    public static string Explain(CapRow row) => row.State switch
    {
        CapState.Available => $"{row.Name} is available{(row.ReasonText.Length > 0 ? $" ({row.ReasonText})" : "")}.",
        CapState.Pending => $"{row.Name} is pending ({ReasonName(row.Reason)}){(row.ReasonText.Length > 0 ? ": " + row.ReasonText : "")}.",
        _ when row.Reason == CapReason.Unknown => $"{row.Name} is unavailable: {row.ReasonText}.",
        _ => $"{row.Name} is unavailable ({ReasonName(row.Reason)}){(row.ReasonText.Length > 0 ? ": " + row.ReasonText : "")}.",
    };

    /// <summary>The reason as the capabilities file spells it (<c>offline</c>, <c>symbol-missing</c>).</summary>
    public static string ReasonName(CapReason reason) => reason switch
    {
        CapReason.SymbolMissing => "symbol-missing",
        CapReason.ServiceMissing => "service-missing",
        CapReason.ServiceOlder => "service-older",
        CapReason.WrongSide => "wrong-side",
        CapReason.NeedsKilnClient => "needs-kiln-client",
        _ => reason.ToString().ToLowerInvariant(),
    };

    public void Report(string owner, string name, CapState state, CapReason reason, string? text, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (state == CapState.Available) reason = CapReason.None;
        else if (reason == CapReason.None) throw new ArgumentException($"{name}: a row that is not available needs a reason", nameof(reason));
        if (reason == CapReason.Unknown) throw new ArgumentException("Unknown is never reported, it is the answer for a name with no row", nameof(reason));
        Change(name, list =>
        {
            list.RemoveAll(r => r.Owner == owner);
            list.Add(new Entry(owner, state, reason, text ?? "", source));
            if (state == CapState.Pending) _session.Add(name);
        });
    }

    public void Withdraw(string owner, string name) => Change(name, list => list.RemoveAll(r => r.Owner == owner));

    public CapRow GetWithout(string owner, string name)
    {
        lock (_lock)
            return _reports.TryGetValue(name, out var list) && Row(name, list.Where(r => r.Owner != owner).ToList()) is { } row ? row : Missing(name);
    }

    private void Change(string name, Action<List<Entry>> edit)
    {
        CapRow before, after;
        lock (_lock)
        {
            before = Get(name);
            if (!_reports.TryGetValue(name, out var list)) _reports[name] = list = [];
            edit(list);
            if (list.Count == 0) _reports.Remove(name);
            var rows = new Dictionary<string, CapRow>(_rows, StringComparer.Ordinal);
            if (Row(name, list) is { } row) rows[name] = row;
            else rows.Remove(name);
            _rows = rows;
            after = Get(name);
        }
        if (before != after) Changed?.Invoke(before, after);
    }

    // Worst report wins: unavailable, then pending, then available; among equals the first reporter.
    private CapRow? Row(string name, List<Entry> list)
    {
        if (list.Count == 0) return null;
        static int Rank(CapState s) => s switch { CapState.Unavailable => 0, CapState.Pending => 1, _ => 2 };
        var worst = list.OrderBy(r => Rank(r.State)).First();
        return new CapRow(name, worst.State, worst.Reason, worst.Text, worst.Source) { SessionDependent = _session.Contains(name) };
    }
}
