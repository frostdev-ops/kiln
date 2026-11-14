// SPDX-License-Identifier: MIT
// The JSON twin of TerrainPatch (docs/kiln/services/terrain.md, "What a patch is"). Strict: an unknown property or source
// kind is an error, so a typo never becomes a silently smaller patch.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class TerrainJson
{
    private static readonly JsonDocumentOptions Read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static TerrainPatch Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: Read) as JsonObject ?? throw new ArgumentException("a terrain patch is a JSON object", nameof(json));
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"terrain patch: not JSON ({e.Message})", nameof(json), e);
        }
        try
        {
            Only(root, "patch", "id", "version", "region", "palette", "source", "persistence", "$schema");
            var region = Obj(root, "region");
            Only(region, "region", "x", "y", "z", "sizeX", "sizeY", "sizeZ");
            var box = new CellBox(Int(region, "x"), Int(region, "y"), Int(region, "z"), Int(region, "sizeX"), Int(region, "sizeY"), Int(region, "sizeZ"));
            var palette = new List<TerrainPaletteEntry>();
            foreach (var n in Arr(root, "palette"))
            {
                var e = n as JsonObject ?? throw new ArgumentException("terrain patch: a palette entry is an object");
                Only(e, "palette entry", "material", "fill");
                palette.Add(new TerrainPaletteEntry(Byte(e, "material"), e.ContainsKey("fill") ? Byte(e, "fill") : (byte)255));
            }
            var src = Obj(root, "source");
            TerrainSource source;
            var kind = Str(src, "kind");
            switch (kind)
            {
                case "voxelVolume":
                    Only(src, "source", "kind", "cells");
                    byte[] cells;
                    try { cells = Convert.FromBase64String(Str(src, "cells")); }
                    catch (FormatException e) { throw new ArgumentException("terrain patch: source.cells is not base64", nameof(json), e); }
                    source = new TerrainVoxelVolume(cells);
                    break;
                case "heightfield":
                    Only(src, "source", "kind", "heights", "fill", "mode", "surface", "surfaceDepth");
                    var heights = Arr(src, "heights").Select(h => h is JsonValue v && v.TryGetValue<float>(out var f) ? f
                        : throw new ArgumentException("terrain patch: source.heights holds something that is not a number")).ToArray();
                    var mode = src.ContainsKey("mode") ? Str(src, "mode") switch
                    {
                        "replace" => TerrainHeightMode.Replace,
                        "addOnTop" => TerrainHeightMode.AddOnTop,
                        var m => throw new ArgumentException($"terrain patch: unknown heightfield mode '{m}' (replace, addOnTop)"),
                    } : TerrainHeightMode.Replace;
                    source = new TerrainHeightfield(heights, Byte(src, "fill"), mode, src.ContainsKey("surface") ? Byte(src, "surface") : (byte)0,
                        src.ContainsKey("surfaceDepth") ? Int(src, "surfaceDepth") : 0);
                    break;
                default:
                    throw new ArgumentException($"terrain patch: unknown source kind '{kind}' (voxelVolume, heightfield)");
            }
            var persistence = root.ContainsKey("persistence") ? Str(root, "persistence") switch
            {
                "kept" => TerrainPersistence.Kept,
                "transient" => TerrainPersistence.Transient,
                var p => throw new ArgumentException($"terrain patch: unknown persistence '{p}' (kept, transient)"),
            } : TerrainPersistence.Kept;
            return new TerrainPatch(Str(root, "id"), Int(root, "version"), box, source, palette, persistence);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
        {
            throw new ArgumentException($"terrain patch: {e.Message}", nameof(json), e);
        }
    }

    public static string Write(TerrainPatch p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var r = p.Region;
        var palette = new JsonArray();
        foreach (var e in p.Palette)
        {
            var o = new JsonObject { ["material"] = e.Material };
            if (e.Fill != 255) o["fill"] = e.Fill;
            palette.Add(o);
        }
        JsonObject source = p.Source switch
        {
            TerrainVoxelVolume v => new JsonObject { ["kind"] = "voxelVolume", ["cells"] = Convert.ToBase64String(v.Cells) },
            TerrainHeightfield h => new JsonObject
            {
                ["kind"] = "heightfield",
                ["heights"] = new JsonArray(h.Heights.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
                ["fill"] = h.Fill,
                ["mode"] = h.Mode == TerrainHeightMode.AddOnTop ? "addOnTop" : "replace",
                ["surface"] = h.Surface,
                ["surfaceDepth"] = h.SurfaceDepth,
            },
            _ => throw new ArgumentException("the patch has no source this version can write", nameof(p)),
        };
        var root = new JsonObject
        {
            ["id"] = p.Id,
            ["version"] = p.Version,
            ["region"] = new JsonObject { ["x"] = r.X, ["y"] = r.Y, ["z"] = r.Z, ["sizeX"] = r.SizeX, ["sizeY"] = r.SizeY, ["sizeZ"] = r.SizeZ },
            ["palette"] = palette,
            ["source"] = source,
            ["persistence"] = p.Persistence == TerrainPersistence.Transient ? "transient" : "kept",
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Only(JsonObject o, string what, params string[] known)
    {
        foreach (var (k, _) in o)
            if (Array.IndexOf(known, k) < 0) throw new ArgumentException($"terrain patch: unknown property '{k}' in the {what}");
    }

    private static JsonNode Need(JsonObject o, string key) =>
        o[key] ?? throw new ArgumentException($"terrain patch: '{key}' is missing");

    private static JsonObject Obj(JsonObject o, string key) =>
        Need(o, key) as JsonObject ?? throw new ArgumentException($"terrain patch: '{key}' is not an object");

    private static JsonArray Arr(JsonObject o, string key) =>
        Need(o, key) as JsonArray ?? throw new ArgumentException($"terrain patch: '{key}' is not a list");

    private static string Str(JsonObject o, string key) =>
        Need(o, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"terrain patch: '{key}' is not text");

    private static int Int(JsonObject o, string key) =>
        Need(o, key) is JsonValue v && v.TryGetValue<int>(out var i) ? i : throw new ArgumentException($"terrain patch: '{key}' is not a whole number");

    private static byte Byte(JsonObject o, string key) =>
        Int(o, key) is >= 0 and <= 255 and var i ? (byte)i : throw new ArgumentException($"terrain patch: '{key}' is outside 0 to 255");
}
