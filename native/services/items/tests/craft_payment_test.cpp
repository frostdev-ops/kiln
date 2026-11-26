// SPDX-License-Identifier: MIT
// The actual RecipePaid adapter on fake server transactions. No game, no live world.
#include "../stations.cpp"
#include <cstdarg>
#include <map>
#include <string>
namespace fixture {
std::map<uint32_t,std::map<uint32_t,uint32_t>> inventory;
bool server_storage = true, fail_take = false;
// GitHub #148: kiln.items' access_from_tx needs every accessor of the transaction, Fuel among them; the blueprint program's
// transaction has no Fuel accessor (its setup leaves +0x70 zero, InventoryTxInit copies that to tx+0x38), so it fails
// there and only access_from_tx_reads (InventorySetup and Inventory) accepts it
bool tx_fuel = true;
uint8_t stock_error = 0x1f;
std::vector<uint32_t> paid;
void* bases=nullptr;
bool in_base=false;
void* expected_tx=nullptr;
bool wrong_tx=false;
bool production_station=false, component_fault=false;
std::vector<uint32_t> stock_list;                              // the base's Magic chests BaseStockForPlayer hands back
std::vector<uint32_t> calls;                                   // every PayMaterial call, in order (entity)
uint32_t refuse=0; uint8_t refuse_error=0x0b;                  // PayMaterial answers this entity with an error byte
std::map<uint32_t,std::map<uint32_t,uint32_t>> counted_as;     // what the transaction's accessors show, when it differs
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
std::vector<std::string> logs; // every kiln.log line the adapter wrote
void items_log(int,const char* fmt,...) {
    char b[1024]; va_list a; va_start(a,fmt); std::vsnprintf(b,sizeof b,fmt,a); va_end(a); logs.emplace_back(b);
}
uint64_t current_step() { return 1; }
const KilnApi* items_api() { return g_api; }
KilnOwner items_owner() { return 10; }
bool access_from_tx(void*,Access* out) { *out = {}; return fixture::server_storage && fixture::tx_fuel; }
bool access_from_tx_reads(void*,Access* out) { *out = {}; return fixture::server_storage; }
bool setup_flags(const Access&,KilnEntity e,uint8_t* flags) { *flags = e <= 1023 ? 1 : 0; return fixture::inventory.count(e) != 0; }
bool read_pages(const Access&,KilnEntity e,PageSet* out,bool* initialized) {
    *out = {}; *initialized = fixture::inventory.count(e) != 0;
    if (!*initialized) return false;
    out->n = 1; out->p[0].entity = e; size_t i=0;
    const auto& view = fixture::counted_as.count(e) ? fixture::counted_as[e] : fixture::inventory[e];
    for (auto [item,count] : view) { if(i==8)break; out->p[0].s[i++] = {0,item,count,0}; }
    return true;
}
void player_guard(const Access&,KilnEntity e,PlayerGuard* out) { *out = {}; out->is_player = e <= 1023; }
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
    if(fixture::refuse && entity==fixture::refuse) {*out={};out->error=fixture::refuse_error;out->left=0;return out;}
    if(fixture::fail_take && entity==2000) RaiseException(0xe1230001,0,0,nullptr);
    auto& count=fixture::inventory[entity][item]; const auto take=std::min(n,count); count-=take;
    if(take)fixture::paid.push_back(entity);
    *out={};out->left=n-take;return out;
}
KilnResult KILN_CALL position(KilnEntity,KilnVec3* out) {*out={};return KILN_OK;}
PtrSize* __fastcall component(PtrSize* out,uintptr_t,uint32_t,uint32_t entity) {
    if(fixture::component_fault)RaiseException(0xe1230001,0,0,nullptr);
    static uint8_t factory[0x68]={};
    *out={};
    if(entity==2000 && fixture::production_station) {out->ptr=factory;out->size=sizeof factory;}
    return out;
}
bool KILN_CALL tick_thread() {return true;}
uintptr_t KILN_CALL singleton(uintptr_t,KilnStr name) {
    return std::string_view(name.ptr,name.len)=="PlayerBasesSystemServer"?reinterpret_cast<uintptr_t>(fixture::bases):0;
}
KilnResult KILN_CALL session(KilnSessionInfo* info) {info->state=KILN_SESSION_DEDICATED;return KILN_OK;}
}
int main() {
    using namespace kitems;
    int checks=0,failed=0;
#define CHECK(x) do {++checks;if(!(x)){++failed;std::printf("FAIL %d %s\n",__LINE__,#x);}}while(0)
    KilnApi api{}; api.session=&session;api.on_tick_thread=&tick_thread;api.singleton=&singleton; g_api=&api;
    G.read=G.craft_ok=G.registered_ok=G.outputs_ok=true;G.base_at=&base_at;G.base_stock=&base_stock;G.pay=&pay;G.recipe_paid=0x1234;
    E.get_component=&component;H_FACTORY=99;
    uint8_t infos[0xa0*2]={};wr(infos,0,uint32_t{1});infos[0x9d]=2;
    uint8_t bases[0x400]={};wr(bases,0x10,infos);g_srv_bases.store(bases);fixture::bases=bases;
    uint8_t tx[0x100]={};wr(tx,0x70,bases);fixture::expected_tx=tx;
    uint8_t cost[24]={};wr(cost,0x10,uint32_t{7});wr(cost,0x14,uint32_t{3});
    struct {void* ptr;uint64_t n;} costs{cost,1};
    fixture::inventory={{1,{{7,1}}},{2000,{{7,2}}}};
    uint64_t handle=0;
    KilnCraftSourceRequest request{sizeof request,1,2000,0,0,0,nullptr};
    g_srv_bases.store(nullptr);uint64_t early=0;
    CHECK(t_add_source(10,&request,&early)==KILN_OK && early!=0); // current server singleton, before the first cached Pre
    if(early)t_remove_source(10,early);
    g_srv_bases.store(bases);
    CHECK(t_add_source(10,&request,&handle)==KILN_OK && handle!=0);
    KilnStationStack stocks[2]{};uint32_t n=0;
    CHECK(t_source_stock(1,0,stocks,2,&n)==KILN_OK && n==1 && stocks[0].item==7 && stocks[0].count==2);
    CHECK(t_source_stock(1,0,nullptr,0,&n)==KILN_ERR_BUFFER_TOO_SMALL && n==1);
    CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_ALL,stocks,2,&n)==KILN_OK && n==1 && stocks[0].count==2);
    CHECK(t_source_stock(1,32,stocks,2,&n)==KILN_ERR_INVALID_ARGUMENT); // 2 LOCKS (#147), 4 FILLS and 8 BUILDING (#132) since 0.2.0-dev.31.12, 16 RESPEC (#150)
    // GitHub #132: one purpose's rows count only the links opted into it; two purposes at once are refused
    CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_FILLS,stocks,2,&n)==KILN_OK && n==0);    // the link above: crafting only
    CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_FILLS|KILN_CRAFT_SOURCE_STOCK_BUILDING,stocks,2,&n)==KILN_ERR_INVALID_ARGUMENT);
    CHECK(t_source_stock(1,uint32_t{KILN_CRAFT_SOURCE_STOCK_LOCKS}|KILN_CRAFT_SOURCE_STOCK_FILLS,stocks,2,&n)==KILN_ERR_INVALID_ARGUMENT);
    {
        fixture::inventory[2001]={{7,4},{8,1}};fixture::inventory[2002]={{7,5}};
        uint64_t fill_link=0,build_link=0,both=0;
        KilnCraftSourceRequest f{sizeof f,1,2001,0,1,1,nullptr,KILN_CRAFT_SOURCE_FILLS,0};const KilnItemId seven=7;f.items=&seven; // allow 7
        KilnCraftSourceRequest b{sizeof b,1,2002,1,0,0,nullptr,KILN_CRAFT_SOURCE_BUILDING,0};
        KilnCraftSourceRequest fb{sizeof fb,1,2002,0,0,0,nullptr,KILN_CRAFT_SOURCE_FILLS|KILN_CRAFT_SOURCE_BUILDING,0};
        CHECK(t_add_source(10,&f,&fill_link)==KILN_OK && t_add_source(10,&b,&build_link)==KILN_OK);
        CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_FILLS,stocks,2,&n)==KILN_OK && n==1 && stocks[0].item==7 && stocks[0].count==4);
        CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_BUILDING,stocks,2,&n)==KILN_OK && n==1 && stocks[0].item==7 && stocks[0].count==5);
        CHECK(t_add_source(10,&fb,&both)==KILN_OK);
        CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_FILLS,stocks,2,&n)==KILN_OK && n==1 && stocks[0].count==9); // 2002 counted once
        infos[0x9d]=0; // no CanAccessInventories: nothing counts
        CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_FILLS,stocks,2,&n)==KILN_OK && n==0);
        infos[0x9d]=2;
        t_remove_source(10,fill_link);t_remove_source(10,build_link);t_remove_source(10,both);
        CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_BUILDING,stocks,2,&n)==KILN_OK && n==0);
        fixture::inventory.erase(2001);fixture::inventory.erase(2002);
    }
    KilnWorldApi world{};world.position=&position;g_w=&world;
    fixture::in_base=true;uint32_t base_chest=2000;
    G.stock_items=0x20;G.stock_items_count=0x28;G.stock_water=0x30;G.stock_water_count=0x38;G.aabb_y=0x80;G.zone_mask_rel=0x20;
    wr(bases,0,uint32_t{1});wr(bases,0x20,&base_chest);wr(bases,0x28,uint64_t{1});
    CHECK(t_source_stock(1,0,stocks,2,&n)==KILN_OK && n==0); // already present in crafting base stock
    CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_ALL,stocks,2,&n)==KILN_OK && n==1 && stocks[0].count==2); // tint still needs it
    fixture::in_base=false;g_w=nullptr;wr(bases,0,uint32_t{0});
    auto bad=request;bad.container=2;uint64_t rejected=0;
    CHECK(t_add_source(10,&bad,&rejected)==KILN_ERR_INVALID_ARGUMENT && !rejected);
    bad.container=9999;
    CHECK(t_add_source(10,&bad,&rejected)==KILN_ERR_INVALID_ARGUMENT && !rejected);
    CHECK(t_remove_source(11,handle)==KILN_ERR_NOT_FOUND);
    fixture::production_station=true;
    CHECK(t_add_source(10,&request,&rejected)==KILN_ERR_INVALID_ARGUMENT && !rejected);
    CHECK(t_source_stock(1,0,stocks,2,&n)==KILN_OK && n==0); // a former chest may now name a station
    CHECK(t_source_stock(1,KILN_CRAFT_SOURCE_STOCK_ALL,stocks,2,&n)==KILN_OK && n==0);
    uint32_t station_code=99;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&station_code)==0 && fixture::paid.empty());
    CHECK(t_set_output_source(2000,1)==KILN_OK && sources_copy()==std::vector<uint32_t>({2000}));
    CHECK(t_set_output_source(2000,0)==KILN_OK && sources_copy().empty());
    fixture::production_station=false;fixture::component_fault=true;
    CHECK(t_add_source(10,&request,&rejected)==KILN_ERR_INVALID_ARGUMENT && !rejected);
    CHECK(t_source_stock(1,0,stocks,2,&n)==KILN_OK && n==0); // unknown component kind fails closed
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&station_code)==0 && fixture::paid.empty());
    fixture::component_fault=false;
    uint32_t code=99;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0);
    CHECK(fixture::paid==std::vector<uint32_t>({1,2000}) && !fixture::wrong_tx);
    auto reset=[&] {fixture::inventory={{1,{{7,1}}},{2000,{{7,2}}}};fixture::paid.clear();code=99;};
    reset();wr(tx,0x70,uint64_t{0});
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0); // owner-only transaction may omit base words
    wr(tx,0x70,bases);
    reset();tx[0xb1]=2;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0); // no base, error 0x1f
    reset();fixture::stock_error=0x16;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0); // base check fails, independent right succeeds
    reset();infos[0x9d]=0;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    reset();infos[0x9d]=2;
    CHECK(craft_pay_impl(tx,&costs,2,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    reset();wr(infos,4,uint32_t{99});
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty()); // reused player slot, another login
    reset();
    CHECK(t_source_stock(1,0,stocks,2,&n)==KILN_OK && n==0);
    wr(infos,4,uint32_t{0});
    reset();fixture::server_storage=false;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    fixture::server_storage=true;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x9999,&code)==0 && fixture::paid.empty());
    reset();fixture::fail_take=true;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==4); // caller must rollback this transaction
    fixture::fail_take=false;
    // ---- GitHub #132 (0.2.0-dev.31.12): a block placement (ApplyMaterials' take in the blueprint program, t_build_pay set
    // by hk_build_apply) pays from the player's building links only, bag and base first, all or nothing ----
    {
        auto said=[&](const char* s) { for (const auto& l : logs) if (l.find(s) != std::string::npos) return true; return false; };
        g_build.paid=0x5678;reset();logs.clear();
        fixture::tx_fuel=false;  // #148: the blueprint program's own transaction, as the game builds it (no Fuel accessor)
        t_build_pay=true;  // only the crafting link (container 2000) exists: a placement never takes from it
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
        uint64_t bl=0;KilnCraftSourceRequest b{sizeof b,1,2000,0,0,0,nullptr,KILN_CRAFT_SOURCE_BUILDING,0};
        CHECK(t_add_source(10,&b,&bl)==KILN_OK && bl);
        t_build_pay=false;reset();  // ApplyMaterials' take outside the blueprint program (props, impact natives): the game's
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
        t_build_pay=true;reset();
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==1 && code==0 && fixture::paid==std::vector<uint32_t>({1,2000}));
        CHECK(said("a block placement paid 2 from additional crafting sources"));
        reset();fixture::inventory[2000][7]=1;  // bag 1 + link 1 < 3: left to the game (it refuses), nothing taken
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
        CHECK(said("a block placement left to the game"));
        reset();fixture::fail_take=true;  // a take faulted after another: 4, ApplyMaterials fails and the transaction rolls back
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==1 && code==4);
        fixture::fail_take=false;
        reset();infos[0x9d]=0;  // no CanAccessInventories: the link is not a source
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
        infos[0x9d]=2;
        // #148: a craft's transaction still needs every accessor (its payment may read Fuel): without one it is the game's
        reset();CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
        CHECK(said("the server world's craft transaction did not check out (its accessors)"));
        // #148: a placement whose transaction is not the server world's says so as a placement
        reset();logs.clear();fixture::server_storage=false;
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
        CHECK(said("the server world's block placement transaction did not check out (its accessors)"));
        fixture::server_storage=true;
        // review of #148: another world's placement (a hosting client's own world, Building Tools' redirect followed by the
        // program's frame there too) says so as a placement, not as a craft
        reset();logs.clear();wr(tx,0x70,uint64_t{0x55});
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
        CHECK(said("block placement payment left to the game (not the server world's transaction") && !said("craft payment"));
        wr(tx,0x70,bases);
        // review of #148: a placement of more than 32 cost lines (a Building Tools area action or blueprint of many
        // materials) is the game's, bag and base, and kiln.log says why (it said "the cost lines could not be read")
        {
            uint8_t many[33*24]={};
            for (uint32_t i=0;i<33;++i) {wr(many+i*24,0x10,uint32_t{7}+i);wr(many+i*24,0x14,uint32_t{1});}
            struct {void* ptr;uint64_t n;} long_costs{many,33};
            reset();logs.clear();
            CHECK(craft_pay_impl(tx,&long_costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());
            CHECK(said("block placement payment left to the game (it names more than 32 cost lines") && !said("could not be read"));
            long_costs.n=0;reset();logs.clear();
            CHECK(craft_pay_impl(tx,&long_costs,1,1,nullptr,0x5678,&code)==0 && said("the cost lines could not be read"));
        }
        fixture::tx_fuel=true;
        reset();CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0);  // a craft still uses it (every link crafts)
        t_build_pay=false;g_build.paid=0;reset();
        CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x5678,&code)==0 && fixture::paid.empty());  // no building symbols: never
        t_remove_source(10,bl);reset();
    }
    // #132: a craft at a crafting station (the decoration table's menu sends the same CraftRecipe; kiln.gameplay's craft
    // hook names the station for the call) pays from the player's linked source the same way, all or nothing
    reset();t_craft_station=3000;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0 && fixture::paid==std::vector<uint32_t>({1,2000}));
    // the station being crafted at is never a source, even when a mod linked it
    fixture::inventory={{1,{{7,1}}},{2000,{{7,0}}},{3000,{{7,5}}}};fixture::paid.clear();code=99;
    uint64_t station_link=0;KilnCraftSourceRequest at_station{sizeof at_station,1,3000,0,0,0,nullptr};
    CHECK(t_add_source(10,&at_station,&station_link)==KILN_OK && station_link!=0);
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    t_craft_station=0;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0 && fixture::paid==std::vector<uint32_t>({1,3000}));
    g_registered.remove(10,station_link);
    reset();g_registered.remove(10,handle);
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    // #132 follow-up (hosting client): the client world's own dry runs (RecipeApply on its own transaction, every recipe
    // the crafting menu counts) must not use up the lines that say why the server world's craft was left to the game.
    auto logged=[&](const char* s) { for(const auto& l:logs) if(l.find(s)!=std::string::npos) return true; return false; };
    reset();CHECK(t_add_source(10,&request,&handle)==KILN_OK && handle!=0);
    uint8_t client_bases[0x400]={};uint8_t dry[0x100]={};wr(dry,0x70,client_bases);
    for(int i=0;i<20;++i) CHECK(craft_pay_impl(dry,&costs,1,1,nullptr,0x1234,&code)==0);
    logs.clear();fixture::server_storage=false;
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    CHECK(logged("the server world's craft transaction"));
    fixture::server_storage=true;
    // one line per player craft, at its end, saying how far it got (kiln.gameplay's CraftRecipe hook brackets the call)
    reset();logs.clear();
    kiln_stations_set_craft_context(3000,1);
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==1 && code==0);
    kiln_stations_set_craft_context(0,0);
    kiln_stations_craft_done(1,3000,0x77,0);
    CHECK(logged("player 1's craft of recipe 0x00000077 at station 3000: paid 2 item(s)"));
    // refused before its payment: the game never asked (a requirement, or the station's props)
    logs.clear();
    kiln_stations_set_craft_context(3000,1);kiln_stations_set_craft_context(0,0);kiln_stations_craft_done(1,3000,0x77,4);
    CHECK(logged("refused without reaching the recipe payment"));
    // the payment saw another login's links
    reset();logs.clear();wr(infos,4,uint32_t{99});
    kiln_stations_set_craft_context(3000,1);
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    kiln_stations_set_craft_context(0,0);kiln_stations_craft_done(1,3000,0x77,4);
    CHECK(logged("earlier login"));
    wr(infos,4,uint32_t{0});
    // short even with the linked source: nothing taken, the line says by how much
    reset();logs.clear();fixture::inventory={{1,{{7,0}}},{2000,{{7,1}}}};
    kiln_stations_set_craft_context(3000,1);
    CHECK(craft_pay_impl(tx,&costs,1,1,nullptr,0x1234,&code)==0 && fixture::paid.empty());
    kiln_stations_set_craft_context(0,0);kiln_stations_craft_done(1,3000,0x77,4);
    CHECK(logged("short by 2"));
    // a factory's own cycle and a craft outside any context say nothing
    reset();logs.clear();
    kiln_stations_set_craft_context(3000,1);kiln_stations_set_craft_context(0,0);kiln_stations_craft_done(3000,3000,0x77,0);
    kiln_stations_craft_done(1,3000,0x77,0);
    CHECK(!logged("'s craft of recipe"));
    g_registered.remove(10,handle);
    // a player with nothing linked and no station opted in: the game's craft, no line
    logs.clear();
    kiln_stations_set_craft_context(3000,1);kiln_stations_set_craft_context(0,0);kiln_stations_craft_done(1,3000,0x77,4);
    CHECK(!logged("'s craft of recipe"));
    // ---- GitHub #132 (beta 1, a hosting player): the bag and the Magic chests pay first, linked sources only the rest ----
    // A hosting client's server-world craft: stock mode 2 (the payer, then the base's Magic chests), crafted at a station,
    // the payer standing in the base with CanAccessInventories, one link registered by a mod. Both words a bases system can
    // sit in (+0x68, +0x70) are run. Every PayMaterial call is recorded, so "no lender call" is checked, not inferred.
    {
        constexpr uint32_t PAYER=1, CHEST=2100, LINKED=2000, ITEM=7;
        uint8_t five[24]={};wr(five,0x10,ITEM);wr(five,0x14,uint32_t{5});
        struct {void* ptr;uint64_t n;} cost5{five,1};
        fixture::stock_error=0;fixture::stock_list={CHEST};fixture::in_base=true;fixture::server_storage=true;
        infos[0x9d]=2;wr(infos,4,uint32_t{0});
        uint64_t link=0;
        CHECK(t_add_source(10,&request,&link)==KILN_OK && link!=0);
        for (LogLimit* l : {&g_log_craft,&g_log_paid,&g_log_refused,&g_log_left}) l->budget=1000;  // every line of this block
        auto lent_calls=[&]{return (size_t)std::count(fixture::calls.begin(),fixture::calls.end(),LINKED);};
        auto line_with=[&](const char* s)->std::string{for(const auto& l:logs) if(l.find(s)!=std::string::npos) return l; return {};};
        for (int word : {0x68, 0x70}) {
            uint8_t htx[0x100]={};wr(htx,word,bases);htx[0xb1]=2;fixture::expected_tx=htx;
            auto craft=[&](uint32_t bag,uint32_t chest,uint32_t linked,uint32_t* rc) {
                fixture::inventory={{PAYER,{{ITEM,bag}}},{CHEST,{{ITEM,chest}}},{LINKED,{{ITEM,linked}}}};
                fixture::paid.clear();fixture::calls.clear();logs.clear();*rc=99;
                kiln_stations_set_craft_context(4417,1);
                const int r=craft_pay_impl(htx,&cost5,PAYER,1,nullptr,0x1234,rc);
                kiln_stations_set_craft_context(0,0);kiln_stations_craft_done(PAYER,4417,0x3ef55875,r?*rc:0);
                return r;
            };
            uint32_t rc=0;
            // (a) the bag covers everything: the game's own payment, no PayMaterial call from Kiln at all, no linked line
            CHECK(craft(5,0,10,&rc)==0 && fixture::calls.empty() && lent_calls()==0);
            CHECK(line_with("additional crafting sources").empty() && line_with("from linked sources").empty());
            CHECK(!line_with("covered by the bag").empty() && !line_with("counted 5; the payer's bag: held 5").empty());
            CHECK(fixture::inventory[LINKED][ITEM]==10 && fixture::inventory[PAYER][ITEM]==5);
            // ... the bag and a Magic chest together cover it: still the game's, no lender call
            CHECK(craft(3,2,10,&rc)==0 && fixture::calls.empty() && line_with("additional crafting sources").empty());
            // (b) a partial bag: the bag, then the Magic chest, then the link for exactly the shortfall (2)
            CHECK(craft(2,1,10,&rc)==1 && rc==0);
            CHECK(fixture::calls==std::vector<uint32_t>({PAYER,CHEST,LINKED}) && fixture::paid==std::vector<uint32_t>({PAYER,CHEST,LINKED}));
            CHECK(fixture::inventory[PAYER][ITEM]==0 && fixture::inventory[CHEST][ITEM]==0 && fixture::inventory[LINKED][ITEM]==8);
            CHECK(!line_with("a craft paid 2 from additional crafting sources").empty());
            CHECK(!line_with("paid 2 item(s) from linked sources or opted-in station outputs (the bag and the base's Magic chests: "
                             "counted 3, paid 3; the payer's bag: held 2, paid 2)").empty());
            // (c) a true shortfall, empty bag and chest: #125's linked payment unchanged, the whole cost and no more
            CHECK(craft(0,0,10,&rc)==1 && rc==0 && fixture::inventory[LINKED][ITEM]==5 && lent_calls()==1);
            CHECK(fixture::calls==std::vector<uint32_t>({PAYER,CHEST,LINKED}));  // the bag and the chest were asked first
            CHECK(!line_with("a craft paid 5 from additional crafting sources").empty());
            CHECK(!line_with("counted 0, paid 0; the payer's bag: held 0, paid 0)").empty());
            // ... a link that cannot cover the shortfall: nothing taken
            CHECK(craft(1,0,3,&rc)==0 && fixture::calls.empty() && !line_with("short by 1").empty());
            // (d) a failing lender rolls the whole payment back (4), the bag's take included
            fixture::fail_take=true;
            CHECK(craft(2,0,10,&rc)==1 && rc==4 && !line_with("rolled back").empty());
            fixture::fail_take=false;
            // (e) the beta-1 log line ("paid 5 from additional crafting sources") with the bag holding all 5 in the transaction's
            // view: never, even when the game's PayMaterial would refuse the bag; the count alone leaves it to the game
            fixture::refuse=PAYER;
            CHECK(craft(5,0,10,&rc)==0 && fixture::calls.empty() && line_with("additional crafting sources").empty());
            // (f) the bag was counted to hold 3 but its take was refused: the link must not pay more than the counted
            // shortfall (2); the craft rolls back (4) and the line says what the game refused
            fixture::counted_as={{PAYER,{{ITEM,3}}}};
            CHECK(craft(0,0,10,&rc)==1 && rc==4 && fixture::inventory[LINKED][ITEM]==10 && lent_calls()==0);
            CHECK(!line_with("paid less than they were counted to hold").empty() && !line_with("refused 1 take(s), last error 0x0b").empty());
            fixture::counted_as.clear();fixture::refuse=0;
        }
        g_registered.remove(10,link);fixture::stock_list.clear();fixture::expected_tx=tx;
    }
    std::printf("craft payment: %d checks, %d failed\n",checks,failed);return failed?1:0;
}
