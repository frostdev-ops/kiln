/* SPDX-License-Identifier: MIT
 * kiln.hooks (in-core service): the lowest tier of Forge parity — detour and patch arbitrary game code safely.
 *
 * This is Kiln's Mixin/coremod/access-transformer layer. A consumer (a native plugin, or managed Kiln.Hooks on a
 * mod's behalf) can:
 *   - install an inline detour with a callable original and a priority chain on one target (like a Mixin @Inject
 *     head/tail with cancellation), or a mid-function hook with full register context (like a bytecode @At),
 *   - read and write process memory under a structured-exception guard, and apply byte patches that are restored
 *     automatically when their owner is revoked (access transformers),
 *   - scan the image with sigdb pattern syntax (holes included) and look up sigdb symbols/constants (core and
 *     mod-shipped fragments) plus the image base.
 *
 * Everything is owner-scoped. revoke_owner(owner) removes every hook the owner installed (the detour stays in place
 * but the owner's handlers leave the chain atomically, so a dead mod is never called) and restores every byte patch.
 * Managed Kiln.Hooks calls revoke_owner from a mod's Retiring event.
 *
 * Threads: a detour/mid handler runs on whatever thread the target runs on (often the tick thread, but job threads
 * too). Installing a hook freezes the other threads (safetyhook); do it on the tick thread or at mod load.
 */
#ifndef KILN_HOOKS_H
#define KILN_HOOKS_H

#include "../kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_HOOKS_SERVICE "kiln.hooks"
#define KILN_HOOKS_VERSION 1u

/* A detour call in progress. Passed to every detour handler and back to call_original. The handler may change args
 * before calling the original, and may change the return value after. `reserved` is the native chain cursor: do not
 * touch it, just pass the same KilnHookCall* to call_original. */
typedef struct KilnHookCall {
    uint32_t size;
    uint32_t arg_count;   /* number of valid entries in args (4 on x64) */
    uint64_t hook;        /* the KilnHandle of the hook whose handler is running */
    uintptr_t args[4];    /* integer/pointer args: rcx, rdx, r8, r9 — read/write */
    void* reserved;       /* opaque: the chain cursor */
} KilnHookCall;

/* Detour handler. Return the value the hooked function should yield. Call api->call_original(call) to run the rest
 * of the chain and then the real function (optionally after changing call->args); skip it to replace the function
 * entirely. Runs under an SEH guard: a fault disables this handler, the chain continues. */
typedef uintptr_t(KILN_CALL* KilnDetourFn)(KilnHookCall* call, void* user);

/* Full register context of a mid-function hook. Field order mirrors safetyhook's Context64 exactly, so there is no
 * copy; writing a field changes the register when execution resumes. rsp is read-only (use trampoline_rsp per
 * safetyhook's rules); rip points into a trampoline of the overwritten instruction(s). Each xmm is 16 bytes. */
typedef struct KilnXmm {
    uint8_t bytes[16];
} KilnXmm;

typedef struct KilnRegisters {
    KilnXmm xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7;
    KilnXmm xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15;
    uintptr_t rflags, r15, r14, r13, r12, r11, r10, r9, r8, rdi, rsi, rdx, rcx, rbx, rax, rbp, rsp, trampoline_rsp,
        rip;
} KilnRegisters;

/* Mid-hook handler. Reads and writes registers in place; chained by priority (every handler on the target sees the
 * same registers in turn). Runs under an SEH guard. */
typedef void(KILN_CALL* KilnMidFn)(KilnRegisters* regs, void* user);

/* Result of resolving a mod-shipped symbol fragment against the running exe. */
typedef struct KilnHooksSigSummary {
    uint32_t size;
    uint32_t symbols, symbols_ok;
    uint32_t constants, constants_ok;
    uint32_t features, features_ok;
} KilnHooksSigSummary;

typedef struct KilnHooksStats {
    uint32_t size;
    uint32_t detour_targets;   /* distinct addresses with an inline detour installed */
    uint32_t mid_targets;      /* distinct addresses with a mid hook installed */
    uint32_t handlers;         /* live detour + mid handlers across all chains */
    uint32_t patches;          /* live byte patches */
    uint64_t detour_calls;     /* total detour dispatches since boot */
    uint64_t mid_calls;        /* total mid dispatches since boot */
} KilnHooksStats;

typedef struct KilnHooksApi {
    KilnServiceHeader header;

    /* ---- image ---- */
    uintptr_t(KILN_CALL* image_base)(void);
    size_t(KILN_CALL* image_size)(void);

    /* ---- sigdb (core definitions, then the fragment named by the "<modid>/" prefix) ---- */
    KilnResult(KILN_CALL* symbol)(KilnStr name, uintptr_t* out);
    KilnResult(KILN_CALL* constant)(KilnStr name, int64_t* out);
    /* Scans the image's .text with sigdb pattern syntax (holes act as wildcards). Writes up to `cap` match
     * addresses into `out`; *count receives the total number found (which can exceed cap — then KILN_ERR_BUFFER_
     * TOO_SMALL and out holds the first cap). KILN_ERR_NOT_FOUND when there are none. */
    KilnResult(KILN_CALL* scan)(KilnStr pattern, uintptr_t* out, uint32_t cap, uint32_t* count);
    /* Reads a named hole from a pattern matched at `match` (rel32 holes give the absolute target). */
    KilnResult(KILN_CALL* scan_hole)(KilnStr pattern, uintptr_t match, KilnStr hole, int64_t* out);

    /* ---- mod-shipped symbol fragments ----
     * Loads a JSON sigdb file (same format as sigdb/server.json) as fragment <modid>; every name it defines or
     * captures must be prefixed "<modid>/". Resolved against the running exe with the usual rules (votes,
     * uniqueness) and written to kiln/logs/sigdb-<modid>.json. Look its names up with symbol()/constant().
     * unload_symbols drops the fragment (call when the mod unloads). */
    KilnResult(KILN_CALL* load_symbols)(KilnOwner owner, KilnStr modid, KilnStr json_path,
                                        KilnHooksSigSummary* summary);
    KilnResult(KILN_CALL* unload_symbols)(KilnStr modid);

    /* ---- memory (SEH-guarded) ---- */
    KilnResult(KILN_CALL* read)(uintptr_t addr, void* dst, size_t n);
    KilnResult(KILN_CALL* write)(uintptr_t addr, const void* src, size_t n);
    /* Overwrites n bytes at addr, remembering the originals; restored on unpatch, revoke or revoke_owner. */
    KilnResult(KILN_CALL* patch)(KilnOwner owner, uintptr_t addr, const void* bytes, size_t n, KilnHandle* out);
    KilnResult(KILN_CALL* unpatch)(KilnHandle h);

    /* ---- hooks ----
     * detour: install (or join) an inline detour on `target`; higher priority runs first. mid: a mid-function hook
     * at `target`. Both are owner-scoped. call_original runs the rest of a detour's chain and then the real
     * function. revoke removes one hook or patch; revoke_owner removes everything an owner installed. */
    KilnResult(KILN_CALL* detour)(KilnOwner owner, uintptr_t target, int32_t priority, KilnDetourFn fn, void* user,
                                  KilnHandle* out);
    KilnResult(KILN_CALL* mid)(KilnOwner owner, uintptr_t target, int32_t priority, KilnMidFn fn, void* user,
                               KilnHandle* out);
    uintptr_t(KILN_CALL* call_original)(KilnHookCall* call);
    KilnResult(KILN_CALL* revoke)(KilnHandle h);
    KilnResult(KILN_CALL* revoke_owner)(KilnOwner owner);

    /* ---- introspection ---- */
    KilnResult(KILN_CALL* stats)(KilnHooksStats* out);
} KilnHooksApi;

#ifdef __cplusplus
}
#endif

#endif /* KILN_HOOKS_H */
