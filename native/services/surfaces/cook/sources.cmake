# SPDX-License-Identifier: MIT
# The portable surface cooker, shared by kiln-surfaces.dll and tools/surfacecook. No Windows, no game, no Kiln API.
# It reuses the VFX builder's texture code (PNG, mips, BC encoders, ContentHash, layout fingerprints).
set(KILN_SURFACES_COOK_DIR "${CMAKE_CURRENT_LIST_DIR}")
set(KILN_SURFACES_VFX_COOK_DIR "${CMAKE_CURRENT_LIST_DIR}/../../vfx/cook")
set(KILN_SURFACES_THIRD_PARTY "${CMAKE_CURRENT_LIST_DIR}/../../../third_party")
set(KILN_SURFACES_COOK_SOURCES
    ${KILN_SURFACES_COOK_DIR}/surfacecheck.cpp
    ${KILN_SURFACES_COOK_DIR}/tables.cpp
    ${KILN_SURFACES_COOK_DIR}/surface.cpp
    ${KILN_SURFACES_COOK_DIR}/icon.cpp
    ${KILN_SURFACES_VFX_COOK_DIR}/contenthash.cpp
    ${KILN_SURFACES_VFX_COOK_DIR}/ids.cpp
    ${KILN_SURFACES_VFX_COOK_DIR}/texture.cpp
    ${KILN_SURFACES_VFX_COOK_DIR}/fingerprint.cpp
    ${KILN_SURFACES_THIRD_PARTY}/bc7enc/bc7enc.cpp
    ${KILN_SURFACES_THIRD_PARTY}/bc7enc/bc7decomp.cpp
    ${KILN_SURFACES_THIRD_PARTY}/bc7enc/rgbcx.cpp)
set(KILN_SURFACES_COOK_INCLUDES "${KILN_SURFACES_COOK_DIR}" "${KILN_SURFACES_VFX_COOK_DIR}" "${KILN_SURFACES_THIRD_PARTY}")
# Third-party code and the stb include in texture.cpp stay quiet.
function(kiln_surfaces_cook_properties target)
    set(quiet ${KILN_SURFACES_THIRD_PARTY}/bc7enc/bc7enc.cpp ${KILN_SURFACES_THIRD_PARTY}/bc7enc/bc7decomp.cpp ${KILN_SURFACES_THIRD_PARTY}/bc7enc/rgbcx.cpp
              ${KILN_SURFACES_VFX_COOK_DIR}/texture.cpp)
    if(MSVC)
        set_source_files_properties(${quiet} TARGET_DIRECTORY ${target} PROPERTIES COMPILE_OPTIONS "/w")
    else()
        set_source_files_properties(${quiet} TARGET_DIRECTORY ${target} PROPERTIES COMPILE_OPTIONS "-w")
    endif()
endfunction()
