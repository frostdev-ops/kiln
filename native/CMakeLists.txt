# SPDX-License-Identifier: MIT
# Kiln native: the dinput8 proxy, kiln-core.dll, samples and spikes. Windows x64 only; on Linux configure with
#   cmake -S native -B native/build/cmake -G Ninja -DCMAKE_TOOLCHAIN_FILE=native/cmake/clang-cl-xwin.cmake
# (native/build.sh does that). This project contains no GPL code: it must not link anything from kfc-parser.
cmake_minimum_required(VERSION 3.24)
project(kiln-native LANGUAGES C CXX)
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Kiln native targets Windows x64; use cmake/clang-cl-xwin.cmake to cross-build on Linux")
endif()

# Kiln's version: one line in the repository's VERSION file -> generated/kiln_version.h (KILN_VERSION, ...).
set(KILN_VERSION_FILE "${CMAKE_SOURCE_DIR}/../VERSION")
file(STRINGS "${KILN_VERSION_FILE}" KILN_VERSION LIMIT_COUNT 1)
string(STRIP "${KILN_VERSION}" KILN_VERSION)
if(NOT KILN_VERSION MATCHES "^([0-9]+)\\.([0-9]+)\\.([0-9]+)(-([0-9A-Za-z.-]+))?(\\+[0-9A-Za-z.-]+)?$")
    message(FATAL_ERROR "VERSION must hold a semantic version, found '${KILN_VERSION}'")
endif()
set(KILN_VERSION_MAJOR ${CMAKE_MATCH_1})
set(KILN_VERSION_MINOR ${CMAKE_MATCH_2})
set(KILN_VERSION_PATCH ${CMAKE_MATCH_3})
set(KILN_VERSION_PRERELEASE "${CMAKE_MATCH_5}")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${KILN_VERSION_FILE}")
set(KILN_GENERATED_DIR "${CMAKE_BINARY_DIR}/generated")
configure_file(cmake/kiln_version.h.in "${KILN_GENERATED_DIR}/kiln_version.h" @ONLY)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
add_compile_definitions(WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS UNICODE _UNICODE)
# /EHa (clang: -fasync-exceptions), not /EHsc: with /EHsc clang drops a __try whose body has no call that can throw,
# so a fault inlined in it (a memcpy, a dereference) crashes the process (native/spikes/sehprobe). /EHa also makes
# catch(...) catch hardware faults. It must come after CMake's default /EHsc in CMAKE_CXX_FLAGS: the last /EH wins.
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/EHa>" /Zi)
add_link_options(/DEBUG /OPT:REF /OPT:ICF /IGNORE:4099)

add_library(kiln-safetyhook STATIC third_party/safetyhook/safetyhook.cpp third_party/safetyhook/Zydis.c)
target_include_directories(kiln-safetyhook PUBLIC third_party/safetyhook)
target_compile_definitions(kiln-safetyhook PUBLIC ZYDIS_STATIC_BUILD ZYCORE_STATIC_BUILD)
target_compile_options(kiln-safetyhook PRIVATE -w)

add_library(kiln-proxy SHARED proxy/dinput8.cpp)
set_target_properties(kiln-proxy PROPERTIES OUTPUT_NAME dinput8)
target_include_directories(kiln-proxy PRIVATE "${KILN_GENERATED_DIR}")
target_link_libraries(kiln-proxy PRIVATE user32)

add_library(kiln-core SHARED
    core/src/api.cpp
    core/src/boot.cpp
    core/src/clr.cpp
    core/src/common.cpp
    core/src/config.cpp
    core/src/crash.cpp
    core/src/guard.cpp
    core/src/image.cpp
    core/src/log.cpp
    core/src/registry.cpp
    core/src/sigdb.cpp
    core/src/tick.cpp
    core/src/worldinfo.cpp)
# Services: every core/src/services/*.cpp is part of the core (one file or prefix per service; see service.h).
file(GLOB KILN_SERVICE_SOURCES CONFIGURE_DEPENDS core/src/services/*.cpp)
target_sources(kiln-core PRIVATE ${KILN_SERVICE_SOURCES})
# Native mod loader and profiles.
target_sources(kiln-core PRIVATE core/src/native_mods.cpp core/src/profile.cpp)
target_sources(kiln-core PRIVATE core/src/early_plugins.cpp)  # plugins exporting kiln_plugin_early
target_sources(kiln-core PRIVATE core/src/compat.cpp core/src/compat_overlaps.cpp core/src/compat_rules.cpp)  # closed-DLL report and rules
target_include_directories(kiln-core PRIVATE core/abi core/src third_party "${KILN_GENERATED_DIR}")
target_link_libraries(kiln-core PRIVATE kiln-safetyhook bcrypt)

# Service modules: native/services/<name>/CMakeLists.txt, each calling kiln_service(<name> <sources...>) to build
# bin/services/kiln-<name>.dll. One directory per service, so services build and fail independently.
function(kiln_service name)
    add_library(kiln-${name} SHARED ${ARGN})
    target_include_directories(kiln-${name} PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                    "${KILN_GENERATED_DIR}")
    target_link_libraries(kiln-${name} PRIVATE kiln-safetyhook)
    set_target_properties(kiln-${name} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/services")
endfunction()
file(GLOB KILN_SERVICE_DIRS LIST_DIRECTORIES true CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/services/*")
foreach(dir ${KILN_SERVICE_DIRS})
    if(EXISTS "${dir}/CMakeLists.txt")
        add_subdirectory("${dir}")
    endif()
endforeach()

# Plugin modules shipped with Kiln: native/plugins/<name>/CMakeLists.txt, each calling kiln_plugin(<name> <sources...>)
# to build bin/plugins/kiln-<name>.dll (deployed to <game>/kiln/plugins/; they export kiln_plugin_main).
function(kiln_plugin name)
    add_library(kiln-${name} SHARED ${ARGN})
    target_include_directories(kiln-${name} PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                    "${KILN_GENERATED_DIR}")
    target_link_libraries(kiln-${name} PRIVATE kiln-safetyhook)
    set_target_properties(kiln-${name} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/plugins")
endfunction()
file(GLOB KILN_PLUGIN_DIRS LIST_DIRECTORIES true CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/plugins/*")
foreach(dir ${KILN_PLUGIN_DIRS})
    if(EXISTS "${dir}/CMakeLists.txt")
        add_subdirectory("${dir}")
    endif()
endforeach()

add_library(kiln-native-hello SHARED samples/native-hello/plugin.cpp)
target_include_directories(kiln-native-hello PRIVATE core/abi)

add_library(kiln-tickprobe SHARED spikes/tickprobe/tickprobe.cpp)
target_link_libraries(kiln-tickprobe PRIVATE kiln-safetyhook)

# Samples and spikes with their own CMakeLists.txt (native/samples/<name>/, native/spikes/<name>/): test harnesses,
# test DLLs and sample native mods. native-hello and tickprobe have none and stay listed above.
file(GLOB KILN_EXTRA_DIRS LIST_DIRECTORIES true CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/samples/*" "${CMAKE_SOURCE_DIR}/spikes/*")
foreach(dir ${KILN_EXTRA_DIRS})
    if(EXISTS "${dir}/CMakeLists.txt")
        add_subdirectory("${dir}")
    endif()
endforeach()
