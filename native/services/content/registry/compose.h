// SPDX-License-Identifier: MIT
// kiln.content registry stage: composition and conflicts (registry P1 slice S4; P1-PLAN.md section 5).
//
// compose() runs one start's stage over Kiln fields: the base (the archive as the game will read it, decoded through the
// regmap by S5's model), the writes of Kiln's older paths and named archive-writer packages below the registry (D1), then
// every mod's add, every mod's alter, every mod's final, mods in the runtime load order. The rules (the owner's public
// commitments, BRIEF section 12):
//   - add, alter and final each run across all mods before the next stage starts;
//   - later in load order wins and is reported ("overridden"); relative ops stack ("composed": x4 then x2 = x8);
//   - "exclusive": an edit of another writer on the same field is a conflict and the later edit is refused whole;
//   - "yield": the edit applies only where no earlier writer set the field;
//   - an edit is all or nothing over its selection (a refusal names the mod, its source and the edit's ordinal);
//   - final: every mod's final reads the same frozen post-alter view (plus its own final edits); its edits are held back
//     and composed after every final ran, in load order, with alter's rules;
//   - old paths are never refused because of a registry edit;
//   - the Advanced tier (raw member paths, D5): each path is resolved per entry on this game build through the base model;
//     one that does not resolve refuses its edit (never a blind write). A raw path that is exactly a Kiln field's member
//     on this build (BaseModel::raw_cover) is that field's place: the edit joins the field's chain, so it composes with the
//     field's other writers and every conflict between them is seen. A raw path behind a field Kiln converts (units, a
//     formula) is one place on two scales: an edit of the raw path and a writer of the field there (another mod, the same
//     mod, an old path) cannot be composed, so the later of the two is refused whole and reported (as "exclusive").
// The result is deterministic: it depends on the inputs and their order only. Plain data in, plain data out: no game
// memory, no service table, no lock, no clock.
#pragma once
#include <functional>
#include <memory>
#include <set>

#include "ir.h"

namespace kc::registry {

struct EntryRef {
    std::string registry, id;
    auto operator<=>(const EntryRef&) const = default;
};
struct FieldRef {
    std::string registry, id, field;
    auto operator<=>(const FieldRef&) const = default;
    std::string text() const { return registry + "[" + id + "]." + field; }
};

// A raw member path of one entry as this game build has it (S5's model resolves it through the game's reflection).
struct RawMember {
    FieldSpec spec;  // its type and range (the member's own type), read_only (identities), applies (the registry's back-end)
    Value value;     // its value in the base
};

// The base as Kiln fields (S5's model fills it from the base view; tests by hand).
struct BaseModel {
    struct Entry {
        std::string id;
        std::map<std::string, Value> fields;
        std::set<std::string> traits;
        std::string removed_in;  // not empty: the alias ledger says this game build removed it
        // Internal flattened stack ranges, one per drop group. Never part of schema fields or author-facing values.
        std::vector<std::pair<size_t, size_t>> drop_groups;
        // The archive-writer packages that wrote or created this entry's resource before Kiln, in the order they ran
        // ("shroudforge:<id>", "eml:<id>"; archive_journal.h). Each touched field of the entry gets one ArchiveWriter row
        // per package at the bottom of its chain, valued at the base (the archive's bytes are the base, D1).
        std::vector<std::string> archive;
        // A package of `archive` the journal names on some fields only (format 2's member paths): who -> those Kiln
        // fields. A package in `archive` and not here is named on the whole entry.
        std::map<std::string, std::set<std::string>> archive_fields;
    };
    std::map<std::string, std::vector<Entry>> registries;  // entries in the registry's own order
    std::set<std::string> retired;                          // "<registry>|<id>" the ids ledger retired: never added again

    // The Advanced tier: the member at raw path key (raw_key) of entry id, on this game build. nullopt + *why: it does not
    // resolve there (the game's names or layout changed, or the entry has no game data): every raw edit naming it there is
    // refused. Unset: no raw path resolves. An entry a mod added resolves through its "like" entry.
    std::function<std::optional<RawMember>(const std::string& registry, const std::string& id, const std::string& key, std::string* why)> raw;
    // Raw paths (keys) that are a Kiln field's member on this build, per registry (S2's regmap rows):
    // raw_cover["items"]["maxstacksize"] = {"maxStack", false}. converted: Kiln converts the value (units, a formula).
    struct Cover {
        std::string field;
        bool converted = false;
    };
    std::map<std::string, std::map<std::string, Cover>> raw_cover;
};

// Check: the stage's reference check itself (a list element naming an entry the stage removed, taken out of an entry of a
// registry whose entries are not removed). Drift: the live back-end's best-effort drift probe (drift.h): the game's value
// changed outside Kiln and no writer Kiln knows explains it (never composed; a report row only). Runtime (sfi R3): the
// same change, named: a ShroudForge package of kiln mode wrote the field at run time and kiln.assets' write records prove
// it (runtime_writes.h; a report row only, never composed).
enum class WriterKind : uint8_t { ArchiveWriter, OldPatch, OldPack, Code, Registry, Check, Drift, Runtime };
const char* writer_kind_name(WriterKind k);

// A write below the registry: an old Kiln path, or an archive-writer package the journal names (D1).
struct OldWrite {
    WriterKind kind = WriterKind::OldPatch;
    std::string mod;  // the Kiln mod behind it (empty for an archive-writer package)
    std::string who;  // "<mod>/<key>", "<mod>: content/<file>", "shroudforge:<package>"
    // field.field empty: an entry mark (boot_bridge.cpp old_writes), not a write: an old boot document wrote this entry,
    // whatever its paths map to. Composition skips it; the live drift probe (drift.h) never calls a change there another
    // loader's.
    FieldRef field;
    Op op;
    ListOp list = ListOp::None;  // a list write (op unused): clear, append, remove, replace
    std::vector<Value> list_values;
};

struct ChainRow {
    WriterKind kind = WriterKind::Registry;
    std::string raw;  // the raw member path as written, when this row came from an Advanced-tier edit
    ElementRef element;                             // an element edit of this list ("inputs[2].count"): op applies to that element
    std::shared_ptr<const FieldSpec> element_spec;  // ...typed by its leaf's (or the element's) spec (shared by the edit's rows)
    std::string mod;  // the Kiln mod (empty: an archive-writer package)
    std::string who;  // what the report names: the mod, or the old writer
    bool archive_field = false;  // an ArchiveWriter row the journal names on this field (its member paths), not the entry
    bool stand_in = false;       // a Registry row of a replayed package (Edit::stand_in): not an earlier writer for exclusive/yield
    Stage stage = Stage::Alter;
    std::string source;
    uint32_t n = 0;
    Op op;
    ListOp list = ListOp::None;
    std::vector<Value> list_values;
    bool exclusive = false;
    Value after;  // the field's value after this row
};

struct FieldOutcome {
    Value base;
    std::vector<ChainRow> chain;
    Value result;
    std::string winner;  // the writer whose row decided the result last (empty: the base)
    // A raw member's own chain (FieldRef::field "raw:<key>"): the path as first written, and the member's spec on this build
    // (a Kiln field's spec is the schema's). Back-ends evaluate the chain with it.
    std::string raw;
    std::shared_ptr<const FieldSpec> raw_spec;
    // compose's bookkeeping, not part of the result's meaning: the chain's last rows that are not the archive writer's (all,
    // exclusive ones), and for each the latest row of another mod before it, so "who else wrote here" is answered at once
    struct Summary {
        int last = -1, prev_other = -1, last_excl = -1, prev_excl_other = -1;
    } summary;
    std::vector<double> formula_inputs;  // effective inputs when the live back-end read a formula
};

enum class LineKind : uint8_t { Overridden, Composed, ComposedOn, Exclusive, Dropped, Yielded };
const char* line_kind_name(LineKind k);
std::string report_value_text(const Value& v);
// How sure a conflict line is (registry.json "certainty"): Kiln's own writers are seen for certain; an archive-writer package
// is named from its journal (sfi R1), a run-time writer from kiln.assets' write records (sfi R3); other loaders' DLLs and
// hooks (P4's drift probe) are best effort.
enum class Certainty : uint8_t { Certain, Named, BestEffort };
const char* certainty_name(Certainty c);
struct Line {
    LineKind kind = LineKind::Overridden;
    FieldRef field;
    std::string winner, loser, text;
    Certainty certainty = Certainty::Certain;
};

class View {
public:
    virtual ~View() = default;
    virtual bool exists(const std::string& registry, const std::string& id) const = 0;  // present and not removed
    virtual std::optional<Value> get(const FieldRef& f) const = 0;
    virtual std::vector<std::string> ids(const std::string& registry) const = 0;  // present entries, in order
    virtual bool has_trait(const std::string& registry, const std::string& id, const std::string& trait) const = 0;
};

// One mod's part of the stage. Its batches (data files by name, then edits recorded from code) run in the order given, at
// their stage; then code(stage, view, &batch), if set, adds the edits its callbacks make (C# OnRegistry through Kiln.Host).
struct ModInput {
    std::string id;
    std::vector<Batch> batches;
    // This mod's edits refused before the stage (the IR reader, the build's mapping check): not repeated in the result, but a
    // reference to an entry whose add is among them names that refusal as its cause.
    std::vector<Refusal> refused;
    std::function<void(Stage, const View&, Batch*)> code;
};

// Kiln-internal binding data, never a game pointer or a public ABI record. Constructor values are the Add-only
// baseline; values are the effective composed view. Later stages must not be baked into the native clone twice.
struct EntryDefinition {
    std::string like, base, mod, source;
    uint32_t n = 0;
    std::map<std::string, Value> constructor_values, values;
    std::set<std::string> traits;
};
struct EntryRemoval {
    std::string mod, source;
    Stage stage = Stage::Alter;
    uint32_t n = 0;
    std::string reason;
};

struct Result {
    std::map<FieldRef, FieldOutcome> fields;  // every touched field
    std::vector<Line> lines;                  // in the order they happened
    std::vector<Refusal> refused;
    std::vector<EntryRef> added, removed;
    std::map<EntryRef, EntryDefinition> definitions;
    std::map<EntryRef, EntryRemoval> removals;
};

Result compose(const Schema& schema, const BaseModel& base, const std::vector<OldWrite>& old, const std::vector<ModInput>& mods);
// A pack reload recomputes fields from their original baseline, but cannot invalidate an already established item removal.
// The caller retains the original GameStart entry operations in mods.
Result compose(const Schema& schema, const BaseModel& base, const std::vector<OldWrite>& old,
               const std::vector<ModInput>& mods, const Result& established);
// A later entry point: keep the earlier point's values, writer chains, exclusive/yield claims and report.
// Live callers refuse entry add/remove; this continuation is for field/list edits only.
Result compose_after(const Schema& schema, const BaseModel& base, const Result& previous, const std::vector<ModInput>& mods);

// Prepared immutable base for repeated stable field/list-only turns (no entry operations, raw edits or code).
// Build once when the copied base is installed; later turns use overlays rather than re-walking/copying that base.
struct PreparedBase;
std::shared_ptr<const PreparedBase> prepare_base(const BaseModel& base, const Result* entries = nullptr);
Result compose_after(const Schema& schema, const BaseModel& base, const PreparedBase& prepared,
                     const Result& previous, const std::vector<ModInput>& mods);

// The registry rows of a chain over another value below them (the live back-end: a pack's value under a relative layer).
Value evaluate(const std::vector<ChainRow>& rows, const Value& below, const FieldSpec& f);

}  // namespace kc::registry
