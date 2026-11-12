// SPDX-License-Identifier: MIT
// Test input for typedump: writes a small synthetic x64 PE whose .rdata holds a made-up type registry laid out as
// scan.h reads it: a struct with a base class, an enum, a bitmask, a static array, a typedef, a blob leaf whose inner
// pointer leads to a type that must not be dumped, an unreferenced type, a signed enum (through a typedef) with
// negative values, and a 1-byte enum whose stored u64 values carry high garbage bits. Every descriptor carries a non-zero
// flags byte and a default-value pointer at recognisable bytes (the marker below), which must never reach the dump.
//
//   mkimage <out.bin> [bad-enum]
// bad-enum makes the signed enum's underlying type a float, which typedump must refuse. The file is written with
// mode 0644: no execute permission.
#include "../../../native/services/types/scan.h"

#include <cstdio>
#include <filesystem>
#include <fstream>

using namespace ktypes;

namespace {

constexpr uint64_t kBase = 0x140000000ull;
constexpr uint32_t kText = 0x1000, kRdata = 0x2000, kRdataSize = 0x40000, kImage = kRdata + kRdataSize;
constexpr char kMarker[] = "KILN-DEFAULT-VALUE-BYTES-DO-NOT-DUMP";

struct Img {
    std::vector<uint8_t> b = std::vector<uint8_t>(kImage);  // file layout == image layout (raw offset == RVA)
    uint64_t strings = kBase + 0x30000, records = kBase + 0x20000;
    template<class T> void put(uint64_t va, T v) { std::memcpy(b.data() + (va - kBase), &v, sizeof v); }
    void raw(uint64_t rva, const void* p, size_t n) { std::memcpy(b.data() + rva, p, n); }
    uint64_t text(std::string_view s) {  // NUL-delimited on both sides, as find_cstr wants
        ++strings;
        const uint64_t at = strings;
        std::memcpy(b.data() + (at - kBase), s.data(), s.size());
        strings += s.size() + 1;
        return at;
    }
    void str(uint64_t at, std::string_view s) { put(at, text(s)); put(at + 8, uint64_t(s.size())); }
};

struct Def {
    const char *name, *qname;
    uint8_t kind;
    uint32_t size;
    uint16_t align;
};

}  // namespace

int main(int argc, char** argv) {
    const bool bad_enum = argc == 3 && std::string_view(argv[2]) == "bad-enum";
    if (argc != 2 && !bad_enum) { std::fprintf(stderr, "usage: mkimage <out.bin> [bad-enum]\n"); return 2; }
    Img m;
    // PE headers, as map_pe_bytes reads them.
    m.raw(0, "MZ", 2);
    uint32_t lfanew = 0x80; m.raw(0x3c, &lfanew, 4);
    m.raw(0x80, "PE\0\0", 4);
    uint16_t machine = 0x8664, nsec = 2, optsz = 0xf0, magic = 0x20b;
    m.raw(0x84, &machine, 2); m.raw(0x86, &nsec, 2); m.raw(0x94, &optsz, 2); m.raw(0x98, &magic, 2);
    uint64_t base = kBase; uint32_t image = kImage;
    m.raw(0x98 + 24, &base, 8); m.raw(0x98 + 56, &image, 4);
    struct { const char* name; uint32_t va, size; } secs[2] = {{".text", kText, 0x1000}, {".rdata", kRdata, kRdataSize}};
    for (int i = 0; i < 2; ++i) {
        const size_t s = 0x98 + 0xf0 + 40 * i;
        m.raw(s, secs[i].name, std::strlen(secs[i].name));
        m.raw(s + 8, &secs[i].size, 4); m.raw(s + 12, &secs[i].va, 4);
        m.raw(s + 16, &secs[i].size, 4); m.raw(s + 20, &secs[i].va, 4);
    }

    const uint64_t marker = kBase + 0x38000;
    m.raw(0x38000, kMarker, sizeof kMarker - 1);

    const Def defs[] = {
        {"BlobString", "test::BlobString", 25, 8, 8},   // 0: anchor, and a leaf
        {"uint32", "test::uint32", 6, 4, 4},             // 1: anchor
        {"uint8", "test::uint8", 2, 1, 1},               // 2
        {"Color", "test::Color", 12, 1, 1},              // 3
        {"ColorMask", "test::ColorMask", 13, 1, 1},      // 4
        {"StaticArray<uint32,2>", "test::StaticArray<uint32,2>", 19, 8, 4},  // 5
        {"Handle", "test::Handle", 17, 4, 4},            // 6
        {"Base", "test::Base", 18, 4, 4},                // 7
        {"Widget", "test::Widget", 18, 32, 8},           // 8: the component
        {"Ping", "test::Ping", 18, 4, 4},                // 9: the event
        {"Hidden", "test::Hidden", 18, 4, 4},            // 10: behind the blob leaf only
        {"Unused", "test::Unused", 18, 4, 4},            // 11: in the table, in no list
        {"sint8", "test::sint8", 3, 1, 1},               // 12
        {"Offset8", "test::Offset8", 17, 1, 1},          // 13: typedef of sint8
        {"Delta", "test::Delta", 12, 1, 1},              // 14: signed enum over Offset8
    };
    constexpr size_t n = sizeof defs / sizeof defs[0];
    uint64_t at[n];
    for (size_t i = 0; i < n; ++i) {
        const uint64_t t = at[i] = kBase + 0x3000 + 0x100 * i;
        m.str(t + td::name, defs[i].name);
        m.str(t + td::qname, defs[i].qname);
        m.put(t + td::size, defs[i].size);
        m.put(t + td::align, defs[i].align);
        m.put(t + td::kind, defs[i].kind);
        m.put(t + td::flags, uint8_t(0xA5));
        m.put(t + td::hash, fnv1a(defs[i].qname));
        m.put(t + td::defaults, marker);  // the bytes typedump must never read
        m.put(t + td::defaults + 8, uint64_t(sizeof kMarker - 1));
    }
    auto inner = [&](size_t t, size_t of) { m.put(at[t] + td::inner, at[of]); };
    auto fields = [&](size_t t, std::initializer_list<std::tuple<const char*, uint64_t, size_t>> fs) {
        const uint64_t p = m.records;
        uint32_t i = 0;
        for (auto [name, off, type] : fs) {
            const uint64_t e = p + td::field_stride * i++;
            m.str(e, name);
            m.put(e + td::field_type, at[type]);
            m.put(e + td::field_offset, off);
        }
        m.records += td::field_stride * i + 0x40;
        m.put(at[t] + td::fields, p);
        m.put(at[t] + td::count, i);
    };
    inner(0, 10);  // a leaf's inner: not followed
    inner(3, 2);
    m.put(at[3] + td::enums, m.records);
    m.put(at[3] + td::count, uint32_t(3));
    // Blue's stored u64 has garbage above the enum's one byte: it is written as 7.
    const std::pair<const char*, uint64_t> values[] = {{"Red", 0}, {"Green", 1}, {"Blue", 0xDEAD000000000007ull}};
    for (uint32_t i = 0; i < 3; ++i) {
        m.str(m.records + td::enum_stride * i, values[i].first);
        m.put(m.records + td::enum_stride * i + td::enum_value, values[i].second);
    }
    m.records += 0x100;
    if (bad_enum) m.put(at[12] + td::kind, uint8_t(10));
    inner(13, 12);
    inner(14, 13);
    m.put(at[14] + td::enums, m.records);
    m.put(at[14] + td::count, uint32_t(3));
    // Stored as the game may store them: Down as one byte 0xFE, Back as a full u64 -1. Both sign-extend.
    const std::pair<const char*, uint64_t> deltas[] = {{"Down", 0xFE}, {"Back", ~0ull}, {"Up", 1}};
    for (uint32_t i = 0; i < 3; ++i) {
        m.str(m.records + td::enum_stride * i, deltas[i].first);
        m.put(m.records + td::enum_stride * i + td::enum_value, deltas[i].second);
    }
    m.records += 0x100;
    inner(4, 3);
    inner(5, 1);
    inner(6, 1);
    fields(7, {{"id", 0, 1}});
    inner(8, 7);
    fields(8, {{"color", 4, 3}, {"mask", 5, 4}, {"delta", 6, 14}, {"counts", 8, 5}, {"handle", 16, 6}, {"label", 24, 0}});
    fields(9, {{"seq", 0, 1}});
    fields(10, {{"secret", 0, 1}});
    fields(11, {{"unused", 0, 1}});

    // The table: every descriptor once, then padded to the 1000 entries find_type_table wants with "uint32".
    const uint64_t table = kBase + 0x10000;
    for (uint64_t i = 0; i < 1000; ++i) m.put(table + 8 * i, i < n ? at[i] : at[1]);
    const uint64_t header = kBase + 0x5000;
    m.put(header, table);
    m.put(header + 8, uint64_t(1000));
    m.str(header + 0x10, "ecs.component");
    m.put(header + 0x20, kBase + 0x6000);
    m.put(header + 0x28, uint64_t(1));
    m.str(header + 0x30, "ecs.event");
    m.put(header + 0x40, kBase + 0x6100);
    m.put(header + 0x48, uint64_t(1));
    m.put(kBase + 0x6000, at[8]);
    m.put(kBase + 0x6100, at[9]);

    {
        std::ofstream f(argv[1], std::ios::binary | std::ios::trunc);
        if (!f || !f.write(reinterpret_cast<const char*>(m.b.data()), static_cast<std::streamsize>(m.b.size()))) {
            std::fprintf(stderr, "mkimage: cannot write %s\n", argv[1]);
            return 1;
        }
    }
    namespace fs = std::filesystem;
    std::error_code ec;
    fs::permissions(argv[1], fs::perms::owner_read | fs::perms::owner_write | fs::perms::group_read | fs::perms::others_read,
                    fs::perm_options::replace, ec);
    if (ec) { std::fprintf(stderr, "mkimage: cannot set the mode of %s\n", argv[1]); return 1; }
    return 0;
}
