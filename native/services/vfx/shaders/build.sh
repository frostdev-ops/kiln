#!/bin/bash
# Compiles the starter modules with the local glslc into build/*.spv, the files Kiln ships. Only a change to a module
# needs this: building Kiln itself needs no shader compiler. Checks: test/test_starter.py.
set -euo pipefail
cd "$(dirname "$0")"
for name in kiln_emitter kiln_sprite kiln_light; do
    glslc --target-env=vulkan1.2 --target-spv=spv1.5 -O -x hlsl -fshader-stage=compute -fentry-point=cs_main \
        -I . "$name.hlsl" -o "build/$name.spv"
    spirv-val --target-env vulkan1.2 --scalar-block-layout "build/$name.spv"
    echo "$name: $(stat -c %s "build/$name.spv") bytes"
done
