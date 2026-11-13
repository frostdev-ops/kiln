// SPDX-License-Identifier: MIT
// Client engine bridge adapted from EMF 0.6.0 server_chat_hooks.cpp/server_chat.h (MIT).
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
// The three game calls and GameClient+0xF8 layout were checked upstream on the hash allowlist below.
// The two hooks (ChatLogAdd, ClientOutboxFlush) are kiln.hooks v2 raw links owned by kiln-emf, so they share the one
// detour per function with kiln.chat's and with other Kiln components', and ChatLogAdd runs after kiln.chat's. ChatClientSend
// is not hooked: it is called directly. Links stay for process lifetime; shutdown turns their work off.
#include "emf_internal.h"
#include "net_bridge.h"
#include "net/net_line.h"
#include <algorithm>
#ifndef KEMF_NET_CLIENT_TEST
#include "kiln_hook.hpp"
#endif

namespace kemf {
namespace {
#ifndef KEMF_NET_CLIENT_TEST
struct NetText {
    const char *ptr;
    size_t len;
};
using NetFlushFn = void (*)(uint8_t *);
using NetSendFn = uint8_t *(*)(uint8_t *, uint8_t *, uint8_t, const NetText *, uint32_t, uint32_t);
using NetAddFn = uint8_t *(*)(uint8_t *, uint8_t *, uint8_t, const NetText *, uint32_t, uint32_t, uint32_t, uint64_t,
                              const void *);
#endif
NetSendFn g_send = nullptr;
// Test builds replace the two hooked functions by plain functions; production calls through the links' continuations.
#ifdef KEMF_NET_CLIENT_TEST
NetFlushFn g_flush = nullptr;
NetAddFn g_add = nullptr;
#endif
std::atomic<bool> g_ready{false};  // the hooks and the send function are in place
std::atomic<bool> g_active{false};
std::atomic<uint8_t *> g_system{nullptr}, g_game_client{nullptr};
std::recursive_mutex g_client_dispatch;
#ifndef KEMF_NET_CLIENT_TEST
// Never revoked while the process runs (a game thread may be inside the chain); a RawHook's destructor leaves the link.
kiln::RawHook g_add_link, g_flush_link;
#endif

// The rest of the chain, then the game's function.
void call_flush(uint8_t *gc) {
#ifdef KEMF_NET_CLIENT_TEST
    g_flush(gc);
#else
    g_flush_link.call<void>(gc);
#endif
}
uint8_t *call_add(uint8_t *out, uint8_t *sys, uint8_t kind, const NetText *text, uint32_t tag1, uint32_t tag2,
                  uint32_t handle, uint64_t account, const void *time) {
#ifdef KEMF_NET_CLIENT_TEST
    return g_add(out, sys, kind, text, tag1, tag2, handle, account, time);
#else
    return g_add_link.call<uint8_t *>(out, sys, kind, text, tag1, tag2, handle, account, time);
#endif
}

void pump(uint8_t *gc) {
    std::lock_guard dispatch(g_client_dispatch);
    if (!g_active || !gc)
        return;
    uint8_t *sys = nullptr;
    if (!seh_copy(&sys, gc + 0xF8, sizeof sys) || !sys)
        return;
    // Another GameClient than the one the link talked through is a new connection: nothing of the last one stays (its
    // welcome, its lines, a held greeting, the callbacks queued for it). The first GameClient since the game's start
    // or since a world stop replaces nothing: the link is fresh (no marked line is taken before g_system is set here),
    // and callbacks already queued for the client world that just began must stay.
    uint8_t *const before = g_game_client.exchange(gc);
    if (before && before != gc)
        net_client_reset();
    g_system = sys;
    auto lines = net_client_take(4);
    for (size_t i = 0; i < lines.size(); ++i) {
        NetText text{lines[i].data(), lines[i].size()};
        uint8_t result[16]{};
        // /EHa contains synchronous engine faults; nothing unwinds into the game.
        try {
            g_send(result, sys, 0, &text, 0, 0);
        } catch (...) {
            logf(KILN_LOG_WARN, "EMF net: client chat send faulted; client transport disabled");
            g_active = false;
            return;
        }
        if (result[0] == 0)
            continue;
        if (result[0] == 2)
            net_client_put_back({lines.begin() + static_cast<ptrdiff_t>(i), lines.end()});
        else
            logf(KILN_LOG_WARN, "EMF net: client chat refused a line (code %u)", result[0]);
        return;
    }
}
void hook_flush(uint8_t *gc) {
    try {
        pump(gc);
    } catch (...) {
        g_active = false;
    }
    call_flush(gc);
}
uint8_t *hook_add(uint8_t *out, uint8_t *sys, uint8_t kind, const NetText *text, uint32_t tag1, uint32_t tag2,
                  uint32_t handle, uint64_t account, const void *time) {
    try {
        std::lock_guard dispatch(g_client_dispatch);
        NetText view{};
        if (g_active && out && kind == 0 && sys && sys == g_system.load() && text &&
            seh_copy(&view, text, sizeof view) && view.ptr) {
            bool taken = false;
            if (view.len > emf::core::net::kMaxLine) {
                char prefix[7]{};
                taken =
                    seh_copy(prefix, view.ptr, sizeof prefix) && std::string_view(prefix, sizeof prefix) == "#!emf1 ";
            } else {
                std::string line(view.len, '\0');
                taken = (line.empty() || seh_copy(line.data(), view.ptr, line.size())) &&
                        emf::core::net::is_marked(line) && net_client_line(line, account);
            }
            if (taken) {
                const uint32_t zero = 0;
                const uint8_t ok = 0;
                // All marked lines for this client are consumed, including malformed/spoofed ones.
                if (seh_copy(out, &ok, 1) && seh_copy(out + 4, &zero, 4))
                    return out;
            }
        }
    } catch (...) {
        g_active = false;
    }
    return call_add(out, sys, kind, text, tag1, tag2, handle, account, time);
}
} // namespace

void net_client_clear() {
    g_system = nullptr;
    g_game_client = nullptr;
}
// When the hooks take marked lines (g_active):
//  - from the game's start: nothing of an earlier connection can exist yet, so a greeting that comes while the first
//    joined game still loads is taken (and held until the game knows its own account);
//  - never between a world's stop and the next client world's start: what the stopped connection's GameClient still
//    flushes and what its server still sends must not be taken for the next connection, and without an identity of the
//    connection from the engine nothing here could tell the two apart (the next GameClient may have the same address).
//    A greeting that comes in that time stays in the game's chat and is not answered; the server greets once more;
//  - from each client world's start on.
void net_client_stop() {
    std::lock_guard dispatch(g_client_dispatch);
    g_active = false;
    net_client_clear();
}
// A client world began. When the hooks know a GameClient, the game pumped it while it loaded (the first join of a game
// run): this is the same connection, and a new session on it. As in EMF's core, the last welcome and the lines waiting
// to be sent go, but a greeting held while the game did not know its own account stays. Done here, under the hooks'
// lock, not at the next pump: a second greeting that arrives between this and the next pump is answered, and a reset
// after it would throw that answer away.
void net_client_resume() {
    std::lock_guard dispatch(g_client_dispatch);
    if (g_game_client.load())
        net_client_new_world();
    g_active = g_ready.load();
}

#ifndef KEMF_NET_CLIENT_TEST
bool net_client_install() {
    if (g_ready) {
        g_active = true;
        return true;
    }
    if (!g_api || !g_api->build_info || !g_api->symbol)
        return false;
    KilnBuildInfo b{};
    b.size = sizeof b;
    if (g_api->build_info(&b) != KILN_OK || !b.verified || !b.hooks_enabled)
        return false;
    // 1273043,1285601,1287369. A matching short signature alone cannot validate a GameClient layout.
    static constexpr const char *hashes[] = {"d64966e23b1a458ee6f147bcbceac0b577fa7e39a212f371287d8cc119817882",
                                             "ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618",
                                             "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77"};
    if (std::none_of(std::begin(hashes), std::end(hashes),
                     [&](const char *h) { return std::strncmp(b.exe_sha256, h, 65) == 0; })) {
        logf(KILN_LOG_WARN, "EMF net: client chat layout is unverified on this image; remote client messages are off");
        return false;
    }
    uintptr_t add = 0, flush = 0, send = 0;
    if (g_api->symbol(kstr("chat/ChatLogAdd"), &add) != KILN_OK ||
        g_api->symbol(kstr("emf/ClientOutboxFlush"), &flush) != KILN_OK ||
        g_api->symbol(kstr("emf/ChatClientSend"), &send) != KILN_OK || !add || !flush || !send)
        return false;
    g_send = reinterpret_cast<NetSendFn>(send);
    // ChatLogAdd after kiln.chat's link (kiln.chat holds back players' lines it hands to mods; this link sees every line
    // that reaches the game's function, including the ones kiln.chat adds back). The links are installed in place: a game
    // thread may enter the chain at once, and the continuations are written before it can.
    if (!g_add_link.install(g_api, g_owner, add, reinterpret_cast<void *>(&hook_add), "emf/ChatLogAdd", 0, nullptr,
                            "kiln.chat")) {
        logf(KILN_LOG_WARN, "EMF net: ChatLogAdd link refused (%s); remote client messages are off",
             g_add_link.error() ? g_add_link.error() : "?");
        return false;
    }
    if (!g_flush_link.install(g_api, g_owner, flush, reinterpret_cast<void *>(&hook_flush), "emf/ClientOutboxFlush")) {
        logf(KILN_LOG_WARN, "EMF net: ClientOutboxFlush link refused (%s); remote client messages are off",
             g_flush_link.error() ? g_flush_link.error() : "?");
        g_add_link.revoke();  // the continuation stays valid for a thread already inside hook_add
        return false;
    }
    g_ready = true;
    g_active = true;
    logf(KILN_LOG_INFO, "EMF net: client chat receive/send hooks on (official #!emf1 wire)");
    return true;
}
#else
bool net_client_install() { return true; }
NetClientDetours net_client_test_install(NetFlushFn flush, NetSendFn send, NetAddFn add) {
    net_client_stop();
    g_flush = flush;
    g_send = send;
    g_add = add;
    g_ready = true;
    g_active = true;
    return {&hook_flush, &hook_add};
}
#endif
} // namespace kemf
