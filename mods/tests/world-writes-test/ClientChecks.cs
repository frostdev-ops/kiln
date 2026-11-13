// SPDX-License-Identifier: MIT
// Guided client checks for tracked writes: what only a player in the real game can confirm, that a change made with
// World.Update reaches the player's own screen through the game's replication. The lab proves the save half
// (WorldWritesTestMod.cs) and, on a spawned player character, that the game keeps a tracked write of a carried can's Fuel
// and that a water refill on the character writes it back (CarriedPart.cs); a lab has no client and no screen.
// Docs: docs/kiln/services/world.md, "Client checklist".
using System.Text.Json;
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace WorldWritesTest;

public sealed partial class WorldWritesTestMod
{
    private const string Area = "World writes", Since = "0.2.0-dev.27";
    private const int RejoinUnits = 23 * Items.Fuel.UnitsPerCharge;  // 23 of 100 charges

    private void RegisterClientChecks()
    {
        var n = ClientChecks.Register(this);
        Info($"{n} guided client check(s) registered");
    }

    private static bool HasPlayer(CheckContext c) =>
        c.Expect(!c.Player.IsNone && World.Exists(c.Player), "a player in game runs the check",
            c.Player.IsNone ? "no player (control folder)" : $"entity {c.Player}");

    // A water rule keeps every can the player carries at its level: kiln.items writes it back every step after the game's
    // programs, so a tracked write to half lasts one step (the first try on 13 Nov 2026 ran with the Creative menu's rule
    // set on; CarriedPart.cs shows it on a spawned character). Looked at before the scene is set.
    private static bool NoWaterRule(CheckContext c)
    {
        var rules = Rules.Of(c.Player);
        var sets = string.Join(", ", rules.From.Where(s => s.Active && s.Rules.Water is not null).Select(s => $"'{(s.Label.Length > 0 ? s.Label : s.Owner)}'"));
        if (c.Expect(rules.Water is null, "no water rule keeps the cans you carry full (the Creative menu's rule set has one)",
                rules.Water is null ? "none" : $"water {rules.Water} from rule set(s) {sets}")) return true;
        c.Instruct($"A water rule is on for you ({rules.Water}, from {sets}; Creative has one): it keeps every can you carry full, so the " +
                   "server's change to the can would be undone one step later. Switch Creative (or that rule set) off in Kiln's menu and run " +
                   "this check again; answer skip now.");
        return false;
    }

    private static int FlatSlot(CheckContext c, EntityId item) =>
        Items.Read(c.Player)?.Slots.ToList().FindIndex(s => s.ItemEntity == item) ?? -1;

    /// <summary>Gives a can into the player's backpack; <paramref name="takeBack"/>: the check takes it back when it ends.</summary>
    private async Task<EntityId> GiveCan(CheckContext c, bool takeBack = true)
    {
        var r = await Items.Give(c.Player, Items.Lookup(Can)!.Id, 1, options: GiveOptions.Notify | GiveOptions.NoMerge);
        await c.Ticks(2);
        var slot = r.Changes.Count > 0 ? Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.Slot == r.Changes[0].Slot) : null;
        var can = slot?.ItemEntity ?? EntityId.None;
        c.Expect(!can.IsNone, "a watering can was put into your backpack", r.Status.ToString());
        if (!can.IsNone && takeBack) c.OnEnd(() => TakeBack(c, can));
        return can;
    }

    private static async Task TakeBack(CheckContext c, EntityId can)
    {
        if (Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.ItemEntity == can) is { } s) await Items.TakeSlot(c.Player, s);
    }

    [ClientCheck("world.tracked-water", "A tracked write shows on your screen: a can's water", Area = Area, Since = Since,
        Tags = [Visual], Order = 10)]
    public async Task TrackedWater(CheckContext c)
    {
        if (!HasPlayer(c) || !c.Expect(World.SupportsTrackedWrites, "world.tracked_writes is available", Capabilities.Why(World.TrackedWritesCapability)) ||
            !NoWaterRule(c))
        {
            await c.Verdict();
            return;
        }
        var can = await GiveCan(c);
        if (can.IsNone) { await c.Verdict(); return; }
        var full = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1]);
        c.Expect(full.Ok && full.Kept != false, "the can was filled with a tracked write", full.ToString());
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

    // ------------------------------------------------------------------ across a leave to the menu and back ----
    // Two checks: the world closes when the player leaves to the menu, and with it this mod and a running check, so the
    // second half is a check of its own. The can is the first one's and stays in the backpack until the second ends.

    private sealed record RejoinPlan(uint Item, int Flat, int Units, string When);

    private string RejoinFile => Path.Combine(Context.DataDirectory, "rejoin-can.json");

    private RejoinPlan? LoadRejoin()
    {
        try { return File.Exists(RejoinFile) ? JsonSerializer.Deserialize<RejoinPlan>(File.ReadAllText(RejoinFile)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    /// <summary>The can a plan names: the item in its flat slot, else (the backpack was sorted) a can holding the plan's water.</summary>
    private static EntityId PlannedCan(CheckContext c, RejoinPlan plan)
    {
        var slots = Items.Read(c.Player)?.Slots ?? [];
        if (plan.Flat >= 0 && plan.Flat < slots.Count && slots[plan.Flat] is { ItemEntity.IsNone: false } s && s.Item.Value == plan.Item) return s.ItemEntity;
        return slots.FirstOrDefault(x => x.Item.Value == plan.Item && !x.ItemEntity.IsNone && FuelUnits(x.ItemEntity) == plan.Units)?.ItemEntity ?? EntityId.None;
    }

    [ClientCheck("world.tracked-water-rejoin", "A tracked write is still there after you leave and come back (1 of 2): a can at 23",
        Area = Area, Since = Since, Tags = [Visual], Order = 20)]
    public async Task TrackedWaterRejoin(CheckContext c)
    {
        if (!HasPlayer(c) || !c.Expect(World.SupportsTrackedWrites, "world.tracked_writes is available", Capabilities.Why(World.TrackedWritesCapability)) ||
            !NoWaterRule(c))
        {
            await c.Verdict();
            return;
        }
        // its own can: the one an earlier run left in the backpack, else a new one; it stays there across the leave
        var can = LoadRejoin() is { } earlier ? PlannedCan(c, earlier) : EntityId.None;
        if (can.IsNone) can = await GiveCan(c, takeBack: false);
        else c.Expect(true, "the can of an earlier run of this check is still in your backpack", $"item entity {can}");
        if (can.IsNone) { await c.Verdict(); return; }
        var r = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = RejoinUnits);
        c.Expect(r.Ok && r.Kept != false && FuelUnits(can) == RejoinUnits, "the can was set to 23 charges with a tracked write, and the game left it there", r.ToString());
        Directory.CreateDirectory(Context.DataDirectory);
        var plan = new RejoinPlan(Items.Lookup(Can)!.Id.Value, FlatSlot(c, can), RejoinUnits, DateTime.UtcNow.ToString("O"));
        File.WriteAllText(RejoinFile, JsonSerializer.Serialize(plan));
        c.Log.Info($"{Me}: check {c.Id}: {r}; the can {can} in flat slot {plan.Flat}");
        c.Instruct("A watering can in your backpack was set to 23 of 100 by the server. Look at it, then answer: pass if it shows 23. " +
                   "After that leave to the main menu, come back into the same world and run \"world.tracked-water-rejoin-after\": the can " +
                   "stays in your backpack until then.");
        await c.Verdict();
    }

    [ClientCheck("world.tracked-water-rejoin-after", "A tracked write is still there after you leave and come back (2 of 2): still 23",
        Area = Area, Since = Since, Tags = [Visual], Order = 21)]
    public async Task TrackedWaterRejoinAfter(CheckContext c)
    {
        if (!HasPlayer(c)) { await c.Verdict(); return; }
        if (LoadRejoin() is not { } plan)
        {
            c.Expect(false, "world.tracked-water-rejoin ran first (it leaves a can at 23 in your backpack)");
            await c.Verdict();
            return;
        }
        NoWaterRule(c);  // a water rule on now has already filled the can again: the number below says so
        var can = PlannedCan(c, plan);
        c.Expect(!can.IsNone, "the can is in your backpack again", can.IsNone ? $"no can in flat slot {plan.Flat} and none at {plan.Units} units" : $"item entity {can}, flat slot {FlatSlot(c, can)}");
        if (!can.IsNone)
        {
            c.Expect(FuelUnits(can) == plan.Units, "the server holds 23 charges on it after the rejoin", $"{FuelUnits(can)} units ({plan.Units} written {plan.When})");
            c.OnEnd(async () =>
            {
                await TakeBack(c, can);
                File.Delete(RejoinFile);
            });
        }
        c.Instruct("Look at the watering can in your backpack that the first part set to 23 of 100. Pass if it still shows 23; fail with " +
                   "the number you see. The can is taken back when you answer.");
        await c.Verdict();
    }
}
