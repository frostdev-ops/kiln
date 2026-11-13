// SPDX-License-Identifier: MIT
// items-test, kiln.items v5 (0.2.0-dev.27): Items.Equip and Items.Unequip on a player character spawned in the lab (no
// client: the character is the game's own 1_Player template, which has PlayerInput, SlotSelection and an inventory, so
// kiln_items_equip iterates it like a joined player's). Checks: unequip, equip into a named slot, a swap, the item's own
// slot, a dry run, Kiln.Players' PlayerEquipmentChangedEvent after each change, and every refusal leaving the inventory as
// it was (wrong slot type, an empty slot, another entity's slot, another item entity, nothing equipped, no free backpack
// slot, no player character, a dead player). The character is destroyed at the end.
// What a player's game shows (the item on the character and in the equipment screen, taking it off by hand) is the guided
// check in mods/tests/client-pass/Session1.cs.
using Kiln;

namespace ItemsTest;

public sealed partial class ItemsTestMod
{
    private const uint RingLight = 0x79442800;  // Ring_T3_Ring_of_Light
    private const uint RingShock = 0x4623bcb0;  // Ring_T6_Ring_of_Shock
    private const uint SwordCraft = 0x4c0d3e02; // Weapon_T1_1H_Sword_Craft

    private readonly List<PlayerEquipmentChangedEvent> _equipEvents = new();

    private static string Gear(EntityId p) =>
        Items.Read(p) is { } c ? string.Join(" ", c.Slots.Where(s => !s.IsEmpty).Select(s => $"{s.Slot}={s.Item.Value:x8}{(s.IsEquipped ? "*" : "")}")) : "unreadable";

    private static InventorySlot? FindSlot(EntityId p, uint item, bool equipped) =>
        Items.Read(p)?.Slots.FirstOrDefault(s => s.Item.Value == item && s.IsEquipped == equipped);

    private async Task<bool> EventFor(EntityId p, EquipmentSlot slot, Func<PlayerEquipmentChangedEvent, bool> match, int steps = 8)
    {
        for (var i = 0; i < steps; i++)
        {
            if (_equipEvents.Any(e => e.Player.Entity == p && (int)e.Slot == (int)slot && match(e))) return true;
            await Steps(1);
        }
        return false;
    }

    private async Task RunEquip(EntityId chest)
    {
        if (!Capabilities.Is("items.equip"))
        {
            Check(false, $"equip: capability items.equip is off: {Capabilities.Why("items.equip")}");
            return;
        }
        // a player character, as playerstate-test spawns one, near a base so its terrain is loaded
        var at = new WorldPosition(3845, 835, 1480);
        foreach (var a in World.Find("BaseIdComponent"))
            if (World.Position(a) is { } q) { at = new WorldPosition(q.X + 45, q.Y + 3, q.Z + 45); break; }
        var p = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = "items-test", Flags = SpawnFlags.Transient });
        await Steps(40);
        Check(World.Has(p, "PlayerInput") && Items.Read(p) is { Initialized: true },
            $"equip: spawned player character {p} at {at} has PlayerInput and an initialized inventory");
        if (!World.Has(p, "PlayerInput")) return;
        PlayerStates.Diagnostics.Watch(p);  // kiln.players follows the spawned character like a joined player
        using var sub = Events.On<PlayerEquipmentChangedEvent>(e => _equipEvents.Add(e));
        try
        {
            await EquipChecks(p, chest);
        }
        finally
        {
            PlayerStates.Diagnostics.Watch(p, false);
            Spawn.Destroy(p);
            await Steps(3);
        }
    }

    private async Task EquipChecks(EntityId p, EntityId chest)
    {
        // the game equips gear that fits an empty slot when it is given: give two rings and a sword
        await Items.Give(p, new ItemId(RingLight), 1);
        await Items.Give(p, new ItemId(RingShock), 1);
        await Items.Give(p, new ItemId(SwordCraft), 1);
        await Steps(6);
        Log.Info($"equip: player {p} after the gives: {Gear(p)}; ring0 {Items.Equipped(p, EquipmentSlot.Ring0)}, ring1 {Items.Equipped(p, EquipmentSlot.Ring1)}");

        // 1. unequip Ring0 (the game puts what it holds into a free backpack slot)
        var ring0Before = Items.Equipped(p, EquipmentSlot.Ring0);
        if (ring0Before.IsNone)
        {
            // nothing equipped by the give: put the light ring on first
            var light = FindSlot(p, RingLight, false);
            var put = light is null ? null : await Items.Equip(p, light.Slot, EquipmentSlot.Ring0);
            Log.Info($"equip: Ring0 was empty after the give; equipped the light ring first: {put}");
            await Steps(4);
            ring0Before = Items.Equipped(p, EquipmentSlot.Ring0);
        }
        Check(!ring0Before.IsNone, $"equip: Ring0 holds {ring0Before}");
        _equipEvents.Clear();
        var off = await Items.Unequip(p, EquipmentSlot.Ring0);
        Check(off.Ok && off.Unequipped && off.Item == ring0Before.Item && !off.To.IsNone && off.Reason == null,
            $"unequip Ring0: {off} (item entity {off.ItemEntity}, ref {off.Ref}, game error 0x{off.GameError:x2})");
        Check(Items.Equipped(p, EquipmentSlot.Ring0).IsNone, $"unequip: Ring0 is empty now ({Items.Equipped(p, EquipmentSlot.Ring0)})");
        Check(Items.Read(p)!.Slots.Any(s => s.Slot == off.To && s.Item == off.Item && !s.IsEquipped),
            $"unequip: the ring lies in {off.To} of the backpack, not equipped: {Gear(p)}");
        Check(await EventFor(p, EquipmentSlot.Ring0, e => e.Now.IsNone),
            $"unequip: PlayerEquipmentChangedEvent Ring0 -> none [{string.Join(",", _equipEvents.Select(x => $"{x.Slot}:{x.Before}->{x.Now}"))}]");

        // 2. equip it again into Ring0 (a named slot)
        _equipEvents.Clear();
        var on = await Items.Equip(p, off.To, EquipmentSlot.Ring0);
        Check(on.Ok && !on.Unequipped && on.Slot == EquipmentSlot.Ring0 && on.Item == off.Item && on.From == off.To && !on.To.IsNone,
            $"equip Ring0 from {off.To}: {on}");
        var now = Items.Equipped(p, EquipmentSlot.Ring0);
        Check(now.Item == off.Item && (now.IsNone || on.Ref.IsNone || now == on.Ref),
            $"equip: Ring0 holds the same item again ({now}; result ref {on.Ref})");
        Check(await EventFor(p, EquipmentSlot.Ring0, e => e.Now.Id == off.Item.Value),
            $"equip: PlayerEquipmentChangedEvent Ring0 none -> {off.Item} [{string.Join(",", _equipEvents.Select(x => $"{x.Slot}:{x.Before}->{x.Now}"))}]");

        // 3. a swap: the other ring into Ring0, the one there comes off where the game puts it
        var ring1 = Items.Equipped(p, EquipmentSlot.Ring1);
        if (!ring1.IsNone)
        {
            var r1off = await Items.Unequip(p, EquipmentSlot.Ring1);
            Check(r1off.Ok, $"swap setup: Ring1 off: {r1off}");
        }
        var other = FindSlot(p, on.Item.Value == RingLight ? RingShock : RingLight, false);
        if (other is null)
        {
            Check(false, $"swap: the second ring is in the backpack: {Gear(p)}");
        }
        else
        {
            _equipEvents.Clear();
            var swap = await Items.Equip(p, other.Slot, EquipmentSlot.Ring0);
            Check(swap.Ok && swap.Item == other.Item && swap.Replaced == on.Item && !swap.ReplacedTo.IsNone,
                $"swap into Ring0: {swap}; replaced {swap.Replaced} went to {swap.ReplacedTo}");
            Check(Items.Equipped(p, EquipmentSlot.Ring0).Item == other.Item &&
                  Items.Read(p)!.Slots.Any(s => s.Slot == swap.ReplacedTo && s.Item == on.Item),
                $"swap: Ring0 holds {Items.Equipped(p, EquipmentSlot.Ring0)}, the old ring lies in {swap.ReplacedTo}: {Gear(p)}");
            Check(await EventFor(p, EquipmentSlot.Ring0, e => e.Now.Id == other.Item.Value && e.Before.Id == on.Item.Value),
                $"swap: PlayerEquipmentChangedEvent Ring0 {on.Item} -> {other.Item}");

            // 4. the item's own slot (null): the game picks a free ring slot
            var loose = FindSlot(p, on.Item.Value, false);
            if (loose is not null)
            {
                var own = await Items.Equip(p, loose.Slot);
                Check(own.Ok && own.Slot is (EquipmentSlot.Ring1 or EquipmentSlot.Ring0) && own.Item == on.Item,
                    $"equip into the item's own slot: {own} (slot {own.Slot})");
            }
        }

        // 5. a dry run changes nothing
        var snap = Gear(p);
        var dry = await Items.Unequip(p, EquipmentSlot.Ring0, EquipOptions.DryRun);
        await Steps(2);
        Check(dry.Ok && dry.DryRun && Gear(p) == snap && !Items.Equipped(p, EquipmentSlot.Ring0).IsNone,
            $"dry-run unequip: {dry}; nothing changed");

        // 6. refusals: each leaves the inventory exactly as it was
        var sword = FindSlot(p, SwordCraft, false) ?? FindSlot(p, SwordCraft, true);
        snap = Gear(p);
        if (sword is not null)
        {
            var wrong = await Items.Equip(p, sword.Slot, EquipmentSlot.Ring1);
            await Steps(2);
            Check(!wrong.Ok && wrong.Status == ItemsStatus.Refused && wrong.GameError != 0 && Gear(p) == snap,
                $"refused: a sword into Ring1: {wrong} (game error 0x{wrong.GameError:x2}); inventory unchanged");
        }
        else
        {
            Check(false, $"refusal setup: the sword is in the inventory: {Gear(p)}");
        }
        var empty = Items.Read(p)!.Slots.FirstOrDefault(s => s.IsEmpty && !s.IsEquipped);
        if (empty is not null)
        {
            var none = await Items.Equip(p, empty.Slot, EquipmentSlot.Ring1);
            Check(!none.Ok && none.Status == ItemsStatus.SlotChanged && Gear(p) == snap, $"refused: an empty slot: {none}");
        }
        var chestSlot = Items.Read(chest)?.Slots.FirstOrDefault(s => !s.IsEmpty);
        if (chestSlot is not null)
        {
            var theirs = await Items.Equip(p, chestSlot.Slot, EquipmentSlot.Ring1);
            Check(!theirs.Ok && theirs.Status == ItemsStatus.SlotChanged && Gear(p) == snap,
                $"refused: a slot of container {chest}: {theirs}");
        }
        var ringSlot = Items.Read(p)!.Slots.FirstOrDefault(s => !s.IsEmpty && !s.IsEquipped && !s.ItemEntity.IsNone);
        if (ringSlot is not null)
        {
            var otherEntity = await Items.Equip(p, ringSlot.Slot, null, itemEntity: new EntityId(ringSlot.ItemEntity.Value + 1));
            Check(!otherEntity.Ok && otherEntity.Status == ItemsStatus.SlotChanged && Gear(p) == snap,
                $"refused: the slot holds another item entity: {otherEntity}");
        }
        var hookOff = Items.Equipped(p, EquipmentSlot.HookShot).IsNone;
        var nothing = await Items.Unequip(p, EquipmentSlot.HookShot);
        if (hookOff)
            Check(!nothing.Ok && nothing.Status == ItemsStatus.SlotChanged && Gear(p) == snap,
                $"refused: unequip of an empty slot (HookShot): {nothing}");
        var absent = await Items.Unequip(new EntityId(0x7FFFFFF0), EquipmentSlot.Ring0);
        Check(!absent.Ok && absent.Status == ItemsStatus.NoInventory, $"refused: no player character with that id: {absent}");
        await DeadCheck(p);
        await NoRoomCheck(p);
        Check(Gear(p) == snap, $"equip: the character ends the refusals as it started: {Gear(p)}");
    }

    // Kiln refuses a dead player (the game's equip has no such check). The lab sets the Dead state on the character for
    // one call and puts the state back.
    private async Task DeadCheck(EntityId p)
    {
        var actor = Reflected.Component(p, "Actor");
        var flag = GameTypes.Find("keen::actor::StateFlag")?.EnumValues.Where(v => v.Name == "Dead").Select(v => (long?)v.Value).FirstOrDefault();
        if (actor is null || flag is null || actor.Type.Field("currentState") is not { } f)
        {
            Log.Info("INFO dead check skipped: no Actor reflection here");
            return;
        }
        var before = Convert.ToUInt64(actor["currentState"]);
        try
        {
            actor["currentState"] = before | (1UL << (int)flag.Value);
            var dead = await Items.Unequip(p, EquipmentSlot.Ring0);
            Check(!dead.Ok && dead.Status == ItemsStatus.Refused && dead.Reason == "the player is dead",
                $"refused: a dead player: {dead} (state bit {flag} at +{f.Offset})");
        }
        catch (ArgumentException e)
        {
            Log.Info($"INFO dead check skipped: the state cannot be written here: {e.Message}");
        }
        finally
        {
            try
            {
                actor["currentState"] = before;
            }
            catch (ArgumentException)
            {
                // nothing was written above either
            }
        }
        await Steps(1);
    }

    // An unequip with no free backpack slot comes back NoRoom and changes nothing: fill the backpack, try, take it back.
    private async Task NoRoomCheck(EntityId p)
    {
        if (Items.Equipped(p, EquipmentSlot.Ring0).IsNone) return;
        var wood = new ItemId(0x2b0cf1d5);
        var free = Items.Read(p)!.FreeSlots();
        var fill = await Items.Give(p, wood, Math.Max(1, free) * 250, options: GiveOptions.NoMerge);
        await Steps(2);
        var snap = Gear(p);
        var full = await Items.Unequip(p, EquipmentSlot.Ring0);
        await Steps(2);
        Check(!full.Ok && full.Status is ItemsStatus.NoRoom && Gear(p) == snap,
            $"refused: unequip with a full backpack ({free} free slots filled: {fill.Status} {fill.Done}): {full}");
        var back = await Items.Take(p, wood, fill.Done);  // the result lists 16 slots at most: take the count back
        Check(back.Status == ItemsStatus.Done, $"no-room cleanup: {fill.Done} wood logs taken back: {back}");
        await Steps(2);
    }
}
