// SPDX-License-Identifier: MIT
// The capability rows of block kinds (docs/kiln/services/caps.md): blocks.kinds (registration) and blocks.compile (pure
// compile to a frozen plan) are managed code only and work wherever Kiln.Blocks does. Kiln.Blocks has no native table, so
// the rows exist from its first use. What a row says is decided here; the table is told.
namespace Kiln;

internal static class KindCapabilities
{
    internal const string Kinds = "blocks.kinds", Compile = "blocks.compile";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, BlockCapabilities.Row> Rows = new(StringComparer.Ordinal)
    {
        [Kinds] = new(CapabilityState.Available, CapabilityReason.None, null),
        [Compile] = new(CapabilityState.Available, CapabilityReason.None, null),
    };

    private static long _reported = -1;

    internal static BlockCapabilities.Row RowOf(string name) { lock (Gate) return Rows[name]; }

    /// <summary>Sets a row and tells the table.</summary>
    internal static void Set(string name, BlockCapabilities.Row row)
    {
        lock (Gate) Rows[name] = row;
        try { BlockCapabilities.Report(name, row); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>Reports the rows, once per registry binding.</summary>
    internal static void Ensure()
    {
        try
        {
            var epoch = Capabilities.CurrentBinding.Epoch;
            if (Interlocked.Exchange(ref _reported, epoch) == epoch) return;
            KeyValuePair<string, BlockCapabilities.Row>[] rows;
            lock (Gate) rows = [.. Rows];
            foreach (var (name, row) in rows) BlockCapabilities.Report(name, row);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            // no capability table in this process (a tool, a test)
            Interlocked.Exchange(ref _reported, -1);
        }
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming the row when it is not available.</summary>
    internal static void Require(string name)
    {
        Ensure();
        var row = RowOf(name);
        if (row.State != CapabilityState.Available) throw new KilnUnavailableException(name, row.Text);
    }
}
