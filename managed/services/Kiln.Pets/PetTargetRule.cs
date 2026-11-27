// SPDX-License-Identifier: MIT
// The pets' target rule for orders (19d §4.4): an explicit attack (and any assist a mod orders) is checked against the
// same rule the native module applies to every hit, before a lease or any threat. The rule is the definition's own:
// PetTargetPolicy (its hostile factions, its PvP mode) and the pairs a mod declared hostile.
namespace Kiln;

internal static class PetTargetRule
{
    /// <summary>Factions never fair (Player, AutomatedPlayer, PlayerAlly, FarmAnimal): kiln.pets' native kNeverFair, which
    /// also refuses a binding whose hostile mask names one. Pets.Define checks a definition's list against it.</summary>
    internal const uint NeverFair = (1u << 7) | (1u << 8) | (1u << 11) | (1u << 12);

    internal static (CharacterKey, CharacterKey) Pair(CharacterKey a, CharacterKey b) =>
        string.CompareOrdinal(a.Text, b.Text) <= 0 ? (a, b) : (b, a);

    /// <summary>Null: the pet may attack it; else why not (TargetProtected, TargetGone).</summary>
    public static string? Check(PetState pet, PetDefinition def, EntityRef target, PetTargetInfo info, PetState? targetPet,
        PetDefinition? targetDef, IReadOnlySet<(CharacterKey, CharacterKey)> pairs)
    {
        if (!info.Exists || !info.Alive) return "TargetGone: the target is not there";
        if (targetPet is not null)
        {
            if (targetPet.Owner == pet.Owner) return "TargetProtected: a pet of the same owner";
            if (def.Targets.Pvp == PetPvpMode.ByModRule && targetDef?.Targets.Pvp == PetPvpMode.ByModRule && pairs.Contains(Pair(pet.Owner, targetPet.Owner))) return null;
            return "TargetProtected: another character's pet";
        }
        if (info.IsPlayer)
        {
            if (def.Targets.Pvp == PetPvpMode.ByModRule && !info.Player.IsNone && pairs.Contains(Pair(pet.Owner, info.Player))) return null;
            return "TargetProtected: players are protected from pets (PvP off, or no pair declared)";
        }
        if (!info.EnemyAi) return "TargetProtected: not an unowned enemy creature";
        var f = (byte)info.Faction;
        if (f >= 32 || (NeverFair >> f & 1u) != 0) return $"TargetProtected: a {info.Faction} creature";
        if ((def.Targets.HostileMask >> f & 1u) == 0) return $"TargetProtected: {info.Faction} is not a faction this pet fights";
        return null;
    }
}
