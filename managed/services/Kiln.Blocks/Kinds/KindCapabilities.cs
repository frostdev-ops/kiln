// SPDX-License-Identifier: MIT
// The capability rows of block kinds (docs/kiln/services/caps.md): blocks.kinds (registration), blocks.compile (pure
// compile to a frozen plan), blocks.apply (the dispatcher) and blocks.apply.<primary> for each built-in a plan may be made
// of. All are managed code only; they are available where blocks have something to compile to (kiln.content, kiln.gamedata
// and kiln.assets, as Blocks.Available), and the apply rows also need a build that can revoke a plan's owner: a plan that
// is refused part way is taken back whole, or it is not applied at all. Kiln.Blocks has no native table, so the rows exist
// from its first use. The state is decided from what this process has; it does not change while it runs.
namespace Kiln;

internal static class KindCapabilities
{
    internal const string Kinds = "blocks.kinds", Compile = "blocks.compile", Apply = "blocks.apply";

    internal static string ApplyRow(BuiltInBlockProductKind primary) => $"{Apply}.{primary.ToString().ToLowerInvariant()}";

    internal static readonly string[] ApplyRows = [Apply, .. Enum.GetValues<BuiltInBlockProductKind>().Select(ApplyRow)];

    private const string NoBlocks = "kiln.content, kiln.gamedata and kiln.assets are not all loaded: a block kind's plan is made of blocks they register";

    /// <summary>The row of kinds and compile, from whether blocks compile here. Pure.</summary>
    internal static BlockCapabilities.Row RowFor(bool blocks) => blocks
        ? new BlockCapabilities.Row(CapabilityState.Available, CapabilityReason.None, null)
        : new BlockCapabilities.Row(CapabilityState.Unavailable, CapabilityReason.ServiceMissing, NoBlocks);

    /// <summary>The apply rows, from whether blocks compile here and whether a plan's owner can be revoked. Pure.</summary>
    internal static BlockCapabilities.Row ApplyRowFor(bool blocks, bool revoke) => (blocks, revoke) switch
    {
        (false, _) => new BlockCapabilities.Row(CapabilityState.Unavailable, CapabilityReason.ServiceMissing, NoBlocks),
        (_, false) => new BlockCapabilities.Row(CapabilityState.Unavailable, CapabilityReason.ServiceOlder, "this Kiln cannot revoke a plan's owner, so a plan that is refused part way could not be taken back: no plan is applied"),
        _ => new BlockCapabilities.Row(CapabilityState.Available, CapabilityReason.None, null),
    };

    private static long _reported = -1;
    private static BlockCapabilities.Row _row = RowFor(false), _apply = ApplyRowFor(false, false);

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
        var apply = _apply = ApplyRowFor(blocks, KindApply.RevokeOwner != null);
        try
        {
            BlockCapabilities.Report(Kinds, row);
            BlockCapabilities.Report(Compile, row);
            foreach (var name in ApplyRows) BlockCapabilities.Report(name, apply);
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
        var row = name.StartsWith(Apply, StringComparison.Ordinal) ? _apply : _row;
        if (row.State != CapabilityState.Available) throw new KilnUnavailableException(name, row.Text);
        // Native policy/fault reports can hold an individual primary even when our managed prerequisites are present.
        var reported = Capabilities.Get(name);
        if (reported.State != CapabilityState.Available && reported.Reason != CapabilityReason.Unknown)
            throw new KilnUnavailableException(name, reported.ReasonText);
    }

    /// <summary>The dispatcher's row and the row of the plan's primary.</summary>
    internal static void RequireApply(BlockPlan plan)
    {
        Require(Apply);
        Require(ApplyRow(plan.Primary.Kind));
    }

    /// <summary>For tests: decide again at the next <see cref="Ensure"/>.</summary>
    internal static void Rearm() => Interlocked.Exchange(ref _reported, -1);
}
