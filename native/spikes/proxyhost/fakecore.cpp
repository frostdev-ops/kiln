// SPDX-License-Identifier: MIT
// A stand-in kiln-core.dll for proxyhost: kiln_boot writes the KilnBootInfo the proxy passed to the harness log.
#include "harness.h"
#include "kiln_api.h"

namespace {
const char* str(KilnStr s, char* buf, size_t cap) {
    size_t n = s.ptr ? (s.len < cap - 1 ? s.len : cap - 1) : 0;
    for (size_t i = 0; i < n; ++i) buf[i] = s.ptr[i];
    buf[n] = 0;
    return buf;
}
}  // namespace

extern "C" __declspec(dllexport) KilnResult KILN_CALL kiln_boot(const KilnBootInfo* info) {
    char a[1024], b[1024];
    if (!info) {
        ph_log("core boot info=null");
        return KILN_OK;
    }
    ph_log("core boot size=%u v1=%u", info->size, KILN_BOOT_INFO_V1_SIZE);
    if (!KILN_BOOT_INFO_HAS(info, play_once_profile)) return KILN_OK;
    ph_log("core play_once=%u nonce=%s profile=%s chain=%u", info->play_once, str(info->play_once_nonce, a, sizeof a),
           str(info->play_once_profile, b, sizeof b), info->chain_count);
    for (uint32_t i = 0; i < info->chain_count; ++i) {
        const KilnChainEntry& e = info->chain[i];
        ph_log("core chain %u status=%u source=%u error=%d detail=%u module=%d path=%s", i, e.status, e.source, e.error,
               e.detail, e.module ? 1 : 0, str(e.path, a, sizeof a));
    }
    const unsigned char* entry = ph_entry();
    ph_log("core entry bytes %02x %02x %02x %02x", entry[0], entry[1], entry[2], entry[3]);
    return KILN_OK;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(inst);
    return TRUE;
}
