# SPDX-License-Identifier: MIT
# In a Kiln checkout: Kiln's own toolchain (native/cmake/clang-cl-xwin.cmake). The Kiln.Sdk package ships that file
# itself under this name (managed/Kiln.Sdk/Kiln.Sdk.csproj packs it), so the path is the same for both.
include("${CMAKE_CURRENT_LIST_DIR}/../../../../native/cmake/clang-cl-xwin.cmake")
