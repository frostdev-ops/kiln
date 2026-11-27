#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Run through the shared local build limiter; no game execution.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${1:?usage: test-ammo.sh OUTPUT_DIRECTORY}
mkdir -p "$out"
for test in ammo_abi ammo_list ammo_allocation ammo_scope ammo_sources ammo_hook ammo_reveal ammo_trace presentation presentation_api presentation_admission brush_frame brush_trace held_state held_observer service_presentation_lifecycle; do
    extra=()
    [[ $test == service_presentation_lifecycle ]] && extra+=("$here/../hooks/presentation.cpp")
    "${CXX:-c++}" -std=c++20 -pthread -D__fastcall= -I"$here/../../../core/abi" -I"$here/../include" -I"$here/../../../third_party" \
        "$here/$test.cpp" "$here/../policy.cpp" "${extra[@]}" -o "$out/$test"
    "$out/$test"
done
