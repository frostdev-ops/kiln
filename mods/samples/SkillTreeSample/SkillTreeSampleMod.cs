// SPDX-License-Identifier: MIT
using Kiln;

namespace SkillTreeSample;

/// <summary>
/// kiln.skills sample. Everything is data (skills/sample.json): Kiln reads its "gameTree" natively at boot (the game's
/// own tree gets two nodes) and its "trees" at its first service tick (the Kiln talent tree "Ember Arts"). The code
/// only watches: changes are logged on the server, and a Kiln client shows a toast for its own unlocks.
/// </summary>
public sealed class SkillTreeSampleMod : Mod
{
    public override void OnLoad()
    {
        var game = GameTree.Status;
        Log.Info(game is null ? "skilltreesample: kiln.skills is not available here" : $"skilltreesample: {game}");
        foreach (var d in GameTree.Definitions.Where(d => d.Owner == "skilltreesample"))
            Log.Info($"skilltreesample: game-tree node {d.Name} (0x{d.Id:x8}) {d.State}{(d.Index >= 0 ? $" at index {d.Index}" : "")}{(d.Error is { } e ? $": {e}" : "")}");
    }

    [Subscribe]
    private void OnChanged(TalentChangedEvent e)
    {
        if (e.Change.Tree.Id != "skilltreesample:ember") return;
        Log.Info($"skilltreesample: {e.Change.Holder} {e.Change.Kind} {e.Change.Node} -> level {e.Change.NewLevel}, {e.Change.Points} point(s) left");
        if (e.Change is { Kind: TalentChangeKind.Unlock, Node: "core" }) Log.Info("skilltreesample: Ember Core taken: the whole tree is done");
    }

    [Subscribe]
    private void OnRefunded(GameNodesRefundedEvent e) =>
        Log.Info($"skilltreesample: {e.Nodes.Count} game-tree node(s) of a mod that is gone came back as points for entity {e.Player}");
}
