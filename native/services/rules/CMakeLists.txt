kiln_service(rules rules.cpp creative.cpp flight.cpp steer.cpp)
# kiln.flame (native/plugins/flame): the creative hooks stay off while Creative Mode runs
target_include_directories(kiln-rules PRIVATE "${CMAKE_SOURCE_DIR}/plugins/flame/include")
