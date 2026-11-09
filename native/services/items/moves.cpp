// SPDX-License-Identifier: MIT
// kiln.items: moves, transfer rules and container names without game memory. See moves.h.
#include "moves.h"

#include <algorithm>
#include <cstring>
#include <vector>

namespace kitems {

bool find_destination(const Page* pages, size_t n, uint32_t item, uint32_t max_stack, bool gear, const uint32_t* skip,
                      size_t skip_n, uint64_t prefer, uint64_t* out) {
    auto skipped = [&](uint32_t entity) {
        for (size_t i = 0; i < skip_n; ++i)
            if (skip[i] == entity) return true;
        return false;
    };
    auto fits = [&](const Stack& s) {
        if (is_empty(s)) return true;
        return !gear && s.item == item && s.count < max_stack && pide_of(s) == 0;
    };
    if (prefer) {
        for (size_t p = 0; p < n; ++p) {
            if (pages[p].entity == 0 || skipped(pages[p].entity)) continue;
            for (uint32_t k = 0; k < kSlotsPerPage; ++k) {
                if (KILN_SLOT_ID(pages[p].entity, k) == prefer && fits(pages[p].s[k])) {
                    *out = prefer;
                    return true;
                }
            }
        }
    }
    bool have_empty = false;
    uint64_t empty = 0;
    for (size_t p = 0; p < n; ++p) {
        if (pages[p].entity == 0 || skipped(pages[p].entity)) continue;
        for (uint32_t k = 0; k < kSlotsPerPage; ++k) {
            const Stack& s = pages[p].s[k];
            if (is_empty(s)) {
                if (!have_empty) {
                    have_empty = true;
                    empty = KILN_SLOT_ID(pages[p].entity, k);
                }
            } else if (!gear && s.item == item && s.count < max_stack && pide_of(s) == 0) {
                *out = KILN_SLOT_ID(pages[p].entity, k);
                return true;
            }
        }
    }
    if (have_empty) *out = empty;
    return have_empty;
}

namespace {
struct Piece {
    uint64_t slot;
    uint32_t item;
    uint32_t amount;
    uint32_t pide;
};
}  // namespace

size_t build_ledger(const Page* from_before, const Page* from_after, size_t from_n, const Page* to_before,
                    const Page* to_after, size_t to_n, Change* out, size_t capacity, LedgerTotals* totals) {
    LedgerTotals t{};
    std::vector<Piece> src, dst;
    for (size_t p = 0; p < from_n; ++p) {
        for (uint32_t k = 0; k < kSlotsPerPage; ++k) {
            const Stack& b = from_before[p].s[k];
            const Stack& a = from_after[p].s[k];
            if (is_empty(b)) continue;
            if (pide_of(b) != 0) {  // gear leaves when the slot no longer holds its item entity
                if (pide_of(a) == pide_of(b) && !is_empty(a)) continue;
                src.push_back({b.slot, b.item, 1, pide_of(b)});
                continue;
            }
            const bool same = !is_empty(a) && a.item == b.item;
            const uint32_t left = same ? std::min(a.count, b.count) : 0;
            if (b.count > left) src.push_back({b.slot, b.item, b.count - left, 0});
        }
    }
    for (size_t p = 0; p < to_n; ++p) {
        for (uint32_t k = 0; k < kSlotsPerPage; ++k) {
            const Stack& b = to_before[p].s[k];
            const Stack& a = to_after[p].s[k];
            if (is_empty(a)) continue;
            if (pide_of(a) != 0) {
                if (pide_of(b) == pide_of(a) && !is_empty(b)) continue;
                dst.push_back({a.slot, a.item, 1, pide_of(a)});
                continue;
            }
            const bool same = !is_empty(b) && b.item == a.item;
            const uint32_t had = same ? b.count : 0;
            if (a.count > had) dst.push_back({a.slot, a.item, a.count - had, 0});
        }
    }
    std::vector<Change> all;
    auto emit = [&](const Piece& s, const Piece& d, uint32_t amount) {
        all.push_back(Change{s.slot, d.slot, s.item, amount, s.pide});
        t.done += amount;
    };
    std::vector<bool> src_used(src.size(), false), dst_used(dst.size(), false);
    std::vector<bool> src_counted(src.size(), false);
    // gear: paired by its item entity
    for (size_t i = 0; i < src.size(); ++i) {
        if (src[i].pide == 0) continue;
        for (size_t j = 0; j < dst.size(); ++j) {
            if (dst_used[j] || dst[j].pide != src[i].pide) continue;
            emit(src[i], dst[j], 1);
            src_used[i] = dst_used[j] = true;
            src_counted[i] = true;
            ++t.stacks;
            break;
        }
    }
    // plain stacks: per item, in slot order
    for (size_t i = 0; i < src.size(); ++i) {
        if (src[i].pide != 0) continue;
        uint32_t left = src[i].amount;
        for (size_t j = 0; j < dst.size() && left > 0; ++j) {
            if (dst_used[j] || dst[j].pide != 0 || dst[j].item != src[i].item) continue;
            const uint32_t take = std::min(left, dst[j].amount);
            Piece s = src[i];
            s.amount = take;
            emit(s, dst[j], take);
            left -= take;
            dst[j].amount -= take;
            if (dst[j].amount == 0) dst_used[j] = true;
            if (!src_counted[i]) {
                src_counted[i] = true;
                ++t.stacks;
            }
        }
        src[i].amount = left;
        if (left == 0) src_used[i] = true;
    }
    for (size_t i = 0; i < src.size(); ++i)
        if (!src_used[i]) t.lost += src[i].amount;
    for (size_t j = 0; j < dst.size(); ++j)
        if (!dst_used[j]) t.gained += dst[j].amount;
    if (totals) *totals = t;
    // in the order things left the source (slot order), then the target's
    std::stable_sort(all.begin(), all.end(), [](const Change& a, const Change& b) {
        return a.from_slot != b.from_slot ? a.from_slot < b.from_slot : a.to_slot < b.to_slot;
    });
    const size_t n = std::min(all.size(), capacity);
    for (size_t i = 0; i < n; ++i) out[i] = all[i];
    return n;
}

void apply_rules(const RuleView* rules, size_t n, uint32_t kind, uint32_t from, uint32_t to, const Page* from_pages,
                 size_t from_n, Verdict* v) {
    for (size_t r = 0; r < n; ++r) {
        const RuleView& rule = rules[r];
        const uint32_t kinds = rule.kinds ? rule.kinds : KILN_TRANSFER_ANY;
        if (!(kinds & kind)) continue;
        switch (rule.type) {
            case KILN_TRANSFER_DENY:
                if (rule.entity != 0 && (rule.entity == from || rule.entity == to)) v->deny = true;
                break;
            case KILN_TRANSFER_KEEP_SLOT:
                for (size_t p = 0; p < from_n; ++p)
                    for (uint32_t k = 0; k < kSlotsPerPage; ++k)
                        if (from_pages[p].s[k].slot == rule.slot && !is_empty(from_pages[p].s[k])) keep_slot(v, rule.slot);
                break;
            case KILN_TRANSFER_KEEP_ITEM:
                if (rule.entity != 0 && rule.entity != from) break;
                for (size_t p = 0; p < from_n; ++p)
                    for (uint32_t k = 0; k < kSlotsPerPage; ++k)
                        if (!is_empty(from_pages[p].s[k]) && from_pages[p].s[k].item == rule.item)
                            keep_slot(v, from_pages[p].s[k].slot);
                break;
            default:
                break;
        }
    }
}

uint64_t fnv1a64(const uint8_t* text, size_t len) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (size_t i = 0; i < len; ++i) h = (h ^ text[i]) * 0x100000001b3ull;
    return h;
}

bool parse_name_record(const uint8_t* rec, size_t size, uintptr_t addr, uint64_t key, size_t max_offset, char* out,
                       size_t out_cap, uint32_t* len, size_t* offset) {
    constexpr size_t kText = 0x28, kCap = 0x100;
    for (size_t o = 0; o + kText + 1 <= size && o <= max_offset; o += 8) {
        uint64_t k, ptr, cap;
        uint32_t n;
        std::memcpy(&k, rec + o, 8);
        if (k != key) continue;
        if (o + kText + kCap > size) continue;
        std::memcpy(&ptr, rec + o + 8, 8);
        std::memcpy(&n, rec + o + 0x10, 4);
        std::memcpy(&cap, rec + o + 0x18, 8);
        if (ptr != static_cast<uint64_t>(addr + o + kText) || cap != kCap || n < 1 || n >= kCap) continue;
        const uint8_t* text = rec + o + kText;
        if (text[n] != 0) continue;
        if (fnv1a64(text, n) != key) continue;
        if (out && out_cap) {
            const size_t c = std::min<size_t>(n, out_cap - 1);
            std::memcpy(out, text, c);
            out[c] = 0;
        }
        if (len) *len = n;
        if (offset) *offset = o;
        return true;
    }
    return false;
}

}  // namespace kitems
