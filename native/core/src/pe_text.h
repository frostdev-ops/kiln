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
    uint32_t size_of_image = 0;
    uint32_t text_rva = 0, text_vsize = 0;    // .text as mapped
    uint32_t text_raw = 0, text_rawsize = 0;  // .text in the file
    // Fixups that touch .text, as RVAs of the patched field, sorted: IMAGE_REL_BASED_DIR64 (8 bytes) and
    // IMAGE_REL_BASED_HIGHLOW (4 bytes). A field that only partly lies inside .text is listed too.
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
    if (!read(file, size, 0, &mz) || mz != 0x5A4D) return error = "no MZ header", false;
    if (!read(file, size, 0x3C, &pe) || !read(file, size, pe, &sig) || sig != 0x00004550)
        return error = "no PE header", false;
    uint16_t machine = 0, nsec = 0, optsz = 0, magic = 0;
    read(file, size, pe + 4, &machine);
    read(file, size, pe + 6, &nsec);
    read(file, size, pe + 20, &optsz);
    const size_t opt = pe + 24;
    if (!read(file, size, opt, &magic) || magic != 0x20B) return error = "not a PE32+ image", false;
    if (machine != 0x8664) return error = "not an x64 image", false;
    uint32_t ndirs = 0, reloc_rva = 0, reloc_size = 0;
    if (!read(file, size, opt + 24, &out.image_base) || !read(file, size, opt + 56, &out.size_of_image) ||
        !read(file, size, opt + 108, &ndirs))
        return error = "truncated optional header", false;
    if (ndirs > 5 && optsz >= 112 + 6 * 8) {  // directory 5: base relocations
        read(file, size, opt + 112 + 5 * 8, &reloc_rva);
        read(file, size, opt + 112 + 5 * 8 + 4, &reloc_size);
    }
    struct Sec {
        char name[8];
        uint32_t vsize, va, rawsize, raw;
    };
    std::vector<Sec> secs;
    size_t off = opt + optsz;
    for (uint16_t i = 0; i < nsec; ++i, off += 40) {
        Sec s{};
        if (off > size || size - off < 40) return error = "truncated section table", false;
        std::memcpy(s.name, file + off, 8);
        read(file, size, off + 8, &s.vsize);
        read(file, size, off + 12, &s.va);
        read(file, size, off + 16, &s.rawsize);
        read(file, size, off + 20, &s.raw);
        secs.push_back(s);
        if (std::memcmp(s.name, ".text\0\0\0", 8) == 0) {
            out.text_rva = s.va, out.text_vsize = s.vsize, out.text_raw = s.raw, out.text_rawsize = s.rawsize;
        }
    }
    if (!out.text_rva) return error = "no .text section", false;
    if (out.text_raw > size || size - out.text_raw < out.text_rawsize) return error = ".text runs past the file", false;

    auto file_offset = [&](uint32_t rva, size_t* o) {
        for (const Sec& s : secs)
            if (rva >= s.va && rva - s.va < std::max(s.vsize, s.rawsize) && rva - s.va < s.rawsize) {
                *o = static_cast<size_t>(s.raw) + (rva - s.va);
                return true;
            }
        return false;
    };
    size_t at = 0;
    if (reloc_rva && reloc_size) {
        if (!file_offset(reloc_rva, &at)) return error = "base relocations are not in the file", false;
        const size_t end = std::min(size, at + reloc_size);
        const uint32_t lo = out.text_rva, hi = out.text_rva + out.text_vsize;
        while (at + 8 <= end) {
            uint32_t page = 0, block = 0;
            read(file, size, at, &page);
            read(file, size, at + 4, &block);
            if (block < 8 || at + block > end) break;
            for (size_t k = at + 8; k + 2 <= at + block; k += 2) {
                uint16_t e = 0;
                read(file, size, k, &e);
                const uint32_t rva = page + (e & 0xFFF);
                const unsigned type = e >> 12;
                if (type == 10 && rva + 8 > lo && rva < hi) out.relocs64.push_back(rva);
                else if (type == 3 && rva + 4 > lo && rva < hi) out.relocs32.push_back(rva);
            }
            at += block;
        }
    }
    std::sort(out.relocs64.begin(), out.relocs64.end());
    std::sort(out.relocs32.begin(), out.relocs32.end());
    return true;
}

// Applies the fixups the loader applies for a load at `actual_base` to `text`: writable bytes of .text, `text_size`
// long, starting at RVA ft.text_rva. A field that straddles either end of the buffer gets its in-range bytes patched
// as the loader would. Returns the number of fixups that changed bytes (0 when the image is at its preferred base).
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

    const uint8_t* end() const { return scan + size; }
    // The live address of the byte at `p` (a pointer into the scanned bytes).
    uintptr_t to_live(const uint8_t* p) const { return live + static_cast<uintptr_t>(p - scan); }
    // The scanned byte that holds live address `a`, or nullptr when `a` is not in the range.
    const uint8_t* to_scan(uintptr_t a) const { return a >= live && a - live < size ? scan + (a - live) : nullptr; }
    // live - scan: add it to a pointer into the scanned bytes to get the live address (rel32 targets).
    intptr_t delta() const { return static_cast<intptr_t>(live - reinterpret_cast<uintptr_t>(scan)); }
};

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
