// SPDX-License-Identifier: MIT
// kiln.content: the game's own texts (text() / text_info(), the row content.text; since 0.2.0-dev.29.18), on the host:
// newcontent.cpp's loca hook, nc_text and nc_text_info over a fake game lookup (two language tables), a fake LocaPool
// whose language sits at +0xd8, a fake kiln.hooks / kiln.caps / kiln.ui and a settable thread id.
//  - a dedicated server: the row is unavailable (wrong-side) and text() says unavailable
//  - a build without the hook: unavailable, with why
//  - a client before the game looked a text up: pending; text() unavailable, never a crash
//  - the first lookup the game makes: the pool is kept, the row turns available (once)
//  - the main thread (kiln.ui's UI thread in the menu, or the client world's thread): the game's text in the pool's
//    language; a language change applies at the next call (nothing cached); Kiln's own tags answer per language as the
//    UI sees them; an unknown id and id 0 are not found; the English table still loading is unavailable
//  - another thread: wrong thread, and the game's lookup is not called
//  - the text is copied out (a buffer too small says how long it is); text_info names the language
// Each case runs in a child process: the hook, the pool and the texts live as long as the process, as in the game.
#include <windows.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

// What newcontent.cpp takes from <windows.h> and <intrin.h> beyond host/windows.h (as itemfind_test.cpp).
inline unsigned long long GetTickCount64() { return 1000; }
inline unsigned long GetExceptionCode() { return 0; }
#define EXCEPTION_EXECUTE_HANDLER 1
#define __except(x) catch (...)
#define __declspec(x)
inline long long _InterlockedOr64(volatile long long* p, long long v) {
    const long long old = *p;
    *p = old | v;
    return old;
}

#include "../newcontent.cpp"

// ============================================================================================= the fake game =====
namespace fake {
bool client = true;
bool client_world_thread = false;  // on_world_thread(KILN_WORLD_CLIENT) for the calling thread
unsigned long ui_thread = 7;       // kiln.ui's screen().ui_thread (0: kiln.ui does not know it)
bool ui_loaded = true;
bool loading = false;              // the game's English table is not loaded yet (error 3)
int lookups = 0;                   // calls that reached the game's own lookup
std::vector<std::string> lines;
struct Row { std::string name; uint32_t state, reason; std::string text; };
std::vector<Row> rows;             // kiln.caps reports, in order

alignas(16) uint8_t pool[0x200];
void set_language(uint32_t l) { std::memcpy(pool + 0xd8, &l, 4); }

constexpr uint32_t kManaCost = 0xbe5b2600, kRadius = 0xc9d82dea;
const std::map<uint32_t, std::map<uint32_t, std::string>> tables = {
    {1, {{kManaCost, "Mana Cost"}, {kRadius, "Radius"}}},
    {3, {{kManaCost, "Manakosten"}}},  // German has no Radius: the game falls back to English
};

kc::LocaResult* game_lookup(kc::LocaResult* out, void* p, uint32_t id) {
    ++lookups;
    std::memset(out, 0, sizeof *out);
    if (!id) { out->error = 0x12; return out; }
    if (loading) { out->error = 3; return out; }
    uint32_t lang = 0;
    std::memcpy(&lang, static_cast<uint8_t*>(p) + 0xd8, 4);
    for (uint32_t l : {lang, 1u})
        if (auto t = tables.find(l); t != tables.end())
            if (auto it = t->second.find(id); it != t->second.end()) {
                out->text = it->second.c_str();
                out->len = it->second.size();
                return out;
            }
    out->error = 0x1f;
    return out;
}
using Lookup = kc::LocaResult* (*)(kc::LocaResult*, void*, uint32_t);
Lookup detour = nullptr;
kc::LocaResult* entry(kc::LocaResult* out, void* p, uint32_t id) { return detour ? detour(out, p, id) : game_lookup(out, p, id); }
// The UI draws a frame: it looks a text up through the (hooked) entry.
std::string ui_draws(uint32_t id) {
    kc::LocaResult r{};
    entry(&r, pool, id);
    return r.error ? std::string() : std::string(r.text, r.len);
}

KilnResult KILN_CALL raw_detour(KilnOwner, uintptr_t target, const KilnHookOrder*, void* fn, void** original, KilnHandle* out) {
    *original = reinterpret_cast<void*>(&game_lookup);
    if (target == reinterpret_cast<uintptr_t>(&entry)) detour = reinterpret_cast<Lookup>(fn);
    *out = 1;
    return KILN_OK;
}
KilnResult KILN_CALL revoke(KilnHandle) { return KILN_OK; }
uintptr_t KILN_CALL caller(KilnHandle) { return 0; }
KilnHooksApi hooks = [] {
    KilnHooksApi h{};
    h.header.size = sizeof h;
    h.header.version = 2;
    h.raw_detour = &raw_detour;
    h.revoke = &revoke;
    h.caller = &caller;
    return h;
}();

KilnResult KILN_CALL report(KilnOwner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) {
    rows.push_back({std::string(name.ptr, name.len), state, reason, std::string(text.ptr, text.len)});
    return KILN_OK;
}
KilnCapsApi caps = [] {
    KilnCapsApi c{};
    c.header.size = sizeof c;
    c.header.version = 1;
    c.report = &report;
    return c;
}();

KilnResult KILN_CALL screen(KilnUiScreen* s) {
    const uint32_t size = s->size;
    std::memset(s, 0, size);
    s->size = size;
    s->ui_thread = static_cast<uint32_t>(ui_thread);
    return KILN_OK;
}
KilnUiApi ui = [] {
    KilnUiApi u{};
    u.header.size = sizeof u;
    u.header.version = KILN_UI_VERSION;
    u.screen = &screen;
    return u;
}();

const void* KILN_CALL service(KilnStr name, uint32_t) {
    const std::string n(name.ptr, name.len);
    if (n == KILN_HOOKS_SERVICE) return &hooks;
    if (n == KILN_CAPS_SERVICE) return &caps;
    if (n == KILN_UI_SERVICE && ui_loaded) return &ui;
    return nullptr;
}
uint32_t KILN_CALL side() { return client ? KILN_SIDE_CLIENT : KILN_SIDE_SERVER; }
bool KILN_CALL on_world_thread(uint32_t kind) { return kind == KILN_WORLD_CLIENT && client_world_thread; }
uintptr_t KILN_CALL world(uint32_t) { return 0; }

KilnApi api = [] {
    KilnApi a{};
    a.size = sizeof a;
    a.service = &service;
    a.side = &side;
    a.world = &world;
    a.on_world_thread = &on_world_thread;
    return a;
}();

const Row* last_row() {
    for (auto it = rows.rbegin(); it != rows.rend(); ++it)
        if (it->name == "content.text") return &*it;
    return nullptr;
}
int row_count(uint32_t state) {
    int n = 0;
    for (const Row& r : rows) n += r.name == "content.text" && r.state == state;
    return n;
}
}  // namespace fake

// ============================================================================================= the service's other files
namespace kc {
const KilnApi* g_api = &fake::api;
KilnOwner g_owner = 1;
Engine E;
void logf(int, const char* fmt, ...) {
    char b[2048];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    fake::lines.emplace_back(b);
    if (std::getenv("TEXT_TEST_LOG")) std::printf("  log: %s\n", b);
}
uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; i++) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 16777619u;
    return h;
}
std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
bool guid_parse(const std::string& s, uint8_t out[16]) {
    std::string hex;
    for (char c : s)
        if (c != '-') hex += c;
    if (hex.size() != 32) return false;
    for (int i = 0; i < 16; ++i) out[i] = static_cast<uint8_t>(std::strtoul(hex.substr(i * 2, 2).c_str(), nullptr, 16));
    return true;
}
std::string guid_text(const uint8_t*) { return "00000000-0000-0000-0000-000000000000"; }
bool rd(uintptr_t p, void* out, size_t n) {
    if (!p) return false;
    std::memcpy(out, reinterpret_cast<const void*>(p), n);
    return true;
}
void* near_alloc(uintptr_t, size_t n) { return std::calloc(1, n ? n : 1); }
void StoreMutex::lock() { m_.lock(); }
void StoreMutex::unlock() { m_.unlock(); }
bool StoreMutex::try_lock() { return m_.try_lock(); }
StoreMutex g_store;
StoreMutex& store_mutex() { return g_store; }
PhaseTimer::PhaseTimer(Phase) {}
PhaseTimer::~PhaseTimer() {}
std::string owner_label(KilnOwner o) { return o == 1 ? "kiln" : "mod test-items"; }
std::string data_dir() { return "D:\\kiln\\data"; }
bool path_exists(const std::string&) {
    return false;
}
const std::string& current_pack_dir() {
    static const std::string none;
    return none;
}
uintptr_t active_world() { return 0; }
uint32_t active_kind() { return KILN_WORLD_CLIENT; }
// Reached from newcontent.cpp's other paths (the clone builder, recipes, loot, the store); never called by these cases.
DropLayout DL;
void boot_late_check() {}
void barber_sync(const std::vector<BarberItem>&) {}
bool build_schema(const char*, const std::vector<std::string>&, Schema*, std::string*) { return false; }
int64_t field_offset(const char*, const char*) { return -1; }
uint32_t type_size(const char*) { return 0; }
bool enum_values(const char* type, std::vector<std::pair<std::string, int64_t>>* out) {
    if (std::string(type) != "keen::LanguageId") return false;
    *out = {{"None", 0}, {"En_Us", 1}, {"En_Gb", 2}, {"De_De", 3}, {"Count", 4}};
    return true;
}
bool self_relative_fields(const char*, std::vector<uint32_t>*, std::string*) { return false; }
bool encode(const Field&, const KilnContentValue&, uint8_t*, std::string*) { return false; }
const Field* Schema::find(const std::string&) const { return nullptr; }
const Entry* Registry::lookup(const std::string&) const { return nullptr; }
Registry& reg(uint32_t) {
    static Registry r;
    return r;
}
void drops_apply_rules(const Entry&) {}
void kitbash(const Schema&, const std::string&, const nlohmann::ordered_json&, const ItemFind&, const std::vector<uintptr_t>&,
             KitbashOut*, uintptr_t) {}
void set_kitbash_mod(const std::string*) {}
uint32_t map_count(uintptr_t) { return 0; }
bool map_read(uintptr_t, std::vector<uint32_t>*, std::vector<uint64_t>*) { return false; }
std::string near_alloc_report() { return "-"; }
std::string read_blob_string(uintptr_t) { return ""; }
uint64_t next_code_seq() { return 1; }
bool store_put(KilnOwner, uint64_t, uint32_t, uint32_t, int32_t, int32_t, uintptr_t, uint8_t, const uint8_t*, const std::string&) {
    return false;
}
bool store_drop(KilnOwner, uintptr_t) { return false; }
bool store_original(uintptr_t, uint8_t, uint8_t*) { return false; }
void store_originals(uintptr_t, size_t, uint8_t*) {}
size_t store_flush(std::string*) { return 0; }
}  // namespace kc

// ============================================================================================= the cases =========
namespace {

int failures = 0;
void check(bool ok, const std::string& what) {
    std::printf("  %s %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

// What nc_boot does on a client build with the loca hooks (content.loca, content.loca_language).
void client_boot() {
    kc::g_loca_lang_off = 0xd8;
    kc::g_loca_hook.install(&fake::api, 1, reinterpret_cast<uintptr_t>(&fake::entry), reinterpret_cast<void*>(&kc::hk_loca),
                            "content/LocaLookup");
    kc::N.loca = static_cast<bool>(kc::g_loca_hook);
    kc::g_text_on = kc::N.loca;
    kc::nc_text_boot();
}

KilnResult text(uint32_t id, std::string* out) { return kc::nc_text(id, out); }

std::string api_text(uint32_t id, size_t cap, KilnResult* rc, size_t* len) {
    std::vector<char> buf(cap ? cap : 1);
    *rc = kc::nc_text_api(id, cap ? buf.data() : nullptr, cap, len);
    return cap ? std::string(buf.data()) : std::string();
}

KilnContentTextInfo info() {
    KilnContentTextInfo i{};
    i.size = sizeof i;
    kc::nc_text_info(&i);
    return i;
}

void case_server() {
    fake::client = false;
    kc::nc_text_boot();  // nc_boot left the defaults (no content.loca on a server)
    const fake::Row* r = fake::last_row();
    check(r && r->state == KILN_CAP_UNAVAILABLE && r->reason == KILN_CAP_REASON_WRONG_SIDE,
          "a dedicated server: content.text is unavailable, wrong-side: " + (r ? r->text : std::string("(no row)")));
    std::string s = "x";
    check(text(fake::kManaCost, &s) == KILN_ERR_UNAVAILABLE && s.empty(), "text() answers unavailable, with no text");
    check(info().state == KILN_CAP_UNAVAILABLE && info().reason == KILN_CAP_REASON_WRONG_SIDE, "text_info says the same");
}

void case_no_hook() {
    kc::g_text_reason = KILN_CAP_REASON_SYMBOL_MISSING;  // as nc_boot sets it when content.loca did not resolve
    kc::g_text_off = "needs content.loca: the game's text lookup did not resolve on this build";
    kc::nc_text_boot();
    const fake::Row* r = fake::last_row();
    check(r && r->state == KILN_CAP_UNAVAILABLE && r->reason == KILN_CAP_REASON_SYMBOL_MISSING && r->text.find("content.loca") != std::string::npos,
          "a client build without the lookup: unavailable, symbol-missing, naming content.loca");
    std::string s;
    check(text(fake::kManaCost, &s) == KILN_ERR_UNAVAILABLE, "text() answers unavailable");
}

void case_pending_then_available() {
    client_boot();
    const fake::Row* r = fake::last_row();
    check(r && r->state == KILN_CAP_PENDING && r->reason == KILN_CAP_REASON_SESSION, "a client at boot: pending until the game looks a text up");
    fake::client_world_thread = true;
    std::string s;
    check(text(fake::kManaCost, &s) == KILN_ERR_UNAVAILABLE && fake::lookups == 0, "before that, text() is unavailable and calls nothing");
    check(info().state == KILN_CAP_PENDING, "text_info: pending");
    fake::set_language(1);
    check(fake::ui_draws(fake::kManaCost) == "Mana Cost", "the game's UI draws its own text through the hook");
    check(fake::row_count(KILN_CAP_AVAILABLE) == 1, "the first lookup turns the row available");
    fake::ui_draws(fake::kRadius);
    check(fake::row_count(KILN_CAP_AVAILABLE) == 1, "once: later lookups report nothing");
    check(info().state == KILN_CAP_AVAILABLE, "text_info: available");
}

void case_texts() {
    KilnResult rc;
    client_boot();
    fake::set_language(1);
    fake::ui_draws(fake::kManaCost);
    // the menu: no client world steps; kiln.ui knows the UI thread
    host_thread_id = 7;
    std::string s;
    rc = text(fake::kManaCost, &s);
    check(rc == KILN_OK && s == "Mana Cost", "the main thread (kiln.ui's UI thread): \"" + s + "\"");
    fake::set_language(3);
    rc = text(fake::kManaCost, &s);
    check(rc == KILN_OK && s == "Manakosten", "the language changed: the next call is German, nothing cached: \"" + s + "\"");
    rc = text(fake::kRadius, &s);
    check(rc == KILN_OK && s == "Radius", "a tag German lacks: the game's own English fallback");
    const KilnContentTextInfo i = info();
    check(i.language == 3 && std::string(i.language_key) == "de-de", "text_info names the language: " + std::string(i.language_key));
    check(text(0x12345678, &s) == KILN_ERR_NOT_FOUND && s.empty(), "an id no table has: not found");
    check(text(0, &s) == KILN_ERR_NOT_FOUND, "id 0: not found");
    // Kiln's own tag (a new item's name), per language, as the UI sees it
    const uint32_t tag = 0x0badcafe;
    kc::set_texts(tag, kc::TextSet{{"en", "Frost Edge"}, {"de", "Frostklinge"}});
    rc = text(tag, &s);
    check(rc == KILN_OK && s == "Frostklinge", "Kiln's own tag in German: \"" + s + "\"");
    check(fake::ui_draws(tag) == s, "the same text the game's UI gets for it");
    fake::set_language(1);
    rc = text(tag, &s);
    check(rc == KILN_OK && s == "Frost Edge", "and in English after a switch: \"" + s + "\"");
    // another thread: refused before the game is asked
    const int before = fake::lookups;
    host_thread_id = 9;
    check(text(fake::kManaCost, &s) == KILN_ERR_WRONG_THREAD && fake::lookups == before, "another thread: wrong thread, the game's lookup is not called");
    host_thread_id = 7;
    fake::loading = true;
    check(text(fake::kManaCost, &s) == KILN_ERR_UNAVAILABLE, "the English table still loading: unavailable");
    fake::loading = false;
}

void case_client_world_thread() {
    KilnResult rc;
    fake::ui_loaded = false;  // no kiln.ui: the client world's thread is the main thread
    client_boot();
    fake::ui_draws(fake::kManaCost);
    host_thread_id = 11;
    std::string s;
    check(text(fake::kManaCost, &s) == KILN_ERR_WRONG_THREAD, "no kiln.ui and no client world: no thread is known to be the main one");
    fake::client_world_thread = true;
    fake::set_language(1);
    rc = text(fake::kManaCost, &s);
    check(rc == KILN_OK && s == "Mana Cost", "the client world's thread is the main thread");
    fake::client_world_thread = false;
    host_thread_id = 12;
    check(text(fake::kManaCost, &s) == KILN_ERR_WRONG_THREAD, "once known, only that thread");
}

void case_copy_out() {
    client_boot();
    fake::set_language(1);
    fake::ui_draws(fake::kManaCost);
    host_thread_id = 7;
    KilnResult rc;
    size_t len = 0;
    std::string s = api_text(fake::kManaCost, 64, &rc, &len);
    check(rc == KILN_OK && s == "Mana Cost" && len == 9, "the table's text(): copied out, NUL-terminated");
    s = api_text(fake::kManaCost, 5, &rc, &len);
    check(rc == KILN_ERR_BUFFER_TOO_SMALL && len == 9 && s == "Mana", "a buffer too small: cut, and the whole length");
    api_text(fake::kManaCost, 0, &rc, &len);
    check(rc == KILN_ERR_BUFFER_TOO_SMALL && len == 9, "no buffer: the length only");
    KilnContentTextInfo i{};
    i.size = 8;
    check(kc::nc_text_info_api(&i) == KILN_ERR_INVALID_ARGUMENT, "text_info with a short struct: invalid argument");
}

}  // namespace

int main() {
    const std::pair<const char*, void (*)()> cases[] = {
        {"dedicated server", &case_server},
        {"client build without the lookup", &case_no_hook},
        {"pending, then available", &case_pending_then_available},
        {"texts on the main thread", &case_texts},
        {"the client world's thread", &case_client_world_thread},
        {"copied out", &case_copy_out},
    };
    std::string failed;
    for (const auto& [name, fn] : cases) {
        std::printf("== %s\n", name);
        std::fflush(stdout);
        const pid_t pid = fork();
        if (pid == 0) {
            fn();
            std::fflush(stdout);
            _exit(failures ? 1 : 0);
        }
        int status = 0;
        waitpid(pid, &status, 0);
        if (WIFSIGNALED(status) || WEXITSTATUS(status) != 0) {
            if (WIFSIGNALED(status)) std::printf("  FAIL the case crashed (signal %d)\n", WTERMSIG(status));
            failed += std::string(failed.empty() ? "" : ", ") + name;
        }
    }
    if (failed.empty()) std::printf("text-test: all %zu cases passed\n", std::size(cases));
    else std::printf("text-test: FAILED: %s\n", failed.c_str());
    return failed.empty() ? 0 : 1;
}
