// SPDX-License-Identifier: MIT
// Kiln.Assets in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (the first service tick, AssetsStartup); kiln.assets installs its hooks when it loads, so every row is fixed
// from then on.
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
        var t = T;
        var client = Mods.Side == ModSide.Client;
        if (t == null)
        {
            // sigdb may have resolved a feature that no service is here to serve
            var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing, "kiln.assets is not loaded", Caps.Service);
            foreach (var name in (string[])[Caps.Resources, Caps.Content, Caps.Icons]) ServiceCaps.Lower(name, reason, text);
            ServiceCaps.Static(Caps.Textures, false, CapabilityReason.ServiceMissing, "kiln.assets is not loaded", Caps.Service);
            return;
        }
        // kiln.assets publishes its table also when its hooks are off (safe idle, or an install that failed): every call
        // of a part whose hooks are off answers "unavailable", so its row says so too
        var hooks = HookState(t);
        if (!hooks.Resources) ServiceCaps.Lower(Caps.Resources, CapabilityReason.Faulted, Off("the resource hooks", hooks.ResourcesWhy));
        if (!hooks.Content) ServiceCaps.Lower(Caps.Content, CapabilityReason.Faulted, Off("the content hooks", hooks.ContentWhy));
        if (client && !hooks.Icons) ServiceCaps.Lower(Caps.Icons, CapabilityReason.Faulted, Off("the UI icon hooks", hooks.IconsWhy));
        if (!client)
            ServiceCaps.Report(Caps.Textures, CapabilityState.Unavailable, CapabilityReason.WrongSide,
                "kiln.assets overrides textures in the game client only");
        else
            // texture_override_img needs both the resource and the content hooks
            ServiceCaps.Static(Caps.Textures, hooks.Resources && hooks.Content, CapabilityReason.Faulted,
                !hooks.Resources ? Off("the resource hooks", hooks.ResourcesWhy) : Off("the content hooks", hooks.ContentWhy),
                Caps.Service, Caps.Resources, Caps.Content);
    }

    private static string Off(string what, string why) =>
        why.Length > 0 ? $"{what} of kiln.assets are off: {why}" : $"{what} of kiln.assets are off on this build (see kiln.log)";

    /// <summary>What kiln.assets says about its hooks (<c>status</c>: <c>{..., "hooks": {"resources": true,
    /// "resourcesWhy": "...", "content": ..., "uiIcons": ...}}</c>). The switches are read as written (native prints the
    /// reasons unescaped, so a reason with a quote or a backslash breaks the JSON, not the switches); all off when the
    /// status cannot be read.</summary>
    internal static (bool Resources, string ResourcesWhy, bool Content, string ContentWhy, bool Icons, string IconsWhy) HookState(AssetsNative.Table* t)
    {
        if (t->Status == null) return (false, "it has no status function", false, "", false, "");  // a test's partial table
        nuint n = 0;
        t->Status(null, 0, &n);
        var buf = new byte[(int)n + 1];
        int rc;
        fixed (byte* p = buf) rc = t->Status(p, (nuint)buf.Length, &n);
        if (rc != AssetsNative.Ok) return (false, "its status could not be read", false, "", false, "");
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, (int)Math.Min(n, (nuint)buf.Length - 1));
        var at = text.IndexOf("\"hooks\":", StringComparison.Ordinal);
        if (at < 0) return (false, "its status names no hooks", false, "", false, "");
        var hooks = text[at..];
        bool On(string name) => hooks.Contains($"\"{name}\":true", StringComparison.Ordinal);
        string Why(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(hooks, $"\"{name}\":\"([^\"\\\\]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        return (On("resources"), Why("resourcesWhy"), On("content"), Why("contentWhy"), On("uiIcons"), Why("uiWhy"));
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the capability rows are there before a
/// mod asks for them, not from Kiln.Assets' first use.</summary>
internal static class AssetsStartup
{
    static AssetsStartup() => Assets.Rows.Ensure();
}
