// SPDX-License-Identifier: MIT
// What Kiln.Blocks made of mods/samples/FrostboltBlock, player-free, on a dedicated server. A collision needs terrain streamed near a
// player, so a projectile cannot land here (docs/re/abilities.md "Lab limits"): the impact is run directly, the flight is checked as data.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace BlocksTest;

public sealed partial class BlocksTestMod : Mod
{
    private const string Sample = "frostbolt-block";
    private static readonly Guid Swing = Guid.Parse("1f6ab3c4-7d21-4c08-9e55-3a1b2c4d6e70");
    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();
    private readonly List<ImpactDamageEvent> _hits = new();
    private readonly Dictionary<ImpactDamageEvent, double> _hitAt = new(ReferenceEqualityComparer.Instance);  // the game's clock when a hit came, in seconds
    private readonly List<ImpactBuffEvent> _buffs = new();
    private WorldPosition _at;
    private uint _clock;  // the game's 60 Hz clock at the last step
    private double _longestStep;  // the longest step since it was last set to 0, in seconds of the game's clock

    private double GameSeconds => _clock / 60.0;

    private void Info(string s) => Log.Info($"blocks-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"blocks-test: PASS {what}"); }
        else { _fail++; Log.Error($"blocks-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static uint Fnv(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var c in System.Text.Encoding.UTF8.GetBytes(s)) h = (h ^ c) * 0x1000193u;
        return h;
    }

    private static string Cfg(string name) => $"0x{Fnv(name):x8}";

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        Blocks.Define("timed", new StatusBlock
        {
            Name = "Timed",
            Seconds = 3,
            OnApply = [new EffectBlock { Damage = Damage.Blunt(7), Flinch = Flinch.None }],
            OnExpire = [new EffectBlock { Damage = Damage.Blunt(9), Flinch = Flinch.None }],
        });
        DefineParts();
        DefineVfxBlocks();
        Context.Tick.Pre += (in TickInfo t) =>
        {
            _clock = t.Clock;
            _longestStep = Math.Max(_longestStep, t.ClockDelta / 60.0);
        };
        Events.On<ImpactNativeEvent>(e =>
        {
            if (e is ImpactDamageEvent h)
            {
                _hits.Add(h);
                _hitAt[h] = GameSeconds;
            }
            if (e is ImpactBuffEvent b) _buffs.Add(b);
            if (e is ImpactSpawnEvent s) _spawnsSeen.Add(s);
        });
        Events.On<ImpactSignalEvent>(e =>
        {
            if (e.Tag == ProbeTag) _probe = e.Value;
            else if ((e.Tag & 0xFFFF0000) == 0xB10D0000) _vfxCues.Add((e.Tag, BitConverter.ToSingle(e.Value, 0), BitConverter.ToSingle(e.Value, 8)));
            else if ((e.Tag & 0xFFFF0000) == 0xB10C0000) _sounds.Add((e.Tag, BitConverter.ToSingle(e.Value, 0), BitConverter.ToSingle(e.Value, 8)));
        });
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(120);
            var dump = Path.Combine(Context.DataDirectory, "programs");
            Info($"{Blocks.DumpPrograms(dump)} generated program(s) written to {dump}");
            Errors();
            Data();
            Twin();
            await Ids();
            await Effects();
            await ItemPath();
            await Weapon();
            await VfxBlocks();
            await Parts();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"blocks-test done: {_pass} PASS, {_fail} FAIL");
    }

    // ---- mistakes ----------------------------------------------------------------------------------------------

    private void Errors()
    {
        var report = Blocks.Report();
        Check(report.TryGetValue("blocks-test", out var mine), "blocks-test's own blocks/broken.json was read");
        void Named(string block, params string[] words)
        {
            var e = mine.Errors.FirstOrDefault(x => x.Contains(block, StringComparison.Ordinal));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
        }
        Named("no-cast", "cast");            // a required field
        Named("typo", "manacosts");          // an unknown field
        Named("no-target", "no target");     // an effect at a place that has none
        Named("bad-ref", "no-such-effect");  // a name that is nothing
        Named("bad-anim", "sideways", "fast-bolt");  // what it could have been
        Named("bad-vfx-field", "burts");     // a VFX's unknown field
        Named("vfx-in-kiln-mod", "KilnClients", "mod");   // a new VFX in a mod that says clients kiln
        Check(!mine.Blocks.Any(x => x is "no-cast" or "typo" or "no-target" or "bad-ref" or "bad-anim" or "bad-vfx-field" or "vfx-in-kiln-mod"), $"none of the broken blocks became anything ({string.Join(", ", mine.Blocks)})");

        var none = report.TryGetValue("blocks-noclients", out var nc) ? nc.Errors.FirstOrDefault() : null;
        Check(none != null && none.Contains("KilnClients", StringComparison.Ordinal), $"a mod with clients none is told what to set: \"{none}\"");

        var thrown = "";
        try
        {
            Blocks.Define("bad", new SpellBlock
            {
                Name = "Bad",
                Cast = new CastBlock { ManaCost = -1 },
                Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 1, Effects = [new EffectBlock { Heal = 1 }] } }] },
            });
        }
        catch (BlockError e)
        {
            thrown = e.Message;
        }
        Check(thrown.Contains("spell bad", StringComparison.Ordinal) && thrown.Contains("cast.manaCost", StringComparison.Ordinal), $"C#: a wrong value throws with the block and the field: \"{thrown}\"");
    }

    // ---- the data ----------------------------------------------------------------------------------------------

    private static double N(GameDataEntry e, string path) => e.Get(path).Number;

    private void Data()
    {
        var spell = (BlockHandle<SpellBlock>)Blocks.Find("frostbolt", Sample)!;
        Check(spell != null, $"frostbolt is defined: item {spell?.GameName}, id 0x{spell?.Id:x8}");
        var d = Content.Items.Get(spell!.GameName)!;
        var dd = Content.ItemData.Get(spell.GameName)!;
        Check(d != null && d.Guid == BlockIds.Derive(Sample, "item", "frostbolt"), $"the item's GUID is the mod id + name rule: {d?.Guid}");
        Check(N(dd, "maxStackSize") == 500, $"charges in a stack {N(dd, "maxStackSize")}");
        Check(N(dd, "itemLevelRange.minLevel") == 1 && N(dd, "itemLevelRange.maxLevel") == 1, "one level, so the tooltip's level is the made item's");
        Check(Math.Abs(N(dd, "equipment.placedEntityLocalSpawnVelocity.z") - 30) < 1e-4, $"Speed 30: the item's placedEntityLocalSpawnVelocity.z {N(dd, "equipment.placedEntityLocalSpawnVelocity.z")}");
        Check(N(dd, "impactValues.ManaCost") == 30, $"ManaCost 30: impactValues.ManaCost {N(dd, "impactValues.ManaCost")}");
        Check(N(dd, "uiValues[0].value") == 30, $"tooltip: Mana Cost {N(dd, "uiValues[0].value")}");
        Check(N(dd, "uiValues[1].value") == 1000, $"tooltip: Charge Time {N(dd, "uiValues[1].value")} ms, FastBolt's");
        Check(N(Content.ItemData.Get("blocks-dataonly.ember")!, "uiValues[1].value") == 1000, "and on a spell whose starter item had 2500 ms");
        Check(N(dd, "uiValues[2].value") == 8000, $"tooltip: Debuff Duration {N(dd, "uiValues[2].value")} ms (the status's 8 s)");
        Check((uint)N(dd, "uiValues[3].locaId") == 0xc9d82dea && N(dd, "uiValues[3].value") == 300, $"tooltip: Radius {N(dd, "uiValues[3].value")} (3 m, Float x100), in place of the starter's Slow");
        var dm = N(dd, "damageSetup.dmgMod");
        var sm = N(dd, "damageSetup.speedMod");
        var power = (int)((float)(int)(10 * 0.15f * 1 + 10) * (float)dm * (float)sm);
        Check(power == 40, $"tooltip damage: int(int(10 * 0.15 * level + 10) * dmgMod {dm:0.####} * speedMod {sm}) = {power}, Damage.Ice(40)");
        Check(Math.Abs(N(dd, "damageSetup.distribution.magicalIceDamage") - 1) < 1e-5 && N(dd, "damageSetup.distribution.magicalFireDamage") == 0, "all of it is ice");

        var cast = dd.Get("sequences[0].sequence").Guid;
        Check(cast == BlockIds.Derive(Sample, "sequence", "frostbolt"), $"the cast is a copy of FastBolt's sequence, for its VFX: {cast}");
        Check((uint)N(dd, "sequences[0].sequenceId") == BlockIds.Fnv(cast), "its sequenceId follows");

        var projectile = Content.Templates.Get($"{Sample}.frostbolt.projectile");
        var impact = Content.Templates.Get($"{Sample}.frostbolt.impact");
        Check(projectile != null && impact != null, "the projectile and the impact are templates of their own");
        Check(dd.Get("equipment.placedEntity").Guid == projectile!.Guid, "the item's placedEntity is the projectile, what an empty SpawnEntityEvent fires");
        Check(projectile.Get("ImpactConfiguration.configValues.SpawnTemplate").Guid == impact!.Guid, "the projectile's SpawnTemplate is the impact");
        Check(Math.Abs(N(projectile, "StaticGravityModifier.modifier") - 0.1) < 1e-5, $"Gravity {N(projectile, "StaticGravityModifier.modifier")}");
        Check(Math.Abs(projectile.Get("LifeTime.lifeTime").Number - 5) < 1e-3, $"Range 150 at Speed 30: lifetime {projectile.Get("LifeTime.lifeTime").Number} s");
        var program = impact.Get("Impact.program").Guid;
        Check((uint)N(impact, "Impact.programId") == BlockIds.Fnv(program), $"the impact runs its generated program {program}, and programId follows ({N(impact, "Impact.programId"):0})");
        Check(Abilities.Programs.Info(BlockIds.Fnv(program)) is not null, "kiln.abilities verified and registered it");

        var status = Content.Buffs.Get($"{Sample}.chilled");
        Check(status != null && status.Guid == BlockIds.Derive(Sample, "buff", "chilled"), $"the status is a buff of its own: 0x{status?.Id:x8}");
        Check(status!.Get("defaultLifeTime").Number == 8, $"Seconds 8: defaultLifeTime {status.Get("defaultLifeTime").Number}");
        Check(Abilities.Programs.Info(BlockIds.Fnv(status.Get("impact").Guid)) is not null, $"it runs its generated program {status.Get("impact").Guid}");

        var recipe = Content.Recipes.Get($"{Sample}.frostbolt.recipe");
        var makes = recipe?.Outputs.FirstOrDefault();
        var needs = recipe?.Inputs.FirstOrDefault();
        Check(recipe != null && makes?.Count == 10 && needs?.Count == 2, $"Makes 10 charges from 2 twigs: {makes}, {needs}");

        var sword = Content.Items.Get($"{Sample}.frost-edge");
        var sd = Content.ItemData.Get($"{Sample}.frost-edge")!;
        Check(sword != null && N(sd, "itemLevelRange.minLevel") == 5 && N(sd, "itemLevelRange.maxLevel") == 5 && sd.Get("generateRarity").Number == 0,
            "the sword has the crafted-item form: one level, no rolled rarity");
        var swordPower = (int)((float)(int)(10 * 0.15f * 5 + 10) * (float)N(sd, "damageSetup.dmgMod") * (float)N(sd, "damageSetup.speedMod"));
        Check(swordPower == 30, $"its tooltip damage at level 5 is Power 30: {swordPower}");
        var clients = File.ReadAllText(Path.Combine(Mods.Get(Sample)!.Directory, "kiln.mod.json"));
        Check(clients.Contains("\"both\"", StringComparison.Ordinal) && clients.Contains("\"clients\": \"mod\"", StringComparison.Ordinal), "the SDK made the manifest side both from KilnServices, and clients mod from its new VFX");
        var only = Blocks.Report().TryGetValue("blocks-dataonly", out var dataOnly);
        Check(only && dataOnly.Blocks.Contains("ember") && Content.Items.Get("blocks-dataonly.ember") != null && Content.Recipes.Get("blocks-dataonly.ember.recipe") != null, "a mod with no code: blocks/ember.json made its item and recipe");
    }

    // JSON and C# are the same objects: what they lower to differs only in names and the ids that follow from names.
    private void Twin()
    {
        string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "[Ff]rostlance", m => m.Value[0] == 'F' ? "Frostbolt" : "frostbolt").Replace("-json", "");
        var cs = Normal(Blocks.Lowered("frostbolt", Sample));
        var json = Normal(Blocks.Lowered("frostlance", Sample));
        Check(cs.Length > 500 && cs == json, $"blocks/frostlance.json lowers to what the C# frostbolt does ({cs.Length} characters of definitions)");
        if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        Info($"frostbolt lowers to: {Regex.Replace(Blocks.Lowered("frostbolt", Sample) ?? "", @"\s+", " ")}");
    }

    // ---- ids across a restart ----------------------------------------------------------------------------------

    private async Task Ids()
    {
        var names = new[] { "frostbolt", "frost-edge", "chilled", "frostlance", "frost_burst", "frost_trail", "frost_burst-json" };
        var now = new JsonObject();
        foreach (var n in names) now[n] = Blocks.Find(n, Sample)!.Guid.ToString();
        now["program"] = Content.Templates.Get($"{Sample}.frostbolt.impact")!.Get("Impact.program").Guid.ToString();
        var file = Path.Combine(Context.DataDirectory, "ids.json");
        var spell = new ItemId(Blocks.Find("frostbolt", Sample)!.Id);
        if (File.Exists(file))
        {
            var was = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var (k, v) in now) Check(was[k]?.ToString() == v?.ToString(), $"after the restart {k} is still {v}");
            // entity ids change between sessions: look for it
            var holder = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty).FirstOrDefault(e => Items.Read(e)?.Count(spell) > 0);
            var c = holder.IsNone ? null : Items.Read(holder);
            Check(c?.Count(spell) == 7, $"the save kept the charges put in a chest: {c?.Count(spell)} of 7, in {holder}");
            if (!holder.IsNone) await Items.Take(holder, spell, 7);
            File.Delete(file);
            return;
        }
        // what a run that did not get to its restart left in this world's chests
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            if (Items.Read(e)?.Count(spell) is > 0 and var left) await Items.Take(e, spell, left);
        EntityId box = EntityId.None;
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 2 && (!EntityData.Available || EntityData.IsPersistent(e)))
            {
                box = e;
                break;
            }
        if (Check(!box.IsNone, $"a saved container for the restart: {box}"))
        {
            var g = await Items.Give(box, spell, 7);
            Check(g.Status == ItemsStatus.Done, $"give 7 charges: {g}");
        }
        File.WriteAllText(file, now.ToJsonString());
        Info("ids written: restart the lab for the second phase");
    }

    // ---- effects -----------------------------------------------------------------------------------------------

    private async Task<WorldPosition> Anchor()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(inf.Position.X + 40, inf.Position.Y + 0.5, inf.Position.Z + 40);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }

    private async Task<EntityId> Make(string template, WorldPosition at)
    {
        var e = await Spawn.Entity(template, at, 0, new SpawnOptions { Tag = "blocks-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(10);
        return e;
    }

    private static List<(uint Id, double Seconds)> BuffsOf(uint type)
    {
        var r = new List<(uint, double)>();
        foreach (var x in World.Find("BuffNetworkData"))
            if (World.ReadBytes(x, "BuffNetworkData") is { Length: >= 24 } b && BitConverter.ToUInt32(b, 0x10) == type)
                r.Add((x.Value, BitConverter.ToInt64(b, 0) / 1e9));
        return r;
    }

    private async Task Effects()
    {
        _at = await Anchor();
        var caster = await Make("1_Player", _at);
        var enemy = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 2, _at.Y, _at.Z));
        var far = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 30, _at.Y, _at.Z));
        var friend = await Make("1_Player", new WorldPosition(_at.X, _at.Y, _at.Z + 2));
        Combat.SetInvulnerable(caster, true);
        Combat.SetInvulnerable(friend, true);
        var impact = Content.Templates.Get($"{Sample}.frostbolt.impact")!;
        var program = BlockIds.Fnv(impact.Get("Impact.program").Guid);
        var chilled = Blocks.Find("chilled", Sample)!;
        var h0 = Combat.GetHealth(enemy)?.Current;
        var far0 = Combat.GetHealth(far)?.Current;
        _hits.Clear();
        _buffs.Clear();
        var before = BuffsOf(chilled.Id).Count;
        var boom = await Abilities.RunImpact(impact.Guid, _at, new ImpactOptions { Root = caster, Tag = "blocks-test" });
        await Steps(12);
        var h1 = Combat.GetHealth(enemy)?.Current;
        var hit = _hits.FirstOrDefault(h => h.Impact.ProgramId == program && h.Target == enemy);
        Info($"RunImpact {impact.Name}: entity {boom}; hits [{string.Join("; ", _hits.Select(h => $"{h.Attacker}->{h.Target} ice {h.Damage.Ice} program 0x{h.Impact.ProgramId:x8}"))}]; enemy health {h0} -> {h1}");
        Check(hit != null && hit.Attacker == caster && Math.Abs(hit.Damage.Ice - 40) < 1e-3, $"the area hit the enemy within 3 m through applyDamage, from the caster, ice {hit?.Damage.Ice}");
        Check(h1 < h0, $"it deals damage: enemy health {h0} -> {h1}");
        Check(!_hits.Any(h => h.Target == far || h.Target == caster || h.Target == friend), "nothing else was hit: not the far enemy, the caster, or a friend inside the radius");
        Check(_hits.Count(h => h.Impact.ProgramId == program) == 1, $"one hit per creature: its weak spot and VFX link are not hit too ({_hits.Count} in all)");
        Check(Combat.GetHealth(far)?.Current == far0, "the enemy 30 m away is untouched");
        var buffs = BuffsOf(chilled.Id);
        Check(buffs.Count > before && _buffs.Any(b => b.Buff == chilled.Guid && b.Target == enemy), $"it applied Chilled ({chilled.Id:x8}) on the enemy: {buffs.Count} buff entit(ies), {buffs.FirstOrDefault().Seconds:0.##} s");
        Check(buffs.Any(b => Math.Abs(b.Seconds - 8) < 0.01), "for the status's 8 s");

        // the status's own effects: 3 ice every 2 s, for 8 s. Timed on the game's clock, from the hit that applied it: a busy machine makes
        // steps slower, and the status can run out inside the window.
        var applied = hit != null && _hitAt.TryGetValue(hit, out var at) ? at : GameSeconds;
        _hits.Clear();
        _longestStep = 0;
        var from = GameSeconds;
        await Steps(25 * 5);
        var to = GameSeconds;
        var ticks = _hits.Where(h => h.Target == enemy && Math.Abs(h.Damage.Ice - 3) < 1e-3).ToList();
        var h2 = Combat.GetHealth(enemy)?.Current;
        var tickTimes = ticks.Select(t => _hitAt.TryGetValue(t, out var s) ? s - applied : double.NaN).ToList();
        // a tick is due every 2 s while the status lasts; one at the window's edges or at its last moment may fall either side, and a hit is
        // seen up to a step late
        var due = Enumerable.Range(1, 4).Count(k => applied + 2 * k > from && applied + 2 * k <= Math.Min(to, applied + 8));
        var steady = tickTimes.Zip(tickTimes.Skip(1), (a, b) => b - a).All(gap => Math.Abs(gap - 2) <= 0.2 + _longestStep);
        Info($"after {to - from:0.0} s of game time: {ticks.Count} tick(s) at [{string.Join(", ", tickTimes.Select(t => $"{t:0.00}"))}] s after it was applied, health {h1} -> {h2}, attacker {ticks.FirstOrDefault()?.Attacker}");
        Check(ticks.Count > 0 && Math.Abs(ticks.Count - due) <= 1 && steady,
            $"OnTick ran on the target every 2 s: {ticks.Count} ticks in {to - from:0.0} s of game time ({due} due), at [{string.Join(", ", tickTimes.Select(t => $"{t:0.0}"))}] s");
        Check(h2 < h1, $"and it hurts: {h1} -> {h2}");
        Check(ticks.All(t => t.Attacker == caster), "credited to the caster");
        await Steps(25 * 5);
        Check(BuffsOf(chilled.Id).Count == before, "the status is gone after its 8 s");
        Spawn.Destroy(enemy);
        Spawn.Destroy(far);

        // the other two trigger points of a status
        var victim = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 2, _at.Y, _at.Z - 2));
        var timed = Blocks.Find("timed")!;
        _hits.Clear();
        await Abilities.ApplyBuff(victim, timed.Guid, source: caster);
        await Steps(20);
        Check(_hits.Any(h => h.Target == victim && Math.Abs(h.Damage.Blunt - 7) < 1e-3), $"OnApply ran when the status started: [{string.Join("; ", _hits.Select(h => h.Damage.Blunt))}]");
        Check(!_hits.Any(h => h.Target == victim && Math.Abs(h.Damage.Blunt - 9) < 1e-3), "OnExpire has not run yet");
        await Steps(25 * 3);
        Check(_hits.Any(h => h.Target == victim && Math.Abs(h.Damage.Blunt - 9) < 1e-3), $"OnExpire ran when it ended: [{string.Join("; ", _hits.Select(h => h.Damage.Blunt))}]");
        Spawn.Destroy(victim);
    }

    // A real cast: the game copies the used items into the projectile's ImpactItemConfig, a spawn copies it to the impact, and the item's
    // damageSetup then decides the Damage config. The lab cannot cast, but the component can be given to the impact as the game gives it.
    private async Task ItemPath()
    {
        var enemy = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 2, _at.Y, _at.Z - 2));
        var caster = await Make("1_Player", _at);
        Combat.SetInvulnerable(caster, true);
        var impact = Content.Templates.Get($"{Sample}.frostbolt.impact")!;
        var program = BlockIds.Fnv(impact.Get("Impact.program").Guid);
        var spell = Blocks.Find("frostbolt", Sample)!.Id;
        var staff = Content.Items.Get("Weapon_T1_2H_Staff_Craft")!.Id;
        async Task<float?> Cast(uint used, uint ammo)
        {
            var c = new byte[0x2c];
            BitConverter.GetBytes(used).CopyTo(c, 8);
            BitConverter.GetBytes(ammo).CopyTo(c, 0x14 + 8);
            _hits.Clear();
            await Abilities.RunImpact(impact.Guid, _at, new ImpactOptions { Root = caster, Tag = "blocks-test", Components = [new SpawnComponent(0x4006f1f8, c)] });
            await Steps(12);
            return _hits.FirstOrDefault(h => h.Impact.ProgramId == program && h.Target == enemy)?.Damage.Ice;
        }
        var alone = await Cast(0, spell);
        Check(alone is { } a && Math.Abs(a - 40) < 1e-3, $"with the spell as the used ammunition, the item's damage reaches the program: ice {alone} (the tooltip's 40)");
        var both = await Cast(staff, spell);
        Check(both is { } b && Math.Abs(b - 48) < 1e-3, $"with a level 5 staff too, its power adds, as in a real cast: ice {both} (40 + int(17 * dmgMod 0.5) = 48)");
        Spawn.Destroy(enemy);
    }

    // ---- the same effect on a weapon -----------------------------------------------------------------------------

    private async Task<(int Burst, int Applied, int Total, int? Before, int? After)> SwingAt(EntityId wearer, uint onHit, Guid status)
    {
        var target = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 3, _at.Y, _at.Z + 3));
        var at = World.Position(target)!.Value;
        var h0 = Combat.GetHealth(target)?.Current;
        _hits.Clear();
        _buffs.Clear();
        var he = await Abilities.RunProgram(Swing, at, new ImpactOptions
        {
            Root = wearer,
            Tag = "blocks-test",
            Configs = new ImpactConfigs
            {
                [Cfg("blockstest.Target")] = target,
                [Cfg("blockstest.At")] = new[] { (float)at.X, (float)at.Y, (float)at.Z }.SelectMany(BitConverter.GetBytes).ToArray(),
            },
        });
        await Steps(16);
        Spawn.Destroy(he);
        var bursts = _hits.Count(h => h.Impact.ProgramId == onHit && h.Target == target && Math.Abs(h.Damage.Ice - 40) < 1e-3);
        var applied = _buffs.Count(b => b.Buff == status && b.Target == target);
        var total = _hits.Count(h => h.Impact.ProgramId == onHit);
        var h1 = Combat.GetHealth(target)?.Current;
        Spawn.Destroy(target);
        await Steps(4);
        return (bursts, applied, total, h0, h1);
    }

    private async Task Weapon()
    {
        var wearer = await Make("1_Player", new WorldPosition(_at.X - 2, _at.Y, _at.Z - 2));
        Combat.SetInvulnerable(wearer, true);
        var sword = Blocks.Find("frost-edge", Sample)!;
        var low = JsonNode.Parse(Blocks.Lowered("frost-edge", Sample)!)!;
        var program = BlockIds.Fnv(Guid.Parse((string)low["onHitProgram"]!));
        var chilled = Blocks.Find("chilled", Sample)!.Guid;

        // without it in hand: the swing is a plain hit
        var off = await SwingAt(wearer, program, chilled);
        Check(off.Burst == 0 && off.Applied == 0, $"not in hand: a hit does nothing more ({off.Burst} bursts, {off.Applied} statuses)");

        var give = await Items.Give(wearer, sword.GameName, 1);
        Info($"give the sword: {give}");
        await Steps(20);
        var slot = Items.Read(wearer)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == sword.Id);
        if (!Check(slot is not null && !slot.IsEmpty, $"the sword is in the bag: slot {slot?.Slot}")) return;
        var index = (uint)slot!.Slot.Index;
        GameTree.GameRequest(wearer, 5, index);
        await Steps(24);
        var eq = World.ReadBytes(wearer, "Equipment");
        var held = eq is null ? 0 : BitConverter.ToUInt32(eq, 8 + 8);
        Check(held == sword.Id, $"it is in hand: Equipment slot 0 holds 0x{held:x8}");
        var on = await SwingAt(wearer, program, chilled);
        Info($"in hand: {on}");
        Check(on.Burst == 1, $"a hit runs the effect the spell uses, once: {on.Burst} burst of ice 40");
        Check(on.Applied == 1, $"and it applies the same status: {on.Applied}");
        Check(on.Total == 1, $"a hit dealt by the effect does not trigger the effect again: {on.Total} hit(s) from the program");
        Check(on.After < on.Before, $"it hurts: {on.Before} -> {on.After}");

        GameTree.GameRequest(wearer, 5, index == 0 ? 1u : 0u);
        await Steps(24);
        var away = await SwingAt(wearer, program, chilled);
        Check(away.Burst == 0 && away.Applied == 0, $"another hotbar slot: the effect stops ({away.Burst}, {away.Applied})");
        GameTree.GameRequest(wearer, 5, index);
        await Steps(24);
        var back = await SwingAt(wearer, program, chilled);
        Check(back.Burst == 1 && back.Applied == 1, $"back in hand it runs again ({back.Burst}, {back.Applied})");
    }
}
