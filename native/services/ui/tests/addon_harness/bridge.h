// SPDX-License-Identifier: MIT
// Included inside the harness namespace. Only source-backed game stand-ins feed
// real encoded packets through the addon's production store/update detours.
using Json = nlohmann::json;
using BridgeCall = uint32_t (*)(const uint8_t*, size_t, uint8_t*, size_t, size_t*);
constexpr size_t kBridgeReply = 256 * 1024;
constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
std::string packet_encode(const std::vector<uint8_t>& bytes) {
    std::string out = "~sl1~";
    for (size_t i = 0; i < bytes.size(); i += 3) {
        uint32_t n = uint32_t(bytes[i]) << 16;
        if (i + 1 < bytes.size()) n |= uint32_t(bytes[i + 1]) << 8;
        if (i + 2 < bytes.size()) n |= bytes[i + 2];
        for (size_t j = 0; j <= std::min(size_t{3}, bytes.size() - i); ++j) out += kAlphabet[(n >> (18 - 6*j)) & 63];
    }
    return out;
}
std::vector<uint8_t> packet_decode(const std::string& text) {
    std::vector<uint8_t> out;
    if (!text.starts_with("~sl1~")) return out;
    for (size_t i = 5; i < text.size(); i += 4) {
        uint32_t n = 0; const size_t len = std::min(size_t{4}, text.size() - i);
        for (size_t j = 0; j < len; ++j) { const char* p = std::strchr(kAlphabet, text[i+j]); if (!p) return {}; n |= uint32_t(p-kAlphabet) << (18-6*j); }
        for (size_t j = 0; j + 1 < len; ++j) out.push_back(uint8_t(n >> (16-8*j)));
    }
    return out;
}
void little(std::vector<uint8_t>& out, uint64_t n, size_t bytes) { for (size_t i = 0; i < bytes; ++i) out.push_back(uint8_t(n >> (8*i))); }
uint64_t little_read(const std::vector<uint8_t>& bytes, size_t at, size_t len) { uint64_t n = 0; for (size_t i = 0; i < len; ++i) n |= uint64_t(bytes.at(at+i)) << (8*i); return n; }
void receive_packet(const std::string& packet) {
    uint8_t out = 9; StringView view{reinterpret_cast<const uint8_t*>(packet.data()), packet.size()};
    reinterpret_cast<uint8_t* (*)(uint8_t*, void*, uint64_t, const StringView*, uint32_t)>(g_game.store)(&out, nullptr, 0, &view, 0);
    check(out == 0, "production store hook accepts %zu byte encoded frame", packet.size());
}
void beacon() { std::vector<uint8_t> bytes{2}; little(bytes, 0, 8); bytes.push_back(3); receive_packet(packet_encode(bytes)); }
alignas(16) uint8_t g_client[0x200]{};
void client_tick() { reinterpret_cast<void (*)(uint8_t*, void*, uint8_t)>(g_game.update)(g_client, nullptr, 0); }
uint64_t claim_nonce() {
    for (auto it = g_packets.rbegin(); it != g_packets.rend(); ++it) { auto bytes = packet_decode(*it); if (bytes.size() == 10 && bytes[0] == 3) return little_read(bytes, 1, 8); }
    return 0;
}
void server_message(uint64_t nonce, uint32_t msg, const Json& message) {
    const auto data = message.dump(); constexpr size_t chunk_size = 169;
    const size_t count = (data.size() + chunk_size - 1) / chunk_size;
    for (size_t chunk = 0; chunk < count; ++chunk) {
        std::vector<uint8_t> bytes{4}; little(bytes, nonce, 8); little(bytes, msg, 4); little(bytes, chunk, 2); little(bytes, count, 2);
        bytes.insert(bytes.end(), data.begin() + chunk*chunk_size, data.begin() + std::min(data.size(), (chunk+1)*chunk_size));
        receive_packet(packet_encode(bytes));
    }
}
std::vector<Json> sent_events(uint64_t nonce) {
    std::map<uint32_t, std::vector<std::string>> pending; std::vector<Json> out;
    for (const auto& packet : g_packets) {
        const auto b = packet_decode(packet);
        if (b.size() < 17 || b[0] != 4 || little_read(b, 1, 8) != nonce) continue;
        const auto chunk = little_read(b, 13, 2), count = little_read(b, 15, 2);
        if (count == 0 || count > 32 || chunk >= count) continue;
        auto& parts = pending[uint32_t(little_read(b, 9, 4))]; if (parts.empty()) parts.resize(count);
        parts[chunk] = std::string(b.begin()+17, b.end());
        if (std::all_of(parts.begin(), parts.end(), [](const auto& p) {return !p.empty();})) {
            std::string joined; for (const auto& p : parts) joined += p;
            auto v = Json::parse(joined); if (v.value("op", "") == "ui_event") out.push_back(v); pending.erase(uint32_t(little_read(b, 9, 4)));
        }
    }
    return out;
}
Json bridge_rpc(BridgeCall rpc, const Json& request, uint32_t expected = 0) {
    const auto input = request.dump(); std::vector<uint8_t> out(kBridgeReply); size_t written = 0;
    const auto status = rpc(reinterpret_cast<const uint8_t*>(input.data()), input.size(), out.data(), out.size(), &written);
    check(status == expected && written <= out.size(), "bridge %s returns %u (want %u), bounded output", request.value("op", "?").c_str(), status, expected);
    return status == 0 ? Json::parse(out.begin(), out.begin()+written) : Json::object();
}
Json acquire(BridgeCall rpc, const Json& state) { return bridge_rpc(rpc, Json{{"op","acquire"},{"module",state["module"]},{"session",state["session"]},{"client","kiln-headless"}}); }
Json request_event(const Json& state, const std::string& node, const Json& event) {
    return Json{{"op","event"},{"module",state["module"]},{"session",state["session"]},{"lease",state["lease"]},{"revision",state["revision"]},{"owner","storage"},{"event",{{"window","w"},{"node",node},{"event",event}}}};
}
int bridge_checks(const std::filesystem::path& dll, bool stock, bool late) {
    HMODULE module = load_addon(dll, true); make_window();
    if (!stock && !late) kui::input_attach(g_win);
    frame();
    if (!stock && late) kui::input_attach(g_win);
    if (!stock) check((module_of(reinterpret_cast<void*>(top())) == module) == !late,"bridge runs with expected Kiln/addon subclass order");
    auto rpc = reinterpret_cast<BridgeCall>(GetProcAddress(module,"shroudlink_client_bridge_v1"));
    if (stock) { check(!rpc,"stock addon has no bridge export; optional fallback leaves it unchanged"); return g_fails; }
    check(rpc != nullptr,"cooperating private addon owns versioned bridge export"); if (!rpc) return g_fails;
    size_t written = 99; uint8_t output = 0;
    check(rpc(nullptr,1,&output,1,&written)==2 && written==0,"ABI rejects null request");
    check(rpc(reinterpret_cast<const uint8_t*>("{}"),2,nullptr,1,&written)==2,"ABI rejects null output with capacity");
    check(rpc(reinterpret_cast<const uint8_t*>("{}"),2,&output,kBridgeReply+1,&written)==2,"ABI rejects oversized reply capacity");
    check(rpc(reinterpret_cast<const uint8_t*>("{}"),2,&output,1,nullptr)==2,"ABI rejects null written pointer");
    const auto initial = bridge_rpc(rpc,{{"op","read"}}); check(!initial["ready"].get<bool>() && initial["entries"].empty(),"no beacon means no fabricated host menus");
    beacon(); client_tick(); const auto nonce = claim_nonce(); check(nonce!=0,"real update sends a nonzero claim nonce");
    server_message(nonce,1,{{"mod","shroudlink"},{"op","welcome"},{"ui_protocol",2},{"mods",Json::array()}});
    client_tick(); set_screen(1); frame();
    auto state = bridge_rpc(rpc,{{"op","read"}}); check(state["ready"].get<bool>() && state["entries"].empty(),"actual beacon, welcome, world and update are ready without invented entries");
    server_message(nonce,2,{{"mod","ui"},{"op","entries"},{"owner","storage"},{"entries",Json::array({{{"id","settings"},{"label","Storage"}}})}});
    state = bridge_rpc(rpc,{{"op","read"}});
    const auto probe = Json{{"op","acquire"},{"module",state["module"]},{"session",state["session"]},{"client","probe"}}.dump();
    check(rpc(reinterpret_cast<const uint8_t*>(probe.data()),probe.size(),nullptr,0,&written)==1 && written>0 && written<=kBridgeReply,"short-buffer acquire reports size without committing a lease");
    check(bridge_rpc(rpc,{{"op","read"}})["leased"]==false,"ABI probe leaves presentation unleased");
    state = acquire(rpc,state);
    const auto observed=bridge_rpc(rpc,{{"op","read"}});
    check(observed["lease"].is_null() && observed["leased"]==true,"read never leaks another client's presentation token");
    bridge_rpc(rpc,{{"op","acquire"},{"module",state["module"]},{"session",state["session"]},{"client","other"}},5);
    check(state["entries"][0]["label"]=="Storage" && state["lease"].is_string(),"actual Entries and opaque presentation lease available");
    key_through("leased presentation",1); frame();
    check(!log_has("ui: mods key on screen 0x01",0),"leased keyboard passes through the chain without triggering native Mods hotkeys");
    auto menu = request_event(state,"settings",{{"kind","menu"}}); menu["event"]["window"]="";
    bridge_rpc(rpc,menu); check(sent_events(nonce).empty(),"render-thread request does not send until game update");
    client_tick(); auto sent=sent_events(nonce);
    check(sent.size()==1 && sent[0]["mod"]=="storage" && sent[0]["event"]["event"]["kind"]=="menu","real update sends nested owner-addressed Menu on actual session codec");
    auto content = Json::parse(R"([{"type":"text","text":"Message"},{"type":"heading","text":"Heading"},{"type":"separator"},{"type":"row","children":[{"type":"button","id":"button","label":"Go"}]},{"type":"text_input","id":"input","value":"old","hint":"Name"},{"type":"toggle","id":"toggle","label":"Enabled","value":false},{"type":"item_grid","id":"items","columns":2,"items":[{"id":42,"count":3}]},{"type":"list","id":"list","entries":["one"]},{"type":"tabs","id":"tabs","tabs":["a","b"],"selected":0}])");
    server_message(nonce,3,{{"mod","ui"},{"op","open"},{"owner","storage"},{"window",{{"id","w"},{"title","Settings"},{"content",content}}}});
    state=acquire(rpc,bridge_rpc(rpc,{{"op","read"}}));
    check(state["windows"][0]["window"]["content"]==content,"actual Open snapshot preserves all ten real node types");
    if (FILE* fp = _wfopen((exe_dir()/L"bridge-snapshot.json").c_str(),L"wb")) { const auto bytes=state.dump(); std::fwrite(bytes.data(),1,bytes.size(),fp); std::fclose(fp); }
    bridge_rpc(rpc,menu,4); // preceding revision became stale
    auto invalid=request_event(state,"items",{{"kind","click"},{"index",1}}); bridge_rpc(rpc,invalid,2);
    invalid=request_event(state,"button",{{"kind","click"}}); invalid["owner"]="assistants"; bridge_rpc(rpc,invalid,2);
    std::vector<std::pair<std::string,Json>> cases={{"button",{{"kind","click"},{"index",nullptr}}},{"toggle",{{"kind","change"},{"value",true}}},{"input",{{"kind","change"},{"value","snow"}}},{"input",{{"kind","submit"},{"value",std::string(200,'x')}}},{"items",{{"kind","click"},{"index",0}}},{"list",{{"kind","click"},{"index",0}}},{"tabs",{{"kind","click"},{"index",1}}}};
    for (const auto& [node,event] : cases) { bridge_rpc(rpc,request_event(state,node,event)); client_tick(); sent=sent_events(nonce); check(sent.back()["mod"]=="storage" && sent.back()["event"]["event"]==event,"real game-thread codec preserves validated %s event",node.c_str()); }
    auto close=request_event(state,"",{{"kind","closed"}}); bridge_rpc(rpc,close); client_tick();
    check(bridge_rpc(rpc,{{"op","read"}})["windows"].empty(),"committed Closed removes mirrored native window");
    auto queued=menu; state=acquire(rpc,bridge_rpc(rpc,{{"op","read"}})); queued=request_event(state,"settings",{{"kind","menu"}}); queued["event"]["window"]=""; bridge_rpc(rpc,queued);
    bridge_rpc(rpc,{{"op","release"},{"module",state["module"]},{"session",state["session"]},{"lease",state["lease"]}});
    const auto before=sent_events(nonce).size(); client_tick(); check(sent_events(nonce).size()==before,"release discards queued events before update");
    state=acquire(rpc,bridge_rpc(rpc,{{"op","read"}})); set_screen(0); frame();
    bridge_rpc(rpc,request_event(state,"settings",{{"kind","menu"}}),4);
    set_screen(1); frame(); client_tick(); check(!bridge_rpc(rpc,{{"op","read"}})["ready"].get<bool>(),"world return cannot revive stale welcome/session ownership");
    beacon(); client_tick(); const auto rejoined_nonce=claim_nonce();
    server_message(rejoined_nonce,1,{{"mod","shroudlink"},{"op","welcome"},{"ui_protocol",2},{"mods",Json::array()}}); frame(); client_tick();
    const auto rejoined=bridge_rpc(rpc,{{"op","read"}});
    check(rejoined["ready"].get<bool>() && rejoined["entries"].empty() && rejoined["windows"].empty(),"real rejoin requires new host state, never revives old windows");
    bridge_rpc(rpc,queued,4);
    const auto copy=exe_dir()/L"dinput8-bridge-reload.dll"; std::filesystem::copy_file(dll,copy);
    auto replacement=load_addon(copy,true); auto rpc2=reinterpret_cast<BridgeCall>(GetProcAddress(replacement,"shroudlink_client_bridge_v1"));
    bridge_rpc(rpc,{{"op","read"}},3);
    check(rpc2 && bridge_rpc(rpc2,{{"op","read"}})["module"]!=initial["module"],"replaced module gets a new opaque epoch and old copy is inactive");
    return g_fails;
}
