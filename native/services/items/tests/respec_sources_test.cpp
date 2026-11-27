// SPDX-License-Identifier: MIT
// GitHub #150: the skill tree's respec (a node's unlearn, the paid reset) from the player's links registered with
// KILN_CRAFT_SOURCE_RESPEC, on the real kiln-items glue: registration and source_stock's respec row, the payment adapter's
// respec branch on fake stock-mode-2 transactions (the respec payer's InventoryPay call, its return respec/RespecPaid),
// the server half's code check, a Kiln client's snapshot and the unlearn / reskill / item-row mid hooks (fake registers),
// resolve against fake symbols, and the stand-downs. No game: what the game sends and draws is the real-game check.
#include "../stations.cpp"
#include <cstdarg>
#include <map>
#include <string>
namespace fixture {
std::map<uint32_t,std::map<uint32_t,uint32_t>> inventory;
bool server_storage = true, fail_take = false;
uint8_t stock_error = 0x1f;
std::vector<uint32_t> paid, calls, stock_list;
void* bases = nullptr;
bool in_base = false;
void* expected_tx = nullptr;
bool wrong_tx = false;
uint32_t refuse = 0;
uint32_t lab_character = 0;                                   // a spawned player character (labs only)
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
void items_log(int,const char* fmt,...) {
    char b[1024]; va_list a; va_start(a,fmt); std::vsnprintf(b,sizeof b,fmt,a); va_end(a); logs.emplace_back(b);
}
uint64_t current_step() { return 1; }
const KilnApi* items_api() { return g_api; }
KilnOwner items_owner() { return 10; }
// the respec payer's transaction has no Fuel accessor (S 0xb9230 leaves setup +0x70 zero): only the reads check out
bool access_from_tx(void*,Access* out) { *out = {}; return false; }
bool access_from_tx_reads(void*,Access* out) { *out = {}; return fixture::server_storage; }
bool setup_flags(const Access&,KilnEntity e,uint8_t* flags) { *flags = e <= 1023 ? 1 : 0; return fixture::inventory.count(e) != 0; }
bool read_pages(const Access&,KilnEntity e,PageSet* out,bool* initialized) {
    *out = {}; *initialized = fixture::inventory.count(e) != 0;
    if (!*initialized) return false;
    out->n = 1; out->p[0].entity = e; size_t i=0;
    for (auto [item,count] : fixture::inventory[e]) { if(i==8)break; out->p[0].s[i++] = {0,item,count,0}; }
    return true;
}
void player_guard(const Access&,KilnEntity e,PlayerGuard* out) { *out = {}; out->is_player = e <= 1023 || e == fixture::lab_character; }
bool inventory_matches(uintptr_t,KilnEntity e,uint32_t,bool* match,unsigned long*) { *match = e > 1023 && fixture::inventory.count(e); return true; }
}
namespace {
using namespace kitems;
BaseHit* __fastcall base_at(BaseHit* hit,void*,const float*) { *hit={}; hit->error=fixture::in_base?0:0x1f;return hit; }
void __fastcall base_stock(StockErr* err,StockLists* stock,uint64_t,uint64_t,void*,uint32_t) {
    *err={};err->error=fixture::stock_error;*stock={};
    if(!err->error){stock->items=fixture::stock_list.data();stock->nitems=fixture::stock_list.size();}
}
PayOut* __fastcall pay(PayOut* out,void* tx,uint32_t entity,uint32_t item,uint32_t n) {
    if(tx!=fixture::expected_tx)fixture::wrong_tx=true;
    fixture::calls.push_back(entity);
    if(fixture::refuse && entity==fixture::refuse) {*out={};out->error=0x0b;return out;}
    if(fixture::fail_take && entity>1023) RaiseException(0xe1230001,0,0,nullptr);
    auto& count=fixture::inventory[entity][item]; const auto take=std::min(n,count); count-=take;
    if(take)fixture::paid.push_back(entity);
    *out={};out->left=n-take;return out;
}
PtrSize* __fastcall component(PtrSize* out,uintptr_t,uint32_t,uint32_t) { *out={}; return out; }
bool KILN_CALL tick_thread() {return true;}
uintptr_t KILN_CALL singleton(uintptr_t,KilnStr name) {
    return std::string_view(name.ptr,name.len)=="PlayerBasesSystemServer"?reinterpret_cast<uintptr_t>(fixture::bases):0;
}
KilnResult KILN_CALL session(KilnSessionInfo* info) {info->state=KILN_SESSION_DEDICATED;return KILN_OK;}
KilnResult KILN_CALL position(KilnEntity e,KilnVec3* out) {*out={};return e==1 && fixture::in_base ? KILN_OK : KILN_ERR_NOT_FOUND;}
// kiln.hooks and kiln.caps stand-ins: mid2 records its targets, code_state answers FOREIGN inside `foreign`
std::map<uintptr_t,int> mid2_links;
std::vector<uintptr_t> foreign;
KilnResult KILN_CALL mid2(KilnOwner,uintptr_t target,const KilnHookOrder*,KilnMidFn,void*,KilnHandle* h) { ++mid2_links[target]; *h = 100 + target; return KILN_OK; }
KilnResult KILN_CALL revoke(KilnHandle) { return KILN_OK; }
KilnResult KILN_CALL code_state(uintptr_t at,size_t n,uint32_t* state,uint64_t* generation) {
    *state=KILN_CODE_ORIGINAL; *generation=0;
    for (uintptr_t f : foreign) if (f >= at && f - at < n) *state = KILN_CODE_FOREIGN;
    return KILN_OK;
}
KilnHooksApi hooks = [] { KilnHooksApi t{}; t.header = {sizeof t, KILN_HOOKS_VERSION}; t.mid2 = &mid2; t.revoke = &revoke;
                          t.code_state = &code_state; return t; }();
std::map<std::string,uint32_t> lowered;
KilnResult KILN_CALL report(KilnOwner,KilnStr feature,uint32_t,uint32_t reason,KilnStr) { lowered[std::string(feature.ptr, feature.len)] = reason; return KILN_OK; }
KilnCapsApi caps = [] { KilnCapsApi t{}; t.header = {sizeof t, KILN_CAPS_VERSION}; t.report = &report; return t; }();
const void* KILN_CALL service(KilnStr name,uint32_t) {
    const std::string s(name.ptr, name.len);
    return s == KILN_HOOKS_SERVICE ? static_cast<const void*>(&hooks) : s == KILN_CAPS_SERVICE ? static_cast<const void*>(&caps) : nullptr;
}
uint32_t side_now = KILN_SIDE_SERVER;
uint32_t KILN_CALL side() { return side_now; }
// sigdb stand-ins: symbols and constants by name; the feature crafting.respec_sources on or off
std::map<std::string, uintptr_t> symbols;
std::map<std::string, int64_t> constants;
bool respec_feature = true;
KilnResult KILN_CALL symbol(KilnStr name, uintptr_t* out) {
    auto it = symbols.find(std::string(name.ptr, name.len));
    if (it == symbols.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second; return KILN_OK;
}
KilnResult KILN_CALL constant(KilnStr name, int64_t* out) {
    auto it = constants.find(std::string(name.ptr, name.len));
    if (it == constants.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second; return KILN_OK;
}
bool KILN_CALL feature(KilnStr name) { return respec_feature && std::string_view(name.ptr, name.len) == "crafting.respec_sources"; }
// a code page with direct calls: put_call(at, to) writes an E8 whose return is code + at + 5
alignas(16) uint8_t code[0x400] = {};
uintptr_t put_call(size_t at, uintptr_t to) {
    const auto ret = reinterpret_cast<intptr_t>(code + at + 5);
    const int32_t rel = static_cast<int32_t>(static_cast<intptr_t>(to) - ret);
    code[at] = 0xe8; std::memcpy(code + at + 1, &rel, 4);
    return static_cast<uintptr_t>(ret);
}
}
int main() {
    using namespace kitems;
    int checks=0,failed=0;
#define CHECK(x) do {++checks;if(!(x)){++failed;std::printf("FAIL %d %s\n",__LINE__,#x);}}while(0)
    auto logged=[&](const char* s) { for(const auto& l:logs) if(l.find(s)!=std::string::npos) return true; return false; };
    KilnApi api{}; api.size=sizeof api;api.session=&session;api.on_tick_thread=&tick_thread;api.singleton=&singleton;
    api.service=&service;api.side=&side;api.symbol=&symbol;api.constant=&constant;api.feature=&feature; g_api=&api;
    G.read=G.craft_ok=G.registered_ok=G.outputs_ok=true;G.base_at=&base_at;G.base_stock=&base_stock;G.pay=&pay;G.recipe_paid=0x1234;
    E.get_component=&component;H_FACTORY=99;
    for (LogLimit* l : {&g_log_respec_pay,&g_log_respec_world,&g_log_respec_snap,&g_log_respec_check,&g_log_left,&g_log_paid,
                        &g_log_refused,&g_log_tx,&g_log_dry}) l->budget=100000;
    uint8_t infos[0xa0*2]={};wr(infos,0,uint32_t{1});infos[0x9d]=2;
    uint8_t bases[0x400]={};wr(bases,0x10,infos);g_srv_bases.store(bases);fixture::bases=bases;
    constexpr uint32_t PLAYER=1, RUNE=0x41a792dc, CHEST=2100, LINK=2000, CRAFT_ONLY=2200, OTHER=0x77;
    KilnWorldApi world{};world.position=&position;g_w=&world;
    G.stock_items=0x20;G.stock_items_count=0x28;G.stock_water=0x30;G.stock_water_count=0x38;G.aabb_y=0x80;G.zone_mask_rel=0x20;

    // ---- resolve: every symbol, each call checked; a client adds the two checks and the display row ----
    const uintptr_t pay_fn = reinterpret_cast<uintptr_t>(code + 0x300), avail = reinterpret_cast<uintptr_t>(code + 0x380);
    const uintptr_t payer = reinterpret_cast<uintptr_t>(code + 0x10);
    const uintptr_t paid = put_call(0x60, pay_fn);
    const uintptr_t node = put_call(0x100, avail), altar = put_call(0x140, avail), header = put_call(0x180, avail);
    symbols = {{"respec/Payer", payer}, {"respec/RespecPaid", paid}, {"rules/InventoryPay", pay_fn},
               {"respec/client.NodeCountRet", node}, {"respec/client.AltarCountRet", altar},
               {"respec/client.HeaderCountRet", header}, {"stations/client.UiAvailableCount", avail}};
    constants = {{"respec/item", RUNE}, {"respec/client.ui.item", 0x44b238}, {"respec/client.ui.cost", 0x44b23c}};
    respec_resolve();
    CHECK(g_respec_feat && g_respec.item == RUNE && g_respec.paid == paid && !g_respec.node_ret && lowered.empty());  // a server
    respec_feature=false;g_respec_feat=false;respec_resolve();CHECK(!g_respec_feat && lowered.empty());  // feature off: nothing
    respec_feature=true;
    symbols["respec/RespecPaid"]=paid+1;respec_resolve();  // not the return of a call to InventoryPay
    CHECK(!g_respec_feat && g_respec.paid==0 && lowered["crafting.respec_sources"]==KILN_CAP_REASON_SYMBOL_MISSING);
    symbols["respec/RespecPaid"]=paid;lowered.clear();
    side_now=KILN_SIDE_CLIENT;
    symbols.erase("respec/client.HeaderCountRet");respec_resolve();
    CHECK(g_respec_feat && g_respec.node_ret==node && g_respec.altar_ret==altar && !g_respec_row_feat && lowered.empty() &&
          logged("the respec tree's item row keeps the game's count"));  // display only: the feature stays
    symbols["respec/client.HeaderCountRet"]=header;
    symbols["respec/client.AltarCountRet"]=altar-1;respec_resolve();
    CHECK(!g_respec_feat && lowered["crafting.respec_sources"]==KILN_CAP_REASON_SYMBOL_MISSING);  // a check's call did not check out
    symbols["respec/client.AltarCountRet"]=altar;lowered.clear();respec_resolve();
    CHECK(g_respec_feat && g_respec_row_feat && g_respec.ui_item==0x44b238 && g_respec.ui_cost==0x44b23c);
    side_now=KILN_SIDE_SERVER;

    // ---- registration and source_stock's respec row ----
    auto add=[&](uint32_t container,uint32_t flags,uint32_t filter=0,std::vector<uint32_t> items={}) {
        KilnCraftSourceRequest r{sizeof r,PLAYER,container,0,filter,static_cast<uint32_t>(items.size()),items.data(),flags,0};
        uint64_t h=0; return t_add_source(10,&r,&h)==KILN_OK ? h : 0;
    };
    fixture::inventory={{PLAYER,{{RUNE,2}}},{CHEST,{{RUNE,3}}},{LINK,{{RUNE,20},{OTHER,4}}},{CRAFT_ONLY,{{RUNE,50}}}};
    {KilnCraftSourceRequest bad{sizeof bad,PLAYER,LINK,0,0,0,nullptr,16,0};uint64_t h=0;
     CHECK(t_add_source(10,&bad,&h)==KILN_ERR_INVALID_ARGUMENT && !h);}  // the next unknown bit
    const uint64_t craft_only=add(CRAFT_ONLY,0), link=add(LINK,KILN_CRAFT_SOURCE_RESPEC);
    CHECK(craft_only && link && g_registered.any_respec());
    KilnStationStack rows[4]={};uint32_t n=0;
    CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_RESPEC,rows,4,&n)==KILN_OK && n==1 && rows[0].item==RUNE && rows[0].count==20);
    CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_RESPEC,rows,0,&n)==KILN_ERR_BUFFER_TOO_SMALL && n==1);
    CHECK(t_source_stock(PLAYER,uint32_t{KILN_CRAFT_SOURCE_STOCK_RESPEC}|KILN_CRAFT_SOURCE_STOCK_BUILDING,rows,4,&n)==KILN_ERR_INVALID_ARGUMENT);
    CHECK(t_source_stock(PLAYER,32,rows,4,&n)==KILN_ERR_INVALID_ARGUMENT);
    // a link to a Magic chest of the player's base counts with the bag, not as a link
    fixture::in_base=true;uint32_t base_chest=LINK;wr(bases,0,uint32_t{1});wr(bases,0x20,&base_chest);wr(bases,0x28,uint64_t{1});
    CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_RESPEC,rows,4,&n)==KILN_OK && n==1 && rows[0].item==RUNE && rows[0].count==0);
    fixture::in_base=false;wr(bases,0,uint32_t{0});
    infos[0x9d]=0;  // no CanAccessInventories: the links hold nothing for this player
    CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_RESPEC,rows,4,&n)==KILN_OK && n==1 && rows[0].count==0);
    infos[0x9d]=2;
    g_respec_feat=false;CHECK(t_source_stock(PLAYER,KILN_CRAFT_SOURCE_STOCK_RESPEC,rows,4,&n)==KILN_ERR_UNAVAILABLE);g_respec_feat=true;

    // ---- the payment: the respec payer's InventoryPay call (ret = respec/RespecPaid), stock mode 2, one Rune line ----
    uint8_t tx[0x100]={};wr(tx,0x70,bases);tx[0xb1]=2;fixture::expected_tx=tx;
    uint8_t line[24]={};wr(line,0x10,RUNE);
    struct {void* ptr;uint64_t n;} costs{line,1};
    fixture::stock_error=0;fixture::stock_list={CHEST};
    auto respec=[&](uint32_t cost,uint32_t bag,uint32_t chest,uint32_t linked,uint32_t* rc,uintptr_t ret=0) {
        wr(line,0x14,cost);
        fixture::inventory={{PLAYER,{{RUNE,bag}}},{CHEST,{{RUNE,chest}}},{LINK,{{RUNE,linked}}},{CRAFT_ONLY,{{RUNE,50}}}};
        fixture::paid.clear();fixture::calls.clear();logs.clear();*rc=99;
        return craft_pay_impl(tx,&costs,PLAYER,1,nullptr,ret?ret:g_respec.paid,rc);
    };
    uint32_t rc=0;
    // before the server half's code check ran: the game's (bag and Magic chests), and kiln.log says so
    CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty() && logged("respec: player 1's skill respec left to the game: the server half's code check has not run yet"));
    // the server's Pre: a link with Respec exists and the code checks out -> ready
    respec_server_pre();CHECK(g_respec_ready.load() && logged("a link opted in with Respec"));
    // every call of the respec payer is counted (kiln_stations_craft_stats' 14th value), whatever came of it
    {uint32_t st[14]={};CHECK(kiln_stations_craft_stats(st,14)==14);const uint32_t before=st[13];
     respec(2,2,0,20,&rc);respec(2,2,0,20,&rc,0x1234);kiln_stations_craft_stats(st,14);CHECK(st[13]==before+1);}
    // (a) the bag covers it: the game's own payment, no take by Kiln, nothing from the links
    CHECK(respec(2,2,0,20,&rc)==0 && fixture::calls.empty() && fixture::inventory[LINK][RUNE]==20);
    // ... the bag and the Magic chest together: still the game's
    CHECK(respec(5,2,3,20,&rc)==0 && fixture::calls.empty());
    // (b) short in the bag and the chest: the bag, then the chest, then the link for exactly the rest, in the transaction
    CHECK(respec(10,2,3,20,&rc)==1 && rc==0 && fixture::calls==std::vector<uint32_t>({PLAYER,CHEST,LINK}) && !fixture::wrong_tx);
    CHECK(fixture::inventory[PLAYER][RUNE]==0 && fixture::inventory[CHEST][RUNE]==0 && fixture::inventory[LINK][RUNE]==15);
    CHECK(logged("respec: a skill respec paid 5 from additional crafting sources"));
    CHECK(fixture::inventory[CRAFT_ONLY][RUNE]==50);  // a crafting-only link never pays a respec
    // (c) Runes only in the linked chest
    CHECK(respec(7,0,0,20,&rc)==1 && rc==0 && fixture::inventory[LINK][RUNE]==13);
    // (d) short even with the link: nothing taken, the game refuses (the unlearn puts the node back), one line says so
    CHECK(respec(30,2,3,20,&rc)==0 && fixture::calls.empty() && logged("respec: a skill respec left to the game: the bag, the Magic chests and the respec links cannot cover it (short by 5 of item 0x41a792dc"));
    // (e) a take that faults: 4, the payer rolls the whole transaction back (the bag's take included)
    fixture::fail_take=true;
    CHECK(respec(10,2,3,20,&rc)==1 && rc==4 && logged("respec: a skill respec paid from additional sources rolled back"));
    fixture::fail_take=false;
    // (f) a respec link without the player's right, and a link of another login: the game's
    infos[0x9d]=0;CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty());infos[0x9d]=2;
    wr(infos,4,uint32_t{99});CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty());wr(infos,4,uint32_t{0});
    // (g) another world's payer (a hosting client's prediction world: its own bases): the game's own, one line
    {uint8_t other[0x400]={};wr(tx,0x70,other);
     CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty() && logged("a skill respec in another world"));
     wr(tx,0x70,bases);}
    // (h) the server world's transaction whose reads do not check out
    fixture::server_storage=false;CHECK(respec(10,2,3,20,&rc)==0 && logged("respec: the server world's skill respec transaction did not check out"));
    fixture::server_storage=true;
    // (i) another payer's call is never the respec branch: a craft's (RecipePaid) needs every accessor of its transaction
    // (this fixture's have no Fuel: a craft line, not a respec one), any other return address is the game's
    CHECK(respec(10,2,3,20,&rc,0x1234)==0 && fixture::calls.empty() && logged("stations: the server world's craft transaction did not check out"));
    CHECK(respec(10,2,3,20,&rc,0x9999)==0 && fixture::calls.empty() && !logged("respec:"));
    // (j) the respec link removed: the game's again, no line about links
    g_registered.remove(10,link);
    CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty() && !logged("respec:"));
    const uint64_t link2=add(LINK,KILN_CRAFT_SOURCE_RESPEC);CHECK(link2);
    // (k) a stand-down: left to the game with a line
    g_respec_off.store(true);
    CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty() && logged("respec from linked sources stood down"));
    g_respec_off.store(false);
    // ---- the free reset: the game's join check never calls the payer (pay 0, docs/re/respec.md); cost 0 never reaches
    // InventoryPay either (the payer answers paid at once). Kiln's branch has no cost-0 line to pay: ----
    CHECK(respec(0,0,0,0,&rc)==0 && fixture::calls.empty());
    // ---- the server half's code check: another module's code on the payment call stands it down ----
    g_respec_ready.store(false);foreign={g_respec.paid-3};logs.clear();
    respec_server_pre();
    CHECK(!g_respec_ready.load() && g_respec_off.load() && lowered["crafting.respec_sources"]==KILN_CAP_REASON_FAULTED &&
          logged("respec: the skill respec payment from linked sources stands down"));
    CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty());
    foreign.clear();g_respec_off.store(false);lowered.clear();respec_server_pre();CHECK(g_respec_ready.load());

    // ---- a Kiln client: the snapshot and the unlearn / reskill / item-row hooks ----
    side_now=KILN_SIDE_CLIENT;respec_resolve();CHECK(g_respec_feat && g_respec_row_feat);
    KilnStationStack snap[2]={{RUNE,20},{OTHER,1}};
    CHECK(t_set_respec_stock(2,nullptr,0)==KILN_ERR_INVALID_ARGUMENT && t_set_respec_stock(1,nullptr,1)==KILN_ERR_INVALID_ARGUMENT);
    {KilnStationStack zero[1]={{0,1}};CHECK(t_set_respec_stock(1,zero,1)==KILN_ERR_INVALID_ARGUMENT);}
    CHECK(t_set_respec_stock(1,snap,KILN_STATIONS_MAX_RESPEC_STOCK+1)==KILN_ERR_INVALID_ARGUMENT);
    respec_client_pre();CHECK(mid2_links.empty());  // no snapshot yet: nothing hooked (a vanilla server never sends one)
    logs.clear();
    CHECK(t_set_respec_stock(1,snap,2)==KILN_OK && logged("respec: the server counts the respec item from linked sources: 2 item row(s)"));
    respec_client_pre();
    CHECK(mid2_links[node]==1 && mid2_links[altar]==1 && mid2_links[header]==1 && logged("this client's unlearn and reskill checks count"));
    respec_client_pre();CHECK(mid2_links[node]==1);  // once
    static uint8_t ui[0x44b240]={};wr(ui,0x44b238,RUNE);
    auto node_check=[&](uint32_t game,uint16_t cost) { KilnRegisters r{};r.rax=game;r.r14=0xabcd0000u|cost;r.r12=reinterpret_cast<uintptr_t>(ui);
                                                      logs.clear();hk_respec_node(r);return static_cast<uint32_t>(r.rax); };
    auto altar_check=[&](uint32_t game,uint32_t cost) { wr(ui,0x44b23c,cost);KilnRegisters r{};r.rax=game;r.rdi=reinterpret_cast<uintptr_t>(ui);
                                                       logs.clear();hk_respec_altar(r);return static_cast<uint32_t>(r.rax); };
    // the unlearn: the game's count (bag mirror + published chests) 2, the node costs 5: 2 + 20 linked, offered
    CHECK(node_check(2,5)==22 && logged("respec: the skill tree's unlearn check counts the linked sources: item 0x41a792dc, the game's count 2 + linked 20, needs 5 (offered)"));
    CHECK(node_check(2,5)==22 && logs.empty());   // once per change (the UI runs every frame)
    CHECK(node_check(2,30)==22 && logged("(short: not offered)"));
    CHECK(node_check(9,5)==29 && logs.empty());   // the game's count covers it: counted, nothing to say
    // the altar: the points spent 12
    CHECK(altar_check(0,12)==20 && logged("the Flame Altar's reskill check counts the linked sources: item 0x41a792dc, the game's count 0 + linked 20, needs 12 (offered)"));
    // another item at the UI's slot (a mod changed it): no row, the game's count, one line
    wr(ui,0x44b238,uint32_t{0x99});CHECK(altar_check(3,12)==3 && logged("no snapshot row for the item"));wr(ui,0x44b238,RUNE);
    // the item row (display only)
    {KilnRegisters r{};r.rax=4;r.rbx=reinterpret_cast<uintptr_t>(ui);hk_respec_row(r);CHECK(r.rax==24);}
    // the row faults: only the row keeps the game's count; the checks go on
    {KilnRegisters r{};r.rax=4;r.rbx=0xdead000000000000ull;logs.clear();hk_respec_row(r);  // a non-canonical address faults

     CHECK(r.rax==4 && g_respec_row_off.load() && !g_respec_off.load() && logged("the row keeps the game's count"));
     r.rbx=reinterpret_cast<uintptr_t>(ui);hk_respec_row(r);CHECK(r.rax==4);}
    CHECK(node_check(2,5)==22);
    // the snapshot cleared (the server's last Respec link went): the game's count again
    CHECK(t_set_respec_stock(0,nullptr,0)==KILN_OK && node_check(2,5)==2 && altar_check(0,12)==0);
    CHECK(t_set_respec_stock(1,snap,2)==KILN_OK && node_check(2,5)==22);
    // the client world stopping clears it too
    {KilnWorldEvent stop{sizeof stop,KILN_WORLD_STOPPING,KILN_WORLD_CLIENT,1,0};client_world(&stop,nullptr);}
    CHECK(!g_respec_on.load() && node_check(2,5)==2);
    // a fault in the unlearn check's hook: the feature stands down at once, its row on the next Pre (never from the hook)
    CHECK(t_set_respec_stock(1,snap,2)==KILN_OK);
    {KilnRegisters r{};r.rax=2;r.r14=5;r.r12=0xdead000000000000ull;logs.clear();hk_respec_node(r);
     CHECK(r.rax==2 && g_respec_off.load() && logged("the skill tree's unlearn check hook faulted") && !lowered.count("crafting.respec_sources"));}
    respec_client_pre();CHECK(lowered["crafting.respec_sources"]==KILN_CAP_REASON_FAULTED);
    CHECK(node_check(2,5)==2 && altar_check(0,12)==0);  // off: the game's
    lowered.clear();respec_client_pre();CHECK(lowered.empty());  // lowered once
    // the server half follows the stand-down too (one kiln-items in a hosting client's process)
    side_now=KILN_SIDE_SERVER;
    CHECK(respec(10,2,3,20,&rc)==0 && fixture::calls.empty());
    g_respec_off.store(false);
    // a client whose check sites carry another module's code: the client half stands down, named in kiln.log
    {
        g_respec_node_on.store(false);g_respec_altar_on.store(false);g_respec_row_on.store(false);mid2_links.clear();
        side_now=KILN_SIDE_CLIENT;foreign={altar-2};logs.clear();
        respec_client_pre();
        CHECK(mid2_links.empty() && g_respec_off.load() && lowered["crafting.respec_sources"]==KILN_CAP_REASON_FAULTED &&
              logged("respec: the client's respec checks from linked sources stands down"));
        foreign.clear();
    }
    // ---- labs only (KILN_STATIONS_CRAFT_LAB=1): a spawned player character's respec links ----
    {
        side_now=KILN_SIDE_SERVER;g_respec_off.store(false);lowered.clear();
        constexpr uint32_t CHAR=3000, LABCHEST=2300;
        CHECK(kiln_stations_lab_respec_link(CHAR,LABCHEST)==0);            // the switch off: refused, nothing kept
        g_craft_lab=true;
        // review of #150: a client process (a hosted world, where real players play) never takes a lab link, even with the
        // switch set: only a dedicated server
        side_now=KILN_SIDE_CLIENT;
        CHECK(kiln_stations_lab_respec_link(CHAR,LABCHEST)==0 && !respec_lab_any());
        side_now=KILN_SIDE_SERVER;
        CHECK(kiln_stations_lab_respec_link(PLAYER,LABCHEST)==0 && kiln_stations_lab_respec_link(CHAR,5)==0);  // never a real player
        logs.clear();
        CHECK(kiln_stations_lab_respec_link(CHAR,LABCHEST)==1 && logged("respec: lab respec link"));  // kiln.log names each one
        auto lab=[&](uint32_t bag,uint32_t chest,uint32_t* rc,uintptr_t ret=0) {
            wr(line,0x14,uint32_t{2});
            fixture::inventory={{CHAR,{{RUNE,bag}}},{LABCHEST,{{RUNE,chest}}},{CHEST,{{RUNE,0}}}};
            fixture::paid.clear();fixture::calls.clear();logs.clear();*rc=99;
            return craft_pay_impl(tx,&costs,CHAR,1,nullptr,ret?ret:g_respec.paid,rc);
        };
        CHECK(lab(0,5,&rc)==0 && fixture::calls.empty());                 // not a character (the transaction's view): nothing
        fixture::lab_character=CHAR;
        CHECK(lab(0,5,&rc)==1 && rc==0 && fixture::inventory[LABCHEST][RUNE]==3);  // the Runes only in the linked chest
        // review of #150: the same lab link is never used in a client process (a hosting client's server world)
        side_now=KILN_SIDE_CLIENT;
        CHECK(lab(0,5,&rc)==0 && fixture::calls.empty() && fixture::inventory[LABCHEST][RUNE]==5);
        side_now=KILN_SIDE_SERVER;
        CHECK(lab(2,5,&rc)==0 && fixture::calls.empty());                 // the bag covers it: the game's
        CHECK(lab(0,1,&rc)==0 && fixture::calls.empty() && logged("short by 1"));  // short: the game refuses
        CHECK(lab(0,5,&rc,0x1234)==0 && fixture::calls.empty());          // a craft never pays from a lab link
        CHECK(kiln_stations_lab_respec_link(CHAR,0)==1 && lab(0,5,&rc)==0 && fixture::calls.empty());  // removed
        // review of #150: at most 16 lab links in all (a lab needs one); one already there is accepted again
        {
            bool all = true;
            for (uint32_t i = 0; i < 16; ++i) all = kiln_stations_lab_respec_link(CHAR, LABCHEST + i) == 1 && all;
            CHECK(all && kiln_stations_lab_respec_link(CHAR + 1, LABCHEST) == 0 && kiln_stations_lab_respec_link(CHAR, LABCHEST) == 1);
            CHECK(kiln_stations_lab_respec_link(CHAR, 0) == 1 && !respec_lab_any());
        }
        g_craft_lab=false;fixture::lab_character=0;
    }
    g_registered.remove(10,craft_only);g_registered.remove(10,link2);
    std::printf("respec sources: %d checks, %d failed\n",checks,failed);return failed?1:0;
}
