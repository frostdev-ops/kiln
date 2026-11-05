// SPDX-License-Identifier: MIT
// Cross-check of kiln lint's JSON dialect for data packs, assets.json and skills/*.json: parses each file exactly as
// native/services/content/packs.cpp, assets.cpp and skills.cpp do (nlohmann::ordered_json, ignore_comments) and
// prints "<file>\tok" or "<file>\tdiscarded". Built and run by tests/lint.rs (nlohmann_dialect) when g++ is there.
#include <fstream>
#include <iostream>
#include <sstream>

#include "nlohmann/json.hpp"

int main(int argc, char** argv) {
    for (int i = 1; i < argc; ++i) {
        std::ifstream f(argv[i], std::ios::binary);
        std::stringstream ss;
        ss << f.rdbuf();
        const std::string text = ss.str();
        auto doc = nlohmann::ordered_json::parse(text, nullptr, false, true);
        std::cout << argv[i] << '\t' << (doc.is_discarded() ? "discarded" : "ok") << '\n';
    }
    return 0;
}
