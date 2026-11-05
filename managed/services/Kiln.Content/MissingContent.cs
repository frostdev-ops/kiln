// SPDX-License-Identifier: MIT
// Missing content (Forge's "missing registry entries"): every new item a world has seen is recorded in that world's
// WorldData (Kiln.Saves), with the definition needed to rebuild it. At world load, right after the registries are
// ready (before the game reads the save), an item whose mod is gone gets an inert placeholder "[Missing: mod:item]"
// under its own GUID: the same id, the same base and field values (max stack, gear), so saved stacks survive. When
// the mod comes back, its definition takes the id back (kiln.content: state "restored"). Usage and the lab test:
// docs/kiln/services/content.md "Missing content".
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static unsafe class MissingContent
{
    private const string Key = "missing-content";

    /// <summary>The record kept in the world's WorldData (key "missing-content" of Kiln's own "kiln.content" bundle).</summary>
    public sealed class Record
    {
        public int Format { get; set; } = 1;
        /// <summary>GUID text -> what is needed to rebuild a placeholder.</summary>
        public Dictionary<string, SeenItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SeenItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Mod { get; set; }
        public string? DisplayName { get; set; }
        /// <summary>The definition as kiln.content normalized it (the data-pack form).</summary>
        public string Definition { get; set; } = "{}";
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private static ModContext? _ctx;   // Kiln's own data context: WorldData keys belong to "kiln.content"
    private static ulong _owner;
    private static int _lastCount = -1;
    private static string? _world;
    private static readonly List<string> _placed = new();

    /// <summary>Placeholders registered at the last world load ("mod:item (0x...)").</summary>
    public static IReadOnlyList<string> Placed => _placed;

    private static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "missing content: " + text);

    private static bool Ensure()
    {
        if (_ctx != null) return true;
        if (!Saves.Available) return false;
        _ctx = HostAccess.CreateContext(new ModInfo("kiln.content", "Kiln content", "4", AppContext.BaseDirectory), 0, AppContext.BaseDirectory);
        try
        {
            _owner = Backend.Current.OwnerCreate("content-missing");
            if (Content.T != null && _owner != 0)
            {
                using var s = new ContentNative.Utf8("missing content");
                Content.T->LabelOwner(_owner, s.Str);
            }
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"no owner for placeholders: {e.Message}");
        }
        return true;
    }

    /// <summary>Runs <paramref name="fn"/> with WorldData scoped to Kiln's own bundle (tick thread).</summary>
    private static T AsKiln<T>(Func<T> fn)
    {
        var prev = ModContext.CurrentMod;
        ModContext.CurrentMod = _ctx;
        try
        {
            return fn();
        }
        finally
        {
            ModContext.CurrentMod = prev;
        }
    }

    private static bool HasServerWorld => !Kiln.Client.IsClient || Kiln.Client.IsHosting;

    /// <summary>The registries were (re)applied. World load: placeholders for recorded items that nobody defines, then
    /// the record is brought up to date. Native ready callback (world thread), before mods' OnReady handlers.</summary>
    internal static void OnReady(bool reload)
    {
        try
        {
            if (!NewContent.Available || !HasServerWorld || !Ensure()) return;
            var world = Saves.WorldId;
            var rec = AsKiln(() => WorldData.Get<Record>(Key));
            var defs = NewContent.Definitions();
            if (!reload || world != _world)
            {
                _world = world;
                _placed.Clear();
                var defined = new HashSet<Guid>(defs.Where(d => d.Kind == ContentKind.Items && d.State != "orphaned" && !d.IsPlaceholder).Select(d => d.Guid));
                var have = new HashSet<Guid>(defs.Where(d => d.Kind == ContentKind.Items).Select(d => d.Guid));
                foreach (var (g, seen) in rec.Items)
                {
                    if (!Guid.TryParse(g, out var guid) || defined.Contains(guid)) continue;
                    if (have.Contains(guid) && defs.Any(d => d.Guid == guid && d.IsPlaceholder && d.State != "orphaned")) continue;
                    Place(guid, seen);
                }
                if (_placed.Count > 0)
                    Log(LogLevel.Warn, $"{_placed.Count} item(s) of mods that are not loaded are kept as placeholders (their saved stacks stay): {string.Join(", ", _placed)}");
                else if (rec.Items.Count > 0)
                    Log(LogLevel.Info, $"world {world}: {rec.Items.Count} new item(s) recorded, all defined");
                defs = NewContent.Definitions();
            }
            Update(rec, defs);
        }
        catch (Exception e)
        {
            Log(LogLevel.Error, $"failed: {e}");
        }
    }

    /// <summary>Every ~4 s: items registered since (a hot-loaded mod, a pack) join the record.</summary>
    internal static void Tick(ulong step)
    {
        if (step % 100 != 0 || _ctx == null || !HasServerWorld) return;
        try
        {
            if (!Content.Ready) return;
            if (Saves.WorldId != _world)
            {
                // a hosted world's registries come up before the game loads it: now its own record is there
                if (Saves.WorldDataReady && Saves.WorldId is not null) OnReady(false);
                return;
            }
            if (_world == null) return;
            var defs = NewContent.Definitions();
            var n = defs.Count(d => d.Kind == ContentKind.Items && d.Worlds.Contains("server") && !d.IsPlaceholder);
            if (n == _lastCount) return;
            Update(AsKiln(() => WorldData.Get<Record>(Key)), defs);
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"record update failed: {e.Message}");
        }
    }

    private static void Update(Record rec, IReadOnlyList<ContentDefinition> defs)
    {
        var now = DateTime.UtcNow;
        var added = 0;
        var items = defs.Where(d => d.Kind == ContentKind.Items && d.Worlds.Contains("server") && !d.IsPlaceholder && d.DefinitionJson != null).ToList();
        foreach (var d in items)
        {
            var key = d.Guid.ToString("D");
            if (!rec.Items.TryGetValue(key, out var s))
            {
                rec.Items[key] = s = new SeenItem { FirstSeen = now };
                added++;
            }
            s.Id = $"0x{d.Id:x8}";
            s.Name = d.Name;
            s.Mod = d.Mod;
            s.Definition = d.DefinitionJson!;
            s.DisplayName = DisplayOf(d.DefinitionJson!);
            s.LastSeen = now;
        }
        _lastCount = items.Count;
        if (added > 0) Log(LogLevel.Info, $"world {_world}: {added} new item(s) recorded ({rec.Items.Count} in all): kept as placeholders if their mod goes away");
    }

    private static string? DisplayOf(string json)
    {
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            return (string?)o?["displayName"] ?? (string?)o?["texts"]?["en"]?["name"] ?? (string?)o?["texts"]?["*"]?["name"];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An inert placeholder: the recorded base and field values (so stacks, gear and max stack stay valid) and
    /// the recorded references (it looks the same), no loot / drops / knowledge, no recipes, a "[Missing: ...]" name.</summary>
    private static void Place(Guid guid, SeenItem seen)
    {
        JsonObject body;
        try
        {
            body = JsonNode.Parse(seen.Definition) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException e)
        {
            Log(LogLevel.Warn, $"{seen.Name} ({guid}): the recorded definition is unreadable ({e.Message}); its stacks are lost at load");
            return;
        }
        var mod = seen.Mod ?? "?";
        var label = $"{mod}:{seen.Name}";
        var ph = new JsonObject
        {
            ["guid"] = guid.ToString("D"),
            ["name"] = seen.Name,
            ["base"] = body["base"]?.DeepClone(),
            ["displayName"] = $"[Missing: {label}]",
            ["description"] = $"This item ({seen.DisplayName ?? seen.Name}) comes from the mod \"{mod}\", which is not installed. " +
                              "It is kept so that nothing is lost: install the mod again and it is back.",
            ["placeholder"] = new JsonObject { ["mod"] = mod, ["item"] = seen.Name, ["displayName"] = seen.DisplayName },
        };
        foreach (var k in new[] { "fields", "model", "visual", "perks", "weaponType", "impact", "program", "animation", "collectionModel", "iconModel", "refs" })
            if (body[k] is { } v) ph[k] = v.DeepClone();
        if (body["icon"] is JsonValue iv && iv.TryGetValue<string>(out var icon) && !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            ph["icon"] = icon;
        try
        {
            var d = NewContent.Define(ContentKind.Items, ph, _owner);
            _placed.Add($"{label} (0x{d.Id:x8})");
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, $"{label} ({guid}): no placeholder: {e.Message}");
        }
    }
}
