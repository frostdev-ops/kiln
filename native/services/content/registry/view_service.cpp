// SPDX-License-Identifier: MIT
// kiln.content registry views: the service behind kiln.registry.view (view_service.h).
#include "view_service.h"

#include <cctype>
#include <cstdio>
#include <cstring>

#include "live_table.h"
#include "schema_ir.h"

namespace kc::registry::views {

namespace {

std::string guid_of(const uint8_t g[16]) {
    static constexpr char hx[] = "0123456789abcdef";
    std::string s;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) s += '-';
        s += hx[g[i] >> 4], s += hx[g[i] & 15];
    }
    return s;
}

constexpr size_t kMaxNotes = 256;

}  // namespace

Service::Service(ServiceEnv env) : env_(std::move(env)) {}

void Service::note(const std::string& line) {
    std::lock_guard lock(mu_);
    if (!env_.log || noted_.size() >= kMaxNotes || !noted_.insert(line).second) return;
    env_.log(0, "registry view: " + line + (noted_.size() == kMaxNotes ? " (the last view refusal kiln.log names this start)" : ""));
}

KilnResult Service::prepare(std::string* why) {
    std::lock_guard lock(mu_);
    if (env_.ready && !env_.ready(why)) return KILN_ERR_UNAVAILABLE;
    if (built_) return KILN_OK;
    const Schema schema = env_.schema ? *env_.schema : generated_schema();
    map::RegMap rm;
    const bool have_rm = env_.regmap && env_.regmap(&rm);
    std::vector<std::string> errs;
    if (env_.aliases && !env_.aliases(&aliases_, &errs)) aliases_ = {};
    for (const std::string& e : errs) note(e + " (the alias table is not used)");
    std::vector<TypeDesc> types;
    std::string no_types;
    if (env_.resource_types && env_.describe) {
        for (const auto& [name, hash] : env_.resource_types(&no_types))
            if (auto d = env_.describe(name)) {
                d->name = name, d->hash = hash;
                types.push_back(std::move(*d));
            }
    } else {
        no_types = "the game's type list is not available here";
    }
    if (types.empty() && !no_types.empty()) unbuilt_ = no_types;
    catalog_ = catalog(schema, types, aliases_, have_rm ? &rm : nullptr);
    built_ = true;  // catalog_, aliases_ and unbuilt_ never change from here on: read without the lock
    return KILN_OK;
}

namespace {
bool same_keys(const std::vector<KilnAssetKey>& a, const std::vector<KilnAssetKey>& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
        if (std::memcmp(a[i].guid, b[i].guid, 16) != 0 || a[i].type != b[i].type || a[i].part != b[i].part) return false;
    return true;
}
}  // namespace

Service::Listing Service::entries(const View& v, std::string* why) {
    std::vector<KilnAssetKey> keys;
    if (!env_.list || !env_.list(v.type_hash, &keys)) return *why = v.name + ": its entries cannot be listed on this game build", nullptr;
    std::shared_ptr<std::mutex> one;
    {
        std::lock_guard lock(mu_);
        const auto it = entries_.find(v.name);
        if (it != entries_.end() && same_keys(it->second->keys, keys)) return it->second;
        auto& m = listing_[v.name];
        if (!m) m = std::make_shared<std::mutex>();
        one = m;
    }
    // one listing of this view at a time (another thread asking for it waits for that one); the service lock is not held, so
    // every other view, the registries and the catalog answer meanwhile
    std::lock_guard listing(*one);
    {
        std::lock_guard lock(mu_);
        const auto it = entries_.find(v.name);
        if (it != entries_.end() && same_keys(it->second->keys, keys)) return it->second;
    }
    auto es = std::make_shared<Entries>();
    es->keys = std::move(keys);
    std::vector<map::GameEntry> game;
    game.reserve(es->keys.size());
    for (const KilnAssetKey& k : es->keys) {
        map::GameEntry e;
        e.guid = guid_of(k.guid);
        if (!v.name_member.empty()) {
            json n;
            std::string ignored;
            if (env_.member && env_.member(k, v.type, v.name_member, &n, &ignored) && n.is_string()) e.name = n.get<std::string>();
        }
        es->names.push_back(e.name);
        game.push_back(std::move(e));
    }
    std::vector<std::string> notes;
    es->ids = ids(v, aliases_, game, &notes);
    for (const std::string& n : notes) note(n);
    for (size_t i = 0; i < es->ids.size(); ++i) es->by_id[es->ids[i]] = i, es->by_guid[game[i].guid] = i;
    Listing done = std::move(es);
    std::lock_guard lock(mu_);
    entries_[v.name] = done;  // a call still holding the previous listing keeps it whole
    return done;
}

KilnResult Service::find_entry(const View& v, const std::string& id, Listing* es, size_t* i, std::string* why) {
    *es = entries(v, why);
    if (!*es) return KILN_ERR_UNAVAILABLE;
    const auto it = (*es)->by_id.find(id);
    if (it == (*es)->by_id.end()) return *why = v.name + " has no entry " + id + " on this game build", KILN_ERR_NOT_FOUND;
    *i = it->second;
    return KILN_OK;
}

KilnResult Service::read_one(const View& v, const Listing& es, size_t i, const std::string& field, Taken* taken, json* out, std::string* why) {
    const ViewField* f = nullptr;
    const auto gp = game_path(v, field, &f, why);
    if (!gp) return f ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    json game;
    std::string err;
    if (f->kind == FieldKind::Unread || (f->kind == FieldKind::List && f->element == FieldKind::Unread)) {
        *why = v.name + "." + f->path + ": " + f->reason;
        return KILN_ERR_UNAVAILABLE;
    }
    if (!env_.member || !env_.member(es->keys[i], v.type, *gp, &game, &err)) {
        *why = v.name + "[" + es->ids[i] + "]." + f->path + ": this entry has no value of it on this game build";
        return KILN_ERR_UNAVAILABLE;
    }
    // an id's entry, from one listing of its view per call (this call's own view: the listing it started on)
    const RefFn refs = [this, taken](const std::string& view, const std::string& guid) -> std::optional<std::string> {
        Listing te;
        if (const auto it = taken->find(view); it != taken->end()) {
            te = it->second;
        } else if (const View* t = catalog_.find(view)) {
            std::string ignored;
            te = entries(*t, &ignored);
            (*taken)[view] = te;
        }
        if (!te) return std::nullopt;
        const auto it = te->by_guid.find(guid);
        return it == te->by_guid.end() ? std::nullopt : std::optional<std::string>(te->ids[it->second]);
    };
    // a list element by index reads as the element's kind
    ViewField one = *f;
    if (field.size() > 1 && field.back() == ']' && f->kind == FieldKind::List && generic_path(field) != f->path) one.kind = f->element;
    auto val = kiln_value(v, one, game, refs, &err);
    if (!val) {
        // "quest.kind: ..." -> "quest[game:quest_odd].kind: ..."
        const std::string place = v.name + "[" + es->ids[i] + "]";
        *why = err.compare(0, v.name.size() + 1, v.name + ".") == 0 ? place + err.substr(v.name.size()) : place + ": " + err;
        return KILN_ERR_UNAVAILABLE;
    }
    *out = std::move(*val);
    return KILN_OK;
}

KilnResult Service::views(std::string* out) {
    std::string why;
    if (const KilnResult rc = prepare(&why); rc != KILN_OK) return *out = why, rc;
    std::map<std::string, size_t> counts;
    {
        std::lock_guard lock(mu_);
        for (const auto& [name, es] : entries_) counts[name] = es->keys.size();
    }
    json j = views_json(catalog_, counts);
    if (!unbuilt_.empty()) j["advanced"] = unbuilt_;
    *out = j.dump();
    return KILN_OK;
}

KilnResult Service::fields(const std::string& view, std::string* out) {
    std::string why;
    if (const KilnResult rc = prepare(&why); rc != KILN_OK) return *out = why, rc;
    const View* v = catalog_.find(view);
    if (!v) return *out = "there is no view " + view + " on this game build", KILN_ERR_NOT_FOUND;
    json j = fields_json(*v);
    if (v->stable && env_.registry_mapping)
        for (json& f : j["fields"]) {
            std::string state, reason;
            if (!env_.registry_mapping(view + "." + f["path"].get<std::string>(), &state, &reason)) continue;
            f["state"] = state;
            if (!reason.empty()) f["reason"] = reason;
        }
    *out = j.dump();
    return KILN_OK;
}

KilnResult Service::read_stable(const View& v, const std::string& id, const std::string& field, std::string* out) {
    const ViewField* f = v.field(field);
    if (!f) return *out = v.name + " has no field " + field, KILN_ERR_NOT_FOUND;
    std::string state, reason;
    if (env_.registry_mapping && env_.registry_mapping(v.name + "." + f->path, &state, &reason) && state != "mapped")
        return *out = v.name + "[" + id + "]." + f->path + ": " + (reason.empty() ? state : reason), KILN_ERR_UNAVAILABLE;
    if (!env_.registry_read) return *out = "the registry is not available here", KILN_ERR_UNAVAILABLE;
    const KilnResult rc = env_.registry_read(v.name, id, field, out);
    if (rc == KILN_ERR_NOT_FOUND) *out = v.name + " has no entry " + id;
    else if (rc != KILN_OK) *out = v.name + "[" + id + "]." + field + ": the registry cannot read it now";
    return rc;
}

KilnResult Service::read(const std::string& view, const std::string& id, const std::string& field, std::string* out) {
    std::string why;
    if (const KilnResult rc = prepare(&why); rc != KILN_OK) return *out = why, rc;
    const View* v = catalog_.find(view);
    if (!v) return *out = "there is no view " + view + " on this game build", KILN_ERR_NOT_FOUND;
    if (v->stable) return read_stable(*v, id, field, out);
    Listing es;
    size_t i = 0;
    if (const KilnResult rc = find_entry(*v, id, &es, &i, &why); rc != KILN_OK) return *out = why, rc;
    json val;
    Taken taken{{v->name, es}};
    const KilnResult rc = read_one(*v, es, i, field, &taken, &val, &why);
    if (rc != KILN_OK) {
        // logged once per field and kind of refusal (the entry's id is left out, so a refusal of every entry is one line)
        const ViewField* f = v->field(field);
        note(f ? v->name + "." + f->path + " refused: " + (f->reason.empty() ? "an entry has no value of it" : f->reason) : why);
        return *out = why, rc;
    }
    *out = val.dump();
    return KILN_OK;
}

KilnResult Service::select(const std::string& view, const std::string& selector, std::string* out) {
    std::string why;
    if (const KilnResult rc = prepare(&why); rc != KILN_OK) return *out = why, rc;
    const View* v = catalog_.find(view);
    if (!v) return *out = "there is no view " + view + " on this game build", KILN_ERR_NOT_FOUND;
    if (selector.empty()) return *out = "a selector is an id, a glob over ids or a name", KILN_ERR_INVALID_ARGUMENT;
    if (v->stable) {
        if (!env_.registry_select) return *out = "the registry is not available here", KILN_ERR_UNAVAILABLE;
        const KilnResult rc = env_.registry_select(view, json(selector).dump(), out);
        if (rc != KILN_OK) *out = view + ": the registry cannot select now";
        return rc;
    }
    const Listing es = entries(*v, &why);
    if (!es) return *out = why, KILN_ERR_UNAVAILABLE;
    json a = json::array();
    for (size_t i = 0; i < es->ids.size(); ++i)
        if (selects(selector, es->ids[i], es->names[i])) a.push_back(es->ids[i]);
    *out = a.dump();
    return KILN_OK;
}

KilnResult Service::entry(const std::string& view, const std::string& id, std::string* out) {
    std::string why;
    if (const KilnResult rc = prepare(&why); rc != KILN_OK) return *out = why, rc;
    const View* v = catalog_.find(view);
    if (!v) return *out = "there is no view " + view + " on this game build", KILN_ERR_NOT_FOUND;
    json fields = json::object(), refused = json::object();
    if (v->stable) {
        for (const ViewField& f : v->fields) {
            if (f.path.find("[]") != std::string::npos) continue;  // element leaves come with their list
            std::string text;
            if (read_stable(*v, id, f.path, &text) == KILN_OK) fields[f.path] = json::parse(text, nullptr, false);
            else refused[f.path] = text;
        }
        if (fields.empty() && !refused.empty() && refused.begin()->get<std::string>().find("has no entry") != std::string::npos)
            return *out = view + " has no entry " + id, KILN_ERR_NOT_FOUND;
    } else {
        Listing es;
        size_t i = 0;
        if (const KilnResult rc = find_entry(*v, id, &es, &i, &why); rc != KILN_OK) return *out = why, rc;
        Taken taken{{v->name, es}};  // every field of the entry from the listing it was found in
        for (const ViewField& f : v->fields) {
            if (f.path.find("[]") != std::string::npos) continue;
            json val;
            std::string r;
            if (read_one(*v, es, i, f.path, &taken, &val, &r) == KILN_OK) fields[f.path] = std::move(val);
            else refused[f.path] = r;
        }
    }
    *out = json{{"view", view}, {"id", id}, {"tier", v->stable ? "stable" : "advanced"}, {"fields", fields}, {"refused", refused}}.dump();
    return KILN_OK;
}

// ---- the C table

namespace {

Service* g_service = nullptr;
using stage::copy_out;
using stage::str;
using stage::str_ok;
constexpr size_t kMaxName = 1024, kMaxSelector = 4096;

KilnResult answer(KilnResult rc, const std::string& text, char* buf, size_t cap, size_t* out_len) {
    if (!buf && cap) return KILN_ERR_INVALID_ARGUMENT;
    const KilnResult copied = copy_out(text, buf, cap, out_len);
    return rc == KILN_OK ? copied : rc;  // a refusal keeps its code; its reason is in buf as far as it fits
}
KilnResult KILN_CALL t_views(char* buf, size_t cap, size_t* out_len) {
    if (!g_service) return KILN_ERR_UNAVAILABLE;
    std::string out;
    const KilnResult rc = g_service->views(&out);
    return answer(rc, out, buf, cap, out_len);
}
KilnResult KILN_CALL t_fields(KilnStr view, char* buf, size_t cap, size_t* out_len) {
    if (!str_ok(view, kMaxName)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_service) return KILN_ERR_UNAVAILABLE;
    std::string out;
    const KilnResult rc = g_service->fields(str(view), &out);
    return answer(rc, out, buf, cap, out_len);
}
KilnResult KILN_CALL t_read(KilnStr view, KilnStr id, KilnStr field, char* buf, size_t cap, size_t* out_len) {
    if (!str_ok(view, kMaxName) || !str_ok(id, kMaxName) || !str_ok(field, kMaxName)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_service) return KILN_ERR_UNAVAILABLE;
    std::string out;
    const KilnResult rc = g_service->read(str(view), str(id), str(field), &out);
    return answer(rc, out, buf, cap, out_len);
}
KilnResult KILN_CALL t_select(KilnStr view, KilnStr selector, char* buf, size_t cap, size_t* out_len) {
    if (!str_ok(view, kMaxName) || !str_ok(selector, kMaxSelector)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_service) return KILN_ERR_UNAVAILABLE;
    std::string out;
    const KilnResult rc = g_service->select(str(view), str(selector), &out);
    return answer(rc, out, buf, cap, out_len);
}
KilnResult KILN_CALL t_entry(KilnStr view, KilnStr id, char* buf, size_t cap, size_t* out_len) {
    if (!str_ok(view, kMaxName) || !str_ok(id, kMaxName)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_service) return KILN_ERR_UNAVAILABLE;
    std::string out;
    const KilnResult rc = g_service->entry(str(view), str(id), &out);
    return answer(rc, out, buf, cap, out_len);
}
const KilnRegistryViewApi kTable = {{sizeof(KilnRegistryViewApi), KILN_REGISTRY_VIEW_VERSION}, &t_views, &t_fields, &t_read, &t_select, &t_entry};

}  // namespace

const KilnRegistryViewApi* view_table(Service* service) {
    g_service = service;
    return &kTable;
}

}  // namespace kc::registry::views
