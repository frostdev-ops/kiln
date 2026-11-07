#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-/tmp/kiln-review-set}/set-lua-host
mkdir -p "$out"
root=$(cd "$here/../../.." && pwd)
lua_sources=()
for source in "$root"/third_party/lua/*.c; do
    case "$(basename "$source")" in lua.c|luac.c|onelua.c) ;; *) lua_sources+=("$source");; esac
done
g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -Wno-unused-parameter -Wno-unused-function -Wno-missing-field-initializers -Wno-sign-compare -fsanitize=address,undefined \
  -I"$here/lua_host" -I"$here/../src" -I"$root/core/abi" -I"$root/third_party/lua" \
  "$here/lua_runtime_test.cpp" "$here/../src/game.cpp" "$here/../src/lua_json.cpp" -x c++ "${lua_sources[@]}" -o "$out/lua_runtime_test"
ASAN_OPTIONS=${ASAN_OPTIONS:-detect_leaks=0} "$out/lua_runtime_test" "$out"
