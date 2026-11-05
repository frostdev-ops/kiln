// SPDX-License-Identifier: MIT
// Cross-check of schema/skills.schema.json's game-tree nodes: runs native/services/skills' real parse_node on each entry
// of schema/fixtures/lint/skills-nodes.json and prints "ok" or "refused\t<why>", one line per entry. Built and run by
// tests/lint.rs (skills_native_reader_agrees) when g++ is there.
#include <fstream>
#include <iostream>
#include <sstream>

#include "tree.h"

int main(int argc, char** argv) {
    if (argc < 2) return 2;
    std::ifstream f(argv[1], std::ios::binary);
    std::stringstream ss;
    ss << f.rdbuf();
    const auto list = kiln::skills::json::parse(ss.str(), nullptr, false, true);
    if (!list.is_array()) return 2;
    for (const auto& e : list) {
        kiln::skills::NodeDef d;
        std::string why;
        if (kiln::skills::parse_node(e["node"], &d, &why)) std::cout << "ok\n";
        else std::cout << "refused\t" << why << '\n';
    }
    return 0;
}
