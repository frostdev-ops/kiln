// SPDX-License-Identifier: MIT
// How something gets there: a delivery block -> the templates and programs it needs. A spell's item and a wand's attack both place
// what this returns (equipment.placedEntity, or a SpawnEntityEvent's templateReference, with the speed as the local spawn velocity).
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>What a delivery became: the template to place, how fast it leaves, what to put on the item for it, and the emitter its
/// effects went through (the main damage, the first radius and the first status's seconds are the tooltip's numbers).</summary>
internal sealed record DeliveryLowered(Guid PlacedEntity, float Speed, EffectEmitter Emitter, string HandItem);

internal static partial class Compiler
{
    /// <param name="key">The block's oldest name (<c>Was ?? name</c>): ids come from it.</param>
    /// <param name="name">The block's name: the game's debug names come from it.</param>
    /// <param name="path">Where the delivery is in the block, for errors ("delivery").</param>
    /// <param name="onCast">What happens to the caster the moment it leaves, if anything.</param>
    /// <param name="primary">Its first damage is what the item's power stands for: bound to the Damage config a real cast overwrites with the used
    /// item's power. An item has one power, so only one attack of it can be bound; with false, the delivery deals exactly the numbers written.</param>
    internal static DeliveryLowered LowerDelivery(BlockContext ctx, Lowered low, string key, string name, IDeliveryBlock d, string path, IReadOnlyList<EffectRef>? onCast = null, string onCastPath = "onCast", bool primary = true)
    {
        if (d is not ProjectileBlock p) throw new BlockError(ctx.Block, path, $"{d.GetType().Name} is not a delivery Kiln can compile yet");
        if (p.Speed <= 0) throw new BlockError(ctx.Block, $"{path}.speed", $"{p.Speed} must be more than 0");
        if (p.Range <= 0) throw new BlockError(ctx.Block, $"{path}.range", $"{p.Range} must be more than 0");
        if (p.OnImpact is not { Count: > 0 } && p.OnHit is not { Count: > 0 })
            throw new BlockError(ctx.Block, path, "a projectile that does nothing: give it onImpact or onHit");
        var mod = ctx.Mod.Id;

        // the place it stops: the look's impact, running the effects
        var (program, blob, em, listing) = EffectEmitter.PointProgram(ctx, $"{key}.impact", p.OnImpact ?? [], $"{path}.onImpact", primary);
        low.Program(program, $"{key}.impact", blob, listing);
        var impactGuid = BlockIds.Derive(mod, "template", $"{key}.impact");
        var impact = new JsonObject { ["Impact.program"] = program.ToString() };
        // An effect with a VFX says what shows where it lands, in place of the look's burst.
        if (OnImpactHasVfx(ctx, p, path)) impact["VfxComponentResource.vfx"] = "none";
        low.Clone("templates", impactGuid, $"{mod}.{name}.impact", p.Look.Impact, impact);

        // the flight: the look's projectile. The game's own program spawns the impact when it hits something; a program of ours does that
        // too, and also what the block says happens on the way
        var flight = new JsonObject
        {
            ["StaticGravityModifier.modifier"] = p.Gravity,
            ["LifeTime.lifeTime"] = p.Range / p.Speed,
        };
        if (p.OnHit is { Count: > 0 } || onCast is { Count: > 0 })
        {
            var (fp, fblob, fem, flisting) = EffectEmitter.ProjectileProgram(ctx, $"{key}.projectile", onCast, onCastPath, p.OnHit, $"{path}.onHit", primary: primary && em.PrimaryDamage == null);
            low.Program(fp, $"{key}.projectile", fblob, flisting);
            flight["Impact.program"] = fp.ToString();
            em.Adopt(fem);
        }
        var projectileGuid = BlockIds.Derive(mod, "template", $"{key}.projectile");
        if (p.Vfx is { } vfx) Merge(flight, VfxData.Component(ctx, VfxData.Lower(ctx, vfx, $"{path}.vfx"), $"{mod}.{name}.projectile"));
        low.Clone("templates", projectileGuid, $"{mod}.{name}.projectile", p.Look.Projectile, flight);

        // a clone can name another clone once both are registered: a rule, not a field
        low.TemplateRules($"{mod}.{name}.projectile", new JsonObject { ["ImpactConfiguration.configValues.SpawnTemplate"] = impactGuid.ToString() });
        if (p.Tweak.Count > 0) low.Tweak($"{ctx.Block}, {path}", "templates", $"{mod}.{name}.projectile", p.Tweak);
        return new DeliveryLowered(projectileGuid, p.Speed, em, p.Look.HandItem);
    }

    private static bool OnImpactHasVfx(BlockContext ctx, ProjectileBlock p, string path)
    {
        var effects = p.OnImpact ?? [];
        for (var i = 0; i < effects.Count; i++)
            if (ctx.Effect(effects[i], $"{path}.onImpact[{i}]") is var e && (e.Vfx != null || e.Area?.Vfx != null)) return true;
        return false;
    }
}
