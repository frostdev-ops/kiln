// SPDX-License-Identifier: MIT
using Kiln;

namespace TkLifeUser;

/// <summary>Depends on tk-base (the test's manifest says so). Records the lifecycle like it, and on a "send" message
/// sends "hello" to tk-base.</summary>
public sealed class TkLifeUserMod : Mod
{
    public override void OnLoad()
    {
        Log.Info($"life: OnLoad phase {Lifecycle.Phase}; tk-base loaded {Mods.IsLoaded("tk-base")}");
        Context.Messages.On("send", _ =>
        {
            var sent = Mods.Send("tk-base", "hello", "hi");
            Log.Info($"sent hello: {sent}");
        });
    }

    public override void OnUnload() => Log.Info($"life: OnUnload phase {Lifecycle.Phase}");

    [Subscribe] private void Loaded(ModsLoadedEvent e) => Log.Info($"life: ModsLoaded {string.Join(",", e.Loaded.Select(m => m.Id))}");
    [Subscribe] private void Starting(ServerStartingEvent e) => Log.Info($"life: Starting step {e.Step} phase {Lifecycle.Phase}");
    [Subscribe] private void Started(ServerStartedEvent e) => Log.Info($"life: Started step {e.Step} {e.Source} at {Lifecycle.StartedAtStep}");
    [Subscribe] private void Stopping(ServerStoppingEvent e) => Log.Info($"life: Stopping {e.Reason} phase {Lifecycle.Phase}");
}
