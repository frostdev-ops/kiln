#!/usr/bin/env bash
# kiln.npcspeech's core (what a bubble draw shows, the record's voice-over mute and restore, since 0.2.0-dev.32 the picks
# and the dialogue menu's parts on a fake window) without a game or Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=/tmp/kiln-npcspeech-tests}"
mkdir -p "$KILN_OUT/npcspeech-tests"
for t in npcspeech npcdialog; do
    g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
        -I"$here/.." -I"$here/../../../core/abi" "$here/${t}_test.cpp" -o "$KILN_OUT/npcspeech-tests/${t}-test"
    "$KILN_OUT/npcspeech-tests/${t}-test"
done
