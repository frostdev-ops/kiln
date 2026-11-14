// SPDX-License-Identifier: MIT
// Blocks' face for a weapon's moves with follow-ups: Items/MoveFollowUps.cs, Items/MoveApplications.cs. docs/kiln/modding/item-blocks.md, "Follow-ups".
namespace Kiln;

public static partial class Blocks
{
    /// <summary>Where a weapon's moves with follow-ups are: waiting for the game's data, applied, refused (and why), or removed with the mod.
    /// A snapshot: it does not change; ask again, or subscribe to <see cref="WeaponMovesApplicationChangedEvent"/>. A weapon with follow-ups applies all
    /// its moves at once or none, and a refused attempt leaves the last applied one playing (<see cref="MoveApplicationResult.AppliedGeneration"/>).</summary>
    /// <exception cref="ArgumentException">The handle is another mod's, is not the block the mod has under that name now (a reload made a new one), or its moves have no follow-ups.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.move_followups</c> is off here.</exception>
    [AnyThread, RequiresCapability("blocks.move_followups")]
    public static MoveApplicationResult MovesStatus(BlockHandle<WeaponBlock> weapon)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        BlockCapabilities.RequireFollowUps(query: true);
        if (BlocksBridge.CallerModId is { } caller && caller != weapon.Mod)
            throw new ArgumentException($"{weapon} is a block of {weapon.Mod}: a mod asks for the status of its own weapons", nameof(weapon));
        if (!Mods_.TryGetValue(weapon.Mod, out var mod) || !mod.Handles.TryGetValue(weapon.Name, out var live) || !ReferenceEquals(live, weapon))
            throw new ArgumentException($"{weapon} is not the block {weapon.Mod} has under that name now (the mod reloaded, or the handle was not made by Blocks.Define)", nameof(weapon));
        return MoveApplications.Status(weapon.Mod, weapon.Name)
            ?? throw new ArgumentException($"{weapon}: its moves have no followUps, so they are not applied as a generation and have no status", nameof(weapon));
    }
}
