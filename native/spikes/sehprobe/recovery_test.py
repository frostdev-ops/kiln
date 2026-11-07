#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Host tests of the production recovery bodies, with faulting dependencies substituted.

These check the disable/retry policy using C++ throws. Hardware dispatch is tested by probe.cpp under Wine.
Extract the actual functions so this test cannot quietly drift into testing a copy of the implementation.
"""
import os
from pathlib import Path
import subprocess

NATIVE = Path(__file__).resolve().parents[2]
OUT = Path(os.environ.get("KILN_OUT", "/tmp/kiln-review-eha")) / "sehprobe" / "host"
OUT.mkdir(parents=True, exist_ok=True)


def function(file, signature):
    text = (NATIVE / file).read_text()
    start = text.index(signature)
    brace = text.index("{", start)
    depth = 1
    end = brace + 1
    # The selected functions have no brace literals/comments; fail closed if the extraction stops compiling.
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


source = r'''
#include <atomic>
#include <cassert>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>
#define KILN_CALL
#define __fastcall
#define LV_BAD ""
#define KILN_LOG_ERROR 1
using DWORD = unsigned long;
using KilnTickInfo = int;
void fail() { throw std::runtime_error("injected fault"); }
namespace lua_test {
std::atomic<bool> gBroken{false}, gLoaded{true}, gHasDraw{true};
unsigned stops = 0;
bool av = false, cleanup_fault = false;
std::vector<std::string> logs;
void gSink(const char* s) { logs.emplace_back(s); }
void stopHooks(void*) { ++stops; if (cleanup_fault) fail(); }
bool noAccessViolation(void (*f)(uintptr_t), uintptr_t a) { if (av) return false; f(a); return true; }
'''
source += function("plugins/set/src/lua_api.cpp", "void guarded(void (*fn)(uintptr_t)")
source += r'''
void test() {
    guarded([](uintptr_t) { fail(); }, 0, "test");
    assert(gBroken && !gLoaded && !gHasDraw && stops == 1 && logs.size() == 1);
    gBroken = false; gLoaded = gHasDraw = true; stops = 0; logs.clear();
    av = cleanup_fault = true;
    guarded([](uintptr_t) {}, 0, "cleanup fault");
    assert(gBroken && !gLoaded && !gHasDraw && stops == 1 && logs.empty());
}
}
namespace content_test {
std::atomic<uintptr_t> g_early_world{1}, g_early_ireg{99};
std::atomic<uint64_t> g_version{1};
bool g_early_test = true, g_in_update_hook = false;
unsigned g_early_kind = 0, logs = 0, syncs = 0;
struct WorldState { uintptr_t world = 1; uint64_t done = 0; } g_ws[1];
struct Api { uintptr_t world(unsigned) { return 0; } } api, *g_api = &api;
void sync_world(unsigned, uintptr_t) { ++syncs; fail(); }
#define LOGE(...) (++logs)
'''
source += function("services/content/newcontent.cpp", "void early_sync()")
source += r'''
uintptr_t g_itemupdate_orig(uintptr_t, uintptr_t, uintptr_t, uintptr_t) { return 123; }
void early_sync_guarded() { early_sync(); }
'''
source += function("services/content/newcontent.cpp", "uintptr_t __fastcall hk_itemupdate(")
source += r'''
void test() {
    assert(hk_itemupdate(99, 0, 0, 0) == 123);
    assert(hk_itemupdate(99, 0, 0, 0) == 123);
    assert(!g_in_update_hook && g_early_ireg == 0 && logs == 1 && syncs == 1);
}
}
namespace net_test {
std::atomic<bool> g_client_faulted{false};
std::atomic<uint64_t> g_last_client_hook{0}, g_last_client_pre{0};
bool g_client_up = true;
unsigned deliveries = 0, originals = 0, logs = 0;
unsigned flushes = 0;
bool flush_fault = false, g_knocked_ready = false;
std::mutex g_mu;
namespace safetyhook { struct Context { uintptr_t rcx = 1; }; }
struct Counters { std::atomic<unsigned> drains{0}; } C;
struct Hook { template<class T> T call(uintptr_t) { ++originals; } } g_client_recv;
uint64_t now_ms() { return 2000; }
void logf(int, const char*, ...) { ++logs; }
void sink_client(const uint8_t*, uint64_t, void*) {}
namespace knet {
uintptr_t client_socket(uintptr_t, uintptr_t*, uintptr_t*) { return 1; }
int drain(uintptr_t, uintptr_t, void (*)(const uint8_t*, uint64_t, void*), void*) { return 0; }
}
void client_set_up(bool, uintptr_t) {}
void client_deliver() { ++deliveries; fail(); }
void client_knock_on_time() {}
void client_flush(uintptr_t, uintptr_t) { ++flushes; if (flush_fault) fail(); }
void client_queue_knock_locked() {}
'''
source += function("services/net/net.cpp", "void __fastcall hk_client_receive(uintptr_t gc)")
source += function("services/net/net.cpp", "void KILN_CALL on_client_pre(const KilnTickInfo*")
source += function("services/net/net.cpp", "void hk_client_ready(safetyhook::Context& ctx)")
source += r'''
void test() {
    hk_client_receive(1);
    hk_client_receive(1);
    on_client_pre(nullptr, nullptr);
    assert(g_client_faulted && originals == 2 && deliveries == 1 && logs == 1);
    g_client_faulted = false; logs = 0; flush_fault = true;
    safetyhook::Context ctx;
    hk_client_ready(ctx); hk_client_ready(ctx);
    assert(g_client_faulted && flushes == 1 && logs == 1);
}
}
int main() {
    lua_test::test(); content_test::test(); net_test::test();
    puts("SEH recovery policy: Lua, early content, client networking PASS");
}
'''
cpp = OUT / "recovery_test.cpp"
exe = OUT / "recovery_test"
cpp.write_text(source)
subprocess.run(["g++", "-std=c++20", "-O1", "-g", "-Wall", "-Wextra", "-Werror", "-fsanitize=address,undefined",
                str(cpp), "-o", str(exe)], check=True)
env = dict(os.environ)
env.setdefault("ASAN_OPTIONS", "detect_leaks=0")  # LSan cannot run inside the sandbox's ptrace monitor.
subprocess.run([str(exe)], check=True, env=env)
