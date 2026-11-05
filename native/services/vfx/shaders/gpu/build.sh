#!/bin/bash
# Builds the headless runner into $KILN_OUT (default: a folder in the system temp dir). A test tool, not part of Kiln.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../../../.." && pwd)"
out="${KILN_OUT:-${TMPDIR:-/tmp}/kiln-vfx-gpu}"
mkdir -p "$out"
g++ -std=c++17 -O1 -g -Wall -Wno-missing-field-initializers -fPIC -shared \
    -I "$root/native/third_party/imgui/vulkan-headers/include" \
    "$here/vfxgpu.cpp" -o "$out/libvfxgpu.so" -lvulkan
echo "$out/libvfxgpu.so"
