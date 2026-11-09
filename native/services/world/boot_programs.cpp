// SPDX-License-Identifier: MIT
// Reserve real scheduler declarations before worlds finalize. Managed OnLoad only binds callbacks later.
#include <filesystem>
#include <fstream>
#include <cstdio>
#include "boot_programs.h"
#include "world_internal.h"
#include "services/kiln_profile.h"
namespace kw {
namespace {
namespace fs = std::filesystem;
fs::path utf8_path(std::string_view text) { return fs::path(std::u8string_view(reinterpret_cast<const char8_t*>(text.data()),text.size())); }
std::string bounded_file(const fs::path& file) {
    std::error_code error;
    if (!fs::is_regular_file(file, error) || error || fs::is_symlink(file, error) || error) throw std::runtime_error("not a regular file");
    std::ifstream input(file, std::ios::binary);
    if (!input) throw std::runtime_error("cannot read file");
    std::string text(kBootProgramBytes + 1, '\0');
    input.read(text.data(), static_cast<std::streamsize>(text.size()));
    const auto size = input.gcount();
    if (input.bad() || size > static_cast<std::streamsize>(kBootProgramBytes)) throw std::runtime_error("file exceeds 64 KiB or read failed");
    text.resize(static_cast<size_t>(size));
    return text;
}
// Manifest JSONC: replace comments and trailing commas with spaces, leaving strings and line numbers intact.
std::string manifest_json(std::string text) {
    bool string = false, escaped = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (string) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') string = false; continue; }
        if (c == '"') { string = true; continue; }
        if (c == '/' && i + 1 < text.size() && text[i + 1] == '/') {
            text[i++] = ' '; text[i] = ' ';
            while (i + 1 < text.size() && text[i + 1] != '\n') text[++i] = ' ';
        } else if (c == '/' && i + 1 < text.size() && text[i + 1] == '*') {
            text[i++] = ' '; text[i] = ' ';
            bool ended = false;
            while (++i < text.size()) {
                if (text[i] == '*' && i + 1 < text.size() && text[i + 1] == '/') { text[i++] = ' '; text[i] = ' '; ended = true; break; }
                if (text[i] != '\n' && text[i] != '\r') text[i] = ' ';
            }
            if (!ended) throw std::runtime_error("unterminated manifest comment");
        }
    }
    string = escaped = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (string) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') string = false; continue; }
        if (c == '"') { string = true; continue; }
        if (c != ',') continue;
        size_t next = i + 1;
        while (next < text.size() && (text[next] == ' ' || text[next] == '\t' || text[next] == '\r' || text[next] == '\n')) ++next;
        if (next < text.size() && (text[next] == ']' || text[next] == '}')) text[i] = ' ';
    }
    return text;
}
struct Candidate { fs::path directory; BootCandidate policy; };
void reserve(const Candidate& candidate) {
    const auto file = candidate.directory / L"kiln.programs.json";
    std::error_code error;
    if (!fs::exists(file, error)) return;
    const auto programs = parse_boot_programs(bounded_file(file));
    // Parse the complete file before accepting the first descriptor, so syntax errors never partly reserve it.
    for (const auto& program : programs) {
        char name[64];
        std::snprintf(name, sizeof name, "kiln_managed_%08x_%08x", fnv1a(candidate.policy.id), fnv1a(program.key));
        std::array<std::vector<KilnStr>,16> names;
        KilnNames lists[16]{};
        for (size_t i = 0; i < names.size(); ++i) {
            for (const auto& item : program.lists[i]) names[i].push_back(kstr(item));
            lists[i] = {names[i].data(), static_cast<uint32_t>(names[i].size()), 0};
        }
        KilnProgramDesc desc{};
        desc.size = sizeof desc; desc.flags = program.flags; desc.name = kstr(name);
        desc.each = [](KilnProgramCtx*, uint64_t*, KilnEntity, void*) {};
        KilnNames* dest[] = {&desc.tpl,&desc.tpl_optional,&desc.read,&desc.read_optional,&desc.write,&desc.write_optional,
            &desc.singletons_read,&desc.singletons_write,&desc.other_tpl,&desc.other_read,&desc.other_write,
            &desc.with,&desc.without,&desc.any_of,&desc.events_in,&desc.events_out};
        for (size_t i = 0; i < names.size(); ++i) *dest[i] = lists[i];
        const auto result = reserve_program(candidate.policy.id, program.key, &desc);
        if (result == KILN_OK) KW_I("managed program %s/%s reserved as %s", candidate.policy.id.c_str(), program.key.c_str(), name);
        else KW_W("managed program %s/%s could not reserve (%d)", candidate.policy.id.c_str(), program.key.c_str(), result);
    }
}
} // namespace
void boot_programs_init() {
    if (!F.programs) return;
    std::vector<Candidate> candidates;
    bool dropins = true;
    const auto* profile = static_cast<const KilnProfileApi*>(g_api->service(kstr(KILN_PROFILE_SERVICE),1));
    if (profile && profile->header.size >= sizeof(KilnProfileApi)) {
        const auto mode = profile->mode();
        if (mode == KILN_PROFILE_NO_MODS || mode == KILN_PROFILE_INVALID) return;
        if (mode == KILN_PROFILE_ACTIVE || mode == KILN_PROFILE_ONCE) {
            dropins = (profile->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
            const auto count = profile->count(KILN_PROFILE_KIND_KILN);
            if (count > 1024) { KW_W("too many profile mods for managed program discovery"); return; }
            for (uint32_t i = 0; i < count; ++i) {
                KilnProfileMod mod{}; mod.size = sizeof mod;
                if (profile->get(KILN_PROFILE_KIND_KILN,i,&mod) != KILN_OK) continue;
                candidates.push_back({utf8_path(sv(mod.path)), {std::string(sv(mod.id)), mod.enabled != 0, true, true, candidates.size()}});
            }
        }
    }
    if (dropins) {
        char path[4096]; size_t length = 0;
        if (g_api->path(KILN_PATH_MODS,path,sizeof path,&length) == KILN_OK && length < sizeof path) {
            std::error_code error;
            std::vector<fs::path> dirs;
            for (auto it = fs::directory_iterator(utf8_path(std::string_view(path,length)),error); !error && it != fs::directory_iterator(); it.increment(error)) {
                if (dirs.size() >= 1024) { KW_W("too many drop-in folders for managed program discovery"); return; }
                if (it->is_directory(error) && !it->is_symlink(error)) dirs.push_back(it->path());
            }
            std::sort(dirs.begin(),dirs.end());
            for (const auto& dir : dirs) candidates.push_back({dir, {"",!fs::exists(dir/L".disabled",error),false,true,candidates.size()}});
        }
    }
    std::vector<BootCandidate> policies;
    for (auto& item : candidates) {
        try {
            const auto manifest = nlohmann::json::parse(manifest_json(bounded_file(item.directory/L"kiln.mod.json")));
            const auto id = manifest.value("id",std::string());
            const auto side = manifest.value("side",std::string("server"));
            if (!item.policy.profile) item.policy.id = id;
            else if (item.policy.id != id) item.policy.enabled = false;
            item.policy.server = side == "server" || side == "both";
        } catch (...) { item.policy.enabled = false; }
        policies.push_back(item.policy);
    }
    for (size_t token : select_boot_candidates(policies,dropins)) {
        try { reserve(candidates[token]); }
        catch (const std::exception& error) { KW_W("managed programs for %s refused: %s",candidates[token].policy.id.c_str(),error.what()); }
    }
}
} // namespace kw
