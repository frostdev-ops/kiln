// SPDX-License-Identifier: MIT
// Exercise actual native subscriber/tick API consumers, hooks and zones with fake host tables.
#include "../gameplay.cpp"
#include "../demand_hooks.h"
#include <cstdio>
#include <thread>
#include <map>
#include <filesystem>
#include <fstream>
#include <cstdlib>
#include <algorithm>
#include "services/kiln_caps.h"
namespace kg {
KilnResult component_change(KilnOwner,KilnEntity,uint32_t,const void*,uint32_t,uint32_t) { return KILN_ERR_UNAVAILABLE; }
KilnResult workshop_recipes(uint32_t,uint32_t*,uint32_t,uint32_t*) { return KILN_ERR_UNAVAILABLE; }
KilnResult recipe(uint32_t,KilnGameplayRecipe*) { return KILN_ERR_UNAVAILABLE; }
KilnResult watch(KilnEntity,bool) { return KILN_ERR_UNAVAILABLE; }
KilnResult inject(const KilnGameplayInject*) { return KILN_ERR_UNAVAILABLE; }
bool program_active() { return false; }
void discard_status(uint64_t* a,uint64_t* b) { *a=*b=0; }

bool seh_call(WalkFn,void*,unsigned long*) { return false; }
bool seh_build_target(uintptr_t) { return true; }
bool seh_subscriber(KilnGameplayFn f,KilnGameplayEvent* e,void* u,uint32_t* v,unsigned long*) { *v=f(e,u); return true; }
bool seh_get_transform(uintptr_t,KilnEntity,const int64_t**) { return false; }
bool seh_get_component(uintptr_t,uint32_t,KilnEntity,void**,uint32_t*) { return false; }
}
static unsigned ready=2, installs=0, originals=0, checks=0, failed=0, retry=0;
static std::string control_path;
static bool retry_each=false; static std::map<std::string,unsigned> target_tries;
static uint32_t interest=0; static bool partial=false;
static std::map<std::string,uint32_t> rows; static KilnCapsApi caps{};
static KilnHooksApi ht{}; static KilnApi api{};
struct Link { KilnTypedFn fn; void* user; };
static std::vector<Link> links;
#define CHECK(x) do { ++checks; if(!(x)) { ++failed; std::printf("FAIL %d %s\n",__LINE__,#x); } } while(0)
static uint32_t KILN_CALL event(KilnGameplayEvent*,void*) { return interest; }
// No-world fallback: the unchecked typed install, refusals other than unchecked threads, everything logged.
static unsigned unchecked_installs=0; static bool sigfail=false, always_unchecked=false; static std::vector<std::string> logged;
static long seen(const char* text) { return std::count_if(logged.begin(),logged.end(),[&](const std::string& s) { return s.find(text)!=std::string::npos; }); }
int main(int argc,char** argv) {
    const std::string mode=argc>1 ? argv[1] : "craft";
    ht.header={sizeof ht,2};
    ht.typed_install_ready=[]()->uint32_t { return ready; };
    ht.raw_detour=[](KilnOwner,uintptr_t,const KilnHookOrder*,void*,void**,KilnHandle*)->KilnResult { ++installs; return KILN_ERR_INTERNAL; };
    ht.detour_typed_checked=[](KilnOwner,uintptr_t,const KilnHookOrder* order,const KilnSig* sig,KilnTypedFn f,void* u,KilnHandle* out)->KilnResult {
        ++installs; CHECK(order->priority==0 && kg::sv(order->before)=="kiln.set,kiln.shroudforge,kiln.npcai" && kg::sv(order->after)=="kiln.creatures,kiln.build,kiln.rules");
        CHECK(sig->argc>=2 && sig->argc<=6);
        if(sigfail) return KILN_HOOKS_ERR_SIG_MISMATCH;
        if(always_unchecked) return KILN_HOOKS_ERR_UNCHECKED;
        if(retry_each && ++target_tries[std::string(kg::sv(order->label))]<3) return KILN_HOOKS_ERR_UNCHECKED;
        if(retry && (!partial || installs>1)) { --retry; return KILN_HOOKS_ERR_UNCHECKED; }
        *out=links.size()+1; links.push_back({f,u}); return KILN_OK;
    };
    ht.detour_typed=[](KilnOwner,uintptr_t,const KilnHookOrder*,const KilnSig*,KilnTypedFn f,void* u,KilnHandle* out)->KilnResult {
        ++unchecked_installs; *out=links.size()+1; links.push_back({f,u}); return KILN_OK;
    };
    ht.call_original_frame=[](KilnCallFrame* f)->KilnResult { ++originals; f->rax=7; return KILN_OK; };
    ht.caller=[](KilnHandle)->uintptr_t { return 0; };
    caps.report=[](KilnOwner,KilnStr name,uint32_t state,uint32_t,KilnStr)->KilnResult { rows[std::string(kg::sv(name))]=state; return KILN_OK; };
    api.size=sizeof api; api.service=[](KilnStr name,uint32_t)->const void* { return kg::sv(name)=="kiln.hooks" ? static_cast<const void*>(&ht) : kg::sv(name)=="kiln.caps" ? &caps : nullptr; };
    api.session=[](KilnSessionInfo* s)->KilnResult { s->worlds[0]=ready==2 ? 0 : 1; return KILN_OK; };
    api.server_world=[]()->uintptr_t { return 0; }; api.on_tick_thread=[]()->bool { return true; };
    api.log=[](KilnOwner,int,KilnStr t){ logged.emplace_back(t.ptr,t.len); }; api.feature=[](KilnStr)->bool { return true; };
    api.symbol=[](KilnStr,uintptr_t* p)->KilnResult { *p=100; return KILN_OK; };
    api.constant=[](KilnStr name,int64_t* p)->KilnResult {
        auto s=kg::sv(name); *p=s=="spawn/storage.entity_map_cap" ? 0x110 : s=="spawn/storage.entity_records" ? 0x140 :
             s=="spawn/storage.entity_map" ? 0x100 : 0x10; return KILN_OK;
    };
    kg::g_api=&api; kg::g_owner=1; kg::L.world_storage=8;
    kg::F.craft=kg::F.terraform=kg::F.spawn=true; kg::fn.craft=kg::fn.terraform=kg::fn.queue_spawn=100;
    if(mode.starts_with("r4-")) {
        kg::F.build=true;
        kg::fn.place_block=kg::fn.place_prop=kg::fn.dismantle=kg::fn.dismantle2=kg::fn.pick_up=100;
    }
    if(mode.find("inject")!=std::string::npos || mode=="r4-build-unmarked") {
        control_path=(std::filesystem::path(std::getenv("KILN_OUT"))/"gameplay-inject-fixture").string();
        std::filesystem::create_directories(control_path);
        std::filesystem::remove(std::filesystem::path(control_path)/"gameplay.lab");
        if(mode!="r4-build-unmarked") std::ofstream(std::filesystem::path(control_path)/"gameplay.lab").close();
        std::ofstream(std::filesystem::path(control_path)/"gameplay.unchecked") << "build " << (mode=="r4-build-inject-0" ? 0 : 2);
        api.path=[](int32_t,char* b,size_t cap,size_t* n)->KilnResult { *n=control_path.size(); if(cap<*n) return KILN_ERR_BUFFER_TOO_SMALL;
            std::memcpy(b,control_path.data(),*n); return KILN_OK; };
    }
    kg::hooks_install(); kg::entities_install(); CHECK(installs==0);
    CHECK(rows["gameplay.craft_hooks"]==KILN_CAP_PENDING); CHECK(rows["gameplay.terraform_hooks"]==KILN_CAP_PENDING);
    CHECK(rows["gameplay.spawn_hooks"]==KILN_CAP_PENDING); CHECK(rows["gameplay.entities_hooks"]==KILN_CAP_PENDING);
    CHECK(rows["gameplay.spawn_replace"]==KILN_CAP_AVAILABLE); CHECK(rows["gameplay.entity_events"]==KILN_CAP_AVAILABLE);
    if(mode.starts_with("r4-")) {
        if(mode=="r4-legacy-retry" || mode=="r4-legacy-retry-noworld") {
            retry_each=true; ready=mode=="r4-legacy-retry" ? 1 : 2;
            if(ready==1) { KilnWorldEvent e{}; e.event=KILN_WORLD_STARTED; e.kind=KILN_WORLD_SERVER; kg::on_build_world(&e,nullptr); }
            else { KilnGameplayStatus s{}; s.size=sizeof s; CHECK(kg::t_status(&s)==KILN_OK); }
            if(ready==2) {  // no world: a refusal for unchecked threads goes in unchecked at once, as dev.28 installed at start
                CHECK(installs==10); CHECK(unchecked_installs==10); CHECK(kg::build_installed());
                CHECK(seen("installed unchecked: no world running")==10); CHECK(seen("no demand-aware host: all gameplay hooks installed as before")==1);
            } else {
            CHECK(installs==5); // one attempt per group in the batch, no immediate retry after a refusal
            kg::hooks_poll(); CHECK(installs==5); // ordinary on_pre later in that SAME first Pre
            ready=1; for(int n=0;n<30;++n) kg::hooks_poll(); CHECK(installs==30); CHECK(kg::build_installed()); CHECK(unchecked_installs==0);
            }
        } else if(mode=="r4-legacy-status" || mode=="r4-aware-status") {
            const bool aware=mode=="r4-aware-status";
            if(aware) CHECK(kg::t_request_hooks(99,0)==KILN_OK);
            ready=2; KilnGameplayStatus s{}; s.size=sizeof s; CHECK(kg::t_status(&s)==KILN_OK);
            CHECK(installs==(aware ? 0u : 10u)); CHECK(static_cast<bool>(s.build_hooks)==!aware);
        } else if(mode.find("inject")!=std::string::npos) {
            ready=0; CHECK(kg::t_request_build(77)==KILN_OK); ready=1;
            for(int n=0;n<4;++n) kg::hooks_poll();
            const auto count=mode=="r4-build-inject-0" ? 0u : 2u;
            CHECK(installs==count); CHECK(links.size()==count); CHECK(!kg::build_installed()); CHECK(kg::build_state()==2);
            CHECK(kg::build_mask()==((1u<<count)-1)); CHECK(kg::build_reason().find("three attempts")!=std::string::npos);
            CHECK(kg::build_reason().find("next no-world moment")!=std::string::npos);
            CHECK(seen("kiln.gameplay: LAB build target")==3); CHECK(seen("build hooks wait for the next no-world moment")==1);
            if(count) { KilnCallFrame f{}; CHECK(links[0].fn(&f,links[0].user)==KILN_TYPED_CONTINUE); CHECK(originals==0); }
            kg::hooks_poll(); CHECK(installs==count); CHECK(unchecked_installs==0);  // no fourth attempt while the world runs
            ready=2; kg::hooks_poll_no_world();  // the next no-world moment: the forced target goes in unchecked, the rest checked
            CHECK(seen("kiln.gameplay: LAB build target")==4); CHECK(unchecked_installs==1); CHECK(installs==4);
            CHECK(kg::build_installed()); CHECK(kg::build_state()==1); CHECK(kg::build_mask()==31);
            CHECK(seen("installed unchecked: no world running (the checked install refused by gameplay.unchecked)")==1);
            CHECK(seen("build hooks installed with no world running, after waiting since the refusal during play")==1);
            std::filesystem::remove_all(control_path);
        } else if(mode=="r4-build-inject-noworld") {
            ready=2; CHECK(kg::t_request_build(77)==KILN_OK);  // forced refusal before any world: the fallback, not a wait
            CHECK(seen("kiln.gameplay: LAB build target 2 unchecked attempt 1 (nothing written)")==1);
            CHECK(unchecked_installs==1); CHECK(installs==4); CHECK(kg::build_installed()); CHECK(kg::build_mask()==31);
            CHECK(seen("build hooks: gameplay/DismantleBlock installed unchecked: no world running")==1);
            std::filesystem::remove_all(control_path);
        } else if(mode=="r4-fallback-noworld") {
            // No world runs: a checked install refused for threads it cannot check goes in unchecked, as dev.28 did at start.
            always_unchecked=true; ready=2;
            CHECK(kg::t_request_build(77)==KILN_OK);
            CHECK(installs==5); CHECK(unchecked_installs==5); CHECK(kg::build_installed()); CHECK(kg::build_state()==1);
            CHECK(rows["gameplay.build_hooks"]==KILN_CAP_AVAILABLE);
            CHECK(seen("kiln.gameplay: build hooks: gameplay/PlaceBlueprint installed unchecked: no world running")==1);
            CHECK(seen("installed unchecked: no world running (the checked install found threads it could not check")==5);
        } else if(mode=="r4-fallback-play") {
            // While a world runs: three checked attempts, then the group waits for the next no-world moment (a client's
            // menu frame through the UI tick) and goes in there, unchecked where it must.
            static KilnUiTickApi ui{}; static KilnUiFrameTickFn menu=nullptr; static unsigned subs=0, unsubs=0; static bool in_menu=false;
            ui.header={sizeof ui,1}; ui.subscribe=[](KilnOwner,KilnUiFrameTickFn fn,void*,KilnHandle* h)->KilnResult { menu=fn; *h=++subs; return KILN_OK; };
            ui.unsubscribe=[](KilnHandle)->KilnResult { ++unsubs; return KILN_OK; }; ui.in_menu=[]()->bool { return in_menu; };
            kg::g_compat_ui=&ui;
            always_unchecked=true; ready=0; CHECK(kg::t_request_build(77)==KILN_OK); ready=1;
            for(int n=0;n<3;++n) kg::hooks_poll();
            CHECK(installs==3); CHECK(unchecked_installs==0); CHECK(!kg::build_installed()); CHECK(kg::build_state()==2);
            CHECK(kg::build_reason().find("waiting for the next no-world moment (the next menu)")!=std::string::npos);
            CHECK(rows["gameplay.build_hooks"]==KILN_CAP_PENDING);
            CHECK(seen("kiln.gameplay: build hooks wait for the next no-world moment (the next menu)")==1); CHECK(subs==1);
            for(int n=0;n<5;++n) kg::hooks_poll(); CHECK(installs==3);  // no attempt while the world runs
            CHECK(kg::t_request_build(88)==KILN_OK); CHECK(installs==3);  // more demand while the world runs: still waiting
            KilnGameplayZone zone{}; zone.size=sizeof zone; zone.shape=KILN_GAMEPLAY_ZONE_SPHERE; zone.radius=10; uint64_t none=0;
            zone.allowed_steam_ids=&none; zone.deny=KILN_GAMEPLAY_DENY_BUILD; uint32_t id=0;
            CHECK(kg::zone_add(77,&zone,&id)==KILN_ERR_UNAVAILABLE); CHECK(id==0);  // protection refused while it waits
            menu(1,nullptr); CHECK(installs==3);  // a UI frame while a world steps installs nothing
            ready=2; in_menu=true; menu(2,nullptr);  // the menu: no world in the process
            CHECK(installs==8); CHECK(unchecked_installs==5); CHECK(kg::build_installed()); CHECK(kg::build_state()==1);
            CHECK(unsubs==1); CHECK(rows["gameplay.build_hooks"]==KILN_CAP_AVAILABLE);
            CHECK(seen("build hooks installed with no world running, after waiting since the refusal during play")==1);
            CHECK(kg::zone_add(77,&zone,&id)==KILN_OK);
        } else if(mode=="r4-fallback-server") {
            // A dedicated server has no menu: the wait lasts until the next server start, and says so.
            always_unchecked=true; ready=0; CHECK(kg::t_request_hooks(77,KILN_GAMEPLAY_HOOK_CRAFT)==KILN_OK); ready=1;
            for(int n=0;n<6;++n) kg::hooks_poll();
            KilnGameplayStatus st{}; kg::hooks_status(&st);
            CHECK(st.hook_groups[0].state==2); CHECK(std::string(st.hook_groups[0].reason).find("(the next server start)")!=std::string::npos);
            CHECK(installs==3); CHECK(unchecked_installs==0);
            CHECK(kg::t_request_hooks(99,KILN_GAMEPLAY_HOOK_CRAFT)==KILN_OK);  // pending, not refused
        } else if(mode=="r4-fallback-other") {
            // A refusal for another reason (the code does not fit, the bytes changed) never falls back, with or without a world.
            sigfail=true; ready=2;
            CHECK(kg::t_request_build(77)==KILN_ERR_UNAVAILABLE);
            CHECK(installs==1); CHECK(unchecked_installs==0); CHECK(kg::build_state()==3); CHECK(!kg::build_installed());
            ready=0; CHECK(kg::t_request_hooks(77,KILN_GAMEPLAY_HOOK_CRAFT)==KILN_OK); ready=1;
            for(int n=0;n<4;++n) kg::hooks_poll();
            KilnGameplayStatus st{}; kg::hooks_status(&st); CHECK(st.hook_groups[0].state==3);
            CHECK(installs==2); CHECK(unchecked_installs==0);
            ready=2; kg::hooks_poll_no_world(); CHECK(installs==2); CHECK(unchecked_installs==0);
            CHECK(seen("installed unchecked")==0);
        } else if(mode=="r4-all-retry") {
            retry_each=true; ready=0; CHECK(kg::t_request_hooks(77,31)==KILN_OK); ready=1;
            for(int n=1;n<=18;++n) { kg::hooks_poll(); CHECK(kg::build_installed()==(n==18)); }
            CHECK(installs==30); CHECK(kg::build_installed());
        } else if(mode=="r4-all-pace") {
            ready=0; CHECK(kg::t_request_hooks(77,31)==KILN_OK); CHECK(installs==0); ready=1;
            for(unsigned n=1;n<=5;++n) { kg::hooks_poll(); CHECK(installs==n*2); CHECK(kg::build_installed()==(n==5)); }
        } else if(mode=="r4-late-timing") {
            // A write while a world runs stops the game's other threads: each one is logged at info with the time the
            // installer call took and its place in the Pre. Nobody plays during a no-world install: no line then.
            static std::vector<std::string> lines;
            api.log=[](KilnOwner,int level,KilnStr t) { if(level==KILN_LOG_INFO) lines.emplace_back(t.ptr,t.len); };
            auto seen=[&](const char* text) { return std::count_if(lines.begin(),lines.end(),[&](const std::string& s) { return s.find(text)!=std::string::npos; }); };
            ready=2; CHECK(kg::t_request_hooks(77,KILN_GAMEPLAY_HOOK_CRAFT)==KILN_OK); CHECK(installs==1);
            CHECK(seen("during play")==0);
            ready=0; CHECK(kg::t_request_hooks(77,KILN_GAMEPLAY_HOOK_BUILD)==KILN_OK); CHECK(installs==1); ready=1;
            kg::hooks_poll();
            CHECK(seen("kiln.gameplay: gameplay/PlaceBlueprint went in during play: the installer call took ")==1);
            CHECK(seen(" ms on the world's thread (write 1 in this Pre)")==1);
            for(int n=0;n<6;++n) kg::hooks_poll();
            CHECK(kg::build_installed()); CHECK(seen("went in during play")==5);
            CHECK(seen("kiln.gameplay: gameplay/PickUpProp went in during play")==1);
        } else if(mode=="r4-build-subscribe") {
            ready=0; KilnHandle h=0; CHECK(kg::t_subscribe(77,1u<<KILN_GAMEPLAY_BUILD,&event,nullptr,&h)==KILN_OK);
            CHECK(installs==0); ready=1; for(int n=0;n<3;++n) kg::hooks_poll(); CHECK(installs==6); CHECK(kg::build_installed());
            CHECK(kg::t_unsubscribe(h)==KILN_OK); CHECK(kg::build_installed());
        } else {
        const bool compatibility=mode=="r4-old-host" || mode=="r4-native-only" || mode=="r4-client-menu";
        if(compatibility || mode=="r4-aware-host" || mode=="r4-client-menu") {
            if(!compatibility) CHECK(kg::t_request_hooks(99,0)==KILN_OK);
            ready=mode=="r4-client-menu" ? 2 : 1; // core's first authoritative world notification is inside TypedPreScope, before Host OnLoad
            KilnWorldEvent ev{}; ev.event=KILN_WORLD_STARTED; ev.kind=KILN_WORLD_SERVER;
            if(mode=="r4-client-menu") {
                KilnUiTickApi ui{}; ui.unsubscribe=[](KilnHandle)->KilnResult { return KILN_OK; };
                kg::g_compat_ui=&ui; kg::g_compat_menu=123; kg::on_compat_menu(1,nullptr); CHECK(kg::g_compat_menu==0);
            } else kg::on_build_world(&ev,nullptr);
            CHECK(installs==(compatibility ? 10u : 0u));
            ready=1;
            for(int i=0;i<5;++i) kg::hooks_poll();
            CHECK(installs==(compatibility ? 10u : 0u));
            if(compatibility) CHECK(kg::build_installed());
            else { CHECK(kg::t_request_hooks(77,1)==KILN_OK); CHECK(!kg::build_installed());
                for(int i=0;i<3;++i) kg::hooks_poll(); CHECK(kg::build_installed()); CHECK(installs==5); }
        } else {
            const bool immediate=mode=="r4-build-immediate";
            ready=immediate ? 2 : 0;
            CHECK(kg::t_request_build(77)==KILN_OK);
            CHECK(installs==(immediate ? 5u : 0u));
            if(!immediate) { CHECK(kg::build_state()==2); CHECK(kg::build_reason().find("safe Pre")!=std::string::npos);
                ready=1; kg::hooks_poll(); CHECK(installs==2); CHECK(!kg::build_installed());
                kg::hooks_poll(); CHECK(installs==4); CHECK(!kg::build_installed()); kg::hooks_poll(); }
            CHECK(kg::build_installed()); CHECK(installs==5); CHECK(kg::build_mask()==31);
        }
        }
        if(mode=="r4-build-unmarked") std::filesystem::remove_all(control_path);
        kg::zones_retire(); // the snapshots a Post would free (LeakSanitizer at exit)
        std::printf("remaining %s: %u checks, %u failures\n",mode.c_str(),checks,failed); return failed ? 1 : 0;
    }
    if(mode=="idle") {
        uint8_t from[16]={1},to[16]={2}; CHECK(kg::hooks_spawn_replace(77,from,to,0)==KILN_OK);
        CHECK(kg::hooks_spawn_replace_clear(77)==0); CHECK(kg::entity_filter(0)==KILN_OK); kg::hooks_poll(); CHECK(installs==0);
        std::printf("remaining idle: %u checks, %u failures\n",checks,failed); return failed ? 1 : 0;
    }
    unsigned group=mode.find("terraform")!=std::string::npos ? 4 : mode.find("spawn")!=std::string::npos ? 8 : mode.find("entities")!=std::string::npos ? 16 : 2;
    // "refuse": the thread check refuses three times while the world runs; "sigrefuse": refused for its code (final)
    const bool sig=mode.find("sigrefuse")!=std::string::npos;
    const bool late=mode.find("late")!=std::string::npos, denied=!sig && mode.find("refuse")!=std::string::npos;
    if(late || denied || sig) ready=0;
    if(denied) retry=3;
    sigfail=sig;
    partial=mode.find("partial")!=std::string::npos;
    if(mode.find("threads")!=std::string::npos) {
        std::thread a([&] { kg::t_request_hooks(77,group); }), b([&] { kg::t_request_hooks(88,group); }); a.join(); b.join();
    } else if(mode.find("update")!=std::string::npos) {
        KilnGameplayZone zone{}; zone.size=sizeof zone; zone.shape=KILN_GAMEPLAY_ZONE_SPHERE; zone.radius=10;
        uint64_t empty=0; zone.allowed_steam_ids=&empty; zone.deny=KILN_GAMEPLAY_DENY_INTERACT; uint32_t id=0;
        CHECK(kg::zone_add(77,&zone,&id)==KILN_OK);
        zone.deny=group==4 ? KILN_GAMEPLAY_DENY_TERRAFORM : KILN_GAMEPLAY_DENY_SPAWN;
        CHECK(kg::zone_update(id,&zone)==(late ? KILN_ERR_UNAVAILABLE : KILN_OK));
        CHECK(kg::zones_any(zone.deny)==!late);
    } else if(mode.find("zone")!=std::string::npos) {
        KilnGameplayZone zone{}; zone.size=sizeof zone; zone.shape=KILN_GAMEPLAY_ZONE_SPHERE; zone.radius=10;
        zone.deny=group==4 ? KILN_GAMEPLAY_DENY_TERRAFORM : KILN_GAMEPLAY_DENY_SPAWN;
        uint64_t empty=0; zone.allowed_steam_ids=&empty; uint32_t id=0;
        CHECK(kg::zone_add(77,&zone,&id)==(late ? KILN_ERR_UNAVAILABLE : KILN_OK));
        CHECK(kg::zone_count()==(late ? 0 : 1));
    } else if(mode.find("replace")!=std::string::npos) {
        uint8_t guid[16]={1},to[16]={2}; CHECK(kg::hooks_spawn_replace(77,guid,to,1)==KILN_OK);
    } else if(mode.find("filter")!=std::string::npos) CHECK(kg::entity_filter(KILN_GAMEPLAY_ENTITY_ALL)==KILN_OK);
    else if(mode.find("tick")!=std::string::npos) {
        KilnHandle h=0; CHECK(kg::t_subscribe(77,1,&event,nullptr,&h)==KILN_OK);
        interest=1u<<(group==2 ? KILN_GAMEPLAY_CRAFTED : group==4 ? KILN_GAMEPLAY_BUILD : group==8 ? KILN_GAMEPLAY_SPAWN : KILN_GAMEPLAY_ENTITY_JOINED);
        kg::tick_subscribers();
    } else {
        KilnHandle h=0;
        const auto kind=group==2 ? KILN_GAMEPLAY_CRAFTED : group==8 ? KILN_GAMEPLAY_SPAWN : KILN_GAMEPLAY_ENTITY_LEFT;
        if(group==4 && mode.find("subscribe")!=std::string::npos)
            CHECK(kg::t_subscribe(77,1u<<KILN_GAMEPLAY_BUILD,&event,nullptr,&h)==KILN_ERR_UNAVAILABLE); // missing five-piece symbols; terraform still requested
        else if(group==4) CHECK(kg::t_request_hooks(77,group)==KILN_OK); // Build itself retains its separate stop policy.
        else CHECK(kg::t_subscribe(77,1u<<kind,&event,nullptr,&h)==KILN_OK);
        if(h) CHECK(kg::t_unsubscribe(h)==KILN_OK);
    }
    const int gi=group==2?0:group==4?1:group==8?2:3;
    const char* names[]={"craft","terraform","spawn","entities"};
    const std::string row=std::string("gameplay.")+names[gi]+"_hooks";
    if(late || denied || sig) {
        CHECK(installs==0); KilnGameplayStatus s{}; kg::hooks_status(&s); CHECK(s.hook_groups[gi].state==2);
        ready=1; kg::hooks_poll(); if(denied) { kg::hooks_poll(); kg::hooks_poll(); }
    }
    const auto count=group==16 ? 2u : 1u;
    if(sig) {
        // Refused for its code, not for threads: final, and never written unchecked, also when no world runs.
        CHECK(installs==1); CHECK(unchecked_installs==0); CHECK(!kg::hooks_installed(group));
        KilnGameplayStatus st{}; kg::hooks_status(&st); CHECK(st.hook_groups[gi].state==3u); CHECK(rows[row]==KILN_CAP_UNAVAILABLE);
        if(group==8) { CHECK(rows["gameplay.spawn_replace"]==KILN_CAP_UNAVAILABLE);
            uint8_t g2[16]={3},t2[16]={4}; CHECK(kg::hooks_spawn_replace(77,g2,t2,1)==KILN_ERR_UNAVAILABLE); }
        if(group==16) CHECK(rows["gameplay.entity_events"]==KILN_CAP_UNAVAILABLE);
        CHECK(!kg::hooks_request(group,99)); ready=2; kg::hooks_poll_no_world(); kg::hooks_poll();
        CHECK(installs==1); CHECK(unchecked_installs==0); CHECK(seen("installed unchecked")==0);
        if(mode.find("replace")!=std::string::npos) CHECK(kg::hooks_spawn_replace_clear(77)==1u);  // the inert rule is still removable
        kg::zones_retire(); // the snapshots a Post would free (LeakSanitizer at exit)
        std::printf("remaining %s: %u checks, %u failures\n",mode.c_str(),checks,failed); return failed ? 1 : 0;
    }
    if(denied) {
        // Three thread-check refusals while the world runs: no unchecked write during play; the group waits for the next
        // no-world moment (this harness has no UI tick: a server's next start) instead of staying refused.
        CHECK(installs==(partial ? 4u : 3u)); CHECK(unchecked_installs==0); CHECK(!kg::hooks_installed(group));
        KilnGameplayStatus st{}; kg::hooks_status(&st); CHECK(st.hook_groups[gi].state==2u);
        CHECK(std::string(st.hook_groups[gi].reason).find("waiting for the next no-world moment (the next server start)")!=std::string::npos);
        CHECK(rows[row]==KILN_CAP_PENDING);
        CHECK(seen((std::string("kiln.gameplay: ")+names[gi]+" hooks wait for the next no-world moment").c_str())==1);
        if(group==8) CHECK(rows["gameplay.spawn_replace"]==KILN_CAP_AVAILABLE);  // a pending replacement stays registered
        if(group==16) CHECK(rows["gameplay.entity_events"]==KILN_CAP_AVAILABLE);
        CHECK(kg::hooks_request(group,99)); for(int n=0;n<4;++n) kg::hooks_poll(); CHECK(installs==(partial ? 4u : 3u));
        if(partial) { CHECK(links.size()==1); KilnCallFrame f{}; CHECK(links[0].fn(&f,links[0].user)==KILN_TYPED_CONTINUE); CHECK(originals==0); }
        ready=2; kg::hooks_poll_no_world();  // the next no-world moment: the checked install succeeds now
        CHECK(unchecked_installs==0);
        CHECK(seen((std::string("kiln.gameplay: ")+names[gi]+" hooks installed with no world running, after waiting").c_str())==1);
    }
    const unsigned expected=denied ? (group==16 ? 5u : 4u) : count;
    CHECK(installs==expected); CHECK(kg::hooks_installed(group));
    KilnGameplayStatus status{}; kg::hooks_status(&status); const auto& s=status.hook_groups[gi];
    CHECK(s.state==1u); CHECK(s.reason[0]);
    CHECK(rows[row]==KILN_CAP_AVAILABLE);
    if(mode.find("replace")!=std::string::npos) CHECK(kg::hooks_spawn_replace_clear(77)==1u);
    if(mode.find("filter")!=std::string::npos) CHECK(kg::entity_filter(0)==KILN_OK);
    CHECK(kg::hooks_request(group,99)); kg::hooks_poll(); CHECK(installs==expected);
    {
        KilnCallFrame frame{}; frame.slot=1; uint64_t stack[2]{}; frame.stack=stack;
        CHECK(links[0].fn(&frame,links[0].user)==KILN_TYPED_SKIP); CHECK(originals==1); CHECK(frame.rax==7);
    }
    kg::zones_retire(); // the snapshots a Post would free (LeakSanitizer at exit)
    std::printf("remaining %s: %u checks, %u failures\n",mode.c_str(),checks,failed); return failed ? 1 : 0;
}
