// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Game data ops over kiln.gamedata (Kiln.Content's GameData registries; docs/kiln/services/gamedata.md "For
/// Kiln.Admin"): templates, arsenals, sequences, buffs, perks, skills, item data. Sets are Kiln.Admin's layers (ops run
/// in its tick handler) and revert when Kiln.Admin unloads. Values are pack JSON (numbers, names, flag lists, objects,
/// <c>{"mul": 1.5}</c>).</summary>
internal static partial class Ops
{
    private static readonly string[] GameDataSections = ["templates", "arsenals", "sequences", "buffs", "perks", "skills", "items"];

    internal static bool RegisterGameDataSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterGameData(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"game data ops off: {e.GetType().Name}");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterGameData(Bridge bridge, Logger log)
    {
        if (!Content.GameDataAvailable) return false;
        bridge.Register("gamedata_status", _ => ParseOrText(Content.GameDataStatus()));
        bridge.Register("gamedata_configs", _ => GameDataGuard(() => ParseOrText(Content.ConfigVocabulary())));
        // the clones (added entries) of every section
        bridge.Register("gamedata_definitions", _ => GameDataGuard(() =>
        {
            var list = new JsonArray();
            foreach (var name in GameDataSections)
                foreach (var e in Registry(name))
                    if (e.IsClone) list.Add(EntryJson(name, e));
            return list;
        }));
        // {section, q?, limit?}: entries whose name, id or GUID contains q
        bridge.Register("gamedata_search", req => GameDataGuard(() =>
        {
            var section = SectionName(req);
            var q = Str(req, "q")?.Trim() ?? "";
            var limit = req["limit"] is null ? 200 : Math.Clamp((int)Number(req, "limit"), 1, 2000);
            var hits = Registry(section).Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                    e.Guid.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                    ("0x" + e.Id.ToString("x8")).Contains(q, StringComparison.OrdinalIgnoreCase));
            return new JsonArray([.. hits.Take(limit).Select(e => (JsonNode)EntryJson(section, e))]);
        }));
        foreach (var (prefix, section) in new[] { ("template", "templates"), ("arsenal", "arsenals"), ("sequence", "sequences") })
        {
            bridge.Register(prefix + "_get", req => GameDataGuard(() => Get(section, req)));
            bridge.Register(prefix + "_set", req => GameDataGuard(() => Set(section, req, log)));
            bridge.Register(prefix + "_reset", req => GameDataGuard(() => Reset(section, req)));
        }
        // {entry, prefix?}
        bridge.Register("template_fields", req => GameDataGuard(() =>
        {
            var e = Entry("templates", req);
            return new JsonArray([.. e.Fields(Str(req, "prefix"), 2000).Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.Path, ["type"] = f.Type, ["value"] = f.Value?.DeepClone(), ["writable"] = f.Writable, ["original"] = f.Original?.DeepClone(),
            })]);
        }));
        // {section, entry, path, value?}
        bridge.Register("gamedata_get", req => GameDataGuard(() => Get(SectionName(req), req)));
        bridge.Register("gamedata_set", req => GameDataGuard(() => Set(SectionName(req), req, log)));
        bridge.Register("gamedata_reset", req => GameDataGuard(() => Reset(SectionName(req), req)));
        // {section, entry?}
        bridge.Register("gamedata_dump", req => GameDataGuard(() => ParseOrText(Registry(SectionName(req)).Dump(Str(req, "entry")))));
        return true;
    }

    private static JsonNode? Get(string section, JsonObject req) => Entry(section, req).GetJson(Str(req, "path") ?? "");

    private static JsonNode? Set(string section, JsonObject req, Logger log)
    {
        var e = Entry(section, req);
        var path = Str(req, "path") is { Length: > 0 } p ? p : throw new AdminError("path required");
        var value = req["value"] ?? throw new AdminError("value required (pack JSON)");
        var n = e.SetJson(path, value.ToJsonString());
        log.Info($"dashboard set {section} {e.Name}.{path} ({n} leaf/leaves)");
        return new JsonObject { ["applied"] = n, ["value"] = e.GetJson(path) };
    }

    private static JsonNode? Reset(string section, JsonObject req)
    {
        var e = Entry(section, req);
        var path = Str(req, "path") ?? "";
        var done = e.Reset(path);
        return new JsonObject { ["reset"] = done, ["value"] = path.Length > 0 ? e.GetJson(path) : null };
    }

    private static string SectionName(JsonObject req)
    {
        var s = Str(req, "section")?.ToLowerInvariant() ?? throw new AdminError("section required");
        return GameDataSections.Contains(s) ? s : throw new AdminError($"section: {string.Join(", ", GameDataSections)}");
    }

    private static GameDataRegistry Registry(string section) => section switch
    {
        "templates" => Content.Templates,
        "arsenals" => Content.Arsenals,
        "sequences" => Content.Sequences,
        "buffs" => Content.Buffs,
        "perks" => Content.Perks,
        "skills" => Content.Skills,
        _ => Content.ItemData,
    };

    private static GameDataEntry Entry(string section, JsonObject req)
    {
        var key = Str(req, "entry") is { Length: > 0 } k ? k : throw new AdminError("entry required (name, GUID or 0x id)");
        return Registry(section).Get(key) ?? throw new AdminError($"no {section} entry '{key}'");
    }

    private static JsonObject EntryJson(string section, GameDataEntry e) => new()
    {
        ["section"] = section, ["name"] = e.Name, ["id"] = "0x" + e.Id.ToString("x8"), ["guid"] = e.Guid == Guid.Empty ? null : e.Guid.ToString(),
        ["modified"] = e.Modified, ["clone"] = e.IsClone,
    };

    private static JsonNode? ParseOrText(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }

    private static JsonNode? GameDataGuard(Func<JsonNode?> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new AdminError(e.Message);
        }
    }
}
