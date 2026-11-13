// SPDX-License-Identifier: MIT
// Tracked writes without the game: the table's layout (append-only) and a consumer built against v2 on it, what mods
// list and which names the program takes, one write over a fake component store (every status), the check one step
// later, the queue (order, cancel, owners, limits, world identity, the watch over waiting requests' entities), the
// delivery, the watchdog, the catalog, and the boundaries tests/parity.json shares with Kiln.TestKit's fake world.
#include "../boot_programs.h"
#include "../transform_layout.h"
#include "../write_queue.h"
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <memory>
#include <thread>
#include <type_traits>

// v2_consumer.cpp, compiled against the header 0.2.0-dev.26 shipped.
extern "C" bool v2_accepts(const void* table);
extern "C" void v2_call_all(const void* table);
extern "C" uint32_t v2_entries();
extern "C" uint32_t v2_table_size();
extern "C" uint32_t v2_cap_bits();
extern "C" uint32_t v2_struct_sizes(uint32_t* out, uint32_t capacity);

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
    check(many.lookup("more", write_hash("Other39"), &type, &state) && state == KILN_WORLD_WRITES_PENDING,
          "before a world says which names it has, every listed name waits");
    many.admit([](uint32_t) { return true; });
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

// Refusals at submit count against the same limit, and one Pre delivers a bounded share.
void saturation() {
    WriteQueue q;
    int refused = 0;
    for (size_t i = 0; i < kWriteMaxPending + 10; ++i) {
        WriteJob job = job_of(1, 1, 0, {1});
        job.status = KILN_WORLD_WRITE_NOT_DECLARED;
        job.callback = [](const KilnWorldWriteResult*, void*) {};
        if (q.reject(std::move(job), 0, nullptr) != KILN_OK) ++refused;
    }
    check(refused == 10 && q.outstanding() == kWriteMaxPending, "refusals pile up to the limit and no further");
    check(q.submit(job_of(1, 1, 0, {1}), 0, nullptr) == KILN_ERR_BUFFER_TOO_SMALL, "and a request to run is refused while they wait");
    auto first = q.drain();
    check(first.size() == kWriteDeliverPerPre && q.outstanding() == kWriteMaxPending - kWriteDeliverPerPre,
          "one Pre delivers a bounded share, the rest wait for the next");
    q.end_delivery();
    check(q.submit(job_of(1, 1, 0, {1}), 0, nullptr) == KILN_OK, "delivered answers free their places");
}

// The world a request was asked in, and the entity it names.
void identity() {
    WriteQueue q;
    auto make = [](uint64_t epoch, uint32_t entity) {
        WriteJob job = job_of(entity, 1, 0, {1});
        job.epoch = epoch;
        job.callback = [](const KilnWorldWriteResult*, void*) {};
        return job;
    };
    uint64_t old_pending = 0, old_held = 0, now_pending = 0;
    q.submit(make(1, 5), 10, &old_held);
    q.submit(make(1, 6), 10, &old_pending);
    auto batch = q.take(1);
    batch[0].status = KILN_WORLD_WRITE_DONE;
    q.hold(std::move(batch[0]));
    q.end_update();
    q.submit(make(2, 7), 11, &now_pending);
    q.drop_stale(2, 12);
    auto done = q.drain();
    check(done.size() == 2 && q.waiting() == 1 && q.not_run() == 1, "a new world ends the requests of the old one, not its own");
    bool dropped = false, unchecked = false;
    for (const auto& job : done) {
        if (job.ticket == old_pending && job.status == KILN_WORLD_WRITE_DROPPED && job.reason == write_world_changed()) dropped = true;
        if (job.ticket == old_held && job.status == KILN_WORLD_WRITE_DONE && !(job.out_flags & KILN_WORLD_WROTE_CHECKED)) unchecked = true;
    }
    check(dropped && unchecked, "one that never ran is dropped with the reason; one already written completes unchecked");
    q.end_delivery();
}

// A request that waits behind a full update while its entity goes away and an entity is made again under the same id
// (a player's slot: entity = slot + 1) never writes into the new entity.
void lifetime(const nlohmann::json& fx) {
    const auto& lt = fx.at("lifetime");
    WriteType scale;
    scale.name = "ScaleComponent";
    scale.hash = write_hash(scale.name);
    scale.state = KILN_WORLD_WRITES_READY;
    FakeStore store;
    store.blocks[{1, scale.hash}] = {0, 0, 0, 0};       // player 1's entity
    store.blocks[{900, scale.hash}] = {0, 0, 0, 0};     // the filler's target
    WriteQueue q;
    const size_t filler = lt.at("filler").get<size_t>();
    for (size_t i = 0; i < filler; ++i) q.submit(job_of(900, scale.hash, 0, {static_cast<uint8_t>(i), 1, 1, 1}), 0, nullptr);
    uint64_t mine = 0;
    q.submit(job_of(1, scale.hash, 0, {7, 7, 7, 7}), 0, &mine);
    auto present = [&](const WriteJob& job) {
        const uint8_t* data = nullptr;
        uint32_t size = 0;
        return store.read(job.entity, scale, &data, &size) && data && size;
    };
    // update 1: the filler takes the whole share; the player leaves during it
    for (auto& job : q.take(kWritePerUpdate)) {
        write_run(job, &scale, KILN_WORLD_WRITES_READY, store);
        q.complete(std::move(job), 1);
    }
    store.blocks.erase({1, scale.hash});
    q.watch(present);
    q.end_update();
    // the next player takes the slot: the same id, a new entity
    store.blocks[{1, scale.hash}] = {3, 3, 3, 3};
    // update 2: the waiting request runs
    auto batch = q.take(kWritePerUpdate);
    check(batch.size() == 1 && batch[0].ticket == mine && batch[0].lost, "the request that waited saw its entity go");
    write_run(batch[0], &scale, KILN_WORLD_WRITES_READY, store);
    const bool status = std::string(lt.at("status").get<std::string>()) == "NoComponent" && batch[0].status == KILN_WORLD_WRITE_NO_COMPONENT;
    check(status && batch[0].reason.find(lt.at("reasonContains").get<std::string>()) != std::string::npos,
          "it fails as the fixture says, with the reason");
    check(store.blocks[{1, scale.hash}] == std::vector<uint8_t>({3, 3, 3, 3}), "and the new entity under the same id is untouched");
    // an entity that is there all the time is not lost
    WriteQueue r;
    for (size_t i = 0; i < filler + 1; ++i) r.submit(job_of(900, scale.hash, 0, {1}), 0, nullptr);
    r.take(kWritePerUpdate);
    r.watch(present);
    auto rest = r.take(kWritePerUpdate);
    check(rest.size() == 1 && !rest[0].lost, "a waiting request whose entity stays is not lost");
}

// The watchdog counts the requests held for their check too: a world that stops between a write and its check still
// answers it.
void watchdog() {
    WriteQueue q;
    uint64_t t = 0;
    WriteJob held = job_of(1, 1, 0, {1}, KILN_WORLD_WRITE_VERIFY);
    held.callback = [](const KilnWorldWriteResult*, void*) {};
    q.submit(std::move(held), 0, &t);
    auto batch = q.take(8);
    batch[0].status = KILN_WORLD_WRITE_DONE;
    q.hold(std::move(batch[0]));
    q.end_update();
    check(q.not_run() == 0 && q.waiting() == 1, "only a held request is left");
    WriteWatchdog w;
    const int64_t last_update = 1000;
    check(!w.stalled(q, 1000, last_update), "it starts watching");
    check(!w.stalled(q, 1000 + kWriteStallMs, last_update), "10 s are not over yet");
    check(w.stalled(q, 2000 + kWriteStallMs, last_update), "past 10 s with no update, a held request alone is stalled");
    q.drop_waiting("no world", 5);
    auto done = q.drain();
    check(done.size() == 1 && done[0].status == KILN_WORLD_WRITE_DONE && !(done[0].out_flags & KILN_WORLD_WROTE_CHECKED),
          "the held request completes, written and unchecked");
    q.end_delivery();
    WriteWatchdog running;
    WriteQueue busy, idle;
    busy.submit(job_of(1, 1, 0, {1}), 0, nullptr);
    check(!running.stalled(busy, 20000, 0) && !running.stalled(busy, 45000, 44000), "a program that runs is never stalled");
    check(!running.stalled(idle, 90000, 0), "nothing waiting: nothing to drop");
}

// Each callback is checked right before it runs: one that ran before it may have cancelled it or unloaded its owner.
std::set<KilnOwner> g_dead;
std::vector<int> g_ran;
WriteQueue* g_q = nullptr;
uint64_t g_cancel_me = 0;

void delivery() {
    WriteQueue q;
    g_q = &q;
    auto make = [](KilnOwner owner, int tag, KilnWorldWriteFn fn) {
        WriteJob job = job_of(1, 1, 0, {1});
        job.owner = owner;
        job.status = KILN_WORLD_WRITE_NOT_DECLARED;
        job.callback = fn;
        job.user = reinterpret_cast<void*>(static_cast<intptr_t>(tag));
        return job;
    };
    auto record = [](const KilnWorldWriteResult*, void* user) { g_ran.push_back(static_cast<int>(reinterpret_cast<intptr_t>(user))); };
    // 1 (owner 10) unloads owner 20; 2 (owner 20) must not run. 3 (owner 10) cancels 4; 4 must not run. 5 runs.
    q.reject(make(10, 1, [](const KilnWorldWriteResult*, void*) { g_ran.push_back(1); g_dead.insert(20); }), 0, nullptr);
    q.reject(make(20, 2, record), 0, nullptr);
    q.reject(make(10, 3, [](const KilnWorldWriteResult*, void*) { g_ran.push_back(3); g_q->cancel(g_cancel_me); }), 0, nullptr);
    q.reject(make(30, 4, record), 0, &g_cancel_me);
    q.reject(make(30, 5, record), 0, nullptr);
    auto done = q.drain();
    const size_t n = write_deliver(q, done, [](KilnOwner owner) { return !g_dead.count(owner); },
                                   [](WriteJob& job) {
                                       KilnWorldWriteResult r{};
                                       r.size = sizeof r;
                                       job.callback(&r, job.user);
                                   });
    check(g_ran == std::vector<int>({1, 3, 5}) && n == 3, "a callback is not run after an earlier one unloaded its owner or cancelled it");
    check(!q.wanted(g_cancel_me), "nothing stays wanted after the delivery");
    g_q = nullptr;
}

void deps() {
    WriteDeps d;
    d.programs = d.write_other = d.read_other = d.names = true;
    d.job_storage = 0x40;
    check(write_deps_ok(d), "everything resolved: tracked writes are offered");
    for (int i = 0; i < 5; ++i) {
        WriteDeps m = d;
        if (i == 0) m.programs = false;
        if (i == 1) m.write_other = false;
        if (i == 2) m.read_other = false;
        if (i == 3) m.names = false;
        if (i == 4) m.job_storage = -1;
        check(!write_deps_ok(m), i == 2 ? "without ReadOther (every write reads first) they are not offered" : "a missing dependency turns them off");
    }
}

// write_info's source for a type nobody listed: published whole, read on any thread while a new one is published.
void catalog() {
    WriteCatalog c;
    CatalogEntry e;
    check(!c.find(5, &e), "nothing before a world published its catalog");
    auto map = std::make_shared<WriteCatalog::Map>();
    (*map)[5].writers = 2;
    (*map)[5].writer_names = "fuel_setup, actor_interaction";
    c.publish(map);
    check(c.find(5, &e) && e.writers == 2 && e.writer_names == "fuel_setup, actor_interaction" && !c.find(6, &e), "a published entry is found");
    std::atomic<bool> stop{false};
    std::atomic<int> reads{0}, bad{0};
    std::vector<std::thread> readers;
    for (int t = 0; t < 4; ++t)
        readers.emplace_back([&] {
            CatalogEntry x;
            while (!stop.load()) {
                if (c.find(5, &x) && x.writer_names.size() < 5) ++bad;
                ++reads;
            }
        });
    for (int i = 0; i < 2000; ++i) {
        auto next = std::make_shared<WriteCatalog::Map>();
        for (uint32_t k = 0; k < 64; ++k) (*next)[k].writer_names = "program_" + std::to_string(i);
        c.publish(next);
    }
    stop.store(true);
    for (auto& t : readers) t.join();
    check(bad.load() == 0 && reads.load() > 0, "readers on other threads always see a whole catalog");
}

// ---- tests/parity.json: the boundaries Kiln.TestKit's fake world mirrors ----

std::vector<std::string> expand(const nlohmann::json& spec) {
    std::vector<std::string> out;
    auto one = [&](const nlohmann::json& item) {
        if (item.is_string()) {
            out.push_back(item.get<std::string>());
            return;
        }
        const std::string pattern = item.at("pattern").get<std::string>();
        const int count = item.at("count").get<int>(), from = item.value("from", 0);
        for (int i = from; i < from + count; ++i) {
            std::string name = pattern;
            if (const auto at = name.find("{i}"); at != std::string::npos) name.replace(at, 3, std::to_string(i));
            out.push_back(name);
        }
    };
    if (spec.is_array()) for (const auto& item : spec) one(item);
    else one(spec);
    return out;
}

// "known:3" -> a name the test's world has, "unknown:3" -> one it does not.
std::string world_name(const std::string& ref) {
    const auto colon = ref.find(':');
    return (ref.substr(0, colon) == "known" ? "Known" : "Nope") + ref.substr(colon + 1);
}

const char* state_name(uint32_t state) {
    switch (state) {
        case KILN_WORLD_WRITES_READY: return "Ready";
        case KILN_WORLD_WRITES_PENDING: return "Pending";
        case KILN_WORLD_WRITES_NOT_DECLARED: return "NotDeclared";
        case KILN_WORLD_WRITES_NOT_LISTED: return "NotListed";
        case KILN_WORLD_WRITES_UNKNOWN: return "UnknownComponent";
        case KILN_WORLD_WRITES_OVER_LIMIT: return "OverLimit";
        default: return "Off";
    }
}

void parity(const nlohmann::json& fx) {
    const auto& lim = fx.at("limits");
    check(lim.at("perUpdate") == kWritePerUpdate && lim.at("maxPending") == kWriteMaxPending && lim.at("maxTypes") == kWriteMaxTypes &&
          lim.at("maxPerMod") == kWriteMaxPerMod && lim.at("deliverPerPre") == kWriteDeliverPerPre && lim.at("maxBytes") == kWriteMaxBytes &&
          lim.at("maxPerMod") == kBootWrites, "parity: the limits are the native ones");

    // one update runs perUpdate requests, the next the rest
    const auto& b = fx.at("batch");
    WriteQueue q;
    for (int i = 0; i < b.at("submitted").get<int>(); ++i) q.submit(job_of(1, 1, 0, {1}), 0, nullptr);
    const size_t first = q.take(kWritePerUpdate).size();
    q.end_update();
    const size_t second = q.take(kWritePerUpdate).size();
    check(first == b.at("firstStep").get<size_t>() && second == b.at("secondStep").get<size_t>(), "parity: 513 requests run 512, then 1");

    // the manifest's "writes", as boot_programs.cpp reads it
    for (const auto& c : fx.at("manifests")) {
        nlohmann::json manifest = c.at("manifest");
        if (manifest.contains("writes") && manifest["writes"].is_object()) manifest["writes"] = expand(manifest["writes"]);
        bool valid = true;
        std::vector<std::string> list;
        try { list = parse_boot_writes(manifest); }
        catch (const std::runtime_error&) { valid = false; }
        const std::string what = "parity manifest: " + c.at("case").get<std::string>();
        check(valid == c.at("valid").get<bool>(), what.c_str());
        if (valid && c.contains("writes")) check(list == c.at("writes").get<std::vector<std::string>>(), what.c_str());
        if (valid && c.contains("count")) check(list.size() == c.at("count").get<size_t>(), what.c_str());
    }

    // which names the program takes
    std::set<uint32_t> world;
    for (int i = 0; i < 200; ++i) world.insert(write_hash("Known" + std::to_string(i)));
    for (const auto& c : fx.at("declarations")) {
        WriteDeclarations d;
        for (const auto& mod : c.at("mods")) {
            std::vector<std::string> names;
            for (const auto& ref : expand(mod.at("writes"))) names.push_back(world_name(ref));
            d.add(mod.at("id").get<std::string>(), names);
        }
        d.admit([&](uint32_t hash) { return world.count(hash) != 0; });
        d.set_program(true, false);
        auto expect = [&](const std::string& mod, const nlohmann::json& states) {
            for (const auto& [ref, want] : states.items()) {
                WriteType type;
                uint32_t state = 0;
                d.lookup(mod, write_hash(world_name(ref)), &type, &state);
                const std::string what = "parity declarations: " + c.at("case").get<std::string>() + " (" + ref + " for '" + mod + "': " +
                                         state_name(state) + ")";
                check(want.get<std::string>() == state_name(state), what.c_str());
            }
        };
        expect("", c.at("expect"));
        if (c.contains("expectFor"))
            for (const auto& [mod, states] : c.at("expectFor").items()) expect(mod, states);
    }

    // CurrentTransform's bytes
    const auto& t = fx.at("transform");
    const std::string hex = t.at("hex").get<std::string>();
    std::vector<uint8_t> bytes;
    for (size_t i = 0; i + 1 < hex.size(); i += 2) bytes.push_back(static_cast<uint8_t>(std::stoul(hex.substr(i, 2), nullptr, 16)));
    KilnTransform out{};
    check(t.at("minSize") == kTransformMinSize && t.at("component") == "CurrentTransform" &&
          write_hash("CurrentTransform") == kCurrentTransformHash, "parity transform: the component and its size");
    check(transform_decode(bytes.data(), static_cast<uint32_t>(bytes.size()), &out), "parity transform: decoded");
    const auto p = t.at("position"), r = t.at("rotation"), s = t.at("scale");
    bool same = out.position.x == p[0].get<double>() && out.position.y == p[1].get<double>() && out.position.z == p[2].get<double>();
    for (int i = 0; i < 4; ++i) same = same && out.rotation[i] == r[i].get<float>();
    for (int i = 0; i < 3; ++i) same = same && out.scale[i] == s[i].get<float>();
    check(same, "parity transform: position, rotation and scale as the fixture says");
    check(!transform_decode(bytes.data(), kTransformMinSize - 1, &out), "parity transform: a smaller component is no transform");
}

// ---- a consumer built against v2, on the v3 table ----

std::vector<int> g_hits;
template <int I, class F>
struct StubOf;
template <int I, class R, class... A>
struct StubOf<I, R(KILN_CALL*)(A...)> {
    static R KILN_CALL fn(A...) {
        g_hits.push_back(I);
        if constexpr (!std::is_void_v<R>) return R{};
    }
};

void v2_consumer() {
    KilnWorldApi t{};
    t.header = {sizeof(KilnWorldApi), KILN_WORLD_VERSION};
    int i = 0;
#define STUB(field) t.field = &StubOf<__COUNTER__, decltype(t.field)>::fn
    const int base = __COUNTER__ + 1;
    STUB(register_program); STUB(program_slot); STUB(program_info); STUB(iter_begin); STUB(iter_next); STUB(current_entity);
    STUB(command_buffer); STUB(queue_push); STUB(queue_add); STUB(queue_remove); STUB(mark_written); STUB(read_other);
    STUB(component_hash); STUB(component); STUB(exists); STUB(transform); STUB(position); STUB(find); STUB(entity_count);
    STUB(server_world); STUB(teleport); STUB(teleport_cancel); STUB(status); STUB(write_other); STUB(unregister_program);
    STUB(bind_program); STUB(capabilities); STUB(program_events); STUB(program_emit);
    STUB(write_submit); STUB(write_cancel); STUB(write_info); STUB(write_status); STUB(write_declare);
#undef STUB
    (void)i;
    check(v2_accepts(&t), "v2 consumer: a v3 table passes its size and version checks");
    check(v2_table_size() == offsetof(KilnWorldApi, write_submit), "v2 consumer: its whole table is the v3 table's prefix");
    g_hits.clear();
    v2_call_all(&t);
    bool in_order = g_hits.size() == v2_entries();
    for (size_t k = 0; in_order && k < g_hits.size(); ++k) in_order = g_hits[k] == base + static_cast<int>(k);
    check(in_order, "v2 consumer: each of its 29 calls lands on the entry v3 has at that place");
    uint32_t sizes[16] = {};
    const uint32_t n = v2_struct_sizes(sizes, 16);
    const uint32_t now[] = {sizeof(KilnProgramDesc), sizeof(KilnProgramInfo), sizeof(KilnNames), sizeof(KilnTransform), sizeof(KilnVec3),
                            sizeof(KilnTeleportRequest), sizeof(KilnTeleportResult), sizeof(KilnWorldStatus)};
    bool same = n == sizeof now / sizeof now[0];
    for (uint32_t k = 0; same && k < n; ++k) same = sizes[k] == now[k];
    check(same, "v2 consumer: every struct it shares with v3 kept its size");
    check((v2_cap_bits() & KILN_WORLD_CAP_TRACKED_WRITES) == 0, "v2 consumer: the new capability bit is one it never knew");
    KilnWorldApi small = t;
    small.header.size = v2_table_size();
    check(small.header.size < offsetof(KilnWorldApi, write_submit) + sizeof(void*), "a v3 consumer's size check refuses a v2 table's write entries");
}

// tests/abi.json: every field of the v3 header, in order, at its offset; the managed mirrors are checked against the
// same file.
void abi(const nlohmann::json& fx) {
    nlohmann::json now;
    auto list = [&](const char* name, size_t size) -> nlohmann::json& {
        now[name]["size"] = size;
        now[name]["fields"] = nlohmann::json::array();
        return now[name]["fields"];
    };
#define F(S, f) l.push_back(nlohmann::json::array({#f, offsetof(S, f)}))
    {
        auto& l = list("KilnWorldApi", sizeof(KilnWorldApi));
        F(KilnWorldApi, header); F(KilnWorldApi, register_program); F(KilnWorldApi, program_slot); F(KilnWorldApi, program_info);
        F(KilnWorldApi, iter_begin); F(KilnWorldApi, iter_next); F(KilnWorldApi, current_entity); F(KilnWorldApi, command_buffer);
        F(KilnWorldApi, queue_push); F(KilnWorldApi, queue_add); F(KilnWorldApi, queue_remove); F(KilnWorldApi, mark_written);
        F(KilnWorldApi, read_other); F(KilnWorldApi, component_hash); F(KilnWorldApi, component); F(KilnWorldApi, exists);
        F(KilnWorldApi, transform); F(KilnWorldApi, position); F(KilnWorldApi, find); F(KilnWorldApi, entity_count);
        F(KilnWorldApi, server_world); F(KilnWorldApi, teleport); F(KilnWorldApi, teleport_cancel); F(KilnWorldApi, status);
        F(KilnWorldApi, write_other); F(KilnWorldApi, unregister_program); F(KilnWorldApi, bind_program); F(KilnWorldApi, capabilities);
        F(KilnWorldApi, program_events); F(KilnWorldApi, program_emit); F(KilnWorldApi, write_submit); F(KilnWorldApi, write_cancel);
        F(KilnWorldApi, write_info); F(KilnWorldApi, write_status); F(KilnWorldApi, write_declare);
    }
    {
        auto& l = list("KilnWorldWriteRequest", sizeof(KilnWorldWriteRequest));
        F(KilnWorldWriteRequest, size); F(KilnWorldWriteRequest, flags); F(KilnWorldWriteRequest, entity); F(KilnWorldWriteRequest, component);
        F(KilnWorldWriteRequest, offset); F(KilnWorldWriteRequest, length); F(KilnWorldWriteRequest, data); F(KilnWorldWriteRequest, change);
        F(KilnWorldWriteRequest, callback); F(KilnWorldWriteRequest, user); F(KilnWorldWriteRequest, mod_id);
    }
    {
        auto& l = list("KilnWorldWriteResult", sizeof(KilnWorldWriteResult));
        F(KilnWorldWriteResult, size); F(KilnWorldWriteResult, status); F(KilnWorldWriteResult, ticket); F(KilnWorldWriteResult, entity);
        F(KilnWorldWriteResult, component); F(KilnWorldWriteResult, offset); F(KilnWorldWriteResult, length); F(KilnWorldWriteResult, component_size);
        F(KilnWorldWriteResult, flags); F(KilnWorldWriteResult, steps); F(KilnWorldWriteResult, writers); F(KilnWorldWriteResult, before);
        F(KilnWorldWriteResult, after); F(KilnWorldWriteResult, reason);
    }
    {
        auto& l = list("KilnWorldWriteInfo", sizeof(KilnWorldWriteInfo));
        F(KilnWorldWriteInfo, size); F(KilnWorldWriteInfo, state); F(KilnWorldWriteInfo, flags); F(KilnWorldWriteInfo, writers);
        F(KilnWorldWriteInfo, init_writers); F(KilnWorldWriteInfo, component_size); F(KilnWorldWriteInfo, writer_names);
    }
    {
        auto& l = list("KilnWorldWriteStatus", sizeof(KilnWorldWriteStatus));
        F(KilnWorldWriteStatus, size); F(KilnWorldWriteStatus, program); F(KilnWorldWriteStatus, available); F(KilnWorldWriteStatus, running);
        F(KilnWorldWriteStatus, attributes); F(KilnWorldWriteStatus, bits); F(KilnWorldWriteStatus, declared); F(KilnWorldWriteStatus, refused);
        F(KilnWorldWriteStatus, pending); F(KilnWorldWriteStatus, reserved); F(KilnWorldWriteStatus, submitted); F(KilnWorldWriteStatus, written);
        F(KilnWorldWriteStatus, unchanged); F(KilnWorldWriteStatus, failed); F(KilnWorldWriteStatus, updates);
    }
#undef F
    for (const auto& [name, layout] : now.items()) {
        const std::string what = std::string("abi.json: ") + name + " field for field";
        check(fx.contains(name) && fx.at(name) == layout, what.c_str());
    }
    check(fx.at("v2Entries").get<size_t>() * sizeof(void*) == v2_table_size(), "abi.json: the v2 table is its first v2Entries entries");
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

int main(int argc, char** argv) {
    const char* fixture = argc > 1 ? argv[1] : "parity.json";
    std::ifstream in(fixture);
    if (!in) {
        std::fprintf(stderr, "cannot read %s\n", fixture);
        return 2;
    }
    const nlohmann::json fx = nlohmann::json::parse(in);
    const std::string dir = std::string(fixture).substr(0, std::string(fixture).find_last_of('/') + 1);
    std::ifstream abi_in(dir + "abi.json");
    if (!abi_in) {
        std::fprintf(stderr, "cannot read %sabi.json\n", dir.c_str());
        return 2;
    }
    const nlohmann::json abi_fx = nlohmann::json::parse(abi_in);
    declarations();
    one_write();
    queue();
    saturation();
    identity();
    lifetime(fx);
    watchdog();
    delivery();
    deps();
    catalog();
    parity(fx);
    v2_consumer();
    abi(abi_fx);
    manifest_writes();
    std::printf("world tracked writes: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
