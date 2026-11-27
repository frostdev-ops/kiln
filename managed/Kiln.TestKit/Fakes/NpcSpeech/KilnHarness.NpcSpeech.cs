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
/// </remarks>
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
    public string? PickOf(EntityId npc, NpcLineKind kind = NpcLineKind.Greeting)
    {
        lock (S.Lock)
            return S.Picks.TryGetValue((npc.Value, (uint)kind), out var p)
                ? p.Pick switch { 1 => "Random", 2 => "InOrder", 3 => $"Seeded({unchecked((int)p.Seed)})", 4 => "By", _ => "WhenTheGamePicks" }
                : null;
    }

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
