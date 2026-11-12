// SPDX-License-Identifier: MIT
using Kiln;

namespace TkPlayers;

/// <summary>Logs who is there at load, every join and leave through both of Kiln.Players' paths (its events and the bus),
/// and has commands for the permission tests: an admin-only one, one behind a node of its own, and one that takes a player.</summary>
public sealed class TkPlayersMod : Mod
{
    public override void OnLoad()
    {
        Log.Info($"present: {string.Join(",", Players.All.Select(p => p.Name))}");
        Players.Joined += p => Log.Info($"joined: {p.Name} entity {p.Entity.Value} steam {p.SteamId}");
        Players.Left += p => Log.Info($"left: {p.Name} entity {p.Entity.Value}");
        Commands.Register("tkp-admin", ctx => ctx.Reply($"admin ok for {ctx.Source.Name}"), permission: CommandPermission.Admin);
        Commands.Register("tkp-everyone", ctx => ctx.Reply($"hello {ctx.Source.Name}"), permission: CommandPermission.Everyone);
        Permissions.Register("tkplayers.secret", PermissionLevel.Admin, "a node only a grant gives a plain player");
        Commands.Register("tkp-secret", ctx =>
        {
            if (!ctx.Source.Has("tkplayers.secret")) throw new CommandException("no secret for you");
            ctx.Reply("the secret");
        }, permission: CommandPermission.Everyone);
        Commands.Register(Commands.Literal("tkp-whois")
            .Then(Commands.Argument("target", Arg.Player()).Executes(ctx => ctx.Reply($"target {ctx.Get<Player>("target").Name}"))));
    }

    [Subscribe]
    private void OnJoined(PlayerJoinedEvent e) => Log.Info($"bus joined: {e.Player.Name} seq {e.Sequence}");

    [Subscribe]
    private void OnLeft(PlayerLeftEvent e) => Log.Info($"bus left: {e.Player.Name} seq {e.Sequence}");
}
