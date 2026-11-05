// SPDX-License-Identifier: MIT
#include "tracker.h"

#include <windows.h>

#include <fstream>
#include <sstream>

#include "nlohmann/json.hpp"

namespace kspawn::tracker {
namespace {

using json = nlohmann::ordered_json;

std::mutex g_mu;
std::vector<Record> g_records;
std::filesystem::path g_file;
std::string g_key, g_save;
uint64_t g_next = 1;
bool g_open = false;

json to_json(const Record& r) {
    json j;
    j["serial"] = r.serial;
    j["template"] = r.tmpl.text();
    j["name"] = r.name;
    j["spawn_time"] = r.spawn_time;
    j["pos"] = {r.pos[0], r.pos[1], r.pos[2]};
    j["spawn_pos"] = {r.spawn_pos[0], r.spawn_pos[1], r.spawn_pos[2]};
    j["spawned"] = r.spawned_unix;
    j["flags"] = r.flags;
    j["tag"] = r.tag;
    j["origin"] = r.origin;
    j["missed_sessions"] = r.missed_sessions;
    j["last_entity"] = r.entity;  // informational: ids change after a restart
    return j;
}

bool from_json(const json& j, Record* r) {
    try {
        r->serial = j.at("serial").get<uint64_t>();
        if (!Guid::parse(j.at("template").get<std::string>(), &r->tmpl)) return false;
        r->name = j.value("name", "");
        r->spawn_time = j.value("spawn_time", int64_t{0});
        const auto& p = j.at("pos");
        const auto& sp = j.contains("spawn_pos") ? j["spawn_pos"] : p;
        for (int i = 0; i < 3; ++i) {
            r->pos[i] = p.at(i).get<double>();
            r->spawn_pos[i] = sp.at(i).get<double>();
        }
        r->spawned_unix = j.value("spawned", int64_t{0});
        r->flags = j.value("flags", 0u);
        r->tag = j.value("tag", "");
        r->origin = j.value("origin", "");
        r->missed_sessions = j.value("missed_sessions", 0u);
        r->entity = 0;
        r->live = false;
        return true;
    } catch (...) {
        return false;
    }
}

void write_locked() {
    if (!g_open || g_file.empty()) return;
    json doc;
    doc["format"] = 1;
    doc["comment"] = "Entities Kiln spawned in this world (kiln.spawn). Remove them with Spawn.CleanupTracked / the "
                     "admin cleanup; do not edit while the server runs.";
    doc["world"] = g_key;
    doc["save"] = g_save;
    doc["next_serial"] = g_next;
    json list = json::array();
    for (const Record& r : g_records) list.push_back(to_json(r));
    doc["entities"] = std::move(list);
    std::error_code ec;
    std::filesystem::create_directories(g_file.parent_path(), ec);
    std::filesystem::path tmp = g_file;
    tmp += L".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        if (!f) return;
        f << doc.dump(1, '\t', false, json::error_handler_t::replace) << '\n';
        if (!f) return;
    }
    MoveFileExW(tmp.c_str(), g_file.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
}

}  // namespace

void open(const std::filesystem::path& file, const std::string& world_key, const std::string& save_file) {
    std::lock_guard lock(g_mu);
    g_file = file;
    g_key = world_key;
    g_save = save_file;
    g_records.clear();
    g_next = 1;
    std::ifstream f(file, std::ios::binary);
    if (f) {
        try {
            std::stringstream ss;
            ss << f.rdbuf();
            const json doc = json::parse(ss.str());
            g_next = doc.value("next_serial", uint64_t{1});
            for (const auto& j : doc.value("entities", json::array())) {
                Record r;
                if (from_json(j, &r)) {
                    g_records.push_back(std::move(r));
                    if (g_records.back().serial >= g_next) g_next = g_records.back().serial + 1;
                }
            }
        } catch (...) {
            // A broken sidecar is kept aside, never overwritten silently.
            std::filesystem::path bad = file;
            bad += L".broken";
            std::error_code ec;
            std::filesystem::copy_file(file, bad, std::filesystem::copy_options::overwrite_existing, ec);
        }
    }
    g_open = true;
}

void rekey(const std::filesystem::path& file, const std::string& world_key, const std::string& save_file) {
    std::lock_guard lock(g_mu);
    const std::filesystem::path old = g_file;
    g_file = file;
    g_key = world_key;
    g_save = save_file;
    write_locked();
    std::error_code ec;
    if (!old.empty() && old != file) std::filesystem::remove(old, ec);
}

bool opened() {
    std::lock_guard lock(g_mu);
    return g_open;
}

std::string world_key() {
    std::lock_guard lock(g_mu);
    return g_key;
}

std::filesystem::path file() {
    std::lock_guard lock(g_mu);
    return g_file;
}

uint64_t next_serial() {
    std::lock_guard lock(g_mu);
    return g_next++;
}

void add(Record r) {
    std::lock_guard lock(g_mu);  // (all writers run on the tick thread; the lock is for readers elsewhere)
    g_records.push_back(std::move(r));
    write_locked();
}

bool remove_entity(uint32_t id) {
    std::lock_guard lock(g_mu);
    for (auto it = g_records.begin(); it != g_records.end(); ++it) {
        if (it->live && it->entity == id) {
            g_records.erase(it);
            write_locked();
            return true;
        }
    }
    return false;
}

void remove_serial(uint64_t serial) {
    std::lock_guard lock(g_mu);
    std::erase_if(g_records, [&](const Record& r) { return r.serial == serial; });
    write_locked();
}

std::vector<Record> snapshot() {
    std::lock_guard lock(g_mu);
    return g_records;
}

bool find_live(uint32_t id, Record* out) {
    std::lock_guard lock(g_mu);
    for (const Record& r : g_records)
        if (r.live && r.entity == id) return *out = r, true;
    return false;
}

size_t count() {
    std::lock_guard lock(g_mu);
    return g_records.size();
}

bool at(size_t i, Record* out) {
    std::lock_guard lock(g_mu);
    if (i >= g_records.size()) return false;
    *out = g_records[i];
    return true;
}

void replace(std::vector<Record>&& list, bool dirty) {
    std::lock_guard lock(g_mu);
    g_records = std::move(list);
    if (dirty) write_locked();
}

void save_now() {
    std::lock_guard lock(g_mu);
    write_locked();
}

}  // namespace kspawn::tracker
