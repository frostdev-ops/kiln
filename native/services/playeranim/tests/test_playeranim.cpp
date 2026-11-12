// SPDX-License-Identifier: MIT
// kiln.playeranim's portable part on the host, against the Python reference (see run.sh, gen_cases.py):
//   test_playeranim <cases dir> [<game exe>]         every check; the exe (read as a file) for the type-table checks
//   test_playeranim --time <cases dir> [<game exe>]  the times on the game's graph only (an optimized build)
// No game process, no Wine: the exe is only ever opened as a file.
#include <chrono>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <functional>
#include <iterator>
#include <map>
#include <memory>
#include <string>

#include "../../types/scan.h"
#include "blob.h"
#include "merge.h"
#include "nlohmann/json.hpp"
#include "states.h"
#include "typedb.h"

using namespace kpa;
using json = nlohmann::json;

namespace {

int g_failed = 0, g_passed = 0;
bool g_quiet_pass = true;

bool check(const std::string& name, bool ok, const std::string& detail = "") {
    (ok ? g_passed : g_failed)++;
    if (!ok || !g_quiet_pass) std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", name.c_str(), detail.empty() ? "" : ": ", detail.c_str());
    return ok;
}

std::string slurp(const std::string& p) {
    std::ifstream f(p, std::ios::binary);
    return {std::istreambuf_iterator<char>(f), {}};
}
Bytes slurp_bytes(const std::string& p) {
    const std::string s = slurp(p);
    return Bytes(s.begin(), s.end());
}
std::string unhex(const std::string& h) {
    std::string out;
    for (size_t i = 0; i + 1 < h.size(); i += 2) out.push_back(static_cast<char>(std::stoi(h.substr(i, 2), nullptr, 16)));
    return out;
}
std::string to_hex(std::string_view s) {
    static const char* d = "0123456789abcdef";
    std::string out;
    for (unsigned char c : s) out.push_back(d[c >> 4]), out.push_back(d[c & 15]);
    return out;
}
double ms_since(std::chrono::steady_clock::time_point t0) { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count(); }

std::string first_diff(const Bytes& a, const Bytes& b) {
    for (size_t i = 0; i < std::min(a.size(), b.size()); ++i)
        if (a[i] != b[i]) return "first difference at byte " + std::to_string(i) + " of " + std::to_string(a.size()) + "/" + std::to_string(b.size());
    return "sizes " + std::to_string(a.size()) + " and " + std::to_string(b.size());
}
std::string report_diff(const json& want, const json& got) {
    if (want == got) return "";
    const json patch = json::diff(want, got);
    std::string s = patch.dump();
    return s.size() > 1500 ? s.substr(0, 1500) + "..." : s;
}

// ---- inputs ----

struct Ctx {
    std::string dir;
    TypeDb bundled;
    std::map<std::string, std::unique_ptr<TypeDb>> dumps;
    const TypeDb* image = nullptr;  // from the exe, when given

    const TypeDb& types(const json& name) {
        if (name.is_null()) return bundled;
        const std::string n = name.get<std::string>();
        auto it = dumps.find(n);
        if (it == dumps.end()) {
            auto db = std::make_unique<TypeDb>();
            std::string why;
            if (!TypeDb::from_json(slurp(dir + "/" + n), db.get(), &why)) std::printf("FAIL types %s: %s\n", n.c_str(), why.c_str());
            it = dumps.emplace(n, std::move(db)).first;
        }
        return *it->second;
    }
};

std::optional<std::string> opt_str(const json& j) { return j.is_string() ? std::optional<std::string>(j.get<std::string>()) : std::nullopt; }

std::map<std::string, std::optional<ClipFacts>> facts_of(const json& j) {
    std::map<std::string, std::optional<ClipFacts>> out;
    for (auto it = j.begin(); it != j.end(); ++it) {
        if (it->is_null()) {
            out[it.key()] = std::nullopt;
            continue;
        }
        ClipFacts f;
        f.hierarchy_hash = (*it)["hierarchyHash"].get<uint64_t>();
        f.joint_count = (*it)["jointCount"].get<uint64_t>();
        f.fps = (*it)["fps"].get<uint64_t>();
        f.frame_count = (*it)["frameCount"].get<uint64_t>();
        f.loop = (*it)["loop"].get<bool>();
        f.root_motion = (*it)["rootMotion"].get<bool>();
        f.sequences = (*it)["sequences"].get<bool>();
        out[it.key()] = f;
    }
    return out;
}
ClipFactsFn facts_fn(const std::map<std::string, std::optional<ClipFacts>>& m) {
    return [&m](const std::string& g) -> std::optional<ClipFacts> {
        const auto it = m.find(g);
        return it == m.end() ? std::nullopt : it->second;
    };
}

uint32_t id_1234(const std::string&) { return 0x1234; }
uint32_t node_id_pq77(const std::vector<std::string>& p) {
    if (p.size() == 3 && ((p[1] == "p" && p[2] == "clip") || (p[1] == "q" && p[2] == "cond"))) return 77;
    return node_id(p);
}
void lossy2(int call, Bytes* out) {
    if (call == 2) out->push_back(0);
}

std::vector<ModStates> mods_of(const json& mods, const IdHooks* hooks = nullptr) {
    std::vector<ModStates> out;
    for (const json& m : mods)
        out.push_back(parse(m["text"].get<std::string>(), m["mod"].get<std::string>(), opt_str(m["side"]), opt_str(m["clients"]), m["source"].get<std::string>(), hooks));
    return out;
}

// ---- the checks ----

void codec(Ctx& c, const json& cases) {
    for (const json& k : cases["codec"]) {
        const TypeDb& db = c.types(k.value("types", json()));
        const Bytes raw = slurp_bytes(c.dir + "/" + k["graph"].get<std::string>());
        const Type* g = db.get(kGraphType);
        try {
            const Value v = Decoder(db, kMaxArray).decode(g, raw);
            const Bytes back = Encoder(db).encode(g, v);
            const Value again = Decoder(db, kMaxArray).decode(g, back);
            check("codec " + k["name"].get<std::string>() + ": decode then encode gives its " + std::to_string(raw.size()) + " bytes", back == raw, back == raw ? "" : first_diff(back, raw));
            check("codec " + k["name"].get<std::string>() + ": decodes to the same value again", equal(v, again));
        } catch (const PyError& e) {
            check("codec " + k["name"].get<std::string>(), false, e.type + ": " + e.text);
        }
    }
}

void texts(const json& cases) {
    int bad = 0;
    for (const json& t : cases["text"]) {
        const std::string b = unhex(t["bytes"].get<std::string>());
        const std::string got = utf8_replace(reinterpret_cast<const uint8_t*>(b.data()), b.size());
        if (got != t["text"].get<std::string>() && ++bad <= 5) std::printf("  utf8 %s: %s want %s\n", t["bytes"].get<std::string>().c_str(), to_hex(got).c_str(), to_hex(t["text"].get<std::string>()).c_str());
    }
    check("bytes decoded with replacement as Python does (" + std::to_string(cases["text"].size()) + " samples)", bad == 0, std::to_string(bad) + " differ");
    bad = 0;
    for (const json& t : cases["repr"]) {
        const std::string s = unhex(t["s"].get<std::string>());
        const std::string got = py_repr(std::string_view(s)), want = t["repr"].get<std::string>();
        if (got != want && ++bad) std::printf("  repr: %s want %s\n", got.c_str(), want.c_str());
    }
    check("repr() of text as Python's (" + std::to_string(cases["repr"].size()) + " samples)", bad == 0);
    for (const json& t : cases["repr_unassigned"]) {
        const std::string s = unhex(t["s"].get<std::string>());
        const std::string got = py_repr(std::string_view(s)), want = t["repr"].get<std::string>();
        std::printf("NOTE repr of unassigned code points: %s here, %s in Python%s\n", got.c_str(), want.c_str(), got == want ? " (the same)" : " (a known difference)");
    }
    bad = 0;
    for (const json& t : cases["floatrepr"]) {
        const std::string x = t["x"].get<std::string>();
        double d = 0;
        if (x == "nan") d = std::nan("");
        else if (x == "inf") d = HUGE_VAL;
        else if (x == "-inf") d = -HUGE_VAL;
        else d = std::strtod(x.c_str(), nullptr);  // hex form
        const std::string got = py_float_repr(d), want = t["repr"].get<std::string>();
        if (got != want && ++bad) std::printf("  float repr: %s want %s\n", got.c_str(), want.c_str());
    }
    check("repr() of floats as Python's (" + std::to_string(cases["floatrepr"].size()) + " samples)", bad == 0);
}

std::string hexopt(const std::optional<std::string>& s) { return s ? to_hex(*s) : std::string(); }
std::string hexj(const json& j) { return j.is_string() ? j.get<std::string>() : std::string(); }

void definitions(const json& cases) {
    for (const json& k : cases["parse"]) {
        const std::string name = "definitions " + k["name"].get<std::string>();
        IdHooks hooks;
        if (k["hook"] == "state_id_1234") hooks.state_id = id_1234;
        try {
            ModStates m = k.contains("bytes") ? load_bytes(unhex(k["bytes"].get<std::string>()), k["mod"].get<std::string>(), opt_str(k["side"]), opt_str(k["clients"]),
                                                           k["source"].get<std::string>(), &hooks)
                                              : parse(k["text"].get<std::string>(), k["mod"].get<std::string>(), opt_str(k["side"]), opt_str(k["clients"]),
                                                      k["source"].get<std::string>(), &hooks);
            if (!k.contains("states")) {
                check(name, false, "read, but the Python " + std::string(k.contains("error") ? "refuses the file: " + unhex(hexj(k["error"])) : "stops: " + unhex(hexj(k["crash"]))));
                continue;
            }
            const json& want = k["states"];
            bool ok = want.size() == m.states.size();
            std::string detail = ok ? "" : "states " + std::to_string(m.states.size()) + ", want " + std::to_string(want.size());
            for (size_t i = 0; ok && i < want.size(); ++i) {
                const State& s = m.states[i];
                const json& w = want[i];
                auto num = [](const json& j, const std::optional<double>& v) { return j.is_null() ? !v : v && j.get<double>() == *v; };
                auto boo = [](const json& j, const std::optional<bool>& v) { return j.is_null() ? !v : v && j.get<bool>() == *v; };
                const bool same = to_hex(s.name) == hexj(w["name"]) && to_hex(s.mod) == hexj(w["mod"]) && s.guid == w["guid"].get<std::string>() &&
                                  s.id == w["id"].get<uint32_t>() && hexopt(s.clip_ref) == hexj(w["clip_ref"]) && hexopt(s.clip) == hexj(w["clip"]) &&
                                  hexopt(s.like) == hexj(w["like"]) && num(w["blend_in"], s.blend_in) && boo(w["loop"], s.loop) && num(w["speed"], s.speed) &&
                                  (w["status"].is_null() ? !s.status : s.status && *s.status == w["status"].get<std::string>()) && hexopt(s.reason) == hexj(w["reason"]);
                if (!same) ok = false, detail = "state " + s.name + ": reason " + (s.reason ? *s.reason : "-") + " | want " + unhex(hexj(w["reason"]));
            }
            check(name, ok, detail);
        } catch (const DefinitionError& e) {
            if (k.contains("error")) {
                const std::string want = unhex(hexj(k["error"]));
                check(name, want == e.what(), std::string(e.what()) + " | want " + want);
            } else if (k.contains("crash")) {
                check(name + " (the Python stops: " + unhex(hexj(k["crash"])) + "; refused here)", true);
                std::printf("NOTE %s: the Python stops with %s; this reader refuses the file: %s\n", name.c_str(), unhex(hexj(k["crash"])).c_str(), e.what());
            } else {
                check(name, false, std::string("refused: ") + e.what());
            }
        }
    }
    for (const json& k : cases["manifest"]) {
        const std::string name = "manifest " + json(k["text"]).dump();
        try {
            const ManifestFields f = read_manifest_text(k["text"].get<std::string>(), "kiln.mod.json");
            if (!k.contains("fields")) {
                check(name, false, "read, but the Python " + (k.contains("error") ? "refuses: " + k["error"].get<std::string>() : "stops: " + k["crash"].get<std::string>()));
                continue;
            }
            const json& w = k["fields"];
            check(name, opt_str(w["id"]) == f.id && opt_str(w["side"]) == f.side && opt_str(w["clients"]) == f.clients);
        } catch (const DefinitionError& e) {
            if (k.contains("error")) check(name, k["error"].get<std::string>() == e.what(), std::string(e.what()) + " | want " + k["error"].get<std::string>());
            else if (k.contains("crash")) check(name + " (the Python stops: " + k["crash"].get<std::string>() + "; refused here)", true);
            else check(name, false, std::string("refused: ") + e.what());
        }
    }
}

// One merge case: the same bytes and the same report as the Python.
bool merge_case(Ctx& c, const std::string& name, const Bytes& raw, const TypeDb& db, const std::vector<ModStates>& mods, const ClipFactsFn& facts, const json& want_report,
                const json& want_bin, const MergeHooks* hooks = nullptr, bool lazy = false, const json* want_called = nullptr, double* ms = nullptr) {
    bool called = false;
    GraphSource src;
    if (lazy) src.read = [&]() {
        called = true;
        GraphRead r;
        r.kind = GraphRead::Ok;
        r.bytes = raw;
        return r;
    };
    else src.bytes = &raw;
    const auto t0 = std::chrono::steady_clock::now();
    const MergeResult r = merge(src, db, mods, facts, hooks);
    if (ms) *ms = ms_since(t0);
    const json got = json::parse(r.report.dump(-1, ' ', false, nlohmann::ordered_json::error_handler_t::replace));
    const std::string rd = report_diff(want_report, got);
    bool ok = check(name + ": the report", rd.empty(), rd);
    if (want_bin.is_null()) {
        ok &= check(name + ": no graph", !r.graph, r.graph ? "a graph of " + std::to_string(r.graph->size()) + " bytes" : "");
    } else {
        const Bytes want = slurp_bytes(c.dir + "/" + want_bin.get<std::string>());
        ok &= check(name + ": the same " + std::to_string(want.size()) + " bytes", r.graph && *r.graph == want, r.graph ? first_diff(*r.graph, want) : "no graph");
    }
    if (want_called) ok &= check(name + ": the game's graph read only when a state is left", called == want_called->get<bool>());
    return ok;
}

void merges(Ctx& c, const json& cases) {
    for (const json& k : cases["merge"]) {
        const std::string name = "merge " + k["name"].get<std::string>();
        const Bytes raw = slurp_bytes(c.dir + "/" + k["graph"].get<std::string>());
        MergeHooks hooks;
        if (k["hook"] == "node_id_pq77") hooks.ids.node_id = node_id_pq77;
        if (k["hook"] == "lossy2") hooks.after_encode = lossy2;
        if (k["hook"].is_string() && k["hook"].get<std::string>().rfind("drop:", 0) == 0) {
            const std::string list = k["hook"].get<std::string>().substr(5);
            for (size_t from = 0; from <= list.size();) {
                const size_t comma = std::min(list.find(',', from), list.size());
                hooks.drop_first.push_back(list.substr(from, comma - from));
                from = comma + 1;
            }
        }
        const auto facts = facts_of(k["facts"]);
        try {
            merge_case(c, name, raw, c.types(k["types"]), mods_of(k["mods"]), facts_fn(facts), k["report"], k["bin"], &hooks, k["lazy"].get<bool>(), &k["called"]);
        } catch (const std::exception& e) {
            check(name, false, std::string("threw: ") + e.what());
        }
    }
}

Value* node_by_id(Value& graph, uint64_t id) {
    for (Value& w : graph.at("nodeDefinitions").items)
        if (w.items[0].at("id").id() == id) return &w.items[0];
    return nullptr;
}
Value* machine_named(Value& graph, std::string_view name) {
    for (Value& w : graph.at("nodeDefinitions").items)
        if (w.items[0].at("type").is("StateMachine") && w.items[0].at("debugName").s == name) return &w.items[0];
    return nullptr;
}

void validations(Ctx& c, const json& cases) {
    for (const json& k : cases["validate"]) {
        const TypeDb& db = c.bundled;
        const Type* g = db.get(kGraphType);
        const Decoder dec(db, kMaxArray);
        const Value pristine = dec.decode(g, slurp_bytes(c.dir + "/" + k["graph"].get<std::string>()));
        const Bytes merged = slurp_bytes(c.dir + "/" + k["merged"].get<std::string>());
        auto set_of = [](const json& j) {
            std::set<uint64_t> s;
            for (const json& x : j) s.insert(x.get<uint64_t>());
            return s;
        };
        const std::set<uint64_t> machines = set_of(k["machines"]), lists = set_of(k["lists"]), selects = set_of(k["selects"]), fresh = set_of(k["new"]);
        const uint64_t reset = k["reset"].get<uint64_t>(), clip = k["clip"].get<uint64_t>(), state = k["state"].get<uint64_t>();
        std::map<std::string, std::function<void(Value&)>> changes = {
            {"none", [](Value&) {}},
            {"a game node", [](Value& v) { Value& n = v.at("nodeDefinitions").items[0].items[0]; n.at("loop").u ^= 1; }},
            {"a machine's own transitions", [](Value& v) { machine_named(v, "SM_Main")->at("globalTransitions").items[0].set("duration", Value::real(9.0)); }},
            {"a list lost an id", [&](Value& v) { auto& l = node_by_id(v, reset)->at("values").items; l.erase(l.begin()); }},
            {"the header", [](Value& v) { v.at("hasGenderMapping").u ^= 1; }},
            {"usedInputIds", [](Value& v) { auto& l = v.at("usedInputIds").at("parameterIds").items; l.erase(l.begin()); }},
            {"too many",
             [](Value& v) {
                 Value* m = machine_named(v, "SM_Main");
                 auto& gts = m->at("globalTransitions").items;
                 const size_t n = m->at("numGlobalTransitions").u;
                 for (size_t i = n; i < gts.size(); ++i) gts[i] = gts[0];
                 m->set("numGlobalTransitions", Value::integer(kMaxGlobal + 1));
             }},
            {"count", [](Value& v) { machine_named(v, "SM_Main")->at("numGlobalTransitions").u -= 1; }},
            {"shape", [&](Value& v) { node_by_id(v, clip)->at("speedController").at("rawValueController").set("extra", Value::integer(1)); }},
            {"a missing field", [&](Value& v) { node_by_id(v, state)->erase("restState"); }},
            {"a game node gone", [](Value& v) { auto& l = v.at("nodeDefinitions").items; l.erase(l.begin()); }},
        };
        for (auto it = k["results"].begin(); it != k["results"].end(); ++it) {
            Value bad = dec.decode(g, merged);
            const auto ch = changes.find(it.key());
            if (ch == changes.end()) {
                check("validate " + it.key(), false, "no such change in this test");
                continue;
            }
            ch->second(bad);
            std::string got;
            try {
                validate(db, pristine, bad, machines, lists, selects, fresh);
            } catch (const PyError& e) {
                got = e.type == "MergeOff" ? e.text : e.type + ": " + e.text;
            }
            const std::string want = it->is_null() ? "" : it->get<std::string>();
            check("validate refuses " + it.key() + (want.empty() ? " (nothing)" : ""), got == want, got + " | want " + want);
        }
    }
}

void joint_animations(Ctx& c, const json& cases) {
    for (const json& k : cases["jointanim"]) {
        const std::string b = unhex(k["bytes"].get<std::string>());
        std::string got;
        try {
            const ClipFacts f = joint_animation_facts(c.types(k["types"]), reinterpret_cast<const uint8_t*>(b.data()), b.size());
            if (k.contains("facts")) {
                const json& w = k["facts"];
                check("joint animation " + k["name"].get<std::string>(), f.hierarchy_hash == w["hierarchyHash"].get<uint64_t>() && f.joint_count == w["jointCount"].get<uint64_t>() &&
                                                                              f.fps == w["fps"].get<uint64_t>() && f.frame_count == w["frameCount"].get<uint64_t>() &&
                                                                              f.loop == w["loop"].get<bool>());
                continue;
            }
            got = "facts";
        } catch (const PyError& e) {
            got = e.type + ": " + e.text;
        }
        const std::string want = k.contains("error") ? k["error"].get<std::string>() : "facts";
        check("joint animation " + k["name"].get<std::string>(), got == want, got + " | want " + want);
    }
}

void fuzz(Ctx& c, const json& cases) {
    const auto facts = facts_of(cases["fuzz_facts"]);
    const std::vector<ModStates> mods = {parse(R"({"format": 1, "states": {"a": {"clip": "x", "like": "Parry_FullBody"}}})", "m", "both", "mod")};
    const TypeDb& db = c.bundled;
    const Type* g = db.get(kGraphType);
    int decodes = 0, same_verdict = 0, reports = 0, reason_only = 0, n = 0;
    for (const json& k : cases["fuzz"]) {
        ++n;
        const Bytes raw = slurp_bytes(c.dir + "/" + k["graph"].get<std::string>());
        bool dec_ok = false, rt = false;
        std::string err;
        try {
            const Value v = Decoder(db, kMaxArray).decode(g, raw);
            dec_ok = true;
            try {
                rt = Encoder(db).encode(g, v) == raw;
            } catch (const PyError&) {
            }
        } catch (const PyError& e) {
            err = e.type;
        }
        decodes += dec_ok;
        const bool verdict = dec_ok == k["decodes"].get<bool>() && (!dec_ok || rt == k["roundtrip"].get<bool>());
        same_verdict += verdict;
        if (!verdict)
            std::printf("  fuzz %s: decodes %d roundtrip %d (%s); the Python: decodes %d roundtrip %d (%s)\n", k["graph"].get<std::string>().c_str(), dec_ok, rt, err.c_str(),
                        k["decodes"].get<bool>(), k.value("roundtrip", false), k.value("decode_error", "").c_str());
        if (k.contains("crash")) continue;
        const MergeResult r = merge(GraphSource{&raw, {}}, db, mods, facts_fn(facts));
        json got = json::parse(r.report.dump(-1, ' ', false, nlohmann::ordered_json::error_handler_t::replace)), want = k["report"];
        const bool bytes_same = k["bin"].is_null() ? !r.graph : r.graph && *r.graph == slurp_bytes(c.dir + "/" + k["bin"].get<std::string>());
        if (got == want && bytes_same) {
            ++reports;
            continue;
        }
        // the same verdict, another wording of a malformed graph's reason (the report lists the known ones)
        json g2 = got, w2 = want;
        g2.erase("reason"), w2.erase("reason");
        for (auto& s : g2["states"]) s.erase("reason");
        for (auto& s : w2["states"]) s.erase("reason");
        if (g2 == w2 && bytes_same) {
            ++reason_only;
            std::printf("  fuzz %s: reason \"%s\" | the Python's \"%s\"\n", k["graph"].get<std::string>().c_str(), got["reason"].dump().c_str(), want["reason"].dump().c_str());
        } else {
            std::printf("  fuzz %s: %s\n", k["graph"].get<std::string>().c_str(), report_diff(want, got).c_str());
        }
    }
    check("fuzz: " + std::to_string(n) + " graphs with changed bytes decode (" + std::to_string(decodes) + ") and round-trip as the Python's do", same_verdict == n,
          std::to_string(n - same_verdict) + " differ");
    check("fuzz: the merge's report and bytes are the Python's", reports + reason_only == n,
          std::to_string(n - reports - reason_only) + " differ (" + std::to_string(reason_only) + " in the words of the reason only)");
    std::printf("NOTE fuzz: %d of %d reports identical, %d differ in the words of the reason only\n", reports, n, reason_only);
}

// ---- the exe, mapped by hand (as native/services/types/types_test.cpp does) ----

struct Mapped {
    std::vector<uint8_t> mem;
    ktypes::Image im;
};
bool map_pe(const std::string& path, Mapped& m) {
    const std::string raw = slurp(path);
    auto u16 = [&](size_t o) { uint16_t v = 0; if (o + 2 <= raw.size()) std::memcpy(&v, raw.data() + o, 2); return v; };
    auto u32 = [&](size_t o) { uint32_t v = 0; if (o + 4 <= raw.size()) std::memcpy(&v, raw.data() + o, 4); return v; };
    auto u64 = [&](size_t o) { uint64_t v = 0; if (o + 8 <= raw.size()) std::memcpy(&v, raw.data() + o, 8); return v; };
    if (raw.size() < 0x40 || u16(0) != 0x5a4d) return false;
    const size_t pe = u32(0x3c);
    if (u32(pe) != 0x4550 || u16(pe + 4) != 0x8664) return false;
    const size_t nsec = u16(pe + 6), opt = pe + 24, optsz = u16(pe + 20);
    if (!nsec || nsec > 96 || optsz < 0xf0 || u16(opt) != 0x20b) return false;
    const uint64_t base = u64(opt + 24);
    const uint32_t image_size = u32(opt + 56);
    if (image_size < 0x1000 || image_size > (1u << 30)) return false;
    m.mem.assign(image_size, 0);
    m.im.base = base;
    m.im.all = {base, base + image_size};
    for (size_t i = 0; i < nsec; ++i) {
        const size_t s = opt + optsz + 40 * i;
        if (s + 40 > raw.size()) return false;
        char name[9] = {};
        std::memcpy(name, raw.data() + s, 8);
        const uint32_t vsize = u32(s + 8), va = u32(s + 12), rsize = u32(s + 16), rptr = u32(s + 20);
        const uint32_t length = vsize ? vsize : rsize, n = std::min(length, rsize);
        if (!length || va > image_size || length > image_size - va || size_t{rptr} + rsize > raw.size()) return false;
        if (n) std::memcpy(m.mem.data() + va, raw.data() + rptr, n);
        if (!std::strcmp(name, ".text")) m.im.text = {base + va, base + va + length};
        if (!std::strcmp(name, ".rdata")) m.im.rdata = {base + va, base + va + length};
    }
    m.im.mem = m.mem.data();
    return m.im.valid();
}

// Every type of the bundled layouts (the graph's, the clips', and every node type's closure) as the exe's table has it.
void compare_types(const TypeDb& bundled, const TypeDb& image) {
    int differ = 0;
    for (const Type& t : bundled.types()) {
        const Type* u = image.by_name(t.name);
        std::string why;
        if (!u) why = "missing";
        else if (u->hash != t.hash || u->size != t.size || u->align != t.align || u->kind != t.kind || u->count != t.count || u->has_inner != t.has_inner || u->inner != t.inner)
            why = "header";
        else if (u->fields.size() != t.fields.size() || u->values.size() != t.values.size())
            why = "counts";
        else {
            for (size_t i = 0; i < t.fields.size() && why.empty(); ++i)
                if (u->fields[i].name != t.fields[i].name || u->fields[i].type != t.fields[i].type || u->fields[i].has_type != t.fields[i].has_type ||
                    u->fields[i].offset != t.fields[i].offset)
                    why = "field " + t.fields[i].name;
            for (size_t i = 0; i < t.values.size() && why.empty(); ++i)
                if (u->values[i].name != t.values[i].name || u->values[i].value != t.values[i].value) why = "value " + t.values[i].name;
        }
        if (!why.empty() && ++differ <= 10) std::printf("  type %s: %s\n", t.name.c_str(), why.c_str());
    }
    check("types: the exe's table and the bundled layouts agree on all " + std::to_string(bundled.size()) + " types the graph, the clips and the nodes use",
          differ == 0, std::to_string(differ) + " differ");
}

void game(Ctx& c, const json& cases, bool timing_only) {
    const json& gm = cases["game"];
    if (gm.is_null()) {
        std::printf("SKIP the game's graph: none (KILN_PLAYER_GRAPH)\n");
        return;
    }
    const Bytes raw = slurp_bytes(gm["graph"].get<std::string>());
    if (raw.empty()) {
        std::printf("SKIP the game's graph: %s is not there\n", gm["graph"].get<std::string>().c_str());
        return;
    }
    const auto facts = facts_of(gm["facts"]);
    std::vector<std::pair<std::string, const TypeDb*>> sources = {{"bundled layouts", &c.bundled}};
    if (c.image) sources.push_back({"the exe's layouts", c.image});
    for (const auto& [src, db] : sources) {
        const Type* g = db->get(kGraphType);
        const auto t0 = std::chrono::steady_clock::now();
        const Value v = Decoder(*db, kMaxArray).decode(g, raw);
        const double dms = ms_since(t0);
        const auto t1 = std::chrono::steady_clock::now();
        const Bytes back = Encoder(*db).encode(g, v);
        const double ems = ms_since(t1);
        if (!timing_only) check("game graph (" + src + "): decode then encode gives its " + std::to_string(raw.size()) + " bytes", back == raw, back == raw ? "" : first_diff(back, raw));
        std::printf("TIME game graph (%s): decode %.1f ms, encode %.1f ms\n", src.c_str(), dms, ems);
        for (const json& k : gm["cases"]) {
            const std::string name = "game (" + src + ") " + k["name"].get<std::string>();
            const std::vector<ModStates> mods = mods_of(k["mods"]);
            if (timing_only) {
                double best = 1e9;
                for (int i = 0; i < 5; ++i) {
                    const auto t = std::chrono::steady_clock::now();
                    const MergeResult r = merge(GraphSource{&raw, {}}, *db, mods, facts_fn(facts));
                    best = std::min(best, ms_since(t));
                }
                std::printf("TIME merge, %s (%s): %.1f ms (best of 5)\n", k["name"].get<std::string>().c_str(), src.c_str(), best);
                continue;
            }
            double ms = 0;
            merge_case(c, name, raw, *db, mods, facts_fn(facts), k["report"], k["bin"], nullptr, false, nullptr, &ms);
        }
    }
}

}  // namespace

int main(int argc, char** argv) {
    int a = 1;
    const bool timing = argc > 1 && std::string(argv[1]) == "--time";
    if (timing) ++a;
    if (a >= argc) {
        std::fprintf(stderr, "usage: test_playeranim [--time] <cases dir> [<game exe>]\n");
        return 2;
    }
    Ctx c;
    c.dir = argv[a];
    const json cases = json::parse(slurp(c.dir + "/cases.json"));
    std::string why;
    if (!TypeDb::from_json(slurp(cases["types"].get<std::string>()), &c.bundled, &why)) {
        std::printf("FAIL bundled types: %s\n", why.c_str());
        return 1;
    }
    std::unique_ptr<Mapped> mapped;
    TypeDb image;
    if (a + 1 < argc) {
        const std::string exe = argv[a + 1];
        mapped = std::make_unique<Mapped>();
        if (!std::ifstream(exe, std::ios::binary)) {
            std::printf("SKIP the exe's type table: no file at %s\n", exe.c_str());
        } else if (!map_pe(exe, *mapped)) {
            check("the exe maps", false, exe);
        } else {
            const auto t0 = std::chrono::steady_clock::now();
            const bool ok = TypeDb::from_image(mapped->im, &image, &why);
            std::printf("TIME type table from the exe: %.1f ms, %zu types\n", ms_since(t0), image.size());
            if (check("types: the exe's type table reads", ok, why)) {
                c.image = &image;
                if (!timing) compare_types(c.bundled, image);
            }
        }
    } else if (!timing) {
        std::printf("SKIP the exe's type table: no exe given\n");
    }
    if (!timing) {
        codec(c, cases);
        texts(cases);
        definitions(cases);
        merges(c, cases);
        validations(c, cases);
        joint_animations(c, cases);
        fuzz(c, cases);
    }
    game(c, cases, timing);
    if (timing) return 0;
    std::printf("%d passed, %d failed\n", g_passed, g_failed);
    return g_failed ? 1 : 0;
}
