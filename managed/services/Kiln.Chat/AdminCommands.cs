// SPDX-License-Identifier: MIT
// Kiln's admin commands for kick and ban (since 0.2.0-dev.27): /kick, /ban, /unban, /bans. Kiln's own (owner "kiln"),
// level Admin, nodes kiln.command.<name> (permissions.json can grant or deny each). They call Kiln.Players' Players.Kick
// and Bans; Kiln.Network does the kicking. Registered when Kiln.Chat starts (the console service, first service tick).
using System.Globalization;

namespace Kiln;

internal static class AdminCommands
{
    private static int _registered;

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's commands: they stay whatever mod runs this tick
        try
        {
            // one by one: a mod that already has one of these names keeps it, and Kiln's other commands still come
            Add("kick", Kick, "<player> [reason...]", "kicks a player who is in game (they can join again)");
            Add("ban", Ban, "<player|steamid> [<n>m|<n>h|<n>d] [reason...]",
                "bans a player by Steam id (Kiln's ban list, kept for every world of this server) and kicks them if in game");
            Add("unban", Unban, "<steamid|accountid>", "removes a player from Kiln's ban list");
            Add("bans", List, null, "lists Kiln's ban list");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static void Add(string name, CommandHandler handler, string? usage, string description)
    {
        try
        {
            Commands.Register(name, handler, CommandPermission.Admin, usage, description);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.chat: Kiln's /{name} is not registered: {e.Message}");
        }
    }

    private static Player Target(CommandContext c, string what)
    {
        Player? p;
        try
        {
            p = Players.Find(what) ?? (ulong.TryParse(what, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? Players.FindBySteamId(id) : null);
        }
        catch (NotSupportedException e)
        {
            throw new CommandException(e.Message);
        }
        return p is { InGame: true } ? p : throw new CommandException($"no player '{what}' in game");
    }

    private static string Reason(CommandContext c, int from, string fallback) => c.Count > from ? c.Rest(from).Trim() : fallback;

    private static void Kick(CommandContext c)
    {
        if (c.Count < 1) throw new CommandException("usage: kick <player> [reason...]");
        var p = Target(c, c.Args[0]);
        var reason = Reason(c, 1, "You were kicked from this server.");
        try
        {
            var t = Players.Kick((PlayerRef)p, reason);
            c.Reply($"kicking {p.Name}: {reason}");
            _ = t.ContinueWith(r => Backend.Current.Log(0, LogLevel.Info, $"kiln: /kick by {c.Source.Name}: {(r.IsCompletedSuccessfully ? r.Result.ToString() : r.Exception?.InnerException?.Message)}"),
                TaskScheduler.Default);
        }
        catch (Exception e) when (e is KilnUnavailableException or ArgumentException)
        {
            throw new CommandException(e.Message);
        }
    }

    private static void Ban(CommandContext c)
    {
        if (c.Count < 1) throw new CommandException("usage: ban <player|steamid> [<n>m|<n>h|<n>d] [reason...]");
        var at = 1;
        TimeSpan? duration = null;
        if (c.Count > 1 && ParseDuration(c.Args[1]) is { } d)
        {
            duration = d;
            at = 2;
        }
        var reason = Reason(c, at, "Banned by an admin.");
        try
        {
            BanEntry entry;
            Player? online = null;
            try { online = Players.Find(c.Args[0]); }
            catch (NotSupportedException) { }  // kiln.players cannot say: a Steam id still works
            if (online is { SteamId: not 0 }) entry = Bans.Add((PlayerRef)online, reason, duration);
            else if (ulong.TryParse(c.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var steam) && steam != 0)
                entry = Bans.Add(steam, reason, duration);
            else throw new CommandException($"no player '{c.Args[0]}' with a known Steam id; give the SteamID64 instead");
            c.Reply($"banned {(entry.Name.Length > 0 ? entry.Name + " " : "")}{entry.SteamId}{(entry.Until is { } u ? $" until {u.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "")}: {entry.Reason}");
        }
        catch (Exception e) when (e is KilnUnavailableException or ArgumentException or InvalidOperationException)
        {
            throw new CommandException(e.Message);
        }
    }

    private static void Unban(CommandContext c)
    {
        if (c.Count < 1 || !ulong.TryParse(c.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new CommandException("usage: unban <steamid|accountid>");
        try
        {
            c.Reply(Bans.Remove(id) ? $"{id} is no longer banned" : $"{id} is not on Kiln's ban list");
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
            throw new CommandException(e.Message);
        }
    }

    private static void List(CommandContext c)
    {
        var all = Bans.All;
        if (all.Count == 0)
        {
            c.Reply("Kiln's ban list is empty");
            return;
        }
        foreach (var b in all.OrderBy(b => b.Since))
            c.Reply($"{b.SteamId}{(b.AccountId != 0 && b.AccountId != b.SteamId ? $"/{b.AccountId}" : "")}{(b.Name.Length > 0 ? " " + b.Name : "")} by {b.By} {b.Since.UtcDateTime:yyyy-MM-dd}" +
                    $"{(b.Until is { } u ? $" until {u.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "")}: {b.Reason}");
    }

    /// <summary>"30m", "12h", "7d" (a positive whole number); null when the word is not a duration.</summary>
    internal static TimeSpan? ParseDuration(string word)
    {
        if (word.Length < 2) return null;
        if (!int.TryParse(word.AsSpan(0, word.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        return char.ToLowerInvariant(word[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
    }
}
