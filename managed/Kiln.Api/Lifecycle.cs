// SPDX-License-Identifier: MIT
// Mod and server lifecycle events (Forge: FMLLoadCompleteEvent, ServerStarting/Started/StoppingEvent). All of them are
// posted on Kiln's event bus (Events) on the tick thread; none is cancelable.
namespace Kiln;

/// <summary>Where the server is in its life, as Kiln sees it.</summary>
public enum ServerPhase
{
    /// <summary>Before the first server-world step: mods are not loaded yet.</summary>
    Booting = 0,
    /// <summary>Mods are loaded; the world steps but the game has not reported it is running (<c>LoadEcsScene to Run</c>).</summary>
    Starting = 1,
    /// <summary>The world is live (<see cref="ServerStartedEvent"/> fired).</summary>
    Running = 2,
    /// <summary>A shutdown began (<see cref="ServerStoppingEvent"/> fired); on a dedicated server mods unload next.</summary>
    Stopping = 3,
    /// <summary>Client only: mods are loaded, no server world runs here (main menu, a joined session, or after a
    /// hosted session ended). Client and both mods stay loaded; server mods load with each hosted world and unload when
    /// it ends; the next hosted session fires ServerStarting/Started again.</summary>
    Idle = 4,
}

/// <summary>Current lifecycle state, for mods that load late (hot reload) and missed the events.</summary>
public static class Lifecycle
{
    private static volatile int _phase;

    public static ServerPhase Phase
    {
        get => (ServerPhase)_phase;
        internal set
        {
            if (value == ServerPhase.Booting) Interlocked.Increment(ref _serverSession);
            _phase = (int)value;
        }
    }

    private static long _serverSession;

    /// <summary>Counts server worlds in this process: raised each time one boots (a dedicated server's start, each world a
    /// game client hosts). What a native service set up for one world (kiln.items' refills, which its world's closing
    /// ends) belongs to the session it was made in.</summary>
    internal static long ServerSession => Interlocked.Read(ref _serverSession);

    /// <summary>True once <see cref="ModsLoadedEvent"/> fired (all mods found at start-up are loaded).</summary>
    public static bool ModsLoaded { get; internal set; }

    /// <summary>True while the world is live (<see cref="ServerPhase.Running"/>).</summary>
    public static bool IsServerRunning => Phase == ServerPhase.Running;

    /// <summary>Server-world step at which <see cref="ServerStartedEvent"/> fired (0 before).</summary>
    public static ulong StartedAtStep { get; internal set; }
}

/// <summary>All mods found at start-up are loaded (Forge's <c>FMLLoadCompleteEvent</c>). Fires once, at the first
/// server-world step, right after the last <c>OnLoad</c>; mods loaded later by a reload do not see it (check
/// <see cref="Lifecycle.ModsLoaded"/>). Tick thread.</summary>
public sealed class ModsLoadedEvent : KilnEvent
{
    internal ModsLoadedEvent(IReadOnlyList<ModEntry> loaded, IReadOnlyList<ModEntry> notLoaded)
    {
        Loaded = loaded;
        NotLoaded = notLoaded;
    }

    /// <summary>Loaded mods in load order.</summary>
    public IReadOnlyList<ModEntry> Loaded { get; }

    /// <summary>Mods that failed or were skipped, with <see cref="ModEntry.Error"/>.</summary>
    public IReadOnlyList<ModEntry> NotLoaded { get; }
}

/// <summary>The server is starting: mods are loaded and the server world steps (Forge's <c>ServerStartingEvent</c>:
/// register commands and world hooks here). Fires once, right after <see cref="ModsLoadedEvent"/>. Tick thread.</summary>
public sealed class ServerStartingEvent : KilnEvent
{
    internal ServerStartingEvent(ulong step) => Step = step;

    /// <summary>The server-world step it fired on.</summary>
    public ulong Step { get; }
}

/// <summary>The world is live (Forge's <c>ServerStartedEvent</c>): the game switched from <c>LoadEcsScene</c> to
/// <c>Run</c> and the server world steps. Fires once, on the first step after the game logged the switch (seen through
/// the game-log mirror; without the mirror, at the first step). Players can join from now on. Tick thread.
/// On a client it fires for each hosted session, at the hosted server world's first step (after ServerStarting).</summary>
public sealed class ServerStartedEvent : KilnEvent
{
    internal ServerStartedEvent(ulong step, string source)
    {
        Step = step;
        Source = source;
    }

    public ulong Step { get; }

    /// <summary>How Kiln knew: <c>game-log</c> (the Run switch was seen), <c>first-step</c> (no log mirror),
    /// <c>timeout</c> (the switch line never came) or, on a client, <c>world-started</c> (a hosted session).</summary>
    public string Source { get; }
}

/// <summary>The server is shutting down (Forge's <c>ServerStoppingEvent</c>). Fires once, on the tick thread, while
/// the world still runs, before every mod's <c>OnUnload</c> (mods unload in reverse load order right after). Kiln
/// holds the console stop signal (Ctrl-C / SIGINT) until handlers and unloads finish, at most
/// <c>mods.stop_timeout_ms</c> (kiln.json, default 5000 ms). Save state here. The game's own final save runs after
/// it (Kiln.Saves posts its shutdown save event right after this one).
/// On a client it fires when a hosted session's server world is being destroyed (world still intact), on the
/// destroying thread; then the side "server" mods unload, client and both mods stay loaded (<see cref="ServerPhase.Idle"/>).</summary>
public sealed class ServerStoppingEvent : KilnEvent
{
    internal ServerStoppingEvent(ulong step, string reason)
    {
        Step = step;
        Reason = reason;
    }

    public ulong Step { get; }

    /// <summary>What triggered it: <c>SIGINT</c> (console Ctrl-C), <c>SIGTERM</c>, <c>SIGHUP</c>, <c>SIGQUIT</c>,
    /// <c>game-log</c> (the game's own shutdown line, when no signal reached Kiln) or, on a client,
    /// <c>world-stopping</c> (a hosted session ends).</summary>
    public string Reason { get; }
}

/// <summary>A mod was hot-reloaded (control command <c>reload</c>, <see cref="Mods.ReloadAsync"/>). Fires after the
/// whole cascade is loaded again; the reloaded mod's new instance receives it too. Tick thread.</summary>
public sealed class ModReloadedEvent : KilnEvent
{
    internal ModReloadedEvent(string id, string version, bool ok, IReadOnlyList<string> cascade)
    {
        Id = id;
        Version = version;
        Ok = ok;
        Cascade = cascade;
    }

    public string Id { get; }

    /// <summary>The version now on disk (it may have changed).</summary>
    public string Version { get; }

    /// <summary>False when the mod or one of its dependents failed to load again.</summary>
    public bool Ok { get; }

    /// <summary>Dependents that were unloaded and loaded again with it, in load order.</summary>
    public IReadOnlyList<string> Cascade { get; }
}

/// <summary>The fault policy disabled a mod (too many exceptions within the window; kiln.json <c>mods.fault_limit</c>,
/// <c>mods.fault_window_s</c>). Its handlers are gone; a crash report was written. Tick thread.</summary>
public sealed class ModDisabledEvent : KilnEvent
{
    internal ModDisabledEvent(string id, int faults, string reason, string? crashReport)
    {
        Id = id;
        Faults = faults;
        Reason = reason;
        CrashReport = crashReport;
    }

    public string Id { get; }
    public int Faults { get; }

    /// <summary>The last exception's message.</summary>
    public string Reason { get; }

    /// <summary>Path of the crash report, when one was written.</summary>
    public string? CrashReport { get; }
}
