#!/usr/bin/env bash
# Host tests of projection, the radial wheel's geometry and rules, the main-menu page, the render service, callback
# lifetime and signature guards: no game, GPU or Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
export KILN_OUT
out=$KILN_OUT/ui-host-test
mkdir -p "$out"
bash "$here/run-window-constraints.sh"
# Kiln's graphics tools: the frame cap's pacing, kiln.json "graphics", the DLSS render size, when they step aside, NGX's
# parameter slots (frame_pacer.h, graphics_settings.h, graphics_aside.h, ngx_param.h): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" "$here/graphics_test.cpp" -o "$out/graphics_test"
"$out/graphics_test" "$out"
# the upscaler swap (phase B): kiln.json "upscaler", the game's FSR description and Kiln's render command, history,
# contexts, the status, AMD FSR's device needs and dispatch mapping (upscale_logic.h, upscale_amd_map.h): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -DVK_NO_PROTOTYPES '-D__declspec(x)=' -I"$here/../../../third_party" \
    -I"$here/../../../third_party/imgui/vulkan-headers/include" -I"$here/../../../third_party/ffx" -I"$here/../../../core/abi" \
    "$here/upscale_test.cpp" -o "$out/upscale_test"
"$out/upscale_test" "$out"
# phase B's upscaler swap (0.2.0-dev.29.18): the game's vendor pins against the launcher's manifest; the vendor files'
# check and its SHA-256 (also over the real files when KILN_VENDOR_FILES names their folder, dev29/vendor-files); the
# merge of a backend's Vulkan needs into the game's create infos; the XeSS backend against a fake libxess
python3 -I "$here/../../../../tools/release/vendor_pins_header.py" --check
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" "$here/vendor_file_test.cpp" -o "$out/vendor_file_test"
"$out/vendor_file_test" "$out" ${KILN_VENDOR_FILES:+"$KILN_VENDOR_FILES"}
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -DVK_NO_PROTOTYPES '-D__declspec(x)=' -I"$here/../../../third_party/imgui/vulkan-headers/include" \
    -I"$here/../../../third_party" -I"$here/../../../third_party/ffx" "$here/device_merge_test.cpp" -o "$out/device_merge_test"
"$out/device_merge_test"
if [[ -f $here/xess_test.cpp ]]; then  # the XeSS backend's test (dev29/upscale-b2)
    g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party/imgui/vulkan-headers/include" -I"$here/../../../third_party" \
        "$here/xess_test.cpp" -o "$out/xess_test"
    "$out/xess_test"
fi
# the overlay's image budget: the descriptor pool holds every image Kiln lets exist, uploads spread over frames (image_budget.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/image_budget_test.cpp" -o "$out/image_budget_test"
"$out/image_budget_test" "$here/../vk.cpp"
# an image's opaque bounds (trimmed item icons), their UVs and fitting a source in a box (image_rect.h): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/image_rect_test.cpp" -o "$out/image_rect_test"
"$out/image_rect_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/render_math_test.cpp" -o "$out/render_math_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/wheel_math_test.cpp" -o "$out/wheel_math_test"
"$out/wheel_math_test"
"$out/render_math_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/overlay_guard_test.cpp" -o "$out/overlay_guard_test"
"$out/overlay_guard_test"
# the crash guard's runtime (overlay_guard.cpp) over host stand-ins for its Windows calls, one game start per process:
# the record a guarded start writes, what a normal exit writes, nothing written after it (guard_host/windows.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/guard_host" -I"$here/../../../core/abi" \
    -I"$here/../../../third_party" "$here/overlay_guard_host_test.cpp" -o "$out/overlay_guard_host_test"
"$out/overlay_guard_host_test"
# the overlay's mode, and the player's kiln.json ui.overlay switch (overlay_mode.h, overlay_setting.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" "$here/overlay_mode_test.cpp" -o "$out/overlay_mode_test"
"$out/overlay_mode_test"
# which present queue the overlay draws on, queues handed out while the device is made, one present's semaphores
# (vk_present.h, inbox 80)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/vk_present_test.cpp" -o "$out/vk_present_test"
"$out/vk_present_test"
# imgui.ini read and written on its own thread, never on the present call (ini_file.h, inbox 50)
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=thread "$here/ini_file_test.cpp" -o "$out/ini_file_test"
"$out/ini_file_test"
# which renderer call is the main menu's Settings entry (another module wrapping the game's call): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/menu_site_test.cpp" -o "$out/menu_site_test"
"$out/menu_site_test"
# which of Kiln's rows a game menu draws (mods' rows past a few would push Quit off the screen): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../core/abi" "$here/menu_rows_test.cpp" -o "$out/menu_rows_test"
"$out/menu_rows_test"
# the scrolling pause menu's rules: begin/end pairing, when it scrolls, the crash guard (menu_scroll.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../core/abi" "$here/menu_scroll_test.cpp" -o "$out/menu_scroll_test"
"$out/menu_scroll_test"
bash "$here/run-menu-scroll-host.sh"
# the middle and side mouse buttons and the wheel as key presses: twins of raw records and messages, notches (mouse_press.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/mouse_press_test.cpp" -o "$out/mouse_press_test"
"$out/mouse_press_test"
# Issue 44: scoped chat Up/Down and UI-thread queue must never steal gameplay or releases.
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined "$here/chat_keys_test.cpp" -o "$out/chat_keys_test"
"$out/chat_keys_test"
# the same under ThreadSanitizer: the window thread's key router against the render thread's completion state
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=thread "$here/chat_keys_test.cpp" -o "$out/chat_keys_tsan_test"
"$out/chat_keys_tsan_test"
# Issue 44 Phase 1b: bounded, UTF-8-valid editor snapshots and revision-safe replacement.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/chat_completion_text_test.cpp" -o "$out/chat_completion_text_test"
"$out/chat_completion_text_test"
# Completion is an internal append-only kiln.ui tail; keep ABI offsets verified in the standard native suite.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../core/abi" \
    "$here/chat_completion_abi_test.cpp" -o "$out/chat_completion_abi_test"
"$out/chat_completion_abi_test"
# Issue 44 Phase 1c: bounded reverse history search, matching, UTF-16/UTF-8 and distinct entries.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/chat_search_test.cpp" -o "$out/chat_search_test"
"$out/chat_search_test"
# Issue 44: history draft, commands-only persistence policy, sensitive text, UTF-8.
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/chat_history_test.cpp" -o "$out/chat_history_test"
"$out/chat_history_test"
# Issue 44: chat-history.json's format, and its writer thread (the UI thread never waits for the disk; chat_store.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined -I"$here/../../../third_party" "$here/chat_store_test.cpp" \
    -o "$out/chat_store_test"
"$out/chat_store_test"
# one key press counts once, also across a focus loss (key_hold.h, GitHub issue 39): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/key_hold_test.cpp" -o "$out/key_hold_test"
"$out/key_hold_test"
# Repeat-window configuration on the render thread alongside key events on the window thread.
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=thread "$here/key_hold_thread_test.cpp" -o "$out/key_hold_thread_test"
"$out/key_hold_thread_test"
# key names and which key a binding means on any layout (key_names.h, GitHub issue 33): plain logic
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/key_names_test.cpp" -o "$out/key_names_test"
"$out/key_names_test"
# the window procedure chain (another mod subclassing the game window after Kiln): fake window functions
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/chain_host" "$here/wndproc_chain_test.cpp" -o "$out/wndproc_chain_test"
"$out/wndproc_chain_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/../../../core/abi" "$here/menu_page_host_test.cpp" -o "$out/menu_page_host_test"
"$out/menu_page_host_test"
# Kiln's line in the main menu's footer: its text, its mark's place, when Kiln's copy runs (menu_footer.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/menu_footer_test.cpp" -o "$out/menu_footer_test"
"$out/menu_footer_test" "$here/../../../../sigdb/symbols/client.d/ui.json"
# Kiln's own texts kiln.ui draws (ui_text.h): the handoff from Kiln.Ui, English fallback, plurals, placeholders, and the
# built-in English against runtime/locales/en.json
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../../../third_party" "$here/ui_text_test.cpp" -o "$out/ui_text_test"
"$out/ui_text_test" "$here/../../../../runtime/locales/en.json"
# which of the game's fonts gives the overlay a Chinese, Japanese or Korean language's glyphs (font_group.h), made-up data
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined "$here/font_group_test.cpp" -o "$out/font_group_test"
"$out/font_group_test"
# the footer function's copy keeps its meaning (code_clone.h, Zydis from safetyhook); also over the game's own footer
# function when the client exe is installed (read only)
safetyhook=$here/../../../third_party/safetyhook
[[ $out/zydis.o -nt $safetyhook/Zydis.c ]] || gcc -O1 -g -w -I"$safetyhook" -c "$safetyhook/Zydis.c" -o "$out/zydis.o"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$safetyhook" "$here/code_clone_test.cpp" "$out/zydis.o" \
    -o "$out/code_clone_test"
"$out/code_clone_test"

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
# the looks: the game's colours through reflection, the player's look, themes, mod fonts (style.cpp, look_palette.h, font_budget.h)
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/.." -I"$here/../../../core/abi" -I"$imgui" -I"$here/../../../third_party" \
    "$here/style_host_test.cpp" "${objs[@]}" -o "$out/style_host_test"
"$out/style_host_test"
# the game's Chinese, Japanese and Korean fonts in the overlay's atlas (style.cpp, font_group.h): made-up resource, system TTF
# (KILN_TEST_CJK_FONT=<an extracted CJK TTF/OTF, outside the tree> also checks a real CJK glyph)
g++ -std=c++20 -O1 -g -Wall -Wextra -Wno-unused-function -pthread -fsanitize=address,undefined \
    -I"$here/stubs" -I"$here/.." -I"$here/../../../core/abi" -I"$imgui" -I"$here/../../../third_party" \
    "$here/group_host_test.cpp" "${objs[@]}" -o "$out/group_host_test"
"$out/group_host_test"
PYTHONDONTWRITEBYTECODE=1 python3 "$here/render_sigdb_test.py"
# chat history's sigdb names are all gated by chat.ui.input; with the client exe installed, its layout resolves (read only)
PYTHONDONTWRITEBYTECODE=1 python3 "$here/chat_sigdb_test.py"
PYTHONDONTWRITEBYTECODE=1 python3 "$here/per_caller_test.py"
