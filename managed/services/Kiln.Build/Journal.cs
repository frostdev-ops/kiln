// SPDX-License-Identifier: MIT
// The per-session undo journal: every Kiln edit made through Kiln.Build (cells, shapes, pastes, props) is recorded
// under its owner (the calling mod, or the player an edit is for) with the cells before and after and the props it
// placed or removed. Undo writes the old cells back only where the world still holds what Kiln wrote (a player's later
// change wins), removes the props it placed and puts removed ones back. Kept in memory: lost at a restart.
using System.Numerics;

namespace Kiln;

/// <summary>One recorded edit.</summary>
public sealed record JournalInfo(long Id, string Owner, string Label, BuildEditKind Kind, DateTimeOffset At, long Cells,
    int PropsPlaced, int PropsRemoved);

/// <summary>What an undo did.</summary>
/// <param name="CellsRestored">Cells set back to their old value.</param>
/// <param name="CellsKept">Cells that changed again after the edit (left alone).</param>
public sealed record UndoResult(long Id, string Label, long CellsRestored, long CellsKept, int PropsRemoved, int PropsRestored,
    bool Complete);

internal sealed class JournalEntry(BuildEditKind kind, string owner)
{
    public long Id;
    public string Owner { get; } = owner;
    public BuildEditKind Kind { get; } = kind;
    public string Label = "";
    public DateTimeOffset At = DateTimeOffset.UtcNow;
    public readonly List<(CellBox Box, Voxel[] Before, Voxel[] After)> Cells = new();
    public readonly List<EntityId> Placed = new();
    public readonly List<PlacedProp> Removed = new();

    public IReadOnlyList<EntityId> RemovedIds => Removed.Select(r => r.Entity).ToList();
    public bool HasContent => Cells.Count > 0 || Placed.Count > 0 || Removed.Count > 0;
    public long CellCount => Cells.Sum(c => (long)c.Before.Length);

    public void AddCells(CellBox box, Voxel[] before, Voxel[] after) => Cells.Add((box, before, after));
}

/// <summary>The session's edit journal (<see cref="Build.Undo"/>, <see cref="Build.History"/>).</summary>
internal static class Journal
{
    private const int MaxEntries = 64;
    private const long MaxCells = 24L << 20;  // before + after: 2 bytes each => ~96 MB at worst

    private static readonly List<JournalEntry> Entries = new();
    private static long _next = 1;

    public static string OwnerOf(BuildOptions o) => o.Actor.IsNone ? Build.Caller : $"player:{o.Actor.Value}";

    public static JournalEntry Begin(BuildEditKind kind, BuildOptions o) => new(kind, OwnerOf(o));

    public static long Commit(JournalEntry e, string label)
    {
        e.Id = _next++;
        e.Label = label;
        Entries.Add(e);
        // bound memory: drop the oldest entries first
        while (Entries.Count > MaxEntries || (Entries.Count > 1 && Entries.Sum(x => x.CellCount) > MaxCells)) Entries.RemoveAt(0);
        return e.Id;
    }

    public static IReadOnlyList<JournalInfo> List(string? owner) => Entries.Where(e => owner is null || e.Owner == owner)
        .Select(e => new JournalInfo(e.Id, e.Owner, e.Label, e.Kind, e.At, e.CellCount, e.Placed.Count, e.Removed.Count))
        .Reverse().ToList();

    public static JournalEntry? Last(string? owner) => Entries.LastOrDefault(e => owner is null || e.Owner == owner);

    public static JournalEntry? Find(long id) => Entries.FirstOrDefault(e => e.Id == id);

    public static void Remove(JournalEntry e) => Entries.Remove(e);

    public static void Clear() => Entries.Clear();
}

public static partial class Build
{
    /// <summary>The session journal, newest first (all owners, or one: a mod id, or "player:&lt;entity&gt;").</summary>
    public static IReadOnlyList<JournalInfo> History(string? owner = null) => Journal.List(owner);

    /// <summary>The journal owner of edits made with these options (the calling mod, or the actor player).</summary>
    public static string JournalOwner(BuildOptions? options = null) => Journal.OwnerOf(options ?? BuildOptions.Default);

    /// <summary>Reverts the newest journal entry of <paramref name="owner"/> (default: the calling mod's own edits; pass
    /// <see cref="JournalOwner"/> of a player's options for theirs, or <c>"*"</c> for the newest of anyone). Null when
    /// there is nothing to undo. Loads the tiles first.</summary>
    public static async Task<UndoResult?> Undo(string? owner = null, TimeSpan? timeout = null)
    {
        Ensure("Build.Undo");
        var who = owner == "*" ? null : owner ?? Caller;
        var e = Journal.Last(who);
        return e is null ? null : await UndoEntry(e, timeout);
    }

    /// <summary>Reverts one journal entry by id.</summary>
    public static async Task<UndoResult?> UndoEntry(long id, TimeSpan? timeout = null)
    {
        Ensure("Build.Undo");
        var e = Journal.Find(id);
        return e is null ? null : await UndoEntry(e, timeout);
    }

    private static async Task<UndoResult> UndoEntry(JournalEntry e, TimeSpan? timeout)
    {
        foreach (var c in e.Cells)
            if (!await Load(c.Box, timeout ?? TimeSpan.FromSeconds(20)))
                throw new BuildException($"undo: the voxel tiles under {c.Box} did not load", BuildNative.Unavailable);
        Ensure("Build.Undo");
        var o = new BuildOptions { Flags = BuildFlags.InBases | BuildFlags.Unloaded, Journal = false, Label = "undo " + e.Label };
        long restored = 0, kept = 0;
        var complete = true;
        // newest patch first (a paste may hold several)
        for (var i = e.Cells.Count - 1; i >= 0; i--)
        {
            var (box, before, after) = e.Cells[i];
            var now = Read(box);
            var mask = new bool[now.Length];
            long back = 0;
            for (var k = 0; k < now.Length; k++)
            {
                if (now[k] == before[k]) continue;
                if (now[k] == after[k])
                {
                    mask[k] = true;
                    back++;
                }
                else
                {
                    kept++;
                }
            }
            restored += back;
            if (back == 0) continue;
            var r = WriteCore(box, before, mask, o, null);
            if (!r.Verified) complete = false;
        }
        var removed = 0;
        foreach (var id in e.Placed)
        {
            var info = Prop(id);
            if (info is null) continue;
            if (RemovePropCore(info, null)) removed++;
        }
        var putBack = 0;
        foreach (var p in e.Removed)
        {
            var id = PlacePropCore(p.Template, null, p.Position, p.Rotation, p.ItemId == 0 ? uint.MaxValue : p.ItemId,
                new BuildOptions { Flags = BuildFlags.InBases | BuildFlags.AllowAltars, Journal = false }, new JournalEntry(BuildEditKind.Undo, e.Owner));
            if (!id.IsNone) putBack++;
            else complete = false;
        }
        Journal.Remove(e);
        var result = new UndoResult(e.Id, e.Label, restored, kept, removed, putBack, complete && kept == 0);
        var box0 = e.Cells.Count > 0 ? e.Cells[0].Box : default;
        if (Events.HasSubscribers<BuildEditEvent>())
            Events.Post(new BuildEditEvent(BuildEditKind.Undo, box0, EntityId.None, Caller, o.Label, null, [], e.Placed, 0));
        return result;
    }
}
