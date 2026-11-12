// SPDX-License-Identifier: MIT
using Kiln;

namespace TkLifeBase;

/// <summary>Records the lifecycle as it sees it ("life: ..." lines), and receives "hello" messages: the handler and the
/// first Pre after it log, so the test sees the message arrive before Pre in the same step.</summary>
public sealed class TkLifeBaseMod : Mod
{
    private bool _afterMessage;

    public override void OnLoad()
    {
        Log.Info($"life: OnLoad phase {Lifecycle.Phase}");
        Context.Messages.On("hello", m =>
        {
            Log.Info($"message: {m.Payload} from {m.Sender}");
            _afterMessage = true;
        });
        Context.Tick.Pre += (in TickInfo t) =>
        {
            if (!_afterMessage) return;
            _afterMessage = false;
            Log.Info($"pre after message, step {t.Step}");
        };
    }

    public override void OnUnload() => Log.Info($"life: OnUnload phase {Lifecycle.Phase}");

    [Subscribe] private void Loaded(ModsLoadedEvent e) => Log.Info($"life: ModsLoaded {string.Join(",", e.Loaded.Select(m => m.Id))}");
    [Subscribe] private void Starting(ServerStartingEvent e) => Log.Info($"life: Starting step {e.Step} phase {Lifecycle.Phase}");
    [Subscribe] private void Started(ServerStartedEvent e) => Log.Info($"life: Started step {e.Step} {e.Source} at {Lifecycle.StartedAtStep}");
    [Subscribe] private void Stopping(ServerStoppingEvent e) => Log.Info($"life: Stopping {e.Reason} phase {Lifecycle.Phase}");
    [Subscribe] private void Reloaded(ModReloadedEvent e) => Log.Info($"life: Reloaded {e.Id} ok {e.Ok}");
}
