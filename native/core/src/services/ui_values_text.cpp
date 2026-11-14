// SPDX-License-Identifier: MIT
// kiln.ui.values: the process-lifetime text store (chapter 05a §5.2).
//
// The game keeps a GameText {utf8, bytes, fnv} tuple and not a copy of the bytes, and nothing measured says when it is
// done with them, so every text a provider hands over lives until the process ends: no byte is freed at callback
// return, frame end, world change, provider removal, reload or owner revoke. The store is allocated once and never
// destroyed (no exit-time destructor racing the game's shutdown).
//
// Accounting. A string is stored once (exact bytes, no Unicode normalization). Each stable mod id that interned it
// holds a membership. A mod is charged bytes + 1 + 64 + 16 for every string it holds, whether or not another mod had
// interned the same bytes first (so its budget does not depend on what other mods did); the store as a whole is charged
// bytes + 1 + 64 once per string plus 16 per membership. Charges stay with the mod id for the process, across reloads.
// Limits: 4096 bytes a string, 8 MiB a mod, 64 MiB and 65,536 strings in all, 64 new memberships a second per mod with
// a burst of 128. Over a limit nothing is evicted: intern refuses (KILN_ERR_BUFFER_TOO_SMALL) and the refusal is
// logged once per change of reason per mod.
#include <algorithm>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>

#include "ui_values_internal.h"

namespace kiln::uivalues::text {
namespace {

constexpr uint64_t kIdTag = 0x5554ull << 48;  // "UT": an id is never a small integer or a pointer
constexpr uint64_t kIdIndex = (1ull << 48) - 1;

enum Refusal : uint32_t { kRefNone = 0, kRefLength, kRefModBudget, kRefStoreBudget, kRefStoreStrings, kRefRate };
const char* const kRefusalText[] = {"", "a string over 4096 bytes", "the mod's 8 MiB text budget is used up",
                                    "the 64 MiB text store is full", "the store holds 65,536 strings",
                                    "more than 64 new strings a second (burst 128)"};

struct Entry {
    std::string bytes;
    uint64_t fnv = 0;
};
struct Mod {
    uint32_t index = 0;
    uint64_t charged = 0;
    uint32_t memberships = 0;
    double tokens = kNewStringsBurst;
    double refilled_ms = 0;
    bool clock = false;
    uint32_t last_refusal = kRefNone;
};
struct Store {
    std::mutex mu;
    std::deque<Entry> entries;                              // id index = position; never shrinks or moves
    std::unordered_map<std::string_view, uint32_t> index;   // views into entries' bytes
    std::unordered_map<std::string, Mod> mods;
    std::unordered_set<uint64_t> members;                   // (mod index << 32) | entry index
    uint64_t charged = 0;
    uint32_t memberships = 0;
};
Store* g_store = new Store;  // never destroyed: the bytes live until the process ends
Store& store() { return *g_store; }

const char kEmpty[] = "";

uint64_t make_id(uint32_t entry) { return kIdTag | (static_cast<uint64_t>(entry) + 1); }
bool entry_of(KilnUiTextId id, uint32_t* entry) {
    if ((id & ~kIdIndex) != kIdTag || (id & kIdIndex) == 0) return false;
    const uint64_t i = (id & kIdIndex) - 1;
    if (i >= store().entries.size()) return false;
    *entry = static_cast<uint32_t>(i);
    return true;
}
uint64_t member_key(uint32_t mod, uint32_t entry) { return (static_cast<uint64_t>(mod) << 32) | entry; }

void refill(Mod& m, double now) {
    if (!m.clock) {
        m.clock = true;
        m.refilled_ms = now;
        return;
    }
    if (now > m.refilled_ms) {
        m.tokens = std::min(kNewStringsBurst, m.tokens + (now - m.refilled_ms) * kNewStringsPerSecond / 1000.0);
        m.refilled_ms = now;
    }
}

}  // namespace

uint64_t fnv64(const char* p, size_t n) {  // signed bytes, as the game and kiln.ui hash text
    uint64_t h = 0xcbf29ce484222325ull;
    for (size_t i = 0; i < n; ++i) {
        h ^= static_cast<uint64_t>(static_cast<int64_t>(static_cast<int8_t>(p[i])));
        h *= 0x100000001b3ull;
    }
    return h;
}

bool valid_utf8(std::string_view s) {
    const auto* p = reinterpret_cast<const unsigned char*>(s.data());
    const size_t n = s.size();
    for (size_t i = 0; i < n;) {
        const unsigned c = p[i];
        if (c == 0) return false;
        if (c < 0x80) {
            ++i;
            continue;
        }
        size_t len;
        unsigned cp;
        if (c >= 0xC2 && c <= 0xDF) len = 2, cp = c & 0x1F;
        else if (c >= 0xE0 && c <= 0xEF) len = 3, cp = c & 0x0F;
        else if (c >= 0xF0 && c <= 0xF4) len = 4, cp = c & 0x07;
        else return false;  // a continuation byte, an overlong lead (C0, C1) or above U+10FFFF (F5..FF)
        if (i + len > n) return false;
        for (size_t k = 1; k < len; ++k) {
            if ((p[i + k] & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (p[i + k] & 0x3F);
        }
        if ((len == 3 && cp < 0x800) || (len == 4 && cp < 0x10000) || cp > 0x10FFFF) return false;  // overlong, range
        if (cp >= 0xD800 && cp <= 0xDFFF) return false;                                             // surrogates
        i += len;
    }
    return true;
}

KilnResult intern(const std::string& mod, std::string_view s, KilnUiTextId* out) {
    *out = 0;
    if (s.empty()) return KILN_OK;
    uint32_t refusal = kRefNone;
    bool log_refusal = false;
    KilnResult rc = KILN_OK;
    if (!valid_utf8(s)) return KILN_ERR_INVALID_ARGUMENT;
    {
        Store& st = store();
        std::lock_guard lk(st.mu);
        auto [mit, fresh] = st.mods.try_emplace(mod);
        Mod& m = mit->second;
        if (fresh) m.index = static_cast<uint32_t>(st.mods.size());
        const auto found = st.index.find(s);
        const bool exists = found != st.index.end();
        if (exists && st.members.count(member_key(m.index, found->second))) {
            *out = make_id(found->second);  // this mod's own string again: free
            return KILN_OK;
        }
        const uint64_t mod_charge = s.size() + kEntryOverhead + kMembershipCharge;
        const uint64_t store_charge = (exists ? 0 : s.size() + kEntryOverhead) + kMembershipCharge;
        if (s.size() > kTextMaxBytes) refusal = kRefLength;
        else if (m.charged + mod_charge > kModTextBudget) refusal = kRefModBudget;
        else if (!exists && st.entries.size() >= kGlobalStrings) refusal = kRefStoreStrings;
        else if (st.charged + store_charge > kGlobalTextBudget) refusal = kRefStoreBudget;
        else {
            refill(m, env().now_ms ? env().now_ms() : 0);
            if (m.tokens < 1) refusal = kRefRate;
        }
        if (refusal != kRefNone) {
            log_refusal = m.last_refusal != refusal;
            m.last_refusal = refusal;
            rc = KILN_ERR_BUFFER_TOO_SMALL;
        } else {
            m.tokens -= 1;
            uint32_t e;
            if (exists) {
                e = found->second;
            } else {
                e = static_cast<uint32_t>(st.entries.size());
                Entry& entry = st.entries.emplace_back();
                entry.bytes.assign(s.data(), s.size());
                entry.fnv = fnv64(entry.bytes.data(), entry.bytes.size());
                st.index.emplace(std::string_view(entry.bytes), e);
            }
            st.members.insert(member_key(m.index, e));
            m.charged += mod_charge;
            ++m.memberships;
            st.charged += store_charge;
            ++st.memberships;
            m.last_refusal = kRefNone;
            *out = make_id(e);
        }
    }
    if (log_refusal)
        logf(KILN_LOG_WARN, "ui.values: text from %s refused (%s); its providers' edits that need new text are refused "
                            "until it fits (nothing is evicted)",
             mod.c_str(), kRefusalText[refusal]);
    return rc;
}

bool held_by(KilnUiTextId id, std::string_view mod) {
    if (id == 0) return true;
    Store& st = store();
    std::lock_guard lk(st.mu);
    uint32_t e;
    if (!entry_of(id, &e)) return false;
    const auto m = st.mods.find(std::string(mod));
    return m != st.mods.end() && st.members.count(member_key(m->second.index, e)) != 0;
}

bool bytes(KilnUiTextId id, std::string_view* out) {
    if (id == 0) {
        *out = std::string_view(kEmpty, 0);
        return true;
    }
    Store& st = store();
    std::lock_guard lk(st.mu);
    uint32_t e;
    if (!entry_of(id, &e)) return false;
    *out = st.entries[e].bytes;  // the bytes never move: the view stays valid for the process
    return true;
}

bool game_text(KilnUiTextId id, GameText* out) {
    if (id == 0) {
        *out = GameText{kEmpty, 0, fnv64(kEmpty, 0)};
        return true;
    }
    Store& st = store();
    std::lock_guard lk(st.mu);
    uint32_t e;
    if (!entry_of(id, &e)) return false;
    const Entry& entry = st.entries[e];
    *out = GameText{entry.bytes.c_str(), entry.bytes.size(), entry.fnv};
    return true;
}

Usage usage(std::string_view mod) {
    Store& st = store();
    std::lock_guard lk(st.mu);
    const auto m = st.mods.find(std::string(mod));
    if (m == st.mods.end()) return {};
    return Usage{m->second.charged, m->second.memberships};
}

Usage usage_all() {
    Store& st = store();
    std::lock_guard lk(st.mu);
    return Usage{st.charged, static_cast<uint32_t>(st.entries.size())};
}

void reset_for_tests() {  // host tests only: no id of the old store is used afterwards
    delete g_store;
    g_store = new Store;
}

}  // namespace kiln::uivalues::text
