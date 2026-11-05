// SPDX-License-Identifier: MIT
// Strings, files, logging and the "not supported" bookkeeping.
#include <cstdio>
#include <fstream>
#include <map>
#include <set>
#include <sstream>

#include "emf_internal.h"

namespace kemf {

std::string vformat(const char* fmt, va_list ap) {
    if (!fmt) return {};
    char small[512];
    va_list copy;
    va_copy(copy, ap);
    const int n = std::vsnprintf(small, sizeof small, fmt, copy);
    va_end(copy);
    if (n < 0) return fmt;
    if (static_cast<size_t>(n) < sizeof small) return std::string(small, static_cast<size_t>(n));
    std::string big(static_cast<size_t>(n) + 1, '\0');
    std::vsnprintf(big.data(), big.size(), fmt, ap);
    big.resize(static_cast<size_t>(n));
    return big;
}

std::string format(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    std::string s = vformat(fmt, ap);
    va_end(ap);
    return s;
}

std::string to_utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring to_wide(std::string_view s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::string lower(std::string_view s) {
    std::string o(s);
    for (char& c : o)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return o;
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

void copy_cstr(char* dst, size_t cap, std::string_view s) {
    if (!dst || cap == 0) return;
    size_t n = s.size() < cap - 1 ? s.size() : cap - 1;
    // never cut inside a UTF-8 sequence
    while (n > 0 && n < s.size() && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;
    std::memcpy(dst, s.data(), n);
    dst[n] = '\0';
}

bool read_file(const std::filesystem::path& p, std::string* out) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return false;
    std::ostringstream ss;
    ss << f.rdbuf();
    *out = ss.str();
    return true;
}

bool write_file_atomic(const std::filesystem::path& p, std::string_view data) {
    std::error_code ec;
    std::filesystem::create_directories(p.parent_path(), ec);
    std::filesystem::path tmp = p;
    tmp += L".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        if (!f) return false;
        f.write(data.data(), static_cast<std::streamsize>(data.size()));
        f.flush();
        if (!f) return false;
    }
    return MoveFileExW(tmp.c_str(), p.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
}

std::string path_utf8(const std::filesystem::path& p) { return to_utf8(p.wstring()); }

uint64_t now_ms() { return GetTickCount64(); }

uint64_t qpc_ns() {
    static LARGE_INTEGER f = [] {
        LARGE_INTEGER x;
        QueryPerformanceFrequency(&x);
        return x;
    }();
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return static_cast<uint64_t>(static_cast<double>(t.QuadPart) * 1e9 / static_cast<double>(f.QuadPart));
}

void account(Mod* m, uint64_t ns) {
    if (!m) return;
    m->tick_ns.fetch_add(ns, std::memory_order_relaxed);
    m->tick_calls.fetch_add(1, std::memory_order_relaxed);
    uint64_t cur = m->tick_max_ns.load(std::memory_order_relaxed);
    while (ns > cur && !m->tick_max_ns.compare_exchange_weak(cur, ns, std::memory_order_relaxed)) {
    }
}

uint32_t fnv1a(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}

// ---------------------------------------------------------------------------------------------- logging ---------
void logf(int level, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    const std::string s = vformat(fmt, ap);
    va_end(ap);
    g_api->log(g_owner, level, kstr(s));
}

void mod_log(const Mod* m, int level, std::string_view text) {
    // strip one trailing newline (printf-style lines from mods often end with one)
    while (!text.empty() && (text.back() == '\n' || text.back() == '\r')) text.remove_suffix(1);
    if (m && m->owner) {
        g_api->log(m->owner, level, kstr(text));
        return;
    }
    const std::string s = std::string("[") + (m ? m->id : "?") + "] " + std::string(text);
    g_api->log(g_owner, level, kstr(s));
}

void mod_logf(const Mod* m, int level, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    const std::string s = vformat(fmt, ap);
    va_end(ap);
    mod_log(m, level, s);
}

namespace {
std::mutex g_once_mutex;
std::set<std::string> g_once;                       // "<mod>|<fn>|<why>"
std::map<std::string, std::set<std::string>> g_unsupported;  // fn -> mods
std::map<std::string, uint64_t> g_calls;            // fn -> count
}  // namespace

void unsupported(const Mod* m, const char* fn, const char* why) {
    const std::string key = std::string(mod_name(m)) + "|" + fn;
    {
        std::lock_guard lock(g_once_mutex);
        g_unsupported[fn].insert(mod_name(m));
        if (!g_once.insert(key).second) return;
    }
    if (why)
        mod_logf(m, KILN_LOG_WARN, "emf: %s called %s, not supported (%s)", mod_name(m), fn, why);
    else
        mod_logf(m, KILN_LOG_WARN, "emf: %s called %s, not supported", mod_name(m), fn);
    mark_status_dirty();
}

void refuse(const Mod* m, const char* fn, const char* why, bool once) {
    if (once) {
        std::lock_guard lock(g_once_mutex);
        if (!g_once.insert(std::string(mod_name(m)) + "|" + fn + "|" + why).second) return;
    }
    mod_logf(m, KILN_LOG_WARN, "%s refused: %s", fn, why);
}

void note_call(const char* fn) {
    std::lock_guard lock(g_once_mutex);
    ++g_calls[fn];
}

// status-file helpers (plugin.cpp)
std::string unsupported_json();
std::string unsupported_json() {
    std::lock_guard lock(g_once_mutex);
    std::string s = "{";
    bool first = true;
    for (const auto& [fn, mods] : g_unsupported) {
        s += first ? "" : ",";
        first = false;
        s += "\"" + fn + "\":[";
        bool f2 = true;
        for (const auto& m : mods) {
            s += (f2 ? "\"" : ",\"") + m + "\"";
            f2 = false;
        }
        s += "]";
    }
    return s + "}";
}
std::string calls_json();
std::string calls_json() {
    std::lock_guard lock(g_once_mutex);
    std::string s = "{";
    bool first = true;
    for (const auto& [fn, n] : g_calls) {
        s += (first ? "\"" : ",\"") + fn + "\":" + std::to_string(n);
        first = false;
    }
    return s + "}";
}

}  // namespace kemf
