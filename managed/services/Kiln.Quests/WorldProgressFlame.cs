// SPDX-License-Identifier: MIT
// What the game derives from a flame level, read from its balancing table (keen::BalancingTable, one resource):
// flameLevelMax +4, playerBaseFogResistance +0x64, playerFogResistancePerFlameLevel +0x68, altarsPerFlameLevel and
// waterSourcesPerFlameLevel (BlobArray<u8>, one entry per level 0..flameLevelMax) +0x1b8 / +0x1c0. Layout of game data
// 1287369 (docs/re/world-status.md section 11); a table that does not look like that reads as null, never as wrong numbers.
namespace Kiln;

internal static class FlameLimitsReader
{
    internal sealed record Table(int Max, int FogBase, int FogPerLevel, byte[] Altars, byte[] Water);

    private static readonly object Lock = new();
    private static Table? _table;
    private static bool _tried;

    public static FlameLimits? At(int level)
    {
        var t = Load();
        if (t is null) return null;
        int? removal = null;
        if (Settings.Available && Settings.Ready && Settings.Values.TryGetValue("flameLevelFogRemoval", out var v))
            removal = v switch { long l => (int)l, double d => (int)d, int i => i, _ => null };
        return Of(t, level, removal);
    }

    private static Table? Load()
    {
        lock (Lock)
        {
            if (_tried) return _table;
            if (!Assets.Available) return null;  // not tried: kiln.assets may start later
            _tried = true;
            try
            {
                var keys = Assets.ListResources(Assets.TypeHash("keen::BalancingTable"));
                if (keys.Length == 1 && Assets.ReadResource(keys[0]) is { } bytes) _table = Parse(bytes);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                _table = null;
            }
            return _table;
        }
    }

    /// <summary>The table of a BalancingTable resource's bytes; null when it does not look like game data 1287369.</summary>
    internal static Table? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var b = new Blob(bytes);
            var max = (int)b.U32(4);
            var (aAt, aCount) = b.Array(0x1b8, 1, 4096);
            var (wAt, wCount) = b.Array(0x1c0, 1, 4096);
            if (max is < 1 or > 255 || aCount != max + 1 || wCount != max + 1) return null;
            var altars = new byte[aCount];
            var water = new byte[wCount];
            for (var i = 0; i < aCount; i++) altars[i] = b.U8(aAt + i);
            for (var i = 0; i < wCount; i++) water[i] = b.U8(wAt + i);
            return new Table(max, (int)b.U32(0x64), (int)b.U32(0x68), altars, water);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The limits of a parsed table (the setting is passed in, so this runs without a game).</summary>
    internal static FlameLimits? Of(Table t, int level, int? removal) =>
        level < 0 || level > t.Max ? null
            : new FlameLimits(level, t.Altars[level], t.Water[level], t.FogBase + t.FogPerLevel * level, removal, removal is { } r ? level >= r : null);
}
