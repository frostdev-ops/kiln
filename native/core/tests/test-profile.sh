#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
set -euo pipefail
# native/core/tests/test-profile.sh [profile.json written by kiln-manage ...]: the production profile parser on the host.
here=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp -d "${TMPDIR:-/tmp}/kiln-profile-test.XXXXXX")
trap 'rm -rf "$out"' EXIT
# profile.cpp's parser uses no Win32 calls; the two platform headers are replaced for this host-only test.
touch "$out/windows.h"
printf '#define KILN_VERSION "test"\n' > "$out/kiln_version.h"
"${CXX:-c++}" -std=c++20 -O1 -Wall -Wextra -I"$out" -I"$here/../abi" -I"$here/../../third_party" \
    "$here/profile_test.cpp" -o "$out/profile_test"
# The folder-name table every validator of ../mods/<folder> is checked with; then the profiles kiln-manage wrote, if any
"$out/profile_test" "$out/game" "$here/../../../schema/fixtures/profile/game-folder-names.json" "$@"
