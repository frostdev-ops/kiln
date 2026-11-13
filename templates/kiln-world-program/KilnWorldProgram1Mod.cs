using Kiln;

namespace KilnWorldProgram1;

// A world program is code of yours that the game's own scheduler runs, every step, for every entity you ask for.
// It has two halves:
//   kiln.programs.json  what the program may touch. The game fixes that when the world starts, so the file is read
//                       at boot: after a change to it, restart the server (a reload of the mod is not enough).
//   EcsPrograms.Bind    the code, given in OnLoad under the name the file uses.
// This one is called "census". Its file says: for every entity that has a Health component ("with" only selects, it
// reads nothing), on the server ("hostOnly"). The code counts them, and /census says how many there were.
// To read or change a component, list it under "read" or "write" in the file and use context.TryRead / TryWrite with
// a struct of the component's size (docs/kiln/modding/ecs-programs.md has every access list and the limits).
public sealed class KilnWorldProgram1Mod : Mod
{
    private IDisposable? _program;
    private long _counting;   // written by the game's worker threads
    private long _lastStep;   // the tick thread's copy of one whole step

    public override void OnLoad()
    {
        // A game build Kiln has no scheduler data for cannot run programs. Say so and go on without: a mod that
        // throws from OnLoad does not load at all.
        if (!EcsPrograms.Available)
        {
            Log.Warn("world programs are not available on this game build: /census will say so");
            return;
        }
        // The callback runs on the game's worker threads, several at once, once for each entity of the query. So:
        // nothing of Kiln's tick-thread API in here (no Chat, no World, no Log in a loop), only your own data,
        // through Interlocked or another thread-safe way. What the context gives you is valid inside the call only.
        _program = EcsPrograms.Bind("census", context => Interlocked.Increment(ref _counting));
        // After each step, on the tick thread: take what the workers counted in that step and start the next count.
        Context.Tick.Post += AfterStep;
    }

    private void AfterStep(in TickInfo tick) => _lastStep = Interlocked.Exchange(ref _counting, 0);

    [Command("census", Description = "how many entities with health the world had in the last step")]
    private string Census(CommandContext c) =>
        _program is null ? "world programs are not available on this game build" : $"{_lastStep} entities with health in the last step";

    // Kiln ends a mod's programs when it unloads and waits for a callback that is running. Saying it here is for the
    // reader; the declaration stays reserved, so the same file binds again after a reload.
    public override void OnUnload()
    {
        Context.Tick.Post -= AfterStep;
        _program?.Dispose();
    }
}
