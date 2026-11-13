// SPDX-License-Identifier: MIT
// Kiln.Players in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (PlayersStartup, the first service tick) and every 25 steps while kiln.players is here: players.positions
// follows the server world. kiln.players, kiln.progression, kiln.playerstate and kiln.characters have only the core's
// rows; the sigdb features each serves are lowered when its table is missing.
namespace Kiln;

internal static unsafe class PlayersCaps
{
    /// <summary>kiln.players' table is here (the core's row): who is on the server.</summary>
    public const string Service = "kiln.players";

    /// <summary>kiln.progression's table is here (the core's row): levels, experience, skills.</summary>
    public const string Progression = "kiln.progression";

    /// <summary>kiln.playerstate's table is here (the core's row): player states, buffs, equipment.</summary>
    public const string States = "kiln.playerstate";

    /// <summary>Kiln.Players' session row: <see cref="Players.PositionOf"/> works now (kiln.players reads positions in a
    /// running server world).</summary>
    public const string Positions = "players.positions";

    /// <summary>The sigdb features each table serves.</summary>
    public static readonly string[] Features = ["players.records", "players.admin", "players.singleton_map"];

    public static readonly string[] ProgressionFeatures =
        ["players.progression", "players.skills", "players.skill_knowledge", "players.progression_events"];

    public static readonly string[] StateFeatures =
        ["players.reflection", "players.action_cancel", "players.sequence_names", "players.client_action_hold"];

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    // the three wrappers remember a miss: forgotten when the refresh finds a table (docs/kiln/services/caps.md)
    static PlayersCaps()
    {
        ServiceCaps.WhenFound(Service, global::Kiln.Players.LookAgain);
        ServiceCaps.WhenFound(Progression, global::Kiln.Progression.LookAgain);
        ServiceCaps.WhenFound(States, PlayerStates.LookAgain);
        ServiceCaps.WhenFound("kiln.characters", PlayerCharacters.LookAgain);
    }

    private static void Report()
    {
        PlayerAim.ReportCapability();  // players.aim (kiln.playerstate's aim functions)
        var players = Players.TableHere;
        if (!players) ServiceCaps.LowerFeatures(Service, Features);
        if (!global::Kiln.Progression.TableHere) ServiceCaps.LowerFeatures(Progression, ProgressionFeatures);
        if (!PlayerStates.TableHere) ServiceCaps.LowerFeatures(States, StateFeatures);
        if (!players)
        {
            ServiceCaps.Static(Positions, false, CapabilityReason.ServiceMissing, "kiln.players is not loaded", Service);
            return;
        }
        var (positions, world) = Players.PositionsNow();
        if (positions || world == 0)
            ServiceCaps.Session(Positions, positions, "positions are read in a running server world", Service);
        else
            ServiceCaps.Report(Positions, CapabilityState.Unavailable, CapabilityReason.Faulted,
                "the server world runs, but kiln.players cannot read positions on this build (see kiln.log)");
    }

    /// <summary>The exception a member throws when <paramref name="capability"/> is off; the rows are reported first.</summary>
    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}
