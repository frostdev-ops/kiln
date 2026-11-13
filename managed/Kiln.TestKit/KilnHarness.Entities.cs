// SPDX-License-Identifier: MIT
using System.Reflection;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private FakeWorld? _entities;

    /// <summary>The fake kiln.world state of this harness (null before the constructor set it up).</summary>
    internal EntitiesFake? EntitiesFake { get; private set; }

    /// <summary>The fake world's entities and components: what Kiln.World (<see cref="global::Kiln.World"/>), Kiln.Game's
    /// typed reads and tracked writes run on offline. The server's saves and world id are <see cref="World"/>.</summary>
    public FakeWorld Entities => _entities ??= new FakeWorld(this);

    private void StartEntities()
    {
        // Kiln.World and Kiln.Game keep what they learned for the process: a table asked for before the kit published
        // one, a capability row of the last harness, a "this type does not fit" verdict reached without a type table.
        WorldNative.Reset();
        WorldWrites.Reset();
        WorldCapabilities.Reset();
        ResetGameTypeChecks();
        EntitiesFake = new EntitiesFake(this);
    }

    private void EndEntities()
    {
        EntitiesFake?.Dispose();
        EntitiesFake = null;
    }

    // Kiln.Game is not referenced by the kit (a test project brings its own): its first-use check caches a verdict per
    // type, and one reached outside a harness would stand for the whole process.
    private static void ResetGameTypeChecks()
    {
        var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Kiln.Game");
        game?.GetType("Kiln.EcsSupport")?.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
    }
}
