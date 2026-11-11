# SPDX-License-Identifier: MIT
# kiln-shroudforge.dll: hosts the player's installed ShroudForge runtime (github.com/bonsaibauer/shroudforge, MIT) on
# Kiln, so ShroudForge's Lua mods load unchanged. Deployed to <game>/kiln/plugins/. Links no ShroudForge code: it loads
# <game>/shroudforge/shroudforge-runtime.dll and kfc-runtime.dll through their exported C functions.
# Docs: docs/kiln/shroudforge-compat.md.
kiln_plugin(shroudforge
    src/plugin.cpp
    src/host_hooks.cpp
    src/key_feed.cpp
    src/processes.cpp)

# sf_host_chain_test.exe: the adapter's host table (src/host_hooks.cpp) against the real kiln.hooks registry, with a foreign
# E9 jump on one of ShroudForge's places first. Wine, no game: tests/test-host-chain.sh. Not deployed.
add_executable(sf_host_chain_test
    tests/host_chain_test.cpp
    src/host_hooks.cpp
    ../../core/src/services/hooks.cpp
    ../../core/src/services/hooks_chain.cpp
    ../../core/src/services/hooks_chain_asm.cpp
    ../../core/src/services/hooks_seh.cpp)
target_include_directories(sf_host_chain_test PRIVATE src ../../core/abi ../../core/src ../../third_party "${KILN_GENERATED_DIR}")
target_link_libraries(sf_host_chain_test PRIVATE kiln-safetyhook)
set_target_properties(sf_host_chain_test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/sfhost")
