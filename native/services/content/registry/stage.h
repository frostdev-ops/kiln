// SPDX-License-Identifier: MIT
// kiln.content registry stage: the stage runner (registry P1 slice S11; P1-PLAN.md sections 1, 5.1, 6 and 8).
//
// Once per start, on the stage thread (the game's main thread at kiln.assets' resources-ready, before the game reads any
// resource Kiln changes): the mods' inputs (their data files' "registry" sections, the batches they submitted before the
// stage, their stage callbacks) in Kiln.Host's runtime load order; every edit of a field this game build has not mapped and
// proven refused per edit (regmap.h mapping); the base read through the model (model.h) and let go as soon as it is decoded;
// compose (compose.h); the boot back-end's writes handed to the resource patches (gamedata_bootpatch.cpp) and the live part
// to the live back-end; registry.json (report.h) written atomically, kiln.log's lines; phase START and the ready callbacks.
// The model's id table is written into kiln.content's data folder (never the repository), and the decoded base is cached
// there under a key of the base view's digest, the decoder's version, Kiln's version and the regmap record: a later start
// whose base reads the same bytes takes it from the cache and gives the same registry.json.
//
// The Runner holds no game state of its own: the environment (Env) gives it the base source, its files and its log, so the
// host tests run it as kiln-content.dll does (stage_service.cpp binds it to the tables, kiln.assets and the files).
// Threads: every member takes the runner's lock; no lock is held while a mod's callback runs.
#pragma once
#include <atomic>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include "compose.h"
#include "model.h"
#include "regmap.h"
#include "report.h"

namespace kc::registry::stage {

enum class Phase : uint32_t { None = 0, Add = 1, Alter = 2, Final = 3, Start = 4, WorldLoad = 5 };  // KILN_REGISTRY_PHASE_*
enum class Mode : uint32_t { ResourcesReady = 0, OnBoot = 1 };                                       // KILN_REGISTRY_MODE_*

struct Env {
    // The base of this start (the live bytes at resources-ready; in mode OnBoot a tree view). null + *why: none.
    std::function<std::unique_ptr<model::Source>(std::string* why)> source;
    // The regmap of the running build (regmap.h load_dir over <game>/kiln/regmap); *errs: rows loaded as unproven.
    std::function<bool(map::RegMap* out, std::vector<std::string>* errs)> regmap;
    std::function<KilnResult(const std::string& name, uint32_t state, uint32_t reason, const std::string& detail)> report_cap;
    // A mod's data sections: one row per pack file in load order (the files of mods the order does not name come after).
    struct DataFile {
        std::string mod, version, rel;  // rel: "content/<file>.json"
        std::vector<Batch> batches;
        std::vector<Refusal> refused;
        std::string error;  // the section is unusable (nothing of it is taken)
        std::string side;   // kiln.mod.json's "side" ("server", "client", "both"; empty: not known)
    };
    std::function<std::vector<DataFile>(const Schema&)> data;
    // The mod folders' order (without .NET: orderSource "folders"): ids with versions.
    std::function<std::vector<std::pair<std::string, std::string>>()> folders;
    // Files: registry.json under state; the id table, the plans and the cache under kiln.content's data folder (rel paths).
    std::function<bool(const std::string& text)> write_report;
    // Runs fn later on a thread of its own (kiln.content's report writer): a live report's JSON and state/registry.json, so a
    // world tick that publishes a report never makes either (owner 11-26). Unset: at once, on the publishing thread.
    std::function<void(std::function<void()> fn)> post_report;
    std::function<bool(const std::string& rel, const std::string& text)> write_data;
    std::function<std::optional<std::string>(const std::string& rel)> read_data;
    // The file is there (one file-system lookup, nothing read; proof-2 N7, as proof.h's exists); unset: read_data decides.
    std::function<bool(const std::string& rel)> data_exists;
    // The files of a data folder (rel, e.g. "plans"; not its subfolders): each name and its last write time (any unit, later is
    // larger), and removing one. The plans' pruning (registry-fixes item 6) uses them; unset: plans are never removed.
    struct DataEntry {
        std::string name;
        int64_t time = 0;
    };
    std::function<std::vector<DataEntry>(const std::string& dir)> list_data;
    std::function<bool(const std::string& rel)> remove_data;
    // The core's current owner state, checked under the runner lock at admission/dispatch (no callbacks into the runner).
    // Closes the interval between the core marking an owner dead and delivering its retiring notification.
    std::function<bool(uint64_t owner)> owner_alive;
    std::function<void(uint64_t owner)> owner_gone; // queued live-layer cleanup, outside the runner lock
    bool prepare_live = false; // support a cached full stable base, deferred until the first registry user
    std::function<bool(const std::string& registry, bool add, std::string* why)> entry_support;
    // kiln.content's stack guard on (store.cpp guard_set): before a start (or a rollback) gives an item a lower max stack than a
    // world already started with (plans/index.json "stacks"), so the game keeps the bigger stacks a save holds whole
    // (registry/saves.h). false + *why: not available here. Unset: not available.
    std::function<bool(std::string* why)> stack_guard;
    // The registries' schema (tests: a fixture's, with id migrations); unset: the generated one (schema_ir.h).
    std::optional<Schema> schema;
    std::function<void(int level, const std::string& line)> log;  // -1 debug ("log_level": "debug"), 0 info, 1 warn, 2 error
    std::function<std::string()> now;                              // RFC 3339 UTC
    uint32_t pid = 0;
    std::string kiln_version, side, exe_sha256;
    std::string writer = "none";  // the archive writer of this start (kiln-eml, shroudforge-hosted, shroudforge-installed, external, none)
    json archive;                 // what Kiln knows that writer did and what it rests on (archive_base.h): registry.json base.archive
    // The archive writer's change journal (archive_journal.h): its text and the generation this start's archive writer
    // published (lower-case hex). nullopt + *why: this start has none (why says which loader path keeps none). Asked once,
    // when the stage reads its base; unset: no journal.
    std::function<std::optional<std::string>(std::string* generation, std::string* why)> archive_journal;
};

// The boot back-end's part of one resource: the registry rows of each boot-routed field, evaluated over the value the old
// documents leave there and written after them, in the same copy (gamedata_bootpatch.cpp build).
// The plans' pruning rule (registry-fixes item 6): of a plans folder's files, the plan files (<sha256>.json) to remove: every
// one that `keep` does not name (plan hashes) and that is not among the `newest` others by time (ties: by name). Newest first.
std::vector<std::string> plans_to_prune(const std::vector<Env::DataEntry>& files, const std::set<std::string>& keep, size_t newest);
constexpr size_t kKeptOtherPlans = 10;  // the newest plans kept besides the named ones

struct BootField {
    FieldRef field;
    const map::RegistryMap* reg = nullptr;
    const map::Row* row = nullptr;
    FieldSpec spec;
    std::vector<ChainRow> rows;  // the registry's rows of the chain, in order
};
struct BootResource {
    KilnAssetKey key{};
    std::string type;
    std::vector<BootField> fields;
    // The front-end documents ("<mod>/<key>") whose values these fields' rows carry (their front rows): the resource patch
    // writer leaves them out of the copy when the registry's write gives the same bytes (gamedata_bootpatch.cpp apply).
    std::vector<std::string> front;
};
// One front-end document of this start (compose.h OldWrite::front): a data pack's "gearRules" (gear.h) that the registry's boot
// back-end writes itself. registry.json "frontEnds".
struct FrontEnd {
    std::string mod, key;  // "gear-values-sample", "gearRules@gear.json"
    // "registry": the registry's boot back-end wrote its values (the same bytes as its resource patch); "resourcePatch": the
    // resource patch did, as before (why says why)
    std::string state;
    std::string why;
    size_t fields = 0;  // the registry fields its values are
};
struct BootOutcome {
    FieldRef field;
    bool ok = false;
    Value written;     // the value now in the resource
    std::string why;   // not written: why (each of its registry rows is refused with it)
};

// Owned stable data returned by a deferred first-use preparation, with the initial writer composition.
struct LiveData {
    BaseModel base;
    std::vector<OldWrite> old;
    Result result;
    std::map<std::string, std::string> ids;
    std::map<std::string, uint32_t> nums;
    std::set<std::string> decoded;
};

// What the live back-end takes (lane D: store layers at world load, registry_live.cpp).
struct LiveHandoff {
    const Schema* schema = nullptr;
    const map::RegMap* regmap = nullptr;
    const Result* result = nullptr;
    const std::map<std::string, std::string>* ids = nullptr;  // "<registry>|<kiln id>" -> GUID text
    const std::map<std::string, uint32_t>* nums = nullptr;    // the same key -> the game's 32-bit id
    const BaseModel* base = nullptr;
    const std::vector<OldWrite>* old = nullptr;
    const std::vector<ModInput>* inputs = nullptr;  // code batches recorded, code unset
    const std::set<std::string>* decoded = nullptr; // "<registry>.<field>" the base holds values for
    std::function<void(Result, bool application)> report;  // registry.json again, generation + 1 for an application
    // kept across a recompose (review finding 6): the refusals made outside compose, the restart fields as the boot back-end
    // wrote them, and the mapping check for a reloaded batch
    const std::vector<Refusal>* pre_refused = nullptr;
    const std::map<FieldRef, FieldOutcome>* boot_fields = nullptr;
    std::function<void(Batch*, std::vector<Refusal>*)> check;
    // A transaction snapshot advances the report generation without pretending that another world loaded.
    std::function<void(Result, bool world)> changed;
    // Empty when the full copied base was prepared during the stage. Serialized and cached otherwise.
    std::function<bool(LiveData*, std::string*)> prepare;
    // Not empty: this start runs on a rolled-back plan (its hash; kiln/data/content/registry/rollback.json). The inputs are the
    // plan's edits: a pack reload does not replace them (it is reported RestartRequired until the rollback is cleared).
    std::string rollback;
    // review: the mods a rolled-back start leaves out (registry.json rollback.leftOut): their live changes are RestartRequired too.
    std::vector<std::string> left_out;
};

class Runner {
   public:
    explicit Runner(Env env);
    ~Runner();

    // ---- before the stage (kiln.internal.registry, kiln.registry) ----
    KilnResult set_order(const std::string& order_json);
    KilnResult set_mode(uint32_t mode);
    Mode mode() const { return mode_.load(); }
    // A stage callback of mod's (one per mod): it submits its edits from inside; false: it faulted (the edits it submitted in
    // that turn are dropped, the subscription removed, reported).
    using StageFn = std::function<bool(Stage, const View&)>;
    KilnResult subscribe_stage(uint64_t owner, const std::string& mod, StageFn fn, uint64_t* handle);
    using ReadyFn = std::function<bool(Phase, uint64_t)>;  // false: guarded native callback faulted; remove it
    KilnResult subscribe_ready(uint64_t owner, ReadyFn fn, uint64_t* handle);
    KilnResult unsubscribe(uint64_t handle);
    // One IR document. Before the stage: queued for its mod; inside that mod's stage callback (stage thread, the running
    // stage): taken into its batch. report: {"accepted", "refused": [{"n", "reason"}]}.
    KilnResult submit(uint64_t owner, const std::string& owner_mod, const std::string& ir, uint32_t* accepted, uint32_t* refused, std::string* report);
    void owner_gone(uint64_t owner);

    // ---- the stage ----
    using OldWritesFn = std::function<std::vector<OldWrite>(model::Model&)>;
    // Reads the base, composes, and returns the boot back-end's resources. The caller writes them (with the old documents)
    // and then calls finish() with what each field became. old(): the old paths' writes below the registry (boot patches).
    std::vector<BootResource> run(const OldWritesFn& old);
    // For deferred preparation, old owns its captures; freeze these resources before legacy boot writers change them.
    std::vector<BootResource> run(const OldWritesFn& old, const std::vector<std::pair<KilnAssetKey, std::string>>& freeze);
    // fronts: the front-end candidates among the old documents (OldWrite::front; id "<mod>/<key>", the resource's type). With
    // any, the stage reads the base at resources-ready (they need their fields' values), even when no mod has registry content.
    std::vector<BootResource> run(const OldWritesFn& old, const std::vector<std::pair<KilnAssetKey, std::string>>& freeze,
                                  const std::vector<std::pair<std::string, std::string>>& fronts);
    // fronts: what the resource patch writer did with each front-end document the stage handed it (BootResource::front).
    void finish(const std::vector<BootOutcome>& boot, const std::vector<FrontEnd>& fronts = {});
    // This start's front-end documents as reported (registry.json "frontEnds"), after finish.
    std::vector<FrontEnd> front_ends() const;
    void set_live(std::function<void(const LiveHandoff&)> install) { live_ = std::move(install); }

    // ---- reads (any thread) ----
    Phase phase() const { return phase_.load(); }
    uint64_t generation() const;
    struct Counts {
        uint32_t edits = 0, refused = 0, conflicts = 0;
        bool failed = false;
    };
    Counts counts() const;
    std::string schema_json();
    // A field's mapping on this build: applies, mapping (KILN_REGISTRY_MAPPED..REMOVED), type, read-only, reason.
    struct FieldInfo {
        uint32_t applies = 0, mapping = 0, type = 0;
        bool read_only = false;
        std::string reason;
    };
    bool field_info(const std::string& qualified, FieldInfo* out);
    // view 0 base, 1 stage (inside the caller's callback only), 2 resolved. Results: KILN_OK, NOT_FOUND, UNAVAILABLE, WRONG_THREAD.
    KilnResult read(uint32_t view, const std::string& registry, const std::string& id, const std::string& field, std::string* out);
    KilnResult select(uint32_t view, const std::string& registry, const std::string& selector, std::string* out);
    KilnResult report_json(std::string* out);
    bool on_stage_thread() const;

    // ---- other hooks of the game functions a hooks registry answers through (limits.h hook_lines; dev.32) ----
    // The conflict lines with them (another mod's or plugin's own hook of the same function: an older path), kept with the
    // report's meta so every later report (the start's, each live one) has them after the stage's own lines; a change after
    // the start writes state/registry.json again (no new generation, no ready event). Any thread.
    void set_hook_lines(std::vector<Line> lines);
    // The writer that decided a field's result in the stage (registry.json "winner"); empty: none (the base). Any thread.
    std::string winner_of(const FieldRef& ref) const;

   private:
    void prune_plans();  // at the end of a successful start (finish): the plans no start needs any more (registry-fixes item 6)
    bool prepare_live(LiveData* out, std::string* why);
    void notify_ready(const std::vector<uint64_t>& handles, Phase phase, uint64_t generation);
    struct Impl;
    std::unique_ptr<Impl> d_;
    std::atomic<Phase> phase_{Phase::None};
    std::atomic<Mode> mode_{Mode::ResourcesReady};
    std::function<void(const LiveHandoff&)> live_;
};

// The mapping check before the stage: every edit naming a field this build does not map (or has not proven) is taken out
// of its batch and refused with mapping()'s reason (an entry add naming one is refused whole, so a reference to it names
// that refusal).
void refuse_unmapped(const map::RegMap& rm, const Schema& schema, Batch* b, std::vector<Refusal>* refused);

// One mod's data files' "registry" sections (files by name, as kiln.content reads packs: comments allowed), lowered (ir.h
// lower_section). files: (rel "content/<file>.json", its text).
std::vector<Env::DataFile> read_sections(const std::string& mod, const std::string& version,
                                         const std::vector<std::pair<std::string, std::string>>& files, const Schema& schema);

// kiln-content.dll's runner (stage_service.cpp): its tables, kiln.assets' live bytes and its files.
Runner& runner();
// Binds the tables (kiln.registry v1, kiln.internal.registry v1) from kiln_plugin_main.
void bind_service();
// kiln.content's tick (content.cpp pre): another mod's or plugin's own hook of a function Kiln's limits hooks answer through,
// looked at every 2 s at most and only while such a hook answers (registry.json conflict lines; limits.h other_hooks).
void hooks_tick();
// kiln.content's exit step (KilnPluginInfo.on_unload): waits up to 2 s for the report writer (Env.post_report), so the newest
// live report is in state/registry.json before the process ends.
void flush_reports();

}  // namespace kc::registry::stage
