#!/bin/bash
# Compiles the screen pass with the local glslc into build/*.spv, the files Kiln ships. Only a change to screen.hlsl needs this: building
# Kiln itself needs no shader compiler. Checks: tests/test_gpu.py.
set -euo pipefail
cd "$(dirname "$0")"
compile() {
    glslc --target-env=vulkan1.0 --target-spv=spv1.0 -O -x hlsl -fshader-stage="$1" -fentry-point="$2" screen.hlsl -o "build/$3.spv"
    spirv-val --target-env vulkan1.0 "build/$3.spv"
    echo "$3: $(stat -c %s "build/$3.spv") bytes"
}
compile vertex vs_main screen_vs
compile fragment ps_main screen_fs
