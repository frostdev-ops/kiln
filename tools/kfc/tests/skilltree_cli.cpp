// SPDX-License-Identifier: MIT
// Runs kiln.skills' tree builder (native/services/skills/tree.cpp, the code kiln-skills.dll runs at boot) on the game's own
// SkillTreeResource, for tools/kfc/tests/test_skilltree_slots.py.
//   skilltree_cli <resource.bin> <definitions.json> [--no-shadow]
// definitions.json: [{"mod": "<owner id>", "node": {<a node of skills/*.json>}}, ...]
// stdout: {"nodes": [{"name", "id", "state", "shadow", "index", "levelIndex", "error"}], "shadowNodes", "freeLevels", "problems"}
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <string>
#include <vector>

#include "tree.h"

using namespace kiln::skills;

int main(int argc, char** argv) {
    if (argc < 3) return 2;
    std::ifstream res(argv[1], std::ios::binary);
    const std::vector<uint8_t> v((std::istreambuf_iterator<char>(res)), {});
    std::ifstream df(argv[2], std::ios::binary);
    const json in = json::parse(df);
    const bool shadow_ok = !(argc > 3 && std::strcmp(argv[3], "--no-shadow") == 0);
    std::vector<std::unique_ptr<Def>> owned;
    std::vector<Def*> defs;
    for (const json& e : in) {
        auto d = std::make_unique<Def>();
        std::string why;
        if (!parse_node(e.at("node"), &d->node, &why)) {
            std::cerr << "bad node: " << why << "\n";
            return 3;
        }
        d->owner_id = e.at("mod").get<std::string>();
        d->source = "test";
        defs.push_back(d.get());
        owned.push_back(std::move(d));
    }
    const Built b = build(v.data(), v.size(), defs, false, true, shadow_ok);
    json out;
    out["ok"] = b.ok;
    out["vanilla"] = b.vanilla_nodes;
    out["shadowNodes"] = b.shadow_nodes;
    out["freeLevels"] = b.free_levels;
    out["problems"] = b.problems;
    out["nodes"] = json::array();
    for (const Def* d : defs)
        out["nodes"].push_back({{"name", d->node.name}, {"id", hex32(d->node.id)}, {"state", static_cast<int>(d->state)}, {"shadow", d->shadow},
                                {"index", d->index}, {"levelIndex", d->level_index}, {"mod", d->owner_id}, {"error", d->error}});
    std::cout << out.dump() << "\n";
    return 0;
}
