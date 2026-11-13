// SPDX-License-Identifier: MIT
// The capability rows of item blocks' features (docs/kiln/services/caps.md): blocks.weapon_moves (a weapon's moves, a wand's and a staff cast's animation and
// hitAt) needs kiln.gamedata's sequence clones and item data; blocks.animated_held (a look's animated held model) needs kiln.gamedata's template clones and
// kiln.assets. Reported once, when the first item block compiles: a row a managed service registers exists only from its first use, so a manifest's
// features cannot require it. The state is decided from what this process has; it does not change while it runs.
namespace Kiln;

internal static class BlockCapabilities
{
    internal const string WeaponMoves = "blocks.weapon_moves", AnimatedHeld = "blocks.animated_held";

    /// <summary>A row: its state, reason and text.</summary>
    internal readonly record struct Row(CapabilityState State, CapabilityReason Reason, string? Text);

    /// <summary>The moves' row, from whether kiln.gamedata is here. Pure.</summary>
    internal static Row MovesRow(bool gamedata) => gamedata
        ? new Row(CapabilityState.Available, CapabilityReason.None, null)
        : new Row(CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.gamedata is not loaded: a move is a sequence clone with rules, and the item's slots are item data");

    /// <summary>The animated held model's row, from whether kiln.gamedata and kiln.assets are here. Pure.</summary>
    internal static Row HeldRow(bool gamedata, bool assets) => (gamedata, assets) switch
    {
        (true, true) => new Row(CapabilityState.Available, CapabilityReason.None, null),
        (false, _) => new Row(CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.gamedata is not loaded: an animated held model is a template clone"),
        _ => new Row(CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.assets is not loaded: it says whether the moving model and its graph are cooked here"),
    };

    /// <summary>Where the rows go: the capability table (tests set it).</summary>
    internal static Action<string, Row> Report = (name, row) => Capabilities.Report(name, row.State, row.Reason, row.Text);

    private static long _reported = -1;  // the registry binding they were reported under

    /// <summary>Reports both rows, once per registry binding (again after a reset, as a test kit's next harness has).</summary>
    internal static void Ensure()
    {
        var epoch = Capabilities.CurrentBinding.Epoch;
        if (Interlocked.Exchange(ref _reported, epoch) == epoch) return;
        try
        {
            var gamedata = Content.GameDataAvailable;
            Report(WeaponMoves, MovesRow(gamedata));
            Report(AnimatedHeld, HeldRow(gamedata, Assets.Available));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            // no capability table in this process (a tool, a test): the blocks still compile
            Interlocked.Exchange(ref _reported, -1);
        }
    }

    /// <summary>For tests: report again at the next <see cref="Ensure"/>.</summary>
    internal static void Rearm() => Interlocked.Exchange(ref _reported, 0);
}
