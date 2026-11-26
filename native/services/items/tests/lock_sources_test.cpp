// SPDX-License-Identifier: MIT
// GitHub #147: a lock's required item from the player's opted-in links, on the real kiln-items glue (the mid-hook
// handlers at locks/LockCountRet, locks/client.UiLockCountRet, locks/client.LockRowCount and locks/LockPaid, the server's cache, the client's
// snapshot and asks, add_source's flags, source_stock's lock rows) against fake game memory: fake registers, a fake
// InteractionLock, a fake transaction and PayMaterial. No game, no world: what the game draws and sends is the
// real-game check.
#include "../stations.cpp"
#include <cstdarg>
#include <chrono>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <thread>
namespace fixture {
std::map<uint32_t,std::map<uint32_t,uint32_t>> inventory;      // what kiln.items' reads and the transaction show
std::map<uint32_t,std::map<uint32_t,uint32_t>> tx_view;        // the transaction's view when it differs
bool server_storage = true, fail_take = false;
uint32_t refuse = 0;
std::vector<std::pair<uint32_t,uint32_t>> taken;               // every PayMaterial take Kiln made (entity, n)
void* expected_tx = nullptr;
bool wrong_tx = false;
uint8_t world_kind = 0;                                        // the program context's world (0 server, 2 client)
uint32_t current = 1;                                          // CurrentEntityId's answer
}
namespace kitems {
Engine E{};
KilnResult KILN_CALL dyes_set_stock(KilnEntity,const KilnStationStack*,uint32_t) { return KILN_ERR_UNAVAILABLE; }
const char* const kAccNames[A_COUNT] = {};
bool verify_access(const uint64_t*,const int*,const int*,const int*,void*,Access*,bool*,char*,unsigned long*) { std::abort(); }
void abandon_tx(uint8_t*) { std::abort(); }
bool note_wanted() { return false; }
NoteLease::~NoteLease() {}
void NoteLease::begin(const Access&,const uint32_t*,uint32_t) { std::abort(); }
void NoteLease::end(const Access&) { std::abort(); }
bool run_move(const Access&,const MoveJob&,uint8_t*,KilnItemsMoveResult*,unsigned long*) { std::abort(); }
void items_count_move(bool) { std::abort(); }
bool read_fuel(uintptr_t,void*,KilnEntity,int32_t*,int32_t*,bool*,unsigned long*) { std::abort(); }
uint64_t items_next_ticket() { std::abort(); }
bool read_inventory(uintptr_t,void*,KilnEntity e,ReadOut* out,unsigned long*) {
    out->found=fixture::inventory.count(e)!=0;out->written=0;out->info.initialized=out->found;
    for(auto [item,count]:fixture::inventory[e]) if(count && out->written<out->capacity) {
        auto& slot=out->slots[out->written++];slot={};slot.item=item;slot.count=count;
    }
    return true;
}
uintptr_t items_server_world() { return 1; }
std::vector<std::string> logs;
std::mutex logs_mu;  // a hosting client's two worlds log from two threads (the install race below)
void items_log(int,const char* fmt,...) {
    char b[1024]; va_list a; va_start(a,fmt); std::vsnprintf(b,sizeof b,fmt,a); va_end(a);
    std::lock_guard lock(logs_mu); logs.emplace_back(b);
}
uint64_t current_step() { return 1; }
const KilnApi* items_api() { return g_api; }
KilnOwner items_owner() { return 10; }
bool access_from_tx(void*,Access*) { std::abort(); }  // the lock payment never asks for Fuel
bool access_from_tx_reads(void* tx,Access* out) { *out = {}; if (tx != fixture::expected_tx) fixture::wrong_tx = true; return fixture::server_storage; }
bool setup_flags(const Access&,KilnEntity e,uint8_t* flags) { *flags = e <= 1023 ? 1 : 0; return fixture::inventory.count(e) != 0; }
bool read_pages(const Access&,KilnEntity e,PageSet* out,bool* initialized) {
    *out = {}; *initialized = fixture::inventory.count(e) != 0;
    if (!*initialized) return false;
    out->n = 1; out->p[0].entity = e; size_t i=0;
    const auto& view = fixture::tx_view.count(e) ? fixture::tx_view[e] : fixture::inventory[e];
    for (auto [item,count] : view) { if(i==8)break; out->p[0].s[i++] = {0,item,count,0}; }
    return true;
}
void player_guard(const Access&,KilnEntity e,PlayerGuard* out) { *out = {}; out->is_player = e <= 1023; }
bool inventory_matches(uintptr_t,KilnEntity e,uint32_t,bool* match,unsigned long*) { *match = e > 1023 && fixture::inventory.count(e); return true; }
}
namespace {
using namespace kitems;
PayOut* __fastcall pay(PayOut* out,void* tx,uint32_t entity,uint32_t item,uint32_t n) {
    if(tx!=fixture::expected_tx)fixture::wrong_tx=true;
    *out={};
    if(fixture::refuse==entity){out->error=0x0b;return out;}
    if(fixture::fail_take) RaiseException(0xe1230001,0,0,nullptr);
    auto& count=fixture::inventory[entity][item]; const auto take=std::min(n,count); count-=take;
    if(fixture::tx_view.count(entity)) fixture::tx_view[entity][item]-=std::min(take,fixture::tx_view[entity][item]);
    if(take)fixture::taken.emplace_back(entity,take);
    out->left=n-take;return out;
}
PtrSize* __fastcall component(PtrSize* out,uintptr_t,uint32_t,uint32_t) { *out={}; return out; }
bool KILN_CALL tick_thread() {return true;}
void* g_bases_ptr = nullptr;
uintptr_t KILN_CALL singleton(uintptr_t,KilnStr name) {
    return std::string_view(name.ptr,name.len)=="PlayerBasesSystemServer"?reinterpret_cast<uintptr_t>(g_bases_ptr):0;
}
KilnResult KILN_CALL session(KilnSessionInfo* info) {info->state=KILN_SESSION_DEDICATED;return KILN_OK;}
// the program context: qword 0 = the job; JobStorage(job) = the storage, its kind byte at +8
uint8_t g_storage[16] = {};
void* __fastcall job_storage(void*) { g_storage[8] = fixture::world_kind; return g_storage; }
uint32_t* current_entity(void*, uint32_t* out) { *out = fixture::current; return out; }
// kiln.hooks and kiln.caps stand-ins: mid2 counts its links per target (the first link at the lock check's call site
// can let another thread in while it is going in, as a hosting client's client world may), code_state answers the
// file's bytes, report records the capability rows lowered
std::mutex mid2_mu;
std::map<uintptr_t,int> mid2_links;
std::function<void(uintptr_t)> mid2_during;
uintptr_t mid2_refuse = 0;            // review of 31.15: a target kiln.hooks cannot hook
std::vector<uintptr_t> foreign;       // review of 31.15: addresses another module patched (code_state says FOREIGN)
KilnResult KILN_CALL mid2(KilnOwner,uintptr_t target,const KilnHookOrder*,KilnMidFn,void*,KilnHandle* h) {
    if (target && target == mid2_refuse) return KILN_ERR_UNAVAILABLE;
    { std::lock_guard lock(mid2_mu); ++mid2_links[target]; *h = 100 + mid2_links.size(); }
    if (mid2_during) mid2_during(target);
    return KILN_OK;
}
KilnResult KILN_CALL revoke(KilnHandle) { return KILN_OK; }
KilnResult KILN_CALL code_state(uintptr_t at,size_t n,uint32_t* state,uint64_t* generation) {
    *state=KILN_CODE_ORIGINAL; *generation=0;
    for (uintptr_t f : foreign) if (f >= at && f - at < n) *state = KILN_CODE_FOREIGN;
    return KILN_OK;
}
KilnHooksApi hooks = [] { KilnHooksApi t{}; t.header = {sizeof t, KILN_HOOKS_VERSION}; t.mid2 = &mid2; t.revoke = &revoke; t.code_state = &code_state; return t; }();
std::map<std::string,uint32_t> lowered;
KilnResult KILN_CALL report(KilnOwner,KilnStr feature,uint32_t,uint32_t reason,KilnStr) {
    lowered[std::string(feature.ptr, feature.len)] = reason;
    return KILN_OK;
}
KilnCapsApi caps = [] { KilnCapsApi t{}; t.header = {sizeof t, KILN_CAPS_VERSION}; t.report = &report; return t; }();
const void* KILN_CALL service(KilnStr name,uint32_t) {
    const std::string s(name.ptr, name.len);
    return s == KILN_HOOKS_SERVICE ? static_cast<const void*>(&hooks) : s == KILN_CAPS_SERVICE ? static_cast<const void*>(&caps) : nullptr;
}
uint32_t KILN_CALL side() { return KILN_SIDE_CLIENT; }  // a hosting client: its server world and its client world
// review of 31.15: lock_resolve against a symbol table; `code` holds the three calls call_matches checks
alignas(16) uint8_t code[0x40] = {};
std::map<std::string,uintptr_t> symbols;
KilnResult KILN_CALL symbol(KilnStr name,uintptr_t* out) {
    const auto it = symbols.find(std::string(name.ptr, name.len));
    if (it == symbols.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second;
    return KILN_OK;
}
KilnResult KILN_CALL constant(KilnStr name,int64_t* out) {
    const std::string s(name.ptr, name.len);
    *out = s == "locks/lock.item" ? 0x10 : s == "locks/lock.amount" ? 0x16 : s == "locks/lock.mask" ? 0x18 : -1;
    return *out < 0 ? KILN_ERR_NOT_FOUND : KILN_OK;
}
bool KILN_CALL feature(KilnStr name) { return std::string_view(name.ptr, name.len) == "items.lock_sources"; }
void put_call(size_t at, uintptr_t to) {  // an E8 at code + at calling `to`
    const int32_t rel = static_cast<int32_t>(static_cast<intptr_t>(to) - reinterpret_cast<intptr_t>(code + at + 5));
    code[at] = 0xe8;
    std::memcpy(code + at + 1, &rel, 4);
}
uint16_t* __fastcall map_find(void*, const uint32_t*) { return nullptr; }
}
int main() {
    using namespace kitems;
    int checks=0,failed=0;
#define CHECK(x) do {++checks;if(!(x)){++failed;std::printf("FAIL %d %s\n",__LINE__,#x);}}while(0)
    auto logged=[&](const char* s) { for(const auto& l:logs) if(l.find(s)!=std::string::npos) return true; return false; };
    KilnApi api{}; api.size=sizeof api;api.session=&session;api.on_tick_thread=&tick_thread;api.singleton=&singleton;
    api.service=&service;api.side=&side; g_api=&api;
    G.read=G.craft_ok=G.registered_ok=G.outputs_ok=true;G.pay=&pay;
    E.get_component=&component;E.job_storage=&job_storage;H_FACTORY=99;
    g_lock_feat=true;g_lock_row_feat=true;g_lock_current=&current_entity;  // as lock_resolve on a client with both prompt sites
    for (LogLimit* l : {&g_log_lock_check,&g_log_lock_pay,&g_log_lock_stand,&g_log_lock_snap,&g_log_lock_prompt}) l->budget=100000;
    // the bases system: player 1's BasicPlayerInfos record (id 1, login 0, CanAccessInventories)
    uint8_t infos[0xa0*2]={};wr(infos,0,uint32_t{1});infos[0x9d]=2;
    uint8_t bases[0x400]={};wr(bases,0x10,infos);g_srv_bases.store(bases);g_bases_ptr=bases;
    constexpr uint32_t PLAYER=1, CRAFT=2100, LATE=2200, EARLY=2300, NINES=2400, PICK=7, NINE=9;
    auto add=[&](uint32_t container,bool after,uint32_t filter,std::vector<uint32_t> items,uint32_t flags,uint32_t size=sizeof(KilnCraftSourceRequest)) {
        KilnCraftSourceRequest r{size,PLAYER,container,after?1u:0u,filter,static_cast<uint32_t>(items.size()),items.data(),flags,0};
        uint64_t h=0; const KilnResult rc=t_add_source(10,&r,&h); return std::make_pair(rc,h);
    };
    fixture::inventory={{PLAYER,{{PICK,0}}},{CRAFT,{{PICK,9}}},{LATE,{{PICK,2}}},{EARLY,{{PICK,1}}},{NINES,{{PICK,4},{NINE,6}}}};

    // ---- add_source: the Locks flag, an older caller's request, unknown bits ----
    CHECK(add(CRAFT,false,0,{},0).first==KILN_OK);                       // crafting only
    CHECK(add(LATE,true,0,{},KILN_CRAFT_SOURCE_LOCKS).first==KILN_OK);   // locks, after station outputs
    CHECK(add(EARLY,false,0,{},KILN_CRAFT_SOURCE_LOCKS).first==KILN_OK); // locks
    CHECK(add(NINES,false,1,{NINE},KILN_CRAFT_SOURCE_LOCKS).first==KILN_OK); // locks, allow 9 only
    CHECK(add(EARLY,false,0,{},16).first==KILN_ERR_INVALID_ARGUMENT);    // an unknown flag (2 FILLS, 4 BUILDING since #132, 8 RESPEC since #150)
    {
        KilnCraftSourceRequest r{sizeof r,PLAYER,EARLY,0,0,0,nullptr,KILN_CRAFT_SOURCE_LOCKS,1};uint64_t h=0;
        CHECK(t_add_source(10,&r,&h)==KILN_ERR_INVALID_ARGUMENT && !h);   // reserved set
        r.size=KILN_CRAFT_SOURCE_REQUEST_V1;r.reserved=1;r.flags=KILN_CRAFT_SOURCE_LOCKS;  // a 0.2.0-dev.31.9 caller: flags unread
        CHECK(t_add_source(10,&r,&h)==KILN_OK && h);
        const auto all=g_registered.copy(PLAYER);
        CHECK(all.size()==5 && !all.back().locks && all[1].locks && !all[0].locks);
        g_registered.remove(10,h);
        r.size=KILN_CRAFT_SOURCE_REQUEST_V1-4;
        CHECK(t_add_source(10,&r,&h)==KILN_ERR_INVALID_ARGUMENT);
    }
    // ---- source_stock's lock rows: opted-in links only, the filter applied, no bag ----
    {
        KilnStationStack rows[4]{};uint32_t n=0;
        CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_LOCKS,rows,4,&n)==KILN_OK && n==2);
        CHECK(rows[0].item==PICK && rows[0].count==3 && rows[1].item==NINE && rows[1].count==6);  // 9 picks of CRAFT not counted
        CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_LOCKS,rows,1,&n)==KILN_ERR_BUFFER_TOO_SMALL && n==2);
        CHECK(t_source_stock(PLAYER,32,rows,4,&n)==KILN_ERR_INVALID_ARGUMENT); // 4/8 FILLS/BUILDING since #132, 16 RESPEC since #150
    }
    // ---- the server's cache and the server world's lock check (locks/LockCountRet) ----
    lock_cache_refresh(1);
    uint8_t lock[0x100]={};wr(lock,0x10,PICK);wr<uint16_t>(lock,0x16,2);lock[0x18]=1;  // Consume, 2 picks
    alignas(16) uint8_t frame[0x200]={}, stack[0x100]={};
    void* job=reinterpret_cast<void*>(0x1234); void* ctx=&job; wr(stack,0x58,ctx);
    auto eval=[&](uint32_t bag,uint8_t error=0) {
        KilnRegisters r{};r.rbp=reinterpret_cast<uintptr_t>(frame)+0x100;r.rsp=reinterpret_cast<uintptr_t>(stack);r.r14=reinterpret_cast<uintptr_t>(lock);
        uint8_t* out=frame+0x100-0x68;std::memset(out,0,12);out[0]=error;wr(out,8,bag);
        logs.clear();lock_eval_impl(r);return rd<uint32_t>(out,8);
    };
    fixture::world_kind=0;fixture::current=PLAYER;
    CHECK(eval(0)==3 && logged("counts the linked sources: bag 0 + linked 3, needs 2"));  // the bag short: bag + opted-in links
    CHECK(eval(2)==2 && logs.empty());                // the bag covers it: the game's count, no line, no link asked
    CHECK(eval(0,0x15)==0 && logs.empty());            // the game's own refusal (no inventory) stays
    wr<uint16_t>(lock,0x16,4);
    CHECK(eval(0)==0 && logged("refused: short count (bag 0, linked 3, needs 4)"));
    CHECK(eval(1)==4);                                  // bag 1 + linked 3 covers 4
    wr<uint16_t>(lock,0x16,2);
    fixture::current=5;CHECK(eval(0)==0 && logs.empty());  // a player with no link: the game's lock, no line
    // ---- a player whose links are not opted in ----
    {   // player 2 (its own BasicPlayerInfos record) has one link, for crafting only
        wr(infos,0xa0,uint32_t{2});infos[0xa0+0x9d]=2;
        const uint64_t h=g_registered.add(10,2,CRAFT,false,true,{},0,false);
        g_lock_dirty=true;lock_cache_refresh(100);fixture::current=2;
        CHECK(eval(0)==0 && logged("source not opted in"));
        g_registered.remove(10,h);g_lock_dirty=true;lock_cache_refresh(200);fixture::current=PLAYER;
    }
    // ---- a Kiln client's lock check and locked prompt: the snapshot, the asks ----
    fixture::world_kind=2;fixture::current=0;
    CHECK(eval(0)==0 && logged("no snapshot from the server"));
    KilnStationStack snap[2]={{NINE,6},{0,0}};
    CHECK(t_set_lock_stock(1,snap,1)==KILN_OK && logged("the server counts lock items from linked sources: 1 item row(s)"));
    CHECK(eval(0)==0 && logged("no snapshot row for the item yet"));
    KilnItemId asks[KILN_STATIONS_MAX_LOCK_ASKS]{};uint32_t na=0;
    CHECK(t_lock_asks(asks,KILN_STATIONS_MAX_LOCK_ASKS,&na)==KILN_OK && na==1 && asks[0]==PICK);
    CHECK(t_lock_asks(nullptr,0,&na)==KILN_ERR_BUFFER_TOO_SMALL && na==1);
    snap[1]={PICK,3};
    CHECK(t_set_lock_stock(1,snap,2)==KILN_OK && t_lock_asks(asks,4,&na)==KILN_OK && na==0);  // the row answers the ask
    CHECK(eval(0)==3 && logged("the client world's lock check of item 0x00000007 (player 0) counts the linked sources"));
    CHECK(eval(5)==5);
    {   // the locked prompt (client only): raised the same way, never a log line
        static uint8_t ui[0x700]={};  // rbp+0x620: the prompt's count result
        KilnRegisters r{};r.rbp=reinterpret_cast<uintptr_t>(ui);r.rdi=reinterpret_cast<uintptr_t>(lock);
        ui[0x620]=0;wr<uint32_t>(ui,0x628,1);logs.clear();
        hk_lock_ui(r);
        CHECK(rd<uint32_t>(ui,0x628)==4 && logs.empty());
        snap[1]={PICK,0};t_set_lock_stock(1,snap,2);wr<uint32_t>(ui,0x628,1);hk_lock_ui(r);
        CHECK(rd<uint32_t>(ui,0x628)==1);  // a row of 0: the links hold none
    }
    {   // the prompt's requirement line ("Required: Lockpick", the HUD, client only): the HUD's own count of the item in
        // the bag decides the line's colour (met = count >= max(amount, 1)); raised from the snapshot the same way, and the
        // prompt's state logged once per change, not every frame (GitHub #147 follow-up)
        static uint8_t rec[0x100]={};  // r14: the prompt record (player_ui's +0x2bc0): item +0xd8, amount u16 +0xdc
        wr(rec,0xd8,PICK);wr<uint16_t>(rec,0xdc,2);
        constexpr uint64_t HIGH=0xdead00000000ull;  // r8's upper half: left alone unless the count changes
        auto row=[&](uint32_t bag) {
            KilnRegisters r{};r.r14=reinterpret_cast<uintptr_t>(rec);r.r8=HIGH|bag;hk_lock_row(r);return r.r8;
        };
        snap[1]={PICK,3};CHECK(t_set_lock_stock(1,snap,2)==KILN_OK);logs.clear();
        CHECK(row(0)==3 && logged("the lock prompt's requirement line counts the linked sources: item 0x00000007, bag 0 + linked 3, needs 2"));
        logs.clear();CHECK(row(0)==3 && row(1)==4 && logs.empty());  // every frame: no new line while the state holds
        CHECK(row(2)==(HIGH|2) && logs.empty());                      // the bag covers it: the game's count
        wr<uint16_t>(rec,0xdc,0);CHECK(row(0)==3);                     // amount 0: the line still wants 1
        wr<uint16_t>(rec,0xdc,5);logs.clear();
        CHECK(row(1)==(HIGH|1) && logged("the lock prompt's requirement line counts the bag only: item 0x00000007, short count (bag 1, linked 3, needs 5)"));
        wr<uint16_t>(rec,0xdc,2);
        // a prompt shown before its row arrives: the item is asked for, and the frame after the row arrives counts it
        wr(rec,0xd8,uint32_t{11});logs.clear();
        CHECK(row(0)==(HIGH|0) && logged("item 0x0000000b, no snapshot row for the item yet"));
        CHECK(t_lock_asks(asks,KILN_STATIONS_MAX_LOCK_ASKS,&na)==KILN_OK && na==1 && asks[0]==11);
        KilnStationStack three[3]={{NINE,6},{PICK,3},{11,2}};
        CHECK(t_set_lock_stock(1,three,3)==KILN_OK && t_lock_asks(asks,4,&na)==KILN_OK && na==0);
        logs.clear();CHECK(row(0)==2 && logged("counts the linked sources: item 0x0000000b, bag 0 + linked 2, needs 2"));
        wr(rec,0xd8,PICK);
        // no item (a lock without one): untouched
        wr(rec,0xd8,uint32_t{0});CHECK(row(0)==(HIGH|0));wr(rec,0xd8,PICK);
        // a fault in the hook (review of 31.15): the line is display only, so the line alone stands down (the game's count);
        // the press and the locked prompt keep counting the links, and no capability row is lowered (on a hosted world the
        // same flag would have stood the server half's lock check and payment down for every player)
        KilnRegisters bad{};bad.r14=0x10;bad.r8=0;logs.clear();
        hk_lock_row(bad);
        CHECK(g_lock_row_off.load() && !g_lock_off.load() && !g_lock_fault_report.load() &&
              logged("the lock prompt's requirement line hook faulted") && logged("the line keeps the game's count"));
        CHECK(row(0)==(HIGH|0));  // the line: nothing counted
        CHECK(eval(0)==3);        // the press: bag 0 + linked 3, as before the fault
        g_lock_row_off=false;
        snap[1]={PICK,0};t_set_lock_stock(1,snap,2);
    }
    for(uint32_t i=0;i<40;++i){wr(lock,0x10,1000+i);eval(0);}
    CHECK(t_lock_asks(asks,KILN_STATIONS_MAX_LOCK_ASKS,&na)==KILN_OK && na==KILN_STATIONS_MAX_LOCK_ASKS && asks[0]==1000);  // capped, oldest kept
    wr(lock,0x10,PICK);
    CHECK(t_set_lock_stock(0,nullptr,0)==KILN_OK && logged("lock snapshot cleared") && t_lock_asks(asks,4,&na)==KILN_OK && na==0);
    CHECK(eval(0)==0);  // off: the game's own count
    CHECK(t_set_lock_stock(2,nullptr,0)==KILN_ERR_INVALID_ARGUMENT && t_set_lock_stock(1,nullptr,1)==KILN_ERR_INVALID_ARGUMENT);
    KilnStationStack zero{0,1};
    CHECK(t_set_lock_stock(1,&zero,1)==KILN_ERR_INVALID_ARGUMENT);
    CHECK(t_set_lock_stock(1,snap,KILN_STATIONS_MAX_LOCK_STOCK+1)==KILN_ERR_INVALID_ARGUMENT);

    // ---- the payment: interaction_attempt's PayMaterial returned; the hook pays the rest (locks/LockPaid) ----
    fixture::world_kind=0;
    alignas(16) static uint8_t pframe[0x400]={}, pstack[0x100]={};
    uint32_t guest=PLAYER;
    auto paid=[&](uint32_t bag_left,uint8_t error=0) {
        KilnRegisters r{};r.rsp=reinterpret_cast<uintptr_t>(pstack);r.rbp=reinterpret_cast<uintptr_t>(pframe);
        fixture::expected_tx=pframe+0x230;  // rbp+0x230: interaction_attempt's transaction
        r.r14=reinterpret_cast<uintptr_t>(&guest);r.rsi=reinterpret_cast<uintptr_t>(lock);
        uint8_t* out=pstack+0x58;std::memset(out,0,12);out[0]=error;wr(out,8,bag_left);
        logs.clear();fixture::taken.clear();fixture::wrong_tx=false;
        lock_paid_impl(r);return rd<uint32_t>(out,8);
    };
    auto reset=[&]{fixture::inventory={{PLAYER,{{PICK,0}}},{CRAFT,{{PICK,9}}},{LATE,{{PICK,2}}},{EARLY,{{PICK,1}}},{NINES,{{PICK,4},{NINE,6}}}};fixture::tx_view.clear();};
    // (a) the bag paid everything: nothing asked
    reset();CHECK(paid(0)==0 && fixture::taken.empty() && logs.empty());
    // (b) the bag paid 1 of 2 (the game's own PayMaterial ran first): 1 more from the first opted-in link in link order
    // (EARLY before LATE: BeforeStationOutputs links first), never the crafting-only link, never NINES (filter)
    reset();CHECK(paid(1)==0 && fixture::taken==(std::vector<std::pair<uint32_t,uint32_t>>{{EARLY,1}}) && !fixture::wrong_tx);
    CHECK(logged("the bag paid 1, 1 from 1 linked source(s)"));
    // (c) the bag paid nothing: 2 = EARLY 1 + LATE 1, in that order
    reset();CHECK(paid(2)==0 && fixture::taken==(std::vector<std::pair<uint32_t,uint32_t>>{{EARLY,1},{LATE,1}}));
    CHECK(fixture::inventory[EARLY][PICK]==0 && fixture::inventory[LATE][PICK]==1 && fixture::inventory[CRAFT][PICK]==9);
    // (d) all or nothing: 4 needed, the opted-in links hold 3: no take, the game's answer (left 4) stays, the line says why
    wr<uint16_t>(lock,0x16,4);
    reset();CHECK(paid(4)==4 && fixture::taken.empty() && logged("short count (the links hold 3, needs 4 more)"));
    wr<uint16_t>(lock,0x16,2);
    // (e) no Consume: the same takes in the transaction (the game rolls the presence test back), the line says so
    lock[0x18]=0;reset();CHECK(paid(2)==0 && logged("no Consume: a presence test"));lock[0x18]=1;
    // (f) a stack the server no longer sees: the cache counted 3, the transaction's view of EARLY and LATE shows none
    reset();g_lock_dirty=true;lock_cache_refresh(300);fixture::tx_view={{EARLY,{{PICK,0}}},{LATE,{{PICK,0}}}};
    CHECK(paid(2)==2 && fixture::taken.empty() && logged("a stack the server no longer sees"));
    // (g) a refused take: nothing more taken, left > 0 (the game declines and rolls back), a warning names the container
    reset();fixture::refuse=EARLY;CHECK(paid(2)==2 && logged("container 2300's take was refused"));fixture::refuse=0;
    // (h) a faulting take: left > 0
    reset();fixture::fail_take=true;CHECK(paid(2)==2 && logged("faulted"));fixture::fail_take=false;
    // (i) the bag's payment answered an error: left to the game with a line
    reset();CHECK(paid(2,0x0b)==2 && fixture::taken.empty() && logged("answered error 0x0b"));
    // (j) not the server world's transaction (a client world's): nothing taken
    reset();fixture::server_storage=false;CHECK(paid(2)==2 && fixture::taken.empty() && logged("not the server world's transaction"));
    fixture::server_storage=true;
    // (k) the filter: NINE is allowed on NINES only
    wr(lock,0x10,NINE);reset();CHECK(paid(2)==0 && fixture::taken==(std::vector<std::pair<uint32_t,uint32_t>>{{NINES,2}}));
    wr(lock,0x10,PICK);
    // (l) another login of the slot: the links no longer pay
    wr(infos,4,uint32_t{99});reset();CHECK(paid(2)==2 && fixture::taken.empty() && logged("earlier login"));wr(infos,4,uint32_t{0});
    // (m) a player without links: the game's, no line
    guest=3;reset();CHECK(paid(2)==2 && logs.empty());guest=PLAYER;
    // (n) the permission: CanAccessInventories gone, no link pays
    infos[0x9d]=0;reset();CHECK(paid(2)==2 && fixture::taken.empty());infos[0x9d]=2;
    // (o) a stand-down (a foreign patch found, a hook fault): the game's own lock everywhere
    g_lock_off=true;reset();CHECK(paid(2)==2 && fixture::taken.empty() && logged("are off here"));
    CHECK(eval(0)==0);g_lock_off=false;
    // ---- the world closes: the cache goes ----
    stations_world_closed();
    {
        uint64_t held=0;bool opted=false;
        CHECK(!lock_cached(PLAYER,PICK,&held,&opted));
    }

    // ---- a hook that faults at run time: the feature stands down and its capability row says faulted (the tick
    // thread lowers it: a hook runs on a game worker thread), so the server stops telling clients it counts links ----
    {
        lowered.clear();g_lock_off=false;
        KilnRegisters r{};r.rbp=reinterpret_cast<uintptr_t>(frame)+0x100;r.rsp=reinterpret_cast<uintptr_t>(stack);
        r.r14=0x10;  // an unreadable InteractionLock
        uint8_t* out=frame+0x100-0x68;std::memset(out,0,12);
        hk_lock_eval(r);
        CHECK(g_lock_off.load() && logged("the lock check's hook faulted"));
        g_registered.add(10,PLAYER,EARLY,false,true,{},0,true);
        lock_server_pre(400);
        CHECK(lowered.count("items.lock_sources") && lowered["items.lock_sources"]==KILN_CAP_REASON_FAULTED);
        lowered.clear();lock_server_pre(401);
        CHECK(lowered.empty());  // once
        g_lock_off=false;g_registered.clear();
    }
    // ---- a hosting client: its server world (the tick thread) and its client world (the main thread) both put the
    // shared lock-check hook in on demand; one link goes in, whichever world gets there first ----
    {
        g_lock={0x140b0a97,0x140a536e,0x1402bc483,0x14093a2a0,0x1401b0d60,0x140bf73be};
        g_lock_eval_on=g_lock_paid_on=g_lock_ui_on=g_lock_row_on=false;
        g_registered.add(10,PLAYER,EARLY,false,true,{},0,true);
        KilnStationStack on[1]={{PICK,1}};
        CHECK(t_set_lock_stock(1,on,1)==KILN_OK);
        std::thread client_world;
        mid2_during=[&](uintptr_t target) {
            if (target!=g_lock.count_ret || client_world.joinable()) return;
            client_world=std::thread([] { lock_client_pre(); });  // the client world's Pre, while this link goes in
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        };
        lock_server_pre(500);
        if (client_world.joinable()) client_world.join();
        mid2_during=nullptr;
        CHECK(mid2_links[g_lock.count_ret]==1 && mid2_links[g_lock.paid]==1 && mid2_links[g_lock.ui_ret]==1 && mid2_links[g_lock.row]==1);
        CHECK(g_lock_eval_on && g_lock_paid_on && g_lock_ui_on && g_lock_row_on);
        CHECK(logged("this client's lock checks count the bag plus linked sources (the press, the locked prompt and its requirement line)"));
        t_set_lock_stock(0,nullptr,0);g_registered.clear();
    }
    // ---- review of 31.15: the requirement line is display only. A hosting client is a client process, so whatever
    // switches items.lock_sources off there also switches off its server half's lock check and payment for every player of
    // the world. A client without the line's site (a build where only that pattern broke), with another module's patch on
    // it, or whose hook cannot go in keeps the rest: only the line keeps the game's count, logged, and no row is lowered ----
    {
        const uintptr_t C = reinterpret_cast<uintptr_t>(code), count_fn = C + 0x30, pay_fn = C + 0x38;
        put_call(0x00, count_fn); put_call(0x10, pay_fn); put_call(0x20, count_fn);  // LockCountRet, LockPaid, UiLockCountRet
        symbols = {{"locks/LockCountRet", C + 5}, {"locks/InventoryItemCount", count_fn}, {"locks/LockPaid", C + 0x15},
                   {"gems/PayMaterial", pay_fn}, {"world/CurrentEntityId", reinterpret_cast<uintptr_t>(&current_entity)},
                   {"locks/client.UiLockCountRet", C + 0x25}, {"locks/client.LockRowCount", 0x140bf73be}};
        api.symbol = &symbol; api.feature = &feature; api.constant = &constant; E.component_map_find = &map_find;
        lowered.clear(); logs.clear(); g_lock = {};
        lock_resolve();
        CHECK(g_lock_feat && g_lock_row_feat && g_lock.ui_ret == C + 0x25 && g_lock.row == 0x140bf73be && lowered.empty());
        symbols.erase("locks/client.LockRowCount"); g_lock = {};
        lock_resolve();
        CHECK(g_lock_feat && !g_lock_row_feat && !g_lock.row && lowered.empty() &&
              logged("the lock prompt's requirement line keeps the game's count (the bag only): locks/client.LockRowCount did not resolve"));
        symbols.erase("locks/client.UiLockCountRet"); g_lock = {}; lowered.clear();
        lock_resolve();  // the locked prompt's site stays required, as since 31.12
        CHECK(!g_lock_feat && !g_lock_row_feat && lowered["items.lock_sources"] == KILN_CAP_REASON_SYMBOL_MISSING);
        api.symbol = nullptr; api.feature = nullptr; api.constant = nullptr; E.component_map_find = nullptr;

        g_lock = {0x140b0a97,0x140a536e,0x1402bc483,0x14093a2a0,0x1401b0d60,0x140bf73be};
        KilnStationStack on[1] = {{PICK,1}};
        auto client = [&](bool row_feat) {  // a fresh client process's first client-world step with the snapshot on
            g_lock_feat = true; g_lock_row_feat = row_feat; g_lock_off = false; g_lock_row_off = false;
            g_lock_eval_on = g_lock_paid_on = g_lock_ui_on = g_lock_row_on = false;
            mid2_links.clear(); lowered.clear();
            CHECK(t_set_lock_stock(1,on,1) == KILN_OK);
            logs.clear(); lock_client_pre();
        };
        auto rest_on = [&] {  // the press and the locked prompt count the links; nothing stood down, nothing lowered
            return g_lock_eval_on && g_lock_ui_on && mid2_links[g_lock.count_ret] == 1 && mid2_links[g_lock.ui_ret] == 1 &&
                   !g_lock_off && lowered.empty() && logged("this client's lock checks count the bag plus linked sources (the press, "
                                                            "the locked prompt; the requirement line keeps the game's count)");
        };
        // another module patched the HUD's count: the line stands down alone
        foreign = {g_lock.row - 0x10};
        client(true);
        CHECK(rest_on() && !g_lock_row_on && g_lock_row_off && !mid2_links.count(g_lock.row));
        CHECK(logged("the lock prompt's requirement line stands down: the lock prompt's requirement line count code state FOREIGN"));
        foreign.clear();
        // its hook cannot go in: the same
        mid2_refuse = g_lock.row;
        client(true);
        CHECK(rest_on() && !g_lock_row_on && g_lock_row_off && logged("locks/client.LockRowCount could not be hooked"));
        mid2_refuse = 0;
        // its site did not resolve: not tried
        client(false);
        CHECK(rest_on() && !g_lock_row_on && !g_lock_row_off && !mid2_links.count(g_lock.row));
        logs.clear(); lock_client_pre();
        CHECK(logs.empty() && mid2_links[g_lock.count_ret] == 1);  // done: the next steps return at once
        // another module patched the locked prompt's count (required since 31.12): everything stands down, as before
        foreign = {g_lock.ui_ret - 0x10};
        client(true);
        CHECK(g_lock_off && !g_lock_eval_on && lowered["items.lock_sources"] == KILN_CAP_REASON_FAULTED);
        foreign.clear();
        g_lock_feat = g_lock_row_feat = true; g_lock_off = false; g_lock_row_off = false;
        t_set_lock_stock(0,nullptr,0);
    }
    std::printf("lock sources: %d checks, %d failed\n",checks,failed);return failed?1:0;
}
