// SPDX-License-Identifier: MIT
// kiln.content gamedata: paths over live game memory, driven by the game's reflection (gamedata_types.cpp).
//
// Blob kinds (offsets relative to the field itself, verified offline by tools/kfc on every resource type):
//   BlobArray {i32 rel, u32 count}, BlobString {i32 rel, u32 len}, BlobOptional {i32 rel; 0 = empty},
//   BlobVariant {u32 type hash, i32 rel (from the rel field, +4), u32 size}.
// Path grammar: "a.b[3].c", "[Type#k]" / "[Type]" (k-th element of a variant array whose type is Type), "[*]" (every
// element), "[<guid>]" (the element whose `guid` field is that GUID), "Component#k" (templates: a component by its type
// name), and below a keen::impact::Configurations a config by alias / 0x id ("impactValues.ManaCost.scaleFactor").
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <set>

#include "gamedata.h"

namespace kc::gd {

// ============================================================================================= blobs =============

bool blob_array(uintptr_t addr, uintptr_t* data, uint32_t* count) {
    int32_t rel = 0;
    uint32_t n = 0;
    if (!rdv(addr, rel) || !rdv(addr + 4, n) || n > 1000000) return false;
    *count = rel ? n : 0;
    *data = rel && n ? addr + static_cast<intptr_t>(rel) : 0;
    return true;
}

bool blob_variant(uintptr_t addr, uint32_t* hash, uintptr_t* data, uint32_t* size) {
    int32_t rel = 0;
    if (!rdv(addr, *hash) || !rdv(addr + 4, rel) || !rdv(addr + 8, *size)) return false;
    *data = *hash ? addr + 4 + static_cast<intptr_t>(rel) : 0;
    return true;
}

std::string blob_string(uintptr_t addr) { return read_blob_string(addr); }

// ============================================================================================= leaves ============

bool leaf_of(const TType* decl, Field* f) {
    if (!decl) return false;
    const TType* t = decl->resolved();
    if (!t) return false;
    *f = Field{};
    f->type_name = decl->name;
    f->offset = 0;
    f->writable = true;
    const uint32_t k = t->kind;
    if (k >= K_BOOL && k <= K_S64) {
        f->bytes = static_cast<uint8_t>(t->size);
        f->vt = k == K_BOOL ? KILN_CV_BOOL : KILN_CV_INT;
        f->is_signed = k == K_S8 || k == K_S16 || k == K_S32 || k == K_S64;
        return f->bytes == 1 || f->bytes == 2 || f->bytes == 4 || f->bytes == 8;
    }
    if (k == K_F32 || k == K_F64) {
        f->bytes = static_cast<uint8_t>(t->size);
        f->vt = KILN_CV_FLOAT;
        f->f64 = t->size == 8;
        return f->bytes == 4 || f->bytes == 8;
    }
    if (k == K_ENUM) {
        f->bytes = static_cast<uint8_t>(t->size);
        f->vt = KILN_CV_INT;
        f->enum_kind = KILN_CONTENT_ENUM;
        f->en = t->en;
        if (const TType* u = t->inner() ? t->inner()->resolved() : nullptr)
            f->is_signed = u->kind == K_S8 || u->kind == K_S16 || u->kind == K_S32 || u->kind == K_S64;
        return f->bytes == 1 || f->bytes == 2 || f->bytes == 4 || f->bytes == 8;
    }
    if (k >= K_BM8 && k <= K_BM64) {
        f->bytes = static_cast<uint8_t>(t->size);
        f->vt = KILN_CV_INT;
        f->enum_kind = KILN_CONTENT_FLAGS;
        f->en = t->en;
        return f->bytes == 1 || f->bytes == 2 || f->bytes == 4 || f->bytes == 8;
    }
    if (k == K_REF || k == K_GUID) {
        if (t->size != 16) return false;
        f->bytes = 16;
        f->vt = KILN_CV_GUID;
        f->ref = k == K_REF || decl->name != "keen::Guid";
        return true;
    }
    if (k == K_STRUCT) {
        if (t->name == "keen::Time" && t->size == 8) {
            f->bytes = 8;
            f->vt = KILN_CV_TIME;
            f->is_signed = true;
            return true;
        }
        if (t->fields.size() == 1) {  // keen::HashKey32 {u32 value}, ids: shown as the scalar
            const TField& in = t->fields[0];
            Field g;
            const TType* it = in.type();
            if (it && it->resolved() && it->resolved()->kind != K_STRUCT && leaf_of(it, &g) && g.vt != KILN_CV_GUID) {
                g.type_name = decl->name;
                g.offset = in.offset;
                *f = g;
                return true;
            }
        }
    }
    return false;
}

namespace {

// Names that identify things: written they would break bindings (config ids), registries or saves.
bool identity_name(const std::string& n) {
    return n == "configId" || n == "configGuid" || n == "objectId" || n == "resourceId" || n == "buffTypeId" ||
           n == "guid" || n == "programId" || n == "programGuid" || n == "itemId";
}

Node deref(Node n) {
    for (int i = 0; i < 6 && n.t; ++i) {
        if (n.t->kind == K_TYPEDEF) {
            n.t = n.t->resolved();
            continue;
        }
        if (n.t->kind == K_BLOBVAR) {
            uint32_t h = 0, sz = 0;
            uintptr_t d = 0;
            if (!blob_variant(n.addr, &h, &d, &sz) || !h) return Node{};
            const TType* vt = type_hash(h);
            n = Node{vt ? vt->resolved() : nullptr, d};
            continue;
        }
        if (n.t->kind == K_BLOBOPT) {
            int32_t rel = 0;
            if (!rdv(n.addr, rel) || !rel) return Node{};
            const TType* in = n.t->inner();
            n = Node{in ? in->resolved() : nullptr, n.addr + static_cast<intptr_t>(rel)};
            continue;
        }
        break;
    }
    return n;
}

bool is_configs(const TType* t) { return t && t->name == "keen::impact::Configurations"; }

// Elements of an array node (static or blob): (element node before deref, count).
bool elements(const Node& n, std::vector<Node>* out) {
    out->clear();
    const TType* el = n.t->inner();
    if (!el) return false;
    const TType* er = el->resolved();
    if (!er) return false;
    if (n.t->kind == K_ARRAY) {
        const uint32_t es = er->size ? er->size : (n.t->count ? n.t->size / n.t->count : 0);
        for (uint32_t i = 0; i < n.t->count && i < 4096; ++i) out->push_back(Node{el, n.addr + i * es});
        return true;
    }
    if (n.t->kind == K_BLOBARRAY) {
        uintptr_t d = 0;
        uint32_t c = 0;
        if (!blob_array(n.addr, &d, &c)) return false;
        const uint32_t es = er->size ? er->size : 12;
        for (uint32_t i = 0; i < c && i < 100000; ++i) out->push_back(Node{el, d + static_cast<uintptr_t>(i) * es});
        return true;
    }
    return false;
}

std::string variant_brief(const Node& raw) {
    const TType* r = raw.t ? raw.t->resolved() : nullptr;
    if (!r || r->kind != K_BLOBVAR) return {};
    uint32_t h = 0, sz = 0;
    uintptr_t d = 0;
    if (!blob_variant(raw.addr, &h, &d, &sz) || !h) return {};
    const TType* vt = type_hash(h);
    return vt ? vt->brief : std::string();
}

uint32_t config_id_of(const Node& cfg) {
    const TField* f = cfg.t ? cfg.t->field("configId") : nullptr;
    uint32_t v = 0;
    if (f) rdv(cfg.addr + f->offset, v);
    return v;
}

// ---- path tokens ----
struct Tok {
    bool index = false;
    std::string s;
};
bool tokenize(const std::string& p, std::vector<Tok>* out, std::string* err) {
    out->clear();
    size_t i = 0;
    std::string cur;
    auto flush = [&] {
        if (!cur.empty()) out->push_back(Tok{false, cur});
        cur.clear();
    };
    while (i < p.size()) {
        const char c = p[i];
        if (c == '.') {
            flush();
            ++i;
        } else if (c == '[') {
            flush();
            const size_t j = p.find(']', i);
            if (j == std::string::npos) {
                *err = "unclosed [ in " + p;
                return false;
            }
            out->push_back(Tok{true, p.substr(i + 1, j - i - 1)});
            i = j + 1;
        } else {
            cur += c;
            ++i;
        }
    }
    flush();
    if (out->empty()) {
        *err = "empty path";
        return false;
    }
    return true;
}

bool parse_u32(const std::string& s, uint32_t* v) {
    if (s.empty()) return false;
    char* end = nullptr;
    unsigned long long x = 0;
    if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) x = std::strtoull(s.c_str() + 2, &end, 16);
    else if (s[0] >= '0' && s[0] <= '9') x = std::strtoull(s.c_str(), &end, 10);
    else return false;
    if (!end || *end || x > 0xFFFFFFFFull) return false;
    *v = static_cast<uint32_t>(x);
    return true;
}

struct State {
    Node node;  // not yet dereferenced
    std::string path;
    uintptr_t ui_owner = 0;
    const TType* ui_type = nullptr;
    uintptr_t config = 0;
    bool in_config = false;
    const TType* cfg_t = nullptr;
};

std::string hex8(uint32_t v) {
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", v);
    return b;
}

}  // namespace

bool identity_field(const std::string& name) { return identity_name(name); }

bool resolve(Root kind, Node root, const std::string& path, std::vector<Resolved>* out, std::string* err,
             bool* first_missing) {
    out->clear();
    if (first_missing) *first_missing = false;
    std::vector<Tok> toks;
    if (!tokenize(path, &toks, err)) return false;
    std::vector<State> cur{State{root, "", 0, nullptr, 0, false}};
    for (size_t ti = 0; ti < toks.size(); ++ti) {
        const Tok& tk = toks[ti];
        std::vector<State> next;
        for (State st : cur) {
            Node n = deref(st.node);
            if (!n.t) {
                *err = (st.path.empty() ? std::string("the root") : st.path) + " is empty";
                continue;
            }
            const std::string sep = st.path.empty() ? "" : ".";
            if (!tk.index || is_configs(n.t)) {
                // a component of a template (by its type name, "#k" for the k-th of that type)
                if (ti == 0 && kind == Root::Template && !n.t->field(tk.s)) {
                    std::string want = tk.s;
                    uint32_t nth = 0;
                    if (const size_t h = want.find('#'); h != std::string::npos) {
                        uint32_t k = 0;
                        if (!parse_u32(want.substr(h + 1), &k)) {
                            *err = "bad component index in " + tk.s;
                            return false;
                        }
                        nth = k;
                        want = want.substr(0, h);
                    }
                    const TField* comps = n.t->field("components");
                    std::vector<Node> els;
                    if (!comps || !elements(Node{comps->type()->resolved(), n.addr + comps->offset}, &els)) {
                        *err = "the template's components are not readable";
                        return false;
                    }
                    uint32_t seen = 0;
                    bool found = false;
                    for (const Node& e : els) {
                        const std::string b = variant_brief(e);
                        if (lower(b) != lower(want)) continue;
                        if (seen++ != nth) continue;
                        next.push_back(State{e, b + (nth ? "#" + std::to_string(nth) : ""), 0, nullptr, 0, false});
                        found = true;
                        break;
                    }
                    if (!found) {
                        if (first_missing) *first_missing = true;
                        *err = "no component " + tk.s;
                    }
                    continue;
                }
                // a config by alias / id below keen::impact::Configurations
                if (is_configs(n.t) && !n.t->field(tk.s)) {
                    std::vector<uint32_t> ids = config_ids(tk.s);
                    if (ids.empty()) {
                        *err = "unknown config \"" + tk.s + "\" (an alias of the config vocabulary or 0x<id>)";
                        continue;
                    }
                    bool found = false;
                    for (const char* arr : {"simple", "scaled"}) {
                        const TField* af = n.t->field(arr);
                        if (!af || found) continue;
                        std::vector<Node> els;
                        const TType* at = af->type() ? af->type()->resolved() : nullptr;
                        if (!at || !elements(Node{at, n.addr + af->offset}, &els)) continue;
                        for (const Node& e : els) {
                            const Node c = deref(e);
                            if (!c.t) continue;
                            const uint32_t id = config_id_of(c);
                            if (std::find(ids.begin(), ids.end(), id) == ids.end()) continue;
                            State s2{c, st.path + sep + config_name(id), st.ui_owner, st.ui_type, c.addr, true, c.t};
                            next.push_back(s2);
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        if (ids.size() != 1) {
                            *err = "config \"" + tk.s + "\" is ambiguous here (ids";
                            for (uint32_t id : ids) *err += " " + hex8(id);
                            *err += "): use the 0x id";
                            continue;
                        }
                        Resolved r;
                        r.path = st.path + sep + config_name(ids[0]);
                        r.add_configs = n.addr;
                        r.add_id = ids[0];
                        r.uivalues_owner = st.ui_owner;
                        r.uivalues_type = st.ui_type;
                        for (size_t q = ti + 1; q < toks.size(); ++q)
                            r.add_rest += (toks[q].index ? "[" + toks[q].s + "]" : (r.add_rest.empty() ? "" : ".") + toks[q].s);
                        if (!r.add_rest.empty()) r.path += "." + r.add_rest;
                        out->push_back(std::move(r));
                    }
                    continue;
                }
                if (n.t->kind != K_STRUCT) {
                    *err = st.path + " has no field " + tk.s;
                    continue;
                }
                const TField* f = n.t->field(tk.s);
                if (!f) {
                    if (ti == 0 && first_missing) *first_missing = true;
                    *err = (st.path.empty() ? n.t->brief : st.path) + " has no field " + tk.s;
                    continue;
                }
                State s2 = st;
                s2.node = Node{f->type(), n.addr + f->offset};
                s2.path = st.path + sep + f->name;
                const TType* ft = f->type() ? f->type()->resolved() : nullptr;
                if (is_configs(ft) && n.t->field("uiValues")) {
                    s2.ui_owner = n.addr;
                    s2.ui_type = n.t;
                }
                next.push_back(std::move(s2));
                continue;
            }
            // [index]
            if (n.t->kind != K_ARRAY && n.t->kind != K_BLOBARRAY) {
                *err = st.path + " is not a list";
                continue;
            }
            std::vector<Node> els;
            if (!elements(n, &els)) {
                *err = st.path + " is not readable";
                continue;
            }
            uint32_t idx = 0;
            if (tk.s == "*") {
                for (size_t i = 0; i < els.size(); ++i) {
                    State s2 = st;
                    s2.node = els[i];
                    s2.path = st.path + "[" + std::to_string(i) + "]";
                    next.push_back(std::move(s2));
                }
                continue;
            }
            if (parse_u32(tk.s, &idx)) {
                if (idx >= els.size()) {
                    *err = st.path + "[" + tk.s + "]: the list has " + std::to_string(els.size()) + " entries";
                    continue;
                }
                State s2 = st;
                s2.node = els[idx];
                s2.path = st.path + "[" + std::to_string(idx) + "]";
                next.push_back(std::move(s2));
                continue;
            }
            uint8_t g[16];
            if (tk.s.size() == 36 && guid_parse(tk.s, g)) {
                bool found = false;
                for (size_t i = 0; i < els.size() && !found; ++i) {
                    const Node e = deref(els[i]);
                    const TField* gf = e.t && e.t->kind == K_STRUCT ? e.t->field("guid") : nullptr;
                    uint8_t have[16];
                    if (gf && rd(e.addr + gf->offset, have, 16) && std::memcmp(have, g, 16) == 0) {
                        State s2 = st;
                        s2.node = els[i];
                        s2.path = st.path + "[" + tk.s + "]";
                        next.push_back(std::move(s2));
                        found = true;
                    }
                }
                if (!found) *err = st.path + ": no entry with guid " + tk.s;
                continue;
            }
            // [Type#k] / [Type]
            std::string want = tk.s;
            uint32_t nth = 0;
            if (const size_t h = want.find('#'); h != std::string::npos) {
                if (!parse_u32(want.substr(h + 1), &nth)) {
                    *err = "bad index [" + tk.s + "]";
                    continue;
                }
                want = want.substr(0, h);
            }
            uint32_t seen = 0;
            bool found = false;
            for (size_t i = 0; i < els.size(); ++i) {
                const std::string b = variant_brief(els[i]);
                if (b.empty() || lower(b) != lower(want)) continue;
                if (seen++ != nth) continue;
                State s2 = st;
                s2.node = els[i];
                s2.path = st.path + "[" + b + "#" + std::to_string(nth) + "]";
                next.push_back(std::move(s2));
                found = true;
                break;
            }
            if (!found)
                *err = st.path + ": no " + want + " #" + std::to_string(nth) + " (" + std::to_string(seen) + " of that type)";
        }
        cur = std::move(next);
        if (cur.empty()) return !out->empty();
    }
    for (State& st : cur) {
        Node n = deref(st.node);
        if (!n.t) continue;
        if (st.in_config && n.t->kind == K_STRUCT && st.config == n.addr) {  // a config by alias: its value
            if (const TField* v = n.t->field("value")) n = Node{v->type(), n.addr + v->offset};
        }
        Resolved r;
        r.node = n;
        r.path = st.path;
        r.uivalues_owner = st.ui_owner;
        r.uivalues_type = st.ui_type;
        r.config = st.config;
        r.config_type = st.cfg_t;
        out->push_back(std::move(r));
    }
    if (out->empty() && err->empty()) *err = path + ": nothing there";
    return !out->empty();
}

// ============================================================================================= enumerate ========

namespace {
void enum_rec(const Node& raw, const std::string& path, int depth, size_t limit, size_t* count, bool in_config,
              const std::function<void(const std::string&, const Node&, const Field&)>& fn) {
    if (*count >= limit || depth > 14) return;
    Field f;
    if (raw.t && raw.t->resolved() && raw.t->resolved()->kind != K_BLOBVAR && leaf_of(raw.t, &f)) {
        const std::string nm = path.substr(path.find_last_of('.') == std::string::npos ? 0 : path.find_last_of('.') + 1);
        if (identity_name(nm)) f.writable = false;
        ++*count;
        fn(path, Node{raw.t, raw.addr}, f);
        return;
    }
    const Node n = deref(raw);
    if (!n.t) return;
    const std::string sep = path.empty() ? "" : ".";
    if (n.t->kind == K_STRUCT) {
        if (is_configs(n.t)) {
            for (const char* arr : {"simple", "scaled"}) {
                const TField* af = n.t->field(arr);
                std::vector<Node> els;
                const TType* at = af && af->type() ? af->type()->resolved() : nullptr;
                if (!at || !elements(Node{at, n.addr + af->offset}, &els)) continue;
                for (const Node& e : els) {
                    const Node c = deref(e);
                    if (!c.t) continue;
                    const std::string cp = path + sep + config_name(config_id_of(c));
                    for (const TField& cf : c.t->fields) {
                        // the base ImpactConfig's bookkeeping is not interesting to modders
                        if (cf.name == "locaTag" || cf.name == "isSigned" || cf.name == "valueFormat" || cf.name == "id" ||
                            cf.name == "configId" || cf.name == "type" || cf.name == "configGuid" || cf.name == "objectId")
                            continue;
                        enum_rec(Node{cf.type(), c.addr + cf.offset}, cf.name == "value" ? cp : cp + "." + cf.name, depth + 1,
                                 limit, count, true, fn);
                    }
                }
            }
            return;
        }
        for (const TField& tf : n.t->fields) enum_rec(Node{tf.type(), n.addr + tf.offset}, path + sep + tf.name, depth + 1, limit, count, in_config, fn);
        return;
    }
    if (n.t->kind == K_ARRAY || n.t->kind == K_BLOBARRAY) {
        std::vector<Node> els;
        if (!elements(n, &els)) return;
        const TType* er = n.t->inner() ? n.t->inner()->resolved() : nullptr;
        if (er && er->kind == K_U8 && els.size() > 16) return;  // raw byte blobs
        for (size_t i = 0; i < els.size(); ++i) enum_rec(els[i], path + "[" + std::to_string(i) + "]", depth + 1, limit, count, in_config, fn);
    }
}
}  // namespace

void enumerate(Root kind, Node root, const std::string& prefix, size_t limit,
               const std::function<void(const std::string&, const Node&, const Field&)>& fn) {
    size_t count = 0;
    if (!prefix.empty()) {
        std::vector<Resolved> rs;
        std::string err;
        if (!resolve(kind, root, prefix, &rs, &err)) return;
        for (const Resolved& r : rs)
            if (r.node.t) enum_rec(r.node, r.path, 0, limit, &count, r.config != 0, fn);
        return;
    }
    const Node n = deref(root);
    if (kind == Root::Template && n.t) {
        for (const TField& tf : n.t->fields) {
            if (tf.name == "components") continue;
            enum_rec(Node{tf.type(), n.addr + tf.offset}, tf.name, 0, limit, &count, false, fn);
        }
        const TField* comps = n.t->field("components");
        std::vector<Node> els;
        if (!comps || !elements(Node{comps->type()->resolved(), n.addr + comps->offset}, &els)) return;
        std::map<std::string, int> seen;
        for (const Node& e : els) {
            const std::string b = variant_brief(e);
            const int k = seen[lower(b)]++;
            enum_rec(e, k ? b + "#" + std::to_string(k) : b, 0, limit, &count, false, fn);
        }
        return;
    }
    enum_rec(root, "", 0, limit, &count, false, fn);
}

// ============================================================================================= JSON ==============

gjson leaf_json(const Field& f, const uint8_t* b, bool raw) {
    const KilnContentValue v = decode(f, b);
    switch (f.vt) {
        case KILN_CV_BOOL: return v.i != 0;
        case KILN_CV_INT:
            if (!raw && f.enum_kind == KILN_CONTENT_ENUM && f.en)
                for (auto& [n, x] : f.en->values)
                    if (x == v.i) return n;
            if (!raw && f.enum_kind == KILN_CONTENT_FLAGS && f.en) {
                gjson a = gjson::array();
                uint64_t rest = static_cast<uint64_t>(v.i);
                for (auto& [n, x] : f.en->values)
                    if (x >= 0 && x < 64 && ((rest >> x) & 1)) {
                        a.push_back(n);
                        rest &= ~(1ull << x);
                    }
                if (rest) a.push_back(rest);
                return a;
            }
            if (!f.is_signed && f.bytes == 8) return static_cast<uint64_t>(v.i);
            return v.i;
        case KILN_CV_FLOAT: return v.f;
        case KILN_CV_TIME: return v.f;
        case KILN_CV_GUID: return guid_text(v.guid);
        default: return nullptr;
    }
}

gjson node_json(const Node& raw, int depth) {
    Field f;
    if (raw.t && raw.t->resolved() && raw.t->resolved()->kind != K_BLOBVAR && leaf_of(raw.t, &f)) {
        uint8_t b[16] = {};
        if (!rd(raw.addr + f.offset, b, f.bytes)) return nullptr;
        return leaf_json(f, b);
    }
    if (depth <= 0) return "...";
    const TType* rt = raw.t ? raw.t->resolved() : nullptr;
    if (rt && rt->kind == K_BLOBSTRING) return blob_string(raw.addr);
    const Node n = deref(raw);
    if (!n.t) return nullptr;
    if (n.t->kind == K_BLOBSTRING) return blob_string(n.addr);
    if (n.t->kind == K_STRUCT) {
        gjson o = gjson::object();
        if (rt && rt->kind == K_BLOBVAR) o["$type"] = n.t->brief;
        if (is_configs(n.t)) {
            for (const char* arr : {"simple", "scaled"}) {
                const TField* af = n.t->field(arr);
                std::vector<Node> els;
                const TType* at = af && af->type() ? af->type()->resolved() : nullptr;
                if (!at || !elements(Node{at, n.addr + af->offset}, &els)) continue;
                for (const Node& e : els) {
                    const Node c = deref(e);
                    if (!c.t) continue;
                    const std::string nm = config_name(config_id_of(c));
                    const TField* vf = c.t->field("value");
                    gjson v = vf ? node_json(Node{vf->type(), c.addr + vf->offset}, depth - 1) : gjson(nullptr);
                    if (std::string(arr) == "scaled") {
                        gjson s = {{"value", v}};
                        for (const char* k : {"scaleFactor", "function", "source"})
                            if (const TField* sf = c.t->field(k)) s[k] = node_json(Node{sf->type(), c.addr + sf->offset}, depth - 1);
                        v = s;
                    }
                    o[nm] = {{"$config", c.t->brief}, {"value", v}};
                }
            }
            return o;
        }
        for (const TField& tf : n.t->fields) o[tf.name] = node_json(Node{tf.type(), n.addr + tf.offset}, depth - 1);
        return o;
    }
    if (n.t->kind == K_ARRAY || n.t->kind == K_BLOBARRAY) {
        std::vector<Node> els;
        if (!elements(n, &els)) return nullptr;
        const TType* er = n.t->inner() ? n.t->inner()->resolved() : nullptr;
        if (er && er->kind == K_U8 && els.size() > 16) return "(" + std::to_string(els.size()) + " bytes)";
        gjson a = gjson::array();
        for (const Node& e : els) a.push_back(node_json(e, depth - 1));
        return a;
    }
    return nullptr;
}

// ============================================================================================= configs ==========

namespace {
std::mutex g_cfg_mu;
bool g_cfg_built = false;
std::unordered_map<std::string, std::set<uint32_t>> g_alias;      // lower(alias) -> ids
std::unordered_map<uint32_t, std::map<std::string, int>> g_names; // id -> name -> uses
std::unordered_map<uint32_t, std::string> g_best;
std::unordered_map<uint32_t, uint32_t> g_programs;                // id -> programs using it
std::unordered_map<uint32_t, uint32_t> g_var_type;                // id -> the program variable's type hash
size_t g_program_count = 0;
std::chrono::steady_clock::time_point g_checked{};
std::set<uint32_t> g_cpp_ids;
struct Builtin {
    uint32_t id;
    const char* name;
};
// Read by C++, not by any program (docs/re/abilities.md section 2: actor_pay_usage_cost, impact_initialize).
const Builtin kBuiltin[] = {
    {0xaf236ece, "ManaCost"},  {0x727d7f78, "StaminaCost"}, {0x2de5f1f5, "SpecialAbilityCost"},
    {0xc8492400, "FuelAmount"}, {0xe99aaff8, "FuelCharges"}, {0x220e6b10, "UsedItem"},
};

void add_name(uint32_t id, const std::string& n, int weight) {
    if (!id || n.empty()) return;
    g_alias[lower(n)].insert(id);
    g_names[id][n] += weight;
}
}  // namespace

void configs_build() {
    std::lock_guard lock(g_cfg_mu);
    if (g_cfg_built) {
        // new programs (tools/impact, served by kiln.assets) bring new config names: look again every 2 s at most
        const auto now = std::chrono::steady_clock::now();
        if (now - g_checked < std::chrono::seconds(2)) return;
        g_checked = now;
        const TType* prog = type_named("keen::ImpactProgram");
        std::vector<std::array<uint8_t, 16>> g;
        if (!prog || !res_list(prog->hash, &g) || g.size() == g_program_count) return;
        g_alias.clear(), g_names.clear(), g_best.clear(), g_programs.clear(), g_var_type.clear(), g_cpp_ids.clear();
    }
    g_checked = std::chrono::steady_clock::now();
    for (const Builtin& b : kBuiltin) {
        add_name(b.id, b.name, 1000000);
        g_cpp_ids.insert(b.id);
    }
    const TType* prog = type_named("keen::ImpactProgram");
    const TType* var = type_named("keen::ImpactVariable");
    const TField* lay = prog ? prog->field("dataLayout") : nullptr;
    const TField* vcid = var ? var->field("configId") : nullptr;
    const TField* vname = var ? var->field("dbgName") : nullptr;
    const TField* vtype = var ? var->field("type") : nullptr;
    std::vector<std::array<uint8_t, 16>> guids;
    if (lay && vcid && vname && res_list(prog->hash, &guids)) {
        for (const auto& g : guids) {
            const uintptr_t p = res_data(g.data(), prog->hash);
            uintptr_t d = 0;
            uint32_t n = 0;
            if (!p || !blob_array(p + lay->offset, &d, &n)) continue;
            std::set<uint32_t> mine;
            for (uint32_t i = 0; i < n && i < 4096; ++i) {
                const uintptr_t v = d + static_cast<uintptr_t>(i) * var->size;
                uint32_t id = 0;
                if (!rdv(v + vcid->offset, id) || !id) continue;
                uint32_t vt = 0;
                if (vtype && rdv(v + vtype->offset, vt) && vt) g_var_type.emplace(id, vt);
                std::string nm = blob_string(v + vname->offset);
                if (const size_t c = nm.find("::"); c != std::string::npos) nm = nm.substr(0, c);
                if (nm.rfind("impact_nodes.", 0) == 0) nm.clear();  // generic value nodes say nothing
                if (!nm.empty()) add_name(id, nm, 1);
                mine.insert(id);
            }
            for (uint32_t id : mine) ++g_programs[id];
        }
    }
    // the best alias of an id: its most used name that names no other id
    for (auto& [id, names] : g_names) {
        std::string best;
        int uses = -1;
        for (auto& [n, u] : names)
            if (g_alias[lower(n)].size() == 1 && u > uses) best = n, uses = u;
        if (!best.empty()) g_best[id] = best;
    }
    g_cfg_built = true;
    g_program_count = guids.size();
    LOGI("gamedata: config vocabulary: %zu config ids with names (%zu aliases) from %zu impact programs and %zu "
         "C++-read ids",
         g_best.size(), g_alias.size(), guids.size(), g_cpp_ids.size());
}

std::vector<uint32_t> config_ids(const std::string& alias) {
    uint32_t v = 0;
    if (parse_u32(alias, &v)) return {v};
    configs_build();
    std::lock_guard lock(g_cfg_mu);
    auto it = g_alias.find(lower(alias));
    if (it != g_alias.end()) return {it->second.begin(), it->second.end()};
    // a config a program names by string (tools/impact `config "mymod.Radius"`): its id is FNV-1a of the string
    const uint32_t h = fnv1a(alias);
    if (g_names.count(h) || g_programs.count(h) || g_var_type.count(h)) return {h};
    return {};
}

std::string config_name(uint32_t id) {
    configs_build();
    std::lock_guard lock(g_cfg_mu);
    auto it = g_best.find(id);
    return it != g_best.end() ? it->second : hex8(id);
}

gjson configs_json() {
    configs_build();
    std::lock_guard lock(g_cfg_mu);
    std::map<uint32_t, gjson> rows;
    for (auto& [id, names] : g_names) {
        gjson nm = gjson::array();
        for (auto& [n, u] : names) nm.push_back(n);
        gjson r = {{"id", hex8(id)}, {"names", nm}, {"programs", g_programs.count(id) ? g_programs[id] : 0}};
        if (g_best.count(id)) r["alias"] = g_best[id];
        if (g_cpp_ids.count(id)) r["readBy"] = "C++";
        rows[id] = r;
    }
    gjson a = gjson::array();
    for (auto& [id, r] : rows) a.push_back(r);
    return a;
}

// ---- exemplars: an existing config entry of an id, for adds ----
namespace {
std::unordered_map<uint32_t, Exemplar> g_ex;
bool g_ex_scanned = false;

// A BuffTypeReference (16 bytes) at addr: its GUID is a buff's (buffTypeId = FNV-1a of it).
void note_buff_ref(uintptr_t addr) {
    uint8_t g[16];
    bool any = false;
    if (!rd(addr, g, 16)) return;
    for (uint8_t b : g) any = any || b;
    if (any) buff_guid_note(g);
}

void take_configs(const Node& configs) {
    static const uint32_t kBuffConfig = fnv1a(std::string("keen::impact::BuffImpactConfig"));
    for (const char* arr : {"simple", "scaled"}) {
        const TField* af = configs.t->field(arr);
        std::vector<Node> els;
        const TType* at = af && af->type() ? af->type()->resolved() : nullptr;
        if (!at || !elements(Node{at, configs.addr + af->offset}, &els)) continue;
        for (const Node& e : els) {
            uint32_t h = 0, sz = 0;
            uintptr_t d = 0;
            if (!blob_variant(e.addr, &h, &d, &sz) || !h || !sz || sz > 4096) continue;
            const Node c = deref(e);
            if (!c.t) continue;
            if (h == kBuffConfig)
                if (const TField* vf = c.t->field("value")) note_buff_ref(c.addr + vf->offset);
            const uint32_t id = config_id_of(c);
            if (!id || g_ex.count(id)) continue;
            Exemplar x;
            x.hash = h;
            x.size = sz;
            x.bytes.resize(sz);
            if (!rd(d, x.bytes.data(), sz)) continue;
            x.scaled = std::string(arr) == "scaled";
            g_ex.emplace(id, std::move(x));
        }
    }
}

void scan_field(const GRegistry& r, const char* field) {
    if (!r.available || !r.type) return;
    const TField* f = r.type->field(field);
    if (!f) return;
    const TType* ft = f->type() ? f->type()->resolved() : nullptr;
    if (!is_configs(ft)) return;
    for (const GEntry& e : r.entries) take_configs(Node{ft, e.addr + f->offset});
}
}  // namespace

void configs_scan() {
    if (g_ex_scanned) return;
    const auto t0 = std::chrono::steady_clock::now();
    g_ex_scanned = true;
    scan_field(greg(KILN_GD_ITEMS), "impactValues");
    scan_field(greg(KILN_GD_PERKS), "impactValues");
    scan_field(greg(KILN_GD_BUFFS), "impactValues");
    scan_field(greg(KILN_GD_SKILLS), "configValues");
    {  // a buff's follow-up buff (BuffType.onTimeout.followUp)
        const GRegistry& B = greg(KILN_GD_BUFFS);
        const TField* ot = B.type ? B.type->field("onTimeout") : nullptr;
        const TType* ott = ot && ot->type() ? ot->type()->resolved() : nullptr;
        const TField* fu = ott ? ott->field("followUp") : nullptr;
        if (fu)
            for (const GEntry& e : B.entries) note_buff_ref(e.addr + ot->offset + fu->offset);
    }
    // sequence events with impactValues (SpawnImpact, PayUsageCost, ApplyBuffEvent, ...)
    const GRegistry& S = greg(KILN_GD_SEQUENCES);
    for (const GEntry& e : S.entries) {
        const Node sub{S.type, e.addr};
        const TField* ev = S.type ? S.type->field("events") : nullptr;
        std::vector<Node> els;
        if (!ev || !elements(Node{ev->type()->resolved(), sub.addr + ev->offset}, &els)) continue;
        for (const Node& x : els) {
            const Node d = deref(x);
            const TField* iv = d.t ? d.t->field("impactValues") : nullptr;
            const TType* it = iv && iv->type() ? iv->type()->resolved() : nullptr;
            if (is_configs(it)) take_configs(Node{it, d.addr + iv->offset});
            if (const TField* bo = d.t ? d.t->field("buffTypeOverride") : nullptr) note_buff_ref(d.addr + bo->offset);
        }
    }
    // templates' ImpactConfiguration
    const GRegistry& T = greg(KILN_GD_TEMPLATES);
    std::vector<Resolved> rs;
    std::string e2;
    for (const GEntry& e : T.entries) {
        if (!resolve(Root::Template, Node{T.type, e.addr}, "ImpactConfiguration.configValues", &rs, &e2)) continue;
        for (const Resolved& r : rs)
            if (r.node.t) take_configs(r.node);
    }
    LOGI("gamedata: config exemplars: %zu config ids found in the game data (%.0f ms)", g_ex.size(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
}

bool config_exemplar(uint32_t id, Exemplar* out, std::string* err) {
    if (auto it = g_ex.find(id); it != g_ex.end()) {
        *out = it->second;
        return true;
    }
    if (!g_ex_scanned) {
        configs_scan();
        if (auto it = g_ex.find(id); it != g_ex.end()) {
            *out = it->second;
            return true;
        }
    }
    // a config no game data sets (a new program's): any entry of the same value type, with this id
    uint32_t vt = 0;
    {
        std::lock_guard lock(g_cfg_mu);
        if (auto t = g_var_type.find(id); t != g_var_type.end()) vt = t->second;
    }
    const TType* base = type_named("keen::impact::ImpactConfig");
    const TField* tf = base ? base->field("type") : nullptr;
    const TField* cf = base ? base->field("configId") : nullptr;
    if (vt && tf && cf) {
        for (auto& [oid, x] : g_ex) {
            uint32_t have = 0;
            if (x.bytes.size() < tf->offset + 4) continue;
            std::memcpy(&have, x.bytes.data() + tf->offset, 4);
            if (have != vt) continue;
            Exemplar e = x;
            std::memcpy(e.bytes.data() + cf->offset, &id, 4);
            for (const char* clear : {"locaTag", "id", "configGuid", "objectId"})
                if (const TField* f = base->field(clear)) {
                    const TType* ft = f->type() ? f->type()->resolved() : nullptr;
                    const uint32_t n = ft ? ft->size : 0;
                    if (n && f->offset + n <= e.bytes.size()) std::memset(e.bytes.data() + f->offset, 0, n);
                }
            *out = e;
            return true;
        }
    }
    *err = "config " + config_name(id) + " (" + hex8(id) + ") is not set anywhere in the game data" +
           (vt ? " and no config has its program variable's type" : " and no program declares it") + ", so Kiln cannot build one";
    return false;
}

void configs_reset_exemplars() {
    g_ex.clear();
    g_ex_scanned = false;
}

// ============================================================================================= values ===========

namespace {
bool enum_value(const Field& f, const std::string& name, int64_t* v) {
    if (!f.en) return false;
    for (auto& [n, x] : f.en->values)
        if (n == name || lower(n) == lower(name)) {
            *v = x;
            return true;
        }
    return false;
}

// The resource type a reference field points at: "keen::ObjectReference<keen::X>" somewhere in its typedef chain.
uint32_t ref_target(const std::string& type_name) {
    const TType* t = type_named(type_name);
    for (int i = 0; i < 12 && t; ++i) {
        const std::string& n = t->name;
        const size_t a = n.find("ObjectReference<");
        if (a != std::string::npos && n.back() == '>') {
            std::string target = n.substr(a + 16, n.size() - a - 17);
            if (target == "keen::ImpactDefinition") target = "keen::ImpactProgram";  // a program reference: the program's GUID
            if (target == "keen::actor::ActorSequence") target = "keen::actor::ActorSequenceResource";
            if (target == "keen::ecs::Template") target = "keen::ecs::TemplateResource";  // a template reference: a loaded template or a clone
            return fnv1a(target);
        }
        if (t->kind != K_TYPEDEF) break;
        t = t->inner();
    }
    return 0;
}
}  // namespace

bool parse_leaf(const Field& f, const gjson& j, const uint8_t* original, uint8_t* out, bool* reset, std::string* err) {
    *reset = false;
    std::memset(out, 0, 16);
    if (j.is_object() && (j.value("reset", false) == true)) {
        *reset = true;
        return true;
    }
    if (f.vt == KILN_CV_GUID) {
        if (j.is_object() && j.contains("mul")) {  // {"mul": 1}: the original reference (content sync resets)
            std::memcpy(out, original, 16);
            return true;
        }
        if (!j.is_string()) {
            *err = "a reference takes a GUID or a name";
            return false;
        }
        const std::string s = j.get<std::string>();
        if (s.empty() || s == "none") return true;  // all zero: no reference
        uint8_t g[16] = {};
        const uint32_t target = ref_target(f.type_name);
        const GEntry* named = nullptr;
        // a buff (BuffTypeReference: BuffType configs, ApplyBuffEvent.buffTypeOverride, onTimeout.followUp): buffs are
        // records of the one collection, not resources; the GUID must hash (FNV-1a) to a loaded buff's id (a game buff
        // or a clone). Names: a clone's name, "buff_<id>" / "0x<id>" when the game data references that buff's GUID.
        static const uint32_t kBuffType = fnv1a(std::string("keen::BuffType"));
        if (target == kBuffType && f.ref) {
            const GRegistry& B = greg(KILN_GD_BUFFS);
            if (s.size() == 36 && guid_parse(s, g)) {
                if (!B.get(fnv1a(g, 16))) {
                    *err = s + " is not a loaded buff (no buff has the id " + hex8(fnv1a(g, 16)) + ")";
                    return false;
                }
            } else {
                const GEntry* b = B.lookup(s);
                if (!b) {
                    *err = "\"" + s + "\" is neither a GUID nor the name of a buff";
                    return false;
                }
                if (!buff_guid(b->id, g)) {
                    *err = "the GUID of " + b->name + " is not known (no game data references it): give the buff's GUID";
                    return false;
                }
            }
            std::memcpy(out, g, 16);
            return true;
        }
        if (!(s.size() == 36 && guid_parse(s, g))) {
            // names: templates, sequences, perks, items ("item:<name>")
            if (s.rfind("item:", 0) == 0) {
                const Entry* it = reg(KILN_CONTENT_ITEMS).lookup(s.substr(5));
                if (!it) {
                    *err = "unknown item " + s.substr(5);
                    return false;
                }
                std::memcpy(g, it->guid, 16);
            } else {
                for (uint32_t sec : {KILN_GD_TEMPLATES, KILN_GD_SEQUENCES, KILN_GD_PERKS})
                    if (!named) named = greg(sec).lookup(s);
                if (!named) {
                    *err = "\"" + s + "\" is neither a GUID nor the name of a template, sequence or perk";
                    return false;
                }
                std::memcpy(g, named->guid, 16);
            }
        }
        if (target && f.ref) {
            // the GUID must be a loaded resource of the reference's type (a template the game knows, ...)
            // A StateId is only hashed, it is never a resource: any GUID is valid, so blocks can invent new states.
            const bool ok = f.type_name == "keen::ObjectReference<keen::animationgraph::StateId>" ||
                            res_data(g, target) != 0 ||
                            (target == fnv1a(std::string("keen::ecs::TemplateResource")) && greg(KILN_GD_TEMPLATES).get(fnv1a(g, 16)));
            if (!ok) {
                *err = guid_text(g) + " is not a loaded " + f.type_name;
                return false;
            }
        } else if (f.ref) {
            *err = f.type_name + ": Kiln cannot check what this reference points at, so it stays read-only";
            return false;
        }
        std::memcpy(out, g, 16);
        return true;
    }
    KilnContentValue v{};
    v.type = f.vt;
    const KilnContentValue orig = decode(f, original);
    if (j.is_boolean()) {
        v.type = KILN_CV_BOOL;
        v.i = j.get<bool>();
        v.f = static_cast<double>(v.i);
    } else if (j.is_number_integer() && f.vt != KILN_CV_FLOAT && f.vt != KILN_CV_TIME) {
        v.type = KILN_CV_INT;
        v.i = j.is_number_unsigned() ? static_cast<int64_t>(j.get<uint64_t>()) : j.get<int64_t>();
        v.f = static_cast<double>(v.i);
    } else if (j.is_number()) {
        v.type = f.vt == KILN_CV_TIME ? KILN_CV_TIME : KILN_CV_FLOAT;
        v.f = j.get<double>();
    } else if (j.is_string()) {
        const std::string s = j.get<std::string>();
        int64_t x = 0;
        if (f.enum_kind != KILN_CONTENT_PLAIN && enum_value(f, s, &x)) {
            v.type = KILN_CV_INT;
            v.i = f.enum_kind == KILN_CONTENT_FLAGS ? (1ll << x) : x;
        } else if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
            v.type = KILN_CV_INT;
            v.i = static_cast<int64_t>(std::strtoull(s.c_str() + 2, nullptr, 16));
        } else {
            *err = "unknown value \"" + s + "\"" + (f.en ? " (not a name of " + f.en->type + ")" : "");
            return false;
        }
    } else if (j.is_array() && f.enum_kind == KILN_CONTENT_FLAGS) {
        // flag names; "+Name" / "-Name" change the original's flags
        int64_t bits = 0;
        bool relative = false;
        for (const gjson& x : j)
            if (x.is_string() && !x.get<std::string>().empty() && (x.get<std::string>()[0] == '+' || x.get<std::string>()[0] == '-'))
                relative = true;
        if (relative) bits = orig.i;
        for (const gjson& x : j) {
            int64_t b = 0;
            if (x.is_string()) {
                std::string s = x.get<std::string>();
                char op = '+';
                if (!s.empty() && (s[0] == '+' || s[0] == '-')) op = s[0], s = s.substr(1);
                if (!enum_value(f, s, &b)) {
                    *err = "\"" + s + "\" is not a flag of " + (f.en ? f.en->type : f.type_name);
                    return false;
                }
                if (op == '-') bits &= ~(1ll << b);
                else bits |= 1ll << b;
            } else if (x.is_number_integer()) {
                bits |= 1ll << x.get<int64_t>();
            } else {
                *err = "flag names expected";
                return false;
            }
        }
        v.type = KILN_CV_INT;
        v.i = bits;
    } else if (j.is_object()) {
        if (j.contains("set")) return parse_leaf(f, j["set"], original, out, reset, err);
        double base = as_double(orig), mul = 1, add = 0, lo = -INFINITY, hi = INFINITY;
        auto num = [&](const char* k, double* d) {
            if (!j.contains(k)) return true;
            if (!j[k].is_number()) {
                *err = std::string("\"") + k + "\" must be a number";
                return false;
            }
            *d = j[k].get<double>();
            return true;
        };
        if (!num("mul", &mul) || !num("add", &add) || !num("min", &lo) || !num("max", &hi)) return false;
        double r = std::clamp(base * mul + add, lo, hi);
        if (j.value("raiseOnly", false)) r = std::max(r, base);
        if (j.value("lowerOnly", false)) r = std::min(r, base);
        if (f.vt == KILN_CV_INT || f.vt == KILN_CV_BOOL) {
            v.type = KILN_CV_INT;
            v.i = static_cast<int64_t>(std::llround(r));
            v.f = r;
        } else {
            v.type = f.vt == KILN_CV_TIME ? KILN_CV_TIME : KILN_CV_FLOAT;
            v.f = r;
        }
    } else {
        *err = "unsupported value " + j.dump();
        return false;
    }
    Field w = f;
    w.writable = true;
    return encode(w, v, out, err);
}

}  // namespace kc::gd
