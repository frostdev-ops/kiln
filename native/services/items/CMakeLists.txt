kiln_service(items items.cpp engine.cpp pide.cpp moves.cpp xfer.cpp names.cpp stamps.cpp instances.cpp equip.cpp wear.cpp toolwear.cpp toolwear_table.cpp)

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
add_executable(items-results-test tests/completed_results_test.cpp)
set_target_properties(items-results-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
add_executable(items-instances-test tests/instances_test.cpp)
target_include_directories(items-instances-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                        "${KILN_GENERATED_DIR}")
set_target_properties(items-instances-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
add_executable(items-equip-test tests/equip_test.cpp)
target_include_directories(items-equip-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                    "${KILN_GENERATED_DIR}")
set_target_properties(items-equip-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
add_executable(items-wear-test tests/wear_test.cpp)
set_target_properties(items-wear-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
add_executable(items-toolwear-test tests/toolwear_test.cpp stamps.cpp)
set_target_properties(items-toolwear-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
add_executable(items-toolwear-table-test tests/toolwear_table_test.cpp toolwear_table.cpp)
target_include_directories(items-toolwear-table-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party"
                                                             "${KILN_GENERATED_DIR}")
set_target_properties(items-toolwear-table-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/items-tests")
