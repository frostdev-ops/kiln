# kiln.surfaces: building-material surfaces in the client's tables, cooked from mods' PNGs at boot (docs/kiln/services/surfaces.md).
include(cook/sources.cmake)
kiln_service(surfaces surfaces.cpp ${KILN_SURFACES_COOK_SOURCES})
target_include_directories(kiln-surfaces PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" ${KILN_SURFACES_COOK_INCLUDES})
kiln_surfaces_cook_properties(kiln-surfaces)
