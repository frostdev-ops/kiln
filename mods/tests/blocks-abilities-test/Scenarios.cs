// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksAbilitiesTest;

public sealed partial class BlocksAbilitiesTestMod
{
    private WorldPosition Off(double x, double z) => new(_at.X + x, _at.Y, _at.Z + z);

    private async Task<EntityId> Character(double x, double z)
    {
        var e = await Make("1_Player", Off(x, z));
        Combat.SetInvulnerable(e, true);
        return e;
    }

    private Task<EntityId> Enemy(double x, double z) => Make("Enemy_Vukah_Melee", Off(x, z));

    /// <summary>A program of a block by the end of its key, as its report lists it.</summary>
    private static uint ProgramOf(string block, string keyEnd)
    {
        var low = JsonNode.Parse(Blocks.Lowered(block, Me) ?? "{}");
        var program = (low?["programs"] as JsonArray)?.FirstOrDefault(p => ((string?)p?["key"])?.EndsWith(keyEnd, StringComparison.Ordinal) == true);
        return program == null ? 0 : BlockIds.Fnv(Guid.Parse((string)program["guid"]!));
    }

    private int Ran(uint program) => _started.GetValueOrDefault(program);

    private List<EntityId> Struck(double fire) => _hits.Where(h => Math.Abs(h.Damage.Fire - fire) < 1e-3).Select(h => h.Target).ToList();

    private async Task<bool> Cast(EntityId caster, string ability, int steps = 16)
    {
        _hits.Clear();
        _buffs.Clear();
        var r = Abilities.Cast(caster, $"{Me}:{ability}");
        if (!r.Ok) Info($"{ability}: {r.Reason}");
        await Steps(steps);
        return r.Ok;
    }

    // ---- perks: the game's rule ---------------------------------------------------------------------------------

    private async Task Perks()
    {
        var wearer = await Character(-4, -4);
        var target = await Enemy(-2, -4);
        var sword = Blocks.Find("perk-sword", Me)!;
        var program = ProgramOf("perk-hit", ".perk");
        var mark = Blocks.Find("mark", Me)!;
        async Task<(int Burst, int Marked)> Swung()
        {
            _hits.Clear();
            _buffs.Clear();
            await Swing(wearer, target);
            return (_hits.Count(h => h.Impact.ProgramId == program && Math.Abs(h.Damage.Ice - 11) < 1e-3 && h.Target == target), _buffs.Count(b => b.Buff == mark.Guid && b.Target == target));
        }
        var strength0 = await Attr("strength", wearer);
        var give = await Items.Give(wearer, sword.GameName, 1);
        Info($"give the sword: {give}");
        await Steps(20);
        var slot = Items.Read(wearer)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == sword.Id);
        if (!Check(slot is { ItemEntity.IsNone: false }, $"the sword is in the bag with an item entity: slot {slot?.Slot}")) return;
        var index = (uint)slot!.Slot.Index;
        GameTree.GameRequest(wearer, 5, index);
        await Steps(24);
        var locked = await Swung();
        Check(locked == (0, 0), $"in hand, no enhancement unlocked: a hit does nothing more ({locked}). The game's rule");
        Check(await Attr("strength", wearer) == strength0, "and no stat changes");
        Check(GameTree.GameRequest(slot.ItemEntity, 4, 1), "the upgrade: one enhancement unlocked");
        GameTree.GameRequest(wearer, 5, index == 0 ? 1u : 0u);
        await Steps(24);
        GameTree.GameRequest(wearer, 5, index);
        await Steps(24);
        var one = await Swung();
        Check(one == (1, 1), $"the first perk runs: every hit its effects, once ({one}), through the game's hit stream and the game's own perk instance");
        Check(_hits.Count(h => h.Impact.ProgramId == program) == 1, $"a hit dealt by the effect does not trigger the effect again: {_hits.Count(h => h.Impact.ProgramId == program)}");
        Check(await Attr("strength", wearer) == strength0, "the second perk is still locked");
        GameTree.GameRequest(slot.ItemEntity, 4, 2);
        GameTree.GameRequest(wearer, 5, index == 0 ? 1u : 0u);
        await Steps(24);
        var hidden = await Swung();
        Check(hidden == (0, 0), $"another hotbar slot: the perk has gone with it ({hidden})");
        GameTree.GameRequest(wearer, 5, index);
        await Steps(24);
        var strength1 = await Attr("strength", wearer);
        Check(strength1 == strength0 + 5, $"the second perk unlocked: Modifies, strength {strength0} -> {strength1}");
        GameTree.GameRequest(wearer, 5, index == 0 ? 1u : 0u);
        await Steps(24);
        Check(await Attr("strength", wearer) == strength0, "and it comes off with the item");
        Spawn.Destroy(target);
    }

    // ---- what an ability costs ----------------------------------------------------------------------------------

    private async Task AbilityCosts()
    {
        var caster = await Character(10, -10);
        var mana0 = await Attr("mana", caster);
        var stamina0 = await Attr("stamina", caster);
        Check(await Cast(caster, "mend"), "a block's ability casts through kiln.abilities");
        var mana1 = await Attr("mana", caster);
        var stamina1 = await Attr("stamina", caster);
        Check(mana1 >= mana0 - 10 && mana1 < mana0, $"it pays its mana: {mana0} -> {mana1}");
        Check(stamina1 >= stamina0 - 5 && stamina1 < stamina0, $"and its stamina: {stamina0} -> {stamina1}");
        var again = Abilities.Cast(caster, $"{Me}:mend");
        Check(!again.Ok && again.Reason!.Contains("cooldown", StringComparison.Ordinal), $"its cooldown: {again.Reason}");
        Abilities.ResetCooldown(caster, $"{Me}:mend");
        Abilities.Attributes.Add(caster, AbilityResource.Mana, -(int)(mana1 ?? 0));
        await Steps(2);
        var poor = Abilities.Cast(caster, $"{Me}:mend");
        Check(!poor.Ok && poor.Reason!.Contains("mana", StringComparison.Ordinal), $"too little mana: {poor.Reason}");
        Check(Abilities.Registered.First(a => a.Id == $"{Me}:mend").Targeting == AbilityTargeting.Forward, "where it aims comes from the block");

        // a spell that takes 3 charges: the game takes one, its flight's start the other two
        var spell = Blocks.Find("stamina-spell", Me)!;
        await Items.Give(caster, spell.GameName, 5);
        await Steps(4);
        var flight = Content.Templates.Get($"{Me}.stamina-spell.projectile")!.Get("Impact.program").Guid;
        var running = await Abilities.RunProgram(flight, World.Position(caster)!.Value, new ImpactOptions { Root = caster, Tag = "blocks-abilities-test" });
        await Steps(12);
        var left = Items.Read(caster)?.Count(new ItemId(spell.Id));
        Check(left == 3, $"Charges = 3 on a spell: its flight takes 2 more at the cast, besides the game's one: 5 -> {left}");
        if (World.Exists(running)) Spawn.Destroy(running);
        Spawn.Destroy(caster);
    }

    // ---- who each delivery reaches ------------------------------------------------------------------------------

    private async Task Deliveries()
    {
        var caster = await Character(30, 0);
        var front = await Enemy(30, 5);
        var line = await Enemy(30, 15);
        var side = await Enemy(36, 0);
        var back = await Enemy(30, -5);
        var far = await Enemy(30, 45);
        var friend = await Make("1_Player", Off(31, 3));
        Combat.SetInvulnerable(friend, true);
        foreach (var e in new[] { front, line, side, back, far }) Combat.SetInvulnerable(e, true);
        string Names(IEnumerable<EntityId> list) => string.Join(",", list.Select(e => e == front ? "front" : e == line ? "line" : e == side ? "side" : e == back ? "back" : e == far ? "far" : e == friend ? "friend" : e == caster ? "caster" : e.ToString()));
        void Exactly(double fire, string what, params EntityId[] expected)
        {
            var got = Struck(fire);
            Check(got.Count == expected.Length && expected.All(got.Contains), $"{what}: {Names(got)}");
        }

        // where a caster faces is where a delivery goes: a caster turned a quarter
        var turned = await Spawn.Entity("1_Player", Off(30, 0), 90, new SpawnOptions { Tag = "blocks-abilities-test", Flags = SpawnFlags.Transient });
        _spawned.Add(turned);
        await Steps(10);
        Combat.SetInvulnerable(turned, true);
        Info($"the turned caster faces {World.Transform(turned)?.YawDegrees}");
        Check(await Cast(turned, "cone"), "Cone, turned");
        Exactly(22, "turned a quarter, the same cone reaches what stands to its new front", side);
        Check(await Cast(turned, "beam"), "Beam, turned");
        Exactly(23, "and a beam", side);
        Spawn.Destroy(turned);
        await Steps(4);

        Check(await Cast(caster, "nova"), "Around casts");
        Exactly(21, "Around, 6 m: what stands within it, not the friend or who is further", front, side, back);
        Check(await Cast(caster, "cone"), "Cone casts");
        Exactly(22, "Cone, 8 m and 60 degrees: only what stands in front", front);
        Check(await Cast(caster, "beam"), "Beam casts");
        Exactly(23, "Beam, 20 m: what stands on the line, near and far, and nothing beside or behind", front, line);
        Check(await Cast(caster, "long-beam", 70), "a lasting beam casts");
        var ticks = Struck(27).Count(e => e == line);
        Check(ticks is >= 2 and <= 4, $"a lasting beam keeps hitting: {ticks} hits in 1.5 s, every 0.5 s");
        Check(!Struck(27).Contains(side), "and only on its line");
        Check(await Cast(caster, "zone", 80), "Zone casts");
        var zoned = Struck(24);
        Check(zoned.Count(e => e == front) is >= 3 and <= 5, $"Zone, 6 m ahead: it keeps hitting what stands in it: {zoned.Count(e => e == front)} hits in 2 s, every 0.5 s");
        Check(!zoned.Contains(side) && !zoned.Contains(back) && !zoned.Contains(line), $"and nothing out of its 4 m: {Names(zoned.Distinct())}");
        Check(Spawn.FindByName($"{Me}.zone.delivery", max: 5).Count == 0, "it is gone at its end");

        // ---- a chain: one after another, never the same twice
        var one = await Enemy(30, 8);
        var two = await Enemy(35, 9);
        var three = await Enemy(39, 11);
        var four = await Enemy(70, 70);
        foreach (var e in new[] { one, two, three, four }) Combat.SetInvulnerable(e, true);
        Check(await Cast(caster, "chain", 24), "Chain casts");
        var chained = Struck(25);
        Info($"chain: {string.Join(",", chained)} (front {front} line {line} one {one} two {two} three {three} four {four})");
        Check(chained.Count == 3 && chained.Distinct().Count() == 3, $"Chain, 2 jumps: three different targets, once each: {chained.Count}");
        Check(!chained.Contains(four), "and none out of reach");

        // ---- a summon
        var zones0 = Spawn.FindByName(GameZone, max: 50).Count;
        Check(await Cast(caster, "summon", 1), "Summon casts");
        var zones1 = zones0;
        for (var i = 0; i < 40 && zones1 < zones0 + 2; i++)
        {
            await Steps(1);
            zones1 = Math.Max(zones1, Spawn.FindByName(GameZone, max: 50).Count);
        }
        Check(zones1 == zones0 + 2, $"Summon: two of a game's template by its name: {zones0} -> {zones1}");
        var zones2 = zones1;
        for (var i = 0; i < 120 && zones2 != zones0; i++)
        {
            await Steps(1);
            zones2 = Spawn.FindByName(GameZone, max: 50).Count;
        }
        Check(zones2 == zones0, $"and they are taken away at its time: {zones2}");

        // ---- on the caster
        var mark = Blocks.Find("mark", Me)!;
        var marks = BuffsOf(mark.Id).Count;
        Check(await Cast(caster, "self-buff"), "Self casts");
        Check(BuffsOf(mark.Id).Count == marks + 1 && _buffs.Any(b => b.Buff == mark.Guid && b.Target == caster), "Self: the status is on the caster");
        Check(Ran(ProgramOf("self-buff", ".cast")) >= 1, "its program ran");

        // ---- a spell's item placed it: a real cast's power reaches the program
        var spell = Blocks.Find("cone-spell", Me)!;
        var placed = Content.Templates.Get($"{Me}.cone-spell.delivery")!;
        var config = new byte[0x2c];
        BitConverter.GetBytes(spell.Id).CopyTo(config, 0x14 + 8);
        _hits.Clear();
        await Abilities.RunImpact(placed.Guid, World.Position(caster)!.Value, new ImpactOptions { Root = caster, YawDegrees = 0, Tag = "blocks-abilities-test", Components = [new SpawnComponent(0x4006f1f8, config)] });
        await Steps(12);
        var main = _hits.FirstOrDefault(h => h.Target == front);
        Check(main != null && Math.Abs(main.Damage.Fire - 41) < 1e-3, $"a spell's cone: its main hit is the item's power, ice bolt or not: fire {main?.Damage.Fire}");
        Check(Struck(41).All(h => h == front || h == one), $"and it reaches what its cone does: {Names(Struck(41))}");
        Spawn.Destroy(friend);
        foreach (var e in new[] { caster, front, line, side, back, far, one, two, three, four }) Spawn.Destroy(e);
    }

    // ---- flights ------------------------------------------------------------------------------------------------

    private async Task Flights()
    {
        var caster = await Character(-30, 0);
        var target = await Enemy(-26, 4);
        Combat.SetInvulnerable(target, true);
        var name = $"{Me}.bolt.projectile";
        var before = Spawn.FindByName(name, max: 10).Count;
        Check(await Cast(caster, "bolt", 3), "an ability's flight casts");
        var flight = Spawn.FindByName(name, max: 10).FirstOrDefault();
        Check(!flight.IsNone && Spawn.FindByName(name, max: 10).Count == before + 1, $"the launcher sends it: {flight}");
        if (!flight.IsNone)
        {
            var v = World.ReadBytes(flight, "Velocity");
            var vz = v is { Length: >= 12 } ? BitConverter.ToSingle(v, 8) : float.NaN;
            var vx = v is { Length: >= 12 } ? BitConverter.ToSingle(v, 0) : float.NaN;
            Info($"bolt: velocity {vx}, {vz}, at {World.Position(flight)}, caster at {World.Position(caster)}");
            Check(Math.Abs(vz - 20) < 1e-1 && Math.Abs(vx) < 1e-1, $"where the caster faces, at its speed: ({vx}, {vz})");
            var p0 = World.Position(flight);
            await Steps(10);
            var p1 = World.Exists(flight) ? World.Position(flight) : null;
            Info($"bolt moved from {p0} to {p1}");
        }

        _hits.Clear();
        var aimed = Abilities.Cast(caster, $"{Me}:hunt");
        Info($"hunt: target {aimed.Cast?.Target} at {aimed.Cast?.Position}, enemy {target} at {World.Position(target)}, caster at {World.Position(caster)}");
        await Steps(3);
        Check(aimed.Ok, "an ability that hunts the nearest enemy casts");
        Check(aimed.Cast?.Target == target, $"at the nearest enemy: {aimed.Cast?.Target}");
        Abilities.Cast(caster, $"{Me}:hunt-plain");
        await Steps(2);
        var plain = Spawn.FindByName($"{Me}.hunt-plain.projectile", max: 10).FirstOrDefault();
        if (Check(!plain.IsNone, $"with no homing too: {plain}"))
        {
            var v = World.ReadBytes(plain, "Velocity");
            var vx = v is { Length: >= 12 } ? BitConverter.ToSingle(v, 0) : float.NaN;
            var vz = v is { Length: >= 12 } ? BitConverter.ToSingle(v, 8) : float.NaN;
            Check(Math.Abs(vx - vz) < 0.5 && vx > 10, $"it leaves along the bearing of the enemy, what the cast aims at: ({vx}, {vz})");
        }
        var hunter = Spawn.FindByName($"{Me}.hunt.projectile", max: 10).FirstOrDefault();
        if (Check(!hunter.IsNone, $"its flight leaves: {hunter}"))
        {
            var v = World.ReadBytes(hunter, "Velocity");
            var vx = v is { Length: >= 12 } ? BitConverter.ToSingle(v, 0) : float.NaN;
            var vz = v is { Length: >= 12 } ? BitConverter.ToSingle(v, 8) : float.NaN;
            Check(Math.Abs(vx - vz) < 4 && vx > 8, $"it leaves along the bearing of the enemy: ({vx}, {vz})");
            var p0 = World.Position(hunter);
            var enemyAt = World.Position(target)!.Value;
            await Steps(12);
            var p1 = World.Exists(hunter) ? World.Position(hunter) : null;
            Info($"homing flight: {p0} -> {p1}, enemy at {enemyAt}");
            var landed = Struck(28);
            Check(landed.Count == 1 && landed[0] == target, $"it reaches the enemy, and OnHit runs on it, once, through a real collision: {string.Join(",", landed)}");
        }

        // sent straight ahead with an enemy off to one side: the game's homing turns it
        var launch = Content.Templates.Get($"{Me}.hunt.launch")!;
        var side = await Enemy(-14, 8);
        Combat.SetInvulnerable(side, true);
        Spawn.Destroy(target);
        await Abilities.RunImpact(launch.Guid, World.Position(caster)!.Value, new ImpactOptions { Root = caster, YawDegrees = 0, Tag = "blocks-abilities-test", Configs = new ImpactConfigs { [$"0x{EffectEmitter.CfgTarget:x8}"] = side } });
        await Steps(2);
        var seeker = Spawn.FindByName($"{Me}.hunt.projectile", max: 10).LastOrDefault();
        var track = new List<(float X, float Z)>();
        for (var i = 0; i < 6 && !seeker.IsNone && World.Exists(seeker); i++)
        {
            var v = World.ReadBytes(seeker, "Velocity");
            if (v is { Length: >= 12 }) track.Add((BitConverter.ToSingle(v, 0), BitConverter.ToSingle(v, 8)));
            await Steps(2);
        }
        Info($"sent straight ahead, enemy to the side: velocity {string.Join(" -> ", track.Select(t => $"({t.X:0.0}, {t.Z:0.0})"))}");
        Check(track.Count >= 3 && track[^1].X > track[0].X + 2, "Homing: the game's own component turns it toward the enemy it was given, on a server with no player");
        Spawn.Destroy(side);

        var seqFlight = $"{Me}.staff-bolt.projectile";
        var flightProgram = ProgramOf("staff-bolt", ".projectile");
        var n0 = Ran(flightProgram);
        var sequence = Content.Sequences.Get($"{Me}.staff-bolt.cast")!;
        Check(sequence.Get("events[SpawnEntityEvent#0].templateReference").Guid == Content.Templates.Get(seqFlight)!.Guid, "an ability with a motion: the motion's event names its flight");
        Check(Math.Abs(N(sequence, "events[SpawnEntityEvent#0].spawnVelocity.z") - 20) < 1e-3, "at its speed");
        Check(await Cast(caster, "staff-bolt", 10), "it casts");
        Check(Abilities.SequenceOf(caster) is { } running && running.Sequence == Abilities.IdOf(sequence.Guid), $"the caster plays the motion: {Abilities.SequenceOf(caster)}");
        for (var i = 0; i < 60 && Ran(flightProgram) == n0; i++) await Steps(1);
        Check(Ran(flightProgram) == n0 + 1, $"and it sends the flight at its release, with no used item: its program started {Ran(flightProgram) - n0} time(s)");
        Spawn.Destroy(caster);
    }

    // ---- what an item starts ------------------------------------------------------------------------------------

    private async Task ItemTriggered()
    {
        var user = await Character(-30, 30);
        var scroll = Blocks.Find("scroll.item", Me);
        if (!Check(scroll != null, "an ability's item is a block of its own")) return;
        var item = Content.ItemData.Get(scroll!.GameName)!;
        var useSequence = Content.Sequences.Get($"{Me}.scroll.item.use")!;
        var program = useSequence.Get("events[SpawnImpact#0].impact").Guid;
        Check(item.Get("sequences[0].sequence").Guid == useSequence.Guid && program != Guid.Empty, "using it runs its program, in a copy of the game's use sequence");
        await Items.Give(user, scroll.GameName, 5);
        await Steps(10);
        int Count() => Items.Read(user)?.Count(new ItemId(scroll.Id)) ?? -1;
        var mana0 = await Attr("mana", user);
        Check(Count() == 5, $"five of it in the bag: {Count()}");
        await Abilities.RunProgram(program, World.Position(user)!.Value, new ImpactOptions { Root = user, Tag = "blocks-abilities-test" });
        await Steps(16);
        var mana1 = await Attr("mana", user);
        Check(mana1 < mana0, $"the ability is cast: it paid its mana, {mana0} -> {mana1}");
        Check(Count() == 4, $"Charges = 2: besides the one the game's use takes, a cast takes one more: {Count()}");
        Check(Abilities.CooldownLeft(user, $"{Me}:scroll") > TimeSpan.Zero, "and its cooldown runs");
        await Abilities.RunProgram(program, World.Position(user)!.Value, new ImpactOptions { Root = user, Tag = "blocks-abilities-test" });
        await Steps(16);
        Check(Count() == 5, $"a cast that is refused gives back what the use took: {Count()}");

        var charm = Content.Sequences.Get($"{Me}.charm.item.use")!;
        Check(charm.Get("events[SpawnImpact#0].consume").Bool == false, "Charges = 0: the use keeps the item");

        // an effect that casts
        var perk = Content.Perks.Get($"{Me}.casts-on-hit")!;
        var running = await Abilities.RunProgram(perk.Get("impact").Guid, World.Position(user)!.Value, new ImpactOptions { Root = user, Tag = "blocks-abilities-test" });
        await Steps(4);
        var enemy = await Enemy(-28, 30);
        var mark = Blocks.Find("mark", Me)!;
        _buffs.Clear();
        await Swing(user, enemy);
        await Steps(8);
        Check(_buffs.Any(b => b.Buff == mark.Guid && b.Target == user), "Casts: a perk's hit casts an ability on the one who hit");
        Spawn.Destroy(running);
        Spawn.Destroy(enemy);

        // a real use, through the game's consume code
        if (!PlayerStates.Diagnostics.LabUseItem(user, Abilities.IdOf(useSequence.Guid), PlayerStateId.ActionPrimary, Items.Read(user)!.Slots.First(s => s.Item.Value == scroll.Id).Slot.Value, scroll.Id))
        {
            Info("KILN_PLAYERSTATE_LAB is off: a real use is not run");
            return;
        }
        Abilities.ResetCooldown(user, $"{Me}:scroll");
        await Steps(60);
        Check(Count() == 3, $"used for real, by the game: the stack goes down by its Charges, 5 -> {Count()}");
        Spawn.Destroy(user);
    }

    // ---- a tree of its own --------------------------------------------------------------------------------------

    private async Task TreeTalents()
    {
        const string Tree = $"{Me}:arts";
        var p = await Character(-30, -30);
        var h = Skills.HolderOf(p);
        Skills.Grant(Tree, h, 10);
        var total = Skills.Points(Tree, h);
        var strength0 = await Attr("strength", p);
        var speed0 = await Attr("move-speed", p);
        async Task Levelled(int level)
        {
            await Steps(8);
            var strength = await Attr("strength", p);
            var speed = await Attr("move-speed", p);
            Check(strength == strength0 + 2 * level, $"level {level}: strength {strength0} -> {strength}, 2 a level");
            Check(speed is { } s && speed0 is { } s0 && Math.Abs(s - s0 - 0.1 * level) < 1e-3, $"level {level}: move speed {speed0} -> {speed}, 0.1 a level");
        }
        var gate = Skills.Unlock(Tree, "gate", h);
        Check(!gate.Ok && gate.Reason.Contains("Hearth", StringComparison.Ordinal), $"prerequisites: {gate.Reason}");
        Check(Skills.Unlock(Tree, "kindle", h).Ok, "unlock through the existing path");
        await Levelled(1);
        Check(Skills.Unlock(Tree, "kindle", h).Ok, "the next level");
        await Levelled(2);
        Check(Skills.Points(Tree, h).Spent == 2, $"it costs per level: {Skills.Points(Tree, h)}");
        Check(Skills.Lock(Tree, "kindle", h).Ok, "refund one level");
        await Levelled(1);

        var enemy = await Enemy(-28, -30);
        _hits.Clear();
        await Swing(p, enemy);
        Check(Struck(31).Count == 0, "a hit does nothing before the talent");
        Check(Skills.Unlock(Tree, "hearth", h).Ok, "unlock a talent with an effect");
        await Steps(8);
        _hits.Clear();
        await Swing(p, enemy);
        Check(Struck(31).Count == 1 && Struck(31)[0] == enemy, $"OnHit: with it, each hit runs its effect once: {Struck(31).Count}");

        var mark = Blocks.Find("mark", Me)!;
        var marks = BuffsOf(mark.Id).Count;
        Check(!Skills.Lock(Tree, "kindle", h).Ok, "a node another hangs on is not given back");
        Check(Skills.Unlock(Tree, "gate", h).Ok, "Keeps: a talent with a status");
        await Steps(12);
        Check(BuffsOf(mark.Id).Count == marks + 1, $"it is on the character: {BuffsOf(mark.Id).Count}");
        Check(Skills.Lock(Tree, "gate", h).Ok, "refund");
        await Steps(12);
        Check(BuffsOf(mark.Id).Count == marks, $"and it comes off: {BuffsOf(mark.Id).Count}");
        Check(Skills.Respec(Tree, h).Ok && Skills.Points(Tree, h).Available == total.Total, $"a respec gives every point back: {Skills.Points(Tree, h)}");
        await Steps(8);
        Check(await Attr("strength", p) == strength0, "and every stat");
        _hits.Clear();
        await Swing(p, enemy);
        Check(Struck(31).Count == 0, "and the effect stops");

        const string KeyTree = $"{Me}:key-tree";
        Skills.Grant(KeyTree, h, 2);
        var shut = Abilities.Cast(p, $"{Me}:gated");
        Check(!shut.Ok && shut.Reason!.Contains("Unlock", StringComparison.Ordinal), $"an ability a talent unlocks: without it, \"{shut.Reason}\"");
        Check(Skills.Unlock(KeyTree, "unlock", h).Ok, "take the talent");
        Check(Abilities.Cast(p, $"{Me}:gated").Ok, "and it casts");
        Skills.Respec(KeyTree, h);
        Spawn.Destroy(enemy);
        Spawn.Destroy(p);
    }

    // ---- the game's own tree ------------------------------------------------------------------------------------

    private async Task GameTalents()
    {
        var def = GameTree.Definitions.First(d => d.Name == $"{Me}.ward");
        var share = GameTree.Definitions.First(d => d.Name == $"{Me}.ward-share");
        var gateNode = GameTree.Definitions.First(d => d.Name == $"{Me}.ward-gate");
        Info($"game-tree nodes: {def.Name} {def.State} index {def.Index}; {share.Name} {share.State} index {share.Index}; {gateNode.Name} {gateNode.State} index {gateNode.Index}; {GameTree.Status}");
        var cache = Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "defined", $"{Me}.json"));
        Check(File.Exists(cache) && File.ReadAllText(cache).Contains(def.Name, StringComparison.Ordinal), $"kiln.skills keeps the definition for the next start: {cache}");
        var nodes = JsonNode.Parse(GameTree.Dump(addedOnly: true))?["nodes"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var node = nodes.FirstOrDefault(n => (string?)n["id"] == $"0x{def.Id:x8}");
        if (node == null)
        {
            Check(def.State is "pending" or "defined", $"this start read its tree before the mod loaded: {def.State}: restart the lab for the rest");
            return;
        }
        Info($"the node as the game holds it: {node.ToJsonString()}");
        Check((int?)node["maxLevel"] == 3 && (int?)node["cost"] == 0, "the world's tree has it, through the game's own lookup, with its levels and cost");
        var p = await Character(30, 30);
        GameTree.GameRequest(p, 3);
        await Steps(2);
        var strength0 = await Attr("strength", p);
        var speed0 = await Attr("move-speed", p);
        var shadow = GameTree.ShadowNodes.Contains(def.Id);
        Info($"ward is a {(shadow ? "shadow" : "native")} node here");
        int Level(uint id)
        {
            if (GameTree.ShadowNodes.Contains(id)) return GameTree.ShadowLevels(p).GetValueOrDefault(id);
            var b = World.ReadBytes(p, "UnlockedSkillNodes");
            if (b == null) return 0;
            for (var i = 0; i < 256; i++)
                if (BitConverter.ToUInt32(b, 4 + i * 4) == id) return (b[0x804 + i] >> 5) is var l and > 0 ? l : 1;
            return 0;
        }
        Check(GameTree.GameRequest(p, 0, def.Id), "asked the game to unlock it, as a player's client would");
        await Steps(6);
        Check(Level(def.Id) == 1, $"the game's handler took it: level {Level(def.Id)}");
        GameTree.GameRequest(p, 0, def.Id);
        await Steps(6);
        Check(Level(def.Id) == 2, $"and its next level: {Level(def.Id)}");
        Check(GameTree.Query(p, share.Id) is { Reachable: true }, $"what hangs on it can be reached: {GameTree.Query(p, share.Id)}");
        GameTree.GameRequest(p, 0, share.Id);
        await Steps(6);
        GameTree.GameRequest(p, 0, share.Id);
        await Steps(10);
        double? strength = null, speed = null;
        for (var i = 0; i < 12; i++)
        {
            strength = await Attr("strength", p);
            speed = await Attr("move-speed", p);
            if (strength == strength0 + 2) break;
            await Steps(10);
        }
        if (shadow)
        {
            Check(strength == strength0 + 2, $"Modifies, whole, by the game's level: strength {strength0} -> {strength} at level 2");
            Check(speed is { } s && speed0 is { } s0 && Math.Abs(s - s0 - 0.2) < 1e-3, $"Modifies, a share, by the game's scaled config: move speed {speed0} -> {speed} at level 2");
        }
        else
        {
            Check(strength == strength0 + 2, $"a native node's level 2, through the game's own skill impact: Modifies, whole, strength {strength0} -> {strength}");
            Check(speed is { } s1 && speed0 is { } s2 && Math.Abs(s1 - s2 - 0.2) < 1e-3, $"and a share, move speed {speed0} -> {speed}");
            // what the game does for a skill impact, as the shadow table does it: the node's configs, its level
            var level = new byte[16];
            BitConverter.GetBytes(2u).CopyTo(level, 8);
            BitConverter.GetBytes(99u).CopyTo(level, 12);
            var dump = nodes.First(n => (string?)n["id"] == $"0x{share.Id:x8}");
            var impact = await Abilities.RunProgram(Guid.Parse((string)dump["program"]!), World.Position(p)!.Value, new ImpactOptions
            {
                Root = p, Tag = "blocks-abilities-test",
                Components = [new SpawnComponent(SpawnComponent.HashOf("SkillIdComponent"), BitConverter.GetBytes(share.Id)), new SpawnComponent(SpawnComponent.HashOf("Level"), level)],
            });
            await Steps(8);
            var scaled = await Attr("move-speed", p);
            Check(scaled is { } x && speed is { } y && Math.Abs(x - y - 0.2) < 1e-3, $"the node's program, as the game starts a skill impact at level 2: move speed {speed} -> {scaled}");
            Spawn.Destroy(impact);
            await Steps(6);
        }
        if (!shadow) await LevelLikeTheGame(share.Id);
        var shut = Abilities.Cast(p, $"{Me}:gated-game");
        Check(!shut.Ok && shut.Reason!.Contains("Ward Gate", StringComparison.Ordinal), $"an ability the node unlocks: without it, \"{shut.Reason}\"");
        GameTree.GameRequest(p, 0, gateNode.Id);
        await Steps(6);
        Check(Abilities.Cast(p, $"{Me}:gated-game").Ok, "with it, it casts: the game's own IsUnlocked says so");
        // the refund: the game's unlearn of a free node, and its respec
        GameTree.GameRequest(p, 1, gateNode.Id);
        await Steps(6);
        Check(Level(gateNode.Id) == 0, $"the game's unlearn takes a free node back: level {Level(gateNode.Id)}");
        GameTree.GameRequest(p, 2);
        await Steps(10);
        var left = new[] { def.Id, share.Id }.Select(id => GameTree.Query(p, id)?.Unlocked).ToList();
        Check(left.All(x => x == false), $"and its respec takes them all, by the game's IsUnlocked: {string.Join(", ", left)}");
        await Steps(8);
        Check(await Attr("strength", p) == strength0, "and their stats");
        Spawn.Destroy(p);
    }

    // A node's level changed by the game's NodeSet (kiln.progression, which a lab character needs forced: it has no skill points to read): the game's own levelled node
    // and ours, side by side, through the game's own skill impacts.
    private async Task LevelLikeTheGame(uint ours)
    {
        const uint Vanilla = 0x7e2a4e3c;
        var vanillaAttr = Guid.Parse("0c15fc03-a3f2-4cda-beb7-999f76e6133a");
        var speed = Stat.MoveSpeed.Attribute;
        var p = await Character(30, 40);
        GameTree.GameRequest(p, 3);
        Progression.Diagnostics.LabPlayer(p);
        await Steps(2);
        async Task<(double? V, double? O)> Read() => (await Attr(vanillaAttr, false, p), await Attr(speed, false, p));
        async Task<(double? V, double? O)> Settle((double? V, double? O) was)
        {
            var now = was;
            for (var i = 0; i < 12; i++)
            {
                await Steps(10);
                now = await Read();
                if (now.V != was.V && now.O != was.O) break;
            }
            return now;
        }
        var r0 = await Read();
        var a = await Progression.Unlock(p, Vanilla, force: true);
        var b = await Progression.Unlock(p, ours, force: true);
        var r1 = await Settle(r0);
        var c = await Progression.Unlock(p, Vanilla, force: true);
        var d = await Progression.Unlock(p, ours, force: true);
        var r2 = await Settle(r1);
        Info($"levels 0, 1, 2 by the game's NodeSet ({a}; {b}; {c}; {d}): the game's node 0x{Vanilla:x8} {r0.V} -> {r1.V} -> {r2.V}; ours {r0.O} -> {r1.O} -> {r2.O}");
        bool Moved(double? x, double? y) => x is { } m && y is { } n && Math.Abs(m - n) > 1e-4;
        Check(Moved(r0.O, r1.O) && Moved(r0.V, r1.V), "at level 1 both run, through the game's own skill impacts");
        // both add 0.1 a level
        Check(r1.V - r0.V is { } v1 && r2.V - r1.V is { } v2 && r1.O - r0.O is { } o1 && r2.O - r1.O is { } o2 && Math.Abs(v1 - o1) < 1e-3 && Math.Abs(v2 - o2) < 1e-3,
            $"and from level 1 to 2 ours does what the game's own does: theirs {r1.V - r0.V:0.###} then {r2.V - r1.V:0.###}, ours {r1.O - r0.O:0.###} then {r2.O - r1.O:0.###}");
        Progression.Diagnostics.LabPlayer(EntityId.None);
        Spawn.Destroy(p);
    }

    // ---- what players do ----------------------------------------------------------------------------------------

    private async Task Triggers()
    {
        var e = await Character(60, 0);
        PlayerStates.Diagnostics.Watch(e);
        await Steps(10);
        var lift = Blocks.Find("glide-buff", Me)!;
        var mark = Blocks.Find("mark", Me)!;
        var speed0 = await Attr("move-speed", e);
        var marks0 = BuffsOf(mark.Id).Count;
        Check(Content.Buffs.Get($"{Me}.glide-buff")!.Get("defaultLifeTime").Number >= 3600, "a status held by a state lasts until the state ends");

        var glideRaw = ProgramOf("on-glide-raw", ".do");
        var ran0 = Ran(glideRaw);
        Check(Abilities.PlaySequence(e, Glide), "a glide, through the game's own sequence");
        var on = false;
        for (var i = 0; i < 40 && !on; i++)
        {
            await Steps(1);
            on = BuffsOf(lift.Id).Count == 1;
        }
        Check(on, $"While = Gliding: the status is on with the state: {PlayerStates.Is(e, PlayerStateId.Gliding)}");
        Check(Ran(glideRaw) == ran0 + 1, $"a trigger by the game's name, a state nobody wrote a special case for (LocomotionHangGliding): ran {Ran(glideRaw) - ran0}");
        Check(BuffsOf(mark.Id).Count == marks0 + 1, "an action's trigger, ActionExternalAbortAndOverride, with When = Hungry: its status is on");
        for (var i = 0; i < 300 && PlayerStates.Check(e, PlayerStateId.Gliding) != false; i++) await Steps(1);
        var off = false;
        for (var i = 0; i < 12 && !off; i++)
        {
            await Steps(1);
            off = BuffsOf(lift.Id).Count == 0;
        }
        Check(off, "the state over: the status is off");

        var sit = ProgramOf("on-sit", ".do");
        var stood = ProgramOf("on-sit", ".end");
        var rest = Blocks.Find("sit-buff", Me)!;
        var s0 = Ran(sit);
        var t0 = Ran(stood);
        Check(Abilities.PlaySequence(e, Sit), "sitting down");
        for (var i = 0; i < 25 && Ran(sit) == s0; i++) await Steps(1);
        Check(Ran(sit) == s0 + 1, $"On = Sitting: Do ran once on the way in: {Ran(sit) - s0}");
        Check(Ran(stood) == t0, "OnEnd has not");
        await Steps(4);
        var seated = await Attr("move-speed", e);
        Check(BuffsOf(rest.Id).Count == 1 && seated is { } a1 && speed0 is { } a2 && Math.Abs(a1 - a2 - 0.25) < 1e-3, $"While = Sitting: the status is on, and does what it says: speed {speed0} -> {seated}, sitting {PlayerStates.Is(e, PlayerStateId.Sitting)}");
        Abilities.PlaySequence(e, Greet);
        for (var i = 0; i < 60 && Ran(stood) == t0; i++) await Steps(1);
        Check(Ran(stood) == t0 + 1, $"up again: OnEnd ran once: {Ran(stood) - t0}");
        await Steps(6);
        var standing = await Attr("move-speed", e);
        Check(BuffsOf(rest.Id).Count == 0 && standing is { } a3 && speed0 is { } a4 && Math.Abs(a3 - a4) < 1e-3, $"and the status has gone with it: speed {standing}");
        Spawn.Destroy(e);
    }

    // ---- what is eaten ------------------------------------------------------------------------------------------

    private async Task Eating()
    {
        var e = await Character(60, 30);
        PlayerStates.Diagnostics.Watch(e);
        await Steps(10);
        var mushroom = Content.Items.Get(Mushroom)!.Id;
        await Items.Give(e, new ItemId(mushroom), 5);
        await Steps(4);
        var slot = Items.Read(e)?.Slots.FirstOrDefault(s => s.Item.Value == mushroom);
        if (!Check(slot != null, "a character with five mushrooms")) return;
        var eating = ProgramOf("started-mushroom", ".do");
        var eaten = ProgramOf("eaten-mushroom", ".do");
        int Count(uint item) => Items.Read(e)?.Count(new ItemId(item)) ?? -1;
        if (!PlayerStates.Diagnostics.LabUseItem(e, Abilities.IdOf(UseFood), PlayerStateId.ActionPrimary, slot!.Slot.Value, mushroom))
        {
            Info("KILN_PLAYERSTATE_LAB is off: eating is not run");
            return;
        }
        for (var i = 0; i < 12 && Ran(eating) == 0; i++) await Steps(1);
        Check(Ran(eating) == 1, $"On = Eating, by item: when it starts ({Ran(eating)})");
        Check(Ran(eaten) == 0, "On = Eaten has not run: the item has not gone");
        for (var i = 0; i < 60 && Ran(eaten) == 0; i++) await Steps(1);
        Check(Ran(eaten) == 1 && Count(mushroom) == 4, $"On = Eaten: once the item went, on the game's own consume: ran {Ran(eaten)}, {Count(mushroom)} left");
        await WaitIdle(e);
        Check(PlayerStates.Is(e, PlayerStateId.Fed), "the player is Fed now");
        Check(PlayerStates.Diagnostics.LabUseItem(e, Abilities.IdOf(UseFood), PlayerStateId.ActionPrimary, slot.Slot.Value, mushroom), "eat again, while Fed");
        await Steps(50);
        Check(Count(mushroom) == 4, $"Stop, When = Fed: it stays: {Count(mushroom)}");
        Check(Ran(eaten) == 1, "and what comes of eating it does not happen");
        await WaitIdle(e);

        await OwnFood(e, "cake", true);
        await OwnFood(e, "ration", false);
        await OwnFood(e, "cake-json", true);
        Spawn.Destroy(e);
    }

    private async Task OwnFood(EntityId e, string block, bool allowed)
    {
        var handle = Blocks.Find(block, Me)!;
        var data = Content.ItemData.Get(handle.GameName)!;
        await Items.Give(e, new ItemId(handle.Id), 2);
        await Steps(4);
        var slot = Items.Read(e)!.Slots.First(s => s.Item.Value == handle.Id);
        var sequence = Abilities.IdOf(data.Get("sequences[0].sequence").Guid);
        var mark = Blocks.Find("mark", Me)!;
        var eaten = ProgramOf(block, ".eaten");
        var ran = Ran(eaten);
        _buffs.Clear();
        int Count() => Items.Read(e)?.Count(new ItemId(handle.Id)) ?? -1;
        var before = Count();
        Check(PlayerStates.Diagnostics.LabUseItem(e, sequence, PlayerStateId.ActionPrimary, slot.Slot.Value, handle.Id), $"{block}: use it");
        await Steps(60);
        if (allowed)
        {
            Check(Count() == before - 1, $"{block}: its CanEat lets it through, and one is used: {before} -> {Count()}");
            Check(Ran(eaten) == ran + 1, $"{block}: OnEat ran once it had gone: {Ran(eaten) - ran}");
        }
        else
        {
            Check(Count() == before, $"{block}: CanEat refuses: it stays: {before} -> {Count()}");
            Check(Ran(eaten) == ran, $"{block}: and OnEat does not run");
            Check(_buffs.Any(b => b.Buff == mark.Guid && b.Target == e), $"{block}: OnRefused does");
        }
        await WaitIdle(e);
    }

    private async Task WaitIdle(EntityId e)
    {
        for (var i = 0; i < 200 && PlayerStates.Read(e)?.Action != null; i++) await Steps(1);
        await Steps(4);
    }
}
