// SPDX-License-Identifier: MIT
// The adapter's side of the host interface (kfc_host.h): kfc-runtime asks Kiln to find its six engine-hook places in the
// exe file's bytes and to hook them with kiln.hooks v2. Without this, the runtime scans live code and refuses a place
// that another DLL already changed.
//
//   find_signature    kiln.hooks scan(): sigdb's scanner over the exe FILE's code, hits translated to live addresses.
//                     The profile's original bytes are checked against the same file bytes (scan_hole reads them).
//   install_hook      kiln.hooks mid2 at the place, owner = this plugin, label "shroudforge/<hook name>". The mid link
//                     reproduces what the runtime's own trampoline did: call the runtime's callback with the registers
//                     of the hook's kind, then run the displaced instruction(s) (Kiln relocates them).
//   remove_hook       revoke. Per-hook state is never freed (a thread may still be inside the handler).
//
// Hot-path rule: the handlers below take no locks and allocate nothing.
#include <cstdio>
#include <cstring>
#include <new>
#include <string>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "services/kiln_hooks.h"
#include "kfc_host.h"
#include "sf_internal.h"

namespace ksf {

namespace {

const KilnHooksApi* g_hooks = nullptr;
KilnOwner g_owner = 0;

struct HookState {
    uint32_t kind;
    void* callback;
    uintptr_t extra;
};

void KILN_CALL on_plain(KilnRegisters* r, void* user) {
    const auto* s = static_cast<const HookState*>(user);
    reinterpret_cast<void(__cdecl*)(void*, void*)>(s->callback)(reinterpret_cast<void*>(r->r15),
                                                                 reinterpret_cast<void*>(r->rbp));
}
void KILN_CALL on_context(KilnRegisters* r, void* user) {
    const auto* s = static_cast<const HookState*>(user);
    reinterpret_cast<void(__cdecl*)(void*, void*)>(s->callback)(reinterpret_cast<void*>(r->rcx), nullptr);
}
void KILN_CALL on_cursor(KilnRegisters* r, void* user) {
    const auto* s = static_cast<const HookState*>(user);
    reinterpret_cast<void(__cdecl*)(void*, void*, void*)>(s->callback)(reinterpret_cast<void*>(r->r14 + s->extra),
                                                                        reinterpret_cast<void*>(r->r13),
                                                                        reinterpret_cast<void*>(r->rbp));
}

// The exe file's bytes at a live address start with expected[0..n)? scan_hole() reads a hole's value from the bytes the
// scanner uses (the file's code, else the live code), so "{h0:d32} {h1:d32} .. 00" read at `at` returns them four at a time.
bool file_bytes_start_with(uintptr_t at, const unsigned char* expected, size_t n) {
    if (!n) return true;
    if (!expected || n > 32) return false;
    const size_t words = (n + 3) / 4;
    std::string pattern;
    for (size_t i = 0; i < words; ++i) pattern += "{h" + std::to_string(i) + ":d32} ";
    pattern += "00";  // the pattern needs one literal byte; its value is not checked
    unsigned char got[32 + 4];
    for (size_t i = 0; i < words; ++i) {
        const std::string hole = "h" + std::to_string(i);
        int64_t v = 0;
        if (g_hooks->scan_hole(KilnStr{pattern.data(), pattern.size()}, at, KilnStr{hole.data(), hole.size()}, &v) != KILN_OK)
            return false;
        const uint32_t word = static_cast<uint32_t>(v);
        std::memcpy(got + 4 * i, &word, 4);
    }
    return std::memcmp(got, expected, n) == 0;
}

uint32_t __cdecl find_signature(const char* signature, const unsigned char* expected, size_t expected_size,
                                uintptr_t* address) {
    if (!g_hooks || !signature || !address) return kUnavailable;
    uintptr_t hits[2] = {};
    uint32_t count = 0;
    const KilnResult rc = g_hooks->scan(KilnStr{signature, std::strlen(signature)}, hits, 2, &count);
    if (rc == KILN_ERR_NOT_FOUND || rc == KILN_ERR_INVALID_ARGUMENT) return kNotFound;  // a signature sigdb cannot read: no place
    if (rc == KILN_ERR_BUFFER_TOO_SMALL || (rc == KILN_OK && count > 1)) return kAmbiguous;
    if (rc != KILN_OK || count != 1) return kUnavailable;
    if (!file_bytes_start_with(hits[0], expected, expected_size)) return kMismatch;
    *address = hits[0];
    return kFound;
}

uint64_t __cdecl install_hook(const char* name, uintptr_t target, uint32_t kind, void* callback, uintptr_t extra) {
    if (!g_hooks || !callback || kind > kHookCursor) return 0;
    if (!name || !*name) name = "hook";
    char label[96];
    const int n = std::snprintf(label, sizeof label, "shroudforge/%s", name);
    const size_t label_len = n < 0 ? 0 : n < static_cast<int>(sizeof label) ? static_cast<size_t>(n) : sizeof label - 1;
    auto* state = new (std::nothrow) HookState{kind, callback, extra};  // never freed: a thread may be inside the handler
    if (!state) return 0;
    const KilnHookOrder order{sizeof(KilnHookOrder), 0, KilnStr{label, label_len}, {}, {}};
    KilnMidFn fn = kind == kHookCursor ? &on_cursor : kind == kHookContext ? &on_context : &on_plain;
    KilnHandle handle = 0;
    const KilnResult rc = g_hooks->mid2(g_owner, target, &order, fn, state, &handle);
    if (rc != KILN_OK || !handle) {
        logf(KILN_LOG_WARN, "kiln.hooks refused ShroudForge's %s hook at rva 0x%llx (result %d)", name,
             static_cast<unsigned long long>(target - g_hooks->image_base()), static_cast<int>(rc));
        delete state;  // mid2 failed: nothing can reach it
        return 0;
    }
    logf(KILN_LOG_INFO, "ShroudForge engine hook %s at rva 0x%llx placed through kiln.hooks", name,
         static_cast<unsigned long long>(target - g_hooks->image_base()));
    return handle;
}

void __cdecl remove_hook(uint64_t handle) {
    if (g_hooks && handle) g_hooks->revoke(handle);
}

KfcHost g_table = {sizeof(KfcHost), kHostVersion, &find_signature, &install_hook, &remove_hook};

}  // namespace

const KfcHost* host_table(const KilnApi* api, KilnOwner owner) {
    const KilnHooksApi* hooks = kiln::hooks_v2(api);
    if (!hooks || !hooks->scan || !hooks->scan_hole || !hooks->mid2 || !hooks->revoke || !hooks->image_base) return nullptr;
    g_hooks = hooks;
    g_owner = owner;
    return &g_table;
}

}  // namespace ksf
