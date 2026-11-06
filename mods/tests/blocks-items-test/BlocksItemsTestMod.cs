// SPDX-License-Identifier: MIT
// What Kiln.Blocks makes of mods/samples/ItemBlocks, with no player, on a dedicated server: registration, the crafted-item rule through the recipe path,
// stats and tooltip rows, C# and JSON the same, ids and stored items across a restart, uses and wand attacks through their sequences on a player-template
// entity, hits through the game's hit stream, armor on the wearer.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace BlocksItemsTest;

public sealed class BlocksItemsTestMod : Mod
{
    private const string Sample = "item-blocks";
    private static readonly Guid Swing = Guid.Parse("3c5d9c52-8a7e-4f11-b9b0-2f1e0a6d7c11");
    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();
    private readonly List<ImpactDamageEvent> _hits = new();
    private readonly List<ImpactBuffEvent> _buffs = new();
    private WorldPosition _at;
    private EntityId _chest;

    private void Info(string s) => Log.Info($"blocks-items-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"blocks-items-test: PASS {what}"); }
        else { _fail++; Log.Error($"blocks-items-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static string G(string block) => $"{Sample}.{block}";
    private static uint Fnv(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var c in System.Text.Encoding.UTF8.GetBytes(s)) h = (h ^ c) * 0x1000193u;
        return h;
    }
    private static string Cfg(string name) => $"0x{Fnv(name):x8}";
    private static double N(GameDataEntry e, string path) => e.Get(path).Number;
    private static GameDataEntry D(string block) => Content.ItemData.Get(G(block)) ?? throw new InvalidOperationException($"no item {block}");
    private static ContentEntry I(string block) => Content.Items.Get(G(block)) ?? throw new InvalidOperationException($"no item {block}");

    // the game's damage power (0x91e9d0): docs/re/abilities.md §2
    private static int Power(GameDataEntry d, int level) => (int)((float)(int)(10 * 0.15f * level + 10) * (float)N(d, "damageSetup.dmgMod") * (float)N(d, "damageSetup.speedMod"));
    // the game's armour: docs/kiln/modding/item-blocks.md
    private static int Armor(GameDataEntry d, string kind, int level) => (int)((15f * level + 23f) * (float)N(d, $"armorSetup.distribution.{kind}") + 0.5f);

    private static List<(uint Loca, int Value)> Rows(GameDataEntry d)
    {
        var r = new List<(uint, int)>();
        for (var i = 0; i < 12; i++)
        {
            try { r.Add(((uint)N(d, $"uiValues[{i}].locaId"), (int)N(d, $"uiValues[{i}].value"))); }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { break; }
        }
        return r;
    }

    private static string RowText(GameDataEntry d) => string.Join(", ", Rows(d).Select(r => $"0x{r.Loca:x8}={r.Value}"));

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        // another mod's blocks, by name: a look, and an item in a recipe
        Blocks.Define("borrowed", new ItemBlock
        {
            Name = "Borrowed", Visual = $"{Sample}:ember-look",
            Recipe = new RecipeBlock { Station = Stations.Hand, Needs = { [$"{Sample}:ember-shard"] = 1 } },
        });
        foreach (var k in Kinds_) Blocks.Define($"station-{k.Key}", new StationBlock { Name = k.Key, Kind = k });
        foreach (var (name, at) in new[] { ("late-wand", 0.5), ("early-wand", 0.2) })
            Blocks.Define(name, new WandBlock
            {
                Name = name,
                Primary = new WandAttack
                {
                    Cast = new CastBlock { ManaCost = 1, ReleaseAt = at },
                    Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 1, Effects = [new EffectBlock { Damage = Damage.Ice(5) }] } }] },
                },
            });
        Events.On<ImpactNativeEvent>(e =>
        {
            if (e is ImpactDamageEvent h) _hits.Add(h);
            if (e is ImpactBuffEvent b) _buffs.Add(b);
        });
        // What a placeable item places, the moment content is ready: its own template already, not the one of the game item it starts from until a
        // later retry finds the template (a world registers the templates after the items)
        Content.OnReady(e =>
        {
            if (e.Reload || _placedAtReady.Count > 0) return;
            foreach (var item in PlacedItems) _placedAtReady[item] = Content.Items.Get(item)?.GetRef("equipment.placedEntity");
        });
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(150);
            var dump = Path.Combine(Context.DataDirectory, "programs");
            Info($"{Blocks.DumpPrograms(dump)} generated program(s) written to {dump}");
            FindChest();
            _at = await Anchor();
            await Part("mistakes", () => { Errors(); return Task.CompletedTask; });
            await Part("registered", () => { Registered(); return Task.CompletedTask; });
            await Part("weapons", () => { Weapons(); return Task.CompletedTask; });
            await Part("details", () => { Details(); return Task.CompletedTask; });
            await Part("kinds", () => { Kinds(); return Task.CompletedTask; });
            await Part("untouched", () => { Untouched(); return Task.CompletedTask; });
            await Part("wand data", () => { Wand(); return Task.CompletedTask; });
            await Part("armor data", () => { ArmorData(); return Task.CompletedTask; });
            await Part("station", () => { Station(); return Task.CompletedTask; });
            await Part("loot", () => { Loot(); return Task.CompletedTask; });
            await Part("twins", () => { Twin(); return Task.CompletedTask; });
            await Part("ids", Ids);
            await Part("crafted", Crafted);
            await Part("station in the world", Stations_);
            await Part("rolled", Rolled);
            await Part("uses", Uses);
            await Part("wand casts", WandCasts);
            await Part("hits", Hits);
            await Part("worn", Worn);
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Log.Info($"blocks-items-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Part(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name}: exception: {e}"); }
    }

    private void FindChest()
    {
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 4 && (!EntityData.Available || EntityData.IsPersistent(e)))
            {
                _chest = e;
                break;
            }
        Check(!_chest.IsNone, $"a saved container to work with: {_chest}");
    }

    // ---- mistakes ----------------------------------------------------------------------------------------------

    private void Errors()
    {
        var report = Blocks.Report();
        var mine = report.GetValueOrDefault("blocks-items-test");
        void Named(string block, params string[] words)
        {
            var e = mine.Errors?.FirstOrDefault(x => Regex.IsMatch(x, $@"\b{Regex.Escape(block)}\b"));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
        }
        Named("no-name", "name");
        Named("typo-field", "stacks");
        Named("heal-material", "heals", "material");
        Named("big-stack", "stack", "65535");
        Named("tool-stack", "stack", "does not stack");
        Named("tool-rare", "rarity", "slots");
        Named("bad-count", "recipe.needs", "less than 1");
        Named("no-needs", "recipe.needs");
        Named("no-such-look", "visual", "gone-look");
        Named("wrong-look", "visual", "an-effect", "effect");
        Named("png-icon", "visual.icon", "mod");
        Named("glb-model", "visual.model", "cook");
        Named("does-nothing", "onUse");
        Named("food-nothing", "feeds");
        Named("no-station", "recipe.station", "anvil");
        Named("loot-nowhere", "loot");
        Named("bad-chance", "loot.tables[0].chance");
        Named("two-tables", "loot.tables[0]");
        Named("bad-class", "class", "spear", "sword");
        Named("wand-class", "class", "wand block");
        Named("rolled-recipe", "loot.rolled", "KL406");
        Named("high-level", "stats.level", "99");
        Named("no-vfx", "visual.vfx");
        Named("no-primary", "primary");
        Named("two-powers", "stats.power", "99");
        Named("staff-moves", "primary.cast.animation");
        Named("no-slot", "slot");
        Named("bad-slot", "slot", "neck", "chest");
        Named("half-health", "bonus", "whole");
        Named("bad-bonus", "bonus", "luck");
        Named("bad-kind", "kind", "anvil", "workbench");
        string[] broken = ["no-name", "typo-field", "heal-material", "big-stack", "tool-stack", "tool-rare", "bad-count", "no-needs", "no-such-look", "wrong-look", "png-icon", "glb-model", "does-nothing", "food-nothing", "no-station", "loot-nowhere", "bad-chance", "two-tables", "bad-class", "wand-class", "rolled-recipe", "high-level", "no-vfx", "no-primary", "two-powers", "staff-moves", "no-slot", "bad-slot", "half-health", "bad-bonus", "bad-kind"];
        var made = (mine.Blocks ?? []).Intersect(broken).ToList();
        Check(made.Count == 0 && Content.Items.Get("blocks-items-test.big-stack") == null, $"none of the broken blocks became anything ({string.Join(", ", made)})");

        var thrown = "";
        try { Blocks.Define("bad", new WeaponBlock { Name = "Bad", Class = WeaponClass.Sword, Stats = new WeaponStats { Level = 99 } }); }
        catch (BlockError e) { thrown = e.Message; }
        Check(thrown.Contains("weapon bad", StringComparison.Ordinal) && thrown.Contains("stats.level", StringComparison.Ordinal), $"C#: a wrong value throws with the block and the field: \"{thrown}\"");
        var other = "";
        try { Blocks.Define("bad2", new ArmorBlock { Name = "Bad", Slot = "neck" }); }
        catch (BlockError e) { other = e.Message; }
        Check(other.Contains("armor bad2", StringComparison.Ordinal) && other.Contains("slot", StringComparison.Ordinal), $"C#: a part by a name that is nothing: \"{other}\"");
    }

    // ---- it is all there ----------------------------------------------------------------------------------------

    private static readonly string[] AllItems =
    [
        "ember-shard", "healing-draught", "ember-tonic", "ember-stew", "ember-arrow", "ember-axe", "ember-pick", "ember-gem",
        "sword", "waraxe", "mace", "greatsword", "greataxe", "hammer", "daggers", "bow", "staff", "ember-guard", "ember-edge", "ember-wand",
        "ember-helm", "ember-mail", "ember-band", "ember-bench", "ember-relic",
    ];

    private void Registered()
    {
        var report = Blocks.Report().GetValueOrDefault(Sample);
        Check(report.Errors is { Count: 0 }, $"the sample's blocks came through with no word from the services: [{string.Join(" | ", report.Errors ?? [])}]");
        foreach (var n in AllItems)
        {
            var h = Blocks.Find(n, Sample);
            var e = Content.Items.Get(G(n));
            Check(h != null && e != null && e.Guid == BlockIds.Derive(Sample, "item", n) && e.Id == h.Id, $"{n}: item {e?.Name}, id 0x{e?.Id:x8}, GUID by the mod id + name rule");
        }
        var defs = NewContent.Definitions().Where(d => d.Name.StartsWith(Sample + ".", StringComparison.Ordinal)).ToList();
        Check(defs.All(d => d.Worlds.Contains("server")), $"{defs.Count} definitions in the server world's registries: not in [{string.Join(", ", defs.Where(d => !d.Worlds.Contains("server")).Select(d => $"{d.Name} {d.State} {d.Problem}"))}]");
        var parts = new[] { "ember-look", "veteran", "searing", "ember-blade", "embersteel" };
        Check(parts.All(p => Blocks.Find(p, Sample) != null), "the parts are blocks too: a look, stats, an on-hit, a class, armor stats");
        var clients = File.ReadAllText(Path.Combine(Mods.Get(Sample)!.Directory, "kiln.mod.json"));
        Check(clients.Contains("\"both\"", StringComparison.Ordinal) && clients.Contains("\"kiln\"", StringComparison.Ordinal), "the SDK made the manifest side both, clients kiln, from KilnServices alone");
    }

    // what the rest of the checks don't reach
    private void Details()
    {
        var charm = Content.Items.Get(G("ember-charm"));
        Check(charm?.Guid == BlockIds.Derive(Sample, "item", "ember-token"), $"a rename: ids come from the oldest name: {charm?.Guid}");
        Check(charm?.Get("comfortSetup.comfortAmount").Integer == 2 && charm.Get("comfortSetup.isSet").Integer != 0, $"comfort {charm?.Get("comfortSetup.comfortAmount").Integer}");

        // Texts: what a starter says is not the block's, and what a base says about where it is found is not true of a clone.
        var flint = Content.Items.Get("Ammo_T1_Arrow_Flint_lvl5")!;
        const string Hint = "acquisitionHint", Told = "description";
        var plainArrow = I("ember-arrow");
        Check(flint.GetRef(Hint) != Guid.Empty && plainArrow.GetRef(Hint) == Guid.Empty, $"a block with no Source says nothing about where to get it (its starter does: {flint.GetRef(Hint)})");
        Check(flint.GetRef(Told) != Guid.Empty && plainArrow.GetRef(Told) == Guid.Empty, $"a block with no Description has none (its starter's is {flint.GetRef(Told)})");
        Check(I("ember-gem").GetRef(Hint) != Guid.Empty && I("ember-gem-json").GetRef(Hint) != Guid.Empty, "Source sets it, in C# and in JSON");
        Check(I("ember-edge").GetRef(Told) != Guid.Empty && I("ember-shard").GetRef(Told) != Guid.Empty, "and Description sets its text");
        var resin = Content.Items.Get("Material_T1_Resin")!;
        const string CloneNoText = "BlocksItems_Resin_Clone", CloneSource = "BlocksItems_Resin_Source";
        // defined every time: after a restart the saved ones are placeholders until their definition comes again
        NewContent.RegisterJson(ContentKind.Items, "{\"guid\":\"5d0a7f13-2c84-4e69-b1a7-93c1e8f6d201\",\"name\":\"" + CloneNoText + "\",\"base\":\"Material_T1_Resin\"}");
        NewContent.RegisterJson(ContentKind.Items, "{\"guid\":\"5d0a7f13-2c84-4e69-b1a7-93c1e8f6d202\",\"name\":\"" + CloneSource + "\",\"base\":\"Material_T1_Resin\",\"source\":\"Sold at the docks\",\"description\":\"\"}");
        var cloneNoText = Content.Items.Get(CloneNoText)!;
        var cloneSource = Content.Items.Get(CloneSource)!;
        Check(resin.GetRef(Hint) != Guid.Empty && cloneNoText.GetRef(Hint) == Guid.Empty, $"a clone does not keep its base's source text (resin's is {resin.GetRef(Hint)})");
        Check(cloneNoText.GetRef(Told) == resin.GetRef(Told) && resin.GetRef(Told) != Guid.Empty, "and keeps its base's description unless it says otherwise");
        Check(cloneSource.GetRef(Hint) != Guid.Empty && cloneSource.GetRef(Hint) != resin.GetRef(Hint) && cloneSource.GetRef(Told) == Guid.Empty, "\"source\" gives a clone its own, and an empty \"description\" gives it none");
        var gem = I("ember-gem");
        var scraps = Content.Items.Get("Material_T1_Metal_Scraps")!;
        Check(gem.GetRef("iconModel") == scraps.GetRef("iconModel"), $"the look's Dropped: the model lying on the ground: {gem.GetRef("iconModel")}");
        Check((uint)gem.Get("itemColorCombinationSetup.color0").Integer == 0xffff6600 && gem.Get("itemColorCombinationSetup.isSet").Integer != 0, $"the look's Tint: 0x{(uint)gem.Get("itemColorCombinationSetup.color0").Integer:x8}");
        var def = NewContent.Definitions().First(d => d.Name == G("ember-gem"));
        Check(def.DefinitionJson is { } j && j.Contains("Glutstein", StringComparison.Ordinal), "Texts per language reach the definition");

        var relic = D("ember-relic");
        Check(relic.Get("generateRarity").Number != 0 && N(relic, "itemLevelRange.maxLevel") > N(relic, "itemLevelRange.minLevel"), $"a rolled item keeps what a roll needs: generateRarity on, levels {N(relic, "itemLevelRange.minLevel")}..{N(relic, "itemLevelRange.maxLevel")}");
        Check(Content.Recipes.Get(G("ember-relic.recipe")) == null && Content.Loot.Get(G("ember-relic")) != null, "and has no recipe, and is in the rolls");

        var borrowed = Content.Items.Get("blocks-items-test.borrowed");
        var model = Content.Items.Get("Weapon_T4_1H_SwordEpic_02")!;
        Check(borrowed?.GetRef("iconModel") == model.GetRef("iconModel"), "another mod's look, by \"mod:name\"");
        var recipe = Content.Recipes.Get("blocks-items-test.borrowed.recipe");
        Check(recipe?.Inputs.Any(i => i.Item.Value == I("ember-shard").Id) == true, $"another mod's item in a recipe, by \"mod:name\": {string.Join(", ", recipe?.Inputs ?? [])}");

        var wand = D("ember-wand");
        var special = Content.Sequences.Get(G("ember-wand.special"))!;
        Check(wand.Get("sequences[1].sequence").Guid == special.Guid, "wand: Special goes to its own");
        var shots = Enumerable.Range(0, 5).Select(i => (Template: special.Get($"events[SpawnEntityEvent#{i}].templateReference").Guid, Time: N(special, $"events[SpawnEntityEvent#{i}].time"), Chance: N(special, $"events[SpawnEntityEvent#{i}].chance"))).ToList();
        var mine = Content.Templates.Get(G("ember-wand.special.projectile"))!.Guid;
        Check(shots[0].Template == mine && shots[0].Chance > 0 && shots.All(x => Math.Abs(x.Time - 0.4) < 1e-3), $"wand: ReleaseAt moves its shot, and the events keep their order: [{string.Join("; ", shots.Select(x => $"{x.Time:0.###} s chance {x.Chance}"))}]");
        Check(shots.Count(x => x.Chance == 0) == 4, "wand: the special's other elements' shots are silenced");

        // ReleaseAt on a motion with two shots: a time change re-sorts the events
        var late = Content.Sequences.Get("blocks-items-test.late-wand.primary")!;
        var flight = Content.Templates.Get("blocks-items-test.late-wand.primary.projectile")!.Guid;
        var both = Enumerable.Range(0, 2).Select(i => (Template: late.Get($"events[SpawnEntityEvent#{i}].templateReference").Guid, Time: N(late, $"events[SpawnEntityEvent#{i}].time"))).ToList();
        Check(both.All(x => x.Template == flight && Math.Abs(x.Time - 0.5) < 1e-3), $"wand: ReleaseAt moves both shots of the bolt: [{string.Join("; ", both.Select(x => $"{x.Time:0.###} s"))}]");
        var early = Content.Sequences.Get("blocks-items-test.early-wand.primary")!;
        var flightEarly = Content.Templates.Get("blocks-items-test.early-wand.primary.projectile")!.Guid;
        var bothEarly = Enumerable.Range(0, 2).Select(i => (Template: early.Get($"events[SpawnEntityEvent#{i}].templateReference").Guid, Time: N(early, $"events[SpawnEntityEvent#{i}].time"))).ToList();
        Check(bothEarly.All(x => x.Template == flightEarly && Math.Abs(x.Time - 0.2) < 1e-3), $"wand: and earlier too: [{string.Join("; ", bothEarly.Select(x => $"{x.Time:0.###} s"))}]");
    }

    private void Weapons()
    {
        // what the game's own class has: how it is held and what skills count it
        (string Block, string Like)[] classes =
        [
            ("sword", "Weapon_T1_1H_Sword_Craft"), ("waraxe", "Weapon_T1_1H_Axe_01"), ("mace", "Weapon_T1_1H_Mace_Craft"), ("greatsword", "Weapon_T1_2H_GreatSword_01"),
            ("greataxe", "Weapon_T1_2H_GreatAxe_01"), ("hammer", "Weapon_T1_2H_Hammer_01"), ("daggers", "Weapon_T1_2H_Daggers_01_DPS"), ("bow", "Weapon_T1_2H_Bow_01"), ("staff", "Weapon_T1_2H_Staff_Craft"),
        ];
        foreach (var (block, like) in classes)
        {
            var d = D(block);
            var it = I(block);
            var game = Content.Items.Get(like)!;
            var ok = N(d, "itemLevelRange.minLevel") == 5 && N(d, "itemLevelRange.maxLevel") == 5 && d.Get("generateRarity").Number == 0 && it.Get("rarity").Name == "Rare" && Power(d, 5) == 30;
            Check(ok, $"{block}: level 5, one level, generateRarity off, Rare, tooltip damage {Power(d, 5)}");
            Check(it.GetRef("weaponCategoryReference") == game.GetRef("weaponCategoryReference") && it.Get("equipment.primaryAnimationSet").Name == game.Get("equipment.primaryAnimationSet").Name,
                $"{block}: held and counted as its class: {it.Get("equipment.primaryAnimationSet").Name}, category {it.GetRef("weaponCategoryReference")}");
            Check(N(d, "perkIds[2]") != 0, $"{block}: the slot Rare needs is filled, or it would come out a rank lower: perkIds[2] 0x{(uint)N(d, "perkIds[2]"):x8}");
        }
        var guard = D("ember-guard");
        Check(I("ember-guard").Get("equipment.slot").Name == "Shield" && Math.Abs(N(guard, "blockSetup.quality") - 0.3) < 1e-5, $"shield: slot Shield, block {N(guard, "blockSetup.quality"):0.##}");

        // the parts at work: the class brought the look and the stats, the block brought its perk
        var edge = D("ember-edge");
        Check(N(edge, "itemLevelRange.minLevel") == 10 && Power(edge, 10) == 60 && I("ember-edge").Get("rarity").Name == "Rare", $"ember-edge: stats from its class's part: level {N(edge, "itemLevelRange.minLevel")}, damage {Power(edge, 10)}");
        var model = Content.Items.Get("Weapon_T4_1H_SwordEpic_02")!;
        Check(I("ember-edge").GetRef("equipment.visualEntity") == model.GetRef("equipment.visualEntity") && I("ember-edge").GetRef("iconModel") == model.GetRef("iconModel"), "ember-edge: the look from its class's part: the T4 sword's held model and icon model");
        var fire = Content.Perks.Get("Perk_Damage_Fire");
        Check((uint)N(edge, "perkIds[0]") == fire?.Id, $"ember-edge: perk slot 0 is Perk_Damage_Fire: 0x{(uint)N(edge, "perkIds[0]"):x8}");
        var low = JsonNode.Parse(Blocks.Lowered("ember-edge", Sample)!)!;
        Check(low["onHitProgram"] != null && Abilities.Programs.Info(BlockIds.Fnv(Guid.Parse((string)low["onHitProgram"]!))) is not null, "ember-edge: its OnHit and OnKill program is verified and registered");
    }

    // A clone's lists are its own: a rule on one is not a rule on the game's item or another clone (native: gd_own_lists).
    private void Untouched()
    {
        var potion = Content.ItemData.Get("Consumable_Potion_Health_T1")!;
        Check(N(potion, "impactValues.HealthChange") == 200 && (uint)N(potion, "uiValues[0].value") == 200, $"the game's Health potion still heals 200: {N(potion, "impactValues.HealthChange")}");
        var wand = Content.ItemData.Get("Weapon_T1_1H_Wand_02_Ice")!;
        var game = Content.Sequences.Get("Attack_Player_Wand_IceChain_normal")!;
        Check(wand.Get("sequences[0].sequence").Guid == game.Guid, $"the game's wand still casts its own: {wand.Get("sequences[0].sequence").Guid}");
        Check(N(wand, "impactValues.0x4e133b42") == 4 && Rows(wand).Count == 3, $"and keeps its rows: {RowText(wand)}");
        var fur = Content.ItemData.Get("Armor_T0_Fur_Chest")!;
        Check(Rows(fur) is [(0x88fbd16d, 4)], $"the game's fur chest keeps its row: {RowText(fur)}");
        var honey = Content.ItemData.Get("Food_T1_raw_sweets_Honey")!;
        Check(honey.Get("equipment.appliedBuff").Guid != Blocks.Find("warmth", Sample)!.Guid, "the game's honey keeps its own buff");
        Check(D("ember-tonic").Get("sequences[0].sequence").Guid != D("ember-tonic-json").Get("sequences[0].sequence").Guid, "two clones of one starter have their own sequences");
    }

    private void Kinds()
    {
        var shard = D("ember-shard");
        Check(N(shard, "maxStackSize") == 100, $"material: stack {N(shard, "maxStackSize")}");
        Check(Math.Abs(I("ember-shard").Get("fuelSetup.burnTime").Number - 120) < 1e-3 && I("ember-shard").Get("fuelSetup.isSet").Integer != 0, $"material: burns {I("ember-shard").Get("fuelSetup.burnTime").Number} s");
        Check(I("ember-shard").Get("category").Name == "Materials", $"material: category {I("ember-shard").Get("category").Name}");

        var draught = D("healing-draught");
        var potion = Content.ItemData.Get("Consumable_Potion_Health_T1")!;
        Check(N(draught, "maxStackSize") == 10 && N(draught, "impactValues.HealthChange") == 150, $"potion: stack 10, HealthChange {N(draught, "impactValues.HealthChange")}");
        Check(Rows(draught) is [(0xf38b388c, 150)], $"potion: tooltip row Health 150: {RowText(draught)}");
        Check(draught.Get("sequences[0].sequence").Guid == potion.Get("sequences[0].sequence").Guid, "potion: the game's drink, which reads HealthChange");

        var tonic = D("ember-tonic");
        var use = Content.Sequences.Get(G("ember-tonic.use"));
        Check(use != null && tonic.Get("sequences[0].sequence").Guid == use.Guid && (uint)N(tonic, "sequences[0].sequenceId") == BlockIds.Fnv(use.Guid), $"tonic: a use sequence of its own: {use?.Guid}, the item's {tonic.Get("sequences[0].sequence").Guid} id 0x{(uint)N(tonic, "sequences[0].sequenceId"):x8}");
        var effect = use?.Get("events[SpawnImpact#0].impact").Guid ?? default;
        Check(effect != default && Abilities.Programs.Info(BlockIds.Fnv(effect)) is not null, $"tonic: the sequence runs its generated program {effect}");
        Check(Rows(tonic) is [(0xf38b388c, 40)] && tonic.Get("uiValues[0].isSigned").Bool,$"tonic: its tooltip says what a use gives, signed as the game's potions: Health 40: [{RowText(tonic)}]");

        var stew = D("ember-stew");
        var warmth = Blocks.Find("warmth", Sample)!;
        var eat = Content.Sequences.Get(G("ember-stew.use"));
        Check(eat != null && eat.Get("events[ApplyBuffEvent#0].buffTypeOverride").Guid == warmth.Guid, $"food: its sequence gives the status: {eat?.Get("events[ApplyBuffEvent#0].buffTypeOverride").Guid}");
        Check(Content.Buffs.Get(warmth.GameName)?.Get("slot").Name == "Food", $"food: the status sits in the food slots: {Content.Buffs.Get(warmth.GameName)?.Get("slot").Name}");
        Check(stew.Get("equipment.appliedBuff").Guid == warmth.Guid, $"food: the item's appliedBuff is the status: {stew.Get("equipment.appliedBuff").Guid}");
        Check(Rows(stew) is [(0x86a75e07, 600000)], $"food: tooltip row Duration, the status's 600 s: {RowText(stew)}");
        // eaten, the game takes the food item's own time (config 0xbee6d4a9), which the starter (Honey) has at 60 s
        Check(Math.Abs(N(stew, "impactValues.0xbee6d4a9") - 600) < 1e-3, $"food: the item's own food time is the status's: {N(stew, "impactValues.0xbee6d4a9")} s");
        var buff = Content.Buffs.Get(warmth.GameName)!;
        var slow = Content.Buffs.Get("0x5f5d2f5c")!;
        var blessing = Content.Buffs.Get("0xd98a51cb")!;
        Check(buff.Get("icon.image").Guid == blessing.Get("icon.image").Guid && buff.Get("icon.image").Guid != slow.Get("icon.image").Guid,
            $"a buff with no Look has a blessing's icon, not the frost slow's snail: {buff.Get("icon.image").Guid}");
        Check(buff.Get("description").Guid != slow.Get("description").Guid && buff.Get("description").Guid != blessing.Get("description").Guid,
            $"and with no Description shows none, not the look's: {buff.Get("description").Guid}");
        Check(buff.GetJson("whileApplied")?["value"] is JsonArray { Count: 0 } && buff.GetJson("impactValues")?["value"] is JsonObject { Count: 0 },
            $"no body effect and no configs of a starter: whileApplied {buff.GetJson("whileApplied")?["value"]?.ToJsonString()}, impactValues {buff.GetJson("impactValues")?["value"]?.ToJsonString()}");
        Check(buff.Get("hideDurationInUi").Integer == 0 && Math.Abs(N(buff, "defaultLifeTime") - 600) < 1e-3, $"it shows its time, 600 s: hideDurationInUi {buff.Get("hideDurationInUi").Integer}, {N(buff, "defaultLifeTime")} s");
        var burn = Content.Buffs.Get(Blocks.Find("burn", Sample)!.GameName)!;
        Check(burn.Get("icon.image").Guid == slow.Get("icon.image").Guid, "a debuff with no Look still looks like the frost slow");
        // a creature's health bar draws only a status with a colour; the frost slow has none
        uint Colour(GameDataEntry e) => (uint)e.Get("color").Integer;
        Check(Colour(burn) == 0xFFFFFFFF && Colour(slow) == 0, $"and is white, so a creature's bar shows it: color 0x{Colour(burn):x8} (the frost slow's 0x{Colour(slow):x8})");
        Check(Colour(buff) == Colour(blessing), $"a buff keeps its look's colour: 0x{Colour(buff):x8}");

        var arrow = D("ember-arrow");
        Check(N(arrow, "maxStackSize") == 200 && N(arrow, "itemLevelRange.minLevel") == 5, "arrow: stack 200, level 5");
        Check(Power(arrow, 5) == 12, $"arrow: power {Power(arrow, 5)}, what it adds to the bow's");
        Check(Math.Abs(N(arrow, "damageSetup.distribution.physicalPierceDamage") - 8.0 / 12) < 1e-4 && Math.Abs(N(arrow, "damageSetup.distribution.magicalFireDamage") - 4.0 / 12) < 1e-4, "arrow: two thirds pierce, a third fire");
        Check(I("ember-arrow").Get("ammunitionType").Name == "Arrow" && I("ember-arrow").Get("equipment.slot").Name == "Arrow", "arrow: goes where arrows go");

        var axe = D("ember-axe");
        var axeItem = I("ember-axe");
        Check(N(axe, "itemLevelRange.minLevel") == 10 && N(axe, "itemLevelRange.maxLevel") == 10 && axe.Get("generateRarity").Number == 0 && axeItem.Get("rarity").Name == "Uncommon", "axe: the crafted form: one level, Uncommon");
        Check(N(axe, "damageSetup.distribution.woodDamage") == 30 && N(axe, "damageSetup.distribution.stoneDamage") == 6 && N(axe, "damageSetup.distribution.metalDamage") == 3, $"axe: Power 30 is its wood damage, the rest along: {N(axe, "damageSetup.distribution.woodDamage")}/{N(axe, "damageSetup.distribution.stoneDamage")}/{N(axe, "damageSetup.distribution.metalDamage")}");
        Check(N(axe, "impactValues.StaminaCost") == 2 && Rows(axe).Contains((0xaa06e3ec, 2)), $"axe: StaminaCost 2 and the tooltip row follows: {RowText(axe)}");
        Check(Rows(axe).Contains((0x88fbd16d, 10)), "axe: the tooltip's Item Level row is its level");

        var pick = D("ember-pick");
        Check(N(pick, "damageSetup.distribution.stoneDamage") == 18 && N(pick, "damageSetup.distribution.woodDamage") == 12, $"pickaxe: Power 18 is its stone damage: {N(pick, "damageSetup.distribution.stoneDamage")}");
        Check(N(pick, "impactValues.0x928ebfe3.value.softDamage") == 100 && N(pick, "impactValues.0x928ebfe3.value.hardDamage") == 45 && N(pick, "impactValues.0x6e279a98.value.hardDamage") == 315, $"pickaxe: it digs along: {N(pick, "impactValues.0x928ebfe3.value.softDamage")}, {N(pick, "impactValues.0x928ebfe3.value.hardDamage")}, {N(pick, "impactValues.0x6e279a98.value.hardDamage")}");
    }

    private void Wand()
    {
        var wand = D("ember-wand");
        var item = I("ember-wand");
        var primary = Content.Sequences.Get(G("ember-wand.primary"));
        var heavy = Content.Sequences.Get(G("ember-wand.heavy"));
        Check(N(wand, "itemLevelRange.minLevel") == 5 && wand.Get("generateRarity").Number == 0 && item.Get("rarity").Name == "Uncommon", "wand: the crafted form");
        Check(Power(wand, 5) == 25 && Math.Abs(N(wand, "damageSetup.distribution.magicalFireDamage") - 1) < 1e-5 && N(wand, "damageSetup.distribution.magicalIceDamage") == 0, $"wand: its power and kind are its primary's main damage, Fire 25: {Power(wand, 5)}");
        Check(item.Get("ammunitionType").Name != "Spell" && item.Get("equipment.primaryAnimationSet").Name == "Wand", $"wand: no ammo, held as a wand ({item.Get("ammunitionType").Name})");
        Check(primary != null && heavy != null && primary.Guid != heavy.Guid, "wand: each attack is a sequence of its own");
        Check(wand.Get("sequences[0].sequence").Guid == primary!.Guid && wand.Get("sequences[4].sequence").Guid == heavy!.Guid && wand.Get("sequences[2].sequence").Guid == primary.Guid && wand.Get("sequences[3].sequence").Guid == heavy.Guid, "wand: Primary and Heavy go to theirs, and the dodge and jump attacks follow them");
        var plain = Content.ItemData.Get("blocks-items-test.late-wand")!;
        var own = Content.Sequences.Get("blocks-items-test.late-wand.primary")!.Guid;
        Check(Enumerable.Range(0, 5).All(i => plain.Get($"sequences[{i}].sequence").Guid == own), "wand: an attack left out is the primary again");
        var flight = Content.Templates.Get(G("ember-wand.primary.projectile"));
        Check(primary.Get("events[SpawnEntityEvent#0].templateReference").Guid == flight?.Guid, "wand: the primary fires its own projectile, named by the sequence");
        Check(Math.Abs(N(primary, "events[SpawnEntityEvent#0].spawnVelocity.z") - 40) < 1e-4 && primary.Get("events[SpawnEntityEvent#0].ignoreSpawnVelocity").Number != 0, $"wand: at its own speed: {N(primary, "events[SpawnEntityEvent#0].spawnVelocity.z")}");
        Check(N(primary, "events[PayUsageCost#0].impactValues.0xaf236ece") == 6, $"wand: the primary's mana on its own payment, which beats the item's [dis 0x1ba980]: {N(primary, "events[PayUsageCost#0].impactValues.0xaf236ece")}");
        Check(N(heavy, "events[PayUsageCost#0].impactValues.0xaf236ece") == 20, $"wand: the heavy's mana: {N(heavy, "events[PayUsageCost#0].impactValues.0xaf236ece")}");
        var heavyFlight = Content.Templates.Get(G("ember-wand.heavy.projectile"));
        Check(heavy.Get("events[SpawnEntityEvent#0].templateReference").Guid == heavyFlight?.Guid && heavy.Get("events[SpawnEntityEvent#0].requiredEntityTag").Guid == default, "wand: the heavy fires its own projectile, for every element");
        Check(Enumerable.Range(1, 4).All(i => N(heavy, $"events[SpawnEntityEvent#{i}].chance") == 0), "wand: the heavy's other elements' shots are silenced");
        Check(Rows(wand) is [(0xbe5b2600, 6), (0xc9d82dea, 200)], $"wand: tooltip: Mana Cost 6, Radius 2: {RowText(wand)}");
        Check(N(wand, "impactValues.0x4e133b42") == 0, "wand: the starter's mana regeneration is not this block's");
    }

    private void ArmorData()
    {
        var mail = D("ember-mail");
        var item = I("ember-mail");
        Check(item.Get("equipment.slot").Name == "Armour_UpperBody" && N(mail, "itemLevelRange.minLevel") == 5 && N(mail, "itemLevelRange.maxLevel") == 5 && mail.Get("generateRarity").Number == 0 && item.Get("rarity").Name == "Rare", "armor: chest, the crafted form, Rare");
        Check(Armor(mail, "physical", 5) == 30 && Armor(mail, "magical", 5) == 12, $"armor: the game's formula gives what was written: physical {Armor(mail, "physical", 5)}, magical {Armor(mail, "magical", 5)}");
        Check(Armor(mail, "fire", 5) == 6, $"armor: fire {Armor(mail, "fire", 5)}");
        Check(N(mail, "impactValues.0x9d74c0c0") == 60, $"armor: Health 60 as the item's config: {N(mail, "impactValues.0x9d74c0c0")}");
        Check(Rows(mail) is [(0x88fbd16d, 5), (0xf38b388c, 60)], $"armor: tooltip: Item Level 5, Health 60: {RowText(mail)}");
        var set = Content.Items.Get("Armor_T1_Warrior_DPS_Chest")!;
        Check(item.GetRef("equipment.visualModel") == set.GetRef("equipment.visualModel") && item.GetRef("iconModel") == set.GetRef("iconModel"), $"armor: the set gave its look for the slot: {item.GetRef("equipment.visualModel")}");
        var helm = D("ember-helm");
        Check(Armor(helm, "physical", 5) == 16 && Armor(helm, "magical", 5) == 8 && I("ember-helm").Get("rarity").Name == "Uncommon", "armor: a helm from a shared stats part");
        Check(Rows(helm) is [(0x88fbd16d, 5)], $"armor: nothing the block did not say: {RowText(helm)}");
        var band = D("ember-band");
        Check(I("ember-band").Get("equipment.slot").Name == "Armour_Ring", "ring: worn as a ring");
        Check(N(band, "impactValues.0xcdfaf8a5") == 30 && N(band, "impactValues.0x4bf0438d") > 0.04 && N(band, "impactValues.0x9d74c0c0") == 0 && N(band, "impactValues.0xc24ecbbb") == 0, "ring: its bonuses, and not the starter's health and regeneration");
        Check(Rows(band) is [(0x88fbd16d, 5), (0xda23cb46, 30), (0x81ec5961, 5)], $"ring: tooltip: Item Level, Mana 30, Magic Damage 5%: {RowText(band)}");
    }

    private static readonly StationKind[] Kinds_ = [StationKind.Workbench, StationKind.Carpenter, StationKind.Kitchen, StationKind.Forge, StationKind.Oven, StationKind.Alchemy];
    // placeable items and their templates: the sample's bench and its JSON twin, and a station of every kind
    private static readonly (string Item, string Template)[] Placed_ =
        [(G("ember-bench"), G("ember-bench.station")), (G("ember-bench-json"), G("ember-bench-json.station")),
         .. Kinds_.Select(k => ($"blocks-items-test.station-{k.Key}", $"blocks-items-test.station-{k.Key}.station"))];
    private static IEnumerable<string> PlacedItems => Placed_.Select(x => x.Item);
    private readonly Dictionary<string, Guid?> _placedAtReady = new();

    private void Station()
    {
        foreach (var k in Kinds_)
        {
            var t = Content.Templates.Get($"blocks-items-test.station-{k.Key}.station");
            Check(t != null && (uint)t.Get("Crafting.workshopId").Number == Convert.ToUInt32(k.Workshop, 16) && Content.Items.Get($"blocks-items-test.station-{k.Key}")?.GetRef("equipment.placedEntity") == t.Guid,
                $"station kind {k.Key}: the item places a template of its own with workshop {k.Workshop}: 0x{(uint)(t?.Get("Crafting.workshopId").Number ?? 0):x8}");
        }
        var bench = I("ember-bench");
        var template = Content.Templates.Get(G("ember-bench.station"));
        Check(template != null && bench.GetRef("equipment.placedEntity") == template.Guid, $"station: the item places a template of its own: {bench.GetRef("equipment.placedEntity")}");
        Check((uint)(template?.Get("Crafting.workshopId").Number ?? 0) == 0x4da88f73, $"station: which opens the kind's workshop: 0x{(uint)(template?.Get("Crafting.workshopId").Number ?? 0):x8}");
        Check(_placedAtReady.Count > 0, $"station: content was ready before the test read the placeable items ({_placedAtReady.Count})");
        foreach (var (item, name) in Placed_)
        {
            var own = Content.Templates.Get(name)?.Guid;
            var atReady = _placedAtReady.GetValueOrDefault(item);
            Check(own != null && atReady == own,
                $"station: {item} places its own template from the moment content is ready ({name} {own}): {(atReady?.ToString() ?? "nothing")} at ready, {Content.Items.Get(item)?.GetRef("equipment.placedEntity")} now");
        }
        var recipe = Content.Recipes.Get(G("ember-gem.recipe"));
        Check((uint)(recipe?.Get("workshopId").Integer ?? 0) == 0x4da88f73, $"station: a recipe naming the station is at its workshop: 0x{(uint)(recipe?.Get("workshopId").Integer ?? 0):x8}");
        Check(recipe?.CraftingDuration == TimeSpan.FromSeconds(4), $"recipe: Seconds 4: {recipe?.CraftingDuration}");
    }

    private void Loot()
    {
        var loot = Content.Loot.Get(G("ember-gem"));
        Check(loot != null && loot.Get("stackSizeMin").Integer == 1 && loot.Get("stackSizeMax").Integer == 2, $"loot: rolled like metal scraps, in stacks of {loot?.Get("stackSizeMin").Integer}..{loot?.Get("stackSizeMax").Integer}");
        var def = NewContent.Definitions().FirstOrDefault(d => d.Name == G("ember-gem"));
        Check(def?.DefinitionJson is { } j && j.Contains("like:Material_T1_Metal_Scraps", StringComparison.Ordinal) && j.Contains("drops", StringComparison.Ordinal), $"loot: and into every drop table that drops them, one chance in four: {Regex.Match(def?.DefinitionJson ?? "", "\"drops\":.{0,120}").Value}");
        Check(I("ember-gem").Get("rarity").Name == "Rare" && N(D("ember-gem"), "maxStackSize") == 20, "loot: a material that says its rarity and stack");
        var info = NewContent.InfoJson();
        Info($"loot info: {Regex.Match(info, "\"loot\":.{0,200}").Value}");
    }

    // JSON and C# are the same objects: what they lower to differs only in names and the ids that follow from names.
    private void Twin()
    {
        static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "-json", "");
        string[] pairs = ["burn", "warmth", "ember-shard", "healing-draught", "ember-tonic", "ember-stew", "ember-arrow", "ember-axe", "ember-pick", "ember-gem", "ember-charm", "sword", "ember-guard", "ember-relic", "ember-edge", "ember-wand", "ember-helm", "ember-mail", "ember-band", "ember-bench"];
        foreach (var n in pairs)
        {
            var cs = Normal(Blocks.Lowered(n, Sample));
            var json = Normal(Blocks.Lowered(n + "-json", Sample));
            Check(cs.Length > 40 && cs == json, $"{n}: blocks/twins.json lowers to what the C# does ({cs.Length} characters of definitions)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
    }

    // ---- ids and stored items across a restart ------------------------------------------------------------------

    private async Task Ids()
    {
        string[] names = ["ember-shard", "healing-draught", "ember-arrow", "ember-axe", "sword", "ember-wand", "ember-mail", "ember-bench", "ember-gem", "burn", "warmth"];
        var now = new JsonObject();
        foreach (var n in names) now[n] = Blocks.Find(n, Sample)!.Guid.ToString();
        now["recipe"] = Content.Recipes.Get(G("sword.recipe"))!.Guid.ToString();
        now["template"] = Content.Templates.Get(G("ember-wand.primary.projectile"))!.Guid.ToString();
        now["sequence"] = Content.Sequences.Get(G("ember-wand.primary"))!.Guid.ToString();
        var file = Path.Combine(Context.DataDirectory, "ids.json");
        (string Block, int Count)[] stored = [("ember-shard", 37), ("ember-arrow", 5), ("sword", 1), ("ember-wand", 1), ("ember-mail", 1)];
        if (File.Exists(file))
        {
            var was = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var (k, v) in now) Check(was[k]?.ToString() == v?.ToString(), $"after the restart {k} is still {v}");
            foreach (var (block, count) in stored)
            {
                var id = new ItemId(Blocks.Find(block, Sample)!.Id);
                var holder = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty).FirstOrDefault(e => Items.Read(e)?.Count(id) > 0);
                var c = holder.IsNone ? null : Items.Read(holder);
                Check(c?.Count(id) == count, $"the save kept {block}: {c?.Count(id)} of {count}, in {holder}");
                var slot = c?.Slots.FirstOrDefault(s => s.Item == id);
                if (block is "sword" or "ember-mail") Check(slot is { Level: 5 } && slot.Rarity == ItemRarity.Rare, $"and it is what it was: {block} level {slot?.Level}, {slot?.Rarity}");
                if (!holder.IsNone) await Items.Take(holder, id, count);
            }
            File.Delete(file);
            return;
        }
        foreach (var (block, count) in stored)
        {
            var g = await Items.Give(_chest, new ItemId(Blocks.Find(block, Sample)!.Id), count);
            Check(g.Status == ItemsStatus.Done, $"give {count} {block} to the chest for the restart: {g}");
            await Steps(2);
        }
        File.WriteAllText(file, now.ToJsonString());
        Info("ids written: restart the lab for the second phase");
    }

    // ---- crafted-item rule, through the recipe path -----------------------------------------------------------------

    private async Task<(ItemRarity? Rarity, int Level, int Available, int Unlocked, int Damage)?> Make(string block)
    {
        var item = I(block);
        var recipe = Content.Recipes.Get(G(block + ".recipe"))!;
        var r = await Items.GiveRecipeResult(_chest, recipe.Id);
        if (r.Status != ItemsStatus.Done) { Check(false, $"{block}: the recipe made nothing: {r}"); return null; }
        var ch = r.Changes.FirstOrDefault(c => c.Item.Value == item.Id);
        await Steps(2);
        var slot = Items.Read(_chest)!.Slots.First(s => s.Slot == ch.Slot);
        var perks = World.ReadBytes(ch.ItemEntity, "PerkContainerNew");
        var state = World.ReadBytes(ch.ItemEntity, "ItemState");
        var made = (slot.Rarity, slot.Level, perks is { Length: >= 2 } ? perks[0] : 0, perks is { Length: >= 2 } ? perks[1] : 0, state is { Length: >= 14 } ? BitConverter.ToUInt16(state, 12) : -1);
        await Items.Take(_chest, new ItemId(item.Id), r.Done > 0 ? ch.Amount : 1, ch.Slot, exactSlot: true);
        await Steps(1);
        return made;
    }

    private async Task Crafted()
    {
        (string Block, ItemRarity Rarity, int Level, int Damage)[] gear =
        [
            ("sword", ItemRarity.Rare, 5, 30), ("waraxe", ItemRarity.Rare, 5, 30), ("mace", ItemRarity.Rare, 5, 30), ("greatsword", ItemRarity.Rare, 5, 30), ("greataxe", ItemRarity.Rare, 5, 30),
            ("hammer", ItemRarity.Rare, 5, 30), ("daggers", ItemRarity.Rare, 5, 30), ("bow", ItemRarity.Rare, 5, 30), ("staff", ItemRarity.Rare, 5, 30), ("ember-guard", ItemRarity.Uncommon, 5, -1),
            ("ember-edge", ItemRarity.Rare, 10, 60), ("ember-wand", ItemRarity.Uncommon, 5, 25), ("ember-axe", ItemRarity.Uncommon, 10, -1), ("ember-pick", ItemRarity.Common, 10, -1),
            ("ember-helm", ItemRarity.Uncommon, 5, -1), ("ember-mail", ItemRarity.Rare, 5, -1), ("ember-band", ItemRarity.Common, 5, -1),
        ];
        foreach (var (block, rarity, level, damage) in gear)
        {
            var seen = new List<string>();
            var ok = true;
            for (var run = 0; run < 3; run++)
            {
                var m = await Make(block);
                if (m is not { } x) { ok = false; break; }
                seen.Add($"{x.Rarity} L{x.Level} perks {x.Available}/{x.Unlocked} dmg {x.Damage}");
                ok &= x.Rarity == rarity && x.Level == level && x.Available == (int)rarity + 1 && x.Unlocked == 0 && (damage < 0 || x.Damage == damage);
            }
            Check(ok, $"{block} through the game's recipe function is what its tooltip shows ({rarity}, level {level}, {(int)rarity + 1} perk(s){(damage >= 0 ? $", damage {damage}" : "")}), three times: {string.Join("; ", seen)}");
        }
        // stacks: what one craft gives, in the item's own stack
        var arrows = Content.Recipes.Get(G("ember-arrow.recipe"))!;
        var given = await Items.GiveRecipeResult(_chest, arrows.Id);
        var id = new ItemId(I("ember-arrow").Id);
        await Steps(2);
        Check(given.Status == ItemsStatus.Done && Items.Read(_chest)!.Count(id) >= 20, $"arrows: one craft makes 20: {given}");
        await Items.Take(_chest, id, 20);
        var used = Content.Recipes.Get(G("ember-stew.recipe"))!;
        Check(used.Inputs.Any(i => i.Item.Value == I("ember-shard").Id && i.Count == 1) && used.Inputs.Any(i => i.Count == 2), $"recipe: needs a block of this mod and a game item: {string.Join(", ", used.Inputs)}");
    }

    // ---- uses, on a player-template entity ---------------------------------------------------------------------

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
        var e = await Spawn.Entity(template, at, 0, new SpawnOptions { Tag = "blocks-items-test", Flags = SpawnFlags.Transient });
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

    private async Task Stations_()
    {
        var bench = await Make(G("ember-bench.station"), new WorldPosition(_at.X - 6, _at.Y, _at.Z + 6));
        var crafting = World.ReadBytes(bench, "Crafting");
        Check(crafting is { Length: >= 20 } && BitConverter.ToUInt32(crafting, 0x10) == 0x4da88f73, $"the station's template spawns, with the workshop's id on it: 0x{(crafting is { Length: >= 20 } ? BitConverter.ToUInt32(crafting, 0x10) : 0):x8}");
        Spawn.Destroy(bench);
        await Steps(2);
    }

    // a rolled item takes what a roll gives it, as the game's loot weapons do
    private async Task Rolled()
    {
        var r = await Items.Give(_chest, new ItemId(I("ember-relic").Id), 1, new Gear(Level: 20, Rarity: ItemRarity.Epic));
        await Steps(2);
        var slot = Items.Read(_chest)!.Slots.FirstOrDefault(s => s.Item.Value == I("ember-relic").Id);
        Check(slot is { Level: 20 } && slot.Rarity == ItemRarity.Epic, $"a rolled item given level 20, Epic, as a roll passes them: level {slot?.Level}, {slot?.Rarity}: {r}");
        if (slot != null) await Items.TakeSlot(_chest, slot);
    }

    private async Task Uses()
    {
        var user = await Make("1_Player", _at);
        Combat.SetHealth(user, 50);
        await Steps(4);
        var h0 = Combat.GetHealth(user)?.Current;
        var use = Content.Sequences.Get(G("ember-tonic.use"))!;
        var effect = BlockIds.Fnv(use.Get("events[SpawnImpact#0].impact").Guid);
        var before = World.Find("Impact").Count;
        // The game's consume path has no check for an empty used item (exe+0x42586 reads ItemInfo+0x42c off null: a lab run, PlaySequence fills no used item, crashed the server).
        // A player's use always has one. So the lab's copy doesn't consume; what consuming does is the game's, unchanged, and a client check.
        use.Set("events[SpawnImpact#0].consume", false);
        var played = Abilities.PlaySequence(user, use.Guid);
        var ran = false;
        Events.On<ImpactStartedEvent>(e => { if (e.Impact.ProgramId == effect) ran = true; });
        await Steps(25 * 2);
        var h1 = Combat.GetHealth(user)?.Current;
        Info($"use: played {played}, program started {ran}, health {h0} -> {h1}");
        Check(played && h1 >= h0 + 40, $"the tonic's use sequence heals who plays it, through the game's SpawnImpact event: health {h0} -> {h1}");
        var after = World.Find("Impact").Count;
        Check(after <= before, $"and its program leaves nothing behind: Impact entities {before} -> {after}");
        use.Reset("events[SpawnImpact#0].consume");
        Check(use.Get("events[SpawnImpact#0].consume").Number != 0, "a player's use consumes: the sequence's own consume is back on");

        var status = Blocks.Find("warmth", Sample)!;
        var eat = Content.Sequences.Get(G("ember-stew.use"))!;
        _buffs.Clear();
        var b0 = BuffsOf(status.Id).Count;
        eat.Set("events[ApplyBuffEvent#0].consume", false);
        var ate = Abilities.PlaySequence(user, eat.Guid);
        await Steps(25 * 2);
        var buffs = BuffsOf(status.Id);
        Check(ate && buffs.Count > b0, $"food: the eat sequence's ApplyBuffEvent gives the status: {buffs.Count} entit(ies), {buffs.FirstOrDefault().Seconds:0.##} s");
        Check(buffs.Any(b => Math.Abs(b.Seconds - 600) < 1), "for the status's 600 s");
        eat.Reset("events[ApplyBuffEvent#0].consume");
        Spawn.Destroy(user);
        await Steps(4);
        await Eaten(status);
    }

    // Eaten as a player eats: the stew is the used item in the trigger context, so the game reads the item's own configs, its duration among them.
    // PlaySequence above has no used item, and the game then takes the status's own time.
    private async Task Eaten(BlockHandle status)
    {
        var stew = I("ember-stew");
        var eater = await Make("1_Player", new WorldPosition(_at.X + 4, _at.Y, _at.Z - 4));
        Combat.SetInvulnerable(eater, true);
        await Items.Give(eater, new ItemId(stew.Id), 2);
        await Steps(4);
        var slot = Items.Read(eater)?.Slots.FirstOrDefault(s => s.Item.Value == stew.Id);
        if (!Check(slot != null, "a character with two Ember Stews")) return;
        var sequence = Abilities.IdOf(D("ember-stew").Get("sequences[0].sequence").Guid);
        var b0 = BuffsOf(status.Id).Select(b => b.Id).ToHashSet();
        if (!PlayerStates.Diagnostics.LabUseItem(eater, sequence, PlayerStateId.ActionPrimary, slot.Slot.Value, stew.Id))
        {
            Info("KILN_PLAYERSTATE_LAB is off: the stew is not eaten as a player eats it");
            Spawn.Destroy(eater);
            return;
        }
        await Steps(60);
        var left = Items.Read(eater)?.Count(new ItemId(stew.Id)) ?? -1;
        var got = BuffsOf(status.Id).Where(b => !b0.Contains(b.Id)).ToList();
        Check(left == 1, $"food eaten as a player eats it: one consumed, {left} left");
        Check(got.Count == 1 && Math.Abs(got[0].Seconds - 600) < 1,
            $"and the status lasts its 600 s, not the starter's food time: {string.Join(", ", got.Select(b => $"{b.Seconds:0.##} s"))}");
        Spawn.Destroy(eater);
        await Steps(4);
    }

    // ---- a wand's attacks --------------------------------------------------------------------------------------

    private async Task WandCasts()
    {
        var caster = await Make("1_Player", _at);
        Combat.SetInvulnerable(caster, true);
        var primary = Content.Sequences.Get(G("ember-wand.primary"))!;
        var flight = Content.Templates.Get(G("ember-wand.primary.projectile"))!;
        var fired = new List<string>();
        var flightProgram = BlockIds.Fnv(flight.Get("Impact.program").Guid);
        var impactProgram = BlockIds.Fnv(Content.Templates.Get(G("ember-wand.primary.impact"))!.Get("Impact.program").Guid);
        using var watch = Events.On<ImpactStartedEvent>(e => fired.Add($"0x{e.Impact.ProgramId:x8}"));
        Check(Abilities.PlaySequence(caster, primary.Guid), "the wand's primary plays on a player-template entity");
        var longest = 0;
        for (var i = 0; i < 40; i++)
        {
            await Steps(1);
            if (Abilities.SequenceOf(caster) is { } now && now.Sequence == BlockIds.Fnv(primary.Guid)) longest = i;
        }
        Info($"the wand's primary played for {longest} step(s); impacts started: [{string.Join("; ", fired)}]; found by name: {Spawn.FindByName(flight.Name, max: 8).Count}");
        Check(fired.Contains($"0x{flightProgram:x8}"), $"the attack's sequence fires its own projectile with no ammo item and no used item: its program 0x{flightProgram:x8} started");
        Check(fired.Contains($"0x{impactProgram:x8}"), $"and where it stopped, its own impact: its program 0x{impactProgram:x8} started");

        // A real cast's item path: as the game gives it, the used item, no ammunition. The impact is run directly: a projectile needs terrain to land on
        var enemy = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 1, _at.Y, _at.Z - 1));
        var wand = Blocks.Find("ember-wand", Sample)!.Id;
        async Task<float?> Cast(string attack)
        {
            var impact = Content.Templates.Get(G($"ember-wand.{attack}.impact"))!;
            var program = BlockIds.Fnv(impact.Get("Impact.program").Guid);
            var c = new byte[0x2c];
            BitConverter.GetBytes(wand).CopyTo(c, 8);
            _hits.Clear();
            await Abilities.RunImpact(impact.Guid, _at, new ImpactOptions { Root = caster, Tag = "blocks-items-test", Components = [new SpawnComponent(0x4006f1f8, c)] });
            await Steps(12);
            Info($"{attack}: hits [{string.Join("; ", _hits.Select(h => $"{h.Attacker}->{h.Target} fire {h.Damage.Fire} program 0x{h.Impact.ProgramId:x8}"))}] (its program 0x{program:x8})");
            return _hits.FirstOrDefault(h => h.Impact.ProgramId == program && h.Target == enemy)?.Damage.Fire;
        }
        var main = await Cast("primary");
        Check(main is { } m && Math.Abs(m - 25) < 1e-3, $"primary, with the wand as the used item, no ammo: fire {main}, its power and the tooltip's number");
        var big = await Cast("heavy");
        Check(big is { } b && Math.Abs(b - 70) < 1e-3, $"heavy, the same: fire {big}, exactly what it says, not the wand's 25");
        Spawn.Destroy(enemy);
        Spawn.Destroy(caster);
        await Steps(4);
    }

    // ---- hits through the game's stream ----------------------------------------------------------------------------

    private async Task<(List<ImpactDamageEvent> Hits, List<ImpactBuffEvent> Buffs, EntityId Target, int? Before, int? After)> SwingAt(EntityId by, EntityId target, bool keep = false)
    {
        var at = World.Position(target)!.Value;
        var h0 = Combat.GetHealth(target)?.Current;
        _hits.Clear();
        _buffs.Clear();
        var he = await Abilities.RunProgram(Swing, at, new ImpactOptions
        {
            Root = by,
            Tag = "blocks-items-test",
            Configs = new ImpactConfigs
            {
                [Cfg("blocksitems.Target")] = target,
                [Cfg("blocksitems.At")] = new[] { (float)at.X, (float)at.Y, (float)at.Z }.SelectMany(BitConverter.GetBytes).ToArray(),
            },
        });
        await Steps(16);
        Spawn.Destroy(he);
        return (_hits.ToList(), _buffs.ToList(), target, h0, Combat.GetHealth(target)?.Current);
    }

    private async Task<bool> ToHand(EntityId wearer, string block)
    {
        var item = Blocks.Find(block, Sample)!;
        var give = await Items.Give(wearer, item.GameName, 1);
        await Steps(20);
        var slot = Items.Read(wearer)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == item.Id);
        if (!Check(slot is not null, $"{block} is in the bag: {give}")) return false;
        GameTree.GameRequest(wearer, 5, (uint)slot!.Slot.Index);
        await Steps(24);
        var eq = World.ReadBytes(wearer, "Equipment");
        var held = eq is null ? 0 : BitConverter.ToUInt32(eq, 8 + 8);
        return Check(held == item.Id, $"{block} is in hand: Equipment slot 0 holds 0x{held:x8}");
    }

    private async Task Hits()
    {
        var wearer = await Make("1_Player", new WorldPosition(_at.X - 2, _at.Y, _at.Z - 2));
        Combat.SetInvulnerable(wearer, true);
        var low = JsonNode.Parse(Blocks.Lowered("ember-edge", Sample)!)!;
        var program = BlockIds.Fnv(Guid.Parse((string)low["onHitProgram"]!));
        var burn = Blocks.Find("burn", Sample)!.Guid;
        if (!await ToHand(wearer, "ember-edge")) return;

        var one = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 3, _at.Y, _at.Z + 3));
        var hit = await SwingAt(wearer, one);
        var fromProgram = hit.Hits.Where(h => h.Impact.ProgramId == program).ToList();
        Info($"in hand: hits [{string.Join("; ", hit.Hits.Select(h => $"{h.Attacker}->{h.Target} fire {h.Damage.Fire} cut {h.Damage.Cut} program 0x{h.Impact.ProgramId:x8}"))}]");
        Check(fromProgram.Count(h => h.Target == one && Math.Abs(h.Damage.Fire - 12) < 1e-3) == 1, $"OnHit works in hand with no unlock, through an OnHit block by handle: {fromProgram.Count} hit(s) of fire 12");
        Check(hit.Buffs.Count(b => b.Buff == burn && b.Target == one) == 1, "and it applies the status the effect names");
        Check(fromProgram.Count == 1, $"a hit dealt by the effect does not trigger the effect again: {fromProgram.Count}");
        Check(hit.After < hit.Before, $"it hurts: {hit.Before} -> {hit.After}");

        // a kill: the area around what died gets the effect, and something near it gets hit by OnKill
        var doomed = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 6, _at.Y, _at.Z + 3));
        var near = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 7, _at.Y, _at.Z + 3));
        Combat.SetHealth(doomed, 1);
        await Steps(4);
        var kill = await SwingAt(wearer, doomed);
        Info($"a kill: {kill.Before} -> {kill.After}, hits [{string.Join("; ", kill.Hits.Select(h => $"{h.Attacker}->{h.Target} fire {h.Damage.Fire} cut {h.Damage.Cut} program 0x{h.Impact.ProgramId:x8}"))}]");
        Check(kill.Hits.Any(h => h.Impact.ProgramId == program && h.Target == near && Math.Abs(h.Damage.Fire - 12) < 1e-3), "OnKill: what died took the area with it: the enemy beside it got the effect");
        var noKill = await SwingAt(wearer, near);
        Check(!noKill.Hits.Any(h => h.Impact.ProgramId == program && h.Target == one), "and a hit that does not kill does not: the first enemy, 3 m away, is not touched");
        Spawn.Destroy(one);
        Spawn.Destroy(near);
        Spawn.Destroy(wearer);
        await Steps(4);
    }

    // ---- armor on a wearer -----------------------------------------------------------------------------------

    // keen::EquipmentSlot Armour_UpperBody, and where the wearer's Equipment holds it (keen::ImpactEquippedItems)
    private const int ChestSlot = 6, ChestImpact = 3;

    private static uint EquippedItem(EntityId wearer, int impact) =>
        World.ReadBytes(wearer, "Equipment") is { } e ? BitConverter.ToUInt32(e, 8 + impact * 0x48 + 8) : 0;

    private static string Armour(EntityId wearer)
    {
        if (World.ReadBytes(wearer, "ArmorPoints") is not { Length: >= 16 } b) return "?";
        var off = BitConverter.ToUInt16(b, 4);
        var r = new List<uint>();
        for (var o = off; o + 4 <= Math.Min(b.Length, off + 24); o += 4) r.Add(BitConverter.ToUInt32(b, o));
        return string.Join(",", r);
    }

    // A spawned player-template entity has no equipment slots set (a join sets them), so the test says which slot the chest is, through a switch only a lab has.
    private async Task Worn()
    {
        var wearer = await Make("1_Player", new WorldPosition(_at.X - 4, _at.Y, _at.Z - 4));
        var mail = Blocks.Find("ember-mail", Sample)!;
        var inv = Items.Read(wearer)!;
        var equipment = inv.Slots.First(s => s.Category == InventoryCategory.Equipment).Slot.Inventory;
        var armourBefore = Armour(wearer);
        var healthBefore = Combat.GetHealth(wearer)?.Max;
        var placed = await Items.Give(wearer, new ItemId(mail.Id), 1, slot: SlotId.Of(equipment, ChestSlot));
        await Steps(4);
        var at = Items.Read(wearer)!.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == mail.Id);
        Check(at?.Slot == SlotId.Of(equipment, ChestSlot), $"the mail went into the equipment inventory's chest slot: {placed}, at {at?.Slot}");

        var gate = Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops"));
        var request = 8 | (ChestSlot << 8);
        var node = equipment.Value | ((uint)ChestSlot << 24);
        File.Move(gate, gate + ".off");
        Check(!GameTree.GameRequest(wearer, request, node), "without the switch, it does nothing");
        File.Move(gate + ".off", gate);
        var notPlayer = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X - 8, _at.Y, _at.Z - 8));
        Check(!GameTree.GameRequest(notPlayer, request, node), "on something that is not a player, it does nothing");
        Spawn.Destroy(notPlayer);
        Check(!GameTree.GameRequest(wearer, 8 | (20 << 8), node), "on a slot that is not one, it does nothing");
        Check(GameTree.GameRequest(wearer, request, node), "the test says the chest is that slot");
        await Steps(50);
        if (!Check(EquippedItem(wearer, ChestImpact) == mail.Id, $"the mail is worn: the game's Equipment holds it: 0x{EquippedItem(wearer, ChestImpact):x8}")) return;

        var armourAfter = Armour(wearer);
        var healthAfter = Combat.GetHealth(wearer)?.Max;
        Info($"worn: ArmorPoints [{armourBefore}] -> [{armourAfter}]; max health {healthBefore} -> {healthAfter}");
        Check(armourAfter.Split(',').Contains("30"), $"the block's Physical 30 reaches the wearer, through the game's apply_armor: [{armourBefore}] -> [{armourAfter}]");
        Check(healthAfter >= healthBefore + 60, $"the block's Health 60 reaches the wearer, through the game's equipment program: max health {healthBefore} -> {healthAfter}");

        // a hit taken: from the enemy, on the wearer; the effect goes to who dealt it
        var enemy = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X - 2, _at.Y, _at.Z - 4));
        var low = JsonNode.Parse(Blocks.Lowered("ember-mail", Sample)!)!;
        var program = BlockIds.Fnv(Guid.Parse((string)low["onHitProgram"]!));
        var hit = await SwingAt(enemy, wearer);
        Info($"a hit taken: hits [{string.Join("; ", hit.Hits.Select(h => $"{h.Attacker}->{h.Target} fire {h.Damage.Fire} cut {h.Damage.Cut} program 0x{h.Impact.ProgramId:x8}"))}]");
        Check(hit.Hits.Count(h => h.Impact.ProgramId == program && h.Target == enemy && h.Attacker == wearer && Math.Abs(h.Damage.Fire - 5) < 1e-3) == 1, "OnHitTaken: the armor's effect hits who dealt the blow, once, while it is worn, with no unlock");

        await Items.Take(wearer, new ItemId(mail.Id), 1);
        await Steps(30);
        var off = await SwingAt(enemy, wearer);
        Check(!off.Hits.Any(h => h.Impact.ProgramId == program), "taken off, it stops");
        Spawn.Destroy(enemy);
        Spawn.Destroy(wearer);
    }
}
