// SPDX-License-Identifier: MIT
// kiln.pets v1 (dev.32, 19d §5.3): the table of the native safety module, in the pinned kiln-creature-control.dll beside
// the broker it shares quarantine and retired roots with. The rows and policies are pets_safety.cpp's; here: the table's
// thread rules, the native death guard of kiln.decisions (installed with the first row, so nothing is hooked while no pet
// exists), the step and world epoch at each Pre, and the first phase of a protection loss: when kiln.decisions lowers
// decisions.native_guards (its death hook faulted), every row is marked ProtectionLost at once and Kiln.Pets retires the
// bodies at the next Pre; rows go only with their bodies. Contract: native/core/abi/services/kiln_pets.h.
#include <windows.h>

#include <cstring>

#include "internal.h"
#include "pets_safety.h"
#include "services/kiln_caps.h"
#include "services/kiln_decisions.h"

namespace kcc {
namespace {

KilnResult quarantine_body(const KilnControlBodyKey& key, uint32_t cause) {
    return g_broker ? g_broker->quarantine(key, cause) : KILN_ERR_UNAVAILABLE;
}

PetSafety& safety() {
    static PetSafety s(&quarantine_body);  // process lifetime: the module is never unloaded while a row protects a body
    return s;
}

const KilnDecisionsApiV1* D = nullptr;
const KilnCapsApi* Caps = nullptr;
bool g_guard_set = false;
std::string g_guard_why;

uint32_t KILN_CALL death_guard(uint64_t epoch, uint32_t entity, int32_t health) { return safety().death_policy(epoch, entity, health); }

// The native death guard goes in with the first row (kiln.decisions installs its hook at a safe Pre).
bool ensure_guard() {
    if (g_guard_set) return true;
    if (!D) D = static_cast<const KilnDecisionsApiV1*>(g_api->service(KilnStr{KILN_DECISIONS_SERVICE, std::strlen(KILN_DECISIONS_SERVICE)}, 1));
    if (!D || D->header.size < offsetof(KilnDecisionsApiV1, set_death_guard) + sizeof(D->set_death_guard) || !D->set_death_guard) {
        g_guard_why = "kiln.decisions has no native death guard on this build";
        return false;
    }
    const KilnResult rc = D->set_death_guard(g_owner, &death_guard);
    if (rc != KILN_OK) {
        g_guard_why = "kiln.decisions refused the native death guard (decisions.native_guards says why)";
        logf(KILN_LOG_WARN, "kiln.pets: %s: %d", g_guard_why.c_str(), static_cast<int>(rc));
        return false;
    }
    g_guard_set = true;
    safety().set_guard_installed(true);
    logf(KILN_LOG_INFO, "kiln.pets: the native death guard is set: guarded pets are never marked dead");
    return true;
}

#define PETS_TICK_ONLY()                                    \
    do {                                                    \
        if (!g_broker) return KILN_ERR_UNAVAILABLE;         \
        if (!on_tick()) return KILN_ERR_WRONG_THREAD;       \
    } while (0)

KilnResult KILN_CALL p_bind(KilnOwner, const KilnPetSafetyBinding* b) {
    PETS_TICK_ONLY();
    if (!b) return KILN_ERR_INVALID_ARGUMENT;
    if (!ensure_guard()) return KILN_ERR_UNAVAILABLE;
    const uint64_t epoch = server_epoch();
    if (!epoch || b->body.world_epoch != epoch) return KILN_ERR_INVALID_ARGUMENT;
    safety().set_epoch(epoch);
    return safety().bind(*b);
}
KilnResult KILN_CALL p_activate(KilnOwner, KilnControlBodyKey key) {
    PETS_TICK_ONLY();
    return safety().activate(key);
}
KilnResult KILN_CALL p_inspect(KilnControlBodyKey key, KilnPetSafetyState* out) { return safety().inspect(key, out); }
KilnResult KILN_CALL p_collect(KilnOwner, KilnPetSafetyState* out, uint32_t capacity, uint32_t* written, uint32_t* rescan) {
    PETS_TICK_ONLY();
    return safety().collect(out, capacity, written, rescan);
}
KilnResult KILN_CALL p_retire(KilnOwner, KilnControlBodyKey key) {
    PETS_TICK_ONLY();
    return safety().retire(key, key.world_epoch == server_epoch() && body_exists(key.entity));
}
uint32_t KILN_CALL p_death(uint64_t epoch, KilnEntity e, int32_t hp) { return safety().death_policy(epoch, e, hp); }
uint32_t KILN_CALL p_hit(const KilnPetHitQuery* q) { return q ? safety().hit_policy(*q) : KILN_PET_HIT_PASS; }
uint32_t KILN_CALL p_involved(uint64_t epoch, KilnEntity e) { return safety().involved(epoch, e); }
KilnResult KILN_CALL p_owner_entity(KilnOwner, KilnControlBodyKey key, KilnEntity owner_entity) {
    PETS_TICK_ONLY();
    return safety().set_owner_entity(key, owner_entity);
}
KilnResult KILN_CALL p_hostile(KilnOwner, KilnEntity a, KilnEntity b, uint32_t on) {
    PETS_TICK_ONLY();
    return safety().set_hostile(a, b, on != 0);
}
KilnResult KILN_CALL p_withdraw(KilnOwner) {
    PETS_TICK_ONLY();
    safety().withdraw();
    logf(KILN_LOG_WARN, "kiln.pets: protection withdrawn by request: every pet body is retired at the next Pre");
    return KILN_OK;
}

const KilnPetsApi kPetsTable = {
    {sizeof(KilnPetsApi), KILN_PETS_VERSION},
    &p_bind, &p_activate, &p_inspect, &p_collect, &p_retire, &p_death, &p_hit, &p_involved, &p_owner_entity, &p_hostile, &p_withdraw,
};

}  // namespace

void pets_register(const KilnApi* api, KilnOwner owner) {
    api->register_service(owner, KilnStr{KILN_PETS_SERVICE, sizeof(KILN_PETS_SERVICE) - 1}, &kPetsTable);
}

void pets_pre(uint64_t epoch, uint64_t step) {
    PetSafety& s = safety();
    s.set_step(step);
    if (epoch) s.set_epoch(epoch);
    if (!g_guard_set || s.rows() == 0) return;
    // phase 1 of a protection loss: the death hook faulted or was refused after the guard was set
    if (!Caps) Caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    if (!Caps || !Caps->find) return;
    KilnCapRow row{};
    row.size = sizeof row;
    if (Caps->find(KilnStr{"decisions.native_guards", 23}, &row) == KILN_OK && row.state == KILN_CAP_UNAVAILABLE) {
        static bool told = false;
        s.withdraw();
        if (!told) logf(KILN_LOG_WARN, "kiln.pets: decisions.native_guards went off: every pet body is retired at the next Pre");
        told = true;
    }
}

}  // namespace kcc
