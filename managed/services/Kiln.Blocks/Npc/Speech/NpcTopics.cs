// SPDX-License-Identifier: MIT
// Kiln.Npcs' talk pages (since 0.2.0-dev.32, Marshall/Ryrin's request): a mod's topics on an NPC's page in the game's
// own dialogue menu, after the NPC's own topics, so a villager without a page (Ramone) gets one. The host decides; each
// Kiln client shows the rows; a pick goes back to the host, which runs the topic's OnPick for that player.
// docs/kiln/services/npc-speech.md "Talk pages".
namespace Kiln;

/// <summary>The marker a topic gets in the dialogue menu's list (the game's uiHintType).</summary>
public enum NpcTopicHint : byte
{
    /// <summary>A plain topic.</summary>
    None = 0,
    /// <summary>The game's hint marker.</summary>
    Hint = 1,
    /// <summary>The game's attention marker.</summary>
    Attention = 2,
    /// <summary>The game's main quest marker.</summary>
    MainQuest = 3,
}

/// <summary>One topic of a mod on an NPC's talk page: its <see cref="Label"/> in the list, the NPC's <see cref="Reply"/>
/// in the speech box when a player picks it, and <see cref="OnPick"/>, which runs on the host for that player. Since
/// 0.2.0-dev.32.</summary>
public sealed record NpcTopic
{
    /// <summary>A topic with the same label in every language.</summary>
    /// <exception cref="ArgumentException">An empty or too long id (64 characters), or an empty or too long label.</exception>
    public NpcTopic(string id, string label) : this(id, new UiText(label ?? "")) { }

    /// <summary>A topic with a label per language.</summary>
    /// <exception cref="ArgumentException">An empty or too long id (64 characters), or a label Kiln clients could not show
    /// (empty, over 200 characters, or its languages over 2048 bytes together).</exception>
    public NpcTopic(string id, UiText label)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength)
            throw new ArgumentException($"a topic id is 1 to {MaxIdLength} characters", nameof(id));
        if (SpeechText.Problem(label) is { } p) throw new ArgumentException($"the label: {p}", nameof(label));
        Id = id;
        Label = label;
    }

    /// <summary>The longest topic id.</summary>
    public const int MaxIdLength = 64;

    /// <summary>The topic's id within its mod: the same id names the same topic (Remove, the pick).</summary>
    public string Id { get; }

    /// <summary>Its text in the topic list.</summary>
    public UiText Label { get; }

    /// <summary>What the NPC answers in the speech box when a player picks it (the game's reveal, no voice-over). Null:
    /// no answer box; the host can still answer that player with <see cref="NpcTopicContext.Reply(string)"/>.</summary>
    public UiText? Reply { get; init; }

    /// <summary>The marker in the list.</summary>
    public NpcTopicHint Hint { get; init; }

    /// <summary>The dialogue ends after the <see cref="Reply"/> (the game's outro). Needs a Reply.</summary>
    public bool Close { get; init; }

    /// <summary>Runs on the host's tick thread when a player picks the topic (the host's own player too), as the mod
    /// that set it. A throw is logged; the topic stays.</summary>
    public Action<NpcTopicContext>? OnPick { get; init; }

    /// <summary>Throws when the reply could not be shown or Close has no reply to close after.</summary>
    internal NpcTopic Check()
    {
        if (Reply is not null && SpeechText.Problem(Reply) is { } p) throw new ArgumentException($"topic {Id}'s reply: {p}");
        if (Close && Reply is null) throw new ArgumentException($"topic {Id}: Close ends the dialogue after the Reply, and it has none");
        if (Hint > NpcTopicHint.MainQuest) throw new ArgumentException($"topic {Id}: no hint {(byte)Hint}");
        return this;
    }
}

/// <summary>A pick of a topic: which NPC, which player, which topic. <see cref="Reply(string)"/> answers that player.</summary>
public sealed class NpcTopicContext
{
    internal NpcTopicContext(EntityId npc, EntityId player, NpcTopic topic, string modId)
    {
        Npc = npc;
        Player = player;
        Topic = topic;
        ModId = modId;
    }

    /// <summary>The NPC.</summary>
    public EntityId Npc { get; }

    /// <summary>The player who picked it.</summary>
    public EntityId Player { get; }

    /// <summary>The topic picked.</summary>
    public NpcTopic Topic { get; }

    /// <summary>The mod whose topic it is.</summary>
    public string ModId { get; }

    /// <summary>Answers this player in the speech box of their menu with the NPC (<see cref="Npcs.MenuSay(EntityId, EntityId, string)"/>).</summary>
    public SpeechResult Reply(string text) => Npcs.MenuSay(Npc, Player, text);

    /// <summary>As <see cref="Reply(string)"/>, with a text per language.</summary>
    public SpeechResult Reply(UiText text) => Npcs.MenuSay(Npc, Player, text);

    /// <summary>The NPC, the player and the topic.</summary>
    public override string ToString() => $"{Player} picked {ModId}/{Topic.Id} of {Npc}";
}

/// <summary>One mod's topics on one NPC's talk page (<see cref="Npcs.Topics"/>). Several mods can each have some: the
/// page lists the NPC's own topics first (never removed), then every mod's, up to 16 in all. They end with
/// <see cref="Clear"/>, when the mod unloads, or when the world ends. Since 0.2.0-dev.32.</summary>
public sealed class NpcTopics
{
    internal NpcTopics(EntityId npc, string owner)
    {
        Npc = npc;
        ModId = owner;
    }

    /// <summary>The most topics a page shows (every mod's together).</summary>
    public const int MaxTopics = 16;

    /// <summary>The NPC.</summary>
    public EntityId Npc { get; }

    /// <summary>The mod these topics belong to ("kiln" outside a mod).</summary>
    public string ModId { get; }

    /// <summary>This mod's topics of the NPC now, in their order. Any thread.</summary>
    public IReadOnlyList<NpcTopic> List => NpcSpeechEngine.Book.TopicsOf(NpcSpeechEngine.Layer, Npc.Value, ModId).Select(t => t.Topic).ToArray();

    /// <summary>This mod's topics of the NPC become these (none: <see cref="Clear"/>). Any thread.</summary>
    /// <exception cref="ArgumentException">More than 16, two with one id, or one whose reply could not be shown.</exception>
    public SpeechResult Set(params NpcTopic[] topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return NpcSpeechEngine.SetTopics(Npc, ModId, topics);
    }

    /// <summary>Adds a topic at the end, or replaces this mod's topic with the same id where it is. Any thread.</summary>
    /// <exception cref="ArgumentException">As <see cref="Set"/>.</exception>
    public SpeechResult Add(NpcTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var list = List.ToList();
        var i = list.FindIndex(t => t.Id == topic.Id);
        if (i >= 0) list[i] = topic;
        else list.Add(topic);
        return NpcSpeechEngine.SetTopics(Npc, ModId, list.ToArray());
    }

    /// <summary>Removes this mod's topic with that id (nothing when there is none). Any thread.</summary>
    public SpeechResult Remove(string id) => NpcSpeechEngine.SetTopics(Npc, ModId, List.Where(t => t.Id != id).ToArray());

    /// <summary>Removes all of this mod's topics of the NPC. Any thread.</summary>
    public void Clear() => NpcSpeechEngine.SetTopics(Npc, ModId, []);

    /// <summary>The NPC, the mod and how many topics.</summary>
    public override string ToString() => $"topics of {Npc} by {ModId}: {List.Count}";
}
