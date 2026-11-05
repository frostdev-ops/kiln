// SPDX-License-Identifier: MIT
// kiln.skills: definitions of game-tree nodes and the builder of the expanded keen::SkillTreeResource (no game calls:
// pure byte work on a copy of the game's resource, so it is the same on the server and on every client).
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "nlohmann/json.hpp"

namespace kiln::skills {
using json = nlohmann::json;

uint32_t fnv1a(const void* p, size_t n, uint32_t h = 0x811c9dc5u);
bool guid_parse(const std::string& s, uint8_t out[16]);  // .NET Guid.ToByteArray() order (the game's)
std::string guid_str(const uint8_t g[16]);
std::string hex32(uint32_t v);

// keen::SkillTreeNode (140 B) and keen::SkillTreeNodeLink (16 B), from the game's reflection.
constexpr size_t kNode = 0x8c, kLink = 0x10;
namespace F {
constexpr size_t id = 0x00, pos = 0x04, name = 0x0c, desc = 0x10, perlvl = 0x14, ability_ref = 0x18, ability_id = 0x28,
                 icon_in = 0x2c, icon_act = 0x3c, impact = 0x4c, ui = 0x5c, cfg_simple = 0x64, cfg_scaled = 0x6c,
                 in = 0x74, out = 0x7c, cost = 0x84, type = 0x86, cls = 0x87, max_level = 0x88, level_index = 0x89;
constexpr size_t kBlobArrays[5] = {ui, cfg_simple, cfg_scaled, in, out};
}  // namespace F

// keen::SkillNodeType / keen::ClassType
enum : uint8_t { kRoot = 0, kAttribute = 1, kActive = 2, kPassive = 3 };

struct NodeDef {
    uint8_t guid[16] = {};
    uint32_t id = 0;
    std::string name;
    int type = -1;     // -1 = the like node's (else PassiveSkill)
    int cls = -1;      // -1 = the like node's (else Neutral)
    int cost = 1;
    int max_level = -1;  // -1 = the like node's (0 without one)
    bool has_pos = false;
    float x = 0, y = 0;
    std::string near_ref;
    float dx = 140, dy = 0;
    bool has_offset = false;
    std::vector<std::string> from, to;
    bool bidirectional = false;
    std::string like;
    bool has_program = false;
    uint8_t program[16] = {};
    std::vector<std::pair<uint32_t, double>> configs, scales;
    bool has_icon = false;
    uint8_t icon[16] = {};
    std::string icon_file;   // resolved by the service (kiln.assets); then has_icon/icon are set
    json texts;              // {"en": {"name", "description", "perLevel"}, ...} or null
    bool has_ability = false;
    uint8_t ability[16] = {};
};

enum DefState { kDefined = 0, kInTree = 1, kError = 2, kOrphaned = 3, kPending = 4 };

struct Def {
    NodeDef node;
    KilnOwner owner = 0;
    std::string owner_id;   // mod id when known
    std::string source;     // file or "code"
    int state = kDefined;
    std::string error;
    int index = -1;         // tree index in the last build
    int level_index = -1;
    bool shadow = false;    // past the game's caps: its state lives in Kiln's shadow table (index >= 256)
    uint32_t tag[3] = {};   // name / description / perLevel LocaTagIds used
};

// Parses one node object. why = the reason when false.
bool parse_node(const json& j, NodeDef* d, std::string* why);

// A node past the game's caps (index >= 256, or no level slot left): what the shadow hooks need to answer for it.
struct ShadowNode {
    uint32_t id = 0;
    uint16_t cost = 0;
    uint8_t max_level = 0;
    uint32_t index = 0;
    std::vector<std::pair<uint32_t, bool>> from;  // incoming link sources (id, is a root): unlocked or root -> reachable
    std::vector<uint32_t> to;                     // outgoing link targets: unlocked -> reachable
};

struct Built {
    std::vector<uint8_t> bytes;
    std::vector<ShadowNode> shadow;
    uint32_t vanilla_nodes = 0, vanilla_links = 0, level_max = 0;
    uint32_t nodes = 0, links = 0, added_nodes = 0, added_links = 0, free_levels = 0, shadow_nodes = 0;
    std::vector<std::string> problems;  // warnings and per-definition errors (also set on the defs)
    bool ok = false;                     // the vanilla resource parsed
};

// Derived ids (deterministic from the node GUID): LocaTag ids for name / description / perLevel, link ids, icon GUID.
uint32_t text_tag(const uint8_t guid[16], int slot);
void icon_guid(const uint8_t guid[16], uint8_t out[16]);

// Builds the expanded resource from the game's bytes and the live definitions (state kDefined / kInTree). Sets each
// definition's state, index, level index and error. force_copy: rebuild the node and link arrays even with no
// additions (the round-trip test of the copy path).
// texts_ok: the service can hand node texts to the game's loca (else new nodes use their like node's texts).
// shadow_ok: the shadow hooks are on, so nodes past the game's caps (256 nodes, 100 level slots) go in as shadow
// nodes (index >= 256, up to kMaxNodes); without them such definitions are refused.
constexpr uint32_t kMaxNodes = 4096;
Built build(const uint8_t* v, size_t len, std::vector<Def*>& defs, bool force_copy = false, bool texts_ok = true,
            bool shadow_ok = false);

// Parses `n` back and compares it with the game's `v`: every game node's fields and sub-arrays, every game link, and
// the added nodes' sub-arrays and links. JSON {"ok", "nodes", "links", "added", "problems": [...]}.
json verify(const uint8_t* v, size_t vlen, const uint8_t* n, size_t nlen);

// Summary of a resource: {"nodes", "links", "levelMax", "ids": [...]} (for logs and tests).
json summary(const uint8_t* p, size_t len);

}  // namespace kiln::skills
