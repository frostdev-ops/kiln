# kiln.ui: the client overlay (Dear ImGui 1.93.0 + its Win32 and Vulkan backends, vendored in third_party/imgui).
set(KILN_IMGUI "${CMAKE_SOURCE_DIR}/third_party/imgui")
add_library(kiln-imgui STATIC
    ${KILN_IMGUI}/imgui.cpp ${KILN_IMGUI}/imgui_draw.cpp ${KILN_IMGUI}/imgui_tables.cpp ${KILN_IMGUI}/imgui_widgets.cpp
    ${KILN_IMGUI}/backends/imgui_impl_win32.cpp ${KILN_IMGUI}/backends/imgui_impl_vulkan.cpp)
target_include_directories(kiln-imgui PUBLIC ${KILN_IMGUI} ${KILN_IMGUI}/vulkan-headers/include)
target_compile_definitions(kiln-imgui PUBLIC IMGUI_IMPL_VULKAN_NO_PROTOTYPES VK_NO_PROTOTYPES IMGUI_IMPL_WIN32_DISABLE_GAMEPAD)
target_compile_options(kiln-imgui PRIVATE -w)

kiln_service(ui ui.cpp vk.cpp input.cpp game.cpp style.cpp render.cpp wheel.cpp)
target_link_libraries(kiln-ui PRIVATE kiln-imgui user32 imm32 dwmapi gdi32)
