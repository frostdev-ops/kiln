// SPDX-License-Identifier: MIT
// Compile the real helpers and owner lifecycle against a fake engine; no game or Windows runtime needed.
#include "../programs.cpp"
#include <cstdio>
#include <cstdlib>
#include <map>
#include <thread>
namespace kw {
int marks=0;
const KilnApi* g_api;
KilnOwner g_owner=1;
Fns fn;
Layout L;
Features F;
std::atomic<uintptr_t> g_world{0};
std::atomic<uint64_t> g_internal_runs{0};
std::atomic<bool> g_world_live{false};
thread_local bool t_in_program=false;
void logf(int,const char*,...) {}
uint32_t fnv1a(std::string_view value,uint32_t hash) { for (unsigned char c:value) hash=(hash^c)*16777619u;return hash; }
uint32_t component_hash(std::string_view value) { return fnv1a(value); }
std::map<uint32_t,uint16_t> known_components;  // component hash -> the engine's type index
bool map_find_u16(uintptr_t,uint32_t key,uint16_t* value) {
    const auto at=known_components.find(key);
    if (at==known_components.end()) return false;
    if (value) *value=at->second;
    return true;
}
void teleport_in_program(KilnProgramCtx*) {}
void writes_before_world(uintptr_t) {}
void writes_after_world() {}
uint32_t writes_capability() { return KILN_WORLD_CAP_TRACKED_WRITES; }
std::vector<std::string> pruned_names;
bool seh_each(KilnProgramEachFn f,KilnProgramCtx* ctx,uint64_t* iter,KilnEntity e,void* user,unsigned long*) { f(ctx,iter,e,user);return true; }
bool seh_update(KilnProgramUpdateFn f,KilnProgramCtx* ctx,void* user,unsigned long*) { f(ctx,user);return true; }
bool seh_register_program(RegisterProgramFn,uintptr_t,const EngineDesc*,bool*,unsigned long*) { return false; }
bool seh_iter_begin(void* ctx,void* iter,uint32_t size,unsigned long*) { fn.iter_begin(ctx,iter,size);return true; }
bool seh_iter_next(void* ctx,void* iter,uint32_t size,bool* more,unsigned long*) { *more=fn.iter_next(ctx,iter,size);return true; }
bool seh_read_u64(uintptr_t address,uint64_t* value) { std::memcpy(value,reinterpret_cast<void*>(address),8);return true; }
bool seh_queue_push(void*,void*,uint8_t,uint32_t,uint32_t,void**) { return false; }
bool seh_copy(void* to,const void* from,size_t size) { std::memcpy(to,from,size);return true; }
}
using namespace kw;
int main() {
    int checks=0,failures=0;
    auto check=[&](bool ok,const char* message) {++checks;if (!ok) {++failures;std::fprintf(stderr,"FAIL %s\n",message);} };
    KilnApi api{};
    api.on_tick_thread=[] {return true;};
    api.post=[](KilnOwner owner,KilnTaskFn,void*) -> KilnResult {return owner==99?KILN_ERR_REVOKED:KILN_OK;};
    g_api=&api;
    F.programs=F.queue=F.write=F.read_other=true;
    Program program;
    program.slot=0;program.owner=99;program.reserved_mod="writer";program.reserved_key="pulse";
    program.state=KILN_PROGRAM_FAULTED;program.each=[](KilnProgramCtx*,uint64_t*,KilnEntity,void*) {};
    program.user=reinterpret_cast<void*>(1234);
    g_programs.push_back(&program);g_by_slot[0]=&program;
    static int posts=0;
    api.post=[](KilnOwner owner,KilnTaskFn,void*) -> KilnResult {++posts;return owner==99?KILN_ERR_REVOKED:KILN_OK;};
    programs_reap_owners();
    check(program.owner==g_owner && program.user==nullptr,"faulted reserved callback releases revoked owner");
    program.owner=55;posts=0;
    for (int i=0;i<100;++i) programs_reap_owners();
    check(posts==0,"owner liveness is probed at most once per second, not per tick");
    program.owner=g_owner;
    KilnHandle rebound=0;
    check(bind_program(100,kstr("writer"),kstr("pulse"),program.each,nullptr,&rebound)==KILN_OK && rebound==1,"faulted declaration rebinds after owner revocation");

    uint8_t job[32]{};
    void* buffer=reinterpret_cast<void*>(1234);
    std::memcpy(job,&buffer,sizeof buffer);
    ProgramCtx context{job,0,0},foreign{job,0,0};
    auto* good=reinterpret_cast<KilnProgramCtx*>(&context);
    auto* wrong=reinterpret_cast<KilnProgramCtx*>(&foreign);
    uint64_t iter[16]{};
    t_in_program=true;t_prog=&program;t_context=&context;t_iterator=iter;
    program.iter_size=sizeof iter;
    L.job_cmdbuf=L.job_storage=0;
    check(iter_begin(good,iter,sizeof iter)==KILN_ERR_UNAVAILABLE,"missing iterator symbol fails soft");
    check(iter_begin(wrong,iter,sizeof iter)==KILN_ERR_INVALID_ARGUMENT,"iterator initialization rejects a foreign context");
    fn.iter_next=[](void*,void*,uint32_t) {return true;};
    fn.current_entity=[](void*,uint32_t* out) {*out=42;return out;};
    check(!iter_next(wrong,iter,sizeof iter),"iteration rejects a foreign context on the same worker");
    check(!iter_next(good,iter,8),"iteration rejects a buffer smaller than the engine layout");
    check(!iter_next(good,iter+1,sizeof iter),"iteration rejects an uninitialized iterator");
    check(iter_next(good,iter,sizeof iter),"matching iterator still advances");
    check(current_entity(wrong)==0 && current_entity(good)==42,"entity lookup uses this callback context only");
    check(command_buffer(wrong)==nullptr && command_buffer(good)==buffer,"command buffer uses this callback context only");
    fn.read_other=[](Slice* out,void*,void*,uint32_t) {out->ptr=reinterpret_cast<void*>(1234);out->size=4;};
    iter[1]=17;program.lists[L_OREAD].slot={1};
    uint32_t size=123;
    check(read_other(wrong,17,1,&size)==nullptr && size==0,"other read rejects a foreign context");
    check(read_other(good,18,1,&size)==nullptr && size==0,"other read rejects an undeclared accessor");
    check(read_other(good,17,1,&size)==buffer && size==4,"declared other read still succeeds");
    fn.read_other=[](Slice* out,void*,void*,uint32_t) {out->ptr=reinterpret_cast<void*>(1234);out->size=uint64_t{UINT32_MAX}+1;};
    check(read_other(good,17,1,&size)==nullptr && size==0,"component sizes cannot truncate at the ABI boundary");
    program.lists[L_WRITE].slot={2};
    iter[7]=1;
    fn.mark_written=[](void*,void*,void*,void*,uint16_t,uint8_t) {++marks;};
    // With an invalid pointer these checks become an ASan crash unless validation happens before dereferencing it.
    mark_written(wrong,reinterpret_cast<uint64_t*>(1));
    mark_written(good,reinterpret_cast<uint64_t*>(1));
    check(marks==0,"mark_written rejects foreign/undeclared write records before reading them");
    mark_written(good,iter+2);
    check(marks==1,"declared write record still marks replication/save dirtiness");
    bool foreign_worker_rejected=false;
    std::thread worker([&] {foreign_worker_rejected=current_entity(good)==0 && command_buffer(good)==nullptr &&
        !iter_next(good,iter,sizeof iter);});
    worker.join();
    check(foreign_worker_rejected,"context cannot be reused on a different worker thread");
    t_in_program=false;t_prog=nullptr;t_context=nullptr;t_iterator=nullptr;
    check(current_entity(good)==0 && command_buffer(good)==nullptr,"helpers reject a context after callback return");
    g_programs.clear();g_by_slot[0]=nullptr;

    // The write program is registered without the names the world does not know; every other program is refused whole.
    check((program_capabilities()&KILN_WORLD_CAP_TRACKED_WRITES)!=0,"the capability word carries tracked writes");
    static uintptr_t fake_world[4]{};
    static uintptr_t fake_registry[64]{};
    fake_world[0]=reinterpret_cast<uintptr_t>(fake_registry);
    L.world_storage=0;L.reg_components=8;L.reg_templates=16;
    g_registered_once=false;
    const KilnStr other[]={kstr("Health"),kstr("NoSuchComponent"),kstr("Fuel"),kstr("AlsoMissing")};
    KilnProgramDesc declared{};
    declared.size=sizeof declared;declared.flags=KILN_PROGRAM_SYSTEM|KILN_PROGRAM_HOST_ONLY;declared.name=kstr("kiln_test_writes");
    declared.update=[](KilnProgramCtx*,void*) {};
    declared.other_write=KilnNames{other,4,0};
    KilnHandle writes=0;
    check(register_program(g_owner,&declared,&writes,false)==KILN_OK && writes==1,"a program with four other-write names is accepted");
    program_set_prune(writes,[](std::string_view name) {pruned_names.emplace_back(name);});
    known_components={{fnv1a("Health",fnv1a("keen::ecs::")),20},{fnv1a("Fuel",fnv1a("keen::ecs::")),10}};
    Program* made=g_programs[0];
    prune_unknown(*made,reinterpret_cast<uintptr_t>(fake_world));
    check(pruned_names==std::vector<std::string>({"NoSuchComponent","AlsoMissing"}),"the names the world lacks are reported");
    check(made->lists[L_OWRITE].names==std::vector<std::string>({"Health","Fuel"}) && made->desc.other_write.n==2 &&
          made->desc.other_write.p==made->lists[L_OWRITE].refs.data(),"and leave the descriptor the engine will read");
    check(program_slot(writes,KILN_SLOT_OTHER_WRITE,kstr("Fuel"))==1 && program_slot(writes,KILN_SLOT_OTHER_WRITE,kstr("Health"))==2 &&
          program_slot(writes,KILN_SLOT_OTHER_WRITE,kstr("NoSuchComponent"))==-1 && made->O==2 && made->iter_size==24,
          "slots are laid out again for the names that stay (name order)");
    std::string why;
    check(validate(*made,reinterpret_cast<uintptr_t>(fake_world),why),"what is left validates against the world's maps");
    pruned_names.clear();
    prune_unknown(*made,reinterpret_cast<uintptr_t>(fake_world));
    check(pruned_names.empty() && made->desc.other_write.n==2,"a second world prunes nothing more");
    g_programs.clear();g_by_slot[0]=nullptr;
    delete made;  // the service never frees a program (the engine keeps its descriptor); the test has no engine
    std::printf("world program boundaries: %s %d checks, %d failures\n",failures?"FAIL":"PASS",checks,failures);
    return failures?1:0;
}
