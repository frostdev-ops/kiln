// SPDX-License-Identifier: MIT
// A quiet server: with nobody online the game's server loop waits 40 ms between two passes. This mod makes that 200 ms,
// so an empty server steps about five times less often; the moment someone joins, it changes nothing any more.
//   Pacing   the places the game's loops wait in, and a handler that may change the server loop's wait
// What to take from it: a pacing handler runs inside the game's loop, on the game's thread, as often as it waits. It
// takes no lock, calls no game or Kiln API and allocates nothing: it reads a few fields and returns. Everything else
// (who is online, the counters for /quiet) is worked out elsewhere and handed to it through plain fields.
using Kiln;

namespace QuietServer;

public sealed class QuietServerMod : Mod
{
    private const long QuietWaitNs = 200_000_000;  // 200 ms; Kiln keeps whatever a handler leaves within its own limits

    // Read by the handler on the server loop's thread, written on the tick thread.
    private volatile bool _empty = true;
    private long _lengthened;       // waits the handler made longer (Interlocked)
    private PacingHandler? _handler;

    public override void OnLoad()
    {
        // Which places Kiln found is a fact about the game build. Without the server wait the handler is never called:
        // say so once instead of pretending.
        if ((Pacing.Sites & PacingSites.ServerWait) == 0)
        {
            Log.Info("quiet-server: this game build's server wait is not known to Kiln: nothing is changed");
            return;
        }
        // Whether the server is empty comes from kiln.players. Without it the mod could not tell, and a server that
        // waits longer with players online is worse than one that is not quiet: do nothing then.
        if (!Players.Available)
        {
            Log.Info("quiet-server: kiln.players is not available, so the mod cannot tell an empty server: nothing is changed");
            return;
        }
        _empty = Players.All.Count == 0;
        _handler = Pacing.OnServerWait(OnServerWait);
        Log.Info($"quiet-server: an empty server waits {QuietWaitNs / 1_000_000} ms between passes instead of {Pacing.ServerIdleWaitNs / 1_000_000} ms");
    }

    // The game picked its wait: the idle one with nobody online, shorter ones with players. Only the idle one is changed.
    private void OnServerWait(ref long waitNanoseconds)
    {
        if (!_empty || waitNanoseconds != Pacing.ServerIdleWaitNs) return;
        waitNanoseconds = QuietWaitNs;
        Interlocked.Increment(ref _lengthened);
    }

    [Subscribe]
    private void OnJoined(PlayerJoinedEvent e) => _empty = false;

    [Subscribe]
    private void OnLeft(PlayerLeftEvent e) => _empty = !Players.Available || Players.All.Count == 0;

    [Command("quiet", Description = "what the quiet server sample does right now", Permission = CommandPermission.Admin)]
    private string Quiet(CommandContext c) =>
        _handler is null ? "not active: this game build's server wait is not known to Kiln, or kiln.players is missing (kiln.log says which)"
        : $"{(_empty ? "empty: waiting longer" : "players online: the game's own pace")}; {Interlocked.Read(ref _lengthened)} wait(s) lengthened so far";

    // Kiln removes a mod's handlers when it unloads; disposing says it in the code.
    public override void OnUnload() => _handler?.Dispose();
}
