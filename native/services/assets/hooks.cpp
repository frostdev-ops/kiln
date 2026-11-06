// SPDX-License-Identifier: MIT
// kiln.assets: hooks on the game's resource service, its content reader and the UI texture cache.
// Evidence and layouts: docs/re/resources.md. Symbols: sigdb/symbols/{server,client}.d/assets.json.
//
// Resources (assets.resources): the service object S keeps function pointers in slots; every caller goes through
// them, so hooking the slot targets covers every lookup:
//   ResourceByKey  (S+0x7d8)  u32* (u32* out, S*, const ResourceId*)      key -> handle (1-based, 0 = none)
//   ResourceData   (S+0x7e8)  DataSpan* (DataSpan* out, S*, u32 handle)     handle -> {data, size, {0,1}}
//   ResourceKeyOf  (S+0x7e0)  KeyOfResult* (out, S*, u32 handle)           handle -> ResourceId
//   ResourcesByType(S+0x7d0)  TypeList* (out, S*, u32 type, u32* opt)      every ResourceId of a type
// New resources get synthetic handles (kSynthBase + n); overrides keep the game's handle and swap the data.
//
// Content (assets.content): ContentSubmit looks a ContentHash up in the KFC content map (KfcContentMap(reader) =
// header + 0x30: {buckets, keys, values} self-relative arrays; bucket = hash0 & (n-1); value {u48 offset,
// u16 container, u32 0, u32 0}), and the async IO queue reads that container file with ReadFile + IOCP. Kiln adds one
// container (AsyncIoCreate gets one more path: an empty file in kiln/data/assets), answers KfcContentMap with a
// merged map in which mod blobs and overridden game hashes point into that container, and answers the game's ReadFile
// calls on that one handle from memory (IAT hooks; the completion is posted to the queue's port as the IO would).
//
// UI icons (assets.ui_icons, client): UiTexturePopulate(cache) runs every UI frame and returns while the byte
// cache+uitex.populated is 1; else it lists every UiTextureResource (ResourcesByType) and inserts unknown keys.
// Kiln clears the byte when new icon keys wait, so the game inserts them itself on its own thread.
#include <winternl.h>

#include <algorithm>
#include <cstdio>

#include "assets.h"
#include "safetyhook.hpp"

namespace ka {

std::atomic<uintptr_t> g_S{0};
uint16_t g_container_index = 0xFFFF;
std::wstring g_container_path;

namespace {

struct Sym {
    uintptr_t init = 0, by_key = 0, data = 0, key_of = 0, by_type = 0;
    uintptr_t submit = 0, cmap = 0, io_create = 0, populate = 0;
    int64_t populated = 0, uitex_res = 0, uitex_map = 0, free_count = 0;
} Y;

bool g_res_on = false, g_content_on = false, g_ui_on = false;
std::string g_res_why, g_content_why, g_ui_why;
int64_t g_kfc_header = 0x60, g_kfc_cmap = 0x30;

SafetyHookInline h_init, h_by_key, h_data, h_key_of, h_by_type, h_io_create, h_cmap, h_populate;

// ---- calls of the originals ----------------------------------------------------------------------------------
uint32_t* orig_by_key(uint32_t* out, uintptr_t S, const uint8_t* key32) {
    return h_by_key.call<uint32_t*>(out, S, key32);
}
DataSpan* orig_data(DataSpan* out, uintptr_t S, uint32_t h) { return h_data.call<DataSpan*>(out, S, h); }
KeyOfResult* orig_key_of(KeyOfResult* out, uintptr_t S, uint32_t h) { return h_key_of.call<KeyOfResult*>(out, S, h); }
TypeList* orig_by_type(TypeList* out, uintptr_t S, uint32_t type, uint32_t* opt) {
    return h_by_type.call<TypeList*>(out, S, type, opt);
}

bool rd(uintptr_t a, void* out, size_t n) {
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(a), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void note_S(uintptr_t S) {
    uintptr_t cur = g_S.load(std::memory_order_relaxed);
    if (cur == S || !S) return;
    if (cur == 0 && g_S.compare_exchange_strong(cur, S)) {
        LOGI("assets: resource service at %p (first seen in a lookup)", reinterpret_cast<void*>(S));
        on_resource_system_ready(S);
    }
}

// ---- resources --------------------------------------------------------------------------------------------------
char __fastcall hk_init(uintptr_t S, void* cfg) {
    const char ok = h_init.call<char>(S, cfg);
    if (ok) {
        uintptr_t cur = 0;
        if (g_S.compare_exchange_strong(cur, S)) {
            LOGI("assets: resource service %p initialized", reinterpret_cast<void*>(S));
            on_resource_system_ready(S);
        }
    } else {
        LOGW("assets: the game's resource service failed to initialize");
    }
    return ok;
}

uint32_t* __fastcall hk_by_key(uint32_t* out, uintptr_t S, const uint8_t* key32) {
    if (key32) {
        ReadGuard g;
        if (const ResView* v = g_res.load(std::memory_order_acquire); v && v->any_keys()) {
            Key k;
            std::memcpy(&k, key32, sizeof k);
            if (const Served* sv = v->key(k); sv && sv->is_new) {
                *out = sv->handle;
                return out;
            }
        }
    }
    return orig_by_key(out, S, key32);
}

DataSpan* __fastcall hk_data(DataSpan* out, uintptr_t S, uint32_t h) {
    {
        ReadGuard g;
        if (const ResView* v = g_res.load(std::memory_order_acquire); v && v->any_handles()) {
            if (const Served* sv = v->handle(h)) {
                out->data = sv->data;  // the bytes are never freed
                out->size = sv->size;
                out->flags = 0x100000000ull;
                return out;
            }
            if (h >= kSynthBase) {  // a synthetic handle whose resource was taken off: "not available", like the game
                out->data = nullptr, out->size = 0, out->flags = 0;
                return out;
            }
        }
    }
    return orig_data(out, S, h);
}

KeyOfResult* __fastcall hk_key_of(KeyOfResult* out, uintptr_t S, uint32_t h) {
    if (h >= kSynthBase) {
        ReadGuard g;
        if (const ResView* v = g_res.load(std::memory_order_acquire)) {
            std::memset(out, 0, sizeof *out);
            if (const Key* k = v->synth(h)) std::memcpy(out->key, k, sizeof(Key));
            else out->error = 0x12;
            return out;
        }
    }
    return orig_key_of(out, S, h);
}

TypeList* __fastcall hk_by_type(TypeList* out, uintptr_t S, uint32_t type, uint32_t* opt) {
    TypeList* r = orig_by_type(out, S, type, opt);
    ReadGuard g;
    const ResView* s = g_res.load(std::memory_order_acquire);
    if (!s || !s->any_new()) return r;
    std::lock_guard lock(s->merged_mu);
    auto mit = s->merged.find(type);
    if (mit == s->merged.end()) {
        std::vector<Key> added;
        s->new_keys(type, &added);
        if (added.empty()) return r;
        auto* v = new std::vector<uint8_t>();  // the game keeps the list it is given: never freed
        const uint64_t n = out->keys ? out->count : 0;
        v->resize((n + added.size()) * 32);
        if (n) std::memcpy(v->data(), out->keys, n * 32);
        for (size_t i = 0; i < added.size(); ++i) {
            std::memset(v->data() + (n + i) * 32, 0, 32);
            std::memcpy(v->data() + (n + i) * 32, &added[i], sizeof(Key));
        }
        mit = s->merged.emplace(type, v).first;
    }
    out->error = 0;
    out->zero = 0;
    out->keys = mit->second->data();
    out->count = mit->second->size() / 32;
    return out;
}

// ---- content: the merged content map -----------------------------------------------------------------------------
struct MapView {
    const uint8_t* base = nullptr;  // the 24-byte header
    uint32_t nb = 0, nk = 0, nv = 0;
    const uint8_t *buckets = nullptr, *keys = nullptr, *values = nullptr;
};
bool view_map(const void* m, MapView* v) {
    int32_t f[6];
    if (!m || !rd(reinterpret_cast<uintptr_t>(m), f, sizeof f)) return false;
    v->base = static_cast<const uint8_t*>(m);
    v->nb = static_cast<uint32_t>(f[1]), v->nk = static_cast<uint32_t>(f[3]), v->nv = static_cast<uint32_t>(f[5]);
    v->buckets = f[0] ? v->base + f[0] : nullptr;
    v->keys = f[2] ? v->base + 8 + f[2] : nullptr;
    v->values = f[4] ? v->base + 16 + f[4] : nullptr;
    return v->nb && (v->nb & (v->nb - 1)) == 0 && v->nk == v->nv && v->buckets && v->keys && v->values;
}

struct Merged {
    uint64_t gen = ~0ull;
    const void* src = nullptr;
    const uint8_t* map = nullptr;
};
std::atomic<const Merged*> g_merged{nullptr};
std::mutex g_merge_mu;
std::atomic<const void*> g_game_map{nullptr};
std::atomic<uintptr_t> g_reader{0};

const uint8_t* build_merged(const MapView& src, const ContentSnap& cs) {
    struct E {
        uint8_t key[16];
        uint8_t val[16];
    };
    std::vector<E> all;
    all.reserve(src.nk + cs.entries.size());
    std::unordered_map<HashK, size_t, HashKHash> pos;
    pos.reserve(cs.entries.size() * 2 + 1);
    for (auto& [h, off] : cs.entries) pos.emplace(h, SIZE_MAX);
    for (uint32_t i = 0; i < src.nk; ++i) {
        E e;
        std::memcpy(e.key, src.keys + i * 16ull, 16);
        std::memcpy(e.val, src.values + i * 16ull, 16);
        HashK hk;
        std::memcpy(&hk, e.key, 16);
        auto it = cs.entries.find(hk);
        if (it != cs.entries.end()) {  // an override of a game blob: point it at Kiln's container
            const uint64_t v = (it->second & 0xFFFFFFFFFFFFull) | static_cast<uint64_t>(g_container_index) << 48;
            std::memset(e.val, 0, 16);
            std::memcpy(e.val, &v, 8);
            pos[hk] = i;
        }
        all.push_back(e);
    }
    for (auto& [h, off] : cs.entries) {
        if (pos[h] != SIZE_MAX) continue;
        E e;
        std::memcpy(e.key, &h, 16);
        std::memset(e.val, 0, 16);
        const uint64_t v = (off & 0xFFFFFFFFFFFFull) | static_cast<uint64_t>(g_container_index) << 48;
        std::memcpy(e.val, &v, 8);
        all.push_back(e);
    }
    const uint32_t nb = src.nb, n = static_cast<uint32_t>(all.size());
    auto bucket_of = [&](const E& e) {
        uint32_t h0;
        std::memcpy(&h0, e.key + 4, 4);
        return h0 & (nb - 1);
    };
    std::vector<uint32_t> counts(nb, 0);
    for (const E& e : all) ++counts[bucket_of(e)];
    const size_t bytes = 24 + static_cast<size_t>(nb) * 8 + static_cast<size_t>(n) * 32;
    uint8_t* m = keep_alloc(bytes);
    if (!m) return nullptr;
    auto* bk = reinterpret_cast<uint32_t*>(m + 24);
    uint8_t* keys = m + 24 + static_cast<size_t>(nb) * 8;
    uint8_t* vals = keys + static_cast<size_t>(n) * 16;
    uint32_t first = 0;
    std::vector<uint32_t> fill(nb);
    for (uint32_t b = 0; b < nb; ++b) {
        bk[b * 2] = counts[b] ? first : 0;
        bk[b * 2 + 1] = counts[b];
        fill[b] = first;
        first += counts[b];
    }
    for (const E& e : all) {
        const uint32_t at = fill[bucket_of(e)]++;
        std::memcpy(keys + at * 16ull, e.key, 16);
        std::memcpy(vals + at * 16ull, e.val, 16);
    }
    int32_t hdr[6];
    hdr[0] = 24;
    hdr[1] = static_cast<int32_t>(nb);
    hdr[2] = static_cast<int32_t>(keys - (m + 8));
    hdr[3] = static_cast<int32_t>(n);
    hdr[4] = static_cast<int32_t>(vals - (m + 16));
    hdr[5] = static_cast<int32_t>(n);
    std::memcpy(m, hdr, sizeof hdr);
    return m;
}

// Checks that the game's map works the way Kiln reads it (bucket = hash0 & (n-1), contiguous bucket ranges).
bool verify_game_map(const MapView& v, std::string* why) {
    const uint32_t step = std::max<uint32_t>(1, v.nk / 64);
    for (uint32_t i = 0; i < v.nk; i += step) {
        uint32_t h0, first, cnt;
        std::memcpy(&h0, v.keys + i * 16ull + 4, 4);
        const uint32_t b = h0 & (v.nb - 1);
        std::memcpy(&first, v.buckets + b * 8ull, 4);
        std::memcpy(&cnt, v.buckets + b * 8ull + 4, 4);
        if (i < first || i >= first + cnt) {
            char buf[160];
            std::snprintf(buf, sizeof buf, "content map: key %u not in its bucket %u (%u+%u)", i, b, first, cnt);
            *why = buf;
            return false;
        }
    }
    return true;
}

const void* __fastcall hk_cmap(uintptr_t reader) {
    const void* game = h_cmap.call<const void*>(reader);
    if (!g_game_map.load(std::memory_order_relaxed)) {
        g_game_map.store(game);
        g_reader.store(reader);
    }
    ReadGuard g;
    const ContentSnap* cs = g_content.load(std::memory_order_acquire);
    if (!cs || cs->entries.empty() || g_container_index == 0xFFFF) return game;
    const Merged* m = g_merged.load(std::memory_order_acquire);
    if (m && m->gen == cs->gen && m->src == game) return m->map;
    std::lock_guard lock(g_merge_mu);
    m = g_merged.load(std::memory_order_acquire);
    cs = g_content.load(std::memory_order_acquire);
    if (m && m->gen == cs->gen && m->src == game) return m->map;
    MapView v;
    if (!view_map(game, &v)) return game;
    const uint8_t* built = build_merged(v, *cs);
    if (!built) return game;
    auto* nm = new Merged{cs->gen, game, built};  // the old ones stay valid: a reader may still use them
    g_merged.store(nm, std::memory_order_release);
    LOGI("assets: content map rebuilt: %u game blobs + %zu Kiln entries (generation %llu)", v.nk, cs->entries.size(),
         static_cast<unsigned long long>(cs->gen));
    return built;
}

// ---- content: the async IO queue and Kiln's container --------------------------------------------------------------
struct IoPath {
    const char* ptr;
    size_t len;
};
struct IoCfg {
    IoPath* paths;
    uint64_t count;
};
std::string g_container_utf8;
std::vector<std::string> g_game_containers;

void* __fastcall hk_io_create(void* platform, IoCfg* cfg) {
    // Only the resource service's queue lists the kfc containers (paths ending in _000.dat ...).
    bool kfc = false;
    if (cfg && cfg->paths && cfg->count > 0 && cfg->count < 4096 && !g_container_utf8.empty()) {
        std::string first(cfg->paths[0].ptr, cfg->paths[0].len);
        kfc = first.size() > 8 && first.compare(first.size() - 8, 8, "_000.dat") == 0;
    }
    if (!kfc) return h_io_create.call<void*>(platform, cfg);
    g_game_containers.clear();
    for (uint64_t i = 0; i < cfg->count; ++i) g_game_containers.emplace_back(cfg->paths[i].ptr, cfg->paths[i].len);
    std::vector<IoPath> paths(cfg->paths, cfg->paths + cfg->count);
    paths.push_back({g_container_utf8.c_str(), g_container_utf8.size()});
    IoPath* old_paths = cfg->paths;
    const uint64_t old_count = cfg->count;
    cfg->paths = paths.data();
    cfg->count = paths.size();
    g_container_index = static_cast<uint16_t>(old_count);
    void* q = h_io_create.call<void*>(platform, cfg);
    cfg->paths = old_paths;
    cfg->count = old_count;
    if (!q) {
        // Kiln's container broke the queue (should not happen: an empty file opens like the game's empty ones). Try
        // again without it so the game still starts.
        g_container_index = 0xFFFF;
        LOGE("assets: the async IO queue refused Kiln's container; content blobs are off");
        return h_io_create.call<void*>(platform, cfg);
    }
    LOGI("assets: async IO queue %p: %llu game container(s) + Kiln's as #%u (%s)", q,
         static_cast<unsigned long long>(old_count), g_container_index, g_container_utf8.c_str());
    return q;
}

// IAT hooks: only the one file handle of Kiln's container is answered; everything else passes through.
using CreateFileWFn = HANDLE(WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
using CreateIoCompletionPortFn = HANDLE(WINAPI*)(HANDLE, HANDLE, ULONG_PTR, DWORD);
using ReadFileFn = BOOL(WINAPI*)(HANDLE, LPVOID, DWORD, LPDWORD, LPOVERLAPPED);
CreateFileWFn g_CreateFileW = nullptr;
CreateIoCompletionPortFn g_CreateIoCompletionPort = nullptr;
ReadFileFn g_ReadFile = nullptr;
std::atomic<HANDLE> g_file{nullptr};
std::atomic<HANDLE> g_port{nullptr};
std::atomic<ULONG_PTR> g_port_key{0};
std::atomic<uint64_t> g_reads{0}, g_read_bytes{0}, g_read_misses{0};

bool same_path(LPCWSTR a, const std::wstring& b) {
    if (!a || b.empty()) return false;
    const size_t n = wcslen(a);
    if (n != b.size()) return false;
    for (size_t i = 0; i < n; ++i) {
        wchar_t x = towlower(a[i]), y = towlower(b[i]);
        if (x == L'/') x = L'\\';
        if (y == L'/') y = L'\\';
        if (x != y) return false;
    }
    return true;
}

HANDLE WINAPI hk_CreateFileW(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa, DWORD disp,
                             DWORD flags, HANDLE tmpl) {
    HANDLE h = g_CreateFileW(name, access, share, sa, disp, flags, tmpl);
    if (h != INVALID_HANDLE_VALUE && same_path(name, g_container_path)) {
        g_file.store(h);
        LOGI("assets: the game opened Kiln's container (handle %p, flags 0x%lx)", h, flags);
    }
    return h;
}

HANDLE WINAPI hk_CreateIoCompletionPort(HANDLE file, HANDLE port, ULONG_PTR key, DWORD threads) {
    HANDLE r = g_CreateIoCompletionPort(file, port, key, threads);
    if (file && file == g_file.load() && r) {
        g_port.store(r);
        g_port_key.store(key);
        LOGI("assets: Kiln's container joined the IO completion port %p (key %llu)", r, static_cast<unsigned long long>(key));
    }
    return r;
}

// The parts of Kiln's container a read covers. Worked out under a ReadGuard, copied after it: the copy into the game's buffer runs under
// hk_ReadFile's __try, and a fault there must not leave a reader counted (the blob bytes themselves are never freed).
struct ReadPart {
    uint64_t at;
    const uint8_t* src;
    uint64_t n;
};
bool plan_read(uint64_t off, uint64_t n, std::vector<ReadPart>* parts) {
    ReadGuard g;
    const ContentSnap* cs = g_content.load(std::memory_order_acquire);
    if (!cs) return false;
    const uint64_t end = off + n;
    auto it = std::upper_bound(cs->extents.begin(), cs->extents.end(), off,
                               [](uint64_t o, const Extent& e) { return o < e.offset; });
    if (it != cs->extents.begin()) --it;
    for (; it != cs->extents.end() && it->offset < end; ++it) {
        const uint64_t a = std::max(off, it->offset), b = std::min(end, it->offset + it->size);
        if (a < b) parts->push_back({a - off, it->data + (a - it->offset), b - a});
    }
    return true;
}

__declspec(noinline) void serve_read(uint64_t off, uint8_t* dst, uint64_t n) {
    std::vector<ReadPart> parts;
    const bool any = plan_read(off, n, &parts);
    std::memset(dst, 0, n);
    if (!any) return;
    for (const ReadPart& p : parts) std::memcpy(dst + p.at, p.src, p.n);
    if (parts.empty()) g_read_misses.fetch_add(1, std::memory_order_relaxed);
}

BOOL WINAPI hk_ReadFile(HANDLE h, LPVOID buf, DWORD n, LPDWORD done, LPOVERLAPPED ov) {
    if (!h || h != g_file.load(std::memory_order_relaxed)) return g_ReadFile(h, buf, n, done, ov);
    const uint64_t off = ov ? (static_cast<uint64_t>(ov->OffsetHigh) << 32 | ov->Offset) : 0;
    __try {
        serve_read(off, static_cast<uint8_t*>(buf), n);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        SetLastError(ERROR_INVALID_USER_BUFFER);
        return FALSE;
    }
    g_reads.fetch_add(1, std::memory_order_relaxed);
    g_read_bytes.fetch_add(n, std::memory_order_relaxed);
    if (done) *done = n;
    if (ov) {
        ov->Internal = 0;  // STATUS_SUCCESS
        ov->InternalHigh = n;
        HANDLE port = g_port.load();
        if (port) {
            // A handle bound to a completion port queues a packet even when the read completes at once: do the same.
            PostQueuedCompletionStatus(port, n, g_port_key.load(), ov);
        } else if (ov->hEvent) {
            SetEvent(ov->hEvent);
        }
    }
    return TRUE;
}

// Same technique as kiln.ui's input.cpp (patch_iat): swaps one import slot of the game exe.
void* patch_iat(const char* dll, const char* name, void* hook) {
    auto* base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return nullptr;
    for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress); imp->Name; ++imp) {
        if (_stricmp(reinterpret_cast<const char*>(base + imp->Name), dll) != 0) continue;
        auto* names =
            reinterpret_cast<IMAGE_THUNK_DATA64*>(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            auto* ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(ibn->Name), name) != 0) continue;
            DWORD old = 0;
            if (!VirtualProtect(&slots->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) return nullptr;
            void* prev = reinterpret_cast<void*>(slots->u1.Function);
            slots->u1.Function = reinterpret_cast<ULONGLONG>(hook);
            VirtualProtect(&slots->u1.Function, sizeof(void*), old, &old);
            return prev;
        }
    }
    return nullptr;
}

// ---- UI icons ---------------------------------------------------------------------------------------------------
std::atomic<uintptr_t> g_ui_cache{0};
std::atomic<bool> g_room_warned{false};
std::atomic<uint64_t> g_populates{0};

void __fastcall hk_populate(uintptr_t cache) {
    if (g_ui_cache.exchange(cache) != cache) LOGI("assets: UI texture cache %p", reinterpret_cast<void*>(cache));
    auto* flag = reinterpret_cast<volatile uint8_t*>(cache + Y.populated);
    const uint64_t want = g_icon_want.load();
    if (*flag == 1 && want > g_icon_served.load()) {
        const uint64_t free_entries = *reinterpret_cast<volatile uint64_t*>(cache + Y.free_count);
        if (free_entries > 16) {
            *flag = 0;  // the game refills its key set from the (merged) UiTextureResource list right now
        } else if (!g_room_warned.exchange(true)) {
            LOGW("assets: the UI texture cache has only %llu free entries: new icons wait for the next start",
                 static_cast<unsigned long long>(free_entries));
        }
    }
    const bool was = *flag != 0;
    h_populate.call<void>(cache);
    if (!was && *flag != 0) {
        g_populates.fetch_add(1);
        icons_after_populate(want);
    }
}

bool sym(const char* name, uintptr_t* out, std::string* why) {
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out) return true;
    *why += std::string(*why->c_str() ? ", " : "") + name;
    return false;
}
bool cst(const char* name, int64_t* out, std::string* why) {
    if (g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK) return true;
    *why += std::string(*why->c_str() ? ", " : "") + name;
    return false;
}
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

template <class F>
bool hook(SafetyHookInline& h, uintptr_t at, F fn, const char* what, std::string* why) {
    h = safetyhook::create_inline(reinterpret_cast<void*>(at), reinterpret_cast<void*>(fn));
    if (!h) {
        *why += std::string(*why->c_str() ? ", " : "") + "hook " + what;
        return false;
    }
    return true;
}

}  // namespace

// ---- calls for assets.cpp -------------------------------------------------------------------------------------------
uint32_t game_handle(uintptr_t S, const Key& k) {
    if (!g_res_on || !S) return 0;
    alignas(16) uint8_t key32[32] = {};
    std::memcpy(key32, &k, sizeof k);
    uint32_t out = 0;
    __try {
        orig_by_key(&out, S, key32);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
    return out;
}

bool game_data(uintptr_t S, uint32_t handle, DataSpan* out) {
    if (!g_res_on || !S || !handle) return false;
    __try {
        orig_data(out, S, handle);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return out->data != nullptr;
}

bool game_type_list(uintptr_t S, uint32_t type, const uint8_t** keys, uint64_t* count) {
    if (!g_res_on || !S) return false;
    TypeList r{};
    __try {
        orig_by_type(&r, S, type, nullptr);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    *keys = r.keys;
    *count = r.keys ? r.count : 0;
    return r.error == 0;
}

bool game_key_of(uintptr_t S, uint32_t handle, Key* out) {
    if (!g_res_on || !S || !handle) return false;
    KeyOfResult r{};
    __try {
        orig_key_of(&r, S, handle);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    if (r.error) return false;
    std::memcpy(out, r.key, sizeof(Key));
    return true;
}

bool game_blob_find(const HashK& h, GameBlob* out) {
    const void* m = g_game_map.load();
    if (!m && g_content_on) {
        // Not requested by the game yet: ask the accessor ourselves (reader = S + 8).
        const uintptr_t S = g_S.load();
        uintptr_t reader = 0;
        if (S && rd(S + 8, &reader, 8) && reader) {
            m = h_cmap.call<const void*>(reader);
            g_game_map.store(m);
        }
    }
    MapView v;
    if (!view_map(m, &v)) return false;
    const uint32_t b = h.h0 & (v.nb - 1);
    uint32_t first, cnt;
    std::memcpy(&first, v.buckets + b * 8ull, 4);
    std::memcpy(&cnt, v.buckets + b * 8ull + 4, 4);
    for (uint32_t i = first; i < first + cnt && i < v.nk; ++i) {
        if (std::memcmp(v.keys + i * 16ull, &h, 16) != 0) continue;
        uint64_t val;
        std::memcpy(&val, v.values + i * 16ull, 8);
        out->offset = val & 0xFFFFFFFFFFFFull;
        out->container = static_cast<uint16_t>(val >> 48);
        return true;
    }
    return false;
}

std::string game_container_path(uint16_t index) { return index < g_game_containers.size() ? g_game_containers[index] : ""; }
uint32_t game_container_count() { return static_cast<uint32_t>(g_game_containers.size()); }

bool resources_on() { return g_res_on; }
// content_hooks_on: the content reader and IO queue are hooked; blobs may be registered now and are served once the
// game opens Kiln's container (during resource-system init, after on_ready). content_on: the container is live too.
bool content_hooks_on() { return g_content_on; }
bool content_on() { return g_content_on && g_container_index != 0xFFFF && g_file.load(); }
bool ui_on() { return g_ui_on; }

std::string hooks_status_json() {
    char b[1024];
    std::snprintf(b, sizeof b,
                  "{\"resources\":%s,\"resourcesWhy\":\"%s\",\"content\":%s,\"contentWhy\":\"%s\",\"container\":%d,"
                  "\"containerOpen\":%s,\"port\":%s,\"reads\":%llu,\"readBytes\":%llu,\"readMisses\":%llu,"
                  "\"uiIcons\":%s,\"uiWhy\":\"%s\",\"uiCache\":%s,\"populates\":%llu,\"service\":\"%p\"}",
                  g_res_on ? "true" : "false", g_res_why.c_str(), g_content_on ? "true" : "false", g_content_why.c_str(),
                  g_container_index == 0xFFFF ? -1 : g_container_index, g_file.load() ? "true" : "false",
                  g_port.load() ? "true" : "false", static_cast<unsigned long long>(g_reads.load()),
                  static_cast<unsigned long long>(g_read_bytes.load()), static_cast<unsigned long long>(g_read_misses.load()),
                  g_ui_on ? "true" : "false", g_ui_why.c_str(), g_ui_cache.load() ? "true" : "false",
                  static_cast<unsigned long long>(g_populates.load()), reinterpret_cast<void*>(g_S.load()));
    return b;
}

bool install_resource_hooks(std::string* why) {
    std::string& w = g_res_why;
    if (!feat("assets.resources")) {
        w = "feature assets.resources is off on this build";
        *why = w;
        return false;
    }
    bool ok = sym("assets/ResourceInit", &Y.init, &w) & sym("assets/ResourceByKey", &Y.by_key, &w) &
              sym("assets/ResourceData", &Y.data, &w) & sym("assets/ResourceKeyOf", &Y.key_of, &w) &
              sym("assets/ResourcesByType", &Y.by_type, &w);
    if (ok) {
        ok = hook(h_by_key, Y.by_key, &hk_by_key, "ResourceByKey", &w) && hook(h_data, Y.data, &hk_data, "ResourceData", &w) &&
             hook(h_key_of, Y.key_of, &hk_key_of, "ResourceKeyOf", &w) &&
             hook(h_by_type, Y.by_type, &hk_by_type, "ResourcesByType", &w) && hook(h_init, Y.init, &hk_init, "ResourceInit", &w);
    }
    g_res_on = ok;
    *why = w;
    return ok;
}

bool install_content_hooks(std::string* why) {
    std::string& w = g_content_why;
    if (!g_res_on) {
        w = "needs assets.resources";
        *why = w;
        return false;
    }
    if (!feat("assets.content")) {
        w = "feature assets.content is off on this build";
        *why = w;
        return false;
    }
    bool ok = sym("assets/ContentSubmit", &Y.submit, &w) & sym("assets/KfcContentMap", &Y.cmap, &w) &
              sym("assets/AsyncIoCreate", &Y.io_create, &w);
    if (ok) {
        // KfcContentMap must be the accessor Kiln reads: mov rax,[rcx+hdr]; add rax,map; ret
        uint8_t b[9] = {};
        ok = rd(Y.cmap, b, sizeof b) && b[0] == 0x48 && b[1] == 0x8B && b[2] == 0x41 && b[4] == 0x48 && b[5] == 0x83 &&
             b[6] == 0xC0 && b[8] == 0xC3;
        if (ok) {
            g_kfc_header = b[3], g_kfc_cmap = b[7];
        } else {
            w = "KfcContentMap is not the expected accessor";
        }
    }
    if (ok) {
        // Kiln's container: an empty file the IO queue opens like the game's own empty containers.
        const std::string dir = api_path(KILN_PATH_DATA) + "\\assets";
        CreateDirectoryW(widen(dir).c_str(), nullptr);
        g_container_utf8 = dir + "\\kiln-content.dat";
        g_container_path = widen(g_container_utf8);
        HANDLE f = CreateFileW(g_container_path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) {
            w = "cannot create " + g_container_utf8;
            ok = false;
        } else {
            CloseHandle(f);
        }
    }
    if (ok) {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        g_CreateFileW = reinterpret_cast<CreateFileWFn>(GetProcAddress(k32, "CreateFileW"));
        g_CreateIoCompletionPort = reinterpret_cast<CreateIoCompletionPortFn>(GetProcAddress(k32, "CreateIoCompletionPort"));
        g_ReadFile = reinterpret_cast<ReadFileFn>(GetProcAddress(k32, "ReadFile"));
        void* a = patch_iat("KERNEL32.dll", "CreateFileW", reinterpret_cast<void*>(&hk_CreateFileW));
        void* b = patch_iat("KERNEL32.dll", "CreateIoCompletionPort", reinterpret_cast<void*>(&hk_CreateIoCompletionPort));
        void* c = patch_iat("KERNEL32.dll", "ReadFile", reinterpret_cast<void*>(&hk_ReadFile));
        if (a) g_CreateFileW = reinterpret_cast<CreateFileWFn>(a);
        if (b) g_CreateIoCompletionPort = reinterpret_cast<CreateIoCompletionPortFn>(b);
        if (c) g_ReadFile = reinterpret_cast<ReadFileFn>(c);
        ok = a && b && c && g_CreateFileW && g_CreateIoCompletionPort && g_ReadFile;
        if (!ok) w = "import slots CreateFileW / CreateIoCompletionPort / ReadFile not found";
    }
    if (ok) ok = hook(h_cmap, Y.cmap, &hk_cmap, "KfcContentMap", &w) && hook(h_io_create, Y.io_create, &hk_io_create, "AsyncIoCreate", &w);
    g_content_on = ok;
    *why = w;
    return ok;
}

bool verify_content_map(std::string* why) {
    GameBlob tmp;
    (void)game_blob_find(HashK{}, &tmp);  // fetches the map
    MapView v;
    if (!view_map(g_game_map.load(), &v)) {
        *why = "content map unreadable";
        return false;
    }
    return verify_game_map(v, why);
}

bool install_ui_hooks(std::string* why) {
    std::string& w = g_ui_why;
    if (!g_client) {
        w = "dedicated server: no UI";
        *why = w;
        return false;
    }
    if (!g_res_on || !feat("assets.ui_icons")) {
        w = g_res_on ? "feature assets.ui_icons is off on this build" : "needs assets.resources";
        *why = w;
        return false;
    }
    bool ok = sym("assets/UiTexturePopulate", &Y.populate, &w) & cst("assets/uitex.populated", &Y.populated, &w) &
              cst("assets/uitex.free_count", &Y.free_count, &w) & cst("assets/uitex.map", &Y.uitex_map, &w);
    if (ok) ok = hook(h_populate, Y.populate, &hk_populate, "UiTexturePopulate", &w);
    g_ui_on = ok;
    *why = w;
    return ok;
}

}  // namespace ka
