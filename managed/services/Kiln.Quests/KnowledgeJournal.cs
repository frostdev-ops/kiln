// SPDX-License-Identifier: MIT
// The game's journal (the vanilla quests) as kiln.knowledge reads it from the server's JournalRegistry: every quest
// with its steps, and a quest's state for the world or a player, evaluated with the game's own knowledge queries.
// docs/re/quests.md sections 1 and 3, docs/kiln/services/knowledge.md "Journal".
namespace Kiln;

/// <summary>Who gives a quest (the game's QuestSourceType: an NPC portrait or a biome).</summary>
public enum QuestSource : byte
{
    None = 0, Flame, Blacksmith, Alchemist, Huntress, Farmer, Carpenter, CryptKeeper, Bard, ChineseNewYearTrader, Barber, Fisher,
    AncientResearcher, Grassland, Deepforest, Steppes, Desert, ColdHeights, Wetlands, AncientLands,
}

/// <summary>The game's QuestType.</summary>
public enum QuestType : byte
{
    Auto = 0,
    WorldQuest = 1,
    PlayerQuest = 2,
}

/// <summary>One step of a journal quest (JournalEntryResource): shown while <see cref="Visible"/> holds, done when
/// <see cref="Done"/> holds (in all but one vanilla quest every step has the quest's completion query). Names and texts
/// are loca tags (client data).</summary>
public sealed record JournalStep(int Index, uint EntryId, uint NameTag, uint TextTag, uint MapMarkerTag, KnowledgeQuery Visible,
    KnowledgeQuery Done, int ProgressSteps, int RecommendedLevel)
{
    /// <summary>"quest:0x&lt;quest&gt;.step&lt;n&gt;" (add ".visible" / ".done" for the requirement names).</summary>
    public string NameOf(JournalQuest quest) => $"{quest.Name}.step{Index + 1}";
}

/// <summary>A journal quest (JournalQuestResource). <see cref="EntryId"/> is also the quest's player Bool knowledge the
/// game sets when it pays the quest's XP ("rewarded").</summary>
public sealed record JournalQuest(int Index, uint EntryId, uint NameTag, QuestSource Source, QuestType Type, bool IsMain, bool IsTutorial,
    bool ForAllPlayers, int Priority, IReadOnlyList<JournalStep> Steps, int Rewards, IReadOnlyList<uint> RewardExperience,
    IReadOnlyList<uint> RewardItems)
{
    /// <summary>"quest:0x&lt;entryId&gt;".</summary>
    public string Name => $"quest:0x{EntryId:x8}";
    public override string ToString() => $"{Name} {Source}/{Type}{(IsMain ? " main" : "")}, {Steps.Count} step(s)";
}

/// <summary>A step's state; null where it could not be evaluated (a player query asked for the world).</summary>
public sealed record JournalStepState(JournalStep Step, bool? Visible, bool? Done);

/// <summary>A quest's state for the world (<see cref="Target"/> 0: shared progress, as the world's journal entries are
/// driven) or one player.</summary>
/// <param name="Rewarded">The player's "rewarded" knowledge (the game paid the quest's XP); null for the world.</param>
public sealed record JournalQuestState(JournalQuest Quest, EntityId Target, IReadOnlyList<JournalStepState> Steps, bool? Rewarded)
{
    public int VisibleSteps => Steps.Count(s => s.Visible == true);
    /// <summary>At least one step is shown in the journal.</summary>
    public bool Started => Steps.Any(s => s.Visible == true);
    /// <summary>The last step's completion query holds (the game then pays the reward).</summary>
    public bool? Completed => Steps.Count == 0 ? null : Steps[^1].Done;
    public override string ToString() =>
        $"{Quest.Name}: {VisibleSteps}/{Steps.Count} visible, {(Completed is null ? "?" : Completed.Value ? "complete" : "open")}" +
        $"{(Rewarded == true ? ", rewarded" : "")}";
}

/// <summary>The game's journal of vanilla quests (server side; the client builds its quest log from its own copy and
/// the replicated knowledge, so granting a step's visibility knowledge shows the step there).</summary>
public static unsafe class Journal
{
    private static IReadOnlyList<JournalQuest>? _quests;
    private static CacheKey _key;

    // the list belongs to one world: rebuilt when the native catalog was rebuilt (new world)
    private readonly record struct CacheKey(int Count, uint First);

    /// <summary>Every journal quest (tick thread; empty until the catalog exists, a few steps after the world started).</summary>
    public static IReadOnlyList<JournalQuest> Quests()
    {
        var t = KnowledgeNative.Require("Journal");
        Services.RequireTickThread("Journal.Quests");
        var count = (int)t->CatalogCount(2);
        KnowledgeNative.JQuest first = default;
        first.Size = (uint)sizeof(KnowledgeNative.JQuest);
        var firstId = count > 0 && t->JournalQuest(0, &first) == KnowledgeNative.Ok ? first.EntryId : 0;
        if (_quests is not null && _key == new CacheKey(count, firstId)) return _quests;
        var list = new List<JournalQuest>(count);
        for (uint i = 0; i < count; i++)
        {
            KnowledgeNative.JQuest q = default;
            q.Size = (uint)sizeof(KnowledgeNative.JQuest);
            if (t->JournalQuest(i, &q) != KnowledgeNative.Ok) continue;
            var steps = new List<JournalStep>((int)q.Steps);
            for (uint k = 0; k < q.Steps; k++)
            {
                KnowledgeNative.JStep s = default;
                s.Size = (uint)sizeof(KnowledgeNative.JStep);
                if (t->JournalStep(i, k, &s) != KnowledgeNative.Ok) continue;
                steps.Add(new JournalStep((int)k, s.EntryId, s.Name, s.Text, s.MapMarker, KnowledgeNative.FromNative(s.Visible),
                    KnowledgeNative.FromNative(s.Done), (int)s.ProgressSteps, (int)s.RecommendedLevel));
            }
            var xp = new List<uint>();
            var items = new List<uint>();
            for (var r = 0; r < Math.Min(q.Rewards, 4u); r++)
            {
                if (q.RewardXp[r] != 0) xp.Add(q.RewardXp[r]);
                if (q.RewardItem[r] != 0) items.Add(q.RewardItem[r]);
            }
            list.Add(new JournalQuest((int)i, q.EntryId, q.Name, (QuestSource)q.Source, (QuestType)q.Type, q.Main != 0, q.Tutorial != 0,
                q.AllPlayers != 0, (int)q.Priority, steps, (int)q.Rewards, xp, items));
        }
        _key = new CacheKey(count, firstId);
        return _quests = list;
    }

    /// <summary>A quest by "quest:0x…", "0x…" or its entry id; null when there is none.</summary>
    public static JournalQuest? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var s = name.Trim();
        if (s.StartsWith("quest:", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s[..dot];
        return Knowledge.TryParseId(s, out var id) ? Find(id) : null;
    }

    public static JournalQuest? Find(uint entryId) => Quests().FirstOrDefault(q => q.EntryId == entryId);

    /// <summary>The quest's state for the world (<paramref name="target"/> 0) or a player (tick thread): every step's
    /// visibility and completion query evaluated with the game's evaluator.</summary>
    public static JournalQuestState State(EntityId target, JournalQuest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        var mode = target.IsNone ? KnowledgeMode.Shared : KnowledgeMode.Player;
        var steps = new List<JournalStepState>(quest.Steps.Count);
        foreach (var s in quest.Steps)
            steps.Add(new JournalStepState(s, s.Visible.IsEmpty ? null : Knowledge.TryQuery(target, s.Visible, mode),
                s.Done.IsEmpty ? null : Knowledge.TryQuery(target, s.Done, mode)));
        bool? rewarded = target.IsNone ? null : Knowledge.Get(target, quest.EntryId)?.IsSet;
        return new JournalQuestState(quest, target, steps, rewarded);
    }

    public static JournalQuestState State(Player player, JournalQuest quest) => State(player.Entity, quest);

    /// <summary>Every quest's state for a target (tick thread).</summary>
    public static IReadOnlyList<JournalQuestState> States(EntityId target) => [.. Quests().Select(q => State(target, q))];
}
