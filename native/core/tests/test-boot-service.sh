#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of kiln.boot (services/boot_service.cpp): deferred mode, staging, the commit protocol with six fake
# collectors, the hold path (fake OS-exit adapter) and the buffer rules, with ASan/UBSan:
#   native/core/tests/test-boot-service.sh
# Build output and scratch: $KILN_OUT/core-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/core-tests
mkdir -p "$out"
cxx=${CXX:-$(command -v clang++ || command -v g++)}
flags=(-std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -fno-sanitize-recover=undefined
       -I"$here/stubs" -I"$here/../abi" -I"$here/../src" -I"$here/../../third_party")
"$cxx" "${flags[@]}" -o "$out/boot_service_test" "$here/boot_service_test.cpp"
timeout 120 "$out/boot_service_test" "$out"
