kiln_service(items items.cpp engine.cpp pide.cpp moves.cpp xfer.cpp names.cpp)

# Host tests (no game): the move / ledger / rule / name logic, and the transfer hooks against a fake inventory world.
# Run under Wine: native/services/items/tests/run.sh
add_executable(items-move-test tests/move_test.cpp)
target_include_directories(items-move-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                   "${KILN_GENERATED_DIR}")
target_link_libraries(items-move-test PRIVATE kiln-safetyhook)
set_target_properties(items-move-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
add_executable(items-pide-test tests/pide_test.cpp)
target_include_directories(items-pide-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                   "${KILN_GENERATED_DIR}")
target_link_libraries(items-pide-test PRIVATE kiln-safetyhook)
set_target_properties(items-pide-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
