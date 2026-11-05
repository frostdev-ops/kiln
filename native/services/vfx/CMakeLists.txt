# kiln.vfx: play the game's VFX from code and guard against unknown GUIDs, in the game client (docs/kiln/services/vfx.md).
include(cook/sources.cmake)
kiln_service(vfx vfx.cpp core.cpp game.cpp sim.cpp boot.cpp ${KILN_VFX_COOK_SOURCES})
target_include_directories(kiln-vfx PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/cook")
kiln_vfx_cook_properties(kiln-vfx)
