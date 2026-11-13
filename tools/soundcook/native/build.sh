#!/usr/bin/env bash
# Builds kiln-soundcook (tools/soundcook's native helper).
#   build.sh            this machine (cc; cmake not needed)  -> $KILN_OUT/soundcook/bin/kiln-soundcook
#   build.sh --windows  Windows x64 with clang-cl + the xwin SDK (as native/build.sh)
#                                                            -> $KILN_OUT/soundcook/win/bin/kiln-soundcook.exe
# KILN_OUT defaults to <repo>/.out/soundcook.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
out=${KILN_OUT:-$root/.out/soundcook}/soundcook
tp=$root/native/third_party
if [[ ${1:-} == --windows ]]; then
  cmake -S "$here" -B "$out/win" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$root/native/cmake/clang-cl-xwin.cmake" \
    -DCMAKE_BUILD_TYPE=Release -Wno-dev >/dev/null
  cmake --build "$out/win"
  echo "$out/win/bin/kiln-soundcook.exe"
  exit 0
fi
cc=${CC:-cc}
mkdir -p "$out/bin" "$out/obj"
# -ffile-prefix-map: the decoders' asserts name their source file (__FILE__); without it the helper, and kiln and the
# launcher that carry it, would hold the build machine's path (the release scan refuses a home path)
common=(-O2 -ffp-contract=off -fno-fast-math -ffile-prefix-map="$root"=. -I"$here/src" -I"$tp")
"$cc" "${common[@]}" -std=c11 -Wall -Wextra -c "$here/src/soundcook.c" -o "$out/obj/soundcook.o"
"$cc" "${common[@]}" -std=c11 -Wall -Wextra -c "$here/src/qoa_frameless.c" -o "$out/obj/qoa_frameless.o"
"$cc" "${common[@]}" -w -c "$here/src/decoders.c" -o "$out/obj/decoders.o"
"$cc" "${common[@]}" -w -c "$here/src/vorbis.c" -o "$out/obj/vorbis.o"
"$cc" "${common[@]}" -w -DOUTSIDE_SPEEX -DRANDOM_PREFIX=kiln_speex -DFLOATING_POINT -DEXPORT= \
  -c "$tp/speexdsp/resample.c" -o "$out/obj/resample.o"
"$cc" "$out"/obj/{soundcook,qoa_frameless,decoders,vorbis,resample}.o -lm -o "$out/bin/kiln-soundcook.tmp"
mv "$out/bin/kiln-soundcook.tmp" "$out/bin/kiln-soundcook"
echo "$out/bin/kiln-soundcook"
