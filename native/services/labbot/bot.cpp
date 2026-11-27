// SPDX-License-Identifier: MIT
// kiln.labbot's portable part: bot.h. Engine facts: docs/re/labbot.md.
#include "bot.h"

#include <cstring>

namespace labbot {
namespace {

void put32(std::vector<uint8_t>& v, uint32_t x) {
    for (int i = 0; i < 4; ++i) v.push_back(uint8_t(x >> (8 * i)));
}
void put16(std::vector<uint8_t>& v, uint16_t x) {
    v.push_back(uint8_t(x));
    v.push_back(uint8_t(x >> 8));
}
uint32_t get32(const uint8_t* p) {
    uint32_t x;
    std::memcpy(&x, p, 4);
    return x;
}
void set32(uint8_t* p, uint32_t x) { std::memcpy(p, &x, 4); }
uint64_t get64(const uint8_t* p) {
    uint64_t x;
    std::memcpy(&x, p, 8);
    return x;
}
void set64(uint8_t* p, uint64_t x) { std::memcpy(p, &x, 8); }

uint64_t fnv1a64(std::string_view s, uint64_t h = 0xcbf29ce484222325ull) {
    for (unsigned char c : s) {
        h ^= c;
        h *= 0x100000001b3ull;
    }
    return h;
}

}  // namespace

// ---------------------------------------------------------------------------------------------- wire ----------------
uint32_t fnv1a32(const uint8_t* p, size_t n) {
    uint32_t h = 0x811c9dc5u;
    for (size_t i = 0; i < n; ++i) {
        h ^= p[i];
        h *= 0x1000193u;
    }
    return h;
}

std::vector<uint8_t> new_character_upload() {
    std::vector<uint8_t> v;
    v.push_back(0);  // not compressed
    v.push_back(0);  // no entity blob: the server spawns from the player template
    put32(v, 0);     // entity blob size
    put32(v, 4);     // knowledge sub-stream size
    put32(v, 0);     // knowledge entries
    return v;
}

std::vector<uint8_t> save_chunk(const std::vector<uint8_t>& body) {
    if (body.size() > 0xffff) return {};
    std::vector<uint8_t> v;
    v.push_back(1);
    put32(v, fnv1a32(body.data(), body.size()));
    put32(v, uint32_t(body.size()));
    put32(v, 0);
    put16(v, uint16_t(body.size()));
    v.insert(v.end(), body.begin(), body.end());
    return v;
}

std::vector<uint8_t> save_ack(uint32_t version) {
    std::vector<uint8_t> v{0, 4};
    put32(v, version);
    return v;
}

std::vector<uint8_t> save_final_request() { return {0, 2}; }

bool write_login(const LoginLayout& L, std::string_view name, const uint8_t character[16], bool is_new, uint8_t* out,
                 size_t cap) {
    const uint32_t fields[] = {L.type, L.name_len + 4, L.name + L.name_max, L.character_present + 1, L.character + 16,
                               L.toggle_present + 1, L.toggle + 8, L.version_present + 1, L.new_present + 1, L.is_new + 1};
    for (uint32_t end : fields)
        if (end > L.size) return false;
    if (!out || cap < L.size || name.empty() || name.size() > L.name_max) return false;
    std::memset(out, 0, L.size);
    out[L.type] = 0;
    set32(out + L.name_len, uint32_t(name.size()));
    std::memcpy(out + L.name, name.data(), name.size());
    out[L.character_present] = 1;
    std::memcpy(out + L.character, character, 16);
    out[L.toggle_present] = 1;  // inputToggleConfig 0: the game's default toggles
    out[L.version_present] = 0;
    out[L.new_present] = 1;
    out[L.is_new] = is_new ? 1 : 0;
    return true;
}

// ------------------------------------------------------------------------------------------ identity ----------------
bool valid_name(std::string_view name, std::string* why) {
    auto no = [&](const char* w) {
        if (why) *why = w;
        return false;
    };
    if (name.empty() || name.size() > 32) return no("a bot name is 1 to 32 characters");
    if (name.front() == ' ' || name.back() == ' ') return no("a bot name has no leading or trailing space");
    for (unsigned char c : name) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '_' ||
                        c == '-' || c == '.';
        if (!ok) return no("a bot name uses letters, digits, space, '_', '-' and '.' only");
    }
    return true;
}

void derive_character(std::string_view name, uint8_t out[16]) {
    const uint64_t a = fnv1a64(name, fnv1a64("kiln.labbot character "));
    const uint64_t b = fnv1a64(name, fnv1a64("kiln.labbot character 2 "));
    for (int i = 0; i < 8; ++i) {
        out[i] = uint8_t(a >> (8 * i));
        out[8 + i] = uint8_t(b >> (8 * i));
    }
    out[6] = uint8_t((out[6] & 0x0f) | 0x50);  // version 5 (name-based)
    out[8] = uint8_t((out[8] & 0x3f) | 0x80);  // RFC 4122 variant
}

uint64_t derive_account(std::string_view name) {
    const uint64_t h = fnv1a64(name, fnv1a64("kiln.labbot account "));
    return h ? h : 1;
}

// ------------------------------------------------------------------------------- the session snapshot ----------------
bool SnapLayout::valid() const {
    if (!player_stride || !machine_stride || !player_count || !machine_count) return false;
    if (uint64_t(players) + uint64_t(player_stride) * player_count > size) return false;
    if (uint64_t(machines) + uint64_t(machine_stride) * machine_count > size) return false;
    const uint32_t pmax = p_account + 8 > p_group + 4 ? p_account + 8 : p_group + 4;
    if (pmax > player_stride || p_handle + 4 > player_stride || p_remote + 4 > player_stride || p_machine + 4 > player_stride)
        return false;
    if (m_players + 24 > machine_stride || m_handle + 4 > machine_stride || m_remote + 1 > machine_stride) return false;
    return true;
}

namespace {
uint8_t* pent(uint8_t* b, const SnapLayout& L, uint32_t s) { return b + L.players + s * L.player_stride; }
uint8_t* ment(uint8_t* b, const SnapLayout& L, uint32_t m) { return b + L.machines + m * L.machine_stride; }
const uint8_t* pent(const uint8_t* b, const SnapLayout& L, uint32_t s) { return b + L.players + s * L.player_stride; }
const uint8_t* ment(const uint8_t* b, const SnapLayout& L, uint32_t m) { return b + L.machines + m * L.machine_stride; }
}  // namespace

uint32_t player_in(const uint8_t* buf, const SnapLayout& L, uint32_t slot) {
    return slot < L.player_count ? get32(pent(buf, L, slot) + L.p_handle) : 0;
}
uint32_t machine_in(const uint8_t* buf, const SnapLayout& L, uint32_t index) {
    return index < L.machine_count ? get32(ment(buf, L, index) + L.m_handle) : 0;
}

Put put_entries(uint8_t* buf, const SnapLayout& L, const SnapEntry& e) {
    if (!buf || !L.valid() || e.slot >= L.player_count || e.machine_index >= L.machine_count || e.machine_index == 0 ||
        !e.machine || !e.player)
        return Put::Bad;
    uint8_t* p = pent(buf, L, e.slot);
    uint8_t* m = ment(buf, L, e.machine_index);
    const uint32_t ph = get32(p + L.p_handle), mh = get32(m + L.m_handle);
    if ((ph && ph != e.player) || (mh && mh != e.machine)) return Put::Taken;
    // the machine's player handles: room for one, in the array the game owns (never replaced: its next copy reuses it)
    uint8_t* arr = m + L.m_players;
    uint32_t* data = nullptr;
    std::memcpy(&data, arr, sizeof data);
    const uint64_t capacity = get64(arr + 16);
    if (!data || capacity < 1) return Put::NeedCapacity;
    set32(p + L.p_handle, e.player);
    set32(p + L.p_remote, e.machine);  // nonzero: a remote player (the host's own is 0)
    set32(p + L.p_machine, e.machine);
    set32(p + L.p_group, e.group);
    set64(p + L.p_account, e.account);
    set32(m + L.m_handle, e.machine);
    m[L.m_remote] = 1;
    data[0] = e.player;
    set64(arr + 8, 1);
    return Put::Ok;
}

bool has_entries(const uint8_t* buf, const SnapLayout& L, const SnapEntry& e) {
    if (!buf || !L.valid() || e.slot >= L.player_count || e.machine_index >= L.machine_count) return false;
    const uint8_t* p = pent(buf, L, e.slot);
    const uint8_t* m = ment(buf, L, e.machine_index);
    if (get32(p + L.p_handle) != e.player || get32(m + L.m_handle) != e.machine) return false;
    const uint8_t* arr = m + L.m_players;
    const uint32_t* data = nullptr;
    std::memcpy(&data, arr, sizeof data);
    const uint64_t n = get64(arr + 8);
    for (uint64_t i = 0; data && i < n && i < 16; ++i)
        if (data[i] == e.player) return true;
    return false;
}

// ---------------------------------------------------------------------------------- join and leave ----------------
const char* phase_name(Phase p) {
    switch (p) {
        case Phase::Free: return "free";
        case Phase::Connecting: return "connecting";
        case Phase::LoggingIn: return "logging in";
        case Phase::Loading: return "loading";
        case Phase::Spawning: return "spawning";
        case Phase::InGame: return "in game";
        case Phase::Leaving: return "leaving";
        case Phase::Left: return "left";
        case Phase::Failed: return "failed";
    }
    return "?";
}

void Bot::go(Phase p, uint64_t now, std::string why) {
    phase = p;
    phase_since = now;
    if (!why.empty()) reason = std::move(why);
}

void Bot::leave(bool drop_now) {
    leave_requested = true;
    drop = drop || drop_now;
}

void Bot::ack_saves(Engine& g) {
    const SaveView s = g.save(entry.slot);
    if (!s.readable || s.handle != entry.player || s.state != kSaveWaitAck || s.version == acked) return;
    bool fin = false;
    if (g.save_receive(entry.player, save_ack(s.version), &fin)) {
        acked = s.version;
        ++saves_acked;
    }
}

void Bot::step(Engine& g, uint64_t now, const LoginLayout& L, const Limits& lim) {
    if (phase == Phase::Free || phase == Phase::Left || phase == Phase::Failed) return;
    if (!joined) joined = now;
    if (!phase_since) phase_since = now;
    const uint64_t in_phase = now - phase_since;
    // a leave before the bot is in game, or a drop: the published state stops listing it; the game resets the record
    if (leave_requested && (drop || phase < Phase::InGame)) {
        go(Phase::Left, now, phase < Phase::InGame ? std::string("dropped before it was in game (") + phase_name(phase) + ")"
                                                   : "dropped (as a lost connection)");
        return;
    }
    RecordView r = phase >= Phase::LoggingIn ? g.record(entry.slot) : RecordView{};
    if (phase >= Phase::LoggingIn) {
        if (!r.readable) return fail(now, "the bot's player record cannot be read");
        record_state = r.state;
        permissions = r.permissions;
        entity = r.entity;
        if (r.handle != entry.player) {
            if (phase == Phase::InGame) return go(Phase::Left, now, "the game removed the bot (its record was freed)");
            if (phase == Phase::Leaving) return go(Phase::Left, now, "logged out (the game removed the record)");
            return fail(now, "the bot's player record was reset or taken by another connection");
        }
    }
    switch (phase) {
        case Phase::Connecting: {
            if (!g.in_snapshot(entry)) {
                if (in_phase > lim.connect) fail(now, "the session's published state never listed the bot (the publish hook did not run)");
                return;
            }
            std::vector<uint8_t> m(L.size);
            if (!write_login(L, name, character, new_character, m.data(), m.size())) return fail(now, "the login message does not fit");
            if (!g.add_player(entry.machine, m.data())) return fail(now, "the game's AddPlayer faulted");
            const RecordView a = g.record(entry.slot);
            if (!a.readable || a.handle != entry.player)
                return fail(now, "the game refused the login: no record was made (server full, shutting down, or the "
                                 "machine was not listed)");
            if (a.state == kRecFailed) return fail(now, "the game refused the login (record state 6: version or slot)");
            record_state = a.state;
            return go(Phase::LoggingIn, now);
        }
        case Phase::LoggingIn: {
            if (r.state == kRecFailed) return fail(now, "the game refused the login (record state 6)");
            if (!upload_sent) {
                bool fin = false;
                if (!g.save_receive(entry.player, save_chunk(new_character_upload()), &fin))
                    return fail(now, "the game's save receive faulted on the character upload");
                upload_sent = true;
            }
            const SaveView s = g.save(entry.slot);
            if (s.readable && s.handle == entry.player && s.state != kSaveWaitUpload) {
                if (s.status != 0)
                    return fail(now, "the game refused the character upload (save status " + std::to_string(s.status) + ")");
                return go(Phase::Loading, now);
            }
            if (in_phase > lim.login) fail(now, "the character upload was never taken (save state " + std::to_string(s.state) + ")");
            return;
        }
        case Phase::Loading: {
            if (r.state == kRecFailed) return fail(now, "the game found no spawn position, or could not load the character (record state 6)");
            if (r.state == kRecLoaded || r.state == kRecInGame) {
                if (!ready_sent) {
                    if (!g.machine_login(entry.machine, kSystemsReady) || !g.machine_login(entry.machine, kGameplayReady))
                        return fail(now, "the game's machine login faulted");
                    ready_sent = true;
                }
                if (r.state == kRecInGame) return go(Phase::Spawning, now);
            }
            if (in_phase > lim.load)
                fail(now, "the record stayed in state " + std::to_string(r.state) + (ready_sent ? " after Gameplay Ready" : " (the spawn position search did not end)"));
            return;
        }
        case Phase::Spawning: {
            ack_saves(g);
            if (r.state == kRecFailed) return fail(now, "the game could not spawn the player entity (record state 6)");
            if (r.state == kRecInGame && r.entity) {
                in_game = now;
                return go(Phase::InGame, now, "in game");
            }
            if (in_phase > lim.spawn) fail(now, "the record is in game but no player entity appeared");
            return;
        }
        case Phase::InGame: {
            ack_saves(g);
            if (r.state != kRecInGame) return go(Phase::Left, now, "the game removed the bot (record state " + std::to_string(r.state) + ")");
            if (!leave_requested) return;
            bool fin = false;
            if (!g.save_receive(entry.player, save_final_request(), &fin)) return go(Phase::Left, now, "the save receive faulted on the logout; dropped");
            final_sent = true;
            if (fin && !g.end_game(entry.slot)) return go(Phase::Left, now, "ending the record's game faulted; dropped");
            return go(Phase::Leaving, now);
        }
        case Phase::Leaving: {
            ack_saves(g);
            if (in_phase > lim.leave) go(Phase::Left, now, "the logout did not finish (record state " + std::to_string(r.state) + "); dropped");
            return;
        }
        default: return;
    }
}

}  // namespace labbot
