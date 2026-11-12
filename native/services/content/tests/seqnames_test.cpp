// SPDX-License-Identifier: MIT
// kiln.content gamedata, host test: a sequence clone keeps its name in every world of the process.
// kiln.assets serves a clone from the first world on; a later world's build_sections lists it with the game's sequences, under its copied
// sub-sequence name (taken by its base, so its GUID). Registering the clone in that world must give the entries the clone's names, or a
// block that names the clone ("kiln-equipment.sunlance.cast") fails in the second world (a joined server after a hosted game).
// Compiles the real gamedata_res.cpp; game-memory paths are stubbed or discarded at link time. seqnames_test.sh builds and runs it.
// Everything gamedata_res.cpp includes is parsed first: libstdc++ has its own __try, and the SEH blocks below are never entered here.
#include <windows.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include "../gamedata.h"
#undef __try
#define __try if (true)
#define __except(x) else if (false)
#include "../gamedata_res.cpp"

#include <cstdarg>
#include <cstdio>
#include <stdexcept>

namespace kc {
const KilnApi* g_api = nullptr;
std::string lower(std::string s) {
    for (char& c : s) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return s;
}
bool guid_parse(const std::string& s, uint8_t out[16]) {
    unsigned a, b, c, d[8];
    if (s.size() != 36 || std::sscanf(s.c_str(), "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x", &a, &b, &c, &d[0], &d[1], &d[2], &d[3], &d[4],
                                      &d[5], &d[6], &d[7]) != 11)
        return false;
    const uint32_t aa = a;
    const uint16_t bb = b, cc = c;
    std::memcpy(out, &aa, 4);
    std::memcpy(out + 4, &bb, 2);
    std::memcpy(out + 6, &cc, 2);
    for (int i = 0; i < 8; ++i) out[8 + i] = static_cast<uint8_t>(d[i]);
    return true;
}
// lookup_alias' game-memory paths (arsenals by template, skills by item): never taken for sequences
[[noreturn]] void unreachable(const char* what) { throw std::logic_error(std::string("a sequence lookup reached ") + what); }
bool rd(uintptr_t, void*, size_t) { unreachable("rd"); }
Registry& reg(uint32_t) { unreachable("reg"); }
const Entry* Registry::lookup(const std::string&) const { unreachable("Registry::lookup"); }
namespace gd {
const TField* TType::field(const std::string&) const { unreachable("TType::field"); }
bool resolve(Root, Node, const std::string&, std::vector<Resolved>*, std::string*, bool*) { unreachable("resolve"); }
}  // namespace gd
uint32_t fnv1a(const void* data, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(data)[i]) * 16777619u;
    return h;
}
}  // namespace kc

namespace {
int checks = 0;
void check(bool yes, const char* name) {
    if (!yes) throw std::runtime_error(name);
    ++checks;
}

using kc::gd::GEntry;
using kc::gd::GRegistry;

constexpr const char* kBaseGuid = "11111111-2222-4333-8444-555555555555";
constexpr const char* kCloneGuid = "9c5f2572-cb0f-5c58-977d-167b0cf7c52d";

void guid(const char* text, uint8_t out[16]) {
    if (!kc::guid_parse(text, out)) throw std::runtime_error("fixture GUID");
}

// What build_sections makes of one sequence resource with n sub-sequences named `sub`: names already taken become the GUID ("guid#k").
void list_resource(GRegistry& S, const char* guid_text, const char* sub, uint32_t n, uintptr_t data) {
    uint8_t g[16];
    guid(guid_text, g);
    for (uint32_t k = 0; k < n; ++k) {
        GEntry e;
        e.addr = data + k * 64;
        std::memcpy(e.guid, g, 16);
        e.sub = static_cast<int32_t>(k);
        e.id = kc::gd::sub_id(g, static_cast<int>(k));
        e.name = k ? std::string(sub) + "_" + std::to_string(k) : sub;
        if (e.name.empty() || S.by_name.count(e.name)) e.name = std::string(guid_text) + (k ? "#" + std::to_string(k) : "");
        S.add(std::move(e));
    }
}

GRegistry fresh() {
    GRegistry S;
    S.section = KILN_GD_SEQUENCES;
    return S;
}

// The first world: the clone is not a resource yet when the registry is built; registering it adds its entries by name.
void first_world() {
    GRegistry S = fresh();
    list_resource(S, kBaseGuid, "Attack_Staff_IceBolt", 2, 0x1000);
    uint8_t g[16];
    guid(kCloneGuid, g);
    kc::gd::sequence_clone_entries(S, g, "kiln-equipment.sunlance.cast", 0x9000, 2, 64);
    const GEntry* c = S.lookup("kiln-equipment.sunlance.cast");
    check(c && c->clone && c->sub == 0 && c->addr == 0x9000, "first world: the clone by its name");
    const GEntry* c1 = S.lookup("kiln-equipment.sunlance.cast#1");
    check(c1 && c1->sub == 1 && c1->addr == 0x9040, "first world: the clone's second sub-sequence by its name");
    check(S.lookup("Attack_Staff_IceBolt") && !S.lookup("Attack_Staff_IceBolt")->clone, "first world: the base keeps its name");
    check(S.entries.size() == 4, "first world: 2 + 2 entries");
}

// A later world of the same process: kiln.assets still serves the clone, so build_sections listed it with the game's sequences, under
// its GUID (its copied sub-sequence names are the base's). Registering it must name its entries, not only find them by id.
void later_world() {
    GRegistry S = fresh();
    list_resource(S, kBaseGuid, "Attack_Staff_IceBolt", 2, 0x1000);
    list_resource(S, kCloneGuid, "Attack_Staff_IceBolt", 2, 0x9000);  // the clone's bytes are the base's: same sub-sequence names
    check(S.lookup("kiln-equipment.sunlance.cast") == nullptr, "fixture: the listed clone has no name yet");
    uint8_t g[16];
    guid(kCloneGuid, g);
    kc::gd::sequence_clone_entries(S, g, "kiln-equipment.sunlance.cast", 0x9000, 2, 64);
    const GEntry* c = S.lookup("kiln-equipment.sunlance.cast");
    check(c != nullptr, "later world: the clone by its name (the block's sequence reference)");
    check(c->clone && c->sub == 0 && c->addr == 0x9000 && std::memcmp(c->guid, g, 16) == 0, "later world: the name leads to the clone");
    const GEntry* c1 = S.lookup("kiln-equipment.sunlance.cast#1");
    check(c1 && c1->clone && c1->sub == 1, "later world: the clone's second sub-sequence by its name");
    check(S.lookup("KILN-EQUIPMENT.SUNLANCE.CAST") == c, "later world: the name in any case");
    check(S.select("kiln-equipment.sunlance.*").size() == 2, "later world: a glob over the clone's name");
    check(S.lookup(kCloneGuid) == c, "later world: the clone by its GUID still");
    const GEntry* b = S.lookup("Attack_Staff_IceBolt");
    check(b && !b->clone && b->addr == 0x1000, "later world: the base keeps its name");
    check(S.lookup("Attack_Staff_IceBolt_1") && S.lookup("Attack_Staff_IceBolt_1")->addr == 0x1040, "later world: the base's sub keeps its name");
    check(S.entries.size() == 4, "later world: no second entry for the clone");
    // registering again (the next pass of the same world) changes nothing
    kc::gd::sequence_clone_entries(S, g, "kiln-equipment.sunlance.cast", 0x9000, 2, 64);
    check(S.lookup("kiln-equipment.sunlance.cast") == c && S.entries.size() == 4, "later world: registering twice");
}

// A name another entry already has stays that entry's (as add() does): the clone is not given it.
void name_taken() {
    GRegistry S = fresh();
    list_resource(S, kBaseGuid, "kiln-equipment.sunlance.cast", 1, 0x1000);
    list_resource(S, kCloneGuid, "kiln-equipment.sunlance.cast", 1, 0x9000);
    uint8_t g[16];
    guid(kCloneGuid, g);
    kc::gd::sequence_clone_entries(S, g, "kiln-equipment.sunlance.cast", 0x9000, 1, 64);
    const GEntry* n = S.lookup("kiln-equipment.sunlance.cast");
    check(n && n->addr == 0x1000, "name taken: it stays the first entry's");
}
}  // namespace

int main() {
    const struct {
        const char* name;
        void (*run)();
    } tests[] = {{"first world", first_world}, {"later world", later_world}, {"name taken", name_taken}};
    int failed = 0;
    for (const auto& t : tests) {
        try {
            t.run();
            std::printf("ok   %s\n", t.name);
        } catch (const std::exception& e) {
            std::printf("FAIL %s: %s\n", t.name, e.what());
            ++failed;
        }
    }
    std::printf("seqnames-test: %d check(s), %d test(s) failed\n", checks, failed);
    return failed ? 1 : 0;
}
