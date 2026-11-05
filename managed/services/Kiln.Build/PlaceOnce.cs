// SPDX-License-Identifier: MIT
// Build.PlaceOnce: run a world edit once per world (a mod's new outpost), remembered in WorldData (Kiln.Saves) under
// the calling mod. The record and the edit reach the disk with the same world save: the edit's chunks are in Kiln's
// save regions, the record in that save's mod-data generation. A crash before the next save loses both, and the next
// boot places it again; never one without the other.
namespace Kiln;

/// <summary>A PlaceOnce record (one per key, per mod, per world).</summary>
public sealed class PlaceOnceRecord
{
    public string Key { get; set; } = "";
    public DateTimeOffset PlacedAt { get; set; }
    public double[] At { get; set; } = [];
    public int Rotation { get; set; }
    public string Schematic { get; set; } = "";
    public string Hash { get; set; } = "";
    public int[] Box { get; set; } = [];
    public long Cells { get; set; }
    public int Props { get; set; }
    public string KilnVersion { get; set; } = "";
}

/// <summary>What <see cref="Build.PlaceOnce(string, Schematic, WorldPosition, PasteOptions?)"/> did.</summary>
/// <param name="Placed">True when it placed now; false when the world already had it (<see cref="Record"/>) or the
/// edit failed (<see cref="Error"/>).</param>
public sealed record PlaceOnceResult(bool Placed, bool AlreadyPlaced, PlaceOnceRecord? Record, PasteResult? Paste, string? Error = null);

public static partial class Build
{
    private const string PlaceOnceKey = "kiln.build.placeonce";

    private sealed class PlaceOnceBook
    {
        public Dictionary<string, PlaceOnceRecord> Records { get; set; } = new();
    }

    /// <summary>The calling mod's PlaceOnce records in this world.</summary>
    public static IReadOnlyList<PlaceOnceRecord> PlacedOnce => WorldData.TryGet<PlaceOnceBook>(PlaceOnceKey, out var b)
        ? b.Records.Values.OrderBy(r => r.PlacedAt).ToList()
        : [];

    /// <summary>Whether the calling mod placed <paramref name="key"/> in this world.</summary>
    public static bool WasPlaced(string key) => WorldData.TryGet<PlaceOnceBook>(PlaceOnceKey, out var b) && b.Records.ContainsKey(key);

    /// <summary>Pastes <paramref name="schematic"/> at <paramref name="at"/> unless the calling mod already placed
    /// <paramref name="key"/> in this world. Waits for the world to run and the tiles to load.</summary>
    public static Task<PlaceOnceResult> PlaceOnce(string key, Schematic schematic, WorldPosition at, PasteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        return PlaceOnceCore(key, async () =>
        {
            var p = await schematic.Paste(at, options);
            return (p.Ok, p, new PlaceOnceRecord
            {
                At = [at.X, at.Y, at.Z],
                Rotation = options?.Rotation ?? 0,
                Schematic = schematic.Name,
                Hash = schematic.Hash,
                Box = [p.Box.X, p.Box.Y, p.Box.Z, p.Box.SizeX, p.Box.SizeY, p.Box.SizeZ],
                Cells = p.Cells.CellsChanged,
                Props = p.PropsPlaced,
            });
        });
    }

    /// <summary>Runs <paramref name="edit"/> (any Kiln.Build edits; return true when it worked) unless the calling
    /// mod already placed <paramref name="key"/> in this world.</summary>
    public static Task<PlaceOnceResult> PlaceOnce(string key, Func<Task<bool>> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return PlaceOnceCore(key, async () => (await edit(), null, new PlaceOnceRecord()));
    }

    /// <summary>Forgets a PlaceOnce record (the next PlaceOnce with that key places again). Does not undo the edit.</summary>
    public static bool ForgetPlaced(string key)
    {
        if (!WorldData.TryGet<PlaceOnceBook>(PlaceOnceKey, out var b) || !b.Records.Remove(key)) return false;
        WorldData.Set(PlaceOnceKey, b);
        return true;
    }

    private static async Task<PlaceOnceResult> PlaceOnceCore(string key, Func<Task<(bool Ok, PasteResult? Paste, PlaceOnceRecord Record)>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException("PlaceOnce needs a mod context (call it from mod code)");
        // the world must run (save loaded, so WorldData holds this world's records)
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (!(Spawn.Available ? Spawn.WorldRunning : Grid is not null))
        {
            if (DateTime.UtcNow > until) return new PlaceOnceResult(false, false, null, null, "the world did not start running");
            await mod.Scheduler.NextTick();
        }
        var book = WorldData.Get<PlaceOnceBook>(PlaceOnceKey);
        if (book.Records.TryGetValue(key, out var have)) return new PlaceOnceResult(false, true, have, null);
        var (ok, paste, record) = await run();
        if (!ok) return new PlaceOnceResult(false, false, null, paste, paste is null ? "the edit reported failure" : $"paste: {paste.Cells.Refusal}, {paste.PropsFailed} prop(s) failed");
        record.Key = key;
        record.PlacedAt = DateTimeOffset.UtcNow;
        record.KilnVersion = Game.KilnVersion;
        book = WorldData.Get<PlaceOnceBook>(PlaceOnceKey);
        book.Records[key] = record;
        WorldData.Set(PlaceOnceKey, book);
        mod.Log.Info($"build: placed '{key}' once in world {Saves.WorldId} ({record.Cells} cells, {record.Props} props)");
        return new PlaceOnceResult(true, false, record, paste);
    }
}
