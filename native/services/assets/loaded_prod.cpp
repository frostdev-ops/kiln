// SPDX-License-Identifier: MIT
// kiln.assets' loaded-resource reads on the running game (kiln_assets.h loaded_snapshot, loaded_list; capability
// assets.loaded): loaded.cpp over kiln.internal.engine's copy and owner view and the exact profile's resource layout, with
// kiln.assets' own served copies (an override, a mod's new resource) and VirtualQuery for a listing's readability filter.
#include <windows.h>
#include <intrin.h>

#include <cstring>
#include <string>

#include "assets.h"
#include "kiln_engine_native.h"
#include "loaded.h"
#include "services/kiln_caps.h"
#include "services/kiln_engine.h"
#include "services/kiln_world.h"

namespace ka {
namespace {

std::once_flag g_once;
loaded::Source g_source;
std::string g_why;      // why the reads are off ("" when on)
std::string g_profile;  // the exact profile's id
const KilnWorldApi* g_world = nullptr;  // game_world_session (the writes' session)

// ---- the writes' seams -----------------------------------------------------------------------------------------
std::uint64_t server_session(void*) {
    return g_world ? g_world->game_world_session(KILN_WORLD_SERVER) : 0;
}
std::uint64_t server_step(void*) {
    if (!g_api || g_api->size < offsetof(KilnApi, session) + sizeof(void*) || !g_api->session) return 0;
    KilnSessionInfo s{};
    s.size = sizeof s;
    return g_api->session(&s) == KILN_OK ? s.steps[KILN_WORLD_SERVER] : 0;
}
// ShroudForge's ScalarDataProtection (compare_exchange_resource_scalar.cpp:52-118): a private committed page of the
// content worker's allocation, read-only or read-write; a read-only one made read-write for the scalar and checked.
bool protect_scalar(void*, std::uintptr_t address, std::size_t width, std::uintptr_t allocation_base, loaded::ProtectState* st) {
    *st = {};
    MEMORY_BASIC_INFORMATION page{};
    if (VirtualQuery(reinterpret_cast<const void*>(address), &page, sizeof page) != sizeof page || page.State != MEM_COMMIT ||
        page.Type != MEM_PRIVATE || reinterpret_cast<std::uintptr_t>(page.AllocationBase) != allocation_base ||
        (page.Protect != PAGE_READONLY && page.Protect != PAGE_READWRITE))
        return false;
    const auto base = reinterpret_cast<std::uintptr_t>(page.BaseAddress);
    if (address < base || address - base > page.RegionSize || width > page.RegionSize - (address - base)) return false;
    st->address = address;
    st->width = width;
    st->allocation_base = allocation_base;
    st->old_protection = page.Protect;
    if (page.Protect == PAGE_READWRITE) return true;
    DWORD previous = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), width, PAGE_READWRITE, &previous)) return false;
    st->old_protection = previous;
    st->changed = true;
    // a changed protection is no permission to write another kind of page: restore even when this fails
    if (previous != PAGE_READONLY) return false;
    MEMORY_BASIC_INFORMATION after{};
    return VirtualQuery(reinterpret_cast<const void*>(address), &after, sizeof after) == sizeof after &&
           after.State == MEM_COMMIT && after.Type == MEM_PRIVATE && after.Protect == PAGE_READWRITE &&
           after.AllocationBase == page.AllocationBase;
}
bool restore_scalar(void*, loaded::ProtectState* st) {
    if (!st->address) return !st->restore_failed;
    if (st->changed) {
        DWORD ignored = 0;
        if (!VirtualProtect(reinterpret_cast<void*>(st->address), st->width, st->old_protection, &ignored)) {
            st->restore_failed = true;
            return false;
        }
        st->changed = false;
    }
    MEMORY_BASIC_INFORMATION restored{};
    if (VirtualQuery(reinterpret_cast<const void*>(st->address), &restored, sizeof restored) != sizeof restored ||
        restored.Protect != st->old_protection || restored.State != MEM_COMMIT || restored.Type != MEM_PRIVATE ||
        reinterpret_cast<std::uintptr_t>(restored.AllocationBase) != st->allocation_base)
        st->restore_failed = true;
    return !st->restore_failed;
}
// compare_exchange_bits (compare_exchange_resource_scalar.cpp:8-31): one interlocked exchange under SEH.
bool cas_scalar(void*, std::uintptr_t address, std::size_t width, std::uint64_t expected, std::uint64_t desired,
                std::uint64_t* observed) {
    __try {
        switch (width) {
            case 1:
                *observed = static_cast<unsigned char>(_InterlockedCompareExchange8(
                    reinterpret_cast<volatile char*>(address), static_cast<char>(desired), static_cast<char>(expected)));
                return true;
            case 2:
                *observed = static_cast<std::uint16_t>(_InterlockedCompareExchange16(
                    reinterpret_cast<volatile short*>(address), static_cast<short>(desired), static_cast<short>(expected)));
                return true;
            case 4:
                *observed = static_cast<std::uint32_t>(_InterlockedCompareExchange(
                    reinterpret_cast<volatile long*>(address), static_cast<long>(desired), static_cast<long>(expected)));
                return true;
            case 8:
                *observed = static_cast<std::uint64_t>(_InterlockedCompareExchange64(
                    reinterpret_cast<volatile long long*>(address), static_cast<long long>(desired),
                    static_cast<long long>(expected)));
                return true;
            default: return false;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The committed writes (owner-tagged, and named by a named write: sfi R3): one record per place for loaded_write_records,
// and the last 256 for kiln.assets' status.
loaded::WriteRecords g_records;
void committed(void*, const loaded::Commit& c) {
    const std::size_t dropped = g_records.dropped();
    const std::uint64_t id = g_records.commit(c);
    Key k;
    std::memcpy(k.g, c.key.guid, 16);
    k.type = c.key.type;
    k.part = c.key.part;
    if (c.writer.empty())
        LOGI("assets: loaded write %llu by owner %llu: %s offset %zu width %u, session %llu", static_cast<unsigned long long>(id),
             static_cast<unsigned long long>(c.owner), key_str(k).c_str(), c.offset, c.width, static_cast<unsigned long long>(c.session));
    else
        LOGI("assets: loaded write %llu by owner %llu for %.*s (%.*s): %s offset %zu width %u, session %llu",
             static_cast<unsigned long long>(id), static_cast<unsigned long long>(c.owner), static_cast<int>(c.writer.size()),
             c.writer.data(), static_cast<int>(c.field.size()), c.field.data(), key_str(k).c_str(), c.offset, c.width,
             static_cast<unsigned long long>(c.session));
    if (dropped == 0 && g_records.dropped() != 0)
        LOGW("assets: the loaded write records are full (%u places): the place written longest ago is dropped for each new one "
             "(its writer is no longer named in Kiln's reports)", KILN_LOADED_WRITE_PLACES);
}

// A JSON string's text (the names are checked text: no control character; quotes and backslashes escaped).
std::string json_text(const std::string& s) {
    std::string out;
    for (const char c : s) {
        if (c == '"' || c == '\\') out += '\\';
        out += c;
    }
    return out;
}

// ShroudForge's readable() (resolve_loaded_resource.cpp:6-41): committed, readable protection, no guard page.
bool committed_readable(void*, std::uintptr_t address, std::size_t size) {
    if (!size) return true;
    if (!address || size > UINTPTR_MAX - address) return false;
    const std::uintptr_t end = address + size;
    std::uintptr_t cursor = address;
    while (cursor < end) {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(reinterpret_cast<const void*>(cursor), &info, sizeof info)) return false;
        const DWORD protection = info.Protect & 0xff;
        if (info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            !(protection == PAGE_READONLY || protection == PAGE_READWRITE || protection == PAGE_WRITECOPY ||
              protection == PAGE_EXECUTE_READ || protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY))
            return false;
        const auto region = reinterpret_cast<std::uintptr_t>(info.BaseAddress);
        if (info.RegionSize > UINTPTR_MAX - region) return false;
        const std::uintptr_t region_end = region + info.RegionSize;
        if (cursor < region || cursor >= region_end) return false;
        cursor = region_end < end ? region_end : end;
    }
    return true;
}

// kiln.assets' served copy of a key: the bytes are immutable and never freed, copied inside a ReadGuard.
bool served_copy(void*, const KilnAssetKey& key, std::vector<std::uint8_t>* out, bool* is_new) {
    Key k;
    std::memcpy(k.g, key.guid, 16);
    k.type = key.type;
    k.part = key.part;
    ReadGuard guard;
    const ResView* view = g_res.load(std::memory_order_acquire);
    const Served* served = view ? view->key(k) : nullptr;
    if (!served || !served->data) return false;
    out->assign(served->data, served->data + served->size);
    *is_new = served->is_new;
    return true;
}

void served_new_keys(void*, std::uint32_t type, std::vector<KilnAssetKey>* out) {
    std::vector<Key> keys;
    {
        ReadGuard guard;
        if (const ResView* view = g_res.load(std::memory_order_acquire)) view->new_keys(type, &keys);
    }
    out->reserve(keys.size());
    for (const Key& k : keys) {
        KilnAssetKey key{};
        std::memcpy(key.guid, k.g, 16);
        key.type = k.type;
        key.part = k.part;
        out->push_back(key);
    }
}

void bind() {
    g_source.user = nullptr;
    g_source.readable = &committed_readable;
    g_source.served = &served_copy;
    g_source.new_keys = &served_new_keys;
    g_source.server_session = &server_session;
    g_source.server_step = &server_step;
    g_source.protect = &protect_scalar;
    g_source.restore = &restore_scalar;
    g_source.cas = &cas_scalar;
    g_source.committed = &committed;
    if (!g_api || !g_api->service) {
        g_why = "no KilnApi";
        return;
    }
    const auto* engine = static_cast<const KilnEngineApi*>(
        g_api->service(KilnStr{KILN_ENGINE_SERVICE, sizeof(KILN_ENGINE_SERVICE) - 1}, KILN_ENGINE_VERSION));
    KilnEngineIdentity id{};
    id.size = sizeof id;
    if (!engine || !engine->identity || engine->identity(&id) != KILN_OK || id.status != KILN_ENGINE_EXACT) {
        g_why = "no exact engine profile for this build";
        return;
    }
    g_profile = id.profile_id;
    const auto* world = static_cast<const KilnWorldApi*>(
        g_api->service(KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, KILN_WORLD_VERSION));
    if (world && world->header.size >= offsetof(KilnWorldApi, game_world_session) + sizeof(void*) && world->game_world_session)
        g_world = world;
    char refused[160] = {};
    const KilnEngineNativeApi* native =
        kiln_engine_native(g_api, g_self, offsetof(KilnEngineNativeApi, ecs_view) + sizeof(void*), refused, sizeof refused);
    // the writes check the gate and guard members themselves (loaded::write_refusal)
    if (!native) {
        g_why = refused;
        return;
    }
    const kiln::engine::Layout* layout = native->layout ? native->layout() : nullptr;
    if (!layout) {
        g_why = "kiln.internal.engine has no exact profile layout";
        return;
    }
    g_source.engine = native;
    g_source.layout = layout;
}

const loaded::Source& source() {
    std::call_once(g_once, &bind);
    return g_source;
}

}  // namespace

KilnResult op_loaded_snapshot(KilnOwner owner, const KilnAssetKey* key, void* bytes, size_t capacity, size_t* required,
                              KilnLoadedInfo* info) {
    return loaded::snapshot(source(), owner, key, bytes, capacity, required, info);
}

KilnResult op_loaded_scalar_cas(KilnOwner owner, const KilnAssetKey* key, uint64_t expected_session, const void* expected_full,
                               size_t resource_size, size_t offset, const void* desired, uint32_t width, KilnMutationReply* reply) {
    return loaded::compare_exchange(source(), owner, key, expected_session, expected_full, resource_size, offset, desired, width,
                                    reply);
}

KilnResult op_loaded_scalar_cas_named(KilnOwner owner, const KilnAssetKey* key, uint64_t expected_session, const void* expected_full,
                                     size_t resource_size, size_t offset, const void* desired, uint32_t width, KilnStr writer,
                                     KilnStr field, KilnMutationReply* reply) {
    return loaded::compare_exchange_named(source(), owner, key, expected_session, expected_full, resource_size, offset, desired,
                                          width, writer, field, reply);
}

KilnResult op_loaded_write_records(KilnLoadedWriteRecord* out, uint32_t record_size, size_t capacity, size_t* required) {
    return g_records.read(out, record_size, capacity, required);
}

std::string loaded_writes_json() {
    std::string out = "[";
    for (const loaded::WriteRecords::Recent& w : g_records.recent()) {
        Key k;
        std::memcpy(k.g, w.key.guid, 16);
        k.type = w.key.type;
        k.part = w.key.part;
        char line[256];
        std::snprintf(line, sizeof line, "%s{\"id\":%llu,\"owner\":%llu,\"key\":\"%s\",\"offset\":%zu,\"width\":%u,\"session\":%llu",
                      out.size() > 1 ? "," : "", static_cast<unsigned long long>(w.id), static_cast<unsigned long long>(w.owner),
                      key_str(k).c_str(), w.offset, w.width, static_cast<unsigned long long>(w.session));
        out += line;
        if (!w.writer.empty()) out += ",\"writer\":\"" + json_text(w.writer) + "\"";
        if (!w.field.empty()) out += ",\"field\":\"" + json_text(w.field) + "\"";
        out += "}";
    }
    return out + "]";
}

KilnResult op_loaded_list(KilnOwner owner, uint32_t type, KilnAssetKey* out, size_t capacity, size_t* required,
                          KilnLoadedInfo* info) {
    return loaded::list(source(), owner, type, out, capacity, required, info);
}

// on_ready: the assets.loaded row (static: per-call readiness stays each read's own answer).
void loaded_report_capability() {
    const loaded::Source& s = source();
    const bool on = s.engine && s.layout;
    const std::string text = on ? "loaded-resource reads (loaded_snapshot, loaded_list) from any thread in a world, on " + g_profile
                                : g_why + "; loaded-resource reads report unavailable";
    LOGI("assets: loaded resources %s (%s)", on ? "on" : "off", on ? g_profile.c_str() : g_why.c_str());
    const char* write_off = loaded::write_refusal(s);
    const std::string write_text =
        write_off ? std::string(write_off) + "; loaded-resource writes report unavailable"
                  : "loaded-resource scalar compare-exchange (loaded_scalar_compare_exchange) of the server world under the "
                    "engine's write gate, between frames, on " + g_profile;
    LOGI("assets: loaded resource writes %s (%s)", write_off ? "off" : "on", write_off ? write_off : g_profile.c_str());
    if (!g_api || !g_api->service) return;
    const auto* caps = static_cast<const KilnCapsApi*>(
        g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION));
    if (!caps || caps->header.size < offsetof(KilnCapsApi, report) + sizeof(void*) || !caps->report) return;
    caps->report(g_self, KilnStr{"assets.loaded", 13}, on ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                 on ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_SYMBOL_MISSING, KilnStr{text.data(), text.size()});
    caps->report(g_self, KilnStr{"assets.loaded_write", 19}, write_off ? KILN_CAP_UNAVAILABLE : KILN_CAP_AVAILABLE,
                 write_off ? KILN_CAP_REASON_SYMBOL_MISSING : KILN_CAP_REASON_NONE, KilnStr{write_text.data(), write_text.size()});
}

}  // namespace ka
