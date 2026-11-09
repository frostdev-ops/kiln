// SPDX-License-Identifier: MIT
// kiln.chat: players' chat lines in, server lines out (docs/re/chat.md).
//
// Game side:
//  - The GameServer frame (one function, the tick thread) runs, in order: the receive loop ChatReceive(GameServer)
//    -> ... -> the server world's sim step (Kiln Pre/Post) -> weather -> the chat send step. The receive loop adds a
//    player's typed line to the ChatMessagesSystem inbox with ChatLogAdd(result, sys, type, text, tag1, tag2,
//    connectionHandle, accountHash, time); the send step sends every inbox line to each player reader whose bit in
//    the line's mask is clear.
//  - Kiln hooks ChatLogAdd (a kiln.hooks v2 raw link) and recognises the receive loop's call by the return address of
//    the game call that entered it, from caller(): other links on the same function (kiln-emf's, an EMF mod's)
//    cannot disturb it. While someone subscribed, such a line is held back (the hook reports success without adding
//    it) and delivered in the next Pre, which runs in the same server frame before the send step. Unless a subscriber
//    consumes it, Kiln then adds it with the original arguments (through the rest of the chain and then the game's
//    function), so it reaches everyone exactly as it would have.
//  - Server lines: ChatLogAdd with the recipient's connection handle and account (a line with no speaker is sent
//    but not shown by clients), then the line is marked as already sent (the game's own MarkSent) for every other
//    reader, so only the recipient gets it. They are notices (type 4, no loca tag): the client's chat shows such a
//    line as its text alone, in the style of its own "Player X joined." notices, without a speaker's name (client
//    26430625, the chat line renderer 0xb72890: type 4 with tag1 0 draws the line's text). Typed lines are type 0
//    and show as "<speaker>: <text>".
//  - Connection records (ServerPlayers, GameServer+0x1E0, 16 x 0x148 B): +0 handle (index = handle & 0x3F),
//    +4 peer handle (reader = peer & 0x7F), +0xA state, +0x10 permission bits, +0x18 player entity, +0x20 name
//    {ptr, len}, +0x48 account hash. The GameServer pointer comes from the ChatReceive hook (every frame).
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "services/kiln_chat.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uintptr_t g_base = 0;

struct ChatResult {
    uint8_t code;
    uint8_t pad[3];
    uint32_t value;
};
struct StrView {
    const char* data;
    uint64_t size;
};
struct OptTime {
    uint64_t time;
    uint8_t has;
    uint8_t pad[7];
};
static_assert(sizeof(ChatResult) == 8 && sizeof(StrView) == 16 && sizeof(OptTime) == 16);

using MarkSentFn = void(__fastcall*)(void* sys, void* node, uint32_t reader, uint32_t size);

struct SysOffsets {
    int64_t max_text = 0x400, inbox_tail = -1, inbox_count = -1, r_active = -1, r_gen = -1, r_pending = -1,
            r_stride = -1, readers = -1, n_len = -1, n_handle = -1, n_account = -1, n_mask = -1, n_text = -1;
} C;
struct PlayerOffsets {
    int64_t gs_chat = -1, gs_players = -1, stride = -1, size = -1, peer = -1, perms = -1, name = -1, entity = -1,
            account = -1, state = -1;
} P;

kiln::RawHook g_add_hook, g_recv_hook;  // links of the kiln.hooks v2 chains on ChatLogAdd and ChatReceive
MarkSentFn g_mark_sent = nullptr;
uintptr_t g_receive_ret = 0;  // return address of the receive loop's ChatLogAdd call
bool g_can_receive = false, g_can_send = false, g_can_players = false;
std::atomic<uint8_t*> g_gs{nullptr};
bool g_logged_ready = false;

struct Pending {
    uint8_t type;
    bool injected;
    uint32_t tag1, tag2, handle;
    uint64_t account;
    std::string text;
};
std::mutex g_queue_mutex;
std::vector<Pending> g_queue;
constexpr size_t kMaxQueue = 256;

struct Sub {
    KilnHandle handle;
    KilnOwner owner;
    KilnChatReceiveFn fn;
    void* user;
    std::atomic<bool> live{true};
};
std::mutex g_subs_mutex;
std::vector<std::shared_ptr<Sub>> g_subs;
std::atomic<uint32_t> g_sub_count{0};
uint64_t g_next_handle = 0x4348415400000001ull;  // "CHAT"

std::atomic<uint64_t> g_received{0}, g_consumed{0}, g_sent{0}, g_ids{0};

void logf(int32_t level, const char* fmt, ...) {
    char buf[1536];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (static_cast<size_t>(n) >= sizeof buf) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

template <typename T>
T& at(uint8_t* p, int64_t off) {
    return *reinterpret_cast<T*>(p + off);
}

// ---- guarded helpers (no C++ objects with destructors, as __try requires) ----
bool guarded_copy(void* dst, const void* src, size_t n) {
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool guarded_sub(KilnChatReceiveFn fn, const KilnChatMessage* m, void* user, bool* consume, unsigned long* code) {
    __try {
        *consume = fn(m, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool guarded_add(ChatResult* r, uint8_t* sys, uint8_t type, const StrView* v, uint32_t tag1, uint32_t tag2,
                 uint32_t handle, uint64_t account, const OptTime* t) {
    __try {
        g_add_hook.call<ChatResult*>(r, sys, type, v, tag1, tag2, handle, account, t);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool guarded_mark(uint8_t* sys, uint8_t* node, uint32_t reader, uint32_t size) {
    __try {
        g_mark_sent(sys, node, reader, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint8_t* chat_sys() {
    uint8_t* gs = g_gs.load(std::memory_order_relaxed);
    if (!gs || P.gs_chat < 0) return nullptr;
    uint8_t* sys = nullptr;
    if (!guarded_copy(&sys, gs + P.gs_chat, sizeof sys)) return nullptr;
    return sys;
}

// Cuts UTF-8 text to `max` bytes at a character boundary.
size_t clip_utf8(const char* s, size_t n, size_t max) {
    if (n <= max) return n;
    size_t k = max;
    while (k > 0 && (static_cast<unsigned char>(s[k]) & 0xC0) == 0x80) --k;
    return k;
}

// ---- connection records ----
uint32_t record_count() {
    if (!g_can_players || P.stride <= 0) return 0;
    return static_cast<uint32_t>(std::min<int64_t>(P.size / P.stride, 64));
}

bool read_record(uint8_t* gs, uint32_t index, KilnChatPlayer* out) {
    uint8_t rec[0x60];
    const int64_t need = std::max({P.peer + 4, P.perms + 1, P.entity + 4, P.name + 16, P.account + 8, P.state + 1,
                                   int64_t{4}});
    if (need > static_cast<int64_t>(sizeof rec)) return false;
    if (!guarded_copy(rec, gs + P.gs_players + static_cast<int64_t>(index) * P.stride, static_cast<size_t>(need)))
        return false;
    const uint32_t handle = at<uint32_t>(rec, 0);
    if (handle == 0 || (handle & 0x3F) != index) return false;
    KilnChatPlayer p{};
    p.size = sizeof p;
    p.connection = handle;
    p.reader = at<uint32_t>(rec, P.peer) & 0x7F;
    p.permissions = at<uint8_t>(rec, P.perms);
    p.entity = at<uint32_t>(rec, P.entity);
    if (p.entity > 1023) p.entity = 0;
    p.account = at<uint64_t>(rec, P.account);
    p.state = at<uint8_t>(rec, P.state);
    const char* name = at<const char*>(rec, P.name);
    uint64_t len = at<uint64_t>(rec, P.name + 8);
    if (name && len) {
        const size_t n = clip_utf8(name, static_cast<size_t>(std::min<uint64_t>(len, 63)), 63);
        if (!guarded_copy(p.name, name, n)) p.name[0] = 0;
        else p.name[n] = 0;
    }
    *out = p;
    return true;
}

uint32_t collect_players(KilnChatPlayer* out, uint32_t capacity) {
    uint8_t* gs = g_gs.load(std::memory_order_relaxed);
    if (!gs) return 0;
    uint32_t n = 0;
    for (uint32_t i = 0, count = record_count(); i < count; ++i) {
        KilnChatPlayer p{};
        if (!read_record(gs, i, &p)) continue;
        if (out && n < capacity) out[n] = p;
        ++n;
    }
    return n;
}

bool find_by(uint32_t handle, KilnEntity entity, KilnChatPlayer* out) {
    KilnChatPlayer all[64];
    const uint32_t n = std::min<uint32_t>(collect_players(all, 64), 64);
    for (uint32_t i = 0; i < n; ++i) {
        if ((handle && all[i].connection == handle) || (entity && all[i].entity == entity)) {
            *out = all[i];
            return true;
        }
    }
    return false;
}

// ---- adding lines ----
// The game's chat line types Kiln writes: a player's typed line ("<speaker>: <text>"), and a notice with no loca tag
// (its text alone, as the game's join and leave notices).
constexpr uint8_t kLineTyped = 0, kLineNotice = 4;

// Adds a line of `type` spoken by (handle, account). only_reader >= 0 narrows it to that reader.
// Returns false when the game refused it; *addressed = the target reader will be sent the line.
bool add_line(uint8_t* sys, uint8_t type, uint32_t handle, uint64_t account, const char* text, size_t len, int only_reader,
              bool* addressed) {
    *addressed = false;
    ChatResult r{};
    StrView v{text, len};
    OptTime none{};
    if (!guarded_add(&r, sys, type, &v, 0, 0, handle, account, &none) || r.code != 0) {
        logf(KILN_LOG_WARN, "chat: ChatLogAdd refused a line (code %u)", r.code);
        return false;
    }
    g_sent.fetch_add(1);
    uint8_t* node = nullptr;
    if (!guarded_copy(&node, sys + C.inbox_tail, sizeof node) || !node) return true;
    if (at<uint32_t>(node, C.n_handle) != handle || at<uint32_t>(node, C.n_len) != len) {
        logf(KILN_LOG_WARN, "chat: newest inbox line is not the one just added; not narrowed");
        return true;
    }
    if (only_reader < 0) {
        *addressed = true;
        return true;
    }
    // ChatLogAdd added this size to every active reader's pending count; MarkSent takes it off again.
    uint32_t size = static_cast<uint32_t>(len) + 0x17;
    if (size & 3) size = (size & ~3u) + 4;
    const uint32_t readers = static_cast<uint32_t>(std::min<int64_t>(C.readers, 32));
    for (uint32_t i = 0; i < readers; ++i) {
        if (static_cast<int>(i) == only_reader) continue;
        if (at<uint32_t>(node, C.n_mask) & (1u << i)) continue;  // inactive reader or already done
        guarded_mark(sys, node, i, size);
    }
    *addressed = only_reader < 32 && !(at<uint32_t>(node, C.n_mask) & (1u << only_reader));
    return true;
}

// ---- hooks ----
bool enqueue(uint8_t type, const char* text, size_t len, uint32_t tag1, uint32_t tag2, uint32_t handle,
             uint64_t account, bool injected) {
    Pending p{type, injected, tag1, tag2, handle, account, std::string()};
    p.text.resize(len);
    if (len && !guarded_copy(p.text.data(), text, len)) return false;
    std::lock_guard lock(g_queue_mutex);
    if (g_queue.size() >= kMaxQueue) return false;
    g_queue.push_back(std::move(p));
    return true;
}

// The game's return address, not this detour's: caller() is right whatever other link runs before this one (kiln-emf's
// hook on this function sits in the same chain). Read it before calling the original or any other hooked function.
ChatResult* __fastcall hk_chat_log_add(ChatResult* out, uint8_t* sys, uint8_t type, const StrView* text, uint32_t tag1,
                                       uint32_t tag2, uint32_t handle, uint64_t account, const OptTime* time) {
    if (g_receive_ret && type == 0 && g_sub_count.load(std::memory_order_relaxed) > 0 && text && text->data &&
        text->size > 0 && text->size <= static_cast<uint64_t>(C.max_text) && g_add_hook.caller() == g_receive_ret) {
        if (enqueue(type, text->data, static_cast<size_t>(text->size), tag1, tag2, handle, account, false)) {
            out->code = 0;  // what ChatLogAdd returns on success; the caller does not look at it anyway
            out->value = 0;
            return out;
        }
    }
    return g_add_hook.call<ChatResult*>(out, sys, type, text, tag1, tag2, handle, account, time);
}

uintptr_t __fastcall hk_chat_receive(uint8_t* gs, uintptr_t a, uintptr_t b, uintptr_t c) {
    if (g_gs.load(std::memory_order_relaxed) != gs) {
        g_gs.store(gs);
        logf(KILN_LOG_INFO, "chat: GameServer at %p", static_cast<void*>(gs));
    }
    return g_recv_hook.call<uintptr_t>(gs, a, b, c);
}

// ---- delivery (tick thread, Pre) ----
void deliver(Pending& p) {
    g_received.fetch_add(1);
    KilnChatMessage m{};
    m.size = sizeof m;
    m.type = p.type;
    m.id = g_ids.fetch_add(1) + 1;
    m.text = KilnStr{p.text.data(), p.text.size()};
    if (!find_by(p.handle, 0, &m.sender)) {
        m.sender = KilnChatPlayer{};
        m.sender.size = sizeof m.sender;
        m.sender.connection = p.handle;
        m.sender.account = p.account;
    }
    if (p.injected && !p.account) p.account = m.sender.account;

    std::vector<std::shared_ptr<Sub>> subs;
    {
        std::lock_guard lock(g_subs_mutex);
        subs = g_subs;
    }
    bool consume = false;
    for (auto& s : subs) {
        if (!s->live.load()) continue;
        bool c = false;
        unsigned long code = 0;
        if (!guarded_sub(s->fn, &m, s->user, &c, &code)) {
            s->live.store(false);
            logf(KILN_LOG_ERROR, "chat: subscriber faulted (0x%08lx) and was removed", code);
            continue;
        }
        consume = consume || c;
    }
    const char* who = m.sender.name[0] ? m.sender.name : "?";
    if (consume) {
        g_consumed.fetch_add(1);
        logf(KILN_LOG_INFO, "chat: line #%llu from %s (entity %u, connection 0x%x) consumed: %.*s",
             static_cast<unsigned long long>(m.id), who, m.sender.entity, p.handle, static_cast<int>(p.text.size()),
             p.text.data());
        return;
    }
    uint8_t* sys = chat_sys();
    if (!sys) {
        logf(KILN_LOG_WARN, "chat: chat system unknown; line #%llu dropped", static_cast<unsigned long long>(m.id));
        return;
    }
    ChatResult r{};
    StrView v{p.text.data(), p.text.size()};
    OptTime none{};
    const bool ok = guarded_add(&r, sys, p.type, &v, p.tag1, p.tag2, p.handle, p.account, &none) && r.code == 0;
    logf(KILN_LOG_DEBUG, "chat: line #%llu from %s (entity %u) %s: %.*s", static_cast<unsigned long long>(m.id), who,
         m.sender.entity, ok ? "passed on" : "REFUSED by the game", static_cast<int>(p.text.size()), p.text.data());
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    if (!g_logged_ready) {
        uint8_t* sys = chat_sys();
        if (sys) {
            g_logged_ready = true;
            uint32_t active = 0;
            for (int64_t i = 0; i < std::min<int64_t>(C.readers, 32); ++i)
                if (at<uint8_t>(sys, C.r_active + i * C.r_stride)) ++active;
            logf(KILN_LOG_INFO, "chat: chat system %p ready: inbox %llu lines, %u active readers, %u players connected",
                 static_cast<void*>(sys), static_cast<unsigned long long>(at<uint64_t>(sys, C.inbox_count)), active,
                 collect_players(nullptr, 0));
        }
    }
    std::vector<Pending> q;
    {
        std::lock_guard lock(g_queue_mutex);
        if (g_queue.empty()) return;
        q.swap(g_queue);
    }
    for (auto& p : q) deliver(p);
}

// ---- world sessions ----
// A game client hosts one server world after another: when the game closes the server world (or a new one starts
// without a close), the GameServer and the lines queued for it belong to the old world. ChatReceive gives the new
// GameServer at its first call.
void drop_world(const char* why) {
    size_t dropped = 0;
    {
        std::lock_guard lock(g_queue_mutex);
        dropped = g_queue.size();
        g_queue.clear();
    }
    g_gs.store(nullptr);
    g_logged_ready = false;
    logf(KILN_LOG_INFO, "chat: server world closed (%s); GameServer released, %zu queued line(s) dropped", why, dropped);
}

bool g_closed = false;  // tick thread: the last server world was closed with a STOPPING

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER) return;
    if (ev->event == KILN_WORLD_STOPPING) {
        drop_world("the game is closing it");
        g_closed = true;
    } else if (ev->event == KILN_WORLD_STARTED) {
        if (ev->session > 1 && !g_closed) drop_world("a new server world started");
        g_closed = false;
    }
}

// ---- table ----
KilnResult KILN_CALL subscribe(KilnOwner owner, KilnChatReceiveFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_can_receive) return KILN_ERR_UNAVAILABLE;
    auto s = std::make_shared<Sub>();
    s->owner = owner;
    s->fn = fn;
    s->user = user;
    std::lock_guard lock(g_subs_mutex);
    s->handle = g_next_handle++;
    g_subs.push_back(s);
    g_sub_count.store(static_cast<uint32_t>(g_subs.size()));
    if (out) *out = s->handle;
    return KILN_OK;
}

KilnResult KILN_CALL unsubscribe(KilnHandle handle) {
    std::lock_guard lock(g_subs_mutex);
    auto it = std::find_if(g_subs.begin(), g_subs.end(), [&](auto& s) { return s->handle == handle; });
    if (it == g_subs.end()) return KILN_ERR_NOT_FOUND;
    (*it)->live.store(false);
    g_subs.erase(it);
    g_sub_count.store(static_cast<uint32_t>(g_subs.size()));
    return KILN_OK;
}

KilnResult KILN_CALL send(KilnEntity to, KilnStr text, uint32_t* delivered) {
    if (delivered) *delivered = 0;
    if (!text.ptr || !text.len) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_can_send || !g_can_players) return KILN_ERR_UNAVAILABLE;
    uint8_t* sys = chat_sys();
    if (!sys) return KILN_ERR_UNAVAILABLE;
    const size_t len = clip_utf8(text.ptr, text.len, static_cast<size_t>(C.max_text));
    KilnChatPlayer all[64];
    const uint32_t n = std::min<uint32_t>(collect_players(all, 64), 64);
    uint32_t count = 0, matched = 0;
    for (uint32_t i = 0; i < n; ++i) {
        if (to && all[i].entity != to) continue;
        ++matched;
        bool addressed = false;
        if (add_line(sys, kLineNotice, all[i].connection, all[i].account, text.ptr, len, static_cast<int>(all[i].reader), &addressed) &&
            addressed)
            ++count;
    }
    if (delivered) *delivered = count;
    if (to && !matched) return KILN_ERR_NOT_FOUND;
    logf(KILN_LOG_DEBUG, "chat: server line to %s: %u of %u players addressed: %.*s", to ? "one player" : "all", count,
         matched, static_cast<int>(len), text.ptr);
    return KILN_OK;
}

KilnResult KILN_CALL say(KilnEntity speaker, KilnStr text, KilnEntity to) {
    if (!text.ptr || !text.len || !speaker) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_can_send || !g_can_players) return KILN_ERR_UNAVAILABLE;
    uint8_t* sys = chat_sys();
    if (!sys) return KILN_ERR_UNAVAILABLE;
    KilnChatPlayer s{}, t{};
    if (!find_by(0, speaker, &s)) return KILN_ERR_NOT_FOUND;
    if (to && !find_by(0, to, &t)) return KILN_ERR_NOT_FOUND;
    const size_t len = clip_utf8(text.ptr, text.len, static_cast<size_t>(C.max_text));
    bool addressed = false;
    if (!add_line(sys, kLineTyped, s.connection, s.account, text.ptr, len, to ? static_cast<int>(t.reader) : -1, &addressed))
        return KILN_ERR_INTERNAL;
    return KILN_OK;
}

KilnResult KILN_CALL players(KilnChatPlayer* out, uint32_t capacity, uint32_t* count) {
    if (!g_can_players) return KILN_ERR_UNAVAILABLE;
    const uint32_t n = collect_players(out, out ? capacity : 0);
    if (count) *count = n;
    return out && n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL find_player(KilnEntity entity, KilnChatPlayer* out) {
    if (!entity || !out) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_can_players) return KILN_ERR_UNAVAILABLE;
    return find_by(0, entity, out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL status(KilnChatStatus* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    KilnChatStatus s{};
    s.size = sizeof s;
    s.receive_hooked = g_can_receive ? 1 : 0;
    uint8_t* sys = chat_sys();
    s.send_ready = (g_can_send && sys) ? 1 : 0;
    s.players = collect_players(nullptr, 0);
    s.subscribers = g_sub_count.load();
    s.received = g_received.load();
    s.consumed = g_consumed.load();
    s.sent = g_sent.load();
    if (sys && C.inbox_count >= 0) guarded_copy(&s.inbox_count, sys + C.inbox_count, sizeof s.inbox_count);
    *out = s;
    return KILN_OK;
}

KilnResult KILN_CALL inject(uint32_t connection, KilnStr text) {
    if (!text.ptr || !text.len) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_can_send) return KILN_ERR_UNAVAILABLE;
    const size_t len = clip_utf8(text.ptr, text.len, static_cast<size_t>(C.max_text));
    return enqueue(0, text.ptr, len, 0, 0, connection, 0, true) ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

KilnResult KILN_CALL last_line(KilnChatLineInfo* info, char* text, size_t capacity) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_can_send) return KILN_ERR_UNAVAILABLE;
    uint8_t* sys = chat_sys();
    if (!sys) return KILN_ERR_UNAVAILABLE;
    uint8_t* node = nullptr;
    if (!guarded_copy(&node, sys + C.inbox_tail, sizeof node)) return KILN_ERR_INTERNAL;
    if (!node) return KILN_ERR_NOT_FOUND;
    KilnChatLineInfo li{};
    li.size = sizeof li;
    li.type = at<uint8_t>(node, 0);
    li.connection = at<uint32_t>(node, C.n_handle);
    li.account = at<uint64_t>(node, C.n_account);
    li.reader_mask = at<uint32_t>(node, C.n_mask);
    li.text_len = at<uint32_t>(node, C.n_len);
    if (info) *info = li;
    if (text && capacity) {
        const size_t n = std::min<size_t>(li.text_len, capacity - 1);
        if (!guarded_copy(text, node + C.n_text, n)) return KILN_ERR_INTERNAL;
        text[n] = 0;
    }
    return KILN_OK;
}

const KilnChatApi kTable = {{sizeof(KilnChatApi), KILN_CHAT_VERSION},
                            &subscribe,
                            &unsubscribe,
                            &send,
                            &say,
                            &players,
                            &find_player,
                            &status,
                            &inject,
                            &last_line};

bool sym(const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

// A link on `target` in kiln.hooks' chain, labelled like the sigdb symbol so kiln.log and state/hooks.json name it.
bool hook(kiln::RawHook& h, uintptr_t target, void* detour, const char* what, const char* label) {
    if (!h.install(g_api, g_owner, target, detour, label)) {
        logf(KILN_LOG_ERROR, "chat: hooking %s at rva 0x%llx failed (%s)", what,
             static_cast<unsigned long long>(target - g_base), h.error() ? h.error() : "unknown");
        return false;
    }
    return true;
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    info->id = KilnStr{KILN_CHAT_SERVICE, sizeof(KILN_CHAT_SERVICE) - 1};
    info->version = KilnStr{"1", 1};

    uintptr_t add = 0, receive = 0, receive_call = 0, mark = 0;
    if (!sym("chat/ChatLogAdd", &add) || !sym("chat/ServerReceive", &receive) || !cst("chat/gs.chat", &P.gs_chat) ||
        !cst("chat/max_text", &C.max_text)) {
        logf(KILN_LOG_WARN, "chat: ChatLogAdd / ChatReceive unresolved on this build; kiln.chat is off");
        return KILN_ERR_UNAVAILABLE;
    }
    g_can_send = feat("chat.send") && sym("chat/MarkSent", &mark) && cst("chat/sys.inbox_tail", &C.inbox_tail) &&
                 cst("chat/sys.inbox_count", &C.inbox_count) && cst("chat/sys.reader_active", &C.r_active) &&
                 cst("chat/sys.reader_gen", &C.r_gen) && cst("chat/sys.reader_pending", &C.r_pending) &&
                 cst("chat/sys.reader_stride", &C.r_stride) && cst("chat/sys.readers", &C.readers) &&
                 cst("chat/node.len", &C.n_len) && cst("chat/node.handle", &C.n_handle) &&
                 cst("chat/node.account", &C.n_account) && cst("chat/node.mask", &C.n_mask) &&
                 cst("chat/node.text", &C.n_text);
    g_mark_sent = reinterpret_cast<MarkSentFn>(mark);
    g_can_players = feat("chat.players") && cst("chat/gs.players", &P.gs_players) &&
                    cst("chat/gs.player_stride", &P.stride) && cst("chat/gs.players_size", &P.size) &&
                    cst("chat/player.peer", &P.peer) && cst("chat/player.permissions", &P.perms) &&
                    cst("chat/player.name", &P.name) && cst("chat/player.entity", &P.entity) &&
                    cst("chat/player.account", &P.account) && cst("chat/player.state", &P.state);
    const bool receive_ok = feat("chat.receive") && sym("chat/ReceiveAddLine", &receive_call);
    g_receive_ret = receive_ok ? receive_call + 5 : 0;  // return address after the E8 rel32 call

    if (!hook(g_recv_hook, receive, reinterpret_cast<void*>(&hk_chat_receive), "ChatReceive", "chat/ChatReceive"))
        return KILN_ERR_UNAVAILABLE;
    if (receive_ok || g_can_send) {
        if (!hook(g_add_hook, add, reinterpret_cast<void*>(&hk_chat_log_add), "ChatLogAdd", "chat/ChatLogAdd")) {
            g_can_send = false;
        } else {
            g_can_receive = receive_ok;
        }
    }
    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    logf(KILN_LOG_INFO, "chat: hooks on ChatReceive rva 0x%llx, ChatLogAdd rva 0x%llx (receive call rva 0x%llx); "
         "receive %s, send %s, players %s",
         static_cast<unsigned long long>(receive - g_base), static_cast<unsigned long long>(add - g_base),
         static_cast<unsigned long long>(receive_call ? receive_call - g_base : 0), g_can_receive ? "on" : "off",
         g_can_send ? "on" : "off", g_can_players ? "on" : "off");
    return api->register_service(owner, KilnStr{KILN_CHAT_SERVICE, sizeof(KILN_CHAT_SERVICE) - 1}, &kTable);
}
