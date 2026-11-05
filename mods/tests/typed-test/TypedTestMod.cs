// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;
using Kiln.GameIds;

namespace TypedTest;

/// <summary>
/// Typed fields against string paths, player-free. The definitions in content/strings.json are written again here with
/// typed fields; both must give the same definition and the same live data. Typed reads and writes on game entries are
/// compared with what the string API sees. Prints PASS / FAIL lines. It holds after <c>reload typed-test</c> and after a restart.
/// </summary>
public sealed class TypedTestMod : Mod
{
    private int _pass, _fail;

    private const string ShortDebuff = "a00dbdc0-0394-43c5-83cb-96c6c425dec9";   // a game buff, as a reference by GUID

    private static readonly Guid
        SwordTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c01"), RecipeTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c02"),
        BoltTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c03"), VukahTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c04"),
        BurstTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c05"), BurstCode = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c09"), BuffTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c06"),
        PerkTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c07"), CastTyped = Guid.Parse("3d0d6f0e-41a3-4c56-9d6b-1c7a0a9f2c08");

    public override void OnLoad()
    {
        Log.Info($"typed-test: OnLoad, catalog {GameCatalog.Side} {GameCatalog.SteamBuild} (data {GameCatalog.DataBuild})");
        Define();
        var ran = false;
        Content.OnReady(ev =>
        {
            if (ran) return;
            ran = true;
            _ = RunAsync();
        });
    }

    private async Task RunAsync()
    {
        // a data pack's new items reach kiln.gamedata's item list a step after the registries are ready
        for (var i = 0; i < 5; i++) await Context.Scheduler.NextTick();
        try
        {
            Run();
        }
        catch (Exception e)
        {
            Check(false, $"the test threw: {e}");
        }
        Log.Info($"typed-test: done, {_pass} PASS, {_fail} FAIL");
    }

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"typed-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Define()
    {
        Content.Items.Register(new ItemDefinition
        {
            Guid = SwordTyped,
            Name = "Typed_Sword_Typed",
            BaseOn = GameItems.Weapon_T1_1H_Sword_01,
            Fields = new FieldValues
            {
                { GameFields.Items.MaxStackSize, 3 },
                { GameFields.Items.Rarity, GameEnums.ItemRarity.Epic },
                { GameFields.Items.DamageSetup.DmgMod, 1.5f },
                { GameFields.Items.DamageSetup.SpeedMod, 1.25f },
                { GameFields.Items.ItemLevelRange.MinLevel, 7 },
                { GameFields.Items.ItemLevelRange.MaxLevel, 7 },
                { GameFields.Items.DisableRarityGeneration, GameEnums.ItemRarityMask.Common | GameEnums.ItemRarityMask.Rare },
            },
        });
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = RecipeTyped,
            Name = "Typed_Recipe_Typed",
            Station = "hand",
            Inputs = [new RecipeIngredient(GameItems.Material_T1_Twigs, 2)],
            Outputs = [new RecipeIngredient("Typed_Sword_Typed", 1)],
            Fields = new FieldValues { { GameFields.Recipes.CraftingDuration, TimeSpan.FromSeconds(2.5) } },
        });
        Content.Templates.Define(new GameDataClone
        {
            Guid = BoltTyped,
            Name = "Typed_Bolt_Typed",
            Base = GameTemplates.Projectile_Player_Staff_IceBolt_1,
            Values =
            {
                { GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(5) },
                { GameFields.Templates.StaticGravityModifier.Modifier, 0.1f },
                { GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnTemplate, GameTemplates.Explosion_Player_Spell_IceBolt_1 },
            },
        });
        Content.Templates.Define(new GameDataClone
        {
            Guid = VukahTyped,
            Name = "Typed_Vukah_Typed",
            Base = GameTemplates.Enemy_Vukah_Melee,
            Values =
            {
                { GameFields.Templates.Level.DataStorage[0], 20 },
                { GameFields.Templates.ScaleComponent.UniformScale, 1.4f },
                { GameFields.Templates.Enemy01BehaviorState.AggroSetup.ViewRange, 36f },
                { GameFields.Templates.Enemy01BehaviorState.AggroSetup.CheckLineOfSight, false },
            },
        });
        Content.Templates.Define(new GameDataClone
        {
            Guid = BurstTyped,
            Name = "Typed_Burst_Typed",
            Base = GameTemplates.Explosion_Player_Spell_Fireball_1,
            Values =
            {
                { GameFields.Templates.ImpactConfiguration.ConfigValues.NoiseRange, 40f },
                { GameFields.Templates.ImpactConfiguration.ConfigValues.PushBackStrength, GameEnums.PushBackForce.Big },
            },
        });
        // the same with string paths in code, the way a mod writes it without GameFields
        Content.Templates.Define(new GameDataClone
        {
            Guid = BurstCode,
            Name = "Typed_Burst_Code",
            Base = GameTemplates.Explosion_Player_Spell_Fireball_1,
            Fields =
            {
                ["ImpactConfiguration.configValues.NoiseRange"] = 40f,
                ["ImpactConfiguration.configValues.PushBackStrength"] = "Big",
            },
        });
        Content.Buffs.Define(new GameDataClone
        {
            Guid = BuffTyped,
            Name = "Typed_Buff_Typed",
            Base = "0x5f425faf",
            Values = { { GameFields.Buffs.DefaultLifeTime, TimeSpan.FromSeconds(9) } },
        });
        Content.Perks.Define(new GameDataClone
        {
            Guid = PerkTyped,
            Name = "Typed_Perk_Typed",
            Base = GamePerks.Perk_OnDamage_Dealt_Debuff_Poisoned_T2,
            Values = { { GameFields.Perks.ImpactValues.BuffType, ShortDebuff } },
        });
        Content.Sequences.Define(new GameDataClone
        {
            Guid = CastTyped,
            Name = "Typed_Cast_Typed",
            Base = GameSequences.Attack_Player_Staff_FixedAnim_Fireball_fast,
            Values =
            {
                { GameFields.Sequences.Length, TimeSpan.FromSeconds(2.5) },
                { GameFields.Sequences.Loop, false },
            },
        });
    }

    private void Run()
    {
        Definitions();
        LiveData();
        Writes();
        Refusals();
        LivePaths();
    }

    // ---- the same definition, as strings and as typed fields ----

    /// <summary>A definition's <c>fields</c> with sorted keys. The rest of the definition differs only by the guid and name it was given.</summary>
    private static string FieldsOf(JsonNode? def)
    {
        if (def?["fields"] is not JsonObject o) return "(none)";
        return string.Join("; ", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " = " + kv.Value?.ToJsonString()));
    }

    private void Definitions()
    {
        foreach (var name in new[] { "Bolt", "Vukah", "Burst", "Buff", "Perk", "Cast" })
        {
            var defs = Content.GameDataDefinitions();
            var s = defs.FirstOrDefault(d => d.Name == $"Typed_{name}_Strings");
            var t = defs.FirstOrDefault(d => d.Name == $"Typed_{name}_Typed");
            Check(s?.State == "registered" && t?.State == "registered", $"{name}: both registered ({s?.State}, {s?.Error}; {t?.State}, {t?.Error})");
            if (s is null || t is null) continue;
            var (fs, ft) = (FieldsOf(JsonNode.Parse(s.DefinitionJson)), FieldsOf(JsonNode.Parse(t.DefinitionJson)));
            Check(fs == ft && fs != "(none)", $"{name}: the definition's fields are identical: {fs}" + (fs == ft ? "" : $" against {ft}"));
        }
        var all = NewContent.Definitions();
        foreach (var name in new[] { "Sword", "Recipe" })
        {
            var s = all.FirstOrDefault(d => d.Name == $"Typed_{name}_Strings");
            var t = all.FirstOrDefault(d => d.Name == $"Typed_{name}_Typed");
            Check(s is not null && t is not null && s.Problem is null && t.Problem is null, $"{name}: both defined ({s?.State}, {s?.Problem}; {t?.State}, {t?.Problem})");
            if (s is null || t is null) continue;
            var (fs, ft) = (FieldsOf(JsonNode.Parse(s.DefinitionJson ?? "{}")), FieldsOf(JsonNode.Parse(t.DefinitionJson ?? "{}")));
            Check(fs == ft && fs != "(none)", $"{name}: the definition's fields are identical: {fs}" + (fs == ft ? "" : $" against {ft}"));
        }
    }

    /// <summary>Every leaf of an entry with the fields that name the entry itself left out.</summary>
    private static Dictionary<string, string> Leaves(GameDataEntry e)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in e.Fields())
            d[f.Path] = f.Value?.ToJsonString() ?? "null";
        return d;
    }

    private void SameLeaves(GameDataEntry? s, GameDataEntry? t, string what, params string[] own)
    {
        if (s is null || t is null)
        {
            Check(false, $"{what}: an entry is missing ({s} / {t})");
            return;
        }
        var (a, b) = (Leaves(s), Leaves(t));
        var differ = a.Keys.Union(b.Keys).Where(k => !(a.TryGetValue(k, out var x) && b.TryGetValue(k, out var y) && x == y))
            .Where(k => !own.Any(o => k == o || k.EndsWith("." + o, StringComparison.Ordinal))).ToList();
        Check(differ.Count == 0 && a.Count > 0, $"{what}: {a.Count} live leaves, {differ.Count} differ" +
            (differ.Count == 0 ? "" : ": " + string.Join(", ", differ.Take(12).Select(k => $"{k} {a.GetValueOrDefault(k)} / {b.GetValueOrDefault(k)}"))));
    }

    private void LiveData()
    {
        SameLeaves(Content.Templates.Get("Typed_Bolt_Strings"), Content.Templates.Get("Typed_Bolt_Typed"), "Bolt");
        SameLeaves(Content.Templates.Get("Typed_Vukah_Strings"), Content.Templates.Get("Typed_Vukah_Typed"), "Vukah");
        SameLeaves(Content.Templates.Get("Typed_Burst_Strings"), Content.Templates.Get("Typed_Burst_Typed"), "Burst");
        SameLeaves(Content.Templates.Get("Typed_Burst_Code"), Content.Templates.Get("Typed_Burst_Typed"), "Burst, strings in code");
        SameLeaves(Content.Buffs.Get("Typed_Buff_Strings"), Content.Buffs.Get("Typed_Buff_Typed"), "Buff", "buffTypeId");
        SameLeaves(Content.Perks.Get("Typed_Perk_Strings"), Content.Perks.Get("Typed_Perk_Typed"), "Perk");
        SameLeaves(Content.Sequences.Get("Typed_Cast_Strings"), Content.Sequences.Get("Typed_Cast_Typed"), "Cast");
        SameItems(Content.Items.Get("Typed_Sword_Strings"), Content.Items.Get("Typed_Sword_Typed"), "Sword", "itemId", "objectId", "debugName");
    }

    // Items through kiln.content's schema: kiln.gamedata's list has what was there when it was made, not an item from a data pack.
    private void SameItems(ContentEntry? s, ContentEntry? t, string what, params string[] own)
    {
        if (s is null || t is null)
        {
            Check(false, $"{what}: an entry is missing ({s} / {t})");
            return;
        }
        var fields = Content.Items.Fields;
        var differ = fields.Select(f => f.Path).Where(p => !own.Contains(p) && s.Get(p) != t.Get(p)).ToList();
        Check(differ.Count == 0 && fields.Count > 0, $"{what}: {fields.Count} live leaves, {differ.Count} differ" +
            (differ.Count == 0 ? "" : ": " + string.Join(", ", differ.Take(12).Select(p => $"{p} {s.Get(p)} / {t.Get(p)}"))));
    }

    // ---- typed reads and writes on the game's entries ----

    private void Writes()
    {
        var sword = Content.Items.Get(GameItems.Weapon_T1_1H_SwordEpic_01)!;
        var dmg = GameFields.Items.DamageSetup.DmgMod;
        var original = sword.Original(dmg);
        Check(sword.GetNumber(dmg.Path) == (double)original || Math.Abs(sword.GetNumber(dmg.Path) - original) < 1e-6, $"float: typed read {original} = string read {sword.GetNumber(dmg.Path)}");
        sword.Set(dmg, 2.5f);
        Check(sword.GetNumber("damageSetup.dmgMod") == 2.5 && sword.Get(dmg) == 2.5f, "float: a typed write is what the string read sees");
        sword.Set("damageSetup.dmgMod", 3.5);
        Check(sword.Get(dmg) == 3.5f, "float: a string write is what the typed read sees");
        Check(sword.Reset(dmg) && sword.Get(dmg) == original, $"float: Reset gives the original {original} back");

        var stack = GameFields.Items.MaxStackSize;
        var stackWas = sword.Get(stack);
        sword.Set(stack, 5);
        Check(sword.GetNumber("maxStackSize") == 5 && sword.Get(stack) == 5, "ushort: a plain 5 fits the field");
        sword.Reset(stack);
        Check(sword.Get(stack) == stackWas, $"ushort: back to {stackWas}");

        var rarity = GameFields.Items.Rarity;
        var rarityWas = sword.Get(rarity);
        Check(sword.Get("rarity").Name == rarityWas.ToString(), $"enum: typed read {rarityWas} = string read {sword.Get("rarity").Name}");
        sword.Set(rarity, GameEnums.ItemRarity.Legendary);
        Check(sword.Get("rarity").Name == "Legendary" && sword.Get(rarity) == GameEnums.ItemRarity.Legendary, "enum: a typed write is what the string read sees");
        sword.Reset(rarity);
        Check(sword.Get(rarity) == rarityWas, "enum: back");

        var mask = GameFields.Items.DisableRarityGeneration;
        var maskWas = sword.Get(mask);
        sword.Set(mask, GameEnums.ItemRarityMask.Common | GameEnums.ItemRarityMask.Epic);
        Check(sword.Get(mask) == (GameEnums.ItemRarityMask.Common | GameEnums.ItemRarityMask.Epic) && sword.Get("disableRarityGeneration").Name == "Common,Epic",
            $"flags: a typed write reads back as {sword.Get("disableRarityGeneration").Name}");
        sword.Reset(mask);
        Check(sword.Get(mask) == maskWas, "flags: back");

        // through kiln.gamedata: a flag set, a time, a reference, a config by name, an index, every element
        var data = sword.Data();
        data.Set(mask, GameEnums.ItemRarityMask.Rare);
        Check(data.Get(mask) == GameEnums.ItemRarityMask.Rare, $"flags through kiln.gamedata: reads back as {data.Get("disableRarityGeneration").Name}");
        data.Reset(mask);

        var buff = Content.Buffs.Get("0x5f425faf")!;
        var life = GameFields.Buffs.DefaultLifeTime;
        var lifeWas = buff.Get(life);
        buff.Set(life, TimeSpan.FromSeconds(42));
        Check(buff.GetNumber("defaultLifeTime") == 42 && buff.Get(life) == TimeSpan.FromSeconds(42), $"time: {lifeWas} -> {buff.Get(life)}");
        buff.Reset(life);
        Check(buff.Get(life) == lifeWas, "time: back");

        var proj = Content.Templates.Get(GameTemplates.Projectile_Player_Staff_IceBolt_1)!;
        var spawn = GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnTemplate;
        var spawnWas = proj.Get(spawn);
        proj.Set(spawn, GameTemplates.Explosion_Player_Spell_Fireball_1);
        var target = Content.Templates.Get(GameTemplates.Explosion_Player_Spell_Fireball_1)!.Guid;
        Check(proj.Get(spawn.Path).Guid == target && Guid.Parse(proj.Get(spawn)) == target, $"reference: a config names another template: {spawnWas} -> {proj.Get(spawn)}");
        proj.Reset(spawn);
        Check(proj.Get(spawn) == spawnWas, "reference: back");

        var ammo = Content.ItemData.Get(GameItems.Ammo_T1_Spell_Fire_Ball_lvl10)!;
        var mana = GameFields.Items.ImpactValues.ManaCost;
        var manaWas = ammo.Get(mana);
        Check(ammo.Original(mana) == (int)ammo.Original(mana.Path).AsDouble, $"config: original {manaWas}");
        ammo.Set(mana, 7);
        Check(ammo.GetNumber("impactValues.ManaCost") == 7 && ammo.Get(mana) == 7, "config by name: a typed write is what the string read sees");
        ammo.Reset(mana);

        var vukah = Content.Templates.Get(GameTemplates.Enemy_Vukah_Melee)!;
        var level = GameFields.Templates.Level.DataStorage[0];
        Check(level.Path == "Level.dataStorage[0]" && vukah.Get(level) == (int)vukah.GetNumber("Level.dataStorage[0]"), $"index: {level.Path} = {vukah.Get(level)}");
        var view = GameFields.Templates.Enemy01BehaviorState.AggroSetup.ViewRange;
        var viewWas = vukah.Get(view);
        Check(viewWas == (float)vukah.GetNumber(view.Path), $"template component field: viewRange {viewWas}");

        var perks = Content.Perks.Get(GamePerks.Perk_OnDamage_Dealt_Debuff_Poisoned_T2)!;
        var rows = perks.GetJson("uiValues");
        Check(perks.Get(GameFields.Perks.UiValues.Self) is not null, $"a list as one value: {rows?.ToJsonString().Length ?? 0} bytes of JSON");

        // Below: what a list of variants holds, past what the catalog lists
        var cast = Content.Sequences.Get(GameSequences.Attack_Player_Staff_FixedAnim_Fireball_fast)!;
        var spawnEvent = GameFields.Sequences.Events["SpawnEntityEvent#0"].Below<TimeSpan>("time");
        Check(spawnEvent.Path == "events[SpawnEntityEvent#0].time" && cast.Get(spawnEvent) == TimeSpan.FromSeconds(cast.GetNumber(spawnEvent.Path)),
            $"a selector and Below: {spawnEvent.Path} = {cast.Get(spawnEvent)}");
    }

    private void Refusals()
    {
        var other = GameFields.Items.MaxStackSize;   // through a local, so the analyzer leaves it to the run-time check
        var vukah = Content.Templates.Get(GameTemplates.Enemy_Vukah_Melee)!;
        try
        {
            vukah.Set(other, 5);
            Check(false, "a field of items on a template was accepted");
        }
        catch (ArgumentException e)
        {
            Check(e.Message.Contains("field of items"), $"a field of another section is refused: {e.Message}");
        }
        try
        {
            Content.Buffs.Define(new GameDataClone
            {
                Guid = Guid.NewGuid(),
                Name = "Typed_Bad",
                Base = "0x5f425faf",
                Values = { { GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(1) } },
            });
            Check(false, "a template field on a buff clone was accepted");
        }
        catch (ArgumentException e)
        {
            Check(e.Message.Contains("field of templates"), $"a clone with a field of another section is refused: {e.Message}");
        }
    }

    // ---- what the analyzer must accept ----

    /// <summary>Every path kiln.gamedata and kiln.content report: <c>Kiln.GameIds.Tests</c> runs them through the check.</summary>
    private void LivePaths()
    {
        var sections = new (string Name, GameDataRegistry Reg, int Step)[]
        {
            ("items", Content.ItemData, 7), ("templates", Content.Templates, 13), ("sequences", Content.Sequences, 5),
            ("buffs", Content.Buffs, 1), ("perks", Content.Perks, 1), ("skills", Content.Skills, 1),
        };
        var paths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> Of(string section) => paths.TryGetValue(section, out var set) ? set : paths[section] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, reg, step) in sections)
            for (var i = 0; i < reg.Count; i += step)
                foreach (var f in reg[i].Fields())
                    Of(name).Add(f.Path);
        // kiln.content's flat schema takes these on ContentEntry
        foreach (var (name, reg) in new[] { ("items", Content.Items), ("recipes", Content.Recipes), ("loot", Content.Loot) })
            foreach (var f in reg.Fields)
                Of(name).Add(f.Path);
        var root = new JsonObject();
        foreach (var (name, set) in paths)
            root[name] = new JsonArray(set.OrderBy(p => p, StringComparer.Ordinal).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        var total = paths.Values.Sum(p => p.Count);
        Directory.CreateDirectory(Context.DataDirectory);
        var file = Path.Combine(Context.DataDirectory, "live-paths.json");
        File.WriteAllText(file, root.ToJsonString());
        Check(total > 1000, $"{total} distinct live paths written to live-paths.json");
    }
}
