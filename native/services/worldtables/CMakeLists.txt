# kiln.worldtables: the game's shared building-material tables, merged at boot (docs/kiln/services/worldtables.md).
# A block's "surface" is checked with kiln.surfaces' portable reader, so a wrong one is refused alike on every side.
kiln_service(worldtables worldtables.cpp reflect.cpp merge.cpp terrain.cpp ${CMAKE_SOURCE_DIR}/services/surfaces/cook/surfacecheck.cpp)
target_include_directories(kiln-worldtables PRIVATE "${CMAKE_SOURCE_DIR}/services/surfaces/cook")
