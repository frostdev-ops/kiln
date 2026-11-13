// SPDX-License-Identifier: MIT
// Kiln.Gameplay in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// first use; kiln.gameplay decides its hooks when it starts, so every row is fixed from then on.
namespace Kiln;

public static unsafe partial class Gameplay
{
    /// <summary>The capability names Kiln.Gameplay answers for.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row).</summary>
        public const string Service = "kiln.gameplay";

        /// <summary>sigdb: the event emitter (<see cref="Gameplay.Inject"/>).</summary>
        public const string Emit = "gameplay.emit";

        /// <summary>sigdb: recipes (<see cref="Gameplay.Recipe"/>, <see cref="Gameplay.WorkshopRecipes"/>).</summary>
        public const string Recipes = "gameplay.recipes";

        /// <summary>Kiln.Gameplay's own row: the entity lifecycle events work (<see cref="Gameplay.EntityEventsAvailable"/>).</summary>
        public const string EntityEvents = "gameplay.entity_events";

        /// <summary>Kiln.Gameplay's own row: natural spawns can be replaced (<see cref="Gameplay.ReplaceNaturalSpawns"/>).</summary>
        public const string SpawnReplace = "gameplay.spawn_replace";

        /// <summary>The sigdb features kiln.gameplay serves.</summary>
        public static readonly string[] Features =
        [
            "gameplay.build", "gameplay.craft", "gameplay.discard", Emit, "gameplay.entities", "gameplay.intercept",
            "gameplay.queues", Recipes, "gameplay.spawn", "gameplay.terraform",
        ];
    }

    internal static readonly ServiceCapRows Rows = new(Caps.Service, ReportCapabilities);

    private static void ReportCapabilities()
    {
        var t = GameplayNative.T;
        if (t == null)
        {
            ServiceCaps.LowerFeatures(Caps.Service, Caps.Features);
        }
        ServiceCaps.Static(Caps.EntityEvents, EntityEventsAvailable, CapabilityReason.Faulted,
            "the entity hooks are off on this build (see kiln.log), or this kiln.gameplay has no entity events", Caps.Service, "gameplay.entities");
        ServiceCaps.Static(Caps.SpawnReplace, t != null && GameplayNative.SpawnReplace(t) != null, CapabilityReason.ServiceOlder,
            "this kiln.gameplay has no spawn replacement (update Kiln)", Caps.Service, "gameplay.spawn");
    }
}
