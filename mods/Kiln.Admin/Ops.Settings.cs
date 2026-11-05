// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    static partial void RegisterSettings(Bridge bridge, Logger log)
    {
        if (!Settings.Available)
        {
            bridge.Register("settings_get", _ => throw new AdminError("kiln.settings is unavailable on this server build"));
            bridge.Register("settings_set", _ => throw new AdminError("kiln.settings is unavailable on this server build"));
            return;
        }
        // Lua contract: {values = {path = value}, write_supported}.
        bridge.Register("settings_get", req =>
        {
            var values = new JsonObject();
            var wanted = req["paths"] is JsonArray paths ? paths.Select(p => p!.GetValue<string>()).ToHashSet() : null;
            foreach (var (name, value) in Settings.Values)
                if (wanted is null || wanted.Contains(name)) values[name] = JsonValue(value);
            return new JsonObject { ["values"] = values, ["write_supported"] = Settings.Ready };
        });
        // Lua contract: {path, value} -> {path, before, after}.
        bridge.Register("settings_set", req =>
        {
            var path = req["path"]?.GetValue<string>() ?? throw new AdminError("path required");
            if (req["value"] is not JsonNode raw) throw new AdminError("value required");
            var value = JsonSerializer.Deserialize<JsonElement>(raw.ToJsonString());
            if (!Settings.TrySet(path, value, out var change, out var error)) throw new AdminError(error);
            return new JsonObject { ["path"] = path, ["before"] = JsonValue(change!.Before), ["after"] = JsonValue(change.After) };
        });
        bridge.Register("settings_save", _ =>
        {
            Settings.Save();
            return new JsonObject { ["saved"] = true };
        });
    }

    private static JsonNode? JsonValue(object? v) => v switch
    {
        null => null,
        bool b => b,
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        string s => s,
        TimeSpan t => t.TotalSeconds,
        _ => v.ToString(),
    };
}
