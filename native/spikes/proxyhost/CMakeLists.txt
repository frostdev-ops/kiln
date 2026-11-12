# SPDX-License-Identifier: MIT
# Proxy test harness (no game needed; run.sh drives it under plain Wine):
#   proxyhost.exe            imports DINPUT8!DirectInput8Create like the game; its entry point is the CRT's, so the
#                            proxy's gate (and a test DLL's entry patch) patch it exactly as they patch the game's
#   kiln-core.dll (fake)     exports kiln_boot and writes the KilnBootInfo it receives to the harness log
#   test DLLs                one source (testdll.cpp) built under several names: plain chained DLLs, two that patch
#                            the exe's entry point the way EMF's loader holds a server's start, one whose DllMain
#                            fails, one that carries ShroudForge's marker text
# Everything goes to bin/proxyhost/.
set(PH_OUT "${CMAKE_BINARY_DIR}/bin/proxyhost")

add_executable(proxyhost proxyhost.cpp)
target_include_directories(proxyhost PRIVATE "${CMAKE_SOURCE_DIR}/core/abi")
target_link_libraries(proxyhost PRIVATE dinput8)
set_target_properties(proxyhost PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${PH_OUT}")

add_library(proxyhost-fakecore SHARED fakecore.cpp)
target_include_directories(proxyhost-fakecore PRIVATE "${CMAKE_SOURCE_DIR}/core/abi")
set_target_properties(proxyhost-fakecore PROPERTIES OUTPUT_NAME kiln-core RUNTIME_OUTPUT_DIRECTORY "${PH_OUT}")

function(proxyhost_dll name)
    add_library(proxyhost-${name} SHARED testdll.cpp)
    target_compile_definitions(proxyhost-${name} PRIVATE TEST_NAME=L"${name}" ${ARGN})
    set_target_properties(proxyhost-${name} PROPERTIES OUTPUT_NAME ${name} RUNTIME_OUTPUT_DIRECTORY "${PH_OUT}")
    add_dependencies(proxyhost proxyhost-${name})
endfunction()
proxyhost_dll(chain-a)
proxyhost_dll(chain-b)
proxyhost_dll(chain-c)
proxyhost_dll(chain-d)
proxyhost_dll(emf-x)
proxyhost_dll(emf-y)
proxyhost_dll(entrypatch-1 TEST_ENTRY_PATCH)
proxyhost_dll(entrypatch-2 TEST_ENTRY_PATCH)
# EMF's loader, as the proxy recognises it (exports): one that starts mods\emf-x.dll itself, one that holds the start
proxyhost_dll(emf-loader TEST_EMF_EXPORTS TEST_LOADS_EMF_X)
proxyhost_dll(emf-loader-patch TEST_EMF_EXPORTS TEST_ENTRY_PATCH)
proxyhost_dll(failing TEST_FAIL)
# patches the entry point, then fails its DllMain: Windows unloads it and the patch points into freed memory
proxyhost_dll(entrypatch-fail TEST_ENTRY_PATCH TEST_FAIL)
proxyhost_dll(fake-sf TEST_SHROUDFORGE)
proxyhost_dll(fake-emf05 TEST_EMF05)
proxyhost_dll(fake-emf-exports TEST_EMF_EXPORTS)
proxyhost_dll(fake-emf-wide TEST_EMF_WIDE)
proxyhost_dll(fake-kiln-exports TEST_KILN_EXPORTS)
proxyhost_dll(addon-log TEST_APPENDS_ADDON_LOG)
# asks for the module called dinput8.dll in its DllMain, as XHL-Core 2.x does to pick its role
proxyhost_dll(dinput8-probe TEST_DINPUT8_PROBE)
add_dependencies(proxyhost proxyhost-fakecore)

# The core's semver port against schema/fixtures/semver (run.py runs it as scenario "semver").
add_executable(proxyhost-semver semvertest.cpp)
target_include_directories(proxyhost-semver PRIVATE "${CMAKE_SOURCE_DIR}/core/src" "${CMAKE_SOURCE_DIR}/third_party")
set_target_properties(proxyhost-semver PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${PH_OUT}")
add_dependencies(proxyhost proxyhost-semver)
