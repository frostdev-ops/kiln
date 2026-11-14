// SPDX-License-Identifier: MIT
// Host test of the owner-retiring observers of the owner registry (src/registry.cpp; kiln.hooks
// subscribe_owner_retiring, dev.28). Run by test-owner-retiring.sh (ASan/UBSan, TSan-free: the lock check is a
// try_lock from inside the callback). The fault guard and the log are stubs.
#include <windows.h>  // tests/stubs/windows.h

#include <cstdarg>
#include <cstdio>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include "../src/registry.cpp"

namespace {
int g_fail = 0, g_pass = 0;
std::vector<std::string> g_errors;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    ok ? ++g_pass : ++g_fail;
}
}  // namespace

namespace kiln {
namespace log {
void writef(int level, const char*, const char* fmt, ...) {
    if (level != KILN_LOG_ERROR) return;
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    g_errors.push_back(buf);
}
}  // namespace log
namespace guard {
bool task(KilnTaskFn fn, void* user, unsigned long*) {
    fn(user);
    return true;
}
}  // namespace guard
namespace hooks::seh {
// The product guard is a structured-exception handler; here a C++ exception stands for the fault.
bool call_owner_retiring(void(KILN_CALL* fn)(KilnOwner, void*), KilnOwner retiring, void* user, unsigned long* code) {
    try {
        fn(retiring, user);
        return true;
    } catch (...) {
        *code = 0xC0000005;
        return false;
    }
}
}  // namespace hooks::seh
}  // namespace kiln

namespace reg = kiln::reg;

namespace {
struct Seen {
    std::vector<KilnOwner> owners;
    bool lock_free = true;      // the registry's locks were free inside every call
    bool owner_dead = true;     // the retiring owner was already dead inside every call
    std::thread::id thread;
    KilnHandle unsubscribe = 0; // unsubscribe this handle from inside the callback
    bool do_throw = false;
};
void KILN_CALL on_retiring(KilnOwner retiring, void* user) {
    auto* s = static_cast<Seen*>(user);
    s->owners.push_back(retiring);
    s->thread = std::this_thread::get_id();
    if (reg::g_mutex.try_lock()) reg::g_mutex.unlock();
    else s->lock_free = false;
    if (reg::g_task_mutex.try_lock()) reg::g_task_mutex.unlock();
    else s->lock_free = false;
    if (reg::owner_alive(retiring)) s->owner_dead = false;
    if (s->unsubscribe) reg::remove_owner_retiring(s->unsubscribe);
    if (s->do_throw) throw std::runtime_error("observer fault");
}
void KILN_CALL tick_fn(const KilnTickInfo*, void*) {}
}  // namespace

int main() {
    const KilnOwner service = reg::owner_create("service:kiln-camera.dll");
    const KilnOwner mod_a = reg::owner_create("mod:a"), mod_b = reg::owner_create("mod:b");
    Seen seen;
    KilnHandle h = 0;
    check(reg::add_owner_retiring(service, &on_retiring, &seen, &h) == KILN_OK && h != 0, "an observer subscribes");
    check(reg::add_owner_retiring(service, nullptr, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT, "no callback is refused");
    check(reg::add_owner_retiring(9999, &on_retiring, &seen, nullptr) == KILN_ERR_REVOKED, "a dead observer owner is refused");

    KilnHandle tick = 0;
    reg::add_tick(mod_a, KILN_TICK_PRE, &tick_fn, nullptr, &tick);
    reg::owner_revoke(mod_a);
    check(seen.owners == std::vector<KilnOwner>{mod_a}, "a plain revoke calls the observer once with the retiring owner");
    check(seen.lock_free, "the call runs with no registry lock held");
    check(seen.owner_dead && reg::tick_sub_count(KILN_TICK_PRE) == 0,
          "the owner is already dead and its own subscriptions are gone when the observer runs");
    check(seen.thread == std::this_thread::get_id(), "the call runs on the revoking thread");
    reg::owner_revoke(mod_a);
    check(seen.owners.size() == 1, "revoking the same owner again tells nobody");
    reg::owner_revoke(0);
    reg::owner_revoke(424242);
    check(seen.owners.size() == 1, "owner 0 and an owner that never existed tell nobody");

    // the load-failure path: an owner created and revoked before it registered anything
    const KilnOwner failed = reg::owner_create("plugin:failed.dll");
    reg::owner_revoke(failed);
    check(seen.owners.size() == 2 && seen.owners.back() == failed, "an owner revoked right after its creation is reported too");

    // two observers; the first unsubscribes the second from inside its callback
    Seen second;
    KilnHandle h2 = 0;
    reg::add_owner_retiring(0, &on_retiring, &second, &h2);  // the core itself may observe
    seen.unsubscribe = h2;
    reg::owner_revoke(mod_b);
    check(seen.owners.back() == mod_b && second.owners.empty() && reg::owner_retiring_count() == 1,
          "an observer unsubscribed by an earlier callback of the same revoke is not called");
    seen.unsubscribe = 0;
    check(reg::remove_owner_retiring(h2) == KILN_ERR_NOT_FOUND, "its handle is gone");

    // an observer unsubscribes itself inside its callback
    Seen self;
    KilnHandle hs = 0;
    reg::add_owner_retiring(service, &on_retiring, &self, &hs);
    self.unsubscribe = hs;
    const KilnOwner mod_c = reg::owner_create("mod:c"), mod_d = reg::owner_create("mod:d");
    reg::owner_revoke(mod_c);
    reg::owner_revoke(mod_d);
    check(self.owners == std::vector<KilnOwner>{mod_c} && seen.owners.back() == mod_d,
          "unsubscribing inside the callback is safe and stops later calls");

    // a faulting observer is removed; the others still run
    Seen bad;
    bad.do_throw = true;
    KilnHandle hb = 0;
    reg::add_owner_retiring(service, &on_retiring, &bad, &hb);
    Seen after;
    reg::add_owner_retiring(service, &on_retiring, &after, nullptr);
    const KilnOwner mod_e = reg::owner_create("mod:e"), mod_f = reg::owner_create("mod:f");
    reg::owner_revoke(mod_e);
    reg::owner_revoke(mod_f);
    check(bad.owners.size() == 1 && after.owners.size() == 2 && g_errors.size() == 1 &&
              g_errors[0].find("faulted") != std::string::npos,
          "an observer that faults is removed and logged; later observers and later revokes go on");

    // the observer's own owner: its subscriptions go with it, and it is not told about itself
    const size_t before = seen.owners.size();
    reg::owner_revoke(service);
    check(seen.owners.size() == before && after.owners.size() == 2 && reg::owner_retiring_count() == 0,
          "an observer is not called for its own owner's revoke, and its subscriptions go with it");
    const KilnOwner mod_g = reg::owner_create("mod:g");
    reg::owner_revoke(mod_g);
    check(seen.owners.size() == before, "nothing is called after the observer's owner is gone");

    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
