// SPDX-License-Identifier: MIT
// The EmfApi table, v18: attribute_get and attribute_add on kiln.combat v2 (native/core/abi/services/kiln_combat.h).
//  - EMF's attribute ids are the game's own: the ids kiln.combat resolves at run time for health, stamina and mana are
//    EMF_ATTR_HEALTH / _STAMINA / _MANA (and _MAX_*), so an id goes to kiln.combat unchanged. Body heat and oxygen work
//    the same way (EMF documents only the first three components).
//  - attribute_get reads any id of those components on the server world's thread, on creatures and players alike.
//  - attribute_add adds to a pair's current value and clamps to its minimum and maximum (damage stops at 0, a heal at the
//    maximum). The maximum, the minimum and the other computed ids are refused: the game recomputes them, so a write would
//    not last (as EMF documents for EMF_ATTR_MAX_*). Off the server world's thread the add is posted and runs in the next
//    step (true = queued, a refusal then is logged); on it, it runs at once.
//  - XP and level ids stay on kiln.progression (api_level.cpp).
//  - The v2 functions are called only when the core's kiln.combat table reaches them (header.size); an older table gives
//    "not supported", once.
#include <intrin.h>

#include "emf_internal.h"
#include "v26_model.h"

namespace kemf {

namespace {

bool is_server_world(EmfWorld* w) { return w && reinterpret_cast<uintptr_t>(w) == g_api->world(KILN_WORLD_SERVER); }

// kiln.combat v2: the attribute functions are the last slots of the table.
bool combat_has_attributes() {
    return S.combat && S.combat->header.size >= offsetof(KilnCombatApi, attribute_add) + sizeof(void*) && S.combat->attribute_get &&
           S.combat->attribute_add;
}

void off(Mod* m, const char* fn) {
    unsupported(m, fn, S.combat ? "kiln.combat has no attribute functions (an older Kiln core)" : "kiln.combat is not available");
}

bool add_now(Mod* m, uint32_t entity, uint32_t attr, int32_t delta) {
    KilnAttributeValue v{};
    v.size = sizeof v;
    const KilnResult rc = S.combat->attribute_add(entity, attr, delta, &v);
    switch (rc) {
        case KILN_OK: return true;
        case KILN_ERR_INVALID_ARGUMENT:
            refuse(m, "attribute_add",
                   format("0x%08X cannot be added to: only the current value of health, stamina, mana, body heat or oxygen (the maximum, the "
                          "minimum and the computed values are recomputed by the game)",
                          attr)
                       .c_str());
            return false;
        case KILN_ERR_NOT_FOUND: refuse(m, "attribute_add", format("entity %u has no such attribute (or it is not set up yet)", entity).c_str()); return false;
        case KILN_ERR_UNAVAILABLE: unsupported(m, "attribute_add", "attributes are off on this game build (see kiln.log)"); return false;
        default: refuse(m, "attribute_add", "not on the server world's thread"); return false;
    }
}

bool api_attribute_get(EmfWorld* w, uint32_t entity, uint32_t attr, int32_t* out) {
    Mod* m = caller_mod(_ReturnAddress());
    if (v26::is_progression_attribute(attr)) return level_attribute_get(m, w, entity, attr, out);  // v22: kiln.progression
    if (!out || !is_server_world(w)) return false;
    if (!combat_has_attributes()) return off(m, "attribute_get"), false;
    if (!g_api->on_tick_thread()) return refuse(m, "attribute_get", "not on the server world's thread", true), false;
    KilnAttributeValue v{};
    v.size = sizeof v;
    switch (S.combat->attribute_get(entity, attr, &v)) {
        case KILN_OK: *out = v.value; return true;
        case KILN_ERR_NOT_FOUND: return false;  // no such entity, no such component yet: EMF's plain false
        case KILN_ERR_INVALID_ARGUMENT:
            refuse(m, "attribute_get", format("0x%08X is not an attribute of Health, Stamina, Mana, BodyHeat or Oxygen", attr).c_str(), true);
            return false;
        case KILN_ERR_UNAVAILABLE: unsupported(m, "attribute_get", "attributes are off on this game build (see kiln.log)"); return false;
        default: return false;
    }
}

bool api_attribute_add(EmfWorld* w, uint32_t entity, uint32_t attr, int32_t delta) {
    Mod* m = caller_mod(_ReturnAddress());
    if (v26::is_progression_attribute(attr)) return level_attribute_add(m, w, entity, attr, delta);  // v22: kiln.progression
    if (!is_server_world(w)) return refuse(m, "attribute_add", "server world only"), false;
    if (!combat_has_attributes()) return off(m, "attribute_add"), false;
    if (g_api->on_tick_thread()) return add_now(m, entity, attr, delta);
    return post_tick([m, entity, attr, delta] { add_now(m, entity, attr, delta); });
}

}  // namespace

void fill_attributes(EmfApi* t) {
    t->attribute_get = &api_attribute_get;
    t->attribute_add = &api_attribute_add;
}

}  // namespace kemf
