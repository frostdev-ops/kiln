// SPDX-License-Identifier: MIT
// kiln.screen's effect list: who plays, how far along each is, and what that makes for the shader this frame. No Vulkan and no Windows, so a
// host test runs it (tests/test_core.cpp).
#pragma once
#include <cstdint>
#include <mutex>
#include <vector>

#include "services/kiln_screen.h"

namespace kscreen {

// shaders/screen.hlsl's Frame constant buffer. Layout checked by tests/test_gpu.py against the SPIR-V.
struct GpuEffect {
    float grade[4];
    float balance[4];
    float tint[4];
    float vignette[4];
    float shape[4];
    float fill[4];
};
struct GpuFrame {
    float resolution[2];
    uint32_t count;
    uint32_t transfer;
    GpuEffect effects[KILN_SCREEN_MAX_EFFECTS];
};
static_assert(sizeof(GpuEffect) == 96 && sizeof(GpuFrame) == 16 + 96 * KILN_SCREEN_MAX_EFFECTS);

// The effect's numbers made safe: NaN to the field's default, the rest into its range.
KilnScreenEffect sanitize(const KilnScreenEffect& raw);
// One effect at full strength, for the shader.
GpuEffect to_gpu(const KilnScreenEffect& e, float weight);
float srgb_to_linear(float v);

class Mixer {
public:
    // 0: already KILN_SCREEN_MAX_EFFECTS playing.
    uint32_t play(KilnOwner owner, const KilnScreenEffect& effect);
    bool stop(uint32_t handle, bool immediate);
    bool set_strength(uint32_t handle, float strength);
    bool playing(uint32_t handle) const;
    uint32_t stop_owner(KilnOwner owner, bool immediate);
    void clear();
    uint32_t count() const;

    // The render thread, once a frame. Moves every effect to `now` (seconds, any clock that only goes up), drops those that ended, and fills
    // frame.count and frame.effects. An effect's clock starts at the first compose that sees it, so one that waits for the pipeline to be
    // built still plays whole. Returns frame.count: 0 can also mean only that everything is faded out this instant.
    uint32_t compose(double now, GpuFrame& frame);

private:
    struct Live {
        uint32_t handle;
        KilnOwner owner;
        KilnScreenEffect e;
        bool started = false;
        double start = 0;
        bool stopping = false;      // asked to stop, and compose has not yet seen it
        bool fading = false;
        double fade_start = 0;
        float fade_from = 0;
    };
    static float level(Live& l, double now, bool* done);

    mutable std::mutex mu_;
    std::vector<Live> live_;  // in the order they run
    uint32_t next_ = 1;
};

}  // namespace kscreen
