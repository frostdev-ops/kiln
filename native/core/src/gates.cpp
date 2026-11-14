// SPDX-License-Identifier: MIT
// The client gate's list and what kiln.json's "preview" lifts of it (gates.h).
#include "gates.h"

#include <algorithm>
#include <mutex>

#include "kiln_api.h"
#include "log.h"
#include "services/kiln_caps.h"

#include "kiln_gates.g.h"  // kBuiltIn, from native/core/gates/*.json

namespace kiln::gates {
namespace {
const Gate* g_list = kBuiltIn;
std::mutex g_mutex;
std::vector<Held> g_held;
bool g_configured = false;

std::vector<Held> all_of(const Gate* list, int side_code) {
    std::vector<Held> out;
    for (const Gate* g = list; g && g->name; ++g) {
        if (side_code >= 0 && g->side != kBoth && g->side != static_cast<uint32_t>(side_code)) continue;
        out.push_back({g->name, std::string(KILN_CAP_GATE_TEXT) + (g->checks ? g->checks : "")});
    }
    std::sort(out.begin(), out.end(), [](const Held& a, const Held& b) { return a.name < b.name; });
    return out;
}

// g_mutex held. The early safe-idle exit never calls configure().
const std::vector<Held>& held_locked() {
    if (!g_configured) {
        g_held = all_of(g_list, -1);
        g_configured = true;
    }
    return g_held;
}
}  // namespace

void use_list_for_tests(const Gate* list) {
    std::lock_guard lock(g_mutex);
    g_list = list ? list : kBuiltIn;
    g_held.clear();
    g_configured = false;
}

void configure(uint32_t side, bool preview_all, const std::vector<std::string>& preview) {
    std::lock_guard lock(g_mutex);
    const std::vector<Held> every = all_of(g_list, -1);
    std::vector<Held> mine = all_of(g_list, side == KILN_SIDE_CLIENT ? kClient : kServer);
    for (const std::string& p : preview)
        if (std::none_of(every.begin(), every.end(), [&](const Held& h) { return h.name == p; }))
            KLOG_W("config: \"preview\" names '%s', which is not a capability held for a game test; ignored", p.c_str());
    const size_t total = mine.size();
    if (preview_all)
        mine.clear();
    else
        std::erase_if(mine, [&](const Held& h) { return std::find(preview.begin(), preview.end(), h.name) != preview.end(); });
    g_held = std::move(mine);
    g_configured = true;
    if (total)
        KLOG_I("gate: %zu of %zu capabilities held until their game test%s", g_held.size(), total,
               preview_all ? " (kiln.json \"preview\": all lifted)" : total != g_held.size() ? " (the rest lifted by kiln.json \"preview\")" : "");
}

std::vector<Held> held() {
    std::lock_guard lock(g_mutex);
    return held_locked();
}

bool is_held(std::string_view name) {
    std::lock_guard lock(g_mutex);
    const std::vector<Held>& h = held_locked();
    const auto it = std::lower_bound(h.begin(), h.end(), name, [](const Held& a, std::string_view n) { return a.name < n; });
    return it != h.end() && it->name == name;
}

}  // namespace kiln::gates
