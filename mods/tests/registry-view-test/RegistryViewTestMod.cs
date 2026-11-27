// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;
using Kiln.Registry;

namespace RegistryViewTest;

/// <summary>kiln.registry.view on a real server build (lab only): the catalog, one member through both tiers, and the first
/// entry of every advanced view, with every refusal's reason in Kiln words.</summary>
public sealed class RegistryViewTestMod : Mod
{
    private int _pass, _fail;
    private bool _ran;
    private static readonly Regex KilnName = new("^[a-z][A-Za-z0-9]*(_[0-9a-f]{8})?$");
    private static readonly Regex GameWords = new(@"keen::|\+0x[0-9a-fA-F]+|\b0x[0-9a-fA-F]{6,}\b");

    private void Check(bool ok, string text)
    {
        if (ok) ++_pass; else ++_fail;
        Log.Info($"registry-view-test: {(ok ? "PASS" : "FAIL")} {text}");
    }

    public override void OnLoad() => Content.OnReady(e =>
    {
        if (_ran || e.Reload) return;
        _ran = true;
        try { Run(); }
        catch (Exception x) { Check(false, x.ToString()); }
        finally { Log.Info($"registry-view-test: SUMMARY {_pass} PASS, {_fail} FAIL"); }
    });

    private void Run()
    {
        Check(RegistryViews.Available, "kiln.registry.view is served");
        var clock = Stopwatch.StartNew();
        var all = RegistryViews.All;
        var catalogMs = clock.Elapsed.TotalMilliseconds;
        var stable = all.Where(v => v.Tier == RegistryViewTier.Stable).Select(v => v.Name).ToHashSet();
        var advanced = all.Where(v => v.Tier == RegistryViewTier.Advanced).ToList();
        Check(new[] { "items", "recipes", "loot", "drops", "balance" }.All(stable.Contains), $"stable views: {string.Join(", ", stable.Order())}");
        Check(advanced.Count >= 20, $"{advanced.Count} advanced views (the catalog took {catalogMs:F0} ms)");
        var bad = all.Where(v => !KilnName.IsMatch(v.Name)).Select(v => v.Name).ToList();
        Check(bad.Count == 0, bad.Count == 0 ? "every view name is a Kiln name" : $"not Kiln names: {string.Join(", ", bad.Take(5))}");

        // one member through both tiers: the item view's maxStackSize is the items registry's maxStack
        var itemInfo = RegistryViews.Find("itemInfo");
        var field = itemInfo?.Field("maxStackSize");
        Check(field is { CoveredBy: "items.maxStack", Exact: true, Readable: true }, $"itemInfo.maxStackSize is covered by {field?.CoveredBy ?? "nothing"} (exact {field?.Exact})");
        var wood = new RegistryId("game:material_t1_wood_logs");
        if (itemInfo is not null && itemInfo.Select(wood.Value).Count == 1)
        {
            var viaView = itemInfo[wood].Get<int>("maxStackSize");
            var viaRegistry = RegistryViews.Find("items")![wood].Get<int>("maxStack");
            Check(viaView == viaRegistry && viaView > 0, $"{wood.Value}: maxStackSize {viaView} through the advanced view, maxStack {viaRegistry} through the registry");
        }
        else Check(false, $"itemInfo has no {wood.Value}");
        // a member one field maps one to one and formulas only read is that field (review of J1)
        var minLevel = itemInfo?.Field("itemLevelRange.minLevel");
        var speed = itemInfo?.Field("damageSetup.speedMod");
        Check(minLevel is { CoveredBy: "items.levels.min", Exact: true } && speed is { CoveredBy: "items.speed", Exact: true },
            $"itemLevelRange.minLevel is covered by {minLevel?.CoveredBy ?? "nothing"}, damageSetup.speedMod by {speed?.CoveredBy ?? "nothing"}");
        // a stable view selects by an old debug name as a registry edit does (D3)
        var byName = RegistryViews.Find("items")!.Select("Material_T1_Wood_Logs");
        Check(byName.Count == 1 && byName[0] == wood, $"items.Select(\"Material_T1_Wood_Logs\") gives {string.Join(", ", byName.Select(i => i.Value))}");

        // a long listing (the template view's thousands of entries, on a worker) holds back no other call: the registries, another
        // view's fields, the catalog answer meanwhile
        var templates = advanced.FirstOrDefault(v => v.Name == "template");
        double listMs = 0, otherMs = 0;
        if (templates is not null)
        {
            var started = Stopwatch.StartNew();
            var listing = Task.Run(() => templates.Select().Count);
            Thread.Sleep(200);  // the worker is inside the listing by now
            var during = Stopwatch.StartNew();
            var stack = RegistryViews.Find("items")![wood].Get<int>("maxStack");
            var count = RegistryViews.All.Count;
            var recipeFields = RegistryViews.Find("recipeRegistry")?.Fields.Count ?? 0;
            otherMs = during.Elapsed.TotalMilliseconds;
            var stillListing = !listing.IsCompleted;
            var listed = listing.GetAwaiter().GetResult();
            listMs = started.Elapsed.TotalMilliseconds;
            Check(stack > 0 && count > 0 && recipeFields > 0 && (otherMs < 1000 || !stillListing),
                $"while template's {listed} entries were listed ({listMs:F0} ms, still listing: {stillListing}), a registry read, the catalog and another view's fields took {otherMs:F0} ms");
        }
        else Check(false, "no template view");

        // every advanced view: its fields, its entries, the first entry read whole
        clock.Restart();
        var summary = new JsonArray();
        int read = 0, refused = 0, entries = 0, fields = 0, unread = 0, withEntries = 0, leaks = 0, reasons = 0;
        var refusedKinds = new Dictionary<string, int>();
        foreach (var v in advanced)
        {
            var fs = v.Fields;
            fields += fs.Count;
            unread += fs.Count(f => !f.Readable);
            var ids = v.Select();
            entries += ids.Count;
            var row = new JsonObject { ["view"] = v.Name, ["fields"] = fs.Count, ["unread"] = fs.Count(f => !f.Readable), ["entries"] = ids.Count,
                ["coveredBy"] = new JsonArray(fs.Where(f => f.CoveredBy is not null).Select(f => (JsonNode)JsonValue.Create(f.CoveredBy!)!).Distinct().ToArray()) };
            if (ids.Count > 0)
            {
                ++withEntries;
                var snap = v[ids[0]].Snapshot();
                read += snap.Fields.Count;
                refused += snap.Refused.Count;
                row["firstRead"] = snap.Fields.Count;
                row["firstRefused"] = snap.Refused.Count;
                foreach (var r in snap.Refused.Values)
                {
                    ++reasons;
                    if (GameWords.IsMatch(r)) ++leaks;
                    var kind = r.Contains("several kinds") ? "variant" : r.Contains("optional") ? "optional" : r.Contains("configuration") ? "configs"
                        : r.Contains("no value of it") ? "no value" : r.Contains("no name for") ? "unnamed value" : r.Contains("too deep") ? "deep" : "other";
                    refusedKinds[kind] = refusedKinds.GetValueOrDefault(kind) + 1;
                }
            }
            summary.Add(row);
        }
        var walkMs = clock.Elapsed.TotalMilliseconds;
        Check(withEntries > 0 && read > 0, $"{withEntries} of {advanced.Count} advanced views have entries ({entries} in all, {fields} fields, {unread} unread); their first entries: {read} fields read, {refused} refused ({string.Join(", ", refusedKinds.Select(p => $"{p.Key} {p.Value}"))}) in {walkMs:F0} ms");
        Check(leaks == 0, $"{reasons} refusal reasons, {leaks} naming a game type or an address");
        var reasonText = advanced.SelectMany(v => v.Fields).Where(f => f.Reason is not null).Select(f => f.Reason!).FirstOrDefault(r => GameWords.IsMatch(r));
        Check(reasonText is null, reasonText is null ? "no field reason names a game type or an address" : $"a reason names the game: {reasonText}");
        // a refusal per entry: a field this view does not have
        var first = advanced.FirstOrDefault(v => v.Select().Count > 0);
        if (first is not null)
        {
            var ok = first.TryRead(first.Select()[0], "noSuchFieldAnywhere", out _, out var why);
            Check(!ok && why is not null && why.Contains("has no field"), $"an unknown field is refused: {why}");
        }
        var path = Path.Combine(Context.DataDirectory, "views-summary.json");
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(path, new JsonObject { ["stable"] = new JsonArray(stable.Order().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()), ["catalogMs"] = catalogMs,
            ["walkMs"] = walkMs, ["templateListMs"] = listMs, ["otherCallsMs"] = otherMs, ["advanced"] = summary }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"registry-view-test: wrote {path}");
    }
}
