// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessChat? _chat;

    /// <summary>The fake kiln.chat state of this harness (null before the constructor set it up).</summary>
    internal ChatFake? ChatFake { get; private set; }

    /// <summary>The server's text chat as the kit's fake of kiln.chat keeps it: what the players typed and every server
    /// line mods sent. What one player was shown is <see cref="FakePlayer.Chat"/>; a player types with
    /// <see cref="FakePlayer.Say"/>.</summary>
    public HarnessChat Chat => _chat ??= new HarnessChat(this);

    private void StartChat() => ChatFake = new ChatFake(this);
}

/// <summary>A line in a fake player's chat (<see cref="FakePlayer.Chat"/>), or a server line a mod sent
/// (<see cref="HarnessChat.Sent"/>).</summary>
public sealed class FakeChatLine
{
    internal FakeChatLine(ulong step, string text, string? speaker, string? modId, FakePlayer? to)
    {
        Step = step;
        Text = text;
        Speaker = speaker;
        ModId = modId;
        To = to;
    }

    /// <summary>The harness step during which it was sent.</summary>
    public ulong Step { get; }

    /// <summary>The line as the game's chat shows it: a notice's text alone (Kiln's server lines start with
    /// <see cref="global::Kiln.Chat.ServerPrefix"/>), a spoken line as <c>Name: text</c>.</summary>
    public string Text { get; }

    /// <summary>Who speaks it: a player's name for a typed line or <see cref="global::Kiln.Chat.Say"/>; null for a notice
    /// (<see cref="global::Kiln.Chat.Tell(EntityId, string)"/>, <see cref="global::Kiln.Chat.Broadcast"/>, command replies).</summary>
    public string? Speaker { get; }

    /// <summary>The mod that sent a server line; null for a player's typed line and for Kiln's own lines.</summary>
    public string? ModId { get; }

    /// <summary>The one player the line was for; null when it went to every player.</summary>
    public FakePlayer? To { get; }

    /// <summary>The line, for a log line.</summary>
    public override string ToString() => $"step {Step}{(To is null ? "" : $" to {To.Name}")}: {Text}";
}

/// <summary>A line a fake player typed (<see cref="FakePlayer.Say"/>), as the chat's receive loop handed it over.</summary>
public sealed class FakeChatMessage
{
    internal FakeChatMessage(ulong step, FakePlayer? player, string text, bool consumed)
    {
        Step = step;
        Player = player;
        Text = text;
        Consumed = consumed;
    }

    /// <summary>The step whose start delivered it (the step after it was typed).</summary>
    public ulong Step { get; }

    /// <summary>The sender; null when the line came from a connection with no player in game.</summary>
    public FakePlayer? Player { get; }

    /// <summary>The text as typed.</summary>
    public string Text { get; }

    /// <summary>A handler or a command took it (<see cref="ChatMessage.Cancel"/>, a command line): nobody was shown it.</summary>
    public bool Consumed { get; }

    /// <summary>The line, for a log line.</summary>
    public override string ToString() => $"step {Step} {Player?.Name ?? "?"}: {Text}{(Consumed ? " (consumed)" : "")}";
}

/// <summary>
/// The fake kiln.chat of a harness (<see cref="KilnHarness.Chat"/>): Kiln.Chat's own code (<see cref="global::Kiln.Chat"/>,
/// <see cref="global::Kiln.Chat.Received"/>, <see cref="ChatMessageEvent"/>, commands typed in chat) runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> The connected players are the fake players in game. A line a player types
/// (<see cref="FakePlayer.Say"/>) reaches Kiln.Chat at the start of the next step, as the game hands a line over in the
/// next sim step's Pre: handlers, <see cref="ChatMessageEvent"/> and a command line run then; a line nobody consumed is
/// shown to every player, the sender too, as <c>Name: text</c>. Server lines (<c>Tell</c>, <c>Broadcast</c>,
/// <c>Say</c>, command replies) are shown to their players at once, from the step the server started (before that the
/// game server has not been seen and a send says so); a line to a player who is not connected is not delivered.</para>
/// <para><b>Not modelled</b>: the game's inbox (<see cref="global::Kiln.Chat.Diagnostics.LastLine"/> is unavailable),
/// <c>"enableTextChat"</c> (the chat is always on), the account hash and connection state of
/// <see cref="ChatPlayer"/> (0), and what a client draws.</para>
/// </remarks>
public sealed class HarnessChat
{
    private readonly KilnHarness _harness;

    internal HarnessChat(KilnHarness harness) => _harness = harness;

    private ChatFake Fake => _harness.ChatFake ?? throw new InvalidOperationException("the harness has no chat yet");

    /// <summary>Every line the players typed, as delivered, in order (consumed ones included).</summary>
    public IReadOnlyList<FakeChatMessage> Typed => _harness.Run(() => Fake.Typed);

    /// <summary>Every server line mods and Kiln sent, in order: one per <c>Broadcast</c>, <c>Tell</c> or <c>Say</c> line that
    /// reached a player (<see cref="FakeChatLine.To"/> null for every player).</summary>
    public IReadOnlyList<FakeChatLine> Sent => _harness.Run(() => Fake.Sent);
}
