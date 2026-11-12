// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// Session 2: the same world hosted again after a full restart of the game, with the content profile plus the cosmetic
// sample, and an overlay that hooks the graphics (ReShade on Windows, vkBasalt or MangoHud on Linux) switched on.
// Orders 201-299.
public sealed partial class ClientPassMod
{
    [ClientCheck("overlay.device-takeover", "Kiln draws next to a graphics overlay", Area = "Menus and hooks", Since = Dev24,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 201)]
    public Task OverlayDeviceTakeover(CheckContext c) => Outside(c,
        "This start runs with an overlay that hooks the graphics (ReShade, vkBasalt or MangoHud). Open Kiln's Mods page " +
        "(pause menu, Kiln) and the game's MODS page in the main menu next time you pass it. Kiln's windows draw over the " +
        "game and the Mods page lists the mods, it is not empty. Is the overlay working too, and does Kiln's Mods page list " +
        "the mods?");

    [ClientCheck("saves.kept", "What session 1 placed survives a restart", Area = "Gameplay", Since = Dev21,
        Needs = ClientCheckNeeds.Hosted, Order = 202)]
    public async Task SavesKept(CheckContext c)
    {
        if (!await Need(c, BlocksMore) || !await NeedPlayer(c)) return;
        c.Instruct("This is the world of session 1, after quitting the game and starting it again. Go to where you left the " +
                   "Lantern (check blocks.prop-model) and the chest of the quick stack check. Is the Lantern still there with " +
                   "its own model, and is the chest's content as you left it?");
        await c.Verdict();
    }

    [ClientCheck("cosmetics.frost-hood", "A hood with a model of its own in the vanity slot", Area = "Cosmetics", Since = Dev10,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 203)]
    public async Task CosmeticsFrostHood(CheckContext c)
    {
        if (!await Need(c, CosmeticSample) || !await NeedPlayer(c)) return;
        await GiveBlock(c, CosmeticSample, "frost-hood");
        c.Instruct("Put the Frost Hood into the head vanity slot and look at your character (inventory preview and in the " +
                   "world, turn the camera round). The hood sits on the head, moves with it, and your hair is hidden under " +
                   "it. Is the hood drawn on the head, with no hair poking through and no floating piece?");
        await c.Verdict();
    }

    [ClientCheck("cosmetics.cape", "A cape with a model of its own", Area = "Cosmetics", Since = Dev10,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 204)]
    public async Task CosmeticsCape(CheckContext c)
    {
        if (!await Need(c, CosmeticSample) || !await NeedPlayer(c)) return;
        await GiveBlock(c, CosmeticSample, "wander-cape");
        c.Instruct("Put the Wanderer's Cape into its vanity slot (if it is not in your inventory, the assertion below says " +
                   "why; it is also in the hand crafting menu). Walk, run and jump. Is the cape drawn on your back and does it move " +
                   "with you?");
        await c.Verdict();
    }
}
