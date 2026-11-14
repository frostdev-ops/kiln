// SPDX-License-Identifier: MIT
#include "../boot_programs.h"
#include <cstdio>
#include "services/kiln_world.h"
#include "services/kiln_types.h"
static_assert(offsetof(KilnWorldApi,status)==184 && offsetof(KilnWorldApi,write_other)==192);
// v3 appended the tracked-write entries (tests/writes_test.cpp pins each): the v2 table is its first 240 bytes.
static_assert(offsetof(KilnWorldApi,write_submit)==240 && offsetof(KilnWorldApi,game_program_find)==280);  // v4 (0.2.0-dev.28) appends from 280
static_assert(offsetof(KilnTypesApi,system_hook)==168 && offsetof(KilnTypesApi,external_runner)==184 && offsetof(KilnTypesApi,type_impact_name)==192 && offsetof(KilnTypesApi,in_system_callback)==200 && sizeof(KilnTypesApi)==208);  // 0.2.0-dev.28 appends from 184
using namespace kw;
int main() {
    int checks = 0;
    auto check = [&](bool ok) { ++checks; if (!ok) { std::fprintf(stderr,"FAIL boot declaration check %d\n",checks); std::exit(1); } };
    auto bad = [&](const std::string& text) { try { parse_boot_programs(text); return false; } catch (...) { return true; } };
    auto value = parse_boot_programs(R"({"format":1,"programs":{"pulse":{"otherWrite":["Health"],"eventsIn":["StopGuardingEvent","StartGuardingEvent"]}}})");
    check(value.size()==1 && value[0].flags==0x14 && value[0].lists[10][0]=="Health" && value[0].lists[14].size()==2);
    check(bad(R"({"format":1,"programs":{"a":{},"a":{}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"eventsIn":[],"eventsIn":[]}}})"));
    check(bad(R"({"format":1,"format":1,"programs":{"a":{}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"otherWrite":["Health","Health"]}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"flags":["system","system"]}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"otherWrite":["keen::ecs::Health"]}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"unknown":[]}}})"));
    check(bad(R"({"format":2,"programs":{"a":{}}})"));
    check(bad(R"({"format":1,"programs":{}})"));
    check(bad(std::string(65537,' ')));
    check(bad(R"({"format":1,"programs":{"a":{"read":["Health"]}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"flags":[]}}})"));
    check(bad(R"({"format":1,"programs":{"a":{"flags":["hostOnly"],"read":["Health"],"write":["Health"]}}})"));
    check(bad(R"({"format":1,"programs":{"a":{},"b":{},"c":{},"d":{},"e":{},"f":{},"g":{},"h":{},"i":{}}})"));
    check(bad(R"({"format":1,"programs":{"a":{},}})"));
    check(bad(R"({"format":1,"programs":{"a":{"singletonsRead":["bad-name"]}}})"));
    check(parse_boot_programs(R"({"format":1,"programs":{"a":{"singletonsRead":["g38_items::ItemRegistry"]}}})")[0].lists[6].size()==1);
    check(!bad("{\"format\":1,\"programs\":{\"a\":{\"otherRead\":[\""+std::string(120,'a')+"\"]}}}"));
    check(bad("{\"format\":1,\"programs\":{\"a\":{\"otherRead\":[\""+std::string(121,'a')+"\"]}}}"));
    // Disabled and side-incompatible profile entries still mask same-ID drop-ins. Duplicate drop-ins are ambiguous.
    std::vector<BootCandidate> candidates = {{"off",false,true,true,1},{"off",true,false,true,2},
        {"on",true,true,true,3},{"on",true,false,true,4},{"client",true,true,false,5},
        {"client",true,false,true,6},{"drop",true,false,true,7},{"duplicate",true,false,true,8},{"duplicate",true,false,true,9}};
    check(select_boot_candidates(candidates,true)==std::vector<size_t>({3,7}));
    check(select_boot_candidates(candidates,false)==std::vector<size_t>({3}));
    candidates.push_back({"on",true,true,true,10});
    check(select_boot_candidates(candidates,true)==std::vector<size_t>({7}));
    check(!boot_mod_id("../escape") && !boot_mod_id("a/b") && !boot_mod_id("UPPER"));
    check(boot_mod_id("my-mod.1") && boot_name("Health"));
    check(select_boot_candidates({{"writer",true,false,true,1},{"writer",false,false,true,2}},true)==std::vector<size_t>({1}));
    std::printf("world boot declarations: PASS %d checks (bounds, duplicate keys, profile precedence, sides)\n",checks);
}
