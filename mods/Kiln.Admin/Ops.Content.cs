// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Game content registries (kiln.content). Changes made here are owned by Kiln.Admin and come off when it
/// unloads; persistent changes belong in a mod's content/*.json data pack.</summary>
internal static partial class Ops
{
    static partial void RegisterContent(Bridge bridge, Logger log)
    {
        if (!Content.Available) return;
        bridge.Register("content_status", _ =>
        {
            var s = Content.Status() ?? throw new AdminError("kiln.content is not loaded");
            return new JsonObject
            {
                ["ready"] = s.Ready, ["items"] = s.Items, ["recipes"] = s.Recipes, ["loot"] = s.Loot, ["drops"] = s.Drops,
                ["packs"] = s.Packs, ["errors"] = s.PackErrors, ["conflicts"] = s.Conflicts, ["changed"] = s.Changed,
                ["stackGuard"] = s.StackGuard, ["generation"] = s.Generation, ["detail"] = s.Detail,
                ["definitions"] = s.Definitions, ["placeholders"] = s.Placeholders,
                ["missing"] = new JsonArray([.. NewContent.Placeholders().Select(p => (JsonNode)$"{p.Mod}:{p.Name} (0x{p.Id:x8})")]),
            };
        });
        // {kind: items|recipes|loot|drops, entry: name|0xid, field} -> {value, original}
        bridge.Register("content_get", req =>
        {
            var (entry, field) = ContentField(req);
            return ContentErrors(() => new JsonObject { ["value"] = entry.Get(field).ToString(), ["original"] = entry.Original(field).ToString() });
        });
        // {kind} -> the registry's fields (from the game's reflection): [{path, type, valueType, writable, enumKind, options?}]
        bridge.Register("content_fields", req =>
        {
            var reg = Content.Registry(ContentKindOf(req));
            return new JsonArray([.. reg.Fields.Select(f => (JsonNode)FieldJson(f))]);
        });
        // {kind, q, limit?} -> entries whose name contains q (or whose id is q): [{id, name, modified}]
        bridge.Register("content_search", req =>
        {
            var reg = Content.Registry(ContentKindOf(req));
            var q = req["q"]?.GetValue<string>()?.Trim() ?? "";
            if (q.Length == 0) throw new AdminError("q required");
            var limit = req["limit"] is JsonValue lv && lv.TryGetValue<double>(out var l) ? (int)Math.Clamp(l, 1, 200) : 30;
            var hits = new List<ContentEntry>();
            if (reg.Get(q) is { } exact) hits.Add(exact);
            var n = reg.Count;
            for (var i = 0; i < n && hits.Count < 400; i++)
            {
                var e = reg[i];
                if (e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && hits.All(h => h.Id != e.Id)) hits.Add(e);
            }
            return new JsonArray([.. hits
                .OrderBy(e => e.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(e => e.Name.Length).Take(limit)
                .Select(e => (JsonNode)new JsonObject { ["id"] = $"0x{e.Id:x8}", ["name"] = e.Name, ["modified"] = e.Modified })]);
        });
        // {kind, entry} -> {id, name, guid, modified, fields[{path, type, writable, value, original, changed}], inputs/outputs | stacks}
        bridge.Register("content_entry", req =>
        {
            var kind = ContentKindOf(req);
            var entry = ContentEntryOf(kind, req["entry"]?.GetValue<string>());
            var fields = new JsonArray();
            foreach (var f in Content.Registry(kind).Fields.Take(400))
            {
                var o = FieldJson(f);
                try
                {
                    var v = entry.Get(f.Path).ToString();
                    var orig = entry.Original(f.Path).ToString();
                    o["value"] = v;
                    o["original"] = orig;
                    o["changed"] = v != orig;
                }
                catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
                {
                    o["error"] = e.Message;
                }
                fields.Add(o);
            }
            var r = new JsonObject
            {
                ["kind"] = kind.ToString().ToLowerInvariant(), ["id"] = $"0x{entry.Id:x8}", ["name"] = entry.Name,
                ["guid"] = entry.Guid.ToString(), ["modified"] = entry.Modified, ["fields"] = fields,
            };
            if (kind == ContentKind.Recipes)
            {
                r["inputs"] = RecipeStacksJson(entry.Inputs);
                r["outputs"] = RecipeStacksJson(entry.Outputs);
            }
            else if (kind == ContentKind.Drops)
                r["stacks"] = new JsonArray([.. entry.Stacks.Select((s, i) => (JsonNode)new JsonObject
                {
                    ["index"] = i, ["item"] = s.Item.ToString(), ["name"] = Content.Items.Get(s.Item)?.Name, ["countMin"] = s.CountMin,
                    ["countMax"] = s.CountMax, ["chance"] = Math.Round(s.Chance, 4), ["weight"] = Math.Round(s.Weight, 4), ["group"] = s.Group,
                })]);
            return r;
        });
        // {kind, entry, field, value} (number, bool or text)
        bridge.Register("content_set", req =>
        {
            var (entry, field) = ContentField(req);
            try
            {
                switch (req["value"])
                {
                    case JsonValue v when v.TryGetValue<bool>(out var b): entry.Set(field, b); break;
                    case JsonValue v when v.TryGetValue<long>(out var l): entry.Set(field, l); break;
                    case JsonValue v when v.TryGetValue<double>(out var d): entry.Set(field, d); break;
                    case JsonValue v when v.TryGetValue<string>(out var t): entry.Set(field, t); break;
                    default: throw new AdminError("value must be a number, bool or text");
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
            {
                throw new AdminError(e.Message);
            }
            log.Info($"dashboard set content {req["kind"]}/{req["entry"]}.{field} = {req["value"]?.ToJsonString()}");
            return new JsonObject { ["value"] = entry.Get(field).ToString(), ["original"] = entry.Original(field).ToString() };
        });
        bridge.Register("content_reset", req =>
        {
            var (entry, field) = ContentField(req);
            return ContentErrors(() => new JsonObject
            {
                ["reset"] = entry.Reset(field), ["value"] = entry.Get(field).ToString(), ["original"] = entry.Original(field).ToString(),
            });
        });
        bridge.Register("content_recipe", req =>
        {
            var entry = ContentEntryOf(ContentKind.Recipes, req["entry"]?.GetValue<string>());
            return new JsonObject
            {
                ["inputs"] = RecipeStacksJson(entry.Inputs), ["outputs"] = RecipeStacksJson(entry.Outputs),
                ["durationSeconds"] = entry.CraftingDuration.TotalSeconds,
            };
        });
        bridge.Register("content_definitions", _ => JsonNode.Parse(NewContent.DefinitionsJson()) ?? new JsonArray());
        // Content sync to Kiln clients (docs/kiln/sync.md): what the server currently serves.
        bridge.Register("sync_status", _ =>
        {
            var c = ContentSync.Serving;
            return new JsonObject
            {
                ["content"] = c is { } x ? new JsonObject { ["state"] = x.StateId.ToString("x8"), ["fields"] = x.Fields, ["bytes"] = x.Bytes, ["parts"] = x.Parts } : null,
            };
        });
        bridge.Register("content_report", _ => new JsonObject { ["report"] = Content.PackReport() });
        bridge.Register("content_dump", _ => new JsonObject { ["path"] = Content.Dump() });
        bridge.Register("content_reload", _ =>
        {
            Content.ReloadPacks();
            return new JsonObject { ["report"] = Content.PackReport() };
        });
    }

    private static ContentKind ContentKindOf(JsonObject req) =>
        req["kind"]?.GetValue<string>() is { } k && Enum.TryParse<ContentKind>(k, ignoreCase: true, out var ck) && Enum.IsDefined(ck)
            ? ck : throw new AdminError("kind must be items, recipes, loot or drops");

    private static JsonObject FieldJson(ContentField f)
    {
        var o = new JsonObject
        {
            ["path"] = f.Path, ["type"] = f.TypeName, ["valueType"] = f.Type.ToString().ToLowerInvariant(), ["writable"] = f.Writable,
            ["enumKind"] = f.EnumKind.ToString().ToLowerInvariant(),
        };
        if (f.EnumKind != ContentEnumKind.Plain) o["options"] = new JsonArray([.. f.Values.Select(v => (JsonNode)v.Name)]);
        return o;
    }

    private static JsonArray RecipeStacksJson(IEnumerable<RecipeStack> s) => new([.. s.Select(x => (JsonNode)new JsonObject
    {
        ["item"] = x.Item.ToString(), ["name"] = Content.Items.Get(x.Item)?.Name, ["count"] = x.Count, ["category"] = x.Category,
    })]);

    /// <summary>Unknown fields and bad values are the operator's mistakes: send the message, no stack trace.</summary>
    private static JsonObject ContentErrors(Func<JsonObject> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AdminError(e.Message);
        }
    }

    private static (ContentEntry Entry, string Field) ContentField(JsonObject req)
    {
        var kind = ContentKindOf(req);
        var entry = ContentEntryOf(kind, req["entry"]?.GetValue<string>());
        var field = req["field"]?.GetValue<string>() is { Length: > 0 } f ? f : throw new AdminError("field required");
        return (entry, field);
    }

    private static ContentEntry ContentEntryOf(ContentKind kind, string? key) =>
        string.IsNullOrEmpty(key) ? throw new AdminError("entry required")
        : Content.Registry(kind).Get(key) ?? throw new AdminError($"no {kind.ToString().ToLowerInvariant()} entry '{key}'");
}
