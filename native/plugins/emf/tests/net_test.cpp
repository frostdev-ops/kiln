// SPDX-License-Identifier: MIT
// Production API/engine bridges against fake Kiln services and an upstream EMF ClientLink wire peer.
#include "emf_internal.h"
#include "net_bridge.h"
#include "net/net_link.h"
#include <array>
#include <cstdio>
#include <stdexcept>
#include <thread>
#include <semaphore>

static int checks = 0, failures = 0;
#define CHECK(x)                                                                                                       \
    do {                                                                                                               \
        ++checks;                                                                                                      \
        if (!(x)) {                                                                                                    \
            ++failures;                                                                                                \
            std::printf("FAIL %d: %s\n", __LINE__, #x);                                                                \
        }                                                                                                              \
    } while (0)
namespace kemf {
const KilnApi *g_api = nullptr;
KilnOwner g_owner = 1;
uint32_t g_side = KILN_SIDE_CLIENT;
Services S;
Paths P;
thread_local Context t_ctx;
static Mod mod, other;
static Mod *caller = &mod;
Mod *caller_mod(const void *) { return caller; }
Mod *mod_of_address(const void *) { return caller; }
void logf(int, const char *, ...) {}
void mod_logf(const Mod *, int, const char *, ...) {}
void refuse(const Mod *, const char *, const char *, bool) {}
void account(Mod *, uint64_t) {}
uint64_t qpc_ns() { return 0; }
static uint64_t now = 1000;
uint64_t now_ms() { return now; }
bool seh_copy(void *dst, const void *src, size_t n) {
    if (!dst || !src)
        return false;
    std::memcpy(dst, src, n);
    return true;
}
bool guarded(Mod *, const char *, ThunkFn fn, void *p) {
    try {
        fn(p);
        return true;
    } catch (...) {
        return false;
    }
}
std::string lower(std::string_view v) {
    std::string s(v);
    for (char &c : s)
        if (c >= 'A' && c <= 'Z')
            c += 32;
    return s;
}
bool read_file(const std::filesystem::path &, std::string *) { return false; }
std::string path_utf8(const std::filesystem::path &p) { return p.string(); }
static bool correct_thread = true;
bool on_thread_of(EmfWorld *) { return correct_thread; }
static EmfWorld *ws = reinterpret_cast<EmfWorld *>(0x1000);
static EmfWorld *wc = reinterpret_cast<EmfWorld *>(0x2000);
uint8_t world_kind_of(EmfWorld *w) { return w == ws ? EMF_WORLD_SERVER : w == wc ? EMF_WORLD_CLIENT : 0xff; }
EmfWorld *world_of_kind(uint32_t k) { return k == EMF_WORLD_SERVER ? ws : k == EMF_WORLD_CLIENT ? wc : nullptr; }
bool commands_is_admin(uint32_t p, uint64_t a) { return p == 7 && a == 123; }
} // namespace kemf
using namespace kemf;
namespace wire = emf::core::net;
static EmfApi api{};
static KilnChatReceiveFn receive = nullptr;
static KilnChatPlayer person{};
static uint32_t session = KILN_SESSION_HOST;
static std::array<uint8_t, 0xA0 * 7> infos{};
static std::vector<std::string> server_lines;
static bool subscribed = false;
static KilnDetourFn observe = nullptr;
static std::array<uint8_t, 0x40> game_server{};
static std::array<uint8_t, 0x200> chat_system{};
static KilnResult symbol(KilnStr, uintptr_t *p) {
    *p = 1;
    return KILN_OK;
}
static KilnResult constant(KilnStr name, int64_t *p) {
    auto n = sv(name);
    *p = n == "chat/gs.chat" ? 0x20 : n == "chat/sys.reader_pending" ? 0x14 : 12;
    return KILN_OK;
}
static KilnResult detour(KilnOwner, uintptr_t, int32_t, KilnDetourFn cb, void *, KilnHandle *h) {
    observe = cb;
    *h = 2;
    return KILN_OK;
}
static KilnResult revoke(KilnHandle) {
    observe = nullptr;
    return KILN_OK;
}
static uintptr_t original(KilnHookCall *) { return 0; }
static void captured_server() {
    KilnHookCall call{};
    call.arg_count = 1;
    call.args[0] = reinterpret_cast<uintptr_t>(game_server.data());
    observe(&call, nullptr);
}
static KilnResult client_state(KilnClientState *c) {
    c->local_player = 7;
    c->session = session;
    return KILN_OK;
}
static KilnResult chat_players(KilnChatPlayer *out, uint32_t cap, uint32_t *n) {
    *n = person.connection ? 1 : 0;
    if (cap && *n)
        *out = person;
    return KILN_OK;
}
static KilnResult find_player(uint32_t e, KilnChatPlayer *out) {
    if (e != person.entity || !person.connection)
        return KILN_ERR_NOT_FOUND;
    *out = person;
    return KILN_OK;
}
static KilnResult chat_status(KilnChatStatus *s) {
    s->receive_hooked = 1;
    s->send_ready = 1;
    return KILN_OK;
}
static KilnResult subscribe(KilnOwner, KilnChatReceiveFn fn, void *, KilnHandle *h) {
    receive = fn;
    *h = 1;
    subscribed = true;
    return KILN_OK;
}
static KilnResult unsubscribe(KilnHandle) {
    subscribed = false;
    return KILN_OK;
}
static KilnResult say(uint32_t who, KilnStr text, uint32_t to) {
    CHECK(who == 7 && to == 7);
    server_lines.emplace_back(text.ptr, text.len);
    return KILN_OK;
}
static uintptr_t singleton(uintptr_t, KilnStr) { return reinterpret_cast<uintptr_t>(infos.data()); }
struct Got {
    EmfWorld *w;
    EmfNetFrom from;
    std::string bytes;
};
static std::vector<Got> got_server, got_client;
static bool stop_from_callback = false;
static std::atomic<bool> block_callback{false};
static std::binary_semaphore entered(0), release_callback(0);
static void server_cb(EmfWorld *w, const EmfNetFrom *f, const void *d, uint32_t n, void *) {
    CHECK(t_ctx.world == w && t_ctx.world_cb);
    got_server.push_back({w, *f, std::string(static_cast<const char *>(d), n)});
    if (block_callback.exchange(false)) {
        entered.release();
        release_callback.acquire();
    }
    if (stop_from_callback)
        net_shutdown();
}
static void client_cb(EmfWorld *w, const EmfNetFrom *f, const void *d, uint32_t n, void *) {
    got_client.push_back({w, *f, std::string(static_cast<const char *>(d), n)});
}
static void boom(EmfWorld *, const EmfNetFrom *, const void *, uint32_t, void *) { throw std::runtime_error("fault"); }
static EmfNetChannel channel(const char *n, uint32_t flags = 3, uint32_t rate = 100) {
    EmfNetChannel d{};
    d.size = sizeof d;
    d.name = n;
    d.version = 3;
    d.flags = flags;
    d.max_size = 16384;
    d.per_10s = rate;
    d.on_server = server_cb;
    d.on_player = client_cb;
    return d;
}
static void inject(std::string_view line, uint64_t account = 123, uint32_t handle = 71) {
    KilnChatMessage m{};
    m.size = sizeof m;
    m.sender = person;
    m.sender.account = account;
    m.sender.connection = handle;
    m.text = {line.data(), line.size()};
    CHECK(receive(&m, nullptr));
}
static void init() {
    mod.id = "test";
    other.id = "other";
    caller = &mod;
    person.size = sizeof person;
    person.entity = 7;
    person.connection = 71;
    person.account = 123;
    person.reader = 1;
    uint32_t id = 7;
    uint64_t a = 123;
    std::memcpy(infos.data() + 6 * 0xA0, &id, 4);
    std::memcpy(infos.data() + 6 * 0xA0 + 8, &a, 8);
    static KilnApi k{};
    k.singleton = singleton;
    k.symbol = symbol;
    k.constant = constant;
    g_api = &k;
    auto *sys = chat_system.data();
    std::memcpy(game_server.data() + 0x20, &sys, sizeof sys);
    static KilnHooksApi h{};
    h.detour = detour;
    h.revoke = revoke;
    h.call_original = original;
    S.hooks = &h;
    static KilnClientApi c{};
    c.header.size = sizeof c;
    c.state = client_state;
    S.client = &c;
    static KilnChatApi chat{};
    chat.header.size = sizeof chat;
    chat.subscribe = subscribe;
    chat.unsubscribe = unsubscribe;
    chat.players = chat_players;
    chat.find_player = find_player;
    chat.status = chat_status;
    chat.say = say;
    S.chat = &chat;
    fill_net(&api);
    net_on_ready();
    captured_server();
}
static void loopback() {
    auto up = channel("test/up", EMF_NET_TO_SERVER);
    auto down = channel("test/down", EMF_NET_TO_PLAYERS);
    CHECK(api.net_channel(&up));
    CHECK(api.net_channel(&down));
    CHECK(!api.net_channel(&up));
    auto wrong = channel("other/pick");
    CHECK(!api.net_channel(&wrong));
    auto bad = up;
    bad.name = "test/bad";
    bad.version = 0;
    CHECK(!api.net_channel(&bad));
    bad = up;
    bad.name = "test/bad";
    bad.flags = 8;
    CHECK(!api.net_channel(&bad));
    bad = up;
    bad.name = "test/bad";
    bad.size = 8;
    CHECK(!api.net_channel(&bad));
    CHECK(!api.net_channel(nullptr));
    mod.id = "manifest_id";
    mod.dll = "test.dll";
    auto file_channel = channel("test/from_file");
    CHECK(api.net_channel(&file_channel));
    auto manifest_channel = channel("manifest_id/from_file");
    CHECK(!api.net_channel(&manifest_channel));
    CHECK(api.net_to_server("test/from_file", "file owner", 10));
    mod.id = "test";
    mod.dll.clear();
    net_after_step(ws, EMF_WORLD_SERVER);
    got_server.clear();
    CHECK(api.net_to_server("test/up", "abc", 3));
    CHECK(got_server.empty());
    net_after_step(wc, EMF_WORLD_CLIENT);
    CHECK(got_server.empty());
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == 1);
    CHECK(got_server[0].w == ws && got_server[0].bytes == "abc" && got_server[0].from.player == 7);
    CHECK(got_server[0].from.local && got_server[0].from.admin && !got_server[0].from.account &&
          got_server[0].from.peer_version == 3);
    caller = &other;
    CHECK(!api.net_to_server("test/up", "x", 1));
    caller = &mod;
    CHECK(!api.net_to_server("test/down", "x", 1));
    CHECK(!api.net_to_server("test/up", nullptr, 1));
    CHECK(!api.net_to_server("test/up", "x", 16385));
    CHECK(!api.net_to_player(ws, 7, "test/down", "x", 1)); // not in a world callback
    {
        ContextScope c(&mod, ws, true);
        CHECK(api.net_to_player(ws, 7, "test/down", "ok", 2));
        CHECK(!api.net_to_player(wc, 7, "test/down", "x", 1));
        CHECK(api.net_to_players(ws, "test/down", "all", 3) == 1);
    }
    {
        ContextScope c(&mod, ws, true);
        correct_thread = false;
        CHECK(!api.net_to_player(ws, 7, "test/down", "x", 1));
        CHECK(api.net_peer(ws, 7, "test/down") == 3);
        correct_thread = true;
    }
    CHECK(got_client.empty());
    net_after_step(wc, EMF_WORLD_CLIENT);
    CHECK(got_client.size() == 2);
    CHECK(got_client[0].w == wc && got_client[0].from.local && got_client[0].bytes == "ok");
    auto rate = channel("test/rate", 1, 2);
    CHECK(api.net_channel(&rate));
    CHECK(api.net_to_server(rate.name, "a", 1));
    CHECK(api.net_to_server(rate.name, "b", 1));
    CHECK(!api.net_to_server(rate.name, "c", 1));
    now += 10000;
    CHECK(api.net_to_server(rate.name, nullptr, 0));
    net_world_stopping(EMF_WORLD_SERVER);
    net_world_started(EMF_WORLD_SERVER);
    const auto before = got_server.size();
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == before);
    auto fault = channel("test/fault", 1);
    fault.on_server = boom;
    CHECK(api.net_channel(&fault));
    CHECK(api.net_to_server(fault.name, "x", 1));
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(api.net_to_server(fault.name, "y", 1));
    net_after_step(ws, EMF_WORLD_SERVER); // disabled faulting callback
    const auto before_expired = got_server.size();
    CHECK(api.net_to_server("test/up", "expired", 7));
    now += 30001;
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == before_expired);
}
static void framing() {
    std::string bytes(3000, '\0');
    for (size_t i = 0; i < bytes.size(); ++i)
        bytes[i] = static_cast<char>(i);
    std::string why;
    auto lines = wire::split_message('M', 0, "test/up", 3, bytes, &why);
    CHECK(lines.size() > 1 && lines.size() <= 24);
    wire::Reassembly r;
    std::optional<wire::Reassembly::Message> complete;
    for (const auto &l : lines) {
        auto encoded = wire::encode_line(l);
        CHECK(encoded.size() <= 1024);
        auto p = wire::parse_line(encoded, &why);
        CHECK(p.has_value());
        complete = r.add(*p, &why, 1000);
    }
    CHECK(complete && complete->data == bytes);
    CHECK(!r.add(lines.back(), &why, 1000));
    for (auto field : {0, 1, 2}) {
        wire::Reassembly mixed;
        CHECK(!mixed.add(lines[0], &why, 1000));
        auto l = lines[1];
        if (field == 0)
            l.version++;
        if (field == 1)
            l.type = 'W';
        if (field == 2)
            l.enc = 't';
        CHECK(!mixed.add(l, &why, 1001));
        CHECK(why == "a fragment without the ones before it");
    }
    wire::Reassembly expired;
    CHECK(!expired.add(lines[0], &why, 1000));
    CHECK(!expired.add(lines[1], &why, 31001));
    CHECK(why == "a fragment without the ones before it");
    CHECK(!wire::parse_line("#!emf1 M 0 test/up@3 0/25 t x", &why));
    CHECK(!wire::parse_line(std::string(1025, 'x'), &why));
    CHECK(!wire::parse_line("#!emf1 M 0 test/up@3 0/1 b !!", &why) || !wire::base64url_decode("!!"));
}
static void remote_wire() {
    // A standalone upstream EMF ClientLink, not a Kiln-native transport. It speaks the published #!emf1 protocol.
    session = KILN_SESSION_REMOTE;
    server_lines.clear();
    captured_server();
    wire::ClientLinkDeps d;
    d.channels = [] { return std::map<std::string, uint32_t>{{"test/up", 3}, {"test/down", 3}}; };
    d.self_account = [] { return std::optional<uint64_t>(123); };
    d.enabled = [] { return true; };
    std::vector<std::string> answers;
    d.on_message = [&](const std::string &, uint32_t, const std::string &bytes) { answers.push_back(bytes); };
    d.emf_version = "0.6.0";
    d.api = 27;
    wire::ClientLink peer(std::move(d));
    net_server_pre();
    now += 5001;
    net_server_pre();
    CHECK(!server_lines.empty());
    CHECK(server_lines[0].starts_with("#!emf1 S "));
    for (const auto &l : server_lines) {
        CHECK(peer.on_chat_line(l, 123));
    }
    server_lines.clear();
    for (const auto &l : peer.take_lines(64))
        inject(l);
    net_server_pre();
    for (const auto &l : server_lines)
        CHECK(peer.on_chat_line(l, 123));
    server_lines.clear();
    CHECK(peer.welcomed());
    CHECK(api.net_peer(ws, 7, "test/up") == 3);
    std::string binary(16384, '\0');
    for (size_t i = 0; i < binary.size(); ++i)
        binary[i] = static_cast<char>(i % 256);
    std::string why;
    CHECK(peer.send("test/up", 3, binary, &why));
    const auto old = got_server.size();
    auto lines = peer.take_lines(64);
    CHECK(lines.size() > 1 && lines.size() <= 24);
    // Wrong authenticated identity cannot borrow another peer's hello, and marked spoofs remain hidden.
    inject(lines[0], 999);
    inject(lines[0], 123, 999);
    for (const auto &l : lines)
        inject(l);
    CHECK(got_server.size() == old);
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == old + 1);
    CHECK(got_server.back().bytes == binary && got_server.back().from.account == 123 &&
          got_server.back().from.player == 7);
    CHECK(!got_server.back().from.local && got_server.back().from.admin);
    {
        ContextScope c(&mod, ws, true);
        CHECK(api.net_to_player(ws, 7, "test/down", binary.data(), binary.size()));
    }
    int32_t pending = 20000;
    std::memcpy(chat_system.data() + 0x14 + 12, &pending, 4);
    net_server_pre();
    CHECK(server_lines.empty()); // reader backpressure retains the entire message
    pending = 0;
    std::memcpy(chat_system.data() + 0x14 + 12, &pending, 4);
    net_server_pre();
    for (const auto &l : server_lines)
        CHECK(peer.on_chat_line(l, 123));
    server_lines.clear();
    CHECK(answers.size() == 1 && answers[0] == binary);
    // Duplicate fragments deliver at most once.
    for (const auto &l : lines) {
        inject(l);
    }
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == old + 1);
    now += 10000;
    CHECK(peer.send("test/up", 3, "old connection", &why));
    for (const auto &l : peer.take_lines(64))
        inject(l);
    person.connection = 72;
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == old + 1);
    person.connection = 71;
    CHECK(peer.send("test/up", 3, "old world", &why));
    for (const auto &l : peer.take_lines(64))
        inject(l);
    net_world_stopping(EMF_WORLD_SERVER);
    net_world_started(EMF_WORLD_SERVER);
    CHECK(api.net_peer(ws, 7, "test/up") == 0);
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == old + 1);
}
static int flushed = 0, added = 0, sent = 0;
static bool pool_full = false;
static void game_flush(uint8_t *) { ++flushed; }
static uint8_t *game_send(uint8_t *out, uint8_t *, uint8_t kind, const NetText *, uint32_t, uint32_t) {
    CHECK(kind == 0);
    ++sent;
    out[0] = pool_full ? 2 : 0;
    return out;
}
static uint8_t *game_add(uint8_t *out, uint8_t *, uint8_t, const NetText *, uint32_t, uint32_t, uint32_t, uint64_t,
                         const void *) {
    ++added;
    return out;
}
static void client_hooks() {
    auto hooks = net_client_test_install(game_flush, game_send, game_add);
    std::array<uint8_t, 0x110> gc{};
    uint8_t sys[16]{};
    auto *sp = sys;
    std::memcpy(gc.data() + 0xF8, &sp, sizeof sp);
    hooks.flush(gc.data());
    CHECK(flushed == 1);
    // Real production receive wrapper: a forged foreign player's beacon is hidden but causes no hello.
    wire::NetLine b;
    b.type = 'S';
    b.channel = "emf/net";
    b.payload = "This server uses EMF mods";
    auto beacon = wire::encode_line(b);
    NetText t{beacon.data(), beacon.size()};
    uint8_t out[16]{};
    hooks.add(out, sys, 0, &t, 0, 0, 71, 999, nullptr);
    CHECK(added == 0);
    std::string too_large = "#!emf1 " + std::string(1024, 'x');
    NetText oversized{too_large.data(), too_large.size()};
    hooks.add(out, sys, 0, &oversized, 0, 0, 71, 123, nullptr);
    CHECK(added == 0);
    hooks.flush(gc.data());
    CHECK(sent == 0);
    hooks.add(out, sys, 0, &t, 0, 0, 71, 123, nullptr);
    pool_full = true;
    hooks.flush(gc.data());
    CHECK(sent > 0);
    const int before = sent;
    pool_full = false;
    hooks.flush(gc.data());
    CHECK(sent > before); // full pool retried
    std::string ordinary = "hello";
    NetText o{ordinary.data(), ordinary.size()};
    hooks.add(out, sys, 0, &o, 0, 0, 71, 123, nullptr);
    CHECK(added == 1);
    hooks.add(out, sys, 1, &t, 0, 0, 71, 123, nullptr);
    CHECK(added == 2); // not typed chat
    uint8_t foreign[16]{};
    hooks.add(out, foreign, 0, &t, 0, 0, 71, 123, nullptr);
    CHECK(added == 3);
    // A new GameClient resets the old welcome/outbox before sending to it.
    hooks.add(out, sys, 0, &t, 0, 0, 71, 123, nullptr);
    auto next = gc;
    const int before_reset = sent;
    hooks.flush(next.data());
    CHECK(sent == before_reset);
    net_client_stop();
    hooks.flush(gc.data());
    CHECK(sent == before_reset);
}
int main() {
    framing();
    init();
    CHECK(subscribed);
    loopback();
    remote_wire();
    client_hooks();
    session = KILN_SESSION_HOST;
    CHECK(api.net_to_server("test/up", "queued", 6));
    CHECK(api.net_to_server("test/up", "also queued", 11));
    block_callback = true;
    std::thread delivering([] { net_after_step(ws, EMF_WORLD_SERVER); });
    entered.acquire();
    std::atomic<bool> stopped = false;
    std::binary_semaphore stop_entered(0);
    std::thread stopping([&] {
        stop_entered.release();
        net_shutdown();
        stopped = true;
    });
    stop_entered.acquire();
    CHECK(!stopped.load());
    release_callback.release();
    delivering.join();
    stopping.join();
    CHECK(stopped.load());
    CHECK(!subscribed);
    const auto before = got_server.size();
    net_after_step(ws, EMF_WORLD_SERVER);
    CHECK(got_server.size() == before);
    CHECK(!api.net_to_server("test/up", "x", 1));
    std::printf("EMF v27 network: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
