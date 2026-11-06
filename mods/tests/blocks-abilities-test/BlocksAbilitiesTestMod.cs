// SPDX-License-Identifier: MIT
// Perk, ability, talent, delivery and trigger blocks, player-free on a dedicated server. A collision needs terrain streamed near a player, so a flight cannot land here: what it does on the way
// is run directly, and what needs a real client is said in the docs' manual test.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace BlocksAbilitiesTest;

public sealed partial class BlocksAbilitiesTestMod : Mod
{
    private const string Me = "blocks-abilities-test";
    private const uint ProbeTag = 0xB10E0101;
    private const string GameZone = "Zone_Player_Spell_IceBolt_1";
    private const string Mushroom = "Food_T1_raw_mushroom_Mushroom_brown";
    private const string Meteor = "Consumable_Scroll_Fog_Meteor_Shower";
    private const string Anchor = "0x8efcc472";
    private static readonly Guid Greet = new("13fd798d-033a-494b-95e8-76fe817202b5"), Glide = new("9c7cee31-58bc-4ae7-b963-c779a0f94dfc"), Sit = new("b5e65ebc-c1fc-4fef-a127-d133512fa701"), UseFood = new("90dc9374-acbf-4213-b01c-71ea7e9c6565");
    private static readonly Guid Mana = new("9f15218b-a7d4-4589-8de1-d5ab745bf51a"), Stamina = new("0a0eabad-6094-4ba2-8dc5-614d500d0a34");
    private readonly List<ImpactDamageEvent> _hits = new();
    private readonly List<ImpactBuffEvent> _buffs = new();
    private readonly List<EntityId> _spawned = new();
    private readonly Dictionary<uint, int> _started = new();
    private byte[]? _probe;
    private Guid _swing, _probeInt, _probeFloat;
    private WorldPosition _at;
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"blocks-abilities-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"blocks-abilities-test: PASS {what}"); }
        else { _fail++; Log.Error($"blocks-abilities-test: FAIL {what}"); }
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
    private static double N(GameDataEntry e, string path) => e.Get(path).Number;

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        Define();
        ReorderRows();
        HelperPrograms();
        Events.On<ImpactNativeEvent>(e =>
        {
            if (e is ImpactDamageEvent h) _hits.Add(h);
            if (e is ImpactBuffEvent b) _buffs.Add(b);
        });
        Events.On<ImpactStartedEvent>(e => _started[e.Impact.ProgramId] = _started.GetValueOrDefault(e.Impact.ProgramId) + 1);
        Events.On<ImpactSignalEvent>(e => { if (e.Tag == ProbeTag) _probe = e.Value; });
        _ = Run();
    }

    // A hit through the game's applyDamage, as a weapon's swing does it, and reads of an attribute: programs for the test itself.
    private void HelperPrograms()
    {
        var mod = Blocks.ModFor(Me, () => 0);
        var ctx = new BlockContext(mod, "test helpers");
        var low = new Lowered(mod);
        var swing = new ProgramBuilder("test.swing");
        var attacker = swing.V("attacker", ImpactType.Entity);
        var target = swing.V("target", ImpactType.Entity, default, Fnv("blocksabilities.Target"));
        var at = swing.V("at", ImpactType.Float3, default, Fnv("blocksabilities.At"));
        var damage = new byte[52];
        BitConverter.GetBytes(5f).CopyTo(damage, 0);
        swing.BeginCode();
        swing.Call("getRoot", null, attacker);
        swing.Pop();
        swing.Call("applyDamage", null, swing.V("blocked", ImpactType.Bool), attacker, target, at, swing.V("normal", ImpactType.Float3), swing.V("direction", ImpactType.Direction), swing.VFloat("attackMod", 1), swing.VFloat("stamina", 1),
            swing.VFloat("noise", 0), null, swing.VInt("gain", 0), swing.V("scale", ImpactType.Attribute), swing.VByte("push", ImpactType.Push, 1), swing.VU16("reaction", ImpactType.Reaction, 2), swing.V("damage", ImpactType.Damage, damage),
            swing.VByte("category", ImpactType.AttackMask, 1), swing.V("weapon", ImpactType.WeaponCategory), swing.V("material", ImpactType.Material), null, swing.VBool("canBlock", false), null, null, null, swing.VBool("repeat", true), null);
        swing.Pop();
        swing.Halt();
        swing.BeginShutdown();
        swing.Halt();
        (_swing, var swingBlob) = ctx.Build(swing);
        low.Program(_swing, "test.swing", swingBlob, "");
        (_probeInt, var intBlob) = ctx.Build(Probe(ImpactType.Sint32, "test.probe-int"));
        low.Program(_probeInt, "test.probe-int", intBlob, "");
        (_probeFloat, var floatBlob) = ctx.Build(Probe(ImpactType.Float, "test.probe-float"));
        low.Program(_probeFloat, "test.probe-float", floatBlob, "");
        low.Commit();
    }

    private static ProgramBuilder Probe(ImpactType type, string key)
    {
        var b = new ProgramBuilder(key);
        var target = b.V("target", ImpactType.Entity, default, Fnv("blocksabilities.Target"));
        var attr = b.V("attr", ImpactType.Attribute, default, Fnv("blocksabilities.Attr"));
        var value = b.V("value", type);
        b.BeginCode();
        b.Call("fetchOptionalAttribute", null, value, attr, target);
        b.Pop();
        b.Call("kiln.Signal", null, value, b.V("tag", ImpactType.Uint32, BitConverter.GetBytes(ProbeTag)));
        b.Pop();
        b.Halt();
        b.BeginShutdown();
        b.Halt();
        return b;
    }

    private Task<double?> Attr(string which, EntityId on)
    {
        var (guid, whole) = which switch
        {
            "mana" => (Mana, true),
            "stamina" => (Stamina, true),
            _ => (Stat.All.First(x => x.Key == which).Attribute, Stat.All.First(x => x.Key == which).Whole),
        };
        return Attr(guid, whole, on);
    }

    private async Task<double?> Attr(Guid guid, bool whole, EntityId on)
    {
        _probe = null;
        var e = await Abilities.RunProgram(whole ? _probeInt : _probeFloat, World.Position(on)!.Value, new ImpactOptions
        {
            Tag = "blocks-abilities-test",
            Configs = new ImpactConfigs { [Cfg("blocksabilities.Target")] = on, [Cfg("blocksabilities.Attr")] = guid.ToByteArray() },
        });
        await Steps(4);
        if (World.Exists(e)) Spawn.Destroy(e);
        if (_probe is not { Length: >= 4 } b) return null;
        return whole ? BitConverter.ToInt32(b) : BitConverter.ToSingle(b);
    }

    private async Task Swing(EntityId from, EntityId to)
    {
        var at = World.Position(to)!.Value;
        var he = await Abilities.RunProgram(_swing, at, new ImpactOptions
        {
            Root = from,
            Tag = "blocks-abilities-test",
            Configs = new ImpactConfigs
            {
                [Cfg("blocksabilities.Target")] = to,
                [Cfg("blocksabilities.At")] = new[] { (float)at.X, (float)at.Y, (float)at.Z }.SelectMany(BitConverter.GetBytes).ToArray(),
            },
        });
        await Steps(16);
        if (World.Exists(he)) Spawn.Destroy(he);
    }

    private async Task<EntityId> Make(string template, WorldPosition at, int settle = 10)
    {
        var e = await Spawn.Entity(template, at, 0, new SpawnOptions { Tag = "blocks-abilities-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(settle);
        return e;
    }

    private async Task<WorldPosition> FindPlace()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(inf.Position.X + 60, inf.Position.Y + 0.5, inf.Position.Z + 60);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }

    private static List<uint> BuffsOf(uint type)
    {
        var r = new List<uint>();
        foreach (var x in World.Find("BuffNetworkData"))
            if (World.ReadBytes(x, "BuffNetworkData") is { Length: >= 24 } b && BitConverter.ToUInt32(b, 0x10) == type) r.Add(x.Value);
        return r;
    }

    private static string Refused(Action define)
    {
        try { define(); return ""; }
        catch (BlockError e) { return e.Message; }
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(120);
            Info($"{Blocks.DumpPrograms(Path.Combine(Context.DataDirectory, "programs"))} generated program(s) written");
            _at = await FindPlace();
            Mistakes();
            await DataAndCode();
            Twins();
            Data();
            Ids();
            RowsBySync();
            await Perks();
            await AbilityCosts();
            await Deliveries();
            await Flights();
            await ItemTriggered();
            await TreeTalents();
            await GameTalents();
            await Triggers();
            await Eating();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"blocks-abilities-test done: {_pass} PASS, {_fail} FAIL");
    }

    // ---- tooltip rows and the content sync ------------------------------------------------------------------------

    private const uint RowStamina = 0xaa06e3ec, RowMana = 0xbe5b2600, RowCharge = 0x5c76837c;

    // A spell on the Fireball's item (rows: mana, charge time, radius) whose rows are stamina, mana, charge time: each config's row is at another
    // place than in the starter, which is what a mod's own list may do.
    private void ReorderRows()
    {
        static JsonObject Row(uint loca, string format, int value) => new() { ["locaId"] = $"0x{loca:x8}", ["valueFormat"] = format, ["value"] = value };
        BlockRuntime.AddRules(Blocks.ModFor(Me, () => 0), "items", $"{Me}.rows-reordered", new JsonObject
        {
            ["uiValues"] = new JsonArray(Row(RowStamina, "Normal", 7), Row(RowMana, "Normal", 5), Row(RowCharge, "Duration", 1000)),
        });
    }

    private static Dictionary<uint, int> RowsOf(GameDataEntry e)
    {
        var rows = new Dictionary<uint, int>();
        var n = e.GetJson("uiValues")?["value"] is JsonArray a ? a.Count : 0;
        for (var i = 0; i < n; i++) rows[(uint)N(e, $"uiValues[{i}].locaId")] = (int)N(e, $"uiValues[{i}].value");
        return rows;
    }

    // The content sync sends what a server changed and a client applies it (here: the server's own export applied on top of itself, and a vanilla
    // server matched). A tooltip row follows its config by the row's locaId, wherever the row is in the list.
    private void RowsBySync()
    {
        var item = Content.ItemData.Get($"{Me}.rows-reordered")!;
        string Say(Dictionary<uint, int> r) => string.Join(", ", r.Select(kv => $"0x{kv.Key:x8} {kv.Value}"));
        var before = RowsOf(item);
        Check(before.GetValueOrDefault(RowStamina) == 7 && before.GetValueOrDefault(RowMana) == 5 && before.GetValueOrDefault(RowCharge) == 1000 && (uint)N(item, "uiValues[0].locaId") == RowStamina,
            $"rows in another order than the starter's: {Say(before)}");
        var wand = Content.ItemData.Get($"{Me}.rows-wand")!;
        var wandBefore = RowsOf(wand);
        Check(wandBefore.GetValueOrDefault(RowMana) == 6 && wandBefore.GetValueOrDefault(0xc9d82dea) == 200, $"a wand's rows, which are not its starter's: {Say(wandBefore)}");
        var rt = ContentSync.CheckRoundTrip();
        var ids = new[] { item, wand }.Select(e => $"0x{e.Id:x8}").ToList();
        var mine = rt.Problems.Where(p => ids.Any(id => p.Contains(id, StringComparison.OrdinalIgnoreCase)) || p.Contains("rows-", StringComparison.Ordinal)).ToList();
        Check(mine.Count == 0, $"the content sync's round trip leaves each row its own value: {string.Join("; ", mine.Take(2))}");
        Check(rt.Ok, $"and the whole round trip is clean, every mod loaded: {string.Join("; ", rt.Problems.Take(2))}");
        bool Same(Dictionary<uint, int> a, Dictionary<uint, int> b) => a.Count == b.Count && a.All(kv => b.GetValueOrDefault(kv.Key) == kv.Value);
        var after = RowsOf(item);
        var wandAfter = RowsOf(wand);
        Check(Same(after, before) && Same(wandAfter, wandBefore), $"and after it the rows are as they were: {Say(after)}; the wand's {Say(wandAfter)}");
    }

    // ---- mistakes ----------------------------------------------------------------------------------------------

    private void Mistakes()
    {
        var (blocks, errors) = Blocks.Report()[Me];
        void Named(string block, params string[] words)
        {
            var e = errors.FirstOrDefault(x => x.Contains(block, StringComparison.Ordinal));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
        }
        Named("perk-nothing", "does nothing");
        Named("perk-bad-value", "value", "lots");
        Named("perk-typo", "modifes");
        Named("ability-nothing", "delivery", "does nothing");
        Named("ability-charges", "cast.charges", "no charges");
        Named("ability-bad-cone", "delivery.angle", "200");
        Named("ability-no-effects", "delivery.effects");
        Named("ability-unknown-delivery", "laser");
        Named("ability-bad-homing", "delivery.homing.turnSpeed");
        Named("ability-casts-nothing", "onCast[0].casts", "no-such-ability");
        Named("talent-no-anchor", "game.from");
        Named("talent-levels", "maxLevel", "9");
        Named("talent-class", "game.class", "Bard");
        Named("talent-tiers", "pointsSpent");
        Named("tree-no-nodes", "nodes");
        Named("tree-bad-require", "nodes[a].requires[0]", "nope");
        Named("tree-loop", "requires itself");
        Named("tree-game-node", "nodes[a].game");
        Named("tree-same-cell", "nodes[b].column");
        Named("trigger-nothing", "does nothing");
        Named("trigger-bad-state", "on", "NoSuchState");
        Named("trigger-stop-state", "stop", "state");
        Named("trigger-end-action", "onEnd");
        Named("trigger-stop-eaten", "stop");
        Named("status-bad-while", "while", "NoSuchState");
        Named("status-no-time", "seconds");
        Named("item-eat-material", "onEat");
        Named("item-empty-rule", "canEat");
        string[] wrong = ["perk-nothing", "perk-bad-value", "perk-typo", "ability-nothing", "ability-charges", "ability-bad-cone", "ability-no-effects", "ability-unknown-delivery", "ability-bad-homing", "ability-casts-nothing", "talent-no-anchor", "talent-levels", "talent-class", "talent-tiers",
            "tree-no-nodes", "tree-bad-require", "tree-loop", "tree-game-node", "tree-same-cell", "trigger-nothing", "trigger-bad-state", "trigger-stop-state", "trigger-end-action", "trigger-stop-eaten", "status-bad-while", "status-no-time", "item-eat-material", "item-empty-rule"];
        var made = wrong.Where(blocks.Contains).ToList();
        Check(made.Count == 0, $"none of the {wrong.Length} wrong blocks became anything: {string.Join(", ", made)}");
        var cs = Refused(() => Blocks.Define("cs-bad-perk", new PerkBlock { Name = "x", Value = "lots", Modifies = { [Stat.Strength] = 1 } }));
        Check(cs.Contains("perk cs-bad-perk", StringComparison.Ordinal) && cs.Contains("value", StringComparison.Ordinal), $"C#: a wrong perk throws with the block and the field: \"{cs}\"");
        cs = Refused(() => Blocks.Define("cs-bad-trigger", new TriggerBlock { On = "NoSuchState", Do = [new EffectBlock { Heal = 1 }] }));
        Check(cs.Contains("trigger cs-bad-trigger", StringComparison.Ordinal) && cs.Contains("NoSuchState", StringComparison.Ordinal), $"C#: and a wrong trigger: \"{cs}\"");
        var meteor = errors.FirstOrDefault(x => x.Contains(Meteor, StringComparison.Ordinal));
        Check(meteor != null && meteor.Contains("stop-meteor", StringComparison.Ordinal) && meteor.Contains("first moment", StringComparison.Ordinal), $"a rule on the scroll the game uses up at once is refused by name: \"{meteor}\"");
    }

    // A mod's code and its data files name each other's blocks, whichever is defined first: the ids are the same rule either way.
    private async Task DataAndCode()
    {
        var (_, errors) = Blocks.Report()[Me];
        foreach (var name in new[] { "code-names-data", "data-names-code" })
            Check(Blocks.Find(name, Me) != null && !errors.Any(e => e.Contains(name, StringComparison.Ordinal)), $"{name} is defined, with no error: {errors.FirstOrDefault(e => e.Contains(name, StringComparison.Ordinal))}");
        Check(Blocks.Find("mark-json", Me)?.Guid == BlockIds.Derive(Me, "buff", "mark-json") && Blocks.Find("burst-json", Me)?.Guid == BlockIds.Derive(Me, "effect", "burst-json"),
            "a data block compiled because code named it keeps the id it always has");
        var caster = await Character(-60, 0);
        var target = await Enemy(-58, 0);
        Combat.SetInvulnerable(target, true);
        async Task<(bool Hit, bool Marked)> Apply(string status, string mark)
        {
            _hits.Clear();
            _buffs.Clear();
            await Abilities.ApplyBuff(target, Blocks.Find(status, Me)!.Guid, source: caster);
            await Steps(12);
            return (_hits.Any(h => h.Target == target && Math.Abs(h.Damage.Ice - 11) < 1e-3), _buffs.Any(b => b.Target == target && b.Buff == Blocks.Find(mark, Me)!.Guid));
        }
        var codeNamesData = await Apply("code-names-data", "mark-json");
        Check(codeNamesData == (true, true), $"a code block naming the data files' effect and status, during OnLoad: both run {codeNamesData}");
        var dataNamesCode = await Apply("data-names-code", "mark");
        Check(dataNamesCode == (true, true), $"and a data block naming the code's: {dataNamesCode}");
        Spawn.Destroy(target);
        Spawn.Destroy(caster);
    }

    // JSON and C# are the same objects: what they lower to differs only in names and the ids that follow from names.
    private void Twins()
    {
        static string Normal(string? json) => Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID").Replace("-json", "").Replace("\r", "");
        foreach (var name in new[] { "mark", "glide-buff", "perk-hit", "perk-might", "perk-sword", "mend", "nova", "cone", "beam", "zone", "chain", "summon", "bolt", "arts", "ward", "on-sit", "cake" })
        {
            var cs = Normal(Blocks.Lowered(name, Me));
            var json = Normal(Blocks.Lowered($"{name}-json", Me));
            Check(cs.Length > 10 && cs == json, $"blocks/twins.json lowers to what the C# {name} does ({cs.Length} characters)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
    }

    // ---- what they became --------------------------------------------------------------------------------------

    private void Data()
    {
        var perk = Content.Perks.Get($"{Me}.perk-hit");
        Check(perk != null && perk.Guid == BlockIds.Derive(Me, "perk", "perk-hit"), $"the perk is a perk of its own, its id from the mod and the name: {perk?.Guid}");
        Check(Abilities.Programs.Info(BlockIds.Fnv(perk!.Get("impact").Guid)) != null, $"it runs its generated program {perk.Get("impact").Guid}");
        Check(perk.GetJson("uiValues")?["value"] is JsonArray { Count: 1 } && N(perk, "uiValues[0].value") == 5 && perk.Get("uiValues[0].valueFormat").ToString() == "Percentage", $"Value \"5%\": its tooltip row says 5, as a percentage: {perk.GetJson("uiValues")?["value"]?.ToJsonString()}");
        var might = Content.Perks.Get($"{Me}.perk-might")!;
        Check(N(might, "uiValues[0].value") == 5 && might.Get("uiValues[0].valueFormat").ToString() == "Normal", "with no Value, one whole stat gives the row: +5");
        var swift = Content.Perks.Get($"{Me}.perk-swift")!;
        Check(N(swift, "uiValues[0].value") == 10 && swift.Get("uiValues[0].valueFormat").ToString() == "Percentage", "one share gives it as a percentage: +10%");
        var chance = Content.Perks.Get($"{Me}.perk-chance")!;
        Check(N(chance, "uiValues[0].value") == 30, "and a single on-hit effect's chance: 30%");
        var quiet = Content.Perks.Get($"{Me}.perk-quiet")!;
        Check(quiet.GetJson("uiValues")?["value"] is JsonArray { Count: 0 }, "a perk with no number shows no row of the starter's");
        Check(Content.Perks.Get($"{Me}.perk-icon")?.GetJson("icon")?["value"]?.ToJsonString() == Content.Perks.Get("Perk_Damage_Ice")?.GetJson("icon")?["value"]?.ToJsonString(), "Icon: a perk takes a game perk's icon");
        var sword = Content.ItemData.Get($"{Me}.perk-sword")!;
        Check(sword.Get("perkReferences[0]").Guid == perk.Guid, $"the sword's first enhancement is the perk: {sword.Get("perkReferences[0]").Guid}");
        Check((uint)N(sword, "perkIds[0]") == BlockIds.Fnv(perk.Guid), "and its id follows");

        var spell = Content.ItemData.Get($"{Me}.stamina-spell")!;
        Check(N(spell, "impactValues.ManaCost") == 5 && N(spell, "impactValues.StaminaCost") == 7, $"a spell's costs: mana {N(spell, "impactValues.ManaCost")}, stamina {N(spell, "impactValues.StaminaCost")}");
        Check(N(spell, "uiValues[0].value") == 5 && N(spell, "uiValues[2].value") == 7 && (uint)N(spell, "uiValues[2].locaId") == 0xaa06e3ec, "and the tooltip's rows say both, stamina by the game's own row, after the starter's");
        Check(Content.Sequences.Get($"{Me}.free-spell.cast")?.Get("events[PayUsageCost#1].consume").Bool == false, "Charges = 0: the cast's consume is off");
        Check(Content.Sequences.Get($"{Me}.stamina-spell.cast") == null, "Charges > 0 alone needs no copy of the motion");

        var coneSpell = Content.ItemData.Get($"{Me}.cone-spell")!;
        var placed = Content.Templates.Get($"{Me}.cone-spell.delivery");
        Check(placed != null && coneSpell.Get("equipment.placedEntity").Guid == placed.Guid, "a spell with a cone places the cone: its item's placedEntity");
        Check(N(coneSpell, "equipment.placedEntityLocalSpawnVelocity.z") == 0, "at no speed");
        Check(Abilities.Programs.Info(BlockIds.Fnv(placed!.Get("Impact.program").Guid)) != null, "and it runs its generated program");

        var homing = Content.Templates.Get($"{Me}.homing-spell.projectile")!;
        Check(Math.Abs(N(homing, "HomeToTarget.turnSpeed") - 40) < 1e-3 && Math.Abs(N(homing, "HomeToTarget.rayLength") - 15) < 1e-3, $"Homing: the game's own component: turn speed {N(homing, "HomeToTarget.turnSpeed")}, reach {N(homing, "HomeToTarget.rayLength")}");
        Check(N(homing, "RandomOrientation.randomOrientationOffset.minX") == 0 && N(homing, "RandomOrientation.randomOrientationOffset.maxY") == 0, "with none of the starter's random aim");
        var look = Content.Templates.Get("Projectile_Player_Staff_IceBolt_1")!;
        Check(homing.Get("VfxComponentResource.vfx").Guid == look.Get("VfxComponentResource.vfx").Guid, $"it looks like its Look: {homing.Get("VfxComponentResource.vfx").Guid}");
        Check(homing.Get("ImpactConfiguration.configValues.0xf6a785cd").Guid == Content.Templates.Get($"{Me}.homing-spell.impact")?.Guid, "and its impact is added to what the starter had");
        Check(homing.Get("Impact.program").Guid != Content.Templates.Get("Projectile_Player_Staff_Bone_Channel_Homing")!.Get("Impact.program").Guid, "and it runs a program of ours, which spawns it");

        var ids = Abilities.Registered.Select(a => a.Id).ToHashSet();
        foreach (var n in new[] { "mend", "nova", "cone", "beam", "zone", "chain", "summon", "bolt", "mend-json", "nova-json", "scroll", "charm", "gated" }) Check(ids.Contains($"{Me}:{n}"), $"the ability {Me}:{n} is registered");
        var mend = Abilities.Registered.First(a => a.Id == $"{Me}:mend");
        Check(mend.Cost == new AbilityCost(10, 5, 0) && mend.Cooldown == TimeSpan.FromSeconds(5) && mend.ChatCommand == "mend", $"with its costs ({mend.Cost}), cooldown and command");
        var tree = Skills.Find($"{Me}:arts");
        Check(tree is { Nodes.Count: 3 }, $"the tree is registered, {tree?.Nodes.Count} nodes, points {tree?.Points}");
        TalentNode? Node(string id) => tree?.Nodes.FirstOrDefault(n => n.Id == id);
        Check(Node("kindle") is { MaxLevel: 3, Column: 0, Row: 0 } && Node("hearth") is { Row: 1 } && Node("gate") is { Row: 2, Cost: 2 }, "laid out from what leads where");
        Check(Node("kindle")?.Description?.ToString() == "+2 Strength, +10% Move Speed", $"with no description, it says what it does: \"{Node("kindle")?.Description}\"");
        Check(GameTree.Definitions.Any(d => d.Name == $"{Me}.ward"), $"the game-tree node is defined: {GameTree.Definitions.FirstOrDefault(d => d.Name == $"{Me}.ward")?.State}");
        var report = Path.Combine(Path.GetDirectoryName(Context.DataDirectory)!, "blocks", $"{Me}.json");
        Check(File.Exists(report) && File.ReadAllText(report).Contains("\"ability\"", StringComparison.Ordinal), "kiln blocks can read what they became");
    }

    private void Ids()
    {
        var names = new[] { "perk-hit", "mend", "nova", "arts", "ward", "on-sit", "glide-buff", "cake" };
        var now = new JsonObject();
        foreach (var n in names) now[n] = Blocks.Find(n, Me)!.Guid.ToString();
        now["perk-program"] = Content.Perks.Get($"{Me}.perk-hit")!.Get("impact").Guid.ToString();
        now["delivery"] = Content.Templates.Get($"{Me}.nova.delivery")!.Guid.ToString();
        var node = GameTree.Definitions.First(d => d.Name == $"{Me}.ward");
        now["node"] = node.Id.ToString("x8");
        var file = Path.Combine(Context.DataDirectory, "ids.json");
        if (File.Exists(file))
        {
            var was = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var (k, v) in now) Check(was[k]?.ToString() == v?.ToString(), $"after the restart {k} is still {v}");
            File.Delete(file);
            return;
        }
        File.WriteAllText(file, now.ToJsonString());
        Info("ids written: restart the lab for the second phase");
    }
}
