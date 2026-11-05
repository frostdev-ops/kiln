// SPDX-License-Identifier: MIT
using Kiln;

namespace FireNova;

/// <summary>
/// Two kiln.abilities features in one mod:
///  1. Fire Nova, a scripted ability: key G on a Kiln client with this mod (or /nova in chat, which vanilla players can
///     use too), 40 mana, 5 s cooldown, a 0.5 s cast lock. The server spawns the game's own Fireball explosion at the
///     caster and a ring of six more 3.5 m around it, rippling outwards. The explosions are bound to the caster (their
///     ImpactHierarchy root), so the caster is not hit and gets the kill credit. Everyone sees the game's own VFX.
///  2. A per-cast tweak of the game's Fireball: at night, every Fireball explosion a player causes starts with its
///     AttackMod doubled (ImpactStartingEvent, before the explosion's first run). Vanilla clients see the bigger
///     numbers; their tooltips stay vanilla.
/// </summary>
public sealed class FireNovaMod : Mod
{
    private const string Explosion = "Explosion_Player_Spell_Fireball_1";
    private const uint FireballExplosionProgram = 0x7a120ee8;  // program 80a2448f-… of the Explosion_Player_Spell_Fireball_* templates

    public override void OnLoad()
    {
        Abilities.Register(new AbilityDefinition
        {
            Id = "firenova:fire-nova",
            DisplayName = "Fire Nova",
            Description = "A ring of fire around you",
            DefaultKey = "G",
            ChatCommand = "nova",
            Cost = AbilityCost.OfMana(40),
            Cooldown = TimeSpan.FromSeconds(5),
            CastTime = TimeSpan.FromSeconds(0.5),
            Targeting = AbilityTargeting.Self,
            OnCast = CastNova,
        });
        if (Abilities.Available) Abilities.HoldPrograms(FireballExplosionProgram);  // only this program waits for OnStarting
        Log.Info("firenova: Fire Nova registered (G or /nova); Fireball damage x2 at night");
    }

    // The explosion's own damage: a Kiln-spawned impact has no used item (a staff cast takes its damage from the
    // spell's damageSetup), so without a Damage config it hits for 0. Fire x10 is about 16 per explosion at level 16.
    private static readonly ImpactConfigs NovaConfigs = new() { ["Damage1"] = DamageSetup.FireOnly(10f) };

    private static async Task CastNova(AbilityCast cast)
    {
        var c = cast.CasterPosition;
        await cast.Impact(Explosion, c, NovaConfigs);
        for (var ring = 0; ring < 6; ring++)
        {
            await cast.At(TimeSpan.FromMilliseconds(80 * (ring + 1)));
            var a = ring * Math.PI / 3;
            await cast.Impact(Explosion, c with { X = c.X + 3.5 * Math.Cos(a), Z = c.Z + 3.5 * Math.Sin(a) }, NovaConfigs);
        }
    }

    [Subscribe]
    private void OnImpactStarting(ImpactStartingEvent e)
    {
        if (e.Impact.ProgramId != FireballExplosionProgram || !e.RootIsPlayer) return;
        if (!Time.TryGetState(out var t) || t.IsDay) return;
        if (e.TemplateName is not { } name || !name.StartsWith("Explosion_Player_Spell_Fireball", StringComparison.Ordinal)) return;
        if (e.Scale("AttackMod", 2f)) Log.Debug($"firenova: night Fireball {e.Impact}: AttackMod x2");
    }
}
