// SPDX-License-Identifier: MIT
#define main boundary_fixture
#include "presentation_api.cpp"
#undef main
#include "../brush_frame.h"
static uint32_t held_item;
static uint32_t KILN_CALL fixture_held() {return held_item;}
static KilnMidFn mid_callback;static void* mid_user;static uintptr_t mid_site;
static KilnResult KILN_CALL checked_mid(KilnOwner,uintptr_t target,const KilnHookOrder*,KilnMidFn fn,void* user,KilnHandle* out,uint32_t* unchecked) {
    mid_callback=fn;mid_user=user;mid_site=target;*out=55;*unchecked=0;return KILN_OK;
}
struct Frame {
    alignas(16) std::array<uint8_t,0x2b28> bytes{};
    uint32_t info{};
    KilnRegisters regs{};
    explicit Frame(uint32_t item) :info(item) {
        regs.rsp=reinterpret_cast<uintptr_t>(bytes.data());regs.rbp=regs.rsp+0x100;
        regs.rcx=regs.rbp+0x690;regs.rdx=regs.rsp+0x40;regs.r8=regs.rbp+0x100;regs.r9=regs.rsp+0x30;
        const uintptr_t ptr=reinterpret_cast<uintptr_t>(&info),data=regs.rbp+0x6b8;const uint64_t cap=16;
        std::memcpy(reinterpret_cast<void*>(regs.rbp+0x118),&item,4);std::memcpy(reinterpret_cast<void*>(regs.rbp+0xe0),&ptr,sizeof ptr);
        std::memcpy(reinterpret_cast<void*>(regs.rcx),&data,sizeof data);std::memcpy(reinterpret_cast<void*>(regs.rcx+16),&cap,8);
        std::memcpy(reinterpret_cast<void*>(regs.r9),overgrowth_brush_tag.data(),16);std::memcpy(reinterpret_cast<void*>(regs.r8),overgrowth_brush_tag.data(),16);
    }
    bool own_tag(const uint8_t* tag) const {return !std::memcmp(reinterpret_cast<const void*>(regs.r9),tag,16);}
    uint64_t count() const {uint64_t c;std::memcpy(&c,reinterpret_cast<const void*>(regs.rcx+8),8);return c;}
    struct Row {uint64_t lead;uint32_t item;uint8_t zero[0x34];uint32_t nominator,denominator;};
    Row row(size_t i) const {Row r;std::memcpy(&r,reinterpret_cast<const void*>(regs.rbp+0x6b8+i*72),72);return r;}
};
static void invoke(uintptr_t site,Frame& frame) {if(site==mid_site)mid_callback(&frame.regs,mid_user);}
static std::vector<std::string> logged;
static void KILN_CALL capture_log(KilnOwner,int32_t,KilnStr text) {logged.emplace_back(text.ptr,text.len);}
int main() {
    boundary_fixture();
    KilnApi fake{};fake.size=sizeof fake;fake.abi_version=KILN_ABI_VERSION;fake.service=&service;fake.subscribe_tick=&tick;fake.unsubscribe=&unsubscribe;fake.owner_revoke=&owner_revoke;
    api=&fake;hooks=&fake_hooks;child=88;retiring=99;
    for(uint32_t i=0;i<KILN_TOOL_HOOK_COUNT;++i)kiln_tools_report_hook(i,1);
    auto family=definition();KilnToolBrush brushes[]={{3155237504u,0,0.5},{3172015123u,0,1},{3255903218u,0,2}};family.brushes=brushes;family.brush_count=3;
    KilnHandle handle=0;CHECK(kiln_tools_define_family(7,&family,&handle)==KILN_OK);
    fake_hooks.mid2_checked=&checked_mid;
    brush_query_resume.store(0xc3fe1d);
    CHECK(brush_frame_hook.install_checked(&fake,child,0xc3fe18,&brush_query_frame,"brush fixture"));
    kiln_tools_presentation_set_held_provider(&fixture_held);held_item=family.tool;
    Frame own(family.tool);const auto saved=own.regs;invoke(0xc3fe18,own);
    CHECK(own.own_tag(family.identity));CHECK(!std::memcmp(reinterpret_cast<const void*>(own.regs.r8),family.identity,16));
    CHECK(!std::memcmp(&saved,&own.regs,sizeof saved)); // No register/stack redirection for a successful call.
    // The query scans only the game data's terraformingTools list, which never holds a runtime brush: the family's
    // own rows are written into the fresh output, in declared order, before the native call sorts them by ratio.
    CHECK(!own.own_tag(overgrowth_brush_tag.data()));
    CHECK(own.count()==3);
    for(size_t i=0;i<3;++i)CHECK(own.row(i).item==brushes[i].item&&own.row(i).nominator==i+1&&own.row(i).denominator==1);
#ifndef KILN_RED_BASELINE
    CHECK(brush_trace.count(BrushFrameExit::Patched)==1&&brush_trace.last()==BrushFrameExit::Patched);
#endif
    Frame vanilla(0xbe8b0750);const auto vanilla_bytes=vanilla.bytes;held_item=0xbe8b0750;invoke(0xc3fe18,vanilla);CHECK(vanilla.bytes==vanilla_bytes);
    held_item=family.tool;
    for(auto other:{uintptr_t(0xd180a0),uintptr_t(0xd20190),uintptr_t(0xd6aa05)}) {Frame callback(family.tool);const auto before=callback.bytes;invoke(other,callback);CHECK(callback.bytes==before);}
    Frame stale(0xbe8b0750);const auto stale_bytes=stale.bytes;invoke(0xc3fe18,stale);CHECK(stale.bytes==stale_bytes);
    Frame changed(family.tool);changed.info=123;const auto changed_bytes=changed.bytes;invoke(0xc3fe18,changed);CHECK(changed.bytes==changed_bytes);
    Frame nested(family.tool);const auto nested_bytes=nested.bytes;held_item=0;invoke(0xc3fe18,nested);CHECK(nested.bytes==nested_bytes); // No inherited scope from outer own call.
    held_item=family.tool;Frame decay(family.tool);std::swap(decay.regs.r9,decay.regs.rdx);const auto decay_bytes=decay.bytes;invoke(0xc3fe18,decay);CHECK(decay.bytes==decay_bytes);
    Frame modified(family.tool);*reinterpret_cast<uint8_t*>(modified.regs.r9)=0;const auto modified_bytes=modified.bytes;invoke(0xc3fe18,modified);CHECK(modified.bytes==modified_bytes);
#ifndef KILN_RED_BASELINE
    // Every refused frame above was counted under its own exit; one change of state each way publishes one line.
    CHECK(brush_trace.count(BrushFrameExit::NoFamily)==2&&brush_trace.count(BrushFrameExit::NotSelected)==1&&
          brush_trace.count(BrushFrameExit::NotResolved)==1&&brush_trace.count(BrushFrameExit::FrameMismatch)==1&&
          brush_trace.count(BrushFrameExit::TagMismatch)==1&&brush_trace.count(BrushFrameExit::Patched)==1);
    logged.clear();fake.log=&capture_log;fake.build_info=&client_build;parent_owner=66;fake_caps.report=&report_cap;
    publish_brush_trace();
    CHECK(logged.size()==1&&logged[0].find("tools: brush picker: tag mismatch")!=std::string::npos&&
          logged[0].find("state changes since the last line")!=std::string::npos&&logged[0].find("patched 1 (3 rows)")!=std::string::npos);
    CHECK(capabilities["tools.brush_families"].text.find("picker frames: ")!=std::string::npos&&
          capabilities["tools.brush_families"].text.find("no held family 2")!=std::string::npos);
    publish_brush_trace();CHECK(logged.size()==1); // nothing changed: no second line
    for(int i=0;i<100;++i){Frame again(family.tool);invoke(0xc3fe18,again);CHECK(again.count()==3);}
    trace_published-=std::chrono::seconds(2);publish_brush_trace();
    CHECK(logged.size()==2&&logged[1].find("tools: brush picker: patched")!=std::string::npos&&logged[1].find("state changes")==std::string::npos);
    for(int i=0;i<100;++i){Frame again(family.tool);invoke(0xc3fe18,again);}publish_brush_trace();CHECK(logged.size()==2); // 100 frames, one state
    {Frame vanilla_again(0xbe8b0750);held_item=0xbe8b0750;invoke(0xc3fe18,vanilla_again);held_item=family.tool;}
    publish_brush_trace();CHECK(logged.size()==2); // within a second of the last line: held until the next publication
    trace_published-=std::chrono::seconds(2);publish_brush_trace();CHECK(logged.size()==3&&logged[2].find("no held family")!=std::string::npos);
    fake.log=nullptr;
#endif
    // kiln#153: picking a brush in the picker makes the hotbar hold that brush (the game's CreateBuildingItemAction puts
    // the picked row's item in the selected actionbar slot, as a vanilla Overgrowth shape takes the tool's place). The
    // frame's selected hotbar item is then the brush, not the root: the frame must stay the family's, for every brush, or
    // the vanilla query lists the vanilla Overgrowth shapes and the next pick hands the player the vanilla tool.
    for(const auto& brush:brushes) {
        for(const uint32_t held:{brush.item,family.tool}) { // the held sample may still name the item before the pick
            held_item=held;Frame picked(brush.item);invoke(0xc3fe18,picked);
            CHECK(picked.own_tag(family.identity)&&picked.count()==3);
            for(size_t i=0;i<3;++i)CHECK(picked.row(i).item==brushes[i].item&&picked.row(i).nominator==i+1);
            CHECK(brush_trace.last()==BrushFrameExit::Patched);
        }
    }
    held_item=brushes[2].item;
    {Frame resolved_other(brushes[2].item);resolved_other.info=brushes[0].item;const auto before=resolved_other.bytes; // ItemInfo of another item
     invoke(0xc3fe18,resolved_other);CHECK(resolved_other.bytes==before&&brush_trace.last()==BrushFrameExit::NotResolved);}
    {Frame vanilla_slot(0xbe8b0750);const auto before=vanilla_slot.bytes;invoke(0xc3fe18,vanilla_slot); // a vanilla item in the slot
     CHECK(vanilla_slot.bytes==before&&brush_trace.last()==BrushFrameExit::NotSelected);}
    {   // another family's brush in the slot while this family is held stays that slot's, not this family's
        auto other=definition(12);KilnToolBrush other_brush{777,0,1};other.brushes=&other_brush;other.brush_count=1;
        KilnHandle other_handle=0;CHECK(kiln_tools_define_family(7,&other,&other_handle)==KILN_OK);
        Frame foreign(777);const auto before=foreign.bytes;invoke(0xc3fe18,foreign);
        CHECK(foreign.bytes==before&&brush_trace.last()==BrushFrameExit::NotSelected);
        CHECK(kiln_tools_remove_family(7,other_handle)==KILN_OK);
    }
    {   // a family of 20: the picker lists the first 16, and a brush past them in the slot (crafted, traded) is still the family's
        auto wide=definition(20);std::vector<KilnToolBrush> many;for(uint32_t i=0;i<20;++i)many.push_back({0x51000000u+i,0,0.5+0.25*i});
        wide.brushes=many.data();wide.brush_count=20;KilnHandle wide_handle=0;CHECK(kiln_tools_define_family(7,&wide,&wide_handle)==KILN_OK);
        held_item=many[18].item;Frame past(many[18].item);invoke(0xc3fe18,past);
        CHECK(past.own_tag(wide.identity)&&past.count()==16&&past.row(15).item==many[15].item&&brush_trace.last()==BrushFrameExit::Patched);
        CHECK(kiln_tools_remove_family(7,wide_handle)==KILN_OK);
    }
    held_item=family.tool;
#ifdef _WIN32
    Frame fault(family.tool);auto* memory=static_cast<uint8_t*>(VirtualAlloc(nullptr,fault.bytes.size(),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));CHECK(memory);
    std::memcpy(memory,fault.bytes.data(),fault.bytes.size());
    const auto delta=reinterpret_cast<uintptr_t>(memory)-fault.regs.rsp;
    fault.regs.rsp+=delta;fault.regs.rbp+=delta;fault.regs.rcx+=delta;fault.regs.rdx+=delta;fault.regs.r8+=delta;fault.regs.r9+=delta;
    const uintptr_t data=fault.regs.rbp+0x6b8;std::memcpy(reinterpret_cast<void*>(fault.regs.rcx),&data,sizeof data);
    DWORD old=0;CHECK(VirtualProtect(memory,4096,PAGE_READONLY,&old));invoke(0xc3fe18,fault);
    CHECK(fault.regs.rip==0xc3fe1d&&!kiln_tools_hook_installed(KILN_TOOL_HOOK_BRUSH_PICKER));
    CHECK(fault.own_tag(overgrowth_brush_tag.data()));CHECK(VirtualProtect(memory,4096,old,&old));CHECK(VirtualFree(memory,0,MEM_RELEASE));
#endif
    kiln_tools_presentation_uninstall();
    std::cout<<"brush frame: "<<checks<<" checks passed (includes boundary fixture)\n";
}
