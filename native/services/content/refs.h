// SPDX-License-Identifier: MIT
// kiln.content: reference fields ("kitbash", refs.cpp). An item's GUID references (model, visual template, icon setup,
// perks, weapon category, impact entity, program, ...) pointed at resources the game already loaded, validated by type.
#pragma once
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

#include "content.h"
#include "nlohmann/json.hpp"

namespace kc {

// An item selector (debug name, "0x" id, GUID, a definition's name) -> its ItemInfo in the world being worked on.
using ItemFind = std::function<uintptr_t(const std::string& selector)>;

struct RefWrite {
    const Field* f = nullptr;
    uint8_t bytes[16] = {};
};

// A BlobArray header pointed at another item's blob data (the model bundle's per-body models and pickup models).
struct BlobWrite {
    std::string path;
    uint32_t offset = 0;
    uintptr_t data = 0;  // 0 = empty list
    uint32_t count = 0;
};

struct KitbashOut {
    std::vector<RefWrite> writes;
    std::vector<BlobWrite> blobs;
    std::vector<std::string> problems;  // ops that were skipped (unknown item, wrong type, GUID not loaded, ...)
    bool icon = false;                  // "icon": the UI icon texture is looked up under icon_guid
    uint8_t icon_guid[16] = {};
};

// The keys an item definition / rule may use for references: model visual icon perks weaponType impact program
// animation collectionModel refs, or the path of any reference field ("equipment.visualEntity").
bool is_kitbash_key(const Schema& s, const std::string& key);

// Expands one key into field writes for the ItemInfo schema `s`. `items`: every ItemInfo of the world (the pools that
// validate GUIDs come from them). Values:
//   "item:<selector>"            that item's value of the same field (bundles: every field of the bundle)
//   "item:<selector>.<path>"     another field of that item, of the same reference type
//   "<guid>"                     a GUID that some loaded item uses in a field of the same reference type (templates:
//                                any template the engine loaded); "none" clears the reference
// `self`: the item being changed (its current values decide where a bare model GUID goes), 0 = unknown.
void kitbash(const Schema& s, const std::string& key, const nlohmann::ordered_json& value, const ItemFind& find,
             const std::vector<uintptr_t>& items, KitbashOut* out, uintptr_t self = 0);

// Pools (GUID sets per reflected reference type) are built from the world's items on first use; a world reset drops
// them. Diagnostics: "<type>: n GUIDs" per pool, and how many perk slots satisfy perkIds = FNV-1a(perkReferences).
void refs_reset();

// packs.cpp: one kitbash key applied as `owner`'s layers on a registered item (store_mutex held; flush after). Game
// items need allow_game (the opt-in); new items (definitions) never do. Problems are appended.
KilnResult apply_kitbash(KilnOwner owner, uint64_t seq, const Entry& e, const std::string& key, const nlohmann::ordered_json& value,
                         bool allow_game, const std::string& source, std::vector<std::string>* problems);
std::vector<uintptr_t> registry_items();
std::string refs_info_json(const Schema& s, const std::vector<uintptr_t>& items);
std::vector<std::filesystem::path> mod_folder_list();  // every enabled mod folder, in load order

}  // namespace kc
