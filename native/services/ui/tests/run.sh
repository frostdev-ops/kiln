#!/usr/bin/env bash
# Host tests of projection, the render service, callback lifetime and signature guards: no game, GPU or Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/ui-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/render_math_test.cpp" -o "$out/render_math_test"
"$out/render_math_test"

imgui=$here/../../../third_party/imgui
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/../../../core/abi" -I"$imgui" \
    "$here/render_host_test.cpp" "$imgui/imgui.cpp" "$imgui/imgui_draw.cpp" \
    "$imgui/imgui_tables.cpp" "$imgui/imgui_widgets.cpp" -o "$out/render_host_test"
"$out/render_host_test"
PYTHONDONTWRITEBYTECODE=1 python3 "$here/render_sigdb_test.py"
