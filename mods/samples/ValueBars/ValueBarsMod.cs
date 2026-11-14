// SPDX-License-Identifier: MIT
using Kiln;

namespace ValueBars;

/// <summary>
/// Ui.Values (docs/kiln/ui.md "Values in the game's own UI"): what the game draws for an item, the held item and the HUD,
/// replaced in the picture only. The server keeps the real values; a player without this mod sees the game unchanged.
///  - the detail view of anything that holds water shows its water in charges (the raw fuel divided by the item's units
///    per charge) under a label "Charges";
///  - its slot bar is drawn from its own fuel (0/0 stays the game's full bar: the provider passes);
///  - the health pair reads as a percentage (the bar looks the same, the numbers say 0 to 100);
///  - Kiln's "Item details" window lists the water of the last inspected container; F8 (or the pause menu's "Item
///    details") opens it.
/// Each part is a capability of its own and may be off (a server, an unsupported build, or held until its game test):
/// the sample registers what it can and logs the rest. The labels are fixed texts: a label is kept for the whole
/// process, so a provider never makes a new one per frame; numbers go into Current/Max.
/// </summary>
public sealed class ValueBarsMod : Mod
{
    private static readonly UiText Charges = UiText.Of(("en", "Charges"), ("de", "Ladungen"));
    private readonly List<IDisposable> _registrations = [];

    public override void OnLoad()
    {
        Try(Ui.Values.DetailCapability, () => Ui.Values.ItemBar(default, DetailInCharges));
        Try(Ui.Values.SlotCapability, () => Ui.Values.SlotBar(default, SlotFromFuel));
        Try(Ui.Values.HudCapability, () => Ui.Values.Hud(HudValue.Health, HealthPercent));
        if (Try(Ui.Values.TooltipCapability, () => Ui.Values.TooltipLines(default, WaterLines)))
            KeyBindings.Register("valuebars.details", "F8", OpenDetails, "Open Kiln's Item details window");
    }

    public override void OnUnload()
    {
        foreach (var r in _registrations) r.Dispose();  // Kiln removes them with the mod anyway
        _registrations.Clear();
    }

    private bool Try(string capability, Func<UiValueRegistration> register)
    {
        try
        {
            var r = register();
            if (!r.Ok)
            {
                Log.Warn($"valuebars: {capability} refused: {r.Reason}");
                return false;
            }
            _registrations.Add(r);
            return true;
        }
        catch (KilnUnavailableException e)
        {
            Log.Info($"valuebars: {e.Message}");
            return false;
        }
    }

    // Detail view: water in charges. Detail numbers are what the game prints (water: charges), view.Fuel is raw units.
    private static UiProviderDecision DetailInCharges(in UiValueContext context, in UiItemView item, UiBarValue accumulated)
    {
        if (item.FuelMax == 0) return UiProviderDecision.Pass;  // not a water container (or an empty definition)
        var per = Math.Max(1u, item.UnitsPerCharge);
        return UiProviderDecision.Replace(new UiBarEdit(
            Fraction: Math.Clamp((float)item.Fuel / item.FuelMax, 0f, 1f),
            Current: item.Fuel / per,
            Max: item.FuelMax / per,
            Label: new UiLabelEdit(UiLabelMode.Set, Charges)));
    }

    // Slot bar: only the fraction is drawn. 0/0 passes, so the game's own full bar stays.
    private static UiProviderDecision SlotFromFuel(in UiValueContext context, in UiItemView item, UiBarValue accumulated) =>
        item.FuelMax == 0 ? UiProviderDecision.Pass
            : UiProviderDecision.Replace(new UiBarEdit(Fraction: Math.Clamp((float)item.Fuel / item.FuelMax, 0f, 1f)));

    // HUD: the same bar, numbers out of 100. Held and HUD pairs take Current/Max only, with Max above 0.
    private static UiProviderDecision HealthPercent(in UiValueContext context, in UiItemView item, UiBarValue accumulated) =>
        accumulated.Max == 0 ? UiProviderDecision.Pass
            : UiProviderDecision.Replace(new UiBarEdit(Current: (uint)Math.Round(100.0 * accumulated.Current / accumulated.Max), Max: 100));

    // Tooltip lines for Kiln's Item details window. A line per distinct value is interned once and kept: the charge
    // counts of a container are few, so this stays small; never put a clock or a counter into a line.
    private static UiTooltipDecision WaterLines(in UiValueContext context, in UiItemView item)
    {
        if (item.FuelMax == 0) return UiTooltipDecision.None;
        var per = Math.Max(1u, item.UnitsPerCharge);
        return UiTooltipDecision.With(
            UiText.Of(("en", $"Water: {item.Fuel / per} of {item.FuelMax / per} charges"),
                      ("de", $"Wasser: {item.Fuel / per} von {item.FuelMax / per} Ladungen")),
            $"Raw fuel: {item.Fuel} / {item.FuelMax} ({per} a charge)");
    }

    private void OpenDetails()
    {
        try
        {
            var r = Ui.Values.OpenTooltipDetails();
            if (!r.Ok) Hud.Toast($"Item details: {r.Reason}", ToastKind.Info, 3);
        }
        catch (KilnUnavailableException e)
        {
            Log.Info($"valuebars: {e.Message}");
        }
    }
}
