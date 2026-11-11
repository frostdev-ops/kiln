// SPDX-License-Identifier: MIT
#include "inspector.h"
#include "game.h"
#include <algorithm>
#include <array>
#include <charconv>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <limits>
#include <mutex>
#include <utility>

namespace {
std::mutex mx;
setinspect::View published;
bool opened = false;
std::string filter = "CurrentTransform";
uint32_t selected = 0;
uint64_t serial = 1;
struct Edit { uint64_t serial; setinspect::Field field; std::string value; };
std::vector<Edit> edits;
std::vector<setinspect::Pin> pins;
bool pinsDirty = false, disabled = false;
uintptr_t lastWorld = 0;
bool gone = false;
uint64_t lastSerial = 0;
std::chrono::steady_clock::time_point lastRefresh;

bool scalar(refl::Type t) {
    if (!t) return false;
    const auto n = t.size();
    switch (t.prim()) {
        case refl::Bool: case refl::U8: case refl::S8: case refl::Bitmask8: return n == 1;
        case refl::U16: case refl::S16: case refl::Bitmask16: return n == 2;
        case refl::U32: case refl::S32: case refl::F32: case refl::Bitmask32: return n == 4;
        case refl::U64: case refl::S64: case refl::F64: case refl::Bitmask64: return n == 8;
        case refl::Enum: return n == 1 || n == 2 || n == 4 || n == 8;
        default: return false;
    }
}
bool locate(refl::Type type, const std::string& path, uint32_t& off, refl::Type& leaf) {
    if (!path.empty()) return refl::path(type, path, off, leaf);
    off = 0; leaf = refl::unwrap(type.resolved()); return static_cast<bool>(leaf);
}
std::string valueOf(const uint8_t* p, refl::Type t) {
    if (t.prim() != refl::F32 && t.prim() != refl::F64) return refl::text(p, t);
    double v; if (t.prim() == refl::F32) { float f; memcpy(&f, p, 4); v = f; } else memcpy(&v, p, 8);
    char b[64]; snprintf(b, sizeof b, "%.17g", v); return b;
}
bool parse(refl::Type t, const std::string& text, std::array<uint8_t, 8>& bytes) {
    const auto k = t.prim(); const auto n = t.size();
    if (k == refl::Bool) { if (text != "true" && text != "false") return false; bytes[0] = text == "true"; return true; }
    if (k == refl::F32 || k == refl::F64) {
        char* end = nullptr; double d = strtod(text.c_str(), &end);
        if (text.empty() || end != text.c_str() + text.size() || !std::isfinite(d)) return false;
        if (k == refl::F32) { if (std::abs(d) > std::numeric_limits<float>::max()) return false; float f = static_cast<float>(d); memcpy(bytes.data(), &f, 4); }
        else memcpy(bytes.data(), &d, 8);
        return true;
    }
    uint64_t raw = 0;
    bool sign = k == refl::S8 || k == refl::S16 || k == refl::S32 || k == refl::S64;
    if (k == refl::Enum) {
        bool found = false;
        refl::forEnum(t, [&](std::string_view name, int64_t v) { if (text == name || text == std::to_string(v)) { raw = static_cast<uint64_t>(v); found = true; } });
        if (!found) return false;
    } else if (sign) {
        int64_t v = 0; auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), v);
        if (ec != std::errc{} || end != text.data() + text.size()) return false;
        if (n < 8 && (v < -(int64_t(1) << (n * 8 - 1)) || v >= (int64_t(1) << (n * 8 - 1)))) return false;
        raw = static_cast<uint64_t>(v);
    } else {
        std::string_view s = text; int base = 10;
        if (s.starts_with("0x")) { s.remove_prefix(2); base = 16; }
        auto [end, ec] = std::from_chars(s.data(), s.data() + s.size(), raw, base);
        if (ec != std::errc{} || end != s.data() + s.size() || (n < 8 && raw >= (uint64_t(1) << (n * 8)))) return false;
    }
    memcpy(bytes.data(), &raw, n); return true;
}
void fields(std::vector<setinspect::Field>& out, const std::string& comp, std::string path,
            refl::Type t, const uint8_t* data, uint32_t size, unsigned depth = 0) {
    t = refl::unwrap(t.resolved());
    if (!t || !t.size() || t.size() > size || out.size() >= 512 || depth > 6) return;
    if (scalar(t)) { setinspect::Field f{comp, path, valueOf(data, t), true}; f.bytes = static_cast<uint8_t>(t.size()); memcpy(f.original.data(), data, f.bytes); out.push_back(std::move(f)); return; }
    if (t.prim() == refl::Struct) {
        refl::forFields(t, [&](const refl::Field& f) {
            if (f.offset <= size) fields(out, comp, path.empty() ? std::string(f.name) : path + "." + std::string(f.name), f.type, data + f.offset, std::min(size - f.offset, t.size() - std::min(f.offset, t.size())), depth + 1);
        });
    } else if (t.prim() == refl::StaticArray && t.inner() && t.inner().size()) {
        const auto n = std::min(t.size() / t.inner().size(), 32u);
        for (uint32_t i = 0; i < n; ++i) fields(out, comp, path + "[" + std::to_string(i) + "]", t.inner(), data + i * t.inner().size(), t.inner().size(), depth + 1);
    } else out.push_back({comp, path, "<" + std::string(t.name()) + ": read-only>", false});
}
}
void setinspect::request(bool open, std::string component, uint32_t entity, bool reselect) {
    std::lock_guard lock(mx);
    if (reselect || filter != component || selected != entity || opened != open) ++serial;
    opened = open; filter = std::move(component); selected = entity;
}
void setinspect::pin(uint64_t generation, uint32_t entity, const Field& field) {
    std::lock_guard lock(mx);
    if (generation != serial || published.serial != generation || published.selected != entity || published.gone || !field.editable || pins.size() >= 64) return;
    auto same = [&](const auto& f) { return f.component == field.component && f.path == field.path; };
    if (std::none_of(published.fields.begin(), published.fields.end(), same) ||
        std::any_of(pins.begin(), pins.end(), [&](const Pin& p) { return p.entity == entity && same(p.field); })) return;
    pins.push_back({entity, field}); pinsDirty = true;
}
void setinspect::unpin(uint32_t entity, const Field& field) {
    std::lock_guard lock(mx);
    std::erase_if(pins, [&](const Pin& p) { return p.entity == entity && p.field.component == field.component && p.field.path == field.path; });
    published.pins = pins;
}
void setinspect::edit(uint64_t generation, const Field& f, std::string value) {
    std::lock_guard lock(mx);
    if (edits.size() < 32 && value.size() <= 256) edits.push_back({generation, f, std::move(value)});
}
setinspect::View setinspect::view() { std::lock_guard lock(mx); return published; }
void setinspect::clear() {
    std::lock_guard lock(mx); published = {}; published.off = disabled; edits.clear(); pins.clear(); pinsDirty = false; ++serial;
    lastWorld = 0; lastSerial = 0; gone = false; lastRefresh = {};
}
namespace {
void tickImpl(uintptr_t world) {
    using namespace setinspect;
    bool open, pinChanged = false; std::string component; uint32_t entity; uint64_t generation; std::vector<Edit> pending;
    {
        std::lock_guard lock(mx);
        if (disabled) return;
        if (world && lastWorld && world != lastWorld) { ++serial; published = {}; edits.clear(); pins.clear(); }
        if (world) for (auto& p : pins) if (!p.gone && !ecs::exists(p.entity)) { p.gone = true; p.have = false; pinChanged = true; }
        pinChanged |= std::exchange(pinsDirty, false);
        published.pins = pins;
        open = opened; component = filter; entity = selected; generation = serial; pending.swap(edits);
    }
    if (!world) { clear(); return; }
    if (!open) return;
    const auto now = std::chrono::steady_clock::now();
    if (world != lastWorld || generation != lastSerial) gone = false;
    const bool justGone = entity && !gone && !ecs::exists(entity);
    if (justGone) gone = true;
    if (!justGone && !pinChanged && world == lastWorld && generation == lastSerial && pending.empty() && now - lastRefresh < std::chrono::milliseconds(500)) return;
    lastWorld = world; lastSerial = generation; lastRefresh = now;
    View next; next.serial = generation; next.inWorld = true; next.selected = entity;
    refl::Type ft = refl::lookup(component);
    // Copy a bounded candidate set, then retain the nearest 200, not the first 200 in hash-map order.
    if (ft) {
        auto ids = ecs::findBounded(ft.hash(), 65536, next.matched);
        double origin[3]{}; uint32_t player = ecs::player();
        if (!player) { auto input = refl::lookup("PlayerInput"); uint32_t count = 0; auto first = input ? ecs::findBounded(input.hash(), 1, count) : std::vector<uint32_t>{}; if (!first.empty()) player = first.front(); }
        const bool haveOrigin = player && ecs::position(player, origin);
        for (auto id : ids) {
            Row r; r.entity = id; r.havePosition = ecs::position(id, r.position);
            if (r.havePosition && haveOrigin) r.distance = std::hypot(r.position[0] - origin[0], r.position[1] - origin[1], r.position[2] - origin[2]);
            next.rows.push_back(r);
        }
        auto nearer = [](const Row& a, const Row& b) { if ((a.distance < 0) != (b.distance < 0)) return b.distance < 0; return a.distance != b.distance ? a.distance < b.distance : a.entity < b.entity; };
        const auto keep = std::min(next.rows.size(), size_t(200));
        std::partial_sort(next.rows.begin(), next.rows.begin() + keep, next.rows.end(), nearer); next.rows.resize(keep);
        for (const auto& r : next.rows) next.entities.push_back(r.entity);
        if (next.matched > ids.size()) next.note = "Candidate scan capped at 65536; nearest order covers those candidates only.";
    } else next.note = "Unknown component filter";
    bool present = !entity || (!gone && ecs::exists(entity));
    if (entity && !gone) for (auto t : ecs::componentTypes()) {
        int idx = ecs::compIndex(world, t.hash()); uint32_t n = 0;
        const auto* p = ecs::component(world, entity, idx, &n);
        if (!p) continue;
        present = true;
        if (n <= (256u << 10)) fields(next.fields, std::string(t.name()), "", t, p, std::min(n, t.size()));
        if (next.fields.size() >= 512) break;
    }
    if (entity && !present) gone = true;
    next.gone = gone;
    // A renderer may have changed selection while these game-side copies were being gathered. Keep the final
    // generation check and all writes together, so no edit can apply to a request that was closed/replaced.
    std::lock_guard lock(mx);
    if (serial != generation || !opened) return;
    for (auto& pin : pins) {
        pin.have = false;
        if (pin.gone) continue;
        auto type = refl::lookup(pin.field.component); refl::Type leaf; uint32_t off = 0, n = 0;
        if (!type || !locate(type, pin.field.path, off, leaf) || !scalar(leaf)) continue;
        const auto* p = ecs::component(world, pin.entity, ecs::compIndex(world, type.hash()), &n);
        if (p && off <= n && leaf.size() <= n - off) { pin.field.value = valueOf(p + off, leaf); pin.have = true; }
    }
    next.pins = pins;
    for (const auto& e : pending) {
        next.note = "Edit refused: selection changed, entity gone or value changed";
        if (e.serial != generation || gone || !entity) continue;
        auto f = std::find_if(next.fields.begin(), next.fields.end(), [&](const Field& f) { return f.editable && f.component == e.field.component && f.path == e.field.path && f.bytes == e.field.bytes && f.original == e.field.original; });
        if (f == next.fields.end()) continue;
        auto t = refl::lookup(f->component); uint32_t off = 0; refl::Type leaf;
        if (!t || !locate(t, f->path, off, leaf) || !scalar(leaf)) continue;
        uint32_t n = 0; auto* p = ecs::component(world, entity, ecs::compIndex(world, t.hash()), &n);
        if (!p || off > n || leaf.size() > n - off || leaf.size() != e.field.bytes || memcmp(p + off, e.field.original.data(), leaf.size()) != 0) continue;
        std::array<uint8_t, 8> bytes{};
        if (!parse(leaf, e.value, bytes)) { next.note = "Edit refused: invalid type, range or enum value"; continue; }
        memcpy(p + off, bytes.data(), leaf.size()); f->value = valueOf(p + off, leaf); f->original = bytes;
        next.note = "Changed " + f->component + "." + f->path;
    }
    published = std::move(next);
}
}
void setinspect::tick(uintptr_t world) {
    // Production C++ is built with /EHa: hardware faults unwind the snapshot/edit locks here. No Lua is entered
    // by tickImpl, so its state remains usable; unlike a fault inside a Lua callback, only this inspector stops.
    try { tickImpl(world); }
    catch (...) {
        std::lock_guard lock(mx); disabled = true; published = {}; published.off = true;
        published.note = "Inspector faulted; disabled until the game restarts.";
        pins.clear(); edits.clear(); ++serial;
    }
}
