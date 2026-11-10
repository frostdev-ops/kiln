# SPDX-License-Identifier: MIT
# progress_probe.exe: the kiln.progress service (core/src/services/progress.cpp) with a few host stubs, run under Wine on a
# virtual display: native/core/tests/test-progress-window.sh. Output: bin/progressprobe/progress_probe.exe.
add_executable(progress_probe
    probe.cpp
    ../../core/src/services/progress.cpp)
target_include_directories(progress_probe PRIVATE ../../core/abi ../../core/src ../../third_party "${KILN_GENERATED_DIR}")
target_link_libraries(progress_probe PRIVATE user32 gdi32)
set_target_properties(progress_probe PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/progressprobe")
