// SPDX-License-Identifier: MIT
// The box around a glTF's meshes, in the game's axes, without the model cook: a prop's collision follows from its model on a server too, which cooks nothing.
// glTF requires min and max on every POSITION accessor, so this reads only the JSON: each mesh's accessor box, its eight corners through the node's world
// transform. modelcook mirrors x into the game's left-handed axes, and so does this.
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class GltfBounds
{
    /// <summary>The box (min, max) around every mesh of the file's default scene, in metres in the game's axes. Null: no file, not glTF, or no positions.</summary>
    public static (Vector3 Min, Vector3 Max)? Of(string file)
    {
        JsonNode? doc;
        try { doc = Read(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }
        if (doc is not JsonObject root || root["nodes"] is not JsonArray nodes || root["meshes"] is not JsonArray meshes || root["accessors"] is not JsonArray accessors) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        void Visit(int n, Matrix4x4 parent, int depth)
        {
            if (depth > 64 || n < 0 || n >= nodes.Count || nodes[n] is not JsonObject node) return;
            var world = Local(node) * parent;
            if (node["mesh"] is JsonValue mv && mv.TryGetValue<int>(out var m) && m >= 0 && m < meshes.Count && meshes[m]?["primitives"] is JsonArray prims)
                foreach (var prim in prims)
                    if (prim?["attributes"]?["POSITION"] is JsonValue pv && pv.TryGetValue<int>(out var a) && a >= 0 && a < accessors.Count
                        && Vec(accessors[a]?["min"]) is { } lo && Vec(accessors[a]?["max"]) is { } hi)
                        for (var c = 0; c < 8; c++)
                        {
                            var corner = Vector3.Transform(new Vector3((c & 1) != 0 ? hi.X : lo.X, (c & 2) != 0 ? hi.Y : lo.Y, (c & 4) != 0 ? hi.Z : lo.Z), world);
                            min = Vector3.Min(min, corner);
                            max = Vector3.Max(max, corner);
                            any = true;
                        }
            if (node["children"] is JsonArray kids)
                foreach (var k in kids)
                    if (k is JsonValue kv && kv.TryGetValue<int>(out var ki)) Visit(ki, world, depth + 1);
        }
        var scene = root["scene"] is JsonValue sv && sv.TryGetValue<int>(out var si) ? si : 0;
        if (root["scenes"] is JsonArray scenes && scene < scenes.Count && scenes[scene]?["nodes"] is JsonArray top)
        {
            foreach (var t in top)
                if (t is JsonValue tv && tv.TryGetValue<int>(out var ti)) Visit(ti, Matrix4x4.Identity, 0);
        }
        else
            for (var i = 0; i < nodes.Count; i++) Visit(i, Matrix4x4.Identity, 0);
        if (!any) return null;
        // the game's x is the file's mirrored
        return (new Vector3(-max.X, min.Y, min.Z), new Vector3(-min.X, max.Y, max.Z));
    }

    private static JsonNode? Read(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length >= 20 && bytes[0] == 'g' && bytes[1] == 'l' && bytes[2] == 'T' && bytes[3] == 'F')
        {
            var length = BitConverter.ToInt32(bytes, 12);
            if (BitConverter.ToUInt32(bytes, 16) != 0x4E4F534A || length <= 0 || 20 + length > bytes.Length) throw new InvalidDataException("no JSON chunk");
            return JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, length));
        }
        return JsonNode.Parse(bytes);
    }

    private static Vector3? Vec(JsonNode? n) =>
        n is JsonArray { Count: >= 3 } a && a[0] is JsonValue x && a[1] is JsonValue y && a[2] is JsonValue z
            ? new Vector3(x.GetValue<float>(), y.GetValue<float>(), z.GetValue<float>()) : null;

    private static Matrix4x4 Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray { Count: 16 } m)
        {
            var f = m.Select(v => v!.GetValue<float>()).ToArray();
            // column-major in glTF; System.Numerics multiplies row vectors, so the same 16 numbers read row by row
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        var s = Vec(node["scale"]) ?? Vector3.One;
        var t = Vec(node["translation"]) ?? Vector3.Zero;
        var r = node["rotation"] is JsonArray { Count: 4 } q ? new Quaternion(q[0]!.GetValue<float>(), q[1]!.GetValue<float>(), q[2]!.GetValue<float>(), q[3]!.GetValue<float>()) : Quaternion.Identity;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }
}
