#!/bin/bash
# Builds kiln.screen's headless GPU runner into $KILN_OUT (default: a folder in the system temp dir). A test tool, not part of Kiln.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../../.." && pwd)"
out="${KILN_OUT:-${TMPDIR:-/tmp}/kiln-screen-gpu}"
mkdir -p "$out"
# pass.cpp loads its Vulkan functions through what it is given. Only this runner links the loader, with the prototypes on.
g++ -std=c++23 -O1 -g -Wall -Wno-missing-field-initializers -fPIC -c -I "$root/native/core/abi" -I "$root/native/third_party/imgui/vulkan-headers/include" -I "$here/.." \
    "$here/../pass.cpp" -o "$out/screen_pass.o"
g++ -std=c++23 -O1 -g -Wall -Wno-missing-field-initializers -fPIC -c -I "$root/native/core/abi" -I "$here/.." "$here/../core.cpp" -o "$out/screen_core.o"
g++ -std=c++23 -O1 -g -Wall -Wno-missing-field-initializers -fPIC -shared -I "$root/native/core/abi" -I "$root/native/third_party/imgui/vulkan-headers/include" -I "$here/.." \
    "$here/screengpu.cpp" "$out/screen_pass.o" "$out/screen_core.o" -o "$out/libscreengpu.so" -lvulkan -pthread
echo "$out/libscreengpu.so"
