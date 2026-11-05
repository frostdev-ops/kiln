// SPDX-License-Identifier: MIT
// Item blocks -> what the services take: an item definition, its recipe, its loot; config values and tooltip rows as rules; a cast or use sequence where the item
// needs its own. Which game item a block starts from is chosen here, never by the author. What it derives:
//  - the crafted-item rule: the tooltip reads rarity and itemLevelRange.minLevel, a craft uses generateRarity and a level roll, so gear gets generateRarity off and one
//    level, and the perk slot its rarity needs (docs/kiln/services/content.md "Rarity, level and perks of new gear"; lint KL406).
//  - tooltip values: the game's own formulas are solved for the number written (damage: Compiler.DmgModFor; armour: below).
//  - stack sizes, and what clients it takes (Kind picks; a PNG icon or a model of the mod's own takes clients "mod").
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    // The game's armour: round((gradient * level + offset) * distribution[kind]) (client 0xe6dfe0, keen::BalancingTable +0x2a0 armorSetup). Checked at ready (ItemChecks).
    internal static float ArmorOffset = 23, ArmorGradient = 15;
    private const uint ConfigLevel = 0x17147b63, RowDuration = 0x86a75e07, RowHealth = 0xf38b388c, RowItemLevel = 0x88fbd16d;
    private const string ConfigStamina = "StaminaCost";

    internal static double ArmorShare(int armor, int level) => armor / (ArmorGradient * level + ArmorOffset);

    /// <summary>The blocks of this file. Null: not one of them.</summary>
    private static BlockHandle? DefineItemBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock => block switch
    {
        WeaponBlock w => DefineWeapon(ctx, low, name, w),
        WandBlock w => DefineWand(ctx, low, name, w),
        ArmorBlock a => DefineArmor(ctx, low, name, a),
        ItemBlock i => DefineItem(ctx, low, name, i),
        StationBlock s => DefineStation(ctx, low, name, s),
        VisualBlock v => Part(ctx, low, name, v, CheckVisual),
        OnHitBlock h => Part(ctx, low, name, h, CheckHits),
        WeaponStats s => Part(ctx, low, name, s, CheckWeaponStats),
        ArmorStats s => Part(ctx, low, name, s, CheckArmorStats),
        WeaponClass c => Part(ctx, low, name, c, (x, b) => ClassOf(x, b, "like")),
        ArmorSlot c => Part(ctx, low, name, c, (x, b) => SlotOf(x, b, "like")),
        ArmorSet s => Part(ctx, low, name, s, null),
        _ => null,
    };

    // A part has no resource of its own: it is checked where it was written, and compiled where it is used.
    private static BlockHandle Part<T>(BlockContext ctx, Lowered low, string name, T part, Action<BlockContext, T>? check) where T : class, IBlock
    {
        check?.Invoke(ctx, part);
        low.Json["part"] = name;
        return new BlockHandle<T>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "part", name), name, part);
    }

    // ---- what every item has -----------------------------------------------------------------------------------

    /// <summary>An item being put together.</summary>
    private sealed class Draft(BlockContext ctx, Lowered low, string key, string name, string starter)
    {
        public readonly BlockContext Ctx = ctx;
        public readonly Lowered Low = low;
        public readonly string Key = key, Name = name, Starter = starter;
        public readonly string Game = $"{ctx.Mod.Id}.{name}";
        public readonly Guid Guid = BlockIds.Derive(ctx.Mod.Id, "item", key);
        public readonly Dictionary<string, object> Fields = new();
        public readonly Dictionary<string, string> Refs = new();
        public readonly JsonObject Rules = new();
        /// <summary>References by name or to a clone: kiln.gamedata's item paths, when content is ready.</summary>
        public readonly JsonObject DataRules = new();
        /// <summary>The tooltip's rows, replacing the starter's. Applied before the configs: a config's change follows to a row of the list it finds, so a config that had a row
        /// only in the starter's list must not leave a value behind for it.</summary>
        public JsonArray? Rows;
        public string? Model, Icon;
        public string?[]? Perks;
        public ItemLoot? Loot;
        public List<ItemDrop>? Drops;
        public bool Rolled;
        public IReadOnlyDictionary<string, ItemTexts>? Texts;

        public void Commit(string displayName, string? description)
        {
            Low.Item(new ItemDefinition
            {
                Guid = Guid, Name = Game, BaseOn = Starter, DisplayName = displayName, Description = description, Fields = Fields, Refs = Refs,
                Model = Model, Icon = Icon, Perks = Perks, Loot = Loot, Drops = Drops, RolledLoot = Rolled, Texts = Texts,
            });
            var parts = new JsonObject();
            if (Model != null) parts["model"] = Model;
            if (Icon != null) parts["icon"] = Icon;
            if (Perks != null) parts["perks"] = new JsonArray(Perks.Select(p => (JsonNode?)(p == null ? null : JsonValue.Create(p))).ToArray());
            if (Loot != null) parts["loot"] = new JsonObject { ["like"] = Loot.Like, ["stackMin"] = Loot.StackMin, ["stackMax"] = Loot.StackMax };
            if (Drops != null) parts["drops"] = new JsonArray(Drops.Select(d => (JsonNode)new JsonObject { ["table"] = d.Table, ["countMin"] = d.CountMin, ["countMax"] = d.CountMax, ["chance"] = d.Chance, ["weight"] = d.Weight }).ToArray());
            if (Rolled) parts["rolled"] = true;
            if (Texts != null) parts["texts"] = new JsonArray(Texts.Keys.Order().Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
            if (parts.Count > 0) Low.Json["itemParts"] = parts;
            var rules = new JsonObject();
            if (Rows != null) rules["uiValues"] = Rows;
            foreach (var (k, v) in Rules) rules[k] = v?.DeepClone();
            Low.ItemRules(Game, rules);     // also what makes kiln.content's words about this item come back to the author, by name, when content is ready
            if (DataRules.Count > 0)
            {
                Low.AfterCommit(() => BlockRuntime.AddRules(Ctx.Mod, "itemdata", Game, DataRules));
                Low.Json["itemDataRules"] = DataRules.DeepClone();
            }
            ItemChecks.Once(Low);
        }
    }

    private static string RequireName(BlockContext ctx, string name, string what) =>
        string.IsNullOrWhiteSpace(name) ? throw new BlockError(ctx.Block, "name", $"{what} needs a name players can read") : name;

    private static void CheckLevel(BlockContext ctx, int level, string path)
    {
        if (level is < 1 or > 55) throw new BlockError(ctx.Block, path, $"{level} is outside the game's item levels 1..55");
    }

    // ---- the crafted-item rule ----------------------------------------------------------------------------------

    /// <summary>Gear is made exactly as its tooltip shows: one level, one rarity. <c>Rolled</c> is the opt-out for loot that scales, and it refuses a recipe, whose tooltip would then lie.</summary>
    private static void Gear(Draft d, Rarity rarity, int level, LootBlock? loot, RecipeBlock? recipe)
    {
        if (loot is { Rolled: true })
        {
            if (recipe != null) throw new BlockError(d.Ctx.Block, "loot.rolled", "a rolled item comes out of a craft at the lowest rarity and a random level, while the recipe tooltip shows otherwise: give it a recipe or roll it, not both (KL406)");
            d.Rolled = true;
            d.Fields["rarity"] = rarity.ToString();
            d.Fields["generateRarity"] = true;
            d.Fields["itemLevelRange.minLevel"] = level;
            d.Fields["itemLevelRange.maxLevel"] = 55;
            return;
        }
        d.Fields["rarity"] = rarity.ToString();
        d.Fields["generateRarity"] = false;
        d.Fields["itemLevelRange.minLevel"] = level;
        d.Fields["itemLevelRange.maxLevel"] = level;
    }

    /// <summary>Enhancement slots. An item's rarity is its perk count less one, so the slot its rarity needs must be filled, or it comes out a rank lower.</summary>
    private static void PerkSlots(Draft d, IReadOnlyList<PerkRef>? perks, Rarity rarity, int starterSlots, string? perksFrom, string path, string rarityPath)
    {
        var slots = new string?[5];
        var any = false;
        if (perks != null)
        {
            if (perks.Count > 5) throw new BlockError(d.Ctx.Block, path, $"{perks.Count} perks: an item has 5 enhancement slots");
            for (var i = 0; i < perks.Count; i++)
            {
                if (perks[i].Value is not { Length: > 0 } v) continue;
                if (v == "none" || v.StartsWith("item:", StringComparison.Ordinal) || Guid.TryParse(v, out _))
                {
                    slots[i] = v;
                    any = true;
                }
                else d.DataRules[$"perkReferences[{i}]"] = v;   // a perk by name resolves when content is ready, a clone's too
            }
        }
        var top = (int)rarity;
        for (var i = 0; perks != null && i < perks.Count && i <= top; i++)
            if (perks[i].Value == "none") throw new BlockError(d.Ctx.Block, $"{path}[{i}]", $"{rarity} has {top + 1} enhancements, so slot {i} can't be empty: it would come out a rank lower");
        for (var i = starterSlots; i <= top; i++)
        {
            var mine = perks != null && i < perks.Count && perks[i].Value is { Length: > 0 } v && v != "none";
            if (mine) continue;
            if (perksFrom == null)
                throw new BlockError(d.Ctx.Block, rarityPath, $"{rarity} needs {top + 1} enhancement slots and this has {starterSlots}: name a perk for slot {i} in perks, or lower the rarity");
            slots[i] = "item:" + perksFrom;
            any = true;
        }
        if (any) d.Perks = slots;
    }

    // ---- the parts ----------------------------------------------------------------------------------------------

    private static void CheckVisual(BlockContext ctx, VisualBlock v)
    {
        if (v.Vfx != null) throw NoVfxYet(ctx, "vfx");
        if (v.Tint is { } t && !IsColour(t)) throw new BlockError(ctx.Block, "tint", $"\"{t}\" is not a colour: #rrggbb");
    }

    private static bool IsColour(string t) => t.Length == 7 && t[0] == '#' && t.Skip(1).All(Uri.IsHexDigit);

    private static void CheckHits(BlockContext ctx, OnHitBlock h)
    {
        if (h.Vfx != null) throw NoVfxYet(ctx, "vfx");
    }

    private static void CheckWeaponStats(BlockContext ctx, WeaponStats s)
    {
        CheckLevel(ctx, s.Level, "level");
        if (s.Power is < 0) throw new BlockError(ctx.Block, "power", $"{s.Power} is less than 0");
        if (s.Block is < 0 or > 1) throw new BlockError(ctx.Block, "block", $"{s.Block} is outside 0..1");
        if (s.StaminaCost is < 0) throw new BlockError(ctx.Block, "staminaCost", $"{s.StaminaCost} is less than 0");
    }

    private static void CheckArmorStats(BlockContext ctx, ArmorStats s)
    {
        CheckLevel(ctx, s.Level, "level");
        if (s.Physical is < 0) throw new BlockError(ctx.Block, "physical", $"{s.Physical} is less than 0");
        if (s.Magical is < 0) throw new BlockError(ctx.Block, "magical", $"{s.Magical} is less than 0");
    }

    private static void ApplyVisual(Draft d, PartRef<VisualBlock>? part, string path)
    {
        if (part == null) return;
        var ctx = d.Ctx;
        var v = ctx.Part(part, path);
        if (v.Vfx != null) throw NoVfxYet(ctx, $"{path}.vfx");
        if (v.Like is { } like)
        {
            var n = ctx.ItemName(like, $"{path}.like");
            d.Model = "item:" + n;
            d.Icon = "item:" + n;
        }
        if (v.Model is { } model)
        {
            if (model.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                throw new BlockError(ctx.Block, $"{path}.model", $"\"{model}\": a glTF is cooked before it can be named here: add a model step to assets/cook.json, serve it from assets/assets.json, and give its GUID (docs/kiln/modding/models.md)");
            d.Model = model;
            if (Guid.TryParse(model, out _)) ctx.Need(ClientsLevel.Mod, $"{path}.model", "a model of the mod's own has to be on every player's game: nothing can send it (an item's look, \"item:<name>\", asks no more than kiln)");
        }
        if (v.Icon is { } icon)
        {
            d.Icon = icon;
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ctx.Need(ClientsLevel.Mod, $"{path}.icon", "a PNG icon has to be on every player's game: nothing can send it (an item's icon, \"item:<name>\", asks no more than kiln)");
        }
        if (v.Dropped is { } dropped) d.Refs["iconModel"] = "item:" + ctx.ItemName(dropped, $"{path}.dropped");
        if (v.Tint is { } t)
        {
            if (!IsColour(t)) throw new BlockError(ctx.Block, $"{path}.tint", $"\"{t}\" is not a colour: #rrggbb");
            var rgba = 0xff000000u | Convert.ToUInt32(t[1..], 16);
            d.Fields["itemColorCombinationSetup.color0"] = (double)rgba;
            d.Fields["itemColorCombinationSetup.isSet"] = true;
        }
    }

    private static void ApplyLoot(Draft d, LootBlock? l)
    {
        if (l == null) return;
        var ctx = d.Ctx;
        if (l.StackMin is < 1) throw new BlockError(ctx.Block, "loot.stackMin", $"{l.StackMin} is less than 1");
        if (l.StackMax < l.StackMin) throw new BlockError(ctx.Block, "loot.stackMax", $"{l.StackMax} is less than stackMin {l.StackMin}");
        if (l.Like is { } like) d.Loot = new ItemLoot { Like = ctx.ItemName(like, "loot.like"), StackMin = l.StackMin, StackMax = l.StackMax };
        else if (l.StackMin != null || l.StackMax != null) throw new BlockError(ctx.Block, "loot.like", "stack sizes are for random loot: say which item's rolls it joins");
        if (l.Tables is not { Count: > 0 } tables)
        {
            if (l.Like == null && !l.Rolled) throw new BlockError(ctx.Block, "loot", "this loot goes nowhere: give it like, or tables");
            return;
        }
        d.Drops = new();
        for (var i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            var p = $"loot.tables[{i}]";
            if ((t.Table == null) == (t.DropsLike == null)) throw new BlockError(ctx.Block, p, "say table (a table's GUID) or dropsLike (an item), one of them");
            if (t.CountMin < 1) throw new BlockError(ctx.Block, $"{p}.countMin", $"{t.CountMin} is less than 1");
            if (t.CountMax < t.CountMin) throw new BlockError(ctx.Block, $"{p}.countMax", $"{t.CountMax} is less than countMin {t.CountMin}");
            if (t.Chance is < 0 or > 1) throw new BlockError(ctx.Block, $"{p}.chance", $"{t.Chance} is outside 0..1");
            if (t.Weight < 0) throw new BlockError(ctx.Block, $"{p}.weight", $"{t.Weight} is less than 0");
            d.Drops.Add(new ItemDrop(t.Table ?? "like:" + ctx.ItemName(t.DropsLike!.Value, $"{p}.dropsLike")) { CountMin = t.CountMin, CountMax = t.CountMax, Chance = t.Chance, Weight = t.Weight });
        }
    }

    // ---- recipes ------------------------------------------------------------------------------------------------

    private static void DefineRecipe(BlockContext ctx, Lowered low, string key, string item, RecipeBlock r)
    {
        if (r.Makes is < 1 or > 65535) throw new BlockError(ctx.Block, "recipe.makes", $"{r.Makes} is outside 1..65535");
        if (r.Needs.Count == 0) throw new BlockError(ctx.Block, "recipe.needs", "a recipe that needs nothing is not listed by the game");
        if (r.Seconds is <= 0) throw new BlockError(ctx.Block, "recipe.seconds", $"{r.Seconds} must be more than 0");
        var inputs = new List<RecipeIngredient>();
        foreach (var (what, count) in r.Needs)
        {
            if (count < 1) throw new BlockError(ctx.Block, $"recipe.needs[{what}]", $"{count} is less than 1");
            inputs.Add(new RecipeIngredient(ctx.ItemName(what, $"recipe.needs[{what}]"), count));
        }
        var (_, station) = ctx.Station(r.Station, "recipe.station");
        low.Recipe(new RecipeDefinition
        {
            Guid = BlockIds.Derive(ctx.Mod.Id, "recipe", key), Name = item + ".recipe", Station = station, Inputs = inputs,
            Outputs = [new RecipeIngredient(item, r.Makes)], Duration = r.Seconds is { } d ? TimeSpan.FromSeconds(d) : null,
        });
    }

    // ---- weapons ------------------------------------------------------------------------------------------------

    private static WeaponClass ClassOf(BlockContext ctx, WeaponClass c, string path)
    {
        if (c.Like == null)
            return c.Starter != null ? c : throw new BlockError(ctx.Block, path, $"a class starts from a premade: {string.Join(", ", WeaponClass.All.Select(x => x.Key))}");
        var start = WeaponClass.All.FirstOrDefault(x => x.Key == c.Like)
                   ?? throw new BlockError(ctx.Block, path, $"\"{c.Like}\" is not a class; there are {string.Join(", ", WeaponClass.All.Select(x => x.Key))}");
        return start with { Visual = c.Visual ?? start.Visual, Stats = c.Stats ?? start.Stats, Perks = c.Perks ?? start.Perks };
    }

    private static void WeaponFields(Draft d, WeaponClass cls, WeaponStats stats, IReadOnlyList<PerkRef>? perks, PartRef<VisualBlock>? visual, LootBlock? loot, RecipeBlock? recipe, DamageBlock? damage, int? power)
    {
        var ctx = d.Ctx;
        CheckLevel(ctx, stats.Level, "stats.level");
        if (power is < 0) throw new BlockError(ctx.Block, "stats.power", $"{power} is less than 0");
        if (stats.Block is < 0 or > 1) throw new BlockError(ctx.Block, "stats.block", $"{stats.Block} is outside 0..1");
        Gear(d, stats.Rarity, stats.Level, loot, recipe);
        if (damage != null)
        {
            AddDamageFields(d.Fields, damage, stats.Level);
            if (damage.Total <= 0 && power == null) throw new BlockError(ctx.Block, "stats.damage", "none of it hurts: give it a kind that does, or a power");
        }
        if (power is { } p)
        {
            d.Fields["damageSetup.speedMod"] = 1.0;
            d.Fields["damageSetup.dmgMod"] = DmgModFor(p, stats.Level);
        }
        if (stats.Block is { } block) d.Fields["blockSetup.quality"] = block;
        if (stats.StaminaCost is { } stamina)
        {
            if (stamina < 0) throw new BlockError(ctx.Block, "stats.staminaCost", $"{stamina} is less than 0");
            d.Rules[$"impactValues.{ConfigStamina}"] = stamina;
        }
        // a roll can give it any rarity, so every slot has to be filled
        PerkSlots(d, perks ?? cls.Perks, loot is { Rolled: true } ? Rarity.Legendary : stats.Rarity, cls.PerksFrom != null ? 1 : 5, cls.PerksFrom, "perks", "stats.rarity");
        ApplyVisual(d, visual ?? cls.Visual, visual != null ? "visual" : "class.visual");
        ApplyLoot(d, loot);
    }

    private static BlockHandle DefineWeapon(BlockContext ctx, Lowered low, string name, WeaponBlock w)
    {
        RequireName(ctx, w.Name, "a weapon");
        var cls = ClassOf(ctx, ctx.Part(w.Class, "class"), "class");
        if (cls.Key == WeaponClass.Wand.Key) throw new BlockError(ctx.Block, "class", "a wand's attacks are spells of its own: use a wand block (wands in JSON), with its primary attack");
        var key = w.Was ?? name;
        var d = new Draft(ctx, low, key, name, cls.Starter!);
        var stats = w.Stats != null ? ctx.Part(w.Stats, "stats") : cls.Stats != null ? ctx.Part(cls.Stats, "class.stats") : new WeaponStats();
        var power = stats.Power ?? (stats.Damage != null ? (int)Math.Round(stats.Damage.Total) : null);
        WeaponFields(d, cls, stats, w.Perks, w.Visual, w.Loot, w.Recipe, stats.Damage, power);
        d.Commit(w.Name, w.Description);
        Hosted(ctx, low, key, d.Guid, d.Game, ctx.Hits(w.OnHit, "onHit"), ctx.Hits(w.OnKill, "onKill"), []);
        if (w.Recipe != null) DefineRecipe(ctx, low, key, d.Game, w.Recipe);
        return new BlockHandle<WeaponBlock>(ctx.Mod.Id, name, d.Guid, d.Game, w);
    }

    // ---- wands --------------------------------------------------------------------------------------------------

    // The item's sequence slots on a wand starter: Primary, SpecialAbility, Primary while evading, Heavy while airborne, Heavy.
    private const int WandPrimary = 0, WandSpecial = 1, WandEvading = 2, WandAirborne = 3, WandHeavy = 4;

    private static BlockHandle DefineWand(BlockContext ctx, Lowered low, string name, WandBlock w)
    {
        RequireName(ctx, w.Name, "a wand");
        var cls = w.Class != null ? ClassOf(ctx, ctx.Part(w.Class, "class"), "class") : WeaponClass.Wand;
        var key = w.Was ?? name;
        var mod = ctx.Mod.Id;
        var d = new Draft(ctx, low, key, name, cls.Starter!);
        var stats = w.Stats != null ? ctx.Part(w.Stats, "stats") : cls.Stats != null ? ctx.Part(cls.Stats, "class.stats") : new WeaponStats();

        var first = LowerWandAttack(ctx, low, key, name, "primary", w.Primary, WandMoves.Bolt, main: true);
        var primary = first.Sequence;
        var heavy = w.Heavy != null ? LowerWandAttack(ctx, low, key, name, "heavy", w.Heavy, WandMoves.Heavy, main: false).Sequence : primary;
        var special = w.Special != null ? LowerWandAttack(ctx, low, key, name, "special", w.Special, WandMoves.Special, main: false).Sequence : primary;
        var mainHit = first.Emitter.PrimaryDamage;

        // A real cast's main damage comes from the item, not the program (docs/re/abilities.md §2): the wand's power is its primary's number, in its kinds.
        var hurts = (int)Math.Round(mainHit?.Total ?? 0);
        if (stats.Power is { } said && mainHit != null && said != hurts)
            throw new BlockError(ctx.Block, "stats.power", $"{said}, while the primary attack's main damage says {hurts}: a wand's power is that number. Leave power out, or make them the same");
        var power = stats.Power ?? (mainHit != null ? hurts : null);
        WeaponFields(d, cls, stats, w.Perks, w.Visual, w.Loot, w.Recipe, mainHit ?? stats.Damage, power);
        d.Rules[$"sequences[{WandPrimary}].sequence"] = primary;
        d.Rules[$"sequences[{WandEvading}].sequence"] = primary;
        d.Rules[$"sequences[{WandHeavy}].sequence"] = heavy;
        d.Rules[$"sequences[{WandAirborne}].sequence"] = heavy;
        d.Rules[$"sequences[{WandSpecial}].sequence"] = special;
        // What the starter's rows said is the starter's: the tooltip says what this wand's primary does. The mana regeneration it gave is not this block's.
        d.Rules["impactValues.0x4e133b42"] = 0;
        var rows = new JsonArray { Row(RowMana, "Normal", w.Primary.Cast.ManaCost) };
        if (first.Emitter.FirstRadius is { } r) rows.Add(Row(RowRadius, "Float", (int)Math.Round(r * 100)));
        if (first.Emitter.FirstStatusSeconds is { } secs) rows.Add(Row(RowDebuffDuration, "Duration", (int)Math.Round(secs * 1000)));
        d.Rows = rows;
        d.Commit(w.Name, w.Description);
        Hosted(ctx, low, key, d.Guid, d.Game, ctx.Hits(w.OnHit, "onHit"), ctx.Hits(w.OnKill, "onKill"), []);
        if (w.Recipe != null) DefineRecipe(ctx, low, key, d.Game, w.Recipe);
        return new BlockHandle<WandBlock>(mod, name, d.Guid, d.Game, w);
    }

    /// <summary>One attack: its flight, and a clone of the motion that sends it. No ammo item: the sequence names the template itself, so the attack does not depend on what the
    /// cast's trigger context carries, as an empty SpawnEntityEvent would (docs/re/abilities.md §2).</summary>
    private static (string Sequence, EffectEmitter Emitter) LowerWandAttack(BlockContext ctx, Lowered low, string key, string name, string slot, WandAttack a, WandMoves motion, bool main)
    {
        var mod = ctx.Mod.Id;
        var moves = a.Moves ?? motion;
        if (a.Cast.ManaCost < 0) throw new BlockError(ctx.Block, $"{slot}.cast.manaCost", $"{a.Cast.ManaCost} is less than 0");
        if (a.Cast.ReleaseAt is <= 0) throw new BlockError(ctx.Block, $"{slot}.cast.releaseAt", $"{a.Cast.ReleaseAt} must be more than 0");
        if (a.Cast.Animation != CastAnimation.FastBolt) throw new BlockError(ctx.Block, $"{slot}.cast.animation", "that is a staff's: a wand's motion is its attack's moves");
        if (a.Cast.Vfx != null) throw NoVfxYet(ctx, $"{slot}.cast.vfx");
        var lowered = LowerDelivery(ctx, low, $"{key}.{slot}", $"{name}.{slot}", a.Delivery, $"{slot}.delivery", primary: main);
        var seqName = $"{mod}.{name}.{slot}";
        low.Clone("sequences", BlockIds.Derive(mod, "sequence", $"{key}.{slot}"), seqName, moves.Sequence, new JsonObject());
        // A clone can name another clone once both are registered: a rule, not a field. A time and a cost are ordinary values.
        var rules = new JsonObject();
        var template = lowered.PlacedEntity.ToString();
        foreach (var i in moves.Fires)
        {
            rules[$"events[SpawnEntityEvent#{i}].templateReference"] = template;
            rules[$"events[SpawnEntityEvent#{i}].ignoreSpawnVelocity"] = true;
            rules[$"events[SpawnEntityEvent#{i}].spawnVelocity.z"] = (double)lowered.Speed;
            rules[$"events[SpawnEntityEvent#{i}].requiredEntityTag"] = "none";
        }
        foreach (var i in moves.Silenced) rules[$"events[SpawnEntityEvent#{i}].chance"] = 0;
        if (a.Cast.ManaCost > 0) rules["events[PayUsageCost#0].impactValues.0xaf236ece"] = a.Cast.ManaCost;
        // Last, and all of the motion's shots, the silenced too, so that they keep their order: a time change re-sorts the events, and [Type#k] counts in the current order,
        // also where a client applies them. Moved later, the last goes first and each move leaves the earlier ones where they are; moved earlier, the other way round.
        if (a.Cast.ReleaseAt is { } at)
        {
            var shots = moves.Fires.Concat(moves.Silenced).Order();
            foreach (var i in at >= moves.ReleaseSeconds ? shots.Reverse() : shots) rules[$"events[SpawnEntityEvent#{i}].time"] = at;
        }
        low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "sequences", seqName, rules));
        (low.Json["sequenceRules"] ??= new JsonObject()).AsObject()[slot] = rules.DeepClone();
        return (seqName, lowered.Emitter);
    }

    // ---- armor --------------------------------------------------------------------------------------------------

    private static ArmorSlot SlotOf(BlockContext ctx, ArmorSlot c, string path)
    {
        if (c.Like == null)
            return c.Starter != null ? c : throw new BlockError(ctx.Block, path, $"a slot starts from a premade: {string.Join(", ", ArmorSlot.All.Select(x => x.Key))}");
        var start = ArmorSlot.All.FirstOrDefault(x => x.Key == c.Like)
                   ?? throw new BlockError(ctx.Block, path, $"\"{c.Like}\" is not a slot; there are {string.Join(", ", ArmorSlot.All.Select(x => x.Key))}");
        return start with { Visual = c.Visual ?? start.Visual, Stats = c.Stats ?? start.Stats };
    }

    private static BlockHandle DefineArmor(BlockContext ctx, Lowered low, string name, ArmorBlock a)
    {
        RequireName(ctx, a.Name, "a piece of armor");
        var slot = SlotOf(ctx, ctx.Part(a.Slot, "slot"), "slot");
        var key = a.Was ?? name;
        var stats = a.Stats != null ? ctx.Part(a.Stats, "stats") : slot.Stats != null ? ctx.Part(slot.Stats, "slot.stats") : new ArmorStats();
        CheckLevel(ctx, stats.Level, "stats.level");
        if (stats.Physical is < 0) throw new BlockError(ctx.Block, "stats.physical", $"{stats.Physical} is less than 0");
        if (stats.Magical is < 0) throw new BlockError(ctx.Block, "stats.magical", $"{stats.Magical} is less than 0");

        var d = new Draft(ctx, low, key, name, slot.Starter!);

        Gear(d, stats.Rarity, stats.Level, a.Loot, a.Recipe);
        void Armor(string field, int? n)
        {
            if (n is { } v) d.Fields[$"armorSetup.distribution.{field}"] = ArmorShare(v, stats.Level);
        }
        Armor("physical", stats.Physical);
        Armor("magical", stats.Magical);
        if (stats.Resist is { } r)
        {
            Armor("cut", r.Cut); Armor("pierce", r.Pierce); Armor("blunt", r.Blunt);
            Armor("fire", r.Fire); Armor("ice", r.Ice); Armor("fog", r.Shroud); Armor("lightning", r.Lightning);
        }
        d.Rules[$"impactValues.0x{ConfigLevel:x8}"] = stats.Level;     // the tooltip's Item Level row follows it

        // What the starter gives that the block does not say is not given. The tooltip says what this piece does.
        var bonus = stats.Bonus?.ToList() ?? [];
        foreach (var c in slot.ClearConfigs)
            if (!bonus.Any(b => string.Equals(b.Key.Config, c, StringComparison.OrdinalIgnoreCase))) d.Rules[$"impactValues.{c}"] = 0;
        var rows = new JsonArray { Row($"0x{RowItemLevel:x8}", "Normal", stats.Level) };
        foreach (var (b, value) in bonus)
        {
            if (b.Whole && value != Math.Floor(value)) throw new BlockError(ctx.Block, $"stats.bonus[{b.Key}]", $"{value}: this bonus is a whole number");
            d.Rules[$"impactValues.{b.Config}"] = value;
            rows.Add(Row($"0x{b.Row:x8}", b.Format, (int)Math.Round(b.Format == "Percentage" ? value * 100 : value)));
        }
        d.Rows = rows;
        PerkSlots(d, a.Perks, stats.Rarity, 5, null, "perks", "stats.rarity");
        var look = a.Visual ?? slot.Visual ?? SetLook(ctx, a.Set, slot);
        ApplyVisual(d, look, "visual");
        ApplyLoot(d, a.Loot);
        d.Commit(a.Name, a.Description);
        Hosted(ctx, low, key, d.Guid, d.Game, [], [], ctx.Hits(a.OnHitTaken, "onHitTaken"));
        if (a.Recipe != null) DefineRecipe(ctx, low, key, d.Game, a.Recipe);
        return new BlockHandle<ArmorBlock>(ctx.Mod.Id, name, d.Guid, d.Game, a);
    }

    private static PartRef<VisualBlock>? SetLook(BlockContext ctx, PartRef<ArmorSet>? set, ArmorSlot slot)
    {
        if (set == null) return null;
        var s = ctx.Part(set, "set");
        return slot.Key switch { "head" => s.Head, "chest" => s.Chest, "arms" => s.Arms, "legs" => s.Legs, "feet" => s.Feet, _ => null };
    }

    // ---- items --------------------------------------------------------------------------------------------------

    private static ItemKind KindOf(ItemBlock b) => b.Kind
        ?? (b.Tool != null ? ItemKind.Tool : b.Ammo != null ? ItemKind.Ammo : b.Feeds != null ? ItemKind.Food : b.Heals != null || b.OnUse != null ? ItemKind.Consumable : ItemKind.Material);

    private static BlockHandle DefineItem(BlockContext ctx, Lowered low, string name, ItemBlock b)
    {
        RequireName(ctx, b.Name, "an item");
        var kind = KindOf(b);
        void Only(bool has, string field, params ItemKind[] kinds)
        {
            if (has && !kinds.Contains(kind)) throw new BlockError(ctx.Block, field, $"a {kind.Key} has no {field}: that is for a {string.Join(" or ", kinds.Select(x => x.Key))}");
        }
        Only(b.Heals != null, "heals", ItemKind.Consumable, ItemKind.Food);
        Only(b.OnUse != null, "onUse", ItemKind.Consumable, ItemKind.Food);
        Only(b.Feeds != null, "feeds", ItemKind.Food);
        Only(b.Fuel != null, "fuel", ItemKind.Material);
        Only(b.Comfort != null, "comfort", ItemKind.Material);
        Only(b.Tool != null, "tool", ItemKind.Tool);
        Only(b.Ammo != null, "ammo", ItemKind.Ammo);
        if (kind == ItemKind.Tool && b.Tool == null) throw new BlockError(ctx.Block, "tool", "a tool says what it harvests");
        if (kind == ItemKind.Food && b.Feeds == null) throw new BlockError(ctx.Block, "feeds", "food says what status eating it gives");
        if (kind == ItemKind.Consumable && b.Heals == null && b.OnUse == null) throw new BlockError(ctx.Block, "onUse", "using this would do nothing: give it heals or onUse");
        if (b.Heals is <= 0) throw new BlockError(ctx.Block, "heals", $"{b.Heals} must be more than 0");
        if (b.Fuel is <= 0) throw new BlockError(ctx.Block, "fuel", $"{b.Fuel} must be more than 0");
        if (b.Comfort is < 0) throw new BlockError(ctx.Block, "comfort", $"{b.Comfort} is less than 0");
        if (b.Stack is < 1 or > 65535) throw new BlockError(ctx.Block, "stack", $"{b.Stack} is outside 1..65535");
        if (kind == ItemKind.Tool && b.Stack is > 1) throw new BlockError(ctx.Block, "stack", "a tool has an item entity of its own: it does not stack");
        if (b.Level is { } level) CheckLevel(ctx, level, "level");

        var key = b.Was ?? name;
        var mod = ctx.Mod.Id;
        var healPotion = kind == ItemKind.Consumable && b.Heals != null;
        var starter = kind == ItemKind.Tool ? b.Tool!.Harvests.Starter : healPotion ? "Consumable_Potion_Health_T1" : kind.Starter;
        var d = new Draft(ctx, low, key, name, starter) { Texts = b.Texts };
        if (b.Stack is { } stack) d.Fields["maxStackSize"] = stack;
        ApplyVisual(d, b.Visual, "visual");
        ApplyLoot(d, b.Loot);

        if (kind == ItemKind.Tool)
        {
            var t = b.Tool!;
            var rarity = b.Rarity ?? Rarity.Common;
            var lvl = b.Level ?? 1;
            Gear(d, rarity, lvl, b.Loot, b.Recipe);
            PerkSlots(d, null, rarity, t.Harvests.Slots, null, "perks", "rarity");
            d.Rules[$"impactValues.0x{ConfigLevel:x8}"] = lvl;
            if (t.Power is { } p)
            {
                if (p <= 0) throw new BlockError(ctx.Block, "tool.power", $"{p} must be more than 0");
                var f = (double)p / t.Harvests.MainPower;
                d.Fields["damageSetup.distribution.woodDamage"] = (int)Math.Round(t.Harvests.WoodDamage * f);
                d.Fields["damageSetup.distribution.stoneDamage"] = (int)Math.Round(t.Harvests.StoneDamage * f);
                d.Fields["damageSetup.distribution.metalDamage"] = (int)Math.Round(t.Harvests.MetalDamage * f);
                if (t.Harvests.Digs)
                {
                    // How deep it digs by the hardness of what it hits (Pickaxe T1 to T7 in the game's own data: 80..100, 65..100, 50..100, 30..90, 10..80).
                    string[] hard = ["softDamage", "slightlyHardDamage", "moderatelyHardDamage", "hardDamage", "veryHardDamage"];
                    int[] at1 = [80, 65, 50, 30, 10];
                    for (var i = 0; i < 5; i++)
                    {
                        var dig = (int)Math.Min(100, Math.Round(at1[i] * f));
                        d.Rules[$"impactValues.0x928ebfe3.value.{hard[i]}"] = dig;
                        d.Rules[$"impactValues.0x6e279a98.value.{hard[i]}"] = dig * 7;
                    }
                }
            }
            if (t.StaminaCost is { } stamina)
            {
                if (stamina < 0) throw new BlockError(ctx.Block, "tool.staminaCost", $"{stamina} is less than 0");
                d.Rules[$"impactValues.{ConfigStamina}"] = stamina;
            }
        }
        else
        {
            if (b.Rarity is { } rarity) d.Fields["rarity"] = rarity.ToString();
            if (b.Loot is { Rolled: true }) throw new BlockError(ctx.Block, "loot.rolled", "only gear rolls a level and a rarity");
        }

        if (kind == ItemKind.Ammo)
        {
            var lvl = b.Level ?? 1;
            d.Fields["itemLevelRange.minLevel"] = lvl;
            d.Fields["itemLevelRange.maxLevel"] = lvl;
            if (b.Ammo is { } am)
            {
                if (am.Power is < 0) throw new BlockError(ctx.Block, "ammo.power", $"{am.Power} is less than 0");
                if (am.Damage != null) AddDamageFields(d.Fields, am.Damage, lvl);
                if (am.Power is { } p)
                {
                    d.Fields["damageSetup.speedMod"] = 1.0;
                    d.Fields["damageSetup.dmgMod"] = DmgModFor(p, lvl);
                }
                if (am.Delivery != null)
                {
                    var flight = LowerDelivery(ctx, low, key, name, am.Delivery, "ammo.delivery");
                    d.Refs["equipment.placedEntity"] = flight.PlacedEntity.ToString();
                    d.Fields["equipment.placedEntityLocalSpawnVelocity.z"] = (double)flight.Speed;
                }
            }
        }
        else if (b.Level is { } lv && kind != ItemKind.Tool)
        {
            d.Fields["itemLevelRange.minLevel"] = lv;
            d.Fields["itemLevelRange.maxLevel"] = lv;
        }

        if (b.Fuel is { } fuel)
        {
            d.Fields["fuelSetup.burnTime"] = fuel;
            d.Fields["fuelSetup.energyOutput"] = 1000;
            d.Fields["fuelSetup.isSet"] = true;
        }
        if (b.Comfort is { } comfort)
        {
            d.Fields["comfortSetup.comfortAmount"] = comfort;
            d.Fields["comfortSetup.isSet"] = true;
        }

        if (b.Heals is { } heals) d.Rules["impactValues.0xbbe4bf53"] = heals;     // HealthChange: the game's use programs read it, and the tooltip's Health row follows it
        var useSequence = kind == ItemKind.Food ? "UseItem_Food_Buff" : healPotion ? "UseItem_Potion_Health" : "UseItem_Potion_Antidote_RemovePoison";
        var clone = kind == ItemKind.Food || b.OnUse != null;
        var sequence = $"{mod}.{name}.use";
        var seqRules = new JsonObject();
        if (kind == ItemKind.Food)
        {
            var (status, seconds) = ctx.Status(b.Feeds!, "feeds");
            var buff = $"0x{BlockIds.Fnv(status):x8}";
            seqRules["events[ApplyBuffEvent#0].buffTypeOverride"] = status.ToString();
            d.DataRules["equipment.appliedBuff"] = status.ToString();
            var rows = new JsonArray { Row($"0x{RowDuration:x8}", "Duration", (int)Math.Round(seconds * 1000)) };
            if (b.Heals is { } hp) rows.Add(Row($"0x{RowHealth:x8}", "Normal", hp));
            d.Rows = rows;
            low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "buffs", buff, new JsonObject { ["slot"] = "Food" }));
        }
        if (clone)
        {
            var fields = new JsonObject();
            if (b.OnUse is { Count: > 0 } onUse)
            {
                var effects = onUse;
                if (b.Heals is { } h) effects = [new EffectBlock { Heal = h }, .. onUse];    // the program takes over from the game's, which read the config
                var (program, blob, _, listing) = EffectEmitter.SelfProgram(ctx, $"{key}.use", effects, "onUse");
                low.Program(program, $"{key}.use", blob, listing);
                fields["events[SpawnImpact#0].impact"] = program.ToString();
            }
            low.Clone("sequences", BlockIds.Derive(mod, "sequence", $"{key}.use"), sequence, useSequence, fields);
            if (seqRules.Count > 0) low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "sequences", sequence, seqRules));
            d.Rules["sequences[0].sequence"] = sequence;
        }

        d.Commit(b.Name, b.Description);
        if (b.Recipe != null) DefineRecipe(ctx, low, key, d.Game, b.Recipe);
        return new BlockHandle<ItemBlock>(mod, name, d.Guid, d.Game, b);
    }

    // ---- stations -----------------------------------------------------------------------------------------------

    private static BlockHandle DefineStation(BlockContext ctx, Lowered low, string name, StationBlock s)
    {
        RequireName(ctx, s.Name, "a station");
        var key = s.Was ?? name;
        var mod = ctx.Mod.Id;
        var d = new Draft(ctx, low, key, name, s.Kind.Item);
        // What is placed: a template of its own, with the kind's workshop. Its menu is that workshop's (a menu of its own needs a workshop the game's menu lists; docs/kiln/modding/item-blocks.md).
        var template = BlockIds.Derive(mod, "template", $"{key}.station");
        low.Clone("templates", template, $"{mod}.{name}.station", s.Kind.Template, new JsonObject());
        d.Refs["equipment.placedEntity"] = template.ToString();
        ApplyVisual(d, s.Visual, "visual");
        ApplyLoot(d, s.Loot);
        d.Commit(s.Name, s.Description);
        if (s.Recipe != null) DefineRecipe(ctx, low, key, d.Game, s.Recipe);
        return new BlockHandle<StationBlock>(mod, name, d.Guid, d.Game, s);
    }

    // ---- what follows the wielder ----------------------------------------------------------------------------------

    private static void Hosted(BlockContext ctx, Lowered low, string key, Guid item, string game, IReadOnlyList<EffectRef> onHit, IReadOnlyList<EffectRef> onKill, IReadOnlyList<EffectRef> onHitTaken)
    {
        if (onHit.Count == 0 && onKill.Count == 0 && onHitTaken.Count == 0) return;
        var (program, blob, _, listing) = EffectEmitter.WielderProgram(ctx, $"{key}.onhit", onHit, "onHit", onKill, "onKill", onHitTaken, "onHitTaken");
        low.Program(program, $"{key}.onhit", blob, listing);
        low.Json["onHitProgram"] = program.ToString();
        low.AfterCommit(() => BlockRuntime.AddWeapon(BlockIds.Fnv(item), program, game));
    }
}

/// <summary>The game numbers armour follows from: read once, and a game update that changes them is reported rather than silently changing what authors wrote.</summary>
internal static class ItemChecks
{
    private static bool _hooked;

    public static void Once(Lowered low)
    {
        low.AfterCommit(() =>
        {
            if (_hooked) return;
            _hooked = true;
            BlocksBridge.OnReady(() =>
            {
                try
                {
                    var b = Assets.ReadResource(new AssetKey(new Guid("82706b40-61b1-4b8f-8b23-dcec6971bda1"), "keen::BalancingTable"));
                    if (b is { Length: >= 0x2a8 })
                    {
                        var offset = BitConverter.ToSingle(b, 0x2a0);
                        var gradient = BitConverter.ToSingle(b, 0x2a4);
                        if (Math.Abs(offset - Compiler.ArmorOffset) > 1e-3 || Math.Abs(gradient - Compiler.ArmorGradient) > 1e-3)
                            BlocksBridge.Log(LogLevel.Warn, $"blocks: the game's armour curve changed (offset {offset}, gradient {gradient}; Kiln.Blocks was made for {Compiler.ArmorOffset}, {Compiler.ArmorGradient}): tooltips of armor blocks are off until Kiln follows");
                    }
                }
                catch (Exception e)
                {
                    BlocksBridge.Log(LogLevel.Warn, $"blocks: cannot read the game's armour curve: {e.Message}");
                }
                return true;
            });
        });
    }
}
