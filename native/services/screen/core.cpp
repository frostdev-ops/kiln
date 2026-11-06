// SPDX-License-Identifier: MIT
#include "core.h"

#include <algorithm>
#include <cmath>

namespace kscreen {
namespace {

float safe(float v, float fallback, float lo, float hi) {
    if (!std::isfinite(v)) return fallback;
    return std::clamp(v, lo, hi);
}

void safe_colour(float (&c)[4], const float (&fallback)[4]) {
    for (int i = 0; i < 4; ++i) c[i] = safe(c[i], fallback[i], 0.0f, 1.0f);
}

}  // namespace

float srgb_to_linear(float v) { return v <= 0.04045f ? v / 12.92f : std::pow((v + 0.055f) / 1.055f, 2.4f); }

KilnScreenEffect sanitize(const KilnScreenEffect& raw) {
    const KilnScreenEffect d = kiln_screen_effect_defaults();
    KilnScreenEffect e = raw;
    e.size = sizeof e;
    e.strength = safe(e.strength, d.strength, 0, 1);
    e.fade_in = safe(e.fade_in, d.fade_in, 0, 60);
    e.seconds = std::isfinite(e.seconds) ? (e.seconds < 0 ? -1.0f : std::min(e.seconds, 3600.0f)) : d.seconds;
    e.fade_out = safe(e.fade_out, d.fade_out, 0, 60);
    e.exposure = safe(e.exposure, d.exposure, -8, 8);
    e.contrast = safe(e.contrast, d.contrast, 0.05f, 4);
    e.saturation = safe(e.saturation, d.saturation, 0, 4);
    e.gamma = safe(e.gamma, d.gamma, 0.1f, 5);
    e.temperature = safe(e.temperature, d.temperature, -1, 1);
    safe_colour(e.tint, d.tint);
    safe_colour(e.vignette, d.vignette);
    e.vignette_radius = safe(e.vignette_radius, d.vignette_radius, 0, 1);
    e.vignette_softness = safe(e.vignette_softness, d.vignette_softness, 0.001f, 2);
    safe_colour(e.fill, d.fill);
    return e;
}

GpuEffect to_gpu(const KilnScreenEffect& e, float weight) {
    GpuEffect g{};
    g.grade[0] = std::pow(2.0f, e.exposure);
    g.grade[1] = e.contrast;
    g.grade[2] = e.saturation;
    g.grade[3] = 1.0f / e.gamma;
    // Warmer is more red and less blue, with brightness held: the factors are scaled so that a grey stays as bright.
    const float red = 1.0f + 0.25f * e.temperature, blue = 1.0f - 0.25f * e.temperature;
    const float luma = 0.2126f * red + 0.7152f + 0.0722f * blue;
    g.balance[0] = red / luma;
    g.balance[1] = 1.0f / luma;
    g.balance[2] = blue / luma;
    for (int i = 0; i < 3; ++i) {
        g.tint[i] = srgb_to_linear(e.tint[i]);
        g.vignette[i] = srgb_to_linear(e.vignette[i]);
        g.fill[i] = srgb_to_linear(e.fill[i]);
    }
    g.tint[3] = e.tint[3];
    g.vignette[3] = e.vignette[3];
    g.fill[3] = e.fill[3];
    g.shape[0] = e.vignette_radius;
    g.shape[1] = e.vignette_softness;
    g.shape[3] = weight;
    return g;
}

uint32_t Mixer::play(KilnOwner owner, const KilnScreenEffect& effect) {
    std::lock_guard<std::mutex> lk(mu_);
    if (live_.size() >= KILN_SCREEN_MAX_EFFECTS) return 0;
    Live l{};
    l.handle = next_++;
    if (next_ == 0) next_ = 1;
    l.owner = owner;
    l.e = sanitize(effect);
    const auto at = std::upper_bound(live_.begin(), live_.end(), l.e.order, [](int32_t order, const Live& o) { return order < o.e.order; });
    const uint32_t h = l.handle;
    live_.insert(at, l);
    return h;
}

bool Mixer::stop(uint32_t handle, bool immediate) {
    std::lock_guard<std::mutex> lk(mu_);
    for (auto it = live_.begin(); it != live_.end(); ++it) {
        if (it->handle != handle) continue;
        if (immediate || !it->started || it->e.fade_out <= 0) live_.erase(it);
        else it->stopping = true;
        return true;
    }
    return false;
}

bool Mixer::set_strength(uint32_t handle, float strength) {
    std::lock_guard<std::mutex> lk(mu_);
    for (Live& l : live_) {
        if (l.handle != handle) continue;
        l.e.strength = safe(strength, l.e.strength, 0, 1);
        return true;
    }
    return false;
}

bool Mixer::playing(uint32_t handle) const {
    std::lock_guard<std::mutex> lk(mu_);
    return std::any_of(live_.begin(), live_.end(), [&](const Live& l) { return l.handle == handle; });
}

uint32_t Mixer::stop_owner(KilnOwner owner, bool immediate) {
    std::lock_guard<std::mutex> lk(mu_);
    uint32_t n = 0;
    for (auto it = live_.begin(); it != live_.end();) {
        if (it->owner != owner) {
            ++it;
            continue;
        }
        ++n;
        if (immediate || !it->started || it->e.fade_out <= 0) {
            it = live_.erase(it);
        } else {
            it->stopping = true;
            ++it;
        }
    }
    return n;
}

void Mixer::clear() {
    std::lock_guard<std::mutex> lk(mu_);
    live_.clear();
}

uint32_t Mixer::count() const {
    std::lock_guard<std::mutex> lk(mu_);
    return static_cast<uint32_t>(live_.size());
}

float Mixer::level(Live& l, double now, bool* done) {
    const KilnScreenEffect& e = l.e;
    const double t = now - l.start;
    float rising = e.fade_in > 0 && t < e.fade_in ? static_cast<float>(t / e.fade_in) : 1.0f;
    if (!l.fading) {
        const bool over = e.seconds >= 0 && t >= static_cast<double>(e.fade_in) + e.seconds;
        if (l.stopping || over) {
            l.stopping = false;
            l.fading = true;
            // an effect that ran out fades from its due time, so a slow frame doesn't shift the fade; a stop fades from now, from where it is
            l.fade_start = over ? l.start + e.fade_in + e.seconds : now;
            l.fade_from = over ? 1.0f : rising;
        }
    }
    if (!l.fading) return rising;
    const double gone = now - l.fade_start;
    if (e.fade_out <= 0 || gone >= e.fade_out) {
        *done = true;
        return 0;
    }
    return l.fade_from * (1.0f - static_cast<float>(gone / e.fade_out));
}

uint32_t Mixer::compose(double now, GpuFrame& frame) {
    std::lock_guard<std::mutex> lk(mu_);
    uint32_t n = 0;
    for (auto it = live_.begin(); it != live_.end();) {
        if (!it->started) {
            it->started = true;
            it->start = now;
        }
        bool done = false;
        const float weight = level(*it, now, &done) * it->e.strength;
        if (done) {
            it = live_.erase(it);
            continue;
        }
        if (weight > 0 && n < KILN_SCREEN_MAX_EFFECTS) frame.effects[n++] = to_gpu(it->e, weight);
        ++it;
    }
    frame.count = n;
    return n;
}

}  // namespace kscreen
