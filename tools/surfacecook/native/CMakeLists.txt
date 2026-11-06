# SPDX-License-Identifier: MIT
# kiln-surfacecook: the surface cooker of kiln-surfaces.dll as a command line, for offline tests (docs/kiln/services/surfaces.md).
# Host build:     tools/surfacecook/native/build.sh            -> $KILN_OUT/surfacecook/bin/kiln-surfacecook
# Windows build:  tools/surfacecook/native/build.sh --windows  (clang-cl + xwin, native/cmake/clang-cl-xwin.cmake)
# Third-party code (native/third_party): nlohmann/json (MIT), stb_image (MIT/public domain), bc7enc (MIT/Unlicense). No game code or data.
cmake_minimum_required(VERSION 3.20)
project(kiln-surfacecook LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(KILN_NATIVE "${CMAKE_CURRENT_SOURCE_DIR}/../../../native")
include("${KILN_NATIVE}/services/surfaces/cook/sources.cmake")
add_executable(kiln-surfacecook src/main.cpp ${KILN_SURFACES_COOK_SOURCES} "${KILN_NATIVE}/services/worldtables/merge.cpp" "${KILN_NATIVE}/services/worldtables/terrain.cpp")
target_include_directories(kiln-surfacecook PRIVATE ${KILN_SURFACES_COOK_INCLUDES} "${KILN_NATIVE}/services/worldtables")
kiln_surfaces_cook_properties(kiln-surfacecook)
if(MSVC)
    target_compile_options(kiln-surfacecook PRIVATE /O2 /EHsc /clang:-ffp-contract=off)
    target_compile_definitions(kiln-surfacecook PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
    set_property(TARGET kiln-surfacecook PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
else()
    target_compile_options(kiln-surfacecook PRIVATE -O2 -ffp-contract=off -fno-fast-math -Wall -Wextra)
    find_package(Threads REQUIRED)
    target_link_libraries(kiln-surfacecook PRIVATE Threads::Threads)
endif()
set_target_properties(kiln-surfacecook PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
