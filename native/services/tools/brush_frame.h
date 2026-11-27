// SPDX-License-Identifier: MIT
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#ifdef _WIN32
#include <windows.h>
#endif
namespace kiln::tools {
// Exact client26430625 Cc3fe18 call-site layout. No borrowed frame pointer survives this call.
struct BrushFrame {uintptr_t stack,frame,output,exclude,ratio,include;};
enum class BrushFrameResult { Unchanged, Patched, WriteFault };
// Every way out of the brush-frame adapter, in the order the checks run. The trace counts each one.
enum class BrushFrameExit : uint8_t {
    Disabled,       // no qualified resume address: the hook is not installed, or it retired after a write fault
    NoFamily,       // no registered family is held (a vanilla tool, or no current held-item observation)
    NoIdentity,     // a family is held but its identity record is gone (removed between the two lookups)
    FrameMismatch,  // not the Overgrowth branch's owned temporaries (the Decay branch, another frame)
    NotSelected,    // the selected hotbar item is neither the family's root nor one of its brushes, or no ItemInfo resolved
    NotResolved,    // the resolved ItemInfo names another item than the selected hotbar item
    OutputNotFresh, // the output is not the fresh, empty, capacity-16 inline array
    TagMismatch,    // include/ratio do not hold the vanilla Overgrowth tag
    ReadFault,      // a guarded read faulted
    Patched,        // the family's rows were written and include/ratio replaced
    WriteFault,     // a write faulted; the adapter skips this one query
    Count
};
inline constexpr std::array<uint8_t,16> overgrowth_brush_tag{
    0xcf,0xa6,0xae,0x08,0xe8,0xe0,0x6c,0x4d,0x8a,0x29,0xa2,0x1f,0x46,0x93,0xe0,0x06};
// One 72-byte presentation row of the brush query (0xd25b50, docs/re/building.md 1.2.2): it zeroes the row, writes the
// item id at +8 and copies the ratio tag's nominator/denominator to +0x40/+0x44; 0xb0b110 then sorts by that ratio.
struct BrushRow {uint64_t lead;uint32_t item;uint8_t zero[0x40-0x0c];uint32_t nominator,denominator;};
static_assert(sizeof(BrushRow)==72&&offsetof(BrushRow,item)==8&&offsetof(BrushRow,nominator)==0x40&&offsetof(BrushRow,denominator)==0x44);
inline constexpr size_t brush_rows_capacity=16; // the frame's inline SmallDynamicArray; its grow callback may be null
inline constexpr size_t brush_family_capacity=64; // a family's brushes (presentation.h Registry::valid)
// Every brush of the held family, in declared order: the first 16 become the picker's rows, all of them are the family's.
struct BrushFamilyRows {const uint32_t* items;size_t count;};
struct BrushFrameOutcome {BrushFrameResult result;BrushFrameExit exit;uint32_t rows;bool truncated;};
// The query scans only ItemRegistryResource.terraformingTools, a list in the game's data, never the runtime registry, so
// a family's brushes (runtime items) are never in it. Admission therefore writes the family's own rows into the fresh
// output, in declared order with ratio i+1 over 1, and replaces include/ratio with the family identity: no list item
// carries it, so the native query appends nothing and only sorts these rows by ratio (ascending: declared order).
// The frame's selected hotbar item is the family's when it is the root or any of its brushes: picking a row puts that
// brush in the actionbar slot (CreateBuildingItemAction, as a vanilla Overgrowth shape replaces its tool), so from the
// first pick on the slot holds a brush. A root-only check sent every later frame to the vanilla query, which lists the
// vanilla Overgrowth shapes, and the next pick gave the player the vanilla tool (kiln#153).
inline BrushFrameOutcome patch_brush_frame(const BrushFrame& f,uint32_t root,const std::array<uint8_t,16>& tag,BrushFamilyRows brushes) {
    const auto unchanged=[](BrushFrameExit exit){return BrushFrameOutcome{BrushFrameResult::Unchanged,exit,0,false};};
    const size_t rows=brushes.items?(brushes.count<brush_rows_capacity?brushes.count:brush_rows_capacity):0;
    if(!root || !rows || brushes.count>brush_family_capacity)return unchanged(BrushFrameExit::NoFamily); // a registered family always has 1..64 brushes
    const auto in_family=[&](uint32_t item) {
        if(item==root)return true;
        for(size_t i=0;i<brushes.count;++i)if(brushes.items[i]==item)return true;
        return false;
    };
    // Native prologue: RBP=RSP+0x100. Accept only the Overgrowth branch's distinct owned temporaries.
    if(!f.stack || f.stack>UINTPTR_MAX-0x2b28 || f.frame!=f.stack+0x100 ||
       f.output!=f.frame+0x690 || f.include!=f.stack+0x30 || f.ratio!=f.frame+0x100 || f.exclude!=f.stack+0x40)
        return unchanged(BrushFrameExit::FrameMismatch);
    bool writing=false;
#ifdef _WIN32
    __try {
#endif
        uint32_t selected=0,resolved=0;uintptr_t info=0,data=0;uint64_t count=0,capacity=0;
        std::memcpy(&selected,reinterpret_cast<const void*>(f.frame+0x118),4);
        std::memcpy(&info,reinterpret_cast<const void*>(f.frame+0xe0),sizeof info);
        if(!selected || !in_family(selected) || !info)return unchanged(BrushFrameExit::NotSelected);
        std::memcpy(&resolved,reinterpret_cast<const void*>(info),4);
        if(resolved!=selected)return unchanged(BrushFrameExit::NotResolved);
        std::memcpy(&data,reinterpret_cast<const void*>(f.output),sizeof data);
        std::memcpy(&count,reinterpret_cast<const void*>(f.output+8),8);
        std::memcpy(&capacity,reinterpret_cast<const void*>(f.output+16),8);
        if(data!=f.frame+0x6b8 || count || capacity!=brush_rows_capacity)return unchanged(BrushFrameExit::OutputNotFresh);
        if(std::memcmp(reinterpret_cast<const void*>(f.include),overgrowth_brush_tag.data(),16) ||
           std::memcmp(reinterpret_cast<const void*>(f.ratio),overgrowth_brush_tag.data(),16))
            return unchanged(BrushFrameExit::TagMismatch);
        // Original tag values, registers, output and all persistent/callback arrays remain untouched until admission.
        // These two GUID buffers and the inline rows belong to this native frame, populated just before the call.
        writing=true;
        std::memcpy(reinterpret_cast<void*>(f.include),tag.data(),16);
        std::memcpy(reinterpret_cast<void*>(f.ratio),tag.data(),16);
        for(size_t i=0;i<rows;++i) {
            BrushRow row{};row.item=brushes.items[i];row.nominator=uint32_t(i+1);row.denominator=1;
            std::memcpy(reinterpret_cast<void*>(data+i*sizeof row),&row,sizeof row);
        }
        const uint64_t written=rows;std::memcpy(reinterpret_cast<void*>(f.output+8),&written,8); // count last
        return {BrushFrameResult::Patched,BrushFrameExit::Patched,uint32_t(rows),brushes.count>rows};
#ifdef _WIN32
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        // The adapter skips this one query if a write faulted; it must not execute a half-patched/unrestricted query.
        return writing?BrushFrameOutcome{BrushFrameResult::WriteFault,BrushFrameExit::WriteFault,0,false}:unchanged(BrushFrameExit::ReadFault);
    }
#else
    (void)writing;
#endif
}
} // namespace kiln::tools
