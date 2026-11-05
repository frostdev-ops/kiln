// SPDX-License-Identifier: MIT
using System.Text.Json;
using Kiln;

namespace KitbashTest;

/// <summary>
/// Lab test (no player) for kiln.content v4: new items whose references point at other loaded resources ("kitbash"),
/// texts per language, layers on references (new items freely, game items only with the opt-in), and the
/// distribution of new items (loot, drop tables, knowledge). Every check logs one PASS or FAIL line.
/// </summary>
public sealed class KitbashTestMod : Mod
{
    private static readonly Guid SwordGuid = Guid.Parse("0f8d4b7e-61a3-4c0f-9b9a-7e3a52c1d0a4");
    private static readonly Guid HatchetGuid = Guid.Parse("c1e7a0d2-58b4-4a6e-8f13-90d6b2e4a7c5");
    private static readonly Guid BadGuid = Guid.Parse("9b2f6c84-0d1e-47a3-b5c9-3e8f1a7d2b60");
    private static readonly Guid TokenGuid = Guid.Parse("3a9d7e21-6f4b-4c18-8e0a-b5c2d9f17e63");
    private static readonly Guid CoinGuid = Guid.Parse("e6b1f04c-2d8a-4973-a15e-7c3f9b2d0a58");
    private static readonly Guid TradeGuid = Guid.Parse("71c4e9a2-b03d-4f6e-9a57-2d8e1c6b4f09");
    private const string Scavenger = "a20e8eae-7833-462e-9ea9-59d1e17a1b98", Scraps = "Material_T1_Metal_Scraps";
    private const string Sword = "Kiln_Kitbash_Sword", Hatchet = "Kiln_Kitbash_Hatchet", Bad = "Kiln_Kitbash_Bad",
        PackChest = "Kiln_Kitbash_PackChest";
    private const string Bronze = "Weapon_T1_1H_SwordEpic_01", ModelFrom = "Weapon_T4_1H_SwordEpic_02",
        PerkFrom = "Weapon_T5_1H_SwordEpic_01", Axe = "Weapon_T1_1H_Axe_01";

    private int _pass, _fail;
    private bool _ran, _stop;

    public override void OnLoad()
    {
        if (!Content.Available || !NewContent.Available)
        {
            Log.Warn("kitbash-test: kiln.content new content unavailable");
            return;
        }
        Content.Items.Register(new ItemDefinition
        {
            Guid = SwordGuid,
            Name = Sword,
            BaseOn = Bronze,
            DisplayName = "Kitbash Blade",
            Texts = new Dictionary<string, ItemTexts>
            {
                ["en"] = new() { Name = "Kitbash Blade", Description = "A bronze sword in a better sword's shape.", Caption = "Kitbashed Sword" },
                ["de"] = new() { Name = "Flickwerk-Klinge", Description = "Ein Bronzeschwert in der Form eines besseren." },
                ["pt-BR"] = new() { Name = "Lâmina Remendada" },
            },
            Model = $"item:{ModelFrom}",
            Perks = [$"item:{PerkFrom}", null, "none"],
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.4, ["rarity"] = "Epic" },
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = HatchetGuid,
            Name = Hatchet,
            BaseOn = Bronze,
            DisplayName = "Kitbash Hatchet",
            WeaponType = $"item:{Axe}",
            Refs = new Dictionary<string, string> { ["equipment.impactEntity"] = $"item:{Axe}" },
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = BadGuid,
            Name = Bad,
            BaseOn = Bronze,
            Model = "item:No_Such_Item_Anywhere",
            Perks = ["12345678-1234-1234-1234-123456789abc"],
            Refs = new Dictionary<string, string> { ["equipment.visualEntity"] = $"item:{Bronze}.iconScene" },
        });
        // distribution: random loot like the metal scraps, a stack in the scavenger's drop table, one next to the scraps
        Content.Items.Register(new ItemDefinition
        {
            Guid = TokenGuid,
            Name = "Kiln_Kitbash_Token",
            BaseOn = Scraps,
            DisplayName = "Kitbash Token",
            Loot = new ItemLoot { Like = Scraps, StackMin = 2, StackMax = 4 },
            Drops = [new ItemDrop(Scavenger) { CountMin = 3, CountMax = 3, Chance = 1 }],
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = CoinGuid,
            Name = "Kiln_Kitbash_Coin",
            BaseOn = Scraps,
            DisplayName = "Kitbash Coin",
            Drops = [new ItemDrop($"like:{Scraps}") { CountMin = 2, CountMax = 2, Chance = 1 }],
        });
        // a trade offer: a recipe at the fisher trader's workshop (NPC traders are workshops with barter recipes)
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = TradeGuid,
            Name = "Kiln_Kitbash_Trade",
            Station = "0xc4acc104",
            Inputs = [("Kiln_Kitbash_Coin", 3)],
            Outputs = [("Kiln_Kitbash_Token", 1)],
        });
        Content.OnReady(ev =>
        {
            if (_ran) return;
            _ran = true;
            _ = RunAsync();
        });
    }

    public override void OnUnload() => _stop = true;

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private static string G(ContentEntry e, string path) => e.GetRef(path).ToString();

    private async Task RunAsync()
    {
        try
        {
            await Context.Scheduler.NextTick();
            Kitbash();
            Texts();
            Layers();
            await Distribution();
        }
        catch (Exception ex)
        {
            Check(false, $"test aborted: {ex}");
        }
        Log.Info($"kitbash-test done: {_pass} PASS, {_fail} FAIL");
    }

    private void Kitbash()
    {
        var info = NewContent.InfoJson();
        Log.Info($"kitbash-test: info {info}");
        using (var doc = JsonDocument.Parse(info))
        {
            var r = doc.RootElement.GetProperty("refs");
            Check(r.GetProperty("pools").EnumerateObject().Count() > 10 && r.GetProperty("perkSlots").GetInt32() > 0,
                $"reference pools from the game data: {r.GetProperty("pools").EnumerateObject().Count()} reference types, perk slots {r.GetProperty("perkSlots")} (id = FNV-1a(GUID) in {r.GetProperty("perkIdIsFnv")})");
        }
        var sword = Content.Items.Get(Sword)!;
        var bronze = Content.Items.Get(Bronze)!;
        var from = Content.Items.Get(ModelFrom)!;
        var perkFrom = Content.Items.Get(PerkFrom)!;
        var refsSame = new[] { "equipment.visualEntity", "equipment.visualModel", "equipment.equipmentEntityModelSetup.model",
            "equipment.equipmentEntityModelSetup.animation", "collectionInfo.overrideModel", "iconModel" };
        var diff = refsSame.Where(p => G(sword, p) != G(from, p)).ToList();
        Check(diff.Count == 0 && G(sword, "equipment.equipmentEntityModelSetup.model") != G(bronze, "equipment.equipmentEntityModelSetup.model"),
            $"model = item:{ModelFrom}: model {G(sword, "equipment.equipmentEntityModelSetup.model")} (base {G(bronze, "equipment.equipmentEntityModelSetup.model")}), visual entity {G(sword, "equipment.visualEntity")}" +
            (diff.Count > 0 ? $"; differ: {string.Join(", ", diff)}" : ""));
        Check(Math.Abs(sword.GetNumber("equipment.equipmentEntityModelSetup.offset.orientationOffset.w") - from.GetNumber("equipment.equipmentEntityModelSetup.offset.orientationOffset.w")) < 1e-6
              && sword.Get("equipment.hideMask").Integer == from.Get("equipment.hideMask").Integer,
            "model bundle: the model setup's offsets and the hide mask come with the model");
        Check(G(sword, "iconScene") == G(bronze, "iconScene") && G(sword, "weaponCategoryReference") == G(bronze, "weaponCategoryReference")
              && Math.Abs(sword.GetNumber("damageSetup.dmgMod") - 1.4) < 1e-6 && sword.Get("rarity").Name == "Epic"
              && sword.Get("flags").Integer == bronze.Get("flags").Integer,
            $"own stats and the base's behaviour: dmgMod {sword.GetNumber("damageSetup.dmgMod")}, rarity {sword.Get("rarity")}, weapon category and icon (scene) of {Bronze}; the pickup model (iconModel) comes with the model");
        Check(G(sword, "perkReferences[0]") == G(perkFrom, "perkReferences[0]") && sword.Get("perkIds[0]").Integer == perkFrom.Get("perkIds[0]").Integer
              && G(sword, "perkReferences[1]") == G(bronze, "perkReferences[1]") && sword.Get("perkIds[1]").Integer == bronze.Get("perkIds[1]").Integer
              && sword.GetRef("perkReferences[2]") == Guid.Empty && sword.Get("perkIds[2]").Integer == 0,
            $"perks: [0] from {PerkFrom} ({G(sword, "perkReferences[0]")} id {sword.Get("perkIds[0]")}), [1] the base's, [2] none (id {sword.Get("perkIds[2]")})");
        var hatchet = Content.Items.Get(Hatchet)!;
        var axe = Content.Items.Get(Axe)!;
        Check(G(hatchet, "weaponCategoryReference") == G(axe, "weaponCategoryReference") && G(hatchet, "weaponCategoryReference") != G(bronze, "weaponCategoryReference")
              && G(hatchet, "equipment.impactEntity") == G(axe, "equipment.impactEntity"),
            $"weaponType = item:{Axe}: category {G(hatchet, "weaponCategoryReference")} (sword {G(bronze, "weaponCategoryReference")}); refs equipment.impactEntity {G(hatchet, "equipment.impactEntity")}");
        var bad = NewContent.Definitions().First(d => d.Name == Bad);
        var badEntry = Content.Items.Get(Bad);
        Check(bad.State == "registered" && bad.Warnings.Count == 3 && badEntry != null
              && G(badEntry, "equipment.visualEntity") == G(bronze, "equipment.visualEntity") && G(badEntry, "perkReferences[0]") == G(bronze, "perkReferences[0]"),
            $"bad references are skipped, the item registers with the base's values: {bad.State}, warnings: {string.Join(" | ", bad.Warnings)}");
        var chest = Content.Items.Get(PackChest);
        var tank = Content.Items.Get("Armor_T2_Warrior_Tank_Chest")!;
        var fighter = Content.Items.Get("Armor_T1_Warrior_DPS_Chest")!;
        Check(chest != null && G(chest, "equipment.visualModel") == G(tank, "equipment.visualModel") && G(chest, "equipment.visualModel") != G(fighter, "equipment.visualModel")
              && G(chest, "iconModel") == G(tank, "iconModel") && Math.Abs(chest.GetNumber("armorSetup.distribution.physical") - 2.0) < 1e-6,
            $"data pack \"add\" (armour) with model = item:Armor_T2_Warrior_Tank_Chest: worn model {(chest == null ? "-" : G(chest, "equipment.visualModel"))} (base {G(fighter, "equipment.visualModel")}), own armour 2.0");
        var field = Content.Items.Field("equipment.visualEntity");
        Check(field is { IsReference: true, Writable: false } && Content.Items.Field("maxStackSize") is { IsReference: false },
            $"schema: {field} is a reference");
    }

    private void Texts()
    {
        using var doc = JsonDocument.Parse(NewContent.DefinitionsJson());
        var e = doc.RootElement.EnumerateArray().First(x => x.GetProperty("name").GetString() == Sword);
        var langs = e.TryGetProperty("languages", out var l) ? l.EnumerateArray().Select(x => x.GetString()).ToList() : [];
        Check(langs.SequenceEqual(["*", "de", "en", "pt-br"]), $"texts per language: {string.Join(", ", langs)}");
        using var info = JsonDocument.Parse(NewContent.InfoJson());
        var game = info.RootElement.GetProperty("languages").EnumerateArray().Select(x => x.GetString()).ToList();
        Check(game.Count >= 10 && game.Contains("de-de") && game.Contains("en-us"),
            $"the game's languages from keen::LanguageId: {string.Join(" ", game.Select((k, i) => $"{i}={k}"))}");
    }

    private void Layers()
    {
        var sword = Content.Items.Get(Sword)!;
        var t3 = Content.Items.Get("Weapon_T3_1H_Sword_01")!;
        var before = G(sword, "equipment.visualEntity");
        sword.SetRef("visual", "item:Weapon_T3_1H_Sword_01");
        var after = G(sword, "equipment.visualEntity");
        Check(after == G(t3, "equipment.visualEntity") && after != before, $"SetRef on a new item (no opt-in needed): visual {before} -> {after}");
        sword.Reset("equipment.visualEntity");
        Check(G(Content.Items.Get(Sword)!, "equipment.visualEntity") == before, "Reset: the definition's visual is back");
        var game = Content.Items.Get("Weapon_T3_1H_Sword_02")!;
        try
        {
            game.SetRef("visual", "item:Weapon_T3_1H_Sword_01");
            Check(false, "SetRef on a game item without the opt-in was not refused");
        }
        catch (ArgumentException ex)
        {
            Check(true, $"SetRef on a game item without the opt-in refused: {ex.Message}");
        }
        var orig = G(game, "equipment.visualEntity");
        game.SetRef("visual", "item:Weapon_T3_1H_Sword_01", allowGameItem: true);
        Check(G(game, "equipment.visualEntity") == G(t3, "equipment.visualEntity") && game.Original("equipment.visualEntity").Guid.ToString() == orig,
            $"SetRef on a game item with the opt-in: {orig} -> {G(game, "equipment.visualEntity")} (original kept)");
        try
        {
            game.SetRef("visual", "0a0b0c0d-1111-2222-3333-444455556666", allowGameItem: true);
            Check(false, "a GUID that is not a loaded template was accepted");
        }
        catch (ArgumentException ex)
        {
            Check(true, $"a GUID that is not loaded is refused: {ex.Message}");
        }
        var pack = Content.Items.Get("Weapon_T2_1H_Sword_01")!;
        Check(G(pack, "equipment.visualEntity") == G(t3, "equipment.visualEntity"), "data pack rule with \"@refs\": true changed a game item's visual");
        var refused = Content.Items.Get("Weapon_T2_1H_SwordEpic_01")!;
        Check(G(refused, "equipment.visualEntity") != G(t3, "equipment.visualEntity") && Content.PackReport().Contains("opt-in"),
            "data pack rule on a game item without \"@refs\" is refused (reported)");
        Check(Content.Revert() > 0 && G(Content.Items.Get("Weapon_T3_1H_Sword_02")!, "equipment.visualEntity") == orig,
            "Content.Revert takes the reference layer off the game item");
    }

    private async Task Distribution()
    {
        var loot = Content.Loot.Get("Kiln_Kitbash_Token");
        var scraps = Content.Loot.Get(Scraps);
        Check(loot != null && loot.Get("stackSizeMin").Integer == 2 && loot.Get("stackSizeMax").Integer == 4 && scraps != null
              && loot.Get("rarities").Integer == scraps.Get("rarities").Integer && loot.Get("shufflebagType").Integer == scraps.Get("shufflebagType").Integer,
            $"random loot: Kiln_Kitbash_Token has a loot entry like {Scraps} (rarities {loot?.Get("rarities")}, stacks {loot?.Get("stackSizeMin")}..{loot?.Get("stackSizeMax")})");
        var trade = NewContent.Definitions().FirstOrDefault(d => d.Name == "Kiln_Kitbash_Trade");
        using (var doc = JsonDocument.Parse(NewContent.DefinitionsJson()))
        {
            var e = doc.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == "Kiln_Kitbash_Trade");
            var listed = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("listed", out var l) && l.GetBoolean();
            Check(trade is { State: "registered" } && listed && Content.Recipes.Get("Kiln_Kitbash_Trade")?.Get("workshopId").Integer == 0xc4acc104,
                $"vendor: a trade recipe at the fisher trader (workshop 0xc4acc104): {trade} listed {listed} {trade?.Problem}");
        }
        for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Read: true }; i++) await Context.Scheduler.NextTick();
        EntityId chest = EntityId.None;
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
        {
            chest = e;
            break;
        }
        if (!Spawn.Available || chest.IsNone || Spawn.Inspect(chest) is not { } at)
        {
            Log.Info("SKIP drops: kiln.spawn unavailable");
            return;
        }
        var ent = await Spawn.Entity("LootContainer_Enemy_Scavenger_Melee01", at.Position with { Y = at.Position.Y + 1.5 },
            options: new SpawnOptions { Tag = "kitbash-test" });
        InventoryContents? inv = null;
        for (var i = 0; i < 100 && !_stop; i++)
        {
            inv = Items.Read(ent);
            if (inv is { UsedSlots: > 0 }) break;
            await Context.Scheduler.NextTick();
        }
        var token = new ItemId(NewContent.IdOf(TokenGuid));
        var coin = new ItemId(NewContent.IdOf(CoinGuid));
        var rolled = inv == null ? "none" : string.Join(", ", inv.Slots.Where(x => !x.IsEmpty).Select(x => $"{Content.Items.Get(x.Item.Value)?.Name} x{x.Count}"));
        Check(inv != null && inv.Count(token) >= 3 && inv.Count(coin) >= 2,
            $"drops: the scavenger's loot container rolled the new stacks (table GUID: Token x3, like:{Scraps}: Coin x2, times the world's Loot Amount): {rolled}");
        var table = Content.Drops.Get(Scavenger);
        Check(table != null && table.Stacks.Any(x => x.Item == token) && table.Stacks.Any(x => x.Item == coin && x.CountMax >= 5),
            $"drops: Content.Drops lists the table with the new stacks (the pack's drop rule on Kiln_Kitbash_Coin: countMax 5): {string.Join("; ", table?.Stacks.Select(x => $"{Content.Items.Get(x.Item.Value)?.Name} {x.CountMin}..{x.CountMax} p{x.Chance}") ?? [])}");
        Spawn.Destroy(ent);
        await LootProbe(at.Position, token);
    }

    /// <summary>Random loot needs a roll: enemy loot containers whose tables hold random stacks (item 0) roll from the
    /// candidate list (the getter kiln.content extends). Spawns up to 40 of them far from anyone, counts the rolls.</summary>
    private async Task LootProbe(WorldPosition at, ItemId token)
    {
        static (long Rolls, long Extended) Info()
        {
            using var doc = JsonDocument.Parse(NewContent.InfoJson());
            var l = doc.RootElement.GetProperty("loot");
            return (l.GetProperty("rolls").GetInt64(), l.GetProperty("extended").GetInt64());
        }
        var before = Info();
        var seen = new Dictionary<string, int>();
        var tokens = 0;
        var only = Path.Combine(Context.DataDirectory, "loot-probe.txt");  // lab diagnostics: one template name per line
        var candidates = File.Exists(only)
            ? File.ReadAllLines(only).Select(n => Spawn.Templates.FirstOrDefault(t => t.Name == n.Trim())).Where(t => t != null).Select(t => t!).ToList()
            : Spawn.Templates.Where(t => t.Name.StartsWith("LootContainer_", StringComparison.Ordinal)).Take(20).ToList();
        Log.Info($"kitbash-test: loot probe over {candidates.Count} container template(s)");
        var n = 0;
        foreach (var t in candidates)
        {
            if (++n % 20 == 0 && Info().Rolls > before.Rolls) break;
            EntityId e;
            try
            {
                e = await Spawn.Entity(t.Name, at with { Y = at.Y + 2 }, options: new SpawnOptions { Tag = "kitbash-test" });
            }
            catch (Exception)
            {
                continue;
            }
            for (var i = 0; i < 20 && !_stop && Items.Read(e) is not { UsedSlots: > 0 }; i++) await Context.Scheduler.NextTick();
            if (Items.Read(e) is { } inv)
            {
                tokens += inv.Count(token);
                foreach (var x in inv.Slots.Where(x => !x.IsEmpty)) seen[Content.Items.Get(x.Item.Value)?.Name ?? x.Item.ToString()] = 1;
            }
            Spawn.Destroy(e);
        }
        var after = Info();
        Log.Info($"kitbash-test: loot probe: {after.Rolls - before.Rolls} random-loot roll(s) during {candidates.Count} spawned container(s) " +
                 $"({after.Extended - before.Extended} with the new items), Kiln_Kitbash_Token rolled {tokens} time(s); random containers roll when a player opens them");
        using var doc = JsonDocument.Parse(NewContent.InfoJson());
        var g = doc.RootElement.GetProperty("loot").TryGetProperty("getter", out var gg) ? gg : default;
        var items = doc.RootElement.GetProperty("loot").GetProperty("items").GetInt32();
        Check(g.ValueKind == JsonValueKind.Object && items >= 1 && g.GetProperty("newAtTheEnd").GetInt32() == items && g.GetProperty("lootFind").GetInt32() == items,
            $"random loot: the rolls' candidate getter (LootItems, through the hook) returns {(g.ValueKind == JsonValueKind.Object ? g.GetProperty("count").ToString() : "?")} entries, the last {items} the new items; LootFind finds them: {g}");
    }
}
