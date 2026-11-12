// SPDX-License-Identifier: MIT
#include "merge.h"
#include <iostream>
#include <fstream>
#include <iterator>
using namespace kpa;

int main(int argc, char** argv) {
    if (argc != 3) return 2;
    TypeDb db;
    Type a; a.name = kGraphType; a.kind = K_TYPEDEF; a.size = 4; a.has_inner = true; a.inner = "cycle";
    Type b = a; b.name = "cycle"; b.inner = a.name;
    db.add(a); db.add(b); db.finish();
    const std::string mode = argv[1];
    bool ok = false;
    if (mode == "merge") {
        auto mod = parse(R"({"format":1,"states":{"slash":{"clip":"slash","like":"Parry_FullBody"}}})", "m", "both", "mod", "fixture");
        std::ifstream input(argv[2]);
        std::string json(std::istreambuf_iterator<char>(input), {}), why;
        TypeDb bundled, corrupt;
        if (!TypeDb::from_json(json, &bundled, &why)) return 2;
        for (Type t : bundled.types()) {
            if (t.name == kGraphType) { t.fields[0].type = "cycle"; t.fields[0].has_type = true; }
            corrupt.add(std::move(t));
        }
        b.inner = "cycle"; corrupt.add(b); corrupt.finish();
        Bytes raw(corrupt.get(kGraphType)->size); GraphSource source; source.bytes = &raw;
        auto result = merge(source, corrupt, {mod}, [](const std::string&) -> std::optional<ClipFacts> { return std::nullopt; });
        ok = !result.graph && result.report["graph"] == "off" && result.report["reason"].get<std::string>().find("typedef") != std::string::npos;
    } else {
        try {
            if (mode == "decode") (void)Decoder(db).decode(db.get(kGraphType), Bytes(4));
            else if (mode == "encode") (void)Encoder(db).encode(db.get(kGraphType), Value::integer(1));
            else return 2;
        } catch (const PyError& e) { ok = e.text.find("typedef") != std::string::npos; }
    }
    std::cout << (ok ? "PASS" : "FAIL") << " typedef cycle " << mode << " fails closed with a reason" << std::endl;
    if (!ok) return 1;
    if (mode == "merge") return 0;
    for (int length : {16, 17}) {
        TypeDb chain;
        for (int i = 0; i <= length; ++i) {
            Type t; t.name = std::to_string(i); t.size = 4;
            t.kind = i == length ? K_U32 : K_TYPEDEF;
            if (i != length) { t.has_inner = true; t.inner = std::to_string(i + 1); }
            chain.add(t);
        }
        chain.finish();
        bool rejected = false;
        try {
            if (mode == "decode") (void)Decoder(chain).decode(chain.get("0"), Bytes(4));
            else (void)Encoder(chain).encode(chain.get("0"), Value::integer(1));
        } catch (const PyError& e) { rejected = e.text.find("typedef") != std::string::npos; }
        ok = rejected == (length > 16);
        std::cout << (ok ? "PASS" : "FAIL") << " typedef length " << length << " " << mode << std::endl;
        if (!ok) return 1;
    }
    return 0;
}
