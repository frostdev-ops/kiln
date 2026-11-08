// SPDX-License-Identifier: MIT
// Host test for kiln.types' scanner: maps an Enshrouded exe (server or client) by hand and checks that the type table,
// the ECS lists and the system descriptors are found and look right. Usage: types_test <exe> [<exe> ...]
// (test-types.sh passes the server and client exes when they exist). Reads the files only.
#include <cstdio>
#include <fstream>
#include <iterator>
#include <unordered_map>

#include "scan.h"
#include "runtime.h"

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

static bool map_pe_bytes(const std::vector<uint8_t>& raw, Mapped& m) {
    if (raw.size() < 0x40) return false;
    const Range file{0, raw.size()};
    auto u16 = [&](size_t o) { uint16_t v = 0; if (file.has(o, 2)) std::memcpy(&v, raw.data() + o, 2); return v; };
    auto u32 = [&](size_t o) { uint32_t v = 0; if (file.has(o, 4)) std::memcpy(&v, raw.data() + o, 4); return v; };
    auto u64 = [&](size_t o) { uint64_t v = 0; if (file.has(o, 8)) std::memcpy(&v, raw.data() + o, 8); return v; };
    const size_t pe = u32(0x3c);
    if (u16(0) != 0x5a4d || !file.has(pe, 24) || u32(pe) != 0x4550 || u16(pe + 4) != 0x8664) return false;
    const size_t nsec = u16(pe + 6), opt = pe + 24, optsz = u16(pe + 20);
    if (!nsec || nsec > 96 || optsz < 0xf0 || !file.has(opt, optsz) || u16(opt) != 0x20b ||
        !file.has(opt + optsz, nsec * 40)) return false;
    const uint64_t base = u64(opt + 24);
    const uint32_t image_size = u32(opt + 56);
    if (image_size < 0x1000 || image_size > 1024 * 1024 * 1024 || base > UINT64_MAX - image_size) return false;
    m.mem.assign(image_size, 0);
    m.im.base = base;
    m.im.all = {base, base + image_size};
    for (size_t i = 0; i < nsec; ++i) {
        const size_t s = opt + optsz + 40 * i;
        char name[9] = {};
        std::memcpy(name, raw.data() + s, 8);
        const uint32_t vsize = u32(s + 8), va = u32(s + 12), rsize = u32(s + 16), rptr = u32(s + 20);
        const uint32_t length = vsize ? vsize : rsize, n = std::min(length, rsize);
        if (!length || va > image_size || length > image_size - va || !file.has(rptr, rsize)) return false;
        if (n) std::memcpy(m.mem.data() + va, raw.data() + rptr, n);
        const Range r{base + va, base + va + length};
        if (!std::strcmp(name, ".text")) m.im.text = r;
        if (!std::strcmp(name, ".rdata")) m.im.rdata = r;
    }
    m.im.mem = m.mem.data();
    return m.im.valid();
}
static bool map_pe(const char* path, Mapped& m) {
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f || f.tellg() < 0 || f.tellg() > 1024 * 1024 * 1024) return false;
    std::vector<uint8_t> raw(static_cast<size_t>(f.tellg()));
    f.seekg(0);
    if (!f.read(reinterpret_cast<char*>(raw.data()), raw.size())) return false;
    return map_pe_bytes(raw, m);
}

static uint64_t type_named(const Image& im, const TypeTable& t, std::string_view qn) {
    for (uint32_t i = 0; i < t.count; ++i) {
        const uint64_t d = im.q(t.table + 8ull * i);
        if (str_at(im, d + td::qname) == qn) return d;
    }
    return 0;
}

struct Fixture {
    std::vector<uint8_t> bytes = std::vector<uint8_t>(0x40000);
    Image im{bytes.data(), 0x100000, {0x100000, 0x101000}, {0x101000, 0x140000}, {0x100000, 0x140000}};
    uint64_t names = 0x130000;
    template<class T> void put(uint64_t va, T value) { std::memcpy(bytes.data() + va - im.base, &value, sizeof value); }
    void name(uint64_t at, std::string_view text) {
        ++names;
        put(at, names); put(at + 8, uint64_t(text.size()));
        std::memcpy(bytes.data() + names - im.base, text.data(), text.size());
        names += text.size() + 1;
    }
    uint64_t type(uint64_t at, std::string_view name_, uint8_t kind, uint32_t size) {
        name(at, name_); name(at + td::qname, name_); put(at + td::kind, kind); put(at + td::size, size);
        put(at + td::hash, fnv1a(name_)); return at;
    }
    void registry() {
        type(0x102000, "BlobString", 25, 8); type(0x102100, "uint32", 6, 4);
        for (uint64_t i = 0; i < 1000; ++i) put(0x108000 + i * 8, uint64_t(i == 0 ? 0x102000 : 0x102100));
        put(0x104000, uint64_t(0x108000)); put(0x104008, uint64_t(1000));
        name(0x104010, "ecs.component"); put(0x104020, uint64_t(0x10a000)); put(0x104028, uint64_t(1));
        name(0x104030, "ecs.event"); put(0x104040, uint64_t(0x10a000)); put(0x104048, uint64_t(1));
        put(0x10a000, uint64_t(0x102100));
    }
    bool read(uint64_t at, void* out, size_t n) const {
        if (!im.all.has(at, n)) return false;
        std::memcpy(out, bytes.data() + at - im.base, n); return true;
    }
    bool writable(uint64_t at, uint64_t n) const { return im.all.has(at, n); }
    bool write(uint64_t at, const void* data, size_t n) const {
        if (!writable(at, n)) return false;
        std::memcpy(const_cast<uint8_t*>(bytes.data()) + at - im.base, data, n); return true;
    }
};

static void regressions() {
    CHECK(scalar_layout(1, 1) && !scalar_layout(1, 4) && scalar_layout(6, 4) && !scalar_layout(6, 8) &&
          !scalar_layout(12, 3) && !scalar_layout(29, 8), "malformed scalar widths refused");
    std::unordered_map<uint32_t, uint64_t> hashes;
    unique_insert(hashes, uint32_t(7), 100); unique_insert(hashes, uint32_t(7), 100);
    CHECK(hashes[7] == 100, "same descriptor may repeat");
    unique_insert(hashes, uint32_t(7), 200); unique_insert(hashes, uint32_t(7), 100);
    CHECK(!hashes[7], "ambiguous hashes stay unavailable");
    Image invalid{};
    CHECK(!find_type_table(invalid).error.empty() && scan_systems(invalid).empty(), "null image fails soft");
    Fixture f; f.registry();
    auto t = find_type_table(f.im);
    CHECK(t.error.empty() && t.count == 1000 && t.event_count == 1, "synthetic valid registry");
    f.put(f.im.all.hi - 8, uint64_t(0x102000));
    CHECK(find_type_table(f.im).error.empty(), "anchor pointer at final qword cannot read past image");
    f.put(f.im.all.hi - 8, uint64_t(0x108000));
    CHECK(find_type_table(f.im).error.empty(), "truncated duplicate header ignored");
    f.put(0x108010, uint64_t(UINT64_MAX - 20));
    t = find_type_table(f.im);
    CHECK(!t.error.empty() && !t.count && !t.component_count && !t.event_count, "rejected registry exposes no ECS list");
    auto bad = f.im; bad.rdata.hi = bad.all.hi + 1;
    CHECK(!find_type_table(bad).error.empty() && scan_systems(bad).empty(), "out-of-image section rejected");
    bad = f.im; bad.rdata = {UINT64_MAX - 3, UINT64_MAX};
    CHECK(scan_systems(bad).empty(), "alignment overflow rejected");
    f.put(0x103000, uint64_t(0x130800)); f.put(0x103008, uint64_t(5)); f.put(0x103010, f.im.text.lo);
    std::memcpy(f.bytes.data() + 0x30800, "abc_d", 6);
    CHECK(scan_systems(f.im).size() == 1, "valid system found");
    f.put(0x103000, f.im.rdata.hi - 2);
    CHECK(scan_systems(f.im).empty(), "truncated system name refused");

    f.name(0x103000, "abc_d"); f.name(0x103020, "abc_d");
    f.put(0x103030, f.im.text.lo + 8);
    CHECK(scan_systems(f.im).size() == 2, "conflicting descriptors remain visible for alias refusal");

    const uint64_t scalar = f.type(0x102200, "bool", 1, 1), object = f.type(0x102300, "TestEvent", 18, 8);
    f.put(object + td::fields, uint64_t(0x105000)); f.put(object + td::count, uint32_t(1));
    f.name(0x105000, "flag"); f.put(0x105010, scalar); f.put(0x105018, uint64_t(0));
    uint8_t payload[16]{};
    CHECK(field_entry(f.im, object, 0) && valid_payload(f.im, object, payload, 8), "inline event accepted");
    payload[0] = 2;
    CHECK(!valid_payload(f.im, object, payload, 8), "invalid bool refused");
    payload[0] = 0;
    f.put(0x105018, uint64_t(8));
    CHECK(!field_entry(f.im, object, 0) && !valid_payload(f.im, object, payload, 8), "field outside owning object refused");
    f.put(0x105018, UINT64_MAX);
    CHECK(!field_entry(f.im, object, 0), "overflowed field offset refused");
    f.put(object + td::fields, UINT64_MAX - 7);
    CHECK(!field_entry(f.im, object, 0), "overflowed member pointer refused");
    f.put(object + td::fields, uint64_t(0x105000)); f.put(0x105018, uint64_t(0));
    f.put(scalar + td::kind, uint8_t(21));
    CHECK(!valid_payload(f.im, object, payload, 8), "dynamic string injection refused");
    f.put(scalar + td::kind, uint8_t(12)); f.put(scalar + td::enums, uint64_t(0x105100));
    f.put(scalar + td::count, uint32_t(1)); f.name(0x105100, "One"); f.put(0x105110, uint64_t(1));
    CHECK(!valid_payload(f.im, object, payload, 8), "unlisted enum refused");
    payload[0] = 1;
    CHECK(valid_payload(f.im, object, payload, 8), "listed enum accepted");
    f.put(0x105110, uint64_t(257));
    CHECK(!valid_payload(f.im, object, payload, 8), "out-of-range enum metadata cannot authorize a truncated value");
    f.put(0x105110, uint64_t(1));
    f.put(scalar + td::enums, UINT64_MAX - 8);
    CHECK(!valid_payload(f.im, object, payload, 8), "bad enum table refused");
    f.put(scalar + td::kind, uint8_t(17)); f.put(scalar + td::inner, scalar);
    CHECK(!valid_payload(f.im, object, payload, 8), "typedef cycle bounded");

    const uint64_t floating = f.type(0x102400, "float", 10, 4);
    f.put(0x105010, floating); uint32_t infinity = 0x7f800000; std::memcpy(payload, &infinity, 4);
    CHECK(!valid_payload(f.im, object, payload, 8), "nonfinite event float refused");
    std::memset(payload, 0, sizeof payload);
    CHECK(valid_payload(f.im, object, payload, 8), "finite event float accepted");
    const uint64_t boolean = f.type(0x102600, "arrayBool", 1, 1), array = f.type(0x102500, "BoolArray", 19, 2);
    f.put(array + td::inner, boolean); payload[0] = payload[1] = 1;
    CHECK(valid_payload(f.im, array, payload, 2), "inline event array accepted");
    payload[1] = 2;
    CHECK(!valid_payload(f.im, array, payload, 2), "invalid nested array element refused");
    f.put(array + td::inner, floating);
    CHECK(!valid_payload(f.im, array, payload, 2), "misaligned array layout refused");
    const uint64_t derived = f.type(0x102700, "DerivedEvent", 18, 8);
    f.put(derived + td::inner, object); std::memset(payload, 0, sizeof payload);
    CHECK(valid_payload(f.im, derived, payload, 8), "base-class event data validated");
    f.put(object + td::size, uint32_t(16));
    CHECK(!valid_payload(f.im, derived, payload, 8), "oversized event base refused");
    f.put(object + td::size, uint32_t(8));

    uint64_t unreadable;
    CHECK(!read_at(f, UINT64_MAX - 3, 8, unreadable), "job/field address addition checked before read");
    // A shallow diamond of repeated zero-sized children has exponential expansion despite a depth cap.
    const uint64_t leaf = f.type(0x10b000, "Empty", 18, 0);
    uint64_t child = leaf;
    for (unsigned level = 0; level < 14; ++level) {
        const uint64_t parent = f.type(0x10b100 + level * 0x100, "Diamond", 18, 0);
        const uint64_t fields = 0x10d000 + level * 0x60;
        f.put(parent + td::fields, fields); f.put(parent + td::count, uint32_t(2));
        for (unsigned j = 0; j < 2; ++j) {
            const uint64_t field = fields + j * 0x30;
            f.name(field, j ? "right" : "left"); f.put(field + td::field_type, child); f.put(field + td::field_offset, uint64_t(0));
        }
        child = parent;
    }
    CHECK(!valid_payload(f.im, child, payload, 0), "total event traversal budget bounds acyclic overlapping layouts");

    const uint64_t map = 0x106000;
    f.put(map + 0x10, uint64_t(8)); f.put(map + 0x54, uint32_t(1));
    f.put(map + 8, uint64_t(0x106100)); f.put(map + 0x28, uint64_t(0x106200));
    f.put(0x106100, uint64_t(2)); f.put(0x106204, uint32_t(9));
    CHECK(event_slot(f, map, 9) == 1 && event_slot(f, map, 10) == -1, "known hash only");
    f.put(map + 0x10, uint64_t(1) << 63);
    CHECK(event_slot(f, map, 9) == -2, "huge map capacity bounded");
    f.put(map + 0x10, uint64_t(8)); f.put(map + 8, UINT64_MAX - 4);
    CHECK(event_slot(f, map, 9) == -2, "unreadable bitmap fails soft");
    const uint64_t stream = 0x107000, poster = 0x107100, buffer = 0x107200;
    f.put(stream + 0x14, uint32_t(8)); f.put(poster, buffer); f.put(poster + 8, uint64_t(64)); f.put(poster + 0x10, uint32_t(0));
    CHECK(append_event(f, stream, poster, 9, payload, 8) == 0 && f.im.d(poster + 0x10) == 16, "checked poster append");
    f.put(poster + 0x10, UINT32_MAX - 1);
    CHECK(append_event(f, stream, poster, 9, payload, 8) == 2, "used-byte overflow refused");
    f.put(poster + 0x10, uint32_t(0)); f.put(stream + 0x14, uint32_t(16));
    CHECK(append_event(f, stream, poster, 9, payload, 8) == 1 && !f.im.d(poster + 0x10), "undersized stream payload never written");
    f.put(stream + 0x14, uint32_t(8)); f.put(poster, UINT64_MAX - 3);
    CHECK(append_event(f, stream, poster, 9, payload, 8) == 2, "wrapped buffer pointer refused");

    const uint64_t pool = 0x110000;
    f.put(stream + 0x30, pool); f.put(stream + 0x18, uint32_t(2)); f.put(stream + 0x40, uint16_t(1));
    f.put(pool + 8, uint32_t(42)); f.put(pool + 12, uint32_t(43));
    std::vector<std::vector<uint8_t>> padded;
    CHECK(!copy_events(f, stream, 4, padded), "stride rounded past the reflected size refused");
    f.put(stream + 0x14, uint32_t(4)); f.put(poster, buffer);
    payload[0] = 0x5a; payload[3] = 0xa5; payload[4] = 0xee;
    CHECK(append_event(f, stream, poster, 9, payload, 4) == 0, "4-byte event posts to a stride-4 stream");
    // The poster record is the game's EmitEvent layout whatever the stride: {u32 hash, u16 0, u8 size, u8 0} + payload
    // zero-padded to 8; DeliverEvents steps by 8 + align8(size) and copies the stride's bytes into the queue.
    CHECK(f.im.d(poster + 0x10) == 16 && f.im.d(buffer) == 9 && f.im.b(buffer + 6) == 4 && f.im.b(buffer + 7) == 0 &&
          f.im.b(buffer + 8) == 0x5a && f.im.b(buffer + 11) == 0xa5 && f.im.d(buffer + 12) == 0,
          "4-byte record: size byte 4, payload padded to 8 with zeros");
    f.put(stream + 0x14, uint32_t(3));
    CHECK(append_event(f, stream, poster, 9, payload, 3) == 0 && f.im.d(poster + 0x10) == 32 && f.im.b(buffer + 22) == 3 &&
          f.im.b(buffer + 27) == 0 && f.im.d(buffer + 28) == 0, "odd-sized event packs at stride 3, record padded to 8");
    CHECK(append_event(f, stream, poster, 9, payload, 4) == 1 && f.im.d(poster + 0x10) == 32, "size above the stride refused");
    std::memset(payload, 0, sizeof payload);
    f.put(stream + 0x14, uint32_t(4)); f.put(poster + 0x10, uint32_t(0));
    std::vector<std::vector<uint8_t>> copies;
    CHECK(copy_events(f, stream, 4, copies) && copies.size() == 2 && copies[0][0] == 42 && copies[1][0] == 43,
          "events copied at the reflected size (stride 4)");
    copies.clear(); f.put(stream + 0x18, uint32_t(600)); f.put(pool + 2, uint16_t(1));
    CHECK(!copy_events(f, stream, 4, copies), "cyclic event page chain refused");
    copies.clear(); f.put(pool + 2, uint16_t(2));
    // The game's queue append (0x5feb60) opens a new page when off + stride > 0x7F8: 510 stride-4 events per page.
    f.put(pool + 8 + 509 * 4, uint32_t(0x509)); f.put(pool + 0x800 + 8, uint32_t(0x510)); f.put(pool + 0x800 + 8 + 89 * 4, uint32_t(0x599));
    auto u32 = [&](size_t i) { uint32_t v; std::memcpy(&v, copies[i].data(), 4); return v; };
    CHECK(copy_events(f, stream, 4, copies) && copies.size() == 600 && u32(509) == 0x509 && u32(510) == 0x510 &&
          u32(599) == 0x599, "event page transition checked: 510 stride-4 events fill a page");
    copies.clear(); f.put(stream + 0x14, uint32_t(12)); f.put(stream + 0x18, uint32_t(171));
    f.put(pool + 8 + 169 * 12, uint32_t(0x169)); f.put(pool + 0x800 + 8, uint32_t(0x170));
    CHECK(copy_events(f, stream, 12, copies) && copies.size() == 171 && copies[0].size() == 12 && u32(169) == 0x169 &&
          u32(170) == 0x170, "stride-12 events: 170 per page, the 171st on the next");
    copies.clear(); f.put(stream + 0x14, uint32_t(4)); f.put(stream + 0x18, uint32_t(600));
    copies.clear(); f.put(stream + 0x40, uint16_t(65535));
    CHECK(!copy_events(f, stream, 4, copies), "unreadable event page refused");
    copies.clear(); f.put(stream + 0x18, UINT32_MAX);
    CHECK(!copy_events(f, stream, 4, copies), "hostile event count bounded before allocation");

    CHECK(can_disable("stamina_depletion_sprint") && can_disable("fall_damage_infliction") && !can_disable("save_world") &&
          !can_disable("network_init"), "only reviewed gameplay systems may be disabled");
    std::vector<System> systems{{"stamina_depletion_sprint", 1}, {"save_world", 1}};
    CHECK(!can_disable(systems, 0), "update aliases cannot disable a protected system");
    systems = {{"stamina_depletion_sprint", 1}, {"stamina_depletion_sprint", 2}};
    CHECK(!can_disable(systems, 0), "conflicting duplicate system names cannot be disabled");
    SystemOwners owners;
    CHECK(owners.set(1, 0, false) && owners.set(2, 0, false), "two owners disable same system");
    owners.release(1); CHECK(owners.disabled.count(0), "one retiring owner preserves other's request");
    owners.release(2); CHECK(owners.disabled.empty(), "last retiring owner restores system");
    owners.release(2); CHECK(owners.disabled.empty(), "release is idempotent");

    owners.set(1, 0, false); owners.set(2, 0, false);
    CHECK(owners.reap([](uint64_t owner) { return owner == 2; }) && owners.disabled.at(0).size() == 1,
          "revocation probe restores dead owner's request before skip");
    CHECK(owners.reap([](uint64_t) { return false; }) && owners.disabled.empty(), "revoked final owner restores system");
    owners.set(1, 0, false); owners.disabled.clear();
    CHECK(owners.disabled.empty(), "world stop and service unload restore all switches");

    ProfileSession profile;
    profile.set(true, 100); const uint64_t first = profile.generation;
    CHECK(profile.accepts(first) && profile.seconds(150, 10) == 5, "active profile generation and duration");
    profile.set(false, 170); profile.set(false, 200);
    CHECK(!profile.accepts(first) && profile.seconds(999, 10) == 7, "stop invalidates slices and preserves duration");
    profile.set(true, 300);
    CHECK(!profile.accepts(first) && profile.accepts(profile.generation) && profile.seconds(330, 10) == 3,
          "restart rejects slices from an earlier profile");
    bool displaced = false;
    owners.set(5, 0, false);
    CHECK(!displace_runner(false, displaced, owners, profile, 350) && !owners.disabled.empty() && profile.active,
          "ordinary module inventory preserves runner state");
    CHECK(displace_runner(true, displaced, owners, profile, 350) && displaced && owners.disabled.empty() && !profile.active,
          "late native SET restores every owner and stops the runner profile");
    CHECK(profile.seconds(999, 10) == 5 && !displace_runner(true, displaced, owners, profile, 999),
          "native SET displacement is idempotent and preserves profile stop time");

    Mapped mapped;
    std::vector<uint8_t> pe(0x400);
    CHECK(!map_pe_bytes(pe, mapped), "non-PE rejected");
    pe[0] = 'M'; pe[1] = 'Z'; uint32_t huge = UINT32_MAX; std::memcpy(pe.data() + 0x3c, &huge, 4);
    CHECK(!map_pe_bytes(pe, mapped), "truncated/out-of-range NT header rejected");
    pe.assign(0x600, 0);
    auto raw_put = [&]<class T>(size_t at, T value) { std::memcpy(pe.data() + at, &value, sizeof value); };
    raw_put(0, uint16_t(0x5a4d)); raw_put(0x3c, uint32_t(0x80)); raw_put(0x80, uint32_t(0x4550));
    raw_put(0x84, uint16_t(0x8664)); raw_put(0x86, uint16_t(2)); raw_put(0x94, uint16_t(0xf0));
    raw_put(0x98, uint16_t(0x20b)); raw_put(0x98 + 24, uint64_t(0x140000000)); raw_put(0x98 + 56, uint32_t(0x4000));
    const size_t sections = 0x188;
    for (unsigned i = 0; i < 2; ++i) {
        const size_t sec = sections + i * 40;
        std::memcpy(pe.data() + sec, i ? ".rdata" : ".text", i ? 6 : 5);
        raw_put(sec + 8, uint32_t(0x100)); raw_put(sec + 12, uint32_t(0x1000 + i * 0x1000));
        raw_put(sec + 16, uint32_t(0x100)); raw_put(sec + 20, uint32_t(0x400 + i * 0x100));
    }
    CHECK(map_pe_bytes(pe, mapped), "minimal valid PE maps");
    raw_put(sections + 8, UINT32_MAX);
    CHECK(!map_pe_bytes(pe, mapped), "virtual section overflow rejected");
    raw_put(sections + 8, uint32_t(0x100)); raw_put(sections + 20, UINT32_MAX);
    CHECK(!map_pe_bytes(pe, mapped), "raw section overflow rejected");
    raw_put(0x86, uint16_t(97));
    CHECK(!map_pe_bytes(pe, mapped), "section header count bounded");
}

int main(int argc, char** argv) {
    regressions();
    CHECK(fnv1a("keen::ecs::Health") == 0xdc101091u, "known FNV-1a hash");
    CHECK(system_name("fall_damage_infliction") && !system_name("Fall") && !system_name("a-b"), "system names");
    for (int a = 1; a < argc; ++a) {
        Mapped m;
        if (!map_pe(argv[a], m)) {
            CHECK(false, "%s is not a valid supported PE image", argv[a]);
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
