// SPDX-License-Identifier: MIT
// The second owner of a tool wear rule (toolwear-test's reveal case): its rule is its own, set and taken away on
// toolwear-test's messages, on the tick thread.
using Kiln;

namespace ToolwearTestB;

public sealed class ToolwearTestBMod : Mod
{
    private ToolWearRule? _rule;

    public override void OnLoad()
    {
        Context.Messages.On("set", m =>
        {
            var (item, cost) = ((uint, int))m.Payload!;
            var r = Items.SetToolWearPerDebit(new ItemId(item), cost);
            _rule = r.Rule;
            Mods.Send("toolwear-test", "b-done", r.Ok ? "set" : "refused: " + r.Reason);
        });
        Context.Messages.On("drop", _ =>
        {
            _rule?.Dispose();
            _rule = null;
            Mods.Send("toolwear-test", "b-done", "dropped");
        });
    }
}
