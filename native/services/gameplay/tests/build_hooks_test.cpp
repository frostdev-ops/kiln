// SPDX-License-Identifier: MIT
// Real building observers and zone table against the checked typed installer; never execute Windows/game code.
#include "../hooks.cpp"
#include "../zones.cpp"
#include <cstdio>
#include <thread>
namespace kg {
const KilnApi* g_api; KilnOwner g_owner=1; Layout L; Fns fn; Features F; EventIds H; Stats S; uint64_t g_step;
void logf(int,const char*,...) {}
bool hooks_watch_no_world(bool) { return false; }  // gameplay.cpp's UI-tick watch: none here (a server)
bool has_subscribers(uint32_t) { return true; }
bool entity_position(KilnEntity,KilnVec3*) { return false; }
bool seh_call(void(*f)(void*),void*,unsigned long*) { return false; }
bool dispatch(KilnGameplayEvent*) { return false; }
}
static int installs, calls, fail_at, failures, checks; static uint32_t ready=2;
struct Link { KilnTypedFn fn; void* user; };
static std::vector<Link> links;
static uint8_t original(uint8_t*,void*,uint32_t) { ++calls; return 7; }
#define CHECK(x) do { ++checks; if(!(x)) { ++failures; std::printf("FAIL %d: %s\n",__LINE__,#x); } } while(0)
static bool nested=false, retry_failure=false; static KilnCallFrame* outer=nullptr;
int main(int argc,char** argv) {
    const std::string mode=argc>1 ? argv[1] : "normal";
    KilnHooksApi hooks{}; hooks.header={sizeof hooks,2};
    hooks.raw_detour=[](KilnOwner,uintptr_t,const KilnHookOrder*,void*,void**,KilnHandle*)->KilnResult { CHECK(false); return KILN_ERR_INTERNAL; };
    hooks.typed_install_ready=[]()->uint32_t { return ready; };
    hooks.detour_typed_checked=[](KilnOwner,uintptr_t,const KilnHookOrder* order,const KilnSig* sig,KilnTypedFn fn,void* user,KilnHandle* out)->KilnResult {
        ++installs;
        CHECK(kg::sv(order->before)=="kiln.set,kiln.shroudforge,kiln.npcai");
        CHECK(kg::sv(order->after)=="kiln.creatures,kiln.build,kiln.rules");
        CHECK(sig->argc==3 && sig->ret_kind==KILN_SIG_U8);
        CHECK(sig->args[0].kind==KILN_SIG_PTR && sig->args[1].kind==KILN_SIG_PTR && sig->args[2].kind==KILN_SIG_U32);
        if(retry_failure && installs>=fail_at) return KILN_HOOKS_ERR_UNCHECKED;
        if(installs==fail_at) return KILN_ERR_UNAVAILABLE;
        *out=links.size()+1; links.push_back({fn,user}); return KILN_OK;
    };
    hooks.call_original_frame=[](KilnCallFrame* f)->KilnResult {
        CHECK(f==kg::FrameLink::current); CHECK(f->gpr[2]==123); CHECK(f->gpr[3]==456); ++calls;
        if(nested) { nested=false; outer=f;
            KilnCallFrame inner=*f; CHECK(links[0].fn(&inner,links[0].user)==KILN_TYPED_SKIP);
            CHECK(kg::FrameLink::current==outer); }
        f->rax=7; return KILN_OK;
    };
    KilnApi api{}; api.size=sizeof api;
    static const KilnHooksApi* table; table=&hooks;
    api.service=[](KilnStr s,uint32_t)->const void* { return std::string_view(s.ptr,s.len)=="kiln.hooks" ? table : nullptr; };
    kg::g_api=&api; kg::F.build=true; kg::L.op_player=0;
    kg::fn.place_block=kg::fn.place_prop=kg::fn.dismantle=kg::fn.dismantle2=kg::fn.pick_up=reinterpret_cast<uintptr_t>(&original);
    if(mode=="precheck") kg::fn.pick_up=0;
    kg::hooks_install(); CHECK(installs==0);
    CHECK(reinterpret_cast<uint8_t(*)(uint8_t*,void*,uint32_t)>(kg::fn.place_prop)(nullptr,nullptr,1)==7);
    calls=0;
    if(mode=="failure" || mode=="retry") fail_at=3;
    retry_failure=mode=="retry";
    ready=mode=="immediate" ? 2 : 0;
    std::thread a([] { kg::build_request(11); }), b([] { kg::build_request(22); }); a.join(); b.join();
    KilnGameplayZone zone{}; zone.size=sizeof zone; zone.shape=KILN_GAMEPLAY_ZONE_SPHERE; zone.radius=10;
    uint64_t allowed=0; zone.allowed_steam_ids=&allowed; uint32_t id=0;
    if(mode=="zones") {
        zone.deny=KILN_GAMEPLAY_DENY_BUILD;
        CHECK(kg::zone_add(11,&zone,&id)==KILN_ERR_UNAVAILABLE); CHECK(id==0); CHECK(kg::zone_count()==0);
        zone.deny=KILN_GAMEPLAY_DENY_INTERACT; CHECK(kg::zone_add(11,&zone,&id)==KILN_OK);
        zone.deny=KILN_GAMEPLAY_DENY_BREAK;
        CHECK(kg::zone_update(id,&zone)==KILN_ERR_UNAVAILABLE); CHECK(!kg::zones_any(KILN_GAMEPLAY_DENY_BREAK));
    }
    if(ready==0) { CHECK(installs==0); CHECK(!kg::build_installed()); kg::hooks_poll(); CHECK(installs==0); }
    if(mode=="hosted") { kg::hooks_poll(); CHECK(installs==0); } // joined/hosting eligibility is the shared installer policy
    ready=1;
    for(int i=0;i<(retry_failure ? 4 : 3);++i) kg::hooks_poll();
    if(mode=="precheck") { CHECK(installs==0); CHECK(!kg::build_installed()); CHECK(kg::build_state()==3); }
    else if(fail_at) {
        // a thread-check refusal during play waits for the next no-world moment; any other refusal is final
        CHECK(installs==(retry_failure ? 5 : 3)); CHECK(!kg::build_installed()); CHECK(kg::build_state()==(retry_failure ? 2u : 3u)); CHECK(kg::build_mask()==3);
        KilnCallFrame invalid{}; CHECK(links[0].fn(&invalid,links[0].user)==KILN_TYPED_CONTINUE);
        CHECK(calls==0); CHECK(kg::g_recs.empty());
        if(retry_failure) CHECK(kg::build_reason().find("three attempts")!=std::string::npos);
    } else {
        CHECK(installs==5); CHECK(kg::build_installed()); CHECK(kg::build_state()==1); CHECK(kg::build_mask()==31);
        uint8_t op[8]{}; op[0]=7; alignas(float) uint8_t arg[64]{};
        for(auto link:links) {
            KilnCallFrame f{}; f.gpr[0]=reinterpret_cast<uintptr_t>(op); f.gpr[1]=reinterpret_cast<uintptr_t>(arg); f.gpr[2]=123; f.gpr[3]=456;
            CHECK(link.fn(&f,link.user)==KILN_TYPED_SKIP); CHECK(f.rax==7); CHECK(kg::FrameLink::current==nullptr);
        }
        CHECK(calls==5); CHECK(kg::g_recs.size()==5);
        nested=true; KilnCallFrame f{}; f.gpr[0]=reinterpret_cast<uintptr_t>(op); f.gpr[1]=reinterpret_cast<uintptr_t>(arg); f.gpr[2]=123; f.gpr[3]=456;
        CHECK(links[1].fn(&f,links[1].user)==KILN_TYPED_SKIP); CHECK(calls==7); CHECK(kg::FrameLink::current==nullptr);
        kg::g_recs.clear(); calls=0;
        zone.deny=KILN_GAMEPLAY_DENY_BUILD|KILN_GAMEPLAY_DENY_BREAK;
        CHECK(kg::zone_add(11,&zone,&id)==KILN_OK);
        for(auto link:links) { KilnCallFrame f{}; f.gpr[0]=reinterpret_cast<uintptr_t>(op); f.gpr[1]=reinterpret_cast<uintptr_t>(arg); f.gpr[2]=123;
            CHECK(link.fn(&f,link.user)==KILN_TYPED_SKIP); CHECK(f.rax==0); }
        CHECK(calls==0); CHECK(kg::g_recs.size()==5); // all five deny paths intentionally skip continuation
        if(mode=="zones") { CHECK(kg::zone_update(id,&zone)==KILN_OK); CHECK(kg::zones_any(KILN_GAMEPLAY_DENY_BREAK)); }
    }
    CHECK(!kg::build_reason().empty());
    const auto before=installs; kg::build_request(33); kg::hooks_poll(); CHECK(installs==before);
    kg::zones_retire(); // the snapshots a Post would free: the test has no Post (LeakSanitizer at exit)
    std::printf("build_hooks_test %s: %d checks, %d failures\n",mode.c_str(),checks,failures); return failures ? 1 : 0;
}
