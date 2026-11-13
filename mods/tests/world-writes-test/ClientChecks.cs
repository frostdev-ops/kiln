// SPDX-License-Identifier: MIT
// Guided client checks for tracked writes: what only a player in the real game can confirm, that a change made with
// World.Update reaches the player's own screen through the game's replication. The lab proves the save half
// (WorldWritesTestMod.cs) and, on a spawned player character, that the game keeps a tracked write of a carried can's Fuel
// and that a water refill on the character writes it back (CarriedPart.cs); a lab has no client and no screen. The rejoin
// checks' plan and matching are plain functions in RejoinPlan.cs (tested in managed/tests/Kiln.World.Tests).
// Docs: docs/kiln/services/world.md, "Client checklist".
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace WorldWritesTest;

public sealed partial class WorldWritesTestMod
{
    private const string Area = "World writes", Since = "0.2.0-dev.27";
    private const int RejoinUnits = 23 * Items.Fuel.UnitsPerCharge;  // 23 of 100 charges

    // The rejoin checks' mark on the can they give: instance data only they set, kept with the world's record of the
    // character (Persist.World), read back to find that can and no other.
    private sealed class CanMark
    {
        public string Plan { get; set; } = "";
    }

    private InstanceKey<CanMark>? _canMark;
    private readonly Guid _load = Guid.NewGuid();  // this load of the mod: a hosted world loads it anew each time it opens

    private void RegisterClientChecks()
    {
        try { _canMark = InstanceData.Key<CanMark>("rejoin-can"); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KilnUnavailableException) { Info($"no instance data key for the rejoin checks: {e.Message}"); }
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
    // second half is a check of its own. The first half gives a can and marks it with instance data (CanMark) that only it
    // sets; its plan (RejoinPlan.cs) is one file per world and character and names the session it ran in. The second half
    // judges only in another session, finds the can by the mark alone, and takes nothing when no item or more than one
    // carries it. A first half whose second half never runs leaves the can in the backpack (the player's to keep or throw
    // away) and its plan: the next first half in that world with that character uses the same can again.

    private string PlanFile(string world, string character) => Path.Combine(Context.DataDirectory, "rejoin", RejoinPlan.FileName(world, character));

    private RejoinPlan? LoadPlan(string world, string character)
    {
        try { return RejoinPlan.Parse(File.Exists(PlanFile(world, character)) ? File.ReadAllText(PlanFile(world, character)) : null); }
        catch (IOException) { return null; }
    }

    private static string? CharacterOf(EntityId player) =>
        CharacterData.KeyOf(player) is { IsNone: false } k ? $"{k.Account:x}-{k.Id:N}" : null;

    private string SessionOf(EntityId player) =>
        Players.Find(player) is { } p ? Rejoin.Session(_load, p.Handle, p.JoinedStep) : Rejoin.Session(_load, 0, 0);

    /// <summary>The cans the player carries, each with its mark (null: none).</summary>
    private List<(InventorySlot Slot, string? Mark)> CarriedCans(CheckContext c)
    {
        var can = Items.Lookup(Can)!.Id;
        return Items.Read(c.Player)?.Slots.Where(s => s.Item == can && !s.ItemEntity.IsNone)
            .Select(s => (s, !s.Ref.IsNone && s.Ref.TryGet(_canMark!, out var m) ? m.Plan : (string?)null)).ToList() ?? [];
    }

    /// <summary>The one carried can with <paramref name="mark"/>, or none and why.</summary>
    private (InventorySlot? Slot, string? Why) MarkedCan(CheckContext c, string mark)
    {
        var cans = CarriedCans(c);
        var (i, why) = Rejoin.Marked(cans.Select(x => x.Mark).ToList(), mark);
        return i < 0 ? (null, why) : (cans[i].Slot, null);
    }

    /// <summary>The world and character a plan is for, and that the mark can be read; false with the reason otherwise.</summary>
    private bool RejoinPossible(CheckContext c, out string world, out string character)
    {
        world = Saves.WorldId ?? "";
        character = CharacterOf(c.Player) ?? "";
        return c.Expect(_canMark is not null && Capabilities.Is("saves.instance_data"), "the can can be marked as this check's (instance data)",
                   Capabilities.Why("saves.instance_data")) &&
               c.Expect(world.Length > 0, "the world has an id (it was saved at least once)", world.Length > 0 ? world : "none yet: play a minute or leave and come back once") &&
               c.Expect(character.Length > 0, "your character is known (kiln.saves)", character.Length > 0 ? character : "unknown");
    }

    [ClientCheck("world.tracked-water-rejoin", "A tracked write is still there after you leave and come back (1 of 2): a can at 23",
        Area = Area, Since = Since, Tags = [Visual], Order = 20)]
    public async Task TrackedWaterRejoin(CheckContext c)
    {
        if (!HasPlayer(c) || !c.Expect(World.SupportsTrackedWrites, "world.tracked_writes is available", Capabilities.Why(World.TrackedWritesCapability)) ||
            !NoWaterRule(c) || !RejoinPossible(c, out var world, out var character))
        {
            await c.Verdict();
            return;
        }
        // the can of an earlier first half in this world with this character, found by its mark; else a new one, marked
        var earlier = LoadPlan(world, character);
        var (slot, why) = earlier is null ? (null, "no earlier run") : MarkedCan(c, earlier.Mark);
        var mark = earlier?.Mark ?? "";
        if (slot is not null) c.Expect(true, "the can an earlier run gave you is still in your backpack (by its mark)", $"item entity {slot.ItemEntity}");
        else
        {
            if (earlier is not null) c.Log.Info($"{Me}: check {c.Id}: the earlier run's can is not found ({why}); a new one is given");
            var can = await GiveCan(c, takeBack: false);
            slot = can.IsNone ? null : Items.Read(c.Player)?.Slots.FirstOrDefault(s => s.ItemEntity == can);
            mark = Guid.NewGuid().ToString("N");
            if (slot is { Ref.IsNone: false }) slot.Ref.Set(_canMark!, new CanMark { Plan = mark });
            var marked = slot is not null && MarkedCan(c, mark).Slot?.ItemEntity == can;
            if (!c.Expect(marked, "the can carries this check's mark", slot is null ? "no can" : slot.Ref.IsNone ? "the can has no instance (items.instances)" : "read back"))
            {
                if (slot is not null) await Items.TakeSlot(c.Player, slot);  // the can this run just gave, by its entity: not kept unmarked
                await c.Verdict();
                return;
            }
        }
        if (slot is null) { await c.Verdict(); return; }
        var r = await World.Update<Ecs.Fuel>(slot.ItemEntity, (ref Ecs.Fuel f) => f.Values[0] = RejoinUnits);
        c.Expect(r.Ok && r.Kept != false && FuelUnits(slot.ItemEntity) == RejoinUnits, "the can was set to 23 charges with a tracked write, and the game left it there", r.ToString());
        var plan = new RejoinPlan(mark, world, character, SessionOf(c.Player), RejoinUnits, DateTime.UtcNow.ToString("O"));
        Directory.CreateDirectory(Path.GetDirectoryName(PlanFile(world, character))!);
        File.WriteAllText(PlanFile(world, character), plan.ToJson());
        c.Log.Info($"{Me}: check {c.Id}: {r}; the can {slot.ItemEntity}, mark {mark}, plan {PlanFile(world, character)}");
        c.Instruct("A watering can in your backpack was set to 23 of 100 by the server. Look at it, then answer: pass if it shows 23. " +
                   "After that leave to the main menu, come back into the same world with the same character and run " +
                   "\"world.tracked-water-rejoin-after\", which takes the can back. If you never run it, the can stays in your backpack " +
                   "(yours to keep or throw away) and the next run of this check uses it again.");
        await c.Verdict();
    }

    [ClientCheck("world.tracked-water-rejoin-after", "A tracked write is still there after you leave and come back (2 of 2): still 23",
        Area = Area, Since = Since, Tags = [Visual], Order = 21)]
    public async Task TrackedWaterRejoinAfter(CheckContext c)
    {
        if (!HasPlayer(c) || !RejoinPossible(c, out var world, out var character)) { await c.Verdict(); return; }
        var plan = LoadPlan(world, character);
        var state = Rejoin.Judge(plan, world, character, SessionOf(c.Player));
        if (state != RejoinState.Ready)
        {
            c.Expect(false, state == RejoinState.SameSession
                    ? "you left to the main menu (or left the server) and came back since the first part"
                    : "world.tracked-water-rejoin ran first in this world with this character", state.ToString());
            c.Instruct(state == RejoinState.SameSession
                ? "Leave to the main menu and come back into this world first, then run this check again. Nothing was taken; answer skip now."
                : "Run \"world.tracked-water-rejoin\" first (in this world, with this character). Nothing was taken; answer skip now.");
            await c.Verdict();
            return;
        }
        NoWaterRule(c);  // a water rule on now has already filled the can again: the number below says so
        var (slot, why) = MarkedCan(c, plan!.Mark);
        if (slot is null)
        {
            c.Expect(false, "the can the first part gave you is in your backpack again (found by its mark)", $"{why}: nothing is taken");
            c.Instruct("Kiln did not find the first part's can by its mark, so nothing was taken from your backpack and the plan is kept. " +
                       "If you see the can the first part set to 23, pass if it still shows 23, fail with the number you see; otherwise skip.");
            await c.Verdict();
            return;
        }
        c.Expect(true, "the can the first part gave you is in your backpack again (found by its mark)", $"item entity {slot.ItemEntity}");
        c.Expect(FuelUnits(slot.ItemEntity) == plan.Units, "the server holds 23 charges on it after the rejoin", $"{FuelUnits(slot.ItemEntity)} units ({plan.Units} written {plan.When})");
        c.OnEnd(async () =>
        {
            // the mark again, at the end: exactly one item with it, or nothing is taken and the plan stays
            var (now, whyNot) = MarkedCan(c, plan.Mark);
            if (now is null)
            {
                c.Log.Info($"{Me}: check {c.Id}: nothing taken back ({whyNot}); the plan stays");
                return;
            }
            await Items.TakeSlot(c.Player, now);
            File.Delete(PlanFile(world, character));
        });
        c.Instruct("Look at the watering can in your backpack that the first part set to 23 of 100. Pass if it still shows 23; fail with " +
                   "the number you see. That can (and only that one) is taken back when you answer.");
        await c.Verdict();
    }
}
