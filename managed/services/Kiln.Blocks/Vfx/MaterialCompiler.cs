// SPDX-License-Identifier: MIT
// MaterialBlock -> kiln.vfx's builder, by the road VfxBlock takes. The block serialises to the JSON a data file has, and the builder is the one that runs at boot. The game lists every
// material's textures once at start, and a texture it didn't list is fatal, so a definition from code it has not seen is kept for the next start. A dedicated server has no builder and
// no materials: the block still has its id, which is all a model's data needs there.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    /// <summary>The room for new materials when the game's own count isn't known: the 4,096 it can hold less the 3,403 it has. The builder reads the real one.</summary>
    internal const int MaterialRoom = 4096 - 3403;

    internal static JsonObject MaterialDefinition(MaterialBlock m) => (JsonObject)JsonSerializer.SerializeToNode(m, VfxWrite)!;

    private static BlockHandle DefineMaterial(BlockContext ctx, Lowered low, string name, MaterialBlock m)
    {
        ctx.Need(ClientsLevel.Mod, null, "a new material has to be on every player's game: the server does not send assets");
        CheckMaterial(ctx, m);
        var mod = ctx.Mod.Id;
        if (m.Replaces != null && !Guid.TryParse(m.Replaces, out _)) throw new BlockError(ctx.Block, "replaces", $"\"{m.Replaces}\" is not the GUID of one of the game's materials");
        if (m.Replaces != null && m.Was != null) throw new BlockError(ctx.Block, "replaces", "a material that replaces one of the game's has that one's id: \"was\" has no meaning here");
        var guid = m.Replaces != null ? Guid.Parse(m.Replaces) : BlockIds.Derive(mod, "material", m.Was ?? name);
        var definition = MaterialDefinition(m);
        low.Json["material"] = guid.ToString();
        low.Json["definition"] = definition.DeepClone();

        var handle = new MaterialBlockHandle(mod, name, guid, m, VfxBlockStatus.NotHere);
        // from a data file: the bytes the boot build read, so an unchanged block is known to be the same one
        ctx.Mod.VfxRaw.Remove(name, out var raw);
        handle.Status = BuildMaterial(ctx, low, name, raw ?? definition.ToJsonString(), guid);
        return handle;
    }

    private static VfxBlockStatus BuildMaterial(BlockContext ctx, Lowered low, string name, string blockJson, Guid guid)
    {
        var mod = ctx.Mod;
        var status = VfxNative.BuildStatusJson();
        if (status == null)
        {
            // Nothing is built on this side. What would overflow a client's game is still the author's to hear.
            var count = Blocks.AllMods.Sum(m => m.Handles.Values.Count(h => h is MaterialBlockHandle && h.Name != name));
            if (count >= MaterialRoom)
                throw new BlockError(ctx.Block, null, $"over the game's limit: it has room for {MaterialRoom} new materials across all mods, and {count} are defined already. A client refuses a whole mod that does not fit");
            return VfxBlockStatus.NotHere;
        }
        foreach (var refused in VfxRefusals(status, mod.Id)) throw new BlockError(ctx.Block, null, refused);

        var document = $"{{\"format\":1,\"mod\":{JsonSerializer.Serialize(mod.Id)},\"dir\":{JsonSerializer.Serialize(BlocksBridge.ModDirectory(mod.Id))},\"materials\":{{{JsonSerializer.Serialize(name)}:{blockJson}}}}}";
        var report = VfxNative.DefineJson(document, mod.Owner) ?? "";
        var (result, problem, warnings) = ReadMaterialReport(report, name, guid);
        foreach (var w in warnings) low.AfterCommit(() => BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: material {name}: {w}"));
        if (problem != null) throw new BlockError(ctx.Block, problem.Value.Field, problem.Value.Text);
        low.Json["status"] = result.ToString().ToLowerInvariant();
        if (result == VfxBlockStatus.Pending)
            low.AfterCommit(() =>
            {
                var text = $"material {name} is new to this game: it is kept for the next start, and a model that names it doesn't draw until then";
                lock (mod.Warnings)
                    if (!mod.Warnings.Contains(text)) mod.Warnings.Add(text);
                mod.ReportDirty = true;
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {text}");
                Hud.Toast($"{mod.Id}: new material \"{name}\": restart the game to see it", ToastKind.Warning, 8);
            });
        return result;
    }

    /// <summary>The builder's report on one material: its status, or what is wrong by field.</summary>
    internal static (VfxBlockStatus Status, (string? Field, string Text)? Problem, List<string> Warnings) ReadMaterialReport(string report, string name, Guid guid)
    {
        var warnings = new List<string>();
        JsonObject? doc;
        try { doc = JsonNode.Parse(report) as JsonObject; }
        catch (JsonException) { doc = null; }
        if (doc == null) return (VfxBlockStatus.NotHere, (null, $"kiln.vfx gave no report: {report}"), warnings);
        foreach (var w in doc["warnings"] as JsonArray ?? []) warnings.Add(MaterialField(w?.GetValue<string>() ?? "", name).Text);
        foreach (var e in doc["errors"] as JsonArray ?? [])
        {
            var (field, text) = MaterialField(e?.GetValue<string>() ?? "", name);
            return (VfxBlockStatus.NotHere, (field, text), warnings);
        }
        foreach (var r in doc["refusedMods"] as JsonArray ?? [])
            return (VfxBlockStatus.NotHere, (null, r?.GetValue<string>() ?? "refused"), warnings);
        foreach (var b in doc["materials"] as JsonArray ?? [])
        {
            if ((string?)b?["name"] != name) continue;
            if (Guid.TryParse((string?)b?["guid"], out var built) && built != guid)
                return (VfxBlockStatus.NotHere, (null, $"kiln.vfx built it as {built}, and Kiln.Blocks has {guid}: this is a bug"), warnings);
            return ((string?)b?["status"] == "registered" ? VfxBlockStatus.Registered : VfxBlockStatus.Pending, null, warnings);
        }
        return (VfxBlockStatus.NotHere, (null, "kiln.vfx did not build it and did not say why"), warnings);
    }

    // "mod: code: materials.steel.roughness: 2 is more than 1" -> the field and what is wrong.
    private static (string? Field, string Text) MaterialField(string message, string name)
    {
        var key = $"materials.{name}";
        var at = message.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return (null, message);
        var rest = message[(at + key.Length)..].TrimStart('.');
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return (null, rest.TrimStart(':', ' '));
        return colon == 0 ? (null, rest[2..]) : (rest[..colon], rest[(colon + 2)..]);
    }

    // The builder's rules, for the side that has no builder: a mistake should not wait for someone to start a client. The words are the builder's.
    private static void CheckMaterial(BlockContext ctx, MaterialBlock m)
    {
        void Fail(string field, string text) => throw new BlockError(ctx.Block, field, text);
        void Unit(string field, double? x)
        {
            if (x is { } n && (n < 0 || n > 1)) Fail(field, $"{n} is outside 0..1");
        }
        void Colour(string field, string? c)
        {
            var t = c?.TrimStart('#');
            if (c != null && (t!.Length is not (3 or 6) || !t.All(char.IsAsciiHexDigit))) Fail(field, $"\"{c}\" is not a colour: #rrggbb");
        }
        void Png(string field, string? file)
        {
            if (file == null) return;
            if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) Fail(field, $"\"{file}\" is not a PNG in your mod");
            if (file.StartsWith('/') || file.Contains("..", StringComparison.Ordinal) || file.Contains(':')) Fail(field, $"\"{file}\" must be relative, inside the mod's folder");
        }
        Png("albedo", m.Albedo);
        Colour("tint", m.Tint);
        Png("normal", m.Normal?.Map);
        if (m.Normal?.Strength is < 0 or > 16) Fail("normal.strength", $"{m.Normal.Strength} is outside 0..16");
        Png("params", m.Params);
        Png("metallicRoughness", m.MetallicRoughness);
        Png("occlusion", m.Occlusion);
        if (m.Params != null && (m.MetallicRoughness != null || m.Occlusion != null)) Fail("params", "holds metallic, roughness and occlusion already: give params, or metallicRoughness and occlusion");
        Unit("roughness", m.Roughness);
        Unit("metallic", m.Metallic);
        if (m.Reflectance is { Value: { } r }) Unit("reflectance", r);
        else if (m.Reflectance != null && m.Params == null) Fail("reflectance", "\"params\" reads the alpha of a params map, and this material has none");
        if (m.Emissive is { } e)
        {
            Png("emissive.map", e.Map);
            Colour("emissive.color", e.Color);
            if (e.Strength is < 0) Fail("emissive.strength", $"{e.Strength} is less than 0");
            Unit("emissive.exposure", e.Exposure);
        }
        if (m.AlphaTest is { Cutoff: { } c } && (c <= 0 || c > 1)) Fail("alphaTest", $"{c} is outside 0..1, above 0");
        Unit("translucent", m.Translucent);
        if (m.Dissolve is { } d)
        {
            Colour("dissolve.color", d.Color);
            if (d.Width is <= 0 or > 1) Fail("dissolve.width", $"{d.Width} is outside 0..1, above 0");
            if (d.NoiseTiling is < 0) Fail("dissolve.noiseTiling", $"{d.NoiseTiling} is less than 0");
            Unit("dissolve.noiseBlend", d.NoiseBlend);
        }
        if (m.UvScale is { } uv && (uv.U < 0 || uv.V < 0)) Fail("uvScale", "must be 0 or more");
        if (m.Shader != null) Fail("shader", "surface shaders aren't built yet: a material draws with the game's own shaders. Take this out for now");
    }
}
