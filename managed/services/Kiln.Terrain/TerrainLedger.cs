// SPDX-License-Identifier: MIT
// The per-world ledger of kept overlays: one book per mod in its WorldData (key kiln.terrain.ledger), written with the
// world save like Build.PlaceOnce's records, so cells and record reach the disk together. Public setters: System.Text.Json.
namespace Kiln;

internal sealed class TerrainBook
{
    public const string Key = "kiln.terrain.ledger";
    public Dictionary<string, TerrainRecord> Plans { get; set; } = new();
}

internal sealed class TerrainRecord
{
    public int Version { get; set; }
    public int[] Region { get; set; } = [];
    /// <summary>TerrainPlan.ContentHash of the patch that was applied.</summary>
    public string Content { get; set; } = "";
    /// <summary>False while an apply is under way (or was interrupted): it is resumed by the fingerprints.</summary>
    public bool Complete { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public string KilnVersion { get; set; } = "";
    /// <summary>Blocks of this version, by <c>TerrainPlan.BlockKey</c>.</summary>
    public Dictionary<string, TerrainBlock> Blocks { get; set; } = new();
    /// <summary>During an upgrade: the earlier version's blocks not yet visited, and its region.</summary>
    public Dictionary<string, TerrainBlock>? EarlierBlocks { get; set; }
    public int[]? EarlierRegion { get; set; }

    public CellBox Box => ToBox(Region);
    public static int[] FromBox(CellBox b) => [b.X, b.Y, b.Z, b.SizeX, b.SizeY, b.SizeZ];
    public static CellBox ToBox(int[] r) => r.Length == 6 ? new CellBox(r[0], r[1], r[2], r[3], r[4], r[5]) : default;
}

internal sealed class TerrainBlock
{
    /// <summary>Fingerprint of the block's cells inside the plan's region before the plan wrote them.</summary>
    public string Before { get; set; } = "";
    /// <summary>The same cells after the write.</summary>
    public string After { get; set; } = "";
    /// <summary>The plan gave the block up (someone changed it, or it lies in a base): no version touches it again.</summary>
    public bool GivenUp { get; set; }
    /// <summary>Why it was given up: a <see cref="TerrainConflictKind"/>.</summary>
    public int Why { get; set; }
}

/// <summary>What Kiln.Terrain knows in this process about every mod's kept plans, per world: the blocks each owns. A
/// mod's book is read at its first call, so a plan of a mod that is not loaded is not known here (the contract's limit).</summary>
internal sealed class TerrainSession
{
    // world id -> mod id -> plan id -> block keys
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, HashSet<string>>>> _worlds = new(StringComparer.Ordinal);

    public void Learn(string world, string mod, TerrainBook book)
    {
        if (!_worlds.TryGetValue(world, out var mods)) _worlds[world] = mods = new(StringComparer.Ordinal);
        var plans = mods[mod] = new(StringComparer.Ordinal);
        foreach (var (id, rec) in book.Plans)
        {
            var set = plans[id] = new(StringComparer.Ordinal);
            foreach (var (key, b) in rec.Blocks) if (!b.GivenUp) set.Add(key);
            if (rec.EarlierBlocks is not null)
                foreach (var (key, b) in rec.EarlierBlocks) if (!b.GivenUp) set.Add(key);
        }
    }

    // world|mod|plan of the applies under way
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// <summary>Marks an apply as under way; false when the same plan of the same mod already is.</summary>
    public bool Begin(string world, string mod, string plan) => _running.Add($"{world}|{mod}|{plan}");
    public void End(string world, string mod, string plan) => _running.Remove($"{world}|{mod}|{plan}");

    /// <summary>The blocks a plan is about to write are its own from now on, so another mod's apply that starts while this
    /// one is spread over ticks is refused.</summary>
    public void Claim(string world, string mod, string plan, IEnumerable<string> blockKeys)
    {
        if (!_worlds.TryGetValue(world, out var mods)) _worlds[world] = mods = new(StringComparer.Ordinal);
        if (!mods.TryGetValue(mod, out var plans)) mods[mod] = plans = new(StringComparer.Ordinal);
        if (!plans.TryGetValue(plan, out var set)) plans[plan] = set = new(StringComparer.Ordinal);
        foreach (var k in blockKeys) set.Add(k);
    }

    /// <summary>The "mod/plan" of another mod that owns the block, or null.</summary>
    public string? OwnerOf(string world, string exceptMod, string blockKey)
    {
        if (!_worlds.TryGetValue(world, out var mods)) return null;
        foreach (var (mod, plans) in mods)
        {
            if (mod == exceptMod) continue;
            foreach (var (plan, set) in plans)
                if (set.Contains(blockKey)) return $"{mod}/{plan}";
        }
        return null;
    }
}
