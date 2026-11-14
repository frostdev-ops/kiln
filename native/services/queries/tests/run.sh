#!/usr/bin/env bash
# The real service with fake game leaves, plus its pure parts. No game or Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=/tmp/kiln-queries-tests}"
mkdir -p "$KILN_OUT/queries-tests"
mkdir -p "$KILN_OUT/queries-tests/stubs"
cat >"$KILN_OUT/queries-tests/stubs/windows.h" <<'HEADER'
#pragma once
struct LARGE_INTEGER { long long QuadPart; };
inline int QueryPerformanceFrequency(LARGE_INTEGER* out) { out->QuadPart = 1000000000; return 1; }
inline int QueryPerformanceCounter(LARGE_INTEGER* out) { static long long now = 0; out->QuadPart = ++now; return 1; }
inline unsigned long GetCurrentThreadId() { return 1; }
HEADER
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$KILN_OUT/queries-tests/stubs" -I"$here/../../../core/abi" \
    -I"$here/.." -I"$here/../../../third_party" "$here/queries_test.cpp" -o "$KILN_OUT/queries-tests/queries-test"
"$KILN_OUT/queries-tests/queries-test"
# path.cpp's admission (path_submit) against kiln.world's in_program, with the service's globals faked.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$KILN_OUT/queries-tests/stubs" -I"$here/../../../core/abi" \
    -I"$here/.." -I"$here/../../../third_party" "$here/path_submit_test.cpp" -o "$KILN_OUT/queries-tests/path-submit-test"
"$KILN_OUT/queries-tests/path-submit-test"
