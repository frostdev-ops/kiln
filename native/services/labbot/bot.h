// SPDX-License-Identifier: MIT
// kiln.labbot's portable part (no Windows, no game): what a bot player sends the dedicated server, how it appears in the
// session's published state, and its join and leave, step by step. labbot.cpp runs it on the game; tests/bot_test.cpp
// on a fake (tests/test-labbot-host.sh). Engine facts and RVAs: docs/re/labbot.md.
#ifndef KILN_LABBOT_BOT_H
#define KILN_LABBOT_BOT_H

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace labbot {

// ---------------------------------------------------------------------------------------------- wire ----------------
// FNV-1a 32: the game's hash over a whole character upload (Save2's chunk receive compares it).
uint32_t fnv1a32(const uint8_t* p, size_t n);

// The character upload of a new character: u8 compressed 0, u8 new character 1, u32 kCharacterBytes, the character
// creator's choices (u8, 9 x u32, u8; all 0: the defaults), u32 knowledge size 4, u32 knowledge count 0. A new character
// makes the server spawn the player from the game's own player template with those choices (the branch a character with
// no saved entity takes, which reads them from the section a saved entity fills otherwise: byte 1 = 0 makes the game
// deserialize the section as a saved entity), so the bot needs no character file.
constexpr size_t kCharacterBytes = 38;
std::vector<uint8_t> new_character_upload();
// One Save2 message on the player's save channel carrying `body` whole: u8 1, u32 hash, u32 total, u32 offset 0, u16
// length, the bytes. Empty when the body does not fit one chunk (65535 bytes).
std::vector<uint8_t> save_chunk(const std::vector<uint8_t>& body);
// u8 0, u8 4 (ack), u32 version: the client's acknowledgement of the server's save `version`.
std::vector<uint8_t> save_ack(uint32_t version);
// u8 0, u8 2: the client asks for its final save (it is leaving); the server's receive then ends the record's game.
std::vector<uint8_t> save_final_request();

// GameMessageLoginRequest as the server's reader fills it (0x140 bytes; offsets from sigdb, defaults = svn 1287369).
struct LoginLayout {
    uint32_t size = 0x140;
    uint32_t type = 0x0;             // u8 GameMessageLoginType: 0 Login, 1 SystemsReady, 2 GameplayReady, 3 Logout
    uint32_t name_len = 0x4;         // u32 length of `username`
    uint32_t name = 0x8;             // the bytes (no terminator needed)
    uint32_t name_max = 0x100;
    uint32_t character_present = 0x108;
    uint32_t character = 0x10c;      // 16 bytes CharacterId
    uint32_t toggle_present = 0x120; // inputToggleConfig: the server's receive requires it present
    uint32_t toggle = 0x128;
    uint32_t version_present = 0x130;  // buildVersion: left absent (the server then skips its version check)
    uint32_t new_present = 0x138;
    uint32_t is_new = 0x139;
};
// A Login message for `name` playing `character` (a new character when `is_new`). false: name too long or bad layout.
bool write_login(const LoginLayout& L, std::string_view name, const uint8_t character[16], bool is_new, uint8_t* out,
                 size_t cap);
// SystemsReady (1) and GameplayReady (2): only the type byte is read.
constexpr uint8_t kSystemsReady = 1, kGameplayReady = 2;

// ------------------------------------------------------------------------------------------ identity ----------------
// 1..32 bytes of [A-Za-z0-9 _-.], no leading or trailing space.
bool valid_name(std::string_view name, std::string* why);
// Stable per name: the same bot name is the same character (and account) on a world, so per-character data (pets,
// quests) carries over between joins. A version-5-shaped GUID from two FNV-1a 64 hashes; never an id the game made.
void derive_character(std::string_view name, uint8_t out[16]);
uint64_t derive_account(std::string_view name);
// Handles: the game's form (index | generation << 7 for machines, slot | generation << 6 for players), with a
// generation no session hands out early (real handles count up from 1).
constexpr uint32_t kGeneration = 0x55;
inline uint32_t machine_handle(uint32_t index) { return kGeneration << 7 | (index & 0x7f); }
inline uint32_t player_handle(uint32_t slot) { return kGeneration << 6 | (slot & 0x3f); }

// ------------------------------------------------------------------------------- the session snapshot ----------------
// One buffer of the session's published double buffer (what the GameServer reads its machines and players from).
struct SnapLayout {
    uint32_t size = 0x2668;
    uint32_t players = 0x178, player_stride = 0x150, player_count = 16;
    uint32_t machines = 0x1678, machine_stride = 0xf0, machine_count = 17;
    // a player entry
    uint32_t p_handle = 0x0, p_remote = 0x4, p_machine = 0x8, p_group = 0xc, p_account = 0x20;
    // a machine entry
    uint32_t m_handle = 0x0, m_remote = 0x4, m_players = 0xc0;  // m_players: {u32* data, u64 count, u64 capacity, ...}
    bool valid() const;
};
struct SnapEntry {
    uint32_t machine_index = 0, slot = 0;
    uint32_t machine = 0, player = 0;  // handles
    uint32_t group = 0;                // the user group id the GameServer looks its rights up by
    uint64_t account = 0;
};
enum class Put { Ok, NeedCapacity, Taken, Bad };
// Writes the bot's player and machine entries into `buf` (the back buffer, before it is published). NeedCapacity: the
// machine entry's player-handle array has no room; the caller grows it through the game's own reserve and calls again.
// Taken: the session itself uses that slot or machine now (a real connection): nothing written.
Put put_entries(uint8_t* buf, const SnapLayout& L, const SnapEntry& e);
// Whether `buf` (a published buffer) shows the bot: both entries, and the machine lists the player.
bool has_entries(const uint8_t* buf, const SnapLayout& L, const SnapEntry& e);
// After the bot left: removes its entries from `buf` (a back buffer, before it is published), each only while it still
// holds the bot's handle (a real connection's entry is never touched). The session copies the current buffer forward
// into the next, so without this a left bot stays listed until the session next copies its own state. true: something
// was removed.
bool clear_entries(uint8_t* buf, const SnapLayout& L, const SnapEntry& e);
// The real session's use of a slot / machine in `buf` (0: free there).
uint32_t player_in(const uint8_t* buf, const SnapLayout& L, uint32_t slot);
uint32_t machine_in(const uint8_t* buf, const SnapLayout& L, uint32_t index);

// Which entries each publish gets (labbot.cpp's publish hook, the session thread; list/unlist from the GameServer step).
// A live bot's entries go into every publish. A bot that left has its entries cleared from the next kClears publishes
// (the session copies the current buffer forward, and a buffer in flight may still hold them), whatever bot is listed
// in its place meanwhile: the clears run first, then the listed bots go in. Not thread-safe: the caller holds one lock.
class Publisher {
public:
    static constexpr uint32_t kBots = 4;      // KILN_LABBOT_MAX
    static constexpr uint8_t kClears = 8;
    static constexpr uint32_t kPending = 16;  // left bots' entries still being cleared
    // The game's buffer, under labbot.cpp's fault guard.
    struct Ops {
        virtual ~Ops() = default;
        virtual Put put(const SnapEntry& e) = 0;     // put_entries (Bad also for a fault)
        virtual bool grow(const SnapEntry& e) = 0;   // room for one handle in the machine's player array (ArrayReserve)
        virtual bool clear(const SnapEntry& e) = 0;  // clear_entries: true = something was removed
    };
    // The bot of `index` (0..kBots-1) is live: its entries go into every publish from now on.
    void list(uint32_t index, const SnapEntry& e);
    // The bot of `index` left (or never got in): its entries come out of the next kClears publishes.
    void unlist(uint32_t index);
    // Nothing to put or clear: the hook leaves the buffer alone.
    bool idle() const;
    // One publish into its back buffer: true when a bot was listed in it.
    bool apply(Ops& ops);
    uint64_t taken() const { return taken_; }    // publishes where the session itself held a bot's slot or machine
    uint64_t faults() const { return faults_; }

private:
    struct Listed {
        bool on = false;
        SnapEntry e;
    };
    struct Pending {
        SnapEntry e;
        uint8_t left = 0;
    };
    Listed listed_[kBots];
    Pending pending_[kPending];
    uint64_t taken_ = 0, faults_ = 0;
};

// ---------------------------------------------------------------------------------- join and leave ----------------
enum class Phase : uint32_t {
    Free = 0,
    Connecting = 1,  // in the session's published state; waiting until the GameServer can see it, then the login
    LoggingIn = 2,   // the login made the record; the character upload goes in
    Loading = 3,     // the record searches a spawn position; then SystemsReady and GameplayReady
    Spawning = 4,    // the record is in game; the GameServer spawns the player entity
    InGame = 5,
    Leaving = 6,     // the final save was asked for; the GameServer removes the entity, then the record
    Left = 7,
    Failed = 8,
};
const char* phase_name(Phase p);

// The game's player record states (record +0x0A).
enum : uint8_t { kRecFree = 0, kRecLoading = 1, kRecLoaded = 2, kRecInGame = 3, kRecRemovingEntity = 4, kRecRemoving = 5, kRecFailed = 6 };
// Save2's per-player states (+0x0C): 1 waits for the client's upload, 2 idle, 3 sending, 4 waits for an ack, 5 final ack.
enum : uint8_t { kSaveWaitUpload = 1, kSaveIdle = 2, kSaveSending = 3, kSaveWaitAck = 4, kSaveFinalAcked = 5, kSaveCancelled = 6 };

struct RecordView {
    bool readable = false;
    uint32_t handle = 0, peer = 0, entity = 0;
    uint8_t state = 0, permissions = 0;
};
struct SaveView {
    bool readable = false;
    uint32_t handle = 0, version = 0;
    uint8_t state = 0, status = 0, has_entity = 0;
    uint64_t upload_capacity = 0;  // bytes Save2's upload buffer holds (0: not known); a bigger upload is refused
};

// What a bot needs of the game: labbot.cpp implements it on the GameServer (inside its receive step, server thread);
// tests on a fake. Calls that run game code return false when the call faulted.
struct Engine {
    virtual ~Engine() = default;
    virtual bool in_snapshot(const SnapEntry& e) = 0;
    virtual bool add_player(uint32_t machine, const uint8_t* login) = 0;
    virtual RecordView record(uint32_t slot) = 0;
    virtual SaveView save(uint32_t slot) = 0;
    // The game's save-channel receive for `player`. *final_requested: the message asked for the final save.
    virtual bool save_receive(uint32_t player, const std::vector<uint8_t>& message, bool* final_requested) = 0;
    virtual bool machine_login(uint32_t machine, uint8_t type) = 0;
    // What the game's receive does after a final request of an in-game record: record state 4, and a world save.
    virtual bool end_game(uint32_t slot) = 0;
};

// Milliseconds each phase may take before the bot gives up (Bot::step's `now` is a millisecond clock: the GameServer's
// receive step runs hundreds of times a second, so a count of steps is no measure of time; lab p1).
struct Limits {
    uint64_t connect = 10000;  // the session publishes within a frame or two, SyncSockets makes the sockets in the next
    uint64_t login = 10000;
    uint64_t load = 60000;     // the spawn position search streams the world around the spawn point
    uint64_t spawn = 60000;
    uint64_t leave = 20000;    // the final save waits up to 5 s for an ack, the record's removal a few frames
};

struct Bot {
    // the request
    std::string name;
    uint8_t character[16] = {};
    uint64_t account = 0;
    bool new_character = true;
    SnapEntry entry;  // slot, machine, handles, group
    // progress
    Phase phase = Phase::Free;
    std::string reason;            // Failed / Left: why; otherwise the last notable event
    uint64_t phase_since = 0;      // `now` the phase began
    uint64_t joined = 0, in_game = 0;  // `now` of the first step and of the entity's appearance
    uint32_t entity = 0;
    uint8_t record_state = 0, permissions = 0;
    bool upload_sent = false, ready_sent = false, final_sent = false;
    bool leave_requested = false, drop = false;
    bool logged_in = false;        // the game's AddPlayer made the bot's record: the game knows its character now (Played)
    uint32_t acked = 0;            // newest save version acknowledged
    uint32_t saves_acked = 0;

    // Whether the published state must list this bot now (the session thread asks).
    bool injected() const { return phase >= Phase::Connecting && phase <= Phase::Leaving; }
    // One server frame. `now` = a millisecond clock (Limits are in milliseconds); it never goes back.
    void step(Engine& g, uint64_t now, const LoginLayout& L, const Limits& lim = {});
    // Ends the bot's game: a clean logout (final save, the game removes it), or `drop` = as a lost connection (the
    // published state stops listing it and the game resets the record). Before the bot is in game: always a drop.
    void leave(bool drop_now);

private:
    void go(Phase p, uint64_t now, std::string why = {});
    void fail(uint64_t now, std::string why) { go(Phase::Failed, now, std::move(why)); }
    void ack_saves(Engine& g);
};

// The characters (and accounts) that logged in on this server since it started: Bot::logged_in. The game does not spawn
// one of them again after its logout while the server keeps running (lab p1, p2, s6: the next join of that name stays in
// the spawn position search, record state 1, until the load limit; a new name joins at once), so kiln.labbot refuses
// such a join at once, with the reason, instead of failing it 60 s later.
class Played {
public:
    void add(const uint8_t character[16], uint64_t account);
    // The character, or the account, logged in before.
    bool has(const uint8_t character[16], uint64_t account) const;
    size_t size() const { return list_.size(); }

private:
    struct One {
        uint8_t character[16];
        uint64_t account;
    };
    std::vector<One> list_;
};

}  // namespace labbot

#endif
