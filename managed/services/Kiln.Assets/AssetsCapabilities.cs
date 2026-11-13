// SPDX-License-Identifier: MIT
// Kiln.Assets in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// first use; every row is fixed from then on.
namespace Kiln;

public static unsafe partial class Assets
{
    /// <summary>The capability names Kiln.Assets answers for.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row).</summary>
        public const string Service = AssetsNative.Service;

        /// <summary>sigdb: resources served and overridden (<see cref="Assets.Register"/>, <see cref="Assets.Override"/>).</summary>
        public const string Resources = "assets.resources";

        /// <summary>sigdb: content blobs (<see cref="Assets.AddBlob"/>, <see cref="Assets.OverrideBlob"/>).</summary>
        public const string Content = "assets.content";

        /// <summary>sigdb, game client: item icons (<see cref="Assets.RegisterIcon(Guid, byte[])"/>).</summary>
        public const string Icons = "assets.ui_icons";

        /// <summary>Kiln.Assets' own row: texture overrides (<see cref="Assets.OverrideTexture"/>), which kiln.assets
        /// does in the game client only.</summary>
        public const string Textures = "assets.textures";
    }

    internal static readonly ServiceCapRows Rows = new(Caps.Service, ReportCapabilities);

    static Assets() => Rows.Ensure();

    private static void ReportCapabilities()
    {
        var here = T != null;
        if (!here)
        {
            // sigdb may have resolved a feature that no service is here to serve
            var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing, "kiln.assets is not loaded", Caps.Service);
            foreach (var name in (string[])[Caps.Resources, Caps.Content, Caps.Icons]) ServiceCaps.Lower(name, reason, text);
        }
        var client = Mods.Side == ModSide.Client;
        if (here && !client)
            ServiceCaps.Report(Caps.Textures, CapabilityState.Unavailable, CapabilityReason.WrongSide,
                "kiln.assets overrides textures in the game client only");
        else
            ServiceCaps.Static(Caps.Textures, here, CapabilityReason.ServiceMissing, "kiln.assets is not loaded", Caps.Service);
    }
}
