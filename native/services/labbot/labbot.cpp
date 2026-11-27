// SPDX-License-Identifier: MIT
// kiln.labbot: bot players on dedicated LAB servers (native/core/abi/services/kiln_labbot.h). The portable part (the
// wire bytes, the published entries, join and leave step by step) is bot.cpp; this file runs it on the game.
//
//  - SessionPublish (labbot/SessionPublish, the session thread): the session makes a filled back buffer current here.
//    Before the game's own code runs, each live bot's machine and player entries go into that buffer (put_entries,
//    growing the machine's player list through the game's own ArrayReserve when it has no room), and each left bot's
//    entries come out of it (clear_entries). Every reader of the session's state (SyncSockets, the GameServer's player
//    update, AddPlayer's machine and player info) then sees the bot as a connected remote machine with one player.
//  - ServerReceive (net/ServerReceive, the GameServer thread, which is the tick thread on a dedicated server): after
//    the game's own receive step, each bot takes one step (Bot::step) on this GameServer: it calls what a client's
//    messages reach there (AddPlayer for the Login, Save2Receive for the character upload, save acks and the final
//    request, MachineLogin for SystemsReady and GameplayReady). MachineLogin is called at the game's address, so
//    kiln.net's gate sees the bot's Gameplay Ready as any client's.
//  - Lab servers only: kiln.json's policy "any" and kiln.net's lab transport (labPort), as kiln.repl's loopback harness.
//    No Steam account, ticket or password is used: the session never authenticated the bot because it never saw it.
// Engine facts and addresses: docs/re/labbot.md.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

#include "bot.h"
#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "nlohmann/json.hpp"
#include "services/kiln_caps.h"
#include "services/kiln_hooks.h"
#include "services/kiln_labbot.h"
#include "services/kiln_net.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace {

constexpr uint8_t kKickBanRight = 1;  // kiln_chat.h KILN_CHAT_PERM_KICK_BAN: the user group's canKickBan

using labbot::Bot;
using labbot::Phase;
using labbot::SnapEntry;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnCapsApi* g_caps = nullptr;

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof(buf) - 1)))});
}

KilnStr kstr(const char* s) { return KilnStr{s, std::strlen(s)}; }

// ------------------------------------------------------------------------------------------- what it resolved ------
struct Functions {
    uintptr_t publish = 0, server_receive = 0, add_player = 0, save2_receive = 0, machine_login = 0, request_save = 0,
              array_reserve = 0;
} F;

struct Layout {
    int64_t session_current = -1, session_buffers = -1, session_stride = -1;
    int64_t gs_sockets = -1, sockets_machines = -1, machine_stride = -1;
    int64_t gs_session = -1, gs_machines = -1, gs_records = -1, rec_stride = -1, rec_state = -1, rec_peer = -1,
            rec_entity = -1, rec_perms = -1;
    int64_t gs_save2 = -1, save2_slots = -1, save_handle = -1, save_state = -1, save_status = -1, save_has_entity = -1,
            save_version = -1, save_upload = -1;
    int64_t gs_groups = -1, gs_groups_count = -1, group_stride = -1;
} Ly;
labbot::SnapLayout g_snap;
labbot::LoginLayout g_login;

bool g_resolved = false;
bool g_policy_any = false;
uint32_t g_lab_port = 0;
bool g_allowed = false;
std::string g_why_off = "not started";

kiln::RawHook g_publish_hook;
kiln::RawHook g_receive_hook;

// ------------------------------------------------------------------------------------ guarded game access ----------
bool valid_ptr(uintptr_t p) { return p >= 0x10000 && p < 0x00007FFFFFFFFFFFull; }

bool read_mem(uintptr_t address, void* out, size_t n) {
    if (!valid_ptr(address) || address > UINTPTR_MAX - n) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(address), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
template <class T>
T rd(uintptr_t a, T fallback = T{}) {
    T v{};
    return read_mem(a, &v, sizeof v) ? v : fallback;
}
bool write_u8(uintptr_t address, uint8_t v) {
    if (!valid_ptr(address)) return false;
    __try {
        *reinterpret_cast<volatile uint8_t*>(address) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

using AddPlayerFn = void(__fastcall*)(uintptr_t gs, uint32_t machine, const uint8_t* login);
using Save2ReceiveFn = uint8_t(__fastcall*)(uint8_t* final_requested, uintptr_t save2, uint32_t player, const void* view);
using MachineLoginFn = void(__fastcall*)(uintptr_t gs, uint32_t machine, const uint8_t* message);
using RequestSaveFn = void(__fastcall*)(uintptr_t gs);
using ArrayReserveFn = uint8_t(__fastcall*)(uintptr_t array, uint64_t capacity, uint8_t flag);

bool call_add_player(uintptr_t gs, uint32_t machine, const uint8_t* login) {
    __try {
        reinterpret_cast<AddPlayerFn>(F.add_player)(gs, machine, login);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
struct View {
    const uint8_t* data;
    uint64_t size;
};
bool call_save2(uintptr_t save2, uint32_t player, const View* v, uint8_t* fin, uint8_t* ret) {
    __try {
        *ret = reinterpret_cast<Save2ReceiveFn>(F.save2_receive)(fin, save2, player, v);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_machine_login(uintptr_t gs, uint32_t machine, const uint8_t* message) {
    __try {
        reinterpret_cast<MachineLoginFn>(F.machine_login)(gs, machine, message);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_request_save(uintptr_t gs) {
    __try {
        reinterpret_cast<RequestSaveFn>(F.request_save)(gs);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_reserve(uintptr_t array, uint64_t capacity) {
    __try {
        return reinterpret_cast<ArrayReserveFn>(F.array_reserve)(array, capacity, 0) != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
// put_entries / clear_entries on a game buffer, under a guard. -1 = fault.
int guarded_put(uint8_t* buf, const SnapEntry& e) {
    __try {
        return static_cast<int>(labbot::put_entries(buf, g_snap, e));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int guarded_clear(uint8_t* buf, const SnapEntry& e) {
    __try {
        return labbot::clear_entries(buf, g_snap, e) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int guarded_has(const uint8_t* buf, const SnapEntry& e) {
    __try {
        return labbot::has_entries(buf, g_snap, e) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

uintptr_t session_of(uintptr_t gs) { return gs ? rd<uintptr_t>(gs + static_cast<uintptr_t>(Ly.gs_session)) : 0; }
// The session's current published buffer (a reader's view; the session thread may publish the next one meanwhile).
uintptr_t current_buffer(uintptr_t session) {
    if (!valid_ptr(session)) return 0;
    const uint32_t i = rd<uint32_t>(session + static_cast<uintptr_t>(Ly.session_current), 0xffffffffu);
    if (i > 1) return 0;
    return session + static_cast<uintptr_t>(Ly.session_buffers) + static_cast<uintptr_t>(Ly.session_stride) * i;
}
uintptr_t record_addr(uintptr_t gs, uint32_t slot) {
    return gs + static_cast<uintptr_t>(Ly.gs_records) + static_cast<uintptr_t>(Ly.rec_stride) * slot;
}
uint32_t machine_slot_peer(uintptr_t gs, uint32_t index) {
    return rd<uint32_t>(gs + static_cast<uintptr_t>(Ly.gs_machines) + static_cast<uintptr_t>(index) * 8);
}

// ------------------------------------------------------------------------------- the published entries --------------
// Read by the session thread in SessionPublish; written on the GameServer thread. One small lock: the hook holds it for
// a few memory writes (labbot::Publisher: the left bots' clears, then the live bots' entries).
std::mutex g_pub_mu;
labbot::Publisher g_pub;
std::atomic<bool> g_pub_active{false};  // !g_pub.idle(): the hook touches the buffer
std::atomic<uint64_t> g_publishes{0};    // publishes the bots were in
std::atomic<uint64_t> g_publish_calls{0};

// The back buffer of one publish, each write under a fault guard.
struct BufferOps final : labbot::Publisher::Ops {
    uint8_t* buf = nullptr;
    labbot::Put put(const SnapEntry& e) override {
        const int r = guarded_put(buf, e);
        return r < 0 ? labbot::Put::Bad : static_cast<labbot::Put>(r);
    }
    bool grow(const SnapEntry& e) override {
        const uintptr_t arr = reinterpret_cast<uintptr_t>(buf) + g_snap.machines +
                              static_cast<uintptr_t>(g_snap.machine_stride) * e.machine_index + g_snap.m_players;
        return call_reserve(arr, 1);
    }
    bool clear(const SnapEntry& e) override { return guarded_clear(buf, e) > 0; }
};

void __fastcall hk_publish(uintptr_t session, uint8_t* buffer) {
    g_publish_calls.fetch_add(1, std::memory_order_relaxed);
    if (buffer && g_pub_active.load(std::memory_order_acquire)) {
        std::lock_guard<std::mutex> lk(g_pub_mu);
        BufferOps ops;
        ops.buf = buffer;
        if (g_pub.apply(ops)) g_publishes.fetch_add(1, std::memory_order_relaxed);
        g_pub_active.store(!g_pub.idle(), std::memory_order_release);
    }
    g_publish_hook.call<void>(session, buffer);
}

// GameServer thread: list or unlist a bot's entries from the next publish on.
void pub_set(uint32_t index, const SnapEntry& e, bool listed) {
    std::lock_guard<std::mutex> lk(g_pub_mu);
    if (listed) g_pub.list(index, e);
    else g_pub.unlist(index);
    g_pub_active.store(!g_pub.idle(), std::memory_order_release);
}

// ------------------------------------------------------------------------------------- the bot's engine --------------
struct GameEngine final : labbot::Engine {
    uintptr_t gs = 0;
    std::string seen;  // what in_snapshot found (diagnostics)

    // What a client's Login finds when it arrives: the session lists its machine and player, and SyncSockets (the first
    // step of ServerReceive) made the machine's sockets. The machine slot and the player record are AddPlayer's own
    // first step (0x7abbe0: admit the machine, make the record with the login's name and character).
    bool in_snapshot(const SnapEntry& e) override {
        const uintptr_t cur = current_buffer(session_of(gs));
        const int pub = cur ? guarded_has(reinterpret_cast<const uint8_t*>(cur), e) : -1;
        const uintptr_t set = rd<uintptr_t>(gs + static_cast<uintptr_t>(Ly.gs_sockets));
        const uintptr_t table = valid_ptr(set) ? rd<uintptr_t>(set + static_cast<uintptr_t>(Ly.sockets_machines)) : 0;
        const bool sockets = valid_ptr(table) &&
                             rd<uint32_t>(table + static_cast<uintptr_t>(Ly.machine_stride) * (e.machine_index & 0x7f)) == e.machine;
        seen = std::string("published ") + (pub > 0 ? "yes" : pub == 0 ? "no" : "unreadable") + ", machine sockets " +
               (sockets ? "yes" : "no");
        return pub > 0 && sockets;
    }
    bool add_player(uint32_t machine, const uint8_t* login) override { return call_add_player(gs, machine, login); }
    labbot::RecordView record(uint32_t slot) override {
        labbot::RecordView v;
        uint8_t r[0x148];
        if (Ly.rec_stride > static_cast<int64_t>(sizeof r) || !read_mem(record_addr(gs, slot), r, static_cast<size_t>(Ly.rec_stride)))
            return v;
        v.readable = true;
        std::memcpy(&v.handle, r, 4);
        std::memcpy(&v.peer, r + Ly.rec_peer, 4);
        std::memcpy(&v.entity, r + Ly.rec_entity, 4);
        v.state = r[Ly.rec_state];
        v.permissions = r[Ly.rec_perms];
        return v;
    }
    uintptr_t save2() const { return rd<uintptr_t>(gs + static_cast<uintptr_t>(Ly.gs_save2)); }
    labbot::SaveView save(uint32_t slot) override {
        labbot::SaveView v;
        const uintptr_t s2 = save2();
        if (!valid_ptr(s2)) return v;
        const uintptr_t p = rd<uintptr_t>(s2 + static_cast<uintptr_t>(Ly.save2_slots) + static_cast<uintptr_t>(slot) * 8);
        if (!valid_ptr(p)) return v;
        v.readable = read_mem(p + Ly.save_handle, &v.handle, 4) && read_mem(p + Ly.save_version, &v.version, 4) &&
                     read_mem(p + Ly.save_state, &v.state, 1) && read_mem(p + Ly.save_status, &v.status, 1) &&
                     read_mem(p + Ly.save_has_entity, &v.has_entity, 1);
        if (Ly.save_upload >= 0) v.upload_capacity = rd<uint64_t>(p + static_cast<uintptr_t>(Ly.save_upload) + 0x10);  // {data, size, capacity}
        return v;
    }
    bool save_receive(uint32_t player, const std::vector<uint8_t>& message, bool* final_requested) override {
        *final_requested = false;
        const uintptr_t s2 = save2();
        if (!valid_ptr(s2) || message.empty()) return false;
        const View v{message.data(), message.size()};
        uint8_t fin = 0, ret = 0;
        if (!call_save2(s2, player, &v, &fin, &ret)) return false;
        *final_requested = fin != 0;
        return true;
    }
    bool machine_login(uint32_t machine, uint8_t type) override {
        std::vector<uint8_t> m(g_login.size, 0);
        m[g_login.type] = type;
        return call_machine_login(gs, machine, m.data());
    }
    bool end_game(uint32_t slot) override {
        // what ServerReceive does after a final request of a record in game: state 4 (its entity goes, the final save
        // follows), and a world save
        const uintptr_t st = record_addr(gs, slot) + static_cast<uintptr_t>(Ly.rec_state);
        if (rd<uint8_t>(st) != labbot::kRecInGame) return true;
        return write_u8(st, labbot::kRecRemovingEntity) && call_request_save(gs);
    }
};

// ------------------------------------------------------------------------------------------------ the bots ----------
struct Slot {
    bool used = false;
    uint32_t id = 0;
    KilnOwner owner = 0;
    uint32_t flags = 0;
    Bot bot;
    Phase logged = Phase::Free;
    uint64_t last_note = 0;
    uint64_t joined_step = 0, in_game_step = 0;  // server steps (KilnLabBotInfo); the bot itself counts milliseconds
    labbot::ClientInput input;                   // what its client says in its input (input()); written while in game
    bool input_written = false, input_failed = false;  // the first write's outcome was logged
};
std::recursive_mutex g_mu;  // the bots; GameServer thread steps them, any thread reads info/list
Slot g_slots[KILN_LABBOT_MAX];
labbot::SpentMachines g_spent;  // every machine index a bot held in this server session: never given again (bot.h)
uint32_t g_next_id = 1;
std::atomic<uintptr_t> g_gs{0};
std::atomic<uint64_t> g_step{0};
std::atomic<uint64_t> g_receive_ms{0};

bool live(const Slot& s) {
    return s.used && s.bot.phase != Phase::Free && s.bot.phase != Phase::Left && s.bot.phase != Phase::Failed;
}

void fill_info(const Slot& s, KilnLabBotInfo* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->id = s.id;
    out->phase = static_cast<uint32_t>(s.bot.phase);
    out->slot = s.bot.entry.slot;
    out->machine = s.bot.entry.machine;
    out->player = s.bot.entry.player;
    out->entity = s.bot.phase == Phase::InGame || s.bot.phase == Phase::Leaving ? s.bot.entity : 0;
    out->record_state = s.bot.record_state;
    out->permissions = s.bot.permissions;
    out->group = s.bot.entry.group;
    out->saves_acked = s.bot.saves_acked;
    out->account = s.bot.account;
    std::memcpy(out->character_id, s.bot.character, 16);
    out->joined_step = s.joined_step;
    out->in_game_step = s.in_game_step;
    std::snprintf(out->name, sizeof out->name, "%s", s.bot.name.c_str());
    std::snprintf(out->reason, sizeof out->reason, "%s", s.bot.reason.c_str());
}

// ------------------------------------------------------------------------------------- the bots' client input --------
// PlayerInput's client parts (bot.h InputLayout), from the game's reflection at on_ready; kiln.world gives the bot's
// component. A bot's PlayerInput is written by nothing else (no input message arrives for its slot).
labbot::InputLayout g_input;
bool g_input_ok = false;
std::string g_input_why = "not resolved yet";
const KilnWorldApi* g_world = nullptr;
uint32_t g_input_hash = 0;  // component hash of keen::ecs::PlayerInput

struct TypesFields final : labbot::Fields {
    const KilnTypesApi* t = nullptr;
    const KilnType* find(std::string_view name) const { return t->type_by_name(KilnStr{name.data(), name.size()}); }
    bool info(const KilnType* ty, KilnTypeInfo* out) const {
        std::memset(out, 0, sizeof *out);
        out->size = sizeof *out;
        return ty && t->type_info(ty, out) == KILN_OK;
    }
    bool field(std::string_view type, std::string_view name, int64_t* offset, std::string* field_type, uint32_t* field_size) override {
        const KilnType* ty = find(type);
        KilnTypeInfo ti;
        if (!info(ty, &ti)) return false;
        for (uint32_t k = 0; k < ti.field_count; ++k) {
            KilnTypeField f{};
            if (t->type_field(ty, k, &f) != KILN_OK || std::string_view(f.name.ptr ? f.name.ptr : "", f.name.ptr ? f.name.len : 0) != name)
                continue;
            KilnTypeInfo fi;
            if (!info(f.type, &fi)) return false;
            *offset = static_cast<int64_t>(f.offset);
            field_type->assign(fi.qualified_name.ptr ? fi.qualified_name.ptr : "", fi.qualified_name.ptr ? fi.qualified_name.len : 0);
            *field_size = fi.type_size;
            return true;
        }
        return false;
    }
    uint32_t size_of(std::string_view type) override {
        KilnTypeInfo ti;
        return info(find(type), &ti) ? ti.type_size : 0;
    }
};

void resolve_input() {
    const auto* types = static_cast<const KilnTypesApi*>(g_api->service(kstr(KILN_TYPES_SERVICE), 1));
    g_world = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    if (!types || types->header.size < offsetof(KilnTypesApi, type_field) + sizeof(void*)) {
        g_input_why = "kiln.types is not here";
    } else if (!g_world || g_world->header.size < offsetof(KilnWorldApi, component) + sizeof(void*)) {
        g_input_why = "kiln.world is not here";
    } else {
        TypesFields f;
        f.t = types;
        std::string why;
        if (!labbot::resolve_input(f, &g_input, &why)) {
            g_input_why = "PlayerInput's client parts did not resolve: " + why;
        } else {
            g_input_hash = g_world->component_hash(kstr("PlayerInput"));
            g_input_ok = g_input_hash != 0;
            g_input_why = g_input_ok ? "" : "no component hash for PlayerInput";
        }
    }
    if (g_input_ok)
        logf(KILN_LOG_INFO, "labbot: bots' client input: PlayerInput %u bytes, camera at +0x%llx, readiness at +0x%llx", g_input.size,
             static_cast<unsigned long long>(g_input.camera_position), static_cast<unsigned long long>(g_input.ready));
    else
        logf(KILN_LOG_WARN, "labbot: bots' client input is off: %s", g_input_why.c_str());
}

int guarded_write_input(uint8_t* pi, uint32_t size, const labbot::ClientInput& in) {
    __try {
        return labbot::write_input(pi, size, g_input, in) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

// After the GameServer's receive, where a client's input lands: the parts the bot's client says.
void send_input(Slot& s) {
    if (!g_input_ok || !g_world || !s.bot.entity) return;
    uint32_t size = 0;
    auto* pi = static_cast<uint8_t*>(const_cast<void*>(g_world->component(s.bot.entity, g_input_hash, &size)));
    const int r = pi ? guarded_write_input(pi, size, s.input) : 0;
    if (r > 0 && !s.input_written) {
        s.input_written = true;
        logf(KILN_LOG_INFO, "labbot: bot %u '%s' sends its client input (camera %s, ready %d)", s.id, s.bot.name.c_str(),
             s.input.camera ? "set" : "not set", s.input.ready);
    } else if (r <= 0 && !s.input_failed) {
        s.input_failed = true;
        logf(KILN_LOG_WARN, "labbot: bot %u '%s': its input could not be written (%s)", s.id, s.bot.name.c_str(),
             !pi ? "its entity has no PlayerInput" : r < 0 ? "a fault" : "the component is smaller than the layout");
    }
}

void step_bots(uintptr_t gs) {
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    const uint64_t now = g_step.fetch_add(1, std::memory_order_relaxed) + 1;
    const uint64_t ms = GetTickCount64();
    GameEngine eng;
    eng.gs = gs;
    for (uint32_t i = 0; i < KILN_LABBOT_MAX; ++i) {
        Slot& s = g_slots[i];
        if (!s.used) continue;
        const Phase before = s.bot.phase;
        if (live(s)) s.bot.step(eng, ms, g_login);
        const Phase after = s.bot.phase;
        if (!s.joined_step) s.joined_step = now;
        if (after == Phase::InGame && !s.in_game_step) s.in_game_step = now;
        pub_set(i, s.bot.entry, s.bot.injected());
        if (after == Phase::InGame && s.input.any()) send_input(s);
        if (after == Phase::Loading && s.logged == Phase::LoggingIn) {
            const labbot::SaveView sv = eng.save(s.bot.entry.slot);
            logf(KILN_LOG_INFO, "labbot: bot %u '%s': the server took its character upload (its upload buffer holds %llu bytes)", s.id,
                 s.bot.name.c_str(), static_cast<unsigned long long>(sv.upload_capacity));
        }
        if (after != s.logged) {
            const int level = after == Phase::Failed ? KILN_LOG_ERROR : KILN_LOG_INFO;
            logf(level, "labbot: bot %u '%s' %s -> %s%s%s (slot %u, machine 0x%x, player 0x%x, entity %u, record state %u)", s.id,
                 s.bot.name.c_str(), labbot::phase_name(s.logged), labbot::phase_name(after), s.bot.reason.empty() ? "" : ": ",
                 s.bot.reason.c_str(), s.bot.entry.slot, s.bot.entry.machine, s.bot.entry.player, s.bot.entity, s.bot.record_state);
            s.logged = after;
        }
        // while it waits to be seen, say what is missing every ~5 s
        if (after == Phase::Connecting && before == Phase::Connecting && ms - s.last_note >= 5000) {
            s.last_note = ms;
            logf(KILN_LOG_INFO, "labbot: bot %u '%s' waits to be seen: %s", s.id, s.bot.name.c_str(), eng.seen.c_str());
        }
    }
}

uint64_t __fastcall hk_receive(uintptr_t gs) {
    const uint64_t r = g_receive_hook.call<uint64_t>(gs);
    if (g_allowed && gs) {
        g_gs.store(gs, std::memory_order_relaxed);
        g_receive_ms.store(GetTickCount64(), std::memory_order_relaxed);
        step_bots(gs);
    }
    return r;
}

// The user group a join asks for: the id the GameServer looks a player's rights up by (userGroups of
// enshrouded_server.json, in the GameServer: {u32 id; u8 rights} per entry).
int bits_of(uint8_t v) {
    int n = 0;
    for (; v; v &= static_cast<uint8_t>(v - 1)) ++n;
    return n;
}
bool pick_group(uintptr_t gs, uint32_t how, uint32_t index, uint32_t* out, uint32_t* count_out) {
    const uint64_t n = rd<uint64_t>(gs + static_cast<uintptr_t>(Ly.gs_groups_count));
    const uintptr_t arr = rd<uintptr_t>(gs + static_cast<uintptr_t>(Ly.gs_groups));
    if (count_out) *count_out = static_cast<uint32_t>(std::min<uint64_t>(n, 64));
    if (!n || n > 64 || !valid_ptr(arr)) return false;
    int best = -1, best_key = 0;
    for (uint32_t i = 0; i < n; ++i) {
        const uint8_t rights = rd<uint8_t>(arr + static_cast<uintptr_t>(Ly.group_stride) * i + 4);
        int key = 0;  // higher wins
        if (how == KILN_LABBOT_GROUP_INDEX) key = i == index ? 1 : 0;
        else if (how == KILN_LABBOT_GROUP_ADMIN) key = (rights & kKickBanRight) * 16 + bits_of(rights) + 1;
        else key = 16 - bits_of(rights);
        if (key > best_key) {
            best = static_cast<int>(i);
            best_key = key;
        }
    }
    if (best < 0) return false;
    *out = rd<uint32_t>(arr + static_cast<uintptr_t>(Ly.group_stride) * best);
    return true;
}

bool slot_taken(uint32_t slot, uint32_t machine_index) {
    for (const Slot& s : g_slots)
        if (live(s) && (s.bot.entry.slot == slot || s.bot.entry.machine_index == machine_index)) return true;
    return false;
}

// ------------------------------------------------------------------------------------------------ the table ---------
bool world_running() {
    const uintptr_t gs = g_gs.load(std::memory_order_relaxed);
    return gs && GetTickCount64() - g_receive_ms.load(std::memory_order_relaxed) < 5000;
}

KilnResult KILN_CALL t_status(KilnLabBotStatus* out) {
    if (!out || out->size < sizeof(KilnLabBotStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->available = g_allowed && world_running() ? 1 : 0;
    for (const Slot& s : g_slots) out->bots += live(s) ? 1 : 0;
    uint32_t groups = 0, id = 0;
    if (const uintptr_t gs = g_gs.load(std::memory_order_relaxed)) pick_group(gs, KILN_LABBOT_GROUP_LEAST, 0, &id, &groups);
    out->groups = groups;
    out->publishes = g_publishes.load(std::memory_order_relaxed);
    const char* why = !g_allowed ? g_why_off.c_str() : !world_running() ? "no server world runs (no GameServer receive step in the last 5 s)" : "";
    std::snprintf(out->why, sizeof out->why, "%s", why);
    return KILN_OK;
}

KilnResult KILN_CALL t_join(KilnOwner owner, const KilnLabBotJoin* rq, uint32_t* out_id) {
    if (!g_allowed) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!rq || rq->size < sizeof(KilnLabBotJoin) || !out_id) return KILN_ERR_INVALID_ARGUMENT;
    if (rq->flags & ~static_cast<uint32_t>(KILN_LABBOT_JOIN_KILN_CLIENT)) return KILN_ERR_INVALID_ARGUMENT;
    if (rq->group > KILN_LABBOT_GROUP_INDEX) return KILN_ERR_INVALID_ARGUMENT;
    const std::string name(rq->name.ptr ? rq->name.ptr : "", rq->name.ptr ? rq->name.len : 0);
    std::string why;
    if (!labbot::valid_name(name, &why)) {
        logf(KILN_LOG_WARN, "labbot: join refused: %s", why.c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const uintptr_t gs = g_gs.load(std::memory_order_relaxed);
    if (!world_running()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    for (const Slot& s : g_slots)
        if (live(s) && s.bot.name == name) return KILN_ERR_INVALID_ARGUMENT;
    uint8_t character[16];
    bool zero = true;
    for (uint8_t c : rq->character_id) zero = zero && c == 0;
    if (zero) labbot::derive_character(name, character);
    else std::memcpy(character, rq->character_id, 16);
    const uint64_t account = rq->account ? rq->account : labbot::derive_account(name);
    int free_slot = -1;
    for (uint32_t i = 0; i < KILN_LABBOT_MAX && free_slot < 0; ++i)
        if (!live(g_slots[i])) free_slot = static_cast<int>(i);
    if (free_slot < 0) return KILN_ERR_NOT_FOUND;
    uint32_t group = 0;
    if (!pick_group(gs, rq->group, rq->group_index, &group, nullptr)) {
        logf(KILN_LOG_WARN, "labbot: join of '%s' refused: no such user group", name.c_str());
        return KILN_ERR_NOT_FOUND;
    }
    // the highest player slot and machine index nobody uses: real clients get the low ones from the session
    const uintptr_t cur = current_buffer(session_of(gs));
    uint8_t snap[0x2668];
    const bool have_snap = cur && g_snap.size <= sizeof snap && read_mem(cur, snap, g_snap.size);
    std::vector<bool> slot_busy(g_snap.player_count), machine_busy(g_snap.machine_count);
    for (uint32_t s = 0; s < g_snap.player_count; ++s)
        slot_busy[s] = rd<uint32_t>(record_addr(gs, s)) != 0 || (have_snap && labbot::player_in(snap, g_snap, s) != 0) ||
                       slot_taken(s, 0xffffffffu);
    for (uint32_t m = 0; m < g_snap.machine_count; ++m)
        machine_busy[m] = machine_slot_peer(gs, m) != 0 || (have_snap && labbot::machine_in(snap, g_snap, m) != 0) ||
                          slot_taken(0xffffffffu, m);
    // a machine index an earlier bot held keeps what the server sent that bot: the next bot's spawn message there is
    // refused every frame (lab p1; bot.h SpentMachines), so each bot gets one no bot held in this server session
    const int slot = labbot::pick_index(g_snap.player_count, 0, slot_busy, nullptr);
    const int machine = labbot::pick_index(g_snap.machine_count, 1, machine_busy, &g_spent);
    if (slot < 0 || machine < 0) {
        logf(KILN_LOG_WARN, "labbot: join of '%s' refused: %s", name.c_str(),
             slot < 0 ? "no free player slot"
                      : "no free machine index that no bot held in this server session (each bot needs a fresh one: restart the lab)");
        return KILN_ERR_NOT_FOUND;
    }
    g_spent.add(static_cast<uint32_t>(machine));
    Slot& s = g_slots[free_slot];
    s = Slot{};
    s.used = true;
    s.id = g_next_id++;
    if (!g_next_id) g_next_id = 1;
    s.owner = owner;
    s.flags = rq->flags;
    Bot& b = s.bot;
    b.name = name;
    std::memcpy(b.character, character, 16);
    b.account = account;
    b.new_character = true;
    b.entry.slot = static_cast<uint32_t>(slot);
    b.entry.machine_index = static_cast<uint32_t>(machine);
    b.entry.player = labbot::player_handle(static_cast<uint32_t>(slot));
    b.entry.machine = labbot::machine_handle(static_cast<uint32_t>(machine));
    b.entry.group = group;
    b.entry.account = b.account;
    b.phase = Phase::Connecting;
    b.phase_since = 0;
    b.reason = "joining";
    pub_set(static_cast<uint32_t>(free_slot), b.entry, true);
    *out_id = s.id;
    logf(KILN_LOG_INFO, "labbot: bot %u '%s' joins: slot %u, machine %u (0x%x), player 0x%x, user group 0x%x, account %016llx", s.id,
         name.c_str(), b.entry.slot, b.entry.machine_index, b.entry.machine, b.entry.player, group,
         static_cast<unsigned long long>(b.account));
    s.logged = Phase::Connecting;
    return KILN_OK;
}

Slot* find(uint32_t id) {
    for (Slot& s : g_slots)
        if (s.used && s.id == id) return &s;
    return nullptr;
}

KilnResult KILN_CALL t_leave(uint32_t id, uint32_t flags) {
    if (!g_allowed) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (flags & ~static_cast<uint32_t>(KILN_LABBOT_LEAVE_DROP)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    Slot* s = find(id);
    if (!s || !live(*s)) return KILN_ERR_NOT_FOUND;
    s->bot.leave((flags & KILN_LABBOT_LEAVE_DROP) != 0);
    logf(KILN_LOG_INFO, "labbot: bot %u '%s' leaves (%s)", s->id, s->bot.name.c_str(), (flags & KILN_LABBOT_LEAVE_DROP) ? "drop" : "logout");
    return KILN_OK;
}

KilnResult KILN_CALL t_info(uint32_t id, KilnLabBotInfo* out) {
    if (!out || out->size < sizeof(KilnLabBotInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    const Slot* s = find(id);
    if (!s) return KILN_ERR_NOT_FOUND;
    fill_info(*s, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_list(KilnLabBotInfo* out, uint32_t cap, uint32_t* count) {
    if (!count || (cap && !out)) return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t i = 0; i < cap; ++i)
        if (out[i].size < sizeof(KilnLabBotInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    std::vector<const Slot*> all;
    for (const Slot& s : g_slots)
        if (s.used) all.push_back(&s);
    std::sort(all.begin(), all.end(), [](const Slot* a, const Slot* b) { return a->id > b->id; });
    *count = static_cast<uint32_t>(all.size());
    for (uint32_t i = 0; i < cap && i < all.size(); ++i) fill_info(*all[i], &out[i]);
    return KILN_OK;
}

KilnResult KILN_CALL t_input(uint32_t id, const KilnLabBotInput* in) {
    if (!g_allowed) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    constexpr uint32_t kKnown = KILN_LABBOT_INPUT_CAMERA | KILN_LABBOT_INPUT_READY;
    if (!in || in->size < sizeof(KilnLabBotInput) || !in->flags || (in->flags & ~kKnown)) return KILN_ERR_INVALID_ARGUMENT;
    if (in->flags & KILN_LABBOT_INPUT_CAMERA) {
        float len = 0;
        for (float c : in->camera_orientation) {
            if (!std::isfinite(c)) return KILN_ERR_INVALID_ARGUMENT;
            len += c * c;
        }
        if (len < 1e-6f || !std::isfinite(in->camera_distance) || in->camera_distance < 0) return KILN_ERR_INVALID_ARGUMENT;
    }
    if (!g_input_ok) {
        logf(KILN_LOG_WARN, "labbot: bot %u's input refused: %s", id, g_input_why.c_str());
        return KILN_ERR_UNAVAILABLE;
    }
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    Slot* s = find(id);
    if (!s || !live(*s)) return KILN_ERR_NOT_FOUND;
    if (in->flags & KILN_LABBOT_INPUT_CAMERA) {
        s->input.camera = true;
        std::memcpy(s->input.position, in->camera_position, sizeof s->input.position);
        std::memcpy(s->input.orientation, in->camera_orientation, sizeof s->input.orientation);
        s->input.distance = in->camera_distance;
    }
    if (in->flags & KILN_LABBOT_INPUT_READY) s->input.ready = in->ready ? 1 : 0;
    s->input_written = s->input_failed = false;  // the next write says how it went
    logf(KILN_LOG_INFO, "labbot: bot %u '%s' input: camera %s (%.2f, %.2f, %.2f; distance %.2f), ready %d", s->id, s->bot.name.c_str(),
         s->input.camera ? "set" : "not set", static_cast<double>(s->input.position[0]) / 4294967296.0,
         static_cast<double>(s->input.position[1]) / 4294967296.0, static_cast<double>(s->input.position[2]) / 4294967296.0,
         static_cast<double>(s->input.distance), s->input.ready);
    return KILN_OK;
}

const KilnLabBotApi kTable = {{sizeof(KilnLabBotApi), KILN_LABBOT_VERSION}, &t_status, &t_join, &t_leave, &t_info, &t_list, &t_input};

// ------------------------------------------------------------------------------------------- lifecycle -------------
void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (!ev || ev->kind != KILN_WORLD_SERVER || ev->event != KILN_WORLD_STOPPING) return;
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    for (uint32_t i = 0; i < KILN_LABBOT_MAX; ++i) {
        Slot& s = g_slots[i];
        if (!live(s)) continue;
        s.bot.phase = Phase::Left;
        s.bot.reason = "the world ended";
        pub_set(i, s.bot.entry, false);
        logf(KILN_LOG_INFO, "labbot: bot %u '%s' left: the world ended", s.id, s.bot.name.c_str());
        s.logged = Phase::Left;
    }
    g_gs.store(0, std::memory_order_relaxed);
}

// A mod that joined bots unloads: its bots drop (the next receive step takes them out).
void KILN_CALL on_owner_retiring(KilnOwner retiring, void*) {
    std::lock_guard<std::recursive_mutex> lk(g_mu);
    for (Slot& s : g_slots)
        if (live(s) && s.owner == retiring) {
            s.bot.leave(true);
            s.bot.reason = "the mod that joined it unloaded";
        }
}

bool policy_any_from_config() {
    try {
        char b[1024];
        size_t n = 0;
        if (g_api->path(KILN_PATH_KILN, b, sizeof b, &n) != KILN_OK) return false;
        const std::string dir(b, n);
        const std::filesystem::path p = std::filesystem::path(std::u8string(dir.begin(), dir.end())) / "kiln.json";
        std::ifstream f(p);
        if (!f) return false;
        const auto j = nlohmann::json::parse(f, nullptr, false, true);
        if (!j.is_object()) return false;
        // the core's rule (native/core/src/config.cpp): the top level, then the server section replaces it
        bool any = j.contains("policy") && j["policy"].is_string() && j["policy"].get<std::string>() == "any";
        if (j.contains("server") && j["server"].is_object() && j["server"].contains("policy") && j["server"]["policy"].is_string())
            any = j["server"]["policy"].get<std::string>() == "any";
        return any;
    } catch (...) {
        return false;
    }
}

void report(uint32_t state, uint32_t reason, const std::string& text) {
    if (g_caps)
        g_caps->report(g_owner, kstr("labbot.join"), state, reason, KilnStr{text.data(), text.size()});
}

void KILN_CALL on_ready(void*) {
    if (!g_resolved) return;
    const auto* net = static_cast<const KilnNetApi*>(g_api->service(kstr(KILN_NET_SERVICE), 1));
    KilnNetStatus st{};
    st.size = sizeof st;
    g_lab_port = net && net->status(&st) == KILN_OK && st.lab ? st.lab_port : 0;
    g_policy_any = policy_any_from_config();
    if (!g_policy_any || !g_lab_port) {
        g_why_off = std::string("lab servers only: needs ") + (!g_policy_any ? "kiln.json policy \"any\"" : "") +
                    (!g_policy_any && !g_lab_port ? " and " : "") +
                    (!g_lab_port ? "kiln.net's lab port (labPort in kiln/config/network.json)" : "");
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY, g_why_off);
        logf(KILN_LOG_INFO, "labbot: off (%s)", g_why_off.c_str());
        return;
    }
    // lab server: the hooks go in now, never on a server where real players join
    if (!g_publish_hook.install(g_api, g_owner, F.publish, reinterpret_cast<void*>(&hk_publish), "labbot/SessionPublish")) {
        g_why_off = std::string("the session's publish could not be hooked: ") + g_publish_hook.error();
    } else if (!g_receive_hook.install(g_api, g_owner, F.server_receive, reinterpret_cast<void*>(&hk_receive), "net/ServerReceive")) {
        g_publish_hook.revoke();
        g_why_off = std::string("the GameServer's receive could not be hooked: ") + g_receive_hook.error();
    } else {
        g_allowed = true;
        g_why_off.clear();
        resolve_input();
        logf(KILN_LOG_WARN, "labbot: on (lab port %u, policy any; bot players for tests only, never on a public server)", g_lab_port);
        return;
    }
    report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, g_why_off);
    logf(KILN_LOG_ERROR, "labbot: off (%s)", g_why_off.c_str());
}

bool resolve() {
    auto sym = [](const char* n, uintptr_t* o) { return g_api->symbol(kstr(n), o) == KILN_OK && *o; };
    auto cst = [](const char* n, int64_t* o) { return g_api->constant(kstr(n), o) == KILN_OK; };
    auto c32 = [&](const char* n, uint32_t* o) {
        int64_t v = -1;
        if (!cst(n, &v) || v < 0 || v > 0x100000) return false;
        *o = static_cast<uint32_t>(v);
        return true;
    };
    if (!g_api->feature(kstr("labbot.session")) || !g_api->feature(kstr("labbot.join"))) {
        g_why_off = std::string("feature ") + (!g_api->feature(kstr("labbot.session")) ? "labbot.session" : "labbot.join") + " is off on this build";
        return false;
    }
    const bool ok =
        sym("labbot/SessionPublish", &F.publish) && sym("net/ServerReceive", &F.server_receive) && sym("labbot/AddPlayer", &F.add_player) &&
        sym("labbot/Save2Receive", &F.save2_receive) && sym("net/MachineLogin", &F.machine_login) &&
        sym("saves/RequestServerSave", &F.request_save) && sym("labbot/ArrayReserve", &F.array_reserve) &&
        cst("labbot/session.current", &Ly.session_current) && cst("labbot/session.buffers", &Ly.session_buffers) &&
        cst("labbot/session.stride", &Ly.session_stride) && cst("net/gs.session", &Ly.gs_session) && cst("net/gs.sockets", &Ly.gs_sockets) &&
        cst("net/sockets.machines", &Ly.sockets_machines) && cst("net/machine.stride", &Ly.machine_stride) &&
        cst("net/gs.machines", &Ly.gs_machines) && cst("net/gs.records", &Ly.gs_records) && cst("net/rec.stride", &Ly.rec_stride) &&
        cst("net/rec.state", &Ly.rec_state) && cst("net/rec.peer", &Ly.rec_peer) && cst("net/rec.entity", &Ly.rec_entity) &&
        cst("players/rec.permissions", &Ly.rec_perms) && cst("saves/inst.save2", &Ly.gs_save2) &&
        cst("labbot/save2.slots", &Ly.save2_slots) && cst("labbot/save.handle", &Ly.save_handle) &&
        cst("labbot/save.state", &Ly.save_state) && cst("labbot/save.status", &Ly.save_status) &&
        cst("labbot/save.has_entity", &Ly.save_has_entity) && cst("labbot/save.version", &Ly.save_version) &&
        (cst("labbot/save.upload", &Ly.save_upload) || true) && cst("labbot/gs.groups", &Ly.gs_groups) && cst("labbot/gs.groups_count", &Ly.gs_groups_count) &&
        cst("labbot/group.stride", &Ly.group_stride) &&
        c32("labbot/session.stride", &g_snap.size) && c32("labbot/snap.players", &g_snap.players) &&
        c32("labbot/snap.player_stride", &g_snap.player_stride) && c32("labbot/snap.player_count", &g_snap.player_count) &&
        c32("labbot/snap.machines", &g_snap.machines) && c32("labbot/snap.machine_stride", &g_snap.machine_stride) &&
        c32("labbot/snap.machine_count", &g_snap.machine_count) && c32("labbot/snap.p_remote", &g_snap.p_remote) &&
        c32("labbot/snap.p_machine", &g_snap.p_machine) && c32("labbot/snap.p_group", &g_snap.p_group) &&
        c32("labbot/snap.p_account", &g_snap.p_account) && c32("labbot/snap.m_remote", &g_snap.m_remote) &&
        c32("labbot/minfo.players", &g_snap.m_players) && c32("labbot/login.size", &g_login.size) &&
        c32("labbot/login.name_len", &g_login.name_len) && c32("labbot/login.name", &g_login.name) &&
        c32("labbot/login.character", &g_login.character) && c32("labbot/login.toggle_present", &g_login.toggle_present) &&
        c32("labbot/login.toggle", &g_login.toggle) && c32("labbot/login.version_present", &g_login.version_present) &&
        c32("labbot/login.new_present", &g_login.new_present) && c32("labbot/login.is_new", &g_login.is_new);
    if (!ok) {
        g_why_off = "a labbot symbol or constant did not resolve on this build";
        return false;
    }
    // the receive's own login buffer: name_max is the room up to the character-present byte
    int64_t recv_login = -1, recv_name = -1, recv_char = -1;
    if (cst("labbot/recv.login", &recv_login) && cst("labbot/recv.login_name_len", &recv_name) &&
        cst("labbot/recv.login_character_present", &recv_char) && recv_name - recv_login == g_login.name_len &&
        recv_char - recv_login > g_login.name)
        g_login.name_max = static_cast<uint32_t>(recv_char - recv_login) - g_login.name;
    // the layouts the portable part checks, and the record and save fields this file reads
    if (!g_snap.valid() || g_snap.size > 0x2668 || Ly.rec_stride <= 0 || Ly.rec_stride > 0x148 || Ly.rec_state >= Ly.rec_stride ||
        Ly.rec_peer + 4 > Ly.rec_stride || Ly.rec_entity + 4 > Ly.rec_stride || Ly.rec_perms >= Ly.rec_stride || Ly.group_stride < 5 ||
        static_cast<int64_t>(g_snap.size) != Ly.session_stride) {
        g_why_off = "the labbot layouts on this build are not the ones Kiln knows";
        return false;
    }
    return true;
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, subscribe_world) + sizeof(api->subscribe_world))
        return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = kstr(KILN_LABBOT_SERVICE);
    info->version = KilnStr{"1", 1};
    if (api->side() != KILN_SIDE_SERVER) return KILN_ERR_UNAVAILABLE;  // dedicated lab servers only
    g_caps = static_cast<const KilnCapsApi*>(api->service(kstr(KILN_CAPS_SERVICE), 1));
    g_resolved = resolve();
    if (!g_resolved) {
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING, g_why_off);
        logf(KILN_LOG_INFO, "labbot: off (%s)", g_why_off.c_str());
    }
    info->on_ready = &on_ready;
    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    if (const auto* hooks = static_cast<const KilnHooksApi*>(api->service(kstr(KILN_HOOKS_SERVICE), 2));
        hooks && hooks->header.size >= offsetof(KilnHooksApi, subscribe_owner_retiring) + sizeof(void*) && hooks->subscribe_owner_retiring) {
        KilnHandle o = 0;
        hooks->subscribe_owner_retiring(owner, &on_owner_retiring, nullptr, &o);
    }
    return api->register_service(owner, kstr(KILN_LABBOT_SERVICE), &kTable);
}
