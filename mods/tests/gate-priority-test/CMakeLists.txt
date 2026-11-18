# SPDX-License-Identifier: MIT
# The priority kit's measurement mod (docs/kiln/priority-kit.md): bin/mods/gate-priority-test/{gate-priority-test.dll,
# native.json}, and its Windows test host for headless Wine (tests/wine.sh), built explicitly into bin/tests/: the mod
# DLL loaded over a fake Kiln whose kiln.world is the real compare-exchange code over a fake engine, with kiln.engine's
# real Windows primitives (the SEH copy and write, the Interlocked words, the gate). Never a game target or bundle file.
add_library(gate-priority-test SHARED kit.cpp)
target_include_directories(gate-priority-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi")
set_target_properties(gate-priority-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/mods/gate-priority-test")
configure_file(native.json "${CMAKE_BINARY_DIR}/bin/mods/gate-priority-test/native.json" COPYONLY)

set(GPT_ENGINE "${CMAKE_SOURCE_DIR}/services/engine")
set(GPT_WORLD "${CMAKE_SOURCE_DIR}/services/world")
add_executable(gate-priority-host EXCLUDE_FROM_ALL tests/host_win.cpp "${GPT_WORLD}/ecs_engine.cpp" "${GPT_WORLD}/ecs_lookup.cpp"
               "${GPT_WORLD}/ecs_reads.cpp" "${GPT_WORLD}/ecs_writes.cpp" "${GPT_WORLD}/ecs_write_hold.cpp"
               "${GPT_WORLD}/ecs_write_policy.cpp" "${GPT_ENGINE}/resolve.cpp" "${GPT_ENGINE}/gate.cpp"
               "${GPT_ENGINE}/memory_win.cpp")
target_include_directories(gate-priority-host PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                      "${GPT_ENGINE}" "${GPT_ENGINE}/include" "${GPT_ENGINE}/tests"
                                                      "${GPT_WORLD}" "${GPT_WORLD}/tests" "${CMAKE_CURRENT_SOURCE_DIR}")
set_target_properties(gate-priority-host PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/tests")
add_dependencies(gate-priority-host gate-priority-test)
