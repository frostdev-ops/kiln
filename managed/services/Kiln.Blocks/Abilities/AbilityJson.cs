// SPDX-License-Identifier: MIT
// The sections of blocks/*.json for perks, abilities and talents: BlockJson reads them.
using System.Text.Json;

namespace Kiln;

internal static class AbilityJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("perks", BlockJson.Make<PerkBlock>), ("abilities", BlockJson.Make<AbilityBlock>), ("talents", BlockJson.Make<TalentBlock>), ("talentTrees", BlockJson.Make<TalentTreeBlock>), ("triggers", BlockJson.Make<TriggerBlock>),
    ];
}
