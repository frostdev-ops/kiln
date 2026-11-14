// SPDX-License-Identifier: MIT
// The collector side of kiln.boot (services/kiln_boot.h), header-only, for the native collectors (assets,
// worldtables, content, skills, knowledge, vfx). C++17, nlohmann::json (the services' third_party).
//
// A collector's three parts:
//   bind      kiln_plugin_main / on_ready, unchanged timing: hooks, tables, callbacks. collect() registers the prepare
//             function; it says true only in a boot profile, and then the collector must not load its definitions in
//             on_ready. False: no boot profile (or no kiln.boot): the old on_ready path runs, untouched.
//   prepare   called by kiln.boot's commit, on the boot thread, before the game's entry point: reads the mods' files
//             and the IR envelope's documents of this collector, leaving out every excluded mod; returns a plan or a
//             refusal (refuse()). It may not wait for any readiness (when_resources_ready, a world): the game is held.
//   consumer  the hooks the game reaches after the entry point use what was prepared (published() says it is there).
//
// Documents. The envelope's collectors.<id> entries are {owner, key, document}; each collector reads `document` in the
// JSON its own files (or the cache of code definitions) already use, as its file says. Precedence for one identity:
// the mod's data file > its baked/generated file > its OnBoot code (these documents) > the pending cache of earlier
// runs: a collector reads them in that order and keeps the first.
#pragma once
#include <algorithm>
#include <cstddef>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "nlohmann/json.hpp"
#include "services/kiln_boot.h"

namespace kiln::boot {

// The kiln.boot table, or nullptr (no such service: an older core, safe idle).
inline const KilnBootApiV1* table(const KilnApi* api) {
    const auto* t = static_cast<const KilnBootApiV1*>(api->service(KilnStr{KILN_BOOT_SERVICE, sizeof(KILN_BOOT_SERVICE) - 1}, 1));
    // the v1 table up to active_plan; what was appended later is asked for by its own offset
    return t && t->header.size >= offsetof(KilnBootApiV1, active_plan) + sizeof(void*) ? t : nullptr;
}

// Bind: registers `fn` as collector `id`. True: this process has a boot generation and fn will prepare it.
inline bool collect(const KilnApi* api, KilnOwner owner, const char* id, KilnBootPrepareFnV1 fn, void* user) {
    const KilnBootApiV1* b = table(api);
    return b && b->collector(owner, KilnStr{id, std::strlen(id)}, fn, user) == KILN_OK;
}

// The plan kiln.boot published for `id`; nullptr before the publication or without a boot generation.
inline const void* published(const KilnApi* api, const char* id, uint64_t* generation = nullptr) {
    const KilnBootApiV1* b = table(api);
    return b ? b->active_plan(KilnStr{id, std::strlen(id)}, generation) : nullptr;
}

// Reports that published content of `mod` could not be applied (kiln.boot activation_fault). For a mod whose boot
// content is required this does not return: the start stops. false: this kiln.boot has no such call.
inline bool activation_fault(const KilnApi* api, KilnOwner collector, const std::string& mod, const std::string& kind,
                             const std::string& key, const std::string& reason) {
    const KilnBootApiV1* b = table(api);
    if (!b || b->header.size < offsetof(KilnBootApiV1, activation_fault) + sizeof(void*) || !b->activation_fault) return false;
    return b->activation_fault(collector, KilnStr{mod.data(), mod.size()}, KilnStr{kind.data(), kind.size()},
                               KilnStr{key.data(), key.size()}, KilnStr{reason.data(), reason.size()}) == KILN_OK;
}

struct Doc {
    std::string owner, key;
    nlohmann::json document;
};

// The prepare input, read once.
struct Input {
    bool ok = false;  // the envelope parsed (it always does: kiln.boot checked it)
    uint64_t generation = 0;
    nlohmann::json ir;
    std::set<std::string> excluded;          // mods whose definitions and files stay out
    std::map<std::string, KilnOwner> owner;  // participant -> its process owner "boot:<id>"

    bool out(const std::string& mod) const { return excluded.count(mod) != 0; }
    std::vector<Doc> docs(const char* collector) const {
        std::vector<Doc> v;
        if (!ir.is_object() || !ir.contains("collectors") || !ir["collectors"].is_object() || !ir["collectors"].contains(collector))
            return v;
        for (const auto& d : ir["collectors"][collector]) {
            if (!d.is_object() || !d.contains("owner") || !d["owner"].is_string()) continue;
            Doc doc{d["owner"].get<std::string>(), d.value("key", std::string()), d.contains("document") ? d["document"] : nlohmann::json()};
            if (!out(doc.owner)) v.push_back(std::move(doc));
        }
        return v;
    }
    // Documents of one mod, in envelope order.
    std::vector<Doc> docs(const char* collector, const std::string& mod) const {
        std::vector<Doc> all = docs(collector), v;
        for (auto& d : all)
            if (d.owner == mod) v.push_back(std::move(d));
        return v;
    }
};

inline Input parse(const KilnBootPrepareInputV1* in) {
    Input r;
    if (!in || in->size < sizeof(KilnBootPrepareInputV1)) return r;
    r.generation = in->generation;
    const auto& s = in->canonical_ir_json;
    r.ir = nlohmann::json::parse(s.ptr ? std::string(s.ptr, s.len) : std::string("{}"), nullptr, false);
    if (!r.ir.is_object()) return r;
    if (r.ir.contains("excluded") && r.ir["excluded"].is_array())
        for (const auto& e : r.ir["excluded"])
            if (e.is_string()) r.excluded.insert(e.get<std::string>());
    if (r.ir.contains("owners") && r.ir["owners"].is_array())
        for (const auto& o : r.ir["owners"])
            if (o.is_object() && o.contains("id") && o["id"].is_string())
                r.owner[o["id"].get<std::string>()] = o.contains("owner") && o["owner"].is_number_unsigned() ? o["owner"].get<KilnOwner>() : 0;
    r.ok = true;
    return r;
}

// Thrown by a collector's reading code for a document it cannot read; its prepare turns it into a refusal of that mod (refuse()).
struct Refusal : std::runtime_error {
    std::string mod, kind, key;
    Refusal(std::string m, std::string k, std::string ky, const std::string& why)
        : std::runtime_error(why), mod(std::move(m)), kind(std::move(k)), key(std::move(ky)) {}
};

template <size_t N>
inline void put(char (&dst)[N], const std::string& s) {
    size_t n = std::min(s.size(), N - 1);
    if (n < s.size())
        while (n > 0 && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;
    std::memcpy(dst, s.data(), n);
    dst[n] = 0;
}

// A prepared plan (non-null; an empty plan is a pointer to something that holds nothing).
inline KilnResult prepared(KilnBootPreparedV1* out, const void* plan, void(KILN_CALL* destroy)(const void*)) {
    out->ok = 1;
    out->plan = plan;
    out->destroy = destroy;
    return KILN_OK;
}

// A refusal. `mod`: the participant it is about (empty: none; kiln.boot then aborts the generation).
inline KilnResult refuse(KilnBootPreparedV1* out, KilnBootIssueV1* issue, const std::string& mod, const std::string& kind,
                         const std::string& key, const std::string& reason, uint32_t code = KILN_BOOT_ISSUE_PREPARE_REFUSED) {
    out->ok = 0;
    out->plan = nullptr;
    out->destroy = nullptr;
    issue->code = code;
    put(issue->mod_id, mod);
    put(issue->kind, kind);
    put(issue->key, key);
    put(issue->phase, std::string("prepare"));
    put(issue->reason, reason);
    return KILN_OK;
}

// kiln.mod.json's "id" of a mod folder; empty when it has none.
inline std::string mod_id_of(const std::filesystem::path& dir) {
    std::ifstream f(dir / L"kiln.mod.json", std::ios::binary);
    if (!f) return {};
    std::string s((std::istreambuf_iterator<char>(f)), {});
    const nlohmann::json j = nlohmann::json::parse(s, nullptr, false, true);
    return j.is_object() && j.contains("id") && j["id"].is_string() ? j["id"].get<std::string>() : std::string();
}

}  // namespace kiln::boot
