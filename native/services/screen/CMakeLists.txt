# kiln.screen: full-screen effects at present, on kiln.ui's pre-overlay callback (docs/kiln/services/screen.md). The shaders are embedded as
# committed (shaders/build/*.spv): building Kiln needs no shader compiler.
kiln_service(screen screen.cpp core.cpp pass.cpp)
target_include_directories(kiln-screen PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}" "${CMAKE_SOURCE_DIR}/third_party/imgui/vulkan-headers/include")
set_source_files_properties(pass.cpp PROPERTIES OBJECT_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/shaders/build/screen_vs.spv;${CMAKE_CURRENT_SOURCE_DIR}/shaders/build/screen_fs.spv")
