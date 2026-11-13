// SPDX-License-Identifier: MIT
// Tracked writes without the game: the table's layout (append-only), what mods list, one write over a fake component
// store (every status), the check one step later, and the queue (order, cancel, owners, limits).
#include "../boot_programs.h"
#include "../write_queue.h"
#include <cstdio>
#include <cstdlib>
#include <memory>

// The v2 table is a prefix of v3: nothing before write_submit moved, and the new entries follow in this order.
static_assert(offsetof(KilnWorldApi, status) == 184 && offsetof(KilnWorldApi, write_other) == 192);
static_assert(offsetof(KilnWorldApi, program_emit) == 232 && offsetof(KilnWorldApi, write_submit) == 240);
static_assert(offsetof(KilnWorldApi, write_cancel) == 248 && offsetof(KilnWorldApi, write_info) == 256);
static_assert(offsetof(KilnWorldApi, write_status) == 264 && offsetof(KilnWorldApi, write_declare) == 272);
static_assert(sizeof(KilnWorldApi) == 280);
// The managed mirrors (Kiln.World/WorldWrites.cs) and the test kit's fake are pinned to these.
static_assert(sizeof(KilnWorldWriteRequest) == 72 && offsetof(KilnWorldWriteRequest, data) == 24);
static_assert(offsetof(KilnWorldWriteRequest, change) == 32 && offsetof(KilnWorldWriteRequest, mod_id) == 56);
static_assert(sizeof(KilnWorldWriteResult) == 80 && offsetof(KilnWorldWriteResult, ticket) == 8);
static_assert(offsetof(KilnWorldWriteResult, before) == 48 && offsetof(KilnWorldWriteResult, reason) == 64);
static_assert(sizeof(KilnWorldWriteInfo) == 256 && offsetof(KilnWorldWriteInfo, writer_names) == 24);
static_assert(sizeof(KilnWorldWriteStatus) == 72 && offsetof(KilnWorldWriteStatus, submitted) == 32);
static_assert(KILN_WORLD_VERSION == 3u && KILN_WORLD_CAP_TRACKED_WRITES == 8u);

using namespace kw;

namespace {
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) {
        ++failures;
        std::fprintf(stderr, "FAIL %s\n", what);
    }
}

// Components as byte blocks per (entity, hash); counts what the write path did.
struct FakeStore final : WriteStore {
    std::map<std::pair<uint32_t, uint32_t>, std::vector<uint8_t>> blocks;
    int reads = 0, marks = 0, formula_runs = 0, changes = 0;
    bool fault_read = false, fault_mark = false, fault_formulas = false, fault_change = false, move_on_mark = false;
    std::vector<uint8_t> moved;
    bool read(uint32_t entity, const WriteType& type, const uint8_t** data, uint32_t* size) override {
        ++reads;
        *data = nullptr;
        *size = 0;
        if (fault_read) return false;
        auto at = blocks.find({entity, type.hash});
        if (at == blocks.end()) return true;
        *data = at->second.data();
        *size = static_cast<uint32_t>(at->second.size());
        return true;
    }
    bool mark(uint32_t entity, const WriteType& type, uint8_t** data, uint32_t* size) override {
        ++marks;
        *data = nullptr;
        *size = 0;
        if (fault_mark) return false;
        auto at = blocks.find({entity, type.hash});
        if (at == blocks.end()) return true;
        if (move_on_mark) {
            moved = at->second;
            *data = moved.data();
        } else {
            *data = at->second.data();
        }
        *size = static_cast<uint32_t>(at->second.size());
        return true;
    }
    bool formulas(uint8_t* component, const WriteType&) override {
        ++formula_runs;
        if (fault_formulas) return false;
        // the game's clamp, as for Fuel: values[0] is held within 0..values[1]
        int32_t value, max;
        std::memcpy(&value, component + 8, 4);
        std::memcpy(&max, component + 12, 4);
        if (value > max) std::memcpy(component + 8, &max, 4);
        return true;
    }
    bool change(KilnWorldChangeFn fn, uint32_t entity, uint8_t* data, uint32_t size, void* user, bool* accepted) override {
        ++changes;
        if (fault_change) return false;
        *accepted = fn(entity, data, size, user);
        return true;
    }
};

WriteJob job_of(uint32_t entity, uint32_t component, uint32_t offset, std::vector<uint8_t> data, uint32_t flags = 0) {
    WriteJob job;
    job.entity = entity;
    job.component = component;
    job.offset = offset;
    job.length = static_cast<uint32_t>(data.size());
    job.data = std::move(data);
    job.flags = flags;
    return job;
}

std::vector<uint8_t> attribute_block(int32_t value, int32_t max) {
    std::vector<uint8_t> block(16);
    const uint32_t group = 0x05b0bf75;
    const uint16_t offset = 8;
    std::memcpy(block.data(), &group, 4);
    std::memcpy(block.data() + 4, &offset, 2);
    block[6] = 1;
    block[7] = 2;
    std::memcpy(block.data() + 8, &value, 4);
    std::memcpy(block.data() + 12, &max, 4);
    return block;
}

void declarations() {
    WriteDeclarations types;
    auto refused = types.add("alpha", {"Health", "Fuel", "bad-name", "keen::ecs::Health", "", "Health"});
    check(refused == std::vector<std::string>({"bad-name", "keen::ecs::Health", ""}), "names that are not component names are refused");
    check(types.count() == 2 && types.program_names() == std::vector<std::string>({"Health", "Fuel"}), "a repeated name counts once, order kept");
    types.add("beta", {"Fuel", "Scale"});
    WriteType type;
    uint32_t state = 99;
    check(types.lookup("alpha", write_hash("Fuel"), &type, &state) && state == KILN_WORLD_WRITES_PENDING && type.mods.size() == 2,
          "a listed type waits for the world");
    check(types.lookup("alpha", write_hash("Scale"), &type, &state) && state == KILN_WORLD_WRITES_NOT_LISTED,
          "a type another mod listed is not this mod's");
    check(types.lookup("", write_hash("Scale"), &type, &state) && state == KILN_WORLD_WRITES_PENDING, "no mod id: any listed type");
    check(!types.lookup("alpha", write_hash("Mana"), &type, &state) && state == KILN_WORLD_WRITES_NOT_DECLARED, "an unlisted type is not declared");
    types.set_unknown("Scale");
    types.set_program(true, false);
    check(types.lookup("beta", write_hash("Scale"), &type, &state) && state == KILN_WORLD_WRITES_UNKNOWN, "a name the world does not know stays refused");
    check(types.lookup("beta", write_hash("Fuel"), &type, &state) && state == KILN_WORLD_WRITES_READY, "the rest follow the program: ready");
    check(types.program_names() == std::vector<std::string>({"Health", "Fuel"}), "an unknown name leaves the program's list");
    uint32_t declared = 0, off = 0;
    types.counts(&declared, &off);
    check(declared == 2 && off == 1, "counts: two in the program, one refused");
    types.add_writer("Fuel", "fuel_setup", false);
    types.add_writer("Fuel", "actor_interaction", false);
    types.add_writer("Fuel", "fuel_init", true);
    types.add_writer("Mana", "mana_recharge", false);
    types.set_facts(write_hash("Fuel"), 16, true, true, true, false);
    check(types.lookup("", write_hash("Fuel"), &type, &state) && type.writers == 2 && type.init_writers == 1 &&
          type.writer_names == "fuel_setup, actor_interaction" && type.size == 16,
          "the game's writers of a listed type are counted and named, init programs apart");
    check(type.flags == (KILN_WORLD_WROTE_GAME_WRITES | KILN_WORLD_WROTE_ATTRIBUTE | KILN_WORLD_WROTE_BITS_KNOWN | KILN_WORLD_WROTE_SAVED),
          "facts keep the writer flag and carry attribute, saved, not replicated");
    check(types.lookup("", write_hash("Health"), &type, &state) && type.writers == 0 && type.flags == 0, "a type nobody else writes has no writer flag");
    types.clear_writers();
    check(types.lookup("", write_hash("Fuel"), &type, &state) && type.writers == 0 && !(type.flags & KILN_WORLD_WROTE_GAME_WRITES) &&
          (type.flags & KILN_WORLD_WROTE_SAVED), "a new world's scan starts from zero and keeps the facts");
    types.set_program(false, true);
    check(types.lookup("", write_hash("Fuel"), &type, &state) && state == KILN_WORLD_WRITES_OFF, "a refused program turns its types off");

    WriteDeclarations many;
    std::vector<std::string> names;
    for (int i = 0; i < 70; ++i) names.push_back("Type" + std::to_string(i));
    check(many.add("big", names).size() == 6, "one mod lists 64 names at most");
    names.clear();
    for (int i = 0; i < 40; ++i) names.push_back("Other" + std::to_string(i));
    many.add("more", names);
    check(many.count() == 104 && many.program_names().size() == kWriteMaxTypes, "the program takes 92 types, in listing order");
    check(many.lookup("more", write_hash("Other39"), &type, &state) && state == KILN_WORLD_WRITES_OVER_LIMIT, "a type past the limit says so");
    check(many.lookup("more", write_hash("Other27"), &type, &state) && state == KILN_WORLD_WRITES_PENDING, "the last one within it is in");
    many.set_program(true, false);
    check(many.lookup("more", write_hash("Other39"), &type, &state) && state == KILN_WORLD_WRITES_OVER_LIMIT, "and it stays refused once the program runs");
    for (uint32_t s : {KILN_WORLD_WRITES_NOT_DECLARED, KILN_WORLD_WRITES_NOT_LISTED, KILN_WORLD_WRITES_UNKNOWN,
                       KILN_WORLD_WRITES_OVER_LIMIT, KILN_WORLD_WRITES_PENDING, KILN_WORLD_WRITES_OFF})
        check(!write_refusal(s, "Fuel", "alpha").empty(), "every refusal has a sentence");
    check(write_refusal(KILN_WORLD_WRITES_NOT_LISTED, "Fuel", "alpha").find("\"writes\": [\"Fuel\"]") != std::string::npos,
          "the sentence says what to add");
}

void one_write() {
    WriteType plain;
    plain.name = "ScaleComponent";
    plain.hash = write_hash(plain.name);
    plain.state = KILN_WORLD_WRITES_READY;
    plain.writers = 3;
    plain.flags = KILN_WORLD_WROTE_GAME_WRITES | KILN_WORLD_WROTE_BITS_KNOWN | KILN_WORLD_WROTE_SAVED;
    FakeStore store;
    store.blocks[{7, plain.hash}] = {1, 2, 3, 4, 5, 6, 7, 8};

    auto job = job_of(7, plain.hash, 4, {9, 9});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_DONE, "a write within the component is done");
    check(store.blocks[{7, plain.hash}] == std::vector<uint8_t>({1, 2, 3, 4, 9, 9, 7, 8}) && store.marks == 1, "only its bytes change, through one tracked mark");
    check(job.read && job.before == std::vector<uint8_t>({5, 6}) && job.after == std::vector<uint8_t>({9, 9}) && job.component_size == 8,
          "the result carries the bytes before and after");
    check(job.writers == 3 && job.out_flags == plain.flags && job.reason.empty(), "and what is known of the type");

    job = job_of(7, plain.hash, 4, {9, 9});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_UNCHANGED && store.marks == 1,
          "equal bytes: unchanged, and nothing is marked");

    job = job_of(8, plain.hash, 0, {1});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_NO_COMPONENT && !job.read &&
          job.reason.find("entity 8 has no ScaleComponent") != std::string::npos, "an entity without the component is named");

    job = job_of(7, plain.hash, 7, {1, 2});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_OUT_OF_RANGE && job.component_size == 8,
          "bytes past the end are refused with the live size");
    job = job_of(7, plain.hash, 0xFFFFFFF0u, {1, 2});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_OUT_OF_RANGE, "an offset that would wrap is refused");
    job = job_of(7, plain.hash, 0, {1, 2, 3, 4}, KILN_WORLD_WRITE_WHOLE);
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_OUT_OF_RANGE &&
          job.reason.find("4 bytes") != std::string::npos, "a whole value of another size is refused: a layout of another build");
    check(store.blocks[{7, plain.hash}] == std::vector<uint8_t>({1, 2, 3, 4, 9, 9, 7, 8}) && store.marks == 1, "none of the refusals wrote or marked");

    for (uint32_t state : {KILN_WORLD_WRITES_NOT_DECLARED, KILN_WORLD_WRITES_NOT_LISTED, KILN_WORLD_WRITES_UNKNOWN, KILN_WORLD_WRITES_OVER_LIMIT}) {
        job = job_of(7, plain.hash, 0, {1});
        job.mod = "alpha";
        check(!write_run(job, state == KILN_WORLD_WRITES_NOT_DECLARED ? nullptr : &plain, state, store) &&
              job.status == KILN_WORLD_WRITE_NOT_DECLARED && !job.reason.empty(), "a type that is not this mod's to write is not declared, with the reason");
    }
    for (uint32_t state : {KILN_WORLD_WRITES_PENDING, KILN_WORLD_WRITES_OFF}) {
        job = job_of(7, plain.hash, 0, {1});
        check(!write_run(job, &plain, state, store) && job.status == KILN_WORLD_WRITE_DROPPED, "no program: dropped");
    }
    check(store.marks == 1, "and none of those reached the store's write path");

    // the engine side faulting
    store.fault_read = true;
    job = job_of(7, plain.hash, 0, {1});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_FAULTED, "a faulting read fails the write");
    store.fault_read = false;
    store.fault_mark = true;
    job = job_of(7, plain.hash, 0, {42});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_FAULTED &&
          store.blocks[{7, plain.hash}][0] == 1, "a faulting tracked write leaves the bytes alone");
    store.fault_mark = false;
    store.move_on_mark = true;
    job = job_of(7, plain.hash, 0, {42});
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_FAULTED &&
          store.blocks[{7, plain.hash}][0] == 1 && store.moved[0] == 1, "another block than the one read is left alone");
    store.move_on_mark = false;

    // read-modify-write
    job = WriteJob();
    job.entity = 7;
    job.component = plain.hash;
    job.change = [](KilnEntity entity, void* data, uint32_t size, void* user) -> bool {
        auto* bytes = static_cast<uint8_t*>(data);
        *static_cast<uint32_t*>(user) = entity * 1000 + size;
        bytes[0] = static_cast<uint8_t>(bytes[0] + 10);
        return true;
    };
    uint32_t seen = 0;
    job.user = &seen;
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_DONE && seen == 7008,
          "a change callback gets the entity and the whole component");
    check(store.blocks[{7, plain.hash}][0] == 11 && job.offset == 0 && job.length == 8 && job.before[0] == 1 && job.after[0] == 11,
          "its edit is written, the result spans the component");
    job.change = [](KilnEntity, void* data, uint32_t, void*) -> bool {
        static_cast<uint8_t*>(data)[0] = 99;
        return false;
    };
    const int marks = store.marks;
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_REFUSED &&
          store.blocks[{7, plain.hash}][0] == 11 && store.marks == marks, "a callback that returns false changes nothing: it edited a copy");
    job.change = [](KilnEntity, void*, uint32_t, void*) -> bool { return true; };
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_UNCHANGED && store.marks == marks,
          "a callback that changes nothing marks nothing");
    store.fault_change = true;
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_FAULTED && store.marks == marks,
          "a faulting callback fails the write before anything is marked");
    store.fault_change = false;
    store.blocks[{9, plain.hash}] = std::vector<uint8_t>(kWriteMaxComponent + 1);
    job.entity = 9;
    check(!write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_OUT_OF_RANGE, "a component too big to copy is refused");
    auto partial = job_of(9, plain.hash, kWriteMaxComponent - 1, {5, 6});
    check(!write_run(partial, &plain, KILN_WORLD_WRITES_READY, store) && partial.status == KILN_WORLD_WRITE_DONE, "a field of a big component can still be written");

    // attributes
    WriteType fuel;
    fuel.name = "Fuel";
    fuel.hash = write_hash(fuel.name);
    fuel.state = KILN_WORLD_WRITES_READY;
    fuel.flags = KILN_WORLD_WROTE_ATTRIBUTE;
    store.blocks[{20, fuel.hash}] = attribute_block(100, 6400);
    const int32_t over = 9000;
    std::vector<uint8_t> value(4);
    std::memcpy(value.data(), &over, 4);
    job = job_of(20, fuel.hash, 8, value);
    check(!write_run(job, &fuel, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_DONE && store.formula_runs == 1,
          "an attribute's formulas run after its write");
    int32_t held = 0;
    std::memcpy(&held, job.after.data(), 4);
    check(held == 6400, "and the result says what the game made of the value (the clamp)");
    auto header = attribute_block(5, 6400);
    header[7] = 9;
    job = job_of(20, fuel.hash, 0, header, KILN_WORLD_WRITE_WHOLE);
    check(!write_run(job, &fuel, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_REFUSED &&
          job.reason.find("World.Update") != std::string::npos && store.blocks[{20, fuel.hash}][7] == 2,
          "an attribute's header cannot be changed");
    job = job_of(20, fuel.hash, 0, attribute_block(5, 6400), KILN_WORLD_WRITE_WHOLE);
    check(!write_run(job, &fuel, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_DONE && store.formula_runs == 2,
          "a whole attribute with the game's own header is written");
    store.fault_formulas = true;
    job = job_of(20, fuel.hash, 8, value);
    check(!write_run(job, &fuel, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_FAULTED, "faulting formulas are reported");
    store.fault_formulas = false;

    // the check one step later
    job = job_of(7, plain.hash, 1, {77}, KILN_WORLD_WRITE_VERIFY);
    check(write_run(job, &plain, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_DONE, "a write that asks for the check is held");
    write_check(job, &plain, store);
    check(job.out_flags & KILN_WORLD_WROTE_CHECKED && job.out_flags & KILN_WORLD_WROTE_KEPT, "bytes the game left alone are kept");
    job = job_of(7, plain.hash, 1, {78}, KILN_WORLD_WRITE_VERIFY);
    check(write_run(job, &plain, KILN_WORLD_WRITES_READY, store), "held again");
    store.blocks[{7, plain.hash}][1] = 3;  // the game's own writer ran
    write_check(job, &plain, store);
    check((job.out_flags & KILN_WORLD_WROTE_CHECKED) && !(job.out_flags & KILN_WORLD_WROTE_KEPT) && job.status == KILN_WORLD_WRITE_DONE,
          "bytes the game wrote over are checked, not kept; the write still happened");
    job = job_of(7, plain.hash, 1, {79}, KILN_WORLD_WRITE_VERIFY);
    check(write_run(job, &plain, KILN_WORLD_WRITES_READY, store), "held a third time");
    store.blocks.erase({7, plain.hash});
    write_check(job, &plain, store);
    check((job.out_flags & KILN_WORLD_WROTE_CHECKED) && !(job.out_flags & KILN_WORLD_WROTE_KEPT), "an entity gone one step later is not kept");
    WriteType alone = plain;  // a type no program of the game writes: nothing can write over it
    alone.writers = 0;
    alone.flags = 0;
    store.blocks[{7, plain.hash}] = {1, 2, 3, 4, 5, 6, 7, 8};
    job = job_of(7, plain.hash, 1, {80}, KILN_WORLD_WRITE_VERIFY);
    check(!write_run(job, &alone, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_DONE &&
          !(job.out_flags & KILN_WORLD_WROTE_CHECKED), "a type with no other writer is answered at once, unchecked");
    fuel.writers = 2;
    job = job_of(20, fuel.hash, 8, std::vector<uint8_t>(4, 0), KILN_WORLD_WRITE_VERIFY);
    check(write_run(job, &fuel, KILN_WORLD_WRITES_READY, store), "an attribute write is held too");
    job = job_of(20, fuel.hash, 8, std::vector<uint8_t>(4, 0), KILN_WORLD_WRITE_VERIFY);
    check(!write_run(job, &fuel, KILN_WORLD_WRITES_READY, store) && job.status == KILN_WORLD_WRITE_UNCHANGED, "an unchanged write needs no check");
}

int g_delivered = 0;

void queue() {
    WriteQueue q;
    uint64_t t1 = 0, t2 = 0, t3 = 0;
    auto make = [](KilnOwner owner, uint32_t entity) {
        WriteJob job = job_of(entity, 1, 0, {1});
        job.owner = owner;
        job.callback = [](const KilnWorldWriteResult*, void*) { ++g_delivered; };
        return job;
    };
    check(q.submit(make(5, 1), 10, &t1) == KILN_OK && q.submit(make(5, 2), 10, &t2) == KILN_OK && q.submit(make(6, 3), 11, &t3) == KILN_OK,
          "requests are accepted");
    check(t1 == 1 && t2 == 2 && t3 == 3 && q.waiting() == 3 && q.not_run() == 3, "tickets count up from 1");
    check(q.cancel(t2) && !q.cancel(t2) && q.waiting() == 2, "a request that has not run is removed by cancel, once");
    auto batch = q.take(1);
    check(batch.size() == 1 && batch[0].ticket == t1 && batch[0].entity == 1, "the oldest request runs first");
    check(q.cancel(t1), "a request that runs right now can still lose its callback");
    batch[0].status = KILN_WORLD_WRITE_DONE;
    q.complete(std::move(batch[0]), 12);
    q.end_update();
    auto done = q.drain();
    check(done.size() == 1 && done[0].callback == nullptr && done[0].steps == 2, "it completes without a callback, with its steps");
    check(q.drain().empty(), "a result is handed over once");
    batch = q.take(8);
    check(batch.size() == 1 && batch[0].ticket == t3, "the rest follow in order");
    batch[0].status = KILN_WORLD_WRITE_DONE;
    q.hold(std::move(batch[0]));
    q.end_update();
    check(q.waiting() == 1 && q.not_run() == 0 && q.drain().empty(), "a held request waits for its check, not for a run");
    check(q.owners() == std::vector<KilnOwner>({6}), "its owner is still watched");
    check(q.cancel(t3), "cancel reaches a held request");
    auto due = q.verify_due();
    check(due.size() == 1 && due[0].callback == nullptr && q.verify_due().empty(), "the check is due once");
    q.complete(std::move(due[0]), 13);
    q.end_update();
    check(q.drain().size() == 1 && q.waiting() == 0, "and then it is done");

    // owners that unload
    uint64_t a = 0, b = 0, c = 0;
    q.submit(make(7, 1), 20, &a);
    q.submit(make(8, 2), 20, &b);
    q.submit(make(7, 3), 20, &c);
    q.drop_owner(7);
    batch = q.take(8);
    check(batch.size() == 1 && batch[0].ticket == b, "an unloaded owner's waiting requests never run");
    batch[0].status = KILN_WORLD_WRITE_UNCHANGED;
    q.complete(std::move(batch[0]), 21);
    q.end_update();
    q.drop_owner(8);
    done = q.drain();
    check(done.size() == 1 && done[0].callback == nullptr, "and a finished one loses its callback");

    // the world goes away
    q.submit(make(9, 1), 30, &a);
    q.submit(make(9, 2), 30, &b);
    batch = q.take(1);
    batch[0].status = KILN_WORLD_WRITE_DONE;
    q.hold(std::move(batch[0]));
    q.end_update();
    q.drop_waiting("no server world", 40);
    done = q.drain();
    check(done.size() == 2 && q.waiting() == 0, "nothing waits for a world that is gone");
    bool dropped = false, written = false;
    for (const auto& job : done) {
        if (job.status == KILN_WORLD_WRITE_DROPPED && job.reason == "no server world" && job.steps == 10) dropped = true;
        if (job.status == KILN_WORLD_WRITE_DONE && !(job.out_flags & KILN_WORLD_WROTE_CHECKED)) written = true;
    }
    check(dropped && written, "a request that never ran is dropped with the reason; a written one completes unchecked");

    // a request refused at submit
    WriteJob refused = make(9, 5);
    refused.status = KILN_WORLD_WRITE_NOT_DECLARED;
    refused.reason = "add it";
    uint64_t ticket = 0;
    q.reject(std::move(refused), 50, &ticket);
    done = q.drain();
    check(ticket != 0 && done.size() == 1 && done[0].ticket == ticket && done[0].status == KILN_WORLD_WRITE_NOT_DECLARED && q.not_run() == 0,
          "a request refused at submit is delivered like any other, without a run");

    // limits
    WriteQueue full;
    KilnResult last = KILN_OK;
    for (size_t i = 0; i < kWriteMaxPending + 1; ++i) last = full.submit(make(1, 1), 0, nullptr);
    check(last == KILN_ERR_BUFFER_TOO_SMALL && full.waiting() == kWriteMaxPending, "the queue refuses the request past its limit");
    check(full.take(kWritePerUpdate).size() == kWritePerUpdate && full.not_run() == kWriteMaxPending - kWritePerUpdate, "one update takes its share");
    const WriteCounters n = q.counters();
    check(n.submitted == 9 && n.written == 3 && n.unchanged == 1 && n.failed == 2, "the counters add up");
}

void manifest_writes() {
    auto parse = [](const char* text) { return parse_boot_writes(nlohmann::json::parse(text)); };
    auto bad = [&](const char* text) { try { parse(text); return false; } catch (const std::runtime_error&) { return true; } };
    check(parse(R"({"id":"a"})").empty(), "a manifest without writes lists nothing");
    check(parse(R"({"writes":["Fuel","Health","Fuel"]})") == std::vector<std::string>({"Fuel", "Health"}), "writes are read in order, once each");
    check(bad(R"({"writes":"Fuel"})") && bad(R"({"writes":[1]})") && bad(R"({"writes":["keen::ecs::Fuel"]})") && bad(R"({"writes":["a b"]})"),
          "anything but a list of component names is refused");
    std::string many = R"({"writes":[)";
    for (int i = 0; i < 65; ++i) many += (i ? ",\"T" : "\"T") + std::to_string(i) + "\"";
    check(bad((many + "]}").c_str()), "over 64 names are refused");
}
}  // namespace

int main() {
    declarations();
    one_write();
    queue();
    manifest_writes();
    std::printf("world tracked writes: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
