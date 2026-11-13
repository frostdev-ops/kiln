// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessWorld? _world;

    /// <summary>The fake kiln.saves state of this harness (null before the constructor set it up).</summary>
    internal WorldFake? WorldFake { get; private set; }

    /// <summary>The harness's virtual time in ms (steps × <see cref="KilnHarnessOptions.WallDeltaMs"/>).</summary>
    internal long VirtualMs => Volatile.Read(ref _virtualMs);

    /// <summary>The world this server runs: its id, its saves, its sidecar folder. Kiln.Saves (<see cref="WorldData"/>,
    /// <see cref="PlayerData"/>, <see cref="Saves"/>, per-world configs) runs on it through the kit's fake of the native
    /// service.</summary>
    public HarnessWorld World => _world ??= new HarnessWorld(this);

    /// <summary>
    /// Ends the server the way <c>kill -9</c> does, as far as the world goes: the game makes no shutdown save, a save that
    /// is being written never completes, so a harness started next on this folder (<see cref="KilnHarnessOptions.ServerDirectory"/>)
    /// reads the last save and nothing after it. No <see cref="ServerStoppingEvent"/> is posted. The harness still unloads
    /// its mods, so the test process is left clean (their <c>OnUnload</c> runs, which a killed server would not do); steps
    /// after it run nothing. Dispose the harness next.
    /// </summary>
    public void Kill()
    {
        Call(() =>
        {
            WorldFake?.Kill();
            if (_stopping) return;
            _stopping = true;
            Lifecycle.Phase = ServerPhase.Stopping;
            if (!_modsLoaded) return;
            var count = _mods.Mods.Count;
            _mods.UnloadAll("server killed");
            _host.Log(LogLevel.Info, $"lifecycle: server killed (step {_step}): no shutdown save; {count} mod(s) unloaded");
        });
    }

    // ---- the fakes' life (KilnHarness.cs, KilnHarness.Steps.cs call these) ----

    partial void StartFakes()
    {
        SavesReset.BetweenHarnesses();
        StartEntities();
        StartPlayers();
        WorldFake = new WorldFake(this, _options.WorldId);
        _host.Log(LogLevel.Info, $"testkit: world {WorldFake.Id} revision {WorldFake.Revision} ({(WorldFake.Revision == 0 ? "never saved" : "saved before")}), " +
                                 $"saves in {WorldFake.SaveDirectory}");
    }

    partial void EndFakes()
    {
        EndPlayers();
        EndEntities();
    }

    /// <summary>The native services' Pre, before Kiln's: the save cycle, then the players' scan, then the saves pump.</summary>
    partial void FakesBeforeStep(ulong step)
    {
        if (_stopping) return;
        EntitiesFake?.BeforeStep(step);  // the world's own step: the writes of the last step, then their answers
        WorldFake?.BeforeStep(step, _started);
        if (PlayersFake?.Scan(step) is { } left) WorldFake?.PlayersLeft(left);
        WorldFake?.AfterScan();
    }

    /// <summary>The server stopped and its mods unloaded: the game's shutdown save.</summary>
    partial void FakesStopped(ulong step) => WorldFake?.ShutdownSave(step);
}

/// <summary>
/// The world of a harness's server (<see cref="KilnHarness.World"/>): what the game's world file and its save cycle are
/// to Kiln.Saves. Every harness has one from the start, as a dedicated server has: a world id
/// (<see cref="KilnHarnessOptions.WorldId"/>, else the one the server folder ran last, else a new one), a revision that
/// grows by one with each written save, and its mod data under <c>kiln/data/worlds/&lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// <para>The save cycle runs on the harness's steps, as the native service reports the game's: a request is accepted at
/// the start of the next step (<see cref="WorldSavingEvent"/>, then Kiln stages the mod data), and the world file is
/// written at the start of the step after (<see cref="WorldSavedEvent"/>, the staged generation is confirmed). The game's
/// own saves happen too: the autosave every 30 s of virtual time (the timer restarts at every save), one after a player
/// leaves, and the shutdown save when the server stops (not after <see cref="KilnHarness.Kill"/>). The world loads
/// (<see cref="WorldLoadedEvent"/>) at the first step after the server started.</para>
/// <para>Not modelled, and unavailable: <see cref="Saves.Backup"/> (there is no world file to copy; the request is
/// refused), the world file's path (<see cref="Saves.WorldFile"/> is null), the snapshot stage and a client's hosted world.
/// A new world has its id from the start (in the game it gets one at its first save).</para>
/// <para>To read what one harness saved in the next, keep the first one's folder (<see cref="KilnHarnessOptions.KeepFolder"/>)
/// and start the second with <see cref="KilnHarnessOptions.ServerDirectory"/> set to it.</para>
/// </remarks>
public sealed class HarnessWorld
{
    private readonly KilnHarness _harness;

    internal HarnessWorld(KilnHarness harness) => _harness = harness;

    private WorldFake Fake => _harness.WorldFake ?? throw new InvalidOperationException("the harness has no world yet");

    /// <summary>The world's id (what <see cref="Saves.WorldId"/> and <see cref="WorldData.WorldId"/> say).</summary>
    public Guid Id => Fake.Id;

    /// <summary>The revision of the newest written save (0: never saved).</summary>
    public uint Revision => Fake.Revision;

    /// <summary>The world's sidecar folder, <c>kiln/data/worlds/&lt;id&gt;</c>: mod data and per-world configs.</summary>
    public string Directory => Path.Combine(Fake.DataRoot, Fake.Id.ToString());

    /// <summary>Whether the world has loaded (the first step after the server started).</summary>
    public bool Loaded => Fake.Loaded;

    /// <summary>
    /// Saves the world now, as an admin's "save now": asks the game for a save and steps the server until the world file
    /// is written and Kiln has confirmed the mod data (two steps when nothing else is being saved). Returns the new
    /// revision. Mods see <see cref="WorldSavingEvent"/> and <see cref="WorldSavedEvent"/> with reason
    /// <see cref="SaveReason.Request"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server is not running.</exception>
    /// <exception cref="TimeoutException">No save was written within <paramref name="maxSteps"/> steps.</exception>
    public uint Save(int maxSteps = 100)
    {
        return _harness.Run(() =>
        {
            if (_harness.State is not (HarnessState.Starting or HarnessState.Running))
                throw new InvalidOperationException($"the server is {_harness.State.ToString().ToLowerInvariant()}: a save needs a running server");
            var before = Fake.Written;
            if (Fake.Request(SavesAbi.ReasonRequest) != FakeTables.Ok) throw new InvalidOperationException("the world refused the save request");
            _harness.StepUntil(() => Fake.Written > before, maxSteps);
            return Fake.Revision;
        });
    }
}
