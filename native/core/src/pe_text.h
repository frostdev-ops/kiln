// SPDX-License-Identifier: MIT
// The game exe's .text as the file on disk has it: PE parsing, base relocations, and the translation between a
// position in those bytes and the live address of the same instruction.
//
// sigdb matches its patterns against these bytes instead of the live code, so another engine's inline hook (a jmp
// written over a prologue) cannot hide a function from Kiln. Instruction bytes are the same on disk and in memory
// except where the loader applies base relocations; relocate_text() applies the same fixups to the file's copy, so
// the bytes sigdb scans equal the live code minus whatever someone patched in.
//
// Platform independent (no Windows headers): the host tests build it as is.
#pragma once
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace kiln::pe {

struct FileText {
    uint64_t image_base = 0;     // the preferred ImageBase from the optional header
    uint32_t size_of_image = 0, timestamp = 0;
    uint32_t text_rva = 0, text_vsize = 0;    // .text as mapped
    uint32_t text_raw = 0, text_rawsize = 0;  // .text in the file
    // Fixups that touch .text, as RVAs of the patched field, sorted: IMAGE_REL_BASED_DIR64 (8 bytes) and
    // IMAGE_REL_BASED_HIGHLOW (4 bytes). Fields may straddle the end of .text, never its start.
    std::vector<uint32_t> relocs64, relocs32;
    // Bytes of .text the file holds: the loader zero-fills the rest of the virtual size.
    uint32_t file_bytes() const { return std::min(text_vsize, text_rawsize); }
};

namespace detail {
template <class T>
inline bool read(const uint8_t* file, size_t size, size_t off, T* out) {
    if (off > size || size - off < sizeof(T)) return false;
    std::memcpy(out, file + off, sizeof(T));
    return true;
}
}  // namespace detail

// Parses a PE32+ (x64) file image: the .text section and the base relocations that touch it.
inline bool parse_file_text(const uint8_t* file, size_t size, FileText& out, std::string& error) {
    using detail::read;
    out = FileText{};
    uint16_t mz = 0;
    uint32_t pe = 0, sig = 0;
    if (!file || !read(file, size, 0, &mz) || mz != 0x5A4D) return error = "no MZ header", false;
    if (!read(file, size, 0x3C, &pe) || pe < 0x40 || pe > size || size - pe < 24 ||
        !read(file, size, pe, &sig) || sig != 0x00004550)
        return error = "no PE header", false;
    // Widen before adding: e_lfanew is a hostile 32-bit file offset.
    const size_t coff = static_cast<size_t>(pe) + 4, opt = static_cast<size_t>(pe) + 24;
    uint16_t machine = 0, nsec = 0, optsz = 0, magic = 0;
    read(file, size, coff, &machine);
    read(file, size, coff + 2, &nsec);
    read(file, size, coff + 4, &out.timestamp);
    read(file, size, coff + 16, &optsz);
    if (optsz < 112 || opt > size || optsz > size - opt)
        return error = "truncated optional header", false;
    read(file, size, opt, &magic);
    if (magic != 0x20B) return error = "not a PE32+ image", false;
    if (machine != 0x8664) return error = "not an x64 image", false;
    uint32_t ndirs = 0, reloc_rva = 0, reloc_size = 0, headers = 0;
    read(file, size, opt + 24, &out.image_base);
    read(file, size, opt + 56, &out.size_of_image);
    read(file, size, opt + 60, &headers);
    read(file, size, opt + 108, &ndirs);
    if (ndirs > (optsz - 112u) / 8u) return error = "truncated data directories", false;
    if (ndirs > 5) {
        read(file, size, opt + 112 + 5 * 8, &reloc_rva);
        read(file, size, opt + 112 + 5 * 8 + 4, &reloc_size);
    }
    struct Sec {
        char name[8];
        uint32_t vsize, va, rawsize, raw;
    };
    const size_t table = opt + optsz;
    if (!nsec || nsec > (size - table) / 40) return error = "truncated section table", false;
    if (headers < table + static_cast<size_t>(nsec) * 40 || headers > size || headers > out.size_of_image)
        return error = "invalid SizeOfHeaders", false;
    std::vector<Sec> secs;
    bool found_text = false;
    for (size_t i = 0, off = table; i < nsec; ++i, off += 40) {
        Sec s{};
        std::memcpy(s.name, file + off, 8);
        read(file, size, off + 8, &s.vsize);
        read(file, size, off + 12, &s.va);
        read(file, size, off + 16, &s.rawsize);
        read(file, size, off + 20, &s.raw);
        const uint64_t mapped = std::max(s.vsize, s.rawsize);
        if ((s.rawsize && (s.raw < headers || s.raw > size || s.rawsize > size - s.raw)) ||
            (mapped && (s.va < headers || s.va >= out.size_of_image || mapped > out.size_of_image - s.va)))
            return error = "section exceeds file or image", false;
        secs.push_back(s);
        if (std::memcmp(s.name, ".text\0\0\0", 8) == 0) {
            if (found_text || !s.va || !s.vsize || !s.rawsize) return error = "invalid .text section", false;
            found_text = true;
            out.text_rva = s.va, out.text_vsize = s.vsize, out.text_raw = s.raw, out.text_rawsize = s.rawsize;
        }
    }
    // Validate disjoint ranges in O(n log n), even for a hostile 65k-entry section table.
    std::vector<std::pair<uint64_t, uint64_t>> raw_ranges, virtual_ranges;
    for (const auto& s : secs) {
        if (s.rawsize) raw_ranges.emplace_back(s.raw, uint64_t{s.raw} + s.rawsize);
        if (const auto mapped = std::max(s.vsize, s.rawsize))
            virtual_ranges.emplace_back(s.va, uint64_t{s.va} + mapped);
    }
    for (auto* ranges : {&raw_ranges, &virtual_ranges}) {
        std::sort(ranges->begin(), ranges->end());
        for (size_t i = 1; i < ranges->size(); ++i)
            if ((*ranges)[i - 1].second > (*ranges)[i].first) return error = "overlapping sections", false;
    }
    if (!found_text) return error = "no .text section", false;

    // The entire directory must be backed by one section, not clamped to EOF or allowed to spill into a neighbour.
    auto file_offset = [&](uint32_t rva, uint32_t len, size_t* o) {
        for (const Sec& s : secs)
            if (rva >= s.va && rva - s.va < s.rawsize && len <= s.rawsize - (rva - s.va)) {
                *o = static_cast<size_t>(s.raw) + (rva - s.va);
                return true;
            }
        return false;
    };
    if ((!reloc_rva) != (!reloc_size)) return error = "incomplete relocation directory", false;
    size_t at = 0;
    if (reloc_size) {
        if (!file_offset(reloc_rva, reloc_size, &at)) return error = "base relocations are not in the file", false;
        const size_t end = at + reloc_size;
        const uint64_t lo = out.text_rva, hi = lo + out.text_vsize;
        while (at < end) {
            if (end - at < 8) return error = "truncated relocation header", false;
            uint32_t page = 0, block = 0;
            read(file, size, at, &page);
            read(file, size, at + 4, &block);
            if (block < 8 || (block & 3) || block > end - at)
                return error = "invalid relocation block", false;
            if ((page & 0xFFF) || page >= out.size_of_image) return error = "invalid relocation page", false;
            for (size_t k = at + 8; k < at + block; k += 2) {
                uint16_t e = 0;
                read(file, size, k, &e);
                const uint64_t rva = uint64_t{page} + (e & 0xFFF);
                const unsigned type = e >> 12;
                if (!type) continue;  // IMAGE_REL_BASED_ABSOLUTE padding
                // Fail closed for types whose width/semantics we cannot reproduce, even outside .text.
                if (type != 10 && type != 3) return error = "unsupported base relocation type", false;
                const size_t width = type == 10 ? 8 : 4;
                if (rva >= out.size_of_image || width > out.size_of_image - rva)
                    return error = "relocation field exceeds image", false;
                if (rva + width <= lo || rva >= hi) continue;
                // Low bytes outside this buffer can carry into .text; zero is not a valid substitute for them.
                if (rva < lo) return error = "relocation crosses start of .text", false;
                if (type == 10) out.relocs64.push_back(static_cast<uint32_t>(rva));
                else out.relocs32.push_back(static_cast<uint32_t>(rva));
            }
            at += block;
        }
    }
    std::sort(out.relocs64.begin(), out.relocs64.end());
    std::sort(out.relocs32.begin(), out.relocs32.end());
    std::vector<std::pair<uint32_t, unsigned>> fields;
    for (uint32_t r : out.relocs64) fields.emplace_back(r, 8);
    for (uint32_t r : out.relocs32) fields.emplace_back(r, 4);
    std::sort(fields.begin(), fields.end());
    for (size_t i = 1; i < fields.size(); ++i)
        if (uint64_t{fields[i - 1].first} + fields[i - 1].second > fields[i].first)
            return error = "overlapping .text relocations", false;
    return true;
}

// Applies the fixups the loader applies for a load at `actual_base` to `text`: writable bytes of .text, `text_size`
// long, starting at RVA ft.text_rva. A field that straddles the end gets its low bytes patched as the loader
// would. parse_file_text rejects a field crossing the start: missing low bytes could carry into the buffer.
// Returns the number of fixups that changed bytes (0 when the image is at its preferred base).
inline size_t relocate_text(uint8_t* text, size_t text_size, const FileText& ft, uint64_t actual_base) {
    const uint64_t delta = actual_base - ft.image_base;
    if (!delta) return 0;
    size_t n = 0;
    auto patch = [&](uint32_t rva, size_t width) {
        // Assemble the field from the buffer (bytes outside it count as 0: only the carried sum's in-range bytes are
        // written back), add the delta, write the in-range bytes back.
        uint8_t field[8] = {};
        const int64_t start = static_cast<int64_t>(rva) - ft.text_rva;
        for (size_t b = 0; b < width; ++b) {
            const int64_t i = start + static_cast<int64_t>(b);
            if (i >= 0 && static_cast<uint64_t>(i) < text_size) field[b] = text[i];
        }
        uint64_t v = 0;
        std::memcpy(&v, field, width);
        v += width == 8 ? delta : static_cast<uint32_t>(delta);
        std::memcpy(field, &v, width);
        bool any = false;
        for (size_t b = 0; b < width; ++b) {
            const int64_t i = start + static_cast<int64_t>(b);
            if (i >= 0 && static_cast<uint64_t>(i) < text_size) text[i] = field[b], any = true;
        }
        n += any;
    };
    for (uint32_t r : ft.relocs64) patch(r, 8);
    for (uint32_t r : ft.relocs32) patch(r, 4);
    return n;
}

// A range of code sigdb scans (`scan`, `size` bytes) and the live address of its first byte. For the disk view `scan`
// points at the file's (relocated) .text; for the live fallback scan == live.
struct CodeView {
    const uint8_t* scan = nullptr;
    size_t size = 0;
    uintptr_t live = 0;
    bool disk = false;

    const uint8_t* end() const { return scan ? scan + size : nullptr; }
    size_t remaining(uintptr_t a) const { return a >= live && a - live < size ? size - (a - live) : 0; }
    const uint8_t* scope_end(const uint8_t* p, size_t n) const {
        return p + std::min(n, static_cast<size_t>(end() - p));
    }
    // The live address of the byte at `p` (a pointer into the scanned bytes).
    uintptr_t to_live(const uint8_t* p) const { return live + static_cast<uintptr_t>(p - scan); }
    // The scanned byte that holds live address `a`, or nullptr when `a` is not in the range.
    const uint8_t* to_scan(uintptr_t a) const { return a >= live && a - live < size ? scan + (a - live) : nullptr; }
    // live - scan: add it to a pointer into the scanned bytes to get the live address (rel32 targets).
    intptr_t delta() const { return static_cast<intptr_t>(live - reinterpret_cast<uintptr_t>(scan)); }
};

// The first bytes of a relay as hookers write them: an absolute jump through the next eight bytes, a near jump, or
// `mov rax, imm64; jmp rax`. `p` holds at least 14 bytes.
inline bool relay_shape(const uint8_t* p) {
    return (p[0] == 0xFF && p[1] == 0x25 && p[2] == 0 && p[3] == 0 && p[4] == 0 && p[5] == 0) || p[0] == 0xE9 ||
           (p[0] == 0x48 && p[1] == 0xB8 && p[10] == 0xFF && p[11] == 0xE0);
}

// A redirected call at offset `start` of the code: an `E8 rel32` whose opcode is in place in both copies, that calls
// into the code section in the file and to outside the image in the live code. `call_lands`, when given, is asked
// whether the new target is a place such a call is sent to (sigdb: a relay or a loaded module). The byte before a
// call cannot be checked for being an instruction boundary here, so an E8 inside another instruction's immediate
// passes when its four bytes read as such a pair of targets; the tests on both targets are what keeps that rare.
inline bool redirected_call_at(const uint8_t* file, const uint8_t* live, size_t size, size_t start, uintptr_t live_text,
                               uintptr_t image_base, size_t image_size, bool (*call_lands)(uintptr_t) = nullptr) {
    if (start >= size || size - start < 5 || file[start] != 0xE8 || live[start] != 0xE8) return false;
    int32_t was, now;
    std::memcpy(&was, file + start + 1, 4);
    std::memcpy(&now, live + start + 1, 4);
    if (was == now) return false;
    const uintptr_t next = live_text + start + 5;
    const uintptr_t from = next + static_cast<intptr_t>(was), to = next + static_cast<intptr_t>(now);
    const bool in_code = from >= live_text && from - live_text < size;
    const bool outside = to < image_base || to - image_base >= image_size;
    return in_code && outside && (!call_lands || call_lands(to));
}

// Differences between the file's code and the live code that no hook form explains: bytes another mod changed in
// place (a check skipped, a jump made unconditional, a number raised). `sites` counts them as places (changed bytes
// closer than 16 bytes are one place), `first` holds the offsets of the first few.
struct OtherDiffs {
    size_t sites = 0, bytes = 0;
    std::vector<uint32_t> first;
};

// Sorts the differences into hooks and the rest. A hook is a familiar inline jump to outside the image (plus the
// hooker's instruction-padding NOPs) or a redirected call (above). ShroudForge's native runtime sends the main menu's
// two Settings calls to a relay next to the image this way, before Kiln's scan (players' reports of 10 and 13 Nov
// 2026). Everything else is returned: in-place patches, which is how many mods change the game (The Worst Map Mod's
// four map marker patches, a player's report of 14 Nov 2026).
inline OtherDiffs other_differences(const uint8_t* file, const uint8_t* live, size_t size,
                                    uintptr_t live_text, uintptr_t image_base, size_t image_size,
                                    bool (*call_lands)(uintptr_t) = nullptr, size_t keep = 8, bool first_only = false) {
    auto outside = [&](uintptr_t a) { return a < image_base || a - image_base >= image_size; };
    OtherDiffs out;
    size_t last = 0;
    bool any = false;
    for (size_t i = 0; i < size;) {
        if (file[i] == live[i]) { ++i; continue; }
        bool covered = false;
        for (size_t start = i > 13 ? i - 13 : 0; start <= i; ++start) {
            const auto* p = live + start;
            const size_t left = size - start;
            size_t width = 0;
            uintptr_t target = 0;
            if (i - start >= 1 && i - start <= 4 &&
                redirected_call_at(file, live, size, start, live_text, image_base, image_size, call_lands)) {
                // a call whose operand alone changed: no padding belongs to it
                i = start + 5;
                covered = true;
                break;
            }
            if (left >= 5 && p[0] == 0xE9) {
                int32_t d;
                std::memcpy(&d, p + 1, 4);
                width = 5, target = live_text + start + 5 + static_cast<intptr_t>(d);
            } else if (left >= 14 && p[0] == 0xFF && p[1] == 0x25 &&
                       p[2] == 0 && p[3] == 0 && p[4] == 0 && p[5] == 0) {
                width = 14;
                std::memcpy(&target, p + 6, 8);
            } else if (left >= 12 && p[0] == 0x48 && p[1] == 0xB8 && p[10] == 0xFF && p[11] == 0xE0) {
                width = 12;
                std::memcpy(&target, p + 2, 8);
            }
            if (!width || !outside(target)) continue;
            size_t end = start + width;
            // the NOPs a hook fills the rest of the instructions it replaced with: up to 20, what Greeneye Loader
            // writes behind its 12-byte jump (a hook of at most 32 bytes); the others stay well below that
            for (size_t pad = 0; pad < 20 && end < size && live[end] == 0x90; ++pad) ++end;
            if (i < end) { i = end; covered = true; break; }
        }
        if (covered) continue;
        ++out.bytes;
        if (!any || i - last > 16) {
            ++out.sites;
            if (out.first.size() < keep) out.first.push_back(static_cast<uint32_t>(i));
            if (first_only) return out;
        }
        any = true;
        last = i;
        ++i;
    }
    return out;
}

// True when every difference is a hook (other_differences finds nothing else).
inline bool only_hook_differences(const uint8_t* file, const uint8_t* live, size_t size,
                                  uintptr_t live_text, uintptr_t image_base, size_t image_size,
                                  bool (*call_lands)(uintptr_t) = nullptr) {
    return other_differences(file, live, size, live_text, image_base, image_size, call_lands, 0, true).sites == 0;
}

// Live-vs-file differences in .text: [rva, rva + len) runs, sorted. Who patched the code (hooks) shows up here.
struct DiffRun {
    uint32_t rva, len;
};
// Compares two equally long byte ranges (the file's relocated .text and the live .text). Runs closer than `merge`
// bytes are joined; at most `max_runs` are recorded but every differing byte is counted.
inline std::vector<DiffRun> diff_runs(const uint8_t* a, const uint8_t* b, size_t size, uint32_t base_rva,
                                      size_t* differing, size_t max_runs = 4096, size_t merge = 16) {
    std::vector<DiffRun> runs;
    size_t count = 0;
    constexpr size_t kBlock = 256;
    for (size_t off = 0; off < size; off += kBlock) {
        const size_t n = std::min(kBlock, size - off);
        if (std::memcmp(a + off, b + off, n) == 0) continue;
        for (size_t i = off; i < off + n; ++i) {
            if (a[i] == b[i]) continue;
            ++count;
            const uint32_t rva = base_rva + static_cast<uint32_t>(i);
            if (!runs.empty() && rva - (runs.back().rva + runs.back().len) <= merge) {
                runs.back().len = rva - runs.back().rva + 1;
            } else if (runs.size() < max_runs) {
                runs.push_back({rva, 1});
            }
        }
    }
    if (differing) *differing = count;
    return runs;
}

}  // namespace kiln::pe
