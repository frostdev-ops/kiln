// SPDX-License-Identifier: MIT
// kiln.skills shadow table: unlock state of skill-tree nodes past the game's caps (index >= 256 or no level byte),
// kept by Kiln and answered through hooks on the game's own skill functions (shadow.cpp; docs/kiln/services/skills.md).
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "kiln_skills.h"
#include "tree.h"

namespace kiln::skills::shadow {

// Resolves the symbols and installs the hooks (kiln_plugin_main). Returns true when the server-side set is complete.
bool init(const KilnApi* api, KilnOwner owner);
// on_ready: kiln.world (players' component addresses) and kiln.client (the local player on a client).
void ready();
// 1 server-side hooks on, 2 also the client's screen hook, 0 off.
uint32_t level();
std::string why_off();
// The shadow nodes of the tree Kiln serves now (after each build).
void set_tree(const std::vector<ShadowNode>& nodes);
// Server world Pre (tick thread): which component addresses belong to which player.
void pre_server();
// Client world Pre (main thread): the local player's addresses.
void pre_client();
// Login guard: a shadow node's id found in a player's UnlockedSkillNodes (component at `unlocked`) moves into the table.
bool migrate(const void* unlocked, uint32_t node, uint8_t level_byte);
bool is_shadow(uint32_t node);
// The game's join check runs on this thread while on: PointsSpent counts native nodes only, so shadow costs can never
// make the game reset a valid native tree. Kiln.Skills checks the shadow nodes' budget itself (GameShadow.CheckBudget).
void native_costs_only(bool on);

// table
KilnResult t_nodes(uint32_t* out, uint32_t cap, uint32_t* n);
KilnResult t_get(uint32_t world, KilnEntity e, KilnSkillsShadowEntry* out, uint32_t cap, uint32_t* n);
KilnResult t_set(uint32_t world, KilnEntity e, const KilnSkillsShadowEntry* in, uint32_t n);
KilnResult t_changes(uint64_t after, KilnSkillsShadowChange* out, uint32_t cap, uint32_t* n);
KilnResult t_spent(KilnEntity e, int32_t* out);
KilnResult t_is_unlocked(KilnEntity e, uint32_t node, int32_t* unlocked, int32_t* reachable);
KilnResult t_scope(uint32_t world, KilnEntity e);
KilnResult t_level(uint32_t world, KilnEntity e, uint32_t node, uint32_t* level, uint32_t* max_level);
KilnResult t_reset(uint32_t world, KilnEntity e, uint32_t* levels);
KilnResult t_linked(uint32_t world, KilnEntity e, const void* ui, void* tree, uint32_t without, int32_t* ok);

}  // namespace kiln::skills::shadow
