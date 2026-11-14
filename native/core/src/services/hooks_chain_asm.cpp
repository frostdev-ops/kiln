// SPDX-License-Identifier: MIT
// Static machine code of the kiln.hooks v2 registry (see hooks_chain.h). The per-target and per-link stubs are
// generated at run time (hooks_chain.cpp); the three routines below are the shared parts they jump to. They are
// assembled into the DLL so the loader registers their unwind info (.pdata/.xdata) like any compiled function.
//
//  kiln_hk_slow        first call of a thread through any entry stub: allocate its caller table. Called (not jumped
//                      to) from the entry stub; preserves every register except rcx, which returns the table. The
//                      stack may have any alignment (the stub also serves mid hooks in the middle of a function).
//  kiln_hk_structured  entry of a structured link (v1 detour, detour2): copies rcx/rdx/r8/r9 into its own frame (the
//                      caller's shadow space is never written), then calls kiln_hk_structured_dispatch(regs, stack
//                      args, link). r11 = the Link, set by the link's adapter stub.
//  kiln_hk_mid         entry of a mid link. The link's stub pushed [continuation] then [Link*]; this builds a
//                      safetyhook-compatible Context64 below them in which the continuation slot is ctx.rip and the
//                      Link* slot is ctx.trampoline_rsp, so the layout and the rules are safetyhook's own:
//                      trampoline_rsp = the hooked code's rsp - 8 (it points at ctx.rip) and the stub resumes with
//                      `pop rsp; ret`. The Link* is moved into rdx (kiln_hk_mid_dispatch(ctx, link)) before the slot
//                      gets its trampoline_rsp value. A handler at a function entry that returns early by setting
//                      trampoline_rsp = rsp (and rax) returns to the game's caller with the stack it had.
//                      rbp is its frame pointer (the call is realigned at run time); the unwind info treats the two
//                      pushed slots as an allocation, so a walk from a handler continues at the hooked code's [rsp]
//                      (the game's caller for a mid hook at a function entry).
//
//  kiln_hk_typed       entry of a typed link (dev.28). r11 = the Link, set by the link's adapter stub. Builds a
//                      KilnCallFrame on its own stack (rcx/rdx/r8/r9, xmm0-3, a pointer to the caller's stack
//                      arguments; the return fields zeroed; the caller's home space is never written), calls
//                      kiln_hk_typed_dispatch(frame, link), then returns frame.rax in rax and frame.xmm0r in xmm0.
//                      Entry rsp = 8 mod 16 (a function entry).
//  kiln_hk_call_frame  (fn, frame, nstack): copies nstack stack arguments from frame.stack above a fresh home space,
//                      loads the eight argument registers from the frame, calls fn, stores rax and xmm0 back. The
//                      frame may be unaligned (a caller's own). rbp is the frame pointer of both, so an exception
//                      thrown by the called function unwinds through them.
//
// Syntax: the clang-cl assembler defaults to Intel syntax; the directives are the COFF/SEH ones.
#include <cstddef>

#include "services/kiln_hooks.h"

// kiln_hk_mid hard-codes these offsets (the push order below builds the struct from rip down to xmm0).
static_assert(offsetof(KilnRegisters, xmm15) == 0xF0, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, rflags) == 0x100, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, r15) == 0x108, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, rax) == 0x170, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, rbp) == 0x178, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, rsp) == 0x180, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, trampoline_rsp) == 0x188, "KilnRegisters layout (kiln_hk_mid)");
static_assert(offsetof(KilnRegisters, rip) == 0x190, "KilnRegisters layout (kiln_hk_mid)");
static_assert(sizeof(KilnRegisters) == 0x198, "KilnRegisters layout (kiln_hk_mid)");
// kiln_hk_typed and kiln_hk_call_frame hard-code these.
static_assert(offsetof(KilnCallFrame, gpr) == 0x00, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(offsetof(KilnCallFrame, xmm) == 0x20, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(offsetof(KilnCallFrame, stack) == 0x60, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(offsetof(KilnCallFrame, rax) == 0x68, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(offsetof(KilnCallFrame, xmm0r) == 0x70, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(offsetof(KilnCallFrame, slot) == 0x80, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(offsetof(KilnCallFrame, reserved) == 0x88, "KilnCallFrame layout (kiln_hk_typed)");
static_assert(sizeof(KilnCallFrame) == 0x90, "KilnCallFrame layout (kiln_hk_typed)");
__asm__(
    ".intel_syntax noprefix\n"
    ".text\n"

    // ---------------------------------------------------------------------------------------------------------------
    ".globl kiln_hk_slow\n"
    ".def kiln_hk_slow; .scl 2; .type 32; .endef\n"
    ".seh_proc kiln_hk_slow\n"
    "kiln_hk_slow:\n"
    "  push rbp\n"
    "  .seh_pushreg rbp\n"
    "  mov rbp, rsp\n"
    "  .seh_setframe rbp, 0\n"
    "  sub rsp, 0xE0\n"
    "  .seh_stackalloc 0xE0\n"
    "  and rsp, -16\n"
    "  .seh_endprologue\n"
    "  mov [rsp+0x20], rax\n"
    "  mov [rsp+0x28], rdx\n"
    "  mov [rsp+0x30], r8\n"
    "  mov [rsp+0x38], r9\n"
    "  mov [rsp+0x40], r10\n"
    "  mov [rsp+0x48], r11\n"
    "  movdqa xmmword ptr [rsp+0x50], xmm0\n"
    "  movdqa xmmword ptr [rsp+0x60], xmm1\n"
    "  movdqa xmmword ptr [rsp+0x70], xmm2\n"
    "  movdqa xmmword ptr [rsp+0x80], xmm3\n"
    "  movdqa xmmword ptr [rsp+0x90], xmm4\n"
    "  movdqa xmmword ptr [rsp+0xA0], xmm5\n"
    "  call kiln_hk_alloc_block\n"
    "  mov rcx, rax\n"
    "  mov rax, [rsp+0x20]\n"
    "  mov rdx, [rsp+0x28]\n"
    "  mov r8, [rsp+0x30]\n"
    "  mov r9, [rsp+0x38]\n"
    "  mov r10, [rsp+0x40]\n"
    "  mov r11, [rsp+0x48]\n"
    "  movdqa xmm0, xmmword ptr [rsp+0x50]\n"
    "  movdqa xmm1, xmmword ptr [rsp+0x60]\n"
    "  movdqa xmm2, xmmword ptr [rsp+0x70]\n"
    "  movdqa xmm3, xmmword ptr [rsp+0x80]\n"
    "  movdqa xmm4, xmmword ptr [rsp+0x90]\n"
    "  movdqa xmm5, xmmword ptr [rsp+0xA0]\n"
    "  lea rsp, [rbp]\n"
    "  pop rbp\n"
    "  ret\n"
    ".seh_endproc\n"

    // ---------------------------------------------------------------------------------------------------------------
    ".globl kiln_hk_structured\n"
    ".def kiln_hk_structured; .scl 2; .type 32; .endef\n"
    ".seh_proc kiln_hk_structured\n"
    "kiln_hk_structured:\n"
    "  sub rsp, 0x68\n"           // 0x20 shadow + 0x20 register copy + 0x28 spare; entry rsp = 8 mod 16 -> aligned
    "  .seh_stackalloc 0x68\n"
    "  .seh_endprologue\n"
    "  mov [rsp+0x20], rcx\n"
    "  mov [rsp+0x28], rdx\n"
    "  mov [rsp+0x30], r8\n"
    "  mov [rsp+0x38], r9\n"
    "  lea rcx, [rsp+0x20]\n"     // regs[4]
    "  lea rdx, [rsp+0x90]\n"     // the caller's stack arguments: entry rsp + 0x28
    "  mov r8, r11\n"             // Link*
    "  call kiln_hk_structured_dispatch\n"
    "  add rsp, 0x68\n"
    "  ret\n"
    ".seh_endproc\n"

    // ---------------------------------------------------------------------------------------------------------------
    ".globl kiln_hk_mid\n"
    ".def kiln_hk_mid; .scl 2; .type 32; .endef\n"
    ".seh_proc kiln_hk_mid\n"
    "kiln_hk_mid:\n"
    "  nop\n"
    "  .seh_stackalloc 16\n"      // the continuation (ctx.rip) and Link* (ctx.trampoline_rsp) slots the stub pushed
    "  push rsp\n"                // ctx.rsp (read-only; set below)
    "  .seh_stackalloc 8\n"
    "  push rbp\n"
    "  .seh_pushreg rbp\n"
    "  push rax\n"
    "  .seh_pushreg rax\n"
    "  push rbx\n"
    "  .seh_pushreg rbx\n"
    "  push rcx\n"
    "  .seh_pushreg rcx\n"
    "  push rdx\n"
    "  .seh_pushreg rdx\n"
    "  push rsi\n"
    "  .seh_pushreg rsi\n"
    "  push rdi\n"
    "  .seh_pushreg rdi\n"
    "  push r8\n"
    "  .seh_pushreg r8\n"
    "  push r9\n"
    "  .seh_pushreg r9\n"
    "  push r10\n"
    "  .seh_pushreg r10\n"
    "  push r11\n"
    "  .seh_pushreg r11\n"
    "  push r12\n"
    "  .seh_pushreg r12\n"
    "  push r13\n"
    "  .seh_pushreg r13\n"
    "  push r14\n"
    "  .seh_pushreg r14\n"
    "  push r15\n"
    "  .seh_pushreg r15\n"
    "  pushfq\n"
    "  .seh_stackalloc 8\n"
    "  sub rsp, 0x100\n"
    "  .seh_stackalloc 0x100\n"
    "  movdqu xmmword ptr [rsp+0x00], xmm0\n"
    "  movdqu xmmword ptr [rsp+0x10], xmm1\n"
    "  movdqu xmmword ptr [rsp+0x20], xmm2\n"
    "  movdqu xmmword ptr [rsp+0x30], xmm3\n"
    "  movdqu xmmword ptr [rsp+0x40], xmm4\n"
    "  movdqu xmmword ptr [rsp+0x50], xmm5\n"
    "  movdqu xmmword ptr [rsp+0x60], xmm6\n"
    "  .seh_savexmm xmm6, 0x60\n"
    "  movdqu xmmword ptr [rsp+0x70], xmm7\n"
    "  .seh_savexmm xmm7, 0x70\n"
    "  movdqu xmmword ptr [rsp+0x80], xmm8\n"
    "  .seh_savexmm xmm8, 0x80\n"
    "  movdqu xmmword ptr [rsp+0x90], xmm9\n"
    "  .seh_savexmm xmm9, 0x90\n"
    "  movdqu xmmword ptr [rsp+0xA0], xmm10\n"
    "  .seh_savexmm xmm10, 0xA0\n"
    "  movdqu xmmword ptr [rsp+0xB0], xmm11\n"
    "  .seh_savexmm xmm11, 0xB0\n"
    "  movdqu xmmword ptr [rsp+0xC0], xmm12\n"
    "  .seh_savexmm xmm12, 0xC0\n"
    "  movdqu xmmword ptr [rsp+0xD0], xmm13\n"
    "  .seh_savexmm xmm13, 0xD0\n"
    "  movdqu xmmword ptr [rsp+0xE0], xmm14\n"
    "  .seh_savexmm xmm14, 0xE0\n"
    "  movdqu xmmword ptr [rsp+0xF0], xmm15\n"
    "  .seh_savexmm xmm15, 0xF0\n"
    "  mov rbp, rsp\n"             // frame pointer: the call below realigns rsp, so the unwinder must start from rbp
    "  .seh_setframe rbp, 0\n"     // (ctx.rbp is already saved in its slot)
    "  .seh_endprologue\n"
    "  mov rdx, [rsp+0x188]\n"    // Link* (2nd argument); its slot becomes ctx.trampoline_rsp
    "  lea rax, [rsp+0x190]\n"    // trampoline_rsp = &ctx.rip = the hooked code's rsp - 8, as safetyhook
    "  mov [rsp+0x188], rax\n"
    "  lea rax, [rsp+0x198]\n"    // ctx.rsp = the hooked code's rsp
    "  mov [rsp+0x180], rax\n"
    "  mov rcx, rsp\n"            // &ctx
    "  sub rsp, 0x30\n"
    "  and rsp, -16\n"
    "  call kiln_hk_mid_dispatch\n"
    "  mov rsp, rbp\n"
    "  movdqu xmm0, xmmword ptr [rsp+0x00]\n"
    "  movdqu xmm1, xmmword ptr [rsp+0x10]\n"
    "  movdqu xmm2, xmmword ptr [rsp+0x20]\n"
    "  movdqu xmm3, xmmword ptr [rsp+0x30]\n"
    "  movdqu xmm4, xmmword ptr [rsp+0x40]\n"
    "  movdqu xmm5, xmmword ptr [rsp+0x50]\n"
    "  movdqu xmm6, xmmword ptr [rsp+0x60]\n"
    "  movdqu xmm7, xmmword ptr [rsp+0x70]\n"
    "  movdqu xmm8, xmmword ptr [rsp+0x80]\n"
    "  movdqu xmm9, xmmword ptr [rsp+0x90]\n"
    "  movdqu xmm10, xmmword ptr [rsp+0xA0]\n"
    "  movdqu xmm11, xmmword ptr [rsp+0xB0]\n"
    "  movdqu xmm12, xmmword ptr [rsp+0xC0]\n"
    "  movdqu xmm13, xmmword ptr [rsp+0xD0]\n"
    "  movdqu xmm14, xmmword ptr [rsp+0xE0]\n"
    "  movdqu xmm15, xmmword ptr [rsp+0xF0]\n"
    "  add rsp, 0x100\n"
    "  popfq\n"
    "  pop r15\n"
    "  pop r14\n"
    "  pop r13\n"
    "  pop r12\n"
    "  pop r11\n"
    "  pop r10\n"
    "  pop r9\n"
    "  pop r8\n"
    "  pop rdi\n"
    "  pop rsi\n"
    "  pop rdx\n"
    "  pop rcx\n"
    "  pop rbx\n"
    "  pop rax\n"
    "  pop rbp\n"
    "  lea rsp, [rsp+8]\n"        // skip the read-only rsp slot
    "  pop rsp\n"                 // trampoline_rsp: &ctx.rip unless the handler moved it
    "  ret\n"                     // continue at [trampoline_rsp] (ctx.rip)
    ".seh_endproc\n"

    // ---------------------------------------------------------------------------------------------------------------
    ".globl kiln_hk_typed\n"
    ".def kiln_hk_typed; .scl 2; .type 32; .endef\n"
    ".seh_proc kiln_hk_typed\n"
    "kiln_hk_typed:\n"
    "  push rbp\n"
    "  .seh_pushreg rbp\n"
    "  mov rbp, rsp\n"
    "  .seh_setframe rbp, 0\n"
    "  sub rsp, 0xB0\n"            // 0x20 home + 0x90 frame; entry rsp = 8 mod 16 -> rsp and the frame are 16-aligned
    "  .seh_stackalloc 0xB0\n"
    "  .seh_endprologue\n"
    "  mov [rsp+0x20], rcx\n"     // frame.gpr
    "  mov [rsp+0x28], rdx\n"
    "  mov [rsp+0x30], r8\n"
    "  mov [rsp+0x38], r9\n"
    "  movdqa xmmword ptr [rsp+0x40], xmm0\n"  // frame.xmm
    "  movdqa xmmword ptr [rsp+0x50], xmm1\n"
    "  movdqa xmmword ptr [rsp+0x60], xmm2\n"
    "  movdqa xmmword ptr [rsp+0x70], xmm3\n"
    "  lea rax, [rbp+0x30]\n"     // frame.stack = entry rsp + 0x28 = (rbp + 8) + 0x28
    "  mov [rsp+0x80], rax\n"
    "  xor eax, eax\n"
    "  mov [rsp+0x88], rax\n"     // frame.rax = 0
    "  mov [rsp+0x90], rax\n"     // frame.xmm0r = 0
    "  mov [rsp+0x98], rax\n"
    "  mov [rsp+0xA0], rax\n"     // frame.slot, frame.reserved: the dispatcher sets them
    "  mov [rsp+0xA8], rax\n"
    "  lea rcx, [rsp+0x20]\n"     // frame
    "  mov rdx, r11\n"            // Link*
    "  call kiln_hk_typed_dispatch\n"
    "  mov rax, [rsp+0x88]\n"
    "  movdqa xmm0, xmmword ptr [rsp+0x90]\n"
    "  lea rsp, [rbp]\n"
    "  pop rbp\n"
    "  ret\n"
    ".seh_endproc\n"

    // ---------------------------------------------------------------------------------------------------------------
    ".globl kiln_hk_call_frame\n"
    ".def kiln_hk_call_frame; .scl 2; .type 32; .endef\n"
    ".seh_proc kiln_hk_call_frame\n"
    "kiln_hk_call_frame:\n"
    "  push rbp\n"
    "  .seh_pushreg rbp\n"
    "  push rbx\n"
    "  .seh_pushreg rbx\n"
    "  push rsi\n"
    "  .seh_pushreg rsi\n"
    "  mov rbp, rsp\n"
    "  .seh_setframe rbp, 0\n"
    "  .seh_endprologue\n"
    "  mov r11, rcx\n"            // fn
    "  mov rbx, rdx\n"            // frame
    "  lea rax, [r8*8+0x2F]\n"    // round16(0x20 home + 8 * nstack)
    "  and rax, -16\n"
    "  sub rsp, rax\n"            // three pushes from 8 mod 16 -> rsp = 0 mod 16 here and at the call
    "  xor ecx, ecx\n"
    "  mov rsi, [rbx+0x60]\n"     // frame.stack (not read when nstack = 0)
    ".Lkiln_cf_loop:\n"
    "  cmp rcx, r8\n"
    "  jae .Lkiln_cf_done\n"
    "  mov rax, [rsi+rcx*8]\n"
    "  mov [rsp+rcx*8+0x20], rax\n"
    "  inc rcx\n"
    "  jmp .Lkiln_cf_loop\n"
    ".Lkiln_cf_done:\n"
    "  mov rcx, [rbx+0x00]\n"
    "  mov rdx, [rbx+0x08]\n"
    "  mov r8, [rbx+0x10]\n"
    "  mov r9, [rbx+0x18]\n"
    "  movdqu xmm0, xmmword ptr [rbx+0x20]\n"
    "  movdqu xmm1, xmmword ptr [rbx+0x30]\n"
    "  movdqu xmm2, xmmword ptr [rbx+0x40]\n"
    "  movdqu xmm3, xmmword ptr [rbx+0x50]\n"
    "  call r11\n"
    "  mov [rbx+0x68], rax\n"     // frame.rax
    "  movdqu xmmword ptr [rbx+0x70], xmm0\n"  // frame.xmm0r
    "  lea rsp, [rbp]\n"
    "  pop rsi\n"
    "  pop rbx\n"
    "  pop rbp\n"
    "  ret\n"
    ".seh_endproc\n"
);
