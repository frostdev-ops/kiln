// SPDX-License-Identifier: MIT
// Host tests of kiln.ui.values (src/services/ui_values*.cpp, abi/services/kiln_ui_values.h): the registry (quotas,
// order, queued add/remove, retired owners, faults), the text store (budgets, rate, UTF-8, dedupe, lifetime) and the
// value chain. The service's environment (owners, kiln.caps, kiln.client, kiln.hooks, game memory) is the fake below;
// "game memory" is this process's memory. Run by test-ui-values.sh (ASan/UBSan, host compiler, no Wine).
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "../src/services/ui_values.cpp"
#include "../src/services/ui_values_text.cpp"

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
std::set<std::string> rows;           // rows kiln.caps shows available
std::vector<std::string> rows_off;    // row_off reports
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
std::set<uintptr_t> unreadable;       // addresses whose reads fail (a guarded read of unmapped memory)
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
    e.hook_raw = [](uintptr_t t, const char* label, void* fn, void** original) {
        if (hook_result == KILN_OK) raws.push_back({t, label, fn, original});
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
    e.log = [](int, const char* text) { logs.emplace_back(text); };
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
    int throws = 0;            // the next n calls throw
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
    KilnUiBarValue seen{};         // the input of the last call
    KilnUiItemView view{};
    KilnHandle unsubscribe = 0;    // removed during the call
    bool subscribe_more = false;   // registers another provider during the call
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
    CHECK(T()->copy_slots(o, 0, 0, &fr, nullptr, 0, &req) == KILN_ERR_UNAVAILABLE &&
              T()->copy_slots(o, 0, 0, nullptr, nullptr, 0, &req) == KILN_ERR_INVALID_ARGUMENT,
          "copy_slots: arguments checked, UNAVAILABLE until the slot collector exists");
}

void quota() {
    fake::reset();
    const KilnOwner a = fake::owner("mod:greedy");
    std::vector<KilnHandle> hs;
    for (int i = 0; i < 128; ++i) {
        auto r = reg_record();
        const KilnResult rc = (i % 2) ? T()->subscribe(a, KUI_SLOT, 0, &pass, nullptr, &r)
                                      : T()->tooltip_lines(a, 0, [](const KilnUiProviderContext*, const KilnUiItemView*,
                                                                    KilnUiLineWriter*, void*) -> KilnResult { return KILN_OK; },
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
    CHECK(T()->subscribe(reload, KUI_DETAIL, 0, &pass, nullptr, &r) == KILN_OK && r.ok == 1 &&
              active("mod:greedy") == 1,
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
          "(%zu calls)", g_calls.order.size());
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
    CHECK(g_calls.order.empty() && !any(KUI_DETAIL), "revoke: a retired owner's providers are gone at the next dispatch");
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
          "+ two memberships (%llu)", (unsigned long long)text::usage_all().charged);
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
    CHECK(refused == 7, "text: invalid UTF-8 (bad byte, overlong, surrogate, above U+10FFFF, cut, lone continuation) "
                        "-> INVALID_ARGUMENT (%d of 7)", refused);
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
    CHECK(text::game_text(0, &gt) && gt.n == 0 && gt.p && gt.fnv == 0xcbf29ce484222325ull &&
              text::game_text(a, &gt) && gt.n == 4 && std::memcmp(gt.p, "Fuel", 4) == 0 && gt.p[4] == 0 &&
              gt.fnv == text::fnv64("Fuel", 4),
          "text: GameText tuples point at permanent NUL-terminated bytes with the signed-byte FNV");
    CHECK(text::fnv64("\xc3\x9c", 2) != 0xcbf29ce484222325ull &&
              text::fnv64("\xc3\x9c", 2) == ((((0xcbf29ce484222325ull ^ 0xffffffffffffffc3ull) * 0x100000001b3ull) ^
                                             0xffffffffffffff9cull) * 0x100000001b3ull),
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
    CHECK(s4.id == s.id && text::usage("mod:labels").strings == 1 &&
              text::usage("mod:labels").charged == 7 + 65 + 16,
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
    std::printf(g_fail ? "%d FAILED\n" : "all passed\n", g_fail);
    return g_fail ? 1 : 0;
}
