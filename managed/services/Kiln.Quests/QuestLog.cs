// SPDX-License-Identifier: MIT
// A character's quests in one list: the game's own journal quests (read with the game's evaluator, as the client's
// journal shows them) and Kiln quests (the engine's books). Read-only; starting, advancing and completing stay with
// Quests.Start/Advance/Complete and Knowledge. Usage: docs/kiln/services/world-progress.md "Quests".
namespace Kiln;

public enum QuestKind : byte
{
    /// <summary>A quest of the game's journal (JournalRegistryResource), driven by knowledge.</summary>
    Vanilla = 0,
    /// <summary>A quest a mod defined (Kiln's quest engine).</summary>
    Kiln = 1,
}

/// <summary>One quest as a character stands in it.</summary>
/// <param name="Id">Vanilla: <c>quest:0x&lt;entryId&gt;</c>; Kiln: the quest's full id <c>&lt;mod&gt;:&lt;id&gt;</c>.</param>
/// <param name="Name">A readable name: vanilla quests the game's named query behind them
/// (<c>NPC_Alchemist_Hint01_HAS_Shelter_Journal</c>; null for the few without one), Kiln quests their name text.</param>
/// <param name="Status">Vanilla quests are Locked (not started), Active or Done; the game has no failed state.</param>
/// <param name="Step">The current step (0-based; the last step when done); -1 when not started.</param>
/// <param name="NameTag">Vanilla: the loca tag of the quest's title (the catalog's text).</param>
public sealed record QuestEntry(string Id, string? Name, QuestKind Kind, QuestStatus Status, int Step, int Steps, string? StepId,
    uint NameTag, JournalQuest? Journal, QuestDefinition? Definition)
{
    public bool Active => Status == QuestStatus.Active;
    public bool Done => Status == QuestStatus.Done;
    public override string ToString() => $"{Name ?? Id}: {Status}{(Step >= 0 ? $" step {Step + 1}/{Steps}" : "")}";
}

/// <summary>A character's quests (<see cref="Quests.Of(EntityId)"/>), read when it is made.</summary>
public sealed class QuestLog
{
    internal QuestLog(EntityId player, IReadOnlyList<QuestEntry> all)
    {
        Player = player;
        All = all;
    }

    public EntityId Player { get; }
    public IReadOnlyList<QuestEntry> All { get; }
    public IEnumerable<QuestEntry> Active => All.Where(q => q.Status == QuestStatus.Active);
    public IEnumerable<QuestEntry> Completed => All.Where(q => q.Status == QuestStatus.Done);
    public IEnumerable<QuestEntry> Vanilla => All.Where(q => q.Kind == QuestKind.Vanilla);
    public IEnumerable<QuestEntry> Kiln => All.Where(q => q.Kind == QuestKind.Kiln);

    /// <summary>By id (<c>quest:0x8755c335</c>, <c>0x8755c335</c>, <c>mymod:tobin</c>, or a Kiln quest's id within its mod) or
    /// name (case-insensitive); null when there is none.</summary>
    public QuestEntry? Find(string idOrName)
    {
        ArgumentNullException.ThrowIfNull(idOrName);
        var s = idOrName.Trim();
        if (global::Kiln.Knowledge.TryParseId(s, out var raw)) s = $"quest:0x{raw:x8}";
        return All.FirstOrDefault(q => string.Equals(q.Id, s, StringComparison.OrdinalIgnoreCase))
               ?? All.FirstOrDefault(q => string.Equals(q.Name, s, StringComparison.OrdinalIgnoreCase))
               ?? All.FirstOrDefault(q => q.Kind == QuestKind.Kiln && q.Definition is { } d && string.Equals(d.Id, s, StringComparison.OrdinalIgnoreCase));
    }

    public QuestStatus StatusOf(string idOrName) => Find(idOrName)?.Status ?? QuestStatus.Locked;
}

public static partial class Quests
{
    /// <summary>A player's quests, vanilla and Kiln (tick thread). Vanilla quests need kiln.knowledge; on a build without it
    /// the list has only Kiln quests.</summary>
    [TickThreadOnly]
    public static QuestLog Of(EntityId player)
    {
        Game.AssertTickThread();
        WorldIndex.EnsureStarted();  // the vanilla quests' names come from the game's query db
        var list = new List<QuestEntry>();
        if (!player.IsNone && global::Kiln.Knowledge.Available) list.AddRange(VanillaOf(player));
        if (!player.IsNone) list.AddRange(KilnOf(QuestHolder.Of(player)));
        return new QuestLog(player, list);
    }

    [TickThreadOnly]
    public static QuestLog Of(Player player) => Of(player.Entity);

    /// <summary>A vanilla quest's state for a player as an entry (tick thread).</summary>
    internal static QuestEntry VanillaEntry(JournalQuestState s)
    {
        var q = s.Quest;
        var status = s.Rewarded == true || s.Completed == true ? QuestStatus.Done : s.Started ? QuestStatus.Active : QuestStatus.Locked;
        // the client's journal forces every step before a visible one to visible and done: the current step is the last visible
        var step = -1;
        for (var i = s.Steps.Count - 1; i >= 0; i--)
            if (s.Steps[i].Visible == true)
            {
                step = i;
                break;
            }
        if (status == QuestStatus.Done) step = q.Steps.Count - 1;
        return new QuestEntry(q.Name, VanillaNames.Of(q.EntryId), QuestKind.Vanilla, status, step, q.Steps.Count,
            step >= 0 && step < q.Steps.Count ? $"step{step + 1}" : null, q.NameTag, q, null);
    }

    private static IEnumerable<QuestEntry> VanillaOf(EntityId player)
    {
        IReadOnlyList<JournalQuestState> states;
        try
        {
            states = Journal.States(player);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            yield break;  // the journal is not available on this build, or the player left
        }
        foreach (var s in states) yield return VanillaEntry(s);
    }

    internal static QuestEntry KilnEntry(QuestDefinition q, QuestHolder h)
    {
        var status = StatusOf(q, h);
        var r = RecordFor(q, q.Scope == QuestScope.World ? QuestHolder.World : h);
        var step = r is null ? -1 : r.Status == QuestStatus.Done ? q.Steps.Count - 1 : r.StepIndex;
        return new QuestEntry(q.FullId, q.Name.ToString() is { Length: > 0 } n ? n : q.Id, QuestKind.Kiln, status, step, q.Steps.Count,
            r?.StepId, 0, null, q);
    }

    private static IEnumerable<QuestEntry> KilnOf(QuestHolder h)
    {
        foreach (var q in Definitions)
            if (!q.Hidden || RecordFor(q, q.Scope == QuestScope.World ? QuestHolder.World : h) is not null)
                yield return KilnEntry(q, h);
    }
}

/// <summary>Readable names of the game's journal quests (the named query behind each), from world progress's index.</summary>
internal static class VanillaNames
{
    private static Dictionary<uint, string>? _names;
    private static int _quests;

    public static string? Of(uint entryId)
    {
        var map = _names;
        if (map is null || _quests == 0)
        {
            var places = WorldIndex.PlacesIfBuilt;
            if (places is null) return null;  // the index is not read yet: no names yet
            IReadOnlyList<JournalQuest> quests;
            try
            {
                quests = Journal.Quests();
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
            {
                return null;
            }
            map = WorldCatalogBuilder.QuestNames(quests.Where(q => q.Steps.Count > 0)
                .Select(q => (q.EntryId, q.Steps[0].Visible, q.Steps[^1].Done)), places.QueryNames);
            _names = map;
            _quests = quests.Count;
        }
        return map.TryGetValue(entryId, out var n) ? n : null;
    }
}
