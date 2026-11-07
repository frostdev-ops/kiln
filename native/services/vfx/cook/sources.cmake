# SPDX-License-Identifier: MIT
# The portable builder, shared by kiln-vfx.dll and tools/vfxcook/native. No Windows, no game, no Kiln API.
set(KILN_VFX_COOK_DIR "${CMAKE_CURRENT_LIST_DIR}")
set(KILN_VFX_THIRD_PARTY "${CMAKE_CURRENT_LIST_DIR}/../../../third_party")
set(KILN_VFX_COOK_SOURCES
    ${KILN_VFX_COOK_DIR}/contenthash.cpp
    ${KILN_VFX_COOK_DIR}/ids.cpp
    ${KILN_VFX_COOK_DIR}/budget.cpp
    ${KILN_VFX_COOK_DIR}/starter.cpp
    ${KILN_VFX_COOK_DIR}/shader.cpp
    ${KILN_VFX_COOK_DIR}/define.cpp
    ${KILN_VFX_COOK_DIR}/build.cpp
    ${KILN_VFX_COOK_DIR}/texture.cpp
    ${KILN_VFX_COOK_DIR}/material.cpp
    ${KILN_VFX_COOK_DIR}/pack.cpp
    ${KILN_VFX_COOK_DIR}/fingerprint.cpp
    ${KILN_VFX_THIRD_PARTY}/bc7enc/bc7enc.cpp
    ${KILN_VFX_THIRD_PARTY}/bc7enc/rgbcx.cpp)
# A rebuild when a committed module changes; third-party code stays quiet.
function(kiln_vfx_cook_properties target)
    set_source_files_properties(${KILN_VFX_COOK_DIR}/starter.cpp PROPERTIES OBJECT_DEPENDS
        "${KILN_VFX_COOK_DIR}/../shaders/build/kiln_emitter.spv;${KILN_VFX_COOK_DIR}/../shaders/build/kiln_sprite.spv;${KILN_VFX_COOK_DIR}/../shaders/build/kiln_light.spv")
    if(MSVC)
        set_source_files_properties(${KILN_VFX_THIRD_PARTY}/bc7enc/bc7enc.cpp ${KILN_VFX_THIRD_PARTY}/bc7enc/rgbcx.cpp ${KILN_VFX_COOK_DIR}/texture.cpp PROPERTIES COMPILE_OPTIONS "/w")
    else()
        set_source_files_properties(${KILN_VFX_THIRD_PARTY}/bc7enc/bc7enc.cpp ${KILN_VFX_THIRD_PARTY}/bc7enc/rgbcx.cpp ${KILN_VFX_COOK_DIR}/texture.cpp PROPERTIES COMPILE_OPTIONS "-w")
    endif()
endfunction()
