// SPDX-License-Identifier: MIT
// Host test of kcompat::ExportCache (export_cache.h): a DLL's export table is read once per version of the file.
#include "../export_cache.h"

#include <chrono>
#include <cstdio>
#include <fstream>
#include <string>

namespace fs = std::filesystem;

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const fs::path dir(argv[1]);
    fs::remove_all(dir);
    fs::create_directories(dir);
    int failures = 0, checks = 0;
    auto check = [&](bool ok, const char* what) {
        ++checks;
        if (!ok) ++failures;
        std::printf("%s: %s\n", ok ? "ok" : "FAIL", what);
    };
    int reads = 0;
    bool answer = true;
    auto read = [&](const fs::path&, std::string_view) {
        ++reads;
        return answer;
    };
    kcompat::ExportCache cache;
    const fs::path dll = dir / "mod.dll";
    std::ofstream(dll, std::ios::binary) << std::string(4096, 'x');
    check(cache.exports(dll, "CreateModInstance", read) && reads == 1, "a new DLL is read");
    for (int i = 0; i < 1000; ++i) cache.exports(dll, "CreateModInstance", read);  // 500 s of looks every 500 ms
    check(reads == 1, "...and not again while it is unchanged");
    answer = false;
    check(cache.exports(dll, "CreateModInstance", read) && reads == 1, "the answer is the file's, kept");
    check(!cache.exports(dll, "OtherExport", read) && reads == 2, "another name is its own question");
    // a new version of the file: another size, or the same size written later
    std::ofstream(dll, std::ios::binary) << std::string(8192, 'y');
    check(!cache.exports(dll, "CreateModInstance", read) && reads == 3, "a DLL that changed size is read again");
    std::ofstream(dll, std::ios::binary) << std::string(8192, 'z');
    fs::last_write_time(dll, fs::last_write_time(dll) + std::chrono::seconds(5));
    answer = true;
    check(cache.exports(dll, "CreateModInstance", read) && reads == 4, "a DLL written again with the same size is read again");
    check(cache.exports(dll, "CreateModInstance", read) && reads == 4, "...and kept after that");
    // a file that is gone is asked every time, and not kept
    const size_t kept = cache.size();
    answer = false;
    check(!cache.exports(dir / "gone.dll", "CreateModInstance", read) && reads == 5, "a missing file is asked");
    check(!cache.exports(dir / "gone.dll", "CreateModInstance", read) && reads == 6 && cache.size() == kept, "...every time, and not kept");
    fs::remove_all(dir);
    std::printf("export cache: %d/%d checks passed\n", checks - failures, checks);
    return failures ? 1 : 0;
}
