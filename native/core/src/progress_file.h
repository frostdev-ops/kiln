// SPDX-License-Identifier: MIT
// <game>/kiln/state/progress.json: what a start is doing, from kiln_boot until the game can be used (the launcher draws
// its progress bar and its time estimate from it). The contract is schema/state-progress.schema.json and
// docs/kiln/launcher/contracts.md; the call that lets services, plugins and Kiln.Host report is kiln.progress v2's
// `step` and `mark` (abi/services/kiln_progress.h).
//
// This header is the part that is not Win32, so that a host test runs it (native/core/tests/progress_file_test.cpp):
//  - Model: the plan, the steps begun so far (with when each began and what it took), the state, and the rules: a
//    step that is not the running one or the next in the plan is refused, nothing goes back, nothing changes after
//    ready, idle or failed.
//  - Writer: one writer for the file. A change of step or state is written at once; a change of label, detail or
//    count at most every 250 ms, and the last one is never lost (it is written when its time comes or with the next
//    step change). The writing happens on a thread of the writer's own (or in pump() for a test): a caller only
//    changes the model under a lock and never waits for the disk. A failed write is ignored (on_fail is told once).
//    After a final state (ready, idle, failed) is written, nothing is written again.
// services/progress_file.cpp gives it the clock, the file and the boot's identity.
#pragma once
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#include "nlohmann/json.hpp"
#include "progress_model.h"  // clip(): the same 255-byte UTF-8-safe cut as the window's text

namespace kiln::progress_file {

using progress::clip;

inline constexpr uint64_t kDetailEveryMs = 250;  // a change of label, detail or count: at most this often
inline constexpr uint64_t kPartMinMs = 500;      // a part shorter than this is not kept
inline constexpr size_t kMaxParts = 16;          // per step

enum class State { booting, loading, ready, idle, failed };
inline const char* state_name(State s) {
    switch (s) {
        case State::booting: return "booting";
        case State::loading: return "loading";
        case State::ready: return "ready";
        case State::idle: return "idle";
        default: return "failed";
    }
}

// What a call did. ok: taken. refused: not a step of the plan, or not the running one or the next. over: the file's
// life has ended (ready, idle, failed) or it never began.
enum class Result { ok, refused, over };

// kiln.progress `mark` (KILN_PROGRESS_MARK_*): facts the core cannot see itself.
enum class Mark : uint32_t {
    menu = 1,            // client: the main menu (or, a start straight into a world, a world frame) drew (kiln-ui)
    server_started = 2,  // server: Kiln.Host's ServerStarted (the world runs, the mods are loaded): kilnctl's signal
    world_started = 3,   // server: the server world's first step (kiln-core's tick; the signal when there is no .NET)
};

// The label a step has when the caller gives none.
inline const char* default_label(std::string_view id) {
    if (id == "start") return "Reading Kiln's settings and the profile";
    if (id == "native-early") return "Loading early native mods";
    if (id == "early-plugins") return "Running the early plugins";
    if (id == "signatures") return "Finding the game's functions";
    if (id == "services") return "Starting Kiln's services";
    if (id == "plugins") return "Starting plugins";
    if (id == "native") return "Starting native mods";
    if (id == "dotnet") return "Starting .NET";
    if (id == "mods") return "Loading the mods";
    if (id == "content") return "Applying the mods' content";
    if (id == "game") return "The game is loading";
    return "";
}

// A DLL by the name a player knows: Kiln's plugins for other loaders by the loader's name ("kiln-shroudforge.dll" ->
// "ShroudForge"), Kiln's own services and plugins without ".dll" ("kiln-vfx.dll" -> "kiln-vfx"), anything else as it is.
inline std::string module_name(std::string_view file) {
    std::string n(file);
    std::string lower = n;
    for (char& c : lower)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    if (lower.size() > 4 && lower.compare(lower.size() - 4, 4, ".dll") == 0) n.resize(n.size() - 4), lower.resize(lower.size() - 4);
    static const std::pair<const char*, const char*> kLoaders[] = {
        {"kiln-shroudforge", "ShroudForge"}, {"kiln-eml", "EML"},           {"kiln-emf", "EMF"},
        {"kiln-set", "SET"},                 {"kiln-shroudlink", "Shroudlink"}, {"kiln-enshwick", "Enshwick"},
        {"kiln-greeneye", "Greeneye"},       {"kiln-flame", "Flame"},       {"kiln-ember", "Ember"},
        {"kiln-shroudtopia", "Shroudtopia"},
    };
    for (const auto& [dll, name] : kLoaders)
        if (lower == dll) return name;
    return n;
}

struct Identity {
    uint32_t pid = 0;
    std::string kiln;     // "0.2.0-dev.27+20261113.2e75c1f"
    std::string side;     // client | server
    std::string started;  // RFC 3339 UTC with milliseconds
};

struct Part {
    std::string label;
    uint64_t took = 0;
};

struct Step {
    std::string id, label, detail;
    uint64_t done = 0, total = 0;
    uint64_t ms = 0;    // began, ms since started
    int64_t took = -1;  // -1: still running
    std::vector<Part> parts;
};

// Not thread-safe: Writer holds its lock around every call.
class Model {
public:
    enum class Change { none, later, now };  // nothing to write; within the 250 ms rule; at once

    Model(Identity who, std::vector<std::string> plan) : who_(std::move(who)), plan_(std::move(plan)) {}

    const std::vector<std::string>& plan() const { return plan_; }
    const std::vector<Step>& steps() const { return steps_; }
    State state() const { return state_; }
    bool final() const { return state_ == State::ready || state_ == State::idle || state_ == State::failed; }
    const std::string& running() const {
        static const std::string none;
        return steps_.empty() ? none : steps_.back().id;
    }

    int index_of(std::string_view id) const {
        for (size_t i = 0; i < plan_.size(); ++i)
            if (plan_[i] == id) return static_cast<int>(i);
        return -1;
    }

    // A step report. The running step: label (empty keeps it), detail (empty clears it) and done/total (both 0 keep
    // them) change. The next step of the plan: the running one ends (tookMs) and this one begins. Anything else is
    // refused.
    Result step(std::string_view id, std::string_view label, std::string_view detail, uint64_t done, uint64_t total,
                uint64_t now, Change* change) {
        *change = Change::none;
        if (final()) return Result::over;
        const int at = index_of(id);
        if (at < 0) return Result::refused;
        if (!steps_.empty() && steps_.back().id == id) {
            Step& s = steps_.back();
            bool changed = false;
            if (!label.empty() && s.label != clip(label)) s.label = clip(label), changed = true;
            if (s.detail != clip(detail)) s.detail = clip(detail), changed = true;
            if ((done || total) && (s.done != done || s.total != total)) s.done = done, s.total = total, changed = true;
            if (changed) *change = Change::later;
            return Result::ok;
        }
        const int next = steps_.empty() ? 0 : index_of(steps_.back().id) + 1;
        if (at != next) return Result::refused;
        end_running(now);
        Step s;
        s.id = std::string(id);
        s.label = label.empty() ? std::string(default_label(id)) : clip(label);
        s.detail = clip(detail);
        s.done = done;
        s.total = total;
        s.ms = now;
        steps_.push_back(std::move(s));
        *change = Change::now;
        try_ready(now);
        return Result::ok;
    }

    // A named piece of the running step that took `took` ms (kept from kPartMinMs, at most kMaxParts per step).
    Result part(std::string_view label, uint64_t took, Change* change) {
        *change = Change::none;
        if (final() || steps_.empty()) return Result::over;
        Step& s = steps_.back();
        if (took < kPartMinMs || s.parts.size() >= kMaxParts) return Result::ok;
        s.parts.push_back({clip(label), took});
        *change = Change::later;
        return Result::ok;
    }

    // Kiln's boot is complete: the game goes on loading.
    Result loading(uint64_t now, Change* change) {
        *change = Change::none;
        if (final()) return Result::over;
        if (state_ != State::booting) return Result::ok;
        state_ = State::loading;
        *change = Change::now;
        try_ready(now);
        return Result::ok;
    }

    Result mark(Mark m, uint64_t now, Change* change) {
        *change = Change::none;
        if (final()) return Result::over;
        if (m == Mark::menu) menu_ = true;
        else if (m == Mark::server_started) server_started_ = true;
        else if (m == Mark::world_started) world_started_ = true;
        else return Result::refused;
        if (try_ready(now)) *change = Change::now;
        return Result::ok;
    }

    // idle or failed, with why (one sentence). The running step ends; the file's life ends with it.
    Result finish(State s, std::string_view why, uint64_t now, Change* change) {
        *change = Change::none;
        if (final()) return Result::over;
        if (s != State::idle && s != State::failed && s != State::ready) return Result::refused;
        end_running(now);
        state_ = s;
        why_ = std::string(why);
        final_at_ = now;
        *change = Change::now;
        return Result::ok;
    }

    // Is the start usable? Kiln's boot is complete, Kiln's part is over (the plan's last step, "game", has begun: the mods
    // are loaded and their content applied), and the side's signal has come, before or after that (a signal that comes
    // early is remembered).
    bool ready_due() const {
        if (state_ != State::loading || steps_.empty() || plan_.empty() || steps_.back().id != plan_.back()) return false;
        return who_.side == "client" ? menu_ : index_of("dotnet") >= 0 ? server_started_ : world_started_;
    }

    // The JSON of the file as it stands at `now` (a final state: as it stood when it ended, so that the steps' tookMs
    // add up to atMs).
    std::string render(uint64_t now) const {
        using nlohmann::json;
        json steps = json::array();
        for (const Step& s : steps_) {
            json o = {{"id", s.id}, {"label", s.label}};
            if (!s.detail.empty()) o["detail"] = s.detail;
            if (s.total) o["done"] = s.done, o["total"] = s.total;
            o["ms"] = s.ms;
            if (s.took >= 0) o["tookMs"] = s.took;
            if (!s.parts.empty()) {
                json parts = json::array();
                for (const Part& p : s.parts) parts.push_back({{"label", p.label}, {"tookMs", p.took}});
                o["parts"] = parts;
            }
            steps.push_back(std::move(o));
        }
        json doc = {
            {"format", 1},
            {"pid", who_.pid},
            {"kiln", who_.kiln},
            {"side", who_.side},
            {"started", who_.started},
            {"atMs", final() ? final_at_ : now},
            {"state", state_name(state_)},
            {"plan", plan_},
            {"steps", steps},
            {"why", (state_ == State::idle || state_ == State::failed) && !why_.empty() ? json(why_) : json(nullptr)},
        };
        // invalid UTF-8 from a caller becomes U+FFFD instead of an exception
        return doc.dump(1, ' ', false, json::error_handler_t::replace) + "\n";
    }

private:
    void end_running(uint64_t now) {
        if (steps_.empty() || steps_.back().took >= 0) return;
        Step& s = steps_.back();
        s.took = static_cast<int64_t>(now >= s.ms ? now - s.ms : 0);
    }

    bool try_ready(uint64_t now) {
        if (!ready_due()) return false;
        end_running(now);
        state_ = State::ready;
        final_at_ = now;
        return true;
    }

    Identity who_;
    std::vector<std::string> plan_;
    std::vector<Step> steps_;
    State state_ = State::booting;
    std::string why_;
    uint64_t final_at_ = 0;
    bool menu_ = false, server_started_ = false, world_started_ = false;
};

// The one writer. `sink` writes the whole text to the file (atomically) and says whether it could; `clock` is ms since
// `started`. threaded: a thread of its own does the writing (the game); otherwise pump() does it (a test).
class Writer {
public:
    using Sink = std::function<bool(const std::string&)>;
    using Clock = std::function<uint64_t()>;

    Writer(Model model, Sink sink, Clock clock, bool threaded, std::function<void()> on_fail = {})
        : model_(std::move(model)), sink_(std::move(sink)), clock_(std::move(clock)), on_fail_(std::move(on_fail)) {
        if (threaded) thread_ = std::thread([this] { loop(); });
    }
    // The game never destroys its writer (services/progress_file.cpp); a test does: the thread ends first.
    ~Writer() {
        {
            std::lock_guard lk(mu_);
            stop_ = true;
        }
        cv_.notify_all();
        if (thread_.joinable()) thread_.join();
    }
    Writer(const Writer&) = delete;
    Writer& operator=(const Writer&) = delete;

    Result step(std::string_view id, std::string_view label = {}, std::string_view detail = {}, uint64_t done = 0,
                uint64_t total = 0) {
        return apply([&](uint64_t now, Model::Change* c) { return model_.step(id, label, detail, done, total, now, c); });
    }
    // The first step, which began before the writer existed (boot's start: at 0).
    Result first(std::string_view id, uint64_t at) {
        return apply([&](uint64_t, Model::Change* c) { return model_.step(id, {}, {}, 0, 0, at, c); });
    }
    // The detail of `id` when it is the running step; nothing otherwise (refused).
    Result detail_if_running(std::string_view id, std::string_view detail) {
        return apply([&](uint64_t now, Model::Change* c) {
            const Model& m = model_;
            if (m.running() != id) return Result::refused;
            return model_.step(id, {}, detail, 0, 0, now, c);
        });
    }
    Result part(std::string_view label, uint64_t took) {
        return apply([&](uint64_t, Model::Change* c) { return model_.part(label, took, c); });
    }
    Result loading() {
        return apply([&](uint64_t now, Model::Change* c) { return model_.loading(now, c); });
    }
    Result mark(Mark m) {
        return apply([&](uint64_t now, Model::Change* c) { return model_.mark(m, now, c); });
    }
    Result finish(State s, std::string_view why) {
        return apply([&](uint64_t now, Model::Change* c) { return model_.finish(s, why, now, c); });
    }

    // A test without the thread: writes what is due at the clock's time. Returns whether it wrote.
    bool pump() {
        std::unique_lock lk(mu_);
        return write_if_due(lk, clock_());
    }

    // Copies for a test.
    Model model() const {
        std::lock_guard lk(mu_);
        return model_;
    }
    uint64_t writes() const {
        std::lock_guard lk(mu_);
        return writes_;
    }
    uint64_t failures() const {
        std::lock_guard lk(mu_);
        return failures_;
    }
    bool done() const {
        std::lock_guard lk(mu_);
        return written_final_;
    }

private:
    template <class F>
    Result apply(F f) {
        std::lock_guard lk(mu_);
        if (written_final_) return Result::over;
        Model::Change c = Model::Change::none;
        const Result r = f(clock_(), &c);
        if (c == Model::Change::now) now_ = true, pending_ = true;
        else if (c == Model::Change::later) pending_ = true;
        if (c != Model::Change::none) cv_.notify_one();
        return r;
    }

    // When the pending change may be written: at once for a step or state change, else 250 ms after the last write.
    uint64_t due_at() const { return now_ || writes_ == 0 ? 0 : last_write_ + kDetailEveryMs; }

    bool write_if_due(std::unique_lock<std::mutex>& lk, uint64_t now) {
        if (!pending_ || written_final_ || now < due_at()) return false;
        const std::string text = model_.render(now);
        const bool final = model_.final();
        pending_ = false;
        now_ = false;
        // the disk is reached without the lock: a caller never waits for it (only this writer writes)
        lk.unlock();
        const bool ok = sink_ ? sink_(text) : false;
        lk.lock();
        ++writes_;
        last_write_ = now;
        if (!ok && failures_++ == 0 && on_fail_) on_fail_();
        if (final) written_final_ = true;
        return true;
    }

    void loop() {
        std::unique_lock lk(mu_);
        while (!written_final_ && !stop_) {
            if (!pending_) {
                cv_.wait(lk);
                continue;
            }
            const uint64_t now = clock_();
            const uint64_t due = due_at();
            if (now < due) {
                cv_.wait_for(lk, std::chrono::milliseconds(due - now));
                continue;
            }
            write_if_due(lk, now);
        }
    }

    mutable std::mutex mu_;
    std::condition_variable cv_;
    Model model_;
    Sink sink_;
    Clock clock_;
    std::function<void()> on_fail_;
    bool pending_ = false, now_ = false, written_final_ = false, stop_ = false;
    std::thread thread_;  // last: it starts once everything above is there
    uint64_t writes_ = 0, failures_ = 0, last_write_ = 0;
};

}  // namespace kiln::progress_file
