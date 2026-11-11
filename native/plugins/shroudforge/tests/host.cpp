// SPDX-License-Identifier: MIT
// Fake runtime host only: never launches or loads Enshrouded.
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <cstdio>
#include <ctime>
#include <atomic>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>
#include "kiln_api.h"
#include "services/kiln_hooks.h"
#include "services/kiln_profile.h"
#include "services/kiln_ui.h"
static std::filesystem::path game;
static KilnResult path(int32_t kind, char* out, size_t cap, size_t* len) {
    auto p = game;
    if (kind == KILN_PATH_KILN) p /= L"kiln";
    if (kind == KILN_PATH_STATE) p /= L"kiln/state";
    const auto u = p.u8string(); *len = u.size();
    if (cap < u.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out, u.data(), u.size()); return KILN_OK;
}
static void log(KilnOwner, int32_t, KilnStr text) { std::printf("%.*s\n", int(text.len), text.ptr); }
// A fake kiln.hooks v2 (KILN_SF_FAKE_HOOKS=1): one "game" place at 0x1000 whose file bytes are AA BB CC DD EE FF 00 11 22 33
// 44 55; "11 22 33 44 55 66" is ambiguous; mid2 links are kept so main() can fire them with made-up registers.
struct Link { KilnMidFn fn; void* user; std::string label; uintptr_t target; };
static std::vector<Link> links;
static KilnHooksApi fake{};
static std::string text(KilnStr s) { return std::string(s.ptr, s.len); }
static const unsigned char file_bytes[16] = {0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0, 0, 0, 0};
static KilnResult f_scan(KilnStr pattern, uintptr_t* out, uint32_t cap, uint32_t* count) {
    const std::string p = text(pattern);
    if (p == "AA BB CC DD EE FF") { out[0] = 0x1000; *count = 1; return KILN_OK; }
    if (p == "11 22 33 44 55 66") { out[0] = 0x2000; out[1] = 0x3000; *count = 2; return KILN_OK; }
    *count = 0;
    return KILN_ERR_NOT_FOUND;
}
static KilnResult f_scan_hole(KilnStr pattern, uintptr_t match, KilnStr hole, int64_t* out) {
    const std::string h = text(hole);
    if (match != 0x1000 || h.size() < 2 || h[0] != 'h') return KILN_ERR_NOT_FOUND;
    const size_t i = std::stoul(h.substr(1));
    if (i * 4 + 4 > sizeof file_bytes) return KILN_ERR_NOT_FOUND;
    uint32_t v;
    std::memcpy(&v, file_bytes + i * 4, 4);
    *out = v;
    (void)pattern;
    return KILN_OK;
}
static KilnResult f_mid2(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, KilnMidFn fn, void* user, KilnHandle* out) {
    if (target != 0x1000) return KILN_ERR_INVALID_ARGUMENT;
    links.push_back({fn, user, text(order->label), target});
    *out = 0x100 + links.size();
    std::printf("[fake kiln.hooks] mid2 owner=%llu label=%s target=0x%llx\n", (unsigned long long)owner, links.back().label.c_str(), (unsigned long long)target);
    return KILN_OK;
}
static KilnResult f_revoke(KilnHandle h) { std::printf("[fake kiln.hooks] revoke %llu\n", (unsigned long long)h); return KILN_OK; }
static uintptr_t f_image_base() { return 0; }
// A fake kiln.profile (KILN_SF_PROFILE=<file>): an active profile whose text is that file.
static std::string profile_text;
static uint32_t p_mode() { return KILN_PROFILE_ACTIVE; }
static KilnStr p_json() { return KilnStr{profile_text.data(), profile_text.size()}; }
static KilnProfileApi fake_profile{};
// A fake kiln.ui (KILN_SF_FAKE_UI=5: v5 with subscribe_window_input; 4: an older kiln.ui; short: says v5 but its table
// ends before the slot). Its window thread hands records over as kiln.ui does: under a gate per call; unsubscribe
// clears the callback, then waits at the gate for a call in flight. KILN_SF_FAKE_UI_STUCK=1: unsubscribe fails
// (KILN_ERR_WRONG_THREAD) and keeps calling, so only the adapter's own guard stands between a record and its runtime.
static KilnUiApi fake_ui{};
static void mark(const char* what) {  // the same call log as the fake runtime's, written the same way (fake.cpp)
    wchar_t file[32768]{};
    if (!GetEnvironmentVariableW(L"KILN_SF_TEST_LOG", file, 32768)) return;
    HANDLE f = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    const std::string line = std::string(what) + "\n";
    DWORD written = 0;
    WriteFile(f, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    CloseHandle(f);
}
static std::mutex ui_gate, ui_mu;
static KilnUiWindowInputFn ui_fn = nullptr;
static void* ui_user = nullptr;
static constexpr KilnHandle kUiHandle = 0x5eed;
static KilnResult ui_subscribe(KilnOwner owner, KilnUiWindowInputFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(ui_mu);
    ui_fn = fn;
    ui_user = user;
    if (out) *out = kUiHandle;
    std::printf("[fake kiln.ui] subscribe_window_input owner=%llu\n", (unsigned long long)owner);
    mark("ui-subscribe");
    return KILN_OK;
}
static KilnResult ui_unsubscribe(KilnHandle h) {
    if (h != kUiHandle) return KILN_ERR_NOT_FOUND;
    if (GetEnvironmentVariableW(L"KILN_SF_FAKE_UI_STUCK", nullptr, 0)) {
        std::printf("[fake kiln.ui] unsubscribe refused\n");
        mark("ui-unsubscribe-refused");
        return KILN_ERR_WRONG_THREAD;
    }
    {
        std::lock_guard lock(ui_mu);
        ui_fn = nullptr;
    }
    std::lock_guard call(ui_gate);
    std::printf("[fake kiln.ui] unsubscribe\n");
    mark("ui-unsubscribe");
    return KILN_OK;
}
static bool ui_deliver(KilnUiWindowInput r) {
    if (!r.size) r.size = sizeof r;
    std::lock_guard call(ui_gate);
    KilnUiWindowInputFn fn;
    void* user;
    {
        std::lock_guard lock(ui_mu);
        fn = ui_fn;
        user = ui_user;
    }
    if (!fn) return false;
    fn(&r, user);
    return true;
}
static KilnUiWindowInput rec(uint32_t kind, uint32_t message) {
    KilnUiWindowInput r{};
    r.kind = kind;
    r.message = message;
    return r;
}
static KilnUiWindowInput raw(uint32_t make_code, uint32_t flags, uint32_t vkey) {
    auto r = rec(KILN_UI_INPUT_RAW_KEY, WM_INPUT);
    r.make_code = make_code, r.flags = flags, r.vkey = vkey;
    return r;
}
static KilnUiWindowInput key(uint32_t message, uint64_t w, int64_t l) {
    auto r = rec(KILN_UI_INPUT_WINDOW_KEY, message);
    r.wparam = w, r.lparam = l;
    return r;
}
static KilnUiWindowInput button(uint32_t message, uint32_t b, uint32_t down) {
    auto r = rec(KILN_UI_INPUT_MOUSE_BUTTON, message);
    r.button = b, r.down = down;
    return r;
}
// What the game window gives kiln.ui, in its order: window keys before the first raw keyboard record, raw keys and their
// window messages, button messages before the first raw button record, raw buttons and their messages, a focus loss, a
// kind this adapter does not know, and a record too short to read.
static void play_script() {
    ui_deliver(key(WM_KEYDOWN, 'A', 0x1e0001));
    ui_deliver(key(WM_KEYUP, 'A', 0xc01e0001));
    ui_deliver(raw(0x43, 0, VK_F9));
    ui_deliver(key(WM_KEYDOWN, VK_F9, 0x430001));
    ui_deliver(raw(0x43, RI_KEY_BREAK, VK_F9));
    ui_deliver(key(WM_KEYUP, VK_F9, 0xc0430001));
    ui_deliver(key(WM_SYSKEYDOWN, VK_MENU, 0x20380001));
    ui_deliver(button(WM_XBUTTONDOWN, 4, 1));
    ui_deliver(button(WM_XBUTTONUP, 4, 0));
    ui_deliver(button(WM_INPUT, 1, 1));
    ui_deliver(button(WM_LBUTTONDOWN, 1, 1));
    ui_deliver(button(WM_INPUT, 1, 0));
    ui_deliver(button(WM_LBUTTONUP, 1, 0));
    ui_deliver(rec(KILN_UI_INPUT_FOCUS_LOST, WM_KILLFOCUS));
    ui_deliver(rec(99, 0));
    auto short_record = raw(0x44, 0, VK_F10);
    short_record.size = offsetof(KilnUiWindowInput, wparam);
    ui_deliver(short_record);
    std::printf("[fake kiln.ui] script delivered\n");
}
// KILN_SF_REAL_KEYS (real_keys.py): the real runtime with a probe mod that logs its shortcut state. Steps a quarter of a
// second apart, so that its 10 ms update sees each one; the probe's lines land in ShroudForge's own log.
static bool log_has(const std::filesystem::path& file, const char* needle, int wait_ms) {
    for (int waited = 0;; waited += 50) {
        std::ifstream in(file, std::ios::binary);
        const std::string text((std::istreambuf_iterator<char>(in)), {});
        if (text.find(needle) != std::string::npos) return true;
        if (waited >= wait_ms) return false;
        Sleep(50);
    }
}
static void play_real(const std::filesystem::path& game) {
    if (!log_has(game / L"shroudforge/logs/shroudforge-client.log", "probe ready", 30000)) {
        std::printf("[real] the probe mod did not report ready\n");
        return;
    }
    const auto step = [](const char* what, KilnUiWindowInput r) {
        ui_deliver(r);
        std::printf("[real] %s\n", what);
        Sleep(250);
    };
    step("F9 down (raw)", raw(0x43, 0, VK_F9));
    step("F9 up (raw)", raw(0x43, RI_KEY_BREAK, VK_F9));
    step("F9 down (window message, after raw input: the same key again, not forwarded)", key(WM_KEYDOWN, VK_F9, 0x430001));
    step("F9 up (window message)", key(WM_KEYUP, VK_F9, 0xc0430001));
    step("left Ctrl down (raw)", raw(0x1d, 0, VK_CONTROL));
    step("F9 down (raw)", raw(0x43, 0, VK_F9));
    step("F9 up (raw)", raw(0x43, RI_KEY_BREAK, VK_F9));
    step("left Ctrl up (raw)", raw(0x1d, RI_KEY_BREAK, VK_CONTROL));
    step("XBUTTON1 down (raw)", button(WM_INPUT, 4, 1));
    step("XBUTTON1 up (raw)", button(WM_INPUT, 4, 0));
    step("F9 down (raw)", raw(0x43, 0, VK_F9));
    step("focus lost with F9 held", rec(KILN_UI_INPUT_FOCUS_LOST, WM_KILLFOCUS));
    step("F9 up (raw) after the focus loss", raw(0x43, RI_KEY_BREAK, VK_F9));
    std::printf("[real] steps done\n");
}
// ShroudForge answers shortcut queries only while a window of this process is in front (GetForegroundWindow). Wine
// without a display has no top-level windows, so the runtime's GetForegroundWindow import answers with a message-only
// window of this process: an OS stand-in for "the game window is in front"; the runtime's own check stays as it is.
static HWND g_front = nullptr;
static HWND WINAPI front_window() { return g_front; }
static bool patch_import(HMODULE module, const char* function, void* with, void** original);
static void game_window_in_front() {
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"KilnSfRealKeys";
    RegisterClassW(&wc);
    g_front = CreateWindowExW(0, wc.lpszClassName, L"real keys", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
    void* original = nullptr;
    HMODULE runtime = GetModuleHandleW(L"shroudforge-runtime.dll");
    const bool patched = g_front && runtime &&
                         patch_import(runtime, "GetForegroundWindow", reinterpret_cast<void*>(&front_window), &original);
    DWORD pid = 0;
    GetWindowThreadProcessId(g_front, &pid);
    std::printf("[real] message-only window %p of this process (%s); the runtime's GetForegroundWindow answers it: %s\n",
                static_cast<void*>(g_front), pid == GetCurrentProcessId() ? "yes" : "no", patched ? "yes" : "no");
}
static void* ui_table() {
    wchar_t v[8] = L"";
    if (!GetEnvironmentVariableW(L"KILN_SF_FAKE_UI", v, 8)) return nullptr;
    fake_ui.header = {sizeof fake_ui, 5};
    fake_ui.unsubscribe = ui_unsubscribe;
    fake_ui.subscribe_window_input = ui_subscribe;
    if (!wcscmp(v, L"4")) fake_ui.header = {static_cast<uint32_t>(offsetof(KilnUiApi, subscribe_window_input)), 4};
    if (!wcscmp(v, L"short")) fake_ui.header.size = static_cast<uint32_t>(offsetof(KilnUiApi, subscribe_window_input));
    return &fake_ui;
}
static const void* service(KilnStr name, uint32_t min) {
    if (text(name) == KILN_UI_SERVICE) {
        const auto* t = static_cast<const KilnUiApi*>(ui_table());
        return t && t->header.version >= min ? t : nullptr;  // as the core: a version at least `min`
    }
    if (text(name) == KILN_PROFILE_SERVICE) {
        wchar_t file[1024];
        const DWORD n = GetEnvironmentVariableW(L"KILN_SF_PROFILE", file, 1024);
        if (!n || n >= 1024 || min > 1) return nullptr;
        if (FILE* f = _wfopen(file, L"rb")) {
            char buf[4096];
            size_t got;
            profile_text.clear();
            while ((got = std::fread(buf, 1, sizeof buf, f)) > 0) profile_text.append(buf, got);
            std::fclose(f);
        }
        fake_profile.header = {sizeof fake_profile, KILN_PROFILE_VERSION};
        fake_profile.mode = p_mode;
        fake_profile.json = p_json;
        return &fake_profile;
    }
    if (!GetEnvironmentVariableW(L"KILN_SF_FAKE_HOOKS", nullptr, 0) || text(name) != KILN_HOOKS_SERVICE || min > 2) return nullptr;
    fake.header = {sizeof fake, 2};
    fake.image_base = f_image_base;
    fake.scan = f_scan;
    fake.scan_hole = f_scan_hole;
    fake.mid2 = f_mid2;
    fake.revoke = f_revoke;
    return &fake;
}
static uint32_t side() { return GetEnvironmentVariableW(L"KILN_SF_CLIENT", nullptr, 0) ? KILN_SIDE_CLIENT : KILN_SIDE_SERVER; }
// A windowless stand-in for the installed helper: owns/consumes precisely SF's public auto-reset toggle event.
static int helper(int argc, char** argv) {
    if (std::string(argv[1]) != "--module-ui") return 0;
    std::wstring pid, stop;
    for (int i = 2; i + 1 < argc; ++i) {
        if (std::string(argv[i]) == "--game-pid") pid.assign(argv[i + 1], argv[i + 1] + std::strlen(argv[i + 1]));
        if (std::string(argv[i]) == "--stop-event") stop.assign(argv[i + 1], argv[i + 1] + std::strlen(argv[i + 1]));
    }
    auto event = CreateEventW(nullptr, FALSE, FALSE, (L"Local\\ShroudForge.ModMenu.Open." + pid).c_str());
    auto end = OpenEventW(SYNCHRONIZE, FALSE, stop.c_str());
    HANDLE handles[] = {end, event};
    if (!end || !event) return 10;
    while (true) {
        const auto wait = WaitForMultipleObjects(2, handles, FALSE, 100);
        if (wait == WAIT_OBJECT_0) break;
        if (wait == WAIT_OBJECT_0 + 1) {
            if (auto ack = OpenEventW(EVENT_MODIFY_STATE, FALSE, (L"Local\\Kiln.SfTest.Ack." + pid).c_str())) {
                SetEvent(ack);
                CloseHandle(ack);
            }
        }
        if (GetEnvironmentVariableW(L"KILN_SF_HELPER_EXIT", nullptr, 0)) break;
    }
    CloseHandle(end); CloseHandle(event);
    return 0;
}
// Slow file I/O for this fake process's adapter only, not for any runtime ABI call: every rename it makes (heartbeat,
// shroudforge.json) and each open of ShroudForge's log for the [sf] mirror take 350 ms, and each prints
// "slow-io <begin> <end> <thread> <file>". No production test switch or game-memory patch is needed.
using MoveFileFn = BOOL (WINAPI*)(LPCWSTR, LPCWSTR, DWORD);
using CreateFileFn = HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
static MoveFileFn original_move;
static CreateFileFn original_create;
static void slow_io(LPCWSTR file) {
    const auto begin = GetTickCount64();
    Sleep(350);
    std::printf("slow-io %llu %llu %lu %ls\n", begin, GetTickCount64(), GetCurrentThreadId(),
                std::filesystem::path(file).filename().c_str());
}
static BOOL WINAPI slow_move(LPCWSTR from, LPCWSTR to, DWORD flags) {
    if (to) slow_io(to);
    return original_move(from, to, flags);
}
static HANDLE WINAPI slow_create(LPCWSTR name, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES sa, DWORD how, DWORD flags,
                                 HANDLE tmpl) {
    if (name && std::filesystem::path(name).filename() == L"shroudforge-server.log") slow_io(name);
    return original_create(name, access, share, sa, how, flags, tmpl);
}
static bool patch_import(HMODULE module, const char* function, void* with, void** original) {
    auto* image = reinterpret_cast<unsigned char*>(module);
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
    const auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    for (auto* imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(image + directory.VirtualAddress);
         imports->Name; ++imports) {
        if (!imports->OriginalFirstThunk) continue;
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA64*>(image + imports->OriginalFirstThunk);
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA64*>(image + imports->FirstThunk);
        for (unsigned i = 0; names[i].u1.AddressOfData; ++i) {
            if (IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal)) continue;
            const auto* named = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(image + names[i].u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(named->Name), function)) continue;
            DWORD protection{}, ignored{};
            if (!VirtualProtect(&slots[i].u1.Function, sizeof(ULONGLONG), PAGE_READWRITE, &protection)) return false;
            *original = reinterpret_cast<void*>(slots[i].u1.Function);
            slots[i].u1.Function = reinterpret_cast<ULONGLONG>(with);
            return VirtualProtect(&slots[i].u1.Function, sizeof(ULONGLONG), protection, &ignored) != FALSE;
        }
    }
    return false;
}
static bool slow_heartbeat(HMODULE module) {
    return patch_import(module, "MoveFileExW", reinterpret_cast<void*>(&slow_move), reinterpret_cast<void**>(&original_move)) &&
           patch_import(module, "CreateFileW", reinterpret_cast<void*>(&slow_create), reinterpret_cast<void**>(&original_create));
}
int main(int argc, char** argv) {
    if (argc > 1 && std::string(argv[1]).starts_with("--")) return helper(argc, argv);
    if (argc < 3) return 2;
    game = std::filesystem::absolute(argv[1]);
    if (GetEnvironmentVariableW(L"KILN_SF_BUSY_LOCK", nullptr, 0)) {
        HANDLE lock = CreateFileW((game / L".kiln-eml.lock").c_str(), GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        OVERLAPPED overlapped{};
        if (lock == INVALID_HANDLE_VALUE || !LockFileEx(lock, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY,
                                                       0, MAXDWORD, MAXDWORD, &overlapped)) return 7;
        // Deliberately retained while the adapter tries a different handle, simulating another process's lock.
    }
    if (GetEnvironmentVariableW(L"KILN_SF_SELF_HOSTED", nullptr, 0)) LoadLibraryW((game / L"bootstrap.dll").c_str());
    auto dll = std::filesystem::absolute(argv[2]);
    if (argc > 3 && std::string(argv[3]) == "early") LoadLibraryW((game / L"kiln/plugins/kiln-eml.dll").c_str());
    HMODULE h = LoadLibraryW(dll.c_str()); if (!h) return 3;
    const bool delayed = GetEnvironmentVariableW(L"KILN_SF_SLOW_HEARTBEAT", nullptr, 0) != 0;
    const bool stop_during_write = GetEnvironmentVariableW(L"KILN_SF_STOP_DURING_HEARTBEAT", nullptr, 0) != 0;
    const bool stalled = GetEnvironmentVariableW(L"KILN_SF_STALL_UPDATE", nullptr, 0) != 0;
    if (stalled) std::printf("heartbeat-start %lld\n", static_cast<long long>(std::time(nullptr)));
    if (delayed && !slow_heartbeat(h)) return 8;
    auto fn = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, argc > 3 ? "kiln_plugin_early" : "kiln_plugin_main"));
    if (!fn) return 4;
    KilnApi api{}; api.size = sizeof api; api.abi_version = KILN_ABI_VERSION;
    api.path = path; api.log = log; api.service = service; api.side = side;
    KilnPluginInfo info{}; info.size = sizeof info;
    if (fn(&api, 1, &info) != KILN_OK) return 5;
    const bool real = GetEnvironmentVariableW(L"KILN_SF_REAL_KEYS", nullptr, 0) != 0;
    if (info.on_ready) info.on_ready(info.user);
    if (real) game_window_in_front();
    // kiln.ui's window thread (KILN_SF_FAKE_UI): the script once, or (KILN_SF_FEED_RACE) key records without a pause
    // until the subscription is gone, while this thread tears the adapter down below.
    std::thread window;
    std::atomic<bool> window_stop{false};
    std::atomic<long> window_records{0}, window_late{0};
    std::atomic<bool> torn_down{false};
    if (GetEnvironmentVariableW(L"KILN_SF_FEED_RACE", nullptr, 0)) {
        window = std::thread([&] {
            while (!window_stop) {
                if (!ui_deliver(raw(0x43, 0, VK_F9)) || !ui_deliver(raw(0x43, RI_KEY_BREAK, VK_F9))) break;
                window_records += 2;
                if (torn_down) window_late += 2;
            }
        });
        Sleep(150);
    } else if (real) {
        play_real(game);
    } else if (GetEnvironmentVariableW(L"KILN_SF_FAKE_UI", nullptr, 0)) {
        play_script();
    }
    Sleep(stalled ? 2900 : stop_during_write ? 60 : delayed ? 1300 : 120);  // delayed: one writer turn (3 x 350 ms) in the loop
    if (GetEnvironmentVariableW(L"KILN_SF_CLIENT", nullptr, 0)) {
        const auto suffix = std::to_wstring(GetCurrentProcessId());
        HANDLE event = nullptr;
        const bool self = GetEnvironmentVariableW(L"KILN_SF_SELF_HOSTED", nullptr, 0) != 0;
        const bool off = GetEnvironmentVariableW(L"KILN_SF_MENU_OFF", nullptr, 0) != 0;
        if (self || off) {
            event = OpenEventW(SYNCHRONIZE, FALSE, (L"Local\\ShroudForge.ModMenu.Open." + suffix).c_str());
            if (event) return 11;  // Kiln must not claim a self-hosted/disabled helper's event
        } else {
            HANDLE ack = CreateEventW(nullptr, FALSE, FALSE, (L"Local\\Kiln.SfTest.Ack." + suffix).c_str());
            for (int i = 0; i < 100 && !event; ++i) {
                event = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (L"Local\\ShroudForge.ModMenu.Open." + suffix).c_str());
                if (!event) Sleep(20);
            }
            if (!event) return 12;
            const bool installed = std::filesystem::exists(game / L"shroudforge/shroudforge.exe");
            const bool exits = GetEnvironmentVariableW(L"KILN_SF_HELPER_EXIT", nullptr, 0) != 0;
            if (exits) Sleep(2300);  // worker notices a dead helper and publishes the Kiln handoff
            for (int i = 0; i < 3; ++i) {
                SetEvent(event);
                if (installed && !exits) {
                    if (WaitForSingleObject(ack, 2000) != WAIT_OBJECT_0) return 13;
                    if (WaitForSingleObject(event, 0) != WAIT_TIMEOUT) return 14;
                } else {
                    if (WaitForSingleObject(event, 2000) != WAIT_OBJECT_0) return 15;
                    if (WaitForSingleObject(event, 0) != WAIT_TIMEOUT) return 16;
                }
            }
            CloseHandle(event); CloseHandle(ack);
            if (installed && !exits) std::printf("menu: three auto-reset toggles received by helper\n");
            // No Kiln.Ui here: this process sets the event and takes it back itself. That shows the adapter holds an
            // auto-reset event of ShroudForge's name (in game kfc-runtime.dll may have created it first), not that
            // Kiln.Ui, which waits on it in game, received it.
            else std::printf("menu: Kiln's auto-reset event is open; three toggles set and taken back by this test\n");
        }
    }
    if (delayed && !stop_during_write) {
        std::ifstream heartbeat(game / L"shroudforge/runtime/heartbeat.json");
        const std::string contents((std::istreambuf_iterator<char>(heartbeat)), {});
        std::printf("heartbeat %s\n", contents.c_str());
        std::printf("heartbeat-observed %lld\n", static_cast<long long>(std::time(nullptr)));
    }
    // What the game would do: run each hook with registers of its own (r15 0x15, rbp 0xbb, rcx 0xc7, r14 0x14, r13 0x13).
    for (const Link& l : links) {
        KilnRegisters regs{};
        regs.r15 = 0x15, regs.rbp = 0xbb, regs.rcx = 0xc7, regs.r14 = 0x14, regs.r13 = 0x13;
        l.fn(&regs, l.user);
    }
    // The core may call normal main again after the early pass; it must not prepare or start a second worker.
    if (argc > 3) {
        auto normal = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, "kiln_plugin_main"));
        info = {}; info.size = sizeof info;
        if (normal(&api, 2, &info) != KILN_OK) return 6;
        if (info.on_ready) info.on_ready(info.user);
    }
    if (info.on_unload) info.on_unload(info.user);
    Sleep(delayed ? 2500 : 150); // allow signalled, serial worker cleanup (slow writer join + final status) before exit
    if (window.joinable()) {
        torn_down = true;  // the worker's teardown (unsubscribe, end, destroy) is done by now; records keep coming
        Sleep(300);
        window_stop = true;
        window.join();
        std::printf("[fake kiln.ui] window thread delivered %ld records, %ld after the teardown\n", window_records.load(),
                    window_late.load());
    }
    return 0;
}
