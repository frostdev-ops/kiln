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
