// SPDX-License-Identifier: MIT
// Optional: the whole world progress index over real game data. Set KILN_WORLD_RES to a folder of resource dumps laid out
// <type hash hex>/<guid>.<part>.bin (the knowledge query db, every keen::SceneEntityChunkResource and every
// keen::ecs::TemplateResource of a game folder, read with tools/kfc); without it the test does nothing. Nothing of the
// game's data is in the repository.
using Xunit;

namespace Kiln.QuestTests;

internal sealed class FolderResources(string root) : IGameResources
{
    public bool Available => true;

    public AssetKey[] List(uint type)
    {
        var dir = Path.Combine(root, type.ToString("x8"));
        if (!Directory.Exists(dir)) return [];
        var list = new List<AssetKey>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.bin"))
        {
            var parts = Path.GetFileNameWithoutExtension(f).Split('.');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var g) && uint.TryParse(parts[1], out var part))
                list.Add(new AssetKey(g, type, part));
        }
        return [.. list];
    }

    public byte[]? Read(AssetKey key)
    {
        var f = Path.Combine(root, key.Type.ToString("x8"), $"{key.Guid}.{key.Part}.bin");
        return File.Exists(f) ? File.ReadAllBytes(f) : null;
    }
}

public sealed class RealDataTests(ITestOutputHelper output)
{
    [Fact]
    public void The_index_over_real_game_data_lists_spires_shrines_roots_obelisks_and_bosses()
    {
        var dir = Environment.GetEnvironmentVariable("KILN_WORLD_RES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            output.WriteLine("KILN_WORLD_RES not set: skipped");
            return;
        }
        WorldIndex.Source = new FolderResources(dir);
        Assert.True(WorldIndex.Wait(bosses: true, TimeSpan.FromMinutes(5)), string.Join("; ", WorldIndex.Problems));
        var p = WorldIndex.PlacesIfBuilt!;
        output.WriteLine($"places {WorldIndex.PlacesSeconds:0.00} s, bosses {WorldIndex.BossesSeconds:0.00} s; problems: {string.Join("; ", WorldIndex.Problems)}");
        foreach (var s in p.Spires)
            output.WriteLine($"spire {s.Id} {s.Template} {s.Position} unlock {s.Unlock.Query} top {s.TopReached?.Query} beacon {s.Beacon?.Query}");
        foreach (var s in p.Shrines.Take(6)) output.WriteLine($"shrine {s.Id} {s.Kind} {s.Template} {s.Position} {s.Discovery.Query}");
        foreach (var r in (WorldIndex.Roots ?? []).Take(6)) output.WriteLine($"root {r.Id} well={r.ElixirWell} {r.Position} {r.Destruction.Query}");
        foreach (var o in p.Obelisks.Take(4)) output.WriteLine($"obelisk {o.Id} {o.Position} {o.Reading.Query}");
        var bosses = WorldIndex.Bosses!;
        foreach (var b in bosses.Where(b => b.Tracked)) output.WriteLine($"boss {b.Id} ({b.Spawner}) {b.Position} kill {b.Kill!.Query}");
        output.WriteLine($"{p.Spires.Count} spires, {p.Shrines.Count} shrines ({p.Shrines.Count(s => s.Position is not null)} placed), " +
                         $"{p.Roots.Count} roots ({WorldIndex.Roots!.Count(r => r.Position is not null)} placed), {p.Obelisks.Count} obelisks, " +
                         $"{bosses.Count} bosses ({bosses.Count(b => b.Tracked)} tracked), {p.QueryNames.Count} named queries");

        Assert.True(p.Spires.Count >= 8);
        Assert.Contains(p.Spires, s => s.TopReached is not null && s.Beacon is not null);
        Assert.True(p.Shrines.Count >= 40);
        Assert.Contains(p.Shrines, s => s.Kind == ShrineKind.Sanctum);
        Assert.True(p.Shrines.Count(s => s.Position is not null) * 10 >= p.Shrines.Count * 9);
        Assert.True(p.Roots.Count >= 40);
        Assert.Contains(p.Roots, r => r.ElixirWell);
        Assert.True(WorldIndex.Roots!.Count(r => r.Position is not null) * 10 >= p.Roots.Count * 9);
        Assert.True(bosses.Count >= 20);
        Assert.Contains(bosses, b => b.Tracked);
        Assert.Equal(bosses.Count, bosses.Select(b => b.Id).Distinct().Count());
        Assert.Equal(p.Shrines.Count, p.Shrines.Select(s => s.Id).Distinct().Count());
    }
}
