// SPDX-License-Identifier: MIT
// quests/*.json -> the game's resources. The JSON format: docs/kiln/services/npcs.md.
//
// What becomes what (evidence: docs/re/quests.md, docs/re/creatures.md section 7). The game's UI is the default: only
// "journal": "chat" and "native": false stay out of it.
//  - every quest: a JournalQuestResource + knowledge objects for its state (Kiln.Quests/Native mirrors the engine's state into
//    them), player objects unless the quest's scope is "world"; a step's counted objectives a Counter the journal shows;
//  - every dialogue: lines in its NPC's NpcDialog: the start node's text is an init line (the greeting), every option a
//    dialogOption whose reply is the target node's text. The game has one flat topic list and one requirement per line, so a node
//    after the start node gets a gate knowledge object set by the option that leads to it. What one knowledge check cannot say
//    (all, any, not, level, item, predicate, a gate and an "if", a hand-in's items) becomes a knowledge object of its own that the
//    engine keeps true or false for each player ("computed" in the manifest);
//  - an NPC without a dialogue that gives quests or takes deliveries: a topic for each;
//  - every line id is a player knowledge object: the server pushes {line, Bool, 1} ("heard") and the client evaluates the
//    id as a query and hides the line when the id is unknown (client 0x460b40);
//  - every text a loca tag (kiln.content's loca hooks answer it on clients);
//  - every NPC clone a new TemplateResource under its own GUID (NpcId = FNV-1a of the GUID, as the game's roster),
//    a roster entry when it has "roster";
//  - every step with a place (a marker, site, spawn, reach or kill place, an NPC of the mod) a new TemplateResource (a copy of
//    a search-area marker) with its own name tag, visible while the step is active; Kiln.Quests/Native puts it at the place.
#include "questpack.h"

#include <cstdio>
#include <cstring>
#include <map>
#include <set>

namespace kiln::qb {

namespace {

const char* const kNs = "6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77";  // Kiln's namespace for derived GUIDs

// keen::QuestSourceType
const char* const kSources[] = {"None", "Flame", "Blacksmith", "Alchemist", "Huntress", "Farmer", "Carpenter",
                                "CryptKeeper", "Bard", "ChineseNewYearTrader", "Barber", "Fisher", "AncientResearcher",
                                "Grassland", "Deepforest", "Steppes", "Desert", "ColdHeights", "Wetlands", "AncientLands"};
// The marker every quest step marker copies unless it names another: search-area label with a visibility requirement
constexpr const char* kMarkerBase = "8kMapLabel_townsfolk_female_03_GabrielleFairburn_searchArea_01_noUI";

// A top-level section of a document (an empty object when absent): safe to iterate with items().
const ojson& section(const ojson& doc, const char* k) {
    static const ojson empty = ojson::object();
    auto it = doc.find(k);
    return it != doc.end() && it->is_object() ? *it : empty;
}
std::string hex32(uint32_t v) {
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", v);
    return b;
}
std::string str(const ojson& j, const char* k, const std::string& d = "") {
    auto it = j.find(k);
    return it != j.end() && it->is_string() ? it->get<std::string>() : d;
}
bool truthy(const ojson& j, const char* k, bool d = false) {
    auto it = j.find(k);
    return it != j.end() && it->is_boolean() ? it->get<bool>() : d;
}

struct Ctx {
    const GameData& game;
    PackOut& out;
    const ModInput* mod = nullptr;
    std::vector<std::string> errors;  // of the current mod
    std::map<std::string, uint32_t> names;              // knowledge name (of this mod) -> id
    std::map<uint32_t, KnowledgeDef> knowledge;         // of the current mod
    ojson m_knowledge = ojson::object(), m_quests = ojson::array(), m_lines = ojson::array(), m_npcs = ojson::array(),
          m_markers = ojson::array(), m_computed = ojson::array();
    std::vector<JournalQuestDef> quests;
    std::vector<NpcDef> npcs;
    std::vector<TextOut> texts;
    std::vector<std::string> items, recipes;
    struct Tpl {
        Guid base, guid;
        TemplateEdit edit;
    };
    std::vector<Tpl> templates;
    std::map<std::string, const ojson*> dialogues;  // of this mod
    std::map<std::string, const ojson*> doc_quests;     // the quests in the game's journal
    std::map<std::string, const ojson*> doc_npcs;

    void err(const std::string& s) { errors.push_back(mod->id + ": " + s); }
    uint32_t id(const std::string& kind, const std::string& name) const { return derive_id(mod->id, kind, name); }

    uint32_t add_knowledge(const std::string& name, uint8_t type, bool world, bool player, uint32_t flags = 0) {
        const uint32_t i = id("know", name);
        auto [it, fresh] = knowledge.try_emplace(i);
        KnowledgeDef& d = it->second;
        if (fresh) {
            d.id = i, d.type = type, d.name = mod->id + ":" + name, d.expected_flags = flags;
            m_knowledge[name] = hex32(i);
        }
        d.world = d.world || world;
        d.player = d.player || player;
        names[name] = i;
        return i;
    }
    uint32_t text(const std::string& path, const ojson& j) {
        if (j.is_null()) return 0;
        const uint32_t tag = id("text", path);
        TextOut t;
        t.tag = tag;
        t.json = j.is_string() ? ojson({{"*", j}}).dump() : j.dump();
        texts.push_back(std::move(t));
        return tag;
    }
    Guid voice(const ojson& j, const std::string& where) {
        // "<line>" (this mod's voice-over), "<mod>:<line>" (needs that mod's guidNamespace: not resolvable here), or a GUID
        if (!j.is_string()) return {};
        const std::string s = j.get<std::string>();
        Guid g;
        if (parse_guid(s, &g)) return g;
        if (mod->audio.is_object()) {
            auto v = mod->audio.find("voiceovers");
            if (v != mod->audio.end() && v->contains(s) && (*v)[s].contains("guid") && parse_guid((*v)[s]["guid"].get<std::string>(), &g))
                return g;
        }
        Guid ns;
        if (!mod->guid_namespace.empty() && parse_guid(mod->guid_namespace, &ns)) return uuid5(ns, "voiceover:" + s);
        err(where + ": voice \"" + s + "\" is not in assets/cooked/audio.json and the mod has no assets/cook.json");
        return {};
    }
    bool find_template(const std::string& s, Guid* g) {
        if (parse_guid(s, g)) return true;
        if (game.template_by_name && game.template_by_name(s, g)) return true;
        return false;
    }
    Tpl* template_for(const Guid& guid) {
        for (Tpl& t : templates)
            if (t.guid == guid) return &t;
        return nullptr;
    }
    // A quest of this mod whose "available" is more than "not started yet": the engine knows.
    bool plain_available(const std::string& qn) const {
        auto it = doc_quests.find(qn);
        if (it == doc_quests.end()) return false;
        const ojson& q = *it->second;
        auto req = q.find("requires");
        return (req == q.end() || req->empty()) && !truthy(q, "repeatable") && truthy(q, "retryable", true);
    }
    // One knowledge check for a condition, when one can say it: the game has exactly one per line. False: it takes the engine.
    bool direct_condition(const ojson& c, Query* q) {
        if (!c.is_object()) return false;
        if (auto it = c.find("quest"); it != c.end() && it->is_string()) {
            const std::string qn = it->get<std::string>(), is = str(c, "is", "done");
            if (!doc_quests.count(qn)) return false;
            if (is == "available" && plain_available(qn)) *q = query_unset(id("know", "q." + qn + ".started"));
            else if (is == "active" || is == "done" || is == "started") *q = query_bool(id("know", "q." + qn + "." + is));
            else return false;
            return true;
        }
        if (auto it = c.find("step"); it != c.end() && it->is_string()) {
            const std::string sn = it->get<std::string>(), is = str(c, "is", "done");
            const size_t dot = sn.find('.');
            if (dot == std::string::npos || !doc_quests.count(sn.substr(0, dot)) || (is != "active" && is != "done")) return false;
            *q = query_bool(id("know", "s." + sn + "." + is));
            return true;
        }
        if (auto it = c.find("knowledge"); it != c.end() && it->is_string()) {
            uint32_t k = 0;
            if (!resolve_quiet(it->get<std::string>(), &k)) return false;
            const bool want = !c.contains("is") || (c["is"].is_boolean() && c["is"].get<bool>());
            *q = want ? query_bool(k) : query_unset(k);
            return true;
        }
        return false;
    }
    // The knowledge object the engine keeps for what a line cannot check itself.
    Query computed(const std::string& name, const char* kind, const std::string& target, const std::string& node, int index, uint32_t gate, uint32_t heard) {
        const uint32_t k = add_knowledge(name, kBool, false, true);
        ojson e = {{"id", hex32(k)}, {"kind", kind}, {"target", target}};
        if (!node.empty()) e["node"] = node;
        if (index >= 0) e["index"] = index;
        if (gate) e["gate"] = hex32(gate);
        if (heard) e["heard"] = hex32(heard);
        m_computed.push_back(std::move(e));
        return query_bool(k);
    }
    // A quest's step that takes items from the player ("<quest>.<step>"): its hand-in is on offer only with them.
    bool is_handin(const std::string& sn) const {
        const size_t dot = sn.find('.');
        if (dot == std::string::npos) return false;
        auto it = doc_quests.find(sn.substr(0, dot));
        if (it == doc_quests.end()) return false;
        for (const auto& s : it->second->value("steps", ojson::array()))
            if (str(s, "id") == sn.substr(dot + 1) && has_deliver(s)) return true;
        return false;
    }
    static bool has_deliver(const ojson& s) {
        if (s.contains("objective") && s["objective"].is_object() && s["objective"].contains("deliver")) return true;
        for (const auto& o : s.value("objectives", ojson::array()))
            if (o.is_object() && o.contains("deliver")) return true;
        return false;
    }
    // The hand-in step an option advances, "" when it advances none.
    std::string handin_of(const ojson& option) const {
        for (const auto& a : option.value("do", ojson::array()))
            if (a.is_object() && a.contains("advance") && a["advance"].is_string() && is_handin(a["advance"].get<std::string>()))
                return a["advance"].get<std::string>();
        return {};
    }
    Query ready(const std::string& sn) { return query_bool(id("know", "s." + sn + ".ready")); }
    // A name the boot build can turn into an id by itself; others ("quest:…", "query:…", "line:…") take the engine.
    bool resolve_quiet(const std::string& s, uint32_t* out) const {
        if (auto it = names.find(s); it != names.end()) {
            *out = it->second;
            return true;
        }
        if (s.rfind("0x", 0) == 0) {
            *out = static_cast<uint32_t>(std::stoul(s, nullptr, 16));
            return true;
        }
        if (s.rfind("npc:", 0) == 0) {
            std::string dn = s.substr(4);
            if (dn.size() > 7 && dn.compare(dn.size() - 7, 7, ".unlock") == 0) dn.resize(dn.size() - 7);
            return npc_unlock(dn, out);
        }
        return false;
    }
    // Knowledge names: this mod's, "0x…" (the game's or another mod's id), "npc:<debugName>[.unlock]".
    bool resolve(const std::string& s, uint32_t* out, const std::string& where) {
        if (resolve_quiet(s, out)) return true;
        if (s.rfind("npc:", 0) == 0) {
            err(where + ": \"" + s + "\": no such NPC in the roster, or it has no unlock requirement");
            return false;
        }
        err(where + ": unknown knowledge \"" + s + "\" (declare it in \"knowledge\", or use 0x… or npc:<debugName>.unlock)");
        return false;
    }
    bool npc_unlock(const std::string& debug_name, uint32_t* out) const {
        const uint8_t* b = nullptr;
        size_t n = 0;
        Guid g;
        parse_guid(kGuidNpcs, &g);
        if (!game.read(g, kTypeNpcs, &b, &n)) return false;
        int32_t rel;
        uint32_t cnt;
        std::memcpy(&rel, b, 4);
        std::memcpy(&cnt, b + 4, 4);
        for (uint32_t i = 0; i < cnt; ++i) {
            const uint8_t* e = b + rel + uint64_t{i} * 136;
            int32_t srel;
            uint32_t slen;
            std::memcpy(&srel, e + 0x80, 4);
            std::memcpy(&slen, e + 0x84, 4);
            if (slen && debug_name == std::string(reinterpret_cast<const char*>(e + 0x80 + srel), slen)) {
                uint32_t id;
                std::memcpy(&id, e + 0x10, 4);
                *out = id;
                return id != 0;
            }
        }
        return false;
    }
};

uint8_t hint_of(const std::string& s) {
    if (s == "Hint") return 1;
    if (s == "Attention") return 2;
    if (s == "MainQuest") return 3;
    return 0;
}

// The always-true requirement every unconditional option needs (a line whose requirement does not evaluate is hidden;
// the query is "this world counter is 0" on a Kiln-owned object nothing ever sets).
Query always(Ctx& c) {
    const uint32_t k = c.add_knowledge("kiln.always", kCounter, true, false);
    return Query{k, 0, kCmpEquals, kQueryCount, 0};
}

// Every objective of a step ("objective" and "objectives").
template <class F>
void for_objectives(const ojson& s, F&& f) {
    if (auto it = s.find("objective"); it != s.end() && it->is_object()) f(*it);
    if (auto it = s.find("objectives"); it != s.end() && it->is_array())
        for (const auto& o : *it)
            if (o.is_object()) f(o);
}

// The journal's counter of a step: what its counted objectives add up to (a hand-in, a place or a talk is one thing to do).
uint32_t counted_total(const ojson& s) {
    uint32_t total = 0;
    for_objectives(s, [&](const ojson& o) {
        for (const char* k : {"kill", "collect", "craft", "build"})
            if (o.contains(k) && o[k].is_object()) total += o[k].value("count", 1u);
        if (o.contains("custom")) total += o["custom"].is_object() ? o["custom"].value("count", 1u) : o.value("count", 1u);
    });
    return total;
}

// A step has a place the player should be shown: the map marker comes by itself.
bool step_has_place(Ctx& c, const ojson& s) {
    if (s.contains("site") || s.contains("spawn")) return true;
    bool place = false;
    for_objectives(s, [&](const ojson& o) {
        if (o.contains("reach") && o["reach"].is_object() && !o["reach"].contains("marker")) place = true;
        if (o.contains("kill") && o["kill"].is_object() && o["kill"].contains("near")) place = true;
        for (const auto& [kind, key] : {std::pair<const char*, const char*>{"talk", "npc"}, {"deliver", "to"}})
            if (o.contains(kind) && o[kind].is_object() && c.doc_npcs.count(str(o[kind], key))) place = true;
    });
    return place;
}

void compile_quest(Ctx& c, const std::string& qid, const ojson& q) {
    const std::string where = "quest " + qid;
    const std::string scope = str(q, "scope", "player");
    const bool world = scope == "world";
    JournalQuestDef d;
    d.entry_id = c.id("know", "q." + qid);  // the quest's own player Bool, as the game: trigger_quest_rewards sets it
    c.add_knowledge("q." + qid, kBool, false, true);
    const uint32_t started = c.add_knowledge("q." + qid + ".started", kBool, world, !world);
    const uint32_t active = c.add_knowledge("q." + qid + ".active", kBool, world, !world);
    const uint32_t done = c.add_knowledge("q." + qid + ".done", kBool, world, !world);
    d.name = c.text("quest." + qid + ".name", q.value("name", ojson()));
    if (!d.name) c.err(where + ": \"name\" is required for a journal entry");
    const std::string source = str(q, "source", "None");
    int src = -1;
    for (int i = 0; i < 20; ++i)
        if (source == kSources[i]) src = i;
    if (src < 0) {
        c.err(where + ": unknown \"source\" " + source + " (a keen::QuestSourceType: None, Flame, Blacksmith, ...)");
        src = 0;
    }
    d.source = static_cast<uint8_t>(src);
    d.type = world ? 1 : 2;  // keen::QuestType WorldQuest / PlayerQuest
    d.main = truthy(q, "main") ? 1 : 0;
    d.priority = q.value("priority", 50u);  // the game's quests use 40..100
    d.document = d.name;                   // the journal's document name: every quest of the game has one
    ojson ms = ojson::array();
    const ojson steps = q.value("steps", ojson::array());
    if (!steps.is_array() || steps.empty()) c.err(where + ": a native quest needs steps");
    for (const auto& s : steps) {
        const std::string sid = str(s, "id");
        if (sid.empty()) {
            c.err(where + ": a step without \"id\"");
            continue;
        }
        const std::string sw = qid + "." + sid;
        JournalStepDef st;
        st.entry_id = c.id("journal", "s." + sw);
        const uint32_t vis = c.add_knowledge("s." + sw + ".visible", kBool, world, !world);
        const uint32_t act = c.add_knowledge("s." + sw + ".active", kBool, world, !world);
        const uint32_t dn = c.add_knowledge("s." + sw + ".done", kBool, world, !world);
        const uint32_t text = c.text("step." + sw + ".text", s.value("text", ojson()));
        const uint32_t title = c.text("step." + sw + ".title", s.value("title", ojson()));
        if (!text && !title) c.err(where + " step " + sid + ": a step in the journal needs \"text\"");
        // The game's 406 steps all have both: name is the short line (the journal's list, the tracked quest), text the paragraph.
        // A first step with no title of its own: its text is the short line, the quest's description the paragraph.
        st.name = title ? title : text;
        st.text = text ? text : title;
        if (!title && d.steps.empty() && q.contains("description")) st.text = c.text("quest." + qid + ".description", q["description"]);
        st.visible = query_bool(vis);
        st.done = query_bool(dn);
        // the game's shape: every SimpleBool step of its PlayerQuests says it reads the player's object, not the world's
        st.visible.explicit_player = st.done.explicit_player = world ? 0 : 1;
        st.level = s.value("recommendedLevel", 0u);
        if (s.contains("voice")) st.voiceover = c.voice(s["voice"], where + " step " + sid);
        ojson ml = {{"id", sid}, {"visible", hex32(vis)}, {"active", hex32(act)}, {"done", hex32(dn)}};
        if (const uint32_t total = counted_total(s); total > 1) {
            const uint32_t count = c.add_knowledge("s." + sw + ".count", kCounter, world, !world);
            st.has_progress = true;
            st.progress = query_progress(count, total);
            ml["count"] = hex32(count);
            ml["total"] = total;
        }
        if (Ctx::has_deliver(s)) {
            const uint32_t ready = c.add_knowledge("s." + sw + ".ready", kBool, false, true);
            ml["ready"] = hex32(ready);
            c.m_computed.push_back({{"id", hex32(ready)}, {"kind", "ready"}, {"target", c.mod->id + ":" + sw}});
        }
        // a map marker: a copy of a search-area marker with its own name, shown while the step is active. Where it stands is
        // decided at run time (Kiln.Quests/Places.cs); a step with no place has none unless it asks ("marker": {...}).
        const auto mk0 = s.find("marker");
        const bool no_marker = mk0 != s.end() && mk0->is_boolean() && !mk0->get<bool>();
        static const ojson no_keys = ojson::object();
        const ojson& mks = mk0 != s.end() && mk0->is_object() ? *mk0 : no_keys;
        if (!no_marker && (mk0 != s.end() || step_has_place(c, s))) {
            const ojson* mk = &mks;
            const std::string base = str(*mk, "base", kMarkerBase);
            Guid bg;
            if (!c.find_template(base, &bg)) {
                c.err(where + " step " + sid + ": marker base template \"" + base + "\" not found");
            } else {
                Ctx::Tpl t;
                t.base = bg;
                t.guid = derive_guid(c.mod->id, "marker", sw);
                t.edit.new_name = "Kiln_Marker_" + c.mod->id + "_" + qid + "_" + sid;
                t.edit.has_marker = true;
                t.edit.marker_tag = derive_guid(c.mod->id, "markertag", sw);
                t.edit.has_loca = true;
                t.edit.loca_tag = t.edit.marker_tag;
                t.edit.has_marker_req = true;
                t.edit.marker_visible = query_bool(act);
                t.edit.marker_hint = Query{};
                t.edit.marker_completed = Query{};  // as the game's search areas: gone from the map when the step is over
                TextOut tx;
                tx.tag = fnv(t.edit.marker_tag);
                const ojson nm = mk->value("name", s.value("text", ojson(sid)));
                tx.json = nm.is_string() ? ojson({{"*", nm}}).dump() : nm.dump();
                c.texts.push_back(std::move(tx));
                st.marker = fnv(t.edit.marker_tag);
                c.m_markers.push_back({{"quest", qid}, {"step", sid}, {"template", t.edit.new_name},
                                       {"guid", guid_text(t.guid)}, {"at", mk->value("at", ojson())}, {"radius", mk->value("radius", 0.0)}});
                c.templates.push_back(std::move(t));
            }
        }
        d.steps.push_back(std::move(st));
        ms.push_back(std::move(ml));
    }
    c.quests.push_back(std::move(d));
    c.m_quests.push_back({{"id", qid}, {"fullId", c.mod->id + ":" + qid}, {"scope", scope}, {"entryId", hex32(c.id("know", "q." + qid))},
                          {"started", hex32(started)}, {"active", hex32(active)}, {"done", hex32(done)}, {"steps", std::move(ms)}});
}

LineDef line_base(Ctx& c, const std::string& name) {
    LineDef l;
    l.id = c.add_knowledge("line." + name, kBool, false, true);
    return l;
}

void compile_dialogue(Ctx& c, const std::string& dlg, const ojson& d, Ctx::Tpl& target, const std::string& npc_id) {
    const std::string where = "dialogue " + dlg;
    DialogEdit& e = target.edit.dialog;
    e.any = true;
    const std::string start = str(d, "start", "start");
    const ojson nodes = d.value("nodes", ojson::object());
    if (!nodes.contains(start)) {
        c.err(where + ": \"start\" must name a node (default \"start\")");
        return;
    }
    const std::string full = c.mod->id + ":" + dlg;
    auto lines_add = [&](LineDef&& l, const std::string& node, int option) {
        c.m_lines.push_back({{"dialogue", full}, {"npc", npc_id}, {"node", node}, {"option", option},
                             {"line", hex32(l.id)}});
    };
    // the greeting: a clone's start node text; on a game NPC only with "greet": true (its own greetings stay, and a plain
    // greeting of a mod would compete with them at random)
    if (truthy(d, "greet", !target.edit.new_name.empty())) {
        const ojson& n = nodes[start];
        LineDef l = line_base(c, dlg + "." + start);
        l.bodies.push_back(c.text("dlg." + dlg + "." + start + ".text", n.value("text", ojson())));
        if (!l.bodies[0]) c.err(where + " node " + start + ": a greeting needs \"text\"");
        l.voiceovers.push_back(n.contains("voice") ? c.voice(n["voice"], where + " node " + start) : Guid{});
        l.hint = hint_of(str(n, "hint", str(d, "hint")));
        LineDef copy = l;
        lines_add(std::move(copy), start, -1);
        e.init.push_back(std::move(l));
    }
    // extra greetings: {"if": cond, "text", "voiceover", "hint"}: shown before the plain one while the condition holds
    int gi = 0;
    for (const auto& g : d.value("greetings", ojson::array())) {
        const int gindex = gi++;
        const std::string path = dlg + ".greeting" + std::to_string(gindex);
        LineDef l = line_base(c, path);
        if (g.contains("if") && !c.direct_condition(g["if"], &l.requirement))
            l.requirement = c.computed("cond." + path, "greeting", full, "", gindex, 0, 0);
        l.bodies.push_back(c.text("dlg." + path + ".text", g.value("text", ojson())));
        if (!l.bodies[0]) {
            c.err(where + ": a greeting needs \"text\"");
            continue;
        }
        l.voiceovers.push_back(g.contains("voice") ? c.voice(g["voice"], where + " greeting") : Guid{});
        l.hint = hint_of(str(g, "hint", "Attention"));
        e.init.push_back(std::move(l));
    }
    // options: one per node option, in the file's order
    for (auto it = nodes.begin(); it != nodes.end(); ++it) {
        const std::string node = it.key();
        const ojson opts = it.value().value("options", ojson::array());
        int oi = 0;
        for (const auto& o : opts) {
            const int index = oi++;
            const std::string path = dlg + "." + node + "." + std::to_string(index);
            const std::string w = where + " node " + node + " option " + std::to_string(index);
            LineDef l = line_base(c, path);
            l.extra = c.text("dlg." + path + ".option", o.value("text", ojson()));
            if (!l.extra) c.err(w + ": an option needs \"text\"");
            const std::string next = str(o, "goto", "end");
            const ojson* tn = next != "end" && nodes.contains(next) ? &nodes[next] : nullptr;
            if (next != "end" && !tn) c.err(w + ": goto \"" + next + "\" is not a node");
            const ojson reply = o.contains("reply") ? o["reply"] : (tn && tn->contains("text") ? (*tn)["text"] : o.value("text", ojson()));
            l.bodies.push_back(c.text("dlg." + path + ".reply", reply));
            const ojson* vo = o.contains("voice") ? &o["voice"] : (tn && tn->contains("voice") ? &(*tn)["voice"] : nullptr);
            l.voiceovers.push_back(vo ? c.voice(*vo, w) : Guid{});
            l.hint = hint_of(str(o, "hint"));
            if (truthy(o, "close")) l.end_action = 1;  // keen::DialogAction PlayOutro
            // One line, one knowledge check. Whatever one check can say stays the game's own; the rest takes the engine.
            const bool gated = node != start, once = truthy(o, "once"), has_if = o.contains("if");
            const std::string handin = c.handin_of(o);
            const uint32_t gate = gated ? c.add_knowledge("gate." + dlg + "." + node, kBool, false, true) : 0;
            Query direct;
            const bool if_direct = has_if && c.direct_condition(o["if"], &direct);
            // a hand-in with {"step": it, "is": "active"} or no "if": on offer while the player holds the items
            const bool plain_handin = !handin.empty() && !gated && !once &&
                (!has_if || (o["if"].is_object() && str(o["if"], "step") == handin && str(o["if"], "is", "done") == "active"));
            if (plain_handin) l.requirement = c.ready(handin);
            else if (!handin.empty() || int(gated) + int(once) + int(has_if) > 1 || (has_if && !if_direct))
                l.requirement = c.computed("cond." + path, "if", full, node, index, gate, once ? l.id : 0);
            else if (has_if) l.requirement = direct;
            else if (gated) l.requirement = query_bool(gate);
            else if (once) l.requirement = query_unset(l.id);
            else l.requirement = always(c);
            if (tn && tn->contains("options")) l.ops.push_back(OpDef{c.add_knowledge("gate." + dlg + "." + next, kBool, false, true), kBool, 1});
            for (const auto& s : o.value("set", ojson::array())) {
                uint32_t k = 0;
                if (s.is_string() && c.resolve(s.get<std::string>(), &k, w)) l.ops.push_back(OpDef{k, kBool, 1});
            }
            LineDef copy = l;
            lines_add(std::move(copy), node, index);
            e.options.push_back(std::move(l));
        }
    }
    // the farewell and transition lines (voice-over optional): clones keep their base's unless given
    int fi = 0;
    for (const auto& f : d.value("farewell", ojson::array())) {
        const std::string path = dlg + ".farewell" + std::to_string(fi++);
        LineDef l;  // farewell lines are not knowledge in the game's data
        l.id = c.id("line", path);
        l.bodies.push_back(c.text("dlg." + path + ".text", f.is_object() ? f.value("text", ojson()) : f));
        l.voiceovers.push_back(f.is_object() && f.contains("voice") ? c.voice(f["voice"], where + " farewell") : Guid{});
        l.requirement = Query{};
        e.farewell.push_back(std::move(l));
        e.replace_farewell = true;
    }
    int ti = 0;
    for (const auto& f : d.value("transition", ojson::array())) {
        const std::string path = dlg + ".transition" + std::to_string(ti++);
        LineDef l;
        l.id = c.id("line", path);
        l.bodies.push_back(c.text("dlg." + path + ".text", f.is_object() ? f.value("text", ojson()) : f));
        l.voiceovers.push_back(f.is_object() && f.contains("voice") ? c.voice(f["voice"], where + " transition") : Guid{});
        e.transition.push_back(std::move(l));
        e.replace_transition = true;
    }
}

// An NPC without a dialogue that gives quests or takes deliveries: a topic for each, by the quests' ids. "auto" in the manifest
// says what the line does: nothing else defines it.
void compile_auto_topics(Ctx& c, const std::string& nid, Ctx::Tpl& target) {
    DialogEdit& e = target.edit.dialog;
    int index = 0;
    auto add = [&](const std::string& what, const ojson& option_text, const ojson& reply, const Query& requirement) {
        const std::string path = "auto." + nid + ".topic." + std::to_string(index);
        LineDef l = line_base(c, path);
        l.extra = c.text("dlg." + path + ".option", option_text);
        l.bodies.push_back(c.text("dlg." + path + ".reply", reply));
        l.voiceovers.push_back(Guid{});
        l.requirement = requirement;
        l.hint = 2;  // Attention
        e.any = true;
        c.m_lines.push_back({{"dialogue", c.mod->id + ":auto." + nid}, {"npc", nid}, {"node", "topic"}, {"option", index},
                             {"line", hex32(l.id)}, {"auto", what}});
        e.options.push_back(std::move(l));
        ++index;
    };
    for (const auto& [qn, qp] : c.doc_quests) {
        const ojson& q = *qp;
        if (str(q, "giver") == nid) {
            const Query req = c.plain_available(qn) ? query_unset(c.id("know", "q." + qn + ".started"))
                                                    : c.computed("cond.auto." + qn + ".available", "available", c.mod->id + ":" + qn, "", -1, 0, 0);
            add("start:" + qn, q.value("name", ojson(qn)), q.value("description", q.value("name", ojson(qn))), req);
        }
        for (const auto& s : q.value("steps", ojson::array())) {
            bool takes = false;
            for_objectives(s, [&](const ojson& o) { takes = takes || (o.contains("deliver") && o["deliver"].is_object() && str(o["deliver"], "to") == nid); });
            if (takes) add("advance:" + qn + "." + str(s, "id"), s.value("text", ojson(qn)), s.value("text", ojson(qn)), c.ready(qn + "." + str(s, "id")));
        }
    }
}

// Does an NPC without a dialogue have quests to give or deliveries to take?
bool has_auto_topics(const Ctx& c, const std::string& nid) {
    for (const auto& [qn, qp] : c.doc_quests) {
        (void)qn;
        if (str(*qp, "giver") == nid) return true;
        for (const auto& s : qp->value("steps", ojson::array())) {
            bool takes = false;
            for_objectives(s, [&](const ojson& o) { takes = takes || (o.contains("deliver") && o["deliver"].is_object() && str(o["deliver"], "to") == nid); });
            if (takes) return true;
        }
    }
    return false;
}

// The game's Survivors list draws a roster NPC from its summon tool: the row's title is the item's name and the page's text is the
// item's description; only the page's title is the roster entry's own name (client 26430625: the row 0xc046c0 reads ItemInfo +0x48,
// the page 0xc46f1f reads +0x68, docs/re/creatures.md 7.6). So all 35 of the game's tools carry their NPC's name and a line about
// them ("Captain Melville Fontane", "... is a fisher and entrepreneur who offers fishing gear."). A definition's tool gets the same:
// the NPC's names and its "description", per language. What the tool is goes in its caption or lore.
ojson summon_item_def(Ctx& c, const ojson& npc, ojson def, const std::string& where) {
    def.erase("comment");
    ojson texts = def.contains("texts") && def["texts"].is_object() ? def["texts"] : ojson::object();
    const auto put = [&](const ojson& from, const char* slot) {
        if (from.is_string()) {
            texts["*"][slot] = from;
        } else if (from.is_object()) {
            for (auto it = from.begin(); it != from.end(); ++it) texts[it.key()][slot] = it.value();
        }
    };
    const auto own = [&](const char* flat, const char* slot) {  // the definition's own text of a slot, any language
        if (def.contains(flat)) return true;
        for (auto it = texts.begin(); it != texts.end(); ++it)
            if (it.value().is_object() && it.value().contains(slot)) return true;
        return false;
    };
    const auto drop = [&](const char* flat, const char* slot) {
        def.erase(flat);
        for (auto it = texts.begin(); it != texts.end(); ++it)
            if (it.value().is_object()) it.value().erase(slot);
    };
    if (auto names = npc.find("names"); names != npc.end()) {
        if (own("displayName", "name"))
            c.out.warnings.push_back(c.mod->id + ": " + where + ": roster.summonItem has a name of its own, but the game's Survivors list shows the tool's name "
                                     "for the NPC: it is named after the NPC. Put what the tool is in its \"caption\" or \"lore\"");
        drop("displayName", "name");
        put(*names, "name");
    }
    if (auto bio = npc.find("description"); bio != npc.end()) {
        if (own("description", "description"))
            c.out.warnings.push_back(c.mod->id + ": " + where + ": roster.summonItem has a description of its own, but the Survivors page shows the tool's: "
                                     "the NPC's \"description\" is used");
        drop("description", "description");
        put(*bio, "description");
    } else if (!own("description", "description")) {
        // the base tool's text is about another NPC
        c.out.warnings.push_back(c.mod->id + ": " + where + ": no \"description\": the Survivors page shows the NPC's name as its text");
        if (auto names = npc.find("names"); names != npc.end()) put(*names, "description");
    }
    def["texts"] = std::move(texts);
    return def;
}

void compile_npc(Ctx& c, const std::string& nid, const ojson& n) {
    const std::string where = "npc " + nid;
    const std::string base_name = str(n, "template");
    Guid base;
    if (base_name.empty() || !c.find_template(base_name, &base)) {
        c.err(where + ": template \"" + base_name + "\" not found");
        return;
    }
    const bool clone = n.contains("clone") && !(n["clone"].is_boolean() && !n["clone"].get<bool>());
    const std::string dlg = str(n, "dialogue");
    const ojson* dialogue = nullptr;
    if (!dlg.empty()) {
        auto it = c.dialogues.find(dlg);
        if (it != c.dialogues.end() && truthy(*it->second, "native", true)) dialogue = it->second;
    }
    const bool native_roster = n.contains("roster");
    const bool auto_topics = dlg.empty() && has_auto_topics(c, nid);
    if (!clone && !dialogue && !native_roster && !auto_topics) {  // a plain vanilla giver: Kiln.Quests/Native only places it
        c.m_npcs.push_back({{"id", nid}, {"fullId", c.mod->id + ":" + nid}, {"template", base_name}, {"guid", guid_text(base)},
                            {"clone", false}, {"place", n.value("place", ojson())}});
        return;
    }
    if (native_roster && !clone) {
        c.err(where + ": \"roster\" needs \"clone\": the game's own NPCs are in the roster already");
        return;
    }
    Guid guid = base;
    std::string tname = base_name;
    if (clone) {
        const ojson cl = n["clone"].is_object() ? n["clone"] : ojson::object();
        const std::string g = str(cl, "guid");
        if (g.empty()) guid = derive_guid(c.mod->id, "npc", nid);
        else if (!parse_guid(g, &guid)) c.err(where + ": clone.guid is not a GUID");
        tname = str(cl, "name", "Kiln_" + c.mod->id + "_" + nid);
    }
    Ctx::Tpl* tp = c.template_for(guid);
    if (!tp) {
        Ctx::Tpl t;
        t.base = base;
        t.guid = guid;
        c.templates.push_back(std::move(t));
        tp = &c.templates.back();
    }
    TemplateEdit& e = tp->edit;
    uint32_t name_tag = 0;
    if (n.contains("names")) {
        const Guid loca = derive_guid(c.mod->id, "npcname", nid);
        name_tag = fnv(loca);
        TextOut tx;
        tx.tag = name_tag;
        tx.json = n["names"].is_string() ? ojson({{"*", n["names"]}}).dump() : n["names"].dump();
        c.texts.push_back(std::move(tx));
        if (clone) {
            e.has_loca = true;
            e.loca_tag = loca;
        }
    }
    const uint32_t npc_id = fnv(guid);
    if (clone) {
        e.new_name = tname;
        e.has_npc_id = true;
        e.npc_id = npc_id;
        e.dialog.any = name_tag != 0;
        e.dialog.headline = name_tag;
    } else if (name_tag) {
        e.dialog.any = true;
        e.dialog.headline = name_tag;  // a game NPC under a new name in its dialogue menu
    }
    if (auto v = n.find("vendor"); v != n.end() && v->is_object()) {
        // A vendor's stock is the recipes of its workshop. Clones share their base's workshop (a new workshop id would
        // need the client's crafting menu configuration, docs/kiln/services/npcs.md "Vendors").
        for (const auto& r : v->value("recipes", ojson::array())) c.recipes.push_back(r.dump());
    }
    if (dialogue) {
        if (clone) {
            e.dialog.replace_init = true;
            e.dialog.replace_options = true;
        }
        compile_dialogue(c, dlg, *dialogue, *tp, nid);
    } else if (auto_topics) {
        if (clone) e.dialog.replace_options = true;  // a new NPC has only these topics; the greetings stay the base's
        compile_auto_topics(c, nid, *tp);
    }
    uint32_t unlock_id = 0;
    if (native_roster) {
        const ojson r = n["roster"];
        NpcDef d;
        d.id = npc_id;
        d.name = name_tag;
        d.copy_from = fnv(base);
        d.debug_name = tname;
        if (r.contains("roamingRange")) {
            d.has_roaming = true;
            d.roaming = r["roamingRange"].get<float>();
        }
        d.assistant = truthy(r, "assistant");
        if (auto w = r.find("workshop"); w != r.end() && w->is_string()) {
            // an existing workshop's 0x id: its stock and its crafting menu (a new id has no menu: npcs.md "Vendors")
            const std::string ws = w->get<std::string>();
            if (ws.rfind("0x", 0) != 0) {
                c.err(where + ": roster.workshop: the 0x id of an existing workshop");
            } else {
                d.has_workshop = true;
                d.workshop_id = static_cast<uint32_t>(std::stoul(ws, nullptr, 16));
                e.has_workshop = true;
                e.workshop_id = d.workshop_id;
            }
        }
        if (auto u = r.find("unlock"); u != r.end() && u->is_string()) {
            if (c.resolve(u->get<std::string>(), &unlock_id, where + " roster")) d.unlock = query_bool(unlock_id);
        }
        if (auto s = r.find("summonItem"); s != r.end()) {
            Guid item;
            if (s->is_string()) {
                if (!parse_guid(s->get<std::string>(), &item)) c.err(where + ": roster.summonItem: a GUID or a definition object");
            } else if (s->is_object()) {
                if (!parse_guid(str(*s, "guid"), &item)) c.err(where + ": roster.summonItem.guid is not a GUID");
                c.items.push_back(summon_item_def(c, n, *s, where).dump());
            }
            d.item_id = fnv(item);
            d.item_ref = item;
        }
        c.npcs.push_back(std::move(d));
    }
    c.m_npcs.push_back({{"id", nid}, {"fullId", c.mod->id + ":" + nid}, {"template", tname}, {"guid", guid_text(guid)}, {"clone", clone},
                        {"npcId", hex32(npc_id)}, {"roster", native_roster}, {"unlock", hex32(unlock_id)},
                        {"place", n.value("place", ojson())}, {"dialogue", dialogue ? c.mod->id + ":" + dlg : std::string()},
                        {"summonItem", native_roster && n["roster"].contains("summonItem") && n["roster"]["summonItem"].is_object()
                                           ? n["roster"]["summonItem"].value("guid", "") : std::string()}});
}

bool read_required(const GameData& g, const char* guid_text_, uint32_t type, const uint8_t** p, size_t* n, std::string* err) {
    Guid gd;
    parse_guid(guid_text_, &gd);
    if (!g.read(gd, type, p, n) || !*p || !*n) {
        *err = std::string("the game has no resource ") + guid_text_;
        return false;
    }
    return true;
}

}  // namespace

Guid derive_guid(const std::string& mod, const std::string& kind, const std::string& name) {
    Guid ns;
    parse_guid(kNs, &ns);
    return uuid5(ns, mod + ":" + kind + ":" + name);
}

PackOut compile_pack(const std::vector<ModInput>& mods, const GameData& game) {
    PackOut out;
    out.manifest = ojson::object();
    ojson m_mods = ojson::array();
    std::map<uint32_t, std::string> tags;
    std::vector<Ctx::Tpl> all_templates;
    for (const ModInput& mod : mods) {
        Ctx c{game, out};
        c.mod = &mod;
        bool native = false;
        try {
            for (const ojson& doc : mod.documents) {
                for (const auto& [k, v] : section(doc, "dialogues").items()) {
                    (void)v;
                    c.dialogues[k] = &doc["dialogues"][k];
                }
                // the game's journal is where a quest goes, unless it says "chat" (a mod vanilla clients join)
                for (const auto& [k, v] : section(doc, "quests").items())
                    if (v.is_object() && str(v, "journal") != "chat" && !c.doc_quests.count(k)) c.doc_quests[k] = &doc["quests"][k];
                for (const auto& [k, v] : section(doc, "npcs").items())
                    if (v.is_object() && !c.doc_npcs.count(k)) c.doc_npcs[k] = &doc["npcs"][k];
            }
            for (const ojson& doc : mod.documents) {
                for (const auto& [k, v] : section(doc, "knowledge").items()) {
                    native = true;
                    const std::string scope = str(v, "scope", "player"), type = str(v, "type", "bool");
                    const uint8_t ty = type == "counter" ? kCounter : type == "flags" ? kFlags : kBool;
                    if (type != "bool" && type != "counter" && type != "flags") c.err("knowledge " + k + ": type must be bool, counter or flags");
                    if (scope != "world" && scope != "player" && scope != "both") c.err("knowledge " + k + ": scope must be world, player or both");
                    c.add_knowledge(k, ty, scope != "player", scope != "world", v.value("expectedFlags", 0u));
                }
            }
            for (const auto& [k, q] : c.doc_quests) {
                native = true;
                compile_quest(c, k, *q);
            }
            for (const auto& [k, np] : c.doc_npcs) {
                const ojson& v = *np;
                const bool native_dialogue = v.contains("dialogue") && c.dialogues.count(str(v, "dialogue")) && truthy(*c.dialogues[str(v, "dialogue")], "native", true);
                const bool topics = !v.contains("dialogue") && has_auto_topics(c, k);
                if (v.contains("clone") || v.contains("roster") || native_dialogue || (topics && v.contains("template"))) {
                    native = true;
                    compile_npc(c, k, v);
                } else if (v.contains("place") && v.contains("template")) {
                    compile_npc(c, k, v);  // a game NPC placed as a giver: no resource changes, vanilla clients are fine
                }
            }
        } catch (const std::exception& e) {
            native = true;
            c.err(std::string("bad quests JSON: ") + e.what());
        }
        if (!native && c.m_npcs.empty() && c.errors.empty()) continue;
        if (!c.errors.empty()) {
            out.errors.insert(out.errors.end(), c.errors.begin(), c.errors.end());
            m_mods.push_back({{"id", mod.id}, {"ok", false}, {"errors", c.errors}});
            continue;
        }
        if (native) out.any = true;
        for (auto& [id, d] : c.knowledge) out.knowledge.push_back(d);
        out.quests.insert(out.quests.end(), c.quests.begin(), c.quests.end());
        out.npcs.insert(out.npcs.end(), c.npcs.begin(), c.npcs.end());
        out.content_items.insert(out.content_items.end(), c.items.begin(), c.items.end());
        out.content_recipes.insert(out.content_recipes.end(), c.recipes.begin(), c.recipes.end());
        for (TextOut& t : c.texts) {
            auto [it, fresh] = tags.emplace(t.tag, t.json);
            if (!fresh && it->second != t.json) out.warnings.push_back(mod.id + ": two texts have the loca tag " + hex32(t.tag));
            out.texts.push_back(std::move(t));
        }
        all_templates.insert(all_templates.end(), c.templates.begin(), c.templates.end());
        m_mods.push_back({{"id", mod.id}, {"ok", true}, {"knowledge", c.m_knowledge}, {"quests", c.m_quests}, {"lines", c.m_lines},
                          {"npcs", c.m_npcs}, {"markers", c.m_markers}, {"computed", c.m_computed}});
    }
    out.manifest["mods"] = m_mods;
    out.manifest["errors"] = out.errors;
    if (!out.any) {
        out.ok = out.errors.empty();
        return out;
    }
    // the kiln.always counter is one id per mod: fine (each mod's own object)
    std::string err;
    const uint8_t* p = nullptr;
    size_t n = 0;
    auto fail = [&](const std::string& why) {
        out.errors.push_back(why);
        out.ok = false;
        out.any = false;
        out.manifest["errors"] = out.errors;
        return out;
    };
    if (!read_required(game, kGuidKnowledge, kTypeKnowledge, &p, &n, &err)) return fail(err);
    if (!rebuild_knowledge(p, n, out.knowledge, &out.res_knowledge, &err)) return fail(err);
    uint32_t cw = 0, cp = 0;
    if (knowledge_counts(out.res_knowledge.data(), out.res_knowledge.size(), &cw, &cp) && (cw > kReservedWorld || cp > kReservedPlayer))
        out.warnings.push_back("the game reserves its knowledge maps for " + std::to_string(kReservedWorld) + " world and " + std::to_string(kReservedPlayer) +
                               " player entries; there are now " + std::to_string(cw) + " and " + std::to_string(cp) + " objects (untested beyond that)");
    if (!out.quests.empty()) {
        if (!read_required(game, kGuidJournal, kTypeJournal, &p, &n, &err)) return fail(err);
        if (!rebuild_journal(p, n, out.quests, &out.res_journal, &err)) return fail(err);
    }
    if (!out.npcs.empty()) {
        if (!read_required(game, kGuidNpcs, kTypeNpcs, &p, &n, &err)) return fail(err);
        if (!rebuild_npcs(p, n, out.npcs, &out.res_npcs, &err)) return fail(err);
    }
    // several mods may add lines to one game NPC: their edits go into one override, in load order
    std::vector<Ctx::Tpl> merged;
    for (Ctx::Tpl& t : all_templates) {
        Ctx::Tpl* m = nullptr;
        for (Ctx::Tpl& x : merged)
            if (x.guid == t.guid) m = &x;
        if (!m) {
            merged.push_back(std::move(t));
            continue;
        }
        if (!m->edit.new_name.empty() || !t.edit.new_name.empty()) return fail("two mods define the clone " + guid_text(t.guid));
        DialogEdit& a = m->edit.dialog;
        const DialogEdit& b = t.edit.dialog;
        a.any = a.any || b.any;
        if (b.headline) a.headline = b.headline;
        a.init.insert(a.init.end(), b.init.begin(), b.init.end());
        a.options.insert(a.options.end(), b.options.begin(), b.options.end());
        a.transition.insert(a.transition.end(), b.transition.begin(), b.transition.end());
        a.farewell.insert(a.farewell.end(), b.farewell.begin(), b.farewell.end());
        a.replace_transition = a.replace_transition || b.replace_transition;
        a.replace_farewell = a.replace_farewell || b.replace_farewell;
    }
    for (Ctx::Tpl& t : merged) {
        if (!game.read(t.base, kTypeTemplate, &p, &n) || !p || !n) return fail("template " + guid_text(t.base) + " is not in the game's data");
        TemplateOut o;
        o.guid = t.guid;
        o.is_new = !t.edit.new_name.empty();
        o.name = o.is_new ? t.edit.new_name : template_name(p, n);
        if (!edit_template(p, n, t.edit, &o.data, &err)) return fail(o.name + ": " + err);
        out.templates.push_back(std::move(o));
    }
    out.ok = out.errors.empty();
    return out;
}

}  // namespace kiln::qb
