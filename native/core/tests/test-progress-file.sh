#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host test of progress.json's model and writer (src/progress_file.h). No Win32, no Wine.
#   native/core/tests/test-progress-file.sh            (scratch files under $TMPDIR, else /tmp)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=$(mktemp -d "${TMPDIR:-/tmp}/kiln-progress-file-test.XXXXXX")
trap 'rm -rf "$out"' EXIT
"${CXX:-c++}" -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -I"$here/../../third_party" \
  "$here/progress_file_test.cpp" -o "$out/progress_file_test"
"$out/progress_file_test"
