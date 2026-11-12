// SPDX-License-Identifier: MIT
using Kiln;

namespace TkCommands;

/// <summary>A [Command] method, a command registered in code, and one whose handler throws.</summary>
public sealed class TkCommandsMod : Mod
{
    public override void OnLoad()
    {
        Commands.Register("tk-add", ctx =>
        {
            var sum = ctx.Args.Select(int.Parse).Sum();
            ctx.Reply($"sum {sum}");
        }, usage: "<n>...", description: "adds numbers");
        Commands.Register("tk-fail", _ => throw new InvalidOperationException("the handler broke"));
    }

    [Command("tk-echo", Description = "says it back")]
    private void Echo(CommandContext ctx, [Greedy] string text)
    {
        ctx.Reply($"echo: {text}");
        ctx.Reply($"tick thread {Game.IsTickThread}");
    }
}
