// SPDX-License-Identifier: MIT
#include "map_list.h"

#include <cmath>
#include <cstring>
#include <vector>

#include "services/kiln_caps.h"
#include "services/kiln_markers.h"

namespace kmk {

namespace {
constexpr uint64_t kHeaderBytes = 0x28;  // data pointer, count, capacity, growth callback, allocator word

int64_t fixed(double v) { return static_cast<int64_t>(std::llround(v * 4294967296.0)); }

// [a, a + an) and [b, b + bn) do not overlap
bool apart(uint32_t a, uint32_t an, uint32_t b, uint32_t bn) { return a + an <= b || b + bn <= a; }

bool same_rows(const uint8_t* data, const MapLayout& l, uint64_t base, uint64_t n, const std::vector<uint8_t>& written) {
    if (written.size() != n * l.entry_size) return false;
    return std::memcmp(data + base * l.entry_size, written.data(), written.size()) == 0;
}

// The bytes of the rows we wrote last (to recognise them); one copy, replaced by each append.
std::vector<uint8_t> g_written;
}  // namespace

bool MapLayout::valid() const {
    return header > 0 && capacity > 0 && capacity <= 0x4000 && entry_size >= 0x38 && entry_size <= 0x400 && position + 16 <= entry_size &&
           z + 8 <= entry_size && type + 4 <= entry_size && visibility < entry_size && (!label || has_label());
}

bool MapLayout::has_label() const {
    if (!label || label + 16 > entry_size || label_hash + 8 > entry_size || label % 8 || label_hash % 8) return false;
    if (!apart(label, 16, label_hash, 8)) return false;
    const uint32_t fields[][2] = {{position, 16}, {z, 8}, {type, 4}, {visibility, 1}};
    for (const auto& f : fields)
        if (!apart(label, 16, f[0], f[1]) || !apart(label_hash, 8, f[0], f[1])) return false;
    return true;
}

ListState read_list(const uint8_t* ui, const MapLayout& l, uint64_t* count, uint64_t* room) {
    *count = 0;
    *room = 0;
    if (!l.valid()) return ListState::BadHeader;
    const uint8_t* h = ui + l.header;
    uint64_t data = 0, cnt = 0, cap = 0;
    std::memcpy(&data, h, 8);
    std::memcpy(&cnt, h + 8, 8);
    std::memcpy(&cap, h + 16, 8);
    if (data != reinterpret_cast<uintptr_t>(h) + kHeaderBytes || cap != l.capacity || cnt > cap) return ListState::BadHeader;
    *count = cnt;
    *room = cap - cnt;
    return ListState::Ok;
}

void encode_entry(uint8_t* e, const MapLayout& l, const MapRow& row) {
    std::memset(e, 0, l.entry_size);
    const int64_t x = fixed(row.x), y = fixed(row.y), z = fixed(row.z);
    std::memcpy(e + l.position, &x, 8);
    std::memcpy(e + l.position + 8, &y, 8);
    std::memcpy(e + l.z, &z, 8);
    std::memcpy(e + l.type, &row.type, 4);
    e[l.visibility] = 2;
    if (row.text && row.text_len && l.has_label()) {  // issue 160: the text view the game's name resolver takes first
        const uint64_t p = reinterpret_cast<uintptr_t>(row.text);
        std::memcpy(e + l.label, &p, 8);
        std::memcpy(e + l.label + 8, &row.text_len, 8);
        std::memcpy(e + l.label_hash, &row.text_hash, 8);
    }
}

uint32_t append_rows(uint8_t* ui, const MapLayout& l, const MapRow* rows, uint32_t n, Appended* last, ListState* state) {
    uint64_t count = 0, room = 0;
    *state = read_list(ui, l, &count, &room);
    if (*state != ListState::Ok) return 0;
    uint8_t* h = ui + l.header;
    uint8_t* data = h + kHeaderBytes;
    // the game did not clear its list since our last append: the tail is still ours, write over it
    if (last->step && last->n && count == last->base + last->n && same_rows(data, l, last->base, last->n, g_written)) {
        count = last->base;
        room = l.capacity - count;
    }
    const uint32_t take = static_cast<uint32_t>(n < room ? n : room);
    g_written.assign(static_cast<size_t>(take) * l.entry_size, 0);
    for (uint32_t i = 0; i < take; ++i) encode_entry(g_written.data() + static_cast<size_t>(i) * l.entry_size, l, rows[i]);
    if (take) std::memcpy(data + count * l.entry_size, g_written.data(), g_written.size());
    const uint64_t next = count + take;
    std::memcpy(h + 8, &next, 8);
    last->base = count;
    last->n = take;
    last->step += 1;
    return take;
}

void MapKeep::set(uint64_t e, const MapRow* r, uint32_t n) {
    if (!e || !n) return clear();
    epoch = e;
    rows.assign(r, r + n);
}

void MapKeep::clear() {
    epoch = 0;
    rows.clear();
}

void MapKeep::begin_post() {
    clear();
    posted = true;
}

bool MapKeep::end_step(bool ticked) {
    const bool post = posted;
    posted = false;
    if (ticked && !post) clear();
    return !rows.empty();
}

uint32_t rewrite_kept(uint8_t* ui, const MapLayout& l, const MapKeep& keep, uint64_t epoch, Appended* last, ListState* state) {
    *state = ListState::Ok;
    if (!epoch || keep.epoch != epoch || keep.rows.empty()) return 0;
    return append_rows(ui, l, keep.rows.data(), static_cast<uint32_t>(keep.rows.size()), last, state);
}

uint64_t label_hash(const char* p, size_t n) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (size_t i = 0; i < n; ++i) {
        h ^= static_cast<uint64_t>(static_cast<int64_t>(static_cast<signed char>(p[i])));
        h *= 0x100000001b3ull;
    }
    return h;
}

MapRow map_row(double x, double y, double z, uint32_t icon, uint32_t label_mode, std::string_view text, bool text_on) {
    MapRow r{x, y, z, icon};
    if (label_mode == KILN_MARKER_LABEL_NATIVE_TOOLTIP && text_on && !text.empty()) {
        r.text = text.data();
        r.text_len = text.size();
        r.text_hash = label_hash(text.data(), text.size());
    }
    return r;
}

TextRow map_text_row(bool map_on, bool text_guards, uint32_t guard_reason, const char* guard_text) {
    if (!text_guards) return TextRow{KILN_CAP_UNAVAILABLE, guard_reason, guard_text};
    if (!map_on)
        return TextRow{KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING,
                       "needs markers.map: the text goes into the tooltip of the marker's native map icon"};
    return TextRow{KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, ""};
}

}  // namespace kmk
