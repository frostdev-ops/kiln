// SPDX-License-Identifier: MIT
// Exercise the actual resolver, view selection, hole API, scopes and prescan pool on the host.
#ifndef _WIN32
#define __try try
#define __except(x) catch (...)
#endif
#define main sigdisk_helpers_main
#include "sigdisk_test.cpp"
#undef main
#include "../src/sigdb.cpp"
#include "../src/hook_guard.h"
#include <limits>

namespace kiln {
ImageInfo test_image;
bool test_log_throw = false;
const ImageInfo& image() { return test_image; }
double now_ms() { return 0; }
std::string to_utf8(std::wstring_view w) { return std::string(w.begin(), w.end()); }
namespace log {
void writef(int, const char*, const char*, ...) {
    if (test_log_throw) { test_log_throw = false; throw std::runtime_error("log allocation failed"); }
}
}
}

struct Loaded {
    TestPe pe;
    std::vector<uint8_t> live;
    pe::FileText ft;
    Loaded(bool tail = false) : live(0x3040, 0) {
        if (tail) put<uint32_t>(pe.file, 0x148 + 8, TestPe::kTextSize + 32);
        std::string err;
        if (!pe::parse_file_text(pe.file.data(), pe.file.size(), ft, err)) std::abort();
        std::vector<uint8_t> text(ft.text_vsize, 0);
        std::memcpy(text.data(), pe.file.data() + ft.text_raw, ft.file_bytes());
        pe::relocate_text(text.data(), text.size(), ft, reinterpret_cast<uintptr_t>(live.data()));
        std::memcpy(live.data() + ft.text_rva, text.data(), text.size());
        auto& img = kiln::test_image;
        img = {};
        img.base = reinterpret_cast<uintptr_t>(live.data());
        img.size = ft.size_of_image;
        img.text = live.data() + ft.text_rva;
        img.text_size = ft.text_vsize;
        img.timestamp = ft.timestamp;
        img.file = pe.file.data();
        img.file_size = pe.file.size();
        sigdb::g_code = {};
    }
};

static void views_and_holes() {
    test_query = true;
    test_mbi = {reinterpret_cast<void*>(UINTPTR_MAX - 4095), 4095, MEM_COMMIT, 4};
    CHECK(!sigdb::readable(UINTPTR_MAX - 4, 14), "runtime: diagnostic read size cannot wrap around address space");
    test_query = false;
    {
        Loaded x;
        kiln::test_log_throw = true;
        bool threw = false;
        try { sigdb::code_from_disk(); } catch (const std::runtime_error&) { threw = true; }
        CHECK(threw, "runtime: exercise call_once retry after a log allocation failure");
        CHECK(sigdb::code_from_disk(), "runtime: matching executable uses disk code");
        sigdb::Pattern p;
        std::string err;
        sigdb::parse_pattern("48 {n:d32}", p, err);
        int64_t n = 123;
        const auto& cv = sigdb::code();
        CHECK(!sigdb::hole_value_at(p, cv.live + cv.size - 1, "n", &n) && n == 123,
              "runtime: a section-end hole cannot fall through to live bytes");
        CHECK(!sigdb::hole_value_at(p, cv.live + cv.size, "n", &n),
              "runtime: a hole outside .text cannot fall through to live bytes");
        CHECK(!sigdb::hole_value_at(p, cv.live - 1, "n", &n), "runtime: hole before .text rejected");
        CHECK(cv.scope_end(cv.scan + 1, std::numeric_limits<size_t>::max()) == cv.end(),
              "runtime: overflowing scope size clamps within the view");
        CHECK(sigdb::scan(p, cv.scan, cv.end(), 0).empty(), "runtime: zero scan limit returns no hits");
    }
    {
        Loaded x;
        auto* at = x.live.data() + TestPe::kFunc;
        at[0] = 0xE9;
        const int32_t rel = 0x7FFF0000;
        std::memcpy(at + 1, &rel, 4);
        sigdb::init_code();
        CHECK(sigdb::g_code.view.disk, "runtime: a five-byte foreign hook stays on disk scan at the 1%% boundary");
        sigdb::Pattern p;
        std::string err;
        sigdb::parse_pattern("48 B8 {low:d32}", p, err);
        int64_t low = 0;
        CHECK(sigdb::hole_value_at(p, reinterpret_cast<uintptr_t>(at), "low", &low) &&
              low == static_cast<uint32_t>(kiln::test_image.base + 0x1100),
              "runtime: hole bytes under a foreign hook still come from the relocated disk view");
    }
    {
        Loaded x;
        // One byte differs, far below 1%, and layout/metadata are unchanged: this is not a hook.
        x.live[0x1050] ^= 1;
        sigdb::init_code();
        CHECK(!sigdb::g_code.view.disk, "runtime: sparse non-hook exe difference selects live scan");
    }
    {
        Loaded x;
        ++kiln::test_image.timestamp;
        sigdb::init_code();
        CHECK(!sigdb::g_code.view.disk, "runtime: same layout with a different build timestamp selects live scan");
    }
    {
        Loaded x;
        ++kiln::test_image.size;
        sigdb::init_code();
        CHECK(!sigdb::g_code.view.disk, "runtime: mismatched SizeOfImage selects live scan");
    }
    {
        Loaded x(true);
        sigdb::init_code();
        CHECK(sigdb::g_code.view.disk && sigdb::g_code.view.size == x.ft.text_vsize,
              "runtime: the file view includes the loader's zero-filled .text tail");
        CHECK(sigdb::g_code.live_diff_bytes == 0, "runtime: relocation spanning raw data and zero fill matches loader");
        sigdb::Pattern p;
        std::string err;
        sigdb::parse_pattern("?? ?? ?? ?? 00 {tail:d32}", p, err);
        int64_t value = -1;
        CHECK(sigdb::hole_value_at(p, sigdb::g_code.view.live + x.ft.text_rawsize - 4, "tail", &value) && value == 0,
              "runtime: hole spanning raw data and the zero-filled tail uses the full view");
    }
    {
        Loaded x;
        std::memset(x.live.data() + x.ft.text_rva, 0, x.ft.text_vsize);
        sigdb::init_code();
        CHECK(!sigdb::g_code.view.disk, "runtime: greater than 1%% differences select live scan");
    }
    {
        Loaded x;
        kiln::test_image.file = nullptr;
        sigdb::init_code();
        CHECK(!sigdb::g_code.view.disk, "runtime: missing mapping selects live scan");
    }
}

static void pool_and_resolver(const char* dir) {
    Loaded x;
    sigdb::init_code();
    const auto& cv = sigdb::g_code.view;
    sigdb::Pattern p;
    std::string err;
    sigdb::parse_pattern("48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}", p, err);
    std::vector<const sigdb::Pattern*> patterns(96, &p);
    std::vector<std::vector<const uint8_t*>> hits;
    std::vector<uint8_t> done;
    sigdb::scan_many(patterns, cv.scan, cv.end(), 2, hits, done);
    const auto expected = sigdb::scan(p, cv.scan, cv.end(), 2);
    CHECK(sigdb::scan_code(p, size_t{UINT32_MAX} + 1).size() == expected.size(),
          "runtime: maximum hooks-API capacity plus its sentinel is a wide scan limit");
    CHECK(expected.size() == 1 && std::all_of(done.begin(), done.end(), [](auto d) { return d == 1; }) &&
          std::all_of(hits.begin(), hits.end(), [&](const auto& h) { return h == expected; }),
          "runtime: pool joins all 96 scans with the sequential result");
    const auto file = std::filesystem::path(dir) / "resolver.json";
    {
        std::ofstream out(file);
        out << R"({"format":1,"symbols":{
          "root":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}"}]},
          "scoped":{"find":[{"pattern":"8B 81 {field:d32} C3","scope":{"symbol":"root","size":18446744073709551615}}]},
          "backward":{"find":[{"pattern":"E8 {negative:rel32} 8B 81","scope":{"symbol":"root","size":64}}]}
        }})";
    }
    sigdb::Resolver r;
    CHECK(r.load(file, ""), "runtime: scoped definitions load");
    r.resolve();
    uintptr_t a = 0;
    int64_t field = 0;
    CHECK(r.symbol("root", &a) && a == kiln::test_image.base + TestPe::kFunc,
          "runtime: unscoped disk match translates to live address");
    CHECK(r.symbol("helper", &a) && a == kiln::test_image.base + TestPe::kHelper,
          "runtime: rel32-captured scope symbol translates to live address");
    CHECK(r.symbol("scoped", &a) && a == kiln::test_image.base + TestPe::kFunc + 15 &&
          r.constant("field", &field) && field == 0x1234, "runtime: huge scope remains bounded and captures disk bytes");
    std::filesystem::remove(file);
}

// kiln.hooks v2's accessors: the exe file's bytes at a live address, the symbol name at an address, and the symbols whose
// live bytes differed from the file's when they were resolved (with the 16 live bytes found then).
static void live_diff_accessors(const char* dir) {
    Loaded x;
    uint8_t* fn = x.live.data() + TestPe::kFunc;
    fn[0] = 0xE9;  // another engine's jmp over the function's first bytes, to far outside the image
    const int32_t rel = 0x10000000;
    std::memcpy(fn + 1, &rel, 4);
    test_query = true;
    test_mbi = {x.live.data(), x.live.size(), MEM_COMMIT, 4};
    sigdb::init_code();
    const auto file = std::filesystem::path(dir) / "livediff.json";
    {
        std::ofstream out(file);
        out << R"({"format":1,"symbols":{"root":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}"}]}}})";
    }
    sigdb::Resolver r;
    r.load(file, "");
    r.resolve();
    uintptr_t a = 0;
    CHECK(r.symbol("root", &a) && a == kiln::test_image.base + TestPe::kFunc, "live diffs: the hooked symbol still resolves on the file");
    std::vector<sigdb::LiveDiff> d;
    r.collect_live_diffs(d);
    CHECK(d.size() == 1 && d[0].symbol == "root" && d[0].address == a && d[0].at == a && d[0].bytes_ok && d[0].bytes[0] == 0xE9 &&
              d[0].by.rfind("jmp to", 0) == 0,
          "live diffs: the symbol is listed with the live bytes seen at resolve time and its jump");
    CHECK(r.name_at(a) == "root" && r.name_at(a + 1).empty(), "live diffs: name_at finds the symbol at an address");
    uint8_t got[5] = {};
    CHECK(sigdb::file_bytes(a, got, sizeof got) && got[0] == 0x48 && got[1] == 0xB8 && fn[0] == 0xE9,
          "live diffs: file_bytes returns the exe file's bytes, not the hook");
    CHECK(!sigdb::file_bytes(kiln::test_image.base + 1, got, sizeof got) &&
              !sigdb::file_bytes(a + x.ft.text_vsize, got, sizeof got),
          "live diffs: file_bytes refuses addresses outside .text");
    test_query = false;
    std::filesystem::remove(file);
}

static void hook_guard() {
    struct Hook { bool on = true; int restores = 0; bool enabled() { return on; }
                  bool disable() { ++restores; on = false; return true; } } h;
    std::array<uint8_t, 32> bytes{};
    bytes[0] = 0xE9;
    kiln::hooks::HookGuard guard;
    guard.armed(bytes.data());
    int logs = 0;
    CHECK(guard.disable(h, [&] { ++logs; }) && !h.on && h.restores == 1 && !logs,
          "guard: owned jump disables normally");
    h.on = true;
    guard.armed(bytes.data());
    ++bytes[1];  // another E9 destination: opcode-only checks would miss it
    CHECK(!guard.disable(h, [&] { ++logs; }) && h.on && h.restores == 1 && logs == 1,
          "guard: foreign jump stays armed without restoring bytes");
    CHECK(!guard.disable(h, [&] { ++logs; }) && logs == 1, "guard: foreign jump is logged once");
    bytes[0] = 0xFF; bytes[1] = 0x25;
    CHECK(!guard.disable(h, [&] { ++logs; }) && h.on && logs == 1, "guard: far foreign jump stays armed");
}

static void diff_budget() {
    std::array<uint8_t, 1000> file{}, live{};
    file.fill(0xCC);
    live = file;
    const uintptr_t base = 0x100000, text = base + 0x1000;
    auto hook = [&](size_t off) {
        live[off] = 0xE9;
        int32_t d = 0x55555555;
        std::memcpy(live.data() + off + 1, &d, 4);
    };
    hook(20); hook(995);  // exactly 1%, including a hook in the final five bytes
    size_t n = 0;
    pe::diff_runs(file.data(), live.data(), live.size(), 0, &n);
    CHECK(n == live.size() / 100 && pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: exactly 1%% hook differences, including section-end hook, remain eligible for disk scan");
    live[200] ^= 1;
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: an unrelated code byte is not hidden by recognised hooks");
    sigdb::Pattern p;
    std::string err;
    sigdb::parse_pattern("E8 {back:rel32}", p, err);
    std::array<uint8_t, 5> call{0xE8};
    const int32_t disp = -12;
    std::memcpy(call.data() + 1, &disp, 4);
    int64_t target = 0;
    CHECK(sigdb::hole_value_in(p, {call.data(), call.size(), text, true}, text, "back", &target) &&
          static_cast<uintptr_t>(target) == text - 7, "runtime: negative rel32 at the section edge uses live address");
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    views_and_holes();
    pool_and_resolver(argv[1]);
    live_diff_accessors(argv[1]);
    hook_guard();
    diff_budget();
    return g_fail ? 1 : 0;
}
