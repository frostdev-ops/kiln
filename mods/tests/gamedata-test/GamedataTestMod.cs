// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace GamedataTest;

/// <summary>
/// kiln.gamedata lab test (no player connected). Phase "fresh" (no marker): the registries, the mod's pack
/// (content/gamedata-test.json: item configs with tooltips, a template collider and configs including an added one,
/// a glob with @where, sequence event times with the order kept, an arsenal attack, a skill node, an external section),
/// code layers over it with a conflict, refusals, the live data spawned entities read (GetTemplateComponent of a
/// spawned Vukah = the template data Kiln changed; a spawned Fireball projectile's LifeTime), the content-sync round
/// trip, a dump. It leaves its code layers in place and writes a marker; then:
///  - after <c>reload gamedata-test</c> the new instance checks the code layers came off and the pack stayed;
///  - after a restart it checks the pack applied at world load and re-applies its code layers.
/// </summary>
public sealed class GamedataTestMod : Mod
{
    private const string Fireball = "Ammo_T1_Spell_Fire_Ball_lvl10", DoubleJump = "Ability_DoubleJump";
    private const string Explosion = "Explosion_Player_Spell_Fireball_1", Projectile = "Projectile_Player_Staff_Fireball_1";
    private const string Vukah = "Enemy_Vukah_Melee", Cast = "Attack_Player_Staff_FixedAnim_Fireball_fast";
    private const string ViewRange = "Enemy01BehaviorState.aggroSetup.viewRange";
    private const string Radius = "ColliderResourceComponent.colliders[0].dataArray[0].shape.radius";
    private const string Noise = "ImpactConfiguration.configValues.NoiseRange", PushBack = "ImpactConfiguration.configValues.PushBackStrength";
    private const string Life = "LifeTime.lifeTime";
    private const string Elite = "Kiln_Test_Elite_Vukah";
    private static readonly Guid EliteGuid = Guid.Parse("0b7f5a1e-6c2d-4e8a-9f31-5d2c7b9e4a10");
    private static readonly WorldPosition Far = new(3865.8, 833.0, 1433.8);  // 60 m from the copied world's base
    // perk and buff clones: the mod gamedata-clones (Clones/content/clones.json)
    private const string ClonesMod = "gamedata-clones";
    private const string Frostbite = "Kiln_Test_Frostbite", FrostbitePerk = "Kiln_Test_Frostbite_Perk";
    private static readonly Guid FrostbiteGuid = Guid.Parse("5e1d0c3b-7a92-4c61-b8f4-2d9e6a1c3f07");
    private static readonly Guid FrostbitePerkGuid = Guid.Parse("9c4f2e81-3b6d-4a07-9e15-7f8a2c6d4b30");
    private const string PoisonBuff = "0x5f425faf", PoisonPerk = "Perk_OnDamage_Dealt_Debuff_Poisoned_T2", PerkSword = "Weapon_T1_1H_Sword_01";
    private static readonly Guid ShortDebuffGuid = Guid.Parse("a00dbdc0-0394-43c5-83cb-96c6c425dec9");  // 0xc0c267ae, a 3 s debuff
    private static bool ClonesLoaded => Mods.All.Any(m => m.Id == ClonesMod && m.State == ModState.Loaded);

    private int _pass, _fail;
    private bool _stop, _ran;
    private readonly List<(string Owner, string Source, string Json)> _sectionCalls = [];
    private readonly List<(string Owner, string? Source)> _sectionReverts = [];

    public override void OnLoad()
    {
        Log.Info($"gamedata-test: OnLoad (gamedata {(Content.GameDataAvailable ? "available" : "MISSING")})");
        if (!Content.GameDataAvailable) return;
        Content.RegisterSection("gdtest", c =>
        {
            _sectionCalls.Add((c.Owner, c.Source, c.Json));
            return ContentSectionResult.Ok(1);
        }, (o, s) => _sectionReverts.Add((o, s)));
        Content.OnReady(e =>
        {
            if (_ran) return;
            _ran = true;
            _ = RunAsync(e);
        });
    }

    public override void OnUnload()
    {
        _stop = true;
        Content.UnregisterSection("gdtest");
        Log.Info("gamedata-test: OnUnload (its code layers come off now)");
    }

    private string Marker() => Path.Combine(Context.DataDirectory, "phase.json");

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    private static string ProcessStart() => Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");

    private async Task RunAsync(ContentReadyEvent e)
    {
        try
        {
            var marker = Marker();
            if (!File.Exists(marker)) await Fresh(e);
            else
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                var same = doc.RootElement.GetProperty("pid").GetInt32() == Environment.ProcessId &&
                           doc.RootElement.GetProperty("start").GetString() == ProcessStart();
                if (same) AfterReload();
                else AfterRestart(e);
            }
        }
        catch (Exception ex)
        {
            Check(false, $"test aborted: {ex}");
        }
        Log.Info($"gamedata-test done: {_pass} PASS, {_fail} FAIL");
    }

    private void WriteMarker(string phase)
    {
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Marker(), $"{{\"phase\": \"{phase}\", \"pid\": {Environment.ProcessId}, \"start\": \"{ProcessStart()}\"}}");
    }

    private static double Num(GameDataEntry e, string path) => e.Get(path).AsDouble;

    private static string Val(GameDataEntry e, string path) => e.GetJson(path)?["value"]?.ToString() ?? "?";

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-4;

    // ------------------------------------------------------------------------------------------- fresh ---------

    private async Task Fresh(ContentReadyEvent e)
    {
        var status = JsonNode.Parse(Content.GameDataStatus())!;
        var secs = status["sections"]!;
        int Count(string s) => (int?)secs[s]?["count"] ?? 0;
        Check((bool?)status["ready"] == true && Count("templates") > 10000 && Count("sequences") > 2000 && Count("arsenals") > 100 &&
              Count("buffs") > 100 && Count("perks") > 100 && Count("skills") > 100,
            $"ready (generation {e.Generation}, through {status["resources"]}): {Count("templates")} templates, {Count("arsenals")} arsenals, " +
            $"{Count("sequences")} sub-sequences, {Count("buffs")} buffs, {Count("perks")} perks, {Count("skills")} skill nodes, {Count("items")} items");

        // ---- lookups ----
        var vukah = Content.Templates.Get(Vukah);
        var cast = Content.Sequences.Get("deff43ba-530a-4bee-be84-9b3c781e9556");
        var arsenal = Content.Arsenals.Get("template:" + Vukah);
        var jumpSkill = Content.Skills.Get("item:" + DoubleJump);
        Check(vukah?.Guid == Guid.Parse("d5d05217-52dc-467a-9192-26b4f661d8d9") && cast?.Name == Cast &&
              arsenal?.Guid == Guid.Parse("e30312e7-9749-4329-8bfd-469a295747b5") && jumpSkill != null && Content.Perks.Get("Perk_Damage_Ice") != null,
            $"lookups: {vukah} by name, {cast} by sequence GUID, arsenal {arsenal?.Name} by template:, skill {jumpSkill} by item:, a perk by debug name");

        // ---- the pack ----
        var fb = Content.ItemData.Get(Fireball)!;
        Check(Num(fb, "impactValues.ManaCost") == 30 && fb.Original("impactValues.ManaCost").AsDouble == 53,
            $"pack: {Fireball} impactValues.ManaCost {Num(fb, "impactValues.ManaCost")} (original {fb.Original("impactValues.ManaCost")})");
        Check(Num(fb, "uiValues[0].value") == 30 && fb.Original("uiValues[0].value").AsDouble == 53,
            $"pack: the tooltip value followed (uiValues[0].value {Num(fb, "uiValues[0].value")}, original {fb.Original("uiValues[0].value")}); " +
            $"the duration one stayed {Num(fb, "uiValues[1].value")}");
        var dj = Content.Items.Get(DoubleJump)!.Config("StaminaCost");
        Check(dj.Get().AsDouble == 5 && dj.Original().AsDouble == 10, $"pack: {DoubleJump} StaminaCost {dj.Get()} (original {dj.Original()}) via item.Config()");
        var ex = Content.Templates.Get(Explosion)!;
        Check(Near(Num(ex, Radius), 2.6 * 1.5) && Near(ex.Original(Radius).AsDouble, 2.6),
            $"pack: {Explosion} collider radius {Num(ex, Radius):0.###} (original {ex.Original(Radius)}; x1.5)");
        Check(Num(ex, Noise) == 40 && ex.Original(Noise).AsDouble == 25 && Num(ex, "ImpactConfiguration.configValues[NoiseRange]") == 40 &&
              Num(ex, "ImpactConfiguration.configValues.0xa19409b2") == 40,
            $"pack: {Explosion} NoiseRange {Num(ex, Noise)} (original 25; also as [NoiseRange] and 0xa19409b2)");
        Check(Val(ex, PushBack) == "Big" && ex.Config("PushBackStrength").Exists,
            $"pack: {Explosion} PushBackStrength {Val(ex, PushBack)} (added: the template had no such config)");
        Check(Near(Num(vukah!, ViewRange), 27) && Near(vukah!.Original(ViewRange).AsDouble, 18),
            $"pack: {Vukah} {ViewRange} {Num(vukah, ViewRange)} (original 18; glob Enemy_Vukah_* with @where Faction Vukah)");
        var vukahs = Content.Templates.Where(t => t.Name.StartsWith("Enemy_Vukah_", StringComparison.OrdinalIgnoreCase)).ToList();
        var changed = vukahs.Count(t => t.Modified);
        var notVukah = vukahs.Where(t => !t.Modified).Select(t => t.Name).Take(3).ToList();
        Check(changed > 0, $"pack: {changed} of {vukahs.Count} Enemy_Vukah_* templates changed (faction Vukah); unchanged e.g. {string.Join(", ", notVukah)}");
        var spawnT = Num(cast!, "events[SpawnEntityEvent#0].time");
        var times = (cast.GetJson("events[*].time") as JsonArray)?.Select(x => (double)x!["value"]!).ToList() ?? [];
        var sorted = times.Zip(times.Skip(1)).All(p => p.First <= p.Second);
        var spawnIndex = (cast.GetJson("events[*]") as JsonArray)?.Select((x, i) => (x, i))
            .Where(p => ((string?)p.x!["type"])?.EndsWith("::SpawnEntityEvent") == true).Select(p => p.i).DefaultIfEmpty(-1).First() ?? -1;
        Check(Near(spawnT, 0.45) && Near(cast.Original("events[SpawnEntityEvent#0].time").AsDouble, 0.65) && sorted,
            $"pack: {Cast} SpawnEntityEvent at {spawnT:0.###} s (original 0.65), PayUsageCost at {Num(cast, "events[PayUsageCost#0].time"):0.###}; " +
            $"the {times.Count} events stay sorted by time (the spawn is event {spawnIndex} now)");
        Check(Num(arsenal!, "attacks[0].description.maxRange") == 3.5 && arsenal.Original("attacks[0].description.maxRange").AsDouble == 2.5,
            $"pack: arsenal of {Vukah} attacks[0].description.maxRange {Num(arsenal, "attacks[0].description.maxRange")} (original 2.5)");
        Check(Num(jumpSkill!, "costs") == 1, $"pack: skill node {jumpSkill} (unlocks {DoubleJump}) costs {Num(jumpSkill, "costs")} (original {jumpSkill.Original("costs")})");
        for (var i = 0; i < 25 && _sectionCalls.Count == 0; i++) await Steps(1);
        Check(_sectionCalls.Any(c => c.Owner == "pack:gamedata-test" && c.Source.EndsWith("gamedata-test.json") && c.Json.Contains("hello")),
            $"external section \"gdtest\": the handler got the pack's section ({string.Join("; ", _sectionCalls.Select(c => $"{c.Owner} {c.Source} {c.Json}"))})");

        // ---- a template clone (pack "add") ----
        var elite = Content.Templates.Get(Elite);
        Check(elite is { IsClone: true } && elite.Guid == EliteGuid && Content.Templates.IsClone(EliteGuid) &&
              Near(Num(elite, ViewRange), 40) && Near(Num(elite, "ScaleComponent.uniformScale"), 1.4) && Num(elite, "Level.dataStorage[0]") == 20 &&
              Near(Num(vukah, ViewRange), 27),
            $"clone: {elite} of {Vukah}: viewRange {(elite != null ? Num(elite, ViewRange) : 0)}, scale {(elite != null ? Num(elite, "ScaleComponent.uniformScale") : 0):0.##}, " +
            $"level {(elite != null ? Num(elite, "Level.dataStorage[0]") : 0)}; the base keeps {Num(vukah, ViewRange)}");

        // ---- a sequence clone (pack "add"), an item rebound to it (code) ----
        var castClone = Content.Sequences.Get("Kiln_Test_Fireball_Cast");
        Check(castClone is { IsClone: true } && Near(Num(castClone, "events[SpawnEntityEvent#0].time"), 0.3) && Near(spawnT, 0.45),
            $"clone: {castClone} of {Cast}: SpawnEntityEvent at {(castClone != null ? Num(castClone, "events[SpawnEntityEvent#0].time") : 0):0.##} s (the base keeps 0.45)");
        if (castClone != null)
        {
            Check(fb.Get("sequences[0].sequence").Guid == castClone.Guid,
                $"pack: {Fireball} sequences[0].sequence names the clone defined in the same pack ({fb.Get("sequences[0].sequence").Guid})");
            fb.Set("sequences[0].sequence", castClone.Name);
            var bound = fb.Get("sequences[0].sequence").Guid;
            var sid = (uint)fb.Get("sequences[0].sequenceId").Integer;
            Check(bound == castClone.Guid && sid == Fnv(castClone.Guid.ToByteArray()) && fb.Original("sequences[0].sequence").Guid == cast.Guid,
                $"code: {Fireball} sequences[0].sequence -> {bound} (the clone), sequenceId 0x{sid:x8} = FNV-1a of its GUID (original {fb.Original("sequences[0].sequence").Guid})");
        }

        // ---- perk and buff clones (the mod gamedata-clones) ----
        PerkBuffClones("clones");

        // ---- code layers ----
        var proj = Content.Templates.Get(Projectile)!;
        proj.Set(Life, TimeSpan.FromSeconds(8));
        vukah.Set(ViewRange, 30.0);
        var buff = Content.Buffs.FirstOrDefault(b => b.Get("defaultLifeTime").AsDouble > 0);
        buff?.SetJson("defaultLifeTime", "{\"mul\": 2}");
        var progBuff = Content.Buffs.FirstOrDefault(b => b.Get("impact").Guid != Guid.Empty);
        Check(progBuff != null && !Throws(() => progBuff.Set("impact", progBuff.Get("impact").Guid)) && progBuff.Reset("impact"),
            $"code: a buff's program reference ({progBuff?.Name} impact {progBuff?.Get("impact").Guid}) takes a loaded ImpactProgram GUID");
        var perk = Content.Perks.Get("Perk_Damage_Ice")!;
        var perkLeaf = perk.Fields("damageModifier").FirstOrDefault(f => f.Value is JsonValue v && v.TryGetValue<double>(out var d) && d != 0 && f.Writable);
        if (perkLeaf != null) perk.SetJson(perkLeaf.Path, "{\"mul\": 2}");
        Check(Near(Num(proj, Life), 8) && Near(proj.Original(Life).AsDouble, 5), $"code: {Projectile} {Life} {Num(proj, Life)} s (original {proj.Original(Life)})");
        Check(buff != null && Near(buff.Get("defaultLifeTime").AsDouble, buff.Original("defaultLifeTime").AsDouble * 2),
            $"code: {buff} defaultLifeTime {buff?.Get("defaultLifeTime")} (original {buff?.Original("defaultLifeTime")})");
        Check(perkLeaf != null && Near(perk.Get(perkLeaf.Path).AsDouble, perk.Original(perkLeaf.Path).AsDouble * 2),
            $"code: Perk_Damage_Ice {perkLeaf?.Path} {(perkLeaf != null ? perk.Get(perkLeaf.Path) : default)} (x2)");
        var st = Content.Status()!;
        Check(Near(Num(vukah, ViewRange), 30) && st.Conflicts > 0,
            $"code over pack: {Vukah} {ViewRange} {Num(vukah, ViewRange)} (code 30 wins over the pack's 27; {st.Conflicts} conflict(s) reported)");

        // ---- refusals ----
        Check(Throws(() => arsenal.Set("guid", Guid.NewGuid())) && Throws(() => ex.Set(PushBack, "Huge")) &&
              Throws(() => vukah.Set("Enemy01BehaviorState.noSuchField", 1)) && Throws(() => fb.Set("impactValues.NoSuchAlias", 1)),
            "refusals: an identity GUID, an unknown enum name, an unknown field, an unknown config alias");

        // ---- live data that spawned entities read ----
        await LiveChecks();

        // ---- fields, dump, sync ----
        var fields = ex.Fields("ImpactConfiguration");
        Check(fields.Any(f => f.Path.EndsWith(".NoiseRange") && f.Original != null) && fields.Any(f => f.Path.EndsWith(".PushBackStrength")),
            $"fields: {fields.Count} leaves under {Explosion}.ImpactConfiguration (NoiseRange with its original, the added PushBackStrength)");
        var dump = Content.Templates.Dump(Vukah);
        Check(File.Exists(dump) && File.ReadAllText(dump).Contains("aggroSetup"), $"dump: {dump}");
        var rt = ContentSync.CheckRoundTrip();
        Check(rt.Ok, $"content sync round trip with the gamedata sections: {rt}");

        // the world's WorldData now holds the clones' record (kept as placeholders if gamedata-clones goes away)
        var saved = await Saves.Request();
        Check(saved.Success, $"saved the world (the missing-gamedata record goes with it): {saved}");
        WriteMarker("fresh-done");
        Log.Info("gamedata-test: code layers stay in place (reload / unload the mod, or restart the server, for the next phases)");
    }

    private static uint Fnv(byte[] b)
    {
        var h = 2166136261u;
        foreach (var x in b) h = (h ^ x) * 16777619u;
        return h;
    }

    private static bool Throws(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------- live ----------

    private nuint _world;

    private async Task LiveChecks()
    {
        if (!Spawn.Available)
        {
            Check(false, "live: kiln.spawn is not available");
            return;
        }
        // kiln.content is ready ~11 s before the save is read; spawns need the running world
        var sw = Stopwatch.StartNew();
        while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240 && !_stop) await Steps(1);
        await Steps(25);
        var hook = Kiln.Hooks.Detour("GetComponent", call =>
        {
            if (_world == 0) _world = call[1];
            return call.CallOriginal();
        });
        _ = World.ReadBytes(new EntityId(1), "CurrentTransform");
        hook.Remove();
        var tpl = Symbols.Get("GetTemplateComponent");
        var a = await Spawn.Entity(Vukah, Far, 0, new SpawnOptions { Tag = "gamedata-test", Flags = SpawnFlags.Transient });
        var p = await Spawn.Entity(Projectile, Far with { X = Far.X + 6 }, 0, new SpawnOptions { Tag = "gamedata-test", Flags = SpawnFlags.Transient });
        EntityId c = default;
        try
        {
            c = await Spawn.Entity(Elite, Far with { Z = Far.Z + 6 }, 0, new SpawnOptions { Tag = "gamedata-test", Flags = SpawnFlags.Transient });
        }
        catch (Exception ex)
        {
            Check(false, $"live: spawning the clone {Elite}: {ex.Message}");
        }
        await Steps(5);
        try
        {
            var vukah = Content.Templates.Get(Vukah)!;
            var comp = (ulong?)vukah.GetJson("Enemy01BehaviorState")?["address"] ?? 0;
            var live = TemplateComponent(tpl, "Enemy01BehaviorState", a);
            var addr = (ulong?)vukah.GetJson(ViewRange)?["address"] ?? 0;
            var liveView = live != 0 && addr >= comp ? ReadF32(live + (nuint)(addr - comp)) : float.NaN;
            Check(live != 0 && live == (nuint)comp && Math.Abs(liveView - 30) < 1e-4,
                $"live: spawned {Vukah} {a}: GetTemplateComponent(Enemy01BehaviorState) = 0x{live:X} = the template data Kiln changed (0x{comp:X}); " +
                $"the AI reads viewRange {liveView}");
            var dyn = World.ComponentSize(p, "LifeTime");
            double lifeS;
            string how;
            if (dyn > 0)
            {
                var b = World.ReadBytes(p, "LifeTime")!;
                lifeS = BitConverter.ToInt64(b, 0) / 1e9;
                how = "its own LifeTime (copied from the template at spawn)";
            }
            else
            {
                var pl = TemplateComponent(tpl, "LifeTime", p);
                lifeS = pl != 0 ? ReadI64(pl) / 1e9 : double.NaN;
                how = "the template's LifeTime";
            }
            Check(Math.Abs(lifeS - 8) < 1e-6, $"live: spawned {Projectile} {p}: {how} = {lifeS} s (template set to 8 s by code)");
            if (!c.IsNone)
            {
                var elite = Content.Templates.Get(Elite)!;
                var ecomp = (ulong?)elite.GetJson("Enemy01BehaviorState")?["address"] ?? 0;
                var elive = TemplateComponent(tpl, "Enemy01BehaviorState", c);
                var eview = elive != 0 ? ReadF32(elive + (nuint)(((ulong?)elite.GetJson(ViewRange)?["address"] ?? 0) - ecomp)) : float.NaN;
                var lvlOff = (int)(((ulong?)elite.GetJson("Level.dataStorage[0]")?["address"] ?? 0) - ((ulong?)elite.GetJson("Level")?["address"] ?? 0));
                var lvl = World.ReadBytes(c, "Level");
                var scale = World.ReadBytes(c, "ScaleComponent");
                var lvlNow = lvl != null && lvlOff >= 0 && lvlOff + 4 <= lvl.Length ? BitConverter.ToSingle(lvl, lvlOff) : float.NaN;
                var lvlInt = lvl != null && lvlOff >= 0 && lvlOff + 4 <= lvl.Length ? BitConverter.ToInt32(lvl, lvlOff) : -1;
                var scaleNow = scale is { Length: >= 4 } ? BitConverter.ToSingle(scale, 0) : float.NaN;
                Check(elive != 0 && elive == (nuint)ecomp && Math.Abs(eview - 40) < 1e-4,
                    $"live: spawned the clone {Elite} {c}: GetTemplateComponent(Enemy01BehaviorState) = 0x{elive:X} = the clone's own data (0x{ecomp:X}); its AI reads viewRange {eview}");
                Check(Math.Abs(scaleNow - 1.4f) < 1e-4 && (Math.Abs(lvlNow - 20) < 1e-4 || lvlInt == 20),
                    $"live: the clone's own Level {(Math.Abs(lvlNow - 20) < 1e-4 ? lvlNow : lvlInt)} and ScaleComponent {scaleNow} (from the clone's template at spawn)");
            }
            await LiveBuffs(a);
        }
        finally
        {
            Spawn.Destroy(a);
            Spawn.Destroy(p);
            if (!c.IsNone) Spawn.Destroy(c);
        }
    }

    // ------------------------------------------------------------------------------------------- perks, buffs --

    private void PerkBuffClones(string phase)
    {
        if (!ClonesLoaded)
        {
            Check(false, $"{phase}: the mod {ClonesMod} is not loaded (build and deploy mods/tests/gamedata-test/Clones)");
            return;
        }
        var bid = Fnv(FrostbiteGuid.ToByteArray());
        var fb = Content.Buffs.Get(Frostbite);
        var poison = Content.Buffs.Get(PoisonBuff)!;
        Check(fb is { IsClone: true } && fb.Guid == FrostbiteGuid && fb.Id == bid && Content.Buffs.Get(FrostbiteGuid)?.Id == bid &&
              Content.Buffs.Get(bid)?.Name == Frostbite && (uint)fb.Get("buffTypeId").Integer == bid &&
              Near(fb.Get("defaultLifeTime").AsDouble, 7) && Near(fb.Original("defaultLifeTime").AsDouble, poison.Get("defaultLifeTime").AsDouble) &&
              fb.Get("impact").Guid == poison.Get("impact").Guid && Near(poison.Get("defaultLifeTime").AsDouble, 5),
            $"{phase}: buff clone {fb} of {poison}: buffTypeId 0x{(fb != null ? (uint)fb.Get("buffTypeId").Integer : 0):x8} = FNV-1a of its GUID " +
            $"(found by name, GUID and id), defaultLifeTime {fb?.Get("defaultLifeTime")} (the base's {poison.Get("defaultLifeTime")}), the base's program {fb?.Get("impact").Guid}");
        Check(fb != null && fb.Get("name").Guid != poison.Get("name").Guid && fb.Get("name").Guid != Guid.Empty &&
              fb.Get("description").Guid != poison.Get("description").Guid,
            $"{phase}: the buff clone has its own loca tags: name {fb?.Get("name").Guid}, description {fb?.Get("description").Guid} " +
            $"(the base's {poison.Get("name").Guid}; texts en and de, shown by Kiln clients)");
        var pid = Fnv(FrostbitePerkGuid.ToByteArray());
        var perk = Content.Perks.Get(FrostbitePerk);
        var pbase = Content.Perks.Get(PoisonPerk)!;
        Check(perk is { IsClone: true } && perk.Guid == FrostbitePerkGuid && perk.Id == pid && Content.Perks.Get(FrostbitePerkGuid)?.Name == FrostbitePerk &&
              (string?)perk.GetJson("debugName")?["value"] == FrostbitePerk && perk.Get("impact").Guid == pbase.Get("impact").Guid &&
              perk.Get("name").Guid != pbase.Get("name").Guid,
            $"{phase}: perk clone {perk} of {pbase}: debugName {perk?.GetJson("debugName")?["value"]}, its own name tag {perk?.Get("name").Guid}, the base's program");
        Check(perk != null && perk.Get("impactValues.BuffType").Guid == FrostbiteGuid && pbase.Get("impactValues.BuffType").Guid != FrostbiteGuid,
            $"{phase}: the perk clone applies the buff clone: impactValues.BuffType {perk?.Get("impactValues.BuffType").Guid} " +
            $"(the base keeps {pbase.Get("impactValues.BuffType").Guid})");
        var sword = Content.ItemData.Get(PerkSword)!;
        Check(sword.Get("perkReferences[4]").Guid == FrostbitePerkGuid && (uint)sword.Get("perkIds[4]").Integer == pid &&
              sword.Original("perkReferences[4]").Guid != FrostbitePerkGuid,
            $"{phase}: item {PerkSword} perkReferences[4] -> {sword.Get("perkReferences[4]").Guid} (the clone), perkIds[4] 0x{(uint)sword.Get("perkIds[4]").Integer:x8} " +
            $"followed (FNV-1a of its GUID; original {sword.Original("perkReferences[4]").Guid})");
        var skill = Content.Skills.Get("item:" + DoubleJump)!;
        var sv = skill.Config("0x5adb2644");  // "BuffType" names several config ids; the node has none of them
        Check(sv.Exists && sv.Get().Guid == FrostbiteGuid,
            $"{phase}: skill node {skill} configValues.0x5adb2644 (BuffType) -> {(sv.Exists ? sv.Get().Guid : Guid.Empty)} (the buff clone; a config the node did not have, added)");
        var defs = Content.GameDataDefinitions().Where(d => d.Mod == ClonesMod).ToList();
        Check(defs.Count == 2 && defs.All(d => d.State == "registered" && !d.IsPlaceholder) &&
              Content.Buffs.Definitions().Any(d => d.Guid == FrostbiteGuid) && Content.Perks.Definitions().Any(d => d.Guid == FrostbitePerkGuid),
            $"{phase}: definitions of {ClonesMod}: {string.Join("; ", defs.Select(d => $"{d.Section} {d.Name} {d.State} (owner {d.Owner})"))}");
    }

    private void Placeholders()
    {
        var defs = Content.GameDataDefinitions();
        var b = defs.FirstOrDefault(d => d.Guid == FrostbiteGuid);
        var p = defs.FirstOrDefault(d => d.Guid == FrostbitePerkGuid);
        Check(b is { IsPlaceholder: true, State: "registered" } && p is { IsPlaceholder: true, State: "registered" } && b.Mod == ClonesMod && p.Mod == ClonesMod,
            $"without {ClonesMod}: its clones are placeholders from world load on: {b?.Section} {b?.Name} {b?.State} placeholder {b?.IsPlaceholder}, " +
            $"{p?.Section} {p?.Name} {p?.State} placeholder {p?.IsPlaceholder} (mod {b?.Mod})");
        var fb = Content.Buffs.Get(FrostbiteGuid);
        var perk = Content.Perks.Get(FrostbitePerkGuid);
        Check(fb != null && fb.Id == Fnv(FrostbiteGuid.ToByteArray()) && fb.Get("impact").Guid == Guid.Empty && perk != null &&
              perk.Id == Fnv(FrostbitePerkGuid.ToByteArray()) && perk.Get("impact").Guid == Guid.Empty &&
              perk.Fields("impactValues").Count == 0 && fb.Fields("impactValues").Count == 0,
            $"without {ClonesMod}: the placeholders keep their ids (buff 0x{fb?.Id:x8}, perk 0x{perk?.Id:x8}: saved references resolve) and are inert " +
            $"(no program: {fb?.Get("impact").Guid} / {perk?.Get("impact").Guid}; the perk applies nothing)");
        var sword = Content.ItemData.Get(PerkSword)!;
        Check(sword.Get("perkReferences[4]").Guid != FrostbitePerkGuid, $"without {ClonesMod}: its item rule went with it ({PerkSword} perkReferences[4] {sword.Get("perkReferences[4]").Guid})");
    }

    // Buffs through the game's applyBuff (Kiln.Abilities) on a spawned Vukah: the buff clone, and a game buff changed by
    // code (the game reads Kiln's copy of the collection, which follows the game's records).
    private async Task LiveBuffs(EntityId vukah)
    {
        if (!Abilities.Available || !ClonesLoaded)
        {
            Check(false, $"live buffs: kiln.abilities {(Abilities.Available ? "ok" : "missing")}, {ClonesMod} {(ClonesLoaded ? "loaded" : "missing")}");
            return;
        }
        var shortB = Content.Buffs.Get(ShortDebuffGuid);
        var shortLife = shortB?.Get("defaultLifeTime").AsDouble ?? 0;
        shortB?.Set("defaultLifeTime", TimeSpan.FromSeconds(9));
        await Steps(2);
        EntityId e1 = default, e2 = default;
        try
        {
            e1 = await Abilities.ApplyBuff(vukah, FrostbiteGuid);
            e2 = await Abilities.ApplyBuff(vukah, ShortDebuffGuid);
        }
        catch (Exception ex)
        {
            Check(false, $"live buffs: ApplyBuff threw {ex.GetType().Name}: {ex.Message}");
        }
        await Steps(4);
        var nets = new List<(EntityId E, uint Type, double Life, double End)>();
        foreach (var x in World.Find("BuffNetworkData"))
            if (World.ReadBytes(x, "BuffNetworkData") is { Length: >= 24 } b)
                nets.Add((x, BitConverter.ToUInt32(b, 0x10), BitConverter.ToInt64(b, 0) / 1e9, BitConverter.ToInt64(b, 8) / 1e9));
        var bid = Fnv(FrostbiteGuid.ToByteArray());
        var sid = Fnv(ShortDebuffGuid.ToByteArray());
        var mine = nets.Where(n => n.Type == bid).ToList();
        var shortNet = nets.Where(n => n.Type == sid).ToList();
        Info($"live buffs: {nets.Count} entities with BuffNetworkData: {string.Join(", ", nets.Take(8).Select(n => $"{n.E} 0x{n.Type:x8} life {n.Life:0.##} end {n.End:0.##}"))}");
        var cont = World.ReadBytes(vukah, "BuffContainer");
        var types = new List<uint>();
        if (cont != null)
            for (var off = 0; off + 12 <= Math.Min(cont.Length, 0x1c8); off += 12)
                if (BitConverter.ToUInt32(cont, off + 4) is var t and not 0) types.Add(t);
        Check(mine.Count > 0 && mine.Any(n => Math.Abs(n.Life - 7) < 1e-3) && types.Contains(bid),
            $"live: Abilities.ApplyBuff({vukah}, {Frostbite}) through the game's applyBuff: buff entity {mine.FirstOrDefault().E} with BuffNetworkData " +
            $"buffTypeId 0x{bid:x8}, lifeTime {mine.FirstOrDefault().Life:0.##} s (the clone's 7 s; the base's 5 s); the Vukah's BuffContainer lists " +
            $"{string.Join(", ", types.Select(t => $"0x{t:x8}"))}");
        Check(shortNet.Any(n => Math.Abs(n.Life - 9) < 1e-3) && types.Contains(sid),
            $"live: a game buff changed by code (0x{sid:x8} defaultLifeTime {shortLife} -> 9 s) applied with lifeTime {shortNet.FirstOrDefault().Life:0.##} s: " +
            "the game reads it through Kiln's copy of the collection, which follows the change");
        shortB?.Reset("defaultLifeTime");
        foreach (var n in mine.Concat(shortNet)) Spawn.Destroy(n.E);
        if (!e1.IsNone) Spawn.Destroy(e1);
        if (!e2.IsNone) Spawn.Destroy(e2);
    }

    private void Info(string s) => Log.Info("gamedata-test: " + s);

    // GetTemplateComponent(world, component hash, entity): the template data the entity's programs read.
    private unsafe nuint TemplateComponent(nuint fn, string component, EntityId e) =>
        _world != 0 && fn != 0 ? ((delegate* unmanaged<nuint, uint, uint, nuint>)fn)(_world, World.ComponentHash(component), e.Value) : 0;

    private static unsafe float ReadF32(nuint a) => *(float*)a;
    private static unsafe long ReadI64(nuint a) => *(long*)a;

    // ------------------------------------------------------------------------------------------- later phases --

    private void AfterReload()
    {
        var proj = Content.Templates.Get(Projectile)!;
        var vukah = Content.Templates.Get(Vukah)!;
        Check(Near(Num(proj, Life), 5), $"after reload: {Projectile} {Life} back to {Num(proj, Life)} s (the old instance's code layer came off)");
        Check(Near(Num(vukah, ViewRange), 27), $"after reload: {Vukah} viewRange {Num(vukah, ViewRange)} (code layer off, the pack's 27 stayed)");
        Check(Num(Content.ItemData.Get(Fireball)!, "impactValues.ManaCost") == 30, "after reload: the pack's ManaCost 30 stayed (it follows the loaded mod)");
        proj.Set(Life, TimeSpan.FromSeconds(8));
        Check(Near(Num(proj, Life), 8), "after reload: code layer applied again (8 s)");
    }

    private void AfterRestart(ContentReadyEvent e)
    {
        var fb = Content.ItemData.Get(Fireball)!;
        var cast = Content.Sequences.Get(Cast)!;
        var times = (cast.GetJson("events[*].time") as JsonArray)?.Select(x => (double)x!["value"]!).ToList() ?? [];
        Check(e.Generation == 1 && Num(fb, "impactValues.ManaCost") == 30 && Num(fb, "uiValues[0].value") == 30 &&
              Near(Num(Content.Templates.Get(Vukah)!, ViewRange), 27) && Val(Content.Templates.Get(Explosion)!, PushBack) == "Big" &&
              times.Zip(times.Skip(1)).All(p => p.First <= p.Second) && Near(Num(cast, "events[SpawnEntityEvent#0].time"), 0.45),
            $"after restart: the pack applied at world load (generation {e.Generation}): ManaCost 30 + tooltip, viewRange 27, the added PushBackStrength, the sorted events");
        var proj = Content.Templates.Get(Projectile)!;
        Check(Near(Num(proj, Life), 5), "after restart: code layers are not there before OnReady re-applies them (5 s)");
        proj.Set(Life, TimeSpan.FromSeconds(8));
        Check(Near(Num(proj, Life), 8), "after restart: code layer re-applied in OnReady (8 s)");
        if (ClonesLoaded) PerkBuffClones("after restart");
        else Placeholders();
        File.Delete(Marker());
    }
}
