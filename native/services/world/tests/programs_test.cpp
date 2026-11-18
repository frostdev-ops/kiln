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
bool game_in_slice() { return false; }  // wraps.cpp's: no wrap stage in this fixture
void map_each_u16(uintptr_t,const std::function<void(uint32_t,uint16_t)>&) {}  // entities.cpp's
uint32_t current_thread_id() { return 1; }
void writes_world_changing() {}  // tracked-write lifecycle belongs to writes_test; this fixture tests programs
void writes_before_world(uintptr_t) {}
void writes_after_world() {}
uint32_t writes_capability() { return KILN_WORLD_CAP_TRACKED_WRITES; }
uint32_t ecs_capability() { return KILN_WORLD_CAP_ECS_READ; }
std::vector<std::string> pruned_names;
bool seh_each(KilnProgramEachFn f,KilnProgramCtx* ctx,uint64_t* iter,KilnEntity e,void* user,unsigned long*) { f(ctx,iter,e,user);return true; }
bool seh_update(KilnProgramUpdateFn f,KilnProgramCtx* ctx,void* user,unsigned long*) { f(ctx,user);return true; }
// A fake engine table for register_into: count u16 at world + L.programs_count, entries from L.programs_table (the
// descriptor pointer at +0, the 1-based id at +L.entry_id), RegisterProgram refusing a full table as the engine does.
// `engine_skip` names a program the "engine" answers true for without registering it (descriptor flag 0x2 does that).
bool engine_on=false;
int engine_calls=0;
std::string engine_skip;
bool seh_register_program(RegisterProgramFn,uintptr_t world,const EngineDesc* d,bool* ok,unsigned long*) {
    if (!engine_on) return false;
    ++engine_calls;
    auto* count=reinterpret_cast<uint16_t*>(world+L.programs_count);
    if (std::string_view(d->name.s,d->name.n)==engine_skip) { *ok=true;return true; }
    if (*count>=L.programs_max) { *ok=false;return true; }
    const uintptr_t entry=world+L.programs_table+static_cast<uintptr_t>(*count)*L.programs_stride;
    std::memcpy(reinterpret_cast<void*>(entry),&d,8);
    const uint16_t id=static_cast<uint16_t>(*count+1);
    std::memcpy(reinterpret_cast<void*>(entry+L.entry_id),&id,2);
    ++*count;*ok=true;return true;
}
bool seh_call(void (*f)(void*),void* c,unsigned long*) { f(c);return true; }
bool seh_iter_begin(void* ctx,void* iter,uint32_t size,unsigned long*) { fn.iter_begin(ctx,iter,size);return true; }
bool seh_iter_next(void* ctx,void* iter,uint32_t size,bool* more,unsigned long*) { *more=fn.iter_next(ctx,iter,size);return true; }
bool seh_read_u64(uintptr_t address,uint64_t* value) { std::memcpy(value,reinterpret_cast<void*>(address),8);return true; }
bool seh_queue_push(void*,void*,uint8_t,uint32_t,uint32_t,void**) { return false; }
bool seh_copy(void* to,const void* from,size_t size) { std::memcpy(to,from,size);return true; }
StopDrain world_stop_drain(uint32_t, uint32_t) { return StopDrain{false, "a bounded wait only (test)"}; }
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

    // Kiln's quota (0.2.0-dev.29): 64 programs, Kiln's own services and the mods' together; the 65th is refused by name
    g_registered_once=false;
    std::vector<KilnHandle> handles;
    auto sys=[](const std::string& name) {
        static std::vector<std::string> names;
        names.push_back(name);
        KilnProgramDesc d{};
        d.size=sizeof d;d.flags=KILN_PROGRAM_SYSTEM|KILN_PROGRAM_HOST_ONLY;d.name=kstr(names.back());
        d.each=[](KilnProgramCtx*,uint64_t*,KilnEntity,void*) {};
        return d;
    };
    int accepted=0;
    for (int i=0;i<64;++i) {
        KilnProgramDesc d=sys("quota_"+std::to_string(i));
        KilnHandle h=0;
        if (register_program(g_owner,&d,&h,false)==KILN_OK && h==static_cast<KilnHandle>(i+1)) ++accepted,handles.push_back(h);
    }
    check(accepted==64,"64 programs are accepted");
    KilnProgramDesc over=sys("quota_64");
    KilnHandle oh=0;
    check(register_program(g_owner,&over,&oh,false)==KILN_ERR_UNAVAILABLE && oh==0,"the 65th is refused (KILN_ERR_UNAVAILABLE), no handle");
    check(programs_registered()==64 && program_quota()==64,"the ledger holds 64 of 64");
    check(g_by_slot[63]==g_programs[63] && g_programs[63]->desc.update==kTrampolines[63],"slot 63 has its own trampoline");
    KilnProgramInfo info{};
    info.size=sizeof info;
    check(program_info(handles[63],&info)==KILN_OK && info.state==KILN_PROGRAM_PENDING,"the 64th is pending until a world registers it");

    // Mods' boot declarations keep the last 8 slots for Kiln's own services, which may register after them
    for (Program* p : g_programs) delete p;
    g_programs.clear();
    std::fill(std::begin(g_by_slot),std::end(g_by_slot),nullptr);
    for (int i=0;i<55;++i) { KilnProgramDesc d=sys("svc_"+std::to_string(i)); KilnHandle h=0; register_program(g_owner,&d,&h,false); }
    KilnProgramDesc m1=sys("mod_a"),m2=sys("mod_b");
    KilnStr required[]={kstr("Fuel"),kstr("Health")}, optional[]={kstr("CurrentTransform")}, excluded[]={kstr("StaticTransform")};
    m1.flags=KILN_PROGRAM_EXCLUSIVE|KILN_PROGRAM_PINNED;
    m1.read={required,2,0}; m1.read_optional={optional,1,0}; m1.without={excluded,1,0};
    check(reserve_program("writer","a",&m1)==KILN_OK && programs_registered()==56,"a mod's declaration takes slot 56");
    check(reserve_program("writer","b",&m2)==KILN_ERR_UNAVAILABLE && programs_registered()==56,"the next waits on the 8 kept for services: refused");
    uint32_t bytes=0;
    check(program_reservation(kstr("writer"),kstr("a"),nullptr,0,&bytes)==KILN_OK && bytes>0,"reservation query sizes the immutable boot snapshot");
    std::vector<char> snapshot(bytes+1);
    check(program_reservation(kstr("writer"),kstr("a"),snapshot.data(),bytes,&bytes)==KILN_OK,"reservation query copies its snapshot");
    const std::string saved(snapshot.data(),bytes);
    check(saved.find("flags=00000480\ninterval=1\n")!=std::string::npos,"snapshot preserves flags and scheduler interval");
    check(saved.find("read=Fuel,Health\nreadOptional=CurrentTransform\n")!=std::string::npos && saved.find("without=StaticTransform\n")!=std::string::npos,"snapshot retains ordered names, optional split and filter");
    required[0]=kstr("ChangedAfterBoot");
    g_programs.back()->desc.interval=9; // live descriptors are not the boot contract
    check(program_reservation(kstr("writer"),kstr("a"),snapshot.data(),bytes,&bytes)==KILN_OK && std::string(snapshot.data(),bytes)==saved,"snapshot survives changes to the live descriptor");
    check(program_reservation(kstr("other-mod"),kstr("a"),nullptr,0,&bytes)==KILN_ERR_NOT_FOUND && bytes==0,"reservation identity includes mod id");
    check(program_reservation(kstr("writer"),kstr("a"),snapshot.data(),1,&bytes)==KILN_ERR_BUFFER_TOO_SMALL,"short reservation buffer is refused");
    check(program_reservation(kstr("writer"),kstr("a"),nullptr,0,nullptr)==KILN_ERR_INVALID_ARGUMENT,"reservation query needs a size output");
    int late=0;
    for (int i=0;i<8;++i) { KilnProgramDesc d=sys("late_"+std::to_string(i)); KilnHandle h=0; late+=register_program(g_owner,&d,&h,false)==KILN_OK; }
    check(late==8 && programs_registered()==64,"a service that registers later still finds its 8");

    // A live engine table: 894 of 896 taken (the game's and another loader's). Kiln registers until the engine is full and
    // refuses the rest before calling the game; a program the engine "takes" without counting it is refused.
    for (Program* p : g_programs) delete p;
    g_programs.clear();
    std::fill(std::begin(g_by_slot),std::end(g_by_slot),nullptr);
    static std::vector<uint8_t> engine(0x40+896*0x30);
    std::fill(engine.begin(),engine.end(),0);
    const uintptr_t world=reinterpret_cast<uintptr_t>(engine.data());
    L.programs_count=0;L.programs_table=0x40;L.programs_stride=0x30;L.programs_max=896;
    *reinterpret_cast<uint16_t*>(world)=894;
    engine_on=true;engine_calls=0;engine_skip="quota_b";
    for (const char* n : {"quota_a","quota_b","quota_c","quota_d"}) {
        KilnProgramDesc d=sys(n);
        KilnHandle h=0;
        register_program(g_owner,&d,&h,false);
    }
    known_components.clear();
    fake_world[0]=reinterpret_cast<uintptr_t>(fake_registry);
    register_into(world);
    auto state=[&](int i) { KilnProgramInfo x{};x.size=sizeof x;program_info(static_cast<KilnHandle>(i+1),&x);return x; };
    check(state(0).state==KILN_PROGRAM_ACTIVE && state(0).engine_id==895,"895 of 896: registered as #895");
    check(state(1).state==KILN_PROGRAM_REFUSED,"an engine that answers true without counting it: refused");
    check(state(2).state==KILN_PROGRAM_ACTIVE && state(2).engine_id==896,"896 of 896: registered as #896");
    check(state(3).state==KILN_PROGRAM_REFUSED && engine_calls==3,"the 897th is refused before the game is called");
    check(*reinterpret_cast<uint16_t*>(world)==896,"the engine table holds 896, no more");
    engine_on=false;
    for (Program* p : g_programs) delete p;
    g_programs.clear();
    std::fill(std::begin(g_by_slot),std::end(g_by_slot),nullptr);

    // "world" is part of a reservation's identity (0.2.0-dev.29, chapter 14): a declaration that is not the server
    // world's alone ends its boot snapshot with its world; a server one's text is what it was before
    g_registered_once=false;
    g_client_on=true;
    KilnStr names[]={kstr("RenderTransform")};
    auto entity=[&](const std::string& name,uint32_t flags) { KilnProgramDesc d=sys(name); d.flags=flags; d.read={names,1,0}; return d; };
    KilnProgramDesc ws=entity("w_server",KILN_PROGRAM_HOST_ONLY),wc=entity("w_client",0),wb=entity("w_both",KILN_PROGRAM_HOST_ONLY),
        wp=entity("w_prediction",0);
    check(reserve_program_world("writer","s",&ws,kWorldBitServer)==KILN_OK && reserve_program_world("writer","c",&wc,kWorldBitClient)==KILN_OK &&
          reserve_program_world("writer","b",&wb,kWorldBitServer|kWorldBitClient)==KILN_OK &&
          reserve_program_world("writer","p",&wp,kWorldBitPrediction)==KILN_OK,"four reservations, one per world");
    auto text=[&](const char* key) {
        uint32_t n=0;
        if (program_reservation(kstr("writer"),kstr(key),nullptr,0,&n)!=KILN_OK) return std::string("?");
        std::string t(n,'\0');
        program_reservation(kstr("writer"),kstr(key),t.data(),n,&n);
        return t;
    };
    const std::string ts=text("s"),tc=text("c"),tb=text("b"),tp=text("p");
    check(ts.find("world=")==std::string::npos && ts.size()>=26 && ts.compare(ts.size()-26,26,"flags=00000010\ninterval=1\n")==0,
          "a server reservation's snapshot has no world line: the text it had before 0.2.0-dev.29");
    check(tc.size()>13 && tc.compare(tc.size()-13,13,"world=client\n")==0 && tc.find("read=RenderTransform\n")!=std::string::npos,
          "a client reservation's snapshot ends with world=client");
    check(tb.size()>11 && tb.compare(tb.size()-11,11,"world=both\n")==0 && tp.size()>17 && tp.compare(tp.size()-17,17,"world=prediction\n")==0,
          "both and prediction say so too");
    check(tc.substr(0,tc.size()-13)==tp.substr(0,tp.size()-17) && tc!=tp,
          "the same lists and flags for another world: another reservation (a rebuild under one key is refused until a restart)");
    g_client_on=false;
    g_side_programs=0;
    for (Program* p : g_programs) delete p;
    g_programs.clear();
    std::fill(std::begin(g_by_slot),std::end(g_by_slot),nullptr);
    std::printf("world program boundaries: %s %d checks, %d failures\n",failures?"FAIL":"PASS",checks,failures);
    return failures?1:0;
}
