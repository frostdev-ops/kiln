// SPDX-License-Identifier: MIT
// Gem wave G1: the gem rows of the capability registry (kiln.caps; docs/kiln/services/caps.md). gems.read follows
// kiln.items' item instances (a session row), gems.triggers adds kiln.abilities' native events.
namespace Kiln;

internal static class GemsCaps
{
    /// <summary>Managed, session: the read API (<see cref="Gems.Catalog"/>, <see cref="Gems.Get"/>,
    /// <see cref="Gems.Equipped"/>, <see cref="Gems.Socket(ItemInstanceRef)"/>) and <see cref="GemSocketChangedEvent"/>.</summary>
    public const string Read = "gems.read";

    /// <summary>Managed, session: <see cref="GemTriggeredEvent"/> (G1) and gems with their own effects (G2).</summary>
    public const string Triggers = "gems.triggers";

    /// <summary>Managed, session (server): offered chest gems (<see cref="Gems.OfferChestGems"/>, <see cref="GemForgeOpenedEvent"/>),
    /// 0.2.0-dev.29.15.</summary>
    public const string Sources = Gems.SourcesCapability;

    // every 25 steps (about once a second): items.instances is a session row (pending until a server world runs)
    internal static readonly ServiceCapRows Rows = new("kiln.abilities gems", Report, everySteps: 25);

    /// <summary>Reports the rows when they were not reported under the current registry binding yet. Never throws.</summary>
    internal static void Ensure() => Rows.Ensure();

    private static void Report()
    {
        try
        {
            var read = DecideRead(Kiln.Items.Available, Row("kiln.items"), Row("items.instances"), GameTypes.Available,
                GameTypes.Available ? EquipmentLayout.Live() is not null : null, Kiln.World.Available, Content.GameDataAvailable);
            ServiceCaps.Report(Read, read.State, read.Reason, read.Text);
            var triggers = DecideTriggers(read.State, read.Text, AbilitiesNativeHere(), Row("abilities.natives"));
            ServiceCaps.Report(Triggers, triggers.State, triggers.Reason, triggers.Text);
            var sources = DecideSources(Row(Gems.CraftCapability), GemCrafting.Backend.Status() is not null, GemCrafting.Backend.Extras(),
                Network.Available);
            ServiceCaps.Report(Sources, sources.State, sources.Reason, sources.Text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or TypeInitializationException
                                      or FileNotFoundException or FileLoadException or NotSupportedException)
        {
            ModLog.Warn($"kiln.abilities: {Read} / {Triggers} were not reported: {e.Message}");
        }
    }

    private static unsafe bool AbilitiesNativeHere() => AbilitiesNative.T != null;

    private static Capability? Row(string name) => Capabilities.Get(name) is { Reason: not CapabilityReason.Unknown } r ? r : null;

    /// <summary>The <c>gems.read</c> row. Needs kiln.items (catalog) with item instances (sockets, players' items),
    /// the game's reflection of <c>Equipment.equipmentImpacts</c> (<paramref name="layout"/>: null when kiln.types is
    /// not here) and kiln.world. Without kiln.gamedata it is on with a note: the catalog then has no programs or configs.
    /// Pending while item instances wait for a server world. Pure.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideRead(bool items, Capability? itemsRow,
        Capability? instances, bool types, bool? layout, bool world, bool gamedata)
    {
        if (!items) return Off(itemsRow, CapabilityReason.ServiceMissing, "kiln.items is not loaded", "kiln.items");
        if (instances is { State: CapabilityState.Unavailable })
            return Off(instances, CapabilityReason.ServiceOlder, "", "items.instances");
        if (!types) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "needs kiln.types (the game's reflection)");
        if (layout != true)
            return (CapabilityState.Unavailable, CapabilityReason.SymbolMissing,
                "the game's reflection has no Equipment.equipmentImpacts[].weaponGemImpactEntityId on this build");
        if (!world) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "needs kiln.world");
        if (instances is { State: CapabilityState.Pending } p)
            return (CapabilityState.Pending, CapabilityReason.Session, p.ReasonText.Length > 0 ? $"items.instances: {p.ReasonText}" : "waits for a server world");
        return (CapabilityState.Available, CapabilityReason.None,
            gamedata ? "" : "without kiln.gamedata: Gems.Catalog has no programs or configs");
    }

    /// <summary>The <c>gems.triggers</c> row: <c>gems.read</c> (its state), kiln.abilities' table and its native events
    /// (the sigdb row <c>abilities.natives</c> not off). Pure.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideTriggers(CapabilityState read, string readText,
        bool abilities, Capability? natives)
    {
        if (!abilities) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.abilities is not loaded");
        if (natives is { State: CapabilityState.Unavailable } n)
            return (CapabilityState.Unavailable, n.Reason, $"needs abilities.natives: {n.ReasonText}");
        return read switch
        {
            CapabilityState.Unavailable => (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, $"needs {Read}: {readText}"),
            CapabilityState.Pending => (CapabilityState.Pending, CapabilityReason.Session, $"{Read}: {readText}"),
            _ => (CapabilityState.Available, CapabilityReason.None, ""),
        };
    }

    /// <summary>The <c>gems.sources</c> row (server): <c>gems.craft</c> (its state), kiln.gemcraft's appended part with
    /// sources on (<paramref name="extras"/>: null for an older kiln-items.dll), and kiln.net for the players' clients.
    /// Pending while the forge actions wait for a server world. Pure.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideSources(Capability? craft, bool service,
        (uint Flags, string Why)? extras, bool network)
    {
        if (!service) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.gemcraft is not here (no server world in this process, or no kiln-items.dll)");
        if (craft is { State: CapabilityState.Unavailable } c)
            return (CapabilityState.Unavailable, c.Reason, $"needs {Gems.CraftCapability}: {c.ReasonText}");
        if (extras is not { } x) return (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, "kiln.gemcraft predates gem sources (an older kiln-items.dll)");
        if (!network) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "needs kiln.net (the players' clients get the offers over it)");
        if ((x.Flags & GemcraftNative.HasSources) == 0)
            return (CapabilityState.Pending, CapabilityReason.Session, x.Why.Length > 0 ? x.Why : "waits for the server world");
        return (CapabilityState.Available, CapabilityReason.None, "");
    }

    // Off with the reason of the row it needs when that row is off, else the fallback.
    private static (CapabilityState, CapabilityReason, string) Off(Capability? needed, CapabilityReason fallback, string text, string name)
    {
        if (needed is { State: CapabilityState.Unavailable } row)
            return (CapabilityState.Unavailable, row.Reason, $"needs {name}: {row.ReasonText}");
        return (CapabilityState.Unavailable, fallback, text.Length > 0 ? text : $"needs {name}");
    }

    /// <summary>A gem read that needs another service's capability (item instances, kiln.world) says <c>gems.read</c>,
    /// with the other row in its text.</summary>
    internal static KilnUnavailableException AsRead(KilnUnavailableException e) =>
        e.Capability == Read ? e : new KilnUnavailableException(Read, $"needs {e.Capability}: {e.Message}", e);
}
