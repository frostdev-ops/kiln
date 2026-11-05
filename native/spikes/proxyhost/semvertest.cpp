// SPDX-License-Identifier: MIT
// Parity of the core's C++ semver port (native/core/src/native_mods_semver.h) with Kiln.Host's SemVer.cs on
// schema/fixtures/semver/{ranges,parse,order}.json.   wine proxyhost-semver.exe <repo>\schema\fixtures\semver
#include <cstdio>
#include <fstream>
#include <sstream>

#include "native_mods_semver.h"
#include "nlohmann/json.hpp"

using kiln::semver::Range;
using kiln::semver::Version;

static nlohmann::json load(const std::string& dir, const char* name) {
    std::ifstream in(dir + "\\" + name);
    std::stringstream ss;
    ss << in.rdbuf();
    return nlohmann::json::parse(ss.str());
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: proxyhost-semver <schema\\fixtures\\semver>\n");
        return 2;
    }
    const std::string dir = argv[1];
    int pass = 0, fail = 0;
    auto check = [&](bool ok, const std::string& what) {
        if (ok) ++pass;
        else {
            ++fail;
            std::printf("FAIL %s\n", what.c_str());
        }
    };
    const auto ranges = load(dir, "ranges.json");
    for (const auto& c : ranges["cases"]) {
        Range r;
        std::string error;
        Version v;
        const std::string rt = c["range"], vt = c["version"];
        const bool ok = Range::parse(rt, &r, &error) && kiln::semver::parse(vt, &v);
        check(ok && r.satisfies(v) == c["satisfies"].get<bool>(), "range '" + rt + "' vs " + vt);
    }
    for (const auto& c : ranges["invalid"]) {
        Range r;
        std::string error;
        const std::string rt = c["range"];
        check(Range::parse(rt, &r, &error) == c["ok"].get<bool>(), "invalid range '" + rt + "'");
    }
    const auto parse = load(dir, "parse.json");
    for (const auto& c : parse["cases"]) {
        const std::string t = c["text"];
        const auto& e = c["expect"];
        Version v;
        const bool ok = kiln::semver::parse(t, &v);
        bool same = ok == e["ok"].get<bool>();
        if (same && ok) {
            same = v.major == e["major"].get<int>() && v.minor == e["minor"].get<int>() && v.patch == e["patch"].get<int>() &&
                   v.pre == e["pre"].get<std::vector<std::string>>() && v.build == e["build"].get<std::string>() &&
                   v.str() == e["text"].get<std::string>();
        }
        check(same, "parse '" + t + "'");
    }
    const auto order = load(dir, "order.json");
    const auto& ordered = order["ordered"];
    for (size_t i = 0; i + 1 < ordered.size(); ++i) {
        Version a, b;
        kiln::semver::parse(ordered[i].get<std::string>(), &a);
        kiln::semver::parse(ordered[i + 1].get<std::string>(), &b);
        check(kiln::semver::compare(a, b) < 0 && kiln::semver::compare(b, a) > 0,
              ordered[i].get<std::string>() + " < " + ordered[i + 1].get<std::string>());
    }
    for (const auto& pair : order["equal"]) {
        Version a, b;
        kiln::semver::parse(pair[0].get<std::string>(), &a);
        kiln::semver::parse(pair[1].get<std::string>(), &b);
        check(kiln::semver::compare(a, b) == 0, pair[0].get<std::string>() + " == " + pair[1].get<std::string>());
    }
    std::printf("semver parity: %d passed, %d failed\n", pass, fail);
    return fail ? 1 : 0;
}
