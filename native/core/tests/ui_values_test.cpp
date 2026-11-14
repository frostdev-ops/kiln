// SPDX-License-Identifier: MIT
// Host tests of kiln.ui.values (src/services/ui_values*.cpp, abi/services/kiln_ui_values.h): the registry (quotas,
// order, queued add/remove, retired owners, faults), the text store (budgets, rate, UTF-8, dedupe, lifetime) and the
// value chain; the detail and slot bars against fake descriptors; the player-UI update (HUD pairs, the held item, the
// pre-provider stage) against fake play data and a scripted update. The service's environment (owners, kiln.caps,
// kiln.client, kiln.hooks, game memory) is the fake below; "game memory" is this process's memory. Run by
// test-ui-values.sh (ASan/UBSan, host compiler, no Wine).
#include <cmath>
#include <cstdio>
#include <deque>
#include <cstring>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "../src/services/ui_values.cpp"
#include "../src/services/ui_values_text.cpp"
#include "../src/services/ui_values_bars.cpp"
#include "../src/services/ui_values_update.cpp"
#include "../src/services/ui_values_changes.cpp"
#include "../src/services/ui_values_slots.cpp"

using namespace kiln::uivalues;

static int g_fail = 0;
#define CHECK(cond, ...)                      \
    do {                                      \
        if (cond) {                           \
            std::printf("PASS " __VA_ARGS__); \
        } else {                              \
            std::printf("FAIL " __VA_ARGS__); \
            ++g_fail;                         \
        }                                     \
        std::printf("\n");                    \
    } while (0)

// ---- the fake environment ----
namespace fake {
std::map<KilnOwner, std::string> owners;
std::set<KilnOwner> alive;
uint64_t version = 1;
double clock_ms = 1000;
uint64_t ticks = 5000;
bool client_side = true;
std::set<std::string> rows;         // rows kiln.caps shows available
std::vector<std::string> rows_off;  // row_off reports
ClientNow client{};
bool have_client = true;
KilnUiScreen screen{};
bool have_screen = true;
std::map<std::string, uintptr_t> symbols;
std::map<std::string, int64_t> constants;
struct Raw {
    uintptr_t target;
    std::string label;
    void* fn;
    void** original;
    int32_t priority;
};
struct Mid {
    uintptr_t target;
    std::string label;
    KilnMidFn fn;
};
std::vector<Raw> raws;
std::vector<Mid> mids;
KilnResult hook_result = KILN_OK;
std::vector<std::string> logs;
std::set<uintptr_t> unreadable;  // addresses whose reads fail (a guarded read of unmapped memory)
bool frames_ok = true;           // kiln.ui hands out its UI tick
void (*frame_fn)(uint64_t, void*) = nullptr;
void* frame_user = nullptr;
size_t writes = 0;

KilnOwner owner(const std::string& name) {
    static KilnOwner next = 1;
    const KilnOwner o = next++;
    owners[o] = name;
    alive.insert(o);
    ++version;
    return o;
}
void revoke(KilnOwner o) {
    alive.erase(o);
    ++version;
}
size_t logged(const char* part) {
    size_t n = 0;
    for (const auto& l : logs) n += l.find(part) != std::string::npos;
    return n;
}

void no_original(void*) {}

void install() {
    Env& e = env();
    e.owner_name = [](KilnOwner o) { return alive.count(o) ? owners[o] : std::string("(revoked)"); };
    e.owner_alive = [](KilnOwner o) { return alive.count(o) != 0; };
    e.registry_version = [] { return version; };
    e.now_ms = [] { return clock_ms; };
    e.tick_ms = [] { return ticks; };
    e.client_side = [] { return client_side; };
    e.row_available = [](const char* n) { return rows.count(n) != 0; };
    e.row_off = [](const char* n, const char*) { rows_off.push_back(n); };
    e.client = [](ClientNow* out) {
        *out = client;
        return have_client;
    };
    e.screen = [](KilnUiScreen* out) {
        *out = screen;
        return have_screen;
    };
    e.read = [](void* dst, uintptr_t src, size_t n) {
        if (!src) return false;
        for (uintptr_t a : unreadable)
            if (a >= src && a < src + n) return false;
        std::memcpy(dst, reinterpret_cast<const void*>(src), n);
        return true;
    };
    e.write = [](uintptr_t dst, const void* src, size_t n) {
        if (!dst) return false;
        std::memcpy(reinterpret_cast<void*>(dst), src, n);
        ++writes;
        return true;
    };
    e.symbol = [](const char* n, uintptr_t* out) {
        const auto it = symbols.find(n);
        if (it == symbols.end()) return false;
        *out = it->second;
        return true;
    };
    e.constant = [](const char* n, int64_t* out) {
        const auto it = constants.find(n);
        if (it == constants.end()) return false;
        *out = it->second;
        return true;
    };
    e.hook_raw = [](uintptr_t t, const char* label, int32_t priority, void* fn, void** original) {
        if (hook_result == KILN_OK) {
            raws.push_back({t, label, fn, original, priority});
            *original = reinterpret_cast<void*>(&no_original);  // kiln.hooks sets the continuation before it links
        }
        return hook_result;
    };
    e.hook_mid = [](uintptr_t t, const char* label, KilnMidFn fn) {
        if (hook_result == KILN_OK) mids.push_back({t, label, fn});
        return hook_result;
    };
    // the host stand-in of the SEH guard: a provider "faults" by throwing
    e.call_value = [](KilnUiValueFn fn, const KilnUiProviderContext* c, const KilnUiItemView* v,
                      const KilnUiBarValue* in, KilnUiBarEdit* ed, void* user, KilnResult* rc) {
        try {
            *rc = fn(c, v, in, ed, user);
            return true;
        } catch (...) {
            return false;
        }
    };
    e.call_changed = [](KilnUiValueChangedFn fn, const KilnUiValueChange* c, void* user) {
        try {
            fn(c, user);
            return true;
        } catch (...) {
            return false;
        }
    };
    e.call_slots = [](KilnUiSlotsFn fn, const KilnUiSlotsFrame* f, const KilnUiVisibleSlot* sl, uint32_t n,
                      void* user) {
        try {
            fn(f, sl, n, user);
            return true;
        } catch (...) {
            return false;
        }
    };
    e.subscribe_frames = [](void (*fn)(uint64_t, void*), void* user) {
        if (!frames_ok) return false;
        frame_fn = fn;
        frame_user = user;
        return true;
    };
    e.log = [](int, const char* text) { logs.emplace_back(text); };
}
void end_frame(uint64_t frame) {
    if (frame_fn) frame_fn(frame, frame_user);
}

bool always_installed(uint32_t) { return true; }

// A clean service: no registrations, no text, every row available, every list "installed" by a no-op installer.
void reset() {
    kiln::uivalues::reset_for_tests();
    owners.clear();
    alive.clear();
    version = 1;
    clock_ms = 1000;
    client_side = true;
    rows = {"ui.values.detail", "ui.values.slot",          "ui.values.held",
            "ui.values.hud",    "ui.values.tooltip_lines", "ui.slots.inspect"};
    rows_off.clear();
    client = ClientNow{};
    have_client = true;
    screen = KilnUiScreen{};
    screen.size = sizeof screen;
    have_screen = true;
    symbols.clear();
    constants.clear();
    raws.clear();
    mids.clear();
    hook_result = KILN_OK;
    logs.clear();
    unreadable.clear();
    writes = 0;
    frames_ok = true;
    frame_fn = nullptr;
    frame_user = nullptr;
    for (uint32_t l = 0; l < kLists; ++l) set_installer(l, &always_installed);
}
}  // namespace fake

namespace {

const KilnUiValuesApi* T() { return table(); }

KilnUiRegistration reg_record() {
    KilnUiRegistration r{};
    r.size = sizeof r;
    return r;
}

// ---- providers used by the tests ----
struct Calls {
    std::vector<std::string> order;
};
Calls g_calls;

KilnResult KILN_CALL pass(const KilnUiProviderContext*, const KilnUiItemView*, const KilnUiBarValue*, KilnUiBarEdit*,
                          void* user) {
    g_calls.order.push_back(user ? static_cast<const char*>(user) : "pass");
    return KILN_OK;
}

struct Script {  // what a scripted provider does on each call
    const char* name = "";
    int throws = 0;  // the next n calls throw
    KilnResult result = KILN_OK;
    uint32_t reason = 0;
    uint32_t flags = 0;
    float fraction = 0;
    uint32_t current = 0, maximum = 0;
    KilnUiTextId label = 0;
    const char* intern = nullptr;  // interned before returning; its id becomes the label when label_from_intern
    bool label_from_intern = false;
    KilnUiTextId interned = 0;
    KilnResult intern_rc = KILN_OK;
    KilnUiBarValue seen{};  // the input of the last call
    KilnUiItemView view{};
    KilnHandle unsubscribe = 0;   // removed during the call
    bool subscribe_more = false;  // registers another provider during the call
    int calls = 0;
};

KilnResult KILN_CALL scripted(const KilnUiProviderContext* c, const KilnUiItemView* v, const KilnUiBarValue* in,
                              KilnUiBarEdit* e, void* user) {
    auto* s = static_cast<Script*>(user);
    ++s->calls;
    g_calls.order.push_back(s->name);
    s->seen = *in;
    s->view = *v;
    if (s->unsubscribe) T()->unsubscribe(s->unsubscribe);
    if (s->subscribe_more) {
        auto r = reg_record();
        T()->subscribe(c->owner, c->kind, 0, &pass, const_cast<char*>("added-in-callback"), &r);
    }
    if (s->throws > 0) {
        --s->throws;
        throw std::runtime_error("provider bug");
    }
    if (s->intern) {
        s->intern_rc = c->text->intern(c, KilnStr{s->intern, std::strlen(s->intern)}, &s->interned);
        if (s->label_from_intern) e->label_id = s->interned;
    }
    if (!s->label_from_intern) e->label_id = s->label;
    e->flags = s->flags;
    e->fraction = s->fraction;
    e->current = s->current;
    e->maximum = s->maximum;
    e->reason = s->reason;
    return s->result;
}

KilnUiBarValue game_value(uint32_t kind, float fraction, uint32_t current, uint32_t maximum) {
    KilnUiBarValue v{};
    v.size = sizeof v;
    v.kind = kind;
    v.fraction = fraction;
    v.current = current;
    v.maximum = maximum;
    return v;
}

KilnUiItemView item_view(uint32_t item) {
    KilnUiItemView v{};
    v.size = sizeof v;
    v.item = item;
    return v;
}

KilnHandle add_value(KilnOwner o, uint32_t kind, uint32_t item, KilnUiValueFn fn, void* user) {
    auto r = reg_record();
    const KilnResult rc = T()->subscribe(o, kind, item, fn, user, &r);
    return rc == KILN_OK && r.ok ? r.handle : 0;
}

// ======================================================================================================== S1: registry
void table_and_arguments() {
    fake::reset();
    CHECK(T()->header.size == sizeof(KilnUiValuesApi) && T()->header.version == KILN_UI_VALUES_VERSION,
          "table: header {%u, %u}", T()->header.size, T()->header.version);
    const KilnOwner o = fake::owner("mod:args");
    auto r = reg_record();
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, &pass, nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "arguments: no result record -> INVALID_ARGUMENT");
    KilnUiRegistration small{};
    small.size = sizeof small - 1;
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, &pass, nullptr, &small) == KILN_ERR_INVALID_ARGUMENT,
          "arguments: a result record smaller than v1 -> INVALID_ARGUMENT");
    CHECK(T()->subscribe(0, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_ERR_INVALID_ARGUMENT,
          "arguments: owner 0 -> INVALID_ARGUMENT");
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, nullptr, nullptr, &r) == KILN_ERR_INVALID_ARGUMENT,
          "arguments: no callback -> INVALID_ARGUMENT");
    CHECK(T()->subscribe(o, 6, 0, &pass, nullptr, &r) == KILN_ERR_INVALID_ARGUMENT,
          "arguments: kind 6 -> INVALID_ARGUMENT");
    CHECK(T()->subscribe(o, KUI_HEALTH, 7, &pass, nullptr, &r) == KILN_ERR_INVALID_ARGUMENT &&
              T()->subscribe(o, KUI_MANA, 7, &pass, nullptr, &r) == KILN_ERR_INVALID_ARGUMENT,
          "arguments: a HUD kind with an item filter -> INVALID_ARGUMENT");
    CHECK(r.ok == 0 && r.handle == 0, "arguments: a refused call leaves ok 0 and handle 0");
    fake::client_side = false;
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE,
          "side: a dedicated server -> UNAVAILABLE");
    fake::client_side = true;
    fake::rows.erase("ui.values.held");
    CHECK(T()->subscribe(o, KUI_HELD, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE,
          "capability: ui.values.held not available (the client gate holds it) -> UNAVAILABLE");
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1 && r.handle != 0,
          "capability: ui.values.detail available -> registered (each kind alone)");
    // a kind whose hooks cannot be installed stays off and says so in kiln.caps
    set_installer(KUI_SLOT, [](uint32_t) { return false; });
    CHECK(T()->subscribe(o, KUI_SLOT, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE && fake::rows_off.size() == 1 &&
              fake::rows_off[0] == "ui.values.slot",
          "install: a failed install -> UNAVAILABLE and the row reported off (%zu reports)", fake::rows_off.size());
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1,
          "install: the other kinds still register");
    const KilnOwner gone = fake::owner("mod:gone");
    fake::revoke(gone);
    CHECK(T()->subscribe(gone, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_ERR_REVOKED, "owner: retired -> REVOKED");
    CHECK(T()->unsubscribe(12345) == KILN_ERR_NOT_FOUND && T()->unsubscribe(0) == KILN_ERR_NOT_FOUND,
          "unsubscribe: a handle that is not this service's -> NOT_FOUND");
    KilnUiSlotsFrame fr{};
    fr.size = sizeof fr;
    uint32_t req = 0;
    CHECK(T()->copy_slots(o, 0, 0, &fr, nullptr, 0, &req) == KILN_OK && fr.ok == 0 && fr.reason == KUI_NO_FRAME &&
              T()->copy_slots(o, 0, 0, nullptr, nullptr, 0, &req) == KILN_ERR_INVALID_ARGUMENT &&
              T()->copy_slots(0, 0, 0, &fr, nullptr, 0, &req) == KILN_ERR_INVALID_ARGUMENT,
          "copy_slots: arguments checked; no frame yet -> KUI_NO_FRAME");
}

void quota() {
    fake::reset();
    const KilnOwner a = fake::owner("mod:greedy");
    std::vector<KilnHandle> hs;
    for (int i = 0; i < 128; ++i) {
        auto r = reg_record();
        const KilnResult rc = (i % 2) ? T()->subscribe(a, KUI_SLOT, 0, &pass, nullptr, &r)
                                      : T()->tooltip_lines(
                                            a, 0,
                                            [](const KilnUiProviderContext*, const KilnUiItemView*, KilnUiLineWriter*,
                                               void*) -> KilnResult { return KILN_OK; },
                                            nullptr, &r);
        if (rc == KILN_OK && r.ok) hs.push_back(r.handle);
    }
    CHECK(hs.size() == 128 && active("mod:greedy") == 128, "quota: 128 registrations (value and line lists together)");
    auto r = reg_record();
    r.ok = 7;
    r.handle = 99;
    CHECK(T()->subscribe(a, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 0 &&
              r.reason == KUI_REGISTRATION_BUDGET && r.handle == 0,
          "quota: the 129th -> KILN_OK, ok 0, KUI_REGISTRATION_BUDGET, handle 0 (ok %u reason %u)", r.ok, r.reason);
    const KilnOwner reload = fake::owner("mod:greedy");  // the same mod loaded again: the same stable id
    r = reg_record();
    CHECK(T()->subscribe(reload, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 0 &&
              r.reason == KUI_REGISTRATION_BUDGET,
          "quota: counted per stable mod id, not per owner");
    const KilnOwner b = fake::owner("mod:other");
    r = reg_record();
    CHECK(T()->subscribe(b, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1,
          "quota: another mod is not affected");
    CHECK(T()->unsubscribe(hs[5]) == KILN_OK && T()->unsubscribe(hs[5]) == KILN_ERR_NOT_FOUND,
          "unsubscribe: once OK, then NOT_FOUND");
    r = reg_record();
    CHECK(T()->subscribe(a, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1 && active("mod:greedy") == 128,
          "quota: a removal frees one place");
    fake::revoke(a);
    r = reg_record();
    CHECK(
        T()->subscribe(reload, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1 && active("mod:greedy") == 1,
        "quota: the retired owner's registrations are dropped and free the mod's places (%zu held)",
        active("mod:greedy"));
}

void order_and_queued_changes() {
    fake::reset();
    const KilnOwner o = fake::owner("mod:order");
    Script s1, s2, s3;
    s1.name = "first";
    s2.name = "second";
    s3.name = "third";
    const KilnHandle h1 = add_value(o, KUI_DETAIL, 0, &scripted, &s1);
    const KilnHandle h2 = add_value(o, KUI_DETAIL, 0, &scripted, &s2);
    add_value(o, KUI_DETAIL, 0, &scripted, &s3);
    g_calls.order.clear();
    run_chain(KUI_DETAIL, item_view(42), game_value(KUI_DETAIL, 0.5f, 5, 10), {});
    CHECK(g_calls.order == (std::vector<std::string>{"first", "second", "third"}),
          "order: providers run in registration order");
    // During a dispatch: the first removes the second and registers a new one.
    s1.unsubscribe = h2;
    s1.subscribe_more = true;
    g_calls.order.clear();
    run_chain(KUI_DETAIL, item_view(42), game_value(KUI_DETAIL, 0.5f, 5, 10), {});
    CHECK(g_calls.order == (std::vector<std::string>{"first", "third"}),
          "queued: a provider removed during a dispatch is not called again; one added runs from the next dispatch "
          "(%zu calls)",
          g_calls.order.size());
    s1.unsubscribe = 0;
    s1.subscribe_more = false;
    g_calls.order.clear();
    run_chain(KUI_DETAIL, item_view(42), game_value(KUI_DETAIL, 0.5f, 5, 10), {});
    CHECK(g_calls.order == (std::vector<std::string>{"first", "third", "added-in-callback"}),
          "queued: the next dispatch sees the change, in registration order");
    // Item filter: 0 = any nonempty item.
    T()->unsubscribe(h1);
    Script only;
    only.name = "item-7";
    add_value(o, KUI_SLOT, 7, &scripted, &only);
    Script anyitem;
    anyitem.name = "any";
    add_value(o, KUI_SLOT, 0, &scripted, &anyitem);
    run_chain(KUI_SLOT, item_view(8), game_value(KUI_SLOT, 1, 0, 0), {});
    run_chain(KUI_SLOT, item_view(0), game_value(KUI_SLOT, 1, 0, 0), {});
    run_chain(KUI_SLOT, item_view(7), game_value(KUI_SLOT, 1, 0, 0), {});
    CHECK(only.calls == 1 && anyitem.calls == 2,
          "filter: an item filter matches its item only; 0 matches every nonempty item, never item 0 (%d, %d)",
          only.calls, anyitem.calls);
    // A retired owner's provider is not called once the registry moved.
    fake::revoke(o);
    g_calls.order.clear();
    run_chain(KUI_DETAIL, item_view(42), game_value(KUI_DETAIL, 0.5f, 5, 10), {});
    CHECK(g_calls.order.empty() && !any(KUI_DETAIL),
          "revoke: a retired owner's providers are gone at the next dispatch");
}

uint32_t faults_of(uint32_t list) {
    const auto l = providers(list);
    return l->empty() ? 0 : l->front()->faults.load();
}

void faults() {
    fake::reset();
    const KilnOwner o = fake::owner("mod:faulty");
    Script s;
    s.name = "faulty";
    s.throws = 4;
    s.flags = KUI_FRACTION;
    s.fraction = 0.25f;
    add_value(o, KUI_SLOT, 0, &scripted, &s);
    KilnUiBarValue v{};
    for (int i = 0; i < 4; ++i) v = run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    CHECK(any(KUI_SLOT) && faults_of(KUI_SLOT) == 4 && v.fraction == 1.0f,
          "faults: four throws in a row: still registered, a faulting provider's edit is dropped (%u faults)",
          faults_of(KUI_SLOT));
    v = run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    CHECK(any(KUI_SLOT) && faults_of(KUI_SLOT) == 0 && v.fraction == 0.25f,
          "faults: an accepted edit resets the count");
    CHECK(fake::logged("fault 1 of 5") == 1, "faults: the first fault of a run is logged once");
    s.result = KILN_ERR_UNAVAILABLE;  // refusals: no fault, no reset
    s.reason = 77;
    s.throws = 1;
    run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    for (int i = 0; i < 6; ++i) v = run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    CHECK(any(KUI_SLOT) && faults_of(KUI_SLOT) == 1 && v.fraction == 1.0f,
          "faults: six refusals neither count nor reset (%u), the refused edit is not applied", faults_of(KUI_SLOT));
    CHECK(fake::logged("refused its edit (reason 1)") == 1,
          "faults: an unknown refusal reason becomes KUI_REJECTED, logged once per reason");
    s.result = KILN_ERR_INTERNAL;  // what a managed bridge returns for an exception it caught
    run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    s.result = KILN_OK;
    s.flags = 0x80;  // an unknown flag: an invalid edit
    run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    s.flags = KUI_LABEL;  // a label on a slot
    run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    CHECK(any(KUI_SLOT) && faults_of(KUI_SLOT) == 4, "faults: KILN_ERR_INTERNAL and invalid edits count (%u)",
          faults_of(KUI_SLOT));
    s.throws = 1;
    run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    const int calls = s.calls;
    run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1.0f, 3, 4), {});
    CHECK(!any(KUI_SLOT) && s.calls == calls && fake::logged("removed after 5 faults") == 1,
          "faults: the fifth in a row removes it (not called again), logged once");
    CHECK(active("mod:faulty") == 0, "faults: the removal frees its quota place");
}

// ===================================================================================================== S1: text store
KilnResult intern_as(const char* mod, std::string_view s, KilnUiTextId* id) { return text::intern(mod, s, id); }

void text_store() {
    fake::reset();
    KilnUiTextId a = 0, b = 0, c = 0;
    CHECK(intern_as("mod:t", "", &a) == KILN_OK && a == 0 && text::usage("mod:t").charged == 0,
          "text: \"\" is id 0, free");
    CHECK(intern_as("mod:t", "Fuel", &a) == KILN_OK && a != 0 && intern_as("mod:t", "Fuel", &b) == KILN_OK && a == b,
          "text: the same bytes again -> the same id");
    CHECK(text::usage("mod:t").charged == 4 + 1 + 64 + 16 && text::usage("mod:t").strings == 1,
          "text: charged once: bytes + 1 + 64 + 16 (%llu)", (unsigned long long)text::usage("mod:t").charged);
    CHECK(intern_as("mod:u", "Fuel", &c) == KILN_OK && c == a && text::usage("mod:u").charged == 85 &&
              text::usage_all().strings == 1 && text::usage_all().charged == 4 + 65 + 16 + 16,
          "text: another mod's identical bytes share the string; that mod pays its full charge, the store one entry "
          "+ two memberships (%llu)",
          (unsigned long long)text::usage_all().charged);
    CHECK(intern_as("mod:t", "fuel", &b) == KILN_OK && b != a, "text: exact bytes, no case folding");
    std::string_view v;
    CHECK(text::bytes(a, &v) && v == "Fuel" && text::held_by(a, "mod:t") && text::held_by(a, "mod:u") &&
              !text::held_by(a, "mod:x"),
          "text: resolve and membership");
    CHECK(!text::bytes(12345, &v) && !text::bytes(a + 1000, &v), "text: an id the store did not hand out is unknown");
    // UTF-8
    const char* bad[] = {"\xff", "\xc0\x80", "\xe0\x80\xaf", "\xed\xa0\x80", "\xf4\x90\x80\x80", "abc\xe2\x82", "\x80"};
    int refused = 0;
    for (const char* s : bad) refused += intern_as("mod:t", s, &b) == KILN_ERR_INVALID_ARGUMENT && b == 0;
    CHECK(refused == 7,
          "text: invalid UTF-8 (bad byte, overlong, surrogate, above U+10FFFF, cut, lone continuation) "
          "-> INVALID_ARGUMENT (%d of 7)",
          refused);
    CHECK(intern_as("mod:t", std::string_view("a\0b", 3), &b) == KILN_ERR_INVALID_ARGUMENT,
          "text: an embedded NUL -> INVALID_ARGUMENT");
    CHECK(intern_as("mod:t", "Wasserkanne \xc3\x9c\xc3\xb6\xc3\x9f \xe2\x82\xac \xf0\x9f\x94\xa5", &b) == KILN_OK,
          "text: valid multi-byte UTF-8 is taken");
    CHECK(intern_as("mod:t", std::string(4096, 'x'), &b) == KILN_OK &&
              intern_as("mod:t", std::string(4097, 'x'), &c) == KILN_ERR_BUFFER_TOO_SMALL && c == 0,
          "text: 4096 bytes taken, 4097 refused (TEXT_BUDGET)");
    CHECK(fake::logged("text from mod:t refused (a string over 4096 bytes)") == 1, "text: the refusal is logged");
    intern_as("mod:t", std::string(4097, 'y'), &c);
    CHECK(fake::logged("text from mod:t refused") == 1, "text: the same refusal again is not logged again");
    // the canonical empty text and game tuples
    text::GameText gt{};
    CHECK(text::game_text(0, &gt) && gt.n == 0 && gt.p && gt.fnv == 0xcbf29ce484222325ull && text::game_text(a, &gt) &&
              gt.n == 4 && std::memcmp(gt.p, "Fuel", 4) == 0 && gt.p[4] == 0 && gt.fnv == text::fnv64("Fuel", 4),
          "text: GameText tuples point at permanent NUL-terminated bytes with the signed-byte FNV");
    CHECK(text::fnv64("\xc3\x9c", 2) != 0xcbf29ce484222325ull &&
              text::fnv64("\xc3\x9c", 2) ==
                  ((((0xcbf29ce484222325ull ^ 0xffffffffffffffc3ull) * 0x100000001b3ull) ^ 0xffffffffffffff9cull) *
                   0x100000001b3ull),
          "text: non-ASCII bytes hash sign-extended (kiln.ui's convention)");
}

void text_rate_and_budgets() {
    fake::reset();
    KilnUiTextId id = 0;
    int ok = 0;
    for (int i = 0; i < 200; ++i) ok += intern_as("mod:fast", "s" + std::to_string(i), &id) == KILN_OK;
    CHECK(ok == 128, "rate: a burst of 128 new strings, then refused (%d)", ok);
    CHECK(intern_as("mod:fast", "s5", &id) == KILN_OK, "rate: a string the mod already holds is not a new one");
    CHECK(intern_as("mod:slow", "s500", &id) == KILN_OK, "rate: per mod");
    fake::clock_ms += 1000;
    ok = 0;
    for (int i = 200; i < 400; ++i) ok += intern_as("mod:fast", "s" + std::to_string(i), &id) == KILN_OK;
    CHECK(ok == 64, "rate: 64 more after one second (%d)", ok);
    CHECK(fake::logged("more than 64 new strings a second") == 2,
          "rate: logged once per refusal run (twice for two runs)");

    // 8 MiB per mod: 4096-byte strings cost 4177; 2008 fit (8,387,416), the 2009th does not
    fake::reset();
    int n = 0;
    KilnResult rc = KILN_OK;
    for (int i = 0; i < 2100 && rc == KILN_OK; ++i) {
        fake::clock_ms += 1000;
        std::string s(4096, 'a');
        std::snprintf(s.data(), 16, "%08d", i);
        s[8] = 'a';
        rc = intern_as("mod:big", s, &id);
        n += rc == KILN_OK;
    }
    CHECK(n == 2008 && rc == KILN_ERR_BUFFER_TOO_SMALL && text::usage("mod:big").charged <= kModTextBudget,
          "budget: 8 MiB per mod: %d strings of 4096 bytes, then TEXT_BUDGET (%llu charged)", n,
          (unsigned long long)text::usage("mod:big").charged);
    CHECK(intern_as("mod:small", "still fine", &id) == KILN_OK, "budget: other mods still intern");
    CHECK(fake::logged("8 MiB text budget") == 1, "budget: logged");

    // 64 MiB in all: eight full mods (16,064 strings) fit; two more strings of a ninth do, the third does not
    fake::reset();
    rc = KILN_OK;
    int total = 0;
    for (int m = 0; m < 9 && rc == KILN_OK; ++m) {
        const std::string mod = "mod:m" + std::to_string(m);
        for (int i = 0; i < 2008; ++i) {
            fake::clock_ms += 1000;
            std::string s(4096, 'b');
            std::snprintf(s.data(), 24, "%02d-%08d", m, i);
            s[11] = 'b';
            rc = intern_as(mod.c_str(), s, &id);
            if (rc != KILN_OK) break;
            ++total;
        }
    }
    CHECK(total == 16066 && rc == KILN_ERR_BUFFER_TOO_SMALL && text::usage_all().charged <= kGlobalTextBudget &&
              fake::logged("64 MiB text store is full") == 1,
          "budget: the 64 MiB store refuses at %d strings (%llu charged)", total,
          (unsigned long long)text::usage_all().charged);

    // 65,536 strings in all
    fake::reset();
    rc = KILN_OK;
    total = 0;
    for (int m = 0; m < 600 && rc == KILN_OK; ++m) {
        const std::string mod = "mod:n" + std::to_string(m);
        for (int i = 0; i < 120; ++i) {
            rc = intern_as(mod.c_str(), std::to_string(m * 1000 + i), &id);
            if (rc != KILN_OK) break;
            ++total;
        }
    }
    CHECK(total == 65536 && rc == KILN_ERR_BUFFER_TOO_SMALL && fake::logged("65,536 strings") == 1,
          "budget: 65,536 strings in all, then TEXT_BUDGET (%d)", total);
    CHECK(intern_as("mod:n0", "0", &id) == KILN_OK, "budget: a held string is still found when the store is full");
}

// Text through a provider's context: only during its callback, charged to its mod, kept after the owner is gone.
struct TextScript {
    std::string want;
    KilnUiTextId id = 0;
    KilnResult rc = KILN_ERR_INTERNAL;
    KilnResult resolve_rc = KILN_ERR_INTERNAL;
    KilnStr resolved{};
    KilnUiTextId foreign = 0;
    KilnResult foreign_rc = KILN_OK;
    KilnResult input_rc = KILN_ERR_INTERNAL;
    KilnUiProviderContext ctx{};  // a copy: the service's context is gone when the callback returned
    bool set_label = true;
};
KilnResult KILN_CALL text_provider(const KilnUiProviderContext* c, const KilnUiItemView*, const KilnUiBarValue* in,
                                   KilnUiBarEdit* e, void* user) {
    auto* s = static_cast<TextScript*>(user);
    s->ctx = *c;
    s->rc = c->text->intern(c, KilnStr{s->want.data(), s->want.size()}, &s->id);
    s->resolve_rc = c->text->resolve(c, s->id, &s->resolved);
    if (s->foreign) s->foreign_rc = c->text->resolve(c, s->foreign, &s->resolved);
    if (in->label_is_override && in->label_id) {
        KilnStr x{};
        s->input_rc = c->text->resolve(c, in->label_id, &x);
    }
    if (s->set_label) {
        e->flags = KUI_LABEL;
        e->label_id = s->id;
    }
    return KILN_OK;
}

void text_through_providers() {
    fake::reset();
    const KilnOwner o = fake::owner("mod:labels");
    TextScript s;
    s.want = "Charges";
    add_value(o, KUI_DETAIL, 0, &text_provider, &s);
    const KilnUiBarValue v = run_chain(KUI_DETAIL, item_view(3), game_value(KUI_DETAIL, 1, 1, 1), {});
    CHECK(s.rc == KILN_OK && s.id != 0 && v.label_is_override == 1 && v.label_id == s.id,
          "context: intern in the callback, LABEL with the id replaces the label");
    CHECK(s.resolve_rc == KILN_OK && std::string_view(s.resolved.ptr, s.resolved.len) == "Charges",
          "context: resolve of its own id");
    KilnUiTextId late = 0;
    KilnStr x{};
    CHECK(s.ctx.text->intern(&s.ctx, KilnStr{"late", 4}, &late) == KILN_ERR_INVALID_ARGUMENT &&
              s.ctx.text->resolve(&s.ctx, s.id, &x) == KILN_ERR_INVALID_ARGUMENT,
          "context: intern and resolve after the callback returned -> INVALID_ARGUMENT");
    CHECK(text::usage("mod:labels").strings == 1, "context: charged to the provider's stable mod id");

    // a second mod: the first one's id is foreign to it, except as its input label
    const KilnOwner o2 = fake::owner("mod:second");
    TextScript s2;
    s2.want = "Other";
    s2.foreign = s.id;
    s2.set_label = false;
    add_value(o2, KUI_DETAIL, 0, &text_provider, &s2);
    run_chain(KUI_DETAIL, item_view(3), game_value(KUI_DETAIL, 1, 1, 1), {});
    CHECK(s2.input_rc == KILN_OK, "context: the input's label (an earlier provider's) resolves");
    s2.foreign = 0;
    // a foreign id that is not the input label: reach it by giving it as the first provider's own later
    TextScript s3;
    s3.want = "Third";
    s3.set_label = false;
    const KilnOwner o3 = fake::owner("mod:third");
    KilnUiTextId third_id = 0;
    text::intern("mod:fourth", "not yours", &third_id);
    s3.foreign = third_id;
    add_value(o3, KUI_SLOT, 0, &text_provider, &s3);
    run_chain(KUI_SLOT, item_view(3), game_value(KUI_SLOT, 1, 1, 1), {});
    CHECK(s3.foreign_rc == KILN_ERR_NOT_FOUND, "context: another mod's id -> NOT_FOUND");

    // owner revoke keeps the bytes (and the id, and the charge)
    text::GameText before{};
    text::game_text(s.id, &before);
    fake::revoke(o);
    run_chain(KUI_DETAIL, item_view(3), game_value(KUI_DETAIL, 1, 1, 1), {});
    text::GameText after{};
    CHECK(!any(KUI_DETAIL) || active("mod:labels") == 0, "revoke: the owner's registrations are gone");
    CHECK(text::game_text(s.id, &after) && after.p == before.p && std::string_view(after.p, after.n) == "Charges" &&
              text::usage("mod:labels").strings == 1,
          "revoke: the bytes stay at the same address with their id and charge");
    const KilnOwner again = fake::owner("mod:labels");  // the mod reloads
    TextScript s4;
    s4.want = "Charges";
    add_value(again, KUI_DETAIL, 0, &text_provider, &s4);
    run_chain(KUI_DETAIL, item_view(3), game_value(KUI_DETAIL, 1, 1, 1), {});
    CHECK(s4.id == s.id && text::usage("mod:labels").strings == 1 && text::usage("mod:labels").charged == 7 + 65 + 16,
          "reload: the same mod id gets the same id back with no new charge");
}

// A refused intern (budget) refuses the callback's edit whatever it returns; a refusal is not a fault.
void text_latch() {
    fake::reset();
    const KilnOwner o = fake::owner("mod:latch");
    Script s;
    s.name = "latch";
    s.intern = nullptr;
    s.flags = KUI_FRACTION;
    s.fraction = 0.5f;
    std::string big(4097, 'z');
    s.intern = big.c_str();
    add_value(o, KUI_SLOT, 0, &scripted, &s);
    KilnUiBarValue v{};
    for (int i = 0; i < 6; ++i) v = run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1, 0, 0), {});
    CHECK(s.intern_rc == KILN_ERR_BUFFER_TOO_SMALL && v.fraction == 1.0f && any(KUI_SLOT),
          "latch: an intern over a limit refuses the edit (fraction stays %.2f) and is not a fault", v.fraction);
    // 64 KiB per callback: sixteen strings of 4096 bytes fit, the seventeenth does not
    struct Many {
        int ok = 0;
        KilnResult last = KILN_OK;
    } many;
    fake::reset();
    const KilnOwner o2 = fake::owner("mod:many");
    auto fn = [](const KilnUiProviderContext* c, const KilnUiItemView*, const KilnUiBarValue*, KilnUiBarEdit* e,
                 void* user) -> KilnResult {
        auto* m = static_cast<Many*>(user);
        for (int i = 0; i < 17; ++i) {
            std::string s(4096, 'q');
            s[0] = static_cast<char>('a' + i);
            KilnUiTextId id = 0;
            m->last = c->text->intern(c, KilnStr{s.data(), s.size()}, &id);
            m->ok += m->last == KILN_OK;
        }
        e->flags = KUI_FRACTION;
        e->fraction = 0.25f;
        return KILN_OK;
    };
    add_value(o2, KUI_SLOT, 0, fn, &many);
    v = run_chain(KUI_SLOT, item_view(1), game_value(KUI_SLOT, 1, 0, 0), {});
    CHECK(many.ok == 16 && many.last == KILN_ERR_BUFFER_TOO_SMALL && v.fraction == 1.0f,
          "latch: 64 KiB per callback (%d strings of 4096 taken), and the callback is refused", many.ok);
}

// ========================================================================================= S2: detail and slot bars
// Fake game memory: a temporary Bar descriptor, a thumbnail descriptor whose +0x18 points at ItemDetail's record, a
// slot's ItemView and the UI frame's chain to the water charge amount. Offsets are the client's (sigdb).
struct alignas(16) Mem {
    uint8_t bar[0x50];
    uint8_t thumb[0x40];
    uint8_t record[0x180];
    uint8_t slot_view[0x48];
    uint8_t app[0x100], data[0x10], state[0x40], play[0x80], registry[0x40], resource[0x100];
    uintptr_t ui_app;
};
Mem g_mem;

template <class T>
void put(uint8_t* base, size_t at, T v) {
    std::memcpy(base + at, &v, sizeof v);
}
template <class T>
T get(const uint8_t* base, size_t at) {
    T v;
    std::memcpy(&v, base + at, sizeof v);
    return v;
}
uintptr_t addr(const void* p) { return reinterpret_cast<uintptr_t>(p); }

const text::GameText kGameLabel{"Water", 5, 0x1234};

void fill_bar(float fraction, uint32_t current, uint32_t maximum) {
    std::memset(g_mem.bar, 0xCD, sizeof g_mem.bar);
    put(g_mem.bar, 0, fraction);
    put(g_mem.bar, 4, current);
    put(g_mem.bar, 8, maximum);
    put(g_mem.bar, 0x38, kGameLabel);
}
// An ItemView prefix: page, index, item, count, pide, fuel / max (u16).
void fill_view_bytes(uint8_t* v, uint32_t page, uint8_t index, uint32_t item, uint32_t count, uint32_t pide,
                     uint16_t fuel, uint16_t fuel_max) {
    std::memset(v, 0, 0x48);
    put(v, 0, page);
    put(v, 4, index);
    put(v, 8, item);
    put(v, 0x10, count);
    put(v, 0x14, pide);
    put(v, 0x18, uint32_t{70});
    put(v, 0x1c, uint32_t{100});
    put(v, 0x20, fuel);
    put(v, 0x22, fuel_max);
    put(v, 0x24, uint32_t{3});
    put(v, 0x2c, uint8_t{2});
}

void prime_bars() {
    fake::reset();
    kiln::uivalues::g_bars = {};
    kiln::uivalues::g_charges_tried = false;
    set_installer(KUI_DETAIL, &kiln::uivalues::install_bars);
    set_installer(KUI_SLOT, &kiln::uivalues::install_bars);
    fake::symbols = {{"ui/DetailBarDraw", 0x140c1401aull}, {"ui/SlotBarDraw", 0x140c05895ull}};
    fake::constants = {{"ui/bar.fraction", 0},
                       {"ui/bar.current", 4},
                       {"ui/bar.max", 8},
                       {"ui/bar.label", 0x38},
                       {"ui/detail.view", 0x18},
                       {"ui/view.stride", 0x48},
                       {"ui/view.inventoryPage", 0},
                       {"ui/view.slotIndex", 4},
                       {"ui/view.item", 8},
                       {"ui/view.count", 0x10},
                       {"ui/view.pide", 0x14},
                       {"ui/view.durability", 0x18},
                       {"ui/view.durabilityMax", 0x1c},
                       {"ui/view.fuel", 0x20},
                       {"ui/view.fuelMax", 0x22},
                       {"ui/view.level", 0x24},
                       {"ui/view.rarity", 0x2c},
                       {"ui/ui.data", 0xd0},
                       {"ui/state.play", 0x38},
                       {"ui/play.balancing", 0x40},
                       {"ui/balancing.rootData", 0x20},
                       {"ui/balancing.waterChargeAmount", 0xfc}};
    // the UI frame's chain: UiApp -> app; app+0xd0 -> data; *data = state; state+0x38 -> play; play+0x40 -> registry;
    // registry+0x20 -> resource; resource+0xfc = 64 raw fuel units a charge
    std::memset(&g_mem, 0, sizeof g_mem);
    g_mem.ui_app = addr(g_mem.app);
    fake::symbols["ui/UiApp"] = addr(&g_mem.ui_app);
    put(g_mem.app, 0xd0, addr(g_mem.data));
    put(g_mem.data, 0, addr(g_mem.state));
    put(g_mem.state, 0x38, addr(g_mem.play));
    put(g_mem.play, 0x40, addr(g_mem.registry));
    put(g_mem.registry, 0x20, addr(g_mem.resource));
    put(g_mem.resource, 0xfc, uint32_t{64});
    put(g_mem.thumb, 0x18, addr(g_mem.record));
    fake::client = ClientNow{77, 3, 9000};
    fake::screen.frame = 500;
    fake::screen.ui_state = KILN_UI_STATE_PLAY;
}

KilnMidFn mid_at(const char* label) {
    for (const auto& m : fake::mids)
        if (m.label == label) return m.fn;
    return nullptr;
}
void draw_detail() {
    KilnRegisters r{};
    r.rcx = addr(g_mem.bar);
    r.rdi = addr(g_mem.thumb);
    if (KilnMidFn fn = mid_at("ui.values/DetailBarDraw")) fn(&r, nullptr);
}
void draw_slot() {
    KilnRegisters r{};
    r.rcx = addr(g_mem.bar);
    r.rbx = addr(g_mem.slot_view);
    if (KilnMidFn fn = mid_at("ui.values/SlotBarDraw")) fn(&r, nullptr);
}

struct Seen {  // what a provider was handed, and where
    KilnUiItemView view{};
    KilnUiBarValue in{};
    KilnUiProviderContext ctx{};
    uintptr_t view_at = 0, in_at = 0;
    int calls = 0;
};
KilnResult KILN_CALL observe(const KilnUiProviderContext* c, const KilnUiItemView* v, const KilnUiBarValue* in,
                             KilnUiBarEdit*, void* user) {
    auto* s = static_cast<Seen*>(user);
    s->view = *v;
    s->in = *in;
    s->ctx = *c;
    s->view_at = addr(v);
    s->in_at = addr(in);
    ++s->calls;
    return KILN_OK;
}
bool inside(uintptr_t a, const void* p, size_t n) { return a >= addr(p) && a < addr(p) + n; }

void bars_install_and_view() {
    prime_bars();
    const KilnOwner o = fake::owner("mod:bars");
    Seen seen;
    add_value(o, KUI_DETAIL, 0, &observe, &seen);
    add_value(o, KUI_DETAIL, 0xAA, &observe, &seen);
    CHECK(fake::mids.size() == 1 && fake::mids[0].target == 0x140c1401aull && fake::raws.empty(),
          "install: the first detail provider places one mid at DetailBarDraw, the second none (%zu)",
          fake::mids.size());
    fill_bar(0.0f, 0, 100);  // detail water: charges
    fill_view_bytes(g_mem.record, 0x5000, 3, 0xAA, 1, 17500, 0, 6400);
    std::memset(g_mem.record + 0x48, 0xEE, 0x180 - 0x48);  // the rest of ItemDetail's record is not an ItemView
    draw_detail();
    const KilnUiItemView& v = seen.view;
    CHECK(seen.calls == 2 && v.size == sizeof(KilnUiItemView) && v.item == 0xAA && v.inventory_page == 0x5000 &&
              v.slot_index == 3 && v.count == 1 && v.client_entity == 17500 && v.server_entity == 0 &&
              v.flags == (KUI_DETAIL_COPY | KUI_INVENTORY_SLOT | KUI_CLIENT_ENTITY),
          "detail view: the record's ItemView prefix, a client entity, no server entity (flags %u)", v.flags);
    CHECK(v.fuel == 0 && v.fuel_max == 6400 && v.units_per_charge == 64 && v.durability == 70 &&
              v.durability_max == 100 && v.level == 3 && v.rarity == 2,
          "detail view: raw fuel units widened from u16, the charge divisor through the frame's play (%u)",
          v.units_per_charge);
    CHECK(seen.in.kind == KUI_DETAIL && seen.in.current == 0 && seen.in.maximum == 100 && seen.in.fraction == 0.0f &&
              seen.in.label_is_override == 0,
          "detail input: the bar's own numbers (charges) and the game's label");
    CHECK(v.local_player == 77 && v.session == 3 && v.frame == 501 && seen.ctx.frame == 501 &&
              seen.ctx.client_step == 9000 && seen.ctx.session == 3 && v.screen.ui_state == KILN_UI_STATE_PLAY,
          "detail stamp: the frame being drawn (last completed + 1), the client session, step and local player");
    CHECK(!inside(seen.view_at, &g_mem, sizeof g_mem) && !inside(seen.in_at, &g_mem, sizeof g_mem),
          "pointers: a provider gets copies, never a game pointer");
    CHECK(fake::writes == 0 && get<uint32_t>(g_mem.bar, 8) == 100, "pass-through: nothing is written");
    // the slot: its own mid, installed by its own first provider
    Seen slot;
    add_value(o, KUI_SLOT, 0, &observe, &slot);
    CHECK(fake::mids.size() == 2 && fake::mids[1].target == 0x140c05895ull,
          "install: the slot kind places its own mid");
    fill_bar(0.5f, 3200, 6400);  // slot water: raw fuel units
    fill_view_bytes(g_mem.slot_view, 0x5000, 4, 0xBB, 1, 0, 3200, 6400);
    draw_slot();
    CHECK(slot.calls == 1 && slot.view.flags == KUI_INVENTORY_SLOT && slot.view.client_entity == 0 &&
              slot.view.count == 1 && slot.in.current == 3200 && slot.in.maximum == 6400 && slot.in.fraction == 0.5f,
          "slot view: a stack without a pide is a slot without a client entity; input in raw fuel units");
    // an unreadable record: no provider is called, nothing is written
    const int before = seen.calls;
    fake::unreadable.insert(addr(g_mem.record) + 0x10);
    draw_detail();
    CHECK(seen.calls == before && fake::writes == 0, "guard: an unreadable record is skipped");
    fake::unreadable.clear();
}

void bars_synthetic() {
    prime_bars();
    const KilnOwner o = fake::owner("mod:synthetic");
    Seen seen;
    add_value(o, KUI_DETAIL, 0, &observe, &seen);
    fill_bar(1.0f, 7, 7);
    fill_view_bytes(g_mem.record, 0, 0, 0xCC, 0, 0, 0, 0);  // the game's prefix for an item id alone
    draw_detail();
    const KilnUiItemView& v = seen.view;
    CHECK(seen.calls == 1 && v.item == 0xCC && v.flags == (KUI_DETAIL_COPY | KUI_SYNTHETIC) && v.client_entity == 0 &&
              v.server_entity == 0 && v.inventory_page == 0 && v.slot_index == 0 && v.count == 0,
          "synthetic: an item id with no slot, count or entity (flags %u)", v.flags);
    fill_view_bytes(g_mem.record, 0x5000, 0, 0xCC, 5, 0, 0, 0);  // a real stack without a pide
    draw_detail();
    CHECK(v.flags == (KUI_DETAIL_COPY | KUI_INVENTORY_SLOT) && v.count == 5 && v.client_entity == 0,
          "non-pide stack: an inventory slot without a client entity, not synthetic");
}

void bars_chain_writes() {
    prime_bars();
    const KilnOwner a = fake::owner("mod:a"), b = fake::owner("mod:b");
    Script s1, s2;
    s1.name = "a";
    s1.flags = KUI_CURRENT | KUI_FRACTION;
    s1.current = 5;
    s1.fraction = 0.25f;
    s2.name = "b";
    s2.flags = KUI_CURRENT;
    s2.current = 9;
    const KilnHandle h1 = add_value(a, KUI_DETAIL, 0, &scripted, &s1);
    const KilnHandle h2 = add_value(b, KUI_DETAIL, 0, &scripted, &s2);
    fill_bar(0.5f, 50, 100);
    fill_view_bytes(g_mem.record, 0x5000, 1, 0xAA, 1, 900, 3200, 6400);
    draw_detail();
    CHECK(get<uint32_t>(g_mem.bar, 4) == 9 && get<float>(g_mem.bar, 0) == 0.25f && get<uint32_t>(g_mem.bar, 8) == 100 &&
              s2.seen.current == 5 && s2.seen.fraction == 0.25f,
          "last provider wins: the second sees the first's output (current %u, fraction %.2f) and its CURRENT stands",
          get<uint32_t>(g_mem.bar, 4), get<float>(g_mem.bar, 0));
    CHECK(fake::writes == 2, "commit: only the changed fields are written (%zu writes)", fake::writes);
    // a refusal keeps its predecessor's output
    s2.flags = KUI_FRACTION | KUI_CURRENT;
    s2.fraction = 0.75f;
    s2.current = 11;
    s2.result = KILN_ERR_UNAVAILABLE;
    fill_bar(0.5f, 50, 100);
    draw_detail();
    CHECK(get<float>(g_mem.bar, 0) == 0.25f && get<uint32_t>(g_mem.bar, 4) == 5,
          "refusal: the refused edit is dropped, the earlier provider's stands");
    T()->unsubscribe(h1);
    // an invalid edit writes nothing
    const uint8_t* bar = g_mem.bar;
    uint8_t snapshot[sizeof g_mem.bar];
    struct Bad {
        uint32_t flags;
        float fraction;
        uint32_t current, maximum;
        KilnUiTextId label;
        const char* what;
    } bad[] = {{KUI_FRACTION, 1.5f, 0, 0, 0, "fraction 1.5"},
               {KUI_FRACTION, NAN, 0, 0, 0, "fraction NaN"},
               {KUI_FRACTION, -0.1f, 0, 0, 0, "fraction -0.1"},
               {KUI_CURRENT, 0, 0x80000000u, 0, 0, "detail current 2147483648"},
               {KUI_MAXIMUM, 0, 0, 0xFFFFFFFFu, 0, "detail maximum 4294967295"},
               {KUI_LABEL | KUI_GAME_LABEL, 0, 0, 0, 0, "LABEL and GAME_LABEL"},
               {KUI_LABEL, 0, 0, 0, 0x5554000000000001ull, "a foreign text id"},
               {0x20, 0, 0, 0, 0, "an unknown flag"}};
    int clean = 0;
    for (const Bad& x : bad) {
        T()->unsubscribe(h2);
        Script s;
        s.name = "bad";
        s.flags = x.flags | KUI_CURRENT;  // a valid field with the invalid one: the whole edit goes
        s.current = 1;
        if (x.flags & KUI_CURRENT) s.current = x.current;
        s.fraction = x.fraction;
        s.maximum = x.maximum;
        s.label = x.label;
        const KilnHandle h = add_value(b, KUI_DETAIL, 0, &scripted, &s);
        fill_bar(0.5f, 50, 100);
        std::memcpy(snapshot, bar, sizeof snapshot);
        const size_t w = fake::writes;
        draw_detail();
        const bool ok = s.calls == 1 && std::memcmp(snapshot, bar, sizeof snapshot) == 0 && fake::writes == w;
        if (!ok) std::printf("  (%s wrote)\n", x.what);
        clean += ok;
        T()->unsubscribe(h);
    }
    CHECK(clean == 8,
          "invalid edit: nothing written for %d of 8 (fraction out of range or NaN, detail numbers above "
          "Int32.MaxValue, both label bits, a foreign text id, an unknown flag)",
          clean);
    Script edge;
    edge.flags = KUI_CURRENT | KUI_MAXIMUM;
    edge.current = 0x7FFFFFFFu;
    edge.maximum = 0x7FFFFFFFu;
    add_value(b, KUI_DETAIL, 0, &scripted, &edge);
    fill_bar(0.5f, 50, 100);
    draw_detail();
    CHECK(get<uint32_t>(g_mem.bar, 4) == 0x7FFFFFFFu && get<uint32_t>(g_mem.bar, 8) == 0x7FFFFFFFu,
          "detail numbers: Int32.MaxValue itself is taken");
}

void bars_slot_zero() {
    prime_bars();
    const KilnOwner o = fake::owner("mod:slot");
    Seen seen;
    add_value(o, KUI_SLOT, 0, &observe, &seen);
    fill_bar(1.0f, 0, 0);  // fuel 0/0: the game draws it full
    fill_view_bytes(g_mem.slot_view, 0x5000, 0, 0xBB, 1, 600, 0, 0);
    uint8_t snapshot[sizeof g_mem.bar];
    std::memcpy(snapshot, g_mem.bar, sizeof snapshot);
    draw_slot();
    CHECK(seen.calls == 1 && seen.in.fraction == 1.0f && seen.in.current == 0 && seen.in.maximum == 0 &&
              seen.view.fuel == 0 && seen.view.fuel_max == 0,
          "slot 0/0: the provider is called with the full bar");
    CHECK(
        std::memcmp(snapshot, g_mem.bar, sizeof snapshot) == 0 && fake::writes == 0 && get<float>(g_mem.bar, 0) == 1.0f,
        "slot 0/0: a pass-through leaves it full, nothing written");
    Script label;
    label.flags = KUI_LABEL;  // no label on a slot
    add_value(o, KUI_SLOT, 0, &scripted, &label);
    draw_slot();
    CHECK(std::memcmp(snapshot, g_mem.bar, sizeof snapshot) == 0 && fake::writes == 0, "slot: a label edit is invalid");
}

void bars_labels() {
    prime_bars();
    const KilnOwner a = fake::owner("mod:label-a"), b = fake::owner("mod:label-b");
    Script s1;
    s1.intern = "Charges";
    s1.label_from_intern = true;
    s1.flags = KUI_LABEL;
    add_value(a, KUI_DETAIL, 0, &scripted, &s1);
    fill_bar(0.5f, 50, 100);
    fill_view_bytes(g_mem.record, 0x5000, 1, 0xAA, 1, 900, 0, 0);
    draw_detail();
    const auto written = get<text::GameText>(g_mem.bar, 0x38);
    std::string_view stored;
    text::bytes(s1.interned, &stored);
    CHECK(s1.intern_rc == KILN_OK && written.p == stored.data() && written.n == 7 &&
              written.fnv == text::fnv64("Charges", 7),
          "label: LABEL writes a GameText over the store's permanent bytes, not the provider's");
    Script s2;
    s2.flags = KUI_GAME_LABEL;
    const KilnHandle h2 = add_value(b, KUI_DETAIL, 0, &scripted, &s2);
    fill_bar(0.5f, 50, 100);
    const size_t w0 = fake::writes;
    draw_detail();
    const auto kept = get<text::GameText>(g_mem.bar, 0x38);
    CHECK(s2.seen.label_is_override == 1 && s2.seen.label_id == s1.interned && kept.p == kGameLabel.p &&
              kept.n == kGameLabel.n && kept.fnv == kGameLabel.fnv && fake::writes == w0,
          "label: GAME_LABEL after another provider's LABEL keeps the game's own text (nothing written)");
    T()->unsubscribe(h2);
    Script s3;
    s3.flags = KUI_LABEL;  // id 0: cleared
    add_value(b, KUI_DETAIL, 0, &scripted, &s3);
    fill_bar(0.5f, 50, 100);
    draw_detail();
    const auto cleared = get<text::GameText>(g_mem.bar, 0x38);
    CHECK(cleared.n == 0 && cleared.p != nullptr && cleared.fnv == text::fnv64("", 0),
          "label: LABEL with id 0 clears it to the empty text");
}

void bars_kinds_alone() {
    prime_bars();
    fake::symbols.erase("ui/DetailBarDraw");
    const KilnOwner o = fake::owner("mod:alone");
    auto r = reg_record();
    CHECK(T()->subscribe(o, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE && fake::rows_off.size() == 1 &&
              fake::rows_off[0] == "ui.values.detail",
          "kinds: a detail site that did not resolve turns ui.values.detail off");
    r = reg_record();
    CHECK(T()->subscribe(o, KUI_SLOT, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1 && fake::mids.size() == 1,
          "kinds: the slot kind still installs and registers");
    prime_bars();
    fake::constants.erase("ui/view.rarity");
    r = reg_record();
    CHECK(T()->subscribe(fake::owner("mod:alone2"), KUI_SLOT, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE &&
              fake::mids.empty(),
          "kinds: a missing view constant installs nothing");
    prime_bars();
    fake::rows.erase("ui.values.slot");  // held by the client gate
    r = reg_record();
    CHECK(T()->subscribe(fake::owner("mod:held"), KUI_SLOT, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE &&
              fake::mids.empty(),
          "gate: a held row never reaches game code");
}

// ===================================================================== S3: the player-UI update, HUD and held item
// Fake play data (the client's offsets) and a scripted "game update": the raw link's continuation runs a list of
// events (the loop storing the iterated entity, the two capture mids, a nested update) as the game's update would.
struct FakePlay {
    std::vector<uint8_t> b = std::vector<uint8_t>(0x3100, 0);
    std::vector<uint8_t> models;  // contiguous models of stride 0x8f70
    std::vector<std::vector<uint8_t>> views;
    uintptr_t at() { return addr(b.data()); }
    void entity(uint32_t e) { put(b.data(), 0x18, e); }
    void pair(uint32_t kind, uint32_t cur, uint32_t max) {
        static const size_t off[] = {0, 0, 0x2e40, 0x578, 0x584, 0x59c};
        put(b.data(), off[kind], cur);
        put(b.data(), off[kind] + 4, max);
    }
    std::pair<uint32_t, uint32_t> pair_of(uint32_t kind) {
        static const size_t off[] = {0, 0, 0x2e40, 0x578, 0x584, 0x59c};
        return {get<uint32_t>(b.data(), off[kind]), get<uint32_t>(b.data(), off[kind] + 4)};
    }
    // models: one per owner, each with its views (page, index, item, count, pide)
    struct V {
        uint32_t page;
        uint8_t index;
        uint32_t item, count, pide;
    };
    void set_models(const std::vector<std::pair<uint32_t, std::vector<V>>>& ms) {
        models.assign(ms.size() * 0x8f70 + 16, 0);
        views.clear();
        for (size_t m = 0; m < ms.size(); ++m) {
            uint8_t* model = models.data() + m * 0x8f70;
            put(model, 0, ms[m].first);
            auto& vb = views.emplace_back(ms[m].second.size() * 0x48 + 1, 0);
            for (size_t i = 0; i < ms[m].second.size(); ++i) {
                const V& v = ms[m].second[i];
                fill_view_bytes(vb.data() + i * 0x48, v.page, v.index, v.item, v.count, v.pide, 3200, 6400);
            }
            put(model, 0x2d8, addr(vb.data()));
            put(model, 0x2e0, static_cast<uint64_t>(ms[m].second.size()));
        }
        put(b.data(), 0x3060, addr(models.data()));
        put(b.data(), 0x3068, static_cast<uint64_t>(ms.size()));
    }
};

struct Event {
    enum Kind { Entity, Capture, Held, Nested } kind;
    FakePlay* play = nullptr;
    uint32_t value = 0;  // Entity: the entity; Held: the pide
    bool fuel = false;   // Held: rbx set
    const std::vector<Event>* nested = nullptr;
};
using UpdateHook = void (*)(void*);
UpdateHook g_hook = nullptr;
int g_original_calls = 0;

void run_mid(const char* label, uintptr_t rsi, uintptr_t rdi, uintptr_t rbx) {
    KilnRegisters r{};
    r.rsi = rsi;
    r.rdi = rdi;
    r.rbx = rbx;
    if (KilnMidFn fn = mid_at(label)) fn(&r, nullptr);
}
// The game's update as the raw link's continuation sees it.
void game_update(void* it) {
    ++g_original_calls;
    for (const Event& e : *static_cast<const std::vector<Event>*>(it)) {
        switch (e.kind) {
            case Event::Entity:
                e.play->entity(e.value);
                break;
            case Event::Capture:
                run_mid("ui.values/PlayerUiPlayCapture", e.play->at(), 0, 0);
                break;
            case Event::Held:
                run_mid("ui.values/HeldItemCapture", e.play->at(), 0xFFFFFFFF00000000ull | e.value,
                        e.fuel ? 0x1000 : 0);
                break;
            case Event::Nested:
                g_hook(const_cast<std::vector<Event>*>(e.nested));
                break;
        }
    }
}
void update(const std::vector<Event>& script) { g_hook(const_cast<std::vector<Event>*>(&script)); }

uint8_t g_regbuf[0x40], g_resource[0x100];

void prime_update() {
    prime_bars();  // the view and charge constants, a clean registry
    using namespace kiln::uivalues;
    g_upd = {};
    g_base = false;
    g_hud = false;
    g_held = false;
    g_models = false;
    g_update_next = nullptr;
    g_stage.list.clear();
    g_stage.publish();
    g_held_missing_logged = false;
    for (uint32_t k = KUI_HELD; k <= KUI_MANA; ++k) set_installer(k, &install_update);
    fake::symbols["ui/PlayerUiUpdate"] = 0x1402b8430ull;
    fake::symbols["ui/PlayerUiPlayCapture"] = 0x1402b8c1eull;
    fake::symbols["ui/HeldItemCapture"] = 0x1402bbb70ull;
    const std::map<std::string, int64_t> c = {
        {"ui/play.localEntity", 0x18}, {"ui/play.health", 0x578},       {"ui/play.healthMax", 0x57c},
        {"ui/play.stamina", 0x584},    {"ui/play.staminaMax", 0x588},   {"ui/play.mana", 0x59c},
        {"ui/play.manaMax", 0x5a0},    {"ui/play.heldFuel", 0x2e40},    {"ui/play.heldFuelMax", 0x2e44},
        {"ui/play.models", 0x3060},    {"ui/play.modelsCount", 0x3068}, {"ui/model.owner", 0},
        {"ui/model.stride", 0x8f70},   {"ui/model.views", 0x2d8},       {"ui/model.viewsCount", 0x2e0}};
    for (const auto& [k, v] : c) fake::constants[k] = v;
    put(g_regbuf, 0x20, addr(g_resource));
    put(g_resource, 0xfc, uint32_t{64});
    fake::client = ClientNow{77, 4, 12000};
    fake::screen.frame = 900;
    g_original_calls = 0;
}
// Installs the update (through a HUD or held registration) and hooks the scripted game behind the raw link.
bool arm() {
    for (const auto& r : fake::raws)
        if (r.label == "ui.values/PlayerUiUpdate") {
            g_hook = reinterpret_cast<UpdateHook>(r.fn);
            *r.original = reinterpret_cast<void*>(&game_update);
            return true;
        }
    return false;
}
void give_balancing(FakePlay& p) { put(p.b.data(), 0x40, addr(g_regbuf)); }

void update_install() {
    prime_update();
    const KilnOwner o = fake::owner("mod:hud");
    add_value(o, KUI_HEALTH, 0, &pass, nullptr);
    add_value(o, KUI_MANA, 0, &pass, nullptr);
    CHECK(fake::raws.size() == 1 && fake::raws[0].target == 0x1402b8430ull && fake::mids.size() == 1 &&
              fake::mids[0].target == 0x1402b8c1eull && arm(),
          "install: the HUD kinds place one raw link on PlayerUiUpdate and the play capture, once (%zu raw, %zu mid)",
          fake::raws.size(), fake::mids.size());
    add_value(o, KUI_HELD, 0, &pass, nullptr);
    CHECK(fake::raws.size() == 1 && fake::mids.size() == 2 && fake::mids[1].target == 0x1402bbb70ull,
          "install: the held kind adds only its own mid (HeldItemCapture)");
    prime_update();
    fake::rows.erase("ui.values.held");
    auto r = reg_record();
    CHECK(T()->subscribe(fake::owner("mod:x"), KUI_HELD, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE &&
              fake::raws.empty() && fake::mids.empty(),
          "gate: ui.values.held held -> UNAVAILABLE, nothing hooked");
    prime_update();
    fake::rows.erase("ui.values.hud");
    KilnHandle h = 0;
    CHECK(stage_subscribe(
              0, [](const Stage*, void*) {}, nullptr, &h) == KILN_ERR_UNAVAILABLE &&
              h == 0 && fake::raws.empty(),
          "stage: needs ui.values.hud (held: UNAVAILABLE, nothing hooked)");
    prime_update();
    fake::constants.erase("ui/play.manaMax");
    r = reg_record();
    CHECK(T()->subscribe(fake::owner("mod:y"), KUI_HEALTH, 0, &pass, nullptr, &r) == KILN_ERR_UNAVAILABLE &&
              fake::raws.empty(),
          "install: a missing HUD constant turns ui.values.hud off before anything is hooked");
}

void update_hud() {
    prime_update();
    const KilnOwner o = fake::owner("mod:hud");
    Script health, stamina, mana;
    health.name = "health";
    health.flags = KUI_CURRENT | KUI_MAXIMUM;
    health.current = 37;
    health.maximum = 100;
    stamina.name = "stamina";
    mana.name = "mana";
    mana.flags = KUI_MAXIMUM;  // 0: an invalid HUD maximum
    add_value(o, KUI_HEALTH, 0, &scripted, &health);
    add_value(o, KUI_STAMINA, 0, &scripted, &stamina);
    add_value(o, KUI_MANA, 0, &scripted, &mana);
    arm();
    FakePlay p;
    p.entity(77);
    p.pair(KUI_HEALTH, 413, 500);
    p.pair(KUI_STAMINA, 80, 100);
    p.pair(KUI_MANA, 20, 40);
    update({{Event::Capture, &p}});
    CHECK(g_original_calls == 1 && p.pair_of(KUI_HEALTH) == std::make_pair(37u, 100u),
          "hud: the original runs once; health's provider writes the captured pair (%u/%u)",
          p.pair_of(KUI_HEALTH).first, p.pair_of(KUI_HEALTH).second);
    CHECK(health.seen.current == 413 && health.seen.maximum == 500 && std::fabs(health.seen.fraction - 0.826f) < 1e-3 &&
              health.view.local_player == 77 && health.view.item == 0 && health.view.frame == 900 &&
              health.view.session == 4,
          "hud: the input is the game's pair, the fraction derived; the view names the local player and the last "
          "completed frame");
    CHECK(stamina.calls == 1 && p.pair_of(KUI_STAMINA) == std::make_pair(80u, 100u) && mana.calls == 1 &&
              p.pair_of(KUI_MANA) == std::make_pair(20u, 40u) && fake::writes == 2,
          "hud: a pass-through and an invalid MAXIMUM 0 write nothing (%zu writes in all)", fake::writes);
    p.pair(KUI_MANA, 0, 0);
    const int mana_calls = mana.calls;
    update({{Event::Capture, &p}});
    CHECK(mana.calls == mana_calls, "hud: no callback for a pair whose game maximum is 0");
    fake::client.local_player = 0;
    const int calls = health.calls;
    update({{Event::Capture, &p}});
    CHECK(health.calls == calls && g_original_calls == 3, "hud: no local player -> nothing captured, no callback");
}

void update_entities() {
    prime_update();
    const KilnOwner o = fake::owner("mod:entities");
    Script health;
    health.flags = KUI_CURRENT;
    health.current = 1;
    add_value(o, KUI_HEALTH, 0, &scripted, &health);
    arm();
    FakePlay x, l, y;
    x.entity(5);
    l.entity(77);
    y.entity(6);
    for (FakePlay* p : {&x, &l, &y}) p->pair(KUI_HEALTH, 50, 60);
    update({{Event::Capture, &x}, {Event::Capture, &l}, {Event::Capture, &y}});
    CHECK(health.calls == 1 && l.pair_of(KUI_HEALTH).first == 1 && x.pair_of(KUI_HEALTH).first == 50 &&
              y.pair_of(KUI_HEALTH).first == 50,
          "entities: of three iterated entities, only the local player's play data is patched");
    // one play data reused for every iterated entity: the last one written is not the local player
    FakePlay shared;
    shared.pair(KUI_HEALTH, 50, 60);
    update({{Event::Entity, &shared, 5},
            {Event::Capture, &shared},
            {Event::Entity, &shared, 77},
            {Event::Capture, &shared},
            {Event::Entity, &shared, 6},
            {Event::Capture, &shared}});
    CHECK(health.calls == 1 && shared.pair_of(KUI_HEALTH).first == 50,
          "entities: play data that holds another entity after the update is not patched");
    update({{Event::Entity, &shared, 5},
            {Event::Capture, &shared},
            {Event::Entity, &shared, 77},
            {Event::Capture, &shared}});
    CHECK(health.calls == 2 && shared.pair_of(KUI_HEALTH).first == 1,
          "entities: the same play data ending on the local player is patched");
    FakePlay a, b;
    a.entity(77);
    b.entity(77);
    a.pair(KUI_HEALTH, 50, 60);
    b.pair(KUI_HEALTH, 50, 60);
    update({{Event::Capture, &a}, {Event::Capture, &b}});
    CHECK(health.calls == 2 && a.pair_of(KUI_HEALTH).first == 50 && b.pair_of(KUI_HEALTH).first == 50,
          "entities: two play datas claiming the local player -> ambiguous, nothing patched");
}

void update_recursion() {
    prime_update();
    const KilnOwner o = fake::owner("mod:nested");
    Script health, held;
    health.flags = KUI_CURRENT;
    health.current = 1;
    held.flags = KUI_CURRENT;
    held.current = 2;
    add_value(o, KUI_HEALTH, 0, &scripted, &health);
    add_value(o, KUI_HELD, 0, &scripted, &held);
    arm();
    FakePlay outer, inner;
    outer.entity(77);
    inner.entity(77);
    outer.pair(KUI_HEALTH, 300, 300);
    inner.pair(KUI_HEALTH, 200, 200);
    outer.pair(KUI_HELD, 3200, 6400);
    give_balancing(outer);
    outer.set_models({{77, {{0x5000, 0, 0xAA, 1, 17500}}}});
    const std::vector<Event> nested = {{Event::Capture, &inner}};
    update({{Event::Capture, &outer}, {Event::Nested, nullptr, 0, false, &nested}, {Event::Held, &outer, 17500, true}});
    CHECK(g_original_calls == 2 && health.calls == 2 && inner.pair_of(KUI_HEALTH).first == 1 &&
              outer.pair_of(KUI_HEALTH).first == 1,
          "recursion: the nested update patches its own capture, the outer update its own (calls %d)", health.calls);
    CHECK(held.calls == 1 && outer.pair_of(KUI_HELD).first == 2 && held.view.client_entity == 17500,
          "recursion: a capture made after the nested update returned still belongs to the outer one");
    CHECK(!kiln::uivalues::t_capture.active, "recursion: the thread's capture is idle again after the outer update");
}

void update_held() {
    prime_update();
    const KilnOwner o = fake::owner("mod:held");
    Script held;
    held.flags = KUI_CURRENT | KUI_MAXIMUM;
    held.current = 1;
    held.maximum = 4;
    add_value(o, KUI_HELD, 0, &scripted, &held);
    Script other;  // filtered to another item
    add_value(o, KUI_HELD, 0xBB, &scripted, &other);
    arm();
    FakePlay p;
    p.entity(77);
    give_balancing(p);
    p.pair(KUI_HELD, 3200, 6400);
    p.set_models({{77, {{0x5000, 0, 0xAA, 1, 17500}, {0x5000, 1, 0, 0, 0}, {0x5001, 2, 0xAB, 3, 0}}},
                  {99, {{0x6000, 0, 0xBB, 1, 17600}}}});
    update({{Event::Capture, &p}, {Event::Held, &p, 17500, true}});
    const KilnUiItemView& v = held.view;
    CHECK(held.calls == 1 && v.item == 0xAA && v.client_entity == 17500 &&
              v.flags == (KUI_INVENTORY_SLOT | KUI_CLIENT_ENTITY) && v.inventory_page == 0x5000 &&
              v.units_per_charge == 64 && v.fuel == 3200 && v.fuel_max == 6400 && held.seen.current == 3200 &&
              held.seen.maximum == 6400,
          "held: the held pide's view from the local inventory; the pair in raw fuel units (%d calls)", held.calls);
    CHECK(p.pair_of(KUI_HELD) == std::make_pair(1u, 4u) && other.calls == 0,
          "held: the pair is written; a provider for another item is not called");
    // a pide only in another owner's model (an open container): rejected, not invented
    p.pair(KUI_HELD, 3200, 6400);
    update({{Event::Capture, &p}, {Event::Held, &p, 17600, true}});
    CHECK(held.calls == 1 && other.calls == 0 && p.pair_of(KUI_HELD) == std::make_pair(3200u, 6400u) &&
              fake::logged("held stack's view was not found") == 1,
          "held: a held view missing from the local inventory -> no callback, nothing written");
    // two views with the pide naming different items: rejected
    p.set_models({{77, {{0x5000, 0, 0xAA, 1, 17500}, {0x5000, 1, 0xAC, 1, 17500}}}});
    update({{Event::Capture, &p}, {Event::Held, &p, 17500, true}});
    CHECK(held.calls == 1 && p.pair_of(KUI_HELD) == std::make_pair(3200u, 6400u),
          "held: two views of the pide with different items -> rejected");
    // nothing held: the game's no-item branch reaches the mid with pide 0 and no fuel, and the pair is 0/0
    p.set_models({{77, {{0x5000, 0, 0xAA, 1, 17500}}}});
    p.pair(KUI_HELD, 0, 0);
    update({{Event::Capture, &p}, {Event::Held, &p, 0, false}});
    update({{Event::Capture, &p}, {Event::Held, &p, 17500, true}});
    CHECK(held.calls == 1 && p.pair_of(KUI_HELD) == std::make_pair(0u, 0u),
          "held: no callback when the held maximum is 0 (nothing held, or a stack without fuel)");
    p.pair(KUI_HELD, 3200, 6400);
    update({{Event::Capture, &p}});
    CHECK(held.calls == 1, "held: no held capture in the update -> no callback");
    held.flags = KUI_MAXIMUM;
    held.maximum = 0;
    update({{Event::Capture, &p}, {Event::Held, &p, 17500, true}});
    CHECK(held.calls == 2 && p.pair_of(KUI_HELD) == std::make_pair(3200u, 6400u),
          "held: an edit with MAXIMUM 0 is invalid, nothing written");
}

struct StageLog {
    std::vector<std::string>* order = nullptr;
    Stage last{};
    std::vector<StageRow> rows;
    int calls = 0;
};
void stage_observer(const Stage* s, void* user) {
    auto* log = static_cast<StageLog*>(user);
    ++log->calls;
    log->last = *s;
    log->rows.assign(s->rows, s->rows + s->row_count);
    g_calls.order.push_back("stage");
}

void update_stage() {
    prime_update();
    const KilnOwner o = fake::owner("mod:stage");
    Script health;
    health.name = "health";
    health.flags = KUI_CURRENT;
    health.current = 37;
    StageLog log;
    KilnHandle h = 0;
    CHECK(stage_subscribe(0, &stage_observer, &log, &h) == KILN_OK && h != 0 && arm(),
          "stage: an observer installs the update hook (ui.values.hud available)");
    add_value(o, KUI_HEALTH, 0, &scripted, &health);
    FakePlay p;
    p.entity(77);
    p.pair(KUI_HEALTH, 413, 500);
    p.pair(KUI_STAMINA, 80, 100);
    p.pair(KUI_MANA, 20, 40);
    p.set_models(
        {{77, {{0x5001, 2, 0xAB, 3, 0}, {0x5000, 7, 0xAA, 1, 17500}, {0x5000, 1, 0, 0, 0}, {0x5000, 0, 0xAC, 5, 900}}},
         {99, {{0x6000, 0, 0xBB, 1, 17600}}}});
    g_calls.order.clear();
    update({{Event::Capture, &p}});
    const Stage& s = log.last;
    CHECK(log.calls == 1 && g_calls.order == (std::vector<std::string>{"stage", "health"}) &&
              p.pair_of(KUI_HEALTH).first == 37,
          "stage: delivered after the original, before the providers");
    CHECK(s.health == 413 && s.health_max == 500 && s.stamina == 80 && s.mana_max == 40 && s.local_player == 77 &&
              s.session == 4 && s.step == 12000 && s.capture_ms == fake::ticks &&
              s.available == (kStageVitals | kStageInventory),
          "stage: the game's original pairs, tagged with the client session and step");
    CHECK(log.rows.size() == 3 && s.rows_total == 3 && log.rows[0].slot_index == 0 && log.rows[0].item == 0xAC &&
              log.rows[0].count == 5 && log.rows[0].pide == 900 && log.rows[1].slot_index == 7 &&
              log.rows[1].pide == 17500 && log.rows[2].inventory_page == 0x5001 && log.rows[0].holder == 77,
          "stage: the local player's own nonempty slots sorted by (page, index), no container's (%zu rows)",
          log.rows.size());
    std::vector<FakePlay::V> many;
    for (uint32_t i = 0; i < 600; ++i) many.push_back({0x7000 + i / 8, static_cast<uint8_t>(i % 8), 0xAA, 1, 0});
    p.set_models({{77, many}});
    update({{Event::Capture, &p}});
    CHECK(log.calls == 2 && log.last.rows_total == 600 && log.last.row_count == 512 &&
              log.rows.back().inventory_page == 0x7000 + 511 / 8,
          "stage: 600 slots -> the first 512 rows and rows_total 600");
    fake::constants.clear();  // unchanged layout: the update keeps what it loaded
    CHECK(stage_unsubscribe(h) == KILN_OK && stage_unsubscribe(h) == KILN_ERR_NOT_FOUND, "stage: unsubscribe once");
    update({{Event::Capture, &p}});
    CHECK(log.calls == 2, "stage: no call after unsubscribe");
    prime_update();
    const KilnOwner gone = fake::owner("mod:gone");
    StageLog log2;
    stage_subscribe(gone, &stage_observer, &log2, &h);
    arm();
    fake::revoke(gone);
    FakePlay q;
    q.entity(77);
    q.pair(KUI_HEALTH, 1, 2);
    update({{Event::Capture, &q}});
    CHECK(log2.calls == 0 && g_stage_count == 0, "stage: a retired owner's observer is dropped");
}

// ======================================================================================= S4: change publication
struct Changes {
    std::vector<KilnUiValueChange> got;
    std::vector<std::string> labels;
    int throws = 0;
};
void KILN_CALL on_change(const KilnUiValueChange* c, void* user) {
    auto* ch = static_cast<Changes*>(user);
    if (ch->throws > 0) {
        --ch->throws;
        throw std::runtime_error("subscriber bug");
    }
    ch->got.push_back(*c);
    ch->labels.emplace_back(c->after_label.ptr ? std::string(c->after_label.ptr, c->after_label.len) : "");
}
KilnUiItemView slot_view(uint32_t page, uint32_t index, uint32_t item, uint32_t entity, uint64_t session = 1) {
    KilnUiItemView v{};
    v.size = sizeof v;
    v.flags = KUI_INVENTORY_SLOT | (entity ? KUI_CLIENT_ENTITY : 0u);
    v.local_player = 77;
    v.inventory_page = page;
    v.slot_index = index;
    v.item = item;
    v.client_entity = entity;
    v.session = session;
    v.screen.ui_state = KILN_UI_STATE_PLAY;
    v.screen.hud_screen = 9;
    return v;
}
size_t count_kind(const Changes& c, uint32_t kind) {
    size_t n = 0;
    for (const auto& x : c.got) n += x.change_kind == kind;
    return n;
}

void changes_basics() {
    fake::reset();
    fake::client.session = 1;
    const KilnOwner o = fake::owner("mod:changes");
    add_value(o, KUI_SLOT, 0, &pass, nullptr);  // a value provider: the registry is not empty
    Changes ch;
    auto r = reg_record();
    CHECK(T()->subscribe_changes(0, &on_change, &ch, &r) == KILN_ERR_INVALID_ARGUMENT &&
              T()->subscribe_changes(o, nullptr, &ch, &r) == KILN_ERR_INVALID_ARGUMENT,
          "changes: owner 0 or no callback -> INVALID_ARGUMENT");
    fake::frames_ok = false;
    CHECK(T()->subscribe_changes(o, &on_change, &ch, &r) == KILN_ERR_UNAVAILABLE && !changes_wanted(),
          "changes: no game UI frame callback (kiln.ui missing) -> UNAVAILABLE");
    fake::frames_ok = true;
    CHECK(T()->subscribe_changes(o, &on_change, &ch, &r) == KILN_OK && r.ok == 1 && changes_wanted() &&
              active("mod:changes") == 2,
          "changes: registered, counted in the mod's quota");
    const auto v = slot_view(0x5000, 1, 0xAA, 900);
    publish_value(KUI_SLOT, v, game_value(KUI_SLOT, 0.5f, 1, 2));
    publish_value(KUI_SLOT, v, game_value(KUI_SLOT, 0.25f, 1, 2));  // the last occurrence of the frame wins
    fake::end_frame(10);
    CHECK(ch.got.size() == 1 && ch.got[0].change_kind == KUI_ADDED && !ch.got[0].has_before && ch.got[0].has_after &&
              ch.got[0].after.fraction == 0.25f && ch.got[0].frame == 10 &&
              ch.got[0].identity.inventory_page == 0x5000 && ch.got[0].identity.slot_index == 1 &&
              ch.got[0].identity.client_entity == 900 && ch.got[0].identity.hud_screen == 9 &&
              ch.got[0].identity.local_player == 77,
          "changes: first value -> Added (no before), the frame's last occurrence, the slot identity");
    publish_value(KUI_SLOT, v, game_value(KUI_SLOT, 0.25f, 1, 2));
    fake::end_frame(11);
    CHECK(ch.got.size() == 1, "changes: the same value again -> nothing");
    publish_value(KUI_SLOT, v, game_value(KUI_SLOT, 0.75f, 1, 2));
    fake::end_frame(12);
    CHECK(ch.got.size() == 2 && ch.got[1].change_kind == KUI_UPDATED && ch.got[1].before.fraction == 0.25f &&
              ch.got[1].after.fraction == 0.75f,
          "changes: a different value -> Updated with before and after");
    fake::end_frame(13);  // absent once
    CHECK(ch.got.size() == 2, "changes: absent for one completed frame -> still there");
    fake::end_frame(14);  // absent twice
    CHECK(ch.got.size() == 3 && ch.got[2].change_kind == KUI_REMOVED && ch.got[2].has_before && !ch.got[2].has_after &&
              ch.got[2].before.fraction == 0.75f,
          "changes: absent for two completed frames -> Removed (no after)");
    // a synthetic detail groups by kind, session, screen and item
    KilnUiItemView syn{};
    syn.size = sizeof syn;
    syn.flags = KUI_DETAIL_COPY | KUI_SYNTHETIC;
    syn.item = 0xCC;
    syn.local_player = 77;
    syn.session = 1;
    publish_value(KUI_DETAIL, syn, game_value(KUI_DETAIL, 1, 7, 7));
    fake::end_frame(15);
    const auto& id = ch.got.back().identity;
    CHECK(ch.got.back().change_kind == KUI_ADDED && id.synthetic_group == 1 && id.item == 0xCC &&
              id.local_player == 0 && id.inventory_page == 0 && id.client_entity == 0,
          "changes: a synthetic detail's identity is its item and screen only (synthetic_group 1)");
}

void changes_reset_and_hud() {
    fake::reset();
    fake::client.session = 1;
    const KilnOwner o = fake::owner("mod:changes");
    const KilnHandle provider = add_value(o, KUI_SLOT, 0, &pass, nullptr);
    Changes ch;
    auto r = reg_record();
    T()->subscribe_changes(o, &on_change, &ch, &r);
    for (uint32_t i = 0; i < 20; ++i)
        publish_value(KUI_SLOT, slot_view(0x5000, i, 0xAA, 0), game_value(KUI_SLOT, 1, 0, 0));
    fake::end_frame(1);
    CHECK(count_kind(ch, KUI_ADDED) == 20, "reset: 20 identities added");
    ch.got.clear();
    fake::client.session = 2;  // the world was left and another joined
    fake::end_frame(2);
    CHECK(ch.got.size() == 1 && ch.got[0].change_kind == KUI_RESET && ch.got[0].reason == KUI_RESET_WORLD &&
              ch.got[0].old_session == 1 && ch.got[0].new_session == 2 && !ch.got[0].has_before && !ch.got[0].has_after,
          "reset: a session change -> one Reset (world, old and new session), not 20 Removed (%zu records)",
          ch.got.size());
    fake::end_frame(3);
    fake::end_frame(4);
    CHECK(ch.got.size() == 1, "reset: the cleared cache removes nothing later");
    // the last value provider goes: one Reset (registry)
    publish_value(KUI_SLOT, slot_view(0x5000, 0, 0xAA, 0, 2), game_value(KUI_SLOT, 1, 0, 0));
    fake::end_frame(5);
    T()->unsubscribe(provider);
    ch.got.clear();
    fake::end_frame(6);
    CHECK(ch.got.size() == 1 && ch.got[0].change_kind == KUI_RESET && ch.got[0].reason == KUI_RESET_REGISTRY,
          "reset: the last value provider removed -> one Reset (registry)");

    // HUD and held values count player-UI updates, not frames, for absence
    fake::reset();
    fake::client.session = 1;
    const KilnOwner h = fake::owner("mod:hud");
    add_value(h, KUI_HEALTH, 0, &pass, nullptr);
    Changes hud;
    r = reg_record();
    T()->subscribe_changes(h, &on_change, &hud, &r);
    KilnUiItemView hv{};
    hv.size = sizeof hv;
    hv.local_player = 77;
    hv.session = 1;
    hv.screen.hud_screen = 4;  // not part of a HUD identity
    publish_value(KUI_HEALTH, hv, game_value(KUI_HEALTH, 0.5f, 50, 100));
    update_done();
    fake::end_frame(1);
    for (uint64_t f = 2; f < 8; ++f) fake::end_frame(f);  // frames without a player-UI update
    CHECK(hud.got.size() == 1 && hud.got[0].change_kind == KUI_ADDED && hud.got[0].identity.hud_screen == 0 &&
              hud.got[0].identity.local_player == 77,
          "hud: frames without an update remove nothing; the identity is kind, session, local player");
    update_done();
    fake::end_frame(8);
    update_done();
    fake::end_frame(9);
    CHECK(hud.got.size() == 2 && hud.got[1].change_kind == KUI_REMOVED, "hud: two updates without it -> Removed");
}

void changes_eviction_and_faults() {
    fake::reset();
    fake::client.session = 1;
    const KilnOwner o = fake::owner("mod:many");
    add_value(o, KUI_SLOT, 0, &pass, nullptr);
    Changes ch;
    auto r = reg_record();
    T()->subscribe_changes(o, &on_change, &ch, &r);
    // 4096 identities (pages 1..4096) staged in frame 1; all but pages 7 and 9 again in frames 2 and 3
    for (uint32_t p = 1; p <= 4096; ++p)
        publish_value(KUI_SLOT, slot_view(p, 0, 0xAA, 0), game_value(KUI_SLOT, 1, 0, 0));
    fake::end_frame(1);
    for (uint32_t p = 1; p <= 4096; ++p)
        if (p != 7 && p != 9) publish_value(KUI_SLOT, slot_view(p, 0, 0xAA, 0), game_value(KUI_SLOT, 1, 0, 0));
    fake::end_frame(2);
    ch.got.clear();
    for (uint32_t p = 1; p <= 4096; ++p)
        if (p != 7 && p != 9) publish_value(KUI_SLOT, slot_view(p, 0, 0xAA, 0), game_value(KUI_SLOT, 1, 0, 0));
    publish_value(KUI_SLOT, slot_view(5000, 0, 0xAB, 0), game_value(KUI_SLOT, 1, 0, 0));
    publish_value(KUI_SLOT, slot_view(5001, 0, 0xAB, 0), game_value(KUI_SLOT, 1, 0, 0));
    fake::end_frame(3);
    std::vector<uint32_t> evicted;
    for (const auto& c : ch.got)
        if (c.change_kind == KUI_EVICTED) evicted.push_back(c.identity.inventory_page);
    CHECK(evicted == (std::vector<uint32_t>{7, 9}) && count_kind(ch, KUI_ADDED) == 2,
          "eviction: a full cache evicts the least recently seen, the smaller key first (%zu evicted)", evicted.size());
    // a subscriber that throws five times in a row is removed
    Changes bad;
    bad.throws = 100;
    r = reg_record();
    T()->subscribe_changes(o, &on_change, &bad, &r);
    for (uint64_t f = 4; f < 12; ++f) {
        publish_value(KUI_SLOT, slot_view(1, 0, 0xAA, 0), game_value(KUI_SLOT, (f % 2) ? 0.5f : 0.25f, 0, 0));
        fake::end_frame(f);
    }
    CHECK(bad.throws == 95 && fake::logged("removed after 5 faults") == 1,
          "faults: a change subscriber that throws five times in a row is removed (%d calls)", 100 - bad.throws);
    // labels: the record carries the store's bytes
    KilnUiTextId id = 0;
    text::intern("mod:many", "Charges", &id);
    KilnUiBarValue lv = game_value(KUI_DETAIL, 1, 1, 1);
    lv.label_is_override = 1;
    lv.label_id = id;
    add_value(o, KUI_DETAIL, 0, &pass, nullptr);
    ch.got.clear();
    ch.labels.clear();
    publish_value(KUI_DETAIL, slot_view(0x5000, 3, 0xAA, 0), lv);
    fake::end_frame(20);
    std::string label;
    for (size_t i = 0; i < ch.got.size(); ++i)
        if (ch.got[i].change_kind == KUI_ADDED && ch.got[i].identity.kind == KUI_DETAIL) label = ch.labels[i];
    CHECK(label == "Charges", "labels: an Added record carries the label's bytes");
}

void changes_from_the_bars() {
    prime_bars();
    fake::client.session = 3;
    const KilnOwner o = fake::owner("mod:bars-changes");
    Script s;
    s.flags = KUI_FRACTION;
    s.fraction = 0.25f;
    add_value(o, KUI_SLOT, 0, &scripted, &s);
    Changes ch;
    auto r = reg_record();
    T()->subscribe_changes(o, &on_change, &ch, &r);
    fill_bar(1.0f, 0, 0);
    fill_view_bytes(g_mem.slot_view, 0x5000, 2, 0xBB, 1, 600, 0, 0);
    draw_slot();
    draw_slot();
    fake::end_frame(501);
    CHECK(ch.got.size() == 1 && ch.got[0].change_kind == KUI_ADDED && ch.got[0].after.fraction == 0.25f &&
              ch.got[0].identity.client_entity == 600 && ch.got[0].identity.session == 3,
          "bars: a drawn slot publishes the provider's final value once per frame");
}

// ============================================================================================ S5: visible slots
// A scripted game UI frame: the frame link's continuation draws slots through the ItemSlot link; each slot's own
// continuation calls the geometry mid (rax -> inner xywh, r15 = descriptor), may draw a nested slot, then writes the
// 0x20-byte result (outer xywh + 16 state bytes).
struct FakeSlot {
    float outer[4] = {10, 20, 80, 80};
    float inner[4] = {12, 22, 76, 76};
    uint8_t state[16] = {1, 0, 0, 1};
    bool geometry = true;
    struct FakeSlotDesc* nested = nullptr;  // drawn inside this one, before its geometry
};
struct FakeSlotDesc {
    uintptr_t view = 0;  // descriptor +0: the ItemView
    FakeSlot* slot = nullptr;
    uint8_t view_bytes[0x48] = {};
};
using SlotHookFn = void* (*)(void*, void*, const void*, void*);
using FrameHookFn = void (*)(void*, void*);
SlotHookFn g_slot_hook = nullptr;
FrameHookFn g_frame_hook = nullptr;
std::vector<FakeSlotDesc*> g_frame_script;
uint8_t g_system[0x100];  // the UI system: canvas +0x30/+0x34, viewport +0x38, ppu +0x48; sys10 = system + 0x10

void* game_item_slot(void* out, void* descriptor, const void*, void*) {
    auto* d = static_cast<FakeSlotDesc*>(descriptor);
    if (d->slot->nested) {
        uint8_t inner_out[0x20];
        g_slot_hook(inner_out, d->slot->nested, nullptr, nullptr);
    }
    if (d->slot->geometry) {
        KilnRegisters r{};
        r.rax = addr(d->slot->inner);
        r.r15 = addr(d);
        if (KilnMidFn fn = mid_at("ui.values/SlotGeometry")) fn(&r, nullptr);
    }
    std::memcpy(out, d->slot->outer, 16);
    std::memcpy(static_cast<uint8_t*>(out) + 0x10, d->slot->state, 16);
    return out;
}
void game_frame(void*, void*) {
    for (FakeSlotDesc* d : g_frame_script) {
        uint8_t out[0x20];
        g_slot_hook(out, d, nullptr, nullptr);
    }
}
void ui_frame(uint64_t frame) {
    fake::screen.frame = frame;  // kiln.ui publishes its screen inside the chain
    g_frame_hook(g_system + 0x10, nullptr);
}
std::deque<FakeSlotDesc> g_descs;  // owns every descriptor a test made (stable addresses)
FakeSlotDesc* desc(FakeSlot* slot, uint32_t page, uint8_t index, uint32_t item, uint32_t pide) {
    auto* d = &g_descs.emplace_back();
    d->slot = slot;
    fill_view_bytes(d->view_bytes, page, index, item, item ? 1 : 0, pide, 0, 0);
    d->view = addr(d->view_bytes);
    return d;
}

FakePlay g_slots_play;
void prime_slots() {
    prime_bars();  // view constants, the UiApp chain (app -> data -> state -> play)
    set_installer(kListSlots, &kiln::uivalues::install_slots);
    fake::rows.insert("ui.screens");
    fake::symbols["ui/ItemSlot"] = 0x140c046c0ull;
    fake::symbols["ui/SlotGeometry"] = 0x140c05433ull;
    fake::symbols["ui/GameUiFrame"] = 0x140bd0f90ull;
    const std::map<std::string, int64_t> c = {
        {"ui/slot.resultBytes", 0x20}, {"ui/slot.hoverCandidate", 0x10}, {"ui/sys.frame_arg", 0x10},
        {"ui/sys.canvas_w", 0x30},     {"ui/sys.canvas_h", 0x34},        {"ui/sys.viewport", 0x38},
        {"ui/sys.px_per_unit", 0x48},  {"ui/play.models", 0x3060},       {"ui/play.modelsCount", 0x3068},
        {"ui/model.owner", 0},         {"ui/model.stride", 0x8f70},      {"ui/model.views", 0x2d8},
        {"ui/model.viewsCount", 0x2e0}};
    for (const auto& [k, v] : c) fake::constants[k] = v;
    std::memset(g_system, 0, sizeof g_system);
    put(g_system, 0x30, 1920.0f);
    put(g_system, 0x34, 1080.0f);
    put(g_system, 0x38, 0.0f);
    put(g_system, 0x3c, 0.0f);
    put(g_system, 0x40, 2560.0f);
    put(g_system, 0x44, 1440.0f);
    put(g_system, 0x48, 1.3333f);
    put(g_mem.state, 0x38, g_slots_play.at());  // the frame's play data has the inventory models
    g_slots_play.set_models({{77, {{0x5000, 0, 0xAA, 1, 900}, {0x5001, 0, 0, 0, 0}}}, {99, {{0x6000, 0, 0xBB, 1, 0}}}});
    fake::client = ClientNow{77, 5, 100};
    g_frame_script.clear();
}
bool arm_slots() {
    for (const auto& r : fake::raws) {
        if (r.label == "ui.values/ItemSlot") {
            g_slot_hook = reinterpret_cast<SlotHookFn>(r.fn);
            *r.original = reinterpret_cast<void*>(&game_item_slot);
        }
        if (r.label == "ui.values/GameUiFrame") {
            g_frame_hook = reinterpret_cast<FrameHookFn>(r.fn);
            *r.original = reinterpret_cast<void*>(&game_frame);
        }
    }
    return g_slot_hook && g_frame_hook;
}
struct SlotsBuffer {
    KilnUiSlotsFrame frame{};
    std::vector<KilnUiVisibleSlot> slots;
    uint32_t required = 0;
    KilnResult rc = KILN_ERR_INTERNAL;
};
SlotsBuffer copy(KilnOwner o, uint32_t capacity, uint64_t session = 0, uint64_t frame = 0) {
    SlotsBuffer b;
    b.frame.size = sizeof b.frame;
    b.slots.assign(capacity + 1, KilnUiVisibleSlot{});
    for (auto& s : b.slots) {
        std::memset(&s, 0xAB, sizeof s);  // a record that was not written keeps this
        s.size = sizeof s;
    }
    b.rc = T()->copy_slots(o, session, frame, &b.frame, b.slots.data(), capacity, &b.required);
    return b;
}
struct SlotsSeen {
    std::vector<KilnUiSlotsFrame> frames;
    std::vector<uint32_t> counts;
};
void KILN_CALL on_slots(const KilnUiSlotsFrame* f, const KilnUiVisibleSlot*, uint32_t n, void* user) {
    auto* s = static_cast<SlotsSeen*>(user);
    s->frames.push_back(*f);
    s->counts.push_back(n);
}

void slots_basics() {
    prime_slots();
    const KilnOwner o = fake::owner("mod:slots");
    uint32_t req = 0;
    KilnUiSlotsFrame fr{};
    fr.size = sizeof fr;
    fake::rows.erase("ui.slots.inspect");  // held by the client gate
    CHECK(T()->copy_slots(o, 0, 0, &fr, nullptr, 0, &req) == KILN_ERR_UNAVAILABLE && fake::raws.empty(),
          "slots: ui.slots.inspect held -> UNAVAILABLE, nothing hooked");
    fake::rows.insert("ui.slots.inspect");
    CHECK(T()->copy_slots(o, 0, 0, &fr, nullptr, 0, &req) == KILN_OK && fr.ok == 0 && fr.reason == KUI_NO_FRAME,
          "slots: before the first frame -> KUI_NO_FRAME");
    CHECK(fake::mids.size() == 1 && fake::raws.size() == 2 && arm_slots() && fake::raws[1].priority == 100 &&
              fake::raws[0].priority == 0,
          "install: the geometry mid, the ItemSlot link and the frame link at +100 (outside kiln.ui's)");
    SlotsSeen seen;
    auto r = reg_record();
    CHECK(T()->subscribe_slots(o, &on_slots, &seen, &r) == KILN_OK && r.ok == 1, "slots: subscribe_slots registered");
    FakeSlot a, b, c;
    b.outer[2] = 0;    // zero width: no outer rect
    c.inner[1] = NAN;  // non-finite: no inner rect
    c.geometry = true;
    g_frame_script = {desc(&a, 0x5000, 0, 0xAA, 900), desc(&b, 0x5001, 3, 0, 0), desc(&c, 0x6000, 0, 0xBB, 0)};
    ui_frame(700);
    auto got = copy(o, 8);
    const KilnUiVisibleSlot* s = got.slots.data();
    CHECK(got.rc == KILN_OK && got.frame.ok == 1 && got.frame.count == 3 && got.required == 3 &&
              got.frame.frame == 700 && got.frame.session == 5 && got.frame.local_player == 77 &&
              got.frame.capture_ms == fake::ticks && got.frame.transform_flags == KUI_VIEWPORT &&
              got.frame.canvas_width == 1920.0f && got.frame.viewport_px.width == 2560.0f,
          "copy: a whole completed frame, its metadata and the viewport transform (no pixel mapping before 05a-c5)");
    CHECK(s[0].occurrence == 0 && s[0].inventory_page == 0x5000 && s[0].item == 0xAA && s[0].client_entity == 900 &&
              s[0].outer_ui.width == 80 && s[0].inner_ui.x == 12 && s[0].native_state[0] == 1 &&
              s[0].available == (KUI_OUTER_RECT | KUI_INNER_RECT | KUI_NATIVE_STATE | KUI_VIEWPORT) &&
              s[0].size == sizeof(KilnUiVisibleSlot),
          "copy: the slot record: identity, outer and inner rectangles, the state bytes");
    CHECK(!(s[1].available & KUI_OUTER_RECT) && (s[1].available & KUI_INNER_RECT) && s[1].slot_index == 3 &&
              s[1].item == 0 && !(s[2].available & KUI_INNER_RECT) && (s[2].available & KUI_OUTER_RECT) &&
              s[2].item == 0xBB,
          "rects: zero width clears the outer bit, a NaN the inner bit; identity and the other rect stay (empty "
          "slots too)");
    CHECK(got.slots[3].size == sizeof(KilnUiVisibleSlot) && got.slots[3].occurrence == 0xABABABAB,
          "copy: nothing written past the frame's count");
    CHECK(seen.counts == (std::vector<uint32_t>{3}) && seen.frames[0].frame == 700,
          "subscribe: one publication after the frame");

    // a small buffer: metadata and required, no record
    auto small = copy(o, 2);
    CHECK(small.rc == KILN_ERR_BUFFER_TOO_SMALL && small.required == 3 && small.frame.count == 3 &&
              small.frame.frame == 700 && small.slots[0].occurrence == 0xABABABAB &&
              small.slots[1].occurrence == 0xABABABAB,
          "copy: capacity 2 for 3 -> BUFFER_TOO_SMALL with the metadata and required 3, no record written");
    // stale ids: the last two frames are kept
    ui_frame(701);
    CHECK(copy(o, 8, 5, 700).frame.ok == 1, "copy: the previous frame by its ids");
    ui_frame(702);
    auto stale = copy(o, 8, 5, 700);
    CHECK(stale.rc == KILN_OK && stale.frame.ok == 0 && stale.frame.reason == KUI_STALE_FRAME &&
              stale.slots[0].occurrence == 0xABABABAB,
          "copy: a frame older than the last two -> KUI_STALE_FRAME, no record");
    CHECK(copy(o, 8, 4, 702).frame.reason == KUI_STALE_FRAME,
          "copy: the right frame number of another session is stale");
}

void slots_budget_and_nesting() {
    prime_slots();
    const KilnOwner o = fake::owner("mod:slots");
    uint32_t req = 0;
    KilnUiSlotsFrame fr{};
    fr.size = sizeof fr;
    T()->copy_slots(o, 0, 0, &fr, nullptr, 0, &req);
    arm_slots();
    SlotsSeen seen;
    auto r = reg_record();
    T()->subscribe_slots(o, &on_slots, &seen, &r);
    FakeSlot one;
    auto* d = desc(&one, 0x5000, 0, 0xAA, 900);
    g_frame_script.assign(4097, d);
    ui_frame(800);
    auto over = copy(o, 8);
    CHECK(over.rc == KILN_OK && over.frame.ok == 0 && over.frame.reason == KUI_FRAME_BUDGET && over.frame.count == 0 &&
              over.required == 0 && over.slots[0].occurrence == 0xABABABAB && seen.counts.back() == 0,
          "budget: 4097 slot draws -> KUI_FRAME_BUDGET, no partial list (subscriber gets 0)");
    g_frame_script.assign(4096, d);
    ui_frame(801);
    CHECK(copy(o, 4096).frame.count == 4096, "budget: 4096 draws are a whole frame");
    // nested: the outer slot's geometry comes after the inner slot was drawn
    FakeSlot outer, inner;
    outer.inner[0] = 111;
    inner.inner[0] = 222;
    auto* in = desc(&inner, 0x5000, 1, 0xAB, 901);
    outer.nested = in;
    g_frame_script = {desc(&outer, 0x5000, 0, 0xAA, 900)};
    ui_frame(802);
    auto nest = copy(o, 4);
    CHECK(nest.frame.count == 2 && nest.slots[0].item == 0xAB && nest.slots[0].inner_ui.x == 222 &&
              nest.slots[1].item == 0xAA && nest.slots[1].inner_ui.x == 111 &&
              (nest.slots[1].available & KUI_INNER_RECT),
          "nesting: a slot drawn inside another gets its own geometry; the outer one keeps its own after it");
}

void slots_holders() {
    prime_slots();
    const KilnOwner o = fake::owner("mod:holders");
    uint32_t req = 0;
    KilnUiSlotsFrame fr{};
    fr.size = sizeof fr;
    T()->copy_slots(o, 0, 0, &fr, nullptr, 0, &req);
    arm_slots();
    FakeSlot a;
    g_frame_script = {desc(&a, 0x5000, 0, 0xAA, 900), desc(&a, 0x5001, 2, 0, 0), desc(&a, 0x6000, 0, 0xBB, 0),
                      desc(&a, 0x7000, 0, 0xCC, 0)};
    ui_frame(900);
    auto f1 = copy(o, 8);
    const auto* s = f1.slots.data();
    CHECK(s[0].holder == 77 && s[0].page_ordinal == 0 && s[1].holder == 77 && s[1].page_ordinal == 1 &&
              s[0].holder_flags == (KUI_HOLDER_KNOWN | KUI_HOLDER_ORDINAL | KUI_HOLDER_LOCAL_PLAYER) &&
              s[2].holder == 99 && !(s[2].holder_flags & KUI_HOLDER_LOCAL_PLAYER) && s[3].holder_flags == 0 &&
              s[3].holder == 0 && s[0].holder_serial != 0 && s[0].holder_serial == s[1].holder_serial &&
              s[2].holder_serial != s[0].holder_serial,
          "holder: the page's model owner and ordinal, the local player flagged, an unknown page without holder");
    const uint64_t own = s[0].holder_serial, chest = s[2].holder_serial;
    ui_frame(901);
    auto f2 = copy(o, 8);
    CHECK(f2.slots[0].holder_serial == own && f2.slots[2].holder_serial == chest,
          "holder: the serials stay while the holders stay");
    g_slots_play.set_models({{77, {{0x5000, 0, 0xAA, 1, 900}, {0x5001, 0, 0, 0, 0}}}});  // the chest closed
    ui_frame(902);
    g_slots_play.set_models({{77, {{0x5000, 0, 0xAA, 1, 900}, {0x5001, 0, 0, 0, 0}}}, {99, {{0x6000, 0, 0xBB, 1, 0}}}});
    ui_frame(903);
    auto f3 = copy(o, 8);
    CHECK(f3.slots[0].holder_serial == own && f3.slots[2].holder == 99 && f3.slots[2].holder_serial != chest &&
              f3.slots[2].holder_serial > own,
          "holder: a holder back after a frame without it gets a new serial (never reused)");
    fake::client.session = 6;
    ui_frame(904);
    CHECK(copy(o, 8).slots[0].holder_serial != own, "holder: a new client session starts new serials");
}

}  // namespace

int main() {
    fake::install();
    table_and_arguments();
    quota();
    order_and_queued_changes();
    faults();
    text_store();
    text_rate_and_budgets();
    text_through_providers();
    text_latch();
    bars_install_and_view();
    bars_synthetic();
    bars_chain_writes();
    bars_slot_zero();
    bars_labels();
    bars_kinds_alone();
    update_install();
    update_hud();
    update_entities();
    update_recursion();
    update_held();
    update_stage();
    changes_basics();
    changes_reset_and_hud();
    changes_eviction_and_faults();
    changes_from_the_bars();
    slots_basics();
    slots_budget_and_nesting();
    slots_holders();
    std::printf(g_fail ? "%d FAILED\n" : "all passed\n", g_fail);
    return g_fail ? 1 : 0;
}
