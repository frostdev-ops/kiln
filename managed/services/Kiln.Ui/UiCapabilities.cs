// SPDX-License-Identifier: MIT
// Kiln.Ui in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Kiln.Ui's windows never throw for a
// missing capability (their calls draw nothing and return null or false), so they have no row of their own: kiln.ui,
// kiln.render and the client's sigdb features answer. Ui.Values (dev.28) throws by the error rule and answers with
// kiln.ui.values' six sigdb features, lowered here when its table is missing. Reported at the service's start (the first service tick) and at its first use.
// The tables are published also when single hooks failed (kiln.ui and kiln.render install them at their start, before
// they publish): the features those hooks carry are lowered then.
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

    /// <summary>kiln.ui.values' sigdb features (in-core, client only; each held by the client gate until its check
    /// passed): Ui.Values' rows.</summary>
    public static readonly string[] ValueFeatures =
    [
        "ui.values.detail", "ui.values.slot", "ui.values.held", "ui.values.hud", "ui.values.tooltip_lines",
        "ui.slots.inspect",
    ];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        if (UiNative.T == null) ServiceCaps.LowerFeatures(Service, Features);
        else if (UiNative.HasGameUi && !ScreensOn())
            // the hook on the game's UI frame failed: native turns screens, both menus and crafting off together
            foreach (var name in (string[])["ui.screens", "ui.main_menu", "ui.system_menu", "ui.crafting"])
                ServiceCaps.LowerOff(name, CapabilityReason.Faulted,
                    "kiln.ui could not hook the game's UI frame (see kiln.log): no game-UI integration");
        if (UiValuesNative.T == null) ServiceCaps.LowerFeatures(UiValuesNative.Service, ValueFeatures);
        // Collected lines have one production consumer: the overlay's Item details window.
        ServiceCaps.LowerFeatures(Service, "ui.values.tooltip_lines");
        if (!RenderNative.Available) ServiceCaps.LowerFeatures(Render, "render.camera");
        else if (RenderNative.ReadStatus() is { Camera: 0 })
            ServiceCaps.LowerOff("render.camera", CapabilityReason.Faulted, "kiln.render's camera hook is off (see kiln.log)");
    }

    // kiln.ui's screen answers "unavailable" exactly when the game-UI integration is off (feature or frame hook)
    private static bool ScreensOn()
    {
        UiNative.Screen s = default;
        s.Size = (uint)sizeof(UiNative.Screen);
        return UiNative.T->GetScreen != null && UiNative.T->GetScreen(&s) == UiNative.Ok;
    }
}
