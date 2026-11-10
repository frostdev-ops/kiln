#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp -d /tmp/kiln-progress-test.XXXXXX)
trap 'rm -rf "$out"' EXIT
"${CXX:-c++}" -std=c++20 -O1 -Wall -Wextra -pthread "$here/progress_test.cpp" -o "$out/progress_test"
"$out/progress_test"
