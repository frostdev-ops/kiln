// SPDX-License-Identifier: MIT
// kiln.combat lab test, player-free. Prints "combat-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log.
// Spawns Vukah creatures 40 m from the base's flame altar (no player near: no AI, so creatures do not fight on their
// own) and drives everything through kiln.combat: health read/write against the replicated NetworkHealth, hits with
// an attacker (kill credit), EntityHurtEvent cancel/modify on both the synchronous path and the held game-hit path,
// god mode, deaths with and without a killer, what the game spawns at a creature's death, a respawn (lab switch
// watch-all), a stress run of thousands of hits and events, stamina/mana/body heat/oxygen get, set, add and clamp on
// spawned creatures that carry them (kiln.combat v2 attributes), and the release of the listener's interest.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kiln;

namespace CombatTest;

[StructLayout(LayoutKind.Sequential)]
public struct NetworkHealth
{
    public uint Health, HealthMax;
}

[StructLayout(LayoutKind.Sequential)]
public struct NetworkStamina
{
    public ushort Stamina, StaminaMax;
}

public sealed class CombatTestMod : Mod
{
    private enum HurtMode { None, Cancel, Double, Spare }

    private int _pass, _fail;
    private HurtMode _hurtMode;
    private EntityId _spareTarget;
    private readonly List<EntityHurtEvent> _hurt = new();
    private readonly List<EntityHealEvent> _heal = new();
    private readonly List<EntityDamagedEvent> _damaged = new();
    private readonly List<EntityDeathEvent> _deaths = new();
    private readonly List<PlayerRespawnEvent> _respawns = new();
    private readonly List<EntityDropsEvent> _drops = new();
    private bool _cancelDrops;
    private int _attrHurt;
    private readonly List<EventSubscription> _subs = new();
    private readonly List<EntityId> _spawned = new();

    public override void OnLoad() => _ = RunGuarded();

    // Registered by Kiln before OnLoad and removed when the mod unloads: it keeps hits held after the test, so the
    // native log shows the interest drop to 0 at `unload combat-test`.
    [Subscribe(EventPriority.Monitor)]
    private void CountHurt(EntityHurtEvent e) => _attrHurt++;

    public override void OnUnload()
    {
        foreach (var e in _spawned)
            try { Spawn.Destroy(e); } catch { }
    }

    private async Task RunGuarded()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            Info($"world running after {sw.Elapsed.TotalSeconds:F1} s: {Spawn.WorldRunning}");
            await Steps(75);
            await Run();
        }
        catch (Exception e)
        {
            Fail($"test threw: {e}");
        }
        finally
        {
            foreach (var e in _spawned)
                try { Spawn.Destroy(e); } catch { }
            _spawned.Clear();
        }
        Info($"summary: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Run()
    {
        var st = Combat.Status;
        Info($"status: {st}");
        if (!Check(Combat.Available, "kiln.combat health access is available")) return;
        Check(Combat.HitsAvailable, "kiln.combat hits (damage/kill) are available");
        Check(st is { Deaths: true }, "kiln.combat death events are available");

        var (anchor, what) = FindAnchor();
        if (!Check(anchor is not null, $"anchor: {what} at {anchor}")) return;
        var a = anchor!.Value;
        WorldPosition At(double dx, double dz) => new(a.X + 40 + dx, a.Y + 0.5, a.Z + dz);

        var vukah = Spawn.Lookup("Enemy_Vukah_Melee");
        if (!Check(vukah is not null, $"template Enemy_Vukah_Melee {vukah?.Id}")) return;
        async Task<EntityId> Make(double dx, double dz)
        {
            var e = await Spawn.Entity(vukah!.Id, At(dx, dz), 0, new SpawnOptions { Tag = "combat-test", Flags = SpawnFlags.Transient });
            _spawned.Add(e);
            return e;
        }
        var A = await Make(0, 0);
        var B = await Make(3, 0);
        var C = await Make(0, 6);
        var D = await Make(3, 6);
        Info($"spawned A {A}, B {B}, C {C}, D {D} at {At(0, 0)} (40 m from the anchor)");
        await Steps(4);  // init programs (health_init) run on new entities in the next step

        // ---------------------------------------------------------------- health read vs the replicated component
        var h = Combat.GetHealth(A);
        Check(h is { Current: > 0, Max: > 0, HasActor: true, Dead: false }, $"GetHealth(A) = {h}");
        var nh = ReadNetworkHealth(A);
        Check(nh is { } n0 && h is { } h0 && n0.Health == h0.Current && n0.HealthMax == h0.Max,
            $"NetworkHealth(A) = {nh?.Health}/{nh?.HealthMax} matches");
        Check(Combat.GetHealth(new EntityId(0x7ffffff0)) is null, "GetHealth of a missing entity is null");
        var prop = Spawn.FindByName("Prop_", max: 1).FirstOrDefault();
        if (!prop.IsNone) Info($"GetHealth of prop {prop} ({Spawn.Inspect(prop)?.TemplateName}): {Combat.GetHealth(prop)?.ToString() ?? "none"}");
        var offThread = await Task.Run(() =>
        {
            try { Combat.GetHealth(A); return "no exception"; }
            catch (InvalidOperationException) { return "InvalidOperationException"; }
        });
        await Context.Scheduler.NextTick();
        Check(offThread == "InvalidOperationException", $"GetHealth off the tick thread is refused ({offThread})");
        var max = h!.Value.Max;

        // ---------------------------------------------------------------- damage with an attacker, heal, set
        var r = await Combat.Damage(A, 10, B, new DamageOptions { Reaction = HitReaction.Light });
        var hA = Combat.GetHealth(A)!.Value;
        Check(r is { Applied: true } && r.HealthBefore - r.HealthAfter == 10 && hA.Current == r.HealthAfter,
            $"Damage(A, 10, attacker B): {r}; health now {hA}");
        await Steps(2);
        Check(ReadNetworkHealth(A)?.Health == (uint)hA.Current, $"NetworkHealth(A) follows: {ReadNetworkHealth(A)?.Health}");
        var healed = Combat.Heal(A, 5);
        Check(healed == 5 && Combat.GetHealth(A)!.Value.Current == hA.Current + 5, $"Heal(A, 5) added {healed}: {Combat.GetHealth(A)}");
        Check(Combat.Heal(A, 100000) == max - hA.Current - 5 && Combat.GetHealth(A)!.Value.Current == max, $"Heal clamps at max: {Combat.GetHealth(A)}");
        var set = Combat.SetHealth(A, max / 2);
        Check(set == max / 2 && Combat.GetHealth(A)!.Value.Current == max / 2, $"SetHealth(A, {max / 2}) -> {set}");
        await Steps(2);
        Check(ReadNetworkHealth(A)?.Health == (uint)(max / 2), $"NetworkHealth(A) after SetHealth: {ReadNetworkHealth(A)?.Health}");
        Combat.SetHealth(A, max);
        var heavy = await Combat.Damage(A, 1, B, new DamageOptions { Reaction = HitReaction.Heavy, ForceInterrupt = true, PushBack = 50 });
        Check(heavy.Applied, $"Damage with reaction Heavy + ForceInterrupt + push-back: {heavy}");
        var silent = await Combat.Damage(A, 1, B, new DamageOptions { Silent = true });
        Check(silent.Applied && silent.HealthBefore - silent.HealthAfter == 1, $"silent Damage (no HitEvent): {silent}");
        Combat.SetHealth(A, max);

        // ---------------------------------------------------------------- events: subscribe
        _subs.Add(Events.On<EntityHurtEvent>(OnHurt));
        _subs.Add(Events.On<EntityHealEvent>(e => _heal.Add(e)));
        _subs.Add(Events.On<EntityDamagedEvent>(e => _damaged.Add(e)));
        _subs.Add(Events.On<EntityDeathEvent>(e => _deaths.Add(e)));
        _subs.Add(Events.On<PlayerRespawnEvent>(e => _respawns.Add(e)));
        _subs.Add(Events.On<EntityDropsEvent>(e => { _drops.Add(e); if (_cancelDrops) e.Cancel(); }));
        await Steps(2);
        st = Combat.Status;
        Check(st is { Holding: true } && (st.Interest & 0x7E) == 0x7E, $"listener interest after subscribing: 0x{st?.Interest:x}, holding {st?.Holding}");

        // synchronous path (Combat.Damage fires EntityHurtEvent inside the call)
        _hurt.Clear(); _damaged.Clear();
        var before = Combat.GetHealth(A)!.Value.Current;
        r = await Combat.Damage(A, 10, B);
        Check(r.Applied && before - Combat.GetHealth(A)!.Value.Current == 10 && _hurt.Count == 1 && _hurt[0].Origin == CombatOrigin.Kiln && _hurt[0].Attacker == B,
            $"sync hurt: event seen ({_hurt.Count}, origin {_hurt.FirstOrDefault()?.Origin}, attacker {_hurt.FirstOrDefault()?.Attacker}), delta {before - Combat.GetHealth(A)!.Value.Current}");
        await Steps(1);
        Check(_damaged.Any(d => d.Target == A && d.Amount == 10 && d.Attacker == B && d.Origin == CombatOrigin.Kiln),
            $"EntityDamagedEvent after the hit: {string.Join("; ", _damaged.Select(d => $"{d.Target} -{d.Amount} by {d.Attacker} {d.HealthBefore}->{d.HealthAfter} {d.Origin}"))}");
        _hurtMode = HurtMode.Double;
        before = Combat.GetHealth(A)!.Value.Current;
        r = await Combat.Damage(A, 10, B);
        var delta = before - Combat.GetHealth(A)!.Value.Current;
        Check(r.Applied && delta == 20, $"sync hurt, handler doubles 10 -> delta {delta}");
        _hurtMode = HurtMode.Cancel;
        before = Combat.GetHealth(A)!.Value.Current;
        r = await Combat.Damage(A, 10, B);
        await Steps(2);
        delta = before - Combat.GetHealth(A)!.Value.Current;
        Check(r.State == HitState.Dropped && delta == 0, $"sync hurt, handler cancels -> {r.State}, delta {delta}");

        // held path: the hit enters the pending list like a game hit and is held for one step
        foreach (var (mode, want) in new[] { (HurtMode.None, 10), (HurtMode.Double, 20), (HurtMode.Cancel, 0) })
        {
            _hurtMode = mode;
            _hurt.Clear();
            before = Combat.GetHealth(A)!.Value.Current;
            var sw = Stopwatch.StartNew();
            r = await Combat.Damage(A, 10, B, new DamageOptions { AsGameHit = true });
            await Steps(2);
            delta = before - Combat.GetHealth(A)!.Value.Current;
            var ev = _hurt.FirstOrDefault();
            Check(delta == want && _hurt.Count == 1 && ev!.OriginalAmount == 10 && ev.Target == A && ev.Attacker == B,
                $"held game hit, handler {mode}: delta {delta} (want {want}), events {_hurt.Count} (amount {ev?.OriginalAmount}, " +
                $"health {ev?.HealthBefore}/{ev?.HealthMax}, step {ev?.Step}), result {r.State} after {r.Steps} step(s), {sw.ElapsedMilliseconds} ms");
        }
        _hurtMode = HurtMode.None;
        st = Combat.Status;
        Info($"status after the held hits: held {st?.HitsHeld}, released {st?.HitsReleased}, canceled {st?.HitsCanceled}, modified {st?.HitsModified}");

        // heal event on the synchronous path
        Combat.SetHealth(A, max - 20);
        _heal.Clear();
        var hh = Combat.Heal(A, 7);
        Check(hh == 7 && _heal.Count == 1 && _heal[0].Amount == 7, $"EntityHealEvent for Heal(A, 7): {_heal.Count} event(s), healed {hh}");

        // ---------------------------------------------------------------- god mode
        Combat.SetHealth(A, max);
        Combat.SetInvulnerable(A);
        Check(Combat.IsInvulnerable(A) && Combat.GetHealth(A)!.Value.Invulnerable, "SetInvulnerable(A) on");
        var godBefore = Combat.Status!.GodBlocked;
        r = await Combat.Damage(A, 50, B);
        Check(r.State == HitState.Dropped && Combat.GetHealth(A)!.Value.Current == max && Combat.Status!.GodBlocked == godBefore + 1,
            $"god mode drops a hit: {r.State}, health {Combat.GetHealth(A)}, blocked {Combat.Status!.GodBlocked - godBefore}");
        r = await Combat.Damage(A, 50, B, new DamageOptions { AsGameHit = true });
        await Steps(2);
        Check(Combat.GetHealth(A)!.Value.Current == max, $"god mode drops a held game hit: {r.State}, health {Combat.GetHealth(A)}");
        Combat.SetHealth(A, max - 40);  // a health loss that is not a hit (what fall damage does)
        await Steps(2);
        Check(Combat.GetHealth(A)!.Value.Current == max, $"god mode restores a non-hit health loss: {Combat.GetHealth(A)} (restored {Combat.Status!.GodRestored})");
        _deaths.Clear();
        var zero = Combat.SetHealth(A, 0);  // would die in the next step without god mode
        await Steps(4);
        Check(zero == 1 && Combat.GetHealth(A) is { Dead: false, Current: > 1 } && _deaths.Count(d => d.Victim == A) == 0,
            $"god mode: SetHealth(0) leaves {zero}, restored to {Combat.GetHealth(A)}, no death event ({_deaths.Count(d => d.Victim == A)})");
        Combat.SetInvulnerable(A, false);
        r = await Combat.Damage(A, 5, B);
        Check(r.Applied && !Combat.IsInvulnerable(A), $"god mode off: hits apply again ({r})");

        // ---------------------------------------------------------------- spare a lethal hit, then kill with credit
        Combat.SetHealth(A, 30);
        _hurtMode = HurtMode.Spare; _spareTarget = A;
        r = await Combat.Damage(A, 100, B, new DamageOptions { AsGameHit = true });
        await Steps(2);
        Check(Combat.GetHealth(A) is { Current: 1, Dead: false }, $"a handler lowers a lethal held hit (Lethal flagged) to leave 1 hp: {Combat.GetHealth(A)}");
        _hurtMode = HurtMode.None;

        Combat.SetHealth(A, max);
        var corpse = Spawn.Inspect(A)!.Position;
        var near0 = Census(corpse, 8);
        _deaths.Clear();
        var kill = await Combat.Kill(A, B);
        Check(kill is { Applied: true, KillingBlow: true, HealthAfter: <= 0 }, $"Kill(A, attacker B): {kill}");
        var killStep = _deaths.Count;
        var swD = Stopwatch.StartNew();
        var died = await Until(() => _deaths.FirstOrDefault(d => d.Victim == A), 40);
        Check(died is not null && died.Killer == B && died.Source == B && died.Origin == CombatOrigin.Kiln && (died.Flags & HitFlags.KillingBlow) != 0,
            $"EntityDeathEvent: victim {died?.Victim}, killer {died?.Killer}, source {died?.Source}, origin {died?.Origin}, " +
            $"player {died?.IsPlayer}, at {died?.Position}, step {died?.Step}, {swD.ElapsedMilliseconds} ms after the kill");
        Check(Combat.GetHealth(A) is { Dead: true }, $"A is dead: {Combat.GetHealth(A)}");
        await Steps(10);
        Check(_deaths.Count(d => d.Victim == A) == 1, $"one death event per death ({_deaths.Count(d => d.Victim == A)})");

        // Drops: a loot container that appears at the corpse is reported (the game's own loot needs a player near;
        // here a LootContainer_Enemy_* template is spawned 1 m from the corpse, as the death sequence would).
        var lootTpl = Spawn.Templates.FirstOrDefault(t => t.LoadedByGame && t.Name.StartsWith("LootContainer_Enemy_Vukah", StringComparison.Ordinal))
                      ?? Spawn.Templates.FirstOrDefault(t => t.LoadedByGame && t.Name.StartsWith("LootContainer_Enemy_", StringComparison.Ordinal));
        if (Check(lootTpl is not null, $"loot container template {lootTpl?.Name}"))
        {
            _drops.Clear();
            var bag = await Spawn.Entity(lootTpl!.Id, new WorldPosition(corpse.X + 1, corpse.Y, corpse.Z), 0,
                new SpawnOptions { Tag = "combat-test", Flags = SpawnFlags.Transient });
            _spawned.Add(bag);
            var drop = await Until(() => _drops.FirstOrDefault(d => d.Container == bag), 60);
            Check(drop is not null && drop.Victim == A && drop.Killer == B,
                $"EntityDropsEvent: victim {drop?.Victim}, container {drop?.Container} ({Spawn.Inspect(bag)?.TemplateName}), killer {drop?.Killer}, at {drop?.Position}");
            Check(Spawn.Inspect(bag) is not null, "an uncanceled drop stays");
            _cancelDrops = true;
            var bag2 = await Spawn.Entity(lootTpl.Id, new WorldPosition(corpse.X - 1, corpse.Y, corpse.Z + 1), 0,
                new SpawnOptions { Tag = "combat-test", Flags = SpawnFlags.Transient });
            _spawned.Add(bag2);
            var drop2 = await Until(() => _drops.FirstOrDefault(d => d.Container == bag2), 60);
            await Steps(2);
            Check(drop2 is not null && Spawn.Inspect(bag2) is null, $"a canceled drop is removed: event {drop2 is not null}, container exists {Spawn.Inspect(bag2) is not null}");
            _cancelDrops = false;
        }
        // What the game does after a creature's death (loot, corpse), seen from the entity map.
        for (var i = 0; i < 6; ++i)
        {
            await Steps(25);
            var now = Census(corpse, 8);
            var added = now.Keys.Except(near0.Keys).Select(k => $"{k} {now[k]}").ToList();
            var removed = near0.Keys.Except(now.Keys).Select(k => $"{k} {near0[k]}").ToList();
            Info($"t+{(i + 1) * 25 * 41 / 1000.0:F0}s near the corpse: {now.Count} entities (+{added.Count} {string.Join(", ", added.Take(8))}; -{removed.Count} {string.Join(", ", removed.Take(4))}); A exists {Spawn.Inspect(A) is not null}");
        }

        // ---------------------------------------------------------------- death without a hit
        _deaths.Clear();
        Combat.SetHealth(C, 0);
        var died2 = await Until(() => _deaths.FirstOrDefault(d => d.Victim == C), 40);
        Check(died2 is not null && died2.Killer.IsNone && died2.Origin == CombatOrigin.Other, $"death by SetHealth(0): killer {died2?.Killer}, origin {died2?.Origin}");

        // ---------------------------------------------------------------- respawn detection (lab switch watch-all)
        _deaths.Clear(); _respawns.Clear();
        await Combat.Kill(D, B);
        var died3 = await Until(() => _deaths.FirstOrDefault(d => d.Victim == D), 40);
        Check(died3 is not null && died3.Killer == B, $"Kill(D, B) reported: killer {died3?.Killer}");
        Combat.SetHealth(D, max);
        var back = await Until(() => _respawns.FirstOrDefault(x => x.Player == D), 40);
        Check(back is not null && back.Health == max && Combat.GetHealth(D) is { Dead: false },
            $"revived D (lab switch watch-all reports non-players): respawn event {back?.Player} health {back?.Health}/{back?.HealthMax} at {back?.Position}; D now {Combat.GetHealth(D)}");
        _deaths.Clear();
        await Combat.Kill(D, C);
        var died4 = await Until(() => _deaths.FirstOrDefault(d => d.Victim == D), 40);
        Check(died4 is not null && died4.Killer == C, $"a revived entity can die again: killer {died4?.Killer}");

        // ---------------------------------------------------------------- stress
        var targets = new List<EntityId> { B };
        for (var i = 0; i < 6; ++i) targets.Add(await Make(-6 - i * 2, 0));
        await Steps(4);
        foreach (var t in targets) Combat.SetHealth(t, 100000);
        _hurt.Clear(); _damaged.Clear();
        var hits = new List<Task<HitResult>>();
        var stepsRun = 0;
        var swS = Stopwatch.StartNew();
        for (var s = 0; s < 80; ++s)
        {
            for (var i = 0; i < 40; ++i)
                hits.Add(Combat.Damage(targets[(s + i) % targets.Count], 1, targets[(s + i + 1) % targets.Count],
                    new DamageOptions { AsGameHit = (i & 1) == 0 }));
            await Steps(1);
            ++stepsRun;
        }
        for (var i = 0; i < 200 && hits.Any(t => !t.IsCompleted); ++i) await Steps(1);
        await Steps(2);
        var applied = hits.Count(t => t.IsCompletedSuccessfully && t.Result.Applied);
        var pending = hits.Count(t => !t.IsCompleted);
        st = Combat.Status;
        Check(applied == hits.Count, $"stress: {hits.Count} hits over {stepsRun} steps ({swS.Elapsed.TotalSeconds:F1} s): {applied} applied, {pending} pending; " +
                                     $"{_hurt.Count} hurt + {_damaged.Count} damaged events; delivered total {st?.EventsDelivered}, " +
                                     $"deferred by the 4-impacts-per-target cap {st?.HitsDeferred}");
        Check(_damaged.Count >= applied && _hurt.Count == hits.Count, $"stress: one EntityHurtEvent per hit ({_hurt.Count}/{hits.Count}) and an EntityDamagedEvent per applied hit ({_damaged.Count})");

        // ---------------------------------------------------------------- attributes (kiln.combat v2)
        await AttributeTests(At);

        // ---------------------------------------------------------------- unsubscribe: interest released
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        await Steps(3);
        st = Combat.Status;
        Check(st is { Holding: true } && st.Interest == 0x2, $"after disposing the On<> handlers only the [Subscribe] one is left: interest 0x{st?.Interest:x}, holding {st?.Holding} ({_attrHurt} events seen by it)");
        Info($"final status: {st}");
    }


    // Stamina, mana, body heat and oxygen (and health through the same API) on creatures the game's registry has with
    // those components: Enemy_Vukah_Melee (health, stamina, oxygen), Player_Vukah_Melee (+ mana), AutomatedPlayer (+ body
    // heat). Which attributes a template carries is read back, not assumed; absent ones must read null.
    private async Task AttributeTests(Func<double, double, WorldPosition> at)
    {
        var all = new[] { GameAttribute.Health, GameAttribute.Stamina, GameAttribute.Mana, GameAttribute.BodyHeat, GameAttribute.Oxygen };
        Check(Attributes.Available, "attributes: available (resolved from the game's attribute resource)");
        foreach (var k in all)
        {
            var ids = Attributes.Ids(k);
            Check(ids is { CurrentId: not 0, MaxId: not 0, MinId: not 0, ComponentHash: not 0, GameClamp: true },
                $"attributes: ids of {k}: {(ids is { } i ? $"current 0x{i.CurrentId:X8}, min 0x{i.MinId:X8}, max 0x{i.MaxId:X8}, component 0x{i.ComponentHash:X8}, game clamp {i.GameClamp}" : "none")}");
        }
        // EMF's attribute ids (emf_api.h) are the game's: the resolved ones must be the same numbers.
        Check(Attributes.Ids(GameAttribute.Health) is { CurrentId: 0x8EB84995, MaxId: 0xDC8358BE } &&
              Attributes.Ids(GameAttribute.Stamina) is { CurrentId: 0x04B6AA8B, MaxId: 0xF443C410 } &&
              Attributes.Ids(GameAttribute.Mana) is { CurrentId: 0x6AA2574E, MaxId: 0x17536777 },
            "attributes: resolved health, stamina and mana ids equal EMF's constants");
        Check(Attributes.Ids((GameAttribute)99) is null && Attributes.Ids(0) is null, "attributes: an unknown kind has no ids");

        var stamina = Attributes.Ids(GameAttribute.Stamina)!.Value;
        var firstOfKind = new Dictionary<GameAttribute, EntityId>();
        var x = 14.0;
        foreach (var name in new[] { "Enemy_Vukah_Melee", "Player_Vukah_Melee", "AutomatedPlayer" })
        {
            var tpl = Spawn.Lookup(name);
            if (!Check(tpl is not null, $"attributes: template {name} {tpl?.Id}")) continue;
            EntityId e;
            try
            {
                e = await Spawn.Entity(tpl!.Id, at(x, -x), 0, new SpawnOptions { Tag = "combat-test", Flags = SpawnFlags.Transient });
            }
            catch (Exception ex)
            {
                Fail($"attributes: spawning {name}: {ex.Message}");
                continue;
            }
            x += 4;
            _spawned.Add(e);
            await Steps(6);  // the attribute components are set up by the game's init programs in the next steps
            var have = all.Where(k => Attributes.Get(e, k) is not null).ToList();
            Info($"attributes: {name} = {e} carries {string.Join(", ", have)}; lacks {string.Join(", ", all.Except(have))}");
            foreach (var k in all.Except(have))
            {
                Check(Attributes.Get(e, k) is null && !Attributes.TrySet(e, k, 1, out _) && !Attributes.TryAdd(e, k, 1, out _),
                    $"attributes: {name} has no {k}: Get is null, TrySet/TryAdd are false");
                Check(Throws(() => Attributes.Set(e, k, 1)), $"attributes: {name} has no {k}: Set throws");
            }
            foreach (var k in have)
            {
                firstOfKind.TryAdd(k, e);
                await AttributeCycle(name, e, k);
            }
            if (have.Contains(GameAttribute.Stamina)) await AttributeIdChecks(name, e);
        }
        foreach (var k in new[] { GameAttribute.Stamina, GameAttribute.Mana, GameAttribute.BodyHeat, GameAttribute.Oxygen })
            Check(firstOfKind.ContainsKey(k), $"attributes: a spawned creature carried {k} and it was set, added and read back");

        // not on the tick thread, and a missing entity
        var off = await Task.Run(() =>
        {
            try { Attributes.Get(new EntityId(1), GameAttribute.Stamina); return "no exception"; }
            catch (InvalidOperationException) { return "InvalidOperationException"; }
        });
        await Context.Scheduler.NextTick();
        Check(off == "InvalidOperationException", $"attributes: Get off the tick thread is refused ({off})");
        Check(Attributes.Get(new EntityId(0x7ffffff0), GameAttribute.Stamina) is null && !Attributes.TryAdd(new EntityId(0x7ffffff0), GameAttribute.Mana, 1, out _),
            "attributes: a missing entity reads null and TryAdd is false");
        var st = Combat.Status;
        Info($"attributes: done; kiln.combat status {st}");
        _ = stamina;
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    // Set, add, clamp and read back one attribute of one entity (health: the same API, delegated to SetHealth).
    private async Task AttributeCycle(string name, EntityId e, GameAttribute k)
    {
        var ids = Attributes.Ids(k)!.Value;
        var r0 = Attributes.Get(e, k)!.Value;
        var min = r0.Min ?? 0;
        var max = r0.Max ?? 0;
        Check(r0 is { Kind: var kind, IsCurrent: true, Max: > 0 } && kind == k && r0.Current >= min && r0.Current <= max && r0.Value == r0.Current && r0.CurrentId == ids.CurrentId && r0.MaxId == ids.MaxId,
            $"attributes: {name} {k} reads {r0.Current} in [{min}, {max}] (current id 0x{r0.CurrentId:X8}, max id 0x{r0.MaxId:X8})");
        var mid = min + (max - min) / 2;
        var set = Attributes.Set(e, k, mid);
        var back = Attributes.Get(e, k)!.Value;
        Check(set == mid && back.Current == mid && back.Max == max, $"attributes: {name} Set({k}, {mid}) -> {set}, reads back {back}");
        if (k == GameAttribute.Health)
            Check(Combat.GetHealth(e) is { } h && h.Current == mid && h.Max == max, $"attributes: {name} Health through Attributes is Combat's: {Combat.GetHealth(e)}");
        var add = Attributes.Add(e, k, 5);
        Check(add == Math.Min(mid + 5, max) && Attributes.Get(e, k)!.Value.Current == add, $"attributes: {name} Add({k}, 5) -> {add} (want {Math.Min(mid + 5, max)})");
        add = Attributes.Add(e, k, -3);
        Check(add == Math.Min(mid + 5, max) - 3 && Attributes.Get(e, k)!.Value.Current == add, $"attributes: {name} Add({k}, -3) -> {add}");
        var top = Attributes.Add(e, k, 100_000_000);
        Check(top == max && Attributes.Get(e, k)!.Value.Current == max, $"attributes: {name} Add({k}, +100000000) clamps to the maximum: {top} (max {max})");
        top = Attributes.Set(e, k, int.MaxValue);
        Check(top == max, $"attributes: {name} Set({k}, int.MaxValue) clamps to the maximum: {top}");
        if (k == GameAttribute.Health)
        {
            // below the minimum would kill it: only through god mode, which keeps 1 (Combat.SetHealth semantics)
            Combat.SetInvulnerable(e);
            var floor = Attributes.Set(e, k, int.MinValue);
            Check(floor == 1, $"attributes: {name} Set(Health, int.MinValue) on a god-mode entity leaves {floor}");
            Combat.SetInvulnerable(e, false);
            Attributes.Set(e, k, max);
        }
        else
        {
            var low = Attributes.Add(e, k, -100_000_000);
            Check(low == min && Attributes.Get(e, k)!.Value.Current == min, $"attributes: {name} Add({k}, -100000000) clamps to the minimum: {low} (min {min})");
            low = Attributes.Set(e, k, int.MinValue);
            Check(low == min, $"attributes: {name} Set({k}, int.MinValue) clamps to the minimum: {low}");
            Check(Attributes.TrySet(e, k, mid, out var tr) && tr.Current == mid && Attributes.TryAdd(e, k, 2, out var ta) && ta.Current == Math.Min(mid + 2, max),
                $"attributes: {name} TrySet / TryAdd({k}) return the pair after the write");
        }
        // what the game does with it afterwards: its own programs copy it for the clients (creatures: NetworkStamina,
        // NetworkHealth; players: NetworkPlayerAttributes). Regeneration may move it a little.
        var want = Attributes.Set(e, k, mid);
        await Steps(2);
        var now = Attributes.Get(e, k)!.Value.Current;
        if (k == GameAttribute.Stamina && World.TryRead<NetworkStamina>(e, "NetworkStamina", out var ns))
            Check(Math.Abs(ns.Stamina - now) <= Math.Max(2, max / 20) && ns.StaminaMax == Math.Min(max, ushort.MaxValue),
                $"attributes: {name} NetworkStamina follows: {ns.Stamina}/{ns.StaminaMax} (stamina {now} after 2 steps, set {want})");
        else if (k == GameAttribute.Health && World.TryRead<NetworkHealth>(e, "NetworkHealth", out var nh))
            Check(Math.Abs((int)nh.Health - now) <= 2, $"attributes: {name} NetworkHealth follows: {nh.Health}/{nh.HealthMax} (health {now})");
        else if (World.ReadBytes(e, "NetworkPlayerAttributes") is { Length: >= 0xB6 } npa)
        {
            var off = k switch { GameAttribute.Health => 0x84, GameAttribute.Stamina => 0x8C, GameAttribute.Mana => 0x94, GameAttribute.BodyHeat => 0x9C, _ => 0xB2 };
            var net = BitConverter.ToUInt16(npa, off);
            var netMax = BitConverter.ToUInt16(npa, off + 2);
            // the game's own network_player_attributes copy (regeneration moves the value by a step's worth)
            Check(Math.Abs(net - now) <= Math.Max(2, max / 20) && netMax == Math.Min(max, ushort.MaxValue),
                $"attributes: {name} NetworkPlayerAttributes.{k} follows: {net}/{netMax} (value {now} after 2 steps, set {want})");
        }
        else
            Info($"attributes: {name} {k}: no replicated copy to compare ({now} after 2 steps, set {want})");
        Attributes.Set(e, k, max);
    }

    // The same attributes by the game's id, and the refusals.
    private Task AttributeIdChecks(string name, EntityId e)
    {
        var ids = Attributes.Ids(GameAttribute.Stamina)!.Value;
        var cur = Attributes.Get(e, ids.CurrentId);
        var max = Attributes.Get(e, ids.MaxId);
        var min = Attributes.Get(e, ids.MinId);
        Check(cur is { IsCurrent: true } && max is { IsCurrent: false } && min is { IsCurrent: false } && max.Value.Value == cur.Value.Max && min.Value.Value == cur.Value.Min,
            $"attributes: {name} Get by id: current {cur?.Value}, max id reads {max?.Value}, min id reads {min?.Value}");
        Attributes.Set(e, ids.CurrentId, 7);
        Check(Attributes.Add(e, ids.CurrentId, 1) == 8 && Attributes.Get(e, GameAttribute.Stamina)!.Value.Current == 8, $"attributes: {name} Set and Add by the id of the current value");
        Check(Throws(() => Attributes.Set(e, ids.MaxId, 5)) && Throws(() => Attributes.Add(e, ids.MinId, 1)),
            $"attributes: {name} Set/Add on the maximum or minimum id is refused (the game computes them)");
        Check(Throws(() => Attributes.Get(e, 0x12345678u)) && Throws(() => Attributes.Set(e, 0x12345678u, 1)) && Throws(() => Attributes.Add(e, 0u, 1)),
            $"attributes: {name} an unknown attribute id is refused (Get, Set, Add)");
        // the id of a value in another component Kiln does not handle (Level, 0x724BF568) is unknown too
        Check(Throws(() => Attributes.Get(e, 0x724BF568u)), $"attributes: {name} the id of Level (another component) is refused");
        var other = Attributes.Get(e, 0x4114B3E5u);  // Stamina_Max_Adder: a computed input, readable, not writable
        Check(other is { IsCurrent: false }, $"attributes: {name} a computed attribute of the component (Stamina_Max_Adder) reads: {other?.Value} (pair {other?.Current}/{other?.Max})");
        return Task.CompletedTask;
    }

    private void OnHurt(EntityHurtEvent e)
    {
        _hurt.Add(e);
        switch (_hurtMode)
        {
            case HurtMode.Cancel: e.Cancel(); break;
            case HurtMode.Double: e.Amount *= 2; break;
            case HurtMode.Spare when e.Target == _spareTarget && e.Lethal: e.Amount = Math.Max(0, e.HealthBefore - 1); break;
        }
    }

    private NetworkHealth? ReadNetworkHealth(EntityId e) => World.TryRead<NetworkHealth>(e, "NetworkHealth", out var v) ? v : null;

    private Dictionary<uint, string> Census(WorldPosition at, double radius)
    {
        var d = new Dictionary<uint, string>();
        foreach (var id in Spawn.FindByName("_", at, radius, 512))
            d[id.Value] = Spawn.Inspect(id)?.TemplateName ?? "?";
        return d;
    }

    private async Task<T?> Until<T>(Func<T?> probe, int steps) where T : class
    {
        for (var i = 0; i < steps; ++i)
        {
            if (probe() is { } v) return v;
            await Context.Scheduler.NextTick();
        }
        return probe();
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; ++i) await Context.Scheduler.NextTick();
    }

    private (WorldPosition?, string) FindAnchor()
    {
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
            foreach (var id in Spawn.FindByName(filter, max: 4))
                if (Spawn.Inspect(id) is { } info)
                    return (info.Position, $"{info.TemplateName} (entity {id})");
        return (null, "none");
    }

    private bool Check(bool ok, string what)
    {
        if (ok) Pass(what); else Fail(what);
        return ok;
    }

    private void Pass(string s) { _pass++; Log.Info($"combat-test: PASS {s}"); }
    private void Fail(string s) { _fail++; Log.Error($"combat-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"combat-test: INFO {s}");
}
