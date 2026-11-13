// SPDX-License-Identifier: MIT
// Kiln.Ui in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Kiln.Ui never throws for a missing
// capability (its calls draw nothing and return null or false), so it has no row of its own: kiln.ui, kiln.render and
// the client's sigdb features answer. Reported at the service's start (the first service tick) and at its first use.
namespace Kiln;

internal static unsafe class UiCaps
{
    /// <summary>kiln.ui's table is here (the core's row).</summary>
    public const string Service = "kiln.ui";

    /// <summary>kiln.render's table is here (the core's row).</summary>
    public const string Render = "kiln.render";

    /// <summary>The client's sigdb features kiln.ui serves.</summary>
    public static readonly string[] Features =
    [
        "ui.screens", "ui.main_menu", "ui.menu_page", "ui.menu_page_sound", "ui.system_menu", "ui.crafting", "ui.error_popup",
        "ui.language",
    ];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        if (UiNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
        if (!RenderNative.Available) ServiceCaps.LowerFeatures(Render, "render.camera");
    }
}
