// SPDX-License-Identifier: MIT
// kiln.screen's Vulkan work inside one present: copy the frame, draw over it, chain into the present through a semaphore. No Windows here, so
// the same code runs under tests/screengpu.cpp on any Vulkan device, with validation layers on.
//
// The promise: a present never waits for this. Everything that can take long (the pipeline, memory) is built on a thread of its own; until it
// is there frames pass through untouched. A frame whose last use isn't done yet is left alone after a wait of at most 2 ms. Any failure turns
// the pass off with one log line and leaves the game's semaphores unused, so the present goes on as the game asked.
#pragma once
#include <cstdint>
#include <memory>
#include <string>

#include "core.h"
#include "services/kiln_ui.h"

namespace kscreen {

using LogFn = void (*)(int level, const char* text);

struct PassStatus {
    bool off = false;
    bool ready = false;
    uint32_t transfer = 0;
    uint32_t format = 0, width = 0, height = 0;
    uint64_t frames = 0, skipped = 0;
    double pipeline_ms = 0;
    std::string reason;
};

class Pass {
public:
    explicit Pass(LogFn log);
    ~Pass();

    // Render thread, never waits. True: this swapchain can be drawn to now. False: not yet (it starts the build), or it is off.
    bool prepare(const KilnUiPreOverlay& p);

    // Render thread, after prepare said true. True: the work is submitted, it waited on p.wait_semaphores, and *out_semaphore is what the
    // present waits on instead. False: nothing was submitted, and the game's semaphores are untouched. frame.resolution and frame.transfer are
    // filled here.
    bool present(const KilnUiPreOverlay& p, GpuFrame& frame, void** out_semaphore);

    // What a swapchain or (swapchain null) a whole device owned goes. Blocks until the work in flight is done, for at most a second; past that
    // the objects are left alone rather than destroyed under the GPU.
    void gone(void* device, void* swapchain);

    // Frees the copy of the frame and the rest of what only a playing effect needs, once nothing is in flight. Never waits: false means
    // "not yet", so call it again next frame. The pipeline stays.
    bool release_when_idle();

    bool off() const;
    PassStatus status() const;

private:
    struct Impl;
    std::unique_ptr<Impl> d_;
};

}  // namespace kscreen
