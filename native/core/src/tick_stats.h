// SPDX-License-Identifier: MIT
// One kiln/logs/tick-stats.jsonl line (tick.cpp write_window: once a minute per world) and, since 0.2.0-dev.29 (issue #39:
// slow motion at each autosave that the old line could not attribute), the parts of the window it reports:
//   post_parts  the game's own wait for the world's jobs (WaitWorldJobs; server world only), the window services' end
//               (kiln.window_clock phase 4; server world only) and each Post subscriber by owner name;
//   pre_parts   the Pre subscribers, the 8 with the longest call;
//   worst       the window's worst step (Pre + step + Post) broken down the same way, with the local time it ended.
// Each part is {max, total, over50}: its longest call, the sum over the window, and the calls longer than 50 ms. The fields
// before "subs" keep their names and meanings. The tick thread only adds numbers into slots sized when the subscriber lists
// change (Part::add, beat); owner names are looked up and the line is formatted when it is written, once a minute. Pure, so
// a host test drives it (tests/tick_stats_test.cpp).
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <string>
#include <utility>
#include <vector>

#include "tasks_stats.h"

namespace kiln::tick::stats {

inline constexpr float kSlowMs = 50.0f;  // "over50"
inline constexpr size_t kPreTop = 8;     // Pre subscribers listed per line and in the worst step

// One timed part over a window.
struct Part {
    float max = 0;
    double total = 0;
    uint32_t over = 0;  // calls longer than kSlowMs
    void add(float ms) {
        if (ms > max) max = ms;
        total += ms;
        if (ms > kSlowMs) ++over;
    }
    void merge(const Part& o) {
        if (o.max > max) max = o.max;
        total += o.total;
        over += o.over;
    }
    bool empty() const { return max == 0 && total == 0 && over == 0; }
};

// A subscriber's slot: parallel to one world's Pre or Post list, in run order. `cur` is its time in the current step.
struct Slot {
    uint64_t handle = 0, owner = 0;
    Part part;
    float cur = 0;
};

// The slots of one world's subscribers, and those of subscribers that left during the window (with what they took).
struct Subs {
    std::vector<Slot> pre, post, gone_pre, gone_post;
};

// Re-aligns `slots` with a new subscriber list (only when the list changed, not per step): a subscriber that stays keeps
// its window's numbers, one that left keeps them in `gone` until the line is written. SubPtr: anything with ->handle, ->owner.
template <class SubPtr>
void rebind(std::vector<Slot>& slots, std::vector<Slot>& gone, const std::vector<SubPtr>& subs) {
    std::vector<Slot> next(subs.size());
    for (size_t i = 0; i < subs.size(); ++i) {
        next[i].handle = subs[i]->handle;
        next[i].owner = subs[i]->owner;
        for (Slot& o : slots)
            if (o.handle != 0 && o.handle == next[i].handle) {
                next[i].part = o.part;
                o.handle = 0;  // taken
                break;
            }
    }
    for (const Slot& o : slots)
        if (o.handle != 0 && !o.part.empty()) gone.push_back(o);
    slots.swap(next);
}

// A new window: every slot starts again from zero.
inline void clear(Subs& s) {
    for (auto* v : {&s.pre, &s.post})
        for (Slot& x : *v) {
            x.part = Part{};
            x.cur = 0;
        }
    s.gone_pre.clear();
    s.gone_post.clear();
}

struct OwnerMs {
    uint64_t owner = 0;
    float ms = 0;
};

// The window's worst step: the one with the longest Pre + step + Post.
struct Worst {
    float total = -1;  // -1: no step yet
    float pre = 0, step = 0, post = 0, wait = 0, windows = 0;
    unsigned hour = 0, minute = 0, second = 0, milli = 0;
    std::vector<OwnerMs> pre_subs, post_subs;  // reserved for the lists (reserve): copying a step allocates nothing
};

inline void reserve(Worst& w, const Subs& s) {
    w.pre_subs.reserve(s.pre.size());
    w.post_subs.reserve(s.post.size());
}

inline void reset(Worst& w) {
    w.total = -1;
    w.pre = w.step = w.post = w.wait = w.windows = 0;
    w.hour = w.minute = w.second = w.milli = 0;
    w.pre_subs.clear();  // keeps the capacity
    w.post_subs.clear();
}

// This step's times (ms) beat the window's worst: keeps them and every subscriber's time of this step. True when they did
// (the caller stamps the time). `post_ran`: the Post subscribers ran in this step (their `cur` is this step's).
inline bool beat(Worst& w, float pre, float step, float post, float wait, float windows, const Subs& s, bool post_ran) {
    const float total = pre + step + post;
    if (!(total > w.total)) return false;
    w.total = total;
    w.pre = pre;
    w.step = step;
    w.post = post;
    w.wait = wait;
    w.windows = windows;
    w.pre_subs.clear();
    for (const Slot& x : s.pre) w.pre_subs.push_back(OwnerMs{x.owner, x.cur});
    w.post_subs.clear();
    if (post_ran)
        for (const Slot& x : s.post) w.post_subs.push_back(OwnerMs{x.owner, x.cur});
    return true;
}

// ---- the line (written once a minute) ----

struct Pcts {
    float p50 = 0, p99 = 0, max = 0;
};
struct Named {
    std::string owner;
    Part part;
};
struct NamedMs {
    std::string owner;
    float ms = 0;
};

struct Line {
    unsigned year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;  // local time the line is written
    const char* world = nullptr;  // "server" | "prediction" | "client" on a game client; null on a dedicated server
    unsigned long long steps = 0;
    double rate = 0, clock_rate = 0;
    Pcts step, pre, post, interval;
    size_t pre_count = 0, post_count = 0;
    // since 0.2.0-dev.29
    bool waits = false;  // the server world with WaitWorldJobs: wait_ms, windows_ms and the worst step's wait/windows
    Pcts wait;           // over the steps that waited
    Part wait_part, windows;
    std::vector<Named> post_subs, pre_subs;  // pre_subs: the kPreTop with the longest call (top)
    bool has_worst = false;
    float worst_pre = 0, worst_step = 0, worst_post = 0, worst_wait = 0, worst_windows = 0;
    unsigned worst_hour = 0, worst_minute = 0, worst_second = 0, worst_milli = 0;
    std::vector<NamedMs> worst_post_subs, worst_pre_subs;  // worst_pre_subs: the kPreTop longest in that step
    // Issue #49: the kiln.tasks window, appended after every older field. Absent (has_tasks false)
    // when the task pool does not exist, so a line without a pool keeps its old shape byte for byte.
    bool has_tasks = false;
    kiln::tasks_stats::WindowSnapshot tasks;
};

// Slots by owner name, in run order (an owner's first slot gives its place); one owner's subscribers add up.
template <class NameOf>
std::vector<Named> by_owner(const std::vector<Slot>& slots, const std::vector<Slot>& gone, NameOf&& name_of) {
    std::vector<Named> out;
    auto add = [&](const Slot& x) {
        std::string n = name_of(x.owner);
        for (Named& e : out)
            if (e.owner == n) {
                e.part.merge(x.part);
                return;
            }
        out.push_back(Named{std::move(n), x.part});
    };
    for (const Slot& x : slots) add(x);
    for (const Slot& x : gone) add(x);
    return out;
}

template <class NameOf>
std::vector<NamedMs> by_owner(const std::vector<OwnerMs>& v, NameOf&& name_of) {
    std::vector<NamedMs> out;
    for (const OwnerMs& x : v) {
        std::string n = name_of(x.owner);
        bool found = false;
        for (NamedMs& e : out)
            if (e.owner == n) {
                e.ms += x.ms;
                found = true;
                break;
            }
        if (!found) out.push_back(NamedMs{std::move(n), x.ms});
    }
    return out;
}

// The `n` with the longest call (or time), longest first; ties keep run order.
inline void top(std::vector<Named>& v, size_t n) {
    std::stable_sort(v.begin(), v.end(), [](const Named& a, const Named& b) { return a.part.max > b.part.max; });
    if (v.size() > n) v.resize(n);
}
inline void top(std::vector<NamedMs>& v, size_t n) {
    std::stable_sort(v.begin(), v.end(), [](const NamedMs& a, const NamedMs& b) { return a.ms > b.ms; });
    if (v.size() > n) v.resize(n);
}

namespace detail {

inline void appendf(std::string& out, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    va_list copy;
    va_copy(copy, ap);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) {
        va_end(copy);
        return;
    }
    if (static_cast<size_t>(n) < sizeof buf) {
        out.append(buf, static_cast<size_t>(n));
    } else {
        std::string big(static_cast<size_t>(n) + 1, '\0');
        std::vsnprintf(big.data(), big.size(), fmt, copy);
        big.resize(static_cast<size_t>(n));
        out += big;
    }
    va_end(copy);
}

inline double fin(double v) { return std::isfinite(v) ? v : 0.0; }

// A JSON string body: quotes, backslashes and control characters escaped; bytes that are not well-formed UTF-8 become '?'
// (a line a JSON reader rejects would hide its old fields too).
inline void escape(std::string& out, const std::string& s) {
    const auto* p = reinterpret_cast<const unsigned char*>(s.data());
    const size_t n = s.size();
    for (size_t i = 0; i < n;) {
        const unsigned char c = p[i];
        if (c == '"' || c == '\\') {
            out += '\\';
            out += static_cast<char>(c);
            ++i;
        } else if (c < 0x20) {
            appendf(out, "\\u%04x", static_cast<unsigned>(c));
            ++i;
        } else if (c < 0x80) {
            out += static_cast<char>(c);
            ++i;
        } else {
            const size_t len = c >= 0xF0 ? (c <= 0xF4 ? 4 : 0) : c >= 0xE0 ? 3 : c >= 0xC2 ? 2 : 0;
            bool ok = len != 0 && i + len <= n;
            for (size_t k = 1; ok && k < len; ++k) ok = (p[i + k] & 0xC0) == 0x80;
            if (ok && len == 3) {  // no overlong forms, no surrogates
                ok = !(c == 0xE0 && p[i + 1] < 0xA0) && !(c == 0xED && p[i + 1] >= 0xA0);
            } else if (ok && len == 4) {  // no overlong forms, nothing past U+10FFFF
                ok = !(c == 0xF0 && p[i + 1] < 0x90) && !(c == 0xF4 && p[i + 1] >= 0x90);
            }
            if (ok) {
                out.append(s, i, len);
                i += len;
            } else {
                out += '?';
                ++i;
            }
        }
    }
}

inline void part(std::string& out, const Part& p) {
    appendf(out, "\"max\":%.3f,\"total\":%.3f,\"over50\":%u", fin(p.max), fin(p.total), p.over);
}

inline void named_parts(std::string& out, const std::vector<Named>& v) {
    out += '[';
    for (size_t i = 0; i < v.size(); ++i) {
        out += i ? ",{\"owner\":\"" : "{\"owner\":\"";
        escape(out, v[i].owner);
        out += "\",";
        part(out, v[i].part);
        out += '}';
    }
    out += ']';
}

inline void named_ms(std::string& out, const std::vector<NamedMs>& v) {
    out += '[';
    for (size_t i = 0; i < v.size(); ++i) {
        out += i ? ",{\"owner\":\"" : "{\"owner\":\"";
        escape(out, v[i].owner);
        appendf(out, "\",\"ms\":%.3f}", fin(v[i].ms));
    }
    out += ']';
}

}  // namespace detail

// The whole line, newline included. Everything up to "subs" is byte for byte the line before 0.2.0-dev.29.
inline std::string format(const Line& l) {
    using detail::appendf;
    using detail::fin;
    std::string out;
    out.reserve(1024);
    char tag[48] = "";
    if (l.world) std::snprintf(tag, sizeof tag, "\"world\":\"%s\",", l.world);
    appendf(out,
            "{\"time\":\"%04u-%02u-%02uT%02u:%02u:%02u\",%s\"steps\":%llu,\"rate\":%.2f,\"clock_rate\":%.2f,"
            "\"step_ms\":{\"p50\":%.3f,\"p99\":%.3f,\"max\":%.3f},"
            "\"pre_ms\":{\"p50\":%.4f,\"p99\":%.4f,\"max\":%.3f},"
            "\"post_ms\":{\"p50\":%.4f,\"p99\":%.4f,\"max\":%.3f},"
            "\"interval_ms\":{\"p50\":%.2f,\"p99\":%.2f,\"max\":%.2f},\"subs\":{\"pre\":%zu,\"post\":%zu}",
            l.year, l.month, l.day, l.hour, l.minute, l.second, tag, l.steps, fin(l.rate), fin(l.clock_rate),
            fin(l.step.p50), fin(l.step.p99), fin(l.step.max), fin(l.pre.p50), fin(l.pre.p99), fin(l.pre.max),
            fin(l.post.p50), fin(l.post.p99), fin(l.post.max), fin(l.interval.p50), fin(l.interval.p99),
            fin(l.interval.max), l.pre_count, l.post_count);
    out += ",\"post_parts\":{";
    if (l.waits) {
        appendf(out, "\"wait_ms\":{\"p50\":%.4f,\"p99\":%.4f,", fin(l.wait.p50), fin(l.wait.p99));
        detail::part(out, l.wait_part);
        out += "},\"windows_ms\":{";
        detail::part(out, l.windows);
        out += "},";
    }
    out += "\"subs\":";
    detail::named_parts(out, l.post_subs);
    out += "},\"pre_parts\":{\"subs\":";
    detail::named_parts(out, l.pre_subs);
    out += '}';
    if (l.has_worst) {
        appendf(out, ",\"worst\":{\"at\":\"%02u:%02u:%02u.%03u\",\"pre\":%.3f,\"step\":%.3f,\"post\":%.3f", l.worst_hour,
                l.worst_minute, l.worst_second, l.worst_milli, fin(l.worst_pre), fin(l.worst_step), fin(l.worst_post));
        if (l.waits) appendf(out, ",\"wait\":%.3f,\"windows\":%.3f", fin(l.worst_wait), fin(l.worst_windows));
        out += ",\"post_subs\":";
        detail::named_ms(out, l.worst_post_subs);
        out += ",\"pre_subs\":";
        detail::named_ms(out, l.worst_pre_subs);
        out += '}';
    }
    if (l.has_tasks) {
        out += ',';
        out += kiln::tasks_stats::format_window_json(l.tasks);
    }
    out += "}\n";
    return out;
}

}  // namespace kiln::tick::stats
