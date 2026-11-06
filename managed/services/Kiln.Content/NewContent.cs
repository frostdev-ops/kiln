// SPDX-License-Identifier: MIT
// New items and recipes (Forge's DeferredRegister): the managed face of kiln.content v3 define / definitions
// (native/services/content/newcontent.cpp; usage: docs/kiln/services/content.md "New items and recipes").
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// A new item: a clone of an existing item (<see cref="BaseOn"/>: its model, icon, behaviour and stats) under a fixed
/// GUID of its own, with its own debug name, texts and field values. Register it with
/// <c>Content.Items.Register(def)</c>, best in <c>OnLoad</c>.
/// </summary>
/// <remarks>
/// <para>The item id is FNV-1a over the GUID (the game's own ItemId), so the server and every client that registers
/// the same definition agree on it, and saved stacks find the item again after a restart. Never change the GUID of a
/// released item: saves store the id.</para>
/// <para>Install the mod on the server and on the clients (<c>"side": "both"</c>): the client needs the definition to
/// show the item (name, icon, tooltip) and the recipe.</para>
/// </remarks>
public sealed record ItemDefinition
{
    /// <summary>The fixed GUID of the item (never change it once released).</summary>
    public required Guid Guid { get; init; }

    /// <summary>The debug name (<c>A-Z a-z 0-9 _ - .</c>), unique: how packs, recipes and admin tools name the item.</summary>
    public required string Name { get; init; }

    /// <summary>The item to clone: debug name (<c>Weapon_T1_1H_SwordEpic_01</c>), <c>"0x…"</c> id or GUID.</summary>
    public required string BaseOn { get; init; }

    /// <summary>The name players see (all languages). Null: the base item's.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The tooltip description. Null: the base item's. Empty: none.</summary>
    public string? Description { get; init; }

    /// <summary>Where to get it, in the tooltip (all languages). Null: nothing: a new item does not take the base's.</summary>
    public string? Source { get; init; }

    /// <summary>The lore text. Null: the base item's.</summary>
    public string? Lore { get; init; }

    /// <summary>Field values that differ from the base item, by path (<c>"damageSetup.dmgMod"</c> = 1.5,
    /// <c>"rarity"</c> = "Legendary", <c>"maxStackSize"</c> = 1): numbers, bools, enum names, string arrays for flag
    /// sets. The paths are the registry's (<c>Content.Items.Fields</c>); identity fields cannot be set.
    /// Gear: <c>"rarity"</c> and <c>"itemLevelRange.minLevel"</c> say what the item is; it is made exactly so, as its recipe
    /// tooltip shows. docs/kiln/services/content.md "Rarity, level and perks of new gear".</summary>
    public IReadOnlyDictionary<string, object>? Fields { get; init; }

    /// <summary>Opt-in for a rolled loot item: keeps the base's <c>generateRarity</c> and level range (a loot weapon: levels
    /// 1..55), so a level or rarity passed by a loot roll or <c>Gear</c> is used. Without one the game makes the lowest allowed
    /// rarity and rolls a level, so do not give such an item a recipe. By default (false) a new item has a fixed rarity and
    /// one level, like the game's crafted gear.</summary>
    public bool RolledLoot { get; init; }

    // ---- texts per language ----

    /// <summary>Texts per language (<c>"en"</c>, <c>"de"</c>, <c>"pt-BR"</c>, <c>"*"</c> = every language). A game
    /// language without its own entry uses its primary language ("pt" for pt-BR), then <c>"en"</c>, then
    /// <see cref="DisplayName"/> / <see cref="Description"/> / <see cref="Lore"/>. Texts not given stay the base item's, except <see cref="ItemTexts.AcquisitionHint"/>.</summary>
    public IReadOnlyDictionary<string, ItemTexts>? Texts { get; init; }

    // ---- kitbash: references to resources the game already loaded ----

    /// <summary>The model: <c>"item:&lt;name&gt;"</c> takes another item's whole visual setup (held / worn model,
    /// visual entity, model offsets, hide mask, collection model); a model GUID sets the equipment model only.</summary>
    public string? Model { get; init; }

    /// <summary>A worn item's models for what the character wears and is, the game's <c>equipment.visualModels</c>: the client draws the first entry whose <c>Mask</c> (keen::BlendShapeMask) is
    /// exactly the raised flags it looks at, and the plain model otherwise. <c>Model</c>: a model GUID, or <c>"item:&lt;name&gt;.equipment.visualModel"</c>. Null: the base's.</summary>
    public IReadOnlyList<(uint Mask, string Model)>? VisualModels { get; init; }

    /// <summary>The icon: <c>"item:&lt;name&gt;"</c> (another item's icon and icon setup), an item GUID, or a PNG file
    /// (relative to the mod's folder; drawn by kiln.assets on Kiln clients, the base's icon until then). Null: the
    /// base item's.</summary>
    public string? Icon { get; init; }

    /// <summary>Perk slots (up to 5): a perk GUID, <c>"item:&lt;name&gt;"</c> (that item's perk in the same slot),
    /// <c>"none"</c>, or null to keep the base's slot.</summary>
    public IReadOnlyList<string?>? Perks { get; init; }

    /// <summary>The weapon category (skills and bonuses that apply to a weapon type): <c>"item:&lt;name&gt;"</c> or a
    /// weapon category GUID.</summary>
    public string? WeaponType { get; init; }

    /// <summary>Other reference fields by path (<c>"equipment.impactEntity"</c>, <c>"equipment.program"</c>,
    /// <c>"pidEntity"</c>, ...): <c>"item:&lt;name&gt;"</c>, <c>"item:&lt;name&gt;.&lt;path&gt;"</c>, a GUID, <c>"none"</c>.</summary>
    public IReadOnlyDictionary<string, string>? Refs { get; init; }

    // ---- distribution ----

    /// <summary>Random loot: the item can be rolled in containers and world pickups like <see cref="ItemLoot.Like"/>.</summary>
    public ItemLoot? Loot { get; init; }

    /// <summary>Drop tables (enemy drops, fixed chests) that get a stack of this item.</summary>
    public IReadOnlyList<ItemDrop>? Drops { get; init; }

    /// <summary>The item id this definition gets (FNV-1a of the GUID).</summary>
    public uint Id => NewContent.IdOf(Guid);
}

/// <summary>The texts of an item in one language. Null: not given (the next fallback, or the base item's).</summary>
public sealed record ItemTexts
{
    public string? Name { get; init; }
    /// <summary>The line under the name (the item's kind, e.g. "One-Handed Sword").</summary>
    public string? Caption { get; init; }
    public string? Description { get; init; }
    public string? Lore { get; init; }
    /// <summary>Where to get it (shown for items the player does not have yet). Not given in any language: none, not the base item's.</summary>
    public string? AcquisitionHint { get; init; }
    /// <summary>The action hints (e.g. "Attack", "Block").</summary>
    public string? PrimaryAction { get; init; }
    public string? SecondaryAction { get; init; }
    public string? TertiaryAction { get; init; }
}

/// <summary>Random loot of a new item: rolled where <see cref="Like"/> is (its rarities, label groups, stack sizes),
/// with this item's own stack sizes when given.</summary>
public sealed record ItemLoot
{
    /// <summary>The game item whose loot entry is copied (null: the definition's base).</summary>
    public string? Like { get; init; }
    public int? StackMin { get; init; }
    public int? StackMax { get; init; }
}

/// <summary>A stack of a new item added to a drop table (a <c>DefaultInventoryResource</c>).</summary>
/// <param name="Table">The table's GUID, or a glob over table GUIDs; or <c>like:&lt;item&gt;</c>: every table that
/// drops that item (the new stack goes next to it).</param>
public sealed record ItemDrop(string Table)
{
    public int CountMin { get; init; } = 1;
    public int CountMax { get; init; } = 1;
    /// <summary>spawnProbability, 0..1.</summary>
    public double Chance { get; init; } = 1;
    /// <summary>Weight in a selector group (one stack of the group is picked by weight).</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>How a new recipe is unlocked in the crafting UI.</summary>
public enum RecipeUnlock
{
    /// <summary>No knowledge needed: the station lists it once the player knows one of its ingredients (the game's
    /// rule for recipes without a requirement).</summary>
    Always = 0,

    /// <summary>The base recipe's knowledge requirement (EMF observed that a copied requirement may keep the recipe
    /// hidden: some requirements are tied to the original recipe's id).</summary>
    LikeBase = 1,
}

/// <summary>One ingredient or result of a <see cref="RecipeDefinition"/>.</summary>
/// <param name="Item">Item (debug name, <c>"0x…"</c> id, GUID, or the <see cref="ItemDefinition.Name"/> of a new item).</param>
/// <param name="Category">Inputs only: an input category id (<c>RecipeStack.Category</c> of a game recipe), "any item of
/// that category"; then <paramref name="Item"/> is null.</param>
public readonly record struct RecipeIngredient(string? Item, int Count, uint Category = 0)
{
    public static RecipeIngredient Of(string item, int count) => new(item, count);
    public static RecipeIngredient OfCategory(uint category, int count) => new(null, count, category);
    public static implicit operator RecipeIngredient((string Item, int Count) x) => new(x.Item, x.Count);
}

/// <summary>
/// A new recipe: ingredients and results at a station, copied from <see cref="BaseOn"/> (station, level, props,
/// cooking, …) or made for <see cref="Station"/>. Register it with <c>Content.Recipes.Register(def)</c>.
/// </summary>
public sealed record RecipeDefinition
{
    /// <summary>The fixed GUID of the recipe (the recipe id is FNV-1a of it).</summary>
    public required Guid Guid { get; init; }

    /// <summary>The debug name, unique.</summary>
    public required string Name { get; init; }

    /// <summary>A recipe to copy (debug name, <c>"0x…"</c> id, GUID): its station and requirements. Optional when
    /// <see cref="Station"/> is given.</summary>
    public string? BaseOn { get; init; }

    /// <summary>Where it is crafted: a recipe whose station to use, a workshop id (<c>"0x…"</c>), <c>"hand"</c> (the
    /// player's own crafting menu), or <c>"workshop:&lt;guid&gt;"</c>: a workshop of a mod's own, whose id is FNV-1a of the GUID, new if nothing
    /// has it yet. Without <see cref="BaseOn"/> the recipe has no level / comfort / prop needs.</summary>
    public string? Station { get; init; }

    /// <summary>The workshop (<c>"0x…"</c>) whose crafting menu lists it, when that is not the one it is crafted at. Null: its own.</summary>
    public string? Menu { get; init; }

    public required IReadOnlyList<RecipeIngredient> Inputs { get; init; }
    public required IReadOnlyList<RecipeIngredient> Outputs { get; init; }

    /// <summary>The crafting duration (timed stations). Null: the template's.</summary>
    public TimeSpan? Duration { get; init; }

    public RecipeUnlock Unlock { get; init; } = RecipeUnlock.Always;

    /// <summary>A recipe name (most recipes show their result's name). Null: none.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Other RecipeInfo fields by path (<c>"level"</c>, <c>"isCookingRecipe"</c>, …).</summary>
    public IReadOnlyDictionary<string, object>? Fields { get; init; }

    public uint Id => NewContent.IdOf(Guid);
}

/// <summary>A registered definition, as kiln.content reports it.</summary>
/// <param name="State"><c>defined</c> / <c>unchanged</c> / <c>redefined</c> / <c>adopted</c> right after Register;
/// in <see cref="NewContent.Definitions"/>: <c>pending</c> (no world loaded yet), <c>registered</c>, <c>orphaned</c>
/// (its owner went away: kept until restart), <c>error</c>.</param>
/// <param name="Worlds">The worlds whose registries hold it (<c>server</c>, <c>client</c>).</param>
/// <param name="Problem">Why it is not (yet) in a world, if it is not.</param>
public sealed record ContentDefinition(ContentKind Kind, Guid Guid, uint Id, string Name, string State,
    IReadOnlyList<string> Worlds, string? Owner, string? Problem)
{
    /// <summary>The id of the mod that defined it (its code or its data pack); for a placeholder, the missing mod's.</summary>
    public string? Mod { get; init; }

    /// <summary>A placeholder "[Missing: mod:item]" that keeps saved stacks of an item whose mod is gone.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Parts of the definition that were skipped (a kitbash reference that is not loaded, ...).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The normalized definition (the data-pack form).</summary>
    public string? DefinitionJson { get; init; }

    public override string ToString() => $"{Kind} {Name} (0x{Id:x8}, {State}{(Worlds.Count > 0 ? " in " + string.Join("+", Worlds) : "")})";
}

/// <summary>New items and recipes: <c>Content.Items.Register(new ItemDefinition { … })</c>,
/// <c>Content.Recipes.Register(new RecipeDefinition { … })</c>, <see cref="Definitions"/>.</summary>
/// <remarks>
/// <para>Lifecycle (Forge: DeferredRegister + missing mappings): a definition made before the world loads is inserted
/// when the registries load, before the save is read; made later, it is in at the next tick (at once on the tick
/// thread once the registries are ready). It is inserted again into every new world (a client's next hosted or joined
/// session). When the mod unloads, its items stay registered until the server restarts (stacks of them are kept, never
/// cut) and its recipes leave their station; registering the same GUIDs again (a reload) adopts them. After a restart
/// without the mod the game drops stacks of the unknown items when it loads the save (vanilla behaviour).</para>
/// <para>Call from mod code (the definition belongs to the calling mod). Any thread.</para>
/// </remarks>
public static unsafe class NewContent
{
    /// <summary>The service supports new content (kiln.content v3 on a build with feature <c>content.new</c>).</summary>
    public static bool Available => Content.T is var t && t != null && t->Header.Version >= 3 && t->Define != null;

    /// <summary>FNV-1a-32 over the GUID's 16 bytes (memory order): the game's ItemId / RecipeId of that GUID.</summary>
    public static uint IdOf(Guid guid)
    {
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b);
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x01000193u;
        return h;
    }

    /// <summary>Registers a new item as the calling mod's (see <see cref="ItemDefinition"/>).</summary>
    /// <exception cref="ArgumentException">The definition is invalid (the message says why).</exception>
    public static ContentDefinition Register(ItemDefinition def) => Register(def, Content.Owner());

    /// <summary>Registers a new item as a given owner (Kiln.Blocks: a data-only mod's blocks).</summary>
    internal static ContentDefinition Register(ItemDefinition def, ulong owner)
    {
        ArgumentNullException.ThrowIfNull(def);
        var j = new JsonObject
        {
            ["guid"] = def.Guid.ToString("D"),
            ["name"] = def.Name,
            ["base"] = def.BaseOn,
        };
        if (def.DisplayName != null) j["displayName"] = def.DisplayName;
        if (def.Description != null) j["description"] = def.Description;
        if (def.Lore != null) j["lore"] = def.Lore;
        if (def.Source != null) j["source"] = def.Source;
        (def.Fields as FieldValues)?.Require("items", $"item {def.Name}");
        if (def.Fields is { Count: > 0 }) j["fields"] = FieldsJson(def.Fields);
        if (def.Texts is { Count: > 0 })
        {
            var t = new JsonObject();
            foreach (var (lang, x) in def.Texts)
            {
                var o = new JsonObject();
                void Put(string k, string? v) { if (v != null) o[k] = v; }
                Put("name", x.Name); Put("caption", x.Caption); Put("description", x.Description); Put("lore", x.Lore);
                Put("acquisitionHint", x.AcquisitionHint); Put("primaryAction", x.PrimaryAction);
                Put("secondaryAction", x.SecondaryAction); Put("tertiaryAction", x.TertiaryAction);
                t[lang] = o;
            }
            j["texts"] = t;
        }
        if (def.RolledLoot) j["rolledLoot"] = true;
        if (def.Model != null) j["model"] = def.Model;
        if (def.VisualModels != null) j["visualModels"] = new JsonArray(def.VisualModels.Select(v => (JsonNode)new JsonObject { ["mask"] = v.Mask, ["model"] = v.Model }).ToArray());
        if (def.Icon != null) j["icon"] = IconPath(def.Icon);
        if (def.Perks != null) j["perks"] = new JsonArray(def.Perks.Select(p => p == null ? null : (JsonNode)JsonValue.Create(p)!).ToArray());
        if (def.WeaponType != null) j["weaponType"] = def.WeaponType;
        if (def.Refs is { Count: > 0 })
        {
            var r = new JsonObject();
            foreach (var (k, v) in def.Refs) r[k] = v;
            j["refs"] = r;
        }
        if (def.Loot != null)
        {
            var l = new JsonObject();
            if (def.Loot.Like != null) l["like"] = def.Loot.Like;
            if (def.Loot.StackMin != null) l["stackSizeMin"] = def.Loot.StackMin;
            if (def.Loot.StackMax != null) l["stackSizeMax"] = def.Loot.StackMax;
            j["loot"] = l;
        }
        if (def.Drops is { Count: > 0 })
            j["drops"] = new JsonArray(def.Drops.Select(d => (JsonNode)new JsonObject
            {
                ["table"] = d.Table, ["countMin"] = d.CountMin, ["countMax"] = d.CountMax, ["chance"] = d.Chance, ["weight"] = d.Weight,
            }).ToArray());
        return Define(ContentKind.Items, j, owner);
    }

    // a PNG icon relative to the calling mod's folder -> absolute (kiln.assets reads it)
    private static string IconPath(string icon)
    {
        if (!icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(icon)) return icon;
        var mod = ModContext.CurrentMod;
        return mod == null ? icon : Path.GetFullPath(Path.Combine(mod.Info.Directory, icon));
    }

    /// <summary>Registers a new recipe as the calling mod's (see <see cref="RecipeDefinition"/>).</summary>
    public static ContentDefinition Register(RecipeDefinition def) => Register(def, Content.Owner());

    /// <summary>Registers a new recipe as a given owner (Kiln.Blocks: a data-only mod's blocks).</summary>
    internal static ContentDefinition Register(RecipeDefinition def, ulong owner)
    {
        ArgumentNullException.ThrowIfNull(def);
        var j = new JsonObject { ["guid"] = def.Guid.ToString("D"), ["name"] = def.Name };
        if (def.BaseOn != null) j["base"] = def.BaseOn;
        if (def.Station != null) j["station"] = def.Station;
        if (def.Menu != null) j["menu"] = def.Menu;
        j["inputs"] = Stacks(def.Inputs);
        j["outputs"] = Stacks(def.Outputs);
        if (def.Duration is { } d) j["duration"] = d.TotalSeconds;
        j["unlock"] = def.Unlock == RecipeUnlock.LikeBase ? "base" : "always";
        if (def.DisplayName != null) j["displayName"] = def.DisplayName;
        (def.Fields as FieldValues)?.Require("recipes", $"recipe {def.Name}");
        if (def.Fields is { Count: > 0 }) j["fields"] = FieldsJson(def.Fields);
        return Define(ContentKind.Recipes, j, owner);
    }

    /// <summary>Registers a definition given in the data-pack form (one entry of an <c>"add"</c> list).</summary>
    public static ContentDefinition RegisterJson(ContentKind kind, string json) =>
        Define(kind, JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("a JSON object is expected"));

    /// <summary>Every definition (of every mod and data pack) with its state.</summary>
    public static IReadOnlyList<ContentDefinition> Definitions()
    {
        var t = Table("NewContent.Definitions");
        var list = new List<ContentDefinition>();
        using var doc = JsonDocument.Parse(ContentNative.ReadText(t->Definitions));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var worlds = e.TryGetProperty("worlds", out var w) ? w.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            list.Add(new ContentDefinition(e.GetProperty("kind").GetString() == "item" ? ContentKind.Items : ContentKind.Recipes,
                Guid.Parse(e.GetProperty("guid").GetString()!), ParseId(e.GetProperty("id").GetString()),
                e.GetProperty("name").GetString() ?? "", e.GetProperty("state").GetString() ?? "", worlds,
                e.TryGetProperty("owner", out var o) ? o.GetString() : null,
                e.TryGetProperty("error", out var er) ? er.GetString() : null)
            {
                Mod = e.TryGetProperty("mod", out var m) && m.GetString() is { Length: > 0 } ms ? ms : null,
                IsPlaceholder = e.TryGetProperty("placeholder", out var ph) && ph.ValueKind == JsonValueKind.True,
                Warnings = e.TryGetProperty("warnings", out var ws) ? ws.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
                DefinitionJson = e.TryGetProperty("definition", out var dj) ? dj.GetRawText() : null,
            });
        }
        return list;
    }

    /// <summary>Placeholders "[Missing: mod:item]" registered for items whose mod is gone (missing content).</summary>
    public static IReadOnlyList<ContentDefinition> Placeholders() => Definitions().Where(d => d.IsPlaceholder && d.State != "orphaned").ToList();

    /// <summary>kiln.content v4's info: game languages, reference pools, placeholders (JSON; "{}" before v4).</summary>
    public static string InfoJson()
    {
        var t = Content.T;
        if (!ContentNative.Has(t, 4) || t->Info == null) return "{}";
        return ContentNative.ReadText(t->Info);
    }

    /// <summary>(definitions, placeholders) for <see cref="Content.Status"/>; (0, 0) when unavailable.</summary>
    internal static (int Definitions, int Placeholders) Counts()
    {
        if (!Available) return (0, 0);
        try
        {
            using var doc = JsonDocument.Parse(DefinitionsJson());
            int n = 0, p = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                n++;
                if (e.TryGetProperty("placeholder", out var ph) && ph.ValueKind == JsonValueKind.True &&
                    e.GetProperty("state").GetString() != "orphaned") p++;
            }
            return (n, p);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    /// <summary>The definitions as kiln.content's JSON (for admin tools).</summary>
    public static string DefinitionsJson()
    {
        var t = Table("NewContent.DefinitionsJson");
        return ContentNative.ReadText(t->Definitions);
    }

    // ------------------------------------------------------------------------------------------- internals -----

    private static ContentNative.Table* Table(string api)
    {
        var t = Content.T;
        if (t == null) throw new NotSupportedException("kiln.content is not available");
        if (t->Header.Version < 3 || t->Define == null) throw new NotSupportedException($"{api}: kiln.content has no new-content support (v{t->Header.Version})");
        return t;
    }

    private static ContentDefinition Define(ContentKind kind, JsonObject j) => Define(kind, j, Content.Owner());

    /// <summary>Defines as a Kiln owner (content sync, missing-content placeholders): any thread.</summary>
    internal static ContentDefinition Define(ContentKind kind, JsonObject j, ulong owner)
    {
        var t = Table($"Content.{kind}.Register");
        var text = j.ToJsonString();
        const int cap = 4096;
        var buf = stackalloc byte[cap];
        nuint len = 0;
        int rc;
        using (var s = new ContentNative.Utf8(text)) rc = t->Define(owner, (uint)kind, s.Str, buf, cap, &len);
        var rep = ContentNative.Text(buf, cap);
        string? error = null, state = null, warning = null, id = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(rep) ? "{}" : rep);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var e)) error = e.GetString();
            if (r.TryGetProperty("state", out var st)) state = st.GetString();
            if (r.TryGetProperty("warning", out var w)) warning = w.GetString();
            if (r.TryGetProperty("id", out var i)) id = i.GetString();
        }
        catch (JsonException) { }
        if (rc == ContentNative.Unavailable) throw new NotSupportedException($"{kind} register: {error ?? "unavailable on this build"}");
        if (rc != ContentNative.Ok) throw new ArgumentException($"{kind} register {(string?)j["name"]}: {error ?? ContentNative.Message(rc)}");
        var guid = Guid.Parse((string)j["guid"]!);
        return new ContentDefinition(kind, guid, id != null ? ParseId(id) : IdOf(guid), (string)j["name"]!, state ?? "defined", [], null, warning);
    }

    private static uint ParseId(string? s) =>
        s != null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.Parse(s[2..], NumberStyles.HexNumber) : 0;

    private static JsonArray Stacks(IReadOnlyList<RecipeIngredient> list)
    {
        var a = new JsonArray();
        foreach (var s in list)
        {
            var o = new JsonObject { ["count"] = s.Count };
            if (s.Category != 0) o["category"] = "0x" + s.Category.ToString("x8");
            else o["item"] = s.Item ?? throw new ArgumentException("a recipe ingredient needs an item or a category");
            a.Add(o);
        }
        return a;
    }

    private static JsonObject FieldsJson(IReadOnlyDictionary<string, object> fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields)
            o[k] = v switch
            {
                bool b => JsonValue.Create(b),
                string s => JsonValue.Create(s),
                JsonNode n => n.DeepClone(),
                Enum e => JsonValue.Create(e.ToString()),
                IEnumerable<string> names => new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                TimeSpan ts => JsonValue.Create(ts.TotalSeconds),
                IConvertible c when v is float or double or decimal => JsonValue.Create(c.ToDouble(CultureInfo.InvariantCulture)),
                IConvertible c => JsonValue.Create(c.ToInt64(CultureInfo.InvariantCulture)),
                _ => throw new ArgumentException($"field {k}: unsupported value type {v?.GetType().Name ?? "null"}"),
            };
        return o;
    }
}

/// <summary><c>Content.Items.Register(…)</c> / <c>Content.Recipes.Register(…)</c>.</summary>
public static class ContentRegistryDefinitions
{
    /// <summary>Registers a new item (<paramref name="registry"/> must be <c>Content.Items</c>).</summary>
    public static ContentDefinition Register(this ContentRegistry registry, ItemDefinition def) =>
        registry.Kind == ContentKind.Items ? NewContent.Register(def) : throw new ArgumentException("items are registered in Content.Items");

    /// <summary>Registers a new recipe (<paramref name="registry"/> must be <c>Content.Recipes</c>).</summary>
    public static ContentDefinition Register(this ContentRegistry registry, RecipeDefinition def) =>
        registry.Kind == ContentKind.Recipes ? NewContent.Register(def) : throw new ArgumentException("recipes are registered in Content.Recipes");
}
