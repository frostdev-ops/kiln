// SPDX-License-Identifier: MIT
// The limits kiln.vfx reads a mod's JSON with (generated.h): file count, sizes, nesting, and what lies inside the mod.
#include <cstdio>
#include <string>

#include "../generated.h"

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    failures += !ok;
}
}  // namespace

int main() {
    using namespace kvx_generated;
    const Limits& g = kGenerated;
    check(refusal(g, 0, 1000, 0) == nullptr, "a small file is read");
    check(refusal(g, g.files, 10, 0) != nullptr && refusal(g, g.files - 1, 10, 0) == nullptr, "16 baked files at most");
    check(refusal(g, 0, g.file_bytes + 1, 0) != nullptr && refusal(g, 0, g.file_bytes, 0) == nullptr, "4 MiB a baked file at most");
    check(refusal(g, 4, g.file_bytes, g.total_bytes - g.file_bytes + 1) != nullptr &&
              refusal(g, 3, g.file_bytes, g.total_bytes - g.file_bytes) == nullptr,
          "16 MiB of baked files in all at most");
    const Limits& b = kBlocks;
    check(b.files == 256 && b.file_bytes == (16ull << 20) && b.total_bytes == (64ull << 20) && b.depth == 64 && b.entries == 4096,
          "the author's block files: 256 files, 16 MiB each, 64 MiB in all, 64 deep, 4096 entries looked at");
    check(refusal(b, 255, 6 * 1024, 255ull * 6 * 1024) == nullptr && refusal(b, 0, (16ull << 20) + 1, 0) != nullptr,
          "a real mod's blocks pass; a 16 MiB file does not");
    check(depth_ok(R"({"vfx":{"a":{"particles":[{"burst":2}]}}})", 5) && !depth_ok(R"({"vfx":{"a":{"particles":[{"burst":2}]}}})", 4),
          "nesting is counted by arrays and objects");
    std::string deep(65, '[');
    deep += std::string(65, ']');
    check(!depth_ok(deep, 64) && depth_ok(deep.substr(1, 128), 64), "65 deep is refused, 64 is read");
    check(depth_ok(R"({"name":"[[[[[[[[[[","x":"\"[[[["})", 1), "brackets in strings, escaped quotes included, do not count");
    check(depth_ok("{ // [[[[[[\n /* {{{{{{ */ \"a\": 1 }", 1), "brackets in comments do not count");

    const std::wstring root = LR"(\\?\Z:\srv\kiln\mods\fx)";
    check(path_inside(root, LR"(\\?\Z:\srv\kiln\mods\fx\blocks\a.json)"), "a file of the mod's blocks is inside");
    check(path_inside(root + L"\\", LR"(\\?\z:\SRV\kiln\mods\FX\blocks\generated\fx.json)"), "case does not matter, a root's last '\\' neither");
    check(!path_inside(root, LR"(\\?\Z:\srv\kiln\mods\fx2\blocks\a.json)"), "a sibling folder whose name starts the same is outside");
    check(!path_inside(root, LR"(\\?\Z:\srv\elsewhere\blocks\a.json)"), "a link's target elsewhere is outside");
    check(!path_inside(root, root), "the mod's folder itself is not below it");
    check(!path_inside(root, LR"(\\?\Z:\srv\kiln\mods\fx\blocks\..\..\other\a.json)"), "a step back out is outside");
    check(!path_inside(L"", LR"(\\?\Z:\x)"), "no root: nothing is inside");
    std::printf("generated-test: %s\n", failures ? "FAILED" : "all passed");
    return failures ? 1 : 0;
}
