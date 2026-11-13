// SPDX-License-Identifier: MIT
// Host test of scan.cpp, the portable half of reading a mod's files from its own folder (contained.cpp is the Windows
// half and calls these with Windows queries; here they run on real folders with POSIX ones):
// - the identity scan of a cooked folder has no depth limit, follows no link, and says when it was cut;
// - a cut scan makes a file outside its own assets/cooked unproven: refused under "local", said under "warn";
// - the by-name check covers the assets folder itself (a link there is refused) and every component below it.
// Run by tests/run.sh.
#include <sys/stat.h>
#include <unistd.h>

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>

#include "scan.h"

namespace fs = std::filesystem;
static int failures = 0;
#define CHECK(c)                                                         \
    do {                                                                 \
        if (!(c)) {                                                      \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c);     \
            ++failures;                                                  \
        }                                                                \
    } while (0)

using namespace ka;

static void put(const fs::path& p, const std::string& text) {
    fs::create_directories(p.parent_path());
    std::ofstream(p, std::ios::binary) << text;
}

// POSIX versions of the queries contained.cpp makes on Windows
static std::optional<std::vector<Entry>> list_posix(const fs::path& dir) {
    std::error_code ec;
    std::vector<Entry> out;
    for (auto it = fs::directory_iterator(dir, ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        const auto st = it->symlink_status(ec);
        out.push_back({it->path().filename().wstring(), fs::is_symlink(st) ? Entry::Link : fs::is_directory(st) ? Entry::Dir : Entry::File});
    }
    if (ec) return std::nullopt;
    return out;
}
static std::optional<FileId> id_posix(const fs::path& p) {
    struct stat st{};
    if (::stat(p.c_str(), &st) != 0) return std::nullopt;
    return FileId{static_cast<uint32_t>(st.st_dev), static_cast<uint32_t>(uint64_t(st.st_ino) >> 32), static_cast<uint32_t>(st.st_ino)};
}
static NameKind kind_posix(const fs::path& p) {
    std::error_code ec;
    const auto st = fs::symlink_status(p, ec);
    if (ec || !fs::exists(st)) return NameKind::Missing;
    return fs::is_symlink(st) ? NameKind::Link : NameKind::Plain;
}

int main(int argc, char** argv) {
    const fs::path root = argc > 1 ? fs::path(argv[1]) : fs::temp_directory_path() / "kiln-scan-test";
    fs::remove_all(root);
    const fs::path cooked = root / "mod-a" / "assets" / "cooked";

    // a cooked file 20 folders deep is found (the walk has no depth limit), and the scan is complete
    fs::path deep = cooked;
    for (int i = 0; i < 20; ++i) deep /= "d" + std::to_string(i);
    put(deep / "payload.bin", "an unreceipted cooked file, deep");
    put(cooked / "top.bin", "a cooked file");
    // a link in the cooked folder, to a folder and to a file outside: not followed, what it names is no file of it
    put(root / "outside" / "big" / "x.bin", "outside");
    fs::create_directory_symlink(root / "outside" / "big", cooked / "linkdir");
    fs::create_symlink(root / "outside" / "big" / "x.bin", cooked / "link.bin");
    // a cycle through a link would never end a walk that followed links
    fs::create_directory_symlink(cooked, cooked / "d0" / "loop");
    CookedIds ids;
    std::string why;
    CHECK(scan_tree(cooked, list_posix, id_posix, kScanBudget, &ids.ids, &why));
    CHECK(ids.ids.count(*id_posix(deep / "payload.bin")) == 1);
    CHECK(ids.ids.count(*id_posix(cooked / "top.bin")) == 1);
    CHECK(ids.ids.count(*id_posix(root / "outside" / "big" / "x.bin")) == 0);
    CHECK(ids.ids.size() == 2);
    // so the deep file, reached through a link to a file that another mod's manifest names (a plain file under Wine), is
    // an alias under every policy
    for (auto p : {CookedPolicy::Local, CookedPolicy::Warn, CookedPolicy::Any})
        CHECK(alias_verdict(false, ids.ids.count(*id_posix(deep / "payload.bin")) != 0, true, p) == AliasVerdict::Alias);

    // a scan that is cut (the budget; a folder that cannot be listed) says why, and the set is not complete
    FileIds few;
    CHECK(!scan_tree(cooked, list_posix, id_posix, 5, &few, &why) && why.find("more than 5 entries") != std::string::npos);
    fs::create_directories(cooked / "shut");
    fs::permissions(cooked / "shut", fs::perms::none);
    FileIds shut;
    const bool listed = scan_tree(cooked, list_posix, id_posix, kScanBudget, &shut, &why);
    fs::permissions(cooked / "shut", fs::perms::owner_all);
    if (::geteuid() != 0) CHECK(!listed && why.find("cannot list") != std::string::npos);
    // then a file outside its own assets/cooked cannot be shown to be no alias: refused under "local", said under "warn",
    // loaded under "any"; one named below its own assets/cooked is left to its receipt
    CHECK(alias_verdict(false, false, false, CookedPolicy::Local) == AliasVerdict::Unproven);
    CHECK(alias_verdict(false, false, false, CookedPolicy::Warn) == AliasVerdict::UnprovenWarn);
    CHECK(alias_verdict(false, false, false, CookedPolicy::Any) == AliasVerdict::Ok);
    CHECK(alias_verdict(true, true, false, CookedPolicy::Local) == AliasVerdict::Ok);
    CHECK(alias_verdict(false, false, true, CookedPolicy::Local) == AliasVerdict::Ok);

    // the by-name check: a plain path passes; a link at the assets folder itself, or below it, does not; nor a missing file
    const fs::path assets = root / "mod-b" / "assets";
    put(assets / "models" / "a.bin", "the mod's own");
    CHECK(names_plain(assets, "models/a.bin", kind_posix, &why));
    CHECK(!names_plain(assets, "models/none.bin", kind_posix, &why) && why.find("cannot read") != std::string::npos);
    fs::create_directory_symlink(root / "outside", assets / "alias");
    CHECK(!names_plain(assets, "alias/big/x.bin", kind_posix, &why) && why.find("alias is a link") != std::string::npos);
    const fs::path linked_mod = root / "mod-c";
    fs::create_directories(linked_mod);
    put(root / "elsewhere" / "assets.json", "{}");
    put(root / "elsewhere" / "x.bin", "outside the mod");
    fs::create_directory_symlink(root / "elsewhere", linked_mod / "assets");
    CHECK(!names_plain(linked_mod / "assets", "x.bin", kind_posix, &why) && why.find("assets folder is a link") != std::string::npos);

    // the load order's scan covers every enabled mod, with or without an assets.json: mod A is data-only (no
    // assets/assets.json) with an unreceipted assets/cooked/payload.bin; mod B names assets/loose.bin, a link to A's
    // file (a plain file under Wine). B's loose.bin is an alias, refused under every policy
    const fs::path a = root / "mod-data-only", b = root / "mod-loose";
    put(a / "kiln.mod.json", "{}");
    put(a / "assets" / "cooked" / "payload.bin", "unreceipted, in a mod without a manifest");
    put(b / "assets" / "assets.json", "{ \"format\": 1, \"entries\": [ { \"kind\": \"blob\", \"file\": \"loose.bin\" } ] }");
    fs::create_symlink(a / "assets" / "cooked" / "payload.bin", b / "assets" / "loose.bin");
    const CookedIds order = scan_load_order({a, b}, list_posix, id_posix, kind_posix);
    CHECK(order.complete && order.ids.count(*id_posix(b / "assets" / "loose.bin")) == 1);
    CHECK(alias_verdict(named_in_cooked("loose.bin"), order.ids.count(*id_posix(b / "assets" / "loose.bin")) != 0, order.complete,
                        CookedPolicy::Local) == AliasVerdict::Alias);
    // a mod whose cooked folder is a link adds nothing (a read through it is refused by name)
    fs::create_directories(root / "mod-linked-cooked" / "assets");
    fs::create_directory_symlink(a / "assets" / "cooked", root / "mod-linked-cooked" / "assets" / "cooked");
    CHECK(scan_load_order({root / "mod-linked-cooked"}, list_posix, id_posix, kind_posix).ids.empty());

    CHECK(named_in_cooked("cooked/x.bin") && named_in_cooked("Cooked/sub/x.bin") && !named_in_cooked("cooked") && !named_in_cooked("x/cooked/y"));

    fs::remove_all(root);
    if (failures) {
        std::printf("test_scan: %d failure(s)\n", failures);
        return 1;
    }
    std::printf("PASS test_scan\n");
    return 0;
}
