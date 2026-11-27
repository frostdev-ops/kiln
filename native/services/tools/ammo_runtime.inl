// SPDX-License-Identifier: MIT
// Included in presentation.cpp: shares its one selector detour, original-byte qualification and child owner.
namespace ammo {
using namespace kiln::tools;
const KilnClientApi* client{};
const KilnWorldApi* world_api{};
std::atomic<bool> ready{},faulted{};
kiln::RawHook pick_hook,finalizer_hook;
std::mutex mutex;
thread_local unsigned builder_depth{};
struct Publication {
    KilnOwner owner{};uint32_t kind{},mode{},session{},count{};uint64_t serial{};
    uint32_t generation{},local_player{},held{}; // the context it was published for, beside session and serial
    std::array<KilnUiAmmoRow,KUA_CAPACITY> rows{};
};
// What the backpack said of a game row with stock when a binding was made from these rows (GitHub issue 145).
enum Seen:int8_t {SEEN_UNREAD=-2,SEEN_UNREADABLE=-1,SEEN_ABSENT=0,SEEN_HELD=1};
struct Observation {
    KilnUiAmmoContext value{};uint32_t generation{};
    std::array<SelectorRow,KUA_CAPACITY> rows{};
    uint64_t step{};std::chrono::steady_clock::time_point built{}; // the build these rows came from
    std::array<int8_t,KUA_CAPACITY> seen; // per row, kept while the rows stay the same
    Observation(){seen.fill(SEEN_UNREAD);}
};
struct Binding {
    Observation original;
    std::shared_ptr<const Publication> publication;
    AmmoComposition composed;
};
Observation observations[2];
std::shared_ptr<const Publication> publications[2];
// GitHub issue 151: a Replace publication stays up over the game's newer builds of its list (the same client session,
// generation, player and held weapon) until Kiln.Ui publishes for them, at most keep_limit from the first such build.
// Replace takes nothing from the game's rows but a matching row's source, read from the build the rows are composed over,
// so the HUD keeps the mods' rows and Kiln's cursor instead of the game's rows and its selection (the equipped item) for
// the frame before the publication: a flash, and on a dedicated server, where a swap's build comes a round trip after
// the pick, the cursor put back on the equipped item after the player scrolled on. Past the limit the game's own rows
// show, as before, until the next publication; the post tick logs that once. Append is never kept: its rows go in
// around the game's and its room follows them.
constexpr auto keep_limit=std::chrono::milliseconds(1000);
struct Keep {std::chrono::steady_clock::time_point since{};uint64_t first{},game{};bool started{},expired{},report{};};
Keep keeps[2]; // reset by every publish and clear of that kind
std::atomic<bool> keep_report_pending{};
// GitHub issue 151, the flicker 31.21 left: the game's selection sync client_cycle_inventory_stock (0x2b4390) calls the
// selection action 0x40cf00 on the HUD's active group whenever it runs, outside any list build. The action selects the
// item the game's reconciliation 0x40ca70 names: the group's pending pick (+0x63c) while its version (+0x640) is not the
// one the server last consumed (CspHackInventoryStockSelection.versions), else the equipped stack. A pick Kiln takes for
// a mod writes no pending pick (the game's equip is kept back), so until the mod's swap reached the game the sync put the
// cursor back on the equipped item, the spell before the pick; the next build kept it (Frame takes the group's selection),
// and the swap's arrival moved it to the new spell again. Kiln now holds its pick as the game's pending pick holds the
// game's own: right after the sync (select_action) and at a build's entry, from the pick until the sync's choice is that
// item (the swap arrived), a pick of that list goes to the game, its publication ends, the client session, world or
// player changes, or hold_limit passes (then the equipped item shows, as before). Only while Kiln's rows are up (a
// binding of the group). Another held weapon does not end it: the reconciliation's slot is the player's (0xe + category,
// 0x40c980), not the weapon's, as the game's own pending pick, which stays in the group, holds over a weapon change too.
constexpr auto hold_limit=std::chrono::milliseconds(1000);
struct Hold {uintptr_t group{};uint32_t item{},session{},generation{},player{};KilnOwner owner{};std::chrono::steady_clock::time_point since{};};
Hold holds[2];
std::atomic<bool> holding{}; // any hold: the sync hook's one load while none is
bool state(KilnClientState& out);
void drop_hold(unsigned i) {holds[i]={};holding.store(holds[0].item||holds[1].item,std::memory_order_release);}
// Caller holds the mutex. The item Kiln holds list i's cursor on in group, or 0. A hold past hold_limit or of another
// client session, world or player than the game's now ends here, whichever group asks.
uint32_t held(unsigned i,uintptr_t group) {
    auto& h=holds[i];KilnClientState s{};
    if(!h.item)return 0;
    if(std::chrono::steady_clock::now()-h.since>hold_limit||!state(s)||s.client_session!=h.session||s.generation!=h.generation||
       s.local_player!=h.player){drop_hold(i);return 0;}
    return h.group==group?h.item:0;
}
kiln::RawHook action_hook,draw_hook;
AmmoTrace trace; // the pick trace (ammo_trace.h): off unless kiln.json or KILN_AMMO_TRACE turns it on at the start
std::atomic<uintptr_t> trace_groups{}; // the play data's groups the HUD draws one of (set by full builds while tracing)
uint64_t drawn_index{}; // the draw's remembered index as Kiln reckons it: the last index it drew (its widget state +0x10)
// An address is only an identity token. Never dereference one kept here: engine views are read only from the
// current hook's argument, and are copied into owned arrays before leaving that invocation.
std::map<uintptr_t,Binding> bindings;
std::unordered_set<uint32_t> catalog[2];
uint32_t catalog_session{};
uint64_t revision{};
std::deque<std::pair<KilnOwner,KilnUiAmmoPick>> pending;
int (*backpack_probe)(uint32_t,uint32_t){}; // private fixture seam; production uses stable ECS snapshots
int backpack(uint32_t actor,uint32_t item);
void backpack(uint32_t actor,const uint32_t* items,uint32_t n,int8_t* held);
// A pick of a provided row that went to the game (GitHub issue 145): logged at debug level from the client Post tick,
// never from the pick hook itself. At most 8 lines a second; the rest are counted into the next line.
enum class Backpack:uint8_t {Held,Unreadable,NotFoundAtBuild,UnreadableAtBuild,Stale,GameStock};
struct Handoff {uint32_t kind{},item{},count{};uint64_t serial{};int64_t age_ms{};uint64_t age_steps{};Backpack backpack{};};
std::array<Handoff,8> handoffs{};uint32_t handoff_count{};uint64_t handoff_dropped{};
std::atomic<bool> handoff_pending{};
struct {std::chrono::steady_clock::time_point start{};unsigned lines{};} handoff_window;
unsigned index(uint32_t kind){return kind==KUA_KIND_ARROW?0:kind==KUA_KIND_SPELL?1:2;}
bool state(KilnClientState& out) {
    out={};out.size=sizeof out;
    return client&&client->state&&client->state(&out)==KILN_OK&&out.side==KILN_SIDE_CLIENT&&out.session!=KILN_SESSION_NONE&&out.local_player&&out.client_session;
}
bool same(const KilnClientState& s,const Observation& o) {
    return s.client_session==o.value.session&&s.generation==o.generation&&s.local_player==o.value.local_player;
}
bool failed(){return faulted.load(std::memory_order_acquire);}
bool available(){return ready.load(std::memory_order_acquire)&&!faulted.load(std::memory_order_acquire);}
bool live_owner(KilnOwner owner) {
    if(!owner||!api||!api->subscribe_tick||!api->unsubscribe)return false;
    KilnHandle lease=0;const auto r=api->subscribe_tick(owner,KILN_TICK_PRE,&noop_tick,nullptr,&lease);
    if(r==KILN_OK)api->unsubscribe(lease);return r==KILN_OK;
}
template<class T> bool read(T& out,const void* p){return copy_game_value(&out,p,sizeof out);}
uint32_t selection(const SelectorGroup* group) {
    SelectorGroup head{}; // Copy only the scalar fields we use, not arbitrary game capacity.
    if(!group||!copy_game_value(&head,group,24)||!read(head.selected,&group->selected)||!read(head.has_selected,&group->has_selected)||
       !head.has_selected||head.selected>=head.count||head.count>4096||!head.data)return 0;
    uint32_t item=0;return read(item,&head.data[head.selected].item)?item:0;
}
// The trace's view of a group's selection: the valid flag, the index, the row count and that row's item (guarded reads).
AmmoCursor cursor(const SelectorGroup* group) {
    AmmoCursor c{};SelectorGroup head{};uint8_t valid=0;
    if(!group||!copy_game_value(&head,group,24)||!read(head.selected,&group->selected)||!read(valid,&group->has_selected)||
       head.count>4096||(head.count&&!head.data))return c;
    c.readable=true;c.valid=valid!=0;c.index=head.selected;c.count=head.count;
    if(c.index<c.count&&!read(c.item,&head.data[c.index].item))c.readable=false;
    return c;
}
bool write_rows(SelectorGroup* group,const SelectorRow* rows,uint32_t count,uint32_t selected) {
    SelectorRow* data=nullptr;uint64_t capacity=0;
    if(!group||!read(data,&group->data)||!read(capacity,&group->capacity)||count>capacity||count>KUA_CAPACITY||(!data&&count))return false;
    if(count&&!copy_game_value(data,rows,sizeof(SelectorRow)*count))return false;
    uint64_t n=count,at=0;uint8_t found=0;
    for(uint32_t i=0;i<count;++i)if(rows[i].item==selected){at=i;found=1;break;}
    return copy_game_value(&group->count,&n,8)&&copy_game_value(&group->selected,&at,8)&&copy_game_value(&group->has_selected,&found,1);
}
void fault(SelectorGroup* group) {
    if(group)clear_selector_view(group);faulted.store(true,std::memory_order_release);
}
// Bindings outlive the session: vanilla enumeration keeps unmatched rows (count zeroed), so a synthetic row stays in
// its group until that group's next build removes it, and its pick stays suppressed (stale: no event) until then.
void clear_session() {
    for(auto& o:observations)o={};for(auto& p:publications)p.reset();for(auto& k:keeps)k={};pending.clear();drop_hold(0);drop_hold(1);
    catalog[0].clear();catalog[1].clear();catalog_session=0;
}
void KILN_CALL lifecycle(const KilnWorldEvent* e,void*) {
    if(e&&e->kind==KILN_WORLD_CLIENT){std::lock_guard lock(mutex);clear_session();}
}
KilnHandle lifecycle_handle{},report_tick{};
std::atomic<bool> fault_reported{};
void publish_handoffs();
void publish_keep_reports();
// The pick trace's lines (ammo_trace.h), from the client Post tick: one load while there are none.
void publish_trace() {
    if(!trace.pending()||!api||!api->log)return;
    try {for(const auto& line:trace.take(std::chrono::steady_clock::now()))api->log(parent_owner,KILN_LOG_INFO,{line.data(),line.size()});}
    catch(...) {}
}
void KILN_CALL report_post(const KilnTickInfo*,void*) {
    if(failed()&&!fault_reported.exchange(true))report_caps();
    publish_handoffs();publish_keep_reports();publish_trace();
}
// The exact ItemRegistry lookup at Ce94470 uses map+0x38; Ce86770 gives these map field offsets. Copy only
// validated ammunition identities/categories, never a borrowed ItemInfo pointer. Refresh on each new client session.
bool copy_catalog(void* access,uint32_t session) {
    if(catalog_session==session)return true;
    const uint8_t* registry=nullptr;
    if(!access||!read(registry,static_cast<const uint8_t*>(access)+0x50)||!registry)return false;
    const auto* map=registry+0x38;uint64_t capacity=0;const uint64_t* occupied=nullptr;const uint32_t* keys=nullptr;const uint8_t* const* values=nullptr;uint32_t count=0;
    if(!read(capacity,map+0x10)||!capacity||capacity>262144||(capacity&(capacity-1))||!read(count,map+0x54)||count>capacity||
       !read(occupied,map+8)||!read(keys,map+0x28)||!read(values,map+0x40)||!occupied||!keys||!values)return false;
    std::unordered_set<uint32_t> copied[2];uint32_t seen=0;
    for(uint64_t word=0;word<(capacity+63)/64;++word) {
        uint64_t bits=0;if(!read(bits,occupied+word))return false;
        for(unsigned bit=0;bit<64&&word*64+bit<capacity;++bit)if(bits&(uint64_t(1)<<bit)) {
            const auto at=word*64+bit;uint32_t key=0,id=0;const uint8_t* info=nullptr;uint8_t category=0,subtype=0;
            if(!read(key,keys+at)||!key||!read(info,values+at)||!info||!read(id,info)||id!=key||!read(category,info+0xcc)||!read(subtype,info+0xcd))return false;
            ++seen;if(category==8&&(subtype==1||subtype==2))copied[subtype-1].insert(key);
        }
    }
    uint64_t again=0;uint32_t count_again=0;const uint64_t* occupied_again=nullptr;const uint32_t* keys_again=nullptr;const uint8_t* const* values_again=nullptr;
    if(seen!=count||!read(again,map+0x10)||again!=capacity||!read(count_again,map+0x54)||count_again!=count||
       !read(occupied_again,map+8)||occupied_again!=occupied||!read(keys_again,map+0x28)||keys_again!=keys||
       !read(values_again,map+0x40)||values_again!=values)return false;
    catalog[0]=std::move(copied[0]);catalog[1]=std::move(copied[1]);catalog_session=session;return true;
}
Frame::Frame(SelectorGroup* groups,void*,uint8_t only) {
    ++builder_depth;
    uint8_t active=7;if(groups&&only)read(active,reinterpret_cast<const uint8_t*>(groups)+0x2bf8);
    const bool tracing=trace.on();
    if(tracing&&groups&&!only)trace_groups.store(reinterpret_cast<uintptr_t>(groups),std::memory_order_relaxed);
    std::lock_guard lock(mutex);
    for(unsigned i=0;i<2;++i) {
        rebuild[i]=groups&&(!only||active==i);if(!rebuild[i])continue;
        if(tracing)entry[i]=cursor(groups+i);
        publication[i]=publications[i];
        const auto old=bindings.find(reinterpret_cast<uintptr_t>(groups+i));
        if(publication[i]||old!=bindings.end())selected[i]=selection(groups+i);
        // A pick Kiln holds (holds above) wins over what the game's selection sync left in the group since the last build.
        if(old!=bindings.end())if(const uint32_t h=held(i,reinterpret_cast<uintptr_t>(groups+i)))selected[i]=h;
        if(old!=bindings.end()) {
            // Remove last frame's injected rows before vanilla enumeration can accumulate them as stock.
            if(!write_rows(groups+i,old->second.original.rows.data(),old->second.original.value.game_count,selected[i]))fault(groups+i);
            bindings.erase(old);
        }
    }
}
Frame::~Frame(){--builder_depth;}
// Caller holds the mutex. Whether the publication p of list i, made for an earlier build, stays up over the build next
// (GitHub issue 151; see keeps above).
bool keep(unsigned i,const Publication& p,const Observation& next) {
    if(p.mode!=KUA_MODE_REPLACE||p.serial>=next.value.game_serial||p.session!=next.value.session||p.generation!=next.generation||
       p.local_player!=next.value.local_player||p.held!=next.value.held_item)return false;
    auto& k=keeps[i];const auto now=std::chrono::steady_clock::now();
    if(!k.started)k={now,next.value.game_serial,next.value.game_serial,true,false,false};
    if(k.expired)return false;
    if(now-k.since<=keep_limit){k.game=next.value.game_serial;return true;} // the last revision the rows were kept over
    k.expired=k.report=true;keep_report_pending.store(true,std::memory_order_release);return false;
}
// One line per expired keep, from the client Post tick (never from the selector hook).
void publish_keep_reports() {
    if(!keep_report_pending.exchange(false,std::memory_order_acq_rel)||!api||!api->log)return;
    struct Line {unsigned kind;uint64_t serial,first,game;};std::array<Line,2> lines{};unsigned n=0;
    {
        std::lock_guard lock(mutex);
        for(unsigned i=0;i<2;++i)if(keeps[i].report) {
            keeps[i].report=false;lines[n++]={i,publications[i]?publications[i]->serial:0,keeps[i].first,keeps[i].game};
        }
    }
    for(unsigned j=0;j<n;++j)try {
        char text[360];
        const int len=std::snprintf(text,sizeof text,"ui.ammo: the %s list shows the game's own rows: the mods' rows of list revision %llu "
            "were kept over the game's newer builds (revisions %llu to %llu) for %lld ms and no publication for them came; the mods' "
            "rows show again when Kiln.Ui publishes",lines[j].kind?"spell":"arrow",static_cast<unsigned long long>(lines[j].serial),
            static_cast<unsigned long long>(lines[j].first),static_cast<unsigned long long>(lines[j].game),
            static_cast<long long>(keep_limit.count()));
        if(len>0)api->log(parent_owner,KILN_LOG_INFO,{text,std::min<size_t>(size_t(len),sizeof text-1)});
    } catch(...) {}
}
void after(Frame& frame,SelectorGroup* groups,void* access) try {
    if(!available()||!groups||!access)return;
    KilnClientState before{};if(!state(before))return;
    const uint32_t* info=nullptr;uint32_t held=0;
    if(!read(info,static_cast<const uint8_t*>(access)+0x78)||(info&&!read(held,info)))return;
    std::lock_guard lock(mutex);
    if(catalog_session&&catalog_session!=before.client_session)clear_session();
    const bool known=copy_catalog(access,before.client_session);
    for(unsigned i=0;i<2;++i)if(frame.rebuild[i]) {
        // The pick trace's line of this build: armed once the game's rows are read, recorded however the iteration ends.
        struct BuildNote {
            AmmoTraceEvent e{};SelectorGroup* group{};bool armed{};
            ~BuildNote() {if(armed)try{e.at[2]=cursor(group);trace.record(e,std::chrono::steady_clock::now());}catch(...){}}
        } note;
        Observation next{};next.value.size=sizeof next.value;next.value.kind=i?KUA_KIND_SPELL:KUA_KIND_ARROW;
        next.value.session=before.client_session;next.value.local_player=before.local_player;next.value.held_item=held;next.generation=before.generation;
        SelectorRow* data=nullptr;uint64_t count=0,capacity=0;
        if(!read(data,&groups[i].data)||!read(count,&groups[i].count)||!read(capacity,&groups[i].capacity)||count>capacity||count>4096||(!data&&count)) {fault(nullptr);continue;}
        next.value.game_count=uint32_t(std::min<uint64_t>(count,KUA_CAPACITY));
        if(next.value.game_count&&!copy_game_value(next.rows.data(),data,next.value.game_count*sizeof(SelectorRow))){fault(nullptr);continue;}
        for(uint32_t r=0;r<next.value.game_count;++r)std::fill(next.rows[r].source.begin()+5,next.rows[r].source.end(),0);
        next.value.ok=known;next.value.reason=!known?KUA_REASON_CATALOG:count>KUA_CAPACITY?KUA_REASON_LIMIT:KUA_REASON_NONE;
        KilnClientState again{};if(!state(again)||again.generation!=before.generation||again.client_session!=before.client_session||again.local_player!=before.local_player)return;
        auto& previous=observations[i];
        const bool equal=previous.value.game_serial&&same(before,previous)&&previous.value.held_item==held&&previous.value.game_count==next.value.game_count&&
            std::memcmp(previous.rows.data(),next.rows.data(),next.value.game_count*sizeof(SelectorRow))==0&&previous.value.ok==next.value.ok;
        next.value.game_serial=equal?previous.value.game_serial:++revision;
        next.step=before.client_steps;next.built=std::chrono::steady_clock::now();
        // The same rows: what the backpack said of them stays. New rows are read again at their bind, also when a count
        // only fell (an arrow shot): the stack the read found may have left while one the reader cannot see came in or
        // stayed, and a held answer carried over would take that row's pick from the game (issue 127; GitHub issue 145).
        if(equal)next.seen=previous.seen;
        for(uint32_t r=0;r<next.value.game_count;++r)next.value.game_rows[r]={next.rows[r].item,uint32_t(std::min<uint64_t>(next.rows[r].count,UINT32_MAX)),0,r};
        previous=next;
        if(trace.on()) {
            note.armed=true;note.group=groups+i;note.e.stage=AmmoTraceStage::Build;note.e.kind=uint8_t(i);note.e.at[0]=frame.entry[i];
            note.e.at[1]=cursor(groups+i);note.e.revision=next.value.game_serial;note.e.rows=AmmoTraceRows::Game;
        }
        const auto& p=frame.publication[i];
        if(!known||!p||p!=publications[i]||p->session!=next.value.session) {
            note.e.why=!known?AmmoRowsWhy::Catalog:!p||p!=publications[i]?AmmoRowsWhy::NoPublication:AmmoRowsWhy::OtherSession;continue;
        }
        // A Replace publication of an earlier build is composed over this one, as if made for it, until the next
        // publication or keep_limit (GitHub issue 151). Its rows' picks are decided against these rows, as any binding's.
        if(p->serial!=next.value.game_serial&&!keep(i,*p,next)) {
            note.e.why=p->mode!=KUA_MODE_REPLACE?AmmoRowsWhy::AppendWaits:p->generation!=next.generation||p->local_player!=next.value.local_player||
                p->held!=next.value.held_item||p->serial>next.value.game_serial?AmmoRowsWhy::Context:AmmoRowsWhy::KeepEnded;
            continue;
        }
        auto composed=compose_ammo(next.rows.data(),next.value.game_count,p->rows.data(),p->count,p->mode,frame.selected[i]?frame.selected[i]:selection(groups+i));
        if(composed.count>capacity){composed.count=uint32_t(std::min<uint64_t>(capacity,KUA_CAPACITY));composed.truncated=true;}
        if(bindings.size()>=16) {
            // Only an earlier session's binding may make room; a current one still guards rows drawn this session.
            const auto stale=std::find_if(bindings.begin(),bindings.end(),[&](const auto& b){return b.second.original.value.session!=before.client_session;});
            if(stale==bindings.end()){previous.value.reason=KUA_REASON_LIMIT;note.e.why=AmmoRowsWhy::Full;continue;}
            bindings.erase(stale);
        }
        // Once per set of rows, for each game row with stock whose item this publication provides (no other row's pick
        // reads it): does the backpack hold its stack? One walk of the backpack answers them all. A pick of a provided
        // row that the backpack held here and no longer holds is no longer the game's (GitHub issue 145). A row it did
        // not find here (a reader that cannot see that stock) stays the game's, as issue 127 decided.
        {
            std::array<uint32_t,KUA_CAPACITY> items{},at{};std::array<int8_t,KUA_CAPACITY> held{};uint32_t n=0;
            for(uint32_t r=0;r<previous.value.game_count;++r)if(previous.rows[r].count&&previous.seen[r]==SEEN_UNREAD)
                for(uint32_t j=0;j<p->count;++j)if(p->rows[j].item==previous.rows[r].item){items[n]=previous.rows[r].item;at[n++]=r;break;}
            if(n)backpack(before.local_player,items.data(),n,held.data());
            for(uint32_t k=0;k<n;++k)previous.seen[at[k]]=held[k]>0?SEEN_HELD:held[k]==0?SEEN_ABSENT:SEEN_UNREADABLE;
        }
        // Reserve suppression bookkeeping before the first game write. Allocation failure must leave vanilla
        // rows, never a synthetic row whose later equip request has no binding to intercept.
        const auto [bound,inserted]=bindings.emplace(reinterpret_cast<uintptr_t>(groups+i),Binding{previous,p,std::move(composed)});
        if(!inserted){fault(nullptr);note.e.why=AmmoRowsWhy::Fault;continue;}
        const auto& output=bound->second.composed;
        uint32_t want=frame.selected[i]?frame.selected[i]:selection(groups+i);
        // A cursor whose item these rows no longer list (another publication, a held pick it dropped) takes the game's own
        // selection, not none: without a valid selection the HUD draws its remembered index (docs/re/building.md).
        if(want&&std::none_of(output.rows.begin(),output.rows.begin()+output.count,[&](const SelectorRow& r){return r.item==want;})) {
            if(holds[i].item==want&&holds[i].group==reinterpret_cast<uintptr_t>(groups+i))drop_hold(i);
            want=selection(groups+i);
        }
        if(!write_rows(groups+i,output.rows.data(),output.count,want)){fault(groups+i);note.e.why=AmmoRowsWhy::Fault;continue;}
        if(output.truncated)previous.value.reason=KUA_REASON_LIMIT;
        note.e.rows=p->serial==next.value.game_serial?AmmoTraceRows::Current:AmmoTraceRows::Kept;note.e.shown=p->serial;
        if(note.e.rows==AmmoTraceRows::Kept)note.e.kept_ms=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-keeps[i].since).count();
    }
}
catch(...) {fault(nullptr);}
// Stable, self-validating client ECS reads work on the frame/UI path too; never use legacy server-only component().
bool component(uint32_t entity,uint32_t hash,void* out,uint32_t size) {
    if(!world_api||!child)return false;
    KilnObjectRef ref{};ref.size=sizeof ref;
    const auto session=world_api->game_world_session(KILN_WORLD_CLIENT);
    uint32_t required=0;
    return session&&world_api->entity_open(child,0,KILN_WORLD_CLIENT,session,entity,&ref)==KILN_OK&&
        world_api->ecs_snapshot(child,&ref,hash,out,size,&required)==KILN_OK&&required==size;
}
// One walk of the actor's backpack graph for n items: held[k] is 1 when a stack of items[k] is in it, 0 when none is,
// -1 for all of them when the walk cannot be read.
void backpack(uint32_t actor,const uint32_t* items,uint32_t n,int8_t* held) {
    if(backpack_probe){for(uint32_t k=0;k<n;++k){const int r=backpack_probe(actor,items[k]);held[k]=int8_t(r>0?1:r==0?0:-1);}return;}
    std::fill_n(held,n,int8_t(-1));
    struct Setup {uint32_t ids[16];uint8_t categories[16],flags,total,available,initialized;};
    static_assert(sizeof(Setup)==84);
    std::array<uint32_t,64> todo{},seen{};uint32_t used=1,visited=0;todo[0]=actor;
    std::array<bool,KUA_CAPACITY> found{};if(n>found.size())return;
    while(used) {
        const auto entity=todo[--used];
        for(uint32_t i=0;i<visited;++i)if(seen[i]==entity)return;
        if(visited==seen.size())return;seen[visited++]=entity;
        Setup setup{},again{};if(!component(entity,0xc24df82au,&setup,sizeof setup)||!setup.initialized)return;
        for(unsigned i=0;i<16&&setup.ids[i];++i) {
            if(setup.categories[i]==5) {if(used==todo.size())return;todo[used++]=setup.ids[i];}
            else if(setup.categories[i]==4) {
                uint32_t stacks[24]{};if(!component(setup.ids[i],0xb19528a9u,stacks,sizeof stacks))return;
                for(unsigned s=0;s<8;++s)if(stacks[s*3+1])for(uint32_t k=0;k<n;++k)if(stacks[s*3]==items[k])found[k]=true;
            }
        }
        if(!component(entity,0xc24df82au,&again,sizeof again)||std::memcmp(&setup,&again,sizeof setup))return;
    }
    for(uint32_t k=0;k<n;++k)held[k]=found[k]?1:0;
}
int backpack(uint32_t actor,uint32_t item) {
    if(backpack_probe)return backpack_probe(actor,item);
    int8_t held=-1;backpack(actor,&item,1,&held);return held;
}
// Direct UI wishes need not have gone through C40c320's index update. Preserve only the picture's
// identity here; do not write the game's pending/equip wish at +63c/+640 or ClientPlayerInput.
bool select_identity(SelectorGroup* group,uint32_t selected) {
    SelectorRow* data=nullptr;uint64_t count=0,capacity=0;
    if(!read(data,&group->data)||!read(count,&group->count)||!read(capacity,&group->capacity)||count>capacity||count>KUA_CAPACITY||(!data&&count)){fault(nullptr);return false;}
    for(uint64_t r=0;r<count;++r) {
        uint32_t item=0;if(!read(item,&data[r].item)){fault(nullptr);return false;}
        if(item==selected) {
            const uint8_t yes=1;
            if(!copy_game_value(&group->selected,&r,8)||!copy_game_value(&group->has_selected,&yes,1)){fault(group);return false;}
            return true;
        }
    }
    return false;
}
// The count of item's row in the group as it is now; false when the group lists no row of it or cannot be read.
bool shown_count(const SelectorGroup* group,uint32_t item,uint64_t& count) {
    SelectorRow* data=nullptr;uint64_t n=0,capacity=0;
    if(!read(data,&group->data)||!read(n,&group->count)||!read(capacity,&group->capacity)||n>capacity||n>4096||(!data&&n))return false;
    for(uint64_t r=0;r<n;++r) {
        uint32_t at=0;if(!read(at,&data[r].item))return false;
        if(at==item)return read(count,&data[r].count);
    }
    return false;
}
// Caller holds the mutex. The line is formatted and logged later, on the client Post tick.
void note_handoff(const Publication& p,uint32_t item,uint32_t count,const Observation& rows,uint64_t step,Backpack backpack) {
    Handoff h{p.kind,item,count,rows.value.game_serial,
        std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-rows.built).count(),
        step>=rows.step?step-rows.step:0,backpack};
    if(handoff_count==handoffs.size()){std::move(handoffs.begin()+1,handoffs.end(),handoffs.begin());--handoff_count;++handoff_dropped;}
    handoffs[handoff_count++]=h;handoff_pending.store(true,std::memory_order_release);
}
void publish_handoffs() {
    if(!handoff_pending.load(std::memory_order_acquire)||!api||!api->log)return;
    std::array<Handoff,8> lines{};uint32_t n=0;uint64_t dropped=0;
    {
        std::lock_guard lock(mutex);
        const auto now=std::chrono::steady_clock::now();
        if(now-handoff_window.start>=std::chrono::seconds(1))handoff_window={now,0};
        n=std::min<uint32_t>(handoff_count,uint32_t(handoffs.size()-handoff_window.lines));
        std::copy_n(handoffs.begin(),n,lines.begin());
        std::move(handoffs.begin()+n,handoffs.begin()+handoff_count,handoffs.begin());handoff_count-=n;
        handoff_window.lines+=n;
        if(n){dropped=handoff_dropped;handoff_dropped=0;}
        handoff_pending.store(handoff_count||handoff_dropped,std::memory_order_release);
    }
    for(uint32_t i=0;i<n;++i)try {
        const auto& h=lines[i];
        const char* backpack=h.backpack==Backpack::Held?"holds it":h.backpack==Backpack::Unreadable?"unreadable":
            h.backpack==Backpack::NotFoundAtBuild?"not read (the list's build did not find it there)":
            h.backpack==Backpack::UnreadableAtBuild?"not read (unreadable when the list was built)":
            h.backpack==Backpack::Stale?"not read (the client session, world or player changed since the list was built)":
            "not read (the game's own rows showed, with stock of it: the mods' rows for them were not up yet)";
        char text[400];
        int len=std::snprintf(text,sizeof text,"ui.ammo: a pick of %s 0x%08x (a row a mod provided) went to the game: game row count %u "
            "(list revision %llu, built %lld ms, %llu client steps before the pick); backpack: %s",h.kind==KUA_KIND_SPELL?"spell":"arrow",
            h.item,h.count,static_cast<unsigned long long>(h.serial),static_cast<long long>(h.age_ms),static_cast<unsigned long long>(h.age_steps),backpack);
        if(len>0&&i+1==n&&dropped)len+=std::snprintf(text+std::min<size_t>(size_t(len),sizeof text-1),sizeof text-std::min<size_t>(size_t(len),sizeof text-1),
            "; %llu more such picks not logged",static_cast<unsigned long long>(dropped));
        if(len>0)api->log(parent_owner,KILN_LOG_DEBUG,{text,std::min<size_t>(size_t(len),sizeof text-1)});
    } catch(...) {}
}
// The HUD list shows again (GitHub issue 140). The game's list draw fades from the latest of three change times; one
// of them is stamped by the HUD's watcher whenever G38UiPlayData's stock-cycle count changes, and player_ui adds one to
// that count for each UiInventoryStockCycleEvent the host sends for the local player once it has consumed an
// inventoryStockCycleAction. A synthetic pick sends no action, so for the user's own cycle or direct selection (by the
// game's return address) Kiln adds the one itself, for the list the HUD shows. Nothing else reads the count.
struct Reveal {uintptr_t cycle{},select{};int64_t groups{},count{};};
Reveal reveal_sites;
std::atomic<bool> reveal_ready{};
bool reveal_setup(uintptr_t pick) {
    reveal_ready.store(false,std::memory_order_release);
    // The feature covers the whole chain Kiln relies on, not only the names read here: the watcher's stamp
    // (ui/AmmoCycleWatch) and the list draw's read of it (ui/AmmoListRevealRead, ui/ammo.reveal_time). Without them a
    // count change could play the UI sound with no list to show.
    if(!api||!api->symbol||!api->constant||!api->feature||!api->feature({"ui.ammo.reveal",14})||!hooks||
       hooks->header.size<offsetof(KilnHooksApi,caller)+sizeof(hooks->caller)||!hooks->read_original||!hooks->caller)return false;
    Reveal r{};
    if(api->symbol({"ui/AmmoCycleReturn",18},&r.cycle)!=KILN_OK||api->symbol({"ui/AmmoSelectReturn",19},&r.select)!=KILN_OK||
       api->constant({"ui/ammo.groups",14},&r.groups)!=KILN_OK||api->constant({"ui/ammo.cycle_count",19},&r.count)!=KILN_OK)return false;
    // The count is a u32 of the play data before its selector groups.
    if(r.groups<=0||r.groups%8||r.count<0||r.count%4||r.count+4>r.groups)return false;
    for(const uintptr_t site:{r.cycle,r.select}) {
        uint8_t call[5]{};int32_t rel=0;
        if(site<5||!qualify(site-5,"E8 ?? ?? ?? ??")||hooks->read_original(site-5,call,5)!=KILN_OK||call[0]!=0xe8)return false;
        std::memcpy(&rel,call+1,4);if(int64_t(site)+rel!=int64_t(pick))return false;
    }
    reveal_sites=r;reveal_ready.store(true,std::memory_order_release);return true;
}
// Caller holds the mutex; a synthetic pick of category's row was just queued.
void reveal(SelectorGroup* groups,uint8_t category) {
    static_assert(sizeof(SelectorGroup)*7==0x2bf8);
    const uintptr_t at=reinterpret_cast<uintptr_t>(groups);
    uint8_t active=7;
    if(at<uintptr_t(reveal_sites.groups)||!read(active,reinterpret_cast<const uint8_t*>(groups)+0x2bf8)||active!=category)return;
    auto* count=reinterpret_cast<uint32_t*>(at-uintptr_t(reveal_sites.groups)+uintptr_t(reveal_sites.count));
    uint32_t n=0;if(!read(n,count))return;
    ++n;copy_game_value(count,&n,sizeof n);
}
void KILN_CALL pick(SelectorGroup* groups,void* input,uint8_t category,uint32_t item,uint32_t version) try {
    kt::ToolPolicyScope no_impact_policy(nullptr);
    const bool tracing=trace.on()&&groups&&category<2;
    // Read before any call: caller() is the game's call site only until the handler calls on. The trace reads it too
    // where kiln.hooks answers it (reveal_setup requires it as well).
    const bool callers=hooks&&hooks->header.size>=offsetof(KilnHooksApi,caller)+sizeof(hooks->caller)&&hooks->caller;
    const uintptr_t from=(tracing&&callers)||reveal_ready.load(std::memory_order_acquire)?pick_hook.caller():0;
    // The pick trace's line: who took the pick and the cursor after it, recorded however this call ends.
    struct PickNote {
        AmmoTraceEvent e{};SelectorGroup* group{};bool armed{};
        ~PickNote() {if(armed)try{e.at[0]=cursor(group);trace.pick(e,std::chrono::steady_clock::now());}catch(...){}}
    } note;
    if(tracing) {
        note.armed=true;note.group=groups+category;note.e.kind=category;note.e.item=item;note.e.route=AmmoPickRoute::Dropped;
        note.e.site=!from||!reveal_sites.cycle?AmmoPickSite::Unknown:from==reveal_sites.cycle?AmmoPickSite::Cycle:
            from==reveal_sites.select?AmmoPickSite::Select:AmmoPickSite::Game;
    }
    std::shared_ptr<const Publication> p;Observation original{};KilnUiAmmoRow picked{};
    bool provided=false,bound=false;int stocked=-1; // stocked: the index of the bound game row of the item with stock
    uint64_t stock=0; // the game's count of the item: the bound row's, or without a binding the row the group shows now
    if(groups&&category<2) {
        std::lock_guard lock(mutex);const auto b=bindings.find(reinterpret_cast<uintptr_t>(groups+category));
        if(b!=bindings.end()) {
            for(uint32_t i=0;i<b->second.composed.count;++i)if(b->second.composed.rows[i].item==item){p=b->second.publication;original=b->second.original;bound=true;break;}
        } else if(available()&&publications[category]&&observations[category].value.game_serial&&publications[category]->session==observations[category].value.session) {
            // No binding: the game built new rows and the mod has not published for them yet, so the game's own rows show
            // (GitHub issue 145). The observation gives the session and the debug line's revision; the row is decided below
            // by what the group shows now, never by an observation another selector state or a skipped build left.
            p=publications[category];original=observations[category];
        }
        if(p)for(uint32_t j=0;j<p->count;++j)if(p->rows[j].item==item){picked=p->rows[j];provided=true;break;}
        // A row the game listed itself is the game's own in either mode (vanilla equips it). Not one it kept at count 0: a
        // rebuild zeroes every row it keeps, adds stock only to matching rows and does not remove the rest, so a used-up
        // stack's row stays listed at 0 (issue 131). The backpack below decides it.
        if(provided&&bound)for(uint32_t j=0;j<original.value.game_count;++j)if(original.rows[j].item==item&&original.rows[j].count){stocked=int(j);stock=original.rows[j].count;break;}
        if(provided&&!bound) {
            // Without a binding only the game's row as the group shows it counts: none (or unreadable), the game's as before.
            if(!shown_count(groups+category,item,stock))provided=false;
        }
    }
    // A pick that goes to the game ends a held pick of that list: the game's own pending pick holds the cursor now.
    if(!provided) {
        if(groups&&category<2&&holding.load(std::memory_order_acquire)){std::lock_guard lock(mutex);drop_hold(category);}
        note.e.route=AmmoPickRoute::NotProvided;pick_hook.call<void>(groups,input,category,item,version);return;
    }
    const auto vanilla=[&](uint32_t count,uint64_t step,Backpack why) {
        {std::lock_guard lock(mutex);note_handoff(*p,item,count,original,step,why);drop_hold(category);}
        note.e.route=AmmoPickRoute::Game;pick_hook.call<void>(groups,input,category,item,version);
    };
    const uint32_t count=uint32_t(std::min<uint64_t>(stock,UINT32_MAX));
    KilnClientState before{};const bool live=state(before)&&same(before,original);
    if(stock) { // bound: stocked>=0; without a binding the shown row has stock, which is always the game's
        // The game's row with stock is the game's unless the backpack held the stack when these rows were bound and no
        // longer does: a swap moved it out before the game built the list again (GitHub issue 145). Vanilla would equip
        // nothing and empty the slot, so the pick is the mod's. Unread or unreadable then, or unreadable now: the game's.
        const int8_t seen=bound?original.seen[stocked]:int8_t(SEEN_UNREAD);
        if(!live||seen!=SEEN_HELD) {
            vanilla(count,live?before.client_steps:original.step,!live?Backpack::Stale:seen==SEEN_ABSENT?Backpack::NotFoundAtBuild:
                seen==SEEN_UNREADABLE?Backpack::UnreadableAtBuild:Backpack::GameStock);
            return;
        }
    }
    else if(!live) {
        if(!bound){vanilla(count,original.step,Backpack::Stale);return;} // the game's own row, as before this window existed
        return; // A stale synthetic row is never native equip permission.
    }
    const int actual=backpack(before.local_player,item);
    KilnClientState after{};const bool steady=state(after)&&after.generation==before.generation&&after.client_session==before.client_session&&after.local_player==before.local_player;
    if(stocked>=0&&(!steady||actual!=0)){vanilla(count,before.client_steps,actual>0?Backpack::Held:Backpack::Unreadable);return;}
    if(!steady)return;
    if(actual==1){vanilla(count,before.client_steps,Backpack::Held);return;}
    if(actual<0)return;
    std::lock_guard lock(mutex);
    if(publications[category]!=p||!available()||!select_identity(groups+category,item))return;
    if(pending.size()==KUA_CAPACITY)pending.pop_front();
    pending.emplace_back(p->owner,KilnUiAmmoPick{sizeof(KilnUiAmmoPick),p->kind,item,picked.count,picked.row,before.client_session,before.client_steps});
    note.e.route=AmmoPickRoute::Mod;
    // Kiln holds this pick's cursor over the game's selection sync until the mod's swap reaches the game (holds above). A
    // repeat of the held item keeps its start, so the game's own automatic picks of it cannot hold it longer.
    {
        auto& h=holds[category];const auto g=reinterpret_cast<uintptr_t>(groups+category);
        if(h.item!=item||h.group!=g||h.session!=before.client_session||h.generation!=before.generation||h.player!=before.local_player)
            h={g,item,before.client_session,before.generation,before.local_player,p->owner,std::chrono::steady_clock::now()};
        holding.store(true,std::memory_order_release);
    }
    // Deliberately do not call the continuation: no inventoryStockCycleAction version/item or pending native
    // selection at group+0x63c/+0x640 is written for a non-backpack row. The server half alone performs a swap.
    // The game's own automatic selections (other call sites) never show the list.
    if(from&&reveal_ready.load(std::memory_order_acquire)&&(from==reveal_sites.cycle||from==reveal_sites.select))reveal(groups,category);
}
catch(...) {fault(nullptr);}
void KILN_CALL finalizer(SelectorGroup* groups,void* a,void* b,void* c,void* d,void* e) {
    kt::ToolPolicyScope no_impact_policy(nullptr);
    uint32_t selected[2]{};
    const bool tracing=trace.on()&&groups&&!builder_depth;AmmoTraceEvent notes[2]{};
    if(groups&&!builder_depth) {
        std::lock_guard lock(mutex);
        for(unsigned i=0;i<2;++i)if(bindings.contains(reinterpret_cast<uintptr_t>(groups+i))) {
            selected[i]=selection(groups+i);
            if(tracing){notes[i].stage=AmmoTraceStage::Final;notes[i].kind=uint8_t(i);notes[i].at[0]=cursor(groups+i);}
        }
    }
    finalizer_hook.call<void>(groups,a,b,c,d,e);
    if(groups)for(unsigned i=0;i<2;++i)if(selected[i]) {
        if(tracing)notes[i].at[1]=cursor(groups+i);
        {
            std::lock_guard lock(mutex);const auto at=bindings.find(reinterpret_cast<uintptr_t>(groups+i));
            if(at==bindings.end())continue;
            select_identity(groups+i,selected[i]);
        }
        if(tracing){notes[i].at[2]=cursor(groups+i);trace.record(notes[i],std::chrono::steady_clock::now());}
    }
}
// ui/AmmoSelectAction (0x40cf00, out, group, category, ...): client_cycle_inventory_stock's selection sync of the HUD's
// active group (holds above). Its result's first byte is 4 when it selected the item the game's reconciliation named.
void* KILN_CALL select_action(void* out,SelectorGroup* group,uint8_t category,void* context,void* a5,void* a6,void* a7,void* a8) {
    kt::ToolPolicyScope no_impact_policy(nullptr);
    const bool tracing=trace.on()&&group&&category<2;
    AmmoTraceEvent e{};e.stage=AmmoTraceStage::Sync;e.kind=category;
    if(tracing)e.at[0]=cursor(group);
    void* result=action_hook.call<void*>(out,group,category,context,a5,a6,a7,a8);
    if(!group||category>=2||(!tracing&&!holding.load(std::memory_order_acquire)))return result;
    try {
        if(tracing)e.at[1]=cursor(group);
        {
            std::lock_guard lock(mutex);const auto g=reinterpret_cast<uintptr_t>(group);
            // held() first: a hold that ended (its time, session, world or player) goes also when the rows are gone.
            if(const uint32_t item=held(category,g);item&&available()&&bindings.contains(g)) {
                uint8_t code=0;
                if(selection(group)==item){if(read(code,out)&&code==4){drop_hold(category);e.landed=true;}} // the swap arrived
                else {
                    e.held_ms=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-holds[category].since).count();
                    if(select_identity(group,item))e.held=true;else drop_hold(category); // no longer listed: nothing to hold
                }
            }
        }
        if(tracing){e.at[2]=cursor(group);trace.record(e,std::chrono::steady_clock::now());}
    } catch(...) {fault(nullptr);}
    return result;
}
// ui/AmmoListDraw (0xbe5150, the HUD's list of the active group; hooked only while the pick trace is on). It draws the
// group's selection when its selection-valid flag is set, else its remembered index, the index it drew last (its widget
// state +0x10, which each draw sets; docs/re/building.md). Kiln reckons that index the same way, from the first draw on.
void KILN_CALL draw(SelectorGroup* group) {
    if(trace.on()&&group)try {
        trace.next_frame();
        AmmoTraceEvent e{};e.stage=AmmoTraceStage::Draw;
        const auto c=cursor(group);
        if(c.readable) {
            const uint64_t index=c.valid?c.index:drawn_index;drawn_index=index;
            e.at[0]={0,index,c.count,c.valid,true};e.remembered=!c.valid;
            SelectorRow* data=nullptr;if(index<c.count&&read(data,&group->data)&&data)read(e.at[0].item,&data[index].item);
        }
        const auto base=trace_groups.load(std::memory_order_relaxed),at=reinterpret_cast<uintptr_t>(group);
        if(base&&at>=base&&(at-base)%sizeof(SelectorGroup)==0&&(at-base)/sizeof(SelectorGroup)<2) {
            e.kind=uint8_t((at-base)/sizeof(SelectorGroup));trace.record(e,std::chrono::steady_clock::now());
        }
    } catch(...) {}
    draw_hook.call<void>(group);
}
KilnResult KILN_CALL publish(KilnOwner owner,const KilnUiAmmoPublish* value) {
    if(!value||value->size<sizeof *value||index(value->kind)>1||value->mode>KUA_MODE_REPLACE||value->reserved||value->count>KUA_CAPACITY||(!value->rows&&value->count))return KILN_ERR_INVALID_ARGUMENT;
    if(!live_owner(owner))return owner?KILN_ERR_REVOKED:KILN_ERR_INVALID_ARGUMENT;
    if(!available())return KILN_ERR_UNAVAILABLE;
    if(!client->on_client_thread())return KILN_ERR_WRONG_THREAD;
    try {
        const unsigned i=index(value->kind);auto p=std::make_shared<Publication>();p->owner=owner;p->kind=value->kind;p->mode=value->mode;p->session=value->session;p->serial=value->serial;p->count=value->count;
        if(value->count)std::copy_n(value->rows,value->count,p->rows.data());
        std::lock_guard lock(mutex);
        if(!live_owner(owner))return KILN_ERR_REVOKED;
        if(publications[i]&&publications[i]->owner!=owner)return KUA_ERR_BUSY;
        if(value->mode==KUA_MODE_GAME){publications[i].reset();keeps[i]={};drop_hold(i);pending.erase(std::remove_if(pending.begin(),pending.end(),[&](const auto& e){return e.first==owner&&e.second.kind==value->kind;}),pending.end());return KILN_OK;}
        KilnClientState s{};const auto& context=observations[i];
        if(!context.value.game_serial)return KILN_ERR_NOT_FOUND;
        if(!state(s)||!same(s,context)||value->session!=s.client_session||value->serial!=context.value.game_serial)return KILN_ERR_INVALID_ARGUMENT;
        if(!context.value.ok||catalog_session!=s.client_session)return KILN_ERR_UNAVAILABLE;
        std::unordered_set<uint32_t> seen;
        for(uint32_t r=0;r<p->count;++r)if(p->rows[r].flags||!catalog[i].contains(p->rows[r].item)||!seen.insert(p->rows[r].item).second)return KILN_ERR_INVALID_ARGUMENT;
        // Pick keys belong to the old publication. A replacement cannot retarget a queued wish to a new provider.
        pending.erase(std::remove_if(pending.begin(),pending.end(),[&](const auto& e){return e.first==owner&&e.second.kind==value->kind;}),pending.end());
        p->generation=context.generation;p->local_player=context.value.local_player;p->held=context.value.held_item;
        publications[i]=std::move(p);keeps[i]={};return KILN_OK;
    }catch(...){return KILN_ERR_UNAVAILABLE;}
}
KilnResult KILN_CALL context(KilnOwner owner,uint32_t kind,KilnUiAmmoContext* out) {
    if(!out||out->size<sizeof *out||index(kind)>1||!owner)return KILN_ERR_INVALID_ARGUMENT;
    *out={};out->size=sizeof *out;out->kind=kind;
    if(!live_owner(owner))return KILN_ERR_REVOKED;
    if(!available())return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(mutex);KilnClientState s{};
    if(!state(s)){out->reason=KUA_REASON_NO_SESSION;return KILN_ERR_NOT_FOUND;}
    const auto& observed=observations[index(kind)];
    if(!observed.value.game_serial){out->reason=KUA_REASON_NO_CONTEXT;return KILN_ERR_NOT_FOUND;}
    if(!same(s,observed)){out->reason=KUA_REASON_STALE;return KILN_ERR_NOT_FOUND;}
    *out=observed.value;return KILN_OK;
}
KilnResult KILN_CALL take_picks(KilnOwner owner,KilnUiAmmoPick* out,uint32_t capacity,uint32_t* written) {
    if(written)*written=0;
    if(!written||!owner||capacity>KUA_CAPACITY||(!out&&capacity))return KILN_ERR_INVALID_ARGUMENT;
    if(!live_owner(owner))return KILN_ERR_REVOKED;
    if(!available())return KILN_ERR_UNAVAILABLE;
    if(!client->on_client_thread())return KILN_ERR_WRONG_THREAD;
    std::lock_guard lock(mutex);KilnClientState s{};const bool live=state(s);
    for(auto i=pending.begin();i!=pending.end();) {
        if(!live||i->second.session!=s.client_session){i=pending.erase(i);continue;}
        if(i->first!=owner||*written==capacity){++i;continue;}
        out[(*written)++]=i->second;i=pending.erase(i);
    }
    return KILN_OK;
}
void retire(KilnOwner owner) {
    std::lock_guard lock(mutex);
    for(unsigned i=0;i<2;++i)if(publications[i]&&publications[i]->owner==owner){publications[i].reset();keeps[i]={};}
    for(unsigned i=0;i<2;++i)if(holds[i].item&&holds[i].owner==owner)drop_hold(i);
    pending.erase(std::remove_if(pending.begin(),pending.end(),[&](const auto& p){return p.first==owner;}),pending.end());
}
// The selection action's whole body as the game wrote it: reconciliation, the row lookup, the selection write, result 4.
constexpr char action_pattern[]="48 89 5C 24 08 57 48 83 EC 50 48 8B 84 24 98 00 00 00 4D 8B D1 4C 8B 8C 24 90 00 00 00 48 8B FA 44 88 44 24 30 "
    "48 8B D9 4C 8B 84 24 88 00 00 00 49 8B CA 48 89 54 24 28 48 8B 94 24 80 00 00 00 48 89 44 24 20 E8 ?? ?? ?? ?? 85 C0 74 4A 44 8B C0 "
    "48 8D 4C 24 40 48 8B D7 E8 ?? ?? ?? ?? 80 7C 24 40 00 75 33 80 BF 30 06 00 00 00 75 07 C6 87 30 06 00 00 01 48 8B 44 24 48 48 89 87 "
    "28 06 00 00 33 C0 48 89 43 04 48 8B C3 C6 03 04";
constexpr char draw_pattern[]="48 89 4C 24 08 55 56 57 41 54 48 8D AC 24 68 F6 FF FF 48 81 EC 98 0A 00 00 48 8B 05 ?? ?? ?? ?? 33 FF 4C 8B E1 "
    "48 8B 90 D0 00 00 00 48 8B 32 48 85 F6 75 04 8B F7 EB 04 48 8B 76 38 C7 45 80 06 00 0A 00";
bool start() {
    client=static_cast<const KilnClientApi*>(api->service({KILN_CLIENT_SERVICE,sizeof(KILN_CLIENT_SERVICE)-1},KILN_CLIENT_VERSION));
    world_api=static_cast<const KilnWorldApi*>(api->service({KILN_WORLD_SERVICE,sizeof(KILN_WORLD_SERVICE)-1},KILN_WORLD_VERSION));
    if(!client||client->header.size<offsetof(KilnClientApi,client_world)+sizeof(client->client_world)||!client->state||!client->on_client_thread||!client->subscribe_tick||!client->unsubscribe||
       !world_api||world_api->header.size<offsetof(KilnWorldApi,ecs_snapshot)+sizeof(world_api->ecs_snapshot)||!world_api->game_world_session||!world_api->entity_open||!world_api->ecs_snapshot||
       !api->subscribe_world||api->subscribe_world(child,&lifecycle,nullptr,&lifecycle_handle)!=KILN_OK||client->subscribe_tick(child,KILN_TICK_POST,&report_post,nullptr,&report_tick)!=KILN_OK)return false;
    uintptr_t lookup=0,map=0;
    if(api->symbol({"ui/AmmoItemLookup",17},&lookup)!=KILN_OK||api->symbol({"ui/AmmoItemMap",14},&map)!=KILN_OK||
       !qualify(lookup,"89 4C 24 08 48 83 EC 28 85 C9 74 1B 48 8D 4A 38 48 8D 54 24 30 E8 ?? ?? ?? ?? 48 85 C0 74 08 48 8B 00")||
       !qualify(map,"48 89 5C 24 08 48 89 7C 24 10 83 79 54 00 4C 8B C9 48 8B 59 10 74 53 44 8B 1A 48 8D 43 FF 48 8B 79 08 49 23 C3"))return false;
    uint8_t call[5]{};int32_t rel=0;
    if(hooks->read_original(lookup+21,call,5)!=KILN_OK||call[0]!=0xe8)return false;
    std::memcpy(&rel,call+1,4);if(int64_t(lookup)+26+rel!=int64_t(map))return false;
    const bool final=install(finalizer_hook,"ui/AmmoFinalizer","48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 41 54 41 55 41 56 41 57 48 8B EC 48 83 EC 50 4C 8B 6D 50 49 8B F1 4C 8B 65 58",reinterpret_cast<void*>(&finalizer));
    const bool pick_ok=install(pick_hook,"ui/AmmoPick","4C 8B D9 41 80 F8 07 0F 83 91 00 00 00 48 8D 0D ?? ?? ?? ?? 41 0F B6 C0 8B 84 81 F0 C1 40 00 48 03 C1 FF E0",reinterpret_cast<void*>(&pick));
    uintptr_t pick_at=0;
    if(final&&pick_ok&&api->symbol({"ui/AmmoPick",11},&pick_at)==KILN_OK&&!reveal_setup(pick_at)&&api->log) {
        constexpr char line[]="ui.ammo: picks of mod rows do not show the HUD list again on this game build (the stock-cycle reveal is not qualified)";
        api->log(parent_owner,KILN_LOG_INFO,{line,sizeof line-1});
    }
    // The game's selection sync (holds above). Without it only a build puts a held pick back, so the HUD can show the
    // equipped item between the sync and the next build, as before.
    if(final&&pick_ok&&!install(action_hook,"ui/AmmoSelectAction",action_pattern,reinterpret_cast<void*>(&select_action))&&api->log) {
        constexpr char line[]="ui.ammo: after a pick of a mod row the HUD list can show the equipped item again until the mod's swap arrives on "
            "this game build (the game's selection sync, ui/AmmoSelectAction, is not qualified)";
        api->log(parent_owner,KILN_LOG_INFO,{line,sizeof line-1});
    }
    if(final&&pick_ok&&trace.on()&&api->log) {
        const bool drawn=install(draw_hook,"ui/AmmoListDraw",draw_pattern,reinterpret_cast<void*>(&draw));
        const std::string line=std::string("ui.ammo: the pick trace is on (kiln.json \"ui\": {\"ammoTrace\": true} or KILN_AMMO_TRACE=1): ")+
            "\"ui.ammo trace:\" lines for 2 s after each pick of an arrow or spell row"+(drawn?"":
            "; the HUD list draw is not traced on this game build (ui/AmmoListDraw is not qualified), so every frame number is 0");
        api->log(parent_owner,KILN_LOG_INFO,{line.data(),line.size()});
    }
    ready.store(final&&pick_ok,std::memory_order_release);return available();
}
void stop() {
    ready.store(false,std::memory_order_release);reveal_ready.store(false,std::memory_order_release);
    if(report_tick&&client&&client->unsubscribe)client->unsubscribe(report_tick);report_tick=0;
    if(lifecycle_handle&&api&&api->unsubscribe)api->unsubscribe(lifecycle_handle);lifecycle_handle=0;
    std::lock_guard lock(mutex);clear_session();
}
const KilnUiAmmoApi table{{sizeof(KilnUiAmmoApi),KILN_UI_AMMO_VERSION},&publish,&context,&take_picks};
} // namespace ammo
