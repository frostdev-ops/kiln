using Kiln;

namespace KilnModel1;

/// <summary>The model is data: assets/ (kiln.assets, loaded natively at boot) and content/items.json (kiln.content).
/// This code only reports what the game ended up with, one PASS/FAIL line per reference.</summary>
public sealed class KilnModel1Mod : Mod
{
    private const string Item = "KilnModel1_Blade";
    private const string Model = "e3a5c1d7-48b2-4f69-9a0e-6b7d2c5f8a13";    // the new keen::RenderModel (assets/cook.json)
    private const string Visual = "5b8d2e4f-1c7a-4e93-b6f0-3a9c7d1e5b28";   // its visual template (a copy of the Bronze Sword's)

    public override void OnLoad()
    {
        var assets = Path.Combine(Context.Info.Directory, "assets", "cooked", "cook-report.json");
        Log.Info(File.Exists(assets)
            ? "model cooked (assets/cooked/cook-report.json present)"
            : "assets/cooked/ is missing: run `kiln cook` before deploying (the item then keeps the Bronze Sword's look)");
        if (!Content.Available) return;
        Content.OnReady(_ =>
        {
            if (Content.Items.Get(Item) is not { } e)
            {
                Log.Warn($"{Item} is not registered (see kiln.content's pack report in kiln.log)");
                return;
            }
            Check(e, "equipment.visualEntity", Visual);
            Check(e, "equipment.equipmentEntityModelSetup.model", Model);
            Check(e, "iconModel", Model);
        });
    }

    private void Check(ContentEntry e, string path, string want)
    {
        var got = e.GetRef(path).ToString();
        Log.Info($"{(got == want ? "PASS" : "FAIL")} {Item}.{path} = {got} (want {want})");
    }
}
