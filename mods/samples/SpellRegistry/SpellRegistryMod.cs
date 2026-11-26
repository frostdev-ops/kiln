// SPDX-License-Identifier: MIT
// Spell registry example (dev.32): the buffs and abilities registries from C#. content/spell-registry.json moves Fireball I's
// release and payment; this code makes the Poisoned debuff (game:buff_5f425faf, 5 s in the game) last as long as a setting says
// when nothing that applies it gives its own time, at game start and again whenever the world loads or the mod is reloaded, and
// logs what the registry reads.
#pragma warning disable KILNX031
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registry;

namespace SpellRegistry;

[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
public sealed class SpellRegistryMod : Mod
{
    public sealed class Settings
    {
        /// <summary>Poisoned's duration in seconds.</summary>
        [Range(1, 600)] public int PoisonedSeconds { get; set; } = 20;
    }

    public override void OnRegistry(RegistryContext context)
    {
        if (!Capabilities.Is("kiln.registry"))
        {
            Log.Warn($"Kiln's registries are not available here ({Capabilities.Why("kiln.registry")}): Poisoned keeps the game's duration");
            return;
        }
        var settings = Config.Register<Settings>().Value;
        var registry = DataRegistry.For(context);
        registry.Alter(view =>
        {
            var poisoned = view.Buffs["game:buff_5f425faf"];
            poisoned.Duration = TimeSpan.FromSeconds(settings.PoisonedSeconds);
            var cast = view.Abilities["game:attack_player_staff_fixedanim_fireball_fast"];
            Log.Info($"Poisoned lasts {poisoned.Duration.TotalSeconds:0} s ({poisoned.Kind}, applied again: {poisoned.Stacking}); Fireball I's cast releases at {cast.Timing.Release.TotalSeconds:0.00} s");
        });
    }
}
