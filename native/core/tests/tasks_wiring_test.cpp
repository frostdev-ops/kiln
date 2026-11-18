// SPDX-License-Identifier: MIT
// kiln.tasks' lifecycle wiring in the core (src/tick.cpp; the exit call is tested by test-unload-sequence.sh): for the
// server and the client world alike, a new session starts before its STARTED is dispatched, the world's tick hops run
// before its Pre subscribers, a Post edge (KilnTickInfo.step) follows the world's Post, and the session ends before its
// STOPPING is dispatched. Host only: tick.cpp's hooks need the game, so
// it is checked as source, as tick_drain_test does; the behaviour behind the calls is tasks_edges_test's.
//   tasks_wiring_test <tick.cpp>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>

namespace {
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) ++failures;
    std::printf("%s %s\n", ok ? "ok  " : "FAIL", what);
}
std::string body_of(const std::string& src, const char* signature) {
    const size_t fn = src.find(signature);
    if (fn == std::string::npos) return {};
    const size_t end = src.find("\n}\n", fn);
    return end == std::string::npos ? std::string() : src.substr(fn, end - fn);
}
constexpr size_t npos = std::string::npos;
}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: tasks_wiring_test <tick.cpp>\n");
        return 2;
    }
    std::ifstream in(argv[1]);
    std::stringstream ss;
    ss << in.rdbuf();
    const std::string src = ss.str();

    const std::string begin = body_of(src, "void begin_world(uint32_t kind, uintptr_t world) {");
    const size_t started =
        begin.find("if (kind == KILN_WORLD_SERVER || kind == KILN_WORLD_CLIENT) tasks_svc::session_started(kind, session);");
    const size_t dispatch_started = begin.find("dispatch_event(KILN_WORLD_STARTED");
    check(!begin.empty() && started != npos && dispatch_started != npos && started < dispatch_started,
          "begin_world: a server or client world's new session reaches kiln.tasks before its STARTED is dispatched");

    const std::string step = body_of(src, "uintptr_t server_step(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {");
    const size_t post = step.find("run(g_post, g_slots.post, info, te);");
    const size_t edge = step.find("tasks_svc::post_edge(KILN_WORLD_SERVER, g_sessions[KILN_WORLD_SERVER].load(), info.step);");
    const size_t original = step.find("g_hook.call<uintptr_t>(storage, ticked, r8, r9);\n    const double t2");
    check(!step.empty() && post != npos && edge != npos && post < edge && original != npos && original < edge,
          "server_step: the Post edge (the session, KilnTickInfo.step) follows the step and Kiln's Post");
    check(step.find("tasks_svc::post_edge", edge + 1) == npos, "server_step: one edge per step");
    const size_t hops = step.find("tasks_svc::tick_hops(KILN_WORLD_SERVER);");
    const size_t pre = step.find("run(g_pre, g_slots.pre, info, now_ms());");
    check(hops != npos && pre != npos && hops < pre && hops < original,
          "server_step: the managed host's tick hops run before the Pre subscribers");

    // the client world (other_step, kind CLIENT): hops after the posted tasks and before Pre, the edge after its Post
    const std::string other = body_of(src, "uintptr_t other_step(uint32_t kind, uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {");
    const size_t drained = other.find("drain_tasks(kind);");
    const size_t client_hops = other.find("if (kind == KILN_WORLD_CLIENT) tasks_svc::tick_hops(KILN_WORLD_CLIENT);");
    const size_t client_pre = other.find("run_subs(K.pre, K.slots.pre, info, now_ms());");
    const size_t client_original = other.find("g_hook.call<uintptr_t>(storage, ticked, r8, r9);\n    const double t2");
    check(!other.empty() && drained != npos && client_hops != npos && client_pre != npos && drained < client_hops &&
              client_hops < client_pre && client_hops < client_original,
          "other_step: the client world's tick hops run after its posted tasks and before its Pre subscribers");
    const size_t client_post = other.find("if (post_ran) run_subs(K.post, K.slots.post, info, t2);");
    const size_t client_edge = other.find(
        "if (kind == KILN_WORLD_CLIENT) tasks_svc::post_edge(KILN_WORLD_CLIENT, g_sessions[KILN_WORLD_CLIENT].load(), info.step);");
    check(client_post != npos && client_edge != npos && client_post < client_edge && client_original < client_edge &&
              other.find("tasks_svc::post_edge", other.find("tasks_svc::post_edge", client_edge) + 1) == npos,
          "other_step: one client Post edge per step, after its step and its Post subscribers");
    check(other.find("worldinfo::server_world() ? TasksPart::ApplyOnly") != npos,
          "other_step: a hosting client's client line carries only its hop account (the window is on the server line)");

    const std::string destroy = body_of(src, "void __fastcall hk_world_destroy(uintptr_t world) {");
    const size_t stopping = destroy.find(
        "if (kind == KILN_WORLD_SERVER || kind == KILN_WORLD_CLIENT) tasks_svc::session_stopping(kind, session);");
    const size_t prediction = destroy.find("dispatch_event(KILN_WORLD_STOPPING, KILN_WORLD_PREDICTION, prediction, ps);");
    const size_t dispatch_stopping = destroy.find("dispatch_event(KILN_WORLD_STOPPING, kind, world, session);");
    check(!destroy.empty() && stopping != npos && dispatch_stopping != npos && stopping < dispatch_stopping &&
              prediction != npos && prediction < stopping,
          "hk_world_destroy: a server or client session ends in kiln.tasks before its STOPPING is dispatched (the "
          "client world's after its prediction world's)");

    check(src.find("namespace kiln::tasks_svc {") != npos, "tick.cpp declares the kiln.tasks entry points");
    std::printf("tasks wiring: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
