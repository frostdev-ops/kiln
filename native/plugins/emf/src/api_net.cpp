// SPDX-License-Identifier: MIT
// EMF v27 adapter. The protocol/channel implementation in net/ is upstream EMF 0.6.8 (MIT).
// Only the game-facing dependencies live here. Remote messages use EMF's exact #!emf1 chat wire, so a peer need
// not have Kiln. Callbacks are bounded, guarded and delivered only on the destination world's Post thread.
//
// The mod check on join (EMF 0.6.3, the core channel emf/mods; net/mods_check*.h are EMF's): a joined game sends the
// list of its EMF mods once per session to a server that has the channel, the server logs the differences and answers
// with its own list, and the game tells its player once when it lacks a mod the server's players must have. An EMF
// server set to refuse (mods_check=refuse) kicks a game that sends no list, so a Kiln client has to answer.
// Not here yet: a Kiln server greets players only when one of its mods has a channel (the core's channel alone does
// not make it greet), it never refuses a join, and the list has no content mods (the EML content kiln-eml applies).
#include <intrin.h>
#include <algorithm>
#include <array>
#include <deque>
#include <optional>
#include <set>
#include <sstream>

#include "emf_internal.h"
#include "net_bridge.h"
#include "net/mods_check_service.h"
#include "net/net_service.h"

namespace kemf {
namespace {
namespace wire = emf::core::net;
struct Pending {
    uint8_t kind;
    uint64_t epoch, when;
    std::function<void(EmfWorld *)> fn;
};
struct Outbound {
    uint32_t handle;
    uint64_t account, when, epoch;
    std::string line;
};
struct Registration {
    std::string owner;
    wire::ChannelDef def;
};
std::mutex g_mutex;
std::recursive_mutex g_dispatch; // shutdown/world-stop waits for entered callbacks (reentrant from a callback)
std::shared_ptr<wire::NetService> g_net;
std::vector<Registration> g_channels;
std::deque<Pending> g_pending;
std::deque<Outbound> g_out;
std::array<uint64_t, KILN_WORLD_KINDS> g_epoch{};
std::array<bool, KILN_WORLD_KINDS> g_stopping{};
uint64_t g_serial = 0; // identifies the service/connection generation even if an old call is still entered
bool g_running = false, g_server_on = false, g_client_on = false, g_enabled = true, g_greet = true;
KilnHandle g_chat_sub = 0;
KilnHandle g_receive_hook = 0;
std::atomic<uintptr_t> g_game_server{0};
int64_t g_server_chat = -1, g_reader_pending = -1, g_reader_stride = -1;
constexpr size_t kMaxCallbacks = 1024, kMaxOut = 4096, kPerPlayerOut = 256;
constexpr uint64_t kExpireMs = 30000;
// What this end calls itself in a hello and in its mod list: the EMF version whose table it serves, marked as Kiln's.
constexpr const char *kEmfVersionHere = "0.6.8-kiln";
std::unique_ptr<emf::core::ModsCheck> g_mods_check; // made in net_on_ready, never freed (the channel's handlers use it)
std::atomic<uint32_t> g_client_session{0};          // +1 per client world: the list is sent and the note shown again
std::atomic<uint64_t> g_mods_tick_at{0};

uintptr_t KILN_CALL observe_receive(KilnHookCall *call, void *) {
    if (call && call->arg_count)
        g_game_server.store(call->args[0]);
    return S.hooks->call_original(call);
}
bool reader_gate_install() {
    if (!S.hooks || !S.hooks->detour || !S.hooks->revoke || !S.hooks->call_original || !g_api || !g_api->symbol ||
        !g_api->constant)
        return false;
    uintptr_t target = 0;
    if (g_api->symbol(kstr("chat/ServerReceive"), &target) != KILN_OK || !target ||
        g_api->constant(kstr("chat/gs.chat"), &g_server_chat) != KILN_OK ||
        g_api->constant(kstr("chat/sys.reader_pending"), &g_reader_pending) != KILN_OK ||
        g_api->constant(kstr("chat/sys.reader_stride"), &g_reader_stride) != KILN_OK || g_server_chat < 0 ||
        g_server_chat > 0x100000 || g_reader_pending < 0 || g_reader_pending > 0x1000 || g_reader_stride < 4 ||
        g_reader_stride > 256)
        return false;
    return S.hooks->detour(g_owner, target, 0, &observe_receive, nullptr, &g_receive_hook) == KILN_OK;
}
bool reader_ready(uint32_t reader) {
    const uintptr_t server = g_game_server.load();
    uintptr_t sys = 0;
    int32_t pending = -1;
    return server && reader < 32 &&
           seh_copy(&sys, reinterpret_cast<const void *>(server + g_server_chat), sizeof sys) && sys &&
           seh_copy(&pending, reinterpret_cast<const void *>(sys + g_reader_pending + reader * g_reader_stride),
                    sizeof pending) &&
           pending >= 0 && pending <= 16384;
}

std::vector<KilnChatPlayer> players() {
    if (!S.chat || !S.chat->players)
        return {};
    std::array<KilnChatPlayer, 64> ps{};
    uint32_t n = 0;
    if (S.chat->players(ps.data(), static_cast<uint32_t>(ps.size()), &n) != KILN_OK)
        return {};
    return {ps.begin(), ps.begin() + std::min<size_t>(n, ps.size())};
}
std::optional<KilnChatPlayer> connection(uint32_t handle, uint64_t account) {
    for (const auto &p : players())
        if (p.connection == handle && p.account == account && handle && account)
            return p;
    return std::nullopt;
}
std::optional<KilnChatPlayer> player(uint32_t id) {
    if (!id || !S.chat || !S.chat->find_player)
        return std::nullopt;
    KilnChatPlayer p{};
    p.size = sizeof p;
    if (S.chat->find_player(id, &p) != KILN_OK || !p.connection || !p.account)
        return std::nullopt;
    return p;
}
bool hosted(uint32_t *id) {
    *id = 0;
    if (!S.client || !S.client->state)
        return false;
    KilnClientState c{};
    c.size = sizeof c;
    if (S.client->state(&c) != KILN_OK || c.session != KILN_SESSION_HOST || !c.local_player)
        return false;
    *id = c.local_player;
    return true;
}
// Layout from EMF 0.6.0 server_chat.h/core.cpp (MIT), checked only for the upstream known client image hashes.
// The account is a client-world record, never supplied by a chat line. No fallback to the line's claimed speaker.
std::optional<uint64_t> self_account() {
    if (!S.client || !S.client->state || !g_api || !g_api->singleton)
        return std::nullopt;
    KilnClientState c{};
    c.size = sizeof c;
    if (S.client->state(&c) != KILN_OK || !c.local_player || c.local_player > 1023)
        return std::nullopt;
    auto *w = world_of_kind(KILN_WORLD_CLIENT);
    const uintptr_t infos = w ? g_api->singleton(reinterpret_cast<uintptr_t>(w), kstr("BasicPlayerInfos")) : 0;
    uint8_t entry[16]{};
    if (!infos || !seh_copy(entry, reinterpret_cast<const void *>(infos + (c.local_player - 1) * 0xA0), sizeof entry))
        return std::nullopt;
    uint32_t entity = 0;
    uint64_t account = 0;
    std::memcpy(&entity, entry, sizeof entity);
    std::memcpy(&account, entry + 8, sizeof account);
    return entity == c.local_player && account ? std::optional<uint64_t>(account) : std::nullopt;
}
bool enqueue(uint64_t serial, uint32_t handle, uint64_t account, const std::vector<std::string> &lines) {
    if (lines.empty() || lines.size() > wire::kMaxFragments || !connection(handle, account))
        return false;
    for (const auto &l : lines)
        if (l.size() > wire::kMaxLine)
            return false;
    std::lock_guard lock(g_mutex);
    if (!g_running || serial != g_serial || g_stopping[EMF_WORLD_SERVER] || g_out.size() + lines.size() > kMaxOut)
        return false;
    const auto count = std::count_if(g_out.begin(), g_out.end(),
                                     [&](const Outbound &o) { return o.handle == handle && o.account == account; });
    if (static_cast<size_t>(count) + lines.size() > kPerPlayerOut)
        return false;
    for (const auto &l : lines)
        g_out.push_back({handle, account, now_ms(), g_epoch[EMF_WORLD_SERVER], l});
    return true;
}

std::shared_ptr<wire::NetService> service();
// This game's list for the mod check: EMF itself first, then every EMF mod that runs here (loaded, or a script handed
// to the Lua host) by its mod.json side. An id or version the list cannot carry is left out, as EMF does.
std::vector<emf::core::ModEntry> mods_list() {
    std::vector<emf::core::ModEntry> out{{'e', "emf", kEmfVersionHere}};
    std::set<std::string> seen{"emf"};
    std::lock_guard lock(g_mods_mutex);
    for (const auto &m : g_mods) {
        if (m->status != EMF_MOD_LOADED && m->status != EMF_MOD_SCRIPT)
            continue;
        if (!emf::core::valid_list_id(m->id) || !emf::core::valid_list_version(m->version) || !seen.insert(m->id).second)
            continue;
        const char kind = m->side == "both" ? 'b' : m->side == "server" ? 's' : m->side == "client" ? 'l' : 'u';
        out.push_back({kind, m->id, m->version});
    }
    return out;
}
// EMF's note ends by naming EMF's own manager; a Kiln player installs mods in Kiln.
std::string note_for_kiln(std::string note) {
    const std::string_view theirs = "Install the same versions with EMF.exe.";
    if (const size_t at = note.rfind(theirs); at != std::string::npos)
        note.replace(at, theirs.size(), "Install the same versions in the Kiln launcher.");
    return note;
}
emf::core::ModsCheck::Deps mods_check_deps() {
    emf::core::ModsCheck::Deps d;
    d.mine = mods_list;
    d.joined = [] { return world_of_kind(EMF_WORLD_SERVER) == nullptr; }; // no server world here: someone else's
    d.server_version = [] {
        auto s = service();
        return s ? s->peer(0, emf::core::kModsChannel) : 0u;
    };
    d.client_session = [] { return g_client_session.load(); };
    d.to_server = [](const std::string &data) {
        std::string why;
        auto s = service();
        return s && s->to_server("emf", emf::core::kModsChannel, data, &why);
    };
    d.to_player = [](uint32_t id, const std::string &data) {
        std::string why;
        auto s = service();
        return s && s->to_player("emf", id, emf::core::kModsChannel, data, &why);
    };
    d.notify = [](const std::string &note) { core_note(note_for_kiln(note)); };
    d.log = [](const std::string &l) { logf(KILN_LOG_INFO, "EMF %s", l.c_str()); };
    d.has_peer = [](uint32_t id) {
        auto s = service();
        return s && s->peer(id, emf::core::kModsChannel) != 0;
    };
    return d;
}
// The core's own channel. Its handlers run where a mod's do: after the destination world's step, on its thread.
wire::ChannelDef mods_channel() {
    wire::ChannelDef c;
    c.name = emf::core::kModsChannel;
    c.version = emf::core::kModsChannelVersion;
    c.flags = wire::kToServer | wire::kToPlayers;
    c.max_size = emf::core::kMaxModListBytes;
    c.per_10s = 5;
    c.on_server = [](EmfWorld *, const wire::NetFrom &f, const std::string &data) {
        if (f.local || !g_mods_check)
            return;
        // As for a mod's handler: the player may have left, or the connection been reused, since this was queued.
        const auto p = player(f.player);
        if (p && p->account == f.account)
            g_mods_check->on_server_message(f.player, f.account, data);
    };
    c.on_player = [](EmfWorld *, const wire::NetFrom &f, const std::string &data) {
        if (!f.local && g_mods_check)
            g_mods_check->on_player_message(data);
    };
    return c;
}

std::shared_ptr<wire::NetService> make_service() {
    const uint64_t serial = ++g_serial;
    wire::NetDeps d;
    d.hosted = hosted;
    d.post = [serial](uint8_t kind, std::function<void(EmfWorld *)> fn) {
        std::lock_guard lock(g_mutex);
        if (g_running && serial == g_serial && kind < g_epoch.size() && !g_stopping[kind] &&
            g_pending.size() < kMaxCallbacks)
            g_pending.push_back({kind, g_epoch[kind], now_ms(), std::move(fn)});
    };
    d.now_ms = now_ms;
    d.log = [](const std::string &l) { logf(KILN_LOG_INFO, "EMF net: %s", l.c_str()); };
    d.connection_of = [](uint32_t entity) -> std::optional<std::pair<uint32_t, uint64_t>> {
        const auto p = player(entity);
        return p ? std::make_optional(std::make_pair(p->connection, p->account)) : std::nullopt;
    };
    d.entity_of = [](uint64_t account) {
        for (const auto &p : players())
            if (p.account == account)
                return p.entity;
        return 0u;
    };
    d.is_admin = [](uint64_t account) {
        for (const auto &p : players())
            if (p.account == account)
                return commands_is_admin(p.entity, account);
        return false;
    };
    auto s = std::make_shared<wire::NetService>(std::move(d));
    // A game's new hello, or its leaving, ends the list it sent before (set before the server end is enabled).
    s->set_peer_watch([](uint64_t account, bool) {
        if (g_mods_check)
            g_mods_check->forget(account);
    });
    if (g_server_on) {
        wire::ServerEnv e;
        e.emf_version = kEmfVersionHere;
        e.api = EMF_API_VERSION;
        e.greet = g_greet;
        e.send = [serial](uint32_t h, uint64_t a, const std::string &l) { return enqueue(serial, h, a, {l}); };
        e.send_lines = [serial](uint32_t h, uint64_t a, const std::vector<std::string> &ls) {
            return enqueue(serial, h, a, ls);
        };
        // Conservative whole-message room; enqueue applies the tighter per-player cap atomically.
        e.room = [] {
            std::lock_guard lock(g_mutex);
            return kMaxOut - g_out.size();
        };
        e.is_local = [](uint32_t h, uint64_t a) {
            uint32_t local = 0;
            const auto p = hosted(&local) ? player(local) : std::nullopt;
            return p && p->connection == h && p->account == a;
        };
        s->enable_server(std::move(e));
    }
    if (g_client_on) {
        wire::ClientEnv e;
        e.emf_version = kEmfVersionHere;
        e.api = EMF_API_VERSION;
        e.self_account = self_account;
        s->enable_client(std::move(e));
    }
    if (g_mods_check) {
        std::string why;
        if (!s->add_channel("emf", mods_channel(), &why))
            logf(KILN_LOG_WARN, "EMF mods check: off (%s)", why.c_str());
    }
    for (const auto &r : g_channels) {
        std::string why;
        s->add_channel(r.owner, r.def, &why);
    }
    return s;
}
std::shared_ptr<wire::NetService> service() {
    std::lock_guard lock(g_mutex);
    return g_running ? g_net : nullptr;
}
bool bounded_string(const char *p, size_t limit, std::string *out) {
    out->clear();
    if (!p)
        return false;
    for (size_t i = 0; i <= limit; ++i) {
        char c = 0;
        if (!seh_copy(&c, p + i, 1))
            return false;
        if (!c)
            return true;
        if (i == limit)
            return false;
        out->push_back(c);
    }
    return false;
}
bool payload(const void *data, uint32_t size, std::string *out) {
    if (size > EMF_NET_MAX_MESSAGE || (size && !data))
        return false;
    out->resize(size);
    return !size || seh_copy(out->data(), data, size);
}
// EMF namespaces channels by the calling DLL's file name, which can differ from mod.json's id.
std::string channel_owner(const Mod *m) { return m->dll.empty() ? m->id : lower(path_utf8(m->dll.stem())); }
wire::NetHandler handler(Mod *m, EmfNetCallback cb, void *user) {
    if (!cb)
        return {};
    auto broken = std::make_shared<std::atomic<bool>>(false);
    return [m, cb, user, broken](EmfWorld *w, const wire::NetFrom &f, const std::string &data) {
        if (broken->load())
            return;
        // A remote player may have disconnected or the connection may have been reused since it queued this.
        if (!f.local && world_kind_of(w) == EMF_WORLD_SERVER) {
            const auto p = player(f.player);
            if (!p || p->account != f.account)
                return;
        }
        EmfNetFrom from{};
        from.size = sizeof from;
        from.player = f.player;
        from.account = f.account;
        from.peer_version = f.peer_version;
        from.admin = f.admin;
        from.local = f.local;
        struct Call {
            EmfNetCallback cb;
            EmfWorld *w;
            EmfNetFrom *from;
            const std::string *data;
            void *user;
        } c{cb, w, &from, &data, user};
        ContextScope scope(m, w, true);
        const uint64_t start = qpc_ns();
        if (!guarded(
                m, "a net message callback",
                [](void *p) {
                    auto &c = *static_cast<Call *>(p);
                    c.cb(c.w, c.from, c.data->data(), static_cast<uint32_t>(c.data->size()), c.user);
                },
                &c))
            broken->store(true);
        account(m, qpc_ns() - start);
    };
}
bool api_net_channel(const EmfNetChannel *def) {
    Mod *m = caller_mod(_ReturnAddress());
    EmfNetChannel c{};
    uint32_t size = 0;
    std::string name;
    if (!m || !def || !seh_copy(&size, def, sizeof size) || size < sizeof c || !seh_copy(&c, def, sizeof c) ||
        !bounded_string(c.name, 81, &name))
        return refuse(m, "net_channel", "bad channel definition", true), false;
    wire::ChannelDef d;
    d.name = name;
    d.version = c.version;
    d.flags = c.flags;
    d.max_size = c.max_size;
    d.per_10s = c.per_10s;
    d.on_server = handler(m, c.on_server, c.user);
    d.on_player = handler(m, c.on_player, c.user);
    std::string why;
    // Serialize registrations with service replacement at a world boundary.
    std::lock_guard lock(g_mutex);
    if (!g_running || !g_net)
        return false;
    if (g_channels.size() >= 1024)
        return refuse(m, "net_channel", "too many channels", true), false;
    const std::string owner = channel_owner(m);
    if (!g_net->add_channel(owner, d, &why))
        return refuse(m, "net_channel", why.c_str(), true), false;
    g_channels.push_back({owner, std::move(d)});
    return true;
}
bool api_net_to_server(const char *channel, const void *data, uint32_t size) {
    Mod *m = caller_mod(_ReturnAddress());
    std::string name, bytes, why;
    auto s = service();
    if (!m || !s || !bounded_string(channel, 81, &name) || !payload(data, size, &bytes))
        return false;
    if (!s->to_server(channel_owner(m), name, bytes, &why))
        return refuse(m, "net_to_server", why.c_str(), true), false;
    return true;
}
bool server_context(EmfWorld *w) {
    return w && world_kind_of(w) == EMF_WORLD_SERVER && on_thread_of(w) && t_ctx.world_cb && t_ctx.world == w;
}
bool api_net_to_player(EmfWorld *w, uint32_t id, const char *channel, const void *data, uint32_t size) {
    Mod *m = caller_mod(_ReturnAddress());
    std::string name, bytes, why;
    auto s = service();
    if (!m || !s || !server_context(w) || !id || !bounded_string(channel, 81, &name) || !payload(data, size, &bytes))
        return false;
    return s->to_player(channel_owner(m), id, name, bytes, &why);
}
uint32_t api_net_to_players(EmfWorld *w, const char *channel, const void *data, uint32_t size) {
    Mod *m = caller_mod(_ReturnAddress());
    std::string name, bytes;
    auto s = service();
    if (!m || !s || !server_context(w) || !bounded_string(channel, 81, &name) || !payload(data, size, &bytes))
        return 0;
    return s->to_players(channel_owner(m), name, bytes);
}
uint32_t api_net_peer(EmfWorld *w, uint32_t id, const char *channel) {
    std::string name;
    auto s = service();
    if (!s || !bounded_string(channel, 81, &name) || (id && w != world_of_kind(EMF_WORLD_SERVER)))
        return 0;
    return s->peer(id, name);
}
bool KILN_CALL on_line(const KilnChatMessage *m, void *) {
    if (!m || !m->text.ptr || !wire::is_marked(sv(m->text)))
        return false;
    if (m->text.len > wire::kMaxLine)
        return true;
    auto s = service();
    // Always hide a marked line when enabled, even if a spoof, malformed, or not yet a peer.
    if (s && m->type == 0 && connection(m->sender.connection, m->sender.account))
        s->server_line(m->sender.connection, m->sender.account, sv(m->text));
    return true;
}
void config() {
    std::string text;
    if (!read_file(P.config / L"emf" / L"emf_core.ini", &text))
        read_file(P.emf / L"emf_core.ini", &text);
    std::istringstream in(text);
    bool net = false;
    for (std::string l; std::getline(in, l);) {
        l.erase(std::remove_if(l.begin(), l.end(), [](unsigned char c) { return c == ' ' || c == '\t' || c == '\r'; }),
                l.end());
        if (!l.empty() && l[0] == '[') {
            net = lower(l) == "[net]";
            continue;
        }
        if (net && l == "enabled=0")
            g_enabled = false;
        if (net && l == "greet=0")
            g_greet = false;
    }
}
} // namespace

void fill_net(EmfApi *t) {
    t->net_channel = &api_net_channel;
    t->net_to_server = &api_net_to_server;
    t->net_to_player = &api_net_to_player;
    t->net_to_players = &api_net_to_players;
    t->net_peer = &api_net_peer;
}
void net_on_ready() {
    config();
    if (g_enabled && S.chat && S.chat->subscribe && S.chat->say && S.chat->unsubscribe) {
        KilnChatStatus st{};
        st.size = sizeof st;
        g_server_on = S.chat->status && S.chat->status(&st) == KILN_OK && st.receive_hooked && reader_gate_install() &&
                      S.chat->subscribe(g_owner, &on_line, nullptr, &g_chat_sub) == KILN_OK;
    }
    g_client_on = g_enabled && g_side == KILN_SIDE_CLIENT && net_client_install();
    if (!g_mods_check)
        g_mods_check = std::make_unique<emf::core::ModsCheck>(mods_check_deps());
    {
        std::lock_guard lock(g_mutex);
        g_running = true;
        g_stopping.fill(false);
        g_net = make_service();
    }
    logf(KILN_LOG_INFO, "EMF mod messages (v27): loopback on; chat server %s, client %s (official #!emf1 wire); mods check on join: on",
         g_server_on && g_greet ? "on" : "off", g_client_on ? "on" : "off");
}
void net_server_pre() {
    auto s = service();
    if (!s)
        return;
    std::vector<wire::Connection> connections;
    for (const auto &p : players())
        if (p.connection && p.account)
            connections.push_back({p.connection, p.account});
    s->server_tick(connections);
    size_t available = 0;
    {
        std::lock_guard lock(g_mutex);
        available = std::min<size_t>(g_out.size(), 32);
    }
    std::set<std::pair<uint32_t, uint64_t>> blocked;
    for (size_t i = 0; i < available; ++i) {
        Outbound o;
        {
            std::lock_guard lock(g_mutex);
            if (g_out.empty() || g_stopping[EMF_WORLD_SERVER])
                break;
            const auto next = std::find_if(g_out.begin(), g_out.end(), [&](const Outbound &queued) {
                return !blocked.contains({queued.handle, queued.account});
            });
            if (next == g_out.end())
                break;
            o = std::move(*next);
            g_out.erase(next);
            if (o.epoch != g_epoch[EMF_WORLD_SERVER] || now_ms() - o.when > kExpireMs)
                continue;
        }
        const auto p = connection(o.handle, o.account);
        if (p && p->entity && S.chat && S.chat->say) {
            if (!reader_ready(p->reader)) {
                // Keep this recipient's oldest line ahead of its remaining output, including concurrent enqueues.
                // Other recipients can still drain during this frame.
                blocked.emplace(o.handle, o.account);
                std::lock_guard lock(g_mutex);
                g_out.push_front(std::move(o));
                continue;
            }
            S.chat->say(p->entity, kstr(o.line),
                        p->entity); // typed, spoken as the recipient: upstream client verifies it
        }
    }
}
void net_after_step(EmfWorld *w, uint8_t kind) {
    if (!w || kind >= g_epoch.size() || !on_thread_of(w))
        return;
    std::lock_guard dispatch(g_dispatch);
    std::deque<Pending> jobs;
    {
        std::lock_guard lock(g_mutex);
        for (auto it = g_pending.begin(); it != g_pending.end();) {
            if (it->kind == kind) {
                jobs.push_back(std::move(*it));
                it = g_pending.erase(it);
            } else
                ++it;
        }
    }
    for (auto &j : jobs) {
        {
            std::lock_guard lock(g_mutex);
            if (!g_running || g_stopping[kind] || j.epoch != g_epoch[kind] || now_ms() - j.when > kExpireMs)
                continue;
        }
        j.fn(w);
    }
    // A joined game, once a second: its mod list goes to a server that has emf/mods (once per session, see ModsCheck).
    if (kind == EMF_WORLD_CLIENT && g_mods_check) {
        const uint64_t t = now_ms();
        if (t - g_mods_tick_at.load() >= 1000) {
            g_mods_tick_at = t;
            g_mods_check->client_tick();
        }
    }
}
void net_world_stopping(uint8_t kind) {
    if (kind >= g_epoch.size() || kind == EMF_WORLD_PREDICTION)
        return;
    std::lock_guard dispatch(g_dispatch);
    net_client_stop(); // drain entered engine calls before the world's chat storage can be destroyed
    std::lock_guard lock(g_mutex);
    ++g_epoch[kind];
    g_stopping[kind] = true;
    // Both endpoints form one session. A host may stop its worlds in either order.
    g_pending.clear();
    g_out.clear();
    if (kind == EMF_WORLD_SERVER)
        g_game_server = 0;
    if (g_running)
        g_net = make_service();
    net_client_clear();
    // The next server may greet this game while it still loads, before its client world starts (EMF greets 10 s and
    // 45 s after the connection appears): after a quiet moment the hooks take marked lines again. The first pump then
    // resets the fresh service once more; nothing of the stopped world is left to send.
    if (g_running && g_client_on)
        net_client_rearm();
}
void net_world_started(uint8_t kind) {
    if (kind >= g_epoch.size())
        return;
    bool resume = false;
    {
        std::lock_guard lock(g_mutex);
        ++g_epoch[kind];
        g_stopping[kind] = false;
        resume = g_running && g_client_on && kind == EMF_WORLD_CLIENT;
    }
    if (kind == EMF_WORLD_CLIENT)
        ++g_client_session; // a new session: the mod list is sent again, and its note may show again
    if (resume)
        net_client_resume();
}
void net_shutdown() {
    std::lock_guard dispatch(g_dispatch);
    {
        std::lock_guard lock(g_mutex);
        g_running = false;
        g_stopping.fill(true);
        for (auto &e : g_epoch)
            ++e;
        g_pending.clear();
        g_out.clear();
        g_channels.clear();
        g_net.reset();
    }
    net_client_stop();
    g_game_server = 0;
    if (g_receive_hook && S.hooks && S.hooks->revoke)
        S.hooks->revoke(g_receive_hook);
    g_receive_hook = 0;
    if (g_chat_sub && S.chat && S.chat->unsubscribe)
        S.chat->unsubscribe(g_chat_sub);
    g_chat_sub = 0;
}
bool net_client_line(std::string_view line, uint64_t speaker) {
    auto s = service();
    return s && s->client_line(line, speaker);
}
std::vector<std::string> net_client_take(size_t n) {
    auto s = service();
    return s ? s->client_take(n) : std::vector<std::string>{};
}
void net_client_put_back(std::vector<std::string> lines) {
    auto s = service();
    if (s)
        s->client_put_back(std::move(lines));
}
// Called by net_world_started's resume, after it advanced the client epoch: callbacks queued for the last session are
// already void, and none of the new one exists yet, so only the link is reset.
void net_client_new_world() {
    auto s = service();
    if (s)
        s->client_reset(/*keep_held_beacon=*/true);
}
void net_client_reset() {
    auto s = service();
    if (s)
        s->client_reset();
    std::lock_guard lock(g_mutex);
    ++g_epoch[EMF_WORLD_CLIENT];
    g_pending.erase(
        std::remove_if(g_pending.begin(), g_pending.end(), [](const Pending &p) { return p.kind == EMF_WORLD_CLIENT; }),
        g_pending.end());
}
} // namespace kemf
