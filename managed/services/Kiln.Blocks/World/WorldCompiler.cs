// SPDX-License-Identifier: MIT
// Material blocks -> kiln.worldtables. The service read the mods' data files at boot and merged them into the game's shared tables, before anything in the game read them.
// Here a block from a data file finds itself already there; a block from code is compared with what the boot read, and kept for the next start when it's new. A building
// material also gets its block item: a clone of a game block with the material's id and GUID in its voxel fields.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    private const string BlockStarter = "Block_T1_Stone_Rough_CRAFTING", RoofStarter = "Block_Roof_T1_StoneShingles_CRAFTING";

    private static JsonObject? StrengthNode(BlockContext ctx, PartRef<StrengthBlock>? part)
    {
        if (part == null) return null;
        var s = ctx.Part(part, "strength");
        CheckStrength(ctx, s, "strength");
        var o = new JsonObject();
        if (s.Hardness is { } h) o["hardness"] = JsonNamingPolicy.CamelCase.ConvertName(h.ToString());
        if (s.Health is { } hp) o["health"] = hp;
        if (s.Breaks is { } b) o["breaks"] = JsonNamingPolicy.CamelCase.ConvertName(b.ToString());
        if (s.ItemsPerBlocks is { } n) o["itemsPerBlocks"] = n;
        if (s.Drops is { } d) o["drops"] = d;
        return o;
    }

    private static void CheckStrength(BlockContext ctx, StrengthBlock s, string path)
    {
        if (s.Health is < 1 or > 1000000) throw new BlockError(ctx.Block, $"{path}.health", $"{s.Health} is outside 1..1000000");
        if (s.ItemsPerBlocks is < 1 or > 100000) throw new BlockError(ctx.Block, $"{path}.itemsPerBlocks", $"{s.ItemsPerBlocks} is outside 1..100000");
        if (s.Drops is { Length: 0 }) throw new BlockError(ctx.Block, $"{path}.drops", "takes an item: a GameItems name or a GUID");
    }

    private static BlockHandle DefineStrength(BlockContext ctx, Lowered low, string name, StrengthBlock s)
    {
        CheckStrength(ctx, s, "strength");
        low.Json["part"] = name;
        return new BlockHandle<StrengthBlock>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "part", name), name, s);
    }

    /// <summary>The service's word on one block: its status and id, or a BlockError naming the field.</summary>
    private static (TableBlockStatus Status, int CellId) AskTables(BlockContext ctx, Lowered low, string section, string name, JsonObject block)
    {
        var mod = ctx.Mod;
        mod.ManifestClients ??= BlocksBridge.ManifestClients(mod.Id);
        var doc = new JsonObject { ["format"] = 1, ["mod"] = mod.Id, ["dir"] = BlocksBridge.ModDirectory(mod.Id), ["clients"] = mod.ManifestClients, [section] = new JsonObject { [name] = block } };
        var report = WorldTablesNative.DefineJson(doc.ToJsonString());
        if (report == null)
        {
            low.Json["status"] = "off";
            low.AfterCommit(() => BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {name}: kiln.worldtables isn't here on this build: nothing changes in the game's tables"));
            return (TableBlockStatus.Off, -1);
        }
        JsonObject? r;
        try { r = JsonNode.Parse(report) as JsonObject; }
        catch (JsonException) { r = null; }
        if (r == null) throw new BlockError(ctx.Block, null, $"kiln.worldtables gave no report: {report}");
        foreach (var e in r["errors"] as JsonArray ?? [])
        {
            var (field, text) = TableError(e?.GetValue<string>() ?? "", name);
            throw new BlockError(ctx.Block, field, text);
        }
        foreach (var b in r["blocks"] as JsonArray ?? [])
        {
            if ((string?)b?["name"] != name) continue;
            var status = (string?)b?["status"];
            var id = (int?)b?["id"] ?? -1;
            low.Json["status"] = status;
            if (id >= 0) low.Json["cellId"] = id;
            switch (status)
            {
                case "registered":
                    return (TableBlockStatus.Registered, id);
                case "pending":
                    low.AfterCommit(() =>
                    {
                        var text = $"{name} is new to this game: the game read its material tables at start, so it's kept for the next start and does nothing until then";
                        lock (mod.Warnings)
                            if (!mod.Warnings.Contains(text)) mod.Warnings.Add(text);
                        mod.ReportDirty = true;
                        BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {text}");
                    });
                    return (TableBlockStatus.Pending, id);
                default:
                    low.AfterCommit(() => BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {name}: kiln.worldtables is off on this build: nothing changes in the game's tables"));
                    return (TableBlockStatus.Off, -1);
            }
        }
        throw new BlockError(ctx.Block, null, "kiln.worldtables did not take it and did not say why");
    }

    // "code: block materialTweak x, strength.hardness: is not ..." -> the field and what is wrong
    private static (string? Field, string Text) TableError(string message, string name)
    {
        var at = message.IndexOf(" " + name, StringComparison.Ordinal);
        if (at < 0) return (null, message);
        var rest = message[(at + 1 + name.Length)..];
        if (rest.StartsWith(", ", StringComparison.Ordinal))
        {
            var colon = rest.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0) return (rest[2..colon], rest[(colon + 2)..]);
        }
        return (null, rest.TrimStart(':', ' '));
    }

    private static BlockHandle DefineMaterialTweak(BlockContext ctx, Lowered low, string name, MaterialTweakBlock t)
    {
        if (string.IsNullOrWhiteSpace(t.Material)) throw new BlockError(ctx.Block, "material", "which game material it changes: a GameMaterials name");
        var strength = StrengthNode(ctx, t.Strength);
        var surface = SurfaceNode(ctx, t.Surface);
        if ((strength == null || strength.Count == 0) && surface == null)
            throw new BlockError(ctx.Block, "strength", "it changes nothing: say what it takes to dig the material, or give it a surface");
        var block = new JsonObject { ["material"] = t.Material };
        if (strength is { Count: > 0 }) block["strength"] = strength.DeepClone();
        if (surface != null) block["surface"] = surface.DeepClone();
        var (status, id) = AskTables(ctx, low, "materialTweaks", name, block);
        low.Json["material"] = t.Material;
        if (strength is { Count: > 0 }) low.Json["strength"] = strength;
        if (surface != null) low.Json["surface"] = surface;
        return new TableBlockHandle<MaterialTweakBlock>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "materialtweak", name), name, t, status, id);
    }

    private static BlockHandle DefineBuildingMaterial(BlockContext ctx, Lowered low, string name, BuildingMaterialBlock m)
    {
        ctx.Need(ClientsLevel.Mod, null, "a new building material has to be on every player's game: cells reach them as ids");
        RequireName(ctx, m.Name, "a building material");
        var key = m.Was ?? name;
        var mod = ctx.Mod.Id;
        var guid = BlockIds.Derive(mod, "material", key);
        var block = new JsonObject { ["name"] = m.Name, ["roof"] = m.Roof };
        if (m.Was != null) block["was"] = m.Was;
        if (StrengthNode(ctx, m.Strength) is { } strength) block["strength"] = strength;
        var surface = SurfaceNode(ctx, m.Surface);
        if (surface != null) block["surface"] = surface.DeepClone();
        if (m.Icon != null) block["icon"] = m.Icon;   // a client draws a block of its surface for an item without one
        // its main input is what digging it gives back, unless its strength says
        if (m.Recipe is { Needs.Count: > 0 } r)
            block["recipe"] = new JsonObject { ["makes"] = r.Makes, ["needs"] = new JsonObject(r.Needs.Select(n => KeyValuePair.Create(n.Key.Value, (JsonNode?)n.Value))) };
        var (status, id) = AskTables(ctx, low, "buildingMaterials", name, block);
        low.Json["material"] = guid.ToString();
        if (surface != null) low.Json["surface"] = surface;
        var game = $"{mod}.{name}";
        if (status == TableBlockStatus.Registered && id >= 0)
        {
            var d = new Draft(ctx, low, key, name, m.Roof ? RoofStarter : BlockStarter);
            d.Fields["equipment.voxelData.placeVoxelMaterialId"] = id;
            d.Refs["equipment.voxelData.placeVoxelMaterial"] = guid.ToString();
            if (m.Icon != null) d.Icon = m.Icon;
            d.Commit(m.Name, m.Description);
            if (m.Recipe is { } recipe) DefineRecipe(ctx, low, key, game, recipe);
        }
        return new TableBlockHandle<BuildingMaterialBlock>(mod, name, guid, game, m, status, id);
    }
}

internal static class WorldJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("materialTweaks", BlockJson.Make<MaterialTweakBlock>), ("buildingMaterials", BlockJson.Make<BuildingMaterialBlock>), ("strengths", BlockJson.Make<StrengthBlock>),
        ("surfaces", BlockJson.Make<SurfaceBlock>),
    ];
}
