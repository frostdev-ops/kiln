# SPDX-License-Identifier: MIT
# The on_unload lab check's test plugins (unload_test_plugin.cpp), built on their own with the repository's toolchain
# so native/CMakeLists.txt does not change. native/core/tests/test-unload-lab.sh configures and builds this:
#   cmake -S native/core/tests/unload -B <dir> -G Ninja -DCMAKE_TOOLCHAIN_FILE=native/cmake/clang-cl-xwin.cmake \
#         -DCMAKE_BUILD_TYPE=Release
cmake_minimum_required(VERSION 3.20)
project(kiln_unload_test_plugins CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/EHa>" /W4)

# name, mode (unload_test_plugin.cpp): file-name order is load order, so the reverse order calls 6, 5, 4, 3, 2, 1
foreach(spec "unload-test-0-nohandler;2" "unload-test-1-marker;1" "unload-test-2-marker;1" "unload-test-3-fault;3"
             "unload-test-4-block;4" "unload-test-5-reexit;5" "unload-test-6-caps;6")
    list(GET spec 0 name)
    list(GET spec 1 mode)
    add_library(${name} SHARED unload_test_plugin.cpp)
    target_include_directories(${name} PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../../abi")
    target_compile_definitions(${name} PRIVATE UNLOAD_TEST_MODE=${mode} "UNLOAD_TEST_ID=\"${name}\"")
    set_target_properties(${name} PROPERTIES PREFIX "")
endforeach()
