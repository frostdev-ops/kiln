// SPDX-License-Identifier: MIT
// The capability rows of block kinds (docs/kiln/services/caps.md): blocks.kinds (registration) and blocks.compile (pure
// compile to a frozen plan). Both are managed code only; they are available where blocks have something to compile to
// (kiln.content, kiln.gamedata and kiln.assets), the same condition as Blocks.Available, and off with that reason where
// they have not (a tool, the test kit without those services). Kiln.Blocks has no native table, so the rows exist from
// its first use. The state is decided from what this process has; it does not change while it runs.
namespace Kiln;

internal static class KindCapabilities
{
    internal const string Kinds = "blocks.kinds", Compile = "blocks.compile";

    /// <summary>The row of both, from whether blocks compile here. Pure.</summary>
    internal static BlockCapabilities.Row RowFor(bool blocks) => blocks
        ? new BlockCapabilities.Row(CapabilityState.Available, CapabilityReason.None, null)
        : new BlockCapabilities.Row(CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.content, kiln.gamedata and kiln.assets are not all loaded: a block kind's plan is made of blocks they register");

    private static long _reported = -1;
    private static BlockCapabilities.Row _row = RowFor(false);

    /// <summary>Whether blocks compile here (tests set it).</summary>
    internal static Func<bool> BlocksAvailable = () => Blocks.Available;

    /// <summary>Decides and reports the rows, once per registry binding.</summary>
    internal static void Ensure()
    {
        long epoch;
        try { epoch = Capabilities.CurrentBinding.Epoch; }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { epoch = 0; }
        if (Interlocked.Exchange(ref _reported, epoch) == epoch && epoch != 0) return;
        bool blocks;
        try { blocks = BlocksAvailable(); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException) { blocks = false; }
        var row = _row = RowFor(blocks);
        try
        {
            BlockCapabilities.Report(Kinds, row);
            BlockCapabilities.Report(Compile, row);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            // no capability table in this process (a tool, a test): decided again at the next use
            Interlocked.Exchange(ref _reported, -1);
        }
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming the row when it is not available.</summary>
    internal static void Require(string name)
    {
        Ensure();
        var row = _row;
        if (row.State != CapabilityState.Available) throw new KilnUnavailableException(name, row.Text);
    }

    /// <summary>For tests: decide again at the next <see cref="Ensure"/>.</summary>
    internal static void Rearm() => Interlocked.Exchange(ref _reported, -1);
}
