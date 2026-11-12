// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessPlayers? _players;

    /// <summary>The fake kiln.players state of this harness (null before the constructor set it up).</summary>
    internal PlayersFake? PlayersFake { get; private set; }

    internal PlayersFake PlayersFakeOrThrow
    {
        get
        {
            ThrowIfDisposed();
            return PlayersFake ?? throw new InvalidOperationException("the harness has no players yet");
        }
    }

    /// <summary>The players on this server: <see cref="HarnessPlayers.Join"/> one, <see cref="FakePlayer.Leave"/>.
    /// Kiln.Players and what is built on it see them through the kit's fake of the native service.</summary>
    public HarnessPlayers Players => _players ??= new HarnessPlayers(this);

    /// <summary>Runs a command line as <paramref name="player"/> typed it in chat (<see cref="CommandSource.ForPlayer"/>),
    /// through the public <see cref="Commands.Execute"/> on the tick thread: the real permission rules decide
    /// (permissions.json in <see cref="ConfigDirectory"/>, the server's user groups, the player's rights). The reply lines
    /// come back in <see cref="CommandResult.Output"/>; nothing is sent to a chat (kiln.chat is not faked).</summary>
    public CommandResult RunCommand(FakePlayer player, string line)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(line);
        return Call(() => Commands.Execute(CommandSource.ForPlayer(player.Entity, player.Name), line));
    }

    /// <summary>Writes <c>kiln/config/permissions.json</c> (JSONC, the format Kiln writes: groups, userGroups,
    /// players by SteamID64) and has Kiln.Permissions read it now, as it would at its next poll.</summary>
    public void WritePermissions(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ThrowIfDisposed();
        File.WriteAllText(Path.Combine(ConfigDirectory, "permissions.json"), json);
        Call(ReloadPermissionsNow);
    }

    /// <summary>Writes the server's <c>enshrouded_server.json</c> in <see cref="ServerDirectory"/> (Kiln.Permissions reads
    /// its <c>"userGroups"</c>: name and rights) and has Kiln.Permissions read it now.</summary>
    public void WriteServerConfig(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ThrowIfDisposed();
        File.WriteAllText(Path.Combine(ServerDirectory, "enshrouded_server.json"), json);
        Call(ReloadPermissionsNow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadPermissionsNow() => Permissions.Reload();

    private void StartPlayers() => PlayersFake = new PlayersFake(this);

    private void EndPlayers() => PlayersFake?.Clear();
}
