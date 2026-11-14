#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of sigdb's disk scan (pe_text.h, sigdb_pattern.h). With exe paths it also runs the hook simulation on
# the real game files (read only), and with --client the core's own choice of file or live code on that exe:
#   native/core/tests/test-sigdisk.sh [--server <enshrouded_server.exe>] [--client <enshrouded.exe>]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
"$cxx" -std=c++20 -O2 -Wall -Wextra -I"$here/../../third_party" -o "$out/sigdisk_test" "$here/sigdisk_test.cpp"
"$out/sigdisk_test" "$@"
# sigdb checks `sig` descriptors against the function's code (services/hooks_fingerprint.cpp, decoded with Zydis)
cc=${CC:-$(command -v clang || command -v gcc)}
zy=$here/../../third_party/safetyhook
[[ $out/zydis-host.o -nt $zy/Zydis.c ]] || "$cc" -O1 -w -DZYDIS_STATIC_BUILD -DZYCORE_STATIC_BUILD -I"$zy" -c -o "$out/zydis-host.o" "$zy/Zydis.c"
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -DZYDIS_STATIC_BUILD -DZYCORE_STATIC_BUILD \
    -I"$here/stubs" -I"$here/../abi" -I"$here/../src" -I"$here/../../third_party" -I"$zy" \
    -o "$out/sigdb_runtime_test" "$here/sigdb_runtime_test.cpp" "$here/../src/services/hooks_fingerprint.cpp" "$out/zydis-host.o"
"$out/sigdb_runtime_test" "$out" "$@"
"$cxx" -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/../../services/npcai/tests/stubs" -I"$here/../abi" \
    -o "$out/npcai_hook_guard_test" "$here/npcai_hook_guard_test.cpp"
"$out/npcai_hook_guard_test"
