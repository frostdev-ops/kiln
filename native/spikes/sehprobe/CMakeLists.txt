# SPDX-License-Identifier: MIT
# SEH probe (run.sh drives it under Wine): the same source built with synchronous (/EHsc) and asynchronous (/EHa,
# clang -fasync-exceptions) exception handling, independent of the project's global /EH flag. Output: bin/sehprobe/.
set(SP_OUT "${CMAKE_BINARY_DIR}/bin/sehprobe")
foreach(mode EHsc EHa)
    string(TOLOWER ${mode} lower)
    add_executable(sehprobe-${lower} probe.cpp)
    # Drop the project-wide /EH option and set this variant's own (clang-cl: the last /EH option wins).
    get_target_property(opts sehprobe-${lower} COMPILE_OPTIONS)
    list(FILTER opts EXCLUDE REGEX "/EH")
    set_property(TARGET sehprobe-${lower} PROPERTY COMPILE_OPTIONS ${opts} /${mode})
    target_compile_definitions(sehprobe-${lower} PRIVATE PROBE_MODE="${mode}")
    set_target_properties(sehprobe-${lower} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${SP_OUT}")
endforeach()
# Production tick fault recovery must stop delivery, skip an incomplete Post and keep calling the game.
target_sources(sehprobe-eha PRIVATE tick_cases.cpp lua_case.cpp)
target_include_directories(sehprobe-eha PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${KILN_GENERATED_DIR}")
target_link_libraries(sehprobe-eha PRIVATE kiln-safetyhook)
target_compile_definitions(sehprobe-eha PRIVATE PROBE_TICK_CASES)
# Reproduce kiln-set's mixed-mode boundary with the real Lua runtime, not a stand-in catch(...).
file(GLOB PROBE_LUA_SOURCES CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/third_party/lua/*.c")
set_source_files_properties(${PROBE_LUA_SOURCES} PROPERTIES LANGUAGE CXX COMPILE_OPTIONS /EHsc)
add_library(sehprobe-lua STATIC ${PROBE_LUA_SOURCES})
target_include_directories(sehprobe-lua PUBLIC "${CMAKE_SOURCE_DIR}/third_party/lua")
target_link_libraries(sehprobe-eha PRIVATE sehprobe-lua)
