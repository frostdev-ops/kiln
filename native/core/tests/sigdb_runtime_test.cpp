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
    // A symbol with a pattern of its own that a capture elsewhere also names: when the two name different addresses,
    // the symbol is not resolved, and stays so through every later pass (the capture is not taken after all).
    {
        std::ofstream out(file);
        out << R"({"format":1,"symbols":{
          "root":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}"}]},
          "helper":{"find":[{"pattern":"8B 81 34 12 00 00 C3"}]},
          "later":{"find":[{"pattern":"E8 {negative:rel32} 8B 81","scope":{"symbol":"root","size":64}}]}
        }})";
    }
    sigdb::Resolver bad;
    CHECK(bad.load(file, ""), "runtime: a cross-checked definition loads");
    bad.resolve();
    CHECK(bad.symbol("root", &a) && !bad.symbol("helper", &a),
          "runtime: a symbol whose pattern and capture disagree is not resolved, on any pass");
    {
        std::ofstream out(file);
        out << R"({"format":1,"symbols":{
          "root":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}"}]},
          "helper":{"find":[{"pattern":"8B 81 34 12 00 00 C3 FF FF FF FF FF"}]}
        }})";
    }
    sigdb::Resolver fallback;
    CHECK(fallback.load(file, ""), "runtime: a definition with a failing pattern loads");
    fallback.resolve();
    CHECK(fallback.symbol("helper", &a) && a == kiln::test_image.base + TestPe::kHelper,
          "runtime: a symbol whose own pattern matches nothing is still taken from an agreeing capture");
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

// Descriptors of typed links (dev.28): `sig` in a symbol's entry or under "sigs", checked against the function's code
// as the exe file has it; "sig/<symbol>" is a feature. The test image's code: root = mov rax, imm64; call helper;
// then "scoped" = mov eax, [rcx+0x1234]; ret.
static void sig_blocks(const char* dir) {
    Loaded x;
    uint8_t* fn = x.live.data() + TestPe::kFunc + 15;  // "scoped", hooked in the live image by another engine
    fn[0] = 0xE9;
    const int32_t rel = 0x10000000;
    std::memcpy(fn + 1, &rel, 4);
    test_query = true;
    test_mbi = {x.live.data(), x.live.size(), MEM_COMMIT, 4};
    sigdb::init_code();
    const auto file = std::filesystem::path(dir) / "sigs.json";
    const char* symbols = R"("symbols":{
          "root":{"find":[{"pattern":"48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}"}],
                  "sig":{"ret":"u32","args":[{"name":"self","type":"keen::Thing*"}],"invoke":"tick"}},
          "helper":{"sig":{"args":[]}},
          "scoped":{"find":[{"pattern":"8B 81 {field:d32} C3","scope":{"symbol":"root","size":64}}]}
        })";
    auto write = [&](const std::string& rest) {
        std::ofstream out(file);
        out << "{\"format\":1," << symbols << rest << "}";
    };
    write(R"(,"structs":{"Pair":{"size":16,"align":8,"fields":[{"name":"a","type":"u64","offset":0},{"name":"b","type":"u64","offset":8}]}},
          "sigs":{"scoped":{"ret":"u32","args":[{"name":"self","type":"ptr"}],"abi":{"reads":["rcx"],"stack":[],"ret":"rax"}},
                  "missing":{"sret":"Pair","args":[{"name":"x","type":"u8"}]}})");
    sigdb::Resolver r;
    CHECK(r.load(file, ""), "sig: a file with sig blocks, a sigs section and structs loads");
    r.resolve();
    uintptr_t a = 0;
    sigdb::SigInfo info;
    CHECK(r.symbol("scoped", &a) && r.sig("scoped", &info) && info.status == sigdb::SigStatus::Match && info.address == a &&
              info.fingerprint.find(R"("reads":["rcx"])") != std::string::npos && fn[0] == 0xE9,
          "sig: a descriptor under \"sigs\" matches its function, read from the file's bytes although the live entry is hooked");
    CHECK(r.feature("sig/scoped") && !r.feature_problem("sig/scoped", nullptr, nullptr, nullptr),
          "sig: sig/<symbol> is a feature that is on");
    CHECK(r.sig("root", &info) && info.status == sigdb::SigStatus::Match && r.feature("sig/root"),
          "sig: a descriptor in the symbol's own entry (the function passes rcx on to its callee)");
    CHECK(r.symbol("helper", &a) && r.sig("helper", &info) && info.status != sigdb::SigStatus::Unresolved && info.address == a,
          "sig: an entry that holds only a descriptor leaves the name to its capture");
    std::string name, text;
    bool constant = true;
    CHECK(r.sig("missing", &info) && info.status == sigdb::SigStatus::Unresolved && !r.feature("sig/missing") &&
              r.feature_problem("sig/missing", &name, &text, &constant) && name == "missing" && text == "not defined" && !constant,
          "sig: a described symbol that does not resolve is off, and says why");
    const auto names = r.feature_names();
    const auto failed = r.failed_features();
    CHECK(std::count(names.begin(), names.end(), "sig/scoped") == 1 && std::count(names.begin(), names.end(), "sig/missing") == 1 &&
              std::count(failed.begin(), failed.end(), "sig/missing") == 1 && std::count(failed.begin(), failed.end(), "sig/scoped") == 0,
          "sig: the features are listed");
    CHECK(r.sig_at(kiln::test_image.base + TestPe::kFunc + 15, &info) && info.symbol == "scoped" && !r.sig_at(1, &info),
          "sig: sig_at finds the described symbol at an address");
    CHECK(!r.sig("nothing", &info) && !r.feature("sig/nothing"), "sig: a symbol without a descriptor has no feature");

    // the function does not fit the descriptor: the feature is off, the symbol still resolves
    write(R"(,"sigs":{"scoped":{"args":[{"name":"scale","type":"f32"}]}})");
    sigdb::Resolver wrong;
    CHECK(wrong.load(file, ""), "sig: a wrong descriptor loads");
    wrong.resolve();
    CHECK(wrong.symbol("scoped", &a) && wrong.sig("scoped", &info) && info.status == sigdb::SigStatus::Mismatch &&
              !wrong.feature("sig/scoped") && wrong.feature_problem("sig/scoped", &name, &text, &constant) &&
              text == "descriptor arg 1 'scale' is xmm0, the function reads [rcx]",
          "sig: a float where the function reads rcx is a mismatch naming the argument; the symbol still resolves");
    write(R"(,"sigs":{"scoped":{"args":[{"name":"self","type":"ptr"}],"abi":{"reads":["rcx","rdx"]}}})");
    sigdb::Resolver stale;
    stale.load(file, "");
    stale.resolve();
    CHECK(stale.sig("scoped", &info) && info.status == sigdb::SigStatus::Mismatch && info.text.find("review the descriptor") != std::string::npos,
          "sig: a stored fingerprint that the code no longer has is a mismatch");
    // malformed descriptors
    write(R"(,"sigs":{"scoped":{"args":[{"name":"v","type":"Vec9"}]}})");
    sigdb::Resolver bad;
    CHECK(bad.load(file, ""), "sig: a descriptor with an unknown type still loads the file");
    bad.resolve();
    CHECK(bad.symbol("scoped", &a) && bad.sig("scoped", &info) && info.status == sigdb::SigStatus::Bad && !bad.feature("sig/scoped") &&
              bad.feature_problem("sig/scoped", &name, &text, &constant) && text.find("Vec9") != std::string::npos,
          "sig: ...and is off with the reason");
    write(R"(,"sigs":{"scoped":{"args":[{"name":"self","type":"ptr"}],"abi":"trust"}})");
    sigdb::Resolver core_trust;
    core_trust.load(file, "");
    core_trust.resolve();
    CHECK(core_trust.sig("scoped", &info) && info.status == sigdb::SigStatus::Bad, "sig: a core descriptor may not say trust");
    write(R"(,"sigs":{"root":{"args":[]}})");
    sigdb::Resolver twice;
    CHECK(!twice.load(file, "") && twice.load_error.find("two sig blocks") != std::string::npos, "sig: two descriptors for one symbol are refused");
    write(R"(,"sigs":{"@probe":{"args":[]}})");
    sigdb::Resolver probe;
    CHECK(!probe.load(file, ""), "sig: a probe cannot carry a descriptor");
    write(R"(,"structs":{"Pair":{"size":0}})");
    sigdb::Resolver bad_struct;
    CHECK(!bad_struct.load(file, ""), "sig: a malformed struct is refused at load");
    // a fragment: names carry its prefix, trust is allowed, the core's structs are visible
    {
        std::ofstream out(file);
        out << R"({"format":1,"symbols":{"mod/scoped":{"find":[{"pattern":"8B 81 34 12 00 00 C3"}],
                 "sig":{"sret":"Pair","args":[],"abi":"trust"}}},
                 "sigs":{"other/x":{"args":[]}}})";
    }
    sigdb::Resolver frag(&r);
    CHECK(!frag.load(file, "mod/") && frag.load_error.find("other/x") != std::string::npos,
          "sig: a fragment's sigs section may only describe its own names");
    {
        std::ofstream out(file);
        out << R"({"format":1,"symbols":{"mod/scoped":{"find":[{"pattern":"8B 81 34 12 00 00 C3"}],
                 "sig":{"sret":"Pair","args":[],"abi":"trust"}}}})";
    }
    sigdb::Resolver frag2(&r);
    CHECK(frag2.load(file, "mod/"), "sig: a fragment with a trusted descriptor loads");
    frag2.resolve();
    CHECK(frag2.sig("mod/scoped", &info) && info.status == sigdb::SigStatus::Match && info.json.find("Pair") != std::string::npos,
          "sig: a fragment's descriptor uses a struct the core declared; its sret buffer is the rcx the function reads");
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
    live[200] ^= 1;
    // A redirected call (ShroudForge's native runtime and the main menu's two Settings calls): the opcode stays, the
    // operand goes from a place in the image to one outside it.
    auto call = [&](std::array<uint8_t, 1000>& code, size_t off, uintptr_t to) {
        code[off] = 0xE8;
        const int32_t d = static_cast<int32_t>(static_cast<int64_t>(to) - static_cast<int64_t>(text + off + 5));
        std::memcpy(code.data() + off + 1, &d, 4);
    };
    const uintptr_t relay = base + 0x40000, in_image = text + 0x300;
    call(file, 300, in_image); call(live, 300, relay);
    call(file, 400, in_image); call(live, 400, relay);
    CHECK(pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: calls sent from the image to outside it are another engine's, the disk scan stays");
    static uintptr_t asked;
    asked = 0;
    CHECK(pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000,
                                    [](uintptr_t to) { asked = to; return true; }) && asked == relay,
          "runtime: the redirected call's target is the one asked about");
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000, [](uintptr_t) { return false; }),
          "runtime: a call redirected to a place that cannot be called is not accepted");
    call(live, 400, text + 0x380);
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: a call moved to another place inside the image is not a hook");
    call(live, 400, relay);
    file[400] = 0xCC; live[400] = 0xE8;
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: a call written over other bytes is not a redirected call");
    file[400] = 0xE8;
    call(file, 400, base + 0x50000);
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: bytes after an E8 that were no call into the image are not a redirected call");
    call(file, 400, in_image);
    live[399] ^= 1;
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: a changed byte before a redirected call is not covered by it");
    live[399] ^= 1;
    live[405] ^= 1;
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: a changed byte after a redirected call is not covered by it");
    live[405] ^= 1;
    call(file, 995, in_image); call(live, 995, relay);
    CHECK(pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: a redirected call in the final five bytes is read to the end and no further");
    // A call in the file that went to the image but not to its code (data behind the section) is no call to judge by.
    call(file, 400, base + 0x1800); call(live, 400, relay);
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000),
          "runtime: bytes after an E8 that pointed into the image but not into its code are not a redirected call");
    call(file, 400, in_image);
    // An E8 inside another instruction's immediate (review of 6a78171d): mov rax, imm64 with other imm64 bytes. Its four
    // bytes read as a call from the code to outside the image; what stands at that place decides.
    const std::array<uint8_t, 10> mov_was{0x48, 0xB8, 0xE8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    const std::array<uint8_t, 10> mov_now{0x48, 0xB8, 0xE8, 0xF9, 0xEF, 0x03, 0x00, 0x00, 0x00, 0x00};
    std::memcpy(file.data() + 500, mov_was.data(), 10);
    std::memcpy(live.data() + 500, mov_now.data(), 10);
    CHECK(!pe::only_hook_differences(file.data(), live.data(), live.size(), text, base, 0x2000,
                                     [](uintptr_t to) { return to == 0x100000 + 0x40000; }),
          "runtime: an E8 in an immediate whose bytes point at no relay and no module is not a redirected call");
    std::memcpy(live.data() + 500, mov_was.data(), 10);
    const std::array<uint8_t, 14> abs_jump{0xFF, 0x25, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8};
    const std::array<uint8_t, 14> near_jump{0xE9, 1, 2, 3, 4};
    const std::array<uint8_t, 14> mov_jump{0x48, 0xB8, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xE0};
    const std::array<uint8_t, 14> plain{0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20};
    const std::array<uint8_t, 14> rip_jump{0xFF, 0x25, 0x10, 0, 0, 0};
    CHECK(pe::relay_shape(abs_jump.data()) && pe::relay_shape(near_jump.data()) && pe::relay_shape(mov_jump.data()) &&
              !pe::relay_shape(plain.data()) && !pe::relay_shape(rip_jump.data()),
          "runtime: a relay is an absolute, a near or a register jump, nothing else");
    // The call's opcode among bytes that hold another E8 (review of 6a78171d): E8 E8 00 00 00 -> E8 E8 00 03 00. The
    // first differing byte is the call's fourth; the E8 at its second byte is an operand byte, not the call.
    std::array<uint8_t, 64> f2{}, l2{};
    f2.fill(0xCC);
    const std::array<uint8_t, 7> c_was{0xE8, 0xE8, 0x00, 0x00, 0x00, 0x84, 0xC0}, c_now{0xE8, 0xE8, 0x00, 0x03, 0x00, 0x84, 0xC0};
    std::memcpy(f2.data() + 8, c_was.data(), 7);
    l2 = f2;
    std::memcpy(l2.data() + 8, c_now.data(), 7);
    CHECK(pe::redirected_call_at(f2.data(), l2.data(), 0x400, 8, text, base, 0x2000) &&
              !pe::redirected_call_at(f2.data(), l2.data(), 0x400, 9, text, base, 0x2000) &&
              !pe::redirected_call_at(f2.data(), l2.data(), 0x400, 10, text, base, 0x2000),
          "runtime: the redirected call is the E8 whose targets read as one, not an E8 among its operand bytes");
    CHECK(!pe::redirected_call_at(f2.data(), f2.data(), 0x400, 8, text, base, 0x2000) &&
              !pe::redirected_call_at(f2.data(), l2.data(), 12, 8, text, base, 0x2000) &&
              !pe::redirected_call_at(f2.data(), l2.data(), 64, 64, text, base, 0x2000),
          "runtime: an unchanged call, a call cut off by the end and a place past the end are none");
    sigdb::Pattern p;
    std::string err;
    sigdb::parse_pattern("E8 {back:rel32}", p, err);
    std::array<uint8_t, 5> call5{0xE8};
    const int32_t disp = -12;
    std::memcpy(call5.data() + 1, &disp, 4);
    int64_t target = 0;
    CHECK(sigdb::hole_value_in(p, {call5.data(), call5.size(), text, true}, text, "back", &target) &&
          static_cast<uintptr_t>(target) == text - 7, "runtime: negative rel32 at the section edge uses live address");
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    views_and_holes();
    pool_and_resolver(argv[1]);
    live_diff_accessors(argv[1]);
    sig_blocks(argv[1]);
    hook_guard();
    diff_budget();
    return g_fail ? 1 : 0;
}
