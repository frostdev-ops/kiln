// SPDX-License-Identifier: MIT
// kiln.ui: NVIDIA NGX's parameter object (NVSDK_NGX_Parameter), called through its table of virtual functions by slot.
// The object lives in NVIDIA's NGX core (_nvngx.dll); Kiln only calls its Set and Get functions with the documented
// parameter names (graphics.cpp). No windows.h: host test tests/graphics_test.cpp (a fake object).
//
// The slots: the interface declares Set and Get overloads (unsigned long long, float, double, unsigned int, int, two
// resource pointers, void*) and Reset; MSVC lays overloads out in reverse order of declaration, so Set(void*) is slot 0.
// Read off the game's own statically linked helpers in enshrouded.exe (docs/re/client-upscale.md): SetUI calls slot 4
// (+0x20), SetI slot 3, SetF slot 6, SetULL slot 7, SetVoidPointer slot 0; GetVoidPointer slot 8 (+0x40), GetI slot 11,
// GetUI slot 12, GetD slot 13, GetF slot 14, GetULL slot 15. Each takes the object first, the name second (x64: the
// object is the first argument, as a plain function would take it).
#pragma once
#include <cstddef>
#include <cstdint>

namespace kui::ngx {

// NVSDK_NGX_Result: success is 0x1, a failure has 0xBAD in its top twelve bits.
using Result = int32_t;
inline bool ok(Result r) { return (static_cast<uint32_t>(r) & 0xFFF00000u) != 0xBAD00000u; }
inline constexpr Result kFail = static_cast<Result>(0xBAD00000u);

enum Slot : size_t {
    SetVoidPointer = 0, SetI = 3, SetUI = 4, SetD = 5, SetF = 6, SetULL = 7,
    GetVoidPointer = 8, GetI = 11, GetUI = 12, GetD = 13, GetF = 14, GetULL = 15,
};

inline void* const* table(const void* p) { return *static_cast<void* const* const*>(p); }

inline void set_ui(void* p, const char* name, uint32_t v) {
    reinterpret_cast<void (*)(void*, const char*, uint32_t)>(table(p)[SetUI])(p, name, v);
}
inline void set_i(void* p, const char* name, int32_t v) {
    reinterpret_cast<void (*)(void*, const char*, int32_t)>(table(p)[SetI])(p, name, v);
}
inline void set_ptr(void* p, const char* name, void* v) {
    reinterpret_cast<void (*)(void*, const char*, void*)>(table(p)[SetVoidPointer])(p, name, v);
}
inline Result get_ui(const void* p, const char* name, uint32_t* out) {
    return reinterpret_cast<Result (*)(const void*, const char*, uint32_t*)>(table(p)[GetUI])(p, name, out);
}
inline Result get_i(const void* p, const char* name, int32_t* out) {
    return reinterpret_cast<Result (*)(const void*, const char*, int32_t*)>(table(p)[GetI])(p, name, out);
}
inline Result get_ptr(const void* p, const char* name, void** out) {
    return reinterpret_cast<Result (*)(const void*, const char*, void**)>(table(p)[GetVoidPointer])(p, name, out);
}

// NVSDK_NGX_Feature_SuperSampling: DLSS's upscaler.
inline constexpr int32_t kFeatureSuperSampling = 1;

// The parameter names Kiln uses (nvsdk_ngx_defs.h's NVSDK_NGX_Parameter_*, as the game passes them).
inline constexpr const char* kOptimalSettingsCallback = "DLSSOptimalSettingsCallback";
inline constexpr const char* kWidth = "Width";
inline constexpr const char* kHeight = "Height";
inline constexpr const char* kOutWidth = "OutWidth";
inline constexpr const char* kOutHeight = "OutHeight";
inline constexpr const char* kPerfQualityValue = "PerfQualityValue";
inline constexpr const char* kMinWidth = "DLSS.Get.Dynamic.Min.Render.Width";
inline constexpr const char* kMinHeight = "DLSS.Get.Dynamic.Min.Render.Height";
inline constexpr const char* kMaxWidth = "DLSS.Get.Dynamic.Max.Render.Width";
inline constexpr const char* kMaxHeight = "DLSS.Get.Dynamic.Max.Render.Height";
inline constexpr const char* kSubrectWidth = "DLSS.Render.Subrect.Dimensions.Width";
inline constexpr const char* kSubrectHeight = "DLSS.Render.Subrect.Dimensions.Height";
// The render preset hints Kiln sets: the four the game sets itself before every creation besides DLAA's (docs/re/
// client-upscale.md), so the game's own value comes back at the next creation once the player goes back to "default".
// DLAA's is left as the game sets it; Ultra Quality's the game neither sets nor offers.
inline constexpr const char* kPresetHints[] = {
    "DLSS.Hint.Render.Preset.Quality", "DLSS.Hint.Render.Preset.Balanced", "DLSS.Hint.Render.Preset.Performance",
    "DLSS.Hint.Render.Preset.UltraPerformance",
};
inline constexpr const char* kPresetHintDlaa = "DLSS.Hint.Render.Preset.DLAA";
// The hint the game's mode reads (PerfQualityValue -> name); null for DLAA and unknown values.
inline const char* preset_hint_for(int32_t quality) {
    switch (quality) {
        case 0: return "DLSS.Hint.Render.Preset.Performance";
        case 1: return "DLSS.Hint.Render.Preset.Balanced";
        case 2: return "DLSS.Hint.Render.Preset.Quality";
        case 3: return "DLSS.Hint.Render.Preset.UltraPerformance";
        case 4: return "DLSS.Hint.Render.Preset.UltraQuality";
        default: return nullptr;
    }
}

// The optimal-settings callback DLSS hands out in the capability parameters.
using OptimalSettingsFn = Result (*)(void* params);

}  // namespace kui::ngx
