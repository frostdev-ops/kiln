// SPDX-License-Identifier: MIT
// Kiln.Spawn in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). The rows are reported at the
// service's first use (and at its autostart, the first service tick); spawn.world follows the session and is refreshed
// on the service tick, never in a getter.
namespace Kiln;

public static unsafe partial class Spawn
{
    /// <summary>The capability names Kiln.Spawn answers for: kiln.spawn for the table, the sigdb features, and
    /// spawn.&lt;thing&gt; for its own rows.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row).</summary>
        public const string Service = "kiln.spawn";

        /// <summary>sigdb: creating entities. kiln.spawn does not load without it.</summary>
        public const string Create = "spawn.create";

        /// <summary>sigdb: <see cref="Spawn.Destroy(EntityId)"/>.</summary>
        public const string Destroy = "spawn.destroy";

        /// <summary>sigdb: the entity scan (<see cref="Spawn.Find"/>, <see cref="Spawn.EntityCount"/>).</summary>
        public const string Entities = "spawn.entities";

        /// <summary>sigdb: the game's own template list.</summary>
        public const string Templates = "spawn.templates";

        /// <summary>Kiln.Spawn's own row: this kiln.spawn takes <see cref="SpawnOptions.Components"/>.</summary>
        public const string Components = "spawn.components";

        /// <summary>Kiln.Spawn's session row: the server world runs (its save is loaded), so entities can be created
        /// and the tracked list cleaned up.</summary>
        public const string World = "spawn.world";
    }

    internal static readonly ServiceCapRows Rows = new(Caps.Service, ReportCapabilities, everySteps: 25, refreshOnlyWithService: true);

    static Spawn() => Rows.Ensure();

    private static void ReportCapabilities()
    {
        if (T == null)
        {
            // sigdb may have resolved a feature that no service is here to serve
            var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing, "kiln.spawn is not loaded", Caps.Service);
            foreach (var name in (string[])[Caps.Create, Caps.Destroy, Caps.Entities, Caps.Templates]) ServiceCaps.Lower(name, reason, text);
        }
        ServiceCaps.Static(Caps.Components, SupportsComponents, CapabilityReason.ServiceOlder,
            "this kiln.spawn takes no extra spawn components (update Kiln)", Caps.Service);
        ServiceCaps.Session(Caps.World, WorldRunning, "the server world is not running yet (its save loads first)", Caps.Service);
    }
}
