// SPDX-License-Identifier: MIT
// kiln.content gamedata: resource patches of a boot generation (chapter 08, S10; kiln.boot collector "patches").
//
// A mod's OnBoot (or its blocks files' "resourcePatches" section) sets fields of a game resource the game keeps what it
// read of at its start (keen::BalancingTable: a write from OnLoad changes what kiln.assets serves, not what the game's
// BalancingRegistry already holds). One document:
//   {"resource": "<GUID>", "type": "keen::BalancingTable", "part": 0, "set": {"<path>": <value>, ...}}
// path: members by name, "[i]" an element of an existing array, "[*]" every element (gamedata_walk.cpp's forms).
// value: a plain value (set), or {"mul","add","min","max","raiseOnly","lowerOnly"} on the game's own value (parse_leaf);
// bool and enum members take a plain value only.
//
// Two steps, as kiln.knowledge's collector:
//   prepare (kiln.boot's commit, before the game's entry point): the documents are read and checked against the exe's
//     reflection as far as types go (the type exists, the path names members, it ends in a scalar, the value's shape
//     fits); two mods that set one field to different values: the later mod is refused (ruling 7). The plan is frozen.
//   apply (kiln.assets when_resources_ready: the game's resource service is up and nothing has read through it): each
//     resource is copied, the plan's writes are made in the copy with the game's values as the originals, and the copy is
//     served as an override. What only the bytes can say (the resource is not there, an index past the array) is a
//     fault after the publication: kiln.boot activation_fault (recorded; a required mod stops the start).
// Nothing here waits, and no lock is held across a call into kiln.boot or kiln.assets.
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "gamedata.h"
#include "kiln_boot_collector.hpp"
#include "refs.h"
#include "services/kiln_assets.h"
#include "services/kiln_caps.h"

namespace kc {
namespace {

constexpr char kRow[] = "mods.boot.resource_patches";
constexpr char kKind[] = "resourcePatches";
constexpr size_t kMaxSets = 4096;        // field writes of one generation
constexpr size_t kMaxResource = 64u << 20;  // bytes of one patched resource

struct Set {
    std::string mod, key, path;
    nlohmann::json value;
};
struct ResPatch {
    KilnAssetKey res{};
    std::string type, guid;
    std::vector<Set> sets;  // envelope order
};
struct BootPlan {
    std::vector<ResPatch> resources;
    size_t sets = 0;
};

bool g_bound = false;       // the collector is registered (a boot profile)
bool g_ready_asked = false; // when_resources_ready is registered
const char* g_off = nullptr;  // why nothing is applied in this process (then staged patches refuse their mod)

const KilnAssetsApi* assets() {
    const auto* a = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1));
    return a && a->header.size >= offsetof(KilnAssetsApi, when_resources_ready) + sizeof(void*) && a->when_resources_ready && a->resource_data &&
                   a->resource_put
               ? a
               : nullptr;
}

void row(uint32_t state, uint32_t reason, const std::string& text) {
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    if (caps && caps->report) caps->report(g_owner, KilnStr{kRow, sizeof(kRow) - 1}, state, reason, KilnStr{text.data(), text.size()});
}

bool is_op(const nlohmann::json& v) { return v.is_object(); }

// One path segment: a member name, or an index ("3", "*").
struct Seg {
    std::string s;
    bool index = false;
};
bool segments(const std::string& path, std::vector<Seg>* out, std::string* err) {
    out->clear();
    std::string cur;
    auto flush = [&]() {
        if (!cur.empty()) out->push_back({cur, false});
        cur.clear();
    };
    for (size_t i = 0; i < path.size(); ++i) {
        const char c = path[i];
        if (c == '.') {
            if (cur.empty() && (out->empty() || !out->back().index)) return *err = "an empty member name in \"" + path + "\"", false;
            flush();
        } else if (c == '[') {
            flush();
            const size_t end = path.find(']', i);
            if (end == std::string::npos) return *err = "\"[\" without \"]\" in \"" + path + "\"", false;
            const std::string in = path.substr(i + 1, end - i - 1);
            if (in != "*" && (in.empty() || in.size() > 9 || !std::all_of(in.begin(), in.end(), [](char d) { return d >= '0' && d <= '9'; })))
                return *err = "\"[" + in + "]\" in \"" + path + "\": an index is a number or *", false;
            out->push_back({in, true});
            i = end;
        } else {
            cur += c;
        }
    }
    flush();
    if (out->empty()) return *err = "an empty path", false;
    return true;
}

// The path against the types alone. true: it ends in `leaf` (checked), or *unknown (a variant or optional on the way:
// only the bytes can say). false + *err: no such member, not a list, not a scalar.
bool check_types(const gd::TType* root, const std::string& path, Field* leaf, bool* unknown, std::string* err) {
    *unknown = false;
    std::vector<Seg> segs;
    if (!segments(path, &segs, err)) return false;
    const gd::TType* t = root->resolved();
    std::string at = root->brief;
    for (const Seg& s : segs) {
        if (!t) return *unknown = true, true;
        if (s.index) {
            if (t->kind != gd::K_ARRAY && t->kind != gd::K_BLOBARRAY) return *err = at + " is not a list", false;
            t = t->inner() ? t->inner()->resolved() : nullptr;
            at += "[" + s.s + "]";
            continue;
        }
        if (t->kind != gd::K_STRUCT) return *unknown = true, true;  // a variant, an optional, a config list: the walker's business
        const gd::TField* f = t->field(s.s);
        if (!f) return *err = at + " has no member " + s.s, false;
        t = f->type() ? f->type()->resolved() : nullptr;
        at += "." + f->name;
    }
    if (!t) return *unknown = true, true;
    if (t->kind == gd::K_BLOBOPT || t->kind == gd::K_BLOBVAR) return *unknown = true, true;
    if (!gd::leaf_of(t, leaf) || leaf->ref || leaf->vt == KILN_CV_GUID)
        return *err = "NotScalar: " + at + " is " + t->name + ", not a number, a bool or an enum (resource patches set scalar members)", false;
    return true;
}

// The value's shape for a leaf: an operation object only on numbers.
bool check_value(const Field& f, const nlohmann::json& v, std::string* err) {
    if (is_op(v)) {
        if (f.vt == KILN_CV_BOOL || f.enum_kind != KILN_CONTENT_PLAIN)
            return *err = "OperationOnNonNumber: a bool or enum member takes a plain value, not {mul, add, min, max}", false;
        static const char* const known[] = {"mul", "add", "min", "max", "raiseOnly", "lowerOnly", "set"};
        for (const auto& [k, x] : v.items()) {
            if (std::none_of(std::begin(known), std::end(known), [&](const char* n) { return k == n; }))
                return *err = "unknown operation \"" + k + "\" (mul, add, min, max, raiseOnly, lowerOnly, set)", false;
            if ((k == "raiseOnly" || k == "lowerOnly") ? !x.is_boolean() : (k != "set" && !x.is_number()))
                return *err = "\"" + k + "\" has the wrong kind of value", false;
        }
        if (v.empty()) return *err = "an empty operation object", false;
        return true;
    }
    if (v.is_boolean() || v.is_number() || v.is_string() || (v.is_array() && f.enum_kind == KILN_CONTENT_FLAGS)) return true;
    return *err = "unsupported value " + v.dump(), false;
}

std::string base_of(const std::string& path) {  // the path without its indices
    std::string b;
    for (size_t i = 0; i < path.size(); ++i) {
        if (path[i] == '[') {
            const size_t e = path.find(']', i);
            if (e == std::string::npos) break;
            i = e;
        } else {
            b += path[i];
        }
    }
    return b;
}
std::string index_of(const std::string& path) {  // its indices, in order
    std::string x;
    for (size_t i = 0; i < path.size(); ++i)
        if (path[i] == '[') {
            const size_t e = path.find(']', i);
            if (e == std::string::npos) break;
            x += path.substr(i, e - i + 1);
            i = e;
        }
    return x;
}
bool overlap(const std::string& a, const std::string& b) { return a == b || a.find('*') != std::string::npos || b.find('*') != std::string::npos; }

// The "resourcePatches" sections of the mods' blocks files, as documents, in load order (files by name within a mod).
// A file that is not JSON is another reader's to report (Kiln.Blocks reads the same files).
std::vector<kiln::boot::Doc> file_docs(const kiln::boot::Input* boot) {
    namespace fs = std::filesystem;
    std::vector<kiln::boot::Doc> out;
    for (const fs::path& dir : mod_folder_list()) {
        std::error_code ec;
        if (!fs::is_directory(dir / L"blocks", ec)) continue;
        const std::string id = kiln::boot::mod_id_of(dir);
        if (id.empty() || (boot && boot->out(id))) continue;
        std::vector<fs::path> files;
        for (auto it = fs::directory_iterator(dir / L"blocks", ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->path().extension() == L".json") files.push_back(it->path());
        std::sort(files.begin(), files.end());
        for (const fs::path& f : files) {
            std::ifstream in(f, std::ios::binary);
            if (!in) continue;
            const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
            if (text.find("resourcePatches") == std::string::npos) continue;
            const nlohmann::json j = nlohmann::json::parse(text, nullptr, false, true);
            if (!j.is_object() || !j.contains(kKind)) continue;
            if (!j[kKind].is_object()) {
                out.push_back({id, "", nlohmann::json()});  // refused by its shape, with the mod named
                continue;
            }
            for (const auto& [key, doc] : j[kKind].items()) out.push_back({id, key, doc});
        }
    }
    return out;
}

void KILN_CALL destroy_plan(const void* p) { delete static_cast<const BootPlan*>(p); }

KilnResult KILN_CALL prepare_boot(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.content: the boot envelope is not JSON");
    auto plan = std::make_unique<BootPlan>();
    const bool types = gd::type_named("keen::ItemInfo") != nullptr;  // the exe's reflection is readable before the entry point
    struct Claim {
        std::string index, mod, value;
    };
    std::map<std::string, std::vector<Claim>> claims;  // "<resource key>|<path without indices>"
    // each mod's files first, then what its OnBoot staged; a key both have stays the file's
    std::vector<kiln::boot::Doc> docs = file_docs(&input);
    {
        std::set<std::string> have;
        for (const auto& d : docs) have.insert(d.owner + "/" + d.key);
        for (auto& d : input.docs("patches")) {
            if (!have.insert(d.owner + "/" + d.key).second) {
                LOGI("boot: resource patch %s/%s: the mod's blocks file has this key too: the file's stays", d.owner.c_str(), d.key.c_str());
                continue;
            }
            docs.push_back(std::move(d));
        }
    }
    std::set<std::string> not_boot;
    for (const auto& d : docs) {
        // a mod that takes no part in the generation (no "boot" in its kiln.mod.json): its patches are not applied
        if (!input.owner.count(d.owner)) {
            if (not_boot.insert(d.owner).second)
                LOGW("mod %s has \"resourcePatches\" in a blocks file and takes no part in the boot generation: they are not applied. Add \"boot\": true "
                     "to its kiln.mod.json",
                     d.owner.c_str());
            continue;
        }
        auto refused = [&](const std::string& path, const std::string& why) {
            return kiln::boot::refuse(out, issue, d.owner, kKind, d.key, (path.empty() ? std::string() : "\"" + path + "\": ") + why);
        };
        if (g_off) return refused("", std::string("resource patches are not applied here: ") + g_off);
        const nlohmann::json& doc = d.document;
        if (!doc.is_object() || !doc.contains("resource") || !doc["resource"].is_string() || !doc.contains("type") || !doc["type"].is_string() ||
            !doc.contains("set") || !doc["set"].is_object() || doc["set"].empty())
            return refused("", "a resource patch is {\"resource\": \"<GUID>\", \"type\": \"<type name>\", \"set\": {\"<path>\": <value>}}");
        ResPatch rp;
        rp.guid = doc["resource"].get<std::string>();
        rp.type = doc["type"].get<std::string>();
        if (!guid_parse(rp.guid, rp.res.guid)) return refused("", "\"resource\": \"" + rp.guid + "\" is not a GUID");
        if (doc.contains("part") && !doc["part"].is_number_unsigned()) return refused("", "\"part\" is a number from 0");
        rp.res.part = doc.value("part", 0u);
        rp.res.type = fnv1a(rp.type);
        const gd::TType* type = types ? gd::type_named(rp.type) : nullptr;
        if (types && (!type || !type->resolved() || type->resolved()->kind != gd::K_STRUCT))
            return refused("", "UnknownType: this game build has no struct type \"" + rp.type + "\" (the qualified name, as keen::BalancingTable)");
        std::string res_key = guid_text(rp.res.guid) + "/" + rp.type + "/" + std::to_string(rp.res.part);
        for (const auto& [path, value] : doc["set"].items()) {
            if (plan->sets >= kMaxSets) return refused(path, "more than " + std::to_string(kMaxSets) + " field writes in one boot generation");
            std::string err;
            std::vector<Seg> segs;
            if (!segments(path, &segs, &err)) return refused(path, err);
            Field leaf;
            bool unknown = !types;
            if (types && !check_types(type, path, &leaf, &unknown, &err)) return refused(path, err);
            if (!unknown && !check_value(leaf, value, &err)) return refused(path, err);
            if (unknown && is_op(value) && value.empty()) return refused(path, "an empty operation object");
            // two mods, one field
            auto& list = claims[res_key + "|" + base_of(path)];
            const std::string ix = index_of(path), dump = value.dump();
            for (const Claim& c : list)
                if (c.mod != d.owner && overlap(c.index, ix) && (c.value != dump || c.index != ix || is_op(value)))
                    return refused(path, "Conflict: mod " + c.mod + " sets this field of " + res_key + " too (" + c.value +
                                             "); two mods on one field are not combined");
            list.push_back({ix, d.owner, dump});
            rp.sets.push_back({d.owner, d.key, path, value});
            ++plan->sets;
        }
        auto same = std::find_if(plan->resources.begin(), plan->resources.end(), [&](const ResPatch& o) {
            return std::memcmp(&o.res, &rp.res, sizeof rp.res) == 0;
        });
        if (same == plan->resources.end()) plan->resources.push_back(std::move(rp));
        else same->sets.insert(same->sets.end(), std::make_move_iterator(rp.sets.begin()), std::make_move_iterator(rp.sets.end()));
    }
    LOGI("boot generation %llu prepared: %zu resource patch write(s) on %zu resource(s)%s", static_cast<unsigned long long>(input.generation),
         plan->sets, plan->resources.size(), types || !plan->sets ? "" : " (types are checked when they are applied)");
    return kiln::boot::prepared(out, plan.release(), &destroy_plan);
}

// kiln.assets when_resources_ready: the game's resource service is up, nothing has read through it yet.
void KILN_CALL apply(void*) {
    const auto* plan = static_cast<const BootPlan*>(kiln::boot::published(g_api, "patches"));
    if (!plan || plan->resources.empty()) return;
    const KilnAssetsApi* A = assets();
    size_t written = 0, failed = 0;
    // a fault names its mod: for a required mod kiln.boot ends the process inside the call
    auto fault = [&](const Set& s, const std::string& why) {
        ++failed;
        LOGE("boot: resource patch %s/%s \"%s\": %s", s.mod.c_str(), s.key.c_str(), s.path.c_str(), why.c_str());
        kiln::boot::activation_fault(g_api, g_owner, s.mod, kKind, s.key, "\"" + s.path + "\": " + why);
    };
    for (const ResPatch& rp : plan->resources) {
        const std::string what = rp.type + " " + guid_text(rp.res.guid);
        const void* data = nullptr;
        size_t len = 0;
        const gd::TType* type = gd::type_named(rp.type);
        std::string whole;
        if (!A) whole = "kiln.assets' resources are not available";
        else if (!type || !type->resolved() || type->resolved()->kind != gd::K_STRUCT) whole = "UnknownType: this game build has no struct type \"" + rp.type + "\"";
        else if (A->resource_data(&rp.res, &data, &len) != KILN_OK || !data) whole = "MissingResource: this game build has no " + what;
        else if (len < type->resolved()->size || len > kMaxResource)
            whole = "the resource " + what + " is " + std::to_string(len) + " bytes, which is not a " + rp.type;
        if (!whole.empty()) {
            std::set<std::string> said;
            for (const Set& s : rp.sets)
                if (said.insert(s.mod + "/" + s.key).second) fault(s, whole);
                else ++failed;
            continue;
        }
        std::vector<uint8_t> copy(static_cast<const uint8_t*>(data), static_cast<const uint8_t*>(data) + len);
        const uintptr_t lo = reinterpret_cast<uintptr_t>(copy.data()), hi = lo + copy.size();
        size_t here = 0;
        for (const Set& s : rp.sets) {
            std::vector<gd::Resolved> found;
            std::string err;
            if (!gd::resolve(gd::Root::Plain, gd::Node{type->resolved(), lo}, s.path, &found, &err)) {
                fault(s, err.empty() ? "no such member in " + what : err);
                continue;
            }
            if (found.empty()) {
                if (s.path.find('*') != std::string::npos) LOGI("boot: resource patch %s/%s \"%s\": the list is empty in %s: nothing to change", s.mod.c_str(), s.key.c_str(), s.path.c_str(), what.c_str());
                else fault(s, err.empty() ? "IndexPastEnd: no such element in " + what : err);
                continue;
            }
            // every element is computed before any is written: a path that fails changes nothing
            std::vector<std::pair<uintptr_t, std::vector<uint8_t>>> writes;
            std::string bad;
            for (const gd::Resolved& r : found) {
                Field f;
                if (!r.node.t || !gd::leaf_of(r.node.t, &f) || f.ref || f.vt == KILN_CV_GUID) {
                    bad = "NotScalar: " + r.path + " is not a number, a bool or an enum";
                    break;
                }
                if (!check_value(f, s.value, &bad)) break;
                if (r.node.addr < lo || r.node.addr + f.bytes > hi) {
                    bad = r.path + " lies outside the resource's own bytes";
                    break;
                }
                uint8_t out[16] = {};
                bool reset = false;
                if (!gd::parse_leaf(f, gjson::parse(s.value.dump()), reinterpret_cast<const uint8_t*>(r.node.addr), out, &reset, &bad)) break;
                if (reset) continue;  // {"reset": true}: the game's own value stays
                writes.emplace_back(r.node.addr, std::vector<uint8_t>(out, out + f.bytes));
            }
            if (!bad.empty()) {
                fault(s, bad);
                continue;
            }
            for (const auto& [addr, bytes] : writes) std::memcpy(reinterpret_cast<void*>(addr), bytes.data(), bytes.size());
            here += writes.size();
            LOGI("boot: resource patch %s/%s: %s \"%s\" = %s (%zu value(s))", s.mod.c_str(), s.key.c_str(), what.c_str(), s.path.c_str(),
                 s.value.dump().c_str(), writes.size());
        }
        if (!here) continue;
        const KilnResult rc = A->resource_put(g_owner, &rp.res, copy.data(), copy.size(), KILN_ASSET_PUT_OVERRIDE);
        if (rc != KILN_OK) {
            std::set<std::string> said;
            for (const Set& s : rp.sets)
                if (said.insert(s.mod + "/" + s.key).second) fault(s, "kiln.assets refused the patched " + what + " (" + std::to_string(rc) + ")");
            continue;
        }
        written += here;
    }
    LOGI("boot: resource patches applied before the game read its resources: %zu value(s) in %zu resource(s)%s", written, plan->resources.size(),
         failed ? (", " + std::to_string(failed) + " write(s) not applied (boot.json)").c_str() : "");
    if (failed) row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, std::to_string(failed) + " write(s) of this start were not applied (state/boot.json)");
}

}  // namespace

// kiln.content's bind (content.cpp, after gd_boot): the collector of a boot profile and the row.
void gd_boot_patches_bind() {
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    const KilnAssetsApi* A = assets();
    if (!A) g_off = "kiln.assets' resources are off on this build";
    else if (kiln_caps_held(caps, KilnStr{kRow, sizeof(kRow) - 1})) g_off = "mods.boot.resource_patches is held until the client checks pass";
    g_bound = kiln::boot::collect(g_api, g_owner, "patches", &prepare_boot, nullptr);
    if (g_bound && !g_off) g_ready_asked = A->when_resources_ready(&apply, nullptr) == KILN_OK;
    if (g_bound && !g_off && !g_ready_asked) g_off = "kiln.assets' resources are off on this build";
    if (g_off && !kiln_caps_held(caps, KilnStr{kRow, sizeof(kRow) - 1}))
        row(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING, g_off);
    else if (!g_off)
        row(g_bound ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE, g_bound ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_POLICY,
            g_bound ? "" : "no boot phase in this process (no enabled mod has \"boot\" in its kiln.mod.json)");
    if (!g_bound) {
        // no boot phase: a blocks file's patches would be read by nobody, so say it once per mod
        std::set<std::string> said;
        try {
            for (const auto& d : file_docs(nullptr))
                if (said.insert(d.owner).second)
                    LOGW("mod %s has \"resourcePatches\" in a blocks file, and this start has no boot phase: they are not applied. Add \"boot\": true to "
                         "its kiln.mod.json",
                         d.owner.c_str());
        } catch (const std::exception&) {
        }
    }
    if (g_bound) LOGI("boot profile: resource patches are applied when the game's resource service is up%s%s", g_off ? "; off: " : "", g_off ? g_off : "");
}

}  // namespace kc
