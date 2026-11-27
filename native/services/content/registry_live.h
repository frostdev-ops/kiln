// SPDX-License-Identifier: MIT
// kiln.content: the registry's live back-end (registry P1 slice S9; P1-PLAN.md sections 1 and 7).
//
// The registry stage (S11, at resources-ready) composes every mod's registry edits over the base, Kiln's older paths below
// (owner decision D1). Fields that apply "now" or "worldLoad" are written here, as store layers of one owner ("registry") in
// the seq band kRegistrySeq: above every data-pack layer, below code. Each field's registry rows are one evaluated layer
// per game slot: its value is computed from the value below it (the game's, or an old pack's) at every flush, so a pack hot
// reload re-evaluates relative edits over the pack's new value. Fields that apply "restart" are the boot back-end's (S10)
// and are not touched here. Compare-before-write (sfi R3) is the store's: a slot another writer wrote after Kiln's layer is
// skipped and reported, never silently overwritten.
//
// Kiln translates: mods name Kiln ids and Kiln fields only; the regmap rows (S2) say which game members a field is and how
// its value converts (int, float, bool, time, enum, formula, list, ...). Every game entry is found by the GUID / 32-bit id
// the stage's model gave its Kiln id; an entry a mod added by the GUID RegistryLive::added_guid gives it (model.h's rule).
//
// Hot reload (apply_all_packs with reload): the "registry" sections of the pack files are lowered again; when one changed,
// the stage is composed again over the kept inputs (base, old writes, every mod's batches with the changed data batches
// replaced), every registry layer is replaced (a removed pack's edits leave nothing behind) and the report callback
// rewrites registry.json. A changed edit naming a field the start never decoded is refused ("needs a restart").
#pragma once
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "content.h"
#include "registry/compose.h"
#include "registry/regmap.h"
#include "registry/runtime_writes.h"

namespace kc {
namespace registry::stage { struct LiveData; }

struct RegistryLive {
    std::shared_ptr<const registry::Schema> schema;
    std::shared_ptr<const registry::map::RegMap> regmap;
    registry::Result result;
    std::map<std::string, std::string> ids;  // "<registry>|<kiln id>" -> the game entry's GUID text (guid_text's form)
    std::map<std::string, uint32_t> nums;    // the same key -> the game's 32-bit id (items, recipes, loot, drops, inputCategories)
    // Kept for a hot reload's recomposition (ModInputs: code batches recorded, code unset).
    registry::BaseModel base;
    std::vector<registry::OldWrite> old;
    std::vector<registry::ModInput> mods;
    std::set<std::string> decoded;  // "<registry>.<field>" the base holds values for
    // Kept across a hot reload's recomposition (review finding 6): the refusals the stage made outside compose (its mapping
    // check, skipped mods, unusable sections, faulted callbacks, failed boot writes) and the restart fields' outcomes as the
    // boot back-end wrote them (they change only at the next start).
    std::vector<registry::Refusal> pre_refused;
    std::map<registry::FieldRef, registry::FieldOutcome> boot_fields;
    // The stage's mapping check (refuse_unmapped), run on every reloaded batch.
    std::function<void(registry::Batch*, std::vector<registry::Refusal>*)> check;
    // The GUID of an entry a mod added (S11 sets registry::model::added_guid: UUIDv5 of "registry:<registry>:<kiln id>").
    std::function<std::string(const std::string& registry, const std::string& kiln_id)> added_guid;
    // Internal entry backend. Preparation is GameStart-only; native definitions are retained until restart.
    std::function<bool(const registry::EntryRef&, const registry::json&, std::string*)> define_entry;
    std::function<bool(const registry::EntryRef&, const registry::json&, std::string*)> pin_definition;
    std::function<void(const registry::EntryRef&, const registry::json&)> record_entry;
    std::function<bool(const registry::EntryRef&, std::string*)> entry_registered;
    std::function<std::map<registry::EntryRef, std::string>(const registry::Result&, const std::map<std::string, uint32_t>&)> prepare_visibility;
    std::function<std::string(const std::vector<uintptr_t>&)> verify_visibility;
    std::function<void()> publish_visibility;
    std::function<void(const registry::EntryRef&)> forget_entry;
    std::map<registry::EntryRef, registry::json> entry_bodies;
    // registry.json (S11 writes it): a world load or pack reload passes true for a new application.
    // A refresh for late drop tables passes false so it does not send another WORLDLOAD event.
    std::function<void(registry::Result, bool application)> report;
    // A deferred transaction snapshot: generation +1, but never WORLDLOAD. The bool says whether a world exists.
    std::function<void(registry::Result, bool world)> changed;
    // Checked under store_mutex against the token/kind that gates all live accesses; false/missing fails closed.
    std::function<bool(uint32_t kind)> on_world_thread;
    std::function<bool(registry::stage::LiveData*, std::string*)> prepare; // optional copied-base preparation on first use
    // Not empty: the start runs on this rolled-back plan (stage.h LiveHandoff::rollback). mods hold the plan's edits; a pack
    // reload's changed registry sections do not replace them and are reported RestartRequired until the rollback is cleared.
    std::string rollback;
    // The mods that plan leaves out (registry.json rollback.leftOut): their live changes are RestartRequired until the rollback
    // is cleared, like their GameStart edits.
    std::vector<std::string> left_out;
    // The drift probe's suspects (registry/drift.h): asked once per world at registry_live_world_begin, with no lock of
    // kiln.content's held. Unset: none are named.
    std::function<std::vector<std::string>()> suspects;
    // The core's id of an owner (kiln.internal.owners owner_id: "emf:big_stacks" for an EMF DLL mod's owner), for a code
    // writer's row whose owner no "mod <id>" label names (registry-followups 1). Asked once per owner, under the store's lock
    // (a leaf lookup in the core that never calls out). Unset or empty: the owner's label names it.
    std::function<std::string(KilnOwner)> owner_name;
    // sfi R3: the newest recorded run-time write of each loaded-resource place, where it lies in game memory now
    // (kiln.assets loaded_write_records, placed through resource_data: registry/runtime_writes.h). Asked under the store's
    // lock, at most once per report and only when a row needs a name (a value that changed outside Kiln's layers: the drift
    // probe, a later write, a write compare-before-write skipped); kiln.assets copies under its own lock and calls nothing
    // back. Unset or empty: those rows stay "another writer" (an older kiln.assets, or no named write).
    std::function<std::vector<registry::RuntimeWrite>()> runtime_writes;
};

// One pack file's "registry" section, lowered (packs.cpp registry_sections).
struct RegistrySection {
    std::string mod, version, rel, source;  // source: "content/<file>.json" (the batches' source)
    std::string side;                       // the mod's kiln.mod.json "side"
    uint64_t hash = 0;                     // of the section's text: what a reload compares
    std::vector<registry::Batch> batches;       // add, alter, final (those present)
    std::vector<registry::Refusal> refused;
    std::string error;                     // the section itself unusable
};
// The registry sections of the files (in their order) checked against the schema (lower_section). Files without one are
// left out.
std::vector<RegistrySection> registry_sections(const std::vector<PackFile>& files, const registry::Schema& schema);
const registry::Schema& registry_schema();  // the generated schema (schema_ir.h), built once

// The stage hands its result over (S11, resources-ready). Replaces what an earlier start installed.
void registry_live_install(RegistryLive in);
// First-use stable perk identities for saved definitions. Cold preparation needs the registration thread, outside store_mutex.
bool registry_live_prepare_entry_identities(const registry::json& fields, std::string* why);
// Initialize an unpublished, list-owning content clone with stable fields. No store layers or transactions are created;
// a failure discards the clone before the game's id map can expose it. Called only on its world's registration thread.
bool registry_live_initialize_entry(uint32_t kind, const Entry& entry, const std::string& id,
                                    const registry::json& fields, std::string* why, const Schema* schema = nullptr);
registry::json registry_entry_definition(const registry::EntryRef& ref, const registry::EntryDefinition& definition,
                                         const RegistryLive& in, std::string* why);
void registry_entries_prepare(RegistryLive* in);
bool registry_entry_define_native(const registry::EntryRef&, const registry::json&, std::string* why);
void registry_entry_forget_native(const registry::EntryRef&);
// Snapshot the installed schema for lowering pack sections outside the store lock.
std::shared_ptr<const registry::Schema> registry_live_schema();
// With store_mutex held and the legacy pack layers applied, compare the lowered sections to the last application and
// put the live layers over them. Publish the resulting report separately after unlocking.
void registry_live_apply(const std::vector<RegistrySection>& sections, const registry::Schema* schema);
// Publish registry.json after applying packs or binding drop tables, through the stage's report callback.
// Call on the world thread with no store lock held.
void registry_live_publish();
// A drop table seen for the first time, or whose stacks moved (drops_apply_rules after newcontent repointed them): its
// registry layers come off the stacks they were on and go on the stacks the table has now.
void registry_live_table(const Entry& table);
// The last application's result as reported (the stage's plus the live refusals), a copy; none before any.
std::optional<registry::Result> registry_live_result();
// Explicit live changes (kiln.registry.live). Wrappers bind owners; the backend enforces the world thread under store_mutex.
enum class RegistryLiveOutcome : uint32_t { Applied, PendingWorld, Refused, RestartRequired, WorldRequired, Reverted, Skipped, Unverified, PendingResource };
struct RegistryLiveReply {
    RegistryLiveOutcome outcome = RegistryLiveOutcome::Refused;
    uint32_t accepted = 0, refused = 0, skipped = 0;
    uint64_t world = 0;
    std::string reason;
};
struct RegistryLiveStatus { uint64_t world = 0; uint32_t drafts = 0, active = 0, world_kind = KILN_WORLD_KINDS; };
KilnResult registry_live_begin(KilnOwner owner, const std::string& mod, uint32_t point, uint64_t* out);
KilnResult registry_live_submit(KilnOwner owner, uint64_t handle, const std::string& ir, RegistryLiveReply* out, std::string* report);
KilnResult registry_live_commit(KilnOwner owner, uint64_t handle, RegistryLiveReply* out);
KilnResult registry_live_revert(KilnOwner owner, uint64_t handle, RegistryLiveReply* out);
// One turn (dev.32, Registry.Reauthor's replace): commit's drafts commit in that order (each later than the one before) and
// replaced's retained changes come out, in one composition and one write of the bytes that change, so the game never holds
// the earlier layers gone without the new ones, nor both. A candidate whose own write, binding or slot fails rolls the whole
// turn back: no candidate stays and every replaced change is in its place again. *out: one reply per commit entry, then one per
// replaced entry (Reverted; Skipped or Unverified as a revert says them; Refused when the turn rolled back and it stayed). Every
// handle is its owner's (commit: drafts, replaced: retained changes), each once; world thread, like commit and revert.
struct RegistryLiveTurn {
    std::vector<std::pair<KilnOwner, uint64_t>> commit, replaced;
};
KilnResult registry_live_replace(const RegistryLiveTurn& turn, std::vector<RegistryLiveReply>* out);
// Registry.Reauthor off the world tick (dev.32 LOOT-2). A re-authored pass runs on a worker thread of Kiln.Host's and reads
// with registry_live_read's world; its calls (one IR per callback, in commit order) are parsed, composed, bound and evaluated
// off the world thread by registry_live_prepare, from the live state as it is (the store's lock held a slice at a time), and
// the world thread lands them with registry_live_install_turn: the changes, bindings and layers swap in and one flush writes the
// bytes that change. epoch: what a pass reads moves it (any composition, a world's begin or end, a write below the registry).
struct RegistryLiveCall {
    KilnOwner owner = 0;
    std::string mod;
    uint32_t point = 2;  // KILN_REGISTRY_LIVE_WORLD_LOAD
    std::string ir;
};
uint64_t registry_live_epoch();
// *ticket: the prepared turn. KILN_ERR_UNAVAILABLE and *stale: 1 the registry moved since `epoch` (run the pass again), 2 it
// moved while preparing (prepare again); also no world, or no live back-end. INVALID_ARGUMENT: an IR or a handle refused.
KilnResult registry_live_prepare(const std::vector<RegistryLiveCall>& calls, const std::vector<std::pair<KilnOwner, uint64_t>>& replaced,
                                 uint64_t epoch, uint64_t* ticket, uint32_t* stale);
// The world thread lands a prepared turn: *replies one per call then one per replaced change (as registry_live_replace's), and
// *handles one per call (its retained change, 0: none). KILN_ERR_UNAVAILABLE and *stale (the ticket is used up): 1 the pass's
// reads are stale, 2 only the preparation is; with kRegistryInstallAnyway a stale preparation lands the classic way instead (the
// same turn composed and written on the world thread), as a turn the preparation does not cover always does (a field evaluated
// over other fields' values, a removal, the stack guard). NOT_FOUND: no such ticket.
constexpr uint32_t kRegistryInstallAnyway = 1;
KilnResult registry_live_install_turn(uint64_t ticket, uint32_t flags, std::vector<RegistryLiveReply>* replies, std::vector<uint64_t>* handles,
                                      uint32_t* stale);
void registry_live_discard(uint64_t ticket);
// Builds the registry.json report a landed turn owes, off the world thread (the store's lock a slice at a time). False: the
// registry moved meanwhile (the world tick builds it if no later call does).
bool registry_live_build_report();
// exclude_mod (kiln.internal.registry read_pass, review4 NS5): the value without that mod's own registry rows.
// world (dev.32): a pass prepared off the world thread reads on any thread, in that world only (KILN_ERR_UNAVAILABLE in another).
KilnResult registry_live_read(const std::string& registry, const std::string& id, const std::string& field, std::string* out,
                              const std::string& exclude_mod = {}, uint64_t world = 0);
RegistryLiveStatus registry_live_status();
void registry_live_world_begin(uint32_t kind = KILN_WORLD_SERVER);
void registry_live_world_stop();
void registry_live_owner_gone(KilnOwner owner); // notification thread: mark only, never call the game
// How the live back-end sizes a game type (type_size; tests replace it).
extern uint32_t (*g_registry_type_size)(const char* qname);
// A monotonic clock in milliseconds (steady_ms; tests replace it): when registry_live_publish looks again for writes made
// after the last report.
uint64_t steady_ms();
extern uint64_t (*g_registry_now_ms)();
// Switches kiln.content's stack guard on (store.cpp guard_set) before a registry layer lowers a max stack below the value
// under it, so the game keeps the stacks a save holds whole (registry/saves.h "stackCut"); false + *why: not available
// (the edit is refused for that entry). Tests replace it.
extern bool (*g_registry_stack_guard)(std::string* why);
// The owner of the registry's layers (created on first use).
KilnOwner registry_owner();

}  // namespace kc
