// SPDX-License-Identifier: MIT
// kiln.vfx, a simulated game: the same class of game as game.cpp, with a registered set built by the game's insertion rule, a small
// FX pool that follows the FxController's rules (a start for an unknown GUID finishes the entry; an entry lives until its end
// time, its handles are generation-checked) and two entities. It proves the queue, the guard's logic and the C# to C layouts;
// nothing about the real game. Used by tests/ and by a lab with kiln/config/vfx.json {"sim": true}.
#include <atomic>
#include <chrono>
#include <cstring>
#include <map>
#include <mutex>
#include <vector>

#include "game.h"

namespace kvx {

namespace {

constexpr uint64_t kCapacity = 4096;
constexpr uint32_t kSimFloatType = 0x90b5085du, kSimColorType = 0x53039620u;  // DsTypeIds: VfxEffectParameterFloat, VfxEffectParameterColor

struct SimFx {
    int64_t end = 0;
    bool has_end = false;
    uint32_t instance = 0;
    bool dead = false;
};

class SimGame final : public Game {
public:
    SimGame() {
        bits_.assign(kCapacity / 64, 0);
        keys_.assign(kCapacity * 2, 0);
        values_.assign(kCapacity, 0);
        for (int i = 0; i < kSimKnown; ++i) {
            uint8_t g[16];
            sim_known_guid(i, g);
            insert(g, static_cast<uint32_t>(i));
        }
        make_resource(res_[0], 10, -1.f, -1.f, false);
        make_resource(res_[1], 0, 2.f, 1.5f, true);
    }

    bool can_play() const override { return true; }
    bool has_guard() const override { return true; }
    bool is_sim() const override { return true; }

    MapView map(void*) override {
        MapView v;
        v.count = count_;
        v.capacity = kCapacity;
        v.bits = bits_.data();
        v.keys = keys_.data();
        v.values = values_.data();
        return v;
    }
    int is_registered(void*, const uint8_t guid[16]) override { return map_find(map(nullptr), guid); }
    bool vfx_lengths(void*, const uint8_t guid[16], VfxLengths* out) override {
        const MapView v = map(nullptr);
        const int64_t slot = map_slot(v, guid);
        if (slot < 0) return false;
        read_lengths(res_[v.values[slot]], ResLayout{}, out);
        return true;
    }
    bool vfx_params(void*, const uint8_t guid[16], VfxParamLayout* out) override {
        const MapView v = map(nullptr);
        const int64_t slot = map_slot(v, guid);
        if (slot < 0) return false;
        read_params(res_[v.values[slot]], ResLayout{}, out);
        return true;
    }
    bool read_resource(void*, const uint8_t guid[16], void* out, size_t n) override {
        const MapView v = map(nullptr);
        const int64_t slot = map_slot(v, guid);
        if (slot < 0 || n > kResourceBytes) return false;
        std::memcpy(out, res_[v.values[slot]], n);
        return true;
    }

    uint32_t play_at(const Frame& f, const uint8_t guid[16], const WorldTransform&, const FxParams& p, const FxTiming& d) override {
        last_start = p;
        return start(f, guid, d, true);
    }
    uint32_t play_on(const Frame& f, const uint8_t guid[16], const FxAttach& a, const FxParams& p, const FxTiming& d) override {
        last_start = p;
        // the game starts an attached effect when the attachment resolves
        return start(f, guid, d, a.entity != kSimEntityNoSlot);
    }
    void stop(const Frame& f, uint32_t fx, bool immediate) override {
        auto it = fx_.find(fx);
        if (it == fx_.end()) return;
        if (immediate) {
            it->second.dead = true;
            return;
        }
        // the FxController's rule: while there is no instance it retries the start and an end time changes nothing
        if (!it->second.instance) return;
        it->second.has_end = true;
        it->second.end = f.now;
    }
    bool lookup(const Frame& f, uint32_t fx, uint32_t* instance) override {
        auto it = fx_.find(fx);
        if (it == fx_.end()) return false;
        if (it->second.dead || (it->second.instance && it->second.has_end && it->second.end <= f.now)) {
            fx_.erase(it);
            return false;
        }
        *instance = it->second.instance;
        return true;
    }
    void set_param(const Frame&, uint32_t instance, uint32_t hash, uint32_t type, const void* data, size_t size) override {
        last_param_hash = hash;
        last_param_type = type;
        last_param_instance = instance;
        last_param_size = size;
        std::memcpy(last_param, data, size < sizeof last_param ? size : sizeof last_param);
        ++params_applied;
        KV_I("vfx: sim: parameter 0x%08x type 0x%08x on instance 0x%x, %zu B", hash, type, instance, size);
    }
    bool entity_position(const Frame&, uint32_t entity, KilnVec3* out) override {
        if (entity == kSimEntity || entity == kSimEntityNoSlot) {
            *out = KilnVec3{1, 2, 3};
            return true;
        }
        if (entity == kSimEntityGoes) {
            if (!goes_at_) goes_at_ = updates_ + 30;
            if (updates_ >= goes_at_) return false;
            *out = KilnVec3{4, 5, 6};
            return true;
        }
        return false;
    }
    uint32_t create_direct(const Frame&, const uint8_t guid[16]) override {
        if (map_find(map(nullptr), guid) != 1) {
            guard_refused(guid, "VfxCreateInstance");
            return 0;
        }
        return ++next_instance_;
    }
    void preload(const Frame&, const uint8_t*) override { ++preloads; }

    void tick() { ++updates_; }
    uint64_t updates_ = 0;
    uint64_t last_param_hash = 0, last_param_type = 0, last_param_instance = 0, last_param_size = 0, params_applied = 0, preloads = 0;
    uint8_t last_param[16] = {};
    FxParams last_start{};

private:
    // the game's VfxRegister: slot = low 32 bits & (capacity - 1), then linear
    void insert(const uint8_t guid[16], uint32_t value) {
        uint64_t k0, k1;
        std::memcpy(&k0, guid, 8);
        std::memcpy(&k1, guid + 8, 8);
        uint64_t i = k0 & 0xffffffffu & (kCapacity - 1);
        while ((bits_[i >> 6] >> (i & 63)) & 1) i = i + 1 < kCapacity ? i + 1 : 0;
        bits_[i >> 6] |= 1ull << (i & 63);
        keys_[i * 2] = k0;
        keys_[i * 2 + 1] = k1;
        values_[i] = value;
        ++count_;
    }

    // The fields of a VfxResource that read_lengths and read_params take, at the offsets of the real one (docs/re/vfx.md section 2). With a
    // colour: a "color" parameter after the floats, magenta by default, laid out as VFX_Dummy_Impact's.
    static void make_resource(uint8_t* r, uint32_t kill, float duration, float lifetime, bool colour) {
        std::memset(r, 0, kResourceBytes);
        const ResLayout l;
        std::memcpy(r + l.kill, &kill, 4);
        uint32_t count = 0;
        const int32_t rel = 0x100 - static_cast<int32_t>(l.params);  // the descriptions live past the fixed part
        uint8_t* p = r + 0x100;
        auto add = [&](const char* name, uint32_t type, const void* value) {
            uint32_t h = 0x811c9dc5u;
            for (const char* s = name; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
            const uint8_t start = static_cast<uint8_t>(count * 4);
            std::memcpy(p, &h, 4);
            std::memcpy(p + 4, &type, 4);
            p[8] = start;
            p[9] = 4;
            std::memcpy(r + l.defaults + start, value, 4);
            p += 12;
            ++count;
        };
        if (duration >= 0) add("duration", kSimFloatType, &duration);
        if (lifetime >= 0) add("lifetime", kSimFloatType, &lifetime);
        const uint8_t magenta[4] = {0xff, 0x00, 0xff, 0xff};
        if (colour) add("color", kSimColorType, magenta);
        if (count) std::memcpy(r + l.params, &rel, 4);
        std::memcpy(r + l.params + 4, &count, 4);
    }

    uint32_t start(const Frame& f, const uint8_t guid[16], const FxTiming& d, bool resolved) {
        const uint32_t h = (++generation_ << 24) | (next_slot_++ & 0xffffff);
        SimFx fx;
        fx.has_end = d.has != 0;
        fx.end = d.has ? f.now + d.ns : 0;
        // the guard at FxStart: an unknown GUID is emptied, and the game's empty-GUID path finishes the entry
        if (map_find(map(nullptr), guid) != 1) {
            guard_refused(guid, "FxStart");
            fx.dead = true;
        } else if (resolved) {
            fx.instance = ++next_instance_;
        }
        fx_[h] = fx;
        return h;
    }

    static constexpr size_t kResourceBytes = 0x200;
    std::vector<uint64_t> bits_, keys_;
    std::vector<uint32_t> values_;
    uint8_t res_[kSimKnown][kResourceBytes];
    uint64_t count_ = 0;
    uint64_t goes_at_ = 0;
    std::map<uint32_t, SimFx> fx_;
    uint32_t generation_ = 0, next_slot_ = 1, next_instance_ = 0x10000;
};

SimGame* sim() {
    static SimGame g;
    return &g;
}

}  // namespace

Game* sim_game() { return sim(); }

void sim_known_guid(int n, uint8_t out[16]) {
    // Guid 5f5f5f5f-0000-4000-8000-00000000000<n + 1> in .NET's and the game's byte order
    static const uint8_t base[16] = {0x5f, 0x5f, 0x5f, 0x5f, 0x00, 0x00, 0x00, 0x40, 0x80, 0x00, 0, 0, 0, 0, 0, 0};
    std::memcpy(out, base, 16);
    out[15] = static_cast<uint8_t>(n + 1);
}

uint64_t sim_params_applied() { return sim()->params_applied; }
void sim_last_param(uint32_t* hash, uint32_t* type, float* first_float) {
    *hash = static_cast<uint32_t>(sim()->last_param_hash);
    *type = static_cast<uint32_t>(sim()->last_param_type);
    std::memcpy(first_float, sim()->last_param, sizeof(float));
}
uint64_t sim_preloads() { return sim()->preloads; }
FxParams sim_last_start_params() { return sim()->last_start; }

void sim_update() {
    static std::atomic<int64_t> t0{0};
    const int64_t now = std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
    int64_t expect = 0;
    t0.compare_exchange_strong(expect, now);
    sim()->tick();
    core_update(sim(), now - t0.load() + 1);
}

}  // namespace kvx
