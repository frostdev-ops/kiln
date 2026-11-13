using Kiln;

namespace KilnHooks1;

/// <summary>
/// A hook into the game's own code. Use it when no Kiln service does what you need: a hook depends on the game build
/// (Kiln finds the function by a byte pattern, and a game update can move or change it), runs inside the game's own
/// call, on whatever thread the game calls it from, and a mistake in it crashes the game. Keep a handler short, take no
/// locks, and call the original unless you mean to replace it.
/// </summary>
public sealed class KilnHooks1Mod : Mod
{
    // A function Kiln has a symbol for on this game build (kiln.log lists them at start; a mod can ship patterns of
    // its own in sigdb/, see the sample HookSample). GetComponent is called very often and changes nothing.
    private const string Function = "GetComponent";

    private HookHandle? _hook;
    private long _calls;
    private ulong _steps;

    public override void OnLoad()
    {
        // The service can be missing (a game build Kiln has no symbols for yet): say so and do nothing.
        if (!Hooks.Available)
        {
            Log.Warn("kiln.hooks is not available on this game build: nothing is hooked");
            return;
        }
        if (!Symbols.TryGet(Function, out var address))
        {
            Log.Warn($"this game build has no symbol {Function}: nothing is hooked");
            return;
        }
        // A detour: the game's calls come here first. The handler gets the call's arguments (call[0], call[1], ...)
        // and decides what the caller gets back; CallOriginal runs the game's function with them.
        _hook = Hooks.Detour(Function, call =>
        {
            Interlocked.Increment(ref _calls);  // several threads call this at once
            return call.CallOriginal();
        });
        Context.Tick.Pre += OnPre;
        Log.Info($"hooked {Function} at 0x{address:X}");
    }

    private void OnPre(in TickInfo tick)
    {
        if (++_steps % 735 == 0) Log.Info($"{Function} ran {Interlocked.Read(ref _calls)} time(s) so far");  // about every 30 s
    }

    // Kiln takes a mod's hooks out when the mod unloads; removing the handle says it, and is safe to do twice.
    public override void OnUnload() => _hook?.Remove();
}
