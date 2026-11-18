// SPDX-License-Identifier: MIT
// Real producer/broker paths with locks held permanently by another thread.
#define main existing_shutdown_tests
#include "shutdown_test.cpp"
#undef main
#include <future>
#include <chrono>
#include <cstdlib>
static std::atomic<bool> stopped{false};
template<class M> void poison(M& m) {
    auto ready = new std::promise<void>;
    auto never = new std::promise<void>;
    auto done = ready->get_future();
    std::thread([&m, ready, never] {
        m.lock();
        ready->set_value();
        never->get_future().wait(); // deliberately never unlocked, even after the assertion
    }).detach();
    done.wait();
}
template<class F> void returns(F fn) {
    auto done = new std::promise<bool>;
    auto result = done->get_future();
    std::thread worker([fn, done] { done->set_value(fn()); });
    if (result.wait_for(std::chrono::seconds(1)) != std::future_status::ready) {
        std::puts("FAIL: in-flight visit did not exit"); std::fflush(stdout); std::_Exit(1);
    }
    CHECK(result.get());
    worker.join();
}
struct InterestDeath : DeathPorts {
    BoundedMutex* interest;
    bool interested(uint32_t) const { std::lock_guard lock(*interest); return true; }
};
struct MetadataPickup : PickupPorts {
    BoundedMutex* metadata;
    bool row(uint32_t x, uint64_t ep, Row* out) const { std::lock_guard lock(*metadata); return PickupPorts::row(x,ep,out); }
};
int main() {
    lock_stopped.store(+[] { return stopped.load(); });
    // Never free these objects: the poisoned locks remain owned until process exit.
    auto b = new Broker; b->attach(9);
    auto h = new death::Holds; auto x = new pickup::Exposures; auto life = new Shutdown;
    // Keep the actual broker mutex owned, not a substitute lock.
    auto ready = new std::promise<void>; auto never = new std::promise<void>;
    auto locked = ready->get_future();
    std::thread([=] { auto lock = b->lock_for_tests(); ready->set_value(); never->get_future().wait(); }).detach();
    locked.wait();
    returns([=] { DeathPorts p{*life}; return h->visit(p,*b) == death::Visit::Pass; });
    returns([=] { PickupPorts p{*life}; return x->visit(p,*b) == pickup::Visit::Pass; });
    CHECK(h->size() == 0 && x->size() == 0);
    auto free_b = new Broker; free_b->attach(9);
    auto ph = new death::Holds; auto px = new pickup::Exposures;
    auto hr = new std::promise<void>; auto hn = new std::promise<void>; auto hf=hr->get_future();
    std::thread([=] { auto lock=ph->lock_for_tests(); hr->set_value(); hn->get_future().wait(); }).detach(); hf.wait();
    auto xr = new std::promise<void>; auto xn = new std::promise<void>; auto xf=xr->get_future();
    std::thread([=] { auto lock=px->lock_for_tests(); xr->set_value(); xn->get_future().wait(); }).detach(); xf.wait();
    returns([=] { DeathPorts p{*life}; return ph->visit(p,*free_b) == death::Visit::Pass; });
    returns([=] { PickupPorts p{*life}; return px->visit(p,*free_b) == pickup::Visit::Pass; });
    auto interest=new BoundedMutex; auto metadata=new BoundedMutex; poison(*interest); poison(*metadata);
    returns([=] { InterestDeath p{DeathPorts{*life},interest}; return h->visit(p,*free_b)==death::Visit::Pass; });
    returns([=] { MetadataPickup p{PickupPorts{*life},metadata}; return x->visit(p,*free_b)==pickup::Visit::Pass; });
    // Exercise a hook already contending BEFORE shutdown. The callback is reached only after try_lock failed;
    // the test barrier makes that ordering deterministic, without releasing the poisoned mutex.
    static std::atomic<bool> contended{false}, release_check{false};
    lock_stopped.store(+[] {
        contended.store(true);
        while (!release_check.load()) std::this_thread::yield();
        return stopped.load();
    });
    auto before_stop = [&](auto visit) {
        contended.store(false); release_check.store(false); stopped.store(false);
        auto result = std::async(std::launch::async, visit);
        while (!contended.load()) std::this_thread::yield();
        stopped.store(true); life->stop("another thread faulted while holding the lock");
        release_check.store(true);
        if (result.wait_for(std::chrono::seconds(1)) != std::future_status::ready) {
            std::puts("FAIL: pre-existing waiter survived stop"); std::fflush(stdout); std::_Exit(1);
        }
        CHECK(result.get());
    };
    // Fresh lifetime for each hook so armed() succeeded before the contention.
    before_stop([=] { DeathPorts p{*life}; return h->visit(p,*b)==death::Visit::Pass && h->size()==0; });
    life = new Shutdown;
    before_stop([=] { PickupPorts p{*life}; return px->visit(p,*free_b)==pickup::Visit::Pass; });
    life = new Shutdown;
    before_stop([=] { InterestDeath p{DeathPorts{*life},interest}; return h->visit(p,*free_b)==death::Visit::Pass && h->size()==0; });
    CHECK(free_b->active_count()==0 && lock_bypasses.load()>=7);
    lock_stopped.store(nullptr);
    std::printf("waiter_test: %d PASS, %d FAIL\n",g_pass,g_fail);
    // The objects above are never freed and a detached thread still holds the broker's lock: end the process here, as
    // the comment at the top says, without exit's handlers (LeakSanitizer would report them and drop this output).
    std::fflush(stdout);
    std::_Exit(g_fail?1:0);
}
