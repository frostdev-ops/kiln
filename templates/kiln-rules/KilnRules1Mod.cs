using Kiln;

namespace KilnRules1;

public sealed class KilnRules1Mod : Mod
{
    private RuleSet? _weekend;

    public override void OnLoad()
    {
        // A rule set applies rules to a scope (everyone, one player, a box in the world) until it is disposed, its time
        // runs out, or the mod unloads. Rules of several sets combine; the server's kiln/config/rules.json has the
        // last word on what a mod may grant.
        _weekend = Rules.Apply(RuleScope.Everyone, new PlayerRules { BuildCost = 0.5, BuildRefund = 0.5 }, label: "half-cost building");
        Log.Info($"rules: {_weekend.Label} is on for everyone");
    }

    [Command("fly", Description = "the building fly mode anywhere, for a minute")]
    private string Fly(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player can fly");
        // Flight moves the character in the player's own game too: it needs Kiln there (a host has it).
        if (Rules.ClientOf(c.Source.Player) is not (RulesClient.Kiln or RulesClient.Host))
            return "flight needs Kiln in your game";
        Rules.Apply(c.Source.Player, new PlayerRules { FlyAnywhere = true }, TimeSpan.FromSeconds(60), "flight");
        return "you can fly for a minute";
    }

    [Command("ghost", Description = "NoClip on or off for yourself", Permission = CommandPermission.Admin)]
    private string Ghost(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player can fly");
        var on = NoClip.Of(c.Source.Player).On;
        // Turning NoClip on needs this game build's hooks (the capability rules.noclip): ask, and say what is missing.
        if (!on && !NoClip.Available) return $"NoClip is not available on this game build ({NoClip.Capability} is off)";
        // A refusal (the server's rules.json, the player's game, another rule set) is a result, not an exception:
        // its Message is a sentence for the player.
        return NoClip.Set(c.Source.Player, !on).Message;
    }

    public override void OnUnload() => _weekend?.Dispose();  // Kiln ends a mod's rule sets anyway; this says it
}
