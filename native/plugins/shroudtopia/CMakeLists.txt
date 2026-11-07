# SPDX-License-Identifier: MIT
# kiln-shroudtopia.dll: loads unmodified Shroudtopia mods (C++ ModContext API, Shroudtopia 0.1.1 / 1.0 r1) on Kiln.
# include/shroudtopia/shroudtopia.h is Shroudtopia's own header (MIT, Miguel Oppermann; licenses/Shroudtopia-LICENSE.txt).
# The mods pass std::string and std::function across the DLL boundary, so this plugin must use the MSVC STL in
# release layout (_ITERATOR_DEBUG_LEVEL 0), as the xwin toolchain does. The static CRT is fine: since the Universal
# CRT, every CRT copy in a process allocates from the process heap, so a string freed on either side is safe.
kiln_plugin(shroudtopia
    src/plugin.cpp
    src/context.cpp)
target_include_directories(kiln-shroudtopia PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(kiln-shroudtopia PRIVATE _ITERATOR_DEBUG_LEVEL=0)
