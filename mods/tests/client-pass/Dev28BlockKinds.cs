// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 16d-block-kinds (chapter 16d): a block of another mod's kind, used by a joined player whose game has neither the kind's
// provider nor the consumer. It gates no capability: a joined client runs no new code (the lowered item and recipe arrive
// as any definition does). Fixtures on the server: kinds-provider-test, kinds-consumer-test. Orders 4800-4899.
public sealed partial class ClientPassMod
{
    [ClientCheck("16d-external-kind-client", "Synced composition without provider code", Area = "Block kinds", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Joined, Order = 4800)]
    public async Task BlockKindsExternalKindClient(CheckContext c)
    {
        const string provider = "kinds-provider-test", consumer = "kinds-consumer-test";
        if (!await Need(c, provider, consumer)) return;
        if (!await NeedOpen(c, "blocks.apply", "blocks.apply.item")) return;

        // 1. the consumer's lantern is applied on the server (at most 120 s)
        BlockApplyResult app = new() { Reason = "the consumer has no block \"camp\"" };
        for (var i = 0; i < 240; i++)
        {
            if (Blocks.Find("camp", consumer) is { } h) app = Blocks.Application(h);
            if (app.Ok) break;
            await c.Delay(TimeSpan.FromMilliseconds(500));
        }
        c.Expect(app is { Ok: true, State: BlockApplyState.Applied }, $"the server applied the camp lantern ({app.State} {app.GameName} generation {app.Generation} {app.Reason})");
        if (!app.Ok) return;
        var item = Content.Items.Get(app.GameName);
        var recipes = Content.Recipes.Count(r => r.Name.StartsWith(app.GameName, StringComparison.Ordinal));
        c.Expect(item != null && recipes == 1, $"one item and one recipe of it are defined (item {item?.Id}, {recipes} recipe(s))");

        // 2. the joined player crafts and uses it; the client has no provider code
        c.Instruct("You are joined to the dedicated server with a Kiln client that has NEITHER kinds-provider-test NOR " +
                   "kinds-consumer-test in its profile (check the client's mod list). Open the normal crafting menu, find " +
                   "\"Camp lantern\" (2 twigs), craft one, look at it in the inventory (name, icon, durability 600), " +
                   "equip it and use it as a torch. Pass if all of that works and looks like a torch.");
        await c.Verdict();

        // 3. rejoin: the same definition, no second application
        c.Instruct("Leave the server and join it again. Check that the lantern you crafted is still in the inventory and " +
                   "the recipe is listed once. Then press continue.");
        await c.Verdict();
        var after = Blocks.Find("camp", consumer) is { } again ? Blocks.Application(again) : new BlockApplyResult { Reason = "the block is gone" };
        c.Expect(after.Ok && after.Generation == app.Generation, $"the application did not change over the rejoin (generation {app.Generation} -> {after.Generation} {after.Reason})");
        c.Expect(Content.Recipes.Count(r => r.Name.StartsWith(app.GameName, StringComparison.Ordinal)) == 1, "still one recipe");

        // 4. the provider takes its kinds away: what is applied stays
        Mods.Send(provider, "retire");
        await c.Delay(TimeSpan.FromSeconds(1));
        var kept = Blocks.Find("camp", consumer) is { } k ? Blocks.Application(k) : new BlockApplyResult { Reason = "the block is gone" };
        c.Expect(kept.Ok && kept.Generation == app.Generation && Content.Items.Get(app.GameName) != null, $"with the provider's kinds retired the lantern is still applied ({kept.State} {kept.Reason})");
        c.Instruct("The provider's kinds were taken away on the server. Use the lantern again and craft a second one. Pass " +
                   "if both still work.");
        await c.Verdict();

        // then the consumer goes: only what it owned goes with it
        var itemsBefore = Content.Items.Count;
        var unloaded = await Mods.UnloadAsync(consumer);
        await c.Delay(TimeSpan.FromSeconds(1));
        c.Expect(unloaded.Ok, $"the consumer unloaded ({unloaded.Message})");
        c.Expect(Blocks.Find("camp", consumer) == null, "its block is gone");
        c.Log.Info($"client-pass: {c.Id}: items {itemsBefore} -> {Content.Items.Count}; the lantern's item is {(Content.Items.Get(app.GameName) == null ? "gone" : "still listed")}");
        c.Instruct("The consumer mod was unloaded on the server. Look at the inventory: the lanterns must not crash the " +
                   "game or corrupt the inventory (Kiln shows a removed mod's item as a placeholder), the recipe must be " +
                   "gone from the crafting menu, and every other item and recipe must be as before. Pass if so.");
        await c.Verdict();
        Mods.Send(provider, "restore");

        // 5. a game without Kiln is refused
        c.Instruct("Last step: try to join this server with a game that has no Kiln (or ask a second person to). The join " +
                   "must be refused before the world loads. Pass if it was refused; skip if you cannot try it.");
        await c.Verdict();
    }
}
