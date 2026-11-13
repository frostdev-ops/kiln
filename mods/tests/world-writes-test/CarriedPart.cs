// SPDX-License-Identifier: MIT
// A can a player character carries: the owner's case of 13 Nov 2026 (a can in the backpack; World.Update<Ecs.Fuel> to
// half answered "written over one step later" and the can stayed full). A spawned 1_Player stands in for the player
// (no client, no screen). Without a water refill on the character the tracked write of its can's Fuel is kept; with
// one (Items.Fuel.KeepFull on the character: the call a player's Water rule makes, the Creative menu's rule set among
// them, RulesRuntime.Water) kiln.items' refill writes the can back to full every step, and the answer names the refill
// (WorldWriteResult.WrittenOverBy).
using Kiln;

namespace WorldWritesTest;

public sealed partial class WorldWritesTestMod
{
    private static int FuelMax(EntityId e) => World.ReadBytes(e, "Fuel") is { Length: >= 16 } f ? BitConverter.ToInt32(f, 12) : -1;

    private async Task Carried(EntityId chest)
    {
        // a player character next to the base (its terrain is loaded), as items-test's equip part spawns one
        var at = World.Position(chest) is { } q ? new WorldPosition(q.X + 45, q.Y + 3, q.Z + 45) : new WorldPosition(3845, 835, 1480);
        var p = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        try
        {
            for (var i = 0; i < 120 && Items.Read(p) is not { Initialized: true }; i++) await Steps(1);
            if (!Check(!p.IsNone && World.Has(p, "PlayerInput") && Items.Read(p) is { Initialized: true },
                    $"carried: a player character {p} at {at} with an initialized inventory")) return;
            var give = await Items.Give(p, Items.Lookup(Can)!.Id, 1, options: GiveOptions.NoMerge);
            await Steps(2);
            var slot = give.Changes.Count > 0 ? Items.Read(p)?.Slots.FirstOrDefault(s => s.Slot == give.Changes[0].Slot) : null;
            var can = slot?.ItemEntity ?? EntityId.None;
            if (!Check(!can.IsNone && World.Has(can, "Fuel"), $"carried: a watering can in the character's backpack: {give.Status}, slot {slot?.Slot}, item entity {can}")) return;
            Info($"carried: the can after the give: Fuel {FuelUnits(can)} of {FuelMax(can)}, NetworkFuel {NetFuel(can)}");

            // 1. no refill on the character: the guided check's two writes
            var full = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1]);
            var half = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1] / 2);
            await Steps(3);
            Check(full.Ok && half is { Ok: true, Changed: true, Kept: true } && FuelUnits(can) == FuelMax(can) / 2,
                $"carried, no refill: none of the game's {half.GameWriterCount} Fuel writers wrote the carried can back: fill {full}; half {half}; " +
                $"three steps later Fuel {FuelUnits(can)}, NetworkFuel {NetFuel(can)}");

            // 2. a keep-full refill on the character's inventories: what the Creative menu's rule set had on in the owner's game
            var refilled = new List<FuelChange>();
            WorldWriteResult fill2, half2;
            using (var keep = Items.Fuel.KeepFull(p))
            {
                keep.Refilled += changes => refilled.AddRange(changes.Where(c => c.Container == can));
                await Steps(3);
                Info($"carried: a keep-full refill on {p}'s inventories (Items.Fuel.KeepFull, as a Water rule sets it): the can reads {FuelUnits(can)} of {FuelMax(can)}");
                refilled.Clear();
                fill2 = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1]);
                half2 = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1] / 2);
                await Steps(3);
                Info($"carried, refill on: fill {fill2}; half {half2}; three steps later Fuel {FuelUnits(can)}; the refill's own reports for the can: " +
                     $"[{string.Join(", ", refilled.Select(c => $"{c.Before}->{c.After}"))}]");
                Check(fill2.Status == WorldWriteStatus.Unchanged && half2 is { Ok: true, Changed: true, Kept: false } && FuelUnits(can) == FuelMax(can) &&
                      refilled.Any(c => c.Before == FuelMax(can) / 2 && c.After == FuelMax(can)),
                    $"carried, keep-full refill on the character (the owner's case): the fill finds the can full, the write to half is written " +
                    $"back to full one step later, and kiln.items' refill reports writing it ({refilled.Count} report(s)): {half2}");
                // the answer names the refill, not the game (WorldWriteResult.WrittenOverBy)
                Check(half2.WrittenOverBy is { } by && by.StartsWith("kiln.items' water refill ") && by.Contains($"(keep full) of the inventories of {p}") &&
                      !half2.ToString().Contains("by the game"),
                    $"carried, refill on: the answer names the refill that wrote over it: WrittenOverBy \"{half2.WrittenOverBy}\"");
            }

            // 3. the refill ended: the same write is kept again
            await Steps(3);
            var quarter = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = f.Values[1] / 4);
            await Steps(3);
            Check(quarter is { Ok: true, Changed: true, Kept: true, WrittenOverBy: null } && FuelUnits(can) == FuelMax(can) / 4,
                $"carried, the refill ended: the write is kept again: {quarter}; three steps later Fuel {FuelUnits(can)}");

            if (Items.Read(p)?.Slots.FirstOrDefault(s => s.ItemEntity == can) is { } s) await Items.TakeSlot(p, s);
        }
        finally
        {
            if (!p.IsNone && World.Exists(p)) Spawn.Destroy(p);
            await Steps(3);
        }
    }
}
