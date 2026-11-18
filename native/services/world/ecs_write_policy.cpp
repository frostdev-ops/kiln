// SPDX-License-Identifier: MIT
// The compare-exchange write policy; see ecs_write_policy.h.
#include "ecs_write_policy.h"

#include <cstdio>
#include <cstring>
#include <utility>
#include <vector>

namespace kw::ecs::writes {

namespace {
bool plain_leaf(uint8_t kind) {
    switch (kind) {
        case KILN_TYPE_BOOL: case KILN_TYPE_U8: case KILN_TYPE_S8: case KILN_TYPE_U16: case KILN_TYPE_S16:
        case KILN_TYPE_U32: case KILN_TYPE_S32: case KILN_TYPE_U64: case KILN_TYPE_S64: case KILN_TYPE_F32:
        case KILN_TYPE_F64: case KILN_TYPE_ENUM: case KILN_TYPE_BITMASK8: case KILN_TYPE_BITMASK16:
        case KILN_TYPE_BITMASK32: case KILN_TYPE_BITMASK64: case KILN_TYPE_GUID:
            return true;
        default:
            return false;
    }
}
const char* refused_kind(uint8_t kind) {
    switch (kind) {
        case KILN_TYPE_DS_ARRAY: case KILN_TYPE_DS_STRING: case KILN_TYPE_DS_OPTIONAL: case KILN_TYPE_DS_VARIANT:
            return "the type holds a dynamic array, string, optional or variant (it owns heap memory)";
        case KILN_TYPE_BLOB_ARRAY: case KILN_TYPE_BLOB_STRING: case KILN_TYPE_BLOB_OPTIONAL: case KILN_TYPE_BLOB_VARIANT:
            return "the type holds a blob array, string, optional or variant (offsets into blob data)";
        case KILN_TYPE_OBJECT_REFERENCE:
            return "the type holds an object reference (a pointer)";
        default:
            return "the type holds a kind kiln.types does not name";
    }
}
void set_why(KilnEcsWritePolicy* out, const char* why) {
    std::snprintf(out->why, sizeof out->why, "%s", why ? why : "");
}
}  // namespace

Plain classify_plain(const KilnTypesApi* types, uint32_t hash, uint32_t* reflected_size, const char** why) {
    *reflected_size = 0;
    *why = "kiln.types cannot describe the component's type";
    if (!types || types->header.size < offsetof(KilnTypesApi, type_field) + sizeof(void*) || !types->type_by_hash ||
        !types->type_info || !types->type_field)
        return Plain::Unknown;
    const KilnType* root = types->type_by_hash(hash);
    if (!root) {
        *why = "kiln.types has no type under the component's hash";
        return Plain::Unknown;
    }
    KilnTypeInfo info{};
    info.size = sizeof info;
    if (types->type_info(root, &info) != KILN_OK) return Plain::Unknown;
    *reflected_size = info.type_size;
    std::vector<std::pair<const KilnType*, uint32_t>> stack;  // (type, depth)
    stack.emplace_back(root, 0);
    uint32_t nodes = 0;
    while (!stack.empty()) {
        const auto [type, depth] = stack.back();
        stack.pop_back();
        if (++nodes > kMaxNodes || depth > kMaxDepth) {
            *why = "the component's type is deeper or larger than the policy walks";
            return Plain::Unknown;
        }
        KilnTypeInfo t{};
        t.size = sizeof t;
        if (!type || types->type_info(type, &t) != KILN_OK) return Plain::Unknown;
        if (plain_leaf(t.kind)) continue;
        switch (t.kind) {
            case KILN_TYPE_TYPEDEF:
            case KILN_TYPE_STATIC_ARRAY:
                if (!t.inner) return Plain::Unknown;
                stack.emplace_back(t.inner, depth + 1);
                break;
            case KILN_TYPE_STRUCT:
                if (t.inner) stack.emplace_back(t.inner, depth + 1);  // the base class
                if (t.field_count > kMaxNodes) {
                    *why = "the component's type is deeper or larger than the policy walks";
                    return Plain::Unknown;
                }
                for (uint32_t i = 0; i < t.field_count; ++i) {
                    KilnTypeField f{};
                    if (types->type_field(type, i, &f) != KILN_OK || !f.type) return Plain::Unknown;
                    stack.emplace_back(f.type, depth + 1);
                }
                break;
            default:
                *why = refused_kind(t.kind);
                return Plain::No;
        }
    }
    *why = nullptr;
    return Plain::Yes;
}

KilnResult decide(const PolicyFacts& f, uint32_t component, KilnEcsWritePolicy* out) {
    const uint32_t size = out->size;
    *out = KilnEcsWritePolicy{};
    out->size = size;
    out->world_kind = f.world_kind;
    out->component = component;
    out->budget = f.gate_enabled ? f.budget : 0;
    out->component_size = f.registered ? f.registered_size : 0;
    auto refuse = [&](uint32_t status, uint32_t why_code, const char* why) -> KilnResult {
        out->status = status;
        out->why_code = why_code;
        set_why(out, why);
        return status == KILN_ECS_UNAVAILABLE ? KILN_ERR_UNAVAILABLE : KILN_OK;
    };
    if (f.world_kind != KILN_WORLD_SERVER)
        return refuse(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD,
                      "only the server world is written from tasks (the engine's write gate is the local authority's)");
    if (!f.exact_profile || !f.gate_enabled)
        return refuse(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_PROFILE,
                      f.exact_profile ? "this build's engine profile has no write gate"
                                      : "no exact engine profile for this build");
    if (!f.world) return refuse(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_WORLD, "no server world runs now");
    if (!f.registered)
        return refuse(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_UNREGISTERED, "no component is registered under this hash");
    // The facts known so far, also on a refusal below.
    if (f.tracking_known) {
        out->flags |= KILN_ECS_WRITE_POLICY_TRACKING_KNOWN;
        if (f.tracked_a) out->flags |= KILN_ECS_WRITE_POLICY_TRACKED_A;
        if (f.tracked_b) out->flags |= KILN_ECS_WRITE_POLICY_TRACKED_B;
    }
    if (f.plain == Plain::Yes) out->flags |= KILN_ECS_WRITE_POLICY_PLAIN;
    if (f.registered_size > f.budget) out->flags |= KILN_ECS_WRITE_POLICY_OVER_BUDGET;
    if (!f.tracking_known)
        return refuse(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_TRACKING_UNKNOWN,
                      "the component's change-tracking bits could not be read");
    if (f.tracked_a || f.tracked_b)
        return refuse(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_TRACKED,
                      "the engine tracks changes to this component (write it on the tick)");
    if (f.plain != Plain::Yes) return refuse(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_NOT_PLAIN, f.plain_why);
    if (f.reflected_size != f.registered_size || f.registered_size == 0)
        return refuse(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_SIZE,
                      "the reflected size is not the registered size");
    if (f.registered_size > f.budget)
        return refuse(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_OVER_BUDGET,
                      "the component is larger than the engine's write budget");
    out->status = KILN_ECS_OK;
    out->flags |= KILN_ECS_WRITE_POLICY_WRITABLE;
    out->why_code = KILN_ECS_WRITE_WHY_NONE;
    return KILN_OK;
}

} // namespace kw::ecs::writes
