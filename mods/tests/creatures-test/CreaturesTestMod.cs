// SPDX-License-Identifier: MIT
// kiln.creatures lab test, player-free. Prints "creatures-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log.
// Vukahs are spawned 60 m from the base (Transient, destroyed at the end). The research probes (docs/re/creatures.md 9)
// are checks here:
//   probe 1: component map (dynamic / template / absent), template data = the offline decode, shared template memory
//            (a write reaches existing and new Vukahs; restored), Level -> health 119 -> 417, Faction/Scale/Level at
//            spawn (kiln.spawn components);
//   probe 2: the lab readiness switch: the AI leaves Spawning (state events);
//   probe 3: the arsenal registry (201 arsenals) and the arsenal lookup;
//   probe 4: a private Enemy01BehaviorState copy read by enemy01's behaviour update for that entity only, kept across
//            spawns, destroys and a teleport, then given back.
// Plus the service: values with owner layers and revert (a named owner as a data pack would be), features, loot,
// resistances, speed/damage factors, the catalog against the engine, ApplyJson, commands (guard, attack), marks,
// despawn cleanup, and the EliteVukah sample (loaded: an elite at level 20, bigger, own view range, undone on unload).
// Lab switches: kiln/data/kiln.creatures/force-ready (readiness tests; else they are skipped as INFO).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kiln;

namespace CreaturesTest;

public sealed class CreaturesTestMod : Mod
{
    private static readonly WorldPosition Pos = new(3865.8, 833.0, 1433.8);
    private const string Vukah = "Enemy_Vukah_Melee";
    private static readonly Guid VukahId = Guid.Parse("d5d05217-52dc-467a-9192-26b4f661d8d9");
    private static readonly Guid VukahArsenal = Guid.Parse("e30312e7-9749-4329-8bfd-469a295747b5");
    private static readonly Guid VukahLoot = Guid.Parse("fa76a666-cd5a-4a69-8a0d-ada7072b9ede");

    private int _pass, _fail;
    private readonly List<EntityId> _mine = [];
    private readonly List<HookHandle> _hooks = [];
    private readonly List<CreatureStateEvent> _states = [];
    private readonly List<CreatureTargetEvent> _targets = [];
    private nuint _world;

    private void Info(string s) => Log.Info("creatures-test: INFO " + s);
    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"creatures-test: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    [Subscribe]
    private void OnState(CreatureStateEvent e)
    {
        if (_mine.Contains(e.Entity)) _states.Add(e);
    }

    [Subscribe]
    private void OnTarget(CreatureTargetEvent e)
    {
        if (_mine.Contains(e.Entity)) _targets.Add(e);
    }

    public override void OnLoad() => _ = RunGuarded();

    public override void OnUnload() => Cleanup();

    private void Cleanup()
    {
        foreach (var h in _hooks)
            try { h.Remove(); } catch { }
        _hooks.Clear();
        foreach (var e in _mine.Distinct())
            try { Spawn.Destroy(e); } catch { }
        _mine.Clear();
    }

    private Task Steps(int n) => StepsAsync(n);

    private async Task StepsAsync(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task RunGuarded()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            Info($"world running after {sw.Elapsed.TotalSeconds:F1} s");
            await Steps(60);
            await Run();
        }
        catch (Exception e)
        {
            Check(false, $"test threw: {e}");
        }
        finally
        {
            Cleanup();
        }
        await Steps(300);  // let deferred frees run
        var s = Creatures.Status;
        Info($"after cleanup: {s?.Entities} entities with state, {s?.PrivateCopies} copies, {s?.PendingFree} pending frees");
        Check(s is { Entities: 0, PrivateCopies: 0 }, "no creature state is left after the test's creatures were destroyed");
        Check(s is { PendingFree: 0 }, "every private copy was freed after the delay");
        Info($"summary: {_pass} PASS, {_fail} FAIL");
    }

    private async Task<EntityId> SpawnVukah(double dx, double dz, SpawnOptions? o = null)
    {
        var e = await Spawn.Entity(Vukah, Pos with { X = Pos.X + dx, Z = Pos.Z + dz }, 0,
            o ?? new SpawnOptions { Tag = "creatures-test", Flags = SpawnFlags.Transient });
        _mine.Add(e);
        return e;
    }

    private async Task Run()
    {
        var st = Creatures.Status;
        Info($"status: {st}");
        if (!Check(st is not null, "kiln.creatures is available")) return;
        Check(st!.Slots && st.Reflection, "template slots and the game's reflection resolve on this build");
        Check(st.AiHooks, "the enemy01 SetState / HandleCondition hooks are installed");
        Check(st.Commands, "the command program kiln_creatures is active");
        Check(st.Marks, "the replication marks program kiln_creatures_marks is active");

        CatalogChecks(st);
        CaptureWorld();

        var a = await SpawnVukah(0, 0);
        var b = await SpawnVukah(4, 0);
        await Steps(5);
        ComponentMap(a);
        TemplateData(a);
        ArsenalChecks(a, st);
        await ArsenalOverride(a, b);
        await SharedTemplate(a, b);
        await LevelAndValues(a, b);
        await SpawnComponents();
        await PrivateCopy(a, b);
        await Layers(b);
        await Readiness(a, b);
        await Guarding();
        await PackJson();
        await Elite();
        await CloneVariant();
        await DespawnCleanup();
        var end = Creatures.Status!;
        Info($"counters: {end.StateChanges} state changes, {end.EventsDelivered} events, {end.CommandsSent} commands, {end.MarksWritten} marks, {end.EventsDropped} dropped");
        Check(end.MarksWritten > 0, $"replication marks were written for Kiln's changes ({end.MarksWritten})");
    }

    // ---------------------------------------------------------------------------------------------- catalog ----
    private void CatalogChecks(CreaturesStatus st)
    {
        var all = Creatures.Templates;
        var byKind = all.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count());
        Info($"catalog: {all.Count} templates ({string.Join(", ", byKind.Select(kv => $"{kv.Key} {kv.Value}"))}), {st.CatalogLoaded} loaded, " +
             $"{all.Count(t => t.IsBoss)} bosses, {all.Count(t => t.Flags.HasFlag(CreatureFlags.GuardsPlayer))} can guard players");
        Check(all.Count >= 680 && byKind.GetValueOrDefault(CreatureKind.Enemy) >= 530, $"the catalog lists the enemy-AI, wildlife, animal and villager templates ({all.Count})");
        var shipped = all.Count(t => !t.IsRuntime);
        var runtime = all.Where(t => t.IsRuntime).ToList();
        Check(st.CatalogLoaded == shipped, $"every shipped catalog template is loaded by the running game ({st.CatalogLoaded}/{shipped})");
        Check(st.EngineCreaturesMissing == runtime.Count && runtime.All(t => t.IsLoaded && t.Kind != CreatureKind.None),
              $"engine templates with enemy AI / villager state that the shipped catalog lacks are listed as runtime entries ({runtime.Count}{(runtime.Count > 0 ? ": " + string.Join(", ", runtime.Take(4).Select(t => t.Name)) : "")})");
        var v = Creatures.Template("Vukah");
        Check(v is { Name: Vukah, Kind: CreatureKind.Enemy, Faction: Faction.Vukah, EnemyClass: 0 } && v.Arsenal == VukahArsenal &&
              v.LootLabels == VukahLoot && v.LootSlotsMin == 1 && v.LootSlotsMax == 4 && v.LevelMax == 105,
              $"Template(\"Vukah\") = {v?.Name}: enemy, faction {v?.Faction}, melee, arsenal {v?.Arsenal}, loot {v?.LootLabels} {v?.LootSlotsMin}-{v?.LootSlotsMax}");
        Check(Creatures.Template(VukahId)?.Name == Vukah && Creatures.Template(VukahId.ToString())?.Name == Vukah, "lookup by GUID and GUID text");
        var villagers = all.Where(t => t.IsVillager).ToList();
        Check(villagers.Count >= 50 && villagers.All(t => t.Kind == CreatureKind.Villager), $"villagers in the catalog ({villagers.Count}, e.g. {villagers.FirstOrDefault()?.Name})");
        var dragon = Creatures.Template("Enemy_Boss_Fog_Dragon");
        Info($"boss sample: {dragon} boss {dragon?.IsBoss} phases {dragon?.Flags.HasFlag(CreatureFlags.Phases)}");
    }

    // ---------------------------------------------------------------------------------------------- raw access ----
    private void CaptureWorld()
    {
        _hooks.Add(Kiln.Hooks.Detour("GetComponent", c => { if (_world == 0) _world = c[1]; return c.CallOriginal(); }));
        _ = World.ReadBytes(new EntityId(1), "CurrentTransform");
        _hooks[^1].Remove();
        _hooks.RemoveAt(_hooks.Count - 1);
        Info($"world 0x{_world:X}");
    }

    private nuint Tpl(string comp, EntityId e) => Raw.Tpl(_world, comp, e);
    private nuint Dyn(string comp, EntityId e) => Raw.Dyn(_world, comp, e);

    private static int Offset(string component, string path)
    {
        using var d = JsonDocument.Parse(Creatures.AiFields(component));
        foreach (var f in d.RootElement.EnumerateArray())
            if (f.GetProperty("path").GetString() == path) return f.GetProperty("offset").GetInt32();
        return -1;
    }

    // ---------------------------------------------------------------------------------------------- probe 1 ----
    private void ComponentMap(EntityId a)
    {
        string[] dynamic = ["Faction", "Level", "NetworkLevel", "Health", "NetworkHealth", "ScaleComponent", "DynamicEnemy01BehaviorState",
            "NetworkEnemy", "DynamicAggro", "DynamicEnemyScaling", "DamageSusceptibility", "DamageMod", "Movement", "SelectedTargets",
            "RandomLoot", "RandomLootSlotCount", "ExperienceSource", "Actor"];
        string[] template = ["EnemyArsenalReference", "Enemy01BehaviorState", "Aggro", "EnemyScaling", "SubEntities", "PathWalker"];
        string[] absent = ["ModelResource", "ModelDynamic", "OverrideLevel"];
        var badDyn = dynamic.Where(n => World.ComponentSize(a, n) <= 0).ToList();
        var badTpl = template.Where(n => World.ComponentSize(a, n) > 0 || Tpl(n, a) == 0).ToList();
        var badAbs = absent.Where(n => World.ComponentSize(a, n) > 0 || Tpl(n, a) != 0).ToList();
        Check(badDyn.Count == 0, $"per-entity components of a Vukah ({dynamic.Length}): {(badDyn.Count == 0 ? "all dynamic" : "missing " + string.Join(",", badDyn))}");
        Check(badTpl.Count == 0, $"template-only components ({template.Length}): {(badTpl.Count == 0 ? "all shared template data" : "wrong " + string.Join(",", badTpl))}");
        Check(badAbs.Count == 0, $"client-only / absent components on the server ({string.Join(",", absent)}): {(badAbs.Count == 0 ? "absent" : "present " + string.Join(",", badAbs))}");
        Info($"sizes: DynamicEnemy01BehaviorState {World.ComponentSize(a, "DynamicEnemy01BehaviorState")}, SelectedTargets {World.ComponentSize(a, "SelectedTargets")}, Actor {World.ComponentSize(a, "Actor")}");
    }

    private void TemplateData(EntityId a)
    {
        var ai = Creatures.Of(a).Ai;
        (string path, string want)[] expect =
        [
            ("combatSetup.preferedDistance", "5"), ("aggroSetup.viewRange", "18"), ("aggroSetup.viewConeHalfAngle", "88"),
            ("aggroSetup.aggroRange", "10"), ("aggroSetup.alarmedAggroThreshold", "500"), ("guardSetup.startDistance", "10"),
            ("guardSetup.targetDistance", "3"), ("guardSetup.canGuardPlayer", "true"), ("homeZoneSetup.radius", "10"),
            ("enemyClass", "Melee"), ("threatFactor", "1"), ("combatSetup.cooldownGroups[0].cooldown", "4"),
            ("EnemyArsenalReference.arsenal", VukahArsenal.ToString()),
            ("EnemyScaling.playerCountScaling.healthScaling", "0.479999989"), ("EnemyScaling.updateInterval", "5"),
        ];
        var bad = new List<string>();
        foreach (var (p, want) in expect)
        {
            string got;
            try { got = ai.Get(p, out var priv); if (priv) got += " (private?)"; }
            catch (Exception e) { got = "error " + e.Message; }
            if (got != want) bad.Add($"{p} = {got} (want {want})");
        }
        Check(bad.Count == 0, $"template data read through reflection paths equals the offline decode ({expect.Length} fields){(bad.Count > 0 ? ": " + string.Join("; ", bad) : "")}");
        Info($"enabledFeatures {ai.Get("enabledFeatures")}");
        Check(ai.Get("enabledFeatures").Contains("FleeHome") == false && ai.Get("enabledFeatures").Contains("ReturnHome"),
              "the Vukah's feature mask names ReturnHome, not FleeHome (flags formatted by name)");
        try
        {
            ai.Get("aggroSetup.noSuchField");
            Check(false, "an unknown path is refused");
        }
        catch (ArgumentException e) { Check(true, $"an unknown path is refused ({e.Message.Split(':')[0]})"); }
    }

    private void ArsenalChecks(EntityId a, CreaturesStatus st)
    {
        Check(st.Arsenals && st.ArsenalCount == 201, $"the arsenal registry resource holds {st.ArsenalCount} arsenals (201 on svn 1285601)");
        var c = Creatures.Of(a);
        Check(c.Arsenal == VukahArsenal, $"the Vukah's AI reads arsenal {c.Arsenal}");
    }

    // R5: a per-entity EnemyArsenalReference copy switches the arsenal enemy01 looks up (by GUID, every update)
    private async Task ArsenalOverride(EntityId a, EntityId b)
    {
        var other = Creatures.Template("Base_Enemy_Vukah_Melee")?.Arsenal ?? Guid.Empty;
        if (other == Guid.Empty || other == VukahArsenal)
        {
            Info("no second Vukah arsenal in the catalog: arsenal override skipped");
            return;
        }
        var ca = Creatures.Of(a);
        ca.Arsenal = other;
        var reads = new Raw.GuidReads { Want = other, Old = VukahArsenal };
        _hooks.Add(Raw.CountArsenalLookups(reads));
        await Steps(50);
        _hooks[^1].Remove();
        _hooks.RemoveAt(_hooks.Count - 1);
        Check(ca.Arsenal == other && Creatures.Of(b).Arsenal == VukahArsenal, $"A's arsenal is {ca.Arsenal} (private copy), B keeps {Creatures.Of(b).Arsenal}");
        Check(reads.Hits > 0 && reads.OldHits > 0, $"enemy01 looks up A's new arsenal {reads.Hits}x in 2 s (and B's own {reads.OldHits}x): R5 holds, the arsenal is resolved by GUID every update");
        ca.Ai.Reset("EnemyArsenalReference");
        Check(ca.Arsenal == VukahArsenal, "Ai.Reset(\"EnemyArsenalReference\") gives the template's arsenal back");
    }

    // a variant whose base is a kiln.content clone (gamedata's CreatureTweaks / test clone), when one is loaded
    private async Task CloneVariant()
    {
        var clone = new[] { "Kiln_Elite_Vukah", "Kiln_Test_Elite_Vukah" }.Select(n => Spawn.Lookup(n)).FirstOrDefault(t => t is not null);
        if (clone is null)
        {
            Info("no kiln.content clone of the Vukah is loaded: the clone-variant check is skipped");
            return;
        }
        using var reg = CreatureVariants.Register(new CreatureVariant { Id = "ct_clone", Base = clone.Name, XpFactor = 3, Ai = { ["aggroSetup.aggroRange"] = "12" } });
        var e = await Creatures.Spawn("ct_clone", Pos with { Z = Pos.Z - 20 }, 0, new CreatureSpawnOptions { Flags = SpawnFlags.Transient });
        _mine.Add(e);
        var c = Creatures.Of(e);
        var i = c.Inspect()!;
        Check(i.Template == clone.Id && i.XpFactor == 3 && c.Ai.Get("aggroSetup.aggroRange") == "12" &&
              Creatures.Template(clone.Id) is { IsRuntime: true },
              $"a variant of the clone {clone.Name}: template {i.Template}, level {i.Level}, scale {i.Scale}, view {c.Ai.Get("aggroSetup.viewRange")}, xp {i.XpFactor}, aggroRange 12 on top");
        Spawn.Destroy(e);
    }

    private async Task SharedTemplate(EntityId a, EntityId b)
    {
        var pa = Tpl("Enemy01BehaviorState", a);
        var pb = Tpl("Enemy01BehaviorState", b);
        Check(pa != 0 && pa == pb, $"two Vukahs share one Enemy01BehaviorState template (0x{pa:X})");
        var off = Offset("Enemy01BehaviorState", "aggroSetup.viewRange");
        Check(off == 0x18c, $"reflection offset of aggroSetup.viewRange = 0x{off:X} (0x18C)");
        if (pa == 0 || off < 0) return;
        var orig = Memory.Read<float>(pa + (nuint)off);
        Memory.Write(pa + (nuint)off, 42.5f);
        var c = await SpawnVukah(8, 0);
        var seen = (Creatures.Of(a).Ai.Get("aggroSetup.viewRange"), Creatures.Of(b).Ai.Get("aggroSetup.viewRange"), Creatures.Of(c).Ai.Get("aggroSetup.viewRange"));
        Memory.Write(pa + (nuint)off, orig);
        Check(seen == ("42.5", "42.5", "42.5"), $"a template write (viewRange 18 -> 42.5) reaches existing and new Vukahs: {seen}");
        Check(Creatures.Of(a).Ai.Get("aggroSetup.viewRange") == "18", "the template value is restored (18)");
        Spawn.Destroy(c);
        await Steps(2);
    }

    private async Task LevelAndValues(EntityId a, EntityId b)
    {
        var ca = Creatures.Of(a);
        var h0 = Combat.GetHealth(a);
        Check(h0 is { Max: 119 }, $"a level-1 Vukah has 119 max health ({h0?.Max})");
        ca.Level = 10;
        ca.Faction = Faction.PlayerAlly;
        Check(ca.Level == 10 && ca.Faction == Faction.PlayerAlly, "Level 10 and Faction PlayerAlly read back at once");
        await Steps(200);
        var h1 = Combat.GetHealth(a);
        var nl = World.TryRead<ushort>(a, "NetworkLevel", out var netLevel) ? netLevel : 0;
        Check(h1 is { Max: 417, Current: 417 }, $"health follows the level through the game's enemy_scaling: 119 -> {h1?.Max} (417)");
        Check(nl == 10, $"the replicated NetworkLevel follows ({nl})");

        ca.Scale = 1.6f;
        var tr = World.Transform(a);
        Check(Math.Abs(ca.Scale - 1.6f) < 1e-4 && tr is { } t && Math.Abs(t.Scale.X - 1.6f) < 1e-4,
              $"Scale 1.6: ScaleComponent {ca.Scale:0.###} and transform scale {World.Transform(a)?.Scale}");
        ca.XpFactor = 2;
        ca.Resistance[DamageKind.Fire] = 0.25f;
        ca.Resistance[DamageKind.Pierce] = 0;
        ca.Loot.Slots = (3, 7);
        ca.Loot.Labels = VukahLoot;
        ca.SpeedFactor = 0.5f;
        ca.DamageFactor = 1.5f;
        ca.Features.Disable(EnemyFeatures.ReturnHome | EnemyFeatures.FleeHome);
        ca.Features.Enable(EnemyFeatures.Sleep);
        var move0 = World.ReadBytes(b, "Movement")!;
        var move1 = World.ReadBytes(a, "Movement")!;
        var walkB = BitConverter.ToSingle(move0, 8 + 4 * 3);
        var walkA = BitConverter.ToSingle(move1, 8 + 4 * 3);
        Check(Math.Abs(walkA - walkB * 0.5f) < 1e-3, $"SpeedFactor 0.5: walk speed {walkB} -> {walkA}");
        await Steps(150);  // past an enemy_scaling update (5 s interval)
        var i = ca.Inspect()!;
        Info($"A after 6 s: {i}");
        Check(i.XpFactor == 2 && Math.Abs(ca.Resistance[DamageKind.Fire] - 0.25f) < 1e-6 && ca.Resistance[DamageKind.Pierce] == 0 &&
              i.LootSlotsMin == 3 && i.LootSlotsMax == 7, "XP, resistances (Fire 0.25, Pierce 0) and loot slots (3-7) hold after the game's updates");
        Check(Math.Abs(ca.DamageFactor - 1.5f) < 1e-6, $"DamageFactor 1.5 holds after an enemy_scaling update ({ca.DamageFactor})");
        Check(Math.Abs(ca.SpeedFactor - 0.5f) < 1e-3, $"SpeedFactor reads back 0.5 ({ca.SpeedFactor:0.###})");
        Check(i.DisabledFeatures.HasFlag(EnemyFeatures.ReturnHome | EnemyFeatures.FleeHome) && i.EnabledFeatures.HasFlag(EnemyFeatures.Sleep) &&
              Creatures.Of(b).Inspect()!.DisabledFeatures == EnemyFeatures.None,
              $"feature switches are per entity: A -{i.DisabledFeatures} +{i.EnabledFeatures}, B none");
        var home = ca.Home;
        ca.Command.MoveTo(Pos with { X = Pos.X + 20 });
        Check(Math.Abs(ca.Home.X - (Pos.X + 20)) < 0.01, $"MoveTo sets the home zone: {home} -> {ca.Home}");
        var dropped = ca.Reset();
        var r = ca.Inspect()!;
        Check(dropped >= 10 && r.Level == 1 && r.Faction == Faction.Vukah && Math.Abs(r.Scale - 1) < 1e-6 && r.XpFactor == 1 &&
              r.LootSlotsMin == 1 && r.LootSlotsMax == 4 && Math.Abs(ca.SpeedFactor - 1) < 1e-3 && r.DisabledFeatures == EnemyFeatures.None &&
              Math.Abs(ca.Resistance[DamageKind.Fire] - 1) < 1e-6 && Math.Abs(r.Home.X - home.X) < 0.01 && r.Layers == 0,
              $"Reset reverts all {dropped} changes: level {r.Level}, faction {r.Faction}, scale {r.Scale}, xp {r.XpFactor}, slots {r.LootSlotsMin}-{r.LootSlotsMax}, home {r.Home}");
        await Steps(200);
        Check(Combat.GetHealth(a) is { Max: 119 }, $"and the health scales back to level 1 ({Combat.GetHealth(a)?.Max})");
    }

    private async Task SpawnComponents()
    {
        if (!Spawn.SupportsComponents)
        {
            Info("kiln.spawn has no spawn components on this build: spawn-time Faction/Scale/Level skipped");
            return;
        }
        var lv = new byte[16];
        BitConverter.GetBytes(12u).CopyTo(lv, 8);
        BitConverter.GetBytes(105u).CopyTo(lv, 12);
        var e = await SpawnVukah(0, 10, new SpawnOptions
        {
            Tag = "creatures-test", Flags = SpawnFlags.Transient,
            Components = [SpawnComponent.Of("Faction", [11]), SpawnComponent.Of("ScaleComponent", BitConverter.GetBytes(2.0f)), SpawnComponent.Of("Level", lv)],
        });
        await Steps(150);
        var c = Creatures.Of(e);
        var h = Combat.GetHealth(e);
        Check(c.Faction == Faction.PlayerAlly && Math.Abs(c.Scale - 2) < 1e-6 && c.Level == 12 && h is { Max: 511 },
              $"Faction 11, ScaleComponent 2 and Level 12 as spawn components: faction {c.Faction}, scale {c.Scale}, level {c.Level}, health {h?.Max} (511)");
        Spawn.Destroy(e);
    }

    // ---------------------------------------------------------------------------------------------- probe 4 ----
    private async Task PrivateCopy(EntityId a, EntityId b)
    {
        var shared = Tpl("Enemy01BehaviorState", a);
        var ca = Creatures.Of(a);
        ca.Ai.Set("aggroSetup.viewRange", 99);
        ca.Ai.Set("guardSetup.startDistance", 33);
        ca.Ai.Set("Aggro.applyEnemyValue", false);
        var copy = Tpl("Enemy01BehaviorState", a);
        var va = ca.Ai.Get("aggroSetup.viewRange", out var privA);
        var vb = Creatures.Of(b).Ai.Get("aggroSetup.viewRange", out var privB);
        Check(copy != 0 && copy != shared && Tpl("Enemy01BehaviorState", b) == shared, $"A reads a private copy (0x{copy:X}), B the shared template (0x{shared:X})");
        Check(va == "99" && privA && vb == "18" && !privB, $"A viewRange {va} (private), B {vb} (template)");
        Check(ca.Ai.Get("guardSetup.startDistance") == "33" && ca.Ai.Get("aggroSetup.viewConeHalfAngle") == "88",
              "the copy keeps every other field of the template (viewConeHalfAngle 88) and has guardSetup.startDistance 33");
        Check(ca.Ai.Get("Aggro.applyEnemyValue") == "false" && Creatures.Of(b).Ai.Get("Aggro.applyEnemyValue") == "true",
              "a second template component (Aggro) gets its own copy: A false, B true");
        Check(Creatures.Of(a).Inspect()!.PrivateCopies == 2, "A has 2 private copies");

        // enemy01's behaviour update reads the copy for A only (ctx+0x48 = dynamic state, ctx+0x50 = template)
        var reads = new Raw.Reads { Dyn = Dyn("DynamicEnemy01BehaviorState", a), Copy = copy };
        _hooks.Add(Raw.CountReads(reads));
        await Steps(250);
        Check(reads.Copy_ > 100 && reads.Orig == 0 && reads.Other == 0,
              $"in 10 s enemy01's behaviour update for A read the copy {reads.Copy_}x, the shared data {reads.Orig}x; other entities read the copy {reads.Other}x");
        // chunk activity: spawns, destroys and a teleport (archetype change) move A
        var more = new List<EntityId>();
        for (var i = 0; i < 6; i++) more.Add(await SpawnVukah(-3, 3 + i));
        Spawn.Destroy(more[0]);
        Spawn.Destroy(more[1]);
        var tp = await Teleport.To(a, Pos with { X = Pos.X - 3 });
        Info($"teleport A: {tp.Status} {tp.Reason}");
        reads.Copy_ = reads.Orig = reads.Other = 0;
        for (var i = 0; i < 125; i++)
        {
            reads.Dyn = Dyn("DynamicEnemy01BehaviorState", a);  // A moves between chunks (teleport add/remove)
            await Steps(1);
        }
        Check(Tpl("Enemy01BehaviorState", a) == copy && reads.Copy_ > 0 && reads.Orig == 0 && reads.Other == 0 &&
              more.Skip(2).All(m => Tpl("Enemy01BehaviorState", m) == shared),
              $"after 6 spawns, 2 destroys and a teleport A still reads its copy ({reads.Copy_}x, shared {reads.Orig}x), the new Vukahs the template");
        _hooks[^1].Remove();
        _hooks.RemoveAt(_hooks.Count - 1);
        var s0 = Creatures.Status!;
        var n = ca.Ai.Reset();
        var s1 = Creatures.Status!;
        Check(n == 3 && Tpl("Enemy01BehaviorState", a) == shared && ca.Ai.Get("aggroSetup.viewRange") == "18" &&
              ca.Ai.Get("Aggro.applyEnemyValue") == "true" && s1.PendingFree == s0.PendingFree + 2,
              $"Ai.Reset drops {n} overrides: A reads the shared template again, both copies wait to be freed ({s0.PendingFree} -> {s1.PendingFree})");
        foreach (var m in more.Skip(2)) Spawn.Destroy(m);
    }

    // ---------------------------------------------------------------------------------------------- layers ----
    private async Task Layers(EntityId b)
    {
        // a data pack's owner (named) under the test mod's own layer
        var json = """{ "variants": [ { "id": "ct_layer", "base": "Enemy_Vukah_Melee", "level": 7, "scale": 1.2, "ai": { "aggroSetup.viewRange": 21 } } ] }""";
        var rep = Creatures.ApplyJson(json, "pack:creatures-test-layers", "layers.json");
        Check(rep.Ok && rep.Applied == 1, $"ApplyJson registers a variant for a named owner ({rep.Applied}, {string.Join("; ", rep.Errors)})");
        CreatureVariants.Apply(b, "ct_layer");
        var c = Creatures.Of(b);
        Check(c.Level == 7 && Math.Abs(c.Scale - 1.2f) < 1e-6 && c.Ai.Get("aggroSetup.viewRange") == "21", "the pack variant applied: level 7, scale 1.2, viewRange 21");
        c.Level = 15;
        c.Ai.Set("aggroSetup.viewRange", 25);
        Check(c.Level == 15 && c.Ai.Get("aggroSetup.viewRange") == "25", "the test mod's layer on top: level 15, viewRange 25");
        c.Reset();
        Check(c.Level == 7 && c.Ai.Get("aggroSetup.viewRange") == "21", "Reset of the mod's layer: the pack's level 7 / viewRange 21 come back");
        var n = Creatures.RevertJson("pack:creatures-test-layers");
        await Steps(1);
        Check(n == 1 && CreatureVariants.Get("ct_layer") is null && c.Level == 1 && Math.Abs(c.Scale - 1) < 1e-6 &&
              c.Ai.Get("aggroSetup.viewRange", out var priv) == "18" && !priv,
              "RevertJson of the owner removes its variant and reverts its creature to the template (level 1, scale 1, shared viewRange 18)");
    }

    // ---------------------------------------------------------------------------------------------- probe 2 + commands ----
    private async Task Readiness(EntityId a, EntityId b)
    {
        var s = Creatures.Status!;
        if (!s.LabReady)
        {
            Info("lab readiness switch off (kiln/data/kiln.creatures/force-ready): readiness, state-event and command effect checks skipped");
            return;
        }
        _states.Clear();
        _targets.Clear();
        var ca = Creatures.Of(a);
        Check(ca.State == EnemyState.Spawning && !ca.Inspect()!.Ready, $"without a player A stays in {ca.State} (not ready)");
        ca.ForceReady();
        await Steps(125);
        var i = ca.Inspect()!;
        var mine = _states.Where(e => e.Entity == a).ToList();
        Info($"A state events: {string.Join(", ", mine.Take(12).Select(e => $"{e.From}->{e.To} ({e.Condition})"))}");
        Check(i.Ready && i.ForcedReady, "the lab switch holds A ready for gameplay");
        Check(mine.Any(e => e.From == EnemyState.Spawning && e.Condition == StateCondition.IsFinished),
              "CreatureStateEvent: A left Spawning (IsFinished) once ready");
        Check(mine.Any(e => e.To is EnemyState.Idle or EnemyState.Roaming), "CreatureStateEvent: Idle / Roaming follow on its own");
        Check(_states.All(e => e.Entity != b), "B (not ready) had no state change");

        // commands: guard B, then attack B (B on the players' side)
        var sent0 = Creatures.Status!.CommandsSent;
        ca.Command.Guard(b);
        await Steps(50);
        var g = ca.Inspect()!;
        Info($"after Guard(B): state {g.State}, guard {g.Guard}");
        Check(Creatures.Status!.CommandsSent == sent0 + 1, "StartGuardingEvent was sent by kiln_creatures");
        Check(g.Guard == b, $"enemy01 took the guard order: guardEntityId {g.Guard} (B = {b})");
        ca.Command.StopGuarding(b);
        var stopSteps = 0;
        while (!ca.Inspect()!.Guard.IsNone && stopSteps < 150) { await Steps(5); stopSteps += 5; }
        // usually within a few steps; sometimes the game takes it only from Kiln's second StopGuardingEvent (after 1 s)
        Check(ca.Inspect()!.Guard.IsNone, $"StopGuardingEvent clears it ({ca.Inspect()!.Guard}, after {stopSteps} steps)");
        var cb = Creatures.Of(b);
        cb.Faction = Faction.PlayerAlly;
        cb.ForceReady();
        _targets.Clear();
        ca.Command.Attack(b, 5000);
        await Steps(100);
        var t = ca.Inspect()!;
        Info($"after Attack(B): state {t.State}, target {t.Target}; target events {string.Join(", ", _targets.Select(e => $"{e.Entity}: {e.Previous}->{e.Target}"))}");
        Check(t.Target == b || _targets.Any(e => e.Entity == a && e.Target == b),
              $"AddThreatTargetEvent: A targets B (SelectedTargets {t.Target}, a CreatureTargetEvent {(_targets.Any(e => e.Entity == a && e.Target == b) ? "fired" : "missing")})");
        ca.ForceReady(false);
        cb.ForceReady(false);
        cb.Reset();
    }

    // ---------------------------------------------------------------------------------------------- guarding ----
    // A guard order kept by Kiln. Without a player there is no path grid, so the game's Guard state gives up
    // after 0.5 s (path walker TargetNotReachable, the same pattern seen in a real client): here the catch-up has to
    // bring the guard to its target. Positions over time go to the log.
    private async Task Guarding()
    {
        if (!(Creatures.Status?.LabReady ?? false))
        {
            Info("lab readiness switch off: guard checks skipped");
            return;
        }
        var a = await SpawnVukah(0, 30);
        var b = await SpawnVukah(4, 30);
        var c = await SpawnVukah(-50, 80);   // far away: the attacker for "defend"
        var d = await SpawnVukah(-50, -20);  // far away: the victim for "assist"
        await Steps(3);
        CreatureRef ca = Creatures.Of(a), cb = Creatures.Of(b), cc = Creatures.Of(c), cd = Creatures.Of(d);
        ca.Faction = Faction.PlayerAlly;
        cb.Faction = Faction.PlayerAlly;
        foreach (var x in new[] { ca, cb, cc, cd }) x.ForceReady();
        await Steps(25);
        var home0 = ca.Home;
        string Where() => $"A {World.Position(a)} B {World.Position(b)} dist {Dist(a, b):0.0} m, A {ca.State}, home {ca.Home}";
        ca.Command.Guard(b);
        await Steps(15);
        var order = Creatures.GuardOrders.FirstOrDefault(o => o.Guard == a);
        Check(order is not null && order.Target == b, $"Guard(B) keeps an order ({order?.Guard} -> {order?.Target}, owner {order?.Owner})");
        Check(ca.Inspect()!.Guard == b, $"the game took the order (guardEntityId {ca.Inspect()!.Guard})");
        Check(ca.Home.DistanceTo(World.Position(b)!.Value) < 0.5, $"its home zone follows the target: {ca.Home} (B at {World.Position(b)})");
        const EnemyFeatures focus = EnemyFeatures.Awareness | EnemyFeatures.Investigate;
        Check((ca.Features.Disabled & focus) == focus, $"Focus: Awareness and Investigate are off while it guards (disabled {ca.Features.Disabled})");

        // B moves 14 m: no path grid here, so the game's Guard state cannot walk it there; the guard stands still
        var sw = Stopwatch.StartNew();
        var t1 = await Teleport.To(b, World.Position(b)!.Value with { X = World.Position(b)!.Value.X + 14 });
        Info($"B moved 14 m: {t1}; {Where()}");
        var guardTries = _states.Count(e => e.Entity == a && e.To == EnemyState.Guard);
        double best = 1e9;
        for (var i = 0; i < 24 && best > 4; i++)
        {
            await Steps(10);
            best = Math.Min(best, Dist(a, b));
            if (i % 3 == 0 || best <= 4) Info($"guard t+{sw.Elapsed.TotalSeconds:0.0}s: {Where()}");
        }
        var tries = _states.Count(e => e.Entity == a && e.To == EnemyState.Guard) - guardTries;
        Check(tries > 0, $"the game's Guard state started on its own ({tries}x NeedsToGuard: B is beyond guardSetup.startDistance 10 m)");
        Check(best <= 4, $"the guard caught up after standing still (StuckSeconds 4): {Dist(a, b):0.0} m from B after {sw.Elapsed.TotalSeconds:0.0} s");
        // B moves 42 m: farther than CatchUpDistance (30) -> at once
        sw.Restart();
        var t2 = await Teleport.To(b, World.Position(b)!.Value with { X = World.Position(b)!.Value.X + 42 });
        Info($"B moved 42 m: {t2}; {Where()}");
        best = 1e9;
        for (var i = 0; i < 15 && best > 4; i++)
        {
            await Steps(5);
            best = Math.Min(best, Dist(a, b));
        }
        Info($"after the long move: {Where()}");
        Check(best <= 4 && sw.Elapsed.TotalSeconds < 4.5, $"a guard farther than 30 m is brought next to its target without waiting for StuckSeconds ({sw.Elapsed.TotalSeconds:0.0} s, {Dist(a, b):0.0} m; catch-ups are at most one per 2 s)");
        Check(ca.Home.DistanceTo(World.Position(b)!.Value) < 3.5, $"and its home followed ({ca.Home})");

        // the game drops the order (guardEntityId 0): Kiln sends it again
        var dyn = Raw.Dyn(_world, "DynamicEnemy01BehaviorState", a);
        if (dyn != 0)
        {
            unsafe { *(uint*)(dyn + 0x454) = 0; }
            var resent0 = Creatures.GuardOrders.First(o => o.Guard == a).Resent;
            await Steps(80);
            Check(Creatures.GuardOrders.First(o => o.Guard == a).Resent > resent0 && ca.Inspect()!.Guard == b,
                  $"a dropped order is sent again (resent {Creatures.GuardOrders.First(o => o.Guard == a).Resent}, guardEntityId {ca.Inspect()!.Guard})");
        }

        // defend: C hurts B -> A gets threat on C (AddThreatTargetEvent)
        _targets.Clear();
        var bp = World.Position(b)!.Value;
        await Teleport.To(c, bp with { X = bp.X - 6 });
        await Steps(5);
        var hit = await Combat.Damage(b, 1, c);
        await Steps(40);
        var threats1 = Creatures.GuardOrders.First(o => o.Guard == a).ThreatsSent;
        Check(threats1 >= 1 && (ca.Target == c || _targets.Any(e => e.Entity == a && e.Target == c)),
              $"defend: C ({c}) hit B ({hit}) -> A targets C (target {ca.Target}, {threats1} threat event(s))");
        // assist: B hits D -> threat on D
        await Teleport.To(d, bp with { Z = bp.Z + 6 });
        await Steps(5);
        await Combat.Damage(d, 1, b);
        await Steps(20);
        var threats2 = Creatures.GuardOrders.First(o => o.Guard == a).ThreatsSent;
        Check(threats2 > threats1, $"assist: B hit D ({d}) -> threat on D for A ({threats2} threat event(s))");

        // StopGuarding ends the order and gives the home back
        ca.Command.StopGuarding(b);
        await Steps(30);
        Check(!Creatures.GuardOrders.Any(o => o.Guard == a), "StopGuarding ends Kiln's order");
        Check(ca.Home.DistanceTo(home0) < 0.5, $"and the home zone is the one before the order again ({ca.Home}, was {home0})");
        Check((ca.Features.Disabled & focus) == 0, $"and Awareness / Investigate are back (disabled {ca.Features.Disabled})");
        // EventOnly: just the game's event
        ca.Command.Guard(b, GuardOptions.EventOnly);
        var eventSteps = 0;
        while (ca.Inspect()!.Guard != b && eventSteps < 60) { await Steps(5); eventSteps += 5; }  // as the first Guard above, the game may take a while
        await Steps(10);
        Check(!Creatures.GuardOrders.Any(o => o.Guard == a) && ca.Inspect()!.Guard == b,
              $"GuardOptions.EventOnly sends only the game's event (no order kept; guardEntityId {ca.Inspect()!.Guard}, after {eventSteps} steps)");
        ca.Command.StopGuarding(b);
        foreach (var x in new[] { ca, cb, cc, cd }) x.ForceReady(false);
        foreach (var x in new[] { a, b, c, d }) Spawn.Destroy(x);
    }

    private static double Dist(EntityId a, EntityId b) =>
        World.Position(a) is { } p && World.Position(b) is { } q ? p.DistanceTo(q) : 1e9;

    // ---------------------------------------------------------------------------------------------- pack JSON ----
    private Task PackJson()
    {
        var bad = Creatures.ApplyJson("""{ "variants": [ { "id": "x", "base": "NoSuchTemplate" }, { "base": "Enemy_Vukah_Melee" } ], "spawnRules": [ { "replace": "Enemy_Vukah_Melee", "with": "nope" } ], "extra": 1 }""", "pack:creatures-test-bad", "bad.json");
        Check(bad.Errors.Count == 3 && bad.Warnings.Count == 1 && bad.Applied == 0, $"ApplyJson reports bad entries ({bad.Errors.Count} errors, {bad.Warnings.Count} warning): {string.Join(" | ", bad.Errors)}");
        var ok = Creatures.ApplyJson("""{ "variants": [ { "id": "ct_rule", "base": "Vukah", "level": 3, "features": ["-FleeHome"], "faction": "PlayerAlly" } ], "spawnRules": [ { "replace": "Enemy_Vukah_Melee", "with": "ct_rule", "chance": 0.5, "night": true } ] }""", "pack:creatures-test-rule", "rule.json");
        Check(ok.Ok && ok.Applied == 2 && CreatureVariants.SpawnRules.Any(r => r.Variant == "ct_rule" && r.NightOnly && r.Chance == 0.5),
              "a spawn rule from JSON is registered (natural spawns need a player: client checklist)");
        var again = Creatures.ApplyJson("""{ "variants": [ { "id": "ct_rule", "base": "Vukah", "level": 4 } ] }""", "pack:creatures-test-rule", "rule.json");
        Check(again.Ok && CreatureVariants.Get("ct_rule")?.Level == 4 && !CreatureVariants.SpawnRules.Any(r => r.Variant == "ct_rule"),
              "applying the same source again replaces what it added (level 4, the rule gone)");
        Creatures.RevertJson("pack:creatures-test-rule");
        Creatures.RevertJson("pack:creatures-test-bad");
        Check(CreatureVariants.Get("ct_rule") is null, "RevertJson removes the owner's variants");
        // a rule whose variant has another base: kiln.gameplay swaps the spawner's template (spawn_replace)
        try
        {
            using var v = CreatureVariants.Register(new CreatureVariant { Id = "ct_armored", Base = "Enemy_Vukah_Melee_Elite_Armored", Level = 8 });
            using var rule = CreatureVariants.AddSpawnRule(new CreatureSpawnRule(Vukah, "ct_armored", 0.3));
            Check(CreatureVariants.SpawnRules.Any(r => r.Variant == "ct_armored"),
                  "a cross-template spawn rule (Vukah -> armored elite variant) is registered with kiln.gameplay's spawn_replace (kiln.log: spawn_replace ... chance 0.30)");
        }
        catch (Exception e)
        {
            Check(false, $"cross-template spawn rule: {e.Message}");
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------- EliteVukah ----
    private async Task Elite()
    {
        if (!Mods.IsLoaded("elite-vukah"))
        {
            Info("elite-vukah is not loaded: the sample check is skipped");
            return;
        }
        var v = CreatureVariants.Get("elite_vukah");
        Check(v is { Level: 20 }, $"EliteVukah registered its variant ({v?.Id}, level {v?.Level}, scale {v?.Scale})");
        Check(CreatureVariants.SpawnRules.Any(r => r.Variant == "elite_vukah"), "and its natural-spawn rule");
        var r = Commands.Execute(CommandSource.Console, $"creature spawn elite_vukah {Pos.X.ToString(CultureInfo.InvariantCulture)} {Pos.Y.ToString(CultureInfo.InvariantCulture)} {(Pos.Z - 12).ToString(CultureInfo.InvariantCulture)}");
        Info($"/creature spawn: {r}");
        var demo = Spawn.Tracked.Where(t => t.IsLive && t.Tag is "elite-demo" or "admin" && t.TemplateName == Vukah).Select(t => t.Entity).ToList();
        Check(r.Ok && demo.Count > 0, $"an elite spawned through /creature ({string.Join(",", demo)})");
        if (demo.Count == 0) return;
        _mine.AddRange(demo);
        await Steps(150);
        foreach (var e in demo)
        {
            var c = Creatures.Of(e);
            var i = c.Inspect()!;
            Check(i.Level == 20 && Math.Abs(i.Scale - 1.4f) < 1e-4 && c.Ai.Get("aggroSetup.viewRange") == "30" &&
                  Creatures.Of(_mine[0]).Ai.Get("aggroSetup.viewRange") == "18" && i.HealthMax > 417 &&
                  i.DisabledFeatures.HasFlag(EnemyFeatures.FleeHome) && Math.Abs(c.Resistance[DamageKind.Fire] - 0.5f) < 1e-6,
                  $"elite {e}: level {i.Level}, health {i.Health}/{i.HealthMax}, scale {i.Scale}, own view range {c.Ai.Get("aggroSetup.viewRange")} (others 18), fire 0.5, no FleeHome");
        }
        var status = (await Mods.UnloadAsync("elite-vukah"));
        Info($"unload elite-vukah: {status}");
        await Steps(10);
        foreach (var e in demo)
        {
            var c = Creatures.Of(e);
            var i = c.Inspect()!;
            Check(i.Level == 1 && Math.Abs(i.Scale - 1) < 1e-6 && c.Ai.Get("aggroSetup.viewRange", out var priv) == "18" && !priv &&
                  i.DisabledFeatures == EnemyFeatures.None && i.Layers == 0 && i.PrivateCopies == 0 &&
                  CreatureVariants.Get("elite_vukah") is null,
                  $"elite {e} after the unload: level {i.Level}, scale {i.Scale}, view range back to the template, no layers / copies; variant gone");
        }
        var folder = Mods.All.FirstOrDefault(m => m.Id == "elite-vukah")?.Directory;
        if (folder != null)
        {
            var back = await Mods.LoadAsync(folder);
            Info($"load elite-vukah again: {back}");
        }
    }

    // ---------------------------------------------------------------------------------------------- despawn ----
    private async Task DespawnCleanup()
    {
        var e = await SpawnVukah(-6, -6);
        var c = Creatures.Of(e);
        c.Level = 5;
        c.Ai.Set("aggroSetup.viewRange", 50);
        var s0 = Creatures.Status!;
        Spawn.Destroy(e);
        await Steps(3);
        var s1 = Creatures.Status!;
        Check(s1.Entities == s0.Entities - 1 && s1.PrivateCopies == s0.PrivateCopies - 1 && s1.PendingFree == s0.PendingFree + 1,
              $"a destroyed creature's state is dropped by the next Pre ({s0.Entities} -> {s1.Entities} entities, copy pending free {s0.PendingFree} -> {s1.PendingFree})");
        try
        {
            c.Level = 6;
            Check(false, "setting a value on a gone creature throws");
        }
        catch (KeyNotFoundException) { Check(true, "setting a value on a gone creature throws KeyNotFoundException"); }
    }
}

// Raw engine access for the probe checks (sync: no await in unsafe code).
internal static unsafe class Raw
{
    public static nuint Tpl(nuint world, string comp, EntityId e) =>
        world == 0 ? 0 : ((delegate* unmanaged<nuint, uint, uint, nuint>)Symbols.Get("GetTemplateComponent"))(world, World.ComponentHash(comp), e.Value);

    public static nuint Dyn(nuint world, string comp, EntityId e)
    {
        ulong* r = stackalloc ulong[2];
        r[0] = r[1] = 0;
        ((delegate* unmanaged<ulong*, nuint, uint, uint, ulong*>)Symbols.Get("GetComponent"))(r, world, World.ComponentHash(comp), e.Value);
        return (nuint)r[0];
    }

    public sealed class GuidReads
    {
        public Guid Want, Old;
        public long Hits, OldHits;
    }

    // ArsenalFind(registry, const Guid*): enemy01's per-entity arsenal lookup (rdx = the GUID it looks up)
    public static HookHandle CountArsenalLookups(GuidReads r) =>
        Kiln.Hooks.Mid("creatures/ArsenalFind", (ref Registers reg) =>
        {
            var g = new Guid(new ReadOnlySpan<byte>((void*)reg.Rdx, 16));
            if (g == r.Want) Interlocked.Increment(ref r.Hits);
            else if (g == r.Old) Interlocked.Increment(ref r.OldHits);
        });

    public sealed class Reads
    {
        public nuint Dyn, Copy;
        public long Copy_, Orig, Other;
    }

    // enemy01's behaviour update: ctx + ctx.dyn = the entity's dynamic state, ctx + ctx.tpl = the template it reads
    public static HookHandle CountReads(Reads r)
    {
        var ctxDyn = (nuint)Constants.Get("creatures/ctx.dyn");
        var ctxTpl = (nuint)Constants.Get("creatures/ctx.tpl");
        return Kiln.Hooks.Mid("creatures/BehaviorUpdate", (ref Registers reg) =>
        {
            var dyn = *(nuint*)(reg.Rcx + ctxDyn);
            var tpl = *(nuint*)(reg.Rcx + ctxTpl);
            if (dyn == r.Dyn) { if (tpl == r.Copy) Interlocked.Increment(ref r.Copy_); else Interlocked.Increment(ref r.Orig); }
            else if (tpl == r.Copy) Interlocked.Increment(ref r.Other);
        });
    }
}
