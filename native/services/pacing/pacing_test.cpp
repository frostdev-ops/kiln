// SPDX-License-Identifier: MIT
// Host test of kiln.pacing's logic: arming, the hooks' register contracts, the caps' merge and the refusal paths,
// with a stand-in for the game's registers and for the hook installer. No game, no Wine.
#define KILN_PACING_HOST_TEST
#include "pacing.cpp"

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    failures += !ok;
}

struct Seen {
    int loop_calls = 0, server_calls = 0;
    KilnPacingLoopWait last{};
    uint64_t server_in = 0, server_out = 0;
    bool server_write = false;
} seen;

void KILN_CALL on_loop(const KilnPacingLoopWait* w, void* user) {
    ++static_cast<Seen*>(user)->loop_calls;
    static_cast<Seen*>(user)->last = *w;
}
void KILN_CALL on_server(uint64_t* wait, void* user) {
    auto* s = static_cast<Seen*>(user);
    ++s->server_calls;
    s->server_in = *wait;
    if (s->server_write) *wait = s->server_out;
}

void reset(bool server) {
    for (auto& s : g_slot) s = Slot{};
    g_server = server;
    g_armed = 0;
    g_cap_default = g_cap_background = 0;
    g_tasks_started = false;
    g_loop_calls = g_server_calls = 0;
    g_test_refuse = false;
    g_test_installs = 0;
    seen = Seen{};
    t_set_receiver(&on_loop, &on_server, &seen);
}

void find_all() {
    uintptr_t at = 0x1000;
    for (auto& s : g_slot) {
        s.found = true;
        s.at = at += 0x100;
    }
}
}  // namespace

int main() {
    // Kiln.Api's mirror (Services/PacingNative.cs) checks the same sizes.
    static_assert(sizeof(KilnPacingLoopWait) == 24 && sizeof(KilnPacingStatus) == 48 && sizeof(KilnPacingApi) == 48);

    // Nothing found: nothing arms, nothing is promised.
    reset(true);
    check(t_sites() == 0, "a build with no place reports no site");
    check(t_arm(KILN_PACING_LOOP_WAIT | KILN_PACING_SERVER_WAIT) == 0 && g_test_installs == 0, "arming a place the build lacks installs nothing");
    check(t_cap_task_workers(4, 4) == KILN_ERR_UNAVAILABLE, "caps without the places are unavailable");

    // Found: no hook is in until it is asked for.
    reset(true);
    find_all();
    check(t_sites() == (KILN_PACING_LOOP_WAIT | KILN_PACING_MESSAGE_WAIT | KILN_PACING_SERVER_WAIT | KILN_PACING_TASK_WORKERS), "all places found on a server");
    check(g_test_installs == 0, "no hook goes in before something asks");
    hk_loop_wait(*std::make_unique<safetyhook::Context>());
    check(seen.loop_calls == 0, "a hook that is not armed does not call the receiver");

    // Loop wait: ms from r9d, the time left from rbx; the client's message wait reads r8d.
    check(t_arm(KILN_PACING_LOOP_WAIT) == KILN_PACING_LOOP_WAIT && g_test_installs == 1, "arming the loop wait installs one hook");
    safetyhook::Context c;
    c.r9 = 0xFFFFFFFF00000028ull;  // the upper half of r9 is not the wait
    c.r8 = 7;
    c.rbx = 39'900'000;
    hk_loop_wait(c);
    check(seen.loop_calls == 1 && seen.last.ms == 40 && seen.last.left_ns == 39'900'000 && seen.last.kind == KILN_PACING_KIND_HANDLES && seen.last.size == sizeof(KilnPacingLoopWait),
          "the loop wait hands over r9d and rbx");
    c.rbx = static_cast<uintptr_t>(-5);
    hk_loop_wait(c);
    check(seen.last.left_ns == -5, "a deadline that has passed is a negative time left");
    hk_message_wait(c);
    check(seen.loop_calls == 2, "the message wait is not armed yet: its hook does not call");
    check(t_arm(KILN_PACING_LOOP_WAIT | KILN_PACING_MESSAGE_WAIT) == (KILN_PACING_LOOP_WAIT | KILN_PACING_MESSAGE_WAIT) && g_test_installs == 2, "arming the message wait adds a hook, the first stays");
    hk_message_wait(c);
    check(seen.loop_calls == 3 && seen.last.ms == 7 && seen.last.kind == KILN_PACING_KIND_MESSAGES, "the message wait reads r8d");
    check(t_arm(KILN_PACING_LOOP_WAIT | KILN_PACING_MESSAGE_WAIT) != 0 && g_test_installs == 2, "arming again installs nothing new");
    check(t_arm(0) == 0, "disarming returns no site");
    hk_loop_wait(c);
    check(seen.loop_calls == 3, "disarmed: the hook (still in) calls nothing");
    check(g_slot[kLoop].hooked, "the hook stays in once armed");

    // Server wait: the receiver may rewrite r9; what it writes is clamped.
    check(t_arm(KILN_PACING_SERVER_WAIT) == KILN_PACING_SERVER_WAIT, "arming the server wait");
    safetyhook::Context s;
    s.r9 = 1'000'000;
    hk_server_wait(s);
    check(seen.server_calls == 1 && seen.server_in == 1'000'000 && s.r9 == 1'000'000, "a receiver that leaves the wait leaves r9 alone");
    seen.server_write = true;
    seen.server_out = 2'000'000;
    hk_server_wait(s);
    check(s.r9 == 2'000'000, "a receiver that doubles the wait doubles r9");
    seen.server_out = ~0ull;  // -1 as the game's signed ns
    hk_server_wait(s);
    check(s.r9 == KILN_PACING_MAX_WAIT_NS, "a wild wait is clamped to the maximum");
    seen.server_out = 0;
    hk_server_wait(s);
    check(s.r9 == 0, "zero is allowed (poll)");
    t_arm(0);
    s.r9 = 40'000'000;
    seen.server_out = 5;
    hk_server_wait(s);
    check(s.r9 == 40'000'000 && seen.server_calls == 4, "disarmed: r9 untouched");

    // A refused hook drops the place: found shrinks, nothing is delivered.
    reset(true);
    find_all();
    g_test_refuse = true;
    check(t_arm(KILN_PACING_LOOP_WAIT | KILN_PACING_SERVER_WAIT) == 0, "a refused hook arms nothing");
    check((t_sites() & (KILN_PACING_LOOP_WAIT | KILN_PACING_SERVER_WAIT)) == 0 && (t_sites() & KILN_PACING_MESSAGE_WAIT) != 0, "the refused places leave the found set, the others stay");
    KilnPacingStatus st{};
    st.size = sizeof st;
    check(t_status(&st) == KILN_OK && (st.refused & KILN_PACING_LOOP_WAIT) && (st.refused & KILN_PACING_SERVER_WAIT) && st.hooked == 0, "status names what was refused");
    g_test_refuse = false;
    check(t_arm(KILN_PACING_LOOP_WAIT) == 0, "a refused place is not retried");

    // Task workers: the lowest cap wins, a cap only lowers the game's own limit, both groups are separate.
    reset(true);
    find_all();
    check(t_cap_task_workers(0, 0) == KILN_ERR_INVALID_ARGUMENT, "two zero caps are no request");
    check(t_cap_task_workers(4, 0) == KILN_OK && g_cap_default == 4 && g_cap_background == 0 && g_test_installs == 1, "a default cap installs the default hook only");
    check(t_cap_task_workers(6, 2) == KILN_OK && g_cap_default == 4 && g_cap_background == 2 && g_test_installs == 2, "the lowest cap wins per group");
    check(t_cap_task_workers(3, 9) == KILN_OK && g_cap_default == 3 && g_cap_background == 2, "a lower cap replaces a higher one, never the reverse");
    check(t_cap_task_workers(1000, 1000) == KILN_OK && g_cap_default == 3 && g_cap_background == 2, "a huge cap is no change");
    safetyhook::Context w;
    w.rax = 10;
    hk_default_workers(w);
    check(w.rax == 3, "the hook lowers the default group's limit (10 -> 3)");
    w.rax = 2;
    hk_default_workers(w);
    check(w.rax == 2, "a limit already below the cap stays");
    w.rax = 8;
    hk_background_workers(w);
    check(w.rax == 2, "the background group has its own cap (8 -> 2)");
    check(g_tasks_started, "the pools' start is seen when the hook runs");
    check(t_cap_task_workers(1, 1) == KILN_ERR_UNAVAILABLE, "after the pools started a cap is refused");
    check(t_status(&st) == KILN_OK && st.task_system_started == 1 && st.default_cap == 3 && st.background_cap == 2, "status shows the caps and the start");

    // A cap that lowers nothing in a group the hook never saw is a no-op for that group.
    reset(true);
    find_all();
    t_cap_task_workers(0, 5);
    w.rax = 12;
    hk_default_workers(w);
    check(w.rax == 12, "a group with no cap is left to the game");

    // A client has no worker caps, whatever sigdb resolved.
    reset(false);
    find_all();
    check((t_sites() & KILN_PACING_TASK_WORKERS) == 0 && (t_sites() & KILN_PACING_MESSAGE_WAIT) != 0, "a client has the message wait and no worker caps");
    check(t_cap_task_workers(4, 4) == KILN_ERR_UNAVAILABLE, "worker caps are refused on a client");

    // Only the worker places found on a server means the caps need both groups.
    reset(true);
    g_slot[kDefaultWorkers].found = true;
    g_slot[kDefaultWorkers].at = 0x2000;
    check((t_sites() & KILN_PACING_TASK_WORKERS) == 0, "one worker place alone is no cap");

    std::printf("%d failure(s)\n", failures);
    return failures ? 1 : 0;
}
