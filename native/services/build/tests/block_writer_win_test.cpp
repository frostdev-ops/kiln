// SPDX-License-Identifier: MIT
// Run the existing exact writer and shape entry point over a small, readable fake world.
#include "../internal.h"
#include "../voxel_tasks.h"
#include "../block_runtime.h"
#include "services/kiln_caps.h"
#include <map>
#include <cstdio>
#include <cstdarg>
namespace {
KilnVoxel cells[16*16*16]{};
uint64_t grid_data[7]{},wrapper[2]{},base_list[2]{};
uint8_t editor[1024]{};
bool readable_bases=true,on_tick=true;int commits=0;KilnEntity seen_actor=0;
uint32_t session_state=KILN_SESSION_HOST;bool session_live=true;
uint64_t session_id=1;
int cap_lookups=0,cap_reports=0;bool fail_report=false;
std::map<std::string,std::pair<uint32_t,uint32_t>> cap_rows;
KilnResult KILN_CALL cap_report(KilnOwner,KilnStr name,uint32_t state,uint32_t reason,KilnStr){
    ++cap_reports;if(fail_report){fail_report=false;return KILN_ERR_UNAVAILABLE;}
    cap_rows[std::string(kb::sv(name))]={state,reason};return KILN_OK;
}
const void* KILN_CALL service(KilnStr name,uint32_t){
    ++cap_lookups;static KilnCapsApi caps{};caps.header.size=sizeof caps;caps.report=&cap_report;
    return kb::sv(name)==KILN_CAPS_SERVICE?&caps:nullptr;
}
size_t at(int x,int y,int z){return static_cast<size_t>((z*16+y)*16+x);}
uintptr_t KILN_CALL singleton(uintptr_t,KilnStr name){
    return kb::sv(name)=="ecs::VoxelGrid"?reinterpret_cast<uintptr_t>(grid_data):reinterpret_cast<uintptr_t>(base_list);
}
KilnResult KILN_CALL session(KilnSessionInfo* s){s->state=session_state;s->lifecycle=session_live?1:0;s->worlds[KILN_WORLD_SERVER]=123;s->sessions[KILN_WORLD_SERVER]=session_id;return KILN_OK;}
bool KILN_CALL zone_check(KilnEntity actor,KilnVec3,uint32_t,uint32_t*){seen_actor=actor;return false;}
void native_write(uintptr_t,void* p,const uint32_t* d,const int32_t* o){
    ++commits;const auto* span=static_cast<kb::CellSpan*>(p);size_t j=0;
    for(uint32_t z=0;z<d[2];++z)for(uint32_t y=0;y<d[1];++y)for(uint32_t x=0;x<d[0];++x)
        cells[at(o[0]+x,o[1]+y,o[2]+z)]=span->data[j++];
}
}
namespace kb {
const KilnApi* g_api=nullptr;const KilnGameplayApi* g_gameplay=nullptr;
Features F;Layout L;Fns fn;Stats S;std::string detail;KilnOwner g_owner=1;
uintptr_t server_world(){return 123;}
bool tick_thread(){return on_tick;}
bool world_running(){return true;}
void logf(int,const char*,...){}
void set_detail(const char* format,...){char b[256];va_list args;va_start(args,format);std::vsnprintf(b,sizeof b,format,args);va_end(args);detail=b;}
void detail_copy(char* out,size_t n){std::snprintf(out,n,"%s",detail.c_str());}
bool seh_copy(void* dst,const void* src,size_t n){if(src==base_list && !readable_bases)return false;std::memcpy(dst,src,n);return true;}
bool seh_read(uintptr_t,ReadRequest* r,uintptr_t,bool* ok){size_t j=0;
    for(uint32_t z=0;z<r->dims[2];++z)for(uint32_t y=0;y<r->dims[1];++y)for(uint32_t x=0;x<r->dims[0];++x)
        r->data[j++]=r->origin[0]<0 || r->origin[1]<0 || r->origin[2]<0 ||
            r->origin[0]+x>=16 || r->origin[1]+y>=16 || r->origin[2]+z>=16?0:
            cells[at(r->origin[0]+x,r->origin[1]+y,r->origin[2]+z)];
    *ok=true;return true;
}
bool seh_ready(uintptr_t,uintptr_t,float*,bool* ok){*ok=true;return true;}
bool seh_write(Fns::WriteFn f,uintptr_t w,CellSpan* s,const uint32_t* d,const int32_t* o,unsigned long*){f(w,s,d,o);return true;}
bool regions_lifecycle_safe(){return true;}
int keeps=0,kept_before=0;
void regions_keep(const int32_t*,const uint32_t*){++keeps;}
KilnResult transient_kept_before(const int32_t*,const uint32_t*,const uint8_t*){++kept_before;return KILN_OK;}
KilnResult transient_note_before(const int32_t*,const uint32_t*,const KilnVoxel*,const KilnVoxel*,const uint8_t*){return KILN_OK;}
void transient_kept_failed(const std::vector<std::array<int32_t,3>>&){}
namespace tasks {Grid& grid(){static Grid instance;return instance;}void Grid::clear(){}}
}
int main(){
    int total=0,failed=0;auto check=[&](bool ok,const char* name){++total;if(!ok){++failed;std::printf("FAIL %s\n",name);}};
    KilnApi api{};api.singleton=&singleton;api.session=&session;kb::g_api=&api;
    KilnGameplayApi gameplay{};gameplay.header.size=sizeof gameplay;gameplay.zone_denies=&zone_check;kb::g_gameplay=&gameplay;
    kb::F.read=kb::F.write=kb::F.persist=kb::F.zones=true;
    kb::L.grid_iface=0;kb::L.wrapper_world=8;kb::L.world_init=0x14;kb::L.world_origin=0x30;
    kb::L.world_levels=0x40;kb::L.world_store=0x220;kb::L.level=6;kb::L.base_size=32;kb::L.bzs_bases=0;
    uint32_t initialized=1,level[4]={256,256,1,1};uintptr_t store=123;
    std::memcpy(editor+0x14,&initialized,4);std::memcpy(editor+0x40+6*40,level,sizeof level);std::memcpy(editor+0x220,&store,sizeof store);
    wrapper[1]=reinterpret_cast<uintptr_t>(editor);grid_data[0]=reinterpret_cast<uintptr_t>(wrapper);
    grid_data[1]=kb::fn.grid_read=123;grid_data[3]=kb::fn.grid_ready=456;kb::fn.write=&native_write;
    KilnBuildWrite w{};w.size=sizeof w;w.origin[0]=w.origin[1]=w.origin[2]=4;w.dims[0]=w.dims[1]=w.dims[2]=1;
    KilnVoxel value=KILN_VOXEL_MAKE(2,255);w.cells=&value;w.actor=42;w.flags=KILN_BUILD_F_RESPECT_ZONES;
    KilnBuildResult r{};r.size=sizeof r;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_OK && r.cells_changed==1 && (r.flags&KILN_BUILD_R_VERIFIED),"existing Write succeeds with readable protection data");
    check(seen_actor==42,"existing writer forwards the requested actor to zone protection");
    KilnBuildShape shape{};shape.size=sizeof shape;shape.flags=KILN_BUILD_F_RESPECT_ZONES;shape.actor=42;
    shape.op=KILN_BUILD_OP_DIG;shape.shape=KILN_BUILD_SHAPE_SPHERE;shape.center={2.25,2.25,2.25};shape.half={.5,.5,.5};
    check(kb::shape_run(&shape,&r)==KILN_OK && cells[at(4,4,4)]==0 && r.cells_changed>0,"existing Dig succeeds with readable protection data");
    shape.op=KILN_BUILD_OP_ADD;shape.shape=KILN_BUILD_SHAPE_BOX;shape.material=2;
    check(kb::shape_run(&shape,&r)==KILN_OK && cells[at(4,4,4)]!=0 && r.cells_changed>0,"existing box shape succeeds with readable protection data");
    readable_bases=false;value=KILN_VOXEL_MAKE(3,255);const int before=commits;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_ERR_REVOKED && (r.flags&KILN_BUILD_R_BASE) && commits==before && kb::detail.find("base mapping")!=std::string::npos,"unreadable base data refuses before an existing writer commits with a distinct diagnostic");
    w.flags=KILN_BUILD_F_IN_BASES;kb::g_gameplay=nullptr;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_OK && cells[at(4,4,4)]==value,"explicit IN_BASES and RespectZones=false still bypass those optional protections");
    value=KILN_VOXEL_MAKE(4,255);w.flags|=KILN_BUILD_F_RESPECT_ZONES;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_ERR_REVOKED && (r.flags&KILN_BUILD_R_ZONE) && kb::detail.find("zone checks unavailable")!=std::string::npos,"missing requested zone check refuses with a distinct diagnostic");
    // KILN_BUILD_F_GAME_SAVE: the game's own rule (saved inside base build zones only), as ShroudForge's writes are: no Kiln
    // save region, and so no need of the save-region hook; journaled transient cells it sets are still taken out
    w.flags=KILN_BUILD_F_IN_BASES|KILN_BUILD_F_GAME_SAVE;value=KILN_VOXEL_MAKE(5,255);kb::keeps=kb::kept_before=0;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_OK && cells[at(4,4,4)]==value && (r.flags&KILN_BUILD_R_CHANGED) &&
          !(r.flags&KILN_BUILD_R_KEPT) && kb::keeps==0 && kb::kept_before==1,"GAME_SAVE writes without a Kiln save region");
    kb::F.persist=false;value=KILN_VOXEL_MAKE(6,255);
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_OK && cells[at(4,4,4)]==value && kb::keeps==0,"GAME_SAVE needs no save-region hook");
    w.flags=KILN_BUILD_F_IN_BASES;value=KILN_VOXEL_MAKE(7,255);
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_ERR_UNAVAILABLE && cells[at(4,4,4)]==KILN_VOXEL_MAKE(6,255),"a kept write still needs the save-region hook");
    kb::F.persist=true;w.flags=KILN_BUILD_F_IN_BASES|KILN_BUILD_F_GAME_SAVE|KILN_BUILD_F_TRANSIENT;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_ERR_INVALID_ARGUMENT && cells[at(4,4,4)]==KILN_VOXEL_MAKE(6,255),"GAME_SAVE with TRANSIENT is refused");
    w.flags=KILN_BUILD_F_IN_BASES;value=KILN_VOXEL_MAKE(8,255);kb::keeps=0;
    check(kb::vox_write(&w,&r,KILN_GAMEPLAY_DENY_BUILD)==KILN_OK && (r.flags&KILN_BUILD_R_KEPT) && kb::keeps==1,"without GAME_SAVE the write is kept as before");
    KilnBuildBlockInfo info{};info.size=sizeof info;
    check(kb::block_at({-.1,2,2},&info)==KILN_ERR_INVALID_ARGUMENT && info.refusal==KILN_BUILD_BLOCK_INVALID,"block query rejects below-grid coordinates instead of trusting native outside-air");
    check(kb::block_at({128,2,2},&info)==KILN_ERR_INVALID_ARGUMENT && info.refusal==KILN_BUILD_BLOCK_INVALID,"block query rejects the exclusive horizontal grid boundary");
    check(kb::find_wall({.1,2,2},{-.1,2,2},0,&info)==KILN_ERR_INVALID_ARGUMENT && info.refusal==KILN_BUILD_BLOCK_INVALID,"wall search crossing outside the grid refuses instead of reporting a clear line");
    on_tick=false;
    check(kb::block_at({2,2,2},&info)==KILN_ERR_WRONG_THREAD && info.refusal==KILN_BUILD_BLOCK_WRONG_THREAD,"block runtime enforces the tick thread");on_tick=true;
    session_state=KILN_SESSION_REMOTE;
    check(kb::block_at({2,2,2},&info)==KILN_ERR_UNAVAILABLE && info.refusal==KILN_BUILD_BLOCK_WRONG_SIDE,"block runtime refuses a remotely joined client");session_state=KILN_SESSION_HOST;
    api.session=nullptr;
    check(kb::block_at({2,2,2},&info)==KILN_ERR_UNAVAILABLE && info.refusal==KILN_BUILD_BLOCK_OFFLINE,"block runtime reports a missing game provider as Offline");api.session=&session;
    session_live=false;
    check(kb::block_at({2,2,2},&info)==KILN_ERR_UNAVAILABLE && info.refusal==KILN_BUILD_BLOCK_NO_SESSION,"block runtime refuses unavailable world lifecycle");session_live=true;
    api.service=&service;
    kb::block_caps();
    check(cap_reports==8 && cap_rows["build.block_info"].first==KILN_CAP_AVAILABLE,"block caps initially reports all rows");
    int reports=cap_reports,lookups=cap_lookups;
    kb::block_caps();
    check(cap_reports==reports && cap_lookups==lookups+1,"unchanged block caps avoids reports and performs only one service lookup");
    kb::F.read=false;kb::block_caps();
    check(cap_reports==reports+2 && cap_rows["build.block_info"].second==KILN_BUILD_BLOCK_SYMBOL_MISSING,"read hook uninstall updates only affected rows");
    reports=cap_reports;kb::F.read=true;kb::block_caps();
    check(cap_reports==reports+2 && cap_rows["build.find_wall"].first==KILN_CAP_AVAILABLE,"read hook install restores query capabilities mid-session");
    reports=cap_reports;session_live=false;kb::block_caps();
    check(cap_rows["build.block_info"].first==KILN_CAP_PENDING && cap_rows["build.block_info"].second==KILN_BUILD_BLOCK_NO_SESSION,"stopping session changes query rows to pending");
    session_live=true;kb::block_caps();reports=cap_reports;++session_id;kb::block_caps();
    check(cap_reports==reports+4,"replacement session re-reports all four session rows even with equal states");
    reports=cap_reports;kb::block_caps();check(cap_reports==reports,"replacement session settles without per-tick reports");
    kb::F.read=false;fail_report=true;kb::block_caps();reports=cap_reports;kb::block_caps();
    check(cap_reports==reports+1 && cap_rows["build.block_info"].second==KILN_BUILD_BLOCK_SYMBOL_MISSING,"failed capability report is retried, not cached as delivered");
    std::printf("block writer: %d passed, %d failed\n",total-failed,failed);return failed?1:0;
}
