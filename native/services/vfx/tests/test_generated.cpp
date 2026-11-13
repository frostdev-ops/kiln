// SPDX-License-Identifier: MIT
// The limits kiln.vfx reads a mod's blocks/generated/*.json with (generated.h): links, file count, sizes, nesting.
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
    check(refusal(false, 0, 1000, 0) == nullptr, "a small file is read");
    check(refusal(true, 0, 10, 0) != nullptr, "a link or reparse point is refused, whatever its size");
    check(refusal(false, kMaxFiles, 10, 0) != nullptr && refusal(false, kMaxFiles - 1, 10, 0) == nullptr, "16 files at most");
    check(refusal(false, 0, kMaxFileBytes + 1, 0) != nullptr && refusal(false, 0, kMaxFileBytes, 0) == nullptr, "4 MiB a file at most");
    check(refusal(false, 4, kMaxFileBytes, kMaxTotalBytes - kMaxFileBytes + 1) != nullptr &&
              refusal(false, 3, kMaxFileBytes, kMaxTotalBytes - kMaxFileBytes) == nullptr,
          "16 MiB in all at most");
    check(depth_ok(R"({"vfx":{"a":{"particles":[{"burst":2}]}}})", 5) && !depth_ok(R"({"vfx":{"a":{"particles":[{"burst":2}]}}})", 4),
          "nesting is counted by arrays and objects");
    std::string deep(65, '[');
    deep += std::string(65, ']');
    check(!depth_ok(deep, kMaxDepth) && depth_ok(deep.substr(1, 128), kMaxDepth), "65 deep is refused, 64 is read");
    check(depth_ok(R"({"name":"[[[[[[[[[[","x":"\"[[[["})", 1), "brackets in strings, escaped quotes included, do not count");
    check(depth_ok("{ // [[[[[[\n /* {{{{{{ */ \"a\": 1 }", 1), "brackets in comments do not count");
    std::printf("generated-test: %s\n", failures ? "FAILED" : "all passed");
    return failures ? 1 : 0;
}
