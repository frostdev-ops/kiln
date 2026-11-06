// SPDX-License-Identifier: MIT
// Kiln.Rules beyond creative mode: three rule sets a server might want.
using Kiln;

namespace RulesSample;

public sealed class Settings
{
    [Comment("Half-cost building for everyone while this is on (what dismantling gives back is halved too, so placing and dismantling earns nothing).")]
    public bool HalfCostBuilding { get; set; }

    [Comment("Seconds of flight /flight gives.")]
    [Range(5, 3600)]
    public int FlightSeconds { get; set; } = 60;
}

public sealed class RulesSampleMod : Mod
{
    private ModConfig<Settings> _config = null!;
    private RuleSet? _halfCost;
    private readonly List<RuleSet> _bases = new();

    public override void OnLoad()
    {
        _config = Config.Register<Settings>();
        _config.Changed += _ => HalfCost();
        HalfCost();
        // no stamina drain inside any base: one area rule per base (the bases as the world has them now)
        foreach (var b in Build.Bases)
            _bases.Add(Rules.Apply(RuleScope.Box(
                    new WorldPosition(b.Center.X - b.Half.X, b.Center.Y - b.Half.Y, b.Center.Z - b.Half.Z),
                    new WorldPosition(b.Center.X + b.Half.X, b.Center.Y + b.Half.Y, b.Center.Z + b.Half.Z)),
                new PlayerRules { MinStamina = 1 }, label: $"base {b.Altar} stamina"));
        Log.Info($"rules sample: {_bases.Count} base(s) with full stamina, half-cost building {(_config.Value.HalfCostBuilding ? "on" : "off")}");
    }

    private void HalfCost()
    {
        _halfCost?.Dispose();
        _halfCost = _config.Value.HalfCostBuilding
            ? Rules.Apply(RuleScope.Everyone, new PlayerRules { BuildCost = 0.5, BuildRefund = 0.5 }, label: "half-cost building")
            : null;
    }

    // A timed rule set: it ends by itself, and when the player leaves.
    [Command("flight", Description = "the building fly mode anywhere, for a while", Permission = CommandPermission.Admin)]
    private string Flight(CommandContext c)
    {
        if (!c.Source.IsPlayer) c.Fail("run it as a player in game");
        if (Rules.ClientOf(c.Source.Player) is not (RulesClient.Kiln or RulesClient.Host))
            c.Fail("flight needs Kiln in your game");
        var seconds = _config.Value.FlightSeconds;
        Rules.Apply(c.Source.Player, new PlayerRules { FlyAnywhere = true }, TimeSpan.FromSeconds(seconds), "flight");
        return $"You can use the building fly mode anywhere for {seconds} s. When it runs out you are set down on the ground.";
    }
}
