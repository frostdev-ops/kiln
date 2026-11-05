// SPDX-License-Identifier: MIT
#include "log.h"

#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

#include "guard.h"
#include "registry.h"

namespace kiln::log {
namespace {

struct Entry {
    bool game = false;
    int level = KILN_LOG_INFO;
    SYSTEMTIME time{};
    std::string source;
    std::string text;
};

constexpr size_t kMaxQueued = 20000;

std::mutex g_mutex;
std::condition_variable g_cv;
std::deque<Entry> g_queue;
std::FILE* g_file = nullptr;
std::atomic<int> g_level{KILN_LOG_INFO};
std::atomic<uint64_t> g_game_seen{0}, g_game_dropped{0}, g_core_dropped{0};
bool g_started = false;

char level_letter(int level) {
    switch (level) {
        case KILN_LOG_ERROR: return 'E';
        case KILN_LOG_WARN: return 'W';
        case KILN_LOG_INFO: return 'I';
        case KILN_LOG_DEBUG: return 'D';
        default: return 'T';
    }
}

void write_entry(const Entry& e) {
    if (!g_file) return;
    std::fprintf(g_file, "[%c %02u:%02u:%02u.%03u] [%s] %.*s\n", level_letter(e.level), e.time.wHour, e.time.wMinute,
                 e.time.wSecond, e.time.wMilliseconds, e.source.c_str(), static_cast<int>(e.text.size()),
                 e.text.data());
}

void dispatch_game_line(const Entry& e, std::vector<std::shared_ptr<reg::LogSub>>& subs, uint64_t& version) {
    const uint64_t v = reg::version();
    if (v != version) {
        subs = reg::log_subs();
        version = v;
    }
    for (auto& s : subs) {
        if (!s->live.load(std::memory_order_acquire)) continue;
        unsigned long code = 0;
        if (!guard::log_line(s->fn, e.level, KilnStr{e.text.data(), e.text.size()}, s->user, &code)) {
            s->live.store(false);
            reg::remove(s->handle);
            Entry err;
            GetLocalTime(&err.time);
            err.level = KILN_LOG_ERROR;
            err.source = "kiln";
            char buf[160];
            std::snprintf(buf, sizeof buf, "log subscriber of '%s' faulted (0x%08lx); removed",
                          reg::owner_name(s->owner).c_str(), code);
            err.text = buf;
            write_entry(err);
        }
    }
}

void writer_main() {
    SetThreadDescription(GetCurrentThread(), L"kiln-log");
    std::vector<std::shared_ptr<reg::LogSub>> subs;
    uint64_t version = ~0ull;
    std::deque<Entry> batch;
    for (;;) {
        {
            std::unique_lock lock(g_mutex);
            g_cv.wait(lock, [] { return !g_queue.empty(); });
            batch.swap(g_queue);
        }
        for (const Entry& e : batch) {
            if (e.game)
                dispatch_game_line(e, subs, version);
            else
                write_entry(e);
        }
        batch.clear();
        if (g_file) std::fflush(g_file);
    }
}

void enqueue(Entry&& e) {
    {
        std::lock_guard lock(g_mutex);
        if (g_queue.size() >= kMaxQueued) {
            (e.game ? g_game_dropped : g_core_dropped).fetch_add(1, std::memory_order_relaxed);
            return;
        }
        g_queue.push_back(std::move(e));
    }
    g_cv.notify_one();
}

// The game's listener ABI (svn 1285601): fn(u8 level, u8 flags, const {const char* ptr; u32 len}* line, void* user).
// Called on any thread while the game holds its log spinlock: copy and return, never log from here.
struct GameStr {
    const char* ptr;
    uint32_t len;
};

int map_game_level(uint8_t level) {
    switch (level) {  // game: 0 fatal, 1 error, 2 warning, 3 info, 4 verbose, 5 debug
        case 0:
        case 1: return KILN_LOG_ERROR;
        case 2: return KILN_LOG_WARN;
        case 3: return KILN_LOG_INFO;
        case 4: return KILN_LOG_DEBUG;
        default: return KILN_LOG_TRACE;
    }
}

void __cdecl game_listener(uint8_t level, uint8_t /*flags*/, const GameStr* line, void* /*user*/) {
    if (!line || !line->ptr) return;
    g_game_seen.fetch_add(1, std::memory_order_relaxed);
    if (reg::log_subs_empty_hint()) return;
    size_t n = line->len;
    while (n && (line->ptr[n - 1] == '\n' || line->ptr[n - 1] == '\r')) --n;
    Entry e;
    e.game = true;
    e.level = map_game_level(level);
    e.text.assign(line->ptr, n);
    enqueue(std::move(e));
}

}  // namespace

void start() {
    if (g_started) return;
    g_started = true;
    std::error_code ec;
    std::filesystem::create_directories(paths().logs, ec);
    const auto current = paths().logs / L"kiln.log";
    std::filesystem::rename(current, paths().logs / L"kiln.prev.log", ec);
    g_file = _wfopen(current.c_str(), L"w");
    std::thread(writer_main).detach();
}

void set_level(int level) { g_level.store(level); }

void write(int level, std::string_view source, std::string_view text) {
    if (level > g_level.load(std::memory_order_relaxed)) return;
    Entry e;
    GetLocalTime(&e.time);
    e.level = level;
    e.source.assign(source);
    e.text.assign(text);
    enqueue(std::move(e));
}

void writef(int level, const char* source, const char* fmt, ...) {
    if (level > g_level.load(std::memory_order_relaxed)) return;
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    write(level, source, std::string_view(buf, std::min<size_t>(static_cast<size_t>(n), sizeof buf - 1)));
}

bool install_game_listener(uintptr_t add_log_listener) {
    using AddFn = void(__cdecl*)(void* fn, void* user);
    if (!add_log_listener) return false;
    reinterpret_cast<AddFn>(add_log_listener)(reinterpret_cast<void*>(&game_listener), nullptr);
    return true;
}

uint64_t game_lines_seen() { return g_game_seen.load(); }
uint64_t game_lines_dropped() { return g_game_dropped.load(); }

}  // namespace kiln::log
