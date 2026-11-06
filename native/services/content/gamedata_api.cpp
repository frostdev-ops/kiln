// SPDX-License-Identifier: MIT
// kiln.content gamedata: boot, world hooks and the kiln.gamedata C table (native/core/abi/services/kiln_gamedata.h).
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>

#include "gamedata.h"

namespace kc {
namespace gd {

namespace {
std::atomic<bool> g_ready{false};
std::atomic<uint64_t> g_gen{0};
std::mutex g_detail_mu;
std::string g_detail = "waiting for a world";

void set_detail(const std::string& s) {
    std::lock_guard lock(g_detail_mu);
    g_detail = s;
}

bool on_thread() { return g_api->on_world_thread(active_kind()); }

KilnResult common(uint32_t section) {
    if (!valid_section(section)) return KILN_ERR_INVALID_ARGUMENT;
    if (!on_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_ready.load() || !greg(section).available) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}

std::string str(KilnStr s) { return s.ptr && s.len ? std::string(s.ptr, s.len) : std::string(); }

void copy_out(const std::string& s, char* buf, size_t cap, size_t* out_len) {
    if (out_len) *out_len = s.size();
    if (!buf || !cap) return;
    const size_t n = std::min(s.size(), cap - 1);
    std::memcpy(buf, s.data(), n);
    buf[n] = 0;
}

KilnResult copy_res(const std::string& s, char* buf, size_t cap, size_t* len) {
    copy_out(s, buf, cap, len);
    return cap > s.size() ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

void fill(uint32_t section, const GEntry& e, KilnGdEntry* out) {
    out->size = sizeof *out;
    out->section = section;
    out->id = e.id;
    out->flags = (entry_modified(section, e.id) ? KILN_GD_MODIFIED : 0) | (e.clone ? KILN_GD_CLONE : 0);
    out->name = KilnStr{e.name.data(), e.name.size()};
    out->address = e.addr;
    std::memcpy(out->guid, e.guid, 16);
}

// a single leaf at (section, id, path)
KilnResult leaf_at(uint32_t section, uint32_t id, const std::string& path, Resolved* r, Field* f, const GEntry** e) {
    const GRegistry& R = greg(section);
    *e = R.get(id);
    if (!*e) return KILN_ERR_NOT_FOUND;
    std::vector<Resolved> rs;
    std::string err;
    if (!resolve(R.root, root_of(section, **e), path, &rs, &err)) return KILN_ERR_NOT_FOUND;
    if (rs.size() != 1 || !rs[0].node.t || !leaf_of(rs[0].node.t, f)) return KILN_ERR_INVALID_ARGUMENT;
    *r = rs[0];
    return KILN_OK;
}

}  // namespace

uint64_t generation() { return g_gen.load(); }

std::string status_json() {
    gjson j;
    j["ready"] = g_ready.load();
    {
        std::lock_guard lock(g_detail_mu);
        j["detail"] = g_detail;
    }
    j["resources"] = res_via();
    gjson secs = gjson::object();
    if (g_ready.load()) {
        std::lock_guard lock(store_mutex());
        for (uint32_t s = KILN_GD_TEMPLATES; s <= KILN_GD_ITEMS; ++s) {
            const GRegistry& r = greg(s);
            secs[section_name(s)] = {{"available", r.available}, {"count", r.entries.size()}, {"changed", changed_entries(s, nullptr)},
                                     {"type", r.qtype}};
        }
    }
    j["sections"] = secs;
    j["externals"] = gjson::parse(externals_json(), nullptr, false);
    j["waiting"] = gjson::parse(waiting_json(), nullptr, false);
    j["generation"] = g_gen.load();
    j["clones"] = clones_status();
    return j.dump();
}

bool dump_section(uint32_t section, const std::string& key, const std::string& path, std::string* written, std::string* err) {
    namespace fs = std::filesystem;
    const GRegistry& R = greg(section);
    std::string p = path.empty() ? data_dir() + "\\content\\gamedata-" + section_name(section) + ".json" : path;
    std::error_code ec;
    const fs::path fp = fs::path(std::u8string(p.begin(), p.end()));
    fs::create_directories(fp.parent_path(), ec);
    std::ofstream f(fp, std::ios::binary | std::ios::trunc);
    if (!f) {
        *err = "cannot write " + p;
        return false;
    }
    std::vector<const GEntry*> es;
    if (key.empty()) for (const GEntry& e : R.entries) es.push_back(&e);
    else es = R.select(key);
    f << "{\"kiln\": \"gamedata-dump/1\", \"section\": \"" << section_name(section) << "\", \"type\": \"" << R.qtype
      << "\", \"count\": " << es.size() << ",\n \"entries\": [\n";
    for (size_t i = 0; i < es.size(); ++i) {
        const GEntry& e = *es[i];
        gjson ej;
        char b[16];
        std::snprintf(b, sizeof b, "0x%08x", e.id);
        ej["id"] = b;
        ej["name"] = e.name;
        ej["key"] = entry_key(section, e);
        if (R.root == Root::Template) {
            gjson comps = gjson::object();
            enumerate(Root::Template, root_of(section, e), "", 0, [](const std::string&, const Node&, const Field&) {});
            // components as objects by type name
            std::vector<Resolved> rs;
            std::string rerr;
            const TField* cf = R.type ? R.type->field("components") : nullptr;
            uintptr_t d = 0;
            uint32_t n = 0;
            if (cf && blob_array(e.addr + cf->offset, &d, &n)) {
                std::map<std::string, int> seen;
                for (uint32_t k = 0; k < n; ++k) {
                    uint32_t h = 0, sz = 0;
                    uintptr_t cd = 0;
                    if (!blob_variant(d + k * 12, &h, &cd, &sz)) continue;
                    const TType* ct = type_hash(h);
                    if (!ct) continue;
                    const int c = seen[lower(ct->brief)]++;
                    comps[c ? ct->brief + "#" + std::to_string(c) : ct->brief] = node_json(Node{ct->resolved(), cd}, 10);
                }
            }
            ej["components"] = comps;
        } else {
            ej["value"] = node_json(root_of(section, e), 12);
        }
        f << "  " << ej.dump(-1, ' ', false, gjson::error_handler_t::replace) << (i + 1 < es.size() ? ",\n" : "\n");
    }
    f << " ]}\n";
    *written = p;
    return static_cast<bool>(f);
}

}  // namespace gd

using namespace gd;

// ============================================================================================= the table ======

namespace {

KilnResult KILN_CALL t_status(char* buf, size_t cap, size_t* len) {
    PhaseTimer pt(PH_STATUS);
    return copy_res(status_json(), buf, cap, len);
}

uint32_t KILN_CALL t_count(uint32_t section) {
    if (common(section) != KILN_OK) return 0;
    std::lock_guard lock(store_mutex());
    return static_cast<uint32_t>(greg(section).entries.size());
}

KilnResult KILN_CALL t_entry_at(uint32_t section, uint32_t index, KilnGdEntry* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    const GRegistry& r = greg(section);
    if (index >= r.entries.size()) return KILN_ERR_NOT_FOUND;
    fill(section, r.entries[index], out);
    return KILN_OK;
}

KilnResult KILN_CALL t_find(uint32_t section, KilnStr key, KilnGdEntry* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    const GEntry* e = greg(section).lookup(str(key));
    if (!e) return KILN_ERR_NOT_FOUND;
    fill(section, *e, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_get(uint32_t section, uint32_t id, KilnStr path, KilnContentValue* cur, KilnContentValue* orig) {
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    Resolved r;
    Field f;
    const GEntry* e = nullptr;
    if (KilnResult rc = leaf_at(section, id, str(path), &r, &f, &e)) return rc;
    uint8_t b[16] = {}, o[16] = {};
    const uintptr_t at = r.node.addr + f.offset;
    rd(at, b, f.bytes);
    if (!store_original(at, f.bytes, o)) std::memcpy(o, b, 16);
    if (cur) *cur = decode(f, b);
    if (orig) *orig = decode(f, o);
    return KILN_OK;
}

KilnResult KILN_CALL t_get_json(uint32_t section, uint32_t id, KilnStr path, char* buf, size_t cap, size_t* len) {
    if (len) *len = 0;
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    const GRegistry& R = greg(section);
    const GEntry* e = R.get(id);
    if (!e) return KILN_ERR_NOT_FOUND;
    const std::string p = str(path);
    gjson out;
    if (p.empty()) {
        out = {{"path", ""}, {"type", R.qtype}, {"value", node_json(root_of(section, *e), 12)}, {"address", e->addr}};
        return copy_res(out.dump(-1, ' ', false, gjson::error_handler_t::replace), buf, cap, len);
    }
    std::vector<Resolved> rs;
    std::string err;
    if (!resolve(R.root, root_of(section, *e), p, &rs, &err)) {
        out = {{"error", err}};
        copy_out(out.dump(), buf, cap, len);
        return KILN_ERR_NOT_FOUND;
    }
    gjson arr = gjson::array();
    for (const Resolved& r : rs) {
        gjson x;
        x["path"] = r.path;
        if (!r.node.t) {
            x["absent"] = true;  // a config the struct does not have (setting it adds it)
            arr.push_back(x);
            continue;
        }
        x["type"] = r.node.t->name;
        x["address"] = r.node.addr;
        Field f;
        if (leaf_of(r.node.t, &f)) {
            uint8_t b[16] = {}, o[16] = {};
            const uintptr_t at = r.node.addr + f.offset;
            rd(at, b, f.bytes);
            if (!store_original(at, f.bytes, o)) std::memcpy(o, b, 16);
            x["value"] = leaf_json(f, b);
            x["original"] = leaf_json(f, o);
            x["writable"] = !identity_field(r.path.substr(r.path.find_last_of(".]") == std::string::npos ? 0 : r.path.find_last_of(".]") + 1)) &&
                            (f.vt != KILN_CV_GUID || !f.ref || true);
            if (f.en) {
                gjson ev = gjson::array();
                for (auto& [n, v] : f.en->values) ev.push_back(n);
                x[f.enum_kind == KILN_CONTENT_FLAGS ? "flags" : "enum"] = ev;
            }
            if (f.vt == KILN_CV_TIME) x["unit"] = "seconds";
        } else {
            x["value"] = node_json(r.node, 10);
        }
        if (r.config) x["config"] = true;
        arr.push_back(x);
    }
    out = arr.size() == 1 ? arr[0] : arr;
    return copy_res(out.dump(-1, ' ', false, gjson::error_handler_t::replace), buf, cap, len);
}

KilnResult KILN_CALL t_set_json(KilnOwner owner, uint32_t section, uint32_t id, KilnStr path, KilnStr value, char* report,
                                size_t cap, size_t* len) {
    if (len) *len = 0;
    if (KilnResult rc = common(section)) return rc;
    const gjson v = gjson::parse(str(value), nullptr, false);
    std::vector<std::string> errs;
    uint32_t n = 0;
    if (v.is_discarded()) errs.push_back("the value is not JSON (strings need quotes)");
    else {
        std::lock_guard lock(store_mutex());
        const GEntry* e = greg(section).get(id);
        if (!e) return KILN_ERR_NOT_FOUND;
        std::string ferr;
        store_flush(&ferr);
        const std::string p = str(path);
        n = apply_value(owner, next_code_seq(), section, *e, p, v, owner_label(owner) + ": " + section_name(section) + " " + e->name + "." + p,
                        &errs);
        after_changes();
    }
    gjson r = {{"applied", n}, {"errors", errs}};
    copy_out(r.dump(), report, cap, len);
    return n ? KILN_OK : KILN_ERR_INVALID_ARGUMENT;
}

KilnResult KILN_CALL t_set(KilnOwner owner, uint32_t section, uint32_t id, KilnStr path, const KilnContentValue* v) {
    if (!v) return KILN_ERR_INVALID_ARGUMENT;
    gjson j;
    switch (v->type) {
        case KILN_CV_BOOL: j = v->i != 0; break;
        case KILN_CV_INT: j = v->i; break;
        case KILN_CV_FLOAT: j = v->f; break;
        case KILN_CV_TIME: j = v->f != 0 ? v->f : static_cast<double>(v->i) / 1e9; break;
        case KILN_CV_GUID: j = ref_text(v->guid); break;
        default: return KILN_ERR_INVALID_ARGUMENT;
    }
    const std::string s = j.dump();
    char rep[512];
    return t_set_json(owner, section, id, path, KilnStr{s.data(), s.size()}, rep, sizeof rep, nullptr);
}

KilnResult KILN_CALL t_reset(KilnOwner owner, uint32_t section, uint32_t id, KilnStr path) {
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    const GEntry* e = greg(section).get(id);
    if (!e) return KILN_ERR_NOT_FOUND;
    const bool had = reset_path(owner, section, *e, str(path));
    after_changes();
    return had ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_fields(uint32_t section, uint32_t id, KilnStr prefix, uint32_t limit, char* buf, size_t cap, size_t* len) {
    if (len) *len = 0;
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    const GRegistry& R = greg(section);
    const GEntry* e = R.get(id);
    if (!e) return KILN_ERR_NOT_FOUND;
    gjson a = gjson::array();
    enumerate(R.root, root_of(section, *e), str(prefix), limit ? limit : 20000, [&](const std::string& p, const Node& n, const Field& f) {
        uint8_t b[16] = {};
        rd(n.addr + f.offset, b, f.bytes);
        gjson x = {{"path", p}, {"type", f.type_name}, {"value", leaf_json(f, b)}, {"writable", f.writable}};
        if (store_has(n.addr + f.offset)) {
            uint8_t o[16] = {};
            if (store_original(n.addr + f.offset, f.bytes, o)) x["original"] = leaf_json(f, o);
        }
        a.push_back(x);
    });
    return copy_res(a.dump(-1, ' ', false, gjson::error_handler_t::replace), buf, cap, len);
}

KilnResult KILN_CALL t_dump(uint32_t section, KilnStr key, KilnStr path, char* written, size_t cap, size_t* len) {
    if (len) *len = 0;
    if (KilnResult rc = common(section)) return rc;
    std::lock_guard lock(store_mutex());
    std::string w, err;
    const auto t0 = std::chrono::steady_clock::now();
    if (!dump_section(section, str(key), str(path), &w, &err)) {
        LOGE("gamedata dump: %s", err.c_str());
        return KILN_ERR_INTERNAL;
    }
    LOGI("gamedata: dumped %s to %s in %.0f ms", section_name(section), w.c_str(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
    copy_out(w, written, cap, len);
    return KILN_OK;
}

KilnResult KILN_CALL t_configs(char* buf, size_t cap, size_t* len) {
    if (!g_ready.load()) return KILN_ERR_UNAVAILABLE;
    return copy_res(configs_json().dump(), buf, cap, len);
}

KilnResult KILN_CALL t_register_section(KilnOwner owner, KilnStr name, KilnGdSectionFn apply, KilnGdRevertFn revert, void* user) {
    External x;
    x.name = str(name);
    x.owner = owner, x.apply = apply, x.revert = revert, x.user = user;
    return register_external(x);
}

KilnResult KILN_CALL t_unregister_section(KilnStr name) { return unregister_external(str(name)); }

KilnResult KILN_CALL t_define(KilnOwner owner, uint32_t section, KilnStr json, char* report, size_t cap, size_t* len);
KilnResult KILN_CALL t_definitions(char* buf, size_t cap, size_t* len);

const KilnGamedataApi kTable = {
    {sizeof(KilnGamedataApi), KILN_GAMEDATA_VERSION},
    &t_status, &t_count, &t_entry_at, &t_find, &t_get, &t_get_json, &t_set, &t_set_json, &t_reset, &t_fields, &t_dump,
    &t_configs, &t_register_section, &t_unregister_section, &t_define, &t_definitions,
};

KilnResult KILN_CALL t_define(KilnOwner owner, uint32_t section, KilnStr json, char* report, size_t cap, size_t* len) {
    if (len) *len = 0;
    std::string rep;
    const KilnResult rc = clones_define(owner, section, str(json), false, "", &rep);
    if (rep.size() >= cap) {  // the report without its listing entry, rather than cut short
        gjson j = gjson::parse(rep, nullptr, false);
        if (j.is_object()) j.erase("entry"), rep = j.dump(-1, ' ', false, gjson::error_handler_t::replace);
    }
    copy_out(rep, report, cap, len);
    return rc;
}

KilnResult KILN_CALL t_definitions(char* buf, size_t cap, size_t* len) {
    PhaseTimer pt(PH_LISTING);
    return copy_res(clones_json(), buf, cap, len);
}

}  // namespace

// ============================================================================================= hooks ============

void gd_boot() {
    changes_boot();
    clones_boot();
    if (g_api->register_service(g_owner, KilnStr{KILN_GAMEDATA_SERVICE, std::strlen(KILN_GAMEDATA_SERVICE)}, &kTable) != KILN_OK)
        LOGW("gamedata: registering kiln.gamedata failed");
}

void gd_world_ready(uintptr_t world, uint32_t kind) {
    PhaseTimer pt(PH_GD_READY);
    const auto t0 = std::chrono::steady_clock::now();
    std::string why;
    bool ok = false;
    {
        std::lock_guard lock(store_mutex());
        ok = gd::build_sections(world, &why);
    }
    if (!ok) {
        set_detail(why);
        LOGW("gamedata unavailable: %s", why.c_str());
        return;
    }
    clones_world_ready(world, kind);
    configs_build();
    g_ready.store(true);
    g_gen.fetch_add(1);
    set_detail("ready");
    const GRegistry& T = greg(KILN_GD_TEMPLATES);
    LOGI("gamedata ready (%s, through %s) in %.0f ms: %zu templates, %zu arsenals, %zu sub-sequences, %zu buffs, %zu perks, "
         "%zu skill nodes, %zu items",
         kind == KILN_WORLD_CLIENT ? "client world" : "server world", res_via(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count(), T.entries.size(),
         greg(KILN_GD_ARSENALS).entries.size(), greg(KILN_GD_SEQUENCES).entries.size(), greg(KILN_GD_BUFFS).entries.size(),
         greg(KILN_GD_PERKS).entries.size(), greg(KILN_GD_SKILLS).entries.size(), greg(KILN_GD_ITEMS).entries.size());
    if (!T.entries.empty()) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(reinterpret_cast<void*>(T.entries.front().addr), &mbi, sizeof mbi))
            LOGI("gamedata: template data at %p: protect 0x%lx, %s, allocation %p", reinterpret_cast<void*>(T.entries.front().addr),
                 mbi.Protect, mbi.Type == MEM_PRIVATE ? "MEM_PRIVATE" : mbi.Type == MEM_IMAGE ? "MEM_IMAGE" : "MEM_MAPPED", mbi.AllocationBase);
    }
}

void gd_world_reset() {
    ext_world_reset();
    {
        std::lock_guard lock(store_mutex());
        changes_world_reset();
        clones_world_reset();
        for (uint32_t s = KILN_GD_TEMPLATES; s <= KILN_GD_ITEMS; ++s) greg(s).clear();
    }
    g_ready.store(false);
    set_detail("waiting for a world");
}

void gd_tick(uint64_t step) {
    if (!g_ready.load()) return;
    if (step % 25 == 7) changes_tick();
    clones_tick(step);
}

}  // namespace kc
