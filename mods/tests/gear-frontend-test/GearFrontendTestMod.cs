// SPDX-License-Identifier: MIT
// The in-process half of tools/gear-frontend (dev.32 J2): gear and gearRules (content/gear.json) against the same edits as
// registry edits (registry/registry.json, which the session puts in content/ instead). At every content ready (the world's
// load, a pack reload) one dump, <data>/dump-<n>.json (n from 1):
//   - "items": the served bytes of every keen::ItemInfo as the game reads them now (kiln.assets: Kiln's override if any,
//     else the game's resident copy, which kiln.content's layers write), by GUID, SHA-256;
//   - "balance": the keen::BalancingTable's served bytes (hex) and SHA-256 (a boot copy: the resource patch's or the
//     registry's);
//   - "targets": the gear rules' items by name, their values at the members the rules write (to read a difference);
//   - "registry": state/registry.json as it is then.
// Writes nothing else and changes nothing. "gear-frontend-test: DUMP <n> <file> items=<k>" in kiln.log.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Kiln;

namespace GearFrontendTest;

public sealed class GearFrontendTestMod : Mod
{
    private static readonly string[] Targets =
    {
        "Weapon_T1_1H_Sword_Craft", "Armor_T2_Warrior_Tank_Chest", "Weapon_T1_2H_Hammer_01", "Weapon_T1_1H_Axe_01", "Shield_T1_DPS",
        "Ring_T3_Ring_of_Light", "Weapon_T1_2H_Staff_01", "Armor_T1_Warrior_DPS_Helmet", "Weapon_T1_2H_Bow_01", "Weapon_T1_1H_Sword_01",
    };
    private static readonly string[] Paths =
    {
        "damageSetup.dmgMod", "damageSetup.speedMod", "damageSetup.distribution.physicalCutDamage", "damageSetup.distribution.magicalFireDamage",
        "damageSetup.distribution.woodDamage", "armorSetup.distribution.physical", "armorSetup.distribution.magical", "blockSetup.quality", "rarity",
        "generateRarity", "gemSlotOverride", "itemLevelRange.minLevel", "itemLevelRange.maxLevel",
    };
    private int _n;

    public override void OnLoad()
    {
        Content.OnReady(e =>
        {
            try { Write(e.Reload); }
            catch (Exception x) { Log.Error($"gear-frontend-test: FAIL the dump threw: {x}"); }
        });
    }

    private static string Text(ContentValue v) => v.Type switch
    {
        ContentValueType.Float or ContentValueType.Time => v.Number.ToString("R", CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private void Write(bool reload)
    {
        var n = ++_n;
        var items = new JsonObject();
        foreach (var key in Assets.ListResources(Assets.TypeHash("keen::ItemInfo")))
        {
            var bytes = Assets.ReadResource(key);
            items[key.Guid.ToString()] = bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        var balance = new JsonObject();
        foreach (var key in Assets.ListResources(Assets.TypeHash("keen::BalancingTable")))
        {
            var bytes = Assets.ReadResource(key);
            balance[key.Guid.ToString()] = bytes is null ? null : new JsonObject
            {
                ["size"] = bytes.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["hex"] = Convert.ToHexStringLower(bytes),
            };
        }
        var targets = new JsonObject();
        foreach (var name in Targets)
        {
            var entry = Content.Items.Get(name);
            if (entry is null) { targets[name] = null; continue; }
            var values = new JsonObject { ["guid"] = entry.Guid.ToString() };
            foreach (var path in Paths)
            {
                try { values[path] = Text(entry.Get(path)); }
                catch (Exception) { values[path] = null; }
            }
            targets[name] = values;
        }
        var statePath = Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", "..", "state", "registry.json"));
        var doc = new JsonObject
        {
            ["format"] = 1, ["n"] = n, ["reload"] = reload, ["items"] = items, ["balance"] = balance, ["targets"] = targets,
            ["registry"] = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath)) : null,
        };
        Directory.CreateDirectory(Context.DataDirectory);  // the session clears it before each boot
        var path2 = Path.Combine(Context.DataDirectory, $"dump-{n}.json");
        File.WriteAllText(path2, doc.ToJsonString());
        Log.Info($"gear-frontend-test: DUMP {n} {path2} items={items.Count} balance={balance.Count}");
    }
}
