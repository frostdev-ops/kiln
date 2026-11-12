// SPDX-License-Identifier: MIT
// Guided checks for a real player joined to a server with this mod (Kiln.Testing): what a kick and a ban look like in the
// game, and whether the game's login failure takes a player who is already in the world out of it (docs/re/network.md
// proved it only during the join). Each comes in two parts: the first ends with the kick, the second runs after rejoining.
using Kiln;
using Kiln.Testing;

namespace KickTest;

public sealed partial class KickTestMod
{
    private const string Since = "0.2.0-dev.27";
    private const string Area = "Kick and ban";
    private const string KickReason = "client check: you were kicked on purpose";
    private const string BanReason = "client check: a 2-minute ban";
    private static DateTime? _kickedAt, _bannedAt;
    private static KickResult? _kickResult;

    /// <summary>Runs <paramref name="act"/> on the tick thread a few seconds after the check's verdict, outside the check
    /// (the check ends with the verdict; the kick ends the player's session).</summary>
    private void Later(Func<Task> act) => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        Context.Scheduler.Post(() => _ = act());
    });

    [ClientCheck("players.kick", "Kicked while in the world (1 of 2)", Area = Area, Since = Since, Needs = ClientCheckNeeds.Joined, Order = 1)]
    public async Task KickBefore(CheckContext c)
    {
        var who = c.Player;
        var p = Players.Find(who);
        c.Expect(p is { InGame: true }, "you are a player in game on this server", p?.ToString());
        c.Expect(Capabilities.Is("players.kick"), "players.kick is available", Capabilities.Why("players.kick"));
        c.Instruct("Press pass. About 4 seconds later the server kicks you with the reason \"" + KickReason + "\". Watch the screen: " +
                   "Kiln's notice and the game's own dialog. Then join this server again and run \"players.kick.after\".");
        await c.Verdict();
        if (p is null) return;
        Later(async () =>
        {
            _kickedAt = DateTime.UtcNow;
            _kickResult = await Players.Kick((PlayerRef)p, KickReason);
            Log.Info($"kick-test: client check kick: {_kickResult}");
        });
    }

    [ClientCheck("players.kick.after", "Kicked while in the world (2 of 2): what you saw", Area = Area, Since = Since,
        Needs = ClientCheckNeeds.Joined, Order = 2)]
    public async Task KickAfter(CheckContext c)
    {
        c.Expect(_kickedAt is not null, "players.kick ran first and kicked", _kickResult?.ToString());
        c.Expect(_kickResult is { Ok: true }, "Players.Kick says the login failure went out", _kickResult?.ToString());
        c.Instruct("After the kick: were you taken out of the world to the main menu (not left standing in the world)? Did Kiln show " +
                   "the reason \"" + KickReason + "\"? Note in a fail what the game's own dialog said. Could you join again at once?");
        await c.Verdict();
    }

    [ClientCheck("players.ban", "Banned for 2 minutes (1 of 2)", Area = Area, Since = Since, Needs = ClientCheckNeeds.Joined, Order = 3)]
    public async Task BanBefore(CheckContext c)
    {
        var p = Players.Find(c.Player);
        c.Expect(p is { InGame: true, SteamId: not 0 }, "you are a player in game with a known Steam id", p?.ToString());
        c.Expect(Capabilities.Is("players.bans"), "players.bans is available", Capabilities.Why("players.bans"));
        c.Instruct("Press pass. About 4 seconds later Kiln bans you for 2 minutes (\"" + BanReason + "\") and you are kicked. Try to " +
                   "join again at once: you should be refused with that reason. Wait 2 minutes, join again (it should work) and run " +
                   "\"players.ban.after\".");
        await c.Verdict();
        if (p is not { SteamId: not 0 }) return;
        Later(() =>
        {
            _bannedAt = DateTime.UtcNow;
            var entry = Bans.Add((PlayerRef)p, BanReason, TimeSpan.FromMinutes(2));
            Log.Info($"kick-test: client check ban: {entry}");
            return Task.CompletedTask;
        });
    }

    [ClientCheck("players.ban.after", "Banned for 2 minutes (2 of 2): refused, then let in", Area = Area, Since = Since,
        Needs = ClientCheckNeeds.Joined, Order = 4)]
    public async Task BanAfter(CheckContext c)
    {
        var p = Players.Find(c.Player);
        c.Expect(_bannedAt is not null, "players.ban ran first", _bannedAt?.ToString("u"));
        c.Expect(p is not null && Bans.Find(p.SteamId) is null, "the ban has run out (you are in game)");
        c.Instruct("Were you kicked a few seconds after players.ban, and refused when you tried to join again at once, with Kiln " +
                   "showing \"" + BanReason + "\"? Did joining work again after 2 minutes?");
        await c.Verdict();
    }
}
