// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One grant or deny: <see cref="Pattern"/> is a node, "prefix.*" or "*".</summary>
internal readonly record struct PermissionRule(string Pattern, bool Allow)
{
    public bool Wildcard => Pattern == "*" || Pattern.EndsWith(".*", StringComparison.Ordinal);
    public override string ToString() => (Allow ? "" : "-") + Pattern;

    public static PermissionRule? Parse(string text)
    {
        var t = text.Trim();
        var allow = true;
        if (t.StartsWith('-'))
        {
            allow = false;
            t = t[1..];
        }
        else if (t.StartsWith('+')) t = t[1..];
        var n = PermissionRules.Normalize(t);
        return n is null ? null : new PermissionRule(n, allow);
    }
}

internal sealed class PermissionGroup(string name)
{
    public string Name { get; } = name;
    public bool Admin { get; set; }
    public List<string> Inherits { get; set; } = new();
    public List<PermissionRule> Rules { get; } = new();

    public void SetRule(string pattern, bool? allow)
    {
        Rules.RemoveAll(r => r.Pattern == pattern);
        if (allow is { } a) Rules.Add(new PermissionRule(pattern, a));
    }

    public PermissionGroup Clone()
    {
        var g = new PermissionGroup(Name) { Admin = Admin, Inherits = new List<string>(Inherits) };
        g.Rules.AddRange(Rules);
        return g;
    }
}

internal sealed class PermissionPlayer(ulong steamId)
{
    public ulong SteamId { get; } = steamId;
    public string? Name { get; set; }
    public List<string> Groups { get; } = new();
    public List<PermissionRule> Rules { get; } = new();

    public void SetRule(string pattern, bool? allow)
    {
        Rules.RemoveAll(r => r.Pattern == pattern);
        if (allow is { } a) Rules.Add(new PermissionRule(pattern, a));
    }

    public PermissionPlayer Clone()
    {
        var p = new PermissionPlayer(SteamId) { Name = Name };
        p.Groups.AddRange(Groups);
        p.Rules.AddRange(Rules);
        return p;
    }
}

/// <summary>A user group of enshrouded_server.json: name and rights bits (KickBan 1, AccessInventories 2, EditBase 4,
/// EditWorld 8, ExtendBase 16).</summary>
internal sealed record ServerUserGroup(string Name, uint Bits);

/// <summary>permissions.json in memory. Never changed after it is published (edits work on a clone).</summary>
internal sealed class PermissionModel
{
    public bool KickBanIsAdmin { get; set; } = true;
    public string DefaultGroup { get; set; } = "default";
    public Dictionary<string, PermissionGroup> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> UserGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<uint, string> UserGroupIds { get; } = new();
    public Dictionary<ulong, PermissionPlayer> Players { get; } = new();
    public IReadOnlyList<ServerUserGroup> ServerGroups { get; set; } = [];

    public static PermissionModel Default()
    {
        var m = new PermissionModel();
        m.Groups["default"] = new PermissionGroup("default");
        var admin = new PermissionGroup("admin") { Admin = true };
        admin.Rules.Add(new PermissionRule("*", true));
        m.Groups["admin"] = admin;
        m.UserGroups["Admin"] = ["admin"];
        return m;
    }

    public PermissionModel Clone()
    {
        var m = new PermissionModel { KickBanIsAdmin = KickBanIsAdmin, DefaultGroup = DefaultGroup, ServerGroups = ServerGroups };
        foreach (var (k, g) in Groups) m.Groups[k] = g.Clone();
        foreach (var (k, v) in UserGroups) m.UserGroups[k] = new List<string>(v);
        foreach (var (k, v) in UserGroupIds) m.UserGroupIds[k] = v;
        foreach (var (k, p) in Players) m.Players[k] = p.Clone();
        return m;
    }

    public PermissionGroup GroupOf(string name)
    {
        if (PermissionRules.Normalize(name) is null || name.Contains('.') || name.Contains('*'))
            throw new ArgumentException($"invalid group name '{name}'");
        if (!Groups.TryGetValue(name, out var g)) Groups[name] = g = new PermissionGroup(name);
        return g;
    }

    public PermissionPlayer PlayerOf(ulong steamId, string? name)
    {
        if (steamId == 0) throw new ArgumentException("SteamID64 0");
        if (!Players.TryGetValue(steamId, out var p)) Players[steamId] = p = new PermissionPlayer(steamId);
        if (!string.IsNullOrEmpty(name)) p.Name = name;
        return p;
    }

    public JsonObject ToJson()
    {
        var groups = new JsonObject();
        foreach (var g in Groups.Values.OrderBy(g => g.Name, StringComparer.Ordinal))
            groups[g.Name] = new JsonObject
            {
                ["admin"] = g.Admin,
                ["inherits"] = new JsonArray([.. g.Inherits.Select(i => (JsonNode)i)]),
                ["nodes"] = new JsonArray([.. g.Rules.Select(r => (JsonNode)r.ToString())]),
            };
        var userGroups = new JsonObject();
        foreach (var (k, v) in UserGroups.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            userGroups[k] = new JsonArray([.. v.Select(x => (JsonNode)x)]);
        var ids = new JsonObject();
        foreach (var (k, v) in UserGroupIds.OrderBy(kv => kv.Key)) ids[k.ToString()] = v;
        var players = new JsonObject();
        foreach (var p in Players.Values.OrderBy(p => p.SteamId))
        {
            var o = new JsonObject();
            if (p.Name is { Length: > 0 }) o["name"] = p.Name;
            o["groups"] = new JsonArray([.. p.Groups.Select(x => (JsonNode)x)]);
            o["nodes"] = new JsonArray([.. p.Rules.Select(r => (JsonNode)r.ToString())]);
            players[p.SteamId.ToString()] = o;
        }
        return new JsonObject
        {
            ["kickBanIsAdmin"] = KickBanIsAdmin,
            ["defaultGroup"] = DefaultGroup,
            ["groups"] = groups,
            ["userGroups"] = userGroups,
            ["userGroupIds"] = ids,
            ["players"] = players,
            ["serverUserGroups"] = new JsonArray([.. ServerGroups.Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Name, ["rights"] = ((PlayerPermissions)g.Bits).ToString(),
            })]),
        };
    }
}

/// <summary>The decision procedure (pure: model + facts in, decision out).</summary>
internal static class PermissionRules
{
    private const uint RightsMask = 0x1F;  // ReceiveExp (0x20) is ORed into every group by the game

    /// <summary>Lower-case node or pattern; null when invalid. Segments of [a-z0-9_-:], "*" only as the last one.</summary>
    public static string? Normalize(string? node)
    {
        if (string.IsNullOrWhiteSpace(node)) return null;
        var n = node.Trim().ToLowerInvariant();
        if (n.Length > 128) return null;
        if (n == "*") return n;
        var parts = n.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p == "*" && i == parts.Length - 1 && i > 0) continue;
            if (p.Length == 0 || !p.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' or ':')) return null;
        }
        return n;
    }

    /// <summary>How specific <paramref name="pattern"/> is for <paramref name="node"/>: -1 no match, 0 for "*",
    /// the prefix's segment count for "prefix.*", 1000 for the node itself.</summary>
    public static int Match(string pattern, string node)
    {
        if (pattern == node) return 1000;
        if (pattern == "*") return 0;
        if (!pattern.EndsWith(".*", StringComparison.Ordinal)) return -1;
        var prefix = pattern[..^1];  // keep the dot
        return node.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Count(c => c == '.') : -1;
    }

    /// <summary>The most specific matching rule (deny wins a tie); wildcards are ignored for console-level nodes.</summary>
    public static PermissionRule? Best(IEnumerable<PermissionRule> rules, string node, PermissionLevel level)
    {
        PermissionRule? best = null;
        var bestScore = -1;
        foreach (var r in rules)
        {
            if (level == PermissionLevel.Console && r.Wildcard) continue;
            var s = Match(r.Pattern, node);
            if (s < 0) continue;
            if (s > bestScore || s == bestScore && best is { Allow: true } && !r.Allow)
            {
                best = r;
                bestScore = s;
            }
        }
        return best;
    }

    public static PermissionDecision Decide(PermissionModel m, PlayerFacts f, string node, PermissionLevel level)
    {
        switch (f.Kind)
        {
            case PermissionSubjectKind.Console:
                return new PermissionDecision(true, node, level, null, "console", []);
            case PermissionSubjectKind.Remote:
                return new PermissionDecision(level == PermissionLevel.Everyone || f.DeclaredAdmin, node, level, null, "remote", []);
        }
        var groups = Groups(m, f);
        if (f.SteamId != 0 && m.Players.TryGetValue(f.SteamId, out var p) && Best(p.Rules, node, level) is { } own)
            return new PermissionDecision(own.Allow, node, level, own.ToString(), "player", groups);
        PermissionRule? best = null;
        string? from = null;
        var bestScore = -1;
        foreach (var name in groups)
        {
            if (!m.Groups.TryGetValue(name, out var g)) continue;
            if (Best(g.Rules, node, level) is not { } r) continue;
            var s = Match(r.Pattern, node);
            if (s > bestScore || s == bestScore && best is { Allow: true } && !r.Allow)
            {
                best = r;
                bestScore = s;
                from = "group:" + g.Name;
            }
        }
        if (best is { } b) return new PermissionDecision(b.Allow, node, level, b.ToString(), from!, groups);
        var allowed = level switch
        {
            PermissionLevel.Everyone => true,
            PermissionLevel.Admin => IsAdmin(m, f, groups),
            _ => false,
        };
        return new PermissionDecision(allowed, node, level, null, "default:" + level.ToString().ToLowerInvariant(), groups);
    }

    public static bool IsAdmin(PermissionModel m, PlayerFacts f, IReadOnlyList<string> groups)
    {
        if (f.Kind == PermissionSubjectKind.Console) return true;
        if (f.Kind == PermissionSubjectKind.Remote) return f.DeclaredAdmin;
        foreach (var g in groups)
            if (m.Groups.TryGetValue(g, out var grp) && grp.Admin)
                return true;
        if (f.AdminOverride is { } o) return o;
        return m.KickBanIsAdmin && f.Rights is { } r && (r & PlayerPermissions.KickBan) != 0;
    }

    /// <summary>The server user group of a player: from userGroupIds, else the only user group with the same rights.</summary>
    public static string? UserGroup(PermissionModel m, PlayerFacts f)
    {
        if (f.UserGroupId is { } id && m.UserGroupIds.TryGetValue(id, out var byId)) return byId;
        if (f.Rights is not { } rights) return null;
        var bits = (uint)rights & RightsMask;
        string? found = null;
        foreach (var g in m.ServerGroups)
        {
            if ((g.Bits & RightsMask) != bits) continue;
            if (found is not null) return null;  // two groups with the same rights: ambiguous
            found = g.Name;
        }
        return found;
    }

    /// <summary>A player's Kiln groups: their own, those of their user group, the default group, then inherited ones
    /// (breadth first, each once).</summary>
    public static IReadOnlyList<string> Groups(PermissionModel m, PlayerFacts f)
    {
        var start = new List<string>();
        if (f.SteamId != 0 && m.Players.TryGetValue(f.SteamId, out var p)) start.AddRange(p.Groups);
        if (UserGroup(m, f) is { } ug && m.UserGroups.TryGetValue(ug, out var mapped)) start.AddRange(mapped);
        start.Add(m.DefaultGroup);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(start);
        while (queue.Count > 0 && result.Count < 64)
        {
            var g = queue.Dequeue();
            if (!seen.Add(g)) continue;
            if (!m.Groups.TryGetValue(g, out var grp)) continue;
            result.Add(grp.Name);
            foreach (var i in grp.Inherits) queue.Enqueue(i);
        }
        return result;
    }
}
