// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// A player typed a chat line (or a tool injected one). Posted on the tick thread in Pre of the step after the line
/// arrived, before anyone sees it, after the <see cref="Chat.Received"/> handlers (it starts canceled if one of them
/// set <see cref="ChatMessage.Cancel"/>).
/// </summary>
/// <remarks>
/// <para>Cancel: nobody sees the line, not even the sender, and no command runs.</para>
/// <para><see cref="Text"/> may be changed. The game's chat hook can only pass a line on unchanged or drop it, so a
/// changed line is dropped and said again by the sender (<see cref="Chat.Say"/>): everyone sees "&lt;sender&gt;: new
/// text". That needs the sender's entity; without one (a line from a player still loading) the original line is passed
/// on and a warning is logged. If the original was a command line, the changed text runs as the command instead.</para>
/// </remarks>
public sealed class ChatMessageEvent : CancelableEvent
{
    internal ChatMessageEvent(ChatMessage message)
    {
        Message = message;
        Text = message.Text;
    }

    /// <summary>The line as received.</summary>
    public ChatMessage Message { get; }

    public ChatPlayer? Player => Message.Player;
    public EntityId Sender => Message.Sender;
    public string SenderName => Message.SenderName;

    /// <summary>The line as typed; handlers may replace it.</summary>
    public string Text { get; set; }

    /// <summary>The original line starts with the command prefix.</summary>
    public bool IsCommand => Message.IsCommand;

    public bool TextChanged => !string.Equals(Text, Message.Text, StringComparison.Ordinal);
}

/// <summary>
/// A command is about to run: found, the source may use it. Posted on the tick thread before the arguments are
/// parsed. Cancel: the command does not run and the source gets <see cref="CancelMessage"/> as the error.
/// </summary>
public sealed class CommandExecutingEvent : CancelableEvent
{
    internal CommandExecutingEvent(CommandSource source, CommandInfo command, string label, string line,
        IReadOnlyList<string> args)
    {
        Source = source;
        Command = command;
        Label = label;
        Line = line;
        Args = args;
    }

    public CommandSource Source { get; }
    public CommandInfo Command { get; }

    /// <summary>The name as typed (may be an alias).</summary>
    public string Label { get; }

    /// <summary>The whole line without the prefix.</summary>
    public string Line { get; }

    public IReadOnlyList<string> Args { get; }

    /// <summary>What the source is told when the run is canceled.</summary>
    public string? CancelMessage { get; set; }
}

/// <summary>A command finished (ran, failed to parse, failed or was canceled). Posted on the tick thread after the
/// handler; read-only.</summary>
public sealed class CommandExecutedEvent : KilnEvent
{
    internal CommandExecutedEvent(CommandSource source, CommandInfo command, string line, CommandResult result, double ms)
    {
        Source = source;
        Command = command;
        Line = line;
        Result = result;
        ElapsedMs = ms;
    }

    public CommandSource Source { get; }
    public CommandInfo Command { get; }
    public string Line { get; }
    public CommandResult Result { get; }

    /// <summary>Time spent in the handler (and parsing).</summary>
    public double ElapsedMs { get; }
}
