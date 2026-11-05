# SPDX-License-Identifier: MIT
# kiln-soundcook: the native helper of tools/soundcook (decode, resample to 48 kHz, FramelessQOA encode).
# Host build:     tools/soundcook/native/build.sh            -> $KILN_OUT/soundcook/bin/kiln-soundcook
# Windows build:  tools/soundcook/native/build.sh --windows  (clang-cl + xwin, native/cmake/clang-cl-xwin.cmake)
# Third-party code (native/third_party): dr_libs (public domain / MIT-0), stb_vorbis (public domain / MIT), the
# speexdsp resampler (BSD-3-Clause). Nothing GPL/LGPL; no game code or data.
cmake_minimum_required(VERSION 3.20)
project(kiln-soundcook LANGUAGES C)
set(KILN_TP "${CMAKE_CURRENT_SOURCE_DIR}/../../../native/third_party")
add_executable(kiln-soundcook src/soundcook.c src/qoa_frameless.c src/decoders.c src/vorbis.c "${KILN_TP}/speexdsp/resample.c")
target_include_directories(kiln-soundcook PRIVATE src "${KILN_TP}")
set_source_files_properties("${KILN_TP}/speexdsp/resample.c" PROPERTIES
    COMPILE_DEFINITIONS "OUTSIDE_SPEEX;RANDOM_PREFIX=kiln_speex;FLOATING_POINT;EXPORT=")
set_source_files_properties(src/decoders.c src/vorbis.c "${KILN_TP}/speexdsp/resample.c" PROPERTIES COMPILE_OPTIONS
    "$<IF:$<C_COMPILER_ID:MSVC>,/w,-w>")
if(MSVC)
    # clang-cl: no FMA contraction, so float results are the same as the host build's
    target_compile_options(kiln-soundcook PRIVATE /O2 /clang:-ffp-contract=off)
    target_compile_definitions(kiln-soundcook PRIVATE _CRT_SECURE_NO_WARNINGS)
    set_property(TARGET kiln-soundcook PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
else()
    target_compile_options(kiln-soundcook PRIVATE -O2 -ffp-contract=off -fno-fast-math)
    target_link_libraries(kiln-soundcook PRIVATE m)
endif()
set_target_properties(kiln-soundcook PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
