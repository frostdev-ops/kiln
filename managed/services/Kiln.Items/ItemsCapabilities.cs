// SPDX-License-Identifier: MIT
// Kiln.Items in the capability registry (Kiln.Capabilities; native/core/abi/services/kiln_caps.h). The service reports
// its rows when it starts (the first use of Items): what its table has and what the native side resolved is fixed from
// then on. The one row that follows the session, items.moves, is refreshed on the service tick, never in a getter.
namespace Kiln;

public static unsafe partial class Items
{
    /// <summary>The capability names Kiln.Items answers for: the sigdb feature where there is one, kiln.items for the
    /// table, items.&lt;thing&gt; for the rest.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row; Services.Get lowers it when the table is older).</summary>
        public const string Service = "kiln.items";

        /// <summary>Kiln.Items' own row: the table has peek_result (an older runtime's has not).</summary>
        public const string ResultPeek = "items.result_peek";

        /// <summary>sigdb: <see cref="Gear.Unlocked"/> and <see cref="Gear.GemSlot"/> at creation.</summary>
        public const string GearOptions = "items.pide_options";

        /// <summary>sigdb: water in containers (<see cref="Items.Fuel"/>).</summary>
        public const string Fuel = "items.fuel";

        /// <summary>sigdb: moves that keep item state.</summary>
        public const string Move = "items.move";

        /// <summary>sigdb: container names.</summary>
        public const string Names = "items.names";

        /// <summary>sigdb: the quick-stack and move-all hooks (<see cref="Items.Transfers"/>).</summary>
        public const string Transfer = "items.transfer";

        /// <summary>Kiln.Items' session row: moves run right now (the kiln_items program runs in a server world).</summary>
        public const string Moves = "items.moves";

        // sigdb rows the ones above need
        public const string Transaction = "items.transaction";
        public const string Read = "items.read";
        public const string Program = "items.program";
    }

    // Every Available property reads the registry through here, so Items' static constructor (the service's start,
    // which reports the rows) has run before the first read, also from a nested type (Fuel, Transfers).
    private static bool Can(string capability) => Capabilities.Is(capability);

    static Items()
    {
        try
        {
            ReportCapabilities();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.items: its capability rows were not reported: {e.Message}");
        }
    }

    private static IDisposable? _capabilityTick;
    private static (CapabilityState State, CapabilityReason Reason, string Text) _moves = ((CapabilityState)(-1), 0, "");

    // The rows that are fixed once the service has started. A sigdb row is only lowered, never raised.
    private static void ReportCapabilities()
    {
        var t = T;  // Services.Get puts the reason on kiln.items when the table is missing or older
        if (t == null)
        {
            var (reason, text) = Cause(CapabilityReason.ServiceMissing, "kiln.items is not loaded", Caps.Service);
            Capabilities.Report(Caps.ResultPeek, CapabilityState.Unavailable, reason, text);
            foreach (var name in (string[])[Caps.Fuel, Caps.Move, Caps.Names, Caps.Transfer, Caps.Moves]) Lower(name, reason, text);
            return;
        }
        var older = $"kiln.items v{t->Header.Version} has no {{0}} (update Kiln)";
        if (ResultTable != null) Capabilities.Report(Caps.ResultPeek, CapabilityState.Available);
        else Capabilities.Report(Caps.ResultPeek, CapabilityState.Unavailable, CapabilityReason.ServiceOlder, string.Format(older, "result peek"));
        if (V2 == null) Lower(Caps.Fuel, CapabilityReason.ServiceOlder, string.Format(older, "water functions"));
        var s3 = Status3();
        if (s3 == null)
        {
            foreach (var name in (string[])[Caps.Move, Caps.Names, Caps.Transfer, Caps.Moves])
                Lower(name, CapabilityReason.ServiceOlder, string.Format(older, "move, name or transfer functions"));
        }
        else
        {
            // kiln.items decided these when it started (items.cpp resolve()): names and the transfer hooks stay as they are
            if (!s3.Names) Off(Caps.Names, s3.Detail, Caps.Read);
            if (!s3.TransferHooks) Off(Caps.Transfer, s3.Detail, Caps.Transaction);
            RefreshMoves();
            _capabilityTick ??= ServiceTickAccess.Add("kiln.items capabilities", step =>
            {
                if (step % 25 == 0) RefreshMoves();
            });
        }
        // give, take, moves and water all run through the game's inventory transaction
        if (Capabilities.Get(Caps.Transaction) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } tx)
            foreach (var name in (string[])[Caps.Fuel, Caps.Move])
                Lower(name, tx.Reason, $"needs {Caps.Transaction}: {tx.ReasonText}");
    }

    // items.moves follows the kiln_items program: available while moves run, pending while it waits for a server world
    // (and again after one closed), unavailable when it broke or moves are off on this build.
    private static void RefreshMoves()
    {
        var s3 = Status3();
        var s = Status();
        (CapabilityState, CapabilityReason, string) now =
            s3 is { Moves: true } ? (CapabilityState.Available, CapabilityReason.None, "")
            : !Capabilities.Is(Caps.Move) || s is null or { Program: 0 or 4 }
                ? Unavailable(Cause(CapabilityReason.Faulted, s?.Detail ?? "", Caps.Move, Caps.Transaction, Caps.Program))
                : (CapabilityState.Pending, CapabilityReason.Session, s.Detail.Length > 0 ? s.Detail : "waiting for the server world");
        if (now == _moves) return;
        _moves = now;
        Capabilities.Report(Caps.Moves, now.Item1, now.Item2, now.Item3);
    }

    private static (CapabilityState, CapabilityReason, string) Unavailable((CapabilityReason Reason, string Text) c) =>
        (CapabilityState.Unavailable, c.Reason, c.Text);

    // Why something is off: the first row it needs that is off ("needs <row>: <its text>", its reason), else `fallback`.
    private static (CapabilityReason Reason, string Text) Cause(CapabilityReason fallback, string detail, params string[] needs)
    {
        foreach (var n in needs)
            if (Capabilities.Get(n) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } row)
                return (row.Reason, n == Caps.Service ? $"kiln.items is unavailable: {row.ReasonText}" : $"needs {n}: {row.ReasonText}");
        return (fallback, detail.Length > 0 ? detail : "kiln.items turned it off (see kiln.log)");
    }

    // `name` is off although its sigdb row may say it resolved: kiln.items' own checks said no.
    private static void Off(string name, string detail, params string[] needs)
    {
        var (reason, text) = Cause(CapabilityReason.Faulted, detail, needs);
        Lower(name, reason, text);
    }

    // Reports `name` unavailable unless its row already says why it is off (the core's reason stands).
    private static void Lower(string name, CapabilityReason reason, string text)
    {
        if (Capabilities.Get(name) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }) return;
        Capabilities.Report(name, CapabilityState.Unavailable, reason, text);
    }
}
