// SPDX-License-Identifier: MIT
// Events lab test, player-free. Prints "events-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log.
//  1. EntityJoinLevelEvent / EntityLeaveLevelEvent for every entity: Kiln.Spawn's creature and its child entities, the
//     game's own creations/removals in the idle world, a canceled join, the category filter.
//  2. Command selectors: @e[type=..,distance=..,limit=..,sort=..] against the spawned creatures and the villagers,
//     @p/@r/@a without players.
//  3. EntityKnockbackEvent on held hits (AsGameHit) and on Combat.Damage, with the applied push-back and reaction read
//     back from EntityDamagedEvent.
//  4. EntityFallEvent: DynamicFallDamage.pct written on a spawned creature, so the game's fall_damage_infliction applies
//     it; cancel, change, keep, god mode.
//  5. Natural regeneration as EntityHealEvent (Cause Regeneration): cancel holds the health, allow lets it rise.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kiln;

namespace EventsTest;

[StructLayout(LayoutKind.Sequential)]
public struct DynamicFallDamage
{
    public uint WasFalling;
    public float FallStartAltitude, DetectedFallDistance, DetectedFallDamagePercentage;
}

// kiln.world v1 up to component() (native/core/abi/services/kiln_world.h): a test-only write path for components.
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WorldTable
{
    public uint Size, Version;
    public nint RegisterProgram, ProgramSlot, ProgramInfo, IterBegin, IterNext, CurrentEntity, CommandBuffer, QueuePush,
        QueueAdd, QueueRemove, MarkWritten, ReadOther, ComponentHash;
    public delegate* unmanaged[Cdecl]<uint, uint, uint*, void*> Component;
}

public sealed class EventsTestMod : Mod
{
    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();
    private readonly List<EntityJoinLevelEvent> _joins = new();
    private readonly List<EntityLeaveLevelEvent> _leaves = new();
    private string? _cancelJoinOf;
    private readonly List<EntityKnockbackEvent> _kb = new();
    private readonly List<EntityHurtEvent> _hurt = new();
    private readonly List<EntityDamagedEvent> _damaged = new();
    private readonly List<EntityFallEvent> _falls = new();
    private readonly List<EntityHealEvent> _heals = new();
    private Action<EntityKnockbackEvent>? _kbMode;
    private Action<EntityFallEvent>? _fallMode;
    private bool _cancelRegen;

    public override void OnLoad() => _ = RunGuarded();

    public override void OnUnload()
    {
        foreach (var e in _spawned)
            try { Spawn.Destroy(e); } catch { }
    }

    [Command("etest ents", Description = "events-test: entities a selector matches")]
    private string Ents(IReadOnlyList<EntityId> who) => string.Join(",", who.Select(e => e.Value));

    [Command("etest one", Description = "events-test: one entity")]
    private string One(EntityId who) => who.Value.ToString();

    [Command("etest player", Description = "events-test: one player")]
    private string OnePlayer(Player who) => who.Name;

    [Command("etest players", Description = "events-test: players")]
    private string ManyPlayers(IReadOnlyList<Player> who) => string.Join(",", who.Select(p => p.Name));

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
        Info($"combat status: {Combat.Status}");
        Info($"gameplay status: {Gameplay.Status}");
        Info($"summary: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Run()
    {
        var (anchor, what) = FindAnchor();
        if (!Check(anchor is not null, $"anchor: {what} at {anchor}")) return;
        var a = anchor!.Value;
        WorldPosition At(double dx, double dz) => new(a.X + 40 + dx, a.Y + 0.5, a.Z + dz);
        var vukah = Spawn.Lookup("Enemy_Vukah_Melee");
        if (!Check(vukah is not null, $"template Enemy_Vukah_Melee {vukah?.Id}")) return;
        async Task<EntityId> Make(double dx, double dz)
        {
            var e = await Spawn.Entity(vukah!.Id, At(dx, dz), 0, new SpawnOptions { Tag = "events-test", Flags = SpawnFlags.Transient });
            _spawned.Add(e);
            return e;
        }

        using (Events.On<ItemTossEvent>(e => Info($"ItemTossEvent {e.Player} {e.How} {e.ItemId:x8} x{e.Count}")))
        {
            await Steps(3);
            Check(Gameplay.Status is { Discard: true }, $"discard actions are read on this build (ItemTossEvent; needs a player to fire): {Gameplay.Status?.Discard}");
        }
        await EntityLifecycle(Make, At);
        await Selectors_(Make, At);
        await Knockback(Make);
        await Falls(Make);
        await Regeneration(Make);
    }

    // ------------------------------------------------------------------------------------------- 1 lifecycle ----

    private async Task EntityLifecycle(Func<double, double, Task<EntityId>> make, Func<double, double, WorldPosition> at)
    {
        var st = Gameplay.Status;
        Info($"gameplay status: {st}");
        if (!Check(st is { EntityHooks: true }, "kiln.gameplay hooks createEntity/destroyEntity")) return;

        using var join = Events.On<EntityJoinLevelEvent>(e =>
        {
            _joins.Add(e);
            if (_cancelJoinOf is { } t && e.TemplateName == t) e.Cancel();
        });
        using var leave = Events.On<EntityLeaveLevelEvent>(e => _leaves.Add(e));
        await Steps(3);
        Check(Gameplay.Status is { EntityCategories: EntityCategories.Default }, $"default categories while subscribed: {Gameplay.Status?.EntityCategories}");

        // idle world: what the game creates and removes by itself (no player near)
        _joins.Clear();
        _leaves.Clear();
        using (Gameplay.WatchEntities(EntityCategories.All))
        {
            await Steps(2);
            Check(Gameplay.Status is { EntityCategories: EntityCategories.All }, $"WatchEntities(All): {Gameplay.Status?.EntityCategories}");
            await Steps(250);
            var byCat = string.Join(", ", _joins.GroupBy(j => j.Category).Select(g => $"{g.Key} {g.Count()}"));
            var lbyCat = string.Join(", ", _leaves.GroupBy(j => j.Category).Select(g => $"{g.Key} {g.Count()}"));
            Info($"idle world, 250 steps: {_joins.Count} join(s) [{byCat}], {_leaves.Count} leave(s) [{lbyCat}]; " +
                 $"examples: {string.Join("; ", _joins.Take(6).Select(j => $"{j.Entity} {j.TemplateName} parent {j.Parent} kiln {j.ByKiln}"))}");
            var game = _joins.Count(j => !j.ByKiln) + _leaves.Count(l => !l.ByKiln);
            Info($"game-made (not on the tick thread): {game}");

            // Kiln spawn of a creature: the creature (by Kiln) and its child entities
            _joins.Clear();
            _leaves.Clear();
            var v = await make(0, 0);
            await Steps(3);
            var mine = _joins.FirstOrDefault(j => j.Entity == v);
            Check(mine is { Category: EntityCategories.Creature, ByKiln: true, TemplateName: "Enemy_Vukah_Melee" } &&
                  mine.Position is { } p && p.DistanceTo(at(0, 0)) < 1.0,
                $"join of the spawned Vukah {v}: {mine?.TemplateName} {mine?.Category} kiln {mine?.ByKiln} at {mine?.Position}");
            var kids = _joins.Where(j => j.Parent == v).ToList();
            Check(kids.Count > 0, $"its child entities joined too ({kids.Count}): {string.Join(", ", kids.Select(k => $"{k.Entity} {k.TemplateName} {k.Category} kiln {k.ByKiln}"))}");

            // destroy: the creature (Kiln) and its children (removed by the game)
            Spawn.Destroy(v);
            _spawned.Remove(v);
            await Steps(40);
            var gone = _leaves.FirstOrDefault(l => l.Entity == v);
            Check(gone is { Category: EntityCategories.Creature, ByKiln: true }, $"leave of the destroyed Vukah: {gone?.TemplateName} kiln {gone?.ByKiln}");
            var kidsGone = _leaves.Where(l => kids.Any(k => k.Entity == l.Entity)).ToList();
            Check(kidsGone.Count == kids.Count, $"its children left ({kidsGone.Count}/{kids.Count}, by the game: {kidsGone.Count(k => !k.ByKiln)})");

            // the game's queued paths: a gear item's per-item entity (pide) is created by the inventory code through
            // the job's command queue, and destroyed the same way when the stack is taken
            var chest = Items.FindInventories().Where(e => !World.Has(e, "Crafting")).Select(e => Items.Read(e))
                .FirstOrDefault(c => c is { Initialized: true } && c.FreeSlots() >= 2)?.Entity;
            var gear = Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons) ?? Items.All.FirstOrDefault(i => i.IsGear);
            if (Check(chest is not null && gear is not null, $"a chest with room ({chest}) and a gear item ({gear?.Name})"))
            {
                _joins.Clear();
                _leaves.Clear();
                var give = await Items.Give(chest!.Value, gear!.Id, 1);
                await Steps(3);
                var pide = Items.Read(chest.Value)?.Slots.FirstOrDefault(sl => sl.Item == gear.Id && !sl.ItemEntity.IsNone)?.ItemEntity;
                var pj = _joins.FirstOrDefault(j => pide is { } pe && j.Entity == pe);
                Check(give.Succeeded && pj is { ByKiln: false }, $"the gear's item entity {pide} joined from the game's queued create: {pj?.TemplateName} {pj?.Category} kiln {pj?.ByKiln} parent {pj?.Parent}");
                var take = await Items.Take(chest.Value, gear.Id, 1);
                await Steps(3);
                var pl = _leaves.FirstOrDefault(l => pide is { } pe && l.Entity == pe);
                Check(take.Succeeded && pl is { ByKiln: false }, $"and left through the game's queued destroy when taken: kiln {pl?.ByKiln}");
            }
        }
        await Steps(2);
        Check(Gameplay.Status is { EntityCategories: EntityCategories.Default }, $"back to the default after the watch ended: {Gameplay.Status?.EntityCategories}");

        // default filter: children (Other) are not reported, the creature is
        _joins.Clear();
        var v2 = await make(4, 0);
        await Steps(3);
        Check(_joins.Any(j => j.Entity == v2) && _joins.All(j => j.Category != EntityCategories.Other),
            $"default categories report the creature, not its {EntityCategories.Other} children ({_joins.Count} join(s))");

        // cancel a join: the entity is destroyed again
        _cancelJoinOf = "Enemy_Vukah_Melee";
        var v3 = await make(8, 0);
        await Steps(3);
        _cancelJoinOf = null;
        Check(_joins.Any(j => j.Entity == v3 && j.Canceled) && !Spawn.Exists(v3), $"canceled join of {v3}: exists {Spawn.Exists(v3)}");
        _spawned.Remove(v3);
        Info($"gameplay status after lifecycle: {Gameplay.Status}");
    }

    // ------------------------------------------------------------------------------------------- 2 selectors ----

    private async Task Selectors_(Func<double, double, Task<EntityId>> make, Func<double, double, WorldPosition> at)
    {
        var s1 = await make(20, 0);
        var s2 = await make(22, 0);
        var s3 = await make(30, 0);
        await Steps(2);
        var c = at(20, 0);
        string Run(string line)
        {
            var r = Commands.Execute(CommandSource.Console, line);
            return r.Ok ? string.Join("|", r.Output) : "ERR " + r.Error;
        }
        string Xyz(WorldPosition p) => FormattableString.Invariant($"x={p.X:F2},y={p.Y:F2},z={p.Z:F2}");
        var near = Run($"etest ents @e[type=Enemy_Vukah*,{Xyz(c)},distance=..5]");
        Check(near == $"{s1.Value},{s2.Value}" || near == $"{s2.Value},{s1.Value}", $"@e[type=Enemy_Vukah*,distance=..5] around s1 = {near} (s1 {s1}, s2 {s2})");
        var one = Run($"etest one @e[type=creature,{Xyz(at(29, 0))},sort=nearest,limit=1]");
        Check(one == s3.Value.ToString(), $"@e[type=creature,sort=nearest,limit=1] near s3 = {one}");
        var two = Run($"etest ents @e[type=Enemy_Vukah_Melee,{Xyz(c)},sort=furthest,limit=2,distance=..15]");
        Check(two == $"{s3.Value},{s2.Value}", $"sort=furthest,limit=2 = {two}");
        var ring = Run($"etest ents @e[type=Vukah,{Xyz(c)},distance=1..9]");
        Check(ring == s2.Value.ToString(), $"display name type=Vukah, distance=1..9 = {ring}");
        var npcs = Run("etest ents @e[type=npc]");
        Info($"@e[type=npc] = {npcs}");
        Check(!npcs.StartsWith("ERR"), "@e[type=npc] finds the villagers");
        var spaced = Run($"etest ents @e[type=Enemy_Vukah*, {Xyz(c).Replace(",", ", ")}, distance=..5]");
        Check(spaced == near, $"spaces inside the brackets: {spaced}");
        Check(Run("etest one @e[type=creature]").StartsWith("ERR"), $"a single-entity argument refuses several: {Run("etest one @e[type=creature]")}");
        Check(Run("etest player @p").StartsWith("ERR"), $"@p from the console without a position: {Run("etest player @p")}");
        Check(Run("etest players @a").Contains("no player"), $"@a without players: {Run("etest players @a")}");
        Check(Run("etest player @r").Contains("no player"), $"@r without players: {Run("etest player @r")}");
        Check(Run("etest ents @e[colour=red]").Contains("unknown filter"), $"bad filter: {Run("etest ents @e[colour=red]")}");
        var sel = Selectors.Entities(CommandSource.Console, $"@e[type=Enemy_Vukah_Melee,{Xyz(c)},distance=..5]");
        Check(sel.Contains(s1) && sel.Contains(s2) && !sel.Contains(s3), $"Selectors.Entities API: {string.Join(",", sel)}");
        foreach (var e in new[] { s1, s2, s3 })
        {
            Spawn.Destroy(e);
            _spawned.Remove(e);
        }
    }

    // ------------------------------------------------------------------------------------------- 3 knockback ----

    private async Task Knockback(Func<double, double, Task<EntityId>> make)
    {
        var target = await make(0, 10);
        var attacker = await make(3, 10);
        await Steps(4);
        using var kb = Events.On<EntityKnockbackEvent>(e => { if (e.Target == target) { _kb.Add(e); _kbMode?.Invoke(e); } });
        using var hurt = Events.On<EntityHurtEvent>(e => { if (e.Target == target) _hurt.Add(e); });
        using var dmg = Events.On<EntityDamagedEvent>(e => { if (e.Target == target) _damaged.Add(e); });
        await Steps(2);
        Check(Combat.Status is { Holding: true }, "hits are held while EntityKnockbackEvent has a subscriber");

        async Task<(HitResult r, EntityKnockbackEvent? k, EntityDamagedEvent? d)> Hit(DamageOptions o)
        {
            _kb.Clear();
            _damaged.Clear();
            _hurt.Clear();
            var r = await Combat.Damage(target, 3, attacker, o);
            await Steps(3);
            Combat.SetHealth(target, Combat.GetHealth(target)!.Value.Max);
            return (r, _kb.FirstOrDefault(), _damaged.FirstOrDefault());
        }

        // a held game hit (AsGameHit) with push-back 100 and a medium reaction
        _kbMode = null;
        var (r1, k1, d1) = await Hit(new DamageOptions { AsGameHit = true, PushBack = 100, Reaction = HitReaction.Medium });
        Check(k1 is { Strength: 100, Reaction: HitReaction.Medium, Origin: CombatOrigin.Kiln } && _hurt.Count == 1,
            $"knockback event of a held hit: strength {k1?.Strength}, reaction {k1?.Reaction}, direction {k1?.Direction}, after its hurt event ({_hurt.Count})");
        Check(r1.Applied && d1 is { PushBack: 100, Reaction: HitReaction.Medium }, $"unchanged: applied with push-back {d1?.PushBack}, reaction {d1?.Reaction}");

        _kbMode = e => { e.Strength = 7; e.Reaction = HitReaction.Heavy; e.Direction = new System.Numerics.Vector3(0, 0, 2); };
        var (r2, k2, d2) = await Hit(new DamageOptions { AsGameHit = true, PushBack = 100, Reaction = HitReaction.Medium });
        Check(r2.Applied && d2 is { PushBack: 7, Reaction: HitReaction.Heavy }, $"changed to strength 7, Heavy: applied with {d2?.PushBack}, {d2?.Reaction}");

        _kbMode = e => e.Cancel();
        var (r3, k3, d3) = await Hit(new DamageOptions { AsGameHit = true, PushBack = 100, Reaction = HitReaction.Medium });
        Check(r3.Applied && r3.HealthBefore - r3.HealthAfter == 3 && d3 is { PushBack: 0, Reaction: HitReaction.None },
            $"canceled: the damage applies ({r3.HealthBefore}->{r3.HealthAfter}) without push-back ({d3?.PushBack}) or reaction ({d3?.Reaction})");

        // Combat.Damage (synchronous event)
        _kbMode = e => e.Strength = 42;
        var (r4, k4, d4) = await Hit(new DamageOptions { PushBack = 50, Reaction = HitReaction.Light });
        Check(k4 is { OriginalStrength: 50 } && d4 is { PushBack: 42, Reaction: HitReaction.Light }, $"Combat.Damage knockback 50 -> 42: applied {d4?.PushBack} {d4?.Reaction}");

        // no knockback event for a hit without push-back or reaction
        _kbMode = null;
        var (_, k5, _) = await Hit(new DamageOptions { AsGameHit = true });
        Check(k5 is null, "no knockback event for a hit without push-back or reaction");

        // a canceled hurt: no knockback event
        using (Events.On<EntityHurtEvent>(e => { if (e.Target == target) e.Cancel(); }, EventPriority.High))
        {
            var (r6, k6, _) = await Hit(new DamageOptions { AsGameHit = true, PushBack = 100 });
            Check(r6.State == HitState.Dropped && k6 is null, $"a canceled hurt has no knockback event ({r6.State}, knockback {k6 is not null})");
        }
        Info($"combat status after knockback: {Combat.Status}");
    }

    // ------------------------------------------------------------------------------------------- 4 falls --------

    private static unsafe WorldTable* _world;

    // Kiln.Api's Services.Get is internal (mods never see tables); a lab test may reach it by reflection.
    private static unsafe WorldTable* WorldApi()
    {
        if (_world != null) return _world;
        var services = typeof(Mod).Assembly.GetType("Kiln.Services", throwOnError: true)!;
        var get = services.GetMethod("Get")!.MakeGenericMethod(typeof(WorldTable));
        var boxed = get.Invoke(null, ["kiln.world", 1u]);
        return _world = boxed is null ? null : (WorldTable*)System.Reflection.Pointer.Unbox(boxed);
    }

    private static unsafe float* FallPct(EntityId e)
    {
        var t = WorldApi();
        if (t == null) return null;
        uint size = 0;
        var p = (byte*)t->Component(e.Value, World.ComponentHash("DynamicFallDamage"), &size);
        return p != null && size >= 16 ? (float*)(p + 12) : null;
    }

    private async Task Falls(Func<double, double, Task<EntityId>> make)
    {
        var st = Combat.Status;
        if (!Check(st is { Falls: true }, $"fall damage hooks: {st?.Falls}")) return;
        var v = await make(0, 20);
        await Steps(4);
        Info($"Vukah {v}: DynamicFallDamage {World.Has(v, "DynamicFallDamage")}, DynamicLocomotion {World.Has(v, "DynamicLocomotion")}, " +
             $"read {(World.TryRead<DynamicFallDamage>(v, "DynamicFallDamage", out var dfd0) ? $"dist {dfd0.DetectedFallDistance} pct {dfd0.DetectedFallDamagePercentage}" : "-")}");
        using var sub = Events.On<EntityFallEvent>(e => { if (e.Entity == v) { _falls.Add(e); _fallMode?.Invoke(e); } }, receiveCanceled: true);
        await Steps(2);
        var max = Combat.GetHealth(v)!.Value.Max;

        async Task<(EntityFallEvent? ev, int before, int after)> Fall(float pct, int steps = 8)
        {
            _falls.Clear();
            Combat.SetHealth(v, max);
            await Steps(1);
            var before = Combat.GetHealth(v)!.Value.Current;
            unsafe
            {
                var p = FallPct(v);
                if (p == null) return (null, before, before);
                p[-1] = 0;   // detected distance below the start distance: fall_damage_calculation leaves pct alone
                *p = pct;
            }
            await Steps(steps);
            return (_falls.FirstOrDefault(), before, Combat.GetHealth(v)!.Value.Current);
        }

        _fallMode = null;
        var (e1, b1, a1) = await Fall(0.25f);
        var want = b1 - (int)Math.Max(0, Math.Floor(b1 - max * 0.25));
        Check(e1 is { DamageFraction: 0.25f, Lethal: false } && e1.Damage == want && b1 - a1 == want,
            $"fall event: fraction {e1?.DamageFraction}, damage {e1?.Damage} (expected {want}), distance {e1?.Distance}; health {b1} -> {a1}");
        unsafe { var p = FallPct(v); Info($"after the fall pct = {(p != null ? *p : float.NaN)} (the game's applied mark: negative)"); }

        _fallMode = e => e.Cancel();
        var (e2, b2, a2) = await Fall(0.5f);
        Check(e2 is not null && a2 == b2, $"canceled fall: health {b2} -> {a2}");
        unsafe { var p = FallPct(v); Check(p != null && *p < 0, $"canceled fall left the applied mark ({(p != null ? *p : float.NaN)})"); }

        _fallMode = e => e.Damage = 7;
        var (e3, b3, a3) = await Fall(0.5f);
        Check(e3 is not null && b3 - a3 == 7, $"fall damage changed to 7: health {b3} -> {a3}");
        await Steps(10);
        Check(Combat.GetHealth(v)!.Value.Current == a3, $"applied once (no second infliction): {Combat.GetHealth(v)}");

        _fallMode = null;
        var (e4, b4, a4) = await Fall(1.5f);
        Check(e4 is { Lethal: true } && a4 == 0, $"lethal fall: {b4} -> {a4}");
        var dead = Combat.GetHealth(v);
        Info($"after the lethal fall: {dead}");

        // god mode: arrives canceled
        var g = await make(3, 20);
        await Steps(4);
        Combat.SetInvulnerable(g);
        _falls.Clear();
        using (Events.On<EntityFallEvent>(e => { if (e.Entity == g) _falls.Add(e); }, receiveCanceled: true))
        {
            var gb = Combat.GetHealth(g)!.Value.Current;
            unsafe { var p = FallPct(g); if (p != null) { p[-1] = 0; *p = 0.5f; } }
            await Steps(8);
            Check(_falls.FirstOrDefault() is { Canceled: true } && Combat.GetHealth(g)!.Value.Current == gb,
                $"god mode: the fall event arrives canceled, health {gb} -> {Combat.GetHealth(g)?.Current}");
        }
        Combat.SetInvulnerable(g, false);
        Info($"combat status after falls: {Combat.Status}");
    }

    // ------------------------------------------------------------------------------------------- 5 regeneration --

    private async Task Regeneration(Func<double, double, Task<EntityId>> make)
    {
        var st = Combat.Status;
        if (!Check(st is { Regeneration: true }, $"regeneration hook: {st?.Regeneration}")) return;
        var v = await make(0, 30);
        await Steps(4);
        // candidates: entities with the game's HealthRecharge component (the regeneration program's input); without
        // one in the world, spawn a few creature templates and keep those that have it
        var candidates = World.Find("HealthRecharge").Where(e => Combat.GetHealth(e) is { Max: > 0, Dead: false }).Take(4).ToList();
        Info($"entities with HealthRecharge: {World.Find("HealthRecharge").Count}, with Health: {World.Find("Health").Count}");
        if (candidates.Count == 0)
        {
            var tried = new List<string>();
            var pool = Spawn.Templates.Where(t => t.LoadedByGame && !t.Name.Contains("Weakspot")).ToList();
            foreach (var t in pool.Where(t => t.Name.StartsWith("FakePlayer")).Take(6)
                         .Concat(pool.Where(t => t.Kind == TemplateKind.Npc).Take(4))
                         .Concat(pool.Where(t => t.Name.StartsWith("Animal_T")).Take(4))
                         .Concat(pool.Where(t => t.Name.StartsWith("Enemy_") && t.Kind == TemplateKind.Creature).Take(6)))
            {
                EntityId e;
                try { e = await Spawn.Entity(t.Id, new WorldPosition(Combat.GetHealth(v) is null ? 0 : World.Position(v)!.Value.X + 4, World.Position(v)!.Value.Y, World.Position(v)!.Value.Z), 0,
                    new SpawnOptions { Tag = "events-test", Flags = SpawnFlags.Transient }); }
                catch (Exception) { continue; }
                _spawned.Add(e);
                await Steps(2);
                var has = World.Has(e, "HealthRecharge");
                tried.Add($"{t.Name}{(has ? "+" : "")}");
                if (has && Combat.GetHealth(e) is { Max: > 0 }) { candidates.Add(e); if (candidates.Count >= 2) break; }
                else { Spawn.Destroy(e); _spawned.Remove(e); }
            }
            Info($"templates tried for HealthRecharge: {string.Join(", ", tried)}");
        }
        if (candidates.Count == 0)
        {
            // health_recharge needs HealthRecharge, which only players' entities have here: regeneration events need a
            // joined player (client checklist). The hook is installed (status above).
            Info("SKIP regeneration: no entity in the world (or spawned) has HealthRecharge; players only -> client checklist");
            return;
        }
        foreach (var e in candidates)
            Info($"regen candidate {e} {Spawn.Inspect(e)?.TemplateName}: health {Combat.GetHealth(e)}, HealthRecharge " +
                 $"{(World.ReadBytes(e, "HealthRecharge") is { } b ? Convert.ToHexString(b) : "none")}");
        using var sub = Events.On<EntityHealEvent>(e =>
        {
            if (e.Cause != HealCause.Regeneration || !candidates.Contains(e.Target)) return;
            _heals.Add(e);
            if (_cancelRegen) e.Cancel();
        });
        var start0 = new Dictionary<EntityId, int>();
        foreach (var e in candidates)
        {
            var h = Combat.GetHealth(e)!.Value;
            Combat.SetHealth(e, Math.Max(1, h.Max - Math.Max(4, h.Max / 4)));
            start0[e] = Combat.GetHealth(e)!.Value.Current;
        }
        _cancelRegen = true;
        _heals.Clear();
        for (var i = 0; i < 25 * 60 && _heals.Count < 3; ++i) await Context.Scheduler.NextTick();
        Info($"regen events while canceled: {_heals.Count} ({string.Join(", ", _heals.Take(4).Select(h => $"{h.Target} +{h.Amount} at {h.HealthBefore}/{h.HealthMax}"))})");
        foreach (var e in candidates)
            Info($"after: {e} health {Combat.GetHealth(e)}, HealthRecharge {(World.ReadBytes(e, "HealthRecharge") is { } b ? Convert.ToHexString(b) : "none")}");
        if (!Check(_heals.Count > 0, $"natural regeneration reaches EntityHealEvent (Cause Regeneration): {_heals.Count} event(s)"))
        {
            foreach (var e in candidates) if (!e.IsPlayer && e != v) Combat.SetHealth(e, Combat.GetHealth(e)!.Value.Max);
            return;
        }
        var target = _heals[0].Target;
        var held = Combat.GetHealth(target)!.Value.Current;
        Check(held == start0[target], $"canceled regeneration keeps {target}'s health: {start0[target]} -> {held}");
        _cancelRegen = false;
        _heals.Clear();
        for (var i = 0; i < 25 * 60 && _heals.Count(h => h.Target == target) < 2; ++i) await Context.Scheduler.NextTick();
        await Steps(2);
        var rose = Combat.GetHealth(target)!.Value.Current;
        var sum = _heals.Where(h => h.Target == target).Sum(h => h.Amount);
        Check(sum > 0 && rose >= held + sum, $"allowed regeneration adds the points: {held} -> {rose} (+{sum} in {_heals.Count(h => h.Target == target)} event(s))");
        foreach (var e in candidates) if (e != v) Combat.SetHealth(e, Combat.GetHealth(e)!.Value.Max);
        Info($"combat status after regeneration: {Combat.Status}");
    }

    // ------------------------------------------------------------------------------------------- helpers ---------

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

    private void Pass(string s) { _pass++; Log.Info($"events-test: PASS {s}"); }
    private void Fail(string s) { _fail++; Log.Error($"events-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"events-test: INFO {s}");
}
