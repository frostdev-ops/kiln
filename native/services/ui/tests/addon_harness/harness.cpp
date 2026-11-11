// SPDX-License-Identifier: MIT
// ui-addon-harness.exe: shroudlink's real client addon (dinput8.dll, 0.8.0) with Kiln's production window procedure
// (input.cpp, wndproc_chain.h) and startup gate (core/src/foreign_scan.cpp), under Wine without a display.
// Run by native/services/ui/tests/test-addon-wine.sh; the addon DLL is not part of Kiln and comes from SHROUDLINK_ADDON.
//
// The addon looks for 30 game functions by their bytes in the main exe's .text (client/src/link.rs, native.rs). This
// exe carries source-backed stand-ins: matching patterns, layout checks, call targets and RIP-relative globals.
// The harness calls the synthetic per-frame UI function, running the addon's actual UI dispatch and input code.
// A message-only HWND and two window-discovery API adapters let the unchanged addon subclass real user32 procedures
// without any desktop window. Synthetic tab lists also exercise the addon's actual MODS-tab emission predicates.
// This tests initialization/input/widget calls, not real-game rendering, layout, controller behavior or a server connection.
//
//   ui-addon-harness <addon.dll> race         hook item lookup first: the addon disables its native UI
//   ui-addon-harness <addon.dll> wait         production startup gate first: native UI installation succeeds
//   ui-addon-harness <addon.dll> window       Kiln subclasses first; periodic checks, keys, shutdown and hot reload
//   ui-addon-harness <addon.dll> window-late  addon subclasses first, Kiln follows, then addon leaves
//   ui-addon-harness <addon.dll> tabs         actual tab hooks: count/action gates, Kiln subclasses first
//   ui-addon-harness <addon.dll> tabs-late    the same visibility gates, addon subclasses first
//   ui-addon-harness <inert.dll> timeout      stale/partial log and unrelated hook cannot release the gate
//   ui-addon-harness <forwarded.dll> unrelated  matching forwarded export is not an addon
// Lines: "PASS ...", "FAIL ...", "KILN: <kiln.ui log line>". Exit code 0 = no FAIL.
#include <windows.h>
#include <nlohmann/json.hpp>
#include <map>

#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <string>
#include <vector>

#include "foreign_scan.h"
#include "common.h"
#include "log.h"
#include "sigdb.h"
#include "safetyhook.hpp"
#include "ui.h"
#include "wndproc_chain.h"

// ---- what input.cpp needs from ui.cpp ----
namespace kui {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
std::atomic<uint32_t> g_demand{0}, g_interactive{0}, g_render_demand{0}, g_hover_panels{0};
std::atomic<bool> g_menu_open{false}, g_text_input{false}, g_want_mouse{false}, g_wheel_active{false}, g_page_capture{false};
std::vector<std::string> g_log;
void logf(int, const char* fmt, ...) {
    char b[1024];
    va_list a;
    va_start(a, fmt);
    std::vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    std::printf("KILN: %s\n", b);
    g_log.push_back(b);
}
uint64_t now_ms() { return GetTickCount64(); }
bool dispatch_key(const KilnUiKey&) { return false; }  // no Kiln key bindings here
}  // namespace kui

// Only paths/clock/logging and the synthetic image backing sigdb are adapters. Production foreign_scan.cpp and
// input.cpp are linked unchanged; the addon is the supplied unmodified DLL. Use sigdb's real pattern matcher.
namespace kiln {
const Paths& paths() {
    static const Paths p = [] {
        Paths out;
        wchar_t exe[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        out.game = std::filesystem::path(exe).parent_path();
        return out;
    }();
    return p;
}
double now_ms() { return static_cast<double>(GetTickCount64()); }
std::string to_utf8(std::wstring_view w) {
    std::string out(w.size() * 4, '\0');
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), out.data(), static_cast<int>(out.size()), nullptr, nullptr);
    out.resize(n);
    return out;
}
namespace log {
void writef(int, const char*, const char* fmt, ...) {
    va_list a;
    va_start(a, fmt);
    std::vprintf(fmt, a);
    va_end(a);
    std::puts("");
}
}
namespace sigdb {
uint8_t original[0x4000]{};
const uint8_t* live = nullptr;
std::vector<uintptr_t> scan_code(const Pattern& p, size_t limit) {
    std::vector<uintptr_t> out;
    for (const uint8_t* h : scan(p, original, original + sizeof original, limit))
        out.push_back(reinterpret_cast<uintptr_t>(live) + (h - original));
    return out;
}
}
}

namespace {

int g_fails = 0, g_checks = 0;
void check(bool ok, const char* what, ...) {
    char b[1024];
    va_list a;
    va_start(a, what);
    std::vsnprintf(b, sizeof b, what, a);
    va_end(a);
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", b);
    ++g_checks;
    if (!ok) ++g_fails;
}

// ---- the stand-in game code, inside this exe's .text ----
constexpr size_t kBlobSize = 0x4000;
// Actual code, not allocate() data: COFF otherwise emits a second .text with different characteristics, invisible
// to the addon's first-.text scan. The fixture is overwritten before any addon code runs.
__declspec(noinline) void blob_storage() { __asm__ volatile(".fill 16384, 1, 0xcc"); }

// what the addon's globals point at: GAME_UI -> ui object (+0xc8 theme, +0xd0 game state holder) -> holder -> state
// (+0x1cd8 the active screen; 0x01 = playing)
uintptr_t g_builder_slot = 0, g_game_ui_slot = 0, g_tip_slot = 0;
alignas(16) uint8_t g_ui[0x200];
alignas(16) uint8_t g_holder_obj[0x100];
uintptr_t g_holder_ptr = 0;
alignas(16) uint8_t g_state[0x2000];

struct Fake {
    uint8_t* base = nullptr;
    size_t at = 0;
    uint8_t* here() { return base + at; }
    void align() {
        at = (at + 15) & ~size_t{15};
    }
    void put(std::initializer_list<int> v) {
        for (int c : v) base[at++] = static_cast<uint8_t>(c);
    }
    void rel32(const void* target) {  // the displacement of an instruction that ends right after these 4 bytes
        const int32_t d = static_cast<int32_t>(reinterpret_cast<intptr_t>(target) - reinterpret_cast<intptr_t>(here() + 4));
        std::memcpy(here(), &d, 4);
        at += 4;
    }
    void call(const void* target) {
        put({0xE8});
        rel32(target);
    }
    void jump(const void* target) {  // tail call a C++ stand-in after undoing the source-backed prologue
        put({0x48, 0xB8});
        const uintptr_t addr = reinterpret_cast<uintptr_t>(target);
        std::memcpy(here(), &addr, sizeof addr);
        at += sizeof addr;
        put({0xFF, 0xE0});
    }
    // the addon's pattern: "??" takes the next fill (a byte, or 'R' + index: a rel32 to targets[index])
    struct Fill {
        int byte = 0;
        const void* rel = nullptr;
    };
    uint8_t* func(const char* pattern, std::vector<Fill> fills = {}) {
        align();
        uint8_t* start = here();
        size_t f = 0;
        for (const char* p = pattern; *p;) {
            while (*p == ' ') ++p;
            if (!*p) break;
            if (p[0] == '?') {
                const Fill fill = f < fills.size() ? fills[f++] : Fill{};
                if (fill.rel) {  // takes this and the next three wildcards
                    rel32(fill.rel);
                    for (int k = 0; k < 3; ++k) {
                        p += 2;
                        while (*p == ' ') ++p;
                    }
                } else {
                    put({fill.byte});
                }
                p += 2;
                continue;
            }
            put({static_cast<int>(std::strtoul(std::string(p, 2).c_str(), nullptr, 16))});
            p += 2;
        }
        return start;
    }
};

struct Game {
    uint8_t *dispatch = nullptr, *item_info = nullptr, *tab_list = nullptr, *tab_widget = nullptr, *key_resolve = nullptr;
    uint8_t *update = nullptr, *store = nullptr;
} g_game;

// The layout comes from the MIT addon's TabList/TabWidget interfaces, not game data. The game's stand-ins provide
// entries and record widget calls. Recognition, template copying, MODS injection and key override run in the real DLL.
struct TabList { uint8_t* data; size_t len; };
alignas(16) uint8_t g_tabs[8][0x40]{};
size_t g_tab_count = 0;
struct SeenTab { std::string label; uint64_t key; uint8_t action, key_kind; uint16_t hid; };
std::vector<SeenTab> g_seen_tabs;
void* __fastcall fake_tab_list(uint8_t, TabList* list) {
    if (list) *list = {g_tabs[0], g_tab_count};
    return list;
}
uint64_t __fastcall fake_tab_widget(uint8_t* entry, float* rect) {
    const auto* label = *reinterpret_cast<const char**>(entry + 0x18);
    const auto len = *reinterpret_cast<const uint64_t*>(entry + 0x20);
    alignas(16) uint8_t desc[0xc4]{};
    reinterpret_cast<uint8_t* (*)(uint8_t*, size_t, uint32_t)>(g_game.key_resolve)(desc, 0, entry[1]);
    g_seen_tabs.push_back({std::string(label, static_cast<size_t>(len)), *reinterpret_cast<uint64_t*>(entry + 0x28),
                          entry[1], desc[0], *reinterpret_cast<uint16_t*>(desc + 2)});
    if (rect) { rect[0] = 0; rect[1] = 0; rect[2] = 100; rect[3] = 25; }
    return entry[0x30];
}

struct StringView { const uint8_t* data; size_t len; };
std::vector<std::string> g_packets;
uint8_t* fake_send(uint8_t*, uint8_t* out, uint32_t, const StringView* text) {
    g_packets.emplace_back(reinterpret_cast<const char*>(text->data), text->len);
    *out = 0; return out;
}
uint8_t* fake_store(uint8_t* out, void*, uint64_t, const StringView*, uint32_t) { *out = 0; return out; }

// The addon's patterns, from shroudlink 0.8.0 (client/src/link.rs and native.rs, MIT).
void build_fake_game() {
    auto* base = reinterpret_cast<uint8_t*>(&blob_storage);
    DWORD old = 0;
    VirtualProtect(base, kBlobSize, PAGE_EXECUTE_READWRITE, &old);
    Fake w{base, 0};
    w.align();
    uint8_t* ret = w.here();
    w.put({0xC3});
    w.align();
    uint8_t* zero = w.here();  // player index, fill, key lookup: return 0
    w.put({0x31, 0xC0, 0xC3});
    w.align();
    uint8_t* key_resolve = g_game.key_resolve = w.here();  // mov [rsp+8], rbx; mov rax, rcx; ret (5 whole bytes first: the addon detours it)
    w.put({0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0xC8, 0xC3});
    auto rip = [&](const void* slot, uint8_t* insn_end) {  // fill for a rip-relative disp32 ending at insn_end
        const int32_t d = static_cast<int32_t>(reinterpret_cast<intptr_t>(slot) - reinterpret_cast<intptr_t>(insn_end));
        return std::vector<Fake::Fill>{{d & 0xFF}, {(d >> 8) & 0xFF}, {(d >> 16) & 0xFF}, {(d >> 24) & 0xFF}};
    };
    using F = Fake::Fill;
    // link.rs: the client tick (read for its layout: cmp dword [rcx+disp32], 0 at +0x0d, a call within 0x30)
    w.func("48 89 5C 24 10 57 48 81 EC 90 01 00 00");
    w.put({0x83, 0xB9, 0x40, 0x01, 0x00, 0x00, 0x00});
    w.call(zero);
    w.put({0xC3});
    g_game.update = w.func("44 88 44 24 18 48 89 54 24 10 55 53 56 57");  // the client update (detoured)
    w.put({0x5F, 0x5E, 0x5B, 0x5D, 0xC3});
    w.func("48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 4C 89 74 24 20 55 48 8D AC 24 50 FF FF FF");
    w.put({0x5D});
    w.jump(reinterpret_cast<const void*>(&fake_send));
    g_game.store = w.func("48 89 5C 24 08 4C 89 44 24 18 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 20 33 ED");  // store string (detoured)
    w.put({0x48, 0x83, 0xC4, 0x20, 0x41, 0x5F, 0x41, 0x5E, 0x41, 0x5D, 0x41, 0x5C, 0x5F, 0x5E, 0x5D});
    w.jump(reinterpret_cast<const void*>(&fake_store));
    // native.rs: the game's per-frame UI build. The harness calls it as the game's frame does.
    g_game.dispatch = w.func("4C 8B DC 55 49 8D AB E8 FD FF FF 48 81 EC 10 03 00 00");
    w.put({0x48, 0x81, 0xC4, 0x10, 0x03, 0x00, 0x00, 0x5D, 0xC3});
    w.func("40 53 48 83 EC 30 0F 29 74 24 20 48 8B D9 0F 28 F2 E8 ?? ?? ?? ?? 48 8B 4B 08 48 8D 54 24 40 45 33 C0 F3 0F 11 74 24 40",
           {F{0, ret}});
    w.put({0xC3});
    w.func("48 8B C4 55 41 56 48 8D A8 B8 FE FF FF");  // the panel background: calls 10 and 17 are the rectangle fill
    for (int i = 1; i <= 17; ++i) w.call(i == 10 || i == 17 ? zero : ret);
    w.put({0xC3});
    {
        w.align();
        uint8_t* s = w.here();
        w.func("48 83 EC 28 0F 10 05 ?? ?? ?? ?? 48 C7 41 18 FF FF FF FF", rip(&g_tip_slot, s + 11));
        w.put({0x48, 0x83, 0xC4, 0x28, 0xC3});
    }
    w.func("40 55 41 54 48 8D AC 24 68 D9 FF FF");  // panel header (detoured)
    w.put({0xC3});
    w.func("48 89 74 24 18 55 57 41 56 48 8D AC 24 20 FF FF FF");  // slot grid (detoured)
    w.put({0xC3});
    w.func("48 89 5C 24 10 48 89 74 24 18 55 57 41 55 41 56 41 57 48 8D AC 24 F0 F8 FF FF");  // loca text
    w.put({0xC3});
    // the item registry lookup: called by the addon, hooked by kiln-content (content/ItemRegistryFind)
    g_game.item_info = w.func("89 4C 24 08 48 83 EC 28 85 C9 74 ?? 48 8D 4A 38 48 8D 54 24 30 E8 ?? ?? ?? ?? 48 85 C0 74 ??",
                              {F{0}, F{0, ret}, F{0}});
    w.put({0x48, 0x83, 0xC4, 0x28, 0xC3});
    w.func("48 8B C4 48 89 58 10 48 89 68 18 48 89 70 20 57 41 54 41 55 41 56 41 57 48 81 EC B0 00 00 00 0F 29 70 C8");  // slot render
    w.put({0xC3});
    w.func("48 8B C4 4C 89 40 18 48 89 48 08 55 53 56 57 41 55");  // item slot (detoured)
    w.put({0xC3});
    w.func("89 54 24 10 88 4C 24 08 55 53 56 57");  // player menu (detoured)
    w.put({0xC3});
    g_game.tab_list = w.func("48 8B C4 48 89 58 08 48 89 50 10 55 56 57 41 54 41 55 41 56 41 57 48 8D 68 A1");
    w.put({0x41, 0x5F, 0x41, 0x5E, 0x41, 0x5D, 0x41, 0x5C, 0x5F, 0x5E, 0x5D});
    w.jump(reinterpret_cast<const void*>(&fake_tab_list));
    {  // key prompt: its first call is the key lookup the addon detours
        w.align();
        uint8_t* s = w.here();
        w.func("40 53 48 81 EC 90 02 00 00 48 8B 05 ?? ?? ?? ??", rip(&g_tip_slot, s + 16));
        w.call(key_resolve);
        w.put({0xC3});
    }
    g_game.tab_widget = w.func("40 55 53 56 48 8D AC 24 60 FD FF FF");
    w.put({0x5E, 0x5B, 0x5D});
    w.jump(reinterpret_cast<const void*>(&fake_tab_widget));
    {
        w.align();
        uint8_t* s = w.here();
        w.func("48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 50 48 8B 05 ?? ?? ?? ?? 45 8B D9", rip(&g_tip_slot, s + 22));
        w.put({0xC3});
    }
    w.func("40 53 48 83 EC 20 48 89 74 24 38 48 8B D9");  // end layer
    w.put({0xC3});
    w.func("40 53 48 83 EC 20 48 8B D9 48 C7 41 10 00 00 00 00 48 C7 01 00 00 00 00");  // open root
    w.put({0xC3});
    {  // open child: its rip-relative operand is the UI builder global
        w.align();
        uint8_t* s = w.here();
        w.func("40 53 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 48 8B D9 45 33 C0", rip(&g_builder_slot, s + 13));
        w.put({0xC3});
    }
    w.func("40 53 48 83 EC 30 0F 29 74 24 20 48 8B D9 0F 28 F2 E8 ?? ?? ?? ?? 48 8B 4B 08 48 8D 54 24 40 45 33 C0 F3 0F 11 74 24 44",
           {F{0, ret}});
    w.put({0xC3});
    w.func("48 89 5C 24 18 48 89 74 24 20 57 48 83 EC 60 80 79 20 00");  // close frame
    w.put({0xC3});
    w.func("48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 41 56 48 83 EC 50 48 8B DA 41 0F B6 F9");  // text new
    w.put({0xC3});
    w.func("40 55 56 57 48 8D 6C 24 B9 48 81 EC F0 00 00 00 0F 29 BC 24 B0 00 00 00");  // text emit
    w.put({0xC3});
    {  // the tooltip state init: called once by the addon's install (writes [rcx+8])
        w.align();
        uint8_t* s = w.here();
        w.func("48 8B 05 ?? ?? ?? ?? 33 D2 48 89 41 08", rip(&g_tip_slot, s + 7));
        w.put({0xC3});
    }
    {
        w.align();
        uint8_t* s = w.here();
        w.func("48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 40 48 8B 05 ?? ?? ?? ??", rip(&g_tip_slot, s + 27));
        w.put({0xC3});
    }
    w.func("4C 8B DC 49 89 5B 10 57 48 81 EC 00 01 00 00");  // tooltip draw
    w.put({0xC3});
    {  // the QR popup: its rip-relative operand is the GAME_UI global
        w.align();
        uint8_t* s = w.func("4C 8B DC 55 57 49 8D AB 38 F8 FF FF");
        (void)s;
        w.put({0x48, 0x8B, 0x05});
        w.rel32(&g_game_ui_slot);
        w.put({0xC3});
    }
    std::memcpy(kiln::sigdb::original, base, kBlobSize);
    kiln::sigdb::live = base;
    VirtualProtect(base, kBlobSize, PAGE_EXECUTE_READ, &old);
    FlushInstructionCache(GetCurrentProcess(), base, kBlobSize);
    g_game_ui_slot = reinterpret_cast<uintptr_t>(g_ui);
    g_builder_slot = reinterpret_cast<uintptr_t>(g_ui);
    g_holder_ptr = reinterpret_cast<uintptr_t>(g_holder_obj);
    *reinterpret_cast<uintptr_t*>(g_holder_obj + 0x38) = reinterpret_cast<uintptr_t>(g_state);
    std::printf("harness: stand-in game code %zu bytes at %p\n", w.at, static_cast<void*>(base));
}

void set_screen(uint8_t screen) {
    *reinterpret_cast<uintptr_t*>(g_ui + 0xd0) = screen ? reinterpret_cast<uintptr_t>(&g_holder_ptr) : 0;
    g_state[0x1cd8] = screen;
}

// one frame of the game's UI (the addon's UI hook runs in it)
void frame() { reinterpret_cast<void (*)()>(g_game.dispatch)(); }

// Only the game-side tab list and widget are synthetic. These calls pass through both actual addon detours, including
// its native-tab early return. A fresh key set on each row prevents a previously cached last key masking a count gate.
void exercise_tabs(size_t count, uint8_t action, bool expect_mods, const char* when) {
    static uint64_t serial = 0;
    static const char* labels[] = {"Social", "World", "Journal", "Crafting", "Backpack", "Character", "Skills", "Extra"};
    ++serial;
    g_tab_count = count;
    std::memset(g_tabs, 0, sizeof g_tabs);
    for (size_t i = 0; i < count; ++i) {
        g_tabs[i][1] = action;
        *reinterpret_cast<const char**>(g_tabs[i] + 0x18) = labels[i];
        *reinterpret_cast<uint64_t*>(g_tabs[i] + 0x20) = std::strlen(labels[i]);
        *reinterpret_cast<uint64_t*>(g_tabs[i] + 0x28) = (serial << 16) + i + 1;
    }
    uint8_t original[sizeof g_tabs];
    std::memcpy(original, g_tabs, sizeof original);
    TabList list{};
    reinterpret_cast<void* (*)(uint8_t, TabList*)>(g_game.tab_list)(4, &list);
    check(list.data == g_tabs[0] && list.len == count, "%s: original tab list returned %zu entries", when, list.len);
    if (list.data != g_tabs[0] || list.len != count) return;
    g_seen_tabs.clear();
    for (size_t i = 0; i < count; ++i) {
        float rect[4]{};
        reinterpret_cast<uint64_t (*)(uint8_t*, float*)>(g_game.tab_widget)(list.data + i * 0x40, rect);
    }
    size_t mods = 0, vanilla = 0;
    bool keys_ok = true;
    for (const auto& tab : g_seen_tabs) {
        if (tab.label == "Mods") {
            ++mods;
            keys_ok &= tab.key_kind == 1 && tab.hid == 14 && tab.action == action;
        } else {
            ++vanilla;
            keys_ok &= tab.key_kind == 0;  // the addon's override was reset after its own widget call
        }
    }
    check(vanilla == count && mods == static_cast<size_t>(expect_mods),
          "%s: real addon emitted %zu Mods widget(s), expected %d; %zu original widgets", when, mods, expect_mods, vanilla);
    alignas(16) uint8_t desc[0xc4]{};
    reinterpret_cast<uint8_t* (*)(uint8_t*, size_t, uint32_t)>(g_game.key_resolve)(desc, 0, 1);
    keys_ok &= desc[0] == 0;
    if (expect_mods) keys_ok &= !g_seen_tabs.empty() && g_seen_tabs.back().label == "Mods";
    check(keys_ok, "%s: native key override matches Mods widgets, resets, and stays off game tabs", when);
    check(std::memcmp(original, g_tabs, sizeof original) == 0, "%s: original tab entries unchanged", when);
}

// ---- the addon ----
std::filesystem::path exe_dir() {
    wchar_t p[MAX_PATH];
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    return std::filesystem::path(p).parent_path();
}

std::string read_all(const std::filesystem::path& f) {
    std::string s;
    if (FILE* fp = _wfopen(f.c_str(), L"rb")) {
        char b[4096];
        size_t n;
        while ((n = std::fread(b, 1, sizeof b, fp)) > 0) s.append(b, n);
        std::fclose(fp);
    }
    return s;
}

uint64_t unix_now() { return static_cast<uint64_t>(std::time(nullptr)); }

const uint8_t g_dispatch_file[5] = {0x4C, 0x8B, 0xDC, 0x55, 0x49};  // the stand-in's first bytes, as "the file" has them

// Run the production Windows gate, including module enumeration/ownership checks, bounded reads and shared deadline.
kiln::foreign::WaitResult kiln_wait() {
    const size_t before = kiln::foreign::waits().size();
    kiln::foreign::wait_for_scanners();
    if (kiln::foreign::waits().size() == before) return {};
    const auto& w = kiln::foreign::waits().back();
    return {w.end, w.ms, w.line};
}

// The addon's last word about the copy at `mod` (waits up to `ms` for one).
kiln::foreign::AddonLog addon_says(HMODULE mod, uint64_t not_before, uint32_t ms) {
    kiln::foreign::AddonLog r;
    for (uint32_t t = 0; t <= ms; t += 50) {
        r = kiln::foreign::read_addon_log(read_all(exe_dir() / kiln::foreign::kAddonLog), reinterpret_cast<uintptr_t>(mod), not_before);
        if (r.state != kiln::foreign::AddonState::Unknown) break;
        Sleep(50);
    }
    return r;
}

bool log_has(const char* text, uint64_t not_before) {
    const std::string s = read_all(exe_dir() / kiln::foreign::kAddonLog);
    for (size_t at = s.find(text); at != std::string::npos; at = s.find(text, at + 1)) {
        const size_t open = s.rfind('[', at);
        if (open != std::string::npos && std::strtoull(s.c_str() + open + 1, nullptr, 10) >= not_before) return true;
    }
    return false;
}

HMODULE module_of(const void* p) {
    HMODULE m = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(p), &m);
    return m;
}

bool is_addon(HMODULE m) {
    return m && GetProcAddress(m, kiln::foreign::kAddonExport) && GetProcAddress(m, kiln::foreign::kAddonProxyExport);
}

// Kiln's item lookup hook (kiln-content's content/ItemRegistryFind), as Kiln writes it: an inline hook on the prologue
SafetyHookInline g_item_hook;
uintptr_t __fastcall hk_item(uint32_t id, uintptr_t registry) {
    return g_item_hook.call<uintptr_t>(id, registry);
}
void kiln_hooks_item_lookup() {
    g_item_hook = safetyhook::create_inline(g_game.item_info, reinterpret_cast<void*>(&hk_item));
    check(static_cast<bool>(g_item_hook) && g_game.item_info[0] != 0x89, "Kiln's hook is on the item lookup (first byte now 0x%02x)",
          g_game.item_info[0]);
}

// ---- the game window ----
HWND g_win = nullptr;
int g_game_keys = 0, g_game_depth = -1;
LRESULT CALLBACK game_proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (m == WM_KEYDOWN && w == 'K') {
        ++g_game_keys;
        g_game_depth = kui::chain::t_depth;  // Kiln's procedures on the stack right now
    }
    return DefWindowProcW(h, m, w, l);
}

// Wine can create a message-only HWND without a display driver. The addon's unchanged find_window requires a
// visible EnumWindows result, so adapt only those two discovery APIs to this HWND; subclassing, messages and
// shutdown still use real user32. Nothing is displayed, no SendInput reaches the desktop.
SafetyHookInline g_enum_hook, g_visible_hook;
BOOL WINAPI headless_enum(WNDENUMPROC proc, LPARAM l) { return proc(g_win, l) ? TRUE : FALSE; }
BOOL WINAPI headless_visible(HWND h) { return h == g_win ? TRUE : FALSE; }
void make_window() {
    WNDCLASSW wc{};
    wc.lpfnWndProc = game_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"HarnessGame";
    RegisterClassW(&wc);
    g_win = CreateWindowExW(0, L"HarnessGame", L"Headless shroudlink fixture", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr,
                            wc.hInstance, nullptr);
    check(g_win && IsWindow(g_win) && !IsWindowVisible(g_win), "message-only HWND %p (not displayed)", static_cast<void*>(g_win));
    g_enum_hook = safetyhook::create_inline(reinterpret_cast<void*>(&EnumWindows), reinterpret_cast<void*>(&headless_enum));
    g_visible_hook = safetyhook::create_inline(reinterpret_cast<void*>(&IsWindowVisible), reinterpret_cast<void*>(&headless_visible));
    check(g_enum_hook && g_visible_hook, "headless window discovery adapters installed");
}

void pump() {
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

WNDPROC top() { return reinterpret_cast<WNDPROC>(GetWindowLongPtrW(g_win, GWLP_WNDPROC)); }

size_t kiln_lines(const char* text) {
    size_t n = 0;
    for (const auto& l : kui::g_log)
        if (l.find(text) != std::string::npos) ++n;
    return n;
}

// The K key once through the whole chain: the game gets it exactly once, with Kiln's procedure once on the stack.
void key_through(const char* when, int kiln_depth) {
    g_game_keys = 0;
    g_game_depth = -1;
    SendMessageW(g_win, WM_KEYDOWN, 'K', 1);
    SendMessageW(g_win, WM_KEYUP, 'K', 0xC0000001);
    check(g_game_keys == 1 && g_game_depth == kiln_depth, "%s: K reaches the game once (got %d), Kiln's procedure %d deep (want %d)",
          when, g_game_keys, g_game_depth, kiln_depth);
    check(kiln_lines("call each other in a loop") == 0, "%s: no window procedure loop", when);
}

// The addon read the MODS key in its procedure: its UI says so in the next frame while playing.
void addon_saw_key(const char* when, uint64_t not_before) {
    set_screen(0x01);
    SendMessageW(g_win, WM_KEYDOWN, 'K', 1);
    SendMessageW(g_win, WM_KEYUP, 'K', 0xC0000001);
    frame();
    set_screen(0);
    pump();
    bool seen = false;
    for (int i = 0; i < 20 && !(seen = log_has("ui: mods key on screen 0x01", not_before)); ++i) Sleep(50);
    check(seen, "%s: the addon's procedure saw the MODS key (shroudlink_client.log: \"ui: mods key on screen 0x01\")", when);
}

HMODULE load_addon(const std::filesystem::path& dll, bool expect_ready) {
    HMODULE m = LoadLibraryW(dll.c_str());
    check(m != nullptr, "addon loaded: %ls at %p", dll.filename().c_str(), static_cast<void*>(m));
    if (!m) return nullptr;
    check(is_addon(m), "Kiln recognises it as shroudlink's client addon (exports %s + %s)", kiln::foreign::kAddonExport,
          kiln::foreign::kAddonProxyExport);
    const auto w = kiln_wait();
    std::printf("harness: Kiln's wait ended %s after %llu ms%s%s\n", kiln::foreign::wait_end_name(w.end),
                static_cast<unsigned long long>(w.waited_ms), w.line.empty() ? "" : ": ", w.line.c_str());
    if (expect_ready)
        check(w.end == kiln::foreign::WaitEnd::Ready || w.end == kiln::foreign::WaitEnd::Hooked,
              "Kiln waited until the addon was done looking (%s, %llu ms)", kiln::foreign::wait_end_name(w.end),
              static_cast<unsigned long long>(w.waited_ms));
    return m;
}

#include "bridge.h"

int run(const std::filesystem::path& dll, const std::string& mode) {
    build_fake_game();
    if (mode == "bridge" || mode == "bridge-late" || mode == "bridge-stock") return bridge_checks(dll, mode == "bridge-stock", mode == "bridge-late");
    const uint64_t t0 = unix_now() - 1;
    if (mode == "unrelated" || mode == "timeout") {
        HMODULE m = LoadLibraryW(dll.c_str());
        check(m != nullptr, "negative module fixture loaded");
        const size_t before = kiln::foreign::waits().size();
        if (mode == "timeout") {
            const auto copy = exe_dir() / L"inert-second.dll";
            std::filesystem::copy_file(dll, copy, std::filesystem::copy_options::overwrite_existing);
            HMODULE second = LoadLibraryW(copy.c_str());
            check(second && second != m, "second inert copy loaded for the shared-budget check");
            // A stale ready line for the same address plus a current PARTIAL ready append cannot release the gate.
            const std::string marker = "shroudlink client 0.8.0 starting (module 0x";
            char addr[32];
            std::snprintf(addr, sizeof addr, "%llx", static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(m)));
            const std::string log = "[1] " + marker + addr + " inert.dll)\n[2] native UI ready: stale\n[" +
                std::to_string(unix_now() + 1) + "] " + marker + addr + " inert.dll)\n[" +
                std::to_string(unix_now() + 1) + "] native UI ready: partial";
            FILE* fp = _wfopen((exe_dir() / kiln::foreign::kAddonLog).c_str(), L"wb");
            check(fp != nullptr, "stale/partial log fixture opened");
            if (fp) { std::fwrite(log.data(), 1, log.size(), fp); std::fclose(fp); }
            // This is a real hook, but belongs to the harness, not the addon. Arbitrary changed UI bytes are not ready.
            g_item_hook = safetyhook::create_inline(g_game.dispatch, reinterpret_cast<void*>(&hk_item));
            check(static_cast<bool>(g_item_hook), "unrelated UI hook installed");
        }
        const auto at = GetTickCount64();
        kiln::foreign::wait_for_scanners();
        const auto elapsed = GetTickCount64() - at;
        if (mode == "unrelated") {
            check(kiln::foreign::waits().size() == before, "forwarded export is not an addon: no wait");
            check(elapsed < 1000, "unrelated proxy adds no startup delay (%llu ms)", elapsed);
        } else {
            const auto& waits = kiln::foreign::waits();
            check(waits.size() == before + 2, "both inert copies recognised safely");
            check(waits.size() == before + 2 && waits[before].end == kiln::foreign::WaitEnd::Timeout &&
                  waits[before + 1].end == kiln::foreign::WaitEnd::Timeout,
                  "stale/partial log and unrelated hook never mean ready");
            check(waits.size() == before + 2 && waits[before + 1].ms < 1000,
                  "second copy gets only the remaining boot budget");
            check(elapsed >= kiln::foreign::kWaitMs && elapsed < kiln::foreign::kWaitMs + 1000,
                  "two-copy timeout bounded to one boot budget (%llu ms)", elapsed);
        }
        return g_fails;
    }
    if (mode == "race") {
        // Reproduce the scan-order race: Kiln hooks item lookup before the addon has looked.
        kiln_hooks_item_lookup();
        HMODULE m = LoadLibraryW(dll.c_str());
        check(m != nullptr, "addon loaded");
        const auto r = addon_says(m, t0, 20000);
        check(r.state == kiln::foreign::AddonState::UiOff && r.line.find("pattern not found: 89 4C 24 08") != std::string::npos,
              "the addon switches its UI off: \"%s\"", r.line.c_str());
        check(log_has("shroudlink client hooks:", t0), "its link to the host is up all the same (\"shroudlink client hooks: ...\")");
        check(std::memcmp(g_game.dispatch, g_dispatch_file, 5) == 0, "no UI hook: no MODS tab, no chest search, no lock rims");
        return g_fails;
    }
    if (mode == "wait") {
        HMODULE m = load_addon(dll, true);
        kiln_hooks_item_lookup();  // after the wait, as kiln-content does now
        const auto r = addon_says(m, t0, 20000);
        check(r.state == kiln::foreign::AddonState::Ready, "the addon's UI is up: \"%s\"", r.line.c_str());
        check(log_has("MODS tab: drawn by the game's tab widget", t0), "addon reports native MODS tab hook installed (not rendered here)");
        check(std::memcmp(g_game.dispatch, g_dispatch_file, 5) != 0, "its UI hook is on the game's UI function");
        return g_fails;
    }
    if (mode == "tabs" || mode == "tabs-late") {
        HMODULE m = load_addon(dll, true);
        kiln_hooks_item_lookup();
        make_window();
        if (mode == "tabs") kui::input_attach(g_win);
        frame();  // input::ensure installs the addon's real window procedure
        if (mode == "tabs-late") kui::input_attach(g_win);
        check(module_of(reinterpret_cast<void*>(top())) == (mode == "tabs" ? m : GetModuleHandleW(nullptr)),
              "tab checks run with the expected subclass order");
        check(addon_says(m, t0, 20000).state == kiln::foreign::AddonState::Ready,
              "native UI ready before exercising visibility predicates");
        // No core/server or welcome/Entries message exists in this fixture. Tab emission must not require one.
        exercise_tabs(6, 1, false, "six tabs before recognition");
        exercise_tabs(8, 1, false, "eight tabs before recognition");
        exercise_tabs(7, 1, true, "seven tabs, no host/menu entries");
        kui::g_menu_open.store(true);
        exercise_tabs(7, 1, true, "Kiln input capture active");
        kui::g_menu_open.store(false);
        exercise_tabs(7, 1, true, "Kiln input capture released");
        // This is a source-backed conditional, NOT a claim that gamepad mode or either reporter supplies action 0x8b.
        exercise_tabs(7, 0x8b, false, "last tab action none (0x8b), UI still ready");
        exercise_tabs(7, 1, true, "last tab action restored");
        reinterpret_cast<void (*)()>(GetProcAddress(m, kiln::foreign::kAddonExport))();
        exercise_tabs(7, 1, false, "addon shut down");
        check(kiln_lines("call each other in a loop") == 0, "tab exercises did not create a procedure loop");
        return g_fails;
    }
    if (mode == "window") {
        HMODULE m = load_addon(dll, true);
        make_window();
        // the main menu: Kiln subclasses when the swapchain is made (vk.cpp), then looks every second (hk_present)
        kui::input_attach(g_win);
        const WNDPROC kiln = top();
        check(module_of(reinterpret_cast<void*>(kiln)) == GetModuleHandleW(nullptr) && kiln != &game_proc && kui::input_attached(),
              "Kiln's procedure is on the window");
        // The addon's first UI dispatch calls input::ensure, regardless of whether a world has opened.
        for (int i = 0; i < 3; ++i) frame();
        const WNDPROC addon = top();
        check(module_of(reinterpret_cast<void*>(addon)) == m && log_has("input: text fields hooked into the game window", t0),
              "the addon subclassed the window on top of Kiln's");
        for (int i = 0; i < 5; ++i) {  // Kiln's check, once a second
            kui::input_attach(g_win);
            frame();
        }
        check(top() == addon, "Kiln's checks leave the addon on top");
        check(kiln_lines("subclassed the game window after Kiln; Kiln's procedure stays in the chain below it") == 1,
              "Kiln said once that the addon is above it");
        key_through("addon above Kiln", 1);
        addon_saw_key("addon above Kiln", t0);
        // Repeated frames/attach checks (the addon retries nothing once it has the window).
        for (int i = 0; i < 240; ++i) {
            frame();
            if (i % 60 == 0) kui::input_attach(g_win);
        }
        check(top() == addon, "still one subclass each after many frames");
        // the addon leaves (its shroudlink_shutdown: what a newer copy calls on a hot reload): it puts back what it
        // replaced, which is Kiln's procedure
        reinterpret_cast<void (*)()>(GetProcAddress(m, kiln::foreign::kAddonExport))();
        check(top() == kiln, "after the addon's shutdown Kiln's procedure is the window's again");
        kui::input_attach(g_win);
        check(top() == kiln, "Kiln leaves it as it is");
        key_through("addon gone", 1);
        // a hot reload: a second copy under a fresh name shuts the first one down, looks for its patterns again and
        // subclasses on top of Kiln when it gets the window
        const auto copy = exe_dir() / L"dinput8-reload.dll";
        std::filesystem::copy_file(dll, copy, std::filesystem::copy_options::overwrite_existing);
        HMODULE m2 = load_addon(copy, true);
        for (int i = 0; i < 3; ++i) frame();
        const WNDPROC addon2 = top();
        check(module_of(reinterpret_cast<void*>(addon2)) == m2 && m2 != m, "the reloaded copy subclassed on top of Kiln's");
        for (int i = 0; i < 3; ++i) kui::input_attach(g_win);
        check(top() == addon2 && kiln_lines("subclassed the game window after Kiln") == 2, "Kiln stays below the new copy");
        key_through("reloaded addon above Kiln", 1);
        addon_saw_key("reloaded addon above Kiln", t0);
        return g_fails;
    }
    if (mode == "window-late") {
        // the addon gets the window first (Kiln's overlay started late), Kiln subclasses on top of it
        HMODULE m = load_addon(dll, true);
        make_window();
        for (int i = 0; i < 3; ++i) frame();
        const WNDPROC addon = top();
        check(module_of(reinterpret_cast<void*>(addon)) == m, "the addon subclassed the window first");
        kui::input_attach(g_win);
        const WNDPROC kiln = top();
        check(kiln != addon && module_of(reinterpret_cast<void*>(kiln)) == GetModuleHandleW(nullptr), "Kiln subclassed on top of it");
        for (int i = 0; i < 3; ++i) kui::input_attach(g_win);
        check(top() == kiln, "Kiln's checks change nothing");
        key_through("Kiln above the addon", 1);
        addon_saw_key("Kiln above the addon", t0);
        // the addon leaves: it puts back the game's procedure, which takes Kiln's out of the chain
        reinterpret_cast<void (*)()>(GetProcAddress(m, kiln::foreign::kAddonExport))();
        check(top() == &game_proc, "the addon's shutdown put the game's procedure back");
        kui::input_attach(g_win);
        check(top() == kiln, "Kiln's next check subclasses again over the game's procedure");
        key_through("addon gone", 1);
        return g_fails;
    }
    std::printf("unknown mode %s\n", mode.c_str());
    return 2;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 3) {
        std::printf("usage: ui-addon-harness <addon or fixture DLL> race|wait|window|window-late|tabs|tabs-late|timeout|unrelated\n");
        return 2;
    }
    char mode[32];
    WideCharToMultiByte(CP_UTF8, 0, argv[2], -1, mode, sizeof mode, nullptr, nullptr);
    const int fails = run(argv[1], mode);
    std::printf("RESULT %s (%d checks, %d failed)\n", fails ? "FAIL" : "ok", g_checks, fails);
    std::fflush(stdout);
    // the addon's threads keep running: leave without unloading anything
    TerminateProcess(GetCurrentProcess(), fails ? 1 : 0);
    return fails ? 1 : 0;
}
