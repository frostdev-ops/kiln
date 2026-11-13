// SPDX-License-Identifier: MIT
// Host tests of kiln-emf's EmfApi v22-v26 logic (src/v26_model.*) with fakes for the world.
#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "v26_model.h"

using namespace kemf::v26;

static int g_fail = 0, g_pass = 0;
#define CHECK(c)                                                              \
    do {                                                                      \
        if (c) ++g_pass;                                                      \
        else {                                                                \
            ++g_fail;                                                         \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c);          \
        }                                                                     \
    } while (0)

static uint32_t fnv(const char* s) {
    uint32_t h = 2166136261u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 16777619u;
    return h;
}

static void test_note_rows() {
    // every character is 10 wide: a row of at most 20 characters
    const auto w = [](std::string_view t) { return static_cast<float>(t.size()) * 10.f; };
    CHECK((note_rows("short", 200, 3, w) == std::vector<std::string>{"short"}));
    CHECK((note_rows("", 200, 3, w).empty()));
    CHECK((note_rows("   ", 200, 3, w).empty()));
    CHECK((note_rows("one two three four five six", 200, 3, w) ==
           std::vector<std::string>{"one two three four", "five six"}));
    // more than three rows: the last row is cut at a word and says so
    const auto rows = note_rows("aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk llll mmmm nnnn oooo pppp", 200, 3, w);
    CHECK(rows.size() == 3 && rows[0] == "aaaa bbbb cccc dddd" && rows[1] == "eeee ffff gggg hhhh" && rows[2] == "iiii jjjj kkkk ...");
    for (const auto& r : rows) CHECK(w(r) <= 200);
    // a word wider than a row stays whole (it is not split inside a UTF-8 character)
    CHECK((note_rows("tiny averyveryverylongwordthatdoesnotfit end", 200, 3, w) ==
           std::vector<std::string>{"tiny", "averyveryverylongwordthatdoesnotfit", "end"}));
    CHECK((note_rows("a b", 200, 0, w).empty()));
    // the row with the dots fits too: words go until it does, down to the dots alone
    CHECK((note_rows("one two three", 80, 1, w) == std::vector<std::string>{"one ..."}));
    CHECK((note_rows("one two three", 50, 1, w) == std::vector<std::string>{"..."}));
    CHECK((note_rows("one two three", 10, 1, w) == std::vector<std::string>{"..."}));
    CHECK((note_rows("one two three four five", 110, 2, w) == std::vector<std::string>{"one two", "three ..."}));
    // line breaks and tabs are spaces: a note cannot make more rows than it is given
    CHECK((note_rows("a\nb\r\nc\td", 200, 3, w) == std::vector<std::string>{"a b c d"}));
    CHECK((note_rows("aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\ndddddddddd", 120, 3, w) ==
           std::vector<std::string>{"aaaaaaaaaa", "bbbbbbbbbb", "..."}));
}

static void test_table() {
    CHECK(api_requirement((uint64_t{1} << 32) + 27) > EMF_API_VERSION);
    CHECK(api_requirement(UINT64_MAX) > EMF_API_VERSION);
    CHECK(api_requirement(0)==1 && api_requirement(26)==26);
    CHECK(api_size_for(21) == 592);
    CHECK(api_size_for(26) == 688);
    CHECK(api_size_for(27) == 728);
    // v28-v40 (EMF 0.6.3 to 0.6.8): EMF's own sizes, each a whole number of slots after the one before
    const uint32_t sizes[] = {736, 752, 776, 808, 864, 920, 952, 992, 1000, 1016, 1040, 1064, 1088};
    for (uint32_t v = 28; v <= 40; ++v) CHECK(api_size_for(v) == sizes[v - 28]);
    CHECK(api_size_for(40) == sizeof(EmfApi));
    CHECK(api_size_for(41) == 0);
    CHECK(EMF_API_VERSION == 40);
    // the "emf" gate of mod.json (mods.cpp refuses a requirement above the table's version)
    CHECK(api_requirement(40) <= EMF_API_VERSION && api_requirement(41) > EMF_API_VERSION);
    CHECK(kRemoveBuildingV36Size == 144 && kPathQueryV37Size == 52 && kEnemyOrderV39Size == 120 && kMoveOrderV40Size == 128);
    // the hashes are kiln.world's component_hash: FNV-1a of "keen::ecs::<Name>"
    CHECK(fnv("keen::ecs::ScaleComponent") == kScaleComponent);
    CHECK(fnv("keen::ecs::InteractionPause") == kInteractionPause);
    CHECK(fnv("keen::ecs::InteractionOfferRuntime") == kInteractionOfferRuntime);
    CHECK(fnv("keen::ecs::ClientInteractionOffer") == kClientInteractionOffer);
    CHECK(fnv("keen::ecs::CheckPlayerInRange") == kCheckPlayerInRange);
}

static void test_level() {
    CHECK(attribute_write_rule(EMF_ATTR_XP_PENDING) == AttrWrite::GiveOnly);
    CHECK(attribute_write_rule(EMF_ATTR_LEVEL) == AttrWrite::ReadOnly);
    CHECK(attribute_write_rule(EMF_ATTR_XP) == AttrWrite::ReadOnly);
    CHECK(attribute_write_rule(EMF_ATTR_HEALTH) == AttrWrite::Clamped);
    CHECK(is_progression_attribute(EMF_ATTR_XP_REQUIRED) && !is_progression_attribute(EMF_ATTR_HEALTH));
    CHECK(level_set_xp(500, 100) == 99);
    CHECK(level_set_xp(5, 100) == 5);
    CHECK(level_set_xp(5, 0) == 0);
    CHECK(level_set_refusal(5, 45, true).empty());
    CHECK(level_set_refusal(0, 45, true) == "the level must be 1..45");
    CHECK(level_set_refusal(46, 45, true) == "the level must be 1..45");
    CHECK(!level_set_refusal(3, 0, true).empty());
    CHECK(!level_set_refusal(3, 45, false).empty());
}

static void cb_noop(EmfWorld*, const EmfUiEvent*, void*) {}

static void test_windows() {
    EmfUiWidget w[3] = {};
    w[0].type = EMF_UI_HEADING;
    w[0].text = "Beasts";
    w[1].type = EMF_UI_BUTTON;
    w[1].key = "call";
    w[1].text = "Call";
    w[1].icon = 7;
    w[2].type = EMF_UI_BAR;
    w[2].value = 5000;
    EmfWindowDef d{};
    d.size = sizeof d;
    d.id = "beastmaster.stable";
    d.title = "Stable";
    d.widgets = w;
    d.count = 3;
    d.widget_size = sizeof(EmfUiWidget);
    d.cb = &cb_noop;
    WindowRequest r;
    std::string why;
    CHECK(copy_window_def(&d, false, &r, &why));
    CHECK(r.widgets.size() == 3 && r.widgets[1].key == "call" && r.widgets[1].icon == 7);
    CHECK(r.widgets[2].value == 1000);  // clamped
    CHECK(r.width == kDefaultWindowWidth);
    // a v24 mod (56-byte widgets): icon is reserved there and reads 0; fps/height past its stride read 0
    uint8_t old[2 * 56] = {};
    EmfUiWidget a{};
    a.type = EMF_UI_SLOT;
    a.icon = 99;
    std::memcpy(old, &a, 56);
    std::memcpy(old + 56, &a, 56);
    d.widgets = reinterpret_cast<const EmfUiWidget*>(old);
    d.count = 2;
    d.widget_size = 56;
    CHECK(copy_window_def(&d, false, &r, &why));
    CHECK(r.widgets.size() == 2 && r.widgets[1].type == EMF_UI_SLOT && r.widgets[1].icon == 0 && r.widgets[1].fps == 0);
    // refusals in EMF's words
    d.widget_size = 8;
    CHECK(!copy_window_def(&d, false, &r, &why) && why.find("widget_size") != std::string::npos);
    d.widget_size = sizeof(EmfUiWidget);
    d.widgets = w;
    d.count = 3;
    d.id = "bad id!";
    CHECK(!copy_window_def(&d, false, &r, &why) && why == "id must be 1-48 of A-Z a-z 0-9 _ . -");
    d.id = "m.w";
    d.cb = nullptr;
    CHECK(!copy_window_def(&d, false, &r, &why) && why == "no callback");
    CHECK(copy_window_def(&d, true, &r, &why));  // update needs no callback
    d.close_key = 0x01;
    CHECK(!copy_window_def(&d, true, &r, &why));
    d.close_key = 'G';
    w[2].fps = 61;
    CHECK(!copy_window_def(&d, true, &r, &why) && why == "widget 2: fps must be 0 to 60");
    w[2].fps = 0;
    w[2].type = 9;
    CHECK(!copy_window_def(&d, true, &r, &why) && why == "widget 2: unknown type 9");
    d.size = 16;
    CHECK(!copy_window_def(&d, true, &r, &why));
    d.size=sizeof d;d.count=1;w[0]={};d.widget_size=UINT32_MAX;
    CHECK(!copy_window_def(&d,true,&r,&why));
    d.widget_size=sizeof(EmfUiWidget);d.flags=1;
    CHECK(!copy_window_def(&d,true,&r,&why));
    d.flags=0;d.count=UINT32_MAX;
    CHECK(!copy_window_def(&d,true,&r,&why));
    d.count=1;d.widgets=nullptr;
    CHECK(!copy_window_def(&d,true,&r,&why));
    // Partial tail fields must be defaulted as a whole, never assembled from a few bytes.
    d.widgets=w;d.widget_size=61;w[0].height=1.00001f;
    CHECK(copy_window_def(&d,true,&r,&why) && r.widgets[0].height==0);
    CHECK(widget_takes_clicks(EMF_UI_IMAGE) && !widget_takes_clicks(EMF_UI_TEXT));
}

static void test_images() {
    CHECK(portrait_key_ok("wolf") && portrait_key_ok("wolf_2.v1"));
    CHECK(!portrait_key_ok("") && !portrait_key_ok("..") && !portrait_key_ok("a..b") && !portrait_key_ok("x/y"));
    CHECK(!portrait_key_ok("CON") && !portrait_key_ok("com1.png") && !portrait_key_ok("end."));
    const uint8_t png[24] = {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 'I', 'H', 'D', 'R', 0, 0, 1, 0, 0, 0, 0, 64};
    uint32_t w = 0, h = 0;
    CHECK(png_size(png, sizeof png, &w, &h) && w == 256 && h == 64);
    CHECK(!png_size(png, 10, &w, &h));
}

// A fake server world for the component queue.
struct FakeWorld {
    std::set<uint32_t> entities;
    std::set<std::pair<uint32_t, uint32_t>> comps;
    std::map<uint32_t, std::vector<uint8_t>> data;  // entity<<0 ^ hash: last added bytes (by hash)
    bool full = false;
    ComponentEnv env() {
        ComponentEnv e;
        e.exists = [this](uint32_t id) { return entities.count(id) != 0; };
        e.has = [this](uint32_t id, uint32_t h) { return comps.count({id, h}) != 0; };
        e.is_player = [](uint32_t id) { return id >= 1 && id <= 16; };
        return e;
    }
    ComponentPush push() {
        return [this](uint32_t id, uint32_t h, bool add, const std::vector<uint8_t>& d) {
            if (full) return false;
            if (add) {
                comps.insert({id, h});
                data[h] = d;
            } else {
                comps.erase({id, h});
            }
            return true;
        };
    }
};

static void test_components() {
    CHECK(check_change(true, kScaleComponent, 4, true, 0, false).empty());
    CHECK(!check_change(true, kScaleComponent, 8, true, 0, false).empty());
    CHECK(check_change(false, kScaleComponent, 0, false, 0, false).find("removing it is not allowed") != std::string::npos);
    CHECK(check_change(false, kScaleComponent, 0, false, 0, true).empty());
    CHECK(check_change(true, kIsTame, 1, true, EMF_COMPONENT_PERSISTENT, false).find("not checked in game") != std::string::npos);
    CHECK(check_change(true, 0x12345678u, 0, false, 0, false) == "component 0x12345678 is not one EMF lets mods add");
    CHECK(check_change(true, kScaleComponent, 4, true, EMF_COMPONENT_EXPERT, false).find("reserved") != std::string::npos);
    float s = 0.05f;
    CHECK(!check_data(kScaleComponent, &s, 4).empty());
    s = 1.25f;
    CHECK(check_data(kScaleComponent, &s, 4).empty());

    EmfWorld* W = reinterpret_cast<EmfWorld*>(0x1000);
    FakeWorld f;
    f.entities = {5000, 5001, 3};
    ComponentQueue q;
    std::vector<uint32_t> statuses;
    auto req = [&](uint32_t entity, uint32_t hash, bool add, float v, const std::string& owner = "mod") {
        ComponentRequest r;
        r.world = W;
        r.session = 1;
        r.entity = entity;
        r.hash = hash;
        r.add = add;
        if (add && hash == kScaleComponent) {
            r.data.resize(4);
            std::memcpy(r.data.data(), &v, 4);
        }
        r.owner = owner;
        r.done = [&](EmfWorld*, uint32_t st, const std::string&) { statuses.push_back(st); };
        return r;
    };
    std::string why;
    std::vector<ComponentAnswer> ans;
    auto step = [&] {
        ans.clear();
        q.after_step(W, 1, f.env(), &ans);
        for (auto& a : ans) a.done(a.world, a.status, a.why);
        q.run(W, 1, f.env(), f.push());
    };
    CHECK(q.request(req(5000, kScaleComponent, true, 2.0f), &why));
    CHECK(!q.request(req(5000, kScaleComponent, true, 2.0f), &why) && why.find("under way") != std::string::npos);
    step();  // checked, pushed in the program
    CHECK(statuses.empty() && f.comps.count({5000, kScaleComponent}));
    step();  // verified
    CHECK(statuses.size() == 1 && statuses[0] == EMF_COMPONENT_DONE);
    // add again: ALREADY
    CHECK(q.request(req(5000, kScaleComponent, true, 2.0f), &why));
    step();
    CHECK(statuses.back() == EMF_COMPONENT_ALREADY);
    // the mod may remove what it added; another mod may not
    CHECK(!q.request(req(5000, kScaleComponent, false, 0, "other"), &why));
    CHECK(q.request(req(5000, kScaleComponent, false, 0), &why));
    step();
    step();
    CHECK(statuses.back() == EMF_COMPONENT_DONE && !f.comps.count({5000, kScaleComponent}));
    // gone entity, player
    CHECK(q.request(req(7777, kInteractionPause, true, 0), &why));
    step();
    CHECK(statuses.back() == EMF_COMPONENT_FAILED);
    CHECK(q.request(req(3, kInteractionPause, true, 0), &why));
    step();
    CHECK(statuses.back() == EMF_COMPONENT_FAILED);
    // NULL data: zeros of the component's size
    CHECK(q.request(req(5001, kInteractionPause, true, 0), &why));
    step();
    step();
    CHECK(statuses.back() == EMF_COMPONENT_DONE && f.data[kInteractionPause].size() == 8);
    // a full command buffer waits; a world end answers FAILED with NULL
    f.full = true;
    CHECK(q.request(req(5001, kScaleComponent, true, 1.0f), &why));
    step();
    step();
    CHECK(q.pending() == 1);
    std::vector<ComponentAnswer> end;
    q.end_all("the world ended", &end);
    CHECK(end.size() == 1 && end[0].world == nullptr && end[0].status == EMF_COMPONENT_FAILED && q.pending() == 0);
    q.enabled = false;
    CHECK(!q.request(req(5001, kScaleComponent, true, 1.0f), &why) && why.find("off") != std::string::npos);
}

static void test_offers() {
    EmfOffer o{};
    o.size = sizeof o;
    OfferSpec s;
    CHECK(offer_spec_from(&o, &s).empty());
    o.flags = 4;
    CHECK(!offer_spec_from(&o, &s).empty());
    o.flags = EMF_OFFER_BUILD_MODE;
    o.text = "";
    CHECK(offer_spec_from(&o, &s) == "text must be 1 to 63 bytes");
    o.text = nullptr;
    o.verb = EMF_VERB_OPEN;
    o.range_m = 10;
    CHECK(offer_spec_from(&o, &s).empty());
    const auto b = offer_runtime_bytes(s);
    uint32_t seq = 0, verb = 0;
    std::memcpy(&seq, b.data() + 8, 4);
    std::memcpy(&verb, b.data() + 16, 4);
    CHECK(b.size() == 56 && seq == EMF_SEQUENCE_TOUCH && verb == EMF_VERB_OPEN && b[48] == 1 && b[47] == 0);
    CHECK(offer_range_bytes(s)[0] == 10 && offer_range_bytes(OfferSpec{})[0] == 32);
    CHECK(offer_verb(OfferSpec{}) == EMF_VERB_USE);

    // the service over the real component queue and a fake world
    EmfWorld* W = reinterpret_cast<EmfWorld*>(0x1000);
    FakeWorld f;
    f.entities = {6000, 6001};
    f.comps.insert({6001, kInteractionOfferRuntime});  // a chest: the game's own offer
    ComponentQueue q;
    OfferDeps d;
    d.has = [&](uint32_t e, uint32_t h) { return h == 0 ? f.entities.count(e) != 0 : f.comps.count({e, h}) != 0; };
    d.read_offer = [&](uint32_t e) -> std::optional<std::pair<uint32_t, uint32_t>> {
        if (!f.comps.count({e, kInteractionOfferRuntime}) || !f.data.count(kInteractionOfferRuntime)) return std::nullopt;
        const auto& v = f.data[kInteractionOfferRuntime];
        uint32_t a = 0, c = 0;
        std::memcpy(&a, v.data() + 8, 4);
        std::memcpy(&c, v.data() + 16, 4);
        return std::make_pair(a, c);
    };
    d.change = [&](ComponentRequest& r, std::string* why) { return q.request(r, why); };
    OfferService svc(d);
    auto step = [&] {
        std::vector<ComponentAnswer> ans;
        q.after_step(W, 1, f.env(), &ans);
        for (auto& a : ans) a.done(a.world, a.status, a.why);
        q.run(W, 1, f.env(), f.push());
        svc.after_step(W, 1);
    };
    int accepted = 0;
    uint32_t who = 0;
    std::string why;
    CHECK(svc.offer(W, 1, 6000, s, "beastmaster", [&](EmfWorld*, uint32_t p, uint32_t) { ++accepted; who = p; }, &why));
    CHECK(svc.active(1, 6000, "beastmaster") && !svc.active(1, 6000, "other"));
    CHECK(!svc.offer(W, 1, 6000, s, "other", nullptr, &why) && why.find("another mod") != std::string::npos);
    CHECK(!svc.offer(W, 1, 6001, s, "beastmaster", nullptr, &why) && why.find("its own offer") != std::string::npos);
    step();
    step();
    CHECK(f.comps.count({6000, kInteractionOfferRuntime}) && f.comps.count({6000, kClientInteractionOffer}) &&
          f.comps.count({6000, kCheckPlayerInRange}));
    uint8_t ev[24] = {};
    uint32_t guest = 2, host = 6000;
    std::memcpy(ev + 8, &guest, 4);
    std::memcpy(ev + 12, &host, 4);
    svc.on_accept(W, 1, ev, sizeof ev);
    CHECK(accepted == 1 && who == 2);
    CHECK(svc.list().size() == 1);
    // remove: the three parts come off
    CHECK(svc.remove(W, 1, 6000, "beastmaster", &why));
    CHECK(!svc.active(1, 6000, "beastmaster"));
    step();
    step();
    CHECK(!f.comps.count({6000, kInteractionOfferRuntime}) && !f.comps.count({6000, kCheckPlayerInRange}));
    CHECK(!svc.remove(W, 1, 6000, "beastmaster", &why));
    // a gone entity is dropped by the upkeep
    CHECK(svc.offer(W, 1, 6000, s, "beastmaster", nullptr, &why));
    step();
    step();
    f.entities.erase(6000);
    for (int i = 0; i < kOfferUpkeepSteps + 1; ++i) step();
    CHECK(!svc.active(1, 6000, "beastmaster"));
    // a new session forgets everything
    f.entities.insert(6000);
    f.comps.clear(); f.data.clear(); // a new world contains none of the previous world's runtime parts
    CHECK(svc.offer(W, 2, 6000, OfferSpec{}, "m", nullptr, &why));
    CHECK(svc.active(2, 6000, "m") && !svc.active(1, 6000, "m"));
}

// A removal must fail if the entity disappeared; old-session work must never reach the command buffer.
static void test_queue_lifecycle() {
    ComponentQueue q;
    FakeWorld f; f.entities={7000};
    EmfWorld* w=reinterpret_cast<EmfWorld*>(0x1000);
    ComponentRequest r; r.world=w;r.session=1;r.entity=7000;r.hash=kInteractionOfferRuntime;r.internal=true;
    r.add=true;r.data=offer_runtime_bytes(OfferSpec{});
    std::vector<ComponentAnswer> a;
    CHECK(q.request(r,nullptr)); q.after_step(w,1,f.env(),&a);
    // Between check and push the game adds its own data: ALREADY must preserve it.
    f.comps.insert({7000,r.hash}); f.data[r.hash]={42};
    q.run(w,1,f.env(),f.push());
    CHECK(f.data[r.hash]==std::vector<uint8_t>{42});

    q.end_all("end",&a);
    r.add=false;r.data.clear();r.done=[](EmfWorld*,uint32_t,const std::string&){};
    CHECK(q.request(r,nullptr)); a.clear(); q.after_step(w,1,f.env(),&a);
    q.run(w,1,f.env(),f.push());

    f.entities.erase(7000); a.clear(); q.after_step(w,1,f.env(),&a);
    CHECK(a.size()==1 && a[0].status==EMF_COMPONENT_FAILED);
    r.add=true;r.data=offer_runtime_bytes(OfferSpec{});r.session=1;
    CHECK(q.request(r,nullptr)); a.clear();q.after_step(w,2,f.env(),&a);
    CHECK(a.size()==1 && a[0].world==nullptr);
}

static void test_offer_failures() {
    EmfWorld* w = reinterpret_cast<EmfWorld*>(0x1000);
    FakeWorld f; f.entities={8000};
    // The game already owns the client support component: removal must leave it alone.
    f.comps.insert({8000,kClientInteractionOffer});
    ComponentQueue q;
    bool refuse_range=false, ours=true, foreign=false;
    OfferDeps d;
    d.has=[&](uint32_t e,uint32_t h){return h?f.comps.count({e,h})!=0:f.entities.count(e)!=0;};
    d.change=[&](ComponentRequest& r,std::string* why){return !(refuse_range && r.add && r.hash==kCheckPlayerInRange) && q.request(r,why);};
    d.read_offer=[&](uint32_t)->std::optional<std::pair<uint32_t,uint32_t>> {
        if (foreign) return std::make_pair(kOfferDefaultSequence,EMF_VERB_OPEN);
        if (!ours) return std::nullopt;
        return std::make_pair(kOfferDefaultSequence,kOfferDefaultVerb);
    };
    OfferService svc(d);
    auto step=[&]{ std::vector<ComponentAnswer> a; q.after_step(w,1,f.env(),&a); for(auto& x:a)x.done(x.world,x.status,x.why); q.run(w,1,f.env(),f.push()); svc.after_step(w,1); };
    CHECK(svc.offer(w,1,8000,OfferSpec{},"m",nullptr,nullptr)); step();step();
    ours=false; for(int i=0;i<kOfferUpkeepSteps;++i)step();
    CHECK(!svc.active(1,8000,"m"));
    ours=true; CHECK(svc.offer(w,1,8000,OfferSpec{},"m",nullptr,nullptr));
    CHECK(svc.remove(w,1,8000,"m",nullptr)); step();step();
    CHECK(f.comps.count({8000,kClientInteractionOffer}) && !f.comps.count({8000,kInteractionOfferRuntime}));
    refuse_range=true;
    CHECK(svc.offer(w,1,8000,OfferSpec{},"m",nullptr,nullptr));
    CHECK(!svc.active(1,8000,"m"));
    for(int i=0;i<5;++i)step();
    CHECK(q.pending()==0 && !f.comps.count({8000,kInteractionOfferRuntime}) && f.comps.count({8000,kClientInteractionOffer}));
    refuse_range=false;
    CHECK(svc.offer(w,1,8000,OfferSpec{},"m",nullptr,nullptr));
    CHECK(svc.remove(w,1,8000,"m",nullptr)); // remove before any add has landed
    for(int i=0;i<5;++i)step();
    CHECK(q.pending()==0 && !f.comps.count({8000,kInteractionOfferRuntime}));
    // A game offer replaces ours between remove() and the queue's push: preserve every part.
    CHECK(svc.offer(w,1,8000,OfferSpec{},"m",nullptr,nullptr)); step();step();
    CHECK(svc.remove(w,1,8000,"m",nullptr));
    foreign=true; for(int i=0;i<5;++i)step();
    CHECK(f.comps.count({8000,kInteractionOfferRuntime}) && f.comps.count({8000,kCheckPlayerInRange}));
    foreign=false; f.comps.erase({8000,kInteractionOfferRuntime}); f.comps.erase({8000,kCheckPlayerInRange});
    // Game wins the race for the runtime offer: never remove its data during rollback.
    CHECK(svc.offer(w,1,8000,OfferSpec{},"m",nullptr,nullptr));
    f.comps.insert({8000,kInteractionOfferRuntime}); f.data[kInteractionOfferRuntime]={99};
    for(int i=0;i<5;++i)step();
    CHECK(!svc.active(1,8000,"m") && f.data[kInteractionOfferRuntime]==std::vector<uint8_t>{99});
}

static void wheel_cb(EmfWorld*, int, void*) {}

static void test_wheel() {
    const char* const items[] = {"Wolf", "Bear (level 3)", "Fox"};
    EmfWheel w{};
    w.size = sizeof w;
    w.title = "Pets";
    w.items = items;
    w.count = 3;
    w.world_kind = EMF_WORLD_CLIENT;
    w.cb = wheel_cb;
    int token = 0;
    w.user = &token;
    WheelRequest r;
    std::string why;
    CHECK(copy_wheel_def(&w, &r, &why) && why.empty());
    CHECK(r.title == "Pets" && r.items.size() == 3 && r.items[1] == "Bear (level 3)" && r.hold_vk == 0 && r.world_kind == EMF_WORLD_CLIENT &&
          r.cb == wheel_cb && r.user == &token);
    // the strings are copies
    std::string label = "Mutable";
    const char* mutable_items[] = {label.c_str()};
    EmfWheel c = w;
    c.items = mutable_items;
    c.count = 1;
    CHECK(copy_wheel_def(&c, &r, &why));
    label = "Changed";
    CHECK(r.items[0] == "Mutable");
    // a title and labels are optional / cut, never refused for their length
    EmfWheel e = w;
    e.title = nullptr;
    const char* with_null[] = {nullptr, "ok"};
    e.items = with_null;
    e.count = 2;
    CHECK(copy_wheel_def(&e, &r, &why) && r.title.empty() && r.items[0].empty() && r.items[1] == "ok");
    const std::string longer(400, 'x');
    const char* long_items[] = {longer.c_str()};
    e.items = long_items;
    e.count = 1;
    e.title = longer.c_str();
    CHECK(copy_wheel_def(&e, &r, &why) && r.items[0].size() == kMaxWheelLabelLen && r.title.size() == kMaxWheelTitleLen);
    // a cut never lands inside a UTF-8 sequence: 255 bytes of "a", then a two-byte character straddling byte 256
    const std::string straddle = std::string(255, 'a') + "\xC3\xA9" + "zzz";
    const char* straddle_items[] = {straddle.c_str()};
    e.items = straddle_items;
    CHECK(copy_wheel_def(&e, &r, &why) && r.items[0] == std::string(255, 'a'));

    // EMF's refusals: count 1..64, items, size, hold key, world kind, callback
    EmfWheel bad = w;
    bad.count = 0;
    CHECK(!copy_wheel_def(&bad, &r, &why) && why.find("1 to 64") != std::string::npos);
    bad.count = 65;
    CHECK(!copy_wheel_def(&bad, &r, &why));
    bad.count = 64;
    std::vector<const char*> many(64, "e");
    bad.items = many.data();
    CHECK(copy_wheel_def(&bad, &r, &why) && r.items.size() == 64);
    bad = w;
    bad.items = nullptr;
    CHECK(!copy_wheel_def(&bad, &r, &why));
    bad = w;
    bad.size = kWheelV10Size - 1;
    CHECK(!copy_wheel_def(&bad, &r, &why));
    CHECK(!copy_wheel_def(nullptr, &r, &why));
    bad = w;
    bad.cb = nullptr;
    CHECK(!copy_wheel_def(&bad, &r, &why) && why == "no callback");
    bad = w;
    bad.world_kind = 7;
    CHECK(!copy_wheel_def(&bad, &r, &why));
    bad = w;
    bad.world_kind = EMF_WORLD_SERVER;
    CHECK(copy_wheel_def(&bad, &r, &why));
    // hold keys: 0 or a generic keyboard code
    for (uint32_t ok : {0u, 0x10u, 0x11u, 0x12u, 0x20u, uint32_t{'Q'}, 0x70u, 0xFFu}) {
        bad = w;
        bad.hold_key = ok;
        CHECK(copy_wheel_def(&bad, &r, &why) && r.hold_vk == ok);
    }
    for (uint32_t no : {1u, 2u, 4u, 5u, 6u, 0xA0u, 0xA1u, 0xA4u, 0xA5u, 0x100u, 0xFFFFFFFFu}) {
        bad = w;
        bad.hold_key = no;
        CHECK(!copy_wheel_def(&bad, &r, &why) && why.find("hold_key") != std::string::npos);
    }
    CHECK(valid_hold_key(0) && valid_hold_key(0x10) && !valid_hold_key(0xA0) && !valid_hold_key(1));
    // an EmfWheel built with a larger (future) size field is read the same way
    bad = w;
    bad.size = sizeof w + 64;
    CHECK(copy_wheel_def(&bad, &r, &why));
}

int main() {
    test_wheel();
    test_offer_failures();
    test_queue_lifecycle();
    test_table();
    test_note_rows();
    test_level();
    test_windows();
    test_images();
    test_components();
    test_offers();
    std::printf("v26_model_test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
