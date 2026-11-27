// SPDX-License-Identifier: MIT
// The older mod of the registry lab's mixed set (registry P1 S12): content/old.json (an old data pack, Material_T1_Wood_Logs
// maxStackSize x2) and blocks/patches.json (an old boot patch on keen::BalancingTable: experienceNeedPerLevel[0] 1500,
// playerLevelCap 55), the writers registry-mixed-test's registry edits layer on (owner decision D1); since dev.32 also old
// gamedata sections under the creatures, arsenals, perks and skills registries. And the in-process half of tools/registry-compat:
// at every content ready (the world's load, each pack hot reload) one dump, <data>/dump-<n>.json (n counts from 1):
//   - "resources": every resource of the registries' resource types as the game reads it now (kiln.assets: an override if
//     one is registered, else the game's own), by key, with its size and SHA-256: compare.py checks every one the registry
//     does not touch against the game's archive (a Kiln-off boot reads exactly those bytes);
//   - "store": kiln.content's changed fields (original, current) and conflicts (Content.Dump, content-dump/1);
//   - "touched": the resources those changed fields live in (the store writes the game's loaded resource bytes: an item's
//     own resource, the recipe and loot registries' one resource each, a drop table's);
//   - "registry": state/registry.json as it is then.
// dev.32: the resources of templates, the arsenal registry, perks, the skill tree and the workshop registry too, kiln.gamedata's
// changed leaves of the templates, arsenals and perks sections, and the skill nodes' changed costs.
// Writes nothing else and changes nothing. "registry-compat-test: DUMP <n> <file> resources=<k>" in kiln.log.
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Kiln;

namespace RegistryCompatTest;

public sealed class RegistryCompatTestMod : Mod
{
    private static readonly string[] Types =
    {
        "keen::ItemInfo", "keen::RecipeRegistryResource", "keen::LootableItemsResource", "keen::ecs::DefaultInventoryResource", "keen::BalancingTable",
        // dev.32: creatures (templates), arsenals, perks; skills (its cost: kiln.gamedata's skills slots) and stations
        "keen::ecs::TemplateResource", "keen::enemy::EnemyArsenalRegistryResource", "keen::Perk", "keen::SkillTreeResource", "keen::WorkshopRegistryResource",
    };
    // the kiln.gamedata sections the dev.32 registries live in, with the resource an entry is in (null: its own, by its GUID)
    private static readonly (GameDataRegistry Section, string Name, string? Shared)[] Sections =
    {
        (Content.Templates, "templates", null), (Content.Arsenals, "arsenals", "keen::enemy::EnemyArsenalRegistryResource"), (Content.Perks, "perks", null),
    };
    private int _n;

    public override void OnLoad()
    {
        Content.OnReady(e =>
        {
            try { Write(e.Reload); }
            catch (Exception x) { Log.Error($"registry-compat-test: FAIL the dump threw: {x}"); }
        });
    }

    private void Write(bool reload)
    {
        var n = ++_n;
        var res = new JsonArray();
        foreach (var type in Types)
            foreach (var key in Assets.ListResources(Assets.TypeHash(type)))
            {
                var bytes = Assets.ReadResource(key);
                res.Add(new JsonObject
                {
                    ["type"] = type, ["typeHash"] = key.Type, ["guid"] = key.Guid.ToString(), ["part"] = key.Part,
                    ["size"] = bytes?.Length ?? -1, ["sha256"] = bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes)),
                });
            }
        var store = Path.Combine(Context.DataDirectory, $"store-{n}.json");
        Content.Dump(store);
        var changed = new JsonArray();
        var full = JsonNode.Parse(File.ReadAllText(store))!;
        foreach (var reg in new[] { "items", "recipes", "loot", "drops" })
            foreach (var e in full[reg]?["entries"]?.AsArray() ?? new JsonArray())
                if (e?["changed"] is JsonObject c)
                    foreach (var (f, v) in c) changed.Add(new JsonObject { ["registry"] = reg, ["entry"] = (string?)e["name"], ["field"] = f, ["value"] = v?.DeepClone() });
        File.Delete(store);
        // dev.32: the skill nodes kiln.gamedata's store changed (an old skills section, the registry's skills.cost): their cost
        foreach (var node in Content.Skills.Where(e => e.Modified))
            changed.Add(new JsonObject { ["registry"] = "skills", ["entry"] = node.Name.Replace("skill_", "0x"), ["field"] = "costs", ["value"] = node.GetNumber("costs") });
        var touched = new SortedSet<string>(StringComparer.Ordinal);
        string? Single(string type) => Assets.ListResources(Assets.TypeHash(type)).Select(k => k.Guid.ToString()).FirstOrDefault();
        foreach (var c in changed)
        {
            var reg = (string?)c!["registry"];
            var name = (string?)c["entry"] ?? "";
            var g = reg switch
            {
                "items" => Content.Items.Get(name)?.Guid.ToString(),
                "drops" => Content.Drops.Get(name)?.Guid.ToString(),
                "recipes" => Single("keen::RecipeRegistryResource"),
                "loot" => Single("keen::LootableItemsResource"),
                "skills" => Single("keen::SkillTreeResource"),
                _ => null,
            };
            if (g is not null) touched.Add(g);
        }
        // dev.32: the gamedata entries the store changed (old gamedata sections, the creatures / arsenals / perks registries): their
        // changed leaves as store fields, their resources as touched
        foreach (var (section, name, shared) in Sections)
            foreach (var e in section.Where(x => x.Modified))
            {
                foreach (var f in e.Fields())
                    if (f.Original is not null && !JsonNode.DeepEquals(f.Value, f.Original))  // original: only on leaves the store holds
                        changed.Add(new JsonObject { ["registry"] = name, ["entry"] = e.Name.Length > 0 ? e.Name : e.Guid.ToString(), ["field"] = f.Path, ["value"] = f.Value?.DeepClone() });
                if ((shared is null ? e.Guid.ToString() : Single(shared)) is { } g) touched.Add(g);
            }
        var statePath = Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", "..", "state", "registry.json"));
        var doc = new JsonObject
        {
            ["format"] = 1, ["n"] = n, ["reload"] = reload, ["resources"] = res, ["touched"] = new JsonArray(touched.Select(t => (JsonNode)t).ToArray()),
            ["store"] = new JsonObject { ["changed"] = changed, ["conflicts"] = full["conflicts"]?.DeepClone() },
            ["registry"] = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath)) : null,
        };
        var path = Path.Combine(Context.DataDirectory, $"dump-{n}.json");
        File.WriteAllText(path, doc.ToJsonString());
        Log.Info($"registry-compat-test: DUMP {n} {path} resources={res.Count} changed={changed.Count}");
    }
}
