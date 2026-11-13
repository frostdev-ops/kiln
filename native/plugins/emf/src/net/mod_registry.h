// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/mod_registry.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// What the loader found in emf/mods: every mod with its manifest data and what became of it (the Mods tab, the
// "mods" command and the API's mod_info read it). Thread-safe.
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace emf::core {

enum class ModStatus { Loaded, Failed, Disabled, Rejected, Script, Pending };

inline const char* mod_status_name(ModStatus s) {
    switch (s) {
        case ModStatus::Loaded: return "loaded";
        case ModStatus::Failed: return "failed";
        case ModStatus::Disabled: return "disabled";
        case ModStatus::Rejected: return "not loaded";
        case ModStatus::Pending: return "loading";
        default: return "script";
    }
}

struct ModRecord {
    std::string id, name, version, description, authors;  // authors joined with ", "
    std::string source;  // "clock\" for a folder mod, "Loose_Mod.dll" for a loose DLL
    std::string reason;  // why it is disabled, rejected or failed ("" when loaded)
    ModStatus status = ModStatus::Loaded;
    bool has_manifest = false;
    bool enabled_default = true;  // the manifest's "enabled" (the switch's value while the ini has none)
    std::string main;             // script entry for a Script mod ("main.lua")
    std::string side;             // the manifest's "side" in lower case ("" without it)
};

class ModRegistry {
public:
    void set(std::vector<ModRecord> records) {
        std::lock_guard<std::mutex> l(m_);
        records_ = std::move(records);
    }
    std::vector<ModRecord> list() const {
        std::lock_guard<std::mutex> l(m_);
        return records_;
    }
    // The record that owns `id`: a second mod with the same id (a copied folder) is rejected and never wins.
    std::optional<ModRecord> find(std::string_view id) const {
        std::lock_guard<std::mutex> l(m_);
        const ModRecord* any = nullptr;
        for (const auto& r : records_) {
            if (r.id != id) continue;
            if (r.status != ModStatus::Rejected) return r;
            if (!any) any = &r;
        }
        return any ? std::optional<ModRecord>(*any) : std::nullopt;
    }
    // Replaces the record with the same source (the loader updates each mod as it loads).
    void update(const ModRecord& record) {
        std::lock_guard<std::mutex> l(m_);
        for (auto& r : records_)
            if (r.source == record.source) r = record;
    }

private:
    mutable std::mutex m_;
    std::vector<ModRecord> records_;
};

}  // namespace emf::core
