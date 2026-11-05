// SPDX-License-Identifier: MIT
// The budget: what the game's VFX system can still take. Every limit here is a fatal abort in the game when exceeded (docs/re/vfx.md 4.3).
#include "vfxcook.h"

namespace kvb {

uint64_t Budget::room_mesh_bytes() const {
    const uint64_t used = game_.mesh_bytes + mesh_bytes_ + cap_.mesh_margin;
    return cap_.mesh_bytes > used ? cap_.mesh_bytes - used : 0;
}

bool Budget::try_add(const BudgetNeed& need, std::string* why) {
    uint32_t new_meshes = 0, new_sounds = 0, new_curves = 0;
    for (const Guid& g : need.meshes) new_meshes += meshes_.count(g) ? 0 : 1;
    for (const std::string& s : need.sounds) new_sounds += sounds_.count(s) ? 0 : 1;
    for (const Guid& g : need.curves) new_curves += curves_.count(g) ? 0 : 1;
    std::string over;
    auto line = [&](const char* what, uint64_t want, uint64_t room) {
        if (want <= room) return;
        over += (over.empty() ? "" : ", ") + std::string(what) + ": needs " + std::to_string(want) + ", " + std::to_string(room) + " left";
    };
    line("VFX", need.vfx, room_vfx());
    line("meshes", new_meshes, room_meshes());
    line("mesh memory (bytes)", need.mesh_bytes, room_mesh_bytes());
    line("sounds in VFX", new_sounds, room_sounds());
    line("curves", new_curves, room_curves());
    if (!over.empty()) {
        if (why) *why = over;
        return false;
    }
    vfx_ += need.vfx;
    mesh_bytes_ += need.mesh_bytes;
    meshes_.insert(need.meshes.begin(), need.meshes.end());
    sounds_.insert(need.sounds.begin(), need.sounds.end());
    curves_.insert(need.curves.begin(), need.curves.end());
    return true;
}

}  // namespace kvb
