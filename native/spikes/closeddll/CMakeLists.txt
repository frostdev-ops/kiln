# SPDX-License-Identifier: MIT
# compat_test.exe: the compatibility rules and the overlap report (core/src/compat_rules.cpp, compat_overlaps.cpp) run under
# Wine against schema/fixtures/compat-rules/cases.json, no game: native/core/tests/test-compat.sh.
add_executable(compat_test
    ../../core/tests/compat_test.cpp
    ../../core/src/compat_rules.cpp
    ../../core/src/compat_overlaps.cpp)
target_include_directories(compat_test PRIVATE ../../core/src ../../core/abi ../../third_party)
target_link_libraries(compat_test PRIVATE user32)
set_target_properties(compat_test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/closeddll")

# kiln-labprobe.dll: a lab plugin that puts a kiln.hooks v2 link on a sigdb symbol (lab_probe.cpp). Not deployed.
add_library(lab_probe SHARED lab_probe.cpp)
target_include_directories(lab_probe PRIVATE ../../core/abi)
set_target_properties(lab_probe PROPERTIES OUTPUT_NAME kiln-labprobe RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/closeddll")
