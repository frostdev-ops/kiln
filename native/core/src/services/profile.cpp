// SPDX-License-Identifier: MIT
// kiln.profile v1 (abi/services/kiln_profile.h): the profile this process booted with, read once by profile.cpp.
// Answered in safe idle too (KilnApi.service special-cases it), so early native mods and the managed host can read it.
#include "../profile.h"
#include "service.h"

namespace kiln::profile {
namespace {

KilnStr s(const std::string& v) { return KilnStr{v.data(), v.size()}; }

uint32_t KILN_CALL p_mode() { return state().mode; }
KilnStr KILN_CALL p_id() { return s(state().id); }
KilnStr KILN_CALL p_name() { return s(state().name); }
uint64_t KILN_CALL p_generation() { return state().generation; }

uint32_t KILN_CALL p_count(uint32_t kind) {
    return kind < KILN_PROFILE_KINDS ? static_cast<uint32_t>(state().mods[kind].size()) : 0;
}

KilnResult KILN_CALL p_get(uint32_t kind, uint32_t index, KilnProfileMod* out) {
    if (!out || out->size < sizeof(KilnProfileMod)) return KILN_ERR_INVALID_ARGUMENT;
    if (kind >= KILN_PROFILE_KINDS || index >= state().mods[kind].size()) return KILN_ERR_NOT_FOUND;
    const Entry& e = state().mods[kind][index];
    out->kind = kind;
    out->id = s(e.id);
    out->version = s(e.version);
    out->path = s(e.path);
    out->enabled = e.enabled ? 1u : 0u;
    out->reserved = 0;
    return KILN_OK;
}

uint32_t KILN_CALL p_dropins() { return state().dropins; }
KilnStr KILN_CALL p_config_dir() { return s(state().config_dir); }
KilnStr KILN_CALL p_data_dir() { return s(state().data_dir); }
KilnStr KILN_CALL p_json() { return s(state().text); }

const KilnProfileApi kTable = {
    {sizeof(KilnProfileApi), KILN_PROFILE_VERSION},
    &p_mode, &p_id, &p_name, &p_generation, &p_count, &p_get, &p_dropins, &p_config_dir, &p_data_dir, &p_json,
};

}  // namespace

const KilnProfileApi* table() { return &kTable; }

}  // namespace kiln::profile

KILN_SERVICE(profile, KILN_PROFILE_SERVICE, KILN_PROFILE_VERSION, &kiln::profile::kTable, nullptr);
