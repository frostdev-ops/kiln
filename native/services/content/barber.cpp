// SPDX-License-Identifier: MIT
// kiln.content: new hair styles and beards in the barber's and the character screen's lists (barber.h). The lists live in the UI bundle, a resource only a
// client loads (keen::FbUiBundle.characterCustomizationOptions, a keen::CharacterCustomizationResource: hairItems and beardItems, BlobArrays of 44-byte
// {knowledgeRequirementQuery, itemReference, previewImage}). Offsets come from the game's reflection. A list that gets entries has its BlobArray header
// (read-only resource memory, write_batch) pointed at a copy in near memory: the game's entries, then Kiln's; with none, back at the game's own array.
#include "barber.h"

#include <cstdio>
#include <cstring>
#include <mutex>

#include "content.h"
#if __has_include("gamedata.h")
#include "gamedata.h"
#define KC_BARBER 1
#endif

namespace kc {

#ifdef KC_BARBER
namespace {

struct List {
    const char* field;
    uintptr_t header = 0;         // the BlobArray in the bundle
    uintptr_t orig = 0;           // the game's array
    uint32_t count = 0;           // the game's count
    uint64_t applied = 0;         // the key of what is written now (0: the game's own)
    uint32_t added = 0;
};

std::mutex g_mu;
bool g_tried = false, g_ok = false;
uint32_t g_entry = 0, g_ref = 0;  // entry size, itemReference offset
List g_lists[2] = {{"hairItems"}, {"beardItems"}};
std::string g_why = "not tried";

bool zero(const uint8_t g[16]) {
    static const uint8_t z[16] = {};
    return std::memcmp(g, z, 16) == 0;
}

bool setup() {
    if (g_tried) return g_ok;
    g_tried = true;
    using namespace gd;
    if (!res_ready()) {
        g_tried = false;  // resources not up yet: again later
        return false;
    }
    const TType* bundle = type_named("keen::FbUiBundle");
    const TType* opts = type_named("keen::CharacterCustomizationResource");
    const TType* item = type_named("keen::HairCustomizationItem");
    const TType* beard = type_named("keen::BeardCustomizationItem");
    const TField* bf = bundle ? bundle->field("characterCustomizationOptions") : nullptr;
    const TField* ref = item ? item->field("itemReference") : nullptr;
    if (!bf || !opts || !ref || !beard || beard->size != item->size) {
        g_why = "the game's reflection has no hair and beard lists in the UI bundle";
        return false;
    }
    std::vector<std::array<uint8_t, 16>> guids;
    if (!res_list(bundle->hash, &guids) || guids.empty()) {
        g_why = "no UI bundle here (a dedicated server has none)";
        return false;
    }
    const uintptr_t base = res_data(guids[0].data(), bundle->hash);
    if (!base) {
        g_why = "the UI bundle isn't loaded";
        return false;
    }
    for (List& l : g_lists) {
        const TField* f = opts->field(l.field);
        if (!f || !blob_array(base + bf->offset + f->offset, &l.orig, &l.count)) {
            g_why = std::string("the UI bundle's ") + l.field + " don't read";
            return false;
        }
        l.header = base + bf->offset + f->offset;
    }
    g_entry = item->size;
    g_ref = ref->offset;
    g_ok = true;
    g_why = "on";
    LOGI("new content: the barber's lists: %u hair style(s), %u beard(s) of the game's (UI bundle %p)", g_lists[0].count, g_lists[1].count,
         reinterpret_cast<void*>(base));
    return true;
}

// The game's entry for this item, in the game's own array: the index, or -1.
int find(const List& l, const uint8_t guid[16]) {
    if (zero(guid)) return -1;
    for (uint32_t i = 0; i < l.count; ++i) {
        uint8_t g[16];
        if (rd(l.orig + static_cast<uintptr_t>(i) * g_entry + g_ref, g, 16) && std::memcmp(g, guid, 16) == 0) return static_cast<int>(i);
    }
    return -1;
}

}  // namespace
#endif

void barber_sync(const std::vector<BarberItem>& items) {
#ifdef KC_BARBER
    std::lock_guard lock(g_mu);
    if (!setup()) return;
    for (List& l : g_lists) {
        // each new item that starts from a listed style, and isn't listed itself
        std::vector<std::pair<int, const BarberItem*>> add;
        uint64_t key = 0xcbf29ce484222325ull;
        for (const BarberItem& it : items) {
            if (find(l, it.guid) >= 0) continue;
            int from = find(l, it.icon);
            if (from < 0) from = find(l, it.base);
            if (from < 0) continue;
            add.emplace_back(from, &it);
            for (int i = 0; i < 16; ++i) key = (key ^ it.guid[i]) * 0x100000001b3ull;
            key = (key ^ static_cast<uint64_t>(from)) * 0x100000001b3ull;
        }
        if (add.empty()) key = 0;
        if (key == l.applied) continue;
        Write w{};
        w.addr = l.header;
        w.n = 8;
        uintptr_t arr = l.orig;
        uint32_t n = l.count;
        if (!add.empty()) {
            const size_t total = static_cast<size_t>(l.count) + add.size();
            auto* p = static_cast<uint8_t*>(near_alloc(l.header, total * g_entry));
            if (!p || !rd(l.orig, p, static_cast<size_t>(l.count) * g_entry)) {
                LOGW("new content: the barber's %s: no memory near the UI bundle for %zu entries: the game's list stays", l.field, total);
                continue;
            }
            for (size_t i = 0; i < add.size(); ++i) {
                uint8_t* e = p + (l.count + i) * g_entry;
                std::memcpy(e, p + static_cast<size_t>(add[i].first) * g_entry, g_entry);
                std::memcpy(e + g_ref, add[i].second->guid, 16);
            }
            arr = reinterpret_cast<uintptr_t>(p);
            n = static_cast<uint32_t>(total);
        }
        const int64_t off = static_cast<int64_t>(arr) - static_cast<int64_t>(l.header);
        if (off < INT32_MIN || off > INT32_MAX) {
            LOGW("new content: the barber's %s: the copy is out of reach of the UI bundle: the game's list stays", l.field);
            continue;
        }
        const int32_t o32 = static_cast<int32_t>(off);
        std::memcpy(w.bytes, &o32, 4);
        std::memcpy(w.bytes + 4, &n, 4);
        std::vector<Write> ws{w};
        uintptr_t failed = 0;
        if (write_batch(ws, &failed) != 1) {
            LOGW("new content: the barber's %s: the list's header did not take the write", l.field);
            continue;
        }
        l.applied = key;
        l.added = static_cast<uint32_t>(add.size());
        LOGI("new content: the barber lists %u %s of mods (%u of the game's)", l.added, l.field, l.count);
    }
#else
    (void)items;
#endif
}

std::string barber_report() {
#ifdef KC_BARBER
    std::lock_guard lock(g_mu);
    char b[160];
    std::snprintf(b, sizeof b, "barber: %s; %u hair style(s), %u beard(s) of mods", g_why.c_str(), g_lists[0].added, g_lists[1].added);
    return b;
#else
    return "barber: off (no gamedata in this build)";
#endif
}

}  // namespace kc
