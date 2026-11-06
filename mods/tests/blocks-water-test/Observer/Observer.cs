// SPDX-License-Identifier: MIT
using Kiln;

namespace BlocksWaterObserver;

/// <summary>Logs watched values whenever they change (every 25 steps): the test mod's state, its can's definition and recipe, its clone,
/// and the game's own can and template.</summary>
public sealed class ObserverMod : Mod
{
    private const string Test = "blocks-water-test";
    private readonly Dictionary<string, string> _last = new();

    public override void OnLoad() => _ = Loop();

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return "(" + e.GetType().Name + ")"; }
    }

    private async Task Loop()
    {
        while (true)
        {
            for (var i = 0; i < 25; i++) await Context.Scheduler.NextTick();
            if (!Content.Ready || !Content.GameDataAvailable) continue;
            var now = new Dictionary<string, string>
            {
                ["mod"] = Mods.Get(Test)?.State.ToString() ?? "(none)",
                ["big-can definition"] = Try(() => NewContent.Definitions().FirstOrDefault(d => d.Name == $"{Test}.big-can") is { } d ? $"{d.State}{(d.IsPlaceholder ? " placeholder" : "")}" : "(none)"),
                ["big-can recipe"] = Try(() => NewContent.Definitions().FirstOrDefault(d => d.Name == $"{Test}.big-can.recipe") is { } d ? d.State : "(none)"),
                ["big-can water template"] = Try(() => Content.Templates.Get($"{Test}.big-can.water") is { } t ? $"{t.Get("FuelSetup.maxFuelCharges").Integer}" : "(none)"),
                ["game template"] = Try(() => $"{Content.Templates.Get("Pide_Fuel_Watering_Can")!.Get("FuelSetup.maxFuelCharges").Integer}, modified {Content.Templates.Get("Pide_Fuel_Watering_Can")!.Modified}"),
                ["game can Volume"] = Try(() => $"{Content.ItemData.Get("Tool_T1_WateringCan")!.Get("impactValues.Volume").AsDouble}"),
                ["game can item entity"] = Try(() => $"{Content.ItemData.Get("Tool_T1_WateringCan")!.Get("pidEntity").Guid}"),
            };
            foreach (var (k, v) in now)
            {
                if (_last.TryGetValue(k, out var old) && old != v) Log.Info($"OBSERVE {k}: {old} -> {v}");
                else if (!_last.ContainsKey(k)) Log.Info($"OBSERVE {k}: {v}");
                _last[k] = v;
            }
        }
    }
}
