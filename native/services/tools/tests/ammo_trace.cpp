// SPDX-License-Identifier: MIT
// kiln.ui.ammo's pick trace (GitHub issue 151): the recorder's window, repeats, ring and line budget, the lines, the switch.
#include "../ammo_trace.h"
#include "../ammo_trace_setting.h"
#include <cstdlib>
#include <iostream>
using namespace kiln::tools;
static unsigned checks;
#define CHECK(x) do {++checks;if(!(x)){std::cerr<<__LINE__<<": " #x "\n";std::exit(1);}}while(false)
static bool has(const std::vector<std::string>& lines,const std::string& text) {
    for(const auto& l:lines)if(l.find(text)!=std::string::npos)return true;
    return false;
}
int main() {
    using namespace std::chrono_literals;
    const auto t0=AmmoTrace::clock::now();
    AmmoCursor nine_3{0x1a2b3c4du,3,9,true,true},nine_0{0x11111111u,0,9,true,true};
    {
        AmmoTrace t;
        // Off: nothing is recorded, not even a pick.
        AmmoTraceEvent pick{};pick.kind=1;pick.item=0x1a2b3c4du;pick.site=AmmoPickSite::Cycle;pick.route=AmmoPickRoute::Mod;pick.at[0]=nine_3;
        t.pick(pick,t0);CHECK(!t.pending()&&t.size()==0);
        t.enable(true);
        // Outside a pick's window a stage records nothing: the build, sync and draw run every frame.
        AmmoTraceEvent draw{};draw.stage=AmmoTraceStage::Draw;draw.kind=1;draw.at[0]=nine_0;
        t.record(draw,t0);CHECK(t.size()==0);
        t.next_frame();t.pick(pick,t0);CHECK(t.size()==1);
        // A stage records again only when what it records changed; the time and frame are not part of that.
        t.next_frame();t.record(draw,t0+16ms);t.next_frame();t.record(draw,t0+33ms);CHECK(t.size()==2);
        draw.at[0]=nine_3;t.next_frame();t.record(draw,t0+50ms);CHECK(t.size()==3);
        draw.at[0]={0x1a2b3c4du,3,9,false,true};draw.remembered=true;t.record(draw,t0+51ms);CHECK(t.size()==4);
        // Each list has its own window: an arrow stage does not record inside the spell's.
        AmmoTraceEvent arrow=draw;arrow.kind=0;t.record(arrow,t0+52ms);CHECK(t.size()==4);
        // Past 2 s the window closes.
        draw.at[0]=nine_0;draw.remembered=false;t.record(draw,t0+2100ms);CHECK(t.size()==4);
        AmmoTraceEvent build{};build.stage=AmmoTraceStage::Build;build.kind=1;build.at={nine_3,nine_0,nine_3};
        build.rows=AmmoTraceRows::Kept;build.shown=40;build.revision=41;build.kept_ms=16;t.record(build,t0+40ms);
        AmmoTraceEvent sync{};sync.stage=AmmoTraceStage::Sync;sync.kind=1;sync.at={nine_3,nine_0,nine_3};sync.held=true;sync.held_ms=16;
        t.record(sync,t0+41ms);
        AmmoTraceEvent fin{};fin.stage=AmmoTraceStage::Final;fin.kind=1;fin.at={nine_3,nine_3,nine_3};fin.landed=true;t.record(fin,t0+95ms);
        AmmoTraceEvent game{};game.stage=AmmoTraceStage::Build;game.kind=1;game.at={nine_3,nine_0,nine_0};game.rows=AmmoTraceRows::Game;
        game.why=AmmoRowsWhy::KeepEnded;game.revision=42;t.record(game,t0+1200ms);
        const auto lines=t.take(t0+1300ms);CHECK(lines.size()==8&&!t.pending());
        CHECK(lines[0]=="ui.ammo trace: spell #1 +0.0 ms frame 1: pick 0x1a2b3c4d from a scroll (the game's cycle): the mod's "
            "(Picked; the game's equip kept back); cursor 0x1a2b3c4d index 3 of 9");
        CHECK(lines[1]=="ui.ammo trace: spell #1 +16.0 ms frame 2: draw: index 0 of 9, 0x11111111 (the selection)");
        CHECK(lines[2].find("+50.0 ms frame 4: draw: index 3 of 9, 0x1a2b3c4d (the selection)")!=std::string::npos);
        CHECK(lines[3].find("draw: index 3 of 9, 0x1a2b3c4d (the remembered index: no selection)")!=std::string::npos);
        CHECK(lines[4]=="ui.ammo trace: spell #1 +40.0 ms frame 4: build: entry 0x1a2b3c4d index 3 of 9; the game's 0x11111111 index 0 of 9; "
            "exit 0x1a2b3c4d index 3 of 9; rows: the mods' of revision 40 kept over revision 41 (since 16 ms)");
        CHECK(lines[5]=="ui.ammo trace: spell #1 +41.0 ms frame 4: sync: before 0x1a2b3c4d index 3 of 9; the game's 0x11111111 index 0 of 9; "
            "after 0x1a2b3c4d index 3 of 9 (Kiln held its pick of 16 ms ago)");
        CHECK(lines[6].find("final: before 0x1a2b3c4d index 3 of 9; the game's 0x1a2b3c4d index 3 of 9; after 0x1a2b3c4d index 3 of 9 "
            "(the game's choice is Kiln's pick: the hold ends)")!=std::string::npos);
        CHECK(lines[7].find("+1200.0 ms frame 4: build: entry 0x1a2b3c4d index 3 of 9; the game's 0x11111111 index 0 of 9; exit 0x11111111 "
            "index 0 of 9; rows: the game's own (revision 42): the kept rows ended (1 s without a publication for the newer builds)")!=std::string::npos);
        // A new pick numbers on and records every stage afresh.
        pick.kind=0;pick.item=0x22u;pick.site=AmmoPickSite::Game;pick.route=AmmoPickRoute::Game;pick.at[0]={0x22u,1,2,true,true};
        t.pick(pick,t0+3s);arrow.at[0]=nine_0;t.record(arrow,t0+3s+1ms);
        AmmoTraceEvent unread{};unread.stage=AmmoTraceStage::Sync;unread.kind=0;t.record(unread,t0+3s+2ms);
        AmmoTraceEvent nosel{};nosel.stage=AmmoTraceStage::Final;nosel.kind=0;nosel.at[0]={0,5,4,true,true};nosel.at[1]={0,2,4,false,true};
        t.record(nosel,t0+3s+3ms);
        const auto more=t.take(t0+3s+10ms);CHECK(more.size()==4);
        CHECK(more[0].rfind("ui.ammo trace: arrow #2 +0.0 ms frame 4: pick 0x00000022 from elsewhere in the game (not its scroll or direct selection): the game's (its own "
            "equip); cursor 0x00000022 index 1 of 2",0)==0);
        CHECK(more[2].find("sync: before unreadable; the game's unreadable; after unreadable")!=std::string::npos);
        CHECK(more[3].find("final: before index 5 of 4 (past the rows); the game's no selection (index 2 of 4); after unreadable")!=std::string::npos);
        for(auto [route,text]:{std::pair{AmmoPickRoute::NotProvided,": the game's (no mod lists it)"},
                               std::pair{AmmoPickRoute::Dropped,": nobody's (stale or unreadable rows: no equip, no event)"}}) {
            pick.route=route;pick.site=AmmoPickSite::Unknown;t.pick(pick,t0+4s);
            const auto l=t.take(t0+4s);CHECK(l.size()==1&&l[0].find(std::string("pick 0x00000022")+text)!=std::string::npos);
        }
        AmmoTraceEvent cur{};cur.stage=AmmoTraceStage::Build;cur.kind=0;cur.rows=AmmoTraceRows::Current;cur.revision=7;
        pick.site=AmmoPickSite::Select;t.pick(pick,t0+5s);t.record(cur,t0+5s);
        const auto l=t.take(t0+5s);CHECK(l.size()==2&&l[0].find(" from a direct selection:")!=std::string::npos&&
            l[1].find("; rows: the mods' (revision 7)")!=std::string::npos);
    }
    {
        // Bounded: a ring of 256 (the oldest go first), at most 100 lines a second, and the first line after a drop says so.
        AmmoTrace t;t.enable(true);
        AmmoTraceEvent pick{};pick.kind=1;
        for(unsigned i=0;i<300;++i){pick.item=i;t.pick(pick,t0);}
        CHECK(t.size()==256&&t.dropped()==44);
        auto lines=t.take(t0);CHECK(lines.size()==100&&t.pending());
        CHECK(lines[0]=="ui.ammo trace: 44 trace events were dropped (more than 256 waiting; at most 100 lines a second)");
        CHECK(lines[1].find("pick 0x0000002c")!=std::string::npos); // 44, the oldest kept
        CHECK(t.take(t0+500ms).empty());
        lines=t.take(t0+1s);CHECK(lines.size()==100);
        lines=t.take(t0+2s);CHECK(lines.size()==57&&!t.pending()&&lines.back().find("pick 0x0000012b")!=std::string::npos);
        t.reset();CHECK(t.size()==0&&t.frame()==0&&!t.pending());
    }
    {
        // A repeat of the window's pick (the same item, call site, taker and cursor after it) is not a new pick: the game's
        // own selection of the cursor's row runs every frame while its sync finds no row of the item it expects, and each
        // new pick would write every stage again (review of GitHub issue 151).
        AmmoTrace t;t.enable(true);
        AmmoTraceEvent pick{};pick.kind=1;pick.item=0x1a2b3c4du;pick.site=AmmoPickSite::Game;pick.route=AmmoPickRoute::Mod;pick.at[0]=nine_3;
        AmmoTraceEvent sync{};sync.stage=AmmoTraceStage::Sync;sync.kind=1;sync.at={nine_3,nine_0,nine_3};sync.held=true;
        for(unsigned i=0;i<60;++i){t.pick(pick,t0+i*16ms);t.record(sync,t0+i*16ms+1ms);}
        CHECK(t.size()==2);
        // Another call site, item, taker or cursor is a new pick, and so is the same pick once its window closed.
        pick.site=AmmoPickSite::Cycle;t.pick(pick,t0+1s);CHECK(t.size()==3);t.pick(pick,t0+1s+16ms);CHECK(t.size()==3);
        pick.item=0x22u;t.pick(pick,t0+1s+32ms);CHECK(t.size()==4);
        pick.route=AmmoPickRoute::Game;t.pick(pick,t0+1s+48ms);CHECK(t.size()==5);
        pick.at[0]=nine_0;t.pick(pick,t0+1s+64ms);CHECK(t.size()==6);
        t.pick(pick,t0+3s+100ms);CHECK(t.size()==7);
        const auto lines=t.take(t0+4s);CHECK(lines.size()==7&&lines[6].find("spell #6 +0.0 ms")!=std::string::npos);
    }
    // The switch: KILN_AMMO_TRACE (1 or 0) wins; else kiln.json "ui": {"ammoTrace": true}; comments allowed.
    CHECK(ammo_trace_setting(1,""));CHECK(!ammo_trace_setting(0,R"({"ui":{"ammoTrace":true}})"));
    CHECK(ammo_trace_setting(-1,R"({ // the player's
        "policy": "verified-only", "ui": { "overlay": "auto", /* testers */ "ammoTrace": true } })"));
    CHECK(!ammo_trace_setting(-1,R"({"ui":{"ammoTrace":"true"}})"));
    CHECK(!ammo_trace_setting(-1,R"({"ui":{"ammoTrace":false}})"));
    CHECK(!ammo_trace_setting(-1,R"({"client":{"ui":{"ammoTrace":true}}})")); // top level only
    CHECK(!ammo_trace_setting(-1,R"({"ammoTrace":true})"));
    CHECK(!ammo_trace_setting(-1,""));CHECK(!ammo_trace_setting(-1,"{not json"));
    std::cout<<"ammo trace: "<<checks<<" checks passed\n";
}
