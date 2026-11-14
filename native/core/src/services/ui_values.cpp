// SPDX-License-Identifier: MIT
// kiln.ui.values v1 (abi/services/kiln_ui_values.h): the table, the owner registry and the value chain. Plain C++ over
// Env (ui_values_internal.h); ui_values_service.cpp wires it to the core.
//
// Registry. Every registration belongs to an owner and is counted against the owner's stable mod id (its registry
// name, which a reload of the same mod keeps): 128 at a time, value, line, slot and change subscriptions together.
// Each list keeps registration order. A dispatch takes the list's immutable snapshot once and calls from it; adding or
// removing changes the master list under the lock and publishes a new snapshot for the next dispatch, so nothing is
// ever added or removed under a running callback. A removed provider is flagged dead first: it is not called again,
// also not by a dispatch that still holds the old snapshot. Retired owners are dropped by the core's retirement
// notification; pruning when reg::version moves also covers hosts without that notification.
//
// Faults. A structured exception in a provider, an invalid edit or KILN_ERR_INTERNAL returned count as one fault; five
// in a row remove the registration (logged). An accepted edit or a pass-through resets the count; refusals do not
// count and are logged once per change of reason.
#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <exception>
#include <mutex>
#include <unordered_map>

#include "ui_values_internal.h"

namespace kiln::uivalues {
namespace {

constexpr KilnHandle kHandleTag = 0x5576ull << 48;  // kiln.ui.values handles ("Uv"): another service's are not found
constexpr KilnHandle kHandleMask = (1ull << 48) - 1;

// The capability row of each list (change subscriptions follow the value rows: none of their own).
const char* const kRows[kLists] = {"ui.values.detail",        "ui.values.slot",   "ui.values.held",
                                   "ui.values.hud",           "ui.values.hud",    "ui.values.hud",
                                   "ui.values.tooltip_lines", "ui.slots.inspect", nullptr};
const char* const kListNames[kLists] = {"detail", "slot",  "held",  "health", "stamina",
                                        "mana",   "lines", "slots", "changes"};

// Edit masks per value kind (chapter 05a §5.1).
constexpr uint32_t kMask[kValueKinds] = {
    KUI_FRACTION | KUI_CURRENT | KUI_MAXIMUM | KUI_LABEL | KUI_GAME_LABEL,  // detail
    KUI_FRACTION | KUI_CURRENT | KUI_MAXIMUM,                               // slot
    KUI_CURRENT | KUI_MAXIMUM,                                              // held
    KUI_CURRENT | KUI_MAXIMUM,                                              // health
    KUI_CURRENT | KUI_MAXIMUM,                                              // stamina
    KUI_CURRENT | KUI_MAXIMUM,                                              // mana
};

struct Registry {
    std::mutex mu;
    ProviderList all[kLists];
    std::shared_ptr<const ProviderList> snap[kLists];
    std::unordered_map<std::string, uint32_t> per_mod;
    uint64_t next_seq = 1;
};
Registry* g_reg = new Registry;  // never destroyed: a game thread may dispatch while the process ends
std::atomic<uint32_t> g_count[kLists];
std::atomic<uint64_t> g_revision[kLists];
std::atomic<uint64_t> g_reg_seen{0};

struct Installs {
    std::mutex mu;
    Installer fn[kLists] = {};
    int state[kLists] = {};  // 0 not tried, 1 installed, -1 failed
};
Installs& installs() {
    static Installs* i = new Installs;  // function-local: the installers register at static init of other files
    return *i;
}

const std::shared_ptr<const ProviderList>& empty_list() {
    static const auto* e = new std::shared_ptr<const ProviderList>(std::make_shared<const ProviderList>());
    return *e;
}

void publish_locked(uint32_t list) {
    g_reg->snap[list] = std::make_shared<const ProviderList>(g_reg->all[list]);
    g_count[list].store(static_cast<uint32_t>(g_reg->all[list].size()), std::memory_order_release);
    g_revision[list].fetch_add(1, std::memory_order_acq_rel);
}

// Takes `p` out of its list (lock held). False when it was not there.
bool drop_locked(const std::shared_ptr<Provider>& p) {
    auto& v = g_reg->all[p->list];
    const auto it = std::find(v.begin(), v.end(), p);
    if (it == v.end()) return false;
    p->live.store(false, std::memory_order_release);
    v.erase(it);
    auto m = g_reg->per_mod.find(p->mod);
    if (m != g_reg->per_mod.end() && m->second > 0 && --m->second == 0) g_reg->per_mod.erase(m);
    publish_locked(p->list);
    return true;
}

// Drops the registrations of owners the core no longer knows, when its owner registry moved since the last look.
void prune_if_moved() {
    if (!env().registry_version || !env().owner_alive) return;
    const uint64_t v = env().registry_version();
    if (v == g_reg_seen.load(std::memory_order_acquire)) return;
    size_t removed = 0;
    {
        std::lock_guard lk(g_reg->mu);
        g_reg_seen.store(v, std::memory_order_release);
        for (auto& list : g_reg->all) {
            std::vector<std::shared_ptr<Provider>> dead;
            for (const auto& p : list)
                if (!env().owner_alive(p->owner)) dead.push_back(p);
            for (const auto& p : dead) removed += drop_locked(p) ? 1 : 0;
        }
    }
    if (removed) logf(KILN_LOG_DEBUG, "ui.values: %zu registration(s) of retired owners removed", removed);
}

uint32_t reason_of(uint32_t r) { return r >= KUI_REJECTED && r <= KUI_NO_TOOLTIP ? r : KUI_REJECTED; }

void clear_registration(KilnUiRegistration* out) {
    out->ok = 0;
    out->reason = KUI_REASON_NONE;
    out->reserved = 0;
    out->handle = 0;
}

template <class F>
KilnResult guarded(const char* what, F&& f) noexcept {
    try {
        return f();
    } catch (const std::exception& e) {
        logf(KILN_LOG_ERROR, "ui.values: %s failed: %s", what, e.what());
    } catch (...) {
        logf(KILN_LOG_ERROR, "ui.values: %s failed", what);
    }
    return KILN_ERR_INTERNAL;
}

// Registration shared by every list: the caller checked the list-specific arguments.
KilnResult register_in(KilnOwner owner, uint32_t list, uint32_t item, void* fn, void* user, KilnUiRegistration* out) {
    if (!env().client_side || !env().client_side()) return KILN_ERR_UNAVAILABLE;
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;
    if (list == kListLines && (!env().row_available || !env().row_available("kiln.ui")))
        return KILN_ERR_UNAVAILABLE;  // a missing window backend can appear later; do not latch an install failure
    if (!ensure_installed(list)) return KILN_ERR_UNAVAILABLE;
    return add(owner, list, item, fn, user, out);
}

// ---- the table ----
KilnResult KILN_CALL t_subscribe(KilnOwner owner, uint32_t kind, uint32_t item_filter, KilnUiValueFn fn, void* user,
                                 KilnUiRegistration* out) {
    if (!out || out->size < sizeof(KilnUiRegistration)) return KILN_ERR_INVALID_ARGUMENT;
    clear_registration(out);
    if (owner == 0 || !fn || kind >= kValueKinds) return KILN_ERR_INVALID_ARGUMENT;
    if (kind >= KUI_HEALTH && item_filter != 0) return KILN_ERR_INVALID_ARGUMENT;  // HUD kinds: no item
    return guarded("subscribe",
                   [&] { return register_in(owner, kind, item_filter, reinterpret_cast<void*>(fn), user, out); });
}

KilnResult KILN_CALL t_tooltip_lines(KilnOwner owner, uint32_t item_filter, KilnUiLinesFn fn, void* user,
                                     KilnUiRegistration* out) {
    if (!out || out->size < sizeof(KilnUiRegistration)) return KILN_ERR_INVALID_ARGUMENT;
    clear_registration(out);
    if (owner == 0 || !fn) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("tooltip_lines",
                   [&] { return register_in(owner, kListLines, item_filter, reinterpret_cast<void*>(fn), user, out); });
}

KilnResult KILN_CALL t_unsubscribe(KilnHandle h) {
    return guarded("unsubscribe", [&] { return remove(h); });
}

KilnResult KILN_CALL t_copy_slots(KilnOwner owner, uint64_t expected_session, uint64_t expected_frame,
                                  KilnUiSlotsFrame* frame, KilnUiVisibleSlot* slots, uint32_t capacity,
                                  uint32_t* required) {
    if (owner == 0 || !frame || frame->size < sizeof(KilnUiSlotsFrame) || !required || (capacity && !slots))
        return KILN_ERR_INVALID_ARGUMENT;
    return guarded("copy_slots", [&] {
        return slots_copy(owner, expected_session, expected_frame, frame, slots, capacity, required);
    });
}

KilnResult KILN_CALL t_subscribe_slots(KilnOwner owner, KilnUiSlotsFn fn, void* user, KilnUiRegistration* out) {
    if (!out || out->size < sizeof(KilnUiRegistration)) return KILN_ERR_INVALID_ARGUMENT;
    clear_registration(out);
    if (owner == 0 || !fn) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("subscribe_slots",
                   [&] { return register_in(owner, kListSlots, 0, reinterpret_cast<void*>(fn), user, out); });
}

KilnResult KILN_CALL t_subscribe_changes(KilnOwner owner, KilnUiValueChangedFn fn, void* user,
                                         KilnUiRegistration* out) {
    if (!out || out->size < sizeof(KilnUiRegistration)) return KILN_ERR_INVALID_ARGUMENT;
    clear_registration(out);
    if (owner == 0 || !fn) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("subscribe_changes", [&] { return changes_subscribe(owner, fn, user, out); });
}

KilnResult KILN_CALL t_open_tooltip_details(KilnOwner owner, KilnUiTooltipOpen* out) {
    if (owner == 0 || !out || out->size < sizeof(KilnUiTooltipOpen)) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("open_tooltip_details", [&] { return tooltip_open(owner, out); });
}

KilnResult KILN_CALL t_close_tooltip_details(KilnOwner owner) {
    if (owner == 0) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("close_tooltip_details", [&] { return tooltip_close(owner); });
}

KilnResult KILN_CALL t_tooltip_details(KilnOwner owner, uint32_t which, KilnUiTooltipDetails* out) {
    if (owner == 0 || !out || out->size < sizeof(KilnUiTooltipDetails)) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("tooltip_details", [&] { return tooltip_details(owner, which, out); });
}

KilnResult KILN_CALL t_refresh_tooltip_details(KilnOwner owner, KilnUiTooltipOpen* out) {
    if (owner == 0 || !out || out->size < sizeof(KilnUiTooltipOpen)) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("refresh_tooltip_details", [&] { return tooltip_refresh(owner, out); });
}

const KilnUiValuesApi kTable = {
    {sizeof(KilnUiValuesApi), KILN_UI_VALUES_VERSION},
    &t_subscribe,
    &t_tooltip_lines,
    &t_unsubscribe,
    &t_copy_slots,
    &t_subscribe_slots,
    &t_subscribe_changes,
    &t_open_tooltip_details,
    &t_close_tooltip_details,
    &t_tooltip_details,
    &t_refresh_tooltip_details,
};

// ---- text ops for providers ----
thread_local CallState* t_call = nullptr;

CallState* call_of(const KilnUiProviderContext* c) {
    if (!c || c->size < sizeof(KilnUiProviderContext)) return nullptr;
    CallState* cs = t_call;
    if (!cs || c->reserved != cs || cs->magic != kCallMagic || !cs->provider) return nullptr;
    return cs;
}

KilnResult KILN_CALL op_intern(const KilnUiProviderContext* c, KilnStr s, KilnUiTextId* out) {
    CallState* cs = call_of(c);
    if (!cs || !out || (s.len && !s.ptr)) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    if (s.len > kCallbackTextBytes - std::min(cs->bytes, kCallbackTextBytes)) {
        cs->latched = KUI_TEXT_BUDGET;
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    const KilnResult rc = guarded(
        "intern", [&] { return text::intern(cs->provider->mod, std::string_view(s.ptr ? s.ptr : "", s.len), out); });
    if (rc == KILN_OK)
        cs->bytes += s.len;
    else if (rc == KILN_ERR_BUFFER_TOO_SMALL)
        cs->latched = KUI_TEXT_BUDGET;
    return rc;
}

KilnResult KILN_CALL op_resolve(const KilnUiProviderContext* c, KilnUiTextId id, KilnStr* out) {
    CallState* cs = call_of(c);
    if (!cs || !out) return KILN_ERR_INVALID_ARGUMENT;
    return guarded("resolve", [&] {
        if (id != 0 && id != cs->input_label && !text::held_by(id, cs->provider->mod)) return KILN_ERR_NOT_FOUND;
        std::string_view v;
        if (!text::bytes(id, &v)) return KILN_ERR_NOT_FOUND;
        *out = KilnStr{v.data(), v.size()};
        return KILN_OK;
    });
}

const KilnUiTextOps kTextOps = {sizeof(KilnUiTextOps), 1, &op_intern, &op_resolve};

thread_local int t_chain_depth = 0;

}  // namespace

// ---- environment and log ----
Env& env() {
    static Env* e = new Env;
    return *e;
}

void logf(int level, const char* fmt, ...) {
    if (!env().log) return;
    char text[768];
    va_list a;
    va_start(a, fmt);
    std::vsnprintf(text, sizeof text, fmt, a);
    va_end(a);
    env().log(level, text);
}

const char* row_of(uint32_t list) { return list < kLists ? kRows[list] : nullptr; }

// ---- registry ----
KilnResult add(KilnOwner owner, uint32_t list, uint32_t item, void* fn, void* user, KilnUiRegistration* out) {
    prune_if_moved();
    if (list >= kLists || !fn || !out) return KILN_ERR_INVALID_ARGUMENT;
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;
    std::string mod = env().owner_name(owner);
    std::lock_guard lk(g_reg->mu);
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;  // retirement may have won before the lock
    uint32_t& held = g_reg->per_mod[mod];
    if (held >= kRegistrationsPerMod) {
        out->ok = 0;
        out->reason = KUI_REGISTRATION_BUDGET;
        out->handle = 0;
        return KILN_OK;
    }
    auto p = std::make_shared<Provider>();
    p->seq = g_reg->next_seq++;
    p->handle = kHandleTag | (p->seq & kHandleMask);
    p->owner = owner;
    p->mod = std::move(mod);
    p->list = list;
    p->item = item;
    p->fn = fn;
    p->user = user;
    g_reg->all[list].push_back(p);
    ++held;
    publish_locked(list);
    out->ok = 1;
    out->reason = KUI_REASON_NONE;
    out->handle = p->handle;
    return KILN_OK;
}

KilnResult remove(KilnHandle h) {
    if ((h & ~kHandleMask) != kHandleTag) return KILN_ERR_NOT_FOUND;
    prune_if_moved();
    std::lock_guard lk(g_reg->mu);
    for (auto& list : g_reg->all)
        for (const auto& p : list)
            if (p->handle == h) {
                const auto keep = p;  // drop_locked erases the list's reference
                drop_locked(keep);
                return KILN_OK;
            }
    return KILN_ERR_NOT_FOUND;
}

std::shared_ptr<const ProviderList> providers(uint32_t list) {
    if (list >= kLists || g_count[list].load(std::memory_order_acquire) == 0) return empty_list();
    prune_if_moved();
    std::lock_guard lk(g_reg->mu);
    return g_reg->snap[list] ? g_reg->snap[list] : empty_list();
}

bool any(uint32_t list) { return list < kLists && g_count[list].load(std::memory_order_acquire) != 0; }
uint64_t providers_revision(uint32_t list) { return g_revision[list].load(std::memory_order_acquire); }

bool provider_live(const Provider& p) {
    return p.live.load(std::memory_order_acquire) && env().owner_alive(p.owner);
}

void owner_retired(KilnOwner owner) {
    {
        std::lock_guard lk(g_reg->mu);
        for (auto& list : g_reg->all) {
            const auto snapshot = list;
            for (const auto& p : snapshot)
                if (p->owner == owner) drop_locked(p);
        }
    }
    stage_owner_retired(owner);
}

size_t active(std::string_view mod) {
    std::lock_guard lk(g_reg->mu);
    const auto it = g_reg->per_mod.find(std::string(mod));
    return it == g_reg->per_mod.end() ? 0 : it->second;
}

void fault(Provider& p, const char* what) {
    const uint32_t n = p.faults.fetch_add(1, std::memory_order_acq_rel) + 1;
    if (n == 1)
        logf(KILN_LOG_WARN, "ui.values: %s provider of %s %s (fault 1 of %u in a row)", kListNames[p.list],
             p.mod.c_str(), what, kFaultLimit);
    if (n < kFaultLimit) return;
    bool removed = false;
    {
        std::lock_guard lk(g_reg->mu);
        for (const auto& sp : g_reg->all[p.list])
            if (sp.get() == &p) {
                const auto keep = sp;
                removed = drop_locked(keep);
                break;
            }
    }
    if (removed)
        logf(KILN_LOG_WARN,
             "ui.values: %s provider of %s removed after %u faults in a row (last: %s); the game's "
             "own value is drawn again",
             kListNames[p.list], p.mod.c_str(), kFaultLimit, what);
}

void succeeded(Provider& p) {
    p.faults.store(0, std::memory_order_release);
    p.refusal.store(KUI_REASON_NONE, std::memory_order_release);
}

void refused(Provider& p, uint32_t reason) {
    if (p.refusal.exchange(reason, std::memory_order_acq_rel) != reason)
        logf(KILN_LOG_DEBUG, "ui.values: %s provider of %s refused its edit (reason %u)", kListNames[p.list],
             p.mod.c_str(), reason);
}

void set_installer(uint32_t list, Installer fn) {
    if (list >= kLists) return;
    std::lock_guard lk(installs().mu);
    installs().fn[list] = fn;
}

bool ensure_installed(uint32_t list) {
    const char* row = row_of(list);
    if (!row || !env().row_available || !env().row_available(row)) return false;
    Installs& in = installs();
    std::lock_guard lk(in.mu);
    if (in.state[list] != 0) return in.state[list] > 0;
    if (!in.fn[list]) return false;  // not built in this version
    const bool ok = in.fn[list](list);
    in.state[list] = ok ? 1 : -1;
    if (ok) {
        logf(KILN_LOG_INFO, "ui.values: %s hooks installed (%s)", kListNames[list], row);
    } else {
        logf(KILN_LOG_ERROR, "ui.values: %s hooks could not be installed; %s is off for this process", kListNames[list],
             row);
        if (env().row_off) env().row_off(row, "kiln.ui.values could not install its game hooks; kiln.log says why");
        changes_reset(KUI_RESET_CAPABILITY);
    }
    return ok;
}

// ---- the value chain ----
bool apply_edit(uint32_t kind, const KilnUiBarEdit& e, const std::string& mod, KilnUiBarValue* acc) {
    if (kind >= kValueKinds) return false;
    const uint32_t f = e.flags;
    if (f & ~kMask[kind]) return false;
    if ((f & KUI_LABEL) && (f & KUI_GAME_LABEL)) return false;
    if ((f & KUI_FRACTION) && !(std::isfinite(e.fraction) && e.fraction >= 0.0f && e.fraction <= 1.0f)) return false;
    if (kind == KUI_DETAIL &&
        (((f & KUI_CURRENT) && e.current > 0x7FFFFFFFu) || ((f & KUI_MAXIMUM) && e.maximum > 0x7FFFFFFFu)))
        return false;
    if (kind >= KUI_HELD && (f & KUI_MAXIMUM) && e.maximum == 0) return false;
    if ((f & KUI_LABEL) && e.label_id != 0) {
        const KilnUiTextId input = acc->label_is_override ? acc->label_id : 0;
        if (e.label_id != input && !text::held_by(e.label_id, mod)) return false;
        std::string_view probe;
        if (!text::bytes(e.label_id, &probe)) return false;
    }
    if (f & KUI_FRACTION) acc->fraction = e.fraction;
    if (f & KUI_CURRENT) acc->current = e.current;
    if (f & KUI_MAXIMUM) acc->maximum = e.maximum;
    if (f & KUI_LABEL) {
        acc->label_is_override = 1;
        acc->label_id = e.label_id;
    } else if (f & KUI_GAME_LABEL) {
        acc->label_is_override = 0;
        acc->label_id = 0;
    }
    if (kind >= KUI_HELD) {  // held and HUD: the fraction is the pair's, read-only
        const double r = acc->maximum ? static_cast<double>(acc->current) / acc->maximum : 0.0;
        acc->fraction = static_cast<float>(std::min(1.0, std::max(0.0, r)));
    }
    return true;
}

KilnUiBarValue run_chain(uint32_t kind, const KilnUiItemView& view, const KilnUiBarValue& game, const ChainStamp& st) {
    KilnUiBarValue acc = game;
    if (kind >= kValueKinds || t_chain_depth > 0) return acc;
    const auto list = providers(kind);
    if (list->empty()) return acc;
    struct Depth {
        Depth() { ++t_chain_depth; }
        ~Depth() { --t_chain_depth; }
    } depth;
    for (const auto& sp : *list) {
        Provider& p = *sp;
        if (!provider_live(p)) continue;
        if (p.item != 0 ? view.item != p.item : (kind <= KUI_HELD && view.item == 0)) continue;
        CallState cs;
        cs.magic = kCallMagic;
        cs.provider = &p;
        cs.input_label = acc.label_is_override ? acc.label_id : 0;
        KilnUiProviderContext ctx{};
        ctx.size = sizeof ctx;
        ctx.kind = kind;
        ctx.session = st.session;
        ctx.frame = st.frame;
        ctx.client_step = st.step;
        ctx.owner = p.owner;
        ctx.text = &kTextOps;
        ctx.reserved = &cs;
        KilnUiItemView v = view;  // the provider's own copies: a write through a cast-away const reaches nothing
        KilnUiBarValue in = acc;
        KilnUiBarEdit e{};
        e.size = sizeof e;
        KilnResult rc = KILN_ERR_INTERNAL;
        bool called;
        {
            CallScope scope(&cs);
            called = env().call_value(reinterpret_cast<KilnUiValueFn>(p.fn), &ctx, &v, &in, &e, p.user, &rc);
        }
        if (!env().owner_alive(p.owner)) continue;  // retirement also discards its pending edit
        if (!called) {
            fault(p, "faulted");
            continue;
        }
        if (rc == KILN_ERR_INTERNAL) {
            fault(p, "returned an internal error");
            continue;
        }
        if (rc != KILN_OK) {
            refused(p, reason_of(e.reason));
            continue;
        }
        if (cs.latched) {
            refused(p, cs.latched);
            continue;
        }
        if (e.flags == 0) {
            succeeded(p);
            continue;
        }
        KilnUiBarValue next = acc;
        if (!apply_edit(kind, e, p.mod, &next)) {
            fault(p, "made an invalid edit");
            continue;
        }
        acc = next;
        succeeded(p);
    }
    return acc;
}

// ---- call scope, tables ----
CallScope::CallScope(CallState* cs) : prev(t_call) { t_call = cs; }
CallScope::~CallScope() { t_call = prev; }

const KilnUiTextOps* text_ops() { return &kTextOps; }
const KilnUiValuesApi* table() { return &kTable; }

void reset_for_tests() {  // host tests only: nothing dispatches while it runs
    delete g_reg;
    g_reg = new Registry;
    for (auto& c : g_count) c.store(0);
    for (auto& v : g_revision) v.store(0);
    g_reg_seen.store(0);
    {
        std::lock_guard lk(installs().mu);
        for (auto& s : installs().state) s = 0;
    }
    text::reset_for_tests();
    changes_reset_for_tests();
    slots_reset_for_tests();
    lines_reset_for_tests();
}

}  // namespace kiln::uivalues
