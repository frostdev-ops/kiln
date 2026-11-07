// SPDX-License-Identifier: MIT
// AnimationBlock.Retarget: clips authored on a rig of their own (an animation pack, another tool's skeleton) played on a body's skeleton. The cook pairs
// the joints and does the work (tools/animcook/anicook/retarget.py); this file holds the block's shape and what Define checks. docs/kiln/modding/animations.md.
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>
/// How <see cref="AnimationBlock.Clips"/> authored on another rig play on <see cref="AnimationBlock.Skeleton"/>: joints are paired by name, each paired
/// joint turns as its source turns away from the rig's reference pose, and translations carry over on the hips, scaled by the rigs' heights. The rigs
/// may differ in joint names, joint count, bone lengths and rest orientations, as long as their reference poses show the same posture.
/// <code>Retarget = new() { Map = { ["mixamorig:Hips"] = "pelvis", ["mixamorig:Spine"] = "spine" } }</code>
/// </summary>
public sealed record RetargetBlock
{
    /// <summary>The clip file's joint -> the skeleton's joint. Joints of the same name pair by themselves (unless <see cref="ByName"/> is false).</summary>
    public IDictionary<string, string> Map { get; init; } = new Dictionary<string, string>();
    /// <summary>Pair joints of the same name that <see cref="Map"/> leaves out. Default true.</summary>
    public bool? ByName { get; init; }
    /// <summary>The clip file's joints whose movement carries over. Default: the topmost paired joints, and the hips below a root bone on the ground.</summary>
    public IReadOnlyList<string>? Translate { get; init; }
    /// <summary>How far translations go on the skeleton. Default: the rigs' heights (hip height over hip height).</summary>
    public double? Scale { get; init; }
    /// <summary>A pose of the clip file that shows the skeleton's rest posture, when the file's own rest pose doesn't (an A-pose rig for a T-pose body).</summary>
    public RetargetPose? Reference { get; init; }
}

/// <summary>A frame of one of the clip file's animations: <c>new("TPose")</c>.</summary>
public sealed record RetargetPose([property: JsonPropertyName("animation")] string Animation, double At = 0);

internal static class Retargets
{
    /// <summary>What <c>Define</c> checks of an animation's <see cref="AnimationBlock.Retarget"/>; the cook checks the joints themselves.</summary>
    internal static void Check(BlockContext ctx, AnimationBlock a, string path)
    {
        if (a.Retarget is not { } r) return;
        var at = path.Length == 0 ? "retarget" : path + ".retarget";
        if (a.Clips == null) throw new BlockError(ctx.Block, at, "retarget plays clips of a glTF on a skeleton: say clips");
        if (string.IsNullOrWhiteSpace(a.Skeleton))
            throw new BlockError(ctx.Block, at, "say which skeleton the clips play on: Skeleton = the body's (its model file's name without the extension)");
        if (r.Scale is { } s && (!double.IsFinite(s) || !(s > 0))) throw new BlockError(ctx.Block, at + ".scale", $"{s} is not above 0 and finite");
        if (r.Reference is { } p && (string.IsNullOrWhiteSpace(p.Animation) || !double.IsFinite(p.At) || p.At < 0))
            throw new BlockError(ctx.Block, at + ".reference", "an animation of the clip file and a time from 0 seconds");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, to) in r.Map)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) throw new BlockError(ctx.Block, at + ".map", "joint names can't be empty");
            if (!targets.Add(to)) throw new BlockError(ctx.Block, $"{at}.map[{from}]", $"the skeleton's joint {to} is paired twice");
        }
        if (r.Map.Count == 0 && r.ByName == false) throw new BlockError(ctx.Block, at + ".map", "nothing pairs: name the joints in Map, or let joints of the same name pair");
    }
}
