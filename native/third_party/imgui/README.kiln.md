# Dear ImGui (vendored for kiln.ui)

- Dear ImGui **1.93.0** (https://github.com/ocornut/imgui, tag v1.93.0, MIT, `LICENSE.txt`): the core files and the
  `backends/imgui_impl_win32.*` and `backends/imgui_impl_vulkan.*` backends, unmodified.
- `vulkan-headers/`: the Khronos Vulkan C headers from Vulkan-Headers tag `vulkan-sdk-1.4.363.0`
  (`SPDX-License-Identifier: Apache-2.0 OR MIT`; used under MIT, `vulkan-headers/LICENSE-MIT.txt`). Only the C headers
  (`include/vulkan/*.h`, `include/vk_video/*.h`); the backend needs them and the xwin SDK has none.
- Kiln compiles them with `IMGUI_IMPL_VULKAN_NO_PROTOTYPES` (function pointers come from the game's own Vulkan
  device) and `IMGUI_IMPL_WIN32_DISABLE_GAMEPAD` (see `native/services/ui/CMakeLists.txt`).
