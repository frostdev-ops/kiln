// SPDX-License-Identifier: MIT
// kiln.world tracked writes, the part that needs no engine: which component types the mods listed at boot and which
// of them the program takes, the request queue (world identity, limits, the watch over waiting requests' entities),
// one write over an abstract component store, the check one step later, the results and their delivery, and the
// read-only catalog write_info answers from. Portable, so it is host-tested without Wine (tests/writes_test.cpp).
// writes.cpp binds it to the game (docs/re/world.md, "Tracked writes").
#pragma once
#include <algorithm>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_world.h"

namespace kw {

constexpr uint32_t kWriteMaxBytes = 4096;  // the bytes of one request (the engine's own add record carries at most this)
constexpr uint32_t kWriteMaxComponent = 65536;  // a component a change callback gets a copy of
constexpr size_t kWriteMaxPending = 4096;  // requests not yet delivered (waiting, held for their check, or answered
                                           // and waiting for their delivery, refusals included); more are refused
constexpr size_t kWritePerUpdate = 512;    // requests run in one update of the program
constexpr size_t kWriteDeliverPerPre = 1024;  // answers delivered in one Pre; the rest in the next
constexpr size_t kWriteMaxTypes = 92;      // the engine's limit of other-entity accessors of one program
constexpr size_t kWriteMaxPerMod = 64;     // names in one mod's "writes"
constexpr size_t kAttributeHeader = 8;     // {u32 group; u16 values offset; u8 flags; u8 count}
constexpr int64_t kWriteStallMs = 10000;   // requests wait this long for a program that does not run

// What the tracked write path needs of the game: the program hook, the engine's WriteOther (with the job's tracker
// and write flag), ReadOther (every write reads the component first, and the check reads it again), the job's
// storage, and the component map (names and the accessors' check). Missing any: the capability is not advertised.
struct WriteDeps {
    bool programs = false, write_other = false, read_other = false, names = false;
    int64_t job_storage = -1;
};
inline bool write_deps_ok(const WriteDeps& d) { return d.programs && d.write_other && d.read_other && d.names && d.job_storage >= 0; }

inline uint32_t write_hash(std::string_view name) {  // FNV-1a-32 of "keen::ecs::" + name
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : std::string_view("keen::ecs::")) h = (h ^ c) * 0x01000193u;
    for (unsigned char c : name) h = (h ^ c) * 0x01000193u;
    return h;
}

inline bool write_name(std::string_view name) {  // a component's short name
    if (name.empty() || name.size() > 120) return false;
    for (char c : name)
        if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_') return false;
    return true;
}

// ---- what the mods listed -------------------------------------------------------------------------------------

struct WriteType {
    std::string name;
    uint32_t hash = 0;
    std::set<std::string> mods;                    // the mods that list it
    uint32_t state = KILN_WORLD_WRITES_PENDING;    // _PENDING until a server world registered the program
    uint32_t flags = 0;                            // KILN_WORLD_WROTE_GAME_WRITES | _BITS_KNOWN | _SAVED | _REPLICATED | _ATTRIBUTE
    uint32_t writers = 0, init_writers = 0, size = 0;
    std::string writer_names;
    bool attribute() const { return (flags & KILN_WORLD_WROTE_ATTRIBUTE) != 0; }
};

// Built at boot on one thread (add); the first server world's registration decides which names the program takes
// (admit) and fills in what it knows (set_*); every thread reads (lookup, snapshot). One mutex: none of it is on a hot
// path.
class WriteDeclarations {
  public:
    // One mod's "writes". Names that are not component names are returned (refused); a repeated name counts once. Every
    // accepted name waits (PENDING) until a server world says which of them it has (admit).
    std::vector<std::string> add(const std::string& mod, const std::vector<std::string>& names) {
        std::lock_guard lock(mutex_);
        std::vector<std::string> refused;
        size_t taken = 0;
        for (const auto& name : names) {
            if (!write_name(name) || taken >= kWriteMaxPerMod) {
                refused.push_back(name);
                continue;
            }
            ++taken;
            const uint32_t hash = write_hash(name);
            auto at = by_hash_.find(hash);
            if (at == by_hash_.end()) {
                WriteType type;
                type.name = name;
                type.hash = hash;
                order_.push_back(hash);
                at = by_hash_.emplace(hash, std::move(type)).first;
            } else if (at->second.name != name) {  // two names, one hash: the second is refused, never mixed up
                refused.push_back(name);
                continue;
            }
            at->second.mods.insert(mod);
        }
        return refused;
    }

    // The first server world's answer, before the program is made: in listing order (boot order of the mods, then
    // each mod's own order), a name the world has no component type for is UNKNOWN and takes no accessor; of the
    // others the first kWriteMaxTypes are the program's and the rest OVER_LIMIT. Once: later worlds keep the answer.
    void admit(const std::function<bool(uint32_t hash)>& in_world) {
        std::lock_guard lock(mutex_);
        size_t taken = 0;
        for (uint32_t hash : order_) {
            WriteType& type = by_hash_.at(hash);
            if (type.state != KILN_WORLD_WRITES_PENDING) continue;
            if (in_world && !in_world(hash)) type.state = KILN_WORLD_WRITES_UNKNOWN;
            else if (taken >= kWriteMaxTypes) type.state = KILN_WORLD_WRITES_OVER_LIMIT;
            else ++taken;
        }
    }

    // The names for the program's other-write list (admitted ones), in the order they were listed.
    std::vector<std::string> program_names() const {
        std::lock_guard lock(mutex_);
        std::vector<std::string> names;
        for (uint32_t hash : order_) {
            const WriteType& type = by_hash_.at(hash);
            if (type.state != KILN_WORLD_WRITES_OVER_LIMIT && type.state != KILN_WORLD_WRITES_UNKNOWN) names.push_back(type.name);
        }
        return names;
    }

    size_t count() const {
        std::lock_guard lock(mutex_);
        return order_.size();
    }

    // Counts by outcome: in the program, and listed but not in it.
    void counts(uint32_t* declared, uint32_t* refused) const {
        std::lock_guard lock(mutex_);
        *declared = *refused = 0;
        for (const auto& [hash, type] : by_hash_)
            ++*(type.state == KILN_WORLD_WRITES_OVER_LIMIT || type.state == KILN_WORLD_WRITES_UNKNOWN ? refused : declared);
    }

    // The world has no component type of that name (the program is registered without it).
    void set_unknown(std::string_view name) {
        std::lock_guard lock(mutex_);
        if (auto at = by_hash_.find(write_hash(name)); at != by_hash_.end()) at->second.state = KILN_WORLD_WRITES_UNKNOWN;
    }

    // The program's state after a world registered (or refused) it: every name still in the list follows it.
    void set_program(bool ready, bool off) {
        std::lock_guard lock(mutex_);
        for (auto& [hash, type] : by_hash_) {
            if (type.state == KILN_WORLD_WRITES_OVER_LIMIT || type.state == KILN_WORLD_WRITES_UNKNOWN) continue;
            type.state = off ? KILN_WORLD_WRITES_OFF : ready ? KILN_WORLD_WRITES_READY : KILN_WORLD_WRITES_PENDING;
        }
    }

    // A program of the game writes `component` (init: only when an entity is created).
    void add_writer(std::string_view component, std::string_view program, bool init) {
        std::lock_guard lock(mutex_);
        auto at = by_hash_.find(write_hash(component));
        if (at == by_hash_.end() || at->second.name != component) return;
        WriteType& type = at->second;
        if (init) {
            ++type.init_writers;
            return;
        }
        ++type.writers;
        type.flags |= KILN_WORLD_WROTE_GAME_WRITES;
        if (type.writer_names.size() + program.size() + 2 < 200) {
            if (!type.writer_names.empty()) type.writer_names += ", ";
            type.writer_names += program;
        }
    }

    void clear_writers() {
        std::lock_guard lock(mutex_);
        for (auto& [hash, type] : by_hash_) {
            type.writers = type.init_writers = 0;
            type.writer_names.clear();
            type.flags &= ~static_cast<uint32_t>(KILN_WORLD_WROTE_GAME_WRITES);
        }
    }

    // What the build says about the type: its reflected size, whether it is an attribute, its save and replication bits.
    void set_facts(uint32_t hash, uint32_t size, bool attribute, bool bits_known, bool saved, bool replicated) {
        std::lock_guard lock(mutex_);
        auto at = by_hash_.find(hash);
        if (at == by_hash_.end()) return;
        WriteType& type = at->second;
        type.size = size;
        type.flags &= KILN_WORLD_WROTE_GAME_WRITES;
        if (attribute) type.flags |= KILN_WORLD_WROTE_ATTRIBUTE;
        if (bits_known) type.flags |= KILN_WORLD_WROTE_BITS_KNOWN | (saved ? KILN_WORLD_WROTE_SAVED : 0) | (replicated ? KILN_WORLD_WROTE_REPLICATED : 0);
    }

    std::vector<WriteType> snapshot() const {
        std::lock_guard lock(mutex_);
        std::vector<WriteType> all;
        for (uint32_t hash : order_) all.push_back(by_hash_.at(hash));
        return all;
    }

    // The type and its state for `mod` ("" = any mod). False when nobody listed it (state = _NOT_DECLARED).
    bool lookup(std::string_view mod, uint32_t hash, WriteType* out, uint32_t* state) const {
        std::lock_guard lock(mutex_);
        auto at = by_hash_.find(hash);
        if (at == by_hash_.end()) {
            *state = KILN_WORLD_WRITES_NOT_DECLARED;
            return false;
        }
        *out = at->second;
        *state = at->second.state;
        if (!mod.empty() && !at->second.mods.count(std::string(mod))) *state = KILN_WORLD_WRITES_NOT_LISTED;
        return true;
    }

  private:
    mutable std::mutex mutex_;
    std::map<uint32_t, WriteType> by_hash_;
    std::vector<uint32_t> order_;
};

// ---- one request ----------------------------------------------------------------------------------------------

struct WriteJob {
    uint64_t ticket = 0;
    KilnOwner owner = 0;
    uint32_t entity = 0, component = 0, offset = 0, length = 0, flags = 0;
    std::vector<uint8_t> data;
    KilnWorldChangeFn change = nullptr;
    KilnWorldWriteFn callback = nullptr;
    void* user = nullptr;
    std::string mod;
    uint64_t submitted = 0;  // world step at submit
    uint64_t epoch = 0;      // the server world it was asked in (writes.cpp counts worlds from 1); another: dropped
    bool lost = false;       // its entity lacked the component at an update while the request waited: it never runs
    // the outcome
    uint32_t status = KILN_WORLD_WRITE_DROPPED, out_flags = 0, component_size = 0, writers = 0, steps = 0;
    bool read = false;  // before/after hold bytes
    std::vector<uint8_t> before, after;
    std::string reason;
};

// What the write program can do with one component of one entity. writes.cpp: the engine; the tests: a map.
struct WriteStore {
    // The component as it is now, read-only. False: reading faulted. *data null: the entity has none.
    virtual bool read(uint32_t entity, const WriteType& type, const uint8_t** data, uint32_t* size) = 0;
    // The same block through the game's tracked path, which marks it for the save and for replication. False: the
    // call faulted. *data null: gone.
    virtual bool mark(uint32_t entity, const WriteType& type, uint8_t** data, uint32_t* size) = 0;
    // An attribute's formulas after its values changed. False: it faulted.
    virtual bool formulas(uint8_t* component, const WriteType& type) = 0;
    // The request's change callback on a copy. False: it faulted. *accepted: what it returned.
    virtual bool change(KilnWorldChangeFn fn, uint32_t entity, uint8_t* data, uint32_t size, void* user, bool* accepted) = 0;
    virtual ~WriteStore() = default;
};

inline std::string write_text(const char* format, ...) {
    char buffer[512];
    va_list args;
    va_start(args, format);
    const int n = std::vsnprintf(buffer, sizeof buffer, format, args);
    va_end(args);
    return n <= 0 ? std::string() : std::string(buffer, static_cast<size_t>(n) < sizeof buffer ? static_cast<size_t>(n) : sizeof buffer - 1);
}

// Why a component cannot be written for this mod, as a sentence that says what to do.
inline std::string write_refusal(uint32_t state, std::string_view component, std::string_view mod) {
    const std::string c(component.empty() ? std::string_view("?") : component), m(mod);
    switch (state) {
        case KILN_WORLD_WRITES_NOT_DECLARED:
            return write_text("no loaded mod lists this component under \"writes\" in its kiln.mod.json: add it there (Kiln.Sdk "
                              "does that for a mod whose code calls World.Write or World.Update with the type) and restart the "
                              "server; the game fixes a program's access when the world starts");
        case KILN_WORLD_WRITES_NOT_LISTED:
            return write_text("mod '%s' does not list '%s' under \"writes\" in its kiln.mod.json (another mod does): add "
                              "\"writes\": [\"%s\"] and restart the server", m.c_str(), c.c_str(), c.c_str());
        case KILN_WORLD_WRITES_UNKNOWN:
            return write_text("this game build has no component type '%s' (listed under \"writes\"; check the name)", c.c_str());
        case KILN_WORLD_WRITES_OVER_LIMIT:
            return write_text("'%s' was listed after the write program's %u component types were taken (the game's limit of "
                              "one program); fewer mods or fewer \"writes\" entries are needed", c.c_str(),
                              static_cast<unsigned>(kWriteMaxTypes));
        case KILN_WORLD_WRITES_PENDING:
            return "no server world runs yet: the write program has not been registered";
        case KILN_WORLD_WRITES_OFF:
            return "tracked writes are off: the write program was refused, faulted or is unavailable on this game build (kiln.log says which)";
        default:
            return "";
    }
}

inline std::string write_lost(uint32_t entity, std::string_view component) {
    const std::string c(component);
    return write_text("entity %u lost its %s while the write waited (it was removed, or removed and made again under the same id, "
                      "as a player's entity is when the slot is taken by the next player): nothing was written", entity, c.c_str());
}

inline const char* write_world_changed() { return "the server world it was asked in closed or was replaced before the write ran"; }

// Runs one request against the store. Fills the job's outcome; returns true when the job wants its check one step
// later: KILN_WORLD_WRITE_VERIFY on a write that happened, of a type the game's own programs write too (nothing can
// write over a type that has no other writer, so its answer does not wait).
inline bool write_run(WriteJob& job, const WriteType* type, uint32_t state, WriteStore& store) {
    auto fail = [&](uint32_t status, std::string reason) {
        job.status = status;
        job.reason = std::move(reason);
        return false;
    };
    if (!type || state != KILN_WORLD_WRITES_READY) {
        const bool never = state == KILN_WORLD_WRITES_PENDING || state == KILN_WORLD_WRITES_OFF;
        return fail(never ? KILN_WORLD_WRITE_DROPPED : KILN_WORLD_WRITE_NOT_DECLARED,
                    write_refusal(state, type ? std::string_view(type->name) : std::string_view(), job.mod));
    }
    job.out_flags = type->flags;
    job.writers = type->writers;
    if (job.lost)  // the id may name another entity by now (a player's slot is reused): never write through it
        return fail(KILN_WORLD_WRITE_NO_COMPONENT, write_lost(job.entity, type->name));
    const uint8_t* now = nullptr;
    uint32_t size = 0;
    if (!store.read(job.entity, *type, &now, &size)) return fail(KILN_WORLD_WRITE_FAULTED, "reading the component faulted");
    if (!now || size == 0)
        return fail(KILN_WORLD_WRITE_NO_COMPONENT, write_text("entity %u has no %s (or does not exist)", job.entity, type->name.c_str()));
    job.component_size = size;
    std::vector<uint8_t> next;
    if (job.change) {
        if (size > kWriteMaxComponent)
            return fail(KILN_WORLD_WRITE_OUT_OF_RANGE, write_text("%s is %u bytes; a change callback takes components up to %u (write a field instead)",
                                                                  type->name.c_str(), size, kWriteMaxComponent));
        job.offset = 0;
        job.length = size;
        next.assign(now, now + size);
        bool accepted = false;
        if (!store.change(job.change, job.entity, next.data(), size, job.user, &accepted))
            return fail(KILN_WORLD_WRITE_FAULTED, "the change callback faulted; the component is as it was");
        if (!accepted) {
            job.before.assign(now, now + size);
            job.after = job.before;
            job.read = true;
            return fail(KILN_WORLD_WRITE_REFUSED, "the change callback returned false; the component is as it was");
        }
    } else {
        if ((job.flags & KILN_WORLD_WRITE_WHOLE) && (job.offset != 0 || job.length != size))
            return fail(KILN_WORLD_WRITE_OUT_OF_RANGE, write_text("the value is %u bytes and %s of entity %u is %u: the layout is not this game build's",
                                                                  job.length, type->name.c_str(), job.entity, size));
        if (job.offset > size || job.length > size - job.offset)
            return fail(KILN_WORLD_WRITE_OUT_OF_RANGE, write_text("bytes %u..%u are outside %s (%u bytes)", job.offset,
                                                                  job.offset + job.length, type->name.c_str(), size));
        next = job.data;
    }
    job.before.assign(now + job.offset, now + job.offset + job.length);
    job.after = job.before;
    job.read = true;
    if (std::memcmp(next.data(), now + job.offset, job.length) == 0) {
        job.status = KILN_WORLD_WRITE_UNCHANGED;
        return false;
    }
    if (type->attribute() && job.offset < kAttributeHeader) {
        const uint32_t n = std::min<uint32_t>(job.length, static_cast<uint32_t>(kAttributeHeader) - job.offset);
        if (std::memcmp(next.data(), now + job.offset, n) != 0)
            return fail(KILN_WORLD_WRITE_REFUSED, write_text("%s is an attribute: its first %u bytes (group, values offset, flags, count) are the "
                                                             "game's and cannot change; change the values of a copy read from the entity "
                                                             "(World.Update)", type->name.c_str(), static_cast<unsigned>(kAttributeHeader)));
    }
    uint8_t* block = nullptr;
    uint32_t marked = 0;
    if (!store.mark(job.entity, *type, &block, &marked)) return fail(KILN_WORLD_WRITE_FAULTED, "the game's tracked write faulted; nothing was written");
    if (block != now || marked != size)  // not the block that was read: leave it alone
        return fail(KILN_WORLD_WRITE_FAULTED, "the game's tracked write returned another block than the read; nothing was written");
    std::memcpy(block + job.offset, next.data(), job.length);
    if (type->attribute() && !store.formulas(block, *type)) {
        job.after.assign(block + job.offset, block + job.offset + job.length);
        return fail(KILN_WORLD_WRITE_FAULTED, "the bytes were written, but the attribute's formulas faulted");
    }
    job.after.assign(block + job.offset, block + job.offset + job.length);
    job.status = KILN_WORLD_WRITE_DONE;
    return (job.flags & KILN_WORLD_WRITE_VERIFY) != 0 && type->writers > 0;
}

// The check one step later, after the game's programs ran once more: are the written bytes still there?
inline void write_check(WriteJob& job, const WriteType* type, WriteStore& store) {
    job.out_flags |= KILN_WORLD_WROTE_CHECKED;
    const uint8_t* now = nullptr;
    uint32_t size = 0;
    if (!type || !store.read(job.entity, *type, &now, &size) || !now || job.offset > size || job.length > size - job.offset) return;
    if (std::memcmp(now + job.offset, job.after.data(), job.length) == 0) job.out_flags |= KILN_WORLD_WROTE_KEPT;
}

// ---- the queue -----------------------------------------------------------------------------------------------

struct WriteCounters {
    uint64_t submitted = 0, written = 0, unchanged = 0, failed = 0;
};

// submit and cancel: any thread. take, watch, verify_due, hold, complete: the write program. drain, wanted,
// end_delivery: the tick thread.
class WriteQueue {
  public:
    KilnResult submit(WriteJob&& job, uint64_t step, uint64_t* ticket) {
        std::lock_guard lock(mutex_);
        if (outstanding_() >= kWriteMaxPending) return KILN_ERR_BUFFER_TOO_SMALL;
        job.ticket = ++last_;
        job.submitted = step;
        if (ticket) *ticket = job.ticket;
        pending_.push_back(std::move(job));
        ++counters_.submitted;
        return KILN_OK;
    }

    // A request whose outcome is known at submit (not declared, off): it only waits for its delivery. It counts against
    // the same limit as every other request.
    KilnResult reject(WriteJob&& job, uint64_t step, uint64_t* ticket) {
        std::lock_guard lock(mutex_);
        if (outstanding_() >= kWriteMaxPending) return KILN_ERR_BUFFER_TOO_SMALL;
        job.ticket = ++last_;
        job.submitted = step;
        if (ticket) *ticket = job.ticket;
        ++counters_.submitted;
        ++counters_.failed;
        done_.push_back(std::move(job));
        return KILN_OK;
    }

    // A request that has not run is removed; one that waits for its check or its delivery (also inside the delivery
    // loop that drained it) loses its callback.
    bool cancel(uint64_t ticket) {
        std::lock_guard lock(mutex_);
        for (auto at = pending_.begin(); at != pending_.end(); ++at)
            if (at->ticket == ticket) {
                pending_.erase(at);
                return true;
            }
        for (auto* list : {&checking_, &done_})
            for (auto& job : *list)
                if (job.ticket == ticket && job.callback) {
                    job.callback = nullptr;
                    return true;
                }
        if (delivering_.erase(ticket)) return true;
        return cancelled_running_(ticket);
    }

    // Up to `max` requests, oldest first, for one update of the program.
    std::vector<WriteJob> take(size_t max) {
        std::lock_guard lock(mutex_);
        std::vector<WriteJob> batch;
        while (batch.size() < max && !pending_.empty()) {
            batch.push_back(std::move(pending_.front()));
            pending_.pop_front();
        }
        running_.clear();
        for (const auto& job : batch) running_.push_back(job.ticket);
        return batch;
    }

    // The requests that still wait after this update took its share: `present(job)` says whether the entity has the
    // component now. One that lacks it is lost for good: its id may be handed to another entity before it runs.
    void watch(const std::function<bool(const WriteJob&)>& present) {
        std::lock_guard lock(mutex_);
        for (auto& job : pending_)
            if (!job.lost && !present(job)) job.lost = true;
    }

    // A new server world (`epoch`) at world step `step`: every request of an earlier one ends. One that has not run is
    // dropped; one that waits for its check completes as written, unchecked.
    void drop_stale(uint64_t epoch, uint64_t step) {
        std::lock_guard lock(mutex_);
        for (auto at = pending_.begin(); at != pending_.end();) {
            if (at->epoch == epoch) {
                ++at;
                continue;
            }
            at->status = KILN_WORLD_WRITE_DROPPED;
            at->reason = write_world_changed();
            at->steps = static_cast<uint32_t>(step >= at->submitted ? step - at->submitted : 0);
            ++counters_.failed;
            done_.push_back(std::move(*at));
            at = pending_.erase(at);
        }
        for (auto at = checking_.begin(); at != checking_.end();) {
            if (at->epoch == epoch) {
                ++at;
                continue;
            }
            at->steps = static_cast<uint32_t>(step >= at->submitted ? step - at->submitted : 0);
            ++counters_.written;
            done_.push_back(std::move(*at));
            at = checking_.erase(at);
        }
    }

    // The requests whose write happened in an earlier update: their check is due now.
    std::vector<WriteJob> verify_due() {
        std::lock_guard lock(mutex_);
        std::vector<WriteJob> due(std::make_move_iterator(checking_.begin()), std::make_move_iterator(checking_.end()));
        checking_.clear();
        for (const auto& job : due) running_.push_back(job.ticket);
        return due;
    }

    void hold(WriteJob&& job) {
        std::lock_guard lock(mutex_);
        apply_cancel_(job);
        checking_.push_back(std::move(job));
    }

    void complete(WriteJob&& job, uint64_t step) {
        std::lock_guard lock(mutex_);
        apply_cancel_(job);
        job.steps = static_cast<uint32_t>(step >= job.submitted ? step - job.submitted : 0);
        if (job.status == KILN_WORLD_WRITE_DONE) ++counters_.written;
        else if (job.status == KILN_WORLD_WRITE_UNCHANGED) ++counters_.unchanged;
        else ++counters_.failed;
        done_.push_back(std::move(job));
    }

    void end_update() {
        std::lock_guard lock(mutex_);
        running_.clear();
        dropped_running_.clear();
    }

    // Up to `max` finished requests, oldest first, for delivery on the tick thread (write_deliver). They stay
    // cancellable until end_delivery.
    std::vector<WriteJob> drain(size_t max = kWriteDeliverPerPre) {
        std::lock_guard lock(mutex_);
        std::vector<WriteJob> out;
        while (out.size() < max && !done_.empty()) {
            out.push_back(std::move(done_.front()));
            done_.pop_front();
        }
        delivering_.clear();
        for (const auto& job : out)
            if (job.callback) delivering_.emplace(job.ticket, job.owner);
        return out;
    }

    // Whether a drained request is still to be delivered: not cancelled, its owner not dropped since the drain.
    bool wanted(uint64_t ticket) const {
        std::lock_guard lock(mutex_);
        return delivering_.count(ticket) != 0;
    }

    void end_delivery() {
        std::lock_guard lock(mutex_);
        delivering_.clear();
    }

    // Everything that has not run fails with `reason` (the world closed, the program is off). Requests that wait for
    // their check complete as written, unchecked.
    void drop_waiting(const std::string& reason, uint64_t step) {
        std::lock_guard lock(mutex_);
        for (auto& job : pending_) {
            job.status = KILN_WORLD_WRITE_DROPPED;
            job.reason = reason;
            job.steps = static_cast<uint32_t>(step >= job.submitted ? step - job.submitted : 0);
            ++counters_.failed;
            done_.push_back(std::move(job));
        }
        pending_.clear();
        for (auto& job : checking_) {
            job.steps = static_cast<uint32_t>(step >= job.submitted ? step - job.submitted : 0);
            ++counters_.written;
            done_.push_back(std::move(job));
        }
        checking_.clear();
    }

    // The owner is gone: its waiting requests are forgotten and nothing of it is called any more.
    void drop_owner(KilnOwner owner) {
        std::lock_guard lock(mutex_);
        pending_.erase(std::remove_if(pending_.begin(), pending_.end(), [&](const WriteJob& j) { return j.owner == owner; }), pending_.end());
        for (auto* list : {&checking_, &done_})
            for (auto& job : *list)
                if (job.owner == owner) job.callback = nullptr;
        for (auto at = delivering_.begin(); at != delivering_.end();)
            at = at->second == owner ? delivering_.erase(at) : std::next(at);
    }

    std::vector<KilnOwner> owners() const {
        std::lock_guard lock(mutex_);
        std::set<KilnOwner> all;
        for (const auto& job : pending_) all.insert(job.owner);
        for (const auto& job : checking_) all.insert(job.owner);
        return {all.begin(), all.end()};
    }

    // Waiting for a run or for its check: what the write program still owes.
    size_t waiting() const {
        std::lock_guard lock(mutex_);
        return pending_.size() + checking_.size();
    }
    size_t not_run() const {
        std::lock_guard lock(mutex_);
        return pending_.size();
    }
    // Everything not delivered yet: what the limit counts.
    size_t outstanding() const {
        std::lock_guard lock(mutex_);
        return outstanding_();
    }
    WriteCounters counters() const {
        std::lock_guard lock(mutex_);
        return counters_;
    }

  private:
    size_t outstanding_() const { return pending_.size() + checking_.size() + done_.size(); }
    bool cancelled_running_(uint64_t ticket) {
        if (std::find(running_.begin(), running_.end(), ticket) == running_.end()) return false;
        dropped_running_.insert(ticket);
        return true;
    }
    void apply_cancel_(WriteJob& job) {
        if (dropped_running_.erase(job.ticket)) job.callback = nullptr;
    }

    mutable std::mutex mutex_;
    std::deque<WriteJob> pending_, checking_, done_;
    std::vector<uint64_t> running_;      // taken by the update that runs now
    std::set<uint64_t> dropped_running_; // cancelled while their update ran
    std::map<uint64_t, KilnOwner> delivering_;  // drained and not delivered yet
    uint64_t last_ = 0;
    WriteCounters counters_;
};

// Delivers drained results in order. Right before each callback the request must still be wanted (a callback that ran
// before it may have cancelled it) and its owner alive (`alive`: a callback that ran before it may have unloaded that
// owner, freeing what `user` points at). A dead owner is dropped from the queue at once.
template <class Alive, class Deliver>
size_t write_deliver(WriteQueue& queue, std::vector<WriteJob>& done, Alive&& alive, Deliver&& deliver) {
    size_t delivered = 0;
    for (auto& job : done) {
        if (!job.callback || !queue.wanted(job.ticket)) continue;
        if (!alive(job.owner)) {
            queue.drop_owner(job.owner);
            continue;
        }
        deliver(job);
        ++delivered;
    }
    queue.end_delivery();
    return delivered;
}

// The requests that wait for a write program that does not run (no server world steps): after kWriteStallMs of
// waiting with no update in that time, they end. Counts both the requests that have not run and the ones held for
// their check, so a world that stops between a write and its check still answers.
class WriteWatchdog {
  public:
    bool stalled(const WriteQueue& queue, int64_t now_ms, int64_t last_update_ms) {
        if (queue.waiting() == 0) {
            since_ = 0;
            return false;
        }
        if (since_ == 0) since_ = now_ms;
        if (now_ms - since_ <= kWriteStallMs || now_ms - last_update_ms <= kWriteStallMs) return false;
        since_ = 0;
        return true;
    }

  private:
    int64_t since_ = 0;
};

// ---- what write_info answers for a type nobody listed --------------------------------------------------------

// One component type of the world, as the world's setup saw it: the game's programs that write it, its reflected size,
// whether it is an attribute, its save and replication bits.
struct CatalogEntry {
    uint32_t writers = 0, init_writers = 0, size = 0, flags = 0;  // flags: GAME_WRITES | BITS_KNOWN | SAVED | REPLICATED | ATTRIBUTE
    std::string writer_names;
};

// Built on the server thread inside a world's setup and published whole; any thread reads it without touching the
// world (the world's maps change and go away with the world; this does not).
class WriteCatalog {
  public:
    using Map = std::map<uint32_t, CatalogEntry>;

    void publish(std::shared_ptr<const Map> map) {
        std::lock_guard lock(mutex_);
        map_ = std::move(map);
    }
    std::shared_ptr<const Map> current() const {
        std::lock_guard lock(mutex_);
        return map_;
    }
    // False before a world published one, or when the world has no such type.
    bool find(uint32_t hash, CatalogEntry* out) const {
        const auto map = current();
        if (!map) return false;
        const auto at = map->find(hash);
        if (at == map->end()) return false;
        *out = at->second;
        return true;
    }

  private:
    mutable std::mutex mutex_;
    std::shared_ptr<const Map> map_;
};

}  // namespace kw
