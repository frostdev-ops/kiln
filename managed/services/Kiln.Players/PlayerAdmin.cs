// SPDX-License-Identifier: MIT
// Kick and ban (since 0.2.0-dev.27). Kiln.Players owns the API and the ban list (kiln/data/kiln-players/bans.json, the
// server's Kiln data, kept across worlds); Kiln.Network, which references this library, does the kicking: it fills
// PlayerAdmin.Kicker at its start, and its server tick refuses a banned player's connection (and posts PlayerLoginEvent)
// as soon as the connection names an account or a Steam id. Docs: docs/kiln/services/players.md "Kick and ban".
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The answer to <see cref="Players.Kick"/>: whether the player's connection was refused.</summary>
/// <remarks><see cref="Ok"/> false with a <see cref="Reason"/>: the player is not in game, or the game's kick call
/// failed.</remarks>
[ServerOnly]
public sealed record KickResult : IKilnResult
{
    internal KickResult(PlayerRef player, string name, bool ok, string? reason)
    {
        Player = player;
        Name = name;
        Ok = ok;
        Reason = ok ? null : reason;
    }

    /// <summary>The player that was kicked (or was to be).</summary>
    public PlayerRef Player { get; }

    /// <summary>The player's name when known; "" otherwise.</summary>
    public string Name { get; }

    /// <summary>True when the player was refused: the game's login failure went out, or the player's game left on Kiln's
    /// refusal before it did.</summary>
    public bool Ok { get; }

    /// <summary>Why not; null when <see cref="Ok"/>.</summary>
    public string? Reason { get; }

    /// <summary>"kicked …" or the reason.</summary>
    public override string ToString() => Ok ? $"kicked {Describe()}" : $"{Describe()} not kicked: {Reason}";

    private string Describe() => Name.Length > 0 ? $"{Name} ({Player})" : Player.ToString();
}

/// <summary>One entry of Kiln's ban list. A connection is refused when its Steam id or its game account id matches.</summary>
/// <param name="SteamId">SteamID64; 0 when the ban names only an account id.</param>
/// <param name="AccountId">The game's account id (the player record's, what <see cref="Player.AccountId"/> says); 0 when
/// unknown.</param>
/// <param name="Name">The player's name when banned (for the list only; names do not identify).</param>
/// <param name="Reason">What the player is told.</param>
/// <param name="By">Who banned: a mod id, a player's name, "console".</param>
/// <param name="Since">When.</param>
/// <param name="Until">When it ends by itself; null: until unbanned.</param>
public sealed record BanEntry(ulong SteamId, ulong AccountId, string Name, string Reason, string By, DateTimeOffset Since,
    DateTimeOffset? Until)
{
    /// <summary>True while the ban holds at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) => Until is null || now < Until;

    /// <summary>True when the ban names this Steam id or this account id (0 matches nothing).</summary>
    public bool Matches(ulong steamId, ulong accountId) =>
        (steamId != 0 && (SteamId == steamId || AccountId == steamId)) || (accountId != 0 && (AccountId == accountId || SteamId == accountId));
}

/// <summary>Kiln kicked a player: <see cref="Players.Kick"/>, a ban, or a <see cref="PlayerLoginEvent"/> handler that
/// refused. Posted on the tick thread after the game's login failure went out. Observe only.</summary>
[ServerOnly]
public sealed class PlayerKickedEvent : KilnEvent
{
    internal PlayerKickedEvent(PlayerRef player, string name, string reason, string? by, bool banned)
    {
        Player = player;
        Name = name;
        Reason = reason;
        By = by;
        Banned = banned;
    }

    /// <summary>The player (Steam id 0 when the connection named only an account).</summary>
    public PlayerRef Player { get; }

    /// <summary>The player's name, "" when unknown.</summary>
    public string Name { get; }

    /// <summary>What the player was told.</summary>
    public string Reason { get; }

    /// <summary>The mod that kicked; null for Kiln (a ban, a login refusal) or the console.</summary>
    public string? By { get; }

    /// <summary>True when Kiln's ban list refused the connection.</summary>
    public bool Banned { get; }
}

/// <summary>An entry was added to Kiln's ban list (<see cref="Bans.Add(PlayerRef, string, TimeSpan?)"/>). Tick thread.</summary>
[ServerOnly]
public sealed class PlayerBannedEvent : KilnEvent
{
    internal PlayerBannedEvent(BanEntry ban) => Ban = ban;

    /// <summary>The new entry.</summary>
    public BanEntry Ban { get; }
}

/// <summary>An entry left Kiln's ban list (<see cref="Bans.Remove"/>; not when one runs out). Tick thread.</summary>
[ServerOnly]
public sealed class PlayerUnbannedEvent : KilnEvent
{
    internal PlayerUnbannedEvent(BanEntry ban, string by)
    {
        Ban = ban;
        By = by;
    }

    /// <summary>The entry that was removed.</summary>
    public BanEntry Ban { get; }

    /// <summary>Who removed it.</summary>
    public string By { get; }
}

/// <summary>A connection named its player for the first time (its account id or Steam id is known), on the server, while
/// the player is still loading in. Cancel it to refuse the connection: the player gets <see cref="Reason"/> (a Kiln
/// client shows it; a vanilla game shows the game's own login error) and the game's login failure. Posted once per
/// connection, on the tick thread, from Kiln.Network's server tick (about once a second), before Kiln's own ban check;
/// a banned player raises none.</summary>
/// <remarks>Timing: a vanilla client is held at the gate while it proves nothing, so it never reaches the world. A Kiln
/// client is not held and can be in the world for a second or two before the refusal arrives.</remarks>
[ServerOnly]
public sealed class PlayerLoginEvent : CancelableEvent
{
    internal PlayerLoginEvent(string name, ulong steamId, ulong accountId, EntityId entity, bool hasKiln)
    {
        Name = name;
        SteamId = steamId;
        AccountId = accountId;
        Entity = entity;
        HasKiln = hasKiln;
    }

    /// <summary>The player's name ("" when the game has not named it yet).</summary>
    public string Name { get; }

    /// <summary>SteamID64 when Kiln.Players knows it, else 0.</summary>
    public ulong SteamId { get; }

    /// <summary>The game's account id from the player record, 0 when unknown.</summary>
    public ulong AccountId { get; }

    /// <summary>The player's entity, <see cref="EntityId.None"/> before the game made it.</summary>
    public EntityId Entity { get; }

    /// <summary>The connection proved Kiln (a Kiln client) by now.</summary>
    public bool HasKiln { get; }

    /// <summary>What a refused player is told. Default: "You may not join this server."</summary>
    public string Reason { get; set; } = "You may not join this server.";
}

public static partial class Players
{
    /// <summary>Kicks a player who is in game: Kiln sends a Kiln client the reason (its overlay shows it), then the game's
    /// login failure goes out (1.5 s later for a Kiln client, at once for a vanilla one). The task completes when the
    /// login failure went out; <see cref="PlayerKickedEvent"/> is posted then. A kick is not a ban: the player can join
    /// again (<see cref="Bans"/>). Tick thread, server.</summary>
    /// <param name="player">The player (<c>(PlayerRef)player</c> from a <see cref="Player"/>).</param>
    /// <param name="reason">What the player is told, at most 200 characters.</param>
    /// <exception cref="ArgumentException"><paramref name="player"/> is none, or the reason is empty or too long.</exception>
    /// <exception cref="KilnUnavailableException"><c>players.kick</c> is off: Kiln.Network is not running, or the game's
    /// kick call did not resolve on this build, or this process runs no server world.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(PlayerAdmin.KickCapability)]
    public static Task<KickResult> Kick(PlayerRef player, string reason)
    {
        Game.AssertTickThread();
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        PlayerAdmin.CheckReason(reason);
        var kicker = PlayerAdmin.RequireKicker();
        var live = PlayerAdmin.Live(player);
        if (live is null)
            return Task.FromResult(new KickResult(player, "", false, "the player is not in game"));
        var by = ModContext.CurrentMod?.Info.Id;
        return kicker(live, (PlayerRef)live, reason, by, false);
    }
}

/// <summary>Kiln's ban list: players refused when they connect, by Steam id or game account id. Kept with the server's
/// Kiln data (<c>kiln/data/kiln-players/bans.json</c>), for every world the server runs, apart from the game's own ban
/// list. A banned player who is in game is kicked within about a second (Kiln.Network's server tick). Admin commands:
/// <c>/ban</c>, <c>/unban</c>, <c>/bans</c>, <c>/kick</c>. Tick thread, server.</summary>
/// <remarks>Capability <c>players.bans</c>: the list is enforced only where Kiln.Network runs and can kick.</remarks>
public static class Bans
{
    /// <summary>Bans a player (online or not). An earlier entry for the same Steam id or account is replaced. Posts
    /// <see cref="PlayerBannedEvent"/>.</summary>
    /// <param name="player">The player; its Steam id must be known (an online player's account id is taken too).</param>
    /// <param name="reason">What the player is told, at most 200 characters.</param>
    /// <param name="duration">How long; null until unbanned.</param>
    /// <exception cref="ArgumentException">No Steam id, an empty or too long reason, or a duration that is not positive.</exception>
    /// <exception cref="KilnUnavailableException"><c>players.bans</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(PlayerAdmin.BansCapability)]
    public static BanEntry Add(PlayerRef player, string reason, TimeSpan? duration = null)
    {
        Game.AssertTickThread();
        if (player.SteamId == 0) throw new ArgumentException("the player's Steam id is not known", nameof(player));
        var live = PlayerAdmin.LiveQuiet(player);
        return PlayerAdmin.AddBan(player.SteamId, live?.AccountId ?? 0, live?.Name ?? "", reason, duration);
    }

    /// <summary>Bans a Steam id (a player who is not here). As <see cref="Add(PlayerRef, string, TimeSpan?)"/>.</summary>
    /// <exception cref="ArgumentException">Steam id 0, an empty or too long reason, or a duration that is not positive.</exception>
    /// <exception cref="KilnUnavailableException"><c>players.bans</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(PlayerAdmin.BansCapability)]
    public static BanEntry Add(ulong steamId, string reason, TimeSpan? duration = null)
    {
        Game.AssertTickThread();
        if (steamId == 0) throw new ArgumentException("Steam id 0 names nobody", nameof(steamId));
        return PlayerAdmin.AddBan(steamId, 0, "", reason, duration);
    }

    /// <summary>Removes every entry that names this Steam id or account id. False when there was none. Posts
    /// <see cref="PlayerUnbannedEvent"/> for each.</summary>
    /// <exception cref="KilnUnavailableException"><c>players.bans</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(PlayerAdmin.BansCapability)]
    public static bool Remove(ulong steamOrAccountId)
    {
        Game.AssertTickThread();
        PlayerAdmin.RequireBans();
        return PlayerAdmin.RemoveBan(steamOrAccountId, PlayerAdmin.Who());
    }

    /// <summary>The entries that hold now (ended ones are dropped). Any thread.</summary>
    [AnyThread]
    public static IReadOnlyList<BanEntry> All => PlayerAdmin.Store.Active(DateTimeOffset.UtcNow);

    /// <summary>The entry that holds now for this Steam id or account id, or null. Any thread.</summary>
    [AnyThread]
    public static BanEntry? Find(ulong steamOrAccountId) => PlayerAdmin.Store.Find(steamOrAccountId, steamOrAccountId, DateTimeOffset.UtcNow);
}

/// <summary>The plumbing of kick and ban: the seam Kiln.Network fills, the ban store, the capability rows.</summary>
internal static class PlayerAdmin
{
    internal const string KickCapability = "players.kick";
    internal const string BansCapability = "players.bans";
    internal const int MaxReason = 200;

    /// <summary>Kicks a player's connection: (player, its ref, reason, mod id or null, banned) -> the result when the login
    /// failure went out. Set by Kiln.Network at its start; null when it does not run.</summary>
    internal static Func<Player, PlayerRef, string, string?, bool, Task<KickResult>>? Kicker;

    /// <summary>Why kicks are off right now (Kiln.Network's answer); null when they work.</summary>
    internal static Func<string?>? KickOff;

    private static readonly object StoreLock = new();
    private static BanStore? _store;

    /// <summary>The one ban list of this process. Any thread may be the first to ask (<see cref="Bans.All"/> is not
    /// tick-bound): two stores would each hold their own copy of the file, and the later writer would erase the
    /// other's bans.</summary>
    internal static BanStore Store
    {
        get
        {
            if (Volatile.Read(ref _store) is { } s) return s;
            lock (StoreLock) return _store ??= new BanStore(StorePath());
        }
    }

    /// <summary>Where the list is kept; tests name another place.</summary>
    internal static Func<string> StorePath = () => Path.Combine(KilnPaths.Root, "data", "kiln-players", "bans.json");

    /// <summary>Tests: another file (null: the next use makes the store anew).</summary>
    internal static void UseStore(BanStore? store)
    {
        lock (StoreLock) _store = store;
    }

    internal static void CheckReason(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a reason is required", nameof(reason));
        if (reason.Length > MaxReason) throw new ArgumentException($"the reason is longer than {MaxReason} characters", nameof(reason));
    }

    internal static Func<Player, PlayerRef, string, string?, bool, Task<KickResult>> RequireKicker()
    {
        var k = Kicker;
        if (k is null) throw new KilnUnavailableException(KickCapability, Text(KickCapability, "Kiln.Network is not running"));
        if (KickOff?.Invoke() is { } why) throw new KilnUnavailableException(KickCapability, why);
        return k;
    }

    internal static void RequireBans()
    {
        if (Kicker is null) throw new KilnUnavailableException(BansCapability, Text(BansCapability, "Kiln.Network is not running: no ban would be enforced"));
        if (KickOff?.Invoke() is { } why) throw new KilnUnavailableException(BansCapability, $"no ban would be enforced: {why}");
    }

    private static string Text(string capability, string fallback) =>
        Capabilities.Get(capability) is { State: CapabilityState.Unavailable, ReasonText.Length: > 0 } row ? row.ReasonText : fallback;

    /// <summary>The player in game now, or null (offline, or kiln.players cannot say).</summary>
    internal static Player? Live(PlayerRef player)
    {
        var now = player.Resolve();
        return now.Entity.IsNone ? null : PlayerIdentity.FindInGame(now.Entity.Id, PlayerIdentity.PlayersCapability);
    }

    internal static Player? LiveQuiet(PlayerRef player)
    {
        try
        {
            return Live(player);
        }
        catch (Exception e) when (e is KilnUnavailableException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string Who() => ModContext.CurrentMod?.Info.Id ?? "console";

    internal static BanEntry AddBan(ulong steamId, ulong accountId, string name, string reason, TimeSpan? duration)
    {
        CheckReason(reason);
        if (duration is { } d && d <= TimeSpan.Zero) throw new ArgumentException("a ban's duration must be positive", nameof(duration));
        RequireBans();
        var now = DateTimeOffset.UtcNow;
        var entry = new BanEntry(steamId, accountId, name, reason, Who(), now, duration is { } t ? now + t : null);
        Store.Put(entry, now);
        if (Events.HasSubscribers<PlayerBannedEvent>()) Events.Post(new PlayerBannedEvent(entry));
        return entry;
    }

    internal static bool RemoveBan(ulong id, string by)
    {
        var gone = Store.Remove(id, DateTimeOffset.UtcNow);
        if (gone.Count > 0 && Events.HasSubscribers<PlayerUnbannedEvent>())
            foreach (var e in gone) Events.Post(new PlayerUnbannedEvent(e, by));
        return gone.Count > 0;
    }
}

/// <summary>The ban list on disk: one JSON file, written whole (temp file, then a move) at each change and read once.</summary>
internal sealed class BanStore(string path)
{
    private readonly object _lock = new();
    private List<BanEntry>? _entries;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    private sealed record File(int Format, List<BanEntry> Bans);

    private List<BanEntry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            if (System.IO.File.Exists(Path))
            {
                var f = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(Path), Json);
                _entries = f?.Bans?.Where(b => b is not null && (b.SteamId != 0 || b.AccountId != 0)).ToList() ?? [];
                return _entries;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // a broken file is kept for the operator to look at; Kiln starts a new list next to it
            Backend.Current.Log(0, LogLevel.Error, $"kiln.players: the ban list {Path} cannot be read ({e.Message}); it is left as it is and no one is banned until it is fixed or removed");
            _entries = [];
            _broken = true;
            return _entries;
        }
        _entries = [];
        return _entries;
    }

    private bool _broken;

    // Writes `entries` as the list. The caller makes them the list in force only when this returned: a write that
    // fails (a full disk, a folder that cannot be written) must leave the bans as they were, in memory and on disk.
    private void Save(List<BanEntry> entries)
    {
        if (_broken) throw new InvalidOperationException($"the ban list {Path} could not be read when Kiln started; fix or remove it first");
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(new File(1, entries), Json));
        System.IO.File.Move(tmp, Path, overwrite: true);
    }

    public IReadOnlyList<BanEntry> Active(DateTimeOffset now)
    {
        lock (_lock) return Load().Where(b => b.IsActive(now)).ToArray();
    }

    public BanEntry? Find(ulong steamId, ulong accountId, DateTimeOffset now)
    {
        if (steamId == 0 && accountId == 0) return null;
        lock (_lock)
            foreach (var b in Load())
                if (b.IsActive(now) && b.Matches(steamId, accountId)) return b;
        return null;
    }

    public void Put(BanEntry entry, DateTimeOffset now)
    {
        lock (_lock)
        {
            var next = Load().Where(b => b.IsActive(now) && !b.Matches(entry.SteamId, entry.AccountId)).ToList();
            next.Add(entry);
            Save(next);
            _entries = next;
        }
    }

    public IReadOnlyList<BanEntry> Remove(ulong id, DateTimeOffset now)
    {
        if (id == 0) return [];
        lock (_lock)
        {
            var list = Load();
            var gone = list.Where(b => b.Matches(id, id)).ToList();
            var next = list.Where(b => b.IsActive(now) && !gone.Contains(b)).ToList();
            if (next.Count == list.Count) return [];
            Save(next);
            _entries = next;
            return gone.Where(b => b.IsActive(now)).ToArray();
        }
    }
}
