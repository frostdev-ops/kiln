// SPDX-License-Identifier: MIT
// SET 0.2.0 interception rules on Kiln: the rule engine (rules.cpp), the catalog checks (contracts.cpp) and the real
// hook adapter (hooks.cpp) on a fake kiln.hooks table with fake game memory. The pure rule cases are ported from
// Shrouded Engine Tweaks 0.2.0 runtime/hooks_test.cpp (checkRuleOrder ... checkRuleListRace, checkCatalog),
// Copyright (c) 2026 Cacaida, MIT (licenses/ShroudedEngineTweaks-LICENSE.txt). Nothing here runs game code.
#include <bit>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <thread>

#include "../src/hooks.cpp"
#include "../src/contracts.h"

#define CHECK(c) do { if (!(c)) { std::printf("FAIL rules line %d: %s\n", __LINE__, #c); std::exit(1); } } while (0)

namespace kset::hooktest {
extern bool (*write)(uintptr_t at, const void* src, size_t n);
extern bool (*on_stack)(uintptr_t at, size_t n);
}

namespace {
struct Range { uintptr_t at; size_t size; };
std::vector<Range> noAccess, readOnly, stacks;
bool in(const std::vector<Range>& rs, uintptr_t at, size_t n) {
    for (const auto& r : rs) if (at < r.at + r.size && r.at < at + n) return true;
    return false;
}
bool inside(const std::vector<Range>& rs, uintptr_t at, size_t n) {
    for (const auto& r : rs) if (at >= r.at && n <= r.size && at - r.at <= r.size - n) return true;
    return false;
}

// ---- the fake kiln.hooks ----
KilnHooksApi hooks{};
std::map<uintptr_t, std::pair<KilnMidFn, void*>> mids;
struct RawLink { void* fn; void** original; int32_t priority; };
std::map<uintptr_t, RawLink> raws;
std::map<std::string, std::vector<uintptr_t>> scans;  // normalized pattern -> matches
std::vector<uint8_t> image(0x1000, 0xCC);
uintptr_t imageBase() { return reinterpret_cast<uintptr_t>(image.data()); }

KilnResult KILN_CALL h_read(uintptr_t at, void* dst, size_t n) {
    if (!at || in(noAccess, at, n)) return KILN_ERR_INVALID_ARGUMENT;
    std::memcpy(dst, reinterpret_cast<const void*>(at), n);
    return KILN_OK;
}
bool t_write(uintptr_t at, const void* src, size_t n) {
    if (!at || in(noAccess, at, n) || in(readOnly, at, n)) return false;
    std::memcpy(reinterpret_cast<void*>(at), src, n);
    return true;
}
bool t_on_stack(uintptr_t at, size_t n) { return inside(stacks, at, n); }
KilnResult KILN_CALL h_mid(KilnOwner, uintptr_t at, int32_t, KilnMidFn fn, void* user, KilnHandle* out) {
    mids[at] = {fn, user};
    *out = mids.size();
    return KILN_OK;
}
KilnResult KILN_CALL h_raw(KilnOwner, uintptr_t at, const KilnHookOrder* order, void* fn, void** original, KilnHandle* out) {
    raws[at] = {fn, original, order ? order->priority : 0};
    *out = 100 + raws.size();
    return KILN_OK;
}
KilnResult KILN_CALL h_scan(KilnStr pattern, uintptr_t* out, uint32_t cap, uint32_t* count) {
    auto it = scans.find(std::string(pattern.ptr, pattern.len));
    if (it == scans.end()) { *count = 0; return KILN_ERR_NOT_FOUND; }
    *count = uint32_t(it->second.size());
    for (uint32_t i = 0; i < cap && i < *count; ++i) out[i] = it->second[i];
    return *count > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}
const void* KILN_CALL service(KilnStr, uint32_t) { return &hooks; }
KilnResult KILN_CALL symbol(KilnStr, uintptr_t*) { return KILN_ERR_NOT_FOUND; }

// One call of the "game function" at fn, as kiln.hooks would run it: the entry hook, then (unless it returned early)
// the raw link if there is one, then the original. Integer arguments in a[0..7], floats given as their bits in the
// same positions (fp says which of the first four sit in XMM).
using Original = uint64_t (*)(const uint64_t* a);
struct Call {
    bool blocked = false;
    uint64_t ret = 0;
    uint64_t seen[8]{};  // what the original got
    double xmm0 = 0;
};
Original g_original = nullptr;
uint64_t g_seen[8];
uint64_t KILN_CALL xp_original(uint64_t a, uint64_t b, float c, uint64_t d, uint64_t e, uint64_t f, uint64_t g, uint64_t h) {
    const uint64_t v[8] = {a, b, std::bit_cast<uint32_t>(c), d, e, f, g, h};
    std::memcpy(g_seen, v, sizeof v);
    return g_original(v);
}
Call call(uintptr_t fn, Original original, std::array<uint64_t, 8> a, uint8_t fp = 0) {
    Call out;
    std::array<uint64_t, 9> stack{};  // [0] the return address, [1..4] home space, [5..8] arguments 5 to 8
    for (int i = 4; i < 8; ++i) stack[size_t(5 + i - 4)] = a[size_t(i)];
    stacks.push_back({reinterpret_cast<uintptr_t>(stack.data()), sizeof stack});
    KilnRegisters r{};
    uintptr_t* g[] = {&r.rcx, &r.rdx, &r.r8, &r.r9};
    KilnXmm* x[] = {&r.xmm0, &r.xmm1, &r.xmm2, &r.xmm3};
    for (int i = 0; i < 4; ++i) {
        if (fp >> i & 1) std::memcpy(x[i]->bytes, &a[size_t(i)], 8);
        else *g[i] = uintptr_t(a[size_t(i)]);
    }
    r.rsp = reinterpret_cast<uintptr_t>(stack.data());
    r.trampoline_rsp = r.rsp - 8;
    if (auto it = mids.find(fn); it != mids.end()) it->second.first(&r, it->second.second);
    if (r.trampoline_rsp == r.rsp) {
        out.blocked = true;
        out.ret = r.rax;
        std::memcpy(&out.xmm0, r.xmm0.bytes, 8);
        stacks.pop_back();
        return out;
    }
    CHECK(r.trampoline_rsp == r.rsp - 8);
    uint64_t now[8];
    for (int i = 0; i < 4; ++i) {
        if (fp >> i & 1) std::memcpy(&now[i], x[i]->bytes, 8);
        else now[i] = *g[i];
    }
    for (int i = 4; i < 8; ++i) now[i] = stack[size_t(5 + i - 4)];
    g_original = original;
    if (auto it = raws.find(fn); it != raws.end()) {
        *it->second.original = reinterpret_cast<void*>(&xp_original);
        auto link = reinterpret_cast<CombatXpFn>(it->second.fn);
        out.ret = link(now[0], now[1], std::bit_cast<float>(uint32_t(now[2])), now[3], now[4], now[5], now[6], now[7]);
        std::memcpy(out.seen, g_seen, sizeof g_seen);
    } else {
        std::memcpy(out.seen, now, sizeof now);
        out.ret = original(now);
    }
    stacks.pop_back();
    return out;
}

// ---- helpers, as in SET's hooks_test.cpp ----
rules::Loc arg(uint8_t i, uint8_t width = 4, rules::Num num = rules::Signed) { return {i, width, num}; }
rules::Change change(rules::Loc at, rules::Op op, double v) { return {at, op, {rules::Const, 0, v}}; }
rules::Cond cond(rules::Loc at, rules::Cmp cmp, double v) { return {at, cmp, {rules::Const, 0, v}}; }
std::unique_ptr<rules::Rule> rule(std::initializer_list<rules::Cond> cs, std::initializer_list<rules::Change> ch) {
    auto r = std::make_unique<rules::Rule>();
    for (const auto& c : cs) r->shape.cond[r->shape.conds++] = c;
    for (const auto& c : ch) r->shape.change[r->shape.changes++] = c;
    r->active = true;
    return r;
}
uint64_t applied(std::initializer_list<rules::Rule*> rs, rules::Frame in, int pos = 0) {
    rules::Frame out;
    std::vector<rules::Rule*> v(rs);
    CHECK(rules::apply(v.data(), int(v.size()), in, out) == -1);
    return out.a[pos];
}
rules::Loc loc(hk::Kind k, uint8_t pos) {
    rules::Loc l;
    CHECK(hk::argLoc({k, {}}, pos, l));
    return l;
}
rules::Loc fieldAt(uint8_t i, uint32_t off, bool stack, uint8_t width = 4, rules::Num num = rules::Signed) {
    rules::Loc l{i, width, num, true, off};
    l.stack = stack;
    return l;
}
int addRule(uintptr_t fn, const char* key, std::vector<hk::Arg> args, std::initializer_list<rules::Cond> cs,
            std::initializer_list<rules::Change> ch, uint32_t order = 0, std::vector<double> params = {},
            std::string* conflict = nullptr) {
    hk::RuleDecl d{key, std::move(args), rule(cs, ch)->shape, order, std::move(params), {}};
    std::string err, c;
    int id = hk::addRule(fn, d, err, c);
    if (id < 0) std::printf("addRule %s: %s\n", key, err.c_str());
    CHECK(id >= 0);
    if (conflict) *conflict = c;
    return id;
}
std::string refused(uintptr_t fn, const hk::RuleDecl& d) {
    std::string err, c;
    CHECK(hk::addRule(fn, d, err, c) < 0 && !err.empty());
    return err;
}

// Order, the original values in conditions, inactive rules, params and hit counts.
void checkRuleOrder() {
    auto half = rule({}, {change(arg(0), rules::Mul, 0.5)}), plus = rule({}, {change(arg(0), rules::Add, 20)});
    rules::Frame in{{100}};
    CHECK(applied({half.get(), plus.get()}, in) == 70 && applied({plus.get(), half.get()}, in) == 60);
    auto five = rule({cond(arg(0), rules::Eq, 100)}, {change(arg(0), rules::Set, 5)});
    auto one = rule({cond(arg(0), rules::Eq, 100)}, {change(arg(0), rules::Add, 1)});
    CHECK(applied({five.get(), one.get()}, in) == 6);  // one still saw 100
    one->active = false;
    CHECK(applied({five.get(), one.get()}, in) == 5 && one->hits == 1 && five->hits == 2);
    auto scaled = rule({}, {{arg(0), rules::Mul, {rules::Param, 2, 0}}});
    rules::setParam(*scaled, 2, 3);
    CHECK(applied({scaled.get()}, in) == 300);
    rules::setParam(*scaled, 2, -0.25);
    CHECK(applied({scaled.get()}, in) == uint32_t(-25));  // an i32 position: only the low 4 bytes change
    uint8_t slots[15], back[15];
    for (int i = 0; i < 15; ++i) slots[i] = uint8_t(14 - i);
    CHECK(rules::unpackOrder(rules::packOrder(slots, 15), back) == 15 && memcmp(slots, back, 15) == 0);
    CHECK(rules::unpackOrder(rules::packOrder(slots, 0), back) == 0);
    CHECK(rules::writesSame(half->shape, plus->shape) && !rules::writesSame(half->shape, rule({}, {})->shape));
}

// Widths and number kinds: saturation, rounding, NaN, floats keeping the register's other bits.
void checkRuleValues() {
    auto add = [](rules::Loc at, double v, uint64_t in) { auto r = rule({}, {change(at, rules::Add, v)}); return applied({r.get()}, {{in}}); };
    CHECK(add(arg(0, 1, rules::Unsigned), 300, 0xAB0A) == 0xABFF);       // u8 saturates, upper bits kept
    CHECK(add(arg(0, 1, rules::Signed), -200, 0x05) == 0x80);           // i8: -128
    CHECK(add(arg(0, 8, rules::Signed), 1e30, 0) == uint64_t(INT64_MAX));
    CHECK(add(arg(0, 8, rules::Unsigned), -5, 3) == 0);
    auto mul = rule({}, {change(arg(0), rules::Mul, 0.5)});
    CHECK(applied({mul.get()}, {{uint64_t(uint32_t(-7))}}) == uint32_t(-4));  // -3.5 rounds away from zero
    auto nan = rule({}, {change(arg(0), rules::Set, std::nan(""))});
    CHECK(applied({nan.get()}, {{77}}) == 0);
    auto f = rule({}, {change(arg(1, 4, rules::Float), rules::Mul, 2)});
    uint64_t out = applied({f.get()}, {{0, 0xDEADBEEF00000000ull | std::bit_cast<uint32_t>(1.5f)}}, 1);
    CHECK(out >> 32 == 0xDEADBEEF && std::bit_cast<float>(uint32_t(out)) == 3.0f);
    auto d = rule({}, {change(arg(4, 8, rules::Float), rules::Max, 2.5)});  // a stack position
    CHECK(std::bit_cast<double>(applied({d.get()}, {{0, 0, 0, 0, std::bit_cast<uint64_t>(9.0)}}, 4)) == 2.5);
    auto b = rule({}, {change(arg(2, 1, rules::Boolean), rules::Set, 7)});
    CHECK(applied({b.get()}, {{0, 0, 0xFF00}}, 2) == 0xFF01);
}

// Player and list conditions, fields through a pointer, and a fault that leaves nothing behind.
void checkRuleConds() {
    auto mine = rule({{arg(0), rules::Eq, {rules::Player}}}, {change(arg(1), rules::Set, 1)});
    rules::setPlayer(0);
    CHECK(applied({mine.get()}, {{0, 0}}, 1) == 0);  // no player: never matches, even an id of 0
    rules::setPlayer(42);
    CHECK(applied({mine.get()}, {{42, 0}}, 1) == 1 && applied({mine.get()}, {{43, 0}}, 1) == 0);
    rules::setPlayer(0);
    auto listed = rule({{arg(0, 4, rules::Unsigned), rules::In, {}}}, {change(arg(1), rules::Set, 1)});
    uint32_t ids[] = {1, 2, 3};
    rules::setList(*listed, ids, 3);
    CHECK(applied({listed.get()}, {{2, 0}}, 1) == 1 && applied({listed.get()}, {{4, 0}}, 1) == 0);
    struct { uint32_t a; float b; } s{9, 1.5f};
    rules::Loc fb{2, 4, rules::Float, true, 4};
    auto field = rule({cond(fb, rules::Gt, 1.0)}, {change(arg(0), rules::Set, 5)});
    CHECK(applied({field.get()}, {{0, 0, reinterpret_cast<uint64_t>(&s)}}) == 5);
    CHECK(applied({field.get()}, {{0, 0, 0}}) == 0 && field->faults == 0);  // null: no value, no fault
    static uint8_t page[64];
    noAccess.push_back({reinterpret_cast<uintptr_t>(page), sizeof page});
    auto first = rule({}, {change(arg(0), rules::Add, 1)});
    rules::Rule* rs[] = {first.get(), field.get()};
    rules::Frame in{{0, 0, reinterpret_cast<uint64_t>(page)}}, out;
    uint64_t hits = first->hits;
    CHECK(rules::apply(rs, 2, in, out) == 1 && !field->active && field->faults == 1 && first->hits == hits);
    CHECK(rules::apply(rs, 2, in, out) == -1 && out.a[0] == 1);  // the faulted rule is off; the other still runs
    noAccess.clear();
}

// Field changes: in order over staged values, only on this thread's stack when the field says so, written all or none.
void checkRuleFields() {
    struct { int32_t level; bool set; } opt{10, true};
    stacks.push_back({reinterpret_cast<uintptr_t>(&opt), sizeof opt});
    auto lv = fieldAt(0, 0, true);
    auto twice = rule({}, {change(lv, rules::Mul, 2)});
    auto plus = rule({cond(lv, rules::Eq, 10)}, {change(lv, rules::Add, 1)});  // its condition sees the original 10
    rules::Rule* rs[] = {twice.get(), plus.get()};
    rules::Frame in{{reinterpret_cast<uint64_t>(&opt)}}, out;
    CHECK(rules::apply(rs, 2, in, out) == -1 && opt.level == 21 && opt.set && twice->hits == 1 && plus->hits == 1);
    // Off this thread's stack (or null), a stack field can't change: the whole rule doesn't apply.
    auto heap = std::make_unique<int32_t[]>(2);
    heap[0] = 10;
    auto both = rule({}, {change(arg(1), rules::Set, 7), change(lv, rules::Set, 5)});
    rules::Rule* one[] = {both.get()};
    in = {{reinterpret_cast<uint64_t>(heap.get()), 0}};
    CHECK(rules::apply(one, 1, in, out) == -1 && heap[0] == 10 && out.a[1] == 0 && both->refused == 1 && !both->hits);
    in.a[0] = 0;
    CHECK(rules::apply(one, 1, in, out) == -1 && both->refused == 2);
    auto owned = rule({}, {change(fieldAt(0, 0, false), rules::Set, 5)});  // thread-owned: no stack check
    rules::Rule* o[] = {owned.get()};
    in.a[0] = reinterpret_cast<uint64_t>(heap.get());
    CHECK(rules::apply(o, 1, in, out) == -1 && heap[0] == 5);
    // A failing write: the write before it is undone, nothing counts, and only the rule that wrote the field turns off.
    static int32_t page[4];
    page[0] = 3;
    readOnly.push_back({reinterpret_cast<uintptr_t>(page), sizeof page});
    opt.level = 10;
    auto first = rule({}, {change(lv, rules::Set, 99)}), ro = rule({}, {change(fieldAt(1, 0, false), rules::Add, 1)});
    rules::Rule* rw[] = {first.get(), ro.get()};
    in = {{reinterpret_cast<uint64_t>(&opt), reinterpret_cast<uint64_t>(page)}};
    CHECK(rules::apply(rw, 2, in, out) == 1 && opt.level == 10 && page[0] == 3);
    CHECK(first->active && !first->hits && !ro->active && ro->faults == 1);
    readOnly.clear();
    stacks.clear();
}

std::unique_ptr<rules::Rule> blocker(std::initializer_list<rules::Cond> cs, rules::Loc retAt, rules::Operand v) {
    auto r = rule(cs, {});
    r->shape.block = true;
    r->shape.ret = v;
    r->shape.retAt = retAt;
    return r;
}
rules::Operand num(double v) { return {rules::Const, 0, v}; }
uint64_t blockedRet(rules::Loc retAt, double v, bool fp = false) {
    auto r = blocker({}, retAt, num(v));
    rules::Rule* rs[] = {r.get()};
    rules::Frame in{}, out;
    rules::Result res;
    CHECK(rules::apply(rs, 1, in, out, &res) == -1 && res.blocked && res.fp == fp);
    return res.ret;
}

// Blocking: the first matching block wins, nothing before it is committed, and the value takes the return kind.
void checkRuleBlock() {
    auto add = rule({}, {change(arg(0), rules::Add, 1)});
    auto stop = blocker({}, arg(0, 4), num(-2)), later = blocker({}, arg(0, 8), num(9));
    rules::Rule* rs[] = {add.get(), stop.get(), later.get()};
    rules::Frame in{{5}}, out;
    rules::Result res;
    CHECK(rules::apply(rs, 3, in, out, &res) == -1 && res.blocked && res.ret == uint64_t(-2) && out.a[0] == 5);
    CHECK(!add->hits && stop->hits == 1 && !later->hits);
    auto onSix = blocker({cond(arg(0), rules::Eq, 6)}, arg(0, 8), num(1));  // sees the original 5, not add's 6
    rules::Rule* cs[] = {add.get(), onSix.get()};
    CHECK(rules::apply(cs, 2, in, out, &res) == -1 && !res.blocked && out.a[0] == 6 && add->hits == 1);
    CHECK(blockedRet(arg(0, 1, rules::Unsigned), 300) == 0xFF && blockedRet(arg(0, 2, rules::Signed), -1) == ~0ull);
    CHECK(blockedRet(arg(0, 1, rules::Boolean), 7) == 1 && blockedRet({0, 0}, 5) == 0);
    CHECK(blockedRet(arg(0, 4, rules::Float), 1.5, true) == std::bit_cast<uint32_t>(1.5f));
    CHECK(blockedRet(arg(0, 8, rules::Float), -2.25, true) == std::bit_cast<uint64_t>(-2.25));
    auto mine = blocker({}, arg(0, 8), {rules::Player});
    rules::Rule* p[] = {mine.get()};
    rules::setPlayer(0);
    CHECK(rules::apply(p, 1, in, out, &res) == -1 && !res.blocked);  // no player: no value, no block
    auto param = blocker({}, arg(0, 8), {rules::Param, 1, 0});
    rules::setParam(*param, 1, 33);
    rules::Rule* q[] = {param.get()};
    CHECK(rules::apply(q, 1, in, out, &res) == -1 && res.ret == 33);
    CHECK(rules::writesSame(stop->shape, later->shape) && !rules::writesSame(stop->shape, add->shape));
}

std::unique_ptr<rules::Rule> afterRule(std::initializer_list<rules::Cond> cs, rules::Loc retAt, rules::Op op, double v) {
    auto r = rule(cs, {});
    r->shape.retAt = retAt;
    r->shape.after[r->shape.afters++] = change(retAt, op, v);
    return r;
}

// Return changes: only matching rules mark their position; they apply in order, converted to the return kind.
void checkRuleAfter() {
    auto twice = afterRule({}, arg(0, 4), rules::Mul, 2), plus = afterRule({cond(arg(0), rules::Eq, 1)}, arg(0, 4), rules::Add, 5);
    rules::Rule* rs[] = {twice.get(), plus.get()};
    rules::Frame in{{1}}, out;
    rules::Result res;
    CHECK(rules::apply(rs, 2, in, out, &res) == -1 && !res.blocked && res.after == 3 && twice->hits == 1);
    CHECK(rules::after(rs, 2, res.after, 10) == 25);  // (10 * 2) + 5
    in.a[0] = 2;
    CHECK(rules::apply(rs, 2, in, out, &res) == -1 && res.after == 1 && rules::after(rs, 2, res.after, 10) == 20);
    CHECK(rules::after(rs, 2, 1, 0xDEAD00000000ull | 0x7fffffff) == (0xDEAD00000000ull | 0x7fffffff));  // i32 saturates
    auto f = afterRule({}, arg(0, 4, rules::Float), rules::Mul, 2);
    rules::Rule* fs[] = {f.get()};
    uint64_t bits = rules::after(fs, 1, 1, 0xFFFFFFFF00000000ull | std::bit_cast<uint32_t>(1.5f));
    CHECK(bits >> 32 == 0xFFFFFFFF && std::bit_cast<float>(uint32_t(bits)) == 3.0f);
    CHECK(rules::writesSame(twice->shape, plus->shape) && !rules::writesSame(twice->shape, rule({}, {})->shape));
    // A later rule faulting drops the earlier rule's return change too: a call gets all of its changes or none.
    static uint8_t page[64];
    noAccess.push_back({reinterpret_cast<uintptr_t>(page), sizeof page});
    auto bad = rule({cond(rules::Loc{1, 4, rules::Float, true, 0}, rules::Gt, 1.0)}, {change(arg(0), rules::Set, 5)});
    rules::Rule* fr[] = {twice.get(), bad.get()};
    in = {{1, reinterpret_cast<uint64_t>(page)}};
    CHECK(rules::apply(fr, 2, in, out, &res) == 1 && !res.after && !bad->active);
    noAccess.clear();
}

// The list changes while 4 threads read it: a call sees the old or the new list, never an id from neither.
void checkRuleListRace() {
    auto r = rule({{arg(0, 4, rules::Unsigned), rules::In, {}}}, {change(arg(1), rules::Set, 1)});
    std::vector<uint32_t> a(64, 7), b(64, 8);
    std::atomic<bool> stop{false};
    std::atomic<int> started{0};
    std::atomic<uint64_t> seen{0}, wrong{0};
    std::vector<std::thread> readers;
    for (int t = 0; t < 4; ++t) readers.emplace_back([&] {
        rules::Rule* rs[] = {r.get()};
        rules::Frame out;
        ++started;
        while (!stop) {
            for (uint64_t id : {7, 8, 9}) {
                rules::apply(rs, 1, {{id, 0}}, out);
                if (out.a[1]) (id == 9 ? wrong : seen)++;
            }
        }
    });
    while (started < 4) std::this_thread::yield();
    auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(200);
    for (int i = 0; std::chrono::steady_clock::now() < end; ++i) rules::setList(*r, (i & 1 ? b : a).data(), 64);
    stop = true;
    for (auto& t : readers) t.join();
    CHECK(seen > 0 && wrong == 0);
}

// Catalog entries against a fake image: build, pattern and anchors, each entry on its own.
void checkCatalog() {
    using contracts::Entry;
    const uintptr_t base = imageBase();
    const char* tick = "40 55 53 56 41 55 48 8D AC 24 F8 FF";
    const uint8_t bytes[] = {0x40, 0x55, 0x53, 0x56, 0x41, 0x55, 0x48, 0x8D, 0xAC, 0x24, 0xF8, 0xFF};
    std::memcpy(image.data() + 0x100, bytes, sizeof bytes);
    scans[tick] = {base + 0x100};
    scans["90"] = {base + 1, base + 2};
    const uint32_t B = 1792148057;
    std::vector<Entry> entries = {
        {"ok", {"u64", "u8"}, "void", contracts::Args, {}, {{B, tick, {{0, "40 55"}, {6, "48 8D ?? 24"}}}}, "test"},
        {"anchor", {"u64"}, "void", contracts::Args, {}, {{B, tick, {{0, "41 55"}}}}, "test"},
        {"outside", {"u64"}, "void", contracts::Args, {}, {{B, tick, {{0x7fffffff, "90"}}}}, "test"},
        {"other build", {"u64"}, "void", contracts::Args, {}, {{1, tick, {}}}, "test"},
        {"ambiguous", {"u64"}, "void", contracts::Args, {}, {{B, "90", {}}}, "test"},
        {"hidden", {"u64"}, "hidden", contracts::Block, {}, {{B, tick, {}}}, "test"},
        {"fields", {"u64"}, "void", contracts::Fields, {}, {{B, tick, {}}}, "test"},
        {"no note", {"u64"}, "void", contracts::Args, {}, {{B, tick, {}}}, ""},
        {"kind", {"u64", "skip"}, "void", contracts::Fields, {{1, "s", 0, "cstr", true}}, {{B, tick, {}}}, "test"},
        {"overlap", {"u64", "skip"}, "void", contracts::Fields, {{1, "a", 0, "i32", true}, {1, "b", 2, "i16", true}},
         {{B, tick, {}}}, "test"},
        {"shared", {"u64", "skip"}, "void", contracts::Fields, {{1, "a", 0, "i32", false}, {1, "b", 4, "i32", false}},
         {{B, tick, {}}}, "test"},
        {"arg kind", {"Health*"}, "void", contracts::Args, {}, {{B, tick, {}}}, "test"},
        {"read only", {"u64", "skip"}, "void", contracts::Fields, {{1, "a", 0, "entity", true, true}}, {{B, tick, {}}}, "test"},
        {"reads off stack", {"u64", "skip"}, "void", contracts::Fields,
         {{1, "a", 0, "i32", false}, {1, "b", 4, "entity", false, true}}, {{B, tick, {}}}, "test"},
    };
    auto s = contracts::check(base, B, entries);
    CHECK(s.size() == 14 && s[12].why.find("writable") != std::string::npos && s[13].why.empty());
    CHECK(s[0].fn == base + 0x100 && s[0].why.empty() && s[0].contract == B);
    CHECK(!s[1].fn && s[1].why == "Anchor 1 differs" && !s[2].fn && s[2].why == "Anchor 1: outside the game image");
    CHECK(!s[3].fn && s[3].why == "Untested build" && s[4].why.find("ambiguous") != std::string::npos);
    CHECK(s[5].why.find("hidden") == 0 && s[6].why.find("fields") == 0 && s[7].why == "no RE note");
    CHECK(s[8].why == "field kind isn't a number" && s[9].why.find("overlap") != std::string::npos);
    CHECK(s[10].why == "more than one field off the stack" && s[11].why == "unknown argument kind" && s[0].entry == &entries[0]);
    for (const auto& f : contracts::check(base, 2, entries)) CHECK(!f.fn);  // a build no entry lists: every entry off
    auto any = contracts::check(base, 1, entries, true);
    CHECK(any[0].fn && any[3].fn == base + 0x100 && any[3].contract == 1);
    contracts::setChecked(s);
    contracts::Status found;
    CHECK(contracts::find("ok", found) && found.fn == s[0].fn && !contracts::find("missing", found));
    contracts::setChecked({});
    // SET's own catalog is well formed, lists SET's three builds, and is off on any other build (the dedicated server's).
    const auto& cat = contracts::catalog();
    CHECK(cat.size() == 2 && std::string(cat[0].name) == "combat_xp" && std::string(cat[1].name) == "hit");
    CHECK(cat[0].thunk == hk::ThunkCombatXp && cat[1].thunk == hk::NoThunk && (cat[0].caps & contracts::After) && !(cat[1].caps & contracts::After));
    for (const auto& e : contracts::check(base, 1793791038, cat)) CHECK(!e.fn && e.why == "Untested build");
    for (const auto& e : contracts::check(base, 1793791052, cat)) CHECK(!e.fn && e.why.find("Pattern: ") == 0);  // listed, not in this image
    // With the patterns "found" and the anchors in place, both check out on a listed build.
    std::string err;
    for (const auto& e : cat) {
        std::string norm;
        std::vector<int> bs;
        CHECK(parse_pattern(e.builds.back().pattern, norm, bs, err));
        const uintptr_t at = base + (std::string(e.name) == "hit" ? 0x200 : 0x800);
        scans[norm] = {at};
    }
    image.resize(0x2000, 0xCC);  // may move: the scans above are re-pointed below
    const uintptr_t nb = imageBase();
    for (auto& [pat, hits] : scans) for (auto& h : hits) h = h - base + nb;
    auto put = [&](uintptr_t off, std::initializer_list<int> bs) { size_t i = 0; for (int b : bs) image[off + i++] = uint8_t(b); };
    put(0x800 + 0x14, {0x80, 0x7A, 0x04, 0x00}); put(0x800 + 0x1A, {0x8B, 0x02}); put(0x800 + 0xEC, {0xC3, 0xCC});
    put(0x200 + 0x3E7, {0x41, 0x8B, 0xB5, 0xBC, 0, 0, 0});
    put(0x200 + 0x861, {0xBA, 0xE0, 0x57, 0x0D, 0xCC, 0x41, 0xB8, 0xF8, 0, 0, 0});
    put(0x200 + 0x94F, {0x89, 0xB3, 0xBC, 0, 0, 0});
    auto real = contracts::check(nb, 1793791052, cat);
    CHECK(real[0].why.empty() && real[0].fn == nb + 0x800 && real[1].why.empty() && real[1].fn == nb + 0x200);
    image[0x200 + 0x94F] = 0x90;  // one anchor differs: only that entry is off
    real = contracts::check(nb, 1793791052, cat);
    CHECK(real[0].fn && !real[1].fn && real[1].why == "Anchor 3 differs");
}

uint64_t sum8(const uint64_t* a) { return a[0] + a[1] * 2 + a[2] * 3 + a[3] * 4 + a[4] * 5 + a[5] * 6 + a[6] * 7 + a[7] * 8; }

// Register, XMM and stack positions through the entry hook, and observers seeing what the original gets.
void checkHookArgs() {
    const uintptr_t fn = 0x10000;
    std::vector<hk::Arg> args = {{hk::I32, {}}, {hk::F64, {}}, {hk::I32, {}}, {hk::F32, {}}, {hk::F64, {}}, {hk::I32, {}}, {hk::F32, {}}, {hk::F64, {}}};
    int id = addRule(fn, "args/mixed", args, {},
                     {change(loc(hk::F64, 1), rules::Mul, 2), change(loc(hk::F32, 3), rules::Set, 10),
                      change(loc(hk::I32, 5), rules::Add, 100), change(loc(hk::F64, 7), rules::Set, 0)});
    std::string err;
    int obs = hk::subscribe(fn, "args/observer", args, 16, err);
    CHECK(obs > 0);
    const std::array<uint64_t, 8> in = {uint64_t(uint32_t(-3)), std::bit_cast<uint64_t>(1.25), 7, std::bit_cast<uint32_t>(2.5f),
                                       std::bit_cast<uint64_t>(8.25), uint64_t(uint32_t(-4)), std::bit_cast<uint32_t>(3.5f),
                                       std::bit_cast<uint64_t>(16.0)};
    Call c = call(fn, &sum8, in, 0b1010);
    CHECK(!c.blocked && std::bit_cast<double>(c.seen[1]) == 2.5 && std::bit_cast<float>(uint32_t(c.seen[3])) == 10.0f);
    CHECK(int32_t(c.seen[5]) == 96 && std::bit_cast<double>(c.seen[7]) == 0.0 && c.seen[0] == in[0] && c.seen[4] == in[4]);
    hk::Batch b;
    CHECK(hk::drain(obs, b) && std::bit_cast<double>(b.events[0].v[1].bits) == 2.5 && int64_t(b.events[0].v[5].bits) == 96);
    hk::removeRule(id);
    c = call(fn, &sum8, in, 0b1010);
    CHECK(std::bit_cast<double>(c.seen[1]) == 1.25 && int32_t(c.seen[5]) == -4);
    hk::unsubscribe(obs);
    std::string cft;
    hk::RuleDecl wrong{"args/wrong", {{hk::U64, {}}, {hk::U64, {}}}, rule({}, {change(loc(hk::U64, 1), rules::Add, 1)})->shape};
    CHECK(hk::addRule(fn, wrong, err, cft) >= 0);  // nothing else declared now, so it registers
    hk::RuleDecl clash{"args/clash", args, rule({}, {change(loc(hk::F64, 1), rules::Add, 1)})->shape};
    CHECK(refused(fn, clash).find("disagrees with args/wrong") != std::string::npos);
    hk::unsubscribeAll();
    c = call(fn, &sum8, in, 0b1010);
    CHECK(c.seen[1] == in[1]);
}

// Three mods' rules on one value run in mod order, whatever order they registered in; same-value changes by different
// mods are reported; params and lists change live; a removed rule's slot is reused.
void checkHookComposition() {
    const uintptr_t fn = 0x20000;
    std::vector<hk::Arg> u64(1, {hk::U64, {}});
    std::string conflict;
    addRule(fn, "a/last", u64, {}, {change(loc(hk::U64, 0), rules::Add, -1)}, 2);
    addRule(fn, "b/first", u64, {}, {change(loc(hk::U64, 0), rules::Add, 10)}, 0, {}, &conflict);
    CHECK(conflict == "a/last");
    addRule(fn, "c/middle", u64, {}, {change(loc(hk::U64, 0), rules::Mul, 3)}, 1);
    CHECK(call(fn, &sum8, {1, 2, 3, 4, 5, 6, 7, 8}).ret == 204 - 1 + 32);  // ((1 + 10) * 3) - 1
    addRule(fn, "a/same mod", u64, {}, {change(loc(hk::U64, 0), rules::Add, 0)}, 2, {}, &conflict);
    CHECK(conflict == "b/first");  // another mod still does
    std::string err, c;
    hk::RuleDecl again{"a/last", u64, rule({}, {change(loc(hk::U64, 0), rules::Add, -1)})->shape, 2};
    CHECK(hk::addRule(fn, again, err, c) < 0 && err.find("already active") != std::string::npos);
    hk::unsubscribeAll();
    CHECK(call(fn, &sum8, {1, 2, 3, 4, 5, 6, 7, 8}).ret == 204);
    // The same rule made again reuses its slot; a param and an id list change what the next calls get.
    hk::RuleDecl p{"a/param", {{hk::U64, {}}, {hk::Entity, {}}}, {}, 0, {2.0}};
    p.shape.cond[p.shape.conds++] = {loc(hk::Entity, 1), rules::In, {}};
    p.shape.change[p.shape.changes++] = {loc(hk::U64, 0), rules::Mul, {rules::Param, 0, 0}};
    p.list = {7};
    const int id = hk::addRule(fn, p, err, c);
    CHECK(id >= 0);
    CHECK(call(fn, &sum8, {5, 7, 0, 0, 0, 0, 0, 0}).seen[0] == 10 && call(fn, &sum8, {5, 8, 0, 0, 0, 0, 0, 0}).seen[0] == 5);
    CHECK(hk::setParam(id, 0, 3) && hk::setList(id, {8, 9}));
    CHECK(call(fn, &sum8, {5, 7, 0, 0, 0, 0, 0, 0}).seen[0] == 5 && call(fn, &sum8, {5, 8, 0, 0, 0, 0, 0, 0}).seen[0] == 15);
    hk::removeRule(id);
    CHECK(!hk::setParam(id, 0, 4) && call(fn, &sum8, {5, 8, 0, 0, 0, 0, 0, 0}).seen[0] == 5);
    CHECK(hk::addRule(fn, p, err, c) == id);
    hk::unsubscribeAll();
}

// Catalog-style fields through the hook: on the calling thread's stack they change, elsewhere the call is refused; a
// field that can't be read or written turns only its rule off and leaves the call untouched.
void checkHookFields() {
    const uintptr_t fn = 0x30000;
    std::vector<hk::Arg> skips(4);
    struct Hit { uint32_t pad[36]; uint32_t rootSource, target; uint32_t more[9]; int32_t healthChange; } hit{};
    static_assert(offsetof(Hit, healthChange) == 0xBC && offsetof(Hit, target) == 0x94);
    const rules::Loc health = fieldAt(3, 0xBC, true), target = fieldAt(3, 0x94, true, 4, rules::Unsigned);
    hk::RuleDecl d{"mod/tough", skips, {}, 0};
    d.fields = {health};
    d.reads = {target};
    d.shape.cond[d.shape.conds++] = {target, rules::Eq, {rules::Player}};
    d.shape.change[d.shape.changes++] = change(health, rules::Mul, 0.5);
    std::string err, c;
    const int id = hk::addRule(fn, d, err, c);
    CHECK(id >= 0);
    hit.target = 42;
    hit.healthChange = -31;
    stacks.push_back({reinterpret_cast<uintptr_t>(&hit), sizeof hit});
    const std::array<uint64_t, 8> a = {1, 2, 3, reinterpret_cast<uint64_t>(&hit)};
    call(fn, &sum8, a);
    CHECK(hit.healthChange == -31);  // no player yet
    rules::setPlayer(42);
    call(fn, &sum8, a);
    CHECK(hit.healthChange == -16);  // halving rounds away from zero
    hit.healthChange = -1;
    call(fn, &sum8, a);
    CHECK(hit.healthChange == -1);
    stacks.clear();  // the same struct, not on the calling thread's stack: refused, counted
    hit.healthChange = -30;
    call(fn, &sum8, a);
    CHECK(hit.healthChange == -30);
    bool sawRefused = false;
    for (const auto& t : hk::info()) for (const auto& r : t.rules) sawRefused |= r.key == "mod/tough" && r.refused == 1 && r.hits == 2;
    CHECK(sawRefused);
    // Changing a read-only field, or any field without a catalog, is refused when the rule is made.
    hk::RuleDecl bad{"mod/bad", skips, {}, 0};
    bad.fields = {health};
    bad.reads = {target};
    bad.shape.change[bad.shape.changes++] = change(target, rules::Set, 1);
    CHECK(refused(fn, bad).find("doesn't let rules change that field") != std::string::npos);
    hk::RuleDecl plain{"mod/plain", {{hk::EntityPtr, {}}}, {}, 0};
    plain.shape.change[plain.shape.changes++] = change(fieldAt(0, 0, false, 4, rules::Unsigned), rules::Set, 1);
    CHECK(refused(fn, plain) == "changing a field needs a catalog target");
    // An unreadable pointer: the call is untouched, the rule turns off with a line, and it can't come back.
    stacks.push_back({reinterpret_cast<uintptr_t>(&hit), sizeof hit});
    noAccess.push_back({reinterpret_cast<uintptr_t>(&hit), sizeof hit});
    const uint64_t before = hk::problems();
    call(fn, &sum8, a);
    noAccess.clear();
    CHECK(hit.healthChange == -30 && hk::problems() == before + 1);
    static std::vector<std::string> lines;
    hk::setLog([](const char* l) { lines.emplace_back(l); });
    hk::flushLogs();
    bool told = false;
    for (const auto& l : lines) told |= l.find("Rule mod/tough turned off") != std::string::npos;
    CHECK(told);
    call(fn, &sum8, a);
    CHECK(hit.healthChange == -30);
    hk::removeRule(id);
    CHECK(refused(fn, d).find("this rule faulted") != std::string::npos);
    rules::setPlayer(0);
    stacks.clear();
    hk::unsubscribeAll();
}

// Blocking through the entry hook: the original doesn't run, the caller gets the value, observers still see the call.
void checkHookBlock() {
    const uintptr_t fn = 0x40000;
    std::vector<hk::Arg> args = {{hk::U32, {}}, {hk::Skip, {}}, {hk::F32, {}}, {hk::U32, {}}};
    hk::RuleDecl d{"mod/flat", args, {}, 0, {50.0}};
    d.block = true;
    d.shape.block = true;
    d.shape.ret = {rules::Param, 0, 0};
    d.shape.retAt = arg(0, 4, rules::Unsigned);
    d.shape.cond[d.shape.conds++] = cond(loc(hk::U32, 3), rules::Ge, 2);
    std::string err, c;
    hk::RuleDecl noCatalog = d;
    noCatalog.key = "mod/nocatalog";
    noCatalog.block = false;
    CHECK(refused(fn, noCatalog) == "blocking a call needs a catalog target that allows it");
    const int id = hk::addRule(fn, d, err, c);
    CHECK(id >= 0);
    const int obs = hk::subscribe(fn, "mod/watch", args, 8, err);
    static int ran;
    ran = 0;
    const Original orig = [](const uint64_t*) -> uint64_t { ++ran; return 7; };
    Call one = call(fn, orig, {10, 0, std::bit_cast<uint32_t>(0.8f), 1}, 0b0100);
    CHECK(!one.blocked && one.ret == 7 && ran == 1);
    Call two = call(fn, orig, {10, 0, std::bit_cast<uint32_t>(0.8f), 2}, 0b0100);
    CHECK(two.blocked && two.ret == 50 && ran == 1);
    hk::setParam(id, 0, 4294967296.0 * 2);  // saturates at the u32 return kind
    CHECK(call(fn, orig, {10, 0, 0, 3}, 0b0100).ret == 0xFFFFFFFFull);
    hk::Batch b;
    CHECK(hk::drain(obs, b) && b.events.size() == 3);  // blocked calls are observed too
    // A float return goes to XMM0.
    hk::RuleDecl f{"mod/float", args, {}, 1};
    f.block = true;
    f.shape.block = true;
    f.shape.ret = num(1.5);
    f.shape.retAt = arg(0, 8, rules::Float);
    CHECK(hk::addRule(0x40100, f, err, c) >= 0);
    CHECK(call(0x40100, orig, {}).blocked && call(0x40100, orig, {}).xmm0 == 1.5);
    hk::unsubscribeAll();
    CHECK(!call(fn, orig, {10, 0, 0, 3}, 0b0100).blocked);
}

// Return changes: only with the catalog's typed link (kiln.hooks 2 raw links), applied after the original, only for
// calls whose entry matched, and gone when the rule is.
void checkHookAfter() {
    const uintptr_t fn = 0x50000;
    std::vector<hk::Arg> args = {{hk::U32, {}}, {hk::Skip, {}}, {hk::F32, {}}, {hk::U32, {}}, {hk::Skip, {}}};
    auto decl = [&](const char* key) {
        hk::RuleDecl d{key, args, {}, 0};
        d.after = true;
        d.thunk = hk::ThunkCombatXp;
        d.shape.retAt = arg(0, 4, rules::Unsigned);
        d.shape.after[d.shape.afters++] = change(d.shape.retAt, rules::Mul, 2);
        d.shape.after[d.shape.afters++] = change(d.shape.retAt, rules::Max, 5000);
        return d;
    };
    std::string err, c, why;
    hk::RuleDecl none = decl("mod/nothunk");
    none.thunk = hk::NoThunk;
    CHECK(refused(fn, none).find("no return-change link") != std::string::npos && raws.empty());
    hooks.header.size = offsetof(KilnHooksApi, raw_detour);  // a kiln.hooks 1 table: no raw links
    CHECK(!hk::afterAvailable(hk::ThunkCombatXp, why) && why.find("kiln.hooks 2") != std::string::npos);
    CHECK(refused(fn, decl("mod/v1")).find("isn't available") != std::string::npos && raws.empty());
    hooks.header.size = sizeof hooks;
    CHECK(hk::afterAvailable(hk::ThunkCombatXp, why) && !hk::afterAvailable(hk::NoThunk, why));
    hk::RuleDecl d = decl("mod/double");
    d.shape.cond[d.shape.conds++] = cond(loc(hk::U32, 0), rules::Lt, 30);
    const int id = hk::addRule(fn, d, err, c);
    CHECK(id >= 0 && raws.count(fn) == 1 && raws[fn].priority < 0);
    const Original xp = [](const uint64_t* a) -> uint64_t { return 0xAB00000000ull | (a[0] * 100 + a[3]); };
    const float mod = 0.8f;
    Call lo = call(fn, xp, {10, 0x1234, std::bit_cast<uint32_t>(mod), 2, 0x55, 6, 7, 8}, 0b0100);
    CHECK(lo.ret == (0xAB00000000ull | 2004));  // (10 * 100 + 2) * 2: the low 32 bits only
    CHECK(lo.seen[0] == 10 && lo.seen[1] == 0x1234 && uint32_t(lo.seen[2]) == std::bit_cast<uint32_t>(mod) && lo.seen[3] == 2 &&
          lo.seen[4] == 0x55 && lo.seen[5] == 6 && lo.seen[6] == 7 && lo.seen[7] == 8);  // every position reaches the original
    CHECK(call(fn, xp, {29, 0, 0, 0}, 0b0100).ret == (0xAB00000000ull | 5000));  // 5800 capped
    CHECK(call(fn, xp, {30, 0, 0, 0}, 0b0100).ret == (0xAB00000000ull | 3000));  // the condition didn't hold at the entry
    // An argument rule of another mod and the return change compose: the original sees the changed argument.
    addRule(fn, "other/levels", args, {}, {change(loc(hk::U32, 0), rules::Add, 5)}, 1);
    CHECK(call(fn, xp, {10, 0, 0, 0}, 0b0100).ret == (0xAB00000000ull | 3000));  // (10 + 5) * 100 * 2
    hk::removeRule(id);
    CHECK(call(fn, xp, {10, 0, 0, 0}, 0b0100).ret == (0xAB00000000ull | 1500));  // the link stays, and changes nothing
    // A blocking rule and a return change in one rule, or an after on a target without the capability, are refused.
    hk::RuleDecl both = decl("mod/both");
    both.block = true;
    both.shape.block = true;
    CHECK(refused(fn, both) == "a rule that blocks changes nothing: the original doesn't run");
    hk::RuleDecl cap = decl("mod/cap");
    cap.after = false;
    CHECK(refused(fn, cap) == "changing a return value needs a catalog target that allows it");
    hk::unsubscribeAll();
}

// Limits and refusals when a rule is made.
void checkHookLimits() {
    const uintptr_t fn = 0x60000;
    std::vector<hk::Arg> u64(1, {hk::U64, {}});
    std::string err, c;
    for (int i = 0; i < 15; ++i) {
        hk::RuleDecl d{"limit/r" + std::to_string(i), u64, rule({}, {change(loc(hk::U64, 0), rules::Add, i)})->shape};
        CHECK(hk::addRule(fn, d, err, c) >= 0);
    }
    hk::RuleDecl over{"limit/over", u64, rule({}, {change(loc(hk::U64, 0), rules::Add, 99)})->shape};
    CHECK(refused(fn, over).find("15 retained rules") != std::string::npos);
    CHECK(call(fn, &sum8, {0}).seen[0] == 105);
    hk::unsubscribeAll();
    hk::RuleDecl empty{"limit/empty", u64, {}};
    CHECK(refused(fn, empty) == "a rule needs at least one change, a block or an after");
    hk::RuleDecl undeclared{"limit/undeclared", u64, rule({}, {change(loc(hk::U64, 1), rules::Add, 1)})->shape};
    CHECK(refused(fn, undeclared).find("isn't declared") != std::string::npos);
    hk::RuleDecl kind{"limit/kind", u64, rule({}, {change(loc(hk::F64, 0), rules::Add, 1)})->shape};
    CHECK(refused(fn, kind).find("doesn't match argument 1") != std::string::npos);
    hk::RuleDecl param{"limit/param", u64, {}};
    param.shape.change[param.shape.changes++] = {loc(hk::U64, 0), rules::Set, {rules::Param, 0, 0}};
    CHECK(refused(fn, param) == "rule uses an unset param");
    hk::reserve(0x70000, "runtime");
    CHECK(refused(0x70000, over).find("reserved by Kiln") != std::string::npos);
    CHECK(!hk::validRule(empty, err) && hk::validRule(over, err));
    hk::Kind k;
    CHECK(hk::kindOf("ptrsize", k) && k == hk::U64 && hk::kindOf("entity*", k) && k == hk::EntityPtr && !hk::kindOf("Health*", k));
    rules::Loc l;
    CHECK(!hk::argLoc({hk::CStr, {}}, 0, l) && !hk::argLoc({hk::EntityPtr, {}}, 0, l));
    CHECK(hk::fieldLoc({hk::EntityPtr, {}}, 2, "", l, err) && l.field && l.arg == 2 && l.width == 4 && l.num == rules::Unsigned);
    CHECK(!hk::fieldLoc({hk::U32, {}}, 0, "x", l, err) && err.find("doesn't point at a reflected type") != std::string::npos);
}
}  // namespace

// Reflection is not part of these cases (no reflected snapshot arguments).
bool refl::plain(Type) { return true; }
uint32_t refl::Type::u32(size_t off) const { uint32_t n = 0; if (p) std::memcpy(&n, p + off, 4); return n; }
uint8_t refl::Type::prim() const { return p ? p[0x4c] : 0; }
std::string_view refl::Type::str(size_t) const { return {}; }
bool refl::path(Type, std::string_view, uint32_t&, Type&) { return false; }

int main() {
    hooks.header = {sizeof hooks, 2};
    hooks.read = &h_read;
    hooks.mid = &h_mid;
    hooks.raw_detour = &h_raw;
    hooks.scan = &h_scan;
    hooks.image_base = []() -> uintptr_t { return imageBase(); };
    hooks.image_size = []() -> size_t { return image.size(); };
    kset::hooktest::write = &t_write;
    kset::hooktest::on_stack = &t_on_stack;
    KilnApi api{};
    api.service = &service;
    api.symbol = &symbol;
    kset::bindHooks(&api, 7);
    checkRuleOrder();
    checkRuleValues();
    checkRuleConds();
    checkRuleFields();
    checkRuleBlock();
    checkRuleAfter();
    checkRuleListRace();
    std::puts("ok: rule engine (order, values, conditions, fields, block, after, list race)");
    checkCatalog();
    std::puts("ok: catalog checks (builds, patterns, anchors, malformed entries)");
    checkHookArgs();
    checkHookComposition();
    checkHookFields();
    checkHookBlock();
    checkHookAfter();
    checkHookLimits();
    std::puts("ok: rules through the entry hook (arguments, composition, fields, block, after link, limits)");
    std::puts("SET rules review: PASS");
}
