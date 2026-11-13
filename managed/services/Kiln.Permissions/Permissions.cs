// SPDX-License-Identifier: MIT
// Kiln.Permissions: permission nodes with a default level, groups (also mapped from the server's user groups) and
// per-player overrides in kiln/config/permissions.json (Forge's PermissionAPI). Usage: docs/kiln/permissions.md.
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Who has a node when no rule in permissions.json mentions it (Forge's <c>DefaultPermissionLevel</c>).</summary>
public enum PermissionLevel
{
    /// <summary>Every player, the console and remote tools.</summary>
    Everyone = 0,
    /// <summary>Admins: a player in a group with <c>"admin": true</c> or (default) whose user group may kick and ban;
    /// the console; admin remote tools.</summary>
    Admin = 1,
    /// <summary>Only the console and admin remote tools. A player needs a rule naming the node exactly: wildcards never
    /// grant it.</summary>
    Console = 2,
}

public enum PermissionSubjectKind
{
    Console = 0,
    Player = 1,
    Remote = 2,
}

/// <summary>Whose permission is asked. Build it with <see cref="Console"/>, <see cref="ForPlayer(Player)"/>,
/// <see cref="ForPlayer(EntityId)"/>, <see cref="ForSteamId"/> or <see cref="Remote"/> (a <c>CommandSource</c>
/// converts itself).</summary>
public readonly record struct PermissionSubject
{
    public PermissionSubjectKind Kind { get; init; }
    public string Name { get; init; }
    /// <summary>The player entity (online players); none for offline players and non-players.</summary>
    public EntityId Entity { get; init; }
    /// <summary>SteamID64; 0 = look it up from <see cref="Entity"/>.</summary>
    public ulong SteamId { get; init; }
    /// <summary>Remote tools: whether they act as admin.</summary>
    public bool DeclaredAdmin { get; init; }
    /// <summary>Whether the player counts as admin for <see cref="PermissionLevel.Admin"/> nodes before groups are
    /// looked at (Kiln.Chat passes its admin check here); null = from the user group's rights.</summary>
    public bool? AdminOverride { get; init; }
    /// <summary>The user group's rights when known (else looked up from the online player).</summary>
    public PlayerPermissions? GameRights { get; init; }
    /// <summary>The game's user-group id when known.</summary>
    public uint? UserGroupId { get; init; }

    public static PermissionSubject Console => new() { Kind = PermissionSubjectKind.Console, Name = "console", DeclaredAdmin = true };

    public static PermissionSubject Remote(string name, bool admin = true) =>
        new() { Kind = PermissionSubjectKind.Remote, Name = name, DeclaredAdmin = admin };

    public static PermissionSubject ForPlayer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return new()
        {
            Kind = PermissionSubjectKind.Player, Name = player.Name, Entity = player.Entity, SteamId = player.SteamId,
            GameRights = player.PermissionsKnown ? player.Permissions : null,
            UserGroupId = player.PermissionsKnown ? player.UserGroupId : null,
        };
    }

    /// <summary>An online player by entity (looked up through Kiln.Players when asked).</summary>
    public static PermissionSubject ForPlayer(EntityId entity) =>
        new() { Kind = PermissionSubjectKind.Player, Name = $"player {entity}", Entity = entity };

    /// <summary>A person by <see cref="PlayerRef"/>, online or not. Asked by Steam id, so a ref of an earlier session
    /// never borrows the rights of whoever has that entity slot now. A ref without a Steam id counts as its session
    /// entity only while that session lasts (<see cref="PlayerRef.IsOnline"/>, tick thread); after it the subject is a
    /// player nobody knows, who has what every player has by default and nothing more. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentException"><paramref name="player"/> is <see cref="PlayerRef.None"/>.</exception>
    /// <exception cref="KilnUnavailableException">The ref has no Steam id and no service can say whether its session
    /// lasts (<c>identity.entities</c>).</exception>
    public static PermissionSubject ForPlayer(PlayerRef player)
    {
        if (player.IsNone) throw new ArgumentException("PlayerRef.None names nobody", nameof(player));
        if (player.SteamId != 0) return ForSteamId(player.SteamId);
        return player.IsOnline
            ? ForPlayer(player.Entity.Id)
            : new() { Kind = PermissionSubjectKind.Player, Name = "player (left)" };
    }

    /// <summary>A player by SteamID64, online or not (the dashboard, tests). Without <paramref name="gameRights"/>
    /// the rights of the online player with that id are used, if any.</summary>
    public static PermissionSubject ForSteamId(ulong steamId, string? name = null, PlayerPermissions? gameRights = null,
        uint? userGroupId = null) =>
        new()
        {
            Kind = PermissionSubjectKind.Player, Name = name ?? steamId.ToString(), SteamId = steamId, GameRights = gameRights,
            UserGroupId = userGroupId,
        };

    public override string ToString() => Kind == PermissionSubjectKind.Player
        ? $"{Name}{(SteamId != 0 ? $" (steam {SteamId})" : Entity.IsNone ? "" : $" (entity {Entity})")}"
        : $"{Kind.ToString().ToLowerInvariant()} {Name}";
}

/// <summary>A registered node.</summary>
public sealed record PermissionNode(string Node, PermissionLevel DefaultLevel, string Description, string Owner);

/// <summary>The answer of <see cref="Permissions.Explain"/>: allowed or not, and which rule decided. <c>Rule</c> is the
/// rule that matched ("kiln.*", "-mymod.fly"), null when the default level decided; <c>From</c> is "console",
/// "remote", "player", "group:&lt;name&gt;" or "default:&lt;level&gt;"; <c>Groups</c> the player's Kiln groups.</summary>
public sealed record PermissionDecision(bool Allowed, string Node, PermissionLevel Level, string? Rule, string From,
    IReadOnlyList<string> Groups)
{
    public override string ToString() => $"{(Allowed ? "allow" : "deny")} {Node} ({From}{(Rule is null ? "" : $" rule {Rule}")})";
}

/// <summary>permissions.json was reloaded or edited, or a server user group changed. Tick thread.</summary>
public sealed class PermissionsChangedEvent : KilnEvent
{
    internal PermissionsChangedEvent(string reason) => Reason = reason;

    /// <summary>"file", "api" or "userGroups".</summary>
    public string Reason { get; }
}

/// <summary>
/// Permission nodes (Forge's PermissionAPI). Mods register nodes with a default level and ask
/// <see cref="Has(PermissionSubject, string)"/>; server owners grant and deny in <c>kiln/config/permissions.json</c>
/// (groups, the server's user groups mapped to groups, per-player rules by SteamID64), reloaded while the server
/// runs. Commands use it: every command has a node (<c>&lt;mod&gt;.command.&lt;name&gt;</c>).
/// </summary>
/// <remarks>Reads (<see cref="Has(PermissionSubject, string)"/>, <see cref="Explain"/>) work from any thread; edits
/// run on the tick thread.</remarks>
public static class Permissions
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (PermissionNode Node, ModContext? Owner)> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedUnknown = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    /// <summary>Tests replace how online players are found.</summary>
    internal static Func<EntityId, Player?> FindPlayer = e => Safe(() => Kiln.Players.Find(e));
    internal static Func<ulong, Player?> FindPlayerBySteamId = id => Safe(() => Kiln.Players.FindBySteamId(id));

    static Permissions()
    {
        PermissionStore.Start();
    }

    /// <summary>Full path of permissions.json.</summary>
    public static string FilePath => PermissionStore.FilePath;

    /// <summary>
    /// Registers <paramref name="node"/> ("mymod.fly", "mymod.command.home") with the level that has it when
    /// permissions.json says nothing. Owned by the calling mod (gone when it unloads). Registering a node again
    /// (same owner) replaces it. Any thread.
    /// </summary>
    [AnyThread]
    public static PermissionNode Register(string node, PermissionLevel defaultLevel = PermissionLevel.Admin, string? description = null)
    {
        var n = PermissionRules.Normalize(node) ?? throw new ArgumentException($"invalid permission node '{node}' (letters, digits, _ - separated by dots)", nameof(node));
        if (n.EndsWith(".*") || n == "*") throw new ArgumentException("register a node, not a wildcard", nameof(node));
        var mod = ModContext.CurrentMod;
        var info = new PermissionNode(n, defaultLevel, description ?? "", mod?.Info.Id ?? "kiln");
        lock (Lock)
        {
            if (Registered.TryGetValue(n, out var existing) && existing.Node.Owner != info.Owner &&
                existing.Owner is not { Alive: false } && existing.Node.DefaultLevel != defaultLevel)
                Backend.Current.Log(0, LogLevel.Warn,
                    $"permissions: {info.Owner} registers {n} as {defaultLevel}; {existing.Node.Owner} registered it as {existing.Node.DefaultLevel} (kept)");
            if (!Registered.TryGetValue(n, out existing) || existing.Owner is { Alive: false } || existing.Node.Owner == info.Owner)
                Registered[n] = (info, mod);
            if (mod is not null && Watched.Add(mod))
                mod.Retiring += () =>
                {
                    lock (Lock)
                    {
                        Watched.Remove(mod);
                        foreach (var k in Registered.Where(kv => ReferenceEquals(kv.Value.Owner, mod)).Select(kv => kv.Key).ToList())
                            Registered.Remove(k);
                    }
                };
            return Registered[n].Node;
        }
    }

    /// <summary>Every registered node, sorted.</summary>
    public static IReadOnlyList<PermissionNode> Nodes
    {
        get
        {
            lock (Lock) return Registered.Values.Select(v => v.Node).OrderBy(n => n.Node, StringComparer.Ordinal).ToArray();
        }
    }

    public static PermissionNode? Find(string node)
    {
        var n = PermissionRules.Normalize(node);
        if (n is null) return null;
        lock (Lock) return Registered.TryGetValue(n, out var e) ? e.Node : null;
    }

    /// <summary>Whether <paramref name="subject"/> has <paramref name="node"/>.</summary>
    [AnyThread]
    public static bool Has(PermissionSubject subject, string node) => Explain(subject, node).Allowed;

    /// <summary>Whether an online player has <paramref name="node"/>.</summary>
    public static bool Has(Player player, string node) => Has(PermissionSubject.ForPlayer(player), node);

    /// <summary>Whether the online player with this entity has <paramref name="node"/>.</summary>
    public static bool Has(EntityId player, string node) => Has(PermissionSubject.ForPlayer(player), node);

    /// <summary>Whether this person has <paramref name="node"/>, online or not
    /// (<see cref="PermissionSubject.ForPlayer(PlayerRef)"/>: asked by Steam id, so a ref of an earlier session never
    /// answers for the player who has that entity slot now). Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentException"><paramref name="player"/> is <see cref="PlayerRef.None"/>, or the node is invalid.</exception>
    public static bool Has(PlayerRef player, string node) => Has(PermissionSubject.ForPlayer(player), node);

    /// <summary>The decision with the rule that made it (for "/perm check" and the dashboard).</summary>
    [AnyThread]
    public static PermissionDecision Explain(PermissionSubject subject, string node)
    {
        var n = PermissionRules.Normalize(node) ?? throw new ArgumentException($"invalid permission node '{node}'", nameof(node));
        PermissionLevel level;
        lock (Lock)
        {
            if (Registered.TryGetValue(n, out var r)) level = r.Node.DefaultLevel;
            else
            {
                level = PermissionLevel.Admin;
                if (WarnedUnknown.Add(n) && WarnedUnknown.Count < 200)
                    Backend.Current.Log(0, LogLevel.Debug, $"permissions: '{n}' is not registered; treated as an admin node");
            }
        }
        return PermissionRules.Decide(PermissionStore.Model, Facts(subject), n, level);
    }

    /// <summary>Whether the subject counts as admin (for <see cref="PermissionLevel.Admin"/> nodes).</summary>
    public static bool IsAdmin(PermissionSubject subject) => subject.Kind switch
    {
        PermissionSubjectKind.Console => true,
        PermissionSubjectKind.Remote => subject.DeclaredAdmin,
        _ => PermissionRules.IsAdmin(PermissionStore.Model, Facts(subject), PermissionRules.Groups(PermissionStore.Model, Facts(subject))),
    };

    /// <summary>The Kiln groups that apply to the subject (own, from the user group, "default", inherited).</summary>
    public static IReadOnlyList<string> GroupsOf(PermissionSubject subject) =>
        subject.Kind == PermissionSubjectKind.Player ? PermissionRules.Groups(PermissionStore.Model, Facts(subject)) : [];

    /// <summary>The server user group (enshrouded_server.json "userGroups" name) Kiln maps the subject to, or null.</summary>
    public static string? UserGroupOf(PermissionSubject subject) =>
        subject.Kind == PermissionSubjectKind.Player ? PermissionRules.UserGroup(PermissionStore.Model, Facts(subject)) : null;

    /// <summary>The server's user groups as read from enshrouded_server.json (name, rights bits).</summary>
    public static IReadOnlyList<(string Name, PlayerPermissions Rights)> ServerUserGroups =>
        PermissionStore.Model.ServerGroups.Select(g => (g.Name, (PlayerPermissions)g.Bits)).ToArray();

    /// <summary>Reads permissions.json (and the server's user groups) now. Kiln does this by itself on changes.</summary>
    public static void Reload() => PermissionStore.Load("api");

    // ---- editing (tick thread; written to permissions.json) ----

    /// <summary>Grants (true), denies (false) or clears (null) a node for one player.</summary>
    [TickThreadOnly]
    public static void SetPlayerNode(ulong steamId, string node, bool? allow, string? name = null) =>
        PermissionStore.Edit(m => m.PlayerOf(steamId, name).SetRule(Pattern(node), allow), $"player {steamId}: {node} = {Show(allow)}");

    /// <summary>Puts a player into a Kiln group (created when missing).</summary>
    [TickThreadOnly]
    public static void AddPlayerToGroup(ulong steamId, string group, string? name = null) =>
        PermissionStore.Edit(m =>
        {
            m.GroupOf(group);
            var p = m.PlayerOf(steamId, name);
            if (!p.Groups.Contains(group, StringComparer.OrdinalIgnoreCase)) p.Groups.Add(group);
        }, $"player {steamId}: + group {group}");

    [TickThreadOnly]
    public static void RemovePlayerFromGroup(ulong steamId, string group) =>
        PermissionStore.Edit(m =>
        {
            if (m.Players.TryGetValue(steamId, out var p)) p.Groups.RemoveAll(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        }, $"player {steamId}: - group {group}");

    /// <summary>Grants, denies or clears a node for a Kiln group (created when missing).</summary>
    [TickThreadOnly]
    public static void SetGroupNode(string group, string node, bool? allow) =>
        PermissionStore.Edit(m => m.GroupOf(group).SetRule(Pattern(node), allow), $"group {group}: {node} = {Show(allow)}");

    /// <summary>Creates or changes a Kiln group: admin flag and inherited groups (null = unchanged).</summary>
    [TickThreadOnly]
    public static void SetGroup(string group, bool? admin = null, IEnumerable<string>? inherits = null) =>
        PermissionStore.Edit(m =>
        {
            var g = m.GroupOf(group);
            if (admin is { } a) g.Admin = a;
            if (inherits is not null) g.Inherits = inherits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }, $"group {group}");

    /// <summary>Removes a Kiln group (and players' memberships of it).</summary>
    [TickThreadOnly]
    public static void RemoveGroup(string group) =>
        PermissionStore.Edit(m =>
        {
            m.Groups.Remove(group);
            foreach (var p in m.Players.Values) p.Groups.RemoveAll(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
            foreach (var u in m.UserGroups.Values) u.RemoveAll(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        }, $"- group {group}");

    /// <summary>Maps a server user group (enshrouded_server.json name) to Kiln groups (empty: no mapping).</summary>
    [TickThreadOnly]
    public static void MapUserGroup(string userGroup, IEnumerable<string> groups) =>
        PermissionStore.Edit(m =>
        {
            var list = groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) m.UserGroups.Remove(userGroup);
            else m.UserGroups[userGroup] = list;
        }, $"userGroup {userGroup}");

    /// <summary>The whole configuration as JSON (groups, user-group mapping, players, server user groups, nodes).</summary>
    public static JsonObject ToJson()
    {
        var o = PermissionStore.Model.ToJson();
        o["file"] = FilePath;
        o["nodes"] = new JsonArray([.. Nodes.Select(n => (JsonNode)new JsonObject
        {
            ["node"] = n.Node, ["level"] = n.DefaultLevel.ToString().ToLowerInvariant(), ["description"] = n.Description, ["owner"] = n.Owner,
        })]);
        return o;
    }

    // ---- internals ----

    private static string Pattern(string node)
    {
        var n = node.Trim();
        var neg = n.StartsWith('-');
        var p = PermissionRules.Normalize(neg ? n[1..] : n) ?? throw new ArgumentException($"invalid permission node '{node}'", nameof(node));
        return p;
    }

    private static string Show(bool? allow) => allow switch { true => "allow", false => "deny", _ => "cleared" };

    internal static PlayerFacts Facts(PermissionSubject s)
    {
        if (s.Kind != PermissionSubjectKind.Player)
            return new PlayerFacts(s.Kind, s.Name, s.Entity, 0, null, null, s.DeclaredAdmin, s.AdminOverride);
        var steam = s.SteamId;
        var rights = s.GameRights;
        var group = s.UserGroupId;
        var name = s.Name;
        Player? online = null;
        if (!s.Entity.IsNone) online = FindPlayer(s.Entity);
        else if (steam != 0 && (rights is null || group is null)) online = FindPlayerBySteamId(steam);
        if (online is not null)
        {
            if (steam == 0) steam = online.SteamId;
            if (online.PermissionsKnown)
            {
                rights ??= online.Permissions;
                group ??= online.UserGroupId;
            }
            if (online.Name.Length > 0) name = online.Name;
        }
        return new PlayerFacts(s.Kind, name, s.Entity, steam, rights, group, false, s.AdminOverride);
    }

    private static Player? Safe(Func<Player?> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Test hook.</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Registered.Clear();
            WarnedUnknown.Clear();
            Watched.Clear();
        }
    }
}

/// <summary>What the rules look at for one subject.</summary>
internal readonly record struct PlayerFacts(PermissionSubjectKind Kind, string Name, EntityId Entity, ulong SteamId,
    PlayerPermissions? Rights, uint? UserGroupId, bool DeclaredAdmin, bool? AdminOverride);
