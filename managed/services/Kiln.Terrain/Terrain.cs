// SPDX-License-Identifier: MIT
// Kiln.Terrain: bounded terrain overlays. Usage and the contract: docs/kiln/services/terrain.md.
namespace Kiln;

/// <summary>
/// Bounded terrain overlays: a mod declares a patch of ground (an ore vein, a mound, a tunnel) and Kiln applies it once
/// per world over several ticks, remembers which 8-cell blocks the plan owns and at which version, and never writes
/// over what someone changed afterwards. The cells are ordinary <see cref="Build"/> edits: the game saves them and
/// sends them to every client, also to players without mods.
/// </summary>
/// <remarks>
/// <para>Server side, tick thread, from mod code. Game materials only (terrain 1 to 116, building blocks 128 to 212).</para>
/// <para><b>Removing a plan does not restore the ground.</b> A saved edit hides the terrain under it for good and
/// nothing can delete it: <see cref="Forget"/> ends the plan's ownership, the cells stay.</para>
/// </remarks>
public static class Terrain
{
    private static readonly TerrainSession Session = new();

    /// <summary>For tests and the test kit: the world behind the calls. Null = Kiln.Build and Kiln.Saves.</summary>
    internal static Func<ITerrainBackend>? BackendFactory;

    /// <summary>One batch of an overlay reached the world. Tick thread, after the write and the ledger update; once per
    /// written batch, nothing for a refusal or an <see cref="TerrainState.AlreadyApplied"/>.</summary>
    public static event Action<TerrainChangedEvent>? Changed;

    /// <summary>Applies <paramref name="patch"/>: waits for the world to run, then writes one batch per tick. Not
    /// all-or-nothing: a cancel or a failure leaves the batches already written in the world, and the result counts
    /// them.</summary>
    /// <returns><see cref="TerrainResult.Ok"/> for <see cref="TerrainState.Completed"/> and
    /// <see cref="TerrainState.AlreadyApplied"/>; otherwise the reason, and the blocks left alone.</returns>
    /// <exception cref="ArgumentException">The patch or the options are not valid (empty, too big, outside the grid, a
    /// height that is not a number, a palette number with no entry, a bad id).</exception>
    /// <exception cref="KilnUnavailableException"><c>terrain.overlays</c> is off.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("terrain.overlays")]
    public static Task<TerrainResult> Apply(TerrainPatch patch, TerrainApplyOptions? options = null)
    {
        var (engine, plan, o, edge) = Begin(patch, options, TerrainCaps.Overlays, "Terrain.Apply");
        return engine.Apply(plan, o, edge);
    }

    /// <summary>A dry run of <paramref name="patch"/>: its size, what an <see cref="Apply"/> would do now and the
    /// blocks it would leave alone. Writes nothing and records nothing.</summary>
    /// <returns><see cref="TerrainPlanReport.Ok"/> false when the world is not running or the tiles did not load.</returns>
    /// <exception cref="ArgumentException">As <see cref="Apply"/>.</exception>
    /// <exception cref="KilnUnavailableException"><c>kiln.terrain</c> is off, or the world cannot be read.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("kiln.terrain")]
    public static Task<TerrainPlanReport> Inspect(TerrainPatch patch, TerrainApplyOptions? options = null)
    {
        var (engine, plan, o, edge) = Begin(patch, options, TerrainCaps.Library, "Terrain.Inspect");
        return engine.Inspect(plan, o, edge);
    }

    /// <summary>The calling mod's kept plans in this world.</summary>
    /// <exception cref="KilnUnavailableException"><c>terrain.overlays</c> is off.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("terrain.overlays")]
    public static IReadOnlyList<TerrainPlanInfo> Applied
    {
        get
        {
            TerrainCaps.Require(TerrainCaps.Overlays);
            Services.RequireTickThread("Terrain.Applied");
            return new TerrainEngine(Backend("Terrain.Applied"), Session, null).Applied();
        }
    }

    /// <summary>Drops the calling mod's record of plan <paramref name="id"/> in this world: the plan no longer owns its
    /// blocks, and an <see cref="Apply"/> of that id starts fresh over whatever is there. <b>The cells stay.</b></summary>
    /// <returns>False when the world has no such plan.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a plan id.</exception>
    /// <exception cref="KilnUnavailableException"><c>terrain.overlays</c> is off.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability("terrain.overlays")]
    public static bool Forget(string id)
    {
        if (!TerrainPlan.ValidId(id)) throw new ArgumentException("a plan id is 1 to 64 characters of a-z, 0-9, '.', '_', '-'", nameof(id));
        TerrainCaps.Require(TerrainCaps.Overlays);
        Services.RequireTickThread("Terrain.Forget");
        return new TerrainEngine(Backend("Terrain.Forget"), Session, null).Forget(id);
    }

    private static (TerrainEngine, TerrainPlan, TerrainApplyOptions, int) Begin(TerrainPatch patch, TerrainApplyOptions? options, string capability, string api)
    {
        var o = options ?? new TerrainApplyOptions();
        var edge = TerrainPlan.BatchEdge(o.CellsPerTick);
        if (o.LoadTimeout <= TimeSpan.Zero) throw new ArgumentException("LoadTimeout must be positive", nameof(options));
        var plan = TerrainPlan.Lower(patch);
        TerrainCaps.Require(capability);
        Services.RequireTickThread(api);
        return (new TerrainEngine(Backend(api), Session, Raise), plan, o, edge);
    }

    private static ITerrainBackend Backend(string api)
    {
        if (BackendFactory is { } f) return f();
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException($"{api} needs a mod context (call it from mod code)");
        return new BuildTerrainBackend(mod);
    }

    private static void Raise(TerrainChangedEvent e)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<TerrainChangedEvent>)h)(e); }
            catch (Exception ex) { Warn($"terrain: a Changed handler threw: {ex.Message}"); }
        }
    }

    private static void Warn(string text) => Kiln.Backend.Current.Log(0, LogLevel.Warn, text);
}
