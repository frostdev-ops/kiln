// SPDX-License-Identifier: MIT
// kiln.content gamedata: resource patches of a boot generation (chapter 08, S10; kiln.boot collector "patches").
//
// A mod's OnBoot (or its blocks files' "resourcePatches" section) sets fields of a game resource the game keeps what it
// read of at its start (keen::BalancingTable: a write from OnLoad changes what kiln.assets serves, not what the game's
// BalancingRegistry already holds). One document:
//   {"resource": "<GUID>", "type": "keen::BalancingTable", "part": 0, "set": {"<path>": <value>, ...}}
// and, made before the field writes (gamedata_bootstruct.cpp), in this order: "removeComponents" / "addComponents" (a
// template's components, by type name; the template's "set" paths start with a component's name), "copyComponents"
// ({"<component type>": "<template GUID>"}: a component copied whole from another template), "clear" (["<list path>"]: a
// list made empty), "replace" ({"<list path>": {"values": [...]}}: a list of scalars that holds exactly these) and
// "append" ({"<list path>": {"values": [...], "aboveLast": true}}: scalars added to the end of a list).
// path: members by name, "[i]" an element of an existing array, "[*]" every element (gamedata_walk.cpp's forms).
// value: a plain value (set), or {"mul","add","min","max","raiseOnly","lowerOnly"} on the game's own value (parse_leaf);
// bool and enum members take a plain value only.
//
// Two steps, as kiln.knowledge's collector:
//   prepare (kiln.boot's commit, before the game's entry point): the documents are read and checked against the exe's
//     reflection as far as types go (the type exists, the path names members, it ends in a scalar, the value's shape
//     fits; a component is one of the game's; an append's path is a list of scalars); mods that would change one thing
//     two ways are refused, the later one (ruling 7): one field set to different values, one component added (or copied)
//     and removed, one component copied from two templates, one list appended to twice, a component removed that another
//     mod's path starts at, a list appended to and set with [*] by another mod, a list cleared or replaced and changed
//     any other way by another mod (the same clear, or the same values, from both is fine), a "components[i]" path on a
//     template whose components change. Then, when the start has a base view of its game data newer than the boot
//     snapshot (gamedata_bootview.h; none by default), what only the data can say is checked against that view's decoded
//     values too (check_base below): the resource there, an index inside its list, a variant's or an optional's member
//     there, a template's component there, the place a scalar or a list, copyComponents' source there with the component.
//     Such a refusal holds only for a resource whose view the source says is final for this start (nothing in the start
//     creates or overrides it), and is then the mod's as any other; anything else, and what the view cannot say, is left
//     to the bytes. The plan is frozen, with the view's digest.
//   apply (kiln.assets when_resources_ready: the game's resource service is up and nothing has read through it): each
//     resource is copied, every document's structural edits are made in the copy (kPhases: removes, adds, copies, clears,
//     replaces, appends), then every set, with the game's values as the originals, and the copy is served as an override. A document is all or
//     nothing: when one of its edits fails, the copy is made again from the game's bytes without that document, so the
//     resource is what the other documents make it. What only the bytes can say (the resource is not there, an index
//     past the array, a component the template has not got) is a fault after the publication, once per document:
//     kiln.boot activation_fault (recorded; a required mod stops the start).
// Kiln's base rules (baserules.hpp, since 0.2.0-dev.31): the "baseRules" of blocks files and the staged "baseRules@<name>"
// documents are checked in prepare and composed into one raise-only pass over keen::BalancingTable, made in apply after
// every document's sets (a table no document patches gets a copy of its own). They claim no field, so no mod's patch
// conflicts with them. kiln.base_rules (kiln_base_rules.h) reads the game's own and the served table back.
// Nothing here waits, and no lock is held across a call into kiln.boot or kiln.assets.
#include <algorithm>
#include <atomic>
#include <cstring>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <unordered_map>
#include <vector>

#include "gamedata.h"
#include "baserules.hpp"
#include "gamedata_bootview.h"
#include "gear.h"
#include "kiln_blocks_json.hpp"
#include "kiln_boot_collector.hpp"
#include "refs.h"
#include "registry/boot_hooks.h"
#include "registry/claims.h"
#include "services/kiln_assets.h"
#include "services/kiln_base_rules.h"
#include "services/kiln_caps.h"

namespace kc {
namespace {

constexpr char kRow[] = "mods.boot.resource_patches";
constexpr char kBaseRulesRow[] = "build.base_rules";  // Kiln's base rules can be staged and are applied here (baserules.hpp)
constexpr char kKind[] = "resourcePatches";
constexpr char kGearRules[] = "gearRules";  // a data pack's boot values for gear (gear.h): resource patches of the BalancingTable
constexpr size_t kMaxSets = 4096;        // changes of one generation: field writes, list appends, components added or removed
constexpr size_t kMaxResource = 64u << 20;  // bytes of one patched resource

struct Set {
    std::string path;
    nlohmann::json value;
};
struct StructOp {
    char op = 0;                      // '-' removeComponents, '+' addComponents, 'c' copyComponents, 'x' clear, 'r' replace, 'a' append
    std::vector<std::string> names;   // components
    std::vector<KilnAssetKey> sources;  // copyComponents: the template each of `names` is copied from
    std::string path;                 // clear, replace, append
    std::vector<gjson> values;
    bool above_last = false;
};
constexpr char kPhases[] = {'-', '+', 'c', 'x', 'r', 'a'};  // the order of the structural edits on one resource
// One document of one mod: applied whole or not at all.
struct DocPatch {
    std::string mod, key;
    std::vector<StructOp> ops;  // its order; applied by kPhases (every document's), before the sets
    std::vector<Set> sets;      // its order
    size_t seq = 0;             // its place among the generation's documents (the check against the base view names the first)
};
struct ResPatch {
    KilnAssetKey res{};
    std::string type, guid;
    bool tmpl = false;              // keen::ecs::TemplateResource: "set" paths start with a component's name
    std::vector<DocPatch> docs;     // envelope order
};
struct BootPlan {
    std::vector<ResPatch> resources;
    size_t sets = 0;  // changes, against kMaxSets
    std::vector<baserules::Rule> rules;  // Kiln's base rules (baserules.hpp), in load order: one composed pass after every patch
    // the base view the plan was checked against before the start (gamedata_bootview.h); empty: none, the bytes decide
    std::string base_source, base_digest;
};

// Process-lifetime latch: readiness is one-shot; a later write would miss the game's first read.
std::atomic<bool> g_ready_before_publication{false};
constexpr char kMissedDeadline[] =
    "the game's resource service was already up before the boot generation was published; "
    "the patch cannot be written before the game reads its resources in this start";

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

std::string folded(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

// The path's segments as one text for the conflict keys: member names lower-case (the game's names match ignoring
// case), indices as plain numbers, joined again, so "A.b[03]." and "a.B[3]" are one path. On a template, "#0" of the first
// segment is the first component of its type, as no "#" is.
std::vector<Seg> normalised(std::vector<Seg> segs, bool tmpl) {
    for (Seg& s : segs) {
        if (!s.index) s.s = folded(s.s);
        else if (s.s != "*") s.s = std::to_string(std::stoul(s.s));  // nine digits at most (segments)
    }
    if (tmpl && !segs.empty() && !segs[0].index) {
        std::string& f = segs[0].s;
        const size_t h = f.find('#');
        const std::string k = h == std::string::npos ? "" : f.substr(h + 1);
        if (!k.empty() && k.size() <= 9 && std::all_of(k.begin(), k.end(), [](char d) { return d >= '0' && d <= '9'; })) {
            const unsigned long nth = std::stoul(k);
            f = f.substr(0, h) + (nth ? "#" + std::to_string(nth) : "");
        }
    }
    return segs;
}
std::string text_of(const std::vector<Seg>& segs) {
    std::string t;
    for (const Seg& s : segs) t += s.index ? "[" + s.s + "]" : (t.empty() ? "" : ".") + s.s;
    return t;
}
// One place of a resource: the same members, and at each index the same number or a "*" on either side.
bool same_place(const std::vector<Seg>& a, const std::vector<Seg>& b, size_t n = SIZE_MAX) {
    if (n == SIZE_MAX) {
        if (a.size() != b.size()) return false;
        n = a.size();
    }
    if (a.size() < n || b.size() < n) return false;
    for (size_t i = 0; i < n; ++i)
        if (a[i].index != b[i].index || (a[i].s != b[i].s && !(a[i].index && (a[i].s == "*" || b[i].s == "*")))) return false;
    return true;
}
// A path that goes through an element of the list at `list` ("list[2].x", "list[*]"): it names what a clear or a
// replace of that list takes away or puts elsewhere.
bool through(const std::vector<Seg>& list, const std::vector<Seg>& path) {
    return path.size() > list.size() && path[list.size()].index && same_place(list, path, list.size());
}
const char* verb_of(std::string what) {
    if (!what.empty() && what.back() == '!') what.pop_back();  // a fragile path (template_path)
    return what == "append" ? "appends to" : what == "clear" ? "clears" : what == "replace" ? "replaces" : "sets";
}

// On a template, what a path's first segment names: a member of the template itself ("components", "name"), or a component by
// its type name ("Gravity", "Gravity#1" the second of that type). Without the game's types, "components" and "name" are its members.
struct Head {
    bool member = false;            // the template's own member
    bool components_index = false;  // "components[i]...": a component by its place in the list
    std::string component;          // the component's type name as written, without "#k"; empty for a member
    bool bad_nth = false;           // "#k" is not a number
};
Head head_of(const gd::TType* tmpl, const std::vector<Seg>& segs) {
    Head h;
    if (segs.empty() || segs[0].index) return h.member = true, h;
    const std::string& first = segs[0].s;
    const gd::TField* f = tmpl ? tmpl->resolved()->field(first) : nullptr;
    h.member = f || (!tmpl && (folded(first) == "components" || folded(first) == "name"));
    if (h.member) {
        h.components_index = folded(f ? f->name : first) == "components" && segs.size() > 1 && segs[1].index;
        return h;
    }
    const size_t at = first.find('#');
    h.component = first.substr(0, at);
    if (at != std::string::npos) {
        const std::string k = first.substr(at + 1);
        h.bad_nth = k.empty() || k.size() > 9 || !std::all_of(k.begin(), k.end(), [](char d) { return d >= '0' && d <= '9'; });
    }
    return h;
}

// Where a path's type check starts: the resource's type, or on a template the component its first segment names (and the
// path below it). false + *err: the component is no type of the game's, or the path stops at it.
bool type_root(const gd::TType* type, bool tmpl, const std::vector<Seg>& segs, const gd::TType** root, std::vector<Seg>* rest, std::string* err) {
    *root = type;
    *rest = segs;
    if (!tmpl) return true;
    const Head h = head_of(type, segs);
    if (h.member) return true;
    if (h.bad_nth) return *err = "\"" + segs[0].s + "\": a component's \"#k\" is a number (the k-th of that type, from 0)", false;
    if (!(*root = gd::component_type(h.component, err))) return false;
    rest->erase(rest->begin());
    if (rest->empty()) return *err = "a path on a template names a member of a component (\"" + h.component + ".<member>\")", false;
    return true;
}

// The path against the types alone. true: it ends in `leaf` (checked), or *unknown (a variant or optional on the way:
// only the bytes can say). false + *err: no such member, not a list, not a scalar. list: it has to end in a list of scalars
// instead (an append or a replace), and `leaf` is the list's element; any_list: a list of anything (a clear).
bool check_types(const gd::TType* root, const std::vector<Seg>& segs, Field* leaf, bool* unknown, std::string* err, bool list = false,
                 bool any_list = false) {
    *unknown = false;
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
    if (any_list) {
        if (t->kind != gd::K_BLOBARRAY) return *err = "NotAList: " + at + " is " + t->name + ", not a list (clear empties a list)", false;
        return true;
    }
    if (list) {
        if (t->kind != gd::K_BLOBARRAY)
            return *err = "NotAList: " + at + " is " + t->name + ", not a list (append and replace take a list of numbers, bools or enum members)", false;
        const gd::TType* el = t->inner() ? t->inner()->resolved() : nullptr;
        if (!el) return *unknown = true, true;
        if (!gd::leaf_of(el, leaf) || leaf->ref || leaf->vt == KILN_CV_GUID || !leaf->bytes || leaf->bytes > 8)
            return *err = "NotScalar: " + at + " is a list of " + el->name + ", not of numbers, bools or enum members", false;
        return true;
    }
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
            if ((k == "raiseOnly" || k == "lowerOnly") ? !x.is_boolean() : !x.is_number())
                return *err = "\"" + k + "\" has the wrong kind of value", false;
        }
        if (v.empty()) return *err = "an empty operation object", false;
        if (v.contains("min") && v.contains("max") && v["min"].get<double>() > v["max"].get<double>())
            return *err = "min must not be greater than max", false;  // parse_leaf uses std::clamp: inverted bounds are undefined
        return true;
    }
    if (v.is_array() && f.enum_kind == KILN_CONTENT_FLAGS) {
        for (const auto& flag : v) {
            if (flag.is_string()) continue;
            // parse_leaf turns a numeric flag index into 1ll << index. Reject invalid shifts before it runs.
            if (!flag.is_number_integer() || flag < 0 || flag > 63)
                return *err = "a flag is a name or an integer index from 0 to 63", false;
        }
        return true;
    }
    if (v.is_boolean() || v.is_number() || v.is_string()) return true;
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

// The "resourcePatches" sections of the mods' blocks files, as documents, in load order (files by name within a mod). The files are
// read as every reader of blocks files reads them (kiln_blocks_json.hpp: comments, trailing commas). A file that is not such JSON is
// another reader's to report (Kiln.Blocks reads the same files), except for a required participant (kiln_boot_collector.hpp,
// entry_error): a blocks file of its own that cannot be read, or that names "resourcePatches" and is not JSON, refuses it (Refusal,
// turned into a refusal by prepare_boot).
// The "gearRules" of a mod's data packs (content/*.json, files by name, any case of .json as kiln.content finds them): the
// game's balancing values for gear (gear.h), lowered to one resource patch of the BalancingTable per file, keyed
// "gearRules@<file>". A file that cannot be read or is not JSON is kiln.content's packs' to report at the world's load; a
// gearRules that does not lower is an entry error here (a required mod is refused, an optional one logged and left out).
void gear_rule_docs(const std::filesystem::path& dir, const std::string& id, const kiln::boot::Input* boot, std::vector<kiln::boot::Doc>* out) {
    namespace fs = std::filesystem;
    std::error_code ec;
    if (!fs::is_directory(dir / L"content", ec)) return;
    std::vector<fs::path> files;
    for (auto it = fs::directory_iterator(dir / L"content", ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        std::wstring ext = it->path().extension().wstring();
        for (wchar_t& c : ext) c = static_cast<wchar_t>(std::towlower(c));
        if (ext == L".json") files.push_back(it->path());
    }
    std::sort(files.begin(), files.end());
    for (const fs::path& f : files) {
        std::ifstream in(f, std::ios::binary);
        if (!in) continue;
        const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        if (text.find(kGearRules) == std::string::npos) continue;
        const nlohmann::json j = nlohmann::json::parse(text, nullptr, false, true);  // as kiln.content reads packs: comments
        if (!j.is_object() || !j.contains(kGearRules)) continue;
        const std::u8string u = f.filename().u8string();
        const std::string label = "content/" + std::string(u.begin(), u.end());
        std::vector<std::string> errors;
        const gear::json lowered = gear::lower_rules(gear::json::parse(j[kGearRules].dump()), &errors);
        if (!errors.empty()) {
            std::string why;
            for (const std::string& e : errors) why += (why.empty() ? "" : "; ") + e;
            LOGW("mod %s: %s: its gearRules are not applied: %s", id.c_str(), label.c_str(), why.c_str());
            kiln::boot::entry_error(boot, id, kKind, label, "kiln.content: " + label + ": " + why);
            continue;
        }
        out->push_back({id, std::string(kGearRules) + "@" + std::string(u.begin(), u.end()), nlohmann::json::parse(lowered.dump())});
    }
}

// rules (when given): the files' "baseRules" sections, one document per rule, keyed "baseRules@<name>" as a staged rule is.
std::vector<kiln::boot::Doc> file_docs(const kiln::boot::Input* boot, std::vector<kiln::boot::Doc>* rules = nullptr) {
    namespace fs = std::filesystem;
    std::vector<kiln::boot::Doc> out;
    for (const fs::path& dir : mod_folder_list()) {
        std::error_code ec;
        const bool blocks = fs::is_directory(dir / L"blocks", ec);
        if (!blocks && !fs::is_directory(dir / L"content", ec)) continue;  // content/: data packs' gearRules
        const std::string id = kiln::boot::mod_id_of(dir);
        if (id.empty() || (boot && boot->out(id))) continue;
        std::vector<fs::path> files;
        if (blocks)
            for (auto it = fs::directory_iterator(dir / L"blocks", ec); !ec && it != fs::directory_iterator(); it.increment(ec))
                if (it->path().extension() == L".json") files.push_back(it->path());
        std::sort(files.begin(), files.end());
        for (const fs::path& f : files) {
            const std::u8string u = f.filename().u8string();
            const std::string label = "blocks/" + std::string(u.begin(), u.end());
            std::ifstream in(f, std::ios::binary);
            if (!in) {
                if (boot) kiln::boot::entry_error(*boot, id, kKind, label, "kiln.content: " + label + ": cannot be read");
                continue;
            }
            const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
            const bool patches = text.find(kKind) != std::string::npos, base = rules && text.find(baserules::kKind) != std::string::npos;
            if (!patches && !base) continue;
            const nlohmann::json j = kiln::blocks_json::parse_relaxed(text);
            if (!j.is_object() && boot)
                kiln::boot::entry_error(*boot, id, patches ? kKind : baserules::kKind, label,
                                        "kiln.content: " + label + ": not valid JSON (it names " + (patches ? kKind : baserules::kKind) + ")");
            if (!j.is_object()) continue;
            if (base && j.contains(baserules::kKind)) {
                if (!j[baserules::kKind].is_object()) rules->push_back({id, baserules::kPrefix, nlohmann::json()});  // refused by its shape
                else
                    for (const auto& [key, doc] : j[baserules::kKind].items()) rules->push_back({id, std::string(baserules::kPrefix) + key, doc});
            }
            if (!j.contains(kKind)) continue;
            if (!j[kKind].is_object()) {
                out.push_back({id, "", nlohmann::json()});  // refused by its shape, with the mod named
                continue;
            }
            for (const auto& [key, doc] : j[kKind].items()) out.push_back({id, key, doc});
        }
        gear_rule_docs(dir, id, boot, &out);
    }
    return out;
}

// ---- the check against the base view (gamedata_bootview.h) ----
// Before the start, what only the game's data can say: an index is inside its list, a variant holds the member, an
// optional has its value, a template has the component a path starts at, a path ends in a scalar (a number for an
// operation) and a list edit in a list, copyComponents' source has the component. The data is the base view's decoded
// value of each resource, walked as gd::resolve walks the bytes when the patch is written. What the value cannot say is
// left to the bytes, as before: a place another edit of the generation changes (an append, replace or clear of a list on
// the way, a component added, removed or copied), a member outside a variant (the types say which members a struct has,
// and a config named by its alias is no member of the value), a config shown by its alias, a value cut at the view's
// depth or shown as text, a struct of one scalar shown as that scalar (also inside a variant, which the bytes follow to
// the scalar), a resource the view cannot decode. And whatever the view says about a resource refuses only when the source
// says its view of that resource is final for this start (Source::final_for): Kiln, a boot mod, a data pack or another
// mod's assets.json may create or override a resource in this start, served before the patch is written, and then the
// bytes say.
constexpr size_t kBaseReadMax = 16u << 20;    // bytes of one resource's decoded value a check reads
constexpr size_t kBaseReadsMax = 128u << 20;  // bytes of decoded values one prepare reads in all

struct BaseRefusal {
    std::string mod, key, path, why;
};
enum class Seen { Ok, Unknown, Refused };

// One path walked over a decoded value.
struct Walk {
    bool tmpl = false;
    std::vector<std::pair<std::string, const nlohmann::json*>> leaves;  // the places the path names, and their values
    bool unknown = false;                                              // a branch the value cannot say
    std::string error;                                                 // the first branch that is not there
};

const nlohmann::json* member_of(const nlohmann::json& o, const std::string& name) {  // as TType::field: exact, then any case
    const auto it = o.find(name);
    if (it != o.end()) return &*it;
    const std::string f = folded(name);
    for (auto i = o.begin(); i != o.end(); ++i)
        if (folded(i.key()) == f) return &i.value();
    return nullptr;
}
std::string type_in(const nlohmann::json& o) {  // a variant's selected type ("$type"); empty for a plain struct
    const auto t = o.is_object() ? o.find("$type") : o.end();
    return t != o.end() && t->is_string() ? t->get<std::string>() : std::string();
}
std::string place_text(const std::string& at) { return at.empty() ? "the resource" : at; }
constexpr char kInBase[] = " in the game data this start reads";

void walk(const nlohmann::json& node, const std::vector<Seg>& segs, size_t i, const std::string& at, Walk* w) {
    // a config shown by its alias ({"$config", "value"}): gd::resolve reads the config's value or a member of it
    if (node.is_object() && node.contains("$config")) return void(w->unknown = true);
    if (i == segs.size()) return w->leaves.emplace_back(at, &node), void();
    auto fail = [&](const std::string& why) {
        if (w->error.empty()) w->error = why;
    };
    const Seg& s = segs[i];
    if (node.is_string()) return void(w->unknown = true);  // a value cut at the view's depth, a summary: the bytes say
    if (node.is_null()) return fail(place_text(at) + " is empty (an optional without a value)" + kInBase);
    if (!s.index) {
        if (i == 0 && w->tmpl && node.is_object() && !member_of(node, s.s)) {  // a template's component by its type ("Gravity#1")
            const size_t h = s.s.find('#');
            const std::string want = folded(s.s.substr(0, h)), k = h == std::string::npos ? "0" : s.s.substr(h + 1);
            const nlohmann::json* comps = member_of(node, "components");
            if (!comps || !comps->is_array() || k.empty() || k.size() > 9 || !std::all_of(k.begin(), k.end(), [](char c) { return c >= '0' && c <= '9'; }))
                return void(w->unknown = true);
            const unsigned long nth = std::stoul(k);
            unsigned long seen = 0;
            for (const auto& c : *comps) {
                const std::string t = type_in(c);
                if (t.empty()) {
                    w->unknown = true;
                    continue;
                }
                if (folded(t) == want && seen++ == nth) return walk(c, segs, 1, s.s, w);
            }
            if (!w->unknown) fail("the template has no component " + s.s + kInBase);
            return;
        }
        if (node.is_array()) return fail(place_text(at) + " is a list, which has no member " + s.s + kInBase);
        if (!node.is_object()) return void(w->unknown = true);  // a struct of one scalar (keen::Time, an id) shows as the scalar
        const nlohmann::json* m = member_of(node, s.s);
        if (!m) {
            const std::string t = type_in(node);
            // outside a variant the types say which members there are; a keen::impact::Configurations names its configs by
            // alias or id, which are no members of the value
            if (t.empty() || t == "Configurations") return void(w->unknown = true);
            return fail(place_text(at) + " is a " + t + kInBase + ", which has no member " + s.s);
        }
        return walk(*m, segs, i + 1, at.empty() ? s.s : at + "." + s.s, w);
    }
    if (!node.is_array()) {
        // a struct (the types say), or a keen::impact::Configurations, whose "[...]" names a config by its id
        if (node.is_object() && (type_in(node).empty() || type_in(node) == "Configurations")) return void(w->unknown = true);
        return fail(place_text(at) + " is not a list" + kInBase);
    }
    if (s.s == "*") {
        for (size_t k = 0; k < node.size(); ++k) walk(node[k], segs, i + 1, at + "[" + std::to_string(k) + "]", w);
        return;
    }
    const unsigned long k = std::stoul(s.s);  // segments(): nine digits at most
    if (k >= node.size())
        return fail("IndexPastEnd: " + at + "[" + s.s + "]: the list has " + std::to_string(node.size()) + " entries" + kInBase);
    walk(node[k], segs, i + 1, at + "[" + s.s + "]", w);
}

// A variant whose selected struct holds one member that is no struct or list (keen::HashKey32, keen::Time): the view
// shows the struct, the bytes follow the variant to it and take it as that one scalar (gd::leaf_of).
bool one_scalar_variant(const nlohmann::json& n) {
    if (!n.is_object() || type_in(n).empty() || n.size() != 2) return false;
    for (auto it = n.begin(); it != n.end(); ++it)
        if (it.key() != "$type") return !it->is_object() && !it->is_array();
    return false;
}

// A set: refused when a place it names is no scalar (no number, for an operation), or when it names no place at all
// ([*] over an empty list names none and changes nothing: resource-patches.md). [*] keeps the elements that have the
// path, as gd::resolve does.
Seen set_verdict(const Walk& w, const nlohmann::json& value, std::string* why) {
    bool unknown = w.unknown;
    for (const auto& [at, n] : w.leaves) {
        if (one_scalar_variant(*n)) {
            unknown = true;
            continue;
        }
        if (n->is_object() || n->is_array())
            return *why = "NotScalar: " + place_text(at) + " is " + (n->is_array() ? "a list" : "a struct") + kInBase + ", not a number, a bool or an enum",
                   Seen::Refused;
        if (is_op(value) && n->is_boolean())
            return *why = "OperationOnNonNumber: " + place_text(at) + " is a bool" + kInBase + " (it takes a plain value)", Seen::Refused;
    }
    if (unknown) return Seen::Unknown;
    if (w.leaves.empty() && !w.error.empty()) return *why = w.error, Seen::Refused;
    return Seen::Ok;
}

// A clear, replace or append: the path names one list (of scalars for replace and append).
Seen list_verdict(const Walk& w, char op, std::string* why) {
    if (w.leaves.size() == 1) {
        const auto& [at, n] = w.leaves[0];
        if (n->is_string()) return Seen::Unknown;
        if (n->is_null()) return *why = place_text(at) + " is empty (an optional without a value)" + kInBase, Seen::Refused;
        if (!n->is_array()) return *why = "NotAList: " + place_text(at) + " is not a list" + kInBase, Seen::Refused;
        if (op != 'x' && !n->empty() && ((*n)[0].is_object() || (*n)[0].is_array()))
            return *why = "NotScalar: " + place_text(at) + " is a list of structs or lists" + kInBase, Seen::Refused;
        return Seen::Ok;
    }
    if (w.unknown || !w.leaves.empty()) return Seen::Unknown;
    if (!w.error.empty()) return *why = w.error, Seen::Refused;
    return Seen::Unknown;
}

// What the generation's structural edits change on one resource: the lists they clear, replace or append to, and the
// components added, removed or copied. A path through such a list, or into such a component, is the bytes' to say.
struct Shape {
    std::vector<std::vector<Seg>> lists;  // normalised
    std::set<std::string> components;     // folded
    bool components_change = false;
};
Shape shape_of(const ResPatch& rp) {
    Shape sh;
    for (const DocPatch& d : rp.docs)
        for (const StructOp& o : d.ops) {
            if (o.op == 'x' || o.op == 'r' || o.op == 'a') {
                std::vector<Seg> segs;
                std::string e;
                if (segments(o.path, &segs, &e)) sh.lists.push_back(normalised(segs, rp.tmpl));
                continue;
            }
            sh.components_change = true;
            for (const std::string& n : o.names) sh.components.insert(folded(n));
        }
    return sh;
}
bool touched(const Shape& sh, const std::vector<Seg>& place, bool tmpl) {
    for (const auto& list : sh.lists)
        if (through(list, place)) return true;
    if (tmpl && !place.empty() && !place[0].index) {
        const std::string head = place[0].s.substr(0, place[0].s.find('#'));
        if (head == "components") return sh.components_change;  // a component by its place in the list
        if (sh.components.count(head)) return true;
    }
    return false;
}

// One document against the base view. false + *f: refused. What the view says about a resource refuses only when
// final_of(key) says the source's view of that resource is final for this start; otherwise it is logged and the bytes say.
template <class ReadFn, class FinalFn>
bool check_doc(const ResPatch& rp, const DocPatch& d, ReadFn&& read, FinalFn&& final_of, const Shape& sh, BaseRefusal* f) {
    auto refuse = [&](const std::string& path, const std::string& why) {
        *f = {d.mod, d.key, path, why};
        return false;
    };
    // a refusal of what the view says about `key` (`of`: that resource in words): false, refused, when the view of that
    // resource is final for this start; true, not refused, when the start may create or override it before the patch is
    // written (the bytes say then)
    auto refuse_if_final = [&](const KilnAssetKey& key, const std::string& of, const std::string& path, const std::string& why) {
        if (final_of(key)) return refuse(path, why + " (nothing in this start creates or overrides " + of + ")");
        LOGI("boot: resource patch %s/%s: %s%s; this start may create or override %s before the patch is written, so the patch is checked when it "
             "is written",
             d.mod.c_str(), d.key.c_str(), path.empty() ? "" : ("\"" + path + "\": ").c_str(), why.c_str(), of.c_str());
        return true;
    };
    const std::string what = rp.type + " " + guid_text(rp.res.guid);
    const bootview::Read& res = read(bootview::query_of(guid_text(rp.res.guid), rp.res.type, rp.res.part));
    if (res.found == bootview::Found::Missing && !refuse_if_final(rp.res, what, "", "MissingResource: the game data this start reads has no " + what))
        return false;
    // copyComponents' sources, as the game has them before any patch of this start
    for (const StructOp& o : d.ops)
        for (size_t k = 0; o.op == 'c' && k < o.sources.size(); ++k) {
            const std::string g = guid_text(o.sources[k].guid);
            const bootview::Read& src = read(bootview::query_of(g, o.sources[k].type, o.sources[k].part));
            if (src.found == bootview::Found::Missing &&
                !refuse_if_final(o.sources[k], "the template " + g, "",
                                 "\"copyComponents\": MissingResource: the template " + g + " that " + o.names[k] + " is copied from is not" + kInBase))
                return false;
            if (src.found != bootview::Found::Yes) continue;
            const nlohmann::json* comps = src.value.is_object() ? member_of(src.value, "components") : nullptr;
            if (!comps || !comps->is_array()) continue;
            bool found = false, unknown = false;
            for (const auto& c : *comps) {
                const std::string t = type_in(c);
                unknown = unknown || t.empty();
                found = found || folded(t) == folded(o.names[k]);
            }
            if (!found && !unknown &&
                !refuse_if_final(o.sources[k], "the template " + g, "", "\"copyComponents\": the template " + g + " has no " + o.names[k] + kInBase))
                return false;
        }
    if (res.found != bootview::Found::Yes) return true;
    // the paths on the resource itself: the first one the view refuses decides (not final: none of them can refuse)
    for (const StructOp& o : d.ops) {
        if (o.op != 'x' && o.op != 'r' && o.op != 'a') continue;
        std::vector<Seg> segs;
        std::string why;
        if (!segments(o.path, &segs, &why) || touched(sh, normalised(segs, rp.tmpl), rp.tmpl)) continue;
        Walk w;
        w.tmpl = rp.tmpl;
        walk(res.value, segs, 0, "", &w);
        if (list_verdict(w, o.op, &why) == Seen::Refused)
            return refuse_if_final(rp.res, what, o.path, std::string(o.op == 'x' ? "\"clear\": " : o.op == 'r' ? "\"replace\": " : "\"append\": ") + why);
    }
    for (const Set& s : d.sets) {
        std::vector<Seg> segs;
        std::string why;
        if (!segments(s.path, &segs, &why) || touched(sh, normalised(segs, rp.tmpl), rp.tmpl)) continue;
        Walk w;
        w.tmpl = rp.tmpl;
        walk(res.value, segs, 0, "", &w);
        if (set_verdict(w, s.value, &why) == Seen::Refused) return refuse_if_final(rp.res, what, s.path, why);
    }
    return true;
}

// The plan against the base view of this start's game data, every document in load order; the plan keeps the view's
// digest. false + *r: the first document the data refuses. true: none refused, or no view (nothing is checked).
bool check_base(BootPlan* plan, BaseRefusal* r) {
    if (plan->resources.empty() || !bootview::has_source()) return true;
    std::string why;
    bootview::BaseView* view = bootview::current(&why);
    if (!view) {
        LOGI("boot: resource patches are not checked against a base view of this start's game data before the start (%s); the bytes decide "
             "when they are written",
             why.c_str());
        return true;
    }
    const std::string digest = view->digest();
    if (digest.empty()) {
        LOGI("boot: the %s base view names no data; resource patches are checked when they are written", view->source());
        return true;
    }
    struct Item {
        size_t seq;
        const ResPatch* rp;
        const DocPatch* d;
    };
    std::vector<Item> order;
    for (const ResPatch& rp : plan->resources)
        for (const DocPatch& d : rp.docs) order.push_back({d.seq, &rp, &d});
    std::sort(order.begin(), order.end(), [](const Item& a, const Item& b) { return a.seq < b.seq; });
    std::map<std::string, bootview::Read> reads;  // by query: each resource read once
    size_t left = kBaseReadsMax;
    auto read = [&](const std::string& q) -> const bootview::Read& {
        if (const auto it = reads.find(q); it != reads.end()) return it->second;
        bootview::Read rd;
        if (!left) {
            rd.why = "the check read as much of the game data as one prepare may";
        } else {
            rd = bootview::read(*view, q, std::min(kBaseReadMax, left));
            if (rd.found == bootview::Found::Yes) left -= std::min(left, rd.bytes);
        }
        return reads.emplace(q, std::move(rd)).first->second;
    };
    std::map<std::string, bool> finals;  // by resource: the source's answer, asked once, only for a refusal
    auto final_of = [&](const KilnAssetKey& key) {
        const std::string q = bootview::query_of(guid_text(key.guid), key.type, key.part);
        if (const auto it = finals.find(q); it != finals.end()) return it->second;
        return finals.emplace(q, bootview::final_for(key)).first->second;
    };
    std::map<const ResPatch*, Shape> shapes;
    BaseRefusal first;
    bool refused = false;
    for (const Item& it : order) {
        auto sh = shapes.find(it.rp);
        if (sh == shapes.end()) sh = shapes.emplace(it.rp, shape_of(*it.rp)).first;
        if (!check_doc(*it.rp, *it.d, read, final_of, sh->second, &first)) {
            refused = true;
            break;
        }
    }
    // one generation from the first read to the last; otherwise nothing is refused and the bytes decide
    const std::string after = view->digest();
    if (after != digest) {
        LOGW("boot: the %s base view of this start's game data changed while the patches were checked (%.16s, then %.16s); the bytes decide "
             "when they are written",
             view->source(), digest.c_str(), after.empty() ? "none" : after.c_str());
        return true;
    }
    plan->base_source = view->source();
    plan->base_digest = digest;
    if (refused) {
        LOGW("boot: resource patch %s/%s: %s%s (checked before the start against the %s base view %.16s of this start's game data)",
             first.mod.c_str(), first.key.c_str(), first.path.empty() ? "" : ("\"" + first.path + "\": ").c_str(), first.why.c_str(),
             view->source(), digest.c_str());
        *r = std::move(first);
        return false;
    }
    LOGI("boot: resource patches checked before the start against the %s base view %.16s of this start's game data (%zu resource value(s) "
         "read)",
         view->source(), digest.c_str(), reads.size());
    return true;
}

void KILN_CALL destroy_plan(const void* p) { delete static_cast<const BootPlan*>(p); }

KilnResult KILN_CALL prepare_boot(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    // The game's entry point is held until publication (boot_service.cpp b_commit / after_clr).
    // Read once for this prepare; apply may also run on the resource-initialization thread.
    const bool missed_deadline = g_ready_before_publication.load();
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.content: the boot envelope is not JSON");
    auto plan = std::make_unique<BootPlan>();
    const bool types = gd::type_named("keen::ItemInfo") != nullptr;  // the exe's reflection is readable before the entry point
    struct Claim {
        std::string index, mod, value;
    };
    // what the documents so far change, by "<resource key>|...": fields set ("|<normalised path without indices>": its indices,
    // the mod, the value), components added or removed ("|<component, lower-case>": "+" / "-"), paths that start at a
    // component ("|<component>": the path, "set" / "append"), documents that add or remove components ("": which), paths
    // that name a component by its place in the list ("": the path)
    std::map<std::string, std::vector<Claim>> components, uses, structure, by_place;
    // fields set, by "<resource key>|<normalised path without indices>": per index the claims in order, and the claims with a
    // "*" (they meet every index): a set asks its own index and the starred ones, not every earlier claim on those members
    struct FieldClaims {
        std::map<std::string, std::vector<std::pair<uint64_t, Claim>>> exact;
        std::vector<std::pair<uint64_t, Claim>> starred, all;
    };
    std::unordered_map<std::string, FieldClaims> fields;
    uint64_t field_order = 0;
    struct ListClaim {
        std::string res, mod, path;
        std::vector<Seg> place;  // normalised
        std::string what, value;  // reshaped: "clear" / "replace" and the values (dumped)
    };
    // lists appended to; lists set with [*] (the list's place); lists cleared or replaced; every set's place
    std::vector<ListClaim> appended, starred, reshaped, set_places;
    // The same claims in the registry's claim index (registry/claims.h, P1-PLAN 5.3): per resource a trie of the places, so a
    // question walks one path instead of every earlier claim. Its answer is the earliest claim that meets (the lowest seq,
    // the order the vectors hold them in), so every refusal names the mod and path the scans named. A place deeper than the
    // index keeps (kMaxSegs segments) turns the index off for this prepare: the vectors are scanned as before.
    registry::ClaimIndex index;
    bool deep = false;
    using RClaim = registry::Claim;
    using RKind = registry::ClaimKind;
    auto rsegs = [&](const std::vector<Seg>& v) {
        std::vector<registry::Seg> out;
        out.reserve(v.size());
        for (const Seg& s : v) out.push_back({s.s, s.index});
        if (v.size() > registry::kMaxSegs) deep = true;
        return out;
    };
    auto claim = [&](const std::string& res, const std::vector<Seg>& place, RKind kind, const std::string& mod, const std::string& path, const std::string& value) {
        index.add(res, rsegs(place), RClaim{0, mod, kind, path, value});
    };
    auto what_of = [](const RClaim& c) { return std::string(c.kind == RKind::Clear ? "clear" : "replace"); };
    // where each resource's patch is in plan->resources (the vector keeps the envelope's order)
    std::unordered_map<std::string, size_t> res_at;
    // each mod's files first, then what its OnBoot staged; a key both have stays the file's
    std::vector<kiln::boot::Doc> docs, rule_docs;
    try {
        docs = file_docs(&input, &rule_docs);
    } catch (const kiln::boot::Refusal& r) {
        return kiln::boot::refuse(out, issue, r.mod, r.kind, r.key, r.what());
    }
    {
        std::set<std::string> have, have_rules;
        for (const auto& d : docs) have.insert(d.owner + "/" + d.key);
        for (const auto& d : rule_docs) have_rules.insert(d.owner + "/" + d.key);
        for (auto& d : input.docs("patches")) {
            // a staged BaseRulesBlock (Kiln.Blocks' lowering keys it "baseRules@<name>"; a resource patch's name has no "@")
            const bool rule = d.key.rfind(baserules::kPrefix, 0) == 0;
            if (!(rule ? have_rules : have).insert(d.owner + "/" + d.key).second) {
                LOGI("boot: %s %s/%s: the mod's blocks file has this key too: the file's stays", rule ? "base rule" : "resource patch", d.owner.c_str(),
                     d.key.c_str());
                continue;
            }
            (rule ? rule_docs : docs).push_back(std::move(d));
        }
    }
    // Kiln's base rules (baserules.hpp): checked here, composed into one raise-only pass over the balancing table after
    // every patch. They claim no field: no conflict with another mod's patch or rule.
    {
        std::set<std::string> said;
        std::string untranslatable;  // with the types: why this game build's table cannot take Kiln's pass ("" = it can)
        bool checked = false;
        for (const auto& d : rule_docs) {
            const std::string name = d.key.substr(sizeof(baserules::kPrefix) - 1);
            if (!input.owner.count(d.owner)) {
                if (said.insert(d.owner).second)
                    LOGW("mod %s has \"baseRules\" in a blocks file and takes no part in the boot generation: they are not applied. Add \"boot\": true "
                         "to its kiln.mod.json",
                         d.owner.c_str());
                continue;
            }
            auto refused = [&](const std::string& why) { return kiln::boot::refuse(out, issue, d.owner, baserules::kKind, name, why); };
            if (g_off) return refused(std::string("base rules are not applied here: ") + g_off);
            if (missed_deadline) {
                row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, std::string("resource patch writes were not applied: ") + kMissedDeadline);
                return refused(kMissedDeadline);
            }
            baserules::Rule r;
            r.mod = d.owner;
            r.name = name;
            std::string err;
            if (!baserules::parse(d.document, &r, &err)) return refused(err);
            if (types && !checked) {
                checked = true;
                const gd::TType* t = gd::type_named(baserules::kTable);
                if (!t || !t->resolved() || t->resolved()->kind != gd::K_STRUCT) untranslatable = std::string("this game build has no struct type ") + baserules::kTable;
                for (const std::string& path : {std::string(baserules::kAltars) + "[*]", std::string(baserules::kSizes) + "[*].x"}) {
                    if (!untranslatable.empty()) break;
                    std::vector<Seg> segs, rest;
                    const gd::TType* root = nullptr;
                    Field leaf;
                    bool unknown = false;
                    if (!segments(path, &segs, &err) || !type_root(t, false, segs, &root, &rest, &err) || !check_types(root, rest, &leaf, &unknown, &err))
                        untranslatable = "\"" + path + "\": " + err;
                }
            }
            if (!untranslatable.empty()) return refused("Kiln's base rules cannot be written into this game build's balancing table: " + untranslatable);
            plan->rules.push_back(std::move(r));
        }
    }
    std::set<std::string> not_boot;
    size_t seq = 0;
    for (const auto& d : docs) {
        // a mod that takes no part in the generation (no "boot" in its kiln.mod.json): its patches are not applied
        if (!input.owner.count(d.owner)) {
            if (not_boot.insert(d.owner).second) {
                if (d.key.rfind(kGearRules, 0) == 0)
                    LOGW("mod %s has \"gearRules\" in a data pack and takes no part in the boot generation: they are not applied. Add \"boot\": true "
                         "to its kiln.mod.json",
                         d.owner.c_str());
                else
                    LOGW("mod %s has \"resourcePatches\" in a blocks file and takes no part in the boot generation: they are not applied. Add \"boot\": true "
                         "to its kiln.mod.json",
                         d.owner.c_str());
            }
            continue;
        }
        auto refused = [&](const std::string& path, const std::string& why) {
            return kiln::boot::refuse(out, issue, d.owner, kKind, d.key, (path.empty() ? std::string() : "\"" + path + "\": ") + why);
        };
        if (g_off) return refused("", std::string("resource patches are not applied here: ") + g_off);
        const nlohmann::json& doc = d.document;
        auto nonempty = [&](const char* k) {  // present and not empty (a value of the wrong kind is its own member's error)
            return doc.is_object() && doc.contains(k) && !doc[k].is_null() && !((doc[k].is_object() || doc[k].is_array()) && doc[k].empty());
        };
        const bool structural = nonempty("addComponents") || nonempty("removeComponents") || nonempty("append") || nonempty("copyComponents") ||
                                nonempty("clear") || nonempty("replace");
        if (!doc.is_object() || !doc.contains("resource") || !doc["resource"].is_string() || !doc.contains("type") || !doc["type"].is_string() ||
            (doc.contains("set") && !doc["set"].is_object()) || (!nonempty("set") && !structural))
            return refused("", "a resource patch is {\"resource\": \"<GUID>\", \"type\": \"<type name>\", \"set\": {\"<path>\": <value>}} "
                               "(and/or \"addComponents\", \"removeComponents\", \"copyComponents\", \"clear\", \"replace\", \"append\")");
        if (missed_deadline) {
            row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, std::string("resource patch writes were not applied: ") + kMissedDeadline);
            // kiln.boot drops this optional participant and prepares again, or stops for a required one.
            return refused("", kMissedDeadline);
        }
        for (const auto& [k, x] : doc.items())
            if (k != "format" && k != "resource" && k != "type" && k != "part" && k != "set" && k != "removeComponents" && k != "addComponents" &&
                k != "copyComponents" && k != "clear" && k != "replace" && k != "append")
                return refused("", "unknown member \"" + k +
                                       "\" (resource, type, part, set, removeComponents, addComponents, copyComponents, clear, replace, append)");
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
        const std::string res_key = guid_text(rp.res.guid) + "/" + rp.type + "/" + std::to_string(rp.res.part);
        rp.tmpl = rp.type == "keen::ecs::TemplateResource";
        DocPatch dp{d.owner, d.key, {}, {}};
        dp.seq = seq++;
        // every change counts against the limit: a field write, a list's append, a component added or removed
        const std::string too_many =
            "more than " + std::to_string(kMaxSets) + " changes (field writes, lists cleared, replaced or appended to, components) in one boot generation";
        auto count = [&]() { return plan->sets < kMaxSets ? (++plan->sets, true) : false; };
        // A path on a template: a component another mod removes is not one to start at, and "components[i]" names another
        // component once the list changes.
        // `fragile`: the path counts on what the component holds (a list index on the way, a change of the game's value,
        // a list edit): another mod's copyComponents of that component would pull it from under it
        auto template_path = [&](const std::string& path, const std::vector<Seg>& segs, const char* what, bool fragile, std::string* why) {
            const Head h = head_of(types ? type : nullptr, segs);
            if (h.components_index) {
                if (const auto& st = structure[res_key]; !st.empty())
                    return *why = "\"components[i]\" names a component by its place in the list, which mod " + st.front().mod +
                                  " changes (" + st.front().value + " of " + res_key + "); name the component by its type (\"Gravity.isActive\")",
                           false;
                by_place[res_key].push_back({path, d.owner, what});
                return true;
            }
            if (h.member || h.component.empty()) return true;
            const std::string c = folded(h.component);
            for (const Claim& r : components[res_key + "|" + c]) {
                if (r.mod != d.owner && r.value == "-")
                    return *why = "Conflict: mod " + r.mod + " removes the component " + h.component + " of " + res_key +
                                  " that this path starts at; the later mod is refused",
                           false;
                if (r.mod != d.owner && fragile && r.value[0] == 'c')
                    return *why = "Conflict: mod " + r.mod + " copies the component " + h.component + " of " + res_key +
                                  " whole from another template, and this path counts on what it holds (a list, an index, or a change of the "
                                  "game's value); the later mod is refused",
                           false;
            }
            uses[res_key + "|" + c].push_back({path, d.owner, std::string(what) + (fragile ? "!" : "")});
            return true;
        };
        // the structural edits: what can be checked here is, the bytes when they are applied
        for (const char* which : {"removeComponents", "addComponents"}) {
            if (!doc.contains(which) || (doc[which].is_array() && doc[which].empty())) continue;
            const bool add = which[0] == 'a';
            const std::string label = std::string("\"") + which + "\"";
            if (!doc[which].is_array()) return refused("", label + " is a list of component type names");
            if (!rp.tmpl) return refused("", label + ": only a template (keen::ecs::TemplateResource) has components");
            StructOp op;
            op.op = add ? '+' : '-';
            for (const auto& n : doc[which]) {
                if (!n.is_string() || n.get<std::string>().empty()) return refused("", label + " is a list of component type names");
                const std::string brief = n.get<std::string>();
                if (!count()) return refused("", too_many);
                std::string why;
                if (types && !gd::component_type(brief, &why)) return refused("", label + ": " + why);
                const std::string c = folded(brief);
                if (!add && c == "currenttransform") return refused("", label + ": CurrentTransform is what any entity needs to live");
                // two mods: one adds what the other removes
                auto& list = components[res_key + "|" + c];
                for (const Claim& cl : list)
                    if (cl.mod != d.owner && (add ? cl.value == "-" : cl.value != "-"))
                        return refused("", "Conflict: mod " + cl.mod + (add ? " removes " : cl.value == "+" ? " adds " : " copies ") + brief + " of " +
                                               res_key + "; two mods on one component are not combined");
                // nor is a component taken away under another mod's path
                if (!add)
                    for (const Claim& u : uses[res_key + "|" + c])
                        if (u.mod != d.owner)
                            return refused("", "Conflict: mod " + u.mod + " " + verb_of(u.value) + " \"" + u.index + "\" of " + res_key +
                                                   ", and this patch removes " + brief + "; the later mod is refused");
                list.push_back({"", d.owner, add ? "+" : "-"});
                op.names.push_back(brief);
            }
            if (const auto& ix = by_place[res_key]; !ix.empty())
                return refused("", label + ": mod " + ix.front().mod + " names a component of " + res_key + " by its place in the list (\"" + ix.front().index +
                                       "\"), which this changes; the later mod is refused");
            structure[res_key].push_back({"", d.owner, which});
            dp.ops.push_back(std::move(op));
        }
        // copyComponents: {"<component type>": "<template GUID>"}, a component copied whole from another template
        if (doc.contains("copyComponents") && !(doc["copyComponents"].is_object() && doc["copyComponents"].empty())) {
            const std::string label = "\"copyComponents\"";
            if (!doc["copyComponents"].is_object()) return refused("", label + " is {\"<component type>\": \"<template GUID>\"}");
            if (!rp.tmpl) return refused("", label + ": only a template (keen::ecs::TemplateResource) has components");
            StructOp op;
            op.op = 'c';
            for (const auto& [brief, from] : doc["copyComponents"].items()) {
                KilnAssetKey src{};
                if (brief.empty() || !from.is_string() || !guid_parse(from.get<std::string>(), src.guid))
                    return refused("", label + ": \"" + brief + "\" takes the GUID of the template it is copied from");
                if (std::memcmp(src.guid, rp.res.guid, sizeof src.guid) == 0) return refused("", label + ": " + brief + " is copied from the template it patches");
                src.type = rp.res.type;
                src.part = 0;
                if (!count()) return refused("", too_many);
                std::string why;
                if (types && !gd::component_type(brief, &why)) return refused("", label + ": " + why);
                const std::string c = folded(brief);
                if (c == "currenttransform") return refused("", label + ": CurrentTransform is what any entity needs to live: it is not copied over");
                const std::string mine = "c:" + guid_text(src.guid);
                auto& list = components[res_key + "|" + c];
                for (const Claim& cl : list)
                    if (cl.mod != d.owner && (cl.value == "-" || (cl.value[0] == 'c' && cl.value != mine)))
                        return refused("", "Conflict: mod " + cl.mod + (cl.value == "-" ? " removes " : " copies from another template ") + brief +
                                               " of " + res_key + "; two mods on one component are not combined");
                // a copy replaces what another mod's path into the component counts on: the later mod (this one) is refused
                for (const Claim& u : uses[res_key + "|" + c])
                    if (u.mod != d.owner && !u.value.empty() && u.value.back() == '!')
                        return refused("", "Conflict: mod " + u.mod + " " + verb_of(u.value) + " \"" + u.index + "\" of " + res_key +
                                               ", which counts on what " + brief + " holds, and this patch copies " + brief +
                                               " whole from another template; the later mod is refused");
                list.push_back({"", d.owner, mine});
                op.names.push_back(brief);
                op.sources.push_back(src);
            }
            if (const auto& ix = by_place[res_key]; !ix.empty())
                return refused("", label + ": mod " + ix.front().mod + " names a component of " + res_key + " by its place in the list (\"" +
                                       ix.front().index + "\"), which this changes; the later mod is refused");
            structure[res_key].push_back({"", d.owner, "copyComponents"});
            dp.ops.push_back(std::move(op));
        }
        // the list edits, in their order: clear (["<path>"]), replace ({"<path>": {"values": [...]}}), append ({"<path>":
        // {"values": [...], "aboveLast": bool}}). A path names one list ([i] on the way, not [*]).
        for (const char* which : {"clear", "replace", "append"}) {
            if (!doc.contains(which) || ((doc[which].is_object() || doc[which].is_array()) && doc[which].empty())) continue;
            const std::string w = which;
            const bool clear = w == "clear", append = w == "append";
            if (clear ? !doc[which].is_array() : !doc[which].is_object())
                return refused("", clear ? "\"clear\" is a list of list paths ([\"noBuildZones\"])"
                                         : "\"" + w + "\" is {\"<list path>\": {\"values\": [...]" + (append ? ", \"aboveLast\": true" : "") + "}}");
            std::vector<std::pair<std::string, nlohmann::json>> items;
            if (clear) {
                for (const auto& p : doc[which]) {
                    if (!p.is_string() || p.get<std::string>().empty()) return refused("", "\"clear\" is a list of list paths ([\"noBuildZones\"])");
                    items.emplace_back(p.get<std::string>(), nlohmann::json());
                }
            } else {
                for (const auto& [path, spec] : doc[which].items()) items.emplace_back(path, spec);
            }
            for (const auto& [path, spec] : items) {
                std::string err;
                std::vector<Seg> segs;
                if (!segments(path, &segs, &err)) return refused(path, err);
                if (std::any_of(segs.begin(), segs.end(), [](const Seg& s) { return s.index && s.s == "*"; }))
                    return refused(path, "\"" + w + "\": the path names one list ([i], not [*])");
                if (!clear) {
                    if (!spec.is_object() || !spec.contains("values") || !spec["values"].is_array() || spec["values"].empty() ||
                        (append && spec.contains("aboveLast") && !spec["aboveLast"].is_boolean()))
                        return refused(path, append ? "\"append\": a list path takes {\"values\": [<numbers, bools or enum names>], \"aboveLast\": true|false}"
                                                    : "\"replace\": a list path takes {\"values\": [<numbers, bools or enum names>]} (\"clear\" empties a list)");
                    for (const auto& [k, x] : spec.items())
                        if (k != "values" && !(append && k == "aboveLast"))
                            return refused(path, "\"" + w + "\": unknown member \"" + k + "\" (" + (append ? "values, aboveLast" : "values") + ")");
                    for (const auto& v : spec["values"])
                        if (!(v.is_number() || v.is_boolean() || v.is_string()))
                            return refused(path, "\"" + w + "\": a value is a number, a bool or an enum member's name");
                }
                if (!count()) return refused(path, too_many);
                if (rp.tmpl && !template_path(path, segs, which, true, &err)) return refused(path, err);
                // the list and its values against the types (a variant or an optional on the way: the bytes say)
                Field el;
                bool unknown = !types;
                const gd::TType* root = nullptr;
                std::vector<Seg> rest;
                if (types && (!type_root(type, rp.tmpl, segs, &root, &rest, &err) || !check_types(root, rest, &el, &unknown, &err, !clear, clear)))
                    return refused(path, "\"" + w + "\": " + err);
                StructOp op;
                op.op = clear ? 'x' : append ? 'a' : 'r';
                op.path = path;
                op.above_last = append && spec.value("aboveLast", false);
                if (!clear)
                    for (const auto& v : spec["values"]) {
                        op.values.push_back(gjson::parse(v.dump()));
                        uint8_t bytes[16] = {}, zero[16] = {};
                        bool reset = false;
                        if (!unknown && !gd::parse_leaf(el, op.values.back(), zero, bytes, &reset, &err)) return refused(path, "\"" + w + "\": " + err);
                    }
                // two mods on one list
                const std::vector<Seg> place = normalised(segs, rp.tmpl);
                const std::string dump = clear ? std::string() : spec["values"].dump();
                const std::vector<registry::Seg> P = rsegs(place);
                if (!deep) {
                    const std::string owner = d.owner;
                    auto other = [&](std::initializer_list<RKind> kinds) {
                        return [owner, kinds = std::vector<RKind>(kinds)](const RClaim& c) {
                            return c.mod != owner && std::find(kinds.begin(), kinds.end(), c.kind) != kinds.end();
                        };
                    };
                    const auto reshaping = other({RKind::Clear, RKind::Replace});
                    const RClaim* best = nullptr;
                    bool at_same = false;
                    auto take = [&](const RClaim* c, bool same) {
                        if (c && (!best || c->seq < best->seq)) best = c, at_same = same;
                    };
                    take(index.same_place(res_key, P, [&](const RClaim& c) {
                        return reshaping(c) && (append || what_of(c) != w || (c.value != dump && nlohmann::json::parse(c.value) != spec["values"]));
                    }), true);
                    take(index.list_through(res_key, P, reshaping), false);
                    if (!append) take(index.below_list(res_key, P, reshaping), false);
                    if (best && at_same)
                        return refused(path, "Conflict: mod " + best->mod + " " + verb_of(what_of(*best)) + " this list of " + res_key + " (\"" + best->path +
                                                 "\"); two mods on one list are not combined (the same " + what_of(*best) + " from both is)");
                    if (best)
                        return refused(path, "Conflict: mod " + best->mod + " " + verb_of(what_of(*best)) + " \"" + best->path + "\" of " + res_key +
                                                 ", which this path goes through or holds; the later mod is refused");
                    if (append) {
                        if (const RClaim* a = index.same_place(res_key, P, other({RKind::Append})))
                            return refused(path, "Conflict: mod " + a->mod + " appends to this list of " + res_key + " too (\"" + a->path +
                                                     "\"); two mods on one list are not combined");
                        if (const RClaim* s = index.same_place(res_key, P, other({RKind::Starred})))
                            return refused(path, "Conflict: mod " + s->mod + " sets every element of this list of " + res_key + " (\"" + s->path +
                                                     "\"), which would change what is appended here too; the later mod is refused");
                    } else {
                        for (const RKind k : {RKind::Append, RKind::Set}) {
                            best = nullptr;
                            take(index.same_place(res_key, P, other({k})), true);
                            take(index.below_list(res_key, P, other({k})), false);
                            if (best)
                                return refused(path, "Conflict: mod " + best->mod + (k == RKind::Append ? " appends to \"" : " sets \"") + best->path + "\" of " +
                                                         res_key + ", and this patch " + verb_of(w) + " that list; the later mod is refused");
                        }
                    }
                }
                if (deep)
                for (const ListClaim& r : reshaped) {
                    if (r.res != res_key || r.mod == d.owner) continue;
                    // the same values are the same replace however they are written (1 and 1.0)
                    if (same_place(r.place, place) && (append || r.what != w || (r.value != dump && nlohmann::json::parse(r.value) != spec["values"])))
                        return refused(path, "Conflict: mod " + r.mod + " " + verb_of(r.what) + " this list of " + res_key + " (\"" + r.path +
                                                 "\"); two mods on one list are not combined (the same " + r.what + " from both is)");
                    if (through(r.place, place) || (!append && through(place, r.place)))
                        return refused(path, "Conflict: mod " + r.mod + " " + verb_of(r.what) + " \"" + r.path + "\" of " + res_key +
                                                 ", which this path goes through or holds; the later mod is refused");
                }
                if (append) {
                    for (const ListClaim& a : deep ? appended : std::vector<ListClaim>())
                        if (a.res == res_key && a.mod != d.owner && same_place(a.place, place))
                            return refused(path, "Conflict: mod " + a.mod + " appends to this list of " + res_key + " too (\"" + a.path +
                                                     "\"); two mods on one list are not combined");
                    for (const ListClaim& s : deep ? starred : std::vector<ListClaim>())
                        if (s.res == res_key && s.mod != d.owner && same_place(s.place, place))
                            return refused(path, "Conflict: mod " + s.mod + " sets every element of this list of " + res_key + " (\"" + s.path +
                                                     "\"), which would change what is appended here too; the later mod is refused");
                    appended.push_back({res_key, d.owner, path, place, "", ""});
                    claim(res_key, place, RKind::Append, d.owner, path, "");
                } else {
                    for (const auto* claims : {&appended, &set_places})
                        for (const ListClaim& o : deep ? *claims : std::vector<ListClaim>())
                            if (o.res == res_key && o.mod != d.owner && (same_place(o.place, place) || through(place, o.place)))
                                return refused(path, "Conflict: mod " + o.mod + (claims == &appended ? " appends to \"" : " sets \"") + o.path + "\" of " +
                                                         res_key + ", and this patch " + verb_of(w) + " that list; the later mod is refused");
                    reshaped.push_back({res_key, d.owner, path, place, w, dump});
                    claim(res_key, place, clear ? RKind::Clear : RKind::Replace, d.owner, path, dump);
                }
                dp.ops.push_back(std::move(op));
            }
        }
        const nlohmann::json no_sets = nlohmann::json::object();
        for (const auto& [path, value] : (doc.contains("set") ? doc["set"] : no_sets).items()) {
            if (!count()) return refused(path, too_many);
            std::string err;
            std::vector<Seg> segs;
            if (!segments(path, &segs, &err)) return refused(path, err);
            if (rp.tmpl && !template_path(path, segs, "set",
                                          is_op(value) || std::any_of(segs.begin(), segs.end(), [](const Seg& g) { return g.index; }), &err))
                return refused(path, err);
            // a template's path starts at a component (by its type) or at a member of the template
            Field leaf;
            bool unknown = !types;
            const gd::TType* root = nullptr;
            std::vector<Seg> rest;
            if (types && (!type_root(type, rp.tmpl, segs, &root, &rest, &err) || !check_types(root, rest, &leaf, &unknown, &err)))
                return refused(path, err);
            if (!unknown && !check_value(leaf, value, &err)) return refused(path, err);
            if (unknown && is_op(value) && value.empty()) return refused(path, "an empty operation object");
            const std::vector<Seg> place = normalised(segs, rp.tmpl);
            const std::string norm = text_of(place);
            // two mods, one field: the earliest earlier claim on the same members whose indices meet this one's
            FieldClaims& fc = fields[res_key + "|" + base_of(norm)];
            const std::string ix = index_of(norm), dump = value.dump();
            auto meets = [&](const Claim& c) { return c.mod != d.owner && overlap(c.index, ix) && (c.value != dump || c.index != ix || is_op(value)); };
            const Claim* hit = nullptr;
            uint64_t hit_at = 0;
            if (ix.find('*') != std::string::npos) {  // a "*" meets every index: every claim on these members, in order
                for (const auto& [at, c] : fc.all)
                    if (meets(c)) {
                        hit = &c;
                        break;
                    }
            } else {
                if (const auto ex = fc.exact.find(ix); ex != fc.exact.end())
                    for (const auto& [at, c] : ex->second)
                        if (meets(c)) {
                            hit = &c, hit_at = at;
                            break;
                        }
                for (const auto& [at, c] : fc.starred)
                    if (meets(c)) {
                        if (!hit || at < hit_at) hit = &c, hit_at = at;
                        break;
                    }
            }
            if (hit)
                return refused(path, "Conflict: mod " + hit->mod + " sets this field of " + res_key + " too (" + hit->value +
                                         "); two mods on one field are not combined");
            fc.all.emplace_back(field_order, Claim{ix, d.owner, dump});
            (ix.find('*') != std::string::npos ? fc.starred : fc.exact[ix]).emplace_back(field_order, Claim{ix, d.owner, dump});
            ++field_order;
            // a list another mod clears or replaces: its elements are not the game's any more
            const std::vector<registry::Seg> P = rsegs(place);
            if (!deep) {
                const std::string owner = d.owner;
                if (const RClaim* r = index.list_through(res_key, P, [&](const RClaim& c) { return c.mod != owner && (c.kind == RKind::Clear || c.kind == RKind::Replace); }))
                    return refused(path, "Conflict: mod " + r->mod + " " + verb_of(what_of(*r)) + " the list \"" + r->path + "\" of " + res_key +
                                             " this path goes through; the later mod is refused");
            }
            for (const ListClaim& r : deep ? reshaped : std::vector<ListClaim>())
                if (r.res == res_key && r.mod != d.owner && through(r.place, place))
                    return refused(path, "Conflict: mod " + r.mod + " " + verb_of(r.what) + " the list \"" + r.path + "\" of " + res_key +
                                             " this path goes through; the later mod is refused");
            set_places.push_back({res_key, d.owner, path, place, "set", ""});
            claim(res_key, place, RKind::Set, d.owner, path, "");
            // [*] over a list another mod appends to would change the appended values too: the later mod is refused
            // ([i] names the same element either way, and is not)
            for (size_t j = 0; j < place.size(); ++j) {
                if (!place[j].index || place[j].s != "*") continue;
                const std::vector<Seg> listp(place.begin(), place.begin() + static_cast<std::ptrdiff_t>(j));
                const std::vector<registry::Seg> LP = rsegs(listp);
                const std::string owner = d.owner;
                const RClaim* a = deep ? nullptr : index.same_place(res_key, LP, [&](const RClaim& c) { return c.mod != owner && c.kind == RKind::Append; });
                if (a)
                    return refused(path, "Conflict: mod " + a->mod + " appends to this list of " + res_key + " (\"" + a->path +
                                             "\"), and [*] would change the appended values too; the later mod is refused (name elements by index)");
                for (const ListClaim& x : deep ? appended : std::vector<ListClaim>())
                    if (x.res == res_key && x.mod != d.owner && same_place(x.place, listp))
                        return refused(path, "Conflict: mod " + x.mod + " appends to this list of " + res_key + " (\"" + x.path +
                                                 "\"), and [*] would change the appended values too; the later mod is refused (name elements by index)");
                starred.push_back({res_key, d.owner, path, listp, "", ""});
                claim(res_key, listp, RKind::Starred, d.owner, path, "");
            }
            dp.sets.push_back({path, value});
        }
        const std::string res_bytes(reinterpret_cast<const char*>(&rp.res), sizeof rp.res);
        if (const auto at = res_at.find(res_bytes); at != res_at.end()) {
            plan->resources[at->second].docs.push_back(std::move(dp));
        } else {
            res_at.emplace(res_bytes, plan->resources.size());
            rp.docs.push_back(std::move(dp));
            plan->resources.push_back(std::move(rp));
        }
    }
    // what only the game's data can say, against the base view of this start's data when it has one (none: the bytes say it)
    {
        BaseRefusal r;
        if (!check_base(plan.get(), &r)) return kiln::boot::refuse(out, issue, r.mod, kKind, r.key, (r.path.empty() ? std::string() : "\"" + r.path + "\": ") + r.why);
    }
    LOGI("boot generation %llu prepared: %zu resource patch change(s) on %zu resource(s), %zu base rule(s)%s", static_cast<unsigned long long>(input.generation),
         plan->sets, plan->resources.size(), plan->rules.size(), types || !plan->sets ? "" : " (types are checked when they are applied)");
    return kiln::boot::prepared(out, plan.release(), &destroy_plan);
}

// The templates copyComponents copies from, as served before any resource patch of this start is written (read once, at
// the start of apply: a patch on a source template does not carry over into the copies). Key: guid_text.
struct Source {
    std::vector<uint8_t> bytes;
    std::string why;  // not read: why (bytes empty)
};
using Sources = std::map<std::string, Source>;

// One build of a resource's copy from the game's bytes, with every document that is not left out: every document's
// removeComponents, then addComponents, copyComponents, clear, replace and append (kPhases; each on the copy as the one
// before left it), then every document's "set", with the game's values as the originals. false: document *bad failed
// (*why says how) and this build is worth nothing. *notes: what each change did, logged for the build that is served.
bool build(const ResPatch& rp, const gd::TType* type, const uint8_t* data, size_t len, const std::vector<char>& left_out, const Sources& sources,
           const baserules::Composed* rules, std::vector<uint8_t>* copy, size_t* here, std::vector<std::string>* notes, size_t* bad, std::string* why) {
    const std::string what = rp.type + " " + guid_text(rp.res.guid);
    copy->assign(data, data + len);
    *here = 0;
    notes->clear();
    const gd::Root root_kind = rp.tmpl ? gd::Root::Template : gd::Root::Plain;
    // the elements each append added (its list's place, [first, first + n)): another mod's set of one of them is refused
    struct Added {
        std::string mod, path;
        std::vector<Seg> place;
        size_t first = 0, n = 0;
    };
    std::vector<Added> added;
    for (const char phase : kPhases) {
        for (size_t i = 0; i < rp.docs.size(); ++i) {
            if (left_out[i]) continue;
            const DocPatch& d = rp.docs[i];
            for (const StructOp& o : d.ops) {
                if (o.op != phase) continue;
                std::string err;
                size_t n = 0, first = 0;
                std::vector<gd::CopySource> from;
                for (size_t k = 0; o.op == 'c' && k < o.names.size(); ++k) {
                    const std::string g = guid_text(o.sources[k].guid);
                    const auto it = sources.find(g);
                    const bool have = it != sources.end() && it->second.why.empty();
                    from.push_back({o.names[k], g, have ? it->second.bytes.data() : nullptr, have ? it->second.bytes.size() : 0,
                                    it != sources.end() ? it->second.why : std::string()});
                }
                const bool ok = o.op == '-' ? gd::boot_remove_components(type->resolved(), o.names, copy, &n, &err)
                              : o.op == '+' ? gd::boot_add_components(type->resolved(), o.names, copy, &n, &err)
                              : o.op == 'c' ? gd::boot_copy_components(type->resolved(), from, copy, &n, &err, kMaxResource)
                              : o.op == 'x' ? gd::boot_clear(root_kind, type->resolved(), o.path, copy, &n, &err)
                              : o.op == 'r' ? gd::boot_replace(root_kind, type->resolved(), o.path, o.values, copy, &n, &err)
                                            : gd::boot_append(root_kind, type->resolved(), o.path, o.values, o.above_last, copy, &n, &err, &first);
                if (!ok) return *bad = i, *why = err, false;
                if (o.op == 'a' && n) {
                    std::vector<Seg> segs;
                    std::string e;
                    if (segments(o.path, &segs, &e)) added.push_back({d.mod, o.path, normalised(segs, rp.tmpl), first, n});
                }
                if (copy->size() > kMaxResource)
                    return *bad = i, *why = "the patched " + what + " would be over " + std::to_string(kMaxResource >> 20) + " MiB", false;
                // a clear or a replace changes the list even when it ends as long as it was
                *here += o.op == 'x' || o.op == 'r' ? std::max<size_t>(n, 1) : n;
                const std::string list = " \"" + o.path + "\": ";
                notes->push_back("boot: resource patch " + d.mod + "/" + d.key + ": " + what +
                                 (o.op == 'a'   ? list + std::to_string(n) + " value(s) appended"
                                  : o.op == 'x' ? list + "cleared (" + std::to_string(n) + " element(s) were there)"
                                  : o.op == 'r' ? list + "replaced by " + std::to_string(n) + " value(s)"
                                                : ": " + std::to_string(n) + " component(s) " +
                                                      (o.op == '+' ? "added" : o.op == 'c' ? "copied" : "removed")));
            }
        }
    }
    const uintptr_t lo = reinterpret_cast<uintptr_t>(copy->data()), hi = lo + copy->size();
    for (size_t i = 0; i < rp.docs.size(); ++i) {
        if (left_out[i]) continue;
        const DocPatch& d = rp.docs[i];
        for (const Set& s : d.sets) {
            auto failed = [&](const std::string& e) { return *bad = i, *why = "\"" + s.path + "\": " + e, false; };
            // an element another mod appended is that mod's: a set by index does not write over it (either load order:
            // appends are made before every set). The mod's own appended elements, and the list's own, are fine.
            {
                std::vector<Seg> segs;
                std::string e;
                if (segments(s.path, &segs, &e)) {
                    const std::vector<Seg> place = normalised(segs, rp.tmpl);
                    for (const Added& a : added) {
                        const size_t at = a.place.size();
                        if (a.mod == d.mod || place.size() <= at || !place[at].index || place[at].s == "*" || !same_place(a.place, place, at)) continue;
                        const unsigned long k = std::stoul(place[at].s);
                        if (k >= a.first && k < a.first + a.n)
                            return failed("Conflict: element " + std::to_string(k) + " of \"" + a.path + "\" is one mod " + a.mod +
                                          " appended; another mod's set does not write over it");
                    }
                }
            }
            std::vector<gd::Resolved> found;
            std::string err;
            if (!gd::resolve(root_kind, gd::Node{type->resolved(), lo}, s.path, &found, &err)) return failed(err.empty() ? "no such member in " + what : err);
            if (found.empty()) {
                if (s.path.find('*') == std::string::npos) return failed(err.empty() ? "IndexPastEnd: no such element in " + what : err);
                notes->push_back("boot: resource patch " + d.mod + "/" + d.key + " \"" + s.path + "\": the list is empty in " + what + ": nothing to change");
                continue;
            }
            // every element is computed before any is written
            std::vector<std::pair<uintptr_t, std::vector<uint8_t>>> writes;
            std::string bad_value;
            for (const gd::Resolved& r : found) {
                Field f;
                if (!r.node.t || !gd::leaf_of(r.node.t, &f) || f.ref || f.vt == KILN_CV_GUID) {
                    bad_value = "NotScalar: " + r.path + " is not a number, a bool or an enum";
                    break;
                }
                if (!check_value(f, s.value, &bad_value)) break;
                if (r.node.addr < lo || r.node.addr + f.bytes > hi) {
                    bad_value = r.path + " lies outside the resource's own bytes";
                    break;
                }
                uint8_t out[16] = {};
                bool reset = false;
                if (!gd::parse_leaf(f, gjson::parse(s.value.dump()), reinterpret_cast<const uint8_t*>(r.node.addr), out, &reset, &bad_value)) break;
                if (reset) continue;  // {"reset": true}: the game's own value stays
                writes.emplace_back(r.node.addr, std::vector<uint8_t>(out, out + f.bytes));
            }
            if (!bad_value.empty()) return failed(bad_value);
            for (const auto& [addr, bytes] : writes) std::memcpy(reinterpret_cast<void*>(addr), bytes.data(), bytes.size());
            *here += writes.size();
            notes->push_back("boot: resource patch " + d.mod + "/" + d.key + ": " + what + " \"" + s.path + "\" = " + s.value.dump() + " (" +
                             std::to_string(writes.size()) + " value(s))");
        }
    }
    // Kiln's base rules (baserules.hpp): after every document, raise-only, from the game's own values. Fails as a whole
    // (*bad = rp.docs.size()): the caller builds the copy again without it.
    if (rules) {
        size_t n = 0;
        std::string err;
        if (!baserules::raise(type->resolved(), data, copy, *rules, &n, notes, &err)) return *bad = rp.docs.size(), *why = err, false;
        *here += n;
    }
    return true;
}

// What this start did to the balancing table, for kiln.base_rules' state (baserules.hpp): written once by apply, read on
// the tick thread.
struct BaseRecord {
    bool have = false;                  // apply read the table (a patch or a rule of this start was about it)
    KilnAssetKey key{};
    baserules::Lists original;          // the game's own values, before any patch of this start
    std::vector<baserules::Rule> rules; // the plan's, in load order
    std::vector<char> rule_applied;     // per rule
    baserules::Composed composed;
    bool applied = false;               // Kiln's pass is in the served table
    std::string fault;                  // why not, when there are rules
    std::vector<std::string> patched_by;  // mods whose own patches of the table were served
    baserules::Lists after_rules;       // the table after the old documents and the base rules, before the registry's writes
    bool have_after = false;
};
std::mutex g_base_mutex;
BaseRecord g_base;

// The balancing table's key: the plan's patch of it, else the one resource of its type kiln.assets lists.
bool table_key(const KilnAssetsApi* A, KilnAssetKey* key, std::string* why) {
    if (!A || !A->resource_list) return *why = "kiln.assets' resources are not available", false;
    const uint32_t type = fnv1a(std::string(baserules::kTable));
    KilnAssetKey keys[4];
    size_t n = 0;
    if (A->resource_list(type, keys, 4, &n) != KILN_OK || n == 0) return *why = std::string("MissingResource: this game build has no ") + baserules::kTable, false;
    if (n != 1) return *why = std::string("this game build has ") + std::to_string(n) + " resources of type " + baserules::kTable + ", not one", false;
    *key = keys[0];
    return true;
}

// ---- the registry stage (registry P1 slices S10, S11; registry/boot_hooks.h) ----
// The registry stage runs here, at resources-ready, before any patch of this start is written: it reads the base (the bytes
// kiln.assets serves now) and composes the mods' registry edits over the old documents below them (D1: a registry edit
// layers on every earlier writer and wins, reported). Its boot-routed fields (restart) are written in the same copy as the
// old documents, after them: one copy and one override per resource. Without the hooks (a host test) nothing of it runs.
std::vector<registry::boothook::OldDoc> old_docs(const BootPlan* plan) {
    std::vector<registry::boothook::OldDoc> out;
    if (!plan) return out;
    for (const ResPatch& rp : plan->resources)
        for (const DocPatch& d : rp.docs) {
            registry::boothook::OldDoc o;
            o.mod = d.mod, o.key = d.key, o.res = rp.res, o.type = rp.type;
            for (const Set& s : d.sets) o.sets.emplace_back(s.path, s.value);
            for (const StructOp& op : d.ops) {
                if (op.op != 'x' && op.op != 'r' && op.op != 'a') continue;
                registry::boothook::OldDoc::List l;
                l.op = op.op, l.path = op.path;
                for (const gjson& v : op.values) l.values.push_back(nlohmann::json::parse(v.dump()));
                o.lists.push_back(std::move(l));
            }
            out.push_back(std::move(o));
        }
    return out;
}

// Kiln's base rules as writers below the registry (ruling D1, 2026-11-21): each rule is an earlier writer of the balancing
// table, like an old boot patch. The registry stage composes on their result and names them (composedOn, overridden) through
// these documents: per rule, its own raise-only target at every index it raises over the game's value. The values written
// are the composed pass's (apply below); these documents only tell the registry who wrote what beneath it.
std::vector<registry::boothook::OldDoc> rule_docs(const BootPlan* plan, const KilnAssetKey& key, const baserules::Lists& game) {
    std::vector<registry::boothook::OldDoc> out;
    if (!plan) return out;
    static const char* const kAxis[3] = {"x", "y", "z"};
    for (const baserules::Rule& r : plan->rules) {
        const baserules::Composed c = baserules::compose({r});
        registry::boothook::OldDoc o;
        o.mod = r.mod, o.key = std::string(baserules::kKind) + "/" + r.name, o.res = key, o.type = baserules::kTable;
        for (size_t i = 0; i < game.altars.size(); ++i) {
            const int g = static_cast<int>(game.altars[i]);
            const int t = baserules::altars(g, c);
            if (t > g) o.sets.emplace_back("altarsPerFlameLevel[" + std::to_string(i) + "]", nlohmann::json{{"min", t}, {"raiseOnly", true}});
        }
        for (size_t l = 0; l < game.sizes.size(); ++l)
            for (int a = 0; a < 3; ++a) {
                const double g = game.sizes[l][a];
                const double t = baserules::size(g, static_cast<int>(l), a, c);
                if (t > g)
                    o.sets.emplace_back("buildzoneSizesPerAltarLevel[" + std::to_string(l) + "]." + kAxis[a], nlohmann::json{{"min", t}, {"raiseOnly", true}});
            }
        if (!o.sets.empty()) out.push_back(std::move(o));
    }
    return out;
}

// kiln.assets when_resources_ready: the game's resource service is up, nothing has read through it yet.
void KILN_CALL apply(void*) {
    const auto* plan = static_cast<const BootPlan*>(kiln::boot::published(g_api, "patches"));
    if (!plan) g_ready_before_publication.store(true);  // no prepare after this one may write anything
    bootview::finished();  // the generation is published (or none comes) and the resources are up: no check reads the base view any more
    const KilnAssetsApi* A = assets();
    // Kiln's base rules: one composed pass over the balancing table, after every patch of it and before the registry's writes
    BaseRecord record;
    std::string table_why;
    const bool rules_here = plan && !plan->rules.empty();
    if (rules_here) {
        record.rules = plan->rules;
        record.rule_applied.assign(plan->rules.size(), 0);
        record.composed = baserules::compose(plan->rules);
        // the game's own values, before any patch of this start (the rules' targets are computed on them)
        const gd::TType* tt = gd::type_named(baserules::kTable);
        const void* data = nullptr;
        size_t len = 0;
        if (!table_key(A, &record.key, &table_why)) {
        } else if (!tt || !tt->resolved() || tt->resolved()->kind != gd::K_STRUCT) {
            table_why = std::string("UnknownType: this game build has no struct type ") + baserules::kTable;
        } else if (A->resource_data(&record.key, &data, &len) != KILN_OK || !data || len < tt->resolved()->size) {
            table_why = std::string("the ") + baserules::kTable + " is not loaded";
        } else if (!(record.have = baserules::read(tt->resolved(), reinterpret_cast<uintptr_t>(data), &record.original, &table_why))) {
            LOGW("boot: %s: the game's own altar counts and build-zone sizes cannot be read (%s)", baserules::kTable, table_why.c_str());
        }
    }
    // the registry stage: the base as kiln.assets serves it now, before any patch of this start is written, composed over
    // the old documents and the base rules (they lie below it); its boot fields are written below, after them, in the same copy
    const registry::boothook::Hooks* H = registry::boothook::get();
    std::vector<registry::stage::BootResource> reg;
    if (H && H->run) {
        std::vector<registry::boothook::OldDoc> below = old_docs(plan);
        if (record.have) {
            std::vector<registry::boothook::OldDoc> rd = rule_docs(plan, record.key, record.original);
            below.insert(below.end(), std::make_move_iterator(rd.begin()), std::make_move_iterator(rd.end()));
        }
        reg = H->run(below);
    }
    std::vector<registry::stage::BootOutcome> outcomes;
    std::vector<registry::stage::FrontEnd> fronts;  // what became of each front-end document the stage handed over
    std::vector<char> reg_done(reg.size(), 0);
    auto reg_of = [&](const KilnAssetKey& k) -> const registry::stage::BootResource* {
        for (size_t i = 0; i < reg.size(); ++i)
            if (std::memcmp(&reg[i].key, &k, sizeof k) == 0) return reg_done[i] = 1, &reg[i];
        return nullptr;
    };
    auto reg_fail = [&](const registry::stage::BootResource& rb, const std::string& why) {
        for (const auto& f : rb.fields) outcomes.push_back({f.field, false, {}, why});
    };
    if (plan && (!plan->resources.empty() || rules_here)) {
    LOGI("boot: the game's resource service is up: resource patches of %zu resource(s)%s are written into copies of the bytes kiln.assets serves now",
         plan->resources.size(), plan->rules.empty() ? "" : (" and " + std::to_string(plan->rules.size()) + " base rule(s)").c_str());
    // a plan checked before the start against a base view: a fault now comes from the bytes served now (another provider's
    // override, a change after the check, or what only the bytes say); the game's data is not changed back
    const std::string checked = plan->base_digest.empty()
                                    ? std::string()
                                    : "; before the start it passed the check against the " + plan->base_source + " base view " +
                                          plan->base_digest.substr(0, 16) + " of this start's game data, and the bytes served now decide";
    if (!checked.empty())
        LOGI("boot: the plan was checked before the start against the %s base view %s", plan->base_source.c_str(), plan->base_digest.c_str());
    size_t written = 0, failed = 0;
    // one fault per document, naming its mod and key: for a required mod kiln.boot ends the process inside the call
    auto fault = [&](const DocPatch& d, const std::string& why) {
        ++failed;
        LOGE("boot: resource patch %s/%s: %s; nothing of this patch was applied%s", d.mod.c_str(), d.key.c_str(), why.c_str(), checked.c_str());
        kiln::boot::activation_fault(g_api, g_owner, d.mod, kKind, d.key, why + "; nothing of this patch was applied" + checked);
    };
    auto fault_rules = [&](const std::string& why) {
        record.fault = why;
        for (const baserules::Rule& r : plan->rules) {
            ++failed;
            LOGE("boot: base rule %s/%s: %s; Kiln's base rules were not applied", r.mod.c_str(), r.name.c_str(), why.c_str());
            kiln::boot::activation_fault(g_api, g_owner, r.mod, baserules::kKind, r.name, why + "; Kiln's base rules were not applied");
        }
    };
    // the table the rules go to: the plan's patch of it, else one with no documents
    const ResPatch* table = nullptr;
    for (const ResPatch& rp : plan->resources)
        if (!table && rp.type == baserules::kTable) table = &rp;
    ResPatch alone;
    if (rules_here && !table) {
        if (!table_why.empty() && !record.have) {
            fault_rules(table_why);
        } else {
            alone.res = record.key;
            alone.type = baserules::kTable;
            alone.guid = guid_text(alone.res.guid);
            table = &alone;
        }
    }
    // the templates copyComponents copies from, read before any patched copy is served
    Sources sources;
    for (const ResPatch& rp : plan->resources)
        for (const DocPatch& d : rp.docs)
            for (const StructOp& o : d.ops)
                for (size_t k = 0; o.op == 'c' && k < o.sources.size(); ++k) {
                    const std::string g = guid_text(o.sources[k].guid);
                    const void* p = nullptr;
                    size_t n = 0;
                    if (sources.count(g) || !A || A->resource_data(&o.sources[k], &p, &n) != KILN_OK || !p) continue;  // MissingResource
                    if (n > kMaxResource) {
                        sources[g].why = "the template " + g + " is " + std::to_string(n) + " bytes, over the " + std::to_string(kMaxResource >> 20) +
                                         " MiB a copy is read from";
                        continue;
                    }
                    sources[g].bytes.assign(static_cast<const uint8_t*>(p), static_cast<const uint8_t*>(p) + n);
                }
    auto one = [&](const ResPatch& rp) {
        const std::string what = rp.type + " " + guid_text(rp.res.guid);
        const bool is_table = &rp == table;
        const baserules::Composed* rules = is_table && rules_here ? &record.composed : nullptr;
        const void* data = nullptr;
        size_t len = 0;
        const gd::TType* type = gd::type_named(rp.type);
        std::string whole;
        if (!A) whole = "kiln.assets' resources are not available";
        else if (!type || !type->resolved() || type->resolved()->kind != gd::K_STRUCT) whole = "UnknownType: this game build has no struct type \"" + rp.type + "\"";
        else if (A->resource_data(&rp.res, &data, &len) != KILN_OK || !data) whole = "MissingResource: this game build has no " + what;
        else if (len < type->resolved()->size || len > kMaxResource)
            whole = "the resource " + what + " is " + std::to_string(len) + " bytes, which is not a " + rp.type;
        const registry::stage::BootResource* rb = reg_of(rp.res);
        // the front-end documents the stage handed over (BootResource::front, compose.h OldWrite::front): the registry's boot
        // back-end writes their values itself when that gives the same bytes as the documents do
        std::vector<char> front(rp.docs.size(), 0);
        bool any_front = false;
        if (rb && H && H->write)
            for (size_t i = 0; i < rp.docs.size(); ++i)
                if (std::find(rb->front.begin(), rb->front.end(), rp.docs[i].mod + "/" + rp.docs[i].key) != rb->front.end()) front[i] = 1, any_front = true;
        auto verdict = [&](const std::string& why) {
            for (size_t i = 0; i < rp.docs.size(); ++i)
                if (front[i]) fronts.push_back({rp.docs[i].mod, rp.docs[i].key, why.empty() ? "registry" : "resourcePatch", why, 0});
        };
        if (!whole.empty()) {
            for (const DocPatch& d : rp.docs) fault(d, whole);
            if (rules) fault_rules(whole);
            if (rb) reg_fail(*rb, whole);
            verdict(whole);
            return;
        }
        if (rp.type == baserules::kTable && !record.have) {
            std::string e;
            record.key = rp.res;
            record.have = baserules::read(type->resolved(), reinterpret_cast<uintptr_t>(data), &record.original, &e);
            if (!record.have) LOGW("boot: %s: the game's own altar counts and build-zone sizes cannot be read (%s)", what.c_str(), e.c_str());
        }
        // a document that fails is left out and the copy is built again from the game's bytes: the resource is then what
        // the other documents make it (each failure leaves one more out, so this ends). Kiln's base-rules pass fails as a
        // whole: the copy is built again without it.
        std::vector<char> left_out(rp.docs.size(), 0);
        std::vector<uint8_t> copy;
        std::vector<std::string> notes;
        size_t here = 0;
        for (;;) {
            size_t bad = 0;
            std::string why;
            if (build(rp, type, static_cast<const uint8_t*>(data), len, left_out, sources, rules, &copy, &here, &notes, &bad, &why)) break;
            if (bad == rp.docs.size()) {
                fault_rules(why);
                rules = nullptr;
                continue;
            }
            left_out[bad] = 1;
            fault(rp.docs[bad], why);
        }
        // what the table holds after the old documents and the base rules, before the registry writes over it
        if (rules && copy.size() >= type->resolved()->size) {
            std::string e;
            record.have_after = baserules::read(type->resolved(), reinterpret_cast<uintptr_t>(copy.data()), &record.after_rules, &e);
        }
        if (any_front) {
            // Both ways, compared byte for byte (the front-end's promise: the same bytes, or the old path). The old way: the
            // documents as above, then the registry's own rows. The front-end's: the copy built without the front-end
            // documents, then the registry's rows with theirs, in their places in the chain. Served: the front-end's copy when
            // the two are the same, else the old way's, as before (registry.json frontEnds says why).
            registry::stage::BootResource rb_old = *rb;
            rb_old.front.clear();
            for (auto& f : rb_old.fields) std::erase_if(f.rows, [](const registry::ChainRow& c) { return c.front; });
            std::erase_if(rb_old.fields, [](const registry::stage::BootField& f) { return f.rows.empty(); });
            std::vector<registry::stage::BootOutcome> out_old, out_front;
            std::vector<std::string> notes_front;
            std::vector<uint8_t> copy_front;
            size_t here_front = 0;
            if (!rb_old.fields.empty()) here += H->write(rp.res, rp.type, &copy, rb_old, &out_old, &notes);
            std::string why;
            for (size_t i = 0; i < rp.docs.size() && why.empty(); ++i)
                if (front[i] && left_out[i]) why = "its resource patch was not applied (boot.json has the fault)";
            if (why.empty()) {
                std::vector<char> without = left_out;
                for (size_t i = 0; i < rp.docs.size(); ++i) without[i] = without[i] || front[i];
                size_t bad = 0;
                std::string e;
                if (!build(rp, type, static_cast<const uint8_t*>(data), len, without, sources, rules, &copy_front, &here_front, &notes_front, &bad, &e))
                    why = "the resource could not be built without it (" + e + ")";
                else
                    here_front += H->write(rp.res, rp.type, &copy_front, *rb, &out_front, &notes_front);
            }
            if (why.empty() && copy_front != copy) {
                size_t at = 0;
                while (at < copy.size() && at < copy_front.size() && copy[at] == copy_front[at]) ++at;
                why = "the registry's bytes differ from its resource patch's (byte " + std::to_string(at) + " of the resource): the resource patch is applied";
            }
            if (why.empty()) {
                copy = std::move(copy_front);
                notes = std::move(notes_front);
                // the old way's count where it is higher: a resource patch counts every value it sets, one the copy already held
                // too, the registry's write leaves such a value out; so a copy is served (and kiln.base_rules names the mod)
                // exactly where the resource patch's was, with the same bytes
                here = std::max(here, here_front);
                // the outcomes (what each field became: registry.json's result and the join's registry part, kiln.registry.boot,
                // which hashes the values written): the old way's for every field the registry's own rows write, as an older Kiln
                // and a start with front-ends.off report them on these same bytes; the front-end's for the fields only front
                // rows write (no registry edit: never in that part). Not the front-end's own evaluation there: it starts from
                // the document's number (a double), the old way from the float the resource patch left, so a relative registry
                // edit above it gives the same bytes and another value (review-j2: 1.1 x 2 against float(1.1) x 2)
                outcomes.insert(outcomes.end(), out_old.begin(), out_old.end());
                for (const registry::stage::BootOutcome& o : out_front)
                    if (std::none_of(rb_old.fields.begin(), rb_old.fields.end(), [&](const registry::stage::BootField& f) { return f.field == o.field; }))
                        outcomes.push_back(o);
            } else {
                outcomes.insert(outcomes.end(), out_old.begin(), out_old.end());
                LOGW("registry: %s: %s", what.c_str(), why.c_str());
            }
            verdict(why);
        } else if (rb && H && H->write) {
            here += H->write(rp.res, rp.type, &copy, *rb, &outcomes, &notes);
        }
        for (const std::string& n : notes) LOGI("%s", n.c_str());
        if (!here) {
            // nothing to serve: the table already holds what the rules ask for (or nothing changed at all)
            if (rules) record.applied = true, std::fill(record.rule_applied.begin(), record.rule_applied.end(), 1);
            return;
        }
        const KilnResult rc = A->resource_put(g_owner, &rp.res, copy.data(), copy.size(), KILN_ASSET_PUT_OVERRIDE);
        if (rc != KILN_OK) {
            const std::string why = "kiln.assets refused the patched " + what + " (" + std::to_string(rc) + ")";
            for (size_t i = 0; i < rp.docs.size(); ++i)
                if (!left_out[i]) fault(rp.docs[i], why);
            if (rules) fault_rules(why), record.have_after = false;
            if (rb) {
                std::erase_if(outcomes, [&](const registry::stage::BootOutcome& o) {
                    return std::any_of(rb->fields.begin(), rb->fields.end(), [&](const auto& f) { return f.field == o.field; });
                });
                reg_fail(*rb, why);
            }
            for (auto& x : fronts)  // neither way reached the game
                for (size_t i = 0; i < rp.docs.size(); ++i)
                    if (front[i] && x.mod == rp.docs[i].mod && x.key == rp.docs[i].key) x.state = "resourcePatch", x.why = why;
            return;
        }
        written += here;
        if (rp.type == baserules::kTable) {
            for (size_t i = 0; i < rp.docs.size(); ++i)
                if (!left_out[i] && std::find(record.patched_by.begin(), record.patched_by.end(), rp.docs[i].mod) == record.patched_by.end())
                    record.patched_by.push_back(rp.docs[i].mod);
            if (rules) {
                record.applied = true;
                std::fill(record.rule_applied.begin(), record.rule_applied.end(), 1);
            }
        }
    };
    for (const ResPatch& rp : plan->resources) one(rp);
    if (table == &alone) one(alone);
    LOGI("boot: resource patches applied before the game read its resources: %zu value(s) in %zu resource(s)%s", written, plan->resources.size(),
         failed ? (", " + std::to_string(failed) + " patch(es) not applied (boot.json)").c_str() : "");
    if (failed) row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, std::to_string(failed) + " resource patch(es) of this start were not applied (state/boot.json)");
    }
    {
        std::lock_guard lock(g_base_mutex);
        g_base = std::move(record);
    }
    // the registry's boot fields on resources no old document touches: one copy each, the same way
    for (size_t i = 0; i < reg.size(); ++i) {
        if (reg_done[i]) continue;
        const registry::stage::BootResource& rb = reg[i];
        if (rb.type == registry::model::kHooksType) {
            // the hooks registries (limits): no resource to copy; Kiln's own hooks of the game's code take the values
            std::vector<std::string> notes;
            if (H && H->hooks) H->hooks(rb, &outcomes, &notes);
            else reg_fail(rb, "Kiln's hooks of the game's code are not here");
            for (const std::string& n : notes) LOGI("%s", n.c_str());
            continue;
        }
        ResPatch rp;
        rp.res = rb.key;
        rp.type = rb.type;
        rp.tmpl = rb.type == "keen::ecs::TemplateResource";
        const std::string what = rp.type + " " + guid_text(rp.res.guid);
        const gd::TType* type = gd::type_named(rp.type);
        const void* data = nullptr;
        size_t len = 0;
        if (!A || !type || !type->resolved() || A->resource_data(&rp.res, &data, &len) != KILN_OK || !data || len < type->resolved()->size || len > kMaxResource) {
            reg_fail(rb, "the resource " + what + " cannot be read here");
            continue;
        }
        std::vector<uint8_t> copy(static_cast<const uint8_t*>(data), static_cast<const uint8_t*>(data) + len);
        std::vector<std::string> notes;
        const size_t before = outcomes.size();
        const size_t here = H && H->write ? H->write(rp.res, rp.type, &copy, rb, &outcomes, &notes) : 0;
        for (const std::string& n : notes) LOGI("%s", n.c_str());
        if (!here) continue;
        const KilnResult rc = A->resource_put(g_owner, &rp.res, copy.data(), copy.size(), KILN_ASSET_PUT_OVERRIDE);
        if (rc != KILN_OK) {
            outcomes.resize(before);
            reg_fail(rb, "kiln.assets refused the patched " + what + " (" + std::to_string(rc) + ")");
        }
    }
    if (H && H->fronts) H->fronts(fronts);
    if (H && H->finish) H->finish(outcomes);
}

// The fields a registry edit changed after the base rules: {field, afterRules, effective, rules: ["mod:name", ...]}.
nlohmann::json registry_changes(const BaseRecord& r, const baserules::Lists& now) {
    nlohmann::json out = nlohmann::json::array();
    static const char* const kAxis[3] = {"x", "y", "z"};
    auto raisers = [&](auto raised) {
        nlohmann::json names = nlohmann::json::array();
        for (const baserules::Rule& rule : r.rules)
            if (raised(baserules::compose({rule}))) names.push_back(rule.mod + ":" + rule.name);
        return names;
    };
    for (size_t i = 0; i < now.altars.size() && i < r.after_rules.altars.size() && i < r.original.altars.size(); ++i) {
        if (now.altars[i] == r.after_rules.altars[i]) continue;
        const int g = static_cast<int>(r.original.altars[i]);
        nlohmann::json names = raisers([&](const baserules::Composed& c) { return baserules::altars(g, c) > g; });
        if (names.empty()) continue;  // no rule raised it: an old patch's value the registry changed, not a rule's
        out.push_back({{"field", "altarsPerFlameLevel[" + std::to_string(i) + "]"}, {"afterRules", r.after_rules.altars[i]}, {"effective", now.altars[i]}, {"rules", names}});
    }
    for (size_t l = 0; l < now.sizes.size() && l < r.after_rules.sizes.size() && l < r.original.sizes.size(); ++l)
        for (int a = 0; a < 3; ++a) {
            if (now.sizes[l][a] == r.after_rules.sizes[l][a]) continue;
            const double g = r.original.sizes[l][a];
            nlohmann::json names = raisers([&](const baserules::Composed& c) { return baserules::size(g, static_cast<int>(l), a, c) > g; });
            if (names.empty()) continue;
            out.push_back({{"field", "buildzoneSizesPerAltarLevel[" + std::to_string(l) + "]." + kAxis[a]}, {"afterRules", r.after_rules.sizes[l][a]},
                           {"effective", now.sizes[l][a]}, {"rules", names}});
        }
    return out;
}

// kiln.base_rules v1 (kiln_base_rules.h): this process's table and the rules that made it. Tick thread.
KilnResult KILN_CALL base_state(char* buf, size_t cap, size_t* out_len) {
    if (out_len) *out_len = 0;
    const KilnAssetsApi* A = assets();
    if (!A) return KILN_ERR_UNAVAILABLE;
    BaseRecord r;
    {
        std::lock_guard lock(g_base_mutex);
        r = g_base;
    }
    nlohmann::json j = {{"table", false}, {"rules", nlohmann::json::array()}, {"composed", nullptr}, {"applied", r.applied}, {"patchedBy", r.patched_by}};
    for (size_t i = 0; i < r.rules.size(); ++i) {
        nlohmann::json x = baserules::rule_json(r.rules[i]);
        x["applied"] = i < r.rule_applied.size() && r.rule_applied[i] != 0;
        j["rules"].push_back(x);
    }
    if (!r.rules.empty()) j["composed"] = baserules::composed_json(r.composed);
    if (!r.fault.empty()) j["fault"] = r.fault;
    KilnAssetKey key = r.key;
    std::string why;
    const gd::TType* type = gd::type_named(baserules::kTable);
    const void* data = nullptr;
    size_t len = 0;
    baserules::Lists now;
    if (!type || !type->resolved() || type->resolved()->kind != gd::K_STRUCT) why = std::string("UnknownType: this game build has no struct type ") + baserules::kTable;
    else if (!r.have && !table_key(A, &key, &why)) {
    } else if (A->resource_data(&key, &data, &len) != KILN_OK || !data || len < type->resolved()->size) why = std::string("the ") + baserules::kTable + " is not loaded";
    else if (baserules::read(type->resolved(), reinterpret_cast<uintptr_t>(data), &now, &why)) {
        j["table"] = true;
        j["effective"] = baserules::lists_json(now);
        j["original"] = baserules::lists_json(r.have ? r.original : now);
        // the registry stage writes after the base rules (ruling D1): where the served value is not what the rules left, a
        // registry edit changed it; name the rules that raised that field
        if (r.have_after && r.have) j["registryChanged"] = registry_changes(r, now);
    }
    if (!why.empty() && j["table"] == false) j["reason"] = why;
    const std::string text = j.dump();
    if (out_len) *out_len = text.size();
    if (!buf || cap <= text.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, text.data(), text.size());
    buf[text.size()] = 0;
    return KILN_OK;
}

const KilnBaseRulesApi kBaseRulesTable = {{sizeof(KilnBaseRulesApi), KILN_BASE_RULES_VERSION}, &base_state};

}  // namespace

// kiln.content's bind (content.cpp, after gd_boot): the collector of a boot profile and the row.
void gd_boot_patches_bind() {
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    const KilnAssetsApi* A = assets();
    if (!A) g_off = "kiln.assets' resources are off on this build";
    else if (kiln_caps_held(caps, KilnStr{kRow, sizeof(kRow) - 1})) g_off = "mods.boot.resource_patches is held until the client checks pass";
    g_bound = kiln::boot::collect(g_api, g_owner, "patches", &prepare_boot, nullptr);
    // resources-ready runs the registry stage in every start with kiln.assets' resources (the patches only with a boot phase)
    if (A) g_ready_asked = A->when_resources_ready(&apply, nullptr) == KILN_OK;
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
            std::vector<kiln::boot::Doc> rules;
            auto docs = file_docs(nullptr, &rules);
            for (auto& d : rules) docs.push_back(std::move(d));
            for (const auto& d : docs)
                if (said.insert(d.owner).second)
                    LOGW("mod %s has %s, and this start has no boot phase: they are not applied. Add \"boot\": true to its kiln.mod.json",
                         d.owner.c_str(), d.key.rfind(kGearRules, 0) == 0 ? "\"gearRules\" in a data pack"
                                          : d.key.rfind(baserules::kPrefix, 0) == 0 ? "\"baseRules\" in a blocks file"
                                                                                    : "\"resourcePatches\" in a blocks file");
        } catch (const std::exception&) {
        }
    }
    if (g_bound) LOGI("boot profile: resource patches are applied when the game's resource service is up%s%s", g_off ? "; off: " : "", g_off ? g_off : "");
    // Kiln's base rules (baserules.hpp): staged and applied as the patches are; their row follows the patches' row.
    if (caps && caps->report) {
        const bool held = kiln_caps_held(caps, KilnStr{kRow, sizeof(kRow) - 1});
        const std::string text = held     ? "needs mods.boot.resource_patches, which is held until the client checks pass"
                                 : g_off  ? std::string("needs mods.boot.resource_patches: ") + g_off
                                 : !g_bound ? "no boot phase in this process (no enabled mod has \"boot\" in its kiln.mod.json)"
                                            : "";
        const uint32_t state = text.empty() ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE;
        const uint32_t reason = text.empty() ? KILN_CAP_REASON_NONE : (held || !g_bound) ? KILN_CAP_REASON_POLICY : KILN_CAP_REASON_SERVICE_MISSING;
        caps->report(g_owner, KilnStr{kBaseRulesRow, sizeof(kBaseRulesRow) - 1}, state, reason, KilnStr{text.data(), text.size()});
    }
    // the read side (kiln_base_rules.h): with or without a boot phase (then the served table is the game's own)
    if (g_api->register_service(g_owner, KilnStr{KILN_BASE_RULES_SERVICE, sizeof(KILN_BASE_RULES_SERVICE) - 1}, &kBaseRulesTable) != KILN_OK)
        LOGW("kiln.base_rules could not be registered: Build.BaseLimits is unavailable");
}

}  // namespace kc
