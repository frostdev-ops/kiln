#pragma once
#include "overlay_guard.h"
namespace kui {
bool overlay_guard_start() noexcept; // choose mode and prepare guard; true: drawing off, keep non-Vulkan services
void overlay_guard_stage(overlay_guard::Stage stage) noexcept;
// Right before each overlay draw attempt (render thread; after the first, one atomic read): the first attempt of the
// process stops the idle timer from completing the start, and arms the record again when the timer already counted it
// as fine (overlay_guard::rearm).
void overlay_guard_first_draw() noexcept;
// The process exits normally with code 0 (kiln.ui's on_unload): a start still trying did not die at the overlay.
void overlay_guard_exit() noexcept;
// What overlay_guard_start found next to the game: OptiScaler (by its DLL's contents) and NVIDIA Streamline
// (sl.interposer.dll in the game folder, Streamline/ or loaded). Kiln's graphics tools step aside for both (graphics.cpp).
void overlay_guard_tools(bool* optiscaler, bool* streamline) noexcept;
}
