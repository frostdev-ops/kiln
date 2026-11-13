// SPDX-License-Identifier: MIT
// Kiln's own policies that write a component of an entity again every step (kiln.items' water refills: Items.Fuel.Refill,
// KeepFull, a player's Water rule). kiln.world's tracked writes ask them who wrote over a write that did not hold, so the
// answer names the policy instead of blaming the game (WorldWriteResult.WrittenOverBy). Kiln.World and Kiln.Items do not
// reference each other: a service adds its keeper here when it starts its first such policy.
namespace Kiln;

internal static class ComponentKeepers
{
    /// <summary>One service's answer: a phrase naming its policy that writes <paramref name="component"/> of
    /// <paramref name="entity"/> every step ("kiln.items' water refill 3 (keep full) of the inventories of 1, set by
    /// kiln"), or null when none of its policies does. Tick thread.</summary>
    internal delegate string? Keeper(EntityId entity, string component);

    private static readonly object Gate = new();
    private static Keeper[] _keepers = [];

    /// <summary>Adds a keeper once (an equal delegate is not added again). Any thread.</summary>
    internal static void Add(Keeper keeper)
    {
        ArgumentNullException.ThrowIfNull(keeper);
        lock (Gate)
            if (Array.IndexOf(_keepers, keeper) < 0) _keepers = [.. _keepers, keeper];
    }

    /// <summary>Tests: takes a keeper out again.</summary>
    internal static void Remove(Keeper keeper)
    {
        lock (Gate) _keepers = [.. _keepers.Where(k => !k.Equals(keeper))];
    }

    /// <summary>The first keeper's phrase for the entity's component, or null when no policy of Kiln's writes it. A keeper
    /// that throws answers nothing. Tick thread.</summary>
    internal static string? Who(EntityId entity, string component)
    {
        foreach (var keeper in Volatile.Read(ref _keepers))
        {
            try
            {
                if (keeper(entity, component) is { Length: > 0 } who) return who;
            }
            catch (Exception)
            {
                // a service that cannot tell (its native side gone, a read off the tick thread) is no answer
            }
        }
        return null;
    }
}
