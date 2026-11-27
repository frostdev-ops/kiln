// SPDX-License-Identifier: MIT
// The census: every enemy creature a pet may be (an enemy, no boss), with the numbers its clearance depends on: its path
// walker (usePathFinding, horizontalRadius, height), its movement collider (width, height), its template scale and model
// bounds, and whether it flies. One "census" line per creature, by its Kiln id (the creatures registry's id rules).
using System.Text.Json.Nodes;
using Kiln;

namespace PetsClearanceLab;

public sealed partial class PetsClearanceLabMod
{
    internal static JsonObject BodyNumbers(GameDataEntry? t)
    {
        var o = new JsonObject
        {
            ["usePathFinding"] = Out.R(Number(t, "PathWalker.usePathFinding") ?? double.NaN),
            ["walkerRadius"] = Out.R(Number(t, "PathWalker.horizontalRadius") ?? double.NaN),
            ["walkerHeight"] = Out.R(Number(t, "PathWalker.height") ?? double.NaN),
            ["colliderWidth"] = Out.R(Number(t, "MovementColliderConfig.width") ?? double.NaN),
            ["colliderHeight"] = Out.R(Number(t, "MovementColliderConfig.height") ?? double.NaN),
            ["templateScale"] = Out.R(Number(t, "ScaleComponent.uniformScale") ?? double.NaN),
            ["flying"] = Present(t, "Flying"),
            ["flyingEnemy"] = Present(t, "FlyingEnemy"),
        };
        var min = new JsonArray();
        var max = new JsonArray();
        foreach (var a in new[] { "x", "y", "z" })
        {
            min.Add(Out.R(Number(t, $"Enemy01BehaviorState.defaultLocalBoundingBoxMin.{a}") ?? double.NaN));
            max.Add(Out.R(Number(t, $"Enemy01BehaviorState.defaultLocalBoundingBoxMax.{a}") ?? double.NaN));
        }
        o["boundsMin"] = min;
        o["boundsMax"] = max;
        return o;
    }

    private async Task Census()
    {
        var all = Creatures.Templates;
        var names = all.GroupBy(t => t.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        var n = 0;
        foreach (var t in all)
        {
            if (t.Kind != CreatureKind.Enemy || t.IsBoss) continue;
            var lower = t.Name.ToLowerInvariant();
            var id = string.IsNullOrEmpty(t.Name) ? $"game:template_{t.Id.ToString("N")[..8]}"
                : names[lower] > 1 ? $"game:{lower}~{t.Id.ToString("N")[..8]}" : $"game:{lower}";
            GameDataEntry? entry = null;
            try { entry = Content.Templates.Get(t.Id.ToString("D")); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
            var o = BodyNumbers(entry);
            o["id"] = id;
            o["template"] = t.Name;
            o["display"] = t.DisplayName;
            o["scale"] = Out.R(t.Scale);
            o["levelMax"] = t.LevelMax;
            o["loaded"] = t.IsLoaded;
            Evidence.Write("census", o);
            if (++n % 40 == 0) await Steps(1);
        }
        Info($"census: {n} enemy creatures (no bosses)");
    }
}
