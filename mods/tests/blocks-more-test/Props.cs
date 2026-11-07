// SPDX-License-Identifier: MIT
// A prop with a model of the mod's own (a glTF that kiln cook makes into the game's model, found in blocks/*.json) and its own collision: the box around the
// model by default, or one said by hand, on what is placed, on the item's placement check and in its physics shape's hash. Drawn and bumped into: the manual test.
using System.Numerics;
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod
{
    private void DefineProps()
    {
        Blocks.Define("crate-lantern", new PropBlock { Name = "Crate Lantern", Visual = new VisualBlock { Model = "models/lantern.glb" }, Collider = new BoxCollider { Size = new Vector3(1, 0.5f, 1) },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } } });
        Blocks.Define("lantern-prop", new PropBlock { Name = "Lantern", Visual = new VisualBlock { Model = "models/lantern.glb" }, Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } } });
        Blocks.Define("hanging-lantern", new PropBlock { Name = "Hanging Lantern", Visual = new VisualBlock { Model = "models/lantern.glb" }, Placement = new PlacementBlock { On = Surface.Ceiling } });
        Refused("a collider on a game item's look", () => Blocks.Define("collider-like", new PropBlock { Name = "x", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, Collider = new BoxCollider { Size = Vector3.One } }), "collider", "own collision");
        Refused("a collider on a station", () => Blocks.Define("collider-station", new StationBlock { Name = "x", Collider = new BoxCollider { Size = Vector3.One } }), "collider", "station");
        Refused("a model that isn't there", () => Blocks.Define("model-missing", new PropBlock { Name = "x", Visual = new VisualBlock { Model = "models/nope.glb" } }), "visual.model", "not in the mod's folder");
        Refused("a box of no size", () => Blocks.Define("collider-flat", new PropBlock { Name = "x", Visual = new VisualBlock { Model = "models/lantern.glb" }, Collider = new BoxCollider { Size = new Vector3(1, 0, 1) } }), "collider.size");
    }

    private static string Model(string file) => BlockIds.Derive(Me, "model", file[..file.LastIndexOf('.')]).ToString();

    private void Props()
    {
        const string C = "ColliderResourceComponent.colliders[0]", P = "equipment.placementColliders[0]";
        var lantern = Model("models/lantern.glb");
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-4;
        var vaseTemplate = Content.Templates.Get(S(Content.ItemData.Get("Prop_Decoration_T2_Vase_Clay_01")!, "equipment.placedEntity"))!;
        foreach (var twin in new[] { "", "-json" })
        {
            // from the model: the glTF's box, 0.4 x 0.8 x 0.3 standing on the origin
            var item = Item("lantern-prop" + twin);
            var t = Content.Templates.Get($"{Me}.lantern-prop{twin}.prop")!;
            Check(S(t, "ModelResource.model") == lantern && S(item, "iconModel") == lantern, $"lantern{twin}: shows the cooked model {lantern}: {S(t, "ModelResource.model")}, icon model {S(item, "iconModel")}");
            Check(Near(N(t, $"{C}.dataArray[0].shape.halfSize.x"), 0.2) && Near(N(t, $"{C}.dataArray[0].shape.halfSize.y"), 0.4) && Near(N(t, $"{C}.dataArray[0].shape.halfSize.z"), 0.15) && Near(N(t, $"{C}.dataArray[0].offset.y"), 0.4),
                $"lantern{twin}: its collision is the model's box: half {N(t, $"{C}.dataArray[0].shape.halfSize.x")}, {N(t, $"{C}.dataArray[0].shape.halfSize.y")}, {N(t, $"{C}.dataArray[0].shape.halfSize.z")}, centre y {N(t, $"{C}.dataArray[0].offset.y")}");
            Check(Near(N(item, $"{P}.dataArray[0].shape.halfSize.y"), 0.4) && Near(N(item, "equipment.placementAABBmax.y"), 0.8) && Near(N(item, "equipment.placementAABBmin.x"), -0.2),
                $"lantern{twin}: the item's placement check is the same box: {N(item, $"{P}.dataArray[0].shape.halfSize.y")}, AABB max y {N(item, "equipment.placementAABBmax.y")}");
            var vase = (uint)N(vaseTemplate, $"{C}.joltObjects[0].hash");
            Check((uint)N(t, $"{C}.joltObjects[0].hash") != vase && (uint)N(t, $"{C}.joltObjects[0].hash") == (uint)N(item, $"{P}.joltObjects[0].hash"),
                $"lantern{twin}: a physics shape hash of its own, not the starter's {vase}: {(uint)N(t, $"{C}.joltObjects[0].hash")}");
        }
        // by hand: 1 x 0.5 x 1 on the floor
        var crate = Content.Templates.Get($"{Me}.crate-lantern.prop")!;
        Check(Near(N(crate, $"{C}.dataArray[0].shape.halfSize.x"), 0.5) && Near(N(crate, $"{C}.dataArray[0].shape.halfSize.y"), 0.25) && Near(N(crate, $"{C}.dataArray[0].offset.y"), 0.25),
            $"a box by hand: half {N(crate, $"{C}.dataArray[0].shape.halfSize.x")}, {N(crate, $"{C}.dataArray[0].shape.halfSize.y")}, centre y {N(crate, $"{C}.dataArray[0].offset.y")}");
        // on a ceiling the starter's collider is a capsule
        var hanging = Content.Templates.Get($"{Me}.hanging-lantern.prop")!;
        Check(Near(N(hanging, $"{C}.dataArray[0].shape.radius"), 0.2) && Near(N(hanging, $"{C}.dataArray[0].shape.length"), 0.4),
            $"on a ceiling, a capsule around the model: radius {N(hanging, $"{C}.dataArray[0].shape.radius")}, length {N(hanging, $"{C}.dataArray[0].shape.length")}");
        // the game's vase keeps its own
        var gameVase = vaseTemplate;
        Check(Near(N(gameVase, $"{C}.dataArray[0].shape.halfSize.y"), 0.5), $"the game's vase keeps its box: {N(gameVase, $"{C}.dataArray[0].shape.halfSize.y")}");
        // what Kiln.Blocks says the model is, and the step tools/modelcook makes from the same block, agree on the GUID
        var low = JsonNode.Parse(Blocks.Lowered("lantern-prop-json") ?? "{}");
        Check((string?)low?["model"]?["guid"] == lantern && (string?)low?["collider"]?["from"] == "model", $"what it became names the model and where its box came from: {low?["model"]?.ToJsonString()}, {low?["collider"]?.ToJsonString()}");
    }
}
