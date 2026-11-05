# SPDX-License-Identifier: MIT
# CMake toolchain: build Windows x64 binaries on Linux with clang-cl, lld-link and llvm-rc against the MSVC CRT and
# Windows SDK that cargo-xwin downloads (override with -DXWIN_ROOT=... or $XWIN_ROOT).
#   cmake -S . -B build/win -G Ninja -DCMAKE_TOOLCHAIN_FILE=native/cmake/clang-cl-xwin.cmake -DCMAKE_BUILD_TYPE=Release
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_VERSION 10.0)
set(CMAKE_SYSTEM_PROCESSOR AMD64)

if(NOT XWIN_ROOT)
    if(DEFINED ENV{XWIN_ROOT})
        set(XWIN_ROOT "$ENV{XWIN_ROOT}")
    else()
        set(XWIN_ROOT "$ENV{HOME}/.cache/cargo-xwin/xwin")
    endif()
endif()
if(NOT EXISTS "${XWIN_ROOT}/sdk/include/um/windows.h")
    message(FATAL_ERROR "No Windows SDK under XWIN_ROOT=${XWIN_ROOT}; run `cargo xwin env` once to download it")
endif()
set(XWIN_ROOT "${XWIN_ROOT}" CACHE PATH "xwin MSVC CRT + Windows SDK root")
list(APPEND CMAKE_TRY_COMPILE_PLATFORM_VARIABLES XWIN_ROOT)

# xwin ships only the release CRT: compile checks run in Release, and the default runtime is static /MT.
set(CMAKE_TRY_COMPILE_CONFIGURATION Release)
if(NOT DEFINED CMAKE_MSVC_RUNTIME_LIBRARY)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()
list(APPEND CMAKE_TRY_COMPILE_PLATFORM_VARIABLES CMAKE_MSVC_RUNTIME_LIBRARY)

set(CMAKE_C_COMPILER clang-cl)
set(CMAKE_CXX_COMPILER clang-cl)
set(CMAKE_RC_COMPILER llvm-rc)
set(CMAKE_LINKER lld-link)
set(CMAKE_AR llvm-lib)
set(CMAKE_MT llvm-mt)
set(CMAKE_C_COMPILER_TARGET x86_64-pc-windows-msvc)
set(CMAKE_CXX_COMPILER_TARGET x86_64-pc-windows-msvc)

set(_xwin_includes "${XWIN_ROOT}/crt/include" "${XWIN_ROOT}/sdk/include/ucrt"
    "${XWIN_ROOT}/sdk/include/um" "${XWIN_ROOT}/sdk/include/shared" "${XWIN_ROOT}/sdk/include/winrt")
set(_xwin_flags "")
set(_rc_flags "")
foreach(dir IN LISTS _xwin_includes)
    string(APPEND _xwin_flags " /imsvc \"${dir}\"")
    string(APPEND _rc_flags " -I \"${dir}\"")
endforeach()
set(CMAKE_C_FLAGS_INIT "${_xwin_flags}")
set(CMAKE_CXX_FLAGS_INIT "${_xwin_flags}")
set(CMAKE_RC_FLAGS_INIT "${_rc_flags}")

set(_xwin_libs "/libpath:\"${XWIN_ROOT}/crt/lib/x86_64\" /libpath:\"${XWIN_ROOT}/sdk/lib/um/x86_64\" /libpath:\"${XWIN_ROOT}/sdk/lib/ucrt/x86_64\"")
set(CMAKE_EXE_LINKER_FLAGS_INIT "${_xwin_libs}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_xwin_libs}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_xwin_libs}")

set(CMAKE_FIND_ROOT_PATH "${XWIN_ROOT}")
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
