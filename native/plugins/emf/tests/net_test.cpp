// SPDX-License-Identifier: MIT
// Production API/engine bridges against fake Kiln services and an upstream EMF ClientLink wire peer.
#include "emf_internal.h"
#include "net_bridge.h"
#include "net/mods_check_service.h"
#include "net/net_link.h"
#include "net/net_service.h"
#include <algorithm>
#include <array>
#include <cstdarg>
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
static std::vector<std::string> notes, logged;
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
void logf(int, const char *fmt, ...) {
    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(line, sizeof line, fmt, ap);
    va_end(ap);
    if (logged.size() < 4096)
        logged.emplace_back(line);
}
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
static bool no_server_world = false; // a joined game: the server world is in another process
uint8_t world_kind_of(EmfWorld *w) { return w == ws ? EMF_WORLD_SERVER : w == wc ? EMF_WORLD_CLIENT : 0xff; }
EmfWorld *world_of_kind(uint32_t k) {
    return k == EMF_WORLD_SERVER ? (no_server_world ? nullptr : ws) : k == EMF_WORLD_CLIENT ? wc : nullptr;
}
std::vector<std::unique_ptr<Mod>> g_mods;
std::mutex g_mods_mutex;
void core_note(std::string_view text) { notes.emplace_back(text); }
bool commands_is_admin(uint32_t p, uint64_t a) { return p == 7 && a == 123; }
} // namespace kemf
using namespace kemf;
namespace wire = emf::core::net;
static EmfApi api{};
static KilnChatReceiveFn receive = nullptr;
static KilnChatPlayer person{};
static KilnChatPlayer second_person{};
static uint32_t session = KILN_SESSION_HOST;
static std::array<uint8_t, 0xA0 * 7> infos{};
static std::vector<std::string> server_lines;
static std::vector<uint32_t> server_recipients;
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
    *n = 0;
    for (const auto &p : {person, second_person})
        if (p.connection) {
            if (*n < cap) out[*n] = p;
            ++*n;
        }
    return KILN_OK;
}
static KilnResult find_player(uint32_t e, KilnChatPlayer *out) {
    if (e == second_person.entity && second_person.connection) {
        *out = second_person;
        return KILN_OK;
    }
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
    CHECK(who == to && (to == 7 || to == 8));
    server_lines.emplace_back(text.ptr, text.len);
    server_recipients.push_back(to);
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
static void unknown_channel_diagnostics_are_bounded() {
    uint64_t clock = 1000;
    size_t diagnostics = 0;
    wire::NetDeps deps;
    deps.now_ms = [&] { return clock; };
    deps.hosted = [](uint32_t *) { return false; };
    deps.post = [](uint8_t, std::function<void(EmfWorld *)>) { CHECK(false); };
    deps.log = [&](const std::string &message) {
        if (message.find("was dropped: no such channel") != std::string::npos) ++diagnostics;
    };
    wire::NetService service(std::move(deps));
    wire::ServerEnv env;
    env.send_lines = [](uint32_t, uint64_t, const std::vector<std::string> &) { return true; };
    service.enable_server(std::move(env));
    for (int i = 0; i < 512; ++i) {
        const std::string name = "unknown/" + std::to_string(i);
        wire::HelloInfo hello;
        hello.channels = {{name, 1}};
        for (const auto &line : wire::split_message('H', 0, "emf/net", 1, wire::hello_text(hello), nullptr))
            service.server_line(71, 123, wire::encode_line(line));
        for (const auto &line : wire::split_message('M', 1, name, 1, "rejected", nullptr))
            service.server_line(71, 123, wire::encode_line(line));
        clock += 1000; // Under the peer's line/byte rate; changing names must not grow the diagnostic cache forever.
    }
    CHECK(diagnostics > 0 && diagnostics <= 128);
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
    CHECK(server_lines.empty()); // EMF 0.6.8: the first beacon comes 10 s after the connection, not 5
    now += 5000;
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
static void backpressure_is_per_recipient() {
    session = KILN_SESSION_REMOTE;
    second_person = person;
    second_person.entity = 8;
    second_person.connection = 81;
    second_person.account = 456;
    second_person.reader = 2;
    captured_server();
    wire::HelloInfo hello;
    hello.channels = {{"test/down", 3}};
    for (const auto &line : wire::split_message('H', 0, "emf/net", 1, wire::hello_text(hello), nullptr)) {
        inject(wire::encode_line(line));
        inject(wire::encode_line(line), 456, 81);
    }
    {
        ContextScope c(&mod, ws, true);
        CHECK(api.net_to_player(ws, 7, "test/down", "first", 5));
        CHECK(api.net_to_player(ws, 7, "test/down", "second", 6));
        CHECK(api.net_to_player(ws, 8, "test/down", "ready", 5));
    }
    int32_t pending = 20000;
    std::memcpy(chat_system.data() + 0x14 + person.reader * 12, &pending, 4);
    server_lines.clear();
    server_recipients.clear();
    net_server_pre();
    CHECK(!server_recipients.empty());
    CHECK(std::all_of(server_recipients.begin(), server_recipients.end(), [](uint32_t to) { return to == 8; }));
    CHECK(std::any_of(server_lines.begin(), server_lines.end(), [](const std::string &line) { return line.ends_with(" ready"); }));
    pending = 0;
    std::memcpy(chat_system.data() + 0x14 + person.reader * 12, &pending, 4);
    server_lines.clear();
    server_recipients.clear();
    net_server_pre();
    std::vector<std::string> delivered;
    for (const auto &line : server_lines) {
        const auto parsed = wire::parse_line(line, nullptr);
        if (parsed && parsed->type == 'M') delivered.push_back(parsed->payload);
    }
    CHECK((delivered == std::vector<std::string>{"first", "second"}));
    second_person = {};
    net_world_stopping(EMF_WORLD_SERVER);
    net_world_started(EMF_WORLD_SERVER);
}
// ---------------------------------------------------------------- the mod check on join (EMF 0.6.3, emf/mods) ----
static void listed(const char *id, const char *version, const char *side, uint32_t status) {
    auto m = std::make_unique<Mod>();
    m->id = id;
    m->version = version;
    m->side = side;
    m->status = status;
    g_mods.push_back(std::move(m));
}
static void kiln_mods() {
    g_mods.clear();
    listed("shared", "1.0.0", "both", EMF_MOD_LOADED);
    listed("srv", "1.2.0", "server", EMF_MOD_SCRIPT);       // a Lua mod handed to the script host runs too
    listed("off", "1.0.0", "both", EMF_MOD_DISABLED);       // not running: not in the list
    listed("Bad Id", "1.0.0", "both", EMF_MOD_LOADED);      // an id the list cannot carry is left out
    listed("shared", "9.9.9", "both", EMF_MOD_LOADED);      // the first of a repeated id counts
    listed("plain", "0.1.0", "", EMF_MOD_LOADED);
}
static size_t logged_with(std::string_view part) {
    return static_cast<size_t>(std::count_if(logged.begin(), logged.end(),
                                             [&](const std::string &l) { return l.find(part) != std::string::npos; }));
}
// Kiln hosts the world; an unmodified EMF 0.6.8 game (EMF's own ClientLink) joins and sends its list.
static void mods_check_as_server() {
    session = KILN_SESSION_REMOTE;
    server_lines.clear();
    logged.clear();
    captured_server();
    kiln_mods();
    wire::ClientLinkDeps d;
    d.channels = [] { return std::map<std::string, uint32_t>{{"test/up", 3}, {"emf/mods", 1}}; };
    d.self_account = [] { return std::optional<uint64_t>(123); };
    d.enabled = [] { return true; };
    std::vector<std::pair<std::string, std::string>> answers;
    d.on_message = [&](const std::string &channel, uint32_t, const std::string &bytes) { answers.push_back({channel, bytes}); };
    d.emf_version = "0.6.8";
    d.api = 40;
    wire::ClientLink peer(std::move(d));
    const auto to_peer = [&] {
        for (const auto &l : server_lines)
            CHECK(peer.on_chat_line(l, 123));
        server_lines.clear();
    };
    net_server_pre();
    now += 10001;
    net_server_pre();
    CHECK(!server_lines.empty() && server_lines[0].starts_with("#!emf1 S "));
    to_peer();
    for (const auto &l : peer.take_lines(64))
        inject(l);
    net_server_pre();
    to_peer();
    CHECK(peer.welcomed());
    CHECK(api.net_peer(ws, 7, "emf/mods") == 1); // the game has the core's channel, and so does this server
    // Kiln's own tick sends nothing here: this process has the server world
    now += 1000;
    net_after_step(wc, EMF_WORLD_CLIENT);
    CHECK(net_client_take(64).empty());

    std::string why;
    const std::vector<emf::core::ModEntry> theirs{
        {'e', "emf", "0.6.8"}, {'b', "shared", "1.0.0"}, {'b', "extra", "3.0.0"}, {'l', "hud", "2.0.0"}};
    CHECK(peer.send("emf/mods", 1, emf::core::encode_mods(theirs), &why));
    for (const auto &l : peer.take_lines(64))
        inject(l);
    CHECK(answers.empty());
    net_after_step(ws, EMF_WORLD_SERVER); // the handler: the difference is logged, Kiln's list goes back
    net_server_pre();
    to_peer();
    CHECK(answers.size() == 1 && answers[0].first == "emf/mods");
    const auto list = answers.empty() ? std::nullopt : emf::core::decode_mods(answers[0].second, &why);
    CHECK(list && list->size() == 4);
    if (list && list->size() == 4) {
        CHECK((*list)[0].kind == 'e' && (*list)[0].id == "emf" && (*list)[0].version == "0.6.8-kiln");
        CHECK((*list)[1].kind == 'b' && (*list)[1].id == "shared" && (*list)[1].version == "1.0.0");
        CHECK((*list)[2].kind == 's' && (*list)[2].id == "srv" && (*list)[2].version == "1.2.0");
        CHECK((*list)[3].kind == 'u' && (*list)[3].id == "plain");
    }
    CHECK(logged_with("mods check: player 0x000000000000007b (entity 7, EMF 0.6.8): extra: b extra 3.0.0") == 1);

    // a list without an EMF entry is no list: logged once, no answer
    CHECK(peer.send("emf/mods", 1, emf::core::encode_mods({{'b', "shared", "1.0.0"}}), &why));
    for (const auto &l : peer.take_lines(64))
        inject(l);
    net_after_step(ws, EMF_WORLD_SERVER);
    net_server_pre();
    to_peer();
    CHECK(answers.size() == 1 && logged_with("was not taken: no EMF entry") == 1);

    // a player who left before the handler ran is not answered (the list is dropped with the connection)
    CHECK(peer.send("emf/mods", 1, emf::core::encode_mods(theirs), &why));
    for (const auto &l : peer.take_lines(64))
        inject(l);
    person.account = 999;
    net_after_step(ws, EMF_WORLD_SERVER);
    person.account = 123;
    net_server_pre();
    to_peer();
    CHECK(answers.size() == 1);
    g_mods.clear();
    net_world_stopping(EMF_WORLD_SERVER);
    net_world_started(EMF_WORLD_SERVER);
}
// Kiln joined a server that runs EMF 0.6.8: EMF's own NetService stands for it, with the core's emf/mods channel as
// EMF's core sets it up. Also the greeting that comes while the game does not know its own account yet (EMF 0.6.2).
static void mods_check_as_client() {
    session = KILN_SESSION_REMOTE;
    no_server_world = true;
    notes.clear();
    logged.clear();
    kiln_mods();
    net_world_stopping(EMF_WORLD_CLIENT); // a fresh service: no welcome of an earlier test
    net_world_started(EMF_WORLD_CLIENT);
    std::vector<std::string> to_client, lists;
    std::vector<std::function<void(EmfWorld *)>> jobs;
    wire::NetDeps sd;
    sd.hosted = [](uint32_t *p) {
        *p = 0;
        return false;
    };
    sd.post = [&](uint8_t, std::function<void(EmfWorld *)> fn) { jobs.push_back(std::move(fn)); };
    sd.now_ms = [] { return now; };
    sd.log = [](const std::string &) {};
    sd.connection_of = [](uint32_t e) {
        return e == 7 ? std::make_optional(std::make_pair(uint32_t{71}, uint64_t{123})) : std::nullopt;
    };
    sd.entity_of = [](uint64_t a) { return a == 123 ? 7u : 0u; };
    sd.is_admin = [](uint64_t) { return false; };
    wire::NetService srv(std::move(sd));
    srv.set_core_greets([] { return true; }); // the server has mods its players must match
    wire::ServerEnv env;
    env.send = [&](uint32_t, uint64_t, const std::string &l) {
        to_client.push_back(l);
        return true;
    };
    env.send_lines = [&](uint32_t, uint64_t, const std::vector<std::string> &ls) {
        to_client.insert(to_client.end(), ls.begin(), ls.end());
        return true;
    };
    env.room = [] { return size_t{1000}; };
    env.is_local = [](uint32_t, uint64_t) { return false; };
    env.emf_version = "0.6.8";
    env.api = 40;
    srv.enable_server(std::move(env));
    const std::vector<emf::core::ModEntry> server_mods{
        {'e', "emf", "0.6.8"}, {'c', "pack", "1.0.0"}, {'b', "shared", "1.0.0"}, {'b', "needed", "2.0.0"}, {'s', "herald", "1.0.0"}};
    wire::ChannelDef c;
    c.name = emf::core::kModsChannel;
    c.version = emf::core::kModsChannelVersion;
    c.flags = wire::kToServer | wire::kToPlayers;
    c.max_size = emf::core::kMaxModListBytes;
    c.per_10s = 5;
    c.on_server = [&](EmfWorld *, const wire::NetFrom &f, const std::string &data) {
        lists.push_back(data);
        std::string w;
        CHECK(srv.to_player("emf", f.player, emf::core::kModsChannel, emf::core::encode_mods(server_mods), &w));
    };
    std::string why;
    CHECK(srv.add_channel("emf", c, &why));
    const auto to_kiln = [&](uint64_t speaker = 123) {
        for (const auto &l : to_client)
            CHECK(net_client_line(l, speaker));
        to_client.clear();
    };
    const auto to_server = [&] {
        const auto lines = net_client_take(64);
        for (const auto &l : lines)
            srv.server_line(71, 123, l);
        for (auto &j : jobs)
            j(ws);
        jobs.clear();
        return lines.size();
    };
    const auto tick = [&] {
        now += 1000;
        net_after_step(wc, EMF_WORLD_CLIENT);
    };
    const auto set_account = [&](uint64_t a) { std::memcpy(infos.data() + 6 * 0xA0 + 8, &a, 8); };

    // The greeting comes while the game is still loading and does not know its own account: held, then answered.
    srv.server_tick({{71, 123}});
    now += 10001;
    srv.server_tick({{71, 123}});
    CHECK(to_client.size() == 1 && to_client[0].find(wire::kBeaconText) != std::string::npos);
    set_account(0);
    to_kiln();
    CHECK(net_client_take(64).empty());   // taken (never shown), not answered yet: a forged greeting could be it
    net_world_started(EMF_WORLD_CLIENT);  // the client world appears on the same connection: the held greeting stays
    CHECK(net_client_take(64).empty());
    tick();
    CHECK(to_server() == 0 && lists.empty()); // no welcome yet: no list is sent
    set_account(123);
    CHECK(to_server() == 1);              // the hello, once the game knows who it is
    CHECK(!to_client.empty());            // the server's welcome
    to_kiln();
    CHECK(api.net_peer(nullptr, 0, "emf/mods") == 1);
    CHECK(api.net_peer(nullptr, 0, "test/up") == 0); // the server has only the core's channel

    // The list goes once; the server answers with its own; the player is told once what is missing.
    tick();
    CHECK(to_server() >= 1 && lists.size() == 1);
    const auto mine = lists.empty() ? std::nullopt : emf::core::decode_mods(lists[0], &why);
    CHECK(mine && mine->size() == 4);
    if (mine && mine->size() == 4) {
        CHECK((*mine)[0].kind == 'e' && (*mine)[0].id == "emf" && (*mine)[0].version == "0.6.8-kiln");
        CHECK((*mine)[1].kind == 'b' && (*mine)[1].id == "shared" && (*mine)[1].version == "1.0.0");
        CHECK((*mine)[2].kind == 's' && (*mine)[2].id == "srv");
        CHECK((*mine)[3].kind == 'u' && (*mine)[3].id == "plain");
    }
    CHECK(!to_client.empty());
    to_kiln();
    CHECK(notes.empty());                 // the handler runs after the client world's step
    net_after_step(wc, EMF_WORLD_CLIENT);
    CHECK(notes.size() == 1);
    if (!notes.empty()) {
        CHECK(notes[0].starts_with("Missing mods for this server (items from them can be lost): pack 1.0.0 (you: none), "
                                   "needed 2.0.0 (you: none)."));
        CHECK(notes[0].ends_with("Install the same versions in the Kiln launcher.") &&
              notes[0].find("EMF.exe") == std::string::npos);
    }
    CHECK(logged_with("mods check: against the server's mods: missing: c pack 1.0.0 (none), b needed 2.0.0 (none)") == 1);
    for (int i = 0; i < 40; ++i) {        // answered: nothing more is sent in this session
        tick();
        CHECK(to_server() == 0);
    }
    CHECK(lists.size() == 1 && notes.size() == 1);

    // A greeting held while the account was unknown, spoken as another player, is never answered.
    net_world_stopping(EMF_WORLD_CLIENT);
    net_world_started(EMF_WORLD_CLIENT);
    set_account(0);
    const auto forged = wire::encode_line(wire::split_message('S', 0, "emf/net", 1, "fake", nullptr)[0]);
    CHECK(net_client_line(forged, 999));
    set_account(123);
    CHECK(net_client_take(64).empty());
    tick();
    CHECK(to_server() == 0 && lists.size() == 1); // and no welcome means no list

    // A new session on a new connection: greeted again, the list goes again, the note may show again.
    srv.server_tick({});
    srv.server_tick({{71, 123}});
    now += 10001;
    srv.server_tick({{71, 123}});
    CHECK(to_client.size() == 1);
    to_kiln();
    CHECK(to_server() == 1);
    to_kiln();
    tick();
    CHECK(to_server() >= 1 && lists.size() == 2);
    to_kiln();
    net_after_step(wc, EMF_WORLD_CLIENT);
    CHECK(notes.size() == 2);

    // A server whose list this game matches: no note.
    net_world_stopping(EMF_WORLD_CLIENT);
    net_world_started(EMF_WORLD_CLIENT);
    listed("needed", "2.0.0", "both", EMF_MOD_LOADED);
    listed("pack", "1.0.0", "both", EMF_MOD_LOADED); // same id and version: the kind of the game's own entry is not compared
    srv.server_tick({});
    srv.server_tick({{71, 123}});
    now += 10001;
    srv.server_tick({{71, 123}});
    to_kiln();
    CHECK(to_server() == 1);
    to_kiln();
    tick();
    CHECK(to_server() >= 1 && lists.size() == 3);
    to_kiln();
    net_after_step(wc, EMF_WORLD_CLIENT);
    CHECK(notes.size() == 2 && logged_with("mods check: against the server's mods: same mods") == 1);

    g_mods.clear();
    no_server_world = false;
    net_world_stopping(EMF_WORLD_CLIENT);
    net_world_started(EMF_WORLD_CLIENT);
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
    unknown_channel_diagnostics_are_bounded();
    init();
    CHECK(subscribed);
    loopback();
    remote_wire();
    backpressure_is_per_recipient();
    mods_check_as_server();
    mods_check_as_client();
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
    std::printf("EMF network (v27 messages, mods check): %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
