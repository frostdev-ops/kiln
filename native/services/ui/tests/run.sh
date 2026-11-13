#!/usr/bin/env bash
# Host tests of projection, the radial wheel's geometry and rules, the main-menu page, the render service, callback
# lifetime and signature guards: no game, GPU or Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
export KILN_OUT
out=$KILN_OUT/ui-host-test
mkdir -p "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/render_math_test.cpp" -o "$out/render_math_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/wheel_math_test.cpp" -o "$out/wheel_math_test"
"$out/wheel_math_test"
"$out/render_math_test"
# which renderer call is the main menu's Settings entry (another module wrapping the game's call): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/menu_site_test.cpp" -o "$out/menu_site_test"
"$out/menu_site_test"
# which of Kiln's rows a game menu draws (mods' rows past a few would push Quit off the screen): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../core/abi" "$here/menu_rows_test.cpp" -o "$out/menu_rows_test"
"$out/menu_rows_test"
# the scrolling pause menu's rules: begin/end pairing, when it scrolls, the crash guard (menu_scroll.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../core/abi" "$here/menu_scroll_test.cpp" -o "$out/menu_scroll_test"
"$out/menu_scroll_test"
# the middle and side mouse buttons and the wheel as key presses: twins of raw records and messages, notches (mouse_press.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/mouse_press_test.cpp" -o "$out/mouse_press_test"
"$out/mouse_press_test"
# the window procedure chain (another mod subclassing the game window after Kiln): fake window functions
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/chain_host" "$here/wndproc_chain_test.cpp" -o "$out/wndproc_chain_test"
"$out/wndproc_chain_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/../../../core/abi" "$here/menu_page_host_test.cpp" -o "$out/menu_page_host_test"
"$out/menu_page_host_test"

imgui=$here/../../../third_party/imgui
# Dear ImGui once, as objects, for both ImGui tests
for f in imgui imgui_draw imgui_tables imgui_widgets; do
    [[ $out/$f.o -nt $imgui/$f.cpp ]] || g++ -std=c++20 -O1 -g -w -fsanitize=address,undefined -I"$imgui" -c "$imgui/$f.cpp" -o "$out/$f.o" &
done
wait
objs=("$out/imgui.o" "$out/imgui_draw.o" "$out/imgui_tables.o" "$out/imgui_widgets.o")
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/../../../core/abi" -I"$imgui" \
    "$here/render_host_test.cpp" "${objs[@]}" -o "$out/render_host_test"
"$out/render_host_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/../../../core/abi" -I"$imgui" \
    "$here/wheel_host_test.cpp" "${objs[@]}" -o "$out/wheel_host_test"
"$out/wheel_host_test"
PYTHONDONTWRITEBYTECODE=1 python3 "$here/render_sigdb_test.py"
PYTHONDONTWRITEBYTECODE=1 python3 "$here/per_caller_test.py"
