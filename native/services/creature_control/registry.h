// SPDX-License-Identifier: MIT
// kiln.creature_control: the broker itself, without the game. Bodies (bind_body / release_body), owners and their request
// ids, leases and their published snapshots, and the per-step transition of a body (lease.cpp). The game is behind
// `Game` (control.cpp implements it on kiln.world and kiln.creatures; the native tests fake it), and the world program
// only executes the `Op`s a Pre hands it (program.cpp). Everything here but query / can_drive runs on the tick thread.
#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_creature_control.h"
#include "snapshot.h"

namespace kcc {

constexpr uint32_t kNone = UINT32_MAX;
constexpr uint32_t kLeaseSlotBits = 11;
constexpr uint32_t kLeaseSlots = 1u << kLeaseSlotBits;  // 1024 nonterminal leases at most; the rest hold terminal ones
constexpr uint64_t kSecond = 1000000000ull;
constexpr uint64_t kQuiescenceNs = 5 * kSecond;       // the body's current action must end within this
constexpr uint64_t kTransitionNs = kSecond / 2;       // a component removal / restore must verify within this
constexpr uint64_t kRetainNs = 600 * kSecond;         // terminal leases stay queryable this long (simulation time)
constexpr int kZeroSteps = 3;                         // input zeroed for this many steps before a restore
constexpr KilnEntity kFirstBodyId = 1024, kLastBodyId = 0x7fffffffu;  // the engine's counter range (no players)

// What the broker reads of a body, at a job barrier (tick thread, Pre or a tick-thread call).
struct BodyRead {
    bool exists = false;
    bool player = false;
    bool actor = false, input = false, steering = false, enemy_ai = false;
    bool readable = false;       // Actor and ActorInput were read through the guarded offsets
    bool ready = false;          // Actor IsReadyForGameplay
    bool dead = false;           // Actor Dead
    bool action_active = false;  // Actor currentAction.playbackContext.state IsActive
    bool trigger_queued = false; // ActorInput triggerContext.sequenceRuntimeId.sequenceId != 0
};

// The game as the broker needs it. Every call is on the tick thread with no world job running.
class Game {
public:
    virtual ~Game() = default;
    virtual BodyRead read(KilnEntity entity) = 0;
    // The body's whole SteeringAgent as it is now; false when it has none or it could not be read.
    virtual bool steering_bytes(KilnEntity entity, std::vector<uint8_t>* out) = 0;
    // The broker's own AI-off layer (kiln.creatures ai_switch under the broker's owner).
    virtual KilnResult ai_layer(KilnEntity entity, bool off) = 0;
    virtual void log(int32_t level, const std::string& text) = 0;
};

// Work for the broker's world program in the step that follows a Pre (program.cpp executes it).
enum class OpKind : uint8_t {
    ZeroInput = 1,       // ActorInput movement fields to zero (write_other)
    RemoveSteering = 2,  // queue_remove SteeringAgent
    AddSteering = 3,     // queue_add SteeringAgent with `data`
};
struct Op {
    KilnEntity entity = 0;
    OpKind kind = OpKind::ZeroInput;
    std::vector<uint8_t> data;
};

// One lease slot as any thread reads it (query_control, can_drive). Written by the tick thread under `mu`, which is
// held for a copy only: never across a game call or a callback.
struct PublishedLease {
    mutable std::mutex mu;
    uint64_t handle = 0;  // 0: free
    KilnOwner owner = 0;
    uint32_t body = kNone;
    uint64_t generation = 0;  // the body's generation (checked against BodyPublished)
    KilnControlState state{};
};

// Per body slot, lock-free for readers.
struct BodyPublished {
    std::atomic<uint64_t> generation{0};  // 0: free
    std::atomic<uint32_t> latch{0};       // quarantine cause, 0 = none (S6)
};

class Broker {
public:
    explicit Broker(Game* game);
    Broker(const Broker&) = delete;
    Broker& operator=(const Broker&) = delete;

    // ---- the world and the service's state (tick thread) ----
    // The server world's epoch; 0 = none. A change ends every lease with WorldEnded and drops every body (no restore
    // into another world).
    void set_world(uint64_t epoch, const char* why);
    uint64_t epoch() const { return epoch_.load(std::memory_order_acquire); }
    // creature_control (operational): off refuses new binds and admissions (CapabilityOff).
    void set_operational(bool on, const std::string& why);
    bool operational() const { return operational_; }
    void set_layout(const SteeringLayout& layout) { layout_ = layout; }

    // ---- the table (tick thread unless noted) ----
    KilnResult bind(KilnOwner owner, KilnEntity entity, uint32_t baseline_kind, uint32_t retire_on_fatal,
                    KilnControlBodyKey* out);
    KilnResult release_body(KilnOwner owner, const KilnControlBodyKey& key);
    KilnResult next_request(KilnOwner owner, uint64_t* out);
    KilnResult acquire(KilnOwner owner, const KilnControlRequest* request, KilnControlState* out);
    KilnResult release(KilnOwner owner, KilnHandle lease);
    KilnResult notices(KilnOwner owner, KilnControlState* out, uint32_t capacity, uint32_t* written,
                       uint32_t* needs_resync);
    KilnResult query(KilnHandle lease, KilnControlState* out) const;  // any thread
    uint32_t can_drive(KilnOwner owner, KilnHandle lease, const KilnControlBodyKey& key,
                       uint64_t transition_sequence) const;  // any thread

    // ---- the step (tick thread, Pre, no world job running) ----
    // Advances every body's transition at simulation time `now_ns` and fills `ops` with this step's program work.
    void pre(uint64_t now_ns, std::vector<Op>* ops);

    // ---- counters (any thread) ----
    uint32_t bodies() const { return body_count_.load(std::memory_order_relaxed); }
    uint32_t live_leases() const { return live_leases_.load(std::memory_order_relaxed); }
    uint32_t tombstones_reserved() const { return tomb_reserved_; }

    // ---- test surface (native only) ----
    void set_request_capacity(uint32_t n) { request_cap_ = n; }
    void set_notice_capacity(uint32_t n) { notice_cap_ = n; }

private:
    enum class Phase : uint8_t { Idle, Quiesce, Removing, Zeroing, Restoring };

    struct Lease {
        bool used = false;
        uint64_t handle = 0;
        KilnOwner owner = 0;
        uint64_t request_id = 0;
        uint32_t role = 0, flags = 0;
        uint32_t body = kNone;
        KilnControlBodyKey key{};
        uint32_t state = KILN_CONTROL_PENDING, result = KILN_CONTROL_RESULT_NONE;
        std::string reason;
        uint64_t seq = 0;          // transition_sequence when last Active
        bool releasing = false;    // release admitted, restore pending
        bool was_active = false;   // it was Active when released (Released vs Canceled)
        uint64_t terminal_ns = 0;
        KilnControlState last{};   // the last published state (for notices)
        bool published = false;
    };

    struct Body {
        bool used = false;
        bool gone = false;
        KilnControlBodyKey key{};
        uint32_t kind = KILN_CONTROL_BASELINE_ENEMY;
        std::vector<KilnOwner> refs;   // binding references, one per owner
        std::vector<uint32_t> leases;  // nonterminal lease slots, in admission order
        // baseline: what the body was before the first lease of this round
        bool have_base = false, base_steering = false;
        std::vector<uint8_t> steering;
        bool ai_layer = false;         // the broker's AI-off layer is held
        bool steering_out = false;     // the baseline's SteeringAgent is absent now (as of the last read)
        uint64_t seq = 0;              // transition_sequence counter
        uint32_t winner = kNone;       // lease slot that is Active (verified)
        Phase phase = Phase::Idle;
        uint32_t target = kNone;       // lease slot the running entry transition is for
        uint64_t wait_start = 0, op_start = 0;
        int zero_left = 0;
        bool tomb = false;             // a tombstone is reserved for it
    };

    struct OwnerRec {
        uint64_t next = 0;  // last id next_request gave
        uint64_t high = 0;  // highest request id admitted
        std::unordered_map<uint64_t, uint64_t> requests;  // request id -> lease handle (while retained)
        std::deque<KilnControlState> queue;               // notices
        uint64_t notice_seq = 0;
        bool resync = false;
    };

    // registry.cpp
    uint32_t find_body(const KilnControlBodyKey& key) const;  // kNone when not bound (or another generation)
    void free_body(uint32_t b);
    void maybe_free_body(uint32_t b);
    uint32_t alloc_lease();
    void free_lease(uint32_t slot);
    Lease* lease_by_handle(KilnHandle handle);
    void publish(uint32_t slot, bool notify);
    void publish_body(uint32_t b, bool notify);
    void notify(OwnerRec& o, const KilnControlState& s);
    void expire_terminal();
    void log(int32_t level, const char* fmt, ...);
    static const char* state_name(uint32_t s);
    static const char* role_name(uint32_t r);

    // lease.cpp
    void refuse(KilnControlState* out, const KilnControlRequest& request, uint32_t result, const char* reason) const;
    void terminate(uint32_t slot, uint32_t state, uint32_t result, const std::string& reason);
    uint32_t pick_winner(const Body& b) const;
    void step_body(uint32_t b, std::vector<Op>* ops);
    void enter(uint32_t b, const BodyRead& r, uint32_t want, std::vector<Op>* ops);
    void restore(uint32_t b, const BodyRead& r, std::vector<Op>* ops);
    void grant(uint32_t b, uint32_t slot);
    void lose_body(uint32_t b, uint32_t result, const std::string& why);
    void settle_releasing(uint32_t b);
    std::vector<uint8_t> restore_bytes(const Body& b) const;
    uint32_t changed_mask(const Body& b) const;

    Game* game_;
    std::atomic<uint64_t> epoch_{0};
    bool operational_ = false;
    std::string off_why_ = "kiln.creature_control has not started";
    SteeringLayout layout_{};
    uint64_t now_ = 0;
    uint64_t pre_count_ = 0;

    std::array<Body, KILN_CONTROL_MAX_BODIES> bodies_{};
    std::array<BodyPublished, KILN_CONTROL_MAX_BODIES> bodies_pub_{};
    std::unordered_map<KilnEntity, uint32_t> by_entity_;
    uint64_t generation_ = 0;  // last body generation issued (process-wide: never reused)
    uint32_t tomb_reserved_ = 0;

    std::vector<Lease> leases_;
    std::unique_ptr<PublishedLease[]> pub_;
    std::array<uint64_t, kLeaseSlots> slot_serial_{};
    std::atomic<uint64_t> serial_high_{0};  // highest serial issued (any slot)
    uint64_t serial_ = 0;
    std::vector<uint32_t> free_slots_;

    std::unordered_map<KilnOwner, OwnerRec> owners_;
    uint32_t queued_ = 0;  // admissions since the last Pre
    uint32_t request_cap_ = KILN_CONTROL_REQUEST_QUEUE;
    uint32_t notice_cap_ = KILN_CONTROL_NOTICE_QUEUE;
    std::atomic<uint32_t> body_count_{0}, live_leases_{0};
};

// Copies `text` into a state's reason (NUL-terminated, cut at a UTF-8 boundary).
void set_reason(KilnControlState* s, const std::string& text);

}  // namespace kcc
