// SPDX-License-Identifier: MIT
// What Kiln's managed services share when they answer for capabilities (Kiln.Capabilities; docs/kiln/services/caps.md):
// reports that never throw, the cause of a row that is off, and the one exception a member throws when its capability
// is off. Kiln.Items and Kiln.Saves' InstanceData keep their own copies (dev.26); the services adopted in dev.27 use this.
namespace Kiln;

/// <summary>Reports and reads of capability rows for Kiln's managed services. Every member may be called on any thread.</summary>
internal static class ServiceCaps
{
    private static readonly object Gate = new();
    // What this process last reported per name, and under which registry binding: an unchanged row is not written again.
    private static readonly Dictionary<string, (long Epoch, CapabilityState State, CapabilityReason Reason, string Text)> Shown =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    /// <summary>Reports <paramref name="name"/> when it changed since this process last reported it. A report the
    /// registry refuses is logged once per name and never thrown: a row must not fail a service's start or a call.</summary>
    public static void Report(string name, CapabilityState state, CapabilityReason reason = CapabilityReason.None, string? text = null)
    {
        text ??= "";
        try
        {
            var epoch = Capabilities.CurrentBinding.Epoch;
            lock (Gate)
            {
                if (Shown.TryGetValue(name, out var was) && was == (epoch, state, reason, text)) return;
                Shown[name] = (epoch, state, reason, text);  // also when the report is refused: once, not at every refresh
            }
            Capabilities.Report(name, state, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            bool first;
            lock (Gate) first = Warned.Add(name);
            if (first) Backend.Current.Log(0, LogLevel.Warn, $"capabilities: {name} was not reported: {e.Message}");
        }
    }

    /// <summary>A row that is fixed once its service has started: available when <paramref name="on"/>, else
    /// unavailable with the cause (<see cref="Cause"/>: the first of <paramref name="needs"/> that is off, else
    /// <paramref name="reason"/> and <paramref name="text"/>).</summary>
    public static void Static(string name, bool on, CapabilityReason reason, string text, params string[] needs)
    {
        if (on)
        {
            Report(name, CapabilityState.Available);
            return;
        }
        var (r, t) = Cause(reason, text, needs);
        Report(name, CapabilityState.Unavailable, r, t);
    }

    /// <summary>A row that follows the session: available when <paramref name="on"/>; unavailable when one of
    /// <paramref name="needs"/> is off (its reason and text); pending otherwise, with <paramref name="waiting"/> as the
    /// text. A name that was reported pending once carries the session flag for good, so a row that is unavailable from
    /// the start (its service is missing) never claims to depend on the session.</summary>
    public static void Session(string name, bool on, string waiting, params string[] needs)
    {
        if (on)
        {
            Report(name, CapabilityState.Available);
            return;
        }
        if (FirstOff(needs) is { } off)
        {
            Report(name, CapabilityState.Unavailable, off.Reason, NeedsText(off));
            return;
        }
        Report(name, CapabilityState.Pending, CapabilityReason.Session, waiting);
    }

    /// <summary>Asks the backend for <paramref name="service"/>'s table again, so that <see cref="Services.Get{T}"/>
    /// records why it is missing under the registry binding of now. For a wrapper that looks its table up once per
    /// process (a <c>Lazy</c>, a "looked" flag): its row would otherwise be missing after the registry was reset. A
    /// probe is harmless: it never lowers a service some caller has found.</summary>
    /// <returns>True when a table of at least <paramref name="minVersion"/> is there.</returns>
    public static unsafe bool Probe(string service, uint minVersion = 1) =>
        Services.Get<ServiceHeader>(service, minVersion) != null;

    /// <summary>Lowers a row the core reports (a sigdb feature): unavailable, unless the row already says why it is off
    /// (then the core's reason stands).</summary>
    public static void Lower(string name, CapabilityReason reason, string text)
    {
        if (Capabilities.Get(name) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }) return;
        Report(name, CapabilityState.Unavailable, reason, text);
    }

    /// <summary>Why something is off: the first row of <paramref name="needs"/> that is off (its reason, and
    /// "needs &lt;row&gt;: &lt;its text&gt;"), else <paramref name="fallback"/> with <paramref name="detail"/>.</summary>
    public static (CapabilityReason Reason, string Text) Cause(CapabilityReason fallback, string detail, params string[] needs) =>
        FirstOff(needs) is { } off ? (off.Reason, NeedsText(off)) : (fallback, detail);

    /// <summary>The exception a member throws when <paramref name="name"/> is off: it names the capability. Its reason
    /// is <paramref name="detail"/> (what the member said before the contract, kept so that logs read as they did)
    /// followed by the row's own reason in brackets; the row's reason alone when there is no detail. A row that says
    /// it is available adds nothing: the member found it off before the row followed.</summary>
    public static KilnUnavailableException Unavailable(string name, string? detail = null, Exception? inner = null)
    {
        var row = Capabilities.Get(name);
        var why = row.State != CapabilityState.Available && row.Reason != CapabilityReason.Unknown && row.ReasonText.Length > 0
            ? row.ReasonText
            : null;
        var text = string.IsNullOrEmpty(detail) ? why
            : why is null || detail.Contains(why, StringComparison.Ordinal) ? detail
            : $"{detail} ({why})";
        return new KilnUnavailableException(name, text, inner);
    }

    /// <summary>Throws <see cref="Unavailable"/> for <paramref name="name"/> unless its row is available.</summary>
    /// <exception cref="KilnUnavailableException">The capability is off or pending.</exception>
    public static void Require(string name, string? detail = null)
    {
        if (!Capabilities.Is(name)) throw Unavailable(name, detail);
    }

    private static Capability? FirstOff(string[] needs)
    {
        foreach (var n in needs)
            if (Capabilities.Get(n) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } row)
                return row;
        return null;
    }

    private static string NeedsText(Capability off) =>
        off.ReasonText.Length > 0 ? $"needs {off.Name}: {off.ReasonText}" : $"needs {off.Name}";
}

/// <summary>One managed service's capability rows: reported at the service's first use, again when the registry was
/// reset (the test kit starts each harness with an empty one), and kept in step on the service tick when the service
/// has rows that follow the session. The service's static constructor makes it and calls <see cref="Ensure"/>; a member
/// that throws <see cref="KilnUnavailableException"/> calls <see cref="Ensure"/> first, so the row it names is there.</summary>
/// <param name="owner">The service's name for log lines and the tick's name (<c>kiln.spawn</c>).</param>
/// <param name="report">Reports every row of the service (through <see cref="ServiceCaps"/>). Any thread; it must not
/// touch game state.</param>
/// <param name="everySteps">0: the rows are fixed. Otherwise <paramref name="report"/> runs again every that many
/// steps of the service tick.</param>
internal sealed class ServiceCapRows(string owner, Action report, int everySteps = 0)
{
    private readonly object _gate = new();
    private long _epoch = -1;
    private IDisposable? _tick;
    private bool _warned;

    /// <summary>Reports the rows when they were not reported under the current registry binding yet. Cheap otherwise
    /// (one comparison). Never throws: it runs in static constructors.</summary>
    public void Ensure()
    {
        try
        {
            var epoch = Capabilities.CurrentBinding.Epoch;
            if (Volatile.Read(ref _epoch) == epoch) return;
            lock (_gate)
            {
                if (_epoch == epoch) return;
                Volatile.Write(ref _epoch, epoch);
                Run();
                if (everySteps > 0)
                {
                    // registered again under a new binding: a test's reset of the service tick dropped the old one
                    _tick?.Dispose();
                    _tick = ServiceTick.Add(owner + " capabilities", (in TickInfo t) =>
                    {
                        if (t.Step % (ulong)everySteps == 0) Refresh();
                    });
                }
            }
        }
        catch (Exception e)
        {
            Warn(e);
        }
    }

    /// <summary>Reports the rows again now (a state change the service noticed itself, or the tick). An unchanged row
    /// is not written. Never throws.</summary>
    public void Refresh()
    {
        try
        {
            lock (_gate)
            {
                Volatile.Write(ref _epoch, Capabilities.CurrentBinding.Epoch);
                Run();
            }
        }
        catch (Exception e)
        {
            Warn(e);
        }
    }

    private void Run()
    {
        try
        {
            report();
        }
        catch (Exception e)
        {
            Warn(e);
        }
    }

    // never fail a service's start (a type load), a call or the service tick for a row
    private void Warn(Exception e)
    {
        if (_warned) return;
        _warned = true;
        try
        {
            Backend.Current.Log(0, LogLevel.Warn, $"{owner}: its capability rows could not be decided: {e.Message}");
        }
        catch
        {
            // a backend that cannot log: nothing left to say
        }
    }
}
