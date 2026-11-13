// SPDX-License-Identifier: MIT
// The guided check of event filters: real hits from a player's swing through kiln.combat's filtered hold (the lab only
// drives Kiln's own game hits).
using System.Numerics;
using Kiln;
using Kiln.Testing;

namespace EventFilterTest;

public sealed partial class EventFilterTestMod
{
    // A hurt event's entities, for the check's log (the ledger keeps it): what each reported hit named.
    private static string Shape(EntityHurtEvent e) =>
        $"target {e.Target} part {e.Part} attacker {e.Attacker} source {e.Source} amount {e.Amount} flags {e.Flags} origin {e.Origin} impact 0x{e.ImpactId:x8} step {e.Step}";

    // EventFilter.ForEntity(doll) delivers the hits the doll takes part in: the ones it takes (Target, or Part for a weak
    // spot of it) and the ones it deals (Attacker or Source), as the API says (IEntityEvent.Involves). The first run in
    // a real game (13 Nov 2026) waited for any three events and wanted each one's Target to be the doll: the Vukah's own
    // attacks on the player came first (Target the player, Attacker the Vukah) and failed it before the player had hit it.
    [ClientCheck("events.filter-hits", "One watched creature: the hits it takes and deals are reported, the others' land untouched", Area = "Gameplay",
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
        c.Log.Info($"events.filter-hits: watched Vukah {doll}, the other {other}, you {c.Player}");
        var seen = new List<EntityHurtEvent>();
        using var sub = Events.On<EntityHurtEvent>(e =>
        {
            seen.Add(e);
            if (seen.Count <= 40) c.Log.Info($"events.filter-hits: hurt event {seen.Count}: {Shape(e)}");
        }, EventFilter.ForEntity(doll));
        var held0 = Combat.Status?.HitsHeld ?? 0;
        bool ItsOwn(EntityHurtEvent e) => e.Attacker == doll || e.Source == doll;  // an attack the watched Vukah dealt
        c.Instruct("Two Vukahs stand 6 m ahead of you (they take no damage). The left one is watched by a filtered handler: it is told " +
                   "of every hit that Vukah takes part in, the hits it takes and the hits it deals, so its attacks on you count too. " +
                   "Hit the right one three times, then the left one three times, with any weapon.");
        // counted by impact (one swing, one projectile): a swing that stays on a creature in god mode is reported in each
        // step it touches it (the lab: one sword swing on an invulnerable Vukah, three hurt events in three steps)
        int Swings() => seen.Where(e => !ItsOwn(e)).Select(e => e.ImpactId).Distinct().Count();
        await c.Until(() => Swings() >= 3, TimeSpan.FromSeconds(180), "three of your hits on the watched Vukah were reported");
        var heldNow = (Combat.Status?.HitsHeld ?? 0) - held0;
        var onIt = seen.Where(e => !ItsOwn(e)).ToList();
        var itsOwn = seen.Where(ItsOwn).ToList();
        string List(IEnumerable<EntityHurtEvent> l) => string.Join("; ", l.Take(6).Select(Shape));
        c.Log.Info($"events.filter-hits: {seen.Count} reported: {onIt.Count} on it ({Swings()} swings), {itsOwn.Count} of its own attacks; {heldNow} held");
        c.Expect(seen.All(e => e.Involves(doll)), "every reported hit was about the watched Vukah (its target, the part hit, its attacker or its source)",
            $"{seen.Count} reported");
        c.Expect(Swings() >= 3 && onIt.All(e => e.Target == doll && e.Attacker == c.Player),
            "your hits on the watched Vukah came with it as Target and you as Attacker", $"{onIt.Count} on it: {List(onIt)}");
        c.Expect(itsOwn.All(e => e.Attacker == doll && e.Target != doll),
            "its own attacks came with it as Attacker and what it hit (you) as Target", $"{itsOwn.Count} of its own: {List(itsOwn)}");
        var others = seen.Where(e => e.Involves(other) && e.Target != doll && e.Part != doll).ToList();
        c.Expect(others.Count == 0, "no hit of yours on the other Vukah and none of its attacks was reported", $"{others.Count}: {List(others)}");
        // one held record per reported hit; a swing of yours can be two records of one impact (reported once); a hit of
        // the other Vukah's held for nobody would add to it
        c.Expect(heldNow <= (ulong)(seen.Count + onIt.Count + 2), "only hits about the watched Vukah were held",
            $"{heldNow} held, {seen.Count} reported ({onIt.Count} on it, {itsOwn.Count} of its own)");
        c.Instruct("Did your hits on the right Vukah land as always (its hit reaction, the damage numbers), and did the hits on the left " +
                   "one look just the same? Nothing should feel delayed on either.");
        await c.Verdict();
    }
}
