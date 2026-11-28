// SPDX-License-Identifier: MIT
// kiln.markers' rows in the game's map list (chapter 04 §2.3, §4.5): appended in a completed client Post to the normal
// world-marker list of the client world's UI play data, never through the game's own append function and never beyond the
// list's inline storage. Pure: it works on the memory it is given (the glue calls it under a fault guard on the game's
// object; the host tests on a fake one with sentinels).
//
// Guards, all of which must hold or nothing is written: the list's data pointer is exactly its header + 0x28 (the inline
// storage that follows the 40-byte header), its capacity is the layout's (1024 on the measured build), its count is not
// above the capacity. Only rows beyond the current count are written; the count is raised once, after the rows. Nothing
// before the count (the game's entries, another loader's) is read for writing or changed.
//
// Entry encoding (MapLabel.None, v1): the whole entry zeroed, the position as signed 64-bit 32.32 metres at +position
// (x, y) and +z, the marker type id at +type, visibility 2 (shown) at +visibility. Entity, marker loca id and both text
// views stay zero: the native compass cannot reach an id-0 entry. MapLabel.NativeTooltip with markers.map_text on
// (GitHub issue 160) adds the entry's text view: +label the record's text {pointer, u64 length} in kiln.markers' arena,
// +label_hash its hash as the game computes it. The game's name resolver takes that view before anything else, so it is
// the tooltip's title (docs/re/map.md, "The tooltip's name").
#ifndef KILN_MARKERS_MAP_LIST_H
#define KILN_MARKERS_MAP_LIST_H
#include <cstddef>
#include <cstdint>
#include <string_view>
#include <vector>

namespace kmk {

struct MapLayout {
    int64_t header = 0;        // the normal list's header in the UI play data (its count offset - 8)
    uint64_t capacity = 0;     // the inline capacity every list is built with
    uint32_t entry_size = 0;   // the stride
    uint32_t position = 0;     // x at +position, y at +position + 8
    uint32_t z = 0;
    uint32_t type = 0;
    uint32_t visibility = 0;
    uint32_t label = 0;        // the text view {const char*, u64 length} (markers/entry.label); 0: no text is written
    uint32_t label_hash = 0;   // its hash, u64 (markers/entry.label_hash)
    bool valid() const;        // offsets inside the entry, sizes the measured ones; a text layout when label is set
    bool has_label() const;    // label set, the view and the hash inside the entry and apart from every other field
};

struct MapRow {
    double x, y, z;
    uint32_t type;
    // MapLabel.NativeTooltip with the text on (issue 160): the record's text in kiln.markers' arena (never moved or freed
    // while the process lives) and its hash; empty: the entry's text view stays zero, as for MapLabel.None.
    const char* text = nullptr;
    uint64_t text_len = 0;
    uint64_t text_hash = 0;
};

enum class ListState { Ok, BadHeader };

// The list's count and capacity, after the guards. room = capacity - count.
ListState read_list(const uint8_t* ui, const MapLayout& l, uint64_t* count, uint64_t* room);

// Encodes one entry (entry_size bytes).
void encode_entry(uint8_t* e, const MapLayout& l, const MapRow& row);

// What a previous append left, to recognise a list the game did not clear since (no step ran its clear program).
struct Appended {
    uint64_t base = 0, n = 0;  // rows [base, base + n) were ours
    uint64_t step = 0;         // nonzero once something was appended
};

// Appends up to n rows (the guards first). When the list still ends with exactly our previous rows (the game did not
// clear it in between), they are replaced in place instead of appended again. Returns the rows written; *state says
// whether the guards held.
uint32_t append_rows(uint8_t* ui, const MapLayout& l, const MapRow* rows, uint32_t n, Appended* last, ListState* state);

// ---- every client step (GitHub issue 159) -----------------------------------------------------------------------------
// The game clears this list and fills it again at every client SimStep (clear_map_markers_for_ui, then
// replicate_map_markers_for_ui; the client world's StepPrograms waits for their jobs before SimStep returns), the map
// reads it in the UI frame on the same thread, and Kiln's Post runs only at the SimSteps that advance the client clock
// (60 Hz). Above 60 fps the SimSteps in between left the list without Kiln's rows and the icons flickered. So the rows a
// Post wrote are kept and written again at the end of every client SimStep (docs/re/map.md, "The list in a frame").
struct MapKeep {
    uint64_t epoch = 0;        // the client world epoch of the Post that wrote them; 0 = nothing kept
    std::vector<MapRow> rows;  // in the Post's order
    bool posted = false;       // a client Post began since the last client step's end
    void set(uint64_t e, const MapRow* r, uint32_t n);
    void clear();
    // The start of a client Post: nothing kept until post_map keeps what this Post writes.
    void begin_post();
    // The end of a client SimStep, before the rows are written again. The core runs the client Post at every ticked step
    // (tick.cpp), so a ticked step without one means the Post is gone (the core removes a tick subscriber that faults):
    // the kept rows are dropped, as nothing would move or remove them any more. Returns whether rows are kept.
    bool end_step(bool ticked);
};

// The end of a client SimStep: the kept rows again through append_rows when they are this epoch's (the same guards and
// room: the game's rows are never displaced, rows beyond the room are left out). Rows already at the list's end (this
// step's Post wrote them, or the step's clear did not run) are written over in place, never twice. Returns the rows
// written; nothing kept for this epoch writes nothing and leaves *state Ok.
uint32_t rewrite_kept(uint8_t* ui, const MapLayout& l, const MapKeep& keep, uint64_t epoch, Appended* last, ListState* state);

// ---- the tooltip text (GitHub issue 160) ------------------------------------------------------------------------------
// The game's hash of a text view (client 0x316830, 0x308970, 0x11b0fb0): a wrapping 64-bit FNV-1a over each byte
// sign-extended (MOVSX), seed 0xcbf29ce484222325, multiplier 0x100000001b3.
uint64_t label_hash(const char* p, size_t n);

// The row a client row puts in the map list: its position and icon, and its text when its map label is NativeTooltip,
// the text is not empty and text_on (markers.map_text). `text` must outlive the row (the arena's bytes do).
MapRow map_row(double x, double y, double z, uint32_t icon, uint32_t label_mode, std::string_view text, bool text_on);

// markers.map_text on a game client: on exactly when markers.map is on and the tooltip text's guards hold
// (markers.map_tooltip_text resolved, its layout the measured one). Off with the guards' reason and words, else naming
// markers.map. `guard_text` must outlive the result.
struct TextRow {
    uint32_t state;   // KILN_CAP_*
    uint32_t reason;  // KILN_CAP_REASON_*
    const char* text;
};
TextRow map_text_row(bool map_on, bool text_guards, uint32_t guard_reason, const char* guard_text);

}  // namespace kmk
#endif
