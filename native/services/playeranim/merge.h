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
#include <map>
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

// playergraph.ClipFacts: a clip's facts from the mods' shared assets/assets.json "resource" entries (part 0), read from
// their files; else from the game. A one-sided clip, root motion or sequences entry refuses that GUID on both sides.
// Two mods serving one clip with other facts:
// not served. Root motion and sequences: served by any mod, or the game has them. A mod's file that cannot be read counts as
// unreadable (that mod's entry); the game's errors go through (the merge goes off), as in the Python.
class ClipFactsProvider {
public:
    struct Mod {
        std::string assets_dir;   // <mod>/assets
        std::string assets_json;  // the text of its assets.json ("" with has_json false: none)
        bool has_json = false;
    };
    struct Game {
        // The game's own resource (part 0) of `type` under `guid`: true and the bytes, or false: not there.
        std::function<bool(const Guid& guid, uint32_t type, Bytes* out)> read;
        std::function<bool(const Guid& guid, uint32_t type)> has;
    };
    // read_file: a mod's file, `assets_dir` + "/" + the entry's "file"; false: cannot be read.
    ClipFactsProvider(const TypeDb& types, const std::vector<Mod>& mods, std::function<bool(const std::string& path, Bytes* out)> read_file, Game game);
    std::optional<ClipFacts> operator()(const std::string& guid);
    std::vector<std::string> warnings;  // assets.json files the Python could not read either (it would stop there)

private:
    const TypeDb& types_;
    std::function<bool(const std::string&, Bytes*)> read_file_;
    Game game_;
    uint32_t ja_ = 0, rm_ = 0, sq_ = 0;
    std::map<std::pair<std::string, uint32_t>, std::vector<std::string>> served_;  // (guid, type hash) -> files
    std::set<std::string> one_sided_;  // no state may sample a GUID whose clip, root motion or sequences differ by side
    std::map<std::string, std::optional<ClipFacts>> cache_;
};

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
    std::string missing_field;            // lab only: a field name the merge writes is taken as missing from every type
};

// Where a merge's time went, in milliseconds (for the log line and the timing test).
struct MergeTimes {
    double read = 0;      // the game's graph decoded twice and encoded once (the round-trip check)
    double accept = 0;    // the graph indexed, the definitions checked, the clip facts asked
    double build = 0;     // the new nodes written
    double validate = 0;  // the merged graph checked
    double encode = 0;    // encoded, decoded back, compared, encoded again
    double total = 0;
};

struct MergeResult {
    MergeTimes times;
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

// The lookup of note player-anim-04 over the game's own graph: a state by "State" or "Machine/State", its machine, how a
// sequence enters it (the machine's kind in SUPPORTED; elsewhere the state input, else an overlay) and every id that does
// (its global transitions' conditions; with none, its local ways in). The graph holds ids, never GUIDs.
struct GameState {
    std::string machine, name, kind;  // kind: "state" or "overlay"
    std::vector<uint32_t> ids;
};
class GameStates {
public:
    // Decodes the game's graph: throws PyError when it does not decode or index.
    GameStates(const TypeDb& types, const Bytes& raw);
    // Every state the name names, in the graph's order (several: the bare name is in several machines).
    std::vector<const GameState*> find(std::string_view name) const;
    size_t size() const { return all_.size(); }

private:
    std::vector<GameState> all_;
};

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
