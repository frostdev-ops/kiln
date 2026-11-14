// SPDX-License-Identifier: MIT
#include "system_hooks.h"
#include <array>
#include <atomic>
#include <future>
#include <memory>
#include <thread>
#include <cstdio>
using namespace ktypes;
struct Counts { std::atomic<int> before{0},after{0}; };
void KILN_CALL before(void*,void* user) { ++static_cast<Counts*>(user)->before; }
void KILN_CALL after(void*,void* user) { ++static_cast<Counts*>(user)->after; }
int main() {
    int checks=0;
    auto check=[&](bool ok) {++checks;if (!ok) {std::fprintf(stderr,"FAIL system hooks %d\n",checks);std::exit(1);} };
    auto guard=[](KilnSystemHookFn fn,void* ctx,void* user,KilnHandle) {fn(ctx,user);return true;};
    SystemHookCallbacks callbacks;
    auto old=std::make_unique<Counts>();
    Counts next;
    std::atomic<int> originals{0};
    check(callbacks.bind(1,11,&before,&after,old.get()));
    std::promise<void> entered,release;
    auto original_running=entered.get_future();
    auto continue_original=release.get_future().share();
    std::thread client_world([&] {
        run_system_update(callbacks,nullptr,[&](void*) { ++originals;entered.set_value();continue_original.wait(); },guard);
    });
    original_running.wait(); // the first world finished its before callback but remains in original game code
    check(old->before==1);
    check(callbacks.retire(11)); // must not wait for that unrelated original function
    old.reset(); // ASan catches any stale after callback using the old owner's released memory
    check(callbacks.bind(2,12,&before,&after,&next));
    run_system_update(callbacks,nullptr,[&](void*) {++originals;},guard); // a second concurrent world
    check(next.before==1 && next.after==1);
    check(!callbacks.retire(11)); // a stale handle cannot remove the new owner
    release.set_value();client_world.join();
    check(originals==2 && next.before==1 && next.after==1); // old world must not call new owner's after
    check(callbacks.retire(12));
    run_system_update(callbacks,nullptr,[&](void*) {++originals;},guard);
    check(originals==3 && next.before==1 && next.after==1); // retained passive detour still forwards originals
    check(callbacks.bind(3,13,&before,&after,&next));
    auto fault=[](KilnSystemHookFn fn,void* ctx,void* user,KilnHandle) {fn(ctx,user);return false;};
    run_system_update(callbacks,nullptr,[&](void*) {++originals;},fault);
    run_system_update(callbacks,nullptr,[&](void*) {++originals;},guard);
    check(originals==5 && next.before==2 && next.after==1); // fault disables callbacks, never the original
    check(!callbacks.bind(4,14,&before,&after,&next));
    check(callbacks.retire(13) && callbacks.bind(4,14,&before,&after,&next));
    std::array<SystemHookSlot,64> slots{};
    auto lookup=[&](size_t i) {return slots[i];};
    for (size_t i=0;i<slots.size();++i) {
        check(system_hook_slot<64>(i+1,lookup)==static_cast<int>(i));
        slots[i]={i+1,true};
    }
    check(system_hook_slot<64>(1,lookup)==-2);
    check(system_hook_slot<64>(65,lookup)==-1);
    slots[31].claimed=false;
    check(system_hook_slot<64>(32,lookup)==31); // reuse the retained update function only
    check(system_hook_slot<64>(65,lookup)==-1); // distinct-function memory remains bounded even after retirement
    // A wrapped program keeps its name: kiln.world's wrap_original gives the game's update behind its trampoline.
    static uint64_t wrapped_entry=0x5000,game_update=0x1400a000,trampoline=0x7ff00010;
    auto original=[](uintptr_t entry) -> uint64_t { return entry==wrapped_entry?game_update:0; };
    check(program_update(wrapped_entry,trampoline,original)==game_update); // swapped: the game's update
    check(program_update(0x6000,0x1400b000,original)==0x1400b000);       // not swapped: what the entry holds
    check(program_update(wrapped_entry,trampoline,nullptr)==trampoline);  // no kiln.world v4: as before
    check(external_state(false,false)==0 && external_state(true,false)==1 && external_state(true,true)==2 &&
          external_state(false,true)==0);
    std::printf("system hook lifetime: PASS %d checks (concurrent worlds, original-running retire/rebind, stale handles, 64-slot bound, wrapped-program identity, external runner)\n",checks);
}
