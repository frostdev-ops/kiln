#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp -d /tmp/kiln-profile-test.XXXXXX)
trap 'rm -rf "$out"' EXIT
# profile.cpp's parser uses no Win32 calls; the two platform headers are replaced for this host-only test.
touch "$out/windows.h"
printf '#define KILN_VERSION "test"\n' > "$out/kiln_version.h"
"${CXX:-c++}" -std=c++20 -O1 -Wall -Wextra -I"$out" -I"$here/../abi" -I"$here/../../third_party" \
    "$here/profile_test.cpp" -o "$out/profile_test"
"$out/profile_test" "$out/game"
