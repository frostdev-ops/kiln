// SPDX-License-Identifier: MIT
using Kiln;

namespace AssetSample;

/// <summary>
/// Reports how kiln.assets served this sample's icon and retexture. The assets themselves are data: the native boot
/// path reads assets/assets.json before the UI builds. Client only shows the visible results (client checklist).
/// </summary>
public sealed class AssetSampleMod : Mod
{
    private static readonly Guid Emberseal = Guid.Parse("7b7c5f14-2a9c-4b7e-9a3f-10e2bb6e12c9");

    public override void OnLoad()
    {
        if (!Assets.Available) { Log.Info("AssetSample: kiln.assets unavailable (safe idle or old build)"); return; }
        Log.Info($"AssetSample: assets status {Assets.Status()}");
        Log.Info($"AssetSample: Emberseal icon state = {Assets.IconStateOf(Emberseal)} (SERVED once the UI cache has refilled)");
    }
}
