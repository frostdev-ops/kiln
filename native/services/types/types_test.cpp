// SPDX-License-Identifier: MIT
// Host test for kiln.types' scanner: maps an Enshrouded exe (server or client) by hand and checks that the type table,
// the ECS lists and the system descriptors are found and look right. Usage: types_test <exe> [<exe> ...]
// (test-types.sh passes the server and client exes when they exist). Reads the files only.
#include <cstdio>
#include <fstream>
#include <iterator>

#include "scan.h"

using namespace ktypes;

static int g_fail = 0;
#define CHECK(c, ...)                                   \
    do {                                                \
        if (!(c)) {                                     \
            ++g_fail;                                   \
            std::printf("FAIL %s: ", #c);               \
            std::printf(__VA_ARGS__);                   \
            std::printf("\n");                          \
        }                                               \
    } while (0)

struct Mapped {
    std::vector<uint8_t> mem;
    Image im;
};

static bool map_pe(const char* path, Mapped& m) {
    std::ifstream f(path, std::ios::binary);
    std::vector<uint8_t> raw((std::istreambuf_iterator<char>(f)), {});
    if (raw.size() < 0x400) return false;
    auto u16 = [&](size_t o) { uint16_t v; std::memcpy(&v, &raw[o], 2); return v; };
    auto u32 = [&](size_t o) { uint32_t v; std::memcpy(&v, &raw[o], 4); return v; };
    auto u64 = [&](size_t o) { uint64_t v; std::memcpy(&v, &raw[o], 8); return v; };
    const size_t pe = u32(0x3c), nsec = u16(pe + 6), opt = pe + 24, optsz = u16(pe + 20);
    const uint64_t base = u64(opt + 24);
    const uint32_t image_size = u32(opt + 56);
    m.mem.assign(image_size, 0);
    m.im.base = base;
    m.im.all = {base, base + image_size};
    for (size_t i = 0; i < nsec; ++i) {
        const size_t s = opt + optsz + 40 * i;
        char name[9] = {};
        std::memcpy(name, &raw[s], 8);
        const uint32_t vsize = u32(s + 8), va = u32(s + 12), rsize = u32(s + 16), rptr = u32(s + 20);
        const uint32_t n = std::min(vsize ? vsize : rsize, rsize);
        if (va + n <= image_size && rptr + n <= raw.size()) std::memcpy(&m.mem[va], &raw[rptr], n);
        const Range r{base + va, base + va + vsize};
        if (!std::strcmp(name, ".text")) m.im.text = r;
        if (!std::strcmp(name, ".rdata")) m.im.rdata = r;
    }
    m.im.mem = m.mem.data();
    return m.im.text.lo && m.im.rdata.lo;
}

static uint64_t type_named(const Image& im, const TypeTable& t, std::string_view qn) {
    for (uint32_t i = 0; i < t.count; ++i) {
        const uint64_t d = im.q(t.table + 8ull * i);
        if (str_at(im, d + td::qname) == qn) return d;
    }
    return 0;
}

int main(int argc, char** argv) {
    CHECK(fnv1a("keen::ecs::Health") == fnv1a("keen::ecs::Health"), "fnv");
    CHECK(system_name("fall_damage_infliction") && !system_name("Fall") && !system_name("a-b"), "system names");
    for (int a = 1; a < argc; ++a) {
        Mapped m;
        if (!map_pe(argv[a], m)) {
            std::printf("skip %s (not a PE file)\n", argv[a]);
            continue;
        }
        const Image& im = m.im;
        const TypeTable t = find_type_table(im);
        CHECK(t.error.empty(), "%s: %s", argv[a], t.error.c_str());
        CHECK(t.count > 10000, "%s: %u types", argv[a], t.count);
        CHECK(t.component_count > 600 && t.event_count > 100, "%s: %u components, %u events", argv[a], t.component_count,
              t.event_count);
        const uint64_t health = type_named(im, t, "keen::ecs::Health");
        CHECK(health != 0, "keen::ecs::Health is listed");
        if (health) {
            const uint64_t base = im.q(health + td::inner);
            CHECK(str_at(im, base + td::qname) == "keen::ecs::SInt32Attribute", "Health's base class is %.*s",
                  int(str_at(im, base + td::qname).size()), str_at(im, base + td::qname).data());
            CHECK(im.b(health + td::kind) == 0x12, "Health is a struct");
        }
        // An enum with values, and a struct with a default value.
        const uint64_t aa = type_named(im, t, "keen::ecs::ItemRarity");
        uint32_t enum_values = aa ? im.d(aa + td::count) : 0;
        CHECK(!aa || (im.b(aa + td::kind) == 0x0C && enum_values >= 4), "ItemRarity is an enum with values");
        uint32_t with_defaults = 0;
        for (uint32_t i = 0; i < t.count; ++i) with_defaults += im.q(im.q(t.table + 8ull * i) + td::defaults) != 0;
        CHECK(with_defaults > 500, "%u types have defaults", with_defaults);
        const auto sys = scan_systems(im);
        CHECK(sys.size() > 500, "%s: %zu systems", argv[a], sys.size());
        bool sprint = false, fall = false;
        for (const auto& s : sys) {
            sprint |= s.name == "stamina_depletion_sprint";
            fall |= s.name == "fall_damage_infliction";
        }
        CHECK(sprint && fall, "known systems are listed");
        std::printf("%s: %u types (%u with defaults), %u components, %u events, %zu systems\n", argv[a], t.count,
                    with_defaults, t.component_count, t.event_count, sys.size());
    }
    std::printf(g_fail ? "types_test: %d FAILED\n" : "types_test: PASS\n", g_fail);
    return g_fail != 0;
}
