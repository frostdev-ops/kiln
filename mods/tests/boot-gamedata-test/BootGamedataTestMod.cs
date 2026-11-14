// SPDX-License-Identifier: MIT
// The boot game-data snapshot against the live reader (chapter 08 S4's proof: answers/c-06-08-s4-gamedata-reader.md point 4, with the
// rulings of answers/d-04-08b-stop1-snapshot-ready.md). OnBoot reads the chapter's five sample queries through boot.GameData.Read and writes
// the results into the mod's data folder (the boot-author instance is unloaded after OnBoot). Once the world's content is ready, the runtime
// instance reads the same five through kiln.content's live reader and prints one line each into kiln.log:
//   boot-gamedata-test: PASS <query>
//   boot-gamedata-test: FAIL <query>: <first differing member path>: <snapshot> vs <live>
// Equality is JSON equality after parsing: member order does not count, integers compare exactly, other numbers as float32. material: has no
// live reader: the row's cell is compared with the GameMaterials catalog rule (WorldTables.GameMaterialId). Skipped, and printed: the member
// paths index.json lists under runtimeOnly; a query whose name the snapshot lists as a duplicate in the archive; config members whose key
// names the same config id under another alias on each side (a mod's programs change the live vocabulary). Optional scalars are null on both
// sides and compare equal. Where mods.boot.game_data is off it prints SKIP lines and DONE, never PASS.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace BootGamedataTest;

public sealed class BootGamedataTestMod : Mod
{
    private const string Id = "boot-gamedata-test";
    private const string Row = "mods.boot.game_data";
    private const string ReadsFile = "boot-reads.json";

    /// <summary>The chapter's five sample queries (§6, the boot-gallery sample).</summary>
    private static readonly string[] Queries =
    [
        "item:Material_T1_Wood_Logs", "item:Prop_Decoration_T2_Vase_Clay_01", "template:NPC_Workshop_Fisher_Trader", "skill:0x8efcc472",
        "material:StoneRough",
    ];

    private int _pass, _fail, _skip;
    private bool _ran;

    // ------------------------------------------------------------------------------------------------- OnBoot ---

    public override void OnBoot(BootContext boot)
    {
        var on = Capabilities.Is(Row);
        var reads = new JsonArray();
        foreach (var q in Queries)
        {
            var o = new JsonObject { ["query"] = q };
            if (!on)
            {
                o["off"] = Capabilities.Why(Row);
                reads.Add(o);
                continue;
            }
            try
            {
                var r = boot.GameData.Read(q);
                o["ok"] = r.Ok;
                if (r is { Ok: true, Data: { } d })
                {
                    var raw = d.Value.GetRawText();
                    o["resourceId"] = d.ResourceId.ToString();
                    o["typeHash"] = d.TypeHash.ToString("x8");
                    o["part"] = d.Part;
                    o["value"] = JsonNode.Parse(raw);
                    Log.Info($"{Id}: boot read {q}: {d.ResourceId}/{d.TypeHash:x8}/{d.Part}, {raw.Length} bytes");
                }
                else
                {
                    o["reason"] = r.Reason;
                    Log.Info($"{Id}: boot read {q}: refused: {r.Reason}");
                }
            }
            catch (KilnUnavailableException e)
            {
                o["off"] = e.Message;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or FormatException)
            {
                o["ok"] = false;
                o["reason"] = $"{e.GetType().Name}: {e.Message}";
            }
            reads.Add(o);
        }
        var doc = new JsonObject { ["pid"] = Environment.ProcessId, ["generation"] = boot.Generation, ["gameDataOn"] = on, ["reads"] = reads };
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Path.Combine(Context.DataDirectory, ReadsFile), doc.ToJsonString());
    }

    // --------------------------------------------------------------------------------------- after world load ---

    public override void OnLoad()
    {
        if (Mods.Side != ModSide.Server) return;
        Content.OnReady(ready =>
        {
            if (_ran) return;
            _ran = true;
            _ = Run();
        });
    }

    private void Line(string verdict, string text)
    {
        switch (verdict)
        {
            case "PASS": _pass++; break;
            case "FAIL": _fail++; break;
            case "SKIP:": _skip++; break;
        }
        var line = $"{Id}: {verdict} {text}";
        if (verdict == "FAIL") Log.Error(line); else Log.Info(line);
    }

    private void Done() => Log.Info($"{Id}: DONE ({_pass} PASS, {_fail} FAIL, {_skip} SKIP)");

    private async Task Run()
    {
        try
        {
            await RunCore();
        }
        catch (Exception e)
        {
            Line("FAIL", $"the comparison threw: {e.GetType().Name}: {e.Message}");
        }
        Done();
    }

    private async Task RunCore()
    {
        // the reads OnBoot wrote in this start
        JsonDocument? doc = null;
        var why = "";
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.DataDirectory, ReadsFile)));
            if (!doc.RootElement.TryGetProperty("pid", out var pid) || pid.GetInt32() != Environment.ProcessId)
            {
                why = "the file is of another start";
                doc.Dispose();
                doc = null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            why = e.Message;
        }
        if (doc is null)
        {
            var state = Mods.BootState(Id);
            var issues = string.Join("; ", state.Issues.Select(i => $"{i.Code}: {i.Reason}"));
            foreach (var q in Queries)
                Line("SKIP:", $"{q}: OnBoot recorded no read in this start ({why}; boot state {state.State}{(issues.Length == 0 ? "" : ", " + issues)})");
            return;
        }
        using var reading = doc;
        var reads = doc.RootElement.GetProperty("reads").EnumerateArray().ToList();
        if (!doc.RootElement.GetProperty("gameDataOn").GetBoolean())
        {
            foreach (var r in reads)
                Line("SKIP:", $"{r.GetProperty("query").GetString()}: {Row} is off: {Str(r, "off")}");
            return;
        }

        // the game data's own tables come up with the world: wait for them
        for (var i = 0; i < 24 * 120 && !(Content.GameDataAvailable && Content.Templates.Count > 0); i++) await Context.Scheduler.NextTick();
        var index = SnapshotIndex(out var indexWhy);
        var runtimeOnly = index is { } ix && ix.RootElement.TryGetProperty("runtimeOnly", out var ro) && ro.ValueKind == JsonValueKind.Array
            ? ro.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : [];
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        if (index is not null && index.RootElement.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.Array)
            foreach (var e in sk.EnumerateArray())
                if (Str(e, "reason") == "duplicate name") duplicates.Add($"{Str(e, "kind")}/{Str(e, "name")}");
        Log.Info($"{Id}: snapshot index: {(index is null ? indexWhy : $"runtimeOnly: {(runtimeOnly.Length == 0 ? "none" : string.Join(", ", runtimeOnly))}; {duplicates.Count} duplicate name(s), skipped where a query names one")}");
        foreach (var group in duplicates.GroupBy(d => d[..d.IndexOf('/')]))
            Log.Info($"{Id}: duplicate {group.Key}: {string.Join(", ", group.Select(d => d[(d.IndexOf('/') + 1)..]))}");
        index?.Dispose();
        var vocabulary = Vocabulary();

        foreach (var r in reads)
        {
            var q = r.GetProperty("query").GetString()!;
            var colon = q.IndexOf(':');
            var (kind, name) = (q[..colon], q[(colon + 1)..]);
            if (r.TryGetProperty("off", out var off))
            {
                Line("SKIP:", $"{q}: {Row} is off: {off.GetString()}");
                continue;
            }
            if (duplicates.Contains($"{kind}s/{name}"))
            {
                Line("SKIP:", $"{q}: a duplicate {kind} name in the archive (the snapshot serves the first in archive order, the live registry its own first)");
                continue;
            }
            if (!r.GetProperty("ok").GetBoolean())
            {
                Line("FAIL", $"{q}: the boot read refused: {Str(r, "reason")}");
                continue;
            }
            var snapshot = r.GetProperty("value");
            if (kind == "material")
            {
                Material(q, name, snapshot);
                continue;
            }
            if (Live(kind, name, out var liveWhy) is not { } live)
            {
                Line("FAIL", $"{q}: the live reader has no value: {liveWhy}");
                continue;
            }
            using (live)
            {
                var root = kind switch { "item" => "ItemInfo", "template" => "TemplateResource", _ => "SkillTreeNode" };
                var compare = new Comparer(root, runtimeOnly, vocabulary);
                var diff = compare.First(snapshot, live.RootElement, "");
                foreach (var s in compare.Skipped) Log.Info($"{Id}: skipped {q}: {s}");
                if (diff is null) Line("PASS", q);
                else Line("FAIL", $"{q}: {diff}");
            }
        }
    }

    /// <summary>material: no live reader (kiln.content has no material section): the snapshot's row must be the row the GameMaterials
    /// catalog rule names so (lane D's point 1). A building config's cell is 128 + buildingMaterial, a terrain config's terrainMaterial.</summary>
    private void Material(string q, string name, JsonElement row)
    {
        int? cell = null;
        if (row.ValueKind == JsonValueKind.Object)
        {
            if (row.TryGetProperty("buildingMaterial", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var n)) cell = 128 + n;
            else if (row.TryGetProperty("terrainMaterial", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var m)) cell = m;
        }
        var live = WorldTables.GameMaterialId(name);
        if (cell is null) Line("FAIL", $"{q}: buildingMaterial/terrainMaterial: {Show(row)} vs cell {live} (no cell number in the snapshot's row)");
        else if (live < 0) Line("FAIL", $"{q}: cell: {cell} vs -1 (WorldTables.GameMaterialId: not a game material, or the tables are not read)");
        else if (cell != live) Line("FAIL", $"{q}: cell: {cell} vs {live}");
        else Line("PASS", $"{q} (materials: checked against the GameMaterials catalog rule, cell {cell})");
    }

    /// <summary>kiln.content's live value of a record: the "value" of GetJson("") (gamedata_api.cpp t_get_json, node_json(root, 12)).</summary>
    private static JsonDocument? Live(string kind, string name, out string why)
    {
        why = "";
        if (!Content.GameDataAvailable)
        {
            why = $"kiln.gamedata is unavailable: {Capabilities.Why("kiln.gamedata")}";
            return null;
        }
        var registry = kind switch { "item" => Content.ItemData, "template" => Content.Templates, _ => Content.Skills };
        if (registry.Get(name) is not { } entry)
        {
            why = $"no {kind} {name} in Content.{(kind == "item" ? "ItemData" : kind == "template" ? "Templates" : "Skills")}";
            return null;
        }
        if (entry.GetJson("")?["value"] is not { } value)
        {
            why = $"{entry} has no value";
            return null;
        }
        return JsonDocument.Parse(value.ToJsonString());
    }

    /// <summary>index.json of the snapshot kiln.boot read: the directory under kiln/data/boot-gamedata whose index names this executable
    /// (kiln/state/boot.json's exe.sha256).</summary>
    private JsonDocument? SnapshotIndex(out string why)
    {
        why = "";
        var data = Path.GetFullPath(Path.Combine(Context.DataDirectory, ".."));
        string? exe = null;
        try
        {
            using var boot = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "..", "state", "boot.json")));
            exe = boot.RootElement.GetProperty("exe").GetProperty("sha256").GetString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            why = $"kiln/state/boot.json: {e.Message}";
        }
        var root = Path.Combine(data, "boot-gamedata");
        if (!Directory.Exists(root))
        {
            why = "no kiln/data/boot-gamedata";
            return null;
        }
        foreach (var dir in Directory.GetDirectories(root))
        {
            var file = Path.Combine(dir, "index.json");
            if (!File.Exists(file)) continue;
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (exe is null || Str(doc.RootElement, "exeSha256") == exe) return doc;
                doc.Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                why = $"{file}: {e.Message}";
            }
        }
        why = why.Length > 0 ? why : "no index.json for this executable under kiln/data/boot-gamedata";
        return null;
    }

    /// <summary>The live config vocabulary: lower-case name or alias -> config ids (Content.ConfigVocabulary).</summary>
    private static Dictionary<string, HashSet<uint>> Vocabulary()
    {
        var map = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(Content.ConfigVocabulary());
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!Comparer.TryConfigId(Str(row, "id"), out var id)) continue;
                var names = row.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Array
                    ? n.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                if (Str(row, "alias") is { Length: > 0 } alias) names.Add(alias);
                foreach (var name in names.Where(x => x.Length > 0))
                {
                    if (!map.TryGetValue(name.ToLowerInvariant(), out var set)) map[name.ToLowerInvariant()] = set = [];
                    set.Add(id);
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KilnUnavailableException)
        {
            // without the vocabulary no alias key is matched: a renamed config shows as a difference
        }
        return map;
    }

    private static string Str(JsonElement e, string member) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(member, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText() : "";

    internal static string Show(JsonElement e)
    {
        var raw = e.GetRawText();
        return raw.Length <= 120 ? raw : raw[..117] + "...";
    }
}

/// <summary>JSON equality after parsing, with the skips of the snapshot contract; <see cref="First"/> gives the first difference.</summary>
internal sealed class Comparer(string root, IReadOnlyList<string> runtimeOnly, Dictionary<string, HashSet<uint>> vocabulary)
{
    private static readonly Regex Index = new(@"\[\d+\]", RegexOptions.CultureInvariant);
    private static readonly Regex Hex = new("^0x[0-9a-fA-F]{8}$", RegexOptions.CultureInvariant);

    /// <summary>What was skipped and why, in order.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>"&lt;path&gt;: &lt;snapshot&gt; vs &lt;live&gt;" for the first difference, or null when equal.</summary>
    public string? First(JsonElement s, JsonElement l, string path)
    {
        if (s.ValueKind != l.ValueKind && !(IsBool(s) && IsBool(l)))
            return $"{Name(path)}: {BootGamedataTestMod.Show(s)} vs {BootGamedataTestMod.Show(l)}";
        switch (s.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var liveOnly = l.EnumerateObject().Select(p => p.Name).Where(n => !s.TryGetProperty(n, out JsonElement _)).ToList();
                foreach (var p in s.EnumerateObject())
                {
                    var child = Join(path, p.Name);
                    if (IsRuntimeOnly(child))
                    {
                        Skipped.Add($"{child} (runtimeOnly)");
                        continue;
                    }
                    if (!l.TryGetProperty(p.Name, out var lv))
                    {
                        if (Alias(p.Name, liveOnly) is { } other)
                        {
                            liveOnly.Remove(other);
                            Skipped.Add($"{child} (config alias: \"{p.Name}\" in the snapshot, \"{other}\" live)");
                            continue;
                        }
                        return $"{child}: {BootGamedataTestMod.Show(p.Value)} vs (missing)";
                    }
                    if (First(p.Value, lv, child) is { } d) return d;
                }
                foreach (var n in liveOnly)
                {
                    var child = Join(path, n);
                    if (IsRuntimeOnly(child))
                    {
                        Skipped.Add($"{child} (runtimeOnly)");
                        continue;
                    }
                    return $"{child}: (missing) vs {BootGamedataTestMod.Show(l.GetProperty(n))}";
                }
                return null;
            }
            case JsonValueKind.Array:
            {
                var a = s.EnumerateArray().ToList();
                var b = l.EnumerateArray().ToList();
                for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
                    if (First(a[i], b[i], $"{path}[{i}]") is { } d) return d;
                if (a.Count == b.Count) return null;
                var n = Math.Min(a.Count, b.Count);
                return a.Count > b.Count
                    ? $"{path}[{n}]: {BootGamedataTestMod.Show(a[n])} vs (missing) ({a.Count} vs {b.Count} items)"
                    : $"{path}[{n}]: (missing) vs {BootGamedataTestMod.Show(b[n])} ({a.Count} vs {b.Count} items)";
            }
            case JsonValueKind.Number:
                return NumbersEqual(s, l) ? null : $"{Name(path)}: {s.GetRawText()} vs {l.GetRawText()}";
            case JsonValueKind.String:
                return s.GetString() == l.GetString() ? null : $"{Name(path)}: {BootGamedataTestMod.Show(s)} vs {BootGamedataTestMod.Show(l)}";
            case JsonValueKind.True or JsonValueKind.False:
                return s.GetBoolean() == l.GetBoolean() ? null : $"{Name(path)}: {s.GetRawText()} vs {l.GetRawText()}";
            default:
                return null;  // null on both sides
        }
    }

    /// <summary>Integers exactly; anything else as the float32 it came from.</summary>
    internal static bool NumbersEqual(JsonElement a, JsonElement b)
    {
        if (a.TryGetInt64(out var x) && b.TryGetInt64(out var y)) return x == y;
        if (a.TryGetUInt64(out var ux) && b.TryGetUInt64(out var uy)) return ux == uy;
        return (float)a.GetDouble() == (float)b.GetDouble();
    }

    /// <summary>A runtimeOnly entry <c>&lt;Type&gt;.&lt;member&gt;</c> skips a member path (indices removed) equal to it, a member at the root
    /// of a record of that type, or a longer path that ends with it.</summary>
    private bool IsRuntimeOnly(string path)
    {
        var p = Index.Replace(path, "");
        foreach (var e in runtimeOnly)
            if (p == e || $"{root}.{p}" == e || p.EndsWith("." + e, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>A key only the live side has that names the same config id as <paramref name="key"/> (by the live vocabulary), or null.</summary>
    private string? Alias(string key, List<string> liveOnly)
    {
        var mine = Ids(key);
        if (mine.Count == 0) return null;
        return liveOnly.FirstOrDefault(k => Ids(k).Overlaps(mine));
    }

    private HashSet<uint> Ids(string key)
    {
        if (Hex.IsMatch(key) && TryConfigId(key, out var id)) return [id];
        return vocabulary.TryGetValue(key.ToLowerInvariant(), out var set) ? set : [];
    }

    internal static bool TryConfigId(string text, out uint id) =>
        uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, System.Globalization.NumberStyles.HexNumber, null, out id);

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string Join(string path, string member) => path.Length == 0 ? member : $"{path}.{member}";

    private static string Name(string path) => path.Length == 0 ? "(the value)" : path;
}
