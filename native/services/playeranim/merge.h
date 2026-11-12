// SPDX-License-Identifier: MIT
// kiln.playeranim, portable part: every mod's player states merged into the player's animation graph, the port of
// tools/animcook/anicook/playergraph.py. It is compared with the Python byte for byte and report for report
// (tests/run.sh), so it follows the Python step for step: the rules are numbered in playergraph.py's docstring and in
// the comments of merge.cpp. Nothing in it depends on the order mods or files are given.
// Fails closed: a merge that cannot be proven gives no graph and every state "refused" with the reason; a wrong
// definition refuses that state only. No Windows, no game, no Kiln API: tests/ runs it on the host.
#pragma once
#include <cstdint>
#include <functional>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "blob.h"
#include "nlohmann/json.hpp"
#include "states.h"
#include "typedb.h"

namespace kpa {

constexpr uint32_t kStateInput = 0x5D2BFB0Au;  // the id input every graph compares states against
constexpr size_t kMaxGlobal = 34;              // StateMachineNodeDefinition.globalTransitions is a StaticArray of 34
constexpr uint64_t kMaxArray = uint64_t{1} << 26;
#define KPA_NODE "keen::anim_graph::runtime_graph::"
constexpr const char* kGraphType = KPA_NODE "AnimationGraphResource2_0";
constexpr const char* kGtType = KPA_NODE "StateMachineGlobalTransitionInfo";
constexpr const char* kJointAnim = "keen::JointAnimation";
constexpr const char* kRootMotion = "keen::RootMotionAnimation";
constexpr const char* kSequences = "keen::AnimationSequenceContainer";
constexpr const char* kPlayerGraph = "21319e83-5087-4fff-8795-22997843649e";  // the resource the merge reads and serves
constexpr const char* kZeroGuid = "00000000-0000-0000-0000-000000000000";

// What the merge needs of a clip, from its resources (playergraph.ClipFacts): the JointAnimation's header, and whether a
// RootMotionAnimation and an AnimationSequenceContainer are served under the same GUID.
struct ClipFacts {
    uint64_t hierarchy_hash = 0, joint_count = 0, fps = 0, frame_count = 0;
    bool loop = false, root_motion = false, sequences = false;
    bool operator==(const ClipFacts&) const = default;
};
// clip_facts(guid text, lower case) -> facts, or nullopt: not served. It may throw PyError (the merge goes off).
using ClipFactsFn = std::function<std::optional<ClipFacts>(const std::string& guid)>;

// The JointAnimation header's facts from the resource's bytes (playergraph.ClipFacts._header; root_motion and
// sequences false). Throws as the Python does: PyError("MergeOff") when a field is missing, "error" (struct.error) when
// the bytes are short, "KeyError" when a field is not an integer. ClipFacts catches the first two for a mod's file and
// lets every one through for the game's: a provider follows that rule (see the report of part 1).
ClipFacts joint_animation_facts(const TypeDb& types, const uint8_t* data, size_t len);

// The game's graph: bytes given, or a reader the merge calls only when a state is left to merge (so a boot with no
// state reads nothing), or neither ("no graph to merge into").
struct GraphRead {
    enum Kind { Ok, None, Error } kind = None;
    Bytes bytes;
    std::string error;  // Error: why it cannot be read
};
struct GraphSource {
    const Bytes* bytes = nullptr;
    std::function<GraphRead()> read;
};

// Test seams: id rules, and a change to the bytes of the n-th encode (1-based), as the Python tests patch them.
struct MergeHooks {
    IdHooks ids;
    void (*after_encode)(int call, Bytes* out) = nullptr;
    std::vector<std::string> drop_first;  // node kinds the graph is taken to have no template of
};

struct MergeResult {
    std::optional<Bytes> graph;     // the merged graph, only when report["graph"] is "merged"
    nlohmann::ordered_json report;  // playergraph's report, field for field
    std::vector<State> states;      // every state with its status and reason, by mod id then name (the report's order)
};

MergeResult merge(const GraphSource& raw, const TypeDb& types, const std::vector<ModStates>& mods, const ClipFactsFn& clip_facts,
                  const MergeHooks* hooks = nullptr);

// The report's graphHash: the first 8 bytes of SHA-256 over the merged graph, 16 lower-case hex digits.
std::string graph_hash(const Bytes& merged);

// Rule 10: FNV-1a-32 of "kiln.player.<parts joined by dots>".
uint32_t node_id(const std::vector<std::string>& parts);

// The checks of a merged graph (playergraph.validate). Throws PyError("MergeOff", ...) with the first failure.
void validate(const TypeDb& types, const Value& pristine, const Value& merged, const std::set<uint64_t>& touched_machines,
              const std::set<uint64_t>& touched_lists, const std::set<uint64_t>& touched_selects, const std::set<uint64_t>& new_ids);

// The supported neighbours (playergraph.SUPPORTED): machine names in the table's order, and for a machine whose row
// names states, those states. The only names of the game in this code.
struct SupportRow {
    const char* machine;
    const char* kind;                 // the entry kind (merge.cpp's ENTRIES): "state" or "overlay"
    bool every_state;                 // the Python's None: every state of the machine
    std::vector<const char*> states;  // else these
};
const std::vector<SupportRow>& supported();

}  // namespace kpa
