// SPDX-License-Identifier: MIT
// Manifest planning and an ABI call only. The archive reader/CPU renderer lives exclusively in GPL kiln-eml.
// Called after EMF settings/dependency selection, before DLL entry points, in Kiln's ready phase. The early asset
// writer (ShroudForge or kiln-eml) has finished before this phase. This call only reads game archives.
#include "emf_internal.h"
#include "portrait_bridge.h"
#include "v26_model.h"
#include "nlohmann/json.hpp"
#include <atomic>
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cwchar>
#include <fstream>
#include <set>
#include <thread>
#include <utility>
#ifdef KEMF_PORTRAIT_TEST
#include <fcntl.h>
#include <unistd.h>
#endif

namespace kemf {
namespace {
using json = nlohmann::json;
constexpr size_t kManifestLimit = 1u << 20, kAllManifests = 8u << 20, kRequestLimit = 256u << 10;
#ifdef KEMF_PORTRAIT_TEST
PortraitDrawFn g_draw = nullptr;
#endif
bool regular(const std::filesystem::path &p, bool directory) {
    std::error_code ec;
    auto s = std::filesystem::symlink_status(p, ec);
    if (ec || std::filesystem::is_symlink(s) ||
        (directory ? !std::filesystem::is_directory(s) : !std::filesystem::is_regular_file(s)))
        return false;
#ifndef KEMF_PORTRAIT_TEST
    const DWORD a = GetFileAttributesW(p.c_str());
    if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_REPARSE_POINT))
        return false;
#endif
    return true;
}
bool bounded_read(const std::filesystem::path &p, size_t limit, std::string *text) {
    if (!regular(p, false))
        return false;
    std::error_code ec;
    if (std::filesystem::file_size(p, ec) > limit || ec)
        return false;
    std::ifstream f(p, std::ios::binary);
    if (!f)
        return false;
    text->resize(limit + 1);
    f.read(text->data(), static_cast<std::streamsize>(text->size()));
    const auto n = static_cast<size_t>(f.gcount());
    if (n > limit)
        return false;
    text->resize(n);
    return !f.bad();
}
bool save_attempts(const std::filesystem::path &path, const std::string &bytes) {
    static std::atomic<uint64_t> sequence{0};
    auto tmp = path;
    tmp += to_wide(".new-" + std::to_string(now_ms()) + "-" + std::to_string(++sequence));
#ifdef KEMF_PORTRAIT_TEST
    const int fd = ::open(tmp.c_str(), O_CREAT | O_EXCL | O_WRONLY | O_NOFOLLOW, 0600);
    if (fd < 0)
        return false;
    const bool ok = ::write(fd, bytes.data(), bytes.size()) == static_cast<ssize_t>(bytes.size()) && ::fsync(fd) == 0;
    ::close(fd);
    std::error_code ec;
    if (ok)
        std::filesystem::rename(tmp, path, ec);
    if (!ok || ec) {
        std::filesystem::remove(tmp, ec);
        return false;
    }
    return true;
#else
    const HANDLE h = CreateFileW(tmp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                 FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return false;
    DWORD wrote = 0;
    const bool ok = WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &wrote, nullptr) &&
                    wrote == bytes.size() && FlushFileBuffers(h);
    CloseHandle(h);
    if (ok && MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        return true;
    DeleteFileW(tmp.c_str());
    return false;
#endif
}
bool cache_dirs(std::filesystem::path *result) {
    auto p = P.game;
    for (const wchar_t *part : {L"emf", L"cache", L"portraits"}) {
        p /= part;
        std::error_code ec;
        const auto s = std::filesystem::symlink_status(p, ec);
        if (ec && ec != std::errc::no_such_file_or_directory)
            return false;
        if (std::filesystem::exists(s)) {
            if (!regular(p, true))
                return false;
        } else if (!std::filesystem::create_directory(p, ec) || ec)
            return false;
    }
    *result = p;
    return true;
}
bool guid(std::string_view s) {
    if (s.size() != 36)
        return false;
    for (size_t i = 0; i < s.size(); ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        const unsigned char c = s[i];
        if (dash ? c != '-' : !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            return false;
    }
    return true;
}
json fact(const std::filesystem::path &p) {
    std::error_code ec;
    const auto size = std::filesystem::file_size(p, ec);
    if (ec)
        return nullptr;
    const auto time = std::filesystem::last_write_time(p, ec);
    if (ec)
        return nullptr;
    return json::array({path_utf8(p.filename()), size, time.time_since_epoch().count()});
}
PortraitDrawFn renderer() {
#ifdef KEMF_PORTRAIT_TEST
    return g_draw;
#else
    const HMODULE h = GetModuleHandleW(L"kiln-eml.dll");
    return h ? reinterpret_cast<PortraitDrawFn>(GetProcAddress(h, "kiln_eml_portraits")) : nullptr;
#endif
}
json wanted() {
    json items = json::array();
    std::set<std::string> keys;
    size_t total = 0, manifests = 0, entries = 0;
    uint64_t pixels = 0;
    for (const auto &m : g_mods) {
        if ((m->status != EMF_MOD_PENDING && m->status != EMF_MOD_SCRIPT) || !m->enabled)
            continue;
        if (++manifests > 128 || total >= kAllManifests || items.size() == 32 || entries >= 4096)
            break;
        if (!regular(m->folder, true))
            continue;
        std::string text;
        if (!bounded_read(m->folder / L"mod.json", std::min(kManifestLimit, kAllManifests - total), &text))
            continue;
        total += text.size();
        json manifest;
        try {
            manifest = json::parse(text);
        } catch (...) {
            continue;
        }
        if (!manifest.is_object() || !manifest.contains("portraits") || !manifest["portraits"].is_array())
            continue;
        for (const auto &entry : manifest["portraits"]) {
            if (++entries > 4096)
                break;
            if (items.size() == 32)
                break;
            try {
                if (!entry.is_object())
                    continue;
                const std::string key = entry.value("key", std::string{});
                if (!v26::portrait_key_ok(key) || keys.contains(lower(key)))
                    continue;
                const std::string t = entry.value("template", std::string{}),
                                  model = entry.value("model", std::string{});
                if (t.empty() == model.empty() || !guid(t.empty() ? model : t))
                    continue;
                auto integer = [&](const char *name, int fallback) {
                    if (!entry.contains(name))
                        return fallback;
                    if (!entry[name].is_number_integer())
                        return -1;
                    if (entry[name].is_number_unsigned()) {
                        const auto n = entry[name].get<uint64_t>();
                        return n <= 512 ? static_cast<int>(n) : -1;
                    }
                    const auto n = entry[name].get<int64_t>();
                    return n >= 0 && n <= 512 ? static_cast<int>(n) : -1;
                };
                const int frames = integer("frames", 24), size = integer("size", 256);
                if (frames < 1 || frames > 64 || size < 8 || size > 512)
                    continue;
                const uint64_t cost = static_cast<uint64_t>(frames) * size * size;
                if (pixels + cost > 32ull * 1024 * 1024)
                    continue;
                json p = {{"key", key}, {"frames", frames}, {"size", size}};
                p[t.empty() ? "model" : "template"] = t.empty() ? model : t;
                items.push_back(std::move(p));
                keys.insert(lower(key));
                pixels += cost;
            } catch (...) {
                continue;
            }
        }
    }
    return items;
}
// The picture as the renderer left it: its index.json (size, time) and whether every frame file is there. Compared to
// the facts saved after the last run; a picture changed or removed since then is handed to the renderer again.
json picture_fact(const std::filesystem::path &folder, int frames) {
    json index = fact(folder / L"index.json");
    if (index.is_null() || !regular(folder, true))
        return nullptr;
    for (int f = 0; f < frames; ++f) {
        wchar_t name[16];
        swprintf(name, 16, L"%02d.png", f);
        if (!regular(folder / name, false))
            return nullptr;
    }
    return index;
}

// The renderer's request-level codes (gpl/kiln-eml lib.rs). Busy and an I/O error on the cache are transient. Every other
// refusal of the whole request lasts until the inputs change or the cache does ("blocked", below).
constexpr int64_t kBadRequest = 2, kCacheBusy = 5, kCacheFull = 6, kCacheIo = 7;

// The names of the cache's folders (not the ".kiln-" files, which every run rewrites). A refusal that depends on what is
// in the cache is asked again once the user has cleaned it, as the log line advises.
std::string cache_token(const std::filesystem::path &cache) {
    std::vector<std::string> names;
    std::error_code ec;
    std::filesystem::directory_iterator it(cache, ec), end;
    while (!ec && it != end && names.size() <= 4096) {
        const auto name = path_utf8(it->path().filename());
        if (!name.empty() && name[0] != '.')
            names.push_back(name);
        it.increment(ec);
    }
    std::sort(names.begin(), names.end());
    uint64_t hash = 1469598103934665603ull;
    for (const auto &name : names) {
        for (const unsigned char c : name)
            hash = (hash ^ c) * 1099511628211ull;
        hash = (hash ^ '\n') * 1099511628211ull;
    }
    return std::to_string(names.size()) + ":" + std::to_string(hash);
}

struct Job {
    PortraitDrawFn draw;
    std::filesystem::path memory, cache;
    std::string stamp, request;
    json ask;
    std::set<std::string> failed;
};
std::mutex g_work_mu;
std::condition_variable g_work_cv;
bool g_working = false;
std::set<std::string> g_pending; // lower-case keys the running job draws
std::thread::id g_boot_thread;   // the thread that started the job: the only one image_load may hold up
std::chrono::steady_clock::time_point g_deadline; // when image_load stops waiting for the job, all calls together
#ifdef KEMF_PORTRAIT_TEST
int g_wait_ms = 120000;
#else
constexpr int g_wait_ms = 120000;
#endif

std::atomic<void (*)()> g_when_drawn{nullptr}; // portraits_when_drawn

void finish_job() {
    {
        std::lock_guard lock(g_work_mu);
        g_working = false;
        g_pending.clear();
    }
    g_work_cv.notify_all();
    if (const auto fn = g_when_drawn.load()) {
        try {
            fn();
        } catch (...) {
            logf(KILN_LOG_WARN, "EMF portraits: loading the drawn pictures failed; the game continues");
        }
    }
}

void draw_job(Job &job) {
    std::vector<char> reply(1u << 20);
    size_t written = 0;
    int32_t rc = -1;
    struct Call {
        PortraitDrawFn draw;
        const std::string *request;
        std::vector<char> *reply;
        size_t *written;
        int32_t *result;
    } call{job.draw, &job.request, &reply, &written, &rc};
    unsigned long code = 0;
    uintptr_t address = 0;
    const bool ok = seh_call(
        [](void *p) {
            auto &c = *static_cast<Call *>(p);
            *c.result = c.draw(reinterpret_cast<const uint8_t *>(c.request->data()), c.request->size(), c.reply->data(),
                               c.reply->size(), c.written);
        },
        &call, &code, &address);
    json answer;
    bool valid = false;
    if (ok && rc == 0 && written > 0 && written <= reply.size() && reply[written - 1] == '\0') {
        try {
            answer = json::parse(std::string_view(reply.data(), written - 1));
            valid = answer.is_object() && answer.contains("items") && answer["items"].is_array();
        } catch (...) {
        }
    }
    const int64_t status = valid && answer.contains("code") && answer["code"].is_number_integer()
                               ? answer["code"].get<int64_t>()
                               : -1;
    const std::string message = valid && answer.contains("message") && answer["message"].is_string()
                                    ? answer["message"].get<std::string>()
                                    : std::string{};
    // Cache contention is transient. A later start retries it instead of remembering a failed picture. (kiln-eml before
    // dev.21 reported it as code 2; its message is the only way to tell.)
    if (status == kCacheBusy ||
        (status == kBadRequest && message.rfind("another portrait generator", 0) == 0)) {
        logf(KILN_LOG_WARN, "EMF portraits: another generator holds the cache this start; retrying next time");
        return;
    }
    // An I/O error on the cache (a sharing violation, a drive briefly gone) is transient too: nothing is remembered. A
    // kiln-eml without code 7 reports it as code 2 with the OS error text ("... (os error N)").
    if (status == kCacheIo || (status == kBadRequest && message.find("(os error ") != std::string::npos)) {
        logf(KILN_LOG_WARN, "EMF portraits: could not read the picture cache this start (%s); retrying next time",
             message.c_str());
        return;
    }
    // A lasting refusal of the whole request (a full cache, an entry the renderer will not touch): said once here, and
    // remembered while the inputs and the cache's folders stay as they are. The user who cleans the cache gets a retry.
    if (status == kCacheFull || status == kBadRequest) {
        if (status == kCacheFull)
            logf(KILN_LOG_WARN,
                 "EMF portraits: the picture cache is full (%s); delete unused folders in %s to draw new pictures",
                 message.c_str(), path_utf8(job.cache).c_str());
        else
            logf(KILN_LOG_WARN, "EMF portraits: the renderer refused the request (%s); not asked again until the inputs or "
                                "the folders in %s change",
                 message.c_str(), path_utf8(job.cache).c_str());
        const json blocked = {{"format", 1},
                              {"stamp", job.stamp},
                              {"failed", job.failed},
                              {"current", json::object()},
                              {"blocked", cache_token(job.cache)}};
        if (!save_attempts(job.memory, blocked.dump() + "\n"))
            logf(KILN_LOG_WARN, "EMF portraits: could not save attempt memory");
        return;
    }
    size_t drawn = 0, current = 0;
    json pictures = json::object();
    for (const auto &i : job.ask) {
        const auto key = i["key"].get<std::string>();
        bool good = false;
        if (valid)
            for (const auto &a : answer["items"]) {
                if (a.is_object() && a.value("key", std::string{}) == key) {
                    const auto st = a.value("status", std::string{});
                    good = st == "drawn" || st == "current";
                    drawn += st == "drawn";
                    current += st == "current";
                    break;
                }
            }
        if (good) {
            job.failed.erase(key);
            json f = picture_fact(job.cache / to_wide(key), i["frames"].get<int>());
            if (!f.is_null())
                pictures[key] = std::move(f);
        } else
            job.failed.insert(key);
    }
    json next = {{"format", 1}, {"stamp", job.stamp}, {"failed", job.failed}, {"current", pictures}};
    if (!save_attempts(job.memory, next.dump() + "\n"))
        logf(KILN_LOG_WARN, "EMF portraits: could not save attempt memory");
    logf(KILN_LOG_INFO, "EMF portraits: %zu drawn, %zu current, %zu unavailable (offline CPU renderer)", drawn, current,
         job.failed.size());
}

void run_job(Job &job) {
    try {
        draw_job(job);
    } catch (...) {
        logf(KILN_LOG_WARN, "EMF portraits: drawing failed; the game continues");
    }
    finish_job();
}

void prepare() {
    if (g_side != KILN_SIDE_CLIENT)
        return; // a dedicated server never opens a mod picture
    // An external bootstrap has its own gate/worker. Unlike a hosted early plugin, its ready ordering is not ours.
    if (!P.state.empty()) {
        const auto sf = P.state / L"shroudforge.json";
        std::error_code ec;
        if (std::filesystem::exists(std::filesystem::symlink_status(sf, ec))) {
            std::string text;
            if (!bounded_read(sf, kManifestLimit, &text))
                return;
            const auto state = json::parse(text);
            if (!state.is_object() || !state.contains("mode") || !state["mode"].is_string())
                return;
            if (state["mode"] == "self-hosted") {
                logf(KILN_LOG_INFO,
                     "EMF portraits: external startup writer owns preparation; cached pictures remain usable");
                return;
            }
        }
    }
    json items = wanted();
    if (items.empty())
        return;
    const auto draw = renderer();
    if (!draw) {
        logf(KILN_LOG_WARN, "EMF portraits: kiln-eml has no portrait renderer; cached pictures remain usable");
        return;
    }
    std::filesystem::path cache;
    if (!cache_dirs(&cache)) {
        logf(KILN_LOG_WARN, "EMF portraits: cache path is not a regular directory");
        return;
    }
    json facts = {{"format", 1},
                  {"items", items},
                  {"exe", fact(P.game / L"enshrouded.exe")},
                  {"kfc", fact(P.game / L"enshrouded.kfc")},
                  {"resources", fact(P.game / L"enshrouded.kfc_resources")},
                  {"renderer", fact(P.kiln / L"plugins" / L"kiln-eml.dll")}};
    json dats = json::array();
    std::error_code ec;
    size_t count = 0, visited = 0;
    for (const auto &e : std::filesystem::directory_iterator(P.game, ec)) {
        if (++visited > 8192)
            return;
        const auto n = e.path().filename().wstring();
        if (n.rfind(L"enshrouded_", 0) == 0 && e.path().extension() == L".dat") {
            if (++count > 4096)
                return;
            dats.push_back(fact(e.path()));
        }
    }
    std::sort(dats.begin(), dats.end());
    facts["dats"] = std::move(dats);
    const std::string stamp = facts.dump();
    const auto memory = cache / L".kiln-attempts.json";
    json old;
    std::string text;
    if (std::filesystem::exists(std::filesystem::symlink_status(memory, ec)) && !regular(memory, false))
        return;
    if (bounded_read(memory, kRequestLimit, &text)) {
        try {
            old = json::parse(text);
        } catch (...) {
        }
    }
    std::set<std::string> failed;
    json current = json::object(); // key -> facts of the picture's index.json and its frames, as last drawn or checked
    std::string blocked;           // the cache_token when the renderer refused the whole request
    if (old.is_object() && old.contains("stamp") && old["stamp"].is_string() &&
        old["stamp"].get<std::string>() == stamp && old.contains("failed") && old["failed"].is_array()) {
        for (const auto &k : old["failed"])
            if (k.is_string())
                failed.insert(k.get<std::string>());
        if (old.contains("current") && old["current"].is_object())
            current = old["current"];
        if (old.contains("blocked") && old["blocked"].is_string())
            blocked = old["blocked"].get<std::string>();
    }
    json ask = json::array();
    for (const auto &i : items)
        if (!failed.contains(i["key"].get<std::string>()))
            ask.push_back(i);
    if (ask.empty()) {
        logf(KILN_LOG_INFO, "EMF portraits: remembered %zu unavailable picture(s); inputs unchanged", failed.size());
        return;
    }
    // The cheap up-to-date check: same inputs (stamp) and every picture's index and frames as this planner last saw
    // them. Then the renderer, which opens the game's archives, is not called at all.
    bool all_current = true;
    for (const auto &i : ask) {
        const auto key = i["key"].get<std::string>();
        if (!current.contains(key) || current[key].is_null() ||
            current[key] != picture_fact(cache / to_wide(key), i["frames"].get<int>())) {
            all_current = false;
            break;
        }
    }
    if (all_current) {
        logf(KILN_LOG_INFO, "EMF portraits: %zu picture(s) current, %zu remembered unavailable; inputs unchanged",
             ask.size(), failed.size());
        return;
    }
    if (!blocked.empty() && blocked == cache_token(cache)) {
        logf(KILN_LOG_INFO, "EMF portraits: the renderer refused the last request and the folders in %s are unchanged; "
                            "not asking again",
             path_utf8(cache).c_str());
        return;
    }
    const std::string request = json({{"game_dir", path_utf8(P.game)},
                                      {"file_name", "enshrouded"},
                                      {"cache_dir", path_utf8(cache)},
                                      {"items", ask}})
                                    .dump();
    if (request.size() > kRequestLimit || stamp.size() > kRequestLimit / 2)
        return;
    // Drawing reads the game's archives and can take seconds: it runs on its own thread so the game's start goes on.
    // image_load("portrait:<key>") of a picture still being drawn waits for it (portraits_wait).
    auto job = std::make_shared<Job>(Job{draw, memory, cache, stamp, request, std::move(ask), std::move(failed)});
    {
        std::lock_guard lock(g_work_mu);
        if (g_working)
            return;
        g_working = true;
        g_boot_thread = std::this_thread::get_id();
        g_deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(g_wait_ms);
        g_pending.clear();
        for (const auto &i : job->ask)
            g_pending.insert(lower(i["key"].get<std::string>()));
    }
    try {
        std::thread([job] { run_job(*job); }).detach();
    } catch (...) {
        run_job(*job); // no thread: draw here, as before
    }
}
} // namespace
void portraits_on_ready() {
    try {
        prepare();
    } catch (...) {
        logf(KILN_LOG_WARN, "EMF portraits: preparation failed; the game continues");
    }
}
bool portraits_drawing(const std::string &key) {
    std::lock_guard lock(g_work_mu);
    return g_working && g_pending.contains(lower(key));
}
void portraits_when_drawn(void (*fn)()) { g_when_drawn.store(fn); }
bool portraits_wait(const std::string &key) {
    std::unique_lock lock(g_work_mu);
    if (!g_working || !g_pending.contains(lower(key)))
        return true;
    // Only the thread that started the drawing waits: it would have drawn the picture itself before, so a mod that loads
    // its pictures while it starts gets them. Any other thread (tick, render, a mod's own) is never held up: the call
    // answers "not yet" and the mod can ask again.
    if (std::this_thread::get_id() != g_boot_thread)
        return false;
    // bounded for all calls together: a renderer that hangs must not hold the start for 120 s per call
    if (g_work_cv.wait_until(lock, g_deadline, [] { return !g_working; }))
        return true;
    static bool said = false;
    if (!std::exchange(said, true))
        logf(KILN_LOG_WARN, "EMF portraits: still drawing after %d s; pictures being drawn are not ready this start",
             g_wait_ms / 1000);
    return false;
}
#ifdef KEMF_PORTRAIT_TEST
void portraits_test_export(PortraitDrawFn fn) { g_draw = fn; }
void portraits_test_wait_ms(int ms) { g_wait_ms = ms; }
void portraits_test_join() {
    std::unique_lock lock(g_work_mu);
    g_work_cv.wait(lock, [] { return !g_working; });
}
#endif
} // namespace kemf
