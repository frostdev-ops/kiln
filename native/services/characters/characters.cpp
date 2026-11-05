// SPDX-License-Identifier: MIT
// kiln.characters: reads the game's CharacterId out of the 16 player records (docs/re/characters.md). kiln.players owns the
// records (it finds and validates them); this service only adds one field, so kiln.players' frozen KilnPlayer stays as it is.
#include <windows.h>

#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "kiln_api.h"
#include "kiln_characters.h"
#include "services/kiln_players.h"

namespace {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnPlayersApi* g_players = nullptr;

int64_t g_off_id = -1, g_off_account = -1, g_stride = -1, g_count = -1;
bool g_sim = false;
std::atomic<uint64_t> g_scans{0};
std::atomic<uint32_t> g_records{0};

struct Slot {
    uint32_t handle = 0, flags = 0;
    uint64_t account = 0;
    uint8_t id[16] = {};
    bool used = false;
};
std::mutex g_mutex;
std::array<Slot, KILN_PLAYERS_MAX_SLOTS> g_slots;

void logf(int level, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > static_cast<int>(sizeof buf) - 1) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

bool constant(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }

// SEH-guarded read of game memory (no C++ objects with destructors in here, as __try requires).
bool guarded_copy(void* dst, uintptr_t src, size_t n) {
    if (!src) return false;
    __try {
        std::memcpy(dst, reinterpret_cast<const void*>(src), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool is_zero(const uint8_t* id) {
    for (int i = 0; i < 16; ++i)
        if (id[i]) return false;
    return true;
}

// The save's byte order is the Windows GUID layout, so .NET's new Guid(bytes) and this text agree.
void guid_text(const uint8_t* b, char (&out)[37]) {
    std::snprintf(out, sizeof out, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", b[3], b[2], b[1], b[0], b[5], b[4], b[7],
                  b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    if (!g_players) {
        g_players = static_cast<const KilnPlayersApi*>(g_api->service(KilnStr{KILN_PLAYERS_SERVICE, sizeof(KILN_PLAYERS_SERVICE) - 1}, 1));
        if (!g_players) return;
    }
    KilnPlayersStatus st{};
    st.size = sizeof st;
    if (g_players->status(&st) != KILN_OK || !st.records || !st.server_players) {
        g_records.store(0, std::memory_order_relaxed);
        return;
    }
    g_records.store(1, std::memory_order_relaxed);
    g_scans.fetch_add(1, std::memory_order_relaxed);
    const uint32_t count = static_cast<uint32_t>(g_count);
    for (uint32_t i = 0; i < count && i < g_slots.size(); ++i) {
        const uintptr_t rec = st.server_players + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(g_stride);
        uint32_t handle = 0;
        uint64_t account = 0;
        uint8_t id[16];
        if (!guarded_copy(&handle, rec, 4) || !guarded_copy(&account, rec + static_cast<uintptr_t>(g_off_account), 8) ||
            !guarded_copy(id, rec + static_cast<uintptr_t>(g_off_id), 16))
            continue;
        std::lock_guard lock(g_mutex);
        Slot& s = g_slots[i];
        if (s.flags & KILN_CHARACTER_SIMULATED) continue;
        if (handle == 0 || (handle & 0x3F) != i) {
            s.flags &= ~static_cast<uint32_t>(KILN_CHARACTER_LIVE);  // remembered until the slot changes hands
            continue;
        }
        const bool changed = !s.used || s.handle != handle || s.account != account || std::memcmp(s.id, id, 16) != 0;
        s.used = true;
        s.handle = handle;
        s.account = account;
        std::memcpy(s.id, id, 16);
        s.flags = KILN_CHARACTER_LIVE;
        if (changed) {
            char text[37];
            guid_text(id, text);
            logf(KILN_LOG_INFO, "characters: slot %u handle 0x%x: character %s, account %016llx", i, handle, text, static_cast<unsigned long long>(account));
        }
    }
}

KilnResult fill(uint32_t slot, const Slot& s, KilnCharacterInfo* out) {
    if (!out || out->size < sizeof(KilnCharacterInfo)) return KILN_ERR_INVALID_ARGUMENT;
    out->slot = slot;
    out->handle = s.handle;
    out->flags = s.flags;
    out->account = s.account;
    std::memcpy(out->id, s.id, 16);
    return KILN_OK;
}

KilnResult KILN_CALL t_status(KilnCharactersStatus* out) {
    if (!out || out->size < sizeof(KilnCharactersStatus)) return KILN_ERR_INVALID_ARGUMENT;
    out->resolved = g_off_id >= 0;
    out->records = g_records.load(std::memory_order_relaxed);
    out->simulated = g_sim;
    out->scans = g_scans.load(std::memory_order_relaxed);
    out->offset = g_off_id >= 0 ? static_cast<uint32_t>(g_off_id) : 0;
    uint32_t known = 0;
    {
        std::lock_guard lock(g_mutex);
        for (const Slot& s : g_slots)
            if (s.used && !is_zero(s.id)) ++known;
    }
    out->known = known;
    return KILN_OK;
}

KilnResult KILN_CALL t_by_handle(uint32_t handle, KilnCharacterInfo* out) {
    if (handle == 0) return KILN_ERR_NOT_FOUND;
    const uint32_t slot = handle & 0x3F;
    if (slot >= g_slots.size()) return KILN_ERR_NOT_FOUND;
    std::lock_guard lock(g_mutex);
    const Slot& s = g_slots[slot];
    if (!s.used || s.handle != handle) return KILN_ERR_NOT_FOUND;
    return fill(slot, s, out);
}

KilnResult KILN_CALL t_by_slot(uint32_t slot, KilnCharacterInfo* out) {
    if (slot >= g_slots.size()) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    const Slot& s = g_slots[slot];
    if (!s.used) return KILN_ERR_NOT_FOUND;
    return fill(slot, s, out);
}

KilnResult KILN_CALL t_simulate(uint32_t slot, uint64_t account, const uint8_t* id) {
    if (!g_sim) return KILN_ERR_UNAVAILABLE;
    if (slot >= g_slots.size()) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    Slot& s = g_slots[slot];
    s = Slot{};
    if (id) {
        s.used = true;
        s.account = account;
        s.flags = KILN_CHARACTER_LIVE | KILN_CHARACTER_SIMULATED;
        std::memcpy(s.id, id, 16);
    }
    return KILN_OK;
}

const KilnCharactersApi kTable = {
    {sizeof(KilnCharactersApi), KILN_CHARACTERS_VERSION}, &t_status, &t_by_handle, &t_by_slot, &t_simulate,
};
}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_CHARACTERS_SERVICE, sizeof(KILN_CHARACTERS_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    char env[8] = {};
    g_sim = GetEnvironmentVariableA("KILN_CHARACTERS_SIMULATE", env, sizeof env) > 0 && env[0] == '1';
    if (g_sim) logf(KILN_LOG_WARN, "characters: SIMULATION on (KILN_CHARACTERS_SIMULATE): never set this on a real server");
    const bool ok = api->feature(KilnStr{"characters.id", 13}) && constant("characters/rec.character", &g_off_id) &&
                    constant("players/rec.account", &g_off_account) && constant("players/rec.stride", &g_stride) &&
                    constant("players/rec.count", &g_count) && g_off_id >= 0 && g_off_account >= 0 && g_stride >= g_off_id + 16 && g_count > 0;
    if (!ok) {
        g_off_id = -1;
        logf(KILN_LOG_WARN, "characters: the CharacterId's place in the player record is unknown on this build; characters fall back to the account");
    } else {
        KilnHandle h = 0;
        if (api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h) != KILN_OK) {
            g_off_id = -1;
            logf(KILN_LOG_WARN, "characters: no tick on this build; characters fall back to the account");
        } else {
            logf(KILN_LOG_INFO, "characters: CharacterId at record +0x%llx, account at +0x%llx, %lld records of 0x%llx bytes",
                 static_cast<unsigned long long>(g_off_id), static_cast<unsigned long long>(g_off_account), static_cast<long long>(g_count),
                 static_cast<unsigned long long>(g_stride));
        }
    }
    return api->register_service(owner, KilnStr{KILN_CHARACTERS_SERVICE, sizeof(KILN_CHARACTERS_SERVICE) - 1}, &kTable);
}
