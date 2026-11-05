// SPDX-License-Identifier: MIT
// Compiles mods' quests/*.json (every quest and dialogue unless it says "journal": "chat" or "native": false, NPCs, new
// knowledge) into the game's resources (questbuild.h). Portable: tools/kfc/tests runs it on Linux. docs/kiln/services/npcs.md.
#pragma once
#include <functional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "questbuild.h"

namespace kiln::qb {

using ojson = nlohmann::ordered_json;

struct ModInput {
    std::string id;                  // the manifest's id
    std::vector<ojson> documents;    // quests/*.json, then quests/generated/*.json and what code defined (kiln/data/kiln-quests/native/<id>.json)
    std::vector<std::string> files;  // parallel: names for messages
    ojson audio;                     // assets/cooked/audio.json (null when the mod has none)
    std::string guid_namespace;      // assets/cook.json's guidNamespace ("" when none)
};

// Read access to the game's resources.
struct GameData {
    std::function<bool(const Guid&, uint32_t type, const uint8_t** data, size_t* len)> read;
    // template name -> GUID (false: unknown). Names resolve in the same data the game loads.
    std::function<bool(const std::string& name, Guid* out)> template_by_name;
};

struct TemplateOut {
    Guid guid;
    std::string name;
    bool is_new = false;
    Bytes data;
};
struct TextOut {
    uint32_t tag = 0;
    std::string json;  // {"en": "...", "de": "..."}
};
struct PackOut {
    bool any = false;          // something native to build
    bool ok = true;
    std::vector<std::string> errors, warnings;
    std::vector<KnowledgeDef> knowledge;
    std::vector<JournalQuestDef> quests;
    std::vector<NpcDef> npcs;
    Bytes res_knowledge, res_journal, res_npcs;
    std::vector<TemplateOut> templates;
    std::vector<TextOut> texts;
    std::vector<std::string> content_items, content_recipes;  // definitions for kiln.content's define()
    ojson manifest;            // what Kiln.Quests/Native needs at run time (schema: npcs.md)
};

constexpr const char* kGuidKnowledge = "cb1eeb8b-f58a-4785-bf0f-6dd5437e9a5b";
constexpr const char* kGuidJournal = "33701b26-ec1d-423f-8e06-49f023b91b7f";
constexpr const char* kGuidNpcs = "377f61dc-c7b8-4e4f-830f-7d2c3e8b7fd8";

// The deterministic id of a mod's named thing (knowledge object, line, tag, ...).
Guid derive_guid(const std::string& mod, const std::string& kind, const std::string& name);
inline uint32_t derive_id(const std::string& mod, const std::string& kind, const std::string& name) {
    return fnv(derive_guid(mod, kind, name));
}

// Compiles and builds. Errors are collected: when any is reported, nothing of that mod is used (the others are built).
PackOut compile_pack(const std::vector<ModInput>& mods, const GameData& game);

}  // namespace kiln::qb
