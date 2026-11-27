// SPDX-License-Identifier: MIT
// The entry class's name before 0.2.0-dev.32 (API review): Kiln.Registry.Registry was also its namespace's name, so code in a
// Kiln.* namespace needed an alias (using DataRegistry = Kiln.Registry.Registry). Registries holds the API; this class keeps
// the members 0.2.0-dev.31 shipped, so mods built against them keep working.
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Kiln.Registry;

/// <summary>The name of <see cref="Registries"/> before 0.2.0-dev.32 (it is also its namespace's name): the same members,
/// kept for mods built against it. Use <see cref="Registries"/>.</summary>
[Experimental("KILNX031"), EditorBrowsable(EditorBrowsableState.Never)]
public static class Registry
{
    /// <summary><see cref="Registries.Available"/>.</summary>
    [AnyThread]
    public static bool Available => Registries.Available;
    /// <summary><see cref="Registries.For"/>.</summary>
    [RegistryThreadOnly, RequiresCapability("kiln.registry")]
    public static RegistryStage For(RegistryContext context) => Registries.For(context);
    /// <summary><see cref="Registries.Change"/>.</summary>
    [TickThreadOnly, RequiresCapability("kiln.registry.live")]
    public static RegistryChange Change(Action<RegistryStage> edits) => Registries.Change(edits);
    /// <summary><see cref="Registries.Read"/>.</summary>
    [TickThreadOnly, RequiresCapability("kiln.registry")]
    public static void Read(Action<RegistryStage> read) => Registries.Read(read);
    /// <summary><see cref="Registries.Ready"/>.</summary>
    [TickThreadOnly]
    public static event Action<RegistryReady> Ready
    {
        add => Registries.Ready += value;
        remove => Registries.Ready -= value;
    }
}
