# SPDX-License-Identifier: MIT
# kiln-enshwick.dll: hosts Enshwick's own loader (enshwick.dll, MIT, built from source by tools/build-runtime.sh) so
# Enshwick mods run unchanged under Kiln. Deployed to <game>/kiln/plugins/; the loader goes to
# <game>/kiln/plugins/enshwick/enshwick.dll. Docs: docs/kiln/engines/enshwick.md.
kiln_plugin(enshwick src/plugin.cpp)

# Host tests (tests/run.py, under plain Wine with compat's compathost.exe): a stand-in loader that records how it was
# started. Goes to bin/enshwick-tests/.
add_library(enshwick-fake-loader SHARED tests/fake_loader.cpp)
set_target_properties(enshwick-fake-loader PROPERTIES OUTPUT_NAME enshwick
                      RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/enshwick-tests")
add_library(enshwick-fake-mod SHARED tests/fake_mod.cpp)
set_target_properties(enshwick-fake-mod PROPERTIES OUTPUT_NAME fakemod
                      RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/enshwick-tests")
foreach(v STUB OTHER CLAIM)
    string(TOLOWER ${v} lv)
    set(name powrprof_${lv})
    if(v STREQUAL CLAIM)
        set(name claim)
    endif()
    add_library(enshwick-fake-${lv} SHARED tests/fake_proxy.cpp)
    target_compile_definitions(enshwick-fake-${lv} PRIVATE FAKE_${v})
    set_target_properties(enshwick-fake-${lv} PROPERTIES OUTPUT_NAME ${name}
                          RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/enshwick-tests")
    list(APPEND ENSHWICK_FAKES enshwick-fake-${lv})
endforeach()
add_dependencies(kiln-enshwick enshwick-fake-loader enshwick-fake-mod ${ENSHWICK_FAKES})

add_executable(enshwick-contract-test tests/contract_test.cpp)
target_include_directories(enshwick-contract-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party")
set_target_properties(enshwick-contract-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/enshwick-tests")
add_dependencies(kiln-enshwick enshwick-contract-test)
