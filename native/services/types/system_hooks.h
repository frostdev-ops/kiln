// SPDX-License-Identifier: MIT
// Callback ownership is independent of the process-lifetime original trampoline. A world may still be in the
// original update while another world's tick retires/rebinds the callbacks; its old after callback must stay gone.
#pragma once
#include <cstdint>
#include <mutex>
#include "../../core/abi/services/kiln_types.h"
namespace ktypes {
struct SystemHookIdentity { KilnOwner owner; KilnHandle handle; };
class SystemHookCallbacks {
    mutable std::recursive_mutex mutex_;
    KilnOwner owner_ = 0;
    KilnHandle handle_ = 0;
    uint64_t generation_ = 0;
    KilnSystemHookFn before_ = nullptr, after_ = nullptr;
    void* user_ = nullptr;
    bool active_ = false;
public:
    SystemHookIdentity identity() const { std::lock_guard lock(mutex_); return {owner_,handle_}; }
    bool bind(KilnOwner owner, KilnHandle handle, KilnSystemHookFn before, KilnSystemHookFn after, void* user) {
        std::lock_guard lock(mutex_);
        if (handle_) return false;
        owner_=owner; handle_=handle; before_=before; after_=after; user_=user; active_=true; ++generation_;
        return true;
    }
    // Drains user callbacks, but never waits for unrelated original game code or frees its trampoline.
    bool retire(KilnHandle handle) {
        std::lock_guard lock(mutex_);
        if (!handle || handle_!=handle) return false;
        owner_=0; handle_=0; before_=nullptr; after_=nullptr; user_=nullptr; active_=false; ++generation_;
        return true;
    }
    template<class Guard> uint64_t before(void* ctx, Guard& guard) {
        std::lock_guard lock(mutex_);
        const uint64_t generation=generation_;
        if (active_ && before_ && !guard(before_,ctx,user_,handle_)) active_=false;
        return generation;
    }
    template<class Guard> void after(void* ctx, uint64_t generation, Guard& guard) {
        std::lock_guard lock(mutex_);
        if (generation==generation_ && active_ && after_ && !guard(after_,ctx,user_,handle_)) active_=false;
    }
};
// The calling thread runs a system_hook callback (the table's in_system_callback). The guard around each callback sets
// it and puts the outer value back, also after a fault; plain functions, so a guard with SEH holds no C++ destructor.
inline thread_local bool t_system_callback = false;
inline bool enter_system_callback() { const bool previous = t_system_callback; t_system_callback = true; return previous; }
inline void leave_system_callback(bool previous) { t_system_callback = previous; }
inline uint32_t KILN_CALL in_system_callback() { return t_system_callback ? 1u : 0u; }
template<class Original,class Guard>
void run_system_update(SystemHookCallbacks& callbacks,void* ctx,Original original,Guard guard) {
    const uint64_t generation=callbacks.before(ctx,guard);
    original(ctx);
    callbacks.after(ctx,generation,guard);
}
// The update that names a program for the switches and the profiler: what the table entry holds (entry+0x28), or,
// when kiln.world's wrap trampoline sits there, the game's update behind it (kiln.world's wrap_original answers it,
// 0 for an entry it did not swap).
inline uint64_t program_update(uint64_t entry, uint64_t current, uint64_t (KILN_CALL* wrap_original)(uintptr_t)) {
    if (wrap_original)
        if (const uint64_t game = wrap_original(static_cast<uintptr_t>(entry))) return game;
    return current;
}
// external_runner(): 0 the runner is kiln.types', 1 native SET owns it, 2 the modules could not be read (passive).
inline uint32_t external_state(bool external, bool unsure) { return !external ? 0u : unsure ? 2u : 1u; }
struct SystemHookSlot { uint64_t function; bool claimed; };
// -2 duplicate active owner; -1 bounded distinct-function capacity exhausted. Retired slots keep their function.
template<size_t Capacity,class Lookup>
int system_hook_slot(uint64_t function,Lookup lookup) {
    int free=-1;
    for (size_t i=0;i<Capacity;++i) {
        const auto slot=lookup(i);
        if (slot.function==function) return slot.claimed?-2:static_cast<int>(i);
        if (!slot.function && free<0) free=static_cast<int>(i);
    }
    return free;
}
} // namespace ktypes
