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
 * too). The first hook on a target freezes the other threads briefly (safetyhook); do it on the tick thread or at mod
 * load. Adding or revoking further links on that target freezes nothing.
 *
 * v2 (dev.21): see the "v2" block at the end of KilnHooksApi and native/core/abi/kiln_hook.hpp (a header-only C++
 * client with a safetyhook-like surface).
 */
#ifndef KILN_HOOKS_H
#define KILN_HOOKS_H

#include "../kiln_api.h"

#ifdef __cplusplus
extern "C" {
#endif

#define KILN_HOOKS_SERVICE "kiln.hooks"
/* 2 (dev.21) = one registry that owns a single physical detour per target for every kind of handler: native-signature
 * "raw" links with continuations, structured 12-argument detours, ordered mid hooks, caller(), ordering constraints,
 * foreign-hook detection and the hook map. Appended after the v1 functions; v1 callers are unchanged. */
#define KILN_HOOKS_VERSION 2u

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

/* v2 structured detour call: up to 12 integer/pointer arguments (rcx, rdx, r8, r9, then the stack arguments) plus the
 * address the game call returns to. `arg_count` is the count the hook declared (<= 12); args beyond it are copied
 * through to the original as the caller left them. `reserved` is opaque: pass the same pointer to call_original2. */
typedef struct KilnHookCall2 {
    uint32_t size;
    uint32_t arg_count;
    uint64_t hook;        /* KilnHandle of the hook whose handler is running */
    uintptr_t args[12];   /* read/write: changes reach the rest of the chain and the real function */
    uintptr_t caller;     /* return address of the game call that entered the target on this thread */
    void* reserved;
} KilnHookCall2;

typedef uintptr_t(KILN_CALL* KilnDetourFn2)(KilnHookCall2* call, void* user);

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

/* Where a link goes in a target's chain (v2). Order on a target: constraints, then priority (higher first), then
 * registration order. `before` / `after` are comma-separated owner names (a plugin's or mod's id: "kiln.chat",
 * "emf:coolmod", "native:foo"; the kind prefix and a ".dll" suffix are optional, '-' and '.' are alike). An
 * unsatisfiable constraint is dropped with a warning in kiln.log. `label` names the link in kiln.log and the hook map. */
typedef struct KilnHookOrder {
    uint32_t size;       /* sizeof(KilnHookOrder) */
    int32_t priority;    /* higher runs first */
    KilnStr label;       /* e.g. "chat/ChatLogAdd" */
    KilnStr before;      /* owners this link must run before */
    KilnStr after;       /* owners this link must run after */
} KilnHookOrder;

typedef struct KilnHooksStats {
    uint32_t size;
    uint32_t detour_targets;   /* distinct addresses with a detour (raw, structured or v1) link */
    uint32_t mid_targets;      /* distinct addresses with a mid link */
    uint32_t handlers;         /* live links of every kind across all chains */
    uint32_t patches;          /* live byte patches */
    uint64_t detour_calls;     /* structured + v1 detour dispatches since boot (raw links are not counted) */
    uint64_t mid_calls;        /* mid dispatches since boot */
    /* ---- appended in v2: filled only when `size` covers them ---- */
    uint32_t raw_links;        /* live raw links */
    uint32_t foreign_before;   /* targets that were already hooked/patched by someone else when Kiln hooked them */
    uint32_t foreign_over;     /* targets someone else hooked over Kiln's detour afterwards */
    uint32_t targets;          /* distinct hooked addresses */
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

    /* ======== v2 (KILN_HOOKS_VERSION 2): check header.size covers these before calling ========
     *
     * The registry owns one physical detour per target. Every kind of handler below is a link in that target's chain:
     * raw, structured (detour2 and the v1 detour) and mid. Links run in the order of KilnHookOrder; each one reaches the
     * rest of the chain and then the real function through its own continuation (raw), call_original2 / call_original
     * (structured) or by returning (mid). Adding or revoking a link while other threads call through the chain is safe
     * (cells are retargeted atomically; nothing is frozen after the first install of a target).
     *
     * raw_detour: `fn` has the target's own native signature (any arguments, floats, stack arguments, any return).
     * *original is the continuation with the same signature: it runs the rest of the chain and then the real function.
     * Continuations are never freed (a revoked link's continuation forwards to the next link), so a handler that still
     * holds one after revoke is safe. `order` may be NULL. `original` may be NULL.
     *
     * caller: the return address of the game call that entered h's target on the current thread. Valid for every link of
     * the chain, when read before the handler calls its original or another hooked function. A recursive entry into the
     * same target on the same thread overwrites the record. 0 when unknown.
     *
     * detour2: structured handler for integer/pointer signatures up to 12 arguments (stack arguments included), with the
     * caller. Float arguments or a float return need raw_detour. It runs under an SEH guard like v1 detour.
     *
     * mid2: ordered mid hook (the existing `mid` is a mid link with default order). A mid hook may share its address with
     * detour links. Registers are those at the hooked instruction as seen by the previous link. rip is where execution
     * continues (the next link or the original code). trampoline_rsp starts at rsp - 16 (one slot more than safetyhook's
     * rsp - 8): move it relative to its starting value, never set it from rsp.
     *
     * map_json: the hook map (docs/kiln/services/hooks.md), also written to <kiln>/state/hooks.json. *len receives the
     * text length without the NUL; buf needs *len + 1 bytes (NUL-terminated), else KILN_ERR_BUFFER_TOO_SMALL. buf may be
     * NULL to ask for the length. */
    KilnResult(KILN_CALL* raw_detour)(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, void* fn,
                                      void** original, KilnHandle* out);
    uintptr_t(KILN_CALL* caller)(KilnHandle h);
    KilnResult(KILN_CALL* detour2)(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, uint32_t arg_count,
                                   KilnDetourFn2 fn, void* user, KilnHandle* out);
    uintptr_t(KILN_CALL* call_original2)(KilnHookCall2* call);
    KilnResult(KILN_CALL* mid2)(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, KilnMidFn fn, void* user,
                                KilnHandle* out);
    KilnResult(KILN_CALL* map_json)(char* buf, size_t cap, size_t* len);
} KilnHooksApi;

#ifdef __cplusplus
}
#endif

#endif /* KILN_HOOKS_H */
