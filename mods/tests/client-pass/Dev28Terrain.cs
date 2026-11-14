// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 19f-terrain: terrain overlays (Kiln.Terrain) as a player sees them. The fixture is the sample mods/samples/OreVein
// (mod id ore-vein): its /orevein command applies a mound and a vein next to the player. Orders 6300-6399
// (2000 + (28430 - 28000) * 10).
//
// The other four ids of chapter 19f need a cooked terrain pack or a primitive that does not exist; they are declared
// in docs/kiln/services/terrain.md ("Game tests") and have no check here: 19f-terrain-client and
// 19f-derived-companions wait for the tile qualification's fixture, 19f-base-exposure and 19f-outer-tile are deferred
// (no qualified primitive).
public sealed partial class ClientPassMod
{
    private const string OreVeinSample = "ore-vein";

    [ClientCheck("19f-overlay-client", "A terrain overlay as a hosting and a joined player sees it", Area = "Terrain", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Any, Order = 6300)]
    public async Task TerrainOverlayClient(CheckContext c)
    {
        if (!await Need(c, OreVeinSample) || !await NeedOpen(c, "terrain.overlays") || !await NeedPlayer(c)) return;
        c.Expect(Capabilities.Is("terrain.overlays"), "terrain.overlays is available here", Capabilities.Why("terrain.overlays"));
        c.Instruct("Stand on open, fairly flat ground away from any base, on a disposable world. Remember this exact starting position, including its height. Type /orevein in the chat. " +
                   "About 4 m from you (towards the east) a round mound of stone about 3 m high appears within a second or two; the chat " +
                   "says 'Completed' twice. Walk onto the mound: you stand on it and do not fall through. Dig into its middle " +
                   "with a pickaxe: a band of another material (the vein) is inside, and digging gives what that ground gives. " +
                   "Return to that exact starting position before typing /orevein again (the sample chooses its plan from your position): the chat says 'AlreadyApplied' and nothing changes, and the hole you dug " +
                   "is still there. Then leave to the main menu, load the world again and come back (on a dedicated server: " +
                   "restart the server and join again): the mound, the vein and your hole are as you left them. " +
                   "Was all of that so: the mound appeared, you could walk on it and dig it, the second /orevein changed nothing, " +
                   "and it all survived the reload?");
        await c.Verdict();
    }
}
