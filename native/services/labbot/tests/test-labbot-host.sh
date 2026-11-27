#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# host tests of kiln.labbot's portable part (bot.cpp: the bot's wire bytes, identity, the session snapshot entries and
# its join and leave over a fake GameServer), no game, no Wine, under ASan/UBSan.
#   native/services/labbot/tests/test-labbot-host.sh
# Build output: $KILN_OUT/labbot-host-tests (KILN_OUT default native/build).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../../build}/labbot-host-tests
mkdir -p "$out"
cxx=${CXX:-clang++}
flags=(-std=c++20 -O1 -g -Wall -Wextra -Werror -pthread -fno-sanitize-recover=all)
"$cxx" "${flags[@]}" -fsanitize=address,undefined "$here/bot_test.cpp" "$here/../bot.cpp" -o "$out/bot_test"
"$out/bot_test"
