// SPDX-License-Identifier: MIT
// kiln.chat and kiln-emf on ONE function, in the real kiln.hooks v2 registry, under Wine, no game.
//
// The dev.20 review found that kiln-emf's ChatLogAdd hook, layered in front of kiln.chat's safetyhook detour, made
// kiln.chat's `_ReturnAddress() == receive call + 5` test see kiln-emf's detour instead of the game's receive loop: players'
// lines were no longer held back and never reached mods. Both now are raw links in one chain and kiln.chat reads
// caller(). This test builds the production code of both sides:
//   - native/services/chat/chat.cpp (kiln_plugin_main, its hooks, Pre delivery, inject),
//   - native/plugins/emf/src/api_net_client.cpp (net_client_install: ChatLogAdd after kiln.chat, ClientOutboxFlush),
//   - the registry engine core/src/services/hooks_chain*.cpp (the C table below is the three entries this test needs),
// against a stand-in game: a "receive loop" that adds a player's line to the chat inbox with ChatLogAdd from one call site,
// and other call sites that add notices and EMF-marked lines.
//
//   native/services/chat/tests/run.sh       runs the modes below, one process each (chat.cpp keeps process-wide state)
//   chat_layers_test.exe chat-first         kiln.chat links first, then kiln-emf's
//   chat_layers_test.exe emf-first          kiln-emf's link first (its `after kiln.chat` puts kiln.chat in front)
//   chat_layers_test.exe front              an extra link (an EMF mod's hook) in front of both, installed first
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "emf_internal.h"
#include "net/net_line.h"
#include "net_bridge.h"
#include "kiln_hook.hpp"
#include "services/hooks_chain.h"
#include "services/kiln_chat.h"

namespace chain = kiln::hooks::chain;

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info);

// ---------------------------------------------------------------------------------------------------------------------
// Stubs for the rest of kiln-core (the registry engine needs only its log)
namespace {
std::mutex g_log_mutex;
std::vector<std::string> g_lines;
}  // namespace
namespace kiln::log {
void writef(int level, const char*, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_log_mutex);
    g_lines.push_back(std::string(level == KILN_LOG_ERROR ? "E " : level == KILN_LOG_WARN ? "W " : "I ") + buf);
}
}  // namespace kiln::log
namespace kiln {
std::string to_utf8(std::wstring_view w) {
    if (w.empty()) return {};
    std::string s(static_cast<size_t>(WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0,
                                                          nullptr, nullptr)),
                  0);
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), static_cast<int>(s.size()), nullptr,
                        nullptr);
    return s;
}
std::string utc_now() { return "2026-01-01T00:00:00Z"; }
}  // namespace kiln

// ---------------------------------------------------------------------------------------------------------------------
// Stubs for kiln-emf's internals that api_net_client.cpp uses (net_test.cpp has the full host versions)
namespace kemf {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uint32_t g_side = KILN_SIDE_CLIENT;
std::atomic<int> g_marked_taken{0};
std::vector<std::string> g_taken_lines;
std::mutex g_taken_mutex;
void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_log_mutex);
    g_lines.push_back(std::string(level == KILN_LOG_WARN ? "W emf: " : "I emf: ") + buf);
}
bool seh_copy(void* dst, const void* src, size_t n) {
    if (!dst || !src) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
// The EMF side of a marked line: counted and recorded, the line is "for this client".
bool net_client_line(std::string_view line, uint64_t) {
    std::lock_guard lock(g_taken_mutex);
    g_taken_lines.emplace_back(line);
    ++g_marked_taken;
    return true;
}
std::vector<std::string> net_client_take(size_t) { return {}; }
void net_client_put_back(std::vector<std::string>) {}
void net_client_reset() {}
}  // namespace kemf

// ---------------------------------------------------------------------------------------------------------------------
// The game: the chat inbox function and the places that call it
namespace {
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

struct Line {
    int type;
    std::string text;
    uint8_t* sys;
    uint32_t handle;
    uint64_t account;
};
std::mutex g_game_mutex;
std::vector<Line> g_inbox;                // lines the game's own ChatLogAdd really added
std::atomic<int> g_flushes{0}, g_sends{0};
// The stand-in functions USE every argument they are given: clang knows an unused argument of an external function is
// dead in this program and then does not load its register at the call (the hook would see garbage).
uint8_t* volatile g_last_gc = nullptr;
uint8_t g_sys_storage[512];               // the "ChatMessagesSystem" the hooks pass around
uint8_t* const g_sys = g_sys_storage + 64;
uint8_t g_gs_storage[512];                // the "GameServer": +0x20 holds the chat system pointer (chat/gs.chat)
uint8_t g_client_storage[0x110];          // the "GameClient": +0xF8 holds the chat system pointer
}  // namespace

extern "C" {
// ChatLogAdd(result, sys, type, text, tag1, tag2, connection, account, time): 4 register + 5 stack arguments.
__declspec(noinline) ChatResult* game_chat_log_add(ChatResult* out, uint8_t* sys, uint8_t type, const StrView* text,
                                                   uint32_t tag1, uint32_t tag2, uint32_t handle, uint64_t account,
                                                   const OptTime* time) {
    {
        std::lock_guard lock(g_game_mutex);
        g_inbox.push_back({type, std::string(text->data, static_cast<size_t>(text->size)), sys, handle, account});
    }
    out->code = 0;
    out->value = tag1 + tag2 + static_cast<uint32_t>(time->has);
    return out;
}

// The receive loop: a player's typed line enters the inbox from THIS call site (the return address kiln.chat looks for).
__declspec(noinline) uintptr_t game_chat_receive(uint8_t* gs, uintptr_t line, uintptr_t kind, uintptr_t connection) {
    const char* text = reinterpret_cast<const char*>(line);
    ChatResult r{};
    StrView v{text, std::strlen(text)};
    OptTime t{};
    uint8_t* sys = nullptr;
    std::memcpy(&sys, gs + 0x20, sizeof sys);
    game_chat_log_add(&r, sys, static_cast<uint8_t>(kind), &v, 0, 0, static_cast<uint32_t>(connection), 4242, &t);
    return r.code + 1000;  // used after the call: no tail jump
}

// Another place that adds lines (the game's own notices, the client's network receive): never the receive loop.
__declspec(noinline) uintptr_t game_chat_notice(uint8_t* sys, const char* text, uint8_t type) {
    ChatResult r{};
    StrView v{text, std::strlen(text)};
    OptTime t{};
    game_chat_log_add(&r, sys, type, &v, 0, 0, 7, 99, &t);
    return r.code + 2000;
}

__declspec(noinline) void game_client_flush(uint8_t* gc) {
    g_last_gc = gc;
    ++g_flushes;
}
__declspec(noinline) uint8_t* game_client_send(uint8_t* out, uint8_t* sys, uint8_t kind, const void* text, uint32_t a,
                                               uint32_t b) {
    g_last_gc = sys;
    ++g_sends;
    out[0] = static_cast<uint8_t>(kind + a + b + (text ? 0 : 1));
    out[0] = 0;
    return out;
}
}

// ---------------------------------------------------------------------------------------------------------------------
// A kiln.hooks v2 table on the real registry engine (hooks.cpp is the production wrapper; its stubs are larger)
namespace {
chain::Order order_of(const KilnHookOrder* o) {
    chain::Order r;
    if (o && o->size >= sizeof(KilnHookOrder)) {
        r.priority = o->priority;
        r.label = std::string(o->label.ptr ? o->label.ptr : "", o->label.ptr ? o->label.len : 0);
        r.before = std::string(o->before.ptr ? o->before.ptr : "", o->before.ptr ? o->before.len : 0);
        r.after = std::string(o->after.ptr ? o->after.ptr : "", o->after.ptr ? o->after.len : 0);
    }
    return r;
}
uintptr_t g_image_lo = 0, g_image_hi = 0;
KilnResult KILN_CALL t_raw_detour(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, void* fn, void** original,
                                  KilnHandle* out) {
    if (!fn || target < g_image_lo || target >= g_image_hi) return KILN_ERR_INVALID_ARGUMENT;
    return chain::install_raw(owner, target, order_of(order), fn, original, out);
}
uintptr_t KILN_CALL t_caller(KilnHandle h) { return chain::caller_of(h); }
KilnResult KILN_CALL t_revoke(KilnHandle h) { return chain::revoke(h); }

KilnHooksApi make_hooks() {
    KilnHooksApi t{};
    t.header = {sizeof(KilnHooksApi), KILN_HOOKS_VERSION};
    t.raw_detour = &t_raw_detour;
    t.caller = &t_caller;
    t.revoke = &t_revoke;
    return t;
}
const KilnHooksApi g_hooks_table = make_hooks();

std::map<KilnOwner, std::string> g_owner_names = {{0, "kiln"}, {1, "service:kiln-chat.dll"}, {2, "plugin:kiln-emf.dll"},
                                                  {3, "emf:frontmod"}};
std::string owner_name(KilnOwner o) {
    auto it = g_owner_names.find(o);
    return it == g_owner_names.end() ? "(revoked)" : it->second;
}

// ---------------------------------------------------------------------------------------------------------------------
// A KilnApi for the two plugins
std::mutex g_api_mutex;
const void* g_chat_table = nullptr;
KilnTickFn g_pre = nullptr;
std::vector<std::string> g_kiln_log;

KilnStr KILN_CALL a_version() { return KilnStr{"0.0.0-chat-layers", 17}; }
void KILN_CALL a_log(KilnOwner, int32_t level, KilnStr text) {
    std::lock_guard lock(g_api_mutex);
    g_kiln_log.push_back(std::string(level == KILN_LOG_ERROR ? "E " : level == KILN_LOG_WARN ? "W " : "I ") +
                         std::string(text.ptr, text.len));
}
KilnResult KILN_CALL a_build_info(KilnBuildInfo* out) {
    // kiln-emf's client hooks need a verified, hook-enabled build whose exe hash is on its allowlist (1285601 here)
    std::strcpy(out->exe_sha256, "ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618");
    out->verified = 1;
    out->hooks_enabled = 1;
    out->side = KILN_SIDE_CLIENT;
    return KILN_OK;
}
bool KILN_CALL a_feature(KilnStr name) {
    const std::string n(name.ptr, name.len);
    return n == "chat.receive" || n == "chat.send";
}
KilnResult KILN_CALL a_symbol(KilnStr name, uintptr_t* out) {
    const std::string n(name.ptr, name.len);
    uintptr_t v = 0;
    if (n == "chat/ChatLogAdd") v = reinterpret_cast<uintptr_t>(&game_chat_log_add);
    else if (n == "chat/ServerReceive") v = reinterpret_cast<uintptr_t>(&game_chat_receive);
    else if (n == "chat/MarkSent") v = reinterpret_cast<uintptr_t>(&game_chat_notice);
    else if (n == "emf/ClientOutboxFlush") v = reinterpret_cast<uintptr_t>(&game_client_flush);
    else if (n == "emf/ChatClientSend") v = reinterpret_cast<uintptr_t>(&game_client_send);
    else if (n == "chat/ReceiveAddLine") {
        // the `call rel32` to ChatLogAdd inside the receive loop (E8 + rel32; the return address is the call + 5)
        const auto* p = reinterpret_cast<const uint8_t*>(&game_chat_receive);
        for (size_t i = 0; i + 5 < 256 && !v; ++i) {  // the first one: the function after it has a call of its own
            int32_t rel;
            std::memcpy(&rel, p + i + 1, 4);
            if (p[i] == 0xE8 && reinterpret_cast<uintptr_t>(p + i + 5) + static_cast<intptr_t>(rel) ==
                                     reinterpret_cast<uintptr_t>(&game_chat_log_add))
                v = reinterpret_cast<uintptr_t>(p + i);
        }
    }
    if (!v) return KILN_ERR_NOT_FOUND;
    *out = v;
    return KILN_OK;
}
KilnResult KILN_CALL a_constant(KilnStr name, int64_t* out) {
    static const std::map<std::string, int64_t> k = {
        {"chat/gs.chat", 0x20},       {"chat/max_text", 1024},       {"chat/sys.inbox_tail", 8},
        {"chat/sys.inbox_count", 16}, {"chat/sys.reader_active", 32}, {"chat/sys.reader_gen", 36},
        {"chat/sys.reader_pending", 40}, {"chat/sys.reader_stride", 16}, {"chat/sys.readers", 2},
        {"chat/node.len", 8},         {"chat/node.handle", 12},      {"chat/node.account", 16},
        {"chat/node.mask", 24},       {"chat/node.text", 28}};
    auto it = k.find(std::string(name.ptr, name.len));
    if (it == k.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second;
    return KILN_OK;
}
const void* KILN_CALL a_service(KilnStr name, uint32_t min_version) {
    if (std::string(name.ptr, name.len) == KILN_HOOKS_SERVICE && min_version <= KILN_HOOKS_VERSION) return &g_hooks_table;
    return nullptr;
}
KilnResult KILN_CALL a_register_service(KilnOwner, KilnStr name, const void* table) {
    if (std::string(name.ptr, name.len) == KILN_CHAT_SERVICE) g_chat_table = table;
    return KILN_OK;
}
KilnResult KILN_CALL a_subscribe_tick(KilnOwner, int32_t phase, KilnTickFn fn, void*, KilnHandle* out) {
    if (phase == KILN_TICK_PRE) g_pre = fn;
    if (out) *out = 1;
    return KILN_OK;
}
KilnResult KILN_CALL a_subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle* out) {
    if (out) *out = 2;
    return KILN_OK;
}
bool KILN_CALL a_on_tick_thread() { return true; }
KilnOwner KILN_CALL a_owner_create(KilnStr) { return 9; }

KilnApi make_api() {
    KilnApi a{};
    a.size = sizeof a;
    a.abi_version = KILN_ABI_VERSION;
    a.version = &a_version;
    a.log = &a_log;
    a.build_info = &a_build_info;
    a.feature = &a_feature;
    a.symbol = &a_symbol;
    a.constant = &a_constant;
    a.service = &a_service;
    a.register_service = &a_register_service;
    a.subscribe_tick = &a_subscribe_tick;
    a.subscribe_world = &a_subscribe_world;
    a.on_tick_thread = &a_on_tick_thread;
    a.owner_create = &a_owner_create;
    return a;
}
KilnApi g_api = make_api();

// ---------------------------------------------------------------------------------------------------------------------
int g_fail = 0, g_pass = 0;
void check(bool ok, const char* what, const std::string& detail = "") {
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", what, detail.empty() ? "" : ": ", detail.c_str());
    std::fflush(stdout);
    ok ? ++g_pass : ++g_fail;
}

// A subscriber of kiln.chat (a mod): records the lines it is handed; consumes the ones that say "swallow".
std::vector<std::string> g_heard;
bool KILN_CALL on_chat(const KilnChatMessage* m, void*) {
    std::string t(m->text.ptr, m->text.len);
    g_heard.push_back(t);
    return t.find("swallow") != std::string::npos;
}

size_t inbox_size() {
    std::lock_guard lock(g_game_mutex);
    return g_inbox.size();
}
std::string inbox_text(size_t i) {
    std::lock_guard lock(g_game_mutex);
    return i < g_inbox.size() ? g_inbox[i].text : std::string("<none>");
}
bool contains(const std::string& label_order, const std::string& want) { return label_order.find(want) != std::string::npos; }

// A pass-through raw link, as an EMF mod's `hook("ChatLogAdd", ...)` would be: ahead of the others.
void* g_front_original = nullptr;
std::atomic<int> g_front_calls{0};
KilnHandle g_front_handle = 0;
std::mutex g_caller_mutex;
std::vector<uintptr_t> g_front_callers;
ChatResult* front_hook(ChatResult* out, uint8_t* sys, uint8_t type, const StrView* text, uint32_t tag1, uint32_t tag2,
                       uint32_t handle, uint64_t account, const OptTime* time) {
    ++g_front_calls;
    {
        const uintptr_t c = chain::caller_of(g_front_handle);
        std::lock_guard lock(g_caller_mutex);
        if (g_front_callers.size() < 8) g_front_callers.push_back(c);
    }
    return reinterpret_cast<ChatResult* (*)(ChatResult*, uint8_t*, uint8_t, const StrView*, uint32_t, uint32_t, uint32_t,
                                            uint64_t, const OptTime*)>(g_front_original)(out, sys, type, text, tag1, tag2,
                                                                                         handle, account, time);
}

std::string order_labels() {
    std::string s;
    for (KilnHandle h : chain::chain_of(reinterpret_cast<uintptr_t>(&game_chat_log_add))) {
        chain::LinkInfo li;
        if (chain::link_info(h, &li)) s += (s.empty() ? "" : ",") + li.label;
    }
    return s;
}
}  // namespace

int main(int argc, char** argv) {
    const std::string mode = argc > 1 ? argv[1] : "chat-first";
    std::printf("chat_layers_test: mode %s\n", mode.c_str());
    auto* base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
    g_image_lo = reinterpret_cast<uintptr_t>(base);
    g_image_hi = g_image_lo + nt->OptionalHeader.SizeOfImage;

    chain::Env env;
    env.owner_name = &owner_name;
    env.image_lo = g_image_lo;
    env.image_hi = g_image_hi;
    if (!chain::init(env)) {
        std::puts("FAIL the registry engine did not initialise");
        return 1;
    }
    kemf::g_api = &g_api;
    kemf::g_owner = 2;
    const uintptr_t add = reinterpret_cast<uintptr_t>(&game_chat_log_add);
    const uintptr_t receive_site = [] {
        uintptr_t v = 0;
        a_symbol(KilnStr{"chat/ReceiveAddLine", 19}, &v);
        return v;
    }();
    check(receive_site != 0, "the stand-in receive loop has a call rel32 to ChatLogAdd");

    // GameServer+0x20 (chat/gs.chat) and GameClient+0xF8 hold the chat system pointer
    uint8_t* sys = g_sys;
    std::memcpy(g_gs_storage + 0x20, &sys, sizeof sys);
    std::memcpy(g_client_storage + 0xF8, &sys, sizeof sys);

    // ---- install order: the three modes ----
    KilnHandle front_handle = 0;
    auto install_front = [&] {
        KilnHookOrder o{};
        o.size = sizeof o;
        o.priority = 5;
        o.label = KilnStr{"emfmod/ChatLogAdd", 17};
        check(chain::install_raw(3, add, order_of(&o), reinterpret_cast<void*>(&front_hook), &g_front_original, &front_handle) ==
                  KILN_OK,
              "an EMF mod's hook (priority 5) goes in front");
        g_front_handle = front_handle;
    };
    auto install_chat = [&] {
        KilnPluginInfo info{};
        info.size = sizeof info;
        check(kiln_plugin_main(&g_api, 1, &info) == KILN_OK && g_chat_table, "kiln.chat starts and registers its service");
    };
    auto install_emf = [&] {
        check(kemf::net_client_install(), "kiln-emf installs its client hooks (ChatLogAdd, ClientOutboxFlush)");
    };
    if (mode == "front") install_front();
    if (mode == "emf-first") {
        install_emf();
        install_chat();
    } else {
        install_chat();
        install_emf();
    }
    const std::string want_order = mode == "front" ? "emfmod/ChatLogAdd,chat/ChatLogAdd,emf/ChatLogAdd"
                                                   : "chat/ChatLogAdd,emf/ChatLogAdd";
    check(order_labels() == want_order, "chain on ChatLogAdd: kiln.chat's link, then kiln-emf's (after kiln.chat)",
          order_labels());

    const auto* chat = static_cast<const KilnChatApi*>(g_chat_table);
    KilnHandle sub = 0;
    check(chat && chat->subscribe(5, &on_chat, nullptr, &sub) == KILN_OK, "a mod subscribes to players' lines");
    KilnChatStatus st{};
    st.size = sizeof st;
    check(chat->status(&st) == KILN_OK && st.receive_hooked == 1 && st.subscribers == 1, "kiln.chat reports receive hooked");
    check(g_pre != nullptr, "kiln.chat subscribed to Pre");

    // ---- 1. a player's line through the receive loop is held back, handed to the mod, then added ----
    const uintptr_t r1 = game_chat_receive(g_gs_storage, reinterpret_cast<uintptr_t>("hello there"), 0, 0x41);
    check(r1 == 1000, "the receive loop got its success result", std::to_string(r1));
    check(inbox_size() == 0 && g_heard.empty(),
          "player line held back: kiln.chat saw the receive loop as the caller (caller(), not _ReturnAddress())",
          "inbox " + std::to_string(inbox_size()));
    g_pre(nullptr, nullptr);
    check(g_heard.size() == 1 && g_heard[0] == "hello there", "the subscriber got the line in Pre");
    check(inbox_size() == 1 && inbox_text(0) == "hello there",
          "not consumed: added back through the rest of the chain and the game's function");
    {
        std::lock_guard lock(g_game_mutex);
        check(!g_inbox.empty() && g_inbox[0].sys == g_sys && g_inbox[0].handle == 0x41 && g_inbox[0].account == 4242,
              "the game's function got the original arguments (system, connection, account)");
    }

    // ---- 2. other call sites are never held back ----
    game_chat_notice(g_sys, "Bob joined.", 4);
    game_chat_notice(g_sys, "typed somewhere else", 0);
    check(inbox_size() == 3 && inbox_text(1) == "Bob joined." && inbox_text(2) == "typed somewhere else" && g_heard.size() == 1,
          "notices and type-0 lines from other call sites pass straight through");

    // ---- 3. a subscriber consumes a line ----
    game_chat_receive(g_gs_storage, reinterpret_cast<uintptr_t>("please swallow this"), 0, 0x41);
    g_pre(nullptr, nullptr);
    check(g_heard.size() == 2 && inbox_size() == 3, "a consumed line never reaches the game's chat");

    // ---- 4. kiln-emf's client hooks work in the same chain ----
    game_client_flush(g_client_storage);
    check(g_flushes.load() == 1, "ClientOutboxFlush: the game's function still ran through the link");
    const std::string marked = "#!emf1 M 1 test/x@1 0/1 t hi";
    game_chat_notice(g_sys, marked.c_str(), 0);
    check(kemf::g_marked_taken.load() == 1 && inbox_size() == 3,
          "an EMF-marked line (another call site) is taken by kiln-emf's link after kiln.chat's",
          "taken " + std::to_string(kemf::g_marked_taken.load()) + ", inbox " + std::to_string(inbox_size()) +
              ", is_marked " + std::to_string(emf::core::net::is_marked(marked)));
    game_chat_notice(g_sys, "plain line", 0);
    check(kemf::g_marked_taken.load() == 1 && inbox_size() == 4 && inbox_text(3) == "plain line",
          "an ordinary line passes kiln-emf's link into the game");

    // ---- 5. a marked line a player typed: held back by kiln.chat, then taken by kiln-emf in Pre's redelivery ----
    game_chat_receive(g_gs_storage, reinterpret_cast<uintptr_t>(marked.c_str()), 0, 0x41);
    check(inbox_size() == 4 && kemf::g_marked_taken.load() == 1, "typed marked line held back first");
    g_pre(nullptr, nullptr);
    check(g_heard.size() == 3 && kemf::g_marked_taken.load() == 2 && inbox_size() == 4,
          "redelivery goes through the rest of the chain: kiln-emf's link takes it");

    // ---- 6. threads: caller() is per thread ----
    {
        const size_t inbox_before = inbox_size(), heard_before = g_heard.size();
        std::atomic<int> notices{0};
        std::vector<std::thread> th;
        for (int t = 0; t < 4; ++t)
            th.emplace_back([&, t] {
                char text[64];
                for (int i = 0; i < 50; ++i) {
                    std::snprintf(text, sizeof text, "T%d line %d", t, i);
                    game_chat_receive(g_gs_storage, reinterpret_cast<uintptr_t>(text), 0, 0x41);
                    game_chat_notice(g_sys, "other site", 0);
                    ++notices;
                }
            });
        for (auto& x : th) x.join();
        size_t typed_in_inbox = 0;
        {
            std::lock_guard lock(g_game_mutex);
            for (size_t i = inbox_before; i < g_inbox.size(); ++i) typed_in_inbox += g_inbox[i].text[0] == 'T';
        }
        check(inbox_size() == inbox_before + static_cast<size_t>(notices.load()) && typed_in_inbox == 0,
              "4 threads x 50: every receive-loop line held back, every other-site line added at once",
              "inbox +" + std::to_string(inbox_size() - inbox_before) + " (want +" + std::to_string(notices.load()) +
                  "), typed lines already added: " + std::to_string(typed_in_inbox));
        g_pre(nullptr, nullptr);
        check(g_heard.size() == heard_before + 200 && inbox_size() == inbox_before + static_cast<size_t>(notices.load()) + 200,
              "all 200 held lines were delivered and added", "heard +" + std::to_string(g_heard.size() - heard_before));
    }
    if (mode == "front") check(g_front_calls.load() > 0, "the mod link in front saw the calls");
    if (mode == "front")
        for (uintptr_t c : g_front_callers) std::printf("  front caller %p (receive site+5 = %p)\n", reinterpret_cast<void*>(c), reinterpret_cast<void*>(receive_site + 5));

    // ---- log ----
    {
        std::lock_guard lock(g_api_mutex);
        bool on = false, bad = false;
        for (const auto& l : g_kiln_log) {
            if (l.find("chat: hooks on ChatReceive") != std::string::npos) on = true;
            if (l[0] == 'E') bad = true, std::printf("  unexpected kiln.log: %s\n", l.c_str());
        }
        check(on, "kiln.chat logged its hooks line");
        check(!bad, "no error lines");
    }
    {
        std::lock_guard lock(g_log_mutex);
        for (const auto& l : g_lines) std::printf("  log: %s\n", l.c_str());
    }
    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    std::puts(g_fail ? "FAILED" : "all passed");
    std::fflush(stdout);
    return g_fail ? 1 : 0;
}
