#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-/tmp/kiln-review-set}/set-review
mkdir -p "$out"
${CXX:-clang++} -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -fno-omit-frame-pointer \
  -I"$here/tests/hooks_host" -I"$here/../../core/abi" \
  "$here/tests/hooks_review_test.cpp" -o "$out/hooks_review_test"
# LeakSanitizer's thread discovery is blocked by the sandbox's ptrace policy. ASan/UBSan remain enabled.
ASAN_OPTIONS=detect_leaks=0 "$out/hooks_review_test"
${CXX:-clang++} -std=c++20 -O1 -Wall -Wextra -I"$here/tests/plugin_host" -I"$here/../../core/abi" \
  "$here/tests/plugin_review_test.cpp" -o "$out/plugin_review_test"
"$out/plugin_review_test" "$out/loader-fixture"
lua_sources=()
for source in "$here"/../../third_party/lua/*.c; do
  case ${source##*/} in lua.c|luac.c) continue ;; esac
  lua_sources+=("$source")
done
${CXX:-clang++} -std=c++20 -O1 -w -x c++ -DLUA_USE_LINUX -I"$here/../../third_party/lua" \
  -I"$here/../../core/abi" "${lua_sources[@]}" "$here/tests/imgui_review_test.cpp" -ldl \
  -o "$out/imgui_review_test"
"$out/imgui_review_test"
