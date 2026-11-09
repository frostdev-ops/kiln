// SPDX-License-Identifier: MIT
// Manifest planning and an ABI call only. The archive reader/CPU renderer lives exclusively in GPL kiln-eml.
// Called after EMF settings/dependency selection, before DLL entry points, in Kiln's ready phase. The early asset
// writer (ShroudForge or kiln-eml) has finished before this phase. This call only reads game archives.
#include "emf_internal.h"
#include "portrait_bridge.h"
#include "v26_model.h"
#include "nlohmann/json.hpp"
#include <algorithm>
#include <fstream>
#include <set>
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
    if (old.is_object() && old.contains("stamp") && old["stamp"].is_string() &&
        old["stamp"].get<std::string>() == stamp && old.contains("failed") && old["failed"].is_array())
        for (const auto &k : old["failed"])
            if (k.is_string())
                failed.insert(k.get<std::string>());
    json ask = json::array();
    for (const auto &i : items)
        if (!failed.contains(i["key"].get<std::string>()))
            ask.push_back(i);
    if (ask.empty()) {
        logf(KILN_LOG_INFO, "EMF portraits: remembered %zu unavailable picture(s); inputs unchanged", failed.size());
        return;
    }
    const std::string request = json({{"game_dir", path_utf8(P.game)},
                                      {"file_name", "enshrouded"},
                                      {"cache_dir", path_utf8(cache)},
                                      {"items", ask}})
                                    .dump();
    if (request.size() > kRequestLimit || stamp.size() > kRequestLimit / 2)
        return;
    std::vector<char> reply(1u << 20);
    size_t written = 0;
    int32_t rc = -1;
    struct Call {
        PortraitDrawFn draw;
        const std::string *request;
        std::vector<char> *reply;
        size_t *written;
        int32_t *result;
    } call{draw, &request, &reply, &written, &rc};
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
    size_t drawn = 0, current = 0;
    // Cache contention is transient. A later start retries it instead of remembering a failed picture.
    if (valid && answer.contains("code") && answer["code"].is_number_integer() && answer["code"].get<int64_t>() == 2) {
        logf(KILN_LOG_WARN, "EMF portraits: cache is unavailable for this start; retrying next time");
        return;
    }
    for (const auto &i : ask) {
        const auto key = i["key"].get<std::string>();
        bool good = false;
        if (valid)
            for (const auto &a : answer["items"]) {
                if (a.is_object() && a.value("key", std::string{}) == key) {
                    const auto status = a.value("status", std::string{});
                    good = status == "drawn" || status == "current";
                    drawn += status == "drawn";
                    current += status == "current";
                    break;
                }
            }
        if (good)
            failed.erase(key);
        else
            failed.insert(key);
    }
    json next = {{"format", 1}, {"stamp", stamp}, {"failed", failed}};
    if (!save_attempts(memory, next.dump() + "\n"))
        logf(KILN_LOG_WARN, "EMF portraits: could not save attempt memory");
    logf(KILN_LOG_INFO, "EMF portraits: %zu drawn, %zu current, %zu unavailable (offline CPU renderer)", drawn, current,
         failed.size());
}
} // namespace
void portraits_on_ready() {
    try {
        prepare();
    } catch (...) {
        logf(KILN_LOG_WARN, "EMF portraits: preparation failed; the game continues");
    }
}
#ifdef KEMF_PORTRAIT_TEST
void portraits_test_export(PortraitDrawFn fn) { g_draw = fn; }
#endif
} // namespace kemf
