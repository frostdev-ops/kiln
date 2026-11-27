// SPDX-License-Identifier: MIT
#include "../presentation_api.h"
#include "../presentation.h"
#include "../held_observer.h"
#include "../brush_frame.h"
#include "../brush_trace.h"
#include "../internal.h"
#include "../impact_binding.h"
#include "kiln_hook.hpp"
#include "kiln_mid_frames.hpp"
#include "services/kiln_caps.h"
#include "services/kiln_world.h"
#include "../ammo_runtime.h"
#include <deque>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>

namespace {
using namespace kiln::tools;
const KilnApi* api{};
const KilnHooksApi* hooks{};
KilnOwner child{}, parent_owner{};
KilnHandle retiring{};
Registry families;
std::mutex gate;
struct Registration { KilnOwner owner; uint32_t tool; std::vector<uint32_t> ids; };
std::map<KilnHandle,Registration> registrations;
std::map<uint32_t,std::array<uint8_t,16>> identities;
KilnHandle next_handle=1;
kiln::RawHook lookup_hook, preview_hook, identity_hook;
kiln::MidHook2 selector_body_hook, selector_done_hook; // tools/MaterialSelector's two halves (selector_enter)
kiln::MidHook2 brush_frame_hook;
std::atomic<uintptr_t> brush_query_resume{};
BrushTrace brush_trace;
const KilnClientApi* trace_client{};
KilnHandle trace_tick_handle{};
std::chrono::steady_clock::time_point trace_published{};
HeldObserver held_observer;
std::atomic<KilnToolHeldItemProvider> held_provider{};
std::shared_ptr<const Family> held_family() {
    const auto provider=held_provider.load(std::memory_order_acquire);
    const auto item=provider?provider():held_observer.current();
    return families.family(families.root(item));
}
uint32_t fnv(const uint8_t* bytes,size_t length) {uint32_t h=0x811c9dc5u;for(size_t i=0;i<length;++i)h=(h^bytes[i])*0x01000193u;return h;}
uint32_t text_tag(const std::array<uint8_t,16>& identity,const char* purpose) {
    auto guid=identity;const auto hash=fnv(reinterpret_cast<const uint8_t*>(purpose),std::strlen(purpose));
    for(unsigned i=0;i<4;++i)guid[i]^=uint8_t(hash>>(i*8));guid[15]^=0x4b;return fnv(guid.data(),guid.size());
}
bool required_hooks(const KilnToolFamilyAction& action) {
    if(action.mode==KILN_TOOL_MODE_NONE)return true;
    if(!kiln_tools_hook_installed(KILN_TOOL_HOOK_EDGE_CHECK))return false;
    if(action.mode==KILN_TOOL_MODE_REMOVE)return kiln_tools_hook_installed(KILN_TOOL_HOOK_KERNEL_FILTER)&&kiln_tools_hook_installed(KILN_TOOL_HOOK_BUILDING_SUPPRESS);
    return action.mode==KILN_TOOL_MODE_ADD&&kiln_tools_hook_installed(KILN_TOOL_HOOK_COST)&&kiln_tools_hook_installed(KILN_TOOL_HOOK_MATERIAL_LOOKUP)&&
        kiln_tools_hook_installed(KILN_TOOL_HOOK_HISTORY_COST);
}
bool valid_action(const KilnToolFamilyAction& action) {
    if(action.mode>KILN_TOOL_MODE_ADD||action.reserved||action.flags!=KILN_TOOL_TERRAIN_ONLY||!action.program)return false;
    if(action.remove_cells[0]&1)return false;
    for(unsigned i=16;i<32;++i)if(action.remove_cells[i])return false;
    if(action.mode!=KILN_TOOL_MODE_REMOVE)for(auto cell:action.remove_cells)if(cell)return false;
    return true;
}
void remove_locked(const Registration& value) {
    kiln_tools_unbind_programs(value.owner,value.tool); // close admission before removing policy snapshots
    for(auto item:value.ids)kt::unbind_tool(item);
    families.remove(value.owner,value.tool);
    identities.erase(value.tool);
}
void KILN_CALL owner_retiring(KilnOwner owner,void*) {
    ammo::retire(owner);
    std::lock_guard lock(gate);
    for(auto i=registrations.begin();i!=registrations.end();) {
        if(i->second.owner!=owner){++i;continue;}
        remove_locked(i->second);i=registrations.erase(i);
    }
}
void KILN_CALL noop_tick(const KilnTickInfo*,void*) {}
bool copy_game_value(void* out,const void* source,size_t size) noexcept {
    if(!source)return false;
#ifdef _WIN32
    __try {
#endif
        std::memcpy(out,source,size);return true;
#ifdef _WIN32
    } __except(EXCEPTION_EXECUTE_HANDLER) {return false;}
#endif
}
uint32_t presentation_item(const uint32_t* item) noexcept {
    uint32_t value=0;return copy_game_value(&value,item,sizeof value)?value:0;
}
bool filter_selector_view(SelectorGroup* group,const std::vector<Material>& pool) noexcept {
#ifdef _WIN32
    __try {
#endif
        return filter_selector(*group,pool);
#ifdef _WIN32
    } __except(EXCEPTION_EXECUTE_HANDLER) {return false;}
#endif
}
bool filter_preview_view(PreviewOwner* out,const std::vector<Material>& pool) noexcept {
#ifdef _WIN32
    __try {
#endif
        return filter_preview(*out,pool);
#ifdef _WIN32
    } __except(EXCEPTION_EXECUTE_HANDLER) {return false;}
#endif
}
void clear_selector_view(SelectorGroup* group) noexcept {
#ifdef _WIN32
    __try {
#endif
        group->count=0;group->selected=0;group->has_selected=0;
#ifdef _WIN32
    } __except(EXCEPTION_EXECUTE_HANDLER) {}
#endif
}
void clear_preview_view(PreviewOwner* out) noexcept {
#ifdef _WIN32
    __try {
#endif
        out->view=nullptr;out->view_count=0;out->owned.count=0;out->selected_item=0;out->selected_count=0;
#ifdef _WIN32
    } __except(EXCEPTION_EXECUTE_HANDLER) {}
#endif
}

const uint8_t* KILN_CALL lookup(void* registry,uint8_t cell) {
    const auto* policy=kiln_tools_active_policy();
    if(policy&&policy->mode==KILN_TOOL_POLICY_ADD) {
        bool allowed=false;
        if(policy->pool_count<=64&&policy->pool)for(uint32_t i=0;i<policy->pool_count;++i)if(policy->pool[i].cell==cell){allowed=true;break;}
        if(!allowed)return nullptr;
    }
    return lookup_hook.call<const uint8_t*>(registry,cell);
}
// tools/MaterialSelector (C 0x40e5d0, void(groups, access, currentOnly)) builds the picker's seven groups. Kiln's two halves
// of it run inside the function, never at its entry: other mods find it by its first bytes (Building Tools 0.34.0 by its
// first 35, once, 4 s after it loads; it then checks bytes up to + 0xa7a and redirects the finalizer's call at + 0xa76),
// and an entry hook hid it from them ("full material list no", "partial area actions: not available"; XHL, 2026-11-25).
// selector_enter runs at tools/MaterialSelectorBody (+ 0x23, the first instruction after the prologue: rcx the groups, rdx
// the access, r8b currentOnly) and selector_leave at tools/MaterialSelectorDone (+ 0xa8a, the epilogue's first
// instruction: after the finalizer, on every path to the one ret; rsi still holds the groups). The function's rsp is the
// same at both sites and pairs them (kiln::MidFrames). presentation_sigdb_test.py checks all of this on the client.
struct SelectorCall {
    SelectorCall(SelectorGroup* g,void* a,uint8_t current_only):groups(g),access(a),ammo_frame(g,a,current_only),family(selector_family(a)) {}
    // A quiet-tick owned observation, sampled once per entry. Never borrow another preview's identity or policy TLS.
    static std::shared_ptr<const Family> selector_family(void* access) {
        auto family=held_family();
        // Access[15] is this update's resolved held ItemInfo, also used by the native category routing.
        // A previous Post sample must not filter a vanilla item selected later in the same client step.
        const uint32_t* item=nullptr;
        if(family&&(!access||!copy_game_value(&item,static_cast<const uint8_t*>(access)+0x78,sizeof item)||
            families.root(presentation_item(item))!=family->tool))family.reset();
        return family;
    }
    SelectorGroup* groups;void* access;
    kt::ToolPolicyScope no_impact_policy{nullptr}; // the game's selector body runs without a tool policy
    ammo::Frame ammo_frame;
    std::shared_ptr<const Family> family;
};
thread_local kiln::MidFrames<SelectorCall> selector_calls;
// The first half, before the game's selector body (frame: the function's rsp).
void selector_enter(SelectorGroup* groups,void* access,uint8_t current_only,uintptr_t frame) noexcept {
    selector_calls.enter(frame,groups,access,current_only);
}
// The second half, after it: groups is what the function holds there (rsi); another value is not this call's.
void selector_leave(uintptr_t frame,SelectorGroup* groups) noexcept {
    SelectorCall* call=selector_calls.leave(frame);
    if(!call)return;
    if(call->groups==groups) {
        ammo::after(call->ammo_frame,call->groups,call->access);
        // Blueprint category10 + BuildTool3 + vanilla TerrainMaterials selects group2 before Overgrowth flags.
        // The other six groups (arrows, spells, wall, roof, vanilla Overgrowth, bait) remain untouched.
        if(call->groups&&call->family&&!filter_selector_view(call->groups+2,call->family->materials))clear_selector_view(call->groups+2);
    }
    selector_calls.pop();
}
void selector_body(KilnRegisters& r) {
    selector_enter(reinterpret_cast<SelectorGroup*>(r.rcx),reinterpret_cast<void*>(r.rdx),uint8_t(r.r8&0xff),r.rsp);
}
void selector_done(KilnRegisters& r) {selector_leave(r.rsp,reinterpret_cast<SelectorGroup*>(r.rsi));}
void report_caps();
// Hot path: every frame the picker draws. Each exit is counted (relaxed atomics); nothing here allocates or logs.
void brush_query_frame(KilnRegisters& regs) {
    const auto resume=brush_query_resume.load(std::memory_order_acquire);
    if(!resume){brush_trace.record(BrushFrameExit::Disabled);return;}
    const auto family=held_family();
    if(!family){brush_trace.record(BrushFrameExit::NoFamily);return;}
    std::array<uint8_t,16> guid{};
    {std::lock_guard lock(gate);const auto i=identities.find(family->tool);
     if(i==identities.end()){brush_trace.record(BrushFrameExit::NoIdentity);return;}guid=i->second;}
    // (std::min): brush_frame.h includes <windows.h>, whose min macro the native suites (test-ammo.ps1, no NOMINMAX) keep.
    std::array<uint32_t,brush_family_capacity> items{};const size_t count=(std::min)(items.size(),family->brushes.size());
    for(size_t i=0;i<count;++i)items[i]=family->brushes[i].item;
    const auto outcome=patch_brush_frame({regs.rsp,regs.rbp,regs.rcx,regs.rdx,regs.r8,regs.r9},family->tool,guid,{items.data(),count});
    brush_trace.record(outcome);
    if(outcome.result==BrushFrameResult::WriteFault) {
        regs.rip=resume; // Skip only this query after a partial write; its fresh output remains empty.
        brush_query_resume.store(0,std::memory_order_release);
        kiln_tools_report_hook(KILN_TOOL_HOOK_BRUSH_PICKER,0);report_caps();
    }
}
PreviewOwner* KILN_CALL preview(PreviewOwner* out,const uint32_t* item,SelectorGroup* group) {
    kt::ToolPolicyScope no_impact_policy(nullptr);
    // Capture this invocation's explicit item identity before the original runs. A nested item-presentation
    // constructor cannot lend its family to this call; only the owned immutable metadata stays pinned.
    auto family=families.family(families.root(presentation_item(item)));
    auto* result=preview_hook.call<PreviewOwner*>(out,item,group);
    if(result&&family&&!filter_preview_view(result,family->materials))clear_preview_view(result);
    return result;
}
struct RootIdentity {uint32_t item,name,description;uint8_t present,reserved[3];};
static_assert(sizeof(RootIdentity)==16);
RootIdentity* KILN_CALL identity(RootIdentity* out,const uint32_t* item) {
    kt::ToolPolicyScope no_impact_policy(nullptr);
    if(out&&item) {
        const auto root=families.root(presentation_item(item));
        if(root) {
            std::array<uint8_t,16> guid{};
            {std::lock_guard lock(gate);auto i=identities.find(root);if(i!=identities.end())guid=i->second;}
            if(fnv(guid.data(),guid.size())==root) {
                const RootIdentity value{root,text_tag(guid,"kiln.item.name"),text_tag(guid,"kiln.item.description"),1,{}};
                if(copy_game_value(out,&value,sizeof value))return out;
            }
        }
    }
    return identity_hook.call<RootIdentity*>(out,item);
}
// Compare verified original bytes, not live detour bytes. Managed chains are valid on reinstall.
bool qualify(uintptr_t address,const char* pattern) {
    if(!hooks||!hooks->read_original||!hooks->code_state)return false;
    std::vector<int> bytes;
    for(const char* p=pattern;*p;) {
        while(*p==' ')++p;if(!*p)break;
        if(p[0]=='?'&&p[1]=='?'){bytes.push_back(-1);p+=2;continue;}
        auto hex=[](char c)->int{return c>='0'&&c<='9'?c-'0':c>='A'&&c<='F'?c-'A'+10:-1;};
        if(!p[1]||hex(p[0])<0||hex(p[1])<0)return false;bytes.push_back(hex(p[0])*16+hex(p[1]));p+=2;
    }
    std::vector<uint8_t> original(bytes.size());uint32_t state=KILN_CODE_UNKNOWN;uint64_t generation=0;
    if(bytes.empty()||hooks->read_original(address,original.data(),original.size())!=KILN_OK||hooks->code_state(address,original.size(),&state,&generation)!=KILN_OK||
        (state!=KILN_CODE_ORIGINAL&&state!=KILN_CODE_MANAGED))return false;
    for(size_t i=0;i<bytes.size();++i)if(bytes[i]>=0&&original[i]!=bytes[i])return false;
    return true;
}
// Client Post: a change of the picker frame's state becomes one log line and the tools.brush_families text, at most once a
// second. The line names the state now and how many changes it stands for, then every counter so far.
void publish_brush_trace() {
    if(!brush_trace.pending()||!api)return;
    const auto now=std::chrono::steady_clock::now();
    if(trace_published.time_since_epoch().count()&&now-trace_published<std::chrono::seconds(1))return;
    BrushFrameExit state{};uint64_t covered=0;
    if(!brush_trace.take(&state,&covered))return;
    trace_published=now;
    try {
        std::string line=std::string("tools: brush picker: ")+brush_exit_name(state)+" ("+brush_exit_meaning(state)+")";
        if(covered>1)line+="; "+std::to_string(covered)+" state changes since the last line";
        line+="; frames: "+brush_trace.summary();
        if(api->log)api->log(parent_owner,state==BrushFrameExit::WriteFault||state==BrushFrameExit::ReadFault?KILN_LOG_WARN:KILN_LOG_INFO,{line.data(),line.size()});
        report_caps();
    } catch(...) {}
}
void KILN_CALL trace_tick(const KilnTickInfo*,void*) {publish_brush_trace();}
void report_caps() {
    if(!api||!api->service||!parent_owner)return;
    const auto* caps=static_cast<const KilnCapsApi*>(api->service({KILN_CAPS_SERVICE,sizeof(KILN_CAPS_SERVICE)-1},KILN_CAPS_VERSION));
    if(!caps||!caps->report)return;
    KilnBuildInfo build{};build.size=sizeof build;
    const bool online=api->build_info&&api->build_info(&build)==KILN_OK;
    const bool enabled=online&&build.verified&&build.hooks_enabled;
    std::string brush_frames;
    try {if(brush_trace.any())brush_frames="; picker frames: "+brush_trace.summary();} catch(...) {}
    auto report=[&](const char* name,bool installed,const char* missing,bool client_only=true) {
        const bool wrong_side=client_only&&build.side!=KILN_SIDE_CLIENT;
        const bool ready=enabled&&!wrong_side&&installed;
        const bool ammo_fault=std::strncmp(name,"ui.ammo.",8)==0&&ammo::failed();
        const uint32_t reason=ammo_fault?KILN_CAP_REASON_FAULTED:!online?KILN_CAP_REASON_OFFLINE:wrong_side?KILN_CAP_REASON_WRONG_SIDE:!enabled?KILN_CAP_REASON_POLICY:KILN_CAP_REASON_SYMBOL_MISSING;
        const char* why=ammo_fault?"ammunition adapter faulted; native equip of its synthetic rows remains suppressed":ready?"qualified native presentation adapter installed":!online?"no game in this process":wrong_side?"tool presentation is client-only":!enabled?"hooks are disabled by the build policy":missing;
        std::string text(why);
        if(!brush_frames.empty()&&std::strcmp(name,"tools.brush_families")==0)try {text+=brush_frames;} catch(...) {}
        caps->report(parent_owner,{name,std::strlen(name)},ready?KILN_CAP_AVAILABLE:KILN_CAP_UNAVAILABLE,ready?KILN_CAP_REASON_NONE:reason,{text.data(),text.size()});
    };
    report("ui.ammo.list",ammo::available(),"exact selector/finalizer/pick mapping, stable client ECS reader or client item catalog adapter is unavailable");
    report("ui.ammo.pick",ammo::available(),"native pick suppression and backpack-source validation are not qualified or installed");
    report("tools.material_lookup",kiln_tools_hook_installed(KILN_TOOL_HOOK_MATERIAL_LOOKUP),"scoped terrain material lookup mapping is unavailable or its hook was refused",false);
    report("tools.material_picker",online&&build.side==KILN_SIDE_CLIENT&&kiln_tools_hook_installed(KILN_TOOL_HOOK_SELECTOR_BUILDER),"selector or owned held-tool observer is unavailable");
    report("tools.material_snapshot",false,"separate per-player backpack/magic-chest observations are not qualified; aggregate selector counts are not split or fabricated");
    report("tools.preview",online&&build.side==KILN_SIDE_CLIENT&&kiln_tools_hook_installed(KILN_TOOL_HOOK_PREVIEW),"owned preview constructor mapping is unavailable or its hook was refused");
    report("tools.brush_families",online&&build.side==KILN_SIDE_CLIENT&&kiln_tools_hook_installed(KILN_TOOL_HOOK_BRUSH_PICKER),"caller-scoped brush frame or owned held-tool observer is unavailable");
    report("tools.identity",online&&build.side==KILN_SIDE_CLIENT&&kiln_tools_hook_installed(KILN_TOOL_HOOK_IDENTITY),"root identity mapping is unavailable or its hook was refused");
}
bool install(kiln::RawHook& hook,const char* symbol,const char* pattern,void* function) {
    uintptr_t address=0;
    return api->symbol({symbol,std::strlen(symbol)},&address)==KILN_OK&&address&&qualify(address,pattern)&&hook.install(api,child,address,function,symbol);
}
#include "../ammo_runtime.inl"
// tools/MaterialSelector's two halves (selector_enter): the function qualified by its first bytes as the game wrote them,
// each site by its own instructions, both inside it. The site after the body goes in first, so a call that is inside the
// body while the hooks go in finds no first half at the second site and stays the game's.
bool install_selector() {
    constexpr char fn_symbol[]="tools/MaterialSelector",body_symbol[]="tools/MaterialSelectorBody",done_symbol[]="tools/MaterialSelectorDone";
    uintptr_t fn=0,body=0,done=0;
    if(api->symbol({fn_symbol,sizeof fn_symbol-1},&fn)!=KILN_OK||!fn||api->symbol({body_symbol,sizeof body_symbol-1},&body)!=KILN_OK||
       api->symbol({done_symbol,sizeof done_symbol-1},&done)!=KILN_OK||body<=fn||body-fn>=0x40||done<=body||done-fn>=0xb3c||
       !qualify(fn,"48 89 5C 24 08 44 88 44 24 18 48 89 54 24 10 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 90 01 00 00 33 ED 4C 8D B9 38 06 00 00 48 8B DA 44 8B F5")||
       !qualify(body,"4C 8D B9 38 06 00 00 48 8B DA 44 8B F5")||
       !qualify(done,"48 8B 9C 24 D0 01 00 00 48 81 C4 90 01 00 00 41 5F 41 5E 41 5D 41 5C 5F 5E 5D C3"))return false;
    if(!selector_done_hook.install_checked(api,child,done,&selector_done,done_symbol))return false;
    if(selector_body_hook.install_checked(api,child,body,&selector_body,body_symbol))return true;
    selector_done_hook.revoke();return false;
}
bool install_brush_frame() {
    uintptr_t site=0,query=0;
    constexpr char site_symbol[]="tools/BrushQueryFrame",query_symbol[]="tools/BrushQuery";
    if(api->symbol({site_symbol,sizeof site_symbol-1},&site)!=KILN_OK||site<7||
       api->symbol({query_symbol,sizeof query_symbol-1},&query)!=KILN_OK||!query||
       !qualify(site-7,"0F 29 85 00 01 00 00 E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8B 88 C8 00 00 00 48 81 C1 E0 A7 00 00 E8 ?? ?? ?? ?? 89 45 74")||
       !qualify(query,"4C 89 44 24 18 48 89 54 24 10 55 53 56 41 54 41 55 41 57 48 8D 6C 24 D1 48 81 EC C8 00 00 00"))return false;
    uint8_t call[5]{};int32_t relative=0;
    if(hooks->read_original(site,call,sizeof call)!=KILN_OK||call[0]!=0xe8)return false;
    std::memcpy(&relative,call+1,4);
    if(int64_t(site)+5+relative!=int64_t(query))return false;
    brush_query_resume.store(site+5,std::memory_order_release);
    if(brush_frame_hook.install_checked(api,child,site,&brush_query_frame,site_symbol))return true;
    brush_query_resume.store(0,std::memory_order_release);return false;
}
}

void kiln_tools_presentation_set_held_provider(KilnToolHeldItemProvider provider) {
    held_provider.store(provider,std::memory_order_release);
}
void kiln_tools_presentation_set_ammo_trace(bool on) {ammo::trace.enable(on);}
namespace {
const char* mode_name(uint32_t mode) {return mode==KILN_TOOL_MODE_REMOVE?"Remove":mode==KILN_TOOL_MODE_ADD?"Add":"none";}
// One line per published family (kiln#133): this process binds the family's programs, so its strokes reach the trace.
void log_bound(const KilnToolFamilyDef& definition) noexcept {
    if(!api||!api->log)return;
    try {
        char tail[200];
        std::snprintf(tail,sizeof tail,": actions bound: primary %s (program 0x%08x), secondary %s (program 0x%08x), %u brushes; "
            "strokes are traced on tools.actions",mode_name(definition.primary.mode),definition.primary.program,
            mode_name(definition.secondary.mode),definition.secondary.program,definition.brush_count);
        const std::string line="tools: "+std::string(definition.name.ptr,definition.name.len)+tail;
        api->log(parent_owner,KILN_LOG_INFO,{line.data(),line.size()});
    } catch(...) {}
}
}
KilnResult KILN_CALL kiln_tools_define_family(KilnOwner owner,const KilnToolFamilyDef* definition,KilnHandle* out) {
    if(out)*out=0;
    if(!definition||!out||!owner||definition->size<sizeof(KilnToolFamilyDef)||!definition->tool||definition->reserved||definition->reserved2||definition->reserved3||
        definition->material_count>64||definition->brush_count<1||definition->brush_count>64||(!definition->materials&&definition->material_count)||!definition->brushes||
        !definition->name.ptr||definition->name.len<1||definition->name.len>1024||definition->description.len>16384||(!definition->description.ptr&&definition->description.len)||
        !valid_action(definition->primary)||!valid_action(definition->secondary)||fnv(definition->identity,16)!=definition->tool)return KILN_ERR_INVALID_ARGUMENT;
    if(!api||!hooks||!required_hooks(definition->primary)||!required_hooks(definition->secondary))return KILN_ERR_UNAVAILABLE;
    {std::lock_guard lock(gate);if(!child||!retiring)return KILN_ERR_UNAVAILABLE;}
    KilnHandle lease=0;auto alive=api->subscribe_tick(owner,KILN_TICK_PRE,&noop_tick,nullptr,&lease);if(alive!=KILN_OK)return alive;api->unsubscribe(lease);
    try {
        Family family{definition->tool,std::string(definition->name.ptr,definition->name.len),std::string(definition->description.ptr?definition->description.ptr:"",definition->description.len),{}, {}};
        std::vector<KilnToolPoolEntry> pool;pool.reserve(definition->material_count);
        for(uint32_t i=0;i<definition->material_count;++i) {
            const auto& entry=definition->materials[i];if(entry.reserved)return KILN_ERR_INVALID_ARGUMENT;
            family.materials.push_back({entry.item,entry.terrain_material,entry.voxels_per_item});pool.push_back({entry.item,uint8_t(entry.terrain_material),{},entry.voxels_per_item});
        }
        for(uint32_t i=0;i<definition->brush_count;++i) {const auto& brush=definition->brushes[i];family.brushes.push_back({brush.item,brush.shape,brush.radius});}
        if(!Registry::valid(family)||((definition->primary.mode==KILN_TOOL_MODE_ADD||definition->secondary.mode==KILN_TOOL_MODE_ADD)&&(!definition->pool||pool.empty())))return KILN_ERR_INVALID_ARGUMENT;
        Registration registration{owner,definition->tool,{definition->tool}};
        for(uint32_t i=0;i<definition->brush_count;++i)registration.ids.push_back(definition->brushes[i].item);
        std::array<uint8_t,16> guid;std::memcpy(guid.data(),definition->identity,16);
        std::unique_lock lock(gate);
        // The owner lease can race teardown; validate admission again at the publication boundary.
        if(!child||!retiring||!required_hooks(definition->primary)||!required_hooks(definition->secondary))return KILN_ERR_UNAVAILABLE;
        // Retirement marks an owner dead before its callback takes this gate. A last live-owner check under
        // the same gate ensures either refusal now or cleanup of this publication by that pending callback.
        lease=0;alive=api->subscribe_tick(owner,KILN_TICK_PRE,&noop_tick,nullptr,&lease);
        if(alive!=KILN_OK)return alive;api->unsubscribe(lease);
        if(registrations.size()>=4096||!next_handle)return KILN_ERR_UNAVAILABLE;
        if(!families.add(owner,std::move(family)))return KILN_ERR_INVALID_ARGUMENT;
        const auto handle=next_handle++;
        try {
            // Allocate every publication record before opening program admission. Roll back all policies if
            // either action, metadata allocation or the final atomic program-map publication fails.
            identities.emplace(definition->tool,guid);registrations.emplace(handle,registration);
        } catch(...) {remove_locked(registration);registrations.erase(handle);throw;}
        bool ok=true;
        for(auto item:registration.ids)for(uint32_t action=0;action<2;++action) {
            const auto& source=action==0?definition->primary:definition->secondary;
            KilnToolPolicyView view{};view.size=sizeof view;view.mode=source.mode;view.policy_id=source.program;view.flags=source.flags;
            std::memcpy(view.remove_cells,source.remove_cells,32);view.pool_count=uint32_t(pool.size());view.pool=pool.data();
            if(!kt::bind_tool_policy(item,action,view))ok=false;
        }
        if(ok)ok=kiln_tools_bind_programs(owner,*definition);
        if(!ok){remove_locked(registration);registrations.erase(handle);return KILN_ERR_UNAVAILABLE;}
        *out=handle;
        lock.unlock();
        log_bound(*definition);
        return KILN_OK;
    } catch(...) {return KILN_ERR_UNAVAILABLE;}
}
KilnResult KILN_CALL kiln_tools_remove_family(KilnOwner owner,KilnHandle handle) {
    std::lock_guard lock(gate);auto i=registrations.find(handle);if(i==registrations.end())return KILN_ERR_NOT_FOUND;
    if(i->second.owner!=owner)return KILN_ERR_INVALID_ARGUMENT;
    remove_locked(i->second);registrations.erase(i);return KILN_OK;
}
KilnResult KILN_CALL kiln_tools_material_snapshot(KilnEntity,uint32_t,KilnToolMaterialSnapshot* out) {
    if(!out||out->size<sizeof(*out))return KILN_ERR_INVALID_ARGUMENT;
    *out={};out->size=sizeof(*out);
    // Selector rows currently expose only aggregate stock. No qualified per-actor/source producer exists;
    // do not fabricate separate backpack/magic-chest counts or return another player's cached selection.
    return KILN_ERR_UNAVAILABLE;
}
KilnResult KILN_CALL kiln_tools_presentation_status(KilnToolPresentationStatus* out) {
    if(!out||out->size<sizeof(*out))return KILN_ERR_INVALID_ARGUMENT;
    *out={};out->size=sizeof(*out);
    KilnBuildInfo build{};build.size=sizeof build;
    const bool online=api&&api->build_info&&api->build_info(&build)==KILN_OK;
    const bool ready=online&&build.side==KILN_SIDE_CLIENT&&build.verified&&build.hooks_enabled;
    out->picker=ready&&kiln_tools_hook_installed(KILN_TOOL_HOOK_SELECTOR_BUILDER);
    out->preview=ready&&kiln_tools_hook_installed(KILN_TOOL_HOOK_PREVIEW);out->brushes=ready&&kiln_tools_hook_installed(KILN_TOOL_HOOK_BRUSH_PICKER);
    out->identity=ready&&kiln_tools_hook_installed(KILN_TOOL_HOOK_IDENTITY);
    out->reason=!online?KILN_CAP_REASON_OFFLINE:build.side!=KILN_SIDE_CLIENT?KILN_CAP_REASON_WRONG_SIDE:
        !build.verified||!build.hooks_enabled?KILN_CAP_REASON_POLICY:out->picker&&out->preview&&out->brushes&&out->identity?KILN_CAP_REASON_NONE:KILN_CAP_REASON_SYMBOL_MISSING;
    return KILN_OK;
}
void kiln_tools_presentation_install(const KilnApi* value,KilnOwner parent) {
    api=value;parent_owner=parent;struct Report {~Report(){report_caps();}} report;
    if(!api||child)return;
    hooks=kiln::hooks_v2(api);if(!hooks||hooks->header.size<offsetof(KilnHooksApi,code_state)+sizeof(hooks->code_state))return;
    KilnBuildInfo build{};build.size=sizeof build;
    if(api->build_info(&build)!=KILN_OK||!build.verified||!build.hooks_enabled)return;
    const bool client=build.side==KILN_SIDE_CLIENT&&std::strcmp(build.exe_sha256,"5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77")==0;
    const bool server=build.side==KILN_SIDE_SERVER&&std::strcmp(build.exe_sha256,"f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2")==0;
    if(!client&&!server)return;
    child=api->owner_create({"kiln.tools.presentation",23});
    if(!child||child==parent){child=0;return;} // Never hook or revoke under the service's parent owner.
    if(hooks->subscribe_owner_retiring(child,&owner_retiring,nullptr,&retiring)!=KILN_OK){api->owner_revoke(child);child=0;return;}
    const bool lookup_ok=install(lookup_hook,"tools/TerrainConfigLookup","4C 8B 51 38 4D 85 D2 74 ?? 45 8B 5A 04 33 C9 4D 85 DB 74 ?? 4D 63 0A 45 33 C0 66 0F 1F 44 00 00 45 85 C9 75 ?? 33 C0 EB ?? 4B 8D 04 0A 49 03 C0 38 10 74 ?? 48 FF C1 49 81 C0 B8 00 00 00",reinterpret_cast<void*>(&lookup));
    kiln_tools_report_hook(KILN_TOOL_HOOK_MATERIAL_LOOKUP,lookup_ok);
    if(client) {
        kiln_tools_report_hook(KILN_TOOL_HOOK_PREVIEW,install(preview_hook,"tools/MaterialPreview","48 89 5C 24 20 4C 89 44 24 18 48 89 4C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 B0 F9 FF FF 48 81 EC 50 07 00 00",reinterpret_cast<void*>(&preview)));
        kiln_tools_report_hook(KILN_TOOL_HOOK_IDENTITY,install(identity_hook,"tools/RootIdentity","48 89 5C 24 20 56 48 83 EC 20 48 8B F1 48 85 D2 0F 84 ?? ?? ?? ?? 8B 82 D8 05 00 00",reinterpret_cast<void*>(&identity)));
        const bool held_ready=held_observer.start(api,child);
        kiln_tools_report_hook(KILN_TOOL_HOOK_SELECTOR_BUILDER,held_ready&&install_selector());
        if(kiln_tools_hook_installed(KILN_TOOL_HOOK_SELECTOR_BUILDER))ammo::start();
        brush_trace.reset();trace_published={}; // before the frame hook can count
        kiln_tools_report_hook(KILN_TOOL_HOOK_BRUSH_PICKER,held_ready&&install_brush_frame());
        if(kiln_tools_hook_installed(KILN_TOOL_HOOK_BRUSH_PICKER)) {
            const auto* client_service=static_cast<const KilnClientApi*>(api->service({KILN_CLIENT_SERVICE,sizeof(KILN_CLIENT_SERVICE)-1},KILN_CLIENT_VERSION));
            if(client_service&&client_service->subscribe_tick&&client_service->unsubscribe&&
               client_service->subscribe_tick(child,KILN_TICK_POST,&trace_tick,nullptr,&trace_tick_handle)==KILN_OK)trace_client=client_service;
            else trace_tick_handle=0;
        }
    }
    if(!client) {kiln_tools_report_hook(KILN_TOOL_HOOK_SELECTOR_BUILDER,0);kiln_tools_report_hook(KILN_TOOL_HOOK_BRUSH_PICKER,0);}
}
void kiln_tools_presentation_uninstall() {
    KilnOwner retiring_child=0;
    {std::lock_guard lock(gate);retiring_child=child;child=0;retiring=0;} // Close family admission before stopping producers.
    ammo::stop();
    held_provider.store(nullptr,std::memory_order_release);held_observer.stop();
    if(trace_tick_handle&&trace_client)trace_client->unsubscribe(trace_tick_handle);trace_tick_handle=0;trace_client=nullptr;
    for(auto id:{KILN_TOOL_HOOK_MATERIAL_LOOKUP,KILN_TOOL_HOOK_SELECTOR_BUILDER,KILN_TOOL_HOOK_PREVIEW,KILN_TOOL_HOOK_BRUSH_PICKER,KILN_TOOL_HOOK_IDENTITY})kiln_tools_report_hook(id,0);
    report_caps(); // Parent-owned capability reports must close before the child hooks are revoked.
    {std::lock_guard lock(gate);for(const auto& [_,registration]:registrations)remove_locked(registration);registrations.clear();}
    if(retiring_child&&hooks)hooks->revoke_owner(retiring_child);if(retiring_child&&api)api->owner_revoke(retiring_child);
    brush_query_resume.store(0,std::memory_order_release);
}

const KilnUiAmmoApi* kiln_tools_ammo_api() {return &ammo::table;}
