// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessNpcSpeech? _npcSpeech;

    /// <summary>The kit's fake kiln.npcspeech of this harness: what a Kiln client's speech bubble of each NPC would show
    /// after the mod's <c>Npcs.Say</c> and <c>Npcs.Speech</c> calls. Since 0.2.0-dev.31.16, mod API 0.2.23.</summary>
    public HarnessNpcSpeech NpcSpeech => _npcSpeech ??= new HarnessNpcSpeech(this);
}

/// <summary>
/// What Kiln clients' speech bubbles would be told (<see cref="KilnHarness.NpcSpeech"/>): the effective override of each
/// NPC (after every mod's priorities) and its running Say, with their texts. Since 0.2.0-dev.31.16, mod API 0.2.23.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> Kiln.Npcs' whole rule (owners, priorities, the most recent on a tie, a Say over an override and
/// its end), and the texts as kiln.content's loca hooks would get them, per language.</para>
/// <para><b>Not modelled.</b> The game's bubble: which NPC is the player's target, its 5 m range and its random pick, the
/// voice-overs; the network (the kit is the host and sends nothing).</para>
/// <para>Since 0.2.0-dev.32 it also shows the menu's lines: farewells, the NPC's own menu line, the replies a topic or
/// <c>Npcs.MenuSay</c> sent a player, the talk-page topics, and a way to pick a topic as a player would. Texts are per game
/// language key (<c>"en-us"</c>, <c>"de-de"</c>).</para>
/// </remarks>
/// <example><code>
/// // a test of a mod that puts a topic on an NPC's talk page
/// using var kit = new KilnHarness();
/// // ... load the mod, then:
/// Assert.Contains("Heard any rumors?", kit.NpcSpeech.TopicsOf(npc));
/// kit.NpcSpeech.PickTopic(npc, "rumor", player);                       // runs the topic's OnPick on this thread
/// Assert.Equal("They say the Vukah are restless.", kit.NpcSpeech.RepliesTo(player).Last().Text);
/// </code></example>
/// <seealso cref="KilnHarness.NpcSpeech"/>
public sealed class HarnessNpcSpeech
{
    private readonly KilnHarness _harness;

    internal HarnessNpcSpeech(KilnHarness harness) => _harness = harness;

    private NpcSpeechFakeState S => KitFakes.State<NpcSpeechFakeState>(_harness);

    /// <summary>What the NPC's greeting bubble does: <see cref="SpeechMode.Game"/> when Kiln leaves it to the game.</summary>
    public SpeechMode ModeOf(EntityId npc)
    {
        lock (S.Lock) return S.Overrides.TryGetValue(npc.Value, out var o) ? o.Mode : SpeechMode.Game;
    }

    /// <summary>The lines a <see cref="SpeechMode.Replace"/> shows, in <paramref name="language"/> (a game language key
    /// such as "en-us" or "de-de"); empty otherwise.</summary>
    public IReadOnlyList<string> LinesOf(EntityId npc, string language = "en-us")
    {
        lock (S.Lock)
            return S.Overrides.TryGetValue(npc.Value, out var o) ? o.Tags.Select(t => TextOf(t, language)).ToArray() : [];
    }

    /// <summary>The text of the Say showing over the NPC now, in <paramref name="language"/>; null when none.</summary>
    public string? Saying(EntityId npc, string language = "en-us")
    {
        lock (S.Lock)
            return S.Says.TryGetValue(npc.Value, out var s) && s.Until > Environment.TickCount64 ? TextOf(s.Tag, language) : null;
    }

    /// <summary>How the NPC's line of <paramref name="kind"/> picks among its lines: "Random", "InOrder",
    /// "Seeded(seed)" or "By" (the host's callback), as a Kiln client would be told; null when Kiln leaves that line to
    /// the game. Since 0.2.0-dev.32.</summary>
    /// <param name="npc">The NPC.</param>
    /// <param name="kind">Which line: the greeting (default), the farewell or the menu's line.</param>
    /// <returns>The pick as text, or null.</returns>
    /// <example><code>
    /// Assert.Equal("InOrder", kit.NpcSpeech.PickOf(npc, NpcLineKind.Farewell));
    /// </code></example>
    public string? PickOf(EntityId npc, NpcLineKind kind = NpcLineKind.Greeting)
    {
        lock (S.Lock)
            return S.Picks.TryGetValue((npc.Value, (uint)kind), out var p)
                ? p.Pick switch { 1 => "Random", 2 => "InOrder", 3 => $"Seeded({unchecked((int)p.Seed)})", 4 => "By", _ => "WhenTheGamePicks" }
                : null;
    }

    /// <summary>The farewells (<c>Npcs.Farewells</c>) a Kiln client's dialogue menu shows for the NPC, in
    /// <paramref name="language"/>; empty when it keeps the game's. Since 0.2.0-dev.32.</summary>
    /// <param name="npc">The NPC.</param>
    /// <param name="language">A game language key (<c>"en-us"</c>, <c>"de-de"</c>).</param>
    /// <returns>The farewell lines, in the mod's order, in that language.</returns>
    public IReadOnlyList<string> FarewellsOf(EntityId npc, string language = "en-us")
    {
        lock (S.Lock)
            return S.Lines.TryGetValue((npc.Value, 1u), out var t) ? t.Select(x => TextOf(x, language)).ToArray() : [];
    }

    /// <summary>The menu's own lines (<c>Npcs.MenuLine</c>) a Kiln client's dialogue menu shows for the NPC when it
    /// opens, in <paramref name="language"/>; empty when it keeps the game's. Since 0.2.0-dev.32.</summary>
    /// <param name="npc">The NPC.</param>
    /// <param name="language">A game language key (<c>"en-us"</c>, <c>"de-de"</c>).</param>
    /// <returns>The menu lines, in the mod's order, in that language.</returns>
    public IReadOnlyList<string> MenuLinesOf(EntityId npc, string language = "en-us")
    {
        lock (S.Lock)
            return S.Lines.TryGetValue((npc.Value, 2u), out var t) ? t.Select(x => TextOf(x, language)).ToArray() : [];
    }

    /// <summary>What <c>Npcs.MenuSay</c> (and a topic's reply) told <paramref name="player"/>'s menu, oldest first: the
    /// NPC and the text in <paramref name="language"/>. Since 0.2.0-dev.32.</summary>
    /// <param name="player">The player whose menu was told.</param>
    /// <param name="language">A game language key (<c>"en-us"</c>, <c>"de-de"</c>).</param>
    /// <returns>Each reply as the NPC that said it and its text.</returns>
    public IReadOnlyList<(EntityId Npc, string Text)> RepliesTo(EntityId player, string language = "en-us")
    {
        lock (S.Lock)
            return S.Replies.Where(r => r.Player == player)
                .Select(r => (new EntityId(r.Npc), System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.Json) is { } m
                    ? new UiText(m).For(language)
                    : r.Json))
                .ToArray();
    }

    /// <summary>The labels of the Kiln topics a Kiln client's talk page of the NPC shows after the NPC's own
    /// (<c>Npcs.Topics</c>, every mod's), in <paramref name="language"/>. Since 0.2.0-dev.32.</summary>
    /// <param name="npc">The NPC.</param>
    /// <param name="language">A game language key (<c>"en-us"</c>, <c>"de-de"</c>).</param>
    /// <returns>The topic labels, in page order.</returns>
    public IReadOnlyList<string> TopicsOf(EntityId npc, string language = "en-us")
    {
        lock (S.Lock)
            return S.Topics.TryGetValue(npc.Value, out var t) ? t.Select(x => TextOf(x.Label, language)).ToArray() : [];
    }

    /// <summary>A pick of the topic <paramref name="topicId"/> (the id a mod gave it) by <paramref name="player"/>, as
    /// the host runs it: the topic's OnPick, now, on this thread. False when no mod's topic of the NPC has that id. Since
    /// 0.2.0-dev.32.</summary>
    /// <param name="npc">The NPC whose page has the topic.</param>
    /// <param name="topicId">The id the mod gave the topic (<see cref="NpcTopic.Id"/>).</param>
    /// <param name="player">The player who picks it.</param>
    /// <returns>True when a topic with that id was picked.</returns>
    public bool PickTopic(EntityId npc, string topicId, EntityId player) => _harness.Run(() => NpcSpeechEngine.PickLocal(npc, topicId, player));

    /// <summary>How many set and say calls reached the fake (a test that expects nothing new compares it).</summary>
    public int Calls
    {
        get { lock (S.Lock) return S.Calls; }
    }

    private string TextOf(uint tag, string language)
    {
        if (!S.Texts.TryGetValue(tag, out var json)) return $"[tag 0x{tag:x8} without a text]";
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return map is null ? json : new UiText(map).For(language);
    }
}
