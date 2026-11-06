// SPDX-License-Identifier: MIT
// CosmeticBlock and WornLook -> an item definition. A vanity item is an armour-slot item with the flag IsVanityItem: flags can't be set on a clone, so the block starts from a vanity item of its slot,
// one that takes dyes or one that doesn't. How the client reads what this writes: docs/re/building.md section 3.
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    // The starters: plain items of the game's, one for each slot, with no drop or shop flags. Flags decide what the two columns differ in: HasPIDE (an item entity, where a dye goes) and IsNonTintable.
    private static string CosmeticStarter(CosmeticSlot slot, bool dyes) => (slot, dyes) switch
    {
        (CosmeticSlot.Head, true) => "Vanity_TX_Hunter_Helmet",
        (CosmeticSlot.Head, false) => "Vanity_T1_Dress_Helmet",
        (CosmeticSlot.UpperBody, true) => "Vanity_T7_NPC_Adventurer_Chest",
        (CosmeticSlot.UpperBody, false) => "Vanity_T6_BeachAttire_Chest",
        (CosmeticSlot.Arms, true) => "Vanity_TX_noble_Gloves",
        (CosmeticSlot.Arms, false) => "Vanity_T1_Dress_Gloves",
        (CosmeticSlot.LowerBody, true) => "Vanity_TX_Hunter_Legs",
        (CosmeticSlot.LowerBody, false) => "Vanity_T6_BeachAttire_Legs",
        (CosmeticSlot.Feet, true) => "Vanity_TX_Hunter_Boots",
        _ => "Vanity_T6_BeachAttire_Boots",
    };

    // The flags the client looks at when it picks a slot's model (client 0xe8fe00), by the slot. A variant for any other flag would never be drawn.
    private static Fit FitsOfSlot(CosmeticSlot slot, bool hidesShield) => slot switch
    {
        CosmeticSlot.UpperBody => Fit.Female | Fit.Tight | Fit.ArmsTight01 | (hidesShield ? Fit.None : Fit.Shield),
        CosmeticSlot.Arms => Fit.Female | (hidesShield ? Fit.None : Fit.Shield),
        CosmeticSlot.LowerBody => Fit.Female | Fit.NoArmor | Fit.LegsBeltTight | Fit.LegsFeetTight | Fit.LegsFeetTight01,
        _ => Fit.Female,
    };

    /// <summary>keen::VisualEquippedItems' name of what a look can hide.</summary>
    private static string HideName(BodyPart p) => p switch
    {
        BodyPart.ArmorHead => "Armour_Head", BodyPart.ArmorUpperBody => "Armour_UpperBody", BodyPart.ArmorArms => "Armour_Arms", BodyPart.ArmorLowerBody => "Armour_LowerBody", BodyPart.ArmorFeet => "Armour_Feet",
        BodyPart.RingLeft => "Ring0", BodyPart.RingRight => "Ring1", BodyPart.Shield => "Shield", BodyPart.MainHand => "MainHand",
        _ => "BodyPart_" + p,
    };

    /// <summary>keen::BlendShapeFlags' names of a set of fits.</summary>
    private static string[] FitNames(Fit f) => Enum.GetValues<Fit>().Where(x => x != Fit.None && f.HasFlag(x)).Select(x => x.ToString()).ToArray();

    private static readonly HashSet<string> Attachable = new(StringComparer.OrdinalIgnoreCase)
    {
        WornAttach.Head, WornAttach.Chest, WornAttach.Pelvis, WornAttach.FootLeft, WornAttach.FootRight, WornAttach.HandLeft, WornAttach.HandRight, WornAttach.KneeLeft, WornAttach.KneeRight, WornAttach.ArmLeft, "HandL", "HandR", "Block",
    };

    private static bool IsGltf(string f) => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

    /// <summary>A model file's GUID: <c>&lt;mod&gt;:model:&lt;its path without the extension&gt;</c>, as the cook works it out (tools/modelcook, cook/worn.py).</summary>
    internal static Guid WornModelGuid(string mod, string file) => BlockIds.Derive(mod, "model", WornModelName(file));

    private static string WornModelName(string file)
    {
        var p = file.Replace('\\', '/');
        var dot = p.LastIndexOf('.');
        return dot > p.LastIndexOf('/') ? p[..dot] : p;
    }

    private static string WornStem(string file) => WornModelName(file).Replace('/', '_');

    private static void CheckWorn(BlockContext ctx, WornLook l)
    {
        if (l.Like == null && l.Model == null) throw new BlockError(ctx.Block, "model", "a look says what it is: a model, or an item it looks like");
        if (l.Attach is { } a && !Attachable.Contains(a) && !(a.Length > 1 && a[0] is 'j' or 'J' && a[1..].All(char.IsAsciiDigit)))
            throw new BlockError(ctx.Block, "attach", $"\"{a}\" is not a slot; there are {string.Join(", ", Attachable.Order())}, or say j<index>");
        if (l.Attach != null && (l.Model == null || !IsGltf(l.Model))) throw new BlockError(ctx.Block, "attach", "attach says where a glTF of yours sits: it needs a model file");
        if (l.Lods is { Count: > 0 } && (l.Model == null || !IsGltf(l.Model))) throw new BlockError(ctx.Block, "lods", "levels of detail are files of a glTF of yours: it needs a model file");
        if (l.PixelSizes is { } ps)
        {
            if (ps.Count != 1 + (l.Lods?.Count ?? 0)) throw new BlockError(ctx.Block, "pixelSizes", $"{ps.Count} pixel sizes for {1 + (l.Lods?.Count ?? 0)} level(s): give one per level");
            if (ps[0] != 0) throw new BlockError(ctx.Block, "pixelSizes", $"the first is {ps[0]}: it has to be 0, the level drawn at any size");
            for (var i = 1; i < ps.Count; i++)
                if (ps[i] <= ps[i - 1]) throw new BlockError(ctx.Block, "pixelSizes", $"{ps[i]} after {ps[i - 1]}: each level's has to be larger than the one before");
        }
        if (l.Fits is { } fits && (fits & (Fit.Female)) != 0) throw new BlockError(ctx.Block, "fits", "female is the character's, and no piece raises it");
    }

    /// <summary>Where a file of a look is and what it should say. A file that isn't there is the author's mistake on any machine. A file that isn't cooked isn't: a dedicated server has no models to
    /// cook, and a player's first start may run before the cook.</summary>
    private static void CheckWornFile(BlockContext ctx, Lowered low, WornLook look, string owner, string file, string path, CosmeticSlot? slot, bool isModel)
    {
        if (file.Contains("..") || Path.IsPathRooted(file)) throw new BlockError(ctx.Block, path, $"\"{file}\": a file in the mod's folder, with a relative path");
        var dir = BlocksBridge.ModDirectory(owner);
        if (dir.Length == 0) return;
        if (!File.Exists(Path.Combine(dir, file)))
            throw new BlockError(ctx.Block, path, $"\"{file}\" is not in the mod's folder. The build copies a mod's folders to where it runs: a folder of models has to be one of them (KilnModFolders, or a None item with CopyToOutputDirectory)");
        if (!BlocksBridge.IsClient) return;
        var side = Path.Combine(dir, "assets", "cooked", WornStem(file) + ".model.json");
        if (!File.Exists(side))
        {
            var step = new JsonObject { ["op"] = "worn", ["model"] = file };
            if (isModel && look.Attach != null) step["attach"] = look.Attach;
            WarnWorn(ctx, low, $"block {ctx.Block}, {path}: {file} is not cooked, so it shows the look it starts from. Run kiln cook. A look in blocks/*.json is found by itself; one in code needs a line in assets/cook.json's steps: {step.ToJsonString()}");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(side));
        var root = doc.RootElement;
        if (root.TryGetProperty("skeleton", out var sk) && sk.GetString() is { } skeleton && !string.Equals(skeleton, PlayerSkeleton, StringComparison.OrdinalIgnoreCase))
            throw new BlockError(ctx.Block, path, $"{file} is cooked on skeleton {skeleton}, not the player's: cook it with \"skeleton\": \"@player\", the default of a worn step");
        if (isModel && !string.Equals(look.Attach ?? "", root.TryGetProperty("attach", out var at) ? at.GetString() : "", StringComparison.OrdinalIgnoreCase))
            throw new BlockError(ctx.Block, "attach", $"{file} is cooked with attach {(root.TryGetProperty("attach", out var a2) ? a2.GetString() : "none")}, and the look says {look.Attach ?? "none"}: a look in code says it in its assets/cook.json step too");
        if (slot is { } s && root.TryGetProperty("morphs", out var morphs))
        {
            var heard = FitsOfSlot(s, look.Hides?.Contains(BodyPart.Shield) == true);
            foreach (var m in morphs.EnumerateArray())
                if (Enum.TryParse<Fit>(m.GetString(), out var f) && (heard & f) == 0)
                    WarnWorn(ctx, low, $"block {ctx.Block}, {path}: its morph target {m.GetString()} is never raised on the {s} slot, which looks at {string.Join(", ", FitNames(heard))}");
        }
    }

    private const string PlayerSkeleton = "87b29df6-6518-43ef-9298-e3f85a6204b9";

    private static void WarnWorn(BlockContext ctx, Lowered low, string text)
    {
        lock (ctx.Mod.Warnings)
            if (!ctx.Mod.Warnings.Contains(text)) ctx.Mod.Warnings.Add(text);
        ctx.Mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Warn, $"blocks: {ctx.Mod.Id}: {text}");
    }

    /// <summary>A model reference: a file becomes the GUID of what the cook makes of it, and asks that every player has it.</summary>
    private static string WornModel(BlockContext ctx, Lowered low, WornLook look, string owner, string value, string path, CosmeticSlot? slot, bool isModel)
    {
        if (value.StartsWith("item:", StringComparison.Ordinal)) return value;
        if (Guid.TryParse(value, out _))
        {
            ctx.Need(ClientsLevel.Mod, path, "a model of the mod's own has to be on every player's game: nothing can send it (a look like a game item's asks no more than kiln)");
            return value;
        }
        if (!IsGltf(value)) throw new BlockError(ctx.Block, path, $"\"{value}\": a .glb or .gltf file in the mod's folder, item:<name>, or a model's GUID");
        ctx.Need(ClientsLevel.Mod, path, "a model of the mod's own has to be on every player's game: nothing can send it (a look like a game item's asks no more than kiln)");
        CheckWornFile(ctx, low, look, owner, value, path, slot, isModel);
        return WornModelGuid(owner, value).ToString();
    }

    /// <summary>The models for the character's fits. The client draws the first entry whose mask is exactly the raised flags it looks at, and the plain model when none is, so a female fit alone would
    /// lose to tight gloves: every set of flags gets the most specific variant that applies, the female one on a tie. Sets with no variant are left to the plain model.</summary>
    private static List<(uint Mask, string Model)> WornVariants(BlockContext ctx, Lowered low, WornLook look, string owner, CosmeticSlot slot, string path)
    {
        var heard = FitsOfSlot(slot, look.Hides?.Contains(BodyPart.Shield) == true);
        var variants = new Dictionary<Fit, string>();
        if (look.Female is { } female)
        {
            variants[Fit.Female] = WornModel(ctx, low, look, owner, female, $"{path}.female", slot, false);
        }
        foreach (var (fit, file) in look.Variants)
        {
            var p = $"{path}.variants[{FitConverter.Text(fit)}]";
            if (fit == Fit.None) throw new BlockError(ctx.Block, $"{path}.variants", "a variant says which fit it is for: the plain case is model");
            if ((fit & ~heard) != 0) throw new BlockError(ctx.Block, p, $"the {slot} slot never looks at {string.Join(", ", FitNames(fit & ~heard))}, only at {string.Join(", ", FitNames(heard))}, so it would never be drawn");
            if (variants.ContainsKey(fit)) throw new BlockError(ctx.Block, p, "says the same as female");
            variants[fit] = WornModel(ctx, low, look, owner, file, p, slot, false);
        }
        var bits = Enum.GetValues<Fit>().Where(f => f != Fit.None && (heard & f) != 0).ToArray();
        var entries = new List<(uint, string)>();
        if (variants.Count == 0) return entries;
        for (var set = 1; set < 1 << bits.Length; set++)
        {
            var mask = Fit.None;
            for (var b = 0; b < bits.Length; b++)
                if ((set >> b & 1) != 0) mask |= bits[b];
            var best = variants.Where(v => (v.Key & ~mask) == 0).OrderByDescending(v => BitOperations.PopCount((uint)v.Key)).ThenByDescending(v => (v.Key & Fit.Female) != 0).ThenBy(v => (uint)v.Key).Select(v => v.Value).FirstOrDefault();
            if (best != null) entries.Add(((uint)mask, best));
        }
        return entries;
    }

    /// <summary>A look's files are in the folder of the mod that wrote it: a look named <c>other-mod:name</c>, or that mod's handle, is that mod's.</summary>
    private static string WornOwner(BlockContext ctx, PartRef<WornLook> part) => part.Name is { } n && n.IndexOf(':') is > 0 and var colon ? n[..colon] : ctx.Mod.Id;

    private static void ApplyWorn(Draft d, WornLook look, string owner, CosmeticSlot slot, string path)
    {
        var ctx = d.Ctx;
        CheckWorn(ctx, look);
        var worn = new JsonObject();
        if (look.Like is { } like)
        {
            var n = ctx.ItemName(like, $"{path}.like");
            d.Model = "item:" + n;
            d.Icon = "item:" + n;
            worn["like"] = n;
        }
        if (look.Model is { } model)
        {
            var m = WornModel(ctx, d.Low, look, owner, model, $"{path}.model", slot, true);
            worn["model"] = m;
            if (m.StartsWith("item:", StringComparison.Ordinal))
            {
                d.Model = m;
                d.Icon ??= m;
            }
            else
            {
                d.Refs["equipment.visualModel"] = m;
                d.Refs["iconModel"] = m;
                // what a game item's look would add to it: a rigid piece the server spawns, and models per body
                d.Refs["equipment.visualEntity"] = "none";
                d.VisualModels = [];
            }
        }
        for (var i = 0; i < (look.Lods?.Count ?? 0); i++)
        {
            if (!IsGltf(look.Lods![i])) throw new BlockError(ctx.Block, $"{path}.lods[{i}]", $"\"{look.Lods[i]}\": a .glb or .gltf file in the mod's folder");
            var dir = BlocksBridge.ModDirectory(owner);
            if (dir.Length > 0 && !File.Exists(Path.Combine(dir, look.Lods[i]))) throw new BlockError(ctx.Block, $"{path}.lods[{i}]", $"\"{look.Lods[i]}\" is not in the mod's folder");
        }
        var variants = WornVariants(ctx, d.Low, look, owner, slot, path);
        if (look.Female != null || look.Variants.Count > 0)
        {
            if (look.Model == null || look.Model.StartsWith("item:", StringComparison.Ordinal))
                throw new BlockError(ctx.Block, $"{path}.model", "models for other bodies need a model of the mod's own to be the plain case");
            d.VisualModels = variants;
            worn["visualModels"] = new JsonArray(variants.Select(v => (JsonNode)new JsonObject { ["mask"] = new JsonArray(FitNames((Fit)v.Mask).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["model"] = v.Model }).ToArray());
        }
        // a look like an item's takes the item's hides and fits, unless it says; the rest start from nothing, not from the starter's
        if (look.Hides != null || look.Like == null)
        {
            var hides = (look.Hides ?? []).Select(HideName).Distinct().ToArray();
            worn["hides"] = new JsonArray(hides.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            SetFlags(d, "equipment.hideMask", hides, d.Model?.StartsWith("item:", StringComparison.Ordinal) == true);
        }
        if (look.Fits != null || look.Like == null)
        {
            var fits = FitNames(look.Fits ?? Fit.None);
            worn["fits"] = new JsonArray(fits.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            SetFlags(d, "equipment.blendShapeMask", fits, d.Model?.StartsWith("item:", StringComparison.Ordinal) == true);
        }
        if (look.Icon is { } icon)
        {
            d.Icon = icon;
            worn["icon"] = icon;
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ctx.Need(ClientsLevel.Mod, $"{path}.icon", "a PNG icon has to be on every player's game: nothing can send it (an item's icon, \"item:<name>\", asks no more than kiln)");
        }
        d.Low.Json["worn"] = worn;
    }

    // An item's look is written after its fields when the block takes another item's look (its own Like, or a piece of armor's Visual.Like), so what it says over that look has to come later still.
    private static void SetFlags(Draft d, string path, string[] names, bool afterLook)
    {
        if (afterLook) d.Rules[path] = new JsonArray(names.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        else d.Fields[path] = names;
    }

    // ---- armor ------------------------------------------------------------------------------------------------------

    private static CosmeticSlot WornSlotOf(BlockContext ctx, ArmorSlot slot, string path) => slot.Key switch
    {
        "head" => CosmeticSlot.Head, "chest" => CosmeticSlot.UpperBody, "arms" => CosmeticSlot.Arms, "legs" => CosmeticSlot.LowerBody, "feet" => CosmeticSlot.Feet,
        _ => throw new BlockError(ctx.Block, path, $"a {slot.Key} isn't drawn on the body: give it a visual"),
    };

    /// <summary>A set's look for a slot: (a look, or a worn look and the mod whose files it names).</summary>
    private static (PartRef<VisualBlock>? Visual, WornLook? Worn, string Owner) ResolveArmorLook(BlockContext ctx, ArmorLook l, string path)
    {
        if (l.Worn is { } w) return (null, ctx.Part(w, path), WornOwner(ctx, w));
        if (l.Visual is { } v) return (v, null, ctx.Mod.Id);
        var name = l.Name!;
        var h = ctx.Find(name, path);
        var owner = name.IndexOf(':') is > 0 and var colon ? name[..colon] : ctx.Mod.Id;
        return h switch
        {
            BlockHandle<VisualBlock> vh => (vh, null, owner),
            BlockHandle<WornLook> wh => (null, wh.Block, owner),
            null => throw new BlockError(ctx.Block, path, $"no look or worn look \"{name}\""),
            _ => throw new BlockError(ctx.Block, path, $"\"{name}\" is a {h.BlockType.Name}; this field takes a look or a worn look"),
        };
    }

    /// <summary>A piece of armor's look. Look wins over Visual for what is worn, and Visual still gives the dropped model and the tint; with neither, the set's look for its slot.</summary>
    private static void ArmorLooks(Draft d, ArmorBlock a, ArmorSlot slot)
    {
        var ctx = d.Ctx;
        var visual = a.Visual ?? slot.Visual;
        WornLook? worn = null;
        var owner = ctx.Mod.Id;
        var path = "look";
        if (a.Look != null)
        {
            worn = ctx.Part(a.Look, "look");
            owner = WornOwner(ctx, a.Look);
        }
        else if (visual == null && SetLook(ctx, a.Set, slot) is { } fromSet)
        {
            path = $"set.{slot.Key}";
            (visual, worn, owner) = ResolveArmorLook(ctx, fromSet, path);
        }
        ApplyVisual(d, visual, a.Visual != null ? "visual" : path == "look" ? "slot.visual" : path);
        if (worn != null) ApplyWorn(d, worn, owner, WornSlotOf(ctx, slot, path), path);
    }

    private static BlockHandle DefineCosmetic(BlockContext ctx, Lowered low, string name, CosmeticBlock c)
    {
        RequireName(ctx, c.Name, "a cosmetic");
        if (!Enum.IsDefined(c.Slot)) throw new BlockError(ctx.Block, "slot", $"{c.Slot} is not a slot; there are {string.Join(", ", Enum.GetNames<CosmeticSlot>())}");
        var key = c.Was ?? name;
        var d = new Draft(ctx, low, key, name, CosmeticStarter(c.Slot, c.Dyes));
        d.Rows = new JsonArray();   // a piece has no stats, so its tooltip has no rows
        ApplyWorn(d, ctx.Part(c.Look, "look"), WornOwner(ctx, c.Look), c.Slot, "look");
        ApplyLoot(d, c.Loot);
        low.Json["cosmetic"] = new JsonObject { ["slot"] = c.Slot.ToString(), ["dyes"] = c.Dyes };
        d.Commit(c.Name, c.Description);
        if (c.Recipe != null) DefineRecipe(ctx, low, key, d.Game, c.Recipe);
        return new BlockHandle<CosmeticBlock>(ctx.Mod.Id, name, d.Guid, d.Game, c);
    }
}
