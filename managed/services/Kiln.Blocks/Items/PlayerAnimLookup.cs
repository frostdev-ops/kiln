// SPDX-License-Identifier: MIT
// The seam's answer: Kiln.PlayerAnim's lookup (PlayerAnimStates.Find, FindGameStates), its records and enums mapped one to one onto the seam's. The mapping
// is pure, and the library is reached through delegates, so a test needs no native table. docs/kiln/modding/animations.md, "Player states".
namespace Kiln;

internal sealed class PlayerAnimLookup(Func<bool> available, Func<bool> capability, Func<PlayerAnimGraphState> graph, Func<string> why,
    Func<string, IReadOnlyList<PlayerAnimGameState>> gameStates, Func<string, string, PlayerAnimStateInfo?> find) : IPlayerStateLookup
{
    private const string Capability = "anim.player_states";

    /// <summary>The library itself.</summary>
    internal static readonly PlayerAnimLookup Instance = new(() => PlayerAnimStates.Available, () => Capabilities.Is(Capability), () => PlayerAnimStates.Graph,
        () => Capabilities.Why(Capability), PlayerAnimStates.FindGameStates, PlayerAnimStates.Find);

    /// <summary>Why it cannot answer: the service missing, the capability off, the merge off, or the graph not up yet. Null when it can.</summary>
    public string? NotAnswering
    {
        get
        {
            try
            {
                if (!available()) return "the kiln.playeranim service is not loaded on this side";
                if (!capability()) return why();
                return graph() switch
                {
                    PlayerAnimGraphState.Off => why(),
                    // the merge runs when the game's resource service comes up, before any world loads: content ready comes after it
                    PlayerAnimGraphState.Waiting => "the player's graph is not up yet: the game's resource service has not started",
                    _ => null,
                };
            }
            catch (KilnUnavailableException e) { return e.Message; }
        }
    }

    public IReadOnlyList<GameStateAnswer> FindGameStates(string name)
    {
        try { return gameStates(name).Select(Map).ToList(); }
        catch (KilnUnavailableException e) { throw new PlayerStatesNotAnswering(e.Message); }
    }

    public ModStateAnswer? Find(string mod, string name)
    {
        try { return find(mod, name) is { } s ? Map(s) : null; }
        catch (KilnUnavailableException e) { throw new PlayerStatesNotAnswering(e.Message); }
    }

    internal static PlayerStateKind Map(PlayerAnimStateKind k) => k switch
    {
        PlayerAnimStateKind.State => PlayerStateKind.State,
        PlayerAnimStateKind.Overlay => PlayerStateKind.Overlay,
        _ => throw new ArgumentOutOfRangeException(nameof(k), k, "a kind of player state Kiln.Blocks does not know"),
    };

    internal static PlayerStateStatus Map(PlayerAnimStateStatus s) => s switch
    {
        PlayerAnimStateStatus.Live => PlayerStateStatus.Live,
        PlayerAnimStateStatus.Refused => PlayerStateStatus.Refused,
        PlayerAnimStateStatus.Pending => PlayerStateStatus.Pending,
        _ => PlayerStateStatus.Unavailable,
    };

    internal static GameStateAnswer Map(PlayerAnimGameState g) => new(g.Machine, g.Name, Map(g.Kind), g.Ids.ToList());

    internal static ModStateAnswer Map(PlayerAnimStateInfo s) => new(s.Guid, Map(s.Kind), Map(s.Status), s.Reason);
}
