// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 0.2.0-dev.28, the coordinator's fix of 14 Nov (merged from fix/sigdb-inplace-patches): another loader that changes the
// game's code in place before Kiln's scan (Flame's Creative Mode 3.2.0, Greeneye's The Worst Map Mod) no longer sends the
// scan to the live code, where other engines' hooks hide functions. A player's game had 22 features off for it (items.*,
// chat.*, ui.screens, rules.*). Order 6900 (00-integration's block).
public sealed partial class ClientPassMod
{
    [ClientCheck("00-sigdb.in-place-patches", "Kiln's functions are found next to a loader that patched the game first",
        Area = "Other frameworks", Since = Dev28, Tags = [Frameworks], Order = 6900)]
    public async Task SigdbInPlacePatches(CheckContext c)
    {
        c.Expect(Capabilities.Is("items.transaction"), "items.transaction is available", Capabilities.Why("items.transaction"));
        c.Expect(Capabilities.Is("ui.screens"), "ui.screens is available", Capabilities.Why("ui.screens"));
        c.Instruct("Profile for this check: Creative Mode 3.2.0 with Flame's own loader in the game's chain, and Multi " +
                   "Planter 0.3.0 (both zips are in Downloads). 1) In kiln/logs/kiln.log, the line that starts with " +
                   "\"sigdb: scanning\" names the exe file's code, not \"the live code\"; a line \"... patched in place by " +
                   "another mod\" may follow it. 2) Plant a seedling with Multi Planter: does it grow into a grid? Pass when " +
                   "both hold; fail with the sigdb line otherwise.");
        await c.Verdict();
    }
}
