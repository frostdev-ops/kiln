// SPDX-License-Identifier: MIT
#pragma once
// kiln.ui.ammo's pick trace (GitHub issue 151, Fuzen777). Off by default: kiln.json "ui": {"ammoTrace": true}, or the
// environment variable KILN_AMMO_TRACE=1 (=0 turns it off; it wins over kiln.json), read once at the game's start
// (ammo_trace_setting.h, tools.cpp). For each pick of an arrow or spell row, under that pick's number, with the HUD
// list's frame number (its draws so far) and the time since the pick, it records:
//   pick   the requested item, where it came from (the scroll and the direct selection by their return addresses; any
//          other call, such as the input handler's third pick 0x3afcce or the selection sync's own pick of the cursor's
//          row 0x2b4846, is "elsewhere"), who took it (the mod, the game, nobody) and the cursor after it;
//   build  the game's list build: the cursor at its entry (before Kiln puts the game's rows back), the game's own
//          selection after its build, the cursor at its exit and the rows it shows (the current publication, one kept
//          over a newer build, or the game's own and why);
//   final  the game's finalizer after the build: the game's choice, then the cursor after Kiln;
//   sync   the game's selection sync (client_cycle_inventory_stock): the cursor before it, the game's choice, then
//          the cursor after Kiln (and whether Kiln held its pick there);
//   draw   the HUD list draw: the index it drew, its item, and whether it drew its remembered index (no selection).
// Bounded: only for 2 s after a pick of that list; a stage records again only when what it records changed (the build,
// the sync and the draw run every frame), and a repeat of the window's pick is not a new pick; a ring of 256 events; the
// client Post tick writes at most 100 lines a second and says how many it dropped. Recording takes a small lock and never
// logs; off, every call site is one relaxed load and the draw is not hooked at all. Host test: tests/ammo_trace.cpp.
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>
#include <vector>
namespace kiln::tools {
// A group's selection as the HUD reads it: the selection-valid flag (+0x630), the index (+0x628), the row count and
// the item of that row (0 when the index is past the rows).
struct AmmoCursor {uint32_t item{};uint64_t index{},count{};bool valid{},readable{};};
inline bool same_cursor(const AmmoCursor& a,const AmmoCursor& b) {
    return a.readable==b.readable&&a.valid==b.valid&&a.index==b.index&&a.count==b.count&&a.item==b.item;
}
enum class AmmoTraceStage:uint8_t {Pick,Build,Final,Sync,Draw,Count};
enum class AmmoPickSite:uint8_t {Unknown,Cycle,Select,Game};
enum class AmmoPickRoute:uint8_t {Mod,Game,NotProvided,Dropped};
enum class AmmoTraceRows:uint8_t {None,Current,Kept,Game};
enum class AmmoRowsWhy:uint8_t {None,NoPublication,OtherSession,AppendWaits,Context,KeepEnded,Catalog,Full,Fault};
struct AmmoTraceEvent {
    AmmoTraceStage stage{};uint8_t kind{}; // 0 arrows, 1 spells
    uint64_t pick{},frame{};int64_t us{};  // filled by the recorder
    uint32_t item{};AmmoPickSite site{};AmmoPickRoute route{}; // pick
    // pick: [0] the cursor after it. build: [0] entry, [1] the game's, [2] exit. final, sync: [0] before, [1] the
    // game's, [2] after Kiln. draw: [0] the drawn index and its item.
    std::array<AmmoCursor,3> at{};
    AmmoTraceRows rows{};AmmoRowsWhy why{};uint64_t revision{},shown{};int64_t kept_ms{}; // build
    bool held{},landed{};int64_t held_ms{}; // sync, final: Kiln's pick held over the game's choice, or the game's choice is it
    bool remembered{};                       // draw: no selection, so the draw used its remembered index
};
// The parts of an event a repeat of its stage is compared by (not its numbers, times or ages).
inline bool same_event(const AmmoTraceEvent& a,const AmmoTraceEvent& b) {
    for(size_t i=0;i<a.at.size();++i)if(!same_cursor(a.at[i],b.at[i]))return false;
    return a.stage==b.stage&&a.kind==b.kind&&a.rows==b.rows&&a.why==b.why&&a.revision==b.revision&&a.shown==b.shown&&
        a.held==b.held&&a.landed==b.landed&&a.remembered==b.remembered;
}
inline std::string ammo_hex(uint32_t v) {char b[16];std::snprintf(b,sizeof b,"0x%08x",v);return b;}
inline std::string ammo_cursor_text(const AmmoCursor& c) {
    if(!c.readable)return "unreadable";
    const std::string of=" of "+std::to_string(c.count);
    if(!c.valid)return "no selection (index "+std::to_string(c.index)+of+")";
    if(c.index>=c.count)return "index "+std::to_string(c.index)+of+" (past the rows)";
    return ammo_hex(c.item)+" index "+std::to_string(c.index)+of;
}
inline const char* ammo_rows_why(AmmoRowsWhy why) {
    switch(why) {
    case AmmoRowsWhy::NoPublication: return "no mod publishes this list";
    case AmmoRowsWhy::OtherSession: return "the publication is of another client session";
    case AmmoRowsWhy::AppendWaits: return "Append: the mods' rows show once Kiln.Ui publishes for this revision";
    case AmmoRowsWhy::Context: return "another held weapon, player or client generation than the publication's";
    case AmmoRowsWhy::KeepEnded: return "the kept rows ended (1 s without a publication for the newer builds)";
    case AmmoRowsWhy::Catalog: return "the item catalog is not read";
    case AmmoRowsWhy::Full: return "the binding table is full";
    case AmmoRowsWhy::Fault: return "a fault";
    default: return "";
    }
}
inline std::string ammo_trace_line(const AmmoTraceEvent& e) {
    char head[96];
    std::snprintf(head,sizeof head,"ui.ammo trace: %s #%llu +%.1f ms frame %llu: ",e.kind?"spell":"arrow",
        static_cast<unsigned long long>(e.pick),double(e.us)/1000.0,static_cast<unsigned long long>(e.frame));
    std::string s(head);
    switch(e.stage) {
    case AmmoTraceStage::Pick: {
        s+="pick "+ammo_hex(e.item);
        s+=e.site==AmmoPickSite::Cycle?" from a scroll (the game's cycle)":e.site==AmmoPickSite::Select?" from a direct selection":
            e.site==AmmoPickSite::Game?" from elsewhere in the game (not its scroll or direct selection)":"";
        s+=e.route==AmmoPickRoute::Mod?": the mod's (Picked; the game's equip kept back)":e.route==AmmoPickRoute::Game?
            ": the game's (its own equip)":e.route==AmmoPickRoute::NotProvided?": the game's (no mod lists it)":
            ": nobody's (stale or unreadable rows: no equip, no event)";
        s+="; cursor "+ammo_cursor_text(e.at[0]);
        break;
    }
    case AmmoTraceStage::Build: {
        s+="build: entry "+ammo_cursor_text(e.at[0])+"; the game's "+ammo_cursor_text(e.at[1])+"; exit "+ammo_cursor_text(e.at[2]);
        s+="; rows: ";
        if(e.rows==AmmoTraceRows::Current)s+="the mods' (revision "+std::to_string(e.revision)+")";
        else if(e.rows==AmmoTraceRows::Kept)s+="the mods' of revision "+std::to_string(e.shown)+" kept over revision "+
            std::to_string(e.revision)+" (since "+std::to_string(e.kept_ms)+" ms)";
        else s+="the game's own (revision "+std::to_string(e.revision)+")"+(e.why!=AmmoRowsWhy::None?std::string(": ")+ammo_rows_why(e.why):"");
        break;
    }
    case AmmoTraceStage::Final: case AmmoTraceStage::Sync: {
        s+=e.stage==AmmoTraceStage::Final?"final: before ":"sync: before ";
        s+=ammo_cursor_text(e.at[0])+"; the game's "+ammo_cursor_text(e.at[1])+"; after "+ammo_cursor_text(e.at[2]);
        if(e.held)s+=" (Kiln held its pick of "+std::to_string(e.held_ms)+" ms ago)";
        else if(e.landed)s+=" (the game's choice is Kiln's pick: the hold ends)";
        break;
    }
    case AmmoTraceStage::Draw: {
        s+="draw: index "+std::to_string(e.at[0].index)+" of "+std::to_string(e.at[0].count);
        if(e.at[0].index<e.at[0].count)s+=", "+ammo_hex(e.at[0].item);else s+=" (past the rows)";
        s+=e.remembered?" (the remembered index: no selection)":" (the selection)";
        break;
    }
    default: break;
    }
    return s;
}
class AmmoTrace {
public:
    using clock=std::chrono::steady_clock;
    static constexpr size_t capacity=256;
    static constexpr auto window=std::chrono::seconds(2);
    static constexpr unsigned lines_per_second=100;
    void enable(bool on) noexcept {on_.store(on,std::memory_order_release);}
    bool on() const noexcept {return on_.load(std::memory_order_relaxed);}
    // The HUD list's draws so far: every event's frame number. The draw hook counts one before it records.
    uint64_t next_frame() noexcept {return frames_.fetch_add(1,std::memory_order_relaxed)+1;}
    uint64_t frame() const noexcept {return frames_.load(std::memory_order_relaxed);}
    // A pick of list e.kind: a new pick number, and that list's window starts again (every stage records afresh). A repeat
    // of the open window's pick (the same item, call site, taker and cursor after it) is not a new pick: the game's own
    // selection of the cursor's row runs every frame while its sync finds no row of the item it expects.
    void pick(AmmoTraceEvent e,clock::time_point now) noexcept {
        if(!on()||e.kind>1)return;
        std::lock_guard lock(m_);
        auto& k=kinds_[e.kind];e.stage=AmmoTraceStage::Pick;
        const auto s=size_t(AmmoTraceStage::Pick);const auto& last=k.last[s];
        if(k.pick&&now-k.at<=window&&k.has[s]&&last.item==e.item&&last.site==e.site&&last.route==e.route&&same_cursor(last.at[0],e.at[0]))return;
        k.pick=++picks_;k.at=now;k.has.fill(false);k.has[s]=true;k.last[s]=e;push(e,k,now);
    }
    // Any other stage: inside its list's window, and only when it differs from that stage's last event of the window.
    void record(AmmoTraceEvent e,clock::time_point now) noexcept {
        if(!on()||e.kind>1||e.stage==AmmoTraceStage::Pick||e.stage>=AmmoTraceStage::Count)return;
        std::lock_guard lock(m_);
        auto& k=kinds_[e.kind];
        if(!k.pick||now-k.at>window)return;
        const auto s=size_t(e.stage);
        if(k.has[s]&&same_event(k.last[s],e))return;
        k.has[s]=true;k.last[s]=e;push(e,k,now);
    }
    bool pending() const noexcept {return pending_.load(std::memory_order_acquire);}
    // The publisher (client Post): the lines it may write now, oldest first; the first line after a drop says so.
    std::vector<std::string> take(clock::time_point now) {
        std::vector<std::string> out;
        if(!pending())return out;
        std::lock_guard lock(m_);
        if(now-second_>=std::chrono::seconds(1)){second_=now;lines_=0;}
        if(dropped_&&lines_<lines_per_second) {
            out.push_back("ui.ammo trace: "+std::to_string(dropped_)+" trace events were dropped (more than "+
                std::to_string(capacity)+" waiting; at most "+std::to_string(lines_per_second)+" lines a second)");
            dropped_=0;++lines_;
        }
        while(size_&&lines_<lines_per_second) {
            out.push_back(ammo_trace_line(ring_[head_]));head_=(head_+1)%capacity;--size_;++lines_;
        }
        pending_.store(size_||dropped_,std::memory_order_release);
        return out;
    }
    void reset() noexcept {
        std::lock_guard lock(m_);
        size_=head_=0;dropped_=0;picks_=0;lines_=0;second_={};for(auto& k:kinds_)k=Kind{};
        frames_.store(0,std::memory_order_relaxed);pending_.store(false,std::memory_order_release);
    }
    size_t size() const {std::lock_guard lock(m_);return size_;}
    uint64_t dropped() const {std::lock_guard lock(m_);return dropped_;}
private:
    struct Kind {uint64_t pick{};clock::time_point at{};std::array<AmmoTraceEvent,size_t(AmmoTraceStage::Count)> last{};
                 std::array<bool,size_t(AmmoTraceStage::Count)> has{};};
    void push(AmmoTraceEvent& e,const Kind& k,clock::time_point now) noexcept {
        e.pick=k.pick;e.frame=frame();e.us=std::chrono::duration_cast<std::chrono::microseconds>(now-k.at).count();
        if(size_==capacity){head_=(head_+1)%capacity;--size_;++dropped_;} // the oldest goes: the newest says what is now
        ring_[(head_+size_)%capacity]=e;++size_;pending_.store(true,std::memory_order_release);
    }
    std::atomic<bool> on_{},pending_{};
    std::atomic<uint64_t> frames_{};
    mutable std::mutex m_;
    std::array<AmmoTraceEvent,capacity> ring_{};
    size_t head_{},size_{};uint64_t dropped_{},picks_{};
    Kind kinds_[2];
    clock::time_point second_{};unsigned lines_{};
};
} // namespace kiln::tools
