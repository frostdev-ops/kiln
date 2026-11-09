// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace Kiln.Sdk;

/// <summary>
/// What a mod's data files ask of players' games (<c>clients</c> in kiln.mod.json), as Kiln.Blocks and the services check it when the mod
/// loads: <c>mod</c> when a player's game needs the mod's own files, <c>kiln</c> when Kiln clients need definitions the server sends, and
/// null when the files ask no more than <c>none</c>. What blocks in code ask comes from Kiln.Analyzers; the build takes the higher of the two.
/// Kiln.Analyzers.Tests (DataClientsTests) keeps the sections and fields here in step with Kiln.Blocks' block types.
/// </summary>
internal static class DataClients
{
    public const string None = "none", Kiln = "kiln", Mod = "mod";

    /// <summary>What a block of each section of blocks/*.json asks on its own: its type's [BlockClients], else kiln (a new item, spell,
    /// status or part: Kiln clients get the definition from the server). A field can ask for more (<see cref="ModFields"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["statuses"] = Kiln, ["spells"] = Kiln, ["weapons"] = Kiln, ["perks"] = Kiln,
        ["items"] = Kiln, ["wands"] = Kiln, ["armor"] = Kiln, ["stations"] = Kiln, ["cosmetics"] = Kiln, ["wornLooks"] = Kiln, ["looks"] = Kiln,
        ["weaponStats"] = Kiln, ["armorStats"] = Kiln, ["weaponClasses"] = Kiln, ["armorSlots"] = Kiln, ["armorSets"] = Kiln, ["onHits"] = Kiln,
        ["props"] = Kiln, ["placements"] = Kiln, ["comforts"] = Kiln, ["processing"] = Kiln, ["menus"] = Kiln,
        ["containers"] = Kiln, ["doors"] = Kiln, ["beds"] = Kiln, ["seats"] = Kiln, ["interactions"] = Kiln, ["hair"] = Kiln, ["beards"] = Kiln,
        ["recipes"] = Kiln,
        // nothing a player's game has to know, unless a field says so (an ability that places something, an NPC attack of its own: kiln)
        ["effects"] = None, ["abilities"] = None, ["triggers"] = None, ["screenEffects"] = None,
        ["materialTweaks"] = None, ["strengths"] = None, ["surfaces"] = None, ["lootTables"] = None,
        ["creatures"] = None, ["bodies"] = None, ["animations"] = None, ["ais"] = None, ["behaviours"] = None, ["attacks"] = None, ["rules"] = None,
        // built by every player's game from the mod's own files
        ["vfx"] = Mod, ["sounds"] = Mod, ["materials"] = Mod, ["talents"] = Mod, ["talentTrees"] = Mod, ["buildingMaterials"] = Mod, ["blueprints"] = Mod, ["npcs"] = Mod, ["terrainMaterials"] = Mod,
        // the game's own journal and NPC menu, built from the mod's files at start
        ["quests"] = Mod, ["dialogues"] = Mod,
    };

    /// <summary>The fields whose value can name the mod's own file, cue, VFX or key ([ClientsModWhen], and what the compilers check):
    /// a VFX of a mod (<c>vfx</c>, <c>light</c>; a game VFX asks kiln), a cue that is not a GUID, a PNG icon, a model's GUID or glTF file, an
    /// NPC body's model and an animation's clips, an ability's key.</summary>
    public static readonly IReadOnlyCollection<string> ModFields = ["vfx", "light", "sound", "icon", "model", "female", "clips", "key"];

    public static string? Max(string? a, string? b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(string? level) => level switch { None => 0, Kiln => 1, Mod => 2, _ => -1 };

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The mod folder's data files: blocks/*.json, skills/*.json, quests/*.json (and quests/generated), content/*.json, assets/assets.json and
    /// assets/cook.json. A file that does not parse is skipped (kiln lint and the loader say why).</summary>
    public static string? OfFolder(string dir, Action<string>? log = null)
    {
        string? level = null;
        foreach (var (sub, of) in new (string, Func<JsonElement, string?>)[] { ("blocks", OfBlocks), ("skills", OfSkills), ("quests", OfQuests), ("quests/generated", OfQuests), ("content", OfContent) })
        {
            var d = Path.Combine(dir, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var file in Directory.GetFiles(d, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                if ((level = Max(level, Read(file, of, log))) == Mod) return Mod;
        }
        level = Max(level, Read(Path.Combine(dir, "assets", "assets.json"), OfAssets, log));
        return Max(level, Read(Path.Combine(dir, "assets", "cook.json"), OfCook, log));
    }

    private static string? Read(string file, Func<JsonElement, string?> of, Action<string>? log)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
            return of(doc.RootElement);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            log?.Invoke($"cannot read {file}: {e.Message}");
            return null;
        }
    }

    /// <summary>One blocks/*.json document: { "section": { "name": { block } } }.</summary>
    public static string? OfBlocks(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? level = null;
        foreach (var section in root.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var block in section.Value.EnumerateObject())
                if ((level = Max(level, OfBlock(section.Name, block.Value))) == Mod) return Mod;
        }
        return level;
    }

    /// <summary>One block of a section.</summary>
    public static string? OfBlock(string section, JsonElement b)
    {
        string? level = Sections.TryGetValue(section, out var own) && own != None ? own : null;
        if (level == Mod) return Mod;
        if (b.ValueKind != JsonValueKind.Object) return level;
        switch (section)
        {
            case "abilities":
                if (Text(b, "key").Length > 0) return Mod;
                // an item to use it from is a new item; a delivery other than effects on the caster places new data, as a cast motion does
                if (b.TryGetProperty("item", out _)) level = Max(level, Kiln);
                if (b.TryGetProperty("delivery", out var d) && !PlainSelf(d)) level = Max(level, Kiln);
                if (b.TryGetProperty("cast", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("animation", out _)) level = Max(level, Kiln);
                break;
            case "bodies" when Text(b, "model").Length > 0:
                return Mod;
            case "animations" when Text(b, "clips").Length > 0:
                return Mod;
            case "attacks" when !b.TryGetProperty("sequence", out _):
                level = Max(level, Kiln);  // an attack of its own is a new sequence
                break;
        }
        return Max(level, Fields(b, section, null));
    }

    // effects on the caster alone, with no VFX and no tweak: the game's generic impact runs them
    private static bool PlainSelf(JsonElement d) =>
        d.ValueKind == JsonValueKind.Object && Text(d, "type") == "self" && !d.TryGetProperty("vfx", out _)
        && !(d.TryGetProperty("tweak", out var t) && t.ValueKind == JsonValueKind.Object && t.EnumerateObject().Any());

    private static string? Fields(JsonElement e, string section, string? parent)
    {
        string? level = null;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
                if ((level = Max(level, Fields(x, section, parent))) == Mod) return Mod;
            return level;
        }
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
            switch (p.Name)
            {
                case "vfx" or "light":
                    var name = p.Value.ValueKind == JsonValueKind.Object ? Text(p.Value, "name") : text;
                    if (name.Length == 0) break;
                    if (!name.StartsWith("game:", StringComparison.Ordinal) && !Guid.TryParse(name, out _)) return Mod;
                    level = Max(level, Kiln);  // new data that names a game VFX reaches players' games from the server
                    continue;
                case "sound" when text.Length > 0 && !Guid.TryParse(text, out _):
                case "icon" when text.EndsWith(".png", StringComparison.OrdinalIgnoreCase):
                case "model" or "female" when Guid.TryParse(text, out _) || Gltf(text):
                case "model" when parent == "body" && text.Length > 0:
                case "clips" when parent == "animation" && text.Length > 0:
                    return Mod;
                case "attacks" when section is "creatures" or "behaviours" && p.Value.ValueKind == JsonValueKind.Array:
                    if (p.Value.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object && !a.TryGetProperty("sequence", out _))) level = Max(level, Kiln);
                    break;
            }
            if ((p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) && (level = Max(level, Fields(p.Value, section, p.Name))) == Mod) return Mod;
        }
        return level;
    }

    /// <summary>skills/*.json: a node in the game's tree or a tree of the mod's, which every player's game builds from the mod's files.</summary>
    public static string? OfSkills(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var game = root.TryGetProperty("gameTree", out var g) ? g.ValueKind == JsonValueKind.Object && g.TryGetProperty("nodes", out var n) ? n : g : default;
        if (game.ValueKind == JsonValueKind.Array && game.GetArrayLength() > 0) return Mod;
        return root.TryGetProperty("trees", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 ? Mod : null;
    }

    /// <summary>quests/*.json: a quest in the game's journal (the default), a dialogue in the game's NPC menu, an NPC clone or a voice of the
    /// mod's: a client builds them at start from the mod's files. Quests that only talk in chat, and dialogues with "native": false, ask nothing.</summary>
    public static string? OfQuests(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var q in Entries(root, "quests"))
            if (Text(q, "journal") != "chat") return Mod;
        foreach (var n in Entries(root, "npcs"))
            if (n.TryGetProperty("clone", out var c) && c.ValueKind != JsonValueKind.False) return Mod;
        foreach (var d in Entries(root, "dialogues"))
        {
            if (!(d.TryGetProperty("native", out var nat) && nat.ValueKind == JsonValueKind.False)) return Mod;
            foreach (var node in Entries(d, "nodes"))
                if (Text(node, "voice") is { Length: > 0 } v && !Guid.TryParse(v, out _)) return Mod;
        }
        return null;
    }

    /// <summary>content/*.json: a new item, recipe, template, sequence, perk or status (kiln), an item with a PNG icon (mod).</summary>
    public static string? OfContent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? level = null;
        foreach (var ns in new[] { "items", "recipes", "templates", "sequences", "perks", "buffs" })
        {
            if (!root.TryGetProperty(ns, out var s) || s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("add", out var add) || add.ValueKind != JsonValueKind.Array) continue;
            foreach (var it in add.EnumerateArray())
            {
                if (ns == "items" && it.ValueKind == JsonValueKind.Object && Text(it, "icon").EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return Mod;
                level = Kiln;
            }
        }
        return level;
    }

    /// <summary>assets/assets.json: an icon, a texture, a model or a material, which only a player's game draws and the server never sends.</summary>
    public static string? OfAssets(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var es) || es.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in es.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var kind = Text(e, "kind");
            var type = Text(e, "type");
            if (kind is "icon" or "texture" || kind == "resource" && (type.Contains("RenderModel") || type.Contains("UiTexture") || type.Contains("RenderMaterial"))) return Mod;
        }
        return null;
    }

    /// <summary>assets/cook.json: a cooked model, sound or voice-over (the Impact cook makes programs, which the server sends).</summary>
    public static string? OfCook(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "tool") == "impact" || !root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in steps.EnumerateArray())
            if (s.ValueKind == JsonValueKind.Object && Text(s, "op") is "model" or "sound" or "cue" or "voiceover") return Mod;
        return null;
    }

    private static IEnumerable<JsonElement> Entries(JsonElement e, string name) =>
        e.TryGetProperty(name, out var s) ? s.ValueKind switch
        {
            JsonValueKind.Object => s.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object),
            JsonValueKind.Array => s.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object),
            _ => [],
        } : [];

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Gltf(string s) => s.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
}
