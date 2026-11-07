# SPDX-License-Identifier: MIT
# hookchain.exe: does a safetyhook inline/mid hook chain correctly on a function another engine hooked first?
# No game needed; run.sh drives it under plain Wine. Output: bin/hookchain/hookchain.exe.
add_executable(hookchain hookchain.cpp)
target_link_libraries(hookchain PRIVATE kiln-safetyhook)
set_target_properties(hookchain PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/hookchain")
