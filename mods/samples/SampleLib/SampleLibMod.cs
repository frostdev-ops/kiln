// SPDX-License-Identifier: MIT
using System.Runtime.Loader;
using Kiln;
using SampleLib.Api;

namespace SampleLib;

public sealed class SampleLibMod : Mod
{
    private sealed class Greeter(string version) : IGreeter
    {
        public string Greet(string who) => $"hello {who}, from sample-lib {version}";
    }

    public override void OnLoad()
    {
        Greeters.Register(new Greeter(Context.Info.Version));
        Context.Messages.On("echo", m =>
        {
            Log.Info($"IMC echo from {m.Sender}: {m.Payload}");
            Mods.Send(m.Sender, "echo-reply", m.Payload);
        });
        // Serializing a mod's own type through the shared default options caches its metadata process-wide; Kiln
        // flushes those caches on unload so the mod's context can still be collected.
        var json = System.Text.Json.JsonSerializer.Serialize(new EchoRequest("probe", 1));
        var api = AssemblyLoadContext.GetLoadContext(typeof(Greeters).Assembly)?.Name;
        Log.Info($"sample-lib {Context.Info.Version} up: exports {string.Join(", ", Context.Info.Exports)} (loaded in {api}); " +
                 $"phase {Lifecycle.Phase}; {json}");
    }

    [Subscribe]
    private void OnModsLoaded(ModsLoadedEvent e) =>
        Log.Info($"lifecycle ModsLoaded: {e.Loaded.Count} loaded ({string.Join(", ", e.Loaded.Select(x => x.Id))}), " +
                 $"{e.NotLoaded.Count} not loaded");

    [Subscribe]
    private void OnStarting(ServerStartingEvent e) => Log.Info($"lifecycle ServerStarting at step {e.Step}");

    [Subscribe]
    private void OnStarted(ServerStartedEvent e) => Log.Info($"lifecycle ServerStarted at step {e.Step} ({e.Source})");

    [Subscribe]
    private void OnStopping(ServerStoppingEvent e) => Log.Info($"lifecycle ServerStopping at step {e.Step} ({e.Reason}); tick thread {Game.IsTickThread}");

    [Subscribe]
    private void OnReloaded(ModReloadedEvent e) =>
        Log.Info($"lifecycle ModReloaded {e.Id} {e.Version} ok={e.Ok}" + (e.Cascade.Count > 0 ? $" with {string.Join(", ", e.Cascade)}" : ""));

    [Subscribe]
    private void OnDisabled(ModDisabledEvent e) => Log.Warn($"lifecycle ModDisabled {e.Id} after {e.Faults} faults ({e.Reason}); report {e.CrashReport}");

    public override void OnUnload() => Log.Info("sample-lib unloading");
}
