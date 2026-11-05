using Kiln;

namespace KilnMod1;

/// <summary>Entry point: Kiln creates one instance per load and calls OnLoad on the server's tick thread.</summary>
public sealed class KilnMod1Mod : Mod
{
    private ulong _steps;

    public override void OnLoad()
    {
        Log.Info($"{Context.Info.Name} {Context.Info.Version} loaded (Kiln {Game.KilnVersion}, phase {Lifecycle.Phase})");
        Context.Tick.Pre += OnPre;
    }

    private void OnPre(in TickInfo tick)
    {
        if (++_steps % 1470 == 0) Log.Info($"still here after {_steps} steps");  // ~1 minute on a dedicated server
    }

    [Subscribe]
    private void OnServerStarted(ServerStartedEvent e) => Log.Info($"the world is live (step {e.Step})");

    [Subscribe]
    private void OnServerStopping(ServerStoppingEvent e) => Log.Info($"server stopping ({e.Reason}): save state here");

    public override void OnUnload() => Log.Info("unloading");
}
