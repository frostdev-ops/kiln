// SPDX-License-Identifier: MIT
// MaterialBlock, player-free. A dedicated server has no materials, so what this proves there is the block: its shape in both forms, its ids, its mistakes, what it asks of a mod.
// With kiln/config/vfx.json {"sim": true} the builder runs at boot on this side too: then what it served is read back through kiln.assets, as the game would.
using System.Text.Json.Nodes;
using Kiln;

namespace MaterialTest;

public sealed class MaterialTestMod : Mod
{
    private const string Id = "material-test";
    private const string MaterialType = "keen::RenderMaterialResource";
    private int _pass, _fail;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"material-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Says(string what, Action act, params string[] words)
    {
        try
        {
            act();
            Check(false, $"{what}: nothing was said");
        }
        catch (BlockError e)
        {
            Check(words.All(w => e.Message.Contains(w, StringComparison.Ordinal)), $"{what}: {e.Message}");
        }
    }

    private static string Definition(string name) => JsonNode.Parse(Blocks.Lowered(name) ?? "{}")?["definition"]?.ToJsonString() ?? "(no definition)";

    // Data files are compiled after OnLoad, so the checks of both forms wait for content to be ready.
    public override void OnLoad() => Content.OnReady(_ =>
    {
        Twins();
        Mistakes();
        Handles();
        Served();
        Log.Info($"material-test: {_pass} PASS, {_fail} FAIL");
    });

    private void Twins()
    {
        var blocks = new (string Name, MaterialBlock Block)[]
        {
            ("steel", new MaterialBlock
            {
                Kind = MaterialKind.Metal,
                Albedo = "tex/a.png",
                Normal = new NormalMap("tex/n.png", 2, true),
                MetallicRoughness = "tex/a.png",
                Occlusion = "tex/n.png",
                Roughness = 0.3f,
                Tint = "#dff2ff",
                Emissive = new EmissiveBlock { Map = "tex/n.png", Color = "#7fd4ff", Strength = 8, Exposure = 0.5f },
            }),
            ("leaf", new MaterialBlock { Kind = MaterialKind.Foliage, Albedo = "tex/a.png", AlphaTest = 0.25f, Translucent = 0.5f, UvScale = (2f, 1f) }),
            ("plain", new MaterialBlock
            {
                Tint = "#336699", DoubleSided = true, Shadows = false, ContactShadows = false, Frost = false, Snow = false, Wetness = false, VertexColor = true, Reflectance = 0.25f, UvScale = 2f,
                Dissolve = new DissolveBlock { Color = "#ff0000", Width = 0.1f, NoiseTiling = 2, NoiseBlend = 0.25f },
            }),
            ("glow", new MaterialBlock { Kind = MaterialKind.Emissive, Emissive = "#7fd4ff" }),
            ("melt", new MaterialBlock { Kind = MaterialKind.Dithered, Dissolve = true }),
            ("fromparams", new MaterialBlock { Params = "tex/a.png", Reflectance = MaterialReflectance.FromParams }),
        };
        foreach (var (name, block) in blocks)
        {
            var h = Blocks.Define(name, block);
            Check(h.Guid == BlockIds.Derive(Id, "material", name), $"{name}: the id is the block rule's: {h.Guid}");
            Check(Definition(name) == Definition(name + "-json"), $"{name}: C# and JSON give the same definition: {Definition(name)}");
        }
        var renamed = Blocks.Define("new-name", new MaterialBlock { Was = "old-name" });
        Check(renamed.Guid == BlockIds.Derive(Id, "material", "old-name"), "Was keeps the id");
        Check(Blocks.Find("steel-json") is MaterialBlockHandle, "a block from a data file has the same handle");
    }

    private void Mistakes()
    {
        Says("a roughness over 1", () => Blocks.Define("m1", new MaterialBlock { Roughness = 2 }), "material m1", "roughness", "outside 0..1");
        Says("a colour", () => Blocks.Define("m2", new MaterialBlock { Tint = "blue" }), "tint", "#rrggbb");
        Says("not a PNG", () => Blocks.Define("m3", new MaterialBlock { Albedo = "a.jpg" }), "albedo", "PNG");
        Says("outside the mod", () => Blocks.Define("m4", new MaterialBlock { Albedo = "../a.png" }), "albedo", "inside the mod");
        Says("params and glTF's channels", () => Blocks.Define("m5", new MaterialBlock { Params = "a.png", Occlusion = "b.png" }), "params", "metallicRoughness and occlusion");
        Says("reflectance from no map", () => Blocks.Define("m6", new MaterialBlock { Reflectance = MaterialReflectance.FromParams }), "reflectance", "has none");
        Says("a cutoff of 0", () => Blocks.Define("m7", new MaterialBlock { AlphaTest = 0f }), "alphaTest");
        Says("an edge of no width", () => Blocks.Define("m8", new MaterialBlock { Dissolve = new DissolveBlock { Width = 0 } }), "dissolve.width");
        Says("a surface shader", () => Blocks.Define("m9", new MaterialBlock { Shader = "mine" }), "shader", "aren't built yet");
    }

    private void Handles()
    {
        var h = Blocks.Define("for-a-particle", new MaterialBlock { Tint = "#ff0000" });
        var v = Blocks.Define("sparks", new VfxBlock { Particles = [new ParticleBlock { Burst = 4, Material = "steel-json" }] });
        Check(JsonNode.Parse(Blocks.Lowered("sparks") ?? "{}")?["definition"]?["particles"]?[0]?["material"]?.GetValue<string>() == "steel-json", "a particle's material by name reaches the builder");
        var byHandle = new ParticleBlock { Burst = 1, Material = h };
        Check(byHandle.Material?.Name == "for-a-particle", "a particle takes a material's handle");
        Check(v.Guid == BlockIds.Derive(Id, "vfx", "sparks"), "a VFX that names a material has the usual id");
        // the JSON twin came through the boot build on a side that has one
        Check(Blocks.Find("sparks-json") is VfxBlockHandle, "a data file's particle names a material");
    }

    private void Served()
    {
        var names = new[] { "steel-json", "leaf-json", "plain-json", "glow-json", "melt-json", "fromparams-json" };
        var handles = names.Select(n => (Name: n, Handle: Blocks.Find(n) as MaterialBlockHandle)).ToList();
        if (handles.All(x => x.Handle?.Status == VfxBlockStatus.NotHere))
        {
            Log.Info("material-test: INFO no builder on this side: what is served needs kiln/config/vfx.json {\"sim\": true}");
            return;
        }
        Check(handles.All(x => x.Handle?.Status == VfxBlockStatus.Registered), $"blocks from a data file are registered: {string.Join(", ", handles.Select(x => $"{x.Name} {x.Handle?.Status}"))}");
        // first start: kept for the next one. A start after that: the boot build had it.
        var fromCode = (Blocks.Find("steel") as MaterialBlockHandle)?.Status;
        Check(fromCode is VfxBlockStatus.Pending or VfxBlockStatus.Registered, $"a block from code is kept for the next start, or was there from this one: {fromCode}");
        Log.Info($"material-test: INFO a block from code is {fromCode}");
        if (!Assets.Available)
        {
            Check(false, "kiln.assets is here");
            return;
        }
        var listed = Assets.ListResources(new AssetKey(Guid.Empty, MaterialType).Type);
        foreach (var (name, handle) in handles)
        {
            var key = new AssetKey(handle!.Guid, MaterialType);
            Check(listed.Contains(key), $"{name}: the resource service lists it, as the renderer will at boot");
            var bytes = Assets.ReadResource(key);
            Check(bytes is { Length: > 40 } && bytes[0] == 1, $"{name}: served, {bytes?.Length} bytes, effect Pbr");
            if (bytes != null)
            {
                var dump = Assets.Dump($"{handle.Guid}:{MaterialType}");
                Log.Info($"material-test: INFO {name} {handle.Guid} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()} dumped to {dump}");
            }
        }
    }
}
