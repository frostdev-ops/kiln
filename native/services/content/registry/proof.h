// SPDX-License-Identifier: MIT
// kiln.content registry stage: the lab's registry proof producer, its native half (registry P2 slice 4; kilnctl regproof,
// regmap/README.md "The proof").
//
// LAB ONLY. It runs when <data>/content/registry/proof/request.json exists (the kilnctl lab gate or a lab script writes it)
// and does nothing else otherwise: one file lookup per start, no read of the game's data, no file written.
//
// What it writes into <data>/content/registry/proof/, each file whole:
//   roundtrip-<side>-boot.json  registry-roundtrip format 1, generation "boot": every boot-routed field (and every read-only
//                               field of a registry that has boot fields) of the registry's first entry, in a boot
//                               generation made in memory on a copy of the entry's resource by the boot back-end itself
//                               (boothook::write): read through the schema, a probe written, read back, the row's members
//                               read raw through the game's reflection, and the generation composed again without the probe
//                               (the bytes are the base's again); and every read-only field of the other registries, read
//                               from the base (the first entry that reads a value), raw by the base's members (a structured
//                               value: its resource's SHA-256), never written
//   values-<side>.json          registry-values format 1: every entry of every registry read through the schema from the
//                               base (kiln.assets' live bytes at resources-ready, before Kiln's boot patches), with the
//                               base's origin and writer
// Both carry regmapDigest: of <kiln>/regmap/common/*.json, sha256 over "<file name>\n<sha256 of its bytes>\n" per file in
// name order (kilnctl sets aside lab files of another mapping).
// Nothing here writes the game: the copies are kiln.content's own memory. Live fields are proven by the lab test mod
// mods/tests/registry-proof-test through kiln.registry.live (the public mapping guard).
#pragma once
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "model.h"
#include "regmap.h"

namespace kc::registry::proof {

inline constexpr const char* kRequest = "proof/request.json";

struct Env {
    // under <data>/content/registry (stage.h Env's read_data / write_data)
    std::function<std::optional<std::string>(const std::string& rel)> read_data;
    // the file is there (one file-system lookup, nothing read; review2 N1); without it requested() falls back to read_data
    std::function<bool(const std::string& rel)> exists;
    std::function<bool(const std::string& rel, const std::string& text)> write_data;
    std::function<std::unique_ptr<model::Source>(std::string* why)> source;  // the base (live bytes at resources-ready)
    std::function<bool(const KilnAssetKey& key, std::vector<uint8_t>* out, std::string* why)> bytes;  // one resource's base bytes
    std::function<bool(map::RegMap* out, std::vector<std::string>* errs)> regmap;
    std::function<std::vector<std::pair<std::string, std::string>>()> common_files;  // (file name, bytes) of regmap/common/*.json
    const Schema* schema = nullptr;
    std::string side, exe_sha256, writer;
    std::function<void(int level, const std::string& line)> log;
};

// The request file is there (nothing else is looked at).
bool requested(const Env& e);
// The proof when requested: the rel paths written (empty: not requested, or nothing could be read).
std::vector<std::string> run(const Env& e);

// regmapDigest over (file name, bytes) pairs, in any order.
std::string regmap_digest(std::vector<std::pair<std::string, std::string>> files);
// A value of the field other than v that the schema allows (a number moved within its range, the other bool, another enum
// member, a list with its first number moved); nullopt when the kind has no probe here.
std::optional<Value> probe_for(const FieldSpec& spec, const Value& v);
// A boot back-end refusal that shows the mapping disagrees with the build (review2 S6): a member of the row's path is missing
// or not a scalar. boot_sample files it as an error sample (kilnctl: changed); other refusals are missing evidence.
bool disagreement(const std::string& why);
// One boot-generation sample of field (a registry's field, or "<list>[].<leaf>" on element 0) of entry id, whose resource
// (key, root type) has the base bytes given: a registry-roundtrip sample object, or null when the producer lacks evidence.
json boot_sample(const map::RegistryMap& reg, const RegistrySpec& rs, const std::string& field, const std::string& id, const KilnAssetKey& key,
                 const std::string& type, const std::vector<uint8_t>& base);
// One sample of a hooks registry's field (limits.h): the game's value from its code, the answer of the game's function with
// the producer's probe given to Kiln's hook for a moment, and after it is taken back; null + *why: no evidence on this build.
json hook_sample(const map::RegistryMap& reg, const RegistrySpec& rs, const std::string& field, const std::string& id, std::string* why);

}  // namespace kc::registry::proof
