// SPDX-License-Identifier: MIT
// 16b-tool-durability: the guided check 16b-g, which opens items.tool_wear_debit on a game client
// (native/core/gates/dev28-16b-tool-durability.json). In a hosted game the check sets a cost of 7 per debit for the game's
// stone pickaxe and gives it to the player; what the code can see is asserted (the game's debits of that pickaxe, each 7,
// logged with current and maximum durability); what only a player can judge is asked: that the durability bar the game
// shows follows the server's value and does not jump back (the client predicts the bar). The fixture mod is
// mods/tests/toolwear-test (on a game client it does not run its lab cases). The joined half (a dedicated server's rule
// seen from a joined client) is not here: it needs a rule set on the server for that player.
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

public sealed partial class ClientPassMod
{
    private const string ToolWearFixture = "toolwear-test", ToolWearRow = "items.tool_wear_debit";
    private const int ToolWearOrder = 2000 + (28260 - 28000) * 10;

    [ClientCheck("16b-g", "Tool wear: a pickaxe worn 7 per debit, and its durability bar", Area = "Items", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = ToolWearOrder + 0)]
    public async Task ToolWearPerDebit(CheckContext c)
    {
        if (!await Need(c, ToolWearFixture) || !await NeedOpen(c, ToolWearRow) || !await NeedPlayer(c)) return;
        if (!Capabilities.Is(ToolWearRow))
        {
            await c.Skip($"{ToolWearRow} is unavailable: {Capabilities.Why(ToolWearRow)}");
            return;
        }
        var pickaxe = Items.All.FirstOrDefault(x => x.Name == "Tool_T0_Pickaxe_Stone_Grassland");
        if (pickaxe is null)
        {
            await c.Skip("the game's stone pickaxe (Tool_T0_Pickaxe_Stone_Grassland) is not in this build's item catalog");
            return;
        }
        var debits = new List<ToolDurabilityChangedEvent>();
        var watch = Events.On<ToolDurabilityChangedEvent>(e =>
        {
            if (e.Actor != c.Player || e.Item.Item != pickaxe.Id) return;
            debits.Add(e);
            c.Log.Info($"client-pass: 16b-g debit {debits.Count}: {e.Source} {e.Before} -> {e.After} of {e.Maximum} (asked {e.RequestedDelta}) step {e.Step}");
        });
        c.OnEnd(() => watch.Dispose());
        var rule = Items.SetToolWearPerDebit(pickaxe.Id, 7);
        if (!c.Expect(rule.Ok, "a cost of 7 per debit for the stone pickaxe", rule.Ok ? "set" : rule.Reason))
        {
            await c.Skip($"the pickaxe's cost of 7 could not be set: {rule.Reason}");
            return;
        }
        c.OnEnd(() => rule.Rule?.Dispose());
        await GiveGame(c, pickaxe.Name);
        c.Instruct("You were given a Pickaxe whose every wear costs 7 durability instead of 1. Put it in your hands and, for about a minute, " +
                   "hit a creature with it (a weak enemy or an animal; a hit that lands is what wears it). Watch the pickaxe's durability bar " +
                   "on the hotbar and in its tooltip in the inventory (I) while you do.");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(75);
        while (DateTime.UtcNow < until) await c.Ticks(10);
        c.Log.Info($"client-pass: 16b-g {debits.Count} debits of the pickaxe; last {(debits.Count > 0 ? $"{debits[^1].After} of {debits[^1].Maximum}" : "none")}");
        if (debits.Count == 0)
        {
            await c.Skip("no hit wore the pickaxe in the time (no debit to judge the bar by)");
            return;
        }
        c.Expect(debits.All(d => d.RequestedDelta == -7 && d.OriginalDelta == -1), "each debit asked 7 instead of the game's 1", $"{debits.Count} debits");
        c.Expect(debits.All(d => d.Before - d.After == Math.Min(7, d.Before)), "each debit took 7 (or what was left)",
            string.Join(", ", debits.Select(d => $"{d.Before}->{d.After}")));
        c.Instruct("Did the pickaxe's bar go down in steps of about 7 durability per wearing hit (some hits do not wear it: the game's skill can save one), " +
                   "the same on the hotbar and in the tooltip, and did it stay where it went (no jump back up a moment later)?");
        await c.Verdict();
    }
}
