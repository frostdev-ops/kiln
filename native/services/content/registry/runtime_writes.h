// SPDX-License-Identifier: MIT
// kiln.content registry: naming run-time writers (sfi R3). A ShroudForge package of kiln mode changes a loaded resource's
// scalar at run time through kiln.assets, which records the newest write of each place with the writer it named
// (kiln_assets.h loaded_write_records; kiln-sfi names "shroudforge:<package id>"). Where the game's bytes of a registry
// field differ from what Kiln expected there (a slot another writer took after Kiln's layer, a later write, the drift
// probe's value before Kiln's first write), the live back-end asks these records first: when taking the recorded writes
// back gives exactly what Kiln expected, those writers changed it, and registry.json names them per field (writer rows of
// kind "runtimeWriter", certainty "named") instead of the best-effort "outside Kiln" row. Anything else (a write no record
// explains, a record of an unnamed write, records of an older kiln.assets or kiln-sfi without names) keeps that row.
//
// Proof, per byte of the field's slots: the newest recorded write covering it is the one whose writer it is. A write is
// taken back only when every byte it owns there still holds what it left (else something later wrote over it), and then
// its bytes go back to what its run began over. The field is named only when what remains is what Kiln expected (its own
// last write, or what the stage read) and every write taken back that changed a byte named its writer.
// Header-only, plain data: host-tested (tests/registry_runtime_writes_test.cpp, and through registry_live_test.cpp).
#pragma once
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <functional>
#include <map>
#include <string>
#include <utility>
#include <vector>

#include "services/kiln_assets.h"

namespace kc::registry {

// One place's newest recorded write, where its scalar lies in game memory now.
struct RuntimeWrite {
    uintptr_t addr = 0;
    uint8_t width = 0;                      // 1, 2, 4 or 8
    uint8_t before[8] = {}, after[8] = {};  // what its run began over, what its newest write left (width bytes)
    std::string writer;                     // "shroudforge:<package id>"; empty: the write named nobody
    uint64_t id = 0;                        // kiln.assets' write id (write order)
};

// kiln.assets' records placed in game memory: resident(key, &at, &len) says where the resource's bytes live now (kiln.assets
// resource_data), asked once per key. A record whose scalar lies outside them, or that is not a record of 1, 2, 4 or 8 bytes,
// is left out.
inline std::vector<RuntimeWrite> place_runtime_writes(const std::vector<KilnLoadedWriteRecord>& records,
                                                      const std::function<bool(const KilnAssetKey&, uintptr_t*, size_t*)>& resident) {
    std::vector<RuntimeWrite> out;
    std::map<std::array<uint8_t, sizeof(KilnAssetKey)>, std::pair<uintptr_t, size_t>> bases;
    for (const KilnLoadedWriteRecord& r : records) {
        if (r.size < sizeof(KilnLoadedWriteRecord) || !(r.width == 1 || r.width == 2 || r.width == 4 || r.width == 8)) continue;
        std::array<uint8_t, sizeof(KilnAssetKey)> k;
        std::memcpy(k.data(), &r.key, k.size());
        auto it = bases.find(k);
        if (it == bases.end()) {
            uintptr_t at = 0;
            size_t len = 0;
            if (!resident || !resident(r.key, &at, &len) || !at) at = 0, len = 0;
            it = bases.emplace(k, std::make_pair(at, len)).first;
        }
        const auto [at, len] = it->second;
        if (!at || r.offset > len || r.width > len - r.offset || at > UINTPTR_MAX - r.offset) continue;
        RuntimeWrite w;
        w.addr = at + static_cast<uintptr_t>(r.offset);
        w.width = static_cast<uint8_t>(r.width);
        std::memcpy(w.before, r.before, 8);
        std::memcpy(w.after, r.after, 8);
        w.writer.assign(r.writer, strnlen(r.writer, sizeof r.writer));
        w.id = r.id;
        out.push_back(std::move(w));
    }
    return out;
}

using RuntimeBytes = std::array<uint8_t, 16>;  // a slot's bytes (kiln.content's widest slot: 16)
struct RuntimeSlot {
    uintptr_t addr;
    uint8_t size;
};
// One named writer of a field: the slots' bytes once its writes are put back over the ones before it.
struct RuntimeStep {
    std::string writer;
    std::vector<RuntimeBytes> bytes;
};

class RuntimeWrites {
public:
    RuntimeWrites() = default;
    explicit RuntimeWrites(std::vector<RuntimeWrite> writes) : writes_(std::move(writes)) {
        std::sort(writes_.begin(), writes_.end(), [](const RuntimeWrite& a, const RuntimeWrite& b) { return a.addr < b.addr; });
    }
    bool empty() const { return writes_.empty(); }

    // The named writers that explain `held` (the game's bytes of the field's slots) over what Kiln expected there:
    // `expected(undone)` says whether the bytes with the recorded writes taken back are that. The writers in the order they
    // first wrote, each with the slots' bytes after its writes; empty when the records do not explain it (see the proof
    // above).
    std::vector<RuntimeStep> explain(const std::vector<RuntimeSlot>& slots, const std::vector<RuntimeBytes>& held,
                                     const std::function<bool(const std::vector<RuntimeBytes>&)>& expected) const {
        if (writes_.empty() || slots.empty() || held.size() != slots.size() || !expected) return {};
        // the newest write covering each byte of the slots, and the bytes each write owns so
        std::map<const RuntimeWrite*, std::vector<std::pair<size_t, size_t>>> owned;
        for (size_t i = 0; i < slots.size(); ++i)
            for (size_t b = 0; b < slots[i].size && b < held[i].size(); ++b) {
                const uintptr_t a = slots[i].addr + b;
                const RuntimeWrite* newest = nullptr;
                // a write of at most 8 bytes covering a begins at most 7 bytes before it
                auto it = std::lower_bound(writes_.begin(), writes_.end(), a >= 7 ? a - 7 : 0,
                                           [](const RuntimeWrite& w, uintptr_t x) { return w.addr < x; });
                for (; it != writes_.end() && it->addr <= a; ++it)
                    if (a - it->addr < it->width && (!newest || it->id > newest->id)) newest = &*it;
                if (newest) owned[newest].push_back({i, b});
            }
        if (owned.empty()) return {};
        std::vector<RuntimeBytes> undone = held;
        std::vector<const RuntimeWrite*> taken;
        for (const auto& [w, bytes] : owned) {
            bool holds = true, changed = false;
            for (const auto& [i, b] : bytes) {
                const size_t at = slots[i].addr + b - w->addr;
                holds = holds && held[i][b] == w->after[at];
                changed = changed || w->before[at] != w->after[at];
            }
            if (!holds) continue;  // something wrote over it since: its bytes stay what the game holds
            for (const auto& [i, b] : bytes) undone[i][b] = w->before[slots[i].addr + b - w->addr];
            if (changed) taken.push_back(w);
        }
        if (taken.empty()) return {};
        for (const RuntimeWrite* w : taken)
            if (w->writer.empty()) return {};  // a write that named nobody (an older kiln-sfi): not Kiln's to name
        if (!expected(undone)) return {};
        std::sort(taken.begin(), taken.end(), [](const RuntimeWrite* a, const RuntimeWrite* b) { return a->id < b->id; });
        std::vector<std::string> order;
        for (const RuntimeWrite* w : taken)
            if (std::find(order.begin(), order.end(), w->writer) == order.end()) order.push_back(w->writer);
        std::vector<RuntimeStep> steps;
        std::vector<RuntimeBytes> bytes = undone;
        for (const std::string& writer : order) {
            for (const RuntimeWrite* w : taken)
                if (w->writer == writer)
                    for (const auto& [i, b] : owned.at(w)) bytes[i][b] = w->after[slots[i].addr + b - w->addr];
            steps.push_back({writer, bytes});
        }
        return steps;
    }

private:
    std::vector<RuntimeWrite> writes_;  // by address
};

// The writers of steps, as one name ("shroudforge:a", "shroudforge:a and shroudforge:b", "a, b and c").
inline std::string runtime_writers_text(const std::vector<RuntimeStep>& steps) {
    std::string s;
    for (size_t i = 0; i < steps.size(); ++i) s += (i == 0 ? "" : i + 1 == steps.size() ? " and " : ", ") + steps[i].writer;
    return s;
}

}  // namespace kc::registry
