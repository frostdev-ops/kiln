// SPDX-License-Identifier: MIT
// Kiln's own policies that write a component of an entity again every step (kiln.items' water refills: Items.Fuel.Refill,
// KeepFull, a player's Water rule). kiln.world's tracked writes ask them who wrote over a write that did not hold, so the
// answer names the policy that did instead of blaming the game (WorldWriteResult.WrittenOverBy); a service names one
// only when it can show that policy wrote over the written bytes in the step the write was checked. Kiln.World and Kiln.Items do not
// reference each other: a service adds its keeper here when it starts its first such policy.
namespace Kiln;

internal static class ComponentKeepers
{
    /// <summary>One service's answer for a tracked write of <paramref name="component"/> of <paramref name="entity"/> that
    /// was not kept: a phrase naming its policy that wrote over it ("kiln.items' water refill 3 (keep full) of the
    /// inventories of 1, set by kiln"), or null when it cannot show that one of its policies did. <paramref name="offset"/>
    /// and <paramref name="written"/>: the bytes the write left in the component (WorldWriteResult.Offset, After). Asked
    /// in the Pre the write's answer arrives in. Tick thread.</summary>
    internal delegate string? Keeper(EntityId entity, string component, int offset, ReadOnlyMemory<byte> written);

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

    /// <summary>The first keeper's phrase, or null when no policy of Kiln's is shown to have written over the write. A
    /// keeper that throws answers nothing. Tick thread.</summary>
    internal static string? Who(EntityId entity, string component, int offset, ReadOnlyMemory<byte> written)
    {
        foreach (var keeper in Volatile.Read(ref _keepers))
        {
            try
            {
                if (keeper(entity, component, offset, written) is { Length: > 0 } who) return who;
            }
            catch (Exception)
            {
                // a service that cannot tell (its native side gone, a read off the tick thread) is no answer
            }
        }
        return null;
    }
}
