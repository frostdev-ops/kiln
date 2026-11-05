// SPDX-License-Identifier: MIT
using Kiln;

namespace ContentObserver;

/// <summary>Every 12 steps (~0.5 s) once kiln.content is ready: reads a few watched values and logs each one that
/// changed since the last look ("OBSERVE items Material_T1_Wood_Logs.maxStackSize: 250 -> 2000").</summary>
public sealed class ObserverMod : Mod
{
    private readonly Dictionary<string, string> _last = new();

    private static readonly (ContentKind Kind, string Entry, string What)[] Watched =
    [
        (ContentKind.Items, "Material_T1_Wood_Logs", "maxStackSize"),
        (ContentKind.Items, "Material_T1_Twigs", "maxStackSize"),
        (ContentKind.Items, "Material_T1_Resin", "maxStackSize"),
        (ContentKind.Recipes, "Tool_basic_FishingRod_T0_PK", "inputs"),
        (ContentKind.Recipes, "Tool_basic_FishingRod_T0_PK", "outputs"),
        (ContentKind.Recipes, "Tool_basic_FishingRod_T0_PK", "craftingDuration"),
        (ContentKind.Recipes, "Ammo_Arrow_T1_Flint", "outputs"),
        (ContentKind.Recipes, "Material_T1_Factory_03_Charcoal", "craftingDuration"),
        (ContentKind.Loot, "Material_T1_Wood_Logs", "stackSizeMax"),
        (ContentKind.Loot, "Material_T1_Twigs", "stackSizeMax"),
    ];

    public override void OnLoad()
    {
        Context.Tick.Pre += (in TickInfo t) =>
        {
            if (t.Step % 12 == 0 && Content.Available && Content.Ready) Look();
        };
    }

    private void Look()
    {
        foreach (var (kind, name, what) in Watched)
        {
            var e = Content.Registry(kind).Get(name);
            if (e == null) continue;
            var v = what switch
            {
                "inputs" => string.Join(", ", e.Inputs),
                "outputs" => string.Join(", ", e.Outputs),
                _ => e.Get(what).ToString(),
            };
            Note($"{kind.ToString().ToLowerInvariant()} {name}.{what}", v);
        }
        var st = Content.Status()!;
        Note("status", $"generation {st.Generation}, packs {st.Packs}, changed {st.Changed}, conflicts {st.Conflicts}, " +
                       $"stack guard {st.StackGuard}, drop tables {st.Drops}");
    }

    private void Note(string key, string value)
    {
        if (_last.TryGetValue(key, out var old) && old == value) return;
        Log.Info($"OBSERVE {key}: {(old == null ? "" : old + " -> ")}{value}");
        _last[key] = value;
    }
}
