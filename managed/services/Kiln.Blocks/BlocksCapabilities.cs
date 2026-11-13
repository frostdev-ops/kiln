// SPDX-License-Identifier: MIT
// Kiln.Blocks' rows (docs/kiln/services/caps.md): blocks.weapon_moves and blocks.animated_held (BlockCapabilities, at the
// first item block's compile) and blocks.affixes, blocks.affixes.effects (AffixRuntime, following the session). Kiln.Blocks
// has no native table; its rows exist from its first use, so a manifest's features cannot require them. This set
// reports them again after a registry reset.
namespace Kiln;

internal static class BlocksRows
{
    internal static readonly ServiceCapRows Rows = new("kiln.blocks", Report);

    private static void Report()
    {
        ServiceCaps.Probe("kiln.npcai");  // Factions needs it: its row says whether it is here
        BlockCapabilities.Ensure();
        AffixRuntime.RefreshCapabilities();
    }
}
