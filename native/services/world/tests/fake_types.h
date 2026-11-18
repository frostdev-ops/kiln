// SPDX-License-Identifier: MIT
// A fake kiln.types for the write policy's host tests: a type graph by qualified name (kind, size, base or element,
// fields), served through the KilnTypesApi members the policy reads (type_by_hash, type_info, type_field). Built by hand
// or from sigdb's reflection dumps (sigdb/types/<side>/<exe sha256>.json, schema 2).
#pragma once
#include <cstdint>
#include <deque>
#include <fstream>
#include <map>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "services/kiln_types.h"

namespace faketypes {

inline uint32_t fnv1a(const std::string& s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}

struct Node {
    std::string name;
    uint8_t kind = 0;
    uint32_t size = 0;
    uint32_t hash = 0;
    std::string inner;  // base class, element or target ("" none)
    std::vector<std::pair<std::string, std::string>> fields;  // (field name, type name)
    // linked
    const Node* inner_node = nullptr;
    std::vector<const Node*> field_nodes;
};

struct Types {
    std::deque<Node> nodes;
    std::map<std::string, Node*> by_name;
    std::map<uint32_t, Node*> by_hash;
    KilnTypesApi table{};

    Node& add(const std::string& name, uint8_t kind, uint32_t size, const std::string& inner = "",
              std::vector<std::pair<std::string, std::string>> fields = {}, uint32_t hash = 0) {
        nodes.push_back(Node{name, kind, size, hash ? hash : fnv1a(name), inner, std::move(fields), nullptr, {}});
        Node& n = nodes.back();
        by_name[name] = &n;
        by_hash[n.hash] = &n;
        return n;
    }
    // Resolves inner and field type names (unknown names stay NULL: kiln.types could not describe them).
    void link() {
        for (Node& n : nodes) {
            const auto it = by_name.find(n.inner);
            n.inner_node = n.inner.empty() || it == by_name.end() ? nullptr : it->second;
            n.field_nodes.clear();
            for (const auto& [field, type] : n.fields) {
                const auto f = by_name.find(type);
                n.field_nodes.push_back(f == by_name.end() ? nullptr : f->second);
            }
        }
    }
    // sigdb's dump: every type, then the component list.
    std::vector<std::string> load_sigdb(const std::string& path) {
        std::ifstream in(path);
        const auto doc = nlohmann::json::parse(in);
        for (const auto& t : doc.at("types")) {
            std::vector<std::pair<std::string, std::string>> fields;
            if (t.contains("fields"))
                for (const auto& f : t.at("fields")) fields.emplace_back(f.at("name").get<std::string>(), f.at("type").get<std::string>());
            const uint32_t hash = static_cast<uint32_t>(std::stoul(t.at("hash").get<std::string>(), nullptr, 16));
            add(t.at("qualifiedName").get<std::string>(), static_cast<uint8_t>(t.at("kind").get<int>()), t.at("size").get<uint32_t>(),
                t.contains("inner") ? t.at("inner").get<std::string>() : "", std::move(fields), hash);
        }
        link();
        std::vector<std::string> components;
        for (const auto& c : doc.at("components")) components.push_back(c.get<std::string>());
        return components;
    }
};

inline Types* g_types = nullptr;
inline const KilnType* KILN_CALL by_hash(uint32_t hash) {
    const auto it = g_types->by_hash.find(hash);
    return it == g_types->by_hash.end() ? nullptr : reinterpret_cast<const KilnType*>(it->second);
}
inline KilnResult KILN_CALL info(const KilnType* type, KilnTypeInfo* out) {
    if (!type || !out) return KILN_ERR_INVALID_ARGUMENT;
    const Node& n = *reinterpret_cast<const Node*>(type);
    const uint32_t size = out->size;
    *out = KilnTypeInfo{};
    out->size = size;
    out->name = KilnStr{n.name.data(), n.name.size()};
    out->qualified_name = out->name;
    out->type_size = n.size;
    out->kind = n.kind;
    out->hash = n.hash;
    out->field_count = static_cast<uint32_t>(n.fields.size());
    out->inner = reinterpret_cast<const KilnType*>(n.inner_node);
    return KILN_OK;
}
inline KilnResult KILN_CALL field(const KilnType* type, uint32_t index, KilnTypeField* out) {
    if (!type || !out) return KILN_ERR_INVALID_ARGUMENT;
    const Node& n = *reinterpret_cast<const Node*>(type);
    if (index >= n.fields.size()) return KILN_ERR_NOT_FOUND;
    out->name = KilnStr{n.fields[index].first.data(), n.fields[index].first.size()};
    out->type = reinterpret_cast<const KilnType*>(n.field_nodes[index]);
    out->offset = 0;
    return KILN_OK;
}
// The table over `types` (one at a time per process).
inline const KilnTypesApi* api(Types& types) {
    g_types = &types;
    types.table = KilnTypesApi{};
    types.table.header = {sizeof(KilnTypesApi), KILN_TYPES_VERSION};
    types.table.type_by_hash = &by_hash;
    types.table.type_info = &info;
    types.table.type_field = &field;
    return &types.table;
}

}  // namespace faketypes
