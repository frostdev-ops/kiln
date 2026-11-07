// SPDX-License-Identifier: MIT
// VfxBlock -> kiln.vfx's builder. The block serialises to the same JSON a data file has, and the builder is the one that runs at boot. The game lists VFX
// once at start, so a definition from code it has not seen is kept for the next start (status "pending"); from a data file it was already read at boot.
// A dedicated server has no builder and no VFX: the block still has its id and its length, which is all the data that names it needs there.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiln;

internal static partial class Compiler
{
    /// <summary>The room for new VFX when the game's own count isn't known: its 2,048 less the 1,575 it has. The builder reads the real one.</summary>
    internal const int VfxRoom = 2048 - 1575;

    private static readonly JsonSerializerOptions VfxWrite = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new Vec3Converter() },
    };

    /// <summary>The block as the builder takes it, the same whichever way it was written.</summary>
    internal static JsonObject VfxDefinition(VfxBlock v) => (JsonObject)JsonSerializer.SerializeToNode(v, VfxWrite)!;

    private static BlockHandle DefineVfx(BlockContext ctx, Lowered low, string name, VfxBlock v)
    {
        ctx.Need(ClientsLevel.Mod, null, "a new VFX has to be on every player's game: the server does not send assets");
        CheckVfx(ctx, v);
        var mod = ctx.Mod.Id;
        var guid = BlockIds.Derive(mod, "vfx", v.Was ?? name);
        var definition = VfxDefinition(v);
        var (seconds, loops) = VfxTiming.Of(v);
        low.Json["vfx"] = guid.ToString();
        low.Json["definition"] = definition.DeepClone();
        low.Json["loops"] = loops;
        if (!loops) low.Json["seconds"] = Math.Round(seconds, 3);

        var handle = new VfxBlockHandle(mod, name, guid, v, VfxBlockStatus.NotHere);
        // from a data file: the bytes the boot build read, so an unchanged block is known to be the same one
        ctx.Mod.VfxRaw.Remove(name, out var raw);
        handle.Status = Build(ctx, low, name, raw ?? definition.ToJsonString(), guid);
        return handle;
    }

    private static VfxBlockStatus Build(BlockContext ctx, Lowered low, string name, string blockJson, Guid guid)
    {
        var mod = ctx.Mod;
        var status = VfxNative.BuildStatusJson();
        if (status == null)
        {
            // Nothing is built on this side. What would overflow a client's game is still the author's to hear.
            var count = Blocks.AllMods.Sum(m => m.Handles.Values.Count(h => h is BlockHandle<VfxBlock> && h.Name != name));
            if (count >= VfxRoom)
                throw new BlockError(ctx.Block, null, $"over the game's limit: it has room for {VfxRoom} new VFX across all mods, and {count} are defined already. A client refuses a whole mod that does not fit");
            return VfxBlockStatus.NotHere;
        }
        foreach (var refused in VfxRefusals(status, mod.Id)) throw new BlockError(ctx.Block, null, refused);

        var document = $"{{\"format\":1,\"mod\":{JsonSerializer.Serialize(mod.Id)},\"dir\":{JsonSerializer.Serialize(BlocksBridge.ModDirectory(mod.Id))},\"vfx\":{{{JsonSerializer.Serialize(name)}:{blockJson}}}}}";
        var report = VfxNative.DefineJson(document, mod.Owner) ?? "";
        var (result, problem, warnings) = ReadVfxReport(report, name, guid);
        foreach (var w in warnings) low.AfterCommit(() => BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: vfx {name}: {w}"));
        if (problem != null) throw new BlockError(ctx.Block, problem.Value.Field, problem.Value.Text);
        low.Json["status"] = result.ToString().ToLowerInvariant();
        if (result == VfxBlockStatus.Pending)
            low.AfterCommit(() =>
            {
                var text = $"vfx {name} is new to this game: it is kept for the next start, and shows nothing until then";
                lock (mod.Warnings)
                    if (!mod.Warnings.Contains(text)) mod.Warnings.Add(text);
                mod.ReportDirty = true;
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {text}");
                Hud.Toast($"{mod.Id}: new VFX \"{name}\": restart the game to see it", ToastKind.Warning, 8);
            });
        return result;
    }

    /// <summary>What the start's build refused of a mod, which reads the same to an author whichever block they look at: over a limit, or a manifest that does not fit.</summary>
    internal static IEnumerable<string> VfxRefusals(string statusJson, string mod)
    {
        if (JsonNode.Parse(statusJson)?["refusedMods"] is not JsonArray refused) yield break;
        foreach (var x in refused)
            if (x?.GetValue<string>() is { } text && text.StartsWith(mod + ":", StringComparison.Ordinal)) yield return text;
    }

    /// <summary>The builder's report on one block (docs/kiln/services/vfx-cook.md): its status, or what is wrong by field.</summary>
    internal static (VfxBlockStatus Status, (string? Field, string Text)? Problem, List<string> Warnings) ReadVfxReport(string report, string name, Guid guid)
    {
        var warnings = new List<string>();
        JsonObject? doc;
        try { doc = JsonNode.Parse(report) as JsonObject; }
        catch (JsonException) { doc = null; }
        if (doc == null) return (VfxBlockStatus.NotHere, (null, $"kiln.vfx gave no report: {report}"), warnings);
        foreach (var w in doc["warnings"] as JsonArray ?? []) warnings.Add(Field(w?.GetValue<string>() ?? "", name).Text);
        foreach (var e in doc["errors"] as JsonArray ?? [])
        {
            var (field, text) = Field(e?.GetValue<string>() ?? "", name);
            return (VfxBlockStatus.NotHere, (field, text), warnings);
        }
        foreach (var r in doc["refusedMods"] as JsonArray ?? [])
            return (VfxBlockStatus.NotHere, (null, r?.GetValue<string>() ?? "refused"), warnings);
        foreach (var b in doc["blocks"] as JsonArray ?? [])
        {
            if ((string?)b?["name"] != name) continue;
            if (Guid.TryParse((string?)b?["guid"], out var built) && built != guid)
                return (VfxBlockStatus.NotHere, (null, $"kiln.vfx built it as {built}, and Kiln.Blocks has {guid}: this is a bug"), warnings);
            return ((string?)b?["status"] == "registered" ? VfxBlockStatus.Registered : VfxBlockStatus.Pending, null, warnings);
        }
        return (VfxBlockStatus.NotHere, (null, "kiln.vfx did not build it and did not say why"), warnings);
    }

    // "frost-fx: code: vfx.frost_burst.particles[0].maxCount: 4097 is outside 1..4096" -> the field and what is wrong.
    private static (string? Field, string Text) Field(string message, string name)
    {
        var at = message.IndexOf($"vfx.{name}", StringComparison.Ordinal);
        if (at < 0) return (null, message);
        var rest = message[(at + 4 + name.Length)..].TrimStart('.');
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return (null, rest.TrimStart(':', ' '));
        return colon == 0 ? (null, rest[2..]) : (rest[..colon], rest[(colon + 2)..]);
    }

    // The builder's rules, for the side that has no builder: a mistake should not wait for someone to start a client. The words are the builder's.
    private static void CheckVfx(BlockContext ctx, VfxBlock v)
    {
        void Fail(string field, string text) => throw new BlockError(ctx.Block, field, text);
        void Between(string field, double? x, double lo, double hi)
        {
            if (x is { } n && (!double.IsFinite(n) || n < lo || n > hi)) Fail(field, $"{n} is outside {lo}..{hi}");
        }
        void Not(string field, double? x, double lo)
        {
            if (x is { } n && (!double.IsFinite(n) || n < lo)) Fail(field, $"{n} is less than {lo}");
        }
        void Colour(string field, string? c)
        {
            var t = c?.TrimStart('#');
            if (c != null && (t!.Length is not (3 or 6) || !t.All(char.IsAsciiHexDigit))) Fail(field, $"\"{c}\" is not a colour: #rrggbb");
        }
        var particles = v.Particles ?? [];
        if (particles.Count == 0 && v.Light == null) Fail("particles", "nothing to draw: give it particles or a light");
        if (particles.Count * 2 + (v.Light != null ? 1 : 0) > 55) Fail("particles", $"too many: {particles.Count * 2 + (v.Light != null ? 1 : 0)} particle entries (two for each block, one for a light), at most 55");
        if (v.CullDistance is { } cull && (cull.Min <= 0 || cull.Max <= 0 || cull.Min > cull.Max)) Fail("cullDistance", "must be above 0, near before far");
        for (var i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            var at = $"particles[{i}]";
            if ((p.Burst ?? 0) == 0 && (p.Rate ?? 0) <= 0) Fail(at, "spawns nothing: give it a burst or a rate");
            Between($"{at}.burst", p.Burst, 0, 65535);
            Not($"{at}.rate", p.Rate, 0);
            Not($"{at}.duration", p.Duration, 0);
            Not($"{at}.delay", p.Delay, 0);
            if (p.Loop == true && (p.Duration ?? 0) <= 0) Fail($"{at}.loop", "a loop needs a duration above 0");
            Between($"{at}.maxCount", p.MaxCount, 1, 4096);
            if (p.Lifetime is { } life && life.Min <= 0) Fail($"{at}.lifetime", "must be above 0");
            if (p.Spread is { Word: null } cone) Between($"{at}.spread.cone", cone.Degrees, 0, 180);
            Not($"{at}.spawnRadius", p.SpawnRadius, 0);
            if (p.Shape != null && p.SpawnRadius != null) Fail($"{at}.shape", "say either shape or spawnRadius (a sphere): not both");
            if (p.Shape is { } shape)
            {
                if (!float.IsFinite(shape.A) || !float.IsFinite(shape.Size.X) || !float.IsFinite(shape.Size.Y) || !float.IsFinite(shape.Size.Z) || shape.A < 0 || shape.A > 1e6f || shape.Size.X < 0 || shape.Size.X > 1e6f || shape.Size.Y < 0 || shape.Size.Y > 1e6f || shape.Size.Z < 0 || shape.Size.Z > 1e6f) Fail($"{at}.shape", "sizes must be finite and within 0..1000000");
                if (shape.Inner is { } inner && (!float.IsFinite(inner) || inner < 0 || inner > shape.A)) Fail($"{at}.shape.inner", "must be between 0 and the radius");
            }
            if (p.Stretch != null && p.Align != ParticleAlign.Velocity) Fail($"{at}.stretch", "stretches along the velocity: it needs \"align\": \"velocity\"");
            Between($"{at}.stretch", p.Stretch, 0, 100);
            if (p.Align == ParticleAlign.Velocity && (p.FaceCamera == false || (p.Mesh == ParticleMesh.QuadXz && p.FaceCamera != true)))
                Fail($"{at}.align", "\"velocity\" turns a camera-facing quad: it needs faceCamera (a \"quad\" mesh faces the camera unless it says otherwise)");
            if (p.Shader is { } shader)
            {
                if (!shader.File.EndsWith(".spv", StringComparison.OrdinalIgnoreCase)) Fail($"{at}.shader", $"\"{shader.File}\" is not a compiled .spv in your mod (build it with kiln-vfxcook shader build)");
                if (shader.File.StartsWith('/') || shader.File.Contains("..", StringComparison.Ordinal) || shader.File.Contains(':')) Fail($"{at}.shader", $"\"{shader.File}\" must be relative, inside the mod's folder");
                if (shader.Constants != null && shader.Constants.Any(x => !float.IsFinite(x) || MathF.Abs(x) > 1e6f)) Fail($"{at}.shader.constants", "numbers must be finite and within -1000000..1000000");
                if (shader.Constants is { Count: > 8 }) Fail($"{at}.shader.constants", "up to 8 numbers");
            }
            Not($"{at}.drag", p.Drag, 0);
            Not($"{at}.intensity", p.Intensity, 0);
            Not($"{at}.depthFade", p.DepthFade, 0);
            Between($"{at}.emissive", p.Emissive, 0, 1);
            if (p.Texture is { } tex && !tex.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) Fail($"{at}.texture", $"\"{tex.File}\" is not a PNG in your mod");
            if (p.Texture is { } rel && (rel.File.StartsWith('/') || rel.File.Contains("..", StringComparison.Ordinal) || rel.File.Contains(':'))) Fail($"{at}.texture", $"\"{rel.File}\" must be relative, inside the mod's folder");
            if (p.Flipbook is { } flip)
            {
                Between($"{at}.flipbook.columns", flip.Columns, 1, 1024);
                Between($"{at}.flipbook.rows", flip.Rows, 1, 1024);
            }
            if (p.Color is { One: { } one }) Colour($"{at}.color", one);
            foreach (var c in p.Color?.List ?? []) Colour($"{at}.color", c);
        }
        if (v.Light is { } light)
        {
            Colour("light.color", light.Color);
            if (light.Radius is <= 0) Fail("light.radius", $"{light.Radius} must be more than 0");
            Not("light.brightness", light.Brightness, 0);
            Not("light.lifetime", light.Lifetime, 0);
            Between("light.flicker", light.Flicker, 0, 1);
        }
    }
}
