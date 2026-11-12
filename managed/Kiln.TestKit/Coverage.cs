// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>How the kit stands behind an area of Kiln's API.</summary>
public enum CoverageKind
{
    /// <summary>Kiln's own code runs, unchanged, as in the game.</summary>
    Real = 0,
    /// <summary>A fake of the game's side: the service's managed code runs against a native table written in C# that
    /// records what mods ask and answers from the test's script.</summary>
    RecordingFake = 1,
    /// <summary>Not there offline: the service's <c>Available</c> is false, its row says <c>offline</c>.</summary>
    Unavailable = 2,
}

/// <summary>One row of <see cref="Coverage.All"/>.</summary>
/// <param name="Area">What a mod author would look for ("Events", "kiln.combat").</param>
/// <param name="Kind">Real, a recording fake, or unavailable offline.</param>
/// <param name="Capabilities">The capability names of the area (empty when it has none: Kiln.Api's own parts).</param>
/// <param name="Note">One line: what holds and what does not.</param>
public sealed record CoverageRow(string Area, CoverageKind Kind, IReadOnlyList<string> Capabilities, string Note);

/// <summary>What the kit makes real, what it fakes and what is unavailable offline: the data the docs' table is made
/// from. Each part of the kit adds its rows in its own file (<c>AddFakes</c>, <c>AddRunner</c>); every Kiln service no row
/// names gets an Unavailable row of its own.</summary>
public static partial class Coverage
{
    private static readonly Lazy<IReadOnlyList<CoverageRow>> Rows = new(Build);

    /// <summary>Every row: the explicit ones in order, then one per Kiln service no row names, by name.</summary>
    public static IReadOnlyList<CoverageRow> All => Rows.Value;

    static partial void AddFakes(List<CoverageRow> rows);

    static partial void AddRunner(List<CoverageRow> rows);

    private static IReadOnlyList<CoverageRow> Build()
    {
        var rows = new List<CoverageRow>();
        AddCore(rows);
        AddFakes(rows);
        AddRunner(rows);
        var named = rows.SelectMany(r => r.Capabilities).ToHashSet(StringComparer.Ordinal);
        foreach (var service in ServiceNames.All.Order(StringComparer.Ordinal))
            if (!named.Contains(service))
                rows.Add(new CoverageRow(service, CoverageKind.Unavailable, [service],
                    "no fake: the service finds no native table, so its own Available is false and its row says offline"));
        return rows;
    }

    private static void AddCore(List<CoverageRow> rows)
    {
        void Row(string area, CoverageKind kind, string note, params string[] caps) => rows.Add(new CoverageRow(area, kind, caps, note));
        Row("Mod loading and reload", CoverageKind.Real,
            "Kiln.Host's loader: kiln.mod.json, the resolver, a collectible load context per mod, hot load, unload and the reload " +
            "cascade; Unload and Reload check that the old load context was collected");
        Row("Lifecycle", CoverageKind.Real,
            "OnLoad in dependency order, ModsLoaded, ServerStarting, ServerStarted (at the first step, or after the test's " +
            "world-running line), ServerStopping, OnUnload in reverse order; Lifecycle.Phase and StartedAtStep");
        Row("Ticks", CoverageKind.Real,
            "Tick.Pre and Tick.Post in Kiln.Host's order; virtual time (40.8 ms and 2 to 3 clock units a step, no sleeping): " +
            "Environment.TickCount64 and DateTime.UtcNow stay real time", "tick");
        Row("Events", CoverageKind.Real,
            "Kiln.Api's event bus: [Subscribe], Events.On, Post, Enqueue, priorities and cancel; a mod's handlers go with its unload");
        Row("Scheduler and await", CoverageKind.Real,
            "Scheduler.Post from any thread, await Scheduler.NextTick, the mod's synchronization context; Game.IsTickThread is " +
            "true on the harness's tick thread only, so tick-thread API from a worker throws as in the game");
        Row("Config (global)", CoverageKind.Real,
            "Config.Register of every kind (common, server, client): files in the temp kiln/config, edits on disk reach the " +
            "change handlers (file watcher: pace steps in real time), bad files are refused and the values kept");
        Row("Commands (console)", CoverageKind.Real,
            "Kiln.Chat's command registry: [Command] methods, typed trees, help, permission levels; RunCommand as the console " +
            "returns the reply lines. Chat itself (kiln.chat) is not faked: players' chat lines never arrive");
        Row("Inter-mod messages", CoverageKind.Real,
            "Mods.Send between loaded mods, delivered at the next step before the receiver's Pre, dropped with an unloaded sender");
        Row("Locale files", CoverageKind.Real,
            "a mod's locales/<language>.json, the lookup order and fallbacks; KilnHarness.Language switches the game's language " +
            "(a dedicated server says en-us)");
        Row("Fault policy", CoverageKind.Real,
            "Kiln.Host's fault policy (5 faults in 60 s of virtual time disable a mod, ModDisabledEvent, a crash report in the temp " +
            "folder); by default the harness call during which a mod faulted throws");
        Row("Game log lines", CoverageKind.Real,
            "ModContext.GameLog handlers and the lifecycle's watch run as in the game, on lines the test writes " +
            "(KilnHarness.GameLog): the game writes none");
        Row("Game features", CoverageKind.Unavailable,
            "Game.HasFeature is false for every game feature (sigdb) unless the test overrides it; an override changes the " +
            "answer only, nothing behind the feature is faked");
        Row("Tests.RunAsync", CoverageKind.Unavailable,
            "Kiln.Testing's Tests.RunAsync from mod code says \"Kiln.Host is not running\" offline");
    }
}
