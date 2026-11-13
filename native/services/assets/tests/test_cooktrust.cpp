// SPDX-License-Identifier: MIT
// Host test of cooktrust.cpp: which cooked files a game loads. A file of a mod's assets/cooked loads only when a cook
// receipt of this game (<game>/kiln/state/cook-receipts/*.json, written by the cook tools) lists its bytes; kiln.json
// "cooked": "any" loads them all. Run by tests/run.sh.
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string>

#include "cooktrust.h"

namespace fs = std::filesystem;
static int failures = 0;
#define CHECK(c)                                                         \
    do {                                                                 \
        if (!(c)) {                                                      \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c);     \
            ++failures;                                                  \
        }                                                                \
    } while (0)

static void put(const fs::path& p, const std::string& text) {
    fs::create_directories(p.parent_path());
    std::ofstream(p, std::ios::binary) << text;
}

int main(int argc, char** argv) {
    using namespace ka;
    // SHA-256: the FIPS 180-4 examples and lengths around a block's end
    CHECK(sha256_hex("", 0) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    CHECK(sha256_hex("abc", 3) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    const std::string two = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
    CHECK(sha256_hex(two.data(), two.size()) == "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1");
    const std::string million(1000000, 'a');
    CHECK(sha256_hex(million.data(), million.size()) == "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0");
    const std::string s55(55, 'x'), s56(56, 'x'), s64(64, 'x');
    CHECK(sha256_hex(s55.data(), 55) != sha256_hex(s56.data(), 56) && sha256_hex(s64.data(), 64).size() == 64);

    // what is a cook output: below "cooked", in any case
    CHECK(is_cooked_path("cooked/a.bin"));
    CHECK(is_cooked_path("Cooked/sub/a.bin"));
    CHECK(is_cooked_path("cooked\\a.bin"));
    CHECK(is_cooked_path("./cooked/a.bin"));
    CHECK(!is_cooked_path("icons/a.png"));
    CHECK(!is_cooked_path("cooked"));
    CHECK(!is_cooked_path("cooked-not/a.bin"));
    CHECK(!is_cooked_path("src/cooked/a.bin"));

    const fs::path root = argc > 1 ? fs::path(argv[1]) : fs::temp_directory_path() / "kiln-cooktrust-test";
    fs::remove_all(root);
    const fs::path kiln = root / "kiln", state = kiln / "state";
    const std::string made = "a program this game's cook made", given = "a program from a download";

    // no receipt at all and no kiln.json: the default policy (warn): a cooked file loads, as one no cook here made
    CookTrust t = load_cook_trust(kiln, state, true);
    CHECK(kCookedDefault == CookedPolicy::Warn && t.policy == kCookedDefault && t.receipts == 0 && t.digests.empty());
    CHECK(cooked_verdict(t, made.data(), made.size()) == CookedVerdict::LoadUnmade);
    CHECK(std::string(policy_name(CookedPolicy::Warn)) == "warn" && std::string(policy_name(CookedPolicy::Local)) == "local");

    // a receipt lists the bytes a cook made: those load, other bytes under the same name do not
    put(state / "cook-receipts" / "a.json",
        "{ \"format\": 1, \"tool\": \"impact\", \"mod\": \"/x/mod\", \"recipe\": \"impact/cook.json\", \"files\": { \"cooked/impact/p.bin\": \"" +
            sha256_hex(made.data(), made.size()) + "\" } }");
    put(state / "cook-receipts" / "broken.json", "{ not json");
    put(state / "cook-receipts" / "other.json", "{ \"format\": 2, \"files\": {} }");
    put(state / "cook-receipts" / "note.txt", "not a receipt");
    t = load_cook_trust(kiln, state, true);
    CHECK(t.receipts == 1 && t.digests.size() == 1);
    CHECK(t.notes.size() == 2);  // the unreadable one and the other format are said
    CHECK(cooked_verdict(t, made.data(), made.size()) == CookedVerdict::Load);
    CHECK(cooked_verdict(t, given.data(), given.size()) == CookedVerdict::LoadUnmade);  // warn: loaded, and said

    // "local": only what a receipt lists loads
    put(kiln / "kiln.json", "{ \"cooked\": \"local\" }");
    t = load_cook_trust(kiln, state, true);
    CHECK(t.policy == CookedPolicy::Local);
    CHECK(cooked_verdict(t, made.data(), made.size()) == CookedVerdict::Load && cooked_trusted(t, made.data(), made.size()));
    CHECK(cooked_verdict(t, given.data(), given.size()) == CookedVerdict::Refuse && !cooked_trusted(t, given.data(), given.size()));
    // "warn" named
    put(kiln / "kiln.json", "{ \"cooked\": \"warn\" }");
    CHECK(load_cook_trust(kiln, state, true).policy == CookedPolicy::Warn);

    // kiln.json "cooked": "any" (a development server): everything loads; the side's section wins
    put(kiln / "kiln.json", "{ \"cooked\": \"any\" }");
    t = load_cook_trust(kiln, state, false);
    CHECK(t.policy == CookedPolicy::Any && cooked_verdict(t, given.data(), given.size()) == CookedVerdict::Load);
    put(kiln / "kiln.json", "{ \"cooked\": \"any\", \"client\": { \"cooked\": \"local\" } }");
    CHECK(load_cook_trust(kiln, state, false).policy == CookedPolicy::Any);
    CHECK(load_cook_trust(kiln, state, true).policy == CookedPolicy::Local);
    put(kiln / "kiln.json", "{ \"cooked\": \"sometimes\" }");
    t = load_cook_trust(kiln, state, true);
    CHECK(t.policy == kCookedDefault && t.notes.size() == 3);
    put(kiln / "kiln.json", "{ // comments are fine\n \"policy\": \"any\", \"cooked\": \"local\" }");
    CHECK(load_cook_trust(kiln, state, true).policy == CookedPolicy::Local);

    // a receipt store that is a link, or the caller found one by name: no receipt is read from it
    put(kiln / "kiln.json", "{ \"cooked\": \"local\" }");
    t = load_cook_trust(kiln, state, true, true);
    CHECK(t.store_refused && t.receipts == 0 && cooked_verdict(t, made.data(), made.size()) == CookedVerdict::Refuse);
    fs::rename(state / "cook-receipts", root / "elsewhere");
    fs::create_directory_symlink(root / "elsewhere", state / "cook-receipts");
    t = load_cook_trust(kiln, state, true);
    CHECK(t.store_refused && t.receipts == 0 && !t.notes.empty());

    fs::remove_all(root);
    if (failures) {
        std::printf("test_cooktrust: %d failure(s)\n", failures);
        return 1;
    }
    std::printf("PASS test_cooktrust\n");
    return 0;
}
