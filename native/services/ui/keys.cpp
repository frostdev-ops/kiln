// SPDX-License-Identifier: MIT
// kiln.ui: the key subscribers (subscribe_key, and since v7 subscribe_key_ex) and the hand-out of one key press to them.
// A subscriber made with subscribe_key gets keyboard keys only, as before 0.2.0-dev.27; one made with subscribe_key_ex
// and KILN_UI_KEYS_MOUSE gets the middle and side mouse buttons and the wheel's notches too (input.cpp, mouse_press.h).
// A mouse press can be taken only by a subscriber that got it, and with no such subscriber input.cpp does not even make
// one (keys_mouse_wanted). Its own unit so that the window-input harness runs the real subscriber rules with input.cpp.
#include <algorithm>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "ui.h"

namespace kui {
namespace {

struct KeySub : CallbackLifetime {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    KilnUiKeyFn fn = nullptr;
    void* user = nullptr;
    uint32_t flags = 0;  // KILN_UI_KEYS_*
};
std::mutex g_mu;
std::vector<std::shared_ptr<KeySub>> g_subs;
// Unsubscribed, a call may still be in flight: kept findable while another thread drains it (also self-unsubscribe).
std::unordered_map<KilnHandle, std::shared_ptr<KeySub>> g_retiring;
std::atomic<uint32_t> g_mouse_subs{0};  // subscribers with KILN_UI_KEYS_MOUSE (any thread reads it)

// Under g_mu, after every change of g_subs.
void count_mouse_locked() {
    const auto n = std::count_if(g_subs.begin(), g_subs.end(), [](const auto& s) { return (s->flags & KILN_UI_KEYS_MOUSE) != 0; });
    g_mouse_subs.store(static_cast<uint32_t>(n), std::memory_order_release);
}

bool guarded_key(KilnUiKeyFn fn, const KilnUiKey* k, void* user, bool* consumed) {
    __try {
        *consumed = fn(k, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace

KilnResult keys_subscribe(KilnOwner owner, KilnUiKeyFn fn, void* user, uint32_t flags, KilnHandle* out) {
    if (!fn || (flags & ~static_cast<uint32_t>(KILN_UI_KEYS_MOUSE)) != 0) return KILN_ERR_INVALID_ARGUMENT;
    auto sub = std::make_shared<KeySub>();
    sub->owner = owner;
    sub->fn = fn;
    sub->user = user;
    sub->flags = flags;
    std::lock_guard lk(g_mu);
    sub->handle = next_handle();
    if (out) *out = sub->handle;
    g_subs.push_back(std::move(sub));
    count_mouse_locked();
    return KILN_OK;
}

KilnResult keys_unsubscribe(KilnHandle h) {
    std::shared_ptr<KeySub> removed;
    {
        std::lock_guard lk(g_mu);
        const auto it = std::find_if(g_subs.begin(), g_subs.end(), [&](const auto& s) { return s->handle == h; });
        if (it != g_subs.end()) {
            removed = *it;
            removed->active.store(false, std::memory_order_release);
            g_retiring[h] = removed;
            g_subs.erase(it);
            count_mouse_locked();
        } else if (const auto r = g_retiring.find(h); r != g_retiring.end()) {
            removed = r->second;
        }
    }
    if (!removed) return KILN_ERR_NOT_FOUND;
    // Never wait under g_mu: the callback may itself subscribe or unsubscribe. Two callbacks cancelling one another must
    // not deadlock their threads: from inside a kiln.ui callback this only tries (retry from the cleanup thread).
    auto drain = removed->drain(t_callback != nullptr);
    if (!drain.owns_lock()) return KILN_ERR_WRONG_THREAD;
    std::lock_guard lk(g_mu);
    g_retiring.erase(h);
    return KILN_OK;
}

bool keys_dispatch(const KilnUiKey& key, bool mouse) {
    std::vector<std::shared_ptr<KeySub>> subs;
    {
        std::lock_guard lk(g_mu);
        subs = g_subs;
    }
    bool consumed = false;
    for (const auto& s : subs) {
        if (mouse && !(s->flags & KILN_UI_KEYS_MOUSE)) continue;  // subscribe_key: keyboard keys only
        std::lock_guard call(s->gate);
        if (!s->active.load(std::memory_order_acquire)) continue;
        bool c = false;
        CallbackLifetime::Invocation invocation(s.get());
        auto* outer = t_callback;
        t_callback = s.get();
        const bool ok = guarded_key(s->fn, &key, s->user, &c);
        t_callback = outer;
        if (!ok) {
            s->active.store(false, std::memory_order_release);
            set_error("a key callback faulted; removed");
            std::lock_guard lk(g_mu);
            std::erase_if(g_subs, [&](const auto& x) { return x == s; });
            count_mouse_locked();
            continue;
        }
        consumed |= c;
    }
    return consumed;
}

bool keys_mouse_wanted() { return g_mouse_subs.load(std::memory_order_acquire) != 0; }

size_t keys_count() {
    std::lock_guard lk(g_mu);
    return g_subs.size();
}

}  // namespace kui
