// SPDX-License-Identifier: MIT
// kiln.content registry stage: the limits registry's back-end (limits.h).
#include "limits.h"

#include <atomic>
#include <cmath>
#include <cstring>
#include <map>
#include <mutex>
#include <set>

#include "compose.h"

namespace kc::registry::limits {

namespace {

std::mutex g_mu;  // Env, the taken values, the probes; never taken by a hook
Env g_env;
// what the hooks read at each call (lock-free): the answer, 0 = the game's own
std::atomic<int64_t> a_cap{0}, a_game_cap{0}, a_table{0};
std::atomic<uint32_t> a_gift_bits{0};
std::atomic<bool> a_gift_on{false};  // the gift getter answers a_gift_bits (a range of 0 m is a value, not "the game's own")
std::function<bool(uintptr_t, void*, size_t)> g_read;  // set once by bind(), read by the altar hook

struct Taken {
    bool have = false;
    double game = 0, value = 0;
};
std::map<std::string, Taken> g_taken;   // the registry's values apply() took
std::map<std::string, double> g_probe;  // the lab producer's probes (over the taken values while set)
std::set<std::string> g_hooked;         // values whose hook is in

void log(int level, const std::string& line) {
    if (g_env.log) g_env.log(level, line);
}

uint32_t bits_of(float f) {
    uint32_t b = 0;
    std::memcpy(&b, &f, 4);
    return b;
}

// The game's value of a hook value from its code (under g_mu).
bool game_value(const std::string& name, double* out, std::string* why) {
    if (name == "altarCap") {
        uintptr_t fn = 0;
        int64_t cap = 0, table = 0;
        if (!g_env.symbol || !g_env.symbol("limits/AltarLimit", &fn) || !fn) return *why = "the game's altar limit was not found on this game build", false;
        if (!g_env.constant || !g_env.constant("limits/altars.cap", &cap) || cap <= 0 || cap > 255 || !g_env.constant("limits/altars.table", &table) ||
            table <= 0 || table > 0x10000)
            return *why = "the game's altar limit has another shape on this game build", false;
        a_game_cap.store(cap), a_table.store(table);
        return *out = static_cast<double>(cap), true;
    }
    if (name == "giftRange") {
        uintptr_t fn = 0;
        if (!g_env.symbol || !g_env.symbol("limits/GiftRange", &fn) || !fn) return *why = "the game's gifting distance was not found on this game build", false;
        // the getter is `movss xmm0,[rip+d]; ret`: the literal is at fn + 8 + d
        uint8_t code[9] = {};
        if (!g_env.read || !g_env.read(fn, code, sizeof code) || code[0] != 0xF3 || code[1] != 0x0F || code[2] != 0x10 || code[3] != 0x05 || code[8] != 0xC3)
            return *why = "the game's gifting distance has another shape on this game build", false;
        int32_t d = 0;
        std::memcpy(&d, code + 4, 4);
        float squared = 0;
        if (!g_env.read(static_cast<uintptr_t>(static_cast<intptr_t>(fn) + 8 + d), &squared, 4)) return *why = "the game's gifting distance cannot be read", false;
        const auto m = gift_range_of(squared);
        if (!m) return *why = "the game's gifting distance is not a distance on this game build", false;
        return *out = *m, true;
    }
    return *why = "Kiln has no hook value " + name, false;
}

// The answers the hooks give from the taken values and the probes (under g_mu).
void publish() {
    auto value = [](const std::string& n, double* v) {
        if (const auto p = g_probe.find(n); p != g_probe.end()) return *v = p->second, true;
        const auto t = g_taken.find(n);
        if (t == g_taken.end() || !t->second.have || t->second.value == t->second.game) return false;
        return *v = t->second.value, true;
    };
    double v = 0;
    a_cap.store(value("altarCap", &v) ? static_cast<int64_t>(std::llround(v)) : 0);
    const bool gift = value("giftRange", &v);
    a_gift_bits.store(gift ? bits_of(gift_answer(v)) : 0);
    a_gift_on.store(gift);
}

bool ensure_hook(const std::string& name, std::string* why) {
    if (g_hooked.count(name)) return true;
    if (!g_env.install) return *why = "Kiln's hooks are not here", false;
    if (!g_env.install(name, why)) return false;
    g_hooked.insert(name);
    return true;
}

std::string field_of(const std::string& name) { return name == "altarCap" ? "maxAltars" : name == "giftRange" ? "giftingRange" : name; }

}  // namespace

bool known(const std::string& name) {
    for (const HookValue& v : kValues)
        if (name == v.name) return true;
    return false;
}

uint32_t altar_answer(uint32_t original, int64_t game_cap, int64_t cap, std::optional<uint32_t> value) {
    if (cap <= 0) return original;
    if (cap <= game_cap) return static_cast<uint32_t>(std::min<int64_t>(original, cap));
    if (!value) return original;
    return static_cast<uint32_t>(std::min<int64_t>(*value, cap));
}

float gift_answer(double metres) { return static_cast<float>(metres * metres); }

std::optional<double> gift_range_of(float squared) {
    if (!std::isfinite(squared) || squared <= 0) return std::nullopt;
    const double m = std::sqrt(static_cast<double>(squared));
    return std::round(m * 1e4) / 1e4;  // 2500 -> 50 exactly; a float literal's last bits are not a distance
}

void bind(Env env) {
    std::lock_guard lk(g_mu);
    g_env = std::move(env);
    g_read = g_env.read;
}

void reset() {
    std::lock_guard lk(g_mu);
    g_env = {};
    g_taken.clear(), g_probe.clear(), g_hooked.clear();
    a_cap.store(0), a_game_cap.store(0), a_table.store(0), a_gift_bits.store(0), a_gift_on.store(false);
}

bool base_value(const std::string& name, json* out, std::string* why) {
    std::lock_guard lk(g_mu);
    double v = 0;
    if (!game_value(name, &v, why)) return false;
    *out = name == "altarCap" ? json(static_cast<int64_t>(v)) : json(v);
    return true;
}

Answers answers() {
    Answers a;
    a.altar_cap = a_cap.load(), a.game_altar_cap = a_game_cap.load(), a.altar_table = a_table.load(), a.gift_answers = a_gift_on.load();
    const uint32_t b = a_gift_bits.load();
    std::memcpy(&a.gift_squared, &b, 4);
    return a;
}

size_t apply(const stage::BootResource& rb, std::vector<stage::BootOutcome>* outcomes, std::vector<std::string>* notes) {
    std::lock_guard lk(g_mu);
    size_t taken = 0;
    for (const stage::BootField& f : rb.fields) {
        stage::BootOutcome o;
        o.field = f.field;
        const std::string name = f.row && !f.row->paths.empty() ? f.row->paths[0] : std::string();
        double game = 0;
        std::string why;
        if (!known(name)) why = "Kiln has no hook for this field on this build";
        else if (!game_value(name, &game, &why)) {}
        if (!why.empty()) {
            o.why = why;
            outcomes->push_back(std::move(o));
            continue;
        }
        const Value base = name == "altarCap" ? Value::integer(static_cast<int64_t>(game)) : Value::number(game);
        const Value v = evaluate(f.rows, base, f.spec);
        if (!v.numeric() || !std::isfinite(v.as_double())) {
            o.why = "the value is not a number: " + v.text_of();
            outcomes->push_back(std::move(o));
            continue;
        }
        const bool differs = v.as_double() != game;
        if (differs && !ensure_hook(name, &why)) {
            o.why = "Kiln's hook of the game's code could not go in (" + why + ")";
            outcomes->push_back(std::move(o));
            continue;
        }
        g_taken[name] = {true, game, v.as_double()};
        o.ok = true;
        o.written = v;
        ++taken;
        notes->push_back("registry: limits." + field_of(name) + " = " + v.text_of() +
                         (differs ? " (Kiln's hook answers the game's check with it; the game's own is " + base.text_of() + ")"
                                  : " (the game's own: no hook)"));
        outcomes->push_back(std::move(o));
    }
    publish();
    return taken;
}

json state() {
    std::lock_guard lk(g_mu);
    json out = json::object();
    for (const HookValue& hv : kValues) {
        json e = {{"field", "limits." + field_of(hv.name)}, {"hooked", g_hooked.count(hv.name) != 0}};
        if (const auto t = g_taken.find(hv.name); t != g_taken.end() && t->second.have) e["game"] = t->second.game, e["value"] = t->second.value;
        out[hv.name] = std::move(e);
    }
    return out;
}

bool probe_set(const std::string& name, double value, std::string* why) {
    std::lock_guard lk(g_mu);
    double game = 0;
    if (!known(name) || !game_value(name, &game, why)) return false;
    if (!ensure_hook(name, why)) return false;
    g_probe[name] = value;
    publish();
    return true;
}

void probe_clear(const std::string& name) {
    std::lock_guard lk(g_mu);
    g_probe.erase(name);
    publish();
}

std::optional<double> game_answer(const std::string& name, uint32_t table_value, std::string* why) {
    uintptr_t fn = 0;
    int64_t off = 0;
    {
        std::lock_guard lk(g_mu);
        const char* sym = name == "altarCap" ? "limits/AltarLimit" : name == "giftRange" ? "limits/GiftRange" : nullptr;
        if (!sym || !g_env.symbol || !g_env.symbol(sym, &fn) || !fn) return *why = "the game's function was not found on this game build", std::nullopt;
        if (name == "altarCap" && (!g_env.constant || !g_env.constant("limits/altars.table", &off))) off = 0;
    }
    if (name == "giftRange") {
        const float squared = reinterpret_cast<float (*)()>(fn)();
        const auto m = gift_range_of(squared);
        if (!m) return *why = "the game's gifting distance is not a distance", std::nullopt;
        return *m;
    }
    // a scratch balancing table as the game lays it out: the registry's table pointer at +0x20, the table's per-level byte list
    // at limits/altars.table (a self-relative list header, then 32 levels each holding table_value)
    if (off <= 0 || off > 0x10000) return *why = "the game's altar limit has another shape on this game build", std::nullopt;
    std::vector<uint8_t> table(static_cast<size_t>(off) + 8 + 32, 0);
    const int32_t rel = 8;
    const uint32_t count = 32;
    std::memcpy(table.data() + off, &rel, 4);
    std::memcpy(table.data() + off + 4, &count, 4);
    std::memset(table.data() + off + 8, static_cast<int>(std::min<uint32_t>(table_value, 255)), 32);
    uint8_t registry[0x40] = {};
    const uintptr_t t = reinterpret_cast<uintptr_t>(table.data());
    std::memcpy(registry + 0x20, &t, sizeof t);
    const uint32_t answer = reinterpret_cast<uint32_t (*)(void*, uint32_t)>(fn)(registry, 0);
    return static_cast<double>(answer);
}

// ---- the hooks (kiln-content.dll installs them through Env.install; limits_hooks.cpp) ----
std::optional<uint32_t> table_value_at(void* registry, uint32_t level) {
    if (!g_read || !registry) return std::nullopt;
    uintptr_t table = 0;
    if (!g_read(reinterpret_cast<uintptr_t>(registry) + 0x20, &table, sizeof table) || !table) return std::nullopt;
    const uintptr_t blob = table + static_cast<uintptr_t>(a_table.load());
    int32_t rel = 0;
    uint32_t count = 0;
    if (!g_read(blob, &rel, 4) || !g_read(blob + 4, &count, 4) || rel == 0 || level >= count) return std::nullopt;
    uint8_t v = 0;
    if (!g_read(static_cast<uintptr_t>(static_cast<intptr_t>(blob) + rel) + level, &v, 1)) return std::nullopt;
    return v;
}

uint32_t altar_hook_answer(void* registry, uint32_t level, uint32_t original) {
    const int64_t cap = a_cap.load(std::memory_order_relaxed);
    if (cap <= 0) return original;
    const int64_t game = a_game_cap.load(std::memory_order_relaxed);
    return altar_answer(original, game, cap, cap > game ? table_value_at(registry, level) : std::nullopt);
}

bool gift_hook_answer(float* out) {
    if (!a_gift_on.load(std::memory_order_acquire)) return false;
    const uint32_t b = a_gift_bits.load(std::memory_order_relaxed);
    std::memcpy(out, &b, 4);
    return true;
}

}  // namespace kc::registry::limits
