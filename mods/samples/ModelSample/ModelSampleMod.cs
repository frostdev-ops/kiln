// SPDX-License-Identifier: MIT
using Kiln;

namespace ModelSample;

/// <summary>
/// Sample: custom 3D models cooked from glTF by tools/modelcook (docs/kiln/modding/models.md).
/// <list type="bullet">
/// <item>assets/assets.json (kiln.assets, loaded natively at boot): an override of the Bronze Sword's
/// keen::RenderModel 9c38c7f0… (a cube-blade sword), a new RenderModel 08525f9a… (the tower sword) and a new visual
/// template 699ccb5f… pointing at it.</item>
/// <item>content/tower-sword.json (kiln.content): the new item Kiln_Sample_TowerSword whose references point at the
/// new model and template, and a hand recipe (one twig).</item>
/// <item>Icons that match the models (assets/icons/, rendered from the same glTF by tools/assets/render-icon.py, named
/// in assets.json as "kind": "icon"): the Bronze Sword is a game item, so kiln.assets retextures its own icon texture;
/// the tower sword is new and gets a new icon texture.</item>
/// </list>
/// The code only reports what the game ended up with: one line per check.
/// </summary>
public sealed class ModelSampleMod : Mod
{
    private const string TowerSword = "Kiln_Sample_TowerSword";
    private const string TowerModel = "08525f9a-f6f9-46f4-8c3e-003e481d740d";
    private const string TowerVisual = "699ccb5f-97a5-4f31-b257-f13c61952589";
    private static readonly Guid TowerSwordGuid = Guid.Parse("56f83e52-ac3b-4f15-ada8-67702ce8e9e6");

    public override void OnLoad()
    {
        var assets = Path.Combine(Context.Info.Directory, "assets", "assets.json");
        Log.Info(File.Exists(assets)
            ? $"ModelSample: {assets} present (kiln.assets serves it from boot)"
            : "ModelSample: assets/ missing: run build-assets.sh before deploying");
        if (!Content.Available) return;
        Content.OnReady(_ =>
        {
            var e = Content.Items.Get(TowerSword);
            if (e == null)
            {
                Log.Warn($"ModelSample: {TowerSword} is not registered (see kiln.content's pack report)");
                return;
            }
            Check(e, "equipment.visualEntity", TowerVisual);
            Check(e, "equipment.equipmentEntityModelSetup.model", TowerModel);
            Check(e, "iconModel", TowerModel);
            if (Assets.Available && Kiln.Client.IsClient)  // icons are drawn by clients only
                Log.Info($"ModelSample: the tower sword's icon is {Assets.IconStateOf(TowerSwordGuid)} (Served: its own PNG; the Bronze Sword's icon texture is retextured at boot)");
        });
    }

    private void Check(ContentEntry e, string path, string want)
    {
        var got = e.GetRef(path).ToString();
        Log.Info($"{(got == want ? "PASS" : "FAIL")} ModelSample: {TowerSword}.{path} = {got} (want {want})");
    }
}
