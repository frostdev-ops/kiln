// SPDX-License-Identifier: MIT
// Event filters on a dedicated server, player-free (dev.27). BetterDummies' case: a mod watches one training doll's hits.
// With an entity filter kiln.combat holds only the hits that name the doll; every other hit of the world applies at
// once, as when nobody listens. Hits are Kiln's own game hits (Combat.Damage with AsGameHit: held and reported like the
// engine's, through the same hold). Prints "event-filter-test: PASS/FAIL/INFO" lines.
using System.Diagnostics;
using Kiln;

namespace EventFilterTest;

public sealed partial class EventFilterTestMod : Mod
{
    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();

    public override void OnLoad()
    {
        Log.Info($"event-filter-test: {Kiln.Testing.ClientChecks.Register(this)} guided check(s) registered");
        if (!Kiln.Client.IsClient) _ = Run();  // the automatic part: a dedicated (lab) server
    }

    public override void OnUnload()
    {
        foreach (var e in _spawned) try { Spawn.Destroy(e); } catch { }
    }

    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"event-filter-test: PASS {what}"); }
        else { _fail++; Log.Error($"event-filter-test: FAIL {what}"); }
        return ok;
    }

    private void Info(string s) => Log.Info($"event-filter-test: INFO {s}");
    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }
    private static readonly DamageOptions GameHit = new() { AsGameHit = true };
    private static ulong Held => Combat.Status?.HitsHeld ?? 0;

    private async Task Run()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(75);
            var altar = Spawn.FindByName("FlameAltar", max: 1).FirstOrDefault();
            if (!Check(!altar.IsNone && Spawn.Inspect(altar) is not null, $"anchor: flame altar {altar}")) return;
            var a = Spawn.Inspect(altar)!.Position;
            var vukah = Spawn.Lookup("Enemy_Vukah_Melee")!;
            async Task<EntityId> Make(double dx)
            {
                var e = await Spawn.Entity(vukah.Id, new WorldPosition(a.X + 40 + dx, a.Y + 0.5, a.Z), 0,
                    new SpawnOptions { Tag = "event-filter-test", Flags = SpawnFlags.Transient });
                _spawned.Add(e);
                return e;
            }
            var doll = await Make(0);
            var other = await Make(6);
            await Steps(4);
            Info($"doll {doll}, another creature {other}; status {Combat.Status}");

            // 1. one watched doll
            var dollHurt = new List<EntityHurtEvent>();
            var dollDamaged = new List<EntityDamagedEvent>();
            var hurtSub = Events.On<EntityHurtEvent>(e => dollHurt.Add(e), EventFilter.ForEntity(doll));
            var damagedSub = Events.On<EntityDamagedEvent>(e => dollDamaged.Add(e), EventFilter.ForEntity(doll));
            await Steps(3);  // the listener pushes the interest and the filter in its next Pre
            Check(Capabilities.Is("combat.hold_filter"), $"capability: {Capabilities.Why("combat.hold_filter")}");
            Check(Combat.Status is { Holding: true }, $"kiln.combat holds hits for the watcher: {Combat.Status?.Holding}");
            var held0 = Held;
            var h0 = Combat.GetHealth(other)!.Value.Current;
            var r1 = await Combat.Damage(other, 5, default, GameHit);
            var held1 = Held;
            Check(r1.Applied && Combat.GetHealth(other)!.Value.Current == h0 - 5, $"a hit on another creature applies: {r1}");
            Check(held1 == held0, $"and is not held: hits held {held0} -> {held1}");
            Check(dollHurt.Count == 0 && dollDamaged.Count == 0, $"and the doll's watcher hears nothing of it (hurt {dollHurt.Count}, damaged {dollDamaged.Count})");
            var d0 = Combat.GetHealth(doll)!.Value.Current;
            var r2 = await Combat.Damage(doll, 5, default, GameHit);
            await Steps(2);
            var held2 = Held;
            Check(r2.Applied && Combat.GetHealth(doll)!.Value.Current == d0 - 5, $"a hit on the doll applies: {r2}");
            Check(held2 == held1 + 1, $"and is held for the watcher: hits held {held1} -> {held2}");
            Check(dollHurt.Count == 1 && dollHurt[0].Target == doll, $"the doll's EntityHurtEvent arrives: {string.Join(", ", dollHurt.Select(e => $"{e.Target} {e.Amount}"))}");
            Check(dollDamaged.Count == 1 && dollDamaged[0].Target == doll, $"the doll's EntityDamagedEvent arrives, the other's not: {dollDamaged.Count}");

            // 2. a handler that changes the doll's hit still works through the filter
            var cut = Events.On<EntityHurtEvent>(e => e.Amount = 1, EventFilter.ForEntity(doll));
            await Steps(2);
            var d1 = Combat.GetHealth(doll)!.Value.Current;
            var r3 = await Combat.Damage(doll, 9, default, GameHit);
            await Steps(2);
            Check(Combat.GetHealth(doll)!.Value.Current == d1 - 1, $"a filtered handler changes the doll's hit (9 -> 1): {d1} -> {Combat.GetHealth(doll)!.Value.Current}, {r3}");
            cut.Dispose();

            // 3. the control: one handler without a filter holds every hit again
            var all = new List<EntityHurtEvent>();
            var allSub = Events.On<EntityHurtEvent>(e => all.Add(e));
            await Steps(3);
            var held3 = Held;
            var r4 = await Combat.Damage(other, 5, default, GameHit);
            await Steps(2);
            var held4 = Held;
            Check(held4 == held3 + 1 && all.Any(e => e.Target == other), $"control: with a handler without a filter the other creature's hit is held too: {held3} -> {held4}, {r4}");
            allSub.Dispose();
            await Steps(3);
            var held5 = Held;
            var r5 = await Combat.Damage(other, 5, default, GameHit);
            await Steps(2);
            Check(Held == held5, $"and once it is gone only the doll is watched again: {held5} -> {Held}, {r5}");

            // 4. nobody watching: nothing is held
            hurtSub.Dispose();
            damagedSub.Dispose();
            await Steps(3);
            Check(Combat.Status is { Holding: false }, $"with no hit handler left kiln.combat holds nothing: {Combat.Status?.Holding}");
        }
        catch (Exception e)
        {
            Check(false, $"threw: {e}");
        }
        finally
        {
            foreach (var e in _spawned) try { if (Spawn.Exists(e)) Spawn.Destroy(e); } catch { }
            Info($"summary: {_pass} PASS, {_fail} FAIL");
        }
    }
}
