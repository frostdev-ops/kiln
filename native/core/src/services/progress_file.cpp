// SPDX-License-Identifier: MIT
// kiln.progress v2 (abi/services/kiln_progress.h): <game>/kiln/state/progress.json, what the start is doing. The model
// and the writer are progress_file.h (host-tested); this file gives them the process: the boot's identity, the QPC
// clock, the file (write_file_atomic: <file>.tmp, then a replacing rename) and one log line when a write fails.
// The writer lives for the process (never destroyed: its thread may outlive static destruction).
#include <atomic>
#include <cstdio>
#include <mutex>

#include "../common.h"
#include "../log.h"
#include "../progress_file.h"
#include "../progress_state.h"
#include "services/kiln_progress.h"

namespace kiln::progress_state {
namespace {

std::atomic<progress_file::Writer*> g_writer{nullptr};
double g_started = 0;

progress_file::Writer* writer() { return g_writer.load(std::memory_order_acquire); }

uint64_t clock_ms() {
    const double d = now_ms() - g_started;
    return d > 0 ? static_cast<uint64_t>(d) : 0;
}

}  // namespace

std::string utc_now_ms() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char b[40];
    std::snprintf(b, sizeof b, "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute,
                  t.wSecond, t.wMilliseconds);
    return b;
}

void begin(const std::string& kiln, const std::string& side, double started_qpc_ms, const std::string& started_utc,
           std::vector<std::string> plan) {
    if (writer()) return;  // one boot per process
    try {
        g_started = started_qpc_ms;
        progress_file::Identity who;
        who.pid = GetCurrentProcessId();
        who.kiln = kiln;
        who.side = side;
        who.started = started_utc;
        const std::filesystem::path file = paths().state / L"progress.json";
        auto* w = new progress_file::Writer(
            progress_file::Model(std::move(who), std::move(plan)),
            [file](const std::string& text) { return write_file_atomic(file, text); }, &clock_ms, true,
            [file] { KLOG_W("state: cannot write %s; the launcher shows less of this start (later writes are tried)", u8(file).c_str()); });
        g_writer.store(w, std::memory_order_release);
        w->first("start", 0);  // "start" began with the boot, not now
    } catch (...) {
    }
}

bool begun() { return writer() != nullptr; }

bool step(std::string_view id, std::string_view label, std::string_view detail, uint64_t done, uint64_t total) {
    auto* w = writer();
    if (!w) return false;
    try {
        return w->step(id, label, detail, done, total) == progress_file::Result::ok;
    } catch (...) {
        return false;
    }
}

void step_or_skip(std::string_view id, std::string_view label, std::string_view detail) {
    auto* w = writer();
    if (!w) return;
    try {
        if (w->step(id, label, detail) == progress_file::Result::ok) return;
        std::vector<std::string> dropped;
        if (w->skip_to(id, label, detail, &dropped) != progress_file::Result::ok) return;
        std::string list;
        for (const auto& d : dropped) list += (list.empty() ? "" : ", ") + d;
        KLOG_W("state: progress.json: this start has no step %s after all; taken out of its plan (a Kiln bug: the plan was made "
               "from other settings)", list.c_str());
    } catch (...) {
    }
}

void part(std::string_view label, uint64_t took_ms) {
    if (auto* w = writer()) try {
            w->part(label, took_ms);
        } catch (...) {
        }
}

void loading() {
    if (auto* w = writer()) try {
            w->loading();
        } catch (...) {
        }
}

void mark(uint32_t what) {
    if (auto* w = writer()) try {
            w->mark(static_cast<progress_file::Mark>(what));
        } catch (...) {
        }
}

void idle(std::string_view why) {
    if (auto* w = writer()) try {
            w->finish(progress_file::State::idle, why);
        } catch (...) {
        }
}

void failed(std::string_view why) {
    if (auto* w = writer()) try {
            w->finish(progress_file::State::failed, why);
        } catch (...) {
        }
}

void window_text(std::string_view text) {
    if (auto* w = writer()) try {
            w->detail_if_running("early-plugins", text);
        } catch (...) {
        }
}

KilnResult KILN_CALL api_step(KilnStr id, KilnStr label, KilnStr detail, uint64_t done, uint64_t total) {
    auto* w = writer();
    if (!w) return KILN_ERR_UNAVAILABLE;
    try {
        switch (w->step(sv(id), sv(label), sv(detail), done, total)) {
            case progress_file::Result::ok: return KILN_OK;
            case progress_file::Result::refused: return KILN_ERR_INVALID_ARGUMENT;
            default: return KILN_ERR_UNAVAILABLE;
        }
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL api_mark(uint32_t what) {
    if (what != KILN_PROGRESS_MARK_MENU && what != KILN_PROGRESS_MARK_SERVER_STARTED) return KILN_ERR_INVALID_ARGUMENT;
    auto* w = writer();
    if (!w) return KILN_ERR_UNAVAILABLE;
    try {
        return w->mark(static_cast<progress_file::Mark>(what)) == progress_file::Result::ok ? KILN_OK : KILN_ERR_UNAVAILABLE;
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

}  // namespace kiln::progress_state
