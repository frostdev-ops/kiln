// SPDX-License-Identifier: MIT
// kiln.content registry stage: what gamedata_bootpatch.cpp's resources-ready apply asks of the registry (slices S10, S11).
// Plain hooks set by kiln-content.dll (boot_bridge.cpp, from stage::bind_service): the resource patches' unit links none of
// the registry's code, so its host tests run it as before without the hooks (nothing of the registry runs then).
//   run      the stage, before any patch of this start is written: the old documents are the old-path writes below it;
//            answers the resources the registry writes (restart fields)
//   write    the registry's writes on one resource's copy, after the old documents (evaluated over what they left there)
//   finish   what each boot field became; registry.json, phase START, the ready callbacks
// Threads: set() once when the DLL binds; the hooks run on the resources-ready thread.
#pragma once
#include <atomic>
#include <functional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "stage.h"

namespace kc::registry::boothook {

// One old document of the published boot plan, as the stage reads it.
struct OldDoc {
    std::string mod, key;
    KilnAssetKey res{};
    std::string type;
    std::vector<std::pair<std::string, nlohmann::json>> sets;  // "set": path -> value or op
    struct List {
        char op = 0;  // 'x' clear, 'r' replace, 'a' append
        std::string path;
        std::vector<nlohmann::json> values;
    };
    std::vector<List> lists;
};

struct Hooks {
    std::function<std::vector<stage::BootResource>(const std::vector<OldDoc>&)> run;
    std::function<size_t(const KilnAssetKey& key, const std::string& type, std::vector<uint8_t>* copy, const stage::BootResource& rb,
                         std::vector<stage::BootOutcome>* outcomes, std::vector<std::string>* notes)>
        write;
    std::function<void(const std::vector<stage::BootOutcome>&)> finish;
    // the restart fields of the hooks registries (one BootResource of type model::kHooksType, no resource): Kiln's hooks take the
    // values (limits.h); returns how many were set
    std::function<size_t(const stage::BootResource& rb, std::vector<stage::BootOutcome>* outcomes, std::vector<std::string>* notes)> hooks;
};

inline std::atomic<const Hooks*>& slot() {
    static std::atomic<const Hooks*> s{nullptr};
    return s;
}
inline void set(const Hooks* h) { slot().store(h, std::memory_order_release); }
inline const Hooks* get() { return slot().load(std::memory_order_acquire); }

// The bridge's own functions (boot_bridge.cpp), host-tested on their own.
std::vector<OldWrite> old_writes(const std::vector<OldDoc>& docs, model::Model& m);
size_t write(const KilnAssetKey& key, const std::string& type, std::vector<uint8_t>* copy, const stage::BootResource& rb,
             std::vector<stage::BootOutcome>* outcomes, std::vector<std::string>* notes);

}  // namespace kc::registry::boothook
