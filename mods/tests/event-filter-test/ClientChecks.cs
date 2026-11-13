// SPDX-License-Identifier: MIT
// The guided check of event filters: real hits from a player's swing through kiln.combat's filtered hold (the lab only
// drives Kiln's own game hits).
using System.Numerics;
using Kiln;
using Kiln.Testing;

namespace EventFilterTest;

public sealed partial class EventFilterTestMod
{
    [ClientCheck("events.filter-hits", "One watched creature: its hits are reported, the others' land untouched", Area = "Gameplay",
        Since = "0.2.0-dev.27", Needs = ClientCheckNeeds.Hosted, Order = 1)]
    public async Task FilterHits(CheckContext c)
    {
        if (World.Transform(c.Player) is not { } t) { c.Instruct("This check needs your character in a hosted world. Press skip."); await c.Verdict(); return; }
        var vukah = Spawn.Lookup("Enemy_Vukah_Melee")!;
        WorldPosition Ahead(float right) { var o = Vector3.Transform(new Vector3(-right, 0, 6), t.Rotation); return new(t.Position.X + o.X, t.Position.Y, t.Position.Z + o.Z); }
        var doll = await Spawn.Entity(vukah.Id, Ahead(-2), 180);
        var other = await Spawn.Entity(vukah.Id, Ahead(2), 180);
        c.OnEnd(() => { Spawn.Destroy(doll); Spawn.Destroy(other); });
        // Spawn.Entity answers a few steps before the creature has its health (the lab part waits 4 steps): the first
        // run in a real game threw "Combat.SetInvulnerable: no such entity (or it has no health)" here
        if (!await c.Until(() => Combat.GetHealth(doll) != null && Combat.GetHealth(other) != null, TimeSpan.FromSeconds(10),
                "both Vukahs are in the world with their health"))
        {
            await c.Verdict();
            return;
        }
        Combat.SetInvulnerable(doll, true);
        Combat.SetInvulnerable(other, true);
        var seen = new List<EntityHurtEvent>();
        using var sub = Events.On<EntityHurtEvent>(e => seen.Add(e), EventFilter.ForEntity(doll));
        var held0 = Combat.Status?.HitsHeld ?? 0;
        var otherHealth = Combat.GetHealth(other)?.Current;
        c.Instruct("Two Vukahs stand 6 m ahead of you (they take no damage). The left one is watched by a filtered handler. Hit the " +
                   "right one three times, then the left one three times, with any weapon.");
        await c.Until(() => seen.Count >= 3, TimeSpan.FromSeconds(120), "three hits on the watched Vukah were reported");
        var heldNow = (Combat.Status?.HitsHeld ?? 0) - held0;
        c.Expect(seen.All(e => e.Target == doll), "every reported hit was on the watched Vukah", $"{seen.Count} reported");
        c.Expect(heldNow <= (ulong)seen.Count + 2, "only the watched Vukah's hits were held", $"{heldNow} held, {seen.Count} reported");
        c.Instruct("Did your hits on the right Vukah land as always (its hit reaction, the damage numbers), and did the hits on the left " +
                   "one look just the same? Nothing should feel delayed on either.");
        await c.Verdict();
    }
}
