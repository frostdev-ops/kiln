kiln_service(chat chat.cpp)

# chat-layers-test.exe: kiln.chat and kiln-emf's client hooks (both production sources) on one function in the real
# kiln.hooks v2 registry engine, under Wine, no game: native/services/chat/tests/run.sh. Output: bin/chat-tests/.
add_executable(chat-layers-test
    tests/chat_layers_test.cpp
    chat.cpp
    ../../plugins/emf/src/api_net_client.cpp
    ../../plugins/emf/src/net/net_line.cpp
    ../../core/src/services/hooks_chain.cpp
    ../../core/src/services/hooks_chain_asm.cpp
    ../../core/src/services/hooks_seh.cpp)
target_include_directories(chat-layers-test PRIVATE
    "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/core/src" "${CMAKE_SOURCE_DIR}/third_party" "${KILN_GENERATED_DIR}"
    "${CMAKE_SOURCE_DIR}/plugins/emf/include" "${CMAKE_SOURCE_DIR}/plugins/emf/src" "${CMAKE_SOURCE_DIR}/services/players")
target_link_libraries(chat-layers-test PRIVATE kiln-safetyhook)
set_target_properties(chat-layers-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/chat-tests")
