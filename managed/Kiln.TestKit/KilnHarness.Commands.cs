// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    /// <summary>Runs a command line as the server console (<see cref="CommandSource.Console"/>), through the public
    /// <see cref="Commands.Execute"/> on the tick thread, and returns its result: <see cref="CommandResult.Output"/> holds
    /// the reply lines (recorded there with no chat behind it), <see cref="CommandResult.Error"/> what went wrong. An
    /// unknown command or a failing handler is a result, not an exception. An async handler that is still running has
    /// returned its lines so far; step on to let it finish.</summary>
    /// <param name="line">The command line, without a leading slash (<c>"hello world"</c>).</param>
    /// <returns>The result: its output lines and its error.</returns>
    public CommandResult RunCommand(string line) => RunCommand(CommandSource.Console, line);

    /// <summary>Runs a command line as <paramref name="source"/> (see <see cref="RunCommand(string)"/>).</summary>
    /// <param name="source">Who runs it: the console, or a player with a permission set.</param>
    /// <param name="line">The command line.</param>
    /// <returns>The result: its output lines and its error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="line"/> is null.</exception>
    public CommandResult RunCommand(CommandSource source, string line)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(line);
        return Call(() => Commands.Execute(source, line));
    }
}
