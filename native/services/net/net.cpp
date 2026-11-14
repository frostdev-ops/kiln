// SPDX-License-Identifier: MIT
// kiln.net: Kiln frames between clients and the server over game channel 12 (docs/re/network.md,
// native/core/abi/services/kiln_net.h).
//
// Server role (dedicated server, or a client hosting a world):
//  - A mid-function hook right after ServerReceive's SyncSockets call (the GameServer is in r13 on the server exe, r15
//    in the client exe) drains channel 12 of every machine in the GameServer's 17 machine slots before the game's own
//    channel loop runs; the game therefore never sees a Kiln frame. Frames wait in an inbox until the next Pre of the
//    server world (same server frame, same thread).
//  - Pre: peers are refreshed from the machine slots and the player records, the lab transport is polled, then peer
//    events and frames are delivered to subscribers.
//  - send: SessionAlloc / copy / SessionCommit on the peer's socket, on the server thread. A frame the game refuses
//    for now (bandwidth budget) is retried in later frames for up to 2 s.
// Client role (game client):
//  - A hook on ClientReceive (GameClient update, main thread) drains channel 12 of the socket toward the server and
//    then sends Kiln's queued frames; the GameClient is alive for the duration of the call.
//  - Delivery in Pre of the client world (main thread); when no client-world step ran for a second (loading), the
//    hook delivers itself.
// Clients without Kiln (kiln_net.h, docs/re/network.md "Vanilla clients"):
//  - Proof: a client sends KILN_NET_KNOCK frames while it loads and once more right before its Gameplay Ready.
//  - Gate: a hook on the machine login holds an unproven peer's Gameplay Ready; the receive hook lets it go on.
//  - Withholding: a hook on the function that queues an entity for a player keeps entities of Kiln-only templates
//    from a player until Kiln.Network clears it, and sends what it kept back once cleared. Clearing records which
//    Kiln-only templates exist at that moment (what the player's acknowledged content state covers); a template added
//    later (a hot load) is kept from that player until it is cleared again, at its next content ack.
//  - Client guard: a mid hook where the game's create path uses its template lookup drops a create of a template this
//    game lacks, along the game's own failure path.
#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "game.h"
#include "kiln_api.h"
#include "lab.h"
#include "nlohmann/json.hpp"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"  // safetyhook::Context: the mid handlers' register view
#include "services/kiln_assets.h"
#include "services/kiln_net.h"

namespace {

using knet::F;
using knet::L;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uintptr_t g_base = 0;
uint32_t g_side = 0;
bool g_server_role = false, g_client_role = false, g_have_players = false, g_can_kick = false;
int g_gs_register = 13;
bool g_world_api = false;  // KilnApi has the client-support functions (subscribe_world_tick, ...)
std::atomic<bool> g_client_faulted{false};

kiln::MidHook2 g_after_sync;
kiln::RawHook g_client_recv;
kiln::RawHook g_machine_login;   // server role: Gameplay Ready (net/MachineLogin)
kiln::RawHook g_enqueue;         // server role: an entity queued for a player (net/ReplicationEnqueue)
kiln::MidHook2 g_client_ready;       // client role: right before the game sends Gameplay Ready (net/ClientGameplayReady)
kiln::MidHook2 g_create_guard;       // client role: the create path's use of its template lookup (net/CreateTemplateCall)

constexpr uint32_t kDefaultMaxFrame = 8000;  // channel 12 carries up to 8192 bytes per message
std::atomic<uint32_t> g_max_frame{kDefaultMaxFrame};
constexpr size_t kInboxFrames = 16384;
constexpr size_t kInboxBytes = 64u << 20;
constexpr size_t kOutboxFrames = 8192;
constexpr size_t kOutboxBytes = 32u << 20;
constexpr uint64_t kRetryMs = 2000;
constexpr double kPeerFramesPerSec = 2000, kPeerFrameBurst = 4000;
constexpr double kPeerBytesPerSec = 4.0 * 1024 * 1024, kPeerByteBurst = 8.0 * 1024 * 1024;
constexpr uint64_t kLabIdleMs = 60000;

constexpr uint8_t kKnock[4] = KILN_NET_KNOCK_INIT;
constexpr uint8_t kLoginGameplayReady = 2;               // keen::GameMessageLoginType::GameplayReady
constexpr uint32_t kTemplateType = 0x39768775u;          // FNV-1a of "keen::ecs::TemplateResource"
constexpr uint64_t kHoldCapMs = 30000;                   // Kiln.Network decides in seconds; this ends a hold if it can't
constexpr uint64_t kKnockMs[] = {1000, 3000};            // after the connection comes up; the last one goes with Gameplay Ready
constexpr int kMaxParents = 8;                           // parents walked when looking for a Kiln-only template
constexpr uint8_t kLabMagic[4] = {'K', 'L', 'A', 'B'};   // lab transport only: simulates what a vanilla peer does
constexpr uint8_t kLabVanilla = 'v', kLabGameplayReady = 'r';

// The local machine (slot 0) of a dedicated server or a hosted world is never gated: it is this process.
constexpr bool is_local_machine(KilnPeer peer) { return (peer & KILN_NET_PEER_LAB) == 0 && (peer & 0x7F) == 0; }

uint64_t now_ms() { return GetTickCount64(); }

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof(buf) - 1)))});
}

std::string guid_text(const uint8_t g[16]);

struct Bucket {
    double frames = kPeerFrameBurst, bytes = kPeerByteBurst;
    uint64_t at = 0;
    bool take(size_t n) {
        const uint64_t t = now_ms();
        if (at) {
            const double dt = static_cast<double>(t - at) / 1000.0;
            frames = std::min(kPeerFrameBurst, frames + dt * kPeerFramesPerSec);
            bytes = std::min(kPeerByteBurst, bytes + dt * kPeerBytesPerSec);
        }
        at = t;
        if (frames < 1 || bytes < static_cast<double>(n)) return false;
        frames -= 1;
        bytes -= static_cast<double>(n);
        return true;
    }
};

using Guid16 = std::array<uint8_t, 16>;
using KnownTemplates = std::set<Guid16>;  // Kiln-only template GUIDs (game byte order)

struct Peer {
    KilnPeer id = 0;
    uint32_t transport = KILN_NET_TRANSPORT_GAME;
    uint64_t lab_key = 0;
    bool reported_up = false;
    bool logged_frame = false;
    bool bound = false;  // lab only: a lab peer standing in for the player of a free slot (lab_selftest BIND)
    int32_t machine = -1;  // lab only: the loopback fake's machine it stands for (lab_selftest BIND_MACHINE)
    uint8_t slot_flags = 0;
    knet::Record rec{};
    bool has_player = false;
    uint64_t up_ms = 0, last_rx_ms = 0;
    uint64_t rx_frames = 0, rx_bytes = 0, tx_frames = 0, tx_bytes = 0, dropped = 0;
    Bucket bucket;
    // The Kiln-only templates there were when it was last cleared (withhold_set off), and kiln.assets' count then.
    std::shared_ptr<const KnownTemplates> known;
    uint64_t known_count = 0;
};

struct Frame {
    KilnPeer peer;
    std::vector<uint8_t> data;
};

struct Pending {
    KilnPeer peer;
    std::vector<uint8_t> data;
    uint64_t since;
};

struct Sub {
    KilnHandle handle;
    KilnOwner owner;
    KilnNetFrameFn on_frame;
    KilnNetPeerFn on_peer;
    void* user;
    bool fresh_server;  // replay UP for server-role peers already up
    bool fresh_client;  // ... and for the server peer of the client role
};

std::mutex g_mu;  // everything below
std::map<KilnPeer, Peer> g_peers;         // server role (game + lab)
std::vector<Frame> g_inbox;               // server role
size_t g_inbox_bytes = 0;
std::deque<Pending> g_retry;              // server role, game frames the game refused for now
std::vector<Sub> g_subs;
std::vector<std::pair<KilnPeer, uint32_t>> g_pending_events;  // server role, delivered in the next Pre
std::set<KilnPeer> g_proved;              // peers a frame came from: they have Kiln
std::map<KilnPeer, uint64_t> g_held;      // peers whose Gameplay Ready waits in the gate, since when
std::set<KilnPeer> g_let_in;              // Kiln.Network let them in (gate_release)
std::set<KilnPeer> g_cleared;             // peers Kiln-only entities go out to (withhold_set)
uint64_t g_next_handle = 1;
std::map<uint64_t, KilnPeer> g_lab_by_key;
uint32_t g_next_lab = 1;
uint16_t g_lab_port = 0;
std::atomic<uintptr_t> g_gs{0};
std::atomic<bool> g_server_seen{false};
bool g_can_hold = false, g_can_withhold = false;
int64_t g_world_storage = -1;
std::atomic<uint32_t> g_gate_mode{KILN_NET_GATE_OFF};
std::atomic<uint32_t> g_withheld_slots{0};    // read by the enqueue hook on the game's replication threads
std::atomic<uint32_t> g_resend_slots{0};      // slots whose kept-back entities are to go out, in their own job
std::atomic<uint64_t> g_withheld_total{0};
uint32_t g_test_slots = 0;                    // lab_selftest: slots forced to withhold
uintptr_t g_create_reader = 0, g_relevance_step = 0;  // lab_selftest only
// What a player's replication job kept back. Touched only when an entity is really kept back or sent, or when a new
// Kiln-only template exists that a cleared player hasn't acknowledged yet.
struct KeptBack {
    std::mutex mu;
    std::set<uint32_t> ids;
    std::shared_ptr<const KnownTemplates> known;  // cleared slots: the templates its player has (its peer's `known`)
    uint64_t newer = 0;                           // entities kept back since then for a template it hasn't acknowledged
    uint32_t first_entity = 0;                    // the first of those, and its template: logged in the next Pre
    Guid16 first_template{};
    bool note = false;
};
KeptBack g_kept[32];
// Cleared slots, and kiln.assets' Kiln-only template count when each one's `known` was taken. While the count now is the
// same, nothing is new to that player and the enqueue hook looks nothing up.
std::atomic<uint32_t> g_cleared_slots{0};
std::atomic<uint64_t> g_known_count[32];
// kiln.assets' Kiln-only template count, read at most once per step by the enqueue hook. Kiln adds templates on the tick
// thread (Pre or Post), when no job of the world runs; the step number moves in every Pre and in every receive step of
// the GameServer, so the first enqueue after a template was added reads the new count.
std::atomic<uint64_t> g_step{1}, g_count_step{0}, g_count{0};
constexpr size_t kMaxKeptBack = 65536;
constexpr uint8_t kStateInHeap = 8;           // the entity state's flag byte: it is in the player's heap (0x66922c)
std::atomic<const KilnAssetsApi*> g_assets{nullptr};

// client role
Peer g_server_peer;                       // id KILN_NET_PEER_SERVER
bool g_client_up = false;
uintptr_t g_client_conn = 0;              // socket pointer identifying the current connection
size_t g_knocks = 0;                      // timed knocks sent on this connection
bool g_knocked_ready = false;             // the knock that goes with Gameplay Ready was sent on this connection
bool g_client_heard = false;              // a frame came from the server on this connection
std::set<uint32_t> g_dropped;             // entities whose create was dropped on this connection
std::map<std::array<uint8_t, 16>, uint64_t> g_missing;  // templates this game lacks, creates dropped for each
std::atomic<bool> g_dropped_any{false};   // read on every create
std::atomic<uint64_t> g_dropped_total{0};
uintptr_t g_create_resume = 0;            // the create path's failure branch
std::vector<Frame> g_client_inbox;
size_t g_client_inbox_bytes = 0;
std::deque<std::vector<uint8_t>> g_client_outbox;
size_t g_client_outbox_bytes = 0;
std::vector<uint32_t> g_client_events;    // pending UP/DOWN for the server peer
std::atomic<uint64_t> g_last_client_hook{0}, g_last_client_pre{0};

struct Counters {
    std::atomic<uint64_t> rx_frames{0}, rx_bytes{0}, tx_frames{0}, tx_bytes{0}, dropped{0}, send_failures{0}, drains{0},
        game_frames{0};
} C;

std::vector<Sub> subs_snapshot() {
    std::lock_guard<std::mutex> lk(g_mu);
    return g_subs;
}

// ---------------------------------------------------------------------------------------------------------------
// Server role: drain (GameServer thread, inside ServerReceive)

struct DrainCtx {
    KilnPeer peer;
};

void sink_server_impl(const uint8_t* data, uint64_t size, DrainCtx* ctx);

// A slot's player is gone (or a lab test unbinds it): the slot's next player starts over.
void forget_slot(uint32_t slot) {
    if (slot >= 32) return;
    KeptBack& k = g_kept[slot];
    std::lock_guard<std::mutex> lk(k.mu);
    k.ids.clear();
    k.known.reset();
    k.newer = 0;
    k.note = false;
    g_known_count[slot].store(0, std::memory_order_release);
}

// Which player slots get no entities of Kiln-only templates: every remote player Kiln.Network hasn't cleared. A cleared
// player's slot gets the templates there were when it was cleared; entities of templates added since wait for its next
// clear (hk_enqueue).
void recompute_withheld_locked() {
    uint32_t mask = g_test_slots, cleared = 0, resend = 0;
    for (const auto& [id, p] : g_peers) {
        if (!p.has_player || is_local_machine(id) || p.rec.slot >= 32) continue;
        if (p.transport != KILN_NET_TRANSPORT_GAME && !p.bound) continue;
        const uint32_t slot = p.rec.slot, bit = 1u << slot;
        if (!g_cleared.count(id)) {
            mask |= bit;
            continue;
        }
        cleared |= bit;
        KeptBack& k = g_kept[slot];
        std::lock_guard<std::mutex> lk(k.mu);
        if (k.known == p.known) continue;
        // A newer clear: what was kept back for templates its player lacked goes out now.
        k.known = p.known;
        k.newer = 0;
        k.note = false;
        g_known_count[slot].store(p.known_count, std::memory_order_release);
        if (!k.ids.empty()) resend |= bit;
    }
    cleared &= ~mask;
    g_cleared_slots.store(cleared, std::memory_order_relaxed);
    // A slot no longer withheld sends what its job kept back. Release: a hook that sees the new mask sees the cleared slots.
    const uint32_t old = g_withheld_slots.exchange(mask, std::memory_order_acq_rel);
    resend |= old & ~mask;
    if (resend) g_resend_slots.fetch_or(resend, std::memory_order_relaxed);
}

// Every Kiln-only template there is now, and kiln.assets' count of them. nullptr when they can't be listed: the player
// is then taken to have every template, as before this check existed.
std::shared_ptr<const KnownTemplates> kiln_templates_now(const KilnAssetsApi* a, uint64_t* count) {
    *count = 0;
    if (!a) return std::make_shared<KnownTemplates>();
    // The count first: a template added while listing then counts as new to the player, never the other way round.
    *count = a->resource_new_count(kTemplateType);
    if (*count == 0) return std::make_shared<KnownTemplates>();
    size_t n = 0;
    a->resource_list(kTemplateType, nullptr, 0, &n);
    std::vector<KilnAssetKey> keys(n + 256);
    size_t got = 0;
    if (a->resource_list(kTemplateType, keys.data(), keys.size(), &got) != KILN_OK) {
        static std::atomic<int> warned{0};
        if (warned.fetch_add(1) == 0) logf(KILN_LOG_WARN, "net: kiln.assets didn't list the templates: a player is taken to have every Kiln-only template when it is cleared");
        return nullptr;
    }
    auto set = std::make_shared<KnownTemplates>();
    for (size_t i = 0; i < got && i < keys.size(); ++i) {
        if (a->resource_is_new(&keys[i]) != 1) continue;
        Guid16 g{};
        std::memcpy(g.data(), keys[i].guid, g.size());
        set->insert(g);
    }
    return set;
}

// A frame came from `peer`. Vanilla code never writes channel 12, so that peer has Kiln.
void note_proof_locked(KilnPeer peer) {
    if (!g_proved.insert(peer).second) return;
    logf(KILN_LOG_INFO, "net: peer 0x%x has Kiln (first frame on the channel)", peer);
}

// Whether `peer`'s Gameplay Ready goes on now; if not, it is held from here on.
bool gate_admit_locked(KilnPeer peer) {
    if (g_gate_mode.load(std::memory_order_relaxed) != KILN_NET_GATE_HOLD || g_proved.count(peer) || g_let_in.count(peer)) return true;
    g_held.emplace(peer, now_ms());
    logf(KILN_LOG_INFO, "net: peer 0x%x sent Gameplay Ready with no proof of Kiln: held, it gets no world data for now", peer);
    return false;
}

// Called from inside the guarded read loop: never let a C++ exception reach that frame.
void sink_server(const uint8_t* data, uint64_t size, void* user) {
    try {
        sink_server_impl(data, size, static_cast<DrainCtx*>(user));
    } catch (...) {
        C.dropped.fetch_add(1, std::memory_order_relaxed);
    }
}

void sink_server_impl(const uint8_t* data, uint64_t size, DrainCtx* ctx) {
    C.game_frames.fetch_add(1, std::memory_order_relaxed);
    if (size == 0 || size > 65536) {
        C.dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    // Plain-data copy into the inbox (no throw: reserve failures are caught by the caller's try).
    std::lock_guard<std::mutex> lk(g_mu);
    note_proof_locked(ctx->peer);
    if (size == sizeof(kKnock) && std::memcmp(data, kKnock, sizeof(kKnock)) == 0) return;  // the proof itself: nothing to deliver
    auto it = g_peers.find(ctx->peer);
    if (g_inbox.size() >= kInboxFrames || g_inbox_bytes + size > kInboxBytes) {
        C.dropped.fetch_add(1, std::memory_order_relaxed);
        if (it != g_peers.end()) it->second.dropped++;
        return;
    }
    if (it != g_peers.end() && !it->second.bucket.take(static_cast<size_t>(size))) {
        it->second.dropped++;
        C.dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_inbox.push_back(Frame{ctx->peer, std::vector<uint8_t>(data, data + size)});
    g_inbox_bytes += static_cast<size_t>(size);
}

void flush_retry(uintptr_t gs);

// Ends holds, inside ServerReceive: the game processes the held Gameplay Ready just as if it had arrived now.
void gate_pump(uintptr_t gs) {
    std::vector<KilnPeer> go, refuse;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_held.empty()) return;
        const uint64_t t = now_ms();
        const bool off = g_gate_mode.load(std::memory_order_relaxed) != KILN_NET_GATE_HOLD;
        for (auto it = g_held.begin(); it != g_held.end();) {
            const KilnPeer peer = it->first;
            const char* why = g_proved.count(peer) ? "it has Kiln" : g_let_in.count(peer) ? "Kiln.Network let it in" : off ? "the gate is off" : nullptr;
            if (why) {
                logf(KILN_LOG_INFO, "net: peer 0x%x goes on after %llu ms in the gate: %s", peer, static_cast<unsigned long long>(t - it->second), why);
                go.push_back(peer);
            } else if (t - it->second > kHoldCapMs) {
                logf(KILN_LOG_ERROR, "net: peer 0x%x was held for %llu s and Kiln.Network didn't decide: refusing it", peer, static_cast<unsigned long long>(kHoldCapMs / 1000));
                refuse.push_back(peer);
            } else {
                ++it;
                continue;
            }
            g_let_in.erase(peer);
            it = g_held.erase(it);
        }
    }
    const auto game_login = g_machine_login ? g_machine_login.original<knet::MachineLoginFn>() : nullptr;
    for (KilnPeer peer : go) {
        if (peer & KILN_NET_PEER_LAB) {
            logf(KILN_LOG_INFO, "net: lab peer 0x%x passed the gate", peer);
        } else if (!knet::machine_login(game_login, gs, peer, kLoginGameplayReady)) {
            logf(KILN_LOG_ERROR, "net: processing the held Gameplay Ready of peer 0x%x faulted", peer);
        }
    }
    for (KilnPeer peer : refuse) {
        if (peer & KILN_NET_PEER_LAB) {
            logf(KILN_LOG_INFO, "net: lab peer 0x%x refused by the gate's time limit", peer);
        } else if (!(g_can_kick && knet::login_failure(gs, peer, 1))) {
            // nothing else can end the hold: withholding still applies
            logf(KILN_LOG_ERROR, "net: can't refuse peer 0x%x on this build: letting it in", peer);
            knet::machine_login(game_login, gs, peer, kLoginGameplayReady);
        }
    }
}

void __fastcall hk_machine_login(uintptr_t gs, uint32_t peer, const uint8_t* message) {
    if (message && *message == kLoginGameplayReady && !is_local_machine(peer)) {
        std::lock_guard<std::mutex> lk(g_mu);
        if (!gate_admit_locked(peer)) return;
    }
    g_machine_login.call<void>(gs, peer, message);
}

// `id`, or a parent of it, has a template the game doesn't ship. Lock-free: kiln.assets answers from its snapshot.
bool entity_kiln_only(uintptr_t storage, uint32_t id, const KilnAssetsApi* a, bool* read = nullptr) {
    if (read) *read = false;
    KilnAssetKey key{};
    key.type = kTemplateType;
    for (int depth = 0; depth < kMaxParents && id != 0; ++depth) {
        uint32_t parent = 0;
        if (!knet::entity_template(storage, id, key.guid, &parent)) return false;
        if (read) *read = true;
        if (a->resource_is_new(&key) == 1) return true;
        id = parent;
    }
    return false;
}

// Sends what this player's job kept back, from inside that job: the game's own thread and moment for this queue. An id
// with no state left has gone out of the player's relevance; if it comes back the game queues it again.
void resend_kept_back(uintptr_t storage, uintptr_t player, uint8_t slot) {
    g_resend_slots.fetch_and(~(1u << slot), std::memory_order_relaxed);
    std::set<uint32_t> ids;
    {
        std::lock_guard<std::mutex> lk(g_kept[slot].mu);
        ids.swap(g_kept[slot].ids);
    }
    uint8_t options[0x20] = {};  // as the game's own re-queue of a changed entity (0x66914a)
    for (uint32_t id : ids) {
        uint8_t flags = 0;
        const uintptr_t state = knet::player_entity_state(player, id, &flags);
        if (state && (flags & kStateInHeap) == 0) g_enqueue.call<void>(storage, player, state, id, options);
    }
}

// kiln.assets' Kiln-only template count as of this step (see g_step).
uint64_t kiln_template_count(const KilnAssetsApi* a) {
    const uint64_t step = g_step.load(std::memory_order_acquire);
    if (g_count_step.load(std::memory_order_acquire) != step) {
        g_count.store(a->resource_new_count(kTemplateType), std::memory_order_relaxed);
        g_count_step.store(step, std::memory_order_release);
    }
    return g_count.load(std::memory_order_relaxed);
}

// `id`, or a parent of it, has a Kiln-only template that isn't in `known` (nullptr: every template is known). The first
// such template goes to *guid.
bool entity_has_unknown(uintptr_t storage, uint32_t id, const KilnAssetsApi* a, const KnownTemplates* known, Guid16* guid) {
    if (!known) return false;
    KilnAssetKey key{};
    key.type = kTemplateType;
    for (int depth = 0; depth < kMaxParents && id != 0; ++depth) {
        uint32_t parent = 0;
        if (!knet::entity_template(storage, id, key.guid, &parent)) return false;
        if (a->resource_is_new(&key) == 1) {
            std::memcpy(guid->data(), key.guid, guid->size());
            if (!known->count(*guid)) return true;
        }
        id = parent;
    }
    return false;
}

// A cleared player's slot, after a Kiln-only template was added: keeps the entity back if its template (or a parent's)
// came after the player's last clear. Its content ack brings that template's definition; the clear that follows sends it.
bool keep_back_newer(uintptr_t storage, uint32_t entity, const KilnAssetsApi* a, uint8_t slot) {
    KeptBack& k = g_kept[slot];
    std::shared_ptr<const KnownTemplates> known;
    {
        std::lock_guard<std::mutex> lk(k.mu);
        known = k.known;
    }
    Guid16 g{};
    if (!entity_has_unknown(storage, entity, a, known.get(), &g)) return false;
    std::lock_guard<std::mutex> lk(k.mu);
    if (k.known != known) return false;  // cleared again meanwhile: what it knows now includes this template
    g_withheld_total.fetch_add(1, std::memory_order_relaxed);
    if (k.ids.size() < kMaxKeptBack) k.ids.insert(entity);
    if (k.newer++ == 0) {
        k.first_entity = entity;
        k.first_template = g;
        k.note = true;
    }
    return true;
}

// The game's only way into a player's replication queue (0x6691a0: storage, the player's replication object, the
// entity's state for that player, the entity, options). Runs on the game's replication threads, per changed entity.
// For a cleared peer this is a few loads and the call while no Kiln-only template was added since its clear; after one
// was, it looks up the entity's template until the peer is cleared again.
void __fastcall hk_enqueue(uintptr_t storage, uintptr_t player, uintptr_t state, uint32_t entity, void* options) {
    const uint32_t slots = g_withheld_slots.load(std::memory_order_acquire);
    const uint32_t cleared = g_cleared_slots.load(std::memory_order_relaxed);
    const uint32_t resend = g_resend_slots.load(std::memory_order_relaxed);
    if ((slots | cleared | resend) != 0) {
        // The object's first byte is the player's slot (0x67d3e1 compares it with entity id - 1 to find the player's own entity).
        const uint8_t slot = *reinterpret_cast<const uint8_t*>(player);
        if (slot < 32) {
            const bool withheld = ((slots >> slot) & 1) != 0;
            if (!withheld && ((resend >> slot) & 1) != 0) resend_kept_back(storage, player, slot);
            if (withheld) {
                const KilnAssetsApi* a = g_assets.load(std::memory_order_acquire);
                if (a && entity_kiln_only(storage, entity, a)) {
                    g_withheld_total.fetch_add(1, std::memory_order_relaxed);
                    std::lock_guard<std::mutex> lk(g_kept[slot].mu);
                    if (g_kept[slot].ids.size() < kMaxKeptBack) g_kept[slot].ids.insert(entity);
                    return;
                }
            } else if (((cleared >> slot) & 1) != 0) {
                const KilnAssetsApi* a = g_assets.load(std::memory_order_acquire);
                if (a && kiln_template_count(a) != g_known_count[slot].load(std::memory_order_acquire) && keep_back_newer(storage, entity, a, slot))
                    return;
            }
        }
    }
    g_enqueue.call<void>(storage, player, state, entity, options);
}

// Logs, from the tick thread, the first entity kept from a cleared player for a template added after its clear.
void note_kept_newer() {
    for (uint32_t slot = 0; slot < 32; ++slot) {
        KeptBack& k = g_kept[slot];
        uint32_t entity = 0;
        Guid16 g{};
        {
            std::lock_guard<std::mutex> lk(k.mu);
            if (!k.note) continue;
            k.note = false;
            entity = k.first_entity;
            g = k.first_template;
        }
        KilnPeer peer = 0;
        {
            std::lock_guard<std::mutex> lk(g_mu);
            for (const auto& [id, p] : g_peers)
                if (p.has_player && p.rec.slot == slot && (p.transport == KILN_NET_TRANSPORT_GAME || p.bound)) peer = id;
        }
        logf(KILN_LOG_INFO, "net: peer 0x%x (slot %u): entity %u of the Kiln template %s, added after its last content ack, waits for its next one",
             peer, slot, entity, guid_text(g.data()).c_str());
    }
}

void drain_server(uintptr_t gs) {
    if (!gs) return;
    g_step.fetch_add(1, std::memory_order_acq_rel);  // see g_step
    g_gs.store(gs, std::memory_order_relaxed);
    if (!g_server_seen.exchange(true)) logf(KILN_LOG_INFO, "net: GameServer at %p (server role ready)", reinterpret_cast<void*>(gs));
    C.drains.fetch_add(1, std::memory_order_relaxed);
    knet::MachineSlot slots[32];
    const int n = knet::read_machine_slots(gs, slots, 32);
    for (int i = 0; i < n; ++i) {
        if (!slots[i].peer) continue;
        uintptr_t session = 0;
        const uintptr_t socket = knet::server_socket(gs, slots[i].peer, &session);
        if (!socket) continue;
        DrainCtx ctx{slots[i].peer};
        try {
            if (knet::drain(session, socket, &sink_server, &ctx) < 0) {
                static std::atomic<int> faults{0};
                if (faults.fetch_add(1) < 5) logf(KILN_LOG_ERROR, "net: draining machine 0x%x faulted", slots[i].peer);
            }
        } catch (...) {
            C.dropped.fetch_add(1);
        }
    }
    flush_retry(gs);
    gate_pump(gs);
}

void hk_after_sync(safetyhook::Context& ctx) {
    const uintptr_t gs = g_gs_register == 15 ? ctx.r15 : ctx.r13;
    drain_server(gs);
}

// ---------------------------------------------------------------------------------------------------------------
// Server role: send

KilnResult send_game(KilnPeer to, const uint8_t* data, uint32_t len, bool queue_on_fail) {
    const uintptr_t gs = g_gs.load(std::memory_order_relaxed);
    if (!gs) return KILN_ERR_UNAVAILABLE;
    uintptr_t session = 0;
    const uintptr_t socket = knet::server_socket(gs, to, &session);
    if (!socket) return KILN_ERR_NOT_FOUND;
    uint64_t cap = 0;
    const uint32_t st = knet::send(session, socket, data, len, &cap);
    if (st == 0) {
        C.tx_frames.fetch_add(1, std::memory_order_relaxed);
        C.tx_bytes.fetch_add(len, std::memory_order_relaxed);
        std::lock_guard<std::mutex> lk(g_mu);
        auto it = g_peers.find(to);
        if (it != g_peers.end()) {
            it->second.tx_frames++;
            it->second.tx_bytes += len;
        }
        return KILN_OK;
    }
    if (st == 0xFE) {
        if (cap >= 1024 && cap < g_max_frame.load()) {
            g_max_frame.store(static_cast<uint32_t>(cap & ~3ull));
            logf(KILN_LOG_WARN, "net: channel socket capacity is %llu bytes; max frame lowered", static_cast<unsigned long long>(cap));
        }
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    if (queue_on_fail) {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_retry.size() < kOutboxFrames) {
            g_retry.push_back(Pending{to, std::vector<uint8_t>(data, data + len), now_ms()});
            return KILN_OK;
        }
    }
    C.send_failures.fetch_add(1, std::memory_order_relaxed);
    static std::atomic<int> logged{0};
    if (logged.fetch_add(1) < 10) logf(KILN_LOG_WARN, "net: the game refused a frame to 0x%x (status %u)", to, st);
    return KILN_ERR_INTERNAL;
}

void flush_retry(uintptr_t gs) {
    if (!gs) return;
    std::deque<Pending> work;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_retry.empty()) return;
        work.swap(g_retry);
    }
    const uint64_t t = now_ms();
    std::deque<Pending> keep;
    bool blocked = false;
    for (auto& p : work) {
        if (!blocked) {
            uintptr_t session = 0;
            const uintptr_t socket = knet::server_socket(gs, p.peer, &session);
            if (!socket) {
                C.send_failures.fetch_add(1);
                continue;
            }
            uint64_t cap = 0;
            const uint32_t st = knet::send(session, socket, p.data.data(), static_cast<uint32_t>(p.data.size()), &cap);
            if (st == 0) {
                C.tx_frames.fetch_add(1);
                C.tx_bytes.fetch_add(p.data.size());
                continue;
            }
            blocked = true;  // keep order: everything after waits too
        }
        if (t - p.since > kRetryMs) {
            C.send_failures.fetch_add(1);
            std::lock_guard<std::mutex> lk(g_mu);
            auto it = g_peers.find(p.peer);
            if (it != g_peers.end()) it->second.dropped++;
            continue;
        }
        keep.push_back(std::move(p));
    }
    std::lock_guard<std::mutex> lk(g_mu);
    for (auto it = keep.rbegin(); it != keep.rend(); ++it) g_retry.push_front(std::move(*it));
}

bool has_pending_for(KilnPeer peer) {
    for (const auto& p : g_retry)
        if (p.peer == peer) return true;
    return false;
}

// ---------------------------------------------------------------------------------------------------------------
// Server role: Pre (peers, lab, delivery)

struct Event {
    KilnPeer peer;
    uint32_t kind;
};

void poll_lab(std::vector<Event>& events) {
    if (!knet::lab::is_open()) return;
    static uint8_t buf[65536];
    for (int i = 0; i < 1024; ++i) {
        uint64_t from = 0;
        const int n = knet::lab::recv(buf, sizeof(buf), &from);
        if (n <= 0) break;
        std::lock_guard<std::mutex> lk(g_mu);
        KilnPeer id;
        auto k = g_lab_by_key.find(from);
        if (k == g_lab_by_key.end()) {
            id = KILN_NET_PEER_LAB | g_next_lab++;
            g_lab_by_key[from] = id;
            Peer p;
            p.id = id;
            p.transport = KILN_NET_TRANSPORT_LAB;
            p.lab_key = from;
            p.up_ms = now_ms();
            std::snprintf(p.rec.name, sizeof(p.rec.name), "lab-%u", id & 0xFFFF);
            p.rec.slot = 0xFFFFFFFFu;
            g_peers[id] = p;
            events.push_back({id, KILN_NET_PEER_UP});
            logf(KILN_LOG_INFO, "net: lab peer 0x%x up (%s)", id, knet::lab::describe(from).c_str());
        } else {
            id = k->second;
        }
        Peer& p = g_peers[id];
        p.last_rx_ms = now_ms();
        if (n == 5 && std::memcmp(buf, kLabMagic, sizeof(kLabMagic)) == 0) {
            // Lab only: what a vanilla peer does. It sends nothing on the channel, so this is no proof.
            if (buf[4] == kLabGameplayReady && gate_admit_locked(id)) logf(KILN_LOG_INFO, "net: lab peer 0x%x passed the gate", id);
            continue;
        }
        note_proof_locked(id);
        if (n == sizeof(kKnock) && std::memcmp(buf, kKnock, sizeof(kKnock)) == 0) continue;
        if (static_cast<size_t>(n) > 65535 || g_inbox.size() >= kInboxFrames || !p.bucket.take(static_cast<size_t>(n))) {
            p.dropped++;
            C.dropped.fetch_add(1);
            continue;
        }
        g_inbox.push_back(Frame{id, std::vector<uint8_t>(buf, buf + n)});
        g_inbox_bytes += static_cast<size_t>(n);
    }
    // forget idle lab peers
    std::lock_guard<std::mutex> lk(g_mu);
    const uint64_t t = now_ms();
    for (auto it = g_peers.begin(); it != g_peers.end();) {
        if (it->second.transport == KILN_NET_TRANSPORT_LAB && t - std::max(it->second.last_rx_ms, it->second.up_ms) > kLabIdleMs) {
            events.push_back({it->first, KILN_NET_PEER_DOWN});
            logf(KILN_LOG_INFO, "net: lab peer 0x%x idle for %llu s: down", it->first, kLabIdleMs / 1000);
            g_lab_by_key.erase(it->second.lab_key);
            g_proved.erase(it->first);
            g_held.erase(it->first);
            g_let_in.erase(it->first);
            g_cleared.erase(it->first);
            if (it->second.bound) forget_slot(it->second.rec.slot);
            it = g_peers.erase(it);
            recompute_withheld_locked();
        } else {
            ++it;
        }
    }
}

void refresh_game_peers(std::vector<Event>& events) {
    const uintptr_t gs = g_gs.load(std::memory_order_relaxed);
    if (!gs) return;
    knet::MachineSlot slots[32];
    const int n = knet::read_machine_slots(gs, slots, 32);
    if (n < 0) return;
    knet::Record recs[64];
    const int nr = g_have_players ? knet::read_records(gs, recs, 64) : -1;
    std::lock_guard<std::mutex> lk(g_mu);
    std::map<KilnPeer, knet::MachineSlot> live;
    for (int i = 0; i < n; ++i)
        if (slots[i].peer) live[slots[i].peer] = slots[i];
    for (auto it = g_peers.begin(); it != g_peers.end();) {
        if (it->second.transport == KILN_NET_TRANSPORT_GAME && !live.count(it->first)) {
            events.push_back({it->first, KILN_NET_PEER_DOWN});
            logf(KILN_LOG_INFO, "net: peer 0x%x down ('%s')", it->first, it->second.rec.name);
            g_held.erase(it->first);
            g_let_in.erase(it->first);
            g_cleared.erase(it->first);
            if (it->second.has_player) forget_slot(it->second.rec.slot);  // the slot's next player starts over
            it = g_peers.erase(it);
        } else {
            ++it;
        }
    }
    for (auto& [peer, slot] : live) {
        auto it = g_peers.find(peer);
        if (it == g_peers.end()) {
            Peer p;
            p.id = peer;
            p.up_ms = now_ms();
            p.rec.slot = 0xFFFFFFFFu;
            it = g_peers.emplace(peer, p).first;
            events.push_back({peer, KILN_NET_PEER_UP});
            logf(KILN_LOG_INFO, "net: peer 0x%x up (machine slot %u, flags 0x%x)", peer, peer & 0x7F, slot.flags);
        }
        Peer& p = it->second;
        p.slot_flags = slot.flags;
        bool found = false;
        for (int r = 0; r < nr; ++r) {
            if (recs[r].connection != 0 && recs[r].peer == peer) {
                const bool had = p.has_player;
                p.rec = recs[r];
                if (p.rec.entity == 0) p.rec.entity = recs[r].slot + 1;
                p.has_player = true;
                found = true;
                if (!had) logf(KILN_LOG_INFO, "net: peer 0x%x is player '%s' (slot %u, state %u)", peer, p.rec.name, p.rec.slot, p.rec.state);
                break;
            }
        }
        if (!found) p.has_player = false;
    }
    // A proof can come before the peer is in g_peers; it ends with the machine.
    for (auto it = g_proved.begin(); it != g_proved.end();)
        it = ((*it & KILN_NET_PEER_LAB) == 0 && !live.count(*it)) ? g_proved.erase(it) : std::next(it);
    recompute_withheld_locked();
}

void deliver(const std::vector<Event>& events, std::vector<Frame>& frames, bool client) {
    auto subs = subs_snapshot();
    // replay UP for subscribers that joined late
    std::vector<KilnPeer> up;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        bool any_fresh = false;
        for (auto& s : g_subs)
            if (client ? s.fresh_client : s.fresh_server) any_fresh = true;
        if (any_fresh) {
            if (client) {
                if (g_client_up) up.push_back(KILN_NET_PEER_SERVER);
            } else {
                for (auto& [id, p] : g_peers)
                    if (p.reported_up) up.push_back(id);
            }
        }
    }
    for (auto& s : subs) {
        if (!(client ? s.fresh_client : s.fresh_server) || !s.on_peer) continue;
        for (KilnPeer p : up) s.on_peer(p, KILN_NET_PEER_UP, s.user);
    }
    {
        std::lock_guard<std::mutex> lk(g_mu);
        for (auto& s : g_subs) (client ? s.fresh_client : s.fresh_server) = false;
    }
    for (const auto& e : events)
        if (e.kind == KILN_NET_PEER_UP)
            for (auto& s : subs)
                if (s.on_peer) s.on_peer(e.peer, e.kind, s.user);
    {
        std::lock_guard<std::mutex> lk(g_mu);
        for (const auto& e : events)
            if (e.kind == KILN_NET_PEER_UP && !client) {
                auto it = g_peers.find(e.peer);
                if (it != g_peers.end()) it->second.reported_up = true;
            }
    }
    for (auto& f : frames) {
        C.rx_frames.fetch_add(1, std::memory_order_relaxed);
        C.rx_bytes.fetch_add(f.data.size(), std::memory_order_relaxed);
        {
            std::lock_guard<std::mutex> lk(g_mu);
            Peer* p = nullptr;
            if (client) {
                p = &g_server_peer;
            } else {
                auto it = g_peers.find(f.peer);
                if (it != g_peers.end()) p = &it->second;
            }
            if (!p) continue;  // peer went away before delivery
            p->rx_frames++;
            p->rx_bytes += f.data.size();
            if (!p->logged_frame) {
                p->logged_frame = true;
                logf(KILN_LOG_INFO, "net: first frame from peer 0x%x (%zu bytes)", f.peer, f.data.size());
            }
        }
        for (auto& s : subs)
            if (s.on_frame) s.on_frame(f.peer, f.data.data(), static_cast<uint32_t>(f.data.size()), s.user);
    }
    for (const auto& e : events)
        if (e.kind == KILN_NET_PEER_DOWN)
            for (auto& s : subs)
                if (s.on_peer) s.on_peer(e.peer, e.kind, s.user);
}

// kiln.assets may load after kiln.net. Nothing can be Kiln-only without it: every new template goes through it.
const KilnAssetsApi* assets_api() {
    if (const KilnAssetsApi* a = g_assets.load(std::memory_order_acquire)) return a;
    const auto* a = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1));
    if (!a || a->header.size < offsetof(KilnAssetsApi, resource_new_count) + sizeof(void*)) return nullptr;
    g_assets.store(a, std::memory_order_release);
    return a;
}

void KILN_CALL on_server_pre(const KilnTickInfo*, void*) {
    g_step.fetch_add(1, std::memory_order_acq_rel);  // see g_step
    assets_api();
    note_kept_newer();
    std::vector<Event> events;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        for (auto& [p, k] : g_pending_events) events.push_back({p, k});
        g_pending_events.clear();
    }
    refresh_game_peers(events);
    poll_lab(events);
    flush_retry(g_gs.load(std::memory_order_relaxed));
    std::vector<Frame> frames;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        frames.swap(g_inbox);
        g_inbox_bytes = 0;
    }
    deliver(events, frames, false);
}

// ---------------------------------------------------------------------------------------------------------------
// Client role

void sink_client_impl(const uint8_t* data, uint64_t size);
void sink_client(const uint8_t* data, uint64_t size, void*) {
    try {
        sink_client_impl(data, size);
    } catch (...) {
        C.dropped.fetch_add(1, std::memory_order_relaxed);
    }
}

void sink_client_impl(const uint8_t* data, uint64_t size) {
    C.game_frames.fetch_add(1, std::memory_order_relaxed);
    if (size == 0 || size > 65536) {
        C.dropped.fetch_add(1);
        return;
    }
    std::lock_guard<std::mutex> lk(g_mu);
    g_client_heard = true;
    if (g_client_inbox.size() >= kInboxFrames || g_client_inbox_bytes + size > kInboxBytes) {
        C.dropped.fetch_add(1);
        g_server_peer.dropped++;
        return;
    }
    g_client_inbox.push_back(Frame{KILN_NET_PEER_SERVER, std::vector<uint8_t>(data, data + size)});
    g_client_inbox_bytes += static_cast<size_t>(size);
}

void client_set_up(bool up, uintptr_t conn) {
    std::lock_guard<std::mutex> lk(g_mu);
    if (up && g_client_up && conn == g_client_conn) return;
    if (g_client_up && (!up || conn != g_client_conn)) {
        g_client_events.push_back(KILN_NET_PEER_DOWN);
        g_client_outbox.clear();
        g_client_outbox_bytes = 0;
        g_client_inbox.clear();
        g_client_inbox_bytes = 0;
        g_client_up = false;
        logf(KILN_LOG_INFO, "net: connection to the server closed");
    }
    if (up) {
        g_client_up = true;
        g_client_conn = conn;
        g_server_peer = Peer{};
        g_server_peer.id = KILN_NET_PEER_SERVER;
        g_server_peer.up_ms = now_ms();
        g_server_peer.rec.slot = 0xFFFFFFFFu;
        std::snprintf(g_server_peer.rec.name, sizeof(g_server_peer.rec.name), "server");
        g_knocks = 0;
        g_knocked_ready = false;
        g_client_heard = false;
        g_dropped.clear();
        g_missing.clear();
        g_dropped_any.store(false, std::memory_order_relaxed);
        g_client_events.push_back(KILN_NET_PEER_UP);
        logf(KILN_LOG_INFO, "net: connected to a server (channel socket %p)", reinterpret_cast<void*>(conn));
    }
}

void client_deliver() {
    if (g_client_faulted.load()) return;
    std::vector<Frame> frames;
    std::vector<Event> events;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        frames.swap(g_client_inbox);
        g_client_inbox_bytes = 0;
        for (uint32_t e : g_client_events) events.push_back({KILN_NET_PEER_SERVER, e});
        g_client_events.clear();
    }
    // A DOWN followed by an UP (reconnect) must not deliver the old connection's frames after the UP: frames were
    // cleared on DOWN, so whatever is here belongs to the current connection.
    deliver(events, frames, true);
}

void client_flush(uintptr_t session, uintptr_t socket) {
    for (int i = 0; i < 4096; ++i) {
        std::vector<uint8_t> f;
        {
            std::lock_guard<std::mutex> lk(g_mu);
            if (g_client_outbox.empty()) return;
            f = std::move(g_client_outbox.front());
            g_client_outbox.pop_front();
            g_client_outbox_bytes -= f.size();
        }
        uint64_t cap = 0;
        const uint32_t st = knet::send(session, socket, f.data(), static_cast<uint32_t>(f.size()), &cap);
        if (st == 0) {
            C.tx_frames.fetch_add(1);
            C.tx_bytes.fetch_add(f.size());
            std::lock_guard<std::mutex> lk(g_mu);
            g_server_peer.tx_frames++;
            g_server_peer.tx_bytes += f.size();
            continue;
        }
        if (st == 0xFE) {
            C.send_failures.fetch_add(1);
            if (cap >= 1024 && cap < g_max_frame.load()) g_max_frame.store(static_cast<uint32_t>(cap & ~3ull));
            continue;  // too big: drop it
        }
        // budget or transient: put it back and try next frame
        std::lock_guard<std::mutex> lk(g_mu);
        g_client_outbox.push_front(std::move(f));
        g_client_outbox_bytes += g_client_outbox.front().size();
        return;
    }
}

void client_queue_knock_locked() {
    g_client_outbox.emplace_back(kKnock, kKnock + sizeof(kKnock));
    g_client_outbox_bytes += sizeof(kKnock);
}

// The proof while the client loads: bounded, because a vanilla server logs one warning per frame on this channel.
void client_knock_on_time() {
    std::lock_guard<std::mutex> lk(g_mu);
    if (!g_client_up || g_client_heard || g_knocks >= std::size(kKnockMs)) return;
    if (now_ms() - g_server_peer.up_ms < kKnockMs[g_knocks]) return;
    ++g_knocks;
    client_queue_knock_locked();
}

// The game is about to send Gameplay Ready (0x86d9f0; called until it has). The proof goes first, in this call, so a
// slow load can't put Gameplay Ready ahead of it; the server reads channel 12 before the game's own channels.
void hk_client_ready(safetyhook::Context& ctx) {
    if (g_client_faulted.load()) return;
    const uintptr_t gc = ctx.rcx;
    try {
        uintptr_t session = 0, set = 0;
        const uintptr_t socket = knet::client_socket(gc, &session, &set);
        if (!socket) return;
        client_set_up(true, socket);
        {
            std::lock_guard<std::mutex> lk(g_mu);
            if (g_knocked_ready) return;
            g_knocked_ready = true;
            client_queue_knock_locked();
        }
        client_flush(session, socket);
    } catch (...) {
        if (!g_client_faulted.exchange(true))
            logf(KILN_LOG_ERROR, "net: exception preparing Gameplay Ready; Kiln client networking disabled until restart");
    }
}

std::string guid_text(const uint8_t g[16]) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1], g[0], g[5], g[4], g[7],
                  g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}

// Diagnostics: the first entity of each Kiln-only template this game creates from the server, with the TemplateInfo it found (compare
// kiln.content's "registered in the client world: TemplateInfo ..." line), once per template in this process: whether a
// placed Kiln prop reached this client at all.
std::set<std::array<uint8_t, 16>> g_kiln_created;
void note_kiln_create(safetyhook::Context& ctx) {
    const KilnAssetsApi* a = assets_api();
    if (!a) return;
    KilnAssetKey key{};
    std::memcpy(key.guid, ctx.xmm6.u8, sizeof key.guid);
    key.type = kTemplateType;
    if (a->resource_is_new(&key) != 1) return;
    std::array<uint8_t, 16> g{};
    std::memcpy(g.data(), key.guid, g.size());
    std::lock_guard<std::mutex> lk(g_mu);
    if (g_kiln_created.size() >= 4096 || !g_kiln_created.insert(g).second) return;
    logf(KILN_LOG_INFO, "net: this game creates entity %u of the Kiln template %s (TemplateInfo %p; first of that template)",
         *reinterpret_cast<const uint32_t*>(ctx.rbp + L.create_entity), guid_text(key.guid).c_str(), reinterpret_cast<void*>(ctx.r14));
}

// The game's create path right before it uses its template lookup (0x961d07, the only call of 0x987910 a message can make
// fail while the entity doesn't exist yet): r14 is the TemplateInfo, 0 on a miss, and the game doesn't test it. On every
// entity create, so the usual case is one test, or two when something was dropped on this connection.
// Dropping resumes at the branch the game takes when that call fails: it frees what the call's argument set up and
// returns an empty result, and its caller then discards the message (0x9c70ee).
void hk_create_template(safetyhook::Context& ctx) {
    const bool miss = ctx.r14 == 0;
    if (!miss) note_kiln_create(ctx);
    if (!miss && !g_dropped_any.load(std::memory_order_relaxed)) return;
    const uint32_t entity = *reinterpret_cast<const uint32_t*>(ctx.rbp + L.create_entity);
    const uint32_t parent = *reinterpret_cast<const uint32_t*>(ctx.rbp + L.create_parent);
    if (!miss && parent == 0) return;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (miss) {
            std::array<uint8_t, 16> guid{};
            std::memcpy(guid.data(), ctx.xmm6.u8, guid.size());
            if (++g_missing[guid] == 1)
                logf(KILN_LOG_WARN, "net: this game has no template %s, which the server uses: its entities aren't created here (first: %u). A server mod you don't have adds it.",
                     guid_text(guid.data()).c_str(), entity);
        } else if (!g_dropped.count(parent)) {
            return;  // a child of something this game has
        }
        g_dropped.insert(entity);
    }
    g_dropped_any.store(true, std::memory_order_relaxed);
    g_dropped_total.fetch_add(1, std::memory_order_relaxed);
    ctx.rip = g_create_resume;
}

using ClientReceiveFn = void(__fastcall*)(uintptr_t gc);

void __fastcall hk_client_receive(uintptr_t gc) {
    if (g_client_faulted.load()) return g_client_recv.call<void>(gc);
    g_last_client_hook.store(now_ms(), std::memory_order_relaxed);
    uintptr_t session = 0, set = 0;
    const uintptr_t socket = knet::client_socket(gc, &session, &set);
    try {
        if (socket) {
            client_set_up(true, socket);
            C.drains.fetch_add(1, std::memory_order_relaxed);
            if (knet::drain(session, socket, &sink_client, nullptr) < 0) {
                static std::atomic<int> faults{0};
                if (faults.fetch_add(1) < 5) logf(KILN_LOG_ERROR, "net: draining the server socket faulted");
            }
        } else {
            client_set_up(false, 0);
        }
        // No client-world step for a while (loading screens): deliver from here, on the main thread.
        if (now_ms() - g_last_client_pre.load(std::memory_order_relaxed) > 1000) client_deliver();
        if (socket) {
            client_knock_on_time();
            client_flush(session, socket);
        }
    } catch (...) {
        // Includes hardware faults in subscribers delivered while no client-world tick is running.
        if (!g_client_faulted.exchange(true))
            logf(KILN_LOG_ERROR, "net: exception in ClientReceive; Kiln client networking disabled until restart");
    }
    g_client_recv.call<void>(gc);
}

void KILN_CALL on_client_pre(const KilnTickInfo*, void*) {
    if (g_client_faulted.load()) return;
    g_last_client_pre.store(now_ms(), std::memory_order_relaxed);
    if (g_client_up && now_ms() - g_last_client_hook.load(std::memory_order_relaxed) > 3000) client_set_up(false, 0);
    client_deliver();
}

void KILN_CALL on_world_event(const KilnWorldEvent* ev, void*) {
    if (ev && ev->event == KILN_WORLD_STOPPING && ev->kind == KILN_WORLD_CLIENT) {
        client_set_up(false, 0);
        client_deliver();
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Table

void fill_info(const Peer& p, KilnNetPeerInfo* out) {
    std::memset(out, 0, sizeof(*out));
    out->size = sizeof(KilnNetPeerInfo);
    out->peer = p.id;
    out->transport = p.transport;
    out->flags = (p.has_player ? KILN_NET_PEER_HAS_PLAYER : 0u) | ((p.slot_flags & 1) ? KILN_NET_PEER_LOGGED_IN : 0u) |
                 (g_proved.count(p.id) ? KILN_NET_PEER_PROVED : 0u) | (g_held.count(p.id) ? KILN_NET_PEER_HELD : 0u);
    if (p.has_player && p.rec.slot < 32 && ((g_withheld_slots.load(std::memory_order_relaxed) >> p.rec.slot) & 1) != 0)
        out->flags |= KILN_NET_PEER_WITHHELD;
    out->entity = p.has_player ? p.rec.entity : 0;
    out->slot = p.has_player ? p.rec.slot : 0xFFFFFFFFu;
    out->connection = p.has_player ? p.rec.connection : 0;
    out->record_state = p.has_player ? p.rec.state : 0;
    out->account = p.has_player ? p.rec.account : 0;
    out->up_ms = p.up_ms;
    out->rx_frames = p.rx_frames;
    out->rx_bytes = p.rx_bytes;
    out->tx_frames = p.tx_frames;
    out->tx_bytes = p.tx_bytes;
    out->dropped = p.dropped;
    std::snprintf(out->name, sizeof(out->name), "%s", p.rec.name);
}

KilnResult KILN_CALL t_status(KilnNetStatus* out) {
    if (!out || out->size < sizeof(KilnNetStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof(*out));
    out->size = sizeof(KilnNetStatus);
    out->roles = (g_server_role ? KILN_NET_ROLE_SERVER : 0u) |
                 (g_client_role && !g_client_faulted.load() ? KILN_NET_ROLE_CLIENT : 0u);
    out->server_ready = g_server_seen.load() ? 1 : 0;
    out->lab = knet::lab::is_open() ? 1 : 0;
    out->channel = static_cast<uint32_t>(L.channel);
    out->max_frame = g_max_frame.load();
    out->lab_port = knet::lab::is_open() ? g_lab_port : 0;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        out->client_ready = g_client_up && !g_client_faulted.load() ? 1 : 0;
        out->peers = static_cast<uint32_t>(g_peers.size()) + (g_client_up ? 1u : 0u);
        out->subscribers = static_cast<uint32_t>(g_subs.size());
    }
    out->rx_frames = C.rx_frames;
    out->rx_bytes = C.rx_bytes;
    out->tx_frames = C.tx_frames;
    out->tx_bytes = C.tx_bytes;
    out->dropped = C.dropped;
    out->send_failures = C.send_failures;
    out->drains = C.drains;
    out->game_frames_seen = C.game_frames;
    return KILN_OK;
}

KilnResult KILN_CALL t_subscribe(KilnOwner owner, KilnNetFrameFn on_frame, KilnNetPeerFn on_peer, void* user,
                                 KilnHandle* out) {
    if (!on_frame && !on_peer) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> lk(g_mu);
    const KilnHandle h = g_next_handle++;
    g_subs.push_back(Sub{h, owner, on_frame, on_peer, user, true, true});
    if (out) *out = h;
    return KILN_OK;
}

KilnResult KILN_CALL t_unsubscribe(KilnHandle handle) {
    std::lock_guard<std::mutex> lk(g_mu);
    auto it = std::find_if(g_subs.begin(), g_subs.end(), [&](const Sub& s) { return s.handle == handle; });
    if (it == g_subs.end()) return KILN_ERR_NOT_FOUND;
    g_subs.erase(it);
    return KILN_OK;
}

bool on_server_thread() {
    if (g_api->on_tick_thread()) return true;
    return g_world_api && g_api->on_world_thread(KILN_WORLD_SERVER);
}

KilnResult KILN_CALL t_send(KilnPeer to, const uint8_t* data, uint32_t len) {
    if (!data || len == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (len > g_max_frame.load()) return KILN_ERR_BUFFER_TOO_SMALL;
    if (to == KILN_NET_PEER_SERVER) {
        if (!g_client_role || g_client_faulted.load()) return KILN_ERR_UNAVAILABLE;
        std::lock_guard<std::mutex> lk(g_mu);
        if (!g_client_up) return KILN_ERR_NOT_FOUND;
        if (g_client_outbox.size() >= kOutboxFrames || g_client_outbox_bytes + len > kOutboxBytes) {
            g_server_peer.dropped++;
            C.dropped.fetch_add(1);
            return KILN_ERR_BUFFER_TOO_SMALL;
        }
        g_client_outbox.emplace_back(data, data + len);
        g_client_outbox_bytes += len;
        return KILN_OK;
    }
    if (to & KILN_NET_PEER_LAB) {
        uint64_t key = 0;
        {
            std::lock_guard<std::mutex> lk(g_mu);
            auto it = g_peers.find(to);
            if (it == g_peers.end() || it->second.transport != KILN_NET_TRANSPORT_LAB) return KILN_ERR_NOT_FOUND;
            key = it->second.lab_key;
            it->second.tx_frames++;
            it->second.tx_bytes += len;
        }
        if (!knet::lab::send(key, data, len)) return KILN_ERR_INTERNAL;
        C.tx_frames.fetch_add(1);
        C.tx_bytes.fetch_add(len);
        return KILN_OK;
    }
    if (!g_server_role) return KILN_ERR_UNAVAILABLE;
    if (!on_server_thread()) return KILN_ERR_WRONG_THREAD;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (!g_peers.count(to)) return KILN_ERR_NOT_FOUND;
        if (has_pending_for(to)) {  // keep order behind frames already waiting
            g_retry.push_back(Pending{to, std::vector<uint8_t>(data, data + len), now_ms()});
            return KILN_OK;
        }
    }
    return send_game(to, data, len, true);
}

KilnResult KILN_CALL t_peers(KilnNetPeerInfo* out, uint32_t capacity, uint32_t* count) {
    std::lock_guard<std::mutex> lk(g_mu);
    uint32_t n = 0;
    auto put = [&](const Peer& p) {
        if (out && n < capacity) fill_info(p, &out[n]);
        ++n;
    };
    if (g_client_up) put(g_server_peer);
    for (auto& [id, p] : g_peers) put(p);
    if (count) *count = n;
    return (out && n > capacity) ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_peer_info(KilnPeer peer, KilnNetPeerInfo* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> lk(g_mu);
    if (peer == KILN_NET_PEER_SERVER) {
        if (!g_client_up) return KILN_ERR_NOT_FOUND;
        fill_info(g_server_peer, out);
        return KILN_OK;
    }
    auto it = g_peers.find(peer);
    if (it == g_peers.end()) return KILN_ERR_NOT_FOUND;
    fill_info(it->second, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_kick(KilnPeer peer, uint32_t reason) {
    if (peer & KILN_NET_PEER_LAB) {
        std::lock_guard<std::mutex> lk(g_mu);
        auto it = g_peers.find(peer);
        if (it == g_peers.end()) return KILN_ERR_NOT_FOUND;
        g_lab_by_key.erase(it->second.lab_key);
        if (it->second.bound) forget_slot(it->second.rec.slot);
        g_peers.erase(it);
        g_proved.erase(peer);
        g_held.erase(peer);
        g_let_in.erase(peer);
        g_cleared.erase(peer);
        recompute_withheld_locked();
        g_pending_events.push_back({peer, KILN_NET_PEER_DOWN});
        logf(KILN_LOG_INFO, "net: lab peer 0x%x kicked (reason %u)", peer, reason);
        return KILN_OK;
    }
    if (!g_server_role || !g_can_kick) return KILN_ERR_UNAVAILABLE;
    if (!on_server_thread()) return KILN_ERR_WRONG_THREAD;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (!g_peers.count(peer)) return KILN_ERR_NOT_FOUND;
    }
    const bool ok = knet::login_failure(g_gs.load(), peer, static_cast<uint8_t>(reason > 5 ? 5 : reason));
    if (ok) {  // refused: its held Gameplay Ready is never processed. A failed kick stays held until the time limit.
        std::lock_guard<std::mutex> lk(g_mu);
        g_held.erase(peer);
        g_let_in.erase(peer);
    }
    logf(ok ? KILN_LOG_INFO : KILN_LOG_ERROR, "net: kick peer 0x%x reason %u: %s", peer, reason, ok ? "login failure sent" : "faulted");
    return ok ? KILN_OK : KILN_ERR_INTERNAL;
}

KilnResult KILN_CALL t_peer_of_entity(KilnEntity entity, KilnPeer* out) {
    if (!entity || !out) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> lk(g_mu);
    for (auto& [id, p] : g_peers)
        if (p.has_player && p.rec.entity == entity) {
            *out = id;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_gate_set(uint32_t mode) {
    if (mode != KILN_NET_GATE_OFF && mode != KILN_NET_GATE_HOLD) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_server_role) return KILN_ERR_UNAVAILABLE;
    // A lab transport simulates peers without the game's hook, so it can use the gate on any build.
    if (!g_can_hold && !knet::lab::is_open()) return KILN_ERR_UNAVAILABLE;
    g_gate_mode.store(mode, std::memory_order_relaxed);
    return KILN_OK;
}

KilnResult KILN_CALL t_gate_info(KilnNetGateInfo* out) {
    if (!out || out->size < sizeof(KilnNetGateInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof(*out));
    out->size = sizeof(KilnNetGateInfo);
    const KilnAssetsApi* a = assets_api();
    out->caps = ((g_can_hold || knet::lab::is_open()) ? KILN_NET_GATE_CAN_HOLD : 0u) | ((g_can_withhold && a) ? KILN_NET_GATE_CAN_WITHHOLD : 0u);
    out->mode = g_gate_mode.load(std::memory_order_relaxed);
    out->kiln_templates = a ? static_cast<uint32_t>(a->resource_new_count(kTemplateType)) : 0;
    out->withheld_slots = g_withheld_slots.load(std::memory_order_relaxed);
    out->withheld_entities = g_withheld_total.load(std::memory_order_relaxed);
    out->dropped_creates = g_dropped_total.load(std::memory_order_relaxed);
    if (g_create_guard) out->caps |= KILN_NET_GATE_CAN_GUARD;
    for (uint32_t slot = 0; slot < 32; ++slot) {
        std::lock_guard<std::mutex> k(g_kept[slot].mu);
        if (g_kept[slot].newer) out->newer_slots |= 1u << slot;
    }
    std::lock_guard<std::mutex> lk(g_mu);
    out->held = static_cast<uint32_t>(g_held.size());
    out->missing_templates = static_cast<uint32_t>(g_missing.size());
    return KILN_OK;
}

KilnResult KILN_CALL t_gate_release(KilnPeer peer) {
    std::lock_guard<std::mutex> lk(g_mu);
    if (!g_held.count(peer)) return KILN_ERR_NOT_FOUND;
    g_let_in.insert(peer);
    return KILN_OK;
}

int32_t KILN_CALL t_entity_withheld(KilnEntity entity) {
    const KilnAssetsApi* a = assets_api();
    const uintptr_t world = g_api->server_world();
    if (!a || !world || g_world_storage < 0) return -1;
    bool read = false;
    const bool kiln_only = entity_kiln_only(world + static_cast<uintptr_t>(g_world_storage), entity, a, &read);
    return read ? (kiln_only ? 1 : 0) : -1;
}

int32_t KILN_CALL t_template_kiln_only(const uint8_t guid[16]) {
    const KilnAssetsApi* a = assets_api();
    if (!a || !guid) return -1;
    KilnAssetKey key{};
    std::memcpy(key.guid, guid, sizeof(key.guid));
    key.type = kTemplateType;
    return a->resource_is_new(&key);
}

KilnResult KILN_CALL t_withhold_set(KilnPeer peer, uint32_t on) {
    // Clearing takes the Kiln-only templates there are now: Kiln.Network clears a Kiln client when its content state covers
    // them all. Each clear takes them again, and sends what was kept for templates that came after the one before.
    uint64_t count = 0;
    std::shared_ptr<const KnownTemplates> known;
    if (!on) known = kiln_templates_now(assets_api(), &count);
    std::lock_guard<std::mutex> lk(g_mu);
    if (on) {
        g_cleared.erase(peer);
    } else {
        g_cleared.insert(peer);
        if (auto it = g_peers.find(peer); it != g_peers.end()) {
            Peer& p = it->second;
            size_t kept = 0;
            uint64_t newer = 0;
            if (p.has_player && p.rec.slot < 32) {
                std::lock_guard<std::mutex> k(g_kept[p.rec.slot].mu);
                kept = g_kept[p.rec.slot].ids.size();
                newer = g_kept[p.rec.slot].newer;
            }
            if (p.known && count > p.known_count)
                logf(KILN_LOG_INFO, "net: peer 0x%x cleared again: %llu Kiln-only template(s), %llu added since its last clear; %zu kept-back entit%s out (%llu kept for those)",
                     peer, static_cast<unsigned long long>(count), static_cast<unsigned long long>(count - p.known_count), kept, kept == 1 ? "y goes" : "ies go",
                     static_cast<unsigned long long>(newer));
            p.known = known;
            p.known_count = count;
        }
    }
    recompute_withheld_locked();
    return KILN_OK;
}

using CreateReaderFn = uint64_t*(__fastcall*)(uint64_t* out, uintptr_t world, const void* message, uint32_t id, uint8_t prediction);
using RelevanceStepFn = void(__fastcall*)(uintptr_t player, uintptr_t world);

int seh_note_fault(EXCEPTION_POINTERS* e, uintptr_t* where) {
    *where = reinterpret_cast<uintptr_t>(e->ExceptionRecord->ExceptionAddress);
    return EXCEPTION_EXECUTE_HANDLER;
}

// 0 when the reader returned; else the address of its fault.
uintptr_t seh_create_reader(uintptr_t fn, uint64_t* result, uintptr_t world, const void* message) {
    uintptr_t where = 0;
    __try {
        reinterpret_cast<CreateReaderFn>(fn)(result, world, message, 1, 0);
        return 0;
    } __except (seh_note_fault(GetExceptionInformation(), &where)) {
        return where ? where : 1;
    }
}

// A player's replication object in `world`, as the game finds it (0x62b3ec, 0x6690e7): 0x125850 bytes per slot, at +8.
uintptr_t player_object(uintptr_t world, uint32_t slot) {
    uintptr_t base = 0;
    if (!knet::read_mem(world + 0x15fd2a8, &base, sizeof base) || !base) return 0;
    return base + slot * 0x125850ull + 8;
}

bool seh_relevance_step(uintptr_t fn, uintptr_t player, uintptr_t world, uint32_t entity) {
    __try {
        std::memset(reinterpret_cast<void*>(player + 0x8dba0), 0, 0x14);  // the step's list of newly relevant entities
        *reinterpret_cast<uint32_t*>(player + 0x8dba0) = entity;
        *reinterpret_cast<uint16_t*>(player + 0x92ba0) = 1;
        reinterpret_cast<RelevanceStepFn>(fn)(player, world);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnResult KILN_CALL t_lab_selftest(uint32_t op, uint64_t a, uint64_t b, uint64_t* out) {
    if (!knet::lab::is_open() || !on_server_thread() || !out) return KILN_ERR_UNAVAILABLE;
    const uintptr_t world = g_api->server_world();
    if (!world) return KILN_ERR_UNAVAILABLE;
    *out = 0;
    switch (op) {
        case KILN_NET_SELFTEST_CREATE: {
            if (!g_create_guard || !g_create_reader) return KILN_ERR_UNAVAILABLE;
            // The message as the writer lays it out (0x62c886): tick, entity, parent, GUID, u8, u16 data length.
            uint8_t m[0x40] = {};
            const uint32_t entity = static_cast<uint32_t>(b), parent = static_cast<uint32_t>(b >> 32);
            std::memcpy(m + 4, &entity, 4);
            std::memcpy(m + 8, &parent, 4);
            std::memcpy(m + 0xc, reinterpret_cast<const void*>(a), 16);
            const void* slice[2] = {m, reinterpret_cast<const void*>(sizeof(m))};
            uint64_t result[2] = {0xAAAA, 0xAAAA};
            if (const uintptr_t where = seh_create_reader(g_create_reader, result, world, slice)) {
                logf(KILN_LOG_WARN, "net: lab_selftest: the create reader faulted at rva 0x%llx", static_cast<unsigned long long>(where - g_base));
                return KILN_ERR_INTERNAL;
            }
            *out = result[0];
            return KILN_OK;
        }
        case KILN_NET_SELFTEST_SLOT: {
            const uintptr_t player = player_object(world, static_cast<uint32_t>(a));
            uint8_t first = 0xFF;
            if (!player || !knet::read_mem(player, &first, 1)) return KILN_ERR_NOT_FOUND;
            *out = first;
            return KILN_OK;
        }
        case KILN_NET_SELFTEST_MASK: {
            std::lock_guard<std::mutex> lk(g_mu);
            g_test_slots = static_cast<uint32_t>(a);
            recompute_withheld_locked();
            return KILN_OK;
        }
        case KILN_NET_SELFTEST_QUEUE: {
            if (!g_enqueue || !g_relevance_step) return KILN_ERR_UNAVAILABLE;
            const uintptr_t player = player_object(world, static_cast<uint32_t>(a));
            if (!player) return KILN_ERR_NOT_FOUND;
            g_step.fetch_add(1, std::memory_order_acq_rel);  // on the tick thread, not in a step's jobs: read kiln.assets' count anew
            // A free slot's byte is 0xFF: mark it taken, as the game does when a player joins. The lab never fills this slot.
            *reinterpret_cast<uint8_t*>(player) = static_cast<uint8_t>(a);
            if (!seh_relevance_step(g_relevance_step, player, world, static_cast<uint32_t>(b))) return KILN_ERR_INTERNAL;
            knet::read_mem(player + 0x8f8, out, sizeof(*out));  // the heap's size (0x6692e0)
            return KILN_OK;
        }
        case KILN_NET_SELFTEST_HEAP: {
            const uintptr_t player = player_object(world, static_cast<uint32_t>(a));
            if (!player || !knet::read_mem(player + 0x8f8, out, sizeof(*out))) return KILN_ERR_NOT_FOUND;
            return KILN_OK;
        }
        case KILN_NET_SELFTEST_BIND: {
            const KilnPeer peer = static_cast<KilnPeer>(a);
            const uint32_t slot = static_cast<uint32_t>(b);
            if (!(peer & KILN_NET_PEER_LAB) || (slot >= 32 && slot != 0xFFFFFFFFu)) return KILN_ERR_INVALID_ARGUMENT;
            std::lock_guard<std::mutex> lk(g_mu);
            auto it = g_peers.find(peer);
            if (it == g_peers.end()) return KILN_ERR_NOT_FOUND;
            Peer& p = it->second;
            if (p.bound) forget_slot(p.rec.slot);
            p.bound = slot < 32;
            p.has_player = p.bound;
            p.rec.slot = slot;
            if (p.bound) forget_slot(slot);
            recompute_withheld_locked();
            logf(KILN_LOG_INFO, "net: lab peer 0x%x %s", peer, p.bound ? ("stands for the player of slot " + std::to_string(slot)).c_str() : "no longer stands for a player");
            return KILN_OK;
        }
        case KILN_NET_SELFTEST_BIND_MACHINE: {
            const KilnPeer peer = static_cast<KilnPeer>(a);
            const uint32_t machine = static_cast<uint32_t>(b);
            const bool valid = machine < static_cast<uint32_t>(L.machine_count > 0 ? L.machine_count : 17);
            if ((peer && !(peer & KILN_NET_PEER_LAB)) || (!valid && !(peer && machine == 0xFFFFFFFFu))) return KILN_ERR_INVALID_ARGUMENT;
            std::lock_guard<std::mutex> lk(g_mu);
            if (peer) {
                auto it = g_peers.find(peer);
                if (it == g_peers.end()) return KILN_ERR_NOT_FOUND;
                for (auto& [id, other] : g_peers)
                    if (valid && other.machine == static_cast<int32_t>(machine)) other.machine = -1;  // one peer per machine
                it->second.machine = valid ? static_cast<int32_t>(machine) : -1;
                logf(KILN_LOG_INFO, "net: lab peer 0x%x %s", peer, valid ? ("stands for the fake player on machine " + std::to_string(machine)).c_str() : "no longer stands for a fake player");
            }
            for (const auto& [id, p] : g_peers)
                if (valid && p.machine == static_cast<int32_t>(machine)) {
                    *out = id;
                    break;
                }
            return KILN_OK;
        }
        default:
            return KILN_ERR_INVALID_ARGUMENT;
    }
}

const KilnNetApi kTable = {{sizeof(KilnNetApi), KILN_NET_VERSION}, &t_status, &t_subscribe, &t_unsubscribe,
                           &t_send, &t_peers, &t_peer_info, &t_kick, &t_peer_of_entity,
                           &t_gate_set, &t_gate_info, &t_gate_release, &t_entity_withheld, &t_template_kiln_only,
                           &t_withhold_set, &t_lab_selftest};

// ---------------------------------------------------------------------------------------------------------------
// Boot

bool sym(const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

uint16_t lab_port_from_config() {
    char env[32];
    const DWORD n = GetEnvironmentVariableA("KILN_NET_LAB_PORT", env, sizeof env);
    if (n > 0 && n < sizeof env) return static_cast<uint16_t>(std::strtoul(env, nullptr, 10));
    try {
        // KILN_PATH_CONFIG: the profile's configDir; an older core answers an error, then <kiln>/config
        std::string dir = api_path(KILN_PATH_CONFIG);
        if (dir.empty()) {
            const std::string kiln = api_path(KILN_PATH_KILN);
            if (kiln.empty()) return 0;
            dir = kiln + "\\config";
        }
        const std::filesystem::path p = std::filesystem::path(std::u8string(dir.begin(), dir.end())) / "network.json";
        std::ifstream f(p);
        if (!f) return 0;
        const auto j = nlohmann::json::parse(f, nullptr, false, true);
        if (j.is_object() && j.contains("labPort") && j["labPort"].is_number_unsigned())
            return static_cast<uint16_t>(j["labPort"].get<unsigned>());
    } catch (...) {
    }
    return 0;
}

bool resolve_session_fns() {
    return sym("net/SessionRead", &F.read) && sym("net/SessionFree", &F.free) && sym("net/SessionAlloc", &F.alloc) &&
           sym("net/SessionCommit", &F.commit) && sym("net/SessionAbort", &F.abort) && cst("net/channel", &L.channel);
}

// The guard for a Kiln client (kiln_net.h). Fails soft: without it a client behaves as before, and says so.
void init_create_guard() {
    uintptr_t call = 0;
    if (feat("net.client_guard") && sym("net/CreateTemplateCall", &call) && cst("net/create.entity", &L.create_entity) &&
        cst("net/create.parent", &L.create_parent)) {
        g_create_resume = call + 0x11;  // after the call and its test, which the pattern fixes: the failure branch
        g_create_guard.install(g_api, g_owner, call, &hk_create_template, "net/CreateTemplateCall");
    }
    if (!g_create_guard) logf(KILN_LOG_WARN, "net: no guard on the game's create path on this build: a template this game lacks crashes it, as without Kiln");
}

// Fails soft, one piece at a time. Kiln clients don't need either piece; Kiln.Network reads the result (gate_info) and
// refuses vanilla clients rather than letting them crash when a piece is missing and something is at stake.
void init_vanilla_gate() {
    uintptr_t login = 0;
    if (feat("net.gate") && sym("net/MachineLogin", &login)) {
        g_machine_login.install(g_api, g_owner, login, reinterpret_cast<void*>(&hk_machine_login), "net/MachineLogin");
        g_can_hold = static_cast<bool>(g_machine_login);
    }
    cst("world.storage", &g_world_storage);
    uintptr_t enqueue = 0;
    if (feat("net.withhold") && sym("net/ReplicationEnqueue", &enqueue) && cst("spawn/storage.entity_map", &L.entity_map) &&
        cst("spawn/storage.entity_map_cap", &L.entity_map_cap) && cst("spawn/storage.entity_records", &L.entity_records) &&
        cst("spawn/record.parent", &L.rec_parent) && cst("spawn/record.template", &L.rec_template) && cst("spawn/record.id", &L.rec_id) &&
        cst("net/player.state_map", &L.player_state_map)) {
        g_enqueue.install(g_api, g_owner, enqueue, reinterpret_cast<void*>(&hk_enqueue), "net/ReplicationEnqueue");
        g_can_withhold = static_cast<bool>(g_enqueue);
    }
    logf(g_can_hold && g_can_withhold ? KILN_LOG_INFO : KILN_LOG_WARN, "net: clients without Kiln: gate %s, withholding %s",
         g_can_hold ? "available" : "NOT available on this build", g_can_withhold ? "available" : "NOT available on this build");
}

bool init_server_role() {
    const char* feature = g_side == 0 ? "net.transport" : "net.host";
    uintptr_t after = 0;
    int64_t reg = 13;
    if (!feat(feature) || !sym("net/SyncReturn", &after) || !cst("net/gs.sockets", &L.gs_sockets) ||
        !cst("net/sockets.machines", &L.sockets_machines) || !cst("net/machine.stride", &L.machine_stride) ||
        !cst("net/machine.sockets", &L.machine_sockets) || !cst("net/gs.machines", &L.gs_machines) ||
        !cst("net/gs.machine_count", &L.machine_count) || !cst("net/after_sync.gs_register", &reg) ||
        (reg != 13 && reg != 15)) {
        logf(KILN_LOG_INFO, "net: feature %s is off on this build: no server role", feature);
        return false;
    }
    g_gs_register = static_cast<int>(reg);
    g_have_players = feat("net.players") && cst("net/gs.records", &L.gs_records) && cst("net/rec.stride", &L.rec_stride) &&
                     cst("net/rec.count", &L.rec_count) && cst("net/rec.state", &L.rec_state) &&
                     cst("net/rec.peer", &L.rec_peer) && cst("net/rec.account", &L.rec_account) &&
                     cst("net/rec.name", &L.rec_name) && cst("net/rec.entity", &L.rec_entity);
    g_can_kick = feat("net.kick") && sym("net/SendLoginResult", &F.login_result);
    if (!g_after_sync.install(g_api, g_owner, after, &hk_after_sync, "net/SyncReturn")) {
        logf(KILN_LOG_ERROR, "net: mid hook at rva 0x%llx failed (%s): no server role",
             static_cast<unsigned long long>(after - g_base), g_after_sync.error());
        return false;
    }
    KilnHandle h = 0;
    if (g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_server_pre, nullptr, &h) != KILN_OK) {
        logf(KILN_LOG_WARN, "net: no server tick: frames cannot be delivered (server role off)");
        g_after_sync = {};
        return false;
    }
    init_vanilla_gate();
    logf(KILN_LOG_INFO, "net: server role on: after-sync hook rva 0x%llx (GameServer in r%d), channel %lld, players %s, kick %s",
         static_cast<unsigned long long>(after - g_base), g_gs_register, static_cast<long long>(L.channel),
         g_have_players ? "on" : "off", g_can_kick ? "on" : "off");
    return true;
}

bool init_client_role() {
    uintptr_t recv = 0;
    if (!feat("net.client") || !sym("net/ClientReceive", &recv) || !cst("net/gc.sockets", &L.gc_sockets) ||
        !cst("net/client.server_sockets", &L.client_server_sockets)) {
        logf(KILN_LOG_INFO, "net: feature net.client is off on this build: no client role");
        return false;
    }
    g_client_recv.install(g_api, g_owner, recv, reinterpret_cast<void*>(&hk_client_receive), "net/ClientReceive");
    if (!g_client_recv) {
        logf(KILN_LOG_ERROR, "net: hooking ClientReceive at rva 0x%llx failed: no client role", static_cast<unsigned long long>(recv - g_base));
        return false;
    }
    uintptr_t ready = 0;
    if (feat("net.client_ready") && sym("net/ClientGameplayReady", &ready)) {
        g_client_ready.install(g_api, g_owner, ready, &hk_client_ready, "net/ClientGameplayReady");
    }
    if (!g_client_ready) logf(KILN_LOG_WARN, "net: no hook on the client's Gameplay Ready on this build: the proof goes out on its timers only");
    init_create_guard();
    bool ticks = false;
    if (g_world_api) {
        KilnHandle h = 0;
        ticks = g_api->subscribe_world_tick(g_owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h) == KILN_OK;
        g_api->subscribe_world(g_owner, &on_world_event, nullptr, &h);
    }
    logf(KILN_LOG_INFO, "net: client role on: ClientReceive rva 0x%llx, delivery on %s", static_cast<unsigned long long>(recv - g_base),
         ticks ? "the client world's Pre (main thread)" : "the receive hook (main thread)");
    return true;
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    info->id = KilnStr{KILN_NET_SERVICE, sizeof(KILN_NET_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    g_world_api = api->size >= offsetof(KilnApi, session) + sizeof(void*);
    if (g_world_api) {
        g_side = api->side();
    } else {
        KilnBuildInfo bi{};
        bi.size = sizeof(bi);
        g_side = api->build_info(&bi) == KILN_OK ? bi.side : 0;
    }
    if (!resolve_session_fns()) {
        logf(KILN_LOG_WARN, "net: the game's session functions are unresolved on this build; kiln.net is off");
        return KILN_ERR_UNAVAILABLE;
    }
    int64_t mode = -1;
    if (!cst("net/channel.mode", &mode) || mode != 3) {
        logf(KILN_LOG_WARN, "net: game channel %lld is not 'reliable + large' (mode %lld) on this build; kiln.net is off",
             static_cast<long long>(L.channel), static_cast<long long>(mode));
        return KILN_ERR_UNAVAILABLE;
    }
    g_server_role = init_server_role();
    if (g_side == 1) g_client_role = init_client_role();
    if (g_server_role) {
        g_lab_port = lab_port_from_config();
        if (g_lab_port) {
            std::string err;
            if (knet::lab::open(g_lab_port, &err)) {
                logf(KILN_LOG_WARN, "net: LAB transport open on 127.0.0.1:%u (tests only; remove labPort on real servers)", g_lab_port);
                // The server exe has the same create reader, unused: lab_selftest runs the guard on the real function.
                if (g_side == 0) {
                    sym("net/CreateEntityReader", &g_create_reader);
                    sym("net/RelevanceStep", &g_relevance_step);
                    init_create_guard();
                }
            } else {
                logf(KILN_LOG_ERROR, "net: lab transport: %s", err.c_str());
            }
        }
    }
    if (!g_server_role && !g_client_role) return KILN_ERR_UNAVAILABLE;
    return api->register_service(owner, KilnStr{KILN_NET_SERVICE, sizeof(KILN_NET_SERVICE) - 1}, &kTable);
}
