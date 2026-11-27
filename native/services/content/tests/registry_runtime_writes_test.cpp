// SPDX-License-Identifier: MIT
// kiln.content registry, host test: naming run-time writers from kiln.assets' write records (registry/runtime_writes.h, sfi
// R3): the records placed in memory, and the proof that names a field's writers only when taking their recorded writes back
// gives what Kiln expected there. registry_runtime_writes_test.sh builds and runs it.
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

#include "../registry/runtime_writes.h"

namespace {
using namespace kc::registry;
int checks = 0;
void check(bool yes, const std::string& name) {
    if (!yes) throw std::runtime_error(name);
    ++checks;
}

alignas(16) uint8_t g_mem[256];  // two made-up resources: A at 0, B at 128
uintptr_t at(size_t off) { return reinterpret_cast<uintptr_t>(g_mem) + off; }
const KilnAssetKey KA = {{1}, 0xb5ce8765u, 0}, KB = {{2}, 0xb5ce8765u, 0}, KC = {{3}, 0xb5ce8765u, 0};

RuntimeWrite write(size_t off, uint8_t width, uint64_t before, uint64_t after, const char* writer, uint64_t id) {
    RuntimeWrite w;
    w.addr = at(off), w.width = width, w.writer = writer, w.id = id;
    std::memcpy(w.before, &before, 8);
    std::memcpy(w.after, &after, 8);
    return w;
}
RuntimeBytes u32(uint32_t v) {
    RuntimeBytes b{};
    std::memcpy(b.data(), &v, 4);
    return b;
}
uint32_t as_u32(const RuntimeBytes& b) {
    uint32_t v = 0;
    std::memcpy(&v, b.data(), 4);
    return v;
}
// what Kiln expected: these u32 values, slot by slot
std::function<bool(const std::vector<RuntimeBytes>&)> is(std::vector<uint32_t> want) {
    return [want](const std::vector<RuntimeBytes>& b) {
        if (b.size() != want.size()) return false;
        for (size_t i = 0; i < b.size(); ++i)
            if (as_u32(b[i]) != want[i]) return false;
        return true;
    };
}

KilnLoadedWriteRecord record(const KilnAssetKey& key, uint64_t offset, uint32_t width, uint64_t before, uint64_t after, const char* writer,
                             uint64_t id) {
    KilnLoadedWriteRecord r{};
    r.size = sizeof r, r.width = width, r.id = id, r.owner = 7, r.session = 77, r.key = key, r.offset = offset;
    std::memcpy(r.before, &before, 8);
    std::memcpy(r.after, &after, 8);
    std::snprintf(r.writer, sizeof r.writer, "%s", writer);
    std::snprintf(r.field, sizeof r.field, "maxStackSize");
    return r;
}

// The records placed where their resources' bytes live now: once per key, a scalar outside the resource or a key the game
// does not have left out, the writer's text kept.
void placed_in_memory() {
    int asked = 0;
    const auto resident = [&](const KilnAssetKey& key, uintptr_t* a, size_t* len) {
        ++asked;
        if (key.guid[0] == 1) return *a = at(0), *len = 64, true;
        if (key.guid[0] == 2) return *a = at(128), *len = 16, true;
        return false;
    };
    std::vector<KilnLoadedWriteRecord> records = {record(KA, 4, 4, 10, 40, "shroudforge:a", 1), record(KA, 8, 4, 1, 2, "", 2),
                                                  record(KB, 12, 4, 1, 2, "shroudforge:b", 3), record(KB, 16, 4, 1, 2, "x", 4),
                                                  record(KC, 0, 4, 1, 2, "x", 5), record(KA, 0, 3, 1, 2, "x", 6)};
    const auto w = place_runtime_writes(records, resident);
    check(asked == 3, "one resource_data per key (" + std::to_string(asked) + ")");
    check(w.size() == 3, "outside the resource, an unknown key, a width of 3: left out (" + std::to_string(w.size()) + ")");
    check(w[0].addr == at(4) && w[0].width == 4 && w[0].writer == "shroudforge:a" && w[0].id == 1 && w[0].before[0] == 10 && w[0].after[0] == 40,
          "placed at its resource's bytes + offset, with its values and writer");
    check(w[1].addr == at(8) && w[1].writer.empty(), "an unnamed write keeps no name");
    check(w[2].addr == at(140) && w[2].writer == "shroudforge:b", "another resource's base");
    KilnLoadedWriteRecord full = record(KA, 0, 4, 0, 0, "", 9);
    std::memset(full.writer, 'w', sizeof full.writer);  // not NUL-terminated: cut at the array (and no package's name: none)
    check(place_runtime_writes({full}, resident)[0].writer.empty(), "a writer without its NUL stays inside its array, and names nobody");
}

// review-j3: kiln.assets keeps what a caller says its writer is ("not a security claim"): the registry names only what
// kiln-sfi names, a ShroudForge package ("shroudforge:" and a plain id). Any other text (another caller's, a kind Kiln does
// not show, a sentence) names nobody, so the field keeps its best-effort drift row rather than a wrong name.
void only_package_names() {
    const auto resident = [&](const KilnAssetKey&, uintptr_t* a, size_t* len) { return *a = at(0), *len = 64, true; };
    std::vector<KilnLoadedWriteRecord> records = {record(KA, 0, 4, 1, 2, "shroudforge:Better_Stacks-2.0", 1), record(KA, 4, 4, 1, 2, "Kiln", 2),
                                                  record(KA, 8, 4, 1, 2, "shroudforge:", 3), record(KA, 12, 4, 1, 2, "shroudforge:two words", 4),
                                                  record(KA, 16, 4, 1, 2, "eml:more-recipes", 5), record(KA, 20, 4, 1, 2, "shroudforge:a/b", 6),
                                                  record(KA, 24, 4, 1, 2, "kiln-eml:unattributed", 7)};
    const auto w = place_runtime_writes(records, resident);
    check(w.size() == 7 && w[0].writer == "shroudforge:Better_Stacks-2.0", "a package's name is kept");
    for (size_t i = 1; i < w.size(); ++i) check(w[i].writer.empty(), "not a ShroudForge package's name: names nobody (" + std::to_string(i) + ")");
    check(RuntimeWrites({w[1]}).explain({{at(4), 4}}, {u32(2)}, is({1})).empty(), "and the field is not named from it");
}

// A scalar slot: Kiln wrote 40, a package wrote 777 over it: named. Not named when the record names nobody, when something
// wrote over it since, when the package wrote before Kiln, or when a newer unnamed write covers it.
void one_scalar() {
    const std::vector<RuntimeSlot> slot = {{at(4), 4}};
    const std::vector<RuntimeBytes> held = {u32(777)};
    auto steps = RuntimeWrites({write(4, 4, 40, 777, "shroudforge:big", 3)}).explain(slot, held, is({40}));
    check(steps.size() == 1 && steps[0].writer == "shroudforge:big" && as_u32(steps[0].bytes[0]) == 777, "named over Kiln's 40");
    check(RuntimeWrites({write(4, 4, 40, 777, "", 3)}).explain(slot, held, is({40})).empty(), "an unnamed write (an older kiln-sfi)");
    check(RuntimeWrites({write(4, 4, 40, 700, "shroudforge:big", 3)}).explain(slot, held, is({40})).empty(), "something wrote 777 over its 700");
    check(RuntimeWrites({write(4, 4, 10, 777, "shroudforge:big", 3)}).explain(slot, held, is({40})).empty(),
          "it wrote over 10, not over Kiln's 40 (an unrecorded write of 777 came after Kiln)");
    check(RuntimeWrites({write(4, 4, 40, 777, "shroudforge:big", 3), write(4, 4, 777, 777, "", 4)}).explain(slot, held, is({40})).empty(),
          "the place's newest write named nobody");
    check(RuntimeWrites({write(4, 4, 40, 40, "shroudforge:big", 3)}).explain({{at(4), 4}}, {u32(40)}, is({40})).empty(),
          "a write that changed nothing names nobody");
    check(RuntimeWrites().explain(slot, held, is({40})).empty() && RuntimeWrites({write(64, 4, 40, 777, "s", 1)}).explain(slot, held, is({40})).empty(),
          "no record, or none at the slot");
    // partial bytes: a value that only shares bytes with what the write left is not its
    check(RuntimeWrites({write(4, 4, 0x28, 0x0309, "shroudforge:big", 3)}).explain(slot, {u32(0x0328)}, is({0x28})).empty(),
          "0x0328 is not the write's 0x0309, though one byte is");
}

// Two slots of one field (a formula's inputs) written by two packages: each named, in write order, each row's bytes after its
// writes. One slot nobody explains: the field is not named.
void two_slots_two_writers() {
    const std::vector<RuntimeSlot> slots = {{at(16), 4}, {at(20), 4}};
    const RuntimeWrites w({write(20, 4, 2, 9, "shroudforge:second", 8), write(16, 4, 1, 5, "shroudforge:first", 6)});
    auto steps = w.explain(slots, {u32(5), u32(9)}, is({1, 2}));
    check(steps.size() == 2 && steps[0].writer == "shroudforge:first" && steps[1].writer == "shroudforge:second", "both, in write order");
    check(as_u32(steps[0].bytes[0]) == 5 && as_u32(steps[0].bytes[1]) == 2 && as_u32(steps[1].bytes[0]) == 5 && as_u32(steps[1].bytes[1]) == 9,
          "each step's bytes after its writer's writes");
    check(w.explain(slots, {u32(5), u32(3)}, is({1, 2})).empty(), "the second slot holds 3, which no record left");
    check(runtime_writers_text(steps) == "shroudforge:first and shroudforge:second", "the writers' text");
    check(runtime_writers_text({{"a", {}}, {"b", {}}, {"c", {}}}) == "a, b and c" && runtime_writers_text({{"a", {}}}) == "a", "lists");
    // the same writer on both slots: one step
    const RuntimeWrites same({write(16, 4, 1, 5, "shroudforge:one", 1), write(20, 4, 2, 9, "shroudforge:one", 2)});
    steps = same.explain(slots, {u32(5), u32(9)}, is({1, 2}));
    check(steps.size() == 1 && steps[0].writer == "shroudforge:one" && as_u32(steps[0].bytes[1]) == 9, "one writer, one step");
}

// Writes of other widths and offsets: an 8-byte write covering a 4-byte slot and the next field (only the slot's bytes are
// asked), a 1-byte write inside a slot, and a newer write covering part of an older one (the newest owns each byte).
void widths_and_overlaps() {
    auto steps = RuntimeWrites({write(32, 8, 0x0000000700000004ull, 0x0000000700000063ull, "shroudforge:wide", 2)})
                     .explain({{at(32), 4}}, {u32(0x63)}, is({4}));
    check(steps.size() == 1 && steps[0].writer == "shroudforge:wide", "an 8-byte write over a 4-byte slot");
    steps = RuntimeWrites({write(41, 1, 0x00, 0x7f, "shroudforge:byte", 2)}).explain({{at(40), 4}}, {u32(0x7f00)}, is({0}));
    check(steps.size() == 1 && as_u32(steps[0].bytes[0]) == 0x7f00, "a byte inside a slot");
    // older 4 bytes by a, newer low 2 bytes by b: b owns bytes 0-1, a bytes 2-3
    const RuntimeWrites overlap({write(48, 4, 0x00010001, 0x00020002, "shroudforge:a", 1), write(48, 2, 0x0002, 0x0003, "shroudforge:b", 2)});
    steps = overlap.explain({{at(48), 4}}, {u32(0x00020003)}, is({0x00010002}));
    check(steps.size() == 2 && steps[0].writer == "shroudforge:a" && steps[1].writer == "shroudforge:b",
          "each byte's newest write (b's run began over a's 0x0002)");
}
}  // namespace

int main() {
    const std::pair<const char*, void (*)()> tests[] = {
        {"placed_in_memory", placed_in_memory},
        {"one_scalar", one_scalar},
        {"two_slots_two_writers", two_slots_two_writers},
        {"widths_and_overlaps", widths_and_overlaps},
        {"only_package_names", only_package_names},
    };
    int failed = 0;
    for (const auto& [name, fn] : tests) {
        try {
            fn();
            std::printf("ok   %s\n", name);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("FAIL %s: %s\n", name, e.what());
        }
    }
    std::printf("registry-runtime-writes-test: %d checks, %d failed\n", checks, failed);
    return failed ? 1 : 0;
}
