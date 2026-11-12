// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    /// <summary>Runs a command line as the server console (<see cref="CommandSource.Console"/>), through the public
    /// <see cref="Commands.Execute"/> on the tick thread, and returns its result: <see cref="CommandResult.Output"/> holds
    /// the reply lines (recorded there with no chat behind it), <see cref="CommandResult.Error"/> what went wrong. An
    /// unknown command or a failing handler is a result, not an exception. An async handler that is still running has
    /// returned its lines so far; step on to let it finish.</summary>
    public CommandResult RunCommand(string line) => RunCommand(CommandSource.Console, line);

    /// <summary>Runs a command line as <paramref name="source"/> (see <see cref="RunCommand(string)"/>).</summary>
    public CommandResult RunCommand(CommandSource source, string line)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(line);
        return Call(() => Commands.Execute(source, line));
    }
}
