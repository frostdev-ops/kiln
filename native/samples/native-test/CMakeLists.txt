# SPDX-License-Identifier: MIT
# Test DLLs for mods/tests/native-test (stage.sh copies them under per-case names):
#   native-test.dll   Kiln-aware: kiln_native_init logs one PASS/FAIL line per check; its behaviour follows the
#                     mod id's suffix (-fail returns 1, -unsupported returns 2, -fault faults)
#   native-plain.dll  a plain DLL mod: no init, logs from DllMain to <dir>/native-plain.log
#   native-plugin.dll a kiln_plugin_main plugin (profile "plugin" entry check)
set(NT_OUT "${CMAKE_BINARY_DIR}/bin/samples/native-test")
add_library(kiln-native-test SHARED test.cpp)
target_include_directories(kiln-native-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi")
set_target_properties(kiln-native-test PROPERTIES OUTPUT_NAME native-test RUNTIME_OUTPUT_DIRECTORY "${NT_OUT}")
add_library(kiln-native-plain SHARED plain.cpp)
set_target_properties(kiln-native-plain PROPERTIES OUTPUT_NAME native-plain RUNTIME_OUTPUT_DIRECTORY "${NT_OUT}")
add_library(kiln-native-plugin SHARED plugin.cpp)
target_include_directories(kiln-native-plugin PRIVATE "${CMAKE_SOURCE_DIR}/core/abi")
set_target_properties(kiln-native-plugin PROPERTIES OUTPUT_NAME native-plugin RUNTIME_OUTPUT_DIRECTORY "${NT_OUT}")
