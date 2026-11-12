// SPDX-License-Identifier: MIT
// Host test of kiln.steamguard's logic: boot verification, the detour's drop/pass, the latch and holds, and every
// refusal path, with a stand-in for the game's achievement function and for the hook registry. No game, no Wine.
#define KILN_STEAMGUARD_HOST_TEST
#include "steamguard.cpp"

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    failures += !ok;
}

KilnStr s(const char* z) { return KilnStr{z, std::strlen(z)}; }

// A valid target image and second-sign string, read by the guard's own bk_read.
uint8_t g_target_buf[sizeof kEntry];
char g_sign_buf[] = "[online] Achievement '%k' couldn't be unlocked.";
// A stand-in operation batch {array ptr, count}; the first operation's id is named in the drop log.
uint32_t g_op_ids[1] = {0x723a87fau};
struct Batch {
    uintptr_t base;
    uint64_t count;
} g_batch;

int g_game_called_marker = 0;
uint64_t KILN_CALL fake_game(void*, void*, void*, void*) {
    ++g_game_called_marker;
    return 0x55;  // a value that is NOT the drop value, so "return is the decided one" can fail if the drop leaks it
}

void boot_valid() {
    test_reset();
    std::memcpy(g_target_buf, kEntry, sizeof kEntry);
    for (size_t i = 0; i < sizeof kEntry; ++i)
        if (!kEntryMask[i]) g_target_buf[i] = 0xAB;  // wildcard bytes: anything
    g_test_target_addr = reinterpret_cast<uintptr_t>(g_target_buf);
    g_test_sign_addr = reinterpret_cast<uintptr_t>(g_sign_buf);
    g_test_game_fn = &fake_game;
    g_game_called_marker = 0;
    g_batch = {reinterpret_cast<uintptr_t>(g_op_ids), 1};
    verify_at_boot();
}

KilnSteamGuardStatus status() {
    KilnSteamGuardStatus st{};
    st.size = sizeof st;
    t_status(&st);
    return st;
}
}  // namespace

int main() {
    static_assert(sizeof(KilnSteamGuardStatus) == 32 + KILN_STEAMGUARD_REASON_MAX);

    // Boot verification passes on a valid target, and the hook is not in until something asks.
    boot_valid();
    check(g_boot_verified, "boot: a valid target verifies");
    check(status().flags & KILN_STEAMGUARD_TARGET_VERIFIED, "boot: TargetVerified is set before anyone asks");
    check(!(status().flags & KILN_STEAMGUARD_HOOK_INSTALLED), "boot: the hook is not installed yet");
    check(g_test_installs == 0, "boot: nothing is installed before a latch or hold");

    // Off (installed, not active): the stand-in is called and Passed goes up; Dropped does not.
    boot_valid();
    check(t_latch(s("prove the guard")) == KILN_OK, "latch on a verified target returns OK");
    check(g_test_installs == 1, "the first latch installs the detour");
    g_game_called_marker = 0;
    // Turn the guard off to see pass-through (no public "off"; the test clears the latch directly).
    g_latched.store(false);
    const uint64_t r_off = hk_report(nullptr, nullptr, &g_batch, nullptr);
    check(g_game_called_marker == 1 && r_off == 0x55, "off: the game function is called and its value returned");
    check(g_passed.load() == 1 && g_dropped.load() == 0, "off: Passed goes up, Dropped does not");

    // On: the stand-in is NOT called, Dropped goes up, the return value is the decided drop value.
    boot_valid();
    check(t_latch(s("prove the guard")) == KILN_OK, "latch again");
    g_game_called_marker = 0;
    const uint64_t r_on = hk_report(nullptr, nullptr, &g_batch, nullptr);
    check(g_game_called_marker == 0, "on: the game function is not called");
    check(g_dropped.load() == 1, "on: Dropped goes up");
    check(r_on == kDropReturn, "on: the dropped call returns the decided value");

    // Two holds need two releases; the guard stays on until the second release.
    boot_valid();
    uint64_t t1 = 0, t2 = 0;
    check(t_hold(s("a"), &t1) == KILN_OK && t1 != 0, "first hold returns a token and protects");
    check(t_hold(s("b"), &t2) == KILN_OK && t2 != 0 && t2 != t1, "second hold returns a second token");
    check(g_holds.load() == 2 && guard_active(), "two holds: active, two live");
    check(t_release(t1) == KILN_OK && guard_active(), "one release: still active");
    check(t_release(t1) == KILN_ERR_NOT_FOUND, "a replayed release is NOT_FOUND and changes nothing");
    check(g_holds.load() == 1 && guard_active(), "after the replay: still one hold, still active");
    check(t_release(t2) == KILN_OK && !guard_active(), "second release: no longer active");

    // A latch survives releases: a hold's release does not switch a latch off.
    boot_valid();
    uint64_t th = 0;
    check(t_latch(s("latch")) == KILN_OK, "latch");
    check(t_hold(s("hold"), &th) == KILN_OK, "hold on top of the latch");
    check(t_release(th) == KILN_ERR_NOT_FOUND ? false : true, "release the hold");
    check(g_holds.load() == 0 && guard_active(), "the latch keeps the guard on after every hold is released");

    // A target that fails verification: Latch and TryHold return false, nothing is installed.
    boot_valid();
    g_target_buf[5] = 0x00;  // break a checked entry byte (was 0x48)
    g_boot_verified = false;
    g_failed.store(false);
    verify_at_boot();
    check(!g_boot_verified && (status().flags & KILN_STEAMGUARD_FAILED), "a wrong target does not verify (fail closed)");
    check(t_latch(s("x")) == KILN_ERR_UNAVAILABLE, "latch refuses an unverified target");
    uint64_t tv = 0;
    check(t_hold(s("x"), &tv) == KILN_ERR_UNAVAILABLE && tv == 0, "hold refuses an unverified target");
    check(g_test_installs == 0 && !(status().flags & KILN_STEAMGUARD_HOOK_INSTALLED), "nothing is installed for an unverified target");

    // An install that fails: not protecting, with the reason.
    boot_valid();
    g_test_install_ok = false;
    check(t_latch(s("x")) == KILN_ERR_UNAVAILABLE, "latch fails when the install fails");
    check(g_failed.load() && std::strstr(g_reason, "install"), "the reason names the failed install");
    check(!(status().flags & KILN_STEAMGUARD_HOOK_INSTALLED), "not installed when the install failed");

    // A foreign hook over the detour: not protecting, with the reason.
    boot_valid();
    g_test_foreign_over = true;
    uint64_t tf = 0;
    check(t_hold(s("x"), &tf) == KILN_ERR_UNAVAILABLE && tf == 0, "hold fails when a foreign hook is over the detour");
    check(g_failed.load() && std::strstr(g_reason, "hooked over"), "the reason names the foreign hook");
    check(!protecting_now(), "not protecting with a foreign hook over the detour");

    // A status struct with a smaller size is filled only that far.
    boot_valid();
    (void)t_latch(s("fill"));
    g_dropped.store(0);
    (void)hk_report(nullptr, nullptr, &g_batch, nullptr);  // Dropped = 1
    KilnSteamGuardStatus small{};
    small.size = offsetof(KilnSteamGuardStatus, dropped);  // covers flags, holds, reserved; not dropped/passed/reason
    small.dropped = 0xDEAD;
    small.passed = 0xBEEF;
    check(t_status(&small) == KILN_OK, "a smaller status is accepted");
    check(small.flags & KILN_STEAMGUARD_ACTIVE, "the smaller status filled flags");
    check(small.dropped == 0xDEAD && small.passed == 0xBEEF, "the smaller status left dropped/passed untouched");
    KilnSteamGuardStatus full = status();
    check(full.dropped == 1, "a full status reports Dropped");
    check(std::strlen(full.reason) > 0, "a full status carries a reason");

    // A status too small even for flags is refused.
    KilnSteamGuardStatus tiny{};
    tiny.size = 4;  // only `size`
    check(t_status(&tiny) == KILN_ERR_INVALID_ARGUMENT, "a status too small for flags is refused");

    std::printf("\n%s (%d failure%s)\n", failures ? "FAILED" : "ok", failures, failures == 1 ? "" : "s");
    return failures ? 1 : 0;
}
