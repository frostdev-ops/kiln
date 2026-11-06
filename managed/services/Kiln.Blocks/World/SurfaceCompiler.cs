// SPDX-License-Identifier: MIT
// SurfaceBlock -> the "surface" of a material block, as kiln.worldtables and kiln.surfaces read it. The block serialises to the shape a data file has; the
// table merge checks it on every side and kiln.surfaces cooks it on a client at boot, so a field the cooker refuses comes back here by name.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiln;

internal static partial class Compiler
{
    private static readonly JsonSerializerOptions SurfaceWrite = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The surface as a data file writes it out; null when the block has none. One defined by name is written out here, so the table merge reads the same either way.</summary>
    internal static JsonObject? SurfaceNode(BlockContext ctx, PartRef<SurfaceBlock>? part)
    {
        if (part == null) return null;
        var s = ctx.Part(part, "surface");
        if (JsonSerializer.SerializeToNode(s, SurfaceWrite) is not JsonObject node) throw new BlockError(ctx.Block, "surface", "doesn't serialise");
        return node;
    }

    /// <summary>A surface defined once, for materials to name. What's wrong with it is said by the material that uses it, where the table merge checks it.</summary>
    private static BlockHandle DefineSurface(BlockContext ctx, Lowered low, string name, SurfaceBlock s)
    {
        low.Json["part"] = name;
        if (JsonSerializer.SerializeToNode(s, SurfaceWrite) is JsonObject node) low.Json["surface"] = node;
        return new BlockHandle<SurfaceBlock>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "part", name), name, s);
    }
}
