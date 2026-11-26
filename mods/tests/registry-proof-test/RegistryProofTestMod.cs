// SPDX-License-Identifier: MIT
// Lab-only: the live half of the registry proof producer (kilnctl regproof; regmap/README.md "The proof"). Never shipped
// (tools/package/stage.sh builds no mods/tests) and never a mod-author example: it reaches kiln.registry.live through the same
// internal bridge as registry-live-native-test, and reads Kiln's regmap, which no mod should.
//
// With <kiln>/data/content/registry/proof/request.json, at the world's first content ready, for every mapped field of the live
// registries (items, recipes, loot, drops; dev.32's buffs and abilities in RegistryProofTestMod.GameData.cs) it takes one entry
// that reads, and:
//   - reads the value through the schema (kiln.registry.live read), the row's members raw (Content.* by the game's member
//     paths), and the SHA-256 of the entry's resource bytes (kiln.assets);
//   - writes a probe value through kiln.registry.live (begin, submit, commit: the public mapping guard), reads it back
//     through the schema and raw again;
//   - reverts the change through kiln.registry.live and hashes the resource bytes again;
// then writes content/registry/proof/roundtrip-<side>-live.json (registry-roundtrip format 1, generation live, regmapDigest).
// Read-only fields are read only. A field it finds no probe for gets no sample (kilnctl calls it unproven, never proven), and
// the log says why no entry gave one ("registry-proof-test: no sample of <field>": read refusals, no probe...).
// The boot generation and the values export are kiln.content's (registry/proof.cpp, the same request file).
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace RegistryProofTest;

public sealed unsafe partial class RegistryProofTestMod : Mod
{
    [StructLayout(LayoutKind.Sequential)] private struct Str { public byte* Ptr; public nuint Len; }
    [StructLayout(LayoutKind.Sequential)] private struct Reply
    {
        public uint Size, Outcome, Accepted, Refused, Skipped, Reserved;
        public ulong World, Generation;
        public fixed byte Reason[192];
    }
    private const uint Manual = 4, Applied = 0, Refused = 2, Reverted = 5;
    private static readonly string[] Live = { "items", "recipes", "loot", "drops" };
    private byte* _table;
    private ulong _owner;
    private int _pass, _fail;
    private bool _ran;
    private string _kiln = "";

    // Why a field has no sample, in Kiln words: kilnctl makes it the record's unproven reason, which kiln.content shows inside
    // parentheses ("... is not proven for game build <b> (<reason>)"), so no parentheses of their own (review3 N2).
    private const string NoDropTables = "no drop table was loaded in the lab's world; the game loads them as players need them";
    // every entry the lab read holds no value of the field (recipes.station on f0d824be): nothing to round-trip (review3 N1)
    private const string NoValue = "no entry the lab read has a value of it on this build, so there was nothing to round-trip";

    private void Check(bool ok, string text)
    {
        if (ok) ++_pass; else ++_fail;
        Log.Info($"registry-proof-test: {(ok ? "PASS" : "FAIL")} {text}");
    }

    // Phase 1 at the world's first content ready: every live registry but the drop tables. Phase 2 once the world runs (the
    // game loads a drop table when something needs it: Spawn puts a few of the game's loot containers next to a container of
    // the lab's world, whose default_inventory program acquires their tables), then the drop rows and the loot rolls; the suite
    // is written once, at the end, and SUMMARY follows it.
    public override void OnLoad() => Content.OnReady(e =>
    {
        if (_ran || e.Reload) return;
        _ran = true;
        var later = false;
        try { later = Run(); }
        catch (Exception x) { Check(false, x.ToString()); }
        finally { if (!later) Summary(); }
        if (later) _ = Later();
    });

    private void Summary() => Log.Info($"registry-proof-test: SUMMARY registry-proof-test: {_pass} PASS, {_fail} FAIL");

    // ---- the native table ----
    private static Str S(byte[] b, byte* p) => new() { Ptr = p, Len = (nuint)b.Length };

    private string? Read(string registry, string id, string field) => Read(registry, id, field, out _);

    private string? Read(string registry, string id, string field, out int rc)
    {
        var r = Encoding.UTF8.GetBytes(registry); var i = Encoding.UTF8.GetBytes(id); var f = Encoding.UTF8.GetBytes(field);
        var fn = *(delegate* unmanaged[Cdecl]<ulong, Str, Str, Str, byte*, nuint, nuint*, int>*)(_table + 40);
        rc = -1;
        for (var cap = 4096; cap <= 1 << 24; cap *= 8)
        {
            var buf = new byte[cap];
            nuint len = 0;
            fixed (byte* rp = r, ip = i, fp = f, bp = buf) rc = fn(_owner, S(r, rp), S(i, ip), S(f, fp), bp, (nuint)cap, &len);
            if (rc == 0) return Encoding.UTF8.GetString(buf, 0, (int)Math.Min(len, (nuint)(cap - 1)));
            if (len < (nuint)cap) return null; // an error, not a short buffer
        }
        return null;
    }

    private (bool Ok, string Why, bool ExactRefusal) Change(string registry, string id, string field, JsonNode value, out ulong handle) =>
        Change(new JsonArray(Edit(registry, id, field, value)), out handle);

    private static JsonObject Edit(string registry, string select, string field, JsonNode value) =>
        new() { ["registry"] = registry, ["select"] = select, ["field"] = field, ["op"] = value.DeepClone() };

    /// <summary>One Manual transaction of <paramref name="edits"/> (all or nothing), committed; the handle reverts it.</summary>
    private (bool Ok, string Why, bool ExactRefusal) Change(JsonArray edits, out ulong handle)
    {
        handle = 0;
        ulong h = 0;
        var begin = *(delegate* unmanaged[Cdecl]<ulong, uint, ulong*, int>*)(_table + 8);
        var rc = begin(_owner, Manual, &h);
        if (rc != 0 || h == 0) return (false, $"begin rc={rc}", false);
        var ir = new JsonObject { ["ir"] = 1, ["mod"] = "registry-proof-test", ["stage"] = "alter", ["source"] = "registry-proof",
            ["edits"] = edits.DeepClone() };
        var bytes = Encoding.UTF8.GetBytes(ir.ToJsonString());
        var reply = new Reply { Size = (uint)sizeof(Reply) };
        var submit = *(delegate* unmanaged[Cdecl]<ulong, ulong, Str, Reply*, byte*, nuint, nuint*, int>*)(_table + 16);
        fixed (byte* p = bytes) rc = submit(_owner, h, S(bytes, p), &reply, null, 0, null);
        if (rc != 0 || reply.Accepted != (uint)edits.Count) { Finish(h, true); return (false, $"submit rc={rc} accepted={reply.Accepted} refused={reply.Refused}: {Why(reply)}", IsExactRefusal(reply)); }
        var commit = Finish(h, false);
        if (commit.Outcome != Applied) { Finish(h, true); return (false, $"commit outcome={commit.Outcome}: {Why(commit)}", IsExactRefusal(commit)); }
        handle = h;
        return (true, "", false);
    }

    private Reply Finish(ulong handle, bool revert)
    {
        var reply = new Reply { Size = (uint)sizeof(Reply) };
        var fn = *(delegate* unmanaged[Cdecl]<ulong, ulong, Reply*, int>*)(_table + (revert ? 32 : 24));
        fn(_owner, handle, &reply);
        return reply;
    }

    private static bool IsExactRefusal(Reply r) => r.Outcome == Refused && Why(r).StartsWith("not_exact: ", StringComparison.Ordinal);

    private static string Why(Reply r) => Encoding.UTF8.GetString(r.Reason, 192).TrimEnd('\0');

    // ---- the game's side ----
    private static ContentRegistry RegistryOf(string name) => name switch
    {
        "items" => Content.Items, "recipes" => Content.Recipes, "loot" => Content.Loot, _ => Content.Drops,
    };

    private static AssetKey? ResourceOf(string registry, ContentEntry e)
    {
        return registry switch
        {
            "items" => new AssetKey(e.Guid, "keen::ItemInfo"),
            "drops" => new AssetKey(e.Guid, "keen::ecs::DefaultInventoryResource"),
            "recipes" => First("keen::RecipeRegistryResource"),
            _ => First("keen::LootableItemsResource"),
        };
        static AssetKey? First(string type)
        {
            var keys = Assets.ListResources(Assets.TypeHash(type));
            return keys.Length > 0 ? keys[0] : null;
        }
    }

    private static string? BytesSha(AssetKey? key)
    {
        if (key is not { } k) return null;
        var b = Assets.ReadResource(k);
        return b is null ? null : Convert.ToHexStringLower(SHA256.HashData(b));
    }

    /// <summary>A member read raw by the game's path: a number, a bool, an enum's name, a GUID; null when the path is not
    /// a scalar the content reader takes (then the sample says so and kilnctl does not count it as compared).</summary>
    private static JsonNode? Raw(ContentEntry e, string path)
    {
        var tries = RawPaths(path);
        for (var i = 0; i < tries.Count; ++i)
        {
            ContentValue v;
            try { v = e.Get(tries[i]); }
            catch (Exception) { continue; }
            if (i > 0 && v.Type != ContentValueType.Bool) return null; // the fallback only stands for a one-flag struct
            return RawValue(v);
        }
        return null;
    }

    /// <summary>An item's impact config member (<c>impactValues.0x&lt;config id&gt;.value</c>) as the game has it now, through
    /// kiln.gamedata (kiln.content's flat schema has no config paths): its kind tells a Sint32 config from a float one. Null
    /// when the item has no such config or kiln.gamedata cannot read it (no evidence, never guessed).</summary>
    private static ContentValue? ConfigValue(ContentEntry e, string path)
    {
        try
        {
            foreach (var g in new[] { Content.ItemData.Get(e.Guid), Content.ItemData.Get(e.Name) })
                if (g is not null && g.Guid == e.Guid) return g.Get(path);
        }
        catch (Exception) { }
        return null;
    }

    private static JsonNode? ConfigRaw(ContentEntry e, string path) => ConfigValue(e, path) is { } v ? RawValue(v) : null;

    /// <summary>The config of an id table entry Content's enumeration does not list: kiln.gamedata by the table's GUID text in
    /// either byte order (kiln.content's guid_text and .NET's).</summary>
    private static ContentValue? ConfigValueOf(string guidText, string path)
    {
        try
        {
            var keys = new List<string> { guidText };
            var hex = guidText.Replace("-", "", StringComparison.Ordinal);
            if (hex.Length == 32) keys.Add(new Guid(Convert.FromHexString(hex)).ToString());
            foreach (var k in keys)
                if (Content.ItemData.Get(k) is { } g) return g.Get(path);
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The kind of an entry's impact config (coordinator ruling, 2026-11-23: evidence only for the kinds the build
    /// has): "sint32", "float", "unknown" (the entry holds the value, but its config kind cannot be read), "notInWorld" (the
    /// entry is in the build's id table but not in the lab's world, so nothing reads it: never counted as "no config";
    /// kilnctl asks the values export whether it holds one, review3 S1), or null (the entry has no such config).</summary>
    private static string? ConfigKind(ContentValueType? type, bool holdsValue, bool inWorld) => type switch
    {
        ContentValueType.Int => "sint32",
        ContentValueType.Float => "float",
        null when !holdsValue => inWorld ? null : "notInWorld",
        _ => "unknown",
    };

    /// <summary>The paths a member is read raw by, in order: the path itself, and for "<c>x.isSet</c>" also "<c>x</c>",
    /// where reflection exposes a struct whose only scalar is that flag (GemSlotOverride {isSet}, items.socket), as
    /// kiln.registry.live binds it.</summary>
    private static IReadOnlyList<string> RawPaths(string path)
    {
        const string flag = ".isSet";
        return path.EndsWith(flag, StringComparison.Ordinal) && path.Length > flag.Length
            ? new[] { path, path[..^flag.Length] } : new[] { path };
    }

    private static JsonNode? RawValue(ContentValue v)
    {
        try
        {
            return v.Type switch
            {
                ContentValueType.Bool => JsonValue.Create(v.Bool),
                ContentValueType.Int when v.Name is { } n => n.Contains(',') ? new JsonArray(n.Split(',').Select(x => (JsonNode)JsonValue.Create(x.Trim())!).ToArray()) : JsonValue.Create(n),
                ContentValueType.Int => JsonValue.Create(v.Integer),
                ContentValueType.Float => JsonValue.Create(v.Number),
                ContentValueType.Time => JsonValue.Create((long)Math.Round(v.Number * 1e9)),
                ContentValueType.Guid => JsonValue.Create(v.Guid.ToString()),
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    /// <summary>A count of a recipe's first input or output stack (<c>input[0]</c>, <c>output[0]</c>), which Content.Get does
    /// not address: the stacks as the game has them now (ContentEntry.Inputs / Outputs, kiln.content's read of the game's
    /// lists); null for any other member (not read, never guessed).</summary>
    private static JsonNode? StackRaw(ContentEntry e, string list, string leaf)
    {
        try
        {
            var stacks = list switch { "input" => e.Inputs, "output" => e.Outputs, _ => null };
            return stacks is { Count: > 0 } && leaf == "count" ? JsonValue.Create((long)stacks[0].Count) : null;
        }
        catch (Exception) { return null; }
    }

    // ---- probes: another value the field takes, in Kiln's terms ----
    // the conversions kilnctl compares raw (roundtrip.rs raw_kind): a member that cannot be read raw stays null (missing
    // evidence), never the resource digest (review2 N5: impactConfig)
    private static readonly HashSet<string> Compared = new() { "int", "float", "bool", "time", "enum", "flags", "optionalEnum", "formula", "impactConfig" };

    /// <summary>A live refusal that shows the mapping disagrees with the build (review2 S6): the schema or the regmap lacks the
    /// field, a game member or enum value is missing, a member is not writable. kilnctl files such a sample as changed. Guard
    /// refusals (Unproven, Unmapped, RestartRequired, not_exact), range refusals and entry-specific ones are missing
    /// evidence instead (no sample).</summary>
    private static bool IsDisagreement(string reason)
    {
        if (reason.Length == 0) return false;
        foreach (var guard in new[] { "Unproven: ", "Unmapped: ", "RestartRequired", "not_exact: " })
            if (reason.Contains(guard, StringComparison.Ordinal)) return false;
        foreach (var structural in new[] { "not a field of the schema", "no regmap row", "is missing on this game build", "is not writable on this game build",
                                           "is not a writable member on this game build", " is not a value of " })
            if (reason.Contains(structural, StringComparison.Ordinal)) return true;
        return false;
    }

    private static JsonNode? Probe(JsonNode? v, JsonObject row, IReadOnlyList<string> otherIds)
    {
        var conv = (string?)row["conv"] ?? "";
        var values = (row["values"] as JsonObject)?.Select(p => p.Key).ToList() ?? new List<string>();
        switch (conv)
        {
            case "int": case "stackCount": case "formula":
                return v is JsonValue n && n.TryGetValue<double>(out var d) ? JsonValue.Create((long)d + 1) : null;
            case "impactConfig": // float-config probe: fractional even when the current value happens to be whole
                return v is JsonValue ic && ic.TryGetValue<double>(out var c) ? JsonValue.Create(Math.Floor(c) + (c == Math.Floor(c) + 0.5 ? 1.5 : 0.5)) : null;
            case "float":
                return v is JsonValue f && f.TryGetValue<double>(out var x) ? JsonValue.Create(x == 0 ? 1.0 : Math.Round(x * 1.25, 4)) : null;
            case "time":
                return v is JsonValue t && t.TryGetValue<double>(out var s) ? JsonValue.Create(s + 1) : null;
            case "bool":
                return v is JsonValue b && b.TryGetValue<bool>(out var bb) ? JsonValue.Create(!bb) : null;
            case "enum":
                return values.FirstOrDefault(k => k != (string?)v) is { } other ? JsonValue.Create(other) : null;
            case "optionalEnum":
                return (string?)v == "none" ? (values.FirstOrDefault(k => k != "none") is { } o ? JsonValue.Create(o) : null) : JsonValue.Create("none");
            case "flags":
            {
                var have = (v as JsonArray)?.Select(x => (string?)x).Where(x => x != null).Cast<string>().ToList() ?? new List<string>();
                var add = values.FirstOrDefault(k => !have.Contains(k));
                var next = add != null ? have.Append(add).ToList() : have.Skip(1).ToList();
                return new JsonArray(next.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            }
            case "unlock":
                return v is JsonValue u && u.TryGetValue<string>(out var us) ? JsonValue.Create(us == "always" ? "game" : "always") : null;
            case "ref": // another id of the referenced table (items, input categories); a reference without ids at hand gets no probe
                return v is JsonValue rv && rv.TryGetValue<string>(out var cur) && otherIds.FirstOrDefault(i => i != cur) is { } r ? JsonValue.Create(r) : null;
            case "refList": // the same list with its last reference swapped for another of the table (a count change is not probed)
            {
                if (v is not JsonArray a || a.Count == 0 || a[^1] is not JsonValue lv || !lv.TryGetValue<string>(out var last)) return null;
                if (otherIds.FirstOrDefault(i => i != last) is not { } swap) return null;
                var copy = (JsonArray)a.DeepClone();
                copy[^1] = JsonValue.Create(swap);
                return copy;
            }
            case "list": case "inventoryWalk":
            {
                if (v is not JsonArray a || a.Count == 0) return null;
                var copy = (JsonArray)a.DeepClone();
                if (copy[0] is JsonObject el)
                {
                    // a count first (+1 stays in its range), then a weight; a chance (0..1) halves, or becomes 0.5 from 0
                    foreach (var k in new[] { "count", "count.max", "max", "count.min", "min", "weight", "chance" })
                        if (el[k] is JsonValue lv && lv.TryGetValue<double>(out var num))
                        {
                            el[k] = k == "chance" ? JsonValue.Create(num == 0 ? 0.5 : Math.Round(num * 0.5, 6))
                                : num == Math.Floor(num) ? JsonValue.Create((long)num + 1) : JsonValue.Create(Math.Round(num * 1.25, 4));
                            return copy;
                        }
                    return null;
                }
                if (copy[0] is not JsonValue ev || !ev.TryGetValue<double>(out var e0)) return null;
                copy[0] = e0 == Math.Floor(e0) ? JsonValue.Create((long)e0 + 1) : JsonValue.Create(Math.Round(e0 * 1.25, 4));
                return copy;
            }
            default: return null; // anything else: no probe here (unproven, never guessed)
        }
    }

    /// <summary>The probe of a field over what the entry reads and its raw members: <see cref="Probe"/>, except that an
    /// optional enum read as "none" takes the first rarity whose game enum differs from the enum below the switch (raw
    /// [switch, enum]), so the round trip writes both members, not the switch alone (review registry-proof: "common" over an
    /// entry whose enum below was already Common).</summary>
    private static JsonNode? ProbeOf(JsonNode? v, JsonArray? raw, JsonObject row, IReadOnlyList<string> otherIds)
    {
        if ((string?)row["conv"] == "optionalEnum" && (string?)v == "none" && row["values"] is JsonObject values &&
            raw is { Count: 2 } && raw[1] is JsonValue below && below.TryGetValue<string>(out var enumBelow))
            foreach (var (kiln, game) in values)
                if (kiln != "none" && game is JsonValue g && g.TryGetValue<string>(out var name) && !string.Equals(name, enumBelow, StringComparison.OrdinalIgnoreCase))
                    return JsonValue.Create(kiln);
        return Probe(v, row, otherIds);
    }

    private static string Digest(string common)
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(common, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal))
            sb.Append(f).Append('\n').Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(common, f!))))).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // what phase 1 gathered, for phase 2 and the suite
    private string _proof = "", _side = "server", _exe = "";
    private JsonObject _ids = new();
    private readonly JsonArray _samples = new(), _effects = new();
    private readonly JsonObject _kinds = new(), _notInWorld = new(), _unsampled = new();
    private readonly List<EntityId> _spawned = new();
    private WorldPosition? _at;

    /// <summary>Phase 1. True when phase 2 follows (it then writes the suite and the summary).</summary>
    private bool Run()
    {
        _kiln = Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", ".."));
        var proof = _proof = Path.Combine(_kiln, "data", "content", "registry", "proof");
        if (!File.Exists(Path.Combine(proof, "request.json")))
        {
            Log.Info("registry-proof-test: no content/registry/proof/request.json: nothing to prove (lab only)");
            return false;
        }
        var bridge = typeof(Content).Assembly.GetType("Kiln.BlocksBridge");
        _table = (byte*)(nint)(bridge?.GetMethod("ServiceTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
            .Invoke(null, new object[] { "kiln.registry.live", 1u }) ?? (nint)0);
        _owner = (ulong)(typeof(ModContext).GetProperty("Owner", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Context) ?? 0UL);
        Check(_table != null && *(uint*)_table >= 56 && _owner != 0, "kiln.registry.live v1 and the mod's owner");
        if (_table == null || *(uint*)_table < 56 || _owner == 0) return false;
        var stateFile = Path.Combine(_kiln, "state", "registry.json");
        if (!File.Exists(stateFile)) { Log.Info("registry-proof-test: unproven: no registry state yet"); return false; }
        var state = JsonNode.Parse(File.ReadAllText(stateFile))!;
        var side = _side = (string?)state["regmap"]?["side"] ?? "server";
        var exe = _exe = (string?)state["regmap"]?["exeSha256"] ?? "";
        var idsFile = Path.Combine(_kiln, "data", "content", "registry", $"ids-{side}-{exe}.json");
        if (!File.Exists(idsFile)) { Log.Info("registry-proof-test: unproven: no id table yet"); return false; }
        var ids = _ids = JsonNode.Parse(File.ReadAllText(idsFile))!["registries"]!.AsObject();
        Check(exe.Length == 64, $"the running build {exe}");
        foreach (var registry in Live.Where(r => r != "drops")) SampleRegistry(registry);
        foreach (var registry in GameDataLive) SampleGameData(registry);  // dev.32: kiln.gamedata's entries (RegistryProofTestMod.GameData.cs)
        return true;
    }

    /// <summary>Every mapped row of one live registry over the entries Content lists now (a field it gets no sample of: the
    /// log says why, and <see cref="_unsampled"/> where the producer knows why in Kiln words).</summary>
    private void SampleRegistry(string registry)
    {
        var (ids, samples, kinds, notInWorld, unsampled) = (_ids, _samples, _kinds, _notInWorld, _unsampled);
        {
            var map = JsonNode.Parse(File.ReadAllText(Path.Combine(_kiln, "regmap", "common", registry + ".json")))!;
            var table = ids[registry]?.AsObject();
            if (table is null) { Log.Info($"registry-proof-test: unproven: {registry}: no id table yet"); return; }
            // the registry's entries with their Kiln ids (GUID, else "#<32-bit id>")
            var entries = new List<(ContentEntry E, string Id)>();
            var seen = 0;
            foreach (var e in RegistryOf(registry))
            {
                var id = (string?)table[e.Guid.ToString()] ?? (string?)table[BytesText(e.Guid)] ?? (string?)table["#" + e.Id.ToString(CultureInfo.InvariantCulture)];
                if (id != null) entries.Add((e, id));
                ++seen;
            }
            Log.Info($"registry-proof-test: {registry}: {seen} game entries, {entries.Count} with a Kiln id (of {table.Count})");
            if (registry == "drops" && entries.Count == 0 && table.Count > 0)
            {
                // review2 S5: the game loads drop tables as players need them; the id table names them all. Try its entries
                // through kiln.registry.live: a table the live back-end can read would be sampled once Content lists it.
                var rcs = new SortedDictionary<int, int>();
                var readable = 0;
                foreach (var idNode in table.Select(p => p.Value).Take(50))
                {
                    if ((string?)idNode is not { } id) continue;
                    if (Read(registry, id, "entries", out var rc) != null) ++readable;
                    else rcs[rc] = rcs.GetValueOrDefault(rc) + 1;
                }
                Log.Info($"registry-proof-test: drops: Content lists no drop table; of the id table's first {Math.Min(50, table.Count)}, {readable} read live" +
                         (rcs.Count > 0 ? ", refused " + string.Join(", ", rcs.Select(p => $"rc={p.Key} x{p.Value}")) : ""));
                if (readable == 0)
                    foreach (var (field, rowNode) in map["rows"]!.AsObject())
                        if ((string?)rowNode?["state"] != "unmapped")
                            unsampled[$"{registry}.{field}"] = _noTables;
            }
            foreach (var (field, rowNode) in map["rows"]!.AsObject())
            {
                var row = rowNode!.AsObject();
                var paths = row["paths"]?.AsArray().Select(p => (string)p!).ToList() ?? new List<string>();
                if ((string?)row["state"] == "unmapped" || paths.Count == 0) continue;
                var backend = (string?)row["backend"] ?? "";
                if ((string?)row["conv"] == "impactConfig")
                    kinds[$"{registry}.{field}"] = CountKinds(registry, field, row, paths, entries, table, notInWorld);
                // Never infer the config's representation from whether its current value is whole.
                foreach (var mode in (string?)row["conv"] == "impactConfig" ? new[] { "sint32", "sint32-refusal", "float" } : new[] { "" })
                {
                    var sample = SampleOf(registry, field, row, paths, backend, entries, map["rows"]!.AsObject(), ids, mode, unsampled);
                    if (sample != null) samples.Add(sample);
                }
            }
        }
    }

    private void WriteSuite()
    {
        var doc = new JsonObject { ["format"] = 1, ["kind"] = "registry-roundtrip", ["side"] = _side, ["exeSha256"] = _exe, ["generation"] = "live",
            ["regmapDigest"] = Digest(Path.Combine(_kiln, "regmap", "common")), ["constants"] = Constants(_ids), ["kinds"] = _kinds, ["notInWorld"] = _notInWorld,
            ["unsampled"] = _unsampled, ["samples"] = _samples, ["effects"] = _effects };
        Directory.CreateDirectory(_proof);
        var path = Path.Combine(_proof, $"roundtrip-{_side}-live.json");
        File.WriteAllText(path + ".tmp", doc.ToJsonString());
        File.Move(path + ".tmp", path, true);
        Log.Info($"registry-proof-test: {(_samples.Count == 0 ? "unproven: no evidence; " : "")}wrote {_samples.Count} live samples and {_effects.Count} roll effects to {path}");
    }

    /// <summary>How many of the build's entries (every entry of the id table, also those Content's enumeration does not list)
    /// hold an impact config in each kind: { "sint32": n, "float": n, "unknown": n, "notInWorld": n }. kilnctl requires
    /// evidence only for the kinds present; an entry that holds the value but whose kind cannot be read is "unknown" (never
    /// assumed absent). An entry the lab's world does not hold and nothing reads is "notInWorld", its id added to
    /// <paramref name="notInWorld"/> under the field: kilnctl checks the counts against the values export (review3 S1).</summary>
    private JsonObject CountKinds(string registry, string field, JsonObject row, List<string> paths, List<(ContentEntry E, string Id)> entries,
                                  JsonObject table, JsonObject notInWorld)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["sint32"] = 0, ["float"] = 0, ["unknown"] = 0, ["notInWorld"] = 0 };
        var outside = new JsonArray();
        if (ConfigId((string?)row["config"]) is not { } configId)
        {
            Log.Info($"registry-proof-test: {registry}.{field}: Kiln has no config id for {(string?)row["config"]}: its kinds are not counted");
            return new JsonObject { ["unknown"] = table.Count };
        }
        var path = paths[0] + "." + configId + ".value";
        var byKey = new Dictionary<string, ContentEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (e, _) in entries) { byKey[e.Guid.ToString()] = e; byKey[BytesText(e.Guid)] = e; }
        foreach (var (key, idNode) in table)
        {
            if ((string?)idNode is not { } id) continue;
            var text = Read(registry, id, field);
            var holds = text != null && JsonNode.Parse(text) is JsonValue jv && jv.TryGetValue<double>(out _);
            var inWorld = byKey.TryGetValue(key, out var e);
            var value = inWorld ? ConfigValue(e!, path) : ConfigValueOf(key, path);
            if (ConfigKind(value?.Type, holds, inWorld) is not { } kind) continue;
            counts[kind]++;
            if (kind == "notInWorld") outside.Add(id);
        }
        notInWorld[$"{registry}.{field}"] = outside;
        Log.Info($"registry-proof-test: {registry}.{field}: config kinds over {table.Count} entries: " + string.Join(", ", counts.Select(p => $"{p.Key} {p.Value}")));
        var o = new JsonObject();
        foreach (var (k, v) in counts) o[k] = v;
        return o;
    }

    /// <summary>The game's config id of a Kiln impact config name (Kiln's translation, docs/kiln/services/gamedata.md); null for
    /// a name Kiln has no id for (no probe, never another config's id).</summary>
    private static string? ConfigId(string? config) => config switch
    {
        "ManaCost" => "0xaf236ece",
        "StaminaCost" => "0x727d7f78",
        _ => null,
    };

    private JsonObject Constants(JsonObject ids)
    {
        var o = new JsonObject();
        var bal = ids["balance"]?.AsObject().Select(p => (string?)p.Value).FirstOrDefault();
        if (bal is null) return o;
        foreach (var k in new[] { "gear.seed", "gear.creep", "gear.armorOffset", "gear.armorGradient" })
            if (Read("balance", bal, k) is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) o[k] = d;
        return o;
    }

    /// <summary>One sample of a field: the first entry that reads (and, for a writable field, takes a probe).</summary>
    private JsonObject? SampleOf(string registry, string field, JsonObject row, List<string> paths, string backend,
                                 List<(ContentEntry E, string Id)> entries, JsonObject rows, JsonObject idTables, string mode = "",
                                 JsonObject? unsampled = null)
    {
        var q = $"{registry}.{field}";
        var element = field.Contains("[].");
        var list = element ? field[..field.IndexOf("[].", StringComparison.Ordinal)] : "";
        var listPath = element ? ((string?)rows[list]?["paths"]?[0] ?? "") : "";
        var rawPaths = paths.Select(p => !element ? p : registry == "drops" ? $"[0].{p}" : $"{listPath}[0].{p}").ToList();
        var conv = (string?)row["conv"] ?? "";
        var leafName = element ? field[(field.IndexOf("[].", StringComparison.Ordinal) + 3)..] : field;
        // the table a reference names (the schema's target): items, input categories, perks; others get no probe
        var table = conv is "ref" or "refList" ? leafName switch { "item" => "items", "category" => "inputCategories", "perks" => "perks", _ => "" } : "";
        var ids = table.Length > 0 ? idTables[table]?.AsObject().Select(p => (string?)p.Value).Where(v => v != null).Cast<string>().Take(64).ToList() ?? new() : new List<string>();
        var configId = ConfigId((string?)row["config"]);
        if (conv == "impactConfig" && configId is null)
        {
            Log.Info($"registry-proof-test: no sample of {q}: Kiln has no config id for {(string?)row["config"]}");
            return null;
        }
        var configPath = paths[0] + "." + configId + ".value";
        // a structured value (list, reference, unlock...) is not one scalar member: its raw view is the entry's resource bytes;
        // a scalar of a recipe's list element (outputs[].count), which Content.Get does not address, from the game's stacks
        JsonArray RawOf(ContentEntry e, AssetKey? key) =>
            new(rawPaths.Select((p, i) => FlagsRaw(conv, conv == "impactConfig" ? ConfigRaw(e, configPath) : Raw(e, p)) ?? (registry == "recipes" && element && Compared.Contains(conv) ? StackRaw(e, listPath, paths[i]) : null)
                ?? (Compared.Contains(conv) ? null : JsonValue.Create("resource:" + BytesSha(key)))).ToArray());
        var tries = 0;
        var why = new SortedDictionary<string, int>(StringComparer.Ordinal); // why the entries tried gave no sample (the log, when none did)
        void Skip(string reason) => why[reason] = why.GetValueOrDefault(reason) + 1;
        // a cost config is on few items (about 150 of 4,213): every entry is looked at for one of each kind
        var candidates = mode.Length > 0 ? entries : entries.Take(400);
        // a reference table the id table does not list (perks, input categories: their ids are written only when a model
        // decodes references): other ids of the same table as this field reads them on other entries (Kiln's own ids)
        if (table.Length > 0 && ids.Count == 0 && !element)
            ids = candidates.Select(x => Read(registry, x.Id, field)).Where(t => t != null).SelectMany(t => JsonNode.Parse(t!) switch
                {
                    JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var r) ? r : null),
                    JsonValue v when v.TryGetValue<string>(out var r) => new[] { r },
                    _ => Array.Empty<string?>(),
                }).Where(r => r != null).Cast<string>().Distinct().Take(64).ToList();
        if (backend != "live" && conv == "bool") candidates = candidates.OrderByDescending(x => Read(registry, x.Id, element ? list : field) == "true");
        foreach (var (e, id) in candidates)
        {
            if (mode.Length > 0)
            {
                ContentValueType kind;
                if (ConfigValue(e, configPath) is not { } cv) { Skip("config kind unreadable"); continue; }
                kind = cv.Type;
                if (kind != (mode == "float" ? ContentValueType.Float : ContentValueType.Int)) continue;
            }
            var readText = Read(registry, id, element ? list : field, out var rc);
            if (readText is null) { Skip($"read rc={rc}"); continue; }
            var whole = JsonNode.Parse(readText);
            JsonNode? read = whole;
            if (registry == "drops" && whole is JsonArray dl && dl.Any(x => x is JsonObject o && ((string?)o["item"])?.StartsWith('#') == true))
            {
                Skip("a random-loot stack (no item) in the table"); // its item reads as no Kiln id: a table of plain stacks is sampled
                continue;
            }
            if (element)
            {
                if (whole is not JsonArray a || a.Count == 0 || a[0] is not JsonObject el) { Skip("an empty list"); continue; }
                read = Leaf(el, leafName);
                if (read is null) { Skip("element 0 has none"); continue; }
            }
            if (read is null || (read is JsonArray ra && ra.Count == 0)) { Skip("null or empty"); continue; }
            var s = new JsonObject { ["field"] = q, ["entry"] = id, ["element"] = element ? 0 : null, ["read"] = read.DeepClone() };
            var key = ResourceOf(registry, e);
            s["rawBefore"] = RawOf(e, key);
            var before = BytesSha(key);
            s["bytesBefore"] = before;
            if (backend != "live") return s; // read-only (and none): read only
            var probe = mode == "sint32" && read is JsonValue iv && iv.TryGetValue<double>(out var integer)
                ? JsonValue.Create(integer + (integer < int.MaxValue ? 1 : -1)) : ProbeOf(read, s["rawBefore"] as JsonArray, row, ids);
            if (mode.Length > 0) s["configKind"] = mode == "float" ? "float" : "sint32";
            if (probe is null || JsonNode.DeepEquals(probe, read)) { Skip("no probe"); continue; } // another entry may take one
            JsonNode op = probe;
            if (element)
            {
                var copy = (JsonArray)whole!.DeepClone();
                SetLeaf((JsonObject)copy[0]!, leafName, probe);
                op = copy;
            }
            var (ok, refusal, exactRefusal) = Change(registry, id, element ? list : field, op, out var handle);
            // a float at the top of its range (a chance of 1.0): the parse refuses 1.25 x, sometimes without a reason text
            if (!ok && mode.Length == 0 && !IsDisagreement(refusal) && LowerProbe(read, row) is { } lower && !JsonNode.DeepEquals(lower, read))
            {
                probe = lower;  // the same entry with 0.75 x
                op = probe;
                if (element)
                {
                    var copy = (JsonArray)whole!.DeepClone();
                    SetLeaf((JsonObject)copy[0]!, leafName, probe);
                    op = copy;
                }
                (ok, refusal, exactRefusal) = Change(registry, id, element ? list : field, op, out handle);
            }
            // registry-followups 4: a save check refused the probe (a levels.min raised narrows the item's level range): the other
            // way on the same entry; an entry that cannot go that way is skipped without counting as a failed try (it says
            // nothing about the mapping), so the producer looks on to an entry that can (gear has a lowest level of 2 or more)
            if (!ok && mode.Length == 0 && IsSaveCheck(refusal))
            {
                if (SaveCheckProbe(read, row, refusal) is { } other && !JsonNode.DeepEquals(other, read))
                {
                    probe = other;
                    op = probe;
                    if (element)
                    {
                        var copy = (JsonArray)whole!.DeepClone();
                        SetLeaf((JsonObject)copy[0]!, leafName, probe);
                        op = copy;
                    }
                    (ok, refusal, exactRefusal) = Change(registry, id, element ? list : field, op, out handle);
                }
                if (!ok && IsSaveCheck(refusal)) { Skip("a save check refused the probe"); continue; }
            }
            s["probe"] = probe.DeepClone();
            if (mode == "sint32-refusal")
            {
                s["expectRefused"] = true;
                s["refused"] = !ok && exactRefusal;
                s["refusalCode"] = exactRefusal ? "not_exact" : "";
                s["refusalReason"] = refusal;
                s["readBack"] = JsonNode.Parse(Read(registry, id, field) ?? "null");
                s["rawAfter"] = RawOf(e, key);
                if (ok) s["reverted"] = Finish(handle, true).Outcome == Reverted;
                s["bytesAfter"] = BytesSha(key);
                return s; // accepting 2.5 on Sint32 is changed, even if its rounded read-back happens to equal the base
            }
            if (!ok && IsDisagreement(refusal))
            {
                // the mapping disagrees with the build: a sample with the error (kilnctl: changed), not missing evidence
                s["error"] = "the live back-end refused the probe: " + refusal;
                return s;
            }
            if (!ok)
            {
                // An entry without this config, an unmapped row or an out-of-range probe supplies no evidence.
                // Expected Sint32 refusals above are different: their exactness contract is explicitly tested.
                Skip("probe refused: " + refusal);
                if (++tries >= 20) break;
                continue;
            }
            var back = Read(registry, id, element ? list : field);
            var backNode = back is null ? null : JsonNode.Parse(back);
            s["readBack"] = element && backNode is JsonArray ba && ba.Count > 0 && ba[0] is JsonObject bel
                ? Leaf(bel, leafName)?.DeepClone() : backNode?.DeepClone();
            s["rawAfter"] = RawOf(e, key);
            var revert = Finish(handle, true);
            s["reverted"] = revert.Outcome == Reverted;
            s["bytesAfter"] = BytesSha(key);
            return s;
        }
        Log.Info($"registry-proof-test: no sample of {q} ({entries.Count} entries): " + (why.Count == 0 ? "no entry" : string.Join(", ", why.Select(p => $"{p.Key} x{p.Value}")))
                     + (backend == "live" && why.Count == 1 && why.Keys.First().StartsWith("read rc=", StringComparison.Ordinal) ? WriteWhy(registry, entries[0].Id, field, conv) : ""));
        if (unsampled != null && why.Count == 1 && why.ContainsKey("null or empty") && unsampled[q] is null) unsampled[q] = NoValue;
        return null; // no entry took a probe: unproven, not a disproof
    }

    /// <summary>Why kiln.registry.live refuses a live field it reads for no entry: one write of a value the field takes (a bool's
    /// true, unlock's always), its refusal for the log; reverted at once if it applies. Never a sample.</summary>
    private string WriteWhy(string registry, string id, string field, string conv)
    {
        JsonNode? v = conv switch { "bool" => JsonValue.Create(true), "unlock" => JsonValue.Create("always"), _ => null };
        if (v is null) return "";
        var (ok, refusal, _) = Change(registry, id, field, v, out var handle);
        if (!ok) return $"; a write to {id} is refused: {refusal}";
        Finish(handle, true);
        return $"; a write to {id} applies (reverted at once): the read alone fails";
    }

    /// <summary>The GUID's 16 bytes in memory order as GUID text (kiln.content's guid_text), next to .NET's ToString,
    /// which turns the first three groups around.</summary>
    private static string BytesText(Guid g)
    {
        var h = Convert.ToHexStringLower(g.ToByteArray());
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    /// <summary>An element's leaf: a flat key with dots first (kiln.registry.live's struct values: a drop entry's
    /// <c>"count.min"</c>), else nested objects.</summary>
    private static JsonNode? Leaf(JsonObject el, string leaf)
    {
        if (el.ContainsKey(leaf)) return el[leaf];
        JsonNode? cur = el;
        foreach (var part in leaf.Split('.')) cur = (cur as JsonObject)?[part];
        return cur;
    }

    private static void SetLeaf(JsonObject el, string leaf, JsonNode value)
    {
        if (el.ContainsKey(leaf) || !leaf.Contains('.')) { el[leaf] = value.DeepClone(); return; }
        var parts = leaf.Split('.');
        var cur = el;
        for (var i = 0; i < parts.Length - 1; i++) cur = (JsonObject)cur[parts[i]]!;
        cur[parts[^1]] = value.DeepClone();
    }

    /// <summary>A flags row's raw member as the list of the game's names (also one name or none); other rows as they are.</summary>
    private static JsonNode? FlagsRaw(string conv, JsonNode? raw) =>
        conv == "flags" && raw is JsonValue v && v.TryGetValue<string>(out var names) ? FlagNames(names) : raw;

    /// <summary>A bitmask's raw value: the list of the game's names kiln.content gives as text ("Common,Rare", "Epic", "").</summary>
    private static JsonArray FlagNames(string names) =>
        new(names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());

    /// <summary>A refusal of the registry's save checks (registry/saves.h: "&lt;place&gt;: saves: ... this edit was not applied"): the
    /// probe would change what saves hold in a way Kiln refuses (an item's level range narrowed), which says nothing about the
    /// mapping. Known by the place's ": saves: " alone: kiln.registry.live's reply cuts its reason to 192 bytes, so the end of
    /// the sentence is often not there (the 31.x lab run).</summary>
    private static bool IsSaveCheck(string refusal) => refusal.Contains(": saves: ", StringComparison.Ordinal);

    /// <summary>The probe the other way after a save check refused one (registry-followups 4): a whole number one lower, at 1 or
    /// more (a lowest level raised narrows the range; lowered, it widens it). Null for any other refusal, a fraction, or a
    /// number that cannot go lower.</summary>
    private static JsonNode? SaveCheckProbe(JsonNode? v, JsonObject row, string refusal) =>
        IsSaveCheck(refusal) && (string?)row["conv"] is "int" or "stackCount" && v is JsonValue n && n.TryGetValue<double>(out var d) &&
        d == Math.Floor(d) && d - 1 >= 1 ? JsonValue.Create((long)d - 1) : null;

    /// <summary>A second probe for a float the field's range refused at 1.25 x (a chance of 1.0, a block of 1.0): 0.75 x.
    /// Null for every other conversion (no second guess).</summary>
    private static JsonNode? LowerProbe(JsonNode? v, JsonObject row) =>
        (string?)row["conv"] == "float" && v is JsonValue f && f.TryGetValue<double>(out var x) && x != 0 ? JsonValue.Create(Math.Round(x * 0.75, 6)) : null;

    /// <summary>A loot roll's scaled stack (0x16db30 on 26430253): n x the world's loot amount, at least 1, at most
    /// <paramref name="maxScaled"/> (for a whole product; the game rounds a fraction up by chance).</summary>
    private static int ScaledCount(int n, double amount, int maxScaled) =>
        Math.Min(Math.Max((int)Math.Round(n * amount, MidpointRounding.AwayFromZero), 1), maxScaled);
}
