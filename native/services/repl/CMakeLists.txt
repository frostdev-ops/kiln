kiln_service(repl repl.cpp)

# Host tests (no game): the ABI layout and the fault rule of the detours. Run under Wine: native/services/repl/tests/run.sh
add_executable(repl-host-test tests/repl_host_test.cpp)
target_include_directories(repl-host-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party")
set_target_properties(repl-host-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/repl-tests")
