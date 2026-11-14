// SPDX-License-Identifier: MIT
// wear.h's facts, resolved from sigdb at start. Fail closed: a missing symbol or constant, a build whose hash has no
// approved frame description (also under policy "any"), or a constant that is not what the approved frames have turns
// items.tool_wear_debit off with the reason, in the capability table and in kiln.log. Nothing is hooked here.
#include "wear.h"

#include <cstdio>
#include <cstring>

#include "engine.h"
#include "kiln_api.h"
#include "services/kiln_caps.h"
#include "xfer.h"

namespace kitems::wear {
namespace {

Facts g_facts;

KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }

bool address(const KilnApi* api, const char* name, uintptr_t* out, Facts* f) {
    uintptr_t a = 0;
    if (api->symbol(str(name), &a) == KILN_OK && a) {
        *out = a;
        return true;
    }
    std::snprintf(f->why, sizeof f->why, "symbol %s did not resolve", name);
    return false;
}

bool constant(const KilnApi* api, const char* name, int64_t* out, Facts* f) {
    if (api->constant(str(name), out) == KILN_OK) return true;
    std::snprintf(f->why, sizeof f->why, "constant %s did not resolve", name);
    return false;
}

void lower(const KilnApi* api, uint64_t owner, uint32_t reason, const char* why) {
    const auto* caps = static_cast<const KilnCapsApi*>(api->service(str(KILN_CAPS_SERVICE), KILN_CAPS_VERSION));
    if (caps && caps->report) caps->report(owner, str(kFeature), KILN_CAP_UNAVAILABLE, reason, str(why));
}

}  // namespace

const Facts& facts() { return g_facts; }

bool resolve(const KilnApi* api, uint64_t owner) {
    Facts f;
    uintptr_t wear_item = 0, wear_delta = 0, enable = 0;
    // the feature first: off in sigdb (or held, or unknown to this build's definitions) is its own reason, already in the table
    if (!api->feature(str(kFeature))) {
        std::snprintf(f.why, sizeof f.why, "the sigdb feature %s is off on this build", kFeature);
        g_facts = f;
        items_log(KILN_LOG_INFO, "tool wear: off: %s", f.why);
        return false;
    }
    const bool resolved =
        address(api, "items/WearItem", &wear_item, &f) && address(api, "items/WearDelta", &wear_delta, &f) &&
        address(api, "items/EnableDurability", &enable, &f) && address(api, "items/ToolWearUpdate", &f.hit_update, &f) &&
        address(api, "items/ActorSpawnUpdate", &f.spawn_update, &f) && address(api, "items/WeaponWearReturn", &f.weapon_return, &f) &&
        address(api, "items/SpawnWearCall", &f.spawn_call, &f) &&
        constant(api, "items/spawn.request_local", &f.spawn_request_local, &f) &&
        constant(api, "items/spawn.durability_accessor_local", &f.spawn_durability_local, &f) &&
        constant(api, "items/spawn.request_pide", &f.spawn_request_pide, &f) &&
        constant(api, "items/spawn.iter_size", &f.spawn_iter_size, &f) && constant(api, "items/spawn.iter_base", &f.spawn_iter_base, &f) &&
        constant(api, "items/spawn.itemstate_slot", &f.spawn_itemstate_slot, &f) &&
        constant(api, "world.storage", &f.world_storage, &f) && constant(api, "storage.kind", &f.storage_kind, &f) &&
        constant(api, "items/registry.components", &f.registry_components, &f);
    uint32_t reason = KILN_CAP_REASON_SYMBOL_MISSING;
    if (resolved) {
        // the frames are per build: the hash decides, not the policy and not the patterns
        KilnBuildInfo b{};
        b.size = sizeof b;
        const bool known = api->build_info(&b) == KILN_OK && b.size >= offsetof(KilnBuildInfo, verified);
        b.exe_sha256[sizeof b.exe_sha256 - 1] = 0;
        if (!known || !approved_build(b.exe_sha256, b.side)) {
            std::snprintf(f.why, sizeof f.why, "this game build (%.12s) has no approved description of the debit's frames", known ? b.exe_sha256 : "unknown");
            reason = KILN_CAP_REASON_POLICY;
        } else if (const char* bad = check_constants(f)) {
            std::snprintf(f.why, sizeof f.why, "%s", bad);
        } else {
            f.wear_item = reinterpret_cast<WearItemFn>(wear_item);
            f.wear_delta = reinterpret_cast<WearDeltaFn>(wear_delta);
            f.enable_durability = reinterpret_cast<EnableDurabilityFn>(enable);
            f.ok = true;
        }
    }
    g_facts = f;
    if (f.ok) {
        items_log(KILN_LOG_INFO, "tool wear: the debit's functions and frames are known on this build");
        return true;
    }
    items_log(KILN_LOG_WARN, "tool wear: off: %s", f.why);
    lower(api, owner, reason, f.why);
    return false;
}

int durability_index(void* attributes) {
    if (!attributes || !E.attr_find) return -1;
    AttrRef r{};
    E.attr_find(&r, kAttrDurability, attributes);
    if (r.error || !r.desc) return -1;
    uint16_t index = 0;
    std::memcpy(&index, r.desc + 0xC, sizeof index);
    return index;
}

}  // namespace kitems::wear
