// SPDX-License-Identifier: MIT
// The float widget shared by the service table and the headless ImGui input test.
#pragma once
#include "imgui.h"

namespace kui {
inline bool slider_float(const char* label, float* value, float min, float max) {
    // Significant digits keep ordinary values readable and tiny values visible. Display formatting must not
    // quantize the selected value (the default %.3f can turn an entire small positive range into zero).
    return ImGui::SliderFloat(label, value, min, max, "%.7g", ImGuiSliderFlags_NoRoundToFormat);
}
}
