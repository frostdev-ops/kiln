// SPDX-License-Identifier: MIT
using System.Text.Json;
using Kiln;

namespace CraftMatchTest;

/// <summary>
/// Lab test (no player): what the game makes must be what the recipe tooltip promises.
/// <para>The promise is computed the way the client's recipe tooltip does (docs/re/items.md section 10): rarity =
/// <c>rarity</c>, level = <c>itemLevelRange.minLevel</c>, perk levels = rarity + 1. Every item is created through the game's
/// recipe result function (<c>Items.GiveRecipeResult</c>) and through <c>Items.Give</c> without Gear, several times, and the
/// item entity's rarity, level and perks are logged ("MADE" lines) and compared.</para>
/// <para>Cases: vanilla crafted gear and a vanilla loot weapon (controls); new items of a loot-weapon base with and
/// without a rarity; a level from the definition; a recipe added later; a game recipe pointed at a new item; an item with no
/// recipe (fixed too); the opt-ins (RolledLoot, an explicit range, explicit generateRarity: as asked, and they warn when a
/// recipe makes the item); a perk slot gap; and NewItems / ModelSample when they are deployed.</para>
/// <para>Phase 2 after a restart: a saved instance made under the old rules keeps its rarity and perks, and its level comes
/// into the item's new one-level range when the save loads.</para>
/// </summary>
public sealed class CraftMatchTestMod : Mod
{
    private const string BronzeSword = "Weapon_T1_1H_SwordEpic_01";   // loot weapon: generateRarity, levels 1..55
    private const string T5Sword = "Weapon_T5_1H_SwordEpic_01";       // loot weapon: generateRarity, shown Legendary
    private const string Twigs = "Material_T1_Twigs";

    private record Case(string Name, Guid Guid, string Base, Dictionary<string, object>? Fields, bool Recipe, Guid RecipeGuid,
        object[]? Perks = null);

    private static readonly Case[] Cases =
    [
        // like Glacier: a loot-weapon base + a rarity
        new("Kiln_CM_Epic", Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a01"), BronzeSword,
            new() { ["damageSetup.dmgMod"] = 1.6, ["rarity"] = "Epic" }, true, Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b01")),
        // like TowerSword: a loot-weapon base and nothing else
        new("Kiln_CM_Plain", Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a02"), BronzeSword, null, true,
            Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b02")),
        // a base shown Legendary, generateRarity on: nothing set by the mod
        new("Kiln_CM_Legend", Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a03"), T5Sword, null, true,
            Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b03")),
        // the level the mod asks for
        new("Kiln_CM_L20", Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a04"), BronzeSword,
            new() { ["rarity"] = "Rare", ["itemLevelRange.minLevel"] = 20 }, true, Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b04")),
        // like Frost Mail: a crafted armour base (no generateRarity, one level)
        new("Kiln_CM_Mail", Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a05"), "Armor_T1_Warrior_DPS_Chest",
            new() { ["rarity"] = "Rare" }, true, Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b05")),
    ];

    private static readonly Guid RangeItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a10");
    private static readonly Guid RangeRecipe = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b10");
    private static readonly Guid GenItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a11");
    private static readonly Guid GenRecipe = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b11");
    private static readonly Guid GapItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a12");
    private static readonly Guid GiveOnlyItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a13");
    private static readonly Guid LateItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a14");
    private static readonly Guid LateRecipe = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b14");
    private static readonly Guid RuleItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a15");
    private static readonly Guid MigrateItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a16");
    private static readonly Guid MigrateRecipe = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b16");
    private static readonly Guid RolledItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a17");
    private static readonly Guid RolledCraftedItem = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0a18");
    private static readonly Guid RolledCraftedRecipe = Guid.Parse("1f7a1c30-52de-4b6e-9a10-3c6d1e8f0b18");

    private int _pass, _fail;
    private bool _stop, _ran;
    private EntityId _chest;

    private string Marker => Path.Combine(Context.DataDirectory, "phase2.json");

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    public override void OnUnload() => _stop = true;

    public override void OnLoad()
    {
        if (!Content.Available || !NewContent.Available || !Items.Available || !World.Available)
        {
            Log.Warn("craftmatch-test: needs kiln.content, kiln.items, kiln.world");
            return;
        }
        foreach (var c in Cases)
        {
            Content.Items.Register(new ItemDefinition { Guid = c.Guid, Name = c.Name, BaseOn = c.Base, DisplayName = c.Name, Fields = c.Fields ?? new Dictionary<string, object>() });
            Content.Recipes.Register(Recipe(c.RecipeGuid, c.Name));
        }
        // opt-outs: the definition asks for the loot behaviour, on purpose
        Content.Items.Register(new ItemDefinition
        {
            Guid = RangeItem, Name = "Kiln_CM_Range", BaseOn = BronzeSword,
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.6, ["rarity"] = "Epic", ["itemLevelRange.minLevel"] = 1, ["itemLevelRange.maxLevel"] = 55 },
        });
        Content.Recipes.Register(Recipe(RangeRecipe, "Kiln_CM_Range"));
        Content.Items.Register(new ItemDefinition
        {
            Guid = GenItem, Name = "Kiln_CM_Gen", BaseOn = BronzeSword,
            Fields = new Dictionary<string, object> { ["rarity"] = "Epic", ["generateRarity"] = true },
        });
        Content.Recipes.Register(Recipe(GenRecipe, "Kiln_CM_Gen"));
        // Epic reads perk slots 0..3: slot 3 cleared
        Content.Items.Register(new ItemDefinition
        {
            Guid = GapItem, Name = "Kiln_CM_Gap", BaseOn = BronzeSword, Perks = [null, null, null, "none"],
            Fields = new Dictionary<string, object> { ["rarity"] = "Epic" },
        });
        // no recipe: fixed all the same
        Content.Items.Register(new ItemDefinition
        {
            Guid = GiveOnlyItem, Name = "Kiln_CM_GiveOnly", BaseOn = BronzeSword, Fields = new Dictionary<string, object> { ["rarity"] = "Rare" },
        });
        // the opt-in for rolled loot: keeps the base's generateRarity and level range
        Content.Items.Register(new ItemDefinition
        {
            Guid = RolledItem, Name = "Kiln_CM_Rolled", BaseOn = BronzeSword, RolledLoot = true, Fields = new Dictionary<string, object> { ["rarity"] = "Epic" },
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = RolledCraftedItem, Name = "Kiln_CM_RolledCrafted", BaseOn = BronzeSword, RolledLoot = true, Fields = new Dictionary<string, object> { ["rarity"] = "Epic" },
        });
        Content.Recipes.Register(Recipe(RolledCraftedRecipe, "Kiln_CM_RolledCrafted"));
        Content.Items.Register(new ItemDefinition { Guid = LateItem, Name = "Kiln_CM_Late", BaseOn = BronzeSword });
        Content.Items.Register(new ItemDefinition { Guid = RuleItem, Name = "Kiln_CM_Rule", BaseOn = BronzeSword, Fields = new Dictionary<string, object> { ["rarity"] = "Uncommon" } });
        // Phase 1: the old behaviour by hand (what a saved instance of an earlier Kiln is). Phase 2: the plain definition.
        var phase2 = File.Exists(Marker);
        Content.Items.Register(new ItemDefinition
        {
            Guid = MigrateItem, Name = "Kiln_CM_Migrate", BaseOn = BronzeSword,
            Fields = phase2
                ? new Dictionary<string, object> { ["rarity"] = "Epic" }
                : new Dictionary<string, object> { ["rarity"] = "Epic", ["generateRarity"] = true, ["itemLevelRange.minLevel"] = 1, ["itemLevelRange.maxLevel"] = 55 },
        });
        Content.Recipes.Register(Recipe(MigrateRecipe, "Kiln_CM_Migrate"));
        Content.OnReady(ev =>
        {
            if (_ran) return;
            _ran = true;
            _ = RunGuarded();
        });
    }

    private static RecipeDefinition Recipe(Guid guid, string item) => new()
    {
        Guid = guid, Name = item + "_Recipe", Station = "hand", Inputs = [(Twigs, 1)], Outputs = [(item, 1)],
    };

    private async Task RunGuarded()
    {
        try
        {
            for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Catalog: true, Read: true }; i++) await Steps(1);
            await Run();
        }
        catch (Exception e)
        {
            Check(false, $"the test threw: {e}");
        }
        Log.Info($"craftmatch-test done: {_pass} PASS, {_fail} FAIL");
    }

    // ---- what one created instance is ----
    private sealed record Made(string Path, ItemRarity? Rarity, int Level, uint[] LevelData, int Available, int Unlocked,
        bool HasPerks, int Damage, EntityId Pide)
    {
        public override string ToString() =>
            $"rarity {Rarity}, level {Level} (Level [{string.Join(",", LevelData)}]), " +
            $"perks {(HasPerks ? $"{Available} available, {Unlocked} unlocked" : "none (no PerkContainerNew)")}, baseDamageUi {Damage}, entity {Pide}";
    }

    private async Task<Made?> Inspect(string path, ContentEntry item, Func<Task<ItemsResult>> create)
    {
        var r = await create();
        if (r.Status != ItemsStatus.Done)
        {
            Check(false, $"{item.Name} via {path}: {r}");
            return null;
        }
        var ch = r.Changes.FirstOrDefault(c => c.Item.Value == item.Id);
        if (ch.ItemEntity.IsNone)
        {
            Check(false, $"{item.Name} via {path}: no item entity: {r}");
            return null;
        }
        await Steps(2);
        var slot = Items.Read(_chest)!.Slots.First(s => s.Slot == ch.Slot);
        var lv = World.ReadBytes(ch.ItemEntity, "Level");
        var perks = World.ReadBytes(ch.ItemEntity, "PerkContainerNew");
        var state = World.ReadBytes(ch.ItemEntity, "ItemState");
        var data = new List<uint>();
        if (lv is { Length: >= 16 })
        {
            var off = BitConverter.ToUInt16(lv, 4);
            for (var o = off; o + 4 <= lv.Length; o += 4) data.Add(BitConverter.ToUInt32(lv, o));
        }
        var made = new Made(path, slot.Rarity, slot.Level, data.ToArray(), perks is { Length: >= 2 } ? perks[0] : 0,
            perks is { Length: >= 2 } ? perks[1] : 0, perks is { Length: >= 2 },
            state is { Length: >= 14 } ? BitConverter.ToUInt16(state, 12) : -1, ch.ItemEntity);
        Log.Info($"MADE {item.Name} via {path}: {made}{PerkList(item, made)}");
        var t = await Items.Take(_chest, new ItemId(item.Id), 1, ch.Slot, exactSlot: true);
        if (t.Status != ItemsStatus.Done) Check(false, $"{item.Name}: taking it back: {t}");
        await Steps(1);
        return made;
    }

    // the perks the instance's level ladder shows: the first `available` slots of the item, by name
    private static string PerkList(ContentEntry item, Made m)
    {
        if (!m.HasPerks || !Content.GameDataAvailable) return "";
        var names = new List<string>();
        for (var i = 0; i < Math.Min(m.Available, 5); i++)
        {
            var id = (uint)item.Get($"perkIds[{i}]").Integer;
            names.Add(id == 0 ? "(empty)" : Content.Perks.Get(id)?.Name ?? $"0x{id:x8}");
        }
        return $", perk list [{string.Join(", ", names)}]";
    }

    // ---- what the recipe tooltip shows (client 0xb1fa70, docs/re/items.md section 10) ----
    private sealed record Promise(ItemRarity Rarity, int Level, int Available)
    {
        public override string ToString() => $"{Rarity}, level {Level}, {Available} perk(s)";
    }

    private static Promise PromiseOf(ContentEntry item)
    {
        var rarity = (ItemRarity)item.Get("rarity").Integer;
        var perks = item.Get("perkIds[0]").Integer != 0;
        return new Promise(rarity, (int)item.Get("itemLevelRange.minLevel").Integer, perks ? Math.Min((int)rarity + 1, 5) : 0);
    }

    private static string Fields(ContentEntry item) =>
        $"rarity {item.Get("rarity").Name}, generateRarity {item.Get("generateRarity").Integer != 0}, " +
        $"levels {item.Get("itemLevelRange.minLevel").Integer}..{item.Get("itemLevelRange.maxLevel").Integer}";

    private async Task<bool> CheckMatches(string name, string? recipeName, int runs = 4)
    {
        var item = Content.Items.Get(name);
        if (!Check(item != null, $"item {name} is registered")) return false;
        var promise = PromiseOf(item!);
        Log.Info($"TOOLTIP {name}: {promise} ({Fields(item!)})");
        var ok = true;
        async Task One(string path, Func<Task<ItemsResult>> create)
        {
            for (var i = 0; i < runs && !_stop; i++)
            {
                var m = await Inspect(path, item!, create);
                if (m == null) { ok = false; return; }
                ok &= m.Rarity == promise.Rarity && m.Level == promise.Level && m.Available == promise.Available && m.Unlocked == 0;
            }
        }
        if (recipeName != null)
        {
            var recipe = Content.Recipes.Get(recipeName);
            if (!Check(recipe != null, $"recipe {recipeName} is registered")) return false;
            await One("recipe", () => Items.GiveRecipeResult(_chest, recipe!.Id));
        }
        await One("give", () => Items.Give(_chest, new ItemId(item!.Id)));
        Check(ok, $"{name}: every instance is what the tooltip promises ({promise})");
        return ok;
    }

    private bool Warned(Guid guid, string what)
    {
        var d = NewContent.Definitions().FirstOrDefault(x => x.Guid == guid && x.Kind == ContentKind.Items);
        var w = d?.Warnings.FirstOrDefault(x => x.Contains(what, StringComparison.OrdinalIgnoreCase));
        return Check(w != null, $"{d?.Name}: the definition carries a warning about \"{what}\": {w ?? "(none: " + string.Join("; ", d?.Warnings ?? []) + ")"}");
    }

    private async Task Run()
    {
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 4 && (!EntityData.Available || EntityData.IsPersistent(e)))
            {
                _chest = e;
                break;
            }
        if (!Check(!_chest.IsNone, $"a saved container with free slots: {_chest}")) return;
        var original = Describe(Items.Read(_chest)!);

        if (File.Exists(Marker))
        {
            await Phase2();
            return;
        }

        // ---- controls: the game's own items ----
        await CheckMatches("Weapon_T1_1H_Sword_Craft", "Weapon_ScrappySword_T1_Grassland");   // Common, one level
        await CheckMatches("Craft_Weapon_T2_1H_SwordEpic_chineseNewYear_01", "Craft_Weapon_T2_1H_SwordEpic_chineseNewYear_01");  // Rare, one level
        var loot = Content.Items.Get(BronzeSword)!;
        Log.Info($"TOOLTIP {BronzeSword}: {PromiseOf(loot)} ({Fields(loot)}): a loot weapon, no recipe");
        var levels = new List<int>();
        for (var i = 0; i < 12 && !_stop; i++)
        {
            var m = await Inspect("give", loot, () => Items.Give(_chest, new ItemId(loot.Id)));
            if (m == null) return;
            levels.Add(m.Level);
        }
        Check(levels.Distinct().Count() > 1 && levels.All(l => l is >= 1 and <= 55),
            $"control: without a level the game rolls one in the range: Bronze Sword levels {string.Join(",", levels)}");
        // three vanilla crafted items keep generateRarity: the game's own tooltip is wrong for them (Rare shown, Common made)
        var odd = Content.Items.Get("Craft_Weapon_T3_2H_StaffEpic_chineseNewYear_01");
        if (odd != null)
        {
            var m = await Inspect("recipe", odd, () => Items.GiveRecipeResult(_chest, Content.Recipes.Get("Craft_Weapon_T3_2H_StaffEpic_chineseNewYear_01")!.Id));
            Log.Info($"NOTE vanilla oddity {odd.Name}: tooltip {PromiseOf(odd)} ({Fields(odd)}), made: {m}");
        }

        // ---- new items ----
        foreach (var c in Cases) await CheckMatches(c.Name, c.Name + "_Recipe");
        var l20 = Content.Items.Get("Kiln_CM_L20")!;
        Check(l20.Get("itemLevelRange.minLevel").Integer == 20 && l20.Get("itemLevelRange.maxLevel").Integer == 20,
            $"Kiln_CM_L20: {Fields(l20)}");

        // a recipe registered later: the item was fixed already
        var late = Content.Items.Get("Kiln_CM_Late")!;
        Check(late.Get("generateRarity").Integer == 0 && late.Get("itemLevelRange.maxLevel").Integer == 1, $"Kiln_CM_Late before it has a recipe: {Fields(late)}");
        Content.Recipes.Register(Recipe(LateRecipe, "Kiln_CM_Late"));
        for (var i = 0; i < 100 && Content.Recipes.Get("Kiln_CM_Late_Recipe") == null; i++) await Steps(1);
        await CheckMatches("Kiln_CM_Late", "Kiln_CM_Late_Recipe");

        // a game recipe pointed at a new item by code
        var rule = Content.Items.Get("Kiln_CM_Rule")!;
        var rod = Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!;
        Check(rule.Get("itemLevelRange.maxLevel").Integer == 1, $"Kiln_CM_Rule before a game recipe makes it: {Fields(rule)}");
        var rodOutputs = rod.Outputs;
        rod.SetOutputs([new RecipeStack(new ItemId(rule.Id), 1)]);
        await CheckMatches("Kiln_CM_Rule", "Tool_basic_FishingRod_T0_PK", 2);
        rod.SetOutputs(rodOutputs);

        // ---- the opt-ins: as asked, and they say so when a recipe makes the item ----
        var rc = Content.Items.Get("Kiln_CM_RolledCrafted")!;
        Check(rc.Get("generateRarity").Integer != 0 && rc.Get("itemLevelRange.maxLevel").Integer == 55, $"RolledLoot keeps the base's: {Fields(rc)}");
        var rcm = new List<Made?>();
        for (var i = 0; i < 6 && !_stop; i++)
            rcm.Add(await Inspect("recipe", rc, () => Items.GiveRecipeResult(_chest, Content.Recipes.Get("Kiln_CM_RolledCrafted_Recipe")!.Id)));
        Check(rcm.All(m => m is { Rarity: ItemRarity.Common }) && rcm.Select(m => m?.Level).Distinct().Count() > 1,
            $"RolledLoot with a recipe: the game's roll, as asked: levels {string.Join(",", rcm.Select(m => m?.Level))}, all Common");
        Warned(RolledCraftedItem, "generateRarity is on");
        Warned(RolledCraftedItem, "rolls a level");
        var range = Content.Items.Get("Kiln_CM_Range")!;
        Log.Info($"TOOLTIP Kiln_CM_Range: {PromiseOf(range)} ({Fields(range)}): explicit range");
        var rolled = new List<int>();
        for (var i = 0; i < 8 && !_stop; i++)
        {
            var m = await Inspect("recipe", range, () => Items.GiveRecipeResult(_chest, Content.Recipes.Get("Kiln_CM_Range_Recipe")!.Id));
            if (m == null) return;
            rolled.Add(m.Level);
        }
        Check(rolled.Distinct().Count() > 1, $"explicit range: a roll per craft, as asked: levels {string.Join(",", rolled)}");
        Warned(RangeItem, "rolls a level");
        var gen = Content.Items.Get("Kiln_CM_Gen")!;
        var gm = await Inspect("recipe", gen, () => Items.GiveRecipeResult(_chest, Content.Recipes.Get("Kiln_CM_Gen_Recipe")!.Id));
        Check(gm is { Rarity: ItemRarity.Common } && gen.Get("generateRarity").Integer != 0, $"explicit generateRarity: as asked: {Fields(gen)}, made {gm}");
        Warned(GenItem, "generateRarity");
        var gap = Content.Items.Get("Kiln_CM_Gap")!;
        var pm = await Inspect("give", gap, () => Items.Give(_chest, new ItemId(gap.Id)));
        Check(pm is { Rarity: ItemRarity.Rare, Available: 3 }, $"Epic with perk slot 3 empty: the rarity follows the filled slots (availablePerks - 1): {pm}");
        Warned(GapItem, "perk slot");

        // ---- no recipe: fixed too; RolledLoot: the range stays, Gear levels work ----
        await CheckMatches("Kiln_CM_GiveOnly", null, 3);
        var only = Content.Items.Get("Kiln_CM_GiveOnly")!;
        Check(only.Get("generateRarity").Integer == 0 && only.Get("itemLevelRange.maxLevel").Integer == 1, $"no recipe, no opt-in: {Fields(only)}");
        var rolledItem = Content.Items.Get("Kiln_CM_Rolled")!;
        Check(rolledItem.Get("generateRarity").Integer != 0 && rolledItem.Get("itemLevelRange.maxLevel").Integer == 55, $"RolledLoot, no recipe: {Fields(rolledItem)}");
        Check(!NewContent.Definitions().First(x => x.Guid == RolledItem && x.Kind == ContentKind.Items).Warnings.Any(), "RolledLoot without a recipe: no warning");
        var g30 = await Inspect("give Gear(30)", rolledItem, () => Items.Give(_chest, new ItemId(rolledItem.Id), 1, new Gear(30)));
        Check(g30 is { Level: 30, Rarity: ItemRarity.Common }, $"RolledLoot: Gear(30) gives level 30, the lowest rarity: {g30}");
        var ge = await Inspect("give Gear(50, Epic)", rolledItem, () => Items.Give(_chest, new ItemId(rolledItem.Id), 1, new Gear(50, ItemRarity.Epic)));
        Check(ge is { Level: 50, Rarity: ItemRarity.Epic, Available: 4 }, $"RolledLoot: Gear(50, Epic): {ge}");

        // a crafted item clamps a Gear level (as for every vanilla crafted item); a Gear rarity is honoured
        var epic = Content.Items.Get("Kiln_CM_Epic")!;
        var clamp = await Inspect("give Gear(50, Common)", epic, () => Items.Give(_chest, new ItemId(epic.Id), 1, new Gear(50, ItemRarity.Common)));
        Check(clamp is { Level: 1, Rarity: ItemRarity.Common, Available: 1 }, $"crafted item: the game clamps Gear(50) to its one level, the rarity is as given: {clamp}");

        // ---- the samples, when deployed ----
        foreach (var (name, recipe) in new[]
                 {
                     ("Kiln_Sample_Glacier", "Kiln_Sample_Glacier_Recipe"), ("Kiln_Sample_FrostEdge", "Kiln_Sample_FrostEdge_Recipe"),
                     ("Kiln_Sample_FrostMail", "Kiln_Sample_FrostMail_Recipe"), ("Kiln_Sample_TowerSword", "Kiln_Sample_TowerSword_Recipe"),
                 })
            if (Content.Items.Get(name) != null) await CheckMatches(name, recipe, 3);
            else Log.Info($"{name}: not deployed");

        // ---- which instance was in the screenshot: damage by level and rarity (dmgMod 1.6, as Glacier) ----
        foreach (var (lvl, rar) in new[] { (1, ItemRarity.Epic), (1, ItemRarity.Common), (2, ItemRarity.Common), (3, ItemRarity.Common), (49, ItemRarity.Common), (50, ItemRarity.Epic), (54, ItemRarity.Epic) })
        {
            var m = await Inspect($"give Gear({lvl}, {rar})", range, () => Items.Give(_chest, new ItemId(range.Id), 1, new Gear(lvl, rar)));
            Log.Info($"DAMAGE dmgMod 1.6, level {lvl}, {rar}: baseDamageUi {m?.Damage}");
        }
        Check(Describe(Items.Read(_chest)!) == original, "the container is as it was");

        // nothing unlocks perks on its own: an Epic left alone for 30 s
        var idle = await Items.Give(_chest, new ItemId(epic.Id));
        var idleEntity = idle.Changes.First(c => c.Item.Value == epic.Id);
        var at0 = Bytes(World.ReadBytes(idleEntity.ItemEntity, "PerkContainerNew"));
        await Steps(2);
        at0 = Bytes(World.ReadBytes(idleEntity.ItemEntity, "PerkContainerNew"));
        var lv0 = Items.Read(_chest)!.Slots.First(x => x.Slot == idleEntity.Slot).Level;
        await Steps(25 * 30);
        var at30 = Bytes(World.ReadBytes(idleEntity.ItemEntity, "PerkContainerNew"));
        var lv30 = Items.Read(_chest)!.Slots.First(x => x.Slot == idleEntity.Slot).Level;
        Check(at0 == "0400" && at30 == at0 && lv30 == lv0, $"an Epic left alone for 30 s: perks {at0} -> {at30}, level {lv0} -> {lv30}");
        await Items.Take(_chest, new ItemId(epic.Id), 1, idleEntity.Slot, exactSlot: true);

        // ---- leave an instance made under the old rules for phase 2 ----
        var mig = Content.Items.Get("Kiln_CM_Migrate")!;
        Log.Info($"phase 1: Kiln_CM_Migrate {Fields(mig)}");
        var a = await Items.Give(_chest, new ItemId(mig.Id), 1, new Gear(49));
        var b = await Items.Give(_chest, new ItemId(mig.Id), 1, new Gear(3));
        await Steps(3);
        var inv = Items.Read(_chest)!;
        var s = inv.Slots.Where(x => x.Item.Value == mig.Id).ToList();
        foreach (var x in s) Log.Info($"phase 1: saved instance level {x.Level} rarity {x.Rarity} perks [{Bytes(World.ReadBytes(x.ItemEntity, "PerkContainerNew"))}] PideRebalanced {World.ComponentSize(x.ItemEntity, "PideRebalanced")} B");
        var saved = await Saves.Request();
        Check(saved.Success && a.Succeeded && b.Succeeded, $"phase 1: two Kiln_CM_Migrate instances saved: {saved}");
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Marker, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["chest"] = _chest.Value, ["levels"] = s.Select(x => x.Level).ToArray(),
        }));
        Log.Info("craftmatch-test: phase 1 done; restart the server for phase 2");
    }

    private async Task Phase2()
    {
        var mig = Content.Items.Get("Kiln_CM_Migrate")!;
        var m = JsonDocument.Parse(File.ReadAllText(Marker)).RootElement;
        _chest = new EntityId(m.GetProperty("chest").GetUInt32());   // the chest phase 1 used (it has fewer free slots now)
        var inv = Items.Read(_chest)!;
        Log.Info($"phase 2: the plain definition now: {Fields(mig)}, saved levels were [{string.Join(",", m.GetProperty("levels").EnumerateArray().Select(x => x.GetInt32()))}]");
        await Steps(10);
        inv = Items.Read(_chest)!;
        var s = inv.Slots.Where(x => x.Item.Value == mig.Id).ToList();
        Check(s.Count == 2, $"phase 2: both instances survived the save: {s.Count}");
        foreach (var x in s)
        {
            var perks = Bytes(World.ReadBytes(x.ItemEntity, "PerkContainerNew"));
            Check(x.Level == 1 && x.Rarity == ItemRarity.Common && perks == "0100",
                $"phase 2: a saved instance keeps its rarity and perks; its level came into the new range: level {x.Level}, {x.Rarity}, perks {perks}");
        }
        var without = Describe(Items.Read(_chest)!, mig.Id);
        foreach (var x in s) await Items.TakeSlot(_chest, x);
        var fresh = await Inspect("recipe", mig, () => Items.GiveRecipeResult(_chest, Content.Recipes.Get("Kiln_CM_Migrate_Recipe")!.Id));
        var promise = PromiseOf(mig);
        Check(fresh != null && fresh.Rarity == promise.Rarity && fresh.Level == promise.Level, $"phase 2: a new craft of the same item is what the tooltip promises ({promise}): {fresh}");
        Check(Describe(Items.Read(_chest)!, 0) == without, "phase 2: the container is as it was before phase 1");
        File.Delete(Marker);
    }

    private static string Bytes(byte[]? b) => b == null ? "none" : Convert.ToHexString(b);

    private static string Describe(InventoryContents c, uint except = 0) =>
        string.Join(" ", c.Slots.Where(s => !s.IsEmpty && s.Item.Value != except).Select(s => $"{s.Item.Value:x8}x{s.Count}"));
}
