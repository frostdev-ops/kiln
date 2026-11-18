// SPDX-License-Identifier: MIT
// Actual sequence registration and definition listing over a fake registry map.
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <set>
#include <unordered_set>
#include "../gamedata.h"
#include "services/kiln_capacity_util.hpp"  // libstdc++'s fstream has its own __try: parsed before the macro below
#undef __try
#define __try if (true)
#define __except(x) else if (false)
inline long long _InterlockedOr64(volatile long long* p, long long v) { auto old=*p; *p=old|v; return old; }
inline long long _InterlockedExchange64(volatile long long* p, long long v) { auto old=*p; *p=v; return old; }
#include "../gamedata_clones.cpp"
#include <stdexcept>
namespace kc {
const KilnApi* g_api = nullptr;
StoreMutex mu;
StoreMutex& store_mutex() { return mu; }
bool rd(uintptr_t p, void* out, size_t n) { if (!p) return false; std::memcpy(out, reinterpret_cast<void*>(p), n); return true; }
uint32_t fnv1a(const void* p, size_t n) { uint32_t h=2166136261u; for(size_t i=0;i<n;i++) h=(h^static_cast<const uint8_t*>(p)[i])*16777619u; return h; }
std::string guid_text(const uint8_t*) { return "11111111-2222-4333-8444-555555555555"; }
std::string owner_label(KilnOwner) { return "mod test"; }
void logf(int, const char*, ...) {}
bool map_read(uintptr_t, std::vector<uint32_t>*, std::vector<uint64_t>*) { return false; }
Engine E{};
namespace gd {
GRegistry registry;
const char* section_name(uint32_t) { return "sequences"; }
GRegistry& greg(uint32_t) { return registry; }
bool have_type = false;
TType sequence_type;
const TType* type_named(const std::string&) { return have_type ? &sequence_type : nullptr; }
uintptr_t res_data(const uint8_t*, uint32_t, uint64_t*) { return 0; }
}
}
int main() {
    using namespace kc::gd;
    KilnApi api{};
    api.world = [](uint32_t kind)->uintptr_t { return kind == KILN_WORLD_CLIENT ? 2 : 1; };
    kc::g_api = &api;
    Def d; d.section = KILN_GD_SEQUENCES; d.data=16; d.state="registered"; d.worlds.insert(1);
    auto before = listing(d);
    if (before["registrations"]["client"]["state"] != "pending") throw std::runtime_error("client must be pending before attach");
    // No reflected sequence type: actual ensure_seq refuses this client's registry.
    if (ensure_seq(d, 2)) throw std::runtime_error("unavailable registry accepted");
    auto failed = listing(d);
    if (failed["registrations"]["client"]["state"] != "failed" || failed["state"] == "registered")
        throw std::runtime_error("failed client registration was hidden");
    if (attach_sequence(d, 2) || listing(d)["state"] == "registered") throw std::runtime_error("permanent failure overwritten");
    // Reflection is now present, but this world's missing registry is recoverable.
    have_type = true;
    api.singleton = [](uintptr_t, KilnStr)->uintptr_t { return 0; };
    Def recovering; recovering.section=KILN_GD_SEQUENCES; recovering.data=16; recovering.worlds.insert(1);
    if (ensure_seq(recovering, 2)) throw std::runtime_error("missing registry accepted");
    if (!listing(recovering)["registrations"]["client"]["retryable"].get<bool>()) throw std::runtime_error("startup failure must retry");
    // A validated cached map becomes available; actual map_put writes it on the retry.
    alignas(8) uint8_t header[128] = {};
    uint64_t bits=0, values[8]={}; uint32_t keys[8]={};
    auto set = [&](size_t off, auto value) { std::memcpy(header+off, &value, sizeof value); };
    set(kc::E.hm_cap, uint64_t{8}); set(kc::E.hm_bits, reinterpret_cast<uintptr_t>(&bits));
    set(kc::E.hm_keys, reinterpret_cast<uintptr_t>(keys)); set(kc::E.hm_values, reinterpret_cast<uintptr_t>(values));
    g_seq_maps.push_back({2, reinterpret_cast<uintptr_t>(header)});
    if (!attach_sequence(recovering, 2) || listing(recovering)["state"] != "registered") throw std::runtime_error("recoverable failure did not register");
    if (listing(recovering)["registrations"]["server"]["state"] != "registered") throw std::runtime_error("server outcome lost");
    if (attach_sequence(d, 2) || listing(d)["state"] == "registered") throw std::runtime_error("permanent failure was retried");
    static_assert(sizeof(KilnGamedataApi) == 160);  // since 0.2.0-dev.29: buff_budget, define_buff_result, buff_registration
    static_assert(offsetof(KilnGamedataApi, definitions) == 128);
    static_assert(offsetof(KilnGamedataApi, buff_budget) == 136 && offsetof(KilnGamedataApi, buff_registration) == 152);
    static_assert(sizeof(KilnContentApi) == 280);  // since 0.2.0-dev.29.18: text, text_info appended
    static_assert(offsetof(KilnContentApi, txn_begin) == 248);
    static_assert(offsetof(KilnContentApi, txn_end) == 256);
    static_assert(offsetof(KilnContentApi, text) == 264 && offsetof(KilnContentApi, text_info) == 272);
    Def invalid; invalid.section=KILN_GD_SEQUENCES; invalid.state="error"; invalid.problem="sequence types unavailable";
    if (listing(invalid)["registrations"]["client"]["state"] != "failed") throw std::runtime_error("resource refusal was incorrectly pending");
    set(kc::E.hm_count, uint32_t{4});
    Def full; full.section=KILN_GD_SEQUENCES; full.data=16; full.guid[0]=0x55; full.worlds.insert(1);
    if (ensure_seq(full, 2) || listing(full)["registrations"]["client"]["retryable"].get<bool>()) throw std::runtime_error("full map must be a permanent refusal");
    if (attach_sequence(full, 2) || listing(full)["state"] == "registered") throw std::runtime_error("full map failure overwritten");
    Def bounded; bounded.section=KILN_GD_SEQUENCES; bounded.data=16;
    for (int attempt=0; attempt<20; ++attempt) ensure_seq(bounded, 3);
    if (!bounded.sequence_failures[3].permanent) throw std::runtime_error("recoverable retries were unbounded");
    std::puts("PASS sequence worlds: 13 runtime checks; 7 gamedata/content ABI assertions (the gamedata and content tables grew at their ends)");
}
