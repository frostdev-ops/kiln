// SPDX-License-Identifier: MIT
// The parts of an effect and a status: Removes, Restores, Spawns, Sound, On, Modifies, game statuses and templates by name,
// the triggers that follow a wielder and what a spell does when cast. Player-free, on a dedicated server.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksTest;

public sealed partial class BlocksTestMod
{
    private const uint ProbeTag = 0xB10E0001;
    private const int Recharge = 12;    // the most a character's pools refill by themselves between two reads here
    private const string GameSlow = "0x5f5d2f5c";       // the game's frost slow, by the id in GameBuffs
    private const string GamePoison = "0x5f425faf";
    private const string GameZone = "Zone_Player_Spell_IceBolt_1";
    private static readonly Guid ProbeInt = Guid.Parse("5a0f6c91-2c1e-4f77-8a02-7b1d3e9c1001"), ProbeFloat = Guid.Parse("5a0f6c91-2c1e-4f77-8a02-7b1d3e9c1002");
    private static readonly Guid CueGuid = Guid.Parse("2f3d7c11-90ab-4cde-8f01-23456789abcd");
    private byte[]? _probe;
    private readonly List<(uint Tag, float X, float Z)> _sounds = new();

    private bool _renamedRun;

    private void DefineParts()
    {
        Renamed();
        Blocks.Define("hold", new StatusBlock { Name = "Hold", Seconds = 30, Kind = StatusKind.Buff });
        Blocks.Define("cleanse", new StatusBlock { Name = "Cleanse", Seconds = 1, OnApply = [new EffectBlock { Removes = ["hold", GameSlow] }] });
        Blocks.Define("poisoner", new StatusBlock { Name = "Poisoner", Seconds = 1, OnApply = [new EffectBlock { Applies = new StatusApply { Status = GameSlow, Seconds = 20 } }] });
        Blocks.Define("drain", new StatusBlock { Name = "Drain", Seconds = 1, OnApply = [new EffectBlock { Restores = Restore.Mana(-30) + Restore.Stamina(-20) + Restore.Health(-6) }] });
        Blocks.Define("refill", new StatusBlock { Name = "Refill", Seconds = 1, OnApply = [new EffectBlock { Restores = Restore.Mana(20) + Restore.Stamina(15) + Restore.Health(4) }] });
        Blocks.Define("weakened", new StatusBlock
        {
            Name = "Weakened",
            Seconds = 3,
            Modifies = { [Stat.MoveSpeed] = -0.5, [Stat.Strength] = 5, [Stat.ResistIce] = 0.25, [Stat.MaxHealth] = 40 },
        });
        Blocks.Define("spawner", new StatusBlock { Name = "Spawner", Seconds = 1, OnApply = [new EffectBlock { Spawns = GameZone, Sound = CueGuid }] });
        Blocks.Define("for-the-caster", new EffectBlock { On = EffectOn.Caster, Restores = Restore.Mana(10), Applies = "hold" });
        Blocks.Define("bolt-parts", new SpellBlock
        {
            Name = "Bolt Parts",
            Cast = new CastBlock { ManaCost = 5 },
            OnCast = ["for-the-caster"],
            Delivery = new ProjectileBlock
            {
                OnHit = [new EffectBlock { Damage = Damage.Ice(12), Applies = "hold" }],
                OnImpact = [new EffectBlock { Spawns = GameZone, Sound = CueGuid, Then = [new EffectBlock { Area = new AreaBlock { Radius = 4, Effects = [new EffectBlock { Damage = Damage.Fire(5) }] } }] }, "for-the-caster"],
            },
        });
        Blocks.Define("hit-only", new SpellBlock
        {
            Name = "Hit Only",
            Cast = new CastBlock { ManaCost = 5 },
            Delivery = new ProjectileBlock { OnHit = [new EffectBlock { Damage = Damage.Ice(33), Applies = new StatusApply { Status = "hold", Seconds = 2 } }] },
        });
        Blocks.Define("flat-bolt", new SpellBlock
        {
            Name = "Flat Bolt",
            Cast = new CastBlock { ManaCost = 5 },
            Delivery = new ProjectileBlock
            {
                OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = new DamageBlock { Ice = 40, Flat = true } }, new EffectBlock { Damage = Damage.Fire(9) }] } }],
            },
        });
        Blocks.Define("two-hits", new SpellBlock
        {
            Name = "Two Hits",
            Cast = new CastBlock { ManaCost = 5 },
            Delivery = new ProjectileBlock
            {
                OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = Damage.Ice(40) }, new EffectBlock { Damage = Damage.Fire(9) }] } }],
            },
        });
        Blocks.Define("borrowed-icon", new StatusBlock { Name = "Borrowed", Seconds = 1, Icon = GamePoison });
        Blocks.Define("no-such-game-status", new StatusBlock { Name = "Nothing", Seconds = 1, OnApply = [new EffectBlock { Applies = "0xdeadbeef" }] });
        Blocks.Define("no-such-template", new StatusBlock { Name = "Nothing", Seconds = 1, OnApply = [new EffectBlock { Spawns = "No_Such_Template_At_All" }] });
    }

    private static string Refused(Action define)
    {
        try { define(); return ""; }
        catch (BlockError e) { return e.Message; }
    }

    // A program that would kill the process: kiln.abilities' verifier refuses it, and running it says so.
    private async Task UncheckedArgument()
    {
        var mod = Blocks.ModFor("blocks-test", () => 0);
        var b = new ProgramBuilder("bad.unconnected");
        b.BeginCode();
        var a = b.VByte("a", ImpactType.AttackMask, 1);
        b.Call("hasAttackCategory", null, a, null, a);
        b.Pop();
        b.Halt();
        b.BeginShutdown();
        b.Halt();
        var (guid, blob) = new BlockContext(mod, "unconnected test").Build(b);
        var low = new Lowered(mod);
        low.Program(guid, "bad.unconnected", blob, "");
        low.Commit();
        await Steps(4);
        var thrown = "";
        try { await Abilities.RunProgram(guid, _at, new ImpactOptions { Tag = "blocks-test" }); }
        catch (Exception e) { thrown = e.Message; }
        Check(thrown.Length > 0, $"a program that leaves an unchecked argument unconnected is refused, not run: \"{thrown}\"");
    }

    private async Task<double?> Attr(string which, EntityId on, bool whole)
    {
        var attr = which switch
        {
            "mana" => new Guid("9f15218b-a7d4-4589-8de1-d5ab745bf51a"),
            "stamina" => new Guid("0a0eabad-6094-4ba2-8dc5-614d500d0a34"),
            _ => Stat.All.First(x => x.Key == which).Attribute,
        };
        _probe = null;
        var e = await Abilities.RunProgram(whole ? ProbeInt : ProbeFloat, World.Position(on)!.Value, new ImpactOptions
        {
            Tag = "blocks-test",
            Configs = new ImpactConfigs { [Cfg("blockstest.Target")] = on, [Cfg("blockstest.Attr")] = attr.ToByteArray() },
        });
        await Steps(4);
        if (World.Exists(e)) Spawn.Destroy(e);
        if (_probe is not { Length: >= 4 } b) return null;
        return whole ? BitConverter.ToInt32(b) : BitConverter.ToSingle(b);
    }

    private async Task Parts()
    {
        var caster = await Make("1_Player", new WorldPosition(_at.X + 6, _at.Y, _at.Z + 6));
        var player = await Make("1_Player", new WorldPosition(_at.X + 8, _at.Y, _at.Z + 6));
        Combat.SetInvulnerable(caster, true);
        Task Give(string name, EntityId on) => Abilities.ApplyBuff(on, Blocks.Find(name)!.Guid, source: caster);

        // ---- Modifies: on while it runs, off when it ends
        var speed0 = await Attr("move-speed", player, false);
        var strength0 = await Attr("strength", player, true);
        var ice0 = await Attr("resist-ice", player, false);
        var maxHealth0 = await Attr("max-health", player, true);
        await Give("weakened", player);
        await Steps(10);
        var speed1 = await Attr("move-speed", player, false);
        var strength1 = await Attr("strength", player, true);
        var ice1 = await Attr("resist-ice", player, false);
        var maxHealth1 = await Attr("max-health", player, true);
        Info($"modifies: speed {speed0} -> {speed1}, strength {strength0} -> {strength1}, ice susceptibility {ice0} -> {ice1}, max health adder {maxHealth0} -> {maxHealth1}");
        Check(speed1 is { } s1 && speed0 is { } s0 && Math.Abs(s1 - s0 + 0.5) < 1e-3, $"Modifies: move speed {speed0} -> {speed1} while it runs");
        Check(strength1 == strength0 + 5, $"Modifies: strength {strength0} -> {strength1}");
        Check(ice1 is { } i1 && ice0 is { } i0 && Math.Abs(i0 - i1 - 0.25) < 1e-3, $"Modifies: a resistance is taken off the susceptibility, as the game's armour does: {ice0} -> {ice1}");
        Check(maxHealth1 == maxHealth0 + 40, $"Modifies: maximum health adder {maxHealth0} -> {maxHealth1}");
        await Steps(25 * 4);
        var speed2 = await Attr("move-speed", player, false);
        var strength2 = await Attr("strength", player, true);
        var ice2 = await Attr("resist-ice", player, false);
        Check(speed2 is { } s2 && speed0 is { } s00 && Math.Abs(s2 - s00) < 1e-3, $"Modifies: move speed is back after it ends: {speed2}");
        Check(strength2 == strength0, $"Modifies: strength is back: {strength2}");
        Check(ice2 is { } i2 && ice0 is { } i00 && Math.Abs(i2 - i00) < 1e-3, $"Modifies: the resistance is back: {ice2}");

        // ---- Restores: more and less, of each
        var mana0 = await Attr("mana", player, true);
        var stamina0 = await Attr("stamina", player, true);
        var health0 = Combat.GetHealth(player)?.Current;
        await Give("drain", player);
        await Steps(10);
        var mana1 = await Attr("mana", player, true);
        var stamina1 = await Attr("stamina", player, true);
        var health1 = Combat.GetHealth(player)?.Current;
        // within what the game's own recharge gives in these steps
        Check(mana1 <= mana0 - 30 + Recharge && mana1 >= mana0 - 30, $"Restores: mana {mana0} -> {mana1}");
        Check(stamina1 <= stamina0 - 20 + Recharge && stamina1 >= stamina0 - 20, $"Restores: stamina {stamina0} -> {stamina1}");
        Check(health1 <= health0 - 6 + Recharge && health1 >= health0 - 6, $"Restores: health {health0} -> {health1}");
        await Give("refill", player);
        await Steps(10);
        var mana2 = await Attr("mana", player, true);
        var stamina2 = await Attr("stamina", player, true);
        var health2 = Combat.GetHealth(player)?.Current;
        Check(mana2 >= mana1 + 20, $"Restores: back up, mana {mana1} -> {mana2}");
        Check(stamina2 >= stamina1 + 15, $"Restores: back up, stamina {stamina1} -> {stamina2}");
        Check(health2 >= health1 + 4, $"Restores: back up, health {health1} -> {health2}");

        // ---- game statuses by id: applied and removed; removes a block's too
        var hold = Blocks.Find("hold")!;
        var slow = Convert.ToUInt32(GameSlow, 16);
        var victim = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 12, _at.Y, _at.Z + 8));
        var holds0 = BuffsOf(hold.Id).Count;
        var slows0 = BuffsOf(slow).Count;
        _buffs.Clear();
        await Give("hold", victim);
        await Give("poisoner", victim);
        await Steps(10);
        var slowed = BuffsOf(slow);
        Info($"game status {GameSlow}: GUID {Content.Buffs.Get(GameSlow)?.Guid}, program events [{string.Join("; ", _buffs.Select(b => $"{b.Buff} on {b.Target}"))}], entities {slowed.Count}; poisoner {Blocks.Find("poisoner")!.Guid}");
        Check(BuffsOf(hold.Id).Count == holds0 + 1, "a status is on the target");
        Check(slowed.Count == slows0 + 1 && slowed.Any(b => Math.Abs(b.Seconds - 20) < 0.5), $"a game status by its GameBuffs id is applied, for the time given: {slowed.Count}, {string.Join(",", slowed.Select(b => b.Seconds))} s");
        await Give("cleanse", victim);
        await Steps(10);
        Check(BuffsOf(hold.Id).Count == holds0, "Removes takes a block's status off");
        Check(BuffsOf(slow).Count == slows0, "Removes takes a game status off, by its id");

        // ---- Spawns and Sound: on a target, by the game's template name
        var zones0 = Spawn.FindByName(GameZone, max: 50).Count;
        _sounds.Clear();
        await Give("spawner", victim);
        await Steps(12);
        var zones1 = Spawn.FindByName(GameZone, max: 50).Count;
        var there = World.Position(victim)!.Value;
        Check(zones1 == zones0 + 1, $"Spawns: a game template by its name, once: {zones0} -> {zones1}");
        Check(_sounds.Count == 1 && Math.Abs(_sounds[0].X - there.X) < 1 && Math.Abs(_sounds[0].Z - there.Z) < 1, $"Sound: the program told Kiln once, where the target is: {string.Join("; ", _sounds)}, target {there}");

        // ---- at a place: what a place can do, and what it does for the caster
        var impact = Content.Templates.Get("blocks-test.bolt-parts.impact")!;
        var near = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 22, _at.Y, _at.Z + 20));
        var place = new WorldPosition(_at.X + 20, _at.Y, _at.Z + 20);
        var casterMana0 = await Attr("mana", caster, true);
        await Give("drain", caster);
        await Steps(8);
        var casterMana1 = await Attr("mana", caster, true);
        var casterHolds0 = BuffsOf(hold.Id).Count;
        zones0 = Spawn.FindByName(GameZone, max: 50).Count;
        _sounds.Clear();
        _hits.Clear();
        await Abilities.RunImpact(impact.Guid, place, new ImpactOptions { Root = caster, Tag = "blocks-test" });
        await Steps(12);
        var casterMana2 = await Attr("mana", caster, true);
        Check(Spawn.FindByName(GameZone, max: 50).Count == zones0 + 1, "at a place: Spawns");
        Check(_sounds.Count == 1 && Math.Abs(_sounds[0].X - place.X) < 1 && Math.Abs(_sounds[0].Z - place.Z) < 1, $"at a place: Sound, where the place is: {string.Join("; ", _sounds)}");
        Check(_hits.Any(h => h.Target == near && Math.Abs(h.Damage.Fire - 5) < 1e-3), $"at a place: Then runs at the place too, its area hit: [{string.Join("; ", _hits.Select(h => $"{h.Target} fire {h.Damage.Fire}"))}]");
        Check(casterMana2 >= casterMana1 + 10 && casterMana2 <= casterMana1 + 10 + Recharge, $"On = Caster at a place: the caster's mana {casterMana1} -> {casterMana2}");
        Check(BuffsOf(hold.Id).Count == casterHolds0 + 1, "On = Caster at a place: and the status");

        // ---- OnCast: runs in the flight's program, on the caster, once
        var flight = Content.Templates.Get("blocks-test.bolt-parts.projectile")!;
        var flightProgram = flight.Get("Impact.program").Guid;
        Check(flightProgram != Guid.Empty && flightProgram != Content.Templates.Get("Projectile_Player_Staff_IceBolt_1")!.Get("Impact.program").Guid, $"a projectile with effects of its own runs a program of ours: {flightProgram}");
        Check(flight.Get("ImpactConfiguration.configValues.SpawnTemplate").Guid == impact.Guid, "and it still spawns the impact");
        var plain = Content.Templates.Get($"{Sample}.frostbolt.projectile")!;
        Check(plain.Get("Impact.program").Guid == Content.Templates.Get("Projectile_Player_Staff_IceBolt_1")!.Get("Impact.program").Guid, "one without keeps the game's program");
        await Give("drain", caster);
        await Steps(8);
        var castMana0 = await Attr("mana", caster, true);
        var castHolds0 = BuffsOf(hold.Id).Count;
        var running = await Abilities.RunProgram(flightProgram, World.Position(caster)!.Value, new ImpactOptions { Root = caster, Tag = "blocks-test" });
        await Steps(25);
        var castMana1 = await Attr("mana", caster, true);
        Check(castMana1 >= castMana0 + 10 && castMana1 <= castMana0 + 10 + Recharge, $"OnCast: on the caster: mana {castMana0} -> {castMana1} (and what the game's recharge gave)");
        Check(BuffsOf(hold.Id).Count <= castHolds0 + 1 && _buffs.Count(b => b.Buff == hold.Guid && b.Target == caster) >= 1, "OnCast: its status is on the caster, once");
        Check(World.Exists(running), "the flight's program keeps waiting for a collision");
        Spawn.Destroy(running);
        var hitOnly = Content.ItemData.Get("blocks-test.hit-only")!;
        var power = (int)((float)(int)(10 * 0.15f * 1 + 10) * (float)N(hitOnly, "damageSetup.dmgMod") * (float)N(hitOnly, "damageSetup.speedMod"));
        Check(power == 33, $"a spell whose main hit is OnHit: its power is that hit: {power}");
        Check(N(hitOnly, "uiValues[2].value") == 2000, $"and the tooltip has its status's time: {N(hitOnly, "uiValues[2].value")} ms");

        // ---- a game reference that is nothing: named when content is ready
        var report = Blocks.Report()["blocks-test"].Errors;
        Check(report.Any(e => e.Contains("no-such-game-status", StringComparison.Ordinal) && e.Contains("0xdeadbeef", StringComparison.Ordinal)), $"a game status that does not exist is named: \"{report.FirstOrDefault(e => e.Contains("0xdeadbeef", StringComparison.Ordinal))}\"");
        Check(report.Any(e => e.Contains("no-such-template", StringComparison.Ordinal) && e.Contains("No_Such_Template_At_All", StringComparison.Ordinal)), $"so is a template: \"{report.FirstOrDefault(e => e.Contains("No_Such_Template_At_All", StringComparison.Ordinal))}\"");

        // ---- what cannot work is refused with what to do
        var own = Refused(() => Blocks.Define("own-sound", new EffectBlock { Sound = "chime" }));
        Check(own.Contains("KilnClients", StringComparison.Ordinal) && own.Contains("mod", StringComparison.Ordinal) && own.Contains("own-sound", StringComparison.Ordinal), $"a sound of the mod's own needs clients mod: \"{own}\"");
        var whole = Refused(() => Blocks.Define("half-strength", new StatusBlock { Name = "x", Seconds = 1, Modifies = { [Stat.Strength] = 0.5 } }));
        Check(whole.Contains("modifies[strength]", StringComparison.Ordinal), $"a stat of whole numbers says so: \"{whole}\"");
        var vfx = Refused(() => Blocks.Define("with-vfx", new EffectBlock { Vfx = VfxRef.Game("Explosion_Player_Spell_Fireball_1") }));
        Check(vfx.Contains("vfx", StringComparison.Ordinal), $"a VFX is refused for now, by name: \"{vfx}\"");
        var idle = Refused(() => Blocks.Define("nothing-bolt", new SpellBlock { Name = "x", Cast = new CastBlock { ManaCost = 1 }, Delivery = new ProjectileBlock() }));
        Check(idle.Contains("onImpact", StringComparison.Ordinal), $"a projectile that does nothing: \"{idle}\"");

        DataParts();
        var iconOf = (string? name) => Content.Buffs.Get(name!)?.GetJson("icon")?["value"]?.ToJsonString();
        Check(iconOf(GamePoison) != null && iconOf("blocks-test.borrowed-icon") == iconOf(GamePoison) && iconOf(GamePoison) != iconOf("blocks-test.hold"), $"Icon: a status takes a game status's icon: {iconOf("blocks-test.borrowed-icon")}");
        var bad = Refused(() => Blocks.Define("png-icon", new StatusBlock { Name = "x", Seconds = 1, Icon = "icons/x.png" }));
        Check(bad.Contains("icon", StringComparison.Ordinal), $"a PNG icon is refused for now, by name: \"{bad}\"");
        await UncheckedArgument();
        await ConsumingSequence();
        CheckRenamed();
        await ItemDamage(caster);
        await Wielder(caster);
        await Rows();
    }

    // ---- the same parts from a data file -------------------------------------------------------------------------------

    private void DataParts()
    {
        var (blocks, errors) = Blocks.Report()["blocks-dataonly"];
        Check(errors.Count == 0, $"blocks/parts.json has no errors: {string.Join("; ", errors)}");
        Check(blocks.Contains("slowed") && blocks.Contains("wash") && blocks.Contains("shard"), $"its blocks are there: {string.Join(", ", blocks)}");
        var shard = Blocks.Lowered("shard", "blocks-dataonly") ?? "";
        Check(shard.Contains("shard.projectile", StringComparison.Ordinal), "a spell with onCast and onHit runs a program of its own");
        var flight = Content.Templates.Get("blocks-dataonly.shard.projectile")!;
        Check(flight.Get("Impact.program").Guid != Content.Templates.Get("Projectile_Player_Staff_IceBolt_1")!.Get("Impact.program").Guid, "and that program waited for the game's status and template, then took its place");
        var item = Content.ItemData.Get("blocks-dataonly.shard")!;
        var power = (int)((float)(int)(10 * 0.15f * 1 + 10) * (float)N(item, "damageSetup.dmgMod") * (float)N(item, "damageSetup.speedMod"));
        Check(power == 9, $"its main hit is what its onHit says: {power}");

        var report = Path.Combine(Path.GetDirectoryName(Context.DataDirectory)!, "blocks", "blocks-dataonly.json");
        var doc = File.Exists(report) ? JsonNode.Parse(File.ReadAllText(report)) : null;
        Check(doc?["blocks"]?["shard"] != null && doc?["programs"] is JsonObject { Count: > 0 }, $"kiln blocks reads it from {report}");
    }

    // ---- a block renamed without Was --------------------------------------------------------------------------------

    private void Renamed()
    {
        var marker = Path.Combine(Context.DataDirectory, "rename-seen.txt");
        var second = File.Exists(marker);
        // the first run makes "before-rename", the second run has the same block under another name and does not say Was
        Blocks.Define(second ? "after-rename" : "before-rename", new SpellBlock
        {
            Name = "Renamed",
            Cast = new CastBlock { ManaCost = 1 },
            Delivery = new ProjectileBlock { OnHit = [new EffectBlock { Heal = 1 }] },
        });
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(marker, "seen");
        _renamedRun = second;
    }

    private void CheckRenamed()
    {
        if (!_renamedRun)
        {
            Info("restart the lab for the rename warning");
            return;
        }
        var warning = Blocks.Warnings("blocks-test").FirstOrDefault(w => w.Contains("before-rename", StringComparison.Ordinal));
        Check(warning != null && warning.Contains("Was", StringComparison.Ordinal), $"a block renamed without Was says so: \"{warning}\"");
        Check(Content.Items.Get("blocks-test.before-rename") != null, "and the world keeps the old item as a placeholder");
    }

    // ---- a sequence that consumes the used item, with no used item ----------------------------------------------------

    private async Task ConsumingSequence()
    {
        GameDataEntry? potion = null;
        string? path = null;
        for (var i = 0; i < Content.Sequences.Count && potion == null; i++)
        {
            var e = Content.Sequences[i];
            if (e.GetJson("events")?["value"] is not JsonArray events) continue;
            var seen = new Dictionary<string, int>();
            foreach (var ev in events)
            {
                var type = (string?)ev?["$type"] ?? "";
                var brief = type.Split(':')[^1];
                seen[brief] = seen.GetValueOrDefault(brief) + 1;
                if (brief == "SpawnImpact" && ev?["consume"] is JsonValue v && v.TryGetValue<bool>(out var on) && on)
                {
                    potion = e;
                    path = $"events[SpawnImpact#{seen[brief] - 1}].consume";
                    break;
                }
            }
        }
        if (!Check(potion != null, $"a game sequence with a consuming SpawnImpact: {potion?.Name} {path}")) return;
        var user = await Make("1_Player", new WorldPosition(_at.X - 14, _at.Y, _at.Z + 14));
        Check(!Abilities.PlaySequence(user, potion!.Guid), $"PlaySequence refuses it: the server is still here, and the log says why");
        await Steps(25);
        Check(World.Exists(user), "and nothing happened to the one it was for");
        var staff = Content.Sequences.Get("Attack_Player_Staff_FixedAnim_ChainLightning");
        Check(staff != null && Abilities.PlaySequence(user, staff.Guid, new SequenceOptions { HoldInputs = false }), "a staff attack whose cost event has consume on plays: the cost payer checks for no item");
        await Steps(25);
        Spawn.Destroy(user);
    }

    // ---- what a used item does to a program's numbers ------------------------------------------------------------------

    private async Task ItemDamage(EntityId caster)
    {
        var enemy = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 2, _at.Y, _at.Z - 26));
        var place = new WorldPosition(_at.X, _at.Y, _at.Z - 26);
        var staff = Content.Items.Get("Weapon_T1_2H_Staff_Craft")!.Id;
        async Task<(float Ice, float Fire)> Cast(string spell, uint used, uint ammo)
        {
            var impact = Content.Templates.Get($"blocks-test.{spell}.impact")!;
            var program = BlockIds.Fnv(impact.Get("Impact.program").Guid);
            var c = new byte[0x2c];
            BitConverter.GetBytes(used).CopyTo(c, 8);
            BitConverter.GetBytes(ammo).CopyTo(c, 0x14 + 8);
            _hits.Clear();
            await Abilities.RunImpact(impact.Guid, place, new ImpactOptions { Root = caster, Tag = "blocks-test", Components = [new SpawnComponent(0x4006f1f8, c)] });
            await Steps(12);
            var mine = _hits.Where(h => h.Impact.ProgramId == program && h.Target == enemy).ToList();
            return (mine.Sum(h => h.Damage.Ice), mine.Sum(h => h.Damage.Fire));
        }
        var flat = Blocks.Find("flat-bolt")!.Id;
        var plain = await Cast("flat-bolt", 0, 0);
        Check(plain == (40f, 9f), $"with no item: the numbers written, ice {plain.Ice}, fire {plain.Fire}");
        var withStaff = await Cast("flat-bolt", staff, flat);
        Check(withStaff == (40f, 9f), $"Flat with a staff and the spell as ammunition: still the numbers written, ice {withStaff.Ice}, fire {withStaff.Fire}");
        var two = Blocks.Find("two-hits")!.Id;
        var scaled = await Cast("two-hits", staff, two);
        Check(scaled.Ice > 40 && scaled.Fire == 9f, $"not Flat: the main hit takes the staff's power, the second hit stays as written: ice {scaled.Ice}, fire {scaled.Fire}");
        var flatItem = Content.ItemData.Get("blocks-test.flat-bolt")!;
        var power = (int)((float)(int)(10 * 0.15f * 1 + 10) * (float)N(flatItem, "damageSetup.dmgMod") * (float)N(flatItem, "damageSetup.speedMod"));
        Check(power == 40, $"and a Flat spell's tooltip shows its number: {power}");
        Spawn.Destroy(enemy);
    }

    // ---- what follows a wielder ----------------------------------------------------------------------------------

    private async Task Wielder(EntityId wearer)
    {
        var hold = Blocks.Find("hold")!;
        var mod = Blocks.ModFor("blocks-test", () => 0);
        var ctx = new BlockContext(mod, "wielder test");
        var (program, blob, _, listing) = EffectEmitter.WielderProgram(ctx, "wielder.test",
            [new EffectBlock { Damage = Damage.Ice(11) }], "onHit",
            [new EffectBlock { Damage = Damage.Fire(13) }, new EffectBlock { On = EffectOn.Caster, Applies = "hold" }], "onKill",
            [new EffectBlock { Damage = Damage.Lightning(17) }], "onHitTaken");
        var low = new Lowered(mod);
        low.Program(program, "wielder.test", blob, listing);
        low.Commit();
        var defender = await Make("1_Player", new WorldPosition(_at.X - 8, _at.Y, _at.Z - 8));
        Combat.SetInvulnerable(wearer, false);
        var runner = await Abilities.RunProgram(program, World.Position(defender)!.Value, new ImpactOptions { Root = defender, Tag = "blocks-wielder" });
        await Steps(4);
        var id = BlockIds.Fnv(program);

        var enemy = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X - 6, _at.Y, _at.Z - 8));
        async Task Hit(EntityId from, EntityId to)
        {
            var at = World.Position(to)!.Value;
            var he = await Abilities.RunProgram(Swing, at, new ImpactOptions
            {
                Root = from,
                Tag = "blocks-test",
                Configs = new ImpactConfigs
                {
                    [Cfg("blockstest.Target")] = to,
                    [Cfg("blockstest.At")] = new[] { (float)at.X, (float)at.Y, (float)at.Z }.SelectMany(BitConverter.GetBytes).ToArray(),
                },
            });
            await Steps(16);
            if (World.Exists(he)) Spawn.Destroy(he);
        }

        _hits.Clear();
        await Hit(defender, enemy);
        Check(_hits.Count(h => h.Impact.ProgramId == id && h.Target == enemy && Math.Abs(h.Damage.Ice - 11) < 1e-3) == 1, $"OnHit: once, on what was hit: [{string.Join("; ", _hits.Where(h => h.Impact.ProgramId == id).Select(h => $"ice {h.Damage.Ice} fire {h.Damage.Fire}"))}]");
        Check(!_hits.Any(h => h.Impact.ProgramId == id && h.Damage.Fire > 0), "OnKill: not for a hit that did not kill");

        _hits.Clear();
        await Hit(enemy, defender);
        Check(_hits.Count(h => h.Impact.ProgramId == id && h.Target == enemy && Math.Abs(h.Damage.Lightning - 17) < 1e-3) == 1, $"OnHitTaken: once, on who dealt it: [{string.Join("; ", _hits.Where(h => h.Impact.ProgramId == id).Select(h => $"{h.Target} lightning {h.Damage.Lightning}"))}]");

        // a kill: down to almost nothing first
        var health = Combat.GetHealth(enemy);
        Combat.SetHealth(enemy, 1);
        await Steps(4);
        var holds0 = BuffsOf(hold.Id).Count;
        _hits.Clear();
        _buffs.Clear();
        await Hit(defender, enemy);
        Info($"kill: health was {health?.Current}, now {Combat.GetHealth(enemy)?.Current}; hits [{string.Join("; ", _hits.Select(h => $"{h.Target} 0x{h.Impact.ProgramId:x8} fire {h.Damage.Fire} cut {h.Damage.Cut}"))}]");
        Check(_hits.Any(h => h.Impact.ProgramId == id && h.Target == enemy && Math.Abs(h.Damage.Fire - 13) < 1e-3), "OnKill: on what was killed");
        Check(_buffs.Any(b => b.Buff == hold.Guid && b.Target == defender), $"OnKill: On = Caster reaches the one who killed it ({BuffsOf(hold.Id).Count - holds0} more)");
        Spawn.Destroy(runner);
    }

    // ---- tooltip rows: as many as the block has ---------------------------------------------------------------------

    private async Task Rows()
    {
        await Steps(1);
        static int Rows(GameDataEntry e) => e.GetJson("uiValues")?["value"] is JsonArray a ? a.Count : -1;
        var frost = Content.ItemData.Get($"{Sample}.frostbolt")!;
        Check(Rows(frost) == 4, $"the frostbolt's tooltip has its 4 rows: {Rows(frost)}");
        var bolt = Content.ItemData.Get("blocks-test.bolt-parts")!;
        Check(Rows(bolt) == 4 && (uint)N(bolt, "uiValues[3].locaId") == 0xc9d82dea && N(bolt, "uiValues[3].value") == 400, $"rows of an area inside a Then: Radius {N(bolt, "uiValues[3].value")}, {Rows(bolt)} rows");
        var hit = Content.ItemData.Get("blocks-test.hit-only")!;
        Check(Rows(hit) == 3, $"a spell with no area has no Radius row: {Rows(hit)} rows");
        var plain = Content.ItemData.Get("Ammo_T1_Spell_Ice_Bolt_lvl5")!;
        Check(N(plain, "uiValues[2].value") == 8000 && Rows(plain) == 4, "the game's own spell keeps its rows");
        Check(N(plain, "impactValues.ManaCost") == plain.Original("impactValues.ManaCost").Number && plain.Original("impactValues.ManaCost").Number == 47, $"and its mana cost: {N(plain, "impactValues.ManaCost")}: a clone's configs are its own");
        Check(N(frost, "impactValues.ManaCost") == 30 && N(bolt, "impactValues.ManaCost") == 5, $"two spells on one starter each keep their own: {N(frost, "impactValues.ManaCost")}, {N(bolt, "impactValues.ManaCost")}");

        // what a Kiln client without the mod gets: the list as one value, and it makes the same list
        var items = JsonNode.Parse(BlockRuntime.SyncExport() ?? "{}")?["items"] as JsonObject;
        var row = items?.Select(x => x.Value).FirstOrDefault(x => (string?)x?["@name"] == frost.Name)?["uiValues"] as JsonArray;
        Check(row is { Count: 4 }, $"the content sync carries the list as one value: {row?.ToJsonString()}");
        if (row != null)
        {
            var doc = new JsonObject { ["format"] = 1, ["items"] = new JsonObject { [hit.Name] = new JsonObject { ["uiValues"] = row.DeepClone() } } };
            var report = Content.Apply(doc.ToJsonString(), "blocks-test sync");
            Check(report.Errors.Count == 0 && Rows(hit) == 4 && N(hit, "uiValues[3].value") == N(frost, "uiValues[3].value"), $"and the receiver makes the same list: {Rows(hit)} rows, {string.Join("; ", report.Errors)}");
        }
    }
}
