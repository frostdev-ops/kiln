// SPDX-License-Identifier: MIT
// kiln_hook.hpp: a header-only C++ client of kiln.hooks v2 with a safetyhook-like surface, so a service or plugin can
// swap `safetyhook::InlineHook` / `safetyhook::MidHook` for `kiln::RawHook` / `kiln::MidHook2` and keep its code.
//
//   #include "kiln_hook.hpp"
//
//   static kiln::RawHook g_add;                       // process lifetime, like the hooks it replaces
//   static void hk_chat_add(void* chat, int type, const char* text) {
//       if (g_add.caller() == g_receive_ret) { ... }  // the game's call site: valid before g_add.call(...)
//       g_add.call<void>(chat, type, text);           // the rest of the chain, then the real function
//   }
//   ...
//   if (!g_add.install(api, owner, target, reinterpret_cast<void*>(&hk_chat_add), "chat/ChatLogAdd")) fail(g_add.error());
//
// What you get over a raw safetyhook hook: one physical detour per address for every Kiln component (two components on
// one function no longer layer), `before` / `after` ordering by owner, the game's return address from caller(), a map
// of all hooks in <kiln>/state/hooks.json, detection of other modules' hooks, and links that can be added and revoked
// while the game runs without freezing threads.
//
// Rules:
//  - The API comes from `api->service("kiln.hooks", 2)`. A core that only has v1 (or none) makes install() fail with
//    error() set; services and the core ship together, so there is no safetyhook fallback here.
//  - `original<T>()` / `call<R>(...)` run the REST OF THE CHAIN and then the real function (a "continuation"). It stays
//    valid after revoke() and is never freed, so a handler that still holds it is safe.
//  - A RawHook is not unhooked by its destructor: the link lives until revoke(), revoke_owner(), or the process ends
//    (static destruction order must never call into the core). The small state block behind it is leaked on purpose.
//  - Assigning to a hook that holds a live link (`g = RawHook::create(...)`, `g = {}`) revokes that link, like assigning
//    a safetyhook hook. install() on a live hook replaces it. The registry keeps one live raw link per handler and
//    target, so the old link leaves the chain in the same step the new one enters it (if the new install fails, the
//    old link stays): a call never runs the handler twice and never loops through it, whatever the priorities. A
//    handler still running on the old link keeps a valid original(). install() with another target than the live
//    hook's revokes the old link first (one handler cannot continue two functions).
//  - install() writes the continuation before the link can be reached, so the handler may call original() on its very
//    first run. create() returns by value: a hook installed after the game started, stored in a global, can run before
//    the assignment finishes; use install() on the global for those.
//  - caller() is valid when read before the handler calls its original or another hooked function. A recursive entry into
//    the same target on the same thread overwrites it.
//  - `call<R>(args...)` casts the continuation to `R(*)(Args...)` with the argument types you pass: pass exactly the
//    game's types (uint64_t, not int, for a 64-bit parameter) or use original<T>() with the full signature.
#ifndef KILN_HOOK_HPP
#define KILN_HOOK_HPP

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>
#include <type_traits>
#include <utility>

#include "kiln_api.h"
#include "services/kiln_hooks.h"

namespace kiln {

// The kiln.hooks table when it is v2 and complete, else nullptr.
inline const KilnHooksApi* hooks_v2(const KilnApi* api) {
    if (!api || api->size < offsetof(KilnApi, service) + sizeof(api->service) || !api->service) return nullptr;
    const auto* t = static_cast<const KilnHooksApi*>(
        api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 2));
    return t && t->header.version >= 2 && t->header.size >= sizeof(KilnHooksApi) ? t : nullptr;
}

// True when kiln.hooks has no free target slot left (stats().targets reached targets_cap, 512). Then a raw_detour /
// detour2 / mid2 that answers KILN_ERR_UNAVAILABLE for an address it does not hook yet means "full", not "refused": no
// Kiln link can ever join that address (a new address is refused for good, targets are never freed), so a plain
// safetyhook detour there cannot meet a Kiln chain later. False when kiln.hooks does not say (a core without targets_cap).
inline bool hooks_targets_full(const KilnHooksApi* h) {
    if (!h || !h->stats) return false;
    KilnHooksStats st{};
    st.size = sizeof st;
    return h->stats(&st) == KILN_OK && st.targets_cap != 0 && st.targets >= st.targets_cap;
}

namespace hook_detail {

struct State {
    void* original = nullptr;
    KilnHandle handle = 0;
    const KilnHooksApi* api = nullptr;
    uintptr_t target = 0;
};

// The register layout kiln.hooks' mid stub builds (safetyhook's Context64).
static_assert(offsetof(KilnRegisters, rflags) == 0x100 && offsetof(KilnRegisters, rsp) == 0x180 &&
                  offsetof(KilnRegisters, trampoline_rsp) == 0x188 && offsetof(KilnRegisters, rip) == 0x190 &&
                  sizeof(KilnRegisters) == 0x198,
              "KilnRegisters must keep safetyhook's Context64 layout");

inline KilnHookOrder make_order(int32_t priority, const char* label, const char* before, const char* after) {
    KilnHookOrder o{};
    o.size = sizeof o;
    o.priority = priority;
    if (label) o.label = KilnStr{label, std::strlen(label)};
    if (before) o.before = KilnStr{before, std::strlen(before)};
    if (after) o.after = KilnStr{after, std::strlen(after)};
    return o;
}

}  // namespace hook_detail

// A native-signature detour link: `fn` has the target's own signature and calls original()/call() to continue.
class RawHook {
public:
    RawHook() = default;
    RawHook(const RawHook&) = delete;
    RawHook& operator=(const RawHook&) = delete;
    RawHook(RawHook&& other) noexcept : s_(other.s_), error_(other.error_) { other.s_ = nullptr; }
    // Revokes the link this hook held (see the rules above). An empty right side keeps the old, revoked state so a
    // handler still running reads a valid original().
    RawHook& operator=(RawHook&& other) noexcept {
        if (this == &other) return *this;
        hook_detail::State* old = s_;
        hook_detail::State* incoming = other.s_;
        other.s_ = nullptr;
        error_ = other.error_;
        s_ = incoming ? incoming : old;
        if (old && old != incoming) revoke_state(old);
        return *this;
    }
    ~RawHook() = default;  // see the rules above: the link stays until revoke()

    // Installs in place (the continuation's storage address is fixed before the link is reachable). Returns true on success.
    // `label` names the link in kiln.log and the hook map; `before` / `after` are comma-separated owner names.
    bool install(const KilnApi* api, KilnOwner owner, uintptr_t target, void* fn, const char* label,
                 int32_t priority = 0, const char* before = nullptr, const char* after = nullptr) {
        error_ = nullptr;
        const KilnHooksApi* h = hooks_v2(api);
        if (!h) return error_ = "kiln.hooks v2 is not available", false;
        auto* st = new (std::nothrow) hook_detail::State;
        if (!st) return error_ = "out of memory", false;
        hook_detail::State* old = s_;
        if (old && old->handle && old->target != target) revoke_state(old);  // another function: out first
        st->api = h;
        st->target = target;
        st->original = old ? old->original : nullptr;  // a handler still running on the old link stays valid
        s_ = st;  // before raw_detour: a handler running at once reads s_->original
        const KilnHookOrder o = hook_detail::make_order(priority, label, before, after);
        const KilnResult rc = h->raw_detour(owner, target, &o, fn, &st->original, &st->handle);
        if (rc != KILN_OK) {
            s_ = old;  // the new state is leaked: nothing can have reached the handler; the old link (if any) stays
            return error_ = rc == KILN_ERR_UNAVAILABLE ? "kiln.hooks could not hook the target (see kiln.log)"
                                                        : "kiln.hooks refused the hook (bad target or arguments)",
                   false;
        }
        if (old) revoke_state(old);  // replaced: the new link is in, take the old one out
        return true;
    }

    // Like install(), but returns the hook by value (check operator bool, then error()).
    static RawHook create(const KilnApi* api, KilnOwner owner, uintptr_t target, void* fn, const char* label,
                          int32_t priority = 0, const char* before = nullptr, const char* after = nullptr) {
        RawHook hk;
        hk.install(api, owner, target, fn, label, priority, before, after);
        return hk;
    }

    explicit operator bool() const { return s_ != nullptr && s_->handle != 0; }
    KilnHandle handle() const { return s_ ? s_->handle : 0; }
    const char* error() const { return error_; }

    // The continuation as a function pointer of the target's signature, e.g. original<uint64_t (*)(void*, int)>().
    template <class T>
    T original() const {
        static_assert(std::is_pointer_v<T> && std::is_function_v<std::remove_pointer_t<T>>,
                      "original<T>() wants a function pointer type");
        return reinterpret_cast<T>(s_ ? s_->original : nullptr);
    }

    // Runs the rest of the chain and then the real function.
    template <class Ret = void, class... Args>
    Ret call(Args... args) const {
        return reinterpret_cast<Ret (*)(Args...)>(s_->original)(args...);
    }

    // The return address of the game call that entered this target on this thread (0 when unknown).
    uintptr_t caller() const { return s_ && s_->api ? s_->api->caller(s_->handle) : 0; }

    // Removes this link from the chain. original() keeps working. Idempotent.
    void revoke() {
        if (s_) revoke_state(s_);
    }

private:
    static void revoke_state(hook_detail::State* st) {
        if (st->handle && st->api) st->api->revoke(st->handle);
        st->handle = 0;
    }

    hook_detail::State* s_ = nullptr;
    const char* error_ = nullptr;
};

// A mid-function hook: `fn(Context&)` reads and writes the registers at `target`. Ctx is KilnRegisters or any type of the
// same size and layout (safetyhook::Context), so an existing handler keeps its signature. Mid links share the target's
// single physical detour with raw links; they run in the chain's order.
class MidHook2 {
public:
    MidHook2() = default;
    MidHook2(const MidHook2&) = delete;
    MidHook2& operator=(const MidHook2&) = delete;
    MidHook2(MidHook2&& other) noexcept : api_(other.api_), handle_(other.handle_), error_(other.error_) {
        other.handle_ = 0;
    }
    // Revokes the link this hook held, like assigning a safetyhook hook.
    MidHook2& operator=(MidHook2&& other) noexcept {
        if (this == &other) return *this;
        if (handle_ && handle_ != other.handle_) revoke();
        api_ = other.api_, handle_ = other.handle_, error_ = other.error_, other.handle_ = 0;
        return *this;
    }
    ~MidHook2() = default;

    template <class Ctx>
    bool install(const KilnApi* api, KilnOwner owner, uintptr_t target, void (*fn)(Ctx&), const char* label,
                 int32_t priority = 0, const char* before = nullptr, const char* after = nullptr) {
        static_assert(sizeof(Ctx) == sizeof(KilnRegisters), "the context must have KilnRegisters' layout");
        static_assert(std::is_standard_layout_v<Ctx> && std::is_trivially_copyable_v<Ctx>,
                      "the context must be a plain struct with KilnRegisters' field order (safetyhook::Context64 is)");
        error_ = nullptr;
        const KilnHooksApi* h = hooks_v2(api);
        if (!h) return error_ = "kiln.hooks v2 is not available", false;
        const KilnHookOrder o = hook_detail::make_order(priority, label, before, after);
        KilnHandle handle = 0;
        const KilnResult rc =
            h->mid2(owner, target, &o, &thunk<Ctx>, reinterpret_cast<void*>(fn), &handle);
        if (rc != KILN_OK)
            return error_ = rc == KILN_ERR_UNAVAILABLE ? "kiln.hooks could not hook the target (see kiln.log)"
                                                        : "kiln.hooks refused the hook (bad target or arguments)",
                   false;
        revoke();  // install() on a live hook replaces it (the new link is already in)
        api_ = h;
        handle_ = handle;
        return true;
    }

    template <class Ctx>
    static MidHook2 create(const KilnApi* api, KilnOwner owner, uintptr_t target, void (*fn)(Ctx&), const char* label,
                           int32_t priority = 0, const char* before = nullptr, const char* after = nullptr) {
        MidHook2 hk;
        hk.install(api, owner, target, fn, label, priority, before, after);
        return hk;
    }

    explicit operator bool() const { return handle_ != 0; }
    KilnHandle handle() const { return handle_; }
    const char* error() const { return error_; }
    // For a mid hook at a function's first instruction: the return address of the game call (else not meaningful).
    uintptr_t caller() const { return api_ && handle_ ? api_->caller(handle_) : 0; }
    void revoke() {
        if (api_ && handle_) api_->revoke(handle_);
        handle_ = 0;
    }

private:
    template <class Ctx>
    static void KILN_CALL thunk(KilnRegisters* regs, void* user) {
        reinterpret_cast<void (*)(Ctx&)>(user)(*reinterpret_cast<Ctx*>(regs));
    }

    const KilnHooksApi* api_ = nullptr;
    KilnHandle handle_ = 0;
    const char* error_ = nullptr;
};

}  // namespace kiln

#endif /* KILN_HOOK_HPP */
