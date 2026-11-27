// SPDX-License-Identifier: MIT
// kiln.content registry stage: the stage runner (stage.h).
#include "stage.h"
#include "saves.h"
#include "archive_journal.h"
#include "unmapped.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <cstdio>
#include <functional>
#include <iterator>

#include "services/kiln_caps.h"
#include "../../../core/src/services/boot_gamedata.h"
#include "schema_ir.h"

namespace kc {  // content.h's (packs.cpp), declared here
std::string guid_text(const uint8_t g[16]);
bool guid_parse(const std::string& s, uint8_t out[16]);
}  // namespace kc

namespace kc::registry::stage {

namespace {

std::string lc(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

std::string sha_hex(const std::string& s) { return kiln::boot_gd::hex_of(kiln::boot_gd::sha256(s)); }

const char* applies_word(Applies a) { return a == Applies::Now ? "now" : a == Applies::WorldLoad ? "worldLoad" : "restart"; }

static_assert(std::size(gen::kRegistries) + std::size(gen::kFields) < KILN_CAP_OWNER_MAX);

// In increasing order of registry coverage. A registry is available if any of its fields is verified here.
enum class CapCoverage { WrongSide, Unmapped, Unverified, Verified };

const char* only_side(gen::Side side) {
    if (side == gen::Side::Client) return "client";
    if (side == gen::Side::Server) return "server";
    return nullptr;
}

std::string field_cap_name(const std::string& prefix, const char* path) {
    std::string name = prefix + "." + path;
    for (size_t pos = name.find("[]"); pos != std::string::npos; pos = name.find("[]", pos)) name.erase(pos, 2);
    return name;
}

// ---- the decoded base in the cache: values with their kinds kept ----
json value_out(const Value& v) {
    switch (v.kind) {
        case Value::Int: return {{"i", v.i}};
        case Value::Num: return {{"d", v.d}};
        case Value::Bool: return {{"b", v.b}};
        case Value::Text: return {{"t", v.t}};
        case Value::List: {
            json a = json::array();
            for (const Value& x : v.list) a.push_back(value_out(x));
            return {{"l", a}};
        }
        case Value::Struct: {
            json a = json::array();
            for (const Value& x : v.list) a.push_back(value_out(x));
            return {{"k", v.keys}, {"s", a}};
        }
        default: return nullptr;
    }
}
bool value_in(const json& j, Value* v) {
    *v = Value{};
    if (j.is_null()) return true;
    if (!j.is_object()) return false;
    if (j.contains("i") && j["i"].is_number_integer()) return *v = Value::integer(j["i"].get<int64_t>()), true;
    if (j.contains("d") && j["d"].is_number()) return *v = Value::number(j["d"].get<double>()), true;
    if (j.contains("b") && j["b"].is_boolean()) return *v = Value::boolean(j["b"].get<bool>()), true;
    if (j.contains("t") && j["t"].is_string()) return *v = Value::text(j["t"].get<std::string>()), true;
    if (j.contains("l") && j["l"].is_array()) {
        std::vector<Value> out;
        for (const auto& x : j["l"]) {
            Value e;
            if (!value_in(x, &e)) return false;
            out.push_back(std::move(e));
        }
        return *v = Value::of_list(std::move(out)), true;
    }
    if (j.contains("k") && j.contains("s") && j["k"].is_array() && j["s"].is_array() && j["k"].size() == j["s"].size()) {
        v->kind = Value::Struct;
        for (size_t i = 0; i < j["k"].size(); ++i) {
            Value e;
            if (!j["k"][i].is_string() || !value_in(j["s"][i], &e)) return false;
            v->keys.push_back(j["k"][i].get<std::string>());
            v->list.push_back(std::move(e));
        }
        return true;
    }
    return false;
}

json base_out(const BaseModel& b) {
    json regs = json::object();
    for (const auto& [reg, es] : b.registries) {
        json a = json::array();
        for (const BaseModel::Entry& e : es) {
            json f = json::object();
            for (const auto& [k, v] : e.fields) f[k] = value_out(v);
            json row = {{"id", e.id}, {"fields", f}};
            if (!e.traits.empty()) row["traits"] = e.traits;
            if (!e.removed_in.empty()) row["removedIn"] = e.removed_in;
            if (!e.drop_groups.empty()) row["dropGroups"] = e.drop_groups;
            a.push_back(std::move(row));
        }
        regs[reg] = std::move(a);
    }
    json cover = json::object();
    for (const auto& [reg, m] : b.raw_cover)
        for (const auto& [k, c] : m) cover[reg][k] = {{"field", c.field}, {"converted", c.converted}};
    return {{"entryMetadata", 1}, {"registries", regs}, {"rawCover", cover}, {"retired", b.retired}};
}
bool base_in(const json& j, BaseModel* b) {
    if (!j.is_object() || j.value("entryMetadata", 0) != 1 || !j.contains("registries") || !j["registries"].is_object()) return false;
    for (auto it = j["registries"].begin(); it != j["registries"].end(); ++it) {
        auto& dst = b->registries[it.key()];
        for (const auto& row : it.value()) {
            BaseModel::Entry e;
            if (!row.contains("id") || !row["id"].is_string()) return false;
            e.id = row["id"].get<std::string>();
            for (auto f = row["fields"].begin(); f != row["fields"].end(); ++f) {
                Value v;
                if (!value_in(f.value(), &v)) return false;
                e.fields[f.key()] = std::move(v);
            }
            if (row.contains("traits")) e.traits = row["traits"].get<std::set<std::string>>();
            if (row.contains("removedIn")) e.removed_in = row["removedIn"].get<std::string>();
            if (row.contains("dropGroups")) e.drop_groups = row["dropGroups"].get<std::vector<std::pair<size_t, size_t>>>();
            dst.push_back(std::move(e));
        }
    }
    if (j.contains("rawCover"))
        for (auto r = j["rawCover"].begin(); r != j["rawCover"].end(); ++r)
            for (auto k = r.value().begin(); k != r.value().end(); ++k)
                b->raw_cover[r.key()][k.key()] = {k.value().value("field", std::string()), k.value().value("converted", false)};
    if (j.contains("retired")) b->retired = j["retired"].get<std::set<std::string>>();
    return true;
}

json need_json(const model::Need& n) {
    return {{"all", n.all}, {"fields", n.fields}, {"whole", n.whole}, {"raw", n.raw}, {"traits", n.traits}, {"registries", n.registries}};
}

std::string key_text(const KilnAssetKey& k) {
    char b[32];
    std::snprintf(b, sizeof b, "/%08x/%u", static_cast<unsigned>(k.type), static_cast<unsigned>(k.part));
    return guid_text(k.guid) + b;
}
bool key_parse(const std::string& s, KilnAssetKey* k) {
    if (s.size() < 38 || s[36] != '/') return false;
    if (!guid_parse(s.substr(0, 36), k->guid)) return false;
    const size_t slash = s.find('/', 37);
    if (slash == std::string::npos) return false;
    k->type = static_cast<uint32_t>(std::stoul(s.substr(37, slash - 37), nullptr, 16));
    k->part = static_cast<uint32_t>(std::stoul(s.substr(slash + 1)));
    return true;
}

// ---- plans (registry-rollback) ----
bool is_hash(const std::string& s) {
    return s.size() == 64 && std::all_of(s.begin(), s.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}
// A plan's name: the SHA-256 of its document without "previous" (the link to the last good plan, which a later start may
// rewrite; the content it names never changes).
std::string plan_hash_of(json plan) {
    plan.erase("previous");
    return sha_hex(plan.dump());
}
bool has_registry_row(const FieldOutcome& fo) {
    return std::any_of(fo.chain.begin(), fo.chain.end(), [](const ChainRow& c) { return c.kind == WriterKind::Registry; });
}
// "items[game:plank].maxStack" (FieldRef::text) back into its parts. false: not that shape.
bool field_ref_of(const std::string& text, FieldRef* out) {
    const size_t open = text.find('['), close = text.find("].");
    if (open == std::string::npos || close == std::string::npos || close < open) return false;
    *out = {text.substr(0, open), text.substr(open + 1, close - open - 1), text.substr(close + 2)};
    return !out->registry.empty() && !out->id.empty() && !out->field.empty();
}
std::string short_hash(const std::string& h) { return h.substr(0, 12); }
// registry.json's text (the start's report, a live one, report_json's): a byte that is not UTF-8 (a label a plugin gave a code
// writer) is written as U+FFFD; nlohmann's default throws, which would leave the file stale on the report writer's thread and
// throw across the kiln.registry C ABI from report_json (review of Q1).
std::string report_dump(const json& doc, int indent = -1) { return doc.dump(indent, ' ', false, json::error_handler_t::replace); }

}  // namespace

// ---- the base cache's key (review finding 5) ----
std::string keys_digest(const std::vector<KilnAssetKey>& keys) {
    std::string s;
    for (const KilnAssetKey& k : keys) s += key_text(k) + "\n";
    return sha_hex(s);
}
// Every type the model lists, with a digest of its keys: a resource another archive writer adds (or removes) misses the cache.
class Inventory final : public model::Source {
   public:
    explicit Inventory(model::Source& in) : in_(in) {}
    const char* name() const override { return in_.name(); }
    bool list(const std::string& type, std::vector<KilnAssetKey>* out, std::string* why) override {
        const bool ok = in_.list(type, out, why);
        if (ok) lists_[type] = keys_digest(*out);
        return ok;
    }
    bool count(const model::Place& p, size_t* n, std::string* why) override { return in_.count(p, n, why); }
    bool member(const model::Place& p, const std::string& path, json* out, std::string* why) override { return in_.member(p, path, out, why); }
    std::string digest() override { return in_.digest(); }
    std::vector<std::pair<KilnAssetKey, std::string>> keys_read() const override { return in_.keys_read(); }
    bool touch(const KilnAssetKey& key, const std::string& type, std::string* why) override { return in_.touch(key, type, why); }
    void release() override { in_.release(); }
    const std::map<std::string, std::string>& lists() const { return lists_; }

   private:
    model::Source& in_;
    std::map<std::string, std::string> lists_;
};
// The loaded regmap's content (common rows and the build's record): a regenerated record under the same version string misses.
std::string regmap_digest(const map::RegMap& rm) {
    json j = json::object();
    for (const auto& [n, r] : rm.registries) {
        const map::EntrySource& e = r.entries;
        json rows = json::object();
        for (const auto& [f, row] : r.rows)
            rows[f] = {row.paths, row.conv, row.values, row.formula, row.config, static_cast<int>(row.read), static_cast<int>(row.backend),
                       static_cast<int>(row.state), row.client_needs};
        json sources = json::array();
        for (const map::EntrySource& s : e.sources) {
            json one = {s.resource_type, s.array, s.id, s.unnamed};
            if (!s.used_by_array.empty()) one.push_back({s.used_by_array, s.used_by_index, s.used_by_id});  // what names the elements
            sources.push_back(one);
        }
        j["registries"][n] = {{"entries", {static_cast<int>(e.kind), e.resource_type, e.array, e.resource_guid, e.name, e.guid, e.id, e.unnamed, e.name_from, e.key}},
                              {"rows", rows}};
        if (!sources.empty()) j["registries"][n]["sources"] = sources;  // kind arrays only: other registries' digests stay as they were
    }
    if (rm.has_build) {
        json rows = json::object();
        for (const auto& [k, v] : rm.build.rows) rows[k] = static_cast<int>(v);
        j["build"] = {{"side", rm.build.side}, {"exe", rm.build.exe_sha256}, {"gameBuild", rm.build.game_build}, {"verified", rm.build.verified},
                      {"regmapDigest", rm.build.regmap_digest}, {"commonDigest", rm.common_digest},
                      {"rows", rows}, {"layouts", rm.build.layouts}, {"pinned", rm.build.aliases.pinned}, {"removed", rm.build.aliases.removed}};
    }
    return sha_hex(j.dump());
}

// Stable full models share a family across mod sets; raw requests are never serialized into this cache.
std::string cache_family(const Env& env, const map::RegMap& rm, const model::Need& need) {
    const json request = need.all && need.raw.empty() ? json{{"all", true}} : need_json(need);
    return sha_hex(std::string(model::kDecoderVersion) + "\nentry-metadata-1\n" + env.kiln_version + "\n" + env.side + "\n" + env.exe_sha256 + "\n" +
        (rm.has_build ? (rm.build.verified ? "verified" : "unverified") : "none") + "\n" + regmap_digest(rm) + "\n" + request.dump());
}
std::string cache_path(const std::string& family) { return "cache/" + family.substr(0, 32) + ".json"; }
bool read_base_cache(const Env& env, const std::string& family, model::Source& source, LiveData* out) {
    if (!env.read_data) return false;
    const auto text = env.read_data(cache_path(family));
    if (!text) return false;
    try {
        const json c = json::parse(*text, nullptr, false);
        if (!c.is_object() || c.value("family", std::string()) != family || !c.at("keys").is_array() || !c.at("lists").is_object()) return false;
        for (auto it = c["lists"].begin(); it != c["lists"].end(); ++it) {
            std::vector<KilnAssetKey> now; std::string why;
            if (!it.value().is_string() || !source.list(it.key(), &now, &why) || keys_digest(now) != it.value().get<std::string>()) return false;
        }
        for (const auto& k : c["keys"]) {
            KilnAssetKey key{}; std::string why;
            if (!k.is_array() || k.size() != 2 || !key_parse(k[0].get<std::string>(), &key) || !source.touch(key, k[1].get<std::string>(), &why)) return false;
        }
        LiveData data;
        if (source.digest() != c.value("digest", std::string()) || !base_in(c.at("base"), &data.base)) return false;
        data.ids = c.at("ids").get<std::map<std::string, std::string>>();
        data.nums = c.at("nums").get<std::map<std::string, uint32_t>>();
        data.decoded = c.at("decoded").get<std::set<std::string>>();
        *out = std::move(data);
        if (env.log) env.log(0, "registry: the base of this start is the one the cache holds (" + std::to_string(c["keys"].size()) +
            " resource(s), digest " + c.value("digest", std::string()).substr(0, 16) + "): not decoded again");
        return true;
    } catch (const std::exception&) { return false; } // damaged cache is a miss, never a partial model
}
void write_base_cache(const Env& env, const std::string& family, model::Source& source, const Inventory& inventory,
                      const BaseModel& base, const std::map<std::string, std::string>& ids,
                      const std::map<std::string, uint32_t>& nums, const std::set<std::string>& decoded) {
    if (!env.write_data) return;
    const std::string digest = source.digest();
    if (digest.empty()) return;
    json keys = json::array();
    for (const auto& [key, type] : source.keys_read()) keys.push_back({key_text(key), type});
    env.write_data(cache_path(family), json{{"format", 1}, {"family", family}, {"digest", digest}, {"keys", keys},
        {"lists", inventory.lists()}, {"base", base_out(base)}, {"ids", ids}, {"nums", nums}, {"decoded", decoded}}.dump());
}

// ---- inputs ----
void refuse_unmapped(const map::RegMap& rm, const Schema& schema, Batch* b, std::vector<Refusal>* refused,
                     const std::function<bool(const std::string&, bool, std::string*)>& support = {}) {
    refuse_unmapped_edits(rm, schema, b, refused, bool(support));
    if (!support) return;
    std::erase_if(b->edits, [&](const Edit& edit) {
        if (edit.entry == EntryOp::None) return false;
        std::string reason;
        if (support(edit.registry, edit.entry == EntryOp::Add, &reason)) return false;
        Refusal row{b->mod, b->source, b->stage, edit.n, reason, "", edit.registry, edit.add_id, ""};
        if (edit.entry == EntryOp::Add) row.entry = edit.registry + "|" + edit.add_id;
        else if (edit.select.exact()) row.id = edit.select.id;
        refused->push_back(std::move(row)); return true;
    });
}

std::vector<Env::DataFile> read_sections(const std::string& mod, const std::string& version,
                                         const std::vector<std::pair<std::string, std::string>>& files, const Schema& schema) {
    std::vector<Env::DataFile> out;
    std::vector<std::pair<std::string, std::string>> sorted = files;
    std::sort(sorted.begin(), sorted.end());
    for (const auto& [rel, text] : sorted) {
        if (text.find("\"registry\"") == std::string::npos) continue;
        const json j = json::parse(text, nullptr, false, true);
        if (!j.is_object() || !j.contains("registry")) continue;  // not JSON: the packs' reader says so at world load
        Env::DataFile f;
        f.mod = mod, f.version = version, f.rel = rel;
        if (!lower_section(j["registry"], mod, version, rel, schema, &f.batches, &f.refused, &f.error) && f.error.empty())
            f.error = "the registry section cannot be read";
        out.push_back(std::move(f));
    }
    return out;
}

// ---- the runner ----
struct Runner::Impl {
    Env env;
    mutable std::mutex mu;
    Schema schema = generated_schema();
    map::RegMap rm;
    std::vector<std::string> rm_errs;
    bool rm_loaded = false;
    std::vector<std::pair<std::string, std::string>> order;  // Kiln.Host's (set_order)
    // mod -> kiln.mod.json side ("server", "client"; absent or "both": both), from set_order or the mod's data files. The join
    // fingerprint (bootSync) leaves out fields a one-side mod writes: the other side never runs it (kiln.boot skips such mods too).
    std::map<std::string, std::string> sides;
    bool registry_users = false;  // set_order named a mod whose code uses the registry API (review4 NS1)
    bool order_set = false;
    struct Sub {
        uint64_t owner = 0;
        std::string mod;
        StageFn fn;
    };
    std::map<uint64_t, Sub> stage_subs;
    struct Ready {
        uint64_t owner = 0;
        ReadyFn fn;
    };
    std::map<uint64_t, Ready> ready_subs;
    uint64_t next_handle = 1;
    struct Queued {
        uint64_t owner = 0;
        Batch batch;
        std::vector<Refusal> refused;
    };
    std::map<std::string, std::vector<Batch>> recorded; // successful callback turns kept for live recomposition
    std::map<std::string, std::vector<Queued>> queued;  // mod -> its submits, retaining each caller's owner
    std::set<uint64_t> gone;  // owner ids are never reused
    bool alive(uint64_t owner) const { return !gone.count(owner) && (!env.owner_alive || env.owner_alive(owner)); }
    std::thread::id stage_thread;
    bool stage_thread_set = false;
    struct Turn {
        std::string mod;
        Stage stage = Stage::Alter;
        const View* view = nullptr;
        Batch* out = nullptr;
    };
    std::optional<Turn> turn;
    // the stage's result
    BaseModel base;
    Result result, live_result;
    bool full_live = false, installing_live = false;
    std::unique_ptr<model::Source> lazy_source;
    OldWritesFn lazy_old;
    std::string lazy_error;
    std::vector<OldWrite> old;
    std::vector<ModInput> inputs;
    std::vector<Refusal> pre_refused;
    std::vector<Refusal> outside;                    // every refusal made outside compose (the live back-end keeps them on a reload)
    std::map<FieldRef, FieldOutcome> boot_fields;    // restart fields as the boot back-end wrote them
    std::map<std::string, std::string> ids;
    std::map<std::string, uint32_t> nums;
    std::set<std::string> decoded;
    uint64_t generation = 0;
    Counts counts;
    std::string report;
    ReportMeta meta;
    // Live reports are made off the world tick (owner 11-26, registry-followups review Q1): a publish keeps its result and
    // meta as one job; env.post_report's thread makes its JSON and state/registry.json (a newer job replaces one not yet
    // written), and report_json() makes the text itself when asked before that thread got to it.
    struct ReportJob {
        uint64_t seq = 0;
        std::shared_ptr<const Result> result;
        ReportMeta meta;
    };
    std::shared_ptr<const ReportJob> report_job;  // the newest live report; null: the start's report is the newest
    uint64_t report_seq = 0;                      // the newest report (the start's or a live one)
    uint64_t report_built = 0;                    // the report whose text `report` holds
    std::mutex write_mu;                          // one writer of state/registry.json at a time (taken before mu, never after)
    uint64_t report_written = 0;                  // under write_mu: the newest report in the file
    // The JSON of a live report: outside mu (the schema does not change after the start; the job is its own copy).
    json report_doc(const ReportJob& job) const { return ::kc::registry::report_json(schema, *job.result, job.meta); }
    // Writes the newest live report to state/registry.json (env.post_report's thread, or the publishing one without it).
    void write_live_report() {
        std::lock_guard w(write_mu);
        std::shared_ptr<const ReportJob> job;
        bool built = false;
        {
            std::lock_guard l(mu);
            job = report_job;
            built = job && job->seq <= report_built;  // report_json made its text already
        }
        if (!job || job->seq <= report_written) return;  // an earlier run wrote it, or the start wrote a newer one
        const json doc = report_doc(*job);
        // The text outside mu (review of Q1): the tick takes mu each step (status, reads, the next publish), and a large
        // report's dump takes tens of milliseconds (51 ms at 10,000 fields on the host bench).
        if (!built) {
            std::string text = report_dump(doc);
            std::lock_guard l(mu);
            if (job->seq > report_built) report = std::move(text), report_built = job->seq;
        }
        if (env.write_report && !env.write_report(report_dump(doc, 1))) log(1, "registry: state/registry.json could not be written");
        report_written = job->seq;
    }
    std::vector<BootResource> boot;
    // front-ends (compose.h OldWrite::front): this start's candidates ("<mod>/<key>", the resource's type) and what became of each
    std::vector<std::pair<std::string, std::string>> front_candidates;
    std::vector<FrontEnd> fronts;
    std::map<std::string, uint64_t> edits_by_mod;
    // plans (registry-rollback): plans/index.json as this start read it (the last good plan, the plan the last world ran on),
    // and the plan this start runs on when a rollback applied (its hash and its own "previous")
    json index = json::object();
    bool index_unreadable = false;  // plans/index.json could not be read at this start: which plans to keep is not known
    std::string rollback, rollback_previous;
    std::vector<std::string> left_out;  // a rolled-back start's mods the plan does not hold (registry.json rollback.leftOut)
    // registry.json "saves" (saves.h): this start's notes from the stage itself (the max stack against the plan the last world
    // ran on), and the lines already in kiln.log (each once per start)
    std::vector<saves::SaveNote> stage_notes;
    std::set<std::string> notes_logged;
    json saves_json(const Result& r) {
        std::vector<saves::SaveNote> all = stage_notes;
        for (saves::SaveNote& n : saves::notes(r)) all.push_back(std::move(n));
        std::vector<std::string> lines;
        for (const saves::SaveNote& n : all) {
            const std::string line = "registry: saves: " + n.field.text() + " " + n.text + " [" + n.decision + "]";
            if (notes_logged.insert(line).second) log(n.decision == "report" ? 0 : 1, line);
        }
        return all.empty() ? json(nullptr) : saves::notes_json(all);
    }
    // the archive writer's change journal, read once per start (archive_journal.h)
    std::optional<ArchiveJournal> journal;
    // Names the journal's packages on base's entries (ids: the model's id table; m: the model, for an array registry's
    // elements when format 2's paths name them) and records it for registry.json's base.
    void name_archive(BaseModel* base, const std::map<std::string, std::string>& ids, model::Model* m) {
        if (!journal) {
            journal.emplace();
            std::string generation, why;
            const std::optional<std::string> text = env.archive_journal ? env.archive_journal(&generation, &why) : std::nullopt;
            if (text) *journal = parse_archive_journal(*text, generation);
            else journal->text = why.empty() ? "this start's archive writer keeps no change journal: what it changed is part of the base and not named" : why;
            if (journal->state == "stale" || journal->state == "unreadable") log(1, "registry: " + journal->text);
        }
        // only a registry whose entries are each a whole resource can be named from the journal's resource keys; an array
        // registry's entries (elements of one shared resource) only from format 2's paths, which say which element
        std::set<std::string> whole;
        std::string shared;
        ArchiveFields fields;
        std::set<std::string> element_types;  // resource types some registry's entries are elements of
        for (const auto& [name, rmap] : rm.registries) {
            if (rmap.entries.kind == map::EntryKind::Array) element_types.insert(rmap.entries.resource_type);
            for (const map::EntrySource& src : rmap.entries.sources) element_types.insert(src.resource_type);
        }
        for (const auto& [name, rmap] : rm.registries) {
            // a singleton that is the head of such a resource (lootRules in the loot registry's resource): only from paths
            if (rmap.entries.kind == map::EntryKind::Single && element_types.count(rmap.entries.resource_type)) {
                fields.partial.insert(name);
                shared += (shared.empty() ? "" : ", ") + name;
            }
            if (rmap.entries.kind != map::EntryKind::Array && rmap.entries.kind != map::EntryKind::Arrays) whole.insert(name);
            else
                shared += (shared.empty() ? "" : ", ") + name,
                    fields.array[name] = rmap.entries.kind == map::EntryKind::Arrays && !rmap.entries.sources.empty() ? rmap.entries.sources[0].array
                                                                                                                    : rmap.entries.array;
            for (const auto& [field, row] : rmap.rows)  // a template's: any write in its component list (archive_paths_of)
                if (field.find("[]") == std::string::npos) fields.rows[name][field] = archive_paths_of(rmap.entries.resource_type, row.paths);
        }
        // an array registry's entries by their resource and element, listed only when a journal path names an element
        std::map<std::string, std::map<std::string, std::map<int32_t, std::string>>> elements;
        std::set<std::string> listed;
        fields.elements = [&](const std::string& reg, const std::string& guid) -> const std::map<int32_t, std::string>* {
            if (!m) return nullptr;
            if (listed.insert(reg).second) {
                std::string why;
                if (const std::vector<model::Entry>* es = m->entries(reg, &why))
                    for (const model::Entry& e : *es) {
                        if (e.place.element < 0) continue;
                        std::string g = guid_text(e.place.key.guid);
                        for (char& c : g) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
                        elements[reg][g][e.place.element] = e.id;
                    }
            }
            const auto r = elements.find(reg);
            if (r == elements.end()) return nullptr;
            const auto g = r->second.find(guid);
            return g == r->second.end() ? nullptr : &g->second;
        };
        const size_t named = name_archive_writers(*journal, ids, whole, base, &fields);
        std::string text = journal->text;
        if (journal->state == "read" && !shared.empty() && journal->format == 1)
            text += "; " + shared + " entries are not named (each is an element or a part of a resource other entries share, and this journal does not say which part a package wrote)";
        else if (journal->state == "read" && journal->format == 2)
            text += "; each mod is named on the fields its member paths name (on recipe and loot entries by their element)";
        meta.base.journal = {journal->state, text, journal->packages.size(), named, journal->truncated};
        if (journal->state == "read")
            log(0, "registry: " + journal->text + "; " + std::to_string(named) + (named == 1 ? " entry" : " entries") +
                       " of the registries name their archive-writer packages");
    }

    void log(int level, const std::string& line) {
        if (env.log) env.log(level, line);
    }
    // a model's notes at info; the entries behind its per-field notes at debug (kiln.log with "log_level": "debug"), one line
    // each: a start with registry content has tens of thousands (31.18 wrote them all at info)
    void log_notes(model::Model& m) {
        for (const std::string& n : m.take_notes()) log(0, "registry: " + n);
        for (const std::string& n : m.take_details()) log(-1, "registry: " + n);
    }
    void revoked_queue(const std::string& mod, uint64_t owner, size_t batches, size_t edits, Stage stage) {
        if (!batches) return;
        const std::string why = "owner " + std::to_string(owner) + " was revoked; dropped " + std::to_string(batches) +
            " queued batch(es), " + std::to_string(edits) + " edit(s) before the stage";
        log(1, "registry: mod " + mod + " " + why);
        pre_refused.push_back({mod, "owner:" + std::to_string(owner), stage, 0, why, ""});
    }
    bool load_regmap() {
        if (rm_loaded) return true;
        rm_loaded = true;
        if (!env.regmap || !env.regmap(&rm, &rm_errs)) {
            log(1, "registry: the regmap could not be read: every field is unproven on this build, edits of fields are refused");
            return false;
        }
        for (const std::string& e : rm_errs) log(1, "registry: regmap: " + e);
        return true;
    }

    // kiln/data/content/registry/rollback.json asks this start to run on an earlier plan's edits. true: *out holds the plan's
    // inputs, read and mapping-checked on this build, every mod of it loaded at the plan's version. false: *why says why the
    // plan cannot start (nothing of it is used). *rb: registry.json's "rollback" so far; *from: the values of the plan rolled
    // away from (the "changedBack" rows compare against them).
    bool rollback_inputs(const std::string& request, const std::set<std::string>& loaded, const std::map<std::string, std::string>& versions,
                         std::vector<ModInput>* out, json* rb, json* from, std::string* why) {
        const json q = json::parse(request, nullptr, false);
        if (!q.is_object() || q.value("format", 0) != 1 || !q.contains("plan") || !q["plan"].is_string() || !is_hash(q["plan"].get<std::string>())) {
            *rb = {{"plan", nullptr}};
            return *why = "the rollback request (kiln/data/content/registry/rollback.json) cannot be read: it is {\"format\": 1, \"plan\": \"<plan hash>\"}", false;
        }
        const std::string plan = q["plan"].get<std::string>();
        const std::string from_hash = q.contains("from") && q["from"].is_string() && is_hash(q["from"].get<std::string>()) ? q["from"].get<std::string>() : "";
        *rb = {{"plan", plan}, {"from", from_hash.empty() ? json(nullptr) : json(from_hash)}, {"requested", q.value("requested", "")}};
        const std::string name = "plan " + short_hash(plan);
        const auto text = env.read_data("plans/" + plan + ".json");
        if (!text) return *why = name + " is not in kiln/data/content/registry/plans (it was removed or never written)", false;
        const json p = json::parse(*text, nullptr, false);
        if (!p.is_object()) return *why = name + " cannot be read (not JSON)", false;
        if (p.value("format", 0) != 2 || !p.contains("inputs") || !p["inputs"].is_array())
            return *why = name + " was written by an older Kiln and holds no edits to start on", false;
        if (plan_hash_of(p) != plan) return *why = name + "'s file does not match its name (it was changed after Kiln wrote it)", false;
        const json key = p.value("key", json::object());
        if (key.value("side", env.side) != env.side) return *why = name + " is the " + key.value("side", std::string()) + "'s; this is the " + env.side, false;
        if (key.value("exeSha256", env.exe_sha256) != env.exe_sha256) (*rb)["madeOnExe"] = key.value("exeSha256", std::string());
        std::vector<std::string> differ;
        for (const json& m : p["inputs"]) {
            const std::string mod = m.value("mod", std::string()), version = m.value("version", std::string());
            if (!loaded.count(mod)) { differ.push_back(mod + " is gone (not loaded in this start)"); continue; }
            const auto v = versions.find(mod);
            const std::string now = v == versions.end() ? std::string() : v->second;
            if (now != version) differ.push_back(mod + " changed (" + version + ", now " + now + ")");
        }
        if (!differ.empty()) {
            std::string all;
            for (const std::string& x : differ) all += (all.empty() ? "" : "; ") + x;
            return *why = name + "'s mods are not this start's: " + all, false;
        }
        for (const json& m : p["inputs"]) {
            ModInput in;
            in.id = m.value("mod", std::string());
            for (const json& doc : m.value("batches", json::array())) {
                Batch b;
                std::vector<Refusal> refused;
                std::string err;
                if (!parse_batch(doc, schema, &b, &refused, &err)) return *why = name + ": an edit document of " + in.id + " cannot be read on this Kiln: " + err, false;
                if (lc(b.mod) != lc(in.id)) return *why = name + ": a batch of " + in.id + " names mod " + b.mod, false;
                refuse_unmapped(rm, schema, &b, &refused, env.entry_support);
                if (!refused.empty()) {
                    const Refusal& x = refused.front();
                    return *why = name + ": " + x.mod + " " + x.source + " #" + std::to_string(x.n) + " is refused on this build: " + x.reason, false;
                }
                in.batches.push_back(std::move(b));
            }
            out->push_back(std::move(in));
        }
        (*rb)["previous"] = p.contains("previous") && p["previous"].is_string() ? p["previous"] : json(nullptr);
        *from = json::object();
        if (!from_hash.empty())
            if (const auto ft = env.read_data("plans/" + from_hash + ".json")) {
                const json f = json::parse(*ft, nullptr, false);
                if (f.is_object() && f.contains("values") && f["values"].is_object()) *from = f["values"];
            }
        return true;
    }

    void report_caps() {
        if (!env.report_cap) return;
        const bool verified_build = rm.verified();  // a record of another regmap/common enables nothing (review3 S4)
        auto publish = [&](const std::string& name, CapCoverage coverage, const std::string& detail) {
            const char* status = "unmapped on this game build";
            uint32_t reason = KILN_CAP_REASON_SYMBOL_MISSING;
            switch (coverage) {
                case CapCoverage::WrongSide:
                    status = "wrong side";
                    reason = KILN_CAP_REASON_WRONG_SIDE;
                    break;
                case CapCoverage::Unmapped:
                    break;
                case CapCoverage::Unverified:
                    status = verified_build ? "unverified mapping on this game build" : "unverified regmap record for this game build";
                    if (!verified_build) reason = KILN_CAP_REASON_POLICY;
                    break;
                case CapCoverage::Verified:
                    status = "mapped and verified";
                    reason = KILN_CAP_REASON_NONE;
                    break;
            }
            const uint32_t state = reason == KILN_CAP_REASON_NONE ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE;
            const KilnResult rc = env.report_cap(name, state, reason, std::string(status) + "; " + detail);
            if (rc != KILN_OK) log(1, "registry: capability " + name + " could not be reported (rc " + std::to_string(rc) + ")");
        };
        for (const auto& reg : gen::kRegistries) {
            const std::string prefix = std::string("registry.") + reg.name;
            CapCoverage best = CapCoverage::WrongSide;
            size_t verified_fields = 0;
            for (size_t i = reg.first; i < reg.first + reg.count; ++i) {
                const auto& f = gen::kFields[i];
                std::string detail = std::string("applies: ") + applies_word(schema_detail::applies_of(f.applies));
                CapCoverage coverage = CapCoverage::WrongSide;
                const char* side = only_side(f.side);
                if (side && env.side != side) {
                    detail += std::string("; ") + side + " only";
                } else {
                    std::string edit_reason;
                    const auto mapping = rm.mapping(reg.name, f.path, &edit_reason);
                    coverage = mapping == map::Mapping::Mapped ? CapCoverage::Verified :
                               mapping == map::Mapping::Unproven ? CapCoverage::Unverified : CapCoverage::Unmapped;
                }
                if (coverage == CapCoverage::Verified) ++verified_fields;
                best = std::max(best, coverage);
                publish(field_cap_name(prefix, f.path), coverage, detail);
            }
            publish(prefix, best, std::to_string(verified_fields) + " of " + std::to_string(reg.count) + " fields verified on this side");
        }
        if (env.entry_support) for (const auto& [name, spec] : schema.registries) {
            for (const bool add : {true, false}) {
                if (add ? !spec.add : !spec.remove) continue;
                std::string reason;
                bool available = env.entry_support(name, add, &reason);
                if (available && !add && name == "items") {
                    for (const char* field : {"entries[].chance", "entries[].weight"})
                        if (rm.mapping("drops", field, &reason) != map::Mapping::Mapped) { available = false; reason = "drop tables not proven on this build"; break; }
                    if (available) for (const char* field : {"inputs", "outputs", "inputs[].item", "outputs[].item"})
                        if (rm.mapping("recipes", field, &reason) != map::Mapping::Mapped) { available = false; reason = "recipe dependencies not proven on this build"; break; }
                }
                const std::string capability = "registry." + name + (add ? ".add" : ".remove");
                env.report_cap(capability, available ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                    available ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_POLICY,
                    "GameStart only; " + (available ? std::string("entry backend ready") : reason));
            }
        }
    }
};

Runner::Runner(Env env) : d_(std::make_unique<Impl>()) {
    d_->env = std::move(env);
    if (d_->env.schema) d_->schema = *d_->env.schema;
}
Runner::~Runner() = default;

KilnResult Runner::set_order(const std::string& order_json) {
    const json j = json::parse(order_json, nullptr, false);
    if (!j.is_array()) return KILN_ERR_INVALID_ARGUMENT;
    std::vector<std::pair<std::string, std::string>> order;
    std::map<std::string, std::string> sides;
    std::set<std::string> seen;
    bool users = false;
    for (const auto& row : j) {
        if (!row.is_object() || !row.contains("mod") || !row["mod"].is_string()) return KILN_ERR_INVALID_ARGUMENT;
        const std::string mod = row["mod"].get<std::string>();
        if (!seen.insert(mod).second) return KILN_ERR_INVALID_ARGUMENT;
        order.emplace_back(mod, row.contains("version") && row["version"].is_string() ? row["version"].get<std::string>() : std::string());
        if (row.contains("side") && row["side"].is_string()) sides[mod] = row["side"].get<std::string>();
        users |= row.contains("registry") && row["registry"].is_boolean() && row["registry"].get<bool>();
    }
    std::lock_guard lk(d_->mu);
    if (phase_.load() != Phase::None) return KILN_ERR_UNAVAILABLE;
    d_->order = std::move(order);
    d_->sides = std::move(sides);
    d_->order_set = true;
    d_->registry_users = users;
    return KILN_OK;
}

KilnResult Runner::set_mode(uint32_t mode) {
    if (mode > 1) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(d_->mu);
    if (phase_.load() != Phase::None) return KILN_ERR_UNAVAILABLE;
    mode_.store(static_cast<Mode>(mode));
    return KILN_OK;
}

KilnResult Runner::subscribe_stage(uint64_t owner, const std::string& mod, StageFn fn, uint64_t* handle) {
    std::string err;
    if (!owner || !fn || !handle || !mod_id_ok(mod, &err)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(d_->mu);
    if (!d_->alive(owner)) return KILN_ERR_REVOKED;
    if (phase_.load() != Phase::None) return KILN_ERR_UNAVAILABLE;
    for (const auto& [h, s] : d_->stage_subs)
        if (s.mod == mod) return KILN_ERR_INVALID_ARGUMENT;
    *handle = d_->next_handle++;
    d_->stage_subs[*handle] = {owner, mod, std::move(fn)};
    return KILN_OK;
}

KilnResult Runner::subscribe_ready(uint64_t owner, ReadyFn fn, uint64_t* handle) {
    if (!owner || !fn || !handle) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(d_->mu);
    if (!d_->alive(owner)) return KILN_ERR_REVOKED;
    *handle = d_->next_handle++;
    d_->ready_subs[*handle] = {owner, std::move(fn)};
    return KILN_OK;
}

KilnResult Runner::unsubscribe(uint64_t handle) {
    std::lock_guard lk(d_->mu);
    if (d_->stage_subs.erase(handle) || d_->ready_subs.erase(handle)) return KILN_OK;
    return KILN_ERR_NOT_FOUND;
}

void Runner::owner_gone(uint64_t owner) {
    if (!owner) return;
    {
        std::lock_guard lk(d_->mu);
        d_->gone.insert(owner);
        for (auto it = d_->stage_subs.begin(); it != d_->stage_subs.end();)
            it = it->second.owner == owner ? d_->stage_subs.erase(it) : std::next(it);
        for (auto it = d_->ready_subs.begin(); it != d_->ready_subs.end();)
            it = it->second.owner == owner ? d_->ready_subs.erase(it) : std::next(it);
        for (auto it = d_->queued.begin(); it != d_->queued.end();) {
            size_t batches = 0, edits = 0;
            Stage stage = Stage::Alter;
            std::erase_if(it->second, [&](const Impl::Queued& q) {
                if (q.owner != owner) return false;
                if (!batches) stage = q.batch.stage;
                ++batches; edits += q.batch.edits.size() + q.refused.size();
                return true;
            });
            d_->revoked_queue(it->first, owner, batches, edits, stage);
            it = it->second.empty() ? d_->queued.erase(it) : std::next(it);
        }
    }
    if (d_->env.owner_gone) d_->env.owner_gone(owner); // never call out while holding the runner lock
}

void Runner::notify_ready(const std::vector<uint64_t>& handles, Phase phase, uint64_t generation) {
    for (const uint64_t h : handles) {
        ReadyFn fn;
        {
            std::lock_guard lk(d_->mu);
            const auto it = d_->ready_subs.find(h);
            if (it == d_->ready_subs.end()) continue;  // removed by an earlier callback in this same snapshot
            if (!d_->alive(it->second.owner)) {
                d_->ready_subs.erase(it);
                continue;
            }
            fn = it->second.fn;  // dispatch starts here; revoke/unsubscribe do not wait for an in-flight callback
        }
        if (fn(phase, generation)) continue;
        {
            std::lock_guard lk(d_->mu);
            d_->ready_subs.erase(h);
        }
        d_->log(2, "registry: a ready callback faulted and was removed");
    }
}

bool Runner::on_stage_thread() const {
    std::lock_guard lk(d_->mu);
    return d_->stage_thread_set && d_->stage_thread == std::this_thread::get_id();
}

KilnResult Runner::submit(uint64_t owner, const std::string& owner_mod, const std::string& ir, uint32_t* accepted, uint32_t* refused_n, std::string* report) {
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    Batch b;
    std::vector<Refusal> refused;
    std::string err;
    auto say = [&](KilnResult rc) {
        if (accepted) *accepted = static_cast<uint32_t>(b.edits.size());
        if (refused_n) *refused_n = static_cast<uint32_t>(refused.size());
        if (report) {
            json r = {{"accepted", b.edits.size()}, {"refused", json::array()}};
            for (const Refusal& x : refused) r["refused"].push_back({{"n", x.n}, {"reason", x.reason}});
            if (!err.empty()) r["error"] = err;
            *report = r.dump();
        }
        return rc;
    };
    {
        std::lock_guard lk(d_->mu);
        if (!d_->alive(owner)) {
            err = "the caller's owner is revoked: the batch is refused";
            return say(KILN_ERR_REVOKED);
        }
        if (!parse_batch(ir, d_->schema, &b, &refused, &err)) return say(KILN_ERR_INVALID_ARGUMENT);
    }
    if (owner_mod.empty()) {  // review finding 8: Kiln binds every batch to its owner's mod; an owner it cannot bind has none
        err = "the caller's owner has no mod Kiln can bind it to (revoked, or not a mod's owner): the batch is refused";
        b.edits.clear();
        return say(KILN_ERR_INVALID_ARGUMENT);
    }
    if (lc(b.mod) != lc(owner_mod)) {
        err = "the batch names mod " + b.mod + ", not the caller's own mod " + owner_mod;
        b.edits.clear();
        return say(KILN_ERR_INVALID_ARGUMENT);
    }
    // one call's edits per registry against its cap (schema editsPerCall): over it, the batch is refused whole with a row per
    // registry it names (owner 2026-11-26: refused loudly, never cut)
    if (auto over = refuse_over_cap(b, refused, d_->schema); !over.empty()) {
        d_->log(1, "registry: " + b.mod + ": " + over.front().reason);
        b.edits.clear();
        refused = std::move(over);
    }
    std::lock_guard lk(d_->mu);
    if (!d_->alive(owner)) {
        err = "the caller's owner is revoked: the batch is refused";
        b.edits.clear();
        return say(KILN_ERR_REVOKED);
    }
    const Phase ph = phase_.load();
    if (ph == Phase::None) {
        if (b.source.empty()) b.source = "code:" + b.mod;
        d_->queued[b.mod].push_back({owner, b, refused});
        return say(KILN_OK);
    }
    if (ph == Phase::Start || ph == Phase::WorldLoad) {
        err = "the stage is over: a change now is a live change";
        b.edits.clear();
        return say(KILN_ERR_UNAVAILABLE);
    }
    if (!(d_->stage_thread_set && d_->stage_thread == std::this_thread::get_id())) {
        err = "during the stage a batch is taken only on the stage thread";
        b.edits.clear();
        return say(KILN_ERR_WRONG_THREAD);
    }
    if (!d_->turn || d_->turn->mod != b.mod) {
        err = "during the stage a batch is taken only inside the stage callback of its mod";
        b.edits.clear();
        return say(d_->turn ? KILN_ERR_INVALID_ARGUMENT : KILN_ERR_UNAVAILABLE);
    }
    if (b.stage != d_->turn->stage) {
        err = std::string("the running stage is ") + stage_name(d_->turn->stage) + ", the batch's " + stage_name(b.stage);
        b.edits.clear();
        return say(KILN_ERR_INVALID_ARGUMENT);
    }
    refuse_unmapped(d_->rm, d_->schema, &b, &refused, d_->env.entry_support);
    Batch* out = d_->turn->out;
    if (out->source.empty()) out->source = "code:" + b.mod;
    std::map<uint32_t, uint32_t> renumbered;  // the batch's edit numbers -> the turn's
    for (Edit& e : b.edits) {
        renumbered[e.n] = static_cast<uint32_t>(out->edits.size());
        e.n = static_cast<uint32_t>(out->edits.size());
        out->edits.push_back(e);
    }
    // review: the schemas' id migrations of this batch ("#<n>: ...") go with its edits into the turn (registry.json "migrated")
    for (const std::string& note : b.migrated) {
        const size_t colon = note.find(": ");
        uint32_t n = 0;
        const bool numbered = note.size() > 1 && note[0] == '#' && colon != std::string::npos &&
                              std::from_chars(note.data() + 1, note.data() + colon, n).ec == std::errc{};
        const auto it = numbered ? renumbered.find(n) : renumbered.end();
        if (it != renumbered.end()) out->migrated.push_back("#" + std::to_string(it->second) + note.substr(colon));  // a refused edit's: not in the turn
    }
    d_->pre_refused.insert(d_->pre_refused.end(), refused.begin(), refused.end());
    return say(KILN_OK);
}

std::vector<BootResource> Runner::run(const OldWritesFn& old_fn) { return run(old_fn, {}, {}); }

std::vector<BootResource> Runner::run(const OldWritesFn& old_fn, const std::vector<std::pair<KilnAssetKey, std::string>>& freeze) {
    return run(old_fn, freeze, {});
}

std::vector<BootResource> Runner::run(const OldWritesFn& old_fn, const std::vector<std::pair<KilnAssetKey, std::string>>& freeze,
                                      const std::vector<std::pair<std::string, std::string>>& fronts) {
    Impl& d = *d_;
    std::vector<ModInput> inputs;
    {
        std::lock_guard lk(d.mu);
        if (phase_.load() != Phase::None) return {};
        d.front_candidates = old_fn ? fronts : std::vector<std::pair<std::string, std::string>>{};
        d.stage_thread = std::this_thread::get_id();
        d.stage_thread_set = true;
        phase_.store(Phase::Add);
        d.load_regmap();
    }
    // Reports can invoke capability subscribers synchronously, so keep the stage mutex out of the call.
    d.report_caps();
    if (mode_.load() == Mode::OnBoot)
        d.log(1, "registry: stage mode onBoot was asked (option A); this build runs the stage at resources-ready: a kiln-backend start's committed "
                 "view lists no resources and the boot snapshot is older than the start, so the base cannot be read before the entry point yet");
    // the order: Kiln.Host's runtime load order, else the mod folders'
    std::vector<std::pair<std::string, std::string>> order;
    {
        std::lock_guard lk(d.mu);
        order = d.order;
        d.meta.order_source = d.order_set ? "kiln.host" : "folders";
    }
    if (!d.meta.order_source.compare("folders") && d.env.folders) order = d.env.folders();
    std::vector<Env::DataFile> files = d.env.data ? d.env.data(d.schema) : std::vector<Env::DataFile>();
    std::map<std::string, ModInput> by_mod;
    std::map<std::string, std::string> versions;
    std::set<std::string> loaded;
    for (const auto& [m, v] : order) versions[m] = v, loaded.insert(m);
    std::vector<Refusal> skipped;
    {
        std::lock_guard lk(d.mu);
        for (const Env::DataFile& f : files)
            if (!f.side.empty()) d.sides.emplace(f.mod, f.side);  // Kiln.Host's set_order side wins
    }
    for (Env::DataFile& f : files) {
        // review finding 3 (ruling 2026-11-21): only mods Kiln.Host loaded on this side, in its order; the others are skipped
        // and named. With Kiln.Host's order, membership decides alone: on a client it holds side "server" mods when hosted
        // server mods are on, since a hosted world's server runs in this process (recheck N1). Without .NET (the folders'
        // order), a mod for the other side is skipped.
        std::string why;
        if (d.meta.order_source == "kiln.host") {
            if (!loaded.count(f.mod)) why = "Kiln.Host did not load the mod on this side (not in its load order)";
        } else if (!(f.side.empty() || f.side == "both" || f.side == d.env.side)) {
            why = "the mod is for the " + f.side + " side, this is the " + d.env.side;
        }
        if (!why.empty()) {
            skipped.push_back({f.mod, f.rel, Stage::Alter, 0, f.rel + ": " + why + "; its registry section was not applied", ""});  // kiln.log: report_log
            continue;
        }
        ModInput& in = by_mod[f.mod];
        in.id = f.mod;
        if (versions[f.mod].empty()) versions[f.mod] = f.version;
        if (!f.error.empty()) {
            in.refused.push_back({f.mod, f.rel, Stage::Alter, 0, f.rel + ": " + f.error + "; nothing of this file's registry section was applied", ""});  // kiln.log: report_log
        }
        for (Batch& b : f.batches) in.batches.push_back(std::move(b));
        in.refused.insert(in.refused.end(), f.refused.begin(), f.refused.end());
    }
    {
        std::lock_guard lk(d.mu);
        // The stage takes the surviving queue here. Already-taken stage edits stay until the next start on revoke.
        for (auto& [mod, qs] : d.queued) {
            for (Impl::Queued& q : qs) {
                if (!d.alive(q.owner)) {
                    d.revoked_queue(mod, q.owner, 1, q.batch.edits.size() + q.refused.size(), q.batch.stage);
                    continue;
                }
                ModInput& in = by_mod[mod];
                in.id = mod;
                in.batches.push_back(std::move(q.batch));
                in.refused.insert(in.refused.end(), q.refused.begin(), q.refused.end());
            }
        }
        d.queued.clear();
        d.pre_refused.insert(d.pre_refused.end(), skipped.begin(), skipped.end());
        for (const auto& [h, s] : d.stage_subs) {
            ModInput& in = by_mod[s.mod];
            in.id = s.mod;
            const std::string mod = s.mod;
            const uint64_t handle = h;
            in.code = [this, mod, handle](Stage st, const View& v, Batch* out) {
                StageFn fn;
                uint64_t owner = 0;
                {
                    std::lock_guard l2(d_->mu);
                    const auto it = d_->stage_subs.find(handle);
                    if (it == d_->stage_subs.end()) return;
                    if (!d_->alive(it->second.owner)) {
                        d_->stage_subs.erase(it);
                        return;
                    }
                    owner = it->second.owner;
                    fn = it->second.fn;
                    d_->turn = Impl::Turn{mod, st, &v, out};
                    out->mod = mod;
                    out->stage = st;
                    phase_.store(st == Stage::Add ? Phase::Add : st == Stage::Alter ? Phase::Alter : Phase::Final);
                }
                const size_t before = out->edits.size();
                const bool clean = fn(st, v);
                std::lock_guard l2(d_->mu);
                d_->turn.reset();
                if (!d_->alive(owner)) {
                    const size_t dropped = out->edits.size() - before;
                    out->edits.resize(before);
                    // As on fault, keep refusal diagnostics: another live owner may have submitted them.
                    d_->stage_subs.erase(handle);
                    d_->pre_refused.push_back({mod, "code:" + mod, st, 0,
                        "the stage callback's owner was revoked; its " + std::to_string(dropped) + " edit(s) of this turn were dropped", ""});
                } else if (!clean) {
                    const size_t dropped = out->edits.size() - before;
                    out->edits.resize(before);
                    d_->stage_subs.erase(handle);
                    d_->pre_refused.push_back({mod, "code:" + mod, st, 0,
                                               "the stage callback faulted; its " + std::to_string(dropped) + " edit(s) of this stage were dropped and it is not called again", ""});
                    d_->log(2, "registry: mod " + mod + "'s stage callback faulted in " + stage_name(st) + ": its edits of that turn are dropped, the callback is removed");
                } else if (!out->edits.empty()) d_->recorded[mod].push_back(*out);
            };
        }
    }
    // the mapping check, then the inputs in the order (mods the order does not name after it, by id)
    std::vector<std::string> named;
    for (const auto& [m, v] : order)
        if (by_mod.count(m)) named.push_back(m);
    std::vector<std::string> rest;
    for (const auto& [m, in] : by_mod)
        if (std::find(named.begin(), named.end(), m) == named.end()) rest.push_back(m);
    if (!rest.empty() && d.meta.order_source == "kiln.host")
        for (const std::string& m : rest) d.log(0, "registry: mod " + m + " is not in Kiln.Host's load order: its edits run after every mod it names");
    named.insert(named.end(), rest.begin(), rest.end());
    for (const std::string& m : named) {
        ModInput in = std::move(by_mod[m]);
        for (Batch& b : in.batches) refuse_unmapped(d.rm, d.schema, &b, &in.refused, d.env.entry_support);
        inputs.push_back(std::move(in));
    }
    // registry-rollback: the plans' index (the last good plan, the plan the last world ran on), and a rollback request that
    // starts this boot on an earlier plan's edits instead of the mods' current ones (kept until it is cleared)
    json rb;                          // registry.json "rollback"; null: none asked
    json from_values = json::object();
    std::vector<ModInput> fallback;   // the mods' current inputs while the plan's are tried
    bool rolled = false;
    if (d.env.read_data) {
        if (const auto t = d.env.read_data("plans/index.json")) {
            const json idx = json::parse(*t, nullptr, false);
            if (idx.is_object()) {
                d.index = idx;
            } else {
                // review: a write cut short or a bad hand edit. Kept for a person to read; the next world's start writes a new
                // index. The highest max stacks worlds started with are unknown now: the stack guard keeps every saved stack whole.
                d.index_unreadable = true;
                if (d.env.write_data) d.env.write_data("plans/index.unreadable.json", *t);
                std::string w;
                const bool guarded = d.env.stack_guard && d.env.stack_guard(&w);
                d.log(1, "registry: kiln/data/content/registry/plans/index.json cannot be read (kept as plans/index.unreadable.json): the last good plan and "
                         "the highest max stack a world started with are not known, so this start has no previous plan; " +
                         std::string(guarded ? "Kiln's stack guard is on, so saved stacks stay whole"
                                             : "Kiln's stack guard is not available here" + (w.empty() ? std::string() : " (" + w + ")") +
                                                   ": a saved stack above an item's max stack loses the rest at its next move"));
            }
        }
        // review: only plan hashes name plans (a hand-edited index is never passed on as registry.json plan.previous)
        for (const char* k : {"good", "world"}) {
            if (!d.index.contains(k)) continue;
            json& row = d.index[k];
            if (!row.is_object() || !row.contains("plan") || !row["plan"].is_string() || !is_hash(row["plan"].get<std::string>())) {
                d.index.erase(k);
                continue;
            }
            if (row.contains("previous") && !(row["previous"].is_string() && is_hash(row["previous"].get<std::string>()))) row["previous"] = nullptr;
        }
        if (const auto req = d.env.read_data("rollback.json")) {
            std::vector<ModInput> plan_inputs;
            std::string why;
            bool usable = false;
            try {
                usable = d.rollback_inputs(*req, loaded, versions, &plan_inputs, &rb, &from_values, &why);
            } catch (const std::exception& e) {
                // review: a member of the request or the plan of another type than Kiln writes (a hand edit) threw from json::value;
                // refused like any unreadable request, never thrown into the boot
                if (!rb.is_object()) rb = {{"plan", nullptr}};
                plan_inputs.clear();
                from_values = json::object();
                why = std::string("the rollback request or its plan cannot be read (") + e.what() + ")";
            }
            if (usable) {
                for (const ModInput& in : plan_inputs)
                    for (const Batch& b : in.batches)
                        if (versions[in.id].empty()) versions[in.id] = b.mod_version;
                json left = json::array();
                for (const ModInput& in : inputs)
                    if (std::none_of(plan_inputs.begin(), plan_inputs.end(), [&](const ModInput& x) { return x.id == in.id; })) left.push_back(in.id);
                rb["leftOut"] = std::move(left);
                fallback = std::move(inputs);
                inputs = std::move(plan_inputs);
                rolled = true;
            } else {
                rb["state"] = "refused";
                rb["reason"] = why + "; this start runs on the mods' current registry edits";
            }
        }
    }
    // the base
    std::string why;
    // no mod has registry content: nothing is read (an empty registry.json says so), except once per game build, when the
    // running exe has no id table yet (work-bb 2026-11-21: kilnctl update needs one for every build)
    const std::string ids_rel = "ids-" + d.env.side + "-" + d.env.exe_sha256 + ".json";
    // one lookup, nothing read (proof-2 N7: the table is about 1.9 MB); an env without data_exists reads it as before
    const bool ids_missing = d.env.read_data && d.env.write_data && !d.env.exe_sha256.empty() &&
                             (d.env.data_exists ? !d.env.data_exists(ids_rel) : !d.env.read_data(ids_rel));
    // the highest max stack a world ever started with, per item (plans/index.json "stacks", never lowered: a save that world
    // wrote can hold such stacks), against this start's value, else the base's: lower, and a save can hold bigger stacks than
    // this start allows (saves.h stackCut)
    const json then = d.index.contains("stacks") && d.index["stacks"].is_object() ? d.index["stacks"] : json::object();
    bool then_stacks = false;
    for (auto it = then.begin(); it != then.end(); ++it) {
        FieldRef ref;
        then_stacks = then_stacks || (field_ref_of(it.key(), &ref) && ref.registry == "items" && ref.field == "maxStack");
    }
    // a front-end candidate needs its fields' values and the composition of its rows (OldWrite::front), registry content or not
    const bool want_base = !inputs.empty() || !fallback.empty() || then_stacks || !d.front_candidates.empty();
    // A mod whose code uses the registry API (Kiln.Host's set_order flag): decode the stable base here, at resources-ready,
    // before kiln.content writes packs into resident resource pages, rather than at first use (review4 NS1).
    bool users = false;
    {
        std::lock_guard lk(d.mu);
        users = d.registry_users;
    }
    const bool eager = want_base || (users && d.env.prepare_live);
    // review-j2: a start whose only registry work is a front-end (no registry content, no stack record, no registry user) reads
    // what its candidates need (below), never the whole base the live back-end's first use needs (need.all): that waits for a
    // first use, as at a start with no registry work, over a source frozen before the boot writers write (first_use below)
    const bool fronts_only = !d.front_candidates.empty() && inputs.empty() && fallback.empty() && !then_stacks && !users;
    const bool freeze_old = !eager && d.env.prepare_live && !freeze.empty();
    std::unique_ptr<model::Source> src = !eager && !ids_missing && !freeze_old ? nullptr : d.env.source ? d.env.source(&why) : nullptr;
    if ((!eager || (!want_base && !src)) && d.env.prepare_live) d.lazy_old = old_fn; // production supplies owned document copies
    // Only the once-per-build id ledger: no stable field decode on unused starts (entries only, with the reference tables).
    auto write_id_ledger = [&](model::Source& source) {
        model::Model ids_model(source, d.rm, d.schema);
        ids_model.list_all();
        ids_model.id_table(&d.ids, &d.nums);
        d.env.write_data(ids_rel, ids_model.id_table_json().dump());
        d.log(0, "registry: the id table of this game build (" + d.env.side + " " + d.env.exe_sha256.substr(0, 8) + ") is written to the data folder (" +
                     std::to_string(d.ids.size()) + " entries); no mod has registry content");
        d.log_notes(ids_model);
    };
    bool ledger_written = false;
    if (freeze_old && ids_missing && src) {
        // review4 nit: the ledger reads every resource, and the source kept for the first-use decode would hold them all
        // until then. Write it, let that source go, and freeze the old writers' resources in a fresh one: still before any
        // boot writer of this start writes, so the frozen bytes are the same.
        write_id_ledger(*src);
        ledger_written = true;
        src->release();
        src.reset();
        src = d.env.source ? d.env.source(&why) : nullptr;
    }
    if (freeze_old) {
        for (const auto& [key, type] : freeze) {
            std::string error;
            if (!src || !src->touch(key, type, &error)) {
                d.lazy_error = "the registry's pre-patch base could not be frozen: " + (error.empty() ? why : error);
                d.log(1, "registry: " + d.lazy_error);
                break;
            }
        }
    }
    d.meta.pid = d.env.pid;
    d.meta.kiln = d.env.kiln_version;
    d.meta.regmap = {d.env.side, d.env.exe_sha256, d.rm.has_build ? "regmap/" + d.env.side + "/" + d.env.exe_sha256 + ".json" : "", d.rm.verified()};
    d.meta.base.writer = d.env.writer;
    d.meta.base.archive = d.env.archive;
    BaseModel base;
    std::vector<OldWrite> old;
    std::unique_ptr<Inventory> inventory;  // before m: the model reads through it
    std::unique_ptr<model::Model> m;
    std::vector<ModInput> named_inputs = inputs;  // the plan's and the mods' current inputs: either may be composed below
    named_inputs.insert(named_inputs.end(), fallback.begin(), fallback.end());
    model::Need need = model::needs_of(d.schema, named_inputs);
    named_inputs.clear();
    if (then_stacks) need.fields["items"].insert("maxStack"), need.registries.insert("items");
    // a front-end candidate's resource: every scalar boot field of the registries it is an entry of (its rows' base values)
    for (const auto& [id, type] : d.front_candidates)
        for (const auto& [name, rmap] : d.rm.registries) {
            if (rmap.entries.resource_type != type) continue;
            const RegistrySpec* rs = d.schema.registry(name);
            for (const auto& [fname, row] : rmap.rows)
                if (rs && rs->field(fname) && row.backend == map::Backend::Boot && fname.find("[]") == std::string::npos &&
                    (row.conv == "int" || row.conv == "float" || row.conv == "bool" || row.conv == "time" || row.conv == "enum"))
                    need.fields[name].insert(fname), need.registries.insert(name);
        }
    if (d.env.prepare_live && eager && !fronts_only) need.all = true;
    bool hit = false;
    if (!src && !want_base) {
        if (eager && d.env.prepare_live && !freeze.empty() && d.lazy_error.empty()) {
            // review4 nit (NS1 edge): without a base at the stage the old boot writers' resources were not frozen either; a
            // first-use decode would read their patched bytes as the game's and compose their edits over them again
            d.lazy_error = "the registry's base could not be read at the stage (" + why + "), so the old boot writers' resources "
                           "could not be frozen before they wrote; restart the game to use the registry API";
            d.log(1, "registry: " + d.lazy_error);
        } else if (eager) {
            d.log(1, "registry: the base could not be read at the stage (" + why + "); it is prepared at first use instead");
        }
        d.meta.base.origin = "none";
    } else if (!eager) {
        if (ids_missing && !ledger_written) write_id_ledger(*src);
        if (freeze_old) d.lazy_source = std::move(src);
        else { src->release(); src.reset(); }
        d.meta.base.origin = "none";
    } else if (!src) {
        d.log(2, "registry: the stage has no base to read (" + why + "): nothing of the registry is applied in this start");
        std::lock_guard lk(d.mu);
        d.counts.failed = true;
    } else {
        inventory = std::make_unique<Inventory>(*src);
        m = std::make_unique<model::Model>(*inventory, d.rm, d.schema);
        d.meta.base.origin = src->name();
        const std::string family = cache_family(d.env, d.rm, need);
        // review-j2: a front-end-only start's read is a few balance fields: no base cache (a first start of a build lists every
        // registry for the id table, and a cache of that read would have every later start touch all of it to check it)
        if (need.raw.empty() && !ids_missing && !fronts_only) {
            LiveData cached;
            hit = read_base_cache(d.env, family, *src, &cached);
            if (hit) {
                base = std::move(cached.base); d.ids = std::move(cached.ids);
                d.nums = std::move(cached.nums); d.decoded = std::move(cached.decoded);
            }
        }
        if (!hit) {
            m->fill(need, &base);
            // the id table of this build for kilnctl update: every R1 registry the first time this build starts with Kiln
            const bool want_all = ids_missing;
            if (want_all) m->list_all();
            m->id_table(&d.ids, &d.nums);
            d.decoded = m->decoded();
            // review finding 4 (ruling 2026-11-21 19:11): written once per build, when missing, always with every registry
            if (d.env.write_data && want_all) d.env.write_data(ids_rel, m->id_table_json().dump());
            if (need.raw.empty() && !fronts_only) write_base_cache(d.env, family, *src, *inventory, base, d.ids, d.nums, d.decoded);
        }
        d.name_archive(&base, d.ids, m.get());
        if (old_fn) old = old_fn(*m);
        d.log_notes(*m);
    }
    // compose, then the saves check of restart fields (saves.h; the live back-end checks every live write over the value under
    // it): an edit the saves refuse is taken out before the boot back-end writes anything. A plan a rollback starts on is
    // checked on every field, so it is refused whole rather than per entry at the world's load.
    std::vector<Refusal> save_refused;
    std::vector<std::string> plan_guards;  // a plan's live fields whose save check needs the stack guard (a lower max stack)
    auto compose_checked = [&](const std::vector<ModInput>& in, bool plan) {
        save_refused.clear();
        plan_guards.clear();
        Result x = compose(d.schema, base, old, in);
        std::vector<FieldRef> drop;
        for (const auto& [ref, fo] : x.fields) {
            if (is_raw_field(ref.field) || !has_registry_row(fo)) continue;
            const RegistrySpec* rs = d.schema.registry(ref.registry);
            const FieldSpec* f = rs ? rs->field(ref.field) : nullptr;
            if (!plan && !(f && f->applies == Applies::Restart)) continue;
            const auto v = saves::judge(ref.registry, ref.field, saves::before_registry(fo), fo.result);
            // review: the live back-end asks the guard per entry at the world's load; a plan asks it here, so a missing guard
            // refuses the plan whole rather than some of its entries
            if (v && v->decision == saves::Decision::Guard && plan) plan_guards.push_back(ref.text() + " " + v->text.substr(0, v->text.find(':')));
            if (!v || v->decision != saves::Decision::Refuse) continue;
            for (const ChainRow& c : fo.chain)
                if (c.kind == WriterKind::Registry)
                    save_refused.push_back({c.mod, c.source, c.stage, c.n, "saves: " + ref.text() + " " + v->text, "", ref.registry, ref.id, ref.field});
            drop.push_back(ref);
        }
        for (const FieldRef& ref : drop) x.fields.erase(ref);
        return x;
    };
    auto stacks_lowered = [&](const Result& x) {
        std::vector<std::pair<FieldRef, std::pair<Value, Value>>> out;
        for (auto it = then.begin(); it != then.end(); ++it) {
            FieldRef ref;
            if (!field_ref_of(it.key(), &ref) || ref.registry != "items" || ref.field != "maxStack" || !it.value().is_number()) continue;
            Value now;
            if (const auto f = x.fields.find(ref); f != x.fields.end()) now = f->second.result;
            else if (const auto reg = base.registries.find("items"); reg != base.registries.end())
                for (const BaseModel::Entry& e : reg->second)
                    if (e.id == ref.id && e.fields.count("maxStack")) now = e.fields.at("maxStack");
            const Value top = it.value().is_number_integer() ? Value::integer(it.value().get<int64_t>()) : Value::number(it.value().get<double>());
            if (now.numeric() && now.as_double() < top.as_double()) out.push_back({ref, {top, now}});
        }
        return out;
    };
    // A field's value on this start without the registry's rows (after the older paths' writes; the base's when nothing writes
    // it). None: the entry or field is not in this start's base.
    auto bare = [&](const Result& x, const FieldRef& ref) -> Value {
        if (const auto it = x.fields.find(ref); it != x.fields.end()) return saves::before_registry(it->second);
        if (const auto reg = base.registries.find(ref.registry); reg != base.registries.end())
            for (const BaseModel::Entry& e : reg->second)
                if (e.id == ref.id) {
                    const auto f = e.fields.find(ref.field);
                    return f == e.fields.end() ? Value{} : f->second;
                }
        return Value{};
    };
    Result r;
    // review: every value this start's registry edits give (and every value of another plan's) against that plan's values: one
    // saves::judge verdict per field that changes what saves hold (the plan's value -> this start's)
    auto judge_against = [&](const json& against, const std::function<void(const FieldRef&, const std::string&, const saves::Verdict&)>& each) {
        std::set<std::string> keys;
        for (auto it = against.begin(); it != against.end(); ++it) keys.insert(it.key());
        for (const auto& [ref, fo] : r.fields)
            if (has_registry_row(fo)) keys.insert(ref.text());
        for (const std::string& k : keys) {
            FieldRef ref;
            if (!field_ref_of(k, &ref) || is_raw_field(ref.field)) continue;
            const auto it = r.fields.find(ref);
            const Value was = against.contains(k) ? saves::of_json(against[k]) : bare(r, ref);
            const Value now = it != r.fields.end() && has_registry_row(it->second) ? it->second.result : bare(r, ref);
            if (was.kind == Value::None || now.kind == Value::None) continue;  // not on one of the two (an entry this build lacks)
            if (const auto v = saves::judge(ref.registry, ref.field, was, now)) each(ref, k, *v);
        }
    };
    // the values of a kept plan (plans/<hash>.json "values"); empty: not there
    auto plan_values = [&](const std::string& hash) -> json {
        if (hash.empty() || !d.env.read_data) return json::object();
        const auto t = d.env.read_data("plans/" + hash + ".json");
        const json f = t ? json::parse(*t, nullptr, false) : json();
        return f.is_object() && f.contains("values") && f["values"].is_object() ? f["values"] : json::object();
    };
    // a verdict's text against another plan: "<what changes> against plan <hash12>, <which>: <what it does to saves>" (a refusal's
    // "this edit was not applied" left out: the caller says what happens)
    auto against_text = [](std::string text, const std::string& plan, const std::string& which) {
        if (const size_t cut = text.rfind("; this edit was not applied"); cut != std::string::npos) text.resize(cut);
        const std::string where = " against plan " + short_hash(plan) + ", " + which;
        const size_t colon = text.find(": ");
        return plan.empty() ? text : colon == std::string::npos ? text + where : text.substr(0, colon) + where + text.substr(colon);
    };
    const std::string world_plan = d.index.contains("world") && d.index["world"].is_object() && d.index["world"].contains("plan") &&
                                           d.index["world"]["plan"].is_string() ? d.index["world"]["plan"].get<std::string>() : "";
    if (src) r = compose_checked(inputs, rolled);
    std::string guard_why, plan_why;  // plan_why: the whole plan is refused by a check of all of it (not one edit)
    std::vector<saves::SaveNote> rollback_notes;  // registry.json "saves": what the rollback does to saves against that plan
    if (rolled && src && r.refused.empty() && save_refused.empty()) {
        // review: the plan against the values of the plan it rolls away from (else the last world's): a world ran on those, so
        // its saves can hold them. A Refuse verdict refuses the plan whole; a Guard verdict needs the stack guard here.
        json against = from_values;
        std::string against_plan = rb["from"].is_string() ? rb["from"].get<std::string>() : "";
        if (against.empty()) {
            against_plan = world_plan == rb.value("plan", std::string()) ? std::string() : world_plan;
            against = plan_values(against_plan);
        }
        std::vector<std::string> needs_guard = plan_guards;
        std::vector<saves::SaveNote> guard_notes;  // a lower max stack against that plan: the guard keeps such stacks whole
        judge_against(against, [&](const FieldRef& ref, const std::string& k, const saves::Verdict& v) {
            const std::string text = against_text(v.text, against_plan, "which it rolls away from");
            if (v.decision == saves::Decision::Guard) {
                needs_guard.push_back(k + " " + text.substr(0, text.find(": ")));
                guard_notes.push_back({ref, v.cls, "guard", text});
            }
            if (v.decision == saves::Decision::Refuse && plan_why.empty()) plan_why = "saves: " + k + " " + text;
            if (v.decision == saves::Decision::Warn || v.decision == saves::Decision::Report)
                rollback_notes.push_back({ref, v.cls, saves::decision_name(v.decision), text});
        });
        const auto lowered = stacks_lowered(r);
        if (plan_why.empty() && (!lowered.empty() || !needs_guard.empty()) && !(d.env.stack_guard && d.env.stack_guard(&guard_why))) {
            const std::string what = !lowered.empty() ? "it lowers the largest stack of " + std::to_string(lowered.size()) + " item(s) (" + lowered.front().first.text() + " " +
                                                            lowered.front().second.first.text_of() + " -> " + lowered.front().second.second.text_of() + ") below what a world already started with"
                                                      : needs_guard.front();
            plan_why = "saves: " + what + ", and Kiln's stack guard is not available here" + (guard_why.empty() ? "" : " (" + guard_why + ")") +
                       ": the game would delete the part of a saved stack above it";
        }
        if (!plan_why.empty()) save_refused.push_back({"", "", Stage::Alter, 0, plan_why, ""});
        for (const saves::SaveNote& n : guard_notes)  // the stack note below names the ones against a world's highest stack
            if (std::none_of(lowered.begin(), lowered.end(), [&](const auto& l) { return l.first == n.field; })) rollback_notes.push_back(n);
    }
    if (rolled && (!src || !r.refused.empty() || !save_refused.empty())) {
        // never half-applied: one refused edit of the plan on this start refuses the whole plan
        std::string why = "the stage has no base to read";
        if (src && !plan_why.empty()) {
            why = plan_why;
        } else if (src) {
            std::vector<Refusal> all = r.refused;
            all.insert(all.end(), save_refused.begin(), save_refused.end());
            const Refusal& x = all.front();
            why = "the plan's edit " + x.mod + " " + (x.source.empty() ? std::string("(no source)") : x.source) + " #" + std::to_string(x.n) +
                  " is refused on this start: " + x.reason;
            if (all.size() > 1) why += " (and " + std::to_string(all.size() - 1) + " more)";
        }
        rb["state"] = "refused";
        rb["reason"] = "plan " + short_hash(rb.value("plan", std::string())) + ": " + why + "; nothing of the plan was applied, this start runs on the mods' current registry edits";
        rb.erase("leftOut");
        inputs = std::move(fallback);
        fallback.clear();
        rolled = false;
        if (src) r = compose_checked(inputs, false);
    }
    // a start (or rollback) that gives items a lower max stack than a world already started with: the stack guard keeps the
    // bigger stacks a save holds whole; without it the save loses them at their next move (warned: nothing here can be refused)
    if (src) {
        const auto lowered = stacks_lowered(r);
        std::string w;
        const bool guarded = !lowered.empty() && d.env.stack_guard && d.env.stack_guard(&w);
        for (const auto& [ref, vals] : lowered) {
            const std::string a = vals.first.text_of(), b = vals.second.text_of();
            d.stage_notes.push_back({ref, "stackCut", guarded ? "guard" : "warn", guarded ?
                "a world already started with the largest stack " + a + ", this start gives " + b + ": Kiln's stack guard keeps the bigger stacks a save holds whole" :
                "a world already started with the largest stack " + a + ", this start gives " + b + ", and Kiln's stack guard is not available here" +
                    (w.empty() ? "" : " (" + w + ")") + ": the game deletes the part of a saved stack above " + b + " at its next move"});
        }
    }
    if (rolled) d.stage_notes.insert(d.stage_notes.end(), rollback_notes.begin(), rollback_notes.end());
    // review: a start (not a rollback) against the plan the last world ran on. A narrower level range or a shorter build-zone
    // list than that world's changes what its saves hold, and nothing can be refused for it (without the edit the value is
    // the game's, narrower still): a warning. The other classes are judged against the value without the registry, as above,
    // and (review-registry-r3a) a warning class against that plan too where this start's own value gets no warning: a skill
    // node's cost lowered for that world and taken back up now puts characters who spent their points at it over them (the
    // game's join check resets their tree), though against the game's value the start may still lower it.
    if (src && !rolled && !world_plan.empty())
        judge_against(plan_values(world_plan), [&](const FieldRef& ref, const std::string&, const saves::Verdict& v) {
            if (v.decision == saves::Decision::Refuse) {
                d.stage_notes.push_back({ref, v.cls, "warn", against_text(v.text, world_plan, "which the last world ran on") +
                                                             " (nothing to refuse: without this start's value the game's applies, narrower still)"});
                return;
            }
            if (v.decision != saves::Decision::Warn) return;
            if (const auto it = r.fields.find(ref); it != r.fields.end() && has_registry_row(it->second))
                if (saves::judge(ref.registry, ref.field, saves::before_registry(it->second), it->second.result)) return;  // its own note
            d.stage_notes.push_back({ref, v.cls, "warn", against_text(v.text, world_plan, "which the last world ran on")});
        });
    if (rolled) {
        // exactly what changed back: every value of the plan rolled away from, and of this start, that differs
        std::map<std::string, json> now;
        for (const auto& [ref, fo] : r.fields)
            if (has_registry_row(fo)) now[ref.text()] = fo.result.to_json();
        // review: a field only the plan edits had its value without the registry in the plan rolled away from (not the plan's)
        auto without_registry = [&](const std::string& key) -> json {
            FieldRef ref;
            return field_ref_of(key, &ref) ? bare(r, ref).to_json() : json(nullptr);
        };
        std::set<std::string> keys;
        for (auto it = from_values.begin(); it != from_values.end(); ++it) keys.insert(it.key());
        for (const auto& [k, v] : now) keys.insert(k);
        json changed = json::array();
        size_t total = 0;
        for (const std::string& k : keys) {
            const json a = from_values.contains(k) ? from_values[k] : without_registry(k);
            const auto n = now.find(k);
            const json b = n != now.end() ? n->second : without_registry(k);
            if (a == b) continue;
            if (++total <= 2000) changed.push_back({{"field", k}, {"from", a}, {"to", b}});
        }
        rb["state"] = "applied";
        rb["changedBack"] = std::move(changed);
        rb["changedBackTotal"] = total;
        if (from_values.empty() && rb["from"].is_string()) rb["fromValues"] = "the plan rolled away from is not in the plans folder: changedBack lists this start's values only";
        const std::string plan = rb.value("plan", std::string());
        d.log(1, "registry: rolled back to plan " + short_hash(plan) + " (kiln/data/content/registry/rollback.json): " + std::to_string(total) +
                     " value(s) changed back; saves are not touched; kilnctl registry rollback --clear returns to the mods' current edits");
        size_t shown = 0;
        for (const json& row : rb["changedBack"]) {
            if (++shown > 20) break;
            d.log(0, "registry: rollback: " + row["field"].get<std::string>() + " " + (row["from"].is_null() ? std::string("(none)") : row["from"].dump()) + " -> " +
                         (row["to"].is_null() ? std::string("(none)") : row["to"].dump()));
        }
        for (const json& m : rb["leftOut"]) d.log(1, "registry: rollback: mod " + m.get<std::string>() + "'s registry edits are left out (the plan does not hold them)");
    } else if (rb.is_object()) {
        d.log(1, "registry: the rollback " + (rb["plan"].is_string() ? "to plan " + short_hash(rb["plan"].get<std::string>()) + " " : std::string()) +
                     "is refused: " + rb.value("reason", std::string()));
    }
    for (const ModInput& in : inputs) {
        json edits = json::array();
        for (const Batch& b : in.batches) {
            json eb = {{"stage", stage_name(b.stage)}, {"source", b.source}, {"n", b.edits.size()}};
            edits.push_back(eb);
        }
        d.meta.order.push_back({in.id, versions[in.id], sha_hex(edits.dump()).substr(0, 16)});
    }
    // the schemas' id migrations: what they renamed in the edits this start runs (ir.cpp parse_batch did the renaming)
    json migrated = json::array();
    size_t migrated_logged = 0;
    // review: with the GameStart callbacks' turns (recorded during compose, added to the inputs below)
    std::vector<std::pair<std::string, Batch>> turns;
    {
        std::lock_guard lk(d.mu);
        for (const ModInput& in : inputs)
            if (const auto it = d.recorded.find(in.id); it != d.recorded.end())
                for (const Batch& t : it->second)
                    if (!t.migrated.empty()) turns.push_back({in.id, Batch{t.mod, t.mod_version, t.source, t.stage, {}, t.migrated}});
    }
    auto note_batch = [&](const std::string& mod, const Batch& b) {
        for (const std::string& note : b.migrated) {
            if (migrated.size() < 2000) migrated.push_back({{"mod", mod}, {"source", b.source}, {"stage", stage_name(b.stage)}, {"note", note}});
            if (++migrated_logged <= 50) d.log(0, "registry: migrated " + mod + " " + (b.source.empty() ? std::string("(no source)") : b.source) + " " + note);
        }
    };
    for (const ModInput& in : inputs) {
        for (const Batch& b : in.batches) note_batch(in.id, b);
        for (const auto& [mod, t] : turns)
            if (mod == in.id) note_batch(mod, t);
    }
    if (migrated_logged > 50) d.log(0, "registry: " + std::to_string(migrated_logged - 50) + " more migrated name(s): state/registry.json \"migrated\" lists them");
    // the edits refused before the stage are the report's too (and the saves check's: kept across a reload like them)
    std::vector<Refusal> pre;
    for (const ModInput& in : inputs) pre.insert(pre.end(), in.refused.begin(), in.refused.end());
    pre.insert(pre.end(), save_refused.begin(), save_refused.end());
    {
        std::lock_guard lk(d.mu);
        pre.insert(pre.end(), d.pre_refused.begin(), d.pre_refused.end());
    }
    r.refused.insert(r.refused.begin(), pre.begin(), pre.end());
    {
        std::lock_guard lk(d.mu);
        d.outside = pre;
    }
    // the boot back-end's resources: restart fields whose chains hold registry rows, or a front-end document's rows
    // (OldWrite::front: the registry writes that old document's value itself, in its place in the chain)
    std::vector<FrontEnd> verdicts;
    std::map<std::string, std::string> front_kept;  // a candidate the registry does not write -> why
    std::map<std::string, std::set<FieldRef>> front_fields;  // a candidate the registry may write -> its rows' fields
    for (const auto& [id, type] : d.front_candidates) {
        if (!m) {
            front_kept[id] = "the registry stage could not read the game's base at this start";
            continue;
        }
        bool mark = false, rows = false;
        for (const OldWrite& w : old) {
            if (w.who != id) continue;
            if (w.field.field.empty()) {
                mark = true;
                if (!w.front_why.empty() && !front_kept.count(id)) front_kept[id] = w.front_why;
            } else if (w.front) {
                rows = true;
                front_fields[id].insert(w.field);
            }
        }
        if (!front_kept.count(id) && !rows) front_kept[id] = mark ? "it sets no value a registry field holds" : "no registry maps the resource it patches on this game build";
    }
    std::vector<BootResource> boot;
    std::set<FieldRef> boot_refs;
    for (const auto& [ref, fo] : r.fields) {
        const auto reg = d.rm.registries.find(ref.registry);
        if (reg == d.rm.registries.end() || is_raw_field(ref.field)) continue;
        const auto row = reg->second.rows.find(ref.field);
        // a hooks registry's restart fields go the same way: one "resource" of type kHooksType, which kiln.content hands to its
        // hooks (limits.h) instead of a resource copy
        if (row == reg->second.rows.end() || (row->second.backend != map::Backend::Boot && row->second.backend != map::Backend::Hook)) continue;
        const model::Entry* e = m ? m->find(ref.registry, ref.id) : nullptr;
        const RegistrySpec* rs = d.schema.registry(ref.registry);
        const FieldSpec* f = rs ? rs->field(ref.field) : nullptr;
        if (f && e) boot_refs.insert(ref);
    }
    // a candidate is written by the registry only when every value it sets reaches a boot field here (whole or not at all)
    for (const auto& [id, refs] : front_fields)
        for (const FieldRef& ref : refs)
            if (!boot_refs.count(ref) && !front_kept.count(id)) front_kept[id] = ref.text() + " is not a field the registry's boot back-end writes here";
    auto carried = [&](const ChainRow& c) { return c.front && c.kind == WriterKind::OldPatch && !front_kept.count(c.who) && front_fields.count(c.who); };
    for (auto& [ref, fo] : r.fields)  // a kept candidate's rows are the old path's again, everywhere
        for (ChainRow& c : fo.chain)
            if (c.front && !carried(c)) c.front = false;
    for (OldWrite& w : old)
        if (w.front && (front_kept.count(w.who) || !front_fields.count(w.who))) w.front = false;
    for (const auto& [ref, fo] : r.fields) {
        if (!boot_refs.count(ref)) continue;
        const auto reg = d.rm.registries.find(ref.registry);
        const auto row = reg->second.rows.find(ref.field);
        std::vector<ChainRow> rows;
        std::set<std::string> carries;
        for (const ChainRow& c : fo.chain)
            if (c.kind == WriterKind::Registry || carried(c)) {
                rows.push_back(c);
                if (c.front) carries.insert(c.who);
            }
        if (rows.empty()) continue;
        const RegistrySpec* rs = d.schema.registry(ref.registry);
        const FieldSpec* f = rs->field(ref.field);
        const model::Entry* e = m->find(ref.registry, ref.id);
        auto it = std::find_if(boot.begin(), boot.end(), [&](const BootResource& b) { return std::memcmp(&b.key, &e->place.key, sizeof b.key) == 0; });
        if (it == boot.end()) {
            boot.push_back({e->place.key, e->place.type, {}, {}});
            it = boot.end() - 1;
        }
        it->fields.push_back({ref, &reg->second, &row->second, *f, std::move(rows)});
        for (const std::string& who : carries)
            if (std::find(it->front.begin(), it->front.end(), who) == it->front.end()) it->front.push_back(who);
    }
    for (const auto& [id, type] : d.front_candidates) {
        FrontEnd x;
        if (const size_t slash = id.find('/'); slash != std::string::npos) x.mod = id.substr(0, slash), x.key = id.substr(slash + 1);
        else x.key = id;
        if (const auto k = front_kept.find(id); k != front_kept.end()) x.state = "resourcePatch", x.why = k->second;
        else x.state = "pending", x.fields = front_fields[id].size();
        verdicts.push_back(std::move(x));
    }
    // A cache hit has not populated the model's entry locations yet. Locate boot targets before releasing its source.
    if (m) m->release();
    const bool full_live = eager && need.all && bool(src);
    // review-j2: a front-end-only start's first use (fronts_only): a fresh source that holds the old writers' resources as they
    // are now, before any boot writer of this start writes (as an unused start's, freeze_old above); the first use composes the
    // stage's old writes (d.old, with their front marks) over it. This start's own read is over: let it go first (a new source
    // is a new read generation of the view).
    std::unique_ptr<model::Source> first_use;
    std::string first_use_error;
    if (fronts_only && d.env.prepare_live) {
        m.reset();
        inventory.reset();
        src.reset();
        std::string error;
        first_use = d.env.source ? d.env.source(&error) : nullptr;
        for (const auto& [key, type] : freeze)
            if (!first_use || !first_use->touch(key, type, &error)) {
                first_use.reset();
                first_use_error = "the registry's pre-patch base could not be frozen: " + (error.empty() ? why : error);
                d.log(1, "registry: " + first_use_error);
                break;
            }
    }
    std::lock_guard lk(d.mu);
    if (first_use) d.lazy_source = std::move(first_use);
    if (!first_use_error.empty() && d.lazy_error.empty()) d.lazy_error = first_use_error;
    d.meta.migrated = std::move(migrated);
    d.meta.rollback = rb;
    if (rolled) {
        d.rollback = rb.value("plan", std::string());
        for (const json& m : rb.value("leftOut", json::array()))
            if (m.is_string()) d.left_out.push_back(m.get<std::string>());
        d.rollback_previous = rb.contains("previous") && rb["previous"].is_string() ? rb["previous"].get<std::string>() : std::string();
        rb.erase("previous");
        d.meta.rollback = rb;
    } else if (rb.is_object()) {
        d.meta.rollback.erase("previous");
    }
    d.full_live = full_live;
    d.base = std::move(base);
    d.old = std::move(old);
    for (ModInput& in : inputs) {
        in.code = nullptr;
        if (auto it = d.recorded.find(in.id); it != d.recorded.end())
            in.batches.insert(in.batches.end(), it->second.begin(), it->second.end());
    }
    d.recorded.clear();
    d.inputs = std::move(inputs);
    d.result = std::move(r);
    d.boot = boot;
    d.fronts = std::move(verdicts);
    d.counts.refused = static_cast<uint32_t>(d.result.refused.size());
    d.counts.conflicts = static_cast<uint32_t>(d.result.lines.size() + d.meta.hook_lines.size());
    uint32_t edits = 0;
    for (const auto& [ref, fo] : d.result.fields)
        for (const ChainRow& c : fo.chain) edits += c.kind == WriterKind::Registry;
    d.counts.edits = edits + static_cast<uint32_t>(d.result.added.size() + d.result.removed.size());
    return boot;
}

bool Runner::prepare_live(LiveData* out, std::string* why) {
    std::lock_guard lock(d_->mu);
    auto& d = *d_;
    if (phase_.load() < Phase::Start || !d.env.prepare_live) { *why = "the registry stage is not ready"; return false; }
    try {
        if (!d.full_live) {
            if (d.installing_live) { *why = "the registry live handoff is still in progress"; return false; }
            if (!d.inputs.empty()) { *why = "the initial registry base failed; a restart is required"; return false; }
            if (!d.lazy_error.empty()) { *why = d.lazy_error; return false; }
            if (!d.lazy_source && d.env.source) d.lazy_source = d.env.source(why);
            if (!d.lazy_source) return false;
            std::unique_ptr<model::Source> consumed; // destroyed after the model and inventory below
            {
                auto& source = *d.lazy_source;
                const std::string origin = source.name();
                Inventory inventory(source);
                model::Model model(inventory, d.rm, d.schema);
                model::Need need; need.all = true;
                const auto family = cache_family(d.env, d.rm, need);
                LiveData data;
                if (!read_base_cache(d.env, family, source, &data)) {
                    model.fill(need, &data.base);
                    model.id_table(&data.ids, &data.nums);
                    data.decoded = model.decoded();
                    write_base_cache(d.env, family, source, inventory, data.base, data.ids, data.nums, data.decoded);
                }
                d.name_archive(&data.base, data.ids, &model);
                data.old = d.lazy_old ? d.lazy_old(model) : d.old;
                data.result = compose(d.schema, data.base, data.old, d.inputs);
                data.result.refused.insert(data.result.refused.begin(), d.outside.begin(), d.outside.end());
                // Stable live data must not carry the model's raw resolver beyond this owned read session.
                data.base.raw = {};
                d.log_notes(model);
                d.base = std::move(data.base); d.old = std::move(data.old);
                d.live_result = std::move(data.result); d.result = d.live_result;
                d.ids = std::move(data.ids); d.nums = std::move(data.nums); d.decoded = std::move(data.decoded);
                d.meta.base.origin = origin;
                model.release();
                d.full_live = true; d.lazy_old = {};
                consumed = std::move(d.lazy_source);
            }
        }
        if (out) *out = {d.base, d.old, d.live_result, d.ids, d.nums, d.decoded};
        return true;
    } catch (const std::exception& error) {
        *why = std::string("the copied registry base could not be prepared: ") + error.what();
        return false;
    }
}

std::vector<FrontEnd> Runner::front_ends() const {
    std::lock_guard lk(d_->mu);
    return d_->fronts;
}

void Runner::finish(const std::vector<BootOutcome>& outcomes, const std::vector<FrontEnd>& applied) {
    Impl& d = *d_;
    std::vector<uint64_t> ready;
    LiveHandoff live;
    uint64_t gen = 0;
    std::string plan_rel, plan_text, report_text;  // written after the runner's lock is released (review finding 10)
    uint64_t start_seq = 0;                        // this start's report (a live report written later is newer)
    {
        std::lock_guard lk(d.mu);
        if (phase_.load() == Phase::Start || phase_.load() == Phase::WorldLoad) return;
        // the front-ends: what the resource patch writer did with each candidate the stage handed it. One it does not name
        // (its resource could not be read, no apply ran) was not written by the registry: its rows are the old path's again.
        std::set<std::string> kept;
        for (FrontEnd& x : d.fronts) {
            if (x.state != "pending") {
                kept.insert(x.mod + "/" + x.key);
                continue;
            }
            const auto a = std::find_if(applied.begin(), applied.end(), [&](const FrontEnd& y) { return y.mod == x.mod && y.key == x.key; });
            if (a != applied.end() && a->state == "registry") {
                x.state = "registry";
                continue;
            }
            x.state = "resourcePatch";
            x.why = a != applied.end() && !a->why.empty() ? a->why : "the resource patch writer did not hand it to the registry";
            kept.insert(x.mod + "/" + x.key);
        }
        if (!kept.empty()) {
            for (auto& [ref, fo] : d.result.fields)
                for (ChainRow& c : fo.chain)
                    if (c.front && kept.count(c.who)) c.front = false;
            for (OldWrite& w : d.old)
                if (w.front && kept.count(w.who)) w.front = false;
        }
        json front_rows = json::array();
        for (const FrontEnd& x : d.fronts) {
            json row = {{"mod", x.mod}, {"key", x.key}, {"state", x.state}, {"fields", x.fields}};
            if (!x.why.empty()) row["reason"] = x.why;
            front_rows.push_back(std::move(row));
            if (x.state == "registry")
                d.log(0, "registry: " + x.mod + "'s " + x.key + " is written by the registry's boot back-end (" + std::to_string(x.fields) +
                             " field(s), the same bytes as its resource patch)");
            else
                d.log(0, "registry: " + x.mod + "'s " + x.key + " stays a resource patch: " + x.why);
        }
        d.meta.front_ends = front_rows.empty() ? json(nullptr) : std::move(front_rows);
        for (const BootOutcome& o : outcomes) {
            auto fo = d.result.fields.find(o.field);
            if (fo == d.result.fields.end()) continue;
            if (o.ok) {
                if (!(fo->second.result == o.written))
                    d.log(1, "registry: " + o.field.text() + ": composed " + fo->second.result.text_of() + ", the resource holds " + o.written.text_of() +
                                 " after the old documents and the registry's rows (the report shows the resource's value)");
                fo->second.result = o.written;
                continue;
            }
            for (const ChainRow& c : fo->second.chain)
                if (c.kind == WriterKind::Registry)
                {
                    const Refusal x{c.mod, c.source, c.stage, c.n, o.field.registry + " " + o.field.id + ": " + o.field.field + ": " + o.why + "; this edit was not applied", "",
                                    o.field.registry, o.field.id, o.field.field};
                    d.result.refused.push_back(x);
                    d.outside.push_back(x);
                }
            d.result.fields.erase(fo);  // kiln.log names the refusal (report_log)
        }
        d.counts.refused = static_cast<uint32_t>(d.result.refused.size());
        d.boot_fields.clear();
        for (const auto& [ref, fo] : d.result.fields) {
            const RegistrySpec* rs = d.schema.registry(ref.registry);
            const FieldSpec* f = rs && !is_raw_field(ref.field) ? rs->field(ref.field) : nullptr;
            if (f && f->applies == Applies::Restart) d.boot_fields[ref] = fo;
        }
        // A separate additive fingerprint: never change kiln.boot for old peers. Use only successful boot writes,
        // after their actual resource values are known, and only mappings whose client needs its own copy. Compute
        // before report caps; preserve it across runtime reports (live changes cannot rebuild these boot values).
        std::set<FieldRef> written;
        for (const auto& o : outcomes) if (o.ok) written.insert(o.field);
        json values = json::array(), order = json::array();
        std::map<std::string, json> contributions;
        auto one_side = [&](const std::string& mod) {
            auto it = d.sides.find(mod);
            return it != d.sides.end() && !it->second.empty() && it->second != "both";
        };
        auto client_held = [&](const std::string& registry, const std::string& field) {
            auto reg = d.rm.registries.find(registry);
            if (reg == d.rm.registries.end()) return false;
            std::string g = generic_path(field);
            auto row = reg->second.rows.find(g);
            if (row == reg->second.rows.end() && g.size() > 2 && g.ends_with("[]")) row = reg->second.rows.find(g.substr(0, g.size() - 2));
            return row != reg->second.rows.end() && (row->second.backend == map::Backend::Boot || row->second.backend == map::Backend::Hook) &&
                   row->second.client_needs;
        };
        // Both sides hash their own fields, so a field one side leaves out and the other keeps would make every join differ, with
        // nothing the player can change. The part is published only when this start applied each client-held boot edit of the mods
        // both sides run (side both) as asked: none refused (by this build's mapping, by composition or by the write, or a refusal
        // Kiln cannot place: a faulted callback, an unreadable file), the base read, and no side server/client mod writing the same
        // field. Otherwise nothing is sent and nothing is compared with this process (kiln.log says why).
        std::set<std::string> ran;
        for (const auto& o : d.meta.order) ran.insert(o.mod);
        std::string not_compared;
        if (d.counts.failed) not_compared = "the stage could not read the game's base";
        for (const Refusal& x : d.result.refused) {
            if (!not_compared.empty()) break;
            if (x.mod.empty() || one_side(x.mod) || !ran.count(x.mod)) continue;
            if (x.registry.empty()) not_compared = "mod " + x.mod + " has a registry refusal that names no field (" + x.source + ")";
            else if (!x.raw && !x.field.empty() && client_held(x.registry, x.field))
                not_compared = "mod " + x.mod + "'s edit of " + x.registry + " " + x.field + " was refused on this game";
        }
        for (const auto& [ref, fo] : d.boot_fields) {
            if (!written.count(ref)) {
                // a refused write left d.result (the refusal scan above names it); this one no write reached at all
                if (not_compared.empty() && client_held(ref.registry, ref.field) &&
                    std::any_of(fo.chain.begin(), fo.chain.end(),
                                [&](const ChainRow& c) { return c.kind == WriterKind::Registry && !c.mod.empty() && !one_side(c.mod); }))
                    not_compared = ref.text() + " has a side-both mod's edit that no boot write reached";
                continue;
            }
            auto reg = d.rm.registries.find(ref.registry);
            if (reg == d.rm.registries.end()) continue;
            auto row = reg->second.rows.find(ref.field);
            if (row == reg->second.rows.end() || (row->second.backend != map::Backend::Boot && row->second.backend != map::Backend::Hook) ||
                !row->second.client_needs)
                continue;
            // a field a side server/client mod writes (any writer kind) differs by design: the other side never runs that mod. It
            // stays out on both sides only while no side-both mod edits it too (the other side keeps such a field).
            const auto foreign = std::find_if(fo.chain.begin(), fo.chain.end(), [&](const ChainRow& c) { return !c.mod.empty() && one_side(c.mod); });
            if (foreign != fo.chain.end()) {
                const bool shared = std::any_of(fo.chain.begin(), fo.chain.end(),
                                                [&](const ChainRow& c) { return c.kind == WriterKind::Registry && !c.mod.empty() && !one_side(c.mod); });
                if (shared && not_compared.empty())
                    not_compared = ref.text() + " is written by mod " + foreign->mod + " (side " + d.sides[foreign->mod] + ") and edited by a side-both mod";
                continue;
            }
            bool edited = false;
            for (const auto& c : fo.chain) {
                if (c.kind != WriterKind::Registry) continue;
                edited = true;
                auto& edits = contributions[c.mod];
                if (edits.is_null()) edits = json::array();
                edits.push_back({ref.registry, ref.id, ref.field, stage_name(c.stage), c.after.to_json()});
            }
            if (edited) values.push_back({ref.registry, ref.id, ref.field, fo.result.to_json()});
        }
        for (const auto& o : d.meta.order) {
            auto it = contributions.find(o.mod);
            if (it == contributions.end()) continue;
            order.push_back({o.mod, sha_hex(it->second.dump())});
            contributions.erase(it);
        }
        for (const auto& [mod, edits] : contributions) order.push_back({mod, sha_hex(edits.dump())});
        d.meta.boot_sync = nullptr;
        if (not_compared.empty())
            d.meta.boot_sync = {{"values", sha_hex("kiln-registry-boot-values 1\n" + values.dump())},
                                {"order", sha_hex("kiln-registry-boot-order 1\n" + order.dump())}};
        else
            d.log(1, "registry: this start's registry boot values are not compared at joins (" + not_compared +
                         "): players are checked by their mod lists and boot content only (docs/kiln/network.md)");
        d.generation = 1;
        d.meta.generation = 1;
        d.meta.world_load = false;
        d.meta.written = d.env.now ? d.env.now() : "";
        // the plan (P1-PLAN section 6; P1 writes it for debugging and the compat dumps)
        json bootj = json::array();
        for (const BootResource& b : d.boot) {
            json fs = json::array();
            for (const BootField& f : b.fields) fs.push_back({{"field", f.field.text()}, {"rows", f.rows.size()}});
            bootj.push_back({{"resource", key_text(b.key)}, {"type", b.type}, {"fields", fs}});
        }
        json livej = json::array();
        for (const auto& [ref, fo] : d.result.fields) {
            const RegistrySpec* rs = d.schema.registry(ref.registry);
            const FieldSpec* f = rs && !is_raw_field(ref.field) ? rs->field(ref.field) : nullptr;
            if (f && f->applies == Applies::Restart) continue;
            livej.push_back({{"field", ref.text()}, {"applies", f ? applies_word(f->applies) : "now"}, {"result", fo.result.to_json()}});
        }
        json adds = json::array();
        for (const EntryRef& a : d.result.added) adds.push_back({{"registry", a.registry}, {"id", a.id}, {"guid", model::added_guid(a.registry, a.id)}});
        json removed = json::array();
        for (const EntryRef& e : d.result.removed) removed.push_back({{"registry", e.registry}, {"id", e.id}});
        json key = {{"exeSha256", d.env.exe_sha256}, {"side", d.env.side}, {"kiln", d.env.kiln_version}, {"decoder", model::kDecoderVersion},
                    {"regmapRecord", d.meta.regmap.record}, {"order", json::array()}};
        for (const auto& o : d.meta.order) key["order"].push_back({{"mod", o.mod}, {"version", o.version}, {"inputs", o.edits_hash}});
        // registry-rollback: the plan keeps what a later start needs to run on it again: each mod's lowered edits (data
        // sections, queued batches and recorded callback turns, in the order they composed) and the values they resolved to
        json inputs = json::array();
        for (const ModInput& in : d.inputs) {
            std::string version;
            for (const auto& o : d.meta.order)
                if (o.mod == in.id) version = o.version;
            json batches = json::array();
            for (const Batch& b : in.batches) batches.push_back(batch_json(b));
            inputs.push_back({{"mod", in.id}, {"version", version}, {"batches", std::move(batches)}});
        }
        json plan_values = json::object();
        for (const auto& [ref, fo] : d.result.fields)
            if (has_registry_row(fo)) plan_values[ref.text()] = fo.result.to_json();
        json plan = {{"format", 2}, {"key", key}, {"boot", bootj}, {"live", livej}, {"adds", adds}, {"removed", removed}, {"values", plan_values}, {"inputs", inputs}};
        const std::string hash = d.rollback.empty() ? plan_hash_of(plan) : d.rollback;
        // previous: the last good plan that is not this one (a world started on it without a refused edit, plans/index.json)
        std::string previous = d.rollback_previous;
        if (d.rollback.empty()) {
            const json good = d.index.contains("good") && d.index["good"].is_object() ? d.index["good"] : json::object();
            const std::string g = good.contains("plan") && good["plan"].is_string() ? good["plan"].get<std::string>() : "";
            const std::string gp = good.contains("previous") && good["previous"].is_string() ? good["previous"].get<std::string>() : "";
            previous = g != hash ? g : gp;
        }
        if (d.env.write_data) {
            // a rolled-back start runs on a plan that is already written (and named by its own content): it is not rewritten
            if (d.rollback.empty()) {
                plan["previous"] = previous.empty() ? json(nullptr) : json(previous);
                plan_rel = "plans/" + hash + ".json", plan_text = plan.dump(1);
            }
            d.meta.plan = {hash, "kiln/data/content/registry/plans/" + hash + ".json", previous};
        }
        d.meta.saves = d.saves_json(d.result);
        const json doc = ::kc::registry::report_json(d.schema, d.result, d.meta);
        d.report = report_dump(doc);
        report_text = report_dump(doc, 1);
        d.report_job.reset();
        start_seq = d.report_built = ++d.report_seq;
        // refusals first (warnings, never crowded out by conflict lines), then the conflict lines, then one total
        const std::vector<std::string> all = report_log(d.result);  // refusals first (warnings), then conflicts, then the total
        for (size_t i = 0; i < all.size(); ++i) {
            const bool refusal = all[i].rfind("registry: refused ", 0) == 0, total = i + 1 == all.size();
            d.log(refusal || (total && !d.result.refused.empty()) ? 1 : 0, all[i]);
        }
        d.log(0, "registry: the stage ran (" + d.meta.order_source + " order, " + std::to_string(d.meta.order.size()) + " mod(s)): " + std::to_string(d.counts.edits) +
                     " edit(s) in effect or waiting for the world, " + std::to_string(d.counts.refused) + " refused, " + std::to_string(d.counts.conflicts) +
                     " conflict line(s); restart fields are final (phase start)");
        phase_.store(Phase::Start);
        gen = d.generation;
        for (const auto& [h, s] : d.ready_subs) ready.push_back(h);
        live = {&d.schema, &d.rm, &d.result, &d.ids, &d.nums, &d.base, &d.old, &d.inputs, &d.decoded, nullptr, &d.outside, &d.boot_fields, nullptr};
        live.rollback = d.rollback;
        live.left_out = d.left_out;
        live.check = [this](Batch* b, std::vector<Refusal>* refused) {
            std::lock_guard l2(d_->mu);
            refuse_unmapped(d_->rm, d_->schema, b, refused, d_->env.entry_support);
        };
        auto publish = [this](Result r, bool application, bool world_event, bool world) {
            std::vector<uint64_t> handles;
            uint64_t g2 = 0;
            std::string index_text;
            {
                std::lock_guard l2(d_->mu);
                // registry-rollback: a world started on this plan; without a refused edit it is the last good plan, and the good
                // one before it stays its "previous" (plans/index.json)
                if (world_event && !d_->meta.plan.hash.empty() && d_->env.write_data) {
                    json& idx = d_->index;
                    const std::string at = d_->env.now ? d_->env.now() : "";
                    idx["format"] = 1;
                    idx["world"] = {{"plan", d_->meta.plan.hash}, {"at", at}};
                    // the highest max stack each item a registry edit touches had in a world (saves.h stackCut): never lowered
                    if (!idx.contains("stacks") || !idx["stacks"].is_object()) idx["stacks"] = json::object();
                    for (const auto& [ref, fo] : r.fields) {
                        if (ref.registry != "items" || ref.field != "maxStack" || !has_registry_row(fo) || !fo.result.numeric()) continue;
                        json& top = idx["stacks"][ref.text()];
                        if (!top.is_number() || top.get<double>() < fo.result.as_double()) top = fo.result.to_json();
                    }
                    // review: a start whose stage had no base to read applied nothing of its plan: never the last good plan
                    if (r.refused.empty() && !d_->counts.failed) {
                        const json good = idx.contains("good") && idx["good"].is_object() ? idx["good"] : json::object();
                        const std::string g = good.contains("plan") && good["plan"].is_string() ? good["plan"].get<std::string>() : "";
                        if (g != d_->meta.plan.hash) idx["good"] = {{"plan", d_->meta.plan.hash}, {"previous", g.empty() ? json(nullptr) : json(g)}, {"at", at}};
                        else idx["good"]["at"] = at;
                    }
                    index_text = idx.dump(1);
                }
                d_->result = r;
                d_->counts.refused = static_cast<uint32_t>(r.refused.size());
                d_->counts.conflicts = static_cast<uint32_t>(r.lines.size() + d_->meta.hook_lines.size());
                d_->counts.edits = static_cast<uint32_t>(r.added.size() + r.removed.size());
                for (const auto& [ref, field] : r.fields)
                    for (const auto& row : field.chain) d_->counts.edits += row.kind == WriterKind::Registry;
                if (application) {
                    d_->meta.generation = ++d_->generation;
                    d_->meta.world_load = world;
                    g2 = d_->generation;
                }
                if (world_event) {
                    phase_.store(Phase::WorldLoad);
                    for (const auto& [h, s] : d_->ready_subs) handles.push_back(h);
                }
                d_->meta.written = d_->env.now ? d_->env.now() : "";
                d_->meta.saves = d_->saves_json(d_->result);
                // the report's JSON and file: env.post_report's thread (the result moves into the job, no second copy)
                auto job = std::make_shared<Impl::ReportJob>();
                job->seq = ++d_->report_seq;
                job->meta = d_->meta;
                job->result = std::make_shared<const Result>(std::move(r));
                d_->report_job = std::move(job);
            }
            // Outside the runner lock: transaction snapshots update the generation/file, never the ready event.
            if (!index_text.empty() && !d_->env.write_data("plans/index.json", index_text)) d_->log(1, "registry: kiln/data/content/registry/plans/index.json could not be written");
            if (d_->env.post_report) d_->env.post_report([d = d_.get()] { d->write_live_report(); });
            else d_->write_live_report();
            notify_ready(handles, Phase::WorldLoad, g2);
        };
        live.report = [publish](Result r, bool application) { publish(std::move(r), application, application, application); };
        live.changed = [publish](Result r, bool world) { publish(std::move(r), true, false, world); };
        if (d.env.prepare_live && !d.full_live) live.prepare = [this](LiveData* data, std::string* why) { return prepare_live(data, why); };
    }
    const bool plan_written = !plan_rel.empty() && d.env.write_data(plan_rel, plan_text);
    if (!plan_rel.empty() && !plan_written) d.log(1, "registry: the plan could not be written");
    if (start_seq) {
        std::lock_guard w(d.write_mu);
        if (start_seq > d.report_written) {
            if (d.env.write_report && !d.env.write_report(report_text)) d.log(1, "registry: state/registry.json could not be written");
            d.report_written = start_seq;
        }
    }
    // The installer copies borrowed handoff maps outside this lock (store -> runner is the lock order).
    // Fail closed for polling readers until that copy completes; preparation must not mutate those maps concurrently.
    { std::lock_guard lock(d.mu); d.installing_live = true; }
    auto complete_handoff = [&d] { std::lock_guard lock(d.mu); d.installing_live = false; };
    try {
        if (live_) live_(live);
        else {
            bool any = false;
            for (const auto& [ref, fo] : d.result.fields) {
                const RegistrySpec* rs = d.schema.registry(ref.registry);
                const FieldSpec* f = rs && !is_raw_field(ref.field) ? rs->field(ref.field) : nullptr;
                any = any || !f || f->applies != Applies::Restart;
            }
            if (any) d.log(1, "registry: this build of kiln.content has no live back-end: now and worldLoad fields are composed and reported, not applied");
        }
    } catch (...) { complete_handoff(); throw; }
    complete_handoff();
    notify_ready(ready, Phase::Start, gen);
    // the end of a successful start: plans no start needs any more go (never on an error path: a start without a base, an
    // index that could not be read, a refused rollback, a plan that was not written; an exception above never gets here)
    if (plan_written) prune_plans();
}

std::vector<std::string> plans_to_prune(const std::vector<Env::DataEntry>& files, const std::set<std::string>& keep, size_t newest) {
    std::vector<const Env::DataEntry*> others;
    for (const Env::DataEntry& f : files) {
        if (f.name.size() != 64 + 5 || f.name.compare(64, 5, ".json") != 0 || !is_hash(f.name.substr(0, 64))) continue;  // only plans
        if (!keep.count(f.name.substr(0, 64))) others.push_back(&f);
    }
    std::sort(others.begin(), others.end(), [](const Env::DataEntry* a, const Env::DataEntry* b) {
        return a->time != b->time ? a->time > b->time : a->name < b->name;
    });
    std::vector<std::string> out;
    for (size_t i = newest; i < others.size(); ++i) out.push_back(others[i]->name);
    return out;
}

// registry-fixes item 6 (review-registry-rollback Q5): each start with registry edits writes its plan, and none was ever
// removed, so the plans folder grew without bound. Kept: the last good plan and its previous, the plan the last world ran on
// (plans/index.json as this start read it), this start's plan and its previous, every plan a pending rollback.json names
// (read again now: kilnctl may have written one since the stage read it) with the plan it rolls away from, and the newest
// kKeptOtherPlans others; the other plan files are removed. Nothing is removed when which plans are needed is not known (an
// index or a rollback request that cannot be read) or when this start failed (no base, a refused rollback).
void Runner::prune_plans() {
    Impl& d = *d_;
    std::set<std::string> keep;
    {
        std::lock_guard lk(d.mu);
        if (!d.env.list_data || !d.env.remove_data || !d.env.read_data || d.counts.failed || d.index_unreadable ||
            d.meta.plan.hash.empty() || (d.meta.rollback.is_object() && d.meta.rollback.value("state", std::string()) == "refused"))
            return;
        keep.insert(d.meta.plan.hash);
        if (is_hash(d.meta.plan.previous)) keep.insert(d.meta.plan.previous);
        for (const char* k : {"good", "world"})
            if (d.index.contains(k) && d.index[k].is_object())
                for (const char* m : {"plan", "previous"})
                    if (d.index[k].contains(m) && d.index[k][m].is_string() && is_hash(d.index[k][m].get<std::string>()))
                        keep.insert(d.index[k][m].get<std::string>());
        if (is_hash(d.rollback)) keep.insert(d.rollback);
        if (is_hash(d.rollback_previous)) keep.insert(d.rollback_previous);
    }
    if (const auto req = d.env.read_data("rollback.json")) {
        const json q = json::parse(*req, nullptr, false);
        if (!q.is_object() || !q.contains("plan") || !q["plan"].is_string() || !is_hash(q["plan"].get<std::string>())) {
            d.log(1, "registry: plans: kiln/data/content/registry/rollback.json cannot be read, so no plan is removed at this start");
            return;
        }
        keep.insert(q["plan"].get<std::string>());
        if (q.contains("from") && q["from"].is_string() && is_hash(q["from"].get<std::string>())) keep.insert(q["from"].get<std::string>());
    }
    const std::vector<std::string> gone = plans_to_prune(d.env.list_data("plans"), keep, kKeptOtherPlans);
    size_t removed = 0;
    std::string failed;
    for (const std::string& name : gone) {
        if (d.env.remove_data("plans/" + name)) ++removed;
        else failed += (failed.empty() ? "" : ", ") + name.substr(0, 12);
    }
    if (removed)
        d.log(0, "registry: plans: removed " + std::to_string(removed) + " older plan(s) from kiln/data/content/registry/plans (kept: the last good "
                 "plan and its previous, the plan the last world ran on, this start's, a pending rollback's, and the newest " +
                 std::to_string(kKeptOtherPlans) + " others)");
    if (!failed.empty()) d.log(1, "registry: plans: could not remove " + failed + " (they stay; the next start tries again)");
}

uint64_t Runner::generation() const {
    std::lock_guard lk(d_->mu);
    return d_->generation;
}
Runner::Counts Runner::counts() const {
    std::lock_guard lk(d_->mu);
    return d_->counts;
}

std::string Runner::schema_json() {
    std::lock_guard lk(d_->mu);
    d_->load_regmap();
    json regs = json::object();
    for (const auto& [name, reg] : d_->schema.registries) {
        json fields = json::object();
        for (const auto& [fn, f] : reg.fields) {
            std::string reason;
            const map::Mapping mp = d_->rm.mapping(name, fn, &reason);
            json row = {{"applies", applies_word(f.applies)}, {"readOnly", f.read_only},
                        {"mapping", mp == map::Mapping::Mapped ? "mapped" : mp == map::Mapping::Unmapped ? "unmapped" : mp == map::Mapping::Unproven ? "unproven" : "removed"}};
            for (const auto& generated : gen::kFields)
                if (name == generated.registry && fn == generated.path) { row["joinedClient"] = generated.joined_client; break; }
            if (!reason.empty()) row["reason"] = reason;
            if (f.min) row["min"] = *f.min;
            if (f.max) row["max"] = *f.max;
            if (!f.enum_values.empty()) row["values"] = f.enum_values;
            if (!f.ref.empty()) row["ref"] = f.ref;
            fields[fn] = std::move(row);
        }
        regs[name] = {{"version", reg.version}, {"experimental", "KILNX031"}, {"fields", fields}};
    }
    json doc = {{"registrySchema", d_->schema.format}, {"registries", regs}};
    if (!d_->meta.boot_sync.is_null()) doc["bootSync"] = d_->meta.boot_sync;
    return doc.dump();
}

bool Runner::field_info(const std::string& q, FieldInfo* out) {
    const size_t dot = q.find('.');
    if (dot == std::string::npos) return false;
    const std::string reg = q.substr(0, dot), field = q.substr(dot + 1);
    std::lock_guard lk(d_->mu);
    d_->load_regmap();
    const RegistrySpec* rs = d_->schema.registry(reg);
    const FieldSpec* f = rs ? rs->field(field) : nullptr;
    if (!f) return false;
    out->applies = static_cast<uint32_t>(f->applies);
    out->mapping = static_cast<uint32_t>(d_->rm.mapping(reg, generic_path(field), &out->reason));
    out->type = f->type == FieldType::Int ? 1 : f->type == FieldType::Float ? 2 : f->type == FieldType::Bool ? 3 : f->type == FieldType::Enum ? 4 : f->type == FieldType::Id ? 5 : 6;
    out->read_only = f->read_only;
    if (out->mapping == 0) out->reason.clear();
    return true;
}

KilnResult Runner::read(uint32_t view, const std::string& registry, const std::string& id, const std::string& field, std::string* out) {
    if (view != 1 && phase_.load() >= Phase::Start && d_->env.prepare_live) {
        std::string why; if (!prepare_live(nullptr, &why)) return KILN_ERR_UNAVAILABLE;
    }
    std::lock_guard lk(d_->mu);
    const RegistrySpec* rs = d_->schema.registry(registry);
    if (!rs || view > 2 || (!rs->field(field) && !is_raw_field(field))) return KILN_ERR_INVALID_ARGUMENT;
    const FieldRef ref{registry, lc(id), field};
    if (view == 1) {
        if (!(d_->stage_thread_set && d_->stage_thread == std::this_thread::get_id())) return KILN_ERR_WRONG_THREAD;
        if (!d_->turn || !d_->turn->view) return KILN_ERR_UNAVAILABLE;
        if (!d_->turn->view->exists(registry, ref.id)) return KILN_ERR_NOT_FOUND;
        const auto v = d_->turn->view->get(ref);
        *out = v ? v->to_json().dump() : "null";
        return KILN_OK;
    }
    const Phase ph = phase_.load();
    if (view == 2 && ph != Phase::Start && ph != Phase::WorldLoad) return KILN_ERR_UNAVAILABLE;
    if (view == 0 && ph == Phase::None) return KILN_ERR_UNAVAILABLE;
    if (view == 2) {
        for (const EntryRef& e : d_->result.removed)
            if (e.registry == registry && e.id == ref.id) return KILN_ERR_NOT_FOUND;
        if (const auto it = d_->result.fields.find(ref); it != d_->result.fields.end()) return *out = it->second.result.to_json().dump(), KILN_OK;
        if (const auto entry = d_->result.definitions.find({registry, ref.id}); entry != d_->result.definitions.end()) {
            const auto value = entry->second.values.find(field);
            if (value == entry->second.values.end()) return KILN_ERR_UNAVAILABLE;
            return *out = value->second.to_json().dump(), KILN_OK;
        }
    }
    const auto reg = d_->base.registries.find(registry);
    if (reg != d_->base.registries.end())
        for (const BaseModel::Entry& e : reg->second) {
            if (e.id != ref.id || !e.removed_in.empty()) continue;
            const auto f = e.fields.find(field);
            if (f == e.fields.end()) return KILN_ERR_UNAVAILABLE;  // not read in this start
            return *out = f->second.to_json().dump(), KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult Runner::select(uint32_t view, const std::string& registry, const std::string& selector, std::string* out) {
    if (view != 1 && phase_.load() >= Phase::Start && d_->env.prepare_live) {
        std::string why; if (!prepare_live(nullptr, &why)) return KILN_ERR_UNAVAILABLE;
    }
    const json s = json::parse(selector, nullptr, false);
    std::string glob;
    std::vector<std::string> has, has_not;
    if (s.is_string()) glob = s.get<std::string>();
    else if (s.is_object() && s.contains("id") && s["id"].is_string()) {
        glob = s["id"].get<std::string>();
        if (s.contains("where")) return KILN_ERR_UNAVAILABLE;  // P2
        if (s.contains("has")) has = s["has"].get<std::vector<std::string>>();
        if (s.contains("hasNot")) has_not = s["hasNot"].get<std::vector<std::string>>();
    } else {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    std::lock_guard lk(d_->mu);
    if (!d_->schema.registry(registry) || view > 2) return KILN_ERR_INVALID_ARGUMENT;
    json ids = json::array();
    auto take = [&](const std::string& id, const std::set<std::string>& traits) {
        if (!glob_ci(glob, id)) return;
        for (const std::string& t : has)
            if (!traits.count(t)) return;
        for (const std::string& t : has_not)
            if (traits.count(t)) return;
        ids.push_back(id);
    };
    if (view == 1) {
        if (!(d_->stage_thread_set && d_->stage_thread == std::this_thread::get_id())) return KILN_ERR_WRONG_THREAD;
        if (!d_->turn || !d_->turn->view) return KILN_ERR_UNAVAILABLE;
        for (const std::string& id : d_->turn->view->ids(registry)) {
            std::set<std::string> traits;
            for (const std::string& t : has) if (d_->turn->view->has_trait(registry, id, t)) traits.insert(t);
            for (const std::string& t : has_not) if (d_->turn->view->has_trait(registry, id, t)) traits.insert(t);
            take(id, traits);
        }
        return *out = ids.dump(), KILN_OK;
    }
    const Phase ph = phase_.load();
    if (ph == Phase::None || (view == 2 && ph != Phase::Start && ph != Phase::WorldLoad)) return KILN_ERR_UNAVAILABLE;
    std::set<std::string> removed;
    if (view == 2)
        for (const EntryRef& e : d_->result.removed)
            if (e.registry == registry) removed.insert(e.id);
    if (const auto reg = d_->base.registries.find(registry); reg != d_->base.registries.end())
        for (const BaseModel::Entry& e : reg->second)
            if (e.removed_in.empty() && !removed.count(e.id)) take(e.id, e.traits);
    if (view == 2)
        for (const EntryRef& a : d_->result.added)
            if (a.registry == registry && !removed.count(a.id)) {
                const auto definition = d_->result.definitions.find(a);
                take(a.id, definition == d_->result.definitions.end() ? std::set<std::string>{} : definition->second.traits);
            }
    return *out = ids.dump(), KILN_OK;
}

KilnResult Runner::report_json(std::string* out) {
    std::shared_ptr<const Impl::ReportJob> job;
    {
        std::lock_guard lk(d_->mu);
        const Phase ph = phase_.load();
        if (ph != Phase::Start && ph != Phase::WorldLoad) return KILN_ERR_UNAVAILABLE;
        if (!d_->report_job || d_->report_job->seq <= d_->report_built) return *out = d_->report, KILN_OK;
        job = d_->report_job;  // a live report the writer has not made yet: made here, outside the lock
    }
    std::string text = report_dump(d_->report_doc(*job));
    std::lock_guard lk(d_->mu);
    if (job->seq > d_->report_built) d_->report = std::move(text), d_->report_built = job->seq;
    *out = d_->report;  // this job's text, or a newer one made meanwhile
    return KILN_OK;
}

void Runner::set_hook_lines(std::vector<Line> lines) {
    auto same = [](const std::vector<Line>& a, const std::vector<Line>& b) {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i)
            if (a[i].field != b[i].field || a[i].kind != b[i].kind || a[i].winner != b[i].winner || a[i].loser != b[i].loser ||
                a[i].certainty != b[i].certainty || a[i].text != b[i].text)
                return false;
        return true;
    };
    {
        std::lock_guard lk(d_->mu);
        if (same(lines, d_->meta.hook_lines)) return;
        d_->meta.hook_lines = std::move(lines);
        d_->counts.conflicts = static_cast<uint32_t>(d_->result.lines.size() + d_->meta.hook_lines.size());
        const Phase ph = phase_.load();
        if (ph != Phase::Start && ph != Phase::WorldLoad) return;  // the start's report (finish) takes them
        // the report again, as a live one: this result and meta (written off this thread when the runner has a writer thread)
        d_->meta.written = d_->env.now ? d_->env.now() : "";
        auto job = std::make_shared<Impl::ReportJob>();
        job->seq = ++d_->report_seq;
        job->meta = d_->meta;
        job->result = std::make_shared<const Result>(d_->result);
        d_->report_job = std::move(job);
    }
    if (d_->env.post_report) d_->env.post_report([d = d_.get()] { d->write_live_report(); });
    else d_->write_live_report();
}

std::string Runner::winner_of(const FieldRef& ref) const {
    std::lock_guard lk(d_->mu);
    const auto it = d_->result.fields.find(ref);
    return it == d_->result.fields.end() ? std::string() : it->second.winner;
}

}  // namespace kc::registry::stage
