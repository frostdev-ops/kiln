// SPDX-License-Identifier: MIT
// Guided client checks for tracked writes: what only a player in the real game can confirm, that a change made with
// World.Update reaches the player's own screen through the game's replication. The lab proves the save half
// (WorldWritesTestMod.cs); a simulated player has no inventory and no screen.
// Docs: docs/kiln/services/world.md, "Client checklist".
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace WorldWritesTest;

public sealed partial class WorldWritesTestMod
{
    private const string Area = "World writes", Since = "0.2.0-dev.27";

    private void RegisterClientChecks()
    {
        var n = ClientChecks.Register(this);
        Info($"{n} guided client check(s) registered");
    }

    private static bool HasPlayer(CheckContext c) =>
        c.Expect(!c.Player.IsNone && World.Exists(c.Player), "a player in game runs the check",
            c.Player.IsNone ? "no player (control folder)" : $"entity {c.Player}");

    private async Task<EntityId> GiveCan(CheckContext c)
    {
        var r = await Items.Give(c.Player, Items.Lookup(Can)!.Id, 1, options: GiveOptions.Notify | GiveOptions.NoMerge);
        await c.Ticks(2);
        var slot = r.Changes.Count > 0 ? Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.Slot == r.Changes[0].Slot) : null;
        var can = slot?.ItemEntity ?? EntityId.None;
        c.Expect(!can.IsNone, "a watering can was put into your backpack", r.Status.ToString());
        if (!can.IsNone) c.OnEnd(async () => { if (Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.ItemEntity == can) is { } s) await Items.TakeSlot(c.Player, s); });
        return can;
    }

    [ClientCheck("world.tracked-water", "A tracked write shows on your screen: a can's water", Area = Area, Since = Since,
        Tags = [Visual], Order = 10)]
    public async Task TrackedWater(CheckContext c)
    {
        if (!HasPlayer(c) || !c.Expect(World.SupportsTrackedWrites, "world.tracked_writes is available", Capabilities.Why(World.TrackedWritesCapability)))
        {
            await c.Verdict();
            return;
        }
        var can = await GiveCan(c);
        if (can.IsNone) { await c.Verdict(); return; }
        var full = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1]);
        c.Expect(full.Ok, "the can was filled with a tracked write", full.ToString());
        c.Instruct("A watering can was put into your backpack. Open the backpack and look at its water: it should be full (100 of 100). " +
                   "Keep the backpack open: in 15 seconds the water changes.");
        await c.Delay(TimeSpan.FromSeconds(15));
        var half = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1] / 2);
        c.Expect(half is { Ok: true, Changed: true, Kept: true }, "the can was set to half with a tracked write, and the game left it there", half.ToString());
        c.Log.Info($"{Me}: check {c.Id}: {half}");
        c.Instruct("The can's water was just set to half (50 of 100) by the server. Pass if the can in your backpack now shows half its " +
                   "water without your doing anything; fail if it still shows full, or only changes after you move or use it.");
        await c.Verdict();
    }

    [ClientCheck("world.tracked-water-rejoin", "A tracked write is still there after you leave and come back", Area = Area,
        Since = Since, Tags = [Visual], Order = 20)]
    public async Task TrackedWaterRejoin(CheckContext c)
    {
        if (!HasPlayer(c)) { await c.Verdict(); return; }
        var cans = Items.Read(c.Player)?.Slots.Where(s => s.Item == Items.Lookup(Can)!.Id && !s.ItemEntity.IsNone).ToList() ?? [];
        if (!c.Expect(cans.Count > 0, "you carry a watering can (keep the one from an earlier pass, or take one out of a chest)", $"{cans.Count} can(s)"))
        {
            await c.Verdict();
            return;
        }
        var can = cans[0].ItemEntity;
        var r = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 23 * 64);
        c.Expect(r.Ok, "the first can in your backpack was set to 23 charges with a tracked write", r.ToString());
        c.Instruct("The first watering can in your backpack was set to 23 of 100. Note the number, leave to the main menu, come back " +
                   "into the same world and look at the can again. (Run this check a second time afterwards only if you want to " +
                   "repeat it: it sets the can to 23 again.) Pass if the can showed 23 before you left and still shows 23 after you " +
                   "came back; fail with the two numbers you saw.");
        await c.Verdict();
    }
}
