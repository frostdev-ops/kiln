// SPDX-License-Identifier: MIT
// kiln.repl's loopback harness (kiln_repl.h, "lab"; the dossier's chapter 11 §4.2, §2.7). Lab servers only: kiln.json's
// policy "any" and a kiln.net lab port, else every lab function answers KILN_ERR_UNAVAILABLE.
//
// The game's own replication runs for "fake players" on free machine indices; only the session is missing:
//   activate    Prepare's detour appends an item {machine m, slot s, 0x2000, 0x2000} per fake: the game activates the
//               object, sets the slot's bit in the world's slot mask and runs the fake's jobs;
//   keep alive  ResetMachine's detour returns for a fake (the frame resets every machine slot without a peer);
//   acks        RelevanceDriver's detour runs the game's AckProcess for each fake first (the frame does it only for
//               machines with a peer);
//   relevance   RelevanceStep's detour appends the forced entities to a fake context's newly-relevant list (a fake has
//               no player entity, so nothing is relevant to it by itself);
//   capture     SendCallback's detour copies a fake batch's messages, records them as sent (RecordSent, so acks find
//               them), sets batch->sent and returns: there is no socket;
//   snapshot    EntityWriter's and DeltaWriter's detours copy the snapshot the server coded for each fake message;
//   client side in the next Pre (tick thread) the server exe's own copy of the client decoder decodes each message
//               against the template's state (TemplateState) or the harness's earlier decoded state, with Kiln's own
//               allocator; each decoded state is compared, component by component, with the server's snapshot;
//   ack back    the game's AckReceive with every message seq of the frame: baselines advance, the handshake is acked;
//   release     the fakes stop being appended and the game's own ResetMachine frees each object and its slot bit.
// The probe this comes from (the RE wave's repl-lab) carried a can-in-a-chest test; that scenario lives in
// mods/tests/repl-test, not here.
#include <windows.h>

#include <malloc.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_hook.hpp"
#include "nlohmann/json.hpp"
#include "repl.h"
#include "services/kiln_caps.h"
#include "services/kiln_net.h"
#include "services/kiln_repl.h"

namespace repl {
namespace {

constexpr uint32_t kMachines = KILN_REPL_MACHINES;
constexpr uint32_t kMaxForced = 64;
constexpr uint32_t kCaptureBytes = 1u << 20, kCaptureMsgs = 4096;
constexpr uint32_t kSnapBytes = 1u << 20, kSnapRows = 4096;
constexpr uint32_t kMirrorStates = 32;  // decoded states kept per entity, as a client keeps (StoreState)
constexpr uint32_t kFakeMax = 0x2000;   // per-message maximum given to a fake on both channels

// ---- what the harness may do here ----
bool g_resolved = false;    // repl.loopback resolved
bool g_policy_any = false;  // kiln.json policy "any" (top level, or the side section's)
uint32_t g_lab_port = 0;    // kiln.net's lab transport
bool g_allowed = false;
const KilnNetApi* g_net = nullptr;
std::string g_why_off;

// ---- the fakes ----
std::atomic<uint32_t> g_fakes{0};        // bit per machine: append these in Prepare
std::atomic<uint32_t> g_frame_fakes{0};  // what Prepare appended this frame: their batches are captured, never sent
std::atomic<bool> g_pending{false};      // a frame ran since the last drain
std::atomic<bool> g_broken{false};       // a harness detour faulted: no more appends (the game then frees the fakes)
uint8_t g_slot_of[kMachines];            // written before g_fakes (release), read by Prepare after it (acquire)
uint32_t g_forced[kMachines][kMaxForced];
std::atomic<uint32_t> g_forced_n[kMachines];

// What a fake batch held: written by that (machine, channel)'s callback job, read in the next Pre.
struct Capture {
    struct Msg {
        uint8_t type;
        uint32_t seq, off, len;
    };
    uint8_t* bytes = nullptr;
    uint32_t used = 0;
    Msg* msgs = nullptr;
    uint32_t n = 0;
    uint32_t dropped = 0;
};
// The snapshot the server coded for each fake message: written by that (machine, channel)'s queue reader job.
struct Snaps {
    struct Row {
        uint32_t seq, entity, off, len;
    };
    uint8_t* bytes = nullptr;
    uint32_t used = 0;
    Row* rows = nullptr;
    uint32_t n = 0;
    uint32_t dropped = 0;
};
Capture g_cap[kMachines][2];
Snaps g_snap[kMachines][2];

// Kiln's allocator with the game's allocator shape (vtable +8 alloc, +0x10 free; §2.7). The other slots are never
// called by TemplateState, Decode or StateIndex; they count and answer 0.
struct Alloc {
    void* const* vtbl;
};
std::atomic<uint64_t> g_alloc_traps{0};
void* a_alloc(Alloc*, uint64_t size, uint64_t align, uint64_t, uint64_t) {
    return _aligned_malloc(size ? size : 1, align < 16 ? 16 : align);
}
void a_free(Alloc*, void* p, uint64_t) {
    if (p) _aligned_free(p);
}
uintptr_t a_trap(Alloc*) {
    g_alloc_traps.fetch_add(1, std::memory_order_relaxed);
    return 0;
}
void* g_vtbl[16];
Alloc g_alloc{g_vtbl};

// The client side of each fake: decoded states per entity and tick, the baselines of later updates.
struct Mirror {
    std::unordered_map<uint32_t, std::map<uint32_t, uint8_t*>> states;
    void put(uint32_t e, uint32_t tick, uint8_t* s) {
        auto& m = states[e];
        auto [it, fresh] = m.emplace(tick, s);
        if (!fresh) {
            a_free(&g_alloc, it->second, 0);
            it->second = s;
        }
        while (m.size() > kMirrorStates) {
            a_free(&g_alloc, m.begin()->second, 0);
            m.erase(m.begin());
        }
    }
    uint8_t* get(uint32_t e, uint32_t tick) const {
        auto a = states.find(e);
        if (a == states.end()) return nullptr;
        auto b = a->second.find(tick);
        return b == a->second.end() ? nullptr : b->second;
    }
    const uint8_t* newest(uint32_t e, uint32_t* tick) const {
        auto a = states.find(e);
        if (a == states.end() || a->second.empty()) return nullptr;
        *tick = a->second.rbegin()->first;
        return a->second.rbegin()->second;
    }
    void forget(uint32_t e) {
        auto a = states.find(e);
        if (a == states.end()) return;
        for (auto& [t, s] : a->second) a_free(&g_alloc, s, 0);
        states.erase(a);
    }
    void clear() {
        for (auto& [e, m] : states)
            for (auto& [t, s] : m) a_free(&g_alloc, s, 0);
        states.clear();
    }
};
Mirror g_mirror[kMachines][2];

struct Counters {
    uint64_t messages = 0, decoded = 0, equal = 0, differ = 0, custom_equal = 0, custom_differ = 0, decode_faults = 0,
             no_baseline = 0, acked = 0, dropped = 0, destroy_lists = 0;
} C;  // tick thread
int g_first_notes = 0;  // detail lines for the first differences / faults
uint32_t g_pres = 0;    // Pres since the fakes were added

// ---- hooks ----
Guard g_harness_guard{"lab-harness"};  // the harness's own code in Prepare, RelevanceDriver, RelevanceStep, SendCallback
Guard g_writer_guard{"lab-writer"};
Guard g_delta_guard{"lab-delta"};
kiln::RawHook h_prepare, h_reset, h_reldriver, h_relstep, h_callback, h_writer, h_delta;
bool g_hooked = false;

using PrepareFn = void (*)(void* out, uintptr_t world, ItemList* in, uint64_t also_pred);
using ResetFn = void (*)(uintptr_t world, uint64_t machine);
using RelDriverFn = void (*)(uintptr_t world);
using AckProcessFn = void (*)(uintptr_t world, uint64_t machine);
using RelStepFn = void (*)(uintptr_t ctx, uintptr_t world);
using CallbackFn = void (*)(CbPair* pair);
using RecordSentFn = void (*)(uintptr_t world, MsgEntry* msg, uint64_t machine, uint64_t pred);
using WriterFn = uint64_t (*)(WriterOut* out, uint8_t* args);
using DeltaFn = uint64_t (*)(void* bits, uintptr_t registry, const uint8_t* snap, const uint8_t* base, uint64_t tmpl_flag,
                             void* base_ref, uint64_t pred);
using TemplateStateFn = StateRef* (*)(StateRef* out, TemplateArgs* args);
using DecodeFn = StateRef* (*)(StateRef* out, Alloc* alloc, const Slice* payload, const uint8_t* base, uint64_t pred,
                               uintptr_t registry, uint64_t tick, void* zero);
using AckReceiveFn = void (*)(uintptr_t world, uint64_t machine, const Slice* msg, uint64_t pred);

bool is_fake(uint32_t mask, uint32_t m) { return m < kMachines && ((mask >> m) & 1); }

// The machine and channel of a replication context, or -1.
int machine_of_ctx(uintptr_t world, uintptr_t ctx, uint8_t* pred) {
    const uintptr_t base = *reinterpret_cast<const uintptr_t*>(world + static_cast<uintptr_t>(L.world_objects));
    if (!base || ctx < base) return -1;
    const uintptr_t d = ctx - base, stride = static_cast<uintptr_t>(L.obj_stride);
    const uintptr_t m = d / stride, r = d % stride;
    if (m >= kMachines) return -1;
    if (r == static_cast<uintptr_t>(L.obj_client_ctx)) *pred = 0;
    else if (r == static_cast<uintptr_t>(L.obj_pred_ctx)) *pred = 1;
    else return -1;
    return static_cast<int>(m);
}

uintptr_t object_of(uintptr_t world, uint32_t m) {
    const uintptr_t base = read<uintptr_t>(world + static_cast<uintptr_t>(L.world_objects));
    return base ? base + m * static_cast<uintptr_t>(L.obj_stride) : 0;
}

// ---- Prepare: append the fakes' items ----
struct PrepareCall {
    ItemList* in;
    uint32_t mask;
    Item12 items[40];
    ItemList list;
    uint32_t appended;
};
void prepare_items(void* p) {
    auto* c = static_cast<PrepareCall*>(p);
    uint64_t n = 0;
    uint32_t used = 0;
    for (uint64_t i = 0; i < c->in->count && n < kMachines; ++i) {
        c->items[n++] = c->in->ptr[i];
        used |= 1u << (c->in->ptr[i].machine & 31);
    }
    for (uint32_t m = 0; m < kMachines && n < 40; ++m)
        if (is_fake(c->mask, m) && !((used >> m) & 1)) {
            c->items[n++] = Item12{static_cast<uint8_t>(m), g_slot_of[m], 0, kFakeMax, kFakeMax};
            c->appended |= 1u << m;
        }
    c->list = ItemList{c->items, n};
}
void d_prepare(void* out, uintptr_t world, ItemList* in, uint64_t also_pred) {
    const auto original = h_prepare.original<PrepareFn>();
    const uint32_t mask = g_broken.load(std::memory_order_relaxed) ? 0 : g_fakes.load(std::memory_order_acquire);
    PrepareCall c{in, mask, {}, {}, 0};
    if (!mask || !in || !observe(g_harness_guard, &prepare_items, &c)) {
        g_frame_fakes.store(0, std::memory_order_release);
        if (g_harness_guard.disabled.load()) g_broken.store(true);
        return original(out, world, in, also_pred);
    }
    g_frame_fakes.store(c.appended, std::memory_order_release);
    g_pending.store(true, std::memory_order_relaxed);
    original(out, world, &c.list, also_pred);
}

// ---- ResetMachine: a fake's object stays ----
void d_reset(uintptr_t world, uint64_t machine) {
    const uint32_t m = static_cast<uint8_t>(machine);
    if (!g_broken.load(std::memory_order_relaxed) && is_fake(g_fakes.load(std::memory_order_acquire), m)) return;
    h_reset.original<ResetFn>()(world, machine);
}

// ---- RelevanceDriver: the fakes' queued acks first ----
struct AckProcessCall {
    uintptr_t world;
    uint32_t mask;
};
void ack_process_fakes(void* p) {
    auto* c = static_cast<AckProcessCall*>(p);
    const auto fn = reinterpret_cast<AckProcessFn>(F.ack_process);
    for (uint32_t m = 0; m < kMachines; ++m)
        if (is_fake(c->mask, m)) fn(c->world, m);
}
void d_reldriver(uintptr_t world) {
    const uint32_t mask = g_broken.load(std::memory_order_relaxed) ? 0 : g_fakes.load(std::memory_order_acquire);
    if (mask) {
        AckProcessCall c{world, mask};
        if (!observe(g_harness_guard, &ack_process_fakes, &c)) g_broken.store(true);
    }
    h_reldriver.original<RelDriverFn>()(world);
}

// ---- RelevanceStep: the forced entities of a fake context ----
struct RelStepCall {
    uintptr_t ctx, world;
    uint32_t mask;
};
bool in_state_map(uintptr_t ctx, uint32_t e) {
    const uintptr_t map = ctx + kCtxStateMap;
    const uint64_t cap = *reinterpret_cast<const uint64_t*>(map + kMapCap);
    if (!cap || (cap & (cap - 1)) || !*reinterpret_cast<const uint32_t*>(map + kMapCount)) return false;
    const auto* bits = *reinterpret_cast<const uint64_t* const*>(map + kMapBits);
    const auto* keys = *reinterpret_cast<const uint32_t* const*>(map + kMapKeys);
    uint64_t i = map_slot(e, cap);
    for (uint64_t probes = 0; probes < cap; ++probes) {
        if (!((bits[i >> 6] >> (i & 63)) & 1)) return false;
        if (keys[i] == e) return true;
        i = (i + 1) & (cap - 1);
    }
    return false;
}
void force_relevant(void* p) {
    auto* c = static_cast<RelStepCall*>(p);
    uint8_t pred = 0;
    const int m = machine_of_ctx(c->world, c->ctx, &pred);
    if (m < 0 || !is_fake(c->mask, static_cast<uint32_t>(m))) return;
    const uint32_t n = std::min(g_forced_n[m].load(std::memory_order_acquire), kMaxForced);
    auto* count = reinterpret_cast<uint16_t*>(c->ctx + kCtxNewCount);
    for (uint32_t k = 0; k < n; ++k) {
        const uint32_t e = g_forced[m][k];
        if (!e || in_state_map(c->ctx, e) || *count >= kCtxNewMax) continue;
        auto* entry = reinterpret_cast<uint8_t*>(c->ctx + kCtxNewList + *count * static_cast<uintptr_t>(kCtxNewEntry));
        std::memset(entry, 0, kCtxNewEntry);
        std::memcpy(entry, &e, 4);
        *count = static_cast<uint16_t>(*count + 1);
    }
}
void d_relstep(uintptr_t ctx, uintptr_t world) {
    const uint32_t mask = g_broken.load(std::memory_order_relaxed) ? 0 : g_fakes.load(std::memory_order_acquire);
    if (mask) {
        RelStepCall c{ctx, world, mask};
        if (!observe(g_harness_guard, &force_relevant, &c)) g_broken.store(true);
    }
    h_relstep.original<RelStepFn>()(ctx, world);
}

// ---- SendCallback: a fake batch is captured, never sent ----
struct CallbackCall {
    Batch* b;
    uintptr_t world;
};
void capture_batch(void* p) {
    auto* c = static_cast<CallbackCall*>(p);
    Batch* b = c->b;
    Capture& cap = g_cap[b->machine][b->pred & 1];
    const auto record = reinterpret_cast<RecordSentFn>(F.record_sent);
    for (uint32_t i = 0; i < b->count; ++i) {
        MsgEntry& msg = b->msgs[i];
        if (cap.bytes && cap.n < kCaptureMsgs && msg.size <= kCaptureBytes - cap.used) {
            cap.msgs[cap.n++] = Capture::Msg{msg.type, msg.seq, cap.used, static_cast<uint32_t>(msg.size)};
            std::memcpy(cap.bytes + cap.used, msg.data, msg.size);
            cap.used += static_cast<uint32_t>(msg.size);
        } else {
            ++cap.dropped;
        }
        record(c->world, &msg, b->machine, b->pred);  // what the game's callback does after its commit
    }
}
void d_callback(CbPair* pair) {
    Batch* b = pair ? pair->batch : nullptr;
    if (b && is_fake(g_frame_fakes.load(std::memory_order_acquire), b->machine)) {
        CallbackCall c{b, g_world.load(std::memory_order_relaxed)};
        if (!c.world || !observe(g_harness_guard, &capture_batch, &c)) g_broken.store(true);
        b->sent = b->count;  // no socket: nothing was refused
        return;
    }
    h_callback.original<CallbackFn>()(pair);
}

// ---- EntityWriter + DeltaWriter: the snapshot of each fake message ----
struct WriterTls {
    int32_t machine = -1;
    uint8_t pred = 0;
    uint32_t seq = 0, entity = 0;
};
thread_local WriterTls t_writer;

struct WriterCall {
    WriterOut* out;
    uint8_t* args;
    uint64_t result;
};
void writer_before(void* p) {
    auto* c = static_cast<WriterCall*>(p);
    const uint32_t mask = g_frame_fakes.load(std::memory_order_acquire);
    if (!mask) return;
    const uintptr_t world = *reinterpret_cast<const uintptr_t*>(c->args + kWriterWorld);
    const uintptr_t ctx = *reinterpret_cast<const uintptr_t*>(c->args + kWriterCtx);
    uint8_t pred = 0;
    const int m = machine_of_ctx(world, ctx, &pred);
    if (m < 0 || !is_fake(mask, static_cast<uint32_t>(m))) return;
    const uintptr_t entry = *reinterpret_cast<const uintptr_t*>(c->args + kWriterEntry);
    t_writer.seq = *reinterpret_cast<const uint32_t*>(c->args + kWriterSeq);
    t_writer.entity = *reinterpret_cast<const uint32_t*>(entry);
    t_writer.pred = pred;
    t_writer.machine = m;
}
uint64_t d_writer(WriterOut* out, uint8_t* args) {
    WriterCall c{out, args, 0};
    t_writer.machine = -1;
    const uint64_t r = detour(g_writer_guard, &c, &writer_before, nullptr, h_writer.original<WriterFn>(), out, args);
    t_writer.machine = -1;
    return r;
}

struct DeltaCall {
    const uint8_t* snap;
};
void delta_before(void* p) {
    const WriterTls w = t_writer;
    auto* c = static_cast<DeltaCall*>(p);
    if (w.machine < 0 || !c->snap) return;
    Snaps& s = g_snap[w.machine][w.pred & 1];
    const uint32_t len = *reinterpret_cast<const uint16_t*>(c->snap);
    if (!s.bytes || s.n >= kSnapRows || len < kStateHeader || len > kSnapBytes - s.used) {
        ++s.dropped;
        return;
    }
    s.rows[s.n++] = Snaps::Row{w.seq, w.entity, s.used, len};
    std::memcpy(s.bytes + s.used, c->snap, len);
    s.used += len;
}
uint64_t d_delta(void* bits, uintptr_t registry, const uint8_t* snap, const uint8_t* base, uint64_t tmpl_flag, void* base_ref,
                 uint64_t pred) {
    DeltaCall c{snap};
    return detour(g_delta_guard, &c, &delta_before, nullptr, h_delta.original<DeltaFn>(), bits, registry, snap, base,
                  tmpl_flag, base_ref, pred);
}

// ---- the client side, on the tick thread ----
struct Header {
    uint32_t kind, tick, seq, entity, base;
    bool has_guid;
    uint8_t guid[16];
    uint32_t payload_len;
    const uint8_t* payload;
};
uint32_t u32_at(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
// An entity message (§2.3): false when it is not a create or update, or is cut short.
bool parse_header(const uint8_t* d, uint32_t n, Header* h) {
    if (n < 12) return false;
    const uint32_t w0 = u32_at(d);
    h->kind = w0 & 7;
    h->tick = w0 >> 3;
    h->seq = u32_at(d + 4);
    h->entity = u32_at(d + 8);
    h->has_guid = false;
    h->base = 0;
    uint32_t o = 12;
    if (h->kind == kKindCreate) {
        if (n < o + 4 + 16 + 1 + 2) return false;
        o += 4;  // parent
        std::memcpy(h->guid, d + o, 16);
        o += 16 + 1;
        h->has_guid = true;
    } else if (h->kind == kKindUpdate) {
        if (n < o + 4 + 2) return false;
        h->base = u32_at(d + o) & 0x7fffffffu;  // bit 31: the baseline is the acknowledged state A
        o += 4;
        if (h->base == 0) {
            if (n < o + 16 + 2) return false;
            std::memcpy(h->guid, d + o, 16);
            o += 16;
            h->has_guid = true;
        }
    } else {
        return false;
    }
    uint16_t len;
    std::memcpy(&len, d + o, 2);
    o += 2;
    if (o + len > n) return false;
    h->payload_len = len;
    h->payload = d + o;
    return true;
}

// The template map by GUID (R + spawn/registry.template_map): the GUID's first u32 picks the bucket, linear probing,
// full 16-byte compares.
uintptr_t seh_template_by_guid(uintptr_t registry, const uint8_t guid[16]) {
    __try {
        const uintptr_t map = registry + static_cast<uintptr_t>(L.reg_template_map);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(map + kMapCap);
        if (!cap || (cap & (cap - 1)) || !*reinterpret_cast<const uint32_t*>(map + kMapCount)) return 0;
        const auto* bits = *reinterpret_cast<const uint64_t* const*>(map + kMapBits);
        const auto* keys = *reinterpret_cast<const uint8_t* const*>(map + kMapKeys);
        const auto* vals = *reinterpret_cast<const uintptr_t* const*>(map + kMapValues);
        uint64_t i = u32_at(guid) & (cap - 1);
        for (uint64_t probes = 0; probes < cap; ++probes) {
            if (!((bits[i >> 6] >> (i & 63)) & 1)) return 0;
            if (std::memcmp(keys + i * 16, guid, 16) == 0) return vals[i];
            i = (i + 1) & (cap - 1);
        }
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// TemplateState with a fresh Holder (§2.7): the returned state is the caller's; every other block it recorded is freed.
uint8_t* seh_template_state(uintptr_t registry, uintptr_t template_info, uint8_t pred, unsigned long* code) {
    Holder holder{};
    holder.alloc = &g_alloc;
    TemplateArgs args{&holder, registry, template_info, pred, {}};
    StateRef out{};
    __try {
        reinterpret_cast<TemplateStateFn>(F.template_state)(&out, &args);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    for (uint8_t i = 0; i < holder.n && i < 4; ++i)
        if (holder.blocks[i] && holder.blocks[i] != out.state) a_free(&g_alloc, holder.blocks[i], 0);
    return out.state;
}

// Decode against `base` (borrowed): the result is the caller's, from Kiln's allocator. nullptr on a fault or failure.
uint8_t* seh_decode(uintptr_t registry, const uint8_t* payload, uint32_t len, const uint8_t* base, uint8_t pred, uint32_t tick,
                    unsigned long* code) {
    Slice s{payload, len};
    StateRef out{};
    __try {
        reinterpret_cast<DecodeFn>(F.decode)(&out, &g_alloc, &s, base, pred, registry, tick, nullptr);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    return out.state;
}

bool seh_ack_receive(uintptr_t world, uint32_t m, const uint8_t* msg, size_t len, uint8_t pred) {
    Slice s{msg, len};
    __try {
        reinterpret_cast<AckReceiveFn>(F.ack_receive)(world, m, &s, pred);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_reset(uintptr_t world, uint32_t m) {
    __try {
        reinterpret_cast<ResetFn>(F.reset_machine)(world, m);  // through the hook chain: the harness's link passes it on
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The components of a state that carry data (list 1): registry index and the data's offset in the blob.
struct Part {
    uint16_t index;
    uint32_t off;
    uint16_t size;
};
bool state_parts(const Registry& reg, const uint8_t* s, Part* out, uint32_t cap, uint32_t* n) {
    *n = 0;
    alignas(16) uint8_t index[kIndexBytes];
    if (!state_index(index, s, reg.address)) return false;
    uint16_t total, count;
    std::memcpy(&total, s, 2);
    std::memcpy(&count, s + 2, 2);
    uint32_t o = kStateHeader;
    for (uint32_t k = 0; k < count; ++k) {
        if (o + 2 > total || *n >= cap) return false;
        uint16_t i;
        std::memcpy(&i, s + o, 2);
        o += 2;
        if (i >= reg.count) return false;
        if (reg.at[i].flags & KILN_REPL_COMPONENT_VERSIONED) o += 1;
        uint16_t off;
        std::memcpy(&off, index + kIndexOffsets + i * 2, 2);
        const uint32_t at = static_cast<uint32_t>(kStateHeader) + off;
        if (at + reg.at[i].size > total) return false;
        out[(*n)++] = Part{i, at, reg.at[i].size};
    }
    return true;
}

void note(const char* fmt, ...) {
    if (g_first_notes >= 8) return;
    ++g_first_notes;
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    logf(KILN_LOG_WARN, "repl: lab: %s", buf);
}

// The decoded state against the snapshot the server coded for the same message.
void compare(const Registry& reg, const uint8_t* decoded, const uint8_t* snapshot, uint32_t entity, uint32_t tick) {
    Part a[kMaxComponents / 4], b[kMaxComponents / 4];
    uint32_t na = 0, nb = 0;
    if (!state_parts(reg, decoded, a, std::size(a), &na) || !state_parts(reg, snapshot, b, std::size(b), &nb) || na != nb) {
        ++C.differ;
        note("the decoded component set of entity %u (tick %u) is not the server's (%u vs %u)", entity, tick, na, nb);
        return;
    }
    for (uint32_t k = 0; k < na; ++k) {
        const bool same = a[k].index == b[k].index && std::memcmp(decoded + a[k].off, snapshot + b[k].off, a[k].size) == 0;
        if (reg.at[a[k].index].flags & KILN_REPL_COMPONENT_CUSTOM_CODEC) {
            ++(same ? C.custom_equal : C.custom_differ);
            continue;
        }
        if (same) {
            ++C.equal;
        } else {
            ++C.differ;
            note("component %s of entity %u (tick %u) differs from the server's snapshot", reg.names[a[k].index].c_str(), entity, tick);
        }
    }
}

void drain_one(uintptr_t world, const Registry* reg, uint32_t m, uint8_t pred) {
    Capture& cap = g_cap[m][pred];
    Snaps& snaps = g_snap[m][pred];
    Mirror& mir = g_mirror[m][pred];
    C.dropped += cap.dropped + snaps.dropped;
    C.messages += cap.n;
    std::vector<uint8_t> ack;
    ack.reserve(1 + cap.n * 5);
    ack.push_back(0);  // ack list (type 0): u32 first seq, then 0x00 + u32 per further seq
    for (uint32_t i = 0; i < cap.n; ++i) {
        const Capture::Msg& msg = cap.msgs[i];
        const uint8_t* d = cap.bytes + msg.off;
        if (ack.size() > 1) ack.push_back(0);
        for (int k = 0; k < 4; ++k) ack.push_back(static_cast<uint8_t>(msg.seq >> (8 * k)));
        if (msg.type == kMsgList) {  // a destroy list: the client forgets these entities
            ++C.destroy_lists;
            if (msg.len >= 10) {
                const uint32_t ids = std::min<uint32_t>(d[8] + d[9], (msg.len - 10) / 4);
                for (uint32_t k = 0; k < ids; ++k) mir.forget(u32_at(d + 10 + 4 * k));
            }
            continue;
        }
        if (msg.type != kMsgEntity || !reg) continue;
        Header h{};
        if (!parse_header(d, msg.len, &h)) continue;
        // the baseline, as a client finds it
        uint8_t* tmpl = nullptr;
        const uint8_t* base = nullptr;
        unsigned long code = 0;
        if (h.has_guid) {
            const uintptr_t ti = seh_template_by_guid(reg->address, h.guid);
            if (!ti) {
                ++C.no_baseline;
                continue;
            }
            tmpl = seh_template_state(reg->address, ti, pred, &code);
            if (!tmpl) {
                ++C.decode_faults;
                note("the template state of entity %u faulted (0x%lx)", h.entity, code);
                continue;
            }
            base = tmpl;
        } else {
            base = mir.get(h.entity, h.base);
            if (!base) {
                ++C.no_baseline;
                continue;
            }
        }
        uint8_t* state = seh_decode(reg->address, h.payload, h.payload_len, base, pred, h.tick, &code);
        if (tmpl) a_free(&g_alloc, tmpl, 0);
        if (!state) {
            ++C.decode_faults;
            note("decoding entity %u (tick %u) failed (0x%lx)", h.entity, h.tick, code);
            continue;
        }
        ++C.decoded;
        const Snaps::Row* row = nullptr;
        for (uint32_t k = 0; k < snaps.n; ++k)
            if (snaps.rows[k].seq == h.seq && snaps.rows[k].entity == h.entity) {
                row = &snaps.rows[k];
                break;
            }
        if (row) compare(*reg, state, snaps.bytes + row->off, h.entity, h.tick);
        mir.put(h.entity, h.tick, state);
    }
    if (cap.n) {
        if (seh_ack_receive(world, m, ack.data(), ack.size(), pred)) C.acked += cap.n;
        else note("the game's ack handler faulted for fake machine %u", m);
    }
    cap.n = cap.used = cap.dropped = 0;
    snaps.n = snaps.used = snaps.dropped = 0;
}

void drain(uintptr_t world) {
    if (!g_pending.exchange(false, std::memory_order_relaxed) || !world) return;
    const uint32_t mask = g_frame_fakes.load(std::memory_order_acquire) | g_fakes.load(std::memory_order_acquire);
    const Registry* reg = registry();
    for (uint32_t m = 0; m < kMachines; ++m)
        if (is_fake(mask, m))
            for (uint8_t p = 0; p < 2; ++p) drain_one(world, reg, m, p);
}

// ---- buffers and hooks ----
void free_buffers(uint32_t m) {
    for (int p = 0; p < 2; ++p) {
        delete[] g_cap[m][p].bytes;
        delete[] g_cap[m][p].msgs;
        delete[] g_snap[m][p].bytes;
        delete[] g_snap[m][p].rows;
        g_cap[m][p] = Capture{};
        g_snap[m][p] = Snaps{};
        g_mirror[m][p].clear();
    }
    g_forced_n[m].store(0, std::memory_order_release);
}

bool alloc_buffers(uint32_t m) {
    for (int p = 0; p < 2; ++p) {
        auto* cb = new (std::nothrow) uint8_t[kCaptureBytes];
        auto* cm = new (std::nothrow) Capture::Msg[kCaptureMsgs];
        auto* sb = new (std::nothrow) uint8_t[kSnapBytes];
        auto* sr = new (std::nothrow) Snaps::Row[kSnapRows];
        g_cap[m][p] = Capture{cb, 0, cm, 0, 0};
        g_snap[m][p] = Snaps{sb, 0, sr, 0, 0};
        if (!cb || !cm || !sb || !sr) return false;
    }
    return true;
}

void unhook() {
    for (kiln::RawHook* h : {&h_prepare, &h_reset, &h_reldriver, &h_relstep, &h_callback, &h_writer, &h_delta}) h->revoke();
    g_hooked = false;
}

bool hook(std::string* why) {
    if (g_hooked) return true;
    // Innermost (lowest priority): another link on these functions sees the game's call first, also for a fake.
    constexpr int32_t kLast = -1000;
    struct One {
        kiln::RawHook* h;
        uintptr_t target;
        void* fn;
        const char* label;
    } all[] = {
        {&h_prepare, F.prepare, reinterpret_cast<void*>(&d_prepare), "repl/lab Prepare"},
        {&h_reset, F.reset_machine, reinterpret_cast<void*>(&d_reset), "repl/lab ResetMachine"},
        {&h_reldriver, F.relevance_driver, reinterpret_cast<void*>(&d_reldriver), "repl/lab RelevanceDriver"},
        {&h_relstep, F.relevance_step, reinterpret_cast<void*>(&d_relstep), "repl/lab RelevanceStep"},
        {&h_callback, F.send_callback, reinterpret_cast<void*>(&d_callback), "repl/lab SendCallback"},
        {&h_writer, F.entity_writer, reinterpret_cast<void*>(&d_writer), "repl/lab EntityWriter"},
        {&h_delta, F.delta_writer, reinterpret_cast<void*>(&d_delta), "repl/lab DeltaWriter"},
    };
    for (const One& o : all)
        if (!o.h->install(g_api, g_owner, o.target, o.fn, o.label, kLast)) {
            *why = std::string(o.label) + ": " + (o.h->error() ? o.h->error() : "?");
            unhook();
            return false;
        }
    g_hooked = true;
    return true;
}

// The fakes go: the game frees each object and its slot bit (through its own ResetMachine) while the world is intact.
void release(uintptr_t world, bool call_game) {
    drain(world);
    const uint32_t mask = g_fakes.exchange(0, std::memory_order_acq_rel);
    g_frame_fakes.store(0, std::memory_order_release);
    for (uint32_t m = 0; m < kMachines; ++m) {
        if (!is_fake(mask, m)) continue;
        if (call_game && world) {
            const bool ok = seh_reset(world, m);
            const uintptr_t obj = object_of(world, m);
            logf(KILN_LOG_INFO, "repl: lab: released fake machine %u through the game's reset: %s; object active byte now %u, slot mask 0x%04x",
                 m, ok ? "ok" : "FAULT", obj ? read<uint8_t>(obj + kObjActive, 0xff) : 0xff,
                 read<uint16_t>(world + static_cast<uintptr_t>(L.world_slot_mask)));
        }
        free_buffers(m);
    }
    if (g_hooked) unhook();
    if (const uint64_t traps = g_alloc_traps.load(); traps) logf(KILN_LOG_WARN, "repl: lab: the allocator's other slots were called %llu times", static_cast<unsigned long long>(traps));
}

bool policy_any_from_config(uint32_t side) {
    try {
        char b[1024];
        size_t n = 0;
        if (g_api->path(KILN_PATH_KILN, b, sizeof b, &n) != KILN_OK) return false;
        const std::string dir(b, n);
        const std::filesystem::path p = std::filesystem::path(std::u8string(dir.begin(), dir.end())) / "kiln.json";
        std::ifstream f(p);
        if (!f) return false;
        const auto j = nlohmann::json::parse(f, nullptr, false, true);
        if (!j.is_object()) return false;
        // the core's rule (native/core/src/config.cpp): the top level, then the side section replaces it
        bool any = j.contains("policy") && j["policy"].is_string() && j["policy"].get<std::string>() == "any";
        const char* section = side == KILN_SIDE_CLIENT ? "client" : "server";
        if (j.contains(section) && j[section].is_object() && j[section].contains("policy") && j[section]["policy"].is_string())
            any = j[section]["policy"].get<std::string>() == "any";
        return any;
    } catch (...) {
        return false;
    }
}

KilnResult gate() {
    if (!g_allowed) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    return KILN_OK;
}

}  // namespace

// ---- start ----
bool lab_init() {
    for (auto& v : g_vtbl) v = reinterpret_cast<void*>(&a_trap);
    g_vtbl[1] = reinterpret_cast<void*>(&a_alloc);
    g_vtbl[2] = reinterpret_cast<void*>(&a_free);
    for (Guard* g : {&g_harness_guard, &g_writer_guard, &g_delta_guard}) add_guard(g);
    auto sym = [](const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK; };
    auto cst = [](const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; };
    g_resolved = g_api->feature(KilnStr{"repl.loopback", 13}) && sym("repl/Prepare", &F.prepare) &&
                 sym("repl/ResetMachine", &F.reset_machine) && sym("repl/RelevanceDriver", &F.relevance_driver) &&
                 sym("net/RelevanceStep", &F.relevance_step) && sym("repl/AckProcess", &F.ack_process) &&
                 sym("repl/RecordSent", &F.record_sent) && sym("repl/TemplateState", &F.template_state) &&
                 sym("repl/Decode", &F.decode) && sym("repl/SendCallback", &F.send_callback) &&
                 sym("repl/EntityWriter", &F.entity_writer) && sym("repl/DeltaWriter", &F.delta_writer) &&
                 sym("repl/StateIndex", &F.state_index) && sym("repl/AckReceive", &F.ack_receive) &&
                 cst("repl/world.objects", &L.world_objects) && cst("repl/obj.stride", &L.obj_stride) &&
                 cst("repl/obj.client_ctx", &L.obj_client_ctx) && cst("repl/obj.pred_ctx", &L.obj_pred_ctx) &&
                 cst("repl/world.slot_mask", &L.world_slot_mask) && cst("repl/ctx.heap", &L.ctx_heap) &&
                 cst("net/gs.machines", &L.gs_machines) && cst("spawn/registry.template_map", &L.reg_template_map) &&
                 L.world_storage > 0 && L.obj_stride > 0;
    if (g_resolved) {
        // the template map has the engine map's layout (spawn's own check)
        int64_t cap = -1, values = -1;
        g_resolved = cst("spawn/registry.template_map_cap", &cap) && cst("spawn/registry.template_map_values", &values) &&
                     cap == L.reg_template_map + kMapCap && values == L.reg_template_map + kMapValues;
    }
    return g_resolved;
}

void lab_ready(uint32_t side) {
    g_net = static_cast<const KilnNetApi*>(g_api->service(KilnStr{KILN_NET_SERVICE, sizeof(KILN_NET_SERVICE) - 1}, 1));
    KilnNetStatus st{};
    st.size = sizeof st;
    g_lab_port = g_net && g_net->status(&st) == KILN_OK && st.lab ? st.lab_port : 0;
    g_policy_any = policy_any_from_config(side);
    g_allowed = g_resolved && g_policy_any && g_lab_port != 0;
    if (!g_resolved) {
        g_why_off = "feature repl.loopback is off on this build";
    } else if (!g_policy_any || !g_lab_port) {
        g_why_off = std::string("lab servers only: needs ") + (!g_policy_any ? "kiln.json policy \"any\"" : "") +
                    (!g_policy_any && !g_lab_port ? " and " : "") + (!g_lab_port ? "kiln.net's lab port (labPort in kiln/config/network.json)" : "");
        const KilnCapsApi* caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
        if (caps)
            caps->report(g_owner, KilnStr{"repl.loopback", 13}, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY,
                         KilnStr{g_why_off.data(), g_why_off.size()});
    }
    if (g_allowed) logf(KILN_LOG_WARN, "repl: loopback on (lab port %u, policy any; tests only)", g_lab_port);
    else logf(KILN_LOG_INFO, "repl: loopback off (%s)", g_why_off.c_str());
}

bool lab_allowed() { return g_allowed; }

void lab_pre(uintptr_t world) {
    if (!g_allowed) return;
    drain(world);
    if (g_fakes.load() && ++g_pres == 120) {
        // does a fake put the sim's programs on the workers? The world steps them in parallel only while its mask of
        // live player entities is not 0 (chapter 06a §2.4), and a fake has no player entity.
        logf(KILN_LOG_INFO, "repl: lab: %u Pres with fakes 0x%05x: players mask (storage+0x7ddc0a) 0x%04x, slot mask 0x%04x",
             g_pres, g_fakes.load(), read<uint16_t>(world + static_cast<uintptr_t>(L.world_storage) + kStoragePlayersMask, 0xffff),
             read<uint16_t>(world + static_cast<uintptr_t>(L.world_slot_mask), 0xffff));
    }
    if (g_broken.load() && g_fakes.load()) {
        logf(KILN_LOG_WARN, "repl: lab: the harness faulted; its fakes are released");
        release(world, true);
    }
}

void lab_world_stopping() {
    if (!g_fakes.load() && !g_hooked) return;
    release(0, false);  // the world is going: its objects go with it; only Kiln's buffers and states are freed here
}

uint32_t lab_fake_mask() { return g_fakes.load(std::memory_order_acquire); }

uint32_t lab_peer_of(uint32_t machine, uint8_t* flags) {
    *flags = KILN_REPL_PEER_FAKE;
    uint64_t bound = 0;
    if (g_net && g_net->header.size >= offsetof(KilnNetApi, lab_selftest) + sizeof(g_net->lab_selftest) &&
        g_net->lab_selftest(KILN_NET_SELFTEST_BIND_MACHINE, 0, machine, &bound) == KILN_OK && bound) {
        *flags |= KILN_REPL_PEER_BOUND;
        return static_cast<uint32_t>(bound);
    }
    return KILN_REPL_FAKE_PEER | machine;
}

// ---- the table's lab functions ----
KilnResult KILN_CALL lab_add_fakes(uint32_t count, KilnReplFake* out, uint32_t cap, uint32_t* added) {
    if (added) *added = 0;
    if (KilnResult rc = gate(); rc != KILN_OK) return rc;
    if (count < 1 || count > 16 || g_fakes.load()) return KILN_ERR_INVALID_ARGUMENT;
    const uintptr_t world = g_world.load(), gs = g_gs.load();
    if (!world || !gs || g_broken.load()) return KILN_ERR_UNAVAILABLE;
    // free machines (no peer in the GameServer's slot, object inactive) from 16 down; never 0 (the server itself, or a
    // hosting client); free player slots from 15 down (not in the slot mask, not an active object's)
    const uint16_t slot_mask = read<uint16_t>(world + static_cast<uintptr_t>(L.world_slot_mask));
    uint32_t used_slots = slot_mask;
    for (uint32_t m = 0; m < kMachines; ++m) {
        const uintptr_t obj = object_of(world, m);
        if (obj && read<uint8_t>(obj + kObjActive)) used_slots |= 1u << (read<uint8_t>(obj + kObjSlot) & 15);
    }
    KilnReplFake picked[16];
    uint32_t n = 0;
    for (uint32_t m = kMachines - 1, s = 15; m >= 1 && n < count; --m) {
        const uintptr_t obj = object_of(world, m);
        if (!obj || read<uint32_t>(gs + static_cast<uintptr_t>(L.gs_machines) + m * kMachineSlotStride) != 0 ||
            read<uint8_t>(obj + kObjActive, 1) != 0)
            continue;
        while (s < 16 && ((used_slots >> s) & 1)) --s;
        if (s >= 16) break;
        picked[n++] = KilnReplFake{m, s};
        used_slots |= 1u << s;
    }
    if (n < count) return KILN_ERR_NOT_FOUND;
    std::string why;
    if (!hook(&why)) {
        logf(KILN_LOG_WARN, "repl: lab: the harness could not hook the game (%s)", why.c_str());
        return KILN_ERR_UNAVAILABLE;
    }
    uint32_t mask = 0;
    for (uint32_t i = 0; i < n; ++i) {
        const uint32_t m = picked[i].machine;
        if (!alloc_buffers(m)) {
            for (uint32_t k = 0; k <= i; ++k) free_buffers(picked[k].machine);
            unhook();
            return KILN_ERR_UNAVAILABLE;
        }
        g_slot_of[m] = static_cast<uint8_t>(picked[i].slot);
        g_forced_n[m].store(0, std::memory_order_relaxed);
        mask |= 1u << m;
    }
    C = Counters{};
    g_first_notes = 0;
    g_pres = 0;
    g_pending.store(false);
    g_fakes.store(mask, std::memory_order_release);
    std::string list;
    for (uint32_t i = 0; i < n; ++i) {
        if (out && i < cap) out[i] = picked[i];
        list += (i ? ", " : "") + std::to_string(picked[i].machine) + "/" + std::to_string(picked[i].slot);
    }
    if (added) *added = n;
    logf(KILN_LOG_INFO, "repl: lab: %u fake players (machine/slot %s)", n, list.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL lab_force_relevant(uint32_t machine, KilnEntity entity) {
    if (KilnResult rc = gate(); rc != KILN_OK) return rc;
    if (!entity || !is_fake(g_fakes.load(), machine)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t n = g_forced_n[machine].load(std::memory_order_relaxed);
    for (uint32_t k = 0; k < n; ++k)
        if (g_forced[machine][k] == entity) return KILN_OK;
    if (n >= kMaxForced) return KILN_ERR_INVALID_ARGUMENT;
    g_forced[machine][n] = entity;
    g_forced_n[machine].store(n + 1, std::memory_order_release);
    return KILN_OK;
}

KilnResult KILN_CALL lab_remove_fakes() {
    if (KilnResult rc = gate(); rc != KILN_OK) return rc;
    release(g_world.load(), true);
    return KILN_OK;
}

KilnResult KILN_CALL lab_stats(KilnReplLoopbackStats* out) {
    if (!out || out->size < sizeof(KilnReplLoopbackStats)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = gate(); rc != KILN_OK) return rc;
    const uintptr_t world = g_world.load();
    drain(world);
    out->fakes = g_fakes.load();
    out->messages = C.messages;
    out->decoded = C.decoded;
    out->components_equal = C.equal;
    out->components_differ = C.differ;
    out->custom_equal = C.custom_equal;
    out->custom_differ = C.custom_differ;
    out->decode_faults = C.decode_faults;
    out->no_baseline = C.no_baseline;
    out->acked = C.acked;
    out->dropped = C.dropped;
    out->destroy_lists = C.destroy_lists;
    out->active_objects = 0;
    out->slot_mask = world ? read<uint16_t>(world + static_cast<uintptr_t>(L.world_slot_mask)) : 0;
    for (uint32_t m = 0; m < kMachines && world; ++m) {
        const uintptr_t obj = object_of(world, m);
        if (obj && read<uint8_t>(obj + kObjActive)) out->active_objects |= 1u << m;
    }
    return KILN_OK;
}

KilnResult KILN_CALL lab_decoded(uint32_t machine, KilnEntity entity, uint32_t hash, uint8_t* out, uint32_t cap, uint32_t* len,
                                 uint32_t* tick) {
    if (len) *len = 0;
    if (tick) *tick = 0;
    if (KilnResult rc = gate(); rc != KILN_OK) return rc;
    if (machine >= kMachines || !entity || !len) return KILN_ERR_INVALID_ARGUMENT;
    drain(g_world.load());
    const Registry* reg = registry();
    uint32_t t = 0;
    const uint8_t* state = reg ? g_mirror[machine][0].newest(entity, &t) : nullptr;
    if (!state) return KILN_ERR_NOT_FOUND;
    Part parts[kMaxComponents / 4];
    uint32_t n = 0;
    if (!state_parts(*reg, state, parts, std::size(parts), &n)) return KILN_ERR_NOT_FOUND;
    for (uint32_t k = 0; k < n; ++k) {
        if (reg->at[parts[k].index].hash != hash) continue;
        *len = parts[k].size;
        if (tick) *tick = t;
        if (!out) return KILN_OK;
        if (cap < parts[k].size) return KILN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out, state + parts[k].off, parts[k].size);
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

}  // namespace repl
