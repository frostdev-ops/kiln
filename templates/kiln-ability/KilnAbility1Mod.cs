using Kiln;

namespace KilnAbility1;

/// <summary>A scripted ability: key H on a Kiln client with this mod, or "/kiln-mod-id" in chat (vanilla players too).
/// 25 mana, 4 s cooldown, a 0.4 s cast lock; the server spawns the game's own Fireball explosion at the caster and
/// three more in a line in front of them. The explosions are bound to the caster: no self-damage, kill credit is theirs.
/// OnCast runs on the server's tick thread; every await comes back to it.</summary>
public sealed class KilnAbility1Mod : Mod
{
    private const string Explosion = "Explosion_Player_Spell_Fireball_1";   // a game template (`kiln catalog search Explosion_`)

    // A Kiln-spawned impact has no item behind it, so give it its damage explicitly.
    private static readonly ImpactConfigs Configs = new() { ["Damage1"] = DamageSetup.FireOnly(8f) };

    public override void OnLoad()
    {
        Abilities.Register(new AbilityDefinition
        {
            Id = "kiln-mod-id:blast",
            DisplayName = "KilnAbility1 Blast",
            Description = "A line of fire in front of you",
            DefaultKey = "H",
            ChatCommand = "kiln-mod-id",
            Cost = AbilityCost.OfMana(25),
            Cooldown = TimeSpan.FromSeconds(4),
            CastTime = TimeSpan.FromSeconds(0.4),
            Targeting = AbilityTargeting.Self,
            OnCast = CastAsync,
        });
        Log.Info($"ability kiln-mod-id:blast registered (key H or /kiln-mod-id); kiln.abilities {(Abilities.Available ? "available" : "NOT available on this build")}");
    }

    private static async Task CastAsync(AbilityCast cast)
    {
        var c = cast.CasterPosition;
        await cast.Impact(Explosion, c, Configs);
        for (var i = 1; i <= 3; i++)
        {
            await cast.At(TimeSpan.FromMilliseconds(120 * i));
            await cast.Impact(Explosion, c with { X = c.X + 3 * i }, Configs);
        }
    }
}
