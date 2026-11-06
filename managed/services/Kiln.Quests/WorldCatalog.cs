// SPDX-License-Identifier: MIT
// The lists world progress serves, derived from the game's own data by fixed rules (the same rules as the game-data
// catalog's sections, so ids match: docs/re/world-status.md "Ids"). Pure: the inputs are the readers' output
// (GameDataReader.cs), the scope of each knowledge id comes from a function, so tests feed synthetic data.
using System.Text.RegularExpressions;

namespace Kiln;

internal sealed class WorldCatalog
{
    public IReadOnlyList<Spire> Spires { get; init; } = [];
    public IReadOnlyList<FlameShrine> Shrines { get; init; } = [];
    public IReadOnlyList<ShroudRoot> Roots { get; init; } = [];
    public IReadOnlyList<Obelisk> Obelisks { get; init; } = [];
    public IReadOnlyList<Boss> Bosses { get; init; } = [];
    /// <summary>Named queries by id (for quest names).</summary>
    public IReadOnlyDictionary<uint, string> QueryNames { get; init; } = new Dictionary<uint, string>();
}

internal static partial class WorldCatalogBuilder
{
    [GeneratedRegex(@"^achievement_all_FlameTemplesAndShrines_found_(\d+)_([A-Za-z]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShrineList();

    [GeneratedRegex(@"^achievement_all_shroudRoots_destroyed_(\d+)_([A-Za-z]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RootList();

    [GeneratedRegex(@"^achievement_all_Obelisks_read_(\d+)_([A-Za-z]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ObeliskList();

    [GeneratedRegex(@"^MapMarker_Tower_(.+)_FastTravel(.*)$")]
    private static partial Regex SpireMarker();

    public const string BeaconTemplate = "Prop_Tower_Beacon";
    public const double BeaconRadius = 64;

    /// <summary>One entry per Knowledge action of the game's per-region achievement queries.</summary>
    internal static List<(string Id, string Region, QueryAction Action)> Listed(IEnumerable<QueryRecord> queries, Regex list)
    {
        var outList = new List<(string, string, QueryAction)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in queries.Where(q => list.IsMatch(q.Name)).OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase))
        {
            var region = list.Match(q.Name).Groups[2].Value.ToLowerInvariant();
            foreach (var a in q.Actions)
            {
                if (!a.IsSimple || a.Query.Id == 0) continue;
                var n = a.Name.ToLowerInvariant();
                var id = n.StartsWith(region + "_", StringComparison.Ordinal) ? n : $"{region}_{n}";
                outList.Add((Unique(seen, id), region, a));
            }
        }
        return outList;
    }

    private static string Unique(Dictionary<string, int> seen, string id)
    {
        var k = seen.TryGetValue(id, out var c) ? c + 1 : 1;
        seen[id] = k;
        return k == 1 ? id : $"{id}_{k}";
    }

    internal static ProgressFact Fact(QueryData q, Func<uint, ProgressScope> scopeOf) =>
        new(q.ToQuery(), q.Type == 0 ? ProgressScope.World : scopeOf(q.Id));

    internal static ProgressFact Fact(uint id, Func<uint, ProgressScope> scopeOf) => new(KnowledgeQuery.IsSet(id), scopeOf(id));

    /// <summary>Spires, shrines, roots and obelisks: the query db and the scene's global chunk (with its templates).</summary>
    public static WorldCatalog Places(IReadOnlyList<QueryRecord> queries, IReadOnlyList<ScenePlacement> global,
        Func<Guid, TemplateData?> template, Func<uint, ProgressScope> scopeOf, Func<uint, int> skillPoints, ICollection<string> problems)
    {
        // where a knowledge id is set by a placed trigger (first placement wins)
        var setBy = new Dictionary<uint, ScenePlacement>();
        foreach (var p in global)
            foreach (var op in p.Triggers)
                setBy.TryAdd(op.Id, p);

        var shrines = new List<FlameShrine>();
        foreach (var (id, region, a) in Listed(queries, ShrineList()))
        {
            setBy.TryGetValue(a.Query.Id, out var at);
            var t = at is null ? null : template(at.Template);
            var kind = a.Name.Contains("flametemple", StringComparison.OrdinalIgnoreCase) ? ShrineKind.Sanctum : ShrineKind.Shrine;
            shrines.Add(new FlameShrine(id, kind, region, t?.Name, t?.MarkerTag ?? 0, at?.Position, Fact(a.Query, scopeOf)));
        }

        var roots = new List<ShroudRoot>();
        foreach (var (id, region, a) in Listed(queries, RootList()))
        {
            var well = a.Name.StartsWith("well", StringComparison.OrdinalIgnoreCase);
            setBy.TryGetValue(a.Query.Id, out var at);
            roots.Add(new ShroudRoot(id, region, well, skillPoints(a.Query.Id), at?.Position, Fact(a.Query, scopeOf)));
        }

        var obelisks = new List<Obelisk>();
        foreach (var (id, region, a) in Listed(queries, ObeliskList()))
        {
            setBy.TryGetValue(a.Query.Id, out var at);
            obelisks.Add(new Obelisk(id, region, at?.Position, Fact(a.Query, scopeOf)));
        }

        // spires: the fast-travel markers of the global chunk
        var topReached = new HashSet<uint>();
        foreach (var q in queries.Where(q => q.Name.StartsWith("Achievement_Tower_", StringComparison.OrdinalIgnoreCase)))
            foreach (var a in q.Actions)
                if (a.IsSimple && a.Name.Contains("topReached", StringComparison.OrdinalIgnoreCase))
                    topReached.Add(a.Query.Id);
        var beacons = global.Where(p => template(p.Template)?.Name == BeaconTemplate && p.Query is not null).ToList();
        var spires = new List<Spire>();
        var spireIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in global)
        {
            var t = template(p.Template);
            if (t is null) continue;
            var m = SpireMarker().Match(t.Name);
            if (!m.Success) continue;
            if (p.Query is not { } unlock)
            {
                problems.Add($"spire marker {t.Name} at {p.Position} has no unlock query");
                continue;
            }
            var id = Unique(spireIds, (m.Groups[1].Value + m.Groups[2].Value).ToLowerInvariant());
            var top = p.Triggers.FirstOrDefault(o => o.IsBool && topReached.Contains(o.Id));
            var beacon = beacons.Select(b => (b, d: Horizontal(b.Position, p.Position))).Where(x => x.d <= BeaconRadius)
                .OrderBy(x => x.d).Select(x => x.b).FirstOrDefault();
            spires.Add(new Spire(id, t.Name, t.MarkerTag, p.Position, Fact(unlock, scopeOf),
                top.Id != 0 ? Fact(top.Id, scopeOf) : null, beacon?.Query is { } bq ? Fact(bq, scopeOf) : null));
        }
        spires.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

        var names = new Dictionary<uint, string>();
        foreach (var q in queries)
            if (q.Id != 0 && q.Name.Length > 0) names.TryAdd(q.Id, q.Name);

        return new WorldCatalog { Spires = spires, Shrines = shrines, Roots = roots, Obelisks = obelisks, QueryNames = names };
    }

    /// <summary>Templates worth a look in every chunk: bosses (health bar) and spawners that place one.</summary>
    public static Dictionary<Guid, (TemplateData Placed, TemplateData Boss)> BossPlacers(IEnumerable<TemplateData> templates)
    {
        var all = templates.ToDictionary(t => t.Guid);
        var outMap = new Dictionary<Guid, (TemplateData, TemplateData)>();
        foreach (var t in all.Values)
        {
            if (t.IsBoss)
            {
                outMap[t.Guid] = (t, t);
                continue;
            }
            foreach (var g in t.Spawns)
                if (all.TryGetValue(g, out var target) && target.IsBoss)
                {
                    outMap[t.Guid] = (t, target);
                    break;
                }
        }
        return outMap;
    }

    /// <summary>The bosses of the world, from the placements of <see cref="BossPlacers"/>' templates (every chunk, in chunk
    /// and entity order).</summary>
    public static List<Boss> Bosses(IEnumerable<ScenePlacement> placements, IReadOnlyDictionary<Guid, (TemplateData Placed, TemplateData Boss)> placers,
        Func<uint, ProgressScope> scopeOf)
    {
        var rows = placements.Where(p => placers.ContainsKey(p.Template)).OrderBy(p => p.Chunk).ThenBy(p => p.Index).ToList();
        var counts = rows.GroupBy(p => placers[p.Template].Boss.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<Boss>(rows.Count);
        foreach (var p in rows)
        {
            var (placed, boss) = placers[p.Template];
            var baseId = boss.Name.ToLowerInvariant();
            var n = seen.TryGetValue(baseId, out var c) ? c + 1 : 1;
            seen[baseId] = n;
            var id = counts[baseId] > 1 ? $"{baseId}_{n}" : baseId;
            ProgressFact? kill = null;
            // the spawner's kill triggers (copied onto the boss it spawns): a world record first, then a Kill trigger, then the first
            var candidates = p.Triggers.Where(o => o.IsBool && o.Context is TriggerOp.Kill or TriggerOp.Invalid).ToList();
            var op = candidates.FirstOrDefault(o => scopeOf(o.Id) != ProgressScope.Character);
            if (op.Id == 0) op = candidates.FirstOrDefault(o => o.Context == TriggerOp.Kill);
            if (op.Id == 0) op = candidates.FirstOrDefault();
            if (op.Id != 0) kill = Fact(op.Id, scopeOf);
            else if (boss.KillOp is { } k && !boss.KillUsesScene && k.IsBool) kill = Fact(k.Id, scopeOf);
            list.Add(new Boss(id, boss.Name, ReferenceEquals(placed, boss) ? null : placed.Name, boss.BossBarTag, p.Position, kill));
        }
        return list;
    }

    /// <summary>Roots with the position of the placement whose trigger destroys them (the roots are in the regular chunks).</summary>
    public static List<ShroudRoot> WithPositions(IReadOnlyList<ShroudRoot> roots, IEnumerable<ScenePlacement> placements)
    {
        var at = new Dictionary<uint, WorldPosition>();
        foreach (var p in placements)
            foreach (var op in p.Triggers)
                at.TryAdd(op.Id, p.Position);
        return [.. roots.Select(r => r.Position is not null || !at.TryGetValue(r.Destruction.Id, out var pos) ? r : r with { Position = pos })];
    }

    internal static double Horizontal(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>A readable name for a vanilla journal quest: the named query of its last step's completion, else of its
    /// first step's visibility; null when neither is a named query or the name is shared with another quest.</summary>
    public static Dictionary<uint, string> QuestNames(IEnumerable<(uint EntryId, KnowledgeQuery FirstVisible, KnowledgeQuery LastDone)> quests,
        IReadOnlyDictionary<uint, string> queryNames)
    {
        var picked = new List<(uint, string)>();
        foreach (var (entry, first, last) in quests)
        {
            string? n = null;
            foreach (var q in new[] { last, first })
                if (q.Type == KnowledgeQueryType.Extern && q.Id != 0 && queryNames.TryGetValue(q.Id, out var s) && s.Length > 0)
                {
                    n = s;
                    break;
                }
            if (n is not null) picked.Add((entry, n));
        }
        var dup = picked.GroupBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return picked.Where(x => !dup.Contains(x.Item2)).ToDictionary(x => x.Item1, x => x.Item2);
    }
}
