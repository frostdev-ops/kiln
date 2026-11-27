// SPDX-License-Identifier: MIT
// The capability rows of Kiln.Pets (19d §5.3). Each row says one mechanism is there; none says a policy. pets.pvp is not
// a row: who players fight through pets is each definition's PetTargetPolicy, enforced by the same native rule.
namespace Kiln;

public static partial class Pets
{
    // The capability names (rows of Kiln.Capabilities): Capabilities.Is(Pets.SummonCapability), [RequiresCapability] and
    // KilnUnavailableException.Capability name them. A row a later Kiln adds is a constant more here (additive).

    /// <summary>The capability: Kiln.Pets is loaded (definitions, records and the facade).</summary>
    public const string ServiceCapability = Caps.Service;
    /// <summary>The capability: definitions register (<see cref="Define"/>, a <c>PetBlock</c>).</summary>
    public const string DefinitionsCapability = Caps.Definitions;
    /// <summary>The capability: owned records are kept per character in the world's save, with exact save receipts (a server
    /// world runs here).</summary>
    public const string RecordsCapability = Caps.Records;
    /// <summary>The capability: bodies are made and retired (<see cref="Summon"/>, <see cref="Dismiss"/>): kiln.spawn on a
    /// running server world.</summary>
    public const string SummonCapability = Caps.Summon;
    /// <summary>The capability: the broker takes pets' leases (creature_control).</summary>
    public const string ControlCapability = Caps.Control;
    /// <summary>The capability: pets follow and stay (walk-only routes through kiln.queries, or the game's guard).</summary>
    public const string FollowCapability = Caps.Follow;
    /// <summary>The capability: pets fight (attack orders, <see cref="SetHostile"/>): the hostile rule and the outgoing
    /// provenance gate of the native safety module.</summary>
    public const string FightCapability = Caps.Fight;
    /// <summary>The capability: a pet goes down instead of dying (the native death guard and retirement), and is revived
    /// (<see cref="Revive"/>).</summary>
    public const string DownedCapability = Caps.Downed;
    /// <summary>The capability: the server sends owners their pets' view (channel kiln:pets).</summary>
    public const string HudServerCapability = Caps.HudServer;
    /// <summary>The capability: this game shows its owner's pets (the channel and the renderer: <see cref="PetsClient"/>).</summary>
    public const string HudClientCapability = Caps.HudClient;

    /// <summary>The rows Kiln.Pets reports, by their short names (internal: the public names are the constants above; the
    /// literals stay here, where [RequiresCapability] and the docs' generators read them).</summary>
    internal static class Caps
    {
        public const string Service = "kiln.pets";
        public const string Definitions = "pets.definitions";
        public const string Records = "pets.records";
        public const string Summon = "pets.summon";
        public const string Control = "pets.control";
        public const string Follow = "pets.follow";
        public const string Fight = "pets.fight";
        public const string Downed = "pets.downed";
        public const string HudServer = "pets.hud.server";
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
