// SPDX-License-Identifier: MIT
// Host tests of sigdb's disk scan (native/core/src/pe_text.h + sigdb_pattern.h): PE parsing, base relocations,
// file-offset -> live-address translation, rel32 captures from the file's bytes, and live/file diff runs.
//
//   native/core/tests/test-sigdisk.sh [--server <enshrouded_server.exe>] [--client <enshrouded.exe>]
//
// Part 1 builds a small PE in memory (x64, .text + .reloc, preferred base 0x140000000) and "loads" it at another
// base. Part 2 (with real exe paths) parses the game exe, resolves every unscoped sigdb symbol on the file's bytes,
// then writes a 5-byte jmp over each resolved function in a copy that stands for the live code (as an engine that
// hooks first does) and shows that a live scan loses those symbols while the disk scan keeps every one, at the same
// live addresses and with the same captures.
#include <cassert>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <string>
#include <vector>

#include "../src/pe_text.h"
#include "../src/sigdb_pattern.h"
#include "nlohmann/json.hpp"

using namespace kiln;

static int g_fail = 0;
#define CHECK(cond, ...)                                  \
    do {                                                  \
        if (cond) {                                       \
            std::printf("PASS " __VA_ARGS__);             \
        } else {                                          \
            std::printf("FAIL " __VA_ARGS__);             \
            ++g_fail;                                     \
        }                                                 \
        std::printf("\n");                                \
    } while (0)

template <class T>
static void put(std::vector<uint8_t>& b, size_t off, T v) {
    if (b.size() < off + sizeof v) b.resize(off + sizeof v);
    std::memcpy(b.data() + off, &v, sizeof v);
}

// A minimal PE32+: headers at 0, .text at RVA 0x1000 (file 0x400, 0x200 bytes), .reloc at RVA 0x2000 (file 0x600).
struct TestPe {
    std::vector<uint8_t> file;
    static constexpr uint64_t kBase = 0x140000000ull;
    static constexpr uint32_t kText = 0x1000, kTextRaw = 0x400, kTextSize = 0x200;
    static constexpr uint32_t kReloc = 0x2000, kRelocRaw = 0x600;
    // The function sigdb looks for, at RVA 0x1010:
    //   48 B8 <imm64 = base + 0x1100>    mov rax, &data         (DIR64 fixup at RVA 0x1012)
    //   E8 <rel32 -> RVA 0x1180>         call helper
    //   8B 81 34 12 00 00                mov eax, [rcx + 0x1234]
    //   C3
    static constexpr uint32_t kFunc = 0x1010, kImm = 0x1012, kHelper = 0x1180;

    TestPe() {
        file.assign(0x800, 0);
        put<uint16_t>(file, 0, 0x5A4D);
        put<uint32_t>(file, 0x3C, 0x40);
        put<uint32_t>(file, 0x40, 0x00004550);
        put<uint16_t>(file, 0x44, 0x8664);  // machine
        put<uint16_t>(file, 0x46, 2);       // sections
        put<uint16_t>(file, 0x54, 0xF0);    // optional header size
        const size_t opt = 0x58;
        put<uint16_t>(file, opt, 0x20B);
        put<uint64_t>(file, opt + 24, kBase);
        put<uint32_t>(file, opt + 60, 0x400);  // SizeOfHeaders
        put<uint32_t>(file, opt + 56, 0x3000);  // SizeOfImage
        put<uint32_t>(file, opt + 108, 16);     // NumberOfRvaAndSizes
        size_t sec = opt + 0xF0;
        std::memcpy(file.data() + sec, ".text\0\0\0", 8);
        put<uint32_t>(file, sec + 8, kTextSize - 4);  // VirtualSize: the last DIR64 fixup straddles its end
        put<uint32_t>(file, sec + 12, kText);
        put<uint32_t>(file, sec + 16, kTextSize);
        put<uint32_t>(file, sec + 20, kTextRaw);
        sec += 40;
        std::memcpy(file.data() + sec, ".reloc\0\0", 8);
        put<uint32_t>(file, sec + 8, 0x200);
        put<uint32_t>(file, sec + 12, kReloc);
        put<uint32_t>(file, sec + 16, 0x200);
        put<uint32_t>(file, sec + 20, kRelocRaw);

        uint8_t* t = file.data() + kTextRaw;
        std::memset(t, 0xCC, kTextSize);
        size_t f = kFunc - kText;
        t[f++] = 0x48, t[f++] = 0xB8;
        const uint64_t imm = kBase + 0x1100;
        std::memcpy(t + f, &imm, 8), f += 8;
        t[f++] = 0xE8;
        const int32_t rel = static_cast<int32_t>(kHelper - (kFunc + 15));
        std::memcpy(t + f, &rel, 4), f += 4;
        const uint8_t tail[] = {0x8B, 0x81, 0x34, 0x12, 0x00, 0x00, 0xC3};
        std::memcpy(t + f, tail, sizeof tail);
        // A HIGHLOW fixup at RVA 0x1100 (a 32-bit absolute in a jump table, say) holding base low half + 0x1180.
        put<uint32_t>(file, kTextRaw + 0x100, static_cast<uint32_t>(kBase) + kHelper);
        // A DIR64 at the end of .text's virtual size, half inside it.
        put<uint64_t>(file, kTextRaw + kTextSize - 8, kBase + 0x1000);

        // .reloc: one block for page 0x1000: DIR64 @0x012, HIGHLOW @0x100, DIR64 @0x1F8, an ABSOLUTE pad.
        const size_t r = kRelocRaw;
        put<uint32_t>(file, r, 0x1000);
        put<uint32_t>(file, r + 4, 8 + 4 * 2);
        put<uint16_t>(file, r + 8, static_cast<uint16_t>((10 << 12) | 0x012));
        put<uint16_t>(file, r + 10, static_cast<uint16_t>((3 << 12) | 0x100));
        put<uint16_t>(file, r + 12, static_cast<uint16_t>((10 << 12) | 0x1F8));
        put<uint16_t>(file, r + 14, 0);
        put<uint32_t>(file, opt + 112 + 5 * 8, kReloc);
        put<uint32_t>(file, opt + 112 + 5 * 8 + 4, 16);
    }
};

static void synthetic() {
    TestPe pe;
    pe::FileText ft;
    std::string err;
    CHECK(pe::parse_file_text(pe.file.data(), pe.file.size(), ft, err), "test PE parses (%s)", err.c_str());
    CHECK(ft.image_base == TestPe::kBase && ft.text_rva == 0x1000 && ft.text_raw == 0x400,
          "test PE: preferred base, .text RVA and file offset");
    CHECK(ft.file_bytes() == TestPe::kTextSize - 4, "test PE: file bytes = min(virtual, raw) size");
    CHECK(ft.relocs64.size() == 2 && ft.relocs64[0] == 0x1012 && ft.relocs64[1] == 0x11F8 && ft.relocs32.size() == 1 &&
              ft.relocs32[0] == 0x1100,
          "test PE: fixups in .text found (2 DIR64, 1 HIGHLOW)");

    // The file's .text, then the same bytes as the loader leaves them at another base.
    std::vector<uint8_t> text(pe.file.begin() + ft.text_raw, pe.file.begin() + ft.text_raw + ft.file_bytes());
    CHECK(pe::relocate_text(text.data(), text.size(), ft, TestPe::kBase) == 0, "no fixups at the preferred base");
    const uint64_t live_base = 0x00006FFFFC670000ull;  // where Wine put the server exe in the lab
    const size_t fixed = pe::relocate_text(text.data(), text.size(), ft, live_base);
    CHECK(fixed == 3, "3 fixups applied at another base (got %zu)", fixed);
    uint64_t imm = 0;
    std::memcpy(&imm, text.data() + (TestPe::kImm - ft.text_rva), 8);
    CHECK(imm == live_base + 0x1100, "DIR64 relocated: 0x%llx", static_cast<unsigned long long>(imm));
    uint32_t lo = 0;
    std::memcpy(&lo, text.data() + 0x100, 4);
    CHECK(lo == static_cast<uint32_t>(live_base + TestPe::kHelper), "HIGHLOW relocated");
    uint32_t straddle = 0;  // the in-range 4 bytes of the straddling DIR64: the low half of the sum
    std::memcpy(&straddle, text.data() + text.size() - 4, 4);
    CHECK(straddle == static_cast<uint32_t>(live_base + 0x1000), "straddling DIR64: in-range bytes relocated");

    // The "live" image: relocated .text, then another engine's 5-byte jmp over the function's prologue.
    std::vector<uint8_t> live = text;
    const size_t f = TestPe::kFunc - ft.text_rva;
    live[f] = 0xE9;
    const int32_t relay = 0x10000;
    std::memcpy(live.data() + f + 1, &relay, 4);

    pe::CodeView disk{text.data(), text.size(), reinterpret_cast<uintptr_t>(live.data()), true};
    pe::CodeView mem{live.data(), live.size(), reinterpret_cast<uintptr_t>(live.data()), false};
    sigdb::Pattern p;
    CHECK(sigdb::parse_pattern("48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32} 8B 81 {field:d32} C3", p, err),
          "pattern parses");
    CHECK(sigdb::scan(p, mem.scan, mem.end(), 2).empty(), "hooked live code: the pattern no longer matches");
    const auto hits = sigdb::scan(p, disk.scan, disk.end(), 2);
    CHECK(hits.size() == 1, "disk view: exactly one match");
    if (hits.size() != 1) return;
    const uintptr_t at = disk.to_live(hits[0]);
    CHECK(at == reinterpret_cast<uintptr_t>(live.data()) + f, "hit translated to the live address (rva 0x%x)",
          static_cast<unsigned>(at - reinterpret_cast<uintptr_t>(live.data()) + ft.text_rva));
    CHECK(disk.to_scan(at) == hits[0], "live address translated back to the file byte");
    CHECK(disk.to_scan(disk.live + disk.size) == nullptr && disk.to_scan(disk.live - 1) == nullptr,
          "addresses outside .text have no file byte");
    int64_t helper = 0, field = 0;
    sigdb::hole_value(p, hits[0], "helper", &helper, disk.delta());
    sigdb::hole_value(p, hits[0], "field", &field, disk.delta());
    CHECK(static_cast<uintptr_t>(helper) == reinterpret_cast<uintptr_t>(live.data()) + (TestPe::kHelper - ft.text_rva),
          "rel32 capture from the file's bytes is the live target");
    CHECK(field == 0x1234, "d32 capture from the file's bytes");
    int64_t wrong = 0;
    sigdb::hole_value(p, hits[0], "helper", &wrong);
    CHECK(static_cast<uintptr_t>(wrong) != static_cast<uintptr_t>(helper), "without the delta the rel32 target is not live");

    size_t differing = 0;
    const auto runs = pe::diff_runs(text.data(), live.data(), text.size(), ft.text_rva, &differing);
    CHECK(runs.size() == 1 && runs[0].rva == TestPe::kFunc && differing >= 4 && runs[0].len <= 5,
          "diff runs: the hook at rva 0x%x, %zu bytes differ", runs.empty() ? 0u : runs[0].rva, differing);

    // Broken files fail cleanly.
    std::vector<uint8_t> bad = pe.file;
    put<uint32_t>(bad, 0x3C, 0x7FFFFFF0);
    CHECK(!pe::parse_file_text(bad.data(), bad.size(), ft, err), "bad e_lfanew rejected (%s)", err.c_str());
    bad = pe.file;
    bad.resize(0x500);
    CHECK(!pe::parse_file_text(bad.data(), bad.size(), ft, err), "truncated .text rejected (%s)", err.c_str());
    bad = pe.file;
    put<uint16_t>(bad, 0x58, 0x10B);
    CHECK(!pe::parse_file_text(bad.data(), bad.size(), ft, err), "PE32 rejected (%s)", err.c_str());
}

static void hostile_pe() {
    const TestPe good;
    auto rejected = [&](std::vector<uint8_t> b, const char* what) {
        pe::FileText ft;
        std::string err;
        CHECK(!pe::parse_file_text(b.data(), b.size(), ft, err), "%s rejected (%s)", what, err.c_str());
    };
    auto b = good.file;
    put<uint16_t>(b, 0x54, 2);
    rejected(b, "undersized optional header");
    b = good.file;
    put<uint32_t>(b, 0x58 + 108, 17);
    rejected(b, "data directories exceeding optional header");
    b = good.file;
    put<uint32_t>(b, 0x148 + 12, 0xFFFFFF00);
    rejected(b, "overflowing .text RVA range");
    b = good.file;
    put<uint32_t>(b, 0x148 + 8, 0x3000);
    rejected(b, ".text beyond SizeOfImage");
    b = good.file;
    put<uint32_t>(b, 0x58 + 112 + 5 * 8 + 4, 0x400);
    rejected(b, "relocation directory beyond its section");
    b = good.file;
    put<uint32_t>(b, TestPe::kRelocRaw + 4, 18);
    rejected(b, "truncated relocation block");
    b = good.file;
    put<uint32_t>(b, TestPe::kRelocRaw + 4, 9);
    rejected(b, "odd relocation block");
    b = good.file;
    put<uint32_t>(b, TestPe::kRelocRaw, 0xFFFFF000);
    rejected(b, "relocation page beyond image");
    b = good.file;
    put<uint16_t>(b, TestPe::kRelocRaw + 8, (1 << 12) | 0x12);
    rejected(b, "unsupported relocation touching .text");
    b = good.file;
    put<uint16_t>(b, TestPe::kRelocRaw + 10, (3 << 12) | 0x12);
    rejected(b, "overlapping relocation fields");

    // A field beginning before .text needs its missing low bytes to compute carry into .text.
    // Guessing those bytes as zero is unsafe; reject the disk view rather than silently misrelocate it.
    b = good.file;
    put<uint32_t>(b, 0x148 + 12, 0x1004);
    put<uint16_t>(b, TestPe::kRelocRaw + 8, (10 << 12));
    rejected(b, "relocation crossing the start of .text");

    bool truncations_rejected = true;
    for (size_t size = 0; size < good.file.size(); ++size) {
        pe::FileText ft;
        std::string err;
        if (pe::parse_file_text(good.file.data(), size, ft, err)) truncations_rejected = false;
    }
    CHECK(truncations_rejected, "all 2048 truncated prefixes rejected");

    // A valid DIR64 may cross a 4KiB page boundary while remaining wholly in .text, including its zero fill.
    b = good.file;
    put<uint32_t>(b, 0x58 + 56, 0x4000);
    put<uint32_t>(b, 0x148 + 8, 0x1008);
    put<uint32_t>(b, 0x148 + 40 + 12, 0x3000);
    put<uint32_t>(b, 0x58 + 112 + 5 * 8, 0x3000);
    put<uint16_t>(b, TestPe::kRelocRaw + 8, (10 << 12) | 0xFFC);
    pe::FileText ft;
    std::string err;
    CHECK(pe::parse_file_text(b.data(), b.size(), ft, err), "page-spanning relocation parses (%s)", err.c_str());
    std::vector<uint8_t> text(ft.text_vsize, 0);
    std::memcpy(text.data(), b.data() + ft.text_raw, ft.file_bytes());
    pe::relocate_text(text.data(), text.size(), ft, TestPe::kBase - 0x10000);
    uint64_t patched = 0;
    std::memcpy(&patched, text.data() + 0xFFC, 8);
    CHECK(patched == uint64_t{0} - 0x10000, "page-spanning DIR64 applies a negative base delta");
}

struct Def {
    std::string name;
    sigdb::Pattern p;
    int64_t offset;
};

static std::vector<Def> unscoped_defs(const std::filesystem::path& sigdb_dir, const std::string& side) {
    std::vector<std::filesystem::path> files{sigdb_dir / (side + ".json")};
    std::vector<std::filesystem::path> extra;
    for (auto& e : std::filesystem::directory_iterator(sigdb_dir / (side + ".d")))
        if (e.path().extension() == ".json") extra.push_back(e.path());
    std::sort(extra.begin(), extra.end());
    files.insert(files.end(), extra.begin(), extra.end());
    std::vector<Def> out;
    for (auto& file : files) {
        std::ifstream in(file);
        auto doc = nlohmann::json::parse(in, nullptr, true, true);
        for (auto& [name, sym] : doc["symbols"].items()) {
            if (name[0] == '@') continue;
            for (auto& f : sym["find"]) {
                if (f.contains("scope")) continue;
                Def d{name, {}, f.value("offset", 0)};
                std::string err;
                if (sigdb::parse_pattern(f.value("pattern", ""), d.p, err)) out.push_back(std::move(d));
                break;  // the first unscoped strategy, as sigdb tries it first
            }
        }
    }
    return out;
}

static void real_exe(const std::string& exe, const std::string& side, const std::filesystem::path& sigdb_dir) {
    std::ifstream in(exe, std::ios::binary);
    if (!in) {
        std::printf("SKIP %s: cannot read %s\n", side.c_str(), exe.c_str());
        return;
    }
    std::vector<uint8_t> file((std::istreambuf_iterator<char>(in)), {});
    pe::FileText ft;
    std::string err;
    CHECK(pe::parse_file_text(file.data(), file.size(), ft, err), "%s exe parses (%s)", side.c_str(), err.c_str());
    std::printf("INFO %s: .text rva 0x%x, 0x%x bytes in the file, %zu DIR64 + %zu HIGHLOW fixups in .text\n",
                side.c_str(), ft.text_rva, ft.file_bytes(), ft.relocs64.size(), ft.relocs32.size());
    std::vector<uint8_t> text(file.begin() + ft.text_raw, file.begin() + ft.text_raw + ft.file_bytes());
    const uint64_t live_base = 0x00006FFFFC670000ull;
    pe::relocate_text(text.data(), text.size(), ft, live_base);

    // Resolve on the file's bytes, then hook every resolved function in the "live" copy.
    const auto defs = unscoped_defs(sigdb_dir, side);
    std::vector<uint8_t> live = text;
    pe::CodeView disk{text.data(), text.size(), reinterpret_cast<uintptr_t>(live.data()), true};
    pe::CodeView mem{live.data(), live.size(), reinterpret_cast<uintptr_t>(live.data()), false};
    std::map<std::string, uintptr_t> on_disk;
    for (const Def& d : defs) {
        const auto h = sigdb::scan(d.p, disk.scan, disk.end(), 2);
        if (h.size() == 1) on_disk[d.name] = disk.to_live(h[0]) + d.offset;
    }
    for (auto& [name, addr] : on_disk) {
        uint8_t* at = live.data() + (addr - mem.live);
        if (at + 5 > live.data() + live.size()) continue;
        at[0] = 0xE9;
        const int32_t rel = 0x7FFF0000;
        std::memcpy(at + 1, &rel, 4);
    }
    size_t kept = 0, lost = 0, same = 0;
    for (const Def& d : defs) {
        if (!on_disk.count(d.name)) continue;
        const auto live_hits = sigdb::scan(d.p, mem.scan, mem.end(), 2);
        if (live_hits.size() == 1) ++kept;
        else ++lost;
        const auto disk_hits = sigdb::scan(d.p, disk.scan, disk.end(), 2);
        if (disk_hits.size() == 1 && disk.to_live(disk_hits[0]) + d.offset == on_disk[d.name]) ++same;
    }
    std::printf("INFO %s: %zu unscoped symbols resolve on the file; with each function hooked, a live scan keeps %zu "
                "and loses %zu\n",
                side.c_str(), on_disk.size(), kept, lost);
    CHECK(lost > 0, "%s: hooks hide symbols from a live scan (%zu lost)", side.c_str(), lost);
    CHECK(same == on_disk.size(), "%s: the disk scan still resolves all %zu, at the same live addresses", side.c_str(),
          on_disk.size());
    size_t differing = 0;
    const auto runs = pe::diff_runs(text.data(), live.data(), text.size(), ft.text_rva, &differing);
    CHECK(runs.size() <= on_disk.size() && differing <= 5 * on_disk.size(),
          "%s: diff runs find the %zu hooks (%zu runs, %zu bytes)", side.c_str(), on_disk.size(), runs.size(),
          differing);
    if (side != "client") return;
    // ShroudForge's native runtime sends the main menu's two Settings calls to a relay next to the image, before
    // Kiln's scan (players' reports of 10 and 13 Nov 2026). On this exe's own bytes: both are calls to one function
    // in the image, and with only their operands changed the difference check keeps the disk scan.
    std::vector<uint8_t> sf = text;
    const uintptr_t l_text = live_base + ft.text_rva, relay = live_base + ft.size_of_image + 0x10000;
    size_t sites = 0;
    int64_t entry = 0;
    bool one_entry = true;
    for (const char* name : {"ui/MainMenuSettingsA", "ui/MainMenuSettingsB"}) {
        const auto it = on_disk.find(name);
        if (it == on_disk.end()) continue;
        const size_t ret = it->second - mem.live;  // the symbol is the call's return address
        if (ret < 5 || ret > sf.size() || sf[ret - 5] != 0xE8) continue;
        int32_t was = 0;
        std::memcpy(&was, &sf[ret - 4], 4);
        const int64_t target = static_cast<int64_t>(ret) + was;
        if (!sites) entry = target;
        one_entry = one_entry && target == entry;
        const int32_t now = static_cast<int32_t>(static_cast<int64_t>(relay) - static_cast<int64_t>(l_text + ret));
        std::memcpy(&sf[ret - 4], &now, 4);
        ++sites;
    }
    CHECK(sites == 2 && one_entry && entry > 0 && static_cast<size_t>(entry) < text.size(),
          "client: the main menu's two Settings calls are on the file and call one function in the image (%zu found)", sites);
    CHECK(pe::only_hook_differences(text.data(), sf.data(), text.size(), l_text, live_base, ft.size_of_image),
          "client: with both Settings calls sent to a relay outside the image, the disk scan stays");
    sf[sf.size() / 2] ^= 1;
    const auto beside = pe::other_differences(text.data(), sf.data(), text.size(), l_text, live_base, ft.size_of_image);
    CHECK(!pe::only_hook_differences(text.data(), sf.data(), text.size(), l_text, live_base, ft.size_of_image) &&
              beside.sites == 1 && beside.first == std::vector<uint32_t>{static_cast<uint32_t>(sf.size() / 2)},
          "client: another changed byte beside the redirected calls is one in-place patch, the calls are not");
    // The Worst Map Mod 1.3.49 patches four places of the client in place before Kiln's scan (its status file in a
    // player's report of 14 Nov 2026, client build 26430625; on another build these are four other bytes, which is
    // the same to the check). Up to 0.2.0-dev.27 that sent the whole scan to the live code.
    std::vector<uint8_t> wm = text;
    const uint32_t patched[] = {0x31629d, 0x3168c9, 0xd33058, 0xd331ee};
    std::vector<uint32_t> offsets;
    for (uint32_t rva : patched)
        if (rva >= ft.text_rva && rva - ft.text_rva < wm.size()) wm[rva - ft.text_rva] ^= 0x01, offsets.push_back(rva - ft.text_rva);
    const auto in_place = pe::other_differences(text.data(), wm.data(), text.size(), l_text, live_base, ft.size_of_image);
    CHECK(offsets.size() == 4 && in_place.sites == 4 && in_place.bytes == 4 && in_place.first == offsets,
          "client: four bytes patched in place are four in-place patches at their offsets (%zu found)", in_place.sites);
    // The same four places in the shapes such patches usually have (the mod's own bytes are not known here): a
    // conditional jump made unconditional, a short conditional jump turned into `jmp`, and instructions filled with
    // NOPs. A jump that stays inside the image and NOPs behind no jump are no hook. Only on the build whose bytes
    // these are (client 26430625).
    std::vector<uint8_t> shaped = text;
    const size_t skip = 0x31629d - ft.text_rva, vis = 0x3168c9 - ft.text_rva, quest = 0xd33058 - ft.text_rva,
                 lore = 0xd331ee - ft.text_rva;
    if (lore + 3 <= shaped.size() && shaped[skip] == 0x0F && shaped[skip + 1] == 0x84 && shaped[quest] == 0x75 &&
        shaped[lore] == 0x0F && shaped[lore + 1] == 0x44 && shaped[vis] == 0x48 && shaped[vis + 1] == 0x8B) {
        int32_t rel = 0;
        std::memcpy(&rel, &shaped[skip + 2], 4);
        ++rel;  // jz rel32 (6 bytes) -> jmp rel32 (5 bytes) to the same place, and a NOP
        shaped[skip] = 0xE9;
        std::memcpy(&shaped[skip + 1], &rel, 4);
        shaped[skip + 5] = 0x90;
        std::fill(shaped.begin() + vis, shaped.begin() + vis + 5, 0x90);    // mov rax, [rsp+58h]
        shaped[quest] = 0xEB;                                               // jnz short -> jmp short
        std::fill(shaped.begin() + lore, shaped.begin() + lore + 3, 0x90);  // cmovz ebx, ebp
        const auto s4 = pe::other_differences(text.data(), shaped.data(), text.size(), l_text, live_base, ft.size_of_image);
        CHECK(s4.sites == 4 && s4.first == offsets &&
                  !pe::only_hook_differences(text.data(), shaped.data(), text.size(), l_text, live_base, ft.size_of_image),
              "client: a jump inside the image, a flipped short jump and NOP fills are four in-place patches (%zu sites, %zu bytes)",
              s4.sites, s4.bytes);
    } else {
        std::printf("INFO client: not build 26430625 at the four places; the shaped patches are skipped\n");
    }
    // The production view selection on this exe, hooked and patched: sigdb_runtime_test (real_client_in_place).
}

// A hook is a jump out of the image and the NOPs behind it. Greeneye Loader's is the longest: a 12-byte jump and up
// to 20 NOPs (Find hooks 12 to 18 bytes). One NOP more than that is not part of the hook: an in-place patch.
static void padded_hooks() {
    const uintptr_t base = 0x140000000, l_text = base + 0x1000, out = 0x7ff600000000;
    std::vector<uint8_t> file(96);
    for (size_t i = 0; i < file.size(); ++i) file[i] = static_cast<uint8_t>(0x40 + i % 16);  // no 0x90 among them
    auto hooked = [&](size_t nops) {
        std::vector<uint8_t> live = file;
        live[8] = 0x48, live[9] = 0xB8, live[18] = 0xFF, live[19] = 0xE0;  // mov rax, imm64; jmp rax
        std::memcpy(&live[10], &out, 8);
        std::fill(live.begin() + 20, live.begin() + 20 + nops, 0x90);
        return pe::only_hook_differences(file.data(), live.data(), file.size(), l_text, base, 0x10000);
    };
    CHECK(hooked(0) && hooked(6) && hooked(13), "a 12-byte jump with up to 13 NOPs is a hook");
    CHECK(hooked(20), "a 12-byte jump with 20 NOPs (Greeneye Loader's longest hook) is a hook");
    CHECK(!hooked(21), "21 NOPs behind a jump are more than any hook writes: the last one is an in-place patch");
}

int main(int argc, char** argv) {
    synthetic();
    padded_hooks();
    hostile_pe();
    std::filesystem::path sigdb_dir = std::filesystem::path(__FILE__).parent_path() / "../../../sigdb/symbols";
    for (int i = 1; i + 1 < argc; i += 2) {
        const std::string opt = argv[i];
        if (opt == "--server") real_exe(argv[i + 1], "server", sigdb_dir);
        else if (opt == "--client") real_exe(argv[i + 1], "client", sigdb_dir);
        else if (opt == "--sigdb") sigdb_dir = argv[i + 1];
    }
    std::printf(g_fail ? "%d FAILED\n" : "all passed\n", g_fail);
    return g_fail ? 1 : 0;
}
