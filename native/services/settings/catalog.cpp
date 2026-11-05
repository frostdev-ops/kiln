// SPDX-License-Identifier: MIT
#include "catalog.h"

#include <cstring>

#include "services/kiln_settings.h"

namespace kset {
namespace {

constexpr double MIN = 60e9;  // one minute in ns
constexpr uint32_t L = KILN_SETTING_F_LIVE, C = KILN_SETTING_F_CLIENT, N = KILN_SETTING_F_NEXT,
                   V = KILN_SETTING_F_VERIFIED;
constexpr uint32_t F = KILN_SETTING_FLOAT, B = KILN_SETTING_BOOL, E = KILN_SETTING_ENUM, D = KILN_SETTING_DURATION,
                   I = KILN_SETTING_INT;

// Order = the game's field order (and enshrouded_server.json's).
const Known kKnown[] = {
    {"playerHealthFactor", "Player", F, 0.25, 4, 1, nullptr, L | C,
     "live: read whenever max health is computed (BalancingRegistry); health_max_scaling runs every step"},
    {"playerManaFactor", "Player", F, 0.25, 4, 1, nullptr, L | C,
     "live: read whenever max mana is computed (BalancingRegistry); mana_max_scaling runs every step"},
    {"playerStaminaFactor", "Player", F, 0.25, 4, 1, nullptr, L | C,
     "live: read whenever max stamina is computed (BalancingRegistry); stamina_max_scaling runs every step"},
    {"playerBodyHeatFactor", "Player", F, 0.5, 2, 1, nullptr, L | C,
     "live: max body heat every step (body_heat_max_scaling, bodyheat_depletion)"},
    {"playerDivingTimeFactor", "Player", F, 0.5, 2, 1, nullptr, L | C,
     "live: max oxygen every step (player_oxygen_max_scaling, oxygen_update)"},
    {"enableDurability", "Survival", B, 0, 1, 1, nullptr, L | C,
     "live: read on every durability loss (durability_loss, actor_spawn_entity)"},
    {"enableStarvingDebuff", "Survival", B, 0, 1, 0, nullptr, L, "live: read by the hunger update (thirst_and_hunger)"},
    {"foodBuffDurationFactor", "Survival", F, 0.5, 2, 1, nullptr, L | N,
     "next food buff: the duration is set when a buff starts (actor_apply_buff)"},
    {"fromHungerToStarving", "Survival", D, 5 * MIN, 20 * MIN, 10 * MIN, nullptr, L,
     "live: read by thirst_and_hunger"},
    {"shroudTimeFactor", "Player", F, 0.5, 2, 1, nullptr, L | C,
     "live: max Shroud time every step (fog_power_max_scaling)"},
    {"tombstoneMode", "Survival", E, 0, 0, 0, "AddBackpackMaterials", L | N,
     "next death: read when the tombstone is filled (player_items_loot)"},
    {"enableGliderTurbulences", "Survival", B, 0, 1, 1, nullptr, L | C,
     "live: read by glider movement (locomotion_execution); players' games simulate it too"},
    {"weatherFrequency", "World", E, 0, 0, 0, "Normal", L | N,
     "live: the server tick passes it to the weather system every frame; new weather follows it"},
    {"fishingDifficulty", "Other", E, 0, 0, 0, "Normal", L | C | N, "next bite (server_player_fishing)"},
    {"miningDamageFactor", "Rates", F, 0.5, 2, 1, nullptr, L | N,
     "next hit: read per mining hit (player_control_action_sequence)"},
    {"plantGrowthSpeedFactor", "Rates", F, 0.25, 2, 1, nullptr, L, "live: read by the growth program"},
    {"resourceDropStackAmountFactor", "Rates", F, 0.25, 2, 1, nullptr, L | N,
     "next loot roll (loot and inventory programs)"},
    {"factoryProductionSpeedFactor", "Rates", F, 0.25, 2, 1, nullptr, L, "live: read by factory_station"},
    {"perkUpgradeRecyclingFactor", "Rates", F, 0, 1, 0.5, nullptr, L | C | N, "next salvage of an upgraded item"},
    {"perkCostFactor", "Rates", F, 0.25, 2, 1, nullptr, L | C | N, "next upgrade (players' games show the cost)"},
    {"experienceCombatFactor", "Rates", F, 0.25, 2, 1, nullptr, L | N, "next XP gain"},
    {"experienceMiningFactor", "Rates", F, 0, 2, 1, nullptr, L | N, "next XP gain"},
    {"experienceExplorationQuestsFactor", "Rates", F, 0.25, 2, 1, nullptr, L | N, "next XP gain"},
    {"randomSpawnerAmount", "World", E, 0, 0, 0, "Normal", L | N,
     "new random spawns: random_spawner reads it every step; enemies already out stay"},
    {"aggroPoolAmount", "Enemies", E, 0, 0, 0, "Normal", L, "live: read when enemies pick who may attack"},
    {"enemyDamageFactor", "Enemies", F, 0.25, 5, 1, nullptr, L, "live: enemy_scaling applies it every step"},
    {"enemyHealthFactor", "Enemies", F, 0.25, 4, 1, nullptr, L | C,
     "live: enemy max health is recomputed every step (enemy_scaling)"},
    {"enemyStaminaFactor", "Enemies", F, 0.5, 2, 1, nullptr, L | C, "live (expected): read by the enemy programs; reader not pinned"},
    {"enemyPerceptionRangeFactor", "Enemies", F, 0.5, 2, 1, nullptr, L,
     "live: read by target selection (enemy01, enemy_combat_targets)"},
    {"bossDamageFactor", "Enemies", F, 0.2, 5, 1, nullptr, L, "live: enemy_scaling applies it every step"},
    {"bossHealthFactor", "Enemies", F, 0.2, 5, 1, nullptr, L | C,
     "live: boss max health is recomputed every step (enemy_scaling)"},
    {"threatBonus", "Enemies", F, 0.25, 4, 1, nullptr, L, "live (expected): read by the enemy programs; reader not pinned"},
    {"pacifyAllEnemies", "Enemies", B, 0, 1, 0, nullptr, L, "live: the pacify program reads it every step"},
    {"tamingStartleRepercussion", "Other", E, 0, 0, 0, "LoseSomeProgress", L | N, "next startle (wildlife)"},
    {"dayTimeDuration", "Time", D, 2 * MIN, 60 * MIN, 30 * MIN, nullptr, L | C | V,
     "live: the day/night clock takes it at the next step (server_update_daytime); players' clocks follow the resend"},
    {"nightTimeDuration", "Time", D, 2 * MIN, 60 * MIN, 12 * MIN, nullptr, L | C | V,
     "live: the day/night clock takes it at the next step (server_update_daytime); players' clocks follow the resend"},
    {"curseModifier", "Enemies", E, 0, 0, 0, "Normal", L | C | N, "next curse roll"},
    {"flameLevelFogRemoval", "World", I, 0, 20, 11, nullptr, L | C,
     "live: read by the placement check (server_cursor_no_prediction); players' games check it too"},
};

int icmp(const char* a, const char* b) {
    for (;; ++a, ++b) {
        int ca = (*a >= 'A' && *a <= 'Z') ? *a + 32 : *a, cb = (*b >= 'A' && *b <= 'Z') ? *b + 32 : *b;
        if (ca != cb || !ca) return ca - cb;
    }
}

}  // namespace

const Known* known(const char* name) {
    for (const Known& k : kKnown)
        if (!icmp(k.name, name)) return &k;
    return nullptr;
}

const Known* known_at(uint32_t i) { return i < known_count() ? &kKnown[i] : nullptr; }
uint32_t known_count() { return static_cast<uint32_t>(sizeof kKnown / sizeof kKnown[0]); }

}  // namespace kset
