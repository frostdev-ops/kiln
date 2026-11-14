// SPDX-License-Identifier: MIT
// kiln.ui.values inside kiln-core: what its files share (abi/services/kiln_ui_values.h is the public table).
//
//   ui_values.cpp          the table, the owner registry (quotas, ordered provider lists, faults), the value chain
//   ui_values_text.cpp     the process-lifetime text store
//   ui_values_bars.cpp     the item detail bar and the slot bar (mids before the game draws a temporary Bar)
//   ui_values_update.cpp   the player-UI update: HUD pairs, the held item, the pre-provider stage (below)
//   ui_values_service.cpp  the production environment (owners, kiln.caps, sigdb, kiln.hooks, kiln.client, kiln.ui),
//                          the SEH guards and the service's registration with the core
//
// Everything but ui_values_service.cpp is plain C++ over Env below, so native/core/tests/ui_values_test.cpp builds it
// with the host compiler against fakes.
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

#include "services/kiln_hooks.h"
#include "services/kiln_ui_values.h"

namespace kiln::uivalues {

// ---- limits (chapter 05a §5.1, §5.2) ----
inline constexpr uint32_t kValueKinds = 6;             // KUI_DETAIL .. KUI_MANA
inline constexpr uint32_t kRegistrationsPerMod = 128;  // value, line, slot and change subscriptions together
inline constexpr uint32_t kFaultLimit = 5;             // consecutive faults that remove a registration
inline constexpr size_t kTextMaxBytes = 4096;          // one string, without the NUL
inline constexpr size_t kCallbackTextBytes = 64 * 1024;
inline constexpr uint64_t kModTextBudget = 8ull << 20;
inline constexpr uint64_t kGlobalTextBudget = 64ull << 20;
inline constexpr uint32_t kGlobalStrings = 65536;
inline constexpr uint64_t kEntryOverhead = 1 + 64;  // charged per string on top of its bytes
inline constexpr uint64_t kMembershipCharge = 16;   // charged per mod holding a string
inline constexpr double kNewStringsPerSecond = 64;  // per mod
inline constexpr double kNewStringsBurst = 128;

// Registration lists: the value kinds (KilnUiValueKind), then these.
enum : uint32_t { kListLines = kValueKinds, kListSlots, kListChanges, kLists };

// The capability row of a list ("ui.values.hud" for the three HUD kinds).
const char* row_of(uint32_t list);

// ---- what the service takes from its host (production: ui_values_service.cpp; host tests: fakes) ----
struct ClientNow {
    uint32_t local_player = 0;  // kiln.client's local player, 0 = none
    uint64_t session = 0;       // client worlds since boot
    uint64_t step = 0;          // client-world steps since boot
};
struct Env {
    std::string (*owner_name)(KilnOwner) = nullptr;  // the stable mod id ("mod:<id>", "native:<id>")
    bool (*owner_alive)(KilnOwner) = nullptr;
    uint64_t (*registry_version)() = nullptr;  // moves when an owner is revoked
    double (*now_ms)() = nullptr;              // monotonic milliseconds (text rate)
    uint64_t (*tick_ms)() = nullptr;           // GetTickCount64 (capture times)
    bool (*client_side)() = nullptr;
    bool (*row_available)(const char* name) = nullptr;              // kiln.caps shows the row available
    void (*row_off)(const char* name, const char* text) = nullptr;  // reports the row unavailable (faulted)
    bool (*client)(ClientNow* out) = nullptr;
    bool (*screen)(KilnUiScreen* out) = nullptr;                 // kiln.ui's last completed screen
    bool (*read)(void* dst, uintptr_t src, size_t n) = nullptr;  // game memory, guarded
    bool (*write)(uintptr_t dst, const void* src, size_t n) = nullptr;
    bool (*symbol)(const char* name, uintptr_t* out) = nullptr;
    bool (*constant)(const char* name, int64_t* out) = nullptr;
    KilnResult (*hook_raw)(uintptr_t target, const char* label, void* fn, void** original) = nullptr;
    KilnResult (*hook_mid)(uintptr_t target, const char* label, KilnMidFn fn) = nullptr;
    // A provider called under a structured-exception guard: false = it faulted (*rc untouched).
    bool (*call_value)(KilnUiValueFn fn, const KilnUiProviderContext* c, const KilnUiItemView* v,
                       const KilnUiBarValue* in, KilnUiBarEdit* e, void* user, KilnResult* rc) = nullptr;
    void (*log)(int level, const char* text) = nullptr;
};
Env& env();
void logf(int level, const char* fmt, ...);

// ---- the registry ----
struct Provider {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    std::string mod;  // stable mod id at registration (quotas and text are charged to it)
    uint32_t list = 0;
    uint32_t item = 0;  // 0 = any nonempty item
    void* fn = nullptr;
    void* user = nullptr;
    uint64_t seq = 0;  // accepted registration order
    std::atomic<bool> live{true};
    std::atomic<uint32_t> faults{0};   // consecutive
    std::atomic<uint32_t> refusal{0};  // the last refusal reason logged (once per transition)
};
using ProviderList = std::vector<std::shared_ptr<Provider>>;

// Adds a registration after the caller checked the arguments. KILN_OK with out->ok = 0 and KUI_REGISTRATION_BUDGET
// when the mod holds 128; KILN_ERR_REVOKED for a retired owner.
KilnResult add(KilnOwner owner, uint32_t list, uint32_t item, void* fn, void* user, KilnUiRegistration* out);
KilnResult remove(KilnHandle h);
// The live providers of a list in registration order, as one immutable snapshot (changes made while a dispatch holds
// it apply to the next one). Never null.
std::shared_ptr<const ProviderList> providers(uint32_t list);
bool any(uint32_t list);              // lock-free: false = nothing registered (the hot path's early out)
size_t active(std::string_view mod);  // registrations the mod holds (tests)

// A fault of `p` (SEH, invalid edit, KILN_ERR_INTERNAL): the fifth in a row removes it.
void fault(Provider& p, const char* what);
// An accepted edit or a pass-through.
void succeeded(Provider& p);
// A refusal: logged once per reason transition, never a fault.
void refused(Provider& p, uint32_t reason);

// A kind's hooks: installed once, when the first provider of the kind registers and its row is available. Each kind's
// file registers its installer; false = the kind stays unavailable (its row is reported off).
using Installer = bool (*)(uint32_t list);
void set_installer(uint32_t list, Installer fn);
bool ensure_installed(uint32_t list);

// ---- the value chain ----
// Runs the providers of `kind` matching view.item over `game` in order and returns the accumulated value. Writes no
// game memory. A nested chain on the same thread (a provider reaching game UI code) passes through.
struct ChainStamp {
    uint64_t session = 0, frame = 0, step = 0;
};
KilnUiBarValue run_chain(uint32_t kind, const KilnUiItemView& view, const KilnUiBarValue& game,
                         const ChainStamp& stamp);
// Validates `e` against the kind's mask and the input; on success applies it to *acc. False = KUI_INVALID_EDIT.
bool apply_edit(uint32_t kind, const KilnUiBarEdit& e, const std::string& mod, KilnUiBarValue* acc);

// ---- game memory and sigdb through Env ----
inline bool sig_constant(const char* name, int64_t* out) { return env().constant && env().constant(name, out); }
inline bool sig_symbol(const char* name, uintptr_t* out) { return env().symbol && env().symbol(name, out) && *out; }
template <class T>
bool game_read(uintptr_t at, T* out) {
    return at && env().read && env().read(out, at, sizeof(T));
}

// ---- the item view (ui_values_bars.cpp) ----
inline constexpr size_t kViewBytes = 0x48;  // the ItemView prefix (ui/view.stride); a detail record's first 0x48 bytes
struct ViewLayout {                         // offsets inside the prefix (sigdb ui/view.*)
    int64_t page = 0, index = 0, item = 0, count = 0, pide = 0, durability = 0, durability_max = 0, fuel = 0,
            fuel_max = 0, level = 0, rarity = 0;
};
bool load_view_layout(ViewLayout* out);  // false when a constant is missing or does not fit the prefix
// Fills identity, flags and values of `v` from a copied prefix. base_flags: KUI_DETAIL_COPY for a detail record (an
// all-zero identity then makes it KUI_SYNTHETIC), 0 for a slot's or a model's view. server_entity stays 0.
void fill_view(const uint8_t* raw, const ViewLayout& layout, uint32_t base_flags, KilnUiItemView* v);
void load_charge_layout();                  // once; the balancing offsets for units_per_charge
uint32_t units_per_charge(uintptr_t play);  // max(1, the water charge amount) through play's balancing registry

// ---- the pre-provider stage of the player-UI update (ui_values_update.cpp) ----
// For 09-ui-blocks' snapshot service (chapter 09 §4.4: local vitals and inventory), so it never edits the update hook.
// What the game's player-UI update copied for the local player, BEFORE any held or HUD provider patched it: the
// original u32 pairs and the local player's own inventory (its own models only: no open container, no detail copy),
// nonempty slots sorted by (page, index). Called on the thread of the update (the client's main thread) right after
// the update's original returned and play+localEntity was checked against kiln.client's local player, before the
// providers run. Everything is borrowed for the call and holds no game pointer: copy what you keep. Tagged with the
// client world's session and step (kiln.client) and GetTickCount64 milliseconds. Not called without a local player or
// when the update's capture was ambiguous. The observer must not fault (it is core code; no SEH guard).
struct StageRow {  // the layout of chapter 09's KilnUiInventoryRow
    uint32_t holder, inventory_page, slot_index, item, count, pide;
};
static_assert(sizeof(StageRow) == 24);
inline constexpr uint32_t kStageRowsMax = 512;  // rows carried; rows_total says when there were more
enum : uint32_t {
    kStageVitals = 1,     // health / stamina / mana and their maxima are the game's
    kStageInventory = 2,  // rows / rows_total are the local player's inventory (0 rows = nothing in it)
};
struct Stage {
    uint32_t size;
    uint32_t available;  // kStage*: the parts that are valid (inventory needs the model layout to resolve)
    uint32_t local_player;
    uint32_t health, health_max, stamina, stamina_max, mana, mana_max;
    uint32_t rows_total;  // nonempty own slots found; above kStageRowsMax only the first 512 by (page, index) are here
    uint32_t row_count;
    const StageRow* rows;
    uint64_t session, step, capture_ms;
};
using StageFn = void (*)(const Stage* stage, void* user);
// Adds an observer (any thread). It needs the update hook, which comes with ui.values.hud: KILN_ERR_UNAVAILABLE while
// that row is held by the client gate or off. Owner 0 is the core's own; another owner's observers stop when it is
// revoked. Remove with stage_unsubscribe.
KilnResult stage_subscribe(KilnOwner owner, StageFn fn, void* user, KilnHandle* out);
KilnResult stage_unsubscribe(KilnHandle h);

// ---- text (ui_values_text.cpp) ----
namespace text {
struct GameText {  // the game's string view (24 bytes): UTF-8, byte length, signed-byte FNV-1a 64
    const char* p;
    uint64_t n;
    uint64_t fnv;
};
static_assert(sizeof(GameText) == 24);
uint64_t fnv64(const char* p, size_t n);
bool valid_utf8(std::string_view s);  // also refuses NUL
// Interns `s` for `mod`: KILN_OK + *out (0 for ""); KILN_ERR_INVALID_ARGUMENT (not UTF-8, a NUL);
// KILN_ERR_BUFFER_TOO_SMALL (a length, budget or rate limit: KUI_TEXT_BUDGET).
KilnResult intern(const std::string& mod, std::string_view s, KilnUiTextId* out);
bool held_by(KilnUiTextId id, std::string_view mod);  // id 0 counts as held
bool bytes(KilnUiTextId id, std::string_view* out);   // any id of the store; process-stable bytes
bool game_text(KilnUiTextId id, GameText* out);       // for a descriptor's label: permanent bytes
struct Usage {
    uint64_t charged = 0;  // bytes charged
    uint32_t strings = 0;  // strings (store) or memberships (a mod)
};
Usage usage(std::string_view mod);
Usage usage_all();
void reset_for_tests();
}  // namespace text

// The text ops a provider's context carries, and the call it may use them in.
struct CallState {
    uint32_t magic = 0;
    const Provider* provider = nullptr;
    KilnUiTextId input_label = 0;  // the accumulated input's label: resolvable and usable by this callback
    size_t bytes = 0;              // interned during this callback
    uint32_t latched = 0;          // KUI_TEXT_BUDGET after a refused intern: the callback is refused
};
const KilnUiTextOps* text_ops();
// Binds `cs` to the calling thread for the callback's duration (restores the previous one: nested calls).
struct CallScope {
    explicit CallScope(CallState* cs);
    ~CallScope();
    CallState* prev;
};

const KilnUiValuesApi* table();
void reset_for_tests();  // registry, installers' state and text (host tests only)

}  // namespace kiln::uivalues
