// SPDX-License-Identifier: MIT
// The host interface that the bundled ShroudForge native runtime (kfc-runtime.dll, the frostdev-ops fork) accepts through
// its optional export KfcRuntimeSetHost, declared here from the interface's documentation: this file holds the table's
// layout and constants only, no ShroudForge code. docs/kiln/shroudforge-compat.md ("Engine hooks through kiln.hooks").
//
// What it is for: kfc-runtime hooks up to six places in the game's code. Alone it needs their live bytes to be the
// originals, so a DLL that hooked one first turns the runtime off. With this table it finds them in the exe FILE's bytes
// and places them with kiln.hooks, which chains over the other DLL's jump.
#pragma once
#include <stddef.h>
#include <stdint.h>

namespace ksf {

constexpr uint32_t kHostVersion = 1;

// Which registers a hook hands to its callback (the callback runs before the instruction at the hook place):
enum HostHookKind : uint32_t {
    kHookPlain = 0,    // callback(r15, rbp)
    kHookContext = 1,  // callback(rcx, 0)
    kHookCursor = 2,   // callback(r14 + extra, r13, rbp)
};

// find_signature results
enum HostFind : uint32_t {
    kFound = 1,        // exactly one match; *address is its live address
    kNotFound = 0,
    kAmbiguous = 2,
    kMismatch = 3,     // one match, but the file's bytes there do not start with `expected`
    kUnavailable = 4,
};

struct KfcHost {
    uint32_t size;     // sizeof(KfcHost)
    uint32_t version;  // kHostVersion
    uint32_t(__cdecl* find_signature)(const char* signature, const unsigned char* expected, size_t expected_size,
                                      uintptr_t* address);
    uint64_t(__cdecl* install_hook)(const char* name, uintptr_t target, uint32_t kind, void* callback, uintptr_t extra);
    void(__cdecl* remove_hook)(uint64_t handle);
};

using KfcSetHost = bool(__cdecl*)(const KfcHost*);

}  // namespace ksf
