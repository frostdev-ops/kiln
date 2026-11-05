# SPDX-License-Identifier: MIT
# Sample Kiln-aware native mod (docs/kiln/native-mods.md): bin/samples/native-mod/{kiln-native-mod.dll,native.json}.
add_library(kiln-native-mod SHARED mod.cpp)
target_include_directories(kiln-native-mod PRIVATE "${CMAKE_SOURCE_DIR}/core/abi")
set_target_properties(kiln-native-mod PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/samples/native-mod")
configure_file(native.json "${CMAKE_BINARY_DIR}/bin/samples/native-mod/native.json" COPYONLY)
