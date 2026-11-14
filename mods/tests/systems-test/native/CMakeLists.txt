# SPDX-License-Identifier: MIT
# systems-test's native part: kiln-systems-test.dll, a lab-only plugin (mods/tests/systems-test/native/build.sh).
# Windows x64 with clang-cl + the xwin SDK, the toolchain native/build.sh uses; headers from native/core/abi only.
cmake_minimum_required(VERSION 3.20)
project(kiln-systems-test LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(KILN_NATIVE "${CMAKE_CURRENT_SOURCE_DIR}/../../../../native")
add_compile_definitions(WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
# /EHa as in native/CMakeLists.txt: the stages that fault on purpose run under kiln.world's SEH guard
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/EHa>" /Zi)
add_link_options(/DEBUG /OPT:REF /IGNORE:4099)
add_library(kiln-systems-test SHARED systems_test.cpp)
target_include_directories(kiln-systems-test PRIVATE "${KILN_NATIVE}/core/abi")
set_target_properties(kiln-systems-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
