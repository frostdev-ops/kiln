// SPDX-License-Identifier: MIT
// The host table's voxel writer (kfc_host.h voxel_write_ready / voxel_write; the fork's abi/runtime_host.h): ShroudForge's
// runtime.world.voxel.write goes through kiln.build instead of kfc-runtime's own writer.
//
// Why: kfc-runtime finds the game's block writer by reading the game's code from the registered terraforming impact
// (TerraformImpact) down to it, and refuses with outcome 1 ("voxel write was rejected before changing the world") when a
// function on that path carries a hook, or when the exe file cannot be compared with the loaded code. Under Kiln that path
// always carries hooks: kiln.rules and kiln.tools hook TerraformImpact and the carve dispatcher, and mods hook more (XHL
// Vein Mining the block writer itself). Every ShroudForge voxel write failed (netjunk, Altar Cleanup, 0.2.0-dev.31.17).
//
// How: the call comes on ShroudForge's mods thread. It is posted to the server world's thread (api->post), where
// voxel_write_core.h writes through kiln.build (the same engine writer and store commit, called through the hooks as
// the game calls it), and the mods thread waits for the answer. A write the server world has not started within
// kPostWaitMs is cancelled there and refused (outcome 1: it can no longer run); one that started is waited for. A write
// is for the server world the mod saw when it asked: one that the world thread reaches only after another server world
// began (kiln-core's count of them, KilnSessionInfo.sessions) is refused there before anything is read or written.
// After an uncertain outcome (3) that world takes no further ShroudForge voxel write, as ShroudForge's own writer marks
// its session's writes uncertain then; the next server world takes them again.
// Every refusal or failure goes to kiln.log with its box and its reason (the first ones each time, then every 100th).
// In a game that joined a server the reason says so (joined_notice.h: the server's world is the server's to change, as
// in ShroudForge), and plugin.cpp tells it once per joined session in kiln.log and as a Kiln toast.
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <vector>

#include "joined_notice.h"
#include "kfc_host.h"
#include "sf_internal.h"
#include "services/kiln_build.h"
#include "voxel_write_core.h"

namespace ksf {

namespace {

const KilnApi* v_api = nullptr;
KilnOwner v_owner = 0;
std::atomic<JoinedRefusalFn> v_on_joined{nullptr};
std::atomic<const KilnBuildApi*> v_build{nullptr};

constexpr unsigned kPostWaitMs = 2000;     // the server world steps many times a second; a stalled one refuses
constexpr unsigned kRunningWaitMs = 10000; // a write the world thread has begun is waited for this long, then uncertain

const KilnBuildApi* build() {
    if (const auto* b = v_build.load(std::memory_order_acquire)) return b;
    if (!v_api || !v_api->service) return nullptr;
    const auto* b = static_cast<const KilnBuildApi*>(v_api->service({KILN_BUILD_SERVICE, sizeof(KILN_BUILD_SERVICE) - 1}, 1));
    if (!b || b->header.size < offsetof(KilnBuildApi, write) + sizeof(b->write) || !b->status || !b->grid || !b->ready ||
        !b->read || !b->write)
        return nullptr;
    v_build.store(b, std::memory_order_release);  // service tables live as long as the process
    return b;
}

std::string build_detail(const KilnBuildApi* b) {
    KilnBuildStatus st{};
    st.size = sizeof st;
    if (!b || b->status(&st) != KILN_OK) return {};
    st.detail[sizeof st.detail - 1] = 0;
    return st.detail;
}

// The server world a write is for: its pointer and kiln-core's count of server worlds since boot (any thread). False
// when no server world runs in this process (a joined client, the main menu) or the core cannot say.
struct WorldId {
    uintptr_t world = 0;
    uint32_t session = 0;
    bool operator==(const WorldId&) const = default;
};
bool server_world(WorldId& out) {
    if (!v_api || v_api->size < offsetof(KilnApi, session) + sizeof v_api->session || !v_api->session) return false;
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (v_api->session(&s) != KILN_OK || !s.worlds[KILN_WORLD_SERVER]) return false;
    out = WorldId{s.worlds[KILN_WORLD_SERVER], s.sessions[KILN_WORLD_SERVER]};
    return true;
}

// Why no server world runs: a joined game (joined_notice.h) or none at all. No side effect: voxel_write_ready asks it
// whenever ShroudForge checks the operation's availability.
std::string no_server_world() { return joined::refusal_reason(session_worlds(v_api)); }

// A write refused for that reason: in a joined game the notice is told (once per joined session, plugin.cpp).
void refused_without_server_world() {
    const joined::Worlds w = session_worlds(v_api);
    if (const auto fn = v_on_joined.load(std::memory_order_acquire); fn && joined::joined(w)) fn(w);
}

// why a write cannot be tried now, or "" when it can (any thread)
std::string not_ready() {
    const auto* b = build();
    if (!b) return "kiln.build is not available in this start";
    WorldId id;
    if (!server_world(id)) return no_server_world();
    KilnBuildStatus st{};
    st.size = sizeof st;
    if (b->status(&st) != KILN_OK) return "kiln.build has no status";
    st.detail[sizeof st.detail - 1] = 0;
    if (!st.write) return std::string("kiln.build's voxel writes are off") + (st.detail[0] ? ": " : "") + st.detail;
    if (!st.world) return "the voxel world is not running yet";
    return {};
}

// the server world whose voxel writes are off after an uncertain outcome (any thread)
std::mutex v_fence_mu;
WorldId v_fenced;
bool fenced(const WorldId& w) {
    std::lock_guard lock(v_fence_mu);
    return v_fenced.world && v_fenced == w;
}
void fence(const WorldId& w) {
    std::lock_guard lock(v_fence_mu);
    v_fenced = w;
}

voxels::Io build_io(const KilnBuildApi* b) {
    voxels::Io io;
    io.user = const_cast<KilnBuildApi*>(b);
    io.ready = [](void* u, const int32_t o[3], const uint32_t d[3]) { return static_cast<KilnBuildApi*>(u)->ready(o, d); };
    io.read = [](void* u, const int32_t o[3], const uint32_t d[3], KilnVoxel* out, std::size_t cap) {
        return static_cast<KilnBuildApi*>(u)->read(o, d, out, cap);
    };
    io.write = [](void* u, const KilnBuildWrite* w, KilnBuildResult* r) { return static_cast<KilnBuildApi*>(u)->write(w, r); };
    io.detail = [](void* u) { return build_detail(static_cast<KilnBuildApi*>(u)); };
    io.grid = [](void* u, KilnBuildGrid* g) { return static_cast<KilnBuildApi*>(u)->grid(g); };
    return io;
}

struct Job {
    std::mutex mu;
    std::condition_variable cv;
    enum State { Pending, Running, Done, Cancelled } state = Pending;
    WorldId world;  // the server world when the mod asked
    int32_t origin[3]{};
    uint32_t dims[3]{};
    std::vector<uint16_t> values;
    uint32_t outcome = voxels::kUncertain;
    std::string why;
};

// the server world's thread
void KILN_CALL run_job(void* user) {
    std::unique_ptr<std::shared_ptr<Job>> holder(static_cast<std::shared_ptr<Job>*>(user));
    Job& job = **holder;
    {
        std::lock_guard lock(job.mu);
        if (job.state != Job::Pending) return;  // cancelled: the mods thread has answered "refused"
        job.state = Job::Running;
    }
    uint32_t outcome = voxels::kUncertain;
    std::string why;
    try {
        WorldId now;
        if (!server_world(now) || now != job.world)
            outcome = voxels::kRefused,
            why = "the server world changed before the write ran (the box was meant for the world the mod saw when it asked)";
        else if (fenced(now))
            outcome = voxels::kRefused,
            why = "an earlier ShroudForge voxel write in this world had an uncertain outcome (see that line); as with "
                  "ShroudForge's own writer, no further voxel write is made in this world until it is loaded again";
        else if (const auto* b = build())
            outcome = voxels::write_cells(build_io(b), job.origin, job.dims, job.values.data(), job.values.size(), why);
        else outcome = voxels::kRefused, why = "kiln.build is not available in this start";
    } catch (...) {
        outcome = voxels::kUncertain;
        why = "an exception inside the write";
    }
    if (outcome == voxels::kUncertain) fence(job.world);
    {
        std::lock_guard lock(job.mu);
        job.outcome = outcome;
        job.why = std::move(why);
        job.state = Job::Done;
    }
    job.cv.notify_all();
}

// kiln.log: every refusal or failure says why. An uncertain outcome (3) is always logged (that world takes no further
// write). Refusals and put-back writes (2) are counted by outcome and reason (the text before its first " (" or ": "):
// the first 10 of each, then every 100th (a mod that retries every update), so a new reason is never hidden behind an
// old one.
std::atomic<uint64_t> v_writes{0};
std::mutex v_reasons_mu;
std::vector<std::pair<std::string, uint64_t>> v_reasons;  // reason, refusals so far (at most kMaxReasons kinds)
constexpr size_t kMaxReasons = 64;
uint64_t count_reason(uint32_t outcome, const std::string& why) {
    std::string key = std::to_string(outcome) + " " + why.substr(0, std::min(why.find(" ("), why.find(": ")));
    std::lock_guard lock(v_reasons_mu);
    for (auto& [k, n] : v_reasons)
        if (k == key) return ++n;
    if (v_reasons.size() >= kMaxReasons) key.clear();  // more kinds than that: they share one count
    for (auto& [k, n] : v_reasons)
        if (k == key) return ++n;
    v_reasons.emplace_back(std::move(key), 1);
    return 1;
}
void report(const int32_t o[3], const uint32_t d[3], uint32_t outcome, const std::string& why) {
    if (outcome == voxels::kWritten) {
        if (v_writes.fetch_add(1, std::memory_order_relaxed) == 0)
            logf(KILN_LOG_INFO, "ShroudForge voxel writes go through kiln.build (first write: %u x %u x %u cells at %d,%d,%d)",
                 d[0], d[1], d[2], o[0], o[1], o[2]);
        return;
    }
    const uint64_t n = outcome == voxels::kUncertain ? 1 : count_reason(outcome, why);
    if (n > 10 && n % 100) return;
    const char* what = outcome == voxels::kRefused ? "refused before changing the world"
                       : outcome == voxels::kRestored ? "failed; the earlier cells were put back"
                                                      : "failed; the outcome is uncertain";
    logf(outcome == voxels::kRefused ? KILN_LOG_WARN : KILN_LOG_ERROR,
         "ShroudForge voxel write %s: %u x %u x %u cells at %d,%d,%d: %s%s", what, d ? d[0] : 0, d ? d[1] : 0, d ? d[2] : 0,
         o ? o[0] : 0, o ? o[1] : 0, o ? o[2] : 0, why.c_str(),
         n == 10 ? " (from now on every 100th line of this reason)" : "");
}

uint32_t __cdecl voxel_write_ready() { return not_ready().empty() ? 1u : 0u; }

bool __cdecl voxel_write(const int32_t* origin, const uint32_t* dims, const uint16_t* values, size_t count, uint32_t* outcome) {
    if (!outcome) return false;
    *outcome = voxels::kRefused;
    std::string why;
    if (!origin || !dims || !values) {
        report(origin, dims, voxels::kRefused, "the request has no box or no cells");
        return false;
    }
    if (std::string busy = not_ready(); !busy.empty()) {
        if (busy == joined::kReason) refused_without_server_world();
        report(origin, dims, voxels::kRefused, busy);
        return false;
    }
    std::size_t cells = 1;
    for (int a = 0; a < 3; ++a) cells = dims[a] && cells <= voxels::kMaximumCells ? cells * dims[a] : 0;
    if (!cells || cells > voxels::kMaximumCells || count != cells) {
        report(origin, dims, voxels::kRefused, "a voxel write must contain between 1 and 65,536 cells, as many as its box");
        return false;
    }
    auto job = std::make_shared<Job>();
    if (!server_world(job->world)) {
        refused_without_server_world();
        report(origin, dims, voxels::kRefused, no_server_world());
        return false;
    }
    std::memcpy(job->origin, origin, sizeof job->origin);
    std::memcpy(job->dims, dims, sizeof job->dims);
    job->values.assign(values, values + count);
    if (v_api->on_tick_thread && v_api->on_tick_thread()) {  // already on the server world's thread: no post
        run_job(new std::shared_ptr<Job>(job));
    } else {
        auto* holder = new (std::nothrow) std::shared_ptr<Job>(job);
        if (!holder || !v_api->post || v_api->post(v_owner, &run_job, holder) != KILN_OK) {
            delete holder;
            report(origin, dims, voxels::kRefused, "the write could not be passed to the server world's thread");
            return false;
        }
        std::unique_lock lock(job->mu);
        if (!job->cv.wait_for(lock, std::chrono::milliseconds(kPostWaitMs), [&] { return job->state == Job::Done; })) {
            if (job->state == Job::Pending) {
                job->state = Job::Cancelled;  // run_job sees it and does nothing
                lock.unlock();
                report(origin, dims, voxels::kRefused, "the server world did not take the write within 2 seconds");
                return false;
            }
            if (!job->cv.wait_for(lock, std::chrono::milliseconds(kRunningWaitMs), [&] { return job->state == Job::Done; })) {
                lock.unlock();
                fence(job->world);
                *outcome = voxels::kUncertain;
                report(origin, dims, voxels::kUncertain, "the server world began the write and did not finish it within 12 seconds");
                return false;
            }
        }
    }
    uint32_t result;
    {
        std::lock_guard lock(job->mu);
        result = job->outcome;
        why = job->why;
    }
    *outcome = result;
    report(origin, dims, result, why);
    return result == voxels::kWritten;
}

}  // namespace

joined::Worlds session_worlds(const KilnApi* api) {
    joined::Worlds w;
    if (!api || api->size < offsetof(KilnApi, session) + sizeof api->session || !api->session) return w;
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (api->session(&s) != KILN_OK) return w;
    return joined::from_session(s);
}

void host_voxels_on_joined(JoinedRefusalFn fn) { v_on_joined.store(fn, std::memory_order_release); }

void host_voxels_attach(const KilnApi* api, KilnOwner owner, KfcHost* table) {
    v_api = api;
    v_owner = owner;
    table->voxel_write_ready = &voxel_write_ready;
    table->voxel_write = &voxel_write;
}

}  // namespace ksf
