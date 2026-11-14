// SPDX-License-Identifier: MIT
// kiln.hooks typed links (dev.28): the `sig` descriptor of a game function and its ABI fingerprint.
//
// A fingerprint says what a function's code does with the Win64 argument positions, read from the bytes of the exe
// file (never the live entry, which Kiln or another engine may have hooked): which of rcx/rdx/r8/r9/xmm0-3 it reads
// before it writes them, which it hands on untouched to a callee, which stack argument slots it reads, and what was
// written last before each return. sigdb checks every described symbol against it at load, kiln.hooks checks every
// detour_typed, and tools/sigport runs the same pass (in Python) on a new game build.
//
// The pass (this text is the contract the Python twin in tools/sigport follows; a shared vector file tests both):
//   1. If the first instruction at the entry is `jmp rel32`, the entry is its target (one thunk, once).
//   2. Paths are walked depth first from a stack of (address, written, rsp, rbp, last). A path starts only when
//      (address, written, rsp) was not seen. At most 300 instructions are decoded over all paths.
//   3. Per instruction, in this order:
//      - register operands (hidden ones included) that are read count as reads, memory operands' base and index too;
//        a read of an argument register not yet written on the path is a fingerprint read;
//      - exceptions: both operands of xor/sub/pxor/xorps/xorpd (and the VEX forms' two sources) naming one register
//        is a write only; the destination register of movss/movsd/cvtsi2ss/cvtsi2sd/cvtss2sd/cvtsd2ss/sqrtss/sqrtsd/
//        roundss/roundsd/rcpss/rsqrtss is a write only; the destination of cmovcc is neither;
//      - general registers known to hold "entry rsp + offset" are followed: rsp from the start (push/pop/pushfq/
//        popfq move it by 8, leave sets it from rbp); `mov r64, known`, `lea r64, [known + d]` and `add/sub known,
//        imm` give the destination a value; any other write of a register (hidden operands included; a call writes
//        rax rcx rdx r8-r11) loses it. A memory operand that is read through a known register, without an index, at
//        entry rsp + 0x28 + 8k (k < 16) is a read of stack slot k. An indexed access below the end of the argument
//        area, the address of an argument slot taken with lea, or rsp losing its value makes the fingerprint
//        "stack blind": unread stack arguments are then unknown, not absent;
//      - unconditional register writes (any width) mark the register written; a write of rax or xmm0 becomes `last`.
//   4. ret: the path ends; `last` (none / rax / xmm0 / callee) joins the return kinds. int3, ud2, hlt and bytes that
//      do not decode end the path.
//   5. call: argument registers not written on the path are "passes"; then all eight and rax are written, last =
//      callee.
//   6. jmp rel: a target inside the function (same function by the exception table when there is one, and inside
//      [entry, entry + 4 KiB)) continues the path there; any other target is a tail call: unwritten registers are
//      passes, the return kind is callee, the path ends. jmp through a register or memory: the same, and the
//      unwritten registers are also recorded as "indirect" (their use could not be followed).
//   7. jcc/jrcxz/loop: the target, when inside the function, is pushed with the current state; the walk goes on
//      behind the branch.
// Finally passes and indirect lose the registers that are reads.
//
// This file has no Windows dependency: the host tests and tools build it as it is.
#pragma once
#include <cstddef>
#include <cstdint>
#include <map>
#include <string>
#include <string_view>
#include <vector>

#include "services/kiln_hooks.h"

namespace kiln::hooks::fp {

// Bits of the register masks: rcx rdx r8 r9 xmm0 xmm1 xmm2 xmm3.
constexpr int kArgRegs = 8;
const char* reg_name(int bit);  // "rcx" .. "xmm3"

constexpr uint32_t kMaxInstructions = 300;
constexpr uint64_t kWindow = 4096;
constexpr int kStackSlots = 16;

enum : uint8_t { kRetNone = 1, kRetRax = 2, kRetXmm0 = 4, kRetCallee = 8 };

// Where the pass gets the function's code from.
struct Reader {
    void* ctx = nullptr;
    // Copies up to n bytes of the original code at `addr`; returns how many (0 = not code Kiln can read).
    size_t (*bytes)(void* ctx, uint64_t addr, uint8_t* out, size_t n) = nullptr;
    // Optional: the exception table. True when `addr` is inside a function with unwind data; *primary is then the
    // start of that function (chained parts resolved to their first part). *next is the start of the first function
    // with unwind data behind `addr` (UINT64_MAX when none).
    bool (*function)(void* ctx, uint64_t addr, uint64_t* primary, uint64_t* next) = nullptr;
};

struct Fingerprint {
    bool ok = false;         // false: nothing could be read or decoded at the entry (`why` says)
    std::string why;
    uint64_t entry = 0;      // after the leading thunk
    bool thunk = false;
    uint8_t reads = 0;       // argument registers read before written on some path
    uint8_t passes = 0;      // handed to a callee or tail call untouched (and not read anywhere)
    uint8_t indirect = 0;    // untouched at a jump through a register or memory (and not read anywhere)
    uint32_t stack = 0;      // bit k: stack slot k (entry rsp + 0x28 + 8k) is read
    bool tail = false;       // a tail call was seen (it takes the caller's stack arguments with it)
    bool stack_blind = false;  // stack arguments may be read in ways the pass does not follow
    uint8_t rets = 0;        // kRet* seen at the returns reached
    uint32_t insns = 0;
    bool truncated = false;  // the instruction limit ended the walk with paths left
};

Fingerprint compute(const Reader& reader, uint64_t entry);

// {"result":"ok"|"unknown","reads":["rcx"],"passes":[],"indirect":[],"stack":["0x28"],"ret":"rax","insns":12}
// "unknown" when !ok or `indirect` is not empty. Registers are listed in position order (rcx xmm0 rdx xmm1 r8 xmm2
// r9 xmm3), stack slots as offsets from the entry rsp.
std::string to_json(const Fingerprint& f);
// "[rcx, xmm1, r8, stack+0x28]"
std::string reads_text(const Fingerprint& f);
std::string ret_text(uint8_t rets);  // "none" | "rax" | "xmm0" | "callee" | joined with '|' in that order

// ---- descriptors ----
struct SigArg {
    std::string name, type;
    uint32_t kind = 0, size = 0;  // KILN_SIG_*
};

struct StructField {
    std::string name, type;
    uint32_t offset = 0;
};
struct StructDef {
    uint32_t size = 0, align = 0;
    std::vector<StructField> fields;
};
using Structs = std::map<std::string, StructDef, std::less<>>;

struct Sig {
    std::vector<SigArg> args;      // the declared parameters only (never the hidden return buffer)
    std::string ret_type;          // "void", a scalar type, or the struct's name
    uint32_t ret_kind = KILN_SIG_VOID, ret_size = 0;
    bool sret = false;
    std::string invoke = "none";   // "none" | "tick" | "any"
    bool has_abi = false, trust = false;
    uint8_t abi_reads = 0, abi_passes = 0;
    uint32_t abi_stack = 0;
    std::string abi_ret;
    uint32_t positions() const { return static_cast<uint32_t>(args.size()) + (sret ? 1 : 0); }
};

// The "structs" section of a definitions file: {"OptS32": {"size": 8, "align": 4, "fields": [{"name": "value",
// "type": "s32", "offset": 0}, ...]}}. Appends to `out`; false with `error` on a malformed or duplicate entry.
bool parse_structs(std::string_view json_text, Structs* out, std::string* error);
// One `sig` block. `structs` resolves by-value and sret types. `allow_trust`: "abi": "trust" is legal (a mod's own
// fragment). False with `error` naming what is wrong.
bool parse_sig(std::string_view json_text, const Structs& structs, bool allow_trust, Sig* out, std::string* error);

// What a descriptor expects of the positions: how many, which of the first four are floats, and a name per position
// for messages.
struct Shape {
    uint32_t positions = 0;
    uint8_t floats = 0;                  // bit k: position k (< 4) travels in xmm k
    std::vector<std::string> names;      // per position ("" = unnamed); the sret buffer is "(return buffer)"
    bool sret = false;
};
Shape shape_of(const Sig& sig);
Shape shape_of(const KilnSig& sig);  // the caller validated it

enum class Verdict { Match, Mismatch, Unknown };
const char* verdict_name(Verdict v);  // "match" | "mismatch" | "unknown"

// Chapter 06b §4.3: a position the descriptor declares that the function neither reads nor passes, or a read the
// descriptor lacks, is a mismatch; a declared position whose use could not be followed (an indirect jump) is
// unknown. `text` always describes the comparison ("descriptor arg 2 'ecsNow' is rdx, the function reads [rcx, xmm1,
// r8]", or "3 arguments (rcx, xmm1, r8), the function reads [rcx, xmm1, r8]").
Verdict check(const Shape& shape, const Fingerprint& f, std::string* text);
// A sigdb descriptor: the stored fingerprint ("abi": reads and stack must equal the code's, its passes must be among
// the code's), then check(). "abi": "trust" turns Unknown into Match.
Verdict check_sig(const Sig& sig, const Fingerprint& f, std::string* text);

// ---- the exception table of a mapped PE image (for Reader::function) ----
struct Pdata {
    const uint8_t* image = nullptr;  // the image as mapped (sections at their RVAs)
    size_t image_size = 0;
    const uint8_t* table = nullptr;  // RUNTIME_FUNCTION[count], sorted by begin
    size_t count = 0;
};
bool pdata_init(Pdata* out, const uint8_t* image, size_t image_size);
// By RVA. True when `rva` is inside an entry: *primary = the begin RVA of its first part. *next = the begin RVA of
// the first entry behind `rva` (UINT64_MAX when none).
bool pdata_lookup(const Pdata& p, uint64_t rva, uint64_t* primary, uint64_t* next);

}  // namespace kiln::hooks::fp
