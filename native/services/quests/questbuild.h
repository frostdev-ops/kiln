// SPDX-License-Identifier: MIT
// Resource builders for mods' native quests and NPCs (docs/kiln/services/npcs.md, evidence docs/re/quests.md and
// docs/re/creatures.md section 7): the game's GameKnowledgeResource, JournalRegistryResource, NpcCollection and
// ecs::TemplateResource (NpcDialog, clones), rebuilt from the game's own bytes plus a mod's definitions.
//
// Portable on purpose: no Windows, no game memory, no Kiln API. kiln-quests.dll runs it at boot on the bytes the
// resource service holds; tools/kfc/tests/test_questbuild.py compiles the same code on Linux and runs it on the bytes
// tools/kfc reads from the client's files, then decodes the result with the game's reflection.
//
// A resource is one struct followed by its out-of-line data; every offset in it is self-relative, so a copy is valid
// anywhere, and growing an array needs no re-encoding: the new array goes behind the old data and the BlobArray field
// is pointed at it. The old array stays as unused bytes.
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace kiln::qb {

using Bytes = std::vector<uint8_t>;

// A GUID in the game's byte order (.NET Guid.ToByteArray(): the bytes of ItemInfo.objectId).
struct Guid {
    uint8_t b[16] = {};
    bool zero() const;
    bool operator==(const Guid& o) const;
};
uint32_t fnv1a(const void* p, size_t n);
inline uint32_t fnv(const Guid& g) { return fnv1a(g.b, 16); }
bool parse_guid(const std::string& text, Guid* out);
std::string guid_text(const Guid& g);
// RFC 4122 version 5 (Python's uuid.uuid5(ns, name)): what tools/soundcook uses for cues and voice-overs.
Guid uuid5(const Guid& ns, const std::string& name);

// Fixed type hashes and sizes of svn 1287369 (the builders check what they can against the data they are given).
constexpr uint32_t kTypeKnowledge = 0xbc47511cu;   // keen::GameKnowledgeResource
constexpr uint32_t kTypeJournal = 0x60b5ed8au;     // keen::JournalRegistryResource
constexpr uint32_t kTypeNpcs = 0x43ae2af5u;        // keen::NpcCollection
constexpr uint32_t kTypeTemplate = 0x39768775u;    // keen::ecs::TemplateResource
constexpr uint32_t kCompNpcDialog = 0xa9ad90e6u;   // keen::ecs::NpcDialog
constexpr uint32_t kCompActiveNpc = 0x1a8b5909u;   // keen::ecs::ActiveNpcState
constexpr uint32_t kCompLocaTag = 0x59dc15fdu;     // keen::ecs::LocaTagComponent
constexpr uint32_t kCompCrafting = 0xad776575u;    // keen::ecs::Crafting
constexpr uint32_t kCompMapMarker = 0x8af070c5u;   // keen::ecs::MapMarker
constexpr uint32_t kCompMarkerReq = 0x3f65cd0bu;   // keen::ecs::RuntimeMapMarkerUnlockRequirements

enum : uint8_t { kBool = 0, kCounter = 1, kFlags = 2 };                         // keen::KnowledgeType
enum : uint8_t { kQueryExtern = 0, kQueryBool = 1, kQueryCount = 2, kQueryFlag = 3 };  // KnowledgeQueryDataType
enum : uint8_t { kCmpGreater = 0, kCmpLess = 1, kCmpEquals = 2 };

// keen::KnowledgeQueryData (12 B)
struct Query {
    uint32_t id = 0, value = 0;
    uint8_t cmp = kCmpGreater, type = kQueryExtern, explicit_player = 0;
};
inline Query query_bool(uint32_t id) { return Query{id, 1, kCmpEquals, kQueryBool, 0}; }
inline Query query_unset(uint32_t id) { return Query{id, 0, kCmpEquals, kQueryCount, 0}; }
// The journal's counter of a step: value / target. target >= 1.
inline Query query_progress(uint32_t id, uint32_t target) { return Query{id, target - 1, kCmpGreater, kQueryCount, 0}; }

// ---- GameKnowledgeResource -----------------------------------------------------------------------------------------
struct KnowledgeDef {
    uint32_t id = 0;
    uint8_t type = kBool;
    bool world = false, player = false;
    uint32_t expected_flags = 0;  // Flags: the mask of flags that exist
    uint32_t experience_id = 0;   // player objects: a BalancingRegistry XP id paid on unlock
    uint8_t skill_points = 0;     // player objects
    std::string name;             // for messages
};
// A copy of `res` with `defs` added to the world and player static maps (bucket count kept, or doubled when the map
// would hold more than the bucket count). An id the game already has in that map is an error: nothing is built.
bool rebuild_knowledge(const uint8_t* res, size_t len, const std::vector<KnowledgeDef>& defs, Bytes* out, std::string* err);
// The objects in the two static maps. The game reserves its run-time maps and change lists for 6,050 world and 11,000 player
// entries (0x99ead0).
constexpr uint32_t kReservedWorld = 6050, kReservedPlayer = 11000;
bool knowledge_counts(const uint8_t* res, size_t len, uint32_t* world, uint32_t* player);
// What the game's lookups do (0x99fee0 / 0x99fdb0): the object, or nullptr. For tests and self-checks.
const uint8_t* knowledge_find(const uint8_t* res, size_t len, bool player, uint32_t id);

// ---- JournalRegistryResource ---------------------------------------------------------------------------------------
struct JournalStepDef {
    uint32_t entry_id = 0, name = 0, text = 0, marker = 0;
    Query visible, done;
    // progressStepsRequirement: one SimpleCount query shows "value / compareValue + 1" in the journal (journal_ui, client 0x30e470)
    bool has_progress = false;
    Query progress;
    Guid voiceover;
    uint32_t item_icon = 0, level = 0;
};
struct JournalQuestDef {
    uint32_t entry_id = 0, name = 0;
    uint32_t lore_category = 0;
    uint32_t document = 0;     // referencedDocumentName: every quest of the game has one
    uint32_t priority = 50;
    uint8_t source = 0, type = 2, main = 0, unlock_all = 0;
    std::vector<JournalStepDef> steps;
};
bool rebuild_journal(const uint8_t* res, size_t len, const std::vector<JournalQuestDef>& quests, Bytes* out, std::string* err);

// ---- NpcCollection -------------------------------------------------------------------------------------------------
constexpr uint32_t kMaxNpcs = 64;  // npc_unlock_state (0xa2ef90) keeps the unlock states in one u64: a bit per entry
struct NpcDef {
    uint32_t id = 0, name = 0;
    uint32_t copy_from = 0;       // the NpcId of the game's entry to start from (preset, portrait, roaming, ...)
    bool has_roaming = false;
    float roaming = 0;
    bool assistant = false;
    Query unlock;
    uint32_t workshop_id = 0, workshop_label = 0;
    bool has_workshop = false;
    uint32_t item_id = 0;
    Guid item_ref;
    std::string debug_name;
};
bool rebuild_npcs(const uint8_t* res, size_t len, const std::vector<NpcDef>& npcs, Bytes* out, std::string* err);
// The game's roster entry by NpcId: nullptr if none.
const uint8_t* npc_find(const uint8_t* res, size_t len, uint32_t id);

// ---- ecs::TemplateResource: NpcDialog lines, clones ----------------------------------------------------------------
struct OpDef {
    uint32_t id = 0;
    uint8_t type = kBool;
    uint32_t value = 1;
};
struct LineDef {
    uint32_t id = 0;
    bool override_seq = false;
    Guid sequence;
    std::vector<OpDef> ops;
    Query requirement;
    std::vector<uint32_t> bodies;  // LocaTagIds; the client keeps at most 32 and stops the game beyond (0x460b40)
    std::vector<Guid> voiceovers;  // parallel to bodies; zero GUID = silent
    uint8_t hint = 0, start_action = 0, end_action = 0;
    uint32_t extra = 0;            // options: optionText; init lines: isWakeup (bool)
};
struct DialogEdit {
    bool any = false;
    bool replace_init = false, replace_options = false, replace_transition = false, replace_farewell = false;
    uint32_t headline = 0;
    std::vector<LineDef> init, transition, farewell, options;
};
struct TemplateEdit {
    // A clone: the base's bytes under a new name. Empty: an override of the base itself.
    std::string new_name;
    bool has_npc_id = false;
    uint32_t npc_id = 0;           // ActiveNpcState.npcId
    bool has_loca = false;
    Guid loca_tag;                 // LocaTagComponent.locaTag
    bool has_workshop = false;
    uint32_t workshop_id = 0;      // Crafting.workshopId (Crafting.workshop follows)
    Guid workshop_ref;
    bool has_marker = false;
    Guid marker_tag;               // MapMarker.markerId
    bool has_marker_req = false;
    Query marker_hint, marker_visible, marker_completed;  // RuntimeMapMarkerUnlockRequirements
    DialogEdit dialog;
};
bool edit_template(const uint8_t* res, size_t len, const TemplateEdit& edit, Bytes* out, std::string* err);
// The template's name (BlobString at +0), for name lookups.
std::string template_name(const uint8_t* res, size_t len);
// The offset of the first component of that type, 0 if none.
size_t template_component(const uint8_t* res, size_t len, uint32_t type_hash, uint32_t* size = nullptr);

}  // namespace kiln::qb
