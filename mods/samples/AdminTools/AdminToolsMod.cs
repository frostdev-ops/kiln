// SPDX-License-Identifier: MIT
// Admin tools: the player calls a moderation mod needs, each used the way the contract asks (docs/kiln/modding/contract.md).
//   /strike <player> <reason>   first a warning, then Players.Kick, then Bans.Add for a day. Strikes are kept per player.
//   /pardon <name or Steam id>  lifts the ban and forgets the strikes.
//   /strikes                    who has strikes, who is banned.
//   /scout                      Kiln's NoClip switch for the admin: fly through a wall to look at a build, /scout again to land.
// What to take from it: ask a capability before a call that needs one and say what is missing instead of throwing; read
// the result of a call that can be refused (a kick, NoClip) and pass its reason on; key what you store by Steam id.
using Kiln;

namespace AdminTools;

public sealed class Record
{
    public string Name { get; set; } = "";
    public int Strikes { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class AdminToolsMod : Mod
{
    private const string RecordKey = "record";
    // Capability rows (the /caps command lists them): a server whose game build lacks the game's kick call, or that runs
    // without Kiln.Network, has these off. The mod then still counts strikes and says what it could not do.
    private const string KickCapability = "players.kick";
    private const string BansCapability = "players.bans";
    // A node of the mod's own for /scout, beside the Admin level the command needs anyway: a server owner can take
    // scouting away from one admin group in kiln/config/permissions.json and leave the rest.
    private const string ScoutNode = "admin-tools.scout";
    private static readonly TimeSpan BanFor = TimeSpan.FromHours(24);

    public override void OnLoad()
    {
        Permissions.Register(ScoutNode, PermissionLevel.Admin, "fly through walls with /scout");
        Log.Info($"admin-tools: kick {(Capabilities.Is(KickCapability) ? "available" : "not available")}, " +
                 $"bans {(Capabilities.Is(BansCapability) ? "available" : "not available")}, NoClip {(NoClip.Available ? "available" : "not available")}");
    }

    [Command("strike", Description = "one strike for a player: a warning, then a kick, then a ban for a day", Permission = CommandPermission.Admin)]
    private async Task Strike(CommandContext c, Player target, [Greedy] string reason)
    {
        if (target.SteamId == 0) throw new CommandException($"{target.Name}'s Steam id is not known yet");
        var record = PlayerData.Get<Record>(target.SteamId, RecordKey);
        record.Name = target.Name;
        record.Strikes++;
        record.Reasons.Add(reason);
        switch (record.Strikes)
        {
            case 1:
                var told = Chat.Available && Chat.Tell(target.Entity, $"A warning from {c.Source.Name}: {reason}");
                c.Reply(told ? $"{target.Name}: first strike, warned" : $"{target.Name}: first strike recorded (the warning could not be sent: tell them yourself)");
                break;
            case 2:
                if (!Capabilities.Is(KickCapability))
                {
                    c.Reply($"{target.Name}: second strike recorded. Not kicked: this server cannot kick ({KickCapability} is off).");
                    break;
                }
                // The task ends when the game's refusal went out. A kick the game did not carry out is a result with a
                // reason, not an exception.
                var kick = await Players.Kick((PlayerRef)target, $"Second strike: {reason}");
                c.Reply(kick.Ok ? $"{target.Name}: second strike, kicked" : $"{target.Name}: second strike recorded, but not kicked: {kick.Reason}");
                break;
            default:
                if (!Capabilities.Is(BansCapability))
                {
                    c.Reply($"{target.Name}: strike {record.Strikes} recorded. Not banned: this server keeps no ban list ({BansCapability} is off).");
                    break;
                }
                var ban = Bans.Add((PlayerRef)target, $"Third strike: {reason}", BanFor);
                // A ban refuses the next connection. The player who is in game now is shown out as well.
                var shown = Capabilities.Is(KickCapability) ? await Players.Kick((PlayerRef)target, ban.Reason) : null;
                c.Reply($"{target.Name}: banned until {ban.Until:yyyy-MM-dd HH:mm} UTC" + (shown is { Ok: true } ? "" : " (still in game: the kick did not go out)"));
                break;
        }
    }

    [Command("pardon", Description = "lifts a player's ban and forgets the strikes (a name from /strikes, or a Steam id)", Permission = CommandPermission.Admin)]
    private string Pardon(CommandContext c, string who)
    {
        var steamId = ulong.TryParse(who, out var id) ? id : Known().FirstOrDefault(k => k.Record.Name.Equals(who, StringComparison.OrdinalIgnoreCase)).SteamId;
        if (steamId == 0) throw new CommandException($"no strikes on record for {who}");
        var unbanned = Capabilities.Is(BansCapability) && Bans.Remove(steamId);
        var forgotten = PlayerData.Remove(steamId, RecordKey);
        return unbanned || forgotten ? $"{who}: {(unbanned ? "ban lifted, " : "")}strikes forgotten" : $"{who} had neither a ban nor strikes";
    }

    [Command("strikes", Description = "who has strikes, and Kiln's ban list", Permission = CommandPermission.Admin)]
    private void Strikes(CommandContext c)
    {
        foreach (var (steamId, record) in Known())
            c.Reply($"  {record.Name} ({steamId}): {record.Strikes} strike(s), last: {record.Reasons.LastOrDefault()}");
        if (!Capabilities.Is(BansCapability)) return;
        foreach (var ban in Bans.All)
            c.Reply($"  banned: {ban.Name} ({ban.SteamId}) by {ban.By}, {(ban.Until is { } until ? $"until {until:yyyy-MM-dd HH:mm} UTC" : "until unbanned")}: {ban.Reason}");
    }

    [Command("scout", Description = "NoClip on or off for yourself, to look at a build", Permission = CommandPermission.Admin)]
    private string Scout(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player can fly");
        if (!c.Source.Has(ScoutNode)) throw new CommandException($"this needs the permission {ScoutNode}");
        var on = NoClip.Of(c.Source.Player).On;
        // Turning it on needs the capability; turning it off never does. Without it, say so instead of calling.
        if (!on && !NoClip.Available) return $"NoClip is not available on this game build ({NoClip.Capability} is off)";
        // The server's rules.json, the player's own game and other rule sets all have a say: a refusal comes back as a
        // result whose Message is a sentence for the player.
        var result = NoClip.Set(c.Source.Player, !on);
        return result.Message;
    }

    // NoClip ends by itself on a death or a teleport, and Kiln then says where it put a character that was inside something.
    [Subscribe]
    private void OnNoClipEnded(PlayerNoClipEndedEvent e)
    {
        var why = e.Reason switch
        {
            NoClipEnd.Death => "you died",
            NoClipEnd.Teleport => "you were teleported",
            _ => "it was turned off",
        };
        if (Chat.Available)
            Chat.Tell(e.Player, $"Scouting is over ({why})" + (e.RestoredTo is { } at ? $": you were inside something and are back at {at}." : "."));
    }

    // Everyone this mod has a record for, online or not.
    private static IEnumerable<(ulong SteamId, Record Record)> Known()
    {
        foreach (var steamId in PlayerData.Players)
            if (PlayerData.TryGet<Record>(steamId, RecordKey, out var record))
                yield return (steamId, record);
    }
}
