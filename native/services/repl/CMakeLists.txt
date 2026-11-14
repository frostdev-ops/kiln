kiln_service(repl repl.cpp loopback.cpp)

# Host tests (no game), run under Wine: native/services/repl/tests/run.sh
#  - repl-host-test: the ABI layout and the fault rule of the observer detours
#  - repl-gate-test: the service's own start against a stand-in KilnApi: the lab functions' gate (policy, lab port)
add_executable(repl-host-test tests/repl_host_test.cpp)
target_include_directories(repl-host-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party")
set_target_properties(repl-host-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/repl-tests")
add_executable(repl-gate-test tests/repl_gate_test.cpp repl.cpp loopback.cpp)
target_include_directories(repl-gate-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party")
set_target_properties(repl-gate-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/repl-tests")
