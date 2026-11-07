#!/bin/bash
# Compiles the starter modules with the local glslc (and glslangValidator, spirv-opt) into build/*.spv, the files Kiln ships. Only a change to a module
# needs this: building Kiln itself needs no shader compiler. Checks: test/test_starter.py.
set -euo pipefail
cd "$(dirname "$0")"
for name in kiln_emitter kiln_sprite kiln_light; do
    glslc --target-env=vulkan1.2 --target-spv=spv1.5 -O -x hlsl -fshader-stage=compute -fentry-point=cs_main \
        -I . "$name.hlsl" -o "build/$name.spv"
    spirv-val --target-env vulkan1.2 --scalar-block-layout "build/$name.spv"
    echo "$name: $(stat -c %s "build/$name.spv") bytes"
done
# The sprite with link points for mods' hook modules (cook/spvlink.cpp). glslangValidator -Od keeps the hook functions apart (glslc's HLSL
# legalisation always inlines), noinline.py marks them DontInline, and spirv-opt -O optimises everything else around them.
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
glslangValidator -V -D -Od -e cs_main -S comp --target-env vulkan1.2 -I. kiln_sprite_hooked.hlsl -o "$tmp/od.spv" >/dev/null
python3 noinline.py "$tmp/od.spv" "$tmp/ni.spv"
spirv-opt -O --target-env=vulkan1.2 "$tmp/ni.spv" -o build/kiln_sprite_hooked.spv
spirv-val --target-env vulkan1.2 --scalar-block-layout build/kiln_sprite_hooked.spv
echo "kiln_sprite_hooked: $(stat -c %s build/kiln_sprite_hooked.spv) bytes"
