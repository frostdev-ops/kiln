# SPDX-License-Identifier: MIT
# KilnNative1: a Kiln-aware native mod (a Windows x64 DLL; docs/kiln/native-mods.md). Kiln loads it at phase "boot"
# from <game>/kiln/native/kiln-mod-id/ and calls kiln_native_init (native.json).
#   kiln build && kiln deploy --server <folder>         (or by hand, on Linux:)
#   cmake -S . -B build -G Ninja -DKILN_SDK_DIR=<sdk>/native -DCMAKE_TOOLCHAIN_FILE=<sdk>/native/cmake/clang-cl-xwin.cmake \
#         -DCMAKE_BUILD_TYPE=Release -DKILN_SERVER=<game or server folder with Kiln>
#   cmake --build build && cmake --build build --target deploy
# <sdk> = the Kiln.Sdk package (~/.nuget/packages/kiln.sdk/KILN_SDK_VERSION) or <kiln repo>/managed/Kiln.Sdk.
# On Windows: the same with the Visual Studio generator (or Ninja from a VS developer prompt), no toolchain file.
cmake_minimum_required(VERSION 3.24)
project(KilnNative1 LANGUAGES C CXX)

set(KILN_SDK_DIR "$ENV{KILN_SDK_DIR}" CACHE PATH "Kiln native SDK: the Kiln.Sdk package's native/ folder")
if(NOT KILN_SDK_DIR AND EXISTS "$ENV{HOME}/.nuget/packages/kiln.sdk/KILN_SDK_VERSION/native")
    set(KILN_SDK_DIR "$ENV{HOME}/.nuget/packages/kiln.sdk/KILN_SDK_VERSION/native" CACHE PATH "" FORCE)
endif()
if(NOT EXISTS "${KILN_SDK_DIR}/cmake/KilnPlugin.cmake")
    message(FATAL_ERROR "Set KILN_SDK_DIR to the Kiln.Sdk package's native/ folder (kiln build does it)")
endif()
include("${KILN_SDK_DIR}/cmake/KilnPlugin.cmake")

kiln_plugin(KilnNative1 mod.cpp NATIVE_JSON "${CMAKE_CURRENT_SOURCE_DIR}/native.json")
