// SPDX-License-Identifier: MIT
// Kiln.Blocks' rows (docs/kiln/services/caps.md): blocks.weapon_moves and blocks.animated_held (BlockCapabilities, at the
// first item block's compile) and blocks.affixes, blocks.affixes.effects (AffixRuntime, following the session). Kiln.Blocks
// has no native table; its rows exist from its first use, so a manifest's features cannot require them. This set
// reports them again after a registry reset.
namespace Kiln;

internal static class BlocksRows
{
    internal static readonly ServiceCapRows Rows = new("kiln.blocks", Report);

    // Factions' wrapper remembers kiln.npcai's miss: forgotten when the refresh finds the table
    static BlocksRows() => ServiceCaps.WhenFound("kiln.npcai", AiNative.LookAgain);

    private static void Report()
    {
        if (!ServiceCaps.Probe("kiln.npcai")) ServiceCaps.NoteMissing("kiln.npcai");  // Factions needs it: its row says whether it is here
        // BootDefinitions needs mods.boot, the core's row (sigdb and kiln.boot): with no core it has none, so it is reported here
        if (!ServiceCaps.Probe("kiln.boot"))
            ServiceCaps.Report("mods.boot", CapabilityState.Unavailable, CapabilityReason.Offline, "kiln.boot is not here (no Kiln core): no boot phase in this process");
        BlockCapabilities.Ensure();
        KindCapabilities.Ensure();
        AffixRuntime.RefreshCapabilities();
    }
}
