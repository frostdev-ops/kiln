// SPDX-License-Identifier: MIT
#define main presentation_fixture
#include "presentation_api.cpp"
#undef main
#include "../ammo_runtime.h"
#include <vector>
static KilnClientState ammo_client_state;
static KilnResult KILN_CALL ammo_state(KilnClientState* out){*out=ammo_client_state;return KILN_OK;}
static bool KILN_CALL ammo_thread(){return true;}
static int backpack_answer;
static unsigned backpack_reads,backpack_reads_999; // probe calls (all items, item 999)
static int backpack_fixture(uint32_t,uint32_t item){++backpack_reads;if(item==999)++backpack_reads_999;return backpack_answer;}
static unsigned equip_calls,finalizer_calls;
static void KILN_CALL equip_fixture(SelectorGroup*,void*,uint8_t,uint32_t,uint32_t){++equip_calls;}
static uint32_t finalizer_spell; // the spell the game's finalizer selects (its reconciliation's item), 0: it leaves the spells
static void KILN_CALL finalizer_fixture(SelectorGroup* g,void*,void*,void*,void*,void*){
    ++finalizer_calls;g[0].selected=0;g[0].has_selected=1;
    for(uint64_t i=0;finalizer_spell&&i<g[1].count;++i)if(g[1].data[i].item==finalizer_spell){g[1].selected=i;g[1].has_selected=1;}
}
static std::vector<std::pair<int32_t,std::string>> log_lines;
static void KILN_CALL capture_log(KilnOwner,int32_t level,KilnStr text){log_lines.emplace_back(level,std::string(text.ptr,text.len));}
static bool game_lists_555; // the arrow group also lists 555, with this count (0: a stack used up, its row kept)
static uint64_t game_555_count;
static bool spell_swapped; // GitHub issue 151: the spell group after a swap (999 kept at count 0, 556 in the backpack)
static void vanilla_selector(SelectorGroup* groups,void*,uint8_t) {
    for(unsigned i=0;i<7;++i) {
        groups[i]={};groups[i].data=groups[i].inline_rows;groups[i].capacity=64;groups[i].count=2;
        groups[i].data[0].item=999;groups[i].data[0].count=9;groups[i].data[1].item=1257792166u;groups[i].data[1].count=3;
        groups[i].has_selected=1;groups[i].selected=0;
    }
    if(game_lists_555){groups[0].data[2].item=555;groups[0].data[2].count=game_555_count;groups[0].count=3;}
    if(spell_swapped){groups[1].data[0].count=0;groups[1].data[2].item=556;groups[1].data[2].count=1;groups[1].count=3;}
}
static uint32_t held_ammo_item;
static uint32_t KILN_CALL ammo_held(){return held_ammo_item;}
static bool bad_rows;
static SelectorRow* readonly_rows;
static bool nested_ammo;
static SelectorGroup nested_groups[7];
static bool entry_synthetic; // vanilla enumeration retains unmatched rows, so a synthetic row must be gone at entry
static void KILN_CALL ammo_selector_fixture(SelectorGroup* groups,void* access,uint8_t current) {
    entry_synthetic=false; // only the fixture's own rows: the fault cases below leave groups[0].data unreadable
    if(groups[0].data==groups[0].inline_rows)for(uint64_t r=0;r<groups[0].count&&r<64;++r)if(groups[0].data[r].item==555)entry_synthetic=true;
    vanilla_selector(groups,access,current);
    if(bad_rows)groups[0].data=reinterpret_cast<SelectorRow*>(1);
    if(readonly_rows)groups[0].data=readonly_rows;
    if(nested_ammo){nested_ammo=false;selector(nested_groups,nullptr,0);}
}
// GitHub issue 151 (the flicker 31.21 left): client_cycle_inventory_stock's selection action 0x40cf00 as the game wrote
// it: the game's reconciliation names the equipped stack (no pending pick of the game's), and when the group lists it the
// action selects it and returns 4.
struct ActionResult {uint8_t code;uint8_t pad[3];uint32_t zero,item;};
static uint32_t equipped_item;
static unsigned action_calls,draw_calls;
static void* KILN_CALL action_fixture(void* out,SelectorGroup* g,uint8_t,void*,void*,void*,void*,void*) {
    ++action_calls;auto* r=static_cast<ActionResult*>(out);*r={};
    for(uint64_t i=0;i<g->count;++i)if(g->data[i].item==equipped_item){g->has_selected=1;g->selected=i;r->code=4;return out;}
    return out; // the game: the cursor's row, which its caller then picks (code 0)
}
static void KILN_CALL draw_fixture(SelectorGroup*) {++draw_calls;}
static uintptr_t game_caller; // the pick's call site as kiln.hooks reports it
static uintptr_t KILN_CALL hook_caller(KilnHandle) {return game_caller;}
static KilnResult KILN_CALL ammo_raw(KilnOwner,uintptr_t target,const KilnHookOrder*,void*,void** original,KilnHandle* handle) {
    *original=target==20?reinterpret_cast<void*>(&ammo_selector_fixture):target==21?reinterpret_cast<void*>(&equip_fixture):
        target==23?reinterpret_cast<void*>(&action_fixture):target==24?reinterpret_cast<void*>(&draw_fixture):reinterpret_cast<void*>(&finalizer_fixture);
    *handle=target;return KILN_OK;
}
#ifdef _WIN32
static bool guarded_ammo(SelectorGroup* groups,void* access) {
    __try {selector(groups,access,0);return true;} __except(EXCEPTION_EXECUTE_HANDLER) {return false;}
}
#endif
int main() {
    presentation_fixture();
    fake_hooks.raw_detour=&ammo_raw;
    KilnClientApi client_table{};client_table.state=&ammo_state;client_table.on_client_thread=&ammo_thread;
    KilnApi fake{};fake.size=sizeof fake;fake.abi_version=KILN_ABI_VERSION;fake.subscribe_tick=&tick;fake.unsubscribe=&unsubscribe;fake.service=&service;fake.log=&capture_log;
    api=&fake;hooks=&fake_hooks;child=88;retiring=99;
    ammo::client=&client_table;ammo::ready.store(true);ammo::backpack_probe=&backpack_fixture;
    ammo_client_state.size=sizeof ammo_client_state;ammo_client_state.side=KILN_SIDE_CLIENT;ammo_client_state.session=KILN_SESSION_HOST;
    ammo_client_state.generation=7;ammo_client_state.client_session=9;ammo_client_state.local_player=123;ammo_client_state.client_steps=10;
    selector_game=&ammo_selector_fixture;
    CHECK(ammo::pick_hook.install(&fake,child,21,reinterpret_cast<void*>(&ammo::pick),"ammo pick fixture"));
    CHECK(ammo::finalizer_hook.install(&fake,child,22,reinterpret_cast<void*>(&ammo::finalizer),"ammo finalizer fixture"));
    ammo::catalog[0]={999,1257792166u,555};ammo::catalog[1]={999,1257792166u,555};ammo::catalog_session=9;
    for(uint32_t i=0;i<KILN_TOOL_HOOK_COUNT;++i)kiln_tools_report_hook(i,1);
    auto tool=definition();KilnHandle tool_handle=0;CHECK(kiln_tools_define_family(7,&tool,&tool_handle)==KILN_OK);
    held_ammo_item=tool.tool;kiln_tools_presentation_set_held_provider(&ammo_held);
    uint32_t item=tool.tool;const uint32_t* access[16]{};access[15]=&item;
    SelectorGroup groups[7];selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    CHECK(groups[0].count==2&&groups[1].count==2);
    CHECK(groups[2].count==1&&groups[2].data[0].item==1257792166u); // No provider: all vanilla bytes/behavior retained.
    KilnUiAmmoContext context{};context.size=sizeof context;CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK&&context.ok&&context.game_count==2);
    KilnUiAmmoRow supplied[]={{555,12,0,3},{999,20,0,4}};
    KilnUiAmmoPublish pub{sizeof pub,KUA_KIND_ARROW,KUA_MODE_REPLACE,2,9,0,context.game_serial,supplied};
    CHECK(ammo::publish(7,&pub)==KILN_OK);
    CHECK(ammo::publish(8,&pub)==KUA_ERR_BUSY);
    pub.serial++;CHECK(ammo::publish(7,&pub)==KILN_ERR_INVALID_ARGUMENT);pub.serial--;
    supplied[0].item=666;CHECK(ammo::publish(7,&pub)==KILN_ERR_INVALID_ARGUMENT);supplied[0].item=555;
    nested_ammo=true;selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    CHECK(groups[0].count==2&&groups[0].data[0].item==555&&groups[0].data[0].count==12);
    CHECK(groups[2].count==1&&groups[2].data[0].item==1257792166u);
    CHECK(nested_groups[0].data[0].item==999); // Null-access nested call has no admission.
    groups[0].selected=1;groups[0].has_selected=1;selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    CHECK(groups[0].selected==1&&groups[0].data[1].item==999); // identity, not old index
    groups[0].selected=0;groups[0].has_selected=1;std::swap(supplied[0],supplied[1]);
    CHECK(ammo::publish(7,&pub)==KILN_OK);selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    CHECK(groups[0].selected==1&&groups[0].data[1].item==555);
    std::swap(supplied[0],supplied[1]);CHECK(ammo::publish(7,&pub)==KILN_OK);selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    groups[0].selected=1;ammo::finalizer(groups,nullptr,nullptr,nullptr,nullptr,nullptr);CHECK(groups[0].selected==1&&finalizer_calls==1);
    backpack_answer=0;equip_calls=0;ammo::pick(groups,nullptr,0,555,100);CHECK(equip_calls==0);
    CHECK(groups[0].has_selected&&groups[0].data[groups[0].selected].item==555); // A direct click need not have updated the native index yet.
    KilnUiAmmoPick picks[64]{};uint32_t written=99;CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555&&picks[0].row==3);
    backpack_answer=1;ammo::pick(groups,nullptr,0,555,101);CHECK(equip_calls==1); // A real stack uses the game, even for a provider row.
    backpack_answer=-1;ammo::pick(groups,nullptr,0,555,102);CHECK(equip_calls==1); // Unreadable is never equip permission.
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    ammo::pick(groups,nullptr,1,999,103);CHECK(equip_calls==2); // Unprovided Spell unchanged.
    pub.mode=KUA_MODE_GAME;pub.count=0;pub.rows=nullptr;CHECK(ammo::publish(7,&pub)==KILN_OK);
    backpack_answer=0;ammo::pick(groups,nullptr,0,555,104);CHECK(equip_calls==2); // Removed provider's already drawn virtual row is still suppressed.
    selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);CHECK(groups[0].data[0].item==999&&groups[0].count==2);
    CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK);
    pub.mode=KUA_MODE_REPLACE;pub.count=2;pub.rows=supplied;pub.serial=context.game_serial;
    CHECK(ammo::publish(7,&pub)==KILN_OK);selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    backpack_answer=0;for(unsigned i=0;i<70;++i)ammo::pick(groups,nullptr,0,555,200+i);
    CHECK(ammo::take_picks(8,picks,64,&written)==KILN_OK&&written==0);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==64);
    ammo::pick(groups,nullptr,0,555,299);CHECK(ammo::publish(7,&pub)==KILN_OK);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0); // Old row keys cannot retarget new publications.
    selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    ammo::pick(groups,nullptr,0,555,300);++ammo_client_state.client_session;
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);--ammo_client_state.client_session;
#ifdef _WIN32
    CHECK(guarded_ammo(groups,reinterpret_cast<void*>(1))); // Unreadable access never inherits outer admission.
    bad_rows=true;CHECK(guarded_ammo(groups,const_cast<void*>(static_cast<const void*>(access)))&&ammo::failed());bad_rows=false;
    CHECK(groups[0].count==2); // A read fault before any write does not mutate vanilla.
    ammo::faulted.store(false);selector(groups,const_cast<void*>(static_cast<const void*>(access)),0);
    auto* page=static_cast<SelectorRow*>(VirtualAlloc(nullptr,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));CHECK(page);
    page[0].item=999;page[0].count=9;page[1].item=1257792166u;page[1].count=3;
    DWORD old_protection=0;CHECK(VirtualProtect(page,4096,PAGE_READONLY,&old_protection));readonly_rows=page;
    CHECK(guarded_ammo(groups,const_cast<void*>(static_cast<const void*>(access)))&&ammo::failed()&&groups[0].count==0);
    readonly_rows=nullptr;groups[0].data=groups[0].inline_rows;CHECK(VirtualFree(page,0,MEM_RELEASE));ammo::faulted.store(false);
#endif
    auto* const acc=const_cast<void*>(static_cast<const void*>(access));
    selector(groups,acc,0);CHECK(groups[0].count==2&&groups[0].data[0].item==555&&groups[0].data[1].item==999); // Replace {555,999}
    // A row the game itself lists is the game's own in Replace too, whatever the backpack probe says.
    equip_calls=0;backpack_answer=-1;ammo::pick(groups,nullptr,0,999,301);CHECK(equip_calls==1);
    backpack_answer=0;ammo::pick(groups,nullptr,0,999,302);CHECK(equip_calls==2);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    // A world event forgets the session, not the rows already written: the old binding keeps its synthetic row from the
    // game's equip (no event), and the group's next build takes the row out before vanilla enumeration retains it.
    KilnWorldEvent world_event{};world_event.size=sizeof world_event;world_event.kind=KILN_WORLD_CLIENT;
    ammo::lifecycle(&world_event,nullptr);
    ammo::pick(groups,nullptr,0,555,303);CHECK(equip_calls==2);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    selector(groups,acc,0);CHECK(!entry_synthetic&&groups[0].count==2&&groups[0].data[0].item==999);
    // Stale sessions' bindings never starve the current one of the bounded binding table.
    ammo::catalog[0]={999,1257792166u,555};ammo::catalog[1]=ammo::catalog[0];ammo::catalog_session=9;
    selector(groups,acc,0);CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK&&context.ok);
    pub.serial=context.game_serial;CHECK(ammo::publish(7,&pub)==KILN_OK);
    for(uintptr_t key=1;key<=16;++key){ammo::Binding stale{};stale.original.value.session=8;ammo::bindings.emplace(key,stale);}
    selector(groups,acc,0);CHECK(groups[0].data[0].item==555&&ammo::bindings.size()<=16);
    // GitHub issue 131: vanilla keeps the row of a stack the player used up, at count 0. That row is no stock: the
    // backpack at the pick decides whether the mod's row of the same item is the game's own.
    auto republish=[&](uint32_t mode) {
        selector(groups,acc,0);CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK&&context.ok);
        pub.mode=mode;pub.serial=context.game_serial;CHECK(ammo::publish(7,&pub)==KILN_OK);selector(groups,acc,0);
    };
    game_lists_555=true;game_555_count=0;republish(KUA_MODE_REPLACE);
    CHECK(context.game_count==3&&context.game_rows[2].item==555&&context.game_rows[2].count==0);
    CHECK(groups[0].count==2&&groups[0].data[0].item==555&&groups[0].data[0].count==12);
    equip_calls=0;backpack_answer=0;ammo::pick(groups,nullptr,0,555,400);CHECK(equip_calls==0);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555&&picks[0].count==12&&picks[0].row==3);
    backpack_answer=-1;ammo::pick(groups,nullptr,0,555,401);CHECK(equip_calls==0); // Unreadable: no equip, no event.
    backpack_answer=1;ammo::pick(groups,nullptr,0,555,402);CHECK(equip_calls==1); // The player carries it again: vanilla.
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    republish(KUA_MODE_APPEND); // Append shows the game's row (count 0), and its pick is decided the same way.
    CHECK(groups[0].count==3&&groups[0].data[2].item==555&&groups[0].data[2].count==0);
    backpack_answer=0;ammo::pick(groups,nullptr,0,555,403);CHECK(equip_calls==1);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555);
    game_555_count=4;republish(KUA_MODE_REPLACE); // A row the game lists with stock stays the game's own.
    CHECK(groups[0].data[0].item==555&&groups[0].data[0].count==12);
    backpack_answer=0;ammo::pick(groups,nullptr,0,555,404);CHECK(equip_calls==2);
    backpack_answer=-1;ammo::pick(groups,nullptr,0,555,405);CHECK(equip_calls==3);
    CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    // GitHub issue 145: the list was built while the backpack held the stack, then a swap moved the stack out before the
    // game built the list again. The row still shows count 1; vanilla would equip nothing and empty the slot.
    for(const uint32_t mode:{KUA_MODE_REPLACE,KUA_MODE_APPEND}) {
        game_555_count=1;backpack_answer=1;republish(mode); // the build finds the stack in the backpack
        CHECK(context.game_rows[2].item==555&&context.game_rows[2].count==1);
        const uint32_t shown=mode==KUA_MODE_REPLACE?0:2;
        CHECK(groups[0].data[shown].item==555&&groups[0].data[shown].count==(mode==KUA_MODE_REPLACE?12:1));
        groups[0].selected=1;equip_calls=0;
        backpack_answer=0;ammo::pick(groups,nullptr,0,555,500);CHECK(equip_calls==0); // the stack left: the mod's pick
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555&&picks[0].count==12&&picks[0].row==3);
        CHECK(groups[0].has_selected&&groups[0].data[groups[0].selected].item==555);
        backpack_answer=-1;ammo::pick(groups,nullptr,0,555,501);CHECK(equip_calls==1); // unreadable: as before, the game's
        // The currently equipped spell or arrow: its stack is the selected backpack cell, so the game's row counts it
        // and the backpack holds it. The game's own pick, exactly once, no event.
        backpack_answer=1;ammo::pick(groups,nullptr,0,555,502);CHECK(equip_calls==2);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
        // A build that did not find the stack (a reader that cannot see that stock) keeps issue 127's rule: the game's.
        game_555_count=2;backpack_answer=0;republish(mode);equip_calls=0;
        ammo::pick(groups,nullptr,0,555,503);CHECK(equip_calls==1);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
        // Between the game's new build (the stack's row kept at count 0) and the mod's next publication there is no
        // binding: the game's own rows show, and its count-0 row of a provided item is decided as in issue 131.
        backpack_answer=1;republish(mode);game_555_count=0;
        // Replace keeps its publication over the new build (GitHub issue 151, below) up to keep_limit; past it, as here,
        // the game's own rows show until the next publication.
        if(mode==KUA_MODE_REPLACE) {
            selector(groups,acc,0);CHECK(groups[0].count==2&&groups[0].data[0].item==555&&groups[0].data[0].count==12);
            ammo::keeps[0].since-=std::chrono::seconds(2);
        }
        selector(groups,acc,0);
        CHECK(groups[0].count==3&&groups[0].data[2].item==555&&groups[0].data[2].count==0);
        equip_calls=0;backpack_answer=0;ammo::pick(groups,nullptr,0,555,504);CHECK(equip_calls==0);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555&&picks[0].row==3);
        CHECK(groups[0].has_selected&&groups[0].data[groups[0].selected].item==555);
        backpack_answer=-1;ammo::pick(groups,nullptr,0,555,505);CHECK(equip_calls==0); // unreadable: no equip, no event
        backpack_answer=1;ammo::pick(groups,nullptr,0,555,506);CHECK(equip_calls==1); // carried again: the game's
        backpack_answer=0;ammo::pick(groups,nullptr,0,999,507);CHECK(equip_calls==2); // the game's row with stock
        // The window decides by the row the group shows now, not by the last observation (another selector state or a
        // build Kiln did not observe may have changed it): a shown row with stock is the game's.
        groups[0].data[2].count=5;ammo::pick(groups,nullptr,0,555,520);CHECK(equip_calls==3);
        groups[0].data[2].count=0;CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
        // While the service is faulted Kiln leaves every pick in that window to the game, as before the window existed.
        ammo::faulted.store(true);ammo::pick(groups,nullptr,0,555,521);CHECK(equip_calls==4);ammo::faulted.store(false);
        equip_calls=2;
        ammo::pick(groups,nullptr,0,1257792166u,508);CHECK(equip_calls==3);           // a row no mod provided
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
        // A stale session is no permission for a synthetic pick in that window either; the game's row goes the game's way.
        ++ammo_client_state.generation;backpack_answer=0;ammo::pick(groups,nullptr,0,555,509);CHECK(equip_calls==4);
        --ammo_client_state.generation;CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    }
    // The bind-time backpack reads: only game rows with stock whose item the publication provides (555 and 999 here, not
    // 1257792166), in one walk per binding, and none again while the game's rows stay the same.
    game_555_count=3;backpack_answer=1;selector(groups,acc,0);CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK);
    pub.mode=KUA_MODE_REPLACE;pub.serial=context.game_serial;CHECK(ammo::publish(7,&pub)==KILN_OK);
    backpack_reads=backpack_reads_999=0;selector(groups,acc,0);CHECK(backpack_reads==2&&backpack_reads_999==1);
    selector(groups,acc,0);selector(groups,acc,0);CHECK(backpack_reads==2);
    // An Append publication that provides none of the items the game lists with stock reads nothing.
    {
        KilnUiAmmoRow only[]={{555,12,0,3}};game_555_count=0;selector(groups,acc,0);
        CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK);
        KilnUiAmmoPublish append{sizeof append,KUA_KIND_ARROW,KUA_MODE_APPEND,1,9,0,context.game_serial,only};
        CHECK(ammo::publish(7,&append)==KILN_OK);backpack_reads=0;selector(groups,acc,0);CHECK(backpack_reads==0);
    }
    // Append as Ui.AmmoList publishes it since 0.2.0-dev.31.12 (GitHub issue 145): a mod's row of an item the game lists
    // itself goes in behind the game's row (the HUD shows the game's row and count), so a pick of it is known as the mod's
    // row. The quiver lists 555, whose stack the backpack holds (the game's row 555), and 556, which only it holds.
    {
        ammo::catalog[0]={999,1257792166u,555,556};
        KilnUiAmmoRow quiver[]={{555,30,0,0},{556,4,0,1}};
        KilnUiAmmoPublish append{sizeof append,KUA_KIND_ARROW,KUA_MODE_APPEND,2,9,0,0,quiver};
        auto bind=[&] {
            selector(groups,acc,0);CHECK(ammo::context(7,KUA_KIND_ARROW,&context)==KILN_OK&&context.ok);
            append.serial=context.game_serial;CHECK(ammo::publish(7,&append)==KILN_OK);selector(groups,acc,0);
        };
        game_555_count=3;backpack_answer=1;backpack_reads=backpack_reads_999=0;bind();
        CHECK(backpack_reads==1&&backpack_reads_999==0); // one walk, for the provided row with stock
        CHECK(groups[0].count==4&&groups[0].data[2].item==555&&groups[0].data[2].count==3&&groups[0].data[3].item==556&&
              groups[0].data[3].count==4); // the game's 555 with its own count; the quiver's 556 after the game's rows
        // Shots: only 555's count changes. New rows each time, so one walk each at the bind (no answer carries over).
        game_555_count=2;backpack_reads=0;bind();CHECK(backpack_reads==1);
        game_555_count=1;bind();CHECK(backpack_reads==2);
        // The real stocked game row: the game's own pick, exactly once, and no event.
        equip_calls=0;backpack_answer=1;ammo::pick(groups,nullptr,0,555,700);CHECK(equip_calls==1);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
        // The quiver's server half swaps 556 in and 555 out. Before the game builds its rows again 555 still shows count
        // 1: the pick raises Picked for the quiver's row, and the game's equip (which would empty the slot) never runs.
        backpack_answer=0;ammo::pick(groups,nullptr,0,555,701);CHECK(equip_calls==1);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555&&picks[0].count==30&&picks[0].row==0);
        // The game's rebuild keeps 555 at count 0; before Kiln.Ui publishes for the new rows, the game's own rows show.
        game_555_count=0;selector(groups,acc,0);CHECK(groups[0].count==3&&groups[0].data[2].item==555&&groups[0].data[2].count==0);
        ammo::pick(groups,nullptr,0,555,702);CHECK(equip_calls==1);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555&&picks[0].row==0);
        // Kiln.Ui's publication for them (555's row again behind the game's count-0 row): as issue 131 decides it.
        bind();ammo::pick(groups,nullptr,0,555,703);CHECK(equip_calls==1);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==555);
        // The stack comes back with stock: read again once, then the game's.
        game_555_count=4;backpack_answer=1;backpack_reads=0;bind();CHECK(backpack_reads==1);
        ammo::pick(groups,nullptr,0,555,704);CHECK(equip_calls==2);
        // More stock than before: read again, as any new rows.
        game_555_count=7;backpack_reads=0;bind();CHECK(backpack_reads==1);
        // A row the backpack read did not find (issue 127: a reader that cannot see that stock) is read again when the rows
        // change, and stays the game's.
        game_555_count=0;bind();game_555_count=6;backpack_answer=0;backpack_reads=0;bind();CHECK(backpack_reads==1);
        game_555_count=5;backpack_reads=0;bind();CHECK(backpack_reads==1);
        ammo::pick(groups,nullptr,0,555,705);CHECK(equip_calls==3);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
        // Another client generation reads again too.
        backpack_answer=1;bind();++ammo_client_state.generation;game_555_count=4;backpack_reads=0;bind();CHECK(backpack_reads==1);
        --ammo_client_state.generation;bind();
        // The quiver's own row 556 (not in the backpack) is the quiver's, as always.
        backpack_answer=0;ammo::pick(groups,nullptr,0,556,706);CHECK(equip_calls==3);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==556&&picks[0].row==1);
        ammo::catalog[0]={999,1257792166u,555};
    }
    // New rows are read again even when a count only fell, in either mode: the stack the read found may have left while a
    // smaller one the reader cannot see came in or stayed (issue 127's case, as pick 503). A held answer carried from the
    // rows before would send that pick to the mod; the read finds no stack, so the pick is the game's, which equips it.
    for(const uint32_t mode:{KUA_MODE_REPLACE,KUA_MODE_APPEND}) {
        game_555_count=3;backpack_answer=1;republish(mode); // bound: the read found the stacks of 555 and 999
        game_555_count=2;backpack_answer=0;backpack_reads=0;republish(mode);
        CHECK(backpack_reads==2); // 555 (fell) and 999 (same count) in the one walk of the new rows
        equip_calls=0;ammo::pick(groups,nullptr,0,555,510);CHECK(equip_calls==1);
        ammo::pick(groups,nullptr,0,999,511);CHECK(equip_calls==2);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    }
    backpack_answer=0;
    // A debug line for every pick of a provided row that went to the game, at most 8 a second, from the client Post tick.
    log_lines.clear();ammo::handoff_window={};ammo::report_post(nullptr,nullptr); // the lines of the cases above
    CHECK(!log_lines.empty()&&log_lines[0].first==KILN_LOG_DEBUG);
    CHECK(std::count_if(log_lines.begin(),log_lines.end(),[](const auto& l){return l.first==KILN_LOG_DEBUG;})<=8);
    log_lines.clear();ammo::handoff_window={};
    game_555_count=1;backpack_answer=1;republish(KUA_MODE_REPLACE);
    ammo::pick(groups,nullptr,0,555,600);ammo::report_post(nullptr,nullptr);
    CHECK(log_lines.size()==1&&log_lines[0].first==KILN_LOG_DEBUG);
    CHECK(log_lines[0].second.rfind("ui.ammo: a pick of arrow 0x0000022b (a row a mod provided) went to the game: game row count 1 "
        "(list revision ",0)==0);
    CHECK(log_lines[0].second.find("; backpack: holds it")!=std::string::npos);
    CHECK(log_lines[0].second.find(" ms, 0 client steps before the pick)")!=std::string::npos);
    log_lines.clear();game_555_count=2;backpack_answer=0;republish(KUA_MODE_REPLACE);
    ammo::pick(groups,nullptr,0,555,601);ammo::pick(groups,nullptr,0,1257792166u,602);ammo::report_post(nullptr,nullptr);
    CHECK(log_lines.size()==1); // an unprovided row's pick is no mod's: no line
    CHECK(log_lines[0].second.find("; backpack: not read (the list's build did not find it there)")!=std::string::npos);
    log_lines.clear();ammo::handoff_window={};for(unsigned i=0;i<20;++i)ammo::pick(groups,nullptr,0,555,610+i);
    ammo::report_post(nullptr,nullptr);ammo::report_post(nullptr,nullptr);
    CHECK(log_lines.size()==8&&log_lines[7].second.find("; 12 more such picks not logged")!=std::string::npos);
    CHECK(log_lines[6].second.find("more such picks")==std::string::npos);
    log_lines.clear();for(unsigned i=0;i<10;++i)ammo::pick(groups,nullptr,0,555,640+i);
    ammo::report_post(nullptr,nullptr);CHECK(log_lines.empty()); // 8 lines this second already
    ammo::handoff_window={};ammo::report_post(nullptr,nullptr); // the next second
    CHECK(log_lines.size()==8&&log_lines[7].second.find("; 2 more such picks not logged")!=std::string::npos);
    backpack_answer=0;
    game_lists_555=false;
    // GitHub issue 151 (Grimoire, a spellbook in Replace): the provider's rows and Kiln's cursor stay up over the game's
    // newer build of the list until Kiln.Ui publishes for it, instead of the game's own rows and its selection (the
    // equipped spell) for a frame, or on a dedicated server for the round trip of a swap.
    {
        ammo::catalog[1]={999,1257792166u,555,556};ammo::handoff_window={};ammo::report_post(nullptr,nullptr);log_lines.clear();
        KilnUiAmmoPublish arrows{sizeof arrows,KUA_KIND_ARROW,KUA_MODE_GAME,0,0,0,0,nullptr};CHECK(ammo::publish(7,&arrows)==KILN_OK);
        auto spell_context=[&] {
            KilnUiAmmoContext c{};c.size=sizeof c;CHECK(ammo::context(7,KUA_KIND_SPELL,&c)==KILN_OK&&c.ok);return c;
        };
        auto infos=[&]{return std::count_if(log_lines.begin(),log_lines.end(),[](const auto& l){return l.first==KILN_LOG_INFO;});};
        selector(groups,acc,0);auto c=spell_context();
        KilnUiAmmoRow book[]={{555,1,0,10},{556,1,0,11},{999,1,0,12}};
        KilnUiAmmoPublish spells{sizeof spells,KUA_KIND_SPELL,KUA_MODE_REPLACE,3,9,0,c.game_serial,book};
        CHECK(ammo::publish(7,&spells)==KILN_OK);selector(groups,acc,0);
        CHECK(groups[1].count==3&&groups[1].data[0].item==555&&groups[1].data[1].item==556&&groups[1].data[2].item==999);
        // The player picks 556 (a stored spell: the mod's swap), then scrolls on to 555 while the swap is under way.
        equip_calls=0;backpack_answer=0;ammo::pick(groups,nullptr,1,556,800);ammo::pick(groups,nullptr,1,555,801);CHECK(equip_calls==0);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==2&&picks[0].item==556&&picks[1].item==555);
        CHECK(groups[1].has_selected&&groups[1].data[groups[1].selected].item==555);
        // The swap comes back: 556 is in the backpack, 999 went to the storage (the game keeps its row at 0). The game
        // builds a new revision and selects its first row; Kiln.Ui has not published for it yet.
        spell_swapped=true;backpack_answer=1;selector(groups,acc,0);
        const auto rebuilt=spell_context();CHECK(rebuilt.game_serial>c.game_serial&&rebuilt.game_count==3);
        CHECK(groups[1].count==3&&groups[1].data[0].item==555&&groups[1].data[1].item==556&&groups[1].data[2].item==999);
        CHECK(groups[1].has_selected&&groups[1].data[groups[1].selected].item==555); // Kiln's cursor, not the game's selection
        selector(groups,acc,0); // another frame before the publication
        CHECK(groups[1].count==3&&groups[1].data[0].item==555&&groups[1].data[groups[1].selected].item==555);
        // Picks of the kept rows are decided against the new rows (GitHub issue 145): 556 is the game's (the backpack holds
        // its stack), 999 (kept at 0, in the storage now) the mod's, keyed by the publication that is up.
        ammo::pick(groups,nullptr,1,556,802);CHECK(equip_calls==1);
        backpack_answer=0;ammo::pick(groups,nullptr,1,999,803);CHECK(equip_calls==1);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==999&&picks[0].row==12&&picks[0].kind==KUA_KIND_SPELL);
        CHECK(groups[1].data[groups[1].selected].item==999);
        // Kiln.Ui publishes for the new revision: its rows show, and the cursor stays on its item.
        KilnUiAmmoRow next[]={{556,1,0,20},{555,1,0,21},{999,1,0,22}};
        spells.rows=next;spells.serial=rebuilt.game_serial;CHECK(ammo::publish(7,&spells)==KILN_OK);selector(groups,acc,0);
        CHECK(groups[1].count==3&&groups[1].data[0].item==556&&groups[1].data[1].item==555&&groups[1].data[2].item==999);
        CHECK(groups[1].data[groups[1].selected].item==999);
        CHECK(groups[0].data[0].item!=556); // the arrow list is not touched
        // A keep is bounded: past keep_limit without a publication the game's own rows show again, as before, and the
        // post tick logs it once. The next publication shows the mods' rows again.
        spell_swapped=false;selector(groups,acc,0);CHECK(groups[1].count==3&&groups[1].data[0].item==556);
        ammo::keeps[1].since-=std::chrono::seconds(2);selector(groups,acc,0);
        CHECK(groups[1].count==2&&groups[1].data[0].item==999&&groups[1].data[1].item==1257792166u);
        selector(groups,acc,0);CHECK(groups[1].count==2&&groups[1].data[0].item==999);
        ammo::report_post(nullptr,nullptr);CHECK(infos()==1);
        CHECK(std::any_of(log_lines.begin(),log_lines.end(),[](const auto& l){return l.first==KILN_LOG_INFO&&
            l.second.rfind("ui.ammo: the spell list shows the game's own rows: the mods' rows of list revision ",0)==0&&
            l.second.find("for 1000 ms")!=std::string::npos;}));
        log_lines.clear();ammo::report_post(nullptr,nullptr);CHECK(infos()==0);
        c=spell_context();spells.serial=c.game_serial;CHECK(ammo::publish(7,&spells)==KILN_OK);selector(groups,acc,0);
        CHECK(groups[1].count==3&&groups[1].data[0].item==556);
        spell_swapped=true;selector(groups,acc,0);CHECK(groups[1].count==3&&groups[1].data[0].item==556); // a new window
        // The line names the revisions the rows were kept over, not a build that ended the keep with rows of its own.
        {
            const auto kept=spell_context().game_serial;spell_swapped=false;ammo::keeps[1].since-=std::chrono::seconds(2);
            selector(groups,acc,0);CHECK(groups[1].count==2&&groups[1].data[0].item==999&&spell_context().game_serial>kept);
            log_lines.clear();ammo::report_post(nullptr,nullptr);CHECK(infos()==1);
            const auto range="(revisions "+std::to_string(kept)+" to "+std::to_string(kept)+")";
            CHECK(std::any_of(log_lines.begin(),log_lines.end(),[&](const auto& l){return l.second.find(range)!=std::string::npos;}));
            log_lines.clear();
        }
        // Not kept: another held weapon, another client generation, an Append publication, a cleared list.
        auto replace_current=[&](uint32_t mode) {
            selector(groups,acc,0);c=spell_context();spells.mode=mode;spells.serial=c.game_serial;
            CHECK(ammo::publish(7,&spells)==KILN_OK);selector(groups,acc,0);
        };
        auto game_rows=[&]{return groups[1].data[0].item==999&&std::none_of(groups[1].data,groups[1].data+groups[1].count,[](const SelectorRow& r){return r.item==555;});};
        replace_current(KUA_MODE_REPLACE);CHECK(groups[1].data[0].item==556);
        ++item;spell_swapped=!spell_swapped;selector(groups,acc,0);CHECK(game_rows());--item;
        replace_current(KUA_MODE_REPLACE);
        ++ammo_client_state.generation;spell_swapped=!spell_swapped;selector(groups,acc,0);CHECK(game_rows());
        --ammo_client_state.generation;replace_current(KUA_MODE_REPLACE);
        replace_current(KUA_MODE_APPEND);CHECK(groups[1].count==4&&groups[1].data[3].item==555);
        spell_swapped=!spell_swapped;selector(groups,acc,0);CHECK(game_rows());
        replace_current(KUA_MODE_REPLACE);spell_swapped=!spell_swapped;selector(groups,acc,0);CHECK(groups[1].data[0].item==556);
        KilnUiAmmoPublish clear{sizeof clear,KUA_KIND_SPELL,KUA_MODE_GAME,0,0,0,0,nullptr};CHECK(ammo::publish(7,&clear)==KILN_OK);
        selector(groups,acc,0);CHECK(game_rows());
        // A retired owner's kept rows go at the next build too.
        replace_current(KUA_MODE_REPLACE);spell_swapped=!spell_swapped;selector(groups,acc,0);CHECK(groups[1].data[0].item==556);
        ammo::retire(7);selector(groups,acc,0);CHECK(game_rows());
        CHECK(ammo::keeps[0].started==false&&ammo::keeps[1].started==false);
        spell_swapped=false;ammo::catalog[1]={999,1257792166u,555};
        ammo::handoff_window={};ammo::report_post(nullptr,nullptr);CHECK(infos()==0);log_lines.clear();
    }
    // GitHub issue 151, the flicker 31.21 left (Fuzen777, Grimoire 1.2.3): the picker moved to the new spell, showed the one
    // before it for a moment, then went back. The game's selection sync (client_cycle_inventory_stock: the action 0x40cf00,
    // whenever it runs) selects what the game's reconciliation names, the equipped spell until the mod's swap arrives, since
    // a pick Kiln takes for a mod writes no pending pick of the game's. Kiln holds its pick over it, and over a build.
    {
        ammo::catalog[1]={999,1257792166u,555,556};log_lines.clear();
        CHECK(ammo::action_hook.install(&fake,child,23,reinterpret_cast<void*>(&ammo::select_action),"ammo select action fixture"));
        auto spell_context=[&] {
            KilnUiAmmoContext c{};c.size=sizeof c;CHECK(ammo::context(7,KUA_KIND_SPELL,&c)==KILN_OK&&c.ok);return c;
        };
        KilnUiAmmoRow book[]={{555,1,0,10},{556,1,0,11},{999,1,0,12}};
        KilnUiAmmoPublish spells{sizeof spells,KUA_KIND_SPELL,KUA_MODE_REPLACE,3,9,0,0,book};
        auto bind=[&] {
            selector(groups,acc,0);spells.serial=spell_context().game_serial;CHECK(ammo::publish(7,&spells)==KILN_OK);selector(groups,acc,0);
            CHECK(groups[1].count==3&&groups[1].data[0].item==555&&groups[1].data[1].item==556&&groups[1].data[2].item==999);
        };
        auto cursor=[&]{return groups[1].has_selected&&groups[1].selected<groups[1].count?groups[1].data[groups[1].selected].item:0u;};
        ActionResult r{};
        auto sync=[&]{action_calls=0;ammo::select_action(&r,groups+1,1,nullptr,nullptr,nullptr,nullptr,nullptr);CHECK(action_calls==1);return cursor();};
        bind();equipped_item=999;
        CHECK(sync()==999&&r.code==4); // no pick: the sync is the game's own
        // The player scrolls to 556, a stored spell: the mod's swap (Picked); the game's equip is kept back.
        equip_calls=0;backpack_answer=0;ammo::pick(groups,nullptr,1,556,900);CHECK(equip_calls==0&&cursor()==556);
        CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==1&&picks[0].item==556);
        // The sync runs before the swap arrives and names the equipped 999: the cursor stays on 556, and the build keeps it.
        CHECK(sync()==556&&r.code==4);
        selector(groups,acc,0);CHECK(cursor()==556);CHECK(sync()==556);
        // Scrolling on to 555 while the swap is under way: the cursor stays where the player is.
        ammo::pick(groups,nullptr,1,555,901);CHECK(sync()==555);
        // The swap of 556 arrives first (the game equips it): not Kiln's last pick, so the cursor stays on 555.
        equipped_item=556;CHECK(sync()==555);selector(groups,acc,0);CHECK(cursor()==555);
        // The swap of 555 arrives: the sync names 555 itself, and the hold ends. From then on the sync is the game's again.
        equipped_item=555;CHECK(sync()==555&&ammo::holds[1].item==0&&!ammo::holding.load());
        equipped_item=999;CHECK(sync()==999);
        // A hold lasts 1 s at most: a swap that never comes leaves the equipped spell, as before.
        ammo::pick(groups,nullptr,1,556,902);CHECK(sync()==556);
        ammo::holds[1].since-=std::chrono::seconds(2);CHECK(sync()==999&&ammo::holds[1].item==0);
        // A repeat of the held item keeps the hold's start (the game's own automatic picks of the cursor's row are repeats).
        ammo::pick(groups,nullptr,1,556,903);const auto since=ammo::holds[1].since;ammo::pick(groups,nullptr,1,556,904);
        CHECK(ammo::holds[1].since==since);
        // A pick that goes to the game ends the hold: its own pending pick holds the cursor from there (999 is in the backpack).
        backpack_answer=1;ammo::pick(groups,nullptr,1,999,905);CHECK(equip_calls==1&&ammo::holds[1].item==0);backpack_answer=0;
        // So does a pick of a row no mod lists.
        ammo::pick(groups,nullptr,1,556,906);CHECK(ammo::holds[1].item==556);
        ammo::pick(groups,nullptr,1,1257792166u,907);CHECK(equip_calls==2&&ammo::holds[1].item==0);
        // Another client session, a GAME clear, the owner's retirement, the world's client event: no hold.
        ammo::pick(groups,nullptr,1,556,908);++ammo_client_state.client_session;equipped_item=999;
        ammo::select_action(&r,groups+1,1,nullptr,nullptr,nullptr,nullptr,nullptr);CHECK(ammo::holds[1].item==0&&cursor()==999);
        --ammo_client_state.client_session;
        ammo::pick(groups,nullptr,1,556,909);CHECK(ammo::holds[1].item==556);
        KilnUiAmmoPublish clear{sizeof clear,KUA_KIND_SPELL,KUA_MODE_GAME,0,0,0,0,nullptr};CHECK(ammo::publish(7,&clear)==KILN_OK);
        CHECK(ammo::holds[1].item==0);
        bind();ammo::pick(groups,nullptr,1,556,910);ammo::retire(7);CHECK(ammo::holds[1].item==0);
        bind();ammo::pick(groups,nullptr,1,556,911);ammo::lifecycle(&world_event,nullptr);CHECK(ammo::holds[1].item==0&&!ammo::holding.load());
        ammo::catalog[0]={999,1257792166u,555};ammo::catalog[1]={999,1257792166u,555,556};ammo::catalog_session=9;
        // Nor another world or player in the same client session (review of 0.2.0-dev.31.23).
        bind();ammo::pick(groups,nullptr,1,556,9111);CHECK(ammo::holds[1].item==556);++ammo_client_state.generation;
        CHECK(sync()==999&&ammo::holds[1].item==0&&!ammo::holding.load());--ammo_client_state.generation;
        bind();ammo::pick(groups,nullptr,1,556,9112);CHECK(ammo::holds[1].item==556);++ammo_client_state.local_player;
        CHECK(sync()==999&&ammo::holds[1].item==0&&!ammo::holding.load());--ammo_client_state.local_player;
        // Only while Kiln's rows are up: over the game's own rows (no binding) the sync's choice is the game's.
        bind();ammo::pick(groups,nullptr,1,556,912);spell_swapped=true;selector(groups,acc,0); // kept over the swap's build
        ammo::keeps[1].since-=std::chrono::seconds(2);selector(groups,acc,0);
        CHECK(groups[1].count==3&&groups[1].data[2].item==556); // past keep_limit: the game's rows
        CHECK(sync()==999);
        // A hold whose rows are gone still ends at hold_limit: the sync goes back to one load (review of 0.2.0-dev.31.23).
        CHECK(ammo::holds[1].item==556&&ammo::holding.load());ammo::holds[1].since-=std::chrono::seconds(2);
        CHECK(sync()==999&&ammo::holds[1].item==0&&!ammo::holding.load());spell_swapped=false;
        // The other list: an arrow pick is held the same way, and a spell sync never touches it.
        {
            KilnUiAmmoRow quiver[]={{555,30,0,0},{999,9,0,1}};
            selector(groups,acc,0);KilnUiAmmoContext a{};a.size=sizeof a;CHECK(ammo::context(7,KUA_KIND_ARROW,&a)==KILN_OK);
            KilnUiAmmoPublish arrows{sizeof arrows,KUA_KIND_ARROW,KUA_MODE_REPLACE,2,9,0,a.game_serial,quiver};
            CHECK(ammo::publish(7,&arrows)==KILN_OK);selector(groups,acc,0);CHECK(groups[0].data[0].item==555);
            ammo::pick(groups,nullptr,0,555,913);CHECK(ammo::holds[0].item==555);
            ammo::select_action(&r,groups+0,0,nullptr,nullptr,nullptr,nullptr,nullptr);
            CHECK(groups[0].has_selected&&groups[0].data[groups[0].selected].item==555);
            KilnUiAmmoPublish none{sizeof none,KUA_KIND_ARROW,KUA_MODE_GAME,0,0,0,0,nullptr};CHECK(ammo::publish(7,&none)==KILN_OK);
            CHECK(ammo::holds[0].item==0);
        }
        // The build keeps a held pick also when the sync ran without Kiln (a build where ui/AmmoSelectAction did not resolve).
        bind();ammo::pick(groups,nullptr,1,556,914);
        groups[1].selected=2;CHECK(cursor()==999); // the game's sync, as on a build without the hook
        selector(groups,acc,0);CHECK(cursor()==556);
        // A cursor whose item the new rows no longer list takes the game's own selection, never none (which the HUD draws
        // at its remembered index): the provider's next rows drop 556.
        {
            KilnUiAmmoRow fewer[]={{555,1,0,10},{999,1,0,12}};
            spells.rows=fewer;spells.count=2;spells.serial=spell_context().game_serial;CHECK(ammo::publish(7,&spells)==KILN_OK);
            selector(groups,acc,0);CHECK(groups[1].count==2&&groups[1].has_selected&&cursor()==999&&ammo::holds[1].item==0);
            spells.rows=book;spells.count=3;
        }
        // The pick trace (ammo_trace.h): off, nothing; on, one line per stage of a pick, under one pick number and frame count.
        {
            ammo::trace.reset();bind();ammo::pick(groups,nullptr,1,556,915);sync();ammo::report_post(nullptr,nullptr);
            CHECK(std::none_of(log_lines.begin(),log_lines.end(),[](const auto& l){return l.second.rfind("ui.ammo trace:",0)==0;}));
            CHECK(ammo::draw_hook.install(&fake,child,24,reinterpret_cast<void*>(&ammo::draw),"ammo list draw fixture"));
            ammo::trace.enable(true);ammo::trace.reset();bind();equipped_item=999;log_lines.clear();draw_calls=0;
            ammo::draw(groups+1);CHECK(draw_calls==1); // before a pick: no line
            fake_hooks.caller=&hook_caller;game_caller=0xc7c1f;ammo::reveal_sites.cycle=0xc7c1e;ammo::pick(groups,nullptr,1,555,916);sync();selector(groups,acc,0);ammo::draw(groups+1);
            groups[1].has_selected=0;ammo::draw(groups+1);groups[1].has_selected=1;
            finalizer_spell=999;ammo::finalizer(groups,nullptr,nullptr,nullptr,nullptr,nullptr);finalizer_spell=0;
            ammo::report_post(nullptr,nullptr);
            std::vector<std::string> trace;for(const auto& l:log_lines)if(l.second.rfind("ui.ammo trace:",0)==0)trace.push_back(l.second);
            CHECK(trace.size()==6);
            CHECK(trace[0].rfind("ui.ammo trace: spell #1 +0.0 ms frame 1: pick 0x0000022b from elsewhere in the game (not its scroll or direct selection): the mod's "
                "(Picked; the game's equip kept back); cursor 0x0000022b index 0 of 3",0)==0);
            CHECK(trace[1].find(" frame 1: sync: before 0x0000022b index 0 of 3; the game's 0x000003e7 index 2 of 3; after 0x0000022b "
                "index 0 of 3 (Kiln held its pick of ")!=std::string::npos);
            CHECK(trace[2].find(" frame 1: build: entry 0x0000022b index 0 of 3; the game's 0x000003e7 index 0 of 2; exit 0x0000022b "
                "index 0 of 3; rows: the mods' (revision ")!=std::string::npos);
            CHECK(trace[3].find(" frame 2: draw: index 0 of 3, 0x0000022b (the selection)")!=std::string::npos);
            CHECK(trace[4].find(" frame 3: draw: index 0 of 3, 0x0000022b (the remembered index: no selection)")!=std::string::npos);
            CHECK(trace[5].find(" frame 3: final: before 0x0000022b index 0 of 3; the game's 0x000003e7 index 2 of 3; after 0x0000022b "
                "index 0 of 3")!=std::string::npos);
            ammo::trace.enable(false);ammo::trace.reset();ammo::reveal_sites={};ammo::draw_hook={};fake_hooks.caller=nullptr;
        }
        ammo::retire(7);ammo::action_hook={};equipped_item=0;log_lines.clear();
        ammo::catalog[1]={999,1257792166u,555};
    }
    ammo::retire(7);CHECK(ammo::take_picks(7,picks,64,&written)==KILN_OK&&written==0);
    CHECK(kiln_tools_remove_family(7,tool_handle)==KILN_OK);kiln_tools_presentation_set_held_provider(nullptr);
    ammo::stop();ammo::backpack_probe=nullptr;api=nullptr;child=retiring=0;
    std::cout<<"ammo native hooks: "<<checks<<" checks passed (includes presentation fixture)\n";
}
