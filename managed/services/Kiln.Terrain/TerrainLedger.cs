// SPDX-License-Identifier: MIT
// The per-world ledger of kept overlays: one book per mod in its WorldData (key kiln.terrain.ledger), written with the
// world save like Build.PlaceOnce's records. Mismatched save generations must be refused. Public setters: System.Text.Json.
namespace Kiln;

internal sealed class TerrainBook
{
    public const string Key = "kiln.terrain.ledger";
    [System.Text.Json.Serialization.JsonRequired]
    public Dictionary<string, TerrainRecord> Plans { get; set; } = new();

    // WorldData is editable JSON, not a trusted object graph. Do not repair it into an empty/fresh ledger.
    public void Validate((int X, int Z)? grid)
    {
        if (Plans is null || Plans.Count > TerrainPlan.MaxPlans) Bad();
        foreach (var (id, rec) in Plans!)
        {
            if (!TerrainPlan.ValidId(id) || rec is null || rec.Version < 1 || !Hash(rec.Content)) Bad();
            var box = Box(rec!.Region, grid);
            Blocks(rec.Blocks, box);
            if (rec.EarlierBlocks is not null)
            {
                if (rec.Complete || rec.EarlierRegion is null) Bad();
                Blocks(rec.EarlierBlocks, Box(rec.EarlierRegion!, grid));
            }
            else if (rec.EarlierRegion is not null) Bad();
        }
    }

    private static bool Hash(string? hash) => hash is { Length: 16 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Bad() => throw new InvalidDataException("terrain ledger is corrupt or exceeds its limits; stored data was kept");

    private static CellBox Box(int[]? r, (int X, int Z)? grid)
    {
        if (r is not { Length: 6 }) { Bad(); return default; }
        var box = TerrainRecord.ToBox(r);
        if (box.IsEmpty || box.SizeX > TerrainPlan.MaxAxis || box.SizeY > TerrainPlan.MaxAxis || box.SizeZ > TerrainPlan.MaxAxis ||
            box.Count > TerrainPlan.MaxCells || box.X < 0 || box.Y < 0 || box.Z < 0 ||
            (long)box.Y + box.SizeY > TerrainPlan.MaxY ||
            (long)box.X + box.SizeX > (grid?.X ?? 20480) || (long)box.Z + box.SizeZ > (grid?.Z ?? 20480)) Bad();
        return box;
    }

    private static void Blocks(Dictionary<string, TerrainBlock>? blocks, CellBox box)
    {
        var max = ((box.X % 8 + box.SizeX + 7) / 8) * ((box.Y % 8 + box.SizeY + 7) / 8) * ((box.Z % 8 + box.SizeZ + 7) / 8);
        if (blocks is null || blocks.Count > max) { Bad(); return; }
        foreach (var (key, b) in blocks)
        {
            var p = key.Split(',');
            if (p.Length != 3 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var y) || !int.TryParse(p[2], out var z)) { Bad(); continue; }
            if (x < 0 || y < 0 || z < 0 || x % 8 != 0 || y % 8 != 0 || z % 8 != 0 || key != $"{x},{y},{z}" ||
                x < TerrainPlan.FloorTo(box.X, 8) || (long)x >= (long)box.X + box.SizeX ||
                y < TerrainPlan.FloorTo(box.Y, 8) || (long)y >= (long)box.Y + box.SizeY ||
                z < TerrainPlan.FloorTo(box.Z, 8) || (long)z >= (long)box.Z + box.SizeZ ||
                b is null || !Hash(b.Before) || !Hash(b.After) ||
                (b.GivenUp && b.Why is not ((int)TerrainConflictKind.PlayerEdit) and not ((int)TerrainConflictKind.BaseZone))) Bad();
        }
    }
}

internal sealed class TerrainRecord
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public int[] Region { get; set; } = [];
    /// <summary>TerrainPlan.ContentHash of the patch that was applied.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public string Content { get; set; } = "";
    /// <summary>False while an apply is under way (or was interrupted): it is resumed by the fingerprints.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public bool Complete { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public string KilnVersion { get; set; } = "";
    /// <summary>Blocks of this version, by <c>TerrainPlan.BlockKey</c>.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public Dictionary<string, TerrainBlock> Blocks { get; set; } = new();
    /// <summary>During an upgrade: the earlier version's blocks not yet visited, and its region.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public Dictionary<string, TerrainBlock>? EarlierBlocks { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public int[]? EarlierRegion { get; set; }

    public CellBox Box => ToBox(Region);
    public static int[] FromBox(CellBox b) => [b.X, b.Y, b.Z, b.SizeX, b.SizeY, b.SizeZ];
    public static CellBox ToBox(int[] r) => r.Length == 6 ? new CellBox(r[0], r[1], r[2], r[3], r[4], r[5]) : default;
}

internal sealed class TerrainBlock
{
    /// <summary>Fingerprint of the block's cells inside the plan's region before the plan wrote them.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public string Before { get; set; } = "";
    /// <summary>The same cells after the write.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public string After { get; set; } = "";
    /// <summary>The plan gave the block up (someone changed it, or it lies in a base): no version touches it again.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public bool GivenUp { get; set; }
    /// <summary>Why it was given up: a <see cref="TerrainConflictKind"/>.</summary>
    [System.Text.Json.Serialization.JsonRequired]
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
    private readonly Dictionary<(string World, string Mod, string Plan), HashSet<string>> _running = new();

    /// <summary>Marks an apply as under way; false when the same plan of the same mod already is.</summary>
    public bool Begin(string world, string mod, string plan) => _running.TryAdd((world, mod, plan), new(StringComparer.Ordinal));
    public bool Running(string world, string mod, string plan) => _running.ContainsKey((world, mod, plan));
    public void End(string world, string mod, string plan) => _running.Remove((world, mod, plan));
    public void Clear() { _running.Clear(); _worlds.Clear(); }
    public void Retire(string mod)
    {
        foreach (var key in _running.Keys.Where(k => k.Mod == mod).ToList()) _running.Remove(key);
        foreach (var mods in _worlds.Values) mods.Remove(mod);
    }

    /// <summary>The blocks a plan is about to write are its own from now on, so another mod's apply that starts while this
    /// one is spread over ticks is refused.</summary>
    public void Claim(string world, string mod, string plan, IEnumerable<string> blockKeys)
    {
        if (!_worlds.TryGetValue(world, out var mods)) _worlds[world] = mods = new(StringComparer.Ordinal);
        if (!mods.TryGetValue(mod, out var plans)) mods[mod] = plans = new(StringComparer.Ordinal);
        if (!plans.TryGetValue(plan, out var set)) plans[plan] = set = new(StringComparer.Ordinal);
        foreach (var k in blockKeys)
        {
            set.Add(k);
            if (_running.TryGetValue((world, mod, plan), out var active)) active.Add(k);
        }
    }

    /// <summary>The "mod/plan" of another mod that owns the block, or null.</summary>
    public string? OwnerOf(string world, string exceptMod, string blockKey)
    {
        foreach (var (key, set) in _running)
            if (key.World == world && key.Mod != exceptMod && set.Contains(blockKey)) return $"{key.Mod}/{key.Plan}";
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
