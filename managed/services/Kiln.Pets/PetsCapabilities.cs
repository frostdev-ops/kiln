// SPDX-License-Identifier: MIT
// The capability rows of Kiln.Pets (19d §5.3). Each row says one mechanism is there; none says a policy. pets.pvp is not
// a row: who players fight through pets is each definition's PetTargetPolicy, enforced by the same native rule.
namespace Kiln;

public static partial class Pets
{
    /// <summary>The capability names.</summary>
    internal static class Caps
    {
        /// <summary>Kiln.Pets is loaded (definitions, records and the facade).</summary>
        public const string Service = "kiln.pets";
        /// <summary>Definitions register (Pets.Define, PetBlock).</summary>
        public const string Definitions = "pets.definitions";
        /// <summary>Owned records are kept per character in the world's save, with exact save receipts.</summary>
        public const string Records = "pets.records";
        /// <summary>Bodies are made and retired (Summon, Dismiss): kiln.spawn on a running server world.</summary>
        public const string Summon = "pets.summon";
        /// <summary>The broker takes pets' leases (creature_control).</summary>
        public const string Control = "pets.control";
        /// <summary>Pets follow and stay (walk-only routes through kiln.queries, or the game's guard).</summary>
        public const string Follow = "pets.follow";
        /// <summary>Pets fight: the hostile rule and the outgoing provenance gate of the native safety module.</summary>
        public const string Fight = "pets.fight";
        /// <summary>A pet goes down instead of dying: the native death guard and retirement (decisions.native_guards).</summary>
        public const string Downed = "pets.downed";
        /// <summary>The server sends owners their pets' view (channel kiln:pets).</summary>
        public const string HudServer = "pets.hud.server";
        /// <summary>This game shows its owner's pets (the channel and the renderer).</summary>
        public const string HudClient = "pets.hud.client";

        internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 60);

        private static void Report()
        {
            ServiceCaps.Report(Service, CapabilityState.Available);
            ServiceCaps.Report(Definitions, CapabilityState.Available);
            PetsRuntime.ReportRows();
        }
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the rows of Kiln.Pets say what is there
/// before a mod asks, and the service's tick runs (records, bodies, revives, the owners' view).</summary>
internal static class PetsStartup
{
    static PetsStartup()
    {
        Pets.Caps.Rows.Ensure();
        PetsRuntime.Start();
        // the owners' view: the channel on both sides, the built-in bar where frames are drawn
        PetsClient.Start();
        try { Events.On<RenderFrameEvent>(PetsClient.Draw); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            PetsRuntime.Log(LogLevel.Info, $"Kiln.Pets: no pet bar on this side: {e.Message}");
        }
    }
}
