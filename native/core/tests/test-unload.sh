#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of on_unload's arming decision (kiln.json "unloadAtExit" x Wine or native Windows). Cuts the pure block
# between "unload-policy:begin" and "unload-policy:end" out of native/core/src/boot.cpp as it is, builds
# unload_policy_test.cpp around it with the host's C++ compiler and runs the decision table. Exit code 0 = every row
# passed. No Wine and no game are needed.
#   native/core/tests/test-unload.sh
# Env: KILN_OUT                build dir (default native/build); the test goes to $KILN_OUT/unload-policy-test
#      KILN_UNLOAD_POLICY_SRC  another boot.cpp to cut the block from (to see a broken rule fail)
#      CXX                     the host compiler (default c++)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
src=${KILN_UNLOAD_POLICY_SRC:-$here/../src/boot.cpp}
out=${KILN_OUT:-$here/../../build}/unload-policy-test
mkdir -p "$out"
sed -n '/^\/\/ unload-policy:begin$/,/^\/\/ unload-policy:end$/p' "$src" >"$out/unload_policy_block.inc"
[[ -s $out/unload_policy_block.inc ]] || { echo "no unload-policy block in $src" >&2; exit 2; }
"${CXX:-c++}" -std=c++20 -Wall -Wextra -Werror -I "$out" "$here/unload_policy_test.cpp" -o "$out/unload_policy_test"
"$out/unload_policy_test"
