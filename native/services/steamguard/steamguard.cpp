// SPDX-License-Identifier: MIT
// kiln.steamguard: the achievement guard of the game client. Contract:
// native/core/abi/services/kiln_steamguard.h; evidence and the reverse engineering:
// native/services/steamguard/README.md.
//
// What it guards. enshrouded.exe reports an achievement to Steam through one of its own functions, the achievement
// provider's apply method (sigdb steamguard/ReportAchievements, provider vtable slot 6): it walks the queued
// achievement/stat operations and is the single place that calls ISteamUserStats::SetAchievement (interface vtable
// +0x30) and ::StoreStats (+0x48). While the guard is on, kiln.steamguard detours that function and drops the call:
// nothing the game would have reported reaches Steam. An unlocked Steam achievement cannot be taken back, so every
// doubt (the target not verified, the hook not in, another module hooked over it, a read that failed) reads as "not
// protecting".
//
// The hook goes in on the first latch or hold, never at boot: a game where nobody runs a guarded check gets no hook
// on this path. The target is resolved and verified at boot, so TargetVerified is known before anyone asks. There is
// no "off" call: a latch lasts until the game exits, a hold until its token is released; a failure is sticky.
//
// Client only: on a dedicated server there is no Steam user, the symbol is not in the server definitions, and the
// service is not registered (it logs that once).
#ifndef KILN_STEAMGUARD_HOST_TEST
#include <windows.h>
#endif

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "kiln_api.h"
#ifndef KILN_STEAMGUARD_HOST_TEST
#include "kiln_hook.hpp"
#include "nlohmann/json.hpp"
#endif
#include "services/kiln_steamguard.h"

// Kiln.Client's mirror (managed/services/Kiln.Client/SteamGuard.cs) has the same sizes.
static_assert(sizeof(KilnSteamGuardStatus) == 32 + KILN_STEAMGUARD_REASON_MAX);
static_assert(sizeof(KilnSteamGuardApi) == sizeof(KilnServiceHeader) + 4 * sizeof(void*));

namespace {

// The value ReportAchievements returns on its normal path when the batch produced no newly-unlocked achievement (the
// accumulator byte it finally moves into al is 0 then): "ran, nothing to report". Returning it drops the write without
// the game logging a failure or taking its provider-not-ready retry path (which returns 0x18). What only a real client
// can confirm is that the game then drops the queued operation instead of re-submitting it; see the README and the
// first-run watch.
constexpr uint64_t kDropReturn = 0;

// steamguard/ReportAchievements entry bytes, as the sigdb pattern names them (frame displacements wildcarded). The
// guard re-reads the live bytes at boot, before it installs anything, and only verifies the target when they match:
// this catches another engine that hooked the function before Kiln (its live bytes would be a jmp). After the guard
// installs its own detour these bytes are Kiln's jmp, so the check is a boot-time check; its "still holds" part is the
// foreign-over test below.
constexpr uint8_t kEntry[] = {0x40, 0x55, 0x53, 0x41, 0x55, 0x48, 0x8D, 0xAC, 0x24, 0x00, 0x00, 0xFF, 0xFF, 0x48,
                              0x81, 0xEC, 0x00, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0xE9, 0x49, 0x8B, 0xD8, 0x48, 0x8B,
                              0x49, 0x70, 0x48, 0x8B, 0x01, 0xFF, 0x50, 0x08, 0x84, 0xC0, 0x75, 0x00, 0xB0, 0x18};
constexpr uint8_t kEntryMask[] = {1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 0, 0, 1, 1, 1,
                                  1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1};

const char kUnlockTextPrefix[] = "[online] Achievement";  // steamguard/AchievementUnlockText reads back as this

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

std::mutex g_mu;                        // install, the token list and the state booleans; never taken by the detour
std::atomic<bool> g_latched{false};     // a latch is on (until the game exits)
std::atomic<uint32_t> g_holds{0};       // live holds (mirrors g_hold_tokens.size(), read lock-free by the detour)
std::vector<uint64_t> g_hold_tokens;    // live hold tokens, under g_mu (so a replayed release is NOT_FOUND)
std::atomic<bool> g_installed{false};   // the detour is in the game's code
std::atomic<bool> g_failed{false};      // sticky: a check or the install failed
std::atomic<uint64_t> g_dropped{0};
std::atomic<uint64_t> g_passed{0};
std::atomic<uint64_t> g_next_token{1};

bool g_boot_verified = false;  // the boot check passed (symbol, live bytes, second sign)
uintptr_t g_target = 0;        // steamguard/ReportAchievements
uintptr_t g_sign = 0;          // steamguard/AchievementUnlockText (the log string)
char g_reason[KILN_STEAMGUARD_REASON_MAX] = "not asked yet";

void set_reason(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(g_reason, sizeof g_reason, fmt, ap);
    va_end(ap);
}

bool guard_active() { return g_latched.load(std::memory_order_acquire) || g_holds.load(std::memory_order_acquire) != 0; }

void logf(int32_t level, const char* fmt, ...);

// ------------------------------------------------------------------------------------------------- the backend ----
// These five operations are the only contact with kiln.hooks and the game. The host test replaces them with
// stand-ins so the guard's logic runs with no game and no Wine.

bool bk_resolve(const char* name, uintptr_t* out);           // sigdb symbol -> address (0/1 match; the core's rule)
bool bk_read(uintptr_t addr, void* dst, size_t n);           // SEH-guarded read of game memory
bool bk_install(uintptr_t target);                           // put the detour in; false on any failure
bool bk_link_present(uintptr_t target);                      // kiln.hooks still has our link on the target
bool bk_foreign_over(uintptr_t target);                      // another module hooked over our detour
uint64_t bk_call_original(void* a, void* b, void* c, void* d);  // the rest of the chain, then the real function

// ------------------------------------------------------------------------------------------------- the detour ----

void on_dropped(void* ops) {
    const uint64_t n = g_dropped.fetch_add(1, std::memory_order_relaxed) + 1;
    // Name the operation, best effort and fault-safe (bk_read reports a bad pointer instead of faulting). The batch is
    // {array ptr, count}; each operation's first u32 is the game's achievement/stat id. A read that fails just drops
    // the id from the line; the call is dropped either way.
    uintptr_t base = 0;
    uint64_t count = 0;
    uint32_t id = 0;
    bool have_id = false;
    if (ops && bk_read(reinterpret_cast<uintptr_t>(ops), &base, sizeof base) &&
        bk_read(reinterpret_cast<uintptr_t>(ops) + 8, &count, sizeof count) && base && count &&
        bk_read(base, &id, sizeof id)) {
        have_id = true;
    }
    if (have_id)
        logf(KILN_LOG_WARN, "dropped a Steam achievement/stat report (id 0x%08x, %llu in the batch) while the guard is on; total dropped %llu",
             id, static_cast<unsigned long long>(count), static_cast<unsigned long long>(n));
    else
        logf(KILN_LOG_WARN, "dropped a Steam achievement/stat report while the guard is on; total dropped %llu", static_cast<unsigned long long>(n));
}

// The target's own signature is enough integer arguments for this build (rcx provider, rdx, r8 ops, r9). When the
// guard is on the call is replaced: the chain and the real function never run, so nothing reaches Steam. When it is
// off the call passes through and is counted.
uint64_t KILN_CALL hk_report(void* provider, void* a2, void* ops, void* a4) {
    if (guard_active()) {
        on_dropped(ops);
        return kDropReturn;
    }
    g_passed.fetch_add(1, std::memory_order_relaxed);
    return bk_call_original(provider, a2, ops, a4);
}

// ------------------------------------------------------------------------------------- boot-time verification ----

// Reads the live entry bytes and compares them with the pattern's literal bytes. Returns false when they differ (the
// function was hooked before Kiln looked, or the build moved): fail closed.
bool entry_bytes_ok(uintptr_t target) {
    uint8_t live[sizeof kEntry];
    if (!bk_read(target, live, sizeof live)) return false;
    for (size_t i = 0; i < sizeof kEntry; ++i)
        if (kEntryMask[i] && live[i] != kEntry[i]) return false;
    return true;
}

// Reads the string steamguard/AchievementUnlockText and checks it is the achievement log text: the independent sign
// that this is the function that reports achievements. Re-checkable at any time (the string does not move when the
// detour goes in).
bool second_sign_ok(uintptr_t sign) {
    char buf[sizeof kUnlockTextPrefix];
    if (!bk_read(sign, buf, sizeof buf - 1)) return false;
    buf[sizeof buf - 1] = 0;
    return std::strcmp(buf, kUnlockTextPrefix) == 0;
}

// Runs once, before any install, while the target still holds the game's own bytes.
void verify_at_boot() {
    if (!bk_resolve("steamguard/ReportAchievements", &g_target) || g_target == 0) {
        set_reason("steamguard/ReportAchievements did not resolve on this build");
        g_failed.store(true, std::memory_order_release);
        return;
    }
    if (!bk_resolve("steamguard/AchievementUnlockText", &g_sign) || g_sign == 0) {
        set_reason("the achievement log text (the second sign) did not resolve");
        g_failed.store(true, std::memory_order_release);
        return;
    }
    if (!entry_bytes_ok(g_target)) {
        set_reason("the target's live bytes differ from the signature (hooked before Kiln, or the build moved)");
        g_failed.store(true, std::memory_order_release);
        return;
    }
    if (!second_sign_ok(g_sign)) {
        set_reason("the second sign (the achievement log text) did not read back");
        g_failed.store(true, std::memory_order_release);
        return;
    }
    g_boot_verified = true;
    set_reason("target verified; the hook goes in on the first latch or hold");
}

// True when the target is verified now: the boot check passed, nothing has failed, and the second sign still reads.
bool target_verified_now() {
    if (!g_boot_verified || g_failed.load(std::memory_order_acquire)) return false;
    return second_sign_ok(g_sign);
}

// True when the guard's hook is in and no foreign hook sits over it.
bool hook_installed_now() {
    if (!g_installed.load(std::memory_order_acquire) || g_failed.load(std::memory_order_acquire)) return false;
    if (!bk_link_present(g_target)) return false;
    if (bk_foreign_over(g_target)) return false;
    return true;
}

// Under g_mu. Puts the detour in once; records why on failure. Returns true when the hook is in and protecting is
// still possible.
bool ensure_installed() {
    if (g_failed.load(std::memory_order_acquire)) return false;
    if (!target_verified_now()) {
        set_reason("the target is not verified: not protecting");
        g_failed.store(true, std::memory_order_release);
        return false;
    }
    if (!g_installed.load(std::memory_order_acquire)) {
        if (!bk_install(g_target)) {
            set_reason("kiln.hooks could not install the guard's detour: not protecting");
            g_failed.store(true, std::memory_order_release);
            return false;
        }
        g_installed.store(true, std::memory_order_release);
    }
    if (bk_foreign_over(g_target)) {
        set_reason("another module hooked over the guard's detour: not protecting");
        g_failed.store(true, std::memory_order_release);
        return false;
    }
    if (!bk_link_present(g_target)) {
        set_reason("kiln.hooks does not report the guard's link in place: not protecting");
        g_failed.store(true, std::memory_order_release);
        return false;
    }
    return true;
}

bool protecting_now() {
    return target_verified_now() && hook_installed_now() && guard_active();
}

// --------------------------------------------------------------------------------------------------- the table ----

KilnResult KILN_CALL t_status(KilnSteamGuardStatus* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    if (size < offsetof(KilnSteamGuardStatus, holds)) return KILN_ERR_INVALID_ARGUMENT;  // must cover flags
    std::lock_guard lk(g_mu);
    const bool verified = target_verified_now();
    const bool installed = hook_installed_now();
    const bool active = guard_active();
    uint32_t flags = 0;
    if (verified) flags |= KILN_STEAMGUARD_TARGET_VERIFIED;
    if (installed) flags |= KILN_STEAMGUARD_HOOK_INSTALLED;
    if (active) flags |= KILN_STEAMGUARD_ACTIVE;
    if (g_latched.load(std::memory_order_acquire)) flags |= KILN_STEAMGUARD_LATCHED;
    if (g_failed.load(std::memory_order_acquire)) flags |= KILN_STEAMGUARD_FAILED;
    // Fill only as far as `size` allows; a reader that passed a smaller struct gets just the first fields.
    auto fill = [&](size_t field_end, auto writer) {
        if (size >= field_end) writer();
    };
    out->size = size;
    fill(offsetof(KilnSteamGuardStatus, holds), [&] { out->flags = flags; });
    fill(offsetof(KilnSteamGuardStatus, reserved), [&] { out->holds = g_holds.load(std::memory_order_acquire); });
    fill(offsetof(KilnSteamGuardStatus, dropped), [&] { out->reserved = 0; });
    fill(offsetof(KilnSteamGuardStatus, passed), [&] { out->dropped = g_dropped.load(std::memory_order_relaxed); });
    fill(offsetof(KilnSteamGuardStatus, reason), [&] { out->passed = g_passed.load(std::memory_order_relaxed); });
    if (size >= sizeof(KilnSteamGuardStatus)) {
        std::snprintf(out->reason, sizeof out->reason, "%s", g_reason);
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_latch(KilnStr reason) {
    if (reason.ptr == nullptr || reason.len == 0) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    logf(KILN_LOG_INFO, "latch requested: %.*s", static_cast<int>(reason.len), reason.ptr);
    g_latched.store(true, std::memory_order_release);  // the intent is latched before the install so a racing status reads it
    if (!ensure_installed()) {
        return KILN_ERR_UNAVAILABLE;
    }
    return protecting_now() ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

KilnResult KILN_CALL t_hold(KilnStr reason, uint64_t* out_token) {
    if (out_token) *out_token = 0;
    if (reason.ptr == nullptr || reason.len == 0 || out_token == nullptr) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    logf(KILN_LOG_INFO, "hold requested: %.*s", static_cast<int>(reason.len), reason.ptr);
    const uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    g_hold_tokens.push_back(token);
    g_holds.store(static_cast<uint32_t>(g_hold_tokens.size()), std::memory_order_release);  // on before the install
    if (!ensure_installed() || !protecting_now()) {
        g_hold_tokens.pop_back();  // could not protect: nothing is held
        g_holds.store(static_cast<uint32_t>(g_hold_tokens.size()), std::memory_order_release);
        return KILN_ERR_UNAVAILABLE;
    }
    *out_token = token;
    return KILN_OK;
}

KilnResult KILN_CALL t_release(uint64_t token) {
    std::lock_guard lk(g_mu);
    // Each live token is released once. A token never issued, or already released, is NOT_FOUND: a replayed release
    // cannot drop the count twice and so cannot switch the guard off under another holder.
    const auto it = std::find(g_hold_tokens.begin(), g_hold_tokens.end(), token);
    if (it == g_hold_tokens.end()) return KILN_ERR_NOT_FOUND;
    g_hold_tokens.erase(it);
    g_holds.store(static_cast<uint32_t>(g_hold_tokens.size()), std::memory_order_release);
    return KILN_OK;
}

const KilnSteamGuardApi kTable = {{sizeof(KilnSteamGuardApi), KILN_STEAMGUARD_VERSION}, &t_status, &t_latch, &t_hold,
                                  &t_release};

// =============================================================================================== real backend ====
#ifndef KILN_STEAMGUARD_HOST_TEST

const KilnHooksApi* g_hooks = nullptr;
kiln::RawHook g_hook;  // process lifetime; never revoked (the guard has no "off")

void logf(int32_t level, const char* fmt, ...) {
    char buf[320];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    std::string line = "steamguard: ";
    line.append(buf, static_cast<size_t>(n));
    g_api->log(g_owner, level, KilnStr{line.data(), line.size()});
}

bool bk_resolve(const char* name, uintptr_t* out) {
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}

bool bk_read(uintptr_t addr, void* dst, size_t n) {
    return g_hooks && g_hooks->read(addr, dst, n) == KILN_OK;
}

bool bk_install(uintptr_t target) {
    if (!g_hooks) return false;
    if (!g_hook.install(g_api, g_owner, target, reinterpret_cast<void*>(&hk_report), "steamguard/ReportAchievements")) {
        logf(KILN_LOG_WARN, "%s", g_hook.error() ? g_hook.error() : "install failed");
        return false;
    }
    logf(KILN_LOG_INFO, "detour on steamguard/ReportAchievements at 0x%llx", static_cast<unsigned long long>(target));
    return true;
}

bool bk_link_present(uintptr_t) {
    // We never revoke, so our link stays until the process ends. operator bool() is the registry's view of the live link.
    return static_cast<bool>(g_hook);
}

// Asks the hook map whether a foreign hook sits over our detour on the target. Cold path (status), not the detour.
bool bk_foreign_over(uintptr_t target) {
    if (!g_hooks || !g_hooks->map_json) return false;
    size_t len = 0;
    if (g_hooks->map_json(nullptr, 0, &len) != KILN_ERR_BUFFER_TOO_SMALL && len == 0) return false;
    std::string text(len + 1, '\0');
    size_t got = 0;
    if (g_hooks->map_json(text.data(), text.size(), &got) != KILN_OK) return false;
    text.resize(got);
    try {
        const auto doc = nlohmann::json::parse(text, nullptr, false);
        if (!doc.is_object()) return false;
        const uintptr_t base = g_hooks->image_base ? g_hooks->image_base() : 0;
        const uint64_t rva = base && target >= base ? static_cast<uint64_t>(target - base) : 0;
        char want[24];
        std::snprintf(want, sizeof want, "0x%llx", static_cast<unsigned long long>(rva));
        for (const auto& t : doc.value("targets", nlohmann::json::array())) {
            if (t.value("rva", std::string()) == want)
                return !t.value("foreignOver", nlohmann::json()).is_null();
        }
    } catch (...) {
        return false;  // a map we cannot read is not evidence of a foreign hook; link_present and verify still gate us
    }
    return false;
}

uint64_t bk_call_original(void* a, void* b, void* c, void* d) {
    return g_hook.call<uint64_t>(a, b, c, d);
}

bool switched_off() {
    char v[8];
    const DWORD n = GetEnvironmentVariableA("KILN_STEAMGUARD", v, sizeof v);
    return n > 0 && n < sizeof v && std::strcmp(v, "off") == 0;
}

// =============================================================================================== test backend ====
#else

// The host test drives the guard with these stand-ins for kiln.hooks and the game. bk_read reads real test buffers,
// so the entry-byte check and the second-sign check run the same code they run in the game; the stand-in game
// function and the install/link/foreign flags let each refusal path be exercised with no game and no Wine.
using TestGameFn = uint64_t (*)(void*, void*, void*, void*);
int g_test_installs = 0;
bool g_test_install_ok = true;
bool g_test_link_present = true;
bool g_test_foreign_over = false;
uintptr_t g_test_target_addr = 0;
uintptr_t g_test_sign_addr = 0;
TestGameFn g_test_game_fn = nullptr;
int g_test_game_called = 0;
uint64_t g_test_game_ret = 0x7777;

void logf(int32_t level, const char* fmt, ...) {
    char buf[320];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::printf("[log %d] steamguard: %s\n", level, buf);
}

bool bk_resolve(const char* name, uintptr_t* out) {
    if (std::strcmp(name, "steamguard/ReportAchievements") == 0) *out = g_test_target_addr;
    else if (std::strcmp(name, "steamguard/AchievementUnlockText") == 0) *out = g_test_sign_addr;
    else *out = 0;
    return *out != 0;
}

bool bk_read(uintptr_t addr, void* dst, size_t n) {
    if (addr == 0) return false;
    std::memcpy(dst, reinterpret_cast<const void*>(addr), n);
    return true;
}

bool bk_install(uintptr_t) {
    if (!g_test_install_ok) return false;
    ++g_test_installs;
    return true;
}

bool bk_link_present(uintptr_t) { return g_test_link_present; }
bool bk_foreign_over(uintptr_t) { return g_test_foreign_over; }

uint64_t bk_call_original(void* a, void* b, void* c, void* d) {
    ++g_test_game_called;
    return g_test_game_fn ? g_test_game_fn(a, b, c, d) : g_test_game_ret;
}

// Drop every piece of state between test cases.
void test_reset() {
    g_latched.store(false);
    g_holds.store(0);
    g_hold_tokens.clear();
    g_installed.store(false);
    g_failed.store(false);
    g_dropped.store(0);
    g_passed.store(0);
    g_next_token.store(1);
    g_boot_verified = false;
    g_target = 0;
    g_sign = 0;
    std::snprintf(g_reason, sizeof g_reason, "not asked yet");
    g_test_installs = 0;
    g_test_install_ok = true;
    g_test_link_present = true;
    g_test_foreign_over = false;
    g_test_game_called = 0;
    g_test_game_ret = 0x7777;
}

#endif  // !KILN_STEAMGUARD_HOST_TEST

}  // namespace

#ifndef KILN_STEAMGUARD_HOST_TEST
extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side) + sizeof(void*)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_STEAMGUARD_SERVICE, sizeof(KILN_STEAMGUARD_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    if (api->side() != KILN_SIDE_CLIENT) {
        logf(KILN_LOG_INFO, "no Steam user on a dedicated server: the achievement guard is not needed here");
        return KILN_ERR_UNAVAILABLE;
    }
    if (switched_off()) {
        logf(KILN_LOG_INFO, "off by KILN_STEAMGUARD=off: the achievement guard will report not available");
        return KILN_ERR_UNAVAILABLE;
    }
    g_hooks = static_cast<const KilnHooksApi*>(
        api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 2));
    if (!g_hooks || g_hooks->header.version < 2) {
        set_reason("kiln.hooks v2 is not available: the guard cannot hook the achievement report");
        g_failed.store(true, std::memory_order_release);
        logf(KILN_LOG_WARN, "%s", g_reason);
        return api->register_service(owner, KilnStr{KILN_STEAMGUARD_SERVICE, sizeof(KILN_STEAMGUARD_SERVICE) - 1}, &kTable);
    }
    verify_at_boot();
    logf(g_boot_verified ? KILN_LOG_INFO : KILN_LOG_WARN, "%s", g_reason);
    return api->register_service(owner, KilnStr{KILN_STEAMGUARD_SERVICE, sizeof(KILN_STEAMGUARD_SERVICE) - 1}, &kTable);
}
#endif  // !KILN_STEAMGUARD_HOST_TEST
